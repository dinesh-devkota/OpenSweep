package io.opensweep.android;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.os.Build;
import android.os.SystemClock;
import android.provider.OpenableColumns;
import android.system.ErrnoException;
import android.system.Os;
import android.system.OsConstants;

import java.io.FileDescriptor;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;

/** Read-only, expiring grants for individual reviewed files; never exposes a storage root. */
public final class PreviewProvider extends ContentProvider {
    private static final long LIFETIME_MS = 10 * 60 * 1000L;
    private static final int MAX_GRANTS = 24;
    private static final Map<String, Grant> GRANTS = new LinkedHashMap<>();

    private static final class Grant {
        final SharedStorageCleaner.Candidate file;
        final String mime;
        final long expires;

        Grant(SharedStorageCleaner.Candidate file, String mime) {
            this.file = file;
            this.mime = mime;
            expires = SystemClock.elapsedRealtime() + LIFETIME_MS;
        }
    }

    static Uri grant(Context context, SharedStorageCleaner.Candidate file, String mime) throws IOException {
        // Check the actual descriptor before offering a URI to another app.
        try (ParcelFileDescriptor ignored = openSnapshot(context, file)) {
            synchronized (GRANTS) {
                prune();
                while (GRANTS.size() >= MAX_GRANTS) {
                    Iterator<String> oldest = GRANTS.keySet().iterator();
                    oldest.next();
                    oldest.remove();
                }
                String token = UUID.randomUUID().toString();
                GRANTS.put(token, new Grant(file, mime));
                return new Uri.Builder().scheme("content").authority(context.getPackageName() + ".preview")
                        .appendPath(token).build();
            }
        }
    }

    static void revoke(Uri uri) {
        synchronized (GRANTS) { GRANTS.remove(uri.getLastPathSegment()); }
    }

    private static void prune() {
        long now = SystemClock.elapsedRealtime();
        GRANTS.values().removeIf(grant -> grant.expires <= now);
    }

    private Grant find(Uri uri) throws FileNotFoundException {
        if (getContext() == null || !"content".equals(uri.getScheme())
                || !(getContext().getPackageName() + ".preview").equals(uri.getAuthority())
                || uri.getPathSegments().size() != 1 || uri.getQuery() != null || uri.getFragment() != null)
            throw new FileNotFoundException("Invalid preview.");
        synchronized (GRANTS) {
            prune();
            Grant result = GRANTS.get(uri.getLastPathSegment());
            if (result == null) throw new FileNotFoundException("Preview expired. Open the file again.");
            return result;
        }
    }

    /** Opens a regular file without following its final link, then verifies its open inode. */
    static ParcelFileDescriptor openSnapshot(Context context, SharedStorageCleaner.Candidate file) throws IOException {
        if (!SharedStorageCleaner.isCurrentCandidate(context, file)) throw new IOException("File changed.");
        FileDescriptor raw = null;
        ParcelFileDescriptor result = null;
        try {
            int flags = OsConstants.O_RDONLY | OsConstants.O_NOFOLLOW | OsConstants.O_NONBLOCK;
            // Linux O_CLOEXEC is available on Android 8.0, before its Java constant was public.
            flags |= Build.VERSION.SDK_INT >= 27 ? OsConstants.O_CLOEXEC : 0x80000;
            raw = Os.open(file.absolutePath, flags, 0);
            if (!OsConstants.S_ISREG(Os.fstat(raw).st_mode)) throw new IOException("Not a regular file.");
            result = ParcelFileDescriptor.dup(raw);
            // Following /proc/self/fd here addresses the already-open descriptor, not a user path.
            BasicFileAttributes opened = Files.readAttributes(Paths.get("/proc/self/fd/" + result.getFd()),
                    BasicFileAttributes.class);
            if (!SharedStorageCleaner.matchesPreviewAttributes(file, opened)
                    || !SharedStorageCleaner.isCurrentCandidate(context, file)) throw new IOException("File changed.");
            ParcelFileDescriptor accepted = result;
            result = null;
            return accepted;
        } catch (ErrnoException | SecurityException failure) {
            throw new IOException("Preview is unavailable.", failure);
        } finally {
            if (result != null) result.close();
            if (raw != null) {
                try { Os.close(raw); } catch (ErrnoException ignored) { }
            }
        }
    }

    @Override public boolean onCreate() { return true; }

    @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        if (!"r".equals(mode)) throw new FileNotFoundException("Previews are read-only.");
        Grant grant = find(uri);
        try { return openSnapshot(getContext(), grant.file); }
        catch (IOException failure) { throw new FileNotFoundException("File changed or is no longer available."); }
    }

    @Override public String getType(Uri uri) {
        try { return find(uri).mime; }
        catch (FileNotFoundException unavailable) { return null; }
    }

    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        Grant grant;
        try { grant = find(uri); }
        catch (FileNotFoundException unavailable) { return new MatrixCursor(new String[0]); }
        String[] requested = projection == null
                ? new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE} : projection;
        MatrixCursor cursor = new MatrixCursor(requested, 1);
        MatrixCursor.RowBuilder row = cursor.newRow();
        for (String column : requested) {
            if (OpenableColumns.DISPLAY_NAME.equals(column)) row.add(grant.file.name);
            else if (OpenableColumns.SIZE.equals(column)) row.add(grant.file.size);
            else row.add(null);
        }
        return cursor;
    }

    @Override public Uri insert(Uri uri, ContentValues values) { throw new UnsupportedOperationException("Read-only preview."); }
    @Override public int delete(Uri uri, String selection, String[] selectionArgs) { throw new UnsupportedOperationException("Read-only preview."); }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) { throw new UnsupportedOperationException("Read-only preview."); }
}
