using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSweep.Core
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class Fmt
    {
        static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        public static string Bytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : $"{v:0.#} {Units[u]}";
        }

        public static string Count(long n, string singular, string plural = null) =>
            $"{n:N0} {(n == 1 ? singular : plural ?? singular + "s")}";
    }

    public static class AppPaths
    {
        public static string DataDir { get; } = Ensure(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSweep"));
        public static string BackupDir => Ensure(Path.Combine(DataDir, "Backups"));
        public static string LogDir => Ensure(Path.Combine(DataDir, "Logs"));
        public static string SettingsFile => Path.Combine(DataDir, "settings.xml");
        public static string ExePath { get; } = Process.GetCurrentProcess().MainModule.FileName;

        static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }
    }

    public static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                    File.AppendAllText(Path.Combine(AppPaths.LogDir, $"opensweep-{DateTime.Now:yyyyMMdd}.log"),
                        $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch { /* logging must never break the app */ }
        }
    }

    public static class Elevation
    {
        public static bool IsAdmin { get; } =
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public static bool RestartAsAdmin(string args = "")
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppPaths.ExePath, args) { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (Win32Exception) { return false; } // user cancelled UAC
        }
    }

    public static class ShellOps
    {
        public static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write($"Open '{target}' failed: {ex.Message}"); }
        }

        public static void ShowInExplorer(string path)
        {
            if (File.Exists(path)) Open2("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Open2("explorer.exe", $"\"{path}\"");
        }

        static void Open2(string exe, string args)
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); } catch { }
        }

        public static bool SendToRecycleBin(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            catch (Exception ex) { Log.Write($"Recycle '{path}' failed: {ex.Message}"); return false; }
        }

        /// <summary>
        /// Moves files or folders to the Recycle Bin in one shell operation. Windows asks first if something can't be
        /// recycled (USB sticks, files too big for the bin) and would be deleted for good. Call on the UI thread.
        /// Returns the paths that no longer exist afterwards.
        /// </summary>
        public static List<string> Recycle(IList<string> paths, IntPtr owner)
        {
            var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (existing.Count > 0)
            {
                var op = new NativeMethods.SHFILEOPSTRUCT
                {
                    hwnd = owner,
                    wFunc = NativeMethods.FO_DELETE,
                    pFrom = string.Join("\0", existing) + "\0",
                    fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_WANTNUKEWARNING,
                };
                int rc = NativeMethods.SHFileOperation(ref op);
                if (rc != 0) Log.Write($"Recycle of {existing.Count} item(s) returned 0x{rc:X}");
            }
            return paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        }
    }
}
