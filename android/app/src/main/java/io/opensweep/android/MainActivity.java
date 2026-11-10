package io.opensweep.android;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.provider.DocumentsContract;
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
import android.widget.ListView;
import android.widget.TextView;
import android.widget.Toast;

public final class MainActivity extends Activity {
    private static final int CHOOSE_FOLDER = 1;
    private static final int FOLDER_ACCESS = Intent.FLAG_GRANT_READ_URI_PERMISSION
            | Intent.FLAG_GRANT_WRITE_URI_PERMISSION;

    private CleanupSession session;
    private FileAdapter adapter;
    private Button primary;
    private Button select;
    private TextView statusTitle;
    private TextView statusBody;
    private TextView folderName;
    private View busy;
    private View reviewControls;
    private Button changeFolder;
    private AlertDialog confirmation;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        session = (CleanupSession) getLastNonConfigurationInstance();
        if (session == null) session = new CleanupSession(getApplicationContext().getContentResolver());

        setContentView(R.layout.activity_main);
        ListView files = findViewById(R.id.files);
        View header = getLayoutInflater().inflate(R.layout.review_header, files, false);
        files.addHeaderView(header, null, false);
        adapter = new FileAdapter();
        files.setItemsCanFocus(true);
        files.setAdapter(adapter);

        primary = findViewById(R.id.primary);
        select = header.findViewById(R.id.select);
        statusTitle = header.findViewById(R.id.status_title);
        statusBody = header.findViewById(R.id.status_body);
        folderName = header.findViewById(R.id.folder_name);
        busy = header.findViewById(R.id.busy);
        reviewControls = header.findViewById(R.id.review_controls);
        changeFolder = findViewById(R.id.change_folder);
        changeFolder.setOnClickListener(v -> chooseFolder());
        header.findViewById(R.id.rescan).setOnClickListener(v -> session.scan(session.tree));
        primary.setOnClickListener(v -> primaryAction());
        select.setOnClickListener(v -> {
            if (session.selected.size() == session.files.size()) session.selected.clear();
            else session.selected.addAll(session.files);
            adapter.notifyDataSetChanged();
            updateSelectionButtons();
        });
        session.onChanged = this::render;
        render();
    }

    @Override
    public Object onRetainNonConfigurationInstance() {
        return session;
    }

    @Override
    protected void onDestroy() {
        if (confirmation != null) confirmation.dismiss();
        session.onChanged = null;
        if (!isChangingConfigurations()) session.close();
        super.onDestroy();
    }

    private void chooseFolder() {
        Intent picker = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE)
                .addFlags(FOLDER_ACCESS)
                .putExtra(Intent.EXTRA_LOCAL_ONLY, true);
        if (session.tree != null) picker.putExtra(DocumentsContract.EXTRA_INITIAL_URI, session.tree);
        try {
            startActivityForResult(picker, CHOOSE_FOLDER);
        } catch (ActivityNotFoundException unavailable) {
            Toast.makeText(this, R.string.picker_unavailable, Toast.LENGTH_LONG).show();
        }
    }

    @Override
    protected void onActivityResult(int request, int result, Intent intent) {
        super.onActivityResult(request, result, intent);
        if (request != CHOOSE_FOLDER || result != RESULT_OK || intent == null) return;
        Uri folder = intent.getData();
        if (folder == null || !DocumentsContract.isTreeUri(folder)
                || (intent.getFlags() & FOLDER_ACCESS) != FOLDER_ACCESS) {
            Toast.makeText(this, R.string.grant_missing, Toast.LENGTH_LONG).show();
            return;
        }
        session.scan(folder);
    }

    private boolean hasResults() {
        return session.phase == CleanupSession.Phase.REVIEW && !session.files.isEmpty();
    }

    private void primaryAction() {
        switch (session.phase) {
            case START:
                chooseFolder();
                break;
            case SCANNING:
                session.cancelScan();
                break;
            case DELETING:
                break;
            case REVIEW:
                if (hasResults()) {
                    confirmDeletion();
                    break;
                }
                session.scan(session.tree);
                break;
            default:
                session.scan(session.tree);
        }
    }

    private void confirmDeletion() {
        int count = session.selected.size();
        if (count == 0) return;
        String delete = getResources().getQuantityString(R.plurals.delete_files, count, count);
        confirmation = new AlertDialog.Builder(this)
                .setTitle(R.string.confirm_title)
                .setMessage(getString(R.string.confirm_body, displayedFolderName()))
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(delete, (dialog, which) -> session.deleteSelection())
                .create();
        confirmation.setOnDismissListener(dialog -> confirmation = null);
        confirmation.show();
    }

    private String displayedFolderName() {
        return session.folderName == null || session.folderName.isEmpty()
                ? getString(R.string.selected_folder) : session.folderName;
    }

    private void render() {
        boolean working = session.phase == CleanupSession.Phase.SCANNING || session.phase == CleanupSession.Phase.DELETING;
        busy.setVisibility(working ? View.VISIBLE : View.GONE);
        folderName.setVisibility(session.tree == null ? View.GONE : View.VISIBLE);
        folderName.setText(displayedFolderName());
        changeFolder.setVisibility(session.tree != null && !working ? View.VISIBLE : View.GONE);
        reviewControls.setVisibility(hasResults() ? View.VISIBLE : View.GONE);
        primary.setEnabled(true);
        primary.setText(R.string.scan_again);

        switch (session.phase) {
            case START:
                setStatus(R.string.start_title, R.string.start_body);
                primary.setText(R.string.choose_folder);
                break;
            case SCANNING:
                setStatus(R.string.scanning, R.string.scanning_body);
                primary.setText(R.string.cancel_scan);
                break;
            case CANCELED:
                setStatus(R.string.scan_canceled, R.string.scan_canceled_body);
                break;
            case ERROR:
                setStatus(R.string.unavailable, R.string.unavailable_body);
                break;
            case REVIEW:
                int count = session.files.size();
                if (count == 0) setStatus(R.string.no_matches, R.string.no_matches_body);
                else {
                    statusTitle.setText(getResources().getQuantityString(R.plurals.matches, count, count));
                    statusBody.setText(R.string.review_body);
                    updateSelectionButtons();
                }
                if (session.skippedFolders > 0) {
                    statusBody.append("\n" + getResources().getQuantityString(R.plurals.skipped_folders, session.skippedFolders, session.skippedFolders));
                }
                break;
            case DELETING:
                setStatus(R.string.deleting, R.string.deleting_body);
                primary.setText(R.string.deleting);
                primary.setEnabled(false);
                break;
            case FINISHED:
                boolean complete = session.removed == session.requested;
                statusTitle.setText(complete ? R.string.done : R.string.partial);
                statusBody.setText(getResources().getQuantityString(R.plurals.removed, session.requested, session.removed, session.requested));
                if (!complete) statusBody.append("\n" + getString(R.string.delete_skipped));
                break;
        }
        adapter.notifyDataSetChanged();
    }

    private void setStatus(int title, int body) {
        statusTitle.setText(title);
        statusBody.setText(body);
    }

    private void updateSelectionButtons() {
        int count = session.selected.size();
        select.setText(count == session.files.size() ? R.string.clear_selection : R.string.select_all);
        primary.setEnabled(count > 0);
        if (count == 0) primary.setText(R.string.delete_selected);
        else primary.setText(getResources().getQuantityString(R.plurals.delete_files, count, count));
    }

    private final class FileAdapter extends BaseAdapter {
        @Override public int getCount() { return session.files.size(); }
        @Override public FolderCleaner.Candidate getItem(int position) { return session.files.get(position); }
        @Override public long getItemId(int position) { return position; }

        @Override
        public View getView(int position, View row, ViewGroup parent) {
            if (row == null) row = getLayoutInflater().inflate(R.layout.file_row, parent, false);
            CheckBox check = row.findViewById(R.id.file_check);
            FolderCleaner.Candidate file = getItem(position);
            String size = file.size < 0 ? getString(R.string.size_unknown)
                    : Formatter.formatFileSize(MainActivity.this, file.size);
            SpannableStringBuilder label = new SpannableStringBuilder(file.path).append('\n');
            int sizeStart = label.length();
            label.append(size);
            label.setSpan(new RelativeSizeSpan(0.85f), sizeStart, label.length(), Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            label.setSpan(new ForegroundColorSpan(getColor(R.color.body)), sizeStart, label.length(), Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            check.setText(label);
            check.setOnCheckedChangeListener(null);
            check.setChecked(session.selected.contains(file));
            check.setOnCheckedChangeListener((button, checked) -> {
                if (checked) session.selected.add(file);
                else session.selected.remove(file);
                updateSelectionButtons();
            });
            return row;
        }
    }
}
