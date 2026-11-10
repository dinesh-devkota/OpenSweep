package io.opensweep.android;
import io.opensweep.android.ICacheTrimCallback;

interface ICacheTrimService {
    // Reserved transaction used by Shizuku to stop this user service.
    void destroy() = 16777114;
    oneway void trimCaches(ICacheTrimCallback callback) = 1;
}
