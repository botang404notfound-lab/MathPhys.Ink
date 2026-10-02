using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Design;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 题号标记的画法（M12）：<b>宿主侧</b>的图形 Kind —— 数据、持久化、命中、拖动、撤销
/// 全部走图形对象层的既有机制，一行额外代码都不用写。
/// </summary>
/// <remarks>
/// <para>
/// 数据约定（与坐标系等插件 Kind 同一套表达法，存档零改动）：
/// <list type="bullet">
/// <item><c>Texts["label"]</c> —— 题号文本（"12"）；</item>
/// <item><c>Numbers["state"]</c> —— 讲评状态：0 未讲 / 1 错题 / 2 已讲评。</item>
/// </list>
/// </para>
/// <para>
/// 三态颜色是<b>语义色</b>（红 = 错题是批改惯例），进导出产物、不随界面主题翻转
/// —— 所以挂在 <b>画布板</b>（<c>Canvas.QuestionMark*</c>，M10 色板划分判据：
/// 会不会进导出），而不是作为常量散落在渲染器里。
/// </para>
/// </remarks>
public sealed class QuestionMarkRenderer : IGfxObjectRenderer, IGfxParameterProvider, IGfxParameterSink
{
    public const string KindName = "questionMark";

    /// <summary>题号文本键。</summary>
    public const string LabelKey = "label";

    /// <summary>讲评状态键（0 未讲 / 1 错题 / 2 已讲评）。</summary>
    public const string StateKey = "state";

    public string Kind => KindName;

    // ---------------------------------------------------------------- 尺寸（纯函数，harness 可断言）

    /// <summary>题号字高（本地单位 = 世界单位；Scale=1 时 26pt ≈ 9mm，投影下看得清）。</summary>
    public const double ChipHeight = 26.0;

    /// <summary>一个全角字符的估宽。</summary>
    public const double WideCharWidth = 15.0;

    /// <summary>一个半角字符的估宽。</summary>
    public const double NarrowCharWidth = 8.2;

    /// <summary>芯片左右留白合计。</summary>
    public const double ChipPadding = 18.0;

    public Size Measure(IGfxObjectRef obj)
    {
        double textWidth = 0;

        foreach (char ch in LabelOf(obj))
        {
            textWidth += ch > 0xFF ? WideCharWidth : NarrowCharWidth;
        }

        return new Size(ChipPadding + Math.Max(textWidth, NarrowCharWidth), ChipHeight);
    }

    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        // ★ 尺寸必须由 Measure 算出来、当场递给视觉，不能让视觉自己去读 ActualWidth。
        //   图形层把渲染器的视觉放进一个 Canvas、按 DesiredSize 排版，而这类渲染器约定
        //   MeasureOverride 返回 (0,0)（原点 = 对象中心）⇒ ActualWidth 恒为 0。
        //   这里曾经就是「让视觉自己读 ActualWidth」，于是错题态（红底白字）的角标
        //   在卷面上一个像素都画不出来 —— 标了「错题」反而看不见（harness 8.8 抓到的）。
        return new QuestionMarkVisual(
            LabelOf(obj),
            StateColorOf(obj),
            StateOf(obj),
            Measure(obj));
    }

    // ---------------------------------------------------------------- 取参

    public static string LabelOf(IGfxObjectRef obj) => obj.GetText(LabelKey, "?");

    public static int StateOf(IGfxObjectRef obj)
    {
        double raw = obj.GetNumber(StateKey, 0.0);
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0;
        return Math.Clamp((int)Math.Round(raw), 0, 2);
    }

    /// <summary>状态对应的标记色（未讲 = 对象自己的墨色）。</summary>
    public static Color StateColorOf(IGfxObjectRef obj) => StateOf(obj) switch
    {
        1 => Tokens.Color("Canvas.QuestionMarkWrong"),
        2 => Tokens.Color("Canvas.QuestionMarkDone"),
        _ => obj.Color,
    };

    // ---------------------------------------------------------------- 参数面板（改状态 / 看题号）

    public GfxParameterPanel? BuildPanel(IGfxObjectRef obj)
    {
        if (obj is null || !string.Equals(obj.Kind, KindName, StringComparison.Ordinal)) return null;

        return new GfxParameterPanel
        {
            Title = "题号标记",
            Subtitle = $"第 {LabelOf(obj)} 题 · {DescribeState(StateOf(obj))}",
            Fields = new List<GfxParameterField>
            {
                new()
                {
                    Key = StateKey, Label = "讲评状态（0 未讲 / 1 错题 / 2 已讲）",
                    Value = StateOf(obj), Min = 0, Max = 2, Step = 1,
                },
            },
            Note = "提示：用「题号标记」工具再点一下这个角标，也能在三种状态间轮换。",
        };
    }

    public IReadOnlyDictionary<string, double>? Apply(IGfxObjectRef obj, string key, double value)
    {
        if (obj is null) return null;
        if (!string.Equals(key, StateKey, StringComparison.Ordinal)) return null;
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;

        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [StateKey] = Math.Clamp(Math.Round(value), 0, 2),
        };
    }

    /// <summary>状态的中文说法（面板副标题与日志用）。</summary>
    public static string DescribeState(int state) => state switch
    {
        1 => "错题",
        2 => "已讲评",
        _ => "未讲",
    };

    // ---------------------------------------------------------------- 视觉

    /// <summary>圆角芯片 + 题号文字。文字直接画，不挂反向缩放 —— 标记被老师放大时字跟着大是合理的。</summary>
    private sealed class QuestionMarkVisual : FrameworkElement
    {
        private readonly string _label;
        private readonly Color _stateColor;
        private readonly int _state;
        private readonly Size _size;
        private readonly Pen _outline;
        private readonly Brush _fill;
        private readonly Brush _text;

        private const double CornerRadius = 7.0;
        private const double FontSize = 15.0;

        public QuestionMarkVisual(string label, Color stateColor, int state, Size size)
        {
            _label = label;
            _stateColor = stateColor;
            _state = state;
            _size = size;

            // 未讲态：白底彩字彩描边；错题/已讲态：彩底白字（白 = 纸色，走画布板）
            var fillBrush = new SolidColorBrush(state == 0 ? Tokens.Color("Canvas.PageWhite") : stateColor);
            var outlineBrush = new SolidColorBrush(state == 0 ? stateColor : Colors.Transparent);

            _fill = Freeze(fillBrush);
            _outline = Freeze(new Pen(Freeze(outlineBrush), 1.4));
            _text = Freeze(new SolidColorBrush(state == 0 ? stateColor : Tokens.Color("Canvas.PageWhite")));

            IsHitTestVisible = false;
            Focusable = false;
        }

        protected override Size MeasureOverride(Size availableSize) => new(0, 0);

        protected override void OnRender(DrawingContext dc)
        {
            // 本地坐标原点 = 对象中心（宿主把视觉的原点摆到中心），所以一切都居中画。
            // ★ 尺寸取 _size 而不是 ActualWidth：后者在这个宿主里恒为 0（见 CreateVisual 的说明）。
            double w = _size.Width, h = _size.Height;
            var rect = new Rect(-w / 2.0, -h / 2.0, w, h);

            dc.DrawRoundedRectangle(_fill, _outline, rect, CornerRadius, CornerRadius);

            var text = new FormattedText(
                _label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
                             FontWeights.Bold, FontStretches.Normal),
                FontSize,
                _text,
                1.25);

            dc.DrawText(text, new Point(-text.Width / 2.0, -text.Height / 2.0));
        }

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            if (freezable.CanFreeze) freezable.Freeze();
            return freezable;
        }
    }
}
