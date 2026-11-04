using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public sealed class BackupRow
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public long Bytes { get; set; }
        public string SizeText => Fmt.Bytes(Bytes);
    }

    public partial class RestorePage : UserControl, IPage
    {
        bool _loading;
        bool _creating;

        public RestorePage()
        {
            InitializeComponent();
            DescriptionBox.Text = "OpenSweep restore point";
            AdminNote.Visibility = Elevation.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
            CreateButton.IsEnabled = Elevation.IsAdmin;
        }

        public async void OnNavigatedTo()
        {
            LoadBackups();
            await LoadPointsAsync();
        }

        async Task LoadPointsAsync()
        {
            if (!Elevation.IsAdmin)
            {
                PointsText.Text = "Restart OpenSweep as administrator to see and create restore points. “Open System Restore…” works either way.";
                return;
            }
            if (_loading) return;
            _loading = true;
            PointsText.Text = "Loading restore points…";
            try
            {
                var list = await Task.Run(() => SystemRestore.List());
                PointsGrid.ItemsSource = list;
                PointsText.Text = list.Count == 0
                    ? "There are no restore points yet. If System Protection is off, turn it on in System Protection settings."
                    : Fmt.Count(list.Count, "restore point") + ". To roll back to one, use “Open System Restore…”.";
            }
            catch (Exception ex)
            {
                Log.Write("Restore point list failed: " + ex.Message);
                PointsText.Text = "Couldn't read the restore points: " + ex.Message;
            }
            finally { _loading = false; }
        }

        async void Create_Click(object sender, RoutedEventArgs e)
        {
            if (_creating) return;
            if (!Elevation.IsAdmin) { Elevate_Click(sender, e); return; }
            var desc = DescriptionBox.Text.Trim();
            if (desc.Length == 0) desc = "OpenSweep restore point";

            using var history = MaintenanceHistory.Begin("Create system restore point");
            _creating = true;
            CreateButton.IsEnabled = false;
            CreateProgress.Visibility = Visibility.Visible;
            PointsText.Text = "Creating a restore point – this can take a minute…";
            string error;
            try
            {
                using (Ui.Busy()) error = await Task.Run(() => SystemRestore.Create(desc));
                history.AddItem(new MaintenanceItem { Label = desc,
                    Status = error == null ? "Windows reported success" : "Creation failed",
                    Outcome = error == null ? MaintenanceOutcome.Completed : MaintenanceOutcome.Failed,
                    Detail = error ?? "Windows reported successful restore-point creation. Review available restore points in System Restore before relying on one.",
                    Failures = error == null ? 0 : 1, Recovery = error == null ? RecoveryKind.Restore : RecoveryKind.None });
                history.Complete(error == null ? MaintenanceOutcome.Completed : MaintenanceOutcome.Failed,
                    error ?? "Windows accepted the restore-point creation request.");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                history.AddItem(new MaintenanceItem { Label = desc, Status = "Creation failed", Outcome = MaintenanceOutcome.Failed,
                    Detail = ex.Message, Failures = 1 });
                history.Complete(MaintenanceOutcome.Failed, "Restore-point creation did not complete.");
            }
            finally
            {
                _creating = false;
                CreateProgress.Visibility = Visibility.Collapsed;
                CreateButton.IsEnabled = Elevation.IsAdmin;
            }
            await LoadPointsAsync();
            if (error != null) Ui.Warn(error);
            else PointsText.Text = "Windows reported restore-point creation success. " + PointsText.Text;
        }

        void Elevate_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.RestartElevated();
        void OpenRestore_Click(object sender, RoutedEventArgs e) => SystemRestore.OpenSystemRestore();
        void Protection_Click(object sender, RoutedEventArgs e) => SystemRestore.OpenProtectionSettings();

        void LoadBackups()
        {
            var rows = SystemRestore.RegistryBackups()
                .Select(f => new BackupRow { Path = f.FullName, Name = f.Name, Created = f.LastWriteTime, Bytes = f.Length })
                .ToList();
            BackupsGrid.ItemsSource = rows;
            BackupsText.Text = rows.Count == 0
                ? "None yet – OpenSweep saves one automatically before every registry fix and removed entry."
                : Fmt.Count(rows.Count, "backup") + " · saved automatically before registry changes";
        }

        BackupRow SelectedBackup()
        {
            if (BackupsGrid.SelectedItem is BackupRow b) return b;
            Ui.Info("Select a backup in the list first.");
            return null;
        }

        void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            var b = SelectedBackup();
            if (b == null) return;
            var name = b.Name;
            var path = b.Path;
            if (!Ui.ConfirmWarn($"Put back the registry entries saved in “{name}”?\n\n" +
                                "Every key and value in the backup is written back. Windows may ask for administrator permission.",
                                "Restore registry backup")) return;
            using var history = MaintenanceHistory.Begin("Registry backup import");
            try
            {
                RegistryBackup.Import(path);
                history.AddItem(new MaintenanceItem { Label = name, Status = "Regedit launched", Outcome = MaintenanceOutcome.Unknown,
                    Detail = "Regedit was launched to import this backup. Import completion and registry changes were not verified. No automatic rollback of this import is available." });
                history.Complete(MaintenanceOutcome.Unknown, "Registry import was launched; completion is unverified.");
                BackupsText.Text = "Regedit was launched for " + name + ". Import completion is not verified.";
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                history.AddItem(new MaintenanceItem { Label = name, Status = "Cancelled", Outcome = MaintenanceOutcome.Cancelled,
                    Detail = "The Windows permission prompt was cancelled." });
                history.Complete(MaintenanceOutcome.Cancelled, "Registry import was not launched.");
            }
            catch (Exception ex)
            {
                history.AddItem(new MaintenanceItem { Label = name, Status = "Launch failed", Outcome = MaintenanceOutcome.Failed,
                    Detail = ex.Message, Failures = 1 });
                history.Complete(MaintenanceOutcome.Failed, "Registry import could not be launched.");
                Ui.Error("Could not start Regedit:\n" + ex.Message);
            }
        }

        void ViewBackup_Click(object sender, RoutedEventArgs e)
        {
            if (!Ui.IsRowOrButton(BackupsGrid, e)) return;
            if (e is MouseButtonEventArgs && !(BackupsGrid.SelectedItem is BackupRow)) return;
            var b = SelectedBackup();
            if (b == null) return;
            try { Process.Start("notepad.exe", $"\"{b.Path}\""); }
            catch (Exception ex) { Ui.Error("Could not open the backup:\n" + ex.Message); }
        }

        void OpenBackups_Click(object sender, RoutedEventArgs e) => ShellOps.ShowInExplorer(AppPaths.BackupDir);
    }
}
