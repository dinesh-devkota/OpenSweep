using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace OpenSweep.Core
{
    public class AppSettings
    {
        /// <summary>Ids of checked cleaner items. Null means "use the defaults".</summary>
        public List<string> CheckedCleanerItems { get; set; }
        public bool OnlyOldTempFiles { get; set; } = true;
        public bool RestorePointBeforeRegistryFix { get; set; } = true;
        public bool CloseAppsAutomatically { get; set; }
        public bool SilentAppUpdates { get; set; } = true;
        public bool IncludeUnknownVersions { get; set; }
        public bool WindowsUpdateOnline { get; set; }
        public List<string> ExcludedPaths { get; set; } = new List<string>();
        public string Schedule { get; set; } = "Off";
        public string ScheduleTime { get; set; } = "12:00";

        static AppSettings _current;
        public static AppSettings Current => _current ?? (_current = Load());

        static AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    using (var fs = File.OpenRead(AppPaths.SettingsFile))
                        return (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(fs) ?? new AppSettings();
            }
            catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var tmp = AppPaths.SettingsFile + ".tmp";
                using (var fs = File.Create(tmp))
                    new XmlSerializer(typeof(AppSettings)).Serialize(fs, this);
                if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
                File.Move(tmp, AppPaths.SettingsFile);
            }
            catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
        }

        public bool IsExcluded(string path) =>
            ExcludedPaths.Any(x => !string.IsNullOrWhiteSpace(x) &&
                path.StartsWith(x.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                (path.Length == x.TrimEnd('\\').Length || path[x.TrimEnd('\\').Length] == '\\'));
    }

    public static class SystemInfo
    {
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
