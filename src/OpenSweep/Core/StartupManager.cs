using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace OpenSweep.Core
{
    public sealed class StartupEntry : ObservableObject
    {
        public string Name { get; set; }
        public string Command { get; set; }
        public string FilePath { get; set; }
        public string Publisher { get; set; }
        public string Location { get; set; }
        public string Hive { get; set; }
        public string RunKey { get; set; }
        public string ShortcutPath { get; set; }
        public string ApprovedKey { get; set; }
        public string ApprovedName { get; set; }
        public bool NeedsAdmin => Hive == "HKLM";
        public bool IsRegistry => RunKey != null;

        bool _enabled;
        public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Raise(nameof(StateText)); } }
        public string StateText => Enabled ? "Enabled" : "Disabled";
    }

    /// <summary>
    /// Reads Run keys and Startup folders. Enable/disable uses the same "StartupApproved" flags as Task Manager,
    /// so changes are reversible and visible in Windows Settings too.
    /// </summary>
    public static class StartupManager
    {
        const string CV = @"Microsoft\Windows\CurrentVersion";
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static List<StartupEntry> Load()
        {
            var list = new List<StartupEntry>();
            AddRun(list, "HKCU", $@"Software\{CV}\Run", "Run", "Registry · current user");
            AddRun(list, "HKLM", $@"SOFTWARE\{CV}\Run", "Run", "Registry · all users");
            AddRun(list, "HKLM", $@"SOFTWARE\WOW6432Node\{CV}\Run", "Run32", "Registry · all users (32-bit)");
            AddFolder(list, "HKCU", Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder · current user");
            AddFolder(list, "HKLM", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder · all users");
            return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static void AddRun(List<StartupEntry> list, string hive, string key, string approvedSub, string location)
        {
            try
            {
                using (var b = RegistryScanner.Base(hive))
                using (var k = b.OpenSubKey(key))
                {
                    if (k == null) return;
                    foreach (var v in k.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(v)) continue;
                        var cmd = k.GetValue(v)?.ToString() ?? "";
                        var exe = CommandLine.ExePath(cmd);
                        list.Add(new StartupEntry
                        {
                            Name = v, Command = cmd, FilePath = exe, Publisher = PublisherOf(exe), Location = location,
                            Hive = hive, RunKey = key, ApprovedKey = Approved + approvedSub, ApprovedName = v,
                            Enabled = IsApproved(hive, Approved + approvedSub, v),
                        });
                    }
                }
            }
            catch (Exception ex) { Log.Write($"Startup {hive}\\{key}: {ex.Message}"); }
        }

        static void AddFolder(List<StartupEntry> list, string hive, string dir, string location)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string target = f, args = "";
                if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) ResolveShortcut(f, out target, out args);
                list.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(f), Command = (target + " " + args).Trim(), FilePath = target,
                    Publisher = PublisherOf(target), Location = location, Hive = hive, ShortcutPath = f,
                    ApprovedKey = Approved + "StartupFolder", ApprovedName = name,
                    Enabled = IsApproved(hive, Approved + "StartupFolder", name),
                });
            }
        }

        public static void ResolveShortcut(string lnk, out string target, out string args)
        {
            target = lnk; args = "";
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic sc = shell.CreateShortcut(lnk);
                string t = sc.TargetPath;
                if (!string.IsNullOrEmpty(t)) { target = t; args = (string)sc.Arguments ?? ""; }
            }
            catch { }
        }

        static string PublisherOf(string exe)
        {
            try
            {
                if (PathCheck.IsDefinitelyMissing(exe)) return "(file not found)";
                return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "" : "";
            }
            catch { return ""; }
        }

        static bool IsApproved(string hive, string key, string name)
        {
            try
            {
                using (var b = RegistryScanner.Base(hive))
                using (var k = b.OpenSubKey(key))
                    return !(k?.GetValue(name) is byte[] data) || data.Length == 0 || (data[0] & 1) == 0;
            }
            catch { return true; }
        }

        public static void SetEnabled(StartupEntry e, bool enabled)
        {
            var data = new byte[12];
            data[0] = (byte)(enabled ? 2 : 3);
            if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            using (var b = RegistryScanner.Base(e.Hive))
            using (var k = b.CreateSubKey(e.ApprovedKey))
                k.SetValue(e.ApprovedName, data, RegistryValueKind.Binary);
            e.Enabled = enabled;
            Log.Write($"Startup '{e.Name}' {(enabled ? "enabled" : "disabled")}");
        }

        /// <summary>Removes the entry (registry values are backed up first; shortcuts go to the Recycle Bin).</summary>
        public static void Delete(StartupEntry e)
        {
            if (e.IsRegistry)
            {
                RegistryBackup.Export(new[] { e.Hive + "\\" + e.RunKey }, "startup");
                using (var b = RegistryScanner.Base(e.Hive))
                using (var k = b.OpenSubKey(e.RunKey, true))
                    k?.DeleteValue(e.Name, false);
            }
            else if (!ShellOps.SendToRecycleBin(e.ShortcutPath))
                throw new IOException("Could not move the shortcut to the Recycle Bin.");
            try
            {
                using (var b = RegistryScanner.Base(e.Hive))
                using (var k = b.OpenSubKey(e.ApprovedKey, true))
                    k?.DeleteValue(e.ApprovedName, false);
            }
            catch { }
            Log.Write($"Startup '{e.Name}' deleted");
        }
    }
}
