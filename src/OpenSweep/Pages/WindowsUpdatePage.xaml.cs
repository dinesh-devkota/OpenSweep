using System;
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
        bool _running;

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
            if (reboot) RebootBanner.Visibility = Visibility.Visible;
        }

        static int Pending => AppState.Instance.WinUpdates.Count(u => !(u.Status ?? "").StartsWith("Installed"));

        void UpdateState()
        {
            if (_running) return;
            if (!AppState.Instance.WinUpdatesChecked)
            {
                StateTitle.Text = "Not checked yet";
                StateText.Text = "Click Check for Updates. Searching can take a few minutes.";
            }
            else if (Pending == 0)
            {
                StateTitle.Text = "Windows is up to date";
                StateText.Text = "No updates are waiting to be installed.";
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
            CheckButton.IsEnabled = InstallButton.IsEnabled = OnlineBox.IsEnabled = !running;
            Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.Visibility = running && cancellable ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = true;
        }

        async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

        public async Task CheckAsync()
        {
            if (_running) return;
            SetRunning(true);
            StateTitle.Text = "Checking for updates…";
            StateText.Text = "Windows Update is searching. This can take a few minutes – you can keep using OpenSweep.";
            string error = null;
            using (Ui.Busy())
            {
                try
                {
                    var list = await WindowsUpdateService.SearchAsync(AppSettings.Current.WindowsUpdateOnline, CancellationToken.None);
                    AppState.Instance.WinUpdates.ReplaceWith(list.OrderBy(u => u.IsOptional).ThenBy(u => u.Title));
                    AppState.Instance.WinUpdatesChecked = true;
                }
                catch (COMException ex) { error = "Windows Update: " + WindowsUpdateService.Describe(ex.HResult); }
                catch (Exception ex) { error = ex.Message; }
            }
            SetRunning(false);
            UpdateState();
            if (error != null)
            {
                Log.Write("Windows Update search failed: " + error);
                StateTitle.Text = "Couldn't check for updates";
                StateText.Text = error;
            }
        }

        async void Install_Click(object sender, RoutedEventArgs e)
        {
            var updates = AppState.Instance.WinUpdates.Where(u => u.IsChecked && !(u.Status ?? "").StartsWith("Installed")).ToList();
            if (updates.Count == 0) { Ui.Info("Tick the updates you want to install first."); return; }
            if (!Elevation.IsAdmin)
            {
                if (Ui.Confirm("Installing Windows updates needs administrator rights.\n\nRestart OpenSweep as administrator now?"))
                    MainWindow.Instance?.RestartElevated();
                return;
            }
            var size = updates.Sum(u => u.Size);
            if (!Ui.Confirm($"Download and install {Fmt.Count(updates.Count, "update")}" + (size > 0 ? $" (up to {Fmt.Bytes(size)})" : "") +
                            "?\n\nYou can keep using your PC while they install.", "Install updates"))
                return;

            _cts = new CancellationTokenSource();
            SetRunning(true, cancellable: true);
            StateTitle.Text = "Installing updates…";
            StateText.Text = "Follow progress in the Status column. Cancel stops after the current update.";
            bool reboot = false;
            using (Ui.Busy())
            {
                try { reboot = await WindowsUpdateService.InstallAsync(updates, _cts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Write("Windows Update install failed: " + ex);
                    Ui.Error("Installing updates failed:\n" + ex.Message);
                }
            }
            SetRunning(false);
            var ok = updates.Count(u => (u.Status ?? "").StartsWith("Installed"));
            StateTitle.Text = $"Installed {ok} of {Fmt.Count(updates.Count, "update")}";
            StateText.Text = reboot ? "Restart your PC to finish installing."
                : ok < updates.Count ? "See the Status column for details." : "All selected updates were installed.";
            if (reboot || await Task.Run(() => WindowsUpdateService.RebootRequired)) RebootBanner.Visibility = Visibility.Visible;
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
        }

        void Restart_Click(object sender, RoutedEventArgs e)
        {
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
            foreach (var u in AppState.Instance.WinUpdates) u.IsChecked = true;
        }

        void None_Click(object sender, RoutedEventArgs e)
        {
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
