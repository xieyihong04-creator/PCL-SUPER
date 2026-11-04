using System;
using System.IO;

namespace PCL.Living.Features;

/// <summary>原本地资源目录边界；前缀处理仍由调用方的固定 Meloong 实现提供。</summary>
public static class InstanceResourcePaths {
    public static string Normalize(string value, Func<string, string> removeExtendedPrefix) {
        return InstanceResourcePathCompatibility.GetFullPath(removeExtendedPrefix(value)).TrimEnd('\\', '/');
    }

    public static void EnsureWithin(string root, string fileName, bool allowRoot, Func<string, string> removeExtendedPrefix) {
        string parent = Normalize(root, removeExtendedPrefix), full = Normalize(fileName, removeExtendedPrefix);
        if (allowRoot && InstanceResourcePathText.SamePathText(full, parent)) return;
        if (!InstanceResourcePathText.StartsWithPathText(full, parent + "\\"))
            throw new IOException("资源路径超出了当前目录。");
        string current = full;
        while (!InstanceResourcePathText.SamePathText(current, parent)) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请通过资源管理器管理链接文件或链接目录。");
            current = InstanceResourcePathCompatibility.GetDirectoryName(current)!;
        }
    }
}
