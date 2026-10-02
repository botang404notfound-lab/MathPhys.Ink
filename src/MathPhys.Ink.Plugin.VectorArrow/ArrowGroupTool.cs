using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.VectorArrow;

/// <summary>矢量组工具的 Id。</summary>
public static class ArrowGroupToolIds
{
    /// <summary>矢量组（框选型）。快捷键 G（N=箭头 / M=合力 / U=分解 / G=组）。</summary>
    public const string Id = "vectorgroup";
}

/// <summary>
/// 矢量组（创建型，<b>框选</b>）：拖一个框把几根矢量箭头"捆"成一个整体，之后拖一下全动。
/// </summary>
/// <remarks>
/// ★ <b>为什么需要它</b>（老师的真实场景）：一道受力图讲完，要整体挪到旁边、
/// 或者把它整体转个角度看"如果斜面变陡"。逐根拖要拖四次，而且拖完形状必然走样 ——
/// 手一抖，两个力的夹角就变了，而"夹角变了"在卷面上<b>完全看不出来</b>。
/// <para>
/// ★ <b>实现路线与合力同构</b>（一对多跟随）：组自己是一个普通对象
/// （<c>Kind="vectorgroup"</c> + <c>Texts["groupOf"]</c>），成员的基准位姿存在组的
/// <c>Numbers</c> 里；拖动组时按 <see cref="VectorMath.PlaceGroupMember"/> 现算成员位姿。
/// <b>宿主与契约仍然零改动。</b>
/// </para>
/// <para>
/// ★ <b>为什么基准位姿存 Numbers 而不是靠"每帧加增量"</b>：
/// 平移好办，但绕组中心旋转 / 缩放时，每根成员都得沿圆弧挪位置 + 各自转向，
/// 而"每帧在上一帧结果上叠加"会积累浮点误差（拖一分钟形状就散了）。
/// 存基准 + 每帧从基准重算 ⇒ 拖多久都不会漂。
/// </para>
/// <para>
/// <b>组跟着成员走</b>：成员被删掉时组自动缩小（并集变小）；
/// 成员全删光 ⇒ 组退化成一个小方框，仍能选中删除，<b>不留删不掉的幽灵</b>。
/// </para>
/// </remarks>
public sealed class ArrowGroupTool : ITool, IGfxTool
{
    /// <summary>框内至少要有这么多根箭头才打组（一根没什么可捆的）。</summary>
    public const int MinMembers = 2;

    /// <summary>最多记多少根成员的基准（防止 Numbers 字典无限膨胀）。</summary>
    public const int MaxMembers = 24;

    private IToolContext? _context;

    private bool _boxing;
    private Point _boxStart;

    // 预览图元：半透明虚线框（1 个）
    private Rectangle? _box;

    public string Id => ArrowGroupToolIds.Id;
    public string DisplayName => "矢量组";
    public string ToolTip
        => "矢量组：拖一个框把几根矢量箭头圈起来（至少 2 根），松手把它们捆成一个整体（画虚线框）；"
        + "之后点中组里任意一根、拖动即可<b>整组一起动</b>（旋转、缩放也整组走）。"
        + "删掉组对象即解组（箭头原样留下）。快捷键 G";

    public Key? Shortcut => Key.G;

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
            //   （与合力/正交分解同一条规矩；阈值只留给"拖出尺寸"型工具）。
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

    /// <summary>把两个角点规整成"左上角 + 宽高"的矩形（正着拖、反着拖都对）。</summary>
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

        _context?.SetStatus($"矢量组：框住 {CountInside(box)} 根矢量箭头"
                            + $"（至少 {MinMembers} 根才能捆成一组）");
    }

    /// <summary>数一数框住了几根可打组的箭头。</summary>
    private int CountInside(Rect box)
    {
        var gfx = _context?.Gfx;
        if (gfx is null) return 0;

        int count = 0;
        foreach (var obj in gfx.Objects)
        {
            if (!IsGroupable(obj)) continue;
            if (box.IntersectsWith(obj.BoundsWorld)) count++;
        }

        return count;
    }

    /// <summary>该对象能不能进组。</summary>
    /// <remarks>
    /// 三条排除，每一条都有具体理由：
    /// <list type="bullet">
    /// <item><b>不是矢量箭头</b>：坐标系、量角器进不了矢量组（它们有自己的语义）；</item>
    /// <item><b>合力</b>：它是算出来的，捆进组会导致"拖动组成员 ⇒ 合力重算 ⇒ 位置又变"的循环；</item>
    /// <item><b>已有的组</b>：组不能再套组（自己引用自己会让成员解析递归下去）。</item>
    /// </list>
    /// </remarks>
    private static bool IsGroupable(IGfxObjectRef obj)
        => obj.Kind == ArrowRenderer.KindName
        && !ArrowRenderer.IsSum(obj)
        && !GroupRenderer.IsGroup(obj);

    // ---------------------------------------------------------------- 打组

    private void EndBox(IToolContext context, Point current)
    {
        var box = BoxOf(_boxStart, current);

        context.ClearPreview();
        Reset();

        var gfx = context.Gfx;
        if (gfx is null)
        {
            context.SetStatus("矢量组：当前程序不支持图形对象，组建不起来");
            return;
        }

        var members = new List<IGfxObjectRef>();
        foreach (var obj in gfx.Objects)
        {
            if (!IsGroupable(obj)) continue;
            if (box.IntersectsWith(obj.BoundsWorld)) members.Add(obj);

            if (members.Count >= MaxMembers) break;
        }

        if (members.Count < MinMembers)
        {
            context.SetStatus($"矢量组：框住了 {members.Count} 根矢量箭头，至少 {MinMembers} 根才能捆成一组");
            return;
        }

        // ★ 组的中心与基准尺寸取自"成员的并集"（按包围盒算，不看箭头方向）
        var union = GroupRenderer.UnionBoundsWorld(members);
        if (union.IsEmpty || union.Width <= 0 || union.Height <= 0)
        {
            context.SetStatus("矢量组：这几根箭头的范围太小，捆不起来（把它们拉长一点再试）");
            return;
        }

        var groupCenter = new Point(union.X + union.Width / 2.0, union.Y + union.Height / 2.0);

        // ★ 一次性把"成员基准"算好，之后拖动只读这些基准 —— 这就是"拖多久都不散"的依据
        var ids = new List<string>();
        var numbers = new Dictionary<string, double>
        {
            [GroupRenderer.BaseRotationKey] = 0.0,   // 新组的基准旋转恒为 0
            [GroupRenderer.BaseScaleKey] = 1.0,      // 新组的基准缩放恒为 1
        };

        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            ids.Add(member.Id);

            numbers[GroupRenderer.BaseXPrefix + i] = member.Center.X - groupCenter.X;
            numbers[GroupRenderer.BaseYPrefix + i] = member.Center.Y - groupCenter.Y;
            // 成员相对组的额外旋转：成员自己可能转过（选择工具能转），要带上
            numbers[GroupRenderer.BaseRotPrefix + i] = member.RotationDegrees;
        }

        gfx.Add(new GfxDraft
        {
            Kind = GroupRenderer.KindName,
            Center = groupCenter,
            RotationDegrees = 0,
            Scale = 1.0,                 // 本地几何按世界长度画（未绑定 ⇒ 1 单位 = 1 世界长）
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,
            Numbers = numbers,
            Texts = new Dictionary<string, string>
            {
                // ★ 这一条就是"组"的全部身份：渲染器据此画虚线框、宿主据此算成员并集
                [GroupRenderer.GroupOfKey] = string.Join(",", ids),
            },
        });

        context.SetStatus($"矢量组：{members.Count} 根箭头已捆成一组"
                          + "（点中任意一根拖一下，整组一起走；删掉这个框即解组）");
    }

    // ---------------------------------------------------------------- 预览

    /// <summary>建立预览图元（一个半透明虚线框）。只在按下时做一次，之后每帧只改数值。</summary>
    private void BuildPreview(IToolContext context)
    {
        var fill = new SolidColorBrush(Color.FromArgb(28, context.PenColor.R, context.PenColor.G, context.PenColor.B));
        fill.Freeze();

        _box = new Rectangle
        {
            Stroke = new SolidColorBrush(context.PenColor),
            StrokeThickness = context.PenWorldWidth,
            StrokeDashArray = new DoubleCollection { GroupRenderer.GroupDashLength, GroupRenderer.GroupDashGap },
            Fill = fill,
            IsHitTestVisible = false,
        };

        context.AddPreview(_box);
    }

    private void Reset()
    {
        _boxing = false;
        _box = null;
    }
}
