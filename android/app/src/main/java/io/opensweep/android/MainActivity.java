package io.opensweep.android;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
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

public final class MainActivity extends Activity {
    private static final int FILE_ACCESS = 1;
    private static final int NOTIFICATIONS = 2;
    private static final int CLEAR_SHARED_CACHE = 3;
    private CleanupSession session;
    private CleanupSession.Page renderedPage;
    private FrameLayout content;
    private FileAdapter fileAdapter;
    private View scanHeader;
    private AppAdapter appAdapter;
    private AlertDialog dialog;
    private boolean usageAvailable;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        session = (CleanupSession) getLastNonConfigurationInstance();
        if (session == null) {
            session = new CleanupSession(this);
            if (savedInstanceState != null) {
                String pending = savedInstanceState.getString("pendingMode");
                if (pending != null) session.pendingMode = SharedStorageCleaner.Mode.valueOf(pending);
                session.pendingDaily = savedInstanceState.getBoolean("pendingDaily");
                session.pendingCache = savedInstanceState.getBoolean("pendingCache");
                session.waitingForStorage = savedInstanceState.getBoolean("waitingForStorage");
                session.waitingForNotifications = savedInstanceState.getBoolean("waitingForNotifications");
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
    }

    @Override
    protected void onDestroy() {
        if (dialog != null) dialog.dismiss();
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

    private void requestScan(SharedStorageCleaner.Mode mode) {
        if (session.working() || hasStorageAccess()) {
            session.scan(mode);
            return;
        }
        session.pendingMode = mode;
        explainFileAccess();
    }

    private void requestCacheCleanup() {
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
            requestPermissions(new String[]{Manifest.permission.READ_EXTERNAL_STORAGE,
                    Manifest.permission.WRITE_EXTERNAL_STORAGE}, FILE_ACCESS);
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
        findViewById(R.id.junk).setOnClickListener(v -> requestScan(SharedStorageCleaner.Mode.JUNK));
        findViewById(R.id.large).setOnClickListener(v -> requestScan(SharedStorageCleaner.Mode.LARGE_FILES));
        findViewById(R.id.duplicates).setOnClickListener(v -> requestScan(SharedStorageCleaner.Mode.DUPLICATES));
        findViewById(R.id.shared_cache).setOnClickListener(v -> requestCacheCleanup());
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            ((Button) findViewById(R.id.shared_cache)).setText(R.string.manage_app_caches);
            ((TextView) findViewById(R.id.shared_cache_description)).setText(R.string.manual_cache_description);
        }
    }

    private void renderStorage() {
        findViewById(R.id.shared_cache).setEnabled(!session.working());
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

    private void createScanPage() {
        inflate(R.layout.page_scan);
        ListView files = findViewById(R.id.files);
        scanHeader = getLayoutInflater().inflate(R.layout.review_header, files, false);
        files.addHeaderView(scanHeader, null, false);
        fileAdapter = new FileAdapter();
        files.setItemsCanFocus(true);
        files.setAdapter(fileAdapter);
        scanHeader.findViewById(R.id.rescan).setOnClickListener(v -> requestScan(session.mode));
        findViewById(R.id.primary).setOnClickListener(v -> primaryAction());
        scanHeader.findViewById(R.id.select).setOnClickListener(v -> {
            if (!session.selected.isEmpty()) session.selected.clear();
            else for (SharedStorageCleaner.Candidate file : session.files) {
                if (!file.keeper) session.selected.add(file);
            }
            fileAdapter.notifyDataSetChanged();
            updateSelection();
        });
    }

    private void primaryAction() {
        if (session.phase == CleanupSession.Phase.SCANNING) session.cancelScan();
        else if (session.phase == CleanupSession.Phase.REVIEW && !session.files.isEmpty()) confirmDeletion();
        else if (session.phase != CleanupSession.Phase.DELETING) requestScan(session.mode);
    }

    private void confirmDeletion() {
        if (session.selected.isEmpty()) return;
        if (!hasStorageAccess()) {
            toast(R.string.access_denied);
            return;
        }
        int count = session.selected.size();
        showDialog(new AlertDialog.Builder(this).setTitle(R.string.confirm_title)
                .setMessage(getString(R.string.confirm_body, size(session.selectedBytes())))
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(getResources().getQuantityString(R.plurals.delete_files, count, count),
                        (d, w) -> session.deleteSelection()));
    }

    private void renderScan() {
        int title = session.mode == SharedStorageCleaner.Mode.JUNK ? R.string.junk
                : session.mode == SharedStorageCleaner.Mode.LARGE_FILES ? R.string.large : R.string.duplicates;
        ((TextView) scanHeader.findViewById(R.id.mode_title)).setText(title);
        TextView heading = scanHeader.findViewById(R.id.status_title);
        TextView body = scanHeader.findViewById(R.id.status_body);
        Button primary = findViewById(R.id.primary);
        primary.setEnabled(true);
        primary.setText(R.string.scan_again);
        boolean reviewing = session.phase == CleanupSession.Phase.REVIEW && !session.files.isEmpty();
        scanHeader.findViewById(R.id.busy).setVisibility(session.working() ? View.VISIBLE : View.GONE);
        scanHeader.findViewById(R.id.review_controls).setVisibility(reviewing ? View.VISIBLE : View.GONE);
        findViewById(R.id.selected_size).setVisibility(reviewing ? View.VISIBLE : View.GONE);
        switch (session.phase) {
            case START:
            case ERROR:
                heading.setText(R.string.unavailable);
                body.setText(R.string.unavailable_body);
                break;
            case SCANNING:
                heading.setText(R.string.scanning);
                body.setText(session.mode == SharedStorageCleaner.Mode.DUPLICATES ? R.string.hashing_body : R.string.scanning_body);
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
                    heading.setText(getResources().getQuantityString(R.plurals.matches, count, count));
                    body.setText(getString(session.mode == SharedStorageCleaner.Mode.DUPLICATES
                            ? R.string.duplicate_review : R.string.review_body, size(CleanupSession.totalBytes(session.files))));
                    updateSelection();
                }
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
        fileAdapter.notifyDataSetChanged();
    }

    private void updateSelection() {
        int count = session.selected.size();
        ((Button) scanHeader.findViewById(R.id.select)).setText(count > 0 ? R.string.clear_selection : R.string.select_all);
        Button primary = findViewById(R.id.primary);
        primary.setEnabled(count > 0);
        primary.setText(count == 0 ? getString(R.string.delete_selected)
                : getResources().getQuantityString(R.plurals.delete_files, count, count));
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
        @Override public int getCount() { return session.files.size(); }
        @Override public SharedStorageCleaner.Candidate getItem(int position) { return session.files.get(position); }
        @Override public long getItemId(int position) { return position; }
        @Override public View getView(int position, View row, ViewGroup parent) {
            if (row == null) row = getLayoutInflater().inflate(R.layout.file_row, parent, false);
            CheckBox check = row.findViewById(R.id.file_check);
            SharedStorageCleaner.Candidate file = getItem(position);
            String details = size(file.size);
            if (file.duplicateGroup != null) details += " · " + getString(R.string.duplicate_group, session.groups.get(file.duplicateGroup));
            if (file.keeper) details += " · " + getString(R.string.keeper);
            check.setText(detailLabel(file.path, details));
            check.setOnCheckedChangeListener(null);
            check.setChecked(session.selected.contains(file));
            check.setEnabled(!file.keeper);
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
