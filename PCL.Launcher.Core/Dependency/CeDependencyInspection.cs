using PCL.Living.Features;
using Inspector = PCL.Living.Features.ModDependencyInspection<
    PCL.CeDependencyInspection.Context,
    PCL.CeDependencyInspection.Mod,
    PCL.CeDependencyInspection.Dependency,
    PCL.CeDependencyInspection.Edge,
    PCL.CeDependencyInspection.Issue,
    PCL.CeDependencyInspection.Impact,
    PCL.CeDependencyInspection.Report,
    PCL.CeDependencyInspection.OnlineProject,
    PCL.CeDependencyInspection.OnlineDependency,
    PCL.CeDependencyInspection.OnlineFile>;

namespace PCL;

/// <summary>
///     把 LF 的本地模组依赖检查器绑定到 CE 的类型系统（甲：不移植 LF 的 ResourceVersion 模型）。
///     检查器本身只读本地声明，下载与启用仍由 CE 的 Comp 管线和本地文件操作完成。
/// </summary>
internal static class CeDependencyInspection
{
    internal sealed class Context : Inspector.ContextData
    {
    }

    internal sealed class Mod : Inspector.ModData
    {
    }

    internal sealed class Dependency : Inspector.DependencyData
    {
        public override ModDependencyKind KindValue { get; set; }
    }

    internal sealed class Edge : Inspector.EdgeData
    {
        public override ModDependencyMatchState StateValue { get; set; }
    }

    internal sealed class Issue : Inspector.IssueData
    {
        public override ModDependencyIssueKind KindValue { get; set; }
    }

    internal sealed class Impact : Inspector.ImpactData
    {
    }

    internal sealed class Report : Inspector.ReportData
    {
    }

    internal sealed class OnlineProject : Inspector.OnlineProjectData
    {
    }

    internal sealed class OnlineDependency : Inspector.OnlineDependencyData
    {
    }

    internal sealed class OnlineFile : Inspector.OnlineFileData
    {
    }
}
