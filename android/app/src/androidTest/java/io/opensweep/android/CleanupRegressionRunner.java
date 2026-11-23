package io.opensweep.android;

import android.Manifest;
import android.app.Activity;
import android.app.Instrumentation;
import android.accessibilityservice.AccessibilityServiceInfo;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.drawable.BitmapDrawable;
import android.net.Uri;
import android.os.Bundle;
import android.os.Environment;
import android.os.ParcelFileDescriptor;
import android.os.SystemClock;
import android.provider.MediaStore;
import android.view.accessibility.AccessibilityNodeInfo;
import android.widget.ImageView;

import java.io.File;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.FileTime;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicReference;

/** Device-level tests with no third-party runner; all mutations are limited to owned fixtures. */
public final class CleanupRegressionRunner extends Instrumentation {
    private Context app;
    private String stage;
    private String namespace;
    private SharedPreferences fixtures;
    private Activity activity;
    private int checks;
    private final StringBuilder report = new StringBuilder();

    @Override public void onCreate(Bundle arguments) {
        super.onCreate(arguments);
        stage = arguments == null ? "all" : arguments.getString("stage", "all");
        start();
    }

    @Override public void onStart() {
        Bundle result = new Bundle();
        Bundle progress = new Bundle();
        progress.putString("class", getClass().getName());
        progress.putString("test", "regression_" + stage);
        progress.putInt("numtests", 1);
        progress.putInt("current", 1);
        sendStatus(1, progress);
        try {
            app = getTargetContext();
            AccessibilityServiceInfo accessibility = getUiAutomation().getServiceInfo();
            accessibility.flags |= AccessibilityServiceInfo.FLAG_REPORT_VIEW_IDS;
            getUiAutomation().setServiceInfo(accessibility);
            require(app.getPackageName().endsWith(".debug"), "debug-only test target");
            if (stage.equals("legacy")) {
                require(!MediaTrash.supported(), "legacy Android without system media trash");
                require(app.checkSelfPermission(Manifest.permission.READ_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED
                        && app.checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED,
                        "legacy shared-storage access granted");
            } else {
                require(MediaTrash.supported(), "Android 11+ media trash available");
                require(Environment.isExternalStorageManager(), "all-files access enabled for debug test app");
            }
            fixtures = app.getSharedPreferences("cleanup_regression_fixtures", Context.MODE_PRIVATE);
            if (stage.equals("legacy")) legacyPhase();
            if (stage.equals("prepare") || stage.equals("all")) preparePhase();
            if (stage.equals("recover") || stage.equals("all")) recoverPhase();
            require(stage.equals("prepare") || stage.equals("recover") || stage.equals("all") || stage.equals("legacy"), "known test stage");
            result.putString("stream", "\nOpenSweep device regression: " + checks + " checks passed (" + stage + ").\n" + report);
            result.putInt("passed", checks);
            sendStatus(0, progress);
            finish(Activity.RESULT_OK, result);
        } catch (Throwable failure) {
            result.putString("stream", "\nFAIL after " + checks + " checks: " + failure + "\n" + report
                    + android.util.Log.getStackTraceString(failure));
            result.putString("failure", failure.toString());
            progress.putString("stack", android.util.Log.getStackTraceString(failure));
            sendStatus(-2, progress);
            finish(Activity.RESULT_CANCELED, result);
        }
    }

    private void legacyPhase() throws Exception {
        namespace = "OpenSweepTest-" + UUID.randomUUID().toString();
        Path pictures = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_PICTURES).toPath().resolve(namespace);
        Path downloads = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS).toPath().resolve(namespace);
        Files.createDirectories(pictures);
        Files.createDirectories(downloads);
        Path original = pictures.resolve("Original_" + namespace + ".png");
        Path extra = pictures.resolve("Screenshot_" + namespace + ".png");
        Path apk = downloads.resolve("review-installer.apk");
        Path document = downloads.resolve("review-document.tmp");
        try {
            Bitmap bitmap = Bitmap.createBitmap(32, 24, Bitmap.Config.ARGB_8888);
            bitmap.eraseColor(0xff000000 | (namespace.hashCode() & 0x00ffffff));
            try (OutputStream output = Files.newOutputStream(original)) {
                require(bitmap.compress(Bitmap.CompressFormat.PNG, 100, output), "legacy image fixture created");
            } finally { bitmap.recycle(); }
            Files.copy(original, extra);
            Files.write(apk, new byte[]{1, 7, 2, 6});
            Files.write(document, new byte[]{5, 4, 3, 2, 1});
            Files.setLastModifiedTime(document, FileTime.fromMillis(System.currentTimeMillis() - 45L * CleanupRules.DAY_MILLIS));
            require(find(scan(SharedStorageCleaner.Mode.APK_INSTALLERS), apk.toString()) != null, "legacy APK category finds installer");
            require(find(scan(SharedStorageCleaner.Mode.OLD_DOWNLOADS), document.toString()) != null, "legacy old-download category finds document");
            require(find(scan(SharedStorageCleaner.Mode.SCREENSHOTS), extra.toString()) != null, "legacy screenshot category finds image");
            List<SharedStorageCleaner.Candidate> files = scan(SharedStorageCleaner.Mode.DUPLICATES);
            files = SharedStorageCleaner.chooseKeeper(files, find(files, original.toString()));
            SharedStorageCleaner.Candidate keeper = find(files, original.toString());
            SharedStorageCleaner.Candidate copy = find(files, extra.toString());
            require(keeper != null && keeper.keeper && copy != null && !copy.keeper, "legacy duplicate keeper remains protected");
            require(!SharedStorageCleaner.validateCandidate(app, keeper), "legacy keeper cannot be removed");
            testPreview(keeper);
            require(ScanPreferences.protectFolder(app, pictures.toString()), "legacy fixture folder protected");
            try {
                require(!SharedStorageCleaner.delete(app, copy), "legacy protection blocks stale removal");
                require(find(scan(SharedStorageCleaner.Mode.SCREENSHOTS), extra.toString()) == null, "legacy protected folder omitted from scan");
            } finally { ScanPreferences.unprotectFolder(app, pictures.toString()); }
            MediaTrash.Prepared unsupported = new MediaTrash(app).prepareTrash(Collections.singletonList(copy));
            require(unsupported.request == null && unsupported.ready == 0 && unsupported.skipped == 1, "legacy trash reports unsupported without fallback");
            require(Files.exists(original) && Files.exists(extra), "legacy files remain untouched");
        } finally {
            require(pictures.getFileName().toString().equals(namespace) && downloads.getFileName().toString().equals(namespace),
                    "legacy cleanup limited to owned fixture folders");
            Files.deleteIfExists(original);
            Files.deleteIfExists(extra);
            Files.deleteIfExists(apk);
            Files.deleteIfExists(document);
            Files.deleteIfExists(pictures);
            Files.deleteIfExists(downloads);
        }
    }

    private void preparePhase() throws Exception {
        require(!new MediaTrash(app).hasPending(), "no unrelated media operation pending");
        String previous = fixtures.getString("namespace", "");
        if (previous.startsWith("OpenSweepTest-")) {
            namespace = previous;
            deleteOwnedMedia(Uri.parse(fixtures.getString("extra", "")));
            cleanupOwnedFixtures(Uri.parse(fixtures.getString("original", "")));
            new MediaTrash(app).snapshot();
        }
        namespace = "OpenSweepTest-" + UUID.randomUUID().toString();
        fixtures.edit().clear().putString("namespace", namespace).commit();
        Bitmap bitmap = Bitmap.createBitmap(32, 24, Bitmap.Config.ARGB_8888);
        bitmap.eraseColor(0xff000000 | (namespace.hashCode() & 0x00ffffff));
        Uri original = addImage("Original_" + namespace + ".png", bitmap);
        Uri extra = addImage("Screenshot_" + namespace + ".png", bitmap);
        bitmap.recycle();
        fixtures.edit().putString("original", original.toString()).putString("extra", extra.toString()).commit();
        String originalPath = mediaPath(original);
        String extraPath = mediaPath(extra);
        fixtures.edit().putString("originalPath", originalPath).putString("extraPath", extraPath).commit();
        require(new File(originalPath).isFile() && new File(extraPath).isFile(), "indexed images exist");

        Path download = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS).toPath().resolve(namespace);
        Files.createDirectories(download);
        Path apk = download.resolve("review-installer.apk");
        Path unsupported = download.resolve("review-document.tmp");
        Files.write(apk, new byte[]{1, 7, 2, 6});
        Files.write(unsupported, new byte[]{5, 4, 3, 2, 1});
        long old = System.currentTimeMillis() - 45L * CleanupRules.DAY_MILLIS;
        Files.setLastModifiedTime(apk, FileTime.fromMillis(old));
        Files.setLastModifiedTime(unsupported, FileTime.fromMillis(old));
        fixtures.edit().putString("downloadFolder", download.toString()).putString("unsupported", unsupported.toString()).commit();
        require(find(scan(SharedStorageCleaner.Mode.APK_INSTALLERS), apk.toString()) != null, "APK review category finds installer");
        require(find(scan(SharedStorageCleaner.Mode.OLD_DOWNLOADS), unsupported.toString()) != null, "old-download review finds 45-day fixture");
        require(find(scan(SharedStorageCleaner.Mode.SCREENSHOTS), extraPath) != null, "screenshot review finds image");

        List<SharedStorageCleaner.Candidate> duplicates = scan(SharedStorageCleaner.Mode.DUPLICATES);
        SharedStorageCleaner.Candidate originalFile = find(duplicates, originalPath);
        require(originalFile != null, "exact duplicates found");
        duplicates = SharedStorageCleaner.chooseKeeper(duplicates, originalFile);
        SharedStorageCleaner.Candidate keeper = find(duplicates, originalPath);
        SharedStorageCleaner.Candidate copy = find(duplicates, extraPath);
        require(keeper != null && keeper.keeper && copy != null && !copy.keeper, "chosen original remains keeper");
        require(!SharedStorageCleaner.validateCandidate(app, keeper), "keeper cannot be removed");
        require(SharedStorageCleaner.validateCandidate(app, copy), "extra duplicate passes content revalidation");
        testPreview(keeper);

        String mediaFolder = new File(originalPath).getParent();
        require(ScanPreferences.protectFolder(app, mediaFolder), "fixture folder protected");
        try {
            require(!SharedStorageCleaner.isCurrentCandidate(app, copy), "protected folder blocks stale preview candidate");
            require(!SharedStorageCleaner.delete(app, copy), "protected folder blocks stale deletion candidate");
            require(find(scan(SharedStorageCleaner.Mode.SCREENSHOTS), extraPath) == null, "protected folder omitted from new scan");
            require(new File(extraPath).isFile(), "protected duplicate remains on disk");
        } finally { ScanPreferences.unprotectFolder(app, mediaFolder); }

        MediaTrash trash = new MediaTrash(app);
        SharedStorageCleaner.Candidate document = find(scan(SharedStorageCleaner.Mode.JUNK), unsupported.toString());
        require(document != null, "unsupported document reviewed");
        MediaTrash.Prepared skipped = trash.prepareTrash(Collections.singletonList(document));
        require(skipped.ready == 0 && skipped.skipped == 1 && skipped.request == null, "unsupported file never falls back to permanent deletion");
        require(Files.exists(unsupported), "unsupported document stays on disk");
        launchApp();
        MediaTrash.Prepared canceled = trash.prepareTrash(Collections.singletonList(copy));
        require(canceled.ready == 1, "Android trash request prepared");
        respond(canceled, false);
        MediaTrash.Outcome canceledResult = trash.finishPending();
        require(canceledResult.succeeded == 0 && mediaState(extra) == 0 && new File(extraPath).isFile(), "canceled trash leaves file untouched");

        MediaTrash.Prepared accepted = trash.prepareTrash(Collections.singletonList(copy));
        require(accepted.ready == 1, "trash can be retried after cancel");
        respond(accepted, true);
        require(mediaState(extra) == 1, "Android marked only the extra copy as trashed");
        require(mediaState(original) == 0 && new File(originalPath).isFile(), "verified keeper survived trash");
        require(new MediaTrash(app).hasPending(), "pending operation durable before result consumption");
        // Deliberately leave the journal pending: recover runs in a new instrumentation/app process in CI.
        report.append("Prepared durable trash recovery fixture: ").append(namespace).append('\n');
    }

    private void recoverPhase() throws Exception {
        namespace = fixtures.getString("namespace", "");
        require(namespace.startsWith("OpenSweepTest-"), "owned restart fixture recorded");
        Uri extra = Uri.parse(fixtures.getString("extra", ""));
        Uri original = Uri.parse(fixtures.getString("original", ""));
        String extraPath = fixtures.getString("extraPath", "");
        String originalPath = fixtures.getString("originalPath", "");
        require(extraPath.contains("/" + namespace + "/") && originalPath.contains("/" + namespace + "/"), "fixture paths scoped to unique test folder");
        MediaTrash recovered = new MediaTrash(app);
        require(recovered.hasPending(), "journal survived process restart");
        MediaTrash.Outcome completed = recovered.finishPending();
        require(completed.succeeded == 1 && completed.operation == MediaTrash.Operation.TRASH, "recovered result verified from MediaStore");
        List<MediaTrash.Entry> owned = ownEntries(recovered.snapshot());
        require(owned.size() == 1 && owned.get(0).uri.equals(extra), "recovery lists the exact trashed fixture");
        launchApp();
        respond(recovered.prepareRestore(owned), true);
        MediaTrash.Outcome restored = recovered.finishPending();
        require(restored.succeeded == 1 && mediaState(extra) == 0 && new File(extraPath).isFile(), "restored image is back at its original path");
        require(ownEntries(recovered.snapshot()).isEmpty(), "restored item removed from trash history");

        List<SharedStorageCleaner.Candidate> duplicates = scan(SharedStorageCleaner.Mode.DUPLICATES);
        duplicates = SharedStorageCleaner.chooseKeeper(duplicates, find(duplicates, originalPath));
        SharedStorageCleaner.Candidate copy = find(duplicates, extraPath);
        require(copy != null && !copy.keeper, "restored file can be reviewed again");
        respond(recovered.prepareTrash(Collections.singletonList(copy)), true);
        require(recovered.finishPending().succeeded == 1, "restored duplicate can be trashed again");
        respond(recovered.prepareDelete(ownEntries(recovered.snapshot())), true);
        require(recovered.finishPending().succeeded == 1 && mediaState(extra) == -1, "empty-trash removes only confirmed fixture");
        require(mediaState(original) == 0 && new File(originalPath).isFile(), "keeper survived permanent removal of extra");
        require(new File(fixtures.getString("unsupported", "")).isFile(), "unsupported document survives all media operations");
        require(ownEntries(new MediaTrash(app).snapshot()).isEmpty(), "deleted fixture removed from durable history");
        cleanupOwnedFixtures(original);
    }

    private void testPreview(SharedStorageCleaner.Candidate file) throws Exception {
        Uri preview = PreviewProvider.grant(app, file, "image/png");
        try (ParcelFileDescriptor descriptor = app.getContentResolver().openFileDescriptor(preview, "r")) {
            require(descriptor != null, "read-only preview descriptor opens");
            Bitmap image = BitmapFactory.decodeFileDescriptor(descriptor.getFileDescriptor());
            require(image != null && image.getWidth() == 32 && image.getHeight() == 24, "preview exposes exact reviewed image bytes");
            image.recycle();
        }
        boolean writeDenied = false;
        try (ParcelFileDescriptor ignored = app.getContentResolver().openFileDescriptor(preview, "w")) { }
        catch (Exception expected) { writeDenied = true; }
        require(writeDenied, "preview rejects writes");
        PreviewProvider.revoke(preview);
        boolean expiredDenied = false;
        try (ParcelFileDescriptor ignored = app.getContentResolver().openFileDescriptor(preview, "r")) { }
        catch (Exception expected) { expiredDenied = true; }
        require(expiredDenied, "revoked preview token rejects reads");
        FilePreview loader = new FilePreview(app);
        AtomicReference<ImageView> view = new AtomicReference<>();
        runOnMainSync(() -> {
            view.set(new ImageView(app));
            loader.bind(view.get(), file);
        });
        long deadline = SystemClock.elapsedRealtime() + 10_000;
        AtomicReference<Boolean> rendered = new AtomicReference<>(false);
        while (!rendered.get() && SystemClock.elapsedRealtime() < deadline) {
            runOnMainSync(() -> {
                if (view.get().getDrawable() instanceof BitmapDrawable) {
                    Bitmap thumbnail = ((BitmapDrawable) view.get().getDrawable()).getBitmap();
                    rendered.set(thumbnail.getWidth() == 32 && thumbnail.getHeight() == 24);
                }
            });
            if (!rendered.get()) SystemClock.sleep(50);
        }
        runOnMainSync(loader::close);
        require(rendered.get(), "asynchronous thumbnail rendered");
    }

    private Uri addImage(String name, Bitmap image) throws IOException {
        ContentValues values = new ContentValues();
        values.put(MediaStore.MediaColumns.DISPLAY_NAME, name);
        values.put(MediaStore.MediaColumns.MIME_TYPE, "image/png");
        values.put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/" + namespace + "/");
        values.put(MediaStore.MediaColumns.IS_PENDING, 1);
        Uri uri = app.getContentResolver().insert(MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), values);
        if (uri == null) throw new IOException("Cannot create media fixture.");
        try (OutputStream output = app.getContentResolver().openOutputStream(uri)) {
            if (output == null || !image.compress(Bitmap.CompressFormat.PNG, 100, output)) throw new IOException("Cannot write fixture.");
        }
        values.clear();
        values.put(MediaStore.MediaColumns.IS_PENDING, 0);
        app.getContentResolver().update(uri, values, null, null);
        return uri;
    }

    private String mediaPath(Uri uri) {
        try (Cursor row = app.getContentResolver().query(uri, new String[]{MediaStore.MediaColumns.DATA}, null, null, null)) {
            if (row == null || !row.moveToFirst()) throw new AssertionError("Fixture media row missing.");
            return row.getString(0);
        }
    }

    private int mediaState(Uri uri) {
        Bundle args = new Bundle();
        args.putInt(MediaStore.QUERY_ARG_MATCH_TRASHED, MediaStore.MATCH_INCLUDE);
        try (Cursor row = app.getContentResolver().query(uri, new String[]{MediaStore.MediaColumns.IS_TRASHED}, args, null)) {
            return row != null && row.moveToFirst() ? row.getInt(0) : -1;
        }
    }

    private List<SharedStorageCleaner.Candidate> scan(SharedStorageCleaner.Mode mode) throws IOException {
        return SharedStorageCleaner.scan(app, mode, null).files;
    }

    private SharedStorageCleaner.Candidate find(List<SharedStorageCleaner.Candidate> files, String path) {
        for (SharedStorageCleaner.Candidate file : files) if (file.absolutePath.equals(path)) return file;
        return null;
    }

    private List<MediaTrash.Entry> ownEntries(List<MediaTrash.Entry> entries) {
        List<MediaTrash.Entry> result = new ArrayList<>();
        for (MediaTrash.Entry entry : entries) if (entry.originalPath.contains("/" + namespace + "/")) result.add(entry);
        return result;
    }

    private void launchApp() {
        if (activity != null) return;
        activity = startActivitySync(new Intent(app, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        waitForIdleSync();
        long deadline = SystemClock.elapsedRealtime() + 10_000;
        AtomicReference<Boolean> ready = new AtomicReference<>(false);
        while (!ready.get() && SystemClock.elapsedRealtime() < deadline) {
            runOnMainSync(() -> ready.set(activity.findViewById(R.id.duplicates) != null
                    && activity.findViewById(R.id.duplicates).isEnabled()));
            if (!ready.get()) SystemClock.sleep(50);
        }
        require(ready.get(), "app initialization completed before media operation");
    }

    private void respond(MediaTrash.Prepared prepared, boolean approve) throws Exception {
        require(prepared.request != null && prepared.ready == 1, "single-fixture system confirmation prepared");
        AtomicReference<Exception> failure = new AtomicReference<>();
        runOnMainSync(() -> {
            try { activity.startIntentSenderForResult(prepared.request.getIntentSender(), 911, null, 0, 0, 0); }
            catch (Exception error) { failure.set(error); }
        });
        if (failure.get() != null) throw failure.get();
        String buttonId = approve ? "android:id/button1" : "android:id/button2";
        long deadline = SystemClock.elapsedRealtime() + 15_000;
        boolean clicked = false;
        while (!clicked && SystemClock.elapsedRealtime() < deadline) {
            AccessibilityNodeInfo root = getUiAutomation().getRootInActiveWindow();
            if (root != null && root.getPackageName() != null
                    && root.getPackageName().toString().contains("providers.media")) {
                for (AccessibilityNodeInfo button : root.findAccessibilityNodeInfosByViewId(buttonId)) {
                    if (button.isEnabled() && button.isClickable()) {
                        clicked = button.performAction(AccessibilityNodeInfo.ACTION_CLICK);
                        if (clicked) break;
                    }
                }
            }
            if (!clicked) SystemClock.sleep(100);
        }
        require(clicked, approve ? "approved Android media dialog" : "canceled Android media dialog");
        deadline = SystemClock.elapsedRealtime() + 15_000;
        boolean returned = false;
        while (!returned && SystemClock.elapsedRealtime() < deadline) {
            AccessibilityNodeInfo root = getUiAutomation().getRootInActiveWindow();
            returned = root != null && app.getPackageName().contentEquals(root.getPackageName());
            if (!returned) SystemClock.sleep(100);
        }
        require(returned, "system operation returned to app");
        waitForIdleSync();
    }

    private void cleanupOwnedFixtures(Uri original) throws IOException {
        String path = fixtures.getString("originalPath", "");
        require(path.contains("/" + namespace + "/"), "cleanup remains inside owned media fixture folder");
        deleteOwnedMedia(original);
        Path folder = new File(path).getParentFile().toPath();
        Files.deleteIfExists(folder); // Only the now-empty fixture folder, never recursive deletion.
        Path download = new File(fixtures.getString("downloadFolder", "")).toPath();
        require(download.getFileName().toString().equals(namespace), "cleanup remains inside owned download fixture folder");
        Files.deleteIfExists(download.resolve("review-installer.apk"));
        Files.deleteIfExists(download.resolve("review-document.tmp"));
        Files.deleteIfExists(download);
        fixtures.edit().clear().commit();
    }

    private void deleteOwnedMedia(Uri uri) {
        if (!"media".equals(uri.getAuthority())) throw new AssertionError("Unexpected fixture URI.");
        Bundle args = new Bundle();
        args.putInt(MediaStore.QUERY_ARG_MATCH_TRASHED, MediaStore.MATCH_INCLUDE);
        try (Cursor row = app.getContentResolver().query(uri, new String[]{MediaStore.MediaColumns.DATA}, args, null)) {
            if (row != null && row.moveToFirst()) {
                require(row.getString(0).contains("/" + namespace + "/"), "only owned media fixtures cleaned up");
                app.getContentResolver().delete(uri, null, null);
            }
        }
    }

    private void require(boolean value, String label) {
        if (!value) throw new AssertionError(label);
        checks++;
        report.append("PASS ").append(label).append('\n');
    }
}
