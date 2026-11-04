using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCL.Living.Features;

/// <summary>LF 克隆与迁回命令，原工作线程、双版本锁与废弃 Worker 由平台真实适配。</summary>
public sealed class InstanceCloneWorkflow<TInstance, TOptions, TResult, TChange, TPlan>
        where TInstance : class where TOptions : InstanceCloneOptionsData where TResult : InstanceCloneResultData
        where TChange : InstanceCloneMigrationChangeData where TPlan : InstanceCloneMigrationPlanData<TChange> {
    private readonly InstanceCloneEngine<TInstance, TOptions, TResult, TChange, TPlan> engine;
    private readonly InstanceCloneWorkflowHost<TInstance> host;
    public int BusyCount;
    public bool IsBusy => Volatile.Read(ref BusyCount) != 0;
    public Action<TInstance, string, string, CancellationToken>? BeforeMinecraftChangeHandler;
    public InstanceCloneWorkflow(InstanceCloneEngine<TInstance, TOptions, TResult, TChange, TPlan> engine, InstanceCloneWorkflowHost<TInstance> host) { this.engine = engine; this.host = host; }
    public bool CanStart(TInstance? instance) {
        if (instance is null || host.HasRunningGame() || host.HasRunningSessions() || host.LaunchLoading() || host.WorldBackupBusy() || host.HasActiveGame(engine.GameRootForInstance(instance)) || host.EnvironmentBusy() || host.Updating() || host.AnyLocked() || host.UpdatingModFolders()) {
            host.Hint("请先关闭游戏并等待版本任务完成。", false); return false;
        }
        return Interlocked.CompareExchange(ref BusyCount, 1, 0) == 0;
    }
    private InstanceCloneWork Work(string stepName, string title, bool lockSource, Action<CancellationToken, Action<string?>> execute) => new() {
        StepName = stepName, Title = title, LockSource = lockSource, Execute = execute,
        ReleaseBusy = () => Interlocked.Exchange(ref BusyCount, 0),
        SourceError = error => host.Hint("无法读取测试副本来源：" + error.Message, false),
        Stopped = () => {
            Interlocked.Exchange(ref BusyCount, 0);
            host.Dispatch(() => { host.RefreshInstances(); host.NotifyChanged(); });
        }
    };
    public void Start(TInstance? instance, string newName, TOptions? options) {
        if (!CanStart(instance)) return;
        InstanceCloneWork work = Work("复制为独立测试版本", "克隆版本：" + host.Name(instance!), false, (token, setOutput) => {
            TResult result = engine.CloneInstance(instance!, newName, options, token);
            setOutput(result.VersionFolder);
            host.Dispatch(() => host.Hint("已创建测试副本：" + result.Name, true));
        });
        host.StartWork(instance!, work);
    }
    public void Migrate(TInstance? cloneInstance) {
        if (!CanStart(cloneInstance)) return;
        InstanceCloneWork work = Work("预览并迁移测试环境", "迁移测试版本：" + host.Name(cloneInstance!), true, (token, setOutput) => {
            TInstance source = host.CreateInstance(engine.SourceVersionFolder(host.VersionPath(cloneInstance!)));
            TPlan plan = engine.PreviewMigration(host.VersionPath(cloneInstance!), engine.GameRootForInstance(source), token);
            if (plan.Changes.Count == 0) throw new IOException("测试副本与原环境没有可迁移的变化。");
            string preview = string.Join("\r\n", plan.Changes.Take(30).Select(item => item.Kind + " · " + item.Relative!.Substring(2)));
            if (plan.Changes.Count > 30) preview += "\r\n" + $"另有 {plan.Changes.Count - 30} 项变更。";
            if (host.Confirm("将测试环境迁移到原版本：" + Path.GetFileName(plan.SourceVersionFolder) + "\r\n" + $"Minecraft：{plan.OldMinecraft} → {plan.NewMinecraft}" + "\r\n\r\n" + preview + "\r\n\r\n迁移前会保存原环境还原点；存档和截图保留原内容。", "迁移测试环境", "保存还原点并迁移", "取消", true) != 1) throw new OperationCanceledException();
            Action<string, string>? before = null;
            if (BeforeMinecraftChangeHandler is not null) before = (oldVersion, newVersion) => BeforeMinecraftChangeHandler!(source, oldVersion, newVersion, token);
            setOutput(engine.ApplyMigration(plan, token, () => host.CaptureInstance(source, "测试副本迁移前", token), before));
            host.Dispatch(() => { host.ResetSettingsCache(source); host.Hint("已迁移测试环境；原环境可从还原点恢复。", true); });
        });
        host.StartWork(cloneInstance!, work);
    }
}
