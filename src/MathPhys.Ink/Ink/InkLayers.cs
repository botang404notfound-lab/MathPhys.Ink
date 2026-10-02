using System;
using System.Windows.Ink;
using System.Windows.Media;

namespace MathPhys.Ink.Ink;

/// <summary>
/// 图层系统 V1（M12）：批注层 / 草稿层。
/// </summary>
/// <remarks>
/// <para>
/// <b>核心决策：图层 = 每条 Stroke 的自定义属性数据，不是多集合、不是多 InkCanvas。</b>
/// 层 Id 通过 <see cref="DrawingAttributes.AddPropertyData"/> 写进笔画属性，
/// ISF 会把它<b>原样带走</b> ⇒ <c>.tbink</c> / <c>.twb</c> 格式零改动，
/// 旧文件（属性缺席）读入即默认批注层，完全向后兼容。
/// </para>
/// <para>
/// 「隐藏图层」由宿主把该层笔画移进旁路集合实现（InkCanvas 只渲染 <c>Strokes</c> 里的内容），
/// 移动必须包在 <see cref="InkHistory.Silently"/> 里 —— 显隐不是一次"编辑"，
/// 不该出现在撤销栈里；而「清空草稿层」是真实删除，走正常记录、可撤销。
/// </para>
/// </remarks>
public static class InkLayers
{
    /// <summary>层标记的属性键（写在 <see cref="DrawingAttributes"/> 的自定义属性里，ISF 原生持久化）。</summary>
    public static readonly Guid PropertyKey = new("7C3E1A92-6B4D-4F0E-9A18-5D2C4B710012");

    /// <summary>批注层 Id（默认层：讲评正式笔迹，导出必含）。</summary>
    public const string AnnotationId = "annotation";

    /// <summary>草稿层 Id（演算、试画；可整体隐藏 / 一键清空）。</summary>
    public const string DraftId = "draft";

    /// <summary>合法的层 Id 集合（宿主切换当前层时校验用）。</summary>
    public static readonly string[] AllIds = { AnnotationId, DraftId };

    /// <summary>
    /// 读一条笔画的层 Id。属性缺席（旧文件、M12 之前落的笔）一律返回批注层。
    /// </summary>
    public static string LayerOf(Stroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);

        var attributes = stroke.DrawingAttributes;
        if (attributes is null || !attributes.ContainsPropertyData(PropertyKey))
        {
            return AnnotationId;
        }

        return attributes.GetPropertyData(PropertyKey) as string ?? AnnotationId;
    }

    /// <summary>
    /// 给一条笔画打层标记（<b>只在属性缺席时写入</b> —— 同一条笔画不该被改归属，
    /// 重复调用、撤销加回等场景都靠这条"有就不动"的规则天然幂等）。
    /// </summary>
    /// <remarks>
    /// 从 ISF 读回来的笔画，其 <see cref="DrawingAttributes"/> 可能是<b>冻结</b>的
    /// （Freezable 一冻结就不许改）—— 这时换成克隆再写，原笔的几何一个字节都没动。
    /// </remarks>
    public static void MarkLayer(Stroke stroke, string layerId)
    {
        ArgumentNullException.ThrowIfNull(stroke);

        if (layerId != AnnotationId && layerId != DraftId)
        {
            throw new ArgumentException($"未知的图层 Id：{layerId}", nameof(layerId));
        }

        var attributes = stroke.DrawingAttributes;
        if (attributes is null || attributes.ContainsPropertyData(PropertyKey)) return;

        try
        {
            attributes.AddPropertyData(PropertyKey, layerId);
        }
        catch (InvalidOperationException)
        {
            // 冻结的 Freezable 不许改（ISF 读回的笔画可能整支冻结）：克隆一份再写，
            // 原笔的几何一个字节都没动。
            var clone = attributes.Clone();
            clone.AddPropertyData(PropertyKey, layerId);
            stroke.DrawingAttributes = clone;
        }
    }

    /// <summary>某层在给定集合里的笔画条数（宿主状态栏与 harness 断言用）。</summary>
    public static int CountIn(StrokeCollection strokes, string layerId)
    {
        int count = 0;
        foreach (var stroke in strokes)
        {
            if (LayerOf(stroke) == layerId) count++;
        }

        return count;
    }
}
