using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>原 LF 路径、事务、实例与个人资料服务的类型化边界。</summary>
public sealed class EnvironmentHistoryHost<TInstance> where TInstance : class {
    public Func<string> DataRoot = null!;
    public Func<byte[], string> Hash = null!;
    public Action<string, byte[]> AtomicWrite = null!;
    public Func<string, string, string> Target = null!;
    public Func<string, string> RelativePath = null!, AddSlashSuffix = null!, RemoveExtendedPrefix = null!, SafeName = null!;
    public Func<string, string?> Digest = null!;
    public Action<string> EnsureNoLinks = null!;
    public Action<string, string> EnsureWithin = null!;
    public Func<string, string, bool> IsParentOf = null!, SamePath = null!;
    public Action<string, string, List<Tuple<string, string?, string?>>, CancellationToken, Action<int>?> ApplyTransaction = null!;
    public Func<TInstance, string> VersionPath = null!, GameFolder = null!, VanillaName = null!;
    public Func<TInstance, IEnumerable<string>> LibraryPaths = null!, ModLoaderVersions = null!;
    public Func<TInstance, JObject> JsonObject = null!;
    public Func<TInstance, bool> AutoCleanup = null!;
    public Func<TInstance, int> Retention = null!;
    public Action<string> Info = null!, OpenFolder = null!, Hint = null!;
    public Action<Exception, string> Warn = null!;
}

public sealed class EnvironmentHistoryChoice {
    public string Text = null!;
    public string? ToolTip;
}

public sealed class EnvironmentHistoryWork {
    public string StepName = null!, Title = null!;
    public Action<CancellationToken, Action<string?>> Execute = null!;
    public Action Stopped = null!;
}

/// <summary>真实 Loader、版本锁、确认选择与实例缓存仍由原 LF 平台适配。</summary>
public sealed class EnvironmentHistoryWorkflowHost<TInstance> where TInstance : class {
    public Func<bool> HasRunningGame = null!, HasRunningSessions = null!, LaunchLoading = null!, Updating = null!, AnyLocked = null!, UpdatingModFolders = null!;
    public Func<TInstance, string> VersionPath = null!, GameFolder = null!, Name = null!;
    public Func<string, bool> HasPending = null!;
    public Func<TInstance, TInstance> RecoverInstance = null!;
    public Func<string, string, bool> SamePath = null!;
    public Func<string, string, string, string, bool, int> Confirm = null!;
    public Func<IEnumerable<EnvironmentHistoryChoice>, int?> SelectPoint = null!;
    public Action<string> HintError = null!;
    public Action<Action> Dispatch = null!, DispatchWait = null!;
    public Action NotifyChanged = null!;
    public Action<TInstance> ResetSettingsCache = null!;
    public Func<string, TInstance> CreateLoadedInstance = null!;
    public Action<string> ClearIniCache = null!;
    public Func<TInstance?> PageInstance = null!, SelectedInstance = null!;
    public Action<TInstance> SetPageInstance = null!, SetSelectedInstance = null!;
    public Action RefreshPageName = null!;
    public Func<TInstance, EnvironmentHistoryWork, bool> StartWork = null!;
}

public sealed class RecentEnvironmentHost<TInstance> where TInstance : class {
    public Func<string, string> NormalizeRoot = null!, RelativePath = null!;
    public Action<string> EnsureNoLinks = null!;
    public Action<string, string> EnsureWithin = null!;
    public Func<string, string, string> Target = null!;
    public Func<string, CancellationToken, string> Digest = null!;
    public Func<string?, string, bool> SamePath = null!;
    public Func<TInstance, string> VersionPath = null!, GameRootForInstance = null!;
}
