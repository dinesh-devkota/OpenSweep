using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class SoftwareUpdaterPage : UserControl, IPage
    {
        CancellationTokenSource _cts;
        bool _running, _autoChecked, _checkCancelled;
        string _checkError;
        Task _checkTask;
        bool _installing, _restartRequired;
        AppUpdate _activeApp;
        readonly HashSet<string> _restartBlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool RestartRequired => _restartRequired || AppState.Instance.AppUpdates.Any(a => a.RestartRequired);

        public SoftwareUpdaterPage()
        {
            InitializeComponent();
            AppsGrid.ItemsSource = AppState.Instance.AppUpdates;
            SilentBox.IsChecked = AppSettings.Current.SilentAppUpdates;
            UnknownBox.IsChecked = AppSettings.Current.IncludeUnknownVersions;
            UpdateState();
        }

        public async void OnNavigatedTo()
        {
            SilentBox.IsChecked = AppSettings.Current.SilentAppUpdates;
            UnknownBox.IsChecked = AppSettings.Current.IncludeUnknownVersions;
            UpdateState();
            if (!_autoChecked && !_running && !Ui.ExternalInstallerUnconfirmed && !AppState.Instance.AppUpdatesChecked && Winget.IsAvailable)
            {
                _autoChecked = true;
                await CheckAsync();
            }
        }

        static int Pending => AppState.Instance.AppUpdates.Count(a => a.Outcome != UpdateOutcome.Succeeded && !a.RestartRequired);

        void UpdateState()
        {
            WingetMissing.Visibility = Winget.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
            RestartBanner.Visibility = RestartRequired ? Visibility.Visible : Visibility.Collapsed;
            if (_running) return;
            UpdateButton.IsEnabled = !Ui.ExternalInstallerUnconfirmed && Winget.IsAvailable && AppState.Instance.AppUpdatesChecked && Pending > 0;
            RetryButton.IsEnabled = !Ui.ExternalInstallerUnconfirmed && AppState.Instance.AppUpdatesChecked && AppState.Instance.AppUpdates.Any(a => a.CanRetry);
            if (Ui.ExternalInstallerUnconfirmed)
            {
                StateTitle.Text = "Installer completion needs review";
                StateText.Text = "A previous installer did not confirm completion. Finish it first, then use Check for Updates to acknowledge and refresh the installed versions.";
            }
            else if (!Winget.IsAvailable)
            {
                StateTitle.Text = "Windows Package Manager is missing";
                StateText.Text = "Install App Installer from the Microsoft Store to enable software updates.";
            }
            else if (!AppState.Instance.AppUpdatesChecked)
            {
                StateTitle.Text = _checkCancelled ? "Update check cancelled" : _checkError != null ? "Couldn't check for updates" : "Not checked yet";
                StateText.Text = _checkCancelled ? "No results from the cancelled check can be installed. Check again when ready."
                    : _checkError ?? "Click Check for Updates.";
            }
            else if (RestartRequired)
            {
                StateTitle.Text = "Restart required";
                StateText.Text = "An installer requested a Windows restart. Restart before retrying affected apps." +
                    (Pending > 0 ? $" {Fmt.Count(Pending, "other update")} can still be selected." : " Check again after restarting.");
            }
            else if (Pending == 0 && AppState.Instance.AppUpdates.Count > 0)
            {
                StateTitle.Text = "Listed updates completed";
                StateText.Text = "The listed updates have completed. Check again to refresh available versions.";
            }
            else if (Pending == 0)
            {
                StateTitle.Text = "No updates listed";
                StateText.Text = "The last Windows Package Manager check listed no newer versions for the apps it reports.";
            }
            else
            {
                StateTitle.Text = Fmt.Count(Pending, "update") + " available";
                StateText.Text = "Untick anything you don't want to update, then click Update selected.";
            }
        }

        void SetRunning(bool running)
        {
            _running = running;
            CheckButton.IsEnabled = SilentBox.IsEnabled = UnknownBox.IsEnabled = !running;
            UpdateButton.IsEnabled = !running && !Ui.ExternalInstallerUnconfirmed && Winget.IsAvailable && AppState.Instance.AppUpdatesChecked && Pending > 0;
            RetryButton.IsEnabled = !running && !Ui.ExternalInstallerUnconfirmed && AppState.Instance.AppUpdatesChecked && AppState.Instance.AppUpdates.Any(a => a.CanRetry);
            AppsGrid.IsEnabled = !running;
            Progress.IsIndeterminate = true;
            CancelButton.Content = _installing ? "Cancel remaining" : "Cancel check";
            Progress.Visibility = CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
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
                if (!Ui.ConfirmWarn("A previous installer did not report completion.\n\nHave you confirmed that the installer has finished or closed? Continue only after it has stopped; this check does not undo its changes.", "Confirm installer finished")) return;
                Ui.AcknowledgeExternalInstallerFinished();
            }
            // A refresh invalidates the old candidate list immediately. Failed/cancelled
            // checks must not leave an earlier success eligible for installation.
            _autoChecked = true;
            _checkError = null;
            _checkCancelled = false;
            AppState.Instance.AppUpdatesChecked = false;
            AppState.Instance.AppUpdates.Clear();
            if (!Winget.IsAvailable) { UpdateState(); return; }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetRunning(true);
            StateTitle.Text = "Checking for updates…";
            StateText.Text = "Asking Windows Package Manager which installed programs have newer versions.";
            using (var history = MaintenanceHistory.Begin("Check application updates"))
            using (Ui.Busy())
            {
                try
                {
                    var list = await Winget.ListAsync(AppSettings.Current.IncludeUnknownVersions, token);
                    token.ThrowIfCancellationRequested();
                    foreach (var app in list)
                        if (_restartBlocked.Contains(AppIdentity(app)))
                        {
                            app.RestartRequired = true;
                            app.Status = "Restart required before another attempt";
                        }
                    AppState.Instance.AppUpdates.ReplaceWith(list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase));
                    AppState.Instance.AppUpdatesChecked = true;
                    history.Complete(MaintenanceOutcome.Completed, $"Listed {list.Count} available updates. No apps were installed.");
                }
                catch (OperationCanceledException) { _checkCancelled = true; history.Complete(MaintenanceOutcome.Cancelled, "Update check cancelled; results discarded."); }
                catch (Exception ex)
                {
                    Log.Write("winget check failed: " + ex);
                    _checkError = ex.Message;
                    history.Complete(MaintenanceOutcome.Failed, ex.Message);
                }
                finally
                {
                    _cts.Dispose();
                    _cts = null;
                    SetRunning(false);
                    UpdateState();
                }
            }
        }

        async void Update_Click(object sender, RoutedEventArgs e) => await RunUpdatesAsync(false);
        async void Retry_Click(object sender, RoutedEventArgs e) => await RunUpdatesAsync(true);

        async Task RunUpdatesAsync(bool retryFailed)
        {
            if (_running) return;
            if (Ui.ExternalInstallerUnconfirmed)
            {
                Ui.Warn("Confirm the previous installer has finished, then use Check for Updates before starting another installation.");
                return;
            }
            if (Ui.IsBusy)
            {
                Ui.Info("Wait for the current operation to finish before starting application updates.");
                return;
            }
            if (!AppState.Instance.AppUpdatesChecked)
            {
                Ui.Info("Complete a successful update check before choosing programs to install.");
                return;
            }
            var apps = AppState.Instance.AppUpdates.Where(a => retryFailed ? a.CanRetry
                : a.IsChecked && a.Outcome != UpdateOutcome.Succeeded && a.Outcome != UpdateOutcome.Unknown && !a.RestartRequired).ToList();
            if (apps.Count == 0) { Ui.Info(retryFailed ? "There are no failed updates eligible for retry." : "Tick the programs you want to update first."); return; }
            if (!Ui.Confirm($"{(retryFailed ? "Retry" : "Update")} {Fmt.Count(apps.Count, "program")}?\n\nSave your work first. Installers may close apps or request administrator permission. Cancel remaining lets the current installer finish and skips the rest.", "Update programs")) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _installing = true;
            SetRunning(true);
            LogExpander.IsExpanded = true;
            var silent = AppSettings.Current.SilentAppUpdates;
            IProgress<string> log = new Progress<string>(line => Ui.AppendLog(LogBox, line));
            foreach (var app in apps) { app.Phase = UpdatePhase.Queued; app.Status = "Queued"; }
            UpdateBatchResult batch = null;
            using (var history = MaintenanceHistory.Begin("Application updates", retryFailed ? "Retry failed" : "Manual"))
            using (Ui.Busy())
            {
                try
                {
                    batch = await UpdateQueue.RunAsync(apps, async (app, index) =>
                    {
                        _activeApp = app;
                        app.Attempts++;
                        app.Outcome = UpdateOutcome.Running;
                        app.Phase = UpdatePhase.InstallerManaged;
                        app.Status = "Downloading / installing — managed by winget";
                        StateTitle.Text = $"Updating {index + 1} of {apps.Count}: {app.Name}";
                        StateText.Text = $"{app.Version} → {app.Available}. Windows Package Manager controls download and installer progress; no percentage is available.";
                        UpdateHistory.Started(history, app.Id, app.Name, AppDetail(app));
                        log.Report($"=== {app.Name} ({app.Id}), attempt {app.Attempts}: {app.Version} -> {app.Available}");
                        token.ThrowIfCancellationRequested();
                        // Persist before launch: if OpenSweep closes, another instance must
                        // not assume this publisher-controlled installer has finished.
                        Ui.MarkExternalInstallerUnconfirmed();
                        try
                        {
                            var result = await Winget.UpgradeAsync(app, silent, log.Report, token);
                            if (result != null && result.Outcome != UpdateOutcome.Unknown &&
                                result.Outcome != UpdateOutcome.Pending && result.Outcome != UpdateOutcome.Running)
                                Ui.AcknowledgeExternalInstallerFinished();
                            return result;
                        }
                        catch (OperationCanceledException)
                        {
                            // Winget checks this token only before starting the installer.
                            Ui.AcknowledgeExternalInstallerFinished();
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Log.Write("Application installer completion could not be observed: " + ex);
                            return new UpdateResult(UpdateOutcome.Unknown, "Installer completion is unknown — " + ex.Message);
                        }
                    }, (app, result) =>
                    {
                        app.Outcome = result.Outcome;
                        app.Phase = UpdatePhase.Finished;
                        app.Status = result.Message;
                        app.RestartRequired |= result.RestartRequired;
                        _restartRequired |= result.RestartRequired;
                        if (app.RestartRequired) _restartBlocked.Add(AppIdentity(app));
                        if (result.Outcome == UpdateOutcome.Unknown) Ui.MarkExternalInstallerUnconfirmed();
                        UpdateHistory.Finished(history, app.Id, app.Name, AppDetail(app), result);
                        _activeApp = null;
                    }, token);
                    history.Complete(UpdateHistory.Outcome(batch, token.IsCancellationRequested), UpdateHistory.Summary(batch));
                }
                catch (Exception ex)
                {
                    Log.Write("Update queue failed: " + ex);
                    history.Complete(MaintenanceOutcome.Unknown, "Queue completion could not be confirmed: " + ex.Message);
                    if (_activeApp != null) Ui.MarkExternalInstallerUnconfirmed();
                    Ui.Error("The update queue could not complete:\n" + ex.Message);
                }
                finally
                {
                    _activeApp = null;
                    _installing = false;
                    _cts.Dispose();
                    _cts = null;
                    SetRunning(false);
                }
            }
            if (batch == null) { UpdateState(); return; }
            _restartRequired |= batch.RestartRequired;
            RestartBanner.Visibility = _restartRequired ? Visibility.Visible : Visibility.Collapsed;
            StateTitle.Text = Ui.ExternalInstallerUnconfirmed ? "Installer completion needs review" : "Application update queue finished";
            StateText.Text = UpdateHistory.Summary(batch) + (Ui.ExternalInstallerUnconfirmed
                ? " Finish the installer, then Check for Updates before starting anything else."
                : " Check again to refresh installed versions. Retry failed retries only failed items that do not require a restart.");
        }

        static string AppDetail(AppUpdate app) =>
            $"Package {app.Id}; listed version {app.Version}; offered version {app.Available}; attempt {app.Attempts}";

        static string AppIdentity(AppUpdate app) => app.Id + "|" + app.Source;

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            if (_installing)
            {
                StateTitle.Text = "Waiting for the current installer";
                StateText.Text = "The current installer is allowed to finish. Remaining apps will be skipped; already completed changes are kept.";
                if (_activeApp != null) _activeApp.Status = "Waiting for current installer; remaining queue cancelled";
            }
            else StateText.Text = "Cancelling the update check…";
        }

        void RestartSettings_Click(object sender, RoutedEventArgs e) => WindowsUpdateService.OpenSettings();

        void All_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            foreach (var a in AppState.Instance.AppUpdates) a.IsChecked = true;
        }

        void None_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            foreach (var a in AppState.Instance.AppUpdates) a.IsChecked = false;
        }

        void Options_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Current.SilentAppUpdates = SilentBox.IsChecked == true;
            AppSettings.Current.IncludeUnknownVersions = UnknownBox.IsChecked == true;
            AppSettings.Current.Save();
        }

        void GetWinget_Click(object sender, RoutedEventArgs e) => ShellOps.Open(Winget.StoreLink);
    }
}
