using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OpenSweep.Core
{
    public static class Scheduler
    {
        public const string TaskName = "OpenSweep Auto Clean";
        static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

        /// <summary>Creates, updates or removes the scheduled cleaning task. Returns null on success, otherwise an error.</summary>
        public static async Task<string> ApplyAsync(string schedule, string time, string exePath = null)
        {
            if (schedule == "Off")
            {
                if (!await ExistsAsync().ConfigureAwait(false)) return null;
                var removed = await ProcessRunner.CaptureAsync(Schtasks, $"/Delete /TN \"{TaskName}\" /F").ConfigureAwait(false);
                Log.Write($"Schedule Off: exit {removed.ExitCode} {removed.Output.Trim()}");
                return removed.ExitCode == 0 ? null : Failure(removed.ExitCode, removed.Output);
            }
            if (schedule != "Daily" && schedule != "Weekly") return "Choose Off, Daily or Weekly.";
            if (!TimeSpan.TryParse(time, out var ts) || ts < TimeSpan.Zero || ts.TotalHours >= 24) return "Enter a time like 13:30.";
            var sc = schedule == "Weekly" ? "WEEKLY /D SUN" : "DAILY";
            var taskCommand = CommandLine.QuoteArgument(exePath ?? AppPaths.ExePath) + " /auto";
            var args = $"/Create /TN {CommandLine.QuoteArgument(TaskName)} /TR {CommandLine.QuoteArgument(taskCommand)} /SC {sc} /ST {ts.Hours:00}:{ts.Minutes:00} /F"
                       + (Elevation.IsAdmin ? " /RL HIGHEST" : "");
            var (code, output) = await ProcessRunner.CaptureAsync(Schtasks, args).ConfigureAwait(false);
            Log.Write($"Schedule {schedule} {time}: exit {code} {output.Trim()}");
            return code == 0 ? null : Failure(code, output);
        }

        static string Failure(int code, string output) =>
            string.IsNullOrWhiteSpace(output) ? $"Task Scheduler exited with code {code}." : output.Trim();

        public static Task<bool> ExistsAsync() => Task.Run(() =>
        {
            // schtasks returns the same nonzero exit status for missing tasks and access errors.
            // COM exposes a specific not-found HRESULT so a denied query cannot look like "Off".
            object service = null, folder = null, task = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                ((dynamic)service).Connect();
                folder = ((dynamic)service).GetFolder(@"\");
                task = ((dynamic)folder).GetTask(TaskName);
                return true;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { return false; }
            finally
            {
                if (task != null && Marshal.IsComObject(task)) Marshal.FinalReleaseComObject(task);
                if (folder != null && Marshal.IsComObject(folder)) Marshal.FinalReleaseComObject(folder);
                if (service != null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
            }
        });
    }

    /// <summary>Headless cleaning used by the scheduled task (OpenSweep.exe /auto).</summary>
    public static class AutoClean
    {
        public static void Run()
        {
            Log.Write("Auto clean started");
            var s = AppSettings.Current;
            var items = CleanerCatalog.Build().SelectMany(g => g.Items).Where(i => i.IsChecked && !i.IsBlockedByAdmin).ToList();
            long bytes = 0, files = 0;
            foreach (var item in items)
            {
                try
                {
                    if (item.Processes.Length > 0 && item.IsAppRunning)
                    {
                        if (!s.CloseAppsAutomatically) { Log.Write($"Auto clean: skipped {item.Id} ({item.AppName} is running)"); continue; }
                        AppCloser.Close(item.Processes);
                    }
                    CleanerEngine.Clean(item, CancellationToken.None);
                    bytes += item.Bytes;
                    files += item.Files;
                }
                catch (Exception ex) { Log.Write($"Auto clean {item.Id}: {ex.Message}"); }
            }
            Log.Write($"Auto clean finished: removed {Fmt.Count(files, "file")} ({Fmt.Bytes(bytes)})");
        }
    }

    /// <summary>Per-user install: copies the exe to %LocalAppData%\Programs, adds shortcuts and an "Installed apps" entry.</summary>
    public static class SelfInstaller
    {
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenSweep";
        // Written by OpenSweep-Setup.exe (Inno Setup). Must match AppId in installer\OpenSweep.iss.
        const string SetupKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E2B4F1A-3C7D-4E8B-9A15-0F5D2C8B7A31}_is1";
        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenSweep");
        public static string InstalledExe => Path.Combine(InstallDir, "OpenSweep.exe");
        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "OpenSweep.lnk");
        static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OpenSweep.lnk");

        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        public static bool IsInstalled => File.Exists(InstalledExe);
        public static bool IsRunningInstalledCopy =>
            string.Equals(Path.GetFullPath(AppPaths.ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        /// <summary>Path of the Setup uninstaller (unins000.exe) when OpenSweep was installed with OpenSweep-Setup.exe.</summary>
        public static string SetupUninstaller
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(SetupKey))
                    {
                        var exe = (k?.GetValue("UninstallString") as string)?.Trim().Trim('"');
                        return !string.IsNullOrEmpty(exe) && File.Exists(exe) ? exe : null;
                    }
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// Installs the application, then redirects an existing enabled cleaning schedule.
        /// Installation failures throw; a non-null result means installation succeeded but
        /// schedule migration failed. Keep the saved schedule unchanged in that case.
        /// </summary>
        public static async Task<string> InstallAsync(bool desktopShortcut)
        {
            InstallFiles(desktopShortcut);
            var settings = AppSettings.Current;
            var schedule = settings.Schedule;
            var time = settings.ScheduleTime;
            if (schedule == "Off") return null;
            try
            {
                if (!await Scheduler.ExistsAsync().ConfigureAwait(false)) return null;
                var error = await Scheduler.ApplyAsync(schedule, time, InstalledExe).ConfigureAwait(false);
                if (error != null) Log.Write("Installed copy schedule update failed: " + error);
                return error;
            }
            catch (Exception ex)
            {
                Log.Write("Installed copy schedule update failed: " + ex);
                return ex.Message;
            }
        }

        static void InstallFiles(bool desktopShortcut)
        {
            Directory.CreateDirectory(InstallDir);
            if (!IsRunningInstalledCopy) File.Copy(AppPaths.ExePath, InstalledExe, true);
            CreateShortcut(StartMenuLink, InstalledExe);
            if (desktopShortcut) CreateShortcut(DesktopLink, InstalledExe);
            if (SetupUninstaller != null)
            {
                // Setup owns the Installed apps entry; just keep its version current.
                using (var k = Registry.CurrentUser.OpenSubKey(SetupKey, true))
                    k?.SetValue("DisplayVersion", Version);
                Log.Write("Updated Setup install in " + InstallDir);
                return;
            }
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "OpenSweep");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "OpenSweep contributors");
                k.SetValue("DisplayIcon", InstalledExe + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", $"\"{InstalledExe}\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            Log.Write("Installed to " + InstallDir);
        }

        public static void Uninstall(bool removeData)
        {
            // Stop automatic cleanup before removing shortcuts or files. A denied deletion must
            // not leave an active task targeting a portable copy while the UI reports removal.
            var taskError = Scheduler.ApplyAsync("Off", null).GetAwaiter().GetResult();
            if (taskError != null) throw new InvalidOperationException("Could not remove scheduled cleaning: " + taskError);
            AppSettings.Current.Schedule = "Off";
            AppSettings.Current.Save();
            var setupUninstaller = SetupUninstaller;
            if (setupUninstaller != null) { UninstallSetup(setupUninstaller, removeData); return; }
            try { File.Delete(StartMenuLink); } catch { }
            try { File.Delete(DesktopLink); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            // A portable copy can remove the installed folder right away; the rest waits for this process to exit.
            if (!IsRunningInstalledCopy && Directory.Exists(InstallDir))
                try { Directory.Delete(InstallDir, true); } catch (Exception ex) { Log.Write("Uninstall: " + ex.Message); }
            var targets = new List<string>();
            if (Directory.Exists(InstallDir)) targets.Add(InstallDir);
            if (removeData) targets.Add(AppPaths.DataDir);
            if (targets.Count == 0) return;
            // A running exe can't delete itself (and settings are saved on exit), so a hidden PowerShell
            // waits for this process to end before removing the folders.
            bool waitForExit = IsRunningInstalledCopy || removeData;
            var script = (waitForExit ? $"Wait-Process -Id {Process.GetCurrentProcess().Id} -ErrorAction SilentlyContinue; " : "") +
                         "Start-Sleep -Milliseconds 500; " +
                         string.Join("; ", targets.Select(t => $"Remove-Item -LiteralPath '{t.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue"));
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            Process.Start(new ProcessStartInfo(ps, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
            });
            Log.Write("Uninstall scheduled for: " + string.Join(", ", targets));
        }

        /// <summary>Runs the Setup uninstaller silently (it removes the files, shortcuts, entry and scheduled task).</summary>
        static void UninstallSetup(string uninstaller, bool removeData)
        {
            const string args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
            bool runNow = !IsRunningInstalledCopy;
            if (runNow)
            {
                using (var process = Process.Start(uninstaller, args))
                {
                    if (process == null) throw new InvalidOperationException("Could not start the Setup uninstaller.");
                    if (!process.WaitForExit(120000))
                        throw new InvalidOperationException("The Setup uninstaller is still running. Wait for it to finish before trying again.");
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException($"The Setup uninstaller failed (exit code {process.ExitCode}). Your data was kept.");
                }
                Log.Write("Setup uninstaller finished");
                if (!removeData) return;
            }
            // The installed exe (and settings saved on exit) are in use, so wait for this process to end first.
            var script = $"Wait-Process -Id {Process.GetCurrentProcess().Id} -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500" +
                         (runNow ? "" : $"; $result = Start-Process -Wait -PassThru -ErrorAction Stop -FilePath '{uninstaller.Replace("'", "''")}' -ArgumentList '{args}'; if ($result.ExitCode -ne 0) {{ exit $result.ExitCode }}") +
                         (removeData ? $"; Remove-Item -LiteralPath '{AppPaths.DataDir.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue" : "");
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            Process.Start(new ProcessStartInfo(ps, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
            });
            Log.Write("Setup uninstall scheduled after exit");
        }

        static void CreateShortcut(string lnk, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = target + ",0";
            sc.Description = "Clean, optimize and update your PC";
            sc.Save();
        }
    }
}
