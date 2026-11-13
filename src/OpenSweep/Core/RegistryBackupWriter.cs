using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OpenSweep.Core
{
    /// <summary>Publishes a backup only after every requested registry export has succeeded.</summary>
    internal static class RegistryBackupWriter
    {
        const string Header = "Windows Registry Editor Version 5.00";

        internal static string Write(IEnumerable<string> fullKeys, string directory, string label,
            Func<string, string, int> exportKey)
        {
            if (fullKeys == null) throw new ArgumentNullException(nameof(fullKeys));
            if (exportKey == null) throw new ArgumentNullException(nameof(exportKey));
            var keys = fullKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (keys.Count == 0 || keys.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("At least one registry key is required for a backup.", nameof(fullKeys));

            var backup = new StringBuilder(Header + "\r\n");
            foreach (var key in keys)
            {
                var temporaryExport = Path.Combine(Path.GetTempPath(), $"opensweep-{Guid.NewGuid():N}.reg");
                try
                {
                    int code = exportKey(key, temporaryExport);
                    if (code != 0 || !File.Exists(temporaryExport))
                        throw new IOException($"Could not back up {key} (registry export exit code {code}). No entries were changed.");

                    var text = File.ReadAllText(temporaryExport, Encoding.Unicode);
                    int newline = text.IndexOf('\n');
                    var firstLine = newline < 0 ? text : text.Substring(0, newline);
                    var body = newline < 0 ? "" : text.Substring(newline + 1);
                    var fullName = key.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)
                        ? "HKEY_LOCAL_MACHINE" + key.Substring(4)
                        : key.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)
                            ? "HKEY_CURRENT_USER" + key.Substring(4) : key;
                    var section = "[" + fullName + "]";
                    bool containsKey = body.Split('\n').Any(line =>
                        line.TrimEnd('\r').Equals(section, StringComparison.OrdinalIgnoreCase));
                    if (!firstLine.TrimEnd('\r').Equals(Header, StringComparison.Ordinal) || !containsKey)
                        throw new IOException($"The backup of {key} is incomplete or invalid. No entries were changed.");
                    backup.Append("\r\n").Append(body).Append("\r\n");
                }
                finally { try { File.Delete(temporaryExport); } catch { } }
            }

            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"{label}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.reg");
            var pending = file + ".tmp";
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, Encoding.Unicode))
                    writer.Write(backup.ToString());
                File.Move(pending, file);
                return file;
            }
            finally { try { File.Delete(pending); } catch { } }
        }
    }
}
