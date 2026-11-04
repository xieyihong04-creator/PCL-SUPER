using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net;
using PCL.Core.UI;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;
using PCL.Living.Features;
using PCL.Network;

namespace PCL;

/// <summary>
///     把 LF 的整合包更新引擎接到 CE 的实例页：选择新版整合包、预览差异、保留个人修改，
///     更新前自动保存还原点，提交失败按事务日志回滚。
///     暂不支持直接改变 Minecraft 或加载器版本，遇到这类整合包会提示先安装对应版本。
/// </summary>
internal static class InstanceModpackUpdateBridge
{
    private static ModpackUpdateEngine? _engine;
    private static int _busy;

    internal static ModpackUpdateEngine Engine => _engine ??= new ModpackUpdateEngine(CreateHost());

    /// <summary>启动一次整合包更新；所有确认与文件选择都在任务线程里通过 UI 线程完成。</summary>
    internal static void Start(McInstance? instance) =>
        Start(instance, "Instance.ModpackUpdate.Task.Title", Execute);

    /// <summary>按作者清单检查新版本并预览差异；确认后才走同一套事务提交。</summary>
    internal static void StartCheck(McInstance? instance) =>
        Start(instance, "Instance.ModpackUpdate.Check.Task.Title", ExecuteCheck);

    private static void Start(McInstance? instance, string titleKey,
        Func<McInstance, Loading.LoaderTask<string, string>, string> action)
    {
        if (instance is null)
            return;
        if (GameLaunch.isLaunching || GameLaunch.mcLaunchProcess is { HasExited: false }
            || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            HintService.Show(Hints.Info(Lang.Text("Instance.ModpackUpdate.Busy")));
            return;
        }

        var loader = new Loading.LoaderTask<string, string>(
            Lang.Text(titleKey, instance.Name),
            task =>
            {
                try
                {
                    task.output = action(instance, task);
                }
                catch (OperationCanceledException)
                {
                    // 玩家在确认框中取消，属于正常流程。
                }
                catch (Exception ex)
                {
                    Common.Log(ex, "整合包更新失败", Common.LogLevel.Hint,
                        userSummary: Lang.Text("Instance.ModpackUpdate.Failed", ex.Message));
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
            });
        try
        {
            loader.Start();
        }
        catch
        {
            Interlocked.Exchange(ref _busy, 0);
            throw;
        }
    }

    private static string Execute(McInstance instance, Loading.LoaderTask<string, string> task)
    {
        var token = task.AbortedToken;
        var versionFolder = instance.PathInstance;
        var stage = Path.Combine(Common.pathTemp, "ModpackUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            task.Progress = 0.02;
            if (Engine.Pending(versionFolder).Count > 0)
            {
                if (!Confirm(
                        Lang.Text("Instance.ModpackUpdate.Pending.Message"),
                        Lang.Text("Instance.ModpackUpdate.Title"),
                        Lang.Text("Instance.ModpackUpdate.Restore"),
                        Lang.Text("Common.Action.Cancel"),
                        true))
                    throw new OperationCanceledException();
                Engine.Recover(versionFolder, instance.PathIndie);
            }

            var baseline = EnsureBaseline(instance, stage, task);

            var archive = ResolveArchive(stage, token);
            if (archive is null)
                throw new OperationCanceledException();
            task.Progress = 0.1;
            var package = Engine.ReadPackage(archive, Path.Combine(stage, "next"), token);
            if (!SameRuntime(package, instance))
                throw new IOException(Lang.Text("Instance.ModpackUpdate.RuntimeChanged",
                    package.Minecraft, instance.Info.VanillaName));
            var plan = Engine.Plan(instance.PathIndie, baseline, package);
            task.Progress = 0.15;
            Engine.DownloadChanges(plan, Path.Combine(stage, "next"), token);
            task.Progress = 0.7;

            if (!ShowPreview(plan))
                throw new OperationCanceledException();

            return CommitPlan(instance, plan, package, task);
        }
        finally
        {
            Cleanup(stage);
        }
    }

    private static string ExecuteCheck(McInstance instance, Loading.LoaderTask<string, string> task)
    {
        var token = task.AbortedToken;
        var versionFolder = instance.PathInstance;
        var stage = Path.Combine(Common.pathTemp, "ModpackUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var manifestUrl = InstanceModpackManifest.GetUrl(instance)
                              ?? throw new IOException(Lang.Text("Instance.ModpackUpdate.Manifest.NotConfigured"));
            task.Progress = 0.05;
            var latest = InstanceModpackManifest.Fetch(manifestUrl, token);
            task.Progress = 0.1;

            var current = (string?)Engine.ReadManifest(versionFolder)?["version"]
                          ?? States.Instance.ModpackVersion[versionFolder];
            if (!string.IsNullOrWhiteSpace(current) && SameVersion(current, latest.Version))
            {
                LauncherCommon.RunInUi(() => HintService.Show(Hints.Info(
                    Lang.Text("Instance.ModpackUpdate.Check.UpToDate", latest.Version))));
                return "";
            }

            if (Engine.Pending(versionFolder).Count > 0)
                throw new IOException(Lang.Text("Instance.ModpackUpdate.Check.PendingBlocked"));

            var baseline = EnsureBaseline(instance, stage, task);
            task.Progress = 0.15;
            var archive = InstanceModpackManifest.Download(latest.PackageUrl,
                Path.Combine(stage, "package.zip"), token);
            task.Progress = 0.25;
            var package = Engine.ReadPackage(archive, Path.Combine(stage, "next"), token);
            if (!SameRuntime(package, instance))
                throw new IOException(Lang.Text("Instance.ModpackUpdate.RuntimeChanged",
                    package.Minecraft, instance.Info.VanillaName));
            var plan = Engine.Plan(instance.PathIndie, baseline, package);
            Engine.DownloadChanges(plan, Path.Combine(stage, "next"), token);
            task.Progress = 0.7;

            if (!ShowPreview(plan))
                throw new OperationCanceledException();

            return CommitPlan(instance, plan, package, task);
        }
        finally
        {
            Cleanup(stage);
        }
    }

    /// <summary>读取作者清单；没有时询问原始整合包，用来区分作者文件与个人修改。</summary>
    private static JObject EnsureBaseline(McInstance instance, string stage,
        Loading.LoaderTask<string, string> task)
    {
        var baseline = Engine.ReadManifest(instance.PathInstance);
        if (baseline is not null)
            return baseline;
        if (!Confirm(
                Lang.Text("Instance.ModpackUpdate.NoManifest.Message"),
                Lang.Text("Instance.ModpackUpdate.Title"),
                Lang.Text("Instance.ModpackUpdate.SelectOriginal.Button"),
                Lang.Text("Common.Action.Cancel"),
                false))
            throw new OperationCanceledException();
        var original = SelectArchive(Lang.Text("Instance.ModpackUpdate.SelectOriginal.Title"));
        if (original is null)
            throw new OperationCanceledException();
        task.Progress = 0.05;
        var origin = Engine.ReadPackage(original, Path.Combine(stage, "original"), task.AbortedToken);
        if (!SameVersion(origin.Minecraft, instance.Info.VanillaName))
            throw new IOException(Lang.Text("Instance.ModpackUpdate.OriginalMismatch",
                origin.Minecraft, instance.Info.VanillaName));
        return ModpackUpdateEngine.Manifest(origin);
    }

    private static string CommitPlan(McInstance instance, PackUpdatePlan plan, PackUpdatePackage package,
        Loading.LoaderTask<string, string> task)
    {
        var token = task.AbortedToken;
        var versionFolder = instance.PathInstance;
        task.Progress = 0.85;
        if (GameLaunch.isLaunching || GameLaunch.mcLaunchProcess is { HasExited: false })
            throw new IOException(Lang.Text("Instance.ModpackUpdate.Busy"));
        EnvironmentHistoryBridge.CaptureBeforeUpdate(instance, "整合包更新前");
        task.Progress = 0.9;
        var backup = Engine.Commit(plan, versionFolder, token, null,
            index => task.Progress = 0.9 + 0.08 * (index + 1) / Math.Max(1, plan.Changes.Count));
        if (!string.IsNullOrWhiteSpace(package.Version)
            && !string.IsNullOrWhiteSpace(States.Instance.ModpackVersion[versionFolder]))
            States.Instance.ModpackVersion[versionFolder] = package.Version;
        InstanceModpackMode.Set(instance, true);
        task.Progress = 1;
        LauncherCommon.RunInUi(() =>
        {
            HintService.Show(Hints.Success(Lang.Text("Instance.ModpackUpdate.Success", backup)));
            Launcher.frmMain?.PageNameRefresh();
        });
        return backup;
    }

    private static void Cleanup(string stage)
    {
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        catch (Exception ex)
        {
            Common.Log(ex, "清理整合包更新临时文件失败", Common.LogLevel.Developer);
        }
    }

    private static PackUpdateHost CreateHost() => new()
    {
        EnsureWithin = EnvironmentHistoryBridge.EnsureWithin,
        Request = Request,
        Download = Download,
        TempFolder = () =>
        {
            var folder = Path.Combine(Common.pathTemp, "ModpackUpdate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        },
        IndiePath = versionFolder => new McInstance(versionFolder).PathIndie
    };

    private static JToken Request(string url, HttpMethod method, string? body)
    {
        var response = GameDownload.DlModRequest<JsonObject>(url, method.Method, body ?? "", "application/json");
        return JToken.Parse(response.ToJsonString())["data"]
               ?? throw new IOException("整合包文件接口未返回数据。");
    }

    private static void Download(List<PackUpdateNetFile> files, CancellationToken token)
    {
        var client = NetworkService.GetClient();
        try
        {
            Parallel.ForEach(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = token
            }, file => DownloadOne(client, file, token));
        }
        catch (AggregateException ex)
        {
            throw ex.Flatten().InnerExceptions[0];
        }
    }

    private static void DownloadOne(HttpClient client, PackUpdateNetFile file, CancellationToken token)
    {
        Exception? last = null;
        foreach (var url in file.Urls)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                    .GetAwaiter().GetResult();
                Requester.EnsureSuccess(response);
                var folder = Path.GetDirectoryName(file.Destination);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                using var input = response.Content.ReadAsStream(token);
                using var output = new FileStream(file.Destination, FileMode.Create, FileAccess.Write,
                    FileShare.None, 81920);
                input.CopyTo(output);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new IOException("下载整合包更新文件失败：" + last?.Message, last);
    }

    private static bool SameRuntime(PackUpdatePackage package, McInstance instance)
    {
        if (!SameVersion(package.Minecraft, instance.Info.VanillaName))
            return false;
        var current = new Dictionary<ModLoaders, string>
        {
            [ModLoaders.Fabric] = instance.Info.Fabric ?? "",
            [ModLoaders.Forge] = instance.Info.Forge ?? "",
            [ModLoaders.NeoForge] = instance.Info.NeoForge ?? ""
        };
        foreach (var pair in package.Loaders)
            if (!current.TryGetValue(pair.Key, out var value)
                || !string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private static string BuildPreview(PackUpdatePlan plan)
    {
        var changes = plan.Changes.Where(change => !change.Conflict).ToList();
        var preserved = plan.Changes.Where(change => change.Conflict).ToList();
        int added = changes.Count(change => change.Incoming is not null && change.Before is null);
        int updated = changes.Count(change => change.Incoming is not null && change.Before is not null);
        int removed = changes.Count(change => change.Incoming is null);
        var lines = new List<string>
        {
            Lang.Text("Instance.ModpackUpdate.Summary", plan.Package.Name, plan.Package.Version,
                plan.Package.Minecraft, added, updated, removed, preserved.Count)
        };

        void Append(string title, IEnumerable<PackUpdateChange> items)
        {
            var list = items.ToList();
            if (list.Count == 0)
                return;
            lines.Add("");
            lines.Add("**" + title + "**");
            foreach (var change in list.Take(12))
                lines.Add("- `" + change.Relative + "`"
                          + (string.IsNullOrEmpty(change.Reason) ? "" : "（" + change.Reason + "）"));
            if (list.Count > 12)
                lines.Add(Lang.Text("Instance.ModpackUpdate.Preview.More", list.Count - 12));
        }

        Append(Lang.Text("Instance.ModpackUpdate.Section.Added"),
            changes.Where(change => change.Incoming is not null && change.Before is null));
        Append(Lang.Text("Instance.ModpackUpdate.Section.Updated"),
            changes.Where(change => change.Incoming is not null && change.Before is not null));
        Append(Lang.Text("Instance.ModpackUpdate.Section.Removed"),
            changes.Where(change => change.Incoming is null));
        Append(Lang.Text("Instance.ModpackUpdate.Section.Preserved"), preserved);
        return string.Join("\n", lines);
    }

    private static bool ShowPreview(PackUpdatePlan plan) =>
        LauncherCommon.RunInUiWait(() =>
            (DialogService.Instance.Show(new MarkdownDialogRequest
            {
                Title = Lang.Text("Instance.ModpackUpdate.Preview.Title"),
                Content = BuildPreview(plan),
                Buttons = DialogButtons.Create(
                    Lang.Text("Instance.ModpackUpdate.Confirm"),
                    Lang.Text("Common.Action.Cancel")),
                ForceWait = true
            }).Result?.Button ?? 0) == 1);

    private static bool SameVersion(string left, string right) =>
        string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Confirm(string message, string title, string confirmText, string cancelText,
        bool isWarn) =>
        LauncherCommon.RunInUiWait(() =>
            CompUi.Current?.ShowDialog(message, title, confirmText, cancelText, "", isWarn) ?? -1) == 1;

    private static string? SelectArchive(string title) => LauncherCommon.RunInUiWait(() =>
    {
        var file = SystemDialogs.SelectFile(Lang.Text("Instance.ModpackUpdate.Filter"), title);
        return string.IsNullOrEmpty(file) ? null : file;
    });

    /// <summary>先选更新包来源：本地文件或直接下载链接。</summary>
    private static string? ResolveArchive(string stage, CancellationToken token)
    {
        var source = LauncherCommon.RunInUiWait(() =>
            CompUi.Current?.ShowDialog(
                Lang.Text("Instance.ModpackUpdate.Source.Content"),
                Lang.Text("Instance.ModpackUpdate.Source.Title"),
                Lang.Text("Instance.ModpackUpdate.Source.Local"),
                Lang.Text("Instance.ModpackUpdate.Source.Link"),
                "", false) ?? -1);
        return source switch
        {
            1 => SelectArchive(Lang.Text("Instance.ModpackUpdate.SelectNew.Title")),
            2 => DownloadArchive(stage, token),
            _ => null
        };
    }

    private static string? DownloadArchive(string stage, CancellationToken token)
    {
        var url = LauncherCommon.RunInUiWait(() =>
        {
            var result = DialogService.Instance.Show(new InputDialogRequest
            {
                Title = Lang.Text("Instance.ModpackUpdate.Source.LinkTitle"),
                Content = Lang.Text("Instance.ModpackUpdate.Source.LinkContent"),
                HintText = Lang.Text("Instance.ModpackUpdate.Source.LinkHint"),
                Buttons = DialogButtons.Create(
                    Lang.Text("Instance.ModpackUpdate.Source.Download"),
                    Lang.Text("Common.Action.Cancel")),
                ForceWait = true
            }).Result;
            if ((result?.Button ?? 0) != 1) return null;
            return result?.InputText?.Trim();
        });
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new IOException(Lang.Text("Instance.ModpackUpdate.Source.InvalidUrl"));

        var destination = Path.Combine(stage, "package.zip");
        try
        {
            using var response = NetworkService.GetClient()
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).GetAwaiter().GetResult();
            Requester.EnsureSuccess(response);
            using var input = response.Content.ReadAsStream(token);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                81920);
            input.CopyTo(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException(Lang.Text("Instance.ModpackUpdate.Source.DownloadFailed", ex.Message), ex);
        }

        return destination;
    }
}
