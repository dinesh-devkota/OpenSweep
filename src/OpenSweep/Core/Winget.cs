using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSweep.Core
{
    public sealed class AppUpdate : ObservableObject
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string Version { get; set; }
        public string Available { get; set; }
        public string Source { get; set; }
        /// <summary>Listed by winget as needing explicit targeting (pinned or similar).</summary>
        public bool Explicit { get; set; }

        bool _checked = true;
        public bool IsChecked { get => _checked; set => Set(ref _checked, value); }

        string _status = "";
        public string Status { get => _status; set => Set(ref _status, value); }
    }

    /// <summary>Thin wrapper around the Windows Package Manager (winget) command line.</summary>
    public static class Winget
    {
        const int NoPackagesFound = unchecked((int)0x8A150014);

        public static string Exe
        {
            get
            {
                var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\WindowsApps\winget.exe");
                return File.Exists(alias) ? alias : "winget.exe";
            }
        }

        public static bool IsAvailable =>
            File.Exists(Exe) || (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Any(d =>
            {
                try { return d.Trim().Length > 0 && File.Exists(Path.Combine(d.Trim(), "winget.exe")); } catch { return false; }
            });

        public const string StoreLink = "ms-windows-store://pdp/?productid=9NBLGGH4NNS1";

        public static async Task<List<AppUpdate>> ListAsync(bool includeUnknown, CancellationToken ct)
        {
            if (!IsAvailable) throw new InvalidOperationException("winget (App Installer) is not installed. Install 'App Installer' from the Microsoft Store.");
            var args = "upgrade --accept-source-agreements --disable-interactivity" + (includeUnknown ? " --include-unknown" : "");
            var (code, output) = await ProcessRunner.CaptureAsync(Exe, args, ct).ConfigureAwait(false);
            var list = Parse(output);
            if (code != 0 && code != NoPackagesFound)
                throw new InvalidOperationException(LastLine(output) ?? $"winget exited with code {code}.");
            return list;
        }

        public static List<AppUpdate> Parse(string output)
        {
            var list = new List<AppUpdate>();
            if (string.IsNullOrEmpty(output)) return list;
            var lines = output.Replace("\r\n", "\n").Split('\n').Select(ProcessRunner.CleanLine).ToList();
            int table = 0;
            for (int i = 1; i < lines.Count; i++)
            {
                if (!IsDashes(lines[i])) continue;
                // Offsets are terminal cells, not UTF-16 indices: CJK names occupy two
                // cells. Translated headings can contain words separated by single spaces.
                var header = lines[i - 1];
                var starts = Regex.Matches(header, @"\S+").Cast<Match>()
                    .Select(m => DisplayWidth(header.Substring(0, m.Index))).ToList();
                // A column can have only one separating space when its heading is the
                // widest value. The usual single-word layout is unambiguous even then.
                if (starts.Count > 5)
                {
                    starts = Regex.Matches(header, @"\S(?:.*?\S)?(?= {2,}|$)").Cast<Match>()
                        .Select(m => DisplayWidth(header.Substring(0, m.Index))).ToList();
                    // Four padded groups could be five columns with one merged pair.
                    // Do not guess that a translated table has no Source column.
                    if (starts.Count != 5) throw UnreadableTable();
                }
                if (starts.Count != 4 && starts.Count != 5)
                    throw UnreadableTable();
                bool hasSource = starts.Count == 5;
                int rows = 0;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var row = lines[j];
                    if (row.Length == 0 || IsDashes(row)) break;
                    var app = ParseRow(row, starts, hasSource);
                    if (app == null)
                    {
                        // winget follows the table with a localized count, e.g. "2 upgrades
                        // available." Unlike rows, these summaries have no column padding.
                        if (rows > 0 && char.IsDigit(row[0]) && !row.Contains("  ")) break;
                        throw UnreadableTable();
                    }
                    // A shortened ID is not a usable --exact target. Failing the search
                    // also prevents a table of only truncated rows looking up to date.
                    if (IsTruncated(app.Id) || IsTruncated(app.Source))
                        throw new InvalidOperationException("Windows Package Manager shortened a package identifier or source name. Review the updates by running winget upgrade in a wider terminal.");
                    app.Explicit = table > 0;
                    if (!list.Any(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase))) list.Add(app);
                    rows++;
                    i = j;
                }
                if (rows == 0) throw UnreadableTable();
                table++;
            }
            return list;
        }

        static AppUpdate ParseRow(string row, List<int> s, bool hasSource)
        {
            var indices = s.Select(column => ColumnIndex(row, column)).ToList();
            if (indices.Any(index => index < 0)) return null;
            string Field(int k) => Sub(row, indices[k], k + 1 < s.Count ? indices[k + 1] : row.Length);
            var app = new AppUpdate
            {
                Name = Field(0), Id = Field(1), Version = Field(2), Available = Field(3), Source = hasSource ? Field(4) : "",
            };
            if (app.Id.Length == 0 ||
                app.Id.Any(char.IsWhiteSpace) || app.Version.Length == 0 || app.Available.Length == 0) return null;
            for (int k = 1; k < indices.Count; k++)
            {
                if (k == 4 && app.Source.Length == 0) continue;
                if (indices[k] == 0 || row[indices[k] - 1] != ' ') return null;
            }
            if (app.Name.Length == 0) app.Name = app.Id;
            return app;
        }

        static bool IsTruncated(string value) => value.EndsWith("\u2026", StringComparison.Ordinal) ||
            value.EndsWith("...", StringComparison.Ordinal);

        static InvalidOperationException UnreadableTable() => new InvalidOperationException(
            "Windows Package Manager returned an update table that could not be read. Review the updates by running winget upgrade.");

        static int DisplayWidth(string text)
        {
            int width = 0;
            for (int i = 0; i < text.Length;)
            {
                width += CellWidth(text, i, out var length);
                i += length;
            }
            return width;
        }

        static int ColumnIndex(string text, int column)
        {
            int width = 0;
            for (int i = 0; i < text.Length;)
            {
                int cells = CellWidth(text, i, out var length);
                if (width == column && cells != 0) return i;
                if (width + cells > column) return -1; // Never split a wide character.
                width += cells;
                i += length;
            }
            return text.Length;
        }

        static int CellWidth(string text, int index, out int length)
        {
            length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
            int c = length == 2 ? char.ConvertToUtf32(text[index], text[index + 1]) : text[index];
            var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
            if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark ||
                category == UnicodeCategory.Format) return 0;
            // East Asian wide/fullwidth ranges and supplementary emoji used by terminal output.
            return c >= 0x1100 && (c <= 0x115f || c == 0x2329 || c == 0x232a ||
                (c >= 0x2e80 && c <= 0xa4cf && c != 0x303f) || (c >= 0xac00 && c <= 0xd7a3) ||
                (c >= 0xf900 && c <= 0xfaff) || (c >= 0xfe10 && c <= 0xfe19) ||
                (c >= 0xfe30 && c <= 0xfe6f) || (c >= 0xff00 && c <= 0xff60) ||
                (c >= 0xffe0 && c <= 0xffe6) || (c >= 0x1f300 && c <= 0x1faff) ||
                (c >= 0x20000 && c <= 0x3fffd)) ? 2 : 1;
        }

        static string Sub(string row, int from, int to)
        {
            if (from >= row.Length) return "";
            to = Math.Min(to, row.Length);
            return row.Substring(from, Math.Max(0, to - from)).Trim();
        }

        static bool IsDashes(string line) => line.Length >= 10 && line.All(c => c == '-');

        static string LastLine(string output) =>
            output.Replace("\r\n", "\n").Split('\n').Select(ProcessRunner.CleanLine).LastOrDefault(l => l.Length > 0);

        /// <summary>Upgrades one package and returns a short human readable result.</summary>
        public static async Task<(bool Ok, string Status)> UpgradeAsync(AppUpdate app, bool silent, Action<string> onLine, CancellationToken ct)
        {
            var args = $"upgrade --id {CommandLine.QuoteArgument(app.Id)} --exact --accept-package-agreements --accept-source-agreements --disable-interactivity"
                + (silent ? " --silent" : "")
                + (string.Equals(app.Version, "Unknown", StringComparison.OrdinalIgnoreCase) || (app.Version ?? "").StartsWith("<") ? " --include-unknown" : "")
                + (string.IsNullOrEmpty(app.Source) ? "" : $" --source {CommandLine.QuoteArgument(app.Source)}");
            string last = null;
            bool restart = false;
            int code = await ProcessRunner.RunAsync(Exe, args, line =>
            {
                last = line;
                if (line.IndexOf("restart", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("reboot", StringComparison.OrdinalIgnoreCase) >= 0) restart = true;
                onLine?.Invoke(line);
            }, ct).ConfigureAwait(false);
            Log.Write($"winget upgrade {app.Id}: exit {code} ({last})");
            if (code == 0) return (true, restart ? "Updated – restart required" : "Updated");
            return (false, "Failed – " + (last ?? $"exit code 0x{code:X8}"));
        }
    }
}
