using System.Security.Cryptography;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Network;
using PCL.Network.Loaders;
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
using CompFile = PCL.Comp.CompFile;
using CompLoaderType = PCL.Comp.CompLoaderType;
using LocalCompFile = PCL.LocalComp.LocalCompFile;

namespace PCL;

internal sealed class DependencyEnableItem
{
    internal string Source = "";
    internal string Destination = "";
    internal string Sha1 = "";
    internal string ModId = "";
    internal string FileName => Path.GetFileName(Source);
}

internal sealed class DependencyDownloadItem
{
    internal string FileName = "";
    internal string Source = "";
    internal string ProjectId = "";
    internal string ProjectName = "";
    internal string Version = "";
    internal required DownloadFile Net;
}

internal sealed class DependencyAutoFillPlan
{
    internal string ModsFolder = "";
    internal string StageFolder = "";
    internal CeDependencyInspection.Context Context = null!;
    internal CeDependencyInspection.Report Report = null!;
    internal CeDependencyInspection.Report Remaining = null!;
    internal DependencyReportView View = null!;
    internal List<DependencyEnableItem> Enables = [];
    internal List<DependencyDownloadItem> Downloads = [];
    internal List<string> Notes = [];
    internal bool HasWork => Enables.Count > 0 || Downloads.Count > 0;
    internal bool HasErrors => Remaining.Issues.Any(issue => issue.IsError);

    internal string ActionText
    {
        get
        {
            if (Enables.Count > 0 && Downloads.Count > 0) return "启用并补全后启动";
            if (Enables.Count > 0) return "启用并启动";
            if (Downloads.Count > 0) return "补全并启动";
            return "仍然启动";
        }
    }

    internal string Describe()
    {
        var lines = new List<string> { Remaining.ToSummary(20) };
        if (Enables.Count > 0)
        {
            lines.Add("");
            lines.Add("将启用这些已禁用且满足依赖的文件：");
            foreach (var item in Enables.Take(20))
                lines.Add("• " + item.FileName + (string.IsNullOrEmpty(item.ModId) ? "" : "（" + item.ModId + "）"));
            if (Enables.Count > 20) lines.Add("另有 " + (Enables.Count - 20) + " 个文件。");
        }

        if (Downloads.Count > 0)
        {
            lines.Add("");
            lines.Add("将从下载源补全这些必需前置（已按当前游戏版本与加载器筛选）：");
            foreach (var item in Downloads.Take(20))
                lines.Add("• " + item.ProjectName + " · " + item.Source + " · " +
                          (string.IsNullOrEmpty(item.Version) ? "未知版本" : item.Version) + "（" + item.FileName + "）");
            if (Downloads.Count > 20) lines.Add("另有 " + (Downloads.Count - 20) + " 个前置。");
        }

        if (Notes.Count > 0)
        {
            lines.Add("");
            lines.AddRange(Notes.Take(8));
            if (Notes.Count > 8) lines.Add("另有 " + (Notes.Count - 8) + " 条说明。");
        }

        lines.Add("");
        lines.Add("自动补全只使用平台记录与本地文件哈希精确对应的条目；加载器实际选择的内嵌版本仍以游戏为准。");
        return string.Join("\r\n", lines);
    }

    /// <summary>
    ///     仅用于资源页的只读检查展示：不出现“启动”措辞，也不改动任何文件。
    /// </summary>
    internal string DescribeCheck()
    {
        var lines = new List<string> { Remaining.ToSummary(20) };
        if (Enables.Count > 0)
        {
            lines.Add("");
            lines.Add("以下已禁用文件可满足现有依赖，启动时会自动启用：");
            foreach (var item in Enables.Take(20))
                lines.Add("• " + item.FileName + (string.IsNullOrEmpty(item.ModId) ? "" : "（" + item.ModId + "）"));
            if (Enables.Count > 20) lines.Add("另有 " + (Enables.Count - 20) + " 个文件。");
        }

        if (Downloads.Count > 0)
        {
            lines.Add("");
            lines.Add("以下必需前置可在启动时自动补全（已按当前游戏版本与加载器筛选）：");
            foreach (var item in Downloads.Take(20))
                lines.Add("• " + item.ProjectName + " · " + item.Source + " · " +
                          (string.IsNullOrEmpty(item.Version) ? "未知版本" : item.Version) + "（" + item.FileName + "）");
            if (Downloads.Count > 20) lines.Add("另有 " + (Downloads.Count - 20) + " 个前置。");
        }

        if (Notes.Count > 0)
        {
            lines.Add("");
            lines.AddRange(Notes.Take(8));
            if (Notes.Count > 8) lines.Add("另有 " + (Notes.Count - 8) + " 条说明。");
        }

        if (!HasWork) lines.Add("没有发现需要自动处理的依赖项。");
        lines.Add("");
        lines.Add("本页只做检查，不会修改文件；补全在下次启动时由你确认后执行。");
        return string.Join("\r\n", lines);
    }
}

/// <summary>
///     LF ModDependencyAutoFill 的 CE 适配（甲）：补全来源改为 CE 的 Comp/CompDependency 管线，
///     不再移植 LF 的 ResourceVersion、ResourceProject 与在线搜索。
///     规则保持 LF 原意：只处理精确匹配文件声明的必需项目；下载后用加载器元数据复核；
///     已安装但被禁用的前置优先启用；平台候选冲突时不标为缺失。
/// </summary>
internal static class DependencyAutoFill
{
    private const int MaxDownloads = 64;

    internal static DependencyAutoFillPlan Build(McInstance instance, CancellationToken token)
    {
        var folder = CeDependencyProbe.ModsFolder(instance);
        var context = CeDependencyProbe.ContextFor(
            instance,
            GameLaunch.mcLaunchJavaSelected?.Installation.Version.ToString());
        var report = Inspector.Inspect(folder, context, token);
        var plan = new DependencyAutoFillPlan
        {
            ModsFolder = folder,
            StageFolder = Path.Combine(folder, ".pcl-import-" + Guid.NewGuid().ToString("N")),
            Context = context,
            Report = report,
            Remaining = report,
            View = CeDependencyProbe.ToView(report, context)
        };
        if (!Directory.Exists(folder) || !report.Mods.Any(mod => mod.Enabled))
            return plan;

        CollectEnables(plan, report, token);
        if (plan.Enables.Count > 0)
            plan.Remaining = Inspector.WithEnabledFiles(
                report,
                plan.Enables.Select(item => item.Source),
                token);
        CollectDownloads(plan, instance, token);
        return plan;
    }

    /// <summary>启用已禁用的文件不会改动内容，只会去掉 .disabled/.old 后缀。</summary>
    private static void CollectEnables(DependencyAutoFillPlan plan, CeDependencyInspection.Report report, CancellationToken token)
    {
        var disabled = report.Mods
            .Where(mod => !mod.Enabled && !mod.IsNested)
            .GroupBy(mod => mod.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (disabled.Count == 0)
            return;

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in report.Edges)
        {
            token.ThrowIfCancellationRequested();
            if (!edge.Evaluated || edge.ConditionBypassed)
                continue;
            if (edge.Requirement.KindValue != ModDependencyKind.Required)
                continue;
            if (edge.StateValue == ModDependencyMatchState.Satisfied)
                continue;
            foreach (var group in disabled)
            {
                if (!Inspector.FileSatisfiesDependency(report, group.Key, edge.Requirement))
                    continue;
                if (!selected.Add(group.Key))
                    continue;
                string destination = EnableDestination(group.Key);
                if (string.Equals(destination, group.Key, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    plan.Notes.Add(Path.GetFileName(group.Key) + "：同名的启用文件已经存在，未自动启用。");
                    continue;
                }

                try
                {
                    plan.Enables.Add(new DependencyEnableItem
                    {
                        Source = group.Key,
                        Destination = destination,
                        Sha1 = Sha1File(group.Key),
                        ModId = group.FirstOrDefault()?.Id ?? ""
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    plan.Notes.Add(Path.GetFileName(group.Key) + "：读取失败，未自动启用（" + ex.Message + "）。");
                }
            }
        }
    }

    private static void CollectDownloads(DependencyAutoFillPlan plan, McInstance instance, CancellationToken token)
    {
        if (string.IsNullOrEmpty(plan.Remaining.Context.MinecraftVersion))
        {
            plan.Notes.Add("尚未确认当前 Minecraft 版本，未选择自动补全文件。");
            return;
        }

        var loaders = CeDependencyProbe.LoadersFor(instance);
        if (loaders.Count == 0)
        {
            plan.Notes.Add("尚未确认当前加载器，未选择自动补全文件。");
            return;
        }

        var loaderNames = CeDependencyProbe.LoaderNames(instance);
        var cache = CeDependencyProbe.LoadLocalCompCache();
        var exact = new List<(LocalCompFile Local, CompFile File, string Source)>();
        var installed = new List<InstalledModIdentity>();
        foreach (var path in CeDependencyProbe.EnumerateModFiles(plan.ModsFolder))
        {
            token.ThrowIfCancellationRequested();
            if (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                continue;
            var local = new LocalCompFile(path);
            if (!CeDependencyProbe.Hydrate(local, cache, instance, loaders))
                continue;
            var file = local.CompFile;
            if (string.IsNullOrWhiteSpace(file.ProjectId))
                continue;
            // 加载器元数据复核：平台记录必须与本地实际启用、面向客户端的模组声明对应。
            var mods = plan.Remaining.Mods
                .Where(mod => SamePath(mod.RootPath, path) && mod.Enabled && !mod.IsNested && mod.Side != "server")
                .ToList();
            if (mods.Count == 0)
                continue;
            if (!string.IsNullOrEmpty(local.ModId)
                && !mods.Any(mod => string.Equals(mod.Id, local.ModId, StringComparison.OrdinalIgnoreCase)))
            {
                plan.Notes.Add(Path.GetFileName(path) + "：平台记录与本地声明不一致，未用于自动补全。");
                continue;
            }

            var source = file.FromCurseForge ? "CurseForge" : "Modrinth";
            exact.Add((local, file, source));
            installed.Add(new InstalledModIdentity
            {
                SourceProjectId = file.ProjectId,
                Source = source,
                ModId = local.ModId,
                GameVersions = [Comp.NormalizeGameVersion(plan.Remaining.Context.MinecraftVersion)],
                Loaders = loaderNames
            });
        }

        if (exact.Count == 0)
            return;

        var enabledProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var enable in plan.Enables)
        {
            try
            {
                var local = new LocalCompFile(enable.Source);
                if (CeDependencyProbe.Hydrate(local, cache, instance, loaders)
                    && !string.IsNullOrWhiteSpace(local.CompFile.ProjectId))
                    enabledProjects.Add((local.CompFile.FromCurseForge ? "CurseForge" : "Modrinth") + "|" +
                                        local.CompFile.ProjectId);
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, "[Deps] 读取待启用前置的平台记录失败：" + enable.FileName);
            }
        }

        var references = new Dictionary<string, ModDependencyReference>(StringComparer.Ordinal);
        foreach (var (_, file, source) in exact)
        {
            foreach (var projectId in file.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(projectId))
                    continue;
                if (enabledProjects.Contains(source + "|" + projectId))
                    continue;
                references[source + "|" + projectId] = new ModDependencyReference
                {
                    ProjectId = projectId,
                    Source = source,
                    IsRequired = true
                };
            }
        }

        if (references.Count == 0)
            return;

        ModDependencyResolutionResult result;
        try
        {
            var request = new ModDependencyRequest
            {
                TargetMinecraftVersion = plan.Remaining.Context.MinecraftVersion!,
                TargetLoaders = loaderNames,
                RequiredDependencies = [.. references.Values],
                InstalledMods = installed,
                ProjectResolver = CompDependency.ResolveProjectFiles
            };
            result = new ModDependencyResolver().Resolve(request);
        }
        catch (Exception ex)
        {
            plan.Notes.Add("依赖查询未完成：" + ex.Message);
            return;
        }

        foreach (var unresolved in result.Unresolved)
            plan.Notes.Add("无法解析必需前置：" + unresolved.Source + " #" + unresolved.ProjectId +
                           "（" + unresolved.Reason + "）。");

        foreach (var install in result.ToInstall)
        {
            token.ThrowIfCancellationRequested();
            if (plan.Downloads.Count >= MaxDownloads)
            {
                plan.Notes.Add("必需前置数量超过本次自动补全上限，请分批管理模组。");
                break;
            }

            var fromCurseForge = string.Equals(install.Source, "CurseForge", StringComparison.OrdinalIgnoreCase);
            if (!Comp.compProjectCache.TryGetValue(install.ProjectId, out var project)
                || project.FromCurseForge != fromCurseForge)
                continue;
            var file = Comp.CompFilesGet(install.ProjectId, fromCurseForge)
                .FirstOrDefault(item => string.Equals(item.Id, install.File.Id, StringComparison.OrdinalIgnoreCase));
            if (file is null)
                continue;
            var fileName = Comp.CompFileNameGet(project, file);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;
            if (File.Exists(Path.Combine(plan.ModsFolder, fileName)))
            {
                plan.Notes.Add(fileName + "：本地已有同名文件，未自动下载。");
                continue;
            }
            if (File.Exists(Path.Combine(plan.ModsFolder, fileName) + ".disabled")
                || File.Exists(Path.Combine(plan.ModsFolder, fileName) + ".old"))
            {
                plan.Notes.Add(fileName + "：已安装但被禁用，请在模组管理中选择兼容版本后启用。");
                continue;
            }
            if (plan.Enables.Any(item =>
                    string.Equals(Path.GetFileName(item.Source), fileName + ".disabled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(item.Source), fileName + ".old", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (plan.Downloads.Any(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var target = Path.Combine(plan.StageFolder, fileName);
                plan.Downloads.Add(new DependencyDownloadItem
                {
                    FileName = fileName,
                    Source = install.Source,
                    ProjectId = install.ProjectId,
                    ProjectName = !string.IsNullOrWhiteSpace(project.TranslatedName)
                        ? project.TranslatedName
                        : string.IsNullOrWhiteSpace(project.RawName) ? install.ProjectId : project.RawName,
                    Version = file.Version ?? "",
                    Net = file.ToNetFile(
                        target,
                        Comp.DownloadReason.Dependency,
                        plan.Remaining.Context.MinecraftVersion,
                        loaders[0])
                });
            }
            catch (Exception ex)
            {
                plan.Notes.Add("前置 " + install.ProjectId + " 的下载信息无效：" + ex.Message);
            }
        }
    }

    /// <summary>执行计划。返回新增或启用的文件数；任何一步失败都会恢复到执行前的状态。</summary>
    internal static int Apply(DependencyAutoFillPlan plan, CancellationToken token)
    {
        if (!plan.HasWork)
            return 0;
        var stamp = DependencyLaunchGate.Fingerprint(plan.View);
        var before = Inspector.Inspect(plan.ModsFolder, plan.Context, token);
        if (DependencyLaunchGate.Fingerprint(CeDependencyProbe.ToView(before, plan.Context)) != stamp)
            throw new IOException("模组文件已变化，请重新启动以检查依赖。");

        EnsureWithin(plan.ModsFolder, plan.StageFolder);
        Directory.CreateDirectory(plan.StageFolder);
        var created = new List<(string Path, string Sha1)>();
        var enabled = new List<DependencyEnableItem>();
        try
        {
            var chosen = DownloadAndVerify(plan, before, token);

            // 先确认将要启用的文件没有变化，再执行任何改名。
            foreach (var item in plan.Enables)
            {
                token.ThrowIfCancellationRequested();
                EnsureWithin(plan.ModsFolder, item.Source);
                EnsureWithin(plan.ModsFolder, item.Destination);
                if (!File.Exists(item.Source))
                    throw new IOException("已禁用的前置文件已不存在：" + item.FileName);
                if (File.Exists(item.Destination) || Directory.Exists(item.Destination))
                    throw new IOException("启用目标已存在，未覆盖：" + Path.GetFileName(item.Destination));
                if (!string.Equals(Sha1File(item.Source), item.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("已禁用的前置文件已变化，未自动启用：" + item.FileName);
            }

            foreach (var index in chosen)
            {
                token.ThrowIfCancellationRequested();
                var item = plan.Downloads[index];
                var destination = Path.Combine(plan.ModsFolder, item.FileName);
                EnsureWithin(plan.ModsFolder, destination);
                if (File.Exists(destination) || Directory.Exists(destination))
                    throw new IOException("已有同名文件，未覆盖：" + item.FileName);
                File.Move(item.Net.LocalPath, destination);
                created.Add((destination, Sha1File(destination)));
            }

            foreach (var item in plan.Enables)
            {
                token.ThrowIfCancellationRequested();
                File.Move(item.Source, item.Destination);
                enabled.Add(item);
            }

            var after = Inspector.Inspect(plan.ModsFolder, plan.Context, token);
            var beforeErrors = before.Issues
                .Where(issue => issue.IsError)
                .Select(issue => issue.KindValue + "|" + issue.Message)
                .ToHashSet(StringComparer.Ordinal);
            var introduced = after.Issues
                .Where(issue => issue.IsError && !beforeErrors.Contains(issue.KindValue + "|" + issue.Message))
                .ToList();
            if (introduced.Count > 0)
                throw new IOException("补全后出现新的依赖错误，已撤销本次改动：" + Environment.NewLine +
                                      string.Join(Environment.NewLine,
                                          introduced.Take(5).Select(issue => "• " + issue.Message)));
            return created.Count + enabled.Count;
        }
        catch
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                var item = created[index];
                try
                {
                    if (File.Exists(item.Path)
                        && string.Equals(Sha1File(item.Path), item.Sha1, StringComparison.OrdinalIgnoreCase))
                        File.Delete(item.Path);
                }
                catch (Exception ex)
                {
                    LauncherCommon.Log(ex, "[Deps] 回滚新增前置失败：" + item.Path);
                }
            }

            for (var index = enabled.Count - 1; index >= 0; index--)
            {
                var item = enabled[index];
                try
                {
                    if (!File.Exists(item.Source) && File.Exists(item.Destination)
                        && string.Equals(Sha1File(item.Destination), item.Sha1, StringComparison.OrdinalIgnoreCase))
                        File.Move(item.Destination, item.Source);
                }
                catch (Exception ex)
                {
                    LauncherCommon.Log(ex, "[Deps] 回滚启用操作失败：" + item.Source);
                }
            }

            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(plan.StageFolder))
                    Directory.Delete(plan.StageFolder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LauncherCommon.Log(ex, "[Deps] 前置下载临时文件暂时被占用，已保留：" + plan.StageFolder);
            }
        }
    }

    /// <summary>下载并复核，返回可以移动到模组目录的计划下标。</summary>
    private static List<int> DownloadAndVerify(
        DependencyAutoFillPlan plan,
        CeDependencyInspection.Report before,
        CancellationToken token)
    {
        if (plan.Downloads.Count == 0)
            return [];

        var files = plan.Downloads.Select(item => item.Net).ToList();
        var loader = new LoaderDownload("补全模组依赖", files);
        LauncherCommon.RunInUi(() => Loading.loaderTaskbar.Add(loader));
        loader.WaitForExit();
        if (loader.State != LoadState.Finished)
            throw new IOException("必需前置下载未完成，未自动安装。");

        foreach (var item in plan.Downloads)
        {
            token.ThrowIfCancellationRequested();
            var error = item.Net.Check?.Check(item.Net.LocalPath);
            if (!string.IsNullOrEmpty(error))
                throw new IOException("下载的前置文件校验失败：" + error);
        }

        var added = Inspector.Inspect(plan.StageFolder, plan.Context, token);
        if (added.MetadataIncomplete)
            throw new IOException("前置文件的元数据未能完整识别，未自动安装。");

        var existingIds = before.Mods
            .Where(mod => mod.Enabled && !mod.IsNested)
            .Select(mod => mod.Id)
            .ToHashSet(StringComparer.Ordinal);
        var addedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var chosen = new List<int>();
        var chosenPaths = new List<string>();
        for (var index = 0; index < plan.Downloads.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var item = plan.Downloads[index];
            var mods = added.Mods
                .Where(mod => SamePath(mod.RootPath, item.Net.LocalPath) && !mod.IsNested)
                .ToList();
            if (mods.Count == 0)
                throw new IOException("下载文件没有可识别的模组声明：" + item.FileName);
            if (!mods.Any(mod => mod.Side != "server"))
                throw new IOException("下载的前置仅支持服务端，未加入客户端模组目录：" + item.FileName);

            var hash = item.Net.Check?.hash ?? "";
            if (mods.Any(mod => existingIds.Contains(mod.Id)))
            {
                if (mods.Any(mod => addedIds.TryGetValue(mod.Id, out var previous)
                                    && !string.Equals(previous, hash, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("多个前置文件声明同一个模组 ID，未自动安装：" + item.FileName);
                continue;
            }

            chosen.Add(index);
            chosenPaths.Add(item.Net.LocalPath);
            foreach (var mod in mods)
            {
                existingIds.Add(mod.Id);
                addedIds[mod.Id] = hash;
            }
        }

        if (chosen.Count == 0)
            return [];

        var ready = Inspector.WithEnabledFiles(
            before,
            plan.Enables.Select(item => item.Source),
            token);
        var combined = new CeDependencyInspection.Report
        {
            Context = before.Context,
            Builtins = before.Builtins,
            MetadataIncomplete = ready.MetadataIncomplete || added.MetadataIncomplete
        };
        combined.Mods.AddRange(ready.Mods);
        combined.Mods.AddRange(added.Mods.Where(mod => chosenPaths.Any(path => SamePath(mod.RootPath, path))));
        Inspector.BuildReport(combined, token);

        var unmet = combined.Edges
            .Where(edge => edge.Evaluated
                           && edge.Requirement.KindValue == ModDependencyKind.Required
                           && edge.StateValue != ModDependencyMatchState.Satisfied
                           && (chosenPaths.Any(path => SamePath(edge.Owner.RootPath, path))
                               || plan.Enables.Any(enable => SamePath(enable.Source, edge.Owner.RootPath))))
            .ToList();
        if (unmet.Count > 0)
            throw new IOException("前置文件仍有未满足或无法确认的必需依赖，未自动安装：" + Environment.NewLine +
                                  string.Join(Environment.NewLine,
                                      unmet.Take(5).Select(edge =>
                                          "• " + edge.Owner.Display + " 需要 " + edge.Requirement.Display)));

        var beforeIssues = before.Issues
            .Select(issue => issue.KindValue + "|" + issue.Message)
            .ToHashSet(StringComparer.Ordinal);
        var introduced = combined.Issues
            .Where(issue => (issue.IsError
                             || (issue.KindValue == ModDependencyIssueKind.MissingRequired
                                 && chosenPaths.Any(path => SamePath(issue.Owner.RootPath, path))))
                            && !beforeIssues.Contains(issue.KindValue + "|" + issue.Message))
            .ToList();
        if (introduced.Count > 0)
            throw new IOException("前置文件会引入依赖缺失、版本不匹配或冲突，未自动安装：" + Environment.NewLine +
                                  string.Join(Environment.NewLine,
                                      introduced.Take(5).Select(issue => "• " + issue.Message)));

        var current = Inspector.Inspect(plan.ModsFolder, plan.Context, token);
        if (DependencyLaunchGate.Fingerprint(CeDependencyProbe.ToView(current, plan.Context))
            != DependencyLaunchGate.Fingerprint(plan.View))
            throw new IOException("下载期间模组文件发生变化，已停止自动安装。");
        return chosen;
    }

    private static string EnableDestination(string source)
    {
        if (source.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            return source[..^".disabled".Length];
        if (source.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            return source[..^".old".Length];
        return source;
    }

    private static bool SamePath(string first, string second)
    {
        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void EnsureWithin(string root, string path)
    {
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var pathFull = Path.GetFullPath(path);
        if (!pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new IOException("路径超出模组目录：" + path);
    }

    private static string Sha1File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA1.HashData(stream));
    }
}
