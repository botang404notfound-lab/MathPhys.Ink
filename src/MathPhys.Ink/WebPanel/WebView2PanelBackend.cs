using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.WebPanel;

/// <summary>
/// <see cref="IWebPanelBackend"/> 的真实实现：WebView2 + 本地离线资源（M7.5 起，M22 参数化）。
/// </summary>
/// <remarks>
/// <b>这是全工程唯一碰 WebView2 类型的文件。</b>把它的面积压到这么小是有意的：
/// WebView2 初始化需要真实 HWND 与消息循环，harness 跑不了它，
/// 所以凡是能挪进 <see cref="WebPanelController"/> 的逻辑都挪走了，
/// 留在这里的只有"创建窗口、导航、收发字节"。
/// <para>
/// M22 起它不再硬编码"我就是 GeoGebra"：虚拟域名、资源目录、入口页、文案、自测环境变量
/// 全部来自构造时传入的 <see cref="WebPanelProfile"/>。
/// </para>
/// <para>
/// 几个不能改错的点，都写在对应位置的注释里：
/// 虚拟域名映射（否则 WASM 白屏）、运行时缺失的降级（否则白板整体打不开）、
/// 关闭时是否释放（内存 vs 重开速度）。
/// </para>
/// </remarks>
public sealed class WebView2PanelBackend : IWebPanelBackend
{
    /// <summary>本后端服务的面板 profile（虚拟域名 / 资源目录 / 入口页 / 文案都取自它）。</summary>
    private readonly WebPanelProfile _profile;

    /// <summary>承载面板的宿主窗口（主窗口），用来做 Owner。</summary>
    private readonly Window _owner;

    /// <summary>
    /// 主窗口里的停靠区宿主（A1 形态时 WebView2 挂这里）。
    /// </summary>
    /// <remarks>
    /// ★ 停靠区必须与画布是<b>并列的布局单元</b>（主窗口 Grid 的两列），
    /// 不能叠在画布上 —— WebView2 是 HWND，永远渲染在 WPF 之上（空气空间问题）。
    /// </remarks>
    private readonly Border _dockHost;

    /// <summary>离线资源目录（<c>&lt;资源目录名&gt;/</c>，在 exe 旁边）。</summary>
    private readonly string _assetFolder;

    /// <summary>Fixed Version 运行时目录；<c>null</c> 表示用系统安装的 Evergreen 运行时。</summary>
    private readonly string? _fixedRuntimeFolder;

    /// <summary>当前形态（默认全屏；切换 / 重开都沿用最近一次的选择）。</summary>
    private PanelForm _form = PanelForm.FullScreen;

    private WebPanelWindow? _window;
    private WebView2? _view;
    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="profile">本后端服务的面板 profile。</param>
    /// <param name="owner">承载面板的宿主窗口（做 Owner，跟着它最小化 / 关闭）。</param>
    /// <param name="dockHost">主窗口里的停靠区（A1 形态用）。</param>
    /// <param name="fixedRuntimeFolder">随包 Fixed Version 运行时目录；<c>null</c> 用系统 Evergreen。</param>
    public WebView2PanelBackend(
        WebPanelProfile profile,
        Window owner,
        Border dockHost,
        string? fixedRuntimeFolder = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _dockHost = dockHost ?? throw new ArgumentNullException(nameof(dockHost));
        _fixedRuntimeFolder = fixedRuntimeFolder;

        // 资源目录按 profile 推导：exe 旁边的 <资源目录名>/。
        _assetFolder = Path.Combine(AppContext.BaseDirectory, profile.AssetFolderName);

        // 可用性在构造时就探明：按钮要靠它在"按下去之前"置灰。
        (IsAvailable, UnavailableReason) = Probe();
    }

    /// <summary>
    /// 按 exe 所在目录的约定创建某 profile 的后端
    /// （资源 <c>&lt;资源目录名&gt;/</c>、可选运行时 <c>webview2/</c>）。
    /// </summary>
    public static WebView2PanelBackend Create(Window owner, Border dockHost, WebPanelProfile profile)
    {
        var baseDirectory = AppContext.BaseDirectory;

        return new WebView2PanelBackend(
            profile,
            owner,
            dockHost,
            // 随包 Fixed Version 优先；自测可用环境变量再指定一个目录来验证这条链路。
            FindFixedRuntimeFolder(baseDirectory) ?? FixedRuntimeFromEnvironment(profile));
    }

    /// <inheritdoc />
    public bool IsAvailable { get; }

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    /// <inheritdoc />
    public bool IsLoaded => _view is not null
        && (_form == PanelForm.Docked || _window?.IsVisible == true);

    /// <inheritdoc />
    public PanelForm Form => _form;

    /// <inheritdoc />
    public event EventHandler<string>? RawMessageReceived;

    /// <inheritdoc />
    public event EventHandler? CloseRequested;

    /// <inheritdoc />
    public event EventHandler? FormToggleRequested;

    /// <inheritdoc />
    public event EventHandler<PanelForm>? FormChanged;

    /// <inheritdoc />
    public event EventHandler? ExportRequested;

    /// <summary>
    /// 探测"能不能用"：资源在不在、运行时在不在。
    /// </summary>
    /// <remarks>
    /// <b>探测失败绝不能抛异常到调用方</b>：一体机上缺运行时是常态（Win10 不一定预装），
    /// 那必须是"面板按钮置灰 + 一句中文说明"，而不是白板整体打不开。
    /// 这正是 M7.2 插件"失败隔离"原则的延续。
    /// </remarks>
    private (bool Available, string? Reason) Probe()
    {
        var name = _profile.DisplayName;

        // 自测钩子：强制走"无运行时"降级路径（本机装了 Evergreen，天然复现不了）。
        if (string.Equals(
                Environment.GetEnvironmentVariable(_profile.NoRuntimeEnvVar), "1", StringComparison.Ordinal))
        {
            AppLog.Warn($"{_profile.NoRuntimeEnvVar}=1：模拟无 WebView2 运行时的机器。");
            return (false, $"未检测到 WebView2 运行时，{name}面板不可用。"
                           + "白板其它功能不受影响；需要演示面板时请安装 Microsoft Edge WebView2 运行时，"
                           + "或使用随包自带的运行时版本。");
        }

        if (!Directory.Exists(_assetFolder))
        {
            return (false, $"{name}资源未部署（缺少 {_profile.AssetFolderName} 目录），请重新解压完整测试包。");
        }

        if (!File.Exists(Path.Combine(_assetFolder, _profile.EntryHtml)))
        {
            return (false, $"{name}资源不完整（缺少 {_profile.EntryHtml}），请重新解压完整测试包。");
        }

        try
        {
            var browserVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(
                string.IsNullOrEmpty(_fixedRuntimeFolder) ? null : _fixedRuntimeFolder);

            if (!string.IsNullOrEmpty(browserVersion))
            {
                AppLog.Info($"{name}面板可用：WebView2 运行时 {browserVersion}"
                            + (_fixedRuntimeFolder is null ? "（系统 Evergreen）" : "（随包 Fixed Version）"));
                return (true, null);
            }

            return (false, $"未检测到 WebView2 运行时，{name}面板不可用（白板其它功能不受影响）。");
        }
        catch (Exception ex)
        {
            // WebView2RuntimeNotFoundException 会走到这里。
            AppLog.Warn($"WebView2 运行时探测失败：{ex.GetType().Name} {ex.Message}");
            return (false, $"未检测到 WebView2 运行时，{name}面板不可用。"
                           + "白板其它功能不受影响；需要演示面板时请安装 Microsoft Edge WebView2 运行时，"
                           + "或使用随包自带的运行时版本。");
        }
    }

    /// <summary>在 exe 旁边找随包发布的 Fixed Version 运行时（<c>webview2/&lt;版本&gt;/msedgewebview2.exe</c>）。</summary>
    private static string? FindFixedRuntimeFolder(string baseDirectory)
    {
        var root = Path.Combine(baseDirectory, "webview2");
        if (!Directory.Exists(root)) return null;

        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(candidate, "msedgewebview2.exe")))
                {
                    return candidate;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"查找随包 WebView2 运行时失败：{ex.GetType().Name} {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 自测钩子：用环境变量 <c>{EnvPrefix}_FIXED_RUNTIME</c> 指定 Fixed Version 运行时目录。
    /// </summary>
    /// <remarks>
    /// 本机装了系统 Evergreen，没法天然复现"无运行时的一体机"；
    /// 但可以把任意一个 Fixed Version 目录（例如系统 Evergreen 的应用目录，
    /// 它与 Fixed Version 结构一致）指给后端，验证"随包运行时的发现 → CreateAsync → 真启动"
    /// 这条链路真的能跑 —— 终包里放的就是这条链路。
    /// 环境变量不存在或目录不合法时返回 <c>null</c>，行为与不设一致。
    /// </remarks>
    private static string? FixedRuntimeFromEnvironment(WebPanelProfile profile)
    {
        try
        {
            var folder = Environment.GetEnvironmentVariable(profile.FixedRuntimeEnvVar);

            if (string.IsNullOrWhiteSpace(folder)) return null;

            if (File.Exists(Path.Combine(folder, "msedgewebview2.exe")))
            {
                return folder;
            }

            AppLog.Warn($"{profile.FixedRuntimeEnvVar} 指定的目录不含 msedgewebview2.exe，忽略：{folder}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 {profile.FixedRuntimeEnvVar} 失败：{ex.GetType().Name} {ex.Message}");
        }

        return null;
    }

    /// <inheritdoc />
    public void SetForm(PanelForm form) => _form = form;

    /// <inheritdoc />
    public Task SwitchFormAsync(PanelForm form)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (form == _form) return Task.CompletedTask;   // 幂等

        if (_view is null)
        {
            // 还没打开过：只记形态，等 ShowAsync 按它创建（不需要搬控件）。
            _form = form;
            return Task.CompletedTask;
        }

        ReattachTo(form);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把已初始化的 WebView2 控件挂到指定形态的容器上（全屏窗 / 主窗口停靠区）。
    /// </summary>
    /// <remarks>
    /// ★ 是<b>同一个控件实例换父</b>，不是销毁重建：换父只搬 HWND 的挂载位置，
    /// CoreWebView2 会话与页面里的内容原样保留，切换在瞬息完成。
    /// （HwndHost 换父后会跟随新位置的布局矩形 —— 停靠区从收起被撑开的那次布局
    /// 正好给它一个非零尺寸。）
    /// </remarks>
    private void ReattachTo(PanelForm form)
    {
        if (_view is null) return;

        if (form == PanelForm.Docked)
        {
            _window?.Hide();                       // 全屏窗先收起来（控件要从它身上摘走）
            _window?.DetachView();                 // ★ 先断开：WPF 不允许一子两父，直接挂会抛异常
            _dockHost.Child = _view;               // 挂进主窗口停靠区
            AppLog.Info($"{_profile.DisplayName}面板已切换为停靠形态（A1）。");
        }
        else
        {
            _dockHost.Child = null;                // 从停靠区摘下
            EnsureWindow();
            _window!.AttachView(_view);            // AttachView 覆盖式赋值，幂等
            _window.Show();
            _window.Activate();
            AppLog.Info($"{_profile.DisplayName}面板已切换为全屏形态（A3）。");
        }

        _form = form;

        // ★ 页面本体一直在跑，但控制器的就绪闸门在"关掉再打开"时已复位 ——
        //   打个招呼让页面重发 ready，否则宿主之后发的命令会永远排队
        //   （表现成"关过一次面板之后命令全部失灵"，现场极难复现）。
        //   形态切换期间闸门本来就开着，重发一条 ready 是幂等的，无副作用。
        try
        {
            _view.CoreWebView2?.PostWebMessageAsJson(
                WebPanelProtocol.Encode(WebPanelProtocol.Hello, null));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{_profile.DisplayName}面板重发就绪请求失败：{ex.GetType().Name} {ex.Message}");
        }

        FormChanged?.Invoke(this, form);
    }

    /// <inheritdoc />
    public async Task ShowAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsAvailable)
        {
            throw new InvalidOperationException(UnavailableReason ?? $"{_profile.DisplayName}面板不可用。");
        }

        if (_view is not null)
        {
            // 复用已初始化的实例：课堂上"关一下再开"是高频动作，
            // 重新初始化 WebView2 并重新加载整个前端要好几秒，等不起。
            ReattachTo(_form);
            return;
        }

        EnsureWindow();

        var view = new WebView2();
        _view = view;

        // 按当前形态落位：停靠形态挂进主窗口（全屏窗保持隐藏），全屏形态挂进全屏窗。
        if (_form == PanelForm.Docked)
        {
            _dockHost.Child = view;
            AppLog.Info($"{_profile.DisplayName}面板以停靠形态（A1）首次创建。");
        }
        else
        {
            _window!.AttachView(view);

            // 先让窗口露出来，控件才有 HWND 环境可初始化。
            _window.Show();
            _window.Activate();
        }

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: string.IsNullOrEmpty(_fixedRuntimeFolder) ? null : _fixedRuntimeFolder,
            userDataFolder: ResolveUserDataFolder());

        await view.EnsureCoreWebView2Async(environment);

        var core = view.CoreWebView2;

        // ★ 必须走"虚拟域名 → 本地文件夹"映射，不能直接 file:// 打开。
        //   file:// 下 WASM 与 Web Worker 会被同源策略拦掉，
        //   现场表现是"一直转圈、不出图"，而且控制台之外的日志里什么都看不到。
        core.SetVirtualHostNameToFolderMapping(
            _profile.VirtualHost,
            _assetFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);

        // 讲台上不需要开发者工具与右键菜单：误触一次就要手动关掉，很烦。
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.WebMessageReceived += OnWebMessageReceived;
        view.NavigationCompleted += OnNavigationCompleted;

        view.Source = new Uri($"https://{_profile.VirtualHost}/{_profile.EntryHtml}{ResolvePanelQueryString()}");

        AppLog.Info($"{_profile.DisplayName}面板已创建，资源目录 {_assetFolder}，"
                    + $"虚拟域名 https://{_profile.VirtualHost}/");
    }

    /// <inheritdoc />
    public Task HideAsync()
    {
        // 只隐藏、不销毁：见 ShowAsync 里"复用实例"的说明。
        // 真正的释放放在 Dispose（退出程序、或将来做"空闲超时回收"时调用）。
        try
        {
            _window?.Hide();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"隐藏面板窗口失败：{ex.GetType().Name} {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Post(string json)
    {
        var core = _view?.CoreWebView2;
        if (core is null) return;

        core.PostWebMessageAsJson(json);
    }

    /// <inheritdoc />
    public async Task<string?> ExecuteScriptAsync(string script)
    {
        var core = _view?.CoreWebView2;
        if (core is null) return null;

        return await core.ExecuteScriptAsync(script);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ★ 用 <c>CapturePreviewAsync</c>（浏览器自己的合成结果）而不是 WPF 的
    /// <c>RenderTargetBitmap</c>：WebView2 是 HWND 子窗口，WPF 渲染树里<b>没有</b>它的内容，
    /// 用 WPF 那条路抓出来会是一块空白 —— 这正是"空气空间"在抓帧上的表现。
    /// </remarks>
    public async Task<byte[]?> CapturePngAsync()
    {
        var core = _view?.CoreWebView2;
        if (core is null) return null;

        try
        {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            return stream.Length > 0 ? stream.ToArray() : null;
        }
        catch (Exception ex)
        {
            // 抓帧会因为"页面还没渲染出第一帧"而失败，那是预期内的时机问题 ⇒ 记一条就够，不往上抛。
            AppLog.Warn($"{_profile.DisplayName}面板抓帧失败：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;

        _window = new WebPanelWindow(_profile.DisplayName, _profile.AllowDockForm);
        _window.BackRequested += OnWindowBackRequested;
        _window.ToggleFormRequested += OnWindowToggleFormRequested;
        _window.ExportRequested += OnWindowExportRequested;

        // Owner 指过去，面板才会跟着主窗口一起最小化 / 关闭；
        // 主窗口还没显示出来时不设（WPF 不允许 Owner 指向未显示的窗口）。
        if (_owner.IsVisible) _window.Owner = _owner;
    }

    /// <summary>
    /// WebView2 的 user data 目录必须在可写位置 —— exe 旁边的目录可能是只读的。
    /// </summary>
    /// <remarks>
    /// ★ 按 profile 分子目录：同一个 userDataFolder 只能被一个 CoreWebView2Environment 使用，
    /// 两个 profile 各建一个 Environment 时若指向同一目录会冲突。
    /// 分开之后两个面板可以同时存在，互不干扰。
    /// </remarks>
    private string ResolveUserDataFolder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MathPhys.Ink",
            "webview2",
            _profile.Id);

        Directory.CreateDirectory(folder);
        return folder;
    }

    private void OnWindowBackRequested(object? sender, EventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>全屏窗里的「停靠面板」按钮被点了：转给控制器统一编排。</summary>
    private void OnWindowToggleFormRequested(object? sender, EventArgs e)
        => FormToggleRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>顶栏「导出到白板」：只往上抛，抓帧与落画布都不在后端这一层。</summary>
    private void OnWindowExportRequested(object? sender, EventArgs e)
        => ExportRequested?.Invoke(this, EventArgs.Empty);

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? json;

        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取面板消息失败：{ex.GetType().Name} {ex.Message}");
            return;
        }

        RawMessageReceived?.Invoke(this, json ?? string.Empty);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            AppLog.Info($"{_profile.DisplayName}面板页面加载完成。");
            return;
        }

        // 导航失败必须说出来：否则现场看到的就是一个纯白面板，无从判断。
        AppLog.Warn($"{_profile.DisplayName}面板页面加载失败：{e.WebErrorStatus}");
        _window?.SetStatus($"页面加载失败：{e.WebErrorStatus}"
                           + $"（请检查 {_profile.AssetFolderName} 目录是否完整）");
    }

    /// <summary>
    /// 面板页查询串。默认空；发布冒烟时设 <c>{EnvPrefix}_SELFTEST=1</c> 会追加
    /// <c>?selftest=1</c>，让面板页自动跑到自检状态并回报
    /// （用来断言"断网可用"，见页面自己的 runSelfTest）。
    /// </summary>
    private string ResolvePanelQueryString()
    {
        try
        {
            var v = Environment.GetEnvironmentVariable(_profile.SelftestEnvVar);
            if (string.Equals(v, "1", StringComparison.Ordinal)) return "?selftest=1";
            if (string.Equals(v, "2", StringComparison.Ordinal)) return "?selftest=2";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取自测环境变量失败：{ex.GetType().Name} {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>释放：关窗 + <b>真正销毁</b> WebView2 进程（它常驻几百 MB）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_view is not null)
        {
            if (_view.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnWebMessageReceived;
            }

            _view.NavigationCompleted -= OnNavigationCompleted;
            _view.Dispose();
            _view = null;
        }

        if (_window is not null)
        {
            _window.BackRequested -= OnWindowBackRequested;
            _window.ToggleFormRequested -= OnWindowToggleFormRequested;
            _window.ExportRequested -= OnWindowExportRequested;
            _window.CloseForReal();
            _window = null;
        }

        AppLog.Info($"{_profile.DisplayName}面板已释放。");
    }
}
