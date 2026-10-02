using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.VectorArrow;

/// <summary>合力工具的 Id。</summary>
public static class ArrowSumToolIds
{
    /// <summary>合力（框选型）。快捷键 M（N 已被矢量箭头占了）。</summary>
    public const string Id = "vectorsum";
}

/// <summary>
/// 合力（创建型，<b>框选</b>）：在卷面上拖出一个矩形，把框住的矢量箭头求矢量和。
/// </summary>
/// <remarks>
/// ★ <b>为什么是框选而不是"选中一根再按键"</b>（2026-09-22 用户拍板）：
/// 一体机白板上没有键盘，任何"按键触发"的功能都是鸡肋 —— 老师的动作只有"点"和"拖"。
/// 框选又比"逐根点选"更省事：一道受力图往往是三四根力，框一下比点四下快得多。
/// <para>
/// 与其它创建型工具一样落成一个普通对象（<c>Kind="vector"</c> + <c>Texts["sumOf"]</c>），
/// 于是自动获得选中/拖动/旋转/删除/撤销/存档，<b>宿主与契约零改动</b>。
/// 它与普通箭头的唯一区别在渲染器里：几何从分矢量当场算，并且画成虚线。
/// </para>
/// <para>
/// <b>合力不跟随坐标系</b>：它的长度方向由分矢量（世界坐标）决定，
/// 把合力再挂到坐标系上会变成"跟着坐标系转、但长度按分矢量算"的两套逻辑打架。
/// 分矢量自己已经挂在坐标系上，坐标系一挪它们就动，合力自然跟着动。
/// </para>
/// </remarks>
public sealed class ArrowSumTool : ITool, IGfxTool
{
    /// <summary>框内至少要有这么多根箭头才求和（物理上"合力"至少两个力才有意义）。</summary>
    public const int MinMembers = 2;

    /// <summary>力的方向吸附（与单根箭头一致：15°，容差 5°）。合力方向不吸附 —— 它是算出来的。</summary>
    private IToolContext? _context;

    private bool _boxing;
    private Point _boxStart;

    // 预览图元：虚线框（1 个）
    private Rectangle? _box;

    public string Id => ArrowSumToolIds.Id;
    public string DisplayName => "合力";
    public string ToolTip
        => "合力：拖一个框把几根矢量箭头圈起来（至少 2 根），松手自动画出一根虚线合力，"
        + "并选中它；合力的大小与方向在<b>状态栏</b>显示。分矢量被拖动或删除时，合力自动重算。快捷键 M";

    public Key? Shortcut => Key.M;

    public bool UsesInkLayer => false;

    /// <summary>需要指针：框选要拿到按下/移动/抬起三个事件。</summary>
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
            // ★ 按下过就必须响应：框选的"起点"没有歧义，不需要拖动阈值
            //   （与函数图像/矢量箭头同一条规矩；阈值只留给"拖出尺寸"型工具）。
            case ToolPointerPhase.Down:
                _boxStart = pointer.World;
                _boxing = true;
                context.ClearPreview();
                BuildPreview(context);
                Refresh(pointer.World);
                break;

            case ToolPointerPhase.Move:
                if (!_boxing) return;
                Refresh(pointer.World);
                break;

            case ToolPointerPhase.Up:
                if (!_boxing) return;
                _boxing = false;
                EndBox(context, pointer.World);
                break;
        }
    }

    // ---------------------------------------------------------------- 框

    /// <summary>把两个角点规整成一个"左上角 + 宽高"的矩形（正着拖、反着拖都对）。</summary>
    private static Rect BoxOf(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void Refresh(Point current)
    {
        if (_box is null) return;

        var box = BoxOf(_boxStart, current);
        _box.Width = box.Width;
        _box.Height = box.Height;
        System.Windows.Controls.Canvas.SetLeft(_box, box.X);
        System.Windows.Controls.Canvas.SetTop(_box, box.Y);

        _context?.SetStatus($"合力：框住 {CountInside(box)} 根矢量箭头"
                            + $"（至少 {MinMembers} 根才求和）");
    }

    /// <summary>
    /// 数一数框住了几根矢量箭头。
    /// </summary>
    /// <remarks>
    /// 判据是"箭头的<b>包围盒与框相交</b>"，不是"整根都在框里"：
    /// 老师框的时候很难把一根长箭头完整圈进去，而"我明明框到了它却没算"比"多算了一根"
    /// 更让人摸不着头脑（多算的那根就在框边上，一眼能看出来）。
    /// </remarks>
    private int CountInside(Rect box)
    {
        var gfx = _context?.Gfx;
        if (gfx is null) return 0;

        int count = 0;
        foreach (var obj in gfx.Objects)
        {
            if (!IsSelectable(obj)) continue;
            if (box.IntersectsWith(obj.BoundsWorld)) count++;
        }

        return count;
    }

    /// <summary>该对象能不能当分矢量（是矢量箭头，而且它自己不是合力）。</summary>
    /// <remarks>
    /// <b>合力不能再当分矢量</b>：否则"框住一根分力 + 一根合力"会算出二次合成的东西，
    /// 而它们在卷面上看起来都是箭头，老师根本看不出区别。
    /// </remarks>
    private static bool IsSelectable(IGfxObjectRef obj)
        => obj.Kind == ArrowRenderer.KindName && !ArrowRenderer.IsSum(obj);

    // ---------------------------------------------------------------- 落对象

    private void EndBox(IToolContext context, Point current)
    {
        var box = BoxOf(_boxStart, current);

        context.ClearPreview();
        Reset();

        var gfx = context.Gfx;
        if (gfx is null)
        {
            context.SetStatus("合力：当前程序不支持图形对象，合力放不下");
            return;
        }

        var members = new List<IGfxObjectRef>();
        foreach (var obj in gfx.Objects)
        {
            if (!IsSelectable(obj)) continue;
            if (box.IntersectsWith(obj.BoundsWorld)) members.Add(obj);
        }

        if (members.Count < MinMembers)
        {
            context.SetStatus($"合力：框住了 {members.Count} 根矢量箭头，至少 {MinMembers} 根才能求和");
            return;
        }

        // 用"分矢量的大小 + 方向"当场求和：用来判断"是不是正好抵消"（抵消就不落）。
        // ★ 这里算一次是为了给出可读的状态栏理由；真正的合力由渲染器每次重算
        //   （分矢量随时会动，存下来的值一写就过期）。
        var vectors = new List<(double, double)>();
        foreach (var member in members)
        {
            vectors.Add((MagnitudeOf(member), AngleOf(member)));
        }

        var (sumMagnitude, sumDirection) = VectorMath.VectorSum(vectors);

        if (VectorMath.IsZeroSum(sumMagnitude))
        {
            context.SetStatus("合力：" + VectorMath.FormatNumber(sumMagnitude)
                              + " N —— 这几根力互相抵消了，不落合力（换几根再框）");
            return;
        }

        // 合力放在"分矢量的共同中心"：这是老师一眼能认出的位置（而不是卷面别处）。
        // 分矢量自己怎么摆都不影响合力方向（矢量和与起点无关），所以取中心是安全的。
        double cx = 0, cy = 0;
        foreach (var member in members) { cx += member.Center.X; cy += member.Center.Y; }
        cx /= members.Count;
        cy /= members.Count;

        var ids = new List<string>();
        foreach (var member in members) ids.Add(member.Id);

        double unitWorld = ArrowRenderer.UnitOf(members[0]);

        gfx.Add(new GfxDraft
        {
            Kind = ArrowRenderer.KindName,
            Center = new Point(cx, cy),
            RotationDegrees = 0,          // 合力不跟随坐标系（见类注释）
            Scale = 1.0,                  // 未绑定 ⇒ 本地几何 1 单位 = 1 世界长
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,
            Numbers = new Dictionary<string, double>
            {
                // 冗余存档：分矢量被删光后仍能显示"曾经是什么"（也便于别人读存档）
                [ArrowRenderer.MagnitudeKey] = sumMagnitude,
                [ArrowRenderer.AngleKey] = sumDirection,
                [ArrowRenderer.LengthWorldKey] = sumMagnitude * VectorMath.PointsPerInch
                                                 / VectorMath.CentimetersPerInch
                                                 / VectorMath.DefaultNewtonsPerCentimeter,
                [ArrowTool.UnitWorldKey] = unitWorld,
            },
            Texts = new Dictionary<string, string>
            {
                [ArrowRenderer.UnitKey] = "N",
                // ★ 这一条就是"合力"的全部身份：渲染器据此走虚线 + 现场求和
                [ArrowRenderer.SumOfKey] = string.Join(",", ids),
            },
        });

        context.SetStatus("合力：" + VectorMath.SumDescribe(sumMagnitude, sumDirection)
                          + $"（{members.Count} 根分矢量，拖动分矢量它自己跟着变）");
    }

    /// <summary>取分矢量的大小（N）：优先用存档里的冗余值，其次按长度换算。</summary>
    private static double MagnitudeOf(IGfxObjectRef obj)
    {
        double magnitude = obj.GetNumber(ArrowRenderer.MagnitudeKey, double.NaN);
        if (!double.IsNaN(magnitude) && !double.IsInfinity(magnitude) && magnitude > 0) return magnitude;

        return VectorMath.ToNewtons(ArrowRenderer.LengthWorldOf(obj));
    }

    private static double AngleOf(IGfxObjectRef obj) => ArrowRenderer.AngleOf(obj);

    // ---------------------------------------------------------------- 预览

    /// <summary>建立预览图元（一个虚线框）。只在按下时做一次，之后每帧只改数值。</summary>
    private void BuildPreview(IToolContext context)
    {
        _box = new Rectangle
        {
            Stroke = new SolidColorBrush(context.PenColor),
            StrokeThickness = context.PenWorldWidth,
            StrokeDashArray = new DoubleCollection { ArrowRenderer.SumDashLength, ArrowRenderer.SumDashGap },
            Fill = new SolidColorBrush(Color.FromArgb(28, context.PenColor.R, context.PenColor.G, context.PenColor.B)),
            IsHitTestVisible = false,
        };

        // 框内填充是半透明的"选中感"：老师一眼能看出哪些箭头被圈进来了
        _box.Fill.Freeze();
        context.AddPreview(_box);
    }

    private void Reset()
    {
        _boxing = false;
        _box = null;
    }
}
