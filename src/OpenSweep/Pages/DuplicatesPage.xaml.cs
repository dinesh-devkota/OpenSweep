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
            var marked = _groups.SelectMany(g => g.Files).Where(f => f.IsChecked).ToList();
            if (marked.Count == 0)
            {
                Ui.Info("Tick the copies you want to remove first, or use “Keep newest”.");
                return;
            }
            if (!Ui.ConfirmWarn($"Move {Fmt.Count(marked.Count, "selected duplicate file")} ({Fmt.Bytes(marked.Sum(f => f.Size))}) to the Recycle Bin?\n\n" +
                                "Files are rechecked before removal, and one verified copy is kept. Windows asks first if something can't be recycled and would be deleted for good.",
                                "Delete duplicates")) return;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetRunning(true);
            StatusText.Text = "Rechecking duplicate files…";
            try
            {
                using (Ui.Busy())
                using (var plan = await Task.Run(() => DuplicateFinder.PrepareDeletion(_groups, ct)))
                {
                    ct.ThrowIfCancellationRequested();
                    plan.RecheckPaths();
                    foreach (var skipped in plan.Skipped) skipped.Key.Status = skipped.Value;
                    if (plan.Files.Count == 0)
                    {
                        StatusText.Text = "Nothing was deleted. The files or their kept copies changed, are unavailable, or are excluded. Search again.";
                        return;
                    }
                    // The plan holds verified kept copies open until the shell operation has completed.
                    var gone = ShellOps.Recycle(plan.Files.Select(f => f.FullPath).ToList(), Ui.OwnerHandle);
                    var goneSet = new HashSet<string>(gone, StringComparer.OrdinalIgnoreCase);
                    int removed = 0; long freed = 0;
                    foreach (var file in plan.Files)
                    {
                        if (goneSet.Contains(file.FullPath)) { file.Group.Files.Remove(file); removed++; freed += file.Size; }
                        else file.Status = "Not removed (in use, protected or skipped)";
                    }
                    ShowGroups(_groups.Where(g => g.Files.Count > 1).ToList());
                    StatusText.Text = $"Removed {Fmt.Count(removed, "file")} ({Fmt.Bytes(freed)})." +
                                      (removed < plan.Files.Count ? $" {plan.Files.Count - removed} could not be removed." : "") +
                                      (plan.Skipped.Count > 0 ? $" {Fmt.Count(plan.Skipped.Count, "file")} skipped for safety. Search again." : "");
                }
            }
            catch (OperationCanceledException) { StatusText.Text = "Duplicate verification cancelled. Nothing was deleted."; }
            catch (Exception ex)
            {
                Log.Write("Duplicate deletion failed: " + ex);
                StatusText.Text = "Could not remove duplicates: " + ex.Message;
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
