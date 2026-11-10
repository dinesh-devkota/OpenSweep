package io.opensweep.android;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Environment;
import android.os.StatFs;
import android.provider.Settings;

/** Opens Android-owned controls in response to an explicit user action. */
public final class AndroidTools {
    private AndroidTools() {}

    public static final class DeviceStorage {
        public final long totalBytes;
        public final long availableBytes;

        private DeviceStorage(long totalBytes, long availableBytes) {
            this.totalBytes = totalBytes;
            this.availableBytes = availableBytes;
        }
    }

    /** Capacity of the internal data filesystem. -1 means unavailable. */
    public static DeviceStorage storage(Context context) {
        try {
            StatFs space = new StatFs(Environment.getDataDirectory().getAbsolutePath());
            return new DeviceStorage(space.getTotalBytes(), space.getAvailableBytes());
        } catch (RuntimeException unavailable) {
            return new DeviceStorage(-1, -1);
        }
    }

    public static boolean appInfo(Context context, String packageName) {
        return open(context, new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                Uri.fromParts("package", packageName, null)),
                new Intent(Settings.ACTION_MANAGE_APPLICATIONS_SETTINGS),
                new Intent(Settings.ACTION_SETTINGS));
    }

    public static boolean uninstall(Context context, InstalledApps.Entry app) {
        if (app.system) return false;
        // Android presents its own uninstall confirmation and enforces device policy.
        return open(context, new Intent(Intent.ACTION_DELETE,
                Uri.fromParts("package", app.packageName, null)));
    }

    public static boolean appStore(Context context, String packageName) {
        Uri market = new Uri.Builder().scheme("market").authority("details")
                .appendQueryParameter("id", packageName).build();
        try {
            String installer = context.getPackageManager().getInstallerPackageName(packageName);
            if (installer != null && open(context, new Intent(Intent.ACTION_VIEW, market).setPackage(installer))) {
                return true;
            }
        } catch (IllegalArgumentException | SecurityException unavailable) {
            // Sideloaded and removed apps can have no accessible installer.
        }
        Uri browser = new Uri.Builder().scheme("https").authority("play.google.com")
                .path("/store/apps/details").appendQueryParameter("id", packageName).build();
        return open(context, new Intent(Intent.ACTION_VIEW, market), new Intent(Intent.ACTION_VIEW, browser));
    }

    public static boolean usageAccess(Context context) {
        return open(context, new Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS,
                Uri.fromParts("package", context.getPackageName(), null)),
                new Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS),
                new Intent(Settings.ACTION_SETTINGS));
    }

    public static boolean storageSettings(Context context) {
        return open(context, new Intent(Settings.ACTION_INTERNAL_STORAGE_SETTINGS),
                new Intent(Settings.ACTION_SETTINGS));
    }

    public static boolean batterySettings(Context context) {
        return open(context, new Intent(Intent.ACTION_POWER_USAGE_SUMMARY),
                new Intent(Settings.ACTION_BATTERY_SAVER_SETTINGS),
                new Intent(Settings.ACTION_SETTINGS));
    }

    public static boolean securitySettings(Context context) {
        return open(context, new Intent(Settings.ACTION_SECURITY_SETTINGS),
                new Intent(Settings.ACTION_SETTINGS));
    }

    public static boolean systemUpdateSettings(Context context) {
        return open(context, new Intent("android.settings.SYSTEM_UPDATE_SETTINGS"),
                new Intent(Settings.ACTION_DEVICE_INFO_SETTINGS),
                new Intent(Settings.ACTION_SETTINGS));
    }

    private static boolean open(Context context, Intent... intents) {
        for (Intent intent : intents) {
            if (!(context instanceof Activity)) intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            try {
                context.startActivity(intent);
                return true;
            } catch (ActivityNotFoundException | SecurityException unavailable) {
                // Manufacturer Settings apps expose different entry points.
            }
        }
        return false;
    }
}
