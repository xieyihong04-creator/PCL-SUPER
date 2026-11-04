using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PCL.Living.Features;

/// <summary>Existing LF watcher state; no launcher model is copied into the backup engine.</summary>
public enum WorldBackupWatcherState { Loading, Running, Crashed, Ended, Canceled }

public sealed class WorldBackupWatcher<TInstance> where TInstance : class {
    public Process? Process;
    public TInstance? Instance;
    public WorldBackupWatcherState State;
}

/// <summary>Typed adapters for LF models, profile paths and UI services.</summary>
public sealed class WorldBackupHost<TInstance> where TInstance : class {
    public Func<string, string> RemoveExtendedPrefix = null!;
    public Func<string, string> ProfileAddress = null!;
    public Func<TInstance, string> GameFolder = null!;
    public Func<TInstance, string> VersionPath = null!;
    public Func<TInstance, int> Retention = null!;
    public Func<TInstance, bool> AutoEnabled = null!;
    public Func<IReadOnlyList<WorldBackupWatcher<TInstance>?>> Watchers = null!;
    public Func<string, string?> WorldLabel = null!;
    public Func<long, string> FormatSize = null!;
    public Action<Exception, string> Warn = null!;
    public Func<bool> UiBusy = null!;
    public Action<Action> Dispatch = null!;
    public Action NotifyChanged = null!;
    public Action<string> OpenFolder = null!;
    public Action<string, bool> Hint = null!;
}

/// <summary>One actual loader workload; LF owns its worker, file lock and taskbar lifetime.</summary>
public sealed class WorldBackupWork {
    public string StepName = null!;
    public string Title = null!;
    public Action<CancellationToken, Action<string?>> Execute = null!;
    public Action Stopped = null!;
}

public sealed class WorldBackupWorkflowHost<TInstance> where TInstance : class {
    public Func<TInstance, string> GameFolder = null!;
    public Func<TInstance, string> VersionPath = null!;
    public Func<TInstance, string> InstanceName = null!;
    public Func<TInstance, int> Retention = null!;
    public Func<bool> LauncherBusy = null!;
    public Func<string, string, bool> SamePath = null!;
    public Func<string, string, string, int> Confirm = null!;
    public Action<string, bool> Hint = null!;
    public Action<Action> Dispatch = null!;
    public Func<TInstance, WorldBackupWork, bool> StartWork = null!;
}
