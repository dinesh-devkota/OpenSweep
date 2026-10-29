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
    public enum Health { Unknown, Good, Warn, Bad }

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
        readonly HealthCard _junk = new HealthCard { Glyph = "\uEA99", Title = "Junk files", Page = "cleaner", LinkText = "Open Custom Clean" };
        readonly HealthCard _registry = new HealthCard { Glyph = "\uE90F", Title = "Registry", Page = "registry", LinkText = "Review issues" };
        readonly HealthCard _startup = new HealthCard { Glyph = "\uE7E8", Title = "Startup programs", Page = "startup", LinkText = "Manage startup" };
        readonly HealthCard _apps = new HealthCard { Glyph = "\uE896", Title = "App updates", Page = "updater", LinkText = "Open Software Updater" };
        readonly HealthCard _windows = new HealthCard { Glyph = "\uE895", Title = "Windows Update", Page = "winupdate", LinkText = "Open Windows Update" };
        readonly HealthCard _disk = new HealthCard { Glyph = "\uEDA2", Title = "Disk space", Page = "disk", LinkText = "Open Disk Analyzer" };

        CancellationTokenSource _cts;
        bool _running, _scanned, _appsChecking, _winChecking, _appsTried, _winTried;
        List<CleanerItem> _junkItems = new List<CleanerItem>();

        HealthCard[] Cards => new[] { _junk, _registry, _startup, _apps, _windows, _disk };

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
            RefreshRegistry();
            RefreshUpdates();
        }

        void ShowIntro() => SetHeader(Health.Unknown, "\uE95E", "Check your PC's health",
            "Scan PC looks for junk files, registry leftovers, startup programs, missing updates and low disk space. " +
            "Nothing is changed until you click Fix all.");

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
            FixButton.IsEnabled = !running && _scanned;
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
            SetHeader(Health.Unknown, "\uE95E", "Scanning your PC…",
                "This usually takes less than a minute. Update checks carry on in the background.");
            AppState.Instance.SaveChecked();
            StartUpdateChecks();

            bool cancelled = false;
            using (Ui.Busy())
            {
                try
                {
                    await ScanJunkAsync(token);
                    await ScanRegistryAsync(token);
                    CurrentText.Text = "Reading startup programs…";
                    await ScanStartupAsync();
                    RefreshDisk();
                    _scanned = true;
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex)
                {
                    Log.Write("Health check failed: " + ex);
                    Ui.Error("The health check stopped because of an error:\n" + ex.Message);
                }
            }
            SetRunning(false);
            if (cancelled) SetHeader(Health.Unknown, "\uE95E", "Scan cancelled", "Click Scan PC to start again.");
            else if (_scanned) ShowSummary();
            else ShowIntro();
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
                if (!p.Done) { CurrentText.Text = "Analyzing: " + p.Item.Group + " – " + p.Item.Name; return; }
                done++;
                ScanProgress.Value = done * 70.0 / total;
            });
            try { await CleanRunner.AnalyzeAsync(_junkItems, progress, token); }
            finally { RefreshJunk(); }
        }

        void RefreshJunk()
        {
            var found = _junkItems.Where(i => i.HasResult && i.Bytes > 0).ToList();
            long bytes = found.Sum(i => i.Bytes), files = found.Sum(i => i.Files);
            _junk.Update(Fmt.Bytes(bytes),
                bytes == 0 ? "No junk found in the selected items" : $"{Fmt.Count(files, "file")} in {Fmt.Count(found.Count, "item")} can be removed",
                bytes >= 100L * 1024 * 1024 ? Health.Warn : Health.Good);
        }

        async Task ScanRegistryAsync(CancellationToken token)
        {
            _registry.Update("Scanning…", "", Health.Unknown);
            var progress = new Progress<string>(s => CurrentText.Text = s);
            var issues = await Task.Run(() => RegistryScanner.Scan(progress, token));
            AppState.Instance.RegistryIssues.ReplaceWith(issues);
            AppState.Instance.RegistryScanned = true;
            ScanProgress.Value = 90;
            RefreshRegistry();
        }

        void RefreshRegistry()
        {
            if (!AppState.Instance.RegistryScanned) return;
            var list = AppState.Instance.RegistryIssues;
            int locked = Elevation.IsAdmin ? 0 : list.Count(i => i.NeedsAdmin);
            _registry.Update(list.Count == 0 ? "No issues" : Fmt.Count(list.Count, "issue"),
                list.Count == 0 ? "No leftovers from removed programs were found"
                    : "Leftover entries from removed programs" + (locked > 0 ? $" · {locked} need administrator rights" : ""),
                list.Count == 0 ? Health.Good : Health.Warn);
        }

        async Task ScanStartupAsync()
        {
            var entries = await Task.Run(() => StartupManager.Load());
            int enabled = entries.Count(x => x.Enabled);
            _startup.Update($"{enabled} enabled",
                $"of {Fmt.Count(entries.Count, "startup program")} · fewer programs at sign-in means a faster start",
                enabled > 12 ? Health.Warn : Health.Good);
            ScanProgress.Value = 100;
        }

        void RefreshDisk()
        {
            try
            {
                var d = SystemInfo.SystemDrive;
                long free = d.AvailableFreeSpace, total = d.TotalSize;
                double pct = total > 0 ? free * 100.0 / total : 0;
                _disk.Update(Fmt.Bytes(free) + " free", $"of {Fmt.Bytes(total)} on drive {d.Name.TrimEnd('\\')} ({pct:0}% free)",
                    pct < 10 ? Health.Bad : pct < 20 ? Health.Warn : Health.Good);
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
            catch (Exception ex) { Log.Write("Health check update scan: " + ex.Message); }
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
                _apps.Update(n == 0 ? "Up to date" : Fmt.Count(n, "update") + " available",
                    n == 0 ? "No newer versions of your programs were found" : "Choose what to update on the Software Updater page",
                    n == 0 ? Health.Good : Health.Warn);
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
                _windows.Update(important == 0 ? "Up to date" : Fmt.Count(important, "update") + " available",
                    (important == 0 ? "No important updates are waiting" : "Install them on the Windows Update page") +
                    (optional > 0 ? $" · {optional} optional" : ""),
                    important == 0 ? Health.Good : Health.Warn);
            }
        }

        void ShowSummary()
        {
            int problems = Cards.Count(c => c.State == Health.Warn || c.State == Health.Bad);
            bool fixable = _junkItems.Any(i => i.HasResult && i.Bytes > 0) || FixableIssues().Count > 0;
            var pending = _appsChecking || _winChecking ? " Update checks are still running." : "";
            if (problems == 0)
                SetHeader(Health.Good, "\uE73E", "Your PC looks good", "No problems were found." + pending);
            else
                SetHeader(Health.Warn, "\uE7BA", $"{Fmt.Count(problems, "area")} need{(problems == 1 ? "s" : "")} attention",
                    (fixable ? "Click Fix all to remove junk files and fix registry issues. " : "") +
                    "Updates, startup programs and disk space are handled on their own pages – use the links below." + pending);
        }

        static List<RegistryIssue> FixableIssues() =>
            AppState.Instance.RegistryIssues.Where(i => i.IsChecked && (Elevation.IsAdmin || !i.NeedsAdmin)).ToList();

        // ---- Fix all ----

        async void FixAll_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            var items = AppState.Instance.CheckedItems;
            var issues = FixableIssues();
            if (items.Count == 0 && issues.Count == 0)
            {
                Ui.Info("There is nothing to fix. Tick items on the Custom Clean page, or scan again.");
                return;
            }
            long found = _junkItems.Where(i => i.HasResult).Sum(i => i.Bytes);
            var steps = new List<string>();
            if (items.Count > 0) steps.Add($"• Permanently delete junk files in {Fmt.Count(items.Count, "cleaner item")} (about {Fmt.Bytes(found)} found)");
            if (issues.Count > 0) steps.Add($"• Fix {Fmt.Count(issues.Count, "registry issue")} – every key is backed up first");
            if (!Ui.ConfirmWarn("Fix all will:\n\n" + string.Join("\n", steps) +
                                "\n\nUpdates and startup programs are not changed – use their pages for those.\n\nContinue?", "Fix all"))
                return;

            if (items.Count > 0)
            {
                items = await CleanFlow.PrepareAsync(items);
                if (items == null) return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetRunning(true);
            ScanProgress.Value = 0;
            long freed = 0, files = 0;
            int fixedCount = 0;
            string backup = null, error = null;
            bool cancelled = false;
            using (Ui.Busy())
            {
                try
                {
                    if (items.Count > 0)
                    {
                        SetHeader(Health.Unknown, "\uEA99", "Cleaning…", "Deleting junk files. Files that are in use are skipped.");
                        int done = 0, total = items.Count;
                        var progress = new Progress<(CleanerItem Item, bool Done)>(p =>
                        {
                            if (!p.Done) { CurrentText.Text = "Cleaning: " + p.Item.Group + " – " + p.Item.Name; return; }
                            done++;
                            ScanProgress.Value = done * 100.0 / total;
                        });
                        try { await CleanRunner.CleanAsync(items, progress, token); }
                        finally
                        {
                            var cleaned = items.Where(i => i.HasResult).ToList();
                            freed = cleaned.Sum(i => i.Bytes);
                            files = cleaned.Sum(i => i.Files);
                        }
                    }
                    if (issues.Count > 0)
                    {
                        SetHeader(Health.Unknown, "\uE90F", "Fixing registry issues…", "Backing up, then removing leftover registry entries.");
                        CancelButton.Visibility = Visibility.Collapsed;
                        ScanProgress.IsIndeterminate = true;
                        var result = await RegistryPage.FixIssuesAsync(issues, new Progress<string>(s => CurrentText.Text = s));
                        fixedCount = result.Fixed;
                        backup = result.Backup;
                    }
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex)
                {
                    Log.Write("Fix all failed: " + ex);
                    error = ex.Message;
                }
            }
            _scanned = false;
            SetRunning(false);

            if (items.Count > 0)
                _junk.Update(Fmt.Bytes(freed) + " removed", $"{Fmt.Count(files, "file")} deleted · scan again to check for more", Health.Good);
            RefreshRegistry();

            var parts = new List<string>();
            if (items.Count > 0) parts.Add($"{Fmt.Bytes(freed)} of junk removed");
            if (fixedCount > 0) parts.Add($"{Fmt.Count(fixedCount, "registry issue")} fixed");
            var backupNote = backup != null ? "Registry backup: " + backup + ". " : "";
            if (error != null)
                SetHeader(Health.Bad, "\uE7BA", "Fix all stopped because of an error", error);
            else if (cancelled)
                SetHeader(Health.Warn, "\uE7BA", "Fix all cancelled",
                    (parts.Count > 0 ? string.Join(" · ", parts) + ". " : "") + "Click Scan PC to check again.");
            else if (parts.Count == 0)
                SetHeader(Health.Unknown, "\uE95E", "Nothing was changed", backupNote + "Click Scan PC to check again.");
            else
                SetHeader(Health.Good, "\uE73E", "Done – " + string.Join(", ", parts),
                    backupNote + "Files that were in use were skipped. Click Scan PC to check again.");
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            CurrentText.Text = "Stopping…";
        }

        void CardLink_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string page) Ui.Go(page);
        }
    }
}
