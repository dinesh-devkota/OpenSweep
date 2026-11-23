package io.opensweep.android;

import org.junit.Test;

import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.Arrays;
import java.util.Collections;

import static org.junit.Assert.*;

public class ScanPreferencesTest {
    @Test public void folderProtectionUsesPathBoundariesInsteadOfStringPrefixes() {
        Path root = Paths.get(System.getProperty("java.io.tmpdir")).toAbsolutePath().resolve("phone");
        String protectedFolder = root.resolve("Camera").toString();
        assertTrue(ScanPreferences.isProtected(root.resolve("Camera"), Collections.singletonList(protectedFolder)));
        assertTrue(ScanPreferences.isProtected(root.resolve("Camera/2026/photo.jpg"), Collections.singletonList(protectedFolder)));
        assertFalse(ScanPreferences.isProtected(root.resolve("CameraBackup/photo.jpg"), Collections.singletonList(protectedFolder)));
        assertFalse(ScanPreferences.isProtected(root.resolve("other/Camera/photo.jpg"), Collections.singletonList(protectedFolder)));
        assertTrue(ScanPreferences.isProtected(root.resolve("other/../Camera/photo.jpg"), Collections.singletonList(protectedFolder)));
        assertFalse(ScanPreferences.isProtected(root.resolve("Camera/../photo.jpg"), Collections.singletonList(protectedFolder)));
        assertFalse(ScanPreferences.isProtected(root.resolve("photo.jpg"), Arrays.asList("Camera", "invalid\0path")));
        assertTrue(ScanPreferences.isProtected(Paths.get("relative/photo.jpg"), Collections.emptyList()));
    }

    @Test public void downloadAgeChoicesCannotBecomeNegativeOrUnlimited() {
        for (int days : new int[] { 7, 30, 90, 180 }) assertTrue(ScanPreferences.validDays(days));
        for (int days : new int[] { -1, 0, 1, 29, Integer.MAX_VALUE }) assertFalse(ScanPreferences.validDays(days));
    }
}
