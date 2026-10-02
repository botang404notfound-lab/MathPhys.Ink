using System.Windows.Controls;
using System.Windows.Ink;

namespace MathPhys.Ink.Ink;

/// <summary>
/// 橡皮属性：擦除半径 + 擦除方式。
/// </summary>
/// <remarks>
/// 半径用 <b>world 单位 = PDF point</b>，与笔宽同一套语义：放大画布时橡皮在屏幕上也同步变大，
/// "擦掉纸上一块"这个等价关系才成立。
/// <para>
/// 反过来，若把半径当成屏幕像素写死，放大后橡皮只能擦掉笔画的一小段，老师会以为"擦不动"；
/// 缩小后一擦又擦掉半页。这类错误在 M4 之后极难回查（要重新打开 PDF 才能复现），
/// 所以 harness 专门断言 <see cref="ToStylusShape"/> 的直径 = 半径 × 2。
/// </para>
/// </remarks>
public sealed class EraserProfile
{
    /// <summary>点擦半径，world 单位。10 pt ≈ 3.5 mm。</summary>
    public double WorldRadius { get; set; } = 10.0;

    /// <summary>擦除方式。M4 固定点擦（擦到哪里是哪里）；"整笔擦"留作后续档位。</summary>
    public InkCanvasEditingMode Mode { get; set; } = InkCanvasEditingMode.EraseByPoint;

    /// <summary>
    /// 转成 WPF 的橡皮形状。
    /// </summary>
    /// <remarks>
    /// 注意 <see cref="StylusShape.Width"/> 是<b>直径</b>（与 <c>DrawingAttributes.Width</c> 同为
    /// "笔尖粗细"语义，默认值就是 8×8），所以要乘 2，否则橡皮只有设定大小的一半。
    /// </remarks>
    public StylusShape ToStylusShape()
        => new EllipseStylusShape(WorldRadius * 2.0, WorldRadius * 2.0);
}
