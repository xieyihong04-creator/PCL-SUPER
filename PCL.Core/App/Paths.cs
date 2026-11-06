using System;
using System.IO;
using PCL.Core.Utils.OS;
using Special = System.Environment.SpecialFolder;

namespace PCL.Core.App;

/// <summary>
/// Application global paths.<br/>
/// <b>NOTE</b>: The behaviors of all path strings depends on <see cref="Path"/> API provided by
/// .NET standard library. You should use <see cref="Path"/> and other APIs relative to it to process
/// any path string from this service, rather than concat paths manually.
/// </summary>
public static class Paths
{
    /// <summary>
    /// The default directory used for relative path combining.
    /// </summary>
    public static string DefaultDirectory => Basics.ExecutableDirectory;

    private static string _data;
    private static string _sharedData;
    private static string _sharedLocalData;
    private static string _temp;
    
    /// <summary>
    /// Per-instance data directory.
    /// </summary>
    public static string Data { get => _data; set => _data = value; }

    /// <summary>
    /// Shared synchronized data directory.
    /// </summary>
    public static string SharedData { get => _sharedData; set => _sharedData = value; }
    
    /// <summary>
    /// Shared synchronized data directory of old versions.<br/>
    /// Keep the value just for migration, DO NOT USE IT.
    /// </summary>
    public static string OldSharedData { get; set; }

    /// <summary>
    /// Shared local data directory, used to put some large files that can be released or downloaded back anytime.
    /// </summary>
    public static string SharedLocalData { get => _sharedLocalData; set => _sharedLocalData = value; }
    
    /// <summary>
    /// Temporary files directory (can be deleted anytime, except when the program is running).
    /// </summary>
    public static string Temp { get => _temp; set => _temp = value; }

    /// <summary>
    /// 首次启动时从 PCL CE 配置目录导入文件的来源目录，未发生迁移时为 <see langword="null"/>。
    /// </summary>
    public static string? LegacyMigrationSource { get; private set; }

    /// <summary>
    /// 从 PCL CE 配置目录导入的文件数量。
    /// </summary>
    public static int LegacyMigrationFileCount { get; private set; }

    internal const string LegacyMigrationMarkerName = ".migrated-from-pclce";

    /// <summary>
    /// Get path string relative to a special folder.
    /// </summary>
    /// <param name="folder">the special folder</param>
    /// <param name="relative">the relative path</param>
    /// <returns>the path string relative to the special folder</returns>
    public static string GetSpecialPath(Special folder, string relative)
    {
        var folderPath = Environment.GetFolderPath(folder);
        return Path.Combine(folderPath, relative);
    }

    static Paths()
    {
#if DEBUG
        const string name = "PCLSUPER_Debug";
        const string oldName = ".PCLSUPER_Debug";
        const string legacyName = "PCLCE_Debug";
        const string legacyOldName = ".PCLCEDebug";
#else
        const string name = "PCLSUPER";
        const string oldName = ".PCLSUPER";
        const string legacyName = "PCLCE";
        const string legacyOldName = ".PCLCE";
#endif
        // fill paths
        _data = Path.Combine(DefaultDirectory, "PCL");
        _sharedData = GetSpecialPath(Special.ApplicationData, name);
        _sharedLocalData = GetSpecialPath(Special.LocalApplicationData, name);
        _temp = Path.Combine(Path.GetTempPath(), name);
        OldSharedData = GetSpecialPath(Special.ApplicationData, oldName);
#if DEBUG
        // read environment variables
        EnvironmentInterop.ReadVariable("PCL_PATH", ref _data);
        EnvironmentInterop.ReadVariable("PCL_PATH_SHARED", ref _sharedData);
        EnvironmentInterop.ReadVariable("PCL_PATH_LOCAL", ref _sharedLocalData);
        EnvironmentInterop.ReadVariable("PCL_PATH_TEMP", ref _temp);
#endif
        // create directories
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_sharedData);
        Directory.CreateDirectory(_sharedLocalData);
        Directory.CreateDirectory(_temp);

        // PCL SUPER 曾与 PCL CE 共用配置目录，首次启动时复制一份到自己的目录（只读来源，不移动不删除）
        try
        {
            var result = LauncherDataMigration.MigrateSharedConfig(
                _sharedData,
                Path.Combine(_sharedData, LegacyMigrationMarkerName),
                [
                    GetSpecialPath(Special.ApplicationData, legacyName),
                    GetSpecialPath(Special.ApplicationData, legacyOldName)
                ]);
            if (result.Ran)
            {
                LegacyMigrationSource = result.SourceDirectory;
                LegacyMigrationFileCount = result.CopiedFileNames.Count;
            }
        }
        catch (Exception)
        {
            // 迁移失败不应阻止启动
        }
    }
}
