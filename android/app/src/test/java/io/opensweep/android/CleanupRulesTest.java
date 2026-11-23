package io.opensweep.android;

import org.junit.Test;

import java.nio.file.Paths;

import static org.junit.Assert.*;

public class CleanupRulesTest {
    private static final long NOW = 200L * CleanupRules.DAY_MILLIS;

    @Test public void oldDownloadsUseFullDaysAndOnlyThePublicDownloadsRoot() {
        long boundary = NOW - 30L * CleanupRules.DAY_MILLIS;
        assertTrue(CleanupRules.isOldDownload(Paths.get("Download/manual.pdf"), boundary, NOW, 30));
        assertFalse(CleanupRules.isOldDownload(Paths.get("Download/manual.pdf"), boundary + 1, NOW, 30));
        assertTrue(CleanupRules.isOldDownload(Paths.get("Downloads/archive/manual.pdf"), boundary, NOW, 30));
        assertTrue(CleanupRules.isOldDownload(Paths.get("download/manual.pdf"), boundary, NOW, 30));
        assertFalse(CleanupRules.isOldDownload(Paths.get("Documents/Download/manual.pdf"), boundary, NOW, 30));
        assertFalse(CleanupRules.isOldDownload(Paths.get("DownloadsBackup/manual.pdf"), boundary, NOW, 30));
        assertFalse(CleanupRules.isOldDownload(Paths.get("Download/manual.pdf"), 0, NOW, 30));
        assertFalse(CleanupRules.isOldDownload(Paths.get("Download/manual.pdf"), NOW + 1, NOW, 30));
        assertFalse(CleanupRules.isOldDownload(Paths.get("Download/manual.pdf"), boundary, NOW, 90));
    }

    @Test public void screenshotReviewRequiresAnImageAndRecognizedFolderOrFilename() {
        assertTrue(CleanupRules.isScreenshot(Paths.get("Pictures/Screenshots/capture.PNG")));
        assertTrue(CleanupRules.isScreenshot(Paths.get("DCIM/screenshots/capture.jpg")));
        assertTrue(CleanupRules.isScreenshot(Paths.get("Download/Screenshot_2026-11-17.webp")));
        assertTrue(CleanupRules.isScreenshot(Paths.get("Screenshot-2026.heic")));
        assertFalse(CleanupRules.isScreenshot(Paths.get("Pictures/Screenshots/notes.pdf")));
        assertFalse(CleanupRules.isScreenshot(Paths.get("Pictures/ScreenshotsBackup/photo.jpg")));
        assertTrue(CleanupRules.isScreenshot(Paths.get("Pictures/Screenshot 2026.png")));
        assertFalse(CleanupRules.isScreenshot(Paths.get("Pictures/myscreenshot.jpg")));
        assertFalse(CleanupRules.isScreenshot(Paths.get("Pictures/portrait.jpg")));
    }

    @Test public void installerAndVideoExtensionsHaveExactBoundaries() {
        assertTrue(CleanupRules.isApk("application.APK"));
        assertTrue(CleanupRules.isApk("application.apks"));
        assertTrue(CleanupRules.isApk("application.apkm"));
        assertTrue(CleanupRules.isApk("application.xapk"));
        assertFalse(CleanupRules.isApk("application.apk.txt"));
        assertFalse(CleanupRules.isApk(".apk"));
        assertFalse(CleanupRules.isApk(null));
        assertTrue(CleanupRules.isVideo("movie.MP4"));
        assertTrue(CleanupRules.isVideo("movie.mkv"));
        assertFalse(CleanupRules.isVideo("movie.mp4.tmp"));
        assertFalse(CleanupRules.isVideo("backup.zip"));
    }

    @Test public void categoryBoundariesDoNotTreatAllLargeOrOldFilesAsJunk() {
        long size = SharedStorageCleaner.LARGE_FILE_BYTES;
        assertTrue(SharedStorageCleaner.included(Paths.get("Movies/movie.mp4"), size, NOW,
                SharedStorageCleaner.Mode.LARGE_VIDEOS, NOW, 30));
        assertFalse(SharedStorageCleaner.included(Paths.get("Movies/movie.mp4"), size - 1, NOW,
                SharedStorageCleaner.Mode.LARGE_VIDEOS, NOW, 30));
        assertFalse(SharedStorageCleaner.included(Paths.get("backup.zip"), size, NOW,
                SharedStorageCleaner.Mode.LARGE_VIDEOS, NOW, 30));
        assertTrue(SharedStorageCleaner.included(Paths.get("Download/app.apk"), 1, NOW,
                SharedStorageCleaner.Mode.APK_INSTALLERS, NOW, 30));
        assertFalse(SharedStorageCleaner.included(Paths.get("Download/app.apk"), 0, NOW,
                SharedStorageCleaner.Mode.APK_INSTALLERS, NOW, 30));
        assertFalse(SharedStorageCleaner.included(Paths.get("Download/app.apk"), 1, NOW,
                SharedStorageCleaner.Mode.JUNK, NOW, 30));
        assertFalse(SharedStorageCleaner.included(Paths.get("Download/.nomedia"), 1, 1,
                SharedStorageCleaner.Mode.OLD_DOWNLOADS, NOW, 30));
    }

    @Test public void providerTrashAndPendingFilesAreNeverCleanupCandidates() {
        assertFalse(SharedStorageCleaner.allowed(Paths.get("Pictures/.trashed-1780000000-photo.jpg")));
        assertFalse(SharedStorageCleaner.allowed(Paths.get("Download/.pending-1780000000-movie.mp4")));
        assertFalse(SharedStorageCleaner.allowed(Paths.get(".OpenSweepTrash/item.tmp")));
        assertFalse(SharedStorageCleaner.allowed(Paths.get("Pictures/.trash/item.tmp")));
        assertTrue(SharedStorageCleaner.allowed(Paths.get("Pictures/trashed-photo.jpg")));
        assertTrue(SharedStorageCleaner.allowed(Paths.get("Download/pending-notes.pdf")));
    }
}
