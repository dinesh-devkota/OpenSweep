package io.opensweep.android;

import android.annotation.SuppressLint;
import android.content.ContentResolver;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.os.CancellationSignal;
import android.os.OperationCanceledException;
import android.provider.DocumentsContract;
import android.provider.DocumentsContract.Document;

import java.io.FileNotFoundException;
import java.io.IOException;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/** Scans the chosen document tree. Only a candidate from that tree can be deleted. */
public final class FolderCleaner {
    private static final String[] COLUMNS = {
            Document.COLUMN_DOCUMENT_ID, Document.COLUMN_DISPLAY_NAME, Document.COLUMN_MIME_TYPE,
            Document.COLUMN_FLAGS, Document.COLUMN_SIZE, Document.COLUMN_LAST_MODIFIED
    };

    private FolderCleaner() {}

    public static final class Candidate {
        public final String path;
        public final String name;
        public final long size;
        private final Uri tree;
        private final Metadata original;
        private final Location location;

        private Candidate(Uri tree, Location location, Metadata original) {
            this.tree = tree;
            this.location = location;
            this.path = location.relativePath();
            this.original = original;
            name = original.name;
            size = original.size;
        }
    }

    public static final class ScanResult {
        public final List<Candidate> files;
        public final int skippedFolders;

        private ScanResult(List<Candidate> files, int skippedFolders) {
            this.files = Collections.unmodifiableList(files);
            this.skippedFolders = skippedFolders;
        }
    }

    static final class Metadata {
        final String id;
        final String name;
        final String mime;
        final int flags;
        final long size;
        final long modified;

        Metadata(String id, String name, String mime, int flags, long size, long modified) {
            this.id = id;
            this.name = name;
            this.mime = mime;
            this.flags = flags;
            this.size = size;
            this.modified = modified;
        }

        static Metadata read(Cursor cursor) {
            return new Metadata(cursor.getString(cursor.getColumnIndexOrThrow(Document.COLUMN_DOCUMENT_ID)),
                    cursor.getString(cursor.getColumnIndexOrThrow(Document.COLUMN_DISPLAY_NAME)),
                    cursor.getString(cursor.getColumnIndexOrThrow(Document.COLUMN_MIME_TYPE)),
                    cursor.getInt(cursor.getColumnIndexOrThrow(Document.COLUMN_FLAGS)),
                    numberOrUnknown(cursor, Document.COLUMN_SIZE),
                    numberOrUnknown(cursor, Document.COLUMN_LAST_MODIFIED));
        }

        private static long numberOrUnknown(Cursor cursor, String column) {
            int index = cursor.getColumnIndex(column);
            return index < 0 || cursor.isNull(index) ? -1 : cursor.getLong(index);
        }

        @SuppressLint("InlinedApi") // Older providers simply do not set FLAG_PARTIAL.
        boolean eligible() {
            if (id == null || id.isEmpty() || name == null || mime == null || mime.isEmpty()
                    || Document.MIME_TYPE_DIR.equals(mime)
                    || (flags & Document.FLAG_SUPPORTS_DELETE) == 0
                    || (flags & (Document.FLAG_VIRTUAL_DOCUMENT | Document.FLAG_PARTIAL)) != 0) {
                return false;
            }
            return CleanupRules.isJunkName(name);
        }

        boolean stillMatches(Metadata current) {
            return current.eligible() && id.equals(current.id) && name.equals(current.name)
                    && mime.equals(current.mime) && size == current.size && modified == current.modified;
        }
    }

    static final class Location {
        final Location parent;
        final String id;
        final String name;

        Location(Location parent, String id, String name) {
            this.parent = parent;
            this.id = id;
            this.name = name;
        }

        String relativePath() {
            ArrayDeque<String> names = new ArrayDeque<>();
            for (Location part = this; part.parent != null; part = part.parent) {
                names.addFirst(part.name);
            }
            return String.join("/", names);
        }

        boolean matchesIds(List<String> ids) {
            int index = ids.size();
            for (Location part = this; part != null; part = part.parent) {
                if (index == 0 || !part.id.equals(ids.get(--index))) return false;
            }
            return index == 0;
        }
    }

    private static boolean sameLocation(ContentResolver resolver, Candidate file, Uri uri)
            throws FileNotFoundException {
        DocumentsContract.Path path;
        try {
            path = DocumentsContract.findDocumentPath(resolver, uri);
        } catch (UnsupportedOperationException unsupported) {
            path = null;
        }
        if (path != null && !file.location.matchesIds(path.getPath())) return false;

        for (Location part = file.location; part.parent != null; part = part.parent) {
            // The direct metadata query below verifies the file's name. Also verify folder names.
            if (path != null && part == file.location) continue;
            // Providers without canonical paths can still prove each parent-child relationship.
            Uri source = path == null
                    ? DocumentsContract.buildChildDocumentsUriUsingTree(file.tree, part.parent.id)
                    : DocumentsContract.buildDocumentUriUsingTree(file.tree, part.id);
            if (!containsNamedDocument(resolver, source, part)) return false;
        }
        return true;
    }

    private static boolean containsNamedDocument(ContentResolver resolver, Uri source, Location expected) {
        String[] columns = {Document.COLUMN_DOCUMENT_ID, Document.COLUMN_DISPLAY_NAME};
        try (Cursor cursor = resolver.query(source, columns, null, null, null)) {
            if (cursor == null) return false;
            int id = cursor.getColumnIndexOrThrow(Document.COLUMN_DOCUMENT_ID);
            int name = cursor.getColumnIndexOrThrow(Document.COLUMN_DISPLAY_NAME);
            while (cursor.moveToNext()) {
                if (expected.id.equals(cursor.getString(id)) && expected.name.equals(cursor.getString(name))) {
                    return true;
                }
            }
        }
        return false;
    }

    public static ScanResult scan(ContentResolver resolver, Uri tree, CancellationSignal signal)
            throws IOException {
        ArrayDeque<Location> pending = new ArrayDeque<>();
        Set<String> foldersSeen = new HashSet<>();
        Set<String> candidatesSeen = new HashSet<>();
        Location root = new Location(null, DocumentsContract.getTreeDocumentId(tree), "");
        pending.add(root);
        foldersSeen.add(root.id);
        List<Candidate> files = new ArrayList<>();
        int skipped = 0;

        while (!pending.isEmpty()) {
            if (signal != null) signal.throwIfCanceled();
            Location folder = pending.removeFirst();
            Uri children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, folder.id);
            try (Cursor cursor = resolver.query(children, COLUMNS, null, null, null, signal)) {
                if (cursor == null) throw new IOException("Folder listing unavailable.");
                while (cursor.moveToNext()) {
                    if (signal != null) signal.throwIfCanceled();
                    Metadata entry = Metadata.read(cursor);
                    if (entry.id == null || entry.id.isEmpty() || entry.name == null || entry.name.isEmpty()) {
                        continue;
                    }
                    if (Document.MIME_TYPE_DIR.equals(entry.mime)) {
                        if (foldersSeen.add(entry.id)) {
                            pending.addLast(new Location(folder, entry.id, entry.name));
                        }
                    } else if (entry.eligible() && candidatesSeen.add(entry.id)) {
                        files.add(new Candidate(tree, new Location(folder, entry.id, entry.name), entry));
                    }
                }
                Bundle extras = cursor.getExtras();
                if (extras != null && (extras.getBoolean(DocumentsContract.EXTRA_LOADING)
                        || extras.containsKey(DocumentsContract.EXTRA_ERROR))) {
                    skipped++;
                }
            } catch (OperationCanceledException canceled) {
                throw canceled;
            } catch (IOException | RuntimeException error) {
                if (folder == root) throw new IOException("Unable to read the chosen folder.", error);
                skipped++;
            }
        }

        files.sort(Comparator.comparing(file -> file.path));
        if (signal != null) signal.throwIfCanceled();
        return new ScanResult(files, skipped);
    }

    public static boolean delete(ContentResolver resolver, Uri tree, Candidate file) {
        if (!file.tree.equals(tree)) return false;
        Uri uri = DocumentsContract.buildDocumentUriUsingTree(tree, file.original.id);
        try {
            if (!sameLocation(resolver, file, uri)) return false;
            try (Cursor cursor = resolver.query(uri, COLUMNS, null, null, null)) {
                if (cursor == null || !cursor.moveToFirst() || !file.original.stillMatches(Metadata.read(cursor))) {
                    return false;
                }
            }
            return DocumentsContract.deleteDocument(resolver, uri);
        } catch (FileNotFoundException | RuntimeException unavailable) {
            return false;
        }
    }
}
