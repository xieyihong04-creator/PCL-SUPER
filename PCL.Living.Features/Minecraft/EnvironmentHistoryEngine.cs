using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

public static class EnvironmentHistoryRules {
    public const int CurrentCoverageVersion = 2;
    internal static readonly string[] AutomaticLabels = { "游戏启动前", "修改游戏组件前", "更新模组前", "整合包更新前", "恢复前自动保存", "测试副本迁移前" };
    private static readonly string[] LegacyGameFolders = { "mods", "config", "defaultconfigs", "kubejs", "scripts", "resources" };
    public static readonly string[] GameEnvironmentFolders = { "mods", "coremods", "config", "defaultconfigs", "configureddefaults", "kubejs", "scripts", "resources", "global_packs", "openloader" };

    public static int CoverageVersion(JObject point) {
        JToken? value = point["coverageVersion"];
        if (value is null) return 1;
        if (value.Type != JTokenType.Integer) throw new IOException("还原点的覆盖范围版本无效。");
        int version = value.ToObject<int>();
        if (version < 1 || version > CurrentCoverageVersion) throw new IOException("此还原点使用了不支持的覆盖范围，请使用对应版本的启动器。");
        return version;
    }
    public static IEnumerable<string> FoldersForCoverage(int version) => version switch {
        1 => LegacyGameFolders,
        2 => GameEnvironmentFolders,
        _ => throw new IOException("不支持此还原点的覆盖范围。")
    };
    public static bool IsGameFile(string relative) => IsGameFileCovered(relative, CurrentCoverageVersion);
    public static bool IsGameFileCovered(string relative, int version) {
        string lower = relative.Replace("\\", "/").ToLowerInvariant(), top = lower.Split('/')[0];
        if (lower.Contains("waypoint") || lower.StartsWith("config/xaero") || lower.StartsWith("config/journeymap") || lower.StartsWith("config/voxelmap")) return false;
        if (FoldersForCoverage(version).Contains(top)) return true;
        if (lower.Contains("/")) return false;
        return lower == "options.txt" || lower.StartsWith("optionsof") || lower.StartsWith("optionsshaders") ||
            new[] { ".cfg", ".toml", ".properties" }.Contains(Path.GetExtension(lower));
    }
}

/// <summary>LF 内容寻址还原点、跨进程租约、保留策略及恢复事务编排。</summary>
// LF EnvironmentHistory.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983;
// unchanged through 07dd3503b38b39095c8b92b40ae0454907387f9f. No CE code reused.
public sealed partial class EnvironmentHistoryEngine<TInstance> where TInstance : class {
    public readonly object Gate;
    private readonly EnvironmentHistoryHost<TInstance> host;
    public int RestoringCount;
    public string? TestRoot { get; set; }
    public EnvironmentHistoryEngine(object gate, EnvironmentHistoryHost<TInstance> host) { Gate = gate; this.host = host; }
    private static bool SameText(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    public int CoverageVersion(JObject point) => EnvironmentHistoryRules.CoverageVersion(point);
    public IEnumerable<string> FoldersForCoverage(int version) => EnvironmentHistoryRules.FoldersForCoverage(version);
    public bool IsGameFile(string relative) => EnvironmentHistoryRules.IsGameFile(relative);
    public bool IsGameFileCovered(string relative, int version) => EnvironmentHistoryRules.IsGameFileCovered(relative, version);

    public string Store(string versionFolder) {
        string key = host.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(versionFolder).TrimEnd('\\').ToLowerInvariant())).Replace("/", "_").Replace("+", "-").TrimEnd('=');
        return Path.Combine(TestRoot ?? Path.Combine(host.DataRoot(), "environment-history"), key);
    }

    private sealed class StoreLease : IDisposable {
        private readonly Mutex mutex;
        public StoreLease(EnvironmentHistoryEngine<TInstance> owner, string versionFolder) {
            string root = owner.Store(versionFolder);
            owner.host.EnsureNoLinks(root);
            string key = owner.host.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant())).Replace("/", "_").Replace("+", "-").TrimEnd('=');
            mutex = new Mutex(false, "Local\\PCLLF.EnvironmentHistory." + key);
            try {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new IOException("其他启动器正在处理此版本的还原点，请稍后重试。");
            } catch (AbandonedMutexException) {
                // 本线程已取得退出进程留下的锁，引用仍按完整清单计算。
            } catch {
                mutex.Dispose();
                throw;
            }
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    private string Safe(string storeRoot, string relative) => host.Target(storeRoot, relative);
    private string ObjectPath(string storeRoot, string? hash) {
        if (!Regex.IsMatch(hash ?? "", @"\Asha1:[a-f0-9]{40}\z")) throw new IOException("还原点文件校验值无效。");
        return Safe(storeRoot, "objects/" + hash!.Substring(5));
    }
    private void Walk(string root, string folder, Dictionary<string, string> result) {
        host.EnsureWithin(root, folder);
        if (!Directory.Exists(folder)) return;
        foreach (string fileName in Directory.GetFiles(folder)) {
            host.EnsureWithin(root, fileName);
            string relative = fileName.Substring(host.AddSlashSuffix(root).Length).Replace("\\", "/");
            if (IsGameFile(relative)) result["g/" + relative] = fileName;
        }
        foreach (string child in Directory.GetDirectories(folder)) Walk(root, child, result);
    }
    public Dictionary<string, string> Files(string versionFolder, string gameRoot) => FilesWithCoverage(versionFolder, gameRoot, EnvironmentHistoryRules.CurrentCoverageVersion);
    public Dictionary<string, string> FilesWithCoverage(string versionFolder, string gameRoot, int version) {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in FoldersForCoverage(version)) Walk(gameRoot, Path.Combine(gameRoot, name), result);
        if (Directory.Exists(gameRoot)) {
            foreach (string fileName in Directory.GetFiles(gameRoot)) {
                if (IsGameFile(Path.GetFileName(fileName))) result["g/" + Path.GetFileName(fileName)] = host.Target(gameRoot, Path.GetFileName(fileName));
            }
        }
        if (Directory.Exists(versionFolder)) {
            foreach (string fileName in Directory.GetFiles(versionFolder)) {
                if (new[] { ".json", ".jar" }.Contains(Path.GetExtension(fileName).ToLowerInvariant())) result["v/" + Path.GetFileName(fileName)] = host.Target(versionFolder, Path.GetFileName(fileName));
            }
        }
        foreach (string name in new[] { "PCL/Setup.ini", "PCL/config.json", "PCL/ModpackOrigin.json" }) {
            string fileName = host.Target(versionFolder, name);
            if (File.Exists(fileName)) result["v/" + name] = fileName;
        }
        return result;
    }
    public string Capture(string versionFolder, string gameRoot, string label, CancellationToken token,
        Dictionary<string, string>? dependencies = null, string description = "") {
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                string storeRoot = Store(versionFolder), id = Guid.NewGuid().ToString("N");
                Dictionary<string, string> entries = Files(versionFolder, gameRoot);
                JObject content = new();
                if (dependencies is not null) {
                    foreach (KeyValuePair<string, string> pair in dependencies) entries.Add("m/" + host.RelativePath(pair.Key), pair.Value);
                }
                foreach (KeyValuePair<string, string> pair in entries) {
                    token.ThrowIfCancellationRequested();
                    string? hash = host.Digest(pair.Value);
                    if (hash is null) throw new IOException("创建还原点期间文件被移走：" + pair.Key);
                    string blob = ObjectPath(storeRoot, hash);
                    if (!File.Exists(blob)) {
                        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
                        string temporary = blob + ".tmp-" + Guid.NewGuid().ToString("N");
                        try {
                            File.Copy(pair.Value, temporary, false);
                            if (!SameText(host.Digest(temporary), hash)) throw new IOException("创建还原点期间文件发生变化。");
                            File.Move(temporary, blob);
                        } finally {
                            if (File.Exists(temporary)) File.Delete(temporary);
                        }
                    } else if (!SameText(host.Digest(blob), hash)) {
                        throw new IOException("已有还原点内容校验失败，请保留备份并检查磁盘。");
                    }
                    if (!SameText(host.Digest(pair.Value), hash)) throw new IOException("创建还原点期间文件发生变化。");
                    content[pair.Key] = hash;
                }
                Dictionary<string, string> latest = Files(versionFolder, gameRoot);
                if (!new HashSet<string>(entries.Keys.Where(key => !key.StartsWith("m/")), StringComparer.OrdinalIgnoreCase).SetEquals(latest.Keys)) throw new IOException("创建还原点期间目录内容发生变化。");
                foreach (KeyValuePair<string, string> pair in latest) {
                    if (!SameText(host.Digest(pair.Value), content[pair.Key]!.ToString())) throw new IOException("创建还原点期间配置发生变化。");
                }
                JObject data = new(new JProperty("schema", 1), new JProperty("coverageVersion", EnvironmentHistoryRules.CurrentCoverageVersion), new JProperty("id", id), new JProperty("created", DateTime.UtcNow), new JProperty("label", label),
                    new JProperty("description", description), new JProperty("gameRoot", Path.GetFullPath(gameRoot)), new JProperty("healthy", false), new JProperty("manual", !EnvironmentHistoryRules.AutomaticLabels.Contains(label)), new JProperty("files", content));
                host.AtomicWrite(Safe(storeRoot, "points/" + id + ".json"), Encoding.UTF8.GetBytes(data.ToString()));
                return id;
            }
        }
    }
    public string CaptureInstance(TInstance instance, string label, CancellationToken token) {
        string mcRoot = Path.GetDirectoryName(Path.GetDirectoryName(host.VersionPath(instance).TrimEnd('\\'))) + "\\";
        Dictionary<string, string> dependencies = new(StringComparer.OrdinalIgnoreCase);
        foreach (string libraryPath in host.LibraryPaths(instance)) {
            if (host.IsParentOf(host.VersionPath(instance), libraryPath)) continue;
            host.EnsureWithin(mcRoot, libraryPath);
            string relative = host.RemoveExtendedPrefix(Path.GetFullPath(libraryPath)).Substring(mcRoot.Length).Replace("\\", "/");
            if (!relative.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith("versions/", StringComparison.OrdinalIgnoreCase)) throw new IOException("游戏依赖不在标准目录中，无法完整记录环境。");
            dependencies[relative] = libraryPath;
        }
        JObject json = host.JsonObject(instance);
        HashSet<string> seen = new();
        while (json["inheritsFrom"] is not null) {
            string name = json["inheritsFrom"]!.ToString();
            host.SafeName(name);
            if (!seen.Add(name) || seen.Count > 32) throw new IOException("版本继承关系无效。");
            string relative = "versions/" + name + "/" + name + ".json", address = host.Target(mcRoot, relative);
            dependencies[relative] = address;
            json = JObject.Parse(File.ReadAllText(address));
        }
        string id = Capture(host.VersionPath(instance), host.GameFolder(instance), label, token, dependencies,
            host.VanillaName(instance) + " · " + string.Join("、", host.ModLoaderVersions(instance)));
        ApplyRetention(instance, id, token);
        return id;
    }
    public List<JObject> Points(string versionFolder) {
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                string storeRoot = Store(versionFolder), folder = Path.Combine(storeRoot, "points");
                List<JObject> result = new();
                host.EnsureWithin(storeRoot, folder);
                if (!Directory.Exists(folder)) return result;
                foreach (string fileName in Directory.GetFiles(folder, "*.json")) {
                    host.EnsureWithin(storeRoot, fileName);
                    if (new FileInfo(fileName).Length > 32 * 1024 * 1024) throw new IOException("还原点清单过大。");
                    JObject value = JObject.Parse(File.ReadAllText(fileName));
                    if ((int)value["schema"]! != 1) throw new IOException("不支持此版本的环境还原点。");
                    result.Add(value);
                }
                return result.OrderByDescending(item => (DateTime)item["created"]!).ToList();
            }
        }
    }
    public bool IsManualPoint(JObject point) {
        JToken? value = point["manual"];
        if (value is not null) return value.Type != JTokenType.Boolean || value.ToObject<bool>();
        return !EnvironmentHistoryRules.AutomaticLabels.Contains(point["label"]?.ToString()!);
    }
    public void ApplyRetention(TInstance instance, string newestId, CancellationToken token) {
        if (!host.AutoCleanup(instance)) return;
        try {
            int removed = PruneAutomatic(host.VersionPath(instance), host.Retention(instance), newestId, token);
            if (removed > 0) host.Info($"已自动清理 {removed} 个旧环境还原点，手动还原点已保留。");
        } catch (OperationCanceledException) { throw; }
        catch (Exception error) { host.Warn(error, "新还原点已保存，但自动清理旧还原点失败"); }
    }
    private void AddReferences(JObject point, HashSet<string> references) {
        if ((int)point["schema"]! != 1 || !Regex.IsMatch(point["id"]?.ToString() ?? "", @"\A[a-f0-9]{32}\z")) throw new IOException("还原点清单无效，未清理旧还原点。");
        CoverageVersion(point);
        JObject? content = point["files"] as JObject;
        if (content is null) throw new IOException("还原点文件清单缺失，未清理旧还原点。");
        foreach (JProperty entry in content.Properties()) {
            string hash = entry.Value.ToString();
            if (!Regex.IsMatch(hash, @"\Asha1:[a-f0-9]{40}\z")) throw new IOException("还原点引用无效，未清理旧还原点。");
            references.Add(hash.Substring(5));
        }
    }
    public int PruneAutomatic(string versionFolder, int count, string? newestId, CancellationToken token) {
        if (count < 1 || count > 100) throw new IOException("自动还原点保留数量必须为 1 到 100。");
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                if (RestoringCount > 0) return 0;
                token.ThrowIfCancellationRequested();
                string storeRoot = Store(versionFolder);
                List<JObject> all = Points(versionFolder);
                HashSet<string> references = new(StringComparer.Ordinal), seen = new(StringComparer.Ordinal);
                foreach (JObject point in all) {
                    AddReferences(point, references);
                    string id = point["id"]!.ToString(), address = Safe(storeRoot, "points/" + id + ".json");
                    if (!seen.Add(id) || !File.Exists(address) || !JToken.DeepEquals(JObject.Parse(File.ReadAllText(address)), point)) throw new IOException("还原点列表已变化，未清理旧还原点。");
                }
                List<JObject> automatic = all.Where(point => !IsManualPoint(point)).ToList();
                if (automatic.Count <= count) return 0;
                HashSet<string> keep = new(automatic.Where(point => SameText(point["id"]!.ToString(), newestId)).Concat(automatic.Where(point => !SameText(point["id"]!.ToString(), newestId))).Take(count).Select(point => point["id"]!.ToString()));
                List<JObject> expired = automatic.Where(point => !keep.Contains(point["id"]!.ToString())).ToList();
                references.Clear();
                foreach (JObject point in all.Where(item => IsManualPoint(item) || keep.Contains(item["id"]!.ToString()))) AddReferences(point, references);
                string deleted = Path.Combine(storeRoot, "deleted");
                host.EnsureNoLinks(deleted);
                if (Directory.Exists(deleted)) {
                    foreach (string batch in Directory.GetDirectories(deleted)) {
                        host.EnsureNoLinks(batch);
                        if (Directory.GetDirectories(batch).Any()) throw new IOException("删除记录的目录结构未知，未清理旧还原点。");
                        foreach (string address in Directory.GetFiles(batch, "*.json")) {
                            host.EnsureNoLinks(address);
                            if (new FileInfo(address).Length > 32 * 1024 * 1024) throw new IOException("删除记录过大，未清理旧还原点。");
                            AddReferences(JObject.Parse(File.ReadAllText(address)), references);
                        }
                    }
                }
                string objects = Path.Combine(storeRoot, "objects");
                List<string> unused = new();
                host.EnsureNoLinks(objects);
                if (Directory.Exists(objects)) {
                    foreach (string address in Directory.GetFiles(objects)) {
                        host.EnsureNoLinks(address);
                        string hash = Path.GetFileName(address);
                        if (Regex.IsMatch(hash, @"\A[a-f0-9]{40}\z") && !references.Contains(hash)) unused.Add(address);
                    }
                }
                token.ThrowIfCancellationRequested();
                foreach (JObject point in expired) { token.ThrowIfCancellationRequested(); File.Delete(Safe(storeRoot, "points/" + point["id"]!.ToString() + ".json")); }
                foreach (string address in unused) { token.ThrowIfCancellationRequested(); host.EnsureNoLinks(address); File.Delete(address); }
                return expired.Count;
            }
        }
    }
    public void MarkHealthy(string versionFolder, string? id, bool healthy) {
        if (string.IsNullOrEmpty(id) || !healthy) return;
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                if (!Regex.IsMatch(id!, @"\A[a-f0-9]{32}\z")) throw new IOException("还原点编号无效。");
                string address = Safe(Store(versionFolder), "points/" + id + ".json");
                if (!File.Exists(address)) return;
                JObject value = JObject.Parse(File.ReadAllText(address));
                value["healthy"] = true;
                host.AtomicWrite(address, Encoding.UTF8.GetBytes(value.ToString()));
            }
        }
    }
    public void OpenFolder(string versionFolder) {
        string folder = Store(versionFolder);
        if (Directory.Exists(folder)) host.OpenFolder(folder);
        else host.Hint("此版本还没有环境还原点。");
    }
    public string? DeletePoints(string versionFolder, IEnumerable<string> ids) {
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                List<string> selected = ids.Distinct(StringComparer.Ordinal).ToList();
                string storeRoot = Store(versionFolder);
                if (selected.Count == 0) return null;
                List<Tuple<string, string>> moves = new();
                string deleted = "deleted/" + Guid.NewGuid().ToString("N") + "/";
                foreach (string id in selected) {
                    if (!Regex.IsMatch(id, @"\A[a-f0-9]{32}\z")) throw new IOException("还原点编号无效。");
                    string source = Safe(storeRoot, "points/" + id + ".json"), destination = Safe(storeRoot, deleted + id + ".json");
                    if (!File.Exists(source)) throw new IOException("还原点已变化，请刷新列表。");
                    moves.Add(Tuple.Create(source, destination));
                }
                List<Tuple<string, string>> moved = new();
                try {
                    foreach (Tuple<string, string> pair in moves) {
                        Directory.CreateDirectory(Path.GetDirectoryName(pair.Item2)!);
                        File.Move(pair.Item1, pair.Item2);
                        moved.Add(pair);
                    }
                } catch {
                    foreach (Tuple<string, string> pair in moved.AsEnumerable().Reverse()) File.Move(pair.Item2, pair.Item1);
                    throw;
                }
                return Path.GetDirectoryName(moves[0].Item2);
            }
        }
    }
}
