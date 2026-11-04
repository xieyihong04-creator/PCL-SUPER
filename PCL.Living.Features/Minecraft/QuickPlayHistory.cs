using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

public class QuickPlaySessionData {
    public string Id = Guid.NewGuid().ToString("N");
    public string? VersionPath;
    public string? GameFolder;
    public string? LogFile;
}

public class RecentWorldData {
    public string? VersionPath;
    public string? GameFolder;
    public string? Identifier;
    public string? Name;
    public DateTime PlayedUtc;
}

/// <summary>原 LF 最近存档的历史、筛选与 Quick Play 日志读取；不接管进程和最终清理。</summary>
public sealed class QuickPlayHistory {
    private const string HistoryName = "recent-worlds.json";
    private readonly LocalGameDataStore _data;
    private readonly object _gate;
    private readonly Func<bool> _enabled;
    private readonly Action<string, string> _ensureWithin;
    private readonly Func<string, string, bool> _samePath;
    private readonly Func<string, bool> _allowsVersion;
    private readonly Action<Exception, string> _warn;
    private readonly Func<RecentWorldData> _worldFactory;

    public QuickPlayHistory(LocalGameDataStore data, object gate, Func<bool> enabled,
        Action<string, string> ensureWithin, Func<string, string, bool> samePath,
        Func<string, bool> allowsVersion, Action<Exception, string> warn, Func<RecentWorldData> worldFactory) {
        _data = data;
        _gate = gate;
        _enabled = enabled;
        _ensureWithin = ensureWithin;
        _samePath = samePath;
        _allowsVersion = allowsVersion;
        _warn = warn;
        _worldFactory = worldFactory;
    }

    public List<RecentWorldData> ReadEntries() {
        JArray? values = _data.ReadJson(HistoryName)["worlds"] as JArray;
        List<RecentWorldData> result = new();
        foreach (JObject item in (values ?? new JArray()).OfType<JObject>()) {
            try {
                RecentWorldData world = _worldFactory();
                world.VersionPath = item["versionPath"]!.ToString();
                world.GameFolder = item["gameFolder"]!.ToString();
                world.Identifier = item["identifier"]!.ToString();
                world.Name = item["name"]!.ToString();
                world.PlayedUtc = item["playedUtc"]!.ToObject<DateTime>().ToUniversalTime();
                result.Add(world);
            } catch {
                // 单个旧记录无效时仍保留其他记录。
            }
        }
        return result;
    }

    public void Remember(RecentWorldData world) {
        lock (_gate) {
            if (ReadEntries().Any(item => _samePath(item.VersionPath!, world.VersionPath!) && SameString(item.Identifier, world.Identifier) &&
                (item.PlayedUtc > world.PlayedUtc || (item.PlayedUtc == world.PlayedUtc && SameString(item.Name, world.Name))))) return;
            List<RecentWorldData> existing = ReadEntries().Where(item => !(_samePath(item.VersionPath!, world.VersionPath!) && SameString(item.Identifier, world.Identifier))).ToList();
            existing.Add(world);
            JArray values = new(existing.OrderByDescending(item => item.PlayedUtc).Take(40).Select(item => new JObject(
                new JProperty("versionPath", item.VersionPath), new JProperty("gameFolder", item.GameFolder),
                new JProperty("identifier", item.Identifier), new JProperty("name", item.Name), new JProperty("playedUtc", item.PlayedUtc))));
            _data.WriteJson(HistoryName, new JObject(new JProperty("version", 1), new JProperty("worlds", values)));
        }
    }

    public List<RecentWorldData> Recent() {
        List<RecentWorldData> result = new();
        if (!_enabled()) return result;
        lock (_gate) {
            foreach (RecentWorldData world in ReadEntries().OrderByDescending(item => item.PlayedUtc)) {
                try {
                    if (!Directory.Exists(world.VersionPath) || !_allowsVersion(world.VersionPath!)) continue;
                    QuickPlayWorldRules.WorldPath(world.GameFolder!, world.Identifier, _ensureWithin);
                    result.Add(world);
                } catch {
                    // 删除或移动整合包后不报错，不创建缺失目录。
                }
                if (result.Count == 5) break;
            }
        }
        return result;
    }

    public void ReadSessionLog(QuickPlaySessionData session) {
        if (!_enabled() || !File.Exists(session.LogFile)) return;
        _ensureWithin(_data.Address("quickplay-logs"), session.LogFile!);
        if (new FileInfo(session.LogFile!).Length > 1024 * 1024) throw new IOException("Quick Play 记录过大。");
        JToken json = JToken.Parse(File.ReadAllText(session.LogFile!));
        JContainer? container = json as JContainer;
        if (container is null) return;
        List<JObject> objects = container.Descendants().OfType<JObject>().ToList();
        if (container is JObject root) objects.Insert(0, root);
        foreach (JObject entry in objects.Where(item => string.Equals(item["type"]?.ToString(), "singleplayer", StringComparison.OrdinalIgnoreCase))) {
            string? identifier = entry["id"]?.ToString() ?? entry["identifier"]?.ToString();
            try {
                QuickPlayWorldRules.WorldPath(session.GameFolder!, identifier, _ensureWithin);
                DateTime played = File.GetLastWriteTimeUtc(session.LogFile!);
                if (DateTimeOffset.TryParse(entry["lastPlayedTime"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)) played = parsed.UtcDateTime;
                RecentWorldData world = _worldFactory();
                world.VersionPath = session.VersionPath;
                world.GameFolder = session.GameFolder;
                world.Identifier = identifier;
                world.Name = entry["name"]?.ToString() ?? identifier;
                world.PlayedUtc = played;
                Remember(world);
            } catch (IOException error) {
                _warn(error, "忽略不可用的最近存档记录");
            }
        }
    }

    private static bool SameString(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);
}
