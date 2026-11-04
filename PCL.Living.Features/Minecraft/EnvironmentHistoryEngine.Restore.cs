using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

public sealed partial class EnvironmentHistoryEngine<TInstance> where TInstance : class {
    public string Restore(string versionFolder, string gameRoot, string mcRoot, string id, CancellationToken token,
        Action<int>? beforeApply = null, Func<string>? captureBefore = null) {
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                RestoringCount = checked(RestoringCount + 1);
                try {
                    JObject? point = Points(versionFolder).FirstOrDefault(item => SameText(item["id"]!.ToString(), id));
                    if (point is null) throw new IOException("找不到该环境还原点。");
                    if (!host.SamePath(point["gameRoot"]!.ToString(), gameRoot)) throw new IOException("此还原点的游戏目录与当前版本隔离设置不同，请先恢复原隔离设置。");
                    int coverage = CoverageVersion(point);
                    string storeRoot = Store(versionFolder);
                    Dictionary<string, string> current = FilesWithCoverage(versionFolder, gameRoot, coverage), sources = new(StringComparer.OrdinalIgnoreCase);
                    foreach (JProperty entry in ((JObject)point["files"]!).Properties()) {
                        token.ThrowIfCancellationRequested();
                        string key = entry.Name, hash = entry.Value.ToString(), blob = ObjectPath(storeRoot, hash);
                        if (!SameText(host.Digest(blob), hash)) throw new IOException("还原点文件缺失或损坏，当前环境未修改。");
                        if (key.StartsWith("m/")) {
                            string relative = host.RelativePath(key.Substring(2));
                            if (!relative.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith("versions/", StringComparison.OrdinalIgnoreCase)) throw new IOException("无效的依赖文件路径。");
                            string destination = host.Target(mcRoot, relative);
                            if (File.Exists(destination) && !SameText(host.Digest(destination), hash)) throw new IOException("共享支持库已被修改，已停止恢复：" + relative);
                            sources[key] = blob;
                        } else if (key.StartsWith("g/") && IsGameFileCovered(key.Substring(2), coverage)) {
                            host.Target(gameRoot, key.Substring(2));
                            sources[key] = blob;
                        } else if (key.StartsWith("v/") && ((!key.Substring(2).Contains("/") && new[] { ".json", ".jar" }.Contains(Path.GetExtension(key).ToLowerInvariant())) || new[] { "v/PCL/Setup.ini", "v/PCL/config.json", "v/PCL/ModpackOrigin.json" }.Contains(key))) {
                            host.Target(versionFolder, key.Substring(2));
                            sources[key] = blob;
                        } else {
                            throw new IOException("还原点包含不支持的文件范围。");
                        }
                    }
                    List<Tuple<string, string?, string?>> operations = new();
                    foreach (string key in current.Keys.Union(sources.Keys.Where(value => !value.StartsWith("m/")), StringComparer.OrdinalIgnoreCase)) {
                        string destination = host.Target(key.StartsWith("v/") ? versionFolder : gameRoot, key.Substring(2));
                        sources.TryGetValue(key, out string? payload);
                        string? before = host.Digest(destination);
                        if (payload is not null && SameText(before, host.Digest(payload))) continue;
                        operations.Add(Tuple.Create(destination, payload, before));
                    }
                    // 完整环境先留档；缺失共享依赖与原事务的处理顺序保持不变。
                    string undoId = captureBefore is null ? Capture(versionFolder, gameRoot, "恢复前自动保存", token) : captureBefore();
                    foreach (KeyValuePair<string, string> pair in sources.Where(item => item.Key.StartsWith("m/"))) {
                        string destination = host.Target(mcRoot, pair.Key.Substring(2));
                        if (!File.Exists(destination)) { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(pair.Value, destination, false); }
                    }
                    host.ApplyTransaction(gameRoot, versionFolder, operations, token, beforeApply);
                    return undoId;
                } finally { RestoringCount = checked(RestoringCount - 1); }
            }
        }
    }
}
