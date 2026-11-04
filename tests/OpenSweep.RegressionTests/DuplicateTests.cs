using OpenSweep.Core;

internal static partial class Program
{
    static async Task DuplicateTests()
    {
        AppPaths.DataDir = Fixture("duplicate-settings");
        AppSettings.Current.ExcludedPaths = new List<string>();
        await Run("Duplicate scan compares full contents beyond the quick prefix", DuplicateFullHash);
        await Run("Duplicate scan deduplicates overlapping roots", DuplicateOverlap);
        await Run("Duplicate verification accepts an unchanged selected copy", () => VerifyPair("unchanged"));
        await Run("Duplicate verification rejects a same-size changed kept copy", () => VerifyPair("keeper-changed"));
        await Run("Duplicate verification rejects a changed selected copy", () => VerifyPair("selected-changed"));
        await Run("Duplicate verification rejects a missing kept copy", () => VerifyPair("keeper-missing"));
        await Run("Duplicate verification rejects all copies selected", () => VerifyPair("all-selected"));
        await Run("Duplicate verification honors exclusions added after scanning", () => VerifyPair("new-exclusion"));
        await Run("Duplicate verification honors pre-cancellation", DuplicateCancelled);
        await Run("Duplicate selection handles empty groups", () => Sync(() => DuplicateFinder.SelectAllButOne(new[] { new DupGroup() }, true)));
        if (!OperatingSystem.IsWindows())
            await Run("Duplicate scan ignores symbolic-link ancestors", DuplicateLinkedAncestor);
        else { skipped++; Console.WriteLine("SKIP Symbolic-link fixture: not enabled on Windows hosts"); }
    }

    static List<DupGroup> FindDuplicates(params string[] roots) => DuplicateFinder.Find(roots, 1, false, null, CancellationToken.None);

    static (string Directory, DupGroup Group, DupFile Kept, DupFile Selected) Pair()
    {
        AppSettings.Current.ExcludedPaths = new List<string>();
        string directory = Fixture("duplicate-pair");
        File.WriteAllText(Path.Combine(directory, "keeper.txt"), "fixture-content");
        File.WriteAllText(Path.Combine(directory, "remove.txt"), "fixture-content");
        var groups = FindDuplicates(directory);
        Equal(1, groups.Count); Equal(2, groups[0].Files.Count);
        var kept = groups[0].Files.Single(f => f.Name == "keeper.txt");
        var selected = groups[0].Files.Single(f => f.Name == "remove.txt");
        selected.IsChecked = true;
        return (directory, groups[0], kept, selected);
    }

    static Task DuplicateFullHash() => Sync(() =>
    {
        AppSettings.Current.ExcludedPaths = new List<string>();
        string directory = Fixture("duplicate-full-hash");
        byte[] first = Enumerable.Repeat((byte)'a', 65536 + 8192).ToArray();
        byte[] different = (byte[])first.Clone(); different[^1] = (byte)'b';
        File.WriteAllBytes(Path.Combine(directory, "one.bin"), first);
        File.WriteAllBytes(Path.Combine(directory, "two.bin"), first);
        File.WriteAllBytes(Path.Combine(directory, "different.bin"), different);
        var groups = FindDuplicates(directory);
        Equal(1, groups.Count); Equal(2, groups[0].Files.Count);
        Check(groups[0].Files.All(f => f.Name != "different.bin"), "Equal quick prefixes caused a false duplicate");
        Check(!string.IsNullOrWhiteSpace(groups[0].ContentHash), "The scan did not retain a full-content fingerprint");
    });

    static Task DuplicateOverlap() => Sync(() =>
    {
        var pair = Pair();
        var groups = FindDuplicates(pair.Directory, pair.Directory);
        Equal(1, groups.Count); Equal(2, groups[0].Files.Count);
        Equal(2, groups[0].Files.Select(f => f.FullPath).Distinct().Count());
    });

    static Task VerifyPair(string change) => Sync(() =>
    {
        var pair = Pair();
        if (change == "keeper-changed") File.WriteAllText(pair.Kept.FullPath, "changed-content");
        if (change == "selected-changed") File.WriteAllText(pair.Selected.FullPath, "changed-content");
        if (change == "keeper-missing") File.Delete(pair.Kept.FullPath);
        if (change == "all-selected") pair.Kept.IsChecked = true;
        if (change == "new-exclusion") AppSettings.Current.ExcludedPaths = new List<string> { pair.Directory };
        using var plan = DuplicateFinder.PrepareDeletion(new[] { pair.Group }, CancellationToken.None);
        if (change == "unchanged")
        {
            Equal(1, plan.Files.Count); Equal(pair.Selected, plan.Files[0]); Equal(0, plan.Skipped.Count);
            plan.RecheckPaths(); Equal(1, plan.Files.Count);
            // Verifying the plan must never delete fixtures; the Windows shell is not invoked.
            Check(File.Exists(pair.Kept.FullPath) && File.Exists(pair.Selected.FullPath), "Planning modified fixture files");
        }
        else
        {
            Equal(0, plan.Files.Count);
            Check(plan.Skipped.ContainsKey(pair.Selected), "Rejected selection did not carry a skip reason");
        }
    });

    static Task DuplicateCancelled() => Sync(() =>
    {
        var pair = Pair();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Throws<OperationCanceledException>(() => DuplicateFinder.Find(new[] { pair.Directory }, 1, false, null, cts.Token));
        Throws<OperationCanceledException>(() => { using var plan = DuplicateFinder.PrepareDeletion(new[] { pair.Group }, cts.Token); });
    });

    static Task DuplicateLinkedAncestor() => Sync(() =>
    {
        var pair = Pair();
        string parent = Fixture("duplicate-link");
        string link = Path.Combine(parent, "linked");
        Directory.CreateSymbolicLink(link, pair.Directory);
        Equal(0, FindDuplicates(link).Count);
        string child = Path.Combine(pair.Directory, "child"); Directory.CreateDirectory(child);
        File.Copy(pair.Kept.FullPath, Path.Combine(child, "a.txt"));
        File.Copy(pair.Kept.FullPath, Path.Combine(child, "b.txt"));
        Equal(0, FindDuplicates(Path.Combine(link, "child")).Count);
    });
}
