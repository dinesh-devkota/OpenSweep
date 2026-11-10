package io.opensweep.android;

import android.Manifest;
import android.app.AppOpsManager;
import android.app.usage.StorageStats;
import android.app.usage.StorageStatsManager;
import android.app.usage.UsageStats;
import android.app.usage.UsageStatsManager;
import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.os.OperationCanceledException;
import android.os.Process;

import java.io.File;
import java.io.IOException;
import java.text.Collator;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

/** Read-only inventory for the current Android user. Call load on a background thread. */
public final class InstalledApps {
    public static final int USAGE_WINDOW_DAYS = 90;

    private InstalledApps() {}

    public static final class Entry {
        public final String label;
        public final String packageName;
        public final boolean system;
        public final long lastUpdatedMs;
        /** Actual base and split APK file sizes. -1 means unavailable. */
        public final long apkBytes;
        /** Android's app footprint, including compiled code and native libraries. */
        public final long appBytes;
        /** All app data, including cache. Use appBytes + dataBytes for the total. */
        public final long dataBytes;
        public final long cacheBytes;
        /** Last observed use in the 90-day window. -1 means unavailable. */
        public final long lastUsedMs;

        private Entry(PackageInfo info, String label, long apkBytes, StorageStats storage, long lastUsedMs) {
            this.label = label;
            packageName = info.packageName;
            system = (info.applicationInfo.flags
                    & (ApplicationInfo.FLAG_SYSTEM | ApplicationInfo.FLAG_UPDATED_SYSTEM_APP)) != 0;
            lastUpdatedMs = info.lastUpdateTime > 0 ? info.lastUpdateTime : -1;
            this.apkBytes = apkBytes;
            appBytes = storage == null ? -1 : storage.getAppBytes();
            dataBytes = storage == null ? -1 : storage.getDataBytes();
            cacheBytes = storage == null ? -1 : storage.getCacheBytes();
            this.lastUsedMs = lastUsedMs;
        }
    }

    public static List<Entry> load(Context context) {
        PackageManager packages = context.getPackageManager();
        boolean usageAccess = hasUsageAccess(context);
        Map<String, UsageStats> usage = usageAccess ? readUsage(context) : Collections.emptyMap();
        StorageStatsManager storage = usageAccess ? context.getSystemService(StorageStatsManager.class) : null;
        long now = System.currentTimeMillis();
        long earliestUse = now - TimeUnit.DAYS.toMillis(USAGE_WINDOW_DAYS);
        List<Entry> entries = new ArrayList<>();
        for (PackageInfo info : packages.getInstalledPackages(0)) {
            if (Thread.currentThread().isInterrupted()) throw new OperationCanceledException();
            if (info.applicationInfo == null) continue;
            String label = info.packageName;
            try {
                CharSequence loaded = packages.getApplicationLabel(info.applicationInfo);
                if (loaded != null && loaded.length() > 0) label = loaded.toString();
            } catch (RuntimeException unavailable) {
                // An app can disappear while the package inventory is loading.
            }
            long lastUsed = -1;
            UsageStats observed = usage.get(info.packageName);
            if (observed != null && observed.getLastTimeUsed() >= earliestUse
                    && observed.getLastTimeUsed() <= now) {
                lastUsed = observed.getLastTimeUsed();
            }
            entries.add(new Entry(info, label, apkSize(info.applicationInfo),
                    readStorage(storage, info), lastUsed));
        }
        entries.sort(Comparator.comparing((Entry entry) -> entry.label, Collator.getInstance())
                .thenComparing(entry -> entry.packageName));
        return Collections.unmodifiableList(entries);
    }

    public static boolean hasUsageAccess(Context context) {
        AppOpsManager operations = context.getSystemService(AppOpsManager.class);
        if (operations == null) return false;
        try {
            int mode = operations.checkOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS,
                    Process.myUid(), context.getPackageName());
            return mode == AppOpsManager.MODE_ALLOWED
                    || (mode == AppOpsManager.MODE_DEFAULT
                    && context.checkSelfPermission(Manifest.permission.PACKAGE_USAGE_STATS)
                    == PackageManager.PERMISSION_GRANTED);
        } catch (RuntimeException unavailable) {
            return false;
        }
    }

    private static Map<String, UsageStats> readUsage(Context context) {
        UsageStatsManager usage = context.getSystemService(UsageStatsManager.class);
        if (usage == null) return Collections.emptyMap();
        try {
            long now = System.currentTimeMillis();
            Map<String, UsageStats> result = usage.queryAndAggregateUsageStats(
                    now - TimeUnit.DAYS.toMillis(USAGE_WINDOW_DAYS), now);
            return result == null ? Collections.emptyMap() : result;
        } catch (RuntimeException unavailable) {
            return Collections.emptyMap();
        }
    }

    private static StorageStats readStorage(StorageStatsManager storage, PackageInfo info) {
        if (storage == null || info.applicationInfo.storageUuid == null) return null;
        try {
            return storage.queryStatsForPackage(info.applicationInfo.storageUuid,
                    info.packageName, Process.myUserHandle());
        } catch (IOException | PackageManager.NameNotFoundException | RuntimeException unavailable) {
            // Permission can be revoked, an app uninstalled, or a storage volume removed during loading.
            return null;
        }
    }

    private static long apkSize(ApplicationInfo info) {
        try {
            long size = fileSize(info.sourceDir);
            if (size < 0) return -1;
            if (info.splitSourceDirs != null) {
                for (String split : info.splitSourceDirs) {
                    long bytes = fileSize(split);
                    if (bytes < 0) return -1;
                    size = Math.addExact(size, bytes);
                }
            }
            return size;
        } catch (RuntimeException unavailable) {
            return -1;
        }
    }

    private static long fileSize(String path) {
        if (path == null) return -1;
        File file = new File(path);
        return file.isFile() && file.canRead() ? file.length() : -1;
    }
}
