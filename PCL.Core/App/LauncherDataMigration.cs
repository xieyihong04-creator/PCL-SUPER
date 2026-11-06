using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCL.Core.App;

/// <summary>
/// 首次以独立数据目录启动时，从 PCL CE 的配置目录复制启动器数据。
/// 只复制，不移动、不删除来源，避免影响仍在使用的 PCL CE。
/// </summary>
internal static class LauncherDataMigration
{
    internal static readonly string[] SharedConfigFileNames =
    [
        "config.v1.json",
        "config.v1.json.bak",
        "config.json",
        "Config.json",
        "UserKey.bin",
        "profiles.json",
        "profiles.json.bak"
    ];

    internal readonly record struct MigrationResult(
        bool Ran,
        IReadOnlyList<string> CopiedFileNames,
        string? SourceDirectory);

    internal static MigrationResult MigrateSharedConfig(
        string targetDirectory,
        string markerPath,
        IReadOnlyList<string> sourceDirectories)
    {
        if (File.Exists(markerPath))
            return new MigrationResult(false, [], null);

        var targetFullPath = Normalize(targetDirectory);
        var sources = sourceDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .Where(path => !string.Equals(Normalize(path), targetFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sources.Count == 0)
            return new MigrationResult(false, [], null);

        Directory.CreateDirectory(targetDirectory);
        var copied = new List<string>();
        string? usedSource = null;
        var failed = false;
        foreach (var name in SharedConfigFileNames)
        {
            var targetPath = Path.Combine(targetDirectory, name);
            if (File.Exists(targetPath))
                continue;
            foreach (var source in sources)
            {
                var sourcePath = Path.Combine(source, name);
                if (!File.Exists(sourcePath))
                    continue;
                try
                {
                    CopyShared(sourcePath, targetPath);
                    copied.Add(name);
                    usedSource ??= source;
                }
                catch (Exception)
                {
                    failed = true;
                }

                break;
            }
        }

        // 有文件复制失败时不落标记，下次启动还会再试
        if (!failed)
        {
            try
            {
                File.WriteAllText(
                    markerPath,
                    $"migrated from {usedSource ?? sources[0]} at {DateTimeOffset.Now:O}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // 标记写入失败只会导致下次重复检查，不影响本次启动
            }
        }

        return new MigrationResult(true, copied, usedSource ?? sources[0]);
    }

    private static void CopyShared(string sourcePath, string targetPath)
    {
        using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        input.CopyTo(output);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
