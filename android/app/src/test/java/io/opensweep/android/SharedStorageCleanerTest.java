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
import java.util.ArrayList;
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

    @Test public void duplicateScanProtectsProviderBookkeepingWithoutHidingSimilarPersonalFiles() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        write(root, ".transforms/synthetic.jpg", "duplicate");
        write(root, ".TRANSFORMS/nested/synthetic.jpg", "duplicate");
        write(root, "Download/.database_uuid", "duplicate");
        write(root, "Pictures/.DATABASE_UUID", "duplicate");
        write(root, "Download/.nomedia", "duplicate");
        write(root, "Pictures/.NOMEDIA", "duplicate");
        write(root, "Documents/.database_uuid.txt", "duplicate");
        write(root, "Documents/.nomedia-notes", "duplicate");
        write(root, ".transforms-backup/photo.jpg", "duplicate");
        write(root, "Documents/.transforms/photo.jpg", "duplicate");
        SharedStorageCleaner.ScanResult result = scan(root, SharedStorageCleaner.Mode.DUPLICATES);
        assertEquals(4, result.files.size());
        assertEquals(2, result.protectedFolders);
        assertEquals(8L, result.scannedFiles);
        assertEquals(6L, result.scannedFolders);
        for (SharedStorageCleaner.Candidate file : result.files) {
            assertFalse(file.name.equalsIgnoreCase(".database_uuid"));
            assertFalse(file.name.equalsIgnoreCase(".nomedia"));
            assertFalse(file.path.startsWith("Storage/.transforms/"));
            assertFalse(file.path.startsWith("Storage/.TRANSFORMS/"));
        }
        List<SharedStorageCleaner.Volume> volumes = Collections.singletonList(volume(root));
        for (SharedStorageCleaner.Candidate file : result.files) {
            if (!file.keeper) assertTrue(SharedStorageCleaner.delete(volumes, file));
        }
        assertTrue(Files.exists(root.resolve(".transforms/synthetic.jpg")));
        assertTrue(Files.exists(root.resolve(".TRANSFORMS/nested/synthetic.jpg")));
        assertTrue(Files.exists(root.resolve("Download/.database_uuid")));
        assertTrue(Files.exists(root.resolve("Pictures/.NOMEDIA")));
        assertTrue(SharedStorageCleaner.included(".database_uuid", SharedStorageCleaner.LARGE_FILE_BYTES,
                SharedStorageCleaner.Mode.LARGE_FILES));
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

    @Test public void choosingKeeperRetargetsOnlyItsGroupAndProtectsTheChosenFile() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        write(root, "a.txt", "same content");
        write(root, "b.txt", "same content");
        write(root, "c.txt", "other content");
        write(root, "d.txt", "other content");
        List<SharedStorageCleaner.Candidate> original = scan(root, SharedStorageCleaner.Mode.DUPLICATES).files;
        SharedStorageCleaner.Candidate chosen = original.stream().filter(f -> f.name.equals("b.txt")).findFirst().get();
        SharedStorageCleaner.Candidate untouched = original.stream().filter(f -> f.name.equals("c.txt")).findFirst().get();
        List<SharedStorageCleaner.Candidate> changed = SharedStorageCleaner.chooseKeeper(original, chosen);
        assertTrue(changed.contains(untouched));
        assertFalse(changed.contains(chosen));
        assertFalse(chosen.keeper);
        SharedStorageCleaner.Candidate newKeeper = changed.stream().filter(f -> f.name.equals("b.txt")).findFirst().get();
        SharedStorageCleaner.Candidate oldKeeper = changed.stream().filter(f -> f.name.equals("a.txt")).findFirst().get();
        assertTrue(newKeeper.keeper);
        assertFalse(oldKeeper.keeper);
        assertEquals(root.resolve("b.txt").toString(), newKeeper.absolutePath);
        List<SharedStorageCleaner.Volume> volumes = Collections.singletonList(volume(root));
        assertFalse(SharedStorageCleaner.delete(volumes, newKeeper));
        assertTrue(SharedStorageCleaner.delete(volumes, oldKeeper));
        assertTrue(Files.exists(root.resolve("b.txt")));
        assertTrue(Files.exists(root.resolve("c.txt")));
        assertTrue(Files.exists(root.resolve("d.txt")));
    }

    @Test public void changedNewKeeperPreventsDeletingTheOldKeeper() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        Path first = write(root, "a.txt", "same");
        Path chosenPath = write(root, "b.txt", "same");
        List<SharedStorageCleaner.Candidate> matches = scan(root, SharedStorageCleaner.Mode.DUPLICATES).files;
        SharedStorageCleaner.Candidate chosen = matches.stream().filter(f -> f.name.equals("b.txt")).findFirst().get();
        List<SharedStorageCleaner.Candidate> updated = SharedStorageCleaner.chooseKeeper(matches, chosen);
        FileTime time = Files.getLastModifiedTime(chosenPath);
        Files.write(chosenPath, "edit".getBytes());
        Files.setLastModifiedTime(chosenPath, time);
        SharedStorageCleaner.Candidate oldKeeper = updated.stream().filter(f -> f.name.equals("a.txt")).findFirst().get();
        assertFalse(SharedStorageCleaner.delete(Collections.singletonList(volume(root)), oldKeeper));
        assertTrue(Files.exists(first));
        Files.delete(chosenPath);
        assertFalse(SharedStorageCleaner.delete(Collections.singletonList(volume(root)), oldKeeper));
        assertTrue(Files.exists(first));
    }

    @Test public void choosingKeeperRejectsUnknownAndIncompleteGroups() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        write(root, "a.txt", "same");
        write(root, "b.txt", "same");
        List<SharedStorageCleaner.Candidate> matches = scan(root, SharedStorageCleaner.Mode.DUPLICATES).files;
        SharedStorageCleaner.Candidate unknown = scan(root, SharedStorageCleaner.Mode.DUPLICATES).files.get(0);
        assertThrows(IllegalArgumentException.class, () -> SharedStorageCleaner.chooseKeeper(matches, unknown));
        assertThrows(IllegalArgumentException.class, () -> SharedStorageCleaner.chooseKeeper(
                Collections.singletonList(matches.get(0)), matches.get(0)));
        assertThrows(IllegalArgumentException.class, () -> SharedStorageCleaner.chooseKeeper(matches, null));
        write(root, "extra.tmp", "temporary");
        List<SharedStorageCleaner.Candidate> junk = scan(root, SharedStorageCleaner.Mode.JUNK).files;
        assertThrows(IllegalArgumentException.class, () -> SharedStorageCleaner.chooseKeeper(junk, junk.get(0)));
    }

    @Test public void coverageSeparatesReadableProtectedAndUnavailableLocations() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        Path unavailable = temporary.newFolder("card").toPath();
        write(root, "Download/item.tmp", "junk");
        write(root, "Download/keep.txt", "normal");
        write(root, "Android/data/example/private.tmp", "private");
        List<SharedStorageCleaner.Volume> volumes = Arrays.asList(volume(root), volume(unavailable));
        Files.delete(unavailable);
        SharedStorageCleaner.ScanResult result = SharedStorageCleaner.scan(volumes, SharedStorageCleaner.Mode.JUNK, () -> {});
        assertEquals(2L, result.scannedFiles);
        assertEquals(3L, result.scannedFolders);
        assertEquals(Collections.singletonList("Storage"), result.volumeLabels);
        assertEquals(1, result.inaccessibleVolumes);
        assertEquals(1, result.protectedFolders);
        assertEquals(1, result.skippedFolders);
        assertEquals(1, result.files.size());
    }

    @Test public void progressReportsStagesActualCountsAndComparisonWork() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        String body = "identical content";
        write(root, "a.txt", body);
        write(root, "b.txt", body);
        List<SharedStorageCleaner.ScanProgress> updates = new ArrayList<>();
        SharedStorageCleaner.ScanResult result = SharedStorageCleaner.scan(Collections.singletonList(volume(root)),
                SharedStorageCleaner.Mode.DUPLICATES, () -> {}, updates::add);
        assertEquals(SharedStorageCleaner.Stage.DISCOVERING, updates.get(0).stage);
        SharedStorageCleaner.ScanProgress last = updates.get(updates.size() - 1);
        assertEquals(SharedStorageCleaner.Stage.COMPARING, last.stage);
        assertEquals(2L, last.filesVisited);
        assertEquals(1L, last.foldersVisited);
        assertEquals(4L * body.length(), last.bytesCompared);
        assertTrue(last.currentPath.startsWith("Storage/"));
        assertEquals(result.scannedFiles, last.filesVisited);
        for (int i = 1; i < updates.size(); i++) {
            assertTrue(updates.get(i).filesVisited >= updates.get(i - 1).filesVisited);
            assertTrue(updates.get(i).bytesCompared >= updates.get(i - 1).bytesCompared);
        }
        AtomicInteger cancel = new AtomicInteger();
        assertThrows(CancellationException.class, () -> SharedStorageCleaner.scan(Collections.singletonList(volume(root)),
                SharedStorageCleaner.Mode.DUPLICATES, () -> {
                    if (cancel.get() > 0) throw new CancellationException();
                }, progress -> {
                    if (progress.stage == SharedStorageCleaner.Stage.COMPARING) cancel.incrementAndGet();
                }));
        assertTrue(Files.exists(root.resolve("a.txt")));
        assertTrue(Files.exists(root.resolve("b.txt")));
    }

    @Test public void aliasesOfAnExtraCopyAreNotCountedAsReclaimableCopies() throws Exception {
        Path root = temporary.newFolder("phone").toPath();
        write(root, "a.txt", "same bytes");
        Path copy = write(root, "b.txt", "same bytes");
        try {
            Files.createLink(root.resolve("c.txt"), copy);
        } catch (IOException | UnsupportedOperationException unavailable) {
            assumeNoException(unavailable);
        }
        assertTrue(scan(root, SharedStorageCleaner.Mode.DUPLICATES).files.isEmpty());
        assertTrue(Files.exists(copy));
        assertTrue(Files.exists(root.resolve("c.txt")));
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
