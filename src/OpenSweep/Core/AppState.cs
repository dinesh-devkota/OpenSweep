using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace OpenSweep.Core
{
    /// <summary>Shared cleanup and update results, plus registry results from the separate advanced tool.</summary>
    public sealed class AppState
    {
        public static AppState Instance { get; } = new AppState();

        List<CleanerGroup> _groups;
        public List<CleanerGroup> Groups => _groups ?? (_groups = CleanerCatalog.Build());
        public IEnumerable<CleanerItem> AllItems => Groups.SelectMany(g => g.Items);
        public List<CleanerItem> CheckedItems => AllItems.Where(i => i.IsChecked && !i.IsBlockedByAdmin).ToList();

        public ObservableCollection<RegistryIssue> RegistryIssues { get; } = new ObservableCollection<RegistryIssue>();
        public ObservableCollection<AppUpdate> AppUpdates { get; } = new ObservableCollection<AppUpdate>();
        public ObservableCollection<WinUpdate> WinUpdates { get; } = new ObservableCollection<WinUpdate>();
        public bool RegistryScanned { get; set; }
        public bool AppUpdatesChecked { get; set; }
        public bool WinUpdatesChecked { get; set; }

        /// <summary>Remembers which cleaner items are checked (keeping ids of items not present on this PC).</summary>
        public void SaveChecked()
        {
            var present = new HashSet<string>(AllItems.Select(i => i.Id));
            var keep = (AppSettings.Current.CheckedCleanerItems ?? new List<string>()).Where(id => !present.Contains(id));
            AppSettings.Current.CheckedCleanerItems = keep.Concat(AllItems.Where(i => i.IsChecked).Select(i => i.Id)).ToList();
            AppSettings.Current.Save();
        }

        public void ResetCheckedToDefaults()
        {
            foreach (var i in AllItems) i.IsChecked = i.DefaultOn && !i.IsBlockedByAdmin;
            AppSettings.Current.CheckedCleanerItems = null;
            AppSettings.Current.Save();
        }
    }

    public static class CollectionExtensions
    {
        public static void ReplaceWith<T>(this ObservableCollection<T> c, IEnumerable<T> items)
        {
            c.Clear();
            foreach (var i in items) c.Add(i);
        }
    }

}
