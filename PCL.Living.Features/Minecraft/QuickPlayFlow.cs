using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>原实例属性和隔离设置的惰性适配，不预先加载或缓存版本。</summary>
public interface IQuickPlayInstance {
    string VersionPath { get; }
    string GameFolder { get; }
    bool VersionValid { get; }
    int VanillaMajor { get; }
    bool IsSnapshot { get; }
    DateTime ReleaseTime { get; }
    bool HasSavedIsolation { get; }
    bool IsolationEnabled { get; }
}

/// <summary>原 Quick Play 启动会话及隔离存档发现；界面启动和卡片交互仍由调用者管理。</summary>
public sealed class QuickPlayFlow {
    private readonly QuickPlayHistory _history;
    private readonly LocalGameDataStore _data;
    private readonly object _gate;
    private readonly Func<bool> _enabled;
    private readonly Action<string, string> _ensureWithin;
    private readonly Action<Exception, string> _warn;
    private readonly Func<QuickPlaySessionData> _sessionFactory;
    private readonly Func<RecentWorldData> _worldFactory;

    public QuickPlayFlow(QuickPlayHistory history, LocalGameDataStore data, object gate, Func<bool> enabled,
        Action<string, string> ensureWithin, Action<Exception, string> warn,
        Func<QuickPlaySessionData> sessionFactory, Func<RecentWorldData> worldFactory) {
        _history = history;
        _data = data;
        _gate = gate;
        _enabled = enabled;
        _ensureWithin = ensureWithin;
        _warn = warn;
        _sessionFactory = sessionFactory;
        _worldFactory = worldFactory;
    }

    public bool Supported(IQuickPlayInstance? instance) => instance is not null && instance.VersionValid && instance.VanillaMajor >= 20 &&
        (!instance.IsSnapshot || instance.ReleaseTime.ToUniversalTime() >= new DateTime(2023, 4, 5, 0, 0, 0, DateTimeKind.Utc));

    public QuickPlaySessionData? Prepare(IQuickPlayInstance? instance, string? identifier, List<string> arguments, bool saveBatch) {
        if (identifier is not null) {
            if (!Supported(instance)) throw new IOException("该 Minecraft 版本不支持 Quick Play。");
            QuickPlayWorldRules.WorldPath(instance!.GameFolder, identifier, _ensureWithin);
            QuickPlayWorldRules.RemoveArguments(arguments, new[] { "--quickPlaySingleplayer", "--quickPlayMultiplayer", "--quickPlayRealms", "--server", "--port" });
            arguments.Add("--quickPlaySingleplayer");
            arguments.Add(identifier);
        }
        if (!Supported(instance) || !_enabled() || saveBatch) return null;
        // 尊重用户手动指定的记录路径，不抢占其他启动工具的日志。
        if (arguments.Contains("--quickPlayPath")) return null;
        try {
            QuickPlaySessionData session = _sessionFactory();
            session.VersionPath = instance!.VersionPath;
            session.GameFolder = instance.GameFolder;
            session.LogFile = _data.Address("quickplay-logs/" + session.Id + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(session.LogFile)!);
            arguments.Add("--quickPlayPath");
            arguments.Add(session.LogFile);
            return session;
        } catch (Exception error) {
            _warn(error, "无法记录最近存档，不影响本次启动");
            return null;
        }
    }

    public void Track(QuickPlaySessionData? session, Process game) {
        if (session is null) return;
        lock (_gate) {
            _data.WriteJson("quickplay-sessions/" + session.Id + ".json", new JObject(
                new JProperty("versionPath", session.VersionPath), new JProperty("gameFolder", session.GameFolder), new JProperty("logFile", session.LogFile),
                new JProperty("pid", game.Id), new JProperty("started", game.StartTime.ToUniversalTime().Ticks)));
        }
    }

    public void Finish(QuickPlaySessionData? session) {
        if (session is null) return;
        lock (_gate) {
            try {
                _history.ReadSessionLog(session);
            } finally {
                string journal = _data.Address("quickplay-sessions/" + session.Id + ".json");
                if (File.Exists(journal)) File.Delete(journal);
                if (File.Exists(session.LogFile)) {
                    _ensureWithin(_data.Address("quickplay-logs"), session.LogFile!);
                    File.Delete(session.LogFile!);
                }
            }
        }
    }

    public void RecoverExitedSessions() {
        string folder = _data.Address("quickplay-sessions");
        if (!Directory.Exists(folder)) return;
        lock (_gate) {
            foreach (string fileName in Directory.EnumerateFiles(folder, "*.json")) {
                try {
                    JObject json = _data.ReadJson("quickplay-sessions/" + Path.GetFileName(fileName));
                    QuickPlaySessionData session = _sessionFactory();
                    session.Id = Path.GetFileNameWithoutExtension(fileName);
                    session.VersionPath = json["versionPath"]!.ToString();
                    session.GameFolder = json["gameFolder"]!.ToString();
                    session.LogFile = json["logFile"]!.ToString();
                    if (LocalGameDataStore.ProcessMatches(json["pid"]!.ToObject<int>(), json["started"]!.ToObject<long>())) {
                        _history.ReadSessionLog(session);
                    } else {
                        Finish(session);
                    }
                } catch (Exception error) {
                    _warn(error, "恢复最近存档记录失败");
                }
            }
        }
    }

    public void DiscoverExistingWorlds(IQuickPlayInstance? instance) {
        if (!_enabled() || !Supported(instance)) return;
        // 只有明确启用隔离的版本目录能关联已有存档。
        if (!instance!.HasSavedIsolation || !instance.IsolationEnabled) return;
        string saves = Path.Combine(instance.VersionPath, "saves");
        if (!Directory.Exists(saves)) return;
        foreach (string folder in Directory.EnumerateDirectories(saves)) {
            try {
                _ensureWithin(saves, folder);
                WorldMetadataInfo? info = WorldMetadataFiles.Read(folder, false, _ensureWithin);
                if (info is null) continue;
                RecentWorldData world = _worldFactory();
                world.VersionPath = instance.VersionPath;
                world.GameFolder = instance.VersionPath;
                world.Identifier = Path.GetFileName(folder);
                world.Name = string.IsNullOrWhiteSpace(info.Name) ? Path.GetFileName(folder) : info.Name;
                world.PlayedUtc = info.LastPlayedUtc;
                _history.Remember(world);
            } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException) {
                // 保留原异常集合；InvalidDataException 不属于 IOException。
            }
        }
    }

    public bool HasRunningSessions() {
        string folder = _data.Address("quickplay-sessions");
        if (!Directory.Exists(folder)) return false;
        lock (_gate) {
            foreach (string fileName in Directory.EnumerateFiles(folder, "*.json")) {
                try {
                    JObject json = _data.ReadJson("quickplay-sessions/" + Path.GetFileName(fileName));
                    if (LocalGameDataStore.ProcessMatches(json["pid"]!.ToObject<int>(), json["started"]!.ToObject<long>())) return true;
                } catch (Exception error) {
                    _warn(error, "读取游戏运行状态失败");
                }
            }
        }
        return false;
    }
}
