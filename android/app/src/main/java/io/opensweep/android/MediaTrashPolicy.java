package io.opensweep.android;

import java.net.URI;

/** Validation shared by persisted recovery records and the Android adapter. */
final class MediaTrashPolicy {
    private MediaTrashPolicy() {}

    static boolean isItemUri(String value, String volume) {
        try {
            URI uri = new URI(value);
            if (!"content".equals(uri.getScheme()) || !"media".equals(uri.getRawAuthority())
                    || uri.getRawQuery() != null || uri.getRawFragment() != null
                    || volume == null || !volume.matches("[A-Za-z0-9_-]+")) return false;
            String[] parts = uri.getRawPath().split("/", -1);
            return parts.length == 5 && parts[0].isEmpty() && parts[1].equals(volume)
                    && (parts[2].equals("images") || parts[2].equals("video") || parts[2].equals("audio"))
                    && parts[3].equals("media") && parts[4].matches("[1-9][0-9]*")
                    && Long.parseLong(parts[4]) > 0;
        } catch (Exception malformed) {
            return false;
        }
    }

    static boolean sameIdentity(String recordedKey, long recordedSize, long recordedModified,
                                String currentKey, long currentSize, long currentModified) {
        return recordedKey != null && !recordedKey.isEmpty() && recordedKey.equals(currentKey)
                && recordedSize >= 0 && recordedSize == currentSize && recordedModified == currentModified;
    }
}
