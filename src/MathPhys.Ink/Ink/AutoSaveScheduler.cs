using System.Windows.Threading;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Ink;

/// <summary>
/// 批注的自动保存调度器：把「笔迹一直在变」这件事，收敛成「安静下来之后再写一次盘」。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须有它：写字的每一笔都会改动 <c>StrokeCollection</c>，若每次改动都落盘，
/// 一次板书会触发上百次文件写入；而若只在关闭时保存，程序崩了/断电就全丢。
/// 用"停止修改后延迟 N 秒"的防抖，两种毛病一起解决。
/// </para>
/// <para>
/// 计时用 <see cref="DispatcherTimer"/>（必须回到 UI 线程，因为读 <c>StrokeCollection</c> 不是线程安全的），
/// 但保存动作本身由调用方注入 —— 于是"什么时候该存"这套判断可以在验收 harness 里
/// 用 <see cref="Flush"/> 直接驱动，不必真的等 2 秒。
/// </para>
/// </remarks>
public sealed class AutoSaveScheduler : IDisposable
{
    /// <summary>默认防抖时长：停笔 2 秒后落盘。</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _timer;

    /// <summary>真正执行保存的动作，返回是否写了盘。</summary>
    private readonly Func<bool> _save;

    private bool _dirty;
    private bool _disposed;

    public AutoSaveScheduler(Dispatcher dispatcher, TimeSpan debounce, Func<bool> save)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _save = save ?? throw new ArgumentNullException(nameof(save));

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = debounce <= TimeSpan.Zero ? DefaultDebounce : debounce,
        };
        _timer.Tick += OnTick;
    }

    /// <summary>有尚未落盘的改动。</summary>
    public bool IsDirty => _dirty;

    /// <summary>
    /// 挂起自动保存。
    /// </summary>
    /// <remarks>
    /// 加载批注时必须挂起：加载过程本身会往集合里灌入笔迹，
    /// 若此时允许保存，就会"刚读出来立刻写回去"，还可能覆盖掉正在读的文件。
    /// </remarks>
    public bool IsSuspended { get; set; }

    /// <summary>成功写盘后触发，界面据此刷新"已保存"状态。</summary>
    public event EventHandler? Saved;

    /// <summary>标记有改动，并重新开始计时（每次改动都会把落盘时刻往后推）。</summary>
    public void MarkDirty()
    {
        if (_disposed || IsSuspended) return;

        _dirty = true;

        // 重置计时：DispatcherTimer 的 Start 会重新计算间隔，
        // 所以"边写边存"不会在书写过程中插进来打断。
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// 立即落盘（若确有改动）。
    /// </summary>
    /// <returns>是否真的写了盘。</returns>
    /// <remarks>
    /// 换文档前、关闭窗口前都要调用它 —— 这两个时刻是"最后一次机会"，
    /// 错过就真的丢了。它同时会停掉计时器，避免切完文档后旧任务把批注写到新文件上。
    /// </remarks>
    public bool Flush()
    {
        if (_disposed) return false;

        _timer.Stop();

        if (!_dirty || IsSuspended) return false;

        bool written;
        try
        {
            written = _save();
        }
        catch (Exception ex)
        {
            AppLog.Warn("自动保存失败：" + ex.Message);
            return false;
        }

        // 即使底层因为只读目录写失败，也清掉脏标记：否则计时器会每 2 秒重试一次，
        // 在一体机上表现为日志被刷满、程序持续做无用功。用户仍可用 Ctrl+S 或另存解决。
        _dirty = false;

        if (written) Saved?.Invoke(this, EventArgs.Empty);
        return written;
    }

    /// <summary>取消待执行的保存，但保留脏标记。</summary>
    public void Cancel() => _timer.Stop();

    /// <summary>
    /// 清除脏标记且不写盘。
    /// </summary>
    /// <remarks>
    /// 装载完批注后调用：装载过程会往集合里灌笔迹，若不处理就会被当成"用户改了东西"，
    /// 2 秒后再原样写回去一遍 —— 纯属浪费，而且在只读介质上还会白报一次失败。
    /// </remarks>
    public void MarkClean()
    {
        _timer.Stop();
        _dirty = false;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        Flush();
    }
}
