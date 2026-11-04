using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSweep.Core
{
    public sealed class SelfUpdateReadiness
    {
        public bool CanInstall { get; }
        public string Message { get; }
        internal SelfUpdateReadiness(bool canInstall, string message) { CanInstall = canInstall; Message = message; }
    }

    public sealed class SelfUpdateProgress
    {
        public long BytesReceived { get; }
        public long TotalBytes { get; }
        public double Percentage => TotalBytes > 0 ? BytesReceived * 100.0 / TotalBytes : 0;
        internal SelfUpdateProgress(long received, long total) { BytesReceived = received; TotalBytes = total; }
    }

    public sealed class SelfUpdateDownload
    {
        public SelfUpdateRelease Release { get; }
        public SelfUpdateAsset Asset { get; }
        public string FilePath { get; }
        internal SelfUpdateDownload(SelfUpdateRelease release, SelfUpdateAsset asset, string filePath)
        { Release = release; Asset = asset; FilePath = filePath; }
    }

    public static class SelfUpdateService
    {
        public static Version CurrentVersion
        {
            get
            {
                var v = typeof(SelfUpdateService).Assembly.GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static async Task<SelfUpdateRelease> CheckAsync(CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var client = CreateClient())
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    using (var response = await client.GetAsync(SelfUpdatePolicy.ReleasesApi, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        if (IsRedirect(response.StatusCode)) throw new InvalidDataException("The official release API redirected unexpectedly.");
                        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                            throw new InvalidOperationException("GitHub is limiting update checks. Try later or open the official releases page.");
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > SelfUpdatePolicy.MaxFeedBytes)
                            throw new InvalidDataException("The release response exceeds the size limit.");
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var bytes = new MemoryStream())
                        {
                            await CopyBoundedAsync(input, bytes, SelfUpdatePolicy.MaxFeedBytes, null, timeout.Token).ConfigureAwait(false);
                            return SelfUpdatePolicy.ParseReleaseFeed(bytes.ToArray());
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new TimeoutException("The update check timed out. Try again later."); }
            }
        }

        public static Task<SelfUpdateReadiness> GetInstallationReadinessAsync() => Task.Run(() =>
        {
            try
            {
                using (var current = OpenLocked(AppPaths.ExePath)) SelfUpdateAuthenticode.GetTrustedPublicKey(current.Stream);
                return new SelfUpdateReadiness(true, "Downloads must match GitHub's SHA-256 digest and the trusted signing key of this OpenSweep build.");
            }
            catch
            {
                return new SelfUpdateReadiness(false,
                    "Verified installation is unavailable: this OpenSweep build is unsigned, or Windows cannot establish its signature trust. " +
                    "A trusted signed OpenSweep installation is required first. There is no built-in trusted signing key to bootstrap an unsigned build. " +
                    "You can review the official release page manually; a missing or mismatched signature cannot be overridden here.");
            }
        });

        public static async Task<SelfUpdateDownload> DownloadAsync(SelfUpdateRelease release, SelfUpdateAsset asset,
            IProgress<SelfUpdateProgress> progress, CancellationToken cancellationToken)
        {
            if (release == null || asset == null || !release.Assets.Contains(asset) ||
                !SelfUpdatePolicy.IsNewer(release.Version, CurrentVersion)) throw new InvalidOperationException("Choose an asset from a newer official release.");
            string trustedKey;
            using (var current = OpenLocked(AppPaths.ExePath))
                trustedKey = await Task.Run(() => SelfUpdateAuthenticode.GetTrustedPublicKey(current.Stream), cancellationToken).ConfigureAwait(false);
            // Release source-path locks before waiting for the network. Launch revalidates both files.
            string directory = Path.Combine(AppPaths.DataDir, "Updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, asset.Name);
            bool complete = false;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (var client = CreateClient())
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    try
                    {
                        using (var response = await GetDownloadResponseAsync(client, asset.DownloadUri, timeout.Token).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != asset.Size)
                                throw new InvalidDataException("The download size does not match the release metadata.");
                            using (var securedDirectory = new SelfUpdateDirectoryLock(directory, protectAncestors: false))
                            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, true))
                            {
                                long received = await CopyBoundedAsync(input, output, asset.Size, progress, timeout.Token).ConfigureAwait(false);
                                if (received != asset.Size) throw new InvalidDataException("The download ended before the expected size was received.");
                                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                                output.Flush(true);
                                timeout.Token.ThrowIfCancellationRequested();
                            }
                            // Reopen without write access so Windows can inspect the signature under a stable read lock.
                            using (var artifact = OpenLocked(path))
                                await Task.Run(() => VerifyArtifact(artifact.Stream, release, asset, trustedKey), timeout.Token).ConfigureAwait(false);
                            timeout.Token.ThrowIfCancellationRequested();
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { throw new TimeoutException("The download timed out."); }
                }
                complete = true;
                return new SelfUpdateDownload(release, asset, path);
            }
            finally
            {
                if (!complete)
                {
                    try
                    {
                        // Never follow a changed/reparse directory path while cleaning up a failed download.
                        using (var cleanupPath = new SelfUpdateDirectoryLock(directory)) File.Delete(path);
                        // Retain the empty private cache folder: a path-only directory delete after unlocking can race a rename.
                    }
                    catch (Exception ex) { Log.Write("Update download cleanup failed: " + ex.GetType().Name); }
                }
            }
        }

        /// <summary>Reverify under locks immediately before the user-approved visible installer launch.</summary>
        public static Task<int> LaunchInstallerAsync(SelfUpdateDownload download, Action onLaunched, CancellationToken cancellationToken) => Task.Run(() =>
        {
            if (download == null || !download.Asset.IsInstaller) throw new InvalidOperationException("This download is not an installer.");
            cancellationToken.ThrowIfCancellationRequested();
            using (var artifact = OpenLocked(download.FilePath))
            {
                Process process;
                using (var current = OpenLocked(AppPaths.ExePath))
                {
                    var trustedKey = SelfUpdateAuthenticode.GetTrustedPublicKey(current.Stream);
                    VerifyArtifact(artifact.Stream, download.Release, download.Asset, trustedKey);
                    cancellationToken.ThrowIfCancellationRequested();
                    // No silent flags or elevation bypass; the installer owns its normal UI/UAC flow.
                    process = Process.Start(new ProcessStartInfo(download.FilePath) { UseShellExecute = true });
                    if (process == null) throw new InvalidOperationException("Windows did not start the installer.");
                }
                using (process)
                {
                    // An observer must not release the verification locks while the installer is still running.
                    try { onLaunched?.Invoke(); }
                    catch (Exception ex) { Log.Write("Update launch observer failed: " + ex.GetType().Name); }
                    // Keep the artifact and its parent paths locked while the installer reads its overlay.
                    // The current app is unlocked so the installer can replace it after the app closes.
                    // Cancellation never kills an installer or implies rollback.
                    process.WaitForExit();
                    return process.ExitCode;
                }
            }
        }, cancellationToken);

        public static Task VerifyPortableAsync(SelfUpdateDownload download) => Task.Run(() =>
        {
            if (download == null || download.Asset.IsInstaller) throw new InvalidOperationException("This is not a portable download.");
            using (var current = OpenLocked(AppPaths.ExePath))
            using (var artifact = OpenLocked(download.FilePath))
                VerifyArtifact(artifact.Stream, download.Release, download.Asset, SelfUpdateAuthenticode.GetTrustedPublicKey(current.Stream));
        });

        static void VerifyArtifact(FileStream file, SelfUpdateRelease release, SelfUpdateAsset asset, string trustedKey)
        {
            if (file.Length != asset.Size || !SelfUpdatePolicy.VerifySha256(file, asset.Sha256))
                throw new InvalidDataException("The downloaded file failed GitHub SHA-256 verification. It cannot be installed.");
            string artifactKey = SelfUpdateAuthenticode.GetTrustedPublicKey(file);
            var metadata = SelfUpdateMetadata.Read(file);
            if (!SelfUpdatePolicy.MetadataMatchesRelease(metadata.ProductName, metadata.FileVersion, release.Version, CurrentVersion))
                throw new InvalidDataException("The signed file's product or version does not match the newer OpenSweep release. It cannot be installed.");
            if (!SelfUpdatePolicy.IsTrustedSignerMatch(true, trustedKey, true, artifactKey))
                throw new InvalidDataException("The download's trusted signer does not match this OpenSweep build. It cannot be installed.");
        }

        static SelfUpdateFileLock OpenLocked(string path) => new SelfUpdateFileLock(path);
        static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None });
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenSweep/" + CurrentVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return client;
        }

        static async Task<HttpResponseMessage> GetDownloadResponseAsync(HttpClient client, Uri initial, CancellationToken ct)
        {
            var next = initial;
            int redirects = 0;
            if (!SelfUpdatePolicy.IsAllowedDownloadUri(next, initial)) throw new InvalidDataException("The download URL is not an official release asset.");
            while (true)
            {
                var response = await client.GetAsync(next, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode)) return response;
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new InvalidDataException("The download redirect has no destination.");
                next = location.IsAbsoluteUri ? location : new Uri(next, location);
                if (!SelfUpdatePolicy.IsAllowedRedirect(next, initial, ++redirects))
                    throw new InvalidDataException("The download redirected outside the approved GitHub asset hosts or too many times.");
            }
        }
        static bool IsRedirect(HttpStatusCode status) => (int)status == 301 || (int)status == 302 || (int)status == 303 || (int)status == 307 || (int)status == 308;

        static async Task<long> CopyBoundedAsync(Stream input, Stream output, long limit, IProgress<SelfUpdateProgress> progress, CancellationToken ct)
        {
            var buffer = new byte[65536];
            long total = 0;
            using (ct.Register(() => { try { input.Dispose(); } catch { } }))
            {
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        int read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                        if (read == 0) return total;
                        if (total > limit - read || total > SelfUpdatePolicy.MaxDownloadBytes - read)
                            throw new InvalidDataException("The response exceeds the permitted size.");
                        await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        total += read;
                        progress?.Report(new SelfUpdateProgress(total, limit));
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            }
        }
    }
}
