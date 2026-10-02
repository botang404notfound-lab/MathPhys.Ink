using System;
using System.Collections.Generic;
using MathPhys.Ink.Ink;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 全画布的撤销 / 重做<b>统一时间线</b>：墨迹步与图形步按真实发生顺序排在一起。
/// </summary>
/// <remarks>
/// <b>为什么必须有它</b>：墨迹有自己的历史（<see cref="InkHistory"/>，M6 已验收），
/// 图形也有自己的历史（<see cref="GfxHistory"/>）。若让 <c>Ctrl+Z</c> 去猜"该退哪一个"，
/// 老师按下撤销时<b>无法预知</b>退的是字还是图 —— 讲台上这是不可接受的。
/// <para>
/// 做法很轻：时间线只记"这一步是墨迹还是图形"（一个枚举），真正的内容分别存在两条子历史里。
/// 撤销时看指针处是哪种，就派给哪条子历史。于是两条子历史的实现都可以原样保留
/// （墨迹那套"笔画引用 + 反做"、图形那套"快照"），不需要为了统一而改写任何一边。
/// </para>
/// <para>
/// 子历史自己做完事后会各自通知，所以界面按钮的可用态只需要订阅本类。
/// </para>
/// </remarks>
public sealed class BoardHistory
{
    /// <summary>一步操作属于哪一类。</summary>
    public enum StepKind
    {
        /// <summary>笔迹（写字 / 擦除 / 清空 / 直尺落墨）。</summary>
        Ink,

        /// <summary>图形对象（新增 / 移动 / 旋转 / 缩放 / 删除 / 改参数）。</summary>
        Gfx,
    }

    private readonly InkHistory _ink;
    private readonly GfxHistory _gfx;

    /// <summary>时间线；末尾是最近一步。</summary>
    private readonly List<StepKind> _timeline = new();

    /// <summary>已经"应用到位"的步数 —— 也就是当前状态在时间线上的位置。</summary>
    private int _applied;

    public BoardHistory(InkHistory ink, GfxHistory gfx)
    {
        _ink = ink ?? throw new ArgumentNullException(nameof(ink));
        _gfx = gfx ?? throw new ArgumentNullException(nameof(gfx));

        // 墨迹层自己产生新步时通知进来（撤销/重做不会触发 StepRecorded）
        _ink.StepRecorded += (_, _) => NoteStep(StepKind.Ink);

        // 丢笔：墨迹层刚记的那一步被抹掉了 ⇒ 时间线也要把末尾那一格摘掉，
        // 否则按钮可用态与真实可撤销步数对不上，下一次 Ctrl+Z 会被幽灵步吞掉一次。
        _ink.StepDiscarded += (_, _) => NoteDiscardedInkStep();

        // 换文档 / 装载批注会丢弃墨迹历史 ⇒ 时间线也必须一起丢，
        // 否则指针会指向已经不存在的步骤。挂在事件上而不是调用点上：漏掉一个调用点就是一个幽灵步。
        _ink.Resetted += (_, _) => Reset();

        _gfx.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>历史发生变化（新步 / 撤销 / 重做 / 重置）。</summary>
    public event EventHandler? Changed;

    /// <summary>可撤销的步数。</summary>
    public int UndoCount => _applied;

    /// <summary>可重做的步数。</summary>
    public int RedoCount => _timeline.Count - _applied;

    public bool CanUndo => _applied > 0;

    public bool CanRedo => _applied < _timeline.Count;

    /// <summary>
    /// 记一步图形操作。
    /// </summary>
    /// <remarks>
    /// 调用时机由宿主门面（<c>HostGfxObjectHost.BeginStep</c>）保证：
    /// <b>先</b>让 <see cref="GfxHistory.Capture"/> 存快照，<b>再</b>调这里记时间线。
    /// 两边步数因此始终一致。
    /// </remarks>
    public void NoteGfxStep() => NoteStep(StepKind.Gfx);

    /// <summary>撤销一步（不管是墨迹还是图形）。返回是否真的撤销了。</summary>
    public bool Undo()
    {
        if (_applied <= 0) return false;

        var kind = _timeline[_applied - 1];
        bool ok = kind == StepKind.Ink ? _ink.Undo() : _gfx.Undo();

        // 子历史已经空了（例如墨迹栈被容量上限淘汰掉了最老的一步）：
        // 不移动指针，也不假装成功 —— 指针与子栈一旦错位，后面每一步都会退错东西。
        if (!ok) return false;

        _applied--;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>重做一步。返回是否真的重做了。</summary>
    public bool Redo()
    {
        if (_applied >= _timeline.Count) return false;

        var kind = _timeline[_applied];
        bool ok = kind == StepKind.Ink ? _ink.Redo() : _gfx.Redo();

        if (!ok) return false;

        _applied++;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>丢弃全部历史。</summary>
    public void Reset()
    {
        _timeline.Clear();
        _applied = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NoteStep(StepKind kind)
    {
        // 撤销之后又做了新操作 ⇒ 原来的重做分支作废（标准语义）
        if (_applied < _timeline.Count)
        {
            _timeline.RemoveRange(_applied, _timeline.Count - _applied);
        }

        _timeline.Add(kind);
        _applied++;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 墨迹层抹掉了刚记的最后一格（丢笔）⇒ 时间线同步摘掉它。
    /// </summary>
    /// <remarks>
    /// 只在「末尾那一格确实是墨迹步、且它正好是当前指针位置」时动手；对不上宁可不动 ——
    /// 指针与子栈错位的代价远大于多留一格退不动的幽灵步。
    /// </remarks>
    private void NoteDiscardedInkStep()
    {
        if (_timeline.Count == 0 || _applied != _timeline.Count) return;
        if (_timeline[^1] != StepKind.Ink) return;

        _timeline.RemoveAt(_timeline.Count - 1);
        _applied--;

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
