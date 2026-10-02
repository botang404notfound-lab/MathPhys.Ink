using System;
using System.Collections.Generic;
using System.Linq;

namespace MathPhys.Ink.Workflows;

/// <summary>
/// 命令总线：全窗口"可被编程触发的一个动作"的唯一登记处（M12）。
/// </summary>
/// <remarks>
/// <para>
/// 悬浮球菜单、自定义宏、工作台模式这三件事全都要<b>以程序方式触发与按钮相同的动作</b>。
/// 若没有总线，悬浮球只能去反射调私有方法，宏更是无从下手 ——
/// 所以先把"按钮在做什么"统一成 <c>Id → 动作</c> 的一张表，三类消费者都只认 Id。
/// </para>
/// <para>
/// 约定：
/// <list type="bullet">
/// <item>Id 用小写点分（<c>file.save</c> / <c>tool.pen</c> / <c>ink.color.0</c>）；</item>
/// <item><b>重复登记同一 Id 是编程错误</b>，直接抛异常 —— 静默覆盖会让"按钮与宏做不同的事"
/// 这种分叉悄悄发生；</item>
/// <item><c>Execute</c> 对认不出的 Id 返回 <c>false</c> 并留给调用方决定怎么提示 ——
/// 宏文件是用户手写的，认不出一条不该中断整串。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class CommandBus
{
    private readonly Dictionary<string, Action> _commands = new(StringComparer.Ordinal);

    /// <summary>已登记的命令 Id（按登记顺序）。</summary>
    public IReadOnlyList<string> Ids => _commands.Keys.ToList();

    /// <summary>登记一个命令。Id 重复时抛异常（见类型说明）。</summary>
    public void Register(string id, Action action)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("命令 Id 不能为空", nameof(id));
        ArgumentNullException.ThrowIfNull(action);

        if (_commands.ContainsKey(id))
        {
            throw new InvalidOperationException($"命令 Id 重复登记：{id}");
        }

        _commands.Add(id, action);
    }

    /// <summary>是否存在某个命令。</summary>
    public bool Has(string id) => _commands.ContainsKey(id);

    /// <summary>
    /// 从一批 Id 里挑出<b>没有登记</b>的那些（返回空列表 = 全部可用，按传入顺序去重）。
    /// </summary>
    /// <remarks>
    /// ★ 给"程序自己写死的 Id 清单"做自检用。球菜单与内置模式都把 Id 硬编码在源码里，
    /// 而 <see cref="Execute"/> 对认不出的 Id 只返回 <c>false</c> —— 调用方一旦不看返回值，
    /// 表现就是"点了没反应"，日志、状态栏、回归全静默。
    /// 把这件事实摆成一个可断言、可报错的形状，是唯一能让它无法再悄悄发生的方法。
    /// </remarks>
    public IReadOnlyList<string> Missing(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var missing = new List<string>();

        foreach (string id in ids)
        {
            if (string.IsNullOrEmpty(id) || _commands.ContainsKey(id)) continue;
            if (missing.Contains(id)) continue;
            missing.Add(id);
        }

        return missing;
    }

    /// <summary>
    /// 执行一个命令。返回是否真的执行了（Id 不存在时返回 <c>false</c>，不抛异常）。
    /// </summary>
    public bool Execute(string id)
    {
        if (id is not null && _commands.TryGetValue(id, out var action))
        {
            action();
            return true;
        }

        return false;
    }
}
