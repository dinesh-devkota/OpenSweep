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

        void KeepNewest_Click(object sender, RoutedEventArgs e) { DuplicateFinder.SelectAllButOne(_groups, true); UpdateSelected(); }
        void KeepOldest_Click(object sender, RoutedEventArgs e) { DuplicateFinder.SelectAllButOne(_groups, false); UpdateSelected(); }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var f in _groups.SelectMany(g => g.Files)) f.IsChecked = false;
            UpdateSelected();
        }

        void Show_Click(object sender, RoutedEventArgs e)
        {
            if (!Ui.IsRowOrButton(ResultsGrid, e)) return;
            if (ResultsGrid.SelectedItem is DupFile f) ShellOps.ShowInExplorer(File.Exists(f.FullPath) ? f.FullPath : f.Folder);
            else if (!(e is MouseButtonEventArgs)) Ui.Info("Select a file in the list first.");
        }

        static bool StillThere(DupFile f)
        {
            try { var fi = new FileInfo(f.FullPath); return fi.Exists && fi.Length == f.Size; }
            catch { return false; }
        }

        void Delete_Click(object sender, RoutedEventArgs e)
        {
            var plan = new List<DupFile>();
            int unsafeGroups = 0;
            foreach (var g in _groups)
            {
                var marked = g.Files.Where(f => f.IsChecked).ToList();
                if (marked.Count == 0) continue;
                // Never delete copies unless a kept copy is still on disk, unchanged.
                if (!g.Files.Where(f => !f.IsChecked).Any(StillThere))
                {
                    unsafeGroups++;
                    foreach (var f in marked) f.Status = "Skipped – the kept copy is missing or changed";
                    continue;
                }
                plan.AddRange(marked);
            }
            if (plan.Count == 0)
            {
                Ui.Info(unsafeGroups > 0 ? "Nothing was deleted: the copies you kept have changed or are gone. Search again."
                                         : "Tick the copies you want to remove first, or use “Select all but newest”.");
                return;
            }

            var bytes = plan.Sum(f => f.Size);
            if (!Ui.ConfirmWarn($"Move {Fmt.Count(plan.Count, "duplicate file")} ({Fmt.Bytes(bytes)}) to the Recycle Bin?\n\n" +
                                "One copy of every file is kept. Windows asks first if something can't be recycled and would be deleted for good.",
                                "Delete duplicates"))
                return;

            List<string> gone;
            using (Ui.Busy()) gone = ShellOps.Recycle(plan.Select(f => f.FullPath).ToList(), Ui.OwnerHandle);
            var goneSet = new HashSet<string>(gone, StringComparer.OrdinalIgnoreCase);
            int removed = 0; long freed = 0;
            foreach (var f in plan)
            {
                if (goneSet.Contains(f.FullPath)) { f.Group.Files.Remove(f); removed++; freed += f.Size; }
                else f.Status = "Not removed (in use, protected or skipped)";
            }
            ShowGroups(_groups.Where(g => g.Files.Count > 1).ToList());
            StatusText.Text = $"Moved {Fmt.Count(removed, "file")} ({Fmt.Bytes(freed)}) to the Recycle Bin." +
                              (removed < plan.Count ? $" {plan.Count - removed} could not be removed." : "") +
                              (unsafeGroups > 0 ? $" {Fmt.Count(unsafeGroups, "group")} skipped for safety." : "");
        }
    }
}
