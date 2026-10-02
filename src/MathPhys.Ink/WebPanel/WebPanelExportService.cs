using System;
using System.Collections.Generic;
using System.Windows;
using MathPhys.Ink.Export;
using MathPhys.Ink.Gfx;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.WebPanel;

/// <summary>
/// 一次导出的结果。
/// </summary>
/// <param name="Ok">是否真的落到了卷面上。</param>
/// <param name="Message">给用户看的一句话（成功时也写，状态栏要用）。</param>
/// <param name="ObjectId">落成的图形对象 Id；失败时为 <c>null</c>。</param>
public sealed record WebPanelExportResult(bool Ok, string Message, string? ObjectId);

/// <summary>
/// 把仿真面板的画面<b>搬到卷面上</b> —— 面板与图形对象层之间的唯一一段胶水。
/// </summary>
/// <remarks>
/// <para>
/// 它存在的理由是一条硬约束：WebView2 是 HWND 子窗口，画面<b>不进</b> WPF 渲染树
/// （见 <c>docs/25</c> §2 的"空气空间"），所以"面板上的东西"与"卷面上的东西"之间
/// 没有任何自动通路 —— 必须有人去抓帧、量比例、算落点、落成一个图形对象。那就是这个类。
/// </para>
/// <para>
/// <b>为什么依赖全是委托而不是直接抓窗口/视口。</b>落点计算要问"当前页在哪、视口中心在哪"，
/// 这两件事每台机器、每个窗口尺寸都不一样，直接读全局只会变成"只能靠真机肉眼验"。
/// 委托进来之后，落点是一条<b>纯函数</b>（<see cref="ComputePlacement"/>），
/// harness 可以拿几十组页面/视口组合把边界打穿。
/// </para>
/// <para>
/// <b>落成对象之后不关面板</b>：关不关是界面的事（见 <c>MainWindow</c>）。本类只负责
/// "这次导出成不成、为什么不成"，一件事。
/// </para>
/// </remarks>
public sealed class WebPanelExportService
{
    /// <summary>导出的图片默认占页面宽度的比例。</summary>
    /// <remarks>
    /// 0.46 是"一眼能看清细节、又不至于把整道题盖住"的量：题目通常占页宽的一半以上，
    /// 截图再宽就会把题干糊上，而老师接下来的动作往往是把截图拖到题目旁边 —— 起始尺寸
    /// 小于目标尺寸的话，还得先放大一次，那是纯浪费。
    /// </remarks>
    public const double DefaultPageWidthFraction = 0.46;

    /// <summary>图片最多占页高的比例（避免竖长的截图顶到页外）。</summary>
    public const double MaxPageHeightFraction = 0.56;

    /// <summary>图片外沿离页边的留白（世界单位，约 4 mm）。</summary>
    public const double PageMarginWorld = 12.0;

    private readonly IWebPanelBridge _panel;
    private readonly WebPanelImageStore _images;

    /// <summary>取"视口中心所在的那一页"的页面矩形；没有试卷时返回 <c>null</c>。</summary>
    private readonly Func<Rect?> _currentPage;

    /// <summary>取视口中心的世界坐标。</summary>
    private readonly Func<Point> _viewportCenterWorld;

    /// <summary>把一个位图对象落成图形对象，返回它的 Id（失败时 <c>null</c>）。</summary>
    private readonly Func<GfxDraft, string?> _place;

    public WebPanelExportService(
        IWebPanelBridge panel,
        WebPanelImageStore images,
        Func<Rect?> currentPage,
        Func<Point> viewportCenterWorld,
        Func<GfxDraft, string?> place)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _currentPage = currentPage ?? throw new ArgumentNullException(nameof(currentPage));
        _viewportCenterWorld = viewportCenterWorld ?? throw new ArgumentNullException(nameof(viewportCenterWorld));
        _place = place ?? throw new ArgumentNullException(nameof(place));
    }

    /// <summary>
    /// 抓一次画面并落到卷面上。
    /// </summary>
    /// <remarks>
    /// 全程<b>不抛异常</b>：导出失败只该是"没导出成功"（状态栏一句话），
    /// 不该让白板崩在教室的投影上。
    /// </remarks>
    public async System.Threading.Tasks.Task<WebPanelExportResult> ExportAsync(
        double pageWidthFraction = DefaultPageWidthFraction)
    {
        try
        {
            byte[]? png = await _panel.CapturePngAsync().ConfigureAwait(true);

            if (png is not { Length: > 0 })
            {
                return new WebPanelExportResult(false,
                    "没能抓到这个仿真画面 —— 面板刚打开时第一帧还没画出来，稍等一下再点「导出到白板」",
                    null);
            }

            var page = _currentPage();
            if (page is not { } rect || rect.Width <= 0 || rect.Height <= 0)
            {
                return new WebPanelExportResult(false,
                    "还没有打开的试卷，仿真画面没处可放 —— 先打开一份试卷再导出", null);
            }

            PngSize.TryReadOrRatio(png, out int pixelWidth, out int pixelHeight);

            // 先按页宽定，再按页高压一次 —— 两道都只管"多大"，
            // 长宽比从头到尾由像素尺寸说了算（拉伸会把圆画成椭圆）。
            var size = ClampToPageHeight(
                WebPanelImageRenderer.WorldSizeFor(
                    pixelWidth, pixelHeight, rect.Width * pageWidthFraction),
                rect);

            var center = ComputePlacement(rect, _viewportCenterWorld(), size);

            string imageId = _images.Add(png);
            if (imageId.Length == 0)
            {
                return new WebPanelExportResult(false, "抓到的画面是空的，没有可导出的内容", null);
            }

            var draft = new GfxDraft
            {
                Kind = WebPanelImageRenderer.KindName,
                Center = center,
                Color = GfxObjectData.DefaultColorValue,
                LineWorldWidth = 1.5,   // 位图对象用不到线宽，给个合法值即可
                Numbers = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    [WebPanelImageRenderer.WidthKey] = size.Width,
                    [WebPanelImageRenderer.HeightKey] = size.Height,
                },
                Texts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [WebPanelImageRenderer.ImageIdKey] = imageId,
                },
            };

            string? objectId = _place(draft);

            if (string.IsNullOrEmpty(objectId))
            {
                return new WebPanelExportResult(false,
                    "画面抓到了，但没能落到卷面上（画布未就绪），请再试一次", null);
            }

            AppLog.Info($"仿真画面已导出到卷面：对象 {objectId}，位图 {imageId}"
                        + $"（{pixelWidth}×{pixelHeight} 像素 → {size.Width:F0}×{size.Height:F0} pt），"
                        + $"落点 ({center.X:F0},{center.Y:F0})");

            return new WebPanelExportResult(true,
                $"已把仿真画面放到卷面上（{size.Width / rect.Width * 100:F0}% 页宽），"
                + "可以直接拖动或缩放到题目旁边", objectId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"导出仿真画面失败：{ex.GetType().Name} {ex.Message}");
            return new WebPanelExportResult(false, "导出仿真画面失败：" + ex.Message, null);
        }
    }

    /// <summary>
    /// 算出图片的落点：<b>在视口中心</b>，但整张图必须留在页面内。
    /// </summary>
    /// <param name="page">页面矩形（世界坐标）。</param>
    /// <param name="viewportCenter">视口中心的世界坐标（老师此刻在看的地方）。</param>
    /// <param name="size">图片的世界尺寸。</param>
    /// <remarks>
    /// <para>
    /// <b>为什么以视口中心为锚。</b>老师按下「导出到白板」时，眼睛看的是屏幕正中那块，
    /// 图片出现在那里才是"我导的就是这个位置"。若改成"落在当前页正中"，
    /// 一个正在看第 3 题的人会得到一张飞到页中央的图，还得自己再拖回来。
    /// </para>
    /// <para>
    /// <b>为什么要夹进页内。</b>视口中心可能落在页间空隙、页边留白、甚至页外 ——
    /// 从那里导出一张图，它会躺在两条页面之间的空白上：在屏幕上看得见（那是可写的留白），
    /// 但<b>导成 PDF 时不属于任何一页</b>，等于凭空少了一张图。夹进页内是唯一诚实的选择。
    /// </para>
    /// <para>
    /// 图片比页面还大时（极端窄页），居中放置并允许溢出 —— 此时"留在页内"根本无法满足，
    /// 而缩小到页内会让它小到看不清。
    /// </para>
    /// </remarks>
    public static Point ComputePlacement(Rect page, Point viewportCenter, Size size)
    {
        double cx = viewportCenter.X;
        double cy = viewportCenter.Y;

        if (size.Width + 2 * PageMarginWorld <= page.Width)
        {
            cx = Math.Clamp(cx,
                page.Left + PageMarginWorld + size.Width / 2.0,
                page.Right - PageMarginWorld - size.Width / 2.0);
        }
        else
        {
            cx = page.Left + page.Width / 2.0;
        }

        if (size.Height + 2 * PageMarginWorld <= page.Height)
        {
            cy = Math.Clamp(cy,
                page.Top + PageMarginWorld + size.Height / 2.0,
                page.Bottom - PageMarginWorld - size.Height / 2.0);
        }
        else
        {
            cy = page.Top + page.Height / 2.0;
        }

        return new Point(cx, cy);
    }

    /// <summary>
    /// 按页高上限压一次尺寸。
    /// </summary>
    /// <remarks>
    /// 页面多是竖长的 A4，而仿真画面多是横的 16:9 —— 只按页宽定尺寸时，一个很宽的截图
    /// 高度可能超过半页，于是要么盖住题干、要么被上一段夹到页边。
    /// 这里按<b>页高</b>再卡一次，比例仍然守恒。
    /// </remarks>
    public static Size ClampToPageHeight(Size size, Rect page)
    {
        double maxHeight = page.Height * MaxPageHeightFraction;
        if (size.Height <= maxHeight || size.Height <= 0) return size;

        double factor = maxHeight / size.Height;
        return new Size(size.Width * factor, maxHeight);
    }
}
