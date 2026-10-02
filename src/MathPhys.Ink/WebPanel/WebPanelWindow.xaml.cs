using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.WebPanel;

/// <summary>
/// 通用 Web 面板的全屏演示窗（M7.5 起，M22 由 <c>GeoGebraPanelWindow</c> 泛化）。
/// </summary>
/// <remarks>
/// 只负责"显示 + 把用户想退出这件事说出来"：它<b>不</b>自己关闭自己，
/// 而是抛 <see cref="BackRequested"/> 交给 <see cref="WebPanelController"/> ——
/// 因为退出要连带恢复画布输入、清空命令队列、通知插件，那些是控制器的活。
/// <para>
/// 标题栏文字在构造时由 profile 传入：GeoGebra 与物理仿真共用这个窗口类。
/// </para>
/// </remarks>
public partial class WebPanelWindow : Window
{
    /// <summary>用户要求返回白板（点了按钮 / 按了 Esc / Alt+F4）。</summary>
    public event EventHandler? BackRequested;

    /// <summary>用户要求在停靠 / 全屏两种形态间切换（点了「停靠面板」按钮）。</summary>
    /// <remarks>
    /// 与 <see cref="BackRequested"/> 一样只"说出来"不自己动手：
    /// 形态切换要重挂 WebView2、联动主窗口停靠区，那些是后端与控制器的活。
    /// </remarks>
    public event EventHandler? ToggleFormRequested;

    /// <summary>
    /// 用户要求把这个画面导到卷面上（点了「导出到白板」按钮，或按了 E）。
    /// </summary>
    /// <remarks>
    /// 本窗只<b>喊一声</b>。抓帧、量比例、算落点、落成一个可存档的图形对象 ——
    /// 全都需要一个活着的宿主（要碰画布与图形对象通道），不是窗能做的事。
    /// <para>
    /// ★ 按钮做在<b>窗的顶栏</b>而不是网页里：三个仿真页都是第三方官方包
    /// （GeoGebra 官方 bundle / CircuitJS / PhET），一个字节都不该改，
    /// 也不该指望它们替宿主留一个按钮。做在这里，三个页共用一条通路。
    /// </para>
    /// </remarks>
    public event EventHandler? ExportRequested;

    /// <summary>允许真正关闭。只有后端发起关闭时才为真，用来区分"用户要退出"与"程序要收窗"。</summary>
    private bool _allowClose;

    /// <summary>构造。</summary>
    /// <param name="displayName">面板显示名（顶栏标题与窗口标题）。</param>
    /// <param name="allowFormToggle">是否摆出「停靠面板」按钮（M22：物理仿真只做全屏，传 false）。</param>
    public WebPanelWindow(string displayName, bool allowFormToggle = true)
    {
        InitializeComponent();

        var title = string.IsNullOrWhiteSpace(displayName) ? "演示面板" : displayName;
        TitleText.Text = title;
        Title = title;

        // 只做全屏的面板（物理仿真）连按钮都不摆：摆出来再禁用，
        // 老师还是会去点它 —— 不如让那个动作根本不存在。
        if (!allowFormToggle)
        {
            ToggleFormButton.Visibility = Visibility.Collapsed;
        }

        Loaded += OnLoadedForLayoutProbe;
    }

    /// <summary>
    /// 一次性布局自检：把顶栏与网页区的<b>实际</b>位置写进日志。
    /// </summary>
    /// <remarks>
    /// 加这条日志有两个理由，都很实在：
    /// <list type="number">
    /// <item>空气空间那条硬约束（顶栏不许与 WebView2 矩形重叠）是<b>靠布局保证</b>的。
    /// 它一旦被将来的 XAML 改动破坏，症状是"返回按钮既看不见也点不着" ——
    /// 而那时人在一体机前面，挂不了调试器，日志就是唯一的线索。</item>
    /// <item>它把"重叠"这件要命的事变成一行<b>可断言的数字</b>，
    /// 而不是"看起来大概没重叠"。</item>
    /// </list>
    /// </remarks>
    private void OnLoadedForLayoutProbe(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedForLayoutProbe;

        try
        {
            var offset = BackButton.TranslatePoint(new Point(0, 0), this);
            var hostOffset = ViewHost.TranslatePoint(new Point(0, 0), this);
            var hostSize = ViewHost.RenderSize;
            var clear = offset.Y + BackButton.ActualHeight <= hostOffset.Y + 0.5;

            AppLog.Info(
                $"面板布局自检：返回按钮 {BackButton.ActualWidth:0}×{BackButton.ActualHeight:0}"
                + $" @ ({offset.X:0},{offset.Y:0})，可见={BackButton.IsVisible}"
                + $"；网页区 {hostSize.Width:0}×{hostSize.Height:0} @ ({hostOffset.X:0},{hostOffset.Y:0})"
                + $"；★ 顶栏与网页区不重叠={clear}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"面板布局自检失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>把承载网页的控件放进内容区。</summary>
    public void AttachView(UIElement view) => ViewHost.Child = view;

    /// <summary>
    /// 把承载的网页控件摘下来（形态切换换父前必须先断开）。
    /// </summary>
    /// <remarks>
    /// ★ WPF 不允许一个元素同时是两个容器的逻辑子级 ——
    /// 不先摘下来就直接挂到停靠区，会抛
    /// 「指定的元素已经是另一个元素的逻辑子元素」，切换当场失败。
    /// 没挂时调用是 no-op（幂等）。
    /// </remarks>
    public void DetachView() => ViewHost.Child = null;

    /// <summary>在顶栏说一句话（面板状态 / 报错）。</summary>
    public void SetStatus(string? text) => StatusText.Text = text ?? string.Empty;

    /// <summary>
    /// 由后端调用的"真关闭"。
    /// </summary>
    /// <remarks>
    /// 没有这个开关就会死循环：后端关窗 → 触发 <see cref="OnClosing"/> → 又抛"返回白板"
    /// → 控制器又去关窗。用一次性的许可位把两条路径分开。
    /// 后端的 <c>HideAsync</c> 目前只隐藏窗口，真正走这里的是释放流程。
    /// </remarks>
    internal void CloseForReal()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            RequestBack();
            return;
        }

        if (e.Key == Key.D)
        {
            // 停靠 ⇄ 全屏切换（与面板顶栏的「停靠面板」按钮同一个动作）。
            e.Handled = true;
            ToggleFormRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Key == Key.E)
        {
            // 导出到白板（与顶栏按钮同一个动作）。顶栏在 WebView2 之外，
            // 所以这条快捷键只在焦点落在窗自身的部件上时才到得了这里 ——
            // 这也正合适：焦点在网页里时，E 该归页面自己用。
            e.Handled = true;
            ExportRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // 用户按了 Alt+F4（窗口没有标题栏，但快捷键仍然有效）：
            // 不当"关闭窗口"，当"回白板"，走同一条恢复流程。
            e.Cancel = true;
            RequestBack();
            return;
        }

        base.OnClosing(e);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => RequestBack();

    private void OnToggleFormClick(object sender, RoutedEventArgs e)
        => ToggleFormRequested?.Invoke(this, EventArgs.Empty);

    private void OnExportClick(object sender, RoutedEventArgs e)
        => ExportRequested?.Invoke(this, EventArgs.Empty);

    private void RequestBack() => BackRequested?.Invoke(this, EventArgs.Empty);
}
