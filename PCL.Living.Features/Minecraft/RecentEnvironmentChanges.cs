using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

public class RecentEnvironmentChangeData {
    public string? Kind, Category, Relative, PreviousRelative, BeforeVersion, AfterVersion, BeforeState, AfterState;
}

public class RecentEnvironmentChangesReportData<TChange> where TChange : RecentEnvironmentChangeData {
    public bool HasBaseline;
    public string? BaselineId;
    public DateTime? BaselineCreatedUtc;
    public string? Message;
    public List<TChange> Changes = new();
    public string ToAnalysisText(int maximumEntries = 30) {
        StringBuilder result = new();
        result.AppendLine("上次正常运行后改了什么");
        if (!HasBaseline) {
            result.AppendLine(Message ?? "尚无正常运行记录，无法进行环境变更对照。");
            return result.ToString().TrimEnd();
        }
        result.AppendLine("对照正常运行还原点：" + BaselineCreatedUtc!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（" + BaselineId + "）");
        if (!string.IsNullOrWhiteSpace(Message)) result.AppendLine(Message);
        result.AppendLine("以下是文件变更记录，仅表示时间上的相关性，不能据此确定崩溃原因。配置仅列出相对路径，不读取或显示具体值。");
        if (Changes.Count == 0) {
            result.AppendLine("未发现已记录范围内的模组、配置、Minecraft 或加载器文件变化。");
        } else {
            foreach (TChange item in Changes.Take(Math.Max(0, Math.Min(maximumEntries, 200)))) {
                string detail = item.Category + " · " + item.Kind + " · " + item.Relative!.Substring(2);
                if (item.PreviousRelative is not null && !SameText(item.PreviousRelative, item.Relative)) detail += "（原 " + item.PreviousRelative.Substring(2) + "）";
                if (item.Category == "模组") {
                    if (item.BeforeVersion is not null || item.AfterVersion is not null) detail += "；版本 " + (item.BeforeVersion ?? "未知") + " → " + (item.AfterVersion ?? "未知");
                    if (item.BeforeState is not null || item.AfterState is not null) detail += "；" + (item.BeforeState ?? "不存在") + " → " + (item.AfterState ?? "不存在");
                }
                result.AppendLine("- " + detail);
            }
            if (Changes.Count > Math.Max(0, Math.Min(maximumEntries, 200))) result.AppendLine($"另有 {Changes.Count - Math.Max(0, Math.Min(maximumEntries, 200))} 项变更。");
        }
        result.Append("可从还原点页面查看此时间点；恢复前会再次确认并保存当前环境。");
        return result.ToString();
    }
    private static bool SameText(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);
}

/// <summary>只对比最近正常运行的环境；不读取存档、截图、账号与地图标记的内容。</summary>
// LF RecentEnvironmentChanges.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// unchanged through 07dd3503b38b39095c8b92b40ae0454907387f9f. No CE code reused.
public sealed class RecentEnvironmentChangesEngine<TInstance, TChange, TReport>
        where TInstance : class where TChange : RecentEnvironmentChangeData where TReport : RecentEnvironmentChangesReportData<TChange> {
    private const int MaximumFiles = 100000;
    private const long MaximumBytes = 128L * 1024 * 1024 * 1024;
    private readonly EnvironmentHistoryEngine<TInstance> history;
    private readonly RecentEnvironmentHost<TInstance> host;
    private readonly Func<TChange> createChange;
    private readonly Func<TReport> createReport;
    public RecentEnvironmentChangesEngine(EnvironmentHistoryEngine<TInstance> history, RecentEnvironmentHost<TInstance> host,
        Func<TChange> createChange, Func<TReport> createReport) {
        this.history = history; this.host = host; this.createChange = createChange; this.createReport = createReport;
    }
    private static bool SameText(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);
    private bool IsGameFile(string relative, int coverage = 2) {
        string lower = relative.ToLowerInvariant();
        if (Regex.IsMatch(Path.GetFileName(lower), @"\A(?:accounts?|credentials?|access[_-]?tokens?|auth[_-]?tokens?|launcher_accounts|usercache|usernamecache|launcher_profiles)(?:\.|$)", RegexOptions.IgnoreCase)) return false;
        return history.IsGameFileCovered(relative, coverage);
    }
    private bool SafeLogical(string? key, int coverage = 2) {
        if (key is null || key.Length < 3 || key.Length > 500) return false;
        host.RelativePath(key.Substring(2));
        if (key.StartsWith("g/", StringComparison.Ordinal)) return IsGameFile(key.Substring(2), coverage);
        if (key.StartsWith("v/", StringComparison.Ordinal)) {
            if (Regex.IsMatch(Path.GetFileName(key), @"\A(?:accounts?|credentials?|access[_-]?tokens?|auth[_-]?tokens?|launcher_accounts|usercache|usernamecache|launcher_profiles)(?:\.|$)", RegexOptions.IgnoreCase)) return false;
            return (!key.Substring(2).Contains("/") && new[] { ".json", ".jar" }.Contains(Path.GetExtension(key).ToLowerInvariant())) || new[] { "v/PCL/Setup.ini", "v/PCL/config.json", "v/PCL/ModpackOrigin.json" }.Contains(key);
        }
        return false;
    }
    private void Walk(string root, string folder, Dictionary<string, string> entries, CancellationToken token, int depth = 0) {
        token.ThrowIfCancellationRequested();
        host.EnsureNoLinks(folder);
        if (!Directory.Exists(folder)) return;
        if (depth > 64) throw new IOException("环境目录嵌套过深，无法安全完成变更对照。");
        foreach (string address in Directory.EnumerateFileSystemEntries(folder)) {
            token.ThrowIfCancellationRequested();
            host.EnsureWithin(root, address);
            host.EnsureNoLinks(address);
            string relative = address.Substring(root.Length + 1).Replace("\\", "/");
            if (!IsGameFile(relative)) continue;
            if (Directory.Exists(address)) Walk(root, address, entries, token, depth + 1);
            else {
                entries.Add("g/" + relative, address);
                if (entries.Count > MaximumFiles) throw new IOException("环境文件超过 100000 个，已停止变更对照。");
            }
        }
    }
    private Dictionary<string, string> CurrentFiles(string versionFolder, string gameRoot, CancellationToken token, int coverage) {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in history.FoldersForCoverage(coverage)) Walk(gameRoot, Path.Combine(gameRoot, name), result, token);
        foreach (string address in Directory.EnumerateFiles(gameRoot)) {
            token.ThrowIfCancellationRequested();
            if (IsGameFile(Path.GetFileName(address))) { host.EnsureNoLinks(address); result.Add("g/" + Path.GetFileName(address), address); }
        }
        foreach (string address in Directory.EnumerateFiles(versionFolder)) {
            token.ThrowIfCancellationRequested();
            if (SafeLogical("v/" + Path.GetFileName(address))) { host.EnsureNoLinks(address); result.Add("v/" + Path.GetFileName(address), address); }
        }
        foreach (string name in new[] { "PCL/Setup.ini", "PCL/config.json", "PCL/ModpackOrigin.json" }) {
            string address = host.Target(versionFolder, name);
            host.EnsureNoLinks(address);
            if (File.Exists(address)) result.Add("v/" + name, address);
        }
        if (result.Count > MaximumFiles) throw new IOException("环境文件过多，已停止变更对照。");
        return result;
    }
    private Dictionary<string, string> Hashes(Dictionary<string, string> entries, CancellationToken token) {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (KeyValuePair<string, string> pair in entries) {
            token.ThrowIfCancellationRequested();
            bytes = checked(bytes + new FileInfo(pair.Value).Length);
            if (bytes > MaximumBytes) throw new IOException("环境文件超过 128 GiB，已停止变更对照。");
            result.Add(pair.Key, host.Digest(pair.Value, token));
        }
        return result;
    }
    private static bool IsMod(string key) => key.StartsWith("g/mods/", StringComparison.OrdinalIgnoreCase) || key.StartsWith("g/coremods/", StringComparison.OrdinalIgnoreCase);
    private static string EnabledName(string key) {
        string value = key;
        while (value.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.LastIndexOf('.'));
        return value;
    }
    private static string? ModState(string? key) {
        if (key is null) return null;
        if (key.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) return "旧版本备份（停用）";
        if (key.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return "已禁用";
        return new[] { ".jar", ".zip", ".litemod" }.Contains(Path.GetExtension(key).ToLowerInvariant()) ? "已启用" : "其他模组文件";
    }
    private static string? FileVersion(string? key) {
        if (key is null) return null;
        string stem = Path.GetFileNameWithoutExtension(EnabledName(key));
        Match match = Regex.Match(stem, @"[-_]v?(\d[^\s]*)\z", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value + "（文件名）" : null;
    }
    private static string ModFamily(string key) => Regex.Replace(Path.GetFileNameWithoutExtension(EnabledName(key)), @"[-_]v?\d.*\z", "", RegexOptions.IgnoreCase);
    private static string? ReadVersion(string address, CancellationToken token) {
        try {
            token.ThrowIfCancellationRequested();
            using (ZipArchive archive = ZipFile.OpenRead(address)) {
                foreach (string name in new[] { "fabric.mod.json", "quilt.mod.json", "META-INF/mods.toml", "META-INF/neoforge.mods.toml", "mcmod.info" }) {
                    token.ThrowIfCancellationRequested();
                    ZipArchiveEntry? entry = archive.GetEntry(name);
                    if (entry is null || entry.Length > 1024 * 1024) continue;
                    string text;
                    using (StreamReader reader = new(entry.Open())) text = reader.ReadToEnd();
                    string? version = null;
                    if (name.EndsWith(".toml", StringComparison.Ordinal)) {
                        Match match = Regex.Match(text, @"(?m)^\s*version\s*=\s*['""]([A-Za-z0-9._+\-]+)['""]");
                        if (match.Success) version = match.Groups[1].Value;
                    } else {
                        JToken data = JToken.Parse(text);
                        if (name == "mcmod.info") {
                            JArray? items = (data as JArray) ?? (data["modList"] as JArray);
                            version = items?.FirstOrDefault()?["version"]?.ToString();
                        } else {
                            version = name == "quilt.mod.json" ? data["quilt_loader"]?["version"]?.ToString() : data["version"]?.ToString();
                        }
                    }
                    if (version is not null && Regex.IsMatch(version, @"\A[A-Za-z0-9._+\-]{1,100}\z")) return version;
                }
            }
        } catch (Exception error) when (error is IOException || error is InvalidDataException || error is JsonException || error is UnauthorizedAccessException) {
            // 不可解析的模组仍按文件变化显示，不猜测元数据。
        }
        return null;
    }
    private TChange Change(string? beforeKey, string? afterKey, string kind, Dictionary<string, string> entries, CancellationToken token) {
        string key = (afterKey ?? beforeKey)!;
        bool modFile = IsMod(key);
        string category = modFile ? "模组" : key.StartsWith("v/PCL/") ? "启动器版本配置" : key.StartsWith("v/") ? "Minecraft / 加载器" : "配置 / 环境资源";
        TChange result = createChange();
        result.Relative = key; result.PreviousRelative = beforeKey; result.Kind = kind; result.Category = category;
        if (modFile) {
            result.BeforeState = ModState(beforeKey); result.AfterState = ModState(afterKey);
            result.BeforeVersion = FileVersion(beforeKey);
            if (afterKey is not null) result.AfterVersion = ReadVersion(entries[afterKey], token) ?? FileVersion(afterKey);
        }
        return result;
    }
    public TReport BuildInstance(TInstance instance, CancellationToken token) => Build(host.VersionPath(instance), host.GameRootForInstance(instance), token);
    public TReport Build(string versionFolder, string gameRoot, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        versionFolder = host.NormalizeRoot(versionFolder); gameRoot = host.NormalizeRoot(gameRoot);
        TReport result = createReport();
        JObject? point = history.Points(versionFolder).FirstOrDefault(item => item.Value<bool?>("healthy").GetValueOrDefault());
        if (point is null) { result.Message = "尚无正常运行记录，无法判断上次正常运行后的变更。可在版本正常启动并退出后再次查看。"; return result; }
        if (!host.SamePath(point["gameRoot"]?.ToString(), gameRoot)) { result.Message = "最近正常运行记录使用了不同的游戏目录。当前隔离设置已变化，无法可靠对照。"; return result; }
        string? id = point["id"]?.ToString();
        if (!Regex.IsMatch(id ?? "", @"\A[a-f0-9]{32}\z")) throw new IOException("正常运行还原点编号无效。");
        JObject? files = point["files"] as JObject;
        if (files is null || files.Count > MaximumFiles) throw new IOException("正常运行环境清单无效或过大。");
        Dictionary<string, string> baseline = new(StringComparer.OrdinalIgnoreCase);
        int coverage = history.CoverageVersion(point);
        if (coverage == 1) result.Message = "此旧还原点未覆盖扩展环境目录，相关文件未纳入对照。";
        foreach (JProperty item in files.Properties()) {
            token.ThrowIfCancellationRequested();
            if (!SafeLogical(item.Name, coverage)) continue;
            string hash = item.Value.ToString();
            if (!Regex.IsMatch(hash, @"\Asha1:[a-f0-9]{40}\z")) throw new IOException("正常运行环境清单的校验值无效。");
            baseline.Add(item.Name, hash);
        }
        Dictionary<string, string> entries = CurrentFiles(versionFolder, gameRoot, token, coverage), current = Hashes(entries, token);
        HashSet<string> removed = new(baseline.Keys.Where(key => !current.ContainsKey(key)), StringComparer.OrdinalIgnoreCase);
        HashSet<string> added = new(current.Keys.Where(key => !baseline.ContainsKey(key)), StringComparer.OrdinalIgnoreCase);
        foreach (string oldKey in removed.Where(IsMod).OrderBy(key => key).ToArray()) {
            token.ThrowIfCancellationRequested();
            List<string> matches = added.Where(key => IsMod(key) && string.Equals(EnabledName(key), EnabledName(oldKey), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) continue;
            string newKey = matches[0], kind = !SameText(ModState(oldKey), ModState(newKey)) ? SameText(ModState(newKey), "已启用") ? "启用" : "禁用 / 保留旧版" : "重命名";
            if (!SameText(baseline[oldKey], current[newKey])) kind += "并更新内容";
            result.Changes.Add(Change(oldKey, newKey, kind, entries, token));
            removed.Remove(oldKey); added.Remove(newKey);
        }
        foreach (string oldKey in removed.Where(key => IsMod(key) && FileVersion(key) is not null).OrderBy(key => key).ToArray()) {
            token.ThrowIfCancellationRequested();
            string family = ModFamily(oldKey);
            List<string> matches = added.Where(key => IsMod(key) && string.Equals(ModFamily(key), family, StringComparison.OrdinalIgnoreCase)).ToList();
            if (SameText(family, "") || matches.Count != 1 || removed.Where(key => IsMod(key) && string.Equals(ModFamily(key), family, StringComparison.OrdinalIgnoreCase)).Count() != 1) continue;
            string newKey = matches[0];
            result.Changes.Add(Change(oldKey, newKey, "文件版本变化（按文件名配对）", entries, token));
            removed.Remove(oldKey); added.Remove(newKey);
        }
        foreach (string key in baseline.Keys.Intersect(current.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(value => value)) {
            token.ThrowIfCancellationRequested();
            if (!SameText(baseline[key], current[key])) result.Changes.Add(Change(key, key, IsMod(key) ? "更新内容" : "修改", entries, token));
        }
        foreach (string key in removed.OrderBy(value => value)) result.Changes.Add(Change(key, null, "删除", entries, token));
        foreach (string key in added.OrderBy(value => value)) result.Changes.Add(Change(null, key, "新增", entries, token));
        Dictionary<string, string> latest = Hashes(CurrentFiles(versionFolder, gameRoot, token, coverage), token);
        if (current.Count != latest.Count || current.Any(pair => !latest.ContainsKey(pair.Key) || !SameText(latest[pair.Key], pair.Value))) throw new IOException("环境在扫描期间发生变化，请稍后重新查看变更。");
        result.HasBaseline = true; result.BaselineId = id; result.BaselineCreatedUtc = ((DateTime)point["created"]!).ToUniversalTime();
        return result;
    }
    public string? AppendAnalysis(string? analysis, TReport? report) {
        if (report is null) return analysis;
        return (analysis ?? "").TrimEnd() + "\r\n\r\n" + report.ToAnalysisText();
    }
}
