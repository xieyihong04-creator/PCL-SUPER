using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PCL.Living.Features;

/// <summary>按原 LF 文件事务修改一个按键；游戏状态、路径与缓存仍由调用者适配。</summary>
public static class InstanceKeybindingFiles {
    public static void Save(string gameFolder, string key, string? previous, string replacement,
        object gate, Func<bool> hasRunning, Action<string, string> ensureWithin, Action<string> invalidateCache) {
        if (hasRunning()) throw new IOException("请先关闭游戏，再修改按键配置。");
        if (!key.StartsWith("key_", StringComparison.Ordinal) || PersonalOptionsRules.NormalizeBinding(replacement) is null)
            throw new IOException("按键值无效。");
        string fileName = Path.Combine(gameFolder, "options.txt");
        ensureWithin(gameFolder, fileName);
        if (!File.Exists(fileName) || new FileInfo(fileName).Length > 4 * 1024 * 1024)
            throw new IOException("请先运行一次游戏，生成按键配置。");
        lock (gate) {
            byte[] bytes = File.ReadAllBytes(fileName);
            string text = PersonalOptionsRules.Decode(bytes);
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            int index = lines.FindLastIndex(line => line.StartsWith(key + ":", StringComparison.Ordinal));
            if (index < 0) {
                if (previous is not null) throw new IOException("该按键配置已变化，请刷新后重试。");
                if (lines.Count > 0 && lines.Last() == "") lines.RemoveAt(lines.Count - 1);
                lines.Add(key + ":" + replacement);
                if (text.EndsWith("\n", StringComparison.Ordinal)) lines.Add("");
            } else if (previous is null || lines[index].Substring(key.Length + 1) != previous) {
                throw new IOException("该按键配置已变化，请刷新后重试。");
            }
            for (int position = 0; position < lines.Count; position++) {
                if (lines[position].StartsWith(key + ":", StringComparison.Ordinal)) lines[position] = key + ":" + replacement;
            }
            UTF8Encoding encoding = new(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            byte[] body = encoding.GetPreamble().Concat(encoding.GetBytes(string.Join(newline, lines))).ToArray();
            LocalGameDataStore.AtomicWrite(fileName, body, LocalGameDataStore.Hash(bytes), true);
            invalidateCache(fileName);
        }
    }
}
