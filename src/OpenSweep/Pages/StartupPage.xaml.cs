using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class StartupPage : UserControl, IPage
    {
        readonly ObservableCollection<StartupEntry> _items = new ObservableCollection<StartupEntry>();
        readonly List<string> _sourceWarnings = new List<string>();
        ICollectionView _view;
        bool _loaded;
        bool _working;

        public StartupPage()
        {
            InitializeComponent();
            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = Matches;
            EntriesGrid.ItemsSource = _view;
            UpdateCount();
        }

        public async void OnNavigatedTo()
        {
            if (!_loaded) await RefreshAsync();
        }

        void SetWorking(bool working)
        {
            _working = working;
            EntriesGrid.IsEnabled = FilterPanel.IsEnabled = ActionsPanel.IsEnabled = !working;
            LoadProgress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
            UpdateCount();
        }

        async Task RefreshAsync()
        {
            if (_working) return;
            SetWorking(true);
            StatusText.Text = "Reading Run keys and startup folders…";
            try
            {
                var result = await Task.Run(() => StartupManager.LoadDetailed());
                _items.ReplaceWith(result.Entries.OrderBy(e => e.State == StartupState.Unknown ? 2 : e.Enabled ? 0 : 1)
                    .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase));
                _sourceWarnings.Clear();
                _sourceWarnings.AddRange(result.Warnings.Distinct());
                _loaded = true;
                StatusText.Text = "";
            }
            catch (Exception ex)
            {
                Log.Write("Startup list failed: " + ex);
                _sourceWarnings.Clear();
                _sourceWarnings.Add("Refresh failed: " + ex.Message);
                // Retained rows are useful for inspection but must not be mistaken for a current read.
                foreach (var entry in _items) entry.State = StartupState.Unknown;
                StatusText.Text = _items.Count > 0 ? "Refresh failed. Previously loaded entries are shown with unknown status." : "Could not read the startup list.";
            }
            finally
            {
                _view.Refresh();
                SourceWarning.Visibility = _sourceWarnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                SetWorking(false);
            }
        }

        bool Matches(object item)
        {
            if (!(item is StartupEntry entry)) return false;
            StartupState? state = null;
            if (StateFilter?.SelectedItem is ComboBoxItem filter && filter.Tag is string tag &&
                Enum.TryParse(tag, out StartupState parsed)) state = parsed;
            return StartupFilter.Matches(entry.Name, entry.Publisher, entry.Command, entry.State, SearchBox?.Text, state);
        }

        void ApplyFilter()
        {
            _view?.Refresh();
            UpdateCount();
        }

        void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
        void StateFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

        void UpdateCount()
        {
            if (CountText == null || _view == null) return;
            var shown = _view.Cast<StartupEntry>().ToList();
            int enabled = shown.Count(e => e.State == StartupState.Enabled);
            int disabled = shown.Count(e => e.State == StartupState.Disabled);
            int unknown = shown.Count - enabled - disabled;
            CountText.Text = $"{shown.Count} of {_items.Count} entries · {enabled} enabled · {disabled} disabled" +
                (unknown > 0 ? $" · {unknown} unknown" : "");
            ExportButton.IsEnabled = !_working && shown.Count > 0;
            EmptyState.Visibility = shown.Count == 0 && !_working ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Text = _items.Count > 0 ? "No entries match your search and status filter." :
                _sourceWarnings.Count > 0 ? "Startup entries could not be read. See the details above." :
                "No entries were found in the Run keys or startup folders.";
        }

        static string Explain(Exception ex) =>
            ex is UnauthorizedAccessException || ex is SecurityException
                ? "access was denied; an all-users item may require restarting OpenSweep as administrator"
                : ex.Message;

        void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb) || !(cb.DataContext is StartupEntry entry)) return;
            if (_working)
            {
                cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, entry.EnabledState);
                return;
            }
            bool want = cb.IsChecked == true;
            var label = $"{entry.Name} ({entry.Location})";
            var previous = entry.State;
            using var history = MaintenanceHistory.Begin(want ? "Enable startup entry" : "Disable startup entry");
            SetWorking(true);
            try
            {
                StartupManager.SetEnabled(entry, want);
                history.AddItem(new MaintenanceItem { Label = label, Status = want ? "Enabled" : "Disabled",
                    Outcome = MaintenanceOutcome.Completed, Detail = $"Previous state: {previous}. Change this entry again from Startup apps.",
                    Recovery = RecoveryKind.Startup });
                history.Complete(MaintenanceOutcome.Completed, "Startup approval was changed.");
            }
            catch (Exception ex)
            {
                history.AddItem(new MaintenanceItem { Label = label, Status = "Change failed", Outcome = MaintenanceOutcome.Failed,
                    Failures = 1, Detail = Explain(ex) });
                history.Complete(MaintenanceOutcome.Failed, "Startup approval could not be changed.");
                cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, entry.EnabledState);
                Ui.Warn($"“{entry.Name}” could not be changed: {Explain(ex)}.");
            }
            finally { _view.Refresh(); SetWorking(false); }
        }

        void SetSelected(bool enabled)
        {
            if (_working) return;
            var selected = Ui.Selected<StartupEntry>(EntriesGrid).Select(entry => new
                { Entry = entry, Label = $"{entry.Name} ({entry.Location})", Previous = entry.State }).ToList();
            if (selected.Count == 0) { Ui.Info("Select one or more entries in the list first."); return; }
            var errors = new List<string>();
            int changed = 0, skipped = 0;
            using var history = MaintenanceHistory.Begin(enabled ? "Enable startup entries" : "Disable startup entries");
            SetWorking(true);
            try
            {
                foreach (var target in selected)
                {
                    if (target.Previous != StartupState.Unknown && target.Entry.Enabled == enabled)
                    {
                        skipped++;
                        history.AddItem(new MaintenanceItem { Label = target.Label, Status = "Already in the requested state",
                            Outcome = MaintenanceOutcome.Unknown, Skipped = 1, Detail = "No startup change was needed." });
                        continue;
                    }
                    try
                    {
                        StartupManager.SetEnabled(target.Entry, enabled);
                        changed++;
                        history.AddItem(new MaintenanceItem { Label = target.Label, Status = enabled ? "Enabled" : "Disabled",
                            Outcome = MaintenanceOutcome.Completed, Detail = $"Previous state: {target.Previous}. Change this entry again from Startup apps.",
                            Recovery = RecoveryKind.Startup });
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{target.Label}: {Explain(ex)}");
                        history.AddItem(new MaintenanceItem { Label = target.Label, Status = "Change failed", Outcome = MaintenanceOutcome.Failed,
                            Failures = 1, Detail = Explain(ex) });
                    }
                }
                history.Complete(errors.Count == 0 && skipped == 0 ? MaintenanceOutcome.Completed :
                    errors.Count == selected.Count ? MaintenanceOutcome.Failed : MaintenanceOutcome.Partial,
                    $"Changed {changed} startup entries; {skipped} already in the requested state; {errors.Count} failed.");
            }
            finally { _view.Refresh(); SetWorking(false); }
            if (errors.Count > 0) Ui.Warn("Some entries could not be changed:\n\n" + string.Join("\n", errors));
        }

        void Enable_Click(object sender, RoutedEventArgs e) => SetSelected(true);
        void Disable_Click(object sender, RoutedEventArgs e) => SetSelected(false);

        async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_working) return;
            var selected = Ui.Selected<StartupEntry>(EntriesGrid).Select(entry => new
                { Entry = entry, Label = $"{entry.Name} ({entry.Location})", IsRegistry = entry.IsRegistry, Id = Guid.NewGuid().ToString("N") }).ToList();
            if (selected.Count == 0) { Ui.Info("Select one or more entries in the list first."); return; }
            var names = string.Join("\n", selected.Take(10).Select(x => "• " + x.Label)) + (selected.Count > 10 ? "\n…" : "");
            if (!Ui.ConfirmWarn($"Delete {Fmt.Count(selected.Count, "startup entry", "startup entries")}?\n\n{names}\n\n" +
                                "The programs stay installed. Registry entries are backed up and shortcuts go to the Recycle Bin. " +
                                "Disable is usually the safer choice.", "Delete startup entries")) return;

            var errors = new List<string>();
            int cancelled = 0;
            var removed = new List<StartupEntry>();
            using var history = MaintenanceHistory.Begin("Delete startup entries");
            SetWorking(true);
            StatusText.Text = "Removing selected startup entries…";
            try
            {
                using (Ui.Busy())
                {
                    foreach (var target in selected)
                    {
                        string backup = null;
                        history.AddItem(new MaintenanceItem { Id = target.Id, Label = target.Label, Status = "Removal started",
                            Outcome = MaintenanceOutcome.Started, Detail = "The entry removal has not returned a result yet." });
                        try
                        {
                            backup = await Task.Run(() => StartupManager.Delete(target.Entry, path =>
                            {
                                backup = path;
                                history.AddItem(new MaintenanceItem { Id = target.Id, Label = target.Label, Status = "Backup created; removal pending",
                                    Outcome = MaintenanceOutcome.Started, Detail = "The backup was written before removal. The removal result is not yet known.",
                                    Recovery = RecoveryKind.RegistryBackup, BackupPaths = new List<string> { path } });
                            }));
                            history.AddItem(new MaintenanceItem { Id = target.Id, Label = target.Label, Status = "Entry removed",
                                Outcome = MaintenanceOutcome.Completed,
                                Detail = target.IsRegistry ? "The startup registry entry was removed; the program remains installed." :
                                    "The startup shortcut was removed. Inspect the Recycle Bin; recovery is available only if Windows retained it.",
                                Recovery = backup != null ? RecoveryKind.RegistryBackup : RecoveryKind.RecycleBin,
                                BackupPaths = backup != null ? new List<string> { backup } : new List<string>() });
                            removed.Add(target.Entry);
                        }
                        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                        {
                            cancelled++;
                            errors.Add($"{target.Label}: cancelled");
                            history.AddItem(new MaintenanceItem { Id = target.Id, Label = target.Label, Status = "Cancelled",
                                Outcome = MaintenanceOutcome.Cancelled, Detail = "The Windows permission prompt was cancelled.",
                                Recovery = backup == null ? RecoveryKind.None : RecoveryKind.RegistryBackup,
                                BackupPaths = backup == null ? new List<string>() : new List<string> { backup } });
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{target.Label}: {Explain(ex)}");
                            history.AddItem(new MaintenanceItem { Id = target.Id, Label = target.Label, Status = "Removal failed", Outcome = MaintenanceOutcome.Failed,
                                Failures = 1, Detail = Explain(ex), Recovery = backup == null ? RecoveryKind.None : RecoveryKind.RegistryBackup,
                                BackupPaths = backup == null ? new List<string>() : new List<string> { backup } });
                        }
                    }
                }
                history.Complete(errors.Count == 0 ? MaintenanceOutcome.Completed :
                    cancelled == selected.Count ? MaintenanceOutcome.Cancelled :
                    errors.Count == selected.Count ? MaintenanceOutcome.Failed : MaintenanceOutcome.Partial,
                    $"Removed {selected.Count - errors.Count} of {selected.Count} startup entries.");
                foreach (var entry in removed) _items.Remove(entry);
                StatusText.Text = $"Removed {removed.Count} of {selected.Count} selected entries.";
            }
            catch (Exception ex)
            {
                Log.Write("Startup removal results refresh failed: " + ex);
                Ui.Warn("Startup removal results were recorded, but the list could not be refreshed. See History.");
            }
            finally { _view.Refresh(); SetWorking(false); }
            if (errors.Count > 0) Ui.Warn("Some entries could not be deleted:\n\n" + string.Join("\n", errors));
        }

        void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_working) return;
            var rows = _view.Cast<StartupEntry>().Select(entry => new StartupReportRow
            {
                Name = entry.Name, Publisher = entry.Publisher, State = entry.State,
                Location = entry.Location, Command = entry.Command, FilePath = entry.FilePath,
            }).ToList();
            if (rows.Count == 0) { Ui.Info("There are no visible entries to export."); return; }
            var dialog = new SaveFileDialog
            {
                Title = "Export visible startup entries", Filter = "CSV files (*.csv)|*.csv",
                DefaultExt = ".csv", AddExtension = true, FileName = $"OpenSweep-startup-{DateTime.Now:yyyyMMdd}.csv",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                File.WriteAllText(dialog.FileName, StartupReport.Csv(rows), new UTF8Encoding(true));
                StatusText.Text = $"Exported {Fmt.Count(rows.Count, "visible entry", "visible entries")}.";
            }
            catch (Exception ex) { Ui.Error("Could not export startup entries:\n" + ex.Message); }
        }

        void WarningDetails_Click(object sender, RoutedEventArgs e) =>
            Ui.Info(string.Join("\n\n", _sourceWarnings), "Startup read details");

        void OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (_working || !Ui.IsRowOrButton(EntriesGrid, e)) return;
            if (!(EntriesGrid.SelectedItem is StartupEntry entry)) { Ui.Info("Select an entry in the list first."); return; }
            if (!string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath)) ShellOps.ShowInExplorer(entry.FilePath);
            else if (!string.IsNullOrEmpty(entry.ShortcutPath) && File.Exists(entry.ShortcutPath)) ShellOps.ShowInExplorer(entry.ShortcutPath);
            else Ui.Info("The program file was not found:\n\n" + (entry.FilePath ?? entry.Command));
        }

        async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
        void Settings_Click(object sender, RoutedEventArgs e) => ShellOps.Open("ms-settings:startupapps");
    }
}
