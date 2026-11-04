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

        async void Analyze_Click(object sender, RoutedEventArgs e) => await RunAsync(false);

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
            var sw = Stopwatch.StartNew();
            using (Ui.Busy())
            {
                try
                {
                    if (clean)
                    {
                        items = await CleanFlow.PrepareAsync(items, ct);
                        if (items == null || items.Count == 0) return;
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
                    var work = clean ? CleanRunner.CleanAsync(items, progress, ct) : CleanRunner.AnalyzeAsync(items, progress, ct);
                    started = true;
                    await work;
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex)
                {
                    failed = true;
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
            long bytes = finished.Sum(i => i.Bytes), files = finished.Sum(i => i.Files);
            var took = $"{sw.Elapsed.TotalSeconds:0.0} s";
            if (clean)
            {
                SummaryTitle.Text = (failed ? "Cleaning stopped – " : cancelled ? "Cleaning cancelled – " : "Cleaning complete – ") + Fmt.Bytes(bytes) + " removed";
                SummaryText.Text = $"{Fmt.Count(files, "file")} deleted from {Fmt.Count(finished.Count, "item")} in {took}. " +
                                   "Files that were in use were skipped.";
            }
            else
            {
                SummaryTitle.Text = (failed ? "Analysis stopped – " : cancelled ? "Analysis cancelled – " : "") + Fmt.Bytes(bytes) + " can be freed";
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
