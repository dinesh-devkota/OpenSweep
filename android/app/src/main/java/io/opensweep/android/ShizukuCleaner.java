package io.opensweep.android;

import android.content.ComponentName;
import android.content.Context;
import android.content.ServiceConnection;
import android.content.pm.PackageManager;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.RemoteException;

import rikka.shizuku.Shizuku;

/** Main-thread controller retained with the cleanup session; it never retains an Activity. */
final class ShizukuCleaner {
    enum Availability { NOT_INSTALLED, NOT_RUNNING, NEEDS_PERMISSION, READY }
    enum Outcome { COMPLETED, TIMED_OUT, FAILED }

    static final class Result {
        final Outcome outcome;
        final long beforeAvailableBytes;
        final long afterAvailableBytes;

        Result(Outcome outcome, long before, long after) {
            this.outcome = outcome;
            beforeAvailableBytes = before;
            afterAvailableBytes = after;
        }

        /** Observed change in free space, not an assertion about cache bytes deleted. */
        long releasedBytes() {
            if (beforeAvailableBytes < 0 || afterAvailableBytes < 0) return -1;
            return Math.max(0, afterAvailableBytes - beforeAvailableBytes);
        }
    }

    private static final int ACCESS_REQUEST = 17041;
    private final Context context;
    private final Handler main = new Handler(Looper.getMainLooper());
    private Runnable listener;
    private Availability availability = Availability.NOT_INSTALLED;
    private boolean closed;
    private boolean requestingAccess;
    private int errorReason;
    private Result lastResult;
    private Operation operation;
    private long sequence;
    private final Runnable accessTimeout = () -> {
        if (closed || !requestingAccess) return;
        requestingAccess = false;
        errorReason = R.string.shizuku_error_access_timeout;
        refresh();
    };

    private final Shizuku.OnBinderReceivedListener received = this::refresh;
    private final Shizuku.OnBinderDeadListener died = () -> {
        if (closed) return;
        finishAccessWait();
        if (operation != null) finish(operation, Outcome.FAILED, R.string.shizuku_error_disconnected);
        refresh();
    };
    private final Shizuku.OnRequestPermissionResultListener permission = (request, granted) -> {
        if (closed || request != ACCESS_REQUEST) return;
        finishAccessWait();
        errorReason = granted == PackageManager.PERMISSION_GRANTED ? 0 : R.string.shizuku_error_denied;
        refresh();
    };

    ShizukuCleaner(Context context) {
        this.context = context.getApplicationContext();
        Shizuku.addBinderReceivedListenerSticky(received, main);
        Shizuku.addBinderDeadListener(died, main);
        Shizuku.addRequestPermissionResultListener(permission, main);
        refresh();
    }

    void setListener(Runnable listener) { this.listener = listener; }
    Availability availability() { return availability; }
    boolean isBusy() { return operation != null; }
    boolean isRequestingAccess() { return requestingAccess; }
    int errorReason() { return errorReason; }
    Result lastResult() { return lastResult; }

    void refresh() {
        if (closed) return;
        try {
            if (!Shizuku.pingBinder()) {
                finishAccessWait();
                availability = managerInstalled() ? Availability.NOT_RUNNING : Availability.NOT_INSTALLED;
            } else if (Shizuku.isPreV11() || Shizuku.getVersion() < 13) {
                availability = Availability.NOT_RUNNING;
                errorReason = R.string.shizuku_error_old_version;
            } else {
                availability = Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED
                        ? Availability.READY : Availability.NEEDS_PERMISSION;
                if (availability == Availability.READY) {
                    finishAccessWait();
                    if (lastResult == null || lastResult.outcome == Outcome.COMPLETED) errorReason = 0;
                }
            }
        } catch (RuntimeException unavailable) {
            availability = Availability.NOT_RUNNING;
        }
        notifyChanged();
    }

    void requestAccess() {
        if (closed || isBusy() || requestingAccess) return;
        errorReason = 0;
        refresh();
        if (availability != Availability.NEEDS_PERMISSION) return;
        try {
            if (Shizuku.shouldShowRequestPermissionRationale()) {
                errorReason = R.string.shizuku_error_permission_settings;
            } else {
                requestingAccess = true;
                main.postDelayed(accessTimeout, 30_000);
                Shizuku.requestPermission(ACCESS_REQUEST);
            }
        } catch (RuntimeException unavailable) {
            finishAccessWait();
            errorReason = R.string.shizuku_error_unavailable;
        }
        notifyChanged();
    }

    /** Call only after the user confirms that the request affects caches across all apps. */
    void trimCaches() {
        if (closed || isBusy()) return;
        refresh();
        if (availability != Availability.READY) return;
        errorReason = 0;
        lastResult = null;
        Operation next = new Operation(++sequence);
        operation = next;
        main.postDelayed(next.timeout, 110_000);
        notifyChanged();
        try {
            Shizuku.bindUserService(next.args, next.connection);
        } catch (RuntimeException unavailable) {
            finish(next, Outcome.FAILED, R.string.shizuku_error_unavailable);
        }
    }

    private final class Operation {
        final long before = AndroidTools.storage(context).availableBytes;
        final Shizuku.UserServiceArgs args;
        final ServiceConnection connection;
        final Runnable timeout;
        boolean dispatched;

        Operation(long sequence) {
            args = new Shizuku.UserServiceArgs(new ComponentName(context, CacheTrimService.class))
                    .daemon(false).version(1).processNameSuffix("cache_trim")
                    .tag("opensweep-cache-" + android.os.Process.myUid() + "-" + sequence
                            + "-" + System.nanoTime());
            timeout = () -> finish(this, Outcome.TIMED_OUT, R.string.shizuku_error_timeout);
            connection = new ServiceConnection() {
                @Override public void onServiceConnected(ComponentName name, IBinder binder) {
                    main.post(() -> connected(Operation.this, binder));
                }
                @Override public void onServiceDisconnected(ComponentName name) {
                    main.post(() -> finish(Operation.this, Outcome.FAILED, R.string.shizuku_error_disconnected));
                }
            };
        }
    }

    private void connected(Operation current, IBinder binder) {
        if (closed || operation != current) {
            release(current);
            return;
        }
        if (current.dispatched) return;
        current.dispatched = true;
        try {
            ICacheTrimService service = ICacheTrimService.Stub.asInterface(binder);
            service.trimCaches(new ICacheTrimCallback.Stub() {
                @Override public void onFinished(int result) {
                    main.post(() -> {
                        Outcome outcome = result == CacheTrimService.COMPLETED ? Outcome.COMPLETED
                                : result == CacheTrimService.TIMED_OUT ? Outcome.TIMED_OUT : Outcome.FAILED;
                        int reason = outcome == Outcome.COMPLETED ? 0
                                : outcome == Outcome.TIMED_OUT ? R.string.shizuku_error_timeout
                                : R.string.shizuku_error_failed;
                        finish(current, outcome, reason);
                    });
                }
            });
        } catch (RemoteException | RuntimeException unavailable) {
            finish(current, Outcome.FAILED, R.string.shizuku_error_unavailable);
        }
    }

    private void finish(Operation current, Outcome outcome, int reason) {
        if (closed || operation != current) return;
        operation = null;
        main.removeCallbacks(current.timeout);
        lastResult = new Result(outcome, current.before, AndroidTools.storage(context).availableBytes);
        errorReason = reason;
        release(current);
        refresh();
    }

    private void release(Operation current) {
        main.removeCallbacks(current.timeout);
        try {
            if (Shizuku.pingBinder()) Shizuku.unbindUserService(current.args, current.connection, true);
        } catch (RuntimeException unavailable) {
            // The user service has its own lifetime bound if the manager is already gone.
        }
    }

    private boolean managerInstalled() {
        try {
            context.getPackageManager().getPackageInfo("moe.shizuku.privileged.api", 0);
            return true;
        } catch (PackageManager.NameNotFoundException | RuntimeException missing) {
            return false;
        }
    }

    private void finishAccessWait() {
        requestingAccess = false;
        main.removeCallbacks(accessTimeout);
    }

    private void notifyChanged() {
        if (!closed && listener != null) listener.run();
    }

    void close() {
        if (closed) return;
        closed = true;
        listener = null;
        finishAccessWait();
        Shizuku.removeBinderReceivedListener(received);
        Shizuku.removeBinderDeadListener(died);
        Shizuku.removeRequestPermissionResultListener(permission);
        Operation current = operation;
        operation = null;
        if (current != null) release(current);
    }
}
