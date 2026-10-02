using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Compass;

/// <summary>
/// 圆规 / 圆弧的全部数学与几何：角度换算、半径钳制、吸附、圆与弧的参数读取与几何构建。
/// </summary>
/// <remarks>
/// 纯静态函数：不依赖控件、不依赖 <see cref="IToolContext"/> 之外的东西 ——
/// 验收 harness 可以在没有窗口的环境里把"圆画圆了没有、弧方向反了没有、等角退化成整圆没有"
/// 一条条钉死（跟量角器 <c>ProtractorGeometry</c> 同一个套路）。
/// <para>
/// <b>角度约定（本类最重要的定义）</b>：老师屏幕上的"视觉逆时针"——
/// 0° = 正右方，90° = 正上方，180° = 正左方，270° = 正下方。
/// 世界坐标 y 轴向下，所以角度 σ 对应的本地单位方向是 <c>(cos σ, −sin σ)</c>，
/// 世界有向角换算则是 <c>σ = Normalize360(−Atan2(dy, dx)·180/π)</c>。
/// 符号写反的表现是弧长在圆的下半边 —— 屏幕上"看着仍然像圆弧"，只能靠断言逼出来。
/// </para>
/// <para>
/// 圆弧本体用 <see cref="StreamGeometryContext.ArcTo"/> 一次成型：
/// 起点 = <c>LocalPoint(R, start)</c>，终点 = <c>LocalPoint(R, end)</c>，
/// 扫描方向固定 <see cref="SweepDirection.Counterclockwise"/>（正是屏幕视觉逆时针），
/// <c>isLargeArc = sweep &gt; 180</c>。起止角相等（差 &lt; 0.05°）按<b>整圆</b>退化。
/// </para>
/// </remarks>
public static class GeometryHelper
{
    // ---------------------------------------------------------------- 单位

    /// <summary>1 inch = 72 PDF point（世界坐标的定义）。</summary>
    public const double PointsPerInch = 72.0;

    /// <summary>1 inch = 2.54 cm。</summary>
    public const double CentimetersPerInch = 2.54;

    // ---------------------------------------------------------------- 尺寸

    /// <summary>默认半径（世界单位）。110 world ≈ 3.9 cm，与量角器同一把"顺手"的尺寸。</summary>
    public const double DefaultRadiusWorld = 110.0;

    /// <summary>最小半径（世界单位）。低于它，弧上的控制点与角度数字都挤成一团。</summary>
    public const double MinRadiusWorld = 60.0;

    /// <summary>最大半径（世界单位）。再大就盖住整页卷子了。</summary>
    public const double MaxRadiusWorld = 400.0;

    // ---------------------------------------------------------------- 参数键

    /// <summary>数值参数名：半径。</summary>
    public const string RadiusKey = "radius";

    /// <summary>数值参数名：弧起点角（屏幕视觉角，0° = 正右）。</summary>
    public const string StartKey = "start";

    /// <summary>数值参数名：弧终点角（屏幕视觉角）。</summary>
    public const string EndKey = "end";

    // ---------------------------------------------------------------- 交互常量

    /// <summary>两次点击 / 拖动小于此距离视为误触（世界单位）。</summary>
    public const double MinDragWorld = 2.0;

    /// <summary>针尖吸附容差（世界单位）。24 world ≈ 0.85 cm —— 一体机上手指是"胖"的。</summary>
    public const double CenterSnapToleranceWorld = 24.0;

    /// <summary>控制点手柄的屏幕边长（像素）。与宿主选择手柄 <c>GfxTransform.HandleSizePixels</c> 同值。</summary>
    public const double HandleSizePixels = 16.0;

    /// <summary>抓控制点的屏幕容差（像素）。世界容差 = 像素容差 / 当前缩放（见 <see cref="HitTolerance"/>）。</summary>
    public const double HitTolerancePixels = 20.0;

    /// <summary>弧控制角的吸附步长（度）：拖到 15° 整数倍附近时吸过去。</summary>
    public const double AngleSnapStepDegrees = 15.0;

    /// <summary>弧控制角的吸附容差（度）：只吸 5° 内的手抖，不量化"任意夹角"本身。</summary>
    public const double AngleSnapToleranceDegrees = 5.0;

    /// <summary>起止角相差小于该值视为"相等"（退化整圆）。</summary>
    public const double EqualAngleEpsilonDegrees = 0.05;

    // ---------------------------------------------------------------- 视觉比例

    /// <summary>圆心十字标记的半长 / 半径。</summary>
    public const double CenterMarkRatio = 0.05;

    /// <summary>角度数字中心所在的半径 / 半径（落在圆弧外侧一点点）。</summary>
    public const double LabelRadiusRatio = 1.12;

    /// <summary>角度数字字号 / 半径。</summary>
    public const double LabelFontRatio = 0.09;

    // ---------------------------------------------------------------- 容差与缩放

    /// <summary>防御性的缩放取值：非正 / NaN / 无穷一律当 1.0，避免除出脏坐标。</summary>
    public static double SafeScale(double scale)
        => scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale) ? 1.0 : scale;

    /// <summary>抓控制点的世界容差 = 屏幕像素容差 ÷ 当前缩放（缩放越大，世界容差越小）。</summary>
    public static double HitTolerance(double scale) => HitTolerancePixels / SafeScale(scale);

    /// <summary>控制点手柄的世界边长 = 屏幕像素边长 ÷ 当前缩放（屏幕尺寸恒定，随缩放重算）。</summary>
    public static double ThumbWorldSize(double scale) => HandleSizePixels / SafeScale(scale);

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

    // ---------------------------------------------------------------- 角度

    /// <summary>规整到 <c>[0, 360)</c>。</summary>
    public static double Normalize360(double degrees)
    {
        double value = degrees % 360.0;
        if (value < 0) value += 360.0;
        return value == 0.0 ? 0.0 : value;
    }

    /// <summary>两个角度之间的最小夹角（度，恒在 <c>[0, 180]</c>）。</summary>
    public static double AngleDelta(double a, double b)
    {
        double delta = Math.Abs(Normalize360(a) - Normalize360(b));
        return delta > 180.0 ? 360.0 - delta : delta;
    }

    /// <summary>起止角是否相等（圆周差 &lt; <see cref="EqualAngleEpsilonDegrees"/>）。</summary>
    public static bool AnglesEqual(double start, double end)
        => AngleDelta(start, end) < EqualAngleEpsilonDegrees;

    /// <summary>从 <paramref name="start"/> 沿屏幕视觉逆时针转到 <paramref name="end"/> 的扫描角（度，<c>[0,360)</c>）。</summary>
    /// <remarks>起止角相等时返回 0 —— 调用方据此画整圆、显示 360°。</remarks>
    public static double Sweep(double start, double end)
        => Normalize360(end - start);

    /// <summary>
    /// 从 <paramref name="center"/> 指向 <paramref name="target"/> 的<b>屏幕视觉角</b>（度，<c>[0,360)</c>）。
    /// </summary>
    /// <remarks>
    /// 世界坐标 y 轴向下：<c>Atan2(dy, dx)</c> 正角朝下，取负号才是"往上走"。
    /// 写反的表现是 90° 落在正下方 —— 圆弧会整体翻到圆的下半边。
    /// </remarks>
    public static double ScreenAngle(Point center, Point target)
        => center == target ? 0.0
           : Normalize360(-Math.Atan2(target.Y - center.Y, target.X - center.X) * 180.0 / Math.PI);

    /// <summary>屏幕视觉角 <paramref name="screenDegrees"/> 对应的<b>单位方向</b>（本地坐标）。</summary>
    public static Vector LocalDirection(double screenDegrees)
    {
        double radians = screenDegrees * Math.PI / 180.0;

        // y 轴向下 ⇒ 取负号，角度增大才是"右 → 上 → 左"的视觉逆时针
        return new Vector(Math.Cos(radians), -Math.Sin(radians));
    }

    /// <summary>屏幕视觉角对应的本地坐标点（圆心在原点）。</summary>
    public static Point LocalPoint(double radius, double screenDegrees)
    {
        var direction = LocalDirection(screenDegrees);
        return new Point(radius * direction.X, radius * direction.Y);
    }

    /// <summary>
    /// 把角度吸到 <see cref="AngleSnapStepDegrees"/> 的整数倍（容差 <see cref="AngleSnapToleranceDegrees"/>）。
    /// </summary>
    /// <remarks>
    /// 没吸上必须交回"原值"而不是"最近的整数倍"：90.1° 就该是 90.1°，
    /// 凭空跳到 90° 会让"任意夹角"这个立身之本失真。
    /// </remarks>
    public static bool TrySnapAngle(double angle, out double snapped)
    {
        double normalized = Normalize360(angle);
        double index = Math.Round(normalized / AngleSnapStepDegrees);
        double delta = Math.Abs(normalized - index * AngleSnapStepDegrees);

        if (delta <= AngleSnapToleranceDegrees)
        {
            snapped = Normalize360(index * AngleSnapStepDegrees);
            return true;
        }

        snapped = normalized;
        return false;
    }

    // ---------------------------------------------------------------- 吸附

    /// <summary>
    /// 把按下点解析成针尖位置：先试端点，再试线段上的最近点，都不行就用原样。
    /// </summary>
    /// <remarks>
    /// 与量角器同一个理由：角的顶点、辅助线的起点都比"线段中间某处"更值得吸。
    /// <see cref="IToolContext.Query"/> 为 <c>null</c> 时全程自由摆放 ——
    /// 少一个自动对齐是少个便利，而"按下去什么也不出来"是故障。
    /// </remarks>
    public static Point ResolveCenter(IToolContext context, Point world, out bool snapped)
    {
        snapped = false;

        var query = context.Query;
        if (query is null) return world;

        const double tolerance = CenterSnapToleranceWorld;

        // 端点优先：最近且在容差内
        Point? bestPoint = null;
        double bestDistance = tolerance;
        foreach (var point in query.PointsNear(world, tolerance))
        {
            double distance = (point - world).Length;
            if (distance > bestDistance) continue;

            bestDistance = distance;
            bestPoint = point;
        }

        if (bestPoint is { } endpoint)
        {
            snapped = true;
            return endpoint;
        }

        // 次选线段上的最近点（距离必须夹在线段上，不能吸到延长线的空气里）
        Point? bestOnSegment = null;
        bestDistance = tolerance;
        foreach (var segment in query.SegmentsNear(world, tolerance))
        {
            var closest = EdgeSnapHelper.ClosestPointOnSegment(world, segment.From, segment.To);
            double distance = (closest - world).Length;
            if (distance > bestDistance) continue;

            bestDistance = distance;
            bestOnSegment = closest;
        }

        if (bestOnSegment is { } onSegment)
        {
            snapped = true;
            return onSegment;
        }

        return world;
    }

    // ---------------------------------------------------------------- 参数读取

    /// <summary>取对象的半径参数（缺失或损坏时退回默认值）。</summary>
    public static double RadiusOf(IGfxObjectRef obj)
        => ClampRadius(obj.GetNumber(RadiusKey, DefaultRadiusWorld));

    /// <summary>取弧的起点角（缺失时 0，恒规整到 <c>[0,360)</c>）。</summary>
    public static double StartOf(IGfxObjectRef obj)
        => Normalize360(obj.GetNumber(StartKey, 0.0));

    /// <summary>取弧的终点角（缺失时 90，恒规整到 <c>[0,360)</c>）。</summary>
    public static double EndOf(IGfxObjectRef obj)
        => Normalize360(obj.GetNumber(EndKey, 90.0));

    // ---------------------------------------------------------------- 几何

    /// <summary>整圆几何（本地坐标，圆心在原点）。</summary>
    public static Geometry BuildCircleGeometry(double radius)
    {
        var geometry = new EllipseGeometry(new Point(0, 0), radius, radius);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 圆弧几何（本地坐标，圆心在原点）；起止角相等时退化成整圆。
    /// </summary>
    /// <remarks>
    /// <see cref="SweepDirection.Counterclockwise"/> 与角度约定互相咬合：
    /// 起点 (cos σ, −sin σ)·R，逆时针扫过 sweep —— 正是老师屏幕上的视觉逆时针。
    /// 两个等半径端点间有两条弧（优弧 / 劣弧），<c>isLargeArc = sweep &gt; 180</c> 二选一。
    /// </remarks>
    public static Geometry BuildArcGeometry(double radius, double start, double end)
    {
        if (AnglesEqual(start, end)) return BuildCircleGeometry(radius);

        double sweep = Sweep(start, end);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(LocalPoint(radius, start), isFilled: false, isClosed: false);
            context.ArcTo(LocalPoint(radius, end), new Size(radius, radius), 0.0,
                          isLargeArc: sweep > 180.0,
                          sweepDirection: SweepDirection.Counterclockwise,
                          isStroked: true,
                          isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>圆心十字标记（本地坐标，原点 = 圆心）：四条 45° 短臂。</summary>
    public static Geometry BuildCenterMark(double radius)
    {
        double half = radius * CenterMarkRatio * 0.7071067811865476;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (double signX in new[] { -1.0, 1.0 })
            {
                foreach (double signY in new[] { -1.0, 1.0 })
                {
                    context.BeginFigure(new Point(0, 0), isFilled: false, isClosed: false);
                    context.LineTo(new Point(half * signX, half * signY), isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>圆心 → 弧起点 / 终点的两条半径指引线（本地坐标）。</summary>
    public static Geometry BuildArcGuides(double radius, double start, double end)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (double angle in new[] { start, end })
            {
                context.BeginFigure(new Point(0, 0), isFilled: false, isClosed: false);
                context.LineTo(LocalPoint(radius, angle), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 角度数字的几何（本地坐标）：摆在弧中分角外侧、正立。
    /// </summary>
    /// <remarks>起止角相等时显示 360°、摆在正上方。字号随半径等比 —— 属于"图形的一部分"而非固定像素。</remarks>
    public static Geometry BuildAngleLabel(double radius, double start, double end)
    {
        bool fullCircle = AnglesEqual(start, end);
        double sweep = fullCircle ? 360.0 : Sweep(start, end);
        double anchorAngle = fullCircle ? 90.0 : Normalize360(start + sweep / 2.0);

        double emSize = radius * LabelFontRatio;
        var typeface = new Typeface(new FontFamily("Microsoft YaHei"),
                                    FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText($"{sweep:F0}°", CultureInfo.InvariantCulture,
                                          FlowDirection.LeftToRight, typeface, emSize,
                                          Brushes.Black, pixelsPerDip: 1.0);

        var geometry = formatted.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        var anchor = LocalPoint(radius * LabelRadiusRatio, anchorAngle);

        // 先设 Transform 再 Freeze（冻结后写 Transform 会抛异常 —— 项目级铁律）
        geometry.Transform = new TranslateTransform(
            anchor.X - (bounds.X + bounds.Width / 2.0),
            anchor.Y - (bounds.Y + bounds.Height / 2.0));
        geometry.Freeze();
        return geometry;
    }

    // ---------------------------------------------------------------- 状态栏文案

    /// <summary>放置阶段（第一点已放、对象未落）的一句话。</summary>
    public static string DescribePlacing(CompassMode mode, double radius, bool snapped)
    {
        string size = ToCentimeters(radius).ToString("F1");
        string head = mode == CompassMode.Circle
            ? $"圆规：半径 {size} cm，再点一下落成圆"
            : $"圆弧：半径 {size} cm，再点一下落成 90° 圆弧";

        return snapped ? head + "（针尖已吸附卷面上的点或线）" : head;
    }

    /// <summary>落成后编辑阶段的一句话。</summary>
    public static string DescribeEditing(CompassMode mode, double radius, double start, double end)
    {
        string size = ToCentimeters(radius).ToString("F1");

        if (mode == CompassMode.Circle)
        {
            string diameter = ToCentimeters(radius * 2.0).ToString("F1");
            return $"圆规：半径 {size} cm（直径 {diameter} cm），拖针尖移圆、拖笔尖调半径，Esc 退出";
        }

        double sweep = Sweep(start, end);
        string degrees = sweep < EqualAngleEpsilonDegrees ? "360" : sweep.ToString("F0");
        return $"圆弧：半径 {size} cm，{Normalize360(start):F0}° → {Normalize360(end):F0}°（{degrees}°），拖控制点微调，Esc 退出";
    }
}
