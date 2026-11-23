package io.opensweep.android;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.content.IntentSender;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.os.storage.StorageManager;
import android.system.OsConstants;
import android.provider.Settings;
import android.text.SpannableStringBuilder;
import android.text.Spanned;
import android.text.format.Formatter;
import android.text.style.ForegroundColorSpan;
import android.text.style.RelativeSizeSpan;
import android.view.View;
import android.view.ViewGroup;
import android.widget.BaseAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.ListView;
import android.widget.ProgressBar;
import android.widget.SearchView;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import java.text.DateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.HashSet;
import java.util.Set;
import java.io.File;

public final class MainActivity extends Activity {
    private static final int FILE_ACCESS = 1;
    private static final int NOTIFICATIONS = 2;
    private static final int CLEAR_SHARED_CACHE = 3;
    private static final int MEDIA_OPERATION = 4;
    private CleanupSession session;
    private CleanupSession.Page renderedPage;
    private FrameLayout content;
    private FileAdapter fileAdapter;
    private View scanHeader;
    private AppAdapter appAdapter;
    private AlertDialog dialog;
    private boolean usageAvailable;
    private FilePreview previews;
    private MediaTrash.Prepared shownPreparation;
    private final android.os.Handler searchHandler = new android.os.Handler(android.os.Looper.getMainLooper());
    private final Runnable refreshSearch = () -> {
        if (session != null && session.page == CleanupSession.Page.SCAN && !session.working() && fileAdapter != null) {
            fileAdapter.refresh();
            updateSelection();
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        previews = new FilePreview(this);
        session = (CleanupSession) getLastNonConfigurationInstance();
        boolean freshSession = session == null;
        if (session == null) {
            session = new CleanupSession(this);
            if (savedInstanceState != null) {
                String pending = savedInstanceState.getString("pendingMode");
                if (pending != null) session.pendingMode = SharedStorageCleaner.Mode.valueOf(pending);
                session.pendingDaily = savedInstanceState.getBoolean("pendingDaily");
                session.pendingCache = savedInstanceState.getBoolean("pendingCache");
                session.waitingForStorage = savedInstanceState.getBoolean("waitingForStorage");
                session.waitingForNotifications = savedInstanceState.getBoolean("waitingForNotifications");
                session.mediaLaunched = savedInstanceState.getBoolean("mediaLaunched");
            }
        }
        setContentView(R.layout.activity_main);
        content = findViewById(R.id.content);
        findViewById(R.id.tab_storage).setOnClickListener(v -> openPage(CleanupSession.Page.STORAGE));
        findViewById(R.id.tab_apps).setOnClickListener(v -> openPage(CleanupSession.Page.APPS));
        findViewById(R.id.tab_device).setOnClickListener(v -> openPage(CleanupSession.Page.DEVICE));
        session.onChanged = this::render;
        render();
        consumeNotification();
        content.post(this::offerStorageSetup);
        if (freshSession && !session.mediaLaunched && hasStorageAccess()) session.refreshTrash(true, false);
    }

    @Override
    protected void attachBaseContext(Context base) {
        String preference = base.getSharedPreferences("appearance", MODE_PRIVATE).getString("mode", "system");
        if (!preference.equals("system")) {
            Configuration configuration = new Configuration(base.getResources().getConfiguration());
            int night = preference.equals("dark") ? Configuration.UI_MODE_NIGHT_YES : Configuration.UI_MODE_NIGHT_NO;
            configuration.uiMode = (configuration.uiMode & ~Configuration.UI_MODE_NIGHT_MASK) | night;
            base = base.createConfigurationContext(configuration);
        }
        super.attachBaseContext(base);
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (session.waitingForStorage) {
            session.waitingForStorage = false;
            continueAfterAccess();
        }
        if (!hasStorageAccess()) {
            if (session.phase == CleanupSession.Phase.SCANNING) session.cancelScan();
            if (session.phase == CleanupSession.Phase.REVIEW) {
                session.selected.clear();
                session.files = java.util.Collections.emptyList();
                session.phase = CleanupSession.Phase.ERROR;
            }
        }
        session.shizuku.refresh();
        if (session.page == CleanupSession.Page.APPS) session.loadApps();
        if (session.page == CleanupSession.Page.STORAGE) session.refreshStorage();
        render();
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        consumeNotification();
    }

    private void consumeNotification() {
        if ("JUNK".equals(getIntent().getStringExtra("scan_mode"))) {
            getIntent().removeExtra("scan_mode");
            requestScan(SharedStorageCleaner.Mode.JUNK);
        }
    }

    @Override public Object onRetainNonConfigurationInstance() { return session; }

    @Override
    protected void onSaveInstanceState(Bundle state) {
        super.onSaveInstanceState(state);
        if (session.pendingMode != null) state.putString("pendingMode", session.pendingMode.name());
        state.putBoolean("pendingDaily", session.pendingDaily);
        state.putBoolean("pendingCache", session.pendingCache);
        state.putBoolean("waitingForStorage", session.waitingForStorage);
        state.putBoolean("waitingForNotifications", session.waitingForNotifications);
        state.putBoolean("mediaLaunched", session.mediaLaunched);
    }

    @Override
    protected void onDestroy() {
        if (dialog != null) dialog.dismiss();
        searchHandler.removeCallbacksAndMessages(null);
        previews.close();
        session.onChanged = null;
        if (!isChangingConfigurations()) session.close();
        super.onDestroy();
    }

    @Override
    public void onBackPressed() {
        if (session.page != CleanupSession.Page.STORAGE) openPage(CleanupSession.Page.STORAGE);
        else super.onBackPressed();
    }

    private void openPage(CleanupSession.Page page) {
        session.page = page;
        render();
        if (page == CleanupSession.Page.STORAGE) session.refreshStorage();
        if (page == CleanupSession.Page.APPS) session.loadApps();
    }

    private boolean hasStorageAccess() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) return Environment.isExternalStorageManager();
        return checkSelfPermission(Manifest.permission.READ_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED
                && checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED;
    }

    private void offerStorageSetup() {
        if (isFinishing() || isDestroyed() || hasStorageAccess() || dialog != null
                || session.waitingForStorage || session.working()) return;
        android.content.SharedPreferences preferences = getSharedPreferences("storage_setup", MODE_PRIVATE);
        if (preferences.getBoolean("offered", false)) return;
        preferences.edit().putBoolean("offered", true).apply();
        explainFileAccess();
    }

    private void requestScan(SharedStorageCleaner.Mode mode) {
        if (session.shizuku.isBusy()) { toast(R.string.deep_wait); return; }
        if (session.working() || hasStorageAccess()) {
            session.scan(mode);
            return;
        }
        session.pendingMode = mode;
        explainFileAccess();
    }

    private void requestCacheCleanup() {
        if (session.shizuku.isBusy()) { toast(R.string.deep_wait); return; }
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            openPage(CleanupSession.Page.APPS);
            return;
        }
        if (session.working()) {
            openPage(CleanupSession.Page.SCAN);
            return;
        }
        if (!hasStorageAccess()) {
            session.pendingCache = true;
            explainFileAccess();
            return;
        }
        try {
            startActivityForResult(new Intent(StorageManager.ACTION_CLEAR_APP_CACHE), CLEAR_SHARED_CACHE);
        } catch (ActivityNotFoundException | SecurityException unavailable) {
            toast(R.string.settings_unavailable);
        }
    }

    @Override
    protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == MEDIA_OPERATION) { session.finishMedia(); return; }
        if (request != CLEAR_SHARED_CACHE) return;
        if (result == RESULT_OK) toast(R.string.shared_cache_cleared);
        else if (result == OsConstants.EIO) toast(R.string.shared_cache_failed);
        session.refreshStorage();
        session.refreshApps();
    }

    private void explainFileAccess() {
        showDialog(new AlertDialog.Builder(this)
                .setTitle(R.string.access_title)
                .setMessage(session.pendingCache ? R.string.cache_access_body : R.string.access_body)
                .setNegativeButton(android.R.string.cancel, (d, w) -> clearPendingAccess())
                .setPositiveButton(R.string.allow_access, (d, w) -> openFileAccess())
                .setOnCancelListener(d -> clearPendingAccess()));
    }

    private void clearPendingAccess() {
        session.pendingMode = null;
        session.pendingDaily = false;
        session.pendingCache = false;
        render();
    }

    private void openFileAccess() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            session.waitingForStorage = true;
            Intent intent = new Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION,
                    Uri.fromParts("package", getPackageName(), null));
            try {
                startActivity(intent);
            } catch (ActivityNotFoundException | SecurityException unavailable) {
                try {
                    startActivity(new Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION));
                } catch (ActivityNotFoundException | SecurityException noSettings) {
                    session.waitingForStorage = false;
                    clearPendingAccess();
                    toast(R.string.settings_unavailable);
                }
            }
        } else {
            android.content.SharedPreferences preferences = getSharedPreferences("storage_setup", MODE_PRIVATE);
            boolean deniedPermanently = preferences.getBoolean("requested_legacy", false)
                    && !hasStorageAccess()
                    && !shouldShowRequestPermissionRationale(Manifest.permission.WRITE_EXTERNAL_STORAGE);
            if (hasStorageAccess() || deniedPermanently) {
                session.waitingForStorage = true;
                if (!AndroidTools.appInfo(this, getPackageName())) {
                    session.waitingForStorage = false;
                    clearPendingAccess();
                    toast(R.string.settings_unavailable);
                }
            } else {
                preferences.edit().putBoolean("requested_legacy", true).apply();
                requestPermissions(new String[]{Manifest.permission.READ_EXTERNAL_STORAGE,
                        Manifest.permission.WRITE_EXTERNAL_STORAGE}, FILE_ACCESS);
            }
        }
    }

    private void continueAfterAccess() {
        SharedStorageCleaner.Mode mode = session.pendingMode;
        boolean daily = session.pendingDaily;
        boolean cache = session.pendingCache;
        clearPendingAccess();
        if (!hasStorageAccess()) {
            toast(R.string.access_denied);
            return;
        }
        if (mode != null) session.scan(mode);
        if (daily) enableDailyScan();
        if (cache) requestCacheCleanup();
    }

    @Override
    public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request == FILE_ACCESS) continueAfterAccess();
        if (request == NOTIFICATIONS) {
            session.waitingForNotifications = false;
            if (Build.VERSION.SDK_INT < 33
                    || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) {
                enableDailyScan();
            } else toast(R.string.notification_denied);
            render();
        }
    }

    private void enableDailyScan() {
        if (!hasStorageAccess()) {
            session.pendingDaily = true;
            explainFileAccess();
            return;
        }
        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            session.waitingForNotifications = true;
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, NOTIFICATIONS);
            return;
        }
        if (!DailyScanScheduler.setEnabled(this, true)) toast(R.string.daily_failed);
        render();
    }

    private void render() {
        if (renderedPage != session.page) {
            content.removeAllViews();
            renderedPage = session.page;
            switch (session.page) {
                case STORAGE: createStoragePage(); break;
                case SCAN: createScanPage(); break;
                case APPS: createAppsPage(); break;
                case DEVICE: createDevicePage(); break;
            }
        }
        updateTab(R.id.tab_storage, session.page == CleanupSession.Page.STORAGE || session.page == CleanupSession.Page.SCAN);
        updateTab(R.id.tab_apps, session.page == CleanupSession.Page.APPS);
        updateTab(R.id.tab_device, session.page == CleanupSession.Page.DEVICE);
        switch (session.page) {
            case STORAGE: renderStorage(); break;
            case SCAN: renderScan(); break;
            case APPS: renderApps(); break;
            case DEVICE: renderDevice(); break;
        }
        renderMediaDialogs();
    }

    private void updateTab(int id, boolean active) {
        Button tab = findViewById(id);
        tab.setSelected(active);
        tab.setTypeface(null, active ? android.graphics.Typeface.BOLD : android.graphics.Typeface.NORMAL);
        tab.setBackgroundTintList(android.content.res.ColorStateList.valueOf(getColor(active ? R.color.panel : R.color.surface)));
    }

    private void inflate(int layout) {
        getLayoutInflater().inflate(layout, content, true);
    }

    private void createStoragePage() {
        inflate(R.layout.page_storage);
        findViewById(R.id.manage_access).setOnClickListener(v -> openFileAccess());
        findViewById(R.id.junk).setOnClickListener(v -> requestScan(SharedStorageCleaner.Mode.JUNK));
        findViewById(R.id.large).setOnClickListener(v -> requestScan(SharedStorageCleaner.Mode.LARGE_FILES));
        findViewById(R.id.duplicates).setOnClickListener(v -> requestScan(SharedStorageCleaner.Mode.DUPLICATES));
        findViewById(R.id.screenshots).setOnClickListener(v -> requestScan(SharedStorageCleaner.Mode.SCREENSHOTS));
        findViewById(R.id.old_downloads).setOnClickListener(v -> requestScan(SharedStorageCleaner.Mode.OLD_DOWNLOADS));
        findViewById(R.id.apk_installers).setOnClickListener(v -> requestScan(SharedStorageCleaner.Mode.APK_INSTALLERS));
        findViewById(R.id.large_videos).setOnClickListener(v -> requestScan(SharedStorageCleaner.Mode.LARGE_VIDEOS));
        findViewById(R.id.download_age).setOnClickListener(v -> chooseDownloadAge());
        findViewById(R.id.protected_folders).setOnClickListener(v -> showProtectedFolders());
        findViewById(R.id.trash).setOnClickListener(v -> openTrash());
        findViewById(R.id.shared_cache).setOnClickListener(v -> requestCacheCleanup());
        findViewById(R.id.deep_connect).setOnClickListener(v -> {
            if (session.shizuku.errorReason() == R.string.shizuku_error_permission_settings
                    || session.shizuku.availability() == ShizukuCleaner.Availability.NOT_RUNNING) openShizukuManager();
            else if (session.shizuku.availability() == ShizukuCleaner.Availability.NEEDS_PERMISSION)
                session.shizuku.requestAccess();
            else openShizukuGuide();
        });
        findViewById(R.id.deep_guide).setOnClickListener(v -> openShizukuGuide());
        findViewById(R.id.deep_clean).setOnClickListener(v -> confirmDeepCacheCleanup());
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            ((Button) findViewById(R.id.shared_cache)).setText(R.string.manage_app_caches);
            ((TextView) findViewById(R.id.shared_cache_description)).setText(R.string.manual_cache_description);
        }
    }

    private void renderStorage() {
        boolean access = hasStorageAccess();
        ((TextView) findViewById(R.id.access_status)).setText(access ? R.string.access_ready_short : R.string.access_missing);
        ((TextView) findViewById(R.id.access_description)).setText(access ? R.string.access_enabled_body
                : Build.VERSION.SDK_INT >= 30 ? R.string.access_missing_body : R.string.access_missing_legacy);
        ((Button) findViewById(R.id.manage_access)).setText(access ? R.string.review_access
                : Build.VERSION.SDK_INT >= 30 ? R.string.grant_all_files : R.string.grant_storage);
        findViewById(R.id.access_description).setVisibility(access ? View.GONE : View.VISIBLE);
        findViewById(R.id.manage_access).setEnabled(!session.working());
        boolean busy = session.working() || session.shizuku.isBusy();
        for (int id : new int[]{R.id.junk, R.id.duplicates, R.id.large, R.id.shared_cache, R.id.manage_access,
                R.id.screenshots, R.id.old_downloads, R.id.apk_installers, R.id.large_videos,
                R.id.download_age, R.id.protected_folders, R.id.trash})
            findViewById(id).setEnabled(!busy);
        ((TextView) findViewById(R.id.download_age_description)).setText(getString(R.string.old_downloads_description,
                ScanPreferences.getOldDownloadDays(this)));
        ((Button) findViewById(R.id.download_age)).setText(getString(R.string.download_age_short, ScanPreferences.getOldDownloadDays(this)));
        renderDeepCache();
        TextView summary = findViewById(R.id.storage_size);
        ProgressBar progress = findViewById(R.id.storage_progress);
        AndroidTools.DeviceStorage data = session.storage;
        if (data == null || data.totalBytes <= 0) {
            summary.setText(R.string.storage_unavailable);
            progress.setProgress(0);
        } else {
            summary.setText(getString(R.string.storage_free, size(data.availableBytes), size(data.totalBytes)));
            progress.setProgress((int) (1000.0 * (data.totalBytes - data.availableBytes) / data.totalBytes));
        }
    }

    private void openShizukuGuide() {
        try {
            startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse("https://shizuku.rikka.app/guide/setup/")));
        } catch (ActivityNotFoundException unavailable) { toast(R.string.settings_unavailable); }
    }

    private void openShizukuManager() {
        Intent intent = getPackageManager().getLaunchIntentForPackage("moe.shizuku.privileged.api");
        if (intent == null) { openShizukuGuide(); return; }
        try { startActivity(intent); }
        catch (ActivityNotFoundException | SecurityException unavailable) { openShizukuGuide(); }
    }

    private void confirmDeepCacheCleanup() {
        if (session.working() || session.shizuku.isBusy()) { toast(R.string.deep_wait); return; }
        showDialog(new AlertDialog.Builder(this).setTitle(R.string.deep_confirm_title)
                .setMessage(R.string.deep_confirm_body).setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.deep_confirm_action, (d, w) -> session.shizuku.trimCaches()));
    }

    private void renderDeepCache() {
        ShizukuCleaner cleaner = session.shizuku;
        ShizukuCleaner.Availability availability = cleaner.availability();
        int status;
        switch (availability) {
            case NOT_INSTALLED: status = R.string.deep_not_installed; break;
            case NOT_RUNNING: status = R.string.deep_not_running; break;
            case NEEDS_PERMISSION: status = R.string.deep_needs_permission; break;
            default: status = R.string.deep_ready;
        }
        TextView state = findViewById(R.id.deep_status);
        state.setText(cleaner.isBusy() ? R.string.deep_busy : status);
        if (!cleaner.isBusy() && cleaner.errorReason() != 0) state.append("\n" + getString(cleaner.errorReason()));
        Button connect = findViewById(R.id.deep_connect);
        connect.setText(availability == ShizukuCleaner.Availability.NOT_RUNNING
                || cleaner.errorReason() == R.string.shizuku_error_permission_settings ? R.string.deep_open_manager
                : availability == ShizukuCleaner.Availability.NEEDS_PERMISSION ? R.string.deep_connect : R.string.deep_setup);
        connect.setVisibility(availability == ShizukuCleaner.Availability.READY ? View.GONE : View.VISIBLE);
        connect.setEnabled(!cleaner.isBusy() && !cleaner.isRequestingAccess() && !session.working());
        findViewById(R.id.deep_clean).setEnabled(availability == ShizukuCleaner.Availability.READY
                && !cleaner.isBusy() && !session.working());
        ShizukuCleaner.Result result = cleaner.lastResult();
        TextView summary = findViewById(R.id.deep_result);
        summary.setVisibility(result == null ? View.GONE : View.VISIBLE);
        if (result != null) {
            switch (result.outcome) {
                case COMPLETED: summary.setText(getString(R.string.deep_finished,
                        size(result.beforeAvailableBytes), size(result.afterAvailableBytes))); break;
                case TIMED_OUT: summary.setText(R.string.deep_timeout); break;
                default: summary.setText(R.string.deep_failed);
            }
        }
    }

    private void createScanPage() {
        inflate(R.layout.page_scan);
        ListView files = findViewById(R.id.files);
        scanHeader = getLayoutInflater().inflate(R.layout.review_header, files, false);
        files.addHeaderView(scanHeader, null, false);
        fileAdapter = new FileAdapter();
        files.setItemsCanFocus(true);
        files.setAdapter(fileAdapter);
        SearchView search = scanHeader.findViewById(R.id.file_search);
        search.setQuery(session.fileQuery, false);
        search.clearFocus();
        search.setOnQueryTextListener(new SearchView.OnQueryTextListener() {
            @Override public boolean onQueryTextSubmit(String query) {
                searchHandler.removeCallbacks(refreshSearch);
                refreshSearch.run();
                return false;
            }
            @Override public boolean onQueryTextChange(String query) {
                if (session.working()) return false;
                if (!session.fileQuery.equals(query)) {
                    session.fileQuery = query;
                    if (!session.selected.isEmpty()) toast(R.string.filter_cleared_selection);
                    session.selected.clear();
                    searchHandler.removeCallbacks(refreshSearch);
                    searchHandler.postDelayed(refreshSearch, 180);
                    updateSelection();
                }
                return true;
            }
        });
        scanHeader.findViewById(R.id.file_filter).setOnClickListener(v -> chooseFileType());
        scanHeader.findViewById(R.id.rescan).setOnClickListener(v -> requestScan(session.mode));
        scanHeader.findViewById(R.id.coverage).setOnClickListener(v -> showScanDetails());
        findViewById(R.id.primary).setOnClickListener(v -> primaryAction());
        scanHeader.findViewById(R.id.select).setOnClickListener(v -> {
            if (session.working()) return;
            searchHandler.removeCallbacks(refreshSearch);
            fileAdapter.refresh();
            if (!session.selected.isEmpty()) session.selected.clear();
            else for (SharedStorageCleaner.Candidate file : fileAdapter.items) {
                if (!file.keeper) session.selected.add(file);
            }
            fileAdapter.notifyDataSetChanged();
            updateSelection();
        });
    }

    private void primaryAction() {
        if (session.mediaBusy || session.mediaLaunched || session.mediaPrepared != null) return;
        if (session.phase == CleanupSession.Phase.SCANNING) session.cancelScan();
        else if (session.phase == CleanupSession.Phase.REVIEW && !session.files.isEmpty()) chooseRemoval();
        else if (session.phase != CleanupSession.Phase.DELETING) requestScan(session.mode);
    }

    private void chooseRemoval() {
        if (session.selected.isEmpty() || session.working()) return;
        if (!MediaTrash.supported()) { confirmDeletion(); return; }
        showDialog(new AlertDialog.Builder(this).setTitle(R.string.choose_removal)
                .setMessage(getString(R.string.choose_removal_body, session.selected.size(), size(session.selectedBytes())))
                .setPositiveButton(R.string.trash_selected, (d, w) -> session.prepareTrashSelection())
                .setNeutralButton(R.string.permanent_delete, (d, w) -> confirmDeletion())
                .setNegativeButton(android.R.string.cancel, null));
    }

    private void confirmDeletion() {
        if (session.selected.isEmpty()) return;
        if (!hasStorageAccess()) {
            toast(R.string.access_denied);
            return;
        }
        int count = session.selected.size();
        showDialog(new AlertDialog.Builder(this).setTitle(R.string.confirm_title)
                .setMessage(session.mode == SharedStorageCleaner.Mode.DUPLICATES
                        ? getString(R.string.duplicate_confirm, count, size(session.selectedBytes()))
                        : getString(R.string.confirm_body, size(session.selectedBytes())))
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(getResources().getQuantityString(R.plurals.delete_files, count, count),
                        (d, w) -> session.deleteSelection()));
    }

    private void showScanDetails() {
        String locations = session.volumeLabels.isEmpty() ? getString(R.string.no_storage_scanned)
                : android.text.TextUtils.join("\n", session.volumeLabels);
        showDialog(new AlertDialog.Builder(this).setTitle(R.string.scan_details)
                .setMessage(getString(R.string.scan_details_body, session.scannedFiles, session.scannedFolders,
                        locations, session.inaccessibleVolumes, session.protectedFolders,
                        session.skippedFolders, session.skippedFiles))
                .setPositiveButton(android.R.string.ok, null));
    }

    private int modeTitle(SharedStorageCleaner.Mode mode) {
        switch (mode) {
            case JUNK: return R.string.junk;
            case LARGE_FILES: return R.string.large;
            case APK_INSTALLERS: return R.string.apk_installers;
            case OLD_DOWNLOADS: return R.string.old_downloads;
            case SCREENSHOTS: return R.string.screenshots;
            case LARGE_VIDEOS: return R.string.large_videos;
            default: return R.string.duplicates;
        }
    }

    private void chooseDownloadAge() {
        int[] days = {7, 30, 90, 180};
        String[] labels = new String[days.length];
        int selected = 1;
        for (int i = 0; i < days.length; i++) {
            labels[i] = getString(R.string.days_old, days[i]);
            if (days[i] == ScanPreferences.getOldDownloadDays(this)) selected = i;
        }
        showDialog(new AlertDialog.Builder(this).setTitle(R.string.download_age)
                .setSingleChoiceItems(labels, selected, (d, which) -> {
                    ScanPreferences.setOldDownloadDays(this, days[which]);
                    d.dismiss();
                    renderStorage();
                }).setNegativeButton(android.R.string.cancel, null));
    }

    private void showProtectedFolders() {
        List<String> folders = ScanPreferences.getProtectedFolders(this);
        AlertDialog.Builder builder = new AlertDialog.Builder(this).setTitle(R.string.protected_folders);
        if (folders.isEmpty()) builder.setMessage(R.string.protected_folders_empty);
        else builder.setItems(folders.toArray(new String[0]), (d, which) -> {
            String folder = folders.get(which);
            showDialog(new AlertDialog.Builder(this).setTitle(R.string.unprotect_folder)
                    .setMessage(getString(R.string.unprotect_body, folder))
                    .setPositiveButton(R.string.unprotect_action, (confirm, w) -> ScanPreferences.unprotectFolder(this, folder))
                    .setNegativeButton(android.R.string.cancel, null));
        });
        showDialog(builder.setPositiveButton(android.R.string.ok, null));
    }

    private void showFileDetails(SharedStorageCleaner.Candidate file) {
        if (session.working()) return;
        showDialog(new AlertDialog.Builder(this).setTitle(file.name)
                .setMessage(getString(R.string.file_details_body, file.absolutePath, size(file.size),
                        DateFormat.getDateTimeInstance().format(new Date(file.modifiedMillis))))
                .setPositiveButton(R.string.open_file, (d, w) -> previews.open(this, file))
                .setNeutralButton(R.string.protect_folder, (d, w) -> {
                    String folder = new File(file.absolutePath).getParent();
                    showDialog(new AlertDialog.Builder(this).setTitle(R.string.protect_folder)
                            .setMessage(getString(R.string.protect_folder_body, folder))
                            .setPositiveButton(R.string.protect_folder, (confirm, which) -> {
                                if (ScanPreferences.protectFolder(this, folder)) {
                                    session.files = java.util.Collections.emptyList();
                                    session.selected.clear();
                                    session.phase = CleanupSession.Phase.START;
                                    toast(R.string.protected_saved);
                                    render();
                                } else toast(R.string.protect_failed);
                            }).setNegativeButton(android.R.string.cancel, null));
                }).setNegativeButton(android.R.string.cancel, null));
    }

    private int[] fileTypeLabels() {
        return new int[]{R.string.filter_all, R.string.filter_images, R.string.filter_videos,
                R.string.filter_audio, R.string.filter_other};
    }

    private void chooseFileType() {
        if (session.working()) return;
        int[] values = fileTypeLabels();
        String[] labels = new String[values.length];
        for (int i = 0; i < values.length; i++) labels[i] = getString(values[i]);
        showDialog(new AlertDialog.Builder(this).setTitle(R.string.filter_all)
                .setSingleChoiceItems(labels, session.fileType, (d, which) -> {
                    session.fileType = which;
                    if (!session.selected.isEmpty()) toast(R.string.filter_cleared_selection);
                    session.selected.clear();
                    d.dismiss();
                    fileAdapter.refresh();
                    updateSelection();
                }).setNegativeButton(android.R.string.cancel, null));
    }

    private void openTrash() {
        if (session.working()) return;
        if (!MediaTrash.supported()) {
            showDialog(new AlertDialog.Builder(this).setTitle(R.string.trash_title)
                    .setMessage(R.string.trash_unsupported).setPositiveButton(android.R.string.ok, null));
        } else if (!hasStorageAccess()) explainFileAccess();
        else session.refreshTrash(true, true);
    }

    private void renderMediaDialogs() {
        if (isFinishing() || isDestroyed()) return;
        if (session.mediaError) {
            session.mediaError = false;
            showDialog(new AlertDialog.Builder(this).setTitle(R.string.trash_title)
                    .setMessage(R.string.trash_failed).setPositiveButton(android.R.string.ok, null));
        } else if (session.mediaPrepared != null && shownPreparation != session.mediaPrepared) {
            MediaTrash.Prepared prepared = session.mediaPrepared;
            shownPreparation = prepared;
            if (prepared.request == null || prepared.ready == 0) {
                session.mediaPrepared = null;
                render();
                showDialog(new AlertDialog.Builder(this).setTitle(R.string.trash_title)
                        .setMessage(R.string.trash_no_supported).setPositiveButton(android.R.string.ok, (d,w) -> render()));
                return;
            }
            if (prepared.operation != MediaTrash.Operation.TRASH) { launchMediaRequest(prepared); return; }
            showDialog(new AlertDialog.Builder(this).setTitle(R.string.trash_selected)
                    .setMessage(getString(R.string.trash_confirm_body, prepared.ready, size(prepared.bytes), prepared.skipped))
                    .setPositiveButton(R.string.trash_continue, (d,w) -> launchMediaRequest(prepared))
                    .setNegativeButton(android.R.string.cancel, (d,w) -> session.finishMedia())
                    .setOnCancelListener(d -> session.finishMedia()));
        } else if (session.mediaOutcome != null && session.mediaOutcome.operation != null) {
            MediaTrash.Outcome outcome = session.mediaOutcome;
            session.mediaOutcome = null;
            int message = outcome.operation == MediaTrash.Operation.TRASH ? R.string.trash_result
                    : outcome.operation == MediaTrash.Operation.RESTORE ? R.string.restore_result : R.string.empty_result;
            showDialog(new AlertDialog.Builder(this).setTitle(R.string.operation_result)
                    .setMessage(getString(message, outcome.succeeded, outcome.requested, size(outcome.bytes), outcome.failed))
                    .setPositiveButton(android.R.string.ok, null)
                    .setNeutralButton(R.string.trash_title, (d,w) -> openTrash()));
        } else if (session.trashReady) {
            session.trashReady = false;
            showTrashContents();
        }
    }

    private void launchMediaRequest(MediaTrash.Prepared prepared) {
        session.mediaPrepared = null;
        session.mediaLaunched = true;
        try {
            startIntentSenderForResult(prepared.request.getIntentSender(), MEDIA_OPERATION, null, 0, 0, 0);
        } catch (IntentSender.SendIntentException | RuntimeException unavailable) {
            session.finishMedia();
        }
        render();
    }

    private void showTrashContents() {
        List<MediaTrash.Entry> entries = session.trashEntries;
        AlertDialog.Builder builder = new AlertDialog.Builder(this).setTitle(R.string.trash_title)
                .setNegativeButton(android.R.string.cancel, null);
        if (entries.isEmpty()) builder.setMessage(R.string.trash_empty);
        else {
            StringBuilder names = new StringBuilder();
            long total = 0;
            for (int i = 0; i < entries.size(); i++) {
                MediaTrash.Entry entry = entries.get(i);
                total += entry.size;
                if (i < 12) names.append(entry.name).append(" · ").append(size(entry.size)).append('\n');
            }
            if (entries.size() > 12) names.append(getString(R.string.trash_more, entries.size() - 12));
            String message = getString(R.string.trash_summary, entries.size(), size(total), names.toString());
            if (entries.size() > MediaTrash.MAX_BATCH) message += "\n\n" + getString(R.string.trash_batch_notice, MediaTrash.MAX_BATCH);
            builder.setMessage(message)
                    .setPositiveButton(R.string.restore_trash, (d,w) -> session.prepareTrashOperation(false))
                    .setNeutralButton(R.string.empty_trash, (d,w) -> showDialog(new AlertDialog.Builder(this)
                            .setTitle(R.string.empty_trash)
                            .setMessage(getString(R.string.empty_trash_confirm, Math.min(entries.size(), MediaTrash.MAX_BATCH)))
                            .setPositiveButton(R.string.permanent_delete, (confirm, which) -> session.prepareTrashOperation(true))
                            .setNegativeButton(android.R.string.cancel, null)));
        }
        showDialog(builder);
    }

    private void renderScan() {
        SearchView search = scanHeader.findViewById(R.id.file_search);
        if (!search.getQuery().toString().equals(session.fileQuery)) search.setQuery(session.fileQuery, false);
        int title = modeTitle(session.mode);
        ((TextView) scanHeader.findViewById(R.id.mode_title)).setText(title);
        TextView heading = scanHeader.findViewById(R.id.status_title);
        TextView body = scanHeader.findViewById(R.id.status_body);
        Button primary = findViewById(R.id.primary);
        primary.setEnabled(true);
        primary.setText(R.string.scan_again);
        boolean reviewing = session.phase == CleanupSession.Phase.REVIEW && !session.files.isEmpty();
        scanHeader.findViewById(R.id.busy).setVisibility(session.working() ? View.VISIBLE : View.GONE);
        scanHeader.findViewById(R.id.review_controls).setVisibility(reviewing ? View.VISIBLE : View.GONE);
        scanHeader.findViewById(R.id.filter_controls).setVisibility(reviewing ? View.VISIBLE : View.GONE);
        for (int id : new int[]{R.id.select, R.id.rescan, R.id.file_filter, R.id.file_search})
            scanHeader.findViewById(id).setEnabled(!session.working());
        findViewById(R.id.selected_size).setVisibility(reviewing ? View.VISIBLE : View.GONE);
        scanHeader.findViewById(R.id.coverage).setVisibility(session.phase == CleanupSession.Phase.REVIEW
                || session.phase == CleanupSession.Phase.FINISHED ? View.VISIBLE : View.GONE);
        switch (session.phase) {
            case START:
                heading.setText(R.string.scan_again);
                body.setText(R.string.scan_refresh_required);
                break;
            case ERROR:
                heading.setText(R.string.unavailable);
                body.setText(R.string.unavailable_body);
                break;
            case SCANNING:
                heading.setText(R.string.scanning);
                SharedStorageCleaner.ScanProgress progress = session.scanProgress;
                if (progress == null) body.setText(R.string.scanning_body);
                else {
                    String location = progress.currentPath;
                    if (location.length() > 120) location = "…" + location.substring(location.length() - 120);
                    body.setText(progress.stage == SharedStorageCleaner.Stage.COMPARING
                            ? getString(R.string.scan_comparing, size(progress.bytesCompared), progress.filesVisited, location)
                            : getString(R.string.scan_discovering, progress.filesVisited, progress.foldersVisited, location));
                }
                primary.setText(R.string.cancel_scan);
                break;
            case CANCELED:
                heading.setText(R.string.scan_canceled);
                body.setText(R.string.scan_canceled_body);
                break;
            case REVIEW:
                int count = session.files.size();
                if (count == 0) {
                    heading.setText(R.string.no_matches);
                    body.setText(R.string.no_matches_body);
                } else {
                    if (session.mode == SharedStorageCleaner.Mode.DUPLICATES) {
                        int groups = session.groups.size();
                        heading.setText(getResources().getQuantityString(R.plurals.duplicate_groups, groups, groups));
                        body.setText(getResources().getQuantityString(R.plurals.duplicate_summary,
                                session.extraCopies, session.extraCopies, size(session.reclaimableBytes)));
                    } else {
                        heading.setText(getResources().getQuantityString(R.plurals.matches, count, count));
                        body.setText(getString(R.string.review_body, size(CleanupSession.totalBytes(session.files))));
                    }
                    updateSelection();
                }
                if (session.inaccessibleVolumes > 0) body.append("\n" + getString(R.string.scan_partial));
                if (session.skippedFolders > 0) body.append("\n" + getResources().getQuantityString(
                        R.plurals.skipped_folders, session.skippedFolders, session.skippedFolders));
                if (session.skippedFiles > 0) body.append("\n" + getResources().getQuantityString(
                        R.plurals.skipped_files, session.skippedFiles, session.skippedFiles));
                break;
            case DELETING:
                heading.setText(R.string.deleting);
                body.setText(getResources().getQuantityString(R.plurals.deleting_body, session.requested, session.processed, session.requested));
                primary.setText(R.string.deleting);
                primary.setEnabled(false);
                break;
            case FINISHED:
                heading.setText(session.removed == session.requested ? R.string.done : R.string.partial);
                body.setText(getResources().getQuantityString(R.plurals.removed, session.requested, session.removed, session.requested));
                body.append("\n" + getString(R.string.space_reclaimed, size(session.removedBytes)));
                if (session.removed != session.requested) body.append("\n" + getString(R.string.delete_skipped));
                break;
        }
        fileAdapter.refresh();
        if (reviewing) updateSelection();
        if (session.mediaBusy || session.mediaLaunched || session.mediaPrepared != null) {
            primary.setEnabled(false);
            primary.setText(session.mediaLaunched ? R.string.trash_waiting : R.string.trash_prepare);
        }
    }

    private void updateSelection() {
        int count = session.selected.size();
        ((Button) scanHeader.findViewById(R.id.select)).setText(count > 0 ? R.string.clear_selection
                : session.mode == SharedStorageCleaner.Mode.DUPLICATES ? R.string.select_visible_extras : R.string.select_visible);
        Button primary = findViewById(R.id.primary);
        primary.setEnabled(count > 0 && !session.working());
        primary.setText(getString(R.string.review_selected, count));
        ((TextView) findViewById(R.id.selected_size)).setText(getResources().getQuantityString(R.plurals.select_summary, count, count, size(session.selectedBytes())));
    }

    private void createAppsPage() {
        inflate(R.layout.page_apps);
        appAdapter = new AppAdapter();
        ListView list = findViewById(R.id.app_list);
        list.setAdapter(appAdapter);
        list.setOnItemClickListener((parent, view, position, id) -> showAppActions(appAdapter.getItem(position)));
        CheckBox system = findViewById(R.id.show_system);
        system.setChecked(session.showSystem);
        system.setOnCheckedChangeListener((button, checked) -> {
            session.showSystem = checked;
            renderApps();
        });
        SearchView search = findViewById(R.id.app_search);
        search.setQuery(session.appQuery, false);
        search.clearFocus();
        search.setOnQueryTextListener(new SearchView.OnQueryTextListener() {
            @Override public boolean onQueryTextSubmit(String query) { return false; }
            @Override public boolean onQueryTextChange(String query) {
                session.appQuery = query;
                renderApps();
                return true;
            }
        });
        findViewById(R.id.usage).setOnClickListener(v -> showDialog(new AlertDialog.Builder(this)
                .setTitle(R.string.usage_title).setMessage(R.string.usage_body)
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.allow_access, (d, w) -> showSettingsResult(AndroidTools.usageAccess(this)))));
    }

    private void renderApps() {
        usageAvailable = InstalledApps.hasUsageAccess(this);
        findViewById(R.id.usage).setVisibility(usageAvailable ? View.GONE : View.VISIBLE);
        appAdapter.items.clear();
        String query = session.appQuery.trim().toLowerCase(Locale.ROOT);
        for (InstalledApps.Entry app : session.apps) {
            if (!session.showSystem && app.system) continue;
            if (!app.label.toLowerCase(Locale.ROOT).contains(query) && !app.packageName.toLowerCase(Locale.ROOT).contains(query)) continue;
            appAdapter.items.add(app);
        }
        appAdapter.notifyDataSetChanged();
        TextView status = findViewById(R.id.apps_status);
        status.setVisibility(session.appsLoading || session.appsError || appAdapter.items.isEmpty() ? View.VISIBLE : View.GONE);
        status.setText(session.appsLoading ? R.string.apps_loading : session.appsError ? R.string.apps_error : R.string.apps_empty);
    }

    private void showAppActions(InstalledApps.Entry app) {
        List<String> labels = new ArrayList<>();
        labels.add(getString(R.string.app_info));
        labels.add(getString(R.string.app_update));
        if (!app.system) labels.add(getString(R.string.uninstall));
        showDialog(new AlertDialog.Builder(this).setTitle(app.label)
                .setItems(labels.toArray(new String[0]), (d, which) -> {
                    boolean opened;
                    if (which == 0) opened = AndroidTools.appInfo(this, app.packageName);
                    else if (which == 1) opened = AndroidTools.appStore(this, app.packageName);
                    else opened = AndroidTools.uninstall(this, app);
                    showSettingsResult(opened);
                }));
    }

    private void createDevicePage() {
        inflate(R.layout.page_device);
        findViewById(R.id.storage_settings).setOnClickListener(v -> showSettingsResult(AndroidTools.storageSettings(this)));
        findViewById(R.id.battery_settings).setOnClickListener(v -> showSettingsResult(AndroidTools.batterySettings(this)));
        findViewById(R.id.security_settings).setOnClickListener(v -> showSettingsResult(AndroidTools.securitySettings(this)));
        findViewById(R.id.update_settings).setOnClickListener(v -> showSettingsResult(AndroidTools.systemUpdateSettings(this)));
        findViewById(R.id.appearance).setOnClickListener(v -> chooseAppearance());
        try {
            String version = getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
            ((TextView) findViewById(R.id.app_version)).setText(getString(R.string.app_version, version));
        } catch (PackageManager.NameNotFoundException ignored) { }
    }

    private void renderDevice() {
        Switch daily = findViewById(R.id.daily);
        daily.setOnCheckedChangeListener(null);
        daily.setChecked(DailyScanScheduler.isEnabled(this));
        daily.setOnCheckedChangeListener((button, checked) -> {
            if (checked) enableDailyScan();
            else {
                DailyScanScheduler.setEnabled(this, false);
                renderDevice();
            }
        });
    }

    private void chooseAppearance() {
        String[] modes = {"system", "light", "dark"};
        String saved = getSharedPreferences("appearance", MODE_PRIVATE).getString("mode", "system");
        int checked = java.util.Arrays.asList(modes).indexOf(saved);
        showDialog(new AlertDialog.Builder(this).setTitle(R.string.appearance)
                .setSingleChoiceItems(R.array.appearances, Math.max(0, checked), (d, which) -> {
                    getSharedPreferences("appearance", MODE_PRIVATE).edit().putString("mode", modes[which]).apply();
                    d.dismiss();
                    recreate();
                }));
    }

    private void showSettingsResult(boolean opened) {
        if (!opened) toast(R.string.settings_unavailable);
    }

    private void showDialog(AlertDialog.Builder builder) {
        if (dialog != null) dialog.dismiss();
        dialog = builder.create();
        AlertDialog current = dialog;
        current.setOnDismissListener(d -> { if (dialog == current) dialog = null; });
        dialog.show();
    }

    private void toast(int message) {
        Toast.makeText(this, message, Toast.LENGTH_LONG).show();
    }

    private String size(long bytes) {
        return bytes < 0 ? getString(R.string.size_unknown) : Formatter.formatFileSize(this, bytes);
    }

    private SpannableStringBuilder detailLabel(String title, String detail) {
        SpannableStringBuilder text = new SpannableStringBuilder(title).append('\n');
        int start = text.length();
        text.append(detail);
        text.setSpan(new RelativeSizeSpan(0.85f), start, text.length(), Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        text.setSpan(new ForegroundColorSpan(getColor(R.color.body)), start, text.length(), Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        return text;
    }

    private final class FileAdapter extends BaseAdapter {
        final List<SharedStorageCleaner.Candidate> items = new ArrayList<>();

        void refresh() {
            items.clear();
            Set<String> matchingGroups = new HashSet<>();
            for (SharedStorageCleaner.Candidate file : session.files) {
                if (matches(file) && file.duplicateGroup != null) matchingGroups.add(file.duplicateGroup);
            }
            for (SharedStorageCleaner.Candidate file : session.files) {
                if (file.duplicateGroup != null ? matchingGroups.contains(file.duplicateGroup) : matches(file)) items.add(file);
            }
            session.selected.retainAll(items);
            ((Button) scanHeader.findViewById(R.id.file_filter)).setText(getString(R.string.file_filter,
                    getString(fileTypeLabels()[session.fileType])));
            ((TextView) scanHeader.findViewById(R.id.visible_results)).setText(getString(R.string.visible_results,
                    items.size(), session.files.size()));
            notifyDataSetChanged();
        }

        private boolean matches(SharedStorageCleaner.Candidate file) {
            if (!file.path.toLowerCase(Locale.ROOT).contains(session.fileQuery.toLowerCase(Locale.ROOT).trim())) return false;
            String mime = FilePreview.mimeType(file);
            int type = mime.startsWith("image/") ? 1 : mime.startsWith("video/") ? 2 : mime.startsWith("audio/") ? 3 : 4;
            return session.fileType == 0 || session.fileType == type;
        }

        @Override public int getCount() { return items.size(); }
        @Override public SharedStorageCleaner.Candidate getItem(int position) { return items.get(position); }
        @Override public long getItemId(int position) { return position; }
        @Override public View getView(int position, View row, ViewGroup parent) {
            if (row == null) row = getLayoutInflater().inflate(R.layout.file_row, parent, false);
            CheckBox check = row.findViewById(R.id.file_check);
            SharedStorageCleaner.Candidate file = getItem(position);
            TextView groupTitle = row.findViewById(R.id.group_title);
            boolean firstInGroup = file.duplicateGroup != null && (position == 0
                    || !file.duplicateGroup.equals(getItem(position - 1).duplicateGroup));
            groupTitle.setVisibility(firstInGroup ? View.VISIBLE : View.GONE);
            if (firstInGroup) {
                int copies = 0;
                long removable = 0;
                for (int index = position; index < items.size(); index++) {
                    SharedStorageCleaner.Candidate member = items.get(index);
                    if (!file.duplicateGroup.equals(member.duplicateGroup)) break;
                    copies++;
                    if (!member.keeper) removable += member.size;
                }
                groupTitle.setText(getString(R.string.duplicate_group_details,
                        session.groups.get(file.duplicateGroup), copies, size(removable)));
            }
            Button keep = row.findViewById(R.id.keep_copy);
            keep.setVisibility(file.duplicateGroup != null && !file.keeper ? View.VISIBLE : View.GONE);
            keep.setOnClickListener(v -> {
                if (session.working()) return;
                if (session.setKeeper(file)) {
                    fileAdapter.refresh();
                    updateSelection();
                    toast(R.string.keeper_changed);
                } else toast(R.string.keeper_failed);
            });
            String details = size(file.size) + " · " + DateFormat.getDateInstance().format(new Date(file.modifiedMillis));
            if (file.duplicateGroup != null) details += " · " + getString(R.string.duplicate_group, session.groups.get(file.duplicateGroup));
            if (file.keeper) details += " · " + getString(R.string.keeper);
            check.setText(detailLabel(file.name, details + "\n" + file.path));
            ImageView thumbnail = row.findViewById(R.id.thumbnail);
            previews.bind(thumbnail, file);
            thumbnail.setOnClickListener(v -> showFileDetails(file));
            row.findViewById(R.id.file_details).setOnClickListener(v -> showFileDetails(file));
            row.findViewById(R.id.file_details).setEnabled(!session.working());
            keep.setEnabled(!session.working());
            check.setOnCheckedChangeListener(null);
            check.setChecked(session.selected.contains(file));
            check.setEnabled(!file.keeper && !session.working());
            check.setOnCheckedChangeListener((button, checked) -> {
                if (checked && !file.keeper) session.selected.add(file);
                else session.selected.remove(file);
                updateSelection();
            });
            return row;
        }
    }

    private final class AppAdapter extends BaseAdapter {
        final List<InstalledApps.Entry> items = new ArrayList<>();
        @Override public int getCount() { return items.size(); }
        @Override public InstalledApps.Entry getItem(int position) { return items.get(position); }
        @Override public long getItemId(int position) { return position; }
        @Override public View getView(int position, View row, ViewGroup parent) {
            if (row == null) row = getLayoutInflater().inflate(R.layout.app_row, parent, false);
            InstalledApps.Entry app = getItem(position);
            String details = app.packageName + "\n" + getString(R.string.app_apk, size(app.apkBytes));
            if (app.dataBytes >= 0) details += "\n" + getString(R.string.app_storage, size(app.appBytes), size(app.dataBytes), size(app.cacheBytes));
            if (app.lastUsedMs > 0) details += "\n" + getString(R.string.app_recent, DateFormat.getDateInstance().format(new Date(app.lastUsedMs)));
            else if (usageAvailable) details += "\n" + getString(R.string.app_not_observed);
            if (app.system) details += "\n" + getString(R.string.system_app);
            ((TextView) row).setText(detailLabel(app.label, details));
            return row;
        }
    }
}
