using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PCL.Living.Features;

public class InstanceCloneExportChoiceData {
    public readonly bool Enabled;
    public readonly List<string> VersionFolders;
    public InstanceCloneExportChoiceData(bool enabled, IEnumerable<string>? extraVersionFolders = null) {
        Enabled = enabled; VersionFolders = new List<string> { "PCL" };
        foreach (string folder in extraVersionFolders ?? Enumerable.Empty<string>()) {
            string relative = InstanceCloneExportIsolationRules.NormalizeRelative(folder).TrimEnd('\\');
            if (!VersionFolders.Contains(relative, StringComparer.OrdinalIgnoreCase)) VersionFolders.Add(relative);
        }
    }
}
public static class InstanceCloneExportIsolationRules {
    public const string MarkerKey = "LivingExportVersionIsolation";
    public static string NormalizeRelative(string value) {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.StartsWith("/")) throw new InvalidDataException("整合包中包含无效的相对文件路径。");
        string[] parts = value.Replace("/", "\\").TrimEnd('\\').Split('\\');
        if (parts.Any(part => part == ".." || part == "." || part == "" || part.Contains(":"))) throw new InvalidDataException("整合包的文件路径超出了游戏目录。");
        return string.Join("\\", parts);
    }
}
public sealed class InstanceCloneExportIsolationHost {
    public Action<string, string, object> WriteIni = null!;
    public Action<string> ClearIniCache = null!;
    public Func<string, string> AddSlashSuffix = null!;
    public Action<string, string> RemoveSettingCache = null!, CopyFile = null!;
}

/// <summary>只处理 LF 导出标记的原隔离选择和覆盖目录路由。</summary>
// LF ExportedVersionIsolation.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// unchanged through f1b16ce83b7e272a6d34220566e43d89a67afd09. No CE code reused.
public sealed class InstanceCloneExportIsolationEngine<TChoice> where TChoice : InstanceCloneExportChoiceData {
    public const string MarkerKey = InstanceCloneExportIsolationRules.MarkerKey;
    private const string VersionFoldersKey = "LivingExportVersionFolders";
    private readonly InstanceCloneExportIsolationHost host;
    private readonly Func<bool, IEnumerable<string>?, TChoice> createChoice;
    public InstanceCloneExportIsolationEngine(InstanceCloneExportIsolationHost host, Func<bool, IEnumerable<string>?, TChoice> createChoice) { this.host = host; this.createChoice = createChoice; }
    public void ConfigureExport(string overridesFolder, bool enabled, IEnumerable<string>? versionFolders = null) {
        TChoice policy = createChoice(enabled, versionFolders);
        string setupFile = Path.Combine(overridesFolder, "PCL", "Setup.ini");
        host.WriteIni(setupFile, MarkerKey, enabled);
        host.WriteIni(setupFile, VersionFoldersKey, string.Join("|", policy.VersionFolders));
        ConfigureTarget(overridesFolder, policy);
    }
    public TChoice? ReadArchiveChoice(ZipArchive archive, string archiveBaseFolder) {
        string name = (archiveBaseFolder + "overrides/PCL/Setup.ini").Replace("\\", "/");
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(item => item.FullName.Replace("\\", "/").Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        if (entry.Length > 1024L * 1024) throw new InvalidDataException("整合包的版本配置文件过大。");
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        using (StreamReader reader = new(entry.Open())) {
            while (!reader.EndOfStream) { string line = reader.ReadLine()!; int separator = line.IndexOf(':'); if (separator > 0) values[line.Substring(0, separator)] = line.Substring(separator + 1); }
        }
        if (!values.TryGetValue(MarkerKey, out string? raw) || !bool.TryParse(raw, out bool enabled)) return null;
        values.TryGetValue(VersionFoldersKey, out string? folders);
        return createChoice(enabled, (folders ?? "PCL").Split('|'));
    }
    public void ConfigureTarget(string versionFolder, TChoice policy) {
        string setupFile = Path.Combine(versionFolder, "PCL", "Setup.ini");
        host.WriteIni(setupFile, "VersionArgumentIndie", policy.Enabled ? 1 : 2);
        host.WriteIni(setupFile, "VersionArgumentIndieV2", policy.Enabled);
        host.ClearIniCache(setupFile);
        string folder = host.AddSlashSuffix(versionFolder);
        foreach (string key in new[] { "VersionArgumentIndie", "VersionArgumentIndieV2" }) host.RemoveSettingCache(key, folder);
    }
    public string GameFolder(string versionFolder, string minecraftFolder, TChoice? policy) => policy is null || policy.Enabled ? versionFolder : minecraftFolder;
    public string OverrideTarget(string relative, string versionFolder, string gameFolder, TChoice policy) {
        relative = InstanceCloneExportIsolationRules.NormalizeRelative(relative);
        bool isVersionFile = policy.VersionFolders.Any(folder => relative.Equals(folder, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase));
        if (!policy.Enabled && !isVersionFile && new[] { "versions", "libraries", "assets" }.Contains(relative.Split('\\')[0], StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("整合包不能向共享游戏目录的版本、支持库或资源缓存目录写入附加文件。");
        return Path.Combine(policy.Enabled || isVersionFile ? versionFolder : gameFolder, relative);
    }
    public void CopyOverrides(string overridesFolder, string versionFolder, string gameFolder, TChoice policy) {
        if (Directory.Exists(overridesFolder)) {
            string prefix = Path.GetFullPath(overridesFolder).TrimEnd('\\') + "\\";
            foreach (string fileName in Directory.EnumerateFiles(prefix, "*", SearchOption.AllDirectories)) { string relative = fileName.Substring(prefix.Length); host.CopyFile(fileName, OverrideTarget(relative, versionFolder, gameFolder, policy)); }
        }
        ConfigureTarget(versionFolder, policy);
        host.WriteIni(Path.Combine(versionFolder, "PCL", "Setup.ini"), "IsStar", false);
    }
}
