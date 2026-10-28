using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class OptionsPage : UserControl, IPage
    {
        const string LicenseText =
            "MIT License\n\nCopyright (c) 2026 OpenSweep contributors\n\n" +
            "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation " +
            "files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy, " +
            "modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software " +
            "is furnished to do so, subject to the following conditions:\n\n" +
            "The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.\n\n" +
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE " +
            "WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR " +
            "COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, " +
            "ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.";

        public OptionsPage()
        {
            InitializeComponent();
            VersionText.Text = "OpenSweep " + SelfInstaller.Version;
            DataText.Text = "Settings, logs and registry backups are kept in " + AppPaths.DataDir;
        }

        public async void OnNavigatedTo()
        {
            var s = AppSettings.Current;
            OldTempBox.IsChecked = s.OnlyOldTempFiles;
            CloseAppsBox.IsChecked = s.CloseAppsAutomatically;
            RestorePointBox.IsChecked = s.RestorePointBeforeRegistryFix;
            SilentBox.IsChecked = s.SilentAppUpdates;
            UnknownBox.IsChecked = s.IncludeUnknownVersions;
            OnlineBox.IsChecked = s.WindowsUpdateOnline;
            SystemText.Text = SystemInfo.Summary;
            RefreshExclusions();
            ScheduleBox.SelectedItem = ScheduleBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Schedule)
                                       ?? ScheduleBox.Items[0];
            TimeBox.Text = s.ScheduleTime;
            UpdateInstallState();
            await RefreshScheduleAsync();
        }

        void Setting_Click(object sender, RoutedEventArgs e)
        {
            var s = AppSettings.Current;
            s.OnlyOldTempFiles = OldTempBox.IsChecked == true;
            s.CloseAppsAutomatically = CloseAppsBox.IsChecked == true;
            s.RestorePointBeforeRegistryFix = RestorePointBox.IsChecked == true;
            s.SilentAppUpdates = SilentBox.IsChecked == true;
            s.IncludeUnknownVersions = UnknownBox.IsChecked == true;
            s.WindowsUpdateOnline = OnlineBox.IsChecked == true;
            s.Save();
        }

        // ---- Excluded folders ----

        void RefreshExclusions()
        {
            var list = AppSettings.Current.ExcludedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            ExclusionsList.ItemsSource = list;
            ExclusionsEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void AddExclusion_Click(object sender, RoutedEventArgs e)
        {
            var path = Ui.PickFolder("Choose a folder that OpenSweep should never clean.");
            if (path == null) return;
            var s = AppSettings.Current;
            if (s.IsExcluded(path)) { Ui.Info("That folder is already excluded."); return; }
            // A new parent folder makes exclusions inside it redundant.
            s.ExcludedPaths.RemoveAll(p => string.IsNullOrWhiteSpace(p) || IsInside(p, path));
            s.ExcludedPaths.Add(path);
            s.ExcludedPaths.Sort(StringComparer.OrdinalIgnoreCase);
            s.Save();
            RefreshExclusions();
        }

        static bool IsInside(string path, string folder)
        {
            folder = folder.TrimEnd('\\');
            return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                   (path.Length == folder.Length || path[folder.Length] == '\\');
        }

        void RemoveExclusion_Click(object sender, RoutedEventArgs e)
        {
            var selected = ExclusionsList.SelectedItems.OfType<string>().ToList();
            if (selected.Count == 0) { Ui.Info("Select one or more folders in the list first."); return; }
            AppSettings.Current.ExcludedPaths.RemoveAll(p => selected.Contains(p));
            AppSettings.Current.Save();
            RefreshExclusions();
        }

        void Exclusion_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ExclusionsList, d) is ListBoxItem item &&
                item.Content is string path && Directory.Exists(path))
                ShellOps.Open(path);
        }

        // ---- Scheduled cleaning ----

        async Task RefreshScheduleAsync()
        {
            ScheduleStatus.Text = "Checking Task Scheduler…";
            bool exists;
            try { exists = await Scheduler.ExistsAsync(); }
            catch (Exception ex)
            {
                Log.Write("Schedule check: " + ex.Message);
                ScheduleStatus.Text = "Couldn't read Task Scheduler.";
                return;
            }
            var s = AppSettings.Current;
            ScheduleStatus.Text = !exists ? "Automatic cleaning is off."
                : s.Schedule == "Off" ? $"A task named \"{Scheduler.TaskName}\" exists in Task Scheduler. Choose Off and click Apply to remove it."
                : $"Automatic cleaning is on: {(s.Schedule == "Weekly" ? "every Sunday" : "every day")} at {s.ScheduleTime}.";
        }

        async void ApplySchedule_Click(object sender, RoutedEventArgs e)
        {
            var schedule = (ScheduleBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Off";
            var time = TimeBox.Text.Trim();
            ApplyButton.IsEnabled = false;
            try
            {
                AppState.Instance.SaveChecked();
                var error = await Scheduler.ApplyAsync(schedule, time, SelfInstaller.IsInstalled ? SelfInstaller.InstalledExe : null);
                if (error != null) { Ui.Warn("Couldn't change the schedule:\n\n" + error); return; }
                var s = AppSettings.Current;
                s.Schedule = schedule;
                if (schedule != "Off" && TimeSpan.TryParse(time, out var ts)) s.ScheduleTime = $"{ts.Hours:00}:{ts.Minutes:00}";
                s.Save();
                TimeBox.Text = s.ScheduleTime;
                await RefreshScheduleAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Schedule: " + ex);
                Ui.Error("Couldn't change the schedule:\n\n" + ex.Message);
            }
            finally { ApplyButton.IsEnabled = true; }
        }

        // ---- Install ----

        void UpdateInstallState()
        {
            if (SelfInstaller.IsRunningInstalledCopy)
                InstallStatus.Text = $"OpenSweep {SelfInstaller.Version} is installed for this user in {SelfInstaller.InstallDir}.";
            else if (SelfInstaller.IsInstalled)
                InstallStatus.Text = $"An installed copy is in {SelfInstaller.InstallDir}, but this window runs from {AppPaths.ExePath}. " +
                                     $"Updating replaces the installed copy with this version ({SelfInstaller.Version}).";
            else
                InstallStatus.Text = $"Running as a portable app from {AppPaths.ExePath}. Installing copies OpenSweep to " +
                                     $"{SelfInstaller.InstallDir} and adds it to the Start menu and to Installed apps. " +
                                     "Administrator rights aren't needed.";
            InstallButton.Content = SelfInstaller.IsRunningInstalledCopy ? "Repair shortcuts"
                                  : SelfInstaller.IsInstalled ? "Update installed copy" : "Install";
            UninstallButton.IsEnabled = SelfInstaller.IsInstalled;
            ScheduleHint.Visibility = SelfInstaller.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
            ScheduleHintText.Text = "Scheduled cleaning will start OpenSweep from " + AppPaths.ExePath +
                                    ". If that file is moved or deleted the schedule stops working, so consider installing OpenSweep below first.";
        }

        async void Install_Click(object sender, RoutedEventArgs e)
        {
            bool repair = SelfInstaller.IsRunningInstalledCopy;
            InstallButton.IsEnabled = false;
            try
            {
                try { SelfInstaller.Install(DesktopBox.IsChecked == true); }
                catch (Exception ex)
                {
                    Log.Write("Install failed: " + ex);
                    Ui.Error("Couldn't install OpenSweep:\n\n" + ex.Message +
                             (ex is IOException ? "\n\nClose any other OpenSweep window and try again." : ""));
                    return;
                }
                // Point an existing schedule at the installed copy so it keeps working if this file is moved.
                var s = AppSettings.Current;
                if (s.Schedule != "Off" && await Scheduler.ExistsAsync())
                    await Scheduler.ApplyAsync(s.Schedule, s.ScheduleTime, SelfInstaller.InstalledExe);
                UpdateInstallState();
                await RefreshScheduleAsync();
            }
            finally { InstallButton.IsEnabled = true; }

            if (repair) { Ui.Info("The shortcuts and the Installed apps entry were repaired."); return; }
            if (!Ui.Confirm("OpenSweep is installed and can be started from the Start menu.\n\n" +
                            "Close this window and switch to the installed copy now?")) return;
            try
            {
                Process.Start(SelfInstaller.InstalledExe);
                Application.Current.Shutdown();
            }
            catch (Exception ex) { Ui.Error("Couldn't start the installed copy:\n\n" + ex.Message); }
        }

        void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            var r = Ui.Show("Remove the installed copy of OpenSweep, its shortcuts and the scheduled cleaning task?\n\n" +
                            "Yes – also delete settings, logs and registry backups\nNo – keep settings and backups\nCancel – keep OpenSweep",
                            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, "Uninstall OpenSweep");
            if (r != MessageBoxResult.Yes && r != MessageBoxResult.No) return;
            bool removeData = r == MessageBoxResult.Yes;
            bool runningInstalled = SelfInstaller.IsRunningInstalledCopy;
            try { SelfInstaller.Uninstall(removeData); }
            catch (Exception ex)
            {
                Log.Write("Uninstall failed: " + ex);
                Ui.Error("Couldn't uninstall OpenSweep:\n\n" + ex.Message);
                return;
            }

            if (runningInstalled)
            {
                Ui.Info("OpenSweep has been removed and will close now.", "Uninstall OpenSweep");
                Application.Current.Shutdown();
                return;
            }
            ScheduleBox.SelectedIndex = 0;
            UpdateInstallState();
            _ = RefreshScheduleAsync();
            Ui.Info("The installed copy has been removed." +
                    (removeData ? "\n\nSettings, logs and registry backups will be deleted when you close OpenSweep." : ""),
                    "Uninstall OpenSweep");
        }

        void DataFolder_Click(object sender, RoutedEventArgs e) => ShellOps.Open(AppPaths.DataDir);
        void LogFolder_Click(object sender, RoutedEventArgs e) => ShellOps.Open(AppPaths.LogDir);
        void License_Click(object sender, RoutedEventArgs e) => Ui.Info(LicenseText, "OpenSweep license");

        void Website_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/dinesh-devkota/OpenSweep");

        void Donate_Click(object sender, RoutedEventArgs e) => OpenUrl("https://www.paypal.biz/alinafunddevkota");

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Ui.Error("Couldn't open your browser:\n" + url + "\n\n" + ex.Message); }
        }
    }
}
