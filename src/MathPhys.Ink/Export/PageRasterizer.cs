using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MathPhys.Ink.Pdf;
using MathPhys.Ink.Views.Controls;

namespace MathPhys.Ink.Export;

/// <summary>
/// 单页栅格化：把画布上的一页（PDF 底图 + 图形对象 + 笔迹）渲染成一张位图。
/// </summary>
/// <remarks>
/// 与屏幕<b>共用同一条渲染链</b>（<c>CanvasViewportHost</c> 的 <c>WorldHost</c>），
/// 而不是另搭一棵离屏可视树 —— 后者等于把墨迹渲染、图形对象渲染器、页位图档位
/// 全都再实现一遍，两处一旦不同，症状就是"屏幕上好好的、导出少一层"。
/// <para>
/// ★★ 有<b>两件</b>必须做的事，少任何一件都不会报错，只会让结果错：
/// <list type="number">
///   <item><description><b>借视口 + 冻结渲染需求重算</b>（<see cref="ExportSession"/>）。
///     不做 ⇒ 视口一变，宿主就把"视口外"的页位图 <c>Source</c> 清成 <c>null</c>，
///     导出成<b>白纸</b>。</description></item>
///   <item><description><b>把该页底图临时换成按导出 dpi 渲的版本</b>
///     （<see cref="CanvasViewportHost.OverridePageImage"/>）。
///     不做 ⇒ 导出用的就是屏幕上那份位图，而屏幕上用哪个档位
///     <b>取决于老师当时的缩放</b>：缩到 25% 通览整卷时，页面位图只有 0.25 档，
///     拿它去铺 200 dpi 等于放大 8 倍 —— <b>糊</b>。
///     这种糊法还有个坏处：老师在 100% 下测试时一切正常，缩着看时才糊，像是"偶发"。</description></item>
/// </list>
/// </para>
/// <para>
/// 顺序也不能换：必须<b>先换底图、后渲染</b>；而 <c>OverridePageImage</c> 必须在会话借出之后取，
/// 否则冻结还没生效，宿主可能抢先按 1:1 视口重算一次需求、把别的页清掉。
/// </para>
/// </remarks>
public sealed class PageRasterizer
{
    private readonly CanvasViewportHost _host;
    private readonly IPdfDocumentService _pdf;

    public PageRasterizer(CanvasViewportHost host, IPdfDocumentService pdf)
    {
        _host = host;
        _pdf = pdf;
    }

    /// <summary>
    /// 借一次画布、渲一页、再还回去。整卷导出<b>不要</b>循环调它
    /// （每页借还一次会让 <c>WorldTransform</c> 反复改，屏幕可能闪），请用 <see cref="RenderPageIn"/>。
    /// </summary>
    public BitmapSource RenderPage(int pageIndex)
    {
        var pageRect = _host.Layout.GetPageRect(pageIndex);
        var freeze = _host.BeginRenderFreeze();

        try
        {
            using var session = new ExportSession(_host.Viewport, freeze, pageRect.TopLeft);
            return RenderPageIn(session, pageIndex);
        }
        catch
        {
            // 连"借"都失败时别把宿主留在冻结态（那会让画面永远不再重算需求）
            freeze.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 在<b>已借出</b>的会话里渲染指定页。本方法自己负责把视口对齐到这一页
    /// （<see cref="ExportSession.AlignTo"/> 幂等），调用方不必关心对齐细节。
    /// </summary>
    public BitmapSource RenderPageIn(ExportSession session, int pageIndex)
    {
        var pageRect = _host.Layout.GetPageRect(pageIndex);

        // 对齐到本页：1 world 单位 = 1 DIP，页面左上角落在渲染目标的 (0,0)
        session.AlignTo(pageRect.TopLeft);

        var (width, height) = ExportGeometry.PixelSize(_pdf.GetPageSize(pageIndex));
        long pixels = (long)width * height;

        if (pixels > MaxPixels)
        {
            // RenderTargetBitmap 在超大尺寸下会抛一个没人读得懂的异常（甚至直接 OOM）。
            // 与其那样，不如这里给出"哪一页、多大"的中文原因。
            throw new InvalidOperationException(
                $"第 {pageIndex + 1} 页在 {ExportGeometry.RenderDpi:F0} dpi 下需要 {width}×{height} 像素"
                + $"（约 {pixels / 1_000_000.0:F0} 百万像素），超出本版本的导出上限。");
        }

        // ★ 换底图：向 PDF 服务要一份按导出 dpi 渲的页面位图（同步、返回已 Freeze 的位图）。
        //   这一步与"屏幕档位"彻底解耦 —— 老师缩到多小都不影响导出的清晰度。
        var highRes = _pdf.RenderPage(pageIndex, ExportGeometry.ZoomForDpi());
        using var restore = _host.OverridePageImage(pageIndex, highRes);

        var surface = _host.RenderSurface;

        // ★★ 这一步不能省，否则导出图是「白底 + 空白」：
        //   上面刚把 Image.Source 换成了新位图，`InvalidateVisual` 只是把这一层标脏、
        //   把重绘排进了渲染队列；而 `RenderTargetBitmap.Render` 是**同步**取渲染数据的，
        //   它不会自己跑一遍那个队列。于是它画出来的是"上一轮"的内容：
        //   页面白底矩形（简单图元，一直在）在、而页面位图不在 —— 一张白纸。
        //   实测症状：整卷 11 张里只有"曾在屏幕上渲染过"的那两三张有内容，
        //   其余全白；而图的尺寸、格式、可解码性**全都正常**，只有内容是空的。
        if (surface is FrameworkElement element)
        {
            element.UpdateLayout();
        }

        // 屏幕上的那条链就画在 surface 上；这里不改它的属性、只借它当渲染源。
        //
        // dpi 用 BitmapDpi 而不是 200：见 ExportGeometry.BitmapDpi 的注释 ——
        // 200 会让 rtb 的"逻辑尺寸"变成 (像素数 ÷ 200 × 96)，内容被放大到超出画面、右下角整块被裁。
        var rtb = new RenderTargetBitmap(
            width, height,
            ExportGeometry.BitmapDpi(), ExportGeometry.BitmapDpi(),
            PixelFormats.Pbgra32);

        rtb.Render(surface);
        rtb.Freeze();   // Freeze 后才能安全交给编码器/别的线程

        return rtb;
    }

    /// <summary>
    /// 单页像素数上限（6400 万）。
    /// </summary>
    /// <remarks>
    /// A4 纵向 @200 dpi 是 1653×2339 ≈ 387 万像素，离上限还有一个数量级；
    /// 设它是为了拦住"异常大的页面尺寸"（坏 PDF / 尺寸单位被读错）时那种
    /// 既写不出人话、又可能把内存吃光的失败。
    /// </remarks>
    private const long MaxPixels = 64_000_000;
}
