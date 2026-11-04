using System.IO;
using PCL.Core.Minecraft.Saves;

namespace PCL;

/// <summary>
///     为首页“继续上次的世界”卡片查找最近游玩的存档，并确认实例是否支持直接进入存档。
///     只读取存档目录，不修改任何文件。
/// </summary>
internal static class ContinueWorldService
{
    /// <summary>获取指定实例中最后游玩的存档；没有可用存档时返回 null。</summary>
    internal static async Task<SaveInfo?> FindLatestAsync(McInstance instance, CancellationToken token)
    {
        var savesPath = Path.Combine(instance.PathIndie, "saves");
        if (!Directory.Exists(savesPath))
            return null;
        var saves = await new SaveManager().ScanSaveFoldersAsync(savesPath, token).ConfigureAwait(false);
        var latest = saves.FirstOrDefault(save => Directory.Exists(save.FolderPath));
        return latest ?? FindByFileTime(savesPath);
    }

    /// <summary>存档解析器无法识别时，按 level.dat 修改时间给出仅含文件夹名的兜底结果。</summary>
    private static SaveInfo? FindByFileTime(string savesPath)
    {
        string? latestFolder = null;
        var latestTime = DateTime.MinValue;
        foreach (var folder in Directory.EnumerateDirectories(savesPath))
        {
            var levelDat = Path.Combine(folder, "level.dat");
            if (!File.Exists(levelDat))
                continue;
            var time = File.GetLastWriteTimeUtc(levelDat);
            if (time <= latestTime)
                continue;
            latestTime = time;
            latestFolder = folder;
        }

        if (latestFolder is null)
            return null;
        return new SaveInfo
        {
            LevelName = Path.GetFileName(latestFolder),
            FolderPath = latestFolder,
            LastPlayedUtc = latestTime
        };
    }

    /// <summary>判断实例的版本参数是否包含存档快捷启动，失败时按不支持处理。</summary>
    internal static bool SupportsQuickPlay(McInstance instance)
    {
        try
        {
            return new GameLaunch.LaunchArgument(instance).HasArguments("--quickPlaySingleplayer") is true;
        }
        catch (Exception ex)
        {
            Common.Log(ex, "检查存档快捷启动失败", Common.LogLevel.Developer);
            return false;
        }
    }
}
