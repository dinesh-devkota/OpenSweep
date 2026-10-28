using System;
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
        bool _running, _autoChecked;

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
            if (!_autoChecked && !_running && !AppState.Instance.AppUpdatesChecked && Winget.IsAvailable)
            {
                _autoChecked = true;
                await CheckAsync();
            }
        }

        static int Pending => AppState.Instance.AppUpdates.Count(a => !(a.Status ?? "").StartsWith("Updated"));

        void UpdateState()
        {
            WingetMissing.Visibility = Winget.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
            if (_running) return;
            if (!Winget.IsAvailable)
            {
                StateTitle.Text = "Windows Package Manager is missing";
                StateText.Text = "Install App Installer from the Microsoft Store to enable software updates.";
            }
            else if (!AppState.Instance.AppUpdatesChecked)
            {
                StateTitle.Text = "Not checked yet";
                StateText.Text = "Click Check for Updates.";
            }
            else if (Pending == 0)
            {
                StateTitle.Text = "Your programs are up to date";
                StateText.Text = "winget found no newer versions of your installed programs.";
            }
            else
            {
                StateTitle.Text = Fmt.Count(Pending, "update") + " available";
                StateText.Text = "Untick anything you don't want to update, then click Update selected.";
            }
        }

        void SetRunning(bool running, bool indeterminate = true)
        {
            _running = running;
            CheckButton.IsEnabled = UpdateButton.IsEnabled = SilentBox.IsEnabled = UnknownBox.IsEnabled = !running;
            Progress.IsIndeterminate = indeterminate;
            Progress.Visibility = CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = true;
        }

        async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

        public async Task CheckAsync()
        {
            if (_running) return;
            if (!Winget.IsAvailable) { UpdateState(); return; }
            _cts = new CancellationTokenSource();
            SetRunning(true);
            StateTitle.Text = "Checking for updates…";
            StateText.Text = "Asking Windows Package Manager which installed programs have newer versions.";
            string error = null;
            using (Ui.Busy())
            {
                try
                {
                    var list = await Winget.ListAsync(AppSettings.Current.IncludeUnknownVersions, _cts.Token);
                    AppState.Instance.AppUpdates.ReplaceWith(list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase));
                    AppState.Instance.AppUpdatesChecked = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Write("winget check failed: " + ex);
                    error = ex.Message;
                }
            }
            SetRunning(false);
            UpdateState();
            if (error != null)
            {
                StateTitle.Text = "Couldn't check for updates";
                StateText.Text = error;
            }
        }

        async void Update_Click(object sender, RoutedEventArgs e)
        {
            var apps = AppState.Instance.AppUpdates.Where(a => a.IsChecked && !(a.Status ?? "").StartsWith("Updated")).ToList();
            if (apps.Count == 0) { Ui.Info("Tick the programs you want to update first."); return; }
            if (!Ui.Confirm($"Update {Fmt.Count(apps.Count, "program")}?\n\nSave your work first – installers may close the programs they update. " +
                            "Some installers will ask for administrator permission.", "Update programs"))
                return;

            _cts = new CancellationTokenSource();
            SetRunning(true, false);
            Progress.Value = 0;
            LogExpander.IsExpanded = true;
            IProgress<string> log = new Progress<string>(line =>
            {
                var t = line.Trim();
                if (t.Length < 3 || t.IndexOf('█') >= 0 || t.IndexOf('▒') >= 0) return;
                Ui.AppendLog(LogBox, t);
            });

            int ok = 0, done = 0;
            bool restart = false;
            using (Ui.Busy())
            {
                foreach (var a in apps)
                {
                    if (_cts.IsCancellationRequested) { a.Status = "Cancelled"; continue; }
                    StateTitle.Text = $"Updating {done + 1} of {apps.Count}: {a.Name}";
                    StateText.Text = $"{a.Version} → {a.Available}";
                    a.Status = "Updating…";
                    log.Report($"=== {a.Name} ({a.Id}) {a.Version} -> {a.Available}");
                    try
                    {
                        var (success, status) = await Winget.UpgradeAsync(a, AppSettings.Current.SilentAppUpdates, log.Report, _cts.Token);
                        a.Status = status;
                        if (success)
                        {
                            ok++;
                            if (status.IndexOf("restart", StringComparison.OrdinalIgnoreCase) >= 0) restart = true;
                        }
                    }
                    catch (OperationCanceledException) { a.Status = "Cancelled"; }
                    catch (Exception ex) { a.Status = "Failed – " + ex.Message; }
                    done++;
                    Progress.Value = done * 100.0 / apps.Count;
                }
            }
            SetRunning(false);
            StateTitle.Text = $"Updated {ok} of {Fmt.Count(apps.Count, "program")}";
            StateText.Text = restart ? "Restart your PC to finish some of the updates."
                : ok < apps.Count ? "See the Status column and the installer output below for details."
                : "All selected programs were updated.";
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
        }

        void All_Click(object sender, RoutedEventArgs e)
        {
            foreach (var a in AppState.Instance.AppUpdates) a.IsChecked = true;
        }

        void None_Click(object sender, RoutedEventArgs e)
        {
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
