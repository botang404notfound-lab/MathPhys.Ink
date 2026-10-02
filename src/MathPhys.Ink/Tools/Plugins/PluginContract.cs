using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Tools.Plugins;

/// <summary>
/// 插件契约的"身份"与版本规则 —— 加载器的判据全部集中在这里，便于单独验收。
/// </summary>
/// <remarks>
/// <b>为什么版本检查不能读 <c>PluginApi.Version</c> 常量</b>：
/// 它是 <c>const</c>，编译时就被<b>内联</b>进插件自己的 IL 里了，运行时用反射读不到"插件编译时用的是哪个版本"。
/// 所以真正可靠的判据是<b>插件对契约 dll 的引用版本</b>（<see cref="Assembly.GetReferencedAssemblies"/>），
/// 它老老实实写在元数据里。
/// <para>
/// 还有一层必须显式检查的理由：契约 dll <b>没有强名称</b>，.NET 按<b>简单名</b>绑定。
/// 也就是说"插件引用了 v2、宿主只有 v1"这件事<b>不会</b>让加载失败，
/// 而是静默地把宿主的 v1 交给插件 —— 正是那种"能加载但行为诡异"的坏结局。
/// 所以宁可我们自己拒绝，并说清原因。
/// </para>
/// </remarks>
public static class PluginContract
{
    /// <summary>契约程序集名（同时也是编译进插件的引用名）。</summary>
    public const string AssemblyName = "MathPhys.Ink.Plugin.Abstractions";

    /// <summary>宿主程序集名 —— 插件也不该自己带一份宿主。</summary>
    public const string HostAssemblyName = "MathPhys.Ink";

    /// <summary>宿主此刻的契约版本（= 契约 dll 的程序集版本）。</summary>
    public static Version HostVersion => typeof(ITool).Assembly.GetName().Version ?? new Version(1, 0);

    /// <summary>
    /// 这个程序集是否<b>必须共享</b>（走默认上下文，不由插件目录提供）。
    /// </summary>
    /// <remarks>
    /// 这是整套插件机制里最容易静默出错的一处：一旦让插件从自己的目录里加载一份契约副本，
    /// 运行时就会出现两个同名不同类型的 <c>IWhiteBoardPlugin</c>，
    /// <c>as</c> 判断静默返回 <c>null</c> —— 表现是"插件 dll 明明在、一个工具都没注册、还不报错"。
    /// 所以契约与宿主一律"往回走"，插件目录里就算放了同名 dll 也不采用。
    /// </remarks>
    public static bool IsSharedWithHost(string? assemblyName)
        => assemblyName is not null
           && (assemblyName == AssemblyName || assemblyName == HostAssemblyName);

    /// <summary>
    /// 判断插件引用的契约版本能否被本宿主接受；兼容时返回 <c>null</c>，
    /// 否则返回一句能直接念给用户听的原因。
    /// </summary>
    /// <remarks>
    /// 两条规则：
    /// <list type="number">
    /// <item><b>主版本必须相等</b>：接口改名、改语义、给 <c>ITool</c> 加成员这类破坏性改动才升主版本，
    /// 老插件在新宿主上会"能加载但行为诡异"，必须拒绝。</item>
    /// <item><b>次版本不得高于宿主</b>：M7.4 补的一条。契约 dll 没有强名称，.NET 按简单名绑定，
    /// 于是"插件引用了 v1.1、宿主只有 v1.0"<b>不会</b>让加载失败 ——
    /// 它会一路走到 <c>GetTypes()</c>，在那里因为找不到 <c>IGfxObjectHost</c> 之类的新类型
    /// 抛成 <c>ReflectionTypeLoadException</c>，被加载器当"坏类型"跳过。
    /// 结果就是<b>静默少一个工具</b>：用户只看到"插件好像没装上"，
    /// 而日志里写的是一堆与真正原因无关的类型加载失败。</item>
    /// </list>
    /// </remarks>
    public static string? DescribeIncompatibility(Version? referencedVersion)
    {
        var host = HostVersion;

        if (referencedVersion is null)
        {
            return $"没有引用插件契约（{AssemblyName}），不是白板插件";
        }

        if (referencedVersion.Major != host.Major)
        {
            return $"插件针对契约 v{referencedVersion.Major}，本程序是 v{host.Major}";
        }

        if (referencedVersion.Minor > host.Minor)
        {
            return $"插件需要契约 v{referencedVersion.Major}.{referencedVersion.Minor}，"
                   + $"本程序是 v{host.Major}.{host.Minor}，请更新白板程序后再用这个插件";
        }

        return null;
    }
}
