using System.IO;
using System.Reflection;
using MathPhys.Ink.Gfx;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Tools.Plugins;

/// <summary>
/// 插件加载器：扫 <c>plugins\</c> → 每个插件一个上下文 → 找到 <see cref="IWhiteBoardPlugin"/> → 注册工具。
/// </summary>
/// <remarks>
/// 目录约定：<c>plugins\&lt;插件名&gt;\&lt;插件名&gt;.dll</c>（每个插件一个子目录，各自带依赖，互不干扰）。
/// 放在<b>程序目录下</b>而不是 <c>%LOCALAPPDATA%</c>，与侧车文件同一个理由：
/// 老师拷 U 盘把整个文件夹拷走，插件跟着走。
/// <para>
/// <b>停用某个插件 = 把它的子目录改名成 <c>_</c> 开头</b>（例如 <c>plugins\_protractor</c>），
/// 或者挪进 <c>plugins\_disabled\</c>。不引入任何配置文件 ——
/// 老师拖一下文件夹就完成操作，也没有"配置漂移"可言。
/// </para>
/// <para>
/// <b>失败隔离</b>是这里的第一原则：任何一步出错都只损失那一个插件 —— 记日志、写进报告、继续下一个。
/// 绝不抛异常、绝不弹窗。老师站在讲台上，程序崩了没有第二次机会；
/// 而少一个工具只是少一个功能，讲完这节课再查也不迟。
/// </para>
/// <para>
/// <b>诚实说明</b>：.NET 里没有可靠的进程内插件沙箱，插件就是这个进程里的普通代码，
/// 能读能写能做任何事。<b>只装自己信任的插件</b> —— 这句话也写在 <c>plugins\说明.txt</c> 里。
/// </para>
/// </remarks>
public static class PluginLoader
{
    /// <summary>插件目录名（相对程序目录）。</summary>
    public const string DirectoryName = "plugins";

    /// <summary>前缀为它的子目录一律跳过（停用某插件的方式）。</summary>
    public const string DisabledPrefix = "_";

    /// <summary>默认插件目录：程序目录下的 <c>plugins\</c>。</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, DirectoryName);

    /// <summary>
    /// 插件加载上下文的永久引用列表（keep-alive）。
    /// </summary>
    /// <remarks>
    /// ★★★ 这是 <see cref="PluginLoadContext"/> 的保命索，绝不能删。
    /// collectible ALC 没有任何引用时会被 GC 回收（.NET 7 起官方文档明说：
    /// "keep a reference to that AssemblyLoadContext forever to make sure it's never collected"）。
    /// 2026-09-24 真机事故：LoadOne 里 ALC 只是局部变量，扫描完无人引用，
    /// 真机 WPF 重负载下 GC 把上下文收走 —— 首次调用公式插件的 Build 时
    /// JIT 绑定 XamlMath 走进已死的上下文，报
    /// "AssemblyLoadContext is unloading or was already unloaded"（0x80131509）。
    /// M7.2 本就不提供运行时卸载，这里只是把"事实上永不卸载"变成"明示永不卸载"。
    /// 同时攥住 Assembly 引用，双保险（反射得到的类型/实例理论上足以保活，
    /// 但运行时的可回收判定在某些边角场景没有把它们算作根，实测真机中招）。
    /// </remarks>
    private static readonly List<(PluginLoadContext Context, Assembly Assembly)> KeepAlive = new();

    /// <summary>
    /// 扫描并加载全部插件。
    /// </summary>
    /// <param name="registry">工具注册表（插件唯一的注册入口）。</param>
    /// <param name="renderers">图形渲染器注册表；为 <c>null</c> 时跳过渲染器登记（harness 用）。</param>
    /// <param name="pluginsDirectory">插件目录；默认 <see cref="DefaultDirectory"/>（测试时传临时目录）。</param>
    /// <remarks>
    /// 只在启动时扫一次：不做热插拔。运行中重新扫目录要连界面按钮一起重建，
    /// 而"装插件"本来就是个低频动作 —— 重启程序一次没什么可惜的。
    /// </remarks>
    public static PluginLoadReport LoadAll(
        IToolRegistry registry,
        GfxRendererCatalog? renderers = null,
        string? pluginsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var directory = pluginsDirectory ?? DefaultDirectory;
        var report = new PluginLoadReport { PluginsDirectory = directory };

        if (!Directory.Exists(directory))
        {
            // 没有插件目录是"没装插件"的正常状态，不是错误：不建目录、不报警
            AppLog.Info($"未发现插件目录（{directory}），跳过插件加载。");
            return report;
        }

        string[] subDirectories;
        try
        {
            subDirectories = Directory.GetDirectories(directory);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"无法枚举插件目录 {directory}：{ex.Message}");
            return report;
        }

        // 按名字排序，让"哪个插件先注册"是确定的（工具顺序决定按钮顺序）
        Array.Sort(subDirectories, StringComparer.OrdinalIgnoreCase);

        int disabled = 0;

        foreach (var subDirectory in subDirectories)
        {
            // "_" 开头 = 用户手动停用的插件。静默跳过是有意的：
            // 刻意停用的东西不该在每次启动时留下一句警告。
            if (Path.GetFileName(subDirectory).StartsWith(DisabledPrefix, StringComparison.Ordinal))
            {
                disabled++;
                continue;
            }

            report.Add(LoadOne(registry, renderers, subDirectory));
        }

        AppLog.Info($"插件扫描完成（{report.Summary}；停用 {disabled} 个）。{report.Details}");
        return report;
    }

    /// <summary>加载单个插件子目录；任何异常都在这里被收敛成一条报告。</summary>
    private static PluginLoadEntry LoadOne(IToolRegistry registry, GfxRendererCatalog? renderers, string directory)
    {
        var name = Path.GetFileName(directory);
        var assemblyPath = FindPluginAssembly(directory, name);

        if (assemblyPath is null)
        {
            const string reason = "目录里没有 dll（约定：plugins\\插件名\\插件名.dll）";
            AppLog.Warn($"插件 {name} 未加载：{reason}");
            return new PluginLoadEntry(directory, string.Empty, PluginLoadStatus.Rejected, reason, 0);
        }

        var loadContext = new PluginLoadContext(directory, assemblyPath);

        Assembly assembly;
        try
        {
            assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception ex)
        {
            var reason = $"dll 无法加载：{ex.GetType().Name} {ex.Message}";
            AppLog.Warn($"插件 {name} 未加载：{reason}", ex);
            return new PluginLoadEntry(directory, assemblyPath, PluginLoadStatus.Failed, reason, 0);
        }

        // ★ keep-alive：钉住 ALC 与程序集，防止 collectible 上下文被 GC 回收
        //（详见 KeepAlive 的 remarks —— 2026-09-24 真机公式插件 0x80131509 事故）。
        KeepAlive.Add((loadContext, assembly));

        // 契约版本：读插件的引用元数据（不能用 PluginApi.Version 常量，见 PluginContract）
        Version? contractVersion = null;
        try
        {
            contractVersion = assembly.GetReferencedAssemblies()
                .FirstOrDefault(reference => reference.Name == PluginContract.AssemblyName)
                ?.Version;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"插件 {name} 的引用元数据读取失败：{ex.Message}");
        }

        if (PluginContract.DescribeIncompatibility(contractVersion) is { } incompatibility)
        {
            AppLog.Warn($"插件 {name} 被拒绝：{incompatibility}");
            return new PluginLoadEntry(directory, assemblyPath, PluginLoadStatus.Rejected, incompatibility, 0);
        }

        // 找出插件类。GetTypes 在"某个类型加载失败"时会整体抛异常，
        // 但异常对象里带着"哪些类型是好的" —— 用它们继续，别因为一个坏类型丢掉整个插件。
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;

            var messages = new List<string>();
            foreach (var loaderException in ex.LoaderExceptions)
            {
                if (loaderException is null) continue;
                if (!messages.Contains(loaderException.Message)) messages.Add(loaderException.Message);
                if (messages.Count == 3) break;
            }

            AppLog.Warn($"插件 {name} 有部分类型加载失败，已跳过：{string.Join("；", messages)}");
        }
        catch (Exception ex)
        {
            var reason = $"无法读取类型：{ex.GetType().Name} {ex.Message}";
            AppLog.Warn($"插件 {name} 未加载：{reason}", ex);
            return new PluginLoadEntry(directory, assemblyPath, PluginLoadStatus.Failed, reason, 0);
        }

        int registered = 0;
        int pluginClasses = 0;
        string displayName = string.Empty;

        foreach (var type in types)
        {
            // 接口判断走共享契约 ⇒ 能通过就说明类型身份是统一的
            if (type is null
                || type.IsAbstract
                || type.IsInterface
                || !typeof(IWhiteBoardPlugin).IsAssignableFrom(type))
            {
                continue;
            }

            pluginClasses++;

            try
            {
                if (Activator.CreateInstance(type) is not IWhiteBoardPlugin plugin)
                {
                    AppLog.Warn($"插件类 {type.FullName} 没有公开无参构造函数，已跳过。");
                    continue;
                }

                if (displayName.Length == 0) displayName = plugin.Name;

                int before = registry.Tools.Count;
                plugin.Register(registry);
                registered += registry.Tools.Count - before;

                AppLog.Info($"插件 {name}：{plugin.Name} 已注册"
                            + $"（工具 {registry.Tools.Count - before} 个）— {type.FullName}");
            }
            catch (Exception ex)
            {
                // 一个插件类炸了不影响同 dll 里的其他插件类，更不影响别的插件
                AppLog.Warn($"插件 {name} 的 {type.FullName} 注册失败，已跳过："
                            + $"{ex.GetType().Name} {ex.Message}", ex);
            }
        }

        if (pluginClasses == 0)
        {
            const string reason = "dll 里没有实现 IWhiteBoardPlugin 的类型";
            AppLog.Warn($"插件 {name} 被拒绝：{reason}");
            return new PluginLoadEntry(directory, assemblyPath, PluginLoadStatus.Rejected, reason, 0);
        }

        // 图形渲染器：dll 里实现 IGfxObjectRenderer 的类型由加载器自动登记。
        // 为什么不让插件在 Register 里自己登记：那样得先给 IToolRegistry 加一个"注册渲染器"的入口，
        // 于是"注册表"这个概念就被撑成了"什么都往里塞"的口袋。渲染器不是工具，
        // 它是"图形库"这一层的贡献，单独扫一遍更干净，也让插件作者少写一段样板代码。
        int rendererCount = RegisterRenderers(renderers, types, displayName.Length > 0 ? displayName : name);

        if (registered == 0 && rendererCount == 0)
        {
            // 插件类都在，但一个工具、一个渲染器都没入库：注册时全抛异常，或 Id 与已有工具撞车被拒。
            // 对外就是"这个插件什么也没提供"，所以记 Failed 而不是 Loaded ——
            // 报告里写着 Loaded、用户却找不到新按钮，是最招人烦的那种假成功。
            const string reason = "插件类注册时全部失败（工具 Id 可能撞车，详见日志）";
            AppLog.Warn($"插件 {name} 加载了但没有工具入库：{reason}");
            return new PluginLoadEntry(directory, assemblyPath, PluginLoadStatus.Failed, reason, 0);
        }

        var summary = rendererCount > 0
            ? $"已注册 {registered} 个工具、{rendererCount} 个图形渲染器"
            : $"已注册 {registered} 个工具";

        return new PluginLoadEntry(directory, assemblyPath, PluginLoadStatus.Loaded, summary, registered, rendererCount);
    }

    /// <summary>
    /// 登记 dll 里的图形渲染器。
    /// </summary>
    /// <remarks>
    /// 逐个 try/catch：一个渲染器构造失败不该让同 dll 里的其它渲染器一起消失，
    /// 更不该影响已经注册好的工具。
    /// </remarks>
    private static int RegisterRenderers(GfxRendererCatalog? catalog, Type?[] types, string pluginName)
    {
        if (catalog is null) return 0;

        int count = 0;

        foreach (var type in types)
        {
            if (type is null
                || type.IsAbstract
                || type.IsInterface
                || !typeof(IGfxObjectRenderer).IsAssignableFrom(type))
            {
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is not IGfxObjectRenderer renderer)
                {
                    AppLog.Warn($"图形渲染器 {type.FullName} 没有公开无参构造函数，已跳过。");
                    continue;
                }

                if (catalog.Register(renderer, pluginName))
                {
                    count++;
                    AppLog.Info($"图形渲染器已登记：{renderer.Kind} — {type.FullName}（来自 {pluginName}）");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"图形渲染器 {type.FullName} 创建失败，已跳过：{ex.GetType().Name} {ex.Message}");
            }
        }

        return count;
    }

    /// <summary>
    /// 在插件子目录里找入口 dll：优先与目录同名的那个，退而取第一个。
    /// </summary>
    /// <remarks>
    /// 宽容一点：老师或同事拷插件时未必严格按 <c>插件名.dll</c> 命名，
    /// 而"目录里只有一个 dll"的场合，取第一个就是这个插件的入口。
    /// 多个 dll 且没有同名时<b>不猜</b> —— 猜错会把依赖当入口，报出难以理解的错误。
    /// </remarks>
    private static string? FindPluginAssembly(string directory, string pluginName)
    {
        try
        {
            var expected = Path.Combine(directory, pluginName + ".dll");
            if (File.Exists(expected)) return expected;

            var candidates = Directory.GetFiles(directory, "*.dll");
            return candidates.Length == 1 ? candidates[0] : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"枚举插件目录 {directory} 失败：{ex.Message}");
            return null;
        }
    }
}
