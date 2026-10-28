using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OpenSweep.Core
{
    public sealed class DirNode
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public DirNode Parent { get; set; }
        public List<DirNode> Children { get; } = new List<DirNode>();
        public long Size { get; set; }
        public long Files { get; set; }
        /// <summary>Size of the files directly inside this folder (not in sub-folders).</summary>
        public long OwnFilesSize { get; set; }
        public long OwnFiles { get; set; }
    }

    public sealed class BigFile
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public string Folder => Path.GetDirectoryName(FullPath);
        public long Size { get; set; }
        public string SizeText => Fmt.Bytes(Size);
        public DateTime Modified { get; set; }
        public string Category { get; set; }
    }

    public sealed class CategoryStat
    {
        public string Name { get; set; }
        public long Bytes { get; set; }
        public long Files { get; set; }
        public double Percent { get; set; }
        public string PercentText => Percent.ToString("0.0") + " %";
        public string SizeText => Fmt.Bytes(Bytes);
        public string FilesText => Fmt.Count(Files, "file");
    }

    public sealed class DiskScanResult
    {
        public DirNode Root;
        public List<BigFile> LargestFiles;
        public List<CategoryStat> Categories;
        public long TotalBytes, TotalFiles, Folders, Inaccessible;
    }

    public static class DiskAnalyzer
    {
        const int TopFiles = 200;
        static readonly Dictionary<string, string> Categories = BuildCategories();

        static Dictionary<string, string> BuildCategories()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string cat, string exts) { foreach (var e in exts.Split(' ')) d["." + e] = cat; }
            Add("Pictures", "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 nef arw svg ico psd");
            Add("Videos", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp");
            Add("Music", "mp3 wav flac aac ogg m4a wma opus aiff");
            Add("Documents", "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp csv md one msg pst ost epub");
            Add("Archives & disk images", "zip rar 7z tar gz tgz bz2 xz cab iso img vhd vhdx wim esd");
            Add("Programs", "exe msi dll sys msix msixbundle appx appxbundle ocx cpl drv mui");
            Add("Code & data", "cs js ts java py cpp c h json xml yml yaml html css sql db sqlite mdf ldf log jar class");
            return d;
        }

        public static string CategoryOf(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot > 0 && Categories.TryGetValue(name.Substring(dot), out var c) ? c : "Other";
        }

        public static DiskScanResult Scan(string rootPath, IProgress<string> progress, CancellationToken ct)
        {
            var res = new DiskScanResult();
            var top = new SortedSet<(long Size, long Seq, string Path, DateTime Modified)>();
            var cats = new Dictionary<string, CategoryStat>();
            long seq = 0;
            var lastReport = DateTime.UtcNow;
            var full = Path.GetFullPath(rootPath);
            var root = new DirNode { Name = full, FullPath = full };

            void Walk(DirNode node)
            {
                ct.ThrowIfCancellationRequested();
                var pattern = Path.Combine(node.FullPath, "*");
                if (!pattern.StartsWith(@"\\")) pattern = @"\\?\" + pattern;
                var h = NativeMethods.FindFirstFileExW(pattern, 1, out var fd, 0, IntPtr.Zero, NativeMethods.FIND_FIRST_EX_LARGE_FETCH);
                if (h == NativeMethods.INVALID_HANDLE_VALUE) { res.Inaccessible++; return; }
                try
                {
                    do
                    {
                        var name = fd.cFileName;
                        if (name == "." || name == "..") continue;
                        if ((fd.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0)
                        {
                            // Skip junctions/symlinks so nothing is counted twice.
                            if ((fd.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
                            var child = new DirNode { Name = name, FullPath = Path.Combine(node.FullPath, name), Parent = node };
                            node.Children.Add(child);
                            res.Folders++;
                            Walk(child);
                            node.Size += child.Size;
                            node.Files += child.Files;
                            continue;
                        }
                        long size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                        node.Size += size; node.Files++;
                        node.OwnFilesSize += size; node.OwnFiles++;
                        res.TotalBytes += size; res.TotalFiles++;
                        var cat = CategoryOf(name);
                        if (!cats.TryGetValue(cat, out var st)) cats[cat] = st = new CategoryStat { Name = cat };
                        st.Bytes += size; st.Files++;
                        if (top.Count < TopFiles || size > top.Min.Size)
                        {
                            top.Add((size, seq++, Path.Combine(node.FullPath, name), ToDate(fd.ftLastWriteTime)));
                            if (top.Count > TopFiles) top.Remove(top.Min);
                        }
                    } while (NativeMethods.FindNextFileW(h, out fd));
                }
                finally { NativeMethods.FindClose(h); }

                if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report($"{Fmt.Count(res.TotalFiles, "file")} · {Fmt.Bytes(res.TotalBytes)} · {node.FullPath}");
                }
            }

            Walk(root);
            res.Root = root;
            res.LargestFiles = top.Reverse().Select(t => new BigFile
            {
                Name = Path.GetFileName(t.Path), FullPath = t.Path, Size = t.Size, Modified = t.Modified, Category = CategoryOf(t.Path),
            }).ToList();
            foreach (var c in cats.Values) c.Percent = res.TotalBytes > 0 ? c.Bytes * 100.0 / res.TotalBytes : 0;
            res.Categories = cats.Values.OrderByDescending(c => c.Bytes).ToList();
            return res;
        }

        static DateTime ToDate(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        {
            try { return DateTime.FromFileTime(((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime); }
            catch { return DateTime.MinValue; }
        }

        public static IEnumerable<DriveInfo> FixedDrives() =>
            DriveInfo.GetDrives().Where(d => { try { return d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable); } catch { return false; } });
    }
}
