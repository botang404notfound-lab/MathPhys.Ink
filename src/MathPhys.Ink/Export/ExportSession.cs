using System.Windows;
using MathPhys.Ink.Viewport;

namespace MathPhys.Ink.Export;

/// <summary>
/// 导出期间的「画布借用」：把视口摆成 1:1 供离屏渲染，用完原样还回去。
/// </summary>
/// <remarks>
/// <b>为什么是借用，而不是另搭一棵离屏可视树</b>：屏幕上的画面是
/// <c>RootGrid → WorldHost → PdfPageLayer + GfxLayer + InkLayer</c> 这条链算出来的。
/// 离屏重搭等于把这条链再实现一遍 —— 墨迹粗细、图形对象的渲染器、页位图档位
/// 全都要重新对一次；两处一旦不同，症状就是"屏幕上好好的、导出少一层"。
/// 借用现成的视觉树，导出产物与屏幕<b>共享同一份渲染代码</b>。
/// <para>
/// 借用期间做两件事：
/// <list type="number">
///   <item><description>视口摆成 <c>scale=1</c>、并把<b>目标页左上角</b>对齐渲染目标的 (0,0)
///     —— 此时 1 world 单位 = 1 DIP，<c>RenderTargetBitmap</c> 的像素数才等于"页面尺寸 × dpi"；
///     </description></item>
///   <item><description><b>冻结宿主的渲染需求重算</b>（<c>CanvasViewportHost.BeginRenderFreeze</c>）。
///     ★ 不做这件事，导出会得到白纸：视口一变，宿主就按新视口重算"哪些页需要什么档位"，
///     而那个重算会把<b>视口外</b>的页位图清成 null 省内存 —— 1:1 视口看不到的页正好全在其中。</description></item>
/// </list>
/// </para>
/// <para>
/// 用 <c>using</c> 包住即自动归还；中途抛异常也归还（导出失败不该顺手把老师的视口弄丢）。
/// </para>
/// </remarks>
public sealed class ExportSession : IDisposable
{
    private readonly CanvasViewport _viewport;
    private readonly CanvasViewport.ViewSnapshot _snapshot;
    private readonly IDisposable? _freeze;
    private bool _disposed;

    /// <summary>
    /// 借出视口：摆成 1:1，并把 <paramref name="worldTopLeft"/> 对齐渲染目标的 (0,0)。
    /// </summary>
    /// <param name="viewport">要借的视口（宿主那一个）。</param>
    /// <param name="freezeToken">宿主的渲染需求冻结令牌；纯数学场景可传 <c>null</c>。</param>
    /// <param name="worldTopLeft">目标页左上角的世界坐标。</param>
    /// <remarks>
    /// ★ 平移量是 <c>-worldTopLeft</c>，<b>不是 (0,0)</b>：试卷从 (960,960) 起排
    /// （四周留可写留白，见 <c>WorldLayout.DefaultPageMargin</c>），
    /// "世界原点对齐"只有第 1 页碰巧能对上，第 2 页起会整页偏出画面 —— 导出成一张白纸。
    /// </remarks>
    public ExportSession(CanvasViewport viewport, IDisposable? freezeToken, Point worldTopLeft)
    {
        _viewport = viewport;
        _snapshot = viewport.Save();
        _freeze = freezeToken;

        AlignTo(worldTopLeft);
    }

    /// <summary>
    /// 把视口移到另一页（1:1、该页左上角对齐渲染目标的 (0,0)）。整卷导出用。
    /// </summary>
    /// <remarks>
    /// ★ 为什么要有它，而不是"每页借还一次"：借还会把视口<i>还原</i>再<i>重设</i>，
    /// 于是 <c>WorldTransform</c> 在整卷导出期间被改 <c>2N</c> 次。
    /// 冻结只挡"渲染需求重算"，挡不住渲染线程用某个中间视口重绘一帧 ——
    /// 界面上就是导出过程中屏幕闪几下，闪的是别的页的角落。
    /// 借一次、逐页移动，整卷只在首尾各改一次。
    /// <para>
    /// 幂等：对同一页重复调用不会有额外副作用（<see cref="CanvasViewport.SetView"/> 内部会判重）。
    /// </para>
    /// </remarks>
    public void AlignTo(Point worldTopLeft)
        => _viewport.SetView(1.0, -worldTopLeft.X, -worldTopLeft.Y);

    /// <summary>借用期间的视口缩放（恒为 1.0：1 world 单位 = 1 DIP）。</summary>
    public double Scale => _viewport.Scale;

    /// <summary>借用期间视口原点在屏幕上的位置（= 目标页左上角取负）。</summary>
    public Point Offset => new(_viewport.OffsetX, _viewport.OffsetY);

    /// <summary>借用前的视口存档（排查"导出后位置变了"时先看它）。</summary>
    public CanvasViewport.ViewSnapshot Snapshot => _snapshot;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 归还顺序：先还原视口，再解冻。
        // 还原会触发 Changed → 宿主排一次渲染需求重算，此时仍在冻结中、那次被吃掉；
        // 解冻时宿主补排一次 ⇒ 用还原后的视口重算，画面回到老师原来的位置。
        // 反过来写（先解冻再还原）也能走通，但中间会按 1:1 视口白算一次需求，
        // 而且有窗口期：渲染线程可能正好在那一刻把页位图换成浅档。
        _viewport.Restore(_snapshot);
        _freeze?.Dispose();
    }
}
