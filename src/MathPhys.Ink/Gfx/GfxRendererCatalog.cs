using System;
using System.Collections.Generic;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 对象种类 → 渲染器的注册表。
/// </summary>
/// <remarks>
/// 这是"图形库"这一层的入口：插件把自己认识的 Kind 画法注册进来，
/// 宿主对着 Kind 查表渲染、量尺寸。
/// <para>
/// <b>重复 Kind 直接拒绝并记日志</b>，不覆盖：两个插件都声称自己画 <c>vector</c>，
/// "后者替换前者"会表现成"重启一次图形的样子就变了"，比明确拒绝难查得多。
/// </para>
/// </remarks>
public sealed class GfxRendererCatalog
{
    private readonly Dictionary<string, IGfxObjectRenderer> _renderers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

    /// <summary>已注册的 Kind 数。</summary>
    public int Count => _renderers.Count;

    /// <summary>已注册的全部 Kind（按钮/诊断用）。</summary>
    public IReadOnlyCollection<string> Kinds => _renderers.Keys;

    /// <summary>
    /// 注册一个渲染器。
    /// </summary>
    /// <param name="renderer">渲染器。</param>
    /// <param name="pluginName">
    /// 提供它的插件显示名（如「量角器（学科工具）」）。留空时退回程序集简单名。
    /// 这个名字会被写进每个新落成的对象，用于"插件被禁用时告诉用户少了谁"。
    /// </param>
    /// <remarks>
    /// 本方法<b>不抛异常</b>：它跑在插件加载的关键路径上（每个插件一次），
    /// 一个坏渲染器不该让宿主启动失败，也不该带走同一个 dll 里的其它插件类。
    /// </remarks>
    public bool Register(IGfxObjectRenderer? renderer, string? pluginName = null)
    {
        if (renderer is null) return false;

        if (string.IsNullOrWhiteSpace(renderer.Kind))
        {
            AppLog.Warn($"渲染器 {renderer.GetType().FullName} 的 Kind 为空，已拒绝注册。");
            return false;
        }

        if (_renderers.TryGetValue(renderer.Kind, out var existing))
        {
            AppLog.Warn($"渲染器 Kind 重复：{renderer.Kind}"
                        + $"（已有 {existing.GetType().Name}，又来 {renderer.GetType().Name}），已拒绝后一个。");
            return false;
        }

        _renderers.Add(renderer.Kind, renderer);
        _owners[renderer.Kind] = ResolveOwner(renderer, pluginName);
        return true;
    }

    /// <summary>查 Kind 对应的渲染器；没注册过返回 <c>null</c>（调用方用占位框兜底）。</summary>
    public IGfxObjectRenderer? Find(string? kind)
        => !string.IsNullOrEmpty(kind) && _renderers.TryGetValue(kind, out var renderer) ? renderer : null;

    /// <summary>是否认识这个 Kind。</summary>
    public bool Contains(string? kind) => Find(kind) is not null;

    /// <summary>
    /// 查 Kind 对应的渲染器<b>能不能提供参数面板</b>；不能则返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// ★ 契约<b>只加不改</b>：<see cref="IGfxParameterProvider"/> 是可选接口，
    /// 老插件（量角器 / 直尺 / 坐标系）不实现它，这里就返回 <c>null</c>，
    /// 宿主据此隐藏面板 —— 不会因为"插件版本旧"而出任何问题。
    /// <para>
    /// 用 <c>is</c> 判断而不是给 <see cref="IGfxObjectRenderer"/> 加成员：
    /// 加成员会让所有已分发的插件 dll 在加载时抛 <c>TypeLoadException</c>。
    /// </para>
    /// </remarks>
    public IGfxParameterProvider? FindParameterProvider(string? kind)
        => Find(kind) as IGfxParameterProvider;

    /// <summary>某个 Kind 由谁提供；不认识时为空串。</summary>
    public string OwnerOf(string? kind)
        => !string.IsNullOrEmpty(kind) && _owners.TryGetValue(kind, out string? owner) ? owner : string.Empty;

    private static string ResolveOwner(IGfxObjectRenderer renderer, string? pluginName)
    {
        if (!string.IsNullOrWhiteSpace(pluginName)) return pluginName.Trim();

        // 退回程序集名：内置渲染器（宿主自己注册的）走这条，
        // 插件渲染器一般由加载器带上插件显示名，这里只是兜底。
        try
        {
            return renderer.GetType().Assembly.GetName().Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
