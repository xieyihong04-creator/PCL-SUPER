using System.Text.Json.Nodes;

namespace PCL.Living.Features;

/// <summary>GitHub Releases 数据的纯解析规则。</summary>
public static class GitHubReleaseRules
{
    private const int Sha256HexLength = 64;

    /// <summary>取发布时间最新的非草稿 Release；prerelease 决定正式版还是预发布版。</summary>
    public static JsonObject? SelectLatest(JsonArray? releases, bool prerelease)
    {
        if (releases is null)
            return null;

        return releases
            .OfType<JsonObject>()
            .Where(release => !(release["draft"]?.GetValue<bool>() ?? false))
            .Where(release => (release["prerelease"]?.GetValue<bool>() ?? false) == prerelease)
            .OrderByDescending(GetPublishedAt)
            .FirstOrDefault();
    }

    public static string? VersionFromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var version = tag.Trim();
        if (version.Length > 0 && (version[0] == 'v' || version[0] == 'V'))
            version = version[1..];

        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    /// <summary>选择形如 PCL-SUPER-&lt;版本&gt;-win-&lt;架构&gt;.exe 的资产。</summary>
    public static JsonObject? SelectWindowsAsset(JsonObject? release, string arch)
    {
        if (release?["assets"] is not JsonArray assets || string.IsNullOrWhiteSpace(arch))
            return null;

        var suffix = $"-win-{arch}.exe";
        return assets
            .OfType<JsonObject>()
            .FirstOrDefault(asset =>
            {
                var name = asset["name"]?.GetValue<string>();
                return !string.IsNullOrWhiteSpace(name)
                       && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
            });
    }

    public static string? Sha256FromDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;

        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var hash = digest[prefix.Length..].Trim();
        if (hash.Length != Sha256HexLength)
            return null;
        foreach (var ch in hash)
            if (!char.IsAsciiHexDigit(ch))
                return null;

        return hash.ToLowerInvariant();
    }

    private static DateTimeOffset GetPublishedAt(JsonObject release)
    {
        if (DateTimeOffset.TryParse(
                release["published_at"]?.GetValue<string>(),
                out var published))
            return published;
        if (DateTimeOffset.TryParse(
                release["created_at"]?.GetValue<string>(),
                out var created))
            return created;
        return DateTimeOffset.MinValue;
    }
}

/// <summary>更新/公告提示是否应暂缓到用户回到启动器。</summary>
public static class UpdateNotificationRules
{
    /// <summary>启动器非前台、最小化、正在启动游戏或游戏仍在运行时，暂缓非主动提示。</summary>
    public static bool ShouldDefer(
        bool isActive,
        bool isMinimized,
        bool isLaunching,
        bool isGameRunning) =>
        !isActive || isMinimized || isLaunching || isGameRunning;
}
