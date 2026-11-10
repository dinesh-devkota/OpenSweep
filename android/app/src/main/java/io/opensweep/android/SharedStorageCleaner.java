package io.opensweep.android;

import android.content.Context;
import android.os.Build;
import android.os.CancellationSignal;
import android.os.Environment;
import android.os.OperationCanceledException;
import android.os.storage.StorageManager;
import android.os.storage.StorageVolume;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.file.DirectoryIteratorException;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.SecureDirectoryStream;
import java.nio.file.attribute.BasicFileAttributeView;
import java.nio.file.attribute.BasicFileAttributes;
import java.nio.file.attribute.FileTime;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.CancellationException;

/** Scans mounted shared-storage volumes without traversing links or private app directories. */
public final class SharedStorageCleaner {
    public enum Mode { JUNK, LARGE_FILES, DUPLICATES }
    public static final long LARGE_FILE_BYTES = 100L * 1024 * 1024;

    private SharedStorageCleaner() {}

    public static final class Candidate {
        public final String name;
        public final String path;
        public final long size;
        public final String duplicateGroup;
        public final boolean keeper;
        private final Snapshot original;
        private final Snapshot preservedCopy;

        private Candidate(Snapshot original, String duplicateGroup, Snapshot preservedCopy) {
            this.original = original;
            this.preservedCopy = preservedCopy;
            this.duplicateGroup = duplicateGroup;
            name = original.path.getFileName().toString();
            path = original.parent.volume.label + "/" + original.parent.volume.path.relativize(original.path);
            size = original.size;
            keeper = original == preservedCopy;
        }
    }

    public static final class ScanResult {
        public final List<Candidate> files;
        public final int skippedFolders;
        public final int skippedFiles;

        private ScanResult(List<Candidate> files, int skippedFolders, int skippedFiles) {
            this.files = Collections.unmodifiableList(files);
            this.skippedFolders = skippedFolders;
            this.skippedFiles = skippedFiles;
        }
    }

    static final class Volume {
        final String id;
        final String label;
        final Path path;
        final Object key;

        Volume(String id, String label, Path path) throws IOException {
            this.id = id;
            this.label = label;
            // Android may return /sdcard aliases. Only platform-provided roots are resolved here.
            this.path = path.toRealPath();
            BasicFileAttributes attributes = attributes(this.path);
            if (!attributes.isDirectory() || attributes.fileKey() == null) {
                throw new IOException("Storage identity is unavailable.");
            }
            key = attributes.fileKey();
        }

        boolean matches(Volume other) {
            return id.equals(other.id) && path.equals(other.path) && key.equals(other.key);
        }
    }

    private static final class Folder {
        final Volume volume;
        final Folder parent;
        final Path path;
        final Object key;

        Folder(Volume volume) {
            this.volume = volume;
            parent = null;
            path = volume.path;
            key = volume.key;
        }

        Folder(Folder parent, Path path, Object key) {
            volume = parent.volume;
            this.parent = parent;
            this.path = path;
            this.key = key;
        }

        boolean matches() throws IOException {
            for (Folder folder = this; folder != null; folder = folder.parent) {
                BasicFileAttributes current = attributes(folder.path);
                if (!current.isDirectory() || !Objects.equals(folder.key, current.fileKey())) return false;
            }
            return true;
        }
    }

    private static final class Snapshot {
        final Folder parent;
        final Path path;
        final Object key;
        final long size;
        final FileTime modified;

        Snapshot(Folder parent, Path path, BasicFileAttributes attributes) {
            this.parent = parent;
            this.path = path;
            key = attributes.fileKey();
            size = attributes.size();
            modified = attributes.lastModifiedTime();
        }

        boolean matches(BasicFileAttributes current) {
            return current.isRegularFile() && key != null && key.equals(current.fileKey())
                    && size == current.size() && modified.equals(current.lastModifiedTime());
        }

        boolean matches() throws IOException {
            return allowed(parent.volume.path.relativize(path)) && parent.matches() && matches(attributes(path));
        }
    }

    public static ScanResult scan(Context context, Mode mode, CancellationSignal signal) throws IOException {
        return scan(discoverVolumes(context), mode, () -> {
            if (signal != null) signal.throwIfCanceled();
            if (Thread.currentThread().isInterrupted()) throw new OperationCanceledException();
        });
    }

    static ScanResult scan(List<Volume> volumes, Mode mode, Runnable checkCanceled) throws IOException {
        if (volumes.isEmpty()) throw new IOException("Shared storage is unavailable.");
        ArrayDeque<Folder> pending = new ArrayDeque<>();
        for (Volume volume : volumes) pending.add(new Folder(volume));
        List<Snapshot> matches = new ArrayList<>();
        int skippedFolders = 0;
        int skippedFiles = 0;
        int readableFolders = 0;

        while (!pending.isEmpty()) {
            checkCanceled.run();
            Folder folder = pending.removeFirst();
            try {
                if (!folder.matches()) {
                    skippedFolders++;
                    continue;
                }
                try (DirectoryStream<Path> children = Files.newDirectoryStream(folder.path)) {
                    readableFolders++;
                    for (Path path : children) {
                        checkCanceled.run();
                        if (!allowed(folder.volume.path.relativize(path))) continue;
                        try {
                            BasicFileAttributes attributes = attributes(path);
                            if (attributes.isDirectory() && attributes.fileKey() != null) {
                                pending.add(new Folder(folder, path, attributes.fileKey()));
                            } else if (attributes.isRegularFile() && attributes.fileKey() != null
                                    && included(path.getFileName().toString(), attributes.size(), mode)) {
                                matches.add(new Snapshot(folder, path, attributes));
                            }
                        } catch (IOException | SecurityException unavailable) {
                            skippedFiles++;
                        }
                    }
                }
            } catch (IOException | DirectoryIteratorException | SecurityException unavailable) {
                skippedFolders++;
            }
        }
        if (readableFolders == 0) throw new IOException("Shared storage could not be read.");

        List<Candidate> files = new ArrayList<>();
        matches.sort(Comparator.comparing(file -> file.path.toString()));
        if (mode == Mode.DUPLICATES) {
            Map<Long, List<Snapshot>> bySize = new HashMap<>();
            for (Snapshot file : matches) {
                checkCanceled.run();
                bySize.computeIfAbsent(file.size, ignored -> new ArrayList<>()).add(file);
            }
            for (List<Snapshot> sameSize : bySize.values()) {
                checkCanceled.run();
                if (sameSize.size() < 2) continue;
                Map<String, List<Snapshot>> byHash = new LinkedHashMap<>();
                for (Snapshot file : sameSize) {
                    try {
                        String hash = hash(file, checkCanceled);
                        byHash.computeIfAbsent(hash, ignored -> new ArrayList<>()).add(file);
                    } catch (IOException | SecurityException unavailable) {
                        skippedFiles++;
                    }
                }
                for (Map.Entry<String, List<Snapshot>> group : byHash.entrySet()) {
                    if (group.getValue().size() < 2) continue;
                    Snapshot keeper = group.getValue().get(0);
                    List<Snapshot> identical = new ArrayList<>();
                    identical.add(keeper);
                    for (int index = 1; index < group.getValue().size(); index++) {
                        Snapshot copy = group.getValue().get(index);
                        try {
                            if (!keeper.key.equals(copy.key) && sameBytes(keeper, copy, checkCanceled)) identical.add(copy);
                        } catch (IOException | SecurityException unavailable) {
                            skippedFiles++;
                        }
                    }
                    if (identical.size() > 1) {
                        for (Snapshot file : identical) files.add(new Candidate(file, group.getKey(), keeper));
                    }
                }
            }
            files.sort(Comparator.comparing((Candidate file) -> file.duplicateGroup).thenComparing(file -> file.path));
        } else {
            for (Snapshot file : matches) files.add(new Candidate(file, null, null));
            files.sort(Comparator.comparingLong((Candidate file) -> file.size).reversed().thenComparing(file -> file.path));
        }
        checkCanceled.run();
        return new ScanResult(files, skippedFolders, skippedFiles);
    }

    static boolean included(String name, long size, Mode mode) {
        switch (mode) {
            case JUNK:
                return CleanupRules.isJunkName(name);
            case LARGE_FILES:
                return size >= LARGE_FILE_BYTES;
            case DUPLICATES:
                return size > 0;
            default:
                throw new IllegalArgumentException("Unknown scan mode.");
        }
    }

    static boolean allowed(Path relative) {
        if (relative.isAbsolute() || relative.getNameCount() == 0 || relative.startsWith("..")) return false;
        for (Path part : relative) {
            if (part.toString().equals("..") || part.toString().equals(".")) return false;
        }
        String first = relative.getName(0).toString();
        if (first.equalsIgnoreCase(".android_secure")) return false;
        // Only the public media branch belongs to shared storage under Android/.
        return !first.equalsIgnoreCase("Android") || relative.getNameCount() == 1
                || relative.getName(1).toString().equalsIgnoreCase("media");
    }

    private static BasicFileAttributes attributes(Path path) throws IOException {
        return Files.readAttributes(path, BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS);
    }

    private static String hash(Snapshot file, Runnable checkCanceled) throws IOException {
        checkCanceled.run();
        if (!file.matches()) throw new IOException("File changed.");
        MessageDigest digest;
        try {
            digest = MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException impossible) {
            throw new IllegalStateException(impossible);
        }
        try (InputStream input = Files.newInputStream(file.path, LinkOption.NOFOLLOW_LINKS)) {
            byte[] buffer = new byte[64 * 1024];
            int count;
            while ((count = input.read(buffer)) != -1) {
                checkCanceled.run();
                digest.update(buffer, 0, count);
            }
        }
        if (!file.matches()) throw new IOException("File changed.");
        StringBuilder result = new StringBuilder(64);
        for (byte value : digest.digest()) {
            result.append(Character.forDigit((value >>> 4) & 15, 16));
            result.append(Character.forDigit(value & 15, 16));
        }
        return result.toString();
    }

    private static boolean sameBytes(Snapshot first, Snapshot second, Runnable checkCanceled) throws IOException {
        if (!first.matches() || !second.matches()) return false;
        try (InputStream left = Files.newInputStream(first.path, LinkOption.NOFOLLOW_LINKS);
             InputStream right = Files.newInputStream(second.path, LinkOption.NOFOLLOW_LINKS)) {
            byte[] a = new byte[64 * 1024];
            byte[] b = new byte[64 * 1024];
            int count;
            while ((count = left.read(a)) != -1) {
                checkCanceled.run();
                int received = 0;
                while (received < count) {
                    int read = right.read(b, received, count - received);
                    if (read == -1) return false;
                    received += read;
                }
                for (int index = 0; index < count; index++) if (a[index] != b[index]) return false;
            }
            if (right.read() != -1) return false;
        }
        return first.matches() && second.matches();
    }

    public static boolean delete(Context context, Candidate file) {
        try {
            return delete(discoverVolumes(context), file);
        } catch (IOException | SecurityException unavailable) {
            return false;
        }
    }

    static synchronized boolean delete(List<Volume> volumes, Candidate file) {
        if (file.keeper || Thread.currentThread().isInterrupted()) return false;
        Snapshot original = file.original;
        try {
            if (!mounted(volumes, original) || !original.matches()) return false;
            if (file.duplicateGroup != null) {
                Snapshot keeper = file.preservedCopy;
                if (!mounted(volumes, keeper) || !file.duplicateGroup.equals(hash(keeper, SharedStorageCleaner::checkInterrupted))
                        || !file.duplicateGroup.equals(hash(original, SharedStorageCleaner::checkInterrupted))
                        || !sameBytes(keeper, original, SharedStorageCleaner::checkInterrupted)) return false;
            }
            try (DirectoryStream<Path> parent = Files.newDirectoryStream(original.path.getParent())) {
                if (!original.matches()) return false;
                // Android's Unix provider supports descriptor-relative operations. Refuse a provider
                // without them, since path-based delete could remove a substituted empty directory.
                if (!(parent instanceof SecureDirectoryStream)) return false;
                SecureDirectoryStream<Path> secure = (SecureDirectoryStream<Path>) parent;
                BasicFileAttributes folder = secure.getFileAttributeView(BasicFileAttributeView.class).readAttributes();
                if (!Objects.equals(original.parent.key, folder.fileKey())) return false;
                BasicFileAttributes current = secure.getFileAttributeView(original.path.getFileName(),
                        BasicFileAttributeView.class, LinkOption.NOFOLLOW_LINKS).readAttributes();
                if (!original.matches(current)) return false;
                checkInterrupted();
                secure.deleteFile(original.path.getFileName());
            }
            return true;
        } catch (IOException | SecurityException | CancellationException unavailable) {
            return false;
        }
    }

    private static void checkInterrupted() {
        if (Thread.currentThread().isInterrupted()) throw new CancellationException();
    }

    private static boolean mounted(List<Volume> volumes, Snapshot file) {
        for (Volume volume : volumes) if (volume.matches(file.parent.volume)) return true;
        return false;
    }

    private static List<Volume> discoverVolumes(Context context) throws IOException {
        StorageManager manager = context.getSystemService(StorageManager.class);
        Map<Path, Volume> volumes = new LinkedHashMap<>();
        if (manager == null) throw new IOException("Storage manager is unavailable.");
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            for (StorageVolume volume : manager.getStorageVolumes()) {
                if (mounted(volume.getState()) && volume.getDirectory() != null) {
                    addVolume(volumes, volume, volume.getDirectory(), context);
                }
            }
        } else {
            File primary = Environment.getExternalStorageDirectory();
            if (mounted(Environment.getExternalStorageState(primary))) {
                addVolume(volumes, manager.getStorageVolume(primary), primary, context);
            }
            // Before API 30, public StorageVolume has no directory accessor. Platform-provided
            // app directories identify each mounted volume without assuming /storage UUID paths.
            for (File files : context.getExternalFilesDirs(null)) {
                if (files == null || !mounted(Environment.getExternalStorageState(files))) continue;
                File app = files.getParentFile();
                File data = app == null ? null : app.getParentFile();
                File android = data == null ? null : data.getParentFile();
                if (android != null && data.getName().equals("data") && android.getName().equals("Android")) {
                    addVolume(volumes, manager.getStorageVolume(files), android.getParentFile(), context);
                }
            }
        }
        return new ArrayList<>(volumes.values());
    }

    private static boolean mounted(String state) {
        return Environment.MEDIA_MOUNTED.equals(state) || Environment.MEDIA_MOUNTED_READ_ONLY.equals(state);
    }

    private static void addVolume(Map<Path, Volume> volumes, StorageVolume volume, File directory, Context context) {
        if (volume == null || directory == null) return;
        try {
            String id = volume.isPrimary() ? "primary" : volume.getUuid();
            if (id == null) id = directory.getName();
            Volume found = new Volume(id, volume.getDescription(context), directory.toPath());
            volumes.putIfAbsent(found.path, found);
        } catch (IOException | SecurityException unavailable) {
            // Unmounted and inaccessible volumes are omitted. Other mounted volumes are still scanned.
        }
    }
}
