using System;
using System.Collections.Generic;
using System.Linq;

namespace MathPhys.Ink.Workflows;

/// <summary>一个工作台模式：名字 + 一串命令 Id（应用模式 = 按顺序执行这串命令）。</summary>
/// <remarks>
/// <b>模式就是内置宏</b>——同一套命令总线消费者，不需要第二套机制。
/// V1 只内置三个模式；自定义序列走 <see cref="MacroFile"/>（用户手写的宏），不提供模式文件加载。
/// </remarks>
public sealed record BoardMode(string Id, string Name, IReadOnlyList<string> Commands)
{
    /// <summary>逐条执行模式里的命令；认不出的跳过并回报（绝不中断整串）。</summary>
    /// <returns>没执行上的命令 Id 列表（空 = 全部生效）。</returns>
    public IReadOnlyList<string> Apply(CommandBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        var missing = new List<string>();
        foreach (string id in Commands)
        {
            if (!bus.Execute(id)) missing.Add(id);
        }

        return missing;
    }
}

/// <summary>内置工作台模式（M12）。</summary>
public static class BoardModes
{
    /// <summary>批改（默认）：现状行为 —— 红笔、批注层、悬浮球 auto。</summary>
    public static readonly BoardMode Grading = new(
        "grading", "批改",
        new[] { "tool.pen", "ink.color.default", "layer.active.annotation", "layer.show.draft", "ball.mode.auto" });

    /// <summary>讲评：红笔 + 显示草稿层 + 题号导航可用（按钮本就常驻，这里只是把笔与层摆到位）。</summary>
    public static readonly BoardMode Review = new(
        "review", "讲评",
        new[] { "tool.pen", "ink.color.default", "layer.active.annotation", "layer.show.draft", "ball.mode.auto" });

    /// <summary>演示：工具栏藏起来、悬浮球常驻，屏幕全部留给卷面。</summary>
    public static readonly BoardMode Present = new(
        "present", "演示",
        new[] { "toolbar.hide", "ball.mode.always", "tool.pen", "ink.color.default" });

    /// <summary>全部内置模式（顺序即菜单顺序）。</summary>
    public static IReadOnlyList<BoardMode> All { get; } = new[] { Grading, Review, Present };

    /// <summary>按 Id 找模式；找不到返回 <c>null</c>。</summary>
    public static BoardMode? Find(string? id)
        => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
}
