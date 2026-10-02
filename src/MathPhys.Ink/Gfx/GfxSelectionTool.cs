using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;
using MathPhys.Ink.Design;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 宿主内部的"我需要在工具切换后重建预览"标记。
/// </summary>
/// <remarks>
/// 存在的理由很具体：宿主是在<b>工具 <c>Activate</c> 之后</b>才清预览层的
/// （<c>ToolRegistry.Activate</c> → <c>ActiveToolChanged</c> → <c>ApplyTool</c> → <c>ClearPreview</c>），
/// 于是"选中框"这种<b>常驻</b>预览会被自己刚建好就被清掉。
/// <para>
/// 不用契约接口，是因为这只是宿主与自己内置工具之间的事 —— 插件工具（创建型）不需要常驻预览，
/// 让所有插件作者都去实现一个空方法是没有收益的复杂度。
/// </para>
/// </remarks>
internal interface IPreviewRefreshingTool
{
    /// <summary>重建常驻预览（宿主在清空预览层之后调用）。</summary>
    void RefreshPreview();
}

/// <summary>
/// 选择工具：选中 / 拖动 / 旋转 / 缩放 / 删除画布上的图形对象。
/// </summary>
/// <remarks>
/// 它是<b>宿主内置</b>的（不是插件），因为它是所有学科工具的公共操作面：
/// 五个 P0 工具各自只需要"算出几何、交一个对象"，拖动旋转缩放删除只有这一份实现。
/// <para>
/// 输入声明与直尺完全一致（<c>UsesInkLayer=false, NeedsPointer=true</c>），
/// 所以它复用 M7.1 已经建好的指针转发链，宿主输入代码一行没改。
/// </para>
/// <para><b>一体机上的两条硬约束</b>（都在这里落实）：</para>
/// <list type="bullet">
/// <item>删除必须有屏幕按钮（框右上角的 ✕）—— 一体机上没有键盘，只做 Del 键等于没这个功能；</item>
/// <item>手柄命中半径按<b>屏幕</b>像素算（12 DIP / 视口缩放），否则缩小之后手指点不中。</item>
/// </list>
/// </remarks>
public sealed class GfxSelectionTool : ITool, IPreviewRefreshingTool
{
    /// <summary>移动的误触阈值（世界单位）：小于它认为只是想"点一下选中"。</summary>
    private const double MinMoveWorld = 2.0;

    /// <summary>旋转的误触阈值（度）。</summary>
    private const double MinRotateDegrees = 1.0;

    /// <summary>缩放的误触阈值（屏幕像素）。</summary>
    private const double MinScalePixels = 3.0;

    private enum DragMode
    {
        None,
        Move,
        Rotate,
        Scale,
    }

    private readonly GfxObjectStore _store;
    private readonly IGfxObjectHost _host;

    private IToolContext? _context;
    private double _viewScale = 1.0;

    private DragMode _mode;
    private string? _activeId;
    private bool _stepBegun;

    private Point _dragStartWorld;
    private Point _startCenter;
    private double _startRotation;
    private double _startScale;
    private double _startGrabAngle;
    private double _startGrabDistance;

    private Rectangle? _frame;
    private Line? _stem;
    private Ellipse? _rotateHandle;
    private Rectangle? _scaleHandle;
    private Border? _deleteButton;

    public GfxSelectionTool(GfxObjectStore store, IGfxObjectHost host)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public string Id => ToolIds.GfxSelect;

    public string DisplayName => "选择";

    public string ToolTip =>
        "选择：点一下选中图形，按住可拖动；框上方的圆点旋转，右下方块缩放，右上角 ✕ 删除。快捷键 V";

    public Key? Shortcut => Key.V;

    public bool UsesInkLayer => false;

    public bool NeedsPointer => true;

    public ToolInputKind InputKind => ToolInputKind.None;

    public ToolInkMode InkMode => ToolInkMode.None;

    public Cursor? Cursor => Cursors.Arrow;

    // ---------------------------------------------------------------- 生命周期

    public void Activate(IToolContext context)
    {
        _context = context;
        _viewScale = SafeScale(context.Scale);

        _store.Changed += OnStoreChanged;
        _store.SelectionChanged += OnStoreChanged;
        context.ViewportChanged += OnViewportChanged;

        ResetDrag();
        RefreshFrame();
    }

    public void Deactivate()
    {
        _store.Changed -= OnStoreChanged;
        _store.SelectionChanged -= OnStoreChanged;

        if (_context is not null) _context.ViewportChanged -= OnViewportChanged;

        ResetDrag();

        // 只丢引用，不清预览层：清预览是宿主的责任（它清得更早也更可靠），
        // 我们在这里再清一次会与"宿主先清、工具后建"的顺序打架。
        _frame = null;
        _stem = null;
        _rotateHandle = null;
        _scaleHandle = null;
        _deleteButton = null;

        _context = null;
    }

    /// <remarks>
    /// 宿主清空预览层之后会调到这里（见 <see cref="IPreviewRefreshingTool"/>），
    /// 于是选中框立刻回来。少了这一步，切到选择工具后要等下一次点击才看得到框。
    /// </remarks>
    public void RefreshPreview() => RefreshFrame();

    // ---------------------------------------------------------------- 指针

    public void OnPointer(ToolPointer pointer)
    {
        switch (pointer.Phase)
        {
            case ToolPointerPhase.Down:
                OnDown(pointer.World);
                break;

            case ToolPointerPhase.Move:
                OnMove(pointer.World);
                break;

            default:
                OnUp();
                break;
        }
    }

    private void OnDown(Point world)
    {
        if (_context is null) return;

        _viewScale = SafeScale(_context.Scale);

        var selected = _store.Selected;
        double radius = GfxTransform.HandleRadiusWorld(_viewScale);

        if (selected is not null)
        {
            // 先判手柄，再判对象本体：手柄在框外侧，两者不重叠，但判断顺序决定了
            // "贴边按"时到底是转还是拖 —— 手柄优先符合直觉。
            if (Near(world, GfxTransform.DeleteButtonWorld(selected, _viewScale), radius))
            {
                _host.BeginStep("删除图形");
                _store.Remove(selected.Id);
                _context.SetStatus($"已删除一个图形（剩 {_store.Count} 个）");
                RefreshFrame();
                return;
            }

            if (Near(world, GfxTransform.RotationHandleWorld(selected, _viewScale), radius))
            {
                StartDrag(DragMode.Rotate, selected, world);
                RefreshFrame();
                return;
            }

            if (Near(world, GfxTransform.ScaleHandleWorld(selected, _viewScale), radius))
            {
                StartDrag(DragMode.Scale, selected, world);
                RefreshFrame();
                return;
            }
        }

        var hit = _store.HitTest(world);

        if (hit is null)
        {
            _store.Select(null);
            _context.SetStatus(_store.Count == 0 ? "画布上还没有图形" : "已取消选中");
            RefreshFrame();
            return;
        }

        // ★ 点中"组的成员" ⇒ 实际选中的是<b>整个组</b>：
        //   老师点一根箭头时，心里想的是"把这堆东西挪一下"，
        //   而不是"只挪这一根"（若想只挪一根，解组就行）。
        var owner = _store.FindGroupOf(hit.Id);
        if (owner is not null)
        {
            if (!ReferenceEquals(owner, selected)) _store.Select(owner.Id);

            StartDrag(DragMode.Move, owner, world);
            int memberCount = _store.CountGroupMembers(owner.Id);
            _context.SetStatus($"矢量组：{memberCount} 根（按住拖动整组一起走）");
            RefreshFrame();
            return;
        }

        // 不要求"先选中再拖"：直接按下即选中并拖动。少一步就少打断一次讲课节奏。
        if (!ReferenceEquals(hit, selected)) _store.Select(hit.Id);

        StartDrag(DragMode.Move, hit, world);
        _context.SetStatus(Describe(hit) + "（按住拖动可移动）");
        RefreshFrame();
    }

    private void OnMove(Point world)
    {
        if (_context is null || _mode == DragMode.None) return;

        var obj = _store.FindObject(_activeId);
        if (obj is null)
        {
            ResetDrag();
            return;
        }

        switch (_mode)
        {
            case DragMode.Move:
            {
                var delta = world - _dragStartWorld;

                if (!_stepBegun)
                {
                    if (delta.Length < MinMoveWorld) return;
                    _host.BeginStep("移动图形");
                    // M12 S7.2：拖动起步即复位平移折算锚点（函数曲线"拖动改参数"用）
                    _store.BeginRebaseDrag(obj.Id);
                    _stepBegun = true;
                }

                _store.UpdatePose(obj.Id, _startCenter + delta, _startRotation, _startScale);
                _context.SetStatus(Describe(obj));
                break;
            }

            case DragMode.Rotate:
            {
                double angle = GrabAngle(world, obj.Center);
                double change = angle - _startGrabAngle;

                if (!_stepBegun)
                {
                    if (Math.Abs(change) < MinRotateDegrees) return;
                    _host.BeginStep("旋转图形");
                    _stepBegun = true;
                }

                _store.UpdatePose(obj.Id, obj.Center, _startRotation + change, _startScale);
                _context.SetStatus($"旋转 {obj.RotationDegrees:F0}°");
                break;
            }

            case DragMode.Scale:
            {
                double distance = (world - obj.Center).Length;
                double pixelsPerWorld = Math.Max(_viewScale, 1e-6);
                double pixelDelta = Math.Abs(distance - _startGrabDistance) * pixelsPerWorld;

                if (!_stepBegun)
                {
                    if (pixelDelta < MinScalePixels) return;
                    _host.BeginStep("缩放图形");
                    _stepBegun = true;
                }

                double factor = distance / Math.Max(_startGrabDistance, 1e-6);
                _store.UpdatePose(obj.Id, obj.Center, _startRotation, _startScale * factor);
                _context.SetStatus($"缩放 {obj.Scale * 100:F0}%");
                break;
            }
        }
    }

    private void OnUp()
    {
        if (_mode == DragMode.None) return;

        var obj = _store.FindObject(_activeId);

        ResetDrag();

        if (_context is not null && obj is not null) _context.SetStatus(Describe(obj));

        RefreshFrame();
    }

    // ---------------------------------------------------------------- 拖动状态

    private void StartDrag(DragMode mode, Plugins.IGfxObjectRef obj, Point world)
    {
        _mode = mode;
        _activeId = obj.Id;
        _stepBegun = false;

        _dragStartWorld = world;
        _startCenter = obj.Center;
        _startRotation = obj.RotationDegrees;
        _startScale = obj.Scale;
        _startGrabAngle = GrabAngle(world, obj.Center);
        _startGrabDistance = (world - obj.Center).Length;
    }

    private void ResetDrag()
    {
        _mode = DragMode.None;
        _activeId = null;
        _stepBegun = false;
    }

    /// <summary>抓取点相对对象中心的角度（度）。</summary>
    private static double GrabAngle(Point from, Point center)
    {
        var v = from - center;
        if (v.Length < 1e-9) return 0;
        return Math.Atan2(v.Y, v.X) * 180.0 / Math.PI;
    }

    private static bool Near(Point a, Point b, double radius) => (a - b).Length <= radius;

    private static double SafeScale(double scale)
        => scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale) ? 1.0 : scale;

    /// <summary>
    /// 给状态栏用的一句话：位置 / 旋转 / 缩放。
    /// </summary>
    /// <remarks>
    /// 对象若自带名字（<see cref="WebPanelImageRenderer.LabelKey"/>，由插件的
    /// <c>AddImage</c> 写进来，如「单摆仿真」）就<b>念名字</b>，否则退回念 Kind。
    /// 理由：选中一张仿真截图时，"图形 webPanelImage" 对老师毫无信息量，
    /// 而"图形 单摆仿真"一眼就知道选中的是哪个。
    /// </remarks>
    private static string Describe(Plugins.IGfxObjectRef obj)
    {
        string label = WebPanelImageRenderer.LabelOf(obj);
        string name = label.Length > 0 ? label : obj.Kind;

        return $"图形 {name}：位置 ({obj.Center.X:F0},{obj.Center.Y:F0})，"
               + $"旋转 {obj.RotationDegrees:F0}°，缩放 {obj.Scale * 100:F0}%";
    }

    private void OnStoreChanged(object? sender, EventArgs e) => RefreshFrame();

    private void OnViewportChanged(object? sender, EventArgs e)
    {
        if (_context is null) return;

        _viewScale = SafeScale(_context.Scale);
        RefreshFrame();
    }

    // ---------------------------------------------------------------- 选中框（预览）

    /// <summary>
    /// 按当前选中项重建选中框与手柄。
    /// </summary>
    /// <remarks>
    /// 全部图元 <c>IsHitTestVisible=false</c>：它们挂在墨迹层上面，
    /// 一旦参与命中测试，用户就会"看着选中框在那儿，却写不出字"（M7.3 的教训）。
    /// <para>
    /// 命中判定用 <see cref="GfxTransform"/> 的数学算，<b>不</b>依赖这些元素 ——
    /// 于是同一套逻辑在没有窗口的 harness 里也能断言。
    /// </para>
    /// </remarks>
    private void RefreshFrame()
    {
        var context = _context;
        var obj = _store.Selected;

        if (context is null) return;

        if (obj is null)
        {
            HideFrame();
            return;
        }

        EnsureVisuals(context);

        if (_frame is null || _rotateHandle is null || _scaleHandle is null || _deleteButton is null || _stem is null)
        {
            return;
        }

        double scale = SafeScale(_viewScale);
        double objectScale = Math.Max(Math.Abs(obj.Scale), 1e-6);

        // ---- 对象框：与对象本体同一套位姿（尺寸给未缩放值，缩放由矩阵施加），
        //      于是框永远精确贴合对象，包括旋转与缩放之后。
        _frame.Width = obj.LocalSize.Width;
        _frame.Height = obj.LocalSize.Height;
        _frame.RenderTransformOrigin = new Point(0.5, 0.5);
        _frame.RenderTransform = new MatrixTransform(GfxTransform.PoseMatrix(obj));

        // 描边要看起来是 1 个屏幕像素：它会被"对象缩放 × 视口缩放"两级放大，两级都要除掉
        _frame.StrokeThickness = 1.0 / (scale * objectScale);

        Canvas.SetLeft(_frame, obj.Center.X - obj.LocalSize.Width / 2.0);
        Canvas.SetTop(_frame, obj.Center.Y - obj.LocalSize.Height / 2.0);

        // ---- 旋转手柄的连杆：从框顶边中点拉到手柄中心
        var rotateWorld = GfxTransform.RotationHandleWorld(obj, scale);
        var topWorld = GfxTransform.ToWorld(obj, new Point(0, -obj.LocalSize.Height / 2.0));
        _stem.X1 = topWorld.X;
        _stem.Y1 = topWorld.Y;
        _stem.X2 = rotateWorld.X;
        _stem.Y2 = rotateWorld.Y;
        _stem.StrokeThickness = 1.0 / scale;

        PlaceFixed(rotateWorld, GfxTransform.HandleSizePixels, scale, _rotateHandle);
        PlaceFixed(GfxTransform.ScaleHandleWorld(obj, scale), GfxTransform.HandleSizePixels, scale, _scaleHandle);
        PlaceFixed(GfxTransform.DeleteButtonWorld(obj, scale), DeleteButtonPixels, scale, _deleteButton);
    }

    private const double DeleteButtonPixels = 22.0;

    /// <summary>把"屏幕尺寸恒定"的图元摆到某个世界点。</summary>
    /// <remarks>
    /// 位置按世界坐标走、尺寸按屏幕像素走，靠的是 <c>LayoutTransform</c> 反向缩放：
    /// 元素占据的<b>布局</b>尺寸变成 <c>像素 / 视口缩放</c>，渲染出来又乘回去 ⇒ 屏幕上恒为 N 像素。
    /// 与 M7.3 直尺长度文字用的是同一个手法。
    /// </remarks>
    private static void PlaceFixed(Point world, double pixels, double viewScale, FrameworkElement element)
    {
        double k = 1.0 / Math.Max(viewScale, 1e-6);

        element.Width = pixels;
        element.Height = pixels;
        element.LayoutTransform = new ScaleTransform(k, k);

        double half = pixels * k / 2.0;
        Canvas.SetLeft(element, world.X - half);
        Canvas.SetTop(element, world.Y - half);
    }

    /// <summary>建图元并挂到预览层（切工具后预览层被清空时会重新走这里）。</summary>
    private void EnsureVisuals(IToolContext context)
    {
        if (_frame is not null && _frame.Parent is null)
        {
            // 元素还在（我们握着引用），只是被宿主从预览层摘掉了 ⇒ 直接挂回去，不重建
            context.AddPreview(_frame);
            if (_stem is not null) context.AddPreview(_stem);
            if (_rotateHandle is not null) context.AddPreview(_rotateHandle);
            if (_scaleHandle is not null) context.AddPreview(_scaleHandle);
            if (_deleteButton is not null) context.AddPreview(_deleteButton);
            return;
        }

        if (_frame is not null) return;

        var frameBrush = Tokens.Brush("Canvas.SelectionStroke");

        _frame = new Rectangle
        {
            Stroke = frameBrush,
            StrokeDashArray = new DoubleCollection { 6, 4 },
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };

        _stem = new Line
        {
            Stroke = frameBrush,
            IsHitTestVisible = false,
        };

        _rotateHandle = new Ellipse
        {
            Fill = Tokens.Brush("Canvas.SelectionFill"),
            Stroke = frameBrush,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };

        _scaleHandle = new Rectangle
        {
            Fill = Tokens.Brush("Canvas.SelectionFill"),
            Stroke = frameBrush,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };

        _deleteButton = new Border
        {
            Background = Tokens.Brush("Canvas.SelectionFill"),
            BorderBrush = Tokens.Brush("Canvas.DeleteStroke"),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(4),
            Child = BuildCross(),
            IsHitTestVisible = false,
        };

        context.AddPreview(_frame);
        context.AddPreview(_stem);
        context.AddPreview(_rotateHandle);
        context.AddPreview(_scaleHandle);
        context.AddPreview(_deleteButton);
    }

    /// <summary>
    /// 删除按钮里的那个 ✕。
    /// </summary>
    /// <remarks>
    /// 用两条线自己画，不用 "✕" 字符：字符要靠字体回退，
    /// 万一这台机器上没有哪个字体带 U+2715，屏幕上就是一个方框 —— 而这是唯一的删除入口。
    /// </remarks>
    private static Canvas BuildCross()
    {
        var brush = Tokens.Brush("Canvas.DeleteStroke");

        var cross = new Canvas
        {
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        cross.Children.Add(new Line { X1 = 2, Y1 = 2, X2 = 10, Y2 = 10, Stroke = brush, StrokeThickness = 2, IsHitTestVisible = false });
        cross.Children.Add(new Line { X1 = 10, Y1 = 2, X2 = 2, Y2 = 10, Stroke = brush, StrokeThickness = 2, IsHitTestVisible = false });

        return cross;
    }

    private void HideFrame()
    {
        if (_frame is null) return;

        foreach (var element in new FrameworkElement?[] { _frame, _stem, _rotateHandle, _scaleHandle, _deleteButton })
        {
            if (element?.Parent is Panel panel) panel.Children.Remove(element);
        }
    }
}
