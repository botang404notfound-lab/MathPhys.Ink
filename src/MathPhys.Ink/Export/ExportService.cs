using System.Windows;
using System.Windows.Media.Imaging;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Pdf;
using MathPhys.Ink.Views.Controls;

namespace MathPhys.Ink.Export;

/// <summary>
/// 导出编排门面：UI 只跟它打交道，<b>不直接碰 PdfSharpCore</b>。
/// </summary>
/// <remarks>
/// 分工：本类负责"什么时候能导、借画布、把结果翻译成一句人话"；
/// 具体怎么把一页画成位图、怎么把位图拼成 PDF，在 <c>PageRasterizer</c> / <c>PngWriter</c> /
/// <c>PdfWriter</c> 里（S1 / S2 落地）。把 PdfSharpCore 关在这一层之后，
/// 将来换写库只需要动 <c>PdfWriter</c>，UI 与断言一行都不用改。
/// <para>
/// 本类同时是<b>唯一</b>允许改动视口的地方（通过 <see cref="BeginRasterSession"/>）——
/// "导出改视口"这件事只有一处入口，才可能保证它每次都被还回去。
/// </para>
/// </remarks>
public sealed class ExportService
{
    private readonly CanvasViewportHost _host;
    private readonly IPdfDocumentService _pdf;
    private readonly PageRasterizer _rasterizer;

    public ExportService(CanvasViewportHost host, IPdfDocumentService pdf)
    {
        _host = host;
        _pdf = pdf;
        _rasterizer = new PageRasterizer(host, pdf);
    }

    /// <summary>输出分辨率（转发自 <see cref="ExportGeometry"/>，让 UI 只认一个门面）。</summary>
    public static double RenderDpi => ExportGeometry.RenderDpi;

    /// <summary>当前文档页数；没有文档时为 0。</summary>
    public int PageCount => _pdf.IsOpen ? _pdf.PageCount : 0;

    /// <summary>
    /// 现在能不能导出（按钮的 <c>CanExecute</c> 绑这个）。
    /// </summary>
    /// <remarks>
    /// 无文档时按钮直接置灰 —— 导出是"对当前试卷做的事"，没有试卷就没有这个动作，
    /// 让老师点了再告诉他"请先打开试卷"是多余的来回。
    /// </remarks>
    public bool CanExport => !IsBusy && _pdf.IsOpen && _pdf.PageCount > 0;

    /// <summary>
    /// 是否正在导出。
    /// </summary>
    /// <remarks>
    /// 唯一用途是<b>防双击</b>：导出整卷要几秒，这期间按钮必须置灰，
    /// 否则第二次点击会开出第二条导出流程，两条流程会各自借用一次视口、各自写一次文件。
    /// M9 不实现"取消导出"（范围外）。
    /// </remarks>
    public bool IsBusy { get; private set; }

    /// <summary>取某页在 world 坐标里的矩形（导出对齐用）。</summary>
    public Rect GetPageWorldRect(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                $"页号超出范围（当前文档共 {PageCount} 页）");
        }

        return _host.Layout.GetPageRect(pageIndex);
    }

    /// <summary>取某页的输出像素尺寸（弹"确实要导出这么大吗"之类提示时用）。</summary>
    public (int Width, int Height) GetPagePixelSize(int pageIndex)
        => ExportGeometry.PixelSize(_pdf.GetPageSize(pageIndex));

    /// <summary>
    /// 把当前试卷的每一页导出成 PNG（每页一张），落到 <paramref name="directory"/>。
    /// </summary>
    /// <remarks>
    /// <b>整卷只借一次画布</b>（借出后逐页 <c>AlignTo</c>），而不是每页借还一次 —— 理由见
    /// <see cref="ExportSession.AlignTo"/>。
    /// <para>
    /// 中途某页失败就<b>整体失败并说清是第几页</b>（先前写出的文件留在磁盘上）：
    /// 半份导出物比没有更危险 —— 老师可能直接拿它去打印，而缺的那几页没人会去数。
    /// 所以宁可明确报"第 3 页失败"，也不返回一个"部分成功"。
    /// </para>
    /// <para>
    /// 不在这里弹任何对话框：目录由界面层选好传进来，本方法就能在 harness 里直接跑。
    /// </para>
    /// <para>
    /// <paramref name="onPageDone"/> 每写完一页回调一次 <c>(已完成页数, 总页数)</c>。
    /// 它存在的唯一理由是<b>界面反馈</b>：整卷导出要几秒，中间不给反馈就与死机无异
    /// （老师会再点一次、或者干脆强杀进程）。回调里不做耗时的事 —— 它在渲染循环中间执行。
    /// </para>
    /// </remarks>
    public ExportResult ExportPngTo(string directory, string? baseName,
                                    Action<int, int>? onPageDone = null)
    {
        if (IsBusy) return ExportResult.Failure("正在导出，请稍候。");

        if (!CanExport)
        {
            return ExportResult.Failure("当前没有打开试卷，无法导出。");
        }

        IsBusy = true;
        try
        {
            // 目录不存在就先建：对话框选出来的目录一定存在，但环境变量钩子
            // （与将来的"导出到上次用过的位置"）给的路径未必 —— 让它在写第一个文件时才炸，
            // 报告出来会是"写入第 1 页失败：找不到路径的一部分"，读起来像程序坏了。
            try
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                return ExportResult.Failure($"无法创建导出目录（{directory}）：{ex.Message}");
            }

            int count = PageCount;
            long total = 0;
            long largest = 0;   // 单页最大体积（"文件较大"提示按它判，而不是按整批的总和）

            using (var session = BeginRasterSession(0))
            {
                for (int i = 0; i < count; i++)
                {
                    BitmapSource bitmap;
                    try
                    {
                        bitmap = _rasterizer.RenderPageIn(session, i);
                    }
                    catch (Exception ex)
                    {
                        return ExportResult.Failure($"导出第 {i + 1} 页时失败：{ex.Message}");
                    }

                    string path = System.IO.Path.Combine(directory, PngWriter.FileName(baseName, i, count));

                    try
                    {
                        long bytes = PngWriter.Write(path, bitmap);
                        total += bytes;
                        if (bytes > largest) largest = bytes;
                    }
                    catch (Exception ex)
                    {
                        return ExportResult.Failure(
                            $"写入第 {i + 1} 页失败（{System.IO.Path.GetFileName(path)}）：{ex.Message}");
                    }

                    onPageDone?.Invoke(i + 1, count);
                }
            }

            AppLog.Info($"[导出] PNG {count} 页 → {directory}（共 {ExportSizes.Format(total)}）");

            // 大文件提示只加不改：不大时是空串，消息与从前一字不差
            string hint = ExportSizes.LargeHint(largest);

            string message = count == 1
                ? $"已导出 1 张 PNG 到 {directory}（{ExportSizes.Format(total)}）{hint}"
                : $"已导出 {count} 张 PNG 到 {directory}（共 {ExportSizes.Format(total)}）{hint}";

            return ExportResult.Success(message, directory, total);
        }
        catch (Exception ex)
        {
            // 这里兜的是"连画布都借不出来"之类的意外；逐页失败上面已经都翻成人话了
            AppLog.Warn($"[导出] PNG 导出失败：{ex}");
            return ExportResult.Failure("导出失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把当前试卷导出成<b>一份多页 PDF</b>（每页一张栅格图）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ExportPngTo"/> 共用同一套"借画布 → 逐页栅格化"的流程，区别只在产物形态：
    /// 那边是"每页一个文件"，这边是"整卷一个文件"。所以画布只借<b>一次</b>，
    /// 逐页 <c>AlignTo</c> —— 理由同 <see cref="ExportSession.AlignTo"/>。
    /// <para>
    /// <b>先渲完、再拼吗？不是。</b>这里是"渲一页 → 编码 → 立刻交给 PDF → 丢掉字节"，
    /// 峰值内存只多一页。整卷 30 页时，若把 30 页字节都攒在手里再拼，
    /// 会和 PDF 自己的渲染抢内存（一体机内存本来就不宽裕）。
    /// </para>
    /// <para>
    /// 失败口径与 PNG 那边一致：<b>说出是第几页</b>，不返回"部分成功"。
    /// 半份 PDF 比没有更危险 —— 老师会直接拿它去打印，缺的页没人会去数。
    /// </para>
    /// </remarks>
    /// <param name="path">目标文件路径（含文件名，已存在则覆盖）。</param>
    /// <param name="baseName">写进 PDF 元数据的标题；空则用兜底名。</param>
    /// <param name="onPageDone">每完成一页回调 <c>(已完成页数, 总页数)</c>。</param>
    public ExportResult ExportPdfTo(string path, string? baseName = null,
                                    Action<int, int>? onPageDone = null)
    {
        if (IsBusy) return ExportResult.Failure("正在导出，请稍候。");

        if (!CanExport)
        {
            return ExportResult.Failure("当前没有打开试卷，无法导出。");
        }

        IsBusy = true;
        try
        {
            // 目录不存在就先建（另存对话框给的目录一定存在；环境变量钩子给的未必）。
            // 与 PNG 那边同因：让它在写文件时才炸，报告出来会是"找不到路径的一部分"，读起来像程序坏了。
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                return ExportResult.Failure($"无法创建导出目录（{path}）：{ex.Message}");
            }

            int count = PageCount;
            long bytes;

            try
            {
                using (var session = BeginRasterSession(0))
                {
                    bytes = PdfWriter.Write(
                        path,
                        count,
                        i =>
                        {
                            // 每页：栅格化（含换 200dpi 底图）→ 编码成 PNG 字节 → 交给 PDF。
                            // 包一层是为了把"第几页失败"钉进消息里 —— PdfWriter 不知道页号以外的事，
                            // 而老师需要知道的正是"卡在哪一页"。
                            try
                            {
                                var bitmap = _rasterizer.RenderPageIn(session, i);
                                var size = _pdf.GetPageSize(i);
                                return new PdfWriter.PageImage(PngWriter.Encode(bitmap), size.Width, size.Height);
                            }
                            catch (Exception ex)
                            {
                                throw new ExportPageException($"导出第 {i + 1} 页时失败：{ex.Message}", ex);
                            }
                        },
                        onPageDone,
                        baseName);
                }
            }
            catch (ExportPageException ex)
            {
                return ExportResult.Failure(ex.Message);
            }

            AppLog.Info($"[导出] PDF {count} 页 → {path}（{ExportSizes.Format(bytes)}）");

            string message = $"已导出 {count} 页到 {PdfWriter.FileName(baseName)}"
                           + $"（{ExportSizes.Format(bytes)}）{ExportSizes.LargeHint(bytes)}";

            return ExportResult.Success(message, path, bytes);
        }
        catch (Exception ex)
        {
            // 兜的是"连画布都借不出来"、PdfSharpCore 内部异常、磁盘满/只读/无权限等；
            // 逐页失败上面已经翻成人话了
            AppLog.Warn($"[导出] PDF 导出失败：{ex}");
            return ExportResult.Failure("导出失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 借出画布做离屏渲染。用 <c>using</c> 包住，离开作用域自动还原视口。
    /// </summary>
    /// <remarks>
    /// 冻结令牌在<b>动视口之前</b>取 —— 顺序写反的话，改视口排下的那次"重算渲染需求"
    /// 有一小段窗口期可以抢在冻结生效前落地（它排在 Background 优先级，看着像异步，
    /// 实际什么时候跑由调度器决定）。这类窗口期 bug 在本机几乎复现不出来，
    /// 只能靠把顺序写死来避开。
    /// </remarks>
    public ExportSession BeginRasterSession(int pageIndex)
    {
        var pageRect = GetPageWorldRect(pageIndex);

        var freeze = _host.BeginRenderFreeze();
        try
        {
            return new ExportSession(_host.Viewport, freeze, pageRect.TopLeft);
        }
        catch
        {
            // 连"借"都失败时别把宿主留在冻结态 —— 那会让画面永远不再重算需求
            freeze.Dispose();
            throw;
        }
    }
}
