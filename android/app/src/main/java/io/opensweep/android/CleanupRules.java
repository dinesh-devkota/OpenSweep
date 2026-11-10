package io.opensweep.android;

final class CleanupRules {
    private CleanupRules() {}

    static boolean isJunkName(String name) {
        return name != null && (name.equalsIgnoreCase("Thumbs.db") || name.equalsIgnoreCase(".DS_Store")
                || name.regionMatches(true, name.length() - 4, ".tmp", 0, 4));
    }
}
