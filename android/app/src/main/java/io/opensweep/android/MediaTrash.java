package io.opensweep.android;

import android.app.PendingIntent;
import android.content.ContentResolver;
import android.content.ContentUris;
import android.content.Context;
import android.content.SharedPreferences;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.provider.MediaStore;

import androidx.annotation.ChecksSdkIntAtLeast;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.NoSuchFileException;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Android-managed media trash. All methods except supported() run on a worker thread.
 * A request is journaled before it is returned, and results are verified from MediaStore,
 * never inferred from an Activity result code. No operation falls back to file deletion.
 */
final class MediaTrash {
    static final int MAX_BATCH = 100;
    private static final int MAX_HISTORY = 2000;
    private static final String STATE = "state";
    private static final Object JOURNAL_LOCK = new Object();
    private static final String[] FILE_PROJECTION = {
            MediaStore.MediaColumns._ID, MediaStore.MediaColumns.DATA,
            MediaStore.MediaColumns.DISPLAY_NAME, MediaStore.MediaColumns.SIZE,
            MediaStore.MediaColumns.DATE_ADDED, MediaStore.MediaColumns.IS_TRASHED,
            MediaStore.MediaColumns.DATE_EXPIRES, MediaStore.MediaColumns.VOLUME_NAME,
            MediaStore.Files.FileColumns.MEDIA_TYPE, MediaStore.MediaColumns.IS_PENDING
    };
    // Typed image/video/audio collections do not expose Files.MEDIA_TYPE.
    private static final String[] ITEM_PROJECTION = {
            MediaStore.MediaColumns._ID, MediaStore.MediaColumns.DATA,
            MediaStore.MediaColumns.DISPLAY_NAME, MediaStore.MediaColumns.SIZE,
            MediaStore.MediaColumns.DATE_ADDED, MediaStore.MediaColumns.IS_TRASHED,
            MediaStore.MediaColumns.DATE_EXPIRES, MediaStore.MediaColumns.VOLUME_NAME,
            MediaStore.MediaColumns.IS_PENDING
    };

    enum Operation { TRASH, RESTORE, DELETE }

    static final class Entry {
        final Uri uri;
        final String name;
        final String originalPath;
        final long size;
        final long trashedAtMillis;
        final long expiresAtMillis;
        private final String volume;
        private final String version;
        private final String fileKey;
        private final long modifiedMillis;
        private final long addedSeconds;
        private final String lastKnownPath;

        private Entry(Uri uri, String name, String originalPath, long size, long trashedAtMillis,
                      long expiresAtMillis, String volume, String version, String fileKey,
                      long modifiedMillis, long addedSeconds, String lastKnownPath) {
            this.uri = uri;
            this.name = name;
            this.originalPath = originalPath;
            this.size = size;
            this.trashedAtMillis = trashedAtMillis;
            this.expiresAtMillis = expiresAtMillis;
            this.volume = volume;
            this.version = version;
            this.fileKey = fileKey;
            this.modifiedMillis = modifiedMillis;
            this.addedSeconds = addedSeconds;
            this.lastKnownPath = lastKnownPath;
        }

        private Entry withObservation(Observed observed) {
            return new Entry(uri, name, originalPath, size, trashedAtMillis, observed.expiry,
                    volume, version, fileKey, modifiedMillis, addedSeconds,
                    observed.path == null ? lastKnownPath : observed.path);
        }
    }

    static final class Prepared {
        final PendingIntent request;
        final Operation operation;
        final int requested;
        final int ready;
        final int skipped;
        final long bytes;

        Prepared(PendingIntent request, Operation operation, int requested, List<Entry> entries) {
            this.request = request;
            this.operation = operation;
            this.requested = requested;
            ready = entries.size();
            skipped = requested - ready;
            long total = 0;
            for (Entry entry : entries) total += entry.size;
            bytes = total;
        }
    }

    static final class Outcome {
        final Operation operation;
        final int requested;
        final int succeeded;
        final int failed;
        final long bytes;

        Outcome(Operation operation, int requested, int succeeded, long bytes) {
            this.operation = operation;
            this.requested = requested;
            this.succeeded = succeeded;
            failed = requested - succeeded;
            this.bytes = bytes;
        }
    }

    private enum State { TRASHED, PRESENT, MISSING, CHANGED, UNAVAILABLE }

    private static final class Observed {
        final State state;
        final long expiry;
        final String path;
        Observed(State state, long expiry) { this(state, expiry, null); }
        Observed(State state, long expiry, String path) { this.state = state; this.expiry = expiry; this.path = path; }
    }

    private static final class Journal {
        final Map<String, Entry> history = new LinkedHashMap<>();
        Operation pending;
        int requested;
        final List<Entry> pendingEntries = new ArrayList<>();
    }

    private final Context context;
    private final ContentResolver resolver;
    private final SharedPreferences preferences;

    MediaTrash(Context context) {
        this.context = context.getApplicationContext();
        resolver = this.context.getContentResolver();
        preferences = this.context.getSharedPreferences("media_trash", Context.MODE_PRIVATE);
    }

    @ChecksSdkIntAtLeast(api = Build.VERSION_CODES.R)
    static boolean supported() { return Build.VERSION.SDK_INT >= Build.VERSION_CODES.R; }

    boolean hasPending() {
        synchronized (JOURNAL_LOCK) {
            try { return read().pending != null; }
            catch (IOException unavailable) { throw new IllegalStateException(unavailable.getMessage(), unavailable); }
        }
    }

    Prepared prepareTrash(List<SharedStorageCleaner.Candidate> candidates) {
        synchronized (JOURNAL_LOCK) {
            try { return prepareTrashInternal(candidates); }
            catch (IOException unavailable) { throw new IllegalStateException(unavailable.getMessage(), unavailable); }
        }
    }

    private Prepared prepareTrashInternal(List<SharedStorageCleaner.Candidate> candidates) throws IOException {
        Journal journal = readyJournal();
        List<Entry> entries = new ArrayList<>();
        Set<String> seen = new HashSet<>();
        if (supported()) {
            for (SharedStorageCleaner.Candidate candidate : candidates) {
                if (Thread.currentThread().isInterrupted()) throw new IOException("Preparation canceled.");
                if (entries.size() >= MAX_BATCH || journal.history.size() + entries.size() >= MAX_HISTORY) break;
                if (!SharedStorageCleaner.validateCandidate(context, candidate)) continue;
                Entry entry = find(candidate);
                if (entry != null && seen.add(entry.uri.toString())
                        && !journal.history.containsKey(entry.uri.toString())
                        && SharedStorageCleaner.isCurrentCandidate(context, candidate)) entries.add(entry);
            }
        }
        return prepare(journal, Operation.TRASH, candidates.size(), entries);
    }

    Prepared prepareRestore(List<Entry> requested) {
        synchronized (JOURNAL_LOCK) {
            try { return prepareExisting(Operation.RESTORE, requested); }
            catch (IOException unavailable) { throw new IllegalStateException(unavailable.getMessage(), unavailable); }
        }
    }

    Prepared prepareDelete(List<Entry> requested) {
        synchronized (JOURNAL_LOCK) {
            try { return prepareExisting(Operation.DELETE, requested); }
            catch (IOException unavailable) { throw new IllegalStateException(unavailable.getMessage(), unavailable); }
        }
    }

    private Prepared prepareExisting(Operation operation, List<Entry> requested) throws IOException {
        Journal journal = readyJournal();
        List<Entry> entries = new ArrayList<>();
        Set<String> seen = new HashSet<>();
        if (supported()) {
            for (Entry chosen : requested) {
                if (Thread.currentThread().isInterrupted()) throw new IOException("Preparation canceled.");
                if (entries.size() >= MAX_BATCH) break;
                Entry entry = journal.history.get(chosen.uri.toString());
                if (entry != null && seen.add(entry.uri.toString())) {
                    Observed observed = observe(entry);
                    if (observed.state == State.TRASHED) entries.add(entry.withObservation(observed));
                }
            }
        }
        return prepare(journal, operation, requested.size(), entries);
    }

    private Journal readyJournal() throws IOException {
        Journal journal = read();
        if (journal.pending != null) throw new IOException("Finish the current Android confirmation first.");
        return journal;
    }

    private Prepared prepare(Journal journal, Operation operation, int requested, List<Entry> entries) throws IOException {
        if (!supported() || entries.isEmpty()) return new Prepared(null, operation, requested, Collections.emptyList());
        List<Uri> uris = new ArrayList<>();
        for (Entry entry : entries) uris.add(entry.uri);
        PendingIntent request;
        try {
            request = operation == Operation.DELETE ? MediaStore.createDeleteRequest(resolver, uris)
                    : MediaStore.createTrashRequest(resolver, uris, operation == Operation.TRASH);
        } catch (RuntimeException unavailable) {
            throw new IOException("Android could not prepare this media operation.", unavailable);
        }
        journal.pending = operation;
        journal.requested = requested;
        journal.pendingEntries.addAll(entries);
        save(journal);
        return new Prepared(request, operation, requested, entries);
    }

    /** Call once after confirmation/cancellation, or to recover an interrupted app process. */
    Outcome finishPending() {
        synchronized (JOURNAL_LOCK) {
            try { return finishPendingInternal(); }
            catch (IOException unavailable) { throw new IllegalStateException(unavailable.getMessage(), unavailable); }
        }
    }

    private Outcome finishPendingInternal() throws IOException {
        Journal journal = read();
        if (journal.pending == null) return new Outcome(null, 0, 0, 0);
        Operation operation = journal.pending;
        int succeeded = 0;
        long bytes = 0;
        for (Entry entry : journal.pendingEntries) {
            Observed observed = observe(entry);
            boolean done = operation == Operation.TRASH && observed.state == State.TRASHED
                    || operation == Operation.RESTORE && observed.state == State.PRESENT
                    || operation == Operation.DELETE && observed.state == State.MISSING;
            if (done) { succeeded++; bytes += entry.size; }
            String key = entry.uri.toString();
            if (observed.state == State.TRASHED) journal.history.put(key, entry.withObservation(observed));
            else if (observed.state == State.UNAVAILABLE && operation == Operation.TRASH) {
                // Retain an unverified recovery record if a card was removed during the dialog.
                // snapshot() exposes it only after Android confirms that the same file is trashed.
                journal.history.put(key, entry);
            } else if (observed.state != State.UNAVAILABLE) journal.history.remove(key);
        }
        int requested = journal.requested;
        journal.pending = null;
        journal.requested = 0;
        journal.pendingEntries.clear();
        save(journal);
        return new Outcome(operation, requested, succeeded, bytes);
    }

    /** Returns only verified, still-restorable files; unavailable volumes retain their records. */
    List<Entry> snapshot() {
        synchronized (JOURNAL_LOCK) {
            try { return snapshotInternal(); }
            catch (IOException unavailable) { throw new IllegalStateException(unavailable.getMessage(), unavailable); }
        }
    }

    private List<Entry> snapshotInternal() throws IOException {
        Journal journal = read();
        List<Entry> entries = new ArrayList<>();
        boolean changed = false;
        for (Entry entry : new ArrayList<>(journal.history.values())) {
            Observed observed = observe(entry);
            if (observed.state == State.TRASHED) {
                Entry current = entry.withObservation(observed);
                entries.add(current);
                if (entry.expiresAtMillis != current.expiresAtMillis || !entry.lastKnownPath.equals(current.lastKnownPath)) {
                    journal.history.put(entry.uri.toString(), current);
                    changed = true;
                }
            } else if (observed.state != State.UNAVAILABLE) {
                journal.history.remove(entry.uri.toString());
                changed = true;
            }
        }
        if (changed) save(journal);
        return Collections.unmodifiableList(entries);
    }

    private Entry find(SharedStorageCleaner.Candidate candidate) {
        if (!supported()) return null;
        try {
            Bundle query = queryArguments();
            query.putString(ContentResolver.QUERY_ARG_SQL_SELECTION, MediaStore.MediaColumns.DATA + " = ?");
            query.putStringArray(ContentResolver.QUERY_ARG_SQL_SELECTION_ARGS, new String[]{candidate.absolutePath});
            try (Cursor rows = resolver.query(MediaStore.Files.getContentUri(MediaStore.VOLUME_EXTERNAL), FILE_PROJECTION, query, null)) {
                if (rows == null || rows.getCount() != 1 || !rows.moveToFirst() || rows.getInt(5) != 0 || rows.getInt(9) != 0) return null;
                String path = rows.getString(1);
                if (!candidate.absolutePath.equals(path) || rows.getLong(3) != candidate.size) return null;
                String volume = rows.getString(7);
                if (!MediaStore.getExternalVolumeNames(context).contains(volume)) return null;
                Uri collection;
                switch (rows.getInt(8)) {
                    case MediaStore.Files.FileColumns.MEDIA_TYPE_IMAGE: collection = MediaStore.Images.Media.getContentUri(volume); break;
                    case MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO: collection = MediaStore.Video.Media.getContentUri(volume); break;
                    case MediaStore.Files.FileColumns.MEDIA_TYPE_AUDIO: collection = MediaStore.Audio.Media.getContentUri(volume); break;
                    default: return null;
                }
                Uri uri = ContentUris.withAppendedId(collection, rows.getLong(0));
                if (!MediaTrashPolicy.isItemUri(uri.toString(), volume)) return null;
                BasicFileAttributes attributes = attributes(path);
                if (!attributes.isRegularFile() || attributes.fileKey() == null || attributes.size() != candidate.size
                        || attributes.lastModifiedTime().toMillis() != candidate.modifiedMillis) return null;
                String version = MediaStore.getVersion(context, volume);
                if (version == null || version.isEmpty()) return null;
                return new Entry(uri, candidate.name, candidate.path, candidate.size, System.currentTimeMillis(), 0,
                        volume, version, attributes.fileKey().toString(), candidate.modifiedMillis, rows.getLong(4), path);
            }
        } catch (IOException | RuntimeException unavailable) {
            return null;
        }
    }

    private Observed observe(Entry entry) {
        if (!supported() || !Environment.isExternalStorageManager()) return new Observed(State.UNAVAILABLE, 0);
        if (!MediaTrashPolicy.isItemUri(entry.uri.toString(), entry.volume)) return new Observed(State.CHANGED, 0);
        try {
            if (!MediaStore.getExternalVolumeNames(context).contains(entry.volume)) return new Observed(State.UNAVAILABLE, 0);
            if (!entry.version.equals(MediaStore.getVersion(context, entry.volume))) return new Observed(State.CHANGED, 0);
            try (Cursor rows = resolver.query(entry.uri, ITEM_PROJECTION, queryArguments(), null)) {
                if (rows == null) return new Observed(State.UNAVAILABLE, 0);
                if (!rows.moveToFirst()) {
                    try {
                        // A missing index row alone does not prove that the file was deleted.
                        attributes(entry.lastKnownPath);
                        return new Observed(State.UNAVAILABLE, 0);
                    } catch (NoSuchFileException removed) {
                        return new Observed(State.MISSING, 0);
                    }
                }
                if (rows.getInt(8) != 0) return new Observed(State.UNAVAILABLE, 0);
                if (rows.getCount() != 1 || !entry.volume.equals(rows.getString(7))
                        || rows.getLong(4) != entry.addedSeconds || rows.getLong(3) != entry.size) return new Observed(State.CHANGED, 0);
                BasicFileAttributes attributes = attributes(rows.getString(1));
                if (!attributes.isRegularFile() || attributes.fileKey() == null
                        || !MediaTrashPolicy.sameIdentity(entry.fileKey, entry.size, entry.modifiedMillis,
                        attributes.fileKey().toString(), attributes.size(), attributes.lastModifiedTime().toMillis())) {
                    return new Observed(State.CHANGED, 0);
                }
                return new Observed(rows.getInt(5) == 1 ? State.TRASHED : State.PRESENT,
                        rows.isNull(6) ? 0 : rows.getLong(6) * 1000L, rows.getString(1));
            }
        } catch (IOException | RuntimeException unavailable) {
            return new Observed(State.UNAVAILABLE, 0);
        }
    }

    private static BasicFileAttributes attributes(String value) throws IOException {
        if (value == null) throw new IOException("Media path unavailable.");
        Path path = Paths.get(value);
        if (!path.isAbsolute() || !path.normalize().equals(path)) throw new IOException("Unexpected media path.");
        // Reject substituted parent links as well as a link in place of the media file.
        for (Path parent = path.getParent(); parent != null; parent = parent.getParent()) {
            if (!Files.readAttributes(parent, BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS).isDirectory()) {
                throw new IOException("Media location changed.");
            }
        }
        return Files.readAttributes(path, BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS);
    }

    @android.annotation.TargetApi(Build.VERSION_CODES.R)
    private static Bundle queryArguments() {
        Bundle arguments = new Bundle();
        arguments.putInt(MediaStore.QUERY_ARG_MATCH_TRASHED, MediaStore.MATCH_INCLUDE);
        arguments.putInt(MediaStore.QUERY_ARG_MATCH_PENDING, MediaStore.MATCH_INCLUDE);
        return arguments;
    }

    private Journal read() throws IOException {
        Journal journal = new Journal();
        String stored = preferences.getString(STATE, null);
        if (stored == null) return journal;
        try {
            JSONObject value = new JSONObject(stored);
            JSONArray history = value.getJSONArray("history");
            for (int index = 0; index < history.length(); index++) {
                Entry entry = decode(history.getJSONObject(index));
                journal.history.put(entry.uri.toString(), entry);
            }
            JSONObject pending = value.optJSONObject("pending");
            if (pending != null) {
                journal.pending = Operation.valueOf(pending.getString("operation"));
                journal.requested = pending.getInt("requested");
                JSONArray entries = pending.getJSONArray("entries");
                if (entries.length() > MAX_BATCH || journal.requested < entries.length()) throw new JSONException("Invalid pending count.");
                for (int index = 0; index < entries.length(); index++) journal.pendingEntries.add(decode(entries.getJSONObject(index)));
            }
            return journal;
        } catch (JSONException | IllegalArgumentException invalid) {
            throw new IOException("Saved trash history could not be read.", invalid);
        }
    }

    private void save(Journal journal) throws IOException {
        try {
            JSONObject value = new JSONObject();
            JSONArray history = new JSONArray();
            for (Entry entry : journal.history.values()) history.put(encode(entry));
            value.put("history", history);
            if (journal.pending != null) {
                JSONArray entries = new JSONArray();
                for (Entry entry : journal.pendingEntries) entries.put(encode(entry));
                value.put("pending", new JSONObject().put("operation", journal.pending.name())
                        .put("requested", journal.requested).put("entries", entries));
            }
            if (!preferences.edit().putString(STATE, value.toString()).commit()) throw new IOException("Trash history could not be saved.");
        } catch (JSONException invalid) {
            throw new IOException("Trash history could not be saved.", invalid);
        }
    }

    private static JSONObject encode(Entry entry) throws JSONException {
        return new JSONObject().put("uri", entry.uri.toString()).put("name", entry.name)
                .put("originalPath", entry.originalPath).put("size", entry.size)
                .put("trashedAt", entry.trashedAtMillis).put("expiresAt", entry.expiresAtMillis)
                .put("volume", entry.volume).put("version", entry.version).put("fileKey", entry.fileKey)
                .put("modified", entry.modifiedMillis).put("added", entry.addedSeconds).put("lastPath", entry.lastKnownPath);
    }

    private static Entry decode(JSONObject value) throws JSONException {
        String uri = value.getString("uri");
        String volume = value.getString("volume");
        String version = value.getString("version");
        String fileKey = value.getString("fileKey");
        long size = value.getLong("size");
        if (!MediaTrashPolicy.isItemUri(uri, volume) || version.isEmpty() || fileKey.isEmpty() || size < 0) {
            throw new JSONException("Invalid trash item.");
        }
        return new Entry(Uri.parse(uri), value.getString("name"), value.getString("originalPath"), size,
                value.getLong("trashedAt"), value.optLong("expiresAt", 0), volume, version, fileKey,
                value.getLong("modified"), value.getLong("added"), value.getString("lastPath"));
    }
}
