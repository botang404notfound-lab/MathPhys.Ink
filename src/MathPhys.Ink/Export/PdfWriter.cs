using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace MathPhys.Ink.Export;

/// <summary>
/// 把"每一页的图片"拼成一份多页 PDF（PdfSharpCore 适配层）。
/// </summary>
/// <remarks>
/// <b>为什么进 PDF 的是栅格图而不是矢量批注</b>：矢量复刻要把笔迹、七个学科工具图形
/// 各写一遍 PDF 绘制代码，等于把渲染链在第二个后端上再实现一次（铁律 1「不重写墨迹引擎」）。
/// 栅格贴图换来的是「导出所见 = 屏幕所见」这一条铁保证 —— 而老师要的正是这个。
/// <para>
/// <b>入参为什么是"已编码的字节"而不是 <c>BitmapSource</c></b>：
/// <list type="number">
///   <item><description>本类因此<b>不依赖 WPF</b>，harness 里不用开窗口就能验它（造两张小图即可）；</description></item>
///   <item><description>图像编码只有一处（<see cref="PngWriter.Encode"/>）——
///     否则"导出的 PNG 清晰、塞进 PDF 里那张糊"这种分叉迟早出现。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>本类是全工程唯一碰 PdfSharpCore 的文件</b>。将来换写库（或要加"导出带书签的 PDF"）
/// 只动这里，UI 与断言一行都不用改。
/// </para>
/// </remarks>
public static class PdfWriter
{
    /// <summary>基名为空时的兜底文件名。</summary>
    public const string FallbackBaseName = "导出试卷";

    /// <summary>
    /// 一页 PDF 的来源：图片字节 + 这一页的<b>物理尺寸（PDF point）</b>。
    /// </summary>
    /// <remarks>
    /// 尺寸用的是 world 尺寸（= PDF point），<b>不是像素</b>：
    /// PDF 里页面大小是"多少分之一英寸"，像素只决定这张图有多清晰。
    /// 传像素进去会让一页 A4 变成 1654×2339 <b>point</b> 的巨幅页面（约 58×82 厘米），
    /// 打印出来一页装不下、也不会报错 —— 只有拿到实物才看得出来。
    /// </remarks>
    /// <param name="Encoded">页面图片的编码字节（PNG）。</param>
    /// <param name="WidthPoints">页宽（PDF point）。</param>
    /// <param name="HeightPoints">页高（PDF point）。</param>
    public readonly record struct PageImage(byte[] Encoded, double WidthPoints, double HeightPoints);

    /// <summary>
    /// 整卷 PDF 的文件名：<c>基名.pdf</c>。
    /// </summary>
    /// <remarks>
    /// 不补页号：整卷是<b>一个</b>文件，页号在 PDF 内部。
    /// 这里只做空值兜底与去空格，非法字符清洗仍由界面层的 <c>SafeFileName</c> 负责（同 <see cref="PngWriter"/>）。
    /// </remarks>
    public static string FileName(string? baseName)
    {
        string name = string.IsNullOrWhiteSpace(baseName) ? FallbackBaseName : baseName.Trim();
        return name + ".pdf";
    }

    /// <summary>
    /// 逐页取图、拼成 PDF、落盘，返回写出的字节数。
    /// </summary>
    /// <param name="path">目标文件路径（已存在则覆盖）。</param>
    /// <param name="pageCount">页数。</param>
    /// <param name="pageProducer">
    /// 第 N 页（0 起）的来源。**由调用方在"画布借出"的会话里逐页生成** ——
    /// 本类刻意不碰画布，它只管 PDF 结构。
    /// </param>
    /// <param name="onPageDone">每写完一页回调 <c>(已完成页数, 总页数)</c>（界面反馈用）。</param>
    /// <param name="title">写进 PDF 元数据的标题（老师的试卷名）；空则用兜底名。</param>
    /// <remarks>
    /// <b>内存口径要说实话</b>：因为是<b>延迟读图</b>（见下），每页那串 PNG 字节要到 <c>Save</c> 之后
    /// 才可能被释放，所以峰值 ≈ 全部页面的编码字节 + PDF 结构。整卷 30 页约 60 MB，
    /// 这是可接受的；真正的风险不在这些字节，而在<b>解码后的图</b>
    /// （单页 1654×2339 解成位图约 15 MB，30 页就是 450 MB）——
    /// 所以这里绝不去"提前把所有图都解码出来校验一遍"，那是把内存翻十倍换一个肉眼能看出的东西。
    /// <para>
    /// <b>页面方向要先判再设</b>：PdfSharpCore 的 <c>PdfPage.Width/Height</c> 是按
    /// <c>Orientation</c> 解释的（横向页面下 Width 指长边）。先按长宽把 Orientation 设对，
    /// 再设 Width/Height，横向卷子才不会在 PDF 里被转成竖的。
    /// </para>
    /// </remarks>
    public static long Write(string path, int pageCount, Func<int, PageImage> pageProducer,
                             Action<int, int>? onPageDone = null, string? title = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        ArgumentNullException.ThrowIfNull(pageProducer);

        // ★ 这里的图在 Save 之前不能释放、也不该指望"流已经被人读过一遍"：
        //   PdfSharpCore 的 `XImage.FromStream` 收的是 `Func<Stream>`（**开流工厂**，不是流本身），
        //   真正的字节是在 prepare/save 阶段才去读的 —— 这正是它要工厂的原因（同一张图可能被读多次，
        //   每次都得是一条干净的新流，否则第二次读到的是已到结尾的流 ⇒ 图片静默丢失）。
        //   所以：① 工厂每次都新建流；② XImage 攒到 Save 之后再释放。
        var images = new List<XImage>(pageCount);

        try
        {
            using var document = new PdfDocument();

            // 元数据：老师把 PDF 发出去/存档后，靠这一栏还能认出是哪份卷子
            document.Info.Title = string.IsNullOrWhiteSpace(title) ? FallbackBaseName : title.Trim();
            document.Info.Creator = "数理墨";

            for (int i = 0; i < pageCount; i++)
            {
                var source = pageProducer(i);

                var page = document.AddPage();

                // ★ 先定方向、再给长宽：见方法注释里"页面方向要先判再设"。
                bool landscape = source.WidthPoints > source.HeightPoints;
                page.Orientation = landscape
                    ? PdfSharpCore.PageOrientation.Landscape
                    : PdfSharpCore.PageOrientation.Portrait;
                page.Width = XUnit.FromPoint(source.WidthPoints);
                page.Height = XUnit.FromPoint(source.HeightPoints);

                var image = XImage.FromStream(
                    () => new MemoryStream(source.Encoded, writable: false));
                images.Add(image);

                using (var gfx = XGraphics.FromPdfPage(page))
                {
                    // 铺满整页：图片是照 200 dpi 渲的（像素 = point × 200/72），
                    // 按 point 尺寸铺回去 ⇒ 打印出来正好是原尺寸，不是"缩放过的截图"。
                    gfx.DrawImage(image, 0, 0, source.WidthPoints, source.HeightPoints);
                }

                onPageDone?.Invoke(i + 1, pageCount);
            }

            document.Save(path);
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }

        // 回读实际长度（同 PngWriter：不拿估算值冒充）
        return new FileInfo(path).Length;
    }
}
