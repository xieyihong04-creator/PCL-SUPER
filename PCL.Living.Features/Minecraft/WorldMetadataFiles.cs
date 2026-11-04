using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace PCL.Living.Features;

/// <summary>
/// level.dat 文件读取与 LF 存档详情文案；路径策略和日志由调用端提供。
/// </summary>
public static class WorldMetadataFiles {
    /// <summary>
    /// 保留 LF 的压缩文件大小上限、路径检查顺序和读写／删除共享方式。
    /// 输入文件和解压流由本方法关闭；损坏、占用或路径检查异常交给调用者。
    /// </summary>
    public static WorldMetadataInfo? Read(string folder, bool details, Action<string, string> ensureWithin) {
        string fileName = Path.Combine(folder, "level.dat");
        if (!File.Exists(fileName) || new FileInfo(fileName).Length > 8 * 1024 * 1024) return null;
        ensureWithin(folder, fileName);
        using FileStream file = new(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using GZipStream uncompressed = new(file, CompressionMode.Decompress);
        return WorldMetadataReader.Read(uncompressed, details);
    }

    /// <summary>
    /// 保持原有中文字段顺序、当前区域／本地时间格式、CRLF 和缺失／损坏回退。
    /// 不读取 Minecraft 以外的网络数据，也不修改存档。
    /// </summary>
    public static string Describe(string folder, Action<string, string> ensureWithin, Action<Exception, string> warn) {
        List<string> lines = ["存档文件夹：" + new DirectoryInfo(folder).Name];
        try {
            WorldMetadataInfo? data = Read(folder, true, ensureWithin);
            if (data is null) {
                lines.Add("未找到可读取的 level.dat 元数据。");
            } else {
                if (!string.IsNullOrWhiteSpace(data.Name)) lines.Insert(0, "世界名称：" + data.Name);
                if (!string.IsNullOrWhiteSpace(data.VersionName)) lines.Add("游戏版本：" + data.VersionName + (data.Snapshot.GetValueOrDefault() ? "（快照）" : ""));
                if (data.DataVersion.HasValue) lines.Add("数据版本：" + data.DataVersion.Value);
                if (data.LastPlayedUtc != DateTime.MinValue) lines.Add("最后游玩：" + data.LastPlayedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                if (data.GameMode.HasValue) {
                    string[] names = ["生存", "创造", "冒险", "旁观"];
                    lines.Add("游戏模式：" + (data.GameMode.Value >= 0 && data.GameMode.Value < names.Length ? names[data.GameMode.Value] : "未知（" + data.GameMode.Value + "）"));
                }
                if (data.Difficulty.HasValue) {
                    string[] names = ["和平", "简单", "普通", "困难"];
                    lines.Add("难度：" + (data.Difficulty.Value >= 0 && data.Difficulty.Value < names.Length ? names[data.Difficulty.Value] : "未知（" + data.Difficulty.Value + "）"));
                }
                if (data.Hardcore.HasValue) lines.Add("极限模式：" + (data.Hardcore.Value ? "开启" : "关闭"));
                if (data.AllowCommands.HasValue) lines.Add("允许作弊：" + (data.AllowCommands.Value ? "开启" : "关闭"));
                if (data.WasModded.HasValue) lines.Add("模组环境记录：" + (data.WasModded.Value ? "使用过" : "未标记"));
                if (data.Seed.HasValue) lines.Add("世界种子：" + data.Seed.Value);
                if (data.SpawnX.HasValue && data.SpawnY.HasValue && data.SpawnZ.HasValue) lines.Add($"世界出生点：{data.SpawnX.Value}, {data.SpawnY.Value}, {data.SpawnZ.Value}");
                if (data.DayTime.HasValue && data.DayTime.Value >= 0) lines.Add("游戏日：第 " + (data.DayTime.Value / 24000) + " 天");
                if (data.Time.HasValue) lines.Add("世界累计刻数：" + data.Time.Value);
            }
        } catch (Exception exception) {
            warn(exception, "读取本地存档详情失败");
            lines.Add("无法读取 level.dat；文件可能正在写入、已损坏或格式不受支持。");
        }
        lines.Add("\r\n位置：" + Path.GetFullPath(folder));
        return string.Join("\r\n", lines);
    }
}
