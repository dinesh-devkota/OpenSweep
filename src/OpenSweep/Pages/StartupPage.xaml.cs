using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    public partial class StartupPage : UserControl, IPage
    {
        readonly ObservableCollection<StartupEntry> _items = new ObservableCollection<StartupEntry>();
        bool _loaded;

        public StartupPage()
        {
            InitializeComponent();
            EntriesGrid.ItemsSource = _items;
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
                var list = await Task.Run(() => StartupManager.Load());
                _items.ReplaceWith(list.OrderByDescending(e => e.Enabled).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase));
            }
            catch (Exception ex)
            {
                Log.Write("Startup list failed: " + ex);
                Ui.Error("Could not read the startup items:\n" + ex.Message);
            }
            UpdateCount();
        }

        void UpdateCount() =>
            CountText.Text = $"{Fmt.Count(_items.Count, "startup item")} · {_items.Count(e => e.Enabled)} enabled";

        static string Explain(Exception ex) =>
            ex is UnauthorizedAccessException || ex is SecurityException
                ? "this item applies to all users – restart OpenSweep as administrator to change it"
                : ex.Message;

        void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb) || !(cb.DataContext is StartupEntry entry)) return;
            bool want = cb.IsChecked == true;
            try { StartupManager.SetEnabled(entry, want); }
            catch (Exception ex)
            {
                cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, entry.Enabled);
                Ui.Warn($"“{entry.Name}” could not be changed: {Explain(ex)}.");
            }
            UpdateCount();
        }

        void SetSelected(bool enabled)
        {
            var selected = Ui.Selected<StartupEntry>(EntriesGrid);
            if (selected.Count == 0) { Ui.Info("Select one or more items in the list first."); return; }
            var errors = new List<string>();
            foreach (var entry in selected.Where(x => x.Enabled != enabled))
            {
                try { StartupManager.SetEnabled(entry, enabled); }
                catch (Exception ex) { errors.Add($"{entry.Name}: {Explain(ex)}"); }
            }
            UpdateCount();
            if (errors.Count > 0) Ui.Warn("Some items could not be changed:\n\n" + string.Join("\n", errors));
        }

        void Enable_Click(object sender, RoutedEventArgs e) => SetSelected(true);
        void Disable_Click(object sender, RoutedEventArgs e) => SetSelected(false);

        async void Delete_Click(object sender, RoutedEventArgs e)
        {
            var selected = Ui.Selected<StartupEntry>(EntriesGrid);
            if (selected.Count == 0) { Ui.Info("Select one or more items in the list first."); return; }
            var names = string.Join("\n", selected.Take(10).Select(x => "• " + x.Name)) + (selected.Count > 10 ? "\n…" : "");
            if (!Ui.ConfirmWarn($"Delete {Fmt.Count(selected.Count, "startup item")}?\n\n{names}\n\n" +
                                "The programs stay installed. Registry entries are backed up and shortcuts go to the Recycle Bin. " +
                                "Disable is usually the safer choice.", "Delete startup items"))
                return;

            var errors = new List<string>();
            using (Ui.Busy())
            {
                foreach (var entry in selected)
                {
                    try
                    {
                        await Task.Run(() => StartupManager.Delete(entry));
                        _items.Remove(entry);
                    }
                    catch (Exception ex) { errors.Add($"{entry.Name}: {Explain(ex)}"); }
                }
            }
            UpdateCount();
            if (errors.Count > 0) Ui.Warn("Some items could not be deleted:\n\n" + string.Join("\n", errors));
        }

        void OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (!Ui.IsRowOrButton(EntriesGrid, e)) return;
            if (!(EntriesGrid.SelectedItem is StartupEntry entry)) { Ui.Info("Select an item in the list first."); return; }
            if (!string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath)) ShellOps.ShowInExplorer(entry.FilePath);
            else if (!string.IsNullOrEmpty(entry.ShortcutPath) && File.Exists(entry.ShortcutPath)) ShellOps.ShowInExplorer(entry.ShortcutPath);
            else Ui.Info("The program file was not found:\n\n" + (entry.FilePath ?? entry.Command));
        }

        async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        void Settings_Click(object sender, RoutedEventArgs e) => ShellOps.Open("ms-settings:startupapps");
    }
}
