using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCL.Core.App;
using PCL.Core.UI.Hints;
using PCL.Living.Features;

namespace PCL;

/// <summary>
///     把 LF 的环境还原点引擎接到 CE 的实例、界面与任务系统上。
///     引擎本身（内容寻址存储、跨进程租约、覆盖范围、保留策略）不做改动。
/// </summary>
internal static class EnvironmentHistoryBridge
{
    private static readonly object Gate = new();
    private static EnvironmentHistoryEngine<McInstance>? _engine;
    private static EnvironmentHistoryWorkflow<McInstance>? _workflow;

    /// <summary>还原点列表发生变化（创建、恢复、删除或手动刷新）。</summary>
    internal static event Action? Changed;

    internal static EnvironmentHistoryEngine<McInstance> Engine => _engine ??= new EnvironmentHistoryEngine<McInstance>(Gate, CreateEngineHost());

    internal static EnvironmentHistoryWorkflow<McInstance> Workflow =>
        _workflow ??= new EnvironmentHistoryWorkflow<McInstance>(Engine, CreateWorkflowHost());

    internal static string PendingFolder(string versionFolder) =>
        Path.Combine(Engine.Store(versionFolder), "pending");

    internal static void NotifyChanged() => Changed?.Invoke();

    /// <summary>创建还原点；在后台任务中执行，完成后刷新列表。</summary>
    internal static void Capture(McInstance? instance) => Workflow.Start(instance, false);

    /// <summary>把实例恢复到指定还原点。</summary>
    internal static void Restore(McInstance? instance, string pointId) => Workflow.Start(instance, true, pointId);

    /// <summary>删除指定还原点（只删除记录，不删除游戏文件）。</summary>
    internal static void Delete(McInstance? instance, string pointId) => Workflow.Start(instance, false, null, [pointId]);

    /// <summary>
    ///     在替换资源文件前同步保存一次自动还原点（调用方应处于后台任务中）。
    ///     失败只提示，不阻断后续更新。
    /// </summary>
    internal static void CaptureBeforeUpdate(McInstance instance, string label)
    {
        try
        {
            Engine.CaptureInstance(instance, label, CancellationToken.None);
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Common.Log(ex, $"保存还原点失败（{label}）", Common.LogLevel.Hint,
                userSummary: $"更新前保存还原点失败：{ex.Message}");
        }
    }

    internal static void OpenFolder(McInstance instance) => Engine.OpenFolder(instance.PathInstance);

    #region 引擎宿主

    private static EnvironmentHistoryHost<McInstance> CreateEngineHost() => new()
    {
        DataRoot = () =>
        {
            Directory.CreateDirectory(Paths.Data);
            return Paths.Data;
        },
        Hash = bytes => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
        AtomicWrite = AtomicWrite,
        Target = Target,
        RelativePath = RelativePath,
        AddSlashSuffix = path => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar,
        RemoveExtendedPrefix = RemoveExtendedPrefix,
        SafeName = SafeName,
        Digest = Digest,
        EnsureNoLinks = EnsureNoLinks,
        EnsureWithin = EnsureWithin,
        IsParentOf = IsParentOf,
        SamePath = SamePath,
        ApplyTransaction = ApplyTransaction,
        VersionPath = instance => instance.PathInstance,
        GameFolder = instance => instance.PathIndie,
        VanillaName = instance => instance.Info.VanillaName,
        LibraryPaths = instance => Libraries.McLibListGet(instance, true)
            .Where(token => !token.IsNatives && !string.IsNullOrEmpty(token.LocalPath))
            .Select(token => token.LocalPath)
            .ToList(),
        ModLoaderVersions = instance => new[]
            {
                ("Forge", instance.Info.Forge),
                ("NeoForge", instance.Info.NeoForge),
                ("Fabric", instance.Info.Fabric),
                ("Quilt", instance.Info.Quilt),
                ("OptiFine", instance.Info.OptiFine),
                ("Cleanroom", instance.Info.Cleanroom)
            }
            .Where(pair => !string.IsNullOrEmpty(pair.Item2))
            .Select(pair => pair.Item1 + " " + pair.Item2)
            .DefaultIfEmpty("未安装加载器")
            .ToList(),
        JsonObject = instance => JObject.Parse(instance.JsonObject.ToString()),
        // 自用版本不自动删除任何还原点，保留全部手动与自动记录。
        AutoCleanup = _ => false,
        Retention = _ => 10,
        Info = message => LauncherCommon.RunInUi(() => HintService.Show(Hints.Info(message))),
        OpenFolder = path => Common.OpenExplorer(path),
        Hint = message => LauncherCommon.RunInUi(() => HintService.Show(Hints.Info(message))),
        Warn = (error, message) => Common.Log(error, message, Common.LogLevel.Hint, userSummary: message)
    };

    #endregion

    #region 任务与界面宿主

    private static EnvironmentHistoryWorkflowHost<McInstance> CreateWorkflowHost() => new()
    {
        HasRunningGame = () => GameLaunch.isLaunching || GameLaunch.mcLaunchProcess is { HasExited: false },
        HasRunningSessions = () => false,
        LaunchLoading = () => GameLaunch.isLaunching,
        Updating = () => false,
        AnyLocked = () => false,
        UpdatingModFolders = () => false,
        VersionPath = instance => instance.PathInstance,
        GameFolder = instance => instance.PathIndie,
        Name = instance => instance.Name,
        HasPending = HasPending,
        RecoverInstance = RecoverInstance,
        SamePath = SamePath,
        Confirm = Confirm,
        SelectPoint = SelectPoint,
        HintError = message => LauncherCommon.RunInUi(() => HintService.Show(Hints.Error(message))),
        Dispatch = action => LauncherCommon.RunInUi(action),
        DispatchWait = action => LauncherCommon.RunInUiWait(action),
        NotifyChanged = () =>
        {
            NotifyChanged();
            Launcher.frmMain?.PageNameRefresh();
        },
        ResetSettingsCache = _ => { },
        CreateLoadedInstance = versionFolder =>
        {
            var instance = new McInstance(versionFolder);
            instance.Load();
            return instance;
        },
        ClearIniCache = _ => { },
        PageInstance = () => PageInstanceLeft.McInstance,
        SelectedInstance = () => InstanceList.McMcInstanceSelected,
        SetPageInstance = instance => PageInstanceLeft.McInstance = instance,
        SetSelectedInstance = instance => InstanceList.McMcInstanceSelected = instance,
        RefreshPageName = () => Launcher.frmMain?.PageNameRefresh(),
        StartWork = (_, work) => StartWork(work)
    };

    private static int Confirm(string caption, string title, string button1, string button2, bool isWarn) =>
        LauncherCommon.RunInUiWait(() =>
            CompUi.Current?.ShowDialog(caption, title, button1, button2, "", isWarn) ?? -1);

    private static int? SelectPoint(IEnumerable<EnvironmentHistoryChoice> choices)
    {
        var options = choices.Select(choice => choice.Text).ToList();
        if (options.Count == 0) return null;
        return LauncherCommon.RunInUiWait(() =>
            CompUi.Current?.ShowSelect("请选择要恢复的还原点。", "恢复游戏环境", options) ?? null);
    }

    private static bool StartWork(EnvironmentHistoryWork work)
    {
        var loader = new Loading.LoaderTask<string, string>(work.StepName, task =>
        {
            try
            {
                string? output = null;
                work.Execute(task.AbortedToken, value => output = value ?? output);
                task.output = output ?? "";
            }
            catch (OperationCanceledException)
            {
                // 玩家在确认框中取消，属于正常流程。
            }
            catch (Exception ex)
            {
                Common.Log(ex, "环境还原点操作失败", Common.LogLevel.Msgbox,
                    userSummary: "环境还原点操作失败：" + ex.Message);
                throw;
            }
            finally
            {
                work.Stopped?.Invoke();
                NotifyChanged();
            }
        });
        loader.Start();
        return true;
    }

    #endregion

    #region 路径与哈希

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string RemoveExtendedPrefix(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;

    private static string Target(string root, string relative)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithin(root, combined);
        return combined;
    }

    private static string RelativePath(string value)
    {
        if (Path.IsPathRooted(value)) throw new IOException("还原点中的文件路径无效。");
        var normalized = value.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0) throw new IOException("还原点中的文件路径无效。");
        foreach (var segment in normalized.Split('/'))
            if (segment is "" or "." or "..")
                throw new IOException("还原点中的文件路径无效。");
        return normalized;
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("版本名称无效：" + name);
        return name;
    }

    /// <summary>返回 sha1:&lt;40 位小写十六进制&gt;；文件不存在时为 null。</summary>
    private static string? Digest(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return "sha1:" + Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private static void EnsureNoLinks(string path)
    {
        var full = Path.GetFullPath(RemoveExtendedPrefix(path));
        FileSystemInfo? current = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("路径包含符号链接或目录联接：" + current.FullName);
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
    }

    internal static void EnsureWithin(string root, string path)
    {
        var trimmedRoot = Path.GetFullPath(RemoveExtendedPrefix(root))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(RemoveExtendedPrefix(path));
        if (IsParentOf(trimmedRoot, full) || SamePath(trimmedRoot, full)) return;
        throw new IOException("路径超出允许范围：" + full);
    }

    private static bool IsParentOf(string parent, string child)
    {
        var trimmed = Path.GetFullPath(RemoveExtendedPrefix(parent))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(RemoveExtendedPrefix(child));
        return full.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(
            Path.GetFullPath(RemoveExtendedPrefix(first)).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(RemoveExtendedPrefix(second)).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    #endregion

    #region 事务与中断恢复

    private sealed class PendingEntry
    {
        public string Destination = "";
        public string? Backup;
        public string? Stage;
    }

    /// <summary>
    ///     先校验、再落盘：所有目标文件先复制到同级临时文件，成功后才替换；
    ///     任一步失败按逆序回滚。操作前写入 pending 日志，进程意外退出后可在下次恢复。
    /// </summary>
    internal static void ApplyTransaction(string gameRoot, string versionFolder,
        List<Tuple<string, string?, string?>> operations, CancellationToken token, Action<int>? progress)
    {
        if (operations.Count == 0) return;
        var pendingFolder = PendingFolder(versionFolder);
        Directory.CreateDirectory(pendingFolder);
        var journalPath = Path.Combine(pendingFolder, Guid.NewGuid().ToString("N") + ".json");
        var entries = new List<PendingEntry>();

        try
        {
            foreach (var operation in operations)
            {
                token.ThrowIfCancellationRequested();
                var destination = operation.Item1;
                EnsureNoLinks(destination);
                if (operation.Item3 is not null && !string.Equals(Digest(destination), operation.Item3,
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException("恢复期间文件发生变化，已停止：" + Path.GetFileName(destination));
            }

            foreach (var operation in operations)
            {
                token.ThrowIfCancellationRequested();
                var destination = operation.Item1;
                var entry = new PendingEntry { Destination = destination };
                if (operation.Item2 is not null)
                {
                    var folder = Path.GetDirectoryName(destination)!;
                    Directory.CreateDirectory(folder);
                    var stage = destination + ".pcl-stage-" + Guid.NewGuid().ToString("N");
                    File.Copy(operation.Item2, stage, false);
                    entry.Stage = stage;
                }

                if (File.Exists(destination))
                {
                    var backup = destination + ".pcl-backup-" + Guid.NewGuid().ToString("N");
                    File.Copy(destination, backup, false);
                    entry.Backup = backup;
                }

                entries.Add(entry);
            }

            File.WriteAllText(journalPath, SerializePending(entries), new UTF8Encoding(false));

            for (var index = 0; index < entries.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = entries[index];
                if (entry.Stage is not null) File.Move(entry.Stage, entry.Destination, true);
                else if (File.Exists(entry.Destination)) File.Delete(entry.Destination);
                entry.Stage = null;
                progress?.Invoke(index + 1);
            }

            Cleanup(entries);
            File.Delete(journalPath);
        }
        catch
        {
            Rollback(entries);
            if (File.Exists(journalPath)) File.Delete(journalPath);
            throw;
        }
    }

    internal static bool HasPending(string versionFolder)
    {
        var folder = PendingFolder(versionFolder);
        return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.json").Any();
    }

    private static McInstance RecoverInstance(McInstance instance)
    {
        var folder = PendingFolder(instance.PathInstance);
        if (Directory.Exists(folder))
        {
            foreach (var journal in Directory.EnumerateFiles(folder, "*.json").ToList())
            {
                var entries = ParsePending(File.ReadAllText(journal));
                Rollback(entries);
                File.Delete(journal);
            }

            Common.Log("[EnvironmentHistory] 已回滚上次中断的环境恢复。");
        }

        var reloaded = new McInstance(instance.PathInstance);
        reloaded.Load();
        return reloaded;
    }

    private static void Rollback(IEnumerable<PendingEntry> entries)
    {
        foreach (var entry in entries.Reverse())
        {
            try
            {
                // Stage 仍存在说明该条尚未应用，删除暂存文件即可；
                // Stage 已消失则说明文件已被替换，需要按备份还原或删除新增文件。
                var stagePending = entry.Stage is not null && File.Exists(entry.Stage);
                if (stagePending) File.Delete(entry.Stage!);
                if (entry.Backup is not null && File.Exists(entry.Backup))
                    File.Move(entry.Backup, entry.Destination, true);
                else if (!stagePending && entry.Backup is null && File.Exists(entry.Destination))
                    File.Delete(entry.Destination);
            }
            catch (Exception ex)
            {
                Common.Log(ex, "回滚环境文件失败：" + entry.Destination, Common.LogLevel.Hint,
                    userSummary: "回滚环境文件失败，请检查：" + entry.Destination);
            }
        }
    }

    private static void Cleanup(IEnumerable<PendingEntry> entries)
    {
        foreach (var entry in entries)
            try
            {
                if (entry.Stage is not null && File.Exists(entry.Stage)) File.Delete(entry.Stage);
                if (entry.Backup is not null && File.Exists(entry.Backup)) File.Delete(entry.Backup);
            }
            catch (Exception ex)
            {
                Common.Log(ex, "清理环境恢复临时文件失败：" + entry.Destination);
            }
    }

    private static string SerializePending(IEnumerable<PendingEntry> entries)
    {
        var array = new JArray();
        foreach (var entry in entries)
            array.Add(new JObject
            {
                ["destination"] = entry.Destination,
                ["backup"] = entry.Backup,
                ["stage"] = entry.Stage
            });
        return new JObject { ["entries"] = array }.ToString();
    }

    private static List<PendingEntry> ParsePending(string json)
    {
        var result = new List<PendingEntry>();
        foreach (var item in (JArray)JObject.Parse(json)["entries"]!)
            result.Add(new PendingEntry
            {
                Destination = item["destination"]?.ToString() ?? "",
                Backup = item["backup"]?.Type == JTokenType.String ? item["backup"]!.ToString() : null,
                Stage = item["stage"]?.Type == JTokenType.String ? item["stage"]!.ToString() : null
            });
        return result;
    }

    #endregion
}
