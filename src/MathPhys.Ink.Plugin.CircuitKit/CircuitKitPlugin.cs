using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CircuitKit;

/// <summary>
/// 电路元件插件：电学常用元件图形库（必修三电路实验为主）。
/// </summary>
/// <remarks>
/// 渲染器不在这注册，由宿主扫同名 dll 自动登记（与 VectorArrow 同款做法）。
/// </remarks>
public sealed class CircuitKitPlugin : IWhiteBoardPlugin
{
    public string Name => "电路元件（学科工具）";

    public void Register(IToolRegistry registry)
    {
        registry.Add(new CircuitTool());
    }
}
