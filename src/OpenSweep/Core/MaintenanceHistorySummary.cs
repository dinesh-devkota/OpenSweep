using System.Collections.Generic;
using System.Linq;

namespace OpenSweep.Core
{
    public static class MaintenanceHistorySummary
    {
        public static MaintenanceOutcome GetOutcome(IEnumerable<MaintenanceItem> items, bool cancelled = false)
        {
            var results = (items ?? Enumerable.Empty<MaintenanceItem>()).Where(i => i != null).ToList();
            if (cancelled || results.Any(i => i.Outcome == MaintenanceOutcome.Cancelled)) return MaintenanceOutcome.Cancelled;
            if (results.Count == 0) return MaintenanceOutcome.Completed;
            bool success = results.Any(i => i.Outcome == MaintenanceOutcome.Completed || i.Outcome == MaintenanceOutcome.Partial || (i.Files ?? 0) > 0);
            bool failure = results.Any(i => i.Outcome == MaintenanceOutcome.Failed || i.Failures > 0);
            bool unknown = results.Any(i => (i.Outcome == MaintenanceOutcome.Unknown || i.Outcome == MaintenanceOutcome.Started) && i.Skipped == 0);
            if (failure && !success) return MaintenanceOutcome.Failed;
            if (results.Any(i => i.Outcome == MaintenanceOutcome.Partial || i.Skipped > 0) || (success && (failure || unknown))) return MaintenanceOutcome.Partial;
            if (failure) return MaintenanceOutcome.Failed;
            if (unknown) return MaintenanceOutcome.Unknown;
            return MaintenanceOutcome.Completed;
        }
    }
}
