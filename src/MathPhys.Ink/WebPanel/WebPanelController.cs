using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.WebPanel;

/// <summary>
/// Web 面板的<b>全部逻辑</b>：状态机 + 就绪闸门 + 互斥激活 + 消息收发
/// （M7.5 起，M22 由 <c>GeoGebraPanelController</c> 泛化）。
/// </summary>
/// <remarks>
/// 这一层刻意<b>不碰任何 WebView2 类型</b> —— 它只跟 <see cref="IWebPanelBackend"/>
/// （打开/关闭/发消息）与 <see cref="IPanelHostHooks"/>（抑制画布输入/写状态栏）说话。
/// 这么切的好处很实在：面板最容易出错的地方（打开失败后没还回画布输入、命令发早了丢失、
/// 退出后状态恢复错）全都变成可以在 harness 里断言的纯逻辑。
/// <para>
/// 它同时实现 <see cref="IWebPanelBridge"/> 与 <see cref="IGeoGebraBridge"/>：
/// 前者给新工具（物理仿真），后者让已分发的 GeoGebra 插件继续用
/// <see cref="IToolContext.GeoGebra"/> 而<b>一行都不用改</b>。
/// </para>
/// </remarks>
public sealed class WebPanelController : IWebPanelBridge, IGeoGebraBridge, IDisposable
{
    /// <summary>
    /// <c>changed</c> 消息的合并窗口（毫秒）。
    /// </summary>
    /// <remarks>
    /// 拖动一个点时消息会很密（每帧一条），不合并会把 UI 线程拖住 ——
    /// 而 <c>changed</c> 只是"画面变了"的可选通知，不是关键路径，丢中间几条无所谓。
    /// </remarks>
    public const int ChangedThrottleMilliseconds = 200;

    /// <summary>
    /// 就绪闸门的<b>超时</b>（毫秒，M23）：页面迟迟不报 ready 时的兜底。
    /// </summary>
    /// <remarks>
    /// ★ M23 之前这里没有超时：页面 ready 永远不来时（资源缺失 / 页面脚本半路死掉），
    /// 命令在闸门里<b>无限排队</b>，用户看到的是「点了没反应」，且日志里连一条错误都没有
    /// （error 消息只写状态栏，不升级为 loadFailed）。现在超时后按 loadFailed 处理 ——
    /// 停止等待、清空队列、把原因钉到状态栏与日志。
    /// 取 20 秒：完整版首启要解压/映射几百 MB 的虚拟目录，实测 GeoGebra 首开 ~8s，
    /// CircuitJS 首开更慢些；20s 内到不了 ready 的，基本就是真的起不来了。
    /// </remarks>
    public const int ReadyTimeoutMilliseconds = 20_000;

    private readonly WebPanelProfile _profile;
    private readonly IWebPanelBackend _backend;
    private readonly IPanelHostHooks _hooks;
    private readonly Func<long> _nowMilliseconds;
    private readonly List<string> _queued = new();

    /// <summary>
    /// <see cref="_queued"/> 的访问锁。命令可能从多个线程并发入队（插件 / 工具在后台线程
    /// 发命令），而「放行 / 载入失败 / 就绪超时 / 关闭」的 <c>Clear</c> 与 <c>ToArray</c> 又各自由
    /// 后台线程触发 —— <see cref="List{T}"/> 本身不是线程安全的，不加锁会偶发抛
    /// <c>IndexOutOfRangeException</c> / 损坏内部计数（现场是「点了没反应」且日志查不到）。
    /// </summary>
    private readonly object _queuedLock = new();

    /// <summary>当前面板形态（默认全屏 A3；切换后记住用户选择，下次打开沿用）。</summary>
    private PanelForm _currentForm = PanelForm.FullScreen;

    private bool _open;
    private bool _ready;

    /// <summary>
    /// 页面已明确报告「我起不来了」（<see cref="WebPanelProtocol.LoadFailed"/>）。
    /// </summary>
    /// <remarks>
    /// 它的唯一职责是<b>让宿主停止等待</b>：不再往队列里攒命令，也不再指望闸门会放行。
    /// 一个打不开的面板最多算「少了个功能」，一个打不开却让人以为点得到的面板才是事故。
    /// </remarks>
    private bool _loadFailed;

    /// <summary>
    /// 是否处于发布冒烟自测（环境变量 <c>{EnvPrefix}_SELFTEST=2</c>）。
    /// 此时面板一旦就绪，宿主会经真实 <see cref="SendAsync"/> → WebView2 → JS 链路发两条命令
    /// （一条 ready 前排队、一条 ready 后直发），并在 JS 侧触发一次错误回报，
    /// 把"宿主命令确实在真页面里生效 / 报错能上日志"变成可断言的日志证据。
    /// 正常面板恒为 <c>false</c>，不影响任何真实行为。
    /// </summary>
    private readonly bool _s2SelfTest;

    /// <summary>
    /// 是否注入了假时钟（构造参数 <c>nowMilliseconds</c> 非空）。只有 harness 会注入
    /// （为了确定性断言 <c>changed</c> 节流）；此时把「就绪超时」的到期设为 0ms，
    /// 让超时这条兜底路径不用真等 20 秒就能被跑到。生产环境恒为 <c>false</c>。
    /// </summary>
    private readonly bool _testClock;

    private bool _disposed;
    private long _lastChangedForwardedMs = long.MinValue;

    /// <summary>就绪超时的一发定时器（每次打开面板重设；ready / loadFailed / 关闭时拆掉）。</summary>
    /// <remarks>
    /// 用线程池 <see cref="Timer"/> 而不是 DispatcherTimer：控制器是纯逻辑层，
    /// 刻意不依赖 UI 线程（harness 无窗口实例化）；回调里只做「置状态 + 写日志/状态栏」，
    /// 与后端事件回调（本来就是后台线程进来的）同一档线程约束。
    /// </remarks>
    private Timer? _readyTimer;

    /// <summary>
    /// GeoGebra 兼容面的消息订阅者（<c>IGeoGebraBridge.MessageReceived</c>）。
    /// </summary>
    /// <remarks>
    /// 必须单独存一份：它与 <see cref="MessageReceived"/> <b>同名但泛型参数不同</b>
    /// （<c>GeoGebraMessage</c> vs <c>WebPanelMessage</c>），C# 不允许一个事件同时隐式实现两者。
    /// </remarks>
    private EventHandler<GeoGebraMessage>? _geoGebraMessageReceived;

    /// <summary>构造。</summary>
    /// <param name="profile">本面板的 profile（决定资源目录、虚拟域名与界面文案）。</param>
    /// <param name="backend">承载网页的后端（真实环境是 WebView2，harness 里是替身）。</param>
    /// <param name="hooks">宿主钩子（进入/退出面板模式、写状态栏）。</param>
    /// <param name="nowMilliseconds">
    /// 毫秒时钟；留空用 <see cref="Environment.TickCount64"/>。
    /// 注入它只为一件事：让"<c>changed</c> 节流"这条规则能被<b>确定性地</b>断言，
    /// 而不是靠 <c>Thread.Sleep</c> 撞运气。
    /// </param>
    public WebPanelController(
        WebPanelProfile profile,
        IWebPanelBackend backend,
        IPanelHostHooks hooks,
        Func<long>? nowMilliseconds = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _nowMilliseconds = nowMilliseconds ?? (() => Environment.TickCount64);
        _testClock = nowMilliseconds != null;

        _s2SelfTest = string.Equals(
            Environment.GetEnvironmentVariable(profile.SelftestEnvVar), "2", StringComparison.Ordinal);

        _backend.RawMessageReceived += OnRawMessage;
        _backend.CloseRequested += OnBackendCloseRequested;
        _backend.FormToggleRequested += OnBackendFormToggleRequested;
        _backend.FormChanged += OnBackendFormChanged;
        _backend.ExportRequested += OnBackendExportRequested;
    }

    /// <inheritdoc />
    public bool IsAvailable => _backend.IsAvailable;

    /// <inheritdoc />
    public string? UnavailableReason => _backend.UnavailableReason;

    /// <inheritdoc />
    public bool IsOpen => _open;

    /// <inheritdoc />
    public PanelForm CurrentForm => _currentForm;

    /// <inheritdoc />
    public bool IsReady => _ready;

    /// <summary>页面是否已报告「应用没能载入」（此时命令不会被排队，也不会被发送）。</summary>
    public bool IsLoadFailed => _loadFailed;

    /// <summary>就绪闸门里排队等发的命令条数（harness 断言用）。</summary>
    public int QueuedCommandCount
    {
        get { lock (_queuedLock) { return _queued.Count; } }
    }

    /// <summary>面板回话（JS → 宿主）。</summary>
    public event EventHandler<WebPanelMessage>? MessageReceived;

    /// <inheritdoc />
    public event EventHandler<PanelForm>? FormChanged;

    /// <summary>
    /// 面板已关闭（与 <see cref="FormChanged"/> 互补：关闭<b>不改变</b>形态，
    /// 但停靠区的显隐跟着"开没开"走 —— 从停靠形态退出时必须把那块地还给画布）。
    /// </summary>
    public event EventHandler? Closed;

    /// <inheritdoc />
    public event EventHandler? CaptureRequested;

    /// <summary>
    /// GeoGebra 兼容面：老插件订阅的消息通道（会把通用消息投影成 <see cref="GeoGebraMessage"/>）。
    /// </summary>
    event EventHandler<GeoGebraMessage>? IGeoGebraBridge.MessageReceived
    {
        add => _geoGebraMessageReceived += value;
        remove => _geoGebraMessageReceived -= value;
    }

    /// <inheritdoc />
    public async Task<bool> OpenAsync(string? args = null)
    {
        var name = _profile.DisplayName;

        if (!IsAvailable)
        {
            // 不抛异常：课堂上弹一个异常框，老师得先关掉它才能继续讲课 ——
            // 那是把"少个功能"升级成了"课上事故"。
            _hooks.SetStatus(UnavailableReason ?? $"{name}面板不可用。");
            return false;
        }

        if (_open) return true;   // 已经开着：幂等

        // ★ 进入"面板模式"：画布输入被抑制（墨迹层不接输入、视口手势关闭）。
        //   退出时是"把决定权还给当前工具"，而不是"设成能写字" ——
        //   所以这里不需要记状态快照，见 IPanelHostHooks 的说明。
        _hooks.EnterPanelMode();

        _open = true;
        _ready = false;
        _loadFailed = false;   // ★ 重新打开时把"上次没起来"的标记清掉，否则会误吞本期命令。
        lock (_queuedLock) { _queued.Clear(); }
        _lastChangedForwardedMs = long.MinValue;
        DisarmReadyTimer();

        // 让后端按"上次选择的形态"打开（首次打开时 WebView2 还没创建，
        // 后端只记状态；已开着时不会走到这里 —— 上面已幂等返回）。
        _backend.SetForm(_currentForm);

        // ★ 自测：在 ready 之前就发一条命令，验证"就绪闸门的排队不丢"在真机上也成立。
        //   这条命令此刻会被放进 _queued，等页面真正报 ready 时由 FlushQueue 原样发出去。
        if (_s2SelfTest)
        {
            // 只有 GeoGebra 侧的发布冒烟会走到这里（页面按 ?selftest=2 起自检）。
            _ = SendAsync("command", new { command = "M75A=(1,1)" });
        }

        try
        {
            await _backend.ShowAsync();
        }
        catch (Exception ex)
        {
            // ★ 打开失败必须回滚。否则画布输入被永久抑制 ——
            //   那比"打不开面板"严重得多：整个白板写不出字了。
            _open = false;
            _hooks.ExitPanelMode();
            AppLog.Warn($"{name}面板打开失败：{ex.GetType().Name} {ex.Message}");
            _hooks.SetStatus($"{name}面板打开失败：{ex.Message}");
            return false;
        }

        if (!string.IsNullOrEmpty(args))
        {
            // 消息类型与字段名按 profile 取：GeoGebra 页面认 loadMaterial / ggbBase64，
            // 通用仿真页面认 openArgs / args。控制器不认识具体应用。
            await SendAsync(
                _profile.ArgsMessageType,
                new Dictionary<string, string> { [_profile.ArgsPayloadKey] = args });
        }

        // ★ 就绪超时兜底（M23）：页面迟迟不报 ready 时，不能再让命令无限排队 ——
        //   那正是「点了没反应」在日志里查不到的那条路。
        ArmReadyTimer();

        AppLog.Info($"{name}面板已打开。");
        _hooks.SetStatus($"{name}面板已打开（按 Esc 或右上角「返回白板」退出）");
        return true;
    }

    /// <inheritdoc />
    public async Task CloseAsync()
    {
        if (!BeginClose()) return;   // 幂等

        await FinishCloseAsync();
    }

    // ---------------------------------------------------------------- GeoGebra 兼容面
    //
    // 这一节是"老插件一行都不用改"的全部代价：两个显式接口实现。
    // 不能用普通方法（public ShowAsync），那会与 OpenAsync 形成两个"打开面板"的入口，
    // 后来的人难免只改其中一个 —— 显式实现让语义只有一处、只能从老接口进来。

    /// <summary>GeoGebra 兼容面：老插件的 <c>ShowAsync</c> 就是新接口的 <c>OpenAsync</c>。</summary>
    Task<bool> IGeoGebraBridge.ShowAsync(string? materialBase64) => OpenAsync(materialBase64);

    /// <summary>GeoGebra 兼容面：老插件的 <c>HideAsync</c> 就是新接口的 <c>CloseAsync</c>。</summary>
    Task IGeoGebraBridge.HideAsync() => CloseAsync();

    /// <summary>
    /// 关闭的<b>同步</b>前半段：翻转状态 + 把画布输入交还给当前工具。
    /// </summary>
    /// <remarks>
    /// 刻意拆成"同步前半 + 异步后半"：后半段（等后端真的把窗口收掉）是异步的，
    /// 而宿主的状态必须在<b>用户按下返回的那一刻</b>就正确 ——
    /// 否则会出现"面板已经在关了，但画布还锁着"的窗口期。
    /// 顺带的好处：harness 能同步断言"按 Esc 之后输入交还了"。
    /// </remarks>
    /// <returns>是否真的执行了关闭（本来就关着时返回 <c>false</c>）。</returns>
    private bool BeginClose()
    {
        if (!_open) return false;

        _open = false;
        _ready = false;
        lock (_queuedLock) { _queued.Clear(); }
        _lastChangedForwardedMs = long.MinValue;
        DisarmReadyTimer();

        // 输入交还不依赖窗口是否收干净，先还回去。
        _hooks.ExitPanelMode();

        // 停靠区跟着"开没开"走：从停靠形态退出时，宿主要把那块地还给画布
        // （形态偏好本身保留，下次打开仍用停靠）。
        Closed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>关闭的<b>异步</b>后半段：让后端把窗口收掉。</summary>
    private async Task FinishCloseAsync()
    {
        try
        {
            await _backend.HideAsync();
        }
        catch (Exception ex)
        {
            // 关窗失败也要继续 —— 状态早已复位，卡住的代价比一个没关干净的窗口大得多。
            AppLog.Warn($"{_profile.DisplayName}面板关闭时出错：{ex.GetType().Name} {ex.Message}");
        }

        AppLog.Info($"{_profile.DisplayName}面板已关闭。");
        _hooks.SetStatus("已返回白板");
    }

    /// <summary>用户在面板里按了"返回白板" / Esc。</summary>
    private void OnBackendCloseRequested(object? sender, EventArgs e)
    {
        if (!BeginClose()) return;

        // 前半段（状态复位 + 输入交还）已经在上面同步做完了；
        // 后半段只是等后端收窗口，异常它自己吃掉，所以这里不需要 async void。
        _ = FinishCloseAsync();
    }

    /// <inheritdoc />
    public async Task ToggleFormAsync()
    {
        var target = _currentForm == PanelForm.Docked ? PanelForm.FullScreen : PanelForm.Docked;

        try
        {
            await _backend.SwitchFormAsync(target);
            _currentForm = target;   // 成功才记：切换失败时形态没变，不能骗自己。
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{_profile.DisplayName}面板切换形态失败：{ex.GetType().Name} {ex.Message}");
            _hooks.SetStatus($"{_profile.DisplayName}面板切换形态失败：{ex.Message}");
        }
    }

    /// <summary>用户在面板里点了「停靠 / 全屏」按钮。</summary>
    private void OnBackendFormToggleRequested(object? sender, EventArgs e) => _ = ToggleFormAsync();

    /// <summary>后端搬完控件（重挂 WebView2），把结果转给宿主与订阅方。</summary>
    private void OnBackendFormChanged(object? sender, PanelForm form)
    {
        _currentForm = form;
        FormChanged?.Invoke(this, form);
    }

    /// <inheritdoc />
    public Task SendAsync(string type, object? payload = null)
    {
        if (string.IsNullOrWhiteSpace(type) || !_open || _loadFailed)
        {
            // 面板没开就没有收件人，静默丢弃（不记日志：工具可能在 Deactivate 里发收尾消息）。
            // 已经收到 LoadFailed 时同样丢弃：加载已经失败，再攒命令只会让用户以为"点了没反应"。
            return Task.CompletedTask;
        }

        var json = WebPanelProtocol.Encode(type, payload);

        if (!_ready)
        {
            // ★ 就绪闸门：页面还没报 ready，命令排队等发。
            //   直接发会静默丢失（应用还没建起来），现场表现是"偶发点了没反应" ——
            //   取决于页面加载快慢，最难查的那一类。
            lock (_queuedLock) { _queued.Add(json); }
            return Task.CompletedTask;
        }

        Post(json);
        return Task.CompletedTask;
    }

    /// <summary>面板顶栏点了「导出到白板」（M22 S4b）。</summary>
    private void OnBackendExportRequested(object? sender, EventArgs e) => RaiseCaptureRequested();

    /// <summary>
    /// 把"面板请求导出"这件事抛给宿主。
    /// </summary>
    /// <remarks>
    /// 两条来源（顶栏按钮、页面内消息）收口到这一个出口：宿主只订阅一次，
    /// 不会出现"按钮能导、页面按钮导两次"这种只有真机点得出来的怪事。
    /// </remarks>
    private void RaiseCaptureRequested()
    {
        AppLog.Info($"{_profile.DisplayName}面板请求导出画面到卷面。");
        CaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task<byte[]?> CapturePngAsync()
    {
        // 没开着就没有画面可抓：此时返回 null 而不是让后端去碰空的 WebView2。
        if (!_open || _loadFailed) return null;

        var png = await _backend.CapturePngAsync();

        if (png is null) AppLog.Warn($"{_profile.DisplayName}面板抓帧没有拿到画面。");

        return png;
    }

    /// <summary>发给页面（已在闸门之外，可直接发）。</summary>
    private void Post(string json)
    {
        try
        {
            _backend.Post(json);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{_profile.DisplayName}消息发送失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private void OnRawMessage(object? sender, string json)
    {
        if (!WebPanelProtocol.TryDecode(json, out var message, out var error))
        {
            // ★ 解析失败要说出来。静默丢弃的表现正是这个项目最怕的"点了没反应"。
            AppLog.Warn($"{_profile.DisplayName}面板消息无法解析：{error}（原文：{Truncate(json, 200)}）");
            _hooks.SetStatus($"{_profile.DisplayName}面板消息无法解析：{error}");
            return;
        }

        if (string.Equals(message.Type, WebPanelProtocol.Ready, StringComparison.Ordinal))
        {
            _ready = true;
            DisarmReadyTimer();

            // ★★ 这一行是"JS → 宿主"这半边通路的<b>唯一证据</b>，别删。
            //   没有它，日志全绿只说明"页面加载完成了"，页面白屏 / bridge.js 没执行
            //   照样全绿。把 ready 自带的地址与标题一起写出来，
            //   还能顺手证明虚拟域名映射真的生效了。
            AppLog.Info($"{_profile.DisplayName}面板页面已就绪（JS → 宿主通道打通）：{DescribeReady(message)}");

            FlushQueue();
        }
        else if (string.Equals(message.Type, WebPanelProtocol.Error, StringComparison.Ordinal))
        {
            // JS 侧异常必须走到这里 —— 契约要求"不许静默失败"（docs/12 §6.2）。
            var detail = message.ReadPayloadString("message") ?? "（无详情）";
            AppLog.Warn($"{_profile.DisplayName}面板报错：{detail}");
            _hooks.SetStatus($"{_profile.DisplayName}面板报错：{detail}");
        }
        else if (string.Equals(message.Type, WebPanelProtocol.LoadFailed, StringComparison.Ordinal))
        {
            // ★ 应用起不来了：倒掉待发队列 + 把原因钉在状态栏上。
            //   没有这段，加载失败时命令会一直排队等一个永远不来的 ready ——
            //   用户看到的就是"点了没反应"，且日志里连一条错误都没有。
            _loadFailed = true;
            lock (_queuedLock) { _queued.Clear(); }
            DisarmReadyTimer();

            var reason = message.ReadPayloadString("reason") ?? $"{_profile.DisplayName}未能载入。";
            var detail = message.ReadPayloadString("detail");
            var detailPart = string.IsNullOrEmpty(detail) ? string.Empty : $"（{detail}）";
            AppLog.Warn($"{_profile.DisplayName}面板载入失败：{reason}{detailPart}");
            _hooks.SetStatus($"{_profile.DisplayName}载入失败：{reason}");
        }
        else if (string.Equals(message.Type, WebPanelProtocol.SelfTest, StringComparison.Ordinal))
        {
            // 仅发布冒烟用：面板在 ?selftest=1 下跑到自检状态，把结果回传。
            var objects = message.ReadPayloadString("objects") ?? "?";
            var names = message.ReadPayloadString("names") ?? "";
            AppLog.Info($"{_profile.DisplayName}面板自检：{objects}（{names}）");
        }
        else if (string.Equals(message.Type, WebPanelProtocol.Diagnostic, StringComparison.Ordinal))
        {
            // 仅发布冒烟（?selftest=1）时回传的面板内部日志，方便排查"加载器没回调"之类的问题。
            var text = message.ReadPayloadString("text");
            if (!string.IsNullOrEmpty(text)) AppLog.Info($"{_profile.DisplayName}面板诊断：{text}");
        }
        else if (string.Equals(message.Type, WebPanelProtocol.RequestExportPng, StringComparison.Ordinal))
        {
            // 页面里自己也会喊一次（页面内有导出按钮时）—— 与顶栏按钮走同一条出口，
            // 于是"面板请求导出"这件事在宿主看来只有一个来源、一种处理。
            RaiseCaptureRequested();
        }
        else if (string.Equals(message.Type, WebPanelProtocol.Changed, StringComparison.Ordinal))
        {
            if (!ShouldForwardChanged())
            {
                return;   // 被合并窗口吃掉
            }
        }

        RaiseMessageReceived(message);
    }

    /// <summary>把 <c>ready</c> 的 payload 摘成一行便于看日志与验收的摘要。</summary>
    private static string DescribeReady(WebPanelMessage message)
    {
        var parts = new List<string>();
        var title = message.ReadPayloadString("title");
        var href = message.ReadPayloadString("href");
        var version = message.ReadPayloadString("version");

        if (!string.IsNullOrEmpty(title)) parts.Add($"标题 {title}");
        if (!string.IsNullOrEmpty(href)) parts.Add($"地址 {href}");
        if (!string.IsNullOrEmpty(version)) parts.Add($"版本 {version}");

        return parts.Count > 0 ? string.Join("；", parts) : "（页面未附带信息）";
    }

    /// <summary><c>changed</c> 的前向节流：合并窗口内的后续消息直接丢掉。</summary>
    private bool ShouldForwardChanged()
    {
        var now = _nowMilliseconds();

        if (_lastChangedForwardedMs != long.MinValue
            && now - _lastChangedForwardedMs < ChangedThrottleMilliseconds)
        {
            return false;
        }

        _lastChangedForwardedMs = now;
        return true;
    }

    // ---------------------------------------------------------------- 就绪超时（M23）

    /// <summary>布防就绪超时：一次发（到点只触发一次），重开面板时重布。</summary>
    private void ArmReadyTimer()
    {
        DisarmReadyTimer();
        // 生产用真实 20 秒兜底；harness 注入了假时钟（_testClock）时设为 0ms 立即到期，
        // 这样「就绪超时」这条路径在测试里不用真等 20 秒就能确定性地跑到（可并发断言）。
        int dueMs = _testClock ? 0 : ReadyTimeoutMilliseconds;
        _readyTimer = new Timer(OnReadyTimeout, null, dueMs, Timeout.Infinite);
    }

    /// <summary>拆掉就绪超时（幂等）。</summary>
    private void DisarmReadyTimer()
    {
        _readyTimer?.Dispose();
        _readyTimer = null;
    }

    /// <summary>超时回调：页面没报 ready 也没报 loadFailed ⇒ 按 loadFailed 处理。</summary>
    /// <remarks>
    /// 回调在线程池线程上跑，所以先看 <c>_open</c>（可能已经关了），
    /// 再看 <c>_ready/_loadFailed</c>（可能刚好在超时前后就绪/失败）——
    /// 三者任何一个成立都不动手，宁可漏报也不误报。
    /// </remarks>
    private void OnReadyTimeout(object? state)
    {
        if (!_open || _ready || _loadFailed) return;

        _loadFailed = true;
        lock (_queuedLock) { _queued.Clear(); }
        DisarmReadyTimer();

        AppLog.Warn($"{_profile.DisplayName}面板就绪超时：{ReadyTimeoutMilliseconds / 1000} 秒内页面没有报 ready，"
            + "已按载入失败处理（不再排队命令）。");
        _hooks.SetStatus($"{_profile.DisplayName}载入失败：页面迟迟没有就绪"
            + $"（超过 {ReadyTimeoutMilliseconds / 1000} 秒）。可关掉重试一次；反复出现请检查包是否完整。");
    }

    /// <summary>就绪闸门放行：把排队命令按序发出去。</summary>
    private void FlushQueue()
    {
        string[] pending;
        lock (_queuedLock)
        {
            if (_queued.Count == 0) return;

            pending = _queued.ToArray();
            _queued.Clear();
        }

        AppLog.Info($"{_profile.DisplayName}页面就绪，放行排队命令 {pending.Length} 条。");

        foreach (var json in pending)
        {
            Post(json);
        }
    }

    private void RaiseMessageReceived(WebPanelMessage message)
    {
        var handler = MessageReceived;
        if (handler is not null)
        {
            try
            {
                handler(this, message);
            }
            catch (Exception ex)
            {
                // 订阅方（插件）是别人写的代码，它出错只该损失它自己。
                AppLog.Warn($"{_profile.DisplayName}消息订阅者抛异常：{ex.GetType().Name} {ex.Message}");
            }
        }

        var geoHandler = _geoGebraMessageReceived;
        if (geoHandler is null) return;

        try
        {
            // 同一份消息，按 GeoGebra 的老信封投影一次 —— 老插件看见的东西一字未变。
            geoHandler(this, new GeoGebraMessage(message.Type, message.PayloadJson));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"GeoGebra 消息订阅者抛异常：{ex.GetType().Name} {ex.Message}");
        }
    }

    private static string Truncate(string? text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? string.Empty : text[..max] + "…";

    /// <summary>释放：退订并释放后端（WebView2 常驻内存几百 MB，必须真释放）。</summary>
    public void Dispose()
    {
        // 幂等：宿主的 Shutdown 与调用方的 using 可能各释放一次，
        // 后端那层的 Dispose 有保护，但这里退订两次也没意义，索性先挡住。
        if (_disposed) return;
        _disposed = true;

        DisarmReadyTimer();

        _backend.RawMessageReceived -= OnRawMessage;
        _backend.CloseRequested -= OnBackendCloseRequested;
        _backend.FormToggleRequested -= OnBackendFormToggleRequested;
        _backend.FormChanged -= OnBackendFormChanged;
        _backend.ExportRequested -= OnBackendExportRequested;
        _backend.Dispose();
    }
}
