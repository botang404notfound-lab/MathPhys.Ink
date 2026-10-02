using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;
using MathPhys.Ink.Gfx;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Tools;

/// <summary>
/// <see cref="IBoardQuery"/> 的宿主实现：把画布上已有的东西变成"可吸附的线段与端点"。
/// </summary>
/// <remarks>
/// 数据来源是<b>墨迹层</b>：老师手写的字、直尺画的辅助线都在那里。
/// 图形对象层（M7.4 Step 0）目前还没有"线段型"对象（量角器是盘、坐标系是格），
/// 所以这里只读墨迹；等真有线段型对象时再加一路来源，接口本身不用改。
/// <para>
/// <b>两个设计取舍，都是被实测教训逼出来的</b>：
/// </para>
/// <list type="number">
/// <item><b>惰性建索引</b>。笔迹变化是高频的（写字时每一笔都触发 <c>StrokesChanged</c>），
/// 而查询是低频的（只在支持吸附的工具被拖动时发生）。所以变化时只标一个脏位，
/// 等真有人来问才重建 —— 写字这条最热的路径上，一次额外开销都不花。
/// 若反过来"一变就重建"，几百条笔画的一节课上，每一笔都要遍历全部笔画算包围盒。</item>
/// <item><b>包围盒粗筛</b>。传入的 <c>radiusWorld</c> 是调用方给的界，
/// 先用它把笔画筛掉一大半，再逐段精算距离。
/// 一节课下来几百条笔画，逐条精算会在拖动时掉帧 —— 而拖动掉帧是老师在讲台上立刻能感觉到的。</item>
/// </list>
/// <para>
/// 结果有<b>上限</b>（<see cref="MaxResults"/>）：这个接口是给"找一个最近的目标"用的，
/// 不是给"遍历全画布"用的。上限同时兜住了"某条笔画有几千个点"的极端情况。
/// </para>
/// </remarks>
public sealed class BoardQuery : IBoardQuery
{
    /// <summary>单次查询最多返回多少条结果（防止异常输入把结果集撑爆）。</summary>
    private const int MaxResults = 32;

    private readonly StrokeCollection _strokes;

    /// <summary>图形对象来源（M12 吸附用）。可空：没接图形层时（旧用法 / 部分替身）吸附目标只有墨迹。</summary>
    private readonly Func<IReadOnlyList<IGfxObjectRef>>? _gfxObjects;

    /// <summary>渲染器注册表（按 Kind 找出谁实现了 ISnapTargetProvider）。</summary>
    private readonly GfxRendererCatalog? _gfxCatalog;

    /// <summary>笔画包围盒索引；<see cref="_dirty"/> 为真时重建。</summary>
    private readonly List<Entry> _index = new();

    private bool _dirty = true;

    /// <summary>单次吸附查询最多收集多少个候选点（网格交点可能很多，得有界）。</summary>
    private const int MaxSnapCandidates = 256;

    public BoardQuery(StrokeCollection strokes)
        : this(strokes, gfxObjects: null, gfxCatalog: null)
    {
    }

    /// <summary>
    /// 完整构造（M12）：接入图形对象与渲染器目录后，<see cref="SnapToGfx"/> 才有图形来源。
    /// 旧的单参构造保留 —— harness 与既有调用点一行不改。
    /// </summary>
    public BoardQuery(StrokeCollection strokes,
                      Func<IReadOnlyList<IGfxObjectRef>>? gfxObjects,
                      GfxRendererCatalog? gfxCatalog)
    {
        _strokes = strokes ?? throw new ArgumentNullException(nameof(strokes));
        _gfxObjects = gfxObjects;
        _gfxCatalog = gfxCatalog;
        _strokes.StrokesChanged += (_, _) => _dirty = true;
    }

    /// <summary>索引里当前的笔画数（验收用：证明"换了文档之后索引也空了"）。</summary>
    public int IndexedStrokeCount
    {
        get
        {
            EnsureIndex();
            return _index.Count;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BoardSegment> SegmentsNear(Point world, double radiusWorld)
    {
        var results = new List<BoardSegment>();

        if (!(radiusWorld > 0) || double.IsNaN(radiusWorld) || double.IsInfinity(radiusWorld)) return results;
        if (double.IsNaN(world.X) || double.IsNaN(world.Y)) return results;

        EnsureIndex();

        var box = Box(world, radiusWorld);

        foreach (var entry in _index)
        {
            if (!entry.Bounds.IntersectsWith(box)) continue;

            var points = entry.Stroke.StylusPoints;
            if (points.Count < 2) continue;

            for (int i = 0; i + 1 < points.Count; i++)
            {
                var a = new Point(points[i].X, points[i].Y);
                var b = new Point(points[i + 1].X, points[i + 1].Y);

                if (DistanceToSegment(world, a, b) > radiusWorld) continue;

                results.Add(new BoardSegment(a, b));
                if (results.Count >= MaxResults) return results;
            }
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<Point> PointsNear(Point world, double radiusWorld)
    {
        var results = new List<Point>();

        if (!(radiusWorld > 0) || double.IsNaN(radiusWorld) || double.IsInfinity(radiusWorld)) return results;
        if (double.IsNaN(world.X) || double.IsNaN(world.Y)) return results;

        EnsureIndex();

        var box = Box(world, radiusWorld);

        foreach (var entry in _index)
        {
            if (!entry.Bounds.IntersectsWith(box)) continue;

            var points = entry.Stroke.StylusPoints;
            if (points.Count == 0) continue;

            // 只取首末两点：中间那些是笔画的"路过的点"，不是老师心里的"角顶"。
            if (TryAddPoint(results, world, radiusWorld, new Point(points[0].X, points[0].Y))
                && results.Count >= MaxResults) return results;

            if (TryAddPoint(results, world, radiusWorld, new Point(points[^1].X, points[^1].Y))
                && results.Count >= MaxResults) return results;
        }

        return results;
    }

    /// <summary>
    /// 点到线段的距离（线段而不是无限长直线 —— 差这一个截断，
    /// 就会让量角器吸附到"那条线的延长线上的空气里"）。
    /// </summary>
    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= double.Epsilon) return (p - a).Length;

        // 投影参数夹到 [0,1] ⇒ 投影点落在线段内，这正是"最近点"的定义
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared;
        if (t < 0) t = 0;
        else if (t > 1) t = 1;

        var projection = new Point(a.X + t * dx, a.Y + t * dy);
        return (p - projection).Length;
    }

    /// <summary>点在 <paramref name="segment"/> 上的最近点（量角器把圆心吸到线上的落点）。</summary>
    public static Point ClosestPointOn(Point p, Point a, Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= double.Epsilon) return a;

        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared;
        if (t < 0) t = 0;
        else if (t > 1) t = 1;

        return new Point(a.X + t * dx, a.Y + t * dy);
    }

    /// <inheritdoc />
    public Point? SnapToGfx(Point world, double radiusWorld)
    {
        if (!(radiusWorld > 0) || double.IsNaN(radiusWorld) || double.IsInfinity(radiusWorld)) return null;
        if (double.IsNaN(world.X) || double.IsNaN(world.Y)) return null;
        if (_gfxObjects is null || _gfxCatalog is null) return null;

        // 逐对象问渲染器要吸附目标（渲染器异常只损失它自己，照渲染层的同一政策）
        var collector = new SnapCollector(world, radiusWorld, MaxSnapCandidates);
        var box = Box(world, radiusWorld);

        foreach (var obj in _gfxObjects())
        {
            if (obj is null) continue;

            // 包围盒粗筛：离得远的对象连问都不问（与墨迹索引同一条性能纪律）
            var bounds = ComputeBounds(obj);
            if (bounds is { } b && !b.IntersectsWith(box) && !b.Contains(world)) continue;

            var renderer = _gfxCatalog.Find(obj.Kind);
            if (renderer is not ISnapTargetProvider provider) continue;

            try
            {
                if (!provider.TryCollectSnapTargets(obj, collector)) continue;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"渲染器 {renderer.GetType().Name}（{obj.Kind}）收集吸附目标时抛异常，已跳过："
                            + $"{ex.GetType().Name} {ex.Message}");
            }

            if (collector.Full) break;
        }

        return collector.Nearest();
    }

    /// <summary>对象的世界包围盒（失败时 null = 不做粗筛，直接问渲染器）。</summary>
    private static Rect? ComputeBounds(IGfxObjectRef obj)
    {
        try
        {
            return obj.BoundsWorld;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 吸附候选收集器：收点/线的同时记下"离目标最近的点"，收满即停。
    /// </summary>
    private sealed class SnapCollector : ISnapTargetCollector
    {
        private readonly Point _target;
        private readonly double _radius;
        private readonly int _max;

        private Point _best;
        private double _bestDistanceSquared = double.MaxValue;
        private int _count;

        public SnapCollector(Point target, double radius, int max)
        {
            _target = target;
            _radius = radius;
            _max = max;
        }

        public bool Full => _count >= _max;

        public void AddPoint(Point world) => Consider(world);

        public void AddLine(Point from, Point to)
        {
            // 线段目标 = 线上最近点（半径外的线段，最近点也必在半径外，Consider 会自然拒掉）
            Consider(ClosestPointOn(_target, from, to));
        }

        /// <summary>取最近的候选；一个都没收上（或全在半径外）返回 null。</summary>
        public Point? Nearest() => _bestDistanceSquared <= _radius * _radius ? _best : null;

        private void Consider(Point candidate)
        {
            double d2 = (candidate - _target).LengthSquared;
            if (d2 >= _bestDistanceSquared) return;
            if (d2 > _radius * _radius) return;

            _bestDistanceSquared = d2;
            _best = candidate;
            _count++;
        }
    }

    private static Rect Box(Point world, double radius)
        => new(world.X - radius, world.Y - radius, radius * 2.0, radius * 2.0);

    /// <summary>加一个点（去重、按半径过滤）；返回是否真的加了。</summary>
    private static bool TryAddPoint(List<Point> results, Point world, double radius, Point candidate)
    {
        if ((candidate - world).Length > radius) return false;

        // 一条直线的两端点可能来自两条重叠的笔画（直尺画的线叠在别的线上），
        // 不去重就会让同一个顶点出现好几次，调用方的"最近点"选择里就全是重复项。
        foreach (var existing in results)
        {
            if ((existing - candidate).Length <= 1e-6) return false;
        }

        results.Add(candidate);
        return true;
    }

    private void EnsureIndex()
    {
        if (!_dirty) return;
        _dirty = false;

        _index.Clear();

        foreach (Stroke stroke in _strokes)
        {
            if (stroke.StylusPoints.Count == 0) continue;
            _index.Add(new Entry(stroke, stroke.GetBounds()));
        }
    }

    /// <summary>笔画 + 它的包围盒。</summary>
    private readonly struct Entry
    {
        public Entry(Stroke stroke, Rect bounds)
        {
            Stroke = stroke;
            Bounds = bounds;
        }

        public readonly Stroke Stroke;

        public readonly Rect Bounds;
    }
}
