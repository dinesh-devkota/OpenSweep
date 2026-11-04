using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSweep.Core
{
    public sealed class WinUpdate : ObservableObject
    {
        public string UpdateId { get; set; }
        public int Revision { get; set; }
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

        UpdateOutcome _outcome;
        UpdatePhase _phase;
        int _attempts;
        bool _restartRequired;
        public UpdateOutcome Outcome { get => _outcome; set { if (Set(ref _outcome, value)) Raise(nameof(CanRetry)); } }
        public UpdatePhase Phase { get => _phase; set { if (Set(ref _phase, value)) Raise(nameof(PhaseText)); } }
        public string PhaseText => UpdatePhases.Describe(Phase);
        public int Attempts { get => _attempts; set => Set(ref _attempts, value); }
        public bool RestartRequired { get => _restartRequired; set { if (Set(ref _restartRequired, value)) Raise(nameof(CanRetry)); } }
        public bool CanRetry => Outcome == UpdateOutcome.Failed && !RestartRequired;

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
                s.ClientApplicationID = "OpenSweep";
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
                    UpdateId = (string)u.Identity.UpdateID,
                    Revision = (int)u.Identity.RevisionNumber,
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

        /// <summary>
        /// WUA synchronous calls finish their current stage. Cancellation is honored before
        /// downloading and again before installation; an active installation is never killed.
        /// </summary>
        public static Task<UpdateResult> InstallOneAsync(WinUpdate update, CancellationToken ct,
            IProgress<UpdatePhase> progress = null) => Task.Run(() =>
        {
            bool reboot = false, installationStarted = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                dynamic u = update.Com;
                if (!(bool)u.EulaAccepted) u.AcceptEula();
                dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true));
                coll.Add(u);
                if (!(bool)u.IsDownloaded)
                {
                    progress?.Report(UpdatePhase.Downloading);
                    dynamic downloader = Session().CreateUpdateDownloader();
                    downloader.Updates = coll;
                    dynamic download = downloader.Download();
                    if (ct.IsCancellationRequested)
                        return new UpdateResult(UpdateOutcome.Cancelled, "Cancelled after download; installation was not started");
                    dynamic result = download.GetUpdateResult(0);
                    if ((int)result.ResultCode != 2 || !(bool)u.IsDownloaded)
                        return new UpdateResult(UpdateOutcome.Failed, $"Download failed (0x{(int)result.HResult:X8})", false, (int)result.HResult);
                }
                ct.ThrowIfCancellationRequested();
                dynamic installer = Session().CreateUpdateInstaller();
                installer.Updates = coll;
                ct.ThrowIfCancellationRequested();
                progress?.Report(UpdatePhase.Installing);
                installationStarted = true;
                dynamic installed = installer.Install();
                // Read the reboot flag first so a later per-update query failure cannot erase it.
                reboot = (bool)installed.RebootRequired;
                dynamic updateResult = installed.GetUpdateResult(0);
                return UpdateResult.FromWindowsUpdate((int)updateResult.ResultCode, (int)updateResult.HResult, reboot);
            }
            catch (OperationCanceledException)
            { return new UpdateResult(UpdateOutcome.Cancelled, "Cancelled before installation", reboot); }
            catch (COMException ex)
            { return new UpdateResult(installationStarted ? UpdateOutcome.Unknown : UpdateOutcome.Failed,
                (installationStarted ? "Installation completion unknown — " : "Failed — ") + Describe(ex.HResult), reboot || RebootRequired, ex.HResult); }
            catch (Exception ex)
            { return new UpdateResult(installationStarted ? UpdateOutcome.Unknown : UpdateOutcome.Failed,
                (installationStarted ? "Installation completion unknown — " : "Failed — ") + ex.Message, reboot || RebootRequired); }
        });

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
