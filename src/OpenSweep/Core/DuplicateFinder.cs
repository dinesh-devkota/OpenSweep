using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace OpenSweep.Core
{
    public sealed class DupFile : ObservableObject
    {
        public string FullPath { get; set; }
        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath);
        public long Size { get; set; }
        public string SizeText => Fmt.Bytes(Size);
        public DateTime Modified { get; set; }
        public DupGroup Group { get; set; }
        public string GroupLabel => Group.Label;

        bool _checked;
        public bool IsChecked { get => _checked; set => Set(ref _checked, value); }

        string _status = "";
        public string Status { get => _status; set => Set(ref _status, value); }
    }

    public sealed class DupGroup
    {
        public int Number { get; set; }
        public long Size { get; set; }
        public List<DupFile> Files { get; } = new List<DupFile>();
        public long Wasted => Size * (Files.Count - 1);
        public string Label => $"Group {Number:000}  ·  {Files.Count} identical files  ·  {Fmt.Bytes(Size)} each";
    }

    /// <summary>Finds identical files: same size, then same SHA-256 of the first 64 KB, then same SHA-256 of the whole file.</summary>
    public static class DuplicateFinder
    {
        const int QuickBytes = 64 * 1024;
        // Cloud placeholders (OneDrive "online-only") must not be read, or they would be downloaded.
        const int RecallAttributes = 0x00040000 | 0x00400000;

        static readonly HashSet<string> SkipFolders = new HashSet<string>(new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        }.Where(p => !string.IsNullOrEmpty(p)).Select(p => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);

        public static List<DupGroup> Find(IList<string> roots, long minSize, bool skipHiddenSystem, IProgress<string> progress, CancellationToken ct)
        {
            var bySize = new Dictionary<long, List<FileInfo>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long scanned = 0;
            var last = DateTime.UtcNow;

            void Report(string text)
            {
                if ((DateTime.UtcNow - last).TotalMilliseconds < 200) return;
                last = DateTime.UtcNow;
                progress?.Report(text);
            }

            void Enumerate(DirectoryInfo dir)
            {
                ct.ThrowIfCancellationRequested();
                var name = dir.Name;
                if (SkipFolders.Contains(dir.FullName.TrimEnd('\\')) || name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return;
                FileInfo[] files;
                try { files = dir.GetFiles(); } catch { return; }
                foreach (var f in files)
                {
                    try
                    {
                        var a = f.Attributes;
                        if (((int)a & RecallAttributes) != 0 || (a & (FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0) continue;
                        if (skipHiddenSystem && (a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (f.Length < minSize || AppSettings.Current.IsExcluded(f.FullName) || !seen.Add(f.FullName)) continue;
                        if (!bySize.TryGetValue(f.Length, out var list)) bySize[f.Length] = list = new List<FileInfo>();
                        list.Add(f);
                        scanned++;
                    }
                    catch { }
                }
                Report($"Scanning… {scanned:N0} files · {dir.FullName}");
                DirectoryInfo[] subs;
                try { subs = dir.GetDirectories(); } catch { return; }
                foreach (var s in subs)
                {
                    if ((s.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (skipHiddenSystem && (s.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    Enumerate(s);
                }
            }

            foreach (var r in roots.Where(Directory.Exists)) Enumerate(new DirectoryInfo(r));

            var candidates = bySize.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Key).ToList();
            long total = candidates.Sum(kv => kv.Value.Count), done = 0;
            var groups = new List<DupGroup>();
            using (var sha = new SHA256CryptoServiceProvider())
            {
                foreach (var kv in candidates)
                {
                    var quick = kv.Value.GroupBy(f => Hash(sha, f.FullName, QuickBytes, ct)).Where(g => g.Key != null && g.Count() > 1);
                    foreach (var q in quick)
                    {
                        var full = kv.Key <= QuickBytes
                            ? new[] { q.AsEnumerable() }
                            : q.GroupBy(f => Hash(sha, f.FullName, long.MaxValue, ct)).Where(g => g.Key != null && g.Count() > 1).Select(g => g.AsEnumerable());
                        foreach (var same in full)
                        {
                            var g = new DupGroup { Size = kv.Key };
                            foreach (var f in same.OrderByDescending(x => x.LastWriteTime))
                                g.Files.Add(new DupFile { FullPath = f.FullName, Size = f.Length, Modified = f.LastWriteTime, Group = g });
                            if (g.Files.Count > 1) groups.Add(g);
                        }
                    }
                    done += kv.Value.Count;
                    Report($"Comparing files… {done * 100 / Math.Max(1, total)}%");
                }
            }
            int n = 1;
            foreach (var g in groups.OrderByDescending(g => g.Wasted)) g.Number = n++;
            return groups.OrderBy(g => g.Number).ToList();
        }

        static string Hash(HashAlgorithm alg, string path, long limit, CancellationToken ct)
        {
            try
            {
                alg.Initialize();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                {
                    var buf = new byte[1 << 16];
                    long remaining = limit;
                    int read;
                    while (remaining > 0 && (read = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining))) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        alg.TransformBlock(buf, 0, read, null, 0);
                        remaining -= read;
                    }
                    alg.TransformFinalBlock(new byte[0], 0, 0);
                    return Convert.ToBase64String(alg.Hash);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>Checks every file except the newest (or oldest) one in each group.</summary>
        public static void SelectAllButOne(IEnumerable<DupGroup> groups, bool keepNewest)
        {
            foreach (var g in groups)
            {
                var keep = keepNewest ? g.Files.OrderByDescending(f => f.Modified).First() : g.Files.OrderBy(f => f.Modified).First();
                foreach (var f in g.Files) f.IsChecked = f != keep;
            }
        }
    }
}
