using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
        public string ContentHash { get; set; }
        public List<DupFile> Files { get; } = new List<DupFile>();
        public long Wasted => Size * (Files.Count - 1);
        public string Label => $"Group {Number:000}  ·  {Files.Count} identical files  ·  {Fmt.Bytes(Size)} each";
    }

    /// <summary>Owns the read handles that keep verified copies stable during recycling.</summary>
    public sealed class DupDeletionPlan : IDisposable
    {
        readonly List<FileStream> _handles = new List<FileStream>();
        readonly Dictionary<DupFile, Func<bool>> _pathChecks = new Dictionary<DupFile, Func<bool>>();
        public List<DupFile> Files { get; } = new List<DupFile>();
        public Dictionary<DupFile, string> Skipped { get; } = new Dictionary<DupFile, string>();
        internal void Hold(FileStream stream) => _handles.Add(stream);
        internal void Add(DupFile file, FileStream stream, DupFile keeper, FileStream keptStream)
        {
            Hold(stream);
            Files.Add(file);
            _pathChecks[file] = () => DuplicateFinder.PathStillMatches(keeper, keptStream) && DuplicateFinder.PathStillMatches(file, stream);
        }
        /// <summary>Catch path replacements immediately before invoking the path-based Windows shell API.</summary>
        public void RecheckPaths()
        {
            foreach (var file in Files.ToList())
                if (!_pathChecks[file]())
                {
                    Files.Remove(file);
                    Skipped[file] = "Skipped – a file path changed during verification. Search again";
                }
        }
        public void Dispose()
        {
            foreach (var stream in _handles) stream.Dispose();
            _handles.Clear();
            _pathChecks.Clear();
        }
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
            ct.ThrowIfCancellationRequested();
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
                if (IsSkippedDirectory(dir.FullName) || name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return;
                FileInfo[] files;
                try
                {
                    var attributes = dir.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return;
                    if (skipHiddenSystem && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return;
                    files = dir.GetFiles();
                }
                catch { return; }
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
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
                foreach (var sub in subs) Enumerate(sub);
            }

            foreach (var r in roots.Where(Directory.Exists))
            {
                ct.ThrowIfCancellationRequested();
                if (!HasReparseAncestor(r)) Enumerate(new DirectoryInfo(r));
            }

            var candidates = bySize.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Key).ToList();
            long total = candidates.Sum(kv => kv.Value.Count), done = 0;
            var groups = new List<DupGroup>();
            using (var sha = SHA256.Create())
            {
                foreach (var kv in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var quick = kv.Value.GroupBy(f => Hash(sha, f, QuickBytes, ct)).Where(g => g.Key != null && g.Count() > 1);
                    foreach (var q in quick)
                    {
                        var full = kv.Key <= QuickBytes
                            ? new[] { q }
                            : q.GroupBy(f => Hash(sha, f, long.MaxValue, ct)).Where(g => g.Key != null && g.Count() > 1);
                        foreach (var same in full)
                        {
                            var g = new DupGroup { Size = kv.Key, ContentHash = same.Key };
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

        static string Hash(HashAlgorithm alg, FileInfo file, long limit, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (HasReparseAncestor(file.FullName)) return null;
                // FileInfo carries the enumeration snapshot. Reject files changed since that snapshot.
                using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                {
                    if (fs.Length != file.Length || File.GetLastWriteTimeUtc(file.FullName) != file.LastWriteTimeUtc) return null;
                    return HashStream(alg, fs, limit, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        static string HashStream(HashAlgorithm alg, Stream stream, long limit, CancellationToken ct)
        {
            alg.Initialize();
            var buffer = new byte[1 << 16];
            long remaining = limit;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) break;
                alg.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;
            }
            alg.TransformFinalBlock(new byte[0], 0, 0);
            return Convert.ToBase64String(alg.Hash);
        }

        static bool IsSkippedDirectory(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return AppSettings.Current.IsExcluded(full) || SkipFolders.Any(root =>
                full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        static bool HasReparseAncestor(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                while (!string.IsNullOrEmpty(full))
                {
                    if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return true;
                    full = Path.GetDirectoryName(full);
                }
                return false;
            }
            catch { return true; }
        }

        /// <summary>Rechecks current contents after confirmation and holds the kept copy against writes/deletion.</summary>
        public static DupDeletionPlan PrepareDeletion(IEnumerable<DupGroup> groups, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var plan = new DupDeletionPlan();
            try
            {
                using (var sha = SHA256.Create())
                {
                    foreach (var group in groups)
                    {
                        ct.ThrowIfCancellationRequested();
                        var marked = group.Files.Where(f => f.IsChecked).ToList();
                        if (marked.Count == 0) continue;
                        FileStream keeper = null;
                        DupFile keptFile = null;
                        foreach (var file in group.Files.Where(f => !f.IsChecked))
                        {
                            keeper = OpenVerified(file, group.ContentHash, false, sha, ct);
                            if (keeper != null) { keptFile = file; break; }
                        }
                        if (keeper == null)
                        {
                            foreach (var file in marked) plan.Skipped[file] = "Skipped – the kept copy is missing, changed or in use";
                            continue;
                        }
                        plan.Hold(keeper);
                        foreach (var file in marked)
                        {
                            var stream = OpenVerified(file, group.ContentHash, true, sha, ct);
                            if (stream == null) plan.Skipped[file] = "Skipped – this file changed or is no longer safe to remove";
                            else plan.Add(file, stream, keptFile, keeper);
                        }
                    }
                }
                return plan;
            }
            catch { plan.Dispose(); throw; }
        }

        static FileStream OpenVerified(DupFile file, string hash, bool allowDelete, HashAlgorithm sha, CancellationToken ct)
        {
            FileStream stream = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(hash) || AppSettings.Current.IsExcluded(file.FullPath) ||
                    IsSkippedDirectory(file.Folder) || HasReparseAncestor(file.FullPath)) return null;
                var attributes = File.GetAttributes(file.FullPath);
                if (((int)attributes & RecallAttributes) != 0 || (attributes & FileAttributes.Offline) != 0) return null;
                stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                    allowDelete ? FileShare.Read | FileShare.Delete : FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                if (stream.Length == file.Size && HashStream(sha, stream, long.MaxValue, ct) == hash) return stream;
            }
            catch (OperationCanceledException) { stream?.Dispose(); throw; }
            catch { }
            stream?.Dispose();
            return null;
        }

        // SHFileOperation accepts paths, so no application-side check can be atomic with a later
        // filesystem rename. Compare file identities immediately before that call, while read
        // handles prevent writes and a keeper handle also prevents its deletion on Windows.
        internal static bool PathStillMatches(DupFile file, FileStream verified)
        {
            try
            {
                if (HasReparseAncestor(file.FullPath) || AppSettings.Current.IsExcluded(file.FullPath)) return false;
                using (var current = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    {
                        return GetFileInformationByHandle(verified.SafeFileHandle, out var expected) &&
                            GetFileInformationByHandle(current.SafeFileHandle, out var actual) &&
                            expected.VolumeSerialNumber == actual.VolumeSerialNumber &&
                            expected.FileIndexHigh == actual.FileIndexHigh && expected.FileIndexLow == actual.FileIndexLow;
                    }
                    // The application is Windows-only; this branch supports portable managed regression tests.
                    return current.Length == file.Size && File.GetLastWriteTime(file.FullPath) == file.Modified;
                }
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FileIdentity
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity information);

        /// <summary>Checks every file except the newest (or oldest) one in each group.</summary>
        public static void SelectAllButOne(IEnumerable<DupGroup> groups, bool keepNewest)
        {
            foreach (var g in groups)
            {
                if (g.Files.Count == 0) continue;
                var keep = keepNewest ? g.Files.OrderByDescending(f => f.Modified).First() : g.Files.OrderBy(f => f.Modified).First();
                foreach (var f in g.Files) f.IsChecked = f != keep;
            }
        }
    }
}
