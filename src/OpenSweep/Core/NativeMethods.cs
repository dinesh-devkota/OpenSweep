using System;
using System.Runtime.InteropServices;

namespace OpenSweep.Core
{
    internal static class NativeMethods
    {
        public const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        public const uint FO_DELETE = 3;
        public const ushort FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

        // Default packing matches the x64 header layout (the only output field differs on 32-bit Windows; it isn't read).
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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [StructLayout(LayoutKind.Sequential)]
        struct SHQUERYRBINFO64 { public int cbSize; public long i64Size; public long i64NumItems; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct SHQUERYRBINFO32 { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
        static extern int SHQueryRecycleBin64(string pszRootPath, ref SHQUERYRBINFO64 info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
        static extern int SHQueryRecycleBin32(string pszRootPath, ref SHQUERYRBINFO32 info);

        public static (long Size, long Items) QueryRecycleBin()
        {
            if (IntPtr.Size == 8)
            {
                var info = new SHQUERYRBINFO64 { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO64)) };
                return SHQueryRecycleBin64(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
            }
            var info32 = new SHQUERYRBINFO32 { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO32)) };
            return SHQueryRecycleBin32(null, ref info32) == 0 ? (info32.i64Size, info32.i64NumItems) : (0, 0);
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
