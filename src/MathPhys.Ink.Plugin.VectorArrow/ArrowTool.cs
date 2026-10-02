using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.VectorArrow;

/// <summary>本插件注册的工具 Id。</summary>
public static class ArrowToolIds
{
    /// <summary>矢量箭头。快捷键 N（0-9 已被前面几个工具分完）。</summary>
    public const string Id = "vector";
}

/// <summary>
/// 矢量箭头（创建型）：按下定尾 → 拖动定头（<b>角度吸 15°、容差 5°</b>）→ 抬笔落成一个矢量对象。
/// </summary>
/// <remarks>
/// 与坐标系/函数图像同一条链路：<see cref="IGfxTool"/>（创建型）、<see cref="ArrowRenderer"/>（渲染）、
/// <c>Texts["bindTo"]</c>（跟随坐标系）。<b>宿主与契约零改动</b>。
/// <para>
/// <b>本地几何按"数学单位"画</b>（本地长 = 世界长 ÷ unitWorld，见 <see cref="ArrowRenderer.UnitOf"/>）：
/// 这样直接复用宿主对绑定对象的跟随公式 <c>Scale = unitWorld × 坐标系.Scale</c>，
/// 跟随与不跟随两种状态下"画出来的世界长度"都等于拖动长度。反例（本地几何直接当世界长）
/// 会让跟随坐标系时的箭头被放大 28 倍 —— 实测过，是真的。
/// </para>
/// <para>
/// ★ <b>箭头旁不画读数文字</b>（2026-09-22 用户要求）：预览里只有箭杆 + 箭头，
/// 数值走状态栏（拖动中实时更新、落下时再报一句）。省下来的卷面留给题目本身。
/// </para>
/// </remarks>
public sealed class ArrowTool : ITool, IGfxTool
{
    private const string SysKind = "coordsystem";
    private const string SysUnitKey = "unitWorld";

    // ---------------------------------------------------------------- 绑定键（沿用宿主约定）

    /// <summary>绑定目标对象 Id（与宿主 <c>GfxObjectStore.BindKey</c> 同值）。</summary>
    public const string BindToKey = "bindTo";

    /// <summary>相对目标原点的偏移（目标本地系、世界长度）。</summary>
    public const string BindRelXKey = "bindRelX";
    public const string BindRelYKey = "bindRelY";

    /// <summary>相对目标的额外旋转（度）。</summary>
    public const string BindRelRotKey = "bindRelRot";

    /// <summary>冗余保存的单位长（目标被删后仍能自己算）。</summary>
    public const string UnitWorldKey = "unitWorld";

    private IToolContext? _context;

    private bool _drawing;
    private Point _tail;

    // 预览图元：箭杆 + 箭头（2 个）。**不建读数标签** —— 数值走状态栏。
    private Line? _shaft;
    private Polygon? _head;

    public string Id => ArrowToolIds.Id;
    public string DisplayName => "矢量箭头";
    public string ToolTip
        => "矢量箭头：按下定尾、往外拖出力的方向与大小（角度吸附到 15° 的倍数，差 5° 以内才吸），"
        + "抬笔落下；大小与方向在<b>状态栏</b>显示（箭头旁不标注文字）。快捷键 N";

    public Key? Shortcut => Key.N;
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
                _tail = pointer.World;
                _drawing = true;

                context.ClearPreview();
                BuildPreview(context);
                Refresh(HeadFor(pointer.World, out _), snapped: false);
                break;

            case ToolPointerPhase.Move:
                if (!_drawing) return;
                // Refresh 内部会 SetStatus（含"已吸附"提示），这里不再重复算一遍
                Refresh(HeadFor(pointer.World, out var snapped), snapped);
                break;

            case ToolPointerPhase.Up:
                if (!_drawing) return;
                _drawing = false;
                EndDrag(context, pointer.World);
                break;
        }
    }

    // ---------------------------------------------------------------- 头部约束

    private Point HeadFor(Point rawWorld, out bool snapped)
    {
        var head = VectorMath.ConstrainHead(_tail, rawWorld, snap: true, out snapped);

        // 尾部吸附：优先吸已有线段端点（角的两条边交汇的顶点，老师真正想放的地方）
        var query = _context?.Query;
        if (query is not null)
        {
            const double endpointsRadius = 12.0;
            var points = query.PointsNear(rawWorld, endpointsRadius);
            if (points.Count > 0)
            {
                var nearest = points[0];
                double best = (nearest - rawWorld).LengthSquared;
                for (int i = 1; i < points.Count; i++)
                {
                    double d2 = (points[i] - rawWorld).LengthSquared;
                    if (d2 < best) { best = d2; nearest = points[i]; }
                }

                // 吸到端点后重新按"尾部不动、保留长度"摆正方向
                var unit = Unit(rawWorld - _tail);
                double length = (rawWorld - _tail).Length;
                var adjusted = new Point(nearest.X + unit.X * length, nearest.Y + unit.Y * length);
                head = adjusted;
                snapped = true;
            }
            else if (query.SnapToGfx(rawWorld, SnapRadiusWorld(_context)) is { } gfxPoint)
            {
                // M12 S7.1：图形对象提供的吸附目标（坐标系网格交点 / 坐标轴）。
                // 半径是"屏幕 10 DIP"的语义 ⇒ 必须除以视口缩放换回世界单位
                // （世界长常量进屏幕语义要除 Scale —— 已踩三次的同族雷）。
                head = gfxPoint;
                snapped = true;
            }
        }

        return head;
    }

    /// <summary>图形吸附半径（世界单位）= 屏幕 10 DIP ÷ 视口缩放。</summary>
    private static double SnapRadiusWorld(IToolContext? context)
        => 10.0 / Math.Max(context?.Scale ?? 1.0, 1e-6);

    private static Vector Unit(Vector v)
    {
        double len = v.Length;
        return len <= double.Epsilon ? new Vector(1, 0) : new Vector(v.X / len, v.Y / len);
    }

    private void Refresh(Point head, bool snapped)
    {
        if (_shaft is null || _head is null) return;

        var origin = _tail;
        var vector = head - origin;

        _shaft.X1 = origin.X;
        _shaft.Y1 = origin.Y;
        _shaft.X2 = head.X;
        _shaft.Y2 = head.Y;

        // 箭头三角：尖端 = 手指位置，底边往回收一个箭头长（几何在预览层里直接算世界坐标）
        var unit = Unit(vector);
        var normal = new Vector(-unit.Y, unit.X);
        double halfW = ArrowGeometry.HeadHalfWidthWorld;
        var baseCenter = new Point(head.X - unit.X * ArrowGeometry.HeadLengthWorld,
                                   head.Y - unit.Y * ArrowGeometry.HeadLengthWorld);

        _head.Points = new PointCollection
        {
            head,
            new Point(baseCenter.X + normal.X * halfW, baseCenter.Y + normal.Y * halfW),
            new Point(baseCenter.X - normal.X * halfW, baseCenter.Y - normal.Y * halfW),
        };

        // 数值只在状态栏给（箭头旁不标文字）；"已吸附"也一并在这里提示，方便老师确认角度对齐了。
        _context?.SetStatus(Describe(head, snapped));
    }

    // ---------------------------------------------------------------- 抬笔

    private void EndDrag(IToolContext context, Point rawWorld)
    {
        var head = HeadFor(rawWorld, out var snapped);
        double length = (head - _tail).Length;

        context.ClearPreview();
        var tail = _tail;
        Reset();

        if (length < VectorMath.MinDragWorld)
        {
            // 误触 / 零矢量：点一下不该在卷面上留一个零长箭头
            context.SetStatus($"矢量箭头：长度太短（{VectorMath.ToNewtons(length):F2} N），未落下");
            return;
        }

        var gfx = context.Gfx;
        if (gfx is null)
        {
            context.SetStatus("矢量箭头：当前程序不支持图形对象，箭头放不下");
            return;
        }

        double angle = VectorMath.DirectionDegrees(head.X - tail.X, head.Y - tail.Y);
        double magnitude = VectorMath.ToNewtons(length);

        var sys = PickSystem(tail, gfx);
        Point center = new((tail.X + head.X) / 2.0, (tail.Y + head.Y) / 2.0);

        double unitWorld = sys?.GetNumber(SysUnitKey, 1.0) ?? 1.0;
        if (double.IsNaN(unitWorld) || double.IsInfinity(unitWorld) || unitWorld <= 0) unitWorld = 1.0;
        double sysScale = sys is not null && sys.Scale > 0 && !double.IsNaN(sys.Scale) && !double.IsInfinity(sys.Scale)
            ? sys.Scale : 1.0;

        var numbers = new Dictionary<string, double>
        {
            [ArrowRenderer.LengthWorldKey] = length,
            [ArrowRenderer.MagnitudeKey] = magnitude,
            [ArrowRenderer.AngleKey] = angle,
            [UnitWorldKey] = unitWorld,
            [BindRelXKey] = 0, [BindRelYKey] = 0, [BindRelRotKey] = 0,
        };

        var texts = new Dictionary<string, string>
        {
            [ArrowRenderer.UnitKey] = "N",
        };

        // 绑到最近坐标系：宿主会按 bindTo 重算位姿 ⇒ 坐标系一挪，箭头自己跟着走
        if (sys is not null)
        {
            texts[BindToKey] = sys.Id;
        }

        // ★ Scale 的取值必须与"渲染器把本地几何当数学单位画"这件事配对，否则箭头会大 28 倍：
        //   · 未绑定（sys == null）：本地几何 1 单位 = 1 世界长 ⇒ Scale = 1
        //   · 已绑定：宿主 RecomputeAttachments 会把 Scale 强制设成 unitWorld × 坐标系.Scale
        //     我们这里先按同值写一次（落成那一帧就对），之后每次改动由宿主重算
        gfx.Add(new GfxDraft
        {
            Kind = ArrowRenderer.KindName,
            Center = center,
            RotationDegrees = sys?.RotationDegrees ?? 0.0,
            Scale = sys is null ? 1.0 : unitWorld * sysScale,
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,
            Numbers = numbers,
            Texts = texts,
        });

        context.SetStatus("矢量箭头：" + VectorMath.Describe(length, angle)
                          + (sys is not null ? "，已挂到坐标系" : string.Empty));
    }

    /// <summary>
    /// 挑要绑定的坐标系：优先"把尾部包在里面"的那个，其次原点最近的。
    /// </summary>
    /// <remarks>
    /// 与函数图像 <c>PickSystem</c> 同一条判据：只按原点距离挑，会让"在一张大图右下角画的箭头"
    /// 因为附近有张小图的原点而挂错地方。
    /// </remarks>
    private static IGfxObjectRef? PickSystem(Point anchor, IGfxObjectHost gfx)
    {
        IGfxObjectRef? insideBest = null;
        double insideSqr = double.MaxValue;
        IGfxObjectRef? nearBest = null;
        double nearSqr = double.MaxValue;

        foreach (var o in gfx.Objects)
        {
            if (o.Kind != SysKind) continue;

            double d2 = (o.Center - anchor).LengthSquared;
            if (d2 < nearSqr) { nearSqr = d2; nearBest = o; }

            if (o.BoundsWorld.Contains(anchor) && d2 < insideSqr) { insideSqr = d2; insideBest = o; }
        }

        return insideBest ?? nearBest;
    }

    // ---------------------------------------------------------------- 预览

    /// <summary>建立预览图元（箭杆 + 箭头）。只在按下时做一次，之后每帧只改数值。</summary>
    private void BuildPreview(IToolContext context)
    {
        var brush = new SolidColorBrush(context.PenColor);

        _shaft = new Line
        {
            Stroke = brush,
            StrokeThickness = context.PenWorldWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };

        _head = new Polygon
        {
            Fill = brush,
            IsHitTestVisible = false,
        };

        context.AddPreview(_shaft);
        context.AddPreview(_head);
    }

    /// <summary>状态栏读数：<c>矢量箭头：大小 6.0 N　方向 37°（已吸附）</c>。</summary>
    private string Describe(Point head, bool snapped = false)
    {
        var v = head - _tail;
        return "矢量箭头：" + VectorMath.Describe(v.Length, VectorMath.DirectionDegrees(v.X, v.Y))
               + (snapped ? "（已吸附）" : string.Empty);
    }

    private void Reset()
    {
        _drawing = false;
        _shaft = null;
        _head = null;
    }
}
