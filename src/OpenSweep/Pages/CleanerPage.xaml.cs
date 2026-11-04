using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class CleanerPage : UserControl, IPage
    {
        readonly ObservableCollection<CleanerItem> _results = new ObservableCollection<CleanerItem>();
        CancellationTokenSource _cts;

        public CleanerPage()
        {
            InitializeComponent();
            GroupList.ItemsSource = AppState.Instance.Groups;
            ResultsGrid.ItemsSource = _results;
            OldTempBox.IsChecked = AppSettings.Current.OnlyOldTempFiles;
            AdminNote.Visibility = !Elevation.IsAdmin && AppState.Instance.AllItems.Any(i => i.NeedsAdmin)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        public void OnNavigatedTo() => OldTempBox.IsChecked = AppSettings.Current.OnlyOldTempFiles;

        void SetRunning(bool running)
        {
            AnalyzeButton.IsEnabled = RunButton.IsEnabled = GroupList.IsEnabled = OldTempBox.IsEnabled = !running;
            CancelButton.Visibility = Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            if (!running) CurrentText.Text = "";
        }

        public System.Threading.Tasks.Task AnalyzeSelectionAsync() => RunAsync(false);

        async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeSelectionAsync();

        async void Run_Click(object sender, RoutedEventArgs e)
        {
            var count = AppState.Instance.CheckedItems.Count;
            if (count == 0) { Ui.Info("Select at least one item to clean."); return; }
            if (!Ui.ConfirmWarn($"Run Cleaner permanently deletes the junk found in {Fmt.Count(count, "selected item")}.\n\nContinue?", "Run Cleaner"))
                return;
            await RunAsync(true);
        }

        async System.Threading.Tasks.Task RunAsync(bool clean)
        {
            if (_cts != null) return;
            var items = AppState.Instance.CheckedItems;
            if (items.Count == 0) { Ui.Info("Select at least one item first."); return; }
            AppState.Instance.SaveChecked();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetRunning(true);
            Progress.Value = 0;
            bool started = false, cancelled = false, failed = false;
            int preparationSkipped = 0;
            var sw = Stopwatch.StartNew();
            using (var history = clean ? MaintenanceHistory.Begin("Cleanup", "Manual") : null)
            using (Ui.Busy())
            {
                try
                {
                    if (clean)
                    {
                        int requested = items.Count;
                        items = await CleanFlow.PrepareAsync(items, ct, history);
                        if (items == null) return;
                        preparationSkipped = requested - items.Count;
                        if (items.Count == 0)
                        {
                            history.CompleteFromItems(summary: "All selected cleanup categories were skipped because their apps were running.");
                            SummaryTitle.Text = "Nothing cleaned";
                            SummaryText.Text = "All selected categories were skipped. Details are saved in Maintenance history.";
                            return;
                        }
                    }
                    ct.ThrowIfCancellationRequested();
                    _results.Clear();
                    SummaryTitle.Text = clean ? "Cleaning…" : "Analyzing…";
                    SummaryText.Text = "";
                    int done = 0;
                    long runningBytes = 0;
                    var progress = new Progress<(CleanerItem Item, bool Done)>(p =>
                    {
                        if (!p.Done)
                        {
                            CurrentText.Text = (clean ? "Cleaning: " : "Analyzing: ") + p.Item.Group + " – " + p.Item.Name;
                            return;
                        }
                        done++;
                        runningBytes += p.Item.Bytes;
                        Progress.Value = done * 100.0 / items.Count;
                        SummaryText.Text = $"{done} of {items.Count} items · {Fmt.Bytes(runningBytes)} so far";
                        _results.Add(p.Item);
                    });
                    // RunAsync may reject another active scan synchronously. Do not count its results as ours.
                    var work = clean ? CleanRunner.CleanAsync(items, progress, ct, history) : CleanRunner.AnalyzeAsync(items, progress, ct);
                    started = true;
                    await work;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    if (!started && history != null)
                    {
                        foreach (var item in items ?? new List<CleanerItem>()) history.AddItem(CleanupHistory.NotRun(item, true, "Cancelled before this category began."));
                        history.Complete(MaintenanceOutcome.Cancelled, "Cleanup was cancelled during preparation.");
                    }
                }
                catch (Exception ex)
                {
                    failed = true;
                    if (!started && history != null)
                    {
                        foreach (var item in items ?? new List<CleanerItem>()) history.AddItem(CleanupHistory.NotRun(item, false, "Preparation did not complete; cleanup was not started."));
                        history.AddItem(new MaintenanceItem { Label = "Cleanup preparation", Status = "Failed", Detail = ex.Message, Outcome = MaintenanceOutcome.Failed, Failures = 1 });
                        history.Complete(MaintenanceOutcome.Failed, "Cleanup could not start.");
                    }
                    Log.Write("Cleaner failed: " + ex);
                    Ui.Error("The cleaner stopped because of an error:\n" + ex.Message);
                }
                finally
                {
                    sw.Stop();
                    _cts.Dispose();
                    _cts = null;
                    SetRunning(false);
                }
            }

            var finished = started ? items.Where(i => i.HasResult).ToList() : new List<CleanerItem>();
            long bytes = finished.Where(i => i.CountsKnown).Sum(i => i.Bytes), files = finished.Where(i => i.CountsKnown).Sum(i => i.Files);
            long failures = finished.Sum(i => i.Failures), skipped = finished.Sum(i => i.Skipped) + preparationSkipped;
            bool hasUnknownCounts = finished.Any(i => !i.CountsKnown);
            var took = $"{sw.Elapsed.TotalSeconds:0.0} s";
            if (clean)
            {
                SummaryTitle.Text = failed ? "Cleanup stopped" : cancelled ? "Cleanup cancelled" : failures > 0 || skipped > 0 ? "Cleanup finished with skips or errors" : "Cleanup complete";
                SummaryText.Text = $"Recorded: {Fmt.Count(files, "file")} deleted · {Fmt.Bytes(bytes)} of file data · {failures:N0} failed · {skipped:N0} skipped · {took}. " +
                                   (hasUnknownCounts ? "Some selected actions have no measured file totals. " : "") +
                                   "Details are saved in Maintenance history. File sizes are not measured free-space gains.";
            }
            else
            {
                SummaryTitle.Text = (failed ? "Analysis stopped – " : cancelled ? "Analysis cancelled – " : "") + Fmt.Bytes(bytes) + " of file data found";
                SummaryText.Text = $"{Fmt.Count(files, "file")} in {Fmt.Count(finished.Count, "item")} · analysis took {took}. " +
                                   "Click Run Cleaner to remove them.";
            }
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            CurrentText.Text = "Stopping…";
        }

        void All_Click(object sender, RoutedEventArgs e)
        {
            foreach (var g in AppState.Instance.Groups) g.AllChecked = true;
        }

        void None_Click(object sender, RoutedEventArgs e)
        {
            foreach (var g in AppState.Instance.Groups) g.AllChecked = false;
        }

        void Defaults_Click(object sender, RoutedEventArgs e) => AppState.Instance.ResetCheckedToDefaults();

        void OldTemp_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Current.OnlyOldTempFiles = OldTempBox.IsChecked == true;
            AppSettings.Current.Save();
        }
    }
}
