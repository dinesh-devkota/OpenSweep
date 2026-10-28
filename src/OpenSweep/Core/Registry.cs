using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace OpenSweep.Core
{
    public sealed class RegistryIssue : ObservableObject
    {
        public string Category { get; set; }
        public string Problem { get; set; }
        public string Hive { get; set; }
        public string KeyPath { get; set; }
        /// <summary>Value to delete, or null to delete the whole key.</summary>
        public string ValueName { get; set; }

        public string FullKey => Hive + "\\" + KeyPath;
        public string Location => ValueName == null ? FullKey : $"{FullKey}  \u203A  {(ValueName.Length == 0 ? "(Default)" : ValueName)}";
        public bool NeedsAdmin => Hive == "HKLM";

        bool _checked = true;
        public bool IsChecked { get => _checked; set => Set(ref _checked, value); }

        string _status = "";
        public string Status { get => _status; set => Set(ref _status, value); }
    }

    public static class RegistryScanner
    {
        const string CV = @"Microsoft\Windows\CurrentVersion";
        static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        public static RegistryKey Base(string hive) =>
            RegistryKey.OpenBaseKey(hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

        static RegistryKey Open(string hive, string path)
        {
            using (var b = Base(hive)) return b.OpenSubKey(path);
        }

        public static List<RegistryIssue> Scan(IProgress<string> progress, CancellationToken ct)
        {
            var list = new List<RegistryIssue>();
            void Step(string name, Action a)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(name);
                try { a(); } catch (Exception ex) { Log.Write($"Registry scan '{name}': {ex.Message}"); }
            }

            Step("Missing shared DLLs", () =>
            {
                foreach (var k in new[] { $@"SOFTWARE\{CV}\SharedDLLs", $@"SOFTWARE\WOW6432Node\{CV}\SharedDLLs" })
                    ValueNamesAsPaths(list, "HKLM", k, "Missing shared DLL", "Shared DLL no longer exists");
            });
            Step("Application paths", () =>
            {
                foreach (var (hive, k) in new[] { ("HKLM", $@"SOFTWARE\{CV}\App Paths"), ("HKLM", $@"SOFTWARE\WOW6432Node\{CV}\App Paths"), ("HKCU", $@"Software\{CV}\App Paths") })
                    ForEachSubKey(hive, k, (name, sub) =>
                    {
                        var target = sub.GetValue("") as string;
                        if (PathCheck.IsDefinitelyMissing(target))
                            Add(list, "Application paths", $"Program not found: {target}", hive, $@"{k}\{name}", null);
                    });
            });
            Step("Obsolete software", () =>
            {
                foreach (var (hive, k) in new[] { ("HKLM", $@"SOFTWARE\{CV}\Uninstall"), ("HKLM", $@"SOFTWARE\WOW6432Node\{CV}\Uninstall"), ("HKCU", $@"Software\{CV}\Uninstall") })
                    ForEachSubKey(hive, k, (name, sub) =>
                    {
                        var display = sub.GetValue("DisplayName") as string;
                        var us = sub.GetValue("UninstallString") as string;
                        if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(us)) return;
                        if (sub.GetValue("WindowsInstaller") is int wi && wi == 1) return;
                        if (us.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0) return;
                        var exe = CommandLine.ExePath(us);
                        if (!PathCheck.IsDefinitelyMissing(exe)) return;
                        var loc = sub.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(loc) && !PathCheck.IsDefinitelyMissing(loc)) return;
                        Add(list, "Obsolete software", $"'{display}' is no longer installed (uninstaller missing)", hive, $@"{k}\{name}", null);
                    });
            });
            Step("Startup entries", () =>
            {
                foreach (var (hive, k) in new[] { ("HKCU", $@"Software\{CV}\Run"), ("HKLM", $@"SOFTWARE\{CV}\Run"), ("HKLM", $@"SOFTWARE\WOW6432Node\{CV}\Run") })
                    using (var key = Open(hive, k))
                    {
                        if (key == null) continue;
                        foreach (var v in key.GetValueNames())
                        {
                            var exe = CommandLine.ExePath(key.GetValue(v) as string);
                            if (PathCheck.IsDefinitelyMissing(exe))
                                Add(list, "Startup entries", $"Startup program not found: {exe}", hive, k, v);
                        }
                    }
            });
            Step("Installer folders", () =>
                ValueNamesAsPaths(list, "HKLM", $@"SOFTWARE\{CV}\Installer\Folders", "Installer folders", "Folder no longer exists"));
            Step("Fonts", () =>
            {
                foreach (var (hive, k) in new[] { ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"), ("HKCU", @"Software\Microsoft\Windows NT\CurrentVersion\Fonts") })
                    using (var key = Open(hive, k))
                    {
                        if (key == null) continue;
                        foreach (var v in key.GetValueNames())
                        {
                            var file = key.GetValue(v) as string;
                            if (string.IsNullOrWhiteSpace(file) || file.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                            var full = Path.IsPathRooted(file) ? file : Path.Combine(Win, "Fonts", file);
                            if (PathCheck.IsDefinitelyMissing(full))
                                Add(list, "Fonts", $"Font file not found: {full}", hive, k, v);
                        }
                    }
            });
            Step("Help files", () =>
            {
                foreach (var k in new[] { @"SOFTWARE\Microsoft\Windows\Help", @"SOFTWARE\WOW6432Node\Microsoft\Windows\Help" })
                    using (var key = Open("HKLM", k))
                    {
                        if (key == null) continue;
                        foreach (var v in key.GetValueNames())
                        {
                            var folder = key.GetValue(v) as string;
                            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(v)) continue;
                            if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || v.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                            var full = Path.Combine(folder, v);
                            if (PathCheck.IsDefinitelyMissing(full))
                                Add(list, "Help files", $"Help file not found: {full}", "HKLM", k, v);
                        }
                    }
            });
            Step("MUI cache", () =>
            {
                const string k = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
                using (var key = Open("HKCU", k))
                {
                    if (key == null) return;
                    foreach (var v in key.GetValueNames())
                    {
                        string path = null;
                        if (v.StartsWith("@"))
                        {
                            int comma = v.IndexOf(',');
                            path = comma > 1 ? v.Substring(1, comma - 1) : null;
                        }
                        else
                        {
                            int i = v.IndexOf(".exe.", StringComparison.OrdinalIgnoreCase);
                            if (i > 0) path = v.Substring(0, i + 4);
                        }
                        if (PathCheck.IsDefinitelyMissing(path))
                            Add(list, "MUI cache", $"Cached name of a removed program: {path}", "HKCU", k, v);
                    }
                }
            });
            return list;
        }

        static void Add(List<RegistryIssue> list, string cat, string problem, string hive, string key, string value) =>
            list.Add(new RegistryIssue { Category = cat, Problem = problem, Hive = hive, KeyPath = key, ValueName = value });

        static void ValueNamesAsPaths(List<RegistryIssue> list, string hive, string keyPath, string cat, string problem)
        {
            using (var key = Open(hive, keyPath))
            {
                if (key == null) return;
                foreach (var v in key.GetValueNames())
                    if (PathCheck.IsDefinitelyMissing(v))
                        Add(list, cat, $"{problem}: {v}", hive, keyPath, v);
            }
        }

        static void ForEachSubKey(string hive, string keyPath, Action<string, RegistryKey> action)
        {
            using (var key = Open(hive, keyPath))
            {
                if (key == null) return;
                foreach (var name in key.GetSubKeyNames())
                {
                    try { using (var sub = key.OpenSubKey(name)) if (sub != null) action(name, sub); }
                    catch { }
                }
            }
        }
    }

    public static class RegistryFixer
    {
        /// <summary>Backs up every affected key to a .reg file, then removes the selected issues. Returns the backup path.</summary>
        public static string Fix(IList<RegistryIssue> issues, IProgress<string> progress = null)
        {
            progress?.Report("Backing up registry…");
            var backup = RegistryBackup.Export(issues.Select(i => i.FullKey), "registry-fix");
            foreach (var i in issues)
            {
                progress?.Report("Fixing " + i.Location);
                try
                {
                    using (var b = RegistryScanner.Base(i.Hive))
                    {
                        if (i.ValueName == null)
                        {
                            int idx = i.KeyPath.LastIndexOf('\\');
                            using (var parent = b.OpenSubKey(i.KeyPath.Substring(0, idx), true))
                                parent?.DeleteSubKeyTree(i.KeyPath.Substring(idx + 1), false);
                        }
                        else
                            using (var k = b.OpenSubKey(i.KeyPath, true))
                                k?.DeleteValue(i.ValueName, false);
                    }
                    i.Status = "Fixed";
                }
                catch (Exception ex) { i.Status = "Failed: " + ex.Message; }
            }
            Log.Write($"Registry: fixed {issues.Count(i => i.Status == "Fixed")} of {issues.Count}; backup {backup}");
            return backup;
        }
    }

    public static class RegistryBackup
    {
        public static string Export(IEnumerable<string> fullKeys, string label)
        {
            var file = Path.Combine(AppPaths.BackupDir, $"{label}-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
            var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
            var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
            foreach (var key in fullKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"opensweep-{Guid.NewGuid():N}.reg");
                try
                {
                    var (code, _) = ProcessRunner.CaptureAsync(reg, $"export \"{key}\" \"{tmp}\" /y").GetAwaiter().GetResult();
                    if (code == 0 && File.Exists(tmp))
                    {
                        var text = File.ReadAllText(tmp, Encoding.Unicode);
                        int nl = text.IndexOf('\n');
                        sb.Append(nl >= 0 ? text.Substring(nl + 1) : "");
                    }
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
            return file;
        }

        /// <summary>Restores a .reg backup with Regedit (prompts for administrator rights when needed).</summary>
        public static void Import(string file) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("regedit.exe", $"/s \"{file}\"") { UseShellExecute = true });
    }
}
