using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Triangle;

/// <summary>
/// 三角板的画法 —— 插件对"图形库"这一层的贡献。
/// </summary>
/// <remarks>
/// 一个对象 = <b>一个</b>视觉元素（<see cref="TriangleVisual"/>），
/// 外轮廓 / 两条直角边的刻度与数字都在它自己的 <c>OnRender</c> 里画。
/// 不做"一个图元一条刻度"：拖动的代价是逐图元算变换，一体机上当场能感觉到。
/// 也绕开 <c>Shape</c> 的 <c>Stretch=None</c> 平移坑（照量角器的注释）。
/// </remarks>
public sealed class TriangleRenderer : IGfxObjectRenderer
{
    /// <summary>这个渲染器负责的对象种类。</summary>
    public const string KindName = "triangle";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <summary>
    /// 本地包围盒：以直角顶点为中心、边长 2L 的<b>正方形</b>。
    /// </summary>
    /// <remarks>
    /// 三角板本体只占第一象限（直角顶点在原点、两条直角边沿 +x 与 −y），
    /// 但宿主约定"本地原点就是旋转中心、包围盒以原点为中心"。
    /// 于是包围盒声明成绕直角顶点的正方形，三角板只占它的右上半。
    /// 代价：选中框偏大、斜边外侧空白也能按住（一体机上反而好按）；
    /// 收益：绕直角顶点旋转 = 三角板以直角顶点为轴转（核心用法）。
    /// </remarks>
    public Size Measure(IGfxObjectRef obj)
    {
        double leg = TriangleGeometry.LegOf(obj);
        return new Size(leg * 2.0, leg * 2.0);
    }

    /// <inheritdoc />
    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        var kind = TriangleGeometry.KindOf(obj);
        double leg = TriangleGeometry.LegOf(obj);
        return new TriangleVisual(kind, leg, obj.Color, obj.LineWorldWidth);
    }
}

/// <summary>
/// 三角板的矢量视觉：外轮廓 + 两条直角边的 cm 刻度与数字，全部在本地坐标画（原点 = 直角顶点）。
/// </summary>
/// <remarks>
/// 不设 <c>RenderTransform</c>、<c>Canvas.Left/Top</c>：位姿由宿主统一施加，
/// 只有一个矩阵真相源（契约第一条约定）。这里只管"以直角顶点为原点长什么样"。
/// </remarks>
internal sealed class TriangleVisual : FrameworkElement
{
    private readonly Geometry _outline;
    private readonly Geometry _ticksLong;
    private readonly Geometry _ticksShort;
    private readonly Geometry _labelsLong;
    private readonly Geometry _labelsShort;

    private readonly Brush _ink;
    private readonly Pen _edge;
    private readonly Pen _tickPen;

    public TriangleVisual(TriangleGeometry.TriangleKind kind, double leg, Color inkColor, double lineWorldWidth)
    {
        _outline = TriangleGeometry.BuildOutline(kind, leg);
        _ticksLong = TriangleGeometry.BuildTicks(kind, leg, alongLong: true);
        _ticksShort = TriangleGeometry.BuildTicks(kind, leg, alongLong: false);
        _labelsLong = TriangleGeometry.BuildLabels(kind, leg, alongLong: true);
        _labelsShort = TriangleGeometry.BuildLabels(kind, leg, alongLong: false);

        _ink = Frozen(new SolidColorBrush(inkColor));

        double edgeWidth = lineWorldWidth > 0 ? lineWorldWidth : 1.5;
        _edge = Frozen(new Pen(_ink, edgeWidth));

        // 刻度比外框细一档，避免把边涂成一团
        _tickPen = Frozen(new Pen(Frozen(new SolidColorBrush(WithAlpha(inkColor, 0xB4))),
                                  edgeWidth * 0.7));

        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    /// <remarks>绘制顺序：外轮廓 → 刻度 → 数字。数字最后画，保证不被刻度线穿过。</remarks>
    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawGeometry(null, _edge, _outline);
        drawingContext.DrawGeometry(null, _tickPen, _ticksLong);
        drawingContext.DrawGeometry(null, _tickPen, _ticksShort);
        drawingContext.DrawGeometry(_ink, null, _labelsLong);
        drawingContext.DrawGeometry(_ink, null, _labelsShort);
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
