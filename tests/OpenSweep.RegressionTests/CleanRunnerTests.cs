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
        await Run("Clean runner persists partial measured results after a worker failure", CleanHistoryFailure);
        await Run("Clean runner cancellation preserves completed work and skipped items", CleanHistoryCancellation);
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

    static async Task CleanHistoryFailure()
    {
        var store = new MaintenanceHistoryStore(Fixture("clean-history-failure"));
        using var operation = new MaintenanceOperation(store, "Cleanup", "Regression fixture");
        var completed = new CleanerItem { Name = "Completed item" };
        var failing = new CleanerItem { Name = "Partial item" };
        var untouched = new CleanerItem { Name = "Untouched item" };
        CleanerEngine.Operation = (item, token) =>
        {
            Check(!ReferenceEquals(item, untouched), "Cleanup continued after its engine failed");
            item.Bytes = ReferenceEquals(item, completed) ? 100 : 20;
            item.Files = 1; item.HasResult = true;
            if (ReferenceEquals(item, failing)) { item.HadError = true; item.Failures = 1; throw new IOException("Fixture partial failure"); }
        };
        await ThrowsAsync<IOException>(() => CleanRunner.CleanAsync(new[] { completed, failing, untouched }, null, CancellationToken.None, operation));
        var record = store.Load().Single(); Equal(MaintenanceOutcome.Partial, record.Outcome);
        Equal(120L, record.DeletedBytes.Value); Equal(2L, record.Files.Value);
        Equal(MaintenanceOutcome.Completed, record.Items.Single(i => i.Id == completed.Id).Outcome);
        Equal(MaintenanceOutcome.Partial, record.Items.Single(i => i.Id == failing.Id).Outcome);
        var skippedItem = record.Items.Single(i => i.Id == untouched.Id);
        Equal(1L, skippedItem.Skipped); Check(skippedItem.Bytes == null, "Unexecuted item acquired a deleted-byte total");
        await SuccessfulCleanAfterFailure();
    }

    static async Task CleanHistoryCancellation()
    {
        var store = new MaintenanceHistoryStore(Fixture("clean-history-cancellation"));
        using var operation = new MaintenanceOperation(store, "Cleanup", "Regression fixture");
        using var cts = new CancellationTokenSource();
        var completed = new CleanerItem { Name = "Completed item" };
        var untouched = new CleanerItem { Name = "Untouched item" };
        CleanerEngine.Operation = (item, token) =>
        {
            Check(ReferenceEquals(item, completed), "Cancelled cleanup admitted another item");
            item.Bytes = 64; item.Files = 1; item.HasResult = true; cts.Cancel();
        };
        await ThrowsAsync<OperationCanceledException>(() => CleanRunner.CleanAsync(new[] { completed, untouched }, null, cts.Token, operation));
        var record = store.Load().Single(); Equal(MaintenanceOutcome.Cancelled, record.Outcome);
        Equal(64L, record.DeletedBytes.Value); Equal(1L, record.Files.Value);
        Equal(MaintenanceOutcome.Completed, record.Items.Single(i => i.Id == completed.Id).Outcome);
        Equal(MaintenanceOutcome.Cancelled, record.Items.Single(i => i.Id == untouched.Id).Outcome);
        Equal(1L, record.Skipped);
        await SuccessfulCleanAfterFailure();
    }
}
