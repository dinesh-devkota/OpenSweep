using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSweep.Core
{
    public enum UpdateOutcome { Pending, Running, Succeeded, Failed, Cancelled, Skipped, Unknown }
    public enum UpdatePhase { Ready, Queued, Downloading, Installing, InstallerManaged, Finished }

    public static class UpdatePhases
    {
        public static string Describe(UpdatePhase phase)
        {
            switch (phase)
            {
                case UpdatePhase.Queued: return "Queued";
                case UpdatePhase.Downloading: return "Downloading";
                case UpdatePhase.Installing: return "Installing";
                case UpdatePhase.InstallerManaged: return "Updating (winget)";
                case UpdatePhase.Finished: return "Finished";
                default: return "Available";
            }
        }
    }

    public sealed class UpdateResult
    {
        public UpdateOutcome Outcome { get; private set; }
        public bool RestartRequired { get; private set; }
        public int? ExitCode { get; private set; }
        public string Message { get; private set; }
        public bool CanRetry => Outcome == UpdateOutcome.Failed && !RestartRequired;

        public UpdateResult(UpdateOutcome outcome, string message, bool restartRequired = false, int? exitCode = null)
        {
            Outcome = outcome;
            Message = message;
            RestartRequired = restartRequired;
            ExitCode = exitCode;
        }

        // HRESULT constants from microsoft/winget-cli AppInstallerErrors.h. Do not infer
        // restart requirements from localized installer output ("no restart" is not a reboot).
        public static UpdateResult FromWingetExitCode(int code, string detail = null)
        {
            switch (unchecked((uint)code))
            {
                case 0: return new UpdateResult(UpdateOutcome.Succeeded, "Updated", false, code);
                case 3010: case 1641: case 0x8A150109: case 0x8A15010B:
                    return new UpdateResult(UpdateOutcome.Succeeded, "Updated — restart required", true, code);
                case 0x8A15010A:
                    return new UpdateResult(UpdateOutcome.Failed, "Restart required before installation; check again after restarting", true, code);
                case 1602: case 0x8A15010C:
                    return new UpdateResult(UpdateOutcome.Cancelled, "Installer cancelled; earlier changes may remain", false, code);
                case 0x8A15002B:
                    return new UpdateResult(UpdateOutcome.Skipped, "No applicable update; check again to refresh versions", false, code);
                default:
                    return new UpdateResult(UpdateOutcome.Failed, $"Failed (0x{code:X8})" +
                        (string.IsNullOrWhiteSpace(detail) ? "" : " — " + detail), false, code);
            }
        }

        public static UpdateResult FromWindowsUpdate(int resultCode, int hresult, bool restartRequired)
        {
            if (resultCode == 2)
                return new UpdateResult(UpdateOutcome.Succeeded, restartRequired ? "Installed — restart required" : "Installed", restartRequired, hresult);
            if (resultCode == 5)
                return new UpdateResult(UpdateOutcome.Cancelled, "Windows cancelled the update; earlier changes may remain", restartRequired, hresult);
            return new UpdateResult(UpdateOutcome.Failed, $"Failed (0x{hresult:X8}; Windows result {resultCode})", restartRequired, hresult);
        }
    }

    public sealed class UpdateBatchResult
    {
        public int Succeeded { get; internal set; }
        public int Failed { get; internal set; }
        public int Cancelled { get; internal set; }
        public int Skipped { get; internal set; }
        public int Unknown { get; internal set; }
        public bool RestartRequired { get; internal set; }
        public int Total => Succeeded + Failed + Cancelled + Skipped + Unknown;
    }

    /// <summary>Cancellation stops admission to the queue; it does not kill a running installer.</summary>
    public static class UpdateQueue
    {
        public static async Task<UpdateBatchResult> RunAsync<T>(IList<T> items,
            Func<T, int, Task<UpdateResult>> execute, Action<T, UpdateResult> completed, CancellationToken stopRemaining)
        {
            var batch = new UpdateBatchResult();
            bool stop = false;
            for (int i = 0; i < items.Count; i++)
            {
                UpdateResult result;
                if (stop || stopRemaining.IsCancellationRequested)
                    result = new UpdateResult(UpdateOutcome.Skipped, batch.Unknown > 0
                        ? "Skipped — previous installer completion is unknown" : "Skipped — remaining queue cancelled");
                else
                {
                    try { result = await execute(items[i], i).ConfigureAwait(true); }
                    catch (ProcessOutputIncompleteException ex)
                    { result = new UpdateResult(UpdateOutcome.Unknown, "Installer completion is unknown; remaining updates were not started",
                        ex.ExitCode.HasValue && UpdateResult.FromWingetExitCode(ex.ExitCode.Value).RestartRequired, ex.ExitCode); }
                    catch (OperationCanceledException)
                    { result = new UpdateResult(UpdateOutcome.Cancelled, "Cancelled before installation completed; review the result before retrying"); }
                    catch (Exception ex)
                    { result = new UpdateResult(UpdateOutcome.Failed, "Failed — " + ex.Message); }
                    if (result == null || result.Outcome == UpdateOutcome.Pending || result.Outcome == UpdateOutcome.Running)
                        result = new UpdateResult(UpdateOutcome.Unknown, "No final completion result was returned", result?.RestartRequired ?? false, result?.ExitCode);
                }
                batch.RestartRequired |= result.RestartRequired;
                switch (result.Outcome)
                {
                    case UpdateOutcome.Succeeded: batch.Succeeded++; break;
                    case UpdateOutcome.Cancelled: batch.Cancelled++; stop = true; break;
                    case UpdateOutcome.Skipped: batch.Skipped++; break;
                    case UpdateOutcome.Unknown: batch.Unknown++; stop = true; break;
                    default: batch.Failed++; break;
                }
                completed?.Invoke(items[i], result);
            }
            return batch;
        }
    }
}
