using System;
using System.Windows;
using System.Windows.Media;

namespace MathPhys.Ink.Plugin.VectorArrow;

/// <summary>
/// 矢量箭头的<b>几何组装</b>：箭杆（按箭头长缩短）+ 箭头三角。
/// </summary>
/// <remarks>
/// 一行绘制都没有 —— 全部是 <see cref="StreamGeometry"/> 与坐标计算，
/// 渲染器在 <c>OnRender</c> 里"只画不算"。这样"箭头尖有没有精确落在终点"这种几何细节
/// 才能在无窗口的 harness 里断言（<b>肉眼看不出来</b>：差 0.5 世界点的尖端也是尖端）。
/// </remarks>
public static class ArrowGeometry
{
    // ---------------------------------------------------------------- 常量（本地单位 = 世界长）

    /// <summary>
    /// 箭头三角的<b>长度</b>（本地单位 = 世界长）。
    /// </summary>
    /// <remarks>
    /// 箭杆要从终点往回缩这么多，箭头尖才正好落在终点上。
    /// 定值而非"按箭头长度比例"：老师要的是"不管多短的箭头，箭头尖都醒目"，
    /// 长度成比例会让短箭头的箭头小到看不见。
    /// </remarks>
    public const double HeadLengthWorld = 11.0;

    /// <summary>
    /// 箭头三角的半宽（本地单位 = 世界长）。
    /// </summary>
    /// <remarks>
    /// 同时决定 <see cref="Measure"/> 的包围盒高度 —— 箭头"胖"多少，命中区域就该有多宽
    /// （高度取 0 会让命中测试退化成一条线）。
    /// </remarks>
    public const double HeadHalfWidthWorld = 5.5;

    /// <summary>
    /// 绘制时箭杆最少要留的长度。
    /// </summary>
    /// <remarks>
    /// 极短的箭头（长度 < 箭头长）若照搬"箭杆 = 长度 − 箭头长"，会算出负长度 ——
    /// 表现成箭杆倒着画出来一段。这里留一个下限，宁可让短箭头的箭头略微超出，
    /// 也不能出现负长度的诡异图形。
    /// </remarks>
    public const double MinShaftWorld = 0.1;

    // ---------------------------------------------------------------- 箭杆

    /// <summary>
    /// 箭杆的<b>本地</b>几何：从 <c>(-L/2, 0)</c> 到 <c>(L/2 - headLen, 0)</c>。
    /// </summary>
    /// <param name="lengthWorld">箭头总长（世界长，已含单位换算）。</param>
    /// <param name="headLengthWorld">箭头三角长度（本地单位）。</param>
    /// <remarks>
    /// <b>本地原点是首尾中点</b>（对象层约定：本地范围 [-W/2, W/2] × [-H/2, H/2]），
    /// 所以尾部在 -L/2、尖端在 +L/2。箭杆画到 <c>L/2 - headLen</c> 为止 ——
    /// 剩下的那一段正好被三角头盖住，尖端即终点。
    /// </remarks>
    public static Point ShaftFrom(double lengthWorld) => new(-lengthWorld / 2.0, 0);

    /// <summary>箭杆的终点（本地坐标）—— 从尖端往回收一个箭头长。</summary>
    public static Point ShaftTo(double lengthWorld, double headLengthWorld = HeadLengthWorld)
    {
        double shaft = lengthWorld - headLengthWorld;
        if (shaft < MinShaftWorld) shaft = MinShaftWorld;
        return new Point(-lengthWorld / 2.0 + shaft, 0);
    }

    /// <summary>箭头尖端的本地坐标（= 本地 +x 端点）。</summary>
    public static Point TipLocal(double lengthWorld) => new(lengthWorld / 2.0, 0);

    /// <summary>
    /// 方向角对应的"几何旋转"变换：把水平的箭杆/箭头转成该方向。
    /// </summary>
    /// <remarks>
    /// ★ 为什么需要它：箭头的 <c>Center</c> 是**首尾中点**，几何是按"水平向右"画的。
    /// 若不旋转几何而只把方向角写进文字，画出来的箭头永远指向右边（实测过 —— 证据图里
    /// 明明写着 37°，箭头却是水平的）。这不是"位姿"（位姿由宿主矩阵管：Center/Rotation/Scale），
    /// 而是**对象自身形状的一部分**，所以用 <see cref="Geometry.Transform"/> 而不是 RenderTransform。
    /// <para>
    /// 角度取负：<see cref="VectorMath.DirectionDegrees"/> 是"逆时针为正"（物理读法），
    /// 而世界坐标 y 向下 ⇒ 几何要顺时针转同样的角度。
    /// </para>
    /// </remarks>
    public static Transform Rotation(double directionDegrees)
    {
        var t = new RotateTransform(-directionDegrees);
        t.Freeze();
        return t;
    }

    /// <summary>箭杆几何（一条带圆头的直线段），已按方向角旋转。</summary>
    public static Geometry BuildShaft(
        double lengthWorld, double directionDegrees, double headLengthWorld = HeadLengthWorld)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            ctx.BeginFigure(ShaftFrom(lengthWorld), isFilled: false, isClosed: false);
            ctx.LineTo(ShaftTo(lengthWorld, headLengthWorld), isStroked: true, isSmoothJoin: false);
        }

        // 先设 Transform 再 Freeze —— frozen 的 Geometry 上写属性会抛 InvalidOperationException
        sg.Transform = Rotation(directionDegrees);
        sg.Freeze();
        return sg;
    }

    /// <summary>
    /// 箭头三角（实心），尖端在 <c>(L/2, 0)</c>。
    /// </summary>
    /// <remarks>
    /// 底边两角在 <c>(L/2 - headLen, ±halfWidth)</c>。<b>尖端必须精确等于 <see cref="TipLocal"/></b> ——
    /// harness 靠这一条钉死"箭头尖落在终点"。
    /// </remarks>
    public static Geometry BuildHead(
        double lengthWorld,
        double directionDegrees,
        double headLengthWorld = HeadLengthWorld,
        double headHalfWidthWorld = HeadHalfWidthWorld)
    {
        double tipX = lengthWorld / 2.0;
        double baseX = tipX - headLengthWorld;

        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            ctx.BeginFigure(new Point(tipX, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(baseX, headHalfWidthWorld), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(baseX, -headHalfWidthWorld), isStroked: true, isSmoothJoin: false);
        }

        // 与箭杆同一次旋转（尖端转到方向角上）—— 同样先设 Transform 再 Freeze
        sg.Transform = Rotation(directionDegrees);
        sg.Freeze();
        return sg;
    }

    // ---------------------------------------------------------------- 测量

    /// <summary>
    /// 本地包围盒：以箭头<b>中点</b>为中心，宽 = 箭头长（<b>本地单位</b>）、
    /// 高 = 箭头三角全宽（<b>本地单位</b>）。
    /// </summary>
    /// <remarks>
    /// 高度取"箭头三角全宽"而不是 0：包围盒有一条高度为 0 的矩形会让命中测试退化成一条线
    /// （<see cref="GfxTransform.HitTest"/> 用 halfHeight=0 时只有正中央能点中），
    /// 表现成"这个箭头怎么点都抓不住"。
    /// <para>
    /// ★ <b><paramref name="unitWorld"/> 不能省</b>：入参 <paramref name="lengthLocal"/> 是
    /// <b>本地（数学）单位</b>，而 <see cref="HeadHalfWidthWorld"/> 是<b>世界长</b>常量。
    /// 不换算就等于把"世界高"当成"数学高"，绑定坐标系（unitWorld 约 28.35）时包围盒高度被抬高
    /// 28 倍 —— 表现成"箭头周围一大片看不见的命中区，点旁边的空白也会选中它"。
    /// 这个 bug 与"箭头三角被放大 28 倍"是<b>同一个根因</b>（见 <c>ArrowRenderer</c> 的说明）。
    /// </para>
    /// <para>
    /// ★ 箭头旁不画读数文字（2026-09-22 用户要求）⇒ 这个包围盒同时也是<b>越界绘制</b>的边界：
    /// 宽度以"首尾中点"为中心、上下各留一个箭头半宽。
    /// </para>
    /// </remarks>
    public static Size Measure(double lengthLocal, double unitWorld = 1.0)
    {
        double safeUnit = unitWorld > 0 && !double.IsNaN(unitWorld) && !double.IsInfinity(unitWorld)
            ? unitWorld : 1.0;

        // 箭头三角半宽：世界常量 ÷ unitWorld ⇒ 本地（数学）单位
        double headHalfLocal = HeadHalfWidthWorld / safeUnit;

        // 下限同样按本地单位取（1 世界点 = 1/unitWorld 本地单位），免得缩放后矩形退化成一条线
        double minLocal = 1.0 / safeUnit;
        double height = Math.Max(headHalfLocal * 2.0, minLocal);
        return new Size(Math.Max(lengthLocal, minLocal), height);
    }

    // ---------------------------------------------------------------- 长度下限

    /// <summary>把 <paramref name="lengthWorld"/> 夹到"至少能画出来"的下限。</summary>
    public static double ClampLength(double lengthWorld)
    {
        if (double.IsNaN(lengthWorld) || double.IsInfinity(lengthWorld)) return MinDragLength;
        return Math.Max(lengthWorld, MinDragLength);
    }

    /// <summary>
    /// 渲染器要求的最小可画长度（世界长）。
    /// </summary>
    /// <remarks>
    /// 公开是因为合力会用到它：几根分矢量相互抵消时合矢量是零，
    /// 此时仍要画一根"最短但仍点得住"的箭头（否则对象退化成一条线、
    /// 选中框与命中区都没了，用户连删都删不掉）。
    /// </remarks>
    public const double MinDragLength = 1.0;
}
