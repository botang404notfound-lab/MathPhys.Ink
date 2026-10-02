using System.Windows;

namespace MathPhys.Ink.Plugin.Ruler;

/// <summary>
/// 直尺的<b>全部数学</b>：角度吸附、长度换算、刻度端点。
/// </summary>
/// <remarks>
/// 刻意抽成一个不依赖任何 WPF 控件、不依赖 <c>IToolContext</c> 的静态类 ——
/// 它只吃坐标、只吐坐标，于是验收 harness 能把这些边界条件一条条钉死：
/// 跨 0° 环绕、容差边界、单位换算、零长度。
/// <b>算错角度这种事肉眼看不出来</b>（差了 15° 的线也是线），只能靠断言。
/// </remarks>
public static class RulerGeometry
{
    // ---------------------------------------------------------------- 常量

    /// <summary>1 inch = 72 PDF point（世界坐标的定义，见 docs/01）。</summary>
    public const double PointsPerInch = 72.0;

    /// <summary>1 inch = 2.54 cm。</summary>
    public const double CentimetersPerInch = 2.54;

    /// <summary>吸附粒度：每 15° 一根"刻度线"。</summary>
    public const double SnapStepDegrees = 15.0;

    /// <summary>
    /// 吸附容差：只有"离最近的 15° 倍数 ≤ 5°"才吸。
    /// </summary>
    /// <remarks>
    /// <b>为什么是 5 而不是方案里写的 8</b>：起步 7.5°，容差一旦 ≥ 7.5°，
    /// <b>任意</b>角度都落在某个 15° 倍数的容差内 —— 那就不再是"吸附"，
    /// 而是"强制量化到 15°"，老师连一条任意方向的辅助线都画不出来了。
    /// 5° 的含义是：只管掉手抖，管不到"我就是想画 37°"。
    /// </remarks>
    public const double SnapToleranceDegrees = 5.0;

    /// <summary>
    /// 小于此距离的按下-抬起视为误触，不落墨（世界单位）。
    /// </summary>
    /// <remarks>
    /// 2 world ≈ 0.07 cm。老师想切工具时习惯在卷面上点一下，
    /// 没有这一条，卷面就会被一个个小点慢慢铺满。
    /// </remarks>
    public const double MinDragWorld = 2.0;

    /// <summary>两端短横的总长度（世界单位）—— 一端各伸一半。</summary>
    public const double TickLengthWorld = 8.0;

    /// <summary>角度显示的小数位；长度显示的小数位。</summary>
    public const int AngleDecimals = 0;

    // ---------------------------------------------------------------- 换算

    /// <summary>世界坐标长度 → 厘米。</summary>
    public static double ToCentimeters(double worldLength)
        => worldLength * CentimetersPerInch / PointsPerInch;

    /// <summary>把角度规整到 [0, 180) —— 直线没有方向，0° 与 180° 是同一条线。</summary>
    public static double NormalizeAxis(double degrees)
    {
        var value = degrees % 180.0;
        if (value < 0) value += 180.0;

        // -0.0 会一路传到界面上显示成 "-0°"
        return value == 0.0 ? 0.0 : value;
    }

    /// <summary>
    /// 两点连线的<b>轴角</b>，结果在 [0, 180)。
    /// </summary>
    public static double AxisDegrees(Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;

        if (dx == 0 && dy == 0) return 0.0;

        return NormalizeAxis(Math.Atan2(dy, dx) * 180.0 / Math.PI);
    }

    /// <summary>
    /// 吸附角度：离最近的 15° 倍数 ≤ 容差才吸，否则原样返回。
    /// </summary>
    /// <remarks>
    /// 跨 0° 环绕在这里被处理掉（<c>178°</c> 离 <c>180°</c> 只有 2°，要吸成 0°）——
    /// 这是这类计算最经典的错点：写成"取模再比较"就一定漏掉边界那一格。
    /// </remarks>
    public static double SnapAxisDegrees(
        double degrees,
        double step = SnapStepDegrees,
        double tolerance = SnapToleranceDegrees)
    {
        var axis = NormalizeAxis(degrees);
        var nearest = Math.Round(axis / step) * step;

        return Math.Abs(nearest - axis) <= tolerance
            ? NormalizeAxis(nearest)
            : axis;
    }

    /// <summary>
    /// 把"手指拖到的点"约束成直尺的落点：<b>起点永远不动</b>，终点只沿吸附后的方向摆正。
    /// </summary>
    /// <param name="start">起点（按下点，绝不挪动）。</param>
    /// <param name="rawEnd">手指当前所在的点。</param>
    /// <param name="snap">是否吸附（自由角直尺传 false）。</param>
    /// <param name="snapped">本次是否真的发生了吸附。</param>
    /// <remarks>
    /// <b>保留长度、只转角度</b>：终点在"以起点为心、半径 = 手指距离"的圆上滑到 15° 方向上。
    /// 这样吸附生效/失效的瞬间只有旋转、没有长度跳动，手感最稳。
    /// <para>
    /// 方向符号必须单独定：轴角是"直线"的角度，178° 与 0° 是<b>同一个轴</b>，
    /// 但把人往左拖的 178° 直接按 0° 重建会翻到右边去（线跑到手指另一边）。
    /// 所以重建时挑与原始向量同向的那个解。
    /// </para>
    /// </remarks>
    public static Point ConstrainEnd(Point start, Point rawEnd, bool snap, out bool snapped)
    {
        var dx = rawEnd.X - start.X;
        var dy = rawEnd.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);

        snapped = false;
        if (length <= double.Epsilon) return rawEnd;

        var rawAxis = AxisDegrees(start, rawEnd);
        var axis = snap ? SnapAxisDegrees(rawAxis) : rawAxis;

        snapped = snap && Math.Abs(axis - rawAxis) > 1e-9;

        var radians = axis * Math.PI / 180.0;
        var ux = Math.Cos(radians) * length;
        var uy = Math.Sin(radians) * length;

        // 轴角带符号歧义：换算出来的向量若与手指方向相反，就取反向解
        if (ux * dx + uy * dy < 0)
        {
            ux = -ux;
            uy = -uy;
        }

        return new Point(start.X + ux, start.Y + uy);
    }

    /// <summary>
    /// 求某处的短横（刻度）两端点：过 <paramref name="center"/>、垂直于轴线。
    /// </summary>
    /// <param name="center">短横的中点（线段的端点）。</param>
    /// <param name="axisDegrees">轴线角度（度）。</param>
    /// <param name="lengthWorld">短横总长度（世界单位）。</param>
    public static (Point From, Point To) TickAt(Point center, double axisDegrees, double lengthWorld)
    {
        var half = lengthWorld / 2.0;
        var radians = (axisDegrees + 90.0) * Math.PI / 180.0;
        var ux = Math.Cos(radians) * half;
        var uy = Math.Sin(radians) * half;

        return (new Point(center.X - ux, center.Y - uy),
                new Point(center.X + ux, center.Y + uy));
    }

    /// <summary>
    /// 给状态栏用的一句话：「直线 4.2 cm，倾角 30°」。
    /// </summary>
    public static string Describe(Point from, Point to, bool snapped)
    {
        var length = ToCentimeters((to - from).Length);
        var angle = AxisDegrees(from, to);

        return $"直线 {length.ToString("F1")} cm，倾角 {angle.ToString("F" + AngleDecimals)}°"
             + (snapped ? "（已吸附）" : string.Empty);
    }
}
