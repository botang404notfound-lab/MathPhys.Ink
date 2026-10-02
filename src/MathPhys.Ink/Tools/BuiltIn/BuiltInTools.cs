using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Tools.BuiltIn;

/// <summary>
/// 内置工具包：笔 / 橡皮 / 手。
/// </summary>
/// <remarks>
/// 它的存在本身就是一个设计决定：<b>内置工具与外部插件走同一套接口</b>
/// （都实现 <see cref="IWhiteBoardPlugin"/>）。
/// <para>
/// 好处是把迁移风险一次性暴露在 M7.1：如果接口设计错了，
/// 在此刻就会以"内置工具自己也别扭"的形式暴露出来，而不是等到 M7.3 写第一个外部插件才发现 ——
/// 那时契约已经流出去了，改它要付双倍代价。
/// </para>
/// </remarks>
public sealed class BuiltInTools : IWhiteBoardPlugin
{
    public string Name => "内置工具（笔 / 橡皮 / 手）";

    public void Register(IToolRegistry registry)
    {
        registry.Add(new PenTool());
        registry.Add(new EraserTool());
        registry.Add(new HandTool());
    }
}
