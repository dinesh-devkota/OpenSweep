using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class HistoryPage : UserControl, IPage
    {
        List<MaintenanceRecord> _records = new List<MaintenanceRecord>();
        List<MaintenanceRecord> _shown = new List<MaintenanceRecord>();
        bool _loading;
        public HistoryPage()
        {
            InitializeComponent();
            OutcomeBox.ItemsSource = new[] { "All results", "Completed", "Partial", "Failed", "Cancelled", "Started", "Unknown" };
            OutcomeBox.SelectedIndex = 0;
        }
        public async void OnNavigatedTo() => await RefreshAsync();
        async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
        async Task RefreshAsync()
        {
            if (_loading) return;
            _loading = true; RefreshButton.IsEnabled = false;
            try { _records = await Task.Run(() => MaintenanceHistory.Load()); ApplyFilter(); }
            finally
            {
                _loading = false; RefreshButton.IsEnabled = true;
                HistoryWarning.Visibility = string.IsNullOrEmpty(MaintenanceHistory.LastError) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        void Filter_Changed(object sender, TextChangedEventArgs e) { if (RecordsGrid != null) ApplyFilter(); }
        void Outcome_Changed(object sender, SelectionChangedEventArgs e) { if (RecordsGrid != null) ApplyFilter(); }
        void ApplyFilter()
        {
            var selected = (RecordsGrid.SelectedItem as MaintenanceRecord)?.Id;
            var query = (SearchBox.Text ?? "").Trim();
            var filter = OutcomeBox.SelectedItem as string;
            _shown = _records.Where(r => (filter == null || filter == "All results" || r.Outcome.ToString() == filter) &&
                (query.Length == 0 || Contains(r.Operation, query) || Contains(r.Summary, query) || Contains(r.Source, query) ||
                 r.Items.Any(i => Contains(i.Label, query) || Contains(i.Status, query) || Contains(i.Detail, query)))).ToList();
            RecordsGrid.ItemsSource = _shown;
            RecordsGrid.SelectedItem = _shown.FirstOrDefault(r => r.Id == selected) ?? _shown.FirstOrDefault();
            EmptyText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = _records.Count == 0 ? "No maintenance history yet." : "No operations match these filters.";
            CountText.Text = $"{_shown.Count:N0} of {_records.Count:N0} recent operations · stored locally";
            if (_shown.Count == 0) ShowRecord(null);
        }
        static bool Contains(string value, string query) => value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        void Record_Changed(object sender, SelectionChangedEventArgs e) => ShowRecord(RecordsGrid.SelectedItem as MaintenanceRecord);
        void ShowRecord(MaintenanceRecord record)
        {
            DetailTitle.Text = record?.Operation ?? "Select an operation";
            DetailSummary.Text = record == null ? "Operation details will appear here." : record.OutcomeText + " · " + record.StartedText +
                (record.FinishedUtc.HasValue ? " → " + record.FinishedUtc.Value.ToLocalTime().ToString("g") : " · completion not recorded") +
                (string.IsNullOrWhiteSpace(record.Summary) ? "" : "\n" + record.Summary) +
                (record.OmittedItems > 0 ? $"\n{record.OmittedItems:N0} additional item details were summarized to keep this record bounded." : "");
            DetailTotals.Text = record?.TotalsText ?? "";
            ItemsGrid.ItemsSource = record?.Items;
            ItemsGrid.SelectedIndex = record?.Items.Count > 0 ? 0 : -1;
            if (record == null || record.Items.Count == 0) ShowItem(null);
        }
        void Item_Changed(object sender, SelectionChangedEventArgs e) => ShowItem(ItemsGrid.SelectedItem as MaintenanceItem);
        void ShowItem(MaintenanceItem item)
        {
            ItemDetail.Text = item == null ? "" : (item.Detail ?? "") +
                (item.Skipped > 0 || item.Failures > 0 ? $"\nSkipped: {item.Skipped:N0} · Failed: {item.Failures:N0}" : "") +
                (item.BackupPaths.Count > 0 ? "\nRecorded backup locations:\n" + string.Join("\n", item.BackupPaths) : "");
            var action = RecoveryAction(item?.Recovery ?? RecoveryKind.None);
            RecoveryButton.Content = action;
            RecoveryButton.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
        }
        static string RecoveryAction(RecoveryKind kind)
        {
            switch (kind)
            {
                case RecoveryKind.RecycleBin: return "Open Recycle Bin";
                case RecoveryKind.RegistryBackup: return "Open registry backups";
                case RecoveryKind.Startup: return "Review startup apps";
                case RecoveryKind.Restore: return "Open System Restore";
                default: return null;
            }
        }
        void Recovery_Click(object sender, RoutedEventArgs e)
        {
            // History is untrusted local data: only fixed, known destinations are actionable.
            // Backup paths are displayed/exported, never executed or opened from a history entry.
            switch ((ItemsGrid.SelectedItem as MaintenanceItem)?.Recovery ?? RecoveryKind.None)
            {
                case RecoveryKind.RecycleBin: ShellOps.Open("shell:RecycleBinFolder"); break;
                case RecoveryKind.RegistryBackup: Ui.Go("restore"); break;
                case RecoveryKind.Startup: Ui.Go("startup"); break;
                case RecoveryKind.Restore: Ui.Go("restore"); break;
            }
        }
        void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { FileName = "OpenSweep-history-" + DateTime.Now.ToString("yyyyMMdd") + ".xml", Filter = "XML history (*.xml)|*.xml", DefaultExt = ".xml", AddExtension = true };
            if (dialog.ShowDialog() != true) return;
            try { MaintenanceHistory.Export(_shown, dialog.FileName); Ui.Info("History exported. The file may contain personal paths and backup locations."); }
            catch (Exception ex) { Ui.Error("Could not export history:\n" + ex.Message); }
        }
    }
}
