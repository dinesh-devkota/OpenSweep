using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenSweep.Core
{
    /// <summary>Only the facts deliberately collected for a local Windows care report.</summary>
    public sealed class WindowsCareSnapshot
    {
        public DateTimeOffset CapturedAt { get; }
        public string OperatingSystem { get; }
        public long? RamBytes { get; }
        public long? SystemDriveBytes { get; }
        public long? AvailableSystemDriveBytes { get; }
        public bool? IsAdministrator { get; }

        public WindowsCareSnapshot(DateTimeOffset capturedAt, string operatingSystem, long? ramBytes,
            long? systemDriveBytes, long? availableSystemDriveBytes, bool? isAdministrator)
        {
            CapturedAt = capturedAt;
            // Keep the OS caption on one line; no arbitrary errors or paths enter a report.
            var caption = new string((operatingSystem ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            OperatingSystem = caption.Length == 0 || caption.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                ? "Windows (version unavailable)" : caption;
            RamBytes = ramBytes > 0 ? ramBytes : null;
            SystemDriveBytes = systemDriveBytes > 0 ? systemDriveBytes : null;
            AvailableSystemDriveBytes = SystemDriveBytes.HasValue && availableSystemDriveBytes >= 0 &&
                availableSystemDriveBytes <= SystemDriveBytes ? availableSystemDriveBytes : null;
            IsAdministrator = isAdministrator;
        }

        public string RamText => RamBytes.HasValue ? Fmt.Bytes(RamBytes.Value) + " installed" : "Unavailable";
        public string AccessText => IsAdministrator.HasValue
            ? (IsAdministrator.Value ? "Administrator" : "Standard user") : "Unavailable";
        public double? AvailablePercentage => AvailableSystemDriveBytes.HasValue && SystemDriveBytes.HasValue
            ? AvailableSystemDriveBytes.Value * 100.0 / SystemDriveBytes.Value : (double?)null;
        public string DriveText => !SystemDriveBytes.HasValue ? "Unavailable"
            : !AvailableSystemDriveBytes.HasValue ? Fmt.Bytes(SystemDriveBytes.Value) + " total · free space unavailable"
            : Fmt.Bytes(AvailableSystemDriveBytes.Value) + " free of " + Fmt.Bytes(SystemDriveBytes.Value) +
                " (" + AvailablePercentage.Value.ToString("0.#", CultureInfo.CurrentCulture) + "% available)";

        public string ToReport()
        {
            var text = new StringBuilder();
            text.AppendLine("OpenSweep · Windows care snapshot");
            text.AppendLine("Captured (UTC): " + CapturedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
            text.AppendLine("Operating system: " + OperatingSystem);
            text.AppendLine("Installed memory: " + RawBytes(RamBytes));
            text.AppendLine("System drive capacity: " + RawBytes(SystemDriveBytes));
            text.AppendLine("System drive available space: " + RawBytes(AvailableSystemDriveBytes));
            text.AppendLine("OpenSweep access: " + AccessText);
            text.AppendLine();
            text.AppendLine("Scope: read-only system information, memory capacity and system-drive space.");
            text.AppendLine("No performance, malware, update, or system-file integrity scan was performed for this report.");
            text.AppendLine("User names, computer names, installed-app lists and license keys are not collected for this report.");
            return text.ToString();
        }

        static string RawBytes(long? value) => value.HasValue
            ? Fmt.Bytes(value.Value) + " (" + value.Value.ToString(CultureInfo.InvariantCulture) + " bytes)" : "Unavailable";
    }

    public sealed class WindowsCareAction
    {
        public string Label { get; }
        public string Key { get; }
        public bool IsExternal { get; }
        public string ToolTip => IsExternal ? "Opens Windows Settings or Microsoft's guide." : "Opens this tool in OpenSweep.";

        public WindowsCareAction(string label, string key, bool isExternal)
        {
            Label = label;
            Key = key;
            IsExternal = isExternal;
        }
    }

    public sealed class WindowsCareGuide
    {
        public string Glyph { get; }
        public string Title { get; }
        public string Description { get; }
        public string Advice { get; }
        public ReadOnlyCollection<WindowsCareAction> Actions { get; }

        public WindowsCareGuide(string glyph, string title, string description, string advice, params WindowsCareAction[] actions)
        {
            Glyph = glyph;
            Title = title;
            Description = description;
            Advice = advice;
            Actions = Array.AsReadOnly(actions);
        }
    }

    /// <summary>Fixed navigation targets; button data cannot become an arbitrary command or URI.</summary>
    public static class WindowsCareCatalog
    {
        // Microsoft documents these Settings URIs in:
        // https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app
        static readonly Dictionary<string, string> ExternalTargets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["startup-settings"] = "ms-settings:startupapps",
            ["storage-settings"] = "ms-settings:storagepolicies",
            ["notifications-settings"] = "ms-settings:notifications",
            ["active-hours-settings"] = "ms-settings:windowsupdate-activehours",
            ["update-settings"] = "ms-settings:windowsupdate",
            ["privacy-settings"] = "ms-settings:privacy",
            ["repair-guide"] = "https://support.microsoft.com/en-us/windows/using-system-file-checker-in-windows-365e0031-36b1-6031-f804-8fd86e0ef4ca",
            ["performance-guide"] = "https://support.microsoft.com/en-us/windows/tips-to-improve-pc-performance-in-windows-b3b3ef5b-5953-fb6a-2528-4bbed82fba96"
        };
        static readonly HashSet<string> InternalPages = new HashSet<string>(StringComparer.Ordinal)
            { "startup", "disk", "cleaner", "winupdate", "restore" };

        public static bool TryGetExternalTarget(string key, out string target)
        {
            target = null;
            return key != null && ExternalTargets.TryGetValue(key, out target);
        }

        public static bool IsInternalPage(string key) => key != null && InternalPages.Contains(key);

        public static ReadOnlyCollection<WindowsCareGuide> Guides { get; } = Array.AsReadOnly(new[]
        {
            new WindowsCareGuide("\uE7E8", "Slow startup", "Choose which apps need to start when you sign in.",
                "Disable nonessential startup apps one at a time, then restart and compare. Keep security tools enabled and leave unfamiliar entries alone until you identify them.",
                new WindowsCareAction("Review startup apps", "startup", false),
                new WindowsCareAction("Startup settings", "startup-settings", true)),
            new WindowsCareGuide("\uEDA2", "Low disk space", "Find large folders before deciding what to remove.",
                "Use Disk Analyzer to locate large files, then review a Custom Clean analysis. Storage Sense can manage temporary files; check its retention settings before turning it on.",
                new WindowsCareAction("Analyze disk space", "disk", false),
                new WindowsCareAction("Custom Clean", "cleaner", false),
                new WindowsCareAction("Storage Sense", "storage-settings", true)),
            new WindowsCareGuide("\uE7F4", "Too many interruptions", "Adjust notifications for the apps that interrupt you.",
                "In Windows Settings, review each app's notification permissions, banners and sounds. Keep alerts you rely on, such as security and calendar reminders.",
                new WindowsCareAction("Notification settings", "notifications-settings", true)),
            new WindowsCareGuide("\uE895", "Updates and restart timing", "Keep updates moving while choosing a better time to restart.",
                "Review pending updates and set active hours around your normal schedule. Windows may still require a restart; save your work before choosing one. Your organisation can manage these settings.",
                new WindowsCareAction("Review Windows updates", "winupdate", false),
                new WindowsCareAction("Set active hours", "active-hours-settings", true),
                new WindowsCareAction("Windows Update settings", "update-settings", true)),
            new WindowsCareGuide("\uE72E", "App permissions", "Review what apps can access in Windows privacy settings.",
                "Check the relevant privacy category, such as location, camera or microphone, and change access only for apps you recognise. Available controls differ by app and Windows version.",
                new WindowsCareAction("Privacy settings", "privacy-settings", true)),
            new WindowsCareGuide("\uE90F", "Windows errors or crashes", "Use Microsoft's repair guidance when Windows is not working correctly.",
                "Install pending updates and restart first. Microsoft's guide explains DISM followed by System File Checker in an administrator terminal. These checks can take time; read the guide before you begin.",
                new WindowsCareAction("Open Microsoft repair guide", "repair-guide", true),
                new WindowsCareAction("System Restore tools", "restore", false))
        });
    }
}
