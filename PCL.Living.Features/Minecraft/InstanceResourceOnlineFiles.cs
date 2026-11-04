using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>LF 在线资源匹配结果；泛型字段保留原条目和版本对象身份。</summary>
public class InstanceResourceOnlineInfoData<TEntry, TVersion>
        where TEntry : InstanceResourceEntryData where TVersion : class {
    public TEntry Entry = null!;
    public string? Sha1;
    public string? ProjectName;
    public string? Website;
    public string? IconUrl;
    public List<string> Tags = new();
    public TVersion? CurrentVersion;
    public TVersion? UpdateVersion;
    public string Status = "未匹配到在线项目";
    public bool HasUpdate => UpdateVersion is not null;
}

/// <summary>仅原平台版本、网络与下载器跨越宿主边界；不引用主程序集。</summary>
public sealed class InstanceResourceOnlineHost<TVersion, TDownload> where TVersion : class where TDownload : class {
    public Func<JObject, int, TVersion?> ParseVersion = null!;
    public Func<TVersion, bool> DownloadAvailable = null!;
    public Func<TVersion, int> Size = null!, ReleaseType = null!;
    public Func<TVersion, string?> Hash = null!, FileName = null!, Version = null!, Display = null!;
    public Func<TVersion, List<string>> DownloadUrls = null!;
    public Func<TVersion, DateTime> ReleaseDate = null!;
    public Func<TVersion, string, string, TDownload> ToNetFile = null!;
    public Func<string, HttpMethod, string?, JToken> Request = null!;
    public Func<string, uint> Fingerprint = null!;
    public Action<List<TDownload>, CancellationToken> Download = null!;
    public Action<Exception, string> Warn = null!;
}

/// <summary>LF 资源哈希匹配、兼容候选选择及回收后替换；保留原注入式网络和下载入口。</summary>
public sealed class InstanceResourceOnlineFiles<TEntry, TVersion, TDownload, TInfo>
        where TEntry : InstanceResourceEntryData where TVersion : class where TDownload : class
        where TInfo : InstanceResourceOnlineInfoData<TEntry, TVersion> {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 InstanceResourceUpdates.vb.
    // CE is a fixed architecture reference only; this LF hash/update policy is
    // translated in place, retaining ResourceVersion/NetFile/LoaderDownload hosts.
    private readonly Func<string, string> removeExtendedPrefix;
    private readonly InstanceResourceOnlineHost<TVersion, TDownload> host;
    private readonly Func<TInfo> factory;
    private readonly InstanceResourceTransactions transactions;

    public InstanceResourceOnlineFiles(Func<string, string> removeExtendedPrefix,
            InstanceResourceOnlineHost<TVersion, TDownload> host, Func<TInfo> factory) {
        this.removeExtendedPrefix = removeExtendedPrefix;
        this.host = host;
        this.factory = factory;
        transactions = new InstanceResourceTransactions(removeExtendedPrefix);
    }

    public static bool Supported(int kind) => kind == 2 || kind == 3;

    public static string FileHash(string fileName, bool md5 = false) {
        using (FileStream input = File.OpenRead(fileName))
        using (HashAlgorithm algorithm = md5 ? (HashAlgorithm)MD5.Create() : SHA1.Create())
            return BitConverter.ToString(algorithm.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }

    // VB Option Compare Binary treats Nothing and an empty string as equal.
    private static bool SameText(string? left, string? right) =>
        string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);

    private void ValidateEntry(string root, TEntry entry) =>
        InstanceResourceEntryValidator.ValidateEntry(root, entry,
            (parent, file) => InstanceResourcePaths.EnsureWithin(parent, file, false, removeExtendedPrefix));

    private static List<string> Strings(JToken? data) => (data as JArray ?? new JArray())
        .Select(item => item.ToString().ToLowerInvariant()).Distinct().OrderBy(item => item).ToList();

    private bool ValidFile(TVersion? version) {
        if (version is null || !host.DownloadAvailable(version) || host.Size(version) <= 0 ||
                !Regex.IsMatch(host.Hash(version) ?? "", @"\A(?:[0-9a-f]{40}|[0-9a-f]{32})\z")) return false;
        try {
            InstanceResourceNameRules.SafeName(host.FileName(version)!);
            if (!host.FileName(version)!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
            return host.DownloadUrls(version).Count > 0 && host.DownloadUrls(version).All(value =>
                Uri.TryCreate(value, UriKind.Absolute, out Uri? address) && address!.Scheme == "https" && address!.UserInfo == "");
        } catch {
            return false;
        }
    }

    private void Consider(TInfo info, TVersion? current, TVersion? candidate) {
        if (!ValidFile(candidate) || current is null || host.ReleaseDate(candidate!) <= host.ReleaseDate(current) ||
                SameText(host.Hash(candidate!), host.Hash(current))) return;
        if (host.ReleaseType(current) == 1 && host.ReleaseType(candidate!) != 1) return;
        if (host.ReleaseType(current) == 2 && host.ReleaseType(candidate!) == 3) return;
        if (info.UpdateVersion is null || host.ReleaseDate(candidate!) > host.ReleaseDate(info.UpdateVersion))
            info.UpdateVersion = candidate;
    }

    private TVersion? ModrinthFile(JObject data, int kind, string? hash = null) {
        List<JObject> files = (data["files"] as JArray ?? new JArray()).OfType<JObject>().ToList();
        JObject? selected = hash is null
            ? files.FirstOrDefault(item => item["primary"]?.ToObject<bool>() == true)
            : files.FirstOrDefault(item => SameText(item["hashes"]?["sha1"]?.ToString(), hash));
        if (selected is null && hash is null && files.Count == 1) selected = files[0];
        if (selected is null) return null;
        JObject copy = (JObject)data.DeepClone();
        copy["files"] = new JArray(selected.DeepClone());
        return host.ParseVersion(copy, kind);
    }

    public List<TInfo> Check(string root, IEnumerable<TEntry> items, int kind, string? gameVersion,
            CancellationToken token, Func<string, HttpMethod, string?, JToken>? request = null,
            Func<string, uint>? fingerprint = null) {
        if (!Supported(kind) || string.IsNullOrWhiteSpace(gameVersion)) return new List<TInfo>();
        request ??= host.Request;
        fingerprint ??= host.Fingerprint;
        var result = new List<TInfo>();
        foreach (TEntry entry in items.Where(item => !item.IsDirectory)) {
            token.ThrowIfCancellationRequested();
            ValidateEntry(root, entry);
            TInfo info = factory();
            info.Entry = entry;
            info.Sha1 = FileHash(entry.FullPath!);
            result.Add(info);
        }
        if (result.Count == 0) return result;
        var failed = new List<string>();
        // Preserve original platform batch order and failure/partial-result policy.
        for (int offset = 0; offset < result.Count; offset += 100) {
            List<TInfo> batch = result.Skip(offset).Take(100).ToList();
            token.ThrowIfCancellationRequested();
            try { CheckModrinth(batch, kind, gameVersion!, token, request); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { failed.Add("Modrinth"); host.Warn(error, "检查本地资源的 Modrinth 更新失败"); }
            token.ThrowIfCancellationRequested();
            try { CheckCurseForge(batch, kind, gameVersion!, token, request, fingerprint); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { failed.Add("CurseForge"); host.Warn(error, "检查本地资源的 CurseForge 更新失败"); }
        }
        foreach (TInfo info in result) {
            if (info.HasUpdate) info.Status = "可更新至 " + (host.Version(info.UpdateVersion!) ?? host.Display(info.UpdateVersion!));
            else if (info.CurrentVersion is not null) info.Status = "未发现适用于 Minecraft " + gameVersion + " 的更新";
            if (failed.Any()) info.Status += "（" + string.Join("、", failed.Distinct()) + " 查询失败，可重试）";
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private void CheckModrinth(List<TInfo> infos, int kind, string gameVersion, CancellationToken token,
            Func<string, HttpMethod, string?, JToken> request) {
        JObject versions = (JObject)request("https://api.modrinth.com/v2/version_files", HttpMethod.Post,
            new JObject(new JProperty("algorithm", "sha1"), new JProperty("hashes", new JArray(infos.Select(item => item.Sha1).Distinct()))).ToString());
        List<string?> ids = versions.Properties().Select(item => item.Value["project_id"]?.ToString())
            .Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (ids.Count == 0) return;
        token.ThrowIfCancellationRequested();
        Dictionary<string, JObject> projects = ((JArray)request("https://api.modrinth.com/v2/projects?ids=" +
            Uri.EscapeDataString(new JArray(ids).ToString(Formatting.None)), HttpMethod.Get, null)).OfType<JObject>()
            .ToDictionary(item => item["id"]!.ToString());
        var matched = new Dictionary<TInfo, JObject>();
        foreach (TInfo info in infos) {
            JObject? version = versions[info.Sha1!] as JObject;
            if (version is null) continue;
            if (!projects.TryGetValue(version["project_id"]!.ToString(), out JObject? project) ||
                    !SameText(project["project_type"]?.ToString(), kind == 2 ? "resourcepack" : "shader")) continue;
            TVersion? current = ModrinthFile(version, kind, info.Sha1);
            if (current is null) continue;
            info.CurrentVersion = current;
            info.ProjectName = project["title"]?.ToString();
            info.IconUrl = project["icon_url"]?.ToString();
            info.Website = "https://modrinth.com/" + (kind == 2 ? "resourcepack/" : "shader/") + Uri.EscapeDataString(project["id"]!.ToString());
            JArray files = (JArray)version["files"]!;
            if (files.Count > 1 && !files.Any(item => SameText(item["hashes"]?["sha1"]?.ToString(), info.Sha1) &&
                    item["primary"]?.ToObject<bool>() == true)) continue;
            matched.Add(info, version);
        }
        if (matched.Count == 0) return;
        token.ThrowIfCancellationRequested();
        JArray queries = new(matched.Select(pair => new JObject(new JProperty("hash", pair.Key.Sha1),
            new JProperty("loaders", new JArray(Strings(pair.Value["loaders"]))), new JProperty("game_versions", new JArray(gameVersion)),
            new JProperty("version_types", host.ReleaseType(pair.Key.CurrentVersion!) == 1 ? new JArray("release") :
                host.ReleaseType(pair.Key.CurrentVersion!) == 2 ? new JArray("release", "beta") : new JArray("release", "beta", "alpha")))));
        JObject updates = (JObject)request("https://api.modrinth.com/v2/version_files/update_individual", HttpMethod.Post,
            new JObject(new JProperty("algorithm", "sha1"), new JProperty("hashes", queries)).ToString());
        foreach (KeyValuePair<TInfo, JObject> pair in matched) {
            JObject? updated = updates[pair.Key.Sha1!] as JObject;
            if (updated is null || !SameText(updated["project_id"]?.ToString(), pair.Value["project_id"]?.ToString()) ||
                    !Strings(updated["game_versions"]).Contains(gameVersion.ToLowerInvariant()) ||
                    !Strings(updated["loaders"]).SequenceEqual(Strings(pair.Value["loaders"]))) continue;
            Consider(pair.Key, pair.Key.CurrentVersion, ModrinthFile(updated, kind));
        }
    }

    private void CheckCurseForge(List<TInfo> infos, int kind, string gameVersion, CancellationToken token,
            Func<string, HttpMethod, string?, JToken> request, Func<string, uint> fingerprint) {
        Dictionary<TInfo, uint> fingerprints = infos.ToDictionary(item => item, item => fingerprint(item.Entry.FullPath!));
        token.ThrowIfCancellationRequested();
        JArray matches = (JArray)request("https://api.curseforge.com/v1/fingerprints/432", HttpMethod.Post,
            new JObject(new JProperty("fingerprints", new JArray(fingerprints.Values.Distinct()))).ToString())["data"]!["exactMatches"]!;
        List<int> ids = matches.Select(item => item["id"]!.ToObject<int>()).Distinct().ToList();
        if (ids.Count == 0) return;
        token.ThrowIfCancellationRequested();
        Dictionary<string, JObject> projects = ((JArray)request("https://api.curseforge.com/v1/mods", HttpMethod.Post,
            new JObject(new JProperty("modIds", new JArray(ids))).ToString())["data"]!).OfType<JObject>()
            .ToDictionary(item => item["id"]!.ToString());
        foreach (JObject match in matches) {
            token.ThrowIfCancellationRequested();
            if (!projects.TryGetValue(match["id"]!.ToString(), out JObject? project) ||
                    project["isAvailable"]?.ToObject<bool>() == false || project["allowModDistribution"]?.ToObject<bool>() == false) continue;
            string? website = project["links"]?["websiteUrl"]?.ToString();
            if (!Uri.TryCreate(website, UriKind.Absolute, out Uri? address) || address!.Scheme != "https" || !address!.IsDefaultPort ||
                    address!.UserInfo != "" || !new[] { "www.curseforge.com", "curseforge.com" }.Contains(address!.Host, StringComparer.OrdinalIgnoreCase)) continue;
            string expectedPath = kind == 3 ? "/minecraft/shaders/" : "/minecraft/texture-packs/";
            if (!address!.AbsolutePath.StartsWith(expectedPath, StringComparison.OrdinalIgnoreCase) &&
                    !(kind == 2 && address!.AbsolutePath.StartsWith("/minecraft/resourcepacks/", StringComparison.OrdinalIgnoreCase))) continue;
            JObject rawCurrent = (JObject)match["file"]!;
            List<TInfo> matchedInfos = infos.Where(info => fingerprints[info] == rawCurrent["fileFingerprint"]!.ToObject<uint>() &&
                ((JArray)rawCurrent["hashes"]!).Any(hash => SameText(
                    hash["algo"]!.ToObject<int>() == 1 ? info.Sha1 : hash["algo"]!.ToObject<int>() == 2 ? FileHash(info.Entry.FullPath!, true) : "",
                    hash["value"]!.ToString().ToLowerInvariant()))).ToList();
            if (matchedInfos.Count == 0) continue;
            TVersion? current = host.ParseVersion(rawCurrent, kind);
            foreach (TInfo info in matchedInfos) {
                if (info.CurrentVersion is null) {
                    info.CurrentVersion = current;
                    info.ProjectName = project["name"]?.ToString();
                    JObject? logo = project["logo"] as JObject;
                    info.IconUrl = logo?["thumbnailUrl"]?.ToString();
                    if (string.IsNullOrWhiteSpace(info.IconUrl)) info.IconUrl = logo?["url"]?.ToString();
                    info.Tags = (project["categories"] as JArray ?? new JArray()).Select(category => category["name"]?.ToString())
                        .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Take(3).ToList();
                    info.Website = website;
                }
            }
            int index = 0;
            do {
                token.ThrowIfCancellationRequested();
                JToken page = request($"https://api.curseforge.com/v1/mods/{match["id"]}/files?gameVersion={Uri.EscapeDataString(gameVersion)}&pageSize=50&index={index}", HttpMethod.Get, null);
                JArray files = (JArray)page["data"]!;
                foreach (JObject raw in files) {
                    if (!SameText(raw["modId"]?.ToString(), match["id"]!.ToString()) || raw["isAvailable"]?.ToObject<bool>() == false ||
                            string.IsNullOrWhiteSpace(raw["downloadUrl"]?.ToString()) || !Strings(raw["gameVersions"]).Contains(gameVersion.ToLowerInvariant())) continue;
                    TVersion? candidate = host.ParseVersion(raw, kind);
                    foreach (TInfo info in matchedInfos) Consider(info, current, candidate);
                }
                index += files.Count;
                if (files.Count < 50 || index >= (page["pagination"]?["totalCount"]?.ToObject<int>() ?? index)) break;
            } while (index < 10000);
        }
    }

    public void Install(string root, IEnumerable<TInfo> infos, string recycle, int kind, string gameVersion,
            CancellationToken token, Action<List<TDownload>, CancellationToken>? download = null) {
        List<TInfo> items = infos.ToList();
        if (!Supported(kind) || items.Count == 0) return;
        string stage = Path.Combine(root, ".pcl-import-" + Guid.NewGuid().ToString("N"));
        InstanceResourcePaths.EnsureWithin(root, stage, false, removeExtendedPrefix);
        Directory.CreateDirectory(stage);
        try {
            var downloads = new List<TDownload>();
            for (int index = 0; index < items.Count; index++) {
                TInfo info = items[index];
                ValidateEntry(root, info.Entry);
                if (info.Entry.IsDirectory || !info.HasUpdate || !ValidFile(info.UpdateVersion) ||
                        !SameText(FileHash(info.Entry.FullPath!), info.Sha1))
                    throw new IOException("资源已变化，请重新检查更新。");
                downloads.Add(host.ToNetFile(info.UpdateVersion!, Path.Combine(stage, index + ".zip"), gameVersion));
            }
            download ??= host.Download;
            download(downloads, token);
            // Validate every downloaded member before replacing the first resource.
            for (int index = 0; index < items.Count; index++) {
                token.ThrowIfCancellationRequested();
                TInfo info = items[index];
                string fileName = Path.Combine(stage, index + ".zip");
                if (!File.Exists(fileName) || new FileInfo(fileName).Length != host.Size(info.UpdateVersion!) ||
                        !SameText(FileHash(fileName, host.Hash(info.UpdateVersion!)!.Length == 32), host.Hash(info.UpdateVersion!)))
                    throw new IOException("资源更新文件校验失败，原文件已保留。");
                InstanceResourcePackageRules.ValidatePackage(fileName, kind);
            }
            for (int index = 0; index < items.Count; index++) {
                token.ThrowIfCancellationRequested();
                TInfo info = items[index];
                ValidateEntry(root, info.Entry);
                if (!SameText(FileHash(info.Entry.FullPath!), info.Sha1)) throw new IOException("原资源已变化，已停止替换。");
                string box = transactions.MoveToRecycle(root, info.Entry, recycle);
                try { File.Move(Path.Combine(stage, index + ".zip"), info.Entry.FullPath!); }
                catch {
                    if (!File.Exists(info.Entry.FullPath)) {
                        File.Move(Path.Combine(box, "content"), info.Entry.FullPath!);
                        File.Delete(Path.Combine(box, "resource.json"));
                        Directory.Delete(box, false);
                    }
                    throw;
                }
            }
        } finally {
            transactions.DeleteStage(root, stage);
        }
    }
}
