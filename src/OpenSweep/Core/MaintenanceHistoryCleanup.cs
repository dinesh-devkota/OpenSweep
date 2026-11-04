using System;

namespace OpenSweep.Core
{
    public static class CleanupHistory
    {
        public static MaintenanceItem FromItem(CleanerItem item)
        {
            var outcome = item.WasCancelled ? MaintenanceOutcome.Cancelled :
                item.HadError || item.Failures > 0 ? (item.Files > 0 ? MaintenanceOutcome.Partial : MaintenanceOutcome.Failed) :
                item.Skipped > 0 ? MaintenanceOutcome.Partial : MaintenanceOutcome.Completed;
            return new MaintenanceItem
            {
                Id = item.Id, Label = item.Group + " · " + item.Name, Status = item.Status, Outcome = outcome,
                Bytes = item.CountsKnown ? (long?)item.Bytes : null, Files = item.CountsKnown ? (long?)item.Files : null,
                BytesKind = item.CountsKnown ? HistoryBytesKind.Deleted : HistoryBytesKind.None,
                Skipped = item.Skipped, Failures = item.Failures, Recovery = RecoveryKind.None,
                Detail = "Cleanup permanently removes selected data and has no Undo. Byte totals are logical file sizes, not measured disk-space gains. " +
                         "Skipped/failed counts refer to files or locations encountered, not an estimate of unreadable contents." +
                         (string.IsNullOrWhiteSpace(item.ResultDetail) ? "" : "\n" + item.ResultDetail)
            };
        }
        public static MaintenanceItem Skipped(CleanerItem item, string reason) => new MaintenanceItem
        {
            Id = item.Id, Label = item.Group + " · " + item.Name, Status = "Skipped", Detail = reason,
            Outcome = MaintenanceOutcome.Unknown, Skipped = 1, Recovery = RecoveryKind.None
        };
        public static MaintenanceItem NotRun(CleanerItem item, bool cancelled, string reason) => new MaintenanceItem
        {
            Id = item.Id, Label = item.Group + " · " + item.Name, Status = cancelled ? "Not run (cancelled)" : "Not run",
            Detail = reason, Outcome = cancelled ? MaintenanceOutcome.Cancelled : MaintenanceOutcome.Unknown, Skipped = 1
        };
        public static MaintenanceItem Started(CleanerItem item) => new MaintenanceItem
        {
            Id = item.Id, Label = item.Group + " · " + item.Name, Status = "Started", Outcome = MaintenanceOutcome.Started,
            Detail = "Cleanup started. Completion is not yet recorded; do not assume nothing changed."
        };
    }
}
