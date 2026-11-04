using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public enum Health { Unknown, Info, Good, Warn, Bad }

    public sealed class HealthCard : ObservableObject
    {
        public static string BrushKey(Health h) =>
            h == Health.Good ? "AccentDark" : h == Health.Warn ? "Warn" : h == Health.Bad ? "Danger" : "TextSub";

        public static string SoftKey(Health h) =>
            h == Health.Good ? "AccentSoft" : h == Health.Warn ? "WarnSoft" : h == Health.Bad ? "DangerSoft" : "NeutralSoft";

        public static Brush BrushFor(Health h) => (Brush)Application.Current.FindResource(BrushKey(h));
        public static Brush SoftFor(Health h) => (Brush)Application.Current.FindResource(SoftKey(h));

        public HealthCard() { ThemeManager.Changed += () => { Raise(nameof(Brush)); Raise(nameof(SoftBrush)); }; }

        public string Glyph { get; set; }
        public string Title { get; set; }
        public string Page { get; set; }
        public string LinkText { get; set; }

        string _value = "Not scanned", _detail = "Click Scan PC";
        Health _state;
        public string Value { get => _value; set => Set(ref _value, value); }
        public string Detail { get => _detail; set => Set(ref _detail, value); }
        public Health State
        {
            get => _state;
            set { if (Set(ref _state, value)) { Raise(nameof(Brush)); Raise(nameof(SoftBrush)); } }
        }
        public Brush Brush => BrushFor(State);
        public Brush SoftBrush => SoftFor(State);

        public void Update(string value, string detail, Health state)
        {
            Value = value;
            Detail = detail;
            State = state;
        }
    }

    public partial class HealthCheckPage : UserControl, IPage
    {
        readonly HealthCard _junk = new HealthCard { Glyph = "\uEA99", Title = "Cleanup preview", Page = "cleaner", LinkText = "Review cleanup" };
        readonly HealthCard _startup = new HealthCard { Glyph = "\uE7E8", Title = "Startup programs", Page = "startup", LinkText = "Manage startup" };
        readonly HealthCard _apps = new HealthCard { Glyph = "\uE896", Title = "App updates", Page = "updater", LinkText = "Open Software Updater" };
        readonly HealthCard _windows = new HealthCard { Glyph = "\uE895", Title = "Windows Update", Page = "winupdate", LinkText = "Open Windows Update" };
        readonly HealthCard _disk = new HealthCard { Glyph = "\uEDA2", Title = "Disk space", Page = "disk", LinkText = "Open Disk Analyzer" };

        CancellationTokenSource _cts;
        bool _running, _scanned, _appsChecking, _winChecking, _appsTried, _winTried;
        List<CleanerItem> _junkItems = new List<CleanerItem>();

        HealthCard[] Cards => new[] { _junk, _startup, _apps, _windows, _disk };

        public HealthCheckPage()
        {
            InitializeComponent();
            CardsList.ItemsSource = Cards;
            ShowIntro();
            RefreshDisk();
        }

        public void OnNavigatedTo()
        {
            if (_running) return;
            RefreshDisk();
            RefreshUpdates();
            if (_scanned) ShowSummary();
        }

        void ShowIntro() => SetHeader(Health.Info, "\uE95E", "Review routine maintenance",
            "Scan your saved cleanup selection, startup entries, updates, and disk space. Saved selections may include categories you previously opted into. " +
            "The scan is read-only; review cleanup and updates separately before making changes.");

        void SetHeader(Health state, string glyph, string title, string text)
        {
            StateGlyph.Text = glyph;
            StateGlyph.SetResourceReference(TextBlock.ForegroundProperty, state == Health.Unknown ? "AccentDark" : HealthCard.BrushKey(state));
            StateBadge.SetResourceReference(Border.BackgroundProperty, state == Health.Unknown ? "AccentSoft" : HealthCard.SoftKey(state));
            StateTitle.Text = title;
            StateText.Text = text;
        }

        void SetRunning(bool running)
        {
            _running = running;
            ScanButton.IsEnabled = !running;
            ReviewCleanupButton.IsEnabled = !running;
            ScanProgress.Visibility = CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            if (!running)
            {
                CurrentText.Text = "";
                ScanProgress.IsIndeterminate = false;
            }
        }

        // ---- Scan ----

        async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _scanned = false;
            SetRunning(true);
            ScanProgress.Value = 0;
            SetHeader(Health.Info, "\uE95E", "Reading maintenance information…",
                "Previewing your saved cleanup selection without deleting anything. Update checks run separately in the background.");

            bool cancelled = false;
            string error = null;
            using (Ui.Busy())
            {
                try
                {
                    AppState.Instance.SaveChecked();
                    StartUpdateChecks();
                    await ScanJunkAsync(token);
                    token.ThrowIfCancellationRequested();
                    CurrentText.Text = "Reading startup entries…";
                    await ScanStartupAsync(token);
                    token.ThrowIfCancellationRequested();
                    RefreshDisk();
                    _scanned = true;
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex)
                {
                    Log.Write("Maintenance scan failed: " + ex);
                    error = ex.Message;
                }
                finally
                {
                    SetRunning(false);
                    _cts.Dispose();
                    _cts = null;
                }
            }
            if (cancelled)
                SetHeader(Health.Unknown, "\uE95E", "Local scan cancelled",
                    "Completed results remain below. Update checks may still be running on their own pages.");
            else if (error != null)
                SetHeader(Health.Unknown, "\uE95E", "Some information could not be read", error + " Completed results remain below.");
            else ShowSummary();
        }

        async Task ScanJunkAsync(CancellationToken token)
        {
            _junkItems = AppState.Instance.CheckedItems;
            if (_junkItems.Count == 0)
            {
                _junk.Update("Nothing selected", "Tick items on the Custom Clean page to include them", Health.Unknown);
                return;
            }
            _junk.Update("Analyzing…", "", Health.Unknown);
            int done = 0, total = _junkItems.Count;
            var progress = new Progress<(CleanerItem Item, bool Done)>(p =>
            {
                if (_cts == null || _cts.Token != token || token.IsCancellationRequested) return;
                if (!p.Done) { CurrentText.Text = "Analyzing: " + p.Item.Group + " – " + p.Item.Name; return; }
                done++;
                ScanProgress.Value = done * 85.0 / total;
            });
            try
            {
                await CleanRunner.AnalyzeAsync(_junkItems, progress, token);
                RefreshJunk();
            }
            catch (OperationCanceledException)
            {
                _junk.Update("Cancelled", "Scan again to finish checking the selected items", Health.Unknown);
                throw;
            }
            catch
            {
                _junk.Update("Couldn't analyze", "Scan again or open Custom Clean for details", Health.Unknown);
                throw;
            }
        }

        void RefreshJunk()
        {
            var completed = _junkItems.Where(i => i.HasResult).ToList();
            var found = completed.Where(i => i.Bytes > 0).ToList();
            long bytes = found.Sum(i => i.Bytes), files = found.Sum(i => i.Files);
            bool incomplete = completed.Count != _junkItems.Count || completed.Any(i =>
                (i.Status ?? "").StartsWith("Error:", StringComparison.Ordinal) ||
                (i.Status ?? "").StartsWith("Skipped:", StringComparison.Ordinal));
            _junk.Update(Fmt.Bytes(bytes),
                (bytes == 0 ? "No removable files found in the scanned selection."
                    : $"{Fmt.Count(files, "file")} in {Fmt.Count(found.Count, "selected category")}. Review before removing anything.") +
                (incomplete ? " Some categories could not be checked." : " Cached files alone do not mean your PC is unhealthy."),
                incomplete ? Health.Unknown : Health.Info);
        }

        async Task ScanStartupAsync(CancellationToken token)
        {
            _startup.Update("Reading…", "", Health.Unknown);
            try
            {
                var result = await Task.Run(() => StartupManager.LoadDetailed());
                token.ThrowIfCancellationRequested();
                int enabled = result.Entries.Count(x => x.State == StartupState.Enabled);
                int unknown = result.Entries.Count(x => x.State == StartupState.Unknown);
                string detail = $"{Fmt.Count(result.Entries.Count, "entry")} listed. Review apps you do not need at sign-in; the count alone does not measure startup performance.";
                if (unknown > 0) detail += $" {Fmt.Count(unknown, "entry")} with unknown enabled state.";
                if (!result.IsComplete) detail += " Some startup locations could not be read. Open Startup for details.";
                _startup.Update($"{enabled} enabled", detail, result.IsComplete && unknown == 0 ? Health.Info : Health.Unknown);
                ScanProgress.Value = 100;
            }
            catch (OperationCanceledException)
            {
                _startup.Update("Cancelled", "Scan again to read startup entries", Health.Unknown);
                throw;
            }
            catch
            {
                _startup.Update("Unavailable", "Open Startup for details", Health.Unknown);
                throw;
            }
        }

        void RefreshDisk()
        {
            try
            {
                var d = SystemInfo.SystemDrive;
                long free = d.AvailableFreeSpace, total = d.TotalSize;
                if (total <= 0 || free < 0 || free > total)
                {
                    _disk.Update("Unknown", "Windows did not report a usable capacity for the system drive", Health.Unknown);
                    return;
                }
                double pct = free * 100.0 / total;
                _disk.Update(Fmt.Bytes(free) + " free", $"of {Fmt.Bytes(total)} on drive {d.Name.TrimEnd('\\')} ({pct:0}% free)",
                    free < 1024L * 1024 * 1024 && pct < 10 ? Health.Bad : pct < 10 ? Health.Warn : Health.Info);
            }
            catch (Exception ex) { _disk.Update("Unknown", ex.Message, Health.Unknown); }
        }

        // ---- Update checks (run alongside the scan; Windows Update can take minutes) ----

        void StartUpdateChecks()
        {
            var main = MainWindow.Instance;
            if (main == null) return;
            if (!_appsChecking) _ = RunCheckAsync(main.GetPage<SoftwareUpdaterPage>("updater").CheckAsync, true);
            if (!_winChecking) _ = RunCheckAsync(main.GetPage<WindowsUpdatePage>("winupdate").CheckAsync, false);
        }

        async Task RunCheckAsync(Func<Task> check, bool apps)
        {
            if (apps) { _appsChecking = true; _appsTried = true; }
            else { _winChecking = true; _winTried = true; }
            RefreshUpdates();
            try { await check(); }
            catch (Exception ex)
            {
                if (apps) AppState.Instance.AppUpdatesChecked = false;
                else AppState.Instance.WinUpdatesChecked = false;
                Log.Write("Maintenance update check: " + ex.Message);
            }
            finally
            {
                if (apps) _appsChecking = false; else _winChecking = false;
                RefreshUpdates();
                if (_scanned && !_running) ShowSummary();
            }
        }

        void RefreshUpdates()
        {
            var st = AppState.Instance;
            if (!Winget.IsAvailable)
                _apps.Update("Unavailable", "Install App Installer from the Microsoft Store to enable app updates", Health.Unknown);
            else if (_appsChecking)
                _apps.Update("Checking…", "Asking Windows Package Manager for newer versions", Health.Unknown);
            else if (!st.AppUpdatesChecked)
                _apps.Update(_appsTried ? "Couldn't check" : "Not checked",
                    _appsTried ? "Open Software Updater for details" : "Click Scan PC", Health.Unknown);
            else
            {
                int n = st.AppUpdates.Count(a => !(a.Status ?? "").StartsWith("Updated"));
                _apps.Update(n == 0 ? "No listed updates" : Fmt.Count(n, "update") + " listed",
                    n == 0 ? "The last Windows Package Manager check listed no available updates" : "Last check results. Choose what to update on the Software Updater page",
                    n == 0 ? Health.Good : Health.Info);
            }

            if (_winChecking)
                _windows.Update("Checking…", "Windows Update is searching – this can take a few minutes", Health.Unknown);
            else if (!st.WinUpdatesChecked)
                _windows.Update(_winTried ? "Couldn't check" : "Not checked",
                    _winTried ? "Open Windows Update for details" : "Click Scan PC", Health.Unknown);
            else
            {
                var pending = st.WinUpdates.Where(u => !(u.Status ?? "").StartsWith("Installed")).ToList();
                int important = pending.Count(u => !u.IsOptional), optional = pending.Count - important;
                _windows.Update(important == 0 ? "No important updates listed" : Fmt.Count(important, "update") + " listed",
                    (important == 0 ? "The last check listed no important updates" : "Last check results. Review them on the Windows Update page") +
                    (optional > 0 ? $" · {optional} optional" : ""),
                    important == 0 ? Health.Good : Health.Info);
            }
        }

        void ShowSummary()
        {
            int incomplete = Cards.Count(c => c.State == Health.Unknown);
            bool pending = _appsChecking || _winChecking;
            if (_disk.State == Health.Warn || _disk.State == Health.Bad)
                SetHeader(_disk.State, "\uEDA2", "System drive space is running low",
                    "Review the disk-space card and cleanup preview before choosing what to remove. " +
                    (pending ? "Update checks are still running." : incomplete > 0 ? "Some checks could not be completed; see their cards." : "Available updates are listed separately."));
            else if (pending)
                SetHeader(Health.Info, "\uE95E", "Local scan complete; updates are still checking",
                    "Review the available cleanup and startup information below. Update cards will refresh when their checks finish.");
            else if (incomplete > 0)
                SetHeader(Health.Unknown, "\uE95E", "Review the available results",
                    "Some checks were unavailable or not selected. Their cards show the available information and any follow-up needed.");
            else
                SetHeader(Health.Info, "\uE95E", "Maintenance information is ready",
                    "Review your cleanup selection, available updates, startup entries, and disk space below. Nothing was changed by this scan.");
        }

        async void ReviewCleanup_Click(object sender, RoutedEventArgs e) => await ReviewCleanupAsync();

        async Task ReviewCleanupAsync()
        {
            if (_running || MainWindow.Instance == null) return;
            var main = MainWindow.Instance;
            main.Navigate("cleaner");
            // Custom Clean owns its results grid. Refresh that read-only preview so the
            // handoff never shows an empty table or offers deletion based on stale totals.
            await main.GetPage<CleanerPage>("cleaner").AnalyzeSelectionAsync();
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            CurrentText.Text = "Stopping local scan…";
        }

        async void CardLink_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is string page)) return;
            if (page == "cleaner") await ReviewCleanupAsync();
            else Ui.Go(page);
        }
    }
}
