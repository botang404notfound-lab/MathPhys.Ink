using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Triangle;

/// <summary>本工具注册的 Id。</summary>
public static class TriangleTraceToolIds
{
    /// <summary>沿边画线（板型由画布上已有的板决定）。快捷键 9（接在两把三角板之后）。</summary>
    public const string Trace = "triangle-trace";
}

/// <summary>
/// 三角板·沿边画线：在已放好的三角板<b>图形对象</b>上工作 ——
/// 落笔靠近某条边就沿该边画直线（可画出比板更长的延长线），落笔在板内就拖动整块板，
/// 落笔在板外忽略并提示。板本身不增不减，画出来的线是普通墨迹。
/// </summary>
/// <remarks>
/// 与 <see cref="TriangleTool"/>（创建型：落成三角板对象）的分工：
/// 先用创建型把板摆好（尺寸、朝向都在那一步定），再用本工具「用」板。
/// 板 = 画布上的 <c>triangle</c> 图形对象（<see cref="TriangleRenderer.KindName"/>），
/// 本工具经 <see cref="IGfxObjectHost"/> 读它、经 <see cref="IGfxObjectHost.UpdatePose"/> 拖它 ——
/// 宿主、画布层、墨迹层<b>零改动</b>。
/// <para>
/// 几何（边检测 + 投影 + 顶点吸附）全部在 <see cref="EdgeSnapHelper"/> 里 ——
/// 那是给直尺、圆规共用的公共件，本类只管状态机与预览。
/// </para>
/// <para>
/// <b>不做</b> <see cref="IGfxTool"/>：本工具落的是墨迹不是图形对象，
/// 画完一条还要画下一条、拖一下板，自动弹回「选择」只会打断节奏。
/// 板的旋转交给「选择」工具（已有能力）—— 工具内再做旋转把手会跟
/// 「沿边画线 / 拖板」抢手势，一体机单笔分不开。
/// </para>
/// <para>
/// 多块板叠加时只认 <b>Z 序最上层</b>那块：规则一句话说得清，
/// 老师想换板就把上层那块挪开或删掉。
/// </para>
/// </remarks>
public sealed class TriangleTraceTool : ITool
{
    /// <summary>边吸附容差（屏幕像素）。除以缩放变世界单位，保证不同缩放下手感一致。</summary>
    private const double EdgeTolerancePixels = 20.0;

    /// <summary>顶点吸附容差（世界单位）。与 <see cref="TriangleGeometry.VertexSnapToleranceWorld"/> 一致：手指在一体机上是胖的。</summary>
    private const double VertexSnapToleranceWorld = TriangleGeometry.VertexSnapToleranceWorld;

    /// <summary>按下到抬起小于此距离视为误触，不落墨（与直尺同一条）。</summary>
    private const double MinDragWorld = 2.0;

    /// <summary>拖板「真的动了」的位移阈值（世界单位）：低于它不记撤销步，不留空白历史。</summary>
    private const double DragStartWorld = 1.0;

    private IToolContext? _context;

    /// <summary>沿边画线进行中（落笔时激活了某条边）。</summary>
    private bool _drawing;

    /// <summary>拖板进行中（落笔时按在三角形内部）。</summary>
    private bool _dragging;

    /// <summary>正在拖的板 Id。</summary>
    private string? _dragId;

    /// <summary>拖板期间锁定的旋转与缩放：<see cref="IGfxObjectHost.UpdatePose"/> 三值一起给，避免「只转了一半」的中间态。</summary>
    private double _dragRotation;
    private double _dragScale;

    /// <summary>按下点相对板本地原点（直角顶点）的偏移：拖动时保持它，板才不跳。</summary>
    private Vector _grabOffset;

    /// <summary>按下那一刻的板心（本地原点世界位置）：判定「真的动了」用它。</summary>
    private Point _downCenter;

    /// <summary>撤销步是否已记（只在第一次真动时记一次）。</summary>
    private bool _stepStarted;

    /// <summary>画线起点（已贴边或已吸顶点，世界坐标）。</summary>
    private Point _start;

    /// <summary>激活的边（世界坐标两端）。</summary>
    private Point _edgeFrom;
    private Point _edgeTo;

    private Line? _previewLine;

    public string Id => TriangleTraceToolIds.Trace;

    public string DisplayName => "沿边画线";

    public string ToolTip => "三角板·沿边画线：先用「三角板·45°/60°」放一块板，"
        + "再选本工具 —— 落笔靠近板的任何一条边，就沿着那条边画出笔直的线（可以画出比板更长的延长线）；"
        + "落笔在板内可以拖动板；投影点靠近角顶点时自动吸到顶点。快捷键 9";

    public Key? Shortcut => Key.D9;

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

    /// <remarks>拖动中途被切走也会走到这里。私有状态必须复位（与直尺同一条纪律）。</remarks>
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
                OnDown(context, pointer.World);
                break;

            case ToolPointerPhase.Move:
                if (_drawing) RefreshLine(context, pointer.World);
                else if (_dragging) DragBoard(context, pointer.World);
                break;

            case ToolPointerPhase.Up:
                if (_drawing) EndLine(context, pointer.World);
                else if (_dragging) EndDrag();
                break;
        }
    }

    // ---------------------------------------------------------------- 按下：三分支

    /// <summary>
    /// 落笔分流：靠近边 = 画线；三角形内 = 拖板；其余 = 忽略并提示。
    /// </summary>
    private void OnDown(IToolContext context, Point world)
    {
        var gfx = context.Gfx;
        if (gfx is null)
        {
            context.SetStatus("沿边画线：当前程序不支持图形对象，用不了");
            return;
        }

        var board = FindTopmostBoard(gfx);
        if (board is null)
        {
            context.SetStatus("沿边画线：先用「三角板·45°/60°」放一块板，再切回来沿边画线");
            return;
        }

        var vertices = WorldVertices(board);
        var edges = new[]
        {
            new EdgeSnapHelper.Edge(vertices[0], vertices[1]),   // 长直角边
            new EdgeSnapHelper.Edge(vertices[1], vertices[2]),   // 斜边
            new EdgeSnapHelper.Edge(vertices[2], vertices[0]),   // 短直角边
        };

        // 容差按屏幕像素定，除以缩放换成世界单位 —— 放大缩小后手感不变
        double tolerance = EdgeTolerancePixels / SafeScale(context);
        var nearest = EdgeSnapHelper.NearestEdge(world, edges);

        if (nearest.Index >= 0 && nearest.Distance <= tolerance)
        {
            StartLine(context, world, edges[nearest.Index], vertices);
            return;
        }

        if (EdgeSnapHelper.PointInTriangle(world, vertices[0], vertices[1], vertices[2]))
        {
            StartDrag(gfx, board, world);
            return;
        }

        context.SetStatus("沿边画线：落笔靠近三角板边缘才能画线；在板内按下可拖动板");
    }

    /// <summary>进入画线状态：起点先试顶点吸附（三顶点），不中就贴到边上的最近点。</summary>
    private void StartLine(IToolContext context, Point world, EdgeSnapHelper.Edge edge, Point[] vertices)
    {
        _edgeFrom = edge.From;
        _edgeTo = edge.To;
        _start = EdgeSnapHelper.TrySnapToVertex(world, vertices, VertexSnapToleranceWorld, out var snapped)
            ? snapped
            : EdgeSnapHelper.ClosestPointOnSegment(world, edge.From, edge.To);

        _drawing = true;

        // 上一次若因异常路径没清干净，这里再兜一次 —— 预览层不该有历史
        context.ClearPreview();
        BuildPreview(context);
        RefreshLine(context, world);
    }

    /// <summary>进入拖板状态：记住偏移、锁定旋转缩放，选中它让「选择」的手柄跟上来。</summary>
    private void StartDrag(IGfxObjectHost gfx, IGfxObjectRef board, Point world)
    {
        _dragging = true;
        _dragId = board.Id;
        _dragRotation = board.RotationDegrees;
        _dragScale = board.Scale;
        _grabOffset = world - board.Center;
        _downCenter = board.Center;
        _stepStarted = false;

        gfx.Select(board.Id);
    }

    // ---------------------------------------------------------------- 画线

    /// <summary>当前指针位置对应的线终点：先投影到边所在直线（t 不夹，可出延长线），再试顶点吸附。</summary>
    private Point ProjectEnd(Point world)
    {
        var projected = EdgeSnapHelper.ProjectOntoLine(world, _edgeFrom, _edgeTo, out _);

        // 投影点靠近边的任一端就吸过去 —— 画高线、连角这类精确作图靠它
        return EdgeSnapHelper.TrySnapToVertex(projected, new[] { _edgeFrom, _edgeTo },
                   VertexSnapToleranceWorld, out var snapped)
            ? snapped
            : projected;
    }

    /// <summary>刷新预览线与状态栏读数。</summary>
    private void RefreshLine(IToolContext context, Point world)
    {
        var end = ProjectEnd(world);

        if (_previewLine is not null)
        {
            _previewLine.X1 = _start.X;
            _previewLine.Y1 = _start.Y;
            _previewLine.X2 = end.X;
            _previewLine.Y2 = end.Y;
        }

        context.SetStatus($"沿边画线：{TriangleGeometry.ToCentimeters((end - _start).Length):F1} cm，"
                          + $"倾角 {TriangleGeometry.AxisDegrees(_start, end):F0}°");
    }

    /// <summary>抬笔落墨：终点与预览同一条投影逻辑，太短按误触处理。</summary>
    private void EndLine(IToolContext context, Point world)
    {
        var end = ProjectEnd(world);

        // ★ 起点先读成局部变量：下面 Reset() 会把 _start 归零 ——
        //   与 TriangleTool.Commit 对 _vertex 的处置同一条（量角器踩过的坑）。
        var start = _start;
        double length = (end - start).Length;

        context.ClearPreview();
        Reset();

        if (length < MinDragWorld)
        {
            // 误触：点一下不该在卷面上留一个点（与直尺同一条）
            context.SetStatus("沿边画线：拖动距离太短，未落墨");
            return;
        }

        context.CommitStrokes(new StrokeCollection { MakeLine(context, start, end) });
        context.SetStatus($"沿边画线：{TriangleGeometry.ToCentimeters(length):F1} cm，"
                          + $"倾角 {TriangleGeometry.AxisDegrees(start, end):F0}°");
    }

    /// <summary>把两点连成一条笔画：颜色与笔宽取当前调色板（照直尺的做法）。</summary>
    private static Stroke MakeLine(IToolContext context, Point from, Point to)
    {
        var points = new StylusPointCollection
        {
            new StylusPoint(from.X, from.Y),
            new StylusPoint(to.X, to.Y),
        };

        var attributes = new DrawingAttributes
        {
            Color = context.PenColor,
            Width = context.PenWorldWidth,
            Height = context.PenWorldWidth,

            // 两点直线绝不能被拟合成曲线 —— 沿边画出来必须严格共线
            FitToCurve = false,
            IgnorePressure = true,
        };

        return new Stroke(points, attributes);
    }

    // ---------------------------------------------------------------- 拖板

    /// <summary>拖板跟手：按住点相对板不动，只在第一次真动时记一次撤销步。</summary>
    private void DragBoard(IToolContext context, Point world)
    {
        var gfx = context.Gfx;
        var id = _dragId;
        if (gfx is null || id is null) return;

        var center = world - _grabOffset;

        if (!_stepStarted)
        {
            // 按下没动就不记撤销步：不留「什么都没变」的空白历史
            if ((center - _downCenter).Length < DragStartWorld) return;
            gfx.BeginStep("拖动三角板");
            _stepStarted = true;
        }

        // 旋转与缩放取按下那一刻的锁定值：拖动只改位置，三值一起给避免中间态
        gfx.UpdatePose(id, center, _dragRotation, _dragScale);
    }

    /// <summary>抬笔结束拖板：位姿已经实时生效，这里只复位状态。</summary>
    private void EndDrag() => Reset();

    // ---------------------------------------------------------------- 板查询与坐标换算

    /// <summary>Z 序最上层的三角板对象；画布上没有则 <c>null</c>。</summary>
    private static IGfxObjectRef? FindTopmostBoard(IGfxObjectHost gfx)
    {
        for (int i = gfx.Objects.Count - 1; i >= 0; i--)
        {
            if (gfx.Objects[i].Kind == TriangleRenderer.KindName) return gfx.Objects[i];
        }

        return null;
    }

    /// <summary>
    /// 三角板三个顶点的<b>世界坐标</b>：本地顶点（直角顶点为原点）经 旋转→缩放→平移 上到世界。
    /// </summary>
    /// <remarks>
    /// 位姿换算用 <see cref="Matrix"/> 的前乘语义依次 Rotate→Scale→Translate，
    /// 得到 平移·缩放·旋转 —— 与宿主施加给对象视觉的变换是同一套数学（M15 引脚换算已验证）。
    /// 插件拿不到、也不该拿宿主的 RenderTransform，这里自算一份。
    /// </remarks>
    private static Point[] WorldVertices(IGfxObjectRef board)
    {
        var local = TriangleGeometry.Vertices(TriangleGeometry.KindOf(board), TriangleGeometry.LegOf(board));

        var matrix = Matrix.Identity;
        matrix.Rotate(board.RotationDegrees);
        matrix.Scale(board.Scale, board.Scale);
        matrix.Translate(board.Center.X, board.Center.Y);

        var result = new Point[local.Count];
        for (int i = 0; i < local.Count; i++) result[i] = matrix.Transform(local[i]);
        return result;
    }

    /// <summary>缩放的防御性读取：异常值一律按 1 处理（照直尺文字反缩放的同一套）。</summary>
    private static double SafeScale(IToolContext context)
    {
        var scale = context.Scale;
        return scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale) ? 1.0 : scale;
    }

    // ---------------------------------------------------------------- 预览

    /// <summary>建立预览线。只在按下时做一次，之后每帧只改数值。</summary>
    /// <remarks>
    /// 预览层在 WorldHost 内部 ⇒ 本地坐标就是世界坐标，直接写数字不做换算。
    /// IsHitTestVisible=false：预览绝不能自己去抢输入。
    /// </remarks>
    private void BuildPreview(IToolContext context)
    {
        _previewLine = new Line
        {
            Stroke = new SolidColorBrush(context.PenColor),
            StrokeThickness = context.PenWorldWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };

        context.AddPreview(_previewLine);
    }

    // ---------------------------------------------------------------- 复位

    private void Reset()
    {
        _drawing = false;
        _dragging = false;
        _dragId = null;
        _dragRotation = 0.0;
        _dragScale = 1.0;
        _grabOffset = default;
        _downCenter = default;
        _stepStarted = false;
        _start = default;
        _edgeFrom = default;
        _edgeTo = default;
        _previewLine = null;
    }
}
