using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.UI.Hints;
using PCL.Living.Features;
using Binding = PCL.Living.Features.InstanceKeybindingData;

namespace PCL;

/// <summary>实例的按键列表：读取 options.txt、显示归属与冲突，并支持修改单个按键。</summary>
public partial class PageInstanceKeybinding : IPageView
{
    private readonly object _saveGate = new();
    private List<Binding> _entries = [];
    private CancellationTokenSource? _loadToken;

    public PageInstanceKeybinding()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _loadToken?.Cancel();
    }

    public void OnNavigatedTo(object? parameter) => Reload();

    public void OnNavigatedFrom()
    {
    }

    /// <summary>供左侧导航的刷新按钮重新读取配置。</summary>
    public void RefreshContent() => Reload();

    private void SearchChanged(object? sender, EventArgs e) => Render();

    private void ConflictsChanged(object? sender, bool user) => Render();

    private void Reload()
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;
        _loadToken?.Cancel();
        var token = _loadToken = new CancellationTokenSource();
        var gameFolder = instance.PathIndie;
        LabStatus.Text = Lang.Text("Instance.Keybinding.Loading");
        PanKeys.Children.Clear();

        Task.Run(() =>
        {
            try
            {
                var entries = InstanceKeybindingReader.ReadBindings(gameFolder, token.Token,
                    (root, file) => EnvironmentHistoryBridge.EnsureWithin(root, file),
                    (error, message) => Common.Log(error, message),
                    NormalizeTitle,
                    () => new Binding());
                if (token.IsCancellationRequested) return;
                LauncherCommon.RunInUi(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _entries = entries;
                    Render();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Common.Log(ex, "读取按键配置失败", Common.LogLevel.Hint,
                    userSummary: "读取按键配置失败：" + ex.Message);
                LauncherCommon.RunInUi(() => LabStatus.Text = Lang.Text("Instance.Keybinding.Error"));
            }
        }, token.Token);
    }

    private static string NormalizeTitle(string title) =>
        Regex.Replace(title, "\r\n|[\n\r\f\u0085\u2028\u2029]", " ");

    private void Render()
    {
        if (!IsLoaded) return;
        var view = InstanceKeybindingView.BuildView(_entries, SearchBox.Text?.Trim() ?? "",
            CheckConflicts.Checked == true);
        PanKeys.Children.Clear();
        LabStatus.Text = Lang.Text("Instance.Keybinding.Status", view.VisibleCount, view.ConflictCount);

        foreach (var block in view.Blocks)
        {
            if (block.IsGroup)
            {
                PanKeys.Children.Add(new TextBlock
                {
                    Text = Lang.Text("Instance.Keybinding.ConflictGroup",
                        InstanceKeybindingView.DisplayKey(block.Normalized), block.TotalCount),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 10, 0, 4)
                });
            }

            foreach (var entry in block.Members)
                PanKeys.Children.Add(BuildRow(entry));
        }

        if (view.VisibleCount == 0)
            PanKeys.Children.Add(new TextBlock
            {
                Text = Lang.Text("Instance.Keybinding.Empty"),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
    }

    private FrameworkElement BuildRow(Binding entry)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = entry.Name ?? entry.Key ?? "",
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var details = new List<string> { entry.Owners };
        if (entry.SharedCount > 1)
            details.Add(Lang.Text("Instance.Keybinding.Shared", entry.SharedCount));
        if (!entry.Saved) details.Add(Lang.Text("Instance.Keybinding.NotSaved"));
        info.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", details),
            Opacity = 0.65,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(info, 0);
        row.Children.Add(info);

        var button = new MyButton
        {
            Text = InstanceKeybindingView.DisplayKey(entry.Value),
            MinWidth = 110,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ColorType = entry.SharedCount > 1 ? MyButton.ColorState.Red : MyButton.ColorState.Normal
        };
        button.Click += (_, _) => ChangeBinding(entry);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    private void ChangeBinding(Binding entry)
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null || entry.Key is null) return;
        var input = CompUi.Current?.ShowInput(
            Lang.Text("Instance.Keybinding.Change.Title"),
            Lang.Text("Instance.Keybinding.Change.Message"),
            entry.Value ?? "");
        if (string.IsNullOrWhiteSpace(input) || input.Trim() == entry.Value) return;

        var replacement = input.Trim();
        if (PersonalOptionsRules.NormalizeBinding(replacement) is null)
        {
            HintService.Show(Hints.Error(Lang.Text("Instance.Keybinding.Change.Invalid")));
            return;
        }

        try
        {
            InstanceKeybindingFiles.Save(instance.PathIndie, entry.Key, entry.Value, replacement, _saveGate,
                () => GameLaunch.isLaunching || GameLaunch.mcLaunchProcess is { HasExited: false },
                (root, file) => EnvironmentHistoryBridge.EnsureWithin(root, file),
                _ => { });
            HintService.Show(Hints.Info(Lang.Text("Instance.Keybinding.Change.Saved")));
            Reload();
        }
        catch (Exception ex)
        {
            Common.Log(ex, "修改按键失败", Common.LogLevel.Hint,
                userSummary: "修改按键失败：" + ex.Message);
        }
    }
}
