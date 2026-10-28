using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;

namespace OpenSweep.Core
{
    public enum CleanKind { Files, RecycleBin, DnsCache, Clipboard, DeliveryOptimization, WindowsUpdateCache }

    public sealed class FileRule
    {
        public string Dir;
        public string[] Patterns = { "*" };
        public bool Recursive = true;
        public bool RemoveEmptyDirs = true;
        public bool UseTempAge;
        public int MinAgeDays;

        /// <summary>Everything inside a folder (the folder itself is kept).</summary>
        public static FileRule Contents(string dir, bool tempAge = false) => new FileRule { Dir = dir, UseTempAge = tempAge };

        /// <summary>Matching files directly inside a folder.</summary>
        public static FileRule Files(string dir, params string[] patterns) =>
            new FileRule { Dir = dir, Patterns = patterns, Recursive = false, RemoveEmptyDirs = false };

        /// <summary>Matching files in a folder tree; folders are kept.</summary>
        public static FileRule Tree(string dir, params string[] patterns) =>
            new FileRule { Dir = dir, Patterns = patterns, RemoveEmptyDirs = false };
    }

    public sealed class CleanerItem : ObservableObject
    {
        public string Id { get; set; }
        public string Group { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool DefaultOn { get; set; }
        public bool NeedsAdmin { get; set; }
        public bool IsPrivacy { get; set; }
        public CleanKind Kind { get; set; } = CleanKind.Files;
        public string AppName { get; set; }
        public string[] Processes { get; set; } = new string[0];
        public Func<IEnumerable<FileRule>> Rules { get; set; } = () => Enumerable.Empty<FileRule>();

        bool _checked;
        public bool IsChecked { get => _checked; set => Set(ref _checked, value); }

        long _bytes;
        public long Bytes { get => _bytes; set { Set(ref _bytes, value); Raise(nameof(SizeText)); } }

        long _files;
        public long Files { get => _files; set => Set(ref _files, value); }

        bool _hasResult;
        public bool HasResult { get => _hasResult; set { Set(ref _hasResult, value); Raise(nameof(SizeText)); } }

        string _status = "";
        public string Status { get => _status; set => Set(ref _status, value); }

        public string SizeText => HasResult && Bytes > 0 ? Fmt.Bytes(Bytes) : "";
        public string Tip => Description + (NeedsAdmin ? "  (needs administrator)" : "") +
                             (IsPrivacy ? "  (privacy: may sign you out or remove history)" : "");
        public bool IsBlockedByAdmin => NeedsAdmin && !Elevation.IsAdmin;
        public bool CanRun => !IsBlockedByAdmin;
        public bool IsAppRunning => Processes.Any(p => { try { return Process.GetProcessesByName(p).Length > 0; } catch { return false; } });

        public void ResetResult() { Bytes = 0; Files = 0; HasResult = false; Status = ""; }
    }

    public sealed class CleanerGroup : ObservableObject
    {
        public string Name { get; set; }
        public string Icon { get; set; }
        public ObservableCollection<CleanerItem> Items { get; } = new ObservableCollection<CleanerItem>();

        public CleanerGroup()
        {
            Items.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (CleanerItem i in e.NewItems)
                        i.PropertyChanged += (o, a) => { if (a.PropertyName == nameof(CleanerItem.IsChecked)) Raise(nameof(AllChecked)); };
            };
        }

        public bool? AllChecked
        {
            get
            {
                var runnable = Items.Where(i => i.CanRun).ToList();
                int on = runnable.Count(i => i.IsChecked);
                return on == 0 ? false : on == runnable.Count ? true : (bool?)null;
            }
            set
            {
                bool v = value ?? false;
                foreach (var i in Items) i.IsChecked = v && i.CanRun;
                Raise();
            }
        }
    }

    public sealed class CleanTotals { public long Bytes, Files, Failed; }

    public static class CleanerEngine
    {
        static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        public static void Analyze(CleanerItem item, CancellationToken ct) => Run(item, false, ct);
        public static void Clean(CleanerItem item, CancellationToken ct) => Run(item, true, ct);

        static void Run(CleanerItem item, bool delete, CancellationToken ct)
        {
            var t = new CleanTotals();
            string note = null;
            try
            {
                switch (item.Kind)
                {
                    case CleanKind.Files:
                        foreach (var r in item.Rules()) ProcessRule(r, delete, t, ct);
                        break;
                    case CleanKind.RecycleBin:
                        var (size, count) = NativeMethods.QueryRecycleBin();
                        t.Bytes = size; t.Files = count;
                        if (delete && count > 0)
                            NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
                        break;
                    case CleanKind.DnsCache:
                        if (delete) NativeMethods.DnsFlushResolverCache();
                        note = delete ? "Flushed" : "Will be flushed";
                        break;
                    case CleanKind.Clipboard:
                        if (delete) System.Windows.Application.Current?.Dispatcher.Invoke(() => System.Windows.Clipboard.Clear());
                        note = delete ? "Cleared" : "Will be cleared";
                        break;
                    case CleanKind.DeliveryOptimization:
                        CleanDeliveryOptimization(delete, t, ct);
                        break;
                    case CleanKind.WindowsUpdateCache:
                        CleanWindowsUpdateCache(delete, t, ct);
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { note = "Error: " + ex.Message; Log.Write($"{item.Id}: {ex}"); }

            item.Bytes = t.Bytes;
            item.Files = t.Files;
            item.HasResult = true;
            item.Status = note ?? (delete
                ? (t.Failed > 0 ? $"Removed {Fmt.Count(t.Files, "file")} · {t.Failed:N0} in use (skipped)" : $"Removed {Fmt.Count(t.Files, "file")}")
                : (t.Files > 0 ? Fmt.Count(t.Files, "file") : "Nothing to clean"));
        }

        static void ProcessRule(FileRule r, bool delete, CleanTotals t, CancellationToken ct)
        {
            var dir = Environment.ExpandEnvironmentVariables(r.Dir ?? "");
            if (dir.Length < 4 || !Directory.Exists(dir) || IsProtected(dir)) return;
            var s = AppSettings.Current;
            if (s.IsExcluded(dir)) return;
            DateTime? cutoff = null;
            if (r.MinAgeDays > 0) cutoff = DateTime.UtcNow.AddDays(-r.MinAgeDays);
            else if (r.UseTempAge && s.OnlyOldTempFiles) cutoff = DateTime.UtcNow.AddHours(-24);
            Walk(new DirectoryInfo(dir), r, cutoff, delete, t, ct);
        }

        static void Walk(DirectoryInfo dir, FileRule r, DateTime? cutoff, bool delete, CleanTotals t, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            FileInfo[] files;
            try { files = dir.GetFiles(); } catch { return; }
            foreach (var f in files)
            {
                if (!Glob.MatchesAny(f.Name, r.Patterns)) continue;
                if (cutoff.HasValue && Newest(f) > cutoff.Value) continue;
                if (AppSettings.Current.IsExcluded(f.FullName)) continue;
                long len;
                try { len = f.Length; } catch { continue; }
                if (!delete) { t.Bytes += len; t.Files++; continue; }
                try
                {
                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                    f.Delete();
                    t.Bytes += len; t.Files++;
                }
                catch { t.Failed++; }
            }
            if (!r.Recursive) return;
            DirectoryInfo[] subs;
            try { subs = dir.GetDirectories(); } catch { return; }
            foreach (var sub in subs)
            {
                // Never follow junctions / symlinks: they can point outside the folder being cleaned.
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (AppSettings.Current.IsExcluded(sub.FullName)) continue;
                Walk(sub, r, cutoff, delete, t, ct);
                if (delete && r.RemoveEmptyDirs)
                {
                    try
                    {
                        if ((!cutoff.HasValue || sub.CreationTimeUtc <= cutoff.Value) && !sub.EnumerateFileSystemInfos().Any())
                            sub.Delete(false);
                    }
                    catch { }
                }
            }
        }

        static readonly HashSet<string> Protected = new HashSet<string>(new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.CommonDocuments,
        }.Select(Environment.GetFolderPath).Where(p => !string.IsNullOrEmpty(p))
         .Concat(new[] { Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Users"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
         .Select(p => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);

        /// <summary>Safety net: never clean a drive root or a key system/user folder itself, even if an environment variable is misconfigured.</summary>
        static bool IsProtected(string dir)
        {
            try
            {
                var full = Path.GetFullPath(dir).TrimEnd('\\');
                return full.Length <= 3 || Protected.Contains(full);
            }
            catch { return true; }
        }

        static DateTime Newest(FileSystemInfo f)
        {
            var c = f.CreationTimeUtc; var w = f.LastWriteTimeUtc;
            return c > w ? c : w;
        }

        static void CleanDeliveryOptimization(bool delete, CleanTotals t, CancellationToken ct)
        {
            var dir = Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");
            var before = new CleanTotals();
            ProcessRule(FileRule.Contents(dir), false, before, ct);
            if (!delete) { t.Bytes = before.Bytes; t.Files = before.Files; return; }
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            ProcessRunner.RunAsync(ps, "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force\"", null, ct)
                .GetAwaiter().GetResult();
            var after = new CleanTotals();
            ProcessRule(FileRule.Contents(dir), false, after, ct);
            t.Bytes = Math.Max(0, before.Bytes - after.Bytes);
            t.Files = Math.Max(0, before.Files - after.Files);
        }

        static void CleanWindowsUpdateCache(bool delete, CleanTotals t, CancellationToken ct)
        {
            var rule = FileRule.Contents(Path.Combine(Win, @"SoftwareDistribution\Download"));
            if (!delete) { ProcessRule(rule, false, t, ct); return; }
            var restart = new List<ServiceController>();
            foreach (var name in new[] { "wuauserv", "bits" })
            {
                try
                {
                    var sc = new ServiceController(name);
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        restart.Add(sc);
                    }
                }
                catch (Exception ex) { Log.Write($"Stop {name}: {ex.Message}"); }
            }
            try { ProcessRule(rule, true, t, ct); }
            finally
            {
                foreach (var sc in restart)
                    try { sc.Start(); } catch (Exception ex) { Log.Write($"Start {sc.ServiceName}: {ex.Message}"); }
            }
        }
    }

    public static class Glob
    {
        static readonly ConcurrentDictionary<string, Regex> Cache = new ConcurrentDictionary<string, Regex>();

        public static bool MatchesAny(string name, string[] patterns)
        {
            foreach (var p in patterns)
            {
                if (p == "*") return true;
                var rx = Cache.GetOrAdd(p, x => new Regex("^" + Regex.Escape(x).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                if (rx.IsMatch(name)) return true;
            }
            return false;
        }
    }

    public static class AppCloser
    {
        /// <summary>Asks running apps to close, then force-closes anything still running after a few seconds.</summary>
        public static void Close(IEnumerable<string> processNames)
        {
            var names = processNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var n in names)
                foreach (var p in Process.GetProcessesByName(n))
                    try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
            var until = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < until && names.Any(n => Process.GetProcessesByName(n).Length > 0)) Thread.Sleep(300);
            foreach (var n in names)
                foreach (var p in Process.GetProcessesByName(n))
                    try { p.Kill(); } catch { }
            Thread.Sleep(800);
        }
    }
}
