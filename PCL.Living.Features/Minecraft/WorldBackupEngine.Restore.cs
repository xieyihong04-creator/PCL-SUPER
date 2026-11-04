using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

public sealed partial class WorldBackupEngine<TInstance> where TInstance : class {
    // Public only so LF's existing private reflection/test entry points remain forwarding adapters.
    public JObject Metadata(string gameFolder, string id) {
        string folder = PointFolder(gameFolder, id);
        JObject point = ReadJson(Safe(folder, "manifest.json"));
        if ((int)point["schema"]! != 1 || point["id"]?.ToString() != id || !string.Equals(Full(point["gameFolder"]!.ToString()), Full(gameFolder), StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份记录与当前游戏目录不匹配。");
        WorldName(point["world"]!.ToString());
        _ = point["created"]!.ToObject<DateTime>().ToUniversalTime();
        JObject? files = point["files"] as JObject;
        if (files is null || files.Count > MaxFiles) throw new IOException("备份文件清单无效。");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (JProperty pair in files.Properties()) {
            Safe(folder, pair.Name);
            if (pair.Name.Contains("\\") || !names.Add(pair.Name) || string.Equals(pair.Name, "session.lock", StringComparison.OrdinalIgnoreCase)) throw new IOException("备份含有重复或无效文件。");
            long length = pair.Value["length"]!.ToObject<long>();
            if (length < 0 || total > long.MaxValue - length || Convert.FromBase64String(pair.Value["sha256"]!.ToString()).Length != 32) throw new IOException("备份文件校验记录无效。");
            total += length;
        }
        JArray? directories = point["directories"] as JArray;
        if (directories is null || directories.Count > MaxFiles) throw new IOException("备份目录清单无效。");
        foreach (JToken directory in directories) {
            string name = directory.ToString();
            Safe(folder, name);
            if (name.Contains("\\") || !names.Add(name)) throw new IOException("备份含有重复的目录或文件。");
        }
        if (point["bytes"]!.ToObject<long>() != total) throw new IOException("备份总大小与文件清单不一致。");
        NoLinks(Safe(folder, "world.zip"));
        if (!File.Exists(Safe(folder, "world.zip"))) throw new IOException("备份数据已丢失。");
        point["healthy"] = false;
        point["description"] = point["world"]!.ToString() + " · " + point["reason"]!.ToString() + " · " + host.FormatSize(total);
        return point;
    }

    public List<JObject> Points(string gameFolder) {
        lock (Gate) {
            string folder = Safe(Store(gameFolder), "points");
            List<JObject> result = new();
            if (!Directory.Exists(folder)) return result;
            foreach (string child in Directory.EnumerateDirectories(folder)) {
                string id = Path.GetFileName(child);
                if (!Regex.IsMatch(id, @"\A[a-f0-9]{32}\z")) continue;
                try { result.Add(Metadata(gameFolder, id)); }
                catch (Exception ex) { host.Warn(ex, "忽略不可读取的存档备份记录：" + id); }
            }
            return result.OrderByDescending(item => item["created"]!.ToObject<DateTime>()).ToList();
        }
    }

    public void ReadArchive(string folder, JObject point, CancellationToken token, string? destination = null) {
        JObject files = (JObject)point["files"]!;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (destination is not null) {
            foreach (JToken child in (JArray)point["directories"]!) {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Safe(destination, child.ToString()));
            }
        }
        using FileStream stream = new(Safe(folder, "world.zip"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count != files.Count) throw new IOException("备份压缩包与文件清单不一致。");
        foreach (ZipArchiveEntry entry in archive.Entries) {
            token.ThrowIfCancellationRequested();
            Safe(folder, entry.FullName);
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (entry.FullName.Contains("\\") || !seen.Add(entry.FullName) || unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new IOException("备份包含链接或重复文件。");
            JToken? expected = files[entry.FullName];
            if (expected is null || entry.Length != expected["length"]!.ToObject<long>()) throw new IOException("备份包含未记录的文件或大小异常。");
            using Stream input = entry.Open();
            Tuple<long, string> actual;
            if (destination is null) actual = CopyHash(input, null, token, entry.Length);
            else {
                string fileName = Safe(destination, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
                using (FileStream output = new(fileName, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    actual = CopyHash(input, output, token, entry.Length);
                    output.Flush(true);
                }
                File.SetLastWriteTimeUtc(fileName, expected["writeUtc"]!.ToObject<DateTime>().ToUniversalTime());
            }
            if (actual.Item2 != expected["sha256"]!.ToString()) throw new IOException("备份内容校验失败，尚未替换当前世界。");
        }
    }

    private void MatchTree(string world, JObject point, CancellationToken token) {
        JObject files = (JObject)point["files"]!;
        List<string> directories = new();
        Dictionary<string, FileInfo> tree = ScanTree(world, token, directories);
        if (tree.Count != files.Count || !new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase).SetEquals(((JArray)point["directories"]!).Select(item => item.ToString())))
            throw new IOException("当前世界已被其他程序修改，停止恢复以保留当前进度。");
        foreach (KeyValuePair<string, FileInfo> pair in tree) {
            JToken? expected = files[pair.Key];
            if (expected is null) throw new IOException("当前世界已变化，停止恢复。");
            using FileStream input = new(Safe(world, pair.Key), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (CopyHash(input, null, token, expected["length"]!.ToObject<long>()).Item2 != expected["sha256"]!.ToString())
                throw new IOException("当前世界已被其他程序修改，停止恢复以保留当前进度。");
        }
    }

    private string RestorePath(string gameFolder, string transaction, string suffix) {
        PointId(transaction);
        return Safe(gameFolder, "saves/.pcl-world-" + transaction + suffix);
    }
    private string JournalPath(string gameFolder, string transaction) {
        PointId(transaction);
        return Safe(Store(gameFolder), "transactions/" + transaction + ".json");
    }

    // Only roll back data still attributable to this transaction; uncertainty preserves every folder.
    private void RecoverCore(string gameFolder, CancellationToken token) {
        string root = Safe(Store(gameFolder), "transactions");
        if (!Directory.Exists(root)) return;
        foreach (string fileName in Directory.GetFiles(root, "*.json")) {
            token.ThrowIfCancellationRequested();
            string transaction = Path.GetFileNameWithoutExtension(fileName);
            PointId(transaction);
            JObject journal = ReadJson(fileName);
            if (journal["ownerPid"] is not null && journal["ownerStarted"] is not null && journal["ownerPid"]!.ToObject<int>() != Process.GetCurrentProcess().Id &&
                LocalGameDataStore.ProcessMatches(journal["ownerPid"]!.ToObject<int>(), journal["ownerStarted"]!.ToObject<long>()))
                throw new IOException("另一个启动器仍在恢复该存档，请等待操作完成后再启动。");
            if (!string.Equals(Full(journal["gameFolder"]!.ToString()), Full(gameFolder), StringComparison.OrdinalIgnoreCase)) throw new IOException("未完成的恢复记录与当前目录不匹配。");
            string world = WorldFolder(gameFolder, journal["world"]!.ToString()), stage = RestorePath(gameFolder, transaction, ".stage"), previous = RestorePath(gameFolder, transaction, ".before");
            using (FileStream? held = Directory.Exists(world) ? SessionLock(world) : null) { RequireStopped(gameFolder); }
            using (FileStream? held = Directory.Exists(previous) ? SessionLock(previous) : null) { RequireStopped(gameFolder); }
            if (journal["complete"]!.ToObject<bool>()) {
                if (!Directory.Exists(world)) throw new IOException("恢复事务缺少目标世界，已保留恢复前目录：" + previous);
                if (Directory.Exists(previous)) DeleteTree(Safe(gameFolder, "saves"), previous);
            } else if (Directory.Exists(previous)) {
                if (Directory.Exists(world)) {
                    MatchTree(world, Metadata(gameFolder, journal["point"]!.ToString()), token);
                    if (Directory.Exists(stage)) throw new IOException("恢复临时目录状态不一致，已保留所有存档。");
                    Directory.Move(world, stage);
                }
                Directory.Move(previous, world);
            } else if (!journal["existed"]!.ToObject<bool>() && Directory.Exists(world)) {
                MatchTree(world, Metadata(gameFolder, journal["point"]!.ToString()), token);
                if (Directory.Exists(stage)) throw new IOException("恢复临时目录状态不一致，已保留所有存档。");
                Directory.Move(world, stage);
            }
            if (Directory.Exists(stage)) DeleteTree(Safe(gameFolder, "saves"), stage);
            NoLinks(fileName);
            File.Delete(fileName);
        }
    }

    public void RecoverPending(string gameFolder, CancellationToken token) {
        lock (Gate) {
            using BusyScope scope = new(this);
            RequireStopped(gameFolder);
            RecoverCore(gameFolder, token);
        }
    }

    public void RecoverBeforeLaunch(string gameFolder, CancellationToken token) {
        lock (Gate) {
            string transactions = Safe(Store(gameFolder), "transactions");
            if (!Directory.Exists(transactions) || !Directory.EnumerateFiles(transactions, "*.json").Any()) return;
            RecoverPending(gameFolder, token);
        }
    }

    public string? Restore(string gameFolder, string id, int retention, CancellationToken token, Action<int>? beforeCommit = null) {
        lock (Gate) {
            using BusyScope scope = new(this);
            Retain(retention);
            RequireStopped(gameFolder);
            RecoverCore(gameFolder, token);
            JObject point = Metadata(gameFolder, id);
            string identifier = point["world"]!.ToString(), world = WorldFolder(gameFolder, identifier);
            if (!Directory.Exists(gameFolder)) throw new IOException("游戏目录已不存在，请先恢复或重新选择目录。");
            string transaction = Guid.NewGuid().ToString("N"), stage = RestorePath(gameFolder, transaction, ".stage"), previous = RestorePath(gameFolder, transaction, ".before");
            string journalFile = JournalPath(gameFolder, transaction);
            string? undo = null;
            bool moved = false, installed = false, completed = false, journalWritten = false;
            try {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(stage);
                ReadArchive(PointFolder(gameFolder, id), point, token, stage);
                bool existed = Directory.Exists(world);
                if (existed) undo = CaptureCore(gameFolder, identifier, "恢复前自动保存", token);
                JObject journal = new(new JProperty("gameFolder", Full(gameFolder)), new JProperty("world", identifier), new JProperty("point", id), new JProperty("undo", undo), new JProperty("existed", existed), new JProperty("complete", false));
                using (Process owner = Process.GetCurrentProcess()) {
                    journal["ownerPid"] = owner.Id;
                    journal["ownerStarted"] = owner.StartTime.ToUniversalTime().Ticks;
                }
                WriteJson(journalFile, journal);
                journalWritten = true;
                beforeCommit?.Invoke(0);
                using (FileStream? held = existed ? SessionLock(world) : null) {
                    RequireStopped(gameFolder);
                    if (existed) MatchTree(world, Metadata(gameFolder, undo!), token);
                }
                // Windows cannot move a directory containing a handle held by this process.
                // Commit immediately after validation releases the lock; IsBusy and the owner lock block launcher starts.
                RequireStopped(gameFolder);
                token.ThrowIfCancellationRequested();
                if (existed) { Directory.Move(world, previous); moved = existed; }
                beforeCommit?.Invoke(1);
                token.ThrowIfCancellationRequested();
                Directory.Move(stage, world);
                installed = true;
                beforeCommit?.Invoke(2);
                token.ThrowIfCancellationRequested();
                journal["complete"] = true;
                WriteJson(journalFile, journal);
                completed = true;
            } catch (Exception ex) {
                try {
                    if (installed) { NoLinks(world); Directory.Move(world, stage); }
                    if (moved) { NoLinks(previous); Directory.Move(previous, world); }
                    if (journalWritten && File.Exists(journalFile)) File.Delete(journalFile);
                    journalWritten = false;
                } catch (Exception rollbackError) {
                    throw new IOException("恢复失败且无法自动回滚。原世界和临时数据均已保留，请勿删除 saves 下的 .pcl-world 目录：" + previous, new AggregateException(ex, rollbackError));
                }
                throw;
            } finally {
                if (!journalWritten && Directory.Exists(stage)) DeleteTree(Safe(gameFolder, "saves"), stage);
            }
            if (completed) {
                // Cleanup failure retains the committed journal for later retry, without reporting a failed restore.
                try { RecoverCore(gameFolder, CancellationToken.None); }
                catch (Exception ex) { host.Warn(ex, "存档已恢复，暂未清理恢复事务目录"); }
                if (undo is not null) Prune(gameFolder, identifier, retention);
                NotifyChanged();
            }
            return undo;
        }
    }

    public string? DeletePoints(string gameFolder, IEnumerable<string> ids) {
        lock (Gate) {
            using BusyScope scope = new(this);
            List<string> selected = ids.Distinct(StringComparer.Ordinal).ToList();
            string root = Store(gameFolder);
            if (selected.Count == 0) return null;
            foreach (string id in selected) Metadata(gameFolder, id);
            string deleted = Safe(root, "deleted/" + Guid.NewGuid().ToString("N"));
            List<string> moved = new();
            try {
                Directory.CreateDirectory(deleted);
                foreach (string id in selected) {
                    Directory.Move(PointFolder(gameFolder, id), Safe(deleted, id));
                    moved.Add(id);
                }
            } catch {
                foreach (string id in moved.AsEnumerable().Reverse()) Directory.Move(Safe(deleted, id), PointFolder(gameFolder, id));
                throw;
            }
            NotifyChanged();
            return deleted;
        }
    }
}
