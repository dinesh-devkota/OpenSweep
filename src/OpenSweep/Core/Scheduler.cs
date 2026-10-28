using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
                await ProcessRunner.CaptureAsync(Schtasks, $"/Delete /TN \"{TaskName}\" /F");
                return null;
            }
            if (!TimeSpan.TryParse(time, out var ts) || ts < TimeSpan.Zero || ts.TotalHours >= 24) return "Enter a time like 13:30.";
            var sc = schedule == "Weekly" ? "WEEKLY /D SUN" : "DAILY";
            var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath ?? AppPaths.ExePath}\\\" /auto\" /SC {sc} /ST {ts.Hours:00}:{ts.Minutes:00} /F"
                       + (Elevation.IsAdmin ? " /RL HIGHEST" : "");
            var (code, output) = await ProcessRunner.CaptureAsync(Schtasks, args);
            Log.Write($"Schedule {schedule} {time}: exit {code} {output.Trim()}");
            return code == 0 ? null : output.Trim();
        }

        public static async Task<bool> ExistsAsync() =>
            (await ProcessRunner.CaptureAsync(Schtasks, $"/Query /TN \"{TaskName}\"")).ExitCode == 0;
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
        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenSweep");
        public static string InstalledExe => Path.Combine(InstallDir, "OpenSweep.exe");
        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "OpenSweep.lnk");
        static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OpenSweep.lnk");

        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        public static bool IsInstalled => File.Exists(InstalledExe);
        public static bool IsRunningInstalledCopy =>
            string.Equals(Path.GetFullPath(AppPaths.ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        public static void Install(bool desktopShortcut)
        {
            Directory.CreateDirectory(InstallDir);
            if (!IsRunningInstalledCopy) File.Copy(AppPaths.ExePath, InstalledExe, true);
            CreateShortcut(StartMenuLink, InstalledExe);
            if (desktopShortcut) CreateShortcut(DesktopLink, InstalledExe);
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
            try { File.Delete(StartMenuLink); } catch { }
            try { File.Delete(DesktopLink); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { ProcessRunner.CaptureAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Delete /TN \"{Scheduler.TaskName}\" /F").Wait(); } catch { }
            AppSettings.Current.Schedule = "Off";
            AppSettings.Current.Save();
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
