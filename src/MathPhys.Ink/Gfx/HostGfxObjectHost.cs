using System;
using System.Collections.Generic;
using System.Windows;
using MathPhys.Ink.Export;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;
using MathPhys.Ink.WebPanel;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 把 <see cref="IGfxObjectHost"/> 接到真实的 <see cref="GfxObjectStore"/> 与历史引擎上。
/// </summary>
/// <remarks>
/// 与本工程的 <c>HostToolContext</c> 是同一个角色：<b>全工程唯一</b>一处让插件"够得着"图形对象。
/// 收在这里的好处是将来要给工具加一项图形能力，只需在契约里加一个成员、
/// 在这里加一行转发，而不用去翻每个工具怎么拿到宿主。
/// <para>
/// 它还负责一件事：把"记一步历史"和"改对象"这两件事<b>绑成一对</b>。
/// 让每个调用方自己记得先 <c>Capture</c> 再改，迟早有人漏掉 ——
/// 漏掉的表现是"这一步撤销不了"，而用户只会觉得撤销时灵时不灵。
/// </para>
/// </remarks>
public sealed class HostGfxObjectHost : IGfxObjectHost
{
    private readonly GfxObjectStore _store;
    private readonly GfxHistory _gfxHistory;
    private readonly BoardHistory _boardHistory;

    /// <summary>
    /// 位图仓库（M22 S3）。
    /// </summary>
    /// <remarks>
    /// <b>必须与渲染器用的是同一个实例</b>——<see cref="WebPanelImageRenderer"/> 也是拿它去取字节的。
    /// 为 <c>null</c> 时 <see cref="AddImage"/> 一律失败（宿主没装配位图通道），
    /// 而不是静默落一个空框。
    /// </remarks>
    private readonly WebPanelImageStore? _images;

    /// <summary>取"视口中心所在的那一页"的页面矩形；没有试卷 / 没装配时返回 <c>null</c>。</summary>
    private readonly Func<Rect?>? _currentPage;

    /// <summary>取视口中心的世界坐标。</summary>
    private readonly Func<Point>? _viewportCenterWorld;

    /// <summary>
    /// 建图形对象门面。
    /// </summary>
    /// <param name="store">对象集合。</param>
    /// <param name="gfxHistory">图形历史（撤销栈）。</param>
    /// <param name="boardHistory">看板历史（图形步与笔迹步的配对）。</param>
    /// <param name="images">
    /// 位图仓库（M22 S3）。省略则 <see cref="AddImage"/> 不可用 —— 旧装配点与
    /// 只关心 <see cref="Add"/> 的测试可以继续用三参构造。
    /// </param>
    /// <param name="currentPage">取视口中心所在页的矩形（决定"夹进页内"的边界）。</param>
    /// <param name="viewportCenterWorld">取视口中心的世界坐标（决定"落在老师正在看的地方"）。</param>
    public HostGfxObjectHost(
        GfxObjectStore store,
        GfxHistory gfxHistory,
        BoardHistory boardHistory,
        WebPanelImageStore? images = null,
        Func<Rect?>? currentPage = null,
        Func<Point>? viewportCenterWorld = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gfxHistory = gfxHistory ?? throw new ArgumentNullException(nameof(gfxHistory));
        _boardHistory = boardHistory ?? throw new ArgumentNullException(nameof(boardHistory));
        _images = images;
        _currentPage = currentPage;
        _viewportCenterWorld = viewportCenterWorld;
    }

    /// <summary>底层集合（宿主内部与验收 harness 用；插件拿不到）。</summary>
    internal GfxObjectStore Store => _store;

    public IReadOnlyList<IGfxObjectRef> Objects => _store.Objects;

    public IGfxObjectRef? Selected => _store.Selected;

    public event EventHandler? SelectionChanged
    {
        add => _store.SelectionChanged += value;
        remove => _store.SelectionChanged -= value;
    }

    public void BeginStep(string label)
    {
        _gfxHistory.Capture(label ?? string.Empty);
        _boardHistory.NoteGfxStep();
    }

    /// <summary>
    /// 落成一个新对象 —— 并自己把"这一步"记进历史。
    /// </summary>
    /// <remarks>
    /// <b>为什么这里要替工具记一步</b>：撤销的正确姿势是"改之前先存快照"，
    /// 而"新建"这件事没有"改之前"可给工具调用 —— 工具只知道"我要放一个图形"。
    /// 若把 <c>BeginStep</c> 交给每个工具在 <c>Add</c> 前自己调，
    /// 漏一次的表现就是"这个工具放下的图形撤销不掉"，而用户只会觉得撤销时灵时不灵。
    /// <para>
    /// 快照必须在 <see cref="GfxObjectStore.Add"/> <b>之前</b>取，否则存下的已经是
    /// "有这个对象"的状态，撤销时它不会被移除 —— 那是把撤销写成了空操作。
    /// </para>
    /// </remarks>
    public string Add(GfxDraft draft)
    {
        BeginStep("新建图形");
        return _store.Add(draft);
    }

    public bool Remove(string id) => _store.Remove(id);

    public bool UpdatePose(string id, Point center, double rotationDegrees, double scale)
        => _store.UpdatePose(id, center, rotationDegrees, scale);

    public bool UpdateNumbers(string id, IReadOnlyDictionary<string, double> numbers)
        => _store.UpdateNumbers(id, numbers);

    public bool UpdateTexts(string id, IReadOnlyDictionary<string, string> texts)
        => _store.UpdateTexts(id, texts);

    public void Select(string? id) => _store.Select(id);

    /// <summary>
    /// 把一串 PNG 落成卷面上的一张位图对象。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 走宿主既有的 <c>webPanelImage</c> 通道：与 Web 面板「导出到白板」落到卷面上的东西
    /// <b>是同一种对象</b>，于是撤销、存档（<c>.twb</c> 的 <c>images/</c> 段）、导出 PDF
    /// 全都免费继承 —— 一行都不用为原生仿真再写。
    /// </para>
    /// <para>
    /// 与 <see cref="Add"/> 同一套纪律：<b>先记一步历史、再落对象</b>
    /// （快照必须在对象入库之前取，否则撤销成了空操作）。调用方不用自己调
    /// <see cref="BeginStep"/>。
    /// </para>
    /// <para>
    /// 尺寸与落点复用 <see cref="WebPanelExportService"/> 的两个<b>纯函数</b>
    /// （<c>ComputePlacement</c> / <c>ClampToPageHeight</c>）："位图落在卷面上哪儿"
    /// 只该有一个真相源，否则两条通路迟早出现"从面板导的图和从仿真窗导的图落点不一样"。
    /// 不夹进页内时（没有试卷）就落在视口中心 —— 空白画布上那是完全正确的落点。
    /// </para>
    /// <para>
    /// 任何一步不成立都返回 <c>null</c> 并<b>不落对象</b>：往卷面上放一张空框，
    /// 比"这次没导成"糟糕得多（前者看起来像成功了）。
    /// </para>
    /// </remarks>
    public string? AddImage(byte[] pngBytes, string label = "")
    {
        if (pngBytes is not { Length: > 0 }) return null;

        var images = _images;
        if (images is null)
        {
            AppLog.Warn("宿主没有装配位图仓库，插件请求落下的位图已丢弃（对象未落成）。");
            return null;
        }

        // 按 PNG 头的像素尺寸折世界尺寸：**绝不拉伸**（圆形拉成椭圆，物理老师一眼看得出）
        PngSize.TryReadOrRatio(pngBytes, out int pixelWidth, out int pixelHeight);
        var page = _currentPage?.Invoke();
        double targetWidth = WebPanelImageRenderer.NominalWorldWidth;
        if (page is { } rect && rect.Width > 0)
        {
            targetWidth = rect.Width * WebPanelExportService.DefaultPageWidthFraction;
        }

        var size = WebPanelImageRenderer.WorldSizeFor(pixelWidth, pixelHeight, targetWidth);
        if (page is { } pageRect && pageRect.Width > 0 && pageRect.Height > 0)
        {
            size = WebPanelExportService.ClampToPageHeight(size, pageRect);
        }

        var anchor = _viewportCenterWorld?.Invoke() ?? new Point(0, 0);
        var center = page is { } p && p.Width > 0 && p.Height > 0
            ? WebPanelExportService.ComputePlacement(p, anchor, size)
            : anchor;

        string imageId = images.Add(pngBytes);
        if (imageId.Length == 0) return null;

        string objectId = Add(new GfxDraft
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
                [WebPanelImageRenderer.LabelKey] = label ?? string.Empty,
            },
        });

        AppLog.Info($"插件位图已落到卷面：对象 {objectId}，位图 {imageId}"
                    + $"（{pixelWidth}×{pixelHeight} 像素 → {size.Width:F0}×{size.Height:F0} pt），"
                    + $"落点 ({center.X:F0},{center.Y:F0})");

        return objectId;
    }
}
