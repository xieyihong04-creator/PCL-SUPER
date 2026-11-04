using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Hints;
using PCL.Living.Features;

namespace PCL;

internal sealed class CeCloneOptions : InstanceCloneOptionsData;

internal sealed class CeCloneResult : InstanceCloneResultData;

internal sealed class CeCloneChange : InstanceCloneMigrationChangeData;

internal sealed class CeClonePlan : InstanceCloneMigrationPlanData<CeCloneChange>;

/// <summary>
///     把 LF 的测试副本引擎接到 CE 的实例列表、确认框与还原点服务上。
///     来源记录仍写入版本目录的 PCL/CloneOrigin.json，迁移前的留档使用环境还原点。
/// </summary>
internal static class InstanceCloneBridge
{
    private static readonly object Gate = new();
    private static InstanceCloneEngine<McInstance, CeCloneOptions, CeCloneResult, CeCloneChange, CeClonePlan>?
        _engine;

    private static InstanceCloneWorkflow<McInstance, CeCloneOptions, CeCloneResult, CeCloneChange, CeClonePlan>?
        _workflow;

    internal static InstanceCloneEngine<McInstance, CeCloneOptions, CeCloneResult, CeCloneChange, CeClonePlan> Engine =>
        _engine ??= new InstanceCloneEngine<McInstance, CeCloneOptions, CeCloneResult, CeCloneChange, CeClonePlan>(
            Gate, CreateEngineHost(), () => new CeCloneOptions(), () => new CeCloneResult(), () => new CeCloneChange(),
            () => new CeClonePlan());

    internal static InstanceCloneWorkflow<McInstance, CeCloneOptions, CeCloneResult, CeCloneChange, CeClonePlan>
        Workflow => _workflow ??= new InstanceCloneWorkflow<McInstance, CeCloneOptions, CeCloneResult, CeCloneChange,
            CeClonePlan>(Engine, CreateWorkflowHost());

    /// <summary>该实例是否存在来源记录，即是否为测试副本。</summary>
    internal static bool IsClone(McInstance? instance)
    {
        if (instance is null) return false;
        try
        {
            return File.Exists(Path.Combine(instance.PathInstance, "PCL", "CloneOrigin.json"));
        }
        catch
        {
            return false;
        }
    }

    internal static void Clone(McInstance? instance, string newName, CeCloneOptions? options) =>
        Workflow.Start(instance, newName, options);

    internal static void Migrate(McInstance? cloneInstance) => Workflow.Migrate(cloneInstance);

    #region 引擎宿主

    private static InstanceCloneHost<McInstance> CreateEngineHost() => new()
    {
        RemoveExtendedPrefix = RemoveExtendedPrefix,
        RelativePath = RelativePath,
        SafeName = SafeName,
        AddSlashSuffix = path => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar,
        EnsureWithin = EnvironmentHistoryBridge.EnsureWithin,
        SamePath = SamePath,
        HasRunningGame = () => GameLaunch.isLaunching || GameLaunch.mcLaunchProcess is { HasExited: false },
        HasRunningSessions = () => false,
        LaunchLoading = () => GameLaunch.isLaunching,
        WorldBackupBusy = () => false,
        HasActiveGame = _ => GameLaunch.mcLaunchProcess is { HasExited: false },
        HasPending = EnvironmentHistoryBridge.HasPending,
        VersionPath = instance => instance.PathInstance,
        VanillaName = instance => instance.Info.VanillaName,
        IsFool = instance => instance.state == McInstanceState.Fool,
        IsSnapshot = instance => instance.state == McInstanceState.Snapshot,
        HasModLoaders = instance =>
            instance.Info.HasForge || instance.Info.HasNeoForge || instance.Info.HasFabric ||
            instance.Info.HasQuilt || instance.Info.HasLiteLoader || instance.Info.HasCleanroom,
        // LF 的 ApiType 指独立 API 版本，CE 没有对应概念。
        ApiType = _ => false,
        IsolationMode = () => PCL.Core.App.Config.Launch.IndieSolutionV2,
        CaptureEnvironment = (versionFolder, _, label, token) =>
        {
            var source = new McInstance(versionFolder);
            source.Load();
            return EnvironmentHistoryBridge.Engine.CaptureInstance(source, label, token);
        },
        ApplyTransaction = EnvironmentHistoryBridge.ApplyTransaction
    };

    #endregion

    #region 任务与界面宿主

    private static InstanceCloneWorkflowHost<McInstance> CreateWorkflowHost() => new()
    {
        VersionPath = instance => instance.PathInstance,
        Name = instance => instance.Name,
        CreateInstance = versionFolder =>
        {
            var instance = new McInstance(versionFolder);
            instance.Load();
            return instance;
        },
        HasRunningGame = () => GameLaunch.isLaunching || GameLaunch.mcLaunchProcess is { HasExited: false },
        HasRunningSessions = () => false,
        LaunchLoading = () => GameLaunch.isLaunching,
        WorldBackupBusy = () => false,
        EnvironmentBusy = () => EnvironmentHistoryBridge.Workflow.IsBusy,
        Updating = () => false,
        AnyLocked = () => false,
        UpdatingModFolders = () => false,
        HasActiveGame = _ => GameLaunch.mcLaunchProcess is { HasExited: false },
        Hint = (message, success) => LauncherCommon.RunInUi(() =>
            HintService.Show(success ? Hints.Info(message) : Hints.Error(message))),
        Confirm = (caption, title, button1, button2, warn) =>
            LauncherCommon.RunInUiWait(() =>
                CompUi.Current?.ShowDialog(caption, title, button1, button2, "", warn) ?? -1),
        Dispatch = action => LauncherCommon.RunInUi(action),
        ResetSettingsCache = _ => { },
        CaptureInstance = (instance, label, token) =>
            EnvironmentHistoryBridge.Engine.CaptureInstance(instance, label, token),
        RefreshInstances = RefreshInstanceList,
        NotifyChanged = () => EnvironmentHistoryBridge.NotifyChanged(),
        StartWork = (_, work) => StartWork(work)
    };

    private static void RefreshInstanceList()
    {
        InstanceList.mcInstanceListForceRefresh = true;
        Loading.LoaderFolderRun(InstanceList.mcInstanceListLoader, GameFolders.mcFolderSelected,
            Loading.LoaderFolderRunType.ForceRun, 1, @"versions\");
    }

    private static void StartWork(InstanceCloneWork work)
    {
        var loader = new Loading.LoaderTask<string, string>(work.StepName, task =>
        {
            try
            {
                string? output = null;
                work.Execute(task.AbortedToken, value => output = value ?? output);
                task.output = output ?? "";
            }
            catch (OperationCanceledException)
            {
                // 玩家在确认框中取消，属于正常流程。
            }
            catch (Exception ex)
            {
                work.SourceError?.Invoke(ex);
                Common.Log(ex, "测试副本操作失败", Common.LogLevel.Msgbox,
                    userSummary: "测试副本操作失败：" + ex.Message);
                throw;
            }
            finally
            {
                work.ReleaseBusy?.Invoke();
                work.Stopped?.Invoke();
            }
        });
        loader.Start();
    }

    #endregion

    #region 路径

    private static string RemoveExtendedPrefix(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;

    private static string RelativePath(string value)
    {
        if (Path.IsPathRooted(value)) throw new IOException("路径无效。");
        var normalized = value.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0) throw new IOException("路径无效。");
        foreach (var segment in normalized.Split('/'))
            if (segment is "" or "." or "..")
                throw new IOException("路径无效。");
        return normalized;
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("版本名称无效：" + name);
        return name;
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(
            Path.GetFullPath(RemoveExtendedPrefix(first)).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(RemoveExtendedPrefix(second)).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    #endregion
}
