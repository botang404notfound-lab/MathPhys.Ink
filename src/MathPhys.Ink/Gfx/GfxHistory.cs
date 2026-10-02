using System;
using System.Collections.Generic;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 图形对象的撤销 / 重做 —— <b>快照式</b>。
/// </summary>
/// <remarks>
/// <b>为什么不用"命令 + 反做"</b>：命令式撤销最典型的错误是"半还原"——
/// 位置回去了、旋转没回去；或者删掉的对象重建出来少了一个参数。
/// 这类 bug 不会崩，只会让人觉得"撤销有点问题"，极难定位。
/// <para>
/// 对象图很小（一节课几十个对象，一次快照十几 KB），所以直接存整份快照：
/// 撤销 = 还原上一份。代码量极小，而且<b>几乎不可能写错</b>。
/// M6 在墨迹层上已经为"精确反做"付过一遍代价（容量上限、笔画引用、
/// 擦除拆分成碎笔画…），对象层没必要再付一次。
/// </para>
/// <para>
/// 用法固定是"<b>改之前先 <see cref="Capture"/></b>"，一次连续操作（比如整段拖动）
/// 只调用一次 —— 否则按一下 Ctrl+Z 只会退回一个像素。
/// </para>
/// </remarks>
public sealed class GfxHistory
{
    /// <summary>撤销栈深度上限（步）。一节课的图形编辑量远小于此。</summary>
    public const int DefaultCapacity = 60;

    private readonly GfxObjectStore _store;
    private readonly int _capacity;

    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();

    public GfxHistory(GfxObjectStore store, int capacity = DefaultCapacity)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>历史发生变化（记录 / 撤销 / 重做 / 重置）。</summary>
    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    /// <summary>最近一次记录的操作名（诊断用）。</summary>
    public string? LastLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    /// <summary>
    /// 记下"操作前的状态"，让接下来的修改可以被撤销。
    /// </summary>
    /// <param name="label">操作名（写进日志，便于现场反推"刚才那步是什么"）。</param>
    public void Capture(string label = "")
    {
        _undo.Add(new Snapshot(_store.Snapshot(), label));

        int overflow = _undo.Count - _capacity;
        if (overflow > 0) _undo.RemoveRange(0, overflow);

        // 任何新操作都让重做分支作废（标准语义，与 InkHistory 一致）
        _redo.Clear();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>撤销一步。</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;

        var state = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        // 先把"现在"存进重做栈，再回到"过去" —— 顺序反了就没法重做了
        _redo.Add(new Snapshot(_store.Snapshot(), state.Label));

        _store.Restore(state.Objects);

        AppLog.Info($"图形撤销：{state.Label}（对象 {state.Objects.Count} 个）");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>重做一步。</summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;

        var state = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        _undo.Add(new Snapshot(_store.Snapshot(), state.Label));

        _store.Restore(state.Objects);

        AppLog.Info($"图形重做：{state.Label}（对象 {state.Objects.Count} 个）");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>丢弃全部历史（换文档时）。</summary>
    public void Reset()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;

        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>一份对象状态快照。</summary>
    private sealed record Snapshot(IReadOnlyList<GfxObjectData> Objects, string Label);
}
