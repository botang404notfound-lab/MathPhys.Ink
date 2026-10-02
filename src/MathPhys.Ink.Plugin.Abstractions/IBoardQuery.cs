using System.Collections.Generic;
using System.Windows;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 画布上的一段直线 —— 工具的<b>吸附基准</b>。
/// </summary>
/// <remarks>
/// 世界坐标（PDF point）。它可能是手写笔迹里的一小段，也可能是直尺工具画出来的辅助线 ——
/// 对吸附来说两者没有区别：老师在卷面上看到的都是一条线。
/// </remarks>
public readonly record struct BoardSegment(Point From, Point To)
{
    /// <summary>线段长度（世界单位）。</summary>
    public double Length => (To - From).Length;

    /// <summary>线段方向角，结果在 <c>[0, 180)</c>（直线没有正反方向）。</summary>
    public double AxisDegrees
    {
        get
        {
            double degrees = Math.Atan2(To.Y - From.Y, To.X - From.X) * 180.0 / Math.PI;
            degrees %= 180.0;
            if (degrees < 0) degrees += 180.0;
            return degrees == 0.0 ? 0.0 : degrees;
        }
    }
}

/// <summary>
/// 工具"看得见画布上已有的东西"的只读窗口 —— 吸附基准的唯一来源。
/// </summary>
/// <remarks>
/// 量角器要把基线对齐到卷面上已有的一条线、三角板要贴边、坐标系要居中、矢量箭头要吸端点，
/// 都需要这个能力。之所以单独成一个接口而不是塞进 <see cref="IToolContext"/>：
/// 那些方法都得能<b>脱离渲染</b>单独验收（本机没有触摸屏，吸附判对了没有只能靠断言）。
/// <para>
/// <b>只读，而且只有两个方法。</b>这里刻意<b>没有</b> <c>Objects</c>（遍历别人的图形）、
/// 没有 <c>PageRects</c>（页框）、没有"改别人的东西"——
/// 每多一个方法就多一份对外承诺，而猜错的接口比没有接口贵（M7.4 Step 0 的教训）。
/// 真需要它们的时候（函数图像要找到坐标系、坐标系要贴页边）再加，那时语义已经清楚了。
/// </para>
/// <para>
/// <b>性能约定</b>：实现方必须只扫视口附近。传入的 <c>radiusWorld</c> 就是给它的界，
/// 调用方负责给一个合理的值（工具自己知道它关心多大范围）。
/// </para>
/// </remarks>
public interface IBoardQuery
{
    /// <summary>
    /// 找出离 <paramref name="world"/> 在 <paramref name="radiusWorld"/> 以内的已有线段。
    /// </summary>
    /// <remarks>
    /// 来源合并：笔迹层里的笔画（含直尺画的辅助线）与图形对象层里的线段。
    /// 「直尺画的线能被量角器吸附」这条链路就是<b>工具协作</b>的最小证明。
    /// <para>
    /// 返回的线段是<b>原样</b>的（可能很长，比如一条贯穿整页的直尺线）——
    /// 需要最近点的调用方自己做投影，接口不替它猜"你要哪一段"。
    /// </para>
    /// </remarks>
    IReadOnlyList<BoardSegment> SegmentsNear(Point world, double radiusWorld);

    /// <summary>
    /// 找出离 <paramref name="world"/> 在 <paramref name="radiusWorld"/> 以内的关键点（端点）。
    /// </summary>
    /// <remarks>
    /// 端点是"更值钱"的吸附目标：角的两条边交汇的那个顶点、一条辅助线的起点，
    /// 都是老师真正想把圆心放上去的地方。所以量角器<b>先试端点、再试线段最近点</b>。
    /// </remarks>
    IReadOnlyList<Point> PointsNear(Point world, double radiusWorld);

    /// <summary>
    /// 吸附到<b>图形对象</b>提供的目标（坐标系网格交点 / 坐标轴，M12）。
    /// </summary>
    /// <remarks>
    /// 与线段吸附的分工：<see cref="SegmentsNear"/> / <see cref="PointsNear"/> 来自<b>墨迹</b>，
    /// 本方法来自<b>图形对象</b>（渲染器声明自己能提供什么，见 <c>ISnapTargetProvider</c>）。
    /// 没有任何可用目标时返回 <c>null</c> —— 调用方据此走"不吸附"的老路，而不是吸到 (0,0)。
    /// </remarks>
    /// <returns>吸附后的点；没有可吸附目标（或最近的也超出半径）时返回 <c>null</c>。</returns>
    Point? SnapToGfx(Point world, double radiusWorld);
}
