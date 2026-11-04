using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class UninstallPage : UserControl, IPage
    {
        List<InstalledProgram> _all = new List<InstalledProgram>();
        ICollectionView _view;
        bool _loaded;
        bool _mutating;

        public UninstallPage()
        {
            InitializeComponent();
        }

        public async void OnNavigatedTo()
        {
            if (_loaded) return;
            _loaded = true;
            await RefreshAsync();
        }

        async Task RefreshAsync()
        {
            CountText.Text = "Loading…";
            try
            {
                _all = await Task.Run(() => InstalledPrograms.Load()
                    .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
            }
            catch (Exception ex)
            {
                Log.Write("Program list failed: " + ex);
                Ui.Error("Could not read the installed programs:\n" + ex.Message);
            }
            _view = CollectionViewSource.GetDefaultView(_all);
            _view.Filter = Matches;
            ProgramsGrid.ItemsSource = _view;
            UpdateCount();
        }

        bool Matches(object o)
        {
            var q = SearchBox.Text.Trim();
            if (q.Length == 0) return true;
            var p = (InstalledProgram)o;
            return (p.Name ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   (p.Publisher ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        void UpdateCount()
        {
            var shown = _view?.Cast<InstalledProgram>().ToList() ?? new List<InstalledProgram>();
            var size = shown.Sum(p => p.Size);
            CountText.Text = (shown.Count == _all.Count ? Fmt.Count(_all.Count, "program") : $"{shown.Count} of {_all.Count} programs") +
                             (size > 0 ? $" · {Fmt.Bytes(size)}" : "");
        }

        void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _view?.Refresh();
            UpdateCount();
        }

        InstalledProgram Selected()
        {
            if (ProgramsGrid.SelectedItem is InstalledProgram p) return p;
            Ui.Info("Select a program in the list first.");
            return null;
        }

        async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_mutating || !Ui.IsRowOrButton(ProgramsGrid, e)) return;
            var p = Selected();
            if (p == null) return;
            var label = $"{p.Name} {p.Version}".Trim();
            if (!Ui.Confirm($"Uninstall {p.Name}?\n\nIts own uninstaller will open – follow its steps.", "Uninstall")) return;

            using var history = MaintenanceHistory.Begin("Launch program uninstaller");
            _mutating = true;
            try
            {
                Process proc;
                try
                {
                    proc = InstalledPrograms.Uninstall(p);
                    history.AddItem(new MaintenanceItem { Label = label, Status = proc == null ? "Uninstaller launch requested" : "Uninstaller launched",
                        Outcome = MaintenanceOutcome.Unknown,
                        Detail = "The program's external uninstaller was requested. OpenSweep cannot verify that the program was removed; its own installer may ask additional questions or be cancelled. No automatic recovery is available." });
                    history.Complete(MaintenanceOutcome.Unknown, "External uninstaller launch was requested; removal is not verified.");
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    history.AddItem(new MaintenanceItem { Label = label, Status = "Launch cancelled", Outcome = MaintenanceOutcome.Cancelled,
                        Detail = "The Windows permission prompt was cancelled." });
                    history.Complete(MaintenanceOutcome.Cancelled, "Uninstaller was not launched.");
                    return;
                }
                catch (Exception ex)
                {
                    history.AddItem(new MaintenanceItem { Label = label, Status = "Launch failed", Outcome = MaintenanceOutcome.Failed,
                        Failures = 1, Detail = ex.Message });
                    history.Complete(MaintenanceOutcome.Failed, "Uninstaller could not be launched.");
                    Ui.Error($"Could not start the uninstaller:\n{ex.Message}\n\nIf the program is already gone, use Remove entry to tidy up the list.");
                    return;
                }

                StatusText.Text = $"Waiting for the {p.Name} uninstaller to finish…";
                using (Ui.Busy())
                {
                    using (proc)
                        if (proc != null) await Task.Run(() => { try { proc.WaitForExit(); } catch { } });
                    await Task.Delay(1500);
                }
                StatusText.Text = "The external uninstaller finished or detached. Review its messages; removal was not verified.";
                await RefreshAsync();
            }
            finally { _mutating = false; }
        }

        async void RemoveEntry_Click(object sender, RoutedEventArgs e)
        {
            if (_mutating) return;
            var p = Selected();
            if (p == null) return;
            if (p.Hive == "HKLM" && !Elevation.IsAdmin)
            {
                Ui.Warn("This entry is installed for all users and can only be removed after “Restart as administrator”.");
                return;
            }
            if (!Ui.ConfirmWarn($"Remove “{p.Name}” from the list without running its uninstaller?\n\n" +
                                "Only do this for programs that are already gone. The entry is backed up to a .reg file first.", "Remove entry")) return;
            var label = p.Name;
            var key = p.Hive + "\\" + p.KeyPath;
            using var history = MaintenanceHistory.Begin("Remove installed-program entry");
            var itemId = Guid.NewGuid().ToString("N");
            string backup = null;
            bool mutationCompleted = false;
            history.AddItem(new MaintenanceItem { Id = itemId, Label = label, Status = "Removal started",
                Outcome = MaintenanceOutcome.Started, Detail = "The entry removal has not returned a result yet." });
            _mutating = true;
            try
            {
                using (Ui.Busy()) backup = await Task.Run(() => InstalledPrograms.RemoveEntry(p, path =>
                {
                    backup = path;
                    history.AddItem(new MaintenanceItem { Id = itemId, Label = label, Status = "Backup created; removal pending",
                        Outcome = MaintenanceOutcome.Started, Detail = "The backup was written before removal. The removal result is not yet known.",
                        Recovery = RecoveryKind.RegistryBackup, BackupPaths = new List<string> { path } });
                }));
                mutationCompleted = true;
                history.AddItem(new MaintenanceItem { Id = itemId, Label = label, Status = "Registry entry removed", Outcome = MaintenanceOutcome.Completed,
                    Detail = $"Removed {key}. This removes only the list entry; it does not uninstall the program or delete its files.",
                    Recovery = RecoveryKind.RegistryBackup, BackupPaths = new List<string> { backup } });
                history.Complete(MaintenanceOutcome.Completed, "The program-list registry entry was removed and its backup retained.");
                _all.Remove(p);
                _view?.Refresh();
                UpdateCount();
            }
            catch (Exception ex)
            {
                if (!mutationCompleted)
                {
                    history.AddItem(new MaintenanceItem { Id = itemId, Label = label, Status = "Removal did not complete", Outcome = MaintenanceOutcome.Failed,
                        Failures = 1, Detail = ex.Message, Recovery = backup == null ? RecoveryKind.None : RecoveryKind.RegistryBackup,
                        BackupPaths = backup == null ? new List<string>() : new List<string> { backup } });
                    history.Complete(MaintenanceOutcome.Failed, "The program-list entry could not be removed.");
                }
                Ui.Error(mutationCompleted ? "The entry removal was recorded, but the list could not be refreshed. See History." :
                    "Could not remove the entry:\n" + ex.Message);
            }
            finally { _mutating = false; }
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var p = Selected();
            if (p == null) return;
            var dir = p.InstallLocation?.Trim().Trim('"');
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                var exe = CommandLine.ExePath(p.UninstallString ?? "");
                dir = !string.IsNullOrEmpty(exe) && !exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(exe) : null;
            }
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) ShellOps.ShowInExplorer(dir);
            else Ui.Info("This program doesn't record its install folder.");
        }

        async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (!_mutating) await RefreshAsync();
        }

        void Settings_Click(object sender, RoutedEventArgs e) => ShellOps.Open("ms-settings:appsfeatures");
    }
}
