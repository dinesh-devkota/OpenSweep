package io.opensweep.android;

import android.content.ContentResolver;
import android.database.Cursor;
import android.net.Uri;
import android.os.CancellationSignal;
import android.os.Handler;
import android.os.Looper;
import android.os.OperationCanceledException;
import android.provider.DocumentsContract;
import android.provider.DocumentsContract.Document;

import java.io.IOException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Owns the operation across Activity recreation. Visible state is updated on the main thread. */
final class CleanupSession {
    enum Phase { START, SCANNING, REVIEW, CANCELED, ERROR, DELETING, FINISHED }

    private final ContentResolver resolver;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private CancellationSignal scanSignal;
    Runnable onChanged;

    Phase phase = Phase.START;
    Uri tree;
    String folderName;
    List<FolderCleaner.Candidate> files = Collections.emptyList();
    final Set<FolderCleaner.Candidate> selected = new HashSet<>();
    int skippedFolders;
    int requested;
    int removed;

    CleanupSession(ContentResolver resolver) {
        this.resolver = resolver;
    }

    void scan(Uri folder) {
        if (phase == Phase.SCANNING || phase == Phase.DELETING) return;
        if (!folder.equals(tree)) folderName = null;
        tree = folder;
        phase = Phase.SCANNING;
        files = Collections.emptyList();
        selected.clear();
        skippedFolders = 0;
        CancellationSignal signal = new CancellationSignal();
        scanSignal = signal;
        notifyChanged();
        worker.execute(() -> {
            try {
                String label = readFolderName(folder, signal);
                FolderCleaner.ScanResult result = FolderCleaner.scan(resolver, folder, signal);
                main.post(() -> {
                    if (signal.isCanceled()) {
                        finishScan(Phase.CANCELED);
                        return;
                    }
                    folderName = label;
                    files = result.files;
                    skippedFolders = result.skippedFolders;
                    finishScan(Phase.REVIEW);
                });
            } catch (OperationCanceledException canceled) {
                main.post(() -> finishScan(Phase.CANCELED));
            } catch (IOException | RuntimeException inaccessible) {
                main.post(() -> finishScan(signal.isCanceled() ? Phase.CANCELED : Phase.ERROR));
            }
        });
    }

    private String readFolderName(Uri folder, CancellationSignal signal) {
        Uri document = DocumentsContract.buildDocumentUriUsingTree(folder, DocumentsContract.getTreeDocumentId(folder));
        try (Cursor cursor = resolver.query(document, new String[]{Document.COLUMN_DISPLAY_NAME}, null, null, null, signal)) {
            if (cursor != null && cursor.moveToFirst()) return cursor.getString(0);
        } catch (RuntimeException unavailable) {
            signal.throwIfCanceled();
            // Some document providers can list children without returning a display name for the root.
        }
        return null;
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
        List<FolderCleaner.Candidate> confirmed = new ArrayList<>(selected);
        Uri folder = tree;
        requested = confirmed.size();
        removed = 0;
        phase = Phase.DELETING;
        files = Collections.emptyList();
        selected.clear();
        notifyChanged();
        worker.execute(() -> {
            int deleted = 0;
            for (FolderCleaner.Candidate file : confirmed) {
                if (Thread.currentThread().isInterrupted()) break;
                if (FolderCleaner.delete(resolver, folder, file)) deleted++;
            }
            int deletedCount = deleted;
            main.post(() -> {
                removed = deletedCount;
                phase = Phase.FINISHED;
                notifyChanged();
            });
        });
    }

    private void notifyChanged() {
        if (onChanged != null) onChanged.run();
    }

    void close() {
        onChanged = null;
        cancelScan();
        worker.shutdownNow();
    }
}
