using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class DuplicatesPage : UserControl, IPage
    {
        readonly ObservableCollection<string> _folders = new ObservableCollection<string>();
        List<DupGroup> _groups = new List<DupGroup>();
        CancellationTokenSource _cts;

        public DuplicatesPage()
        {
            InitializeComponent();
            _folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            FolderList.ItemsSource = _folders;
            UpdateSelected();
        }

        public void OnNavigatedTo() { }

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dir = Ui.PickFolder("Choose a folder to search for duplicates");
            if (dir == null) return;
            dir = dir.TrimEnd('\\');
            if (dir.Length == 2 && dir[1] == ':') dir += "\\";
            if (_folders.Any(f => Within(dir, f))) { Ui.Info("That folder is already included in the search."); return; }
            foreach (var inner in _folders.Where(f => Within(f, dir)).ToList()) _folders.Remove(inner);
            _folders.Add(dir);
        }

        static bool Within(string path, string root)
        {
            var r = root.TrimEnd('\\');
            return path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && (path.Length == r.Length || path[r.Length] == '\\');
        }

        void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (FolderList.SelectedItem is string dir) _folders.Remove(dir);
            else Ui.Info("Select a folder in the list first.");
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        async void Search_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) return;
            if (_folders.Count == 0) { Ui.Info("Add at least one folder to search."); return; }
            var roots = _folders.ToList();
            long minSize = long.Parse((string)((ComboBoxItem)MinSizeBox.SelectedItem).Tag);
            bool skipHidden = SkipHiddenBox.IsChecked == true;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetRunning(true);
            ShowGroups(new List<DupGroup>());
            StatusText.Text = "Searching…";
            var progress = new Progress<string>(s => StatusText.Text = s);
            try
            {
                using (Ui.Busy())
                {
                    var groups = await Task.Run(() => DuplicateFinder.Find(roots, minSize, skipHidden, progress, ct));
                    ShowGroups(groups);
                    StatusText.Text = groups.Count == 0
                        ? "No duplicate files found."
                        : $"Found {Fmt.Count(groups.Count, "group")} of identical files · {Fmt.Bytes(groups.Sum(g => g.Wasted))} could be freed. " +
                          "Tick the copies to remove, or use “Select all but newest”.";
                }
            }
            catch (OperationCanceledException) { StatusText.Text = "Search cancelled."; }
            catch (Exception ex)
            {
                Log.Write("Duplicate search failed: " + ex);
                StatusText.Text = "Search failed: " + ex.Message;
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetRunning(false);
            }
        }

        void SetRunning(bool running)
        {
            SearchProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            SearchButton.IsEnabled = !running;
            ResultsGrid.IsEnabled = !running;
        }

        void ShowGroups(List<DupGroup> groups)
        {
            _groups = groups.OrderByDescending(g => g.Wasted).ToList();
            var files = _groups.SelectMany(g => g.Files.OrderByDescending(f => f.Modified)).ToList();
            var view = new ListCollectionView(files);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DupFile.GroupLabel)));
            ResultsGrid.ItemsSource = view;
            UpdateSelected();
        }

        void UpdateSelected()
        {
            var chosen = _groups.SelectMany(g => g.Files).Where(f => f.IsChecked).ToList();
            SelectedText.Text = chosen.Count == 0 ? "" : $"{Fmt.Count(chosen.Count, "file")} selected · {Fmt.Bytes(chosen.Sum(f => f.Size))}";
        }

        void FileCheck_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DupFile f && f.IsChecked && f.Group.Files.All(x => x.IsChecked))
            {
                f.IsChecked = false;
                StatusText.Text = "One copy in every group is always kept – untick another copy first if you want to remove this one.";
            }
            UpdateSelected();
        }

        void KeepNewest_Click(object sender, RoutedEventArgs e) { if (_cts != null) return; DuplicateFinder.SelectAllButOne(_groups, true); UpdateSelected(); }
        void KeepOldest_Click(object sender, RoutedEventArgs e) { if (_cts != null) return; DuplicateFinder.SelectAllButOne(_groups, false); UpdateSelected(); }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) return;
            foreach (var f in _groups.SelectMany(g => g.Files)) f.IsChecked = false;
            UpdateSelected();
        }

        void Show_Click(object sender, RoutedEventArgs e)
        {
            if (!Ui.IsRowOrButton(ResultsGrid, e)) return;
            if (ResultsGrid.SelectedItem is DupFile f) ShellOps.ShowInExplorer(File.Exists(f.FullPath) ? f.FullPath : f.Folder);
            else if (!(e is MouseButtonEventArgs)) Ui.Info("Select a file in the list first.");
        }

        async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) return;
            var targets = _groups.SelectMany(g => g.Files).Where(f => f.IsChecked)
                .Select(file => new { File = file, Path = file.FullPath, Size = file.Size }).ToList();
            if (targets.Count == 0)
            {
                Ui.Info("Tick the copies you want to remove first, or use “Keep newest”.");
                return;
            }
            if (!Ui.ConfirmWarn($"Move {Fmt.Count(targets.Count, "selected duplicate file")} ({Fmt.Bytes(targets.Sum(f => f.Size))}) to the Recycle Bin?\n\n" +
                                "Files are rechecked before removal, and one verified copy is kept. Windows asks first if something can't be recycled and would be deleted for good.",
                                "Delete duplicates")) return;

            var groups = _groups.ToList();
            var snapshots = targets.ToDictionary(t => t.File);
            var recorded = new HashSet<DupFile>();
            bool shellStarted = false, outcomesRecorded = false;
            using var history = MaintenanceHistory.Begin("Remove duplicate files");
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetRunning(true);
            StatusText.Text = "Rechecking duplicate files…";
            try
            {
                using (Ui.Busy())
                using (var plan = await Task.Run(() => DuplicateFinder.PrepareDeletion(groups, ct)))
                {
                    ct.ThrowIfCancellationRequested();
                    plan.RecheckPaths();
                    foreach (var skipped in plan.Skipped)
                    {
                        skipped.Key.Status = skipped.Value;
                        history.AddItem(new MaintenanceItem { Label = snapshots[skipped.Key].Path, Status = "Skipped for safety",
                            Outcome = MaintenanceOutcome.Unknown, Skipped = 1, Detail = skipped.Value });
                        recorded.Add(skipped.Key);
                    }
                    if (plan.Files.Count == 0)
                    {
                        history.Complete(MaintenanceOutcome.Partial, "No files were removed; verification or exclusion checks skipped the selection.");
                        outcomesRecorded = true;
                        StatusText.Text = "Nothing was deleted. The files or their kept copies changed, are unavailable, or are excluded. Search again.";
                        return;
                    }
                    // The plan holds verified kept copies open until the shell operation has completed.
                    shellStarted = true;
                    var gone = ShellOps.Recycle(plan.Files.Select(f => snapshots[f].Path).ToList(), Ui.OwnerHandle);
                    var goneSet = new HashSet<string>(gone, StringComparer.OrdinalIgnoreCase);
                    int removed = 0; long movedBytes = 0;
                    foreach (var file in plan.Files)
                    {
                        var snapshot = snapshots[file];
                        bool removedFile = goneSet.Contains(snapshot.Path);
                        history.AddItem(new MaintenanceItem { Label = snapshot.Path,
                            Status = removedFile ? "Removed from original location" : "Not removed",
                            Outcome = removedFile ? MaintenanceOutcome.Completed : MaintenanceOutcome.Unknown,
                            Files = removedFile ? (long?)1 : 0, Bytes = removedFile ? (long?)snapshot.Size : null,
                            BytesKind = removedFile ? HistoryBytesKind.Recycled : HistoryBytesKind.None,
                            Skipped = removedFile ? 0 : 1,
                            Detail = removedFile ? "Logical file size removed from the original location, not measured disk space freed. Inspect the Recycle Bin; recovery is available only if Windows retained the file." :
                                "The path was not confirmed removed. Windows may have skipped or cancelled it, or access may be blocked.",
                            Recovery = removedFile ? RecoveryKind.RecycleBin : RecoveryKind.None });
                        recorded.Add(file);
                        if (removedFile) { removed++; movedBytes += snapshot.Size; }
                    }
                    history.Complete(removed == targets.Count ? MaintenanceOutcome.Completed : MaintenanceOutcome.Partial,
                        $"Removed {removed} of {targets.Count} selected files. Logical moved size: {Fmt.Bytes(movedBytes)}; physical free space was not measured.");
                    outcomesRecorded = true;
                    foreach (var file in plan.Files)
                    {
                        if (goneSet.Contains(snapshots[file].Path)) file.Group.Files.Remove(file);
                        else file.Status = "Not removed (in use, protected, cancelled or skipped)";
                    }
                    ShowGroups(_groups.Where(g => g.Files.Count > 1).ToList());
                    StatusText.Text = $"Removed {Fmt.Count(removed, "file")} from their original locations ({Fmt.Bytes(movedBytes)} logical size; not measured free space)." +
                                      (removed < plan.Files.Count ? $" {plan.Files.Count - removed} were not removed." : "") +
                                      (plan.Skipped.Count > 0 ? $" {Fmt.Count(plan.Skipped.Count, "file")} skipped for safety. Search again." : "");
                }
            }
            catch (OperationCanceledException)
            {
                if (!outcomesRecorded)
                {
                    foreach (var target in targets.Where(t => !recorded.Contains(t.File)))
                        history.AddItem(new MaintenanceItem { Label = target.Path, Status = "Cancelled",
                            Outcome = shellStarted ? MaintenanceOutcome.Unknown : MaintenanceOutcome.Cancelled,
                            Detail = shellStarted ? "The operation was interrupted; file outcomes were not confirmed." : "Verification was cancelled before removal." });
                    history.Complete(shellStarted ? MaintenanceOutcome.Unknown : MaintenanceOutcome.Cancelled,
                        shellStarted ? "File removal outcomes were not confirmed." : "Duplicate verification was cancelled; no files were removed.");
                }
                StatusText.Text = shellStarted ? "The operation was interrupted. Inspect the files and Recycle Bin." : "Duplicate verification cancelled. Nothing was deleted.";
            }
            catch (Exception ex)
            {
                if (!outcomesRecorded)
                {
                    foreach (var target in targets.Where(t => !recorded.Contains(t.File)))
                        history.AddItem(new MaintenanceItem { Label = target.Path, Status = shellStarted ? "Outcome not confirmed" : "Verification failed",
                            Outcome = shellStarted ? MaintenanceOutcome.Unknown : MaintenanceOutcome.Failed, Failures = 1, Detail = ex.Message });
                    history.Complete(shellStarted ? MaintenanceOutcome.Unknown : MaintenanceOutcome.Failed, ex.Message);
                }
                Log.Write("Duplicate deletion failed: " + ex);
                StatusText.Text = outcomesRecorded ? "Removal finished, but the results view could not be refreshed. See History." : "Could not complete duplicate removal: " + ex.Message;
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetRunning(false);
            }
        }

    }
}
