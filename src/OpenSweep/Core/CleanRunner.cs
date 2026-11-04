using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSweep.Core
{
    /// <summary>Runs analyze/clean over a set of cleaner items on a background thread.</summary>
    public static class CleanRunner
    {
        // Health Check and Custom Clean share CleanerItem instances and their result fields.
        // Reject overlapping runs rather than queueing a destructive operation for later.
        static readonly SemaphoreSlim RunGate = new SemaphoreSlim(1, 1);

        /// <param name="progress">Reports each item when it starts (Done=false) and when it finishes (Done=true).</param>
        public static Task AnalyzeAsync(IList<CleanerItem> items, IProgress<(CleanerItem Item, bool Done)> progress, CancellationToken ct) =>
            RunAsync(items, false, progress, ct);

        public static Task CleanAsync(IList<CleanerItem> items, IProgress<(CleanerItem Item, bool Done)> progress, CancellationToken ct) =>
            RunAsync(items, true, progress, ct);

        static Task RunAsync(IList<CleanerItem> items, bool clean, IProgress<(CleanerItem, bool)> progress, CancellationToken ct)
        {
            if (!RunGate.Wait(0, ct))
                throw new InvalidOperationException("Another cleanup or analysis is already running. Wait for it to finish or cancel it first.");
            try
            {
                // Do not pass ct to Task.Run: the delegate must execute to release the gate,
                // even if cancellation happens between acquiring it and starting the worker.
                return Task.Run(() =>
                {
                    try
                    {
                        foreach (var i in items) i.ResetResult();
                        long bytes = 0, files = 0;
                        foreach (var i in items)
                        {
                            ct.ThrowIfCancellationRequested();
                            progress?.Report((i, false));
                            if (clean) CleanerEngine.Clean(i, ct); else CleanerEngine.Analyze(i, ct);
                            bytes += i.Bytes; files += i.Files;
                            progress?.Report((i, true));
                        }
                        ct.ThrowIfCancellationRequested();
                        if (clean) Log.Write($"Cleaned {items.Count} items: {Fmt.Count(files, "file")}, {Fmt.Bytes(bytes)}");
                    }
                    finally { RunGate.Release(); }
                });
            }
            catch
            {
                RunGate.Release();
                throw;
            }
        }
    }
}
