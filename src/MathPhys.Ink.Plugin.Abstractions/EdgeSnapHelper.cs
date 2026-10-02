using System;
using System.Collections.Generic;
using System.Windows;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 「沿边画线」的公共几何：边检测（点到线段距离）、直线投影（t 不夹）、顶点吸附、三角形内判定。
/// </summary>
/// <remarks>
/// 静态纯函数类：只吃坐标吐坐标，不依赖控件、不依赖 <see cref="IToolContext"/> ——
/// 直尺、三角板、圆规等任何「带直边的模板工具」都从这里取用。
/// 与各插件自己的 *Geometry 类的分工：那里放「这个工具有关的全部数学」，这里只放通用的。
/// <para>
/// 投影<b>不夹</b>参数 t 是本类最要紧的约定：真实三角板沿边画线，笔尖滑出板外
/// 也要能继续画（延长线）—— 夹到 <c>[0,1]</c> 线就画不出板了。
/// 需要夹的场合（距离判定）用 <see cref="DistanceToSegment"/>，它内部自己夹。
/// </para>
/// </remarks>
public static class EdgeSnapHelper
{
    /// <summary>一条候选边（线段，世界坐标）。</summary>
    public readonly record struct Edge(Point From, Point To);

    /// <summary>最近边检测结果：边在列表里的序号、点到该边的距离、边上的最近点。</summary>
    public readonly record struct NearestEdgeResult(int Index, double Distance, Point ClosestPoint);

    /// <summary>点到线段的距离（投影参数夹到 <c>[0,1]</c>：端点之外按端点算）。</summary>
    public static double DistanceToSegment(Point point, Point from, Point to)
        => (point - ClosestPointOnSegment(point, from, to)).Length;

    /// <summary>点在<b>线段</b>上的最近点（t 夹到 <c>[0,1]</c>）。</summary>
    public static Point ClosestPointOnSegment(Point point, Point from, Point to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double lengthSquared = dx * dx + dy * dy;

        // 退化线段（两端重合）：最近点就是端点本身
        if (lengthSquared <= double.Epsilon) return from;

        double t = ((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared;
        if (t < 0) t = 0;
        else if (t > 1) t = 1;

        return new Point(from.X + t * dx, from.Y + t * dy);
    }

    /// <summary>
    /// 把点投影到边所在的<b>直线</b>上，返回投影点与参数 t。
    /// </summary>
    /// <remarks>
    /// t <b>不夹</b> <c>[0,1]</c>：t=0 对齐 <paramref name="from"/>、t=1 对齐 <paramref name="to"/>，
    /// 超出范围就是延长线 —— 「画出比三角板本身更长的线」靠的就是它。
    /// 退化线段（两端重合）返回端点本身、t=0。
    /// </remarks>
    public static Point ProjectOntoLine(Point point, Point from, Point to, out double t)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= double.Epsilon)
        {
            t = 0.0;
            return from;
        }

        t = ((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared;
        return new Point(from.X + t * dx, from.Y + t * dy);
    }

    /// <summary>
    /// 在候选边里找离点最近的一条。
    /// </summary>
    /// <remarks>
    /// 只报「最近」，<b>不做容差判断</b> —— 阈值由调用方定（三角板是屏幕 20 px 除以缩放）。
    /// 列表为空时返回序号 -1、距离正无穷。
    /// </remarks>
    public static NearestEdgeResult NearestEdge(Point point, IReadOnlyList<Edge> edges)
    {
        if (edges is null || edges.Count == 0)
        {
            return new NearestEdgeResult(-1, double.PositiveInfinity, default);
        }

        int bestIndex = 0;
        double bestDistance = double.MaxValue;
        var bestPoint = edges[0].From;

        for (int i = 0; i < edges.Count; i++)
        {
            var closest = ClosestPointOnSegment(point, edges[i].From, edges[i].To);
            double distance = (point - closest).Length;
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            bestIndex = i;
            bestPoint = closest;
        }

        return new NearestEdgeResult(bestIndex, bestDistance, bestPoint);
    }

    /// <summary>
    /// 顶点吸附：点距某顶点在容差内时吸附过去。
    /// </summary>
    /// <remarks>
    /// 距离相同时取序号靠前的（直角顶点排在首位，与三角板顶点顺序一致）。
    /// 容差非正或顶点表为空时一律不吸。
    /// </remarks>
    public static bool TrySnapToVertex(Point point, IReadOnlyList<Point> vertices, double tolerance, out Point snapped)
    {
        snapped = point;
        if (vertices is null || vertices.Count == 0 || double.IsNaN(tolerance) || tolerance <= 0) return false;

        Point? best = null;
        double bestDistance = tolerance;

        for (int i = 0; i < vertices.Count; i++)
        {
            double distance = (vertices[i] - point).Length;
            if (distance > bestDistance) continue;

            bestDistance = distance;
            best = vertices[i];
        }

        if (best is null) return false;

        snapped = best.Value;
        return true;
    }

    /// <summary>
    /// 点是否在三角形内（<b>含边界</b>）。
    /// </summary>
    /// <remarks>
    /// 叉积同号法：点对三条边做叉积，三个符号一致（全 ≥0 或全 ≤0）即在内。
    /// 对顺时针 / 逆时针顶点序都成立，退化三角形（三点共线）只对边上的点返回 true。
    /// </remarks>
    public static bool PointInTriangle(Point p, Point a, Point b, Point c)
    {
        double d1 = Cross(p, a, b);
        double d2 = Cross(p, b, c);
        double d3 = Cross(p, c, a);

        bool hasNegative = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPositive = d1 > 0 || d2 > 0 || d3 > 0;

        return !(hasNegative && hasPositive);
    }

    /// <summary>向量 <c>(c-a) × (b-a)</c>：p 相对有向线段 a→b 在哪一侧（正 = 顺时针侧，y 轴向下）。</summary>
    private static double Cross(Point c, Point a, Point b)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
