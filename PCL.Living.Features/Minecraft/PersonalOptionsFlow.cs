using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>按原访问时机获取实例和设置，不提前拍摄版本或开关快照。</summary>
public interface IPersonalOptionsInstance {
    string VersionPath { get; }
    string GameFolder { get; }
    bool SyncEnabled { get; }
    bool VersionValid { get; }
    int VanillaMajor { get; }
}

/// <summary>原个人选项会话字段；允许旧程序集的门面继承并保持对象身份。</summary>
public class PersonalOptionsSessionData {
    public string Id = Guid.NewGuid().ToString("N");
    public string? VersionPath;
    public string? GameFolder;
    public JObject? Baseline;
    public HashSet<string> Keys = new(StringComparer.Ordinal);
    public bool Tracked;
}

/// <summary>
/// LF 个人设置导入、启动合并、退出捕获及遗留会话恢复。
/// 复用调用者的 Monitor 锁与资料存储，不管理 WPF、全局设置或进程生命周期。
/// </summary>
public sealed class PersonalOptionsFlow {
    private const string ProfileName = "personal-options.json";
    private readonly LocalGameDataStore _data;
    private readonly object _gate;
    private readonly Func<bool> _hasRunning;
    private readonly Func<string, IPersonalOptionsInstance> _instanceFactory;
    private readonly Func<string, bool> _selected;
    private readonly Action<string> _invalidateCache;
    private readonly Func<string, string, bool> _samePath;
    private readonly Action<Exception, string> _warn;
    private readonly Func<PersonalOptionsSessionData> _sessionFactory;

    public PersonalOptionsFlow(LocalGameDataStore data, object gate, Func<bool> hasRunning,
        Func<string, IPersonalOptionsInstance> instanceFactory, Func<string, bool> selected,
        Action<string> invalidateCache, Func<string, string, bool> samePath,
        Action<Exception, string> warn, Func<PersonalOptionsSessionData> sessionFactory) {
        _data = data;
        _gate = gate;
        _hasRunning = hasRunning;
        _instanceFactory = instanceFactory;
        _selected = selected;
        _invalidateCache = invalidateCache;
        _samePath = samePath;
        _warn = warn;
        _sessionFactory = sessionFactory;
    }

    public bool Enabled(IPersonalOptionsInstance? instance) => instance is not null && instance.SyncEnabled;
    public bool Modern(IPersonalOptionsInstance instance) => instance.VersionValid && instance.VanillaMajor >= 13;

    public bool HasProfile() {
        lock (_gate) {
            JObject? values = _data.ReadJson(ProfileName)["values"] as JObject;
            return values is not null && values.Count > 0;
        }
    }

    public void ImportFrom(IPersonalOptionsInstance? instance) {
        if (instance is null) throw new IOException("请先选择一个已经运行过的游戏版本。");
        if (_hasRunning()) throw new IOException("请先关闭游戏，再导入个人设置。");
        byte[]? bytes = PersonalOptionsFiles.Read(Path.Combine(instance.GameFolder, "options.txt"));
        if (bytes is null) throw new IOException("该版本尚未生成 options.txt，请先启动一次游戏并保存设置。");
        JObject values = new();
        foreach (KeyValuePair<string, string> pair in PersonalOptionsRules.Parse(PersonalOptionsRules.Decode(bytes)).Where(item => _selected(item.Key))) {
            string? value = PersonalOptionsRules.CanonicalValue(pair.Key, pair.Value);
            if (value is not null) values[pair.Key] = value;
        }
        if (values.Count == 0) throw new IOException("该版本没有可导入的个人设置。");
        lock (_gate) {
            _data.WriteJson(ProfileName, new JObject(new JProperty("version", 1), new JProperty("values", values)));
        }
    }

    public PersonalOptionsSessionData? Prepare(IPersonalOptionsInstance? instance) {
        if (!Enabled(instance) || _hasRunning()) return null;
        RecoverExitedSessions();
        string folder = instance!.GameFolder;
        string fileName = Path.Combine(folder, "options.txt");
        if (HasActiveSession(folder)) return null;
        byte[]? bytes = PersonalOptionsFiles.Read(fileName);
        // 首次文件仍由游戏／默认配置 Mod 创建。
        if (bytes is null) return null;
        lock (_gate) {
            JObject? values = _data.ReadJson(ProfileName)["values"] as JObject;
            if (values is null || values.Count == 0) return null;
            HashSet<string> keys = new(values.Properties().Where(pair => _selected(pair.Name)).Select(pair => pair.Name), StringComparer.Ordinal);
            if (!instance.VersionValid) keys.RemoveWhere(key => key.StartsWith("key_", StringComparison.Ordinal));
            string text = PersonalOptionsRules.Decode(bytes);
            string merged = PersonalOptionsRules.MergeText(text, values, Modern(instance), keys, _selected, instance.VersionValid && instance.VanillaMajor >= 19);
            PersonalOptionsFiles.ApplyMerged(fileName, bytes, text, merged, _invalidateCache);
            JObject baseline = new();
            foreach (KeyValuePair<string, string> pair in PersonalOptionsRules.Parse(merged).Where(item => keys.Contains(item.Key))) {
                string? value = PersonalOptionsRules.CanonicalValue(pair.Key, pair.Value);
                if (value is not null) baseline[pair.Key] = value;
            }
            PersonalOptionsSessionData session = _sessionFactory();
            session.VersionPath = instance.VersionPath;
            session.GameFolder = folder;
            session.Baseline = baseline;
            session.Keys = keys;
            return session;
        }
    }

    public void Track(PersonalOptionsSessionData? session, Process game) {
        if (session is null) return;
        lock (_gate) {
            _data.WriteJson("options-sessions/" + session.Id + ".json", new JObject(
                new JProperty("versionPath", session.VersionPath), new JProperty("gameFolder", session.GameFolder),
                new JProperty("pid", game.Id), new JProperty("started", game.StartTime.ToUniversalTime().Ticks),
                new JProperty("baseline", session.Baseline), new JProperty("keys", new JArray(session.Keys))));
            session.Tracked = true;
        }
    }

    public void CaptureEditedBinding(IPersonalOptionsInstance? instance, string key, string value) {
        if (!Enabled(instance) || !_selected(key)) return;
        lock (_gate) {
            JObject profile = _data.ReadJson(ProfileName);
            JObject? values = profile["values"] as JObject;
            if (values is null) return;
            string? canonical = PersonalOptionsRules.CanonicalValue(key, value);
            if (canonical is null) return;
            values[PersonalOptionsRules.CanonicalKey(key)] = canonical;
            _data.WriteJson(ProfileName, profile);
        }
    }

    public void Finish(PersonalOptionsSessionData? session) {
        if (session is null) return;
        lock (_gate) {
            string journal = _data.Address("options-sessions/" + session.Id + ".json");
            if (session.Tracked && !File.Exists(journal)) return;
            Capture(session);
            if (File.Exists(journal)) File.Delete(journal);
        }
    }

    public void Capture(PersonalOptionsSessionData session) {
        IPersonalOptionsInstance instance = _instanceFactory(session.VersionPath!);
        if (!Enabled(instance)) return;
        byte[]? bytes = PersonalOptionsFiles.Read(Path.Combine(session.GameFolder!, "options.txt"));
        if (bytes is null) return;
        Dictionary<string, string> current = PersonalOptionsRules.Parse(PersonalOptionsRules.Decode(bytes));
        lock (_gate) {
            JObject profile = _data.ReadJson(ProfileName);
            JObject? values = profile["values"] as JObject;
            if (values is null) return;
            foreach (string key in session.Keys.Where(_selected)) {
                if (!current.TryGetValue(key, out string? raw)) continue;
                string? value = PersonalOptionsRules.CanonicalValue(key, raw);
                if (value is not null && session.Baseline![key] is not null && value != session.Baseline[key]!.ToString()) values[key] = value;
            }
            _data.WriteJson(ProfileName, profile);
        }
    }

    public void RecoverExitedSessions() {
        string folder = _data.Address("options-sessions");
        if (!Directory.Exists(folder)) return;
        lock (_gate) {
            foreach (string fileName in Directory.EnumerateFiles(folder, "*.json")) {
                try {
                    JObject json = _data.ReadJson("options-sessions/" + Path.GetFileName(fileName));
                    if (LocalGameDataStore.ProcessMatches(json["pid"]!.ToObject<int>(), json["started"]!.ToObject<long>())) continue;
                    PersonalOptionsSessionData session = _sessionFactory();
                    session.Id = Path.GetFileNameWithoutExtension(fileName);
                    session.VersionPath = json["versionPath"]!.ToString();
                    session.GameFolder = json["gameFolder"]!.ToString();
                    session.Baseline = (JObject?) json["baseline"];
                    session.Keys = new HashSet<string>(json["keys"]!.Select(item => item.ToString()), StringComparer.Ordinal);
                    session.Tracked = true;
                    Finish(session);
                } catch (Exception error) {
                    _warn(error, "恢复个人游戏设置同步会话失败");
                }
            }
        }
    }

    public bool HasActiveSession(string gameFolder) {
        string folder = _data.Address("options-sessions");
        if (!Directory.Exists(folder)) return false;
        lock (_gate) {
            foreach (string fileName in Directory.EnumerateFiles(folder, "*.json")) {
                JObject json = _data.ReadJson("options-sessions/" + Path.GetFileName(fileName));
                if (_samePath(json["gameFolder"]!.ToString(), gameFolder) && LocalGameDataStore.ProcessMatches(json["pid"]!.ToObject<int>(), json["started"]!.ToObject<long>())) return true;
            }
        }
        return false;
    }
}
