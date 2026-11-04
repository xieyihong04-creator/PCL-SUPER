using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

public sealed partial class InstanceCloneEngine<TInstance, TOptions, TResult, TChange, TPlan>
        where TInstance : class where TOptions : InstanceCloneOptionsData where TResult : InstanceCloneResultData
        where TChange : InstanceCloneMigrationChangeData where TPlan : InstanceCloneMigrationPlanData<TChange> {
    private JObject ReadOrigin(string cloneFolder) {
        JObject data = JObject.Parse(ReadSmallText(Target(cloneFolder, OriginName), 1024 * 1024));
        if ((int)data["schema"]! != 1 || data["sourceVersionFolder"] is null || data["sourceGameRoot"] is null) throw new IOException("测试副本来源记录无效。");
        string source = NormalizeRoot(data["sourceVersionFolder"]!.ToString()), root = NormalizeRoot(data["sourceGameRoot"]!.ToString());
        ValidateRoots(source, root);
        if (host.SamePath(source, cloneFolder) || !host.SamePath(MinecraftRoot(source), MinecraftRoot(cloneFolder)) || !SameText(data["cloneName"]?.ToString(), Path.GetFileName(cloneFolder))) throw new IOException("副本已移动或重命名，无法自动迁移，请手动管理版本。");
        return data;
    }
    public string SourceVersionFolder(string cloneFolder) => NormalizeRoot(ReadOrigin(NormalizeRoot(cloneFolder))["sourceVersionFolder"]!.ToString());
    private Dictionary<string, string> MigrationFiles(string versionFolder, string gameRoot, CancellationToken token) {
        Dictionary<string, string> all = Inventory(versionFolder, gameRoot, createOptions(), token);
        return all.Where(pair => pair.Key.StartsWith("g/", StringComparison.OrdinalIgnoreCase) ? EnvironmentHistoryRules.IsGameFile(pair.Key.Substring(2)) :
            !pair.Key.Equals("v/PCL/Setup.ini", StringComparison.OrdinalIgnoreCase) && !pair.Key.Equals("v/PCL/Logo.png", StringComparison.OrdinalIgnoreCase)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
    private string MinecraftName(string versionFolder) {
        JObject data = JObject.Parse(ReadSmallText(JsonFile(versionFolder), 16 * 1024 * 1024));
        if (SameText(data["jar"]?.ToString(), Path.GetFileName(versionFolder))) data.Remove("jar");
        JObject? jarMetadata = null;
        string jarAddress = Target(versionFolder, Path.GetFileName(versionFolder) + ".jar");
        if (File.Exists(jarAddress)) {
            try {
                using (ZipArchive archive = ZipFile.OpenRead(jarAddress)) {
                    ZipArchiveEntry? entry = archive.GetEntry("version.json");
                    if (entry is not null && entry.Length <= 1024 * 1024) {
                        using (StreamReader reader = new(entry.Open())) jarMetadata = JObject.Parse(reader.ReadToEnd());
                    }
                }
            } catch (Exception error) when (error is IOException || error is InvalidDataException || error is JsonException) { }
        }
        try {
            McVersion version = new(Path.GetFileName(versionFolder), data, jarMetadata);
            if (!SameText(version.VanillaName, "Unknown")) return version.VanillaName;
        } catch { }
        string configAddress = Target(versionFolder, "PCL/config.json");
        if (File.Exists(configAddress)) {
            JObject config = JObject.Parse(ReadSmallText(configAddress, 16 * 1024 * 1024));
            string? name = config["MinecraftInstallManifest"]?["Components"]?["MinecraftVersion"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name) && name!.Length <= 100) return name;
        }
        return "未知版本";
    }
    private string MapVersionKey(string key, string oldFolder, string newFolder) {
        if (!key.StartsWith("v/", StringComparison.OrdinalIgnoreCase)) return key;
        if (string.Equals(key, "v/" + Path.GetFileName(JsonFile(oldFolder)), StringComparison.OrdinalIgnoreCase)) return "v/" + Path.GetFileName(JsonFile(newFolder));
        if (string.Equals(key, "v/" + Path.GetFileName(oldFolder) + ".jar", StringComparison.OrdinalIgnoreCase)) return "v/" + Path.GetFileName(newFolder) + ".jar";
        return key;
    }
    private JObject MigrationConfig(string? cloneConfig, string sourceFolder) {
        string sourceAddress = Target(sourceFolder, "PCL/config.json");
        JObject result = File.Exists(sourceAddress) ? JObject.Parse(ReadSmallText(sourceAddress, 16 * 1024 * 1024)) : new JObject();
        JObject incoming = cloneConfig is null ? new JObject() : JObject.Parse(ReadSmallText(cloneConfig, 16 * 1024 * 1024));
        foreach (JProperty existing in result.Properties().Where(item => item.Name.Equals("MinecraftInstallManifest", StringComparison.OrdinalIgnoreCase)).ToList()) existing.Remove();
        foreach (JProperty entry in incoming.Properties().Where(item => item.Name.Equals("MinecraftInstallManifest", StringComparison.OrdinalIgnoreCase))) result[entry.Name] = entry.Value.DeepClone();
        return result;
    }
    public TPlan PreviewMigration(string cloneFolder, string sourceGameRoot, CancellationToken token) {
        AssertStopped(cloneFolder);
        cloneFolder = NormalizeRoot(cloneFolder);
        JObject origin = ReadOrigin(cloneFolder);
        string source = NormalizeRoot(origin["sourceVersionFolder"]!.ToString());
        sourceGameRoot = NormalizeRoot(sourceGameRoot);
        if (!host.SamePath(sourceGameRoot, origin["sourceGameRoot"]!.ToString())) throw new IOException("原版本的隔离设置已改变，请手动检查游戏目录。");
        if (host.HasPending(source) || host.HasPending(cloneFolder)) throw new IOException("版本有未完成的环境修改，请先恢复。");
        AssertStopped(sourceGameRoot, cloneFolder);
        Dictionary<string, string> sourceEntries = MigrationFiles(source, sourceGameRoot, token), cloneEntries = MigrationFiles(cloneFolder, cloneFolder, token);
        TPlan plan = createPlan();
        plan.SourceVersionFolder = source; plan.SourceGameRoot = sourceGameRoot; plan.CloneVersionFolder = cloneFolder;
        plan.OldMinecraft = MinecraftName(source); plan.NewMinecraft = MinecraftName(cloneFolder);
        plan.SourceFiles = Fingerprints(sourceEntries, token); plan.CloneFiles = Fingerprints(cloneEntries, token); plan.OriginHash = Digest(Target(cloneFolder, OriginName), token);
        Dictionary<string, string> incoming = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in plan.CloneFiles) incoming.Add(MapVersionKey(pair.Key, cloneFolder, source), pair.Value);
        string sourceJson = "v/" + Path.GetFileName(JsonFile(source));
        JObject cloneJson = JObject.Parse(ReadSmallText(JsonFile(cloneFolder), 16 * 1024 * 1024));
        cloneJson["id"] = Path.GetFileName(source);
        if (SameText(cloneJson["jar"]?.ToString(), Path.GetFileName(cloneFolder))) cloneJson["jar"] = Path.GetFileName(source);
        RewritePaths(cloneJson, cloneFolder, source);
        if (JToken.DeepEquals(cloneJson, JObject.Parse(ReadSmallText(JsonFile(source), 16 * 1024 * 1024)))) incoming[sourceJson] = plan.SourceFiles[sourceJson];
        if (cloneEntries.ContainsKey("v/PCL/config.json") || sourceEntries.ContainsKey("v/PCL/config.json")) {
            cloneEntries.TryGetValue("v/PCL/config.json", out string? configPayload);
            JObject mergedConfig = MigrationConfig(configPayload, source);
            if (sourceEntries.ContainsKey("v/PCL/config.json") && JToken.DeepEquals(mergedConfig, JObject.Parse(ReadSmallText(sourceEntries["v/PCL/config.json"], 16 * 1024 * 1024)))) incoming["v/PCL/config.json"] = plan.SourceFiles["v/PCL/config.json"];
            else {
                using (SHA1 hash = SHA1.Create()) incoming["v/PCL/config.json"] = "sha1:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(mergedConfig.ToString()))).Replace("-", "").ToLowerInvariant();
            }
        }
        foreach (string key in plan.SourceFiles.Keys.Union(incoming.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(value => value)) {
            plan.SourceFiles.TryGetValue(key, out string? before); incoming.TryGetValue(key, out string? after);
            if (SameText(before, after)) continue;
            TChange change = createChange(); change.Relative = key; change.Kind = before is null ? "新增" : after is null ? "删除" : "修改"; plan.Changes.Add(change);
        }
        return plan;
    }
    public string? ApplyMigration(TPlan? plan, CancellationToken token, Func<string>? captureBefore = null,
        Action<string, string>? beforeMinecraftChange = null, Action<int>? beforeApply = null) {
        if (plan is null) throw new ArgumentNullException("Plan");
        lock (Gate) {
            AssertStopped(plan.SourceGameRoot!, plan.CloneVersionFolder!);
            TPlan fresh = PreviewMigration(plan.CloneVersionFolder!, plan.SourceGameRoot!, token);
            if (!host.SamePath(fresh.SourceVersionFolder!, plan.SourceVersionFolder!) || !SameText(fresh.OriginHash, plan.OriginHash)) throw new IOException("副本来源记录已变化，请重新预览。");
            Verify(plan.SourceFiles!, fresh.SourceFiles!); Verify(plan.CloneFiles!, fresh.CloneFiles!);
            if (!SameText(plan.OldMinecraft, fresh.OldMinecraft) || !SameText(plan.NewMinecraft, fresh.NewMinecraft)) throw new IOException("Minecraft 版本已变化，请重新预览。");
            if (fresh.Changes.Count == 0) return null;
            if (SameText(fresh.OldMinecraft, "未知版本") || SameText(fresh.NewMinecraft, "未知版本")) throw new IOException("无法确认 Minecraft 版本，自动迁移已停止。");
            if (!SameText(fresh.OldMinecraft, fresh.NewMinecraft)) {
                if (beforeMinecraftChange is null) throw new IOException("迁移会更改 Minecraft 版本，需要先完成原存档备份。");
                beforeMinecraftChange(fresh.OldMinecraft!, fresh.NewMinecraft!);
            }
            token.ThrowIfCancellationRequested();
            string undo = captureBefore is null ? host.CaptureEnvironment(fresh.SourceVersionFolder!, fresh.SourceGameRoot!, "测试副本迁移前", token) : captureBefore();
            if (string.IsNullOrEmpty(undo)) throw new IOException("未能保存原环境还原点，迁移已停止。");
            string parent = Path.GetDirectoryName(fresh.CloneVersionFolder!)!, stage = Path.Combine(parent, ".pcl-clone-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try {
                Dictionary<string, string> incoming = new(StringComparer.OrdinalIgnoreCase), cloneEntries = MigrationFiles(fresh.CloneVersionFolder!, fresh.CloneVersionFolder!, token);
                foreach (KeyValuePair<string, string> pair in cloneEntries) {
                    string key = MapVersionKey(pair.Key, fresh.CloneVersionFolder!, fresh.SourceVersionFolder!), payload = Target(stage, key.Substring(2));
                    CopyFile(pair.Value, payload, fresh.CloneFiles![pair.Key], token); incoming.Add(key, payload);
                }
                string jsonKey = "v/" + Path.GetFileName(JsonFile(fresh.SourceVersionFolder!));
                JObject data = JObject.Parse(ReadSmallText(incoming[jsonKey], 16 * 1024 * 1024));
                data["id"] = Path.GetFileName(fresh.SourceVersionFolder);
                if (SameText(data["jar"]?.ToString(), Path.GetFileName(fresh.CloneVersionFolder))) data["jar"] = Path.GetFileName(fresh.SourceVersionFolder);
                RewritePaths(data, fresh.CloneVersionFolder!, fresh.SourceVersionFolder!);
                File.WriteAllText(incoming[jsonKey], data.ToString(), new UTF8Encoding(false));
                if (incoming.ContainsKey("v/PCL/config.json")) File.WriteAllText(incoming["v/PCL/config.json"], MigrationConfig(incoming["v/PCL/config.json"], fresh.SourceVersionFolder!).ToString(), new UTF8Encoding(false));
                if (!incoming.ContainsKey("v/PCL/config.json") && fresh.SourceFiles!.ContainsKey("v/PCL/config.json")) {
                    string configPayload = Target(stage, "PCL/config.json"); Directory.CreateDirectory(Path.GetDirectoryName(configPayload)!);
                    File.WriteAllText(configPayload, MigrationConfig(null, fresh.SourceVersionFolder!).ToString(), new UTF8Encoding(false)); incoming.Add("v/PCL/config.json", configPayload);
                }
                List<Tuple<string, string?, string?>> operations = new();
                foreach (string key in fresh.SourceFiles!.Keys.Union(incoming.Keys, StringComparer.OrdinalIgnoreCase)) {
                    fresh.SourceFiles.TryGetValue(key, out string? before); incoming.TryGetValue(key, out string? payload);
                    if (payload is not null && SameText(Digest(payload, token), before)) continue;
                    string destination = Target(key.StartsWith("v/") ? fresh.SourceVersionFolder! : fresh.SourceGameRoot!, key.Substring(2)); operations.Add(Tuple.Create<string, string?, string?>(destination, payload, before));
                }
                AssertStopped(plan.SourceGameRoot!, plan.CloneVersionFolder!);
                Verify(fresh.SourceFiles, Fingerprints(MigrationFiles(fresh.SourceVersionFolder!, fresh.SourceGameRoot!, token), token));
                Verify(fresh.CloneFiles!, Fingerprints(MigrationFiles(fresh.CloneVersionFolder!, fresh.CloneVersionFolder!, token), token));
                host.ApplyTransaction(fresh.SourceGameRoot!, fresh.SourceVersionFolder!, operations, token, index => { token.ThrowIfCancellationRequested(); beforeApply?.Invoke(index); });
                return undo;
            } finally { DeleteStage(parent, stage); }
        }
    }
}
