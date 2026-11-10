using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSweep.Core
{
    public sealed class WinUpdate : ObservableObject
    {
        public string Title { get; set; }
        public string Kb { get; set; }
        public string Category { get; set; }
        public long Size { get; set; }
        public string SizeText => Size > 0 ? Fmt.Bytes(Size) : "";
        public bool IsDriver { get; set; }
        public bool IsOptional { get; set; }
        internal object Com { get; set; }

        bool _checked = true;
        public bool IsChecked { get => _checked; set => Set(ref _checked, value); }

        string _status = "";
        public string Status { get => _status; set => Set(ref _status, value); }
    }

    /// <summary>Uses the built-in Windows Update Agent COM API (the same engine Settings › Windows Update uses).</summary>
    public static class WindowsUpdateService
    {
        static object _session;

        static dynamic Session()
        {
            if (_session == null)
            {
                dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true));
                s.ClientApplicationID = "SqueakySift";
                _session = s;
            }
            return _session;
        }

        public static Task<List<WinUpdate>> SearchAsync(bool microsoftUpdateOnline, CancellationToken ct) => Task.Run(() =>
        {
            dynamic searcher = Session().CreateUpdateSearcher();
            if (microsoftUpdateOnline) searcher.ServerSelection = 2; // ssWindowsUpdate: skip WSUS / policy server
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
            ct.ThrowIfCancellationRequested();
            // A partial or failed search must not become a misleading "Windows is up to date" result.
            if ((int)result.ResultCode != 2)
                throw new InvalidOperationException("Windows Update did not complete the search successfully. Please try again.");
            var list = new List<WinUpdate>();
            foreach (dynamic u in result.Updates)
            {
                ct.ThrowIfCancellationRequested();
                var kbs = new List<string>();
                foreach (dynamic kb in u.KBArticleIDs) kbs.Add("KB" + (string)kb);
                string category = null;
                foreach (dynamic c in u.Categories)
                    if ((string)c.Type == "UpdateClassification") { category = (string)c.Name; break; }
                bool driver = (int)u.Type == 2;
                bool optional = (bool)u.BrowseOnly;
                list.Add(new WinUpdate
                {
                    Title = (string)u.Title,
                    Kb = string.Join(", ", kbs),
                    Category = driver ? "Driver" : category ?? "Update",
                    Size = Convert.ToInt64(u.MaxDownloadSize),
                    IsDriver = driver,
                    IsOptional = optional,
                    IsChecked = !optional,
                    Com = u,
                });
            }
            return list;
        }, ct);

        /// <summary>Downloads and installs each update in turn. Returns true when a restart is required.</summary>
        public static Task<bool> InstallAsync(IList<WinUpdate> updates, CancellationToken ct) => Task.Run(() =>
        {
            bool reboot = false;
            foreach (var w in updates)
            {
                if (ct.IsCancellationRequested) { w.Status = "Cancelled"; continue; }
                try
                {
                    dynamic u = w.Com;
                    if (!(bool)u.EulaAccepted) u.AcceptEula();
                    dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true));
                    coll.Add(u);
                    if (!(bool)u.IsDownloaded)
                    {
                        w.Status = "Downloading…";
                        dynamic downloader = Session().CreateUpdateDownloader();
                        downloader.Updates = coll;
                        dynamic dr = downloader.Download();
                        // Download() is synchronous: honor a request made while it was running
                        // before beginning any installation. Keep earlier reboot requirements.
                        if (ct.IsCancellationRequested) { w.Status = "Cancelled"; continue; }
                        dynamic downloadResult = dr.GetUpdateResult(0);
                        if ((int)downloadResult.ResultCode != 2 || !(bool)u.IsDownloaded)
                        {
                            w.Status = $"Download failed (0x{(int)downloadResult.HResult:X8})";
                            Log.Write($"Windows Update '{w.Title}': {w.Status}");
                            continue;
                        }
                    }
                    w.Status = "Installing…";
                    dynamic installer = Session().CreateUpdateInstaller();
                    installer.Updates = coll;
                    if (ct.IsCancellationRequested) { w.Status = "Cancelled"; continue; }
                    dynamic ir = installer.Install();
                    // The aggregate result can be SucceededWithErrors. Check this update's own result.
                    dynamic updateResult = ir.GetUpdateResult(0);
                    int rc = (int)updateResult.ResultCode;
                    bool needs = (bool)ir.RebootRequired;
                    reboot |= needs;
                    w.Status = rc == 2 ? (needs ? "Installed – restart required" : "Installed")
                        : $"Failed (0x{(int)updateResult.HResult:X8})";
                }
                catch (COMException ex) { w.Status = "Failed: " + Describe(ex.HResult); }
                catch (Exception ex) { w.Status = "Failed: " + ex.Message; }
                Log.Write($"Windows Update '{w.Title}': {w.Status}");
            }
            return reboot;
        }, ct);

        public static string Describe(int hr)
        {
            switch ((uint)hr)
            {
                case 0x80240044: return "administrator rights are required";
                case 0x8024402C: case 0x80072EE7: case 0x80072EFD: return "cannot reach the update server (check network/proxy)";
                case 0x80240016: return "another installation is in progress";
                case 0x8024001E: return "the Windows Update service is shutting down";
                case 0x80070422: return "the Windows Update service is disabled";
                default: return $"error 0x{hr:X8}";
            }
        }

        public static bool RebootRequired
        {
            get
            {
                try
                {
                    dynamic si = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo", true));
                    return (bool)si.RebootRequired;
                }
                catch { return false; }
            }
        }

        public static void OpenSettings() => ShellOps.Open("ms-settings:windowsupdate");
    }
}
