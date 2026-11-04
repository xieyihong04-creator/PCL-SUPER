using PCL.Living.Features;
using Inspector = PCL.Living.Features.ModDependencyInspection<
    PCL.CeDependencyInspection.Context, PCL.CeDependencyInspection.Mod, PCL.CeDependencyInspection.Dependency,
    PCL.CeDependencyInspection.Edge, PCL.CeDependencyInspection.Issue, PCL.CeDependencyInspection.Impact,
    PCL.CeDependencyInspection.Report, PCL.CeDependencyInspection.OnlineProject,
    PCL.CeDependencyInspection.OnlineDependency, PCL.CeDependencyInspection.OnlineFile>;

namespace PCL;

public sealed record ModDependencyGraphNode(
    string Key,
    string Id,
    string Name,
    string FileName,
    bool Enabled,
    bool IsBuiltin,
    bool IsCandidate);

public sealed record ModDependencyGraphEdge(
    string FromKey,
    string ToKey,
    string Requirement,
    string State);

/// <summary>依赖关系图的数据：只包含必需依赖，其余在提示中说明。</summary>
public sealed record ModDependencyGraph(
    IReadOnlyList<ModDependencyGraphNode> Nodes,
    IReadOnlyList<ModDependencyGraphEdge> Edges,
    string Summary);

/// <summary>只读的模组依赖关系数据入口，供界面绘制关系图。</summary>
public static class ModDependencyGraphService
{
    public static ModDependencyGraph Build(McInstance instance, CancellationToken token)
    {
        var context = CeDependencyProbe.ContextFor(
            instance,
            GameLaunch.mcLaunchJavaSelected?.Installation.Version.ToString());
        var report = Inspector.Inspect(CeDependencyProbe.ModsFolder(instance), context, token);

        var nodes = report.Mods
            .Where(mod => !mod.IsNested)
            .Select(mod => new ModDependencyGraphNode(
                mod.Key,
                mod.Id,
                string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name,
                Path.GetFileName(mod.RootPath),
                mod.Enabled,
                mod.IsBuiltin,
                mod.IsCandidate))
            .OrderBy(node => node.IsBuiltin)
            .ThenBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var known = new HashSet<string>(nodes.Select(node => node.Key), StringComparer.Ordinal);
        var edges = new List<ModDependencyGraphEdge>();
        foreach (var edge in report.Edges)
        {
            token.ThrowIfCancellationRequested();
            if (!edge.Evaluated || edge.ConditionBypassed) continue;
            if (edge.Requirement.KindValue != ModDependencyKind.Required) continue;
            if (!known.Contains(edge.Owner.Key)) continue;
            foreach (var provider in edge.Providers)
            {
                if (!known.Contains(provider.Key)) continue;
                edges.Add(new ModDependencyGraphEdge(edge.Owner.Key, provider.Key,
                    edge.Requirement.Display, edge.StateValue.ToString()));
            }
        }

        var summary = "共 " + nodes.Count(node => !node.IsBuiltin) + " 个模组，必需依赖 " + edges.Count +
                      " 条，其中未满足 " +
                      edges.Count(edge => edge.State != nameof(ModDependencyMatchState.Satisfied)) + " 条。";
        return new ModDependencyGraph(nodes, edges, summary);
    }
}
