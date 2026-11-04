using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

        StartupState _state;
        public StartupState State
        {
            get => _state;
            set
            {
                if (!Set(ref _state, value)) return;
                Raise(nameof(Enabled)); Raise(nameof(EnabledState)); Raise(nameof(StateText)); Raise(nameof(CanToggle));
            }
        }
        public bool Enabled { get => State == StartupState.Enabled; set => State = value ? StartupState.Enabled : StartupState.Disabled; }
        public bool? EnabledState => State == StartupState.Unknown ? (bool?)null : Enabled;
        public bool CanToggle => State != StartupState.Unknown;
        public string StateText => State.ToString();
    }

    public sealed class StartupLoadResult
    {
        public List<StartupEntry> Entries { get; } = new List<StartupEntry>();
        public List<string> Warnings { get; } = new List<string>();
        public bool IsComplete => Warnings.Count == 0;
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
            var result = LoadDetailed();
            if (!result.IsComplete)
                throw new IOException("The startup list is incomplete: " + string.Join("; ", result.Warnings));
            return result.Entries;
        }

        public static StartupLoadResult LoadDetailed()
        {
            var result = new StartupLoadResult();
            AddRun(result, "HKCU", $@"Software\{CV}\Run", "Run", "Registry · current user");
            AddRun(result, "HKLM", $@"SOFTWARE\{CV}\Run", "Run", "Registry · all users");
            if (Environment.Is64BitOperatingSystem)
                AddRun(result, "HKLM", $@"SOFTWARE\WOW6432Node\{CV}\Run", "Run32", "Registry · all users (32-bit)");
            AddFolder(result, "HKCU", Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder · current user");
            AddFolder(result, "HKLM", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder · all users");
            result.Entries.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
            return result;
        }

        static void Warn(StartupLoadResult result, string message)
        {
            result.Warnings.Add(message);
            Log.Write(message);
        }

        static void AddRun(StartupLoadResult result, string hive, string key, string approvedSub, string location)
        {
            try
            {
                using (var b = RegistryScanner.Base(hive))
                using (var k = b.OpenSubKey(key))
                {
                    if (k == null) return;
                    foreach (var name in k.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        var entry = new StartupEntry
                        {
                            Name = name, Location = location, Hive = hive, RunKey = key,
                            ApprovedKey = Approved + approvedSub, ApprovedName = name,
                        };
                        try
                        {
                            entry.Command = k.GetValue(name) as string;
                            if (string.IsNullOrWhiteSpace(entry.Command))
                                Warn(result, $"The startup command for '{name}' is missing or unsupported.");
                            else
                            {
                                entry.FilePath = CommandLine.ExePath(entry.Command);
                                entry.Publisher = PublisherOf(entry.FilePath);
                                entry.State = ReadState(result, hive, entry.ApprovedKey, name);
                            }
                        }
                        catch (Exception ex) { Warn(result, $"Could not read startup item '{name}' from {location}: {ex.Message}"); }
                        result.Entries.Add(entry);
                    }
                }
            }
            catch (Exception ex) { Warn(result, $"Could not read {location}: {ex.Message}"); }
        }

        static void AddFolder(StartupLoadResult result, string hive, string directory, string location)
        {
            if (string.IsNullOrEmpty(directory))
            {
                Warn(result, $"The path for {location} is unavailable.");
                return;
            }
            try
            {
                foreach (var file in Directory.GetFiles(directory))
                {
                    var name = Path.GetFileName(file);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    string target = file, args = "";
                    bool resolved = !file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                        TryResolveShortcut(file, out target, out args);
                    if (!resolved) Warn(result, $"Could not resolve startup shortcut '{name}'. Its target is unavailable.");
                    result.Entries.Add(new StartupEntry
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        Command = resolved ? (CommandLine.QuoteArgument(target) + " " + args).Trim() : file,
                        FilePath = target, Publisher = resolved ? PublisherOf(target) : "", Location = location,
                        Hive = hive, ShortcutPath = file, ApprovedKey = Approved + "StartupFolder", ApprovedName = name,
                        State = resolved ? ReadState(result, hive, Approved + "StartupFolder", name) : StartupState.Unknown,
                    });
                }
            }
            catch (DirectoryNotFoundException) { } // A startup folder with no entries may not exist yet.
            catch (Exception ex) { Warn(result, $"Could not read {location}: {ex.Message}"); }
        }

        public static void ResolveShortcut(string shortcut, out string target, out string args) =>
            TryResolveShortcut(shortcut, out target, out args);

        static bool TryResolveShortcut(string shortcut, out string target, out string args)
        {
            target = shortcut; args = "";
            object shellObject = null, shortcutObject = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                shellObject = Activator.CreateInstance(shellType);
                dynamic shell = shellObject;
                shortcutObject = shell.CreateShortcut(shortcut);
                dynamic link = shortcutObject;
                string resolved = link.TargetPath;
                if (string.IsNullOrEmpty(resolved)) return false;
                target = resolved;
                args = (string)link.Arguments ?? "";
                return true;
            }
            catch { return false; }
            finally
            {
                ReleaseCom(shortcutObject);
                ReleaseCom(shellObject);
            }
        }

        static void ReleaseCom(object value)
        {
            try { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
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

        static StartupState ReadState(StartupLoadResult result, string hive, string key, string name)
        {
            try
            {
                using (var b = RegistryScanner.Base(hive))
                using (var k = b.OpenSubKey(key))
                {
                    var state = StartupApproval.Parse(k?.GetValue(name));
                    if (state == StartupState.Unknown)
                        Warn(result, $"Windows reports an unrecognized startup status for '{name}'.");
                    return state;
                }
            }
            catch (Exception ex)
            {
                Warn(result, $"Could not read startup status for '{name}': {ex.Message}");
                return StartupState.Unknown;
            }
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
                var backupKeys = new List<string> { e.Hive + "\\" + e.RunKey };
                using (var b = RegistryScanner.Base(e.Hive))
                using (var approved = b.OpenSubKey(e.ApprovedKey))
                    if (approved?.GetValue(e.ApprovedName) != null)
                        backupKeys.Add(e.Hive + "\\" + e.ApprovedKey);
                // Restoring a disabled startup entry must also restore its disabled state.
                RegistryBackup.Export(backupKeys, "startup");
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
