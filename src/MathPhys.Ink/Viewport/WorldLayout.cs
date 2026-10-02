using System.Windows;

namespace MathPhys.Ink.Viewport;

/// <summary>
/// 世界坐标下的页面排版：把各页按页宽**纵向连续堆叠、水平居中**。
/// </summary>
/// <remarks>
/// 输出纯粹是世界坐标（单位 = PDF point），不含任何视口信息——
/// 这样缩放平移怎么变都不会影响排版结果，也保证存盘数据与显示状态解耦。
/// </remarks>
public sealed class WorldLayout
{
    /// <summary>
    /// 页外可写留白默认值（world 单位）。960 pt = 13.3 inch ≈ 339 mm。
    /// </summary>
    /// <remarks>
    /// 这个值决定"页面四周能写多宽的批注"，是纯体验参数，改它不影响任何坐标换算
    /// （<see cref="InkExtent"/> 的原点恒为 (0,0)，只是外扩量变了）。
    /// 初版取 96 pt（≈34 mm）实测太窄，连一道旁批都写不下，故放宽到 960 pt。
    /// </remarks>
    public const double DefaultPageMargin = 960.0;

    private readonly List<Rect> _pageRects = new();

    /// <summary>页与页之间的间距（world 单位）。</summary>
    public double PageGap { get; set; } = 24.0;

    /// <summary>
    /// 页面四周的留白（world 单位）：页面从 <c>(PageMargin, PageMargin)</c> 开始排版。
    /// </summary>
    /// <remarks>
    /// 它的存在不是为了好看，而是为了让 <see cref="InkExtent"/> 的原点<b>恰好落在世界原点上</b> ——
    /// 见 <see cref="InkExtent"/> 的说明。
    /// </remarks>
    public double PageMargin { get; set; } = DefaultPageMargin;

    /// <summary>各页在世界坐标中的矩形，索引与 PDF 页索引（0-based）一致。</summary>
    public IReadOnlyList<Rect> PageRects => _pageRects;

    /// <summary>所有页面的并集包围盒。空文档时为 <see cref="Rect.Empty"/>。</summary>
    public Rect WorldBounds { get; private set; } = Rect.Empty;

    /// <summary>
    /// 墨迹层应覆盖的世界矩形 = 页并集向外扩 <see cref="PageMargin"/>，<b>原点恒为 (0,0)</b>。
    /// </summary>
    /// <remarks>
    /// 为什么必须是 (0,0)：`InkCanvas` 只要放在 <c>(0,0)</c> 且尺寸等于本矩形，
    /// 它的本地坐标就<b>恒等于世界坐标</b>。于是 WPF 输入系统把笔尖坐标过一遍世界层逆矩阵后，
    /// <c>Stroke</c> 里存的直接就是世界坐标 —— 没有任何手工换算，
    /// 也就不存在"某个地方忘了补偿"这类 bug。
    /// <para>
    /// 若没有这个外扩、直接把墨水层贴在页并集上，本地原点会与世界原点差一个常量，
    /// 之后保存、擦除命中测试、导出 PDF 每一处都要记得减掉它。
    /// </para>
    /// </remarks>
    public Rect InkExtent => WorldBounds.IsEmpty
        ? Rect.Empty
        : Rect.Inflate(WorldBounds, PageMargin, PageMargin);

    /// <summary>
    /// 依据各页尺寸重建排版。
    /// </summary>
    public void Rebuild(IReadOnlyList<Size> pageSizes)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);

        _pageRects.Clear();

        if (pageSizes.Count == 0)
        {
            WorldBounds = Rect.Empty;
            return;
        }

        // 以最宽页为基准做水平居中：试卷偶有横排页，若逐页各自居中会左右参差
        double maxWidth = 0;
        foreach (var size in pageSizes)
        {
            maxWidth = Math.Max(maxWidth, size.Width);
        }
        double centerX = PageMargin + maxWidth / 2.0;

        // 从 (PageMargin, PageMargin) 起排 —— 于是 InkExtent 的原点恰是世界原点
        double y = PageMargin;
        foreach (var size in pageSizes)
        {
            _pageRects.Add(new Rect(centerX - size.Width / 2.0, y, size.Width, size.Height));
            y += size.Height + PageGap;
        }

        var bounds = _pageRects[0];
        for (int i = 1; i < _pageRects.Count; i++)
        {
            bounds.Union(_pageRects[i]);
        }
        WorldBounds = bounds;
    }

    /// <summary>世界坐标命中测试，返回页索引；未命中任何页返回 -1。</summary>
    public int HitTestPage(Point worldPoint)
    {
        for (int i = 0; i < _pageRects.Count; i++)
        {
            if (_pageRects[i].Contains(worldPoint)) return i;
        }
        return -1;
    }

    /// <summary>取某页在世界坐标中的矩形；越界返回 <see cref="Rect.Empty"/>。</summary>
    public Rect GetPageRect(int pageIndex)
        => pageIndex >= 0 && pageIndex < _pageRects.Count ? _pageRects[pageIndex] : Rect.Empty;
}
