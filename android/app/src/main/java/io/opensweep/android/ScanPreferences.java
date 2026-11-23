package io.opensweep.android;

import android.content.Context;
import android.content.SharedPreferences;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/** Local review choices. Protected folders apply to interactive and scheduled scans. */
public final class ScanPreferences {
    public static final int DEFAULT_DOWNLOAD_DAYS = 30;
    private static final String NAME = "scan_preferences";
    private static final String FOLDERS = "protected_folders";
    private static final String DOWNLOAD_DAYS = "download_days";

    private ScanPreferences() {}

    private static SharedPreferences preferences(Context context) {
        return context.getSharedPreferences(NAME, Context.MODE_PRIVATE);
    }

    public static int getOldDownloadDays(Context context) {
        int days = preferences(context).getInt(DOWNLOAD_DAYS, DEFAULT_DOWNLOAD_DAYS);
        return validDays(days) ? days : DEFAULT_DOWNLOAD_DAYS;
    }

    public static void setOldDownloadDays(Context context, int days) {
        if (!validDays(days)) throw new IllegalArgumentException("Choose 7, 30, 90, or 180 days.");
        preferences(context).edit().putInt(DOWNLOAD_DAYS, days).apply();
    }

    static boolean validDays(int days) {
        return days == 7 || days == 30 || days == 90 || days == 180;
    }

    public static List<String> getProtectedFolders(Context context) {
        List<String> result = new ArrayList<>(preferences(context).getStringSet(FOLDERS, Collections.emptySet()));
        Collections.sort(result);
        return Collections.unmodifiableList(result);
    }

    /** The UI supplies a reviewed file's parent directory, never a file wildcard. */
    public static synchronized boolean protectFolder(Context context, String directory) {
        if (directory == null) return false;
        try {
            Path requested = Paths.get(directory);
            if (!requested.isAbsolute()) return false;
            Path path = requested.toRealPath();
            if (!Files.isDirectory(path, LinkOption.NOFOLLOW_LINKS)) return false;
            Set<String> folders = new HashSet<>(getProtectedFolders(context));
            if (isProtected(path, folders)) return true;
            folders.removeIf(existing -> Paths.get(existing).startsWith(path));
            folders.add(path.toString());
            preferences(context).edit().putStringSet(FOLDERS, folders).apply();
            return true;
        } catch (IOException | RuntimeException unavailable) {
            return false;
        }
    }

    public static synchronized void unprotectFolder(Context context, String directory) {
        Set<String> folders = new HashSet<>(getProtectedFolders(context));
        folders.remove(directory);
        preferences(context).edit().putStringSet(FOLDERS, folders).apply();
    }

    public static boolean isProtected(Context context, String path) {
        if (path == null) return true;
        try {
            return isProtected(Paths.get(path), getProtectedFolders(context));
        } catch (RuntimeException invalid) {
            return true;
        }
    }

    static boolean isProtected(Path path, Iterable<String> folders) {
        if (!path.isAbsolute()) return true;
        Path normalized = path.normalize();
        for (String folder : folders) {
            try {
                Path protectedPath = Paths.get(folder);
                if (protectedPath.isAbsolute() && normalized.startsWith(protectedPath.normalize())) return true;
            } catch (RuntimeException invalid) {
                // Ignore an invalid saved entry, never interpret it as a wildcard.
            }
        }
        return false;
    }
}
