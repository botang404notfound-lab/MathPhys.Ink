using System.Windows.Ink;

namespace MathPhys.Ink.Ink;

/// <summary>
/// 笔迹撤销 / 重做引擎。
/// </summary>
/// <remarks>
/// <para>
/// <b>核心洞察</b>：WPF 的 <see cref="StrokeCollection.StrokesChanged"/> 事件把<b>任何</b>笔迹变化
/// 都描述成"移除了哪些、加入了哪些"——写字、橡皮擦除、清空，无一例外
/// （擦除在 <c>InkCanvas</c> 内部就是"移除原笔画 + 加入被切分后的碎笔画"）。
/// 于是<b>撤销 = 反做（移除 Added、加回 Removed），重做 = 正做</b>，一行几何代码都不用写。
/// </para>
/// <para>
/// 这个设计还有个额外的好处：<b>它不依赖擦除的实现形态</b>。不管内部产生的是
/// 一次 remove + N 次 add，还是单个事件里原地替换，只要变更经由这个事件通知就成立 ——
/// 而"回调里带着完整的 Added/Removed"是 <see cref="StrokeCollection"/> 的公开契约，不是实现细节。
/// </para>
/// <para>
/// 本类是<b>自包含</b>的：构造时接管 <see cref="StrokeCollection"/>，自己订阅变更、自己应用撤销。
/// 宿主只负责"开事务 / 提交事务 / 转发按键"，于是整套撤销逻辑可以脱离 UI 被断言。
/// </para>
/// </remarks>
public sealed class InkHistory
{
    /// <summary>撤销栈的默认深度上限。</summary>
    /// <remarks>
    /// 必须设上限：擦除会把一条笔画拆成若干碎笔画，这些对象被撤销栈引用着无法回收，
    /// 反复擦改时内存会持续增长。100 步远超一节讲评课的批改量，同时给出了回收点。
    /// </remarks>
    public const int DefaultCapacity = 100;

    private readonly StrokeCollection _strokes;
    private readonly int _capacity;

    /// <summary>撤销栈，栈顶在末尾。</summary>
    private readonly List<Edit> _undo = new();

    /// <summary>重做栈，栈顶在末尾。</summary>
    private readonly List<Edit> _redo = new();

    /// <summary>当前事务累积的变更。事务提交前它不属于任何栈。</summary>
    private Edit? _pending;

    private bool _inTransaction;

    /// <summary>正在应用历史。此期间产生的变更<b>绝不能</b>再被记录（否则栈会被自己污染）。</summary>
    private bool _applying;

    public InkHistory(StrokeCollection strokes, int capacity = DefaultCapacity)
    {
        _strokes = strokes ?? throw new ArgumentNullException(nameof(strokes));
        _capacity = Math.Max(1, capacity);

        _strokes.StrokesChanged += OnStrokesChanged;
    }

    /// <summary>历史发生变化（新操作 / 撤销 / 重做 / 重置）。界面据此刷新按钮可用态。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 记录了<b>新的一步</b>笔迹操作（撤销 / 重做不会触发）。
    /// </summary>
    /// <remarks>
    /// 统一时间线（<c>BoardHistory</c>）靠它把墨迹步与图形步按真实顺序排在一起。
    /// 之所以要有这个独立事件，是因为 <see cref="UndoCount"/> 的增减有歧义 ——
    /// "撤销一步"与"重做一步"都会让它变化，光看数字分不清是新操作还是回退。
    /// </remarks>
    public event EventHandler? StepRecorded;

    /// <summary>
    /// 刚记录的那一步被抹掉了（丢笔：<see cref="DiscardAdded"/>）。
    /// </summary>
    /// <remarks>
    /// 统一时间线（<c>BoardHistory</c>）靠它把对应的那一格一起摘掉 —— 只从撤销栈里删、
    /// 不管时间线，时间线上会留下一个永远退不动的「幽灵步」：撤销按钮一直亮着，
    /// 老师按下的那一次 <c>Ctrl+Z</c> 会被这一步白白吞掉。
    /// </remarks>
    public event EventHandler? StepDiscarded;

    /// <summary>历史被丢弃（换文档 / 装载批注）。订阅者应当把自己的时间线一起清掉。</summary>
    public event EventHandler? Resetted;

    /// <summary>撤销栈深度上限。</summary>
    public int Capacity => _capacity;

    /// <summary>当前可撤销的步数。</summary>
    public int UndoCount => _undo.Count;

    /// <summary>当前可重做的步数。</summary>
    public int RedoCount => _redo.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// 开始一个事务：之后产生的所有变更会累加成一个撤销单元。
    /// </summary>
    /// <remarks>
    /// 为什么需要事务：一次拖动擦除会连续产生<b>多次</b> <c>StrokesChanged</c>
    /// （每擦掉一段就改一次集合）。若逐次入栈，用户按一下 Ctrl+Z 只撤销了一小段，根本没法用。
    /// 以"按下 → 抬起"为界合并，才符合"我刚刚做了一件事"的直觉。
    /// <para>
    /// 若上一次事务没有正常收尾（例如笔在屏幕外抬起，配对不上 Up 事件），
    /// 这里先把它提交掉 —— 否则它的变更会被算进这一次，两步并成一步且无从察觉。
    /// </para>
    /// </remarks>
    public void BeginTransaction()
    {
        if (_inTransaction) CommitTransaction();

        _inTransaction = true;
        _pending = new Edit();
    }

    /// <summary>提交事务：有实际变更才入栈，空事务（点一下没画）直接丢弃。</summary>
    public void CommitTransaction()
    {
        if (!_inTransaction) return;

        _inTransaction = false;

        var edit = _pending;
        _pending = null;

        if (edit is null || edit.IsEmpty) return;

        Push(edit);
    }

    /// <summary>撤销一步。返回是否真的撤销了。</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;

        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        Apply(edit, reverse: true);
        _redo.Add(edit);

        RaiseChanged();
        return true;
    }

    /// <summary>重做一步。返回是否真的重做了。</summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;

        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        Apply(edit, reverse: false);
        _undo.Add(edit);

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 在不记录历史的前提下执行一段笔迹操作。
    /// </summary>
    /// <remarks>
    /// <b>从磁盘加载批注时必须走这里。</b>否则"把整份试卷的批注灌进集合"会变成一条撤销记录，
    /// 用户按一次 Ctrl+Z 就把刚恢复的全部批注抹掉 —— 而且看起来像是"批注自己没了"，
    /// 完全联想不到是撤销干的。
    /// <para>
    /// 用可重入的置位而非"清空再恢复"：嵌套调用时内层结束后不应提前解除屏蔽。
    /// </para>
    /// </remarks>
    public void Silently(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        bool previous = _applying;
        _applying = true;

        try
        {
            action();
        }
        finally
        {
            _applying = previous;
        }
    }

    /// <summary>
    /// 「这一笔从来没发生过」：把刚加入的某一笔从历史里彻底抹掉（<b>丢笔专用</b>）。
    /// </summary>
    /// <remarks>
    /// M24 热修⑤（2026-09-28）。断笔检测判定本笔混入手掌瞬移后，宿主会把它从
    /// <c>Strokes</c> 移除；<b>只做移除是不够的</b> —— 那条"加入本笔"的变更已经进了撤销栈，
    /// 老师一按 <c>Ctrl+Z</c> 就会把那条长线<b>整笔复活</b>成一条谁也没见过的鬼影。
    /// <para>
    /// 这里要同时处理两种时机，因为「InkCanvas 什么时候把笔画放进 <c>Strokes</c>」与
    /// 「抬笔回调什么时候到」的先后不是我们可以假定的：
    /// <list type="number">
    /// <item>变更还挂在<b>待提交事务</b>里 ⇒ 从记录里摘掉（提交时净空 ⇒ 不占撤销格）；</item>
    /// <item>变更<b>已经入栈</b> ⇒ 栈顶若是「只加入这一笔、别的什么都没干」，整步抹掉。</item>
    /// </list>
    /// 刻意做成显式调用，而不是在 <see cref="OnStrokesChanged"/> 里按形态猜："加一笔又删一笔"
    /// 与"卷面上只有一笔时按清除"在事件形态上无法区分，猜错会把清除笔迹的撤销能力吞掉。
    /// </para>
    /// </remarks>
    /// <returns>是否真的抹掉了记录（false = 本来就没记过，属正常情形）。</returns>
    public bool DiscardAdded(Stroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);

        bool forgot = false;

        // ① 变更还在待提交事务里
        if (_pending is { } pending) forgot |= pending.RemoveAdded(stroke);

        // ② 变更已经入栈（栈顶恰好是"只加入这一笔"）
        if (_undo.Count > 0 && _undo[^1].IsPureAddOf(stroke))
        {
            _undo.RemoveAt(_undo.Count - 1);
            forgot = true;
            // 时间线同步摘掉那一格（待提交事务的情形没有记过步，自然不发）
            StepDiscarded?.Invoke(this, EventArgs.Empty);
        }

        if (forgot) RaiseChanged();

        return forgot;
    }

    /// <summary>
    /// 丢弃全部历史。
    /// </summary>
    /// <remarks>
    /// <b>换文档时必须调用</b>：换文档会清空笔迹，此刻若留着旧历史，
    /// 一次 Ctrl+Z 就会把<b>上一份试卷的批注复活到新试卷上</b>——而且看起来像是"撤销出了鬼影"，
    /// 极难联想到根因。注意调用时机要在笔迹清空<b>之后</b>（清空本身也会入栈一条记录）。
    /// </remarks>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _pending = null;
        _inTransaction = false;

        Resetted?.Invoke(this, EventArgs.Empty);
        RaiseChanged();
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        // 应用历史时自己改集合也会触发本事件，这里必须直接返回。
        // 漏掉这一条就是"撤销动作被记成新操作"⇒ 栈被自己污染、反复撤销立刻失效。
        if (_applying) return;

        var target = _pending;

        if (target is null)
        {
            // 事务外发生的变更（程序调用 ClearStrokes、SetDocument 清空等）自成一步。
            // 这条兜底不能省：否则这类"非输入产生"的变化会被静默丢掉，撤销不了。
            var single = new Edit();
            single.Capture(e);
            Push(single);
            return;
        }

        target.Capture(e);
    }

    /// <summary>把一条新记录压入撤销栈；任何新操作都会让重做栈作废（标准语义）。</summary>
    private void Push(Edit edit)
    {
        _undo.Add(edit);
        _redo.Clear();

        // 超出上限就丢最早的记录 —— 那一刻它引用的笔画对象才可能被回收
        int overflow = _undo.Count - _capacity;
        if (overflow > 0) _undo.RemoveRange(0, overflow);

        StepRecorded?.Invoke(this, EventArgs.Empty);
        RaiseChanged();
    }

    /// <summary>
    /// 正向或反向应用一条记录。
    /// </summary>
    /// <remarks>
    /// 之所以能只靠 <c>Add</c>/<c>Remove</c> 完成撤销，是因为我们存的是<b>笔画对象引用</b>：
    /// 被移除的笔画仍被本引擎握着、不会被回收，加回去就是原来那一个对象，
    /// 几何、颜色、压力样式全部原样 —— 不存在"重建导致失真"的问题。
    /// </remarks>
    private void Apply(Edit edit, bool reverse)
    {
        _applying = true;

        try
        {
            var toRemove = reverse ? edit.Added : edit.Removed;
            var toAdd = reverse ? edit.Removed : edit.Added;

            // 先移除后加入：两个方向的顺序都固定成"先腾地方再放东西"，
            // 避免同一条笔画在一次记录里既被移除又被加入时，Contains 判断撞车。
            foreach (var stroke in toRemove)
            {
                if (_strokes.Contains(stroke)) _strokes.Remove(stroke);
            }

            foreach (var stroke in toAdd)
            {
                if (!_strokes.Contains(stroke)) _strokes.Add(stroke);
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 一条撤销记录：正向操作中"加入了什么、移除了什么"。
    /// </summary>
    /// <remarks>
    /// 用两个列表而不是"操作类型 + 笔画"，是因为一次事务里可能先移除后加入
    /// （擦除就是典型），只有把两个方向的差异都记住才能精确还原。
    /// </remarks>
    private sealed class Edit
    {
        public List<Stroke> Added { get; } = new();

        public List<Stroke> Removed { get; } = new();

        /// <summary>
        /// 这一步是否<b>净变化为零</b>。
        /// </summary>
        /// <remarks>
        /// ★ M24 热修⑤（2026-09-28）：同一笔既进了 <see cref="Added"/> 又进了
        /// <see cref="Removed"/> 时（程序自己加又删 —— 丢笔、自愈清理），净变化是零，
        /// 必须按「空」处理。照老写法「两个列表非空即非空」，一个事务里的丢笔也会占掉
        /// 一格撤销，<c>Ctrl+Z</c> 再把那条长线复活回来。
        /// <para>
        /// 擦除不受影响：它是「移除原笔 + 加入碎笔」，两边是<b>不同对象</b>，净变化不为零。
        /// </para>
        /// </remarks>
        public bool IsEmpty
        {
            get
            {
                int net = 0;
                foreach (var stroke in Added) if (!ContainsByReference(Removed, stroke)) net++;
                foreach (var stroke in Removed) if (!ContainsByReference(Added, stroke)) net++;
                return net == 0;
            }
        }

        /// <summary>把某一笔从「加入」清单里摘掉；返回是否真的摘掉了（丢笔：这一笔从未发生）。</summary>
        public bool RemoveAdded(Stroke stroke)
        {
            bool removed = false;

            for (int i = Added.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(Added[i], stroke))
                {
                    Added.RemoveAt(i);
                    removed = true;
                }
            }

            return removed;
        }

        /// <summary>这一步是不是「只加入了这一笔、别的什么都没干」。</summary>
        public bool IsPureAddOf(Stroke stroke)
            => Removed.Count == 0 && Added.Count == 1 && ReferenceEquals(Added[0], stroke);

        /// <summary>按<b>引用</b>查一笔是否在清单里（笔画对象不做值相等比较）。</summary>
        private static bool ContainsByReference(List<Stroke> strokes, Stroke stroke)
        {
            foreach (var candidate in strokes)
            {
                if (ReferenceEquals(candidate, stroke)) return true;
            }

            return false;
        }

        public void Capture(StrokeCollectionChangedEventArgs e)
        {
            foreach (var stroke in e.Added) Added.Add(stroke);
            foreach (var stroke in e.Removed) Removed.Add(stroke);
        }
    }
}
