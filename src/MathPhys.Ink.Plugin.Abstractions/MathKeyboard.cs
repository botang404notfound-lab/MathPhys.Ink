using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 数学键盘（可复用组件）：函数图像（Layout.Expression）与
/// LaTeX 公式面板（Layout.Latex）共用同一份实现。
/// </summary>
/// <remarks>
/// <para>
/// 前身是函数图像插件里的 VirtualKeyboard（M7.4）：主键盘区（含顶部数学快捷键行）
/// + 右侧小键盘 + 方向键；触屏点按发出 KeyPressed 事件，
/// 由宿主决定如何插入到文本框。旧类注释里就写着「设计成可复用控件」，M26 兑现：
/// 抽到契约工程，全程序只加载一份。
/// </para>
/// <para>
/// ★ 为什么放 Abstractions：两个插件在各自 ALC 里加载，键盘若留在函数图像插件，
/// 公式插件要么跨插件引用（加载顺序变脆），要么复制代码（两份键盘、两处 bug）。
/// 契约工程全程序只有一份，是唯一安全的公共落点。纯新增类型、ITool 零新增
/// ⇒ 契约 1.6.0 → 1.7.0（规则②只升次版本，旧插件照常加载）。
/// </para>
/// <para>
/// ★ 表达式布局的 token 与旧 VirtualKeyboard 逐键一致
/// （sin( / cos( / sqrt( / ^ / π / e / x / ( / ) / * / / / - / +），
/// 保证 100% 命中函数表达式解析器、单一真相源。
/// </para>
/// <para>
/// ★ 触控尺寸沿用 M7.4 希沃适配值：键宽 40×倍数、高 32、字号 15、键距 2 —— 不另起一套视觉。
/// </para>
/// </remarks>
public sealed class MathKeyboard
{
    // 特殊 token：宿主据此做退格 / 清空 / 移光标，而不是插入字面文本
    public const string Back = "__BACK__";
    public const string Clear = "__CLEAR__";
    public const string Left = "__LEFT__";
    public const string Right = "__RIGHT__";

    /// <summary>键盘布局：两种语法域各一套键位。</summary>
    public enum Layout
    {
        /// <summary>函数表达式语法（sin( / sqrt( / ^ …），与旧 VirtualKeyboard 逐键一致。</summary>
        Expression,
        /// <summary>LaTeX 语法（\frac{}{} / \sqrt{} / ^{} / \alpha …），公式面板用。</summary>
        Latex,
    }

    /// <summary>
    /// 一次按键要插入的内容。CaretOffset = 插入后光标落在「插入起点后第几字符处」；
    /// 负值表示落在插入串末尾（普通字符键）。骨架键用它把光标送进花括号，
    /// 例如 \frac{}{} 的 CaretOffset = 6 ⇒ 光标落在第一个花括号内。
    /// </summary>
    public sealed record KeyInsert(string Text, int CaretOffset = -1);

    private readonly Layout _layout;

    /// <summary>默认表达式布局（函数图像的历史行为，迁移零改动）。</summary>
    public MathKeyboard() : this(Layout.Expression) { }

    public MathKeyboard(Layout layout) => _layout = layout;

    public event Action<KeyInsert>? KeyPressed;

    /// <summary>按键描述：Label 上屏，Token 发出，W 宽度倍数，Muted 视觉占位，CaretOffset 光标落点。</summary>
    private sealed record K(string Label, string Token, double W = 1.0, bool Muted = false, int CaretOffset = -1);

    /// <summary>按当前布局构建键盘 UI（代码建界面；返回带外框 Border，宿主直接挂进面板）。</summary>
    public UIElement Build()
        => _layout switch
        {
            Layout.Latex => Wrap(BuildLatex()),
            _ => Wrap(BuildExpression()),
        };

    // ---------------------------------------------------------- 表达式布局（函数图像，逐键原样迁移）

    private UIElement BuildExpression()
    {
        // ---------------- 主键盘区（左）----------------
        var main = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(2) };
        main.Children.Add(Row(new[]
        {
            new K("sin(", "sin("), new K("cos(", "cos("), new K("tan(", "tan("), new K("ln(", "ln("),
            new K("log(", "log("), new K("√(", "sqrt("), new K("^", "^"), new K("π", "π"),
            new K("e", "e"), new K("x", "x"), new K("(", "("), new K(")", ")"),
        }));
        main.Children.Add(Row(new[]
        {
            new K("`", "`"), new K("1", "1"), new K("2", "2"), new K("3", "3"), new K("4", "4"), new K("5", "5"),
            new K("6", "6"), new K("7", "7"), new K("8", "8"), new K("9", "9"), new K("0", "0"), new K("-", "-"), new K("=", "="),
            new K("⌫", Back, 1.5),
        }));
        main.Children.Add(Row(new[]
        {
            new K("Tab", "", 1.5, true), new K("q", "q"), new K("w", "w"), new K("e", "e"), new K("r", "r"), new K("t", "t"),
            new K("y", "y"), new K("u", "u"), new K("i", "i"), new K("o", "o"), new K("p", "p"), new K("[", "["), new K("]", "]"), new K("\\", "\\"),
        }));
        main.Children.Add(Row(new[]
        {
            new K("Caps", "", 1.5, true), new K("a", "a"), new K("s", "s"), new K("d", "d"), new K("f", "f"), new K("g", "g"),
            new K("h", "h"), new K("j", "j"), new K("k", "k"), new K("l", "l"), new K(";", ";"), new K("'", "'"), new K("Enter", "", 1.5, true),
        }));
        main.Children.Add(Row(new[]
        {
            new K("Shift", "", 1.5, true), new K("z", "z"), new K("x", "x"), new K("c", "c"), new K("v", "v"), new K("b", "b"),
            new K("n", "n"), new K("m", "m"), new K(",", ","), new K(".", "."), new K("/", "/"), new K("Shift", "", 1.5, true),
        }));
        main.Children.Add(Row(new[]
        {
            new K("Ctrl", "", 1.5, true), new K("Win", "", 1.5, true), new K("Alt", "", 1.5, true),
            new K("Space", " ", 5.0), new K("Alt", "", 1.5, true), new K("Fn", "", 1.5, true),
            new K("Menu", "", 1.5, true), new K("Ctrl", "", 1.5, true),
        }));

        // ---------------- 小键盘 + 方向键（右）----------------
        var side = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(2) };
        side.Children.Add(Numpad());
        side.Children.Add(Arrows());

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(main, 0);
        Grid.SetColumn(side, 1);
        grid.Children.Add(main);
        grid.Children.Add(side);
        return grid;
    }

    // ---------------------------------------------------------- LaTeX 布局（公式面板）
    // 键位按「结构骨架 → 关系符号 → 希腊 → 函数 → 括号数字 → 字母 → 编辑」排，
    // 每行 ≤ 12 键，最宽行约 560px，装进公式面板 620px 窗宽（希沃横屏不折行）。

    private UIElement BuildLatex()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(2) };

        // R1 结构骨架：CaretOffset 让插入后光标自动落进花括号（相对插入起点）
        panel.Children.Add(Row(new[]
        {
            new K("a/b", @"\frac{}{}", 1.2, false, 6),
            new K("√", @"\sqrt{}", 1.2, false, 6),
            new K("^", @"^{}", 1.0, false, 2),
            new K("_", @"_{}", 1.0, false, 2),
            new K("∑", @"\sum_{}^{}", 1.2, false, 6),
            new K("∫", @"\int_{}^{}", 1.2, false, 6),
            new K("lim", @"\lim_{}", 1.2, false, 6),
            new K("vec", @"\vec{}", 1.2, false, 5),
            new K("‾", @"\overline{}", 1.2, false, 10),
        }));
        // R2 关系 / 运算符
        panel.Children.Add(Row(new[]
        {
            new K("+", "+"), new K("−", "-"), new K("×", @"\times"), new K("÷", @"\div"),
            new K("=", "="), new K("≠", @"\neq"), new K("≤", @"\leq"), new K("≥", @"\geq"),
            new K("±", @"\pm"), new K("∞", @"\infty"), new K("⊥", @"\perp"), new K("∥", @"\parallel"),
        }));
        // R3 希腊字母（覆盖旧快捷条全部：θ Δ ω π μ ε ρ Ω）
        panel.Children.Add(Row(new[]
        {
            new K("α", @"\alpha"), new K("β", @"\beta"), new K("γ", @"\gamma"), new K("ρ", @"\rho"),
            new K("θ", @"\theta"), new K("π", @"\pi"), new K("φ", @"\phi"), new K("λ", @"\lambda"),
            new K("ω", @"\omega"), new K("μ", @"\mu"), new K("ε", @"\varepsilon"), new K("Ω", @"\Omega"),
        }));
        // R4 常用函数 / 常数
        panel.Children.Add(Row(new[]
        {
            new K("sin", @"\sin"), new K("cos", @"\cos"), new K("tan", @"\tan"),
            new K("log", @"\log"), new K("ln", @"\ln"), new K("·", @"\cdot"),
            new K("e", "e"), new K("x", "x"),
        }));
        // R5 括号（{ 自动配对：插 {} 光标进内）+ 数字
        panel.Children.Add(Row(new[]
        {
            new K("(", "("), new K(")", ")"), new K("[", "["), new K("]", "]"),
            new K("{", "{}", 1.0, false, 1), new K("}", "}"),
            new K("0", "0"), new K("1", "1"), new K("2", "2"), new K("3", "3"), new K("4", "4"), new K("5", "5"),
        }));
        // R6 数字 / 小数 / 常用字母
        panel.Children.Add(Row(new[]
        {
            new K("6", "6"), new K("7", "7"), new K("8", "8"), new K("9", "9"),
            new K(".", "."), new K("-", "-"),
            new K("a", "a"), new K("b", "b"), new K("c", "c"), new K("d", "d"), new K("e", "e"), new K("f", "f"),
        }));
        // R7 字母
        panel.Children.Add(Row(new[]
        {
            new K("g", "g"), new K("h", "h"), new K("i", "i"), new K("j", "j"), new K("k", "k"), new K("l", "l"),
            new K("m", "m"), new K("n", "n"), new K("o", "o"), new K("p", "p"), new K("q", "q"), new K("r", "r"),
        }));
        // R8 字母 + 编辑键（光标左右 / 退格 / 清空）
        panel.Children.Add(Row(new[]
        {
            new K("s", "s"), new K("t", "t"), new K("u", "u"), new K("v", "v"), new K("w", "w"), new K("x", "x"),
            new K("y", "y"), new K("z", "z"),
            new K("←", Left, 1.2), new K("⌫", Back, 1.2), new K("→", Right, 1.2), new K("清空", Clear, 1.2),
        }));
        return panel;
    }

    // ---------------------------------------------------------- 行 / 键（与旧 VirtualKeyboard 同款工厂）

    private UIElement Row(K[] keys)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var k in keys) sp.Children.Add(Make(k));
        return sp;
    }

    private Button Make(K k)
    {
        // 触控尺寸是希沃适配过的既有值（40×W 宽 / 32 高 / 15 号字），迁移时一个像素都不动
        var b = new Button
        {
            Content = k.Label,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
            Width = 40 * k.W,
            Height = 32,
            Margin = new Thickness(2),
            Padding = new Thickness(2),
        };
        if (k.Muted)
        {
            b.Background = Brushes.WhiteSmoke;
            b.Foreground = Brushes.Gray;
        }
        b.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(k.Token)) KeyPressed?.Invoke(new KeyInsert(k.Token, k.CaretOffset));
        };
        return b;
    }

    /// <summary>与原 VirtualKeyboard 同款外框：浅灰描边 + 4px 内边距。</summary>
    private static UIElement Wrap(UIElement inner) => new Border
    {
        Background = new SolidColorBrush(Color.FromArgb(0x10, 0, 0, 0)),
        Child = inner,
        Padding = new Thickness(4),
        BorderBrush = Brushes.LightGray,
        BorderThickness = new Thickness(1, 1, 1, 0),
    };

    private UIElement Numpad()
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (int c = 0; c < 4; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < 4; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        K[][] layout =
        {
            new[] { new K("7", "7"), new K("8", "8"), new K("9", "9"), new K("/", "/") },
            new[] { new K("4", "4"), new K("5", "5"), new K("6", "6"), new K("*", "*") },
            new[] { new K("1", "1"), new K("2", "2"), new K("3", "3"), new K("-", "-") },
            new[] { new K("0", "0", 2.0), new K(".", "."), new K("+", "+") },
        };
        // 注意：最后一行只有 3 个键（「0」 跨 2 列），必须按每行实际长度遍历，
        // 否则 layout[3][3] 会抛 IndexOutOfRangeException（S4 现场 bug，曾导致整个函数图像弹窗打不开）。
        for (int r = 0; r < layout.Length; r++)
            for (int c = 0; c < layout[r].Length; c++)
            {
                var k = layout[r][c];
                var b = Make(k);
                Grid.SetRow(b, r);
                Grid.SetColumn(b, c);
                if (k.W > 1) Grid.SetColumnSpan(b, 2);
                g.Children.Add(b);
            }
        return g;
    }

    private UIElement Arrows()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var up = Make(new K("↑", "")); Grid.SetRow(up, 0); Grid.SetColumn(up, 1);
        var left = Make(new K("←", Left)); Grid.SetRow(left, 1); Grid.SetColumn(left, 0);
        var down = Make(new K("↓", "")); Grid.SetRow(down, 1); Grid.SetColumn(down, 1);
        var right = Make(new K("→", Right)); Grid.SetRow(right, 1); Grid.SetColumn(right, 2);
        g.Children.Add(up); g.Children.Add(left); g.Children.Add(down); g.Children.Add(right);
        return g;
    }

    // ---------------------------------------------------------- 共享插入逻辑
    // 两个宿主（函数输入窗 / 公式面板）都调这里，不再各写一份 —— 光标落点只有一种实现，
    // harness 可以无头断言（确定性落点：骨架键进花括号、普通键落末尾）。

    /// <summary>
    /// 在光标处插入文本（有选区先删选区）；caretOffset 小于 0 时光标落插入串末尾，
    /// 否则落在「插入起点 + caretOffset」处（骨架键用它把光标送进第一个花括号）。
    /// </summary>
    public static void Insert(TextBox box, string text, int caretOffset = -1)
    {
        if (string.IsNullOrEmpty(text)) return;
        int caret = box.SelectionStart;
        if (box.SelectionLength > 0)
        {
            // 有选区先删掉，光标回到选区起点（与真实键盘打字覆盖选区的手感一致）
            box.Text = box.Text.Remove(caret, box.SelectionLength);
            caret = box.SelectionStart;
        }
        box.Text = box.Text.Insert(caret, text);
        // caretOffset 相对插入起点：负值落末尾，正值落骨架内部（如 \frac{}{} 的 6）
        int target = caretOffset < 0 ? caret + text.Length : caret + caretOffset;
        box.SelectionStart = Math.Clamp(target, 0, box.Text.Length);
        box.SelectionLength = 0;
    }

    /// <summary>
    /// 处理一次按键：特殊 token 走退格 / 清空 / 移光标，其余走 Insert。
    /// 两个宿主的键盘事件都收敛到这里 ⇒ 行为只有一种，改一处全生效。
    /// </summary>
    public static void HandleKey(TextBox box, KeyInsert key)
    {
        if (key.Text == Back)
        {
            int caret = box.SelectionStart;
            if (box.SelectionLength > 0)
            {
                box.Text = box.Text.Remove(caret, box.SelectionLength);
                box.SelectionStart = caret;
            }
            else if (caret > 0)
            {
                box.Text = box.Text.Remove(caret - 1, 1);
                box.SelectionStart = caret - 1;
            }
        }
        else if (key.Text == Clear)
        {
            box.Text = string.Empty;
            box.SelectionStart = 0;
        }
        else if (key.Text == Left)
        {
            if (box.SelectionStart > 0) box.SelectionStart = box.SelectionStart - 1;
        }
        else if (key.Text == Right)
        {
            if (box.SelectionStart < box.Text.Length) box.SelectionStart = box.SelectionStart + 1;
        }
        else
        {
            Insert(box, key.Text, key.CaretOffset);
        }
        box.SelectionLength = 0;
    }
}
