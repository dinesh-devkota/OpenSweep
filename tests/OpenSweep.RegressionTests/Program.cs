using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Xml.Serialization;
using OpenSweep.Core;

internal static partial class Program
{
    static string root;
    static int passed, failed, skipped;

    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--child") return await Child(args);
        root = Path.Combine(Path.GetTempPath(), "opensweep-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppPaths.DataDir = Fixture("settings-initial");
        try
        {
            await Run("Windows argument quoting vectors", QuoteVectors);
            await Run("Missing path checks distinguish unknown paths", () => Sync(() =>
            {
                var directory = Fixture("path-state");
                var file = Path.Combine(directory, "existing.txt");
                File.WriteAllText(file, "present");
                Check(!PathCheck.IsMissing(file), "An existing file was treated as missing");
                Check(!PathCheck.IsMissing(directory), "An existing directory was treated as missing");
                Check(PathCheck.IsMissing(Path.Combine(directory, "missing.txt")), "Missing file was not identified");
                Check(!PathCheck.IsMissing("invalid\0path"), "Invalid paths must remain unknown, not missing");
            }));
            await Run("Process RunAsync honors pre-cancellation", () => PreCancelled(false));
            await Run("Process CaptureAsync honors pre-cancellation", () => PreCancelled(true));
            await Run("Process RunAsync drains stdout and stderr", StreamBoth);
            await Run("Process CaptureAsync drains stdout and stderr", CaptureBoth);
            await Run("Process callback exceptions fault the task", CallbackFailure);
            await Run("Process RunAsync live cancellation", () => LiveCancellation(false));
            await Run("Process CaptureAsync live cancellation", () => LiveCancellation(true));
            await Run("Settings serialization failure preserves last valid file", SettingsFailure);
            await Run("Settings consecutive saves replace valid XML", SettingsConsecutive);
            await Run("Settings concurrent saves remain valid XML", SettingsConcurrent);
            await Run("Settings exclusion path boundaries and normalization", Exclusions);
            await Run("Registry backup rejects missing exporter file", () => BackupFailure("missing", (key, path) => 0));
            await Run("Registry backup rejects header-only exporter file", () => BackupFailure("header", (key, path) => Export(path, "Windows Registry Editor Version 5.00\r\n")));
            await Run("Registry backup rejects wrong key", () => BackupFailure("wrong-key", (key, path) => Export(path, RegText("HKCU\\Software\\Other"))));
            await Run("Registry backup rejects nonzero exporter exit", () => BackupFailure("exit", (key, path) => { Export(path, RegText(key)); return 5; }));
            await Run("Registry backup rejects second-key failure atomically", BackupPartialFailure);
            await Run("Registry backup merges Unicode and deduplicates keys", BackupSuccess);
            await Run("Registry backup uses unique repeated filenames", BackupUnique);
            await DuplicateTests();
            await CleanRunnerTests();
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception ex) { Console.Error.WriteLine("Fixture cleanup failed: " + ex.Message); failed++; }
        }
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed, {skipped} skipped");
        return failed == 0 ? 0 : 1;
    }

    static async Task Run(string name, Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(25)); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
    }

    static Task Sync(Action action) { action(); return Task.CompletedTask; }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected '{expected}', got '{actual}'");
    static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T ex) { return ex; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T ex) { return ex; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    static string Fixture(string label)
    {
        string path = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static Task QuoteVectors() => Sync(() =>
    {
        var vectors = new (string Input, string Expected)[]
        {
            ("", "\"\""), ("plain", "\"plain\""), ("two words", "\"two words\""),
            ("a\"b", "\"a\\\"b\""), ("a\tb", "\"a\tb\""),
            (@"C:\space dir\", "\"C:\\space dir\\\\\""),
            ("back\\\"quote", "\"back" + new string('\\', 3) + "\"quote\""),
            (@"a\\b", "\"a\\\\b\"")
        };
        foreach (var vector in vectors) Equal(vector.Expected, CommandLine.QuoteArgument(vector.Input));
        Throws<ArgumentNullException>(() => CommandLine.QuoteArgument(null));
    });

    static (string File, string Args) ChildCommand(string mode, string path = null)
    {
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(host)) host = Environment.ProcessPath;
        string command = CommandLine.QuoteArgument(Assembly.GetExecutingAssembly().Location) + " --child " + mode;
        if (path != null) command += " " + CommandLine.QuoteArgument(path);
        return (host, command);
    }

    static async Task<int> Child(string[] args)
    {
        if (args[1] == "marker") { File.WriteAllText(args[2], "started"); return 0; }
        if (args[1] == "delay")
        {
            File.WriteAllText(args[2], "started");
            await Task.Delay(TimeSpan.FromSeconds(30));
            File.WriteAllText(args[2] + ".finished", "unexpected completion");
            return 0;
        }
        if (args[1] == "output")
        {
            for (int i = 0; i < 4096; i++)
            {
                Console.Out.WriteLine("OUT-" + i.ToString("D4") + "-" + new string('o', 80));
                Console.Error.WriteLine("ERR-" + i.ToString("D4") + "-" + new string('e', 80));
            }
            return 23;
        }
        Console.WriteLine("callback-fixture");
        return 0;
    }

    static async Task PreCancelled(bool capture)
    {
        var path = Path.Combine(Fixture("pre-cancel"), "started.txt");
        var command = ChildCommand("marker", path);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () =>
        {
            if (capture) await ProcessRunner.CaptureAsync(command.File, command.Args, cts.Token);
            else await ProcessRunner.RunAsync(command.File, command.Args, ct: cts.Token);
        });
        Check(!File.Exists(path), "A pre-cancelled operation executed the child marker");
    }

    static async Task StreamBoth()
    {
        var command = ChildCommand("output");
        var lines = new ConcurrentBag<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Equal(23, await ProcessRunner.RunAsync(command.File, command.Args, lines.Add, cts.Token));
        Equal(4096, lines.Count(line => line.StartsWith("OUT-", StringComparison.Ordinal)));
        Equal(4096, lines.Count(line => line.StartsWith("ERR-", StringComparison.Ordinal)));
        Check(lines.Any(line => line.StartsWith("OUT-4095-")) && lines.Any(line => line.StartsWith("ERR-4095-")), "Last output lines were lost");
    }

    static async Task CaptureBoth()
    {
        var command = ChildCommand("output");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await ProcessRunner.CaptureAsync(command.File, command.Args, cts.Token);
        Equal(23, result.ExitCode);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Equal(4096, lines.Count(line => line.StartsWith("OUT-", StringComparison.Ordinal)));
        Equal(4096, lines.Count(line => line.StartsWith("ERR-", StringComparison.Ordinal)));
    }

    static async Task CallbackFailure()
    {
        var command = ChildCommand("callback");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ex = await ThrowsAsync<InvalidOperationException>(() => ProcessRunner.RunAsync(command.File, command.Args,
            line => throw new ApplicationException("fixture callback failure"), cts.Token));
        Check(ex.InnerException is ApplicationException, "The callback exception was not preserved");
        Equal("fixture callback failure", ex.InnerException.Message);
    }

    static async Task LiveCancellation(bool capture)
    {
        var path = Path.Combine(Fixture("live-cancel"), "started.txt");
        var command = ChildCommand("delay", path);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task task = capture ? ProcessRunner.CaptureAsync(command.File, command.Args, cts.Token)
            : ProcessRunner.RunAsync(command.File, command.Args, ct: cts.Token);
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(path) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Check(File.Exists(path), "Child did not start within the deadline");
            cts.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Check(!File.Exists(path + ".finished"), "Cancelled child unexpectedly completed its work");
        }
        finally { cts.Cancel(); try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
    }

    static AppSettings ReadSettings()
    {
        using var stream = File.OpenRead(AppPaths.SettingsFile);
        return (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream);
    }

    static Task SettingsFailure() => Sync(() =>
    {
        AppPaths.DataDir = Fixture("settings-failure");
        var settings = new AppSettings { Schedule = "Daily", Theme = "Dark" };
        settings.Save();
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        settings.Schedule = "broken\0XML";
        settings.Save();
        Check(before.SequenceEqual(File.ReadAllBytes(AppPaths.SettingsFile)), "Failed serialization overwrote valid settings");
        Equal("Daily", ReadSettings().Schedule);
        Check(!Directory.EnumerateFiles(AppPaths.DataDir, "*.tmp").Any(), "Failed serialization left temporary files");
        Check(Log.Entries.Any(message => message.StartsWith("Settings save failed:")), "Serialization failure was not logged");
    });

    static Task SettingsConsecutive() => Sync(() =>
    {
        AppPaths.DataDir = Fixture("settings-consecutive");
        var settings = new AppSettings { Schedule = "Daily" };
        settings.Save(); Equal("Daily", ReadSettings().Schedule);
        settings.Schedule = "Weekly"; settings.Save(); Equal("Weekly", ReadSettings().Schedule);
        Check(!Directory.EnumerateFiles(AppPaths.DataDir, "*.tmp").Any(), "Consecutive saves left temporary files");
    });

    static async Task SettingsConcurrent()
    {
        AppPaths.DataDir = Fixture("settings-concurrent");
        while (Log.Entries.TryDequeue(out _)) { }
        var schedules = Enumerable.Range(0, 20).Select(i => "Schedule-" + i).ToArray();
        await Task.WhenAll(schedules.Select(value => Task.Run(() => new AppSettings { Schedule = value }.Save())));
        Check(schedules.Contains(ReadSettings().Schedule), "Concurrent saves produced invalid or unrelated XML");
        Check(!Log.Entries.Any(), "Concurrent saves logged a failure");
        Check(!Directory.EnumerateFiles(AppPaths.DataDir, "*.tmp").Any(), "Concurrent saves left temporary files");
    }

    static Task Exclusions() => Sync(() =>
    {
        string folder = Path.Combine(Fixture("exclusions"), "protected");
        var settings = new AppSettings { ExcludedPaths = new List<string> { folder + Path.DirectorySeparatorChar } };
        Check(settings.IsExcluded(folder), "Exact excluded directory was not excluded");
        Check(settings.IsExcluded(Path.Combine(folder, "child.txt")), "Excluded descendant was not excluded");
        Check(!settings.IsExcluded(folder + "-sibling/file.txt"), "Sibling sharing a prefix was incorrectly excluded");
        Check(settings.IsExcluded(Path.Combine(folder, "child", "..", "data.txt")), "Canonical descendant was not excluded");
        Check(!settings.IsExcluded(Path.Combine(folder, "..", "outside.txt")), "Traversal outside exclusion was misclassified");
        Check(settings.IsExcluded(folder + Path.DirectorySeparatorChar), "Trailing separator was mishandled");
        settings.ExcludedPaths = null;
        Check(!settings.IsExcluded(folder), "Null exclusion collection should behave as empty");
        Check(settings.IsExcluded(null) && settings.IsExcluded(" ") && settings.IsExcluded("invalid\0path"), "Malformed targets must fail closed");
    });

    static int Export(string path, string text) { File.WriteAllText(path, text, Encoding.Unicode); return 0; }
    static string RegText(string key) => "Windows Registry Editor Version 5.00\r\n\r\n[" +
        key.Replace("HKCU\\", "HKEY_CURRENT_USER\\").Replace("HKLM\\", "HKEY_LOCAL_MACHINE\\") + "]\r\n\"Fixture\"=\"café 日本語\"\r\n";
    static void NoBackups(string directory) => Check(!Directory.EnumerateFiles(directory).Any(), "An incomplete backup was published");

    static Task BackupFailure(string label, Func<string, string, int> exporter) => Sync(() =>
    {
        var directory = Fixture("backup-" + label);
        var temps = new List<string>();
        Throws<IOException>(() => RegistryBackupWriter.Write(new[] { "HKCU\\Software\\Fixture" }, directory, "test", (key, path) => { temps.Add(path); return exporter(key, path); }));
        NoBackups(directory);
        Check(temps.All(path => !File.Exists(path)), "Exporter temporary files were not cleaned");
    });

    static Task BackupPartialFailure() => Sync(() =>
    {
        var directory = Fixture("backup-partial"); var temps = new List<string>(); int calls = 0;
        Throws<IOException>(() => RegistryBackupWriter.Write(new[] { "HKCU\\Software\\One", "HKCU\\Software\\Two" }, directory, "test", (key, path) =>
        { temps.Add(path); calls++; if (calls == 2) return 3; return Export(path, RegText(key)); }));
        Equal(2, calls); NoBackups(directory);
        Check(temps.All(path => !File.Exists(path)), "Partial failure left temporary files");
    });

    static Task BackupSuccess() => Sync(() =>
    {
        var directory = Fixture("backup-success"); var temps = new List<string>();
        var file = RegistryBackupWriter.Write(new[] { "HKCU\\Software\\One", "HKLM\\Software\\Two", "HKCU\\Software\\One" }, directory, "test", (key, path) =>
        { temps.Add(path); return Export(path, RegText(key)); });
        Equal(2, temps.Count);
        Check(temps.All(path => !File.Exists(path)), "Successful exports left temporary files");
        var bytes = File.ReadAllBytes(file);
        Check(bytes[0] == 0xff && bytes[1] == 0xfe, "Merged backup is not UTF-16LE with BOM");
        string text = File.ReadAllText(file, Encoding.Unicode);
        Check(text.Contains("[HKEY_CURRENT_USER\\Software\\One]") && text.Contains("[HKEY_LOCAL_MACHINE\\Software\\Two]"), "Merged backup omitted keys");
        Check(text.Contains("café 日本語"), "Unicode registry value was corrupted");
        Equal(1, text.Split("Windows Registry Editor Version 5.00", StringSplitOptions.None).Length - 1);
    });

    static Task BackupUnique() => Sync(() =>
    {
        var directory = Fixture("backup-unique");
        string Write() => RegistryBackupWriter.Write(new[] { "HKCU\\Software\\Fixture" }, directory, "test", (key, path) => Export(path, RegText(key)));
        var first = Write(); var original = File.ReadAllBytes(first); var second = Write();
        Check(first != second, "Repeated backup calls collided");
        Check(original.SequenceEqual(File.ReadAllBytes(first)), "Second backup changed the previous backup");
        Equal(2, Directory.EnumerateFiles(directory, "*.reg").Count());
    });
}
