package io.opensweep.android;

import android.content.Context;
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
    }

    boolean working() {
        return phase == Phase.SCANNING || phase == Phase.DELETING;
    }

    void scan(SharedStorageCleaner.Mode chosenMode) {
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
        skippedFolders = 0;
        skippedFiles = 0;
        CancellationSignal signal = new CancellationSignal();
        scanSignal = signal;
        notifyChanged();
        worker.execute(() -> {
            try {
                SharedStorageCleaner.ScanResult result = SharedStorageCleaner.scan(context, chosenMode, signal);
                main.post(() -> {
                    if (signal.isCanceled()) {
                        finishScan(Phase.CANCELED);
                        return;
                    }
                    files = result.files;
                    skippedFolders = result.skippedFolders;
                    skippedFiles = result.skippedFiles;
                    for (SharedStorageCleaner.Candidate file : files) {
                        if (file.duplicateGroup != null && !groups.containsKey(file.duplicateGroup))
                            groups.put(file.duplicateGroup, groups.size() + 1);
                    }
                    finishScan(Phase.REVIEW);
                });
            } catch (OperationCanceledException canceled) {
                main.post(() -> finishScan(Phase.CANCELED));
            } catch (IOException | RuntimeException inaccessible) {
                main.post(() -> finishScan(signal.isCanceled() ? Phase.CANCELED : Phase.ERROR));
            }
        });
    }

    private void finishScan(Phase next) {
        scanSignal = null;
        phase = next;
        notifyChanged();
    }

    void cancelScan() {
        if (scanSignal != null) scanSignal.cancel();
    }

    void deleteSelection() {
        if (phase != Phase.REVIEW || selected.isEmpty()) return;
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
            for (int index = 0; index < confirmed.size(); index++) {
                if (Thread.currentThread().isInterrupted()) break;
                SharedStorageCleaner.Candidate file = confirmed.get(index);
                if (SharedStorageCleaner.delete(context, file)) {
                    deleted++;
                    bytes += Math.max(0, file.size);
                }
                int completed = index + 1;
                if (completed % 20 == 0 || completed == confirmed.size()) {
                    main.post(() -> {
                        processed = completed;
                        notifyChanged();
                    });
                }
            }
            int deletedCount = deleted;
            long deletedBytes = bytes;
            AndroidTools.DeviceStorage currentStorage = AndroidTools.storage(context);
            main.post(() -> {
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
                    apps = result;
                    finishLoadingApps();
                });
            } catch (RuntimeException unavailable) {
                main.post(() -> {
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
        appsRefreshPending = false;
        onChanged = null;
        cancelScan();
        worker.shutdownNow();
    }
}
