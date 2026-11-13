using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace OpenSweep.Pages
{
    /// <summary>Pages that want to refresh or lazily load when they are shown.</summary>
    public interface IPage
    {
        void OnNavigatedTo();
    }

    public static class Ui
    {
        static int _busy;
        public static bool IsBusy => _busy > 0;

        /// <summary>Marks a long-running task so closing the window asks for confirmation.</summary>
        public static IDisposable Busy()
        {
            _busy++;
            return new Releaser(() => _busy--);
        }

        sealed class Releaser : IDisposable
        {
            Action _release;
            public Releaser(Action release) { _release = release; }
            public void Dispose() { _release?.Invoke(); _release = null; }
        }

        static Window Owner => Application.Current?.MainWindow is Window w && w.IsVisible ? w : null;

        public static MessageBoxResult Show(string text, MessageBoxButton buttons, MessageBoxImage icon, string title = "OpenSweep")
        {
            var owner = Owner;
            return owner != null
                ? MessageBox.Show(owner, text, title, buttons, icon)
                : MessageBox.Show(text, title, buttons, icon);
        }

        public static bool Confirm(string text, string title = "OpenSweep") =>
            Show(text, MessageBoxButton.YesNo, MessageBoxImage.Question, title) == MessageBoxResult.Yes;

        public static bool ConfirmWarn(string text, string title = "OpenSweep") =>
            Show(text, MessageBoxButton.YesNo, MessageBoxImage.Warning, title) == MessageBoxResult.Yes;

        public static void Info(string text, string title = "OpenSweep") =>
            Show(text, MessageBoxButton.OK, MessageBoxImage.Information, title);

        public static void Warn(string text, string title = "OpenSweep") =>
            Show(text, MessageBoxButton.OK, MessageBoxImage.Warning, title);

        public static void Error(string text, string title = "OpenSweep") =>
            Show(text, MessageBoxButton.OK, MessageBoxImage.Error, title);

        public static string PickFolder(string description, string initial = null)
        {
            using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = description, ShowNewFolderButton = false })
            {
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) d.SelectedPath = initial;
                return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null;
            }
        }

        public static List<T> Selected<T>(DataGrid grid) => grid.SelectedItems.OfType<T>().ToList();

        /// <summary>False when a grid double-click hit the header, a scrollbar or empty space rather than a row.</summary>
        public static bool IsRowOrButton(DataGrid grid, RoutedEventArgs e) =>
            !(e is MouseButtonEventArgs) ||
            (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(grid, d) is DataGridRow);

        public static IntPtr OwnerHandle =>
            MainWindow.Instance != null ? new WindowInteropHelper(MainWindow.Instance).Handle : IntPtr.Zero;

        public static void AppendLog(TextBox box, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (box.LineCount > 3000) box.Clear();
            box.AppendText(line + Environment.NewLine);
            box.ScrollToEnd();
        }

        public static void Go(string page) => MainWindow.Instance?.Navigate(page);
    }
}
