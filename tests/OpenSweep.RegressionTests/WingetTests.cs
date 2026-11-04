using OpenSweep.Core;

internal static partial class Program
{
    static async Task WingetTests()
    {
        await Run("Winget parser preserves spaced app and source names", WingetSpacedFields);
        await Run("Winget parser accepts localized multiword headings", WingetLocalizedHeader);
        await Run("Winget parser accepts compact English table headings", WingetCompactHeader);
        await Run("Winget parser uses terminal columns for long CJK names", WingetWideName);
        await Run("Winget parser handles combining marks and emoji", WingetUnicodeNames);
        await Run("Winget parser supports tables without source column", WingetNoSource);
        await Run("Winget parser deduplicates IDs across explicit-target tables", WingetSections);
        await Run("Winget parser rejects truncated actionable identifiers", WingetTruncatedTargets);
        await Run("Winget parser accepts empty or non-table output", WingetEmpty);
        await Run("Winget parser rejects a malformed first table row", WingetMalformedFirstRow);
        await Run("Winget parser rejects a malformed later table row", WingetMalformedLaterRow);
        await Run("Winget parser rejects shifted version and source fields", WingetMisalignedFields);
        await Run("Winget parser rejects ambiguous localized headings", WingetAmbiguousHeader);
        await Run("Winget parser rejects tables with no readable rows", WingetEmptyTable);
        await Run("Winget parser accepts the normal upgrade count summary", WingetCountSummary);
    }

    // Column widths are fixture facts. Unicode widths below are supplied explicitly,
    // independent of the production display-width algorithm.
    static string WingetHeader(string available = "Available", bool source = true) =>
        "Name".PadRight(44) + "Id".PadRight(26) + "Version".PadRight(16) + (source ? available.PadRight(24) + "Source" : available);
    static string WingetRow(string name, string id, string version, string available, string source = "winget", int? nameCells = null) =>
        name + new string(' ', 44 - (nameCells ?? name.Length)) + id.PadRight(26) + version.PadRight(16) +
        (source != null ? available.PadRight(24) + source : available);
    static string WingetTable(IEnumerable<string> rows, string header = null) =>
        (header ?? WingetHeader()) + "\r\n" + new string('-', 130) + "\r\n" + string.Join("\r\n", rows) + "\r\n";

    static Task WingetSpacedFields() => Sync(() =>
    {
        var apps = Winget.Parse(WingetTable(new[] { WingetRow("Example Desktop Client", "Example.Desktop", "1.2.0", "1.3.0", "Corporate Packages") }));
        Equal(1, apps.Count);
        Equal("Example Desktop Client", apps[0].Name); Equal("Example.Desktop", apps[0].Id);
        Equal("1.2.0", apps[0].Version); Equal("1.3.0", apps[0].Available); Equal("Corporate Packages", apps[0].Source);
        Check(!apps[0].Explicit, "First table was treated as requiring explicit targeting");
    });

    static Task WingetLocalizedHeader() => Sync(() =>
    {
        var apps = Winget.Parse(WingetTable(new[] { WingetRow("Outil bureau", "Example.Tool", "2.0", "2.1") }, WingetHeader("Version disponible")));
        Equal(1, apps.Count); Equal("Example.Tool", apps[0].Id); Equal("2.0", apps[0].Version); Equal("2.1", apps[0].Available);
        Equal("winget", apps[0].Source);
    });

    static Task WingetCompactHeader() => Sync(() =>
    {
        string row = "App  " + "XY " + "1.0".PadRight(8) + "2.0".PadRight(10) + "winget";
        var apps = Winget.Parse(WingetTable(new[] { row }, "Name Id Version Available Source"));
        Equal(1, apps.Count); Equal("App", apps[0].Name); Equal("XY", apps[0].Id);
        Equal("1.0", apps[0].Version); Equal("2.0", apps[0].Available); Equal("winget", apps[0].Source);
    });

    static Task WingetWideName() => Sync(() =>
    {
        string name = string.Concat(Enumerable.Repeat("東京", 10)); // Twenty CJK characters occupy forty terminal cells.
        var apps = Winget.Parse(WingetTable(new[] { WingetRow(name, "Example.Wide", "3.0", "4.0", "Company Source", 40) }));
        Equal(1, apps.Count); Equal(name, apps[0].Name); Equal("Example.Wide", apps[0].Id);
        Equal("3.0", apps[0].Version); Equal("4.0", apps[0].Available); Equal("Company Source", apps[0].Source);
    });

    static Task WingetUnicodeNames() => Sync(() =>
    {
        var apps = Winget.Parse(WingetTable(new[]
        {
            WingetRow("e\u0301ditor", "Example.Combining", "1", "2", "winget", 6),
            WingetRow("Rocket 🚀", "Example.Emoji", "1", "2", "winget", 9)
        }));
        Equal(2, apps.Count);
        Equal("e\u0301ditor", apps[0].Name); Equal("Example.Combining", apps[0].Id);
        Equal("Rocket 🚀", apps[1].Name); Equal("Example.Emoji", apps[1].Id);
        Check(apps.All(app => app.Version == "1" && app.Available == "2"), "Unicode names shifted version columns");
    });

    static Task WingetNoSource() => Sync(() =>
    {
        var apps = Winget.Parse(WingetTable(new[] { WingetRow("Local package", "Example.Local", "Unknown", "5.0", null) }, WingetHeader(source: false)));
        Equal(1, apps.Count); Equal("Example.Local", apps[0].Id); Equal("Unknown", apps[0].Version);
        Equal("5.0", apps[0].Available); Equal("", apps[0].Source);
    });

    static Task WingetSections() => Sync(() =>
    {
        string output = WingetTable(new[] { WingetRow("Example", "Example.App", "1", "2") }) +
            "\r\nPackages requiring explicit targeting:\r\n" +
            WingetTable(new[] { WingetRow("Example duplicate", "example.app", "1", "2"), WingetRow("Pinned Example", "Example.Pinned", "8", "9") });
        var apps = Winget.Parse(output);
        Equal(2, apps.Count); Equal("Example.App", apps[0].Id); Check(!apps[0].Explicit, "Duplicate overwrote first table metadata");
        Equal("Example.Pinned", apps[1].Id); Check(apps[1].Explicit, "Second-table package lost its explicit-target state");
    });

    static Task WingetTruncatedTargets() => Sync(() =>
    {
        foreach (string id in new[] { "Example.Truncated…", "Example.Truncated..." })
            Throws<InvalidOperationException>(() => Winget.Parse(WingetTable(new[] { WingetRow("Example", id, "1", "2") })));
        foreach (string source in new[] { "Corporate…", "Corporate..." })
            Throws<InvalidOperationException>(() => Winget.Parse(WingetTable(new[] { WingetRow("Example", "Example.App", "1", "2", source) })));
        var apps = Winget.Parse(WingetTable(new[] { WingetRow("Long application…", "Example.App", "1", "2") }));
        Equal(1, apps.Count); Equal("Example.App", apps[0].Id);
    });

    static Task WingetEmpty() => Sync(() =>
    {
        Equal(0, Winget.Parse(null).Count); Equal(0, Winget.Parse("").Count);
        Equal(0, Winget.Parse("No installed package found matching input criteria.\r\n").Count);
    });
    static Task WingetMalformedFirstRow() => Sync(() =>
    {
        Throws<InvalidOperationException>(() => Winget.Parse(WingetTable(new[] { "Example.App 1.0 2.0" })));
    });

    static Task WingetMalformedLaterRow() => Sync(() =>
    {
        string output = WingetTable(new[] { WingetRow("Example", "Example.App", "1", "2"), "Another row could not be read" });
        Throws<InvalidOperationException>(() => Winget.Parse(output));
    });

    static Task WingetMisalignedFields() => Sync(() =>
    {
        // Removing the last padding cell before Available shifts version/source values
        // into plausible but incorrect strings. IDs must not be acted on from this table.
        string row = WingetRow("Example", "Example.App", "1.0", "2.0");
        string shifted = row.Remove(85, 1);
        Throws<InvalidOperationException>(() => Winget.Parse(WingetTable(new[] { shifted })));
    });

    static Task WingetAmbiguousHeader() => Sync(() =>
    {
        foreach (string header in new[]
        {
            "Nom Id Version Version disponible Source",
            "Name".PadRight(44) + "Id".PadRight(26) + "Version".PadRight(16) + "Version disponible Source"
        })
            Throws<InvalidOperationException>(() => Winget.Parse(WingetTable(new[] { WingetRow("Example", "Example.App", "1", "2") }, header)));
    });

    static Task WingetEmptyTable() => Sync(() =>
    {
        Throws<InvalidOperationException>(() => Winget.Parse(WingetTable(Array.Empty<string>())));
        Throws<InvalidOperationException>(() => Winget.Parse(WingetTable(new[] { "0 upgrades available." })));
    });

    static Task WingetCountSummary() => Sync(() =>
    {
        string output = WingetTable(new[] { WingetRow("Example", "Example.App", "1", "2") }) + "1 upgrade available.\r\n";
        var apps = Winget.Parse(output);
        Equal(1, apps.Count); Equal("Example.App", apps[0].Id); Equal("2", apps[0].Available);
    });

}
