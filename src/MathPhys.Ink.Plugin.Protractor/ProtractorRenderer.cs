using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Protractor;

/// <summary>
/// 量角器的画法 —— 插件对"图形库"这一层的贡献。
/// </summary>
/// <remarks>
/// 一个对象 = <b>一个</b>视觉元素（<see cref="ProtractorVisual"/>），
/// 盘面 / 刻度 / 数字三层几何都在它自己的 <c>OnRender</c> 里画。
/// <para>
/// <b>为什么不用一堆 <c>Path</c></b>：181 条刻度线 + 38 个数字若各成一个 <c>Path</c>，
/// 就是 220 个图元，拖动时 WPF 要逐个算变换与命中；而它们只需要一次
/// <c>DrawingContext.DrawGeometry</c>。这不是过早优化 —— 一体机上"拖不动"是当场就能感觉到的。
/// 顺带还绕开了 <c>Shape</c> 的一个坑：<c>Stretch=None</c> 时几何可能被平移到元素原点，
/// 于是"几何坐标"与"画在哪里"变成两件事，正好是"看着在这、点着不在"的温床。
/// </para>
/// </remarks>
public sealed class ProtractorRenderer : IGfxObjectRenderer
{
    /// <summary>这个渲染器负责的对象种类。</summary>
    public const string KindName = "protractor";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <summary>
    /// 本地包围盒：以圆心为中心、边长 2R 的<b>正方形</b>。
    /// </summary>
    /// <remarks>
    /// 这一步是本插件唯一需要解释的"不自然"之处，值得说清楚。
    /// <para>
    /// 半圆盘本身的包围盒是 <c>[-R, R] × [−R, 0]</c>，它<b>不以圆心为中心</b>
    /// （圆心在底边中点）。而宿主的约定是：本地原点就是旋转与缩放的中心，
    /// 包围盒则必须以原点为中心 —— 这条约定保证了"绕原点转"与"框住形状"是同一件事。
    /// </para>
    /// <para>
    /// 于是这里有两条路：① 原点放包围盒中心，简单，但<b>圆心就歪了</b> ——
    /// 转一下盘面圆心就跑，而"转盘面去对齐角的另一条边"正是量角器的核心用法，不可接受；
    /// ② 原点放圆心（选它），包围盒声明成绕圆心的 2R × 2R 正方形 ——
    /// <b>盘面只占它的上半边</b>，下半边是空的。
    /// </para>
    /// <para>
    /// 代价说清楚：选中框比盘面高了一倍，且基线下方那块空白也能按住（在一体机上这反而好按）。
    /// 收益是旋转中心正确。日后若真嫌框松，正确的解法是给对象层加"非中心包围盒"
    /// （<c>Rect LocalBounds</c>）—— 那是对象层的改动，不是这个插件的。
    /// </para>
    /// </remarks>
    public Size Measure(IGfxObjectRef obj)
    {
        double radius = ProtractorGeometry.RadiusOf(obj);
        return new Size(radius * 2.0, radius * 2.0);
    }

    /// <inheritdoc />
    public FrameworkElement CreateVisual(IGfxObjectRef obj)
        => new ProtractorVisual(ProtractorGeometry.RadiusOf(obj), obj.Color, obj.LineWorldWidth);
}

/// <summary>
/// 量角器的矢量视觉：盘面 + 双圈刻度 + 数字，全部在本地坐标里画（原点 = 圆心）。
/// </summary>
/// <remarks>
/// 刻意<b>不</b>设 <c>RenderTransform</c>、<c>Canvas.Left/Top</c>：位姿由宿主统一施加，
/// 只有一个矩阵真相源（契约里的第一条约定）。这里只管"以圆心为原点长什么样"。
/// </remarks>
internal sealed class ProtractorVisual : FrameworkElement
{
    /// <summary>盘面填充：很淡的蓝，像真量角器那种半透明塑料。</summary>
    /// <remarks>
    /// 固定颜色而不跟随墨色：老师的墨可能是红的，红盘面会把刻度吃掉；
    /// 而且"量角器是那个蓝色塑料片"本身就是学生认得的形象。描边与刻度才跟随墨色。
    /// </remarks>
    private static readonly Color FaceColor = Color.FromArgb(0x30, 0x00, 0xA0, 0xFF);

    private readonly Geometry _disc;
    private readonly Geometry _ticks;
    private readonly Geometry _digits;

    private readonly Brush _face;
    private readonly Pen _edge;
    private readonly Pen _tickPen;
    private readonly Brush _ink;

    public ProtractorVisual(double radius, Color inkColor, double lineWorldWidth)
    {
        _disc = ProtractorGeometry.BuildDisc(radius);
        _ticks = ProtractorGeometry.BuildTicks(radius);
        _digits = ProtractorGeometry.BuildDigits(radius);

        _face = Frozen(new SolidColorBrush(FaceColor));

        double edgeWidth = lineWorldWidth > 0 ? lineWorldWidth : 1.5;
        _edge = Frozen(new Pen(Frozen(new SolidColorBrush(inkColor)), edgeWidth));

        // 刻度比外框细一档：与描边同宽的话，181 条线会把盘面涂成一块灰。
        _tickPen = Frozen(new Pen(Frozen(new SolidColorBrush(WithAlpha(inkColor, 0xB4))),
                                  edgeWidth * 0.7));

        _ink = Frozen(new SolidColorBrush(inkColor));

        // 图形层整体已经退出命中测试（宿主保证），这里再兜一次：
        // 白板上的第一原则是"输入绝不被图元抢走"（M7.3 的教训）。
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>几何与画笔已经构建完毕，不需要重新测量。</summary>
    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    /// <remarks>
    /// 绘制顺序：盘面 → 刻度 → 数字。盘面先画，刻度压在它上面才看得清；
    /// 数字最后画，保证它永远不会被刻度线穿过。
    /// </remarks>
    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawGeometry(_face, _edge, _disc);
        drawingContext.DrawGeometry(null, _tickPen, _ticks);
        drawingContext.DrawGeometry(_ink, null, _digits);
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>冻结：WPF 遇到冻结的画笔/几何会跳过变更通知，盘面这种"一次建好多次重画"的东西值得。</summary>
    private static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
