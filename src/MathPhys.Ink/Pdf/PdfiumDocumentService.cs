using System.Windows;
using System.Windows.Media.Imaging;
using PdfiumViewer;

namespace MathPhys.Ink.Pdf;

/// <summary>
/// 基于 PdfiumViewer（Bluegrams fork）的实现。
/// <b>整个工程里唯一引用 PdfiumViewer 的文件</b>，换渲染器时只动这里。
/// </summary>
/// <remarks>
/// 刻意只使用 <c>PdfDocument</c>（纯计算类），<b>不使用</b> <c>PdfRenderer</c> / <c>PdfViewer</c>
/// 那两个 WinForms 控件——它们是独立 HWND，不参与 WPF 矩阵变换，接进来会让 PDF 与笔迹错层。
/// 详见设计方案 §3.4。
/// <para>
/// <b>线程模型</b>：<c>PdfDocument</c> 自身非线程安全，本类用<b>一把锁</b>把所有原生调用串起来，
/// 使「UI 线程调 Open/GetPageSize」与「渲染线程调 RenderPage」互斥。
/// 代价：UI 线程在渲染进行中调 <see cref="GetPageSize"/> 最多等一页的渲染时间（几十~几百毫秒）。
/// 这只发生在打开文档的瞬间，可以接受；换来的是换文档时<b>不需要</b>阻塞等待渲染线程退出。
/// </para>
/// </remarks>
public sealed class PdfiumDocumentService : IPdfDocumentService
{
    private readonly object _gate = new();

    private PdfDocument? _document;
    private string? _filePath;

    public bool IsOpen
    {
        get { lock (_gate) return _document is not null; }
    }

    public string? FilePath
    {
        get { lock (_gate) return _filePath; }
    }

    public int PageCount
    {
        get { lock (_gate) return _document?.PageCount ?? 0; }
    }

    public void Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_gate)
        {
            // 先关旧文档：PdfDocument 持有原生句柄，不释放会漏 GDI 句柄
            _document?.Dispose();
            _document = PdfDocument.Load(filePath);
            _filePath = filePath;
        }
    }

    public Size GetPageSize(int pageIndex)
    {
        lock (_gate)
        {
            var document = _document ?? throw new InvalidOperationException("尚未打开 PDF 文档。");
            ValidatePageIndex(document, pageIndex);

            // PdfiumViewer 的 PageSizes 单位就是 PDF point（1/72 inch）
            var size = document.PageSizes[pageIndex];
            return new Size(size.Width, size.Height);
        }
    }

    public BitmapSource RenderPage(int pageIndex, double zoom)
    {
        if (zoom <= 0) throw new ArgumentOutOfRangeException(nameof(zoom), zoom, "缩放必须为正数。");

        lock (_gate)
        {
            var document = _document ?? throw new InvalidOperationException("尚未打开 PDF 文档。");
            ValidatePageIndex(document, pageIndex);

            var size = document.PageSizes[pageIndex];

            // 换算推导：
            //   1 world 单位 = 1 PDF point = 1/72 inch
            //   zoom=1 时希望 1 world 单位占 1 DIP = 1/96 inch
            //   ⇒ 目标 dpi = 96 * zoom；像素 = point * dpi / 72
            double dpi = 96.0 * zoom;
            int pixelWidth = Math.Max(1, (int)Math.Round(size.Width * dpi / 72.0));
            int pixelHeight = Math.Max(1, (int)Math.Round(size.Height * dpi / 72.0));

            // 用 using 确保 GDI+ 图像被释放（Render 返回的是非托管图像）
            using var image = document.Render(pageIndex, pixelWidth, pixelHeight, (float)dpi, (float)dpi, false);
            using var bitmap = new System.Drawing.Bitmap(image);
            return BitmapInterop.ToBitmapSource(bitmap);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _document?.Dispose();
            _document = null;
            _filePath = null;
        }
    }

    public void Dispose() => Close();

    private static void ValidatePageIndex(PdfDocument document, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= document.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                $"页索引越界，文档共 {document.PageCount} 页。");
        }
    }
}
