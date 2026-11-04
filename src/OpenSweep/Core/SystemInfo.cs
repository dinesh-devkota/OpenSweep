using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OpenSweep.Core
{
    public static class SystemInfo
    {
        static readonly Lazy<Task> InitialLoad = new Lazy<Task>(() => Task.Run(() => Load()));

        public static Task EnsureLoadedAsync() => InitialLoad.Value;

        public static string OsName { get; private set; } = "Windows";
        public static string Cpu { get; private set; } = "";
        public static string Gpu { get; private set; } = "";
        public static long RamBytes { get; private set; }

        public static void Load()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string caption = null;
                    try
                    {
                        using (var s = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                            caption = s.Get().Cast<ManagementObject>().Select(o => o["Caption"] as string).FirstOrDefault();
                    }
                    catch { }
                    caption = (caption ?? k?.GetValue("ProductName") as string ?? "Windows").Replace("Microsoft ", "").Trim();
                    var display = k?.GetValue("DisplayVersion") as string;
                    var build = k?.GetValue("CurrentBuild") as string;
                    var ubr = k?.GetValue("UBR");
                    OsName = $"{caption} {display}".Trim() + (build != null ? $" (build {build}{(ubr != null ? "." + ubr : "")})" : "");
                }
            }
            catch { }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    Cpu = System.Text.RegularExpressions.Regex.Replace((k?.GetValue("ProcessorNameString") as string ?? "").Trim(), @"\s+", " ");
            }
            catch { }
            try
            {
                var m = new NativeMethods.MEMORYSTATUSEX();
                if (NativeMethods.GlobalMemoryStatusEx(m)) RamBytes = (long)m.ullTotalPhys;
            }
            catch { }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    Gpu = s.Get().Cast<ManagementObject>().Select(o => o["Name"] as string)
                        .FirstOrDefault(n => n != null && !n.Contains("Basic Display") && !n.Contains("Remote")) ?? "";
            }
            catch { }
        }

        public static string Summary
        {
            get
            {
                var parts = new List<string> { OsName };
                if (Cpu.Length > 0) parts.Add(Cpu);
                if (RamBytes > 0) parts.Add($"{Math.Round(RamBytes / 1073741824.0)} GB RAM");
                if (Gpu.Length > 0) parts.Add(Gpu);
                return string.Join("  ·  ", parts);
            }
        }

        public static DriveInfo SystemDrive => new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
    }
}
