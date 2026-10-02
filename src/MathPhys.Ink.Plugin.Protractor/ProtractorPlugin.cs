using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Protractor;

/// <summary>
/// 量角器插件。
/// </summary>
/// <remarks>
/// 注册一个工具；<see cref="ProtractorRenderer"/> <b>不在这里注册</b> ——
/// 插件 dll 里实现了 <see cref="IGfxObjectRenderer"/> 的类型由加载器自动登记
/// （见 <c>PluginLoader.RegisterRenderers</c>）。插件作者因此少写一段样板代码，
/// 而"哪些 Kind 已经有人认领"这件事也只有一个写入点。
/// <para>
/// <see cref="Register"/> 跑在程序启动的关键路径上：这里只做注册，不读文件、不连网络。
/// </para>
/// </remarks>
public sealed class ProtractorPlugin : IWhiteBoardPlugin
{
    public string Name => "量角器（学科工具）";

    public void Register(IToolRegistry registry) => registry.Add(new ProtractorTool());
}
