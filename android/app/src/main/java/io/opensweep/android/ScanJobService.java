package io.opensweep.android;

import android.Manifest;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.job.JobParameters;
import android.app.job.JobService;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.CancellationSignal;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;

import java.io.IOException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class ScanJobService extends JobService {
    private static final int NOTIFICATION_ID = 1401;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private CancellationSignal active;

    @Override
    public boolean onStartJob(JobParameters params) {
        if (!DailyScanScheduler.isEnabled(this) || !hasStorageAccess()
                || !DailyScanScheduler.canNotify(this)) return false;
        CancellationSignal signal = new CancellationSignal();
        active = signal;
        worker.execute(() -> {
            int count;
            try {
                count = SharedStorageCleaner.scan(this, SharedStorageCleaner.Mode.JUNK, signal).files.size();
            } catch (IOException | RuntimeException unavailable) {
                count = 0;
            }
            final int found = count;
            main.post(() -> {
                if (active != signal) return;
                active = null;
                try {
                    if (!signal.isCanceled() && DailyScanScheduler.isEnabled(this) && found > 0) {
                        notifyReview(found);
                    }
                } catch (RuntimeException unavailable) {
                    // Notification access can change while the scan runs.
                } finally {
                    jobFinished(params, false);
                }
            });
        });
        return true;
    }

    private boolean hasStorageAccess() {
        if (Build.VERSION.SDK_INT >= 30) return Environment.isExternalStorageManager();
        return checkSelfPermission(Manifest.permission.READ_EXTERNAL_STORAGE)
                == PackageManager.PERMISSION_GRANTED;
    }

    private void notifyReview(int found) {
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager == null || !DailyScanScheduler.canNotify(this)) return;
        manager.createNotificationChannel(new NotificationChannel(DailyScanScheduler.CHANNEL_ID,
                getString(R.string.scan_notification_channel), NotificationManager.IMPORTANCE_DEFAULT));
        Intent open = new Intent(this, MainActivity.class)
                .putExtra("scan_mode", "JUNK")
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        PendingIntent review = PendingIntent.getActivity(this, NOTIFICATION_ID, open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Notification notification = new Notification.Builder(this, DailyScanScheduler.CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_menu_search)
                .setContentTitle(getString(R.string.scan_notification_title))
                .setContentText(getResources().getQuantityString(
                        R.plurals.scan_notification_files, found, found))
                .setContentIntent(review)
                .setAutoCancel(true)
                .build();
        manager.notify(NOTIFICATION_ID, notification);
    }

    @Override
    public boolean onStopJob(JobParameters params) {
        if (active != null) active.cancel();
        active = null;
        return DailyScanScheduler.isEnabled(this);
    }

    @Override
    public void onDestroy() {
        if (active != null) active.cancel();
        active = null;
        worker.shutdownNow();
        super.onDestroy();
    }
}
