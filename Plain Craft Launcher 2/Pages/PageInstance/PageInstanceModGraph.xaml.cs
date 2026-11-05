using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>模组必需依赖的分层关系图：左侧为被依赖方，右侧为依赖方。</summary>
public partial class PageInstanceModGraph : IPageView
{
    private const double NodeWidth = 210d;
    private const double NodeHeight = 54d;
    private const double GapX = 110d;
    private const double GapY = 14d;
    private const double MarginSize = 16d;

    private CancellationTokenSource? _loadToken;

    public PageInstanceModGraph()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _loadToken?.Cancel();
    }

    public void OnNavigatedTo(object? parameter) => Reload();

    public void OnNavigatedFrom()
    {
    }

    /// <summary>供左侧导航的刷新按钮重新读取依赖。</summary>
    public void RefreshContent() => Reload();

    private void BtnRefreshGraph_Click(object sender, MouseButtonEventArgs e) => Reload();

    private void Reload()
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;
        _loadToken?.Cancel();
        var token = _loadToken = new CancellationTokenSource();
        LabSummary.Text = Lang.Text("Instance.ModGraph.Loading");
        PanGraph.Children.Clear();

        Task.Run(() =>
        {
            try
            {
                var graph = ModDependencyGraphService.Build(instance, token.Token);
                if (token.IsCancellationRequested) return;
                LauncherCommon.RunInUi(() =>
                {
                    if (token.IsCancellationRequested) return;
                    Draw(graph);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Common.Log(ex, "读取依赖关系失败", Common.LogLevel.Hint,
                    userSummary: "读取依赖关系失败：" + ex.Message);
                LauncherCommon.RunInUi(() => LabSummary.Text = Lang.Text("Instance.ModGraph.Error"));
            }
        }, token.Token);
    }

    private void Draw(ModDependencyGraph graph)
    {
        PanGraph.Children.Clear();
        const int maxNodes = 160;
        var nodes = graph.Nodes.Take(maxNodes).ToList();
        var known = new HashSet<string>(nodes.Select(node => node.Key), StringComparer.Ordinal);
        var edges = graph.Edges
            .Where(edge => known.Contains(edge.FromKey) && known.Contains(edge.ToKey) && edge.FromKey != edge.ToKey)
            .ToList();

        LabSummary.Text = graph.Summary + (graph.Nodes.Count > maxNodes
            ? " " + Lang.Text("Instance.ModGraph.Truncated", maxNodes)
            : "");

        if (nodes.Count == 0)
        {
            PanGraph.Children.Add(new TextBlock
            {
                Text = Lang.Text("Instance.ModGraph.Empty"),
                Margin = new Thickness(MarginSize),
                Opacity = 0.7
            });
            return;
        }

        var outgoing = edges
            .GroupBy(edge => edge.FromKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.ToKey).Distinct().ToList(),
                StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);

        int Depth(string key, HashSet<string> path)
        {
            if (depth.TryGetValue(key, out var cached)) return cached;
            if (!path.Add(key)) return 0;
            var result = 0;
            if (outgoing.TryGetValue(key, out var targets))
                foreach (var target in targets)
                    result = Math.Max(result, Depth(target, path) + 1);
            path.Remove(key);
            depth[key] = result;
            return result;
        }

        foreach (var node in nodes) Depth(node.Key, new HashSet<string>(StringComparer.Ordinal));

        var columns = nodes
            .GroupBy(node => depth.GetValueOrDefault(node.Key))
            .OrderBy(group => group.Key)
            .ToList();
        var positions = new Dictionary<string, (double X, double Y, ModDependencyGraphNode Node)>(StringComparer.Ordinal);
        var maxHeight = 0d;
        foreach (var column in columns)
        {
            var order = column
                .OrderBy(node => node.IsBuiltin)
                .ThenBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var y = MarginSize;
            foreach (var node in order)
            {
                var x = MarginSize + column.Key * (NodeWidth + GapX);
                positions[node.Key] = (x, y, node);
                y += NodeHeight + GapY;
            }

            maxHeight = Math.Max(maxHeight, y);
        }

        var width = columns.Count * (NodeWidth + GapX) + MarginSize;
        PanGraph.Width = width;
        PanGraph.Height = maxHeight + MarginSize;

        var missing = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
            if (edge.State != "Satisfied")
                missing[edge.ToKey] = missing.GetValueOrDefault(edge.ToKey) + 1;

        var lineBrush = TryFindResource("ColorBrush3") as Brush ?? Brushes.Gray;
        var errorBrush = TryFindResource("ColorBrushRedBack") as Brush ?? Brushes.IndianRed;

        foreach (var edge in edges)
        {
            if (!positions.TryGetValue(edge.FromKey, out var from) ||
                !positions.TryGetValue(edge.ToKey, out var to))
                continue;
            var start = new Point(from.X, from.Y + NodeHeight / 2d);
            var end = new Point(to.X + NodeWidth, to.Y + NodeHeight / 2d);
            var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new BezierSegment(
                new Point(start.X - 60d, start.Y),
                new Point(end.X + 60d, end.Y),
                end, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            PanGraph.Children.Add(new Path
            {
                Data = geometry,
                Stroke = edge.State == "Satisfied" ? lineBrush : errorBrush,
                StrokeThickness = edge.State == "Satisfied" ? 1.2d : 2d,
                Opacity = edge.State == "Satisfied" ? 0.45d : 0.85d,
                ToolTip = edge.Requirement
            });
        }

        foreach (var (_, entry) in positions.OrderBy(pair => pair.Value.Y))
        {
            var node = entry.Node;
            var border = new Border
            {
                Width = NodeWidth,
                Height = NodeHeight,
                CornerRadius = new CornerRadius(6d),
                BorderThickness = new Thickness(missing.ContainsKey(node.Key) ? 2d : 1d),
                Padding = new Thickness(9d, 5d, 9d, 5d),
                Background = TryFindResource("ColorBrush2") as Brush,
                BorderBrush = missing.ContainsKey(node.Key) ? errorBrush : lineBrush,
                Opacity = node.Enabled ? 1d : 0.55d,
                ToolTip = node.Id + " · " + node.FileName + (node.Enabled ? "" : " · " +
                    Lang.Text("Instance.ModGraph.Disabled")),
                Cursor = Cursors.Hand
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = node.Name,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(new TextBlock
            {
                Text = node.FileName,
                FontSize = 11d,
                Opacity = 0.65d,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            border.Child = stack;
            border.MouseLeftButtonUp += (_, _) => ShowDetails(node, missing.GetValueOrDefault(node.Key));
            Canvas.SetLeft(border, entry.X);
            Canvas.SetTop(border, entry.Y);
            PanGraph.Children.Add(border);
        }
    }

    private static void ShowDetails(ModDependencyGraphNode node, int missing)
    {
        var lines = new List<string>
        {
            node.Name,
            Lang.Text("Instance.ModGraph.Detail.Id", node.Id),
            Lang.Text("Instance.ModGraph.Detail.File", node.FileName)
        };
        if (!node.Enabled) lines.Add(Lang.Text("Instance.ModGraph.Detail.Disabled"));
        if (node.IsBuiltin) lines.Add(Lang.Text("Instance.ModGraph.Detail.Builtin"));
        if (missing > 0) lines.Add(Lang.Text("Instance.ModGraph.Detail.Missing", missing));
        CompUi.Current?.ShowDialog(string.Join("\r\n", lines), Lang.Text("Instance.ModGraph.Title"), "确定",
            isWarn: missing > 0);
    }
}
