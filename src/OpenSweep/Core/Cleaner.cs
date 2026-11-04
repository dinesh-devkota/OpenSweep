using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

        public long Failures { get; internal set; }
        public long Skipped { get; internal set; }
        public bool WasCancelled { get; internal set; }
        public bool HadError { get; internal set; }
        public bool CountsKnown { get; internal set; } = true;
        public string ResultDetail { get; internal set; }
        public void ResetResult()
        {
            Bytes = 0; Files = 0; HasResult = false; Status = "";
            Failures = 0; Skipped = 0; WasCancelled = false; HadError = false; CountsKnown = true; ResultDetail = null;
        }
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

    public sealed class CleanTotals
    {
        public long Bytes, Files, Failed, Skipped;
        public bool CountsKnown = true;
        public string Detail;
    }

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
                ct.ThrowIfCancellationRequested();
                switch (item.Kind)
                {
                    case CleanKind.Files:
                        foreach (var r in item.Rules()) ProcessRule(r, delete, t, ct);
                        break;
                    case CleanKind.RecycleBin:
                        if (!NativeMethods.TryQueryRecycleBin(out var size, out var count))
                        {
                            t.CountsKnown = false;
                            throw new InvalidOperationException("Windows could not read the Recycle Bin contents.");
                        }
                        if (!delete) { t.Bytes = size; t.Files = count; break; }
                        if (count > 0)
                        {
                            int result = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
                            if (NativeMethods.TryQueryRecycleBin(out var remainingSize, out var remainingCount))
                            {
                                t.Bytes = Math.Max(0, size - remainingSize);
                                t.Files = Math.Max(0, count - remainingCount);
                                t.Detail = "Recycle Bin totals are the observed change before and after emptying; other apps can change its contents at the same time.";
                            }
                            else t.CountsKnown = false;
                            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
                            note = t.CountsKnown ? null : "Emptied; result counts unavailable";
                        }
                        break;
                    case CleanKind.DnsCache:
                        t.CountsKnown = false;
                        if (delete && !NativeMethods.DnsFlushResolverCache())
                            throw new InvalidOperationException("Windows could not flush the DNS cache.");
                        note = delete ? "Flushed" : "Will be flushed";
                        break;
                    case CleanKind.Clipboard:
                        t.CountsKnown = false;
                        if (delete) System.Windows.Application.Current?.Dispatcher.Invoke(() => System.Windows.Clipboard.Clear());
                        note = delete ? "Cleared" : "Will be cleared";
                        break;
                    case CleanKind.DeliveryOptimization:
                        if (HasExcludedCachePath(DeliveryOptimizationPath))
                        {
                            t.Skipped++;
                            note = "Skipped: excluded cache folders";
                        }
                        else
                            CleanDeliveryOptimization(delete, t, ct);
                        break;
                    case CleanKind.WindowsUpdateCache:
                        CleanWindowsUpdateCache(delete, t, ct);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                item.WasCancelled = true;
                note = delete ? $"Cancelled · removed {Fmt.Count(t.Files, "file")}" : "Analysis cancelled";
                throw;
            }
            catch (Exception ex)
            {
                item.HadError = true; t.Failed++;
                note = "Error: " + ex.Message; t.Detail = ex.Message;
                Log.Write($"{item.Id}: {ex}");
            }
            finally
            {
                // An interrupted item may already have changed files: retain its actual partial totals.
                item.Bytes = t.Bytes; item.Files = t.Files; item.Failures = t.Failed; item.Skipped = t.Skipped;
                item.CountsKnown = t.CountsKnown;
                item.ResultDetail = t.Detail;
                item.HasResult = true;
                item.Status = note ?? (delete
                    ? $"Removed {Fmt.Count(t.Files, "file")}" + (t.Failed > 0 ? $" · {t.Failed:N0} failed" : "") + (t.Skipped > 0 ? $" · {t.Skipped:N0} skipped" : "")
                    : (t.Files > 0 ? Fmt.Count(t.Files, "file") : "Nothing to clean"));
            }
        }

        static void ProcessRule(FileRule r, bool delete, CleanTotals t, CancellationToken ct)
        {
            var dir = Environment.ExpandEnvironmentVariables(r.Dir ?? "");
            if (dir.Length < 4 || !Path.IsPathRooted(dir)) { t.Skipped++; return; }
            try
            {
                var attributes = File.GetAttributes(dir);
                if ((attributes & FileAttributes.Directory) == 0) return;
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception ex) { t.Failed++; t.Detail = ex.Message; return; }
            if (HasReparseAncestor(dir)) { t.Skipped++; return; }
            if (IsProtected(dir) && !IsWindowsMemoryDumpRule(dir, r)) { t.Skipped++; return; }
            var s = AppSettings.Current;
            if (s.IsExcluded(dir)) { t.Skipped++; return; }
            DateTime? cutoff = null;
            if (r.MinAgeDays > 0) cutoff = DateTime.UtcNow.AddDays(-r.MinAgeDays);
            else if (r.UseTempAge && s.OnlyOldTempFiles) cutoff = DateTime.UtcNow.AddHours(-24);
            Walk(new DirectoryInfo(dir), r, cutoff, delete, t, ct);
        }

        static void Walk(DirectoryInfo dir, FileRule r, DateTime? cutoff, bool delete, CleanTotals t, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            FileInfo[] files;
            try
            {
                // A rule root can itself be a junction, not just one of its children.
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || AppSettings.Current.IsExcluded(dir.FullName)) { t.Skipped++; return; }
                files = dir.GetFiles();
            }
            catch (Exception ex) { t.Failed++; t.Detail = ex.Message; return; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!Glob.MatchesAny(f.Name, r.Patterns)) continue;
                    // Do not follow file links or alter the attributes of their targets.
                    var attributes = f.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { t.Skipped++; continue; }
                    if (cutoff.HasValue && Newest(f) > cutoff.Value) { t.Skipped++; continue; }
                    if (AppSettings.Current.IsExcluded(f.FullName)) { t.Skipped++; continue; }
                    long len = f.Length;
                    if (!delete) { t.Bytes += len; t.Files++; continue; }
                    if ((attributes & FileAttributes.ReadOnly) != 0) f.Attributes = attributes & ~FileAttributes.ReadOnly;
                    if (NativeMethods.TryDeleteFile(f.FullName, out int error)) { t.Bytes += len; t.Files++; }
                    else if (error == 2 || error == 3)
                    {
                        t.Skipped++; t.Detail = "A file was already removed by another process.";
                    }
                    else if (error == 32 || error == 33)
                    {
                        t.Skipped++; t.Detail = "A file was in use and could not be deleted.";
                    }
                    else
                    {
                        t.Failed++; t.Detail = new Win32Exception(error).Message + " (" + error + ")";
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { t.Failed++; t.Detail = ex.Message; }
            }
            if (!r.Recursive) return;
            DirectoryInfo[] subs;
            try { subs = dir.GetDirectories(); } catch (Exception ex) { t.Failed++; t.Detail = ex.Message; return; }
            foreach (var sub in subs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Never follow junctions / symlinks: they can point outside the folder being cleaned.
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 || AppSettings.Current.IsExcluded(sub.FullName)) { t.Skipped++; continue; }
                }
                catch (Exception ex) { t.Failed++; t.Detail = ex.Message; continue; }
                Walk(sub, r, cutoff, delete, t, ct);
                if (delete && r.RemoveEmptyDirs)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) == 0 &&
                            (!cutoff.HasValue || sub.CreationTimeUtc <= cutoff.Value) && !sub.EnumerateFileSystemInfos().Any())
                            sub.Delete(false);
                    }
                    catch (Exception ex) { t.Failed++; t.Detail = ex.Message; }
                }
            }
        }

        static bool HasReparseAncestor(string path)
        {
            try
            {
                for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                return false;
            }
            catch { return true; } // Unreadable paths cannot be established as safe cleanup roots.
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

        // The built-in memory-dump item targets one exact file in the Windows folder.
        // Never allow a wildcard, recursion, directory removal, or another protected root here.
        static bool IsWindowsMemoryDumpRule(string dir, FileRule rule)
        {
            if (rule.Recursive || rule.RemoveEmptyDirs || rule.Patterns == null || rule.Patterns.Length != 1 ||
                !string.Equals(rule.Patterns[0], "MEMORY.DMP", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(Win) || !Path.IsPathRooted(Win)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'),
                    Path.GetFullPath(Win).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static DateTime Newest(FileSystemInfo f)
        {
            var c = f.CreationTimeUtc; var w = f.LastWriteTimeUtc;
            return c > w ? c : w;
        }

        static string DeliveryOptimizationPath => Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");

        static bool HasExcludedCachePath(string cachePath)
        {
            var settings = AppSettings.Current;
            if (settings.IsExcluded(cachePath)) return true;
            // The Windows cache-purge command cannot honor exclusions within the cache.
            // Skip it rather than delete an explicitly excluded child directory.
            var prefix = Path.GetFullPath(cachePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var excluded in settings.ExcludedPaths ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(excluded)) continue;
                try
                {
                    var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(excluded));
                    if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
            return false;
        }

        static void CleanDeliveryOptimization(bool delete, CleanTotals t, CancellationToken ct)
        {
            var dir = DeliveryOptimizationPath;
            var before = new CleanTotals();
            ProcessRule(FileRule.Contents(dir), false, before, ct);
            if (!delete) { t.Bytes = before.Bytes; t.Files = before.Files; return; }
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            t.CountsKnown = false;
            int code = ProcessRunner.RunAsync(ps, "-NoProfile -NonInteractive -Command \"$ErrorActionPreference = 'Stop'; Delete-DeliveryOptimizationCache -Force\"", null, ct)
                .GetAwaiter().GetResult();
            if (code != 0) throw new InvalidOperationException("Windows could not clear the Delivery Optimization cache.");
            var after = new CleanTotals();
            ProcessRule(FileRule.Contents(dir), false, after, ct);
            t.Bytes = Math.Max(0, before.Bytes - after.Bytes);
            t.Files = Math.Max(0, before.Files - after.Files);
            t.Failed += before.Failed + after.Failed; t.Skipped += before.Skipped + after.Skipped;
            t.CountsKnown = before.Failed == 0 && after.Failed == 0;
        }

        static void CleanWindowsUpdateCache(bool delete, CleanTotals t, CancellationToken ct)
        {
            var rule = FileRule.Contents(Path.Combine(Win, @"SoftwareDistribution\Download"));
            if (!delete) { ProcessRule(rule, false, t, ct); return; }
            var controllers = new List<ServiceController>();
            var restart = new List<ServiceController>();
            try
            {
                foreach (var name in new[] { "wuauserv", "bits" })
                {
                    ct.ThrowIfCancellationRequested();
                    var sc = new ServiceController(name);
                    controllers.Add(sc);
                    var status = sc.Status;
                    if (status == ServiceControllerStatus.Stopped) continue;
                    if (status == ServiceControllerStatus.StopPending)
                    {
                        // Another actor stopped it; do not take ownership of restarting it.
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        continue;
                    }
                    sc.Stop();
                    // Remember ownership immediately: WaitForStatus may time out after Stop succeeded.
                    restart.Add(sc);
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                ProcessRule(rule, true, t, ct);
            }
            finally
            {
                foreach (var sc in restart.AsEnumerable().Reverse())
                {
                    try
                    {
                        sc.Refresh();
                        if (sc.Status == ServiceControllerStatus.StopPending)
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        sc.Refresh();
                        if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                    }
                    catch (Exception ex)
                    {
                        t.Failed++; t.Detail = $"Could not restore service {sc.ServiceName}: {ex.Message}";
                        Log.Write($"Start {sc.ServiceName}: {ex.Message}");
                    }
                }
                foreach (var sc in controllers) sc.Dispose();
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
        public static void Close(IEnumerable<string> processNames, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var names = processNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var n in names)
                foreach (var p in Process.GetProcessesByName(n))
                    try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
            var until = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < until && names.Any(n => Process.GetProcessesByName(n).Length > 0))
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(300);
            }
            ct.ThrowIfCancellationRequested();
            foreach (var n in names)
                foreach (var p in Process.GetProcessesByName(n))
                    try { p.Kill(); } catch { }
            Thread.Sleep(800);
        }
    }
}
