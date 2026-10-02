using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MathPhys.Ink.Design;
using MathPhys.Ink.Gfx;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Views.Controls;

/// <summary>
/// 选中对象后出现在视口右上角的<b>参数面板</b>。
/// </summary>
/// <remarks>
/// ★ 纯代码构建（不用 XAML），理由与函数图像的虚拟键盘一样：
/// 面板的内容是<b>运行时才知道的</b>（有哪些参数、几项、什么范围，全由插件在
/// <see cref="IGfxParameterProvider.BuildPanel"/> 里回答）。用 XAML 只能写死一份骨架，
/// 真正的内容还是要用代码填，白白多一层模板匹配。
/// <para>
/// 本类<b>不做任何数学</b>：显示什么、改了怎么办全部交给
/// <see cref="GfxParameterCoordinator"/>。它的职责只有"把数据变成控件"与"把点击变成调用" ——
/// 这样"面板逻辑对不对"可以在 harness 里断言，"面板好不好看"可以单独调，互不牵连。
/// </para>
/// <para>
/// ★ M10 起本类**一个色值都不写**，全部走 <see cref="Tokens"/>（设计令牌）。
/// 纯代码建界面的类拿不到 XAML 的 StaticResource，这正是 Tokens 存在的理由。
/// <para>
/// ★ 但「不写色值」≠「不会留旧色」—— <c>Tokens.Brush(key)</c> 取的是<b>一次快照</b>，
/// 换肤后本类原先那些元素仍然留着旧主题的色。M10 时这里写着「因此绝不会在换肤后留下旧色」，
/// <b>那句是错的</b>，M20 步 2b 已全部改成 <see cref="Tokens.FollowTheme{T}"/>（代码版 DynamicResource）。
/// </para>
/// </para>
/// <para>
/// 一体机约束（本节所有尺寸都是照它定的）：手指比鼠标粗得多，所以按钮最小 40×40 DIP、
/// 字体 15 起、每个可调项独占一行 —— 挤成两列会让老师点错行。
/// </para>
/// </remarks>
internal sealed class GfxParameterPanelView : Border
{
    /// <summary>面板宽度。定宽而不是自适应：参数名长短不一时，宽度跟着变会让整个面板"跳"。</summary>
    private const double PanelWidth = 268;

    /// <summary>
    /// 加减按钮的边长（一体机手指友好）。
    /// </summary>
    /// <remarks>
    /// ★ 必须 ≥ <c>Tokens.Number("Touch.Min")</c> = 40。
    /// （原文还写着「harness 会把两个值放在一起断言」—— 实际 harness 里没有这条断言，
    ///   M20 步 2b 顺手把话说准；真正的护栏是 <c>Style.PanelStepButton</c> 的 Touch.Min。）
    /// 这里之所以写成常量而不是直接读令牌：本字段是静态初始化的一部分，
    /// 而令牌要等资源字典加载完才拿得到 —— 为一个 40 引入静态初始化顺序依赖不值当。
    /// </remarks>
    private const double StepButtonSize = 40;

    /// <summary>面板内边距。</summary>
    private static readonly Thickness PanelPadding = new(12, 10, 12, 12);

    private readonly GfxParameterCoordinator _coordinator;
    private readonly Action<string> _reportStatus;
    private readonly StackPanel _fieldHost;

    /// <summary>面板是否正在被程序重填（重填过程中不要去响应用户输入，避免自激）。</summary>
    private bool _refilling;

    /// <param name="coordinator">参数协调器（唯一的数据来源）。</param>
    /// <param name="reportStatus">把结果写到状态栏的回调（成功/失败都走它）。</param>
    public GfxParameterPanelView(GfxParameterCoordinator coordinator, Action<string> reportStatus)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _reportStatus = reportStatus ?? (_ => { });

        Width = PanelWidth;
        Margin = new Thickness(0, 12, 12, 0);
        Padding = PanelPadding;
        CornerRadius = Tokens.CornerRadius("Corner.M");
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Visibility = Visibility.Collapsed;

        // 半透明底 + 描边：面板浮在试卷上，必须和卷面（白）明确分开，
        // 又不能全遮住 —— 老师要一边看图形一边调参数。
        // ★ 这对描边是"深色才成立"的典型：浅底上白描边等于没有，
        //   所以它必须走令牌，由浅色板换成黑系。
        // ★ M20 步 2b：底板接在设计令牌上（代码版 DynamicResource）
        this.FollowTheme(BackgroundProperty, "Ui.OverlayPanel");
        this.FollowTheme(BorderBrushProperty, "Ui.OverlayBorder");
        BorderThickness = new Thickness(1);

        _fieldHost = new StackPanel { Orientation = Orientation.Vertical };
        Child = _fieldHost;

        // 面板本身不该把点击"吃"进画布：它在视口上面，但只处理自己的输入
        IsHitTestVisible = true;
    }

    /// <summary>按协调器当前的内容重填面板；没有内容时隐藏自己。</summary>
    /// <remarks>
    /// <b>每次都整个重建，不做增量更新</b>：参数项的数量与种类会随对象变（不同 Kind 的字段完全不同），
    /// 增量更新要写"哪些项还在、哪些要换"的匹配逻辑，而那个逻辑错了的表现是
    /// "面板上显示的是上一个对象的参数" —— 比重建慢几十微秒的代价大得多。
    /// </remarks>
    public void Refresh()
    {
        _refilling = true;
        try
        {
            _fieldHost.Children.Clear();

            GfxParameterPanel? panel;
            try
            {
                panel = _coordinator.CurrentPanel;
            }
            catch (Exception ex)
            {
                // 协调器内部已经把插件异常吞掉了，这里再兜一层：面板是"附加信息"，
                // 它出问题绝不能影响画布本身。
                System.Diagnostics.Debug.WriteLine($"参数面板刷新失败：{ex}");
                panel = null;
            }

            if (panel is null || (panel.Fields.Count == 0 && panel.TextFields.Count == 0))
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            _fieldHost.Children.Add(BuildTitle(panel));

            if (!string.IsNullOrWhiteSpace(panel.Subtitle))
            {
                _fieldHost.Children.Add(new TextBlock
                {
                    Text = panel.Subtitle,
                    FontSize = Tokens.Number("Text.Label"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                }
                .FollowTheme(TextBlock.ForegroundProperty, "Ui.TextSecondary"));
            }

            foreach (var field in panel.Fields)
            {
                _fieldHost.Children.Add(BuildFieldRow(field));
            }

            // 文本项（表达式 / LaTeX）：宿主决定长什么样，插件只给数据（GfxTextField）
            foreach (var tfield in panel.TextFields)
            {
                _fieldHost.Children.Add(BuildTextFieldRow(tfield));
            }

            if (!string.IsNullOrWhiteSpace(panel.Note))
            {
                _fieldHost.Children.Add(new TextBlock
                {
                    Text = panel.Note,
                    FontSize = Tokens.Number("Text.Caption"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                }
                .FollowTheme(TextBlock.ForegroundProperty, "Ui.TextMuted"));
            }

            Visibility = Visibility.Visible;
        }
        finally
        {
            _refilling = false;
        }
    }

    // ---------------------------------------------------------------- 构件

    private UIElement BuildTitle(GfxParameterPanel panel)
    {
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };

        // 关闭按钮：面板挡住卷面时，老师得能把它收起来
        var close = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("Style.PanelCloseButton"),
            ToolTip = "收起参数面板（取消选中对象即可）",
        };
        DockPanel.SetDock(close, Dock.Right);

        // 关闭 = 取消选中，而不是"只是隐藏"：否则面板藏了、对象还选着，
        // 下一次点别处会突然又冒出来，看着像"关不掉"。
        close.Click += (_, _) => _closeRequested?.Invoke();
        dock.Children.Add(close);

        dock.Children.Add(new TextBlock
        {
            Text = panel.Title,
            FontSize = Tokens.Number("Text.Label"),
            FontWeight = Tokens.FontWeight("Weight.SemiBold"),
            VerticalAlignment = VerticalAlignment.Center,
        }
        .FollowTheme(TextBlock.ForegroundProperty, "Ui.TextPrimary"));

        return dock;
    }

    /// <summary>面板请求关闭（由宿主接上"取消选中"）。</summary>
    /// <remarks>
    /// <b>实例字段，不是 static</b>：harness 与宿主可能同时存在多个视口
    /// （每个 <c>CanvasViewportHost</c> 一个面板），静态字段会让"点这个面板的✕
    /// 却取消了另一个面板的对象选中" —— 与 <c>IGfxBoardAwareRenderer</c> 踩过的坑同源。
    /// </remarks>
    private Action? _closeRequested;

    /// <summary>接上"收起面板"的动作。</summary>
    public void SetCloseHandler(Action handler) => _closeRequested = handler;

    /// <summary>建一行参数。</summary>
    private UIElement BuildFieldRow(GfxParameterField field)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ---- 左：名字 + 当前值 ----
        var labelPanel = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };

        labelPanel.Children.Add(new TextBlock
        {
            Text = field.Label,
            FontSize = Tokens.Number("Text.Label"),
        }
        .FollowTheme(TextBlock.ForegroundProperty, "Ui.TextSecondary"));

        // ★ M20 步 2b：改用设计系统已有的输入框样式
        //   （原先这 9 个属性是手抄了一遍 Style.FieldBox 的内容，改令牌要改两处）
        var valueBox = new TextBox
        {
            Text = FormatValue(field, field.Value),
            Style = (Style)FindResource("Style.FieldBox"),
            IsReadOnly = field.IsReadOnly,
        };

        // ★ 开关型字段（Min=0, Max=1, Step=1）不显示成输入框，直接显示 开/关 ——
        //   让老师在"0.5"这种数字上理解"开还是关"是在给他添麻烦。
        bool isToggle = IsToggle(field);
        if (isToggle)
        {
            valueBox.Text = field.Value >= 0.5 ? "开" : "关";
        }

        // 输完回车 / 失焦才生效（而不是每敲一个字符就改一次）：
        // 否则输入 "12" 的过程中会先按 "1" 改一次（对象抖一下、撤销栈多一步）。
        valueBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitTextInput(field, valueBox);
            e.Handled = true;
        };
        valueBox.LostFocus += (_, _) => CommitTextInput(field, valueBox);

        labelPanel.Children.Add(valueBox);
        row.Children.Add(labelPanel);

        // ---- 右：减 / 加 两个按钮 ----
        if (!field.IsReadOnly)
        {
            double step = EffectiveStep(field);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };

            buttons.Children.Add(BuildStepButton(field, valueBox, -step, "−"));
            buttons.Children.Add(BuildStepButton(field, valueBox, +step, "＋"));

            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
        }

        return row;
    }

/// <summary>建一行文本参数（表达式 / LaTeX 之类）。</summary>
    /// <remarks>
    /// 布局与数值行同构：左标签 + 输入框，右侧动作按钮。
    /// <para>
    /// 提交时机：单行按回车提交；多行（LaTeX）的「回车」是打字换行，只能按「应用」按钮提交。
    /// 与数值输入框同一条纪律：<b>打字过程中不改对象</b>，一次提交 = 一步撤销。
    /// </para>
    /// </remarks>
    private UIElement BuildTextFieldRow(GfxTextField field)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelPanel = new StackPanel { Orientation = Orientation.Vertical };

        labelPanel.Children.Add(new TextBlock
        {
            Text = field.Label,
            FontSize = Tokens.Number("Text.Label"),
            Margin = new Thickness(0, 0, 0, 4),
        }
        .FollowTheme(TextBlock.ForegroundProperty, "Ui.TextSecondary"));

        var box = new TextBox
        {
            Text = field.Value,
            Style = (Style)FindResource("Style.FieldBox"),
            IsReadOnly = field.IsReadOnly,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
            FontSize = Tokens.Number("Text.Label"),
        };

        if (field.IsMultiline)
        {
            // LaTeX 可能很长：换行 + 加高。提交只走「应用」按钮（回车是打字换行）。
            box.TextWrapping = TextWrapping.Wrap;
            box.Height = 60;
            box.AcceptsReturn = true;
        }
        else
        {
            // 单行表达式：回车直接提交（与数值输入框同一习惯）。
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                CommitText(field, box);
                e.Handled = true;
            };
        }

        labelPanel.Children.Add(box);
        row.Children.Add(labelPanel);

        if (!field.IsReadOnly)
        {
            var apply = new Button
            {
                Content = "应用",
                Style = (Style)FindResource("Style.PanelStepButton"),
                Width = StepButtonSize,
                Height = StepButtonSize,
                Margin = new Thickness(8, 0, 0, 0),
            };
            apply.Click += (_, _) => CommitText(field, box);
            Grid.SetColumn(apply, 1);
            row.Children.Add(apply);
        }

        return row;
    }

    /// <summary>把文本框内容提交成一次修改。</summary>
    /// <remarks>
    /// 无论成败都写状态栏，并把面板按对象的真实值重填 ——
    /// 与数值行的 <c>ApplyAndRefill</c> 同一条纪律：输入框里永远不留下「并不存在」的值。
    /// </remarks>
    private void CommitText(GfxTextField field, TextBox box)
    {
        if (_refilling) return;

        string status;
        try
        {
            _coordinator.ApplyText(field.Key, box.Text, out status);
        }
        catch (Exception ex)
        {
            // ApplyText 内部已兜底，这里再兜一层：面板绝不能把异常抛回消息循环。
            status = $"改「{field.Label}」时出错：{ex.Message}";
        }

        if (!string.IsNullOrEmpty(status)) _reportStatus(status);
        Refresh();
    }


    private Button BuildStepButton(GfxParameterField field, TextBox valueBox, double delta, string caption)
    {
        // ★ 边长仍用本类的 StepButtonSize（原生 40 = 标准档，手指按得中）；
        //   样式只负责五态与配色 —— 与 Style.PanelStepButton 的尺寸一致，不冲突。
        var button = new Button
        {
            Content = caption,
            Style = (Style)FindResource("Style.PanelStepButton"),
            Width = StepButtonSize,
            Height = StepButtonSize,
            Margin = new Thickness(0, 0, 6, 0),
        };

        button.Click += (_, _) =>
        {
            // 从"当前显示的值"出发，而不是从 field.Value ——
            // 老师可能刚在输入框里打了没回车的新数，加减应当接着那个数走。
            double from = ParseValue(field, valueBox.Text, field.Value);
            double next = field.Snap(from + delta);
            ApplyAndRefill(field, next);
        };

        return button;
    }

    /// <summary>把输入框里的文字提交成一个修改。</summary>
    private void CommitTextInput(GfxParameterField field, TextBox box)
    {
        if (_refilling) return;

        double parsed = ParseValue(field, box.Text, field.Value);
        ApplyAndRefill(field, parsed);
    }

    /// <summary>改一个值，然后把整个面板按对象的新状态重填。</summary>
    private void ApplyAndRefill(GfxParameterField field, double value)
    {
        if (_refilling) return;

        bool changed;
        string status;
        try
        {
            changed = _coordinator.Apply(field.Key, value, out status);
        }
        catch (Exception ex)
        {
            // Apply 内部已兜底，这里再兜一层：面板绝不能把异常抛回消息循环。
            status = $"改「{field.Label}」时出错：{ex.Message}";
            changed = false;
        }

        // 无论成功与否都写状态栏 —— 老师按了按钮没反应时，状态栏是他唯一的线索。
        if (!string.IsNullOrEmpty(status)) _reportStatus(status);

        // ★ 失败也要重填：输入框里可能留着非法/越界的文字（比如输了一堆字母被解析成旧值），
        //   重填会把显示拉回对象的真实值，否则面板会一直显示一个"并不存在"的数。
        Refresh();
        _ = changed;
    }

    // ---------------------------------------------------------------- 取值 / 解析

    private static bool IsToggle(GfxParameterField field)
        => field.Min == 0 && field.Max == 1 && field.Step == 1;

    /// <summary>实际用的步长：插件没给（&lt;=0）就按范围推一个，再不行用 1。</summary>
    private static double EffectiveStep(GfxParameterField field)
    {
        if (field.Step > 0) return field.Step;
        if (field.HasRange) return Math.Max((field.Max - field.Min) / 100.0, 1e-6);
        return 1.0;
    }

    /// <summary>把值转成输入框里显示的文字（开关字段特判）。</summary>
    private static string FormatValue(GfxParameterField field, double value)
    {
        if (IsToggle(field)) return value >= 0.5 ? "开" : "关";
        return GfxParameterCoordinator.Describe(value, field.Unit);
    }

    /// <summary>
    /// 从输入框文字解析一个数。
    /// </summary>
    /// <remarks>
    /// 容错（老师会敲进各种东西）：先试当前区域设置，再试不变文化（小数点/逗号差异），
    /// 都失败就退回原值 —— 绝不抛异常、绝不把对象改成 0。
    /// </remarks>
    private static double ParseValue(GfxParameterField field, string text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        string trimmed = text.Trim();

        // 开关字段：认"开/关"，也认 1/0 与 true/false
        if (IsToggle(field))
        {
            if (trimmed is "开" or "是" or "on" or "ON" or "true" or "True") return 1;
            if (trimmed is "关" or "否" or "off" or "OFF" or "false" or "False") return 0;
        }

        // 去掉单位后缀（面板显示 "28.35 pt"，老师可能连单位一起复制回来）
        if (!string.IsNullOrEmpty(field.Unit) && trimmed.EndsWith(field.Unit, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - field.Unit.Length).Trim();
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out double byCurrent))
            return byCurrent;

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double byInvariant))
            return byInvariant;

        return fallback;
    }
}
