using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Tools.Plugins;

/// <summary>
/// 一个插件专用的加载上下文：依赖只从插件自己的子目录解析，互不干扰。
/// </summary>
/// <remarks>
/// 用 <c>isCollectible: true</c> 建，是为了给"将来能卸载插件"留门 ——
/// 但 M7.2 <b>不提供</b>运行时卸载（工具实例已被注册表与界面按钮引用着，
/// 真正卸载要连界面一起重建，收益低坑多）。这里是刻意的诚实，不是遗漏。
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginDirectory, string pluginAssemblyPath)
        : base($"plugin:{Path.GetFileName(pluginDirectory)}", isCollectible: true)
    {
        _pluginDirectory = pluginDirectory;

        // ★ 卸载绊线：宿主用 keep-alive 列表钉住了本上下文（见 PluginLoader.KeepAlive），
        // 正常情况下 Unloading 永远不该发生。一旦真的发生（回归或未来改动引入），
        // 日志里第一时间留尸检报告 —— 堆栈会指向是谁、在哪一步触发的回收。
        Unloading += context => AppLog.Warn(
            $"★ 插件加载上下文 {Name} 正在卸载 —— collectible ALC 被回收，"
            + "插件依赖将无法再解析（公式插件等首绑依赖的插件会报 0x80131509）。"
            + "堆栈：" + Environment.StackTrace);

        // resolver 要读同名的 .deps.json 才能正确解析插件的间接依赖。
        // 类库项目默认不生成 deps.json，那时它只是个空壳 —— 下面的目录回落会接手。
        // 所以构造失败不算错误，绝不能让它把插件加载搞挂。
        try
        {
            _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"插件 {Path.GetFileName(pluginDirectory)} 的依赖解析器创建失败，"
                        + $"改用同目录回落：{ex.Message}");
            _resolver = null;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // ★ 契约与宿主一律回到默认上下文（见 PluginContract.IsSharedWithHost）。
        // 返回 null 的语义就是"我不解析，交给默认上下文"——这正是我们要的共享。
        if (PluginContract.IsSharedWithHost(assemblyName.Name))
        {
            return null;
        }

        var path = _resolver?.ResolveAssemblyToPath(assemblyName);

        if (path is null && assemblyName.Name is { Length: > 0 } simpleName)
        {
            // 没有 deps.json 的插件（类库默认如此）：退到"插件目录下的同名 dll"。
            // 这一步让"插件把自己的依赖和 dll 放一起拷贝"这种最常见的分发方式能工作。
            var local = Path.Combine(_pluginDirectory, simpleName + ".dll");
            if (File.Exists(local)) path = local;
        }

        if (path is not null && File.Exists(path))
        {
            return LoadFromAssemblyPath(path);
        }

        // 剩下的交给默认上下文（BCL / System.* / WPF 等）
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);

        if (path is null)
        {
            var local = Path.Combine(_pluginDirectory, unmanagedDllName);
            if (File.Exists(local)) path = local;
        }

        // 返回 IntPtr.Zero 表示"没解析到"，运行时随后会去默认路径找
        return path is not null && File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
