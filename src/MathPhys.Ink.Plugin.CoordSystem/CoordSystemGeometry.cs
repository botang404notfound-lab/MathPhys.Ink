using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CoordSystem;

/// <summary>
/// 坐标系的纯数学：世界↔数学换算、网格/轴/箭头/标签的几何组装、范围档位。
/// </summary>
/// <remarks>
/// 一行绘制都没有 —— 全部 <see cref="StreamGeometry"/> + <see cref="FormattedText"/> 几何形状，
/// 渲染器在 <c>OnRender</c> 里只画不画。
/// </remarks>
public static class CoordSystemGeometry
{
    // ---------------------------------------------------------------- Keys

    /// <summary>单位长度（数学 1 = 世界 unitWorld）。</summary>
    public const string UnitWorldKey = "unitWorld";

    /// <summary>x 范围下界。</summary>
    public const string XMinKey = "xMin";

    /// <summary>x 范围上界。</summary>
    public const string XMaxKey = "xMax";

    /// <summary>y 范围下界。</summary>
    public const string YMinKey = "yMin";

    /// <summary>y 范围上界。</summary>
    public const string YMaxKey = "yMax";

    /// <summary>网格步长（数学单位）。</summary>
    public const string StepKey = "step";

    /// <summary>是否显示网格（1=是）。</summary>
    public const string ShowGridKey = "showGrid";

    /// <summary>是否显示刻度标签（1=是）。</summary>
    public const string ShowLabelsKey = "showLabels";

    /// <summary>是否锁定纵横比（1=是）。</summary>
    public const string LockAspectKey = "lockAspect";

    // ---------------------------------------------------------------- Defaults

    public const double DefaultUnitWorld = 28.35;   // 1 cm 世界长
    public const double MinUnitWorld = 8.0;         // ≈ 0.28 cm，再小看不见网格
    public const double MaxUnitWorld = 200.0;       // ≈ 7.06 cm，再大铺满整页
    public const double MinDragWorld = 2.0;

    /// <summary>默认范围 [-10, 10] 步长 1。</summary>
    public static readonly (double Min, double Max, double Step) DefaultRange = (-10.0, 10.0, 1.0);

    /// <summary>出厂档位（用户可调范围时的候选）。固定列表，不是连续区间。</summary>
    public static readonly IReadOnlyList<(double Min, double Max, double Step)> RangePresets = new[]
    {
        (-10.0, 10.0, 1.0),
        (-5.0, 5.0, 0.5),
        (0.0, 2 * Math.PI, Math.PI / 2.0),
        (-4.0, 4.0, 0.5),
    };

    // ---------------------------------------------------------------- 夹紧

    public static double ClampUnit(double u)
    {
        if (double.IsNaN(u) || double.IsInfinity(u) || u <= 0) return DefaultUnitWorld;
        return Math.Clamp(u, MinUnitWorld, MaxUnitWorld);
    }

    public static double ClampRangeBound(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0.0;
        return Math.Clamp(v, -1000.0, 1000.0);
    }

    /// <summary>世界坐标转数学坐标：世界 → 数学。</summary>
    public static (double X, double Y) WorldToMath(Point origin, double unitWorld, Point world)
    {
        double dx = world.X - origin.X;
        double dy = origin.Y - world.Y;            // y 取负：世界 y 向下、数学 y 向上
        return (dx / unitWorld, dy / unitWorld);
    }

    /// <summary>数学坐标转世界坐标。</summary>
    public static Point MathToWorld(Point origin, double unitWorld, double mathX, double mathY)
    {
        return new Point(origin.X + mathX * unitWorld, origin.Y - mathY * unitWorld);
    }

    /// <summary>本地坐标（数学原点已经在 (0,0)）→ 世界坐标。</summary>
    public static Point LocalToWorld(Point origin, double unitWorld, double localX, double localY)
        => MathToWorld(origin, unitWorld, localX, localY);

    /// <summary>本地坐标的 y 取反：方便渲染器"以数学原点为中心"地画。</summary>
    public static double FlipY(double localY) => -localY;

    /// <summary>世界单位转厘米（A4 宽 595 world ≈ 20.99 cm 自检）。</summary>
    public static double ToCentimeters(double world)
        => world * 2.54 / 72.0;

    // ---------------------------------------------------------------- 原点吸附（页中心 / 页边距线）

    /// <summary>
    /// 在 <paramref name="page"/> 周围找出最"值得吸"的候选点：页中心、四条页边距线、八个交点。
    /// </summary>
    /// <remarks>
    /// 不读 Query：坐标系要吸的是<b>页框</b>，不是卷面已有线段 —— 走 <see cref="IToolContext.PageRects"/>。
    /// </remarks>
    /// <summary>
    /// 给出页中心 + 4 条页边**在 <paramref name="world"/> 的 x/y 处**的最近点（9 个候选）。
    /// </summary>
    /// <remarks>
    /// 不是"页角 + 边中点"那 9 个固定点：老师画坐标系时，笔尖按在卷面哪儿，期望原点
    /// 就能吸到"那一列的页边"——而不是非要按在页角或页边正中才吸得上。
    /// </remarks>
    public static IReadOnlyList<Point> PageSnapCandidates(Rect page, Point world)
    {
        double midX = page.X + page.Width / 2.0;
        double midY = page.Y + page.Height / 2.0;

        return new[]
        {
            // 页中心（最常被吸）
            new Point(midX, midY),

            // 四条页边在 world.x / world.y 处的最近点（关键 —— "那一列的那条边"）
            new Point(world.X, page.Y),         // 上边，按下点那列
            new Point(world.X, page.Bottom),    // 下边
            new Point(page.X, world.Y),         // 左边，按下点那行
            new Point(page.Right, world.Y),     // 右边

            // 四条边的中点（传统"页边正中"位置）
            new Point(midX, page.Y),
            new Point(page.Right, midY),
            new Point(midX, page.Bottom),
            new Point(page.X, midY),
        };
    }

    public const double PageSnapToleranceWorld = 28.0;   // ≈ 0.99 cm

    /// <summary>
    /// 从候选点中挑出离 <paramref name="world"/> 最近、且在容差内的那一个；都没有就返回 <c>world</c>。
    /// </summary>
    public static Point SnapOriginToPage(Rect page, Point world, double toleranceWorld)
    {
        double bestSqr = double.MaxValue;
        Point best = world;
        foreach (var c in PageSnapCandidates(page, world))
        {
            double d2 = (c - world).LengthSquared;
            if (d2 < bestSqr)
            {
                bestSqr = d2;
                best = c;
            }
        }
        return bestSqr <= toleranceWorld * toleranceWorld ? best : world;
    }

    // ---------------------------------------------------------------- 网格

    /// <summary>
    /// 网格线几何：竖线 = (xMin, xMin+step, ..., xMax) × unitWorld，横线同理。
    /// </summary>
    /// <remarks>
    /// 这里画的<b>是世界长度</b>（不是数学坐标）：宿主期望"内容以本地原点为中心画"，
    /// 且 <see cref="CoordSystemRenderer.Measure"/> 返回的是 2W × 2H（W/H = |max| × unitWorld），
    /// 所以几何范围必须铺满 ±W、±H，否则落下来只看到原点附近一团。
    /// </remarks>
    public static Geometry BuildGrid(
        double xMin, double xMax, double yMin, double yMax, double step, double unitWorld)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            for (double x = xMin; x <= xMax + 1e-9; x += step)
            {
                double wx = x * unitWorld;
                Point a = new(wx, FlipY(yMin) * unitWorld);
                Point b = new(wx, FlipY(yMax) * unitWorld);
                ctx.BeginFigure(a, isFilled: false, isClosed: false);
                ctx.LineTo(b, isStroked: true, isSmoothJoin: false);
            }
            for (double y = yMin; y <= yMax + 1e-9; y += step)
            {
                double wy = y * unitWorld;
                Point a = new(xMin * unitWorld, FlipY(wy));
                Point b = new(xMax * unitWorld, FlipY(wy));
                ctx.BeginFigure(a, isFilled: false, isClosed: false);
                ctx.LineTo(b, isStroked: true, isSmoothJoin: false);
            }
        }
        sg.Freeze();
        return sg;
    }

    /// <summary>计算网格线总数（竖 + 横）。</summary>
    public static int GridLineCount(double min, double max, double step)
    {
        if (step <= 0) return 0;
        int n = (int)Math.Round((max - min) / step) + 1;
        return n < 0 ? 0 : n;
    }

    // ---------------------------------------------------------------- 轴 + 箭头

    /// <summary>x 轴 + y 轴（贯穿整个范围，世界长度）。</summary>
    public static Geometry BuildAxes(
        double xMin, double xMax, double yMin, double yMax, double unitWorld)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            // x 轴（y = 0 这条线）
            ctx.BeginFigure(new Point(xMin * unitWorld, 0), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(xMax * unitWorld, 0), isStroked: true, isSmoothJoin: false);

            // y 轴（x = 0 这条线）
            ctx.BeginFigure(new Point(0, FlipY(yMin) * unitWorld), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(0, FlipY(yMax) * unitWorld), isStroked: true, isSmoothJoin: false);
        }
        sg.Freeze();
        return sg;
    }

    /// <summary>
    /// 两个箭头：画在 x 轴 / y 轴的<b>正方向末端</b>（尖端指向轴外），而不是原点。
    /// </summary>
    /// <remarks>
    /// 旧实现把箭头画在原点附近（顶点落在 (s,0)/(0,−s)），结果原点处冒出一对小箭头。
    /// 正确位置：x 轴箭头尖端在 (xMax·unit, 0)、y 轴箭头尖端在 (0, FlipY(yMax)·unit)。
    /// </remarks>
    public static Geometry BuildAxisArrows(double xMax, double yMax, double unitWorld, double arrowSizeLocal)
    {
        double s = arrowSizeLocal;
        double xTip = xMax * unitWorld;            // x 轴正末端（世界长）
        double yTip = FlipY(yMax) * unitWorld;     // y 轴正末端（局部几何 y，数学 +y ⇒ −unit）
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            // X 轴箭头：尖端在 +x 末端，指向 +x，底边略向内（xTip − 1.4s）
            ctx.BeginFigure(new Point(xTip, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(xTip - s * 1.4, s * 0.7), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(xTip - s * 1.4, -s * 0.7), isStroked: true, isSmoothJoin: false);

            // Y 轴箭头：尖端在 +y 末端，指向 +y（局部 −y），底边略向内（yTip + 1.4s）
            ctx.BeginFigure(new Point(0, yTip), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(s * 0.7, yTip + s * 1.4), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(-s * 0.7, yTip + s * 1.4), isStroked: true, isSmoothJoin: false);
        }
        sg.Freeze();
        return sg;
    }

    // ---------------------------------------------------------------- 刻度标签

    /// <summary>
    /// 刻度数字 <see cref="Geometry"/>。x 轴数字放在 (x, FlipY(0)) 下方（本地 +y 方向 = 数学 −y），
    /// y 轴数字放在 (0, FlipY(y)) 左侧（本地 −x）。
    /// </summary>
    public static GeometryGroup BuildLabels(
        double xMin, double xMax, double yMin, double yMax, double step,
        double unitWorld, double fontRatio)
    {
        // 字号 = 0.25 × unitWorld（世界长），按 unitWorld 缩放时数字始终可读
        double fontWorld = unitWorld * fontRatio;
        var typeface = new Typeface(
            new FontFamily("Microsoft YaHei, Microsoft YaHei UI, Segoe UI"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        var dg = new GeometryGroup();
        for (double x = xMin; x <= xMax + 1e-9; x += step)
        {
            if (Math.Abs(x) < 1e-12) continue;          // 跳过原点
            // X 轴下方：本地 (x×unitWorld, +offset×unitWorld) —— 与网格/轴的世界长度一致
            dg.Children.Add(MakeText(FormatNumber(x), fontWorld, typeface, TextAlignment.Left,
                                     new TranslateTransform(x * unitWorld, fontWorld * 0.45)));
        }
        for (double y = yMin; y <= yMax + 1e-9; y += step)
        {
            if (Math.Abs(y) < 1e-12) continue;
            // Y 轴左侧：本地 (−offset×unitWorld, FlipY(y×unitWorld))
            dg.Children.Add(MakeText(FormatNumber(y), fontWorld, typeface, TextAlignment.Right,
                                     new TranslateTransform(-fontWorld * 0.55, FlipY(y) * unitWorld - fontWorld * 0.3)));
        }
        dg.Freeze();
        return dg;
    }

    private static Geometry MakeText(string text, double fontWorld, Typeface typeface, TextAlignment align, Transform transform)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontWorld,
            Brushes.Black,
            1.0);                                   // pixelsPerDip = 1：几何已经按世界长画了，不要再加 DIP 缩放

        ft.TextAlignment = align;
        ft.MaxLineCount = 1;
        ft.MaxTextWidth = fontWorld * 4;

        var geo = ft.BuildGeometry(new Point(0, 0));
        // Transform 必须先设再 Freeze —— frozen 的 Geometry 上写属性会抛 InvalidOperationException
        geo.Transform = transform;
        geo.Freeze();
        return geo;
    }

    /// <summary>刻度数字的紧凑显示：整数不带 ".0"，小数最多 3 位。</summary>
    public static string FormatNumber(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- 状态栏

    public static string Describe(double unitWorld, double xMin, double xMax, double yMin, double yMax, double step)
    {
        double cm = ToCentimeters(unitWorld);
        return $"坐标系：原点 ( — ),单位长度 {cm:F2} cm ⇒ 一格 {cm * step:F2} cm"
            + $"，范围 x∈[{xMin:F0},{xMax:F0}]、y∈[{yMin:F0},{yMax:F0}]、步长 {step:G3}";
    }
}