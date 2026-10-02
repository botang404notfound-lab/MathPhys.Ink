using System.Collections.Generic;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 工具注册表 —— 插件唯一的注册入口。
/// </summary>
/// <remarks>
/// 刻意只给插件 <see cref="Add"/>：插件能加工具，但<b>不能</b>移除、替换、或读取别家工具去改它。
/// 一个插件只要拿到"能删别人工具"的权限，出问题时就没人说得清是谁干的。
/// </remarks>
public interface IToolRegistry
{
    /// <summary>注册一个工具。Id 重复会被拒绝（并记日志）。</summary>
    void Add(ITool tool);

    /// <summary>当前已注册的工具（只读快照，顺序即按钮顺序）。</summary>
    IReadOnlyList<ITool> Tools { get; }
}
