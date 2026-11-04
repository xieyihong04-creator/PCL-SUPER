using PCL.Core.App;

namespace PCL;

/// <summary>按实例保存的“整合包模式”标记：资源由整合包统一管理。</summary>
internal static class InstanceModpackMode
{
    internal static bool IsEnabled(McInstance? instance) =>
        instance is not null && States.Instance.ModpackMode[instance.PathInstance];

    internal static void Set(McInstance instance, bool enabled) =>
        States.Instance.ModpackMode[instance.PathInstance] = enabled;
}
