using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OpenSweep.Core;
using OpenSweep.Pages;

namespace OpenSweep
{
    public partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }

        readonly Dictionary<string, FrameworkElement> _pages = new Dictionary<string, FrameworkElement>();
        readonly Dictionary<string, RadioButton> _nav = new Dictionary<string, RadioButton>();
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        readonly CancellationToken _lifetimeToken;
        bool _closeConfirmed;

        public MainWindow()
        {
            _lifetimeToken = _lifetime.Token;
            Instance = this;
            InitializeComponent();
            // Keep the initial window inside the working area on smaller displays.
            MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
            MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height);

            foreach (var rb in NavPanel.Children.OfType<RadioButton>().Concat(new[] { NavOptions }))
                _nav[(string)rb.CommandParameter] = rb;

            var v = typeof(MainWindow).Assembly.GetName().Version;
            VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}  ·  MIT License";
            if (Elevation.IsAdmin)
            {
                AdminBadge.Visibility = Visibility.Visible;
                Title += " (Administrator)";
            }
            else ElevateButton.Visibility = Visibility.Visible;

            Loaded += OnLoaded;
            Closing += OnClosing;
            Closed += (sender, closedArgs) => _lifetime.Dispose();

            var args = Environment.GetCommandLineArgs();
            var i = Array.FindIndex(args, a => a.Equals("/page", StringComparison.OrdinalIgnoreCase));
            var start = i >= 0 && i + 1 < args.Length && _nav.ContainsKey(args[i + 1].ToLowerInvariant())
                ? args[i + 1].ToLowerInvariant() : "health";
            Navigate(start);
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await SystemInfo.EnsureLoadedAsync();
            if (_lifetimeToken.IsCancellationRequested) return;
            SummaryText.Text = SystemInfo.Summary;
            SummaryText.ToolTip = SystemInfo.Summary;
            await CheckForOpenSweepUpdateAsync();
        }

        async Task CheckForOpenSweepUpdateAsync()
        {
            var settings = AppSettings.Current;
            var now = DateTime.UtcNow;
            var last = settings.LastOpenSweepUpdateCheckUtc;
            // A future timestamp from a clock change must not suppress checks indefinitely.
            if (!settings.CheckForOpenSweepUpdates || (last <= now && now - last < TimeSpan.FromDays(1))) return;
            settings.LastOpenSweepUpdateCheckUtc = now;
            settings.Save();
            var token = _lifetimeToken;
            try
            {
                var release = await SelfUpdateService.CheckAsync(token);
                if (token.IsCancellationRequested || !settings.CheckForOpenSweepUpdates ||
                    release == null || !SelfUpdatePolicy.IsNewer(release.Version, typeof(MainWindow).Assembly.GetName().Version)) return;
                AppUpdateText.Text = $"OpenSweep {release.Version.ToString(3)} is available. Review the release before downloading.";
                AppUpdateBanner.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Write("OpenSweep update notification check failed: " + ex.Message); }
        }

        void AppUpdate_Click(object sender, RoutedEventArgs e)
        {
            AppUpdateBanner.Visibility = Visibility.Collapsed;
            Navigate("selfupdate");
        }

        public string CurrentPage => _nav.FirstOrDefault(p => p.Value.IsChecked == true).Key;

        public void Navigate(string key)
        {
            if (!_nav.TryGetValue(key, out var rb)) return;
            if (rb.IsChecked == true) Show(key);
            else rb.IsChecked = true;
        }

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.CommandParameter is string key) Show(key);
        }

        void Show(string key)
        {
            if (PageHost == null) return;
            if (!_pages.TryGetValue(key, out var page))
            {
                page = Create(key);
                _pages[key] = page;
            }
            PageHost.Content = page;
            (page as IPage)?.OnNavigatedTo();
        }

        public T GetPage<T>(string key) where T : FrameworkElement
        {
            if (!_pages.TryGetValue(key, out var page)) _pages[key] = page = Create(key);
            return page as T;
        }

        static FrameworkElement Create(string key)
        {
            switch (key)
            {
                case "health": return new HealthCheckPage();
                case "care": return new WindowsCarePage();
                case "cleaner": return new CleanerPage();
                case "history": return new HistoryPage();
                case "selfupdate": return new SelfUpdatePage();
                case "registry": return new RegistryPage();
                case "updater": return new SoftwareUpdaterPage();
                case "winupdate": return new WindowsUpdatePage();
                case "startup": return new StartupPage();
                case "uninstall": return new UninstallPage();
                case "disk": return new DiskAnalyzerPage();
                case "dupes": return new DuplicatesPage();
                case "restore": return new RestorePage();
                case "options": return new OptionsPage();
                default: throw new ArgumentException("Unknown page: " + key);
            }
        }

        void SaveState()
        {
            try
            {
                AppState.Instance.SaveChecked();
                AppSettings.Current.Save();
            }
            catch (Exception ex) { Log.Write("Saving settings failed: " + ex.Message); }
        }

        void Elevate_Click(object sender, RoutedEventArgs e) => RestartElevated();

        public void RestartElevated()
        {
            if (Ui.IsBusy && !Ui.ConfirmWarn("A task is still running. Restarting interrupts local tasks; external installers may continue.\n\nRestart as administrator anyway?")) return;
            SaveState();
            if (Elevation.RestartAsAdmin("/page " + (CurrentPage ?? "health")))
            {
                _closeConfirmed = true;
                Application.Current.Shutdown();
            }
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_closeConfirmed && Ui.IsBusy &&
                !Ui.ConfirmWarn("OpenSweep is still working. Closing interrupts local tasks; external installers may continue.\n\nClose anyway?"))
            {
                e.Cancel = true;
                return;
            }
            _lifetime.Cancel();
            SaveState();
        }
    }
}
