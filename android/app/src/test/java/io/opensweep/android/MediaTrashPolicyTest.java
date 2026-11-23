package io.opensweep.android;

import org.junit.Test;

import static org.junit.Assert.*;

public class MediaTrashPolicyTest {
    @Test public void acceptsOnlyExactMediaItemsOnTheRecordedVolume() {
        assertTrue(MediaTrashPolicy.isItemUri("content://media/external_primary/images/media/42", "external_primary"));
        assertTrue(MediaTrashPolicy.isItemUri("content://media/ABCD-0123/video/media/9", "ABCD-0123"));
        assertTrue(MediaTrashPolicy.isItemUri("content://media/external_primary/audio/media/1", "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri("content://media/external_primary/images/media/42", "ABCD-0123"));
        assertFalse(MediaTrashPolicy.isItemUri("content://other/external_primary/images/media/42", "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri("content://media/external_primary/file/42", "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri("content://media/external_primary/images/media", "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri("content://media/external_primary/images/media/0", "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri("content://media/external_primary/images/media/9223372036854775808", "external_primary"));
    }

    @Test public void rejectsAmbiguousOrRedirectedRecoveryUris() {
        String base = "content://media/external_primary/images/media/42";
        assertFalse(MediaTrashPolicy.isItemUri(base + "?anything=1", "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri(base + "#fragment", "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri(base + "/", "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri(base.replace("media/42", "media/%34%32"), "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri(base.replace("//media/", "//media:80/"), "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri(base.replace("//media/", "//user@media/"), "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri("file:///storage/emulated/0/DCIM/a.jpg", "external_primary"));
        assertFalse(MediaTrashPolicy.isItemUri(null, "external_primary"));
    }

    @Test public void recoveryRejectsReusedRowsAndChangedFiles() {
        assertTrue(MediaTrashPolicy.sameIdentity("dev=1,ino=2", 90, 1234, "dev=1,ino=2", 90, 1234));
        assertFalse(MediaTrashPolicy.sameIdentity("dev=1,ino=2", 90, 1234, "dev=1,ino=3", 90, 1234));
        assertFalse(MediaTrashPolicy.sameIdentity("dev=1,ino=2", 90, 1234, "dev=1,ino=2", 91, 1234));
        assertFalse(MediaTrashPolicy.sameIdentity("dev=1,ino=2", 90, 1234, "dev=1,ino=2", 90, 1235));
        assertFalse(MediaTrashPolicy.sameIdentity("", 90, 1234, "", 90, 1234));
    }
}
