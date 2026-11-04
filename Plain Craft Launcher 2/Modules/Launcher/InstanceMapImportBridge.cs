using System.IO;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Core.UI.Hints;
using PCL.Living.Features;

namespace PCL;

/// <summary>
///     把 LF 的地图存档导入接到 CE 存档页：校验压缩包只含单个存档、拒绝越界路径，
///     重名自动顺延编号，不覆盖已有存档。
/// </summary>
internal static class InstanceMapImportBridge
{
    internal static void Import(McInstance? instance)
    {
        if (instance is null)
            return;
        var files = LauncherCommon.RunInUiWait(() => SystemDialogs.SelectFiles(
            Lang.Text("Instance.Saves.Import.Filter"),
            Lang.Text("Instance.Saves.Import.Title")));
        if (files.Length == 0)
            return;

        var saves = Path.Combine(instance.PathIndie, "saves");
        var loader = new Loading.LoaderTask<string, string>(
            Lang.Text("Instance.Saves.Import.Task", instance.Name),
            task =>
            {
                try
                {
                    var installer = new MapDownloadInstaller(new MapDownloadHost
                    {
                        EnsureWithin = EnvironmentHistoryBridge.EnsureWithin
                    });
                    var imported = new List<string>();
                    foreach (var file in files)
                    {
                        task.AbortedToken.ThrowIfCancellationRequested();
                        imported.Add(Path.GetFileName(installer.Install(file, saves, task.AbortedToken)));
                    }

                    LauncherCommon.RunInUi(() =>
                    {
                        HintService.Show(Hints.Success(imported.Count == 1
                            ? Lang.Text("Instance.Saves.Import.Success", imported[0])
                            : Lang.Text("Instance.Saves.Import.SuccessCount", imported.Count)));
                        Launcher.frmMain?.PageNameRefresh();
                    });
                }
                catch (OperationCanceledException)
                {
                    // 玩家中止，属于正常流程。
                }
                catch (Exception ex)
                {
                    Common.Log(ex, "导入地图存档失败", Common.LogLevel.Hint,
                        userSummary: Lang.Text("Instance.Saves.Import.Failed", ex.Message));
                }
            });
        loader.Start();
    }
}
