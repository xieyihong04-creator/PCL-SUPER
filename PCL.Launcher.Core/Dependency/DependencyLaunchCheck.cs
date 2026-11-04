namespace PCL;

/// <summary>
///     启动前的模组依赖判定入口。检查在后台完成，确认与自动补全在 UI 线程执行；
///     只有用户确认或本次运行已确认过同一状态时才继续启动。
/// </summary>
internal static class DependencyLaunchCheck
{
    private static readonly Living.Features.DependencyLaunchGate Gate = new();

    [ThreadStatic] private static bool _bypass;

    private static int _running;

    internal static void SettingsChanged() => Gate.SettingsChanged();

    /// <summary>
    ///     返回 true 表示可以立即继续启动；false 表示检查或补全仍在进行，
    ///     完成且用户确认后会通过 <paramref name="launch" /> 重新进入启动流程。
    /// </summary>
    internal static bool TryBegin(McInstance? instance, Action launch)
    {
        if (_bypass)
        {
            _bypass = false;
            return true;
        }

        if (instance is null || !instance.Modable)
            return true;

        string folder;
        try
        {
            folder = CeDependencyProbe.ModsFolder(instance);
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[Deps] 无法确定模组目录，跳过依赖检查");
            return true;
        }

        if (!Directory.Exists(folder))
            return true;

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            LauncherCommon.Log("[Deps] 依赖检查尚未完成，忽略重复的启动请求");
            return false;
        }

        var started = false;
        try
        {
            Task.Run(() =>
            {
                DependencyAutoFillPlan plan;
                try
                {
                    plan = DependencyAutoFill.Build(instance, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LauncherCommon.Log(ex, "[Deps] 启动前依赖检查失败，继续启动");
                    LauncherCommon.RunInUi(() =>
                    {
                        Interlocked.Exchange(ref _running, 0);
                        Launch(launch);
                    });
                    return;
                }

                LauncherCommon.RunInUi(() =>
                {
                    try
                    {
                        Handle(folder, plan, launch);
                    }
                    catch (Exception ex)
                    {
                        LauncherCommon.Log(ex, "[Deps] 展示依赖检查结果失败，继续启动");
                        Interlocked.Exchange(ref _running, 0);
                        Launch(launch);
                    }
                });
            });
            started = true;
        }
        finally
        {
            if (!started)
                Interlocked.Exchange(ref _running, 0);
        }

        return false;
    }

    private static void Handle(string folder, DependencyAutoFillPlan plan, Action launch)
    {
        if (CompUi.Current is null)
        {
            Interlocked.Exchange(ref _running, 0);
            Launch(launch);
            return;
        }

        var hasErrors = plan.Remaining.Issues.Any(issue => issue.IsError);
        if (!plan.HasWork && !hasErrors)
        {
            Gate.Accept(folder, plan.View);
            Interlocked.Exchange(ref _running, 0);
            Launch(launch);
            return;
        }

        if (!plan.HasWork && Gate.WasAccepted(folder, plan.View))
        {
            Interlocked.Exchange(ref _running, 0);
            Launch(launch);
            return;
        }

        var choice = CompUi.Current.ShowDialog(
            plan.Describe(),
            "模组依赖检查",
            plan.ActionText,
            "取消",
            "",
            hasErrors);
        if (choice != 1)
        {
            LauncherCommon.Log("[Deps] 用户取消启动");
            Interlocked.Exchange(ref _running, 0);
            return;
        }

        if (!plan.HasWork)
        {
            Gate.Accept(folder, plan.View);
            Interlocked.Exchange(ref _running, 0);
            Launch(launch);
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var count = DependencyAutoFill.Apply(plan, CancellationToken.None);
                LauncherCommon.Log($"[Deps] 已补全 {count} 个模组文件");
                LauncherCommon.RunInUi(() =>
                {
                    Gate.Accept(folder, plan.View);
                    Interlocked.Exchange(ref _running, 0);
                    Launch(launch);
                });
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, "[Deps] 自动补全失败，游戏没有启动");
                LauncherCommon.RunInUi(() =>
                {
                    Interlocked.Exchange(ref _running, 0);
                    CompUi.Current?.ShowDialog(
                        "自动补全未完成，游戏没有启动。" + Environment.NewLine + Environment.NewLine + ex.Message,
                        "模组依赖检查",
                        "确定",
                        isWarn: true);
                });
            }
        });
    }

    private static void Launch(Action launch)
    {
        _bypass = true;
        try
        {
            launch();
        }
        finally
        {
            _bypass = false;
        }
    }
}
