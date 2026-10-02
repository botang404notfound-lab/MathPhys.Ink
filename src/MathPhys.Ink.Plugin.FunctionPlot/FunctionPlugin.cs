using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.FunctionPlot;

/// <summary>函数图像插件：注册一个工具（渲染器由宿主扫同名 dll 自动登记）。</summary>
public sealed class FunctionPlotPlugin : IWhiteBoardPlugin
{
    public string Name => "函数图像（学科工具）";

    public void Register(IToolRegistry registry)
    {
        registry.Add(new FunctionTool());
    }
}
