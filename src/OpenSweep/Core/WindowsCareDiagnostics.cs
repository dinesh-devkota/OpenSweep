using System;

namespace OpenSweep.Core
{
    public static class WindowsCareDiagnostics
    {
        /// <summary>Read only. Unavailable values stay unknown instead of becoming successful checks.</summary>
        public static WindowsCareSnapshot Capture()
        {
            string os = null;
            long? memory = null, capacity = null, available = null;
            bool? administrator = null;
            // MainWindow loads SystemInfo once. Reuse its OS and RAM values rather than
            // starting duplicate WMI queries on every navigation or Refresh click.
            try { os = SystemInfo.OsName; memory = SystemInfo.RamBytes; } catch { }
            try
            {
                var drive = SystemInfo.SystemDrive;
                if (drive.IsReady)
                {
                    capacity = drive.TotalSize;
                    available = drive.AvailableFreeSpace;
                }
            }
            catch { }
            try { administrator = Elevation.IsAdmin; } catch { }
            return new WindowsCareSnapshot(DateTimeOffset.Now, os, memory, capacity, available, administrator);
        }
    }
}
