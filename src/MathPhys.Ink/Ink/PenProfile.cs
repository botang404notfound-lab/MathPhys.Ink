using System.Windows.Ink;
using System.Windows.Media;

namespace MathPhys.Ink.Ink;

/// <summary>
/// 笔的属性：颜色 + <b>世界单位</b>的笔宽。
/// </summary>
/// <remarks>
/// 关键语义：<see cref="DrawingAttributes.Width"/> 的单位就是<b>墨迹坐标系单位</b>，
/// 而墨迹坐标系 ≡ 世界坐标系 ≡ PDF point。因此笔宽会跟着画布缩放一起变粗 ——
/// 这是"墨落在纸上"的正确语义（OneNote / Word 的手写同理），不是 bug。
/// <para>
/// 反过来，如果把笔宽当成屏幕像素写死，同一笔画在不同缩放下会变成不同粗细的几何，
/// 那缩放时就必须重算笔迹 ⇒ 直接推翻 M3 的核心命题（笔迹几何永不重算）。
/// </para>
/// </remarks>
public sealed class PenProfile
{
    /// <summary>
    /// 出厂墨色。
    /// </summary>
    /// <remarks>
    /// ★ 取值来自 <see cref="InkPalette"/> 标着"默认"的那一档，而不是这里再写一遍
    /// <c>Colors.Red</c>：两处各写一份的话，"把默认笔换成蓝色"这种事就必须记得改两个地方，
    /// 而忘了其中一处的表现是"界面按钮高亮在蓝、落笔却是红的"。
    /// harness 另有一条断言把这两者钉在一起。
    /// </remarks>
    public Color Color { get; set; } = InkPalette.Colors[InkPalette.DefaultColorIndex].Color;

    /// <summary>笔宽，单位 <b>world = PDF point</b>。同样取自调色板的默认档。</summary>
    /// <remarks>
    /// ★ M21：这里原本写死 <c>1.5</c>，等于把"默认笔宽"在 <see cref="InkPalette"/> 和自己身上
    /// 各说了一遍 —— 改了默认档而忘了这一处，表现是"工具栏高亮在「细」，落笔却是「中」"。
    /// 换成引用之后，harness 那条"出厂默认 = 调色板默认档"的断言才真正覆盖到笔宽。
    /// </remarks>
    public double WorldWidth { get; set; } = InkPalette.Widths[InkPalette.DefaultWidthIndex].WorldWidth;

    /// <summary>是否平滑拟合（写字手感更好）。</summary>
    public bool FitToCurve { get; set; } = true;

    /// <summary>是否忽略压感。M3 不做压感，压感是 M4 的正题。</summary>
    public bool IgnorePressure { get; set; } = true;

    /// <summary>
    /// 转成 WPF 的 <see cref="DrawingAttributes"/>。每次返回<b>新实例</b>，
    /// 便于调用方（例如验收 harness）单独改色而不影响笔的默认设置。
    /// </summary>
    public DrawingAttributes ToDrawingAttributes() => new()
    {
        Color = this.Color,
        Width = WorldWidth,
        // 圆头笔尖要求宽高相等，否则会画出椭圆笔尖
        Height = WorldWidth,
        FitToCurve = FitToCurve,
        IgnorePressure = IgnorePressure,
        StylusTip = StylusTip.Ellipse,
    };
}
