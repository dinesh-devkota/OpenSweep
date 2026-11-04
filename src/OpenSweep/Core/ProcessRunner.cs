using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSweep.Core
{
    /// <summary>The command exited, but inherited output handles did not close in time.</summary>
    public sealed class ProcessOutputIncompleteException : IOException
    {
        // An observed root exit code does not prove that an inheriting installer has finished.
        public int? ExitCode { get; }

        public ProcessOutputIncompleteException(int? exitCode)
            : base("The command exited, but an output stream remained open. A child process may still be running; the operation's final outcome is unknown.")
        {
            ExitCode = exitCode;
        }
    }

    public static class ProcessRunner
    {
        // A descendant may inherit the pipe handles even after the command has exited.
        // Never use parameterless WaitForExit() or wait indefinitely for those handles.
        const int OutputDrainMilliseconds = 1500;
        const int CancellationExitMilliseconds = 2000;

        /// <summary>
        /// Runs a console program hidden and streams cleaned-up output lines. After the
        /// process exits, logging drains for a bounded time. An inherited output handle that
        /// remains open raises ProcessOutputIncompleteException instead of claiming success.
        /// Cancellation terminates only this process, never its descendants. Do not pass an
        /// installer queue's stop token here: let the current installer finish instead.
        /// </summary>
        public static async Task<int> RunAsync(string file, string args, Action<string> onLine = null,
            CancellationToken ct = default, Encoding encoding = null)
        {
            var result = await ExecuteAsync(file, args, onLine, false, ct, encoding).ConfigureAwait(false);
            return result.ExitCode;
        }

        /// <summary>
        /// Captures stdout followed by stderr without changing their contents. Incomplete
        /// output after process exit is an error, so a parser cannot treat truncated output
        /// as a successful search. Cancellation terminates only the command itself.
        /// </summary>
        public static Task<(int ExitCode, string Output)> CaptureAsync(string file, string args,
            CancellationToken ct = default, Encoding encoding = null) =>
            ExecuteAsync(file, args, null, true, ct, encoding);

        static Task<(int ExitCode, string Output)> ExecuteAsync(string file, string args,
            Action<string> onLine, bool capture, CancellationToken ct, Encoding encoding)
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
                using (var process = new Process { StartInfo = psi })
                {
                    var outputGate = new object();
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    Exception outputError = null;
                    bool acceptingOutput = true;
                    int closing = 0;
                    StreamReader outputReader = null, errorReader = null;

                    void Emit(string raw)
                    {
                        if (onLine == null || outputError != null) return;
                        var line = CleanLine(raw);
                        if (line.Length == 0) return;
                        // Serialize stdout/stderr callbacks and preserve callback exceptions
                        // on the returned task rather than throwing on an IO completion thread.
                        try { onLine(line); }
                        catch (Exception ex) { outputError = ex; }
                    }

                    async Task ReadAsync(StreamReader reader, StringBuilder destination)
                    {
                        var buffer = new char[4096];
                        var pendingLine = new StringBuilder();
                        try
                        {
                            while (true)
                            {
                                int count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                                lock (outputGate)
                                {
                                    if (!acceptingOutput) return;
                                    if (count == 0)
                                    {
                                        if (!capture && pendingLine.Length > 0) Emit(pendingLine.ToString());
                                        return;
                                    }
                                    if (capture) destination.Append(buffer, 0, count);
                                    else
                                    {
                                        for (int i = 0; i < count; i++)
                                        {
                                            if (buffer[i] == '\r' || buffer[i] == '\n')
                                            {
                                                Emit(pendingLine.ToString());
                                                pendingLine.Clear();
                                            }
                                            else pendingLine.Append(buffer[i]);
                                        }
                                    }
                                }
                            }
                        }
                        catch (ObjectDisposedException) when (Volatile.Read(ref closing) != 0) { }
                        catch (IOException) when (Volatile.Read(ref closing) != 0) { }
                    }

                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!process.Start()) throw new InvalidOperationException("The command could not be started.");
                        outputReader = process.StandardOutput;
                        errorReader = process.StandardError;
                        var reads = Task.WhenAll(ReadAsync(outputReader, stdout), ReadAsync(errorReader, stderr));
                        // If a pipe stays open beyond the drain limit, its later completion
                        // is deliberately not awaited. Always observe any later read fault.
                        _ = reads.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);

                        using (ct.Register(() => TryStop(process)))
                        {
                            Stopwatch cancellationWait = null;
                            while (!process.WaitForExit(100))
                            {
                                if (!ct.IsCancellationRequested) continue;
                                if (cancellationWait == null)
                                {
                                    TryStop(process);
                                    cancellationWait = Stopwatch.StartNew();
                                }
                                if (cancellationWait.ElapsedMilliseconds >= CancellationExitMilliseconds)
                                    throw new OperationCanceledException("Cancellation was requested, but the command did not stop within the wait limit.", ct);
                            }
                            ct.ThrowIfCancellationRequested();

                            var finished = Task.WhenAny(reads, Task.Delay(OutputDrainMilliseconds, ct)).GetAwaiter().GetResult();
                            ct.ThrowIfCancellationRequested();
                            if (finished == reads) reads.GetAwaiter().GetResult();
                            else
                                throw new ProcessOutputIncompleteException(process.ExitCode);

                            lock (outputGate)
                            {
                                acceptingOutput = false;
                                if (outputError != null)
                                    throw new InvalidOperationException("Reading process output failed.", outputError);
                                return (process.ExitCode, capture ? stdout.ToString() + stderr.ToString() : "");
                            }
                        }
                    }
                    finally
                    {
                        lock (outputGate) acceptingOutput = false;
                        Interlocked.Exchange(ref closing, 1);
                        // Closing our read ends releases pending reads without touching
                        // any child process that inherited the write ends.
                        try { outputReader?.Dispose(); } catch (IOException) { }
                        try { errorReader?.Dispose(); } catch (IOException) { }
                    }
                }
            }, ct);
        }

        static void TryStop(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
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
            if (string.IsNullOrWhiteSpace(command) || command.IndexOf('\0') >= 0) return;
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
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
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
