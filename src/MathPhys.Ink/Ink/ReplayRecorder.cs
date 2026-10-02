using System;
using System.Collections.Generic;
using System.Windows.Ink;

namespace MathPhys.Ink.Ink;

/// <summary>回放的一步：一次 <c>StrokesChanged</c> 事件里"加入了什么、移除了什么"。</summary>
/// <remarks>
/// 与 <see cref="InkHistory"/> 的撤销记录同构（橡皮擦除本来就表现为一次 remove + N 次 add），
/// 于是"正着施加每一步"就能重现整段书写过程。
/// </remarks>
public sealed class ReplayStep
{
    public ReplayStep(DateTime at, IReadOnlyList<System.Windows.Ink.Stroke> added,
                      IReadOnlyList<System.Windows.Ink.Stroke> removed)
    {
        At = at;
        Added = added;
        Removed = removed;
    }

    /// <summary>这一步发生的时刻（本机时钟，仅供诊断；回放不按真实时间节奏走）。</summary>
    public DateTime At { get; }

    /// <summary>这一步加入的笔画。</summary>
    public IReadOnlyList<System.Windows.Ink.Stroke> Added { get; }

    /// <summary>这一步移除的笔画（擦除的"原笔"）。</summary>
    public IReadOnlyList<System.Windows.Ink.Stroke> Removed { get; }

    /// <summary>这一步是否什么都没做（防御：空步不该被记录，这里是双保险）。</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
}

/// <summary>
/// 笔迹回放的记录器（M12）：订阅笔画变更，攒出"从头到尾的操作序列"。
/// </summary>
/// <remarks>
/// <para>
/// <b>V1 范围（有意收缩）</b>：只记录<b>本次会话</b>、只回放墨迹（图形对象不回放），
/// 不做持久化 —— 跨次回放需要给 <c>.twb</b> 加新载荷，收益配不上格式成本。
/// 讲评课的真实场景是"这页讲完马上重放一遍给全班看"，会话内回放已覆盖。
/// </para>
/// <para>
/// 步数有上限（防一节长课撑爆内存）：超过上限后<b>停止记录</b>并标记截断 ——
/// 截断后回放不可用（重放出来的终态会与现场不符，宁可不放也不放错的）。
/// </para>
/// </remarks>
public sealed class ReplayRecorder
{
    /// <summary>步数上限。超过即停记并标记截断（见类型说明）。</summary>
    public const int MaxSteps = 400;

    private readonly List<ReplayStep> _steps = new();

    private bool _armed;

    /// <summary>开始记录。重复调用会清掉之前的过程（回放的是"最近一段"）。</summary>
    public void Arm()
    {
        _steps.Clear();
        _armed = true;
        Truncated = false;
    }

    /// <summary>停止记录（已记录的步保留，供回放）。</summary>
    public void Disarm() => _armed = false;

    /// <summary>是否因超过步数上限而停记（此时回放被禁用）。</summary>
    public bool Truncated { get; private set; }

    /// <summary>当前记录的步数。</summary>
    public int Count => _steps.Count;

    /// <summary>记录的全部步（按发生顺序，只读）。</summary>
    public IReadOnlyList<ReplayStep> Steps => _steps;

    /// <summary>
    /// 观察一次笔画变更。未 Arm、已截断、或空变更时忽略。
    /// </summary>
    /// <remarks>
    /// ★ 调用方（宿主）必须保证"回放重演 / 图层显隐"期间<b>不要</b>把事件递进来 ——
    /// 那些是程序自己的动作，记进去会把回放变成无限套娃。
    /// </remarks>
    public void Observe(StrokeCollectionChangedEventArgs e)
    {
        if (!_armed || Truncated) return;
        if (e is null) return;

        var added = new List<System.Windows.Ink.Stroke>();
        foreach (var stroke in e.Added) added.Add(stroke);

        var removed = new List<System.Windows.Ink.Stroke>();
        foreach (var stroke in e.Removed) removed.Add(stroke);

        if (added.Count == 0 && removed.Count == 0) return;

        if (_steps.Count >= MaxSteps)
        {
            Truncated = true;
            _armed = false;
            return;
        }

        _steps.Add(new ReplayStep(DateTime.Now, added, removed));
    }
}
