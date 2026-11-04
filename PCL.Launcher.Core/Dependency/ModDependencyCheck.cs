namespace PCL;

/// <summary>资源页“检查模组”的只读结果。</summary>
public sealed record ModDependencyCheckResult(string Text, bool HasErrors, bool HasWork);

/// <summary>
///     只读的模组依赖检查入口（供界面调用）。不修改任何文件、不下载任何内容；
///     真正的补全只在启动流程中由玩家确认后执行。
/// </summary>
public static class ModDependencyCheck
{
    public static ModDependencyCheckResult Run(McInstance instance, CancellationToken token)
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));
        var plan = DependencyAutoFill.Build(instance, token);
        return new ModDependencyCheckResult(plan.DescribeCheck(), plan.HasErrors, plan.HasWork);
    }
}
