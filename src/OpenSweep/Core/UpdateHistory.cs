using System;

namespace OpenSweep.Core
{
    public static class UpdateHistory
    {
        public static void Started(MaintenanceOperation operation, string id, string label, string detail) =>
            operation.AddItem(new MaintenanceItem { Id = id, Label = label, Detail = detail,
                Status = "Started; completion not yet confirmed", Outcome = MaintenanceOutcome.Started });

        public static void Finished(MaintenanceOperation operation, string id, string label, string detail, UpdateResult result)
        {
            var outcome = result.Outcome == UpdateOutcome.Succeeded ? MaintenanceOutcome.Completed
                : result.Outcome == UpdateOutcome.Failed ? MaintenanceOutcome.Failed
                : result.Outcome == UpdateOutcome.Cancelled ? MaintenanceOutcome.Cancelled
                : result.Outcome == UpdateOutcome.Skipped ? MaintenanceOutcome.Partial : MaintenanceOutcome.Unknown;
            operation.AddItem(new MaintenanceItem { Id = id, Label = label, Outcome = outcome, Status = result.Message,
                Detail = detail + (result.ExitCode.HasValue ? $"; result code 0x{result.ExitCode.Value:X8}" : "") +
                    (result.RestartRequired ? "; restart required" : "; no restart requirement was reported"),
                Failures = result.Outcome == UpdateOutcome.Failed ? 1 : 0,
                Skipped = result.Outcome == UpdateOutcome.Skipped ? 1 : 0 });
        }

        public static string Summary(UpdateBatchResult batch) =>
            $"{batch.Succeeded} completed, {batch.Failed} failed, {batch.Cancelled} cancelled, {batch.Skipped} skipped" +
            (batch.Unknown > 0 ? $", {batch.Unknown} completion unknown" : "") +
            (batch.RestartRequired ? ". Restart required." : ".");

        public static MaintenanceOutcome Outcome(UpdateBatchResult batch, bool cancelled)
        {
            if (batch.Unknown > 0) return MaintenanceOutcome.Unknown;
            if (batch.Failed > 0) return batch.Succeeded > 0 ? MaintenanceOutcome.Partial : MaintenanceOutcome.Failed;
            if (cancelled || batch.Cancelled > 0) return batch.Succeeded > 0 ? MaintenanceOutcome.Partial : MaintenanceOutcome.Cancelled;
            return batch.Skipped > 0 ? MaintenanceOutcome.Partial : MaintenanceOutcome.Completed;
        }
    }
}
