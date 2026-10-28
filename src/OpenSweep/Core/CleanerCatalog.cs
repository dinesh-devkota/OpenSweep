using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static OpenSweep.Core.FileRule;

namespace OpenSweep.Core
{
    public static class CleanerCatalog
    {
        static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        static string P(params string[] parts) => Path.Combine(parts);

        public static List<CleanerGroup> Build()
        {
            var items = new List<CleanerItem>();
            AddWindows(items);
            AddChromium(items, "Microsoft Edge", "edge", P(Local, @"Microsoft\Edge\User Data"), null, "msedge");
            AddChromium(items, "Google Chrome", "chrome", P(Local, @"Google\Chrome\User Data"), null, "chrome");
            AddChromium(items, "Brave", "brave", P(Local, @"BraveSoftware\Brave-Browser\User Data"), null, "brave");
            AddChromium(items, "Vivaldi", "vivaldi", P(Local, @"Vivaldi\User Data"), null, "vivaldi");
            foreach (var (name, folder) in new[] { ("Opera", "Opera Stable"), ("Opera GX", "Opera GX Stable") })
                AddChromium(items, name, name.ToLowerInvariant().Replace(" ", ""), P(Roaming, "Opera Software", folder),
                    P(Local, "Opera Software", folder), "opera");
            AddFirefox(items);
            AddApps(items);
            AddDeveloper(items);

            items = items.Where(IsAvailable).ToList();
            var saved = AppSettings.Current.CheckedCleanerItems;
            var set = saved != null ? new HashSet<string>(saved) : null;
            foreach (var i in items) i.IsChecked = set?.Contains(i.Id) ?? i.DefaultOn;

            return items.GroupBy(i => i.Group).Select(g =>
            {
                var grp = new CleanerGroup { Name = g.Key, Icon = IconFor(g.Key) };
                foreach (var i in g) grp.Items.Add(i);
                return grp;
            }).ToList();
        }

        static bool IsAvailable(CleanerItem i)
        {
            if (i.Kind != CleanKind.Files) return true;
            try { return i.Rules().Any(r => Directory.Exists(Environment.ExpandEnvironmentVariables(r.Dir))); }
            catch { return false; }
        }

        static string IconFor(string group)
        {
            switch (group)
            {
                case "Windows": return "\uE770";
                case "Applications": return "\uE71D";
                case "Developer tools": return "\uE943";
                default: return "\uE774";
            }
        }

        static CleanerItem Item(string id, string group, string name, string desc, bool on, params FileRule[] rules) =>
            new CleanerItem { Id = id, Group = group, Name = name, Description = desc, DefaultOn = on, Rules = () => rules };

        static void AddWindows(List<CleanerItem> l)
        {
            const string g = "Windows";
            l.Add(Item("win.usertemp", g, "Temporary files", "Files left in your personal Temp folder.", true,
                Contents(Path.GetTempPath(), tempAge: true)));
            l.Add(new CleanerItem
            {
                Id = "win.systemtemp", Group = g, Name = "Windows temporary files", DefaultOn = true, NeedsAdmin = true,
                Description = "Files left in the Windows Temp folder.", Rules = () => new[] { Contents(P(Win, "Temp"), tempAge: true) }
            });
            l.Add(new CleanerItem { Id = "win.recycle", Group = g, Name = "Recycle Bin", Kind = CleanKind.RecycleBin, DefaultOn = true,
                Description = "Permanently deletes everything in the Recycle Bin on all drives." });
            l.Add(Item("win.thumbs", g, "Thumbnail cache", "Explorer picture thumbnails (rebuilt automatically).", true,
                Files(P(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db")));
            l.Add(Item("win.inetcache", g, "Internet cache (Windows)", "Cached web content used by Windows and Office.", true,
                Contents(P(Local, @"Microsoft\Windows\INetCache"))));
            l.Add(Item("win.wer", g, "Windows error reports", "Crash and problem reports that were already sent or queued.", true,
                Contents(P(ProgramData, @"Microsoft\Windows\WER\ReportArchive")), Contents(P(ProgramData, @"Microsoft\Windows\WER\ReportQueue")),
                Contents(P(ProgramData, @"Microsoft\Windows\WER\Temp")), Contents(P(Local, @"Microsoft\Windows\WER\ReportArchive")),
                Contents(P(Local, @"Microsoft\Windows\WER\ReportQueue"))));
            l.Add(Item("win.dumps", g, "Memory dumps", "Crash dump files created when Windows or apps crash.", true,
                Files(Win, "MEMORY.DMP"), Contents(P(Win, "Minidump")), Contents(P(Local, "CrashDumps")),
                Contents(P(Win, "LiveKernelReports"))));
            var logs = Tree(P(Win, "Logs"), "*.log", "*.etl", "*.cab");
            logs.UseTempAge = true;
            l.Add(new CleanerItem { Id = "win.logs", Group = g, Name = "Windows log files", DefaultOn = true, NeedsAdmin = true,
                Description = "Setup, update and servicing logs in the Windows\\Logs folder.", Rules = () => new[] { logs } });
            l.Add(Item("win.dxcache", g, "DirectX shader cache", "Graphics shader caches (rebuilt by games and apps).", true,
                Contents(P(Local, "D3DSCache")), Contents(P(Local, @"NVIDIA\DXCache")), Contents(P(Local, @"NVIDIA\GLCache")),
                Contents(P(Local, @"AMD\DxCache")), Contents(P(Local, @"AMD\DxcCache")), Contents(P(Local, @"AMD\GLCache")),
                Contents(P(Local, @"Intel\ShaderCache"))));
            l.Add(new CleanerItem { Id = "win.delivery", Group = g, Name = "Delivery Optimization files", Kind = CleanKind.DeliveryOptimization,
                DefaultOn = true, NeedsAdmin = true, Description = "Update files cached for sharing with other PCs." });
            l.Add(new CleanerItem { Id = "win.wucache", Group = g, Name = "Windows Update download cache", Kind = CleanKind.WindowsUpdateCache,
                NeedsAdmin = true, Description = "Already-downloaded update packages (briefly stops the Windows Update service)." });
            var prefetch = Files(P(Win, "Prefetch"), "*.pf");
            prefetch.MinAgeDays = 30;
            l.Add(new CleanerItem { Id = "win.prefetch", Group = g, Name = "Old Prefetch data", NeedsAdmin = true,
                Description = "Prefetch files for apps not used in the last 30 days.", Rules = () => new[] { prefetch } });
            l.Add(new CleanerItem { Id = "win.recent", Group = g, Name = "Recent documents list", IsPrivacy = true,
                Description = "Shortcuts in the Recent items list (your files are not touched).",
                Rules = () => new[] { Files(P(Roaming, @"Microsoft\Windows\Recent"), "*.lnk") } });
            l.Add(new CleanerItem { Id = "win.dns", Group = g, Name = "DNS cache", Kind = CleanKind.DnsCache, DefaultOn = true,
                Description = "Flushes cached DNS lookups." });
            l.Add(new CleanerItem { Id = "win.clipboard", Group = g, Name = "Clipboard", Kind = CleanKind.Clipboard, IsPrivacy = true,
                Description = "Clears the current clipboard contents." });
        }

        static IEnumerable<string> ChromiumProfiles(string userData)
        {
            if (!Directory.Exists(userData)) return Enumerable.Empty<string>();
            if (File.Exists(P(userData, "Preferences"))) return new[] { userData }; // Opera keeps a single profile in the root
            try { return Directory.GetDirectories(userData).Where(d => File.Exists(P(d, "Preferences"))).ToList(); }
            catch { return Enumerable.Empty<string>(); }
        }

        static void AddChromium(List<CleanerItem> l, string browser, string id, string userData, string localRoot, params string[] procs)
        {
            if (!Directory.Exists(userData)) return;
            IEnumerable<FileRule> Cache()
            {
                var roots = ChromiumProfiles(userData).ToList();
                if (localRoot != null) roots.Add(localRoot);
                foreach (var p in roots)
                    foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "ShaderCache" })
                        yield return Contents(P(p, sub));
                foreach (var sub in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" })
                    yield return Contents(P(userData, sub));
            }
            CleanerItem Make(string suffix, string name, string desc, bool on, bool privacy, Func<IEnumerable<FileRule>> rules) =>
                new CleanerItem { Id = $"{id}.{suffix}", Group = browser, Name = name, Description = desc, DefaultOn = on,
                    IsPrivacy = privacy, AppName = browser, Processes = procs, Rules = rules };

            l.Add(Make("cache", "Internet cache", $"Cached pages, images and scripts. {browser} stays signed in.", true, false, Cache));
            l.Add(Make("history", "History", "Browsing history, typed addresses and top sites.", false, true,
                () => ChromiumProfiles(userData).Select(p => Files(p, "History", "History-journal", "Visited Links", "Top Sites",
                    "Top Sites-journal", "Shortcuts", "Shortcuts-journal", "Network Action Predictor", "Network Action Predictor-journal"))));
            l.Add(Make("cookies", "Cookies", "Website cookies. This signs you out of websites.", false, true,
                () => ChromiumProfiles(userData).SelectMany(p => new[]
                    { Files(P(p, "Network"), "Cookies", "Cookies-journal"), Files(p, "Cookies", "Cookies-journal") })));
            l.Add(Make("session", "Session (open tabs)", "Saved tabs used to restore your last browsing session.", false, true,
                () => ChromiumProfiles(userData).SelectMany(p => new[]
                    { Contents(P(p, "Sessions")), Files(p, "Current Session", "Current Tabs", "Last Session", "Last Tabs") })));
        }

        static void AddFirefox(List<CleanerItem> l)
        {
            var roaming = P(Roaming, @"Mozilla\Firefox\Profiles");
            var local = P(Local, @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(roaming) && !Directory.Exists(local)) return;
            IEnumerable<string> Dirs(string root)
            {
                try { return Directory.Exists(root) ? Directory.GetDirectories(root) : new string[0]; }
                catch { return new string[0]; }
            }
            const string g = "Mozilla Firefox";
            var procs = new[] { "firefox" };
            l.Add(new CleanerItem { Id = "firefox.cache", Group = g, Name = "Internet cache", DefaultOn = true, AppName = g, Processes = procs,
                Description = "Cached pages, images and scripts. Firefox stays signed in.",
                Rules = () => Dirs(local).SelectMany(d => new[] { "cache2", "startupCache", "thumbnails", "jumpListCache", "OfflineCache" }
                    .Select(s => Contents(P(d, s)))) });
            l.Add(new CleanerItem { Id = "firefox.cookies", Group = g, Name = "Cookies", IsPrivacy = true, AppName = g, Processes = procs,
                Description = "Website cookies. This signs you out of websites.",
                Rules = () => Dirs(roaming).Select(d => Files(d, "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm")) });
            l.Add(new CleanerItem { Id = "firefox.session", Group = g, Name = "Session (open tabs)", IsPrivacy = true, AppName = g, Processes = procs,
                Description = "Saved tabs used to restore your last browsing session.",
                Rules = () => Dirs(roaming).SelectMany(d => new[] { Contents(P(d, "sessionstore-backups")), Files(d, "sessionstore.jsonlz4") }) });
        }

        static void AddApps(List<CleanerItem> l)
        {
            const string g = "Applications";
            CleanerItem App(string id, string name, string desc, string[] procs, Func<IEnumerable<FileRule>> rules) =>
                new CleanerItem { Id = id, Group = g, Name = name, Description = desc, AppName = name, Processes = procs, Rules = rules };

            var teamsNew = P(Local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView");
            l.Add(App("app.teams", "Microsoft Teams", "Teams web cache. You stay signed in.", new[] { "ms-teams", "Teams" }, () =>
            {
                var rules = new List<FileRule>();
                try
                {
                    if (Directory.Exists(teamsNew))
                        foreach (var d in Directory.GetDirectories(teamsNew))
                            rules.AddRange(new[] { "Cache", "Code Cache", "GPUCache" }.Select(s => Contents(P(d, s))));
                }
                catch { }
                rules.AddRange(new[] { "Cache", "Code Cache", "GPUCache", "tmp" }.Select(s => Contents(P(Roaming, @"Microsoft\Teams", s))));
                return rules;
            }));
            l.Add(App("app.vscode", "Visual Studio Code", "Editor caches, cached extension packages and logs.", new[] { "Code" },
                () => new[] { "Cache", "CachedData", "Code Cache", "GPUCache", "CachedExtensionVSIXs", "logs" }.Select(s => Contents(P(Roaming, "Code", s)))));
            l.Add(App("app.discord", "Discord", "Discord media cache.", new[] { "Discord" },
                () => new[] { "Cache", "Code Cache", "GPUCache" }.Select(s => Contents(P(Roaming, "discord", s)))));
            l.Add(App("app.slack", "Slack", "Slack media cache.", new[] { "slack" },
                () => new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" }.Select(s => Contents(P(Roaming, "Slack", s)))));
            l.Add(App("app.spotify", "Spotify", "Cached songs and artwork (re-downloaded when needed).", new[] { "Spotify" },
                () => new[] { Contents(P(Local, @"Spotify\Data")), Contents(P(Local, @"Spotify\Browser\Cache")) }));
        }

        static void AddDeveloper(List<CleanerItem> l)
        {
            const string g = "Developer tools";
            CleanerItem Dev(string id, string name, string desc, params FileRule[] rules) =>
                Item(id, g, name, desc + " Re-downloaded automatically when needed.", false, rules);
            l.Add(Dev("dev.npm", "npm cache", "Downloaded npm packages.", Contents(P(Local, "npm-cache"))));
            l.Add(Dev("dev.yarn", "Yarn cache", "Downloaded Yarn packages.", Contents(P(Local, @"Yarn\Cache"))));
            l.Add(Dev("dev.pip", "pip cache", "Downloaded Python packages.", Contents(P(Local, @"pip\Cache"))));
            l.Add(Dev("dev.nuget", "NuGet HTTP cache", "Cached NuGet feed responses (installed packages are kept).",
                Contents(P(Local, @"NuGet\v3-cache")), Contents(P(Local, @"NuGet\plugins-cache"))));
            l.Add(Dev("dev.go", "Go build cache", "Compiled Go build outputs.", Contents(P(Local, "go-build"))));
        }
    }
}
