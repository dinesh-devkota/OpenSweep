using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

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
        public string Theme { get; set; } = "System";

        static readonly Lazy<AppSettings> CurrentSettings = new Lazy<AppSettings>(Load);
        static readonly object SaveGate = new object();
        public static AppSettings Current => CurrentSettings.Value;

        static AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    using (var fs = File.OpenRead(AppPaths.SettingsFile))
                    {
                        var settings = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(fs) ?? new AppSettings();
                        settings.ExcludedPaths = settings.ExcludedPaths ?? new List<string>();
                        return settings;
                    }
            }
            catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }
            return new AppSettings();
        }

        public void Save()
        {
            lock (SaveGate)
            {
                string tmp = AppPaths.SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    // Write and flush completely before replacing the last valid settings file.
                    using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        new XmlSerializer(typeof(AppSettings)).Serialize(fs, this);
                        fs.Flush(true);
                    }
                    if (File.Exists(AppPaths.SettingsFile)) File.Replace(tmp, AppPaths.SettingsFile, null);
                    else File.Move(tmp, AppPaths.SettingsFile);
                }
                catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); }
                    catch (Exception ex) { Log.Write("Settings temporary file cleanup failed: " + ex.Message); }
                }
            }
        }

        public bool IsExcluded(string path)
        {
            var full = NormalizePath(path);
            // A malformed target must never enter a cleanup operation.
            if (full == null) return true;
            var exclusions = ExcludedPaths?.ToArray() ?? new string[0];
            foreach (var entry in exclusions)
            {
                var excluded = NormalizePath(entry);
                if (excluded == null) continue;
                if (string.Equals(full, excluded, StringComparison.OrdinalIgnoreCase)) return true;
                string prefix = excluded.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? excluded : excluded + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            try
            {
                string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                string root = Path.GetPathRoot(full);
                while (full.Length > root.Length && full[full.Length - 1] == Path.DirectorySeparatorChar)
                    full = full.Substring(0, full.Length - 1);
                return full;
            }
            catch { return null; }
        }

    }

}
