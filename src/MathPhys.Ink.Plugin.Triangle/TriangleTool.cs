using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Triangle;

/// <summary>本插件注册的工具 Id。</summary>
public static class TriangleToolIds
{
    /// <summary>45-45-90 三角板。快捷键 7（接在量角器的 6 之后）。</summary>
    public const string FortyFive = "triangle-45";

    /// <summary>30-60-90 三角板。快捷键 8。</summary>
    public const string SixtyNinety = "triangle-60";
}

/// <summary>
/// 三角板：按下放直角顶点 → 拖出尺寸与朝向（一条直角边贴齐已有直线）→ 抬笔落成可拖动/旋转/缩放的对象。
/// </summary>
/// <remarks>
/// 与量角器同一条链路：<see cref="IGfxTool"/>（创建型）、<see cref="TriangleRenderer"/>（渲染）、
/// <see cref="IBoardQuery"/>（吸附）。三条链路量角器已经走通，这里只是"再写一个几何类"。
/// <para>
/// 两种板型（45° / 60°）由构造函数决定，同一个类注册两次就是两个工具 —— 照 Ruler 的做法。
/// </para>
/// <para>
/// <b>不吸附也永远能画。</b><see cref="IToolContext.Query"/> 为 <c>null</c> 时全程退化成自由摆放。
/// </para>
/// </remarks>
public sealed class TriangleTool : ITool, IGfxTool
{
    /// <summary>拖动预览的填充透明度（很淡，避免盖住底下的卷子）。</summary>
    private const byte PreviewFillAlpha = 0x1A;

    private readonly TriangleGeometry.TriangleKind _kind;

    private IToolContext? _context;

    private bool _drawing;

    /// <summary>已吸附的直角顶点（世界坐标）—— 按下那一刻定下，之后<b>再不挪动</b>。</summary>
    private Point _vertex;

    private double _leg = TriangleGeometry.DefaultLegWorld;

    private double _orientation;

    private bool _snapped;

    private Path? _outlinePreview;

    /// <param name="kind">板型：45° 或 60°。</param>
    public TriangleTool(TriangleGeometry.TriangleKind kind) => _kind = kind;

    public string Id => _kind == TriangleGeometry.TriangleKind.SixtyThirtyNinety
        ? TriangleToolIds.SixtyNinety
        : TriangleToolIds.FortyFive;

    public string DisplayName => _kind == TriangleGeometry.TriangleKind.SixtyThirtyNinety
        ? "三角板·60°"
        : "三角板·45°";

    public string ToolTip => _kind == TriangleGeometry.TriangleKind.SixtyThirtyNinety
        ? "三角板（30-60-90）：按下放直角顶点（自动吸到端点或线上），往外拖出大小与朝向、"
        + "一条直角边自动贴齐已有直线，抬笔落下。落下后可用「选择」拖动、旋转。快捷键 8"
        : "三角板（45-45-90）：按下放直角顶点（自动吸到端点或线上），往外拖出大小与朝向、"
        + "一条直角边自动贴齐已有直线，抬笔落下。落下后可用「选择」拖动、旋转。快捷键 7";

    public Key? Shortcut => _kind == TriangleGeometry.TriangleKind.SixtyThirtyNinety ? Key.D8 : Key.D7;

    public bool UsesInkLayer => false;

    public bool NeedsPointer => true;

    public ToolInputKind InputKind => ToolInputKind.None;

    public ToolInkMode InkMode => ToolInkMode.None;

    public Cursor? Cursor => Cursors.None;

    public void Activate(IToolContext context)
    {
        _context = context;
        Reset();
    }

    /// <remarks>拖动中途被切走也会走到这里。私有状态必须复位，否则"切回来一按，三角板从上次的地方冒出来"。</remarks>
    public void Deactivate()
    {
        Reset();
        _context?.ClearPreview();
    }

    public void OnPointer(ToolPointer pointer)
    {
        var context = _context;
        if (context is null) return;

        switch (pointer.Phase)
        {
            case ToolPointerPhase.Down:
                _vertex = ResolveVertex(context, pointer.World);
                _drawing = true;
                _leg = TriangleGeometry.MinLegWorld;
                _orientation = 0.0;
                _snapped = false;

                context.ClearPreview();
                BuildPreview(context);
                UpdatePreview();
                break;

            case ToolPointerPhase.Move:
                if (!_drawing) return;
                Refresh(context, pointer.World);
                break;

            case ToolPointerPhase.Up:
                if (!_drawing) return;
                _drawing = false;
                Commit(context, pointer.World);
                break;
        }
    }

    // ---------------------------------------------------------------- 拖动中

    /// <summary>按当前指针位置重算边长与朝向，刷新预览与状态栏。</summary>
    /// <remarks>直角顶点在这一步绝不挪动（与量角器/直尺同一条）。</remarks>
    private void Refresh(IToolContext context, Point rawWorld)
    {
        double drag = (rawWorld - _vertex).Length;
        _leg = TriangleGeometry.ClampLeg(drag);

        if (drag >= TriangleGeometry.MinDragWorld)
        {
            _orientation = ResolveOrientation(context, _vertex,
                                              TriangleGeometry.OrientDegrees(_vertex, rawWorld),
                                              out _snapped);
        }
        else
        {
            _orientation = 0.0;
            _snapped = false;
        }

        UpdatePreview();
        context.SetStatus(TriangleGeometry.Describe(_kind, _leg, _snapped));
    }

    /// <summary>把一条直角边吸附到附近线段的轴方向；没有可吸的就原样返回。</summary>
    /// <remarks>
    /// <paramref name="vertex"/> <b>必须显式传入，绝不能读 <c>_vertex</c></b> ——
    /// 抬笔那条路（<see cref="Commit"/>）会先读成局部变量再 <see cref="Reset"/>，
    /// 而 <c>Reset</c> 会把 <c>_vertex</c> 归零（量角器踩过的一模一样的坑）。
    /// </remarks>
    private static double ResolveOrientation(IToolContext context, Point vertex, double orientation,
                                             out bool snapped)
    {
        snapped = false;

        var query = context.Query;
        if (query is null) return orientation;

        double search = Math.Max(TriangleGeometry.DefaultLegWorld, TriangleGeometry.VertexSnapToleranceWorld);
        var segments = query.SegmentsNear(vertex, search);

        if (!TriangleGeometry.TrySnapEdgeToSegments(orientation, segments, out double result)) return orientation;

        snapped = true;
        return result;
    }

    /// <summary>把按下点解析成直角顶点：先试端点，再试线段最近点，都不行就用原样。</summary>
    private static Point ResolveVertex(IToolContext context, Point world)
    {
        var query = context.Query;
        if (query is null) return world;

        const double tolerance = TriangleGeometry.VertexSnapToleranceWorld;

        if (TriangleGeometry.PickNearestPoint(world, query.PointsNear(world, tolerance), tolerance) is { } endpoint)
        {
            return endpoint;
        }

        if (TriangleGeometry.PickNearestSegment(world, query.SegmentsNear(world, tolerance), tolerance) is { } segment)
        {
            return TriangleGeometry.ClosestPointOn(world, segment.From, segment.To);
        }

        return world;
    }

    // ---------------------------------------------------------------- 抬笔

    private void Commit(IToolContext context, Point rawWorld)
    {
        var vertex = _vertex;
        double drag = (rawWorld - vertex).Length;

        context.ClearPreview();
        Reset();

        if (drag < TriangleGeometry.MinDragWorld)
        {
            context.SetStatus("三角板：拖动距离太短，未落下");
            return;
        }

        var gfx = context.Gfx;
        if (gfx is null)
        {
            context.SetStatus("三角板：当前程序不支持图形对象，三角板放不下");
            return;
        }

        double leg = TriangleGeometry.ClampLeg(drag);
        double orientation = TriangleGeometry.OrientDegrees(vertex, rawWorld);
        orientation = ResolveOrientation(context, vertex, orientation, out bool snapped);

        gfx.Add(new GfxDraft
        {
            Kind = TriangleRenderer.KindName,
            Center = vertex,
            RotationDegrees = orientation,
            Scale = 1.0,
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,

            // 边长与板型都是"参数"而不是 Scale：Scale 是整体放大，leg 是多大的三角板、kind 是哪把板
            Numbers = new Dictionary<string, double>
            {
                [TriangleGeometry.LegKey] = leg,
                [TriangleGeometry.KindKey] = _kind == TriangleGeometry.TriangleKind.SixtyThirtyNinety ? 1.0 : 0.0,
            },
        });

        string clamped = leg > drag + 1e-9 ? "（已按最小尺寸放大）" : string.Empty;
        context.SetStatus(TriangleGeometry.Describe(_kind, leg, snapped) + clamped);
    }

    // ---------------------------------------------------------------- 预览

    private void BuildPreview(IToolContext context)
    {
        var stroke = new SolidColorBrush(context.PenColor) { Opacity = 0x66 / 255.0 };
        var fill = new SolidColorBrush(Color.FromArgb(PreviewFillAlpha, 0x00, 0xA0, 0xFF));

        _outlinePreview = new Path
        {
            Stroke = stroke,
            StrokeThickness = context.PenWorldWidth,
            Fill = fill,
            Stretch = Stretch.None,
            IsHitTestVisible = false,
        };

        context.AddPreview(_outlinePreview);
    }

    /// <summary>刷新预览轮廓。</summary>
    /// <remarks>
    /// 轮廓几何以直角顶点为原点（成品与预览共用同一份几何），
    /// 摆位 <c>Canvas.Left/Top</c> 直接给<b>直角顶点本身</b>，不要再加包围盒左上角 ——
    /// <c>Stretch=None</c> 的 <c>Path</c> 不平移负坐标几何（量角器第 8 段钉死的那条）。
    /// </remarks>
    private void UpdatePreview()
    {
        var preview = _outlinePreview;
        if (preview is null) return;

        preview.Data = TriangleGeometry.BuildOutline(_kind, _leg);
        Canvas.SetLeft(preview, _vertex.X);
        Canvas.SetTop(preview, _vertex.Y);
    }

    // ---------------------------------------------------------------- 复位

    private void Reset()
    {
        _drawing = false;
        _outlinePreview = null;
        _vertex = default;
        _leg = TriangleGeometry.DefaultLegWorld;
        _orientation = 0.0;
        _snapped = false;
    }
}
