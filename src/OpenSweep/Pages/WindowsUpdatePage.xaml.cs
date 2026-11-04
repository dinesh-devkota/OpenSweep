using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class WindowsUpdatePage : UserControl, IPage
    {
        CancellationTokenSource _cts;
        bool _running, _checkCancelled;
        string _checkError;
        Task _checkTask;
        bool _installing, _restartRequired;
        WinUpdate _activeUpdate;
        readonly HashSet<string> _restartBlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool RestartRequired => _restartRequired || AppState.Instance.WinUpdates.Any(u => u.RestartRequired);

        public WindowsUpdatePage()
        {
            InitializeComponent();
            UpdatesGrid.ItemsSource = AppState.Instance.WinUpdates;
            OnlineBox.IsChecked = AppSettings.Current.WindowsUpdateOnline;
            AdminNote.Visibility = Elevation.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
            UpdateState();
        }

        public async void OnNavigatedTo()
        {
            OnlineBox.IsChecked = AppSettings.Current.WindowsUpdateOnline;
            UpdateState();
            var reboot = await Task.Run(() => WindowsUpdateService.RebootRequired);
            _restartRequired |= reboot;
            UpdateState();
        }

        static int Pending => AppState.Instance.WinUpdates.Count(u => u.Outcome != UpdateOutcome.Succeeded && !u.RestartRequired);

        void UpdateState()
        {
            RebootBanner.Visibility = RestartRequired ? Visibility.Visible : Visibility.Collapsed;
            if (_running) return;
            InstallButton.IsEnabled = !Ui.ExternalInstallerUnconfirmed && AppState.Instance.WinUpdatesChecked && Pending > 0;
            RetryButton.IsEnabled = !Ui.ExternalInstallerUnconfirmed && AppState.Instance.WinUpdatesChecked && AppState.Instance.WinUpdates.Any(u => u.CanRetry);
            if (Ui.ExternalInstallerUnconfirmed)
            {
                StateTitle.Text = "Installer completion needs review";
                StateText.Text = "A previous installation did not confirm completion. Finish it first, then Check for Updates to acknowledge and refresh the result.";
            }
            else if (!AppState.Instance.WinUpdatesChecked)
            {
                StateTitle.Text = _checkCancelled ? "Update check cancelled" : _checkError != null ? "Couldn't check for updates" : "Not checked yet";
                StateText.Text = _checkCancelled ? "Results from the cancelled check were discarded. Check again when ready."
                    : _checkError ?? "Click Check for Updates. Searching can take a few minutes.";
            }
            else if (RestartRequired)
            {
                StateTitle.Text = "Restart required";
                StateText.Text = "Windows reported a pending restart. Restart before retrying affected updates." +
                    (Pending > 0 ? $" {Fmt.Count(Pending, "other update")} can still be selected." : " Check again after restarting.");
            }
            else if (Pending == 0 && AppState.Instance.WinUpdates.Count > 0)
            {
                StateTitle.Text = "Listed updates completed";
                StateText.Text = "The listed updates have completed. Check again to refresh Windows Update availability.";
            }
            else if (Pending == 0)
            {
                StateTitle.Text = "No updates listed";
                StateText.Text = "The last successful Windows Update check listed no pending updates.";
            }
            else
            {
                var optional = AppState.Instance.WinUpdates.Count(u => u.IsOptional);
                StateTitle.Text = Fmt.Count(Pending, "update") + " available";
                StateText.Text = optional > 0
                    ? $"{optional} of them are optional (drivers or previews) and not selected by default."
                    : "Click Install selected to download and install them.";
            }
        }

        void SetRunning(bool running, bool cancellable = false)
        {
            _running = running;
            CheckButton.IsEnabled = OnlineBox.IsEnabled = !running;
            InstallButton.IsEnabled = !running && !Ui.ExternalInstallerUnconfirmed && AppState.Instance.WinUpdatesChecked && Pending > 0;
            RetryButton.IsEnabled = !running && !Ui.ExternalInstallerUnconfirmed && AppState.Instance.WinUpdatesChecked && AppState.Instance.WinUpdates.Any(u => u.CanRetry);
            UpdatesGrid.IsEnabled = !running;
            CancelButton.Content = _installing ? "Cancel remaining" : "Cancel check";
            Progress.IsIndeterminate = true;
            Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.Visibility = running && cancellable ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = true;
        }

        async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

        public Task CheckAsync()
        {
            if (_checkTask != null && !_checkTask.IsCompleted) return _checkTask;
            if (_running) return Task.CompletedTask;
            return _checkTask = CheckCoreAsync();
        }

        async Task CheckCoreAsync()
        {
            if (Ui.ExternalInstallerUnconfirmed)
            {
                if (Ui.HasActiveOperations)
                {
                    Ui.Info("Wait for the active operation to finish before checking installer completion.");
                    return;
                }
                if (!Ui.ConfirmWarn("A previous installation did not report completion.\n\nHave you confirmed that the installer or Windows Update operation has finished? Continue only after it has stopped; checking does not undo its changes.", "Confirm installer finished")) return;
                Ui.AcknowledgeExternalInstallerFinished();
            }
            _checkError = null;
            _checkCancelled = false;
            AppState.Instance.WinUpdatesChecked = false;
            AppState.Instance.WinUpdates.Clear();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetRunning(true, cancellable: true);
            StateTitle.Text = "Checking for updates…";
            StateText.Text = "Windows Update is searching. Cancel discards the results after the current Windows search returns.";
            using (var history = MaintenanceHistory.Begin("Check Windows updates"))
            using (Ui.Busy())
            {
                try
                {
                    var list = await WindowsUpdateService.SearchAsync(AppSettings.Current.WindowsUpdateOnline, token);
                    token.ThrowIfCancellationRequested();
                    foreach (var update in list)
                        if (_restartBlocked.Contains(HistoryId(update)))
                        {
                            update.RestartRequired = true;
                            update.Status = "Restart required before another attempt";
                        }
                    AppState.Instance.WinUpdates.ReplaceWith(list.OrderBy(u => u.IsOptional).ThenBy(u => u.Title));
                    AppState.Instance.WinUpdatesChecked = true;
                    history.Complete(MaintenanceOutcome.Completed, $"Listed {list.Count} Windows updates. No updates were installed.");
                }
                catch (OperationCanceledException) { _checkCancelled = true; history.Complete(MaintenanceOutcome.Cancelled, "Windows update check cancelled; results discarded."); }
                catch (COMException ex) { _checkError = "Windows Update: " + WindowsUpdateService.Describe(ex.HResult); history.Complete(MaintenanceOutcome.Failed, _checkError); }
                catch (Exception ex) { _checkError = ex.Message; history.Complete(MaintenanceOutcome.Failed, _checkError); }
                finally
                {
                    if (_checkError != null) Log.Write("Windows Update search failed: " + _checkError);
                    _cts.Dispose();
                    _cts = null;
                    SetRunning(false);
                    UpdateState();
                }
            }
        }

        async void Install_Click(object sender, RoutedEventArgs e) => await RunUpdatesAsync(false);
        async void Retry_Click(object sender, RoutedEventArgs e) => await RunUpdatesAsync(true);

        async Task RunUpdatesAsync(bool retryFailed)
        {
            if (_running) return;
            if (Ui.ExternalInstallerUnconfirmed)
            {
                Ui.Warn("Confirm the previous installer has finished, then Check for Updates before starting another installation.");
                return;
            }
            if (Ui.IsBusy)
            {
                Ui.Info("Wait for the current operation to finish before starting Windows updates.");
                return;
            }
            if (!AppState.Instance.WinUpdatesChecked)
            {
                Ui.Info("Complete a successful Windows Update check before choosing updates to install.");
                return;
            }
            var updates = AppState.Instance.WinUpdates.Where(u => retryFailed ? u.CanRetry
                : u.IsChecked && u.Outcome != UpdateOutcome.Succeeded && u.Outcome != UpdateOutcome.Unknown && !u.RestartRequired).ToList();
            if (updates.Count == 0) { Ui.Info(retryFailed ? "There are no failed Windows updates eligible for retry." : "Tick the updates you want to install first."); return; }
            if (!Elevation.IsAdmin)
            {
                if (Ui.Confirm("Installing Windows updates needs administrator rights.\n\nRestart OpenSweep as administrator now?"))
                    MainWindow.Instance?.RestartElevated();
                return;
            }
            var size = updates.Sum(u => u.Size);
            if (!Ui.Confirm($"{(retryFailed ? "Retry" : "Download and install")} {Fmt.Count(updates.Count, "update")}" +
                (size > 0 ? $" (up to {Fmt.Bytes(size)})" : "") +
                "?\n\nCancel remaining waits for the current Windows download or installation to finish, then stops. Already completed changes are kept.", "Install updates")) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _installing = true;
            SetRunning(true, cancellable: true);
            foreach (var update in updates) { update.Phase = UpdatePhase.Queued; update.Status = "Queued"; }
            UpdateBatchResult batch = null;
            using (var history = MaintenanceHistory.Begin("Windows updates", retryFailed ? "Retry failed" : "Manual"))
            using (Ui.Busy())
            {
                try
                {
                    batch = await UpdateQueue.RunAsync(updates, async (update, index) =>
                    {
                        _activeUpdate = update;
                        update.Attempts++;
                        update.Outcome = UpdateOutcome.Running;
                        StateTitle.Text = $"Windows update {index + 1} of {updates.Count}: {update.Title}";
                        StateText.Text = "Preparing the update. Windows reports the current phase; no percentage is available.";
                        UpdateHistory.Started(history, HistoryId(update), update.Title, UpdateDetail(update));
                        IProgress<UpdatePhase> phase = new Progress<UpdatePhase>(value =>
                        {
                            if (update.Outcome != UpdateOutcome.Running) return;
                            update.Phase = value;
                            update.Status = UpdatePhases.Describe(value) + "…";
                            if (token.IsCancellationRequested)
                                StateText.Text = "Cancel requested. Waiting for the current Windows phase; remaining updates will be skipped.";
                            else StateText.Text = value == UpdatePhase.Downloading
                                ? "Downloading through Windows Update. Cancel waits for this download and prevents installation."
                                : "Windows is installing this update. Cancel remaining allows this installation to finish.";
                        });
                        token.ThrowIfCancellationRequested();
                        // Cover the download-to-install boundary as well: Windows may
                        // continue working if OpenSweep closes during this operation.
                        Ui.MarkExternalInstallerUnconfirmed();
                        try
                        {
                            var result = await WindowsUpdateService.InstallOneAsync(update, token, phase);
                            if (result != null && result.Outcome != UpdateOutcome.Unknown &&
                                result.Outcome != UpdateOutcome.Pending && result.Outcome != UpdateOutcome.Running)
                                Ui.AcknowledgeExternalInstallerFinished();
                            return result;
                        }
                        catch (Exception ex)
                        {
                            Log.Write("Windows installer completion could not be observed: " + ex);
                            return new UpdateResult(UpdateOutcome.Unknown, "Installation completion is unknown — " + ex.Message);
                        }
                    }, (update, result) =>
                    {
                        update.Outcome = result.Outcome;
                        update.Phase = UpdatePhase.Finished;
                        update.Status = result.Message;
                        update.RestartRequired |= result.RestartRequired;
                        _restartRequired |= result.RestartRequired;
                        if (update.RestartRequired) _restartBlocked.Add(HistoryId(update));
                        if (result.Outcome == UpdateOutcome.Unknown) Ui.MarkExternalInstallerUnconfirmed();
                        UpdateHistory.Finished(history, HistoryId(update), update.Title, UpdateDetail(update), result);
                        _activeUpdate = null;
                    }, token);
                    history.Complete(UpdateHistory.Outcome(batch, token.IsCancellationRequested), UpdateHistory.Summary(batch));
                    _restartRequired |= batch.RestartRequired;
                    _restartRequired |= await Task.Run(() => WindowsUpdateService.RebootRequired);
                }
                catch (Exception ex)
                {
                    Log.Write("Windows update queue failed: " + ex);
                    history.Complete(MaintenanceOutcome.Unknown, "Windows queue completion could not be confirmed: " + ex.Message);
                    if (_activeUpdate != null) Ui.MarkExternalInstallerUnconfirmed();
                    Ui.Error("The Windows update queue could not complete:\n" + ex.Message);
                }
                finally
                {
                    _activeUpdate = null;
                    _installing = false;
                    _cts.Dispose();
                    _cts = null;
                    SetRunning(false);
                }
            }
            if (batch == null) { UpdateState(); return; }
            RebootBanner.Visibility = _restartRequired ? Visibility.Visible : Visibility.Collapsed;
            StateTitle.Text = Ui.ExternalInstallerUnconfirmed ? "Installation completion needs review" : "Windows update queue finished";
            StateText.Text = UpdateHistory.Summary(batch) + (Ui.ExternalInstallerUnconfirmed
                ? " Finish the Windows operation, then check again before another installation."
                : " Check again to refresh availability. Retry failed includes only failed items that do not require a restart.");
        }

        static string HistoryId(WinUpdate update) => string.IsNullOrEmpty(update.UpdateId) ? update.Title + "|" + update.Kb : update.UpdateId;
        static string UpdateDetail(WinUpdate update) => $"{update.Kb}; update {update.UpdateId}; revision {update.Revision}; attempt {update.Attempts}";

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            if (_installing)
            {
                StateTitle.Text = _activeUpdate?.Phase == UpdatePhase.Downloading ? "Waiting for the current download" : "Waiting for the current Windows operation";
                StateText.Text = "Remaining updates will be skipped after the current phase returns. An active installation is allowed to finish.";
            }
            else StateText.Text = "Cancel requested. Waiting for the current Windows search to return before discarding its results.";
        }

        void Restart_Click(object sender, RoutedEventArgs e)
        {
            if (Ui.IsBusy)
            {
                Ui.Warn("Wait for the active operation to finish and confirm any unreported installer completion before restarting.");
                return;
            }
            if (!Ui.ConfirmWarn("Restart the PC in 10 seconds?\n\nSave your work and close your programs first.", "Restart")) return;
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                    "/r /t 10 /c \"Restarting to finish installing updates\"") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex) { Ui.Error("Could not restart:\n" + ex.Message); }
        }

        void All_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            foreach (var u in AppState.Instance.WinUpdates) u.IsChecked = true;
        }

        void None_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            foreach (var u in AppState.Instance.WinUpdates) u.IsChecked = false;
        }

        void Settings_Click(object sender, RoutedEventArgs e) => WindowsUpdateService.OpenSettings();

        void Online_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Current.WindowsUpdateOnline = OnlineBox.IsChecked == true;
            AppSettings.Current.Save();
        }
    }
}
