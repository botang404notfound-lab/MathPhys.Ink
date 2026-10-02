using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Formula;

/// <summary>
/// LaTeX 输入面板：文本框 + 实时预览 + 数学键盘（LaTeX 布局，M26 起与函数图像共用）。
/// </summary>
/// <remarks>
/// <para>
/// 无模式窗口（Show 而不是 ShowDialog）：画布必须保持可点 —— "点画布落公式"就是这个工具的主交互。
/// Topmost 保证它浮在白板上；居中启动在一体机上约等于视口中央（白板全屏）。
/// </para>
/// <para>
/// ★ 快捷条不是"插入示例"，而是<b>在光标处插入命令骨架</b>（如 <c>\frac{}{}</c>），
/// 老师补内容即可 —— 这是打字量与出错率之间的平衡点。
/// </para>
/// <para>
/// ★ S0 勘验结论写进提示：WPF-Math 无 CJK 字形与度量，<b>中文在公式里必然失败</b>；
/// 需要中文标注时用墨迹手写或题号标记，别往公式里打中文。
/// </para>
/// </remarks>
public sealed class FormulaPanelWindow : Window
{
    private readonly FormulaRenderer _renderer;
    private readonly TextBox _input;
    private readonly ContentControl _previewHost = new();
    private readonly TextBlock _hint = new();

    public FormulaPanelWindow(FormulaRenderer renderer, string initialLatex)
    {
        _renderer = renderer;

        Title = "插入公式（LaTeX）";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        // 输入框
        _input = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 16,
            Margin = new Thickness(12, 12, 12, 6),
            Text = initialLatex,
        };
        _input.TextChanged += (_, _) => UpdatePreview();
        _input.KeyDown += OnInputKeyDown;

        // 预览区：白底（公式墨色按纸面惯例是深色），Viewbox 等比放大到框内
        var previewBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12, 0, 12, 6),
            Height = 130,
            Child = new Viewbox { Child = _previewHost, Stretch = Stretch.Uniform },
        };

        // 数学键盘（LaTeX 布局）：M26 起与函数图像共用契约工程里的 MathKeyboard。
        // 旧两行快捷条的 19 键是键盘键位的严格子集，换键盘后单一真相源且省高度；
        // 触控尺寸沿用函数图像键盘（40 宽 / 32 高 / 15 号字），希沃点按视觉零变化。
        var keyboard = new MathKeyboard(MathKeyboard.Layout.Latex);
        keyboard.KeyPressed += k =>
        {
            // 插入逻辑走共用 HandleKey：骨架键光标自动落进花括号（如 \frac{}{} 落进第一个 {}）
            MathKeyboard.HandleKey(_input, k);
            _input.Focus();
        };
        var keyboardHost = keyboard.Build();

        // 提示行
        _hint = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin = new Thickness(14, 0, 14, 8),
            TextWrapping = TextWrapping.Wrap,
            Text = "点画布落下公式；面板记住内容，改一改可以连续落多个。"
                   + "不支持中文与 \\mathbb（缺字形）；中文标注请用墨迹手写。",
        };

        Content = new StackPanel
        {
            Children = { _input, previewBorder, keyboardHost, _hint },
        };

        Loaded += (_, _) => { _input.Focus(); _input.CaretIndex = _input.Text.Length; };
        UpdatePreview();
    }

    /// <summary>面板当前的 LaTeX（工具点画布时读它）。</summary>
    public string? CurrentLatex => _input.Text;

    // ---------------------------------------------------------------- 交互

    /// <summary>回车 = 关面板（内容保留），老师接着点画布；Esc 同效。落成交给画布点击。</summary>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) Close();
    }

    // ---------------------------------------------------------------- 预览

    private void UpdatePreview()
    {
        string latex = _input.Text?.Trim() ?? string.Empty;

        if (latex.Length == 0)
        {
            _previewHost.Content = MakeHintLabel("输入 LaTeX 后这里显示预览");
            return;
        }

        var entry = _renderer.GetEntry(latex);
        if (entry.LocalGeometry is null)
        {
            _previewHost.Content = MakeHintLabel("LaTeX 解析失败 —— 检查 \\ 命令拼写与花括号配对");
            return;
        }

        // 公式视觉画在以 (0,0) 为中心的本地坐标里；给画布一个显式尺寸 + 居中偏移，
        // 再交给 Viewbox 等比缩放到预览框
        double w = Math.Max(entry.LocalSize.Width, 8);
        double h = Math.Max(entry.LocalSize.Height, 8);
        var canvas = new Canvas { Width = w, Height = h };
        var visual = new FormulaVisual(entry.LocalGeometry, entry.LocalSize);
        Canvas.SetLeft(visual, w / 2);
        Canvas.SetTop(visual, h / 2);
        canvas.Children.Add(visual);

        _previewHost.Content = canvas;
    }

    private static TextBlock MakeHintLabel(string text) => new()
    {
        Text = text,
        FontSize = 14,
        Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8),
    };


}
