using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class RegistryPage : UserControl, IPage
    {
        CancellationTokenSource _cts;

        public RegistryPage()
        {
            InitializeComponent();
            IssuesGrid.ItemsSource = AppState.Instance.RegistryIssues;
            AdminNote.Visibility = Elevation.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
            UpdateState();
        }

        public void OnNavigatedTo() => UpdateState();

        void UpdateState()
        {
            var list = AppState.Instance.RegistryIssues;
            if (!AppState.Instance.RegistryScanned)
            {
                StateTitle.Text = "Not scanned yet";
                StateText.Text = "Click Scan for Issues to check the registry.";
            }
            else if (list.Count == 0)
            {
                StateTitle.Text = "No issues found";
                StateText.Text = "Your registry has no leftovers that OpenSweep checks for.";
            }
            else
            {
                StateTitle.Text = Fmt.Count(list.Count, "issue") + " found";
                var locked = Elevation.IsAdmin ? 0 : list.Count(i => i.NeedsAdmin);
                StateText.Text = "Review the list, untick anything you want to keep, then click Fix selected." +
                                 (locked > 0 ? $" {Fmt.Count(locked, "issue")} need administrator rights." : "");
            }
        }

        void SetRunning(bool running)
        {
            ScanButton.IsEnabled = FixButton.IsEnabled = !running;
            Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.Visibility = running && _cts != null ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            if (!running) CurrentText.Text = "";
        }

        async void Scan_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetRunning(true);
            StateTitle.Text = "Scanning the registry…";
            StateText.Text = "";
            var progress = new Progress<string>(s => CurrentText.Text = s);
            using (Ui.Busy())
            {
                try
                {
                    var token = _cts.Token;
                    var issues = await Task.Run(() => RegistryScanner.Scan(progress, token));
                    AppState.Instance.RegistryIssues.ReplaceWith(issues);
                    AppState.Instance.RegistryScanned = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Write("Registry scan failed: " + ex);
                    Ui.Error("The registry scan failed:\n" + ex.Message);
                }
            }
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
            UpdateState();
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
        }

        async void Fix_Click(object sender, RoutedEventArgs e)
        {
            var selected = AppState.Instance.RegistryIssues.Where(i => i.IsChecked).ToList();
            if (selected.Count == 0) { Ui.Info("Tick the issues you want to fix first."); return; }
            var blocked = selected.Where(i => i.NeedsAdmin && !Elevation.IsAdmin).ToList();
            var issues = selected.Except(blocked).ToList();
            if (issues.Count == 0)
            {
                Ui.Warn("The selected issues are machine-wide (HKLM) and need administrator rights.\n\nUse “Restart as administrator” at the top of the window.");
                return;
            }
            var msg = $"Fix {Fmt.Count(issues.Count, "issue")}?\n\nEvery affected key is backed up first, so you can undo this with “Restore a backup…”.";
            if (blocked.Count > 0) msg += $"\n\n{Fmt.Count(blocked.Count, "issue")} need administrator rights and will be skipped.";
            if (!Ui.Confirm(msg, "Fix registry issues")) return;

            SetRunning(true);
            StateTitle.Text = "Fixing…";
            var progress = new Progress<string>(s => CurrentText.Text = s);
            using (Ui.Busy())
            {
                try
                {
                    var (fixedCount, backup) = await FixIssuesAsync(issues, progress, blocked);
                    SetRunning(false);
                    UpdateState();
                    var failed = issues.Count - fixedCount;
                    Ui.Info($"Fixed {Fmt.Count(fixedCount, "issue")}." + (failed > 0 ? $" {failed} could not be fixed (see Status)." : "") +
                            $"\n\nBackup saved to:\n{backup}", "Registry");
                }
                catch (Exception ex)
                {
                    Log.Write("Registry fix failed: " + ex);
                    Ui.Error("Fixing failed:\n" + ex.Message);
                }
            }
            SetRunning(false);
            UpdateState();
        }

        /// <summary>Optional restore point, backup, fix; then removes fixed issues from the shared list.</summary>
        public static async Task<(int Fixed, string Backup)> FixIssuesAsync(List<RegistryIssue> issues, IProgress<string> progress,
            IList<RegistryIssue> skipped = null)
        {
            var targets = issues.Select(issue => new { Issue = issue, Label = issue.Location }).ToList();
            using var history = MaintenanceHistory.Begin("Registry repair");
            foreach (var issue in skipped ?? Array.Empty<RegistryIssue>())
                history.AddItem(new MaintenanceItem { Label = issue.Location, Status = "Skipped", Skipped = 1,
                    Outcome = MaintenanceOutcome.Unknown, Detail = "Administrator rights are required. No change was made." });
            string backup = null;
            bool outcomesRecorded = false, restoreFailed = false;
            foreach (var target in targets) target.Issue.Status = "";
            try
            {
                if (AppSettings.Current.RestorePointBeforeRegistryFix && Elevation.IsAdmin)
                {
                    progress?.Report("Creating a System Restore point…");
                    var error = await Task.Run(() => SystemRestore.Create("OpenSweep registry fix"));
                    history.AddItem(new MaintenanceItem { Label = "Restore point before registry repair",
                        Status = error == null ? "Windows reported success" : "Not created",
                        Outcome = error == null ? MaintenanceOutcome.Completed : MaintenanceOutcome.Failed,
                        Detail = error ?? "Windows reported successful restore-point creation. Review available points in System Restore.",
                        Failures = error == null ? 0 : 1, Recovery = error == null ? RecoveryKind.Restore : RecoveryKind.None });
                    restoreFailed = error != null;
                    if (error != null) Log.Write("Restore point not created: " + error);
                }
                backup = await Task.Run(() => RegistryFixer.Fix(targets.Select(t => t.Issue).ToList(), progress));
                var done = targets.Where(t => t.Issue.Status == "Fixed").ToList();
                foreach (var target in targets)
                    history.AddItem(new MaintenanceItem { Label = target.Label, Status = target.Issue.Status,
                        Outcome = target.Issue.Status == "Fixed" ? MaintenanceOutcome.Completed : MaintenanceOutcome.Failed,
                        Detail = "The affected registry key was backed up before this repair attempt.",
                        Failures = target.Issue.Status == "Fixed" ? 0 : 1,
                        Recovery = RecoveryKind.RegistryBackup, BackupPaths = new List<string> { backup } });
                outcomesRecorded = true;
                history.Complete(done.Count == targets.Count && (skipped?.Count ?? 0) == 0 && !restoreFailed ? MaintenanceOutcome.Completed :
                    done.Count > 0 ? MaintenanceOutcome.Partial : MaintenanceOutcome.Failed,
                    $"Repaired {done.Count} of {targets.Count} attempted registry issues; {skipped?.Count ?? 0} skipped.");
                foreach (var target in done) AppState.Instance.RegistryIssues.Remove(target.Issue);
                return (done.Count, backup);
            }
            catch (Exception ex)
            {
                if (outcomesRecorded) throw;
                foreach (var target in targets)
                    history.AddItem(new MaintenanceItem { Label = target.Label, Status = target.Issue.Status == "Fixed" ? "Fixed" : "Repair did not complete",
                        Outcome = target.Issue.Status == "Fixed" ? MaintenanceOutcome.Completed : MaintenanceOutcome.Unknown,
                        Detail = ex.Message, Failures = target.Issue.Status == "Fixed" ? 0 : 1,
                        BackupPaths = backup == null ? new List<string>() : new List<string> { backup },
                        Recovery = backup == null ? RecoveryKind.None : RecoveryKind.RegistryBackup });
                history.Complete(targets.Any(t => t.Issue.Status == "Fixed") ? MaintenanceOutcome.Partial : MaintenanceOutcome.Failed, ex.Message);
                throw;
            }
        }

        void All_Click(object sender, RoutedEventArgs e)
        {
            foreach (var i in AppState.Instance.RegistryIssues) i.IsChecked = true;
        }

        void None_Click(object sender, RoutedEventArgs e)
        {
            foreach (var i in AppState.Instance.RegistryIssues) i.IsChecked = false;
        }

        void OpenKey_Click(object sender, RoutedEventArgs e)
        {
            if (!Ui.IsRowOrButton(IssuesGrid, e)) return;
            if (!(IssuesGrid.SelectedItem is RegistryIssue issue)) { Ui.Info("Select an issue in the list first."); return; }
            var full = (issue.Hive == "HKLM" ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + "\\" + issue.KeyPath;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                    k?.SetValue("LastKey", "Computer\\" + full);
            }
            catch (Exception ex) { Log.Write("Regedit LastKey: " + ex.Message); }
            ShellOps.Open(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe"));
        }

        void Restore_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Restore a registry backup",
                Filter = "Registry backups (*.reg)|*.reg",
                InitialDirectory = AppPaths.BackupDir
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            if (!Ui.Confirm($"Import “{Path.GetFileName(dlg.FileName)}” into the registry?\n\nThis puts back the keys and values saved in the backup.", "Restore backup"))
                return;
            var file = dlg.FileName;
            using var history = MaintenanceHistory.Begin("Registry backup import");
            try
            {
                RegistryBackup.Import(file);
                history.AddItem(new MaintenanceItem { Label = Path.GetFileName(file), Status = "Regedit launched",
                    Outcome = MaintenanceOutcome.Unknown, Detail = "Regedit was launched to import this backup. Import completion and the resulting registry state were not verified." });
                history.Complete(MaintenanceOutcome.Unknown, "Registry import was launched; completion is unverified.");
                Log.Write("Registry backup import started: " + file);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                history.AddItem(new MaintenanceItem { Label = Path.GetFileName(file), Status = "Cancelled", Outcome = MaintenanceOutcome.Cancelled,
                    Detail = "The Windows permission prompt was cancelled." });
                history.Complete(MaintenanceOutcome.Cancelled, "Registry import was not launched.");
            }
            catch (Exception ex)
            {
                history.AddItem(new MaintenanceItem { Label = Path.GetFileName(file), Status = "Launch failed", Outcome = MaintenanceOutcome.Failed,
                    Detail = ex.Message, Failures = 1 });
                history.Complete(MaintenanceOutcome.Failed, "Registry import could not be launched.");
                Ui.Error("Could not import the backup:\n" + ex.Message);
            }
        }
    }
}
