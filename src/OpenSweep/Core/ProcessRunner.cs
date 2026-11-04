using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSweep.Core
{
    public static class ProcessRunner
    {
        /// <summary>Runs a console program hidden, streaming cleaned-up output lines. Returns the exit code.</summary>
        public static Task<int> RunAsync(string file, string args, Action<string> onLine = null,
            CancellationToken ct = default, Encoding encoding = null)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = encoding ?? Encoding.UTF8,
                    StandardErrorEncoding = encoding ?? Encoding.UTF8,
                };
                using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    Exception outputError = null;
                    var outputGate = new object();
                    DataReceivedEventHandler handler = (s, e) =>
                    {
                        if (e.Data == null || onLine == null) return;
                        var line = CleanLine(e.Data);
                        if (line.Length == 0) return;
                        // stdout and stderr arrive on separate threads. A UI/log callback failure
                        // must fault the returned task rather than crash the process on that thread.
                        lock (outputGate)
                        {
                            if (outputError != null) return;
                            try { onLine(line); }
                            catch (Exception ex) { outputError = ex; }
                        }
                    };
                    p.OutputDataReceived += handler;
                    p.ErrorDataReceived += handler;
                    ct.ThrowIfCancellationRequested();
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    using (ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } }))
                    {
                        p.WaitForExit();
                    }
                    ct.ThrowIfCancellationRequested();
                    if (outputError != null) throw new InvalidOperationException("Reading process output failed.", outputError);
                    return p.ExitCode;
                }
            }, ct);
        }

        /// <summary>Runs a program and captures all of its output.</summary>
        public static async Task<(int ExitCode, string Output)> CaptureAsync(string file, string args,
            CancellationToken ct = default, Encoding encoding = null)
        {
            var sb = new StringBuilder();
            int code = await RunRawAsync(file, args, sb, ct, encoding).ConfigureAwait(false);
            return (code, sb.ToString());
        }

        static Task<int> RunRawAsync(string file, string args, StringBuilder sb, CancellationToken ct, Encoding encoding)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = encoding ?? Encoding.UTF8,
                    StandardErrorEncoding = encoding ?? Encoding.UTF8,
                };
                ct.ThrowIfCancellationRequested();
                using (var p = Process.Start(psi))
                using (ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } }))
                {
                    var errTask = p.StandardError.ReadToEndAsync();
                    sb.Append(p.StandardOutput.ReadToEnd());
                    p.WaitForExit();
                    sb.Append(errTask.Result);
                    ct.ThrowIfCancellationRequested();
                    return p.ExitCode;
                }
            }, ct);
        }

        /// <summary>Keeps only the text after the last carriage return and drops spinner / progress-bar noise.</summary>
        public static string CleanLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int cr = raw.LastIndexOf('\r');
            var line = (cr >= 0 ? raw.Substring(cr + 1) : raw).Trim();
            if (line.Length <= 2 && (line == "-" || line == "\\" || line == "|" || line == "/")) return "";
            int blocks = 0;
            foreach (char c in line) if (c == '\u2588' || c == '\u2592' || c == '\u2591') blocks++;
            if (blocks > 5) return "";
            return line;
        }
    }

    public static class CommandLine
    {
        /// <summary>Quotes one argument using the Windows CommandLineToArgvW/CRT escaping rules.</summary>
        public static string QuoteArgument(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c);
                slashes = 0;
            }
            // Trailing slashes must not escape the closing quote.
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }

        /// <summary>Splits a Windows command line (quoted or unquoted) into the executable and its arguments.</summary>
        public static void Split(string command, out string exe, out string args)
        {
            exe = ""; args = "";
            if (string.IsNullOrWhiteSpace(command)) return;
            var cmd = Environment.ExpandEnvironmentVariables(command.Trim());
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) { exe = cmd.Substring(1, end - 1); args = cmd.Substring(end + 1).Trim(); }
                else exe = cmd.Trim('"');
                return;
            }
            int idx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                int after = idx + 4;
                if (after == cmd.Length || cmd[after] == ' ' || cmd[after] == ',')
                {
                    exe = cmd.Substring(0, after);
                    args = after < cmd.Length ? cmd.Substring(after + 1).Trim() : "";
                    return;
                }
                idx = cmd.IndexOf(".exe", after, StringComparison.OrdinalIgnoreCase);
            }
            int sp = cmd.IndexOf(' ');
            exe = sp > 0 ? cmd.Substring(0, sp) : cmd;
            args = sp > 0 ? cmd.Substring(sp + 1).Trim() : "";
        }

        public static string ExePath(string command)
        {
            Split(command, out var exe, out _);
            return exe;
        }
    }

    public static class PathCheck
    {
        static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        /// <summary>
        /// True when we can be confident a rooted local path is missing. Paths on unavailable drives,
        /// network shares, or relative paths are treated as "unknown" (not missing) to avoid false positives.
        /// </summary>
        public static bool IsDefinitelyMissing(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            if (path.StartsWith(@"\\") || path.Length < 3 || path[1] != ':' || path[2] != '\\') return false;
            try
            {
                var drive = new DriveInfo(path.Substring(0, 1));
                if (!drive.IsReady || drive.DriveType == DriveType.Network || drive.DriveType == DriveType.Removable
                    || drive.DriveType == DriveType.CDRom) return false;
            }
            catch { return false; }

            if (!IsMissing(path)) return false;
            // 32-bit programs see System32 redirected to SysWOW64.
            var sys32 = Path.Combine(Win, "System32") + "\\";
            if (path.StartsWith(sys32, StringComparison.OrdinalIgnoreCase))
            {
                var alt = Path.Combine(Win, "SysWOW64") + "\\" + path.Substring(sys32.Length);
                if (!IsMissing(alt)) return false;
            }
            return true;
        }

        internal static bool IsMissing(string path)
        {
            try { File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            // File.Exists also returns false for access denied and IO failures. Those are unknown,
            // not proof that a registry entry is invalid and safe to delete.
            catch { return false; }
        }
    }
}
