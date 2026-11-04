namespace OpenSweep.Core;

// The real production CleanRunner orchestration is linked into this test assembly.
// Only the Windows cleanup engine and mutable result model are simulated here;
// these tests do not claim to exercise filesystem cleanup or native Windows APIs.
public sealed class CleanerItem
{
    public long Bytes, Files;
    public bool HasResult;
    public int ResetCount;
    public void ResetResult() { Bytes = 0; Files = 0; HasResult = false; ResetCount++; }
}

public static class CleanerEngine
{
    public static Action<CleanerItem, CancellationToken> Operation { get; set; }
    public static void Analyze(CleanerItem item, CancellationToken token) => Operation(item, token);
    public static void Clean(CleanerItem item, CancellationToken token) => Operation(item, token);
}
