using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Ruler;

/// <summary>
/// 直尺插件：注册「直尺」（角度吸附）与「直尺·自由角」（不吸附）两个工具。
/// </summary>
/// <remarks>
/// 两个工具是<b>同一个类的两个实例</b>，只差一个布尔 ——
/// 这就是 M7.1 把工具做成对象的收益：加一个形态的成本是一行注册，不是一份新代码。
/// <para>
/// <see cref="Register"/> 跑在程序启动的关键路径上，所以这里只做注册、不读文件不连网络。
/// </para>
/// </remarks>
public sealed class RulerPlugin : IWhiteBoardPlugin
{
    public string Name => "直尺（学科工具）";

    public void Register(IToolRegistry registry)
    {
        registry.Add(new RulerTool(snapEnabled: true));
        registry.Add(new RulerTool(snapEnabled: false));
    }
}
