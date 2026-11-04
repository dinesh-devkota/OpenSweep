using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
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
        bool _hasResult;
        bool _recycling;
        string _scanRoot;
        DateTimeOffset _scannedAt;
        long _unreadableFolders;

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
            if (_cts != null || _recycling) return;
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
            if (_cts != null || _recycling) return;
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
            catch (OperationCanceledException)
            {
                SummaryText.Text = "Analysis cancelled." + (_hasResult ? " Previous results are still shown." : "");
            }
            catch (Exception ex)
            {
                Log.Write("Disk analysis failed: " + ex);
                SummaryText.Text = "Analysis failed: " + ex.Message + (_hasResult ? " Previous results are still shown." : "");
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
            AnalyzeButton.IsEnabled = BrowseButton.IsEnabled = DriveBox.IsEnabled = !running && !_recycling;
            // A scan must not race with deleting or exporting files from the previous result.
            Tabs.IsEnabled = !running && !_recycling && _hasResult;
        }

        void ShowResult(DiskScanResult r, TimeSpan took)
        {
            _hasResult = true;
            _scanRoot = r.Root.FullPath;
            _scannedAt = DateTimeOffset.Now;
            _unreadableFolders = r.Inaccessible;
            var text = $"{Fmt.Bytes(r.TotalBytes)} in {Fmt.Count(r.TotalFiles, "file")} and {Fmt.Count(r.Folders, "folder")}  ·  analyzed in {took.TotalSeconds:0.0} s";
            if (r.Inaccessible > 0)
                text += $"  ·  {Fmt.Count(r.Inaccessible, "folder")} couldn't be read" + (Elevation.IsAdmin ? "" : " (restart as administrator to include them)");
            SummaryText.Text = text;
            CategoryGrid.ItemsSource = r.Categories;
            _files = new ObservableCollection<BigFile>(r.LargestFiles);
            FilesGrid.ItemsSource = _files;
            FileStatus.Text = "";
            ExportButton.IsEnabled = _files.Count > 0;
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
            if (_cts != null || _recycling) return;
            var targets = Ui.Selected<BigFile>(FilesGrid).Select(file => new
                { File = file, Path = file.FullPath, Size = file.Size, Name = file.Name }).ToList();
            if (targets.Count == 0) { Ui.Info("Select one or more files in the list first (Ctrl+click selects several)."); return; }
            long total = targets.Sum(f => f.Size);
            var what = targets.Count == 1 ? $"“{targets[0].Name}” ({Fmt.Bytes(total)})" : $"{targets.Count} files ({Fmt.Bytes(total)})";
            if (!Ui.ConfirmWarn($"Move {what} to the Recycle Bin?\n\nOnly do this if you are sure you no longer need them. " +
                                "Windows asks first if something can't be recycled and would be deleted for good.", "Move to Recycle Bin")) return;

            using var history = MaintenanceHistory.Begin("Remove files from disk analysis");
            bool outcomesRecorded = false;
            _recycling = true;
            SetRunning(false);
            try
            {
                List<string> gone;
                using (Ui.Busy()) gone = ShellOps.Recycle(targets.Select(f => f.Path).ToList(), Ui.OwnerHandle);
                var goneSet = new HashSet<string>(gone, StringComparer.OrdinalIgnoreCase);
                var removed = targets.Where(f => goneSet.Contains(f.Path)).ToList();
                foreach (var target in targets)
                {
                    bool removedFile = goneSet.Contains(target.Path);
                    history.AddItem(new MaintenanceItem { Label = target.Path,
                        Status = removedFile ? "Removed from original location" : "Not removed",
                        Outcome = removedFile ? MaintenanceOutcome.Completed : MaintenanceOutcome.Unknown,
                        Files = removedFile ? (long?)1 : 0, Bytes = removedFile ? (long?)target.Size : null,
                        BytesKind = removedFile ? HistoryBytesKind.Recycled : HistoryBytesKind.None, Skipped = removedFile ? 0 : 1,
                        Recovery = removedFile ? RecoveryKind.RecycleBin : RecoveryKind.None,
                        Detail = removedFile ? "Logical size recorded during the scan, not measured disk space freed. Inspect the Recycle Bin; recovery is available only if Windows retained the file." :
                            "The path was not confirmed removed by this operation. It may have changed, been skipped, been cancelled, or become inaccessible." });
                }
                history.Complete(removed.Count == targets.Count ? MaintenanceOutcome.Completed : MaintenanceOutcome.Partial,
                    $"Removed {removed.Count} of {targets.Count} files; {Fmt.Bytes(removed.Sum(f => f.Size))} logical size from the scan. Physical free space was not measured.");
                outcomesRecorded = true;
                foreach (var target in removed) _files.Remove(target.File);
                ExportButton.IsEnabled = _files.Count > 0;
                FileStatus.Text = $"Removed {removed.Count} of {targets.Count} files from their original locations ({Fmt.Bytes(removed.Sum(f => f.Size))} logical size, not measured free space). Analyze again to refresh folder sizes.";
            }
            catch (Exception ex)
            {
                if (!outcomesRecorded)
                {
                    foreach (var target in targets)
                        history.AddItem(new MaintenanceItem { Label = target.Path, Status = "Outcome not confirmed", Outcome = MaintenanceOutcome.Unknown,
                            Failures = 1, Detail = "The Windows file operation did not return a confirmed result: " + ex.Message });
                    history.Complete(MaintenanceOutcome.Unknown, "File outcomes were not confirmed. Inspect the original paths and Recycle Bin.");
                }
                Log.Write("Disk file removal failed: " + ex);
                Ui.Error(outcomesRecorded ? "The removal results were recorded, but the list could not be refreshed. See History." : "Could not complete file removal:\n" + ex.Message);
            }
            finally
            {
                _recycling = false;
                SetRunning(false);
            }
        }

        void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null || _recycling || !_hasResult || _files.Count == 0) return;
            var dialog = new SaveFileDialog
            {
                Title = "Export largest files — includes full file paths",
                FileName = "OpenSweep-largest-files-" + _scannedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv",
                Filter = "CSV report (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                var report = new StringBuilder();
                void Row(params string[] fields) => report.Append(string.Join(",", fields.Select(CsvReport.EncodeCell))).Append("\r\n");
                Row("Name", "Full path", "Size (bytes)", "Type", "Modified (local)", "Scan completed", "Scan root", "Unreadable folders");
                // Export the displayed order, including the user's column sorting.
                foreach (var file in FilesGrid.Items.OfType<BigFile>())
                    Row(file.Name, file.FullPath, file.Size.ToString(CultureInfo.InvariantCulture), file.Category,
                        file.Modified == DateTime.MinValue ? "Unknown" : file.Modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        _scannedAt.ToString("o", CultureInfo.InvariantCulture), _scanRoot,
                        _unreadableFolders.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(dialog.FileName, report.ToString(), new UTF8Encoding(true));
                FileStatus.Text = $"Exported {Fmt.Count(_files.Count, "file")}. The report includes full paths and sizes recorded during the scan.";
            }
            catch (Exception ex)
            {
                Log.Write("Storage report export failed: " + ex);
                Ui.Error("Could not save the storage report:\n" + ex.Message);
            }
        }
    }
}
