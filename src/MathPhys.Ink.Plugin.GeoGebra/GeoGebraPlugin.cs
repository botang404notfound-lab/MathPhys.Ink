using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.GeoGebra;

/// <summary>
/// GeoGebra 演示面板插件（M7.5）。
/// </summary>
/// <remarks>
/// 整个插件就一个按钮："打开 GeoGebra 演示面板"。
/// <b>没有任何 WebView2 代码</b> —— 面板是宿主的（HWND 控件必须挂在宿主窗口上），
/// 插件只通过 <see cref="IToolContext.GeoGebra"/> 请求打开。这正是"给能力不给权力"：
/// 插件甚至不知道面板是用什么技术实现的。
/// </remarks>
public sealed class GeoGebraPlugin : IWhiteBoardPlugin
{
    /// <inheritdoc />
    public string Name => "GeoGebra 演示";

    /// <inheritdoc />
    public void Register(IToolRegistry registry)
    {
        // 注册很快、不做重活：真正的可用性检查（运行时在不在）发生在按钮被点亮时，
        // 而不是启动关键路径上 —— 启动时不该为了一个可能用不上的功能去探测系统。
        registry.Add(new GeoGebraTool());
    }
}
