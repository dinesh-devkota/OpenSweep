using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class UninstallPage : UserControl, IPage
    {
        List<InstalledProgram> _all = new List<InstalledProgram>();
        ICollectionView _view;
        bool _loaded;

        public UninstallPage()
        {
            InitializeComponent();
        }

        public async void OnNavigatedTo()
        {
            if (_loaded) return;
            _loaded = true;
            await RefreshAsync();
        }

        async Task RefreshAsync()
        {
            CountText.Text = "Loading…";
            try
            {
                _all = await Task.Run(() => InstalledPrograms.Load()
                    .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
            }
            catch (Exception ex)
            {
                Log.Write("Program list failed: " + ex);
                Ui.Error("Could not read the installed programs:\n" + ex.Message);
            }
            _view = CollectionViewSource.GetDefaultView(_all);
            _view.Filter = Matches;
            ProgramsGrid.ItemsSource = _view;
            UpdateCount();
        }

        bool Matches(object o)
        {
            var q = SearchBox.Text.Trim();
            if (q.Length == 0) return true;
            var p = (InstalledProgram)o;
            return (p.Name ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   (p.Publisher ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        void UpdateCount()
        {
            var shown = _view?.Cast<InstalledProgram>().ToList() ?? new List<InstalledProgram>();
            var size = shown.Sum(p => p.Size);
            CountText.Text = (shown.Count == _all.Count ? Fmt.Count(_all.Count, "program") : $"{shown.Count} of {_all.Count} programs") +
                             (size > 0 ? $" · {Fmt.Bytes(size)}" : "");
        }

        void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _view?.Refresh();
            UpdateCount();
        }

        InstalledProgram Selected()
        {
            if (ProgramsGrid.SelectedItem is InstalledProgram p) return p;
            Ui.Info("Select a program in the list first.");
            return null;
        }

        async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (!Ui.IsRowOrButton(ProgramsGrid, e)) return;
            var p = Selected();
            if (p == null) return;
            if (!Ui.Confirm($"Uninstall {p.Name}?\n\nIts own uninstaller will open – follow its steps.", "Uninstall")) return;

            Process proc;
            try { proc = InstalledPrograms.Uninstall(p); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return; } // UAC prompt cancelled
            catch (Exception ex)
            {
                Ui.Error($"Could not start the uninstaller:\n{ex.Message}\n\nIf the program is already gone, use Remove entry to tidy up the list.");
                return;
            }

            StatusText.Text = $"Waiting for the {p.Name} uninstaller to finish…";
            using (Ui.Busy())
            {
                if (proc != null) await Task.Run(() => { try { proc.WaitForExit(); } catch { } });
                await Task.Delay(1500);
            }
            StatusText.Text = "";
            await RefreshAsync();
        }

        async void RemoveEntry_Click(object sender, RoutedEventArgs e)
        {
            var p = Selected();
            if (p == null) return;
            if (p.Hive == "HKLM" && !Elevation.IsAdmin)
            {
                Ui.Warn("This entry is installed for all users and can only be removed after “Restart as administrator”.");
                return;
            }
            if (!Ui.ConfirmWarn($"Remove “{p.Name}” from the list without running its uninstaller?\n\n" +
                                "Only do this for programs that are already gone. The entry is backed up to a .reg file first.", "Remove entry"))
                return;
            try
            {
                await Task.Run(() => InstalledPrograms.RemoveEntry(p));
                _all.Remove(p);
                _view?.Refresh();
                UpdateCount();
            }
            catch (Exception ex) { Ui.Error("Could not remove the entry:\n" + ex.Message); }
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var p = Selected();
            if (p == null) return;
            var dir = p.InstallLocation?.Trim().Trim('"');
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                var exe = CommandLine.ExePath(p.UninstallString ?? "");
                dir = !string.IsNullOrEmpty(exe) && !exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(exe) : null;
            }
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) ShellOps.ShowInExplorer(dir);
            else Ui.Info("This program doesn't record its install folder.");
        }

        async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        void Settings_Click(object sender, RoutedEventArgs e) => ShellOps.Open("ms-settings:appsfeatures");
    }
}
