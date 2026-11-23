package io.opensweep.android;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.ClipData;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Matrix;
import android.media.ExifInterface;
import android.media.MediaMetadataRetriever;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelFileDescriptor;
import android.system.Os;
import android.system.OsConstants;
import android.util.LruCache;
import android.webkit.MimeTypeMap;
import android.widget.ImageView;
import android.widget.Toast;

import java.io.IOException;
import java.lang.ref.WeakReference;
import java.util.Locale;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.FutureTask;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

/** Small, bounded previews; file access and decoding never run on the UI thread. */
final class FilePreview implements AutoCloseable {
    private static final int EDGE = 192;
    private final Context context;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ThreadPoolExecutor worker = new ThreadPoolExecutor(2, 2, 20, TimeUnit.SECONDS,
            new ArrayBlockingQueue<>(24));
    private final LruCache<String, Bitmap> images = new LruCache<String, Bitmap>(8 * 1024 * 1024) {
        @Override protected int sizeOf(String key, Bitmap value) { return value.getAllocationByteCount(); }
    };
    private volatile boolean closed;

    FilePreview(Context context) {
        this.context = context.getApplicationContext();
        worker.allowCoreThreadTimeOut(true);
    }

    static String mimeType(SharedStorageCleaner.Candidate file) {
        int dot = file.name.lastIndexOf('.');
        String extension = dot < 0 ? "" : file.name.substring(dot + 1).toLowerCase(Locale.ROOT);
        String result = MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension);
        return result == null ? "application/octet-stream" : result;
    }

    void bind(ImageView view, SharedStorageCleaner.Candidate file) {
        Object previous = view.getTag();
        if (previous instanceof FutureTask<?>) {
            ((FutureTask<?>) previous).cancel(true);
            worker.remove((FutureTask<?>) previous);
        }
        String mime = mimeType(file);
        view.setImageResource(mime.startsWith("video/") || mime.startsWith("audio/")
                ? android.R.drawable.ic_media_play : mime.startsWith("image/")
                ? android.R.drawable.ic_menu_gallery : android.R.drawable.ic_menu_agenda);
        view.setContentDescription(context.getString(R.string.preview_open) + ": " + file.name);
        view.setTag(null);
        if (closed || (!mime.startsWith("image/") && !mime.startsWith("video/"))) return;
        String key = file.absolutePath + '\n' + file.size + '\n' + file.modifiedMillis;
        Bitmap cached = images.get(key);
        if (cached != null) {
            view.setImageBitmap(cached);
            return;
        }
        WeakReference<ImageView> target = new WeakReference<>(view);
        final FutureTask<?>[] pending = new FutureTask<?>[1];
        FutureTask<Void> task = new FutureTask<>(() -> {
            Bitmap bitmap = thumbnail(file, mime);
            if (bitmap == null || closed || Thread.currentThread().isInterrupted()) return null;
            images.put(key, bitmap);
            main.post(() -> {
                ImageView current = target.get();
                if (!closed && current != null && current.getTag() == pending[0]) current.setImageBitmap(bitmap);
            });
            return null;
        });
        pending[0] = task;
        view.setTag(task);
        try { worker.execute(task); }
        catch (RejectedExecutionException busy) { view.setTag(null); }
    }

    private Bitmap thumbnail(SharedStorageCleaner.Candidate file, String mime) {
        if (Thread.currentThread().isInterrupted()) return null;
        try (ParcelFileDescriptor descriptor = PreviewProvider.openSnapshot(context, file)) {
            if (mime.startsWith("video/")) return video(descriptor);
            BitmapFactory.Options bounds = new BitmapFactory.Options();
            bounds.inJustDecodeBounds = true;
            BitmapFactory.decodeFileDescriptor(descriptor.getFileDescriptor(), null, bounds);
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null;
            BitmapFactory.Options options = new BitmapFactory.Options();
            options.inSampleSize = 1;
            while (bounds.outWidth / options.inSampleSize > EDGE * 2
                    || bounds.outHeight / options.inSampleSize > EDGE * 2) options.inSampleSize *= 2;
            options.inPreferredConfig = Bitmap.Config.RGB_565;
            Os.lseek(descriptor.getFileDescriptor(), 0, OsConstants.SEEK_SET);
            Bitmap image = BitmapFactory.decodeFileDescriptor(descriptor.getFileDescriptor(), null, options);
            if (image == null) return null;
            try {
                Os.lseek(descriptor.getFileDescriptor(), 0, OsConstants.SEEK_SET);
                ExifInterface metadata = new ExifInterface(descriptor.getFileDescriptor());
                image = orient(image, metadata.getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL));
            } catch (IOException | RuntimeException unsupportedMetadata) { /* A valid image can omit EXIF. */ }
            return image;
        } catch (Exception | OutOfMemoryError unavailable) {
            // Corrupt or unusually large files keep the type icon and remain independently reviewable.
            return null;
        }
    }

    private Bitmap video(ParcelFileDescriptor descriptor) throws IOException {
        MediaMetadataRetriever retriever = new MediaMetadataRetriever();
        try {
            retriever.setDataSource(descriptor.getFileDescriptor());
            if (Build.VERSION.SDK_INT >= 27)
                return retriever.getScaledFrameAtTime(-1, MediaMetadataRetriever.OPTION_CLOSEST_SYNC, EDGE, EDGE);
            // Android 8.0 lacks scaled extraction. Do not decode an unbounded full-size frame.
            long width = Long.parseLong(retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH));
            long height = Long.parseLong(retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT));
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || width * height > 4_000_000L) return null;
            Bitmap frame = retriever.getFrameAtTime(-1);
            if (frame == null) return null;
            double scale = Math.min(1, Math.min((double) EDGE / frame.getWidth(), (double) EDGE / frame.getHeight()));
            Bitmap small = Bitmap.createScaledBitmap(frame, Math.max(1, (int) (frame.getWidth() * scale)),
                    Math.max(1, (int) (frame.getHeight() * scale)), true);
            if (small != frame) frame.recycle();
            return small;
        } finally { retriever.release(); }
    }

    private static Bitmap orient(Bitmap image, int orientation) {
        Matrix transform = new Matrix();
        switch (orientation) {
            case ExifInterface.ORIENTATION_FLIP_HORIZONTAL: transform.setScale(-1, 1); break;
            case ExifInterface.ORIENTATION_ROTATE_180: transform.setRotate(180); break;
            case ExifInterface.ORIENTATION_FLIP_VERTICAL: transform.setScale(1, -1); break;
            case ExifInterface.ORIENTATION_TRANSPOSE: transform.setRotate(90); transform.postScale(-1, 1); break;
            case ExifInterface.ORIENTATION_ROTATE_90: transform.setRotate(90); break;
            case ExifInterface.ORIENTATION_TRANSVERSE: transform.setRotate(-90); transform.postScale(-1, 1); break;
            case ExifInterface.ORIENTATION_ROTATE_270: transform.setRotate(-90); break;
            default: return image;
        }
        Bitmap rotated = Bitmap.createBitmap(image, 0, 0, image.getWidth(), image.getHeight(), transform, true);
        if (rotated != image) image.recycle();
        return rotated;
    }

    void open(Activity activity, SharedStorageCleaner.Candidate file) {
        if (closed) return;
        String mime = mimeType(file);
        if (!(mime.startsWith("image/") || mime.startsWith("video/") || mime.startsWith("audio/")
                || mime.equals("application/pdf") || mime.startsWith("text/"))) {
            Toast.makeText(activity, R.string.preview_unsupported, Toast.LENGTH_LONG).show();
            return;
        }
        WeakReference<Activity> target = new WeakReference<>(activity);
        try {
            worker.execute(() -> {
                Uri uri;
                try { uri = PreviewProvider.grant(context, file, mime); }
                catch (IOException | RuntimeException unavailable) {
                    main.post(() -> showMessage(target, R.string.preview_unavailable));
                    return;
                }
                main.post(() -> {
                    Activity current = target.get();
                    if (closed || current == null || current.isFinishing() || current.isDestroyed()) {
                        PreviewProvider.revoke(uri);
                        return;
                    }
                    Intent intent = new Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime)
                            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
                    intent.setClipData(ClipData.newRawUri(file.name, uri));
                    try { current.startActivity(Intent.createChooser(intent, current.getString(R.string.preview_open_with))); }
                    catch (ActivityNotFoundException | SecurityException unavailable) {
                        PreviewProvider.revoke(uri);
                        showMessage(target, R.string.preview_no_app);
                    }
                });
            });
        } catch (RejectedExecutionException busy) { showMessage(target, R.string.preview_unavailable); }
    }

    private void showMessage(WeakReference<Activity> target, int message) {
        Activity activity = target.get();
        if (!closed && activity != null && !activity.isFinishing() && !activity.isDestroyed())
            Toast.makeText(activity, message, Toast.LENGTH_LONG).show();
    }

    @Override public void close() {
        closed = true;
        worker.shutdownNow();
        main.removeCallbacksAndMessages(null);
        images.evictAll();
    }
}
