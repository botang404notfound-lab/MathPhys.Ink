using System.Windows;

namespace MathPhys.Ink.Export;

/// <summary>
/// 导出的换算真值源：world（PDF point）↔ 输出像素 ↔ PDF 页面尺寸。
/// </summary>
/// <remarks>
/// 与 <c>CanvasViewport</c>（world ↔ 视口）并列：<b>每层只有一个真值源</b>，换算不散落到调用处。
/// <para>
/// ★★ 这里有三个数，它们<b>不是一回事</b>，混用不会崩、只会让导出结果又糊又错位：
/// <list type="number">
///   <item><description><see cref="RenderDpi"/> = 200 —— 输出分辨率，用来算 <b>world → 像素</b>；</description></item>
///   <item><description><see cref="ZoomForDpi"/> = 200/96 ≈ 2.0833 —— 喂给
///     <c>IPdfDocumentService.RenderPage</c> 的 zoom（那个接口的语义是 zoom=1 ⇒ 96 dpi），
///     换来"页面位图"；</description></item>
///   <item><description><see cref="BitmapDpi"/> = 96 × 200/72 ≈ 266.67 —— 喂给
///     <c>RenderTargetBitmap</c> 构造参数，让"world 单位 1:1 等于 DIP"（视口摆在 scale=1 时）
///     而像素密度是 200 dpi。</description></item>
/// </list>
/// 三个都源自同一个 200。任何一个写错，症状都是"能跑、画面对着、就是糊 / 就是右下角被裁掉半截"，
/// 在界面上几乎看不出来 —— 所以本类全部是纯函数，由 harness 钉死。
/// </para>
/// <para>
/// 「200 dpi ⇒ 200/72 像素每 world point」：world 单位就是 PDF point（1/72 英寸）。
/// <b>不要绕道英寸</b>：<c>x / 72 * 200</c> 与 <c>x * 200 / 72</c> 数学等价，但前者容易被顺手改成
/// 2.54 之类的常数，然后把整卷试卷缩成邮票大小。
/// </para>
/// </remarks>
public static class ExportGeometry
{
    /// <summary>
    /// 输出分辨率（每英寸像素）。M9 只有这一档 —— 要改就改这里一处。
    /// </summary>
    public const double RenderDpi = 200.0;

    /// <summary>1 world 单位（PDF point）等于多少输出像素。200 dpi 下 ≈ 2.7778。</summary>
    public static double PixelsPerPoint => RenderDpi / 72.0;

    /// <summary>1 输出像素等于多少 world 单位（PDF point）。200 dpi 下 = 0.36。</summary>
    public static double PointsPerPixel => 72.0 / RenderDpi;

    /// <summary>
    /// world 尺寸 → 输出像素尺寸（四舍五入，且<b>至少 1 像素</b>）。
    /// </summary>
    /// <remarks>
    /// 兜底到 1 是为了不让 <c>RenderTargetBitmap</c> 收到 0：它的构造参数是 int，
    /// 传 0 会在构造时抛，而"某页尺寸量出来是 0"这种事只有在文档异常时才出现 ——
    /// 那种时候更该让导出走完再给老师一句人话，而不是崩在一个没人读得懂的异常上。
    /// </remarks>
    public static (int Width, int Height) PixelSize(Size worldSize)
    {
        int width = (int)Math.Round(worldSize.Width * PixelsPerPoint);
        int height = (int)Math.Round(worldSize.Height * PixelsPerPoint);
        return (Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>
    /// 喂给 <c>IPdfDocumentService.RenderPage</c> 的 zoom（该接口 zoom=1 ⇒ 96 dpi）。
    /// </summary>
    /// <remarks>
    /// 注意 <see cref="Pdf.RenderLevels"/> 那套离散档位：这里给的是<b>连续</b>值，
    /// 页面位图的实际档位由宿主按"不小于它的最小档"挑（200 dpi ⇒ 落到 3.0 档，属超采样）。
    /// 两条路各管各的：档位是为了缓存命中率，这里是"导出到底要多少像素"。
    /// </remarks>
    public static double ZoomForDpi(double dpi = RenderDpi) => dpi / 96.0;

    /// <summary>
    /// 喂给 <c>RenderTargetBitmap</c> 的 dpi（= 96 × dpi / 72）。
    /// </summary>
    /// <remarks>
    /// WPF 的 <c>RenderTargetBitmap(pxW, pxH, dpiX, dpiY, fmt)</c> 是"像素数 ÷ dpi × 96 = 逻辑尺寸(DIP)"。
    /// 我们要求它的逻辑尺寸<b>正好等于世界尺寸</b>（视口摆在 scale=1 时 1 world 单位 = 1 DIP），
    /// 于是 <c>dpiX = (200/72) × 96</c> 恰好满足 —— 像素密度 200 dpi，而 world 与 DIP 1:1。
    /// <para>
    /// 用 96 会得到 96 dpi 的图（糊）；用 200 会让逻辑尺寸变成输出的 96/200，内容被放大到超出画面、
    /// 右下角整块被裁掉。两者都不会报错。
    /// </para>
    /// </remarks>
    public static double BitmapDpi(double dpi = RenderDpi) => 96.0 * dpi / 72.0;
}
