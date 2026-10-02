using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Compass;

/// <summary>
/// 圆规与圆弧插件：注册两个工具（两点式整圆 / 任意夹角圆弧）。
/// </summary>
/// <remarks>
/// <see cref="CompassCircleRenderer"/> / <see cref="CompassArcRenderer"/> <b>不在这里注册</b> ——
/// 插件 dll 里实现了 <see cref="IGfxObjectRenderer"/> 的类型由加载器自动登记
/// （见 <c>PluginLoader.RegisterRenderers</c>），与量角器插件同一约定。
/// <see cref="Register"/> 跑在程序启动的关键路径上：只注册，不读文件、不连网络。
/// </remarks>
public sealed class CompassPlugin : IWhiteBoardPlugin
{
    public string Name => "圆规与圆弧（学科工具）";

    public void Register(IToolRegistry registry)
    {
        registry.Add(new CompassTool(CompassMode.Circle));
        registry.Add(new CompassTool(CompassMode.Arc));
    }
}
