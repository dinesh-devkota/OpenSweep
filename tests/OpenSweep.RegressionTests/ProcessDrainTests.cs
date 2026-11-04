using System.Diagnostics;
using OpenSweep.Core;

internal static partial class Program
{
    static async Task ProcessDrainTests()
    {
        await Run("Streaming process output has bounded inherited-pipe draining", () => HeldPipe(false, false));
        await Run("Captured process output rejects an incomplete inherited pipe", () => HeldPipe(true, false));
        await Run("Streaming process cancellation does not wait for descendant pipes", () => HeldPipe(false, true));
        await Run("Captured process cancellation does not wait for descendant pipes", () => HeldPipe(true, true));
    }

    static async Task<int> PipeChild(string mode, string marker)
    {
        if (mode == "pipe-descendant")
        {
            // The inherited handles remain open, but this fixture cannot outlive six seconds.
            // No console writes after the parent may close its readers (avoids broken-pipe noise).
            File.WriteAllText(marker + ".descendant", "ready");
            await Task.Delay(TimeSpan.FromSeconds(6));
            File.WriteAllText(marker + ".finished", "done");
            return 0;
        }
        var command = ChildCommand("pipe-descendant", marker);
        using var descendant = Process.Start(new ProcessStartInfo(command.File, command.Args)
        {
            UseShellExecute = false,
            CreateNoWindow = true
            // Deliberately inherit both console pipes; do not redirect them to separate readers.
        });
        if (descendant == null) throw new Exception("Could not start the pipe fixture descendant");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(marker + ".descendant") && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        if (!File.Exists(marker + ".descendant")) throw new Exception("Pipe fixture descendant did not initialize");
        Console.Out.WriteLine("root output before exit");
        Console.Error.WriteLine("root error before exit");
        File.WriteAllText(marker + ".root-ready", "ready");
        if (mode == "pipe-cancel") await Task.Delay(TimeSpan.FromSeconds(12));
        return 23;
    }

    static async Task HeldPipe(bool capture, bool cancel)
    {
        string marker = Path.Combine(Fixture("inherited-pipe"), "child");
        var command = ChildCommand(cancel ? "pipe-cancel" : "pipe-exit", marker);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<int> streaming = capture ? null : ProcessRunner.RunAsync(command.File, command.Args, ct: cts.Token);
        Task<(int ExitCode, string Output)> captured = capture ? ProcessRunner.CaptureAsync(command.File, command.Args, cts.Token) : null;
        Task work = capture ? captured : streaming;
        try
        {
            var startup = Stopwatch.StartNew();
            while (!File.Exists(marker + ".root-ready") && startup.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
            Check(File.Exists(marker + ".root-ready"), "Parent process did not reach the inherited-pipe fixture");
            var elapsed = Stopwatch.StartNew();
            if (cancel)
            {
                cts.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(3.5)));
            }
            else
            {
                var error = await ThrowsAsync<ProcessOutputIncompleteException>(() => work.WaitAsync(TimeSpan.FromSeconds(3.5)));
                Equal(23, error.ExitCode.Value);
            }
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(3.5), "Process completion waited for inherited EOF");
            Check(!File.Exists(marker + ".finished"), "Fixture descendant exited before the bounded-drain assertion");
        }
        finally
        {
            cts.Cancel();
            try { await work.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            // The runner intentionally does not kill descendants. Wait for our short-lived
            // fixture to finish before removing its directory; no orphan is left behind.
            if (File.Exists(marker + ".descendant"))
            {
                var cleanup = Stopwatch.StartNew();
                while (!File.Exists(marker + ".finished") && cleanup.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
                Check(File.Exists(marker + ".finished"), "Fixture descendant exceeded its bounded lifetime");
            }
        }
    }
}
