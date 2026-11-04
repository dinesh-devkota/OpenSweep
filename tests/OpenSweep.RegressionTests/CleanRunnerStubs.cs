namespace OpenSweep.Core;

// The real production CleanRunner orchestration is linked into this test assembly.
// Only the Windows cleanup engine and mutable result model are simulated here;
// these tests do not claim to exercise filesystem cleanup or native Windows APIs.
public sealed class CleanerItem
{
    public string Id = Guid.NewGuid().ToString("N"), Group = "Fixture", Name = "Temporary files", Status, ResultDetail;
    public long Bytes, Files;
    public long Failures, Skipped;
    public bool HasResult;
    public bool WasCancelled, HadError, CountsKnown = true;
    public int ResetCount;
    public void ResetResult()
    {
        Bytes = Files = Failures = Skipped = 0;
        HasResult = WasCancelled = HadError = false;
        CountsKnown = true; Status = ResultDetail = null; ResetCount++;
    }
}

public static class CleanerEngine
{
    public static Action<CleanerItem, CancellationToken> Operation { get; set; }
    public static void Analyze(CleanerItem item, CancellationToken token) => Operation(item, token);
    public static void Clean(CleanerItem item, CancellationToken token) => Operation(item, token);
}
