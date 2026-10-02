using System;
// ★ System.Windows 是给 WindowState 用的（Activate 里要把最小化的窗还原）。
//   只引 System.Windows.Input 会漏掉它，报 CS0103「The name 'WindowState' does not exist」。
using System.Windows;
using System.Windows.Input;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.NativeSim;

/// <summary>
/// 「物理仿真」按钮（M22 S3）：打开原生运动学仿真窗。
/// </summary>
/// <remarks>
/// <para>
/// 它是一个<b>瞬态工具</b>（<see cref="ITransientTool"/>）：按下去是"一条命令"，
/// 不是"进入某种模式"。所以它只干一件事 —— 把窗口开出来 ——
/// 开完之后宿主立刻把控制权交回用户原来用的工具。
/// </para>
/// <para>
/// <b>这与 Web 面板那条路有一处本质区别</b>：那边是 HWND 全屏窗，会盖住整个画布，
/// 所以必须靠 <c>EnterPanelMode</c> 让输入互斥；本窗口只是一块普通 WPF 窗口，
/// <b>从来没有把画布输入拿走</b>，于是"窗口关掉交还输入"这件事是天然成立的，
/// 不需要任何回滚逻辑（也就不存在"回滚失败 ⇒ 白板再也写不出字"那种最坏情况）。
/// </para>
/// <para>
/// 「导出到白板」依赖宿主的图形对象通道（<see cref="IGfxObjectHost.AddImage"/>）：
/// 拿不到时按钮<b>置灰并给中文原因</b>，仿真本身照看不误 ——
/// 少一个导出是少个便利，按下去毫无反应是故障。
/// </para>
/// </remarks>
public sealed class NativeSimTool : ITool, ITransientTool
{
    private IToolContext? _context;
    private SimWindow? _window;

    /// <inheritdoc />
    public string Id => NativeSimToolIds.Id;

    /// <inheritdoc />
    public string DisplayName => "物理仿真";

    /// <inheritdoc />
    public string ToolTip
        => "物理仿真：单摆 / 弹簧振子 / 斜面（原生绘制，可调摆长、重力、劲度系数、摩擦因数、阻尼）。"
           + "窗口里可以把当前画面导出到卷面上；完整版另有「网页仿真」入口（CircuitJS / PhET）。";

    /// <summary>
    /// 快捷键 P（Physics）。
    /// </summary>
    /// <remarks>
    /// 挑 P 之前对着 <c>ToolRegistry</c> 的全表核过：D1~D9、D0、C、V、N、R、A、G、M、U、Y 都被占了，
    /// P 是空的，且不与 <c>MainWindow</c> 自己的按键处理（Esc / F9 / F11 / O / Z / Y / S / E / N / W / 加减）
    /// 冲突。重键的表现是"按下去只选中了其中一个工具"，在课堂上是查不出来的。
    /// </remarks>
    public Key? Shortcut => Key.P;

    // ---------------------------------------------------------------- 画布行为
    //
    // 本工具完全不接触画布：它只是"一条命令"。

    /// <inheritdoc />
    public bool UsesInkLayer => false;

    /// <inheritdoc />
    public bool NeedsPointer => false;

    /// <inheritdoc />
    public ToolInputKind InputKind => ToolInputKind.None;

    /// <inheritdoc />
    public ToolInkMode InkMode => ToolInkMode.None;

    /// <inheritdoc />
    public Cursor? Cursor => null;

    /// <inheritdoc />
    public void Activate(IToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        // 已经开着就别再开一个：点两下出现两个仿真窗，谁在上面都说不清
        if (_window is { IsLoaded: true })
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            context.SetStatus("物理仿真窗已经开着（右侧改参数看效果，或按「导出到白板」把画面落到卷面上）。");
            return;
        }

        ShowWindow(context);
    }

    /// <summary>
    /// 被切走。
    /// </summary>
    /// <remarks>
    /// ★ <b>这里必须是空的，而且理由很具体</b>：本工具是瞬态工具，它一被激活，
    /// 宿主立刻就把控制权交回上一个工具（见 <see cref="ITransientTool"/>），
    /// 于是 <c>Deactivate</c> 紧跟在"刚打开窗口"之后被调用 ——
    /// 若在这里关窗口，表现就是"仿真窗闪一下立刻消失"。
    /// <para>
    /// 窗口的关闭由它自己的「关闭」按钮 / Esc 负责。
    /// </para>
    /// </remarks>
    public void Deactivate()
    {
    }

    /// <inheritdoc />
    public void OnPointer(ToolPointer pointer)
    {
        // NeedsPointer=false，宿主不会调用；留着是为了满足接口。
    }

    /// <summary>建窗并显示。</summary>
    private void ShowWindow(IToolContext context)
    {
        var gfx = context.Gfx;

        var window = new SimWindow(
            context.WebSim,
            addImage: AddImage,
            report: context.SetStatus,
            canExport: gfx is not null);

        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();

        context.SetStatus(gfx is not null
            ? "物理仿真：上方切换模型，右侧调参数；「导出到白板」把当前画面落到卷面上。"
            : "物理仿真：当前程序不支持图形对象，「导出到白板」不可用（仿真照常可看）。");
    }

    /// <summary>
    /// 把 PNG 交给宿主落成卷面上的位图对象。
    /// </summary>
    /// <remarks>
    /// 每次现取 <c>_context</c>（而不是在构造窗口时缓存 <c>IToolContext</c>）：
    /// 窗口活得比一次 <c>Activate</c> 长，而上下文是宿主按"当前激活的工具"给的。
    /// </remarks>
    private string? AddImage(byte[] png, string label) => _context?.Gfx?.AddImage(png, label);
}
