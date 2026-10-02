namespace MathPhys.Ink.Plugins;

/// <summary>
/// 插件入口。一个插件 dll 里实现它的类会被宿主实例化并调用 <see cref="Register"/>。
/// </summary>
/// <remarks>
/// 实现类必须有一个<b>公开无参构造函数</b> —— 宿主不知道怎么构造你，也不该知道。
/// </remarks>
public interface IWhiteBoardPlugin
{
    /// <summary>插件显示名（日志与"已加载插件"清单里用它）。</summary>
    string Name { get; }

    /// <summary>
    /// 注册这个插件提供的工具。
    /// </summary>
    /// <remarks>
    /// 这里<b>要尽量短、尽量不要做重活</b>：它跑在程序启动的关键路径上，
    /// 抛异常会被宿主捕获并跳过本插件（不影响启动），但用户就会少一个工具。
    /// 需要读文件、连网络之类的事请等到 <see cref="ITool.Activate"/> 再做。
    /// </remarks>
    void Register(IToolRegistry registry);
}
