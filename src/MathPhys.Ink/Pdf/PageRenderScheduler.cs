using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Pdf;

/// <summary>某一页某个档位渲染完成（回调在 UI 线程执行）。</summary>
public sealed class PageRenderedEventArgs : EventArgs
{
    public PageRenderedEventArgs(int pageIndex, int levelIndex, BitmapSource bitmap)
    {
        PageIndex = pageIndex;
        LevelIndex = levelIndex;
        Bitmap = bitmap;
    }

    public int PageIndex { get; }

    public int LevelIndex { get; }

    public BitmapSource Bitmap { get; }
}

/// <summary>
/// 后台渲染调度器：用一个专用线程串行渲染 PDF 页，结果进 LRU 缓存并回推到 UI 线程。
/// </summary>
/// <remarks>
/// 为什么是「单线程串行」而不是线程池：
/// <c>PdfDocument</c> 非线程安全（见 <see cref="IPdfDocumentService"/> 约定），并发渲染会崩或出脏图。
/// 试卷十几页、只渲视口内页，单线程完全够 —— 真正的瓶颈是"别在 UI 线程渲"，
/// 而不是"并行渲"（并行渲带来的收益远小于它的风险）。
/// <para>
/// 三个关键机制：
/// <list type="number">
/// <item><b>需求队列整体替换</b>：一次缩放手势会在几十毫秒内产生几十个中间档位请求，
///       若用追加队列，就得渲完这几十个过期的才轮到当前需要的。整体替换让它们直接作废。</item>
/// <item><b>generation 失效</b>：换文档时递增。渲染线程在渲<b>之前</b>和渲<b>之后</b>各校验一次，
///       保证作废请求既不发起渲染、渲完的结果也不进缓存。</item>
/// <item><b>去重</b>：已在缓存或已在队列里的 (页, 档位) 不重复排。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class PageRenderScheduler : IDisposable
{
    /// <summary>一个渲染需求：第几页、哪个档位。</summary>
    public readonly record struct Request(int PageIndex, int LevelIndex);

    private readonly object _gate = new();
    private readonly Queue<Request> _queue = new();
    private readonly HashSet<Request> _queued = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dispatcher _dispatcher;
    private readonly Thread _worker;

    private IPdfDocumentService? _document;
    private int _generation;
    private int _renderCount;
    private volatile bool _disposed;

    /// <summary>
    /// 创建调度器并立即启动渲染线程。
    /// </summary>
    /// <param name="dispatcher">UI 线程的 Dispatcher，用于把渲染结果回推。</param>
    /// <param name="cache">可选的缓存实例（便于测试注入）。</param>
    public PageRenderScheduler(Dispatcher dispatcher, PageRenderCache? cache = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Cache = cache ?? new PageRenderCache();

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "MathPhys.PageRenderer",
            // 后台渲染是"用户正在等的活"，但绝不能跟 UI 抢 CPU：
            // 一体机上 UI 掉帧的体感损失，远大于渲染慢 20% 的损失。
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    /// <summary>位图缓存（UI 线程可直接查，用于即时显示）。</summary>
    public PageRenderCache Cache { get; }

    /// <summary>一页渲染完成。回调已切回 UI 线程，且当前 generation 仍未失效。</summary>
    public event EventHandler<PageRenderedEventArgs>? PageRendered;

    /// <summary>真实发起过的渲染次数（含被 generation 丢弃的结果）。仅供诊断与回归测试使用。</summary>
    public int RenderCount => Volatile.Read(ref _renderCount);

    /// <summary>当前待渲队列长度。</summary>
    public int PendingCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    /// <summary>绑定（或解绑）文档。会作废所有在途请求并清空缓存。</summary>
    public void SetDocument(IPdfDocumentService? document)
    {
        lock (_gate)
        {
            _document = document;
            _queue.Clear();
            _queued.Clear();
        }

        // 先清队列再换代：这样"清空"这个动作本身也在新 generation 下生效。
        // 在途的那一页会被渲染后的校验拦下来，不会进缓存。
        Interlocked.Increment(ref _generation);
        Cache.Clear();
        _wake.Set();
    }

    /// <summary>
    /// 用新的需求列表<b>整体替换</b>待渲队列。
    /// <paramref name="demand"/> 需按需要程度<b>从高到低</b>排列（调用方负责排序），渲染线程按序取。
    /// </summary>
    public void UpdateDemand(IReadOnlyList<Request> demand)
    {
        ArgumentNullException.ThrowIfNull(demand);

        lock (_gate)
        {
            _queue.Clear();
            _queued.Clear();

            foreach (var request in demand)
            {
                if (Cache.Contains(new PageRenderCache.Key(request.PageIndex, request.LevelIndex))) continue;
                if (!_queued.Add(request)) continue;
                _queue.Enqueue(request);
            }
        }

        if (demand.Count > 0) _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _wake.Set();
        if (_worker.IsAlive)
        {
            // 不无限等：线程是后台线程，即使还在渲一页也不会阻止进程退出
            _worker.Join(TimeSpan.FromSeconds(2));
        }
        _wake.Dispose();
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            Request request;
            IPdfDocumentService? document;
            int generation;

            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    request = default;
                    document = null;
                    generation = -1;
                }
                else
                {
                    request = _queue.Dequeue();
                    _queued.Remove(request);
                    document = _document;
                    generation = _generation;
                }
            }

            if (generation < 0)
            {
                // 没活干就睡。超时兜底是为了防止极端情况下唤醒信号丢失导致线程永久挂起。
                _wake.WaitOne(500);
                continue;
            }

            // 出队到真正渲染之间可能已经换文档了，这里先拦一道：
            // 拦住就"连渲都不渲"，不浪费 CPU。
            if (generation != Volatile.Read(ref _generation)) continue;
            if (document is null || !document.IsOpen) continue;
            if (Cache.Contains(new PageRenderCache.Key(request.PageIndex, request.LevelIndex))) continue;

            // 计数放在真正调用渲染之前，这样才能断言"作废的请求一次都没发起渲染"
            Interlocked.Increment(ref _renderCount);

            try
            {
                var bitmap = document.RenderPage(request.PageIndex, RenderLevels.ToZoom(request.LevelIndex));

                // 渲染耗时可达几百毫秒，期间可能已经换文档 ⇒ 结果作废（不进缓存、不回推）
                if (generation != Volatile.Read(ref _generation)) continue;

                Cache.Add(new PageRenderCache.Key(request.PageIndex, request.LevelIndex), bitmap);
                PostRendered(request, bitmap, generation);
            }
            catch (Exception ex)
            {
                // 单页失败绝不能让渲染线程死掉，否则之后所有页都渲不出来。
                AppLog.Error($"渲染第 {request.PageIndex + 1} 页（档位 {request.LevelIndex}）失败", ex);
            }
        }
    }

    private void PostRendered(Request request, BitmapSource bitmap, int generation)
    {
        if (_disposed) return;

        try
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;

            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_disposed) return;
                if (generation != Volatile.Read(ref _generation)) return;

                PageRendered?.Invoke(this, new PageRenderedEventArgs(request.PageIndex, request.LevelIndex, bitmap));
            }));
        }
        catch (Exception ex)
        {
            // Dispatcher 已关闭等情况：结果只是没送出去，缓存里已经有了，不影响正确性
            AppLog.Warn("回推渲染结果失败（窗口可能正在关闭）", ex);
        }
    }
}
