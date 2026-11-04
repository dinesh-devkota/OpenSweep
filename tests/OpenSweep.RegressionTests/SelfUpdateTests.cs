using System.Text;
using System.Text.Json;
using OpenSweep.Core;

internal static partial class Program
{
    const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    static async Task SelfUpdateTests()
    {
        await Run("Self-update versions accept only stable semantic tags", SelfUpdateVersions);
        await Run("Self-update comparison rejects older and equal releases", SelfUpdateVersionOrder);
        await Run("Self-update feed ignores draft prerelease and unpublished entries", SelfUpdatePublishedFeed);
        await Run("Self-update feed accepts only canonical release assets", SelfUpdateAssets);
        await Run("Self-update feed and assets enforce size limits", SelfUpdateLimits);
        await Run("Self-update malformed feed data fails closed", SelfUpdateMalformedFeed);
        await Run("Self-update hashes verify actual fixture contents", SelfUpdateHashVerification);
        await Run("Self-update rejects malformed digest metadata", SelfUpdateDigestSyntax);
        await Run("Unsigned current builds cannot establish self-update trust", SelfUpdateUnsignedBootstrap);
        await Run("Self-update signer decisions require two trusted matching keys", SelfUpdateSignerPolicy);
        await Run("Self-update downloads and redirects stay on approved HTTPS routes", SelfUpdateUris);
        await Run("Self-update redirect policy enforces its hop limit", SelfUpdateRedirectLimit);
        await Run("Self-update release notes stay bounded", SelfUpdateNotes);
        await Run("Self-update executable metadata must match the newer official release", SelfUpdateMetadataPolicy);
    }

    static Dictionary<string, object> ReleaseFixture(string tag = "v1.2.3", params object[] assets) => new()
    {
        ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = false,
        ["published_at"] = "2026-11-04T03:00:00Z", ["body"] = "Fixture release notes", ["assets"] = assets
    };
    static Dictionary<string, object> AssetFixture(string name = "OpenSweep.exe", string tag = "v1.2.3") => new()
    {
        ["name"] = name, ["size"] = 3L, ["state"] = "uploaded", ["digest"] = "sha256:" + AbcSha256,
        ["browser_download_url"] = "https://github.com/dinesh-devkota/OpenSweep/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name)
    };
    static byte[] ReleaseFeed(params object[] releases) => JsonSerializer.SerializeToUtf8Bytes(releases);
    static SelfUpdateRelease ParseSingle(Dictionary<string, object> asset) => SelfUpdatePolicy.ParseReleaseFeed(ReleaseFeed(ReleaseFixture("v1.2.3", asset)));

    static Task SelfUpdateVersions() => Sync(() =>
    {
        foreach (string tag in new[] { "v1.2.3", "1.2.3", "v1.2.3+build.42" })
        { Check(SelfUpdatePolicy.TryParseStableVersion(tag, out var version), "Valid stable tag rejected: " + tag); Equal(new Version(1, 2, 3), version); }
        foreach (string tag in new[] { null, "", "v1.2", "v01.2.3", "v1.2.3-rc.1", "v1.2.3\n", " v1.2.3", "v2147483648.0.0", "v1.2.3+", "https://example.com/v1.2.3" })
            Check(!SelfUpdatePolicy.TryParseStableVersion(tag, out var version) && version == null, "Malformed/prerelease tag accepted: " + tag);
    });

    static Task SelfUpdateVersionOrder() => Sync(() =>
    {
        Check(SelfUpdatePolicy.IsNewer(new Version(1, 3, 0), new Version(1, 2, 9, 0)), "New release was not recognized");
        Check(!SelfUpdatePolicy.IsNewer(new Version(1, 2, 3), new Version(1, 2, 3, 99)), "Assembly revision caused an equal release to appear new");
        Check(!SelfUpdatePolicy.IsNewer(new Version(1, 1, 9), new Version(1, 2, 0)), "Older release was accepted as an upgrade");
        Check(!SelfUpdatePolicy.IsNewer(null, new Version(1, 0, 0)) && !SelfUpdatePolicy.IsNewer(new Version(1, 0, 0), null), "Missing version was treated as an upgrade");
    });

    static Task SelfUpdatePublishedFeed() => Sync(() =>
    {
        var current = ReleaseFixture("v1.2.3", AssetFixture());
        var old = ReleaseFixture("v1.1.0", AssetFixture(tag: "v1.1.0"));
        var draft = ReleaseFixture("v9.0.0"); draft["draft"] = true;
        var previewFlag = ReleaseFixture("v8.0.0"); previewFlag["prerelease"] = true;
        var previewTag = ReleaseFixture("v7.0.0-beta.1");
        var unpublished = ReleaseFixture("v6.0.0"); unpublished["published_at"] = null;
        var result = SelfUpdatePolicy.ParseReleaseFeed(ReleaseFeed(old, draft, previewFlag, previewTag, unpublished, current));
        Equal(new Version(1, 2, 3), result.Version); Equal("v1.2.3", result.Tag); Equal(1, result.Assets.Count);
        Equal("https://github.com/dinesh-devkota/OpenSweep/releases/tag/v1.2.3", result.ReleaseUri.AbsoluteUri);
        Check(SelfUpdatePolicy.ParseReleaseFeed(ReleaseFeed(draft, previewFlag, previewTag, unpublished)) == null, "An unpublished/preview-only feed produced an installable release");
    });

    static Task SelfUpdateAssets() => Sync(() =>
    {
        var release = SelfUpdatePolicy.ParseReleaseFeed(ReleaseFeed(ReleaseFixture("v1.2.3", AssetFixture(), AssetFixture("OpenSweep-Setup.exe"), AssetFixture())));
        Equal(2, release.Assets.Count); Check(release.Assets.Any(asset => asset.IsInstaller) && release.Assets.Any(asset => !asset.IsInstaller), "Expected installer/portable choices missing");
        foreach (var change in new Action<Dictionary<string, object>>[]
        {
            asset => asset["name"] = "OpenSweep.zip",
            asset => asset["state"] = "new",
            asset => asset["digest"] = null,
            asset => asset["digest"] = "sha256:bad",
            asset => asset["browser_download_url"] = "https://github.com/other/OpenSweep/releases/download/v1.2.3/OpenSweep.exe",
            asset => asset["browser_download_url"] = "https://example.com/OpenSweep.exe",
            asset => asset["browser_download_url"] = "https://github.com/dinesh-devkota/OpenSweep/releases/download/v1.2.2/OpenSweep.exe",
            asset => asset["browser_download_url"] = "https://github.com/dinesh-devkota/OpenSweep/releases/download/v1.2.3/OpenSweep.exe?download=1"
        })
        {
            var asset = AssetFixture(); change(asset); Equal(0, ParseSingle(asset).Assets.Count);
        }
    });

    static Task SelfUpdateLimits() => Sync(() =>
    {
        var exact = AssetFixture(); exact["size"] = SelfUpdatePolicy.MaxDownloadBytes;
        Equal(1, ParseSingle(exact).Assets.Count);
        foreach (long size in new[] { -1L, 0L, SelfUpdatePolicy.MaxDownloadBytes + 1 })
        { var asset = AssetFixture(); asset["size"] = size; Equal(0, ParseSingle(asset).Assets.Count); }
        Throws<InvalidDataException>(() => SelfUpdatePolicy.ParseReleaseFeed(new byte[SelfUpdatePolicy.MaxFeedBytes + 1]));
        Throws<InvalidDataException>(() => SelfUpdatePolicy.ParseReleaseFeed(ReleaseFeed(Enumerable.Repeat<object>(null, 31).ToArray())));
    });

    static Task SelfUpdateMalformedFeed() => Sync(() =>
    {
        Throws<InvalidDataException>(() => SelfUpdatePolicy.ParseReleaseFeed(null));
        Throws<InvalidDataException>(() => SelfUpdatePolicy.ParseReleaseFeed(Array.Empty<byte>()));
        Throws<System.Runtime.Serialization.SerializationException>(() => SelfUpdatePolicy.ParseReleaseFeed(Encoding.UTF8.GetBytes("[{broken JSON")));
        Throws<InvalidDataException>(() => SelfUpdatePolicy.ParseReleaseFeed(Encoding.UTF8.GetBytes("null")));
        var invalidDate = ReleaseFixture(); invalidDate["published_at"] = "not a date";
        Check(SelfUpdatePolicy.ParseReleaseFeed(ReleaseFeed(invalidDate)) == null, "Invalid publication timestamp was accepted");
    });

    static Task SelfUpdateHashVerification() => Sync(() =>
    {
        string path = Path.Combine(Fixture("self-update-hash"), "download.bin"); File.WriteAllText(path, "abc", new UTF8Encoding(false));
        using (var stream = File.OpenRead(path))
        {
            stream.Position = 2; Check(SelfUpdatePolicy.VerifySha256(stream, AbcSha256), "Known SHA-256 vector did not verify"); Equal(0L, stream.Position);
            Check(SelfUpdatePolicy.VerifySha256(stream, AbcSha256.ToUpperInvariant()), "Uppercase digest rejected");
            Check(!SelfUpdatePolicy.VerifySha256(stream, new string('0', 64)), "Incorrect digest matched actual file contents");
        }
        File.WriteAllText(path, "abd", new UTF8Encoding(false));
        using (var changed = File.OpenRead(path)) Check(!SelfUpdatePolicy.VerifySha256(changed, AbcSha256), "Tampered download matched original digest");
    });

    static Task SelfUpdateDigestSyntax() => Sync(() =>
    {
        Check(SelfUpdatePolicy.TryParseSha256("sha256:" + AbcSha256.ToUpperInvariant(), out string hex), "Valid SHA-256 digest rejected"); Equal(AbcSha256, hex);
        foreach (string digest in new[] { null, "", "sha1:" + AbcSha256, "sha256:" + new string('g', 64), "sha256:" + new string('0', 63), "sha256:" + AbcSha256 + "\n" })
            Check(!SelfUpdatePolicy.TryParseSha256(digest, out hex) && hex == null, "Invalid digest metadata accepted");
    });

    static Task SelfUpdateUnsignedBootstrap() => Sync(() =>
    {
        Check(!SelfUpdatePolicy.IsTrustedSignerMatch(false, AbcSha256, true, AbcSha256), "Unsigned current build established automatic-download trust");
        Check(!SelfUpdatePolicy.IsTrustedSignerMatch(false, null, true, AbcSha256), "Missing current signature was accepted");
    });

    static Task SelfUpdateSignerPolicy() => Sync(() =>
    {
        Check(SelfUpdatePolicy.IsTrustedSignerMatch(true, AbcSha256, true, AbcSha256.ToUpperInvariant()), "Two verified matching signer identities did not match");
        Check(!SelfUpdatePolicy.IsTrustedSignerMatch(true, AbcSha256, false, AbcSha256), "Unverified candidate signer was accepted");
        Check(!SelfUpdatePolicy.IsTrustedSignerMatch(true, AbcSha256, true, new string('0', 64)), "Different verified signer was accepted");
        foreach (string key in new[] { null, "", "not-a-key", new string('0', 63) })
            Check(!SelfUpdatePolicy.IsTrustedSignerMatch(true, key, true, key), "Malformed signer identity matched");
    });

    static Uri InitialDownload() => new Uri("https://github.com/dinesh-devkota/OpenSweep/releases/download/v1.2.3/OpenSweep.exe");
    static Task SelfUpdateUris() => Sync(() =>
    {
        var initial = InitialDownload(); Check(SelfUpdatePolicy.IsAllowedDownloadUri(initial, initial), "Canonical initial asset was rejected");
        Check(SelfUpdatePolicy.IsAllowedDownloadUri(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/fixture?token=fixture"), initial), "Approved release-asset route rejected");
        foreach (string uri in new[]
        {
            "http://release-assets.githubusercontent.com/fixture", "https://release-assets.githubusercontent.com:444/fixture",
            "https://user@release-assets.githubusercontent.com/fixture", "https://release-assets.githubusercontent.com/fixture#fragment",
            "https://github.com.evil.example/fixture", "https://example.com/fixture",
            "https://github.com/dinesh-devkota/OpenSweep/releases/download/v1.2.4/OpenSweep.exe"
        }) Check(!SelfUpdatePolicy.IsAllowedDownloadUri(new Uri(uri), initial), "Unsafe or unrelated redirect accepted: " + uri);
        Check(!SelfUpdatePolicy.IsAllowedDownloadUri(new Uri("https://release-assets.githubusercontent.com/fixture"), new Uri("https://github.com/other/repository/releases/download/v1/OpenSweep.exe")), "Untrusted initial origin was accepted");
        foreach (string path in new[] { "v1.2.3-rc.1/OpenSweep.exe", "v1.2.3/Other.exe", "v1.2/OpenSweep.exe", "v1.2.3/OpenSweep.exe?download=1" })
        {
            var invalidInitial = new Uri(SelfUpdatePolicy.RepositoryUrl + "/releases/download/" + path);
            Check(!SelfUpdatePolicy.IsAllowedDownloadUri(invalidInitial, invalidInitial), "Unsupported initial asset route was accepted: " + path);
            Check(!SelfUpdatePolicy.IsAllowedDownloadUri(new Uri("https://release-assets.githubusercontent.com/fixture"), invalidInitial), "Invalid initial asset established redirect trust: " + path);
        }
    });

    static Task SelfUpdateRedirectLimit() => Sync(() =>
    {
        var uri = new Uri("https://release-assets.githubusercontent.com/fixture"); var initial = InitialDownload();
        Check(SelfUpdatePolicy.IsAllowedRedirect(uri, initial, 1), "First safe redirect was rejected");
        Check(SelfUpdatePolicy.IsAllowedRedirect(uri, initial, SelfUpdatePolicy.MaxRedirects), "Last allowed redirect was rejected");
        foreach (int hops in new[] { -1, 0, SelfUpdatePolicy.MaxRedirects + 1 }) Check(!SelfUpdatePolicy.IsAllowedRedirect(uri, initial, hops), "Out-of-policy redirect count accepted");
    });

    static Task SelfUpdateNotes() => Sync(() =>
    {
        var release = ReleaseFixture(); release["body"] = new string('n', 40000) + "sentinel-at-end";
        string notes = SelfUpdatePolicy.ParseReleaseFeed(ReleaseFeed(release)).Notes;
        Check(notes.Length < 33000 && !notes.Contains("sentinel-at-end"), "Release notes were not bounded");
    });

    static Task SelfUpdateMetadataPolicy() => Sync(() =>
    {
        var current = new Version(1, 2, 0); var release = new Version(1, 2, 1);
        Check(SelfUpdatePolicy.MetadataMatchesRelease("OpenSweep", new Version(1, 2, 1, 0), release, current), "Matching newer executable metadata was rejected");
        Check(SelfUpdatePolicy.MetadataMatchesRelease("OpenSweep", release, release, current), "Unspecified file revision was rejected");
        Check(!SelfUpdatePolicy.MetadataMatchesRelease("OpenSweep", current, release, current), "Old executable relabeled as a newer release was accepted");
        Check(!SelfUpdatePolicy.MetadataMatchesRelease("Other product", release, release, current), "Unrelated signed product was accepted");
        Check(!SelfUpdatePolicy.MetadataMatchesRelease("OpenSweep", new Version(1, 2, 1, 4), release, current), "Unexpected executable revision was accepted");
        Check(!SelfUpdatePolicy.MetadataMatchesRelease("OpenSweep", release, release, release), "Equal release was accepted as an upgrade");
        Check(!SelfUpdatePolicy.MetadataMatchesRelease("OpenSweep", release, release, new Version(1, 3, 0)), "Older release was accepted as an upgrade");
    });
}
