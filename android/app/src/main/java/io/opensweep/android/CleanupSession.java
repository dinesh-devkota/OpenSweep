package io.opensweep.android;

import android.content.Context;
import android.media.MediaScannerConnection;
import android.os.CancellationSignal;
import android.os.Handler;
import android.os.Looper;
import android.os.OperationCanceledException;

import java.io.IOException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Keeps work and review selections alive across Activity recreation. */
final class CleanupSession {
    enum Phase { START, SCANNING, REVIEW, CANCELED, ERROR, DELETING, FINISHED }
    enum Page { STORAGE, SCAN, APPS, DEVICE }

    private final Context context;
    final ShizukuCleaner shizuku;
    private boolean shizukuWasBusy;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private CancellationSignal scanSignal;
    private boolean closed;
    Runnable onChanged;
    Page page = Page.STORAGE;
    Phase phase = Phase.START;
    SharedStorageCleaner.Mode mode = SharedStorageCleaner.Mode.JUNK;
    SharedStorageCleaner.Mode pendingMode;
    boolean pendingDaily;
    boolean pendingCache;
    boolean waitingForStorage;
    boolean waitingForNotifications;
    List<SharedStorageCleaner.Candidate> files = Collections.emptyList();
    final Set<SharedStorageCleaner.Candidate> selected = new HashSet<>();
    final Map<String, Integer> groups = new LinkedHashMap<>();
    SharedStorageCleaner.ScanProgress scanProgress;
    long scannedFiles;
    long scannedFolders;
    List<String> volumeLabels = Collections.emptyList();
    int inaccessibleVolumes;
    int protectedFolders;
    int extraCopies;
    long reclaimableBytes;
    int skippedFolders;
    int skippedFiles;
    int requested;
    int processed;
    int removed;
    long removedBytes;
    AndroidTools.DeviceStorage storage;
    List<InstalledApps.Entry> apps = Collections.emptyList();
    boolean appsLoading;
    private boolean appsRefreshPending;
    boolean appsError;
    boolean showSystem;
    String appQuery = "";

    CleanupSession(Context context) {
        this.context = context.getApplicationContext();
        shizuku = new ShizukuCleaner(this.context);
        shizuku.setListener(() -> {
            if (closed) return;
            boolean busy = shizuku.isBusy();
            if (shizukuWasBusy && !busy) {
                refreshStorage();
                refreshApps();
            }
            shizukuWasBusy = busy;
            notifyChanged();
        });
    }

    boolean working() {
        return phase == Phase.SCANNING || phase == Phase.DELETING;
    }

    void scan(SharedStorageCleaner.Mode chosenMode) {
        if (closed) return;
        page = Page.SCAN;
        if (working()) {
            notifyChanged();
            return;
        }
        mode = chosenMode;
        phase = Phase.SCANNING;
        files = Collections.emptyList();
        selected.clear();
        groups.clear();
        scanProgress = null;
        scannedFiles = 0;
        scannedFolders = 0;
        volumeLabels = Collections.emptyList();
        inaccessibleVolumes = 0;
        protectedFolders = 0;
        extraCopies = 0;
        reclaimableBytes = 0;
        skippedFolders = 0;
        skippedFiles = 0;
        CancellationSignal signal = new CancellationSignal();
        scanSignal = signal;
        notifyChanged();
        worker.execute(() -> {
            try {
                SharedStorageCleaner.ScanResult result = SharedStorageCleaner.scan(context, chosenMode, signal,
                        progress -> main.post(() -> {
                            if (closed || scanSignal != signal || signal.isCanceled()) return;
                            scanProgress = progress;
                            notifyChanged();
                        }));
                main.post(() -> {
                    if (closed || scanSignal != signal) return;
                    if (signal.isCanceled()) {
                        finishScan(signal, Phase.CANCELED);
                        return;
                    }
                    files = result.files;
                    skippedFolders = result.skippedFolders;
                    skippedFiles = result.skippedFiles;
                    scannedFiles = result.scannedFiles;
                    scannedFolders = result.scannedFolders;
                    volumeLabels = result.volumeLabels;
                    inaccessibleVolumes = result.inaccessibleVolumes;
                    protectedFolders = result.protectedFolders;
                    updateDuplicateSummary();
                    finishScan(signal, Phase.REVIEW);
                });
            } catch (OperationCanceledException canceled) {
                main.post(() -> finishScan(signal, Phase.CANCELED));
            } catch (IOException | RuntimeException inaccessible) {
                main.post(() -> finishScan(signal, signal.isCanceled() ? Phase.CANCELED : Phase.ERROR));
            }
        });
    }

    private void finishScan(CancellationSignal signal, Phase next) {
        if (closed || scanSignal != signal) return;
        scanSignal = null;
        phase = next;
        notifyChanged();
    }

    private void updateDuplicateSummary() {
        groups.clear();
        extraCopies = 0;
        reclaimableBytes = 0;
        for (SharedStorageCleaner.Candidate file : files) {
            if (file.duplicateGroup == null) continue;
            if (!groups.containsKey(file.duplicateGroup)) groups.put(file.duplicateGroup, groups.size() + 1);
            if (!file.keeper) {
                extraCopies++;
                reclaimableBytes += Math.max(0, file.size);
            }
        }
    }

    void selectExtraCopies() {
        if (closed || phase != Phase.REVIEW || mode != SharedStorageCleaner.Mode.DUPLICATES) return;
        selected.clear();
        for (SharedStorageCleaner.Candidate file : files) {
            if (file.duplicateGroup != null && !file.keeper) selected.add(file);
        }
        notifyChanged();
    }

    boolean setKeeper(SharedStorageCleaner.Candidate chosen) {
        if (closed || phase != Phase.REVIEW || mode != SharedStorageCleaner.Mode.DUPLICATES
                || chosen == null || chosen.duplicateGroup == null || !files.contains(chosen)) return false;
        Set<String> selectedPaths = new HashSet<>();
        for (SharedStorageCleaner.Candidate file : selected) selectedPaths.add(file.absolutePath);
        List<SharedStorageCleaner.Candidate> updated;
        try {
            updated = SharedStorageCleaner.chooseKeeper(files, chosen);
        } catch (IllegalArgumentException invalid) {
            return false;
        }
        files = updated;
        selected.clear();
        for (SharedStorageCleaner.Candidate file : files) {
            if (!file.keeper && selectedPaths.contains(file.absolutePath)) selected.add(file);
        }
        updateDuplicateSummary();
        notifyChanged();
        return true;
    }

    void cancelScan() {
        if (scanSignal != null) scanSignal.cancel();
    }

    void deleteSelection() {
        if (closed || phase != Phase.REVIEW || selected.isEmpty()) return;
        List<SharedStorageCleaner.Candidate> confirmed = new ArrayList<>(selected);
        requested = confirmed.size();
        processed = 0;
        removed = 0;
        removedBytes = 0;
        phase = Phase.DELETING;
        files = Collections.emptyList();
        selected.clear();
        notifyChanged();
        worker.execute(() -> {
            int deleted = 0;
            long bytes = 0;
            List<String> deletedPaths = new ArrayList<>();
            for (int index = 0; index < confirmed.size(); index++) {
                if (Thread.currentThread().isInterrupted()) break;
                SharedStorageCleaner.Candidate file = confirmed.get(index);
                if (SharedStorageCleaner.delete(context, file)) {
                    deleted++;
                    bytes += Math.max(0, file.size);
                    deletedPaths.add(file.absolutePath);
                }
                int completed = index + 1;
                if (completed % 20 == 0 || completed == confirmed.size()) {
                    main.post(() -> {
                        if (closed) return;
                        processed = completed;
                        notifyChanged();
                    });
                }
            }
            // Ask Android to remove stale media records only for files actually removed.
            // Media-index refresh is best effort and never changes the deletion result.
            if (!deletedPaths.isEmpty()) {
                try {
                    MediaScannerConnection.scanFile(context, deletedPaths.toArray(new String[0]), null, null);
                } catch (RuntimeException unavailable) {
                    // Some providers/devices cannot refresh immediately; deletion still succeeded.
                }
            }
            int deletedCount = deleted;
            long deletedBytes = bytes;
            AndroidTools.DeviceStorage currentStorage = AndroidTools.storage(context);
            main.post(() -> {
                if (closed) return;
                removed = deletedCount;
                removedBytes = deletedBytes;
                storage = currentStorage;
                phase = Phase.FINISHED;
                notifyChanged();
            });
        });
    }

    void refreshStorage() {
        if (closed) return;
        worker.execute(() -> {
            AndroidTools.DeviceStorage result = AndroidTools.storage(context);
            main.post(() -> {
                if (closed) return;
                storage = result;
                notifyChanged();
            });
        });
    }

    void refreshApps() {
        if (closed) return;
        if (appsLoading) appsRefreshPending = true;
        else loadApps();
    }

    private void finishLoadingApps() {
        appsLoading = false;
        notifyChanged();
        if (!closed && appsRefreshPending) {
            appsRefreshPending = false;
            loadApps();
        }
    }

    void loadApps() {
        if (closed || appsLoading) return;
        appsLoading = true;
        appsError = false;
        notifyChanged();
        worker.execute(() -> {
            try {
                List<InstalledApps.Entry> result = InstalledApps.load(context);
                main.post(() -> {
                    if (closed) return;
                    apps = result;
                    finishLoadingApps();
                });
            } catch (RuntimeException unavailable) {
                main.post(() -> {
                    if (closed) return;
                    appsError = true;
                    finishLoadingApps();
                });
            }
        });
    }

    long selectedBytes() {
        return totalBytes(selected);
    }

    static long totalBytes(Iterable<SharedStorageCleaner.Candidate> items) {
        long bytes = 0;
        for (SharedStorageCleaner.Candidate file : items) bytes += Math.max(0, file.size);
        return bytes;
    }

    private void notifyChanged() {
        if (onChanged != null) onChanged.run();
    }

    void close() {
        closed = true;
        shizuku.close();
        appsRefreshPending = false;
        onChanged = null;
        cancelScan();
        worker.shutdownNow();
    }
}
