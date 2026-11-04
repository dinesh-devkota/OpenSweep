using OpenSweep.Core;

internal static partial class Program
{
    static async Task UpdateFlowTests()
    {
        await Run("Updater restart state follows exit codes, not installer text", UpdateRestartCodes);
        await Run("Updater retry selection excludes successes cancellations and restart blocks", UpdateRetryPolicy);
        await Run("Windows update results retain partial failure and restart information", WindowsUpdateResults);
        await Run("Stopping an update queue lets the active installer finish", UpdateQueueStopRemaining);
        await Run("Pre-cancelled update queues execute no installers", UpdateQueuePreCancelled);
        await Run("Installer cancellation stops later queue items", UpdateQueueInstallerCancelled);
        await Run("Update queue records failures and continues unrelated items", UpdateQueueFailure);
        await Run("Unconfirmed installer completion stops the queue without enabling retry", UpdateQueueUnknown);
        await Run("Missing and nonfinal installer results stop the queue as unknown", UpdateQueueNonfinal);
    }

    static Task UpdateRestartCodes() => Sync(() =>
    {
        var success = UpdateResult.FromWingetExitCode(0, "No restart or reboot is required.");
        Equal(UpdateOutcome.Succeeded, success.Outcome); Check(!success.RestartRequired, "Installer wording created a false restart requirement");
        foreach (int code in new[] { 3010, 1641, unchecked((int)0x8A150109), unchecked((int)0x8A15010B) })
        {
            var result = UpdateResult.FromWingetExitCode(code);
            Equal(UpdateOutcome.Succeeded, result.Outcome); Check(result.RestartRequired, "Known restart-success code was lost"); Equal(code, result.ExitCode.Value);
        }
        var blocked = UpdateResult.FromWingetExitCode(unchecked((int)0x8A15010A));
        Equal(UpdateOutcome.Failed, blocked.Outcome); Check(blocked.RestartRequired && !blocked.CanRetry, "Restart-blocked installer remained retryable");
        Equal(UpdateOutcome.Cancelled, UpdateResult.FromWingetExitCode(1602).Outcome);
        Equal(UpdateOutcome.Cancelled, UpdateResult.FromWingetExitCode(unchecked((int)0x8A15010C)).Outcome);
        Equal(UpdateOutcome.Skipped, UpdateResult.FromWingetExitCode(unchecked((int)0x8A15002B)).Outcome);
        Equal(UpdateOutcome.Failed, UpdateResult.FromWingetExitCode(5).Outcome);
    });

    static Task UpdateRetryPolicy() => Sync(() =>
    {
        Check(new UpdateResult(UpdateOutcome.Failed, "failed").CanRetry, "Failed update was not retryable");
        Check(!new UpdateResult(UpdateOutcome.Failed, "restart first", true).CanRetry, "Restart requirement was ignored for retry");
        foreach (var outcome in new[] { UpdateOutcome.Pending, UpdateOutcome.Running, UpdateOutcome.Succeeded, UpdateOutcome.Cancelled, UpdateOutcome.Skipped, UpdateOutcome.Unknown })
            Check(!new UpdateResult(outcome, "fixture").CanRetry, "Non-failed update entered failed-only retry: " + outcome);
    });

    static Task WindowsUpdateResults() => Sync(() =>
    {
        Equal(UpdateOutcome.Succeeded, UpdateResult.FromWindowsUpdate(2, 0, false).Outcome);
        var partial = UpdateResult.FromWindowsUpdate(3, unchecked((int)0x80004005), true);
        Equal(UpdateOutcome.Failed, partial.Outcome); Check(partial.RestartRequired && !partial.CanRetry, "Partial Windows result lost restart/block state");
        Equal(unchecked((int)0x80004005), partial.ExitCode.Value);
        var cancelled = UpdateResult.FromWindowsUpdate(5, 0, true);
        Equal(UpdateOutcome.Cancelled, cancelled.Outcome); Check(cancelled.RestartRequired, "Cancelled update lost an already-required restart");
    });

    static async Task UpdateQueueStopRemaining()
    {
        using var cts = new CancellationTokenSource();
        var release = new TaskCompletionSource<UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0; var results = new Dictionary<int, UpdateResult>();
        var work = UpdateQueue.RunAsync(new[] { 1, 2, 3 }, async (item, index) =>
        {
            calls++; Equal(1, item); Equal(0, index); entered.TrySetResult(true); return await release.Task;
        }, (item, result) => results.Add(item, result), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();
        Check(!work.IsCompleted, "Queue cancellation interrupted a still-running installer");
        release.SetResult(UpdateResult.FromWingetExitCode(3010));
        var batch = await work;
        Equal(1, calls); Equal(1, batch.Succeeded); Equal(0, batch.Failed); Equal(0, batch.Cancelled); Equal(2, batch.Skipped); Equal(3, batch.Total);
        Check(batch.RestartRequired, "Completed installer's restart requirement was dropped");
        Equal(UpdateOutcome.Succeeded, results[1].Outcome); Equal(UpdateOutcome.Skipped, results[2].Outcome); Equal(UpdateOutcome.Skipped, results[3].Outcome);
    }

    static async Task UpdateQueuePreCancelled()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        int calls = 0; var results = new List<UpdateResult>();
        var batch = await UpdateQueue.RunAsync(new[] { "one", "two" }, (item, index) =>
        { calls++; return Task.FromResult(UpdateResult.FromWingetExitCode(0)); }, (item, result) => results.Add(result), cts.Token);
        Equal(0, calls); Equal(2, batch.Skipped); Check(results.All(result => result.Outcome == UpdateOutcome.Skipped), "Unstarted updates were marked installed or failed");
    }

    static async Task UpdateQueueInstallerCancelled()
    {
        int calls = 0;
        var batch = await UpdateQueue.RunAsync(new[] { 1, 2, 3 }, (item, index) =>
        { calls++; throw new OperationCanceledException("fixture installer cancellation"); }, null, CancellationToken.None);
        Equal(1, calls); Equal(1, batch.Cancelled); Equal(2, batch.Skipped); Equal(0, batch.Succeeded);
    }

    static async Task UpdateQueueFailure()
    {
        var results = new Dictionary<int, UpdateResult>();
        var batch = await UpdateQueue.RunAsync(new[] { 1, 2, 3 }, (item, index) =>
        {
            if (item == 1) throw new IOException("fixture installer failure");
            return Task.FromResult(UpdateResult.FromWingetExitCode(item == 2 ? 5 : 0));
        }, (item, result) => results.Add(item, result), CancellationToken.None);
        Equal(2, batch.Failed); Equal(1, batch.Succeeded); Equal(3, batch.Total);
        Check(results[1].CanRetry && results[2].CanRetry && !results[3].CanRetry, "Failed-only retry selection did not match recorded results");
    }
    static async Task UpdateQueueUnknown()
    {
        foreach (int exitCode in new[] { 0, 3010 })
        {
            int calls = 0; var results = new Dictionary<int, UpdateResult>();
            var batch = await UpdateQueue.RunAsync(new[] { 1, 2 }, (item, index) =>
            { calls++; throw new ProcessOutputIncompleteException(exitCode); }, (item, result) => results.Add(item, result), CancellationToken.None);
            Equal(1, calls); Equal(1, batch.Unknown); Equal(1, batch.Skipped); Equal(0, batch.Succeeded); Equal(0, batch.Failed);
            Equal(UpdateOutcome.Unknown, results[1].Outcome); Equal(exitCode, results[1].ExitCode.Value);
            Equal(exitCode == 3010, results[1].RestartRequired); Equal(exitCode == 3010, batch.RestartRequired);
            Check(!results[1].CanRetry, "Unknown installer completion became retryable");
        }
    }

    static async Task UpdateQueueNonfinal()
    {
        foreach (var unfinished in new[] { null, new UpdateResult(UpdateOutcome.Pending, "queued"), new UpdateResult(UpdateOutcome.Running, "installing", true, 3010) })
        {
            int calls = 0; var results = new Dictionary<int, UpdateResult>();
            var batch = await UpdateQueue.RunAsync(new[] { 1, 2 }, (item, index) =>
            { calls++; return Task.FromResult(unfinished); }, (item, result) => results.Add(item, result), CancellationToken.None);
            Equal(1, calls); Equal(1, batch.Unknown); Equal(1, batch.Skipped); Equal(0, batch.Succeeded); Equal(0, batch.Failed);
            Equal(UpdateOutcome.Unknown, results[1].Outcome); Check(!results[1].CanRetry, "An unfinished result enabled a duplicate install attempt");
            Equal(unfinished?.RestartRequired ?? false, batch.RestartRequired);
            Equal(unfinished?.ExitCode, results[1].ExitCode);
        }
    }

}
