using System.Windows;
using System.Windows.Media.Imaging;

namespace MathPhys.Ink.Pdf;

/// <summary>
/// PDF 文档服务抽象。把 PdfiumViewer 的依赖完全隔离在实现里，将来换解析器不动上层。
/// </summary>
/// <remarks>
/// 重要约束：
/// 1. 底层 <c>PdfDocument</c> 非线程安全。<b>实现方负责保证串行安全</b>——
///    即允许多个线程调用，但内部自行串行化（例如加锁）。
///    调用方因此可以在 UI 线程读页尺寸、在渲染线程渲页，而不必自己做外部同步。
/// 2. 页面尺寸与渲染结果都用 world 单位（PDF point，1/72 inch）表述，
///    让上层永远不必接触像素或 DPI。
/// 3. 页索引统一 <b>0-based</b>，UI 上显示 1-based 时自行 +1。
/// 4. <see cref="RenderPage"/> 是<b>耗时操作</b>（高倍档位可达数百毫秒），
///    不得在 UI 线程直接调用，应交给 <see cref="PageRenderScheduler"/>。
/// </remarks>
public interface IPdfDocumentService : IDisposable
{
    bool IsOpen { get; }

    string? FilePath { get; }

    int PageCount { get; }

    /// <summary>取页面尺寸，单位 world 单位（PDF point）。</summary>
    Size GetPageSize(int pageIndex);

    /// <summary>
    /// 按指定缩放渲染一页。
    /// <para><paramref name="zoom"/>=1 的含义是「1 world 单位 → 1 DIP」（等价 96 dpi），
    /// 与 <c>CanvasViewport.Scale</c> 同一套语义，可直接相乘。</para>
    /// 返回的 <see cref="BitmapSource"/> 已 Freeze，可安全跨线程传递。
    /// </summary>
    BitmapSource RenderPage(int pageIndex, double zoom);

    void Open(string filePath);

    void Close();
}
