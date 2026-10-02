using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Compass;

/// <summary>
/// 整圆渲染器：对象种类 <c>compass-circle</c>。
/// </summary>
/// <remarks>
/// 与量角器同一个套路：一个对象 = 一个视觉元素，本地坐标原点 = 圆心，
/// 不碰 <c>RenderTransform</c> / <c>Canvas.Left/Top</c>（位姿由宿主统一施加）。
/// 圆规落成的对象 <c>Scale</c> 恒为 1，几何就是世界长度，线宽直接用世界线宽。
/// </remarks>
public sealed class CompassCircleRenderer : IGfxObjectRenderer
{
    /// <summary>这个渲染器负责的对象种类。</summary>
    public const string KindName = "compass-circle";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public Size Measure(IGfxObjectRef obj)
    {
        double radius = GeometryHelper.RadiusOf(obj);
        return new Size(radius * 2.0, radius * 2.0);
    }

    /// <inheritdoc />
    public FrameworkElement CreateVisual(IGfxObjectRef obj)
        => new CompassCircleVisual(
            GeometryHelper.RadiusOf(obj),
            obj.Color,
            obj.LineWorldWidth);
}

/// <summary>整圆视觉：圆周 + 圆心十字（针尖扎在纸上的那个点）。</summary>
internal sealed class CompassCircleVisual : FrameworkElement
{
    private readonly Geometry _circle;
    private readonly Geometry _centerMark;
    private readonly Pen _pen;
    private readonly Pen _markPen;

    public CompassCircleVisual(double radius, Color inkColor, double lineWorldWidth)
    {
        _circle = GeometryHelper.BuildCircleGeometry(radius);
        _centerMark = GeometryHelper.BuildCenterMark(radius);

        double stroke = lineWorldWidth > 0 ? lineWorldWidth : 1.5;
        var ink = Frozen(new SolidColorBrush(inkColor));
        _pen = Frozen(new Pen(ink, stroke));

        // 圆心十字比圆周细一档、稍淡一点：它是"针眼"的提示，不是第二条边线
        _markPen = Frozen(new Pen(Frozen(new SolidColorBrush(WithAlpha(inkColor, 0xB4))), stroke * 0.7));

        // 图形层整体已退出命中测试（宿主保证），这里再兜一次：白板上输入绝不被图元抢走
        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawGeometry(null, _pen, _circle);
        drawingContext.DrawGeometry(null, _markPen, _centerMark);
    }

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>冻结：WPF 对冻结的画笔 / 几何跳过变更通知，一次建好多次重画的东西值得。</summary>
    private static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}

/// <summary>
/// 任意夹角圆弧渲染器：对象种类 <c>compass-arc</c>。
/// </summary>
/// <remarks>
/// 画三样东西：两条起 / 止半径指引线、逆时针圆弧本体、弧外侧的角度数字。
/// 起止角相等时 <see cref="GeometryHelper.BuildArcGeometry"/> 退化成整圆、数字显示 360°。
/// </remarks>
public sealed class CompassArcRenderer : IGfxObjectRenderer
{
    /// <summary>这个渲染器负责的对象种类。</summary>
    public const string KindName = "compass-arc";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public Size Measure(IGfxObjectRef obj)
    {
        // 2.5R：角度数字摆在 1.12R 外侧，要把它的外扩余量也算进包围盒
        double radius = GeometryHelper.RadiusOf(obj);
        return new Size(radius * 2.5, radius * 2.5);
    }

    /// <inheritdoc />
    public FrameworkElement CreateVisual(IGfxObjectRef obj)
        => new CompassArcVisual(
            GeometryHelper.RadiusOf(obj),
            GeometryHelper.StartOf(obj),
            GeometryHelper.EndOf(obj),
            obj.Color,
            obj.LineWorldWidth);
}

/// <summary>圆弧视觉：指引线 → 弧本体 → 角度数字，本地坐标原点 = 圆心。</summary>
internal sealed class CompassArcVisual : FrameworkElement
{
    private readonly Geometry _arc;
    private readonly Geometry _guides;
    private readonly Geometry _label;
    private readonly Pen _pen;
    private readonly Pen _guidePen;
    private readonly Brush _ink;

    public CompassArcVisual(double radius, double start, double end, Color inkColor, double lineWorldWidth)
    {
        _arc = GeometryHelper.BuildArcGeometry(radius, start, end);
        _guides = GeometryHelper.BuildArcGuides(radius, start, end);
        _label = GeometryHelper.BuildAngleLabel(radius, start, end);

        double stroke = lineWorldWidth > 0 ? lineWorldWidth : 1.5;
        var ink = Frozen(new SolidColorBrush(inkColor));
        _pen = Frozen(new Pen(ink, stroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        });

        // 指引线是辅助线：比弧本体细、更淡
        _guidePen = Frozen(new Pen(Frozen(new SolidColorBrush(WithAlpha(inkColor, 0x78))), stroke * 0.6));
        _ink = ink;

        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawGeometry(null, _guidePen, _guides);
        drawingContext.DrawGeometry(null, _pen, _arc);
        drawingContext.DrawGeometry(_ink, null, _label);
    }

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
