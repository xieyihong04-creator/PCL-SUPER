using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>实例的环境还原点列表：创建、恢复与删除。</summary>
public partial class PageInstanceHistory : IPageView
{
    private sealed record HistoryPoint(
        string Id,
        DateTime CreatedLocal,
        string Label,
        string Description,
        bool Healthy,
        bool Manual);

    private CancellationTokenSource? _refreshToken;

    public PageInstanceHistory()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            EnvironmentHistoryBridge.Changed -= OnHistoryChanged;
            EnvironmentHistoryBridge.Changed += OnHistoryChanged;
            RefreshList();
        };
        Unloaded += (_, _) => EnvironmentHistoryBridge.Changed -= OnHistoryChanged;
    }

    public void OnNavigatedTo(object? parameter) => RefreshList();

    public void OnNavigatedFrom()
    {
    }

    /// <summary>供左侧导航的刷新按钮重新读取列表。</summary>
    public void RefreshContent() => RefreshList();

    private void OnHistoryChanged()
    {
        if (!IsLoaded) return;
        LauncherCommon.RunInUi(RefreshList);
    }

    private void BtnCapture_Click(object sender, MouseButtonEventArgs e)
    {
        if (PageInstanceLeft.McInstance is null) return;
        EnvironmentHistoryBridge.Capture(PageInstanceLeft.McInstance);
    }

    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e) => RefreshList();

    private void BtnOpenFolder_Click(object sender, MouseButtonEventArgs e)
    {
        if (PageInstanceLeft.McInstance is null) return;
        EnvironmentHistoryBridge.OpenFolder(PageInstanceLeft.McInstance);
    }

    private async void RefreshList()
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;

        _refreshToken?.Cancel();
        var token = _refreshToken = new CancellationTokenSource();
        try
        {
            var points = await Task.Run(() =>
            {
                var result = new List<HistoryPoint>();
                foreach (var point in EnvironmentHistoryBridge.Engine.Points(instance.PathInstance))
                {
                    token.Token.ThrowIfCancellationRequested();
                    var id = point["id"]?.ToString();
                    if (string.IsNullOrEmpty(id)) continue;
                    result.Add(new HistoryPoint(
                        id,
                        ((DateTime?)point["created"])?.ToLocalTime() ?? DateTime.MinValue,
                        point["label"]?.ToString() ?? "",
                        point["description"]?.ToString() ?? "",
                        (bool?)point["healthy"] ?? false,
                        (bool?)point["manual"] ?? true));
                }

                return result;
            }, token.Token);

            if (token.IsCancellationRequested) return;
            Render(points);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Common.Log(ex, "读取环境还原点失败", Common.LogLevel.Hint,
                userSummary: "读取环境还原点失败：" + ex.Message);
        }
    }

    private void Render(IReadOnlyList<HistoryPoint> points)
    {
        PanPoints.Children.Clear();
        LabEmpty.Visibility = points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var point in points)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = point.CreatedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) + " · " + point.Label,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var details = new List<string>();
            if (!point.Manual) details.Add(Lang.Text("Instance.History.Automatic"));
            if (point.Healthy) details.Add(Lang.Text("Instance.History.Healthy"));
            if (!string.IsNullOrWhiteSpace(point.Description)) details.Add(point.Description);
            if (details.Count > 0)
                info.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", details),
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12
                });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var btnRestore = new MyButton
            {
                Text = Lang.Text("Instance.History.Restore"),
                MinWidth = 82,
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                ColorType = MyButton.ColorState.Highlight,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            btnRestore.Click += (_, _) => Restore(point.Id);
            Grid.SetColumn(btnRestore, 1);
            row.Children.Add(btnRestore);

            var btnDelete = new MyButton
            {
                Text = Lang.Text("Instance.History.Delete"),
                MinWidth = 70,
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                ColorType = MyButton.ColorState.Red,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            btnDelete.Click += (_, _) => Delete(point.Id);
            Grid.SetColumn(btnDelete, 2);
            row.Children.Add(btnDelete);

            PanPoints.Children.Add(row);
            PanPoints.Children.Add(new Border
            {
                Height = 1,
                Opacity = 0.12,
                Background = TryFindResource("ColorBrush3") as System.Windows.Media.Brush
            });
        }
    }

    // 重复点击由引擎的 BusyCount 拦截，这里只负责转发。
    private void Restore(string pointId)
    {
        if (PageInstanceLeft.McInstance is not null)
            EnvironmentHistoryBridge.Restore(PageInstanceLeft.McInstance, pointId);
    }

    private void Delete(string pointId)
    {
        if (PageInstanceLeft.McInstance is not null)
            EnvironmentHistoryBridge.Delete(PageInstanceLeft.McInstance, pointId);
    }
}
