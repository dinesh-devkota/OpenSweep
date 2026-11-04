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

        public static Task CleanAsync(IList<CleanerItem> items, IProgress<(CleanerItem Item, bool Done)> progress, CancellationToken ct,
            MaintenanceOperation history = null, string source = "Manual") => RunAsync(items, true, progress, ct, history, source);

        static Task RunAsync(IList<CleanerItem> items, bool clean, IProgress<(CleanerItem, bool)> progress, CancellationToken ct,
            MaintenanceOperation history = null, string source = "Manual")
        {
            if (!RunGate.Wait(0, ct))
                throw new InvalidOperationException("Another cleanup or analysis is already running. Wait for it to finish or cancel it first.");
            try
            {
                // Do not pass ct to Task.Run: the delegate must execute to release the gate,
                // even if cancellation happens between acquiring it and starting the worker.
                return Task.Run(() =>
                {
                    var operation = clean ? history ?? MaintenanceHistory.Begin("Cleanup", source) : null;
                    bool cancelled = false, failed = false;
                    var finished = new HashSet<CleanerItem>();
                    try
                    {
                        foreach (var i in items) i.ResetResult();
                        long bytes = 0, files = 0;
                        foreach (var i in items)
                        {
                            ct.ThrowIfCancellationRequested();
                            progress?.Report((i, false));
                            if (clean)
                            {
                                operation.AddItem(CleanupHistory.Started(i));
                                try { CleanerEngine.Clean(i, ct); }
                                finally
                                {
                                    if (i.HasResult) { operation.AddItem(CleanupHistory.FromItem(i)); finished.Add(i); }
                                }
                            }
                            else CleanerEngine.Analyze(i, ct);
                            bytes += i.Bytes; files += i.Files;
                            progress?.Report((i, true));
                        }
                        ct.ThrowIfCancellationRequested();
                        if (clean) Log.Write($"Cleaned {items.Count} items: {Fmt.Count(files, "file")}, {Fmt.Bytes(bytes)}");
                    }
                    catch (OperationCanceledException) { cancelled = true; throw; }
                    catch (Exception ex)
                    {
                        failed = true;
                        operation?.AddItem(new MaintenanceItem { Label = "Cleanup", Status = "Failed", Detail = ex.Message, Outcome = MaintenanceOutcome.Failed, Failures = 1 });
                        throw;
                    }
                    finally
                    {
                        try
                        {
                            if (operation != null)
                            {
                                foreach (var item in items)
                                    if (!finished.Contains(item)) operation.AddItem(CleanupHistory.NotRun(item, cancelled, "Cleanup stopped before a confirmed item result was recorded."));
                                operation.CompleteFromItems(cancelled, failed ? "Cleanup stopped after an error. Review individual results for changes already made." :
                                    "Permanent cleanup. Results describe completed work; skipped locations may contain additional files that were not examined.");
                                if (history == null) operation.Dispose();
                            }
                        }
                        finally { RunGate.Release(); }
                    }
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
