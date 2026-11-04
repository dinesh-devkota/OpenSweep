using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace OpenSweep.Core
{
    public enum MaintenanceOutcome { Started, Completed, Partial, Failed, Cancelled, Unknown }
    public enum RecoveryKind { None, RecycleBin, RegistryBackup, Startup, Restore }
    // Deleted/Recycled refer to logical file sizes, never measured changes in free disk space.
    public enum HistoryBytesKind { None, Deleted, Recycled }

    public sealed class MaintenanceItem
    {
        // Supplying a stable ID lets an operation checkpoint Started then replace it with the result.
        public string Id { get; set; }
        public string Label { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public MaintenanceOutcome Outcome { get; set; } = MaintenanceOutcome.Unknown;
        public long? Bytes { get; set; }
        public long? Files { get; set; }
        public long Skipped { get; set; }
        public long Failures { get; set; }
        public HistoryBytesKind BytesKind { get; set; }
        public RecoveryKind Recovery { get; set; }
        public List<string> BackupPaths { get; set; } = new List<string>();
        [XmlIgnore] public string ResultText => Status ?? Outcome.ToString();
        [XmlIgnore] public string SizeText => Bytes.HasValue ? FormatBytes(Bytes.Value) +
            (BytesKind == HistoryBytesKind.Recycled ? " moved" : BytesKind == HistoryBytesKind.Deleted ? " deleted" : " processed") : "Not measured";
        internal static string FormatBytes(long bytes)
        {
            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, bytes); int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }

    public sealed class MaintenanceRecord
    {
        public int Version { get; set; } = 1;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Operation { get; set; }
        public string Source { get; set; } = "Manual";
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; set; }
        public MaintenanceOutcome Outcome { get; set; } = MaintenanceOutcome.Started;
        public string Summary { get; set; }
        public List<MaintenanceItem> Items { get; set; } = new List<MaintenanceItem>();
        public long OmittedItems { get; set; }
        public bool ItemLimitReached { get; set; }
        [XmlIgnore] public string StartedText => StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        [XmlIgnore] public long? DeletedBytes => Tally(HistoryBytesKind.Deleted);
        [XmlIgnore] public long? RecycledBytes => Tally(HistoryBytesKind.Recycled);
        [XmlIgnore] public long? Files => Items != null && Items.Any(i => i?.Files != null)
            ? (long?)SafeSum(Items.Where(i => i?.Files != null).Select(i => i.Files.Value)) : null;
        [XmlIgnore] public long Skipped => SafeSum((Items ?? new List<MaintenanceItem>()).Where(i => i != null).Select(i => i.Skipped));
        [XmlIgnore] public long Failures => SafeSum((Items ?? new List<MaintenanceItem>()).Where(i => i != null).Select(i => i.Failures));
        [XmlIgnore] public string OutcomeText => Outcome == MaintenanceOutcome.Started ? "Started · completion not yet recorded" : Outcome.ToString();
        [XmlIgnore] public string TotalsText
        {
            get
            {
                var parts = new List<string>();
                if (DeletedBytes.HasValue) parts.Add(MaintenanceItem.FormatBytes(DeletedBytes.Value) + " of file data deleted");
                if (RecycledBytes.HasValue) parts.Add(MaintenanceItem.FormatBytes(RecycledBytes.Value) + " moved toward Recycle Bin");
                if (Files.HasValue) parts.Add(Files.Value.ToString("N0") + " files/items");
                if (Skipped > 0) parts.Add(Skipped.ToString("N0") + " skipped");
                if (Failures > 0) parts.Add(Failures.ToString("N0") + " failed");
                return (ItemLimitReached ? "Incomplete recorded totals: " : "") +
                    (parts.Count > 0 ? string.Join(" · ", parts) : "No file-size or item totals were recorded.");
            }
        }
        long? Tally(HistoryBytesKind kind)
        {
            var values = (Items ?? new List<MaintenanceItem>()).Where(i => i != null && i.BytesKind == kind && i.Bytes.HasValue).ToList();
            return values.Count == 0 ? (long?)null : SafeSum(values.Select(i => i.Bytes.Value));
        }
        internal static long SafeSum(IEnumerable<long> values)
        {
            long total = 0;
            foreach (var value in values) if (value > 0) total = value > long.MaxValue - total ? long.MaxValue : total + value;
            return total;
        }
    }

    [XmlRoot("MaintenanceHistory")]
    public sealed class MaintenanceHistoryExport
    {
        public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
        public List<MaintenanceRecord> Records { get; set; } = new List<MaintenanceRecord>();
    }

    /// <summary>One atomic XML file per operation; no shared index or whole-history rewrite.</summary>
    public sealed class MaintenanceHistoryStore
    {
        public const int MaxItems = 500;
        public const int MaxOperationItems = 10000;
        public const long DefaultMaxTotalBytes = 32 * 1024 * 1024;
        public const long MaxRecordBytes = 8 * 1024 * 1024;
        readonly string _directory;
        readonly int _maxRecords;
        readonly Action<string> _log;
        readonly long _maxTotalBytes;
        static readonly XmlSerializer RecordSerializer = new XmlSerializer(typeof(MaintenanceRecord));
        static readonly XmlSerializer ExportSerializer = new XmlSerializer(typeof(MaintenanceHistoryExport));
        public MaintenanceHistoryStore(string directory, int maxRecords = 200, Action<string> log = null, long maxTotalBytes = DefaultMaxTotalBytes)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A history directory is required.", nameof(directory));
            _directory = Path.GetFullPath(directory);
            _maxRecords = Math.Max(1, Math.Min(2000, maxRecords));
            _log = log;
            if (maxTotalBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));
            _maxTotalBytes = maxTotalBytes;
        }

        public bool Save(MaintenanceRecord record)
        {
            try
            {
                var copy = Normalize(record);
                Directory.CreateDirectory(_directory);
                // Serialize publication and retention across app/scheduler processes. The lock
                // file is never pruned; readers continue to use atomic record snapshots.
                using (AcquireWriteLock())
                {
                    var target = Path.Combine(_directory, copy.Id + ".history.xml");
                    AtomicWrite(target, stream => { lock (RecordSerializer) RecordSerializer.Serialize(stream, copy); }, Math.Min(MaxRecordBytes, _maxTotalBytes));
                    Prune();
                }
                return true;
            }
            catch (Exception ex) { Report("History could not be saved: " + ex.Message); return false; }
        }

        public List<MaintenanceRecord> Load()
        {
            var records = new List<MaintenanceRecord>();
            try
            {
                if (!Directory.Exists(_directory)) return records;
                long readBytes = 0;
                // Limit both parsed files and retained objects, even if older versions left a large directory.
                foreach (var path in NewestFiles(_maxRecords * 2))
                {
                    try
                    {
                        long length = new FileInfo(path).Length;
                        if (length > MaxRecordBytes) { Report("Oversized history record skipped."); continue; }
                        if (length > _maxTotalBytes - readBytes) { Report("History read size limit reached; older records were skipped."); break; }
                        readBytes += length;
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxRecordBytes }))
                        {
                            MaintenanceRecord record;
                            lock (RecordSerializer) record = RecordSerializer.Deserialize(reader) as MaintenanceRecord;
                            if (record == null || record.Version != 1 || !string.Equals(Path.GetFileName(path), record.Id + ".history.xml", StringComparison.Ordinal))
                                throw new InvalidDataException("Unrecognized history record.");
                            records.Add(Normalize(record));
                            if (records.Count >= _maxRecords) break;
                        }
                    }
                    catch (Exception ex) { Report("One history record was skipped: " + ex.Message); }
                }
            }
            catch (Exception ex) { Report("History could not be read: " + ex.Message); }
            return records.OrderByDescending(r => r.StartedUtc).ThenBy(r => r.Id, StringComparer.Ordinal).Take(_maxRecords).ToList();
        }

        public void Export(IEnumerable<MaintenanceRecord> records, string path)
        {
            var copy = new MaintenanceHistoryExport { Records = records.Take(_maxRecords).Select(Normalize).ToList() };
            AtomicWrite(Path.GetFullPath(path), stream => { lock (ExportSerializer) ExportSerializer.Serialize(stream, copy); }, Math.Max(_maxTotalBytes, MaxRecordBytes));
        }

        IEnumerable<string> OwnedFiles() => Directory.EnumerateFiles(_directory, "*.history.xml", SearchOption.TopDirectoryOnly)
            .Where(p => Guid.TryParseExact(Path.GetFileName(p).Substring(0, Path.GetFileName(p).Length - ".history.xml".Length), "N", out _));

        List<string> NewestFiles(int count)
        {
            var newest = new SortedSet<(DateTime Written, string Path)>(Comparer<(DateTime Written, string Path)>.Create((a, b) =>
            {
                int time = a.Written.CompareTo(b.Written);
                return time != 0 ? time : StringComparer.Ordinal.Compare(a.Path, b.Path);
            }));
            foreach (var path in OwnedFiles())
            {
                try
                {
                    newest.Add((File.GetLastWriteTimeUtc(path), path));
                    if (newest.Count > count) newest.Remove(newest.Min);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return newest.Reverse().Select(p => p.Path).ToList();
        }

        void Prune()
        {
            try
            {
                long bytes = 0;
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in NewestFiles(_maxRecords))
                {
                    try
                    {
                        long length = new FileInfo(path).Length;
                        if (length <= _maxTotalBytes - bytes) { keep.Add(path); bytes += length; }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                // AcquireWriteLock prevents cooperating writers from publishing between passes.
                foreach (var path in OwnedFiles())
                {
                    if (keep.Contains(path)) continue;
                    try { File.Delete(path); }
                    catch (IOException ex) { Report("A history record could not be pruned: " + ex.Message); }
                    catch (UnauthorizedAccessException ex) { Report("A history record could not be pruned: " + ex.Message); }
                }
            }
            catch (Exception ex) { Report("History retention could not run: " + ex.Message); }
        }

        FileStream AcquireWriteLock()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try { return new FileStream(Path.Combine(_directory, ".history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(10); }
            }
        }

        static void AtomicWrite(string target, Action<Stream> write, long maxBytes)
        {
            var directory = Path.GetDirectoryName(target);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            var temporary = Path.Combine(directory, ".history-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    if (stream.Length > maxBytes) throw new InvalidDataException("History data exceeds its size limit.");
                    stream.Flush(true);
                }
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else
                {
                    try { File.Move(temporary, target); }
                    catch (IOException) when (File.Exists(target)) { File.Replace(temporary, target, null); }
                }
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        internal static MaintenanceRecord Normalize(MaintenanceRecord record)
        {
            if (record == null || !Guid.TryParseExact(record.Id, "N", out _)) throw new InvalidDataException("Invalid history record ID.");
            var copy = new MaintenanceRecord
            {
                Id = record.Id, Operation = Limit(record.Operation, 256), Source = Limit(record.Source, 64),
                StartedUtc = record.StartedUtc, FinishedUtc = record.FinishedUtc,
                Outcome = Valid(record.Outcome) ? record.Outcome : MaintenanceOutcome.Unknown,
                Summary = Limit(record.Summary, 4096), OmittedItems = Math.Max(0, record.OmittedItems),
                ItemLimitReached = record.ItemLimitReached || (record.Items?.Count ?? 0) > MaxOperationItems
            };
            // Resolve item revisions before reducing details, so an overflow update cannot count twice.
            var latest = new List<MaintenanceItem>();
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in (record.Items ?? new List<MaintenanceItem>()).Take(MaxOperationItems))
            {
                if (item == null) continue;
                var value = CopyItem(item);
                if (!string.IsNullOrEmpty(value.Id) && ids.TryGetValue(value.Id, out var index)) latest[index] = value;
                else { if (!string.IsNullOrEmpty(value.Id)) ids[value.Id] = latest.Count; latest.Add(value); }
            }
            foreach (var item in latest) AddBounded(copy, item);
            // Empty byte-kind buckets are implementation placeholders, not successful work.
            copy.Items.RemoveAll(i => i.Label == "Additional results" && i.Status == null &&
                i.Detail == "Details omitted to keep this record bounded.");
            return copy;
        }

        internal static MaintenanceItem CopyItem(MaintenanceItem item) => new MaintenanceItem
        {
            Id = Limit(item.Id, 256), Label = Limit(item.Label, 256), Status = Limit(item.Status, 512), Detail = Limit(item.Detail, 2048),
            Outcome = Valid(item.Outcome) ? item.Outcome : MaintenanceOutcome.Unknown,
            Bytes = item.Bytes.HasValue ? (long?)Math.Max(0, item.Bytes.Value) : null,
            Files = item.Files.HasValue ? (long?)Math.Max(0, item.Files.Value) : null,
            Skipped = Math.Max(0, item.Skipped), Failures = Math.Max(0, item.Failures),
            BytesKind = Enum.IsDefined(typeof(HistoryBytesKind), item.BytesKind) ? item.BytesKind : HistoryBytesKind.None,
            Recovery = Enum.IsDefined(typeof(RecoveryKind), item.Recovery) ? item.Recovery : RecoveryKind.None,
            BackupPaths = (item.BackupPaths ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Take(4).Select(p => Limit(p, 1024)).ToList()
        };

        internal static void AddBounded(MaintenanceRecord record, MaintenanceItem item)
        {
            if (!string.IsNullOrEmpty(item.Id))
            {
                int existing = record.Items.FindIndex(i => i.Id == item.Id);
                if (existing >= 0) { record.Items[existing] = item; return; }
            }
            if (record.Items.Count < MaxItems) { record.Items.Add(item); return; }
            // Keep bounded detail rows and preserve all numeric totals in separate byte-kind buckets.
            // Replace the last three detail rows with those buckets on the first overflow.
            if (!record.Items.Skip(MaxItems - 3).All(i => i.Label == "Additional results" && i.Detail == "Details omitted to keep this record bounded."))
            {
                var tail = record.Items.Skip(MaxItems - 3).ToList();
                record.Items.RemoveRange(MaxItems - 3, 3);
                foreach (HistoryBytesKind kind in Enum.GetValues(typeof(HistoryBytesKind)))
                    record.Items.Add(new MaintenanceItem { Label = "Additional results", Detail = "Details omitted to keep this record bounded.", BytesKind = kind, Outcome = MaintenanceOutcome.Completed });
                foreach (var old in tail) AddToBucket(record, old);
            }
            AddToBucket(record, item);
        }
        static void AddToBucket(MaintenanceRecord record, MaintenanceItem item)
        {
            var bucket = record.Items.Skip(MaxItems - 3).First(i => i.BytesKind == item.BytesKind);
            bucket.Bytes = SumNullable(bucket.Bytes, item.Bytes); bucket.Files = SumNullable(bucket.Files, item.Files);
            bucket.Skipped = MaintenanceRecord.SafeSum(new[] { bucket.Skipped, item.Skipped });
            bucket.Failures = MaintenanceRecord.SafeSum(new[] { bucket.Failures, item.Failures });
            bucket.Outcome = bucket.Status == null ? item.Outcome : MaintenanceHistorySummary.GetOutcome(new[] { bucket, item });
            bucket.Status = "Summarized results";
            record.OmittedItems++;
        }
        static long? SumNullable(long? a, long? b) => a.HasValue || b.HasValue ? (long?)MaintenanceRecord.SafeSum(new[] { a ?? 0, b ?? 0 }) : null;
        static bool Valid(MaintenanceOutcome value) => Enum.IsDefined(typeof(MaintenanceOutcome), value);
        static string Limit(string value, int max) => MaintenanceHistoryText.Limit(value, max);
        void Report(string message) { try { _log?.Invoke(message); } catch { } }
    }
}
