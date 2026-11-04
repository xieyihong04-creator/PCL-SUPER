using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using static PCL.Living.Features.ModDependencyVersions;

namespace PCL.Living.Features;

/// <summary>Only the original live instance reads cross this boundary; local declarations stay in the inspector.</summary>
public sealed class ModDependencyInstanceHost<TInstance> where TInstance : class {
    public Func<TInstance, bool> Reliable = null!, HasOptiFine = null!, OptiFineAsMod = null!;
    public Func<TInstance, string?> VanillaName = null!, OptiFine = null!;
    public Func<TInstance, IEnumerable<KeyValuePair<string, string?>>> ModLoaderVersions = null!;
    public Func<TInstance, JObject?> JsonObject = null!;
}

public sealed partial class ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile> {
    public static TContext ContextForInstance<TInstance>(TInstance instance, ModDependencyInstanceHost<TInstance> host, string? javaVersion = null) where TInstance : class {
        if (instance is null) throw new ArgumentNullException("Instance");
        TContext result = new() { MinecraftVersion = host.Reliable(instance) ? host.VanillaName(instance) : null, JavaVersion = javaVersion, LoaderKnown = true };
        foreach (KeyValuePair<string, string?> pair in host.ModLoaderVersions(instance)) result.LoaderVersions[CanonicalLoaderId(pair.Key)] = pair.Value;
        JArray? libraries = host.JsonObject(instance)?["libraries"] as JArray;
        if (libraries is not null) {
            foreach (JObject library in libraries.OfType<JObject>()) {
                string coordinate = Text(library["name"]);
                if (!coordinate.StartsWith("org.quiltmc:quilt-loader:", StringComparison.Ordinal)) continue;
                result.LoaderVersions.Remove("fabricloader");
                result.LoaderVersions["quilt_loader"] = coordinate.Substring("org.quiltmc:quilt-loader:".Length).Split(':')[0];
                result.LoaderVersions["fabricloader"] = null;
            }
        }
        if (result.LoaderVersions.TryGetValue("forge", out _) && Regex.IsMatch(result.MinecraftVersion ?? "", @"^1\.(?:[0-9]|1[0-2])(?:\.|$)")) result.LegacyForgeVersionFolder = result.MinecraftVersion;
        if (result.LoaderVersions.ContainsKey("neoforge") && result.MinecraftVersion == "1.20.1") result.LoaderVersions["forge"] = result.LoaderVersions["neoforge"];
        if (host.HasOptiFine(instance) && !host.OptiFineAsMod(instance)) result.AdditionalBuiltins["optifine"] = host.OptiFine(instance);
        return result;
    }
    // Comparison keys alone lose the extended spelling. Real file reads retain their original paths.
    public static string FilePathKey(string fullPath) {
        string value = Path.GetFullPath(fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) value = @"\\" + value.Substring(8);
        else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) && value.Length >= 7 && char.IsLetter(value[4]) && value[5] == ':' && value[6] == '\\') value = value.Substring(4);
        return value;
    }
    public static bool SameFilePath(string first, string second) => string.Equals(FilePathKey(first), FilePathKey(second), StringComparison.OrdinalIgnoreCase);
    private static string CanonicalId(string? id) => id ?? "";
    private static string CanonicalLoaderId(string? id) => (id ?? "").ToLowerInvariant() switch {
        "fabric" or "fabric-loader" or "fabric_loader" or "fabricloader" => "fabricloader",
        "quilt" or "quilt-loader" or "quiltloader" or "quilt_loader" => "quilt_loader",
        "liteloader" => "liteloader", "neoforge" or "forge" or "minecraft" or "java" => id!.ToLowerInvariant(), _ => id ?? ""
    };
    private static List<TMod> CreateBuiltins(TContext context) {
        Dictionary<string, string?> values = new(StringComparer.Ordinal) { ["minecraft"] = context.MinecraftVersion, ["java"] = NormalizeJava(context.JavaVersion) };
        if (!context.LoaderKnown && context.LoaderVersions.Count == 0)
            foreach (string id in new[] { "forge", "neoforge", "fabricloader", "quilt_loader", "liteloader" }) values[id] = null;
        foreach (KeyValuePair<string, string?> pair in context.LoaderVersions) values[CanonicalLoaderId(pair.Key)] = pair.Value;
        if (values.ContainsKey("forge") || values.ContainsKey("neoforge")) {
            values["fml"] = null; values["javafml"] = null; values["lowcodefml"] = null;
            if (values.TryGetValue("forge", out string? forge) && Regex.IsMatch(forge ?? "", @"^\d+(?:\.\d+)*$")) {
                values["javafml"] = forge!.Split('.')[0]; values["lowcodefml"] = forge.Split('.')[0];
            }
        }
        foreach (KeyValuePair<string, string?> pair in context.AdditionalBuiltins) values[CanonicalId(pair.Key)] = pair.Value;
        return values.Select(pair => new TMod { Id = pair.Key, Name = pair.Key, Version = pair.Value, IsBuiltin = true, Enabled = true }).ToList();
    }
    private static string? NormalizeJava(string? value) {
        if (string.IsNullOrEmpty(value)) return value;
        if (value!.StartsWith("1.", StringComparison.Ordinal)) value = value.Substring(2);
        return Regex.Replace(value, @"^(\d+(?:\.\d+)*)_(\d+)$", "$1.$2");
    }
    public static Dictionary<string, string?> Identities(TMod item) {
        Dictionary<string, string?> result = new(item.Provides, StringComparer.Ordinal) { [item.Id] = item.Version };
        if (!string.IsNullOrEmpty(item.Group)) result[item.Group + ":" + item.Id] = item.Version;
        return result;
    }
    private static List<TMod> ActiveMods(TReport report) => report.Mods.Where(item => item.Enabled && SideApplies(item.Side, report.Context)).ToList();
    public static bool FileSatisfiesDependency(TReport report, string fullPath, TDependency requirement) {
        IEnumerable<TMod> modules = report.Mods.Where(item => SameFilePath(item.RootPath, fullPath) && SideApplies(item.Side, report.Context));
        return Resolve(requirement, ProviderMap(modules), report.Context).State == ModDependencyMatchState.Satisfied;
    }
    public static TReport WithEnabledFiles(TReport report, IEnumerable<string> paths, CancellationToken token) {
        HashSet<string> enabled = new(paths.Select(FilePathKey), StringComparer.OrdinalIgnoreCase);
        TReport result = new() { Context = report.Context, Builtins = report.Builtins, MetadataIncomplete = report.MetadataIncomplete };
        result.Notices.AddRange(report.Notices);
        result.Mods.AddRange(report.Mods.Select(item => item.CopyWithEnabled(item.Enabled || enabled.Contains(FilePathKey(item.RootPath)))));
        BuildReport(result, token);
        return result;
    }
    public static bool IsRequiredFileProvider(TReport report, TEdge edge, string fullPath) => FileProviderRelation(report, edge, fullPath) == ModDependencyFileProviderKind.Required;
    public static ModDependencyFileProviderKind FileProviderRelation(TReport report, TEdge edge, string fullPath) {
        if (!edge.Evaluated || edge.ConditionBypassed || SameFilePath(edge.Owner.RootPath, fullPath)) return ModDependencyFileProviderKind.None;
        List<TMod> providers = edge.Providers.Where(item => !item.IsBuiltin).ToList();
        if (!providers.Any(item => SameFilePath(item.RootPath, fullPath)) || FileSatisfiesDependency(report, edge.Owner.RootPath, edge.Requirement)) return ModDependencyFileProviderKind.None;
        if (providers.All(item => SameFilePath(item.RootPath, fullPath))) return ModDependencyFileProviderKind.Required;
        IEnumerable<TMod> withoutFile = ActiveMods(report).Where(item => !SameFilePath(item.RootPath, fullPath)).Concat(report.Builtins);
        if (edge.StateValue == ModDependencyMatchState.Satisfied && Resolve(edge.Requirement, ProviderMap(withoutFile), report.Context).State == ModDependencyMatchState.Unsatisfied) return ModDependencyFileProviderKind.Required;
        if (edge.Requirement.Children.Count == 0 && edge.StateValue == ModDependencyMatchState.Satisfied && Resolve(edge.Requirement,
            ProviderMap(providers.Where(item => SameFilePath(item.RootPath, fullPath))), report.Context).State == ModDependencyMatchState.Unsatisfied) return ModDependencyFileProviderKind.None;
        return ModDependencyFileProviderKind.Possible;
    }
    private static Dictionary<string, List<TMod>> ProviderMap(IEnumerable<TMod> mods) {
        Dictionary<string, List<TMod>> result = new(StringComparer.Ordinal);
        foreach (TMod item in mods) {
            foreach (string id in Identities(item).Keys) {
                if (!result.ContainsKey(id)) result[id] = new List<TMod>();
                result[id].Add(item);
            }
        }
        foreach (string id in result.Keys.ToArray()) {
            List<TMod> direct = result[id].Where(item => !item.IsNested).ToList();
            if (direct.Count > 0) result[id] = direct;
        }
        return result;
    }
    private sealed class Resolution {
        public ModDependencyMatchState State;
        public bool Missing;
        public List<TMod> Providers = new();
    }
    private static Resolution Resolve(TDependency requirement, Dictionary<string, List<TMod>> providers, TContext context, int depth = 0) {
        if (depth > 32 || requirement.IsUnknown) return new Resolution { State = ModDependencyMatchState.Unknown };
        if (!SideApplies(requirement.Side, context)) return new Resolution { State = ModDependencyMatchState.Satisfied };
        Resolution result = new();
        if (requirement.Children.Count > 0) {
            List<Resolution> children = requirement.Children.Select(child => Resolve(child, providers, context, checked(depth + 1))).ToList();
            result.State = Combine(children.Select(child => child.State), requirement.Join == "all");
            result.Missing = children.All(child => child.Missing);
            result.Providers = children.SelectMany(child => child.Providers).Distinct().ToList();
        } else {
            if (!providers.TryGetValue(requirement.Id ?? "", out List<TMod>? candidates) || candidates.Count == 0) {
                result.State = ModDependencyMatchState.Unsatisfied; result.Missing = true;
            } else {
                result.Providers = candidates.ToList();
                result.State = Combine(candidates.Select(item => MatchVersions(Identities(item)[requirement.Id!], requirement.Versions, requirement.Syntax)), false);
            }
        }
        if (requirement.Unless is not null) {
            Resolution unlessResult = Resolve(requirement.Unless, providers, context, checked(depth + 1));
            bool negative = requirement.KindValue is ModDependencyKind.Incompatible or ModDependencyKind.Conflict;
            if (unlessResult.State == ModDependencyMatchState.Satisfied) {
                result.State = negative ? ModDependencyMatchState.Unsatisfied : ModDependencyMatchState.Satisfied;
                result.Missing = false;
                result.Providers = result.Providers.Concat(unlessResult.Providers).Distinct().ToList();
            } else if (unlessResult.State == ModDependencyMatchState.Unknown && ((negative && result.State == ModDependencyMatchState.Satisfied) || (!negative && result.State == ModDependencyMatchState.Unsatisfied))) result.State = ModDependencyMatchState.Unknown;
        }
        if (requirement.KindValue == ModDependencyKind.Optional && result.Missing) result.State = ModDependencyMatchState.Satisfied;
        return result;
    }
    public static void BuildReport(TReport report, CancellationToken token) {
        List<TMod> active = ActiveMods(report);
        Dictionary<string, List<TMod>> map = ProviderMap(active.Concat(report.Builtins));
        foreach (IGrouping<string, TMod> group in active.Where(item => !item.IsNested).GroupBy(item => item.Id, StringComparer.Ordinal).Where(items => items.Count() > 1)) {
            report.Issues.Add(new TIssue { KindValue = ModDependencyIssueKind.DuplicateId, Owner = group.First(), RelatedMods = group.ToList(), IsError = true,
                Message = "重复模组 ID " + group.Key + "：" + string.Join("、", group.Select(item => Path.GetFileName(item.RootPath))) });
        }
        HashSet<string> requiredKeys = new(active.Where(item => !item.IsNested).Select(item => item.Key), StringComparer.Ordinal);
        bool changed = true;
        while (changed) {
            changed = false;
            foreach (TMod item in active.Where(entry => requiredKeys.Contains(entry.Key))) {
                token.ThrowIfCancellationRequested();
                foreach (TDependency requirement in item.Dependencies.Where(entry => entry.KindValue == ModDependencyKind.Required && SideApplies(entry.Side, report.Context))) {
                    Resolution value = Resolve(requirement, map, report.Context);
                    List<TMod> candidates = value.Providers.Where(entry => entry.IsNested).ToList();
                    if (value.Providers.Count == 1 && candidates.Count == 1 && requiredKeys.Add(candidates[0].Key)) changed = true;
                }
            }
        }
        foreach (TMod item in active) item.IsCandidate = item.IsNested && !requiredKeys.Contains(item.Key);
        foreach (TMod item in active) {
            token.ThrowIfCancellationRequested();
            foreach (TDependency requirement in item.Dependencies) {
                Resolution value = Resolve(requirement, map, report.Context);
                TEdge edge = new() { Owner = item, Requirement = requirement, StateValue = value.State, Providers = value.Providers,
                    Evaluated = requiredKeys.Contains(item.Key) && SideApplies(requirement.Side, report.Context),
                    ConditionBypassed = requirement.Unless is not null && Resolve(requirement.Unless, map, report.Context).State == ModDependencyMatchState.Satisfied };
                report.Edges.Add(edge);
                foreach (TMod provider in value.Providers) {
                    if (!report.ReverseDependencies.ContainsKey(provider.Key)) report.ReverseDependencies[provider.Key] = new List<TEdge>();
                    report.ReverseDependencies[provider.Key].Add(edge);
                }
                if (edge.Evaluated) AddIssue(report, edge, value);
            }
        }
        if (active.Any(item => item.IsCandidate)) report.Notices.Add("内嵌模组的候选版本由加载器选择；未把未选中的候选自身依赖判为启动错误。");
    }
    private static void AddIssue(TReport report, TEdge edge, Resolution value) {
        TDependency requirement = edge.Requirement;
        ModDependencyIssueKind kind;
        bool isError = false;
        string detail;
        switch (requirement.KindValue) {
            case ModDependencyKind.Required:
            case ModDependencyKind.Optional:
                if (value.State != ModDependencyMatchState.Unsatisfied) return;
                if (requirement.KindValue == ModDependencyKind.Optional && !requirement.EnforceWhenPresent) return;
                kind = value.Missing ? ModDependencyIssueKind.MissingRequired : ModDependencyIssueKind.VersionMismatch;
                isError = !report.MetadataIncomplete || (!value.Missing && value.Providers.All(item => !item.IsNested));
                detail = (value.Missing ? "缺少必需依赖：" : "版本不匹配：") + edge.Owner.Display + " 需要 " + requirement.Display;
                break;
            case ModDependencyKind.Incompatible:
            case ModDependencyKind.Conflict:
                if (value.State != ModDependencyMatchState.Satisfied || value.Missing || !SideApplies(requirement.Side, report.Context)) return;
                kind = ModDependencyIssueKind.Incompatible;
                isError = requirement.KindValue == ModDependencyKind.Incompatible && value.Providers.All(item => !item.IsCandidate);
                detail = (value.Providers.Any(item => item.IsCandidate) ? "内嵌候选可能不兼容：" : "已声明不兼容：") + edge.Owner.Display + " 与 " + requirement.Display;
                break;
            case ModDependencyKind.Recommended:
                if (value.State != ModDependencyMatchState.Unsatisfied) return;
                kind = ModDependencyIssueKind.Recommendation;
                detail = "建议依赖未满足：" + edge.Owner.Display + " 建议安装 " + requirement.Display;
                break;
            default: return;
        }
        if (value.Providers.Count > 0) detail += "；本地：" + string.Join("、", value.Providers.Select(item => item.Display + " " + (string.IsNullOrEmpty(item.Version) ? "未知版本" : item.Version)));
        List<TMod> disabled = report.Mods.Where(item => !item.Enabled && DependencyMentions(requirement, Identities(item).Keys)).ToList();
        if (value.Missing && disabled.Count > 0) detail += "；已禁用：" + string.Join("、", disabled.Select(item => Path.GetFileName(item.RootPath)).Distinct());
        if (!string.IsNullOrEmpty(requirement.Reason)) detail += "（" + requirement.Reason + "）";
        report.Issues.Add(new TIssue { KindValue = kind, Owner = edge.Owner, Requirement = requirement, RelatedMods = value.Providers, IsError = isError, Message = detail });
    }
    public static List<TImpact> GetImpacts(TReport report, IEnumerable<string> removedPaths) {
        if (report is null) throw new ArgumentNullException("Report");
        HashSet<string> removed = new(removedPaths.Select(FilePathKey), StringComparer.OrdinalIgnoreCase);
        List<TMod> active = ActiveMods(report);
        Dictionary<string, List<TMod>> original = ProviderMap(active.Concat(report.Builtins));
        HashSet<string> unavailable = new(active.Where(item => removed.Contains(FilePathKey(item.RootPath))).Select(item => item.Key), StringComparer.Ordinal);
        List<TImpact> result = new();
        bool changed = true;
        int round = 0;
        while (changed) {
            changed = false;
            Dictionary<string, List<TMod>> map = ProviderMap(active.Where(item => !unavailable.Contains(item.Key)).Concat(report.Builtins));
            List<TImpact> additions = new();
            foreach (TMod item in active.Where(entry => !removed.Contains(FilePathKey(entry.RootPath)) && !unavailable.Contains(entry.Key) && !entry.IsCandidate)) {
                List<TDependency> failed = new();
                foreach (TDependency requirement in item.Dependencies.Where(entry => entry.KindValue == ModDependencyKind.Required && SideApplies(entry.Side, report.Context))) {
                    if (Resolve(requirement, original, report.Context).State == ModDependencyMatchState.Unsatisfied) continue;
                    if (Resolve(requirement, map, report.Context).State == ModDependencyMatchState.Unsatisfied) failed.Add(requirement);
                }
                if (failed.Count == 0) continue;
                additions.Add(new TImpact { Mod = item, Requirements = failed, IsDirect = round == 0,
                    IsDefinite = !report.MetadataIncomplete && failed.All(requirement => Resolve(requirement, original, report.Context).State == ModDependencyMatchState.Satisfied),
                    Reason = (round == 0 ? "将失去必需依赖：" : "间接受影响，必需依赖将无法加载：") + string.Join("；", failed.Select(requirement => requirement.Display)) });
            }
            foreach (TImpact impact in additions) { result.Add(impact); unavailable.Add(impact.Mod.Key); changed = true; }
            round = checked(round + 1);
        }
        return result;
    }
    public static string DescribeImpacts(TReport report, IEnumerable<string> removedPaths) {
        string[] paths = removedPaths.ToArray();
        List<TImpact> impacts = GetImpacts(report, paths);
        string online = report.DescribeOnlineImpacts(paths);
        if (impacts.Count == 0) return online;
        List<string> lines = new() { "以下模组依赖本次操作涉及的文件：" };
        foreach (TImpact impact in impacts.Take(30)) lines.Add("• " + impact.Mod.Display + "（" + Path.GetFileName(impact.Mod.RootPath) + "）" + (impact.IsDefinite ? "" : " [待确认]") + "\r\n  " + impact.Reason);
        if (impacts.Count > 30) lines.Add("另有 " + (impacts.Count - 30) + " 个模组受影响。");
        if (online != "") lines.Add("\r\n" + online);
        lines.Add("仍可继续操作；如需一起禁用或删除，请先返回模组管理选择这些文件。");
        return string.Join("\r\n", lines);
    }
    public static bool DependencyMentions(TDependency requirement, IEnumerable<string> ids) {
        HashSet<string> names = new(ids, StringComparer.Ordinal);
        if (names.Contains(requirement.Id ?? "")) return true;
        if (requirement.Unless is not null && DependencyMentions(requirement.Unless, names)) return true;
        return requirement.Children.Any(child => DependencyMentions(child, names));
    }
    private static string DescribeFile(TReport report, string fullPath) {
        string target = FilePathKey(fullPath);
        List<TMod> items = report.Mods.Where(item => SameFilePath(item.RootPath, target)).ToList();
        List<string> lines = new() { Path.GetFileName(target) };
        if (items.Count == 0) {
            lines.Add("未识别到支持的本地模组元数据，无法确定依赖关系。");
            lines.AddRange(report.Notices.Where(notice => notice.Contains(Path.GetFileName(target))).Take(4));
            string online = report.DescribeOnlineFile(target);
            if (online != "") lines.Add("\r\n" + online);
            return string.Join("\r\n", lines);
        }
        foreach (TMod item in items) {
            lines.Add("");
            lines.Add(item.Display + " · " + (string.IsNullOrEmpty(item.Version) ? "未知版本" : item.Version) + " · " + item.Format + (item.Enabled ? "" : " · 已禁用") + (item.IsNested ? " · 内嵌" : ""));
            if (item.Side != "*") lines.Add("运行环境：" + item.Side);
            lines.Add("需要的模组：");
            if (item.Dependencies.Count == 0) lines.Add("  元数据未声明依赖。");
            foreach (TDependency requirement in item.Dependencies.Take(60)) {
                TEdge? edge = report.Edges.FirstOrDefault(entry => ReferenceEquals(entry.Owner, item) && ReferenceEquals(entry.Requirement, requirement));
                string state = edge is null ? "未参与本次启动检查" : edge.StateValue == ModDependencyMatchState.Satisfied ? "已满足" : edge.StateValue == ModDependencyMatchState.Unsatisfied ? "未满足" : "版本待确认";
                Dictionary<ModDependencyKind, string> kindText = new() { [ModDependencyKind.Required] = "必需", [ModDependencyKind.Optional] = "可选", [ModDependencyKind.Recommended] = "建议", [ModDependencyKind.Suggested] = "可选提示", [ModDependencyKind.Incompatible] = "不兼容", [ModDependencyKind.Conflict] = "冲突提示" };
                if (requirement.KindValue is ModDependencyKind.Incompatible or ModDependencyKind.Conflict) state = edge is not null && edge.StateValue == ModDependencyMatchState.Satisfied ? "检测到匹配项" : "未检测到确定匹配项";
                lines.Add("  • [" + kindText[requirement.KindValue] + "] " + requirement.Display + "（" + state + (requirement.Side == "*" ? "" : "；" + requirement.Side) + "）");
            }
            report.ReverseDependencies.TryGetValue(item.Key, out List<TEdge>? referrers);
            List<TEdge> reverse = (referrers ?? new List<TEdge>()).Where(edge => IsRequiredFileProvider(report, edge, item.RootPath)).ToList();
            lines.Add("被这些模组依赖：");
            if (reverse.Count == 0) lines.Add("  未发现其他已启用模组声明依赖此 ID。");
            foreach (TEdge edge in reverse.Take(60)) lines.Add("  • " + edge.Owner.Display + "（" + Path.GetFileName(edge.Owner.RootPath) + "）" + (edge.Requirement.KindValue == ModDependencyKind.Required ? " · 必需" : " · 可选或兼容声明"));
        }
        lines.Add(""); lines.Add("本地依赖来自模组元数据；未声明的运行时依赖无法在此确认。");
        string supplement = report.DescribeOnlineFile(target);
        if (supplement != "") lines.Add("\r\n" + supplement);
        return string.Join("\r\n", lines);
    }
    public static ModDependencyMatchState MatchVersion(string? actual, string? requirement, string syntax = "fabric") => ModDependencyVersions.MatchVersion(actual, requirement, syntax);
}
