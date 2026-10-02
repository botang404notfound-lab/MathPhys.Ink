using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using MathPhys.Ink.Plugins;
using MathPhys.Ink.Views.Controls;

namespace MathPhys.Ink.Tools;

/// <summary>
/// 把 <see cref="IToolContext"/> 接到真实的画布宿主上。
/// </summary>
/// <remarks>
/// 这是<b>全工程唯一</b>一处让工具"够得着"宿主的地方。收在这里的好处是：
/// 将来要给工具加一项能力，只需在契约里加一个成员、在这里加一行转发，
/// 而不用去翻每个工具怎么拿到宿主。
/// <para>
/// 它只暴露宿主上那些"安全"的操作（提交笔画、加预览、读写笔色笔宽、坐标换算），
/// 视口变换与文档生命周期一概不出现 —— 见 <see cref="IToolContext"/> 的说明。
/// </para>
/// </remarks>
public sealed class HostToolContext : IToolContext
{
    private readonly CanvasViewportHost _host;

    public HostToolContext(CanvasViewportHost host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    public Size ViewportSize => new(_host.ActualWidth, _host.ActualHeight);

    public double Scale => _host.Viewport.Scale;

    public IReadOnlyList<Rect> PageRects => _host.Layout.PageRects;

    public Point ToWorld(Point viewportPoint) => _host.Viewport.ToWorld(viewportPoint);

    public Point ToViewport(Point worldPoint) => _host.Viewport.ToViewport(worldPoint);

    public Color PenColor => _host.Pen.Color;

    public double PenWorldWidth => _host.Pen.WorldWidth;

    /// <summary>
    /// 图形对象通道。
    /// </summary>
    /// <remarks>
    /// 这里是<b>全工程唯一</b>一处把门面递出去的地方，插件拿到的是
    /// <see cref="IGfxObjectHost"/>（受控子集），而不是 <c>GfxObjectStore</c> ——
    /// 后者能清空全部对象、能改别人的对象，那是宿主自己的权力。
    /// </remarks>
    public IGfxObjectHost? Gfx => _host.GfxHost;

    /// <summary>
    /// 吸附基准查询（看到画布上已有的线段与端点）。
    /// </summary>
    /// <remarks>
    /// 是"查询"而不是"遍历"：拿到的线段是原样的一段，怎么用由工具决定。
    /// 这里<b>没有</b>写"我能改别人的东西"——那张门面是 <see cref="Gfx"/>，两件事分开。
    /// </remarks>
    public IBoardQuery? Query => _host.BoardQuery;

    /// <summary>
    /// 演示面板通道（M7.5，GeoGebra）。
    /// </summary>
    /// <remarks>
    /// 转发给宿主。宿主没装配面板时（验收 harness、或资源缺失的机器）为 <c>null</c> ——
    /// 工具据此把按钮置灰并给中文原因，而不是让工具失效。
    /// </remarks>
    public IGeoGebraBridge? GeoGebra => _host.GeoGebra;

    /// <summary>
    /// 通用 Web 面板通道（M22）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="GeoGebra"/> 同一处转发；宿主没装配时为 <c>null</c>，
    /// 工具据此把按钮置灰并给中文原因，而不是让工具失效。
    /// </remarks>
    public IWebPanelBridge? WebSim => _host.WebSim;

    /// <summary>视口变化（缩放 / 平移 / 适配）。屏幕尺寸恒定的图元靠它重算坐标。</summary>
    public event EventHandler? ViewportChanged
    {
        add => _host.ViewportChanged += value;
        remove => _host.ViewportChanged -= value;
    }

    public void CommitStrokes(StrokeCollection strokes) => _host.CommitStrokes(strokes);

    public void AddPreview(UIElement visual) => _host.AddPreview(visual);

    public void ClearPreview() => _host.ClearPreview();

    public void SetStatus(string text) => _host.ReportToolStatus(text);
}
