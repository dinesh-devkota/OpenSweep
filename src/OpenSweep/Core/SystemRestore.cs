using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace OpenSweep.Core
{
    public sealed class RestorePointInfo
    {
        public int Sequence { get; set; }
        public string Description { get; set; }
        public DateTime Created { get; set; }
        public string Type { get; set; }
    }

    public static class SystemRestore
    {
        const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
        const string FreqValue = "SystemRestorePointCreationFrequency";

        /// <summary>Creates a restore point (administrator only). Returns null on success, otherwise an error message.</summary>
        public static string Create(string description)
        {
            if (!Elevation.IsAdmin) return "Administrator rights are required to create a restore point.";
            RegistryKey key = null;
            object oldFreq = null;
            try
            {
                // Windows normally allows only one restore point per 24 hours; lift that limit temporarily.
                key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(SrKey, true);
                oldFreq = key?.GetValue(FreqValue);
                key?.SetValue(FreqValue, 0, RegistryValueKind.DWord);
            }
            catch { }
            try
            {
                var scope = new ManagementScope(@"\\.\root\default");
                using (var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), null))
                using (var input = cls.GetMethodParameters("CreateRestorePoint"))
                {
                    input["Description"] = description;
                    input["RestorePointType"] = 12; // MODIFY_SETTINGS
                    input["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
                    using (var result = cls.InvokeMethod("CreateRestorePoint", input, null))
                    {
                        long rv = Convert.ToInt64(result?["ReturnValue"] ?? 1L);
                        if (rv == 0) { Log.Write("Restore point created: " + description); return null; }
                        return rv == 1058 || rv == 0x80070422
                            ? "System Protection is turned off. Turn it on for your system drive in System Protection settings."
                            : $"Could not create a restore point (error 0x{rv:X}). Make sure System Protection is turned on.";
                    }
                }
            }
            catch (Exception ex) { return "Could not create a restore point: " + ex.Message; }
            finally
            {
                try
                {
                    if (key != null)
                    {
                        if (oldFreq == null) key.DeleteValue(FreqValue, false);
                        else key.SetValue(FreqValue, oldFreq, RegistryValueKind.DWord);
                    }
                }
                catch { }
                key?.Dispose();
            }
        }

        /// <summary>Lists existing restore points (needs administrator rights to read).</summary>
        public static List<RestorePointInfo> List()
        {
            var list = new List<RestorePointInfo>();
            using (var s = new ManagementObjectSearcher(@"root\default", "SELECT * FROM SystemRestore"))
                foreach (ManagementObject o in s.Get())
                    using (o)
                    {
                        DateTime created = DateTime.MinValue;
                        try { created = ManagementDateTimeConverter.ToDateTime(o["CreationTime"] as string); } catch { }
                        list.Add(new RestorePointInfo
                        {
                            Sequence = Convert.ToInt32(o["SequenceNumber"]),
                            Description = o["Description"] as string,
                            Created = created,
                            Type = TypeName(Convert.ToInt32(o["RestorePointType"])),
                        });
                    }
            return list.OrderByDescending(r => r.Created).ToList();
        }

        static string TypeName(int t)
        {
            switch (t)
            {
                case 0: return "App install";
                case 1: return "App uninstall";
                case 6: return "Restore";
                case 7: return "System checkpoint";
                case 10: return "Driver install";
                case 12: return "Settings change";
                case 13: return "Cancelled operation";
                case 18: return "Windows Update";
                default: return "System";
            }
        }

        public static List<FileInfo> RegistryBackups()
        {
            try { return new DirectoryInfo(AppPaths.BackupDir).GetFiles("*.reg").OrderByDescending(f => f.LastWriteTime).ToList(); }
            catch { return new List<FileInfo>(); }
        }

        public static void OpenSystemRestore() => ShellOps.Open(Path.Combine(Environment.SystemDirectory, "rstrui.exe"));
        public static void OpenProtectionSettings() => ShellOps.Open(Path.Combine(Environment.SystemDirectory, "SystemPropertiesProtection.exe"));
    }
}
