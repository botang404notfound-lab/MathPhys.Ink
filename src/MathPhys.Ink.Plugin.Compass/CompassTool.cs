using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Compass;

/// <summary>圆规的两种工作形态。</summary>
public enum CompassMode
{
    /// <summary>两点式整圆：针尖 + 笔尖。</summary>
    Circle,

    /// <summary>任意夹角圆弧：针尖 + 半径 + 圆周上两个起止角控制点。</summary>
    Arc,
}

/// <summary>本插件注册的工具 Id。</summary>
public static class CompassToolIds
{
    /// <summary>整圆模式。快捷键 R。</summary>
    public const string Circle = "compass-circle";

    /// <summary>圆弧模式。快捷键 A。</summary>
    public const string Arc = "compass-arc";
}

/// <summary>拖动中的哪一个控制点。</summary>
internal enum CompassHandle
{
    None,

    /// <summary>针尖（圆心）：拖它把整圆 / 圆弧平移。</summary>
    Center,

    /// <summary>笔尖（半径）：拖它改半径。</summary>
    Radius,

    /// <summary>圆弧起点角（仅圆弧模式）。</summary>
    Start,

    /// <summary>圆弧终点角（仅圆弧模式）。</summary>
    End,
}

/// <summary>
/// 圆规：两点式整圆 / 任意夹角圆弧。
/// </summary>
/// <remarks>
/// 与量角器最大的不同：它<b>不实现 <see cref="IGfxTool"/></b>。
/// 落成对象后工具必须留在原地（下一步十有八九是拖控制点微调），
/// 一旦标记成创建型，宿主会在 <c>Add</c> 之后自动切回「选择」工具，手柄当场消失。
/// <para>
/// 交互分两段：第一次按下放针尖（可吸附卷面上的点或线），移动预览半径，
/// 第二次按下落成对象；落成后四个控制点（圆心 / 半径 / 起角 / 终角）由本工具自己画
/// 在预览层里、自己命中，拖动只改对象的依赖属性，<b>不重建视觉树</b>。
/// </para>
/// <para>
/// Esc（<see cref="ICancellableTool"/>）：放置中 / 拖动中有状态，取消当前一步并留在工具里；
/// 空闲时返回 <c>false</c>，宿主切回激活本工具之前的工具。
/// </para>
/// </remarks>
public sealed class CompassTool : ITool, ICancellableTool
{
    /// <summary>放置阶段圆预览的画笔透明度。</summary>
    private const byte PreviewStrokeAlpha = 0x66;

    /// <summary>角度手柄"真的动了"的阈值（度）：低于它只当手抖，不开撤销步。</summary>
    private const double AngleDragMinDegrees = 0.5;

    private readonly CompassMode _mode;

    private IToolContext? _context;

    // ---------------------------------------------------------------- 放置阶段

    /// <summary>针尖已放、对象未落。</summary>
    private bool _placing;

    private bool _snapped;

    /// <summary>针尖（圆心）世界坐标。</summary>
    private Point _center;

    private double _radius = GeometryHelper.MinRadiusWorld;

    /// <summary>整圆模式下笔尖手柄的本地角（屏幕视觉角）；拖半径时不改它。</summary>
    private double _radiusDirectionDegrees;

    /// <summary>开始放新圆前的笔尖方向快照（Esc 丢弃预览时还回去，旧对象的手柄不会跳位）。</summary>
    private double _radiusDirectionSnapshot;

    // ---------------------------------------------------------------- 已落成的对象

    private string? _objectId;

    /// <summary>圆弧起止角（本地屏幕视觉角）。整圆模式恒 0 / 0，不起作用。</summary>
    private double _startDegrees;

    private double _endDegrees;

    // ---------------------------------------------------------------- 拖动

    private bool _dragging;

    private CompassHandle _dragHandle = CompassHandle.None;

    /// <summary>这次连续拖动是否已开过撤销步（一次操作只开一步）。</summary>
    private bool _stepOpened;

    // 拖动起点快照：Esc 中途取消要原样还回去
    private Point _snapshotCenter;

    private double _snapshotRadius;

    private double _snapshotStart;

    private double _snapshotEnd;

    // ---------------------------------------------------------------- 预览视觉
    // 整个工具生命周期只建一次；拖动 / 缩放只改依赖属性，绝不重建。

    private Canvas? _previewRoot;

    private Path? _placingCircle;

    private Rectangle? _centerThumb;

    private Rectangle? _radiusThumb;

    private Rectangle? _startThumb;

    private Rectangle? _endThumb;

    private Brush? _thumbInk;

    private Brush? _thumbPaper;

    public CompassTool(CompassMode mode) => _mode = mode;

    public string Id => _mode == CompassMode.Circle ? CompassToolIds.Circle : CompassToolIds.Arc;

    public string DisplayName => _mode == CompassMode.Circle ? "圆规" : "圆弧";

    public string ToolTip => _mode == CompassMode.Circle
        ? "圆规：点一下放针尖（会吸到卷面上的点或线），移动预览半径，再点一下落成圆；"
        + "落成后可拖针尖移圆、拖笔尖调半径。快捷键 R"
        : "圆弧：点一下放针尖，移动预览半径，再点一下落成 90° 圆弧；"
        + "落成后拖圆周上的两个控制点改起止角，逆时针绘制。快捷键 A";

    public Key? Shortcut => _mode == CompassMode.Circle ? Key.R : Key.A;

    public bool UsesInkLayer => false;

    public bool NeedsPointer => true;

    public ToolInputKind InputKind => ToolInputKind.None;

    public ToolInkMode InkMode => ToolInkMode.None;

    public Cursor? Cursor => Cursors.None;

    public void Activate(IToolContext context)
    {
        // 事件不允许 `?.` 访问（CS0079）：退订旧上下文必须显式判空
        if (_context is not null) _context.ViewportChanged -= OnViewportChanged;
        _context = context;
        Reset();
        context.ViewportChanged += OnViewportChanged;
    }

    /// <remarks>
    /// 拖动中被切走也会走到这里：宿主虽然会清预览层，但"上一个圆心 / 对象 Id"是我们自己的
    /// 状态，不复位就会表现为"切回来一按，手柄从上次那个对象上冒出来"。
    /// </remarks>
    public void Deactivate()
    {
        if (_context is not null) _context.ViewportChanged -= OnViewportChanged;
        Reset();
        _context?.ClearPreview();
        _context = null;
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
                OnMove(context, pointer.World);
                break;

            case ToolPointerPhase.Up:
                OnUp(context);
                break;
        }
    }

    // ---------------------------------------------------------------- 按下

    private void OnDown(IToolContext context, Point world)
    {
        // 第二次按下 = 落成（放置阶段手柄都收着，不存在"点在手柄上"的歧义）
        if (_placing)
        {
            Commit(context);
            return;
        }

        // 已有对象：先试手柄，拖得中就拖；拖不中说明老师想在空白处画下一个
        if (_objectId is not null && HitHandle(context, world, out var handle))
        {
            StartDrag(handle, world);
            return;
        }

        StartPlacing(context, world);
    }

    private void StartPlacing(IToolContext context, Point world)
    {
        _placing = true;
        _center = GeometryHelper.ResolveCenter(context, world, out _snapped);
        _radius = GeometryHelper.MinRadiusWorld;
        _radiusDirectionSnapshot = _radiusDirectionDegrees;
        _radiusDirectionDegrees = 0.0;

        EnsurePreview(context);

        if (_placingCircle is { } circle)
        {
            circle.Visibility = Visibility.Visible;
            circle.Data = GeometryHelper.BuildCircleGeometry(_radius);
            Canvas.SetLeft(circle, _center.X);
            Canvas.SetTop(circle, _center.Y);
        }

        SetAllThumbsVisible(false);
        context.SetStatus(GeometryHelper.DescribePlacing(_mode, _radius, _snapped));
    }

    // ---------------------------------------------------------------- 移动

    private void OnMove(IToolContext context, Point world)
    {
        if (_placing)
        {
            UpdateRadius(context, world);
            return;
        }

        if (_dragging) DragTo(context, world);
    }

    private void UpdateRadius(IToolContext context, Point world)
    {
        double drag = (world - _center).Length;
        _radius = GeometryHelper.ClampRadius(drag);

        // 拖出可见距离才更新笔尖方向：别把 0° 的噪声当方向
        if (drag >= GeometryHelper.MinDragWorld)
        {
            _radiusDirectionDegrees = GeometryHelper.ScreenAngle(_center, world);
        }

        if (_placingCircle is { } circle)
        {
            circle.Data = GeometryHelper.BuildCircleGeometry(_radius);
        }

        context.SetStatus(GeometryHelper.DescribePlacing(_mode, _radius, _snapped));
    }

    // ---------------------------------------------------------------- 抬起

    private void OnUp(IToolContext context)
    {
        if (!_dragging) return;

        _dragging = false;
        _dragHandle = CompassHandle.None;
        _stepOpened = false;

        context.SetStatus(GeometryHelper.DescribeEditing(_mode, _radius, _startDegrees, _endDegrees));
    }

    // ---------------------------------------------------------------- 落成

    private void Commit(IToolContext context)
    {
        // 先把状态读成局部变量再动字段 —— 顺序反了就是"用默认值落一个对象"
        var center = _center;
        double radius = _radius;

        var gfx = context.Gfx;
        if (gfx is null)
        {
            CancelPlacing(context);
            context.SetStatus("圆规：当前程序不支持图形对象，圆规放不下");
            return;
        }

        var numbers = new Dictionary<string, double> { [GeometryHelper.RadiusKey] = radius };
        if (_mode == CompassMode.Arc)
        {
            numbers[GeometryHelper.StartKey] = 0.0;
            numbers[GeometryHelper.EndKey] = 90.0;
        }

        _objectId = gfx.Add(new GfxDraft
        {
            Kind = _mode == CompassMode.Circle
                ? CompassCircleRenderer.KindName
                : CompassArcRenderer.KindName,
            Center = center,
            RotationDegrees = 0.0,
            Scale = 1.0,
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,
            Numbers = numbers,
        });

        _placing = false;
        _startDegrees = 0.0;
        _endDegrees = _mode == CompassMode.Arc ? 90.0 : 0.0;

        if (_placingCircle is { } circle)
        {
            circle.Visibility = Visibility.Collapsed;
        }

        SetAllThumbsVisible(true);
        PositionThumbs(context);
        context.SetStatus(GeometryHelper.DescribeEditing(_mode, radius, _startDegrees, _endDegrees));
    }

    // ---------------------------------------------------------------- 拖动

    private void StartDrag(CompassHandle handle, Point world)
    {
        _dragging = true;
        _dragHandle = handle;
        _stepOpened = false;

        _snapshotCenter = _center;
        _snapshotRadius = _radius;
        _snapshotStart = _startDegrees;
        _snapshotEnd = _endDegrees;
    }

    private void DragTo(IToolContext context, Point world)
    {
        var gfx = context.Gfx;
        if (gfx is null || _objectId is null) return;

        string id = _objectId;

        switch (_dragHandle)
        {
            case CompassHandle.Center:
                OpenStepOnFirstRealMove((world - _snapshotCenter).Length, GeometryHelper.MinDragWorld,
                                        gfx, "拖动圆规圆心");
                if (!_stepOpened) return;

                _center = world;
                gfx.UpdatePose(id, world, 0.0, 1.0);
                PositionThumbs(context);
                break;

            case CompassHandle.Radius:
                {
                    double radius = GeometryHelper.ClampRadius((world - _center).Length);
                    OpenStepOnFirstRealMove(Math.Abs(radius - _snapshotRadius), GeometryHelper.MinDragWorld,
                                            gfx, "调整圆规半径");
                    if (!_stepOpened) return;

                    _radius = radius;
                    gfx.UpdateNumbers(id, new Dictionary<string, double>
                    {
                        [GeometryHelper.RadiusKey] = radius,
                    });
                    PositionThumbs(context);
                    break;
                }

            case CompassHandle.Start:
                DragAngle(context, gfx, id, world, isStart: true);
                break;

            case CompassHandle.End:
                DragAngle(context, gfx, id, world, isStart: false);
                break;
        }

        context.SetStatus(GeometryHelper.DescribeEditing(_mode, _radius, _startDegrees, _endDegrees));
    }

    /// <summary>拖动角度手柄：世界方向换算成屏幕视觉角，吸 15° 整数倍后写回起 / 止角。</summary>
    private void DragAngle(IToolContext context, IGfxObjectHost gfx, string id, Point world, bool isStart)
    {
        double angle = GeometryHelper.ScreenAngle(_center, world);
        GeometryHelper.TrySnapAngle(angle, out angle);

        double snapshot = isStart ? _snapshotStart : _snapshotEnd;
        OpenStepOnFirstRealMove(
            GeometryHelper.AngleDelta(angle, snapshot),
            AngleDragMinDegrees,
            gfx,
            isStart ? "调整圆弧起点角" : "调整圆弧终点角");
        if (!_stepOpened) return;

        gfx.UpdateNumbers(id, new Dictionary<string, double>
        {
            [isStart ? GeometryHelper.StartKey : GeometryHelper.EndKey] = angle,
        });

        if (isStart) _startDegrees = angle;
        else _endDegrees = angle;

        PositionThumbs(context);
    }

    /// <summary>第一次"真的动了"才开撤销步：按下没动就开步会在历史里留一个空白步。</summary>
    private void OpenStepOnFirstRealMove(double delta, double threshold, IGfxObjectHost gfx, string label)
    {
        if (_stepOpened || delta < threshold)
        {
            return;
        }

        _stepOpened = true;
        gfx.BeginStep(label);
    }

    // ---------------------------------------------------------------- 取消（Esc）

    /// <inheritdoc cref="ICancellableTool.TryCancel" />
    public bool TryCancel()
    {
        var context = _context;

        // 拖动中：按快照原样还回去，工具留在原地
        if (_dragging && context is { Gfx: { } gfx } && _objectId is { } id)
        {
            switch (_dragHandle)
            {
                case CompassHandle.Center:
                    gfx.UpdatePose(id, _snapshotCenter, 0.0, 1.0);
                    _center = _snapshotCenter;
                    break;

                case CompassHandle.Radius:
                    gfx.UpdateNumbers(id, new Dictionary<string, double>
                    {
                        [GeometryHelper.RadiusKey] = _snapshotRadius,
                    });
                    _radius = _snapshotRadius;
                    break;

                case CompassHandle.Start:
                    gfx.UpdateNumbers(id, new Dictionary<string, double>
                    {
                        [GeometryHelper.StartKey] = _snapshotStart,
                    });
                    _startDegrees = _snapshotStart;
                    break;

                case CompassHandle.End:
                    gfx.UpdateNumbers(id, new Dictionary<string, double>
                    {
                        [GeometryHelper.EndKey] = _snapshotEnd,
                    });
                    _endDegrees = _snapshotEnd;
                    break;
            }

            _dragging = false;
            _dragHandle = CompassHandle.None;
            _stepOpened = false;
            PositionThumbs(context);
            context.SetStatus(GeometryHelper.DescribeEditing(_mode, _radius, _startDegrees, _endDegrees));
            return true;
        }

        // 放置中：丢弃半截预览，留在工具里可以原地重画
        if (_placing)
        {
            CancelPlacing(context);
            return true;
        }

        // 空闲：没有可取消的状态，交给宿主切回上一个工具
        return false;
    }

    private void CancelPlacing(IToolContext? context)
    {
        _placing = false;
        _radiusDirectionDegrees = _radiusDirectionSnapshot;

        // 丢掉的是「放新圆的预览」：针尖 / 半径这几个字段已被 StartPlacing 改写成新预览的值，
        // 必须从已落成的对象上读回来 —— 对象才是真相，工具字段只是它的一份镜像。
        // 漏掉这一步的表现：画布上那个圆的手柄跳到大半截预览的位置，按回针尖那儿反而抓不住。
        RestoreFromObject(context);

        if (_placingCircle is { } circle)
        {
            circle.Visibility = Visibility.Collapsed;
        }

        // 取消的是"放新圆的预览"：画布上若还有之前落成的对象，把手柄重新亮出来；
        // 一个对象都没有时才全部收起。
        if (_objectId is not null)
        {
            SetAllThumbsVisible(true);
            if (context is not null) PositionThumbs(context);
        }
        else
        {
            SetAllThumbsVisible(false);
        }

        context?.SetStatus("已取消");
    }

    /// <summary>把已落成对象的位姿读回工具字段（没有对象 / 没有图形层时什么也不做）。</summary>
    private void RestoreFromObject(IToolContext? context)
    {
        var gfx = context?.Gfx;
        if (_objectId is null || gfx is null) return;

        string id = _objectId;
        foreach (var obj in gfx.Objects)
        {
            if (!string.Equals(obj.Id, id, StringComparison.Ordinal)) continue;

            _center = obj.Center;
            _radius = GeometryHelper.RadiusOf(obj);
            _startDegrees = GeometryHelper.StartOf(obj);
            _endDegrees = _mode == CompassMode.Arc ? GeometryHelper.EndOf(obj) : 0.0;
            return;
        }
    }

    // ---------------------------------------------------------------- 手柄

    /// <summary>按世界坐标命中手柄；拖得中返回对应的手柄，拖不中返回 <c>None</c>。</summary>
    private bool HitHandle(IToolContext context, Point world, out CompassHandle handle)
    {
        // 容差 = 屏幕像素 ÷ 当前缩放（缩放越大，世界容差越小 —— M19 边界条件）
        double tolerance = GeometryHelper.HitTolerance(context.Scale);

        // 命中优先级：圆心最优先；起 / 止角在小弧上会互相靠近，起点在前
        if ((HandleWorld(CompassHandle.Center) - world).Length <= tolerance)
        {
            handle = CompassHandle.Center;
            return true;
        }

        if (_mode == CompassMode.Arc)
        {
            if ((HandleWorld(CompassHandle.Start) - world).Length <= tolerance)
            {
                handle = CompassHandle.Start;
                return true;
            }

            if ((HandleWorld(CompassHandle.End) - world).Length <= tolerance)
            {
                handle = CompassHandle.End;
                return true;
            }
        }

        if ((HandleWorld(CompassHandle.Radius) - world).Length <= tolerance)
        {
            handle = CompassHandle.Radius;
            return true;
        }

        handle = CompassHandle.None;
        return false;
    }

    /// <summary>手柄的世界坐标（圆心 + 本地极坐标点）。</summary>
    /// <remarks>本工具只编辑自己刚落成的对象：旋转 / 缩放恒为 0 / 1，不需要乘位姿矩阵。</remarks>
    private Point HandleWorld(CompassHandle handle)
    {
        double angle = handle switch
        {
            CompassHandle.Radius => _mode == CompassMode.Circle
                ? _radiusDirectionDegrees
                : RadiusHandleAngle(),
            CompassHandle.Start => _startDegrees,
            CompassHandle.End => _endDegrees,
            _ => 0.0,
        };

        if (handle == CompassHandle.Center)
        {
            return _center;
        }

        // Point + Point 不可用：把本地极坐标点拆成 X/Y 再平移到圆心
        var local = GeometryHelper.LocalPoint(_radius, angle);
        return new Point(_center.X + local.X, _center.Y + local.Y);
    }

    /// <summary>圆弧模式笔尖手柄的角 = 弧中分角；起止角相等（整圆）时放在正上方。</summary>
    private double RadiusHandleAngle()
        => GeometryHelper.AnglesEqual(_startDegrees, _endDegrees)
            ? 90.0
            : GeometryHelper.Normalize360(_startDegrees + GeometryHelper.Sweep(_startDegrees, _endDegrees) / 2.0);

    private void PositionThumbs(IToolContext context)
    {
        PlaceThumb(context, _centerThumb, HandleWorld(CompassHandle.Center));
        PlaceThumb(context, _radiusThumb, HandleWorld(CompassHandle.Radius));

        if (_mode == CompassMode.Arc)
        {
            PlaceThumb(context, _startThumb, HandleWorld(CompassHandle.Start));
            PlaceThumb(context, _endThumb, HandleWorld(CompassHandle.End));
        }
    }

    private void PlaceThumb(IToolContext context, Rectangle? thumb, Point world)
    {
        if (thumb is null) return;

        double size = GeometryHelper.ThumbWorldSize(context.Scale);
        thumb.Width = size;
        thumb.Height = size;
        thumb.StrokeThickness = 1.5 / GeometryHelper.SafeScale(context.Scale);
        Canvas.SetLeft(thumb, world.X - size / 2.0);
        Canvas.SetTop(thumb, world.Y - size / 2.0);
    }

    private void SetAllThumbsVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (_centerThumb is { } center) center.Visibility = visibility;
        if (_radiusThumb is { } radius) radius.Visibility = visibility;

        // 整圆模式只有两个控制点；起 / 止角手柄永远不出现
        if (_mode == CompassMode.Arc)
        {
            if (_startThumb is { } start) start.Visibility = visibility;
            if (_endThumb is { } end) end.Visibility = visibility;
        }
    }

    // ---------------------------------------------------------------- 预览构建

    private void EnsurePreview(IToolContext context)
    {
        if (_previewRoot is not null) return;

        _thumbInk = new SolidColorBrush(context.PenColor);
        _thumbPaper = Brushes.White;

        _previewRoot = new Canvas { IsHitTestVisible = false };

        _placingCircle = new Path
        {
            Stroke = new SolidColorBrush(context.PenColor) { Opacity = PreviewStrokeAlpha / 255.0 },
            StrokeThickness = context.PenWorldWidth,
            Stretch = Stretch.None,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _previewRoot.Children.Add(_placingCircle);

        // 圆心 / 终点空心、半径 / 起点实心：一眼分清哪只手柄改什么
        _centerThumb = MakeThumb(fill: _thumbPaper, stroke: _thumbInk);
        _radiusThumb = MakeThumb(fill: _thumbInk, stroke: null);
        _startThumb = MakeThumb(fill: _thumbInk, stroke: null);
        _endThumb = MakeThumb(fill: _thumbPaper, stroke: _thumbInk);

        _previewRoot.Children.Add(_centerThumb);
        _previewRoot.Children.Add(_radiusThumb);
        _previewRoot.Children.Add(_startThumb);
        _previewRoot.Children.Add(_endThumb);

        context.AddPreview(_previewRoot);
    }

    private static Rectangle MakeThumb(Brush? fill, Brush? stroke)
    {
        var thumb = new Rectangle
        {
            Fill = fill,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        if (stroke is not null) thumb.Stroke = stroke;
        return thumb;
    }

    // ---------------------------------------------------------------- 缩放

    /// <summary>
    /// 视口缩放 / 平移后重摆手柄：位置是世界坐标（预览层变换自动跟进，不用改），
    /// 只有"屏幕尺寸恒定"的边长与描边要按新缩放重算。
    /// </summary>
    private void OnViewportChanged(object? sender, EventArgs e)
    {
        if (_context is null || _previewRoot is null) return;
        PositionThumbs(_context);
    }

    // ---------------------------------------------------------------- 复位

    private void Reset()
    {
        _placing = false;
        _snapped = false;
        _center = default;
        _radius = GeometryHelper.MinRadiusWorld;
        _radiusDirectionDegrees = 0.0;
        _objectId = null;
        _startDegrees = 0.0;
        _endDegrees = 0.0;
        _dragging = false;
        _dragHandle = CompassHandle.None;
        _stepOpened = false;
        _previewRoot = null;
        _placingCircle = null;
        _centerThumb = null;
        _radiusThumb = null;
        _startThumb = null;
        _endThumb = null;
        _thumbInk = null;
        _thumbPaper = null;
    }
}
