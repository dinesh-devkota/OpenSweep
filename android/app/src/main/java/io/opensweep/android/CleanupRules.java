package io.opensweep.android;

import java.nio.file.Path;
import java.util.Locale;

final class CleanupRules {
    static final long DAY_MILLIS = 24L * 60 * 60 * 1000;
    private CleanupRules() {}

    static boolean isJunkName(String name) {
        return name != null && (name.equalsIgnoreCase("Thumbs.db") || name.equalsIgnoreCase(".DS_Store")
                || name.regionMatches(true, name.length() - 4, ".tmp", 0, 4));
    }

    static boolean isApk(String name) {
        return hasExtension(name, ".apk", ".apks", ".apkm", ".xapk");
    }

    static boolean isImage(String name) {
        return hasExtension(name, ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".avif", ".gif");
    }

    static boolean isVideo(String name) {
        return hasExtension(name, ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".3gp", ".3gpp", ".avi", ".mpeg", ".mpg");
    }

    static boolean isScreenshot(Path relative) {
        if (relative == null || relative.getFileName() == null || !isImage(relative.getFileName().toString())) return false;
        String name = relative.getFileName().toString().toLowerCase(Locale.ROOT);
        if (name.startsWith("screenshot_") || name.startsWith("screenshot-") || name.startsWith("screenshot ")) return true;
        for (int index = 0; index < relative.getNameCount() - 1; index++) {
            if (relative.getName(index).toString().equalsIgnoreCase("Screenshots")) return true;
        }
        return false;
    }

    static boolean isOldDownload(Path relative, long modifiedMillis, long nowMillis, int minimumDays) {
        if (relative == null || relative.getNameCount() < 2 || modifiedMillis <= 0 || nowMillis < modifiedMillis) return false;
        String first = relative.getName(0).toString();
        if (!first.equalsIgnoreCase("Download") && !first.equalsIgnoreCase("Downloads")) return false;
        return nowMillis - modifiedMillis >= (long) minimumDays * DAY_MILLIS;
    }

    private static boolean hasExtension(String name, String... extensions) {
        if (name == null) return false;
        String lower = name.toLowerCase(Locale.ROOT);
        for (String extension : extensions) if (lower.length() > extension.length() && lower.endsWith(extension)) return true;
        return false;
    }
}
