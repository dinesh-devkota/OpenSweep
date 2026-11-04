using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class SelfUpdatePage : UserControl, IPage
    {
        SelfUpdateReadiness _readiness;
        SelfUpdateRelease _release;
        SelfUpdateDownload _download;
        CancellationTokenSource _cts;
        bool _busy;
        int _operation;
        Task _trustTask;

        public SelfUpdatePage()
        {
            InitializeComponent();
            CurrentVersionText.Text = "Installed version " + SelfUpdateService.CurrentVersion;
            StartupCheckBox.IsChecked = AppSettings.Current.CheckForOpenSweepUpdates;
        }

        public async void OnNavigatedTo()
        {
            StartupCheckBox.IsChecked = AppSettings.Current.CheckForOpenSweepUpdates;
            await EnsureTrustAsync(false);
        }

        async Task EnsureTrustAsync(bool refresh)
        {
            if (_trustTask != null) { await _trustTask; return; }
            if (!refresh && _readiness != null) return;
            _trustTask = ReadTrustAsync();
            try { await _trustTask; }
            finally { _trustTask = null; }
        }

        async Task ReadTrustAsync()
        {
            _readiness = null;
            TrustTitle.Text = "Checking installation trust…";
            RefreshButtons();
            try
            {
                _readiness = await SelfUpdateService.GetInstallationReadinessAsync();
                TrustTitle.Text = _readiness.CanInstall ? "Verified downloads are available" : "A trusted signed installation is required";
                TrustText.Text = _readiness.Message;
                TrustBanner.SetResourceReference(Border.BackgroundProperty, _readiness.CanInstall ? "InfoSoft" : "WarnSoft");
            }
            catch
            {
                TrustTitle.Text = "Installation trust could not be checked";
                TrustText.Text = "Downloads remain disabled. Use the official release page to review available versions.";
            }
            finally { RefreshButtons(); }
        }

        void StartupCheck_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Current.CheckForOpenSweepUpdates = StartupCheckBox.IsChecked == true;
            AppSettings.Current.Save();
        }

        async void Check_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (Ui.ExternalInstallerUnconfirmed)
            {
                if (Ui.HasActiveOperations)
                {
                    Ui.Info("An operation is still active. Wait for it to finish before reviewing the previous installer's status.");
                    return;
                }
                if (!Ui.ConfirmWarn("A previous installer may still be running. Confirm in Windows that it has finished or was closed before continuing.\n\nHave you checked that the installer is no longer running?", "Review installer status")) return;
                if (Ui.HasActiveOperations)
                {
                    Ui.Info("An operation started while the confirmation was open. Wait for it to finish.");
                    return;
                }
                Ui.AcknowledgeExternalInstallerFinished();
            }
            SetBusy(true, true);
            _cts = new CancellationTokenSource();
            _download = null;
            _release = null;
            AssetSelector.ItemsSource = null;
            ReleasePanel.Visibility = Visibility.Collapsed;
            ReleaseNotes.Clear();
            _ = EnsureTrustAsync(true);
            StateTitle.Text = "Checking official releases…";
            StateText.Text = "Contacting GitHub for published stable releases.";
            Progress.IsIndeterminate = true;
            try
            {
                _release = await SelfUpdateService.CheckAsync(_cts.Token);
                ShowRelease();
            }
            catch (OperationCanceledException) { StateTitle.Text = "Check cancelled"; StateText.Text = "No files were changed."; }
            catch (Exception ex) { ShowError("Could not check for updates", ex); }
            finally { EndBusy(); }
        }

        void ShowRelease()
        {
            ReleasePanel.Visibility = _release == null ? Visibility.Collapsed : Visibility.Visible;
            if (_release == null)
            {
                StateTitle.Text = "No stable release was found";
                StateText.Text = "You can review all releases on the official GitHub page.";
                return;
            }
            bool newer = SelfUpdatePolicy.IsNewer(_release.Version, SelfUpdateService.CurrentVersion);
            StateTitle.Text = newer ? "OpenSweep " + _release.Version + " is available" : "No newer stable version was found";
            StateText.Text = newer ? "Review the release notes and choose a download format. Installation always needs your confirmation."
                : "Your installed version is the same as or newer than the stable releases returned by GitHub.";
            ReleaseTitle.Text = "Release notes · " + _release.Tag;
            ReleaseNotes.Text = _release.Notes;
            AssetSelector.ItemsSource = _release.Assets;
            AssetSelector.SelectedIndex = _release.Assets.Count > 0 ? 0 : -1;
            UpdateAssetNote();
        }

        void Asset_Changed(object sender, SelectionChangedEventArgs e)
        {
            _download = null;
            if (AssetNote == null) return;
            UpdateAssetNote();
            RefreshButtons();
        }

        void UpdateAssetNote()
        {
            var asset = AssetSelector.SelectedItem as SelfUpdateAsset;
            AssetNote.Text = asset == null ? "This release has no supported asset with a GitHub SHA-256 digest. Use the official release page."
                : asset.IsInstaller ? "The installer opens visibly after verification and confirmation. OpenSweep does not infer installation success from launching it."
                : "The portable file is verified, then shown in its folder. Close OpenSweep and replace your portable copy manually; no replacement script is run.";
        }

        async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _readiness?.CanInstall != true || !(AssetSelector.SelectedItem is SelfUpdateAsset asset)) return;
            if (Ui.IsBusy) { Ui.Info("Wait for the current maintenance task to finish before downloading an OpenSweep update."); return; }
            SetBusy(true, true);
            _cts = new CancellationTokenSource();
            _download = null;
            StateTitle.Text = "Downloading and verifying…";
            StateText.Text = "The file must match GitHub's SHA-256 digest and this app's trusted signing key.";
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            int generation = _operation;
            var downloadToken = _cts.Token;
            var progress = new Progress<SelfUpdateProgress>(p =>
            {
                if (!_busy || generation != _operation || downloadToken.IsCancellationRequested) return;
                Progress.Value = p.Percentage;
                ProgressText.Text = Fmt.Bytes(p.BytesReceived) + " of " + Fmt.Bytes(p.TotalBytes);
            });
            using (Ui.Busy())
            {
                try
                {
                    _download = await SelfUpdateService.DownloadAsync(_release, asset, progress, _cts.Token);
                    StateTitle.Text = "Download verified";
                    StateText.Text = asset.IsInstaller ? "Review and launch the installer when you are ready. Its signature and hash will be checked again before launch."
                        : "The portable file passed digest and signer checks. It will be checked again before its folder is shown.";
                }
                catch (OperationCanceledException) { StateTitle.Text = "Download cancelled"; StateText.Text = "The download was stopped. No installer was launched."; }
                catch (Exception ex) { ShowError("The update could not be verified", ex); }
                finally { EndBusy(); }
            }
        }

        async void Launch_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _download == null || !_download.Asset.IsInstaller) return;
            if (Ui.IsBusy) { Ui.Info("Wait for the current maintenance task to finish before launching the installer."); return; }
            if (!Ui.Confirm("Launch the verified OpenSweep " + _download.Release.Version + " installer?\n\nSave your work first. Follow the installer's prompts; it may ask you to close OpenSweep or approve Windows elevation.", "Update OpenSweep")) return;
            if (Ui.IsBusy) { Ui.Info("A maintenance task started. Wait for it to finish before launching the installer."); return; }
            SetBusy(true, false);
            StateTitle.Text = "Verifying the installer again…";
            StateText.Text = "Checking the file immediately before launch.";
            bool launched = false;
            using (Ui.Busy())
            using (var history = MaintenanceHistory.Begin("OpenSweep installer launch"))
            {
                try
                {
                    int exitCode = await SelfUpdateService.LaunchInstallerAsync(_download, () =>
                    {
                        launched = true;
                        Ui.MarkExternalInstallerUnconfirmed();
                        history.AddItem(new MaintenanceItem { Label = "OpenSweep " + _download.Release.Version, Status = "Launched", Outcome = MaintenanceOutcome.Completed,
                            Detail = "The verified installer was launched. Installation success is not tracked." });
                        Dispatcher.Invoke(() =>
                        {
                            StateTitle.Text = "Installer launched";
                            StateText.Text = "Follow the installer's prompts. OpenSweep will not cancel or stop a running installer.";
                        });
                    }, CancellationToken.None);
                    Ui.AcknowledgeExternalInstallerFinished();
                    history.AddItem(new MaintenanceItem { Label = "Installer process", Status = "Exited with code " + exitCode,
                        Outcome = exitCode == 0 ? MaintenanceOutcome.Completed : MaintenanceOutcome.Partial,
                        Detail = "Exit status was observed; the installed app version was not verified." });
                    history.Complete(exitCode == 0 ? MaintenanceOutcome.Completed : MaintenanceOutcome.Partial,
                        "Installer launched and exited with code " + exitCode + "; installation success is not verified.");
                    StateTitle.Text = "Installer process exited with code " + exitCode;
                    StateText.Text = "OpenSweep cannot confirm whether installation completed. Check the installer result and reopen the app to confirm its version.";
                }
                catch (Win32Exception ex) when (!launched && ex.NativeErrorCode == 1223)
                {
                    history.AddItem(new MaintenanceItem { Label = "OpenSweep update", Status = "Launch cancelled", Outcome = MaintenanceOutcome.Cancelled,
                        Detail = "Windows elevation or launch was cancelled before the installer started." });
                    history.Complete(MaintenanceOutcome.Cancelled, "Installer launch was cancelled.");
                    StateTitle.Text = "Installer launch cancelled";
                    StateText.Text = "The installer was not launched.";
                }
                catch (Exception ex)
                {
                    if (launched) Ui.MarkExternalInstallerUnconfirmed();
                    history.AddItem(new MaintenanceItem { Label = "OpenSweep update", Status = launched ? "Launch observed; completion unknown" : "Not launched",
                        Outcome = launched ? MaintenanceOutcome.Unknown : MaintenanceOutcome.Failed, Detail = launched ? "Installer completion could not be observed." : "Verification or launch failed.", Failures = launched ? 0 : 1 });
                    history.Complete(launched ? MaintenanceOutcome.Unknown : MaintenanceOutcome.Failed, launched ? "Installer was launched; completion is unknown." : "Installer was not launched.");
                    ShowError(launched ? "Installer completion is unknown" : "Installer was not launched", ex);
                }
                finally { EndBusy(); }
            }
        }

        async void Folder_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _download == null || _download.Asset.IsInstaller) return;
            SetBusy(true, false);
            try
            {
                await SelfUpdateService.VerifyPortableAsync(_download);
                ShellOps.ShowInExplorer(_download.FilePath);
                StateTitle.Text = "Verified portable file";
                StateText.Text = "Close OpenSweep before replacing your portable copy. The downloaded file has not been installed or run.";
            }
            catch (Exception ex) { ShowError("The portable file could not be verified", ex); }
            finally { EndBusy(); }
        }

        void OpenRelease_Click(object sender, RoutedEventArgs e) => ShellOps.Open(_release?.ReleaseUri.AbsoluteUri ?? SelfUpdatePolicy.LatestReleaseUrl);
        void Cancel_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); CancelButton.IsEnabled = false; }

        void SetBusy(bool busy, bool cancellable)
        {
            _busy = busy;
            _operation++;
            CheckButton.IsEnabled = AssetSelector.IsEnabled = !busy;
            CancelButton.Visibility = busy && cancellable ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Progress.IsIndeterminate = busy;
            ProgressText.Text = "";
            RefreshButtons();
        }
        void EndBusy()
        {
            _cts?.Dispose(); _cts = null;
            SetBusy(false, false);
        }
        void RefreshButtons()
        {
            if (DownloadButton == null) return;
            DownloadButton.IsEnabled = !_busy && _readiness?.CanInstall == true && AssetSelector.SelectedItem is SelfUpdateAsset &&
                _release != null && SelfUpdatePolicy.IsNewer(_release.Version, SelfUpdateService.CurrentVersion);
            LaunchButton.Visibility = _download?.Asset.IsInstaller == true ? Visibility.Visible : Visibility.Collapsed;
            FolderButton.Visibility = _download != null && !_download.Asset.IsInstaller ? Visibility.Visible : Visibility.Collapsed;
            LaunchButton.IsEnabled = FolderButton.IsEnabled = !_busy;
        }
        void ShowError(string title, Exception ex)
        {
            StateTitle.Text = title;
            StateText.Text = ex.Message;
            Log.Write(title + ": " + ex.GetType().Name);
        }
    }
}
