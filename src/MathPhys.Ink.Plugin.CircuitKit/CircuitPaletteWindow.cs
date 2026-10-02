using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Media;

namespace MathPhys.Ink.Plugin.CircuitKit;

/// <summary>
/// 元件调色板：每个元件一个按钮（矢量小图标 + 中文名），点选后到画布上落。
/// </summary>
/// <remarks>
/// 无模式窗口（画布必须保持可点）；面板保持打开支持连续落元件。
/// 按钮图标直接用 <see cref="CircuitSymbols"/> 的冻结几何（Path + Stroke），
/// 与画布上的形状天然一致 —— 改目录两处一起变。
/// </remarks>
internal sealed class CircuitPaletteWindow : Window
{
    /// <summary>老师点了某个元件（key 由工具接手）。</summary>
    public event Action<string>? SymbolPicked;

    private readonly Dictionary<string, Border> _cards = new();
    private string _selected;
    private readonly Brush _cardNormal = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)));
    private readonly Brush _cardSelected = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0xE9, 0xFF)));

    public CircuitPaletteWindow(string initialSymbol)
    {
        _selected = initialSymbol;

        Title = "电路元件";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(10) };

        // 导线放最前（用得最多）
        grid.Children.Add(MakeWireCard());
        foreach (var def in CircuitSymbols.All)
        {
            grid.Children.Add(MakeCard(def.Key, def.Name, def.StrokeGeom, def.Size));
        }

        var hint = new TextBlock
        {
            Text = "点选元件 → 到画布上点一下落下（可连续放）；导线按住拖动。",
            FontSize = 12,
            Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))),
            Margin = new Thickness(12, 0, 12, 10),
            TextWrapping = TextWrapping.Wrap,
        };

        Content = new StackPanel { Children = { grid, hint } };
        HighlightSelection();
    }

    // ---------------------------------------------------------------- 卡片

    private UIElement MakeCard(string key, string name, Geometry iconGeom, Size iconSize)
    {
        // 图标：把元件几何缩进 56×36 的框里（等比 + 居中）
        var icon = new Path
        {
            Data = iconGeom,
            Stroke = Brushes.Black,
            StrokeThickness = 1.2,
            Fill = null,
            Stretch = Stretch.Uniform,
            Width = 56,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = name,
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var stack = new StackPanel
        {
            Children = { icon, label },
            Margin = new Thickness(2),
        };

        var card = new Border
        {
            Child = stack,
            Background = _cardNormal,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 6, 4, 4),
            Margin = new Thickness(3),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        card.MouseLeftButtonUp += (_, _) =>
        {
            _selected = key;
            HighlightSelection();
            SymbolPicked?.Invoke(key);
        };

        _cards[key] = card;
        return card;
    }

    private UIElement MakeWireCard()
    {
        var wire = Geometry.Parse("M 4,18 L 52,18");
        return MakeCard(CircuitSymbols.WireKey, "导线（拖动）", wire, new Size(56, 36));
    }

    private void HighlightSelection()
    {
        foreach (var (key, card) in _cards)
        {
            card.Background = string.Equals(key, _selected, StringComparison.Ordinal)
                ? _cardSelected
                : _cardNormal;
        }
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }
}
