using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

public class InstanceCloneOptionsData { public bool CopySaves, CopyScreenshots, CopyServers; }
public class InstanceCloneResultData { public string? Name, VersionFolder; public int FileCount; public long Bytes; }
public class InstanceCloneMigrationChangeData { public string? Relative, Kind; }
public class InstanceCloneMigrationPlanData<TChange> where TChange : InstanceCloneMigrationChangeData {
    public string? SourceVersionFolder, SourceGameRoot, CloneVersionFolder, OldMinecraft, NewMinecraft;
    public List<TChange> Changes = new();
    public Dictionary<string, string>? SourceFiles, CloneFiles;
    public string? OriginHash;
}

public sealed class InstanceCloneHost<TInstance> where TInstance : class {
    public Func<string, string> RemoveExtendedPrefix = null!, RelativePath = null!, SafeName = null!, AddSlashSuffix = null!;
    public Action<string, string> EnsureWithin = null!;
    public Func<string, string, bool> SamePath = null!;
    public Func<bool> HasRunningGame = null!, HasRunningSessions = null!, LaunchLoading = null!, WorldBackupBusy = null!;
    public Func<string, bool> HasActiveGame = null!, HasPending = null!;
    public Func<TInstance, string> VersionPath = null!, VanillaName = null!;
    public Func<TInstance, bool> IsFool = null!, IsSnapshot = null!, HasModLoaders = null!, ApiType = null!;
    public Func<int> IsolationMode = null!;
    public Func<string, string, string, CancellationToken, string> CaptureEnvironment = null!;
    public Action<string, string, List<Tuple<string, string?, string?>>, CancellationToken, Action<int>?> ApplyTransaction = null!;
}

public sealed class InstanceCloneWork {
    public string StepName = null!, Title = null!;
    public bool LockSource;
    public Action<CancellationToken, Action<string?>> Execute = null!;
    public Action ReleaseBusy = null!, Stopped = null!;
    public Action<Exception> SourceError = null!;
}
public sealed class InstanceCloneWorkflowHost<TInstance> where TInstance : class {
    public Func<TInstance, string> VersionPath = null!, Name = null!;
    public Func<string, TInstance> CreateInstance = null!;
    public Func<bool> HasRunningGame = null!, HasRunningSessions = null!, LaunchLoading = null!, WorldBackupBusy = null!, EnvironmentBusy = null!, Updating = null!, AnyLocked = null!, UpdatingModFolders = null!;
    public Func<string, bool> HasActiveGame = null!;
    public Action<string, bool> Hint = null!;
    public Func<string, string, string, string, bool, int> Confirm = null!;
    public Action<Action> Dispatch = null!;
    public Action<TInstance> ResetSettingsCache = null!;
    public Func<TInstance, string, CancellationToken, string> CaptureInstance = null!;
    public Action RefreshInstances = null!, NotifyChanged = null!;
    public Action<TInstance, InstanceCloneWork> StartWork = null!;
}

public class InstanceCloneGroupNodeData<TNode> where TNode : InstanceCloneGroupNodeData<TNode> {
    public string? VersionFolder, DeclaredSourceFolder, ParentFolder;
    public List<TNode> Children = new();
}
public class InstanceCloneGroupSetData<TNode> where TNode : InstanceCloneGroupNodeData<TNode> {
    public List<TNode> Roots = new();
    public Dictionary<string, TNode> ByPath = new(StringComparer.OrdinalIgnoreCase);
}
