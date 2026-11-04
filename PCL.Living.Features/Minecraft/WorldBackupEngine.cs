using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>LF world-backup engine: validated archives, retention, recovery and game sessions.</summary>
public sealed partial class WorldBackupEngine<TInstance> where TInstance : class {
    // Ported from LF WorldBackups.vb, retaining LF's on-disk schema and commit boundaries.
    // CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 has no corresponding implementation.
    private readonly WorldBackupHost<TInstance> host;
    private int busy;
    private const int MaxFiles = 500000;
    private const long MaxManifest = 64L * 1024 * 1024;

    public WorldBackupEngine(WorldBackupHost<TInstance> host) { this.host = host; }
    public object Gate { get; } = new();
    public string? TestRoot { get; set; }
    public int BusyCount => Volatile.Read(ref busy);
    public bool IsBusy => BusyCount != 0 || host.UiBusy();

    private sealed class BusyScope : IDisposable {
        private readonly WorldBackupEngine<TInstance> owner;
        public BusyScope(WorldBackupEngine<TInstance> owner) { this.owner = owner; Interlocked.Increment(ref owner.busy); }
        public void Dispose() { Interlocked.Decrement(ref owner.busy); }
    }

    private string Full(string value) {
        if (string.IsNullOrWhiteSpace(value)) throw new IOException("存档目录为空。");
        string result = Path.GetFullPath(host.RemoveExtendedPrefix(value));
        return result.Length == Path.GetPathRoot(result)!.Length ? result : result.TrimEnd('\\', '/');
    }

    private static void NoLinks(string value) {
        string? current = Path.GetFullPath(value);
        while (!string.IsNullOrEmpty(current)) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("存档备份不支持链接文件或链接目录。");
            string? parent = Path.GetDirectoryName(current.TrimEnd('\\', '/'));
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    private string Safe(string root, string relative) {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw new IOException("备份包含无效的相对路径。");
        foreach (string part in relative.Replace('\\', '/').Split('/')) SafePart(part);
        string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(Full(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份路径超出了当前目录。");
        NoLinks(result);
        return result;
    }

    private static void SafePart(string value) {
        if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value != value.TrimEnd() || value.EndsWith(".") ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(value, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
            throw new IOException("备份包含无效的文件或存档名称。");
    }

    private static void WorldName(string value) {
        SafePart(value);
        if (Path.GetFileName(value) != value || value.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase)) throw new IOException("存档文件夹名称无效。");
    }

    private static void PointId(string value) {
        if (value is null || !Regex.IsMatch(value, @"\A[a-f0-9]{32}\z")) throw new IOException("存档备份编号无效。");
    }

    private static string DigestName(string value) {
        using SHA256 algorithm = SHA256.Create();
        return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
    }

    public string Store(string gameFolder) {
        string root = TestRoot ?? host.ProfileAddress("world-backups");
        NoLinks(root);
        return Safe(root, DigestName(Full(gameFolder).ToUpperInvariant()));
    }

    private string PointFolder(string gameFolder, string id) { PointId(id); return Safe(Store(gameFolder), "points/" + id); }
    private string WorldFolder(string gameFolder, string identifier) { WorldName(identifier); return Safe(Full(gameFolder), "saves/" + identifier); }
    private static int Retain(int value) {
        if (value < 1 || value > 50) throw new IOException("每个世界保留的备份份数必须为 1 到 50。");
        return value;
    }

    private static void WriteJson(string fileName, JObject value) {
        NoLinks(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
        string temporary = fileName + ".pcl-" + Guid.NewGuid().ToString("N");
        try {
            byte[] bytes = new UTF8Encoding(false).GetBytes(value.ToString(Formatting.None));
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            NoLinks(fileName);
            if (File.Exists(fileName)) File.Replace(temporary, fileName, null);
            else File.Move(temporary, fileName);
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static JObject ReadJson(string fileName) {
        NoLinks(fileName);
        if (!File.Exists(fileName) || new FileInfo(fileName).Length > MaxManifest) throw new IOException("备份记录已丢失或过大。");
        using StreamReader input = new(fileName, new UTF8Encoding(false, true));
        return JObject.Parse(input.ReadToEnd());
    }

    private static bool Alive(Process? game) {
        if (game is null) return true;
        try { return !game.HasExited; } catch { return true; }
    }

    public bool HasActiveGame(string gameFolder, Process? exitedProcess = null) {
        string target = Full(gameFolder);
        if (exitedProcess is not null && Alive(exitedProcess)) return true;
        // An exit callback may still have its old watcher in the launcher's list.
        foreach (WorldBackupWatcher<TInstance>? watch in host.Watchers()) {
            if (watch is null) continue;
            if (watch.Process is not null && !Alive(watch.Process)) continue;
            if (watch.Process is null && (watch.State == WorldBackupWatcherState.Ended || watch.State == WorldBackupWatcherState.Canceled)) continue;
            try {
                if (watch.Instance is null || string.Equals(Full(host.GameFolder(watch.Instance)), target, StringComparison.OrdinalIgnoreCase)) return true;
            } catch { return true; }
        }
        foreach (string name in new[] { "world-backup-sessions", "quickplay-sessions", "options-sessions" }) {
            string folder = host.ProfileAddress(name);
            NoLinks(folder);
            if (!Directory.Exists(folder)) continue;
            foreach (string fileName in Directory.EnumerateFiles(folder, "*.json")) {
                try {
                    JObject entry = ReadJson(fileName);
                    if (string.Equals(Full(entry["gameFolder"]!.ToString()), target, StringComparison.OrdinalIgnoreCase) &&
                        LocalGameDataStore.ProcessMatches(entry["pid"]!.ToObject<int>(), entry["started"]!.ToObject<long>())) return true;
                } catch (Exception ex) {
                    host.Warn(ex, "无法确认存档所属游戏的运行状态");
                    return true;
                }
            }
        }
        return false;
    }

    private void RequireStopped(string gameFolder, Process? exitedProcess = null) {
        if (HasActiveGame(gameFolder, exitedProcess)) throw new IOException("请先关闭使用此存档目录的游戏，再备份或恢复存档。");
        NoLinks(gameFolder);
    }

    private FileStream? SessionLock(string world) {
        string fileName = Safe(world, "session.lock");
        if (!File.Exists(fileName)) return null;
        try { return new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException ex) { throw new IOException("该存档仍被其他程序使用，请关闭游戏后重试。", ex); }
    }

    private Dictionary<string, FileInfo> ScanTree(string folder, CancellationToken token, List<string>? directories = null) {
        NoLinks(folder);
        Dictionary<string, FileInfo> result = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> pending = new();
        pending.Push(folder);
        while (pending.Count > 0) {
            token.ThrowIfCancellationRequested();
            string current = pending.Pop();
            NoLinks(current);
            foreach (FileSystemInfo child in new DirectoryInfo(current).GetFileSystemInfos()) {
                token.ThrowIfCancellationRequested();
                string relative = child.FullName.Substring(Full(folder).Length + 1).Replace('\\', '/');
                Safe(folder, relative);
                if ((child.Attributes & FileAttributes.Directory) != 0) {
                    if (directories is not null) {
                        if (directories.Count >= MaxFiles) throw new IOException("该存档包含过多文件夹，无法安全记录备份。");
                        directories.Add(relative);
                    }
                    pending.Push(child.FullName);
                } else {
                    if (string.Equals(relative, "session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                    if (result.Count >= MaxFiles) throw new IOException("该存档包含过多文件，无法安全记录备份。");
                    result.Add(relative, (FileInfo)child);
                }
            }
        }
        return result;
    }

    private static Tuple<long, string> CopyHash(Stream input, Stream? output, CancellationToken token, long expectedLength = -1) {
        byte[] buffer = new byte[131072];
        long total = 0;
        using SHA256 algorithm = SHA256.Create();
        while (true) {
            token.ThrowIfCancellationRequested();
            int count = input.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            total = checked(total + count);
            if (expectedLength >= 0 && total > expectedLength) throw new IOException("备份文件长度与记录不一致。");
            algorithm.TransformBlock(buffer, 0, count, buffer, 0);
            output?.Write(buffer, 0, count);
        }
        algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        if (expectedLength >= 0 && total != expectedLength) throw new IOException("备份文件长度与记录不一致。");
        return Tuple.Create(total, Convert.ToBase64String(algorithm.Hash!));
    }

    private string CaptureCore(string gameFolder, string identifier, string reason, CancellationToken token) {
        string world = WorldFolder(gameFolder, identifier);
        if (!Directory.Exists(world)) throw new IOException("该存档已移动或删除，请刷新列表。");
        string root = Store(gameFolder), id = Guid.NewGuid().ToString("N");
        string stage = Safe(root, ".pcl-backup-" + id), destination = PointFolder(gameFolder, id);
        try {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stage);
            JObject files = new();
            long total = 0;
            List<string> directories = new();
            using (FileStream? held = SessionLock(world)) {
                Dictionary<string, FileInfo> before = ScanTree(world, token, directories);
                using (FileStream stream = new(Safe(stage, "world.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
                    using (ZipArchive archive = new(stream, ZipArchiveMode.Create, true)) {
                        foreach (KeyValuePair<string, FileInfo> pair in before.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)) {
                            token.ThrowIfCancellationRequested();
                            string fileName = Safe(world, pair.Key);
                            DateTime stamp = pair.Value.LastWriteTimeUtc;
                            long length = pair.Value.Length;
                            ZipArchiveEntry entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                            using (FileStream input = new(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (Stream output = entry.Open()) {
                                Tuple<long, string> actual = CopyHash(input, output, token, length);
                                files.Add(pair.Key, new JObject(new JProperty("length", actual.Item1), new JProperty("sha256", actual.Item2), new JProperty("writeUtc", stamp)));
                                total = checked(total + actual.Item1);
                            }
                            FileInfo after = new(fileName);
                            if (after.Length != length || after.LastWriteTimeUtc != stamp) throw new IOException("存档在备份期间发生变化，请关闭使用它的程序后重试。");
                        }
                    }
                    stream.Flush(true);
                }
                List<string> afterDirectories = new();
                Dictionary<string, FileInfo> afterTree = ScanTree(world, token, afterDirectories);
                if (!new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase).SetEquals(afterDirectories) || afterTree.Count != before.Count ||
                    before.Any(pair => !afterTree.ContainsKey(pair.Key) || afterTree[pair.Key].Length != pair.Value.Length || afterTree[pair.Key].LastWriteTimeUtc != pair.Value.LastWriteTimeUtc))
                    throw new IOException("存档在备份期间发生变化，未保存本次备份。");
            }
            string label = identifier;
            try {
                string? name = host.WorldLabel(world);
                if (!string.IsNullOrWhiteSpace(name)) label = name!;
            } catch {
                // Invalid metadata must not prevent preserving all original world files.
            }
            JObject point = new(new JProperty("schema", 1), new JProperty("id", id), new JProperty("gameFolder", Full(gameFolder)), new JProperty("world", identifier),
                new JProperty("label", label), new JProperty("created", DateTime.UtcNow), new JProperty("reason", reason), new JProperty("manual", ManualReason(reason)),
                new JProperty("bytes", total), new JProperty("archiveBytes", new FileInfo(Safe(stage, "world.zip")).Length), new JProperty("files", files), new JProperty("directories", new JArray(directories)));
            WriteJson(Safe(stage, "manifest.json"), point);
            ReadArchive(stage, point, token);
            token.ThrowIfCancellationRequested();
            RequireStopped(gameFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            NoLinks(destination);
            Directory.Move(stage, destination);
            return id;
        } finally {
            if (Directory.Exists(stage)) DeleteTree(root, stage);
        }
    }

    private void DeleteTree(string root, string folder) {
        string expected = Full(folder);
        if (!expected.StartsWith(Full(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("清理目录超出了存档备份范围。");
        NoLinks(folder);
        // Check every level before deleting; never recurse through a junction.
        foreach (string fileName in Directory.GetFiles(folder)) { NoLinks(fileName); File.Delete(fileName); }
        foreach (string child in Directory.GetDirectories(folder)) DeleteTree(root, child);
        Directory.Delete(folder, false);
    }

    private static bool ManualReason(string? reason) => reason == "手动备份" || string.Equals(reason, "manual", StringComparison.OrdinalIgnoreCase);
    public bool IsManualPoint(JObject point) {
        JToken? value = point["manual"];
        if (value is not null) return value.Type != JTokenType.Boolean || value.ToObject<bool>();
        return ManualReason(point["reason"]?.ToString());
    }

    private void Prune(string gameFolder, string identifier, int count) {
        foreach (JObject point in Points(gameFolder).Where(item => string.Equals(item["world"]!.ToString(), identifier, StringComparison.OrdinalIgnoreCase) && !IsManualPoint(item)).Skip(Retain(count))) {
            try { DeleteTree(Store(gameFolder), PointFolder(gameFolder, point["id"]!.ToString())); }
            catch (Exception ex) { host.Warn(ex, "新备份已保存，但清理旧存档备份失败"); }
        }
    }

    public string CaptureWorld(string gameFolder, string identifier, string reason, int retention, CancellationToken token, Process? exitedProcess = null) {
        lock (Gate) {
            using BusyScope scope = new(this);
            Retain(retention);
            RequireStopped(gameFolder, exitedProcess);
            RecoverCore(gameFolder, token);
            string id = CaptureCore(gameFolder, identifier, reason, token);
            Prune(gameFolder, identifier, retention);
            NotifyChanged();
            return id;
        }
    }

    public List<string> CaptureAll(TInstance? instance, string reason, CancellationToken token, Process? exitedProcess = null) {
        if (instance is null) throw new IOException("请先选择游戏版本。");
        return CaptureFolder(host.GameFolder(instance), reason, host.Retention(instance), token, exitedProcess);
    }

    public List<string> CaptureFolder(string gameFolder, string reason, int retention, CancellationToken token, Process? exitedProcess = null) {
        lock (Gate) {
            using BusyScope scope = new(this);
            Retain(retention);
            RequireStopped(gameFolder, exitedProcess);
            RecoverCore(gameFolder, token);
            string root = Safe(gameFolder, "saves");
            List<string> result = new();
            if (!Directory.Exists(root)) return result;
            foreach (string folder in Directory.EnumerateDirectories(root).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) {
                token.ThrowIfCancellationRequested();
                string identifier = Path.GetFileName(folder);
                if (identifier.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase)) continue;
                NoLinks(folder);
                if (!File.Exists(Safe(folder, "level.dat"))) continue;
                RequireStopped(gameFolder, exitedProcess);
                result.Add(CaptureCore(gameFolder, identifier, reason, token));
                Prune(gameFolder, identifier, retention);
            }
            NotifyChanged();
            return result;
        }
    }

    public void OpenFolder(string gameFolder) {
        string folder = Store(gameFolder);
        if (Directory.Exists(folder)) host.OpenFolder(folder);
        else host.Hint("此游戏目录还没有存档备份。", false);
    }
    public void NotifyChanged() => host.NotifyChanged();

    public void Track(TInstance? instance, Process? game) {
        if (instance is null || game is null) return;
        string name = game.Id.ToString() + "-" + game.StartTime.ToUniversalTime().Ticks.ToString() + ".json";
        WriteJson(host.ProfileAddress("world-backup-sessions/" + name), new JObject(new JProperty("gameFolder", Full(host.GameFolder(instance))),
            new JProperty("versionPath", host.VersionPath(instance)), new JProperty("pid", game.Id), new JProperty("started", game.StartTime.ToUniversalTime().Ticks)));
    }

    public void OnProcessStopped(TInstance? instance, Process? game, bool normalExit) {
        if (instance is null || game is null || Alive(game)) return;
        try {
            if (normalExit && game.ExitCode == 0 && host.AutoEnabled(instance)) {
                string folder = host.ProfileAddress("world-backup-sessions"), gameFolder = host.GameFolder(instance);
                NoLinks(folder);
                // PID reuse cannot select an arbitrary old session's game folder.
                string sessionFile = Safe(folder, game.Id.ToString() + "-" + game.StartTime.ToUniversalTime().Ticks.ToString() + ".json");
                if (File.Exists(sessionFile)) gameFolder = ReadJson(sessionFile)["gameFolder"]!.ToString();
                CaptureFolder(gameFolder, "正常退出后", host.Retention(instance), CancellationToken.None, game);
            }
        } catch (Exception ex) {
            host.Warn(ex, "游戏退出后的存档自动备份失败");
            host.Dispatch(() => host.Hint("存档自动备份失败：" + ex.Message, true));
        } finally {
            string folder = host.ProfileAddress("world-backup-sessions");
            try {
                NoLinks(folder);
                if (Directory.Exists(folder)) {
                    foreach (string fileName in Directory.GetFiles(folder, game.Id.ToString() + "-*.json")) {
                        JObject entry = ReadJson(fileName);
                        if (!LocalGameDataStore.ProcessMatches(entry["pid"]!.ToObject<int>(), entry["started"]!.ToObject<long>())) File.Delete(fileName);
                    }
                }
            } catch (Exception ex) { host.Warn(ex, "清理已退出的游戏备份会话失败"); }
        }
    }

    public void OnNormalExit(TInstance? instance, Process? game) => OnProcessStopped(instance, game, true);
    public List<string> BeforeMinecraftChange(TInstance? instance, string oldMinecraft, string newMinecraft, CancellationToken token) {
        if (instance is null || string.IsNullOrWhiteSpace(oldMinecraft) || string.Equals(oldMinecraft, newMinecraft, StringComparison.OrdinalIgnoreCase) || !host.AutoEnabled(instance)) return new List<string>();
        return CaptureAll(instance, "更新 Minecraft 前（" + oldMinecraft + " → " + newMinecraft + "）", token);
    }
}
