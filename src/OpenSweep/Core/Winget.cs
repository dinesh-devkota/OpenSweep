using System;
using System.Collections.Generic;
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
            if (list.Count == 0 && code != 0 && code != NoPackagesFound)
                throw new InvalidOperationException(LastLine(output) ?? $"winget exited with code {code}.");
            return list;
        }

        public static List<AppUpdate> Parse(string output)
        {
            var list = new List<AppUpdate>();
            var lines = output.Replace("\r\n", "\n").Split('\n').Select(ProcessRunner.CleanLine).ToList();
            int table = 0;
            for (int i = 1; i < lines.Count; i++)
            {
                if (!IsDashes(lines[i])) continue;
                var starts = Regex.Matches(lines[i - 1], @"\S+").Cast<Match>().Select(m => m.Index).ToList();
                if (starts.Count < 4) continue;
                if (starts.Count > 5) starts = new[] { starts[0] }.Concat(starts.Skip(starts.Count - 4)).ToList();
                bool hasSource = starts.Count == 5;
                int availableCol = starts[3];
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var row = lines[j];
                    if (row.Length <= availableCol || IsDashes(row)) break;
                    var app = ParseRow(row, starts, hasSource);
                    if (app == null) break;
                    app.Explicit = table > 0;
                    if (!list.Any(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase))) list.Add(app);
                    i = j;
                }
                table++;
            }
            return list;
        }

        static AppUpdate ParseRow(string row, List<int> s, bool hasSource)
        {
            string Field(int k) => Sub(row, s[k], k + 1 < s.Count ? s[k + 1] : row.Length);
            var app = new AppUpdate
            {
                Name = Field(0), Id = Field(1), Version = Field(2), Available = Field(3), Source = hasSource ? Field(4) : "",
            };
            bool aligned = row[s[1] - 1] == ' ' && app.Id.Length > 0 && !app.Id.Contains(" ");
            if (!aligned)
            {
                // Rows containing wide characters can shift columns; parse from the right instead.
                var t = row.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int need = hasSource ? 5 : 4;
                if (t.Length < need) return null;
                int n = t.Length;
                app.Source = hasSource ? t[n - 1] : "";
                int off = hasSource ? 1 : 0;
                app.Available = t[n - 1 - off];
                app.Version = t[n - 2 - off];
                app.Id = t[n - 3 - off];
                app.Name = string.Join(" ", t.Take(n - 3 - off));
            }
            if (app.Id.Length == 0 || app.Available.Length == 0) return null;
            if (app.Name.Length == 0) app.Name = app.Id;
            return app;
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
            var args = $"upgrade --id \"{app.Id}\" --exact --accept-package-agreements --accept-source-agreements --disable-interactivity"
                + (silent ? " --silent" : "")
                + (app.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || app.Version.StartsWith("<") ? " --include-unknown" : "")
                + (string.IsNullOrEmpty(app.Source) ? "" : $" --source \"{app.Source}\"");
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
