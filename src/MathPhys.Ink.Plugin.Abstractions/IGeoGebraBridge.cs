using System;
using System.Threading.Tasks;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 演示面板的两种形态（M7.5 S3）。
/// </summary>
public enum PanelForm
{
    /// <summary>全屏演示（A3）：临时接管整块屏幕，退出后回到白板。一体机主用法。</summary>
    FullScreen,

    /// <summary>停靠面板（A1）：在主窗口右侧腾出一块，画布缩小让位，两者并列互斥。</summary>
    Docked,
}

/// <summary>
/// 重型演示面板（GeoGebra）的宿主桥接通道（M7.5）。
/// </summary>
/// <remarks>
/// 面板本身由<b>宿主</b>创建和管理：WebView2 是基于 HWND 的控件，必须挂在宿主的窗口上，
/// 还要参与窗口生命周期 —— 插件做不到，也不该做。
/// 插件通过这个通道"请求打开 / 关闭 / 发消息"，<b>不接触任何 WebView2 类型</b> ——
/// 与 <see cref="IGfxObjectHost"/> 是同一个思路：给能力，不给权力。
/// <para>
/// <b>可能为 <c>null</c></b>（见 <see cref="IToolContext.GeoGebra"/>）：宿主版本较旧、
/// 或验收 harness 里的替身没有实现这一层时会出现。拿不到时的正确做法是
/// <b>把按钮置灰并说明原因</b>，而不是让工具失效 ——
/// 少一个演示面板是少个便利，按下去毫无反应是故障。
/// </para>
/// </remarks>
public interface IGeoGebraBridge
{
    /// <summary>
    /// 面板是否可用（WebView2 运行时是否就绪、离线资源是否部署到位）。
    /// </summary>
    /// <remarks>
    /// 这是<b>属性而不是异常</b>：按钮要靠它在"按下去之前"就置灰。
    /// 若等到按下才报错，用户得到的是"点了没反应"。
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>不可用的原因（中文，可直接显示给用户）；可用时为 <c>null</c>。</summary>
    string? UnavailableReason { get; }

    /// <summary>面板当前是否已打开。</summary>
    bool IsOpen { get; }

    /// <summary>面板当前形态（全屏 / 停靠）。</summary>
    PanelForm CurrentForm { get; }

    /// <summary>
    /// 页面是否已就绪（面板内的 JS 报过 <c>ready</c>）。
    /// </summary>
    /// <remarks>
    /// 就绪之前的 <see cref="SendAsync"/> 命令会被<b>排队</b>而不是丢弃。
    /// </remarks>
    bool IsReady { get; }

    /// <summary>
    /// 打开面板。
    /// </summary>
    /// <param name="materialBase64">
    /// 可选的 GeoGebra 素材（<c>.ggb</c> 文件的 Base64）。<c>null</c> 表示打开空白工作区。
    /// </param>
    /// <returns>
    /// 是否成功打开。<b>不可用时不抛异常</b>，返回 <c>false</c> 并把原因写进
    /// <see cref="UnavailableReason"/> 与状态栏 ——
    /// 课堂上弹一个异常对话框，老师得先关掉它才能继续讲课，那是把故障升级成了事故。
    /// </returns>
    Task<bool> ShowAsync(string? materialBase64 = null);

    /// <summary>
    /// 关闭面板，并把宿主恢复到<b>打开之前</b>的状态。
    /// </summary>
    /// <remarks>
    /// "恢复到打开之前"而不是"一律恢复成某个固定值"：进入面板前用户可能正用手工具
    /// （墨迹层本该不接输入），一律恢复成"能写字"就会让他莫名多出一堆墨点。
    /// </remarks>
    Task HideAsync();

    /// <summary>
    /// 发一条消息给面板里的 JS。
    /// </summary>
    /// <remarks>
    /// <b>★ 就绪闸门</b>：面板还没报 <c>ready</c> 时，命令进队列，等 <c>ready</c> 到达后按序发出。
    /// 没有这道闸门的表现是"偶发点了没反应"（取决于页面加载快慢），现场极难复现 ——
    /// 所以它是契约行为，不是实现细节。
    /// </remarks>
    Task SendAsync(string type, object? payload = null);

    /// <summary>面板回话（JS → 宿主）。</summary>
    event EventHandler<GeoGebraMessage>? MessageReceived;

    /// <summary>
    /// 在当前形态与另一种形态之间切换（全屏 ⇄ 停靠）。
    /// </summary>
    /// <remarks>
    /// 交给宿主内部编排，不暴露给插件：形态是"界面怎么摆"的考量，插件只关心"打开 / 关闭 / 发命令"。
    /// </remarks>
    Task ToggleFormAsync();

    /// <summary>形态切换完成（宿主据此显示 / 隐藏主窗口的停靠区）。</summary>
    event EventHandler<PanelForm>? FormChanged;
}
