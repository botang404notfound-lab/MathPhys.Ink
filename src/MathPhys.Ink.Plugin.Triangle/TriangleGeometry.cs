using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Triangle;

/// <summary>
/// 三角板的<b>全部数学与几何</b>：两板外轮廓、cm 刻度、一条直角边的方向吸附、单位换算。
/// </summary>
/// <remarks>
/// 与 <see cref="ProtractorGeometry"/> 同一个套路：不依赖控件、不依赖 <see cref="IToolContext"/>，
/// 只吃坐标吐坐标 —— 直角是不是 90°、60° 板的边长比是不是 1:√3、刻度是不是 10 格 1 cm，
/// 这些"差一点点也看不出来"的错，只有断言钉得死。
/// <para>
/// <b>本地坐标系的约定（这个类里最重要的一段）</b>：原点 = <b>直角顶点</b>（不是包围盒中心）。
/// 三角板的核心用法是"把直角顶点按在角的顶点上、贴一条直角边去比角度"，
/// 而宿主让对象绕本地原点旋转 —— 原点若不在直角顶点，转一下直角就跑了，
/// 老师每转一次都得重新摆位（与量角器"原点 = 圆心"是同一条不变量，M7.4 §4）。
/// </para>
/// <para>
/// 两条直角边分别沿 <b>+x 轴</b> 与 <b>−y 轴</b>（世界坐标 y 轴向下，所以"向上"是 −y）。
/// 斜边连接两条直角边的末端。
/// </para>
/// </remarks>
public static class TriangleGeometry
{
    // ---------------------------------------------------------------- 单位

    /// <summary>1 inch = 72 PDF point（世界坐标的定义，见 docs/01）。</summary>
    public const double PointsPerInch = 72.0;

    /// <summary>1 inch = 2.54 cm。</summary>
    public const double CentimetersPerInch = 2.54;

    // ---------------------------------------------------------------- 尺寸

    /// <summary>默认直角边长（世界单位）。110 world ≈ 3.9 cm，与量角器默认半径一致，两把工具摆一起协调。</summary>
    public const double DefaultLegWorld = 110.0;

    /// <summary>最小直角边长。低于它 1 mm 刻度就糊成一团，等于没刻度。</summary>
    public const double MinLegWorld = 60.0;

    /// <summary>最大直角边长（≈14 cm，与量角器最大半径一致）。</summary>
    public const double MaxLegWorld = 400.0;

    /// <summary>数值参数名：直角边长（45° 板与 60° 板都取"长直角边"为 leg）。</summary>
    public const string LegKey = "leg";

    /// <summary>数值参数名：板型。0 = 45-45-90 等腰；1 = 30-60-90。</summary>
    public const string KindKey = "kind";

    // ---------------------------------------------------------------- 交互常量

    /// <summary>按下到抬起小于此距离视为误触，不落对象（世界单位）。</summary>
    public const double MinDragWorld = 2.0;

    /// <summary>一条直角边方向吸附容差（度）。只吸已有直线方向，绝不吸 15° 网格。</summary>
    public const double LineSnapToleranceDegrees = 6.0;

    /// <summary>直角顶点吸附容差（世界单位）。24 world ≈ 0.85 cm，手指在一体机上是"胖"的。</summary>
    public const double VertexSnapToleranceWorld = 24.0;

    // ---------------------------------------------------------------- 刻度

    /// <summary>1 mm 在世界坐标里的长度（72 / 25.4 ≈ 2.835）。</summary>
    public const double OneMillimeterWorld = PointsPerInch / (CentimetersPerInch * 10.0);

    /// <summary>刻度线的长度 / 直角边长（短刻度）。</summary>
    public const double TickMinorRatio = 0.025;

    /// <summary>每 5 mm 的中刻度长度 / 直角边长。</summary>
    public const double TickMediumRatio = 0.045;

    /// <summary>每 10 mm（=1 cm）的长刻度长度 / 直角边长。</summary>
    public const double TickMajorRatio = 0.070;

    /// <summary>刻度数字的字号 / 直角边长。</summary>
    public const double LabelFontRatio = 0.050;

    // ---------------------------------------------------------------- 换算

    /// <summary>世界坐标长度 → 厘米。</summary>
    public static double ToCentimeters(double worldLength)
        => worldLength * CentimetersPerInch / PointsPerInch;

    /// <summary>把直角边长夹到 <see cref="MinLegWorld"/>…<see cref="MaxLegWorld"/>；异常值退回默认。</summary>
    public static double ClampLeg(double leg)
    {
        if (double.IsNaN(leg) || double.IsInfinity(leg)) return DefaultLegWorld;
        if (leg < MinLegWorld) return MinLegWorld;
        if (leg > MaxLegWorld) return MaxLegWorld;
        return leg;
    }

    /// <summary>读对象的直角边长参数（缺失或损坏退回默认）。</summary>
    public static double LegOf(IGfxObjectRef obj) => ClampLeg(obj.GetNumber(LegKey, DefaultLegWorld));

    /// <summary>读对象的板型参数（0 = 45° 板，其它一律按 60° 板）。</summary>
    public static TriangleKind KindOf(IGfxObjectRef obj)
        => obj.GetNumber(KindKey, 0) >= 1.0 ? TriangleKind.SixtyThirtyNinety : TriangleKind.FortyFive;

    // ---------------------------------------------------------------- 板型

    /// <summary>
    /// 两种三角板。
    /// </summary>
    public enum TriangleKind
    {
        /// <summary>45-45-90 等腰直角三角形。</summary>
        FortyFive = 0,

        /// <summary>30-60-90 直角三角形（直角边比 1:√3，60° 角在原点）。</summary>
        SixtyThirtyNinety = 1,
    }

    /// <summary>
    /// 长直角边的长度（世界单位）—— 两板都以它作为"尺寸"这个参数。
    /// </summary>
    /// <remarks>
    /// 45° 板：两条直角边等长，leg = 任意一条。
    /// 60° 板：leg = 长直角边（沿 +x 的那条），短直角边 = leg / √3。
    /// 统一用 leg 的好处：工具拖动时"离直角顶点多远"这个量对两板是同一个语义。
    /// </remarks>
    public static double LongLegWorld(TriangleKind kind, double leg) => leg;

    /// <summary>短直角边的长度（世界单位）。45° 板两条直角边等长，返回 leg。</summary>
    public static double ShortLegWorld(TriangleKind kind, double leg)
        => kind == TriangleKind.SixtyThirtyNinety ? leg / Math.Sqrt(3.0) : leg;

    /// <summary>斜边与长直角边（+x 轴）的夹角（度）。45° 板 = 45，60° 板 = 30。</summary>
    public static double HypotenuseAngleDegrees(TriangleKind kind)
        => kind == TriangleKind.SixtyThirtyNinety ? 30.0 : 45.0;

    // ---------------------------------------------------------------- 几何

    /// <summary>三个顶点（本地坐标，直角顶点在原点）。顺序：直角顶点 → 长直角边末端 → 短直角边末端。</summary>
    public static IReadOnlyList<Point> Vertices(TriangleKind kind, double leg)
    {
        double longLeg = LongLegWorld(kind, leg);
        double shortLeg = ShortLegWorld(kind, leg);

        // 直角顶点在原点；长直角边沿 +x，短直角边沿 −y（y 向下）
        return new[]
        {
            new Point(0, 0),
            new Point(longLeg, 0),
            new Point(0, -shortLeg),
        };
    }

    /// <summary>外轮廓：闭合三角形（本地坐标）。</summary>
    public static StreamGeometry BuildOutline(TriangleKind kind, double leg)
    {
        var vertices = Vertices(kind, leg);
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(vertices[0], isFilled: true, isClosed: true);
            context.LineTo(vertices[1], isStroked: true, isSmoothJoin: false);
            context.LineTo(vertices[2], isStroked: true, isSmoothJoin: false);
            // 闭合：第三个顶点 → 回到直角顶点由 isClosed 自动补上
        }

        geometry.Freeze();
        return geometry;
    }

    // ---------------------------------------------------------------- 刻度

    /// <summary>某处刻度值的刻度长度比例（整厘米最长、半厘米次之、1 mm 最短）。</summary>
    public static double TickRatioFor(int millimeters)
    {
        if (millimeters % 10 == 0) return TickMajorRatio;
        if (millimeters % 5 == 0) return TickMediumRatio;
        return TickMinorRatio;
    }

    /// <summary>
    /// 沿一条直角边的全部刻度（1 mm 一小格），合成一个几何。
    /// </summary>
    /// <param name="kind">板型。</param>
    /// <param name="leg">直角边长。</param>
    /// <param name="alongLong">true = 沿长直角边（+x 轴）；false = 沿短直角边（−y 轴）。</param>
    /// <remarks>
    /// 刻度画在直角边<b>内侧</b>（往三角形内部伸），这样刻度不会戳出轮廓外。
    /// 每条刻度从直角边上的分度点出发，往垂直于该直角边、指向三角形内部的方向画一段。
    /// 长直角边（+x）的内侧是 −y 方向；短直角边（−y）的内侧是 +x 方向。
    /// </remarks>
    public static StreamGeometry BuildTicks(TriangleKind kind, double leg, bool alongLong)
    {
        double length = alongLong ? LongLegWorld(kind, leg) : ShortLegWorld(kind, leg);
        int totalMillimeters = (int)Math.Floor(ToCentimeters(length) * 10.0 + 1e-6);

        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            for (int mm = 1; mm <= totalMillimeters; mm++)
            {
                double distWorld = mm * OneMillimeterWorld;
                double tickLength = leg * TickRatioFor(mm);

                Point onEdge;
                Point inner;

                if (alongLong)
                {
                    // 长直角边沿 +x，分度点在 (dist, 0)，内侧是 −y
                    onEdge = new Point(distWorld, 0);
                    inner = new Point(distWorld, -tickLength);
                }
                else
                {
                    // 短直角边沿 −y，分度点在 (0, -dist)，内侧是 +x
                    onEdge = new Point(0, -distWorld);
                    inner = new Point(tickLength, -distWorld);
                }

                context.BeginFigure(onEdge, isFilled: false, isClosed: false);
                context.LineTo(inner, isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 沿一条直角边的刻度数字（整厘米），合成一个几何。
    /// </summary>
    /// <remarks>
    /// 数字画在直角边<b>外侧</b>（与刻度相反的方向），这样数字不会压住刻度线。
    /// 长直角边（+x）的数字在边下方（+y 方向）；短直角边（−y）的数字在边左侧（−x 方向）。
    /// 数字是几何不是控件（照量角器的做法，避免几十个 TextBlock 拖慢拖动）。
    /// </remarks>
    public static Geometry BuildLabels(TriangleKind kind, double leg, bool alongLong)
    {
        double length = alongLong ? LongLegWorld(kind, leg) : ShortLegWorld(kind, leg);
        int totalCentimeters = (int)Math.Floor(ToCentimeters(length) + 1e-6);

        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        var typeface = new Typeface(new FontFamily("Microsoft YaHei"),
                                    FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double emSize = leg * LabelFontRatio;

        for (int cm = 1; cm <= totalCentimeters; cm++)
        {
            double distWorld = cm * 10.0 * OneMillimeterWorld;

            // 数字的中心：长直角边在 (dist, +字高)，短直角边在 (−字宽, −dist)
            // 用一个粗略的锚点，再用 BuildGeometry 的包围盒做居中对齐
            var formatted = new FormattedText(cm.ToString(CultureInfo.InvariantCulture),
                                              CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                              typeface, emSize, Brushes.Black, pixelsPerDip: 1.0);
            var textGeometry = formatted.BuildGeometry(new Point(0, 0));
            var bounds = textGeometry.Bounds;

            double cx = alongLong ? distWorld : -(bounds.Width + leg * 0.04);
            double cy = alongLong ? bounds.Height + leg * 0.04 : -distWorld;

            textGeometry.Transform = new TranslateTransform(
                cx - (bounds.X + bounds.Width / 2.0),
                cy - (bounds.Y + bounds.Height / 2.0));

            textGeometry.Freeze();
            group.Children.Add(textGeometry);
        }

        group.Freeze();
        return group;
    }

    // ---------------------------------------------------------------- 吸附

    /// <summary>规整到 <c>[0, 360)</c>。</summary>
    public static double Normalize360(double degrees)
    {
        double value = degrees % 360.0;
        if (value < 0) value += 360.0;
        return value == 0.0 ? 0.0 : value;
    }

    /// <summary>规整到 <c>[0, 180)</c> —— 直线没有正反方向。</summary>
    public static double NormalizeAxis(double degrees)
    {
        double value = degrees % 180.0;
        if (value < 0) value += 180.0;
        return value == 0.0 ? 0.0 : value;
    }

    /// <summary>两点连线的轴角，结果在 <c>[0, 180)</c>。</summary>
    public static double AxisDegrees(Point from, Point to)
        => from == to ? 0.0 : NormalizeAxis(Math.Atan2(to.Y - from.Y, to.X - from.X) * 180.0 / Math.PI);

    /// <summary>从 <paramref name="center"/> 指向 <paramref name="target"/> 的有向角，<c>[0, 360)</c>。</summary>
    public static double OrientDegrees(Point center, Point target)
        => center == target ? 0.0 : Normalize360(Math.Atan2(target.Y - center.Y, target.X - center.X) * 180.0 / Math.PI);

    /// <summary>两个方向角之间的最小夹角（度，恒在 <c>[0, 180]</c>）。</summary>
    public static double AngleDelta(double a, double b)
    {
        double delta = Math.Abs(Normalize360(a) - Normalize360(b));
        return delta > 180.0 ? 360.0 - delta : delta;
    }

    /// <summary>
    /// 把三角板的一条直角边吸附到某条线段的方向。
    /// </summary>
    /// <remarks>
    /// 一条直角边是"直线"（没有正反），所以正反两个方向都算候选 —— 少算一个，
    /// 老师把笔往相反方向拖时就吸不上。
    /// 吸附后返回的是<b>直角边在世界里的朝向角</b>（有向，0~360），
    /// 因为三角板的旋转角是"直角边指向哪个方向"，不是"这条线是什么轴"。
    /// </remarks>
    public static bool TrySnapEdge(double edgeOrientation, double axisDegrees, out double snapped,
                                   double tolerance = LineSnapToleranceDegrees)
    {
        double normalized = Normalize360(edgeOrientation);
        double best = normalized;
        double bestDelta = double.MaxValue;

        foreach (double candidate in new[] { Normalize360(axisDegrees), Normalize360(axisDegrees + 180.0) })
        {
            double delta = AngleDelta(edgeOrientation, candidate);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = candidate;
            }
        }

        if (bestDelta <= tolerance)
        {
            snapped = best;
            return true;
        }

        snapped = normalized;
        return false;
    }

    /// <summary>从一堆候选线段里挑"方向最接近当前朝向"的那条来吸附。</summary>
    public static bool TrySnapEdgeToSegments(double orientation, IReadOnlyList<BoardSegment> segments,
                                             out double snapped,
                                             double tolerance = LineSnapToleranceDegrees)
    {
        snapped = Normalize360(orientation);
        if (segments is null || segments.Count == 0) return false;

        bool found = false;
        double bestDelta = double.MaxValue;

        foreach (var segment in segments)
        {
            if (!TrySnapEdge(orientation, segment.AxisDegrees, out double candidate, tolerance)) continue;

            double delta = AngleDelta(orientation, candidate);
            if (delta >= bestDelta) continue;

            bestDelta = delta;
            snapped = candidate;
            found = true;
        }

        return found;
    }

    /// <summary>挑离 <paramref name="world"/> 最近、且在容差内的端点；没有就返回 <c>null</c>。</summary>
    public static Point? PickNearestPoint(Point world, IReadOnlyList<Point> candidates, double tolerance)
    {
        if (candidates is null) return null;

        Point? best = null;
        double bestDistance = tolerance;

        foreach (var candidate in candidates)
        {
            double distance = (candidate - world).Length;
            if (distance > bestDistance) continue;

            bestDistance = distance;
            best = candidate;
        }

        return best;
    }

    /// <summary>挑离 <paramref name="world"/> 最近、且在容差内的线段；没有就返回 <c>null</c>。</summary>
    public static BoardSegment? PickNearestSegment(Point world, IReadOnlyList<BoardSegment> segments, double tolerance)
    {
        if (segments is null) return null;

        BoardSegment? best = null;
        double bestDistance = tolerance;

        foreach (var segment in segments)
        {
            double distance = DistanceToSegment(world, segment.From, segment.To);
            if (distance > bestDistance) continue;

            bestDistance = distance;
            best = segment;
        }

        return best;
    }

    /// <summary>点到线段的距离（投影夹到 [0,1]，不按无限长直线算）。</summary>
    public static double DistanceToSegment(Point point, Point from, Point to)
        => (point - ClosestPointOn(point, from, to)).Length;

    /// <summary>点在指定线段上的最近点。</summary>
    public static Point ClosestPointOn(Point point, Point from, Point to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= double.Epsilon) return from;

        double t = ((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared;
        if (t < 0) t = 0;
        else if (t > 1) t = 1;

        return new Point(from.X + t * dx, from.Y + t * dy);
    }

    // ---------------------------------------------------------------- 状态栏文案

    /// <summary>给状态栏用的一句话。一体机上没有键盘，状态栏是老师唯一的"读数"。</summary>
    public static string Describe(TriangleKind kind, double legWorld, bool snapped)
    {
        string name = kind == TriangleKind.SixtyThirtyNinety ? "60° 板" : "45° 板";
        string leg = ToCentimeters(legWorld).ToString("F1");
        return $"三角板（{name}）：直角边 {leg} cm"
             + (snapped ? "（已贴齐卷面上的线）" : string.Empty);
    }
}
