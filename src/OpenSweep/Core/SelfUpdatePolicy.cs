using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OpenSweep.Core
{
    public sealed class SelfUpdateAsset
    {
        public string Name { get; }
        public long Size { get; }
        public Uri DownloadUri { get; }
        public string Sha256 { get; }
        public bool IsInstaller => Name == "OpenSweep-Setup.exe";
        public string DisplayName => IsInstaller ? "Installer · " + Fmt.Bytes(Size) : "Portable app · " + Fmt.Bytes(Size);
        internal SelfUpdateAsset(string name, long size, Uri uri, string sha256)
        { Name = name; Size = size; DownloadUri = uri; Sha256 = sha256; }
    }

    public sealed class SelfUpdateRelease
    {
        public string Tag { get; }
        public Version Version { get; }
        public string Notes { get; }
        public Uri ReleaseUri { get; }
        public ReadOnlyCollection<SelfUpdateAsset> Assets { get; }
        internal SelfUpdateRelease(string tag, Version version, string notes, IEnumerable<SelfUpdateAsset> assets)
        {
            Tag = tag; Version = version; Notes = notes;
            ReleaseUri = new Uri(SelfUpdatePolicy.RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tag));
            Assets = Array.AsReadOnly(assets.ToArray());
        }
    }

    /// <summary>Pure release, URL and trust decisions shared by checking, downloading and launching.</summary>
    public static class SelfUpdatePolicy
    {
        public const string RepositoryUrl = "https://github.com/dinesh-devkota/OpenSweep";
        public const string ReleasesApi = "https://api.github.com/repos/dinesh-devkota/OpenSweep/releases?per_page=30";
        public const string LatestReleaseUrl = RepositoryUrl + "/releases/latest";
        public const int MaxFeedBytes = 1024 * 1024;
        public const long MaxDownloadBytes = 128L * 1024 * 1024;
        public const int MaxRedirects = 5;
        static readonly Regex StableTag = new Regex(@"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant);
        static readonly Regex Hash = new Regex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);

        public static bool TryParseStableVersion(string tag, out Version version)
        {
            version = null;
            if (tag == null || tag.Length > 128) return false;
            var m = StableTag.Match(tag);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
                !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
                !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch)) return false;
            version = new Version(major, minor, patch);
            return true;
        }

        public static bool IsNewer(Version candidate, Version current) => candidate != null && current != null &&
            new Version(candidate.Major, candidate.Minor, Math.Max(0, candidate.Build)) >
            new Version(current.Major, current.Minor, Math.Max(0, current.Build));

        public static bool MetadataMatchesRelease(string productName, Version fileVersion, Version releaseVersion, Version currentVersion) =>
            string.Equals(productName, "OpenSweep", StringComparison.Ordinal) && fileVersion != null && releaseVersion != null &&
            fileVersion.Major == releaseVersion.Major && fileVersion.Minor == releaseVersion.Minor &&
            fileVersion.Build == releaseVersion.Build && (fileVersion.Revision == 0 || fileVersion.Revision == -1) &&
            IsNewer(releaseVersion, currentVersion);

        public static bool TryParseSha256(string digest, out string hex)
        {
            hex = null;
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !Hash.IsMatch(digest.Substring(7))) return false;
            hex = digest.Substring(7).ToLowerInvariant();
            return true;
        }

        public static bool VerifySha256(Stream stream, string expectedHex)
        {
            if (stream == null || !stream.CanRead || !stream.CanSeek || expectedHex == null || !Hash.IsMatch(expectedHex)) return false;
            stream.Position = 0;
            byte[] actual;
            using (var sha = SHA256.Create()) actual = sha.ComputeHash(stream);
            stream.Position = 0;
            int difference = 0;
            for (int i = 0; i < actual.Length; i++) difference |= actual[i] ^ Convert.ToByte(expectedHex.Substring(i * 2, 2), 16);
            return difference == 0;
        }

        public static bool IsTrustedSignerMatch(bool currentTrusted, string currentKey, bool assetTrusted, string assetKey) =>
            currentTrusted && assetTrusted && currentKey != null && assetKey != null && Hash.IsMatch(currentKey) && Hash.IsMatch(assetKey) &&
            string.Equals(currentKey, assetKey, StringComparison.OrdinalIgnoreCase);

        static bool IsSecureUri(Uri uri) => uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

        static bool IsCanonicalAssetUri(Uri uri)
        {
            if (!IsSecureUri(uri) || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || uri.Query.Length != 0) return false;
            var segments = uri.AbsolutePath.Split('/');
            if (segments.Length != 7 || segments[1] != "dinesh-devkota" || segments[2] != "OpenSweep" || segments[3] != "releases" || segments[4] != "download") return false;
            string tag = Uri.UnescapeDataString(segments[5]);
            string name = segments[6];
            if (!TryParseStableVersion(tag, out _) || (name != "OpenSweep.exe" && name != "OpenSweep-Setup.exe")) return false;
            return string.Equals(uri.AbsoluteUri, RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + name, StringComparison.Ordinal);
        }

        public static bool IsAllowedDownloadUri(Uri candidate, Uri initialDownloadUri)
        {
            if (!IsSecureUri(candidate) || !IsCanonicalAssetUri(initialDownloadUri)) return false;
            if (string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return string.Equals(candidate.AbsoluteUri, initialDownloadUri.AbsoluteUri, StringComparison.Ordinal);
            return string.Equals(candidate.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Host, "github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAllowedRedirect(Uri candidate, Uri initialDownloadUri, int redirectsFollowed) =>
            redirectsFollowed > 0 && redirectsFollowed <= MaxRedirects && IsAllowedDownloadUri(candidate, initialDownloadUri);

        public static SelfUpdateRelease ParseReleaseFeed(byte[] json)
        {
            if (json == null || json.Length == 0 || json.Length > MaxFeedBytes) throw new InvalidDataException("The release response is empty or exceeds the size limit.");
            List<ReleaseDto> releases;
            using (var input = new MemoryStream(json, false))
            {
                var serializer = new DataContractJsonSerializer(typeof(List<ReleaseDto>), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 12000 });
                releases = (List<ReleaseDto>)serializer.ReadObject(input);
            }
            if (releases == null || releases.Count > 30) throw new InvalidDataException("The release response has an unexpected number of entries.");
            SelfUpdateRelease newest = null;
            foreach (var release in releases)
            {
                if (release == null || release.Draft || release.Prerelease || !TryParseStableVersion(release.Tag, out var version) ||
                    !DateTimeOffset.TryParse(release.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)) continue;
                if (newest != null && !IsNewer(version, newest.Version)) continue;
                var assets = new List<SelfUpdateAsset>();
                foreach (var asset in release.Assets ?? new List<AssetDto>())
                {
                    if (asset == null || asset.State != "uploaded" || (asset.Name != "OpenSweep.exe" && asset.Name != "OpenSweep-Setup.exe") ||
                        asset.Size <= 0 || asset.Size > MaxDownloadBytes || !TryParseSha256(asset.Digest, out var hash)) continue;
                    var canonical = new Uri(RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(release.Tag) + "/" + Uri.EscapeDataString(asset.Name));
                    if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || !IsSecureUri(uri) ||
                        !string.Equals(uri.AbsoluteUri, canonical.AbsoluteUri, StringComparison.Ordinal) || assets.Any(a => a.Name == asset.Name)) continue;
                    assets.Add(new SelfUpdateAsset(asset.Name, asset.Size, canonical, hash));
                }
                string notes = release.Body ?? "No release notes were provided.";
                if (notes.Length > 32768) notes = notes.Substring(0, 32768) + "\n\n[Notes shortened. Read the full release on GitHub.]";
                newest = new SelfUpdateRelease(release.Tag, version, notes, assets.OrderByDescending(a => a.IsInstaller));
            }
            return newest;
        }

        [DataContract] sealed class ReleaseDto
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
            [DataMember(Name = "published_at")] public string PublishedAt { get; set; }
            [DataMember(Name = "body")] public string Body { get; set; }
            [DataMember(Name = "assets")] public List<AssetDto> Assets { get; set; }
        }
        [DataContract] sealed class AssetDto
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
            [DataMember(Name = "state")] public string State { get; set; }
            [DataMember(Name = "browser_download_url")] public string Url { get; set; }
            [DataMember(Name = "digest")] public string Digest { get; set; }
        }
    }
}
