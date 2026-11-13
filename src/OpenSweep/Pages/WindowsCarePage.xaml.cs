using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class WindowsCarePage : UserControl, IPage
    {
        WindowsCareSnapshot _snapshot;
        bool _refreshing;

        public WindowsCarePage()
        {
            InitializeComponent();
            GuidesList.ItemsSource = WindowsCareCatalog.Guides;
        }

        public async void OnNavigatedTo()
        {
            if (_snapshot == null) await RefreshAsync();
        }

        async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        async Task RefreshAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            RefreshButton.IsEnabled = CopyButton.IsEnabled = SaveButton.IsEnabled = false;
            RefreshProgress.Visibility = Visibility.Visible;
            SnapshotStatus.Text = "Reading system details…";
            ReportStatus.Text = "";
            try
            {
                await SystemInfo.EnsureLoadedAsync();
                _snapshot = await Task.Run(() => WindowsCareDiagnostics.Capture());
                SnapshotValues.DataContext = _snapshot;
                SnapshotStatus.Text = "Read at " + _snapshot.CapturedAt.LocalDateTime.ToString("g") +
                    ". Unavailable values could not be read. Refresh to try again.";
            }
            catch (Exception ex)
            {
                // Do not expose arbitrary diagnostic exception text in the sharable report.
                Log.Write("Windows care snapshot failed: " + ex.GetType().Name);
                SnapshotValues.DataContext = _snapshot ?? new WindowsCareSnapshot(DateTimeOffset.Now, null, null, null, null, null);
                SnapshotStatus.Text = _snapshot == null
                    ? "System details could not be read. Try Refresh again."
                    : "Refresh could not finish. The previous snapshot is still shown.";
            }
            finally
            {
                _refreshing = false;
                RefreshButton.IsEnabled = true;
                CopyButton.IsEnabled = SaveButton.IsEnabled = _snapshot != null;
                RefreshProgress.Visibility = Visibility.Collapsed;
            }
        }

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_refreshing || _snapshot == null) return;
            try
            {
                Clipboard.SetText(_snapshot.ToReport());
                ReportStatus.Text = "Report copied. It includes only the system details shown above.";
            }
            catch (Exception ex)
            {
                Log.Write("Copying Windows care report failed: " + ex.GetType().Name);
                Ui.Warn("The report could not be copied. Another app may be using the clipboard; try again.");
            }
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_refreshing || _snapshot == null) return;
            var dialog = new SaveFileDialog
            {
                Title = "Save Windows care report",
                Filter = "Text report (*.txt)|*.txt",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "OpenSweep-Windows-care-" + _snapshot.CapturedAt.LocalDateTime.ToString("yyyyMMdd-HHmm") + ".txt"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                File.WriteAllText(dialog.FileName, _snapshot.ToReport(), new UTF8Encoding(false));
                ReportStatus.Text = "Report saved to the location you selected.";
            }
            catch (Exception ex)
            {
                Log.Write("Saving Windows care report failed: " + ex.GetType().Name);
                Ui.Error("The report could not be saved. Check that the location is writable, then try again.");
            }
        }

        void GuideAction_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is WindowsCareAction action)) return;
            if (action.IsExternal)
            {
                if (WindowsCareCatalog.TryGetExternalTarget(action.Key, out var target)) ShellOps.Open(target);
            }
            else if (WindowsCareCatalog.IsInternalPage(action.Key)) Ui.Go(action.Key);
        }

        void PerformanceGuide_Click(object sender, RoutedEventArgs e)
        {
            if (WindowsCareCatalog.TryGetExternalTarget("performance-guide", out var target)) ShellOps.Open(target);
        }
    }
}
