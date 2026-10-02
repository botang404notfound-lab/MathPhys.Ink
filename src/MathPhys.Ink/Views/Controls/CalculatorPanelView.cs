using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MathPhys.Ink.Design;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Views.Controls;

/// <summary>
/// 简易计算器面板（M13 S5）：乘除、开根号的按键式小面板 —— 课堂上临时算个数，
/// 一体机上没有物理键盘，按键必须是手指直接点的。
/// </summary>
/// <remarks>
/// 与图层面板同一挂法：纯代码建 UI、浮在 <c>RootGrid</c> 上的<b>屏幕坐标</b>元素。
/// 拖动由宿主经 <see cref="FloatingDrag"/> 挂在 <see cref="TitleBar"/> 上
/// （会话内记住位置即可，不落盘）。求值统一走 <see cref="ArithmeticEvaluator"/>。
/// </remarks>
public sealed class CalculatorPanelView : Border
{
    /// <summary>拖动把手（标题行）—— 宿主把它接给 FloatingDrag。</summary>
    public FrameworkElement TitleBar => _titleBar;

    private readonly Border _titleBar;
    private readonly TextBlock _expressionText;
    private readonly TextBlock _resultText;

    /// <summary>当前算式（显示与求值同源：× ÷ √ 直接进求值器的字符集）。</summary>
    private string _expression = string.Empty;

    public CalculatorPanelView()
    {
        Width = 264;
        // ★ M20 步 2b：底板接在设计令牌上（代码版 DynamicResource）
        this.FollowTheme(BackgroundProperty, "Ui.OverlayPanel");
        this.FollowTheme(BorderBrushProperty, "Ui.OverlayBorder");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = true;

        _expressionText = new TextBlock
        {
            Text = string.Empty,
            TextAlignment = TextAlignment.Right,
            FontSize = Tokens.Number("Text.Label"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        }
        .FollowTheme(TextBlock.ForegroundProperty, "Ui.TextSecondary");

        _resultText = new TextBlock
        {
            Text = "0",
            TextAlignment = TextAlignment.Right,
            FontSize = 24,
            FontWeight = FontWeights.Bold,
        }
        .FollowTheme(TextBlock.ForegroundProperty, "Ui.TextPrimary");

        var root = new StackPanel();

        // 标题行 = 拖动把手 + 关闭
        _titleBar = new Border { Background = Brushes.Transparent, Height = 26 };
        var titleHost = new DockPanel();
        var closeButton = new Button
        {
            Content = "×",
            Style = (Style)FindResource("Style.PanelCloseButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeButton.Click += (_, _) => Close();
        titleHost.Children.Add(closeButton);
        // 必须套文字层级样式：不写 Foreground 的 TextBlock 跟的是 Windows 主题（见 LayerPanelView 同处注释）
        titleHost.Children.Add(new TextBlock
        {
            Text = "计算器",
            Style = (Style)FindResource("Style.TextTitle"),
        });
        _titleBar.Child = titleHost;
        root.Children.Add(_titleBar);

        // 显示区：算式行 + 结果行
        var display = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 6),
            Margin = new Thickness(0, 6, 0, 8),
        }
        .FollowTheme(Border.BackgroundProperty, "Ui.FieldBackground")
        .FollowTheme(Border.BorderBrushProperty, "Ui.FieldBorder");
        var displayStack = new StackPanel();
        displayStack.Children.Add(_expressionText);
        displayStack.Children.Add(_resultText);
        display.Child = displayStack;
        root.Children.Add(display);

        // 按键区（4 列 × 5 行）
        var keys = new Grid();
        for (int i = 0; i < 4; i++)
        {
            keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        AddKeyRow(keys, 0, ("C", ClearKey), ("⌫", BackspaceKey), ("√", () => Append("√")), ("÷", () => Append("÷")));
        AddKeyRow(keys, 1, ("7", () => Append("7")), ("8", () => Append("8")), ("9", () => Append("9")), ("×", () => Append("×")));
        AddKeyRow(keys, 2, ("4", () => Append("4")), ("5", () => Append("5")), ("6", () => Append("6")), ("−", () => Append("-")));
        AddKeyRow(keys, 3, ("1", () => Append("1")), ("2", () => Append("2")), ("3", () => Append("3")), ("+", () => Append("+")));
        AddLastRow(keys, 4);

        root.Children.Add(keys);
        Child = root;
    }

    /// <summary>显示 / 隐藏。</summary>
    public void Toggle() => Visibility = Visibility == Visibility.Visible
        ? Visibility.Collapsed
        : Visibility.Visible;

    /// <summary>关闭（关闭按钮与外部的统一出口）。</summary>
    public void Close() => Visibility = Visibility.Collapsed;

    // ---------------------------------------------------------------- 按键逻辑

    private void Append(string token)
    {
        // 一个数里只允许一个小数点（按住 . 连点不会出现 "1..2"）
        if (token == "." && CurrentNumberSegment().Contains('.'))
        {
            return;
        }

        _expression += token;
        Refresh();
    }

    private void ClearKey()
    {
        _expression = string.Empty;
        Refresh();
    }

    private void BackspaceKey()
    {
        if (_expression.Length > 0)
        {
            _expression = _expression[..^1];
        }

        Refresh();
    }

    /// <summary>等号：求值进结果行；算式有问题就地提示，不清空输入（可以改完再按）。</summary>
    private void EqualsKey()
    {
        if (ArithmeticEvaluator.TryEvaluate(_expression, out double value))
        {
            // 最多 10 位有效数字，去掉没用的尾零（3.0000000001 这类浮点尾巴）
            _resultText.Text = value.ToString("0.##########", CultureInfo.InvariantCulture);
        }
        else
        {
            _resultText.Text = _expression.Length == 0 ? "0" : "算式不对";
        }
    }

    /// <summary>当前正在输入的那个数（最后一个运算符之后的片段），用于小数点判重。</summary>
    private string CurrentNumberSegment()
    {
        int index = _expression.Length;
        while (index > 0 && "+-×÷√".IndexOf(_expression[index - 1]) < 0)
        {
            index--;
        }

        return _expression[index..];
    }

    private void Refresh()
    {
        _expressionText.Text = _expression;
        if (ArithmeticEvaluator.TryEvaluate(_expression, out double live))
        {
            // 边按边出结果（等号只是"定稿"）—— 讲台上少按一下是一下
            _resultText.Text = live.ToString("0.##########", CultureInfo.InvariantCulture);
        }
        else if (_expression.Length == 0)
        {
            _resultText.Text = "0";
        }
    }

    // ---------------------------------------------------------------- UI 构造

    private void AddKeyRow(Grid keys, int row, params (string Label, Action Run)[] cells)
    {
        keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int column = 0; column < cells.Length; column++)
        {
            var (label, run) = cells[column];
            var key = BuildKey(label, run);
            Grid.SetRow(key, row);
            Grid.SetColumn(key, column);
            keys.Children.Add(key);
        }
    }

    /// <summary>末行：0 占两格，然后 . 和 =。</summary>
    private void AddLastRow(Grid keys, int row)
    {
        keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var zero = BuildKey("0", () => Append("0"));
        Grid.SetRow(zero, row);
        Grid.SetColumn(zero, 0);
        Grid.SetColumnSpan(zero, 2);
        keys.Children.Add(zero);

        var dot = BuildKey(".", () => Append("."));
        Grid.SetRow(dot, row);
        Grid.SetColumn(dot, 2);
        keys.Children.Add(dot);

        var equals = BuildKey("=", EqualsKey);
        Grid.SetRow(equals, row);
        Grid.SetColumn(equals, 3);
        keys.Children.Add(equals);
    }

    private Button BuildKey(string label, Action run)
    {
        var key = new Button
        {
            Content = label,
            Style = (Style)FindResource("Style.ToolbarButton"),
            Height = 40,
            MinWidth = 48,
            Margin = new Thickness(3),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
        }
        // 键面沿用"输入区"那一档颜色（与上面的显示区同一套），只是走令牌取值
        .FollowTheme(Control.BackgroundProperty, "Ui.FieldBackground")
        .FollowTheme(Control.BorderBrushProperty, "Ui.FieldBorder");
        key.Click += (_, _) => run();
        return key;
    }
}
