using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CoordSystem;

/// <summary>
/// 空间直角坐标系（xOyZ）的纯数学：静态轴测投影 + 三轴几何组装（M23）。
/// </summary>
/// <remarks>
/// <para>
/// ★ 这是<b>静态轴测图</b>，不是可旋转的三维场景：课堂用法是「给出一副标准的
/// 空间坐标系，老师在上面标点、画向量、讲投影」。可旋转视角是几何教学的需求，
/// 不在本工具的目标里（确认于 M23 方案）。三条轴的屏幕方向按教材惯用画法：
/// </para>
/// <list type="bullet">
/// <item>z 轴：竖直向上 —— 本地坐标 y 向下，所以是 (0, −1)；</item>
/// <item>y 轴：水平向右 (1, 0)；</item>
/// <item>x 轴：指向左下 45°（−√2/2, +√2/2）—— 与 y 轴在纸面上成 135°。</item>
/// </list>
/// <para>
/// ★ 包围盒不变量：三根轴的<b>负半轴也画足 extent</b>（细虚线），于是四个方向的
/// 极值都是 ±extent×unitWorld，包围盒恰好是以数学原点为中心的 2E×2E 正方形 ——
/// 与宿主「对象的本地几何以 (0,0) 为中心」的摆位约定一致，
/// <see cref="CoordSystem3DRenderer.Measure"/> 按同式返回。
/// </para>
/// <para>
/// ★「世界长常量进按数学单位画的本地几何」要除 unitWorld 的老坑在这里同样适用：
/// 刻度长、字号、箭头尺寸全部以 <c>unitWorld</c> 的比例表达，随缩放等比变化。
/// </para>
/// </remarks>
public static class CoordSystem3DGeometry
{
    // ---------------------------------------------------------------- Keys

    /// <summary>单位长度（数学 1 = 世界 unitWorld）。与平面版同名同义。</summary>
    public const string UnitWorldKey = "unitWorld";

    /// <summary>轴的范围（对称 ±extent，数学单位）。</summary>
    public const string ExtentKey = "extent";

    /// <summary>刻度步长（数学单位）。</summary>
    public const string StepKey = "step";

    // ---------------------------------------------------------------- Defaults

    public const double DefaultUnitWorld = CoordSystemGeometry.DefaultUnitWorld;
    public const double MinUnitWorld = CoordSystemGeometry.MinUnitWorld;
    public const double MaxUnitWorld = CoordSystemGeometry.MaxUnitWorld;
    public const double DefaultExtent = 5.0;
    public const double MaxExtent = 50.0;

    /// <summary>x 轴的屏幕方向（本地坐标，y 向下）：左下 45°。</summary>
    public static readonly Vector DirX = new(-Math.Cos(Math.PI / 4.0), Math.Sin(Math.PI / 4.0));

    /// <summary>y 轴的屏幕方向：水平向右。</summary>
    public static readonly Vector DirY = new(1.0, 0.0);

    /// <summary>z 轴的屏幕方向：竖直向上（y 向下 ⇒ 取 −1）。</summary>
    public static readonly Vector DirZ = new(0.0, -1.0);

    /// <summary>轴字母与方向的对应表（顺序 = 界面讲解顺序 x → y → z）。</summary>
    public static readonly IReadOnlyList<(char Axis, Vector Dir)> Axes = new[]
    {
        ('x', DirX), ('y', DirY), ('z', DirZ),
    };

    // ---------------------------------------------------------------- 夹紧

    public static double ClampUnit(double u)
        => CoordSystemGeometry.ClampUnit(u);

    public static double ClampExtent(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return DefaultExtent;
        return Math.Clamp(v, 1.0, MaxExtent);
    }

    /// <summary>数学轴上的刻度 s（可负）→ 本地坐标点：s × unitWorld 沿轴方向。</summary>
    public static Point AxisPoint(Vector dir, double s, double unitWorld)
        => new(dir.X * s * unitWorld, dir.Y * s * unitWorld);

    // ---------------------------------------------------------------- 几何组装

    /// <summary>
    /// 三根轴的<b>正半轴</b>（实线主笔画）与<b>负半轴</b>（细虚线，另几何供虚线笔画）。
    /// </summary>
    public static (Geometry Positive, Geometry Negative) BuildAxes(double extent, double unitWorld)
    {
        double e = extent * unitWorld;
        var pos = new StreamGeometry();
        var neg = new StreamGeometry();
        using (var cp = pos.Open())
        using (var cn = neg.Open())
        {
            foreach (var (_, dir) in Axes)
            {
                cp.BeginFigure(new Point(0, 0), isFilled: false, isClosed: false);
                cp.LineTo(new Point(dir.X * e, dir.Y * e), isStroked: true, isSmoothJoin: false);

                cn.BeginFigure(new Point(0, 0), isFilled: false, isClosed: false);
                cn.LineTo(new Point(-dir.X * e, -dir.Y * e), isStroked: true, isSmoothJoin: false);
            }
        }
        pos.Freeze();
        neg.Freeze();
        return (pos, neg);
    }

    /// <summary>
    /// 三个箭头：画在正半轴末端（尖端指向轴外），实心三角 —— 与平面版同款造型。
    /// </summary>
    public static Geometry BuildAxisArrows(double extent, double unitWorld, double arrowSizeLocal)
    {
        double e = extent * unitWorld;
        double s = arrowSizeLocal;
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            foreach (var (_, dir) in Axes)
            {
                // 尖端在轴末端；底边向内收 1.4s、两侧各张开 0.7s（与 BuildAxisArrows 平面版一致）
                var tip = new Point(dir.X * e, dir.Y * e);
                var perp = new Vector(-dir.Y, dir.X);
                ctx.BeginFigure(tip, isFilled: true, isClosed: true);
                ctx.LineTo(new Point(tip.X - dir.X * s * 1.4 + perp.X * s * 0.7,
                                     tip.Y - dir.Y * s * 1.4 + perp.Y * s * 0.7),
                           isStroked: true, isSmoothJoin: false);
                ctx.LineTo(new Point(tip.X - dir.X * s * 1.4 - perp.X * s * 0.7,
                                     tip.Y - dir.Y * s * 1.4 - perp.Y * s * 0.7),
                           isStroked: true, isSmoothJoin: false);
            }
        }
        sg.Freeze();
        return sg;
    }

    /// <summary>
    /// 刻度短线：三条正半轴上，从 step 到 extent 每格一道（垂直于轴的小短线）。
    /// </summary>
    public static Geometry BuildTicks(double extent, double step, double unitWorld)
    {
        if (!(step > 0)) return Geometry.Empty;
        double tickHalf = unitWorld * 0.10;      // 刻度线半长（世界长，随单位缩放）
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            foreach (var (_, dir) in Axes)
            {
                var perp = new Vector(-dir.Y, dir.X);
                for (double s = step; s <= extent + 1e-9; s += step)
                {
                    var c = AxisPoint(dir, s, unitWorld);
                    ctx.BeginFigure(new Point(c.X - perp.X * tickHalf, c.Y - perp.Y * tickHalf),
                                    isFilled: false, isClosed: false);
                    ctx.LineTo(new Point(c.X + perp.X * tickHalf, c.Y + perp.Y * tickHalf),
                               isStroked: true, isSmoothJoin: false);
                }
            }
        }
        sg.Freeze();
        return sg;
    }

    /// <summary>
    /// 字母标签：x / y / z 放在各自箭头外侧，O 放在原点左下。
    /// </summary>
    public static Geometry BuildLabels(double extent, double unitWorld)
    {
        double fontWorld = unitWorld * 0.30;
        var typeface = new Typeface(
            new FontFamily("Microsoft YaHei, Microsoft YaHei UI, Segoe UI"),
            FontStyles.Italic,
            FontWeights.Normal,
            FontStretches.Normal);

        var group = new GeometryGroup();

        // 三个轴字母：沿轴方向再伸出去 0.55 个字号的距离
        foreach (var (axis, dir) in Axes)
        {
            double e = extent * unitWorld;
            var at = new Point(dir.X * (e + fontWorld * 0.9), dir.Y * (e + fontWorld * 0.9));
            group.Children.Add(MakeText(axis.ToString(), fontWorld, typeface, at));
        }

        // 原点 O：左下角（本地 +x −、+y + 的方向），避让三根轴
        group.Children.Add(MakeText("O", fontWorld, typeface,
            new Point(-fontWorld * 0.9, fontWorld * 0.9)));

        group.Freeze();
        return group;
    }

    private static Geometry MakeText(string text, double fontWorld, Typeface typeface, Point at)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontWorld,
            Brushes.Black,
            1.0);   // pixelsPerDip = 1：几何按世界长画，不再加 DIP 缩放

        ft.TextAlignment = TextAlignment.Center;
        ft.MaxLineCount = 1;

        var geo = ft.BuildGeometry(new Point(0, 0));
        // Transform 必须先设再 Freeze —— frozen 的 Geometry 上写属性会抛异常
        geo.Transform = new TranslateTransform(at.X, at.Y);
        geo.Freeze();
        return geo;
    }

    // ---------------------------------------------------------------- 状态栏

    public static string Describe(double unitWorld, double extent, double step)
    {
        double cm = CoordSystemGeometry.ToCentimeters(unitWorld);
        return $"空间坐标系：单位长度 {cm:F2} cm ⇒ 一格 {cm * step:F2} cm，"
            + $"三轴范围 ±{extent:G3}、步长 {step:G3}";
    }
}
