using OpenSweep.Core;

internal static partial class Program
{
    static async Task CleanRunnerTests()
    {
        await Run("Clean runner rejects concurrent work without resetting its results", RejectConcurrentClean);
        await Run("Clean runner pre-cancellation does not retain the gate", CleanPreCancellation);
        await Run("Clean runner live cancellation releases the gate", CleanLiveCancellation);
        await Run("Clean runner worker failure releases the gate", CleanWorkerFailure);
        await Run("Clean runner honors cancellation at the end of the last item", CleanFinalCancellation);
    }

    static async Task SuccessfulCleanAfterFailure()
    {
        CleanerEngine.Operation = (item, token) => { token.ThrowIfCancellationRequested(); item.Bytes = 7; item.Files = 1; item.HasResult = true; };
        var item = new CleanerItem();
        await CleanRunner.AnalyzeAsync(new[] { item }, null, CancellationToken.None);
        Equal(7L, item.Bytes);
        Check(item.HasResult, "A subsequent run did not complete");
    }

    static async Task RejectConcurrentClean()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var item = new CleanerItem();
        CleanerEngine.Operation = (current, token) =>
        {
            current.Bytes = 123;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture release timed out");
            current.HasResult = true;
        };
        var first = CleanRunner.AnalyzeAsync(new[] { item }, null, CancellationToken.None);
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "First run did not reach the engine");
            Throws<InvalidOperationException>(() => CleanRunner.CleanAsync(new[] { item }, null, CancellationToken.None));
            Equal(123L, item.Bytes);
            Equal(1, item.ResetCount);
        }
        finally { release.Set(); await first; }
        await SuccessfulCleanAfterFailure();
    }

    static async Task CleanPreCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var item = new CleanerItem { Bytes = 42 };
        Throws<OperationCanceledException>(() => CleanRunner.CleanAsync(new[] { item }, null, cts.Token));
        Equal(0, item.ResetCount);
        Equal(42L, item.Bytes);
        await SuccessfulCleanAfterFailure();
    }

    static async Task CleanLiveCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        CleanerEngine.Operation = (item, token) =>
        {
            entered.Set();
            if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture cancellation timed out");
            token.ThrowIfCancellationRequested();
            throw new Exception("Cancelled engine should not complete");
        };
        var work = CleanRunner.CleanAsync(new[] { new CleanerItem() }, null, cts.Token);
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "Run did not reach the engine");
            cts.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => work);
        }
        finally { cts.Cancel(); try { await work; } catch (OperationCanceledException) { } }
        await SuccessfulCleanAfterFailure();
    }

    static async Task CleanWorkerFailure()
    {
        CleanerEngine.Operation = (item, token) => throw new IOException("fixture engine failure");
        var work = CleanRunner.CleanAsync(new[] { new CleanerItem() }, null, CancellationToken.None);
        var error = await ThrowsAsync<IOException>(() => work);
        Equal("fixture engine failure", error.Message);
        await SuccessfulCleanAfterFailure();
    }

    static async Task CleanFinalCancellation()
    {
        using var cts = new CancellationTokenSource();
        CleanerEngine.Operation = (item, token) => { item.HasResult = true; cts.Cancel(); };
        var work = CleanRunner.CleanAsync(new[] { new CleanerItem() }, null, cts.Token);
        await ThrowsAsync<OperationCanceledException>(() => work);
        await SuccessfulCleanAfterFailure();
    }
}
