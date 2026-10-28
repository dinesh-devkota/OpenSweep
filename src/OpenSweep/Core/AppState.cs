using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSweep.Core
{
    /// <summary>State shared by all pages, so a Health Check scan also fills the Cleaner, Registry and Updater pages.</summary>
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

    /// <summary>Runs analyze/clean over a set of cleaner items on a background thread.</summary>
    public static class CleanRunner
    {
        /// <param name="progress">Reports each item when it starts (Done=false) and when it finishes (Done=true).</param>
        public static Task AnalyzeAsync(IList<CleanerItem> items, IProgress<(CleanerItem Item, bool Done)> progress, CancellationToken ct) =>
            RunAsync(items, false, progress, ct);

        public static Task CleanAsync(IList<CleanerItem> items, IProgress<(CleanerItem Item, bool Done)> progress, CancellationToken ct) =>
            RunAsync(items, true, progress, ct);

        static Task RunAsync(IList<CleanerItem> items, bool clean, IProgress<(CleanerItem, bool)> progress, CancellationToken ct) => Task.Run(() =>
        {
            foreach (var i in items) i.ResetResult();
            long bytes = 0, files = 0;
            foreach (var i in items)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((i, false));
                if (clean) CleanerEngine.Clean(i, ct); else CleanerEngine.Analyze(i, ct);
                bytes += i.Bytes; files += i.Files;
                progress?.Report((i, true));
            }
            if (clean) Log.Write($"Cleaned {items.Count} items: {Fmt.Count(files, "file")}, {Fmt.Bytes(bytes)}");
        }, ct);
    }
}
