using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Protractor;

/// <summary>
/// 量角器的<b>全部数学与几何</b>：盘面、双圈刻度、读数换算、两种吸附。
/// </summary>
/// <remarks>
/// 与 <c>RulerGeometry</c> 同一个套路：不依赖控件、不依赖 <c>IToolContext</c>，只吃坐标吐坐标。
/// 于是验收 harness 能把"肉眼绝对看不出来"的那些错一条条钉死 ——
/// 刻度画错 1°、半圆画到了下半边、旋转方向反了，屏幕上都是一把"看着很像量角器"的东西。
/// <para>
/// <b>本地坐标系的约定（这个类里最重要的一段）</b>：原点 = <b>圆心</b>（不是包围盒中心）。
/// 之所以必须是圆心：宿主让对象绕本地原点旋转 / 缩放，而量角器的用法就是
/// "圆心按在角的顶点上、转盘面去对齐角的另一条边" —— 原点若不在圆心，转一下圆心就跑了，
/// 老师每转一次都得重新摆位（M7.4 §4 的交互设计就垮了）。
/// </para>
/// <para>
/// 上半圆的点是"刻度值 a"的点：<c>a = 0</c> 在基线右端、<c>a = 90</c> 在正上方、<c>a = 180</c> 在左端。
/// 本地方向 <c>(cos a, −sin a)</c> —— y 轴向下，取负号才是"往上走"（这一步写反，
/// 整个盘面就会长在基线下面，而且看起来仍然是个半圆）。
/// </para>
/// </remarks>
public static class ProtractorGeometry
{
    // ---------------------------------------------------------------- 单位

    /// <summary>1 inch = 72 PDF point（世界坐标的定义，见 docs/01）。</summary>
    public const double PointsPerInch = 72.0;

    /// <summary>1 inch = 2.54 cm。</summary>
    public const double CentimetersPerInch = 2.54;

    // ---------------------------------------------------------------- 尺寸

    /// <summary>默认半径（世界单位）。110 world ≈ 3.9 cm ⇒ 盘面直径约 7.8 cm，接近学生用的那把小量角器。</summary>
    public const double DefaultRadiusWorld = 110.0;

    /// <summary>
    /// 最小半径。低于它每 1° 的刻度就短到看不见了 —— 一个读不出刻度的量角器等于没有。
    /// </summary>
    public const double MinRadiusWorld = 60.0;

    /// <summary>最大半径（≈14 cm）。再大就不是量角器而是门板了，且会盖住整页卷子。</summary>
    public const double MaxRadiusWorld = 400.0;

    /// <summary>数值参数名：半径。</summary>
    public const string RadiusKey = "radius";

    // ---------------------------------------------------------------- 交互常量

    /// <summary>按下到抬起小于此距离视为误触，不落对象（世界单位）。</summary>
    public const double MinDragWorld = 2.0;

    /// <summary>
    /// 基线方向吸附容差（度）。
    /// </summary>
    /// <remarks>
    /// 只吸 6°，<b>绝不吸附到 15° 网格</b>：量角器存在的全部意义就是量<b>任意</b>方向的角，
    /// 一旦按网格量化，"量出 37°"这件事在物理上就不可能发生了。
    /// 6° 的含义是：只管掉手抖，管不到"我就是要对齐这条歪 20° 的边"。
    /// </remarks>
    public const double LineSnapToleranceDegrees = 6.0;

    /// <summary>圆心吸附容差（世界单位）。24 world ≈ 0.85 cm —— 手指在一体机上是"胖"的。</summary>
    public const double CenterSnapToleranceWorld = 24.0;

    // ---------------------------------------------------------------- 刻度比例（全部相对半径）

    /// <summary>每 1° 的短刻度长度 / 半径（实物量角器的 1° 刻度本来就很短）。</summary>
    public const double TickMinorRatio = 0.032;

    /// <summary>每 5° 的中刻度长度 / 半径。</summary>
    public const double TickMediumRatio = 0.055;

    /// <summary>每 10° 的长刻度长度 / 半径。</summary>
    public const double TickMajorRatio = 0.085;

    /// <summary>
    /// 外圈数字的<b>底边</b>所在半径 / 半径。
    /// </summary>
    /// <remarks>
    /// 两个环都被"顶"在刻度内侧：刻度改短以后数字才能往外挪，弧长跟着变长、字才排得开。
    /// 所以刻度长度、两个环的比例、字号是<b>一组</b>数 —— 动一个必须回头量净缝。
    /// </remarks>
    public const double OuterLabelRadiusRatio = 0.860;

    /// <summary>内圈数字的<b>底边</b>所在半径 / 半径。</summary>
    public const double InnerLabelRadiusRatio = 0.745;

    /// <summary>
    /// 数字字号 / 半径。
    /// </summary>
    /// <remarks>
    /// 这个数不是"看着差不多"定的，是被<b>相邻两个字之间的净缝</b>顶住的：
    /// 双圈各 19 个数字、每 10° 一个，最挤的地方在内圈靠近顶部处 ——
    /// 那里两条半径方向的位移几乎抵消，只剩切向的那一点弧长。
    /// 字号再大一点，净缝就先变薄、再变负，屏幕上表现为"有点糊"到"糊成一团"。
    /// 想让学生看得更大，正路是把量角器本身拖大（半径变大、弧长跟着变长），
    /// 而不是加大字号 —— 所以验收直接量<b>38 个数字两两的净缝必须为正</b>。
    /// </remarks>
    public const double LabelFontRatio = 0.050;

    /// <summary>圆心标记的半长 / 半径。</summary>
    public const double CenterMarkRatio = 0.045;

    /// <summary>刻度总数：0…180 每 1° 一条。</summary>
    public const int TickCount = 181;

    // ---------------------------------------------------------------- 换算

    /// <summary>世界坐标长度 → 厘米。</summary>
    public static double ToCentimeters(double worldLength) => worldLength * CentimetersPerInch / PointsPerInch;

    /// <summary>把半径夹到 <see cref="MinRadiusWorld"/>…<see cref="MaxRadiusWorld"/>；异常值退回默认值。</summary>
    public static double ClampRadius(double radius)
    {
        if (double.IsNaN(radius) || double.IsInfinity(radius)) return DefaultRadiusWorld;
        if (radius < MinRadiusWorld) return MinRadiusWorld;
        if (radius > MaxRadiusWorld) return MaxRadiusWorld;
        return radius;
    }

    /// <summary>取对象的半径参数（缺失或损坏时退回默认值）。</summary>
    public static double RadiusOf(IGfxObjectRef obj)
        => ClampRadius(obj.GetNumber(RadiusKey, DefaultRadiusWorld));

    // ---------------------------------------------------------------- 角度

    /// <summary>规整到 <c>[0, 360)</c>。</summary>
    public static double Normalize360(double degrees)
    {
        double value = degrees % 360.0;
        if (value < 0) value += 360.0;
        return value == 0.0 ? 0.0 : value;
    }

    /// <summary>规整到 <c>[0, 180)</c> —— 直线没有正反方向，0° 与 180° 是同一条线。</summary>
    public static double NormalizeAxis(double degrees)
    {
        double value = degrees % 180.0;
        if (value < 0) value += 180.0;
        return value == 0.0 ? 0.0 : value;
    }

    /// <summary>两点连线的轴角，结果在 <c>[0, 180)</c>。</summary>
    public static double AxisDegrees(Point from, Point to)
        => from == to ? 0.0 : NormalizeAxis(Math.Atan2(to.Y - from.Y, to.X - from.X) * 180.0 / Math.PI);

    /// <summary>
    /// 从 <paramref name="center"/> 指向 <paramref name="target"/> 的<b>有向角</b>，结果在 <c>[0, 360)</c>。
    /// </summary>
    /// <remarks>
    /// 这就是量角器的朝向 —— 基线正方向指向老师拖出去的那一边。
    /// 它<b>不能</b>用轴角（会丢方向，拖到左边却立到右边去）。
    /// </remarks>
    public static double OrientDegrees(Point center, Point target)
        => center == target ? 0.0 : Normalize360(Math.Atan2(target.Y - center.Y, target.X - center.X) * 180.0 / Math.PI);

    /// <summary>两个方向角之间的最小夹角（度，恒在 <c>[0, 180]</c>）。</summary>
    public static double AngleDelta(double a, double b)
    {
        double delta = Math.Abs(Normalize360(a) - Normalize360(b));
        return delta > 180.0 ? 360.0 - delta : delta;
    }

    /// <summary>刻度值 <paramref name="scaleValueDegrees"/> 对应的<b>单位方向</b>（本地坐标）。</summary>
    public static Vector LocalDirection(double scaleValueDegrees)
    {
        double radians = scaleValueDegrees * Math.PI / 180.0;

        // y 轴向下 ⇒ 取负号，刻度值增大才是从右端（0°）经正上方（90°）转到左端（180°）
        return new Vector(Math.Cos(radians), -Math.Sin(radians));
    }

    /// <summary>刻度值对应的本地坐标点。</summary>
    public static Point LocalPoint(double radius, double scaleValueDegrees)
    {
        var direction = LocalDirection(scaleValueDegrees);
        return new Point(radius * direction.X, radius * direction.Y);
    }

    /// <summary>基线正方向（对象旋转角）在世界里的角度 ⇒ 刻度值 a 在世界里的方向角 = 旋转角 − a。</summary>
    public static double WorldDegreesOf(double rotationDegrees, double scaleValueDegrees)
        => Normalize360(rotationDegrees - scaleValueDegrees);

    // ---------------------------------------------------------------- 吸附

    /// <summary>
    /// 把朝向吸附到一条线段的轴方向（线段没有正反，两个方向都算候选）。
    /// </summary>
    /// <param name="orientation">当前朝向（度）。</param>
    /// <param name="axisDegrees">目标线段的轴角（度，<c>[0,180)</c>）。</param>
    /// <param name="snapped">吸附后的朝向；<b>没吸上就是原值</b>（不是最接近的候选）。</param>
    /// <param name="tolerance">容差（度）。</param>
    /// <returns>是否真的吸上了。</returns>
    public static bool TrySnapOrientation(double orientation, double axisDegrees, out double snapped,
                                          double tolerance = LineSnapToleranceDegrees)
    {
        double normalized = Normalize360(orientation);
        double best = normalized;
        double bestDelta = double.MaxValue;

        // 一条 37° 的线，正反两个方向（37° 与 217°）都能对齐 —— 少算一个方向，
        // 老师把笔往左下拖时就吸不上（那是最容易漏的边界）。
        foreach (double candidate in new[] { Normalize360(axisDegrees), Normalize360(axisDegrees + 180.0) })
        {
            double delta = AngleDelta(orientation, candidate);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = candidate;
            }
        }

        // 没吸上必须交回"原值"，而不是"最接近的那个候选"：调用方拿到 false 时
        // 常直接沿用这里给出的朝向，交回候选会让盘面凭空调转到一个谁都没要求的角度
        // （而且屏幕上只表现为"差几度"，最不容易被当回事）。
        if (bestDelta <= tolerance)
        {
            snapped = best;
            return true;
        }

        snapped = normalized;
        return false;
    }

    /// <summary>
    /// 从一堆候选线段里挑"方向最接近当前朝向"的那条来吸附。
    /// </summary>
    /// <remarks>
    /// 挑的是<b>方向最接近</b>而不是<b>离得最近</b>：离圆心最近的那条线可能是页边的表格线，
    /// 而老师心里要对的明明是他刚画的那条。方向差在容差内的才有资格参与竞争。
    /// </remarks>
    public static bool TrySnapOrientationToSegments(double orientation, IReadOnlyList<BoardSegment> segments,
                                                    out double snapped,
                                                    double tolerance = LineSnapToleranceDegrees)
    {
        snapped = Normalize360(orientation);
        if (segments is null || segments.Count == 0) return false;

        bool found = false;
        double bestDelta = double.MaxValue;

        foreach (var segment in segments)
        {
            if (!TrySnapOrientation(orientation, segment.AxisDegrees, out double candidate, tolerance)) continue;

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

    /// <summary>
    /// 点到<b>线段</b>的距离。
    /// </summary>
    /// <remarks>
    /// 必须夹到线段上，不能按无限长直线算：差这一个截断，
    /// 圆心就会吸附到"那条线的延长线上的空气里"—— 屏幕上什么都看不出来，位置却错了。
    /// <para>
    /// 这里自己写一份而不是复用宿主的 <c>BoardQuery</c>：插件<b>只引用契约</b>，
    /// 看不到宿主的类型（那正是插件形态的定义）。十几行数学，值得各自留一份。
    /// </para>
    /// </remarks>
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

    // ---------------------------------------------------------------- 几何

    /// <summary>盘面：基线 + 上半圆弧，闭合的半圆盘（本地坐标，圆心在原点）。</summary>
    /// <remarks>
    /// 圆弧的 <c>SweepDirection</c> 是这一句里唯一会写错的地方，而且写错的表现很迷惑：
    /// 半圆会长在基线<b>下面</b>，看上去仍然是一把量角器（很多真实量角器就长那样）。
    /// 所以验收里钉的是<b>几何包围盒必须是 <c>[-R, R] × [−R, 0]</c></b> —— 落在下半边就报错。
    /// </remarks>
    public static StreamGeometry BuildDisc(double radius)
    {
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(-radius, 0), isFilled: true, isClosed: true);

            // 基线左端 → 正上方 → 基线右端：屏幕上 9 点经 12 点到 3 点，是顺时针
            context.ArcTo(new Point(radius, 0), new Size(radius, radius), 0.0,
                          isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);

            // 基线的直边：真实量角器的底边就是一条直线，且它是"对齐"用来看的那条边
            context.LineTo(new Point(-radius, 0), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>某个刻度值的刻度长度比例（10° 最长、5° 次之、1° 最短）。</summary>
    public static double TickRatioFor(int scaleValueDegrees)
    {
        int value = ((scaleValueDegrees % 180) + 180) % 180;

        if (value % 10 == 0) return TickMajorRatio;
        if (value % 5 == 0) return TickMediumRatio;
        return TickMinorRatio;
    }

    /// <summary>全部刻度线（0…180 每 1° 一条）+ 圆心标记，合成一个几何（一条 Path 就够，别画 181 个图元）。</summary>
    public static StreamGeometry BuildTicks(double radius)
    {
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            for (int value = 0; value < TickCount; value++)
            {
                var outer = LocalPoint(radius, value);
                var inner = LocalPoint(radius * (1.0 - TickRatioFor(value)), value);

                context.BeginFigure(outer, isFilled: false, isClosed: false);
                context.LineTo(inner, isStroked: true, isSmoothJoin: false);
            }

            // 圆心标记只画"×"的上半边（两条从圆心出发、各向上外侧 45° 的短线）。
            // 两条都不能画：完整"×"的下半边会伸到基线**下面** —— 那里已经是卷面、不是盘面，
            // 屏幕上表现为盘外浮着两条短线，几何包围盒还会多出半个标记的高度；
            // "+" 的横线则正好压在基线上，等于白画（基线本身就是一条线）。
            // 顶点落在圆心，肉眼看到的是一个指向圆心的"∨"，正好用来定位。
            double half = radius * CenterMarkRatio * 0.7071067811865476;

            foreach (double signX in new[] { -1.0, 1.0 })
            {
                context.BeginFigure(new Point(0, 0), isFilled: false, isClosed: false);
                context.LineTo(new Point(half * signX, -half), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 盘面上一个刻度数字的排布（本地坐标）。
    /// </summary>
    /// <remarks>
    /// 数字最终是<b>几何</b>（见 <see cref="BuildDigits"/>），但"排在哪儿、占多宽"必须能单独量出来：
    /// 38 个数字挤不挤，在屏幕上只表现为"有点糊"，而两个数字一旦叠上，学生读出来的读数就是错的。
    /// 于是把排布做成一份可读的清单，画法从它派生 —— <b>一处真相，两处用途</b>；
    /// 排布改了，量的和画的一定同时改，验收才有意义。
    /// </remarks>
    public readonly record struct ProtractorLabel(double RingRadiusRatio, double ScaleValueDegrees,
                                                  string Text, Geometry Geometry)
    {
        /// <summary>数字的实际包围盒（本地坐标，已含"底边贴环"的摆放偏移）。</summary>
        public Rect Bounds => Geometry.Bounds;
    }

    /// <summary>当前盘面上的全部刻度数字：外圈 <c>0→180</c>、内圈 <c>180→0</c>，各 19 个。</summary>
    public static IReadOnlyList<ProtractorLabel> LayoutLabels(double radius)
    {
        double emSize = radius * LabelFontRatio;
        var typeface = LabelTypeface();
        var labels = new List<ProtractorLabel>(38);

        for (int value = 0; value <= 180; value += 10)
        {
            // 外圈：刻度值本身；内圈：180 − 刻度值（同一条刻度线的另一种读法）
            labels.Add(MakeLabel(radius, OuterLabelRadiusRatio, value, value, typeface, emSize));
            labels.Add(MakeLabel(radius, InnerLabelRadiusRatio, value, 180 - value, typeface, emSize));
        }

        return labels;
    }

    /// <summary>
    /// 双圈刻度的数字，合成一个几何（数字是"画"出来的而不是控件 —— 38 个 TextBlock 会拖慢拖动，
    /// 而且文字控件在缩放层里会被反复重新排版）。
    /// </summary>
    /// <remarks>
    /// <b>双圈</b>是实物量角器的标准：外圈 <c>0→180</c>、内圈 <c>180→0</c>，同一个刻度线读两遍。
    /// 只画一圈的话，量开口朝左的角时学生要自己心算 <c>180 − 读数</c> —— 那是出错的重灾区。
    /// <para>
    /// 数字一律<b>正立</b>（跟着盘面转），与实物一致；盘面转到 180° 时数字自然是倒的，
    /// 那也是实物的样子，老师会本能地把它转回来看。
    /// </para>
    /// <para>
    /// 这里<b>不含任何排布逻辑</b>：位置全部来自 <see cref="LayoutLabels"/>。
    /// </para>
    /// </remarks>
    public static Geometry BuildDigits(double radius)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };

        foreach (var label in LayoutLabels(radius))
        {
            group.Children.Add(label.Geometry);
        }

        group.Freeze();
        return group;
    }

    private static ProtractorLabel MakeLabel(double radius, double ringRatio, double scaleValueDegrees,
                                             int displayValue, Typeface typeface, double emSize)
    {
        string text = displayValue.ToString(CultureInfo.InvariantCulture);
        var geometry = TextGeometry(radius * ringRatio, scaleValueDegrees, text, typeface, emSize);
        return new ProtractorLabel(ringRatio, scaleValueDegrees, text, geometry);
    }

    /// <summary>刻度数字用的字体：与全局一致（微软雅黑、半粗）。</summary>
    private static Typeface LabelTypeface()
        => new(new FontFamily("Microsoft YaHei"),
               FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>
    /// 把一段文字转成"底边贴住刻度环该处"的几何。
    /// </summary>
    /// <remarks>
    /// 是<b>底边</b>贴住而不是中心对齐：中心对齐会让 <c>0</c> 与 <c>180</c> 这两个数字
    /// 有一半掉到基线下面去 —— 实物量角器上没有这种印法，而且那半截数字等于印在了盘外。
    /// 底边贴住以后，所有数字都完整落在盘面上（验收断言里"没有一个越过基线"就是钉这条）。
    /// <para>
    /// 文字是往<b>环外</b>长（不是往圆心内长），所以"环的半径 + 字高"就是这一圈数字的外沿：
    /// 外圈的外沿必须留在刻度内侧、内圈的外沿必须与外圈留开一条缝 —— 这两条也都在验收里量。
    /// </para>
    /// </remarks>
    private static Geometry TextGeometry(double ringRadius, double scaleValueDegrees, string text,
                                         Typeface typeface, double emSize)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                          typeface, emSize, Brushes.Black, pixelsPerDip: 1.0);

        var geometry = formatted.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        var anchor = LocalPoint(ringRadius, scaleValueDegrees);

        geometry.Transform = new TranslateTransform(
            anchor.X - (bounds.X + bounds.Width / 2.0),
            anchor.Y - bounds.Bottom);

        geometry.Freeze();
        return geometry;
    }

    // ---------------------------------------------------------------- 状态栏文案

    /// <summary>给状态栏用的一句话。一体机上没有键盘，状态栏是老师唯一的"读数"。</summary>
    public static string Describe(double radiusWorld, double orientationDegrees, bool snapped)
    {
        string radius = ToCentimeters(radiusWorld).ToString("F1");
        string diameter = ToCentimeters(radiusWorld * 2.0).ToString("F1");
        string angle = Normalize360(orientationDegrees).ToString("F0");

        return $"量角器：半径 {radius} cm（直径 {diameter} cm），基线 {angle}°"
             + (snapped ? "（已对齐卷面上的线）" : string.Empty);
    }
}
