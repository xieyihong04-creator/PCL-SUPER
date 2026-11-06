namespace PCL;

/// <summary>更新业务向 UI 层发出的通知。</summary>
public interface IUpdateUi
{
    /// <summary>启动器是否为前台活动窗口且没有处于游戏启动/运行中。</summary>
    bool IsUserPresent { get; }

    /// <summary>用户不在场时暂缓执行，回到前台后再执行一次。</summary>
    void RunWhenUserPresent(Action action);

    int ShowMessageBox(
        string caption,
        string? title = null,
        string? button1 = null,
        string? button2 = "",
        string? button3 = "",
        bool isWarn = false,
        Action? button1Action = null,
        Action? button2Action = null,
        Action? button3Action = null);

    void ShowHint(string text, NotificationType type = NotificationType.Info);
    void NavigateSetupUpdate();
    void ShowUpdateRestartButton(string tooltip);
    void RefreshDownloadButton();
    void UpdateSetupButton(string text, bool enabled);
    void ExitApplication(bool restart);
}

public static class UpdateUi
{
    public static IUpdateUi? Current { get; set; }
}

/// <summary>公告按钮动作的处理者。</summary>
public interface IAnnouncementActionHandler
{
    void Execute(string command, string? parameter);
}

public static class AnnouncementActions
{
    public static IAnnouncementActionHandler? Current { get; set; }
}