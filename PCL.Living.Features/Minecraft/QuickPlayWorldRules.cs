using System;
using System.Collections.Generic;
using System.IO;

namespace PCL.Living.Features;

/// <summary>原 Quick Play 的存档路径检查与启动参数移除；不扩展版本或启动规则。</summary>
public static class QuickPlayWorldRules {
    public static string WorldPath(string gameFolder, string? identifier, Action<string, string> ensureWithin) {
        if (string.IsNullOrWhiteSpace(identifier) || identifier == "." || identifier == ".." || LegacyFileName(identifier!) != identifier || identifier!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("存档文件夹名称无效。");
        LegacyPathCharacters.Check(gameFolder);
        string root = Path.Combine(gameFolder, "saves");
        string world = Path.Combine(root, identifier!);
        ensureWithin(root, world);
        if (!Directory.Exists(world) || !File.Exists(Path.Combine(world, "level.dat")))
            throw new IOException("该存档已移动、删除或尚未完整生成，请刷新存档列表。");
        return world;
    }

    private static string LegacyFileName(string path) {
        LegacyPathCharacters.Check(path);
        return Path.GetFileName(path);
    }

    public static void RemoveArguments(List<string> arguments, IEnumerable<string> names) {
        HashSet<string> setNames = new(names, StringComparer.Ordinal);
        int index = 0;
        while (index < arguments.Count) {
            if (setNames.Contains(arguments[index])) {
                arguments.RemoveAt(index);
                if (index < arguments.Count && !arguments[index].StartsWith("--", StringComparison.Ordinal)) arguments.RemoveAt(index);
            } else {
                index++;
            }
        }
    }
}
