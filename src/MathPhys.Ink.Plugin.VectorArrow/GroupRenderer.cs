using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.VectorArrow;

/// <summary>
/// 矢量组的画法：一个<b>虚线包围盒</b>，套住组里全部箭头的和集。
/// </summary>
/// <remarks>
/// 与 <see cref="ArrowRenderer"/> 的合力分支同构：<b>宿主提供"何时重算"，插件决定"怎么重算"</b>。
/// 组对象的几何取自<b>别人</b>（成员箭头），所以：
/// <list type="bullet">
/// <item>宿主在 <c>RecomputeAttachments</c> 末尾把组对象"碰一下"（重算 LocalSize）；</item>
/// <item>渲染器在 <see cref="Measure"/>/<see cref="CreateVisual"/> 里<b>当场</b>遍历成员求并集。</item>
/// </list>
/// <para>
/// ★ 组<b>不画任何箭头</b>，只画一个框。理由：组是一根"隐形的手"，
/// 它在卷面上不该有第二个存在感 —— 老师看到框就知道"这几根绑在一起了"，画别的都是干扰。
/// </para>
/// <para>
/// 组自身的位姿（<see cref="IGfxObjectRef.Center"/>/<c>RotationDegrees</c>/<c>Scale</c>）
/// 才是"组的手柄"。拖动组 = 改这三个值，成员位姿由 <see cref="VectorMath.PlaceGroupMember"/>
/// 从这个组位姿与各自基准位姿现算 —— 见 <see cref="GroupMath"/> 的说明。
/// </para>
/// </remarks>
public sealed class GroupRenderer : IGfxObjectRenderer, IGfxBoardAwareRenderer
{
    /// <summary>对象种类；与 <see cref="GfxDraft.Kind"/> 对应。</summary>
    public const string KindName = "vectorgroup";

    // ---------------------------------------------------------------- 参数键

    /// <summary>成员 Id 列表键（逗号分隔）。</summary>
    public const string GroupOfKey = "groupOf";

    /// <summary>打组时组自身的旋转（度），用于算"组转过多少"。</summary>
    public const string BaseRotationKey = "baseRot";

    /// <summary>打组时组自身的缩放，用于算"组缩放了多少倍"。</summary>
    public const string BaseScaleKey = "baseScale";

    /// <summary>成员基准偏移的键前缀（后缀 = 成员序号，如 <c>bx0</c>/<c>by0</c>）。</summary>
    public const string BaseXPrefix = "bx";

    /// <summary>同上（y）。</summary>
    public const string BaseYPrefix = "by";

    /// <summary>成员自身相对组的额外旋转（度）的键前缀。</summary>
    public const string BaseRotPrefix = "brot";

    /// <summary>组的虚线包围盒：与合力同一套节距，保证卷面上"虚线"只有一种观感。</summary>
    public const double GroupDashLength = 6.0;
    public const double GroupDashGap = 4.0;

    /// <summary>包围盒向外留的空白（世界单位）：免得框线正好压在箭头的边缘上。</summary>
    public const double Padding = 6.0;

    /// <summary>成员全都没了时的占位尺寸（还要能点得到、删得掉）。</summary>
    public const double EmptyPlaceholderSize = 48.0;

    /// <summary>"这是不是一个组"。</summary>
    public static bool IsGroup(IGfxObjectRef obj) => !string.IsNullOrEmpty(obj.GetText(GroupOfKey, ""));

    /// <summary>
    /// 解析成员 Id 列表（逗号分隔；空项、重复项都剔掉）。
    /// </summary>
    /// <remarks>与合力的 <c>SumMemberIds</c> 同一套规矩：重复会让"一组"变成"两组份"，肉眼看不出来。</remarks>
    public static IReadOnlyList<string> GroupMemberIds(IGfxObjectRef obj)
        => ArrowRenderer.ParseIdList(obj.GetText(GroupOfKey, ""));

    /// <summary>
    /// 从画布对象列表里挑出组引用的成员（保持列表顺序，跳过找不到的）。
    /// </summary>
    /// <remarks>
    /// <b>找不到的成员直接跳过、不报错</b>：老师删掉一根箭头是正常操作，
    /// 组应当自动按剩下的重算，而不是变成一团错误框。全都没了 ⇒ 退化成占位尺寸。
    /// </remarks>
    public static IReadOnlyList<IGfxObjectRef> GroupMembers(
        IGfxObjectRef obj, IReadOnlyList<IGfxObjectRef>? all)
    {
        var members = new List<IGfxObjectRef>();
        if (all is null || all.Count == 0) return members;

        foreach (var id in GroupMemberIds(obj))
        {
            foreach (var candidate in all)
            {
                if (!string.Equals(candidate.Id, id, StringComparison.Ordinal)) continue;
                // 只认同类（矢量箭头）；组本身不能再当成员（防嵌套导致的自引用死循环）
                if (candidate.Kind != ArrowRenderer.KindName) continue;
                if (IsGroup(candidate)) continue;

                // ★ 合力也不能当成员：合力的位置每帧由它的分矢量重算，
                //   把它捆进组里会变成组拖它、重算又把它拉回去的拉锯 ——
                //   表现是这根箭头怎么拖都弹回原位，而且只在同时存在合力时才复现。
                if (ArrowRenderer.IsSum(candidate)) continue;

                members.Add(candidate);
                break;
            }
        }

        return members;
    }

    /// <summary>本渲染器看到的画布对象（宿主每次同步视觉前通过 <see cref="SetBoard"/> 递进来）。</summary>
    /// <remarks>
    /// ★ <b>实例字段而不是静态字段</b>：harness 与宿主可能同时存在多个渲染器实例，
    /// 静态字段会互相踩（与 <see cref="ArrowRenderer"/> 的教训完全一样）。
    /// </remarks>
    private IReadOnlyList<IGfxObjectRef>? _board;

    /// <inheritdoc/>
    public void SetBoard(IReadOnlyList<IGfxObjectRef>? objects) => _board = objects;

    /// <inheritdoc/>
    public string Kind => KindName;

    // ---------------------------------------------------------------- 和集包围盒

    /// <summary>
    /// 把全部成员的包围盒并起来，转成<b>以组中心为原点</b>的本地矩形。
    /// </summary>
    /// <remarks>
    /// ★ 为什么返回"本地"而不是"世界"：对象的本地几何必须是以自己中心为原点的，
    /// 位姿（<c>Center</c>/<c>RotationDegrees</c>/<c>Scale</c>）由宿主统一施加。
    /// 这里把世界并集除以组自身的 Scale 再减去组中心，就得到本地矩形。
    /// <para>
    /// 组一般 <c>Scale=1</c>、<c>Rotation=0</c>（打组时就是），
    /// 但拖动缩放之后 Scale 会变，所以必须真的按 Scale 换回来 —— 否则框会越缩越小。
    /// </para>
    /// </remarks>
    public Rect UnionBoundsLocal(IGfxObjectRef obj)
    {
        var members = GroupMembers(obj, _board);

        if (members.Count == 0)
        {
            // 成员全没了：给一个占位方框（仍可点、可删），不要缩成一个点
            double half = EmptyPlaceholderSize / 2.0;
            return new Rect(-half, -half, EmptyPlaceholderSize, EmptyPlaceholderSize);
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var member in members)
        {
            var box = member.BoundsWorld;
            if (box.IsEmpty) continue;

            if (box.Left < minX) minX = box.Left;
            if (box.Top < minY) minY = box.Top;
            if (box.Right > maxX) maxX = box.Right;
            if (box.Bottom > maxY) maxY = box.Bottom;
        }

        if (minX > maxX || minY > maxY)
        {
            double half = EmptyPlaceholderSize / 2.0;
            return new Rect(-half, -half, EmptyPlaceholderSize, EmptyPlaceholderSize);
        }

        // 世界并集 → 本地：减组中心、除组缩放
        double scale = SafeScale(obj.Scale);
        double left = (minX - Padding - obj.Center.X) / scale;
        double top = (minY - Padding - obj.Center.Y) / scale;
        double right = (maxX + Padding - obj.Center.X) / scale;
        double bottom = (maxY + Padding - obj.Center.Y) / scale;

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>和集包围盒的世界矩形（打组时用来定组的中心与尺寸）。</summary>
    public static Rect UnionBoundsWorld(IReadOnlyList<IGfxObjectRef> members)
    {
        if (members.Count == 0) return Rect.Empty;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        foreach (var member in members)
        {
            var box = member.BoundsWorld;
            if (box.IsEmpty) continue;

            any = true;
            if (box.Left < minX) minX = box.Left;
            if (box.Top < minY) minY = box.Top;
            if (box.Right > maxX) maxX = box.Right;
            if (box.Bottom > maxY) maxY = box.Bottom;
        }

        if (!any) return Rect.Empty;

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static double SafeScale(double scale)
        => scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale) ? 1.0 : scale;

    // ---------------------------------------------------------------- 渲染器契约

    /// <inheritdoc/>
    /// <remarks>
    /// ★ 尺寸 = 和集包围盒的尺寸，<b>每次都用当前成员重算</b>。
    /// 这一步不能省：宿主与视觉层都靠"LocalSize 变了"才知道要重建几何
    /// （见 <c>GfxObjectLayer.Sync</c> 的签名判据）。成员一动，并集尺寸就变 ⇒ 框跟着重画。
    /// </remarks>
    public Size Measure(IGfxObjectRef obj)
    {
        var local = UnionBoundsLocal(obj);
        return new Size(Math.Max(local.Width, 1.0), Math.Max(local.Height, 1.0));
    }

    /// <inheritdoc/>
    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        var local = UnionBoundsLocal(obj);
        return new GroupVisual(obj.Color, obj.LineWorldWidth, obj.Scale, local);
    }

    /// <summary>状态栏读数：「矢量组：3 根，合计 12.0 N」。</summary>
    public string Describe(IGfxObjectRef obj)
    {
        var members = GroupMembers(obj, _board);

        double total = 0;
        string unit = "N";
        foreach (var member in members)
        {
            double magnitude = member.GetNumber(ArrowRenderer.MagnitudeKey, double.NaN);
            if (double.IsNaN(magnitude) || magnitude <= 0)
                magnitude = VectorMath.ToNewtons(ArrowRenderer.LengthWorldOf(member));

            total += magnitude;
            unit = ArrowRenderer.UnitLabelOf(member);
        }

        return VectorMath.GroupDescribe(members.Count, total, unit);
    }
}

/// <summary>
/// 组的视觉：一个虚线矩形，画在本地坐标（原点 = 组中心）。
/// </summary>
/// <remarks>
/// ★ <b>不画任何文字</b>（组里有几根、合力多大都走状态栏）：框上写数字会盖住题目正文，
/// 与"箭头旁不标注读数"是同一条拍板。
/// <para>
/// 描边与虚线节距都要<b>除以 Scale</b>：本地几何会被宿主按 Scale 放大，
/// "固定在屏幕上看得见的粗细/节距"必须逆着走（与 <c>ArrowVisual</c> 同一个坑）。
/// </para>
/// </remarks>
internal sealed class GroupVisual : FrameworkElement
{
    private readonly Pen _framePen;
    private readonly Rect _rect;

    /// <param name="inkColor">墨色。</param>
    /// <param name="lineWorldWidth">世界线宽（未含缩放）。</param>
    /// <param name="scale">对象自身的缩放（本地 → 世界）。</param>
    /// <param name="localBounds">本地矩形（原点 = 组中心）。</param>
    internal GroupVisual(Color inkColor, double lineWorldWidth, double scale, Rect localBounds)
    {
        _rect = localBounds;

        double safeScale = scale > 0 && !double.IsNaN(scale) && !double.IsInfinity(scale) ? scale : 1.0;
        double localStroke = (lineWorldWidth > 0 ? lineWorldWidth : 1.5) / safeScale;

        var pen = new Pen(new SolidColorBrush(inkColor), localStroke)
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat,
            DashCap = PenLineCap.Flat,
            // 节距也除 Scale —— 否则缩放一变，虚线就变成实线（或密到看不见）
            DashStyle = new DashStyle(
                new[] { GroupRenderer.GroupDashLength / safeScale, GroupRenderer.GroupDashGap / safeScale }, 0),
        };

        if (pen.CanFreeze) pen.Freeze();
        _framePen = pen;

        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext drawingContext)
        => drawingContext.DrawRectangle(null, _framePen, _rect);
}
