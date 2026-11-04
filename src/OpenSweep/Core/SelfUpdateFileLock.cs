using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenSweep.Core
{
    /// <summary>Keep a local file's path and bytes stable for path-based Windows trust/launch APIs.</summary>
    internal sealed class SelfUpdateFileLock : IDisposable
    {
        readonly SelfUpdateDirectoryLock _directories;
        public FileStream Stream { get; }
        public SelfUpdateFileLock(string path)
        {
            string full = Path.GetFullPath(path);
            _directories = new SelfUpdateDirectoryLock(Path.GetDirectoryName(full));
            try
            {
                Stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                SelfUpdateDirectoryLock.AssertRegularHandle(Stream.SafeFileHandle, full, false);
            }
            catch { Stream?.Dispose(); _directories.Dispose(); throw; }
        }
        public void Dispose() { Stream.Dispose(); _directories.Dispose(); }
    }

    internal sealed class SelfUpdateDirectoryLock : IDisposable
    {
        readonly List<SafeFileHandle> _handles = new List<SafeFileHandle>();
        public SelfUpdateDirectoryLock(string directory, bool protectAncestors = true)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new InvalidOperationException("Windows file verification is required.");
            string full = Path.GetFullPath(directory), root = Path.GetPathRoot(full);
            if (root.Length != 3 || root[1] != ':' || root[2] != '\\')
                throw new InvalidOperationException("Verified updates require a local Windows drive.");
            var chain = new List<string>();
            for (var current = new DirectoryInfo(full); current != null; current = current.Parent) chain.Add(current.FullName);
            chain.Reverse();
            try
            {
                foreach (var path in chain)
                {
                    // During network transfer only the private leaf denies writes; ancestors deny deletion.
                    // Verification and launch take stronger locks on the entire chain against reparse changes.
                    // FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES: metadata-only handles do not reliably impose share restrictions.
                    uint sharing = protectAncestors || string.Equals(path, full, StringComparison.OrdinalIgnoreCase) ? 1u : 3u;
                    var handle = CreateFile(path, 0x81, sharing, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                    if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Could not secure the update file's directory path."); }
                    _handles.Add(handle);
                    AssertRegularHandle(handle, path, true);
                }
            }
            catch { Dispose(); throw; }
        }

        internal static void AssertRegularHandle(SafeFileHandle handle, string expectedPath, bool directory)
        {
            if (!GetFileInformationByHandle(handle, out var information)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((information.Attributes & 0x400) != 0 || ((information.Attributes & 0x10) != 0) != directory)
                throw new InvalidDataException("Verified updates cannot use symbolic links, junctions or other redirected paths.");
            var buffer = new StringBuilder(512);
            uint required = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (required == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (required >= buffer.Capacity)
            {
                buffer = new StringBuilder(checked((int)required + 1));
                required = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (required == 0 || required >= buffer.Capacity) throw new InvalidDataException("The verified update path could not be resolved.");
            }
            string actual = buffer.ToString();
            if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual.Substring(4);
            if (!string.Equals(actual.TrimEnd('\\'), Path.GetFullPath(expectedPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update file path changed while it was being verified.");
        }

        public void Dispose()
        {
            for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
            _handles.Clear();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ByHandleFileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr attributes, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    }
}
