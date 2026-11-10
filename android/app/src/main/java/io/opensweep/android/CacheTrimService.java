package io.opensweep.android;

import android.content.Context;
import android.os.Binder;
import android.os.RemoteException;

import java.io.File;
import java.io.IOException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/** A short-lived Shizuku user service with exactly one allowlisted maintenance operation. */
public final class CacheTrimService extends ICacheTrimService.Stub {
    static final int COMPLETED = 0;
    static final int FAILED = 1;
    static final int TIMED_OUT = 2;
    private final int ownerUid;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final ScheduledExecutorService lifetime = Executors.newSingleThreadScheduledExecutor();
    private final AtomicBoolean started = new AtomicBoolean();
    private volatile Process command;

    /** Shizuku 13+ supplies our package context, which identifies the only permitted caller. */
    public CacheTrimService(Context context) {
        ownerUid = context.getApplicationInfo().uid;
        // A disconnected client must not leave an elevated process behind indefinitely.
        lifetime.schedule(this::stopProcess, 120, TimeUnit.SECONDS);
    }

    @Override
    public void trimCaches(ICacheTrimCallback callback) {
        if (Binder.getCallingUid() != ownerUid) throw new SecurityException("Unexpected caller");
        if (callback == null) return;
        if (!started.compareAndSet(false, true)) {
            reply(callback, FAILED);
            return;
        }
        worker.execute(() -> {
            int outcome = FAILED;
            try {
                // No arbitrary command or package/path can be supplied by the caller. This is
                // cache-only system maintenance; NEVER substitute the app-data-erasing 'clear'.
                Process process = new ProcessBuilder("/system/bin/cmd", "package", "trim-caches",
                        Long.toString(Long.MAX_VALUE), "internal")
                        .redirectErrorStream(true)
                        .redirectOutput(new File("/dev/null"))
                        .start();
                command = process;
                if (!process.waitFor(90, TimeUnit.SECONDS)) {
                    process.destroyForcibly();
                    outcome = TIMED_OUT;
                } else if (process.exitValue() == 0) {
                    // Android's command does not expose how much cache was actually removed.
                    outcome = COMPLETED;
                }
            } catch (IOException | RuntimeException unavailable) {
                outcome = FAILED;
            } catch (InterruptedException interrupted) {
                Thread.currentThread().interrupt();
                outcome = TIMED_OUT;
            } finally {
                Process process = command;
                if (process != null && process.isAlive()) process.destroyForcibly();
                command = null;
            }
            reply(callback, outcome);
        });
    }

    private static void reply(ICacheTrimCallback callback, int outcome) {
        try {
            callback.onFinished(outcome);
        } catch (RemoteException ignored) {
            // The app may have exited; the service watchdog still bounds its lifetime.
        }
    }

    @Override
    public void destroy() {
        int caller = Binder.getCallingUid();
        if (caller != ownerUid && caller != android.os.Process.myUid())
            throw new SecurityException("Unexpected caller");
        stopProcess();
    }

    private void stopProcess() {
        Process process = command;
        if (process != null) process.destroyForcibly();
        worker.shutdownNow();
        lifetime.shutdownNow();
        // This class only runs in Shizuku's separate, dedicated user-service process.
        System.exit(0);
    }
}
