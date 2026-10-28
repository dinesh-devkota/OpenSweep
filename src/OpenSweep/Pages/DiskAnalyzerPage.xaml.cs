using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public sealed class DriveChoice
    {
        public string Label { get; set; }
        public string Path { get; set; }
    }

    public sealed class FolderRow
    {
        public DirNode Node { get; set; } // null for the "(files in this folder)" row
        public string Name { get; set; }
        public string Glyph => Node != null ? "\uE8B7" : "\uE8A5";
        public long Size { get; set; }
        public string SizeText => Fmt.Bytes(Size);
        public long Files { get; set; }
        public double Percent { get; set; }
        public string PercentText => Percent.ToString("0.0") + " %";
    }

    public partial class DiskAnalyzerPage : UserControl, IPage
    {
        readonly ObservableCollection<DriveChoice> _targets = new ObservableCollection<DriveChoice>();
        ObservableCollection<BigFile> _files = new ObservableCollection<BigFile>();
        CancellationTokenSource _cts;
        DirNode _current;
        bool _init;

        public DiskAnalyzerPage()
        {
            InitializeComponent();
            DriveBox.ItemsSource = _targets;
        }

        public void OnNavigatedTo()
        {
            if (_init) return;
            _init = true;
            foreach (var d in DiskAnalyzer.FixedDrives())
            {
                try
                {
                    var letter = d.Name.TrimEnd('\\');
                    var name = string.IsNullOrWhiteSpace(d.VolumeLabel)
                        ? (d.DriveType == DriveType.Removable ? "Removable drive" : "Local disk") : d.VolumeLabel;
                    _targets.Add(new DriveChoice
                    {
                        Path = d.RootDirectory.FullName,
                        Label = $"{name} ({letter})  ·  {Fmt.Bytes(d.AvailableFreeSpace)} free of {Fmt.Bytes(d.TotalSize)}",
                    });
                }
                catch (Exception ex) { Log.Write($"Drive {d.Name} skipped: {ex.Message}"); }
            }
            var sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
            DriveBox.SelectedItem = _targets.FirstOrDefault(t => string.Equals(t.Path, sys, StringComparison.OrdinalIgnoreCase))
                                    ?? _targets.FirstOrDefault();
        }

        async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            if (DriveBox.SelectedItem is DriveChoice target) await AnalyzeAsync(target.Path);
            else Ui.Info("Pick a drive or folder first.");
        }

        async void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dir = Ui.PickFolder("Choose a folder to analyze", (DriveBox.SelectedItem as DriveChoice)?.Path);
            if (dir == null) return;
            var choice = _targets.FirstOrDefault(t => string.Equals(t.Path, dir, StringComparison.OrdinalIgnoreCase));
            if (choice == null) _targets.Insert(0, choice = new DriveChoice { Path = dir, Label = "Folder  ·  " + dir });
            DriveBox.SelectedItem = choice;
            await AnalyzeAsync(dir);
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        async Task AnalyzeAsync(string path)
        {
            if (_cts != null) return;
            if (!Directory.Exists(path)) { Ui.Warn("That drive or folder isn't available any more."); return; }

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetRunning(true);
            SummaryText.Text = "Analyzing " + path + "…";
            var progress = new Progress<string>(s => SummaryText.Text = s);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (Ui.Busy())
                {
                    var r = await Task.Run(() => DiskAnalyzer.Scan(path, progress, ct));
                    ShowResult(r, watch.Elapsed);
                }
            }
            catch (OperationCanceledException) { SummaryText.Text = "Analysis cancelled."; }
            catch (Exception ex)
            {
                Log.Write("Disk analysis failed: " + ex);
                SummaryText.Text = "Analysis failed: " + ex.Message;
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
            ScanProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            AnalyzeButton.IsEnabled = BrowseButton.IsEnabled = DriveBox.IsEnabled = !running;
        }

        void ShowResult(DiskScanResult r, TimeSpan took)
        {
            var text = $"{Fmt.Bytes(r.TotalBytes)} in {Fmt.Count(r.TotalFiles, "file")} and {Fmt.Count(r.Folders, "folder")}  ·  analyzed in {took.TotalSeconds:0.0} s";
            if (r.Inaccessible > 0)
                text += $"  ·  {Fmt.Count(r.Inaccessible, "folder")} couldn't be read" + (Elevation.IsAdmin ? "" : " (restart as administrator to include them)");
            SummaryText.Text = text;
            CategoryGrid.ItemsSource = r.Categories;
            _files = new ObservableCollection<BigFile>(r.LargestFiles);
            FilesGrid.ItemsSource = _files;
            FileStatus.Text = "";
            Tabs.IsEnabled = true;
            ShowFolder(r.Root);
        }

        void ShowFolder(DirNode node)
        {
            _current = node;
            PathText.Text = node.FullPath;
            UpButton.IsEnabled = node.Parent != null;
            double total = Math.Max(1, node.Size);
            var rows = node.Children.OrderByDescending(c => c.Size)
                .Select(c => new FolderRow { Node = c, Name = c.Name, Size = c.Size, Files = c.Files, Percent = c.Size * 100 / total })
                .ToList();
            if (node.OwnFiles > 0)
            {
                var own = new FolderRow { Name = "(files in this folder)", Size = node.OwnFilesSize, Files = node.OwnFiles, Percent = node.OwnFilesSize * 100 / total };
                int i = rows.FindIndex(x => x.Size < own.Size);
                rows.Insert(i < 0 ? rows.Count : i, own);
            }
            FolderGrid.ItemsSource = rows;
            if (rows.Count > 0) FolderGrid.ScrollIntoView(rows[0]);
        }

        void Folder_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!Ui.IsRowOrButton(FolderGrid, e)) return;
            if (FolderGrid.SelectedItem is FolderRow row && row.Node != null && row.Node.Size + row.Node.Children.Count > 0)
                ShowFolder(row.Node);
        }

        void Up_Click(object sender, RoutedEventArgs e)
        {
            if (_current?.Parent == null) return;
            var from = _current;
            ShowFolder(_current.Parent);
            var row = ((List<FolderRow>)FolderGrid.ItemsSource).FirstOrDefault(x => x.Node == from);
            if (row != null) { FolderGrid.SelectedItem = row; FolderGrid.ScrollIntoView(row); }
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var path = (FolderGrid.SelectedItem as FolderRow)?.Node?.FullPath ?? _current?.FullPath;
            if (path != null) ShellOps.ShowInExplorer(path);
        }

        void ShowFile_Click(object sender, RoutedEventArgs e)
        {
            if (!Ui.IsRowOrButton(FilesGrid, e)) return;
            if (FilesGrid.SelectedItem is BigFile f) ShellOps.ShowInExplorer(File.Exists(f.FullPath) ? f.FullPath : f.Folder);
            else if (!(e is MouseButtonEventArgs)) Ui.Info("Select a file in the list first.");
        }

        void RecycleFile_Click(object sender, RoutedEventArgs e)
        {
            var files = Ui.Selected<BigFile>(FilesGrid);
            if (files.Count == 0) { Ui.Info("Select one or more files in the list first (Ctrl+click selects several)."); return; }
            long total = files.Sum(f => f.Size);
            var what = files.Count == 1 ? $"“{files[0].Name}” ({files[0].SizeText})" : $"{files.Count} files ({Fmt.Bytes(total)})";
            if (!Ui.ConfirmWarn($"Move {what} to the Recycle Bin?\n\nOnly do this if you are sure you no longer need them. " +
                                "Windows asks first if something can't be recycled and would be deleted for good.", "Move to Recycle Bin"))
                return;

            List<string> gone;
            using (Ui.Busy()) gone = ShellOps.Recycle(files.Select(f => f.FullPath).ToList(), Ui.OwnerHandle);
            var removed = files.Where(f => gone.Contains(f.FullPath, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var f in removed) _files.Remove(f);
            FileStatus.Text = removed.Count == files.Count
                ? $"Moved {Fmt.Count(removed.Count, "file")} ({Fmt.Bytes(removed.Sum(f => f.Size))}) to the Recycle Bin. Analyze again to refresh folder sizes."
                : $"{removed.Count} of {files.Count} files removed – the rest are in use, protected or were skipped.";
        }
    }
}
