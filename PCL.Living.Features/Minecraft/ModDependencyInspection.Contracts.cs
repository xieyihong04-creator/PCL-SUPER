using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

public enum ModDependencyMatchState { Unknown, Satisfied, Unsatisfied }
public enum ModDependencyFileProviderKind { None, Required, Possible }
public enum ModDependencyKind { Required, Optional, Recommended, Suggested, Incompatible, Conflict }
public enum ModDependencyIssueKind { MissingRequired, DuplicateId, VersionMismatch, Incompatible, Recommendation }

// LF Modules/Minecraft/ModDependencyInspector.vb (LF LICENCE) is the behavior source.
// This LF-specific inspector is absent from PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModJarInJar.cs,
// ModJarInJarDependency.cs and ModLocalComp.cs are C# reference only (CE LICENSE
// and its PCL LICENCE): their cache, scan budgets and version-selection rules differ.
// PCL.Core/Minecraft/ResourceProject/ModDependencyResolver.cs uses online project IDs,
// not local loader Mod IDs; its policies are deliberately not imported here.
public sealed partial class ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>
    where TContext : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ContextData, new()
    where TMod : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ModData, new()
    where TDependency : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.DependencyData, new()
    where TEdge : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.EdgeData, new()
    where TIssue : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.IssueData, new()
    where TImpact : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ImpactData, new()
    where TReport : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ReportData, new()
    where TOnlineProject : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineProjectData, new()
    where TOnlineDependency : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineDependencyData, new()
    where TOnlineFile : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineFileData, new() {

    public class ContextData {
        public string? MinecraftVersion, JavaVersion;
        public string Side = "client";
        public bool LoaderKnown;
        public Dictionary<string, string?> LoaderVersions = new(StringComparer.Ordinal);
        public Dictionary<string, string?> AdditionalBuiltins = new(StringComparer.Ordinal);
        public string? LegacyForgeVersionFolder;
    }
    public class ModData {
        public string Id = null!, Group = null!, Name = null!, RootPath = null!, MetadataPath = null!, IconPath = null!, Format = null!;
        public string? Version;
        public string Side = "*";
        public bool Enabled, IsNested, IsBuiltin, IsCandidate;
        public Dictionary<string, string?> Provides = new(StringComparer.Ordinal);
        public List<TDependency> Dependencies = new();
        public string Key => (RootPath ?? "") + "!" + (MetadataPath ?? "") + ":" + Id;
        public string Display => (string.IsNullOrWhiteSpace(Name) ? Id : Name) + " [" + Id + "]";
        public TMod CopyWithEnabled(bool value) {
            TMod copy = (TMod)MemberwiseClone();
            copy.Enabled = value;
            return copy;
        }
    }
    public abstract class DependencyData {
        public string Id = null!;
        public JToken? Versions = new JValue("*");
        public string Syntax = "fabric";
        // The legacy enum field remains the only storage; its override maps these values.
        [JsonIgnore]
        public abstract ModDependencyKind KindValue { get; set; }
        public string Side = "*", Reason = null!;
        public List<TDependency> Children = new();
        public string Join = "any";
        public TDependency? Unless;
        public bool IsUnknown, EnforceWhenPresent;
        public string Display {
            get {
                if (Children.Count > 0) return "(" + string.Join(Join == "all" ? " 且 " : " 或 ", Children.Select(child => child.Display)) + ")";
                string range = Versions is null ? "*" : Versions.Type == JTokenType.String ? Versions.ToString() : Versions.ToString(Formatting.None);
                return (Id ?? "未知依赖") + (range == "*" || range == "" ? "" : " " + range);
            }
        }
    }
    public abstract class EdgeData {
        public TMod Owner = null!;
        public TDependency Requirement = null!;
        public List<TMod> Providers = new();
        [JsonIgnore]
        public abstract ModDependencyMatchState StateValue { get; set; }
        public bool Evaluated, ConditionBypassed;
    }
    public abstract class IssueData {
        [JsonIgnore]
        public abstract ModDependencyIssueKind KindValue { get; set; }
        public TMod Owner = null!;
        public TDependency Requirement = null!;
        public List<TMod> RelatedMods = new();
        public bool IsError;
        public string Message = null!;
    }
    public class ImpactData {
        public TMod Mod = null!;
        public List<TDependency> Requirements = new();
        public bool IsDirect, IsDefinite;
        public string Reason = null!;
    }
    // These are the original mutable online DTOs only. Online I/O remains in C014.
    public class OnlineProjectData {
        public string Platform = null!, ProjectId = null!, VersionId = null!, ProjectName = null!, VersionName = null!, Website = null!, MatchBasis = null!;
        public bool IsExact;
        public List<TOnlineDependency> Dependencies = new();
    }
    public class OnlineDependencyData {
        public string Platform = null!, ProjectId = null!, VersionId = null!, ProjectName = null!, Website = null!, FileName = null!, Kind = null!;
        public List<string> MatchedPaths = new(), EnabledPaths = new();
        public string Status = "尚未映射到本地文件，需核对";
    }
    public class OnlineFileData {
        public string FullPath = null!, Sha1 = null!;
        public bool Enabled, IsSupplementTarget;
        public List<TOnlineProject> Matches = new(), Candidates = new();
        public string Status = null!;
    }
    public class ReportData {
        public TContext Context = null!;
        public List<TMod> Mods = new(), Builtins = new();
        public List<TEdge> Edges = new();
        public List<TIssue> Issues = new();
        public List<string> Notices = new();
        public bool MetadataIncomplete, OnlineAttempted, OnlineCancelled, OnlineTimedOut;
        public int OnlineRequests;
        public DateTime OnlineCheckedAtUtc;
        public List<TOnlineFile> OnlineFiles = new();
        public List<string> OnlineNotes = new();
        public Dictionary<string, List<TEdge>> ReverseDependencies = new(StringComparer.Ordinal);
        public bool HasErrors => Issues.Any(issue => issue.IsError);
        public string ToSummary(int maxIssues = 30) {
            List<string> lines = new() {
                "已检查 " + Mods.Where(item => item.Enabled && !item.IsNested).Select(item => item.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() +
                " 个启用文件，识别 " + Mods.Count + " 个模组（包含禁用文件与内嵌模组）。",
                "确定问题：" + Issues.Count(issue => issue.IsError) + "；待确认或可选提示：" + Issues.Count(issue => !issue.IsError) + "。"
            };
            foreach (TIssue issue in Issues.OrderByDescending(item => item.IsError).Take(Math.Max(1, maxIssues)))
                lines.Add((issue.IsError ? "• " : "· [提示] ") + issue.Message);
            if (Issues.Count > Math.Max(1, maxIssues)) lines.Add("另有 " + (Issues.Count - Math.Max(1, maxIssues)) + " 项，请在模组依赖详情中查看。");
            if (MetadataIncomplete) lines.Add("部分文件的元数据未读完整；缺失依赖仅供确认，未据此判定启动错误。");
            if (Notices.Count > 0) {
                lines.Add(""); lines.AddRange(Notices.Take(5));
                if (Notices.Count > 5) lines.Add("另有 " + (Notices.Count - 5) + " 条读取说明。");
            }
            if (OnlineAttempted) {
                lines.Add("");
                lines.Add("下载源补充：精确匹配 " + OnlineFiles.Count(item => item.IsSupplementTarget && item.Matches.Any()) + " 个文件；使用 " + OnlineRequests + " 次请求。");
                lines.AddRange(OnlineNotes.Take(4));
                lines.Add("平台项目依赖仅作补充；候选和未映射项目未用于判定缺失或版本错误。");
            }
            lines.Add("");
            lines.Add(OnlineAttempted ? "以本地可靠声明为准；平台补充不能代替加载器对运行环境和内嵌版本的判断。" :
                "仅检查本地声明；运行时条件、加载器选择的内嵌版本与未声明依赖仍由游戏加载器判断。");
            return string.Join("\r\n", lines);
        }
        public string DescribeFile(string fullPath) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.DescribeFile((TReport)this, fullPath);
        public string DescribeOnlineFile(string fullPath) {
            if (!OnlineAttempted) return "";
            string target = FilePathKey(fullPath);
            List<string> lines = new();
            TOnlineFile? info = OnlineFiles.FirstOrDefault(item => SameFilePath(item.FullPath, target));
            if (info is not null) {
                lines.Add("下载源补充（本地可靠声明优先；" + OnlineCheckedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "）：");
                foreach (TOnlineProject match in info.Matches) {
                    lines.Add("• " + match.Platform + " · " + match.ProjectName + " · " + match.VersionName + "（" + match.MatchBasis + "）");
                    if (!string.IsNullOrEmpty(match.Website)) lines.Add("  " + match.Website);
                    if (match.Dependencies.Count == 0) lines.Add("  该平台版本没有声明项目依赖。");
                    foreach (TOnlineDependency requirement in match.Dependencies.Take(40)) {
                        string caption = requirement.ProjectName ?? requirement.ProjectId ?? requirement.FileName ?? "未提供项目 ID";
                        lines.Add("  • [" + OnlineKind(requirement.Kind) + "] " + caption + "：" + requirement.Status);
                        if (!string.IsNullOrEmpty(requirement.Website)) lines.Add("    " + requirement.Website);
                    }
                }
                if (info.Matches.Count == 0) lines.Add(info.Status ?? "没有找到精确文件匹配。");
                if (info.Candidates.Count > 0) {
                    lines.Add("按文件名搜索的候选（未选定、不能据此确认依赖）：");
                    foreach (TOnlineProject candidate in info.Candidates.Take(6)) lines.Add("  • " + candidate.Platform + " · " + candidate.ProjectName + "\r\n    " + candidate.Website);
                }
            }
            HashSet<string> reverse = new(StringComparer.Ordinal);
            foreach (TOnlineFile owner in OnlineFiles.Where(item => !SameFilePath(item.FullPath, target)))
                foreach (TOnlineProject match in owner.Matches)
                    foreach (TOnlineDependency requirement in match.Dependencies.Where(item => item.MatchedPaths.Any(value => SameFilePath(value, target))))
                        reverse.Add("  • " + Path.GetFileName(owner.FullPath) + " · " + match.Platform + " [" + OnlineKind(requirement.Kind) + "]");
            if (reverse.Count > 0) { lines.Add("按平台精确匹配，被这些文件依赖（仍需核对运行环境）："); lines.AddRange(reverse.Take(40)); }
            if (OnlineCancelled) lines.Add("联网补充已取消，本地检查结果已保留。");
            if (OnlineTimedOut) lines.Add("联网补充达到时间预算，本地检查结果已保留。");
            return string.Join("\r\n", lines);
        }
        public string DescribeOnlineImpacts(IEnumerable<string> removedPaths) {
            if (!OnlineAttempted) return "";
            HashSet<string> removed = new(removedPaths.Select(FilePathKey), StringComparer.OrdinalIgnoreCase);
            HashSet<string> lines = new(StringComparer.Ordinal);
            foreach (TOnlineFile owner in OnlineFiles.Where(item => item.Enabled && item.IsSupplementTarget && !removed.Contains(FilePathKey(item.FullPath))))
                foreach (TOnlineProject match in owner.Matches.Where(item => item.IsExact))
                    foreach (TOnlineDependency requirement in match.Dependencies.Where(item => item.Kind == "required" && item.EnabledPaths.Count > 0)) {
                        if (!requirement.EnabledPaths.All(value => removed.Contains(FilePathKey(value)))) continue;
                        lines.Add("• " + Path.GetFileName(owner.FullPath) + "：按 " + match.Platform + " 版本声明，可能失去项目依赖 " +
                            (requirement.ProjectName ?? requirement.ProjectId) + "（平台补充，需核对）。");
                    }
            return lines.Count == 0 ? "" : "下载源声明的补充影响：\r\n" + string.Join("\r\n", lines.Take(20));
        }
        private static string OnlineKind(string? kind) => kind switch {
            "required" => "必需", "optional" => "可选", "embedded" => "内嵌", "incompatible" => "不兼容", _ => "其他声明"
        };
    }
}
