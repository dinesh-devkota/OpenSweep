using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenSweep.Core
{
    internal static class NativeMethods
    {
        // FileInfo.Delete succeeds silently for an already-missing file. Cleanup history
        // needs the native result so another process's removal is never counted as ours.
        public static bool TryDeleteFile(string path, out int error)
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(@"\\?\", StringComparison.Ordinal))
                full = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
            bool deleted = DeleteFileW(full);
            error = deleted ? 0 : Marshal.GetLastWin32Error();
            return deleted;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DeleteFileW(string path);

        public const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        public const uint FO_DELETE = 3;
        public const ushort FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

        // Public representation; the native call below selects the correct architecture-specific layout.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        // shellapi.h uses byte packing for this legacy structure on 32-bit Windows.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        struct SHFILEOPSTRUCT32
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        static extern int SHFileOperation64(ref SHFILEOPSTRUCT operation);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 operation);

        public static int SHFileOperation(ref SHFILEOPSTRUCT operation)
        {
            if (IntPtr.Size == 8) return SHFileOperation64(ref operation);
            var native = new SHFILEOPSTRUCT32
            {
                hwnd = operation.hwnd, wFunc = operation.wFunc,
                pFrom = operation.pFrom, pTo = operation.pTo, fFlags = operation.fFlags,
                fAnyOperationsAborted = operation.fAnyOperationsAborted,
                hNameMappings = operation.hNameMappings, lpszProgressTitle = operation.lpszProgressTitle,
            };
            int result = SHFileOperation32(ref native);
            operation.fAnyOperationsAborted = native.fAnyOperationsAborted;
            operation.hNameMappings = native.hNameMappings;
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SHQUERYRBINFO64 { public int cbSize; public long i64Size; public long i64NumItems; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct SHQUERYRBINFO32 { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
        static extern int SHQueryRecycleBin64(string pszRootPath, ref SHQUERYRBINFO64 info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
        static extern int SHQueryRecycleBin32(string pszRootPath, ref SHQUERYRBINFO32 info);

        public static (long Size, long Items) QueryRecycleBin()
            => TryQueryRecycleBin(out var size, out var items) ? (size, items) : (0, 0);

        public static bool TryQueryRecycleBin(out long size, out long items)
        {
            size = items = 0;
            if (IntPtr.Size == 8)
            {
                var info = new SHQUERYRBINFO64 { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO64)) };
                if (SHQueryRecycleBin64(null, ref info) != 0 || info.i64Size < 0 || info.i64NumItems < 0) return false;
                size = info.i64Size; items = info.i64NumItems;
                return true;
            }
            var info32 = new SHQUERYRBINFO32 { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO32)) };
            if (SHQueryRecycleBin32(null, ref info32) != 0 || info32.i64Size < 0 || info32.i64NumItems < 0) return false;
            size = info32.i64Size; items = info32.i64NumItems;
            return true;
        }

        [DllImport("dnsapi.dll")]
        public static extern bool DnsFlushResolverCache();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // Fast directory enumeration for the disk analyzer.
        public const int FIND_FIRST_EX_LARGE_FETCH = 2;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATA lpFindFileData,
            int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll")]
        public static extern bool FindClose(IntPtr hFindFile);

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    }
}
