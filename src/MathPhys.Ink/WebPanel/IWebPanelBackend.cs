using System;
using System.Threading.Tasks;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.WebPanel;

/// <summary>
/// Web 面板后端 —— 真正承载网页的那一层（M7.5 起，M22 泛化为"任意 profile"）。
/// </summary>
/// <remarks>
/// 抽出这一层<b>不是为了"将来换别的浏览器"</b>（大概率不会换），而是为了<b>可验证</b>。
/// <para>
/// WebView2 初始化需要 Windows 消息循环、需要真实的 HWND —— 验收 harness 是个控制台程序，
/// 起不了它。于是把"面板逻辑"（状态机、就绪闸门、消息编解码、降级）全部放进
/// <see cref="WebPanelController"/>，后端只留"打开 / 关闭 / 发消息"三件事。
/// 测试时用一个替身把后端换掉，逻辑就能在 headless 环境里跑完整断言。
/// </para>
/// <para>
/// 这与 M7.4 把"渲染"与"几何"分开是同一个手法：<b>把不可测的部分压到最薄</b>。
/// </para>
/// </remarks>
public interface IWebPanelBackend : IDisposable
{
    /// <summary>后端是否可用（运行时在不在、资源部署没部署）。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用的原因（中文，可直接显示）；可用时为 <c>null</c>。</summary>
    string? UnavailableReason { get; }

    /// <summary>面板窗口是否已经显示出来。</summary>
    bool IsLoaded { get; }

    /// <summary>当前面板形态（全屏 A3 / 停靠 A1）。</summary>
    PanelForm Form { get; }

    /// <summary>
    /// 设定面板形态（在<b>尚未打开</b>时调用，只记状态、不重排控件）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SwitchFormAsync"/> 的区别：本方法在第一次 <see cref="ShowAsync"/> 之前用，
    /// 此时 WebView2 控件还没创建，只需要把"下次打开用哪种形态"记下来。
    /// </remarks>
    void SetForm(PanelForm form);

    /// <summary>
    /// 打开面板窗口（首次调用时完成 WebView2 初始化与导航）。
    /// </summary>
    Task ShowAsync();

    /// <summary>
    /// 运行时在两种形态间切换（把 WebView2 控件从全屏窗重新挂到主窗口停靠区，或反过来）。
    /// </summary>
    /// <remarks>
    /// 切换是用户主动的、低频的动作；若实现上选择"销毁旧控件重建"而非"重新挂载同一控件"，
    /// 代价（重新初始化约 1~2 秒）是可接受的。
    /// </remarks>
    Task SwitchFormAsync(PanelForm form);

    /// <summary>关闭面板窗口并释放 WebView2（不能只隐藏 —— 它常驻内存几百 MB）。</summary>
    Task HideAsync();

    /// <summary>给页面发一条 JSON 消息。同步：WebView2 的 <c>PostWebMessageAsJson</c> 本身就是同步的。</summary>
    void Post(string json);

    /// <summary>在页面里求值并拿回结果（只在"必须立刻拿一个值"时用，它可能超时）。</summary>
    Task<string?> ExecuteScriptAsync(string script);

    /// <summary>
    /// 把当前画面抓成 PNG 字节（M22 S4）。
    /// </summary>
    /// <remarks>
    /// 走"浏览器侧抓帧"而不是页面内截图：CircuitJS / PhET 是第三方页面，
    /// <b>没法给它们注入截图脚本</b>，而抓帧对任何页面都成立。
    /// <para>
    /// 拿不到时返回 <c>null</c>（面板没开、还没渲染出来、抓帧失败），<b>不抛</b>：
    /// 导出失败只该是"没导出成功"，不该让白板崩掉。
    /// </para>
    /// </remarks>
    Task<byte[]?> CapturePngAsync();

    /// <summary>页面回话（原始 JSON 文本）。</summary>
    event EventHandler<string>? RawMessageReceived;

    /// <summary>
    /// 用户在面板里要求返回白板（点了"返回白板"按钮或按了 Esc）。
    /// </summary>
    /// <remarks>
    /// <b>必须走这条事件回到 <see cref="WebPanelController"/> 去关</b>，
    /// 而不是后端自己把窗口一关就完事：关窗要连带恢复画布输入、清空队列、
    /// 通知插件 —— 那是控制器的职责。后端自己关会让宿主停在"面板没了但输入还被锁着"。
    /// </remarks>
    event EventHandler? CloseRequested;

    /// <summary>
    /// 用户在面板里要求切换形态（点了"停靠 / 全屏"按钮）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="CloseRequested"/> 同理：形态切换也要回到控制器去统一编排
    /// （控制器才持有"当前形态"与"怎么翻成另一种形态"的逻辑），后端只负责搬运控件。
    /// </remarks>
    event EventHandler? FormToggleRequested;

    /// <summary>
    /// 形态已切换（后端搬完控件后抛出，宿主据此显示 / 隐藏主窗口的停靠区）。
    /// </summary>
    event EventHandler<PanelForm>? FormChanged;

    /// <summary>
    /// 用户在面板顶栏要求"把画面导到卷面上"（M22 S4b）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="CloseRequested"/> 同一条规矩：后端只<b>转发</b>，不做决定。
    /// 抓帧要经由控制器（那里才有"面板开着没有、加载失败没有"的状态），
    /// 落画布更是宿主的活（要碰图形对象通道，后端连它存在都不知道）。
    /// </remarks>
    event EventHandler? ExportRequested;
}
