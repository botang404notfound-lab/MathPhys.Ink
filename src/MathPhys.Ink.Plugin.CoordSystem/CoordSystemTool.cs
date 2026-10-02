using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CoordSystem;

/// <summary>本插件注册的工具 Id。</summary>
public static class CoordSystemToolIds
{
    /// <summary>坐标系（带网格，族代表）。快捷键 C。</summary>
    public const string Id = "coordsystem";

    /// <summary>坐标系·无网格（平面 xOy，M23）：同一条交互与渲染链路，只是不画网格。</summary>
    public const string Plain = "coordsystem-plain";

    /// <summary>空间直角坐标系（xOyZ 静态轴测，M23）。</summary>
    public const string Space3D = "coordsystem-3d";
}

/// <summary>
/// 坐标系：按下放原点 → 拖出单位长度 → 抬笔落成可拖动/旋转/缩放的对象。
/// </summary>
/// <remarks>
/// 与量角器/三角板同一条链路：<see cref="IGfxTool"/>（创建型）、<see cref="CoordSystemRenderer"/>（渲染）、
/// <see cref="IToolContext.PageRects"/>（吸页中心/页边距线）。三者**都不需要新的契约成员**：
/// 量角器默认走 <c>IBoardQuery</c>，坐标系走 <c>PageRects</c>，都是接口里早就预留的。
/// <para>
/// 原点吸附：<b>只吸页中心 / 页边 / 页边中点</b>（<see cref="CoordSystemGeometry.PageSnapCandidates"/>），
/// 不吸卷面已有线段（不是坐标系的核心用法）、也不吸"已有坐标系原点"
/// （那是 Step 4 函数图像"绑定坐标系"才用到的能力，等真要时再扩 <c>IBoardQuery</c>）。
/// </para>
/// <para>
/// <b>不吸附也永远能画。</b><see cref="IToolContext.PageRects"/> 为空时退化成自由摆放。
/// </para>
/// </remarks>
public sealed class CoordSystemTool : ITool, IGfxTool
{
    private readonly string _id;
    private readonly string _displayName;
    private readonly double _showGridDefault;

    /// <summary>族代表：带网格的平面直角坐标系。</summary>
    public CoordSystemTool()
        : this(CoordSystemToolIds.Id, "坐标系", showGrid: true)
    {
    }

    /// <summary>
    /// 变体构造（M23）：无网格平面坐标系<b>复用同一条交互与渲染链路</b>，
    /// 只是落对象时 <c>showGrid = 0</c>（渲染器按参数决定画不画网格，不需要第二个 Kind）。
    /// </summary>
    public CoordSystemTool(string id, string displayName, bool showGrid)
    {
        _id = id;
        _displayName = displayName;
        _showGridDefault = showGrid ? 1.0 : 0.0;
    }

    private IToolContext? _context;

    private bool _drawing;

    /// <summary>已吸附的原点（世界坐标）—— 按下那一刻定下，之后<b>再不挪动</b>。</summary>
    private Point _origin;

    private double _unitWorld = CoordSystemGeometry.DefaultUnitWorld;

    private int _pageIndex;          // 吸到了哪一页（状态栏说"在第 N 页"）

    private Path? _axesPreview;       // 拖动中的轴 + 箭头（预览层最显眼）

    public string Id => _id;

    public string DisplayName => _displayName;

    public string ToolTip
        => _showGridDefault > 0
        ? "坐标系：按下放原点（自动吸到当前页的页中心/页边），往外拖出单位长度（1 cm 为准），"
          + "抬笔落下。落下后可用「选择」拖动、旋转、缩放。快捷键 C"
        : "无网格坐标系：只画 xOy 两根轴与刻度数字，不画网格 —— 适合在卷面上自己标坐标。"
          + "按法与「坐标系」相同：按下放原点，拖出单位长度，抬笔落下。";

    /// <summary>
    /// 快捷键 <c>C</c>（坐标系 / Coordinate）。
    /// </summary>
    /// <remarks>
    /// ★ 原为 <c>D9</c>，与 M17 的「三角板·沿边画线」撞车（两处都写 9、两个 tooltip 都宣称 9）：
    /// <see cref="ToolRegistry.FindByShortcut"/> 只返回<b>第一个</b>命中的工具，插件按目录名排序加载 ⇒
    /// 坐标系赢、沿边画线的快捷键变成死的 —— 而老师手上的操作卡写着"按 9 沿边画线"。
    /// M17 的分配（docs/22 + 操作卡）比 M7.4 的（docs/11）新，所以这里是本工具让位。
    /// </remarks>
    public Key? Shortcut => _showGridDefault > 0 ? Key.C : null;

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
                (_origin, _pageIndex) = ResolveOrigin(context, pointer.World);
                _drawing = true;
                _unitWorld = CoordSystemGeometry.DefaultUnitWorld;

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

    private void Refresh(IToolContext context, Point rawWorld)
    {
        double drag = (rawWorld - _origin).Length;
        _unitWorld = CoordSystemGeometry.ClampUnit(drag);

        UpdatePreview();
        context.SetStatus(Describe());
    }

    // ---------------------------------------------------------------- 抬笔

    private void Commit(IToolContext context, Point rawWorld)
    {
        var origin = _origin;
        double drag = (rawWorld - origin).Length;

        context.ClearPreview();
        Reset();

        if (drag < CoordSystemGeometry.MinDragWorld)
        {
            context.SetStatus(_displayName + "：拖动距离太短，未落下");
            return;
        }

        var gfx = context.Gfx;
        if (gfx is null)
        {
            context.SetStatus(_displayName + "：当前程序不支持图形对象，坐标系放不下");
            return;
        }

        double unit = CoordSystemGeometry.ClampUnit(drag);

        gfx.Add(new GfxDraft
        {
            Kind = CoordSystemRenderer.KindName,
            Center = origin,
            RotationDegrees = 0.0,
            Scale = 1.0,
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,

            // 范围/步长/开关都是"参数"：Scale 是整体放大，这些是坐标系的范围与显示控制
            Numbers = new Dictionary<string, double>
            {
                [CoordSystemGeometry.UnitWorldKey] = unit,
                [CoordSystemGeometry.XMinKey] = CoordSystemGeometry.DefaultRange.Min,
                [CoordSystemGeometry.XMaxKey] = CoordSystemGeometry.DefaultRange.Max,
                [CoordSystemGeometry.YMinKey] = CoordSystemGeometry.DefaultRange.Min,
                [CoordSystemGeometry.YMaxKey] = CoordSystemGeometry.DefaultRange.Max,
                [CoordSystemGeometry.StepKey] = CoordSystemGeometry.DefaultRange.Step,
                [CoordSystemGeometry.ShowGridKey] = _showGridDefault,
                [CoordSystemGeometry.ShowLabelsKey] = 1.0,
                [CoordSystemGeometry.LockAspectKey] = 1.0,
            },
        });

        string clamped = unit > drag + 1e-9 ? "（已按最小尺寸放大）" : string.Empty;
        context.SetStatus(Describe() + clamped);
    }

    // ---------------------------------------------------------------- 原点解析（吸页中心 / 页边）

    /// <summary>
    /// 找出按下点最近的页，在那一页的 9 个候选里挑最近的（在容差内），并返回页索引。
    /// </summary>
    /// <remarks>
    /// 没有 <see cref="IToolContext.PageRects"/> ⇒ 返回 <c>world</c> 本身（退化自由摆放）。
    /// </remarks>
    internal static (Point Origin, int PageIndex) ResolveOrigin(IToolContext context, Point world)
    {
        var pages = context.PageRects;
        if (pages is null || pages.Count == 0) return (world, -1);

        Rect bestPage = pages[0];
        int bestIndex = 0;
        double bestSqr = double.MaxValue;
        for (int i = 0; i < pages.Count; i++)
        {
            // 候选点：把"按下点"先投影到页内最近的点（按"页是否包含按下点"挑）
            var p = pages[i];
            double dx = Math.Clamp(world.X, p.X, p.Right) - world.X;
            double dy = Math.Clamp(world.Y, p.Y, p.Bottom) - world.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestSqr)
            {
                bestSqr = d2;
                bestPage = p;
                bestIndex = i;
            }
        }

        var snapped = CoordSystemGeometry.SnapOriginToPage(
            bestPage, world, CoordSystemGeometry.PageSnapToleranceWorld);

        return (snapped, snapped == world ? -1 : bestIndex + 1);   // 1-based：状态栏里"第 1 页"
    }

    // ---------------------------------------------------------------- 预览

    private void BuildPreview(IToolContext context)
    {
        var stroke = new SolidColorBrush(context.PenColor) { Opacity = 0x66 / 255.0 };
        var fill = new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0xA0, 0xFF));

        _axesPreview = new Path
        {
            Stroke = stroke,
            StrokeThickness = context.PenWorldWidth,
            Fill = fill,
            Stretch = Stretch.None,
            IsHitTestVisible = false,
        };

        context.AddPreview(_axesPreview);
    }

    /// <summary>
    /// 刷新预览：轴 + 箭头（网格 / 标签太密，预览里不画，免得遮住底下的卷子）。
    /// </summary>
    /// <remarks>
    /// 几何以<b>数学原点为中心</b>画（坐标为 (x, y) 的数学点；y 用 FlipY），轮廓范围用
    /// 默认 [-10,10] × [-10,10]，单位长度 1 本地 = _unitWorld 世界。摆位 <c>Canvas.Left/Top</c>
    /// 直接给<b>原点本身</b> —— 与量角器第 8 段钉死的那条不变量一致：
    /// <c>Stretch=None</c> 的 <c>Path</c> 不平移负坐标几何。
    /// </remarks>
    private void UpdatePreview()
    {
        var preview = _axesPreview;
        if (preview is null) return;

        // 预览只画"轴 + 箭头"，范围取默认值（× unitWorld）
        double xMin = CoordSystemGeometry.DefaultRange.Min * _unitWorld;
        double xMax = CoordSystemGeometry.DefaultRange.Max * _unitWorld;
        double yMin = CoordSystemGeometry.DefaultRange.Min * _unitWorld;
        double yMax = CoordSystemGeometry.DefaultRange.Max * _unitWorld;

        var axes = new StreamGeometry();
        using (var ctx = axes.Open())
        {
            ctx.BeginFigure(new Point(xMin, 0), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(xMax, 0), isStroked: true, isSmoothJoin: false);
            ctx.BeginFigure(new Point(0, yMin), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(0, yMax), isStroked: true, isSmoothJoin: false);
        }
        axes.Freeze();

        preview.Data = axes;
        Canvas.SetLeft(preview, _origin.X);
        Canvas.SetTop(preview, _origin.Y);
    }

    // ---------------------------------------------------------------- 复位

    private void Reset()
    {
        _drawing = false;
        _axesPreview = null;
        _origin = default;
        _unitWorld = CoordSystemGeometry.DefaultUnitWorld;
        _pageIndex = -1;
    }

    private string Describe()
    {
        string pageText = _pageIndex > 0 ? $"（吸到第 {_pageIndex} 页）" : string.Empty;
        return $"{_displayName}：原点 ( — ),单位长度 {CoordSystemGeometry.ToCentimeters(_unitWorld):F2} cm ⇒ 一格 {CoordSystemGeometry.ToCentimeters(_unitWorld):F2} cm{pageText}";
    }
}
