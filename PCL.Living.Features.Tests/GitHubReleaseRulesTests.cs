using System.Text.Json.Nodes;
using PCL.Living.Features;

internal static class GitHubReleaseRulesTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("github update picks newest stable and skips drafts", StableSelection);
        yield return ("github update picks newest prerelease for beta", BetaSelection);
        yield return ("github update normalizes tag versions", TagVersions);
        yield return ("github update selects the matching architecture asset", AssetSelection);
        yield return ("github update normalizes sha256 digests", Digests);
    }

    private const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static JsonArray Releases()
    {
        return JsonNode.Parse(
            $$"""
              [
                { "tag_name": "v2.15.4-beta.1", "draft": false, "prerelease": true,
                  "published_at": "2026-11-06T04:00:00Z", "body": "beta" },
                { "tag_name": "v2.15.3", "draft": false, "prerelease": false,
                  "published_at": "2026-11-05T00:00:00Z", "body": "stable older" },
                { "tag_name": "v9.9.9", "draft": true, "prerelease": false,
                  "published_at": "2026-11-07T00:00:00Z", "body": "draft" },
                { "tag_name": "v2.15.2", "draft": false, "prerelease": false,
                  "published_at": "2026-11-06T00:00:00Z", "body": "stable newest" }
              ]
              """)!.AsArray();
    }

    private static void StableSelection()
    {
        var release = GitHubReleaseRules.SelectLatest(Releases(), prerelease: false);
        Equal("v2.15.2", release?["tag_name"]?.GetValue<string>());
        Equal("stable newest", release?["body"]?.GetValue<string>());
    }

    private static void BetaSelection()
    {
        var release = GitHubReleaseRules.SelectLatest(Releases(), prerelease: true);
        Equal("v2.15.4-beta.1", release?["tag_name"]?.GetValue<string>());
        Equal(null, GitHubReleaseRules.SelectLatest(
            JsonNode.Parse("""[{ "tag_name": "v1.0.0", "draft": false, "prerelease": false }]""")!.AsArray(),
            prerelease: true)?.ToString());
    }

    private static void TagVersions()
    {
        Equal("2.15.2", GitHubReleaseRules.VersionFromTag("v2.15.2"));
        Equal("2.15.2-beta.1", GitHubReleaseRules.VersionFromTag("V2.15.2-beta.1"));
        Equal("2.15.2", GitHubReleaseRules.VersionFromTag("2.15.2"));
        Equal(null, GitHubReleaseRules.VersionFromTag(""));
        Equal(null, GitHubReleaseRules.VersionFromTag("v"));
    }

    private static void AssetSelection()
    {
        var release = JsonNode.Parse(
            $$"""
              {
                "assets": [
                  { "name": "PCL-SUPER-2.15.2-win-arm64.exe", "browser_download_url": "https://x/arm.exe",
                    "digest": "sha256:{{Sha256}}" },
                  { "name": "PCL-SUPER-2.15.2-win-x64.exe", "browser_download_url": "https://x/x64.exe",
                    "digest": "sha256:{{Sha256}}" },
                  { "name": "PCL-SUPER-2.15.2-win-x64.zip", "browser_download_url": "https://x/x64.zip",
                    "digest": "sha256:{{Sha256}}" }
                ]
              }
              """)!.AsObject();

        var asset = GitHubReleaseRules.SelectWindowsAsset(release, "x64");
        Equal("PCL-SUPER-2.15.2-win-x64.exe", asset?["name"]?.GetValue<string>());
        Equal("https://x/x64.exe", asset?["browser_download_url"]?.GetValue<string>());
        Equal(null, GitHubReleaseRules.SelectWindowsAsset(release, "x86")?.ToString());
    }

    private static void Digests()
    {
        Equal(Sha256, GitHubReleaseRules.Sha256FromDigest("sha256:" + Sha256));
        Equal(Sha256, GitHubReleaseRules.Sha256FromDigest("SHA256:" + Sha256.ToUpperInvariant()));
        Equal(null, GitHubReleaseRules.Sha256FromDigest(null));
        Equal(null, GitHubReleaseRules.Sha256FromDigest("md5:" + Sha256));
        Equal(null, GitHubReleaseRules.Sha256FromDigest("sha256:1234"));
    }

    private static void Equal(string? expected, string? actual)
    {
        if (expected != actual) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
