using System;
using System.Windows.Input;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.GeoGebra;

/// <summary>
/// "打开 GeoGebra 演示面板"按钮（M7.5）。
/// </summary>
/// <remarks>
/// 它是一个<b>瞬态工具</b>（<see cref="ITransientTool"/>）：按下去是"一条命令"，
/// 不是"进入某种模式"。因此它只干一件事 —— 让宿主打开面板 ——
/// 打开之后宿主会立刻把控制权交回用户原来用的工具。
/// <para>
/// 这个设计恰好也是"面板不可用"最好的降级：拿不到通道、或运行时缺失时，
/// 只往状态栏写一句中文原因，其它一切照旧，白板功能一个不少。
/// </para>
/// </remarks>
public sealed class GeoGebraTool : ITool, ITransientTool
{
    /// <summary>工具 Id（插件自己的命名空间，与宿主内置工具不冲突）。</summary>
    public const string ToolId = "geogebra-demo";

    /// <inheritdoc />
    public string Id => ToolId;

    /// <inheritdoc />
    public string DisplayName => "GeoGebra 演示";

    /// <inheritdoc />
    public string ToolTip => "打开 GeoGebra 几何动态演示面板（全屏；按 Esc 或右上角「返回白板」退出）";

    /// <inheritdoc />
    public Key? Shortcut => Key.Y;

    // ---------------------------------------------------------------- 画布行为
    //
    // 本工具完全不接触画布：它只是"一条命令"。
    // 下面五个成员一起表达"我不碰画布，也不要指针事件"。

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

        var bridge = context.GeoGebra;

        if (bridge is null)
        {
            // 宿主没装配面板通道（旧宿主、或检验 harness）。置灰式降级，不失效。
            context.SetStatus("GeoGebra 演示面板未装配（宿主没有提供面板通道）。");
            return;
        }

        if (!bridge.IsAvailable)
        {
            // ★ 缺 WebView2 运行时的主要出口就在这里。必须是"说一句人话"，
            //   而不是抛异常 —— 一体机上没装运行时是常态。
            context.SetStatus(bridge.UnavailableReason ?? "GeoGebra 演示面板不可用。");
            return;
        }

        if (bridge.IsOpen)
        {
            context.SetStatus("GeoGebra 演示面板已经开着（按 Esc 或右上角按钮返回白板）。");
            return;
        }

        // Activate 是同步的，而打开面板是异步的：发起后不等它。
        // ShowAsync 内部自己吞掉异常并返回 bool，所以这里不会产生未观察异常。
        _ = bridge.ShowAsync();
    }

    /// <summary>
    /// 被切走。
    /// </summary>
    /// <remarks>
    /// ★ <b>这里必须是空的，而且理由很具体</b>：本工具是瞬态工具，它一被激活，
    /// 宿主立刻就把控制权交回上一个工具（见 <see cref="ITransientTool"/>），
    /// 于是 <c>Deactivate</c> 紧跟在"刚打开面板"之后被调用 ——
    /// 若在这里关面板，表现就是"面板闪一下立刻消失"。
    /// <para>
    /// 面板的关闭由面板自己的"返回白板"按钮 / Esc 负责（走
    /// <see cref="IGeoGebraBridge.HideAsync"/>）。
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
}
