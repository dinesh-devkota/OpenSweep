package io.opensweep.android;

import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;

import java.io.IOException;
import java.io.RandomAccessFile;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.attribute.FileTime;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.Assert.*;
import static org.junit.Assume.assumeNoException;
import static org.junit.Assume.assumeTrue;

public class SharedStorageCleanerTest {
    @Rule public TemporaryFolder temporary = new TemporaryFolder();

    private SharedStorageCleaner.Volume volume(Path root) throws IOException {
        assumeTrue("These fixtures need Unix file identities, as used on Android.",
                Files.readAttributes(root, java.nio.file.attribute.BasicFileAttributes.class).fileKey() != null);
        return new SharedStorageCleaner.Volume(root.getFileName().toString(), "Storage", root);
    }

    private Path write(Path root, String relative, String contents) throws IOException {
        Path path = root.resolve(relative);
        Files.createDirectories(path.getParent());
        Files.write(path, contents.getBytes(java.nio.charset.StandardCharsets.UTF_8));
        return path;
    }

    private SharedStorageCleaner.ScanResult scan(Path root, SharedStorageCleaner.Mode mode) throws IOException {
        return SharedStorageCleaner.scan(Collections.singletonList(volume(root)), mode, () -> {});
    }

    @Test public void scansPublicFilesAcrossVolumesAndKeepsDirectoriesAndPrivateStorage() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        Path card = temporary.newFolder("card").toPath();
        write(root, "Download/job.tmp", "temporary");
        write(root, "Android/media/example/cache.tmp", "media temp");
        write(root, "Android/data/example/private.tmp", "private");
        write(root, "Android/obb/example/file.tmp", "private");
        write(root, ".android_secure/encrypted.tmp", "private");
        write(root, "Documents/keep.txt", "keep");
        Files.createDirectories(root.resolve("directory.tmp"));
        write(card, "DCIM/Thumbs.db", "thumbnails");
        List<SharedStorageCleaner.Volume> volumes = Arrays.asList(volume(root), volume(card));
        SharedStorageCleaner.ScanResult result = SharedStorageCleaner.scan(volumes, SharedStorageCleaner.Mode.JUNK, () -> {});
        assertEquals(3, result.files.size());
        for (SharedStorageCleaner.Candidate candidate : result.files) assertTrue(SharedStorageCleaner.delete(volumes, candidate));
        assertTrue(Files.isDirectory(root.resolve("directory.tmp")));
        assertTrue(Files.exists(root.resolve("Android/data/example/private.tmp")));
        assertTrue(Files.exists(root.resolve("Documents/keep.txt")));
    }

    @Test public void pathsAndFilenameRulesHavePreciseBoundaries() {
        assertFalse(SharedStorageCleaner.allowed(Paths.get("Android/data/a.tmp")));
        assertFalse(SharedStorageCleaner.allowed(Paths.get("android/OBB/a.tmp")));
        assertFalse(SharedStorageCleaner.allowed(Paths.get("../outside.tmp")));
        assertTrue(SharedStorageCleaner.allowed(Paths.get("Android/media/a.tmp")));
        assertTrue(SharedStorageCleaner.allowed(Paths.get("AndroidBackup/data/a.tmp")));
        assertTrue(CleanupRules.isJunkName("x.TMP"));
        assertFalse(CleanupRules.isJunkName("x.tmp.doc"));
        assertFalse(CleanupRules.isJunkName("notes.log"));
    }

    @Test public void changedFileReplacementAndRemountedRootCannotBeDeleted() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        Path file = write(root, "Download/file.tmp", "original");
        SharedStorageCleaner.Candidate changed = scan(root, SharedStorageCleaner.Mode.JUNK).files.get(0);
        Files.write(file, "changed and larger".getBytes());
        assertFalse(SharedStorageCleaner.delete(Collections.singletonList(volume(root)), changed));
        SharedStorageCleaner.Candidate replaced = scan(root, SharedStorageCleaner.Mode.JUNK).files.get(0);
        Files.move(file, file.resolveSibling("old"));
        write(root, "Download/file.tmp", "changed and larger");
        assertFalse(SharedStorageCleaner.delete(Collections.singletonList(volume(root)), replaced));
        SharedStorageCleaner.Candidate unmounted = scan(root, SharedStorageCleaner.Mode.JUNK).files.get(0);
        assertFalse(SharedStorageCleaner.delete(Collections.emptyList(), unmounted));
        Path moved = root.resolveSibling("old-volume");
        Files.move(root, moved);
        Files.createDirectory(root);
        write(root, "Download/file.tmp", "changed and larger");
        assertFalse(SharedStorageCleaner.delete(Collections.singletonList(volume(root)), unmounted));
    }

    @Test public void symlinksAreNotScannedAndReplacingParentWithLinkPreventsDeletion() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        Path outside = temporary.newFolder("outside").toPath();
        write(outside, "external.tmp", "keep");
        try {
            Files.createSymbolicLink(root.resolve("link"), outside);
        } catch (IOException | UnsupportedOperationException permission) {
            assumeNoException(permission);
        }
        assertTrue(scan(root, SharedStorageCleaner.Mode.JUNK).files.isEmpty());
        Path folder = Files.createDirectory(root.resolve("Download"));
        write(root, "Download/file.tmp", "temp");
        SharedStorageCleaner.Candidate candidate = scan(root, SharedStorageCleaner.Mode.JUNK).files.get(0);
        Files.move(folder, root.resolve("moved"));
        Files.createSymbolicLink(folder, outside);
        assertFalse(SharedStorageCleaner.delete(Collections.singletonList(volume(root)), candidate));
        assertTrue(Files.exists(outside.resolve("external.tmp")));
    }

    @Test public void largeFilesStartAtOneHundredMebibytes() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        try (RandomAccessFile file = new RandomAccessFile(root.resolve("large.bin").toFile(), "rw")) {
            file.setLength(SharedStorageCleaner.LARGE_FILE_BYTES);
        }
        write(root, "small.tmp", "small");
        assertEquals("large.bin", scan(root, SharedStorageCleaner.Mode.LARGE_FILES).files.get(0).name);
        assertFalse(SharedStorageCleaner.included("small.bin", SharedStorageCleaner.LARGE_FILE_BYTES - 1,
                SharedStorageCleaner.Mode.LARGE_FILES));
    }

    @Test public void duplicatesAreContentVerifiedAndAlwaysRetainTheKeeper() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        write(root, "a.txt", "same content");
        write(root, "b.txt", "same content");
        write(root, "c.txt", "different!!!");
        List<SharedStorageCleaner.Candidate> matches = scan(root, SharedStorageCleaner.Mode.DUPLICATES).files;
        assertEquals(2, matches.size());
        assertTrue(matches.get(0).keeper);
        assertEquals(matches.get(0).duplicateGroup, matches.get(1).duplicateGroup);
        List<SharedStorageCleaner.Volume> volumes = Collections.singletonList(volume(root));
        assertFalse(SharedStorageCleaner.delete(volumes, matches.get(0)));
        assertTrue(SharedStorageCleaner.delete(volumes, matches.get(1)));
        assertTrue(Files.exists(root.resolve("a.txt")));
        assertTrue(Files.exists(root.resolve("c.txt")));
        assertFalse(Files.exists(root.resolve("b.txt")));
    }

    @Test public void duplicateDeletionRehashesKeeperEvenIfSizeAndTimeAreUnchanged() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        Path keeper = write(root, "a.txt", "same");
        Path copy = write(root, "b.txt", "same");
        List<SharedStorageCleaner.Candidate> matches = scan(root, SharedStorageCleaner.Mode.DUPLICATES).files;
        FileTime time = Files.getLastModifiedTime(keeper);
        Files.write(keeper, "edit".getBytes());
        Files.setLastModifiedTime(keeper, time);
        assertFalse(SharedStorageCleaner.delete(Collections.singletonList(volume(root)), matches.get(1)));
        assertTrue(Files.exists(copy));
    }

    @Test public void replacementDirectoryAndInterruptedDeletionArePreserved() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        Path file = write(root, "file.tmp", "temporary");
        SharedStorageCleaner.Candidate preview = scan(root, SharedStorageCleaner.Mode.JUNK).files.get(0);
        Files.move(file, root.resolve("original"));
        Files.createDirectory(file);
        assertFalse(SharedStorageCleaner.delete(Collections.singletonList(volume(root)), preview));
        assertTrue(Files.isDirectory(file));

        Path remaining = write(root, "another.tmp", "temporary");
        SharedStorageCleaner.Candidate candidate = scan(root, SharedStorageCleaner.Mode.JUNK).files.get(0);
        Thread.currentThread().interrupt();
        try {
            assertFalse(SharedStorageCleaner.delete(Collections.singletonList(volume(root)), candidate));
        } finally {
            Thread.interrupted();
        }
        assertTrue(Files.exists(remaining));
    }

    @Test public void hardLinksDoNotAppearAsReclaimableDuplicateCopies() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        Path original = write(root, "a.txt", "same bytes");
        try {
            Files.createLink(root.resolve("b.txt"), original);
        } catch (IOException | UnsupportedOperationException unavailable) {
            assumeNoException(unavailable);
        }
        assertTrue(scan(root, SharedStorageCleaner.Mode.DUPLICATES).files.isEmpty());
    }

    @Test public void cancellationInterruptsTraversalAndHashing() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        for (int index = 0; index < 5; index++) write(root, index + ".bin", "same");
        AtomicInteger checks = new AtomicInteger();
        try {
            SharedStorageCleaner.scan(Collections.singletonList(volume(root)), SharedStorageCleaner.Mode.DUPLICATES, () -> {
                if (checks.incrementAndGet() == 15) throw new CancellationException();
            });
            fail("Cancellation was ignored");
        } catch (CancellationException expected) {
            assertEquals(15, checks.get());
        }
    }
}
