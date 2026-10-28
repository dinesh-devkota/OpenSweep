using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenSweep.Core
{
    public sealed class InstalledProgram
    {
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public DateTime? InstalledOn { get; set; }
        public long Size { get; set; }
        public string SizeText => Size > 0 ? Fmt.Bytes(Size) : "";
        public string InstalledText => InstalledOn?.ToString("d") ?? "";
        public string UninstallString { get; set; }
        public string InstallLocation { get; set; }
        public string Hive { get; set; }
        public string KeyPath { get; set; }
        /// <summary>MSI product code when the program was installed by Windows Installer.</summary>
        public string ProductCode { get; set; }
        public string Scope => Hive == "HKCU" ? "Current user" : KeyPath.IndexOf("WOW6432Node", StringComparison.OrdinalIgnoreCase) >= 0 ? "All users (32-bit)" : "All users";
    }

    public static class InstalledPrograms
    {
        const string CV = @"Microsoft\Windows\CurrentVersion\Uninstall";
        static readonly Regex Guid = new Regex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");

        public static List<InstalledProgram> Load()
        {
            var list = new List<InstalledProgram>();
            Read(list, "HKLM", $@"SOFTWARE\{CV}");
            Read(list, "HKLM", $@"SOFTWARE\WOW6432Node\{CV}");
            Read(list, "HKCU", $@"Software\{CV}");
            return list.GroupBy(p => (p.Name + "|" + p.Version).ToLowerInvariant()).Select(g => g.First())
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static void Read(List<InstalledProgram> list, string hive, string path)
        {
            try
            {
                using (var b = RegistryScanner.Base(hive))
                using (var root = b.OpenSubKey(path))
                {
                    if (root == null) return;
                    foreach (var name in root.GetSubKeyNames())
                    {
                        try
                        {
                            using (var k = root.OpenSubKey(name))
                            {
                                var display = (k?.GetValue("DisplayName") as string)?.Trim();
                                if (string.IsNullOrEmpty(display)) continue;
                                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                                if (k.GetValue("ParentKeyName") != null) continue;
                                var release = k.GetValue("ReleaseType") as string ?? "";
                                if (release.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    release.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                var us = k.GetValue("UninstallString") as string;
                                bool msi = k.GetValue("WindowsInstaller") is int wi && wi == 1;
                                if (string.IsNullOrWhiteSpace(us) && !msi) continue; // Windows hides these too
                                string code = null;
                                if (msi && Guid.IsMatch(name) && Guid.Match(name).Value.Length == name.Length) code = name;
                                else if (us != null && us.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0 && Guid.IsMatch(us)) code = Guid.Match(us).Value;
                                DateTime? date = null;
                                if (DateTime.TryParseExact(k.GetValue("InstallDate") as string ?? "", "yyyyMMdd",
                                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;
                                list.Add(new InstalledProgram
                                {
                                    Name = display,
                                    Publisher = (k.GetValue("Publisher") as string ?? "").Trim(),
                                    Version = (k.GetValue("DisplayVersion") as string ?? "").Trim(),
                                    InstalledOn = date,
                                    Size = k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : 0,
                                    UninstallString = us,
                                    InstallLocation = k.GetValue("InstallLocation") as string,
                                    Hive = hive,
                                    KeyPath = path + "\\" + name,
                                    ProductCode = code,
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Write($"Programs {hive}\\{path}: {ex.Message}"); }
        }

        /// <summary>Starts the program's own uninstaller (Windows asks for administrator rights if it needs them).</summary>
        public static Process Uninstall(InstalledProgram p)
        {
            Log.Write($"Uninstall started: {p.Name} {p.Version}");
            if (!string.IsNullOrEmpty(p.ProductCode))
                return Process.Start(new ProcessStartInfo("msiexec.exe", $"/x {p.ProductCode}") { UseShellExecute = true });
            CommandLine.Split(p.UninstallString, out var exe, out var args);
            return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }

        /// <summary>Deletes a leftover entry from the programs list (backed up to a .reg file first).</summary>
        public static void RemoveEntry(InstalledProgram p)
        {
            RegistryBackup.Export(new[] { p.Hive + "\\" + p.KeyPath }, "program-entry");
            int idx = p.KeyPath.LastIndexOf('\\');
            using (var b = RegistryScanner.Base(p.Hive))
            using (var parent = b.OpenSubKey(p.KeyPath.Substring(0, idx), true))
                parent?.DeleteSubKeyTree(p.KeyPath.Substring(idx + 1), false);
            Log.Write($"Program entry removed: {p.Name}");
        }
    }
}
