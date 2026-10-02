using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Protractor;

/// <summary>本插件注册的工具 Id。</summary>
public static class ProtractorToolIds
{
    /// <summary>量角器。快捷键 6（接在直尺的 4 / 5 之后）。</summary>
    public const string Protractor = "protractor";
}

/// <summary>
/// 量角器：按下放圆心 → 拖出半径与基线方向 → 抬笔落成一个可拖动 / 旋转 / 缩放的对象。
/// </summary>
/// <remarks>
/// 与直尺的关键差别：直尺抬笔交的是<b>一条笔画</b>，量角器交的是一个<b>图形对象</b>。
/// 后者带来"可拖动、可旋转、可改参数、可存档"，代价是要顺着对象层那一整套约定走
/// （本地坐标以圆心为原点、位姿交给宿主、参数进 <c>Numbers</c>）。
/// <para>
/// 它同时压住 M7.4 的三条新链路：<b>创建型工具</b>（<see cref="IGfxTool"/>）、
/// <b>对象渲染</b>（<see cref="ProtractorRenderer"/>）、<b>吸附到已有直线</b>（<see cref="IBoardQuery"/>）。
/// 三条都通了，后面四个工具就退化成"再写一个几何类"。
/// </para>
/// <para>
/// <b>不吸附也永远能画。</b><see cref="IToolContext.Query"/> 为 <c>null</c> 时全程退化成自由摆放 ——
/// 少一个自动对齐是少个便利，而"按下去什么也不出来"是故障。
/// </para>
/// </remarks>
public sealed class ProtractorTool : ITool, IGfxTool
{
    /// <summary>拖动预览的画笔透明度。</summary>
    private const byte PreviewStrokeAlpha = 0x66;

    /// <summary>拖动预览的盘面填充透明度（比成品更淡，避免盖住底下的卷子）。</summary>
    private const byte PreviewFaceAlpha = 0x1A;

    private IToolContext? _context;

    private bool _drawing;

    /// <summary>已吸附的圆心（世界坐标）—— 按下那一刻定下，之后<b>再不挪动</b>。</summary>
    private Point _center;

    private double _radius = ProtractorGeometry.DefaultRadiusWorld;

    private double _orientation;

    private bool _snapped;

    private Path? _discPreview;

    public string Id => ProtractorToolIds.Protractor;

    public string DisplayName => "量角器";

    public string ToolTip => "量角器：按下把圆心放上去（会自动吸到已有线段的端点或线上），"
                           + "往外拖出大小与基线朝向，抬笔落下。落下后可用「选择」拖动、旋转。快捷键 6";

    public Key? Shortcut => Key.D6;

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

    /// <remarks>
    /// 拖到一半被切走也会走到这里。<b>私有状态必须在这里复位</b> ——
    /// 宿主虽然会清预览层，但"上一次的圆心"是我们自己的状态，
    /// 不复位就会表现为"切回来一按，盘面从上次那个地方冒出来"。
    /// </remarks>
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
                _center = ResolveCenter(context, pointer.World);
                _drawing = true;
                _radius = ProtractorGeometry.MinRadiusWorld;
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

    /// <summary>按当前指针位置重算半径与朝向，刷新预览与状态栏。</summary>
    /// <remarks>
    /// <b>圆心在这一步绝不挪动</b>（与直尺"起点永不被吸附挪动"是同一条）：
    /// 圆心一动，量角器就会跟着手指跳，老师永远对不准那个顶点。
    /// </remarks>
    private void Refresh(IToolContext context, Point rawWorld)
    {
        double drag = (rawWorld - _center).Length;
        _radius = ProtractorGeometry.ClampRadius(drag);

        if (drag >= ProtractorGeometry.MinDragWorld)
        {
            _orientation = ResolveOrientation(context, _center,
                                              ProtractorGeometry.OrientDegrees(_center, rawWorld),
                                              _radius, out _snapped);
        }
        else
        {
            // 还没拖出可见距离：朝向没有意义，别把 0° 的噪声吸成某个角度显示出来
            _orientation = 0.0;
            _snapped = false;
        }

        UpdatePreview();
        context.SetStatus(ProtractorGeometry.Describe(_radius, _orientation, _snapped));
    }

    /// <summary>把朝向吸附到附近线段的轴方向；没有可吸的就原样返回。</summary>
    /// <remarks>
    /// <paramref name="center"/> <b>必须由调用方显式传入，绝不能在这里读 <c>_center</c></b>。
    /// 抬笔那条路（<see cref="Commit"/>）会先把状态读成局部变量、再 <see cref="Reset"/>，
    /// 而 <c>Reset</c> 会把 <c>_center</c> 归零 —— 在这里读字段，就成了拿 (0,0) 去问
    /// "圆心附近有什么线"，于是<b>吸附静默失效</b>：屏幕上什么异常都没有，只是没对齐，
    /// 而"没对齐"恰恰是这把工具唯一存在的理由。
    /// <para>
    /// 这类"调用顺序陷阱"不会自己暴露，根除办法只有一个：<b>不共享可变状态</b>，
    /// 需要什么就从参数拿。同理 <see cref="ResolveCenter"/> 也是静态的。
    /// </para>
    /// </remarks>
    private static double ResolveOrientation(IToolContext context, Point center, double orientation,
                                             double radius, out bool snapped)
    {
        snapped = false;

        var query = context.Query;
        if (query is null) return orientation;

        // 搜索半径取"盘面自身的大小"：要对齐的那条边通常就从圆心出发，落在盘面范围内。
        // 半径给得比盘面大没有意义 —— 只会捞到远处的表格线。
        double search = Math.Max(radius, ProtractorGeometry.CenterSnapToleranceWorld);
        var segments = query.SegmentsNear(center, search);

        if (!ProtractorGeometry.TrySnapOrientationToSegments(orientation, segments, out double result)) return orientation;

        snapped = true;
        return result;
    }

    /// <summary>把按下点解析成圆心：先试端点，再试线段上的最近点，都不行就用原样。</summary>
    private static Point ResolveCenter(IToolContext context, Point world)
    {
        var query = context.Query;
        if (query is null) return world;

        const double tolerance = ProtractorGeometry.CenterSnapToleranceWorld;

        // 端点优先：角的顶点、辅助线的起点都在那里，那是老师心里想说"放这儿"的位置
        if (ProtractorGeometry.PickNearestPoint(world, query.PointsNear(world, tolerance), tolerance) is { } endpoint)
        {
            return endpoint;
        }

        if (ProtractorGeometry.PickNearestSegment(world, query.SegmentsNear(world, tolerance), tolerance) is { } segment)
        {
            return ProtractorGeometry.ClosestPointOn(world, segment.From, segment.To);
        }

        return world;
    }

    // ---------------------------------------------------------------- 抬笔

    private void Commit(IToolContext context, Point rawWorld)
    {
        // 先把状态读成局部变量，再清预览、复位 —— 顺序反了就是"用默认值落一个对象"。
        // 注意：下面所有用到"圆心"的地方都必须用这个局部变量，不能回头读 _center。
        var center = _center;
        double drag = (rawWorld - center).Length;

        context.ClearPreview();
        Reset();

        if (drag < ProtractorGeometry.MinDragWorld)
        {
            // 误触：老师切工具时习惯在卷面上点一下，没有这一条，卷面会被一个个量角器铺满
            context.SetStatus("量角器：拖动距离太短，未落下");
            return;
        }

        var gfx = context.Gfx;
        if (gfx is null)
        {
            context.SetStatus("量角器：当前程序不支持图形对象，量角器放不下");
            return;
        }

        double radius = ProtractorGeometry.ClampRadius(drag);
        double orientation = ProtractorGeometry.OrientDegrees(center, rawWorld);
        orientation = ResolveOrientation(context, center, orientation, radius, out bool snapped);

        gfx.Add(new GfxDraft
        {
            Kind = ProtractorRenderer.KindName,
            Center = center,
            RotationDegrees = orientation,
            Scale = 1.0,
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,

            // 半径是"参数"而不是 Scale：Scale 表示老师用缩放手柄把它整体放大，
            // 半径表示这是多大的量角器。两件事混在一起，日后做参数面板时就没法区分了。
            Numbers = new Dictionary<string, double> { [ProtractorGeometry.RadiusKey] = radius },
        });

        string clamped = radius > drag + 1e-9 ? "（已按最小尺寸放大）" : string.Empty;
        context.SetStatus(ProtractorGeometry.Describe(radius, orientation, snapped) + clamped);
    }

    // ---------------------------------------------------------------- 预览

    private void BuildPreview(IToolContext context)
    {
        var stroke = new SolidColorBrush(context.PenColor) { Opacity = PreviewStrokeAlpha / 255.0 };
        var fill = new SolidColorBrush(Color.FromArgb(PreviewFaceAlpha, 0x00, 0xA0, 0xFF));

        _discPreview = new Path
        {
            Stroke = stroke,
            StrokeThickness = context.PenWorldWidth,
            Fill = fill,
            Stretch = Stretch.None,
            IsHitTestVisible = false,
        };

        context.AddPreview(_discPreview);
    }

    /// <summary>
    /// 刷新预览盘面。
    /// </summary>
    /// <remarks>
    /// 盘面几何以<b>圆心为原点</b>构建（成品与预览共用同一份几何代码），
    /// 所以这里要把元素摆到圆心处。摆法用 <c>Canvas.Left/Top</c> 而不是
    /// <c>RenderTransform</c>：预览层是世界坐标层，摆位 = 元素在 world 里的位置，
    /// 几何坐标与元素位置是<b>两个各自确定</b>的量，不存在"谁又乘了一遍矩阵"的歧义。
    /// <para>
    /// <b>坐标就是圆心本身，不要再加 <c>bounds.X/Y</c></b>。这不是风格问题：
    /// <c>Stretch=None</c> 的 <c>Path</c> <b>不会</b>把几何平移到非负区，
    /// 它的<b>几何原点就是元素原点</b>（负坐标几何会画到布局槽外面去，Canvas 不裁剪，
    /// 所以看起来一切正常）。
    /// </para>
    /// <para>
    /// 曾经写成 <c>_center + bounds.X/Y</c>（按"元素左上角 = 包围盒左上角"的直觉），
    /// 结果预览盘面在拖动时整体飘到手指的<b>左上方一条半径</b>处 ——
    /// 而这条偏差在代码里看不出、日志里也看不出，是 harness 里把
    /// "蓝色像素外接框"和"圆心 − 半径"逐像素比对才逼出来的
    /// （<c>RunM74S1Checks</c> 第 8 段）。
    /// </para>
    /// </remarks>
    private void UpdatePreview()
    {
        var preview = _discPreview;
        if (preview is null) return;

        preview.Data = ProtractorGeometry.BuildDisc(_radius);
        Canvas.SetLeft(preview, _center.X);
        Canvas.SetTop(preview, _center.Y);
    }

    // ---------------------------------------------------------------- 复位

    private void Reset()
    {
        _drawing = false;
        _discPreview = null;
        _center = default;
        _radius = ProtractorGeometry.DefaultRadiusWorld;
        _orientation = 0.0;
        _snapped = false;
    }
}
