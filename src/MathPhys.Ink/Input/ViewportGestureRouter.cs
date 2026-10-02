using System.Windows;
using MathPhys.Ink.Viewport;

namespace MathPhys.Ink.Input;

/// <summary>
/// 手势 → 视口操作 的路由器：把滚轮、鼠标拖动、触摸捏合、按钮/快捷键
/// 统一翻译成 <see cref="CanvasViewport"/> 的调用。
/// </summary>
/// <remarks>
/// 刻意<b>不引用任何 UI 元素</b>：只依赖视口 + 两个查询回调（视口尺寸、世界范围）。
/// 这样桌面 harness 能直接构造它并断言换算结果 —— 手势数学不靠"手试一下看看对不对"来验证，
/// 而一体机上真正需要的触摸捏合，本机没有触摸屏根本试不了。
/// </remarks>
public sealed class ViewportGestureRouter
{
    /// <summary>滚轮一格（delta = 120）对应的缩放倍率。</summary>
    private const double WheelStep = 1.1;

    /// <summary>按钮 / 快捷键的缩放步进。</summary>
    private const double ButtonStep = 1.25;

    private readonly CanvasViewport _viewport;
    private readonly Func<Size> _viewportSize;
    private readonly Func<Rect> _worldBounds;

    public ViewportGestureRouter(CanvasViewport viewport, Func<Size> viewportSize, Func<Rect> worldBounds)
    {
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _viewportSize = viewportSize ?? throw new ArgumentNullException(nameof(viewportSize));
        _worldBounds = worldBounds ?? throw new ArgumentNullException(nameof(worldBounds));
    }

    /// <summary>
    /// 「用户主动改变了视口」时触发（滚轮、拖动、捏合、按钮缩放）。
    /// 视图侧据此退出"自动适应宽度"模式 —— 否则老师刚缩放到某道题，一拖窗口就跳回适应宽度。
    /// </summary>
    public event EventHandler? ViewportMutated;

    /// <summary>滚轮缩放，以光标为锚点。<paramref name="wheelDelta"/> 即 MouseWheelEventArgs.Delta（一格 120）。</summary>
    public void WheelZoom(Point anchor, int wheelDelta)
    {
        if (wheelDelta == 0) return;

        // 用指数而非"每格固定倍数"：滚得快时倍率自然累积，手感比逐格累加顺
        ZoomAt(anchor, Math.Pow(WheelStep, wheelDelta / 120.0));
    }

    /// <summary>以视口上某点为锚点缩放（该点下的内容保持不动）。</summary>
    public void ZoomAt(Point anchor, double factor)
    {
        _viewport.ZoomAt(anchor, factor);
        NotifyMutated();
    }

    /// <summary>以视口中心为锚点缩放。</summary>
    public void ZoomByFactor(double factor)
    {
        _viewport.ZoomBy(factor, _viewportSize());
        NotifyMutated();
    }

    public void ZoomIn() => ZoomByFactor(ButtonStep);

    public void ZoomOut() => ZoomByFactor(1.0 / ButtonStep);

    /// <summary>按视口像素平移（鼠标拖动）。</summary>
    public void PanBy(double deltaX, double deltaY)
    {
        if (deltaX == 0 && deltaY == 0) return;

        _viewport.PanBy(deltaX, deltaY);
        NotifyMutated();
    }

    /// <summary>
    /// 一次触摸手势（缩放 + 平移）合成<b>一次</b>视口更新。
    /// </summary>
    /// <param name="origin">手势原点（捏合中心 / 单指位置），视口坐标。</param>
    /// <param name="translation">本帧位移，视口像素。</param>
    /// <param name="scaleDelta">本帧缩放增量，1.0 表示不缩放。</param>
    public void Manipulate(Point origin, Vector translation, double scaleDelta)
    {
        // 输入异常时退化成纯平移，而不是把 NaN 灌进矩阵（那会让整个画布消失）
        if (double.IsNaN(scaleDelta) || scaleDelta <= 0) scaleDelta = 1.0;
        if (double.IsNaN(translation.X) || double.IsNaN(translation.Y)) translation = default;

        _viewport.ApplyManipulation(origin, translation, scaleDelta);
        NotifyMutated();
    }

    /// <summary>把缩放精确设为目标值，保持视口中心的内容不动（100% 按钮用）。</summary>
    public void SetScale(double scale)
    {
        double current = _viewport.Scale;
        if (current <= 0) return;

        _viewport.ZoomBy(scale / current, _viewportSize());
        NotifyMutated();
    }

    /// <summary>
    /// 按宽度铺满视口。这是"回到自动模式"的操作，<b>刻意不触发</b> <see cref="ViewportMutated"/>。
    /// </summary>
    public void FitWidth(double sidePadding)
    {
        var bounds = _worldBounds();
        if (bounds.IsEmpty) return;

        _viewport.FitToWidth(bounds, _viewportSize(), sidePadding);
    }

    private void NotifyMutated() => ViewportMutated?.Invoke(this, EventArgs.Empty);
}
