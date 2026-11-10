using System.Globalization;
using OpenSweep.Core;

internal static partial class Program
{
    static async Task WindowsCareTests()
    {
        await Run("Windows care snapshot represents unavailable facts honestly", CareUnknown);
        await Run("Windows care snapshot rejects invalid drive capacities", CareInvalidCapacity);
        await Run("Windows care snapshot preserves zero free space and large capacities", CareDriveRange);
        await Run("Windows care report uses UTC and invariant raw byte values", CareReport);
        await Run("Windows care report prevents caption line injection", CareCaption);
        await Run("Windows care navigation permits only declared targets", CareNavigation);
    }

    static WindowsCareSnapshot Snapshot(long? capacity = null, long? free = null, long? memory = null, bool? admin = null, string os = null) =>
        new WindowsCareSnapshot(new DateTimeOffset(2026, 11, 3, 12, 34, 56, TimeSpan.FromHours(-6)), os, memory, capacity, free, admin);

    static Task CareUnknown() => Sync(() =>
    {
        var snapshot = Snapshot();
        Equal("Windows (version unavailable)", snapshot.OperatingSystem);
        Check(snapshot.RamBytes == null && snapshot.SystemDriveBytes == null && snapshot.AvailableSystemDriveBytes == null, "Unknown measurements were fabricated");
        Equal("Unavailable", snapshot.RamText); Equal("Unavailable", snapshot.DriveText); Equal("Unavailable", snapshot.AccessText);
        Check(snapshot.AvailablePercentage == null, "Unknown drive was assigned an availability percentage");
        Equal("Standard user", Snapshot(admin: false).AccessText);
        Equal("Administrator", Snapshot(admin: true).AccessText);
    });

    static Task CareInvalidCapacity() => Sync(() =>
    {
        foreach (long capacity in new[] { 0L, -1L })
        {
            var snapshot = Snapshot(capacity, 0, -1);
            Check(snapshot.SystemDriveBytes == null && snapshot.AvailableSystemDriveBytes == null && snapshot.RamBytes == null, "Invalid capacities were accepted");
        }
        Check(Snapshot(100, -1).AvailableSystemDriveBytes == null, "Negative free space was accepted");
        Check(Snapshot(100, 101).AvailableSystemDriveBytes == null, "Free space exceeded drive capacity");
        Check(Snapshot(null, 10).AvailableSystemDriveBytes == null, "Free space without a drive capacity was treated as verified");
        var partial = Snapshot(100, null);
        Equal(100L, partial.SystemDriveBytes.Value);
        Check(partial.DriveText.Contains("free space unavailable"), "Partial disk data was shown as fully available");
    });

    static Task CareDriveRange() => Sync(() =>
    {
        var full = Snapshot(100, 0, 8192);
        Equal(0L, full.AvailableSystemDriveBytes.Value); Equal(0.0, full.AvailablePercentage.Value);
        var empty = Snapshot(100, 100); Equal(100.0, empty.AvailablePercentage.Value);
        var large = Snapshot(long.MaxValue, long.MaxValue / 2);
        Check(large.AvailablePercentage >= 49.9 && large.AvailablePercentage <= 50.1, "Large drive percentage overflowed");
    });

    static Task CareReport() => Sync(() =>
    {
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            string text = Snapshot(1099511627776, 536870912000, 17179869184, false, "Windows 11 Pro").ToReport();
            Check(text.Contains("2026-11-03 18:34:56 UTC"), "Report timestamp did not convert to UTC");
            foreach (string value in new[] { "1099511627776 bytes", "536870912000 bytes", "17179869184 bytes" })
                Check(text.Contains(value), "Invariant raw measurement missing: " + value);
            Check(text.Contains("Operating system: Windows 11 Pro"), "OS fact was omitted");
            Check(text.Contains("SqueakySift access: Standard user"), "Access state was omitted");
            Check(text.Contains("read-only system information"), "Report did not describe its limited scope");
            Check(text.Contains("No performance, malware, update, or system-file integrity scan was performed"), "Read-only snapshot was presented as an actual diagnostic scan");
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
    });

    static Task CareCaption() => Sync(() =>
    {
        Equal("Windows (version unavailable)", Snapshot(os: " \tWindows\r\n").OperatingSystem);
        var snapshot = Snapshot(os: " Windows 11\r\nInjected:\tvalue\0 ");
        Check(!snapshot.OperatingSystem.Any(char.IsControl), "Control characters survived caption normalization");
        Check(!snapshot.ToReport().Contains(Environment.NewLine + "Injected:"), "Caption inserted a new report field");
    });

    static Task CareNavigation() => Sync(() =>
    {
        Check(WindowsCareCatalog.TryGetExternalTarget("startup-settings", out string startup), "Startup settings action did not resolve");
        Equal("ms-settings:startupapps", startup);
        foreach (var guide in WindowsCareCatalog.Guides)
        {
            Check(guide.Actions.Count > 0, "A guide has no action");
            foreach (var action in guide.Actions)
            {
                if (!action.IsExternal) { Check(WindowsCareCatalog.IsInternalPage(action.Key), "Unknown internal guide page"); continue; }
                Check(WindowsCareCatalog.TryGetExternalTarget(action.Key, out string target), "Unknown external guide target");
                var uri = new Uri(target);
                Check(uri.Scheme == "ms-settings" || (uri.Scheme == "https" && uri.Host == "support.microsoft.com"), "Guide target left the supported Windows/Microsoft routes");
            }
        }
        foreach (string key in new[] { null, "", "cmd.exe", "https://example.com/", "ms-settings:privacy", "../startup", "STARTUP", "startup;shutdown" })
        {
            Check(!WindowsCareCatalog.TryGetExternalTarget(key, out var target) && target == null, "Arbitrary key resolved externally: " + key);
            Check(!WindowsCareCatalog.IsInternalPage(key), "Arbitrary key resolved internally: " + key);
        }
    });
}
