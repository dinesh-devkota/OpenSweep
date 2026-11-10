package io.opensweep.android;

import android.Manifest;
import android.app.job.JobScheduler;
import android.app.NotificationManager;
import android.app.NotificationChannel;
import android.app.job.JobInfo;
import android.content.ComponentName;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;

import java.util.concurrent.TimeUnit;

/** An opt-in periodic scan that leaves cleanup decisions to the user. */
public final class DailyScanScheduler {
    private static final int JOB_ID = 1401;
    static final String CHANNEL_ID = "storage_review";
    private static final String PREFERENCES = "maintenance";
    private static final String ENABLED = "daily_scan";

    private DailyScanScheduler() {}

    public static boolean isEnabled(Context context) {
        return context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
                .getBoolean(ENABLED, false);
    }

    static boolean canNotify(Context context) {
        if (Build.VERSION.SDK_INT >= 33
                && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED) return false;
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        if (manager == null || !manager.areNotificationsEnabled()) return false;
        NotificationChannel channel = manager.getNotificationChannel(CHANNEL_ID);
        return channel == null || channel.getImportance() != NotificationManager.IMPORTANCE_NONE;
    }

    public static boolean setEnabled(Context context, boolean enabled) {
        JobScheduler scheduler = context.getSystemService(JobScheduler.class);
        if (scheduler == null) return false;
        if (!enabled) {
            context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
                    .edit().putBoolean(ENABLED, false).apply();
            scheduler.cancel(JOB_ID);
            return true;
        }
        if (!canNotify(context)) return false;
        JobInfo job = new JobInfo.Builder(JOB_ID, new ComponentName(context, ScanJobService.class))
                .setPeriodic(TimeUnit.DAYS.toMillis(1), TimeUnit.HOURS.toMillis(6))
                .setRequiresCharging(true)
                .setPersisted(true)
                .build();
        try {
            if (scheduler.schedule(job) != JobScheduler.RESULT_SUCCESS) return false;
        } catch (RuntimeException unavailable) {
            return false;
        }
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
                .edit().putBoolean(ENABLED, true).apply();
        return true;
    }
}
