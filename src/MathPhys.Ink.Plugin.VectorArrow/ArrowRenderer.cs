using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.VectorArrow;

/// <summary>
/// 矢量箭头的画法 —— 插件对"图形库"这一层的贡献。
/// </summary>
/// <remarks>
/// 一个对象 = <b>一个</b>视觉元素（<see cref="ArrowVisual"/>），箭杆 / 箭头
/// 都在它自己的 <c>OnRender</c> 里画（与坐标系同款）。不做"一根线一个 Path"。
/// <para>
/// ★ <b>箭头旁不画读数文字</b>（2026-09-22 用户拍板）：箭头本身就是"力的示意"，
/// 卷面上一堆"大小 x N　方向 y°"会盖住题目正文与受力图。需要精确数值时看状态栏
/// （拖动时实时显示、落下时也报一句），比钉在卷面上更合讲课节奏。
/// 于是本对象<b>越界绘制为零</b> —— 视觉全部落在 <see cref="Measure"/> 的包围盒内。
/// </para>
/// <para>
/// <b>本地几何按"数学单位"画</b>（本地长 = 世界长 ÷ unitWorld）：
/// 宿主 <c>RecomputeAttachments</c> 对绑定坐标系的对象一律设
/// <c>Scale = unitWorld × 坐标系.Scale</c>，除以 unitWorld 正好抵消，得到的才是世界长。
/// 见 <see cref="UnitOf"/> 的详细说明（不这么做的后果是箭头被放大 28 倍）。
/// </para>
/// </remarks>
public sealed class ArrowRenderer : IGfxObjectRenderer, IGfxBoardAwareRenderer
{
    /// <summary>对象种类；与 <see cref="GfxDraft.Kind"/> 对应。</summary>
    public const string KindName = "vector";

    // ---------------------------------------------------------------- 参数键（与 Tool 共用）

    /// <summary>箭头长度（世界长）。</summary>
    public const string LengthWorldKey = "lengthWorld";

    /// <summary>大小（N）。存档里冗余保存，读写时优先用它（老师改过比例也不怕）。</summary>
    public const string MagnitudeKey = "magnitudeN";

    /// <summary>方向角（度，物理读法：逆时针为正、0~360）。</summary>
    public const string AngleKey = "angleDeg";

    /// <summary>单位字符（"N"）。</summary>
    public const string UnitKey = "unit";

    /// <summary>
    /// 合力键：<c>Texts[SumOfKey]</c> = 逗号分隔的分矢量 Id 列表（宿主同名常量）。
    /// </summary>
    /// <remarks>
    /// ★ 与宿主 <c>GfxObjectStore.SumOfKey</c> 同值。这里再写一份常量而不是引用宿主，
    /// 是因为插件<b>不认识</b>宿主类型（契约里没有 GfxObjectStore）；字符串键是本工程
    /// 插件与宿主之间既有的约定形式（<c>bindTo</c>、<c>unitWorld</c> 都这么来的）。
    /// </remarks>
    public const string SumOfKey = "sumOf";

    /// <summary>
    /// 来源键：<c>Texts[ParentKey]</c> = "这根箭头是哪个对象分解出来的"。
    /// </summary>
    /// <remarks>
    /// ★ 目前只做<b>记录</b>、不做联动（删原矢量不会带走分量，那是刻意的：
    /// 老师常常是"先把两根分量留下来讲课、再删掉斜的那根"，自动带走反而帮倒忙）。
    /// 记着它的用处有二：一是读存档时一眼看出这两根箭头的来历；
    /// 二是将来若要做"原矢量一动、分量跟着转"的联动，键名已经在这里定好了，
    /// 不用再改一次数据格式（旧存档没有这个键也不影响 —— 取不到就是空串）。
    /// </remarks>
    public const string ParentKey = "parentVector";

    /// <summary>合力箭头的虚线段长/间隔（本地单位 = 数学单位）。</summary>
    /// <remarks>
    /// 不用 WPF 的 <c>DashStyles</c>：那些是"按线宽倍数"定义的，
    /// 而本插件的线宽会随坐标系缩放变（1.5 → 42），虚线节距会跟着飞到看不见。
    /// 这里用绝对的 <c>DashStyle</c> 数值，保证不管怎么缩放，"虚线感"始终一致。
    /// </remarks>
    public const double SumDashLength = 9.0;
    public const double SumDashGap = 5.0;

    /// <summary>合力"是不是合力"。</summary>
    public static bool IsSum(IGfxObjectRef obj) => !string.IsNullOrEmpty(obj.GetText(SumOfKey, ""));

    /// <summary>
    /// 解析分矢量 Id 列表（逗号分隔；空项、重复项都剔掉）。
    /// </summary>
    /// <remarks>
    /// 重复项必须去重：同一根箭头被记两次会让合力算成两倍 ——
    /// 而"合力是两倍"在卷面上看起来只是"箭头长了点"，肉眼绝对看不出来。
    /// </remarks>
    public static IReadOnlyList<string> SumMemberIds(IGfxObjectRef obj)
        => ParseIdList(obj.GetText(SumOfKey, ""));

    /// <summary>
    /// 解析"逗号分隔的 Id 列表"（空项、重复项都剔掉）—— <b>合力与矢量组共用</b>。
    /// </summary>
    /// <remarks>
    /// ★ 抽成公共函数而不是各写一份：两处规则必须完全一致。
    /// 曾经"去重"只在合力那边写了，将来矢量组若自己再实现一遍，
    /// 迟早出现"合力去重了、组没去重"这种只在某一侧复现的怪 bug。
    /// </remarks>
    public static IReadOnlyList<string> ParseIdList(string raw)
    {
        var ids = new List<string>();

        if (string.IsNullOrEmpty(raw)) return ids;

        foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string id = piece.Trim();
            if (id.Length == 0 || ids.Contains(id, StringComparer.Ordinal)) continue;

            ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// 从画布对象列表里挑出合力引用的分矢量（保持列表顺序，跳过找不到的 / 不是箭头的）。
    /// </summary>
    /// <remarks>
    /// <b>找不到的分矢量直接跳过、不报错</b>：老师删掉一根分力是正常操作，
    /// 合力应当自动按剩下的重算，而不是变成一团错误框。
    /// 全部都没了 ⇒ 合矢量 = 零 ⇒ <see cref="ArrowGeometry.ClampLength"/> 把它压到最小可画长度，
    /// 用户看到一根极短的箭头，可以自己删掉它。
    /// </remarks>
    public static IReadOnlyList<IGfxObjectRef> SumMembers(
        IGfxObjectRef obj, IReadOnlyList<IGfxObjectRef>? all)
    {
        var members = new List<IGfxObjectRef>();
        if (all is null || all.Count == 0) return members;

        foreach (var id in SumMemberIds(obj))
        {
            foreach (var candidate in all)
            {
                if (!string.Equals(candidate.Id, id, StringComparison.Ordinal)) continue;
                if (candidate.Kind != KindName) continue;   // 只认同类（矢量箭头）

                members.Add(candidate);
                break;
            }
        }

        return members;
    }

    /// <summary>
    /// 本渲染器看到的画布对象（宿主每次同步视觉前通过 <see cref="SetBoard"/> 递进来）。
    /// </summary>
    /// <remarks>
    /// ★ <b>用实例字段而不是静态字段</b>：harness 与宿主可能同时存在多个渲染器实例
    /// （每个 <c>GfxRendererCatalog</c> 一个），静态字段会互相踩 ——
    /// 表现成"另一个测试的画布内容跑进了这一个的合力里"，且只在同时跑时复现。
    /// <para>
    /// 只在 <c>Measure</c>/<c>CreateVisual</c> 期间用到，不长期持有引用。
    /// </para>
    /// </remarks>
    private IReadOnlyList<IGfxObjectRef>? _board;

    /// <inheritdoc/>
    public void SetBoard(IReadOnlyList<IGfxObjectRef>? objects) => _board = objects;

    /// <inheritdoc/>
    public string Kind => KindName;

    // ---------------------------------------------------------------- 取参（判空 + 走默认值）

    /// <summary>读箭头长度（<b>世界长</b>）。取不到时按大小换算，再不行给一个最小可画值。</summary>
    public static double LengthWorldOf(IGfxObjectRef obj)
    {
        double length = obj.GetNumber(LengthWorldKey, double.NaN);
        if (!double.IsNaN(length) && !double.IsInfinity(length) && length > 0)
            return ArrowGeometry.ClampLength(length);

        // 回退：从"大小 N"倒推世界长（比例 1 cm = 1 N）
        double magnitude = obj.GetNumber(MagnitudeKey, double.NaN);
        if (!double.IsNaN(magnitude) && magnitude > 0)
        {
            double cm = magnitude / VectorMath.DefaultNewtonsPerCentimeter;
            return ArrowGeometry.ClampLength(cm * VectorMath.PointsPerInch / VectorMath.CentimetersPerInch);
        }

        return ArrowGeometry.ClampLength(0);
    }

    /// <summary>
    /// 读该对象的"一数学单位 = 多少世界长"（<c>unitWorld</c>，与坐标系插件同键名）。
    /// </summary>
    /// <remarks>
    /// ★ 这个值是本渲染器的关键：宿主 <c>RecomputeAttachments</c> 对"绑定了坐标系"的对象
    /// 一律设 <c>Scale = unitWorld × 坐标系.Scale</c>（那是为函数曲线设计的，曲线的本地几何是数学坐标）。
    /// 箭头要复用这一条通用公式，<b>本地几何就必须同样按"数学单位"来画</b> ——
    /// 于是本地长度 = 世界长 ÷ unitWorld。
    /// <para>
    /// 不这么做（本地几何直接用世界长）的后果：跟随坐标系时 Scale ≈ 28，
    /// 整根箭头被放大 28 倍，变成一根横穿整页的巨杆 —— 实测过，是真的。
    /// </para>
    /// </remarks>
    public static double UnitOf(IGfxObjectRef obj)
    {
        double unit = obj.GetNumber(ArrowTool.UnitWorldKey, 1.0);
        if (double.IsNaN(unit) || double.IsInfinity(unit) || unit <= 0) unit = 1.0;
        return unit;
    }

    /// <summary>读方向角（度）。</summary>
    public static double AngleOf(IGfxObjectRef obj)
        => VectorMath.NormalizeDegrees(obj.GetNumber(AngleKey, 0));

    /// <summary>读单位字符。</summary>
    public static string UnitLabelOf(IGfxObjectRef obj) => obj.GetText(UnitKey, "N");

    /// <summary>
    /// 合力：从分矢量求和得到（模长 N、方向角）。
    /// </summary>
    /// <remarks>
    /// 分矢量一个都没找到 ⇒ 返回零矢量（调用方据此画一根最小的箭头，等用户自己删）。
    /// </remarks>
    public (double MagnitudeN, double DirectionDegrees) SumOfMembers(IGfxObjectRef obj)
    {
        var vectors = new List<(double, double)>();

        foreach (var member in SumMembers(obj, _board))
        {
            double lengthWorld = LengthWorldOf(member);
            if (lengthWorld <= 0) continue;

            vectors.Add((ToNewtonsOf(member, lengthWorld), AngleOf(member)));
        }

        return VectorMath.VectorSum(vectors);
    }

    /// <summary>取某根分矢量的"大小（N）"。存档里优先用冗余存的 magnitudeN，取不到再按长度换算。</summary>
    private static double ToNewtonsOf(IGfxObjectRef obj, double lengthWorld)
    {
        double magnitude = obj.GetNumber(MagnitudeKey, double.NaN);
        if (!double.IsNaN(magnitude) && !double.IsInfinity(magnitude) && magnitude > 0) return magnitude;

        return VectorMath.ToNewtons(lengthWorld);
    }

    /// <summary>合力的世界长度（按 1 cm = 1 N 把牛顿换回去）。用于几何与包围盒。</summary>
    public double SumLengthWorldOf(IGfxObjectRef obj)
    {
        var (magnitude, _) = SumOfMembers(obj);

        // 零矢量也要给一个最小可画长度，否则 BoundsWorld 退化成一条线、点都点不住
        double world = magnitude * VectorMath.PointsPerInch / VectorMath.CentimetersPerInch
                       / VectorMath.DefaultNewtonsPerCentimeter;

        return ArrowGeometry.ClampLength(world);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ★ 合力分支：长度与方向都<b>当场从分矢量重算</b>，不看自己存档里的 lengthWorld/angleDeg ——
    /// 那两个值写下来就过期（分矢量随时会动），读了就是"钉在原地"那个 bug。
    /// </remarks>
    public Size Measure(IGfxObjectRef obj)
    {
        double unit = UnitOf(obj);

        if (IsSum(obj))
        {
            var (magnitude, _) = SumOfMembers(obj);

            // 零合力：给一个最小尺寸，让选中框/命中区还在（不然它变成一个点、删不掉）
            if (VectorMath.IsZeroSum(magnitude)) return ArrowGeometry.Measure(ArrowGeometry.MinDragLength / unit, unit);

            return ArrowGeometry.Measure(SumLengthWorldOf(obj) / unit, unit);
        }

        return ArrowGeometry.Measure(LengthWorldOf(obj) / unit, unit);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 合力走同一个 <see cref="ArrowVisual"/>，只多两样：<b>虚线</b>与"当场算出来的长度/方向"。
    /// 不做第二个视觉类 —— 形状本来就是一样的，分叉只会让两边的 bug 各修一遍。
    /// </remarks>
    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        double unit = UnitOf(obj);

        if (IsSum(obj))
        {
            // 合力线跟随分矢量：★ 每次建视觉都重新求和，不用对象上那份过期的存档值
            var (magnitude, direction) = SumOfMembers(obj);

            double localLength = VectorMath.IsZeroSum(magnitude)
                ? ArrowGeometry.MinDragLength / unit
                : SumLengthWorldOf(obj) / unit;

            // 合力线略粗一点（×1.25），一眼能和分矢量区分开
            return new ArrowVisual(
                obj.Color,
                Math.Max(obj.LineWorldWidth, 1.0) * 1.25,
                obj.Scale,
                localLength,
                direction,
                dashed: true,
                unitWorld: unit);
        }

        return new ArrowVisual(
            obj.Color,
            obj.LineWorldWidth,
            obj.Scale,
            LengthWorldOf(obj) / unit,       // 本地几何 = 数学单位（见 UnitOf 的说明）
            AngleOf(obj),                    // ★ 方向角必须传下去，否则箭头永远朝右（回归过）
            unitWorld: unit);                // ★ 箭头三角同样要按 unit 换算（否则放大 28 倍）
    }

    /// <summary>
    /// 状态栏读数：合力走"合力 5.0 N　方向 37°"，普通箭头走"大小 6.0 N　方向 37°"。
    /// </summary>
    /// <remarks>实例方法（合力需要读画布上的分矢量，见 <see cref="_board"/>）。</remarks>
    public string Describe(IGfxObjectRef obj)
    {
        string unit = UnitLabelOf(obj);

        if (IsSum(obj))
        {
            var (magnitude, direction) = SumOfMembers(obj);
            return VectorMath.SumDescribe(magnitude, direction, unit);
        }

        return VectorMath.Describe(LengthWorldOf(obj), AngleOf(obj), unit);
    }
}

/// <summary>
/// 矢量的矢量视觉：箭杆 + 实心箭头，在本地坐标画（原点 = 箭头中点）。
/// </summary>
/// <remarks>
/// 不设 <c>RenderTransform</c>、<c>Canvas.Left/Top</c>：位姿由宿主统一施加，
/// 这里只管"以箭头中点为中心、长什么样"。绘制顺序：箭杆 → 箭头（箭头盖住箭杆末端，保证尖端利落）。
/// <para>
/// ★ <b>不画读数文字</b>（2026-09-22 用户要求）：数值在状态栏给，卷面上只留图形。
/// </para>
/// </remarks>
internal sealed class ArrowVisual : FrameworkElement
{
    private readonly Brush _ink;
    private readonly Pen _shaftPen;
    private readonly Geometry _shaft;
    private readonly Geometry _head;

    /// <param name="inkColor">墨色。</param>
    /// <param name="lineWorldWidth">世界线宽（未含缩放）。</param>
    /// <param name="scale">对象自身的缩放（本地 → 世界）。</param>
    /// <param name="lengthLocal">箭头长度（<b>本地</b>单位 = 数学单位，= 世界长 ÷ unitWorld）。</param>
    /// <param name="angleDegrees">方向角（度）—— 用于把几何转向该方向。</param>
    /// <param name="dashed">是否画虚线（合力用）。</param>
    /// <param name="unitWorld">该对象绑定的"1 数学单位 = 多少世界长"；箭头三角常量要据此换算到本地。</param>
    /// <remarks>
    /// 没有"省掉 <paramref name="angleDegrees"/>"的重载：方向角是这个工具存在的全部意义，
    /// 允许省略就等于允许"读出 37°、画出来朝右"那个 bug 悄悄回来（本插件回归过一次）。
    /// </remarks>
    internal ArrowVisual(
        Color inkColor, double lineWorldWidth, double scale,
        double lengthLocal, double angleDegrees, bool dashed = false,
        double unitWorld = 1.0)
    {
        _ink = Frozen(new SolidColorBrush(inkColor));

        // 本地几何会被宿主按 Scale 放大 ⇒ "世界线宽"换算到本地要除 Scale
        //（S4 现场"线太粗"的教训：少除这一次，线段就被画成 Scale 倍粗）。
        double safeScale = scale > 0 && !double.IsNaN(scale) && !double.IsInfinity(scale) ? scale : 1.0;
        double localStroke = (lineWorldWidth > 0 ? lineWorldWidth : 1.5) / safeScale;

        // ★★ 箭头三角也是"固定尺寸"图元，必须同样除掉 unitWorld ★★
        //   ArrowGeometry.HeadLengthWorld / HeadHalfWidthWorld 是**世界长**常量（11 / 5.5 世界点），
        //   而本视觉的几何按**数学单位**画（lengthLocal = 世界长 ÷ unitWorld）。
        //   之前直接把世界常量塞进数学坐标 ⇒ 绑定坐标系后（unitWorld ≈ 28.35）箭头三角被放大 28.35 倍：
        //   一根 5 cm 的箭头（5 数学单位）配一个 11 数学单位的三角头，
        //   shaft = 5 − 11 = −6 被 MinShaftWorld 截断 ⇒ 整个箭头只剩一个巨大的三角，
        //   现场表现就是"矢量箭头大小不对"。
        double safeUnit = unitWorld > 0 && !double.IsNaN(unitWorld) && !double.IsInfinity(unitWorld)
            ? unitWorld : 1.0;
        double headLenLocal = ArrowGeometry.HeadLengthWorld / safeUnit;
        double headHalfLocal = ArrowGeometry.HeadHalfWidthWorld / safeUnit;

        var pen = new Pen(_ink, localStroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        if (dashed)
        {
            // ★ 虚线节距也要除 Scale：否则缩放一变，虚线就变成实线（或变成看不见的密点）。
            //   与线宽同一个道理 —— "固定在屏幕上看得见的节距"必须逆着 Scale 走。
            pen.DashStyle = new DashStyle(
                new[] { ArrowRenderer.SumDashLength / safeScale, ArrowRenderer.SumDashGap / safeScale },
                0);
            pen.DashCap = PenLineCap.Flat;
        }

        _shaftPen = Frozen(pen);

        // 几何按方向角旋转 —— 这是"对象自身形状"的一部分，不是位姿（位姿由宿主矩阵管）。
        _shaft = ArrowGeometry.BuildShaft(lengthLocal, angleDegrees, headLenLocal);
        _head = ArrowGeometry.BuildHead(lengthLocal, angleDegrees, headLenLocal, headHalfLocal);

        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    /// <remarks>绘制顺序：箭杆 → 箭头。</remarks>
    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawGeometry(null, _shaftPen, _shaft);
        drawingContext.DrawGeometry(_ink, null, _head);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
