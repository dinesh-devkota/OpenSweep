using System;
using System.Collections.Generic;
using System.IO;

namespace OpenSweep.Core
{
    public static class MaintenanceHistory
    {
        static string _lastError;
        public static string LastError => _lastError;
        static void Report(string message) { System.Threading.Interlocked.Exchange(ref _lastError, message); Log.Write(message); }
        static MaintenanceHistoryStore CreateStore() => new MaintenanceHistoryStore(Path.Combine(AppPaths.DataDir, "History"), log: Report);
        public static MaintenanceOperation Begin(string operation, string source = "Manual")
        {
            MaintenanceHistoryStore store = null;
            try { store = CreateStore(); } catch (Exception ex) { Report("History unavailable: " + ex.Message); }
            return new MaintenanceOperation(store, operation, source);
        }
        public static List<MaintenanceRecord> Load()
        {
            try { return CreateStore().Load(); }
            catch (Exception ex) { Report("History unavailable: " + ex.Message); return new List<MaintenanceRecord>(); }
        }
        public static void Export(IEnumerable<MaintenanceRecord> records, string path) => CreateStore().Export(records, path);
    }

    public sealed class MaintenanceOperation : IDisposable
    {
        readonly object _gate = new object();
        readonly MaintenanceHistoryStore _store;
        readonly MaintenanceRecord _record;
        readonly Dictionary<string, int> _itemIds = new Dictionary<string, int>(StringComparer.Ordinal);
        bool _complete;
        public MaintenanceOperation(MaintenanceHistoryStore store, string operation, string source = "Manual")
        {
            _store = store;
            _record = new MaintenanceRecord { Operation = operation, Source = source };
            Save();
        }
        public void AddItem(MaintenanceItem item)
        {
            if (item == null) return;
            lock (_gate)
            {
                if (_complete) return;
                try
                {
                    var value = MaintenanceHistoryStore.CopyItem(item);
                    if (!string.IsNullOrEmpty(value.Id) && _itemIds.TryGetValue(value.Id, out var index)) _record.Items[index] = value;
                    else if (_record.Items.Count < MaintenanceHistoryStore.MaxOperationItems)
                    {
                        if (!string.IsNullOrEmpty(value.Id)) _itemIds[value.Id] = _record.Items.Count;
                        _record.Items.Add(value);
                    }
                    else _record.ItemLimitReached = true;
                    Save();
                }
                catch (Exception ex) { Log.Write("History item unavailable: " + ex.Message); }
            }
        }
        public void Complete(MaintenanceOutcome outcome, string summary = null)
        {
            lock (_gate)
            {
                if (_complete) return;
                _record.Outcome = outcome;
                _record.Summary = summary + (_record.ItemLimitReached ? "\nHistory reached its item limit; later results were not recorded and totals are incomplete." : "");
                _record.FinishedUtc = DateTime.UtcNow;
                _complete = true;
                Save();
            }
        }
        public void CompleteFromItems(bool cancelled = false, string summary = null)
        {
            lock (_gate)
            {
                var outcome = MaintenanceHistorySummary.GetOutcome(_record.Items, cancelled);
                if (_record.ItemLimitReached && outcome == MaintenanceOutcome.Completed) outcome = MaintenanceOutcome.Partial;
                Complete(outcome, summary);
            }
        }
        public void Dispose() => Complete(MaintenanceOutcome.Unknown, "The operation ended without a confirmed result. Review the item details before retrying.");
        void Save() { try { _store?.Save(_record); } catch (Exception ex) { Log.Write("History unavailable: " + ex.Message); } }
    }
}
