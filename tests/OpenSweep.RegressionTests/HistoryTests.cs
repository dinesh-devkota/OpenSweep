using System.Collections.Concurrent;
using System.Xml.Serialization;
using OpenSweep.Core;

internal static partial class Program
{
    static async Task HistoryTests()
    {
        await Run("Maintenance history persists started checkpoints and final outcomes", HistoryLifecycle);
        await Run("Maintenance history keeps interrupted operations unknown", HistoryInterrupted);
        await Run("Maintenance history upserts item checkpoints without double counting", HistoryCheckpoints);
        await Run("Maintenance history writes concurrent independent records durably", HistoryConcurrent);
        await Run("Maintenance history serializes concurrent checkpoints safely", HistoryConcurrentItems);
        await Run("Maintenance history rejected oversized writes preserve the last checkpoint", HistoryAtomicFailure);
        await Run("Maintenance history isolates corrupt unsupported and oversized records", HistoryCorruptRecords);
        await Run("Maintenance history prohibits XML external entities", HistoryDtd);
        await Run("Maintenance history retention preserves unrelated files and backups", HistoryRetention);
        await Run("Maintenance history distinguishes unknown deleted and recycled totals", HistoryTotals);
        await Run("Maintenance history bounds details without losing measured totals", HistoryBoundedDetails);
        await Run("Maintenance history export is atomic and preserves record outcomes", HistoryExport);
        await Run("Maintenance summaries preserve failures cancellation and unknown outcomes", HistoryOutcomeSummary);
        await Run("Cleanup history distinguishes measured zero unknown and partial results", CleanupHistoryResults);
        await Run("Maintenance history preserves Unicode while removing invalid XML characters", HistorySafeText);
        await Run("Maintenance history overflow retains successful outcomes and replacement totals", HistoryOverflowCheckpoints);
        await Run("Maintenance history retention respects its total byte budget", HistoryByteRetention);
        await Run("Maintenance history overflow cannot create success from only failed items", HistoryFailedOverflow);
    }

    static MaintenanceRecord Record(string operation = "Fixture cleanup") => new MaintenanceRecord
    {
        Operation = operation, Source = "Regression fixture", StartedUtc = DateTime.UtcNow,
        Outcome = MaintenanceOutcome.Completed, FinishedUtc = DateTime.UtcNow, Summary = "Fixture completed"
    };
    static string HistoryFile(string directory, MaintenanceRecord record) => Path.Combine(directory, record.Id + ".history.xml");

    static Task HistoryLifecycle() => Sync(() =>
    {
        var store = new MaintenanceHistoryStore(Fixture("history-lifecycle"));
        using (var operation = new MaintenanceOperation(store, "Cleanup", "Scheduled"))
        {
            var started = store.Load().Single();
            Equal(MaintenanceOutcome.Started, started.Outcome); Check(started.FinishedUtc == null, "Started checkpoint already has a completion timestamp");
            operation.AddItem(new MaintenanceItem { Label = "Fixture file", Outcome = MaintenanceOutcome.Completed, BytesKind = HistoryBytesKind.Deleted, Bytes = 10, Files = 1 });
            operation.Complete(MaintenanceOutcome.Partial, "One item finished; another could not be processed");
            operation.AddItem(new MaintenanceItem { Label = "Late result must not rewrite completion" });
        }
        var final = store.Load().Single();
        Equal(MaintenanceOutcome.Partial, final.Outcome); Equal("Scheduled", final.Source); Equal(1, final.Items.Count);
        Check(final.FinishedUtc.HasValue && final.FinishedUtc >= final.StartedUtc, "Completion timestamp was not persisted");
        Equal("One item finished; another could not be processed", final.Summary);
    });

    static Task HistoryInterrupted() => Sync(() =>
    {
        var store = new MaintenanceHistoryStore(Fixture("history-interrupted"));
        using (var operation = new MaintenanceOperation(store, "Interrupted cleanup"))
            operation.AddItem(new MaintenanceItem { Label = "An unfinished item", Outcome = MaintenanceOutcome.Started });
        var record = store.Load().Single();
        Equal(MaintenanceOutcome.Unknown, record.Outcome); Equal(MaintenanceOutcome.Started, record.Items.Single().Outcome);
        Check(record.Summary.Contains("without a confirmed result"), "Interrupted operation acquired a false success summary");
        using (var cancelled = new MaintenanceOperation(store, "Cancelled cleanup")) cancelled.Complete(MaintenanceOutcome.Cancelled, "Stopped by user");
        Equal(MaintenanceOutcome.Cancelled, store.Load().Single(r => r.Operation == "Cancelled cleanup").Outcome);
    });

    static Task HistoryCheckpoints() => Sync(() =>
    {
        var store = new MaintenanceHistoryStore(Fixture("history-checkpoint"));
        using (var operation = new MaintenanceOperation(store, "Cleanup"))
        {
            operation.AddItem(new MaintenanceItem { Id = "item-1", Label = "Temporary files", Outcome = MaintenanceOutcome.Started });
            operation.AddItem(new MaintenanceItem { Id = "item-1", Label = "Temporary files", Outcome = MaintenanceOutcome.Completed, BytesKind = HistoryBytesKind.Deleted, Bytes = 120, Files = 2 });
            operation.AddItem(new MaintenanceItem { Id = "item-2", Label = "Protected file", Outcome = MaintenanceOutcome.Failed, Failures = 1, Skipped = 1 });
            operation.Complete(MaintenanceOutcome.Partial, "Some files could not be removed");
        }
        var record = store.Load().Single();
        Equal(2, record.Items.Count); Equal(120L, record.DeletedBytes.Value); Equal(2L, record.Files.Value);
        Equal(1L, record.Failures); Equal(1L, record.Skipped); Equal(MaintenanceOutcome.Partial, record.Outcome);
    });

    static async Task HistoryConcurrent()
    {
        for (int round = 0; round < 3; round++)
        {
            string directory = Fixture("history-concurrent"); var errors = new ConcurrentQueue<string>();
            var records = Enumerable.Range(0, 32).Select(i => Record("Operation " + i)).ToArray();
            var results = await Task.WhenAll(records.Select(record => Task.Run(() => new MaintenanceHistoryStore(directory, 100, errors.Enqueue).Save(record))));
            Check(results.All(saved => saved), "A concurrent save failed: " + string.Join("; ", errors));
            var loaded = new MaintenanceHistoryStore(directory, 100, errors.Enqueue).Load();
            Check(loaded.Count == records.Length, $"Concurrent round {round}: expected {records.Length} records, loaded {loaded.Count}, published files {Directory.GetFiles(directory, "*.history.xml").Length}; " + string.Join("; ", errors));
            Equal(records.Length, loaded.Select(record => record.Id).Distinct().Count());
            Check(loaded.Select(record => record.Id).ToHashSet().SetEquals(records.Select(record => record.Id)), "A persisted record was lost");
            Check(!errors.Any(), "Concurrent records emitted storage errors");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Concurrent writes left temporary files");
        }
    }

    static async Task HistoryConcurrentItems()
    {
        var store = new MaintenanceHistoryStore(Fixture("history-concurrent-items"));
        using (var operation = new MaintenanceOperation(store, "Concurrent results"))
        {
            await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => operation.AddItem(new MaintenanceItem
            { Id = "item-" + i, Label = "Fixture " + i, Outcome = MaintenanceOutcome.Completed, Files = 1 }))));
            operation.Complete(MaintenanceOutcome.Completed);
        }
        var record = store.Load().Single(); Equal(16, record.Items.Count); Equal(16L, record.Files.Value);
    }

    static Task HistoryAtomicFailure() => Sync(() =>
    {
        string directory = Fixture("history-atomic"); var errors = new List<string>(); var store = new MaintenanceHistoryStore(directory, log: errors.Add);
        var record = Record(); Check(store.Save(record), "Baseline history did not save");
        string originalPath = HistoryFile(directory, record); byte[] baseline = File.ReadAllBytes(originalPath);
        store = new MaintenanceHistoryStore(directory, log: errors.Add, maxTotalBytes: baseline.Length + 50);
        record.Summary = new string('s', 4000);
        Check(!store.Save(record), "Oversized checkpoint was reported saved");
        Check(baseline.SequenceEqual(File.ReadAllBytes(originalPath)), "Rejected history write damaged the previous checkpoint");
        Equal("Fixture completed", store.Load().Single().Summary);
        record.Id = "../not-a-record-id";
        Check(!store.Save(record), "Invalid history identity was reported saved");
        Check(errors.Count > 0 && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failure was not logged or temporary file remained");
    });

    static Task HistoryCorruptRecords() => Sync(() =>
    {
        string directory = Fixture("history-corrupt"); var errors = new List<string>(); var store = new MaintenanceHistoryStore(directory, log: errors.Add);
        var valid = Record("Keep this record"); Check(store.Save(valid), "Valid baseline did not save");
        File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".history.xml"), "<broken");
        var future = Record("Unsupported version"); Check(store.Save(future), "Version fixture did not save");
        string futurePath = HistoryFile(directory, future);
        File.WriteAllText(futurePath, File.ReadAllText(futurePath).Replace("<Version>1</Version>", "<Version>99</Version>"));
        var mismatch = Record("Mismatched ID"); Check(store.Save(mismatch), "ID fixture did not save");
        File.Move(HistoryFile(directory, mismatch), Path.Combine(directory, Guid.NewGuid().ToString("N") + ".history.xml"));
        using (var file = File.Create(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".history.xml"))) file.SetLength(MaintenanceHistoryStore.MaxRecordBytes + 1);
        var loaded = store.Load(); Equal(1, loaded.Count); Equal(valid.Id, loaded[0].Id);
        Check(errors.Count >= 4, "Corrupt records were not diagnosed independently");
    });

    static Task HistoryDtd() => Sync(() =>
    {
        string directory = Fixture("history-dtd"); var errors = new List<string>(); var store = new MaintenanceHistoryStore(directory, log: errors.Add);
        string payload = Path.Combine(directory, "local-fixture.txt"); File.WriteAllText(payload, "fixture-content-must-not-be-expanded");
        string id = Guid.NewGuid().ToString("N");
        string xml = "<!DOCTYPE MaintenanceRecord [<!ENTITY external SYSTEM '" + new Uri(payload).AbsoluteUri + "'>]>" +
            "<MaintenanceRecord><Version>1</Version><Id>" + id + "</Id><Operation>&external;</Operation></MaintenanceRecord>";
        File.WriteAllText(Path.Combine(directory, id + ".history.xml"), xml);
        Equal(0, store.Load().Count); Check(errors.Count > 0, "External-entity record was not rejected");
    });

    static Task HistoryRetention() => Sync(() =>
    {
        string directory = Fixture("history-retention"); var initial = new MaintenanceHistoryStore(directory, 100);
        var records = Enumerable.Range(0, 6).Select(i => Record("Older " + i)).ToArray();
        for (int i = 0; i < records.Length; i++)
        {
            records[i].StartedUtc = new DateTime(2020, 1, 1, 0, i, 0, DateTimeKind.Utc);
            Check(initial.Save(records[i]), "Retention fixture did not save"); File.SetLastWriteTimeUtc(HistoryFile(directory, records[i]), records[i].StartedUtc);
        }
        string backup = Path.Combine(directory, "registry-backup.reg"), unrelated = Path.Combine(directory, "not-a-guid.history.xml");
        File.WriteAllText(backup, "backup fixture"); File.WriteAllText(unrelated, "unrelated fixture");
        var latest = Record("Newest"); var limited = new MaintenanceHistoryStore(directory, 3); Check(limited.Save(latest), "Newest history did not save");
        var loaded = limited.Load(); Equal(3, loaded.Count);
        Check(loaded.Select(record => record.Id).ToHashSet().SetEquals(new[] { latest.Id, records[4].Id, records[5].Id }), "Retention removed recent records instead of old ones");
        Equal("backup fixture", File.ReadAllText(backup)); Equal("unrelated fixture", File.ReadAllText(unrelated));
    });

    static Task HistoryTotals() => Sync(() =>
    {
        var record = Record(); Check(record.DeletedBytes == null && record.RecycledBytes == null && record.Files == null, "Unknown totals became zero");
        record.Items.Add(new MaintenanceItem { BytesKind = HistoryBytesKind.Deleted, Bytes = 0, Files = 0 });
        Equal(0L, record.DeletedBytes.Value); Equal(0L, record.Files.Value);
        record.Items.Add(new MaintenanceItem { BytesKind = HistoryBytesKind.Deleted, Bytes = 100, Files = 2 });
        record.Items.Add(new MaintenanceItem { BytesKind = HistoryBytesKind.Recycled, Bytes = 200, Files = 3, Skipped = 1, Failures = 2 });
        record.Items.Add(new MaintenanceItem { BytesKind = HistoryBytesKind.None, Bytes = 400 });
        Equal(100L, record.DeletedBytes.Value); Equal(200L, record.RecycledBytes.Value); Equal(5L, record.Files.Value);
        Check(record.TotalsText.Contains("deleted") && record.TotalsText.Contains("Recycle Bin"), "Deleted and recycled sizes were conflated");
        Check(!record.TotalsText.Contains("free disk"), "Logical file sizes were claimed as measured free disk gains");
        record.Items.Add(new MaintenanceItem { BytesKind = HistoryBytesKind.Deleted, Bytes = long.MaxValue });
        Equal(long.MaxValue, record.DeletedBytes.Value);
    });

    static Task HistoryBoundedDetails() => Sync(() =>
    {
        var store = new MaintenanceHistoryStore(Fixture("history-bounded")); var record = Record();
        for (int i = 0; i < 620; i++) record.Items.Add(new MaintenanceItem
        { Label = "Item " + i, Outcome = MaintenanceOutcome.Completed, BytesKind = (HistoryBytesKind)(i % 3), Bytes = 10, Files = 1, Skipped = 1, Failures = 1 });
        long deleted = record.DeletedBytes.Value, recycled = record.RecycledBytes.Value;
        Check(store.Save(record), "Bounded record did not save"); var loaded = store.Load().Single();
        Check(loaded.Items.Count <= MaintenanceHistoryStore.MaxItems && loaded.OmittedItems > 0, "History details were not bounded");
        Equal(deleted, loaded.DeletedBytes.Value); Equal(recycled, loaded.RecycledBytes.Value); Equal(620L, loaded.Files.Value);
        Equal(620L, loaded.Skipped); Equal(620L, loaded.Failures);
    });

    static Task HistoryExport() => Sync(() =>
    {
        string directory = Fixture("history-export"); var store = new MaintenanceHistoryStore(directory); var record = Record(); record.Outcome = MaintenanceOutcome.Cancelled;
        string path = Path.Combine(directory, "report.xml"); store.Export(new[] { record }, path);
        using (var input = File.OpenRead(path))
        {
            var exported = (MaintenanceHistoryExport)new XmlSerializer(typeof(MaintenanceHistoryExport)).Deserialize(input);
            Equal(1, exported.Records.Count); Equal(record.Id, exported.Records[0].Id); Equal(MaintenanceOutcome.Cancelled, exported.Records[0].Outcome);
        }
        byte[] baseline = File.ReadAllBytes(path); record.Id = "invalid record identity";
        Throws<InvalidDataException>(() => store.Export(new[] { record }, path));
        Check(baseline.SequenceEqual(File.ReadAllBytes(path)), "Failed report export damaged the existing report");
        Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Export failure left temporary files");
    });

    static Task HistoryOutcomeSummary() => Sync(() =>
    {
        var success = new MaintenanceItem { Outcome = MaintenanceOutcome.Completed };
        var failure = new MaintenanceItem { Outcome = MaintenanceOutcome.Failed, Failures = 1 };
        var unknown = new MaintenanceItem { Outcome = MaintenanceOutcome.Unknown };
        Equal(MaintenanceOutcome.Failed, MaintenanceHistorySummary.GetOutcome(new[] { failure }));
        Equal(MaintenanceOutcome.Unknown, MaintenanceHistorySummary.GetOutcome(new[] { unknown }));
        Equal(MaintenanceOutcome.Unknown, MaintenanceHistorySummary.GetOutcome(new[] { new MaintenanceItem { Outcome = MaintenanceOutcome.Started } }));
        Equal(MaintenanceOutcome.Partial, MaintenanceHistorySummary.GetOutcome(new[] { success, failure }));
        Equal(MaintenanceOutcome.Partial, MaintenanceHistorySummary.GetOutcome(new[] { success, unknown }));
        Equal(MaintenanceOutcome.Partial, MaintenanceHistorySummary.GetOutcome(new[] { success, new MaintenanceItem { Skipped = 1 } }));
        Equal(MaintenanceOutcome.Cancelled, MaintenanceHistorySummary.GetOutcome(new[] { success, failure }, cancelled: true));
        Equal(MaintenanceOutcome.Cancelled, MaintenanceHistorySummary.GetOutcome(new[] { success, new MaintenanceItem { Outcome = MaintenanceOutcome.Cancelled } }));
        var store = new MaintenanceHistoryStore(Fixture("history-summary"));
        using (var operation = new MaintenanceOperation(store, "Partial operation"))
        { operation.AddItem(success); operation.AddItem(failure); operation.CompleteFromItems(summary: "Review failed items"); }
        var record = store.Load().Single(); Equal(MaintenanceOutcome.Partial, record.Outcome); Equal("Review failed items", record.Summary);
    });

    static Task CleanupHistoryResults() => Sync(() =>
    {
        var item = new CleanerItem { Bytes = 0, Files = 0, HasResult = true };
        var empty = CleanupHistory.FromItem(item);
        Equal(0L, empty.Bytes.Value); Equal(0L, empty.Files.Value); Equal(HistoryBytesKind.Deleted, empty.BytesKind);
        Equal(RecoveryKind.None, empty.Recovery); Equal(MaintenanceOutcome.Completed, empty.Outcome);
        item.CountsKnown = false;
        var unknown = CleanupHistory.FromItem(item);
        Check(unknown.Bytes == null && unknown.Files == null, "Unmeasured cleanup totals became zero");
        Equal(HistoryBytesKind.None, unknown.BytesKind);
        item.CountsKnown = true; item.Bytes = 30; item.Files = 2; item.Failures = 1; item.Skipped = 3;
        var partial = CleanupHistory.FromItem(item);
        Equal(MaintenanceOutcome.Partial, partial.Outcome); Equal(30L, partial.Bytes.Value); Equal(2L, partial.Files.Value);
        Equal(1L, partial.Failures); Equal(3L, partial.Skipped);
        item.WasCancelled = true;
        Equal(MaintenanceOutcome.Cancelled, CleanupHistory.FromItem(item).Outcome);
        var notRun = CleanupHistory.NotRun(item, true, "Cancelled before this item");
        Check(notRun.Bytes == null && notRun.Files == null, "Unexecuted cleanup was reported as measured");
        Equal(MaintenanceOutcome.Cancelled, notRun.Outcome); Equal(1L, notRun.Skipped);
    });

    static Task HistorySafeText() => Sync(() =>
    {
        Equal<string>(null, MaintenanceHistoryText.Limit(null, 5));
        Equal("", MaintenanceHistoryText.Limit("text", 0));
        Equal("a\uD83D\uDE00", MaintenanceHistoryText.Limit("a\uD83D\uDE00z", 3));
        Equal("a", MaintenanceHistoryText.Limit("a\uD83D\uDE00z", 2));
        Equal("ab\t\n\r", MaintenanceHistoryText.Limit("a\0\u001B\u000Bb\t\n\r", 30));
        Equal("a\uD83D\uDE00b\uFFFD", MaintenanceHistoryText.Limit("\uD83Da\uDE00\uD83D\uDE00b\uFFFE\uFFFF\uFFFD", 30));
        var store = new MaintenanceHistoryStore(Fixture("history-safe-text")); var record = Record(new string('a', 255) + "\uD83D\uDE00tail");
        record.Summary = "Readable\0 summary\uD800";
        record.Items.Add(new MaintenanceItem { Label = "Fixture\u000B label", Detail = new string('d', 2047) + "\uD83D\uDE00tail" });
        Check(store.Save(record), "Unicode truncation or invalid characters prevented history persistence");
        var loaded = store.Load().Single(); Equal(new string('a', 255), loaded.Operation); Equal("Readable summary", loaded.Summary);
        Equal("Fixture label", loaded.Items.Single().Label);
        Check(!loaded.Items.Single().Detail.Any(char.IsSurrogate), "Text truncation left an incomplete surrogate pair");
    });

    static Task HistoryOverflowCheckpoints() => Sync(() =>
    {
        var store = new MaintenanceHistoryStore(Fixture("history-overflow-checkpoints"));
        using (var operation = new MaintenanceOperation(store, "Many successful results"))
        {
            for (int i = 0; i < 501; i++) operation.AddItem(new MaintenanceItem
            { Id = "item-" + i, Label = "Fixture " + i, Outcome = MaintenanceOutcome.Completed, BytesKind = HistoryBytesKind.Deleted, Bytes = 1, Files = 1 });
            var initial = store.Load().Single(); Equal(501L, initial.DeletedBytes.Value); Equal(501L, initial.Files.Value);
            operation.AddItem(new MaintenanceItem { Id = "item-499", Label = "Replacement result", Outcome = MaintenanceOutcome.Completed, BytesKind = HistoryBytesKind.Deleted, Bytes = 4, Files = 1 });
            var replaced = store.Load().Single(); Equal(504L, replaced.DeletedBytes.Value); Equal(501L, replaced.Files.Value); Equal(initial.OmittedItems, replaced.OmittedItems);
            operation.CompleteFromItems();
        }
        var final = store.Load().Single(); Equal(MaintenanceOutcome.Completed, final.Outcome);
        Equal(504L, final.DeletedBytes.Value); Equal(501L, final.Files.Value);
        Check(final.Items.Count <= MaintenanceHistoryStore.MaxItems, "Persisted details exceeded the record bound");
    });

    static Task HistoryByteRetention() => Sync(() =>
    {
        string directory = Fixture("history-byte-retention"); var store = new MaintenanceHistoryStore(directory, 100);
        var records = Enumerable.Range(0, 4).Select(i => Record("Item " + i)).ToArray();
        for (int i = 0; i < records.Length; i++)
        {
            Check(store.Save(records[i]), "Byte-budget fixture did not save");
            File.SetLastWriteTimeUtc(HistoryFile(directory, records[i]), new DateTime(2020, 1, 1, 0, i, 0, DateTimeKind.Utc));
        }
        long budget = new FileInfo(HistoryFile(directory, records[2])).Length + new FileInfo(HistoryFile(directory, records[3])).Length;
        var limited = new MaintenanceHistoryStore(directory, 100, maxTotalBytes: budget);
        Check(limited.Save(records[3]), "Latest checkpoint did not fit the byte budget");
        var loaded = limited.Load(); Equal(2, loaded.Count);
        Check(loaded.Select(record => record.Id).ToHashSet().SetEquals(new[] { records[2].Id, records[3].Id }), "Byte-budget retention discarded recent records");
        Check(Directory.GetFiles(directory, "*.history.xml").Sum(path => new FileInfo(path).Length) <= budget, "History storage exceeded its total byte budget");
    });

    static Task HistoryFailedOverflow() => Sync(() =>
    {
        var store = new MaintenanceHistoryStore(Fixture("history-failed-overflow")); var record = Record("Failed operation"); record.Outcome = MaintenanceOutcome.Failed;
        record.Items = Enumerable.Range(0, 501).Select(i => new MaintenanceItem
        { Id = "failed-" + i, Outcome = MaintenanceOutcome.Failed, Failures = 1, BytesKind = HistoryBytesKind.None }).ToList();
        Check(store.Save(record), "Failed-overflow fixture did not save");
        var loaded = store.Load().Single(); Equal(501L, loaded.Failures);
        Equal(MaintenanceOutcome.Failed, MaintenanceHistorySummary.GetOutcome(loaded.Items));
        Check(loaded.Items.All(item => item.Outcome == MaintenanceOutcome.Failed), "An empty aggregation bucket introduced a completed result");
    });
}
