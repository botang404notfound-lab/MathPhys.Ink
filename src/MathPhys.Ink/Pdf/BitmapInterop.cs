using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MathPhys.Ink.Pdf;

/// <summary>
/// GDI+ 位图 → WPF 位图 的桥接。
/// 之所以需要它：PdfiumViewer 的 <c>PdfDocument.Render</c> 返回 <c>System.Drawing.Image</c>（GDI+），
/// 而 WPF 渲染树只吃 <see cref="BitmapSource"/>。这一步是把 PDF 拉进 WPF 变换管线的唯一接缝。
/// </summary>
internal static class BitmapInterop
{
    /// <summary>
    /// 把 GDI+ 位图复制成已 Freeze 的 <see cref="BitmapSource"/>。
    /// </summary>
    /// <remarks>
    /// 必须是<b>复制</b>而非包裹：GDI+ 位图的生命周期由调用方 using 管理，
    /// 若只包裹其内存指针，位图释放后 WPF 侧会读到野指针。
    /// Freeze 则是为了能跨线程访问（后续把渲染挪到后台线程时必需）。
    /// </remarks>
    public static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect,
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // Format32bppArgb 在内存中即 BGRA 排列，与 PixelFormats.Bgra32 一致
            var source = BitmapSource.Create(
                bitmap.Width,
                bitmap.Height,
                96, 96,
                PixelFormats.Bgra32,
                palette: null,
                data.Scan0,
                data.Stride * bitmap.Height,
                data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
