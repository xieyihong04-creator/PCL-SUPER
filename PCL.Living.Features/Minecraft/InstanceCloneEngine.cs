using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>LF 独立测试副本的实际扫描、复制、来源记录和来源版本迁回。</summary>
// LF InstanceCloner.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// unchanged through 8d61586e4627102ac2e360ccc7d4e092d8797e2f. No CE equivalent reused.
public sealed partial class InstanceCloneEngine<TInstance, TOptions, TResult, TChange, TPlan>
        where TInstance : class where TOptions : InstanceCloneOptionsData where TResult : InstanceCloneResultData
        where TChange : InstanceCloneMigrationChangeData where TPlan : InstanceCloneMigrationPlanData<TChange> {
    public const string OriginName = "PCL/CloneOrigin.json";
    private const int MaxFiles = 200000;
    private const long MaxBytes = 512L * 1024 * 1024 * 1024;
    private static readonly string[] EnvironmentFolders = { "mods", "coremods", "config", "defaultconfigs", "configureddefaults", "kubejs", "scripts", "resources", "resourcepacks", "shaderpacks", "texturepacks", "schematics", "global_packs", "openloader" };
    private static readonly HashSet<string> SetupKeys = new(new[] { "VersionRamType", "VersionRamCustom", "VersionRamOptimize", "VersionArgumentTitle", "VersionArgumentInfo", "VersionArgumentJavaSelect", "VersionArgumentJavaV2", "VersionArgumentJavaRange", "VersionAdvanceAssets", "VersionAdvanceAssetsV2", "VersionAdvanceDisableJLW", "VersionAdvanceDisableLUA", "VersionAdvanceDisableModUpdate", "VersionAdvanceGC", "CustomInfo", "Logo", "LogoCustom" }, StringComparer.Ordinal);
    public readonly object Gate;
    private readonly InstanceCloneHost<TInstance> host;
    private readonly Func<TOptions> createOptions;
    private readonly Func<TResult> createResult;
    private readonly Func<TChange> createChange;
    private readonly Func<TPlan> createPlan;
    public InstanceCloneEngine(object gate, InstanceCloneHost<TInstance> host, Func<TOptions> createOptions, Func<TResult> createResult,
        Func<TChange> createChange, Func<TPlan> createPlan) {
        Gate = gate; this.host = host; this.createOptions = createOptions; this.createResult = createResult; this.createChange = createChange; this.createPlan = createPlan;
    }
    private static bool SameText(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);
    public string NormalizeRoot(string value) {
        if (string.IsNullOrWhiteSpace(value)) throw new IOException("未指定游戏目录。");
        string full = Path.GetFullPath(host.RemoveExtendedPrefix(value));
        if (full.Length > Path.GetPathRoot(full)!.Length) full = full.TrimEnd('\\', '/');
        EnsureNoLinks(full);
        return full;
    }
    public void EnsureNoLinks(string value) {
        string? current = Path.GetFullPath(host.RemoveExtendedPrefix(value));
        while (!string.IsNullOrEmpty(current)) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current!) & FileAttributes.ReparsePoint) != 0) throw new IOException("目录包含链接，已停止操作，请改用普通独立目录。");
            string? parent = Path.GetDirectoryName(current!);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }
    private string Target(string root, string relative) {
        string full = Path.Combine(root, host.RelativePath(relative).Replace('/', Path.DirectorySeparatorChar));
        host.EnsureWithin(root, full);
        EnsureNoLinks(full);
        return full;
    }
    private string MinecraftRoot(string versionFolder) {
        string? versions = Path.GetDirectoryName(versionFolder);
        if (!string.Equals(Path.GetFileName(versions), "versions", StringComparison.OrdinalIgnoreCase)) throw new IOException("版本必须位于 Minecraft 的 versions 目录中。");
        return NormalizeRoot(Path.GetDirectoryName(versions!)!);
    }
    private void ValidateRoots(string versionFolder, string gameRoot) {
        if (!Directory.Exists(versionFolder)) throw new DirectoryNotFoundException("找不到原版本目录。");
        string mcRoot = MinecraftRoot(versionFolder);
        if (!host.SamePath(gameRoot, versionFolder) && !host.SamePath(gameRoot, mcRoot)) throw new IOException("当前游戏目录不属于此 Minecraft 文件夹。");
    }
    private void AssertStopped(params string[] gameFolders) {
        if (host.HasRunningGame() || host.HasRunningSessions() || host.LaunchLoading() || host.WorldBackupBusy()) throw new IOException("请先关闭游戏并等待启动或存档备份任务完成。");
        foreach (string folder in gameFolders) {
            if (!string.IsNullOrWhiteSpace(folder) && host.HasActiveGame(folder)) throw new IOException("该游戏目录仍有运行中的游戏，请关闭后再克隆或迁移。");
        }
    }
    public string GameRootForInstance(TInstance? instance) {
        if (instance is null) throw new ArgumentNullException("Instance");
        string folder = NormalizeRoot(host.VersionPath(instance)), mcRoot = MinecraftRoot(folder);
        string setupFile = Target(folder, "PCL/Setup.ini");
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (File.Exists(setupFile)) {
            foreach (string line in ReadSmallText(setupFile, 1024 * 1024).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)) {
                int split = line.IndexOf(':');
                if (split > 0) values[line.Substring(0, split)] = line.Substring(split + 1);
            }
        }
        if (values.TryGetValue("VersionArgumentIndieV2", out string? raw) && bool.TryParse(raw, out bool isolated)) return isolated ? folder : mcRoot;
        if (values.TryGetValue("VersionArgumentIndie", out raw) && int.TryParse(raw, out int old) && old > 0) return old == 1 ? folder : mcRoot;
        string mods = Path.Combine(folder, "mods"), saves = Path.Combine(folder, "saves");
        EnsureNoLinks(mods); EnsureNoLinks(saves);
        if ((Directory.Exists(mods) && Directory.EnumerateFiles(mods).Any()) || (Directory.Exists(saves) && Directory.EnumerateDirectories(saves).Any())) return folder;
        bool release = !host.IsFool(instance) && !host.IsSnapshot(instance) && !SameText(host.VanillaName(instance), "Old");
        bool modable = host.HasModLoaders(instance) || host.ApiType(instance);
        isolated = host.IsolationMode() switch { 0 => false, 1 => modable, 2 => !release, 3 => !release || modable, _ => true };
        return isolated ? folder : mcRoot;
    }
    private string ReadSmallText(string address, long limit) {
        EnsureNoLinks(address);
        if (new FileInfo(address).Length > limit) throw new IOException("环境描述文件过大。");
        return File.ReadAllText(address);
    }
    private string JsonFile(string versionFolder) {
        string exact = Target(versionFolder, Path.GetFileName(versionFolder) + ".json");
        if (File.Exists(exact)) return exact;
        foreach (string candidate in Directory.EnumerateFiles(versionFolder, "*.json").Take(128)) {
            JObject data = JObject.Parse(ReadSmallText(candidate, 16 * 1024 * 1024));
            if (data["mainClass"] is not null && data["id"] is not null && data["type"] is not null) return candidate;
        }
        throw new IOException("没有找到有效的 Minecraft 版本 JSON。");
    }
    private static bool Excluded(string relative) {
        string[] parts = relative.Replace("\\", "/").ToLowerInvariant().Split('/');
        return parts.Any(part => part.StartsWith(".pcl-", StringComparison.Ordinal) || new[] { ".cache", "cache", "caches", ".connector", "logs", "crash-reports", "natives", "backups", "backup", "account", "accounts", ".git" }.Contains(part)) ||
            parts.Any(part => Regex.IsMatch(part, @"\A(?:accounts?|credentials?|access[_-]?tokens?|auth[_-]?tokens?|launcher_accounts|usercache|usernamecache|launcher_profiles)(?:\.|$)", RegexOptions.IgnoreCase)) ||
            relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
    }
    private void Walk(string root, string folder, string prefix, Dictionary<string, string> result, CancellationToken token, int depth = 0) {
        token.ThrowIfCancellationRequested();
        if (depth > 64) throw new IOException("游戏目录嵌套过深。");
        EnsureNoLinks(folder);
        if (!Directory.Exists(folder)) return;
        foreach (string address in Directory.EnumerateFileSystemEntries(folder)) {
            token.ThrowIfCancellationRequested();
            host.EnsureWithin(root, address);
            string relative = address.Substring(root.Length + 1).Replace("\\", "/");
            if (Excluded(relative)) continue;
            EnsureNoLinks(address);
            if (Directory.Exists(address)) Walk(root, address, prefix, result, token, depth + 1);
            else { result.Add(prefix + relative, address); if (result.Count > MaxFiles) throw new IOException("环境文件超过 200000 个，请先缩小复制范围。"); }
        }
    }
    private Dictionary<string, string> Inventory(string versionFolder, string gameRoot, TOptions options, CancellationToken token) {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        string primaryJson = JsonFile(versionFolder);
        result.Add("v/" + Path.GetFileName(primaryJson), primaryJson);
        foreach (string address in Directory.EnumerateFiles(versionFolder, "*.jar")) { EnsureNoLinks(address); result.Add("v/" + Path.GetFileName(address), address); }
        foreach (string name in new[] { "PCL/Setup.ini", "PCL/config.json", "PCL/Logo.png" }) { string address = Target(versionFolder, name); if (File.Exists(address)) result.Add("v/" + name, address); }
        foreach (string name in EnvironmentFolders.Concat(options.CopySaves ? new[] { "saves" } : new string[0]).Concat(options.CopyScreenshots ? new[] { "screenshots" } : new string[0])) Walk(gameRoot, Path.Combine(gameRoot, name), "g/", result, token);
        foreach (string address in Directory.EnumerateFiles(gameRoot)) {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(address).ToLowerInvariant();
            if (name == "options.txt" || name.StartsWith("optionsof") || name.StartsWith("optionsshaders") || new[] { ".cfg", ".toml", ".properties" }.Contains(Path.GetExtension(name)) || (options.CopyServers && new[] { "servers.dat", "servers.dat_old" }.Contains(name))) {
                if (!Excluded(name)) { EnsureNoLinks(address); result.Add("g/" + Path.GetFileName(address), address); }
            }
        }
        if (result.Count > MaxFiles) throw new IOException("复制文件过多。");
        return result;
    }
    public string Digest(string address, CancellationToken token) {
        EnsureNoLinks(address);
        using (FileStream input = new(address, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (SHA1 hash = SHA1.Create()) {
            byte[] buffer = new byte[128 * 1024];
            while (true) { token.ThrowIfCancellationRequested(); int count = input.Read(buffer, 0, buffer.Length); if (count == 0) break; hash.TransformBlock(buffer, 0, count, null, 0); }
            hash.TransformFinalBlock(new byte[0], 0, 0);
            return "sha1:" + BitConverter.ToString(hash.Hash!).Replace("-", "").ToLowerInvariant();
        }
    }
    private Dictionary<string, string> Fingerprints(Dictionary<string, string> entries, CancellationToken token) {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (KeyValuePair<string, string> pair in entries) {
            token.ThrowIfCancellationRequested();
            bytes = checked(bytes + new FileInfo(pair.Value).Length);
            if (bytes > MaxBytes) throw new IOException("复制范围超过 512 GiB，请先缩小复制范围。");
            result.Add(pair.Key, Digest(pair.Value, token));
        }
        return result;
    }
    private static void Verify(Dictionary<string, string> expected, Dictionary<string, string> actual) {
        if (expected.Count != actual.Count || expected.Any(pair => !actual.ContainsKey(pair.Key) || !SameText(actual[pair.Key], pair.Value))) throw new IOException("操作期间环境目录发生变化，请等待修改完成后重试。");
    }
    private void CopyFile(string source, string destination, string expectedHash, CancellationToken token) {
        EnsureNoLinks(source); EnsureNoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            byte[] buffer = new byte[128 * 1024];
            while (true) { token.ThrowIfCancellationRequested(); int count = input.Read(buffer, 0, buffer.Length); if (count == 0) break; output.Write(buffer, 0, count); }
            output.Flush(true);
        }
        if (!SameText(Digest(destination, token), expectedHash)) throw new IOException("复制期间来源文件发生变化，未生成测试副本。");
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }
    private void DeleteStage(string parent, string stage) {
        if (!Directory.Exists(stage)) return;
        host.EnsureWithin(parent, stage);
        EnsureNoLinks(stage);
        if (!Regex.IsMatch(Path.GetFileName(stage), @"\A\.pcl-clone-[a-f0-9]{32}\z")) throw new IOException("无效的克隆临时目录。");
        Stack<string> pending = new(); List<string> folders = new(); pending.Push(stage);
        while (pending.Count > 0) {
            string folder = pending.Pop(); EnsureNoLinks(folder); folders.Add(folder);
            foreach (string item in Directory.EnumerateFileSystemEntries(folder)) { EnsureNoLinks(item); if (Directory.Exists(item)) pending.Push(item); else File.Delete(item); }
        }
        foreach (string folder in folders.AsEnumerable().Reverse()) Directory.Delete(folder, false);
    }
    private static void RewritePaths(JContainer data, string oldFolder, string newFolder) {
        foreach (JValue value in data.DescendantsAndSelf().OfType<JValue>().Where(item => item.Type == JTokenType.String)) {
            string text = (string)value.Value!;
            text = text.Replace(oldFolder + "\\", newFolder + "\\").Replace(oldFolder.Replace("\\", "/") + "/", newFolder.Replace("\\", "/") + "/");
            if (SameText(text, oldFolder)) text = newFolder;
            value.Value = text;
        }
    }
    private void PrepareVersion(string stage, string oldFolder, string newFolder, string oldJsonName, string newName) {
        string jsonAddress = Target(stage, oldJsonName);
        JObject data = JObject.Parse(ReadSmallText(jsonAddress, 16 * 1024 * 1024));
        string oldName = Path.GetFileName(oldFolder);
        string? inherit = data["inheritsFrom"]?.ToString();
        if (SameText(inherit, oldName)) throw new IOException("版本 JSON 包含自引用继承关系，无法安全克隆。");
        if (inherit is not null) host.SafeName(inherit);
        if (data["jar"] is not null) { string jarName = data["jar"]!.ToString(); host.SafeName(jarName); if (SameText(jarName, oldName)) data["jar"] = newName; }
        data["id"] = newName;
        RewritePaths(data, oldFolder, newFolder);
        File.Delete(jsonAddress);
        File.WriteAllText(Target(stage, newName + ".json"), data.ToString(), new UTF8Encoding(false));
        string oldJar = Target(stage, oldName + ".jar"), newJar = Target(stage, newName + ".jar");
        if (File.Exists(oldJar) && !host.SamePath(oldJar, newJar)) File.Move(oldJar, newJar);
        string setupAddress = Target(stage, "PCL/Setup.ini"); StringBuilder setup = new();
        if (File.Exists(setupAddress)) {
            foreach (string line in ReadSmallText(setupAddress, 1024 * 1024).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)) {
                int separator = line.IndexOf(':');
                if (separator > 0 && SetupKeys.Contains(line.Substring(0, separator))) setup.AppendLine(line.Replace(oldFolder + "\\", newFolder + "\\"));
            }
        }
        setup.AppendLine("VersionArgumentIndie:1").AppendLine("VersionArgumentIndieV2:True").AppendLine("IsStar:False").AppendLine("CustomInfo:测试副本 · 来自 " + oldName);
        Directory.CreateDirectory(Path.GetDirectoryName(setupAddress)!);
        File.WriteAllText(setupAddress, setup.ToString(), new UTF8Encoding(false));
        string configAddress = Target(stage, "PCL/config.json");
        if (File.Exists(configAddress)) {
            JObject config = JObject.Parse(ReadSmallText(configAddress, 16 * 1024 * 1024)), safe = new();
            foreach (JProperty entry in config.Properties().Where(item => item.Name.Equals("MinecraftInstallManifest", StringComparison.OrdinalIgnoreCase))) safe[entry.Name] = entry.Value.DeepClone();
            RewritePaths(safe, oldFolder, newFolder);
            File.WriteAllText(configAddress, safe.ToString(), new UTF8Encoding(false));
        }
    }
    public TResult CloneInstance(TInstance instance, string newName, TOptions? options, CancellationToken token) => Clone(host.VersionPath(instance), GameRootForInstance(instance), newName, options, token);
    public TResult Clone(string versionFolder, string gameRoot, string newName, TOptions? options, CancellationToken token, Action<int>? afterFileCopied = null) {
        lock (Gate) {
            AssertStopped(gameRoot);
            token.ThrowIfCancellationRequested();
            versionFolder = NormalizeRoot(versionFolder); gameRoot = NormalizeRoot(gameRoot);
            ValidateRoots(versionFolder, gameRoot);
            newName = host.SafeName(newName);
            if (newName.Length > 100 || newName.IndexOfAny(new[] { '!', ';' }) >= 0 || Regex.IsMatch(newName, @"\A(?:COM0|LPT0|CLOCK\$)(?:\.|$)|.{2,}~\d", RegexOptions.IgnoreCase)) throw new IOException("测试版本名称包含 Minecraft 不支持的字符。");
            options ??= createOptions();
            string parent = Path.GetDirectoryName(versionFolder)!, destination = Path.Combine(parent, newName), stage = Path.Combine(parent, ".pcl-clone-" + Guid.NewGuid().ToString("N"));
            EnsureNoLinks(destination);
            if (File.Exists(destination) || Directory.Exists(destination) || host.SamePath(versionFolder, destination)) throw new IOException("同名版本已存在，请使用新的测试版本名称。");
            if (host.HasPending(versionFolder)) throw new IOException("原版本有未完成的环境修改，请先完成恢复后再克隆。");
            Dictionary<string, string> entries = Inventory(versionFolder, gameRoot, options, token), before = Fingerprints(entries, token);
            int count = 0; long bytes = 0;
            Directory.CreateDirectory(stage);
            try {
                foreach (KeyValuePair<string, string> pair in entries) {
                    token.ThrowIfCancellationRequested();
                    string relative = pair.Key.Substring(2), targetFile = Target(stage, relative);
                    CopyFile(pair.Value, targetFile, before[pair.Key], token);
                    count = checked(count + 1); bytes = checked(bytes + new FileInfo(targetFile).Length);
                    afterFileCopied?.Invoke(count);
                }
                string originalJson = Path.GetFileName(JsonFile(versionFolder));
                PrepareVersion(stage, versionFolder, destination, originalJson, newName);
                JObject origin = new(new JProperty("schema", 1), new JProperty("sourceVersionFolder", versionFolder), new JProperty("sourceGameRoot", gameRoot), new JProperty("sourceName", Path.GetFileName(versionFolder)), new JProperty("cloneName", newName), new JProperty("created", DateTime.UtcNow));
                File.WriteAllText(Target(stage, OriginName), origin.ToString(), new UTF8Encoding(false));
                AssertStopped(gameRoot);
                Verify(before, Fingerprints(Inventory(versionFolder, gameRoot, options, token), token));
                token.ThrowIfCancellationRequested();
                EnsureNoLinks(parent); EnsureNoLinks(stage); EnsureNoLinks(destination);
                if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("目标名称已被其他操作占用。");
                Directory.Move(stage, destination);
                TResult result = createResult(); result.Name = newName; result.VersionFolder = host.AddSlashSuffix(destination); result.FileCount = count; result.Bytes = bytes;
                return result;
            } finally { DeleteStage(parent, stage); }
        }
    }
}
