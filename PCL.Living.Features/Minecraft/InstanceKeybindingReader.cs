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

/// <summary>保留原按键项字段，供尚未迁移的页面以派生类型继续使用。</summary>
public class InstanceKeybindingData {
    public string? Key;
    public string? Name;
    public string Owners = "未知归属";
    public string? Value;
    public string? Normalized;
    public int SharedCount;
    public bool Saved = true;
}

/// <summary>读取已保存和默认按键，并按原本地 JAR 语言资源补充名称与归属。</summary>
public static class InstanceKeybindingReader {
    public static List<InstanceKeybindingData> ReadBindings(string gameFolder, CancellationToken token,
        Action<string, string> ensureWithin, Action<Exception, string> warn, Func<string, string> normalizeTitle,
        Func<InstanceKeybindingData> factory) {
        string fileName = Path.Combine(gameFolder, "options.txt");
        List<InstanceKeybindingData> result = new();
        if (!File.Exists(fileName)) return DefaultBindings(factory);
        ensureWithin(gameFolder, fileName);
        if (new FileInfo(fileName).Length > 4 * 1024 * 1024) throw new IOException("游戏设置文件过大。");
        string text = File.ReadAllText(fileName, new UTF8Encoding(false, true));
        string language = "zh_cn";
        foreach (string line in text.Replace("\r\n", "\n").Split('\n')) {
            token.ThrowIfCancellationRequested();
            int separator = line.IndexOf(':');
            if (separator < 1) continue;
            string key = line.Substring(0, separator);
            string value = line.Substring(separator + 1);
            if (key == "lang") language = value.ToLowerInvariant();
            if (!key.StartsWith("key_", StringComparison.Ordinal)) continue;
            InstanceKeybindingData? existing = result.FirstOrDefault(item => item.Key == key);
            if (existing is not null) result.Remove(existing);
            InstanceKeybindingData entry = factory();
            entry.Key = key;
            entry.Name = key.Substring(4);
            entry.Value = value;
            entry.Normalized = PersonalOptionsRules.NormalizeBinding(value);
            entry.Owners = PersonalOptionsRules.VanillaBinding(key) ? "Minecraft" : "未知归属";
            result.Add(entry);
        }
        foreach (InstanceKeybindingData defaultEntry in DefaultBindings(factory)) {
            InstanceKeybindingData? existing = result.FirstOrDefault(item => item.Key == defaultEntry.Key || (defaultEntry.Key == "key_key.swapOffhand" && item.Key == "key_key.swapHands"));
            if (existing is null) result.Add(defaultEntry); else existing.Name = defaultEntry.Name;
        }
        Dictionary<string, InstanceKeybindingData> targets = result.Where(item => !PersonalOptionsRules.VanillaBinding(item.Key!)).ToDictionary(item => item.Key!.Substring(4), item => item);
        Dictionary<string, HashSet<string>> ownerMap = new(StringComparer.Ordinal);
        string mods = Path.Combine(gameFolder, "mods");
        if (Directory.Exists(mods)) {
            foreach (string modFile in Directory.EnumerateFiles(mods, "*.jar")) {
                token.ThrowIfCancellationRequested();
                try {
                    ensureWithin(gameFolder, modFile);
                    using ZipArchive archive = ZipFile.OpenRead(modFile);
                    string modName = Path.GetFileNameWithoutExtension(modFile);
                    ZipArchiveEntry? metadata = archive.GetEntry("fabric.mod.json");
                    if (metadata is not null && metadata.Length < 1024 * 1024) {
                        using StreamReader reader = new(metadata.Open());
                        JObject info = JObject.Parse(reader.ReadToEnd());
                        modName = info["name"]?.ToString() ?? modName;
                    } else {
                        ZipArchiveEntry? toml = archive.GetEntry("META-INF/neoforge.mods.toml") ?? archive.GetEntry("META-INF/mods.toml");
                        if (toml is not null && toml.Length < 1024 * 1024) {
                            using StreamReader reader = new(toml.Open());
                            Match match = Regex.Match(reader.ReadToEnd(), "(?m)^\\s*displayName\\s*=\\s*\"([^\"]+)\"");
                            if (match.Success) modName = match.Groups[1].Value;
                        }
                    }
                    List<ZipArchiveEntry> languages = archive.Entries.Where(entry => entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) && entry.Length < 4 * 1024 * 1024 &&
                        (entry.FullName.EndsWith("/lang/en_us.json", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith("/lang/" + language + ".json", StringComparison.OrdinalIgnoreCase) ||
                         entry.FullName.EndsWith("/lang/en_us.lang", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith("/lang/" + language + ".lang", StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(entry => entry.FullName.Contains("/lang/" + language + ".")).ToList();
                    foreach (ZipArchiveEntry entry in languages) {
                        token.ThrowIfCancellationRequested();
                        Dictionary<string, string> values = new(StringComparer.Ordinal);
                        using (StreamReader reader = new(entry.Open())) {
                            string content = reader.ReadToEnd();
                            if (entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
                                foreach (JProperty pair in JObject.Parse(content).Properties().Where(item => item.Value.Type == JTokenType.String)) values[pair.Name] = pair.Value.ToString();
                            } else {
                                foreach (string line in content.Replace("\r\n", "\n").Split('\n')) {
                                    int separator = line.IndexOf('=');
                                    if (separator > 0) values[line.Substring(0, separator)] = line.Substring(separator + 1);
                                }
                            }
                        }
                        foreach (KeyValuePair<string, InstanceKeybindingData> pair in targets) {
                            if (!values.TryGetValue(pair.Key, out string? title)) continue;
                            pair.Value.Name = normalizeTitle(title);
                            if (!ownerMap.ContainsKey(pair.Key)) ownerMap[pair.Key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            ownerMap[pair.Key].Add(modName + "（" + Path.GetFileName(modFile) + "）");
                        }
                    }
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException) {
                    warn(error, "读取按键所属 Mod 的本地信息失败");
                }
            }
        }
        foreach (KeyValuePair<string, HashSet<string>> pair in ownerMap) targets[pair.Key].Owners = string.Join("、", pair.Value.OrderBy(name => name));
        foreach (IGrouping<string, InstanceKeybindingData> group in result.Where(item => !string.IsNullOrEmpty(item.Normalized) && item.Normalized != "key.keyboard.unknown").GroupBy(item => item.Normalized!, StringComparer.OrdinalIgnoreCase)) {
            foreach (InstanceKeybindingData item in group) item.SharedCount = group.Count();
        }
        return result;
    }

    public static List<InstanceKeybindingData> DefaultBindings(Func<InstanceKeybindingData> factory) {
        Dictionary<string, string> defaults = new() {
            {"attack", "mouse.left"}, {"use", "mouse.right"}, {"pickItem", "mouse.middle"},
            {"forward", "keyboard.w"}, {"back", "keyboard.s"}, {"left", "keyboard.a"}, {"right", "keyboard.d"},
            {"jump", "keyboard.space"}, {"sneak", "keyboard.left.shift"}, {"sprint", "keyboard.left.control"},
            {"drop", "keyboard.q"}, {"inventory", "keyboard.e"}, {"chat", "keyboard.t"}, {"playerlist", "keyboard.tab"},
            {"command", "keyboard.slash"}, {"screenshot", "keyboard.f2"}, {"togglePerspective", "keyboard.f5"},
            {"fullscreen", "keyboard.f11"}, {"swapOffhand", "keyboard.f"}, {"advancements", "keyboard.l"},
            {"saveToolbarActivator", "keyboard.c"}, {"loadToolbarActivator", "keyboard.x"}
        };
        Dictionary<string, string> names = new() {
            {"attack", "攻击/破坏"}, {"use", "使用物品/放置方块"}, {"pickItem", "选取方块"},
            {"forward", "前进"}, {"back", "后退"}, {"left", "向左"}, {"right", "向右"}, {"jump", "跳跃"},
            {"sneak", "潜行"}, {"sprint", "疾跑"}, {"drop", "丢弃物品"}, {"inventory", "打开物品栏"},
            {"chat", "聊天"}, {"playerlist", "玩家列表"}, {"command", "输入命令"}, {"screenshot", "截图"},
            {"togglePerspective", "切换视角"}, {"fullscreen", "切换全屏"}, {"swapOffhand", "交换副手物品"},
            {"advancements", "进度"}, {"saveToolbarActivator", "保存快捷栏"}, {"loadToolbarActivator", "加载快捷栏"}
        };
        List<InstanceKeybindingData> result = new();
        foreach (KeyValuePair<string, string> pair in defaults) {
            InstanceKeybindingData entry = factory();
            entry.Key = "key_key." + pair.Key;
            entry.Name = names[pair.Key];
            entry.Owners = "Minecraft";
            entry.Value = "key." + pair.Value;
            entry.Normalized = "key." + pair.Value;
            entry.Saved = false;
            entry.SharedCount = 1;
            result.Add(entry);
        }
        for (int index = 1; index <= 9; index++) {
            InstanceKeybindingData entry = factory();
            entry.Key = "key_key.hotbar." + index;
            entry.Name = "快捷栏 " + index;
            entry.Owners = "Minecraft";
            entry.Value = "key.keyboard." + index;
            entry.Normalized = "key.keyboard." + index;
            entry.Saved = false;
            entry.SharedCount = 1;
            result.Add(entry);
        }
        return result;
    }
}
