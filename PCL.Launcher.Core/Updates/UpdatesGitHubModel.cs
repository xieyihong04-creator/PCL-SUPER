using System.Text.Json.Nodes;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Living.Features;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>从本项目的 GitHub Releases 获取启动器更新。</summary>
public class UpdatesGitHubModel : IUpdateSource
{
    private const string ReleasesUrl =
        "https://api.github.com/repos/xieyihong04-creator/PCL-SUPER/releases?per_page=30";

    private JsonArray? _releases;

    public UpdatesGitHubModel()
    {
        SourceName = "GitHub Releases";
    }

    public string SourceName { get; set; }

    public bool IsAvailable() => true;

    public bool RefreshCache()
    {
        _releases = Requester.FetchJson<JsonArray>(
            ReleasesUrl,
            new RequestParam
            {
                Accept = "application/vnd.github+json",
                Timeout = 30000,
                Retries = 3
            });
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var release = GetRelease(channel);
        var version = GitHubReleaseRules.VersionFromTag(
                          release?["tag_name"]?.GetValue<string>())
                      ?? throw new Exception("更新 Release 缺少有效的版本标签");
        var asset = GitHubReleaseRules.SelectWindowsAsset(release, GetArchName(arch))
                    ?? throw new Exception($"更新 Release v{version} 缺少 {GetArchName(arch)} 资产");
        var sha256 = GitHubReleaseRules.Sha256FromDigest(
                         asset["digest"]?.GetValue<string>())
                     ?? throw new Exception($"更新 Release v{version} 的资产缺少 SHA256 校验值");

        return new VersionDataModel
        {
            VersionName = version,
            VersionCode = 0,
            Sha256 = sha256,
            Source = SourceName,
            Changelog = release?["body"]?.GetValue<string>() ?? ""
        };
    }

    public bool IsLatest(
        UpdateChannel channel,
        UpdateArch arch,
        SemVer currentVersion,
        int currentVersionCode)
    {
        return currentVersion >= SemVer.Parse(GetLatestVersion(channel, arch).VersionName);
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        return new VersionAnnouncementDataModel { Content = [] };
    }

    public List<Loading.LoaderBase> GetDownloadLoader(
        UpdateChannel channel,
        UpdateArch arch,
        string output)
    {
        var release = GetRelease(channel);
        var asset = GitHubReleaseRules.SelectWindowsAsset(release, GetArchName(arch))
                    ?? throw new Exception($"更新 Release 缺少 {GetArchName(arch)} 资产");
        var url = asset["browser_download_url"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(url))
            throw new Exception("更新资产缺少下载地址");

        return
        [
            new LoaderDownload(
                Lang.Text("Update.Task.DownloadFile"),
                [new DownloadFile([url], output)])
        ];
    }

    private JsonObject GetRelease(UpdateChannel channel)
    {
        if (_releases is null)
            RefreshCache();

        return GitHubReleaseRules.SelectLatest(_releases, channel == UpdateChannel.Beta)
               ?? throw new Exception(
                   channel == UpdateChannel.Beta
                       ? "GitHub Releases 中没有可用的预发布版本"
                       : "GitHub Releases 中没有可用的正式版本");
    }

    private static string GetArchName(UpdateArch arch) =>
        arch == UpdateArch.Arm64 ? "arm64" : "x64";
}
