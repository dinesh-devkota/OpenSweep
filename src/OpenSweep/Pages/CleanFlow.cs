using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using OpenSweep.Core;

namespace OpenSweep.Pages
{
    /// <summary>Shared pre-clean step: deals with programs that keep their files locked while running.</summary>
    public static class CleanFlow
    {
        /// <returns>The items to clean, or null when the user cancelled.</returns>
        public static async Task<List<CleanerItem>> PrepareAsync(List<CleanerItem> items)
        {
            var running = await Task.Run(() => items.Where(i => i.Processes.Length > 0 && i.IsAppRunning).ToList());
            if (running.Count == 0) return items;

            var apps = running.Select(i => i.AppName ?? i.Group).Distinct().ToList();
            if (!AppSettings.Current.CloseAppsAutomatically)
            {
                var answer = Ui.Show(
                    $"These programs are open: {string.Join(", ", apps)}.\n" +
                    "Their files are locked while they run, so they can't be cleaned properly.\n\n" +
                    "Yes – close them now (save your work first) and clean\n" +
                    "No – skip their items this time\n" +
                    "Cancel – don't clean",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, "Programs are running");
                if (answer == MessageBoxResult.Cancel) return null;
                if (answer == MessageBoxResult.No) return items.Except(running).ToList();
            }

            var processes = running.SelectMany(i => i.Processes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await Task.Run(() => AppCloser.Close(processes));
            Log.Write("Closed before cleaning: " + string.Join(", ", processes));
            return items;
        }
    }
}
