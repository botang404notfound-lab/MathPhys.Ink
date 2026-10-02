using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MathPhys.Ink.WebPanel;
using MathPhys.Ink.Gfx;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Ink;
using MathPhys.Ink.Input;
using MathPhys.Ink.Pdf;
using MathPhys.Ink.Plugins;
using MathPhys.Ink.Tools;
using MathPhys.Ink.Tools.BuiltIn;
using MathPhys.Ink.Viewport;
using MathPhys.Ink.Design;

namespace MathPhys.Ink.Views.Controls;

/// <summary>
/// 无限画布宿主：图层栈 + 视口手势 + 后台渲染调度 + 笔迹层。
/// </summary>
/// <remarks>
/// 分工很清楚，本类只做<b>编排</b>：
/// 坐标数学在 <see cref="CanvasViewport"/>，输入翻译在 <see cref="ViewportGestureRouter"/>，
/// 渲染在 <see cref="PageRenderScheduler"/>，笔/指分工在 <see cref="InkInputPolicy"/>。
/// 本类负责把它们接起来，以及管理页视觉与墨迹层的生命周期。
/// </remarks>
public partial class CanvasViewportHost : UserControl, IPanelHostHooks
{
    /// <summary>按宽度适配时两侧留白（DIP）。</summary>
    private const double SidePadding = 24.0;

    /// <summary>视口外的预渲染余量（DIP），避免滚动时一眼看到空白。</summary>
    private const double PrerenderMargin = 256.0;

    private readonly CanvasViewport _viewport = new();
    private readonly WorldLayout _layout = new();
    private readonly List<Image> _pageImages = new();

    /// <summary>笔的属性（颜色 + 世界单位笔宽）。</summary>
    private readonly PenProfile _pen = new();

    // ---------------------------------------------------------------- M11 书写手感
    // 压感与笔锋是两个独立的开关，但 WPF 只有一个 DrawingAttributes.IgnorePressure。
    // 于是它俩的合成关系必须写在**一处**（见 SyncInkPressure），否则就会出现
    // "关掉笔锋却把压感一起关了"这种要读三处代码才知道答案的耦合。

    /// <summary>设备原始压力是否参与（界面的「压感」复选框）。</summary>
    private bool _pressureEnabled;

    /// <summary>笔锋是否开启（速度 → 伪造压力因子）。出厂默认<b>开</b>：有笔锋才像真笔。</summary>
    private bool _craftEnabled = true;

    /// <summary>笔锋强度档位（出厂「中」）。</summary>
    private CraftStrength _craftStrength = CraftStrength.Medium;

    /// <summary>橡皮的属性（世界单位半径 + 擦除方式）。</summary>
    private readonly EraserProfile _eraser = new();

    /// <summary>
    /// 笔 / 触摸输入链路的现场记录仪。
    /// </summary>
    /// <remarks>
    /// 一体机上出的事，本机一件都复现不出来（没有触摸屏、没有笔、没有红外屏），
    /// 事后唯一的判据就是它写下的几行日志。
    /// </remarks>
    private readonly InputDiagnostics _diag = new();

    /// <summary>
    /// 撤销 / 重做引擎。构造函数里接管墨迹层的笔画集合 ——
    /// 之所以不在字段初始化器里建，是因为它依赖 <c>InitializeComponent()</c> 之后的 <c>InkLayer</c>。
    /// </summary>
    private readonly InkHistory _history;

    /// <summary>每页当前实际显示的档位；-1 表示没显示任何位图。用来判断新渲好的一张是否更清晰。</summary>
    private readonly List<int> _displayedLevels = new();

    /// <summary>当前按住的触点 Id。用于屏蔽被 WPF 提升（promote）成鼠标事件的触摸操作。</summary>
    private readonly HashSet<int> _activeTouches = new();

    private readonly ViewportGestureRouter _gestures;

    /// <summary>
    /// 工具注册表 —— 全程序唯一的"有哪些工具、现在用哪个"的真相源（M7.1）。
    /// </summary>
    private readonly ToolRegistry _tools = new();

    /// <summary>图形对象的渲染器注册表（M7.4）：Kind → 画法，由插件在启动时登记。</summary>
    private readonly GfxRendererCatalog _gfxCatalog = new();

    /// <summary>图形对象集合 —— 画布上所有学科工具图形的真相源。</summary>
    private readonly GfxObjectStore _gfxStore;

    /// <summary>图形对象的撤销（快照式）。</summary>
    private readonly GfxHistory _gfxHistory;

    /// <summary>墨迹 + 图形两条子历史的统一时间线（Ctrl+Z 的唯一入口）。</summary>
    private readonly BoardHistory _boardHistory;

    /// <summary>递给工具用的图形对象门面。</summary>
    private readonly HostGfxObjectHost _gfxHost;

    /// <summary>图形对象层：把集合装配成画布上的矢量视觉。</summary>
    private readonly GfxObjectLayer _gfxObjects;

    /// <summary>
    /// 仿真截图的位图仓库（M22 S4b）。
    /// </summary>
    /// <remarks>
    /// 装在宿主而不是渲染器里：它是<b>文档级</b>的状态（随 .twb 存取、换文档清空），
    /// 而渲染器是可以被换掉的画法。渲染器只拿它的引用来查字节。
    /// </remarks>
    private readonly WebPanelImageStore _webPanelImages = new();

    /// <summary>
    /// 参数面板的协调器（M7.4 Step 5 批次 3）：把"选中的对象"翻译成面板内容、把修改写回对象。
    /// </summary>
    private readonly GfxParameterCoordinator _gfxParameters;

    /// <summary>
    /// 参数面板视图（浮在视口右上角）。
    /// </summary>
    /// <remarks>
    /// 挂在 <c>RootGrid</c> 上而不是世界层里：它是<b>屏幕坐标</b>的界面元素，
    /// 不该跟着画布缩放平移走（老师把卷面缩到 30% 时，面板还得是这么大、点得中）。
    /// </remarks>
    private readonly GfxParameterPanelView _gfxPanelView;

    /// <summary>
    /// 内置的选择工具（选中 / 拖动 / 旋转 / 缩放 / 删除）。
    /// </summary>
    /// <remarks>
    /// 留字段是为了两件事：切工具后让它重建常驻预览（选中框），以及给 <c>Del</c> 键一个入口。
    /// </remarks>
    private readonly GfxSelectionTool _selectionTool;

    /// <summary>递给工具用的宿主能力门面。</summary>
    private readonly IToolContext _toolContext;

    /// <summary>
    /// 瞬态工具被激活前，用户用的是哪个工具（见 <see cref="ITransientTool"/>）。
    /// </summary>
    /// <remarks>
    /// 存 Id 而不是引用：工具可能在这期间被插件重新注册，Id 才是稳定身份。
    /// </remarks>
    private string? _toolBeforeTransient;

    /// <summary>
    /// 可取消工具（<see cref="ICancellableTool"/>）被激活前，用户用的是哪个工具（M19）。
    /// </summary>
    /// <remarks>
    /// Esc 时若工具自己无可取消状态，宿主用它把控制权交回"上一个工具"（没有则回笔）。
    /// 与 <see cref="_toolBeforeTransient"/> 同样存 Id：Id 才是插件重新注册后的稳定身份。
    /// </remarks>
    private string? _toolBeforeCancellable;

    /// <summary>
    /// 是否处于"演示面板模式"（M7.5）：画布输入被抑制。
    /// </summary>
    /// <remarks>
    /// 做成独立开关、而不是直接去改 <c>InkLayer.IsHitTestVisible</c>，是因为后者会被
    /// <see cref="ApplyTool"/> 覆盖 —— 面板开着时工具一旦被激活（快捷键扫过也算），
    /// 抑制就悄悄消失了。面板模式必须<b>压在工具之上</b>。
    /// </remarks>
    private bool _panelMode;

    // ---------------------------------------------------------------- M12：图层 / 回放 / 题号

    /// <summary>当前落墨归入的图层（批注层为默认 —— 旧文件、旧笔画没有层标记，读出来就是它）。</summary>
    private string _activeInkLayerId = InkLayers.AnnotationId;

    /// <summary>被隐藏图层的笔画旁路集合（不渲染、不参与条数统计，显示时移回主集合）。</summary>
    private readonly StrokeCollection _hiddenInkStrokes = new();

    /// <summary>当前处于隐藏状态的图层 Id 集合。</summary>
    private readonly HashSet<string> _hiddenInkLayerIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 抑制"画布内容变化"上报（<see cref="InkChanged"/>）。
    /// </summary>
    /// <remarks>
    /// 图层显隐、回放重演都是<b>程序自己的动作</b>，不该被当成"老师改了东西"
    /// （否则藏一下草稿层就亮"未保存"，回放一整页触发上百次置脏）。
    /// 与面板模式同理：必须压在工具之上、由一处统一守门。
    /// </remarks>
    private bool _contentEventsSuspended;

    /// <summary>笔迹回放的记录器（构造起就挂着；真正记不记由 Arm 状态与抑制开关共同决定）。</summary>
    private readonly ReplayRecorder _replayRecorder = new();

    /// <summary>进行中的回放会话；<c>null</c> = 不在回放。</summary>
    private ReplaySession? _replay;

    /// <summary>回放期间是否自动播放。</summary>
    private bool _replayAutoPlay;

    /// <summary>
    /// 是否处于回放会话中（画布输入被抑制 —— 与面板模式同一套路，压在工具之上）。
    /// </summary>
    private bool _replayMode;

    /// <summary>自动播放的节拍器（懒建；只在自动播放时走）。</summary>
    private DispatcherTimer? _replayTimer;

    /// <summary>图层面板视图（M12）。挂在 <c>RootGrid</c> 上的屏幕坐标元素。</summary>
    private readonly LayerPanelView _layerPanelView;

    /// <summary>计算器面板（M13 S5）。</summary>
    private readonly CalculatorPanelView _calculatorView;

    /// <summary>回放控制条视图（M12）。</summary>
    private readonly ReplayPanelView _replayPanelView;

    /// <summary>演示面板桥接（M7.5）。由 <c>MainWindow</c> 注入；未注入时为 <c>null</c>。</summary>
    private IGeoGebraBridge? _geoGebra;

    /// <summary>
    /// 画布查询通道（吸附基准）。
    /// </summary>
    /// <remarks>
    /// 只订阅笔画集合、不持有任何控件状态 —— 换文档时 <c>InkLayer.Strokes.Clear()</c>
    /// 会让它自己标脏，下一次查询就是空的索引。所以<b>不需要</b>在 <see cref="SetDocument"/> 里手清，
    /// 少一条"忘了清"的路径。
    /// </remarks>
    private readonly BoardQuery _boardQuery;

    /// <summary>
    /// 本次指针拖动是否已经在处理中。
    /// </summary>
    /// <remarks>
    /// 同一次触摸会被 WPF 提升成<b>多条</b>链路（Stylus / Mouse，见 M4.2 的一体机诊断日志），
    /// 而每条链路的 Down/Up 都会送到这里。若不去重，插件工具会收到"两次 Down、两个起点"
    /// 或者"Up 比 Down 多一次"的错乱序列。所以只认第一条链路的开始，其余全部忽略。
    /// </remarks>
    private bool _toolPointerActive;

    private PageRenderScheduler? _scheduler;
    private IPdfDocumentService? _document;

    private bool _autoFitWidth = true;
    private bool _renderUpdateQueued;

    /// <summary>
    /// 渲染需求的冻结计数（0 = 正常）。导出期间用它挡住"视口一变就重算需求"。
    /// </summary>
    /// <remarks>
    /// 用计数而不是 bool：冻结有可能嵌套（整卷导出里逐页各开一次小会话的情形），
    /// 内层一还就把外层的冻结解掉，是那种本机复现不出来、一体机上偶发白屏的 bug。
    /// </remarks>
    private int _renderFreezeDepth;

    private bool _panning;
    private Point _panLast;

    /// <summary>设备清单只需要在进程内记一次（窗口重建时不重复刷日志）。</summary>
    private static bool _environmentLogged;

    public CanvasViewportHost()
    {
        InitializeComponent();

        // M13 S2：纸张底色 = 共享可变画刷（不 Freeze）—— RootGrid 与每页纸面矩形
        // 指向同一个实例，换色只改 brush.Color 一处，全部页面（含未建页）同时生效。
        RootGrid.Background = _paperBrush;

        // 启动时把输入设备清单原样记下来 —— 这是"这台机器到底把笔当什么"的第一手判据。
        // 一体机上没有调试器，而"策略为什么被判错"这件事只能从这里反推。
        if (!_environmentLogged)
        {
            _environmentLogged = true;
            InputDiagnostics.LogEnvironment();
        }

        // 撤销引擎要接管笔画集合，必须在 InitializeComponent 之后建
        _history = new InkHistory(InkLayer.Strokes);
        _history.Changed += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);

        // 吸附基准的来源（M7.4 Step 1）。同样要在 InitializeComponent 之后：
        // 它订阅的是 InkLayer.Strokes.StrokesChanged。
        // ★ M12：把图形对象与渲染器目录也接进来 —— SnapToGfx 才有"图形层吸附目标"可查。
        //   _gfxStore 在后面才 new，这里用闭包读取字段（调用发生在工具拖动时，早已就位）。
        _boardQuery = new BoardQuery(InkLayer.Strokes, () => _gfxStore!.Objects, _gfxCatalog);

        // 图形对象层（M7.4）。顺序有依赖：
        //   对象集合要给渲染器注册表（对象要它量尺寸）→ 历史要对象集合 →
        //   统一时间线要两条子历史 → 门面要全部三样 → 视觉层要集合 + 注册表。
        _gfxStore = new GfxObjectStore(_gfxCatalog);
        _gfxHistory = new GfxHistory(_gfxStore);
        _boardHistory = new BoardHistory(_history, _gfxHistory);
        // M22 S3：把位图仓库与"视口中心在哪一页"交给图形门面 ——
        // 插件的 AddImage 据此算尺寸与落点（复用导出服务那两个纯函数）。
        _gfxHost = new HostGfxObjectHost(
            _gfxStore, _gfxHistory, _boardHistory,
            _webPanelImages,
            currentPage: () => PageUnderViewportCenter,
            viewportCenterWorld: () => ViewportCenterWorld);
        _gfxObjects = new GfxObjectLayer(GfxLayer, _gfxStore, _gfxCatalog);

        // 参数面板（M7.4 Step 5 批次 3）：协调器是纯逻辑（无控件），视图只管长出来。
        // 分成两半的理由很实在 —— 前者能在验收 harness 里（没有窗口）被完整断言。
        _gfxParameters = new GfxParameterCoordinator(_gfxStore, _gfxCatalog, _gfxHost);

        _gfxPanelView = new GfxParameterPanelView(_gfxParameters, ReportGfxParameterStatus);
        _gfxPanelView.SetCloseHandler(() => _gfxStore.Select(null));
        RootGrid.Children.Add(_gfxPanelView);

        // 选中项一变就重画面板；对象被改（拖位姿、改数字、被删）也重画 ——
        // 少了后面这条，面板上会留着"上一秒的值"（例如刚拖动过），
        // 而老师看着面板上的数去调另一个参数时会以错的值作基准。
        _gfxStore.SelectionChanged += (_, _) => RefreshGfxParameterPanel();
        _gfxStore.Changed += OnGfxContentChanged;

        // 统一时间线也影响撤销按钮的可用态：界面只需要认这一个事件源。
        _boardHistory.Changed += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);

        // 创建型工具落成对象后自动切回"选择"（Q5 的拍板）。
        // 用 BeginInvoke 推迟到本次指针分发之后：此刻工具正在 OnPointer 里，
        // 直接切工具会在它自己还没返回时就调 Deactivate，状态机正好走到一半。
        _gfxStore.ObjectAdded += OnGfxObjectAdded;

        _gestures = new ViewportGestureRouter(
            _viewport,
            () => new Size(ActualWidth, ActualHeight),
            () => _layout.WorldBounds);

        // 用户一旦自己动过视口，就不再让"窗口尺寸变化"去纠正它 ——
        // 否则老师刚缩放到某道题，拖一下窗口就被弹回适应宽度。
        _gestures.ViewportMutated += (_, _) => _autoFitWidth = false;

        _viewport.Changed += OnViewportChanged;
        WorldTransform.Matrix = _viewport.WorldToViewport;

        SizeChanged += OnHostSizeChanged;

        // 工具系统（M7.1）：先把宿主能力门面交给注册表，再注册内置工具。
        // 注册第一个工具时会自动激活它 ⇒ ApplyTool 在这里先跑一趟（只设墨迹层的
        // 命中 / Manipulation / EditingMode / 光标，不依赖 DrawingAttributes，安全），
        // 随后 SetupInkLayer 末尾还会再跑一次，保证与笔属性的初始化顺序无关。
        _toolContext = new HostToolContext(this);
        _tools.AttachContext(_toolContext);
        _tools.ActiveToolChanged += OnActiveToolChanged;

        new BuiltInTools().Register(_tools);

        // M12 题号标记：宿主侧工具 + 宿主侧渲染器（数据/持久化/命中全走图形层既有机制）。
        // 渲染器必须在任何对象落成之前注册（Add 时按 Kind 量尺寸）。
        _gfxCatalog.Register(new QuestionMarkRenderer());
        _tools.Add(new QuestionMarkTool());

        // M22 S4b 仿真截图（webPanelImage）：同样是宿主侧渲染器 + 宿主侧数据。
        // 必须在任何对象落成之前注册 —— Add 时就要按 Kind 量尺寸。
        _gfxCatalog.Register(new WebPanelImageRenderer(_webPanelImages));

        // 选择工具排在最后：它是"对已经放下的图形做操作"，天然跟在全部工具之后。
        _selectionTool = new GfxSelectionTool(_gfxStore, _gfxHost);
        _tools.Add(_selectionTool);

        // M12 图层面板：浮在 RootGrid 上的屏幕坐标元素（与参数面板同一挂法，不进世界层）。
        _layerPanelView = new LayerPanelView(
            getActiveLayer: () => ActiveInkLayerId,
            isLayerVisible: IsInkLayerVisible,
            setActiveLayer: SetActiveInkLayer,
            setLayerVisible: SetInkLayerVisible,
            clearDraft: () => ClearDraftLayer(),
            close: () => ShowLayerPanel(false));
        _layerPanelView.Visibility = Visibility.Collapsed;
        RootGrid.Children.Add(_layerPanelView);

        // M13 S5 计算器：同一挂法（RootGrid 屏幕坐标层，不进世界层）。
        // 拖动把手 = 标题行；位置会话内有效，不落盘（课堂临时工具，重启回默认位）。
        // M16：拖动中走渲染变换，松手由 FloatingDrag 自己落 Margin（会话内位置）。
        _calculatorView = new CalculatorPanelView();
        RootGrid.Children.Add(_calculatorView);
        FloatingDrag.Attach(_calculatorView.TitleBar, _calculatorView, RootGrid);

        // M12 回放控制条：同一挂法，默认收起。
        _replayPanelView = new ReplayPanelView(
            step: ReplayStepForward,
            toggleAuto: ToggleReplayAutoPlay,
            end: EndReplay);
        _replayPanelView.Visibility = Visibility.Collapsed;
        RootGrid.Children.Add(_replayPanelView);
        ReplayChanged += (_, _) => _replayPanelView.Refresh(IsReplayActive, ReplayPosition, ReplayTotal, _replayAutoPlay);

        SetupInkLayer();
    }

    /// <summary>
    /// 画布上新增了一个图形对象。
    /// </summary>
    /// <remarks>
    /// 判据是"<b>真的落成了对象</b>"而不是"点了按钮"：误触（按下就抬）不该把工具带走，
    /// 否则老师想画个坐标系、手一抖，工具就自己跑掉了。
    /// </remarks>
    private void OnGfxObjectAdded(object? sender, IGfxObjectRef obj)
    {
        if (_tools.ActiveTool is not IGfxTool) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // 再确认一次：工具可能在这两个 tick 之间被用户手动切走了
            if (_tools.ActiveTool is IGfxTool) SetTool(ToolIds.GfxSelect);
        }));
    }

    /// <summary>当前工具发生变化：把它落到墨迹层上。</summary>
    private void OnActiveToolChanged(object? sender, EventArgs e)
    {
        AppLog.Info($"工具切换：{ActiveToolId ?? "(无)"}（{_tools.ActiveTool?.DisplayName ?? "-"}）");
        ApplyTool();

        // ★ 瞬态工具（M7.5）：命令执行完立刻把控制权交回上一个工具。
        //   不交的后果很具体 —— 用户点一下"GeoGebra 演示"之后就一直停在这个工具上，
        //   画布不响应笔，看起来就是"白板卡死了"。
        if (_tools.ActiveTool is not ITransientTool) return;

        var back = _toolBeforeTransient ?? ToolIds.Pen;
        _toolBeforeTransient = null;

        AppLog.Info($"瞬态工具已执行，控制权交回「{back}」。");

        // 直接调注册表：绕过 SetTool，免得又记一次"上一个工具"（那会让交还目标错位）。
        _tools.Activate(back);
    }

    /// <summary>初始化墨迹层：默认笔属性、默认工具、以及笔输入的分工过滤。</summary>
    private void SetupInkLayer()
    {
        // 笔属性 + 压感/笔锋的合成关系，两件事在同一处落（见 SyncInkPressureSettings）
        SyncInkPressureSettings();

        // ★ M11：把笔采样插件的统计出口接到现场记录仪上。
        //   插件本身跑在笔线程，但统计是**投递到 UI 线程之后**才回调的
        //   （见 InkSamplerPlugIn.Publish），所以这里直接写 _diag 不需要任何锁。
        InkLayer.Sampler.AttachSampledSink(stats => _diag.NoteSampledStroke(stats));
        AppLog.Info($"笔采样插件就绪：{InkLayer.Sampler.DescribeSettings()}");

        // 光标控制权收归自己：UseCustomCursor=false 时 InkCanvas 会按 EditingMode
        // 自动覆盖 Cursor（内置笔形/橡皮形光标），我们在 ApplyTool 里设的值就不作数了。
        // 而"现在能写还是能擦"必须准确反映在光标上 —— 老师在一体机上全靠它判断当前工具。
        InkLayer.UseCustomCursor = true;

        // 橡皮的尺寸与方式。形状用 world 单位（见 EraserProfile），
        // 笔尾反转则交给 InkCanvas 原生的 EditingModeInverted ——
        // 于是"笔尾当橡皮"一行擦除逻辑都不用写，我们只负责把反转输入**放行**（见 FilterStylusInput）。
        ApplyEraserSettings();

        // 用「预览」阶段拦截：被抑制的输入根本传不到 InkCanvas 内部的采集器，
        // 于是"手指画墨 / 手掌误擦"在源头就被掐掉，而不是画完再删。
        InkLayer.PreviewStylusDown += OnInkPreviewStylusDown;
        InkLayer.PreviewStylusMove += OnInkPreviewStylusMove;
        InkLayer.PreviewStylusUp += OnInkPreviewStylusUp;

        // 抬笔后校正压感（设备不报压力时按恒定笔宽处理）
        InkLayer.StrokeCollected += OnStrokeCollected;

        // 写字 / 擦除 / 清空都会改动这个集合，界面（状态栏的"笔迹 N 条"）靠它刷新
        InkLayer.Strokes.StrokesChanged += OnStrokesChanged;

        ApplyTool();
    }

    /// <summary>视口（世界坐标 ↔ 视口坐标换算的唯一真值源）。</summary>
    public CanvasViewport Viewport => _viewport;

    /// <summary>页面在世界坐标中的排版结果。</summary>
    public WorldLayout Layout => _layout;

    /// <summary>
    /// 画布底色 —— 也就是"页外空间"的颜色。默认<b>纯白</b>：页外留白与页间缝隙
    /// 和纸面同色，整个视口看起来就是一张连续的纸。
    /// </summary>
    /// <remarks>
    /// 做成可写属性纯粹是为了验收：光栅断言原本靠"白纸外接框"定位页面，
    /// 底色一旦也是白的，那条断言就失去区分度（白像素会铺满整个视口）。
    /// 所以 harness 会临时切黑底跑那几条定位断言，再切回白底验证"页外纯白"。
    /// </remarks>
    public Brush CanvasBackground
    {
        get => RootGrid.Background;
        set => RootGrid.Background = value ?? Tokens.Brush("Canvas.PageWhite");
    }

    /// <summary>
    /// M13 S2：纸张底色画刷（共享单实例）。RootGrid（页外留白）与每页纸面矩形
    /// 都指向它 —— 换色改 <see cref="SetPaperColor"/> 一处，所见即所得地进导出
    /// （页内矩形在 WorldHost 内，离屏渲染自然带上底色）。
    /// </summary>
    private readonly SolidColorBrush _paperBrush =
        new(Tokens.Color("Canvas.PageWhite"));

    /// <summary>当前纸张底色（M13 S2）。</summary>
    public Color PaperColor => _paperBrush.Color;

    /// <summary>
    /// 换纸张底色。顺手把 RootGrid 底接回共享画刷 —— harness 验收时会把
    /// <see cref="CanvasBackground"/> 临时换成黑底定位页面，这里负责复原共用关系。
    /// </summary>
    public void SetPaperColor(Color color)
    {
        _paperBrush.Color = color;
        RootGrid.Background = _paperBrush;
    }

    /// <summary>笔的属性（颜色 + 世界单位笔宽）。</summary>
    public PenProfile Pen => _pen;

    /// <summary>橡皮的属性（世界单位半径 + 擦除方式）。</summary>
    public EraserProfile Eraser => _eraser;

    /// <summary>
    /// 设置墨色并即时生效。已落下的笔画保持原色 —— 墨已经落在纸上了。
    /// </summary>
    public void SetPenColor(Color color)
    {
        _pen.Color = color;
        ApplyPenSettings();
    }

    /// <summary>设置笔宽（world 单位）并即时生效。</summary>
    public void SetPenWidth(double worldWidth)
    {
        _pen.WorldWidth = worldWidth;
        ApplyPenSettings();
    }

    /// <summary>开关压感（设备原始压力是否生效）。</summary>
    /// <remarks>
    /// 关压感 = 笔宽恒定。M4 出厂就是关的：鼠标与一部分不报压力的电容笔压力因子恒为 0.5，
    /// 贸然打开会让整块笔迹统一变成一半粗（见 <see cref="PressurePolicy"/>）。
    /// <para>
    /// ★ M11 起它与「笔锋」是两个独立开关，但**共同决定同一个 <c>IgnorePressure</c>** ——
    /// 合成关系全部收敛到 <see cref="SyncInkPressureSettings"/> 一处。
    /// </para>
    /// </remarks>
    public void SetPressureEnabled(bool enabled)
    {
        _pressureEnabled = enabled;
        SyncInkPressureSettings();
    }

    /// <summary>
    /// 开关笔锋（速度 → 压力因子，见 <see cref="SpeedPressureMap"/>）。出厂默认<b>开</b>。
    /// </summary>
    /// <remarks>
    /// 关掉它之后，插件<b>只采集、不改点</b>，落墨结果与 M10 完全一致 ——
    /// 这就是"笔锋效果不好看时的一键回退"。
    /// </remarks>
    public void SetCraftEnabled(bool enabled)
    {
        _craftEnabled = enabled;
        SyncInkPressureSettings();
        AppLog.Info($"笔锋设置变更：{InkLayer.Sampler.DescribeSettings()}");
    }

    /// <summary>设置笔锋强度档位。</summary>
    public void SetCraftStrength(CraftStrength strength)
    {
        _craftStrength = strength;
        SyncInkPressureSettings();
        AppLog.Info($"笔锋强度：{SpeedPressureMap.Name(strength)}（宽度区间 "
                    + $"{SpeedPressureMap.MinFactor:F2}~{SpeedPressureMap.MaxFactor:F2} 倍笔宽）");
    }

    /// <summary>压感开关的当前状态。</summary>
    public bool IsPressureEnabled => _pressureEnabled;

    /// <summary>笔锋是否开启。</summary>
    public bool IsCraftEnabled => _craftEnabled;

    /// <summary>当前笔锋档位。</summary>
    public CraftStrength CraftLevel => _craftStrength;

    /// <summary>设置笔迹平滑总开关（M18；关 = 原样落墨，与 M17 行为一致）。</summary>
    public void SetSmoothEnabled(bool enabled)
    {
        InkLayer.Sampler.SmoothEnabled = enabled;
        AppLog.Info($"笔迹平滑：{(enabled ? "开" : "关")}。");
    }

    /// <summary>设置平滑强度档位（M18：Light / Medium）。</summary>
    public void SetSmoothStrength(SmoothStrength strength)
    {
        InkLayer.Sampler.SmoothLevel = strength;
        AppLog.Info($"笔迹平滑档位：{strength}。");
    }

    /// <summary>平滑是否开启。</summary>
    public bool IsSmoothEnabled => InkLayer.Sampler.SmoothEnabled;

    /// <summary>
    /// 墨迹层控件本体（<see cref="InkSurfaceCanvas"/>）。
    /// </summary>
    /// <remarks>
    /// 给验收 harness 断言"插件链顺序"用 —— <c>StylusPlugIns</c> 是 protected，
    /// 只有拿到派生类才够得着。产品代码一律走 <see cref="InkSurface"/>。
    /// </remarks>
    public InkSurfaceCanvas InkSamplerHost => InkLayer;

    /// <summary>
    /// 压感 / 笔锋 的<b>唯一</b>合成点。
    /// </summary>
    /// <remarks>
    /// WPF 只有一个 <c>DrawingAttributes.IgnorePressure</c>，而界面上有两个开关，
    /// 于是"什么情况下走压力管线"必须在一处写清楚：
    /// <list type="bullet">
    /// <item>只开压感 ⇒ 用设备压力（M4 的老行为）；</item>
    /// <item>只开笔锋 ⇒ 用速度伪造的压力（一体机上压感常常不存在，这是主用法）；</item>
    /// <item><b>两个都开</b> ⇒ 插件里按几何平均合成（<see cref="SpeedPressureMap.Blend"/>）；</item>
    /// <item>两个都关 ⇒ <c>IgnorePressure=true</c>，笔宽恒定（与 M10 完全一致）。</item>
    /// </list>
    /// </remarks>
    private void SyncInkPressureSettings()
    {
        _pen.IgnorePressure = !(_pressureEnabled || _craftEnabled);

        var sampler = InkLayer.Sampler;
        sampler.CraftEnabled = _craftEnabled;
        sampler.CraftLevel = _craftStrength;
        sampler.PressureEnabled = _pressureEnabled;

        ApplyPenSettings();
    }

    /// <summary>
    /// 把笔属性同步到墨迹层。
    /// </summary>
    /// <remarks>
    /// 只改 <c>DefaultDrawingAttributes</c>，即<b>只影响之后新落下的笔画</b>。
    /// 这不是偷懒：已经画好的笔画若跟着变色变粗，就等于"重写了历史"，
    /// 而正确语义是墨已经落在纸上了。也正因为不改已有笔画，这里不触发任何几何重算。
    /// </remarks>
    public void ApplyPenSettings() => InkLayer.DefaultDrawingAttributes = _pen.ToDrawingAttributes();

    /// <summary>
    /// 把橡皮属性同步到墨迹层。
    /// </summary>
    /// <remarks>
    /// 三处都要落：
    /// <list type="bullet">
    /// <item><c>EraserShape</c> —— 点擦的命中半径（world 单位）</item>
    /// <item><c>EditingModeInverted</c> —— 笔尾反转时用哪种方式擦</item>
    /// <item><c>EditingMode</c> —— 只在当前正握着橡皮工具时才需要跟着改</item>
    /// </list>
    /// 少设第一项的后果最隐蔽：橡皮会按默认的 8×8 擦，而代码里读到的半径却是 20，
    /// 排查时怎么对都对得上，就是"擦的范围不对"。
    /// </remarks>
    public void ApplyEraserSettings()
    {
        InkLayer.EraserShape = _eraser.ToStylusShape();
        InkLayer.EditingModeInverted = _eraser.Mode;

        // 只有当前工具就是擦除类工具时，才需要把墨迹层的采集模式跟着改（M7.1 起按工具声明判断）
        if (_tools.ActiveTool?.InkMode == ToolInkMode.EraseByPoint) InkLayer.EditingMode = _eraser.Mode;
    }

    /// <summary>
    /// 墨迹层控件。暴露出来是为了验收 harness 能往真实控件树里注入测试笔画，
    /// 以及 M6 做序列化时能拿到 <see cref="StrokeCollection"/>。
    /// </summary>
    public InkCanvas InkSurface => InkLayer;

    /// <summary>当前笔迹集合。换文档会清空（M6 才做持久化）。</summary>
    public StrokeCollection Strokes => InkLayer.Strokes;

    /// <summary>当前笔迹条数。</summary>
    public int StrokeCount => InkLayer.Strokes.Count;

    // ---------------------------------------------------------------- 图形对象（M7.4）

    /// <summary>图形对象集合：学科工具落成的矢量图形的真相源。</summary>
    public GfxObjectStore GfxObjects => _gfxStore;

    /// <summary>渲染器注册表。插件加载器把插件提供的渲染器登记在这里。</summary>
    public GfxRendererCatalog GfxRenderers => _gfxCatalog;

    /// <summary>递给工具的图形对象门面（<see cref="IToolContext.Gfx"/> 的落地实现）。</summary>
    internal IGfxObjectHost GfxHost => _gfxHost;

    /// <summary>吸附基准查询（<see cref="IToolContext.Query"/> 的落地实现）。</summary>
    internal IBoardQuery BoardQuery => _boardQuery;

    /// <summary>图形对象层（harness 据此沿真实视觉树验证"渲染与数学同源"）。</summary>
    public GfxObjectLayer GfxObjectLayer => _gfxObjects;

    /// <summary>当前画布上的图形对象个数。</summary>
    public int GfxObjectCount => _gfxStore.Count;

    /// <summary>仿真截图的位图仓库（宿主与 .twb 图像段之间的唯一出入口）。</summary>
    public WebPanelImageStore WebPanelImages => _webPanelImages;

    /// <summary>参数面板的协调器（harness 据此沿真实链路验证"改参数 ⇒ 对象变"）。</summary>
    public GfxParameterCoordinator GfxParameters => _gfxParameters;

    /// <summary>参数面板是否正在显示（harness / 诊断用）。</summary>
    public bool IsGfxPanelVisible => _gfxPanelView.Visibility == Visibility.Visible;

    /// <summary>按当前选中对象重填参数面板。</summary>
    /// <remarks>
    /// 收到的三类事件（选中变化、对象改动、对象增删）都走这里，所以它必须<b>幂等</b>：
    /// 多调一次不该有任何副作用（只是重画一遍界面）。
    /// </remarks>
    private void RefreshGfxParameterPanel() => _gfxPanelView.Refresh();

    /// <summary>把参数面板的结果写到状态栏。</summary>
    /// <remarks>
    /// 复用工具那条状态栏通道（<see cref="ToolStatusMessage"/>）而不是另开一条：
    /// 状态栏只有一条，两条通道会互相覆盖，而"谁最后写的"在界面上根本看不出来。
    /// </remarks>
    private void ReportGfxParameterStatus(string text)
        => ToolStatusMessage?.Invoke(this, text);

    /// <summary>
    /// 登记一个插件提供的渲染器。
    /// </summary>
    /// <remarks>
    /// 走宿主而不是让加载器直接碰注册表：这样"哪些 Kind 已经有人认领"
    /// 这件事只有一个写入点，日志与拒绝理由也都收在一处。
    /// </remarks>
    public bool RegisterGfxRenderer(IGfxObjectRenderer renderer, string? pluginName = null)
        => _gfxCatalog.Register(renderer, pluginName);

    /// <summary>导出图形对象（落盘用；与笔迹一起写进同一个侧车文件）。</summary>
    public IReadOnlyList<GfxObjectData> SnapshotObjects() => _gfxStore.Snapshot();

    /// <summary>
    /// 用存档里的对象整体替换当前图形。
    /// </summary>
    /// <remarks>
    /// 装载<b>不</b>进撤销历史：与 <see cref="ReplaceStrokes"/> 同理，
    /// 否则按一下 Ctrl+Z 就把刚恢复的板书全抹了，而且看不出是谁干的。
    /// </remarks>
    public void ReplaceObjects(IReadOnlyList<GfxObjectData>? objects)
    {
        _gfxHistory.Reset();
        _gfxStore.Restore(objects);
        _boardHistory.Reset();
    }

    /// <summary>
    /// 用存档里的位图整体替换当前的仿真截图（M22 S4b）。
    /// </summary>
    /// <remarks>
    /// ★ <b>必须先于 <see cref="ReplaceObjects"/> 调用。</b>对象装回来时会走渲染器建视觉，
    /// 那时若位图还没进仓库，唯一的结果就是一张"仿真截图缺失"占位框 —— 而对象本身是好的，
    /// 于是表现成"图没了但框还在"，看着像渲染坏了，实际是顺序错了。
    /// </remarks>
    public void ReplaceWebPanelImages(IEnumerable<TwbImageEntry>? images)
        => _webPanelImages.Restore(images);

    /// <summary>视口中心所在的页面矩形（世界坐标）；没有试卷时 <c>null</c>。</summary>
    /// <remarks>仿真截图的落点要靠它（见 <c>WebPanelExportService.ComputePlacement</c>）。</remarks>
    public Rect? PageUnderViewportCenter
    {
        get
        {
            int index = CurrentPageIndex;
            return index >= 0 && index < _layout.PageRects.Count ? _layout.PageRects[index] : null;
        }
    }

    /// <summary>视口中心的世界坐标（"老师此刻在看哪儿"）。</summary>
    public Point ViewportCenterWorld
        => _viewport.ToWorld(new Point(ActualWidth / 2.0, ActualHeight / 2.0));

    /// <summary>
    /// 删除当前选中的图形对象（<c>Del</c> 键与工具栏按钮的入口）。
    /// </summary>
    /// <returns>是否真的删了。</returns>
    public bool DeleteSelectedObject()
    {
        var selected = _gfxStore.Selected;
        if (selected is null) return false;

        _gfxHost.BeginStep("删除图形");
        return _gfxStore.Remove(selected.Id);
    }

    /// <summary>
    /// 让当前工具重建常驻预览（harness 与视图在外部改动选中项后调用）。
    /// </summary>
    public void RefreshToolPreviewNow() => RefreshToolPreview();

    /// <summary>已注册的工具（M7.1 起由注册表给出，界面不再硬编码按钮）。</summary>
    public IReadOnlyList<ITool> Tools => _tools.Tools;

    /// <summary>当前工具。注册了至少一个工具后恒不为 null。</summary>
    public ITool? ActiveTool => _tools.ActiveTool;

    /// <summary>当前工具的 Id；无工具时为 <c>null</c>。</summary>
    public string? ActiveToolId => _tools.ActiveTool?.Id;

    /// <summary>
    /// 切换工具（按 Id）。
    /// </summary>
    /// <remarks>
    /// 换成 Id 字符串而不是枚举，是因为工具可以由插件提供 ——
    /// 宿主在编译期不可能认识一个还没写出来的工具的枚举成员。
    /// </remarks>
    /// <returns>是否切换成功（Id 不存在时返回 false 并记日志）。</returns>
    public bool SetTool(string toolId)
    {
        // ★ 瞬态工具（M7.5）：它是"一条命令"而不是"一种模式"。先记下现在用的是什么，
        //   等它激活完（见 OnActiveToolChanged）再把控制权交回去。
        var target = _tools.Find(toolId);
        var current = _tools.ActiveTool;

        if (target is ITransientTool
            && current is not null
            && current is not ITransientTool
            && !string.Equals(current.Id, toolId, StringComparison.Ordinal))
        {
            _toolBeforeTransient = current.Id;
        }

        // ★ 可取消工具（M19）：先记下"激活它之前用的是什么"，Esc 兜底时切回去。
        //   重复点同一个工具不算"切走"（注册表对同引用 Activate 直接早退），别覆盖记录。
        if (target is ICancellableTool
            && current is not null
            && !string.Equals(current.Id, toolId, StringComparison.Ordinal))
        {
            _toolBeforeCancellable = current.Id;
        }

        return _tools.Activate(toolId);
    }

    /// <summary>
    /// Esc 的第一落点（M19）：先让当前工具自己取消当前一步；工具无可取消状态时切回上一工具。
    /// </summary>
    /// <returns>
    /// <c>true</c> = 这次 Esc 已被工具层处理（取消一步，或已切回上一工具）；
    /// <c>false</c> = 当前工具不支持取消，Esc 原样放行（继续走"退全屏"等宿主语义）。
    /// </returns>
    public bool TryCancelActiveTool()
    {
        if (_tools.ActiveTool is not ICancellableTool cancellable)
        {
            return false;
        }

        // 工具自己处理：取消当前一步但仍留在原地（例如圆规丢弃拖到一半的半径预览）。
        if (cancellable.TryCancel())
        {
            return true;
        }

        // 工具无可取消状态：Esc 的「退出」语义 —— 切回激活它之前的工具（没有则回笔）。
        var back = _toolBeforeCancellable ?? ToolIds.Pen;
        _toolBeforeCancellable = null;

        // 上一工具就是当前工具时不要调 SetTool：注册表对同引用 Activate 直接早退，
        // 而 SetTool 开头还会把这个 Id 再记成「上一工具」，让记录白错位一次。
        if (!string.Equals(ActiveToolId, back, StringComparison.Ordinal))
        {
            SetTool(back);
        }

        return true;
    }

    /// <summary>
    /// 按快捷键查工具；没有工具声明这个键时返回 <c>null</c>（界面据此决定是否吞掉按键）。
    /// </summary>
    public ITool? FindToolByShortcut(Key key) => _tools.FindByShortcut(key);

    /// <summary>
    /// 注册一个工具（M7.2 的插件加载器与验收 harness 用）。
    /// </summary>
    /// <remarks>
    /// 对外只开放"加"：工具一旦注册进来，按钮、快捷键、输入归属全部自动生效 ——
    /// 这正是工具系统的价值所在。
    /// <para>
    /// <b>注意顺序</b>：界面按钮是在 <c>MainWindow</c> 构造时按注册表一次性生成的，
    /// 所以"M7.2 加载插件"必须发生在生成按钮<b>之前</b>，否则插件工具能按快捷键切到、
    /// 却没有按钮（看起来像"插件没加载成功"）。
    /// </para>
    /// </remarks>
    public void AddTool(ITool tool) => _tools.Add(tool);

    /// <summary>
    /// 递给插件加载器的注册表门面（M7.2）。
    /// </summary>
    /// <remarks>
    /// 刻意只交出接口 <see cref="IToolRegistry"/> 而不是 <c>ToolRegistry</c>：
    /// 接口上只有 <c>Add</c> 与只读的 <c>Tools</c>，所以插件拿到它也只能"加自己的工具"，
    /// 不能删别人的、不能改当前工具 —— 权限收在类型上，比写在文档里管用。
    /// </remarks>
    public IToolRegistry PluginRegistry => _tools;

    /// <summary>
    /// 递给工具用的宿主能力门面。
    /// </summary>
    /// <remarks>
    /// 暴露出来是为了验收 harness 能走工具的真实调用路径（提交笔画、加预览、写状态栏），
    /// 而不是绕过门面直接调宿主 —— 前者才是工具实际会用到的那条路。
    /// </remarks>
    public IToolContext ToolContext => _toolContext;

    // ---------------------------------------------------------------- 演示面板（M7.5）

    /// <summary>
    /// 演示面板桥接。由 <c>MainWindow</c> 装配时注入。
    /// </summary>
    /// <remarks>
    /// 没注入时为 <c>null</c>（验收 harness 就是这种情况）：工具据此把按钮置灰，
    /// 而不是失效 —— 与 <see cref="IToolContext.Gfx"/> 的约定一致。
    /// </remarks>
    public IGeoGebraBridge? GeoGebra
    {
        get => _geoGebra;
        set => _geoGebra = value;
    }

    /// <summary>
    /// 通用 Web 面板桥接（M22）：GeoGebra 与物理仿真页共用同一套门面，按 profile 区分。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="GeoGebra"/> 并列、同一套降级约定（没注入时为 <c>null</c>）。
    /// 装配时两个属性指向同一个控制器实例。
    /// </remarks>
    public IWebPanelBridge? WebSim { get; set; }

    /// <summary>当前是否处于"面板模式"（画布输入被抑制）。</summary>
    public bool IsPanelModeActive => _panelMode;

    /// <summary>
    /// 进入面板模式：抑制画布输入（墨迹层不接输入、视口手势关闭）。
    /// </summary>
    /// <remarks>
    /// 必须幂等 —— 重复进入不能把状态搞乱（<see cref="IPanelHostHooks"/> 的约定）。
    /// </remarks>
    public void EnterPanelMode()
    {
        if (_panelMode) return;

        _panelMode = true;
        ApplyTool();   // 让抑制真正生效（顺带关掉视口手势）
        AppLog.Info("进入演示面板模式：画布输入已抑制。");
    }

    /// <summary>
    /// 退出面板模式：把画布输入<b>交还给当前工具</b>。
    /// </summary>
    /// <remarks>
    /// 是"让工具重新说了算"，不是"设成能写字"：用户进面板前若正用手工具，
    /// 退出后也不该一碰就出墨。所以走 <see cref="ApplyTool"/> 而不是直接赋 <c>true</c>。
    /// </remarks>
    public void ExitPanelMode()
    {
        if (!_panelMode) return;

        _panelMode = false;
        ApplyTool();
        AppLog.Info("退出演示面板模式：画布输入已交还当前工具。");
    }

    /// <summary><see cref="IPanelHostHooks"/>：面板的状态提示走宿主状态栏。</summary>
    void IPanelHostHooks.SetStatus(string text) => ReportToolStatus(text);

    /// <summary>清空全部笔迹。M5 起可撤销，所以不再需要"不可逆"的警告对话框。</summary>
    public void ClearStrokes() => InkLayer.Strokes.Clear();

    // ================================================================ M12：图层（批注 / 草稿）

    /// <summary>当前落墨归入的图层 Id（<see cref="InkLayers"/> 的常量之一）。</summary>
    public string ActiveInkLayerId => _activeInkLayerId;

    /// <summary>切换"新笔归哪层"。对已有笔画无影响 —— 墨已经落在纸上了。</summary>
    public void SetActiveInkLayer(string layerId)
    {
        if (layerId != InkLayers.AnnotationId && layerId != InkLayers.DraftId)
        {
            throw new ArgumentException($"未知的图层 Id：{layerId}", nameof(layerId));
        }

        if (_activeInkLayerId == layerId) return;

        _activeInkLayerId = layerId;
        AppLog.Info($"当前图层切换为「{DescribeLayer(layerId)}」。");
    }

    /// <summary>某层当前是否可见（批注层永远可见；草稿层看隐藏集合）。</summary>
    public bool IsInkLayerVisible(string layerId) => !_hiddenInkLayerIds.Contains(layerId);

    /// <summary>当前处于隐藏状态的层数（harness / 面板刷新用）。</summary>
    public int HiddenInkLayerCount => _hiddenInkLayerIds.Count;

    /// <summary>旁路集合里的笔画条数（harness 断言"显隐是移动不是删除"用）。</summary>
    public int HiddenStrokeCount => _hiddenInkStrokes.Count;

    /// <summary>
    /// 显示 / 隐藏一个图层。隐藏 = 该层笔画移进旁路集合（<b>不是删除</b>，显示时原样移回）。
    /// </summary>
    /// <remarks>
    /// ★ 两次包裹缺一不可：<see cref="InkHistory.Silently"/> 让移动不进撤销栈
    /// （显隐不是一次"编辑"），<see cref="_contentEventsSuspended"/> 让它不触发
    /// "内容变了"（藏一下草稿层不该亮"未保存"）。
    /// </remarks>
    public void SetInkLayerVisible(string layerId, bool visible)
    {
        if (layerId != InkLayers.AnnotationId && layerId != InkLayers.DraftId)
        {
            throw new ArgumentException($"未知的图层 Id：{layerId}", nameof(layerId));
        }

        if (IsInkLayerVisible(layerId) == visible) return;

        _contentEventsSuspended = true;

        try
        {
            _history.Silently(() =>
            {
                if (!visible)
                {
                    // 先快照再移除：枚举中改集合是未定义行为
                    var toHide = new List<System.Windows.Ink.Stroke>();
                    foreach (var stroke in InkLayer.Strokes)
                    {
                        if (InkLayers.LayerOf(stroke) == layerId) toHide.Add(stroke);
                    }

                    foreach (var stroke in toHide)
                    {
                        InkLayer.Strokes.Remove(stroke);
                        _hiddenInkStrokes.Add(stroke);
                    }

                    _hiddenInkLayerIds.Add(layerId);
                }
                else
                {
                    var toShow = new List<System.Windows.Ink.Stroke>();
                    foreach (var stroke in _hiddenInkStrokes)
                    {
                        if (InkLayers.LayerOf(stroke) == layerId) toShow.Add(stroke);
                    }

                    foreach (var stroke in toShow)
                    {
                        _hiddenInkStrokes.Remove(stroke);
                        InkLayer.Strokes.Add(stroke);
                    }

                    _hiddenInkLayerIds.Remove(layerId);
                }
            });
        }
        finally
        {
            _contentEventsSuspended = false;
        }

        AppLog.Info($"图层「{DescribeLayer(layerId)}」{(visible ? "已显示" : "已隐藏")}"
                    + $"（旁路 {_hiddenInkStrokes.Count} 条）。");
    }

    /// <summary>
    /// 一键清空草稿层（可见的走正常撤销记录、可找回；藏在旁路里的顺手一并清掉）。
    /// </summary>
    /// <returns>是否真的清了东西。</returns>
    public bool ClearDraftLayer()
    {
        // 藏着的草稿笔不可见也擦不到，但"清空草稿"的语义是"草稿全部不要了"—— 一并清
        int hiddenDraftCount = 0;

        _history.Silently(() =>
        {
            var hiddenDrafts = new List<System.Windows.Ink.Stroke>();
            foreach (var stroke in _hiddenInkStrokes)
            {
                if (InkLayers.LayerOf(stroke) == InkLayers.DraftId) hiddenDrafts.Add(stroke);
            }

            hiddenDraftCount = hiddenDrafts.Count;
            foreach (var stroke in hiddenDrafts) _hiddenInkStrokes.Remove(stroke);
        });

        var drafts = new List<System.Windows.Ink.Stroke>();
        foreach (var stroke in InkLayer.Strokes)
        {
            if (InkLayers.LayerOf(stroke) == InkLayers.DraftId) drafts.Add(stroke);
        }

        if (drafts.Count == 0 && hiddenDraftCount == 0) return false;

        // 可见部分：一次事务包成一个撤销单元（按一下 Ctrl+Z 全部回来）
        if (drafts.Count > 0)
        {
            _history.BeginTransaction();

            try
            {
                foreach (var stroke in drafts) InkLayer.Strokes.Remove(stroke);
            }
            finally
            {
                _history.CommitTransaction();
            }
        }

        AppLog.Info($"草稿层已清空（{drafts.Count} 条可见 + 旁路余量一并清掉）。");
        return true;
    }

    /// <summary>层的中文说法（日志用）。</summary>
    private static string DescribeLayer(string layerId)
        => layerId == InkLayers.DraftId ? "草稿层" : "批注层";

    /// <summary>
    /// 计算器面板当前是否可见（命令总线 / 诊断用）。
    /// </summary>
    public bool IsCalculatorVisible => _calculatorView.Visibility == Visibility.Visible;

    /// <summary>
    /// 显示计算器面板（M13 S5）。首次出现靠右上放 —— 避开悬浮球常驻的右缘中部；
    /// 之后拖到哪算哪（会话内）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ToggleCalculator"/> 分开，是为了让总线上 <c>calc.open</c> 的语义诚实：
    /// "打开"就该是打开（幂等），而不是"碰一下翻个面" ——
    /// 宏里写 <c>calc.open</c> 却把已经开着的计算器关掉，是最难查的那种毛病。
    /// </remarks>
    public void ShowCalculator()
    {
        if (IsCalculatorVisible) return;

        double x = Math.Max(RootGrid.ActualWidth - 300, 12);
        _calculatorView.Margin = new Thickness(x, 64, 0, 0);
        _calculatorView.Toggle();

        // 留一行日志：计算器是"点了应该在、不在就是坏的"那种功能，
        // 出问题时老师手上唯一的证据就是日志（与 GeoGebra 停靠区撑开/收起同一习惯）。
        AppLog.Info("计算器面板已打开。");
    }

    /// <summary>隐藏计算器面板（关闭按钮与命令总线共用的出口）。</summary>
    public void HideCalculator()
    {
        if (!IsCalculatorVisible) return;

        _calculatorView.Close();
        AppLog.Info("计算器面板已收起。");
    }

    /// <summary>开关计算器面板（工具栏按钮与球菜单的 Toggle 语义）。</summary>
    public void ToggleCalculator()
    {
        if (IsCalculatorVisible) HideCalculator();
        else ShowCalculator();
    }

    /// <summary>图层面板的显示 / 隐藏。</summary>
    public void ShowLayerPanel(bool show)
    {
        if (show)
        {
            _layerPanelView.Refresh();
            _layerPanelView.Visibility = Visibility.Visible;
        }
        else
        {
            _layerPanelView.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>图层面板当前是否可见（harness / 界面状态用）。</summary>
    public bool IsLayerPanelVisible => _layerPanelView.Visibility == Visibility.Visible;

    // ================================================================ M12：翻页与题号导航

    /// <summary>视口中心所在页（0 起；居中在缝隙上时归入"下一页"——与存档口径一致）。</summary>
    public int CurrentPageIndex
    {
        get
        {
            double centerWorldY = _viewport
                .ToWorld(new Point(ActualWidth / 2.0, ActualHeight / 2.0)).Y;

            return ProjectComposer.ResolvePageIndex(_layout.PageRects, centerWorldY);
        }
    }

    /// <summary>跳到某页（保持当前缩放，页顶略上方落位）。跳页 = 用户意图 ⇒ 退出自动适应。</summary>
    public bool GoToPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _layout.PageRects.Count) return false;

        var rect = _layout.GetPageRect(pageIndex);
        double scale = _viewport.Scale;

        double offsetX = rect.X + rect.Width / 2.0 - (ActualWidth / 2.0) / scale;
        double offsetY = rect.Y - 40.0;

        RestoreView(scale, offsetX, offsetY);
        return true;
    }

    /// <summary>相对当前页翻 delta 页（+1 下一页 / -1 上一页），越界返回 <c>false</c>。</summary>
    public bool GoToPageRelative(int delta) => GoToPage(CurrentPageIndex + delta);

    /// <summary>画布上的题号标记，按"翻卷顺序"（页 → 页内从上到下）排好。</summary>
    public IReadOnlyList<IGfxObjectRef> OrderedQuestionMarks
        => QuestionMarkIndex.Ordered(_gfxStore.Objects, _layout.PageRects);

    /// <summary>视口中心最接近的题号标记下标；没有标记时 -1。</summary>
    public int CurrentQuestionMarkIndex
    {
        get
        {
            var marks = OrderedQuestionMarks;
            if (marks.Count == 0) return -1;

            var center = _viewport.ToWorld(new Point(ActualWidth / 2.0, ActualHeight / 2.0));
            return QuestionMarkIndex.NearestIndex(marks, center);
        }
    }

    /// <summary>跳到某个题号标记（保持缩放，标记居中）。</summary>
    public bool GoToQuestionMark(int index)
    {
        var marks = OrderedQuestionMarks;
        if (index < 0 || index >= marks.Count) return false;

        var center = marks[index].Center;
        double scale = _viewport.Scale;

        RestoreView(scale,
                    center.X - (ActualWidth / 2.0) / scale,
                    center.Y - (ActualHeight / 2.0) / scale);
        return true;
    }

    /// <summary>相对当前题号跳 delta 个标记。</summary>
    public bool GoToQuestionMarkRelative(int delta)
    {
        int current = CurrentQuestionMarkIndex;
        return GoToQuestionMark(current < 0 ? (delta > 0 ? 0 : -1) : current + delta);
    }

    // ================================================================ M12：笔迹回放（会话内）

    /// <summary>回放状态或位置变化（控制条据此刷新）。</summary>
    public event EventHandler? ReplayChanged;

    /// <summary>回放是否可用（有记录、且没被截断）。</summary>
    public bool CanReplay => _replayRecorder.Count > 0 && !_replayRecorder.Truncated;

    /// <summary>当前是否处于回放会话中。</summary>
    public bool IsReplayActive => _replay is not null;

    /// <summary>已重演到第几步（0 = 一笔都没有）。</summary>
    public int ReplayPosition => _replayApplied;

    /// <summary>回放总步数。</summary>
    public int ReplayTotal => _replay?.Steps.Count ?? 0;

    /// <summary>记录器本身（harness 断言记录语义用）。</summary>
    public ReplayRecorder ReplayRecorder => _replayRecorder;

    /// <summary>
    /// 开始回放：清空画布 → 逐步重演本会话的书写过程 → 结束时原样恢复现场。
    /// </summary>
    /// <remarks>
    /// ★ 三道闸：
    /// <list type="number">
    /// <item>回放期间 <see cref="_contentEventsSuspended"/>：重演不置脏、不被记录器再吃进去；</item>
    /// <item>所有集合改动走 <see cref="InkHistory.Silently"/>：不污染撤销栈；</item>
    /// <item><see cref="_replayMode"/> 压住画布输入（与面板模式同一套路）——
    /// 一边回放一边写字会让"重演"与"现场"分叉，结束时恢复的就是错的东西。</item>
    /// </list>
    /// </remarks>
    public void BeginReplay()
    {
        if (_replay is not null) return;

        if (_replayRecorder.Truncated)
        {
            ReportToolStatus("笔迹回放：本会话书写步数超过上限，回放不可用。");
            return;
        }

        if (_replayRecorder.Count == 0)
        {
            ReportToolStatus("笔迹回放：还没有可回放的书写过程（本会话先写几笔再试）。");
            return;
        }

        // 先记下现场（= 重演终点），回放结束原样恢复
        var final = new List<System.Windows.Ink.Stroke>();
        foreach (var stroke in InkLayer.Strokes) final.Add(stroke);

        var steps = new List<ReplayStep>(_replayRecorder.Steps);

        _replay = new ReplaySession(steps, final);
        _replayApplied = 0;
        _replayAutoPlay = false;

        _contentEventsSuspended = true;
        _replayMode = true;
        ApplyTool();

        _history.Silently(InkLayer.Strokes.Clear);

        _replayPanelView.Visibility = Visibility.Visible;
        ReplayChanged?.Invoke(this, EventArgs.Empty);
        AppLog.Info($"笔迹回放开始：共 {steps.Count} 步。");
    }

    private int _replayApplied;

    /// <summary>重演下一步（含这一步的移除与加入）。已到末尾时自动结束。</summary>
    public void ReplayStepForward()
    {
        var session = _replay;
        if (session is null) return;

        if (_replayApplied >= session.Steps.Count)
        {
            EndReplay();
            return;
        }

        var step = session.Steps[_replayApplied];
        _replayApplied++;

        _history.Silently(() =>
        {
            foreach (var stroke in step.Removed)
            {
                if (InkLayer.Strokes.Contains(stroke)) InkLayer.Strokes.Remove(stroke);
            }

            foreach (var stroke in step.Added)
            {
                if (!InkLayer.Strokes.Contains(stroke)) InkLayer.Strokes.Add(stroke);
            }
        });

        ReplayChanged?.Invoke(this, EventArgs.Empty);

        if (_replayApplied >= session.Steps.Count)
        {
            // 重演到头了：现场与终点一致，直接收尾
            EndReplay();
        }
    }

    /// <summary>开 / 关自动播放（开着时按固定节拍逐步重演）。</summary>
    public void ToggleReplayAutoPlay()
    {
        if (_replay is null) return;

        _replayAutoPlay = !_replayAutoPlay;

        if (_replayAutoPlay)
        {
            if (_replayTimer is null)
            {
                _replayTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(ReplayStepIntervalMs),
                };
                _replayTimer.Tick += (_, _) => ReplayStepForward();
            }

            _replayTimer.Start();
        }
        else
        {
            _replayTimer?.Stop();
        }

        ReplayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>自动播放的步间隔（毫秒）。</summary>
    public const double ReplayStepIntervalMs = 900.0;

    /// <summary>
    /// 结束回放：恢复开始时的现场（哪怕重演到一半被打断，落回的也必须是<b>老师真实的板书</b>）。
    /// </summary>
    public void EndReplay()
    {
        var session = _replay;
        if (session is null) return;

        _replay = null;
        _replayAutoPlay = false;
        _replayTimer?.Stop();

        _history.Silently(() =>
        {
            InkLayer.Strokes.Clear();
            foreach (var stroke in session.Final)
            {
                InkLayer.Strokes.Add(stroke);
            }
        });

        _contentEventsSuspended = false;
        _replayMode = false;
        ApplyTool();

        _replayPanelView.Visibility = Visibility.Collapsed;
        ReplayChanged?.Invoke(this, EventArgs.Empty);
        AppLog.Info("笔迹回放结束，现场已恢复。");
    }

    /// <summary>一次回放会话：重演的步序列 + 开始时的现场快照（= 终点）。</summary>
    private sealed class ReplaySession
    {
        public ReplaySession(List<ReplayStep> steps, List<System.Windows.Ink.Stroke> final)
        {
            Steps = steps;
            Final = final;
        }

        public List<ReplayStep> Steps { get; }

        public List<System.Windows.Ink.Stroke> Final { get; }
    }

    // ---------------------------------------------------------------- 工具系统支持（M7.1）

    /// <summary>
    /// 把工具产出的笔画提交到画布（<see cref="IToolContext.CommitStrokes"/> 的落地实现）。
    /// </summary>
    /// <remarks>
    /// 用一次撤销事务包裹整个提交，于是<b>一次工具操作 = 一个撤销单元</b>：
    /// 用直尺画一条线，按一下 Ctrl+Z 整条消失，而不是"消失一截"。
    /// <para>
    /// 走这条路交上来的笔画，与手写的笔画在系统里<b>完全一样</b> ——
    /// 自动获得世界坐标（随缩放平移不漂移）、橡皮可擦、<c>.tbink</c> 可存可读。
    /// 这就是"落墨型输出契约"的全部价值：插件一行持久化代码都不用写。
    /// </para>
    /// </remarks>
    public void CommitStrokes(StrokeCollection strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (strokes.Count == 0) return;

        _history.BeginTransaction();

        try
        {
            InkLayer.Strokes.Add(strokes);
        }
        finally
        {
            // 放 finally：中途抛异常也必须提交事务，否则事务会一直悬着，
            // 把**下一次**操作并进同一个撤销单元（表现为"按一下撤销掉两步"，极难定位）。
            _history.CommitTransaction();
        }
    }

    /// <summary>加一个临时预览视觉（世界坐标层）。工具拖动中的橡皮筋用它。</summary>
    public void AddPreview(UIElement visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        ToolPreviewLayer.Children.Add(visual);
    }

    /// <summary>清掉全部预览视觉。</summary>
    public void ClearPreview()
    {
        if (ToolPreviewLayer.Children.Count > 0) ToolPreviewLayer.Children.Clear();
    }

    /// <summary>当前预览视觉的个数（验收 harness 据此断言"抬笔后预览已清"）。</summary>
    public int PreviewCount => ToolPreviewLayer.Children.Count;

    /// <summary>工具想在状态栏说的话（例如「直线 12.3 cm，倾角 30°」）。</summary>
    public event EventHandler<string>? ToolStatusMessage;

    /// <summary>转发一句工具状态（<see cref="IToolContext.SetStatus"/> 的落地实现）。</summary>
    internal void ReportToolStatus(string text) => ToolStatusMessage?.Invoke(this, text);

    /// <summary>
    /// 导出当前笔迹的一份副本，供落盘使用。
    /// </summary>
    /// <remarks>
    /// 复制的是<b>集合</b>而不是笔画本身：笔画对象是不可变的（几何与属性都不会就地修改），
    /// 存盘只是读它们，没有理由克隆。克隆反而要自己保证压力、笔尖形状等细节一个不漏。
    /// </remarks>
    public StrokeCollection SnapshotStrokes()
    {
        var copy = new StrokeCollection();

        foreach (var stroke in InkLayer.Strokes)
        {
            copy.Add(stroke);
        }

        return copy;
    }

    /// <summary>
    /// 用给定笔迹整体替换当前笔迹（从磁盘恢复批注时使用）。
    /// </summary>
    /// <remarks>
    /// <b>整体替换而不是逐条 Add</b>，是为了让"装载批注"在撤销历史里完全不可见 ——
    /// 它走 <see cref="InkHistory.Silently"/>，天然不产生撤销记录；
    /// 随后再 <see cref="InkHistory.Reset"/> 把之前的历史一并清掉（换文档时旧历史本就该作废）。
    /// <para>
    /// 这样做的直接后果：装载完批注后按 Ctrl+Z <b>不会</b>把整份批注撤销掉。
    /// 否则用户一按就是"刚恢复的批注全没了"，而且看不出是谁干的。
    /// </para>
    /// </remarks>
    public void ReplaceStrokes(StrokeCollection strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        // 新批注灌进来时，旁路集合与"隐藏了哪些层"一并清零 —— 装载后全部可见（显隐是会话级状态）
        _hiddenInkStrokes.Clear();
        _hiddenInkLayerIds.Clear();

        _history.Silently(() =>
        {
            InkLayer.Strokes.Clear();

            if (strokes.Count > 0)
            {
                InkLayer.Strokes.Add(strokes);
            }
        });

        _history.Reset();
    }

    /// <summary>撤销 / 重做引擎。暴露出来主要供验收 harness 模拟"一次擦除产生的变更序列"。</summary>
    public InkHistory History => _history;

    /// <summary>
    /// 撤销一步（墨迹与图形共用一条时间线）。
    /// </summary>
    /// <remarks>
    /// 对外只暴露这一个入口：让界面去猜"该退字还是该退图"是错的设计 ——
    /// 老师按下 Ctrl+Z 时无法预知会退掉什么，那比"退错一步"更难受。
    /// </remarks>
    public bool Undo() => _boardHistory.Undo();

    /// <summary>重做一步。</summary>
    public bool Redo() => _boardHistory.Redo();

    public bool CanUndo => _boardHistory.CanUndo;

    public bool CanRedo => _boardHistory.CanRedo;

    /// <summary>历史发生变化（新操作 / 撤销 / 重做 / 换文档重置）。界面据此刷新按钮可用态。</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>
    /// 画布上的<b>批注内容</b>发生任何变化时触发：笔迹（写字 / 擦除 / 清空）与图形对象
    /// （新增 / 删除 / 拖位姿 / 改参数 / 装载复原）都算。
    /// </summary>
    /// <remarks>
    /// 界面靠它刷新条数，窗口靠它置「工程有未保存改动」标记、并给侧车自动保存重新计时。
    /// <para>
    /// ★ <b>两类内容必须都上报。</b>早期只跟笔迹，于是老师"拖一下坐标系、直接关窗口"
    /// 既不提示、也不会被存下来 —— 图形对象同样是老师一节课的成果，
    /// 少报一次的代价是静默丢改动（`docs/13` S3）。
    /// </para>
    /// </remarks>
    public event EventHandler? InkChanged;

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        // ★ M12：新落的笔画要打层标记（归属"当前层"）。
        //   擦除产生的碎笔画是个例外：它们是原笔的一部分，必须继承原笔的层 ——
        //   否则"草稿层里写个字、擦一半，剩下一半跑到批注层去了"，层语义就碎了。
        StampInkLayers(e);

        // 回放记录器：只吃"老师真实的手笔"。图层显隐、回放重演期间（抑制中）不记。
        if (!_contentEventsSuspended)
        {
            _replayRecorder.Observe(e);
            InkChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>给这次变更里新增的笔画补上层标记（已有标记的笔画原样跳过 —— 撤销加回就靠这条幂等）。</summary>
    private void StampInkLayers(StrokeCollectionChangedEventArgs e)
    {
        if (e.Added.Count == 0) return;

        // 擦除：新碎笔继承被擦原笔的层
        string? inherited = e.Removed.Count > 0 ? InkLayers.LayerOf(e.Removed[0]) : null;

        foreach (var stroke in e.Added)
        {
            InkLayers.MarkLayer(stroke, inherited ?? _activeInkLayerId);
        }
    }

    /// <summary>
    /// 图形对象的内容变了（新增 / 删除 / 拖位姿 / 改数字 / 装载）。
    /// </summary>
    /// <remarks>
    /// 先刷参数面板、再按「画布内容变化」上报：面板要立刻显示新值，
    /// 而上报之后发生什么（置脏标记 / 重写侧车 / 问要不要保存）是窗口的事 ——
    /// 宿主只对"内容变没变"负责。
    /// </remarks>
    private void OnGfxContentChanged(object? sender, EventArgs e)
    {
        RefreshGfxParameterPanel();
        InkChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 把当前工具的声明落到墨迹层上（M7.1 起：宿主不再自己判断"是什么工具"）。
    /// </summary>
    /// <remarks>
    /// 两条规则就是整套输入归属的全部：
    /// <code>
    /// IsHitTestVisible      = tool.UsesInkLayer
    /// IsManipulationEnabled = !tool.UsesInkLayer &amp;&amp; !tool.NeedsPointer
    /// </code>
    /// <list type="bullet">
    /// <item><b>"不落墨"用 <c>IsHitTestVisible</c>，绝不用 <c>e.Handled</c></b> ——
    /// M4.2 的一体机实测：<c>Handled</c> 挡不住跑在笔输入线程上的 <c>DynamicRenderer</c>
    /// （墨迹看得见、抬笔就消失），而且会泄漏输入捕获（之后所有触摸都失灵）。</item>
    /// <item><b>Manipulation 与墨迹层必须互斥</b> —— WPF 里二者是两条互相抢输入的路径，
    /// 且 Manipulation 不能按手指数过滤（单指落下也会启动并狂发 Translate）。
    /// 同时开着会一边落墨一边平移，并且把墨迹采集打断（笔画永远不提交）。</item>
    /// </list>
    /// </remarks>
    private void ApplyTool()
    {
        // 切工具时先清预览、复位指针状态：被切走的工具可能正停在拖动中途，
        // 而"工具自己会在 Deactivate 里清干净"是不可靠的（那是插件代码，M7.2 起还是别人写的）。
        // 清两次没有代价，漏一次就会在画布上留下一根永远不消失的预览线。
        ClearPreview();
        _toolPointerActive = false;

        // M18 打断点兜底：切工具会中途打断采集（IsHitTestVisible/EditingMode 翻转），
        // 湿墨可能残挂在屏幕上 —— 这里顺手清掉。进出面板模式也走本方法，一处覆盖三路。
        InkLayer.ResetActiveRenderer();

        var tool = _tools.ActiveTool;

        // 一个工具都没注册时保守地按"笔"配置，避免出现"怎么点都没反应"的死状态
        if (tool is null)
        {
            InkLayer.IsHitTestVisible = !_panelMode;
            IsManipulationEnabled = false;
            InkLayer.EditingMode = InkCanvasEditingMode.Ink;
            InkLayer.Cursor = Cursors.Pen;
            RefreshToolPreview();
            return;
        }

        // 面板模式（M7.5）与回放模式（M12）都压过工具：
        // 画布不接输入，"面板开着点画布留墨点 / 回放中途混进新笔"就都不可能发生。
        InkLayer.IsHitTestVisible = !_panelMode && !_replayMode && tool.UsesInkLayer;
        IsManipulationEnabled = !_panelMode && !_replayMode && !tool.UsesInkLayer && !tool.NeedsPointer;

        InkLayer.EditingMode = MapInkMode(tool.InkMode);

        // 光标必须准确反映"现在能写 / 能擦 / 能拖"——
        // 一体机上离得远，使用者全靠它判断当前是什么工具。
        InkLayer.Cursor = tool.Cursor ?? Cursors.Arrow;

        RefreshToolPreview();
    }

    /// <summary>
    /// 让当前工具重建它的<b>常驻</b>预览（选中框这类不是"拖动中才有"的图元）。
    /// </summary>
    /// <remarks>
    /// 必须由宿主主动叫：切工具的顺序是"新工具 <c>Activate</c> → 本方法清空预览层"，
    /// 于是工具在自己 <c>Activate</c> 里建的常驻预览会被紧接着清掉 ——
    /// 表现成"切到选择工具后看不到选中框，点一下才出现"。
    /// <para>
    /// 用内部标记接口而不是契约接口：这是宿主与自己内置工具之间的事，
    /// 创建型插件工具不需要常驻预览，让所有插件作者都实现一个空方法没有收益。
    /// </para>
    /// </remarks>
    private void RefreshToolPreview()
        => (_tools.ActiveTool as IPreviewRefreshingTool)?.RefreshPreview();

    /// <summary>
    /// 把契约里的采集模式翻成 WPF 的采集模式。
    /// </summary>
    /// <remarks>
    /// 擦除分支刻意取 <see cref="EraserProfile.Mode"/> 而不是写死 <c>EraseByPoint</c>：
    /// 让"擦除方式"的真相源仍然只有 <c>EraserProfile</c> 一处，
    /// 否则哪天要改成"整笔擦"，会漏掉这里而表现为"按钮没生效"。
    /// </remarks>
    private InkCanvasEditingMode MapInkMode(ToolInkMode mode) => mode switch
    {
        ToolInkMode.Ink => InkCanvasEditingMode.Ink,
        ToolInkMode.EraseByPoint => _eraser.Mode,
        _ => InkCanvasEditingMode.None,
    };

    /// <summary>
    /// 当前工具是否"空闲"（既不落墨、也不需要指针）—— 也就是视口操作归用户自己的状态。
    /// </summary>
    /// <remarks>
    /// 手工具、以及"一个工具都没注册"都属于这类。它与 <c>IsManipulationEnabled</c> 的判定同构，
    /// 这不是巧合：左键拖动平移与 Manipulation 平移本就该同时开、同时关。
    /// <para>
    /// 需要指针的插件工具（直尺）下左键被工具占用：想平移请用<b>中键</b>，或先切到「手」。
    /// 这是刻意的取舍 —— 否则工具永远抢不到左键。
    /// </para>
    /// </remarks>
    private bool IsViewportIdleTool
        => _tools.ActiveTool is not { } tool || (!tool.UsesInkLayer && !tool.NeedsPointer);

    // ---------------------------------------------------------------- 工具指针转发（M7.1）

    /// <summary>
    /// 把指针事件转发给需要它的工具。
    /// </summary>
    /// <remarks>
    /// 为什么拦的是<b>宿主的隧道事件</b>：需要指针的工具一定同时 <c>UsesInkLayer=false</c>
    /// （输入不路由到墨迹层），事件源就是宿主自己，隧道阶段先经过这里，
    /// 我们最早拿到，且不受任何子元素影响。
    /// <para>
    /// <b>绝不设 <c>e.Handled</c></b> —— M4.2 的教训：设了会打断输入序列、泄漏输入捕获，
    /// 把后续所有触摸都锁死。
    /// </para>
    /// </remarks>
    protected override void OnPreviewStylusDown(StylusDownEventArgs e)
    {
        base.OnPreviewStylusDown(e);
        ForwardStylus(ToolPointerPhase.Down, e);
    }

    protected override void OnPreviewStylusMove(StylusEventArgs e)
    {
        base.OnPreviewStylusMove(e);
        ForwardStylus(ToolPointerPhase.Move, e);
    }

    protected override void OnPreviewStylusUp(StylusEventArgs e)
    {
        base.OnPreviewStylusUp(e);
        ForwardStylus(ToolPointerPhase.Up, e);
    }

    /// <summary>Stylus 链路的转发：设备 Id 与反转标志从 WPF 设备对象上取。</summary>
    private void ForwardStylus(ToolPointerPhase phase, StylusEventArgs e)
    {
        var device = e.StylusDevice;
        if (device is null) return;

        ForwardToTool(phase, e.GetPosition(this), device.Id, StylusInputInfo.From(device).Inverted, clickCount: 1);
    }

    /// <summary>鼠标链路的转发（id 恒为 0、无反转）。</summary>
    private void ForwardMouse(ToolPointerPhase phase, MouseEventArgs e)
        => ForwardToTool(phase, e.GetPosition(this), deviceId: 0, inverted: false,
                         e is MouseButtonEventArgs button ? button.ClickCount : 1);

    /// <summary>
    /// 把一次指针事件翻成 <see cref="ToolPointer"/> 交给当前工具。
    /// </summary>
    /// <param name="phase">阶段。</param>
    /// <param name="viewport">视口坐标（宿主坐标系）。</param>
    /// <param name="deviceId">触点 Id；鼠标为 0。</param>
    /// <param name="inverted">是否笔尾反转。</param>
    /// <param name="clickCount">连击次数。</param>
    private void ForwardToTool(ToolPointerPhase phase, Point viewport, int deviceId, bool inverted, int clickCount)
    {
        var tool = _tools.ActiveTool;
        if (tool is null || !tool.NeedsPointer) return;

        // 同一次触摸会被 WPF 提升成多条链路（Stylus / Mouse），每条都带自己的 Down/Up。
        // 只认第一条链路的开始，其余忽略 —— 否则工具会收到"两次开始、两个起点"，
        // 或者 Up 比 Down 多一次（拖动状态从此错位）。
        switch (phase)
        {
            case ToolPointerPhase.Down:
                if (_toolPointerActive) return;
                _toolPointerActive = true;
                break;

            case ToolPointerPhase.Move:
                if (!_toolPointerActive) return;   // 没按下时的悬停移动，工具不需要
                break;

            default:
                if (!_toolPointerActive) return;
                _toolPointerActive = false;
                break;
        }

        // 坐标换算在这里一次做对：工具拿到的一律是世界坐标 ⇒
        // "同一道题在不同缩放下坐标相同"由宿主保证，工具不必操心（也不会操心错）。
        _tools.DispatchPointer(new ToolPointer(
            phase,
            _viewport.ToWorld(viewport),
            viewport,
            deviceId,
            inverted,
            clickCount));
    }

    /// <summary>
    /// 验收用：按"视口坐标"喂一次指针事件进真实转发链（含去重与坐标换算）。
    /// </summary>
    /// <remarks>
    /// 刻意不绕过 <see cref="ForwardToTool"/> 直接调工具：真正会在一体机上出问题的是
    /// "视口坐标是否只换算一次""同一次触摸被提升成多条链路时是否只认第一条"，
    /// 绕过去就等于把这两条最难的断言换成了"直接调工具一定对"。
    /// </remarks>
    public void SimulatePointer(ToolPointerPhase phase, Point viewport)
        => ForwardToTool(phase, viewport, deviceId: 0, inverted: false, clickCount: 1);

    /// <summary>视口发生变化（缩放/平移/适配）。视图侧据此刷新缩放百分比之类的状态显示。</summary>
    public event EventHandler? ViewportChanged;

    /// <summary>是否处于「跟随窗口宽度自动适配」模式。</summary>
    public bool IsAutoFitWidth => _autoFitWidth;

    /// <summary>
    /// 装载（或清空）文档：重建排版 → 建页视觉 → 按宽度适配 → 排渲染需求。
    /// </summary>
    /// <remarks>
    /// 本方法<b>不负责释放传入的文档服务</b>——服务由 MainViewModel 持有并复用，
    /// 切文档时它的 <c>Open()</c> 会自行关闭旧文档。宿主只借用，不管生命周期。
    /// </remarks>
    public void SetDocument(IPdfDocumentService? document)
    {
        _document = document;

        var scheduler = EnsureScheduler();
        scheduler.SetDocument(document);

        PdfPageLayer.Children.Clear();
        _pageImages.Clear();
        _displayedLevels.Clear();

        // 换文档也要清预览与指针状态：工具拖到一半时切文档，
        // 那半根预览线属于上一份试卷，留在新试卷上就成了鬼影。
        ClearPreview();
        _toolPointerActive = false;

        // 换文档必须清笔迹：否则上一份试卷的批注会残留在新试卷上。
        // M6 起批注会落盘，新文档的批注由视图层在 SetDocument 之后调
        // ReplaceStrokes 装回来 —— 所以这里的清空是"清干净再装"，不是丢数据。
        InkLayer.Strokes.Clear();

        // M12：旁路集合与图层显隐状态同属"上一份试卷"，一并清掉
        _hiddenInkStrokes.Clear();
        _hiddenInkLayerIds.Clear();

        // 清历史必须排在清笔迹**之后**：清空本身会入栈一条记录，
        // 若先 Reset 再 Clear，那条记录会留下来 —— 于是按一下 Ctrl+Z
        // 就能把上一份试卷的批注"复活"到新试卷上，看起来像撤销出了鬼影。
        _history.Reset();

        // 图形对象与笔迹同理：它们也是"属于上一份试卷的批注"，
        // 而且比笔迹更显眼 —— 留着的话新试卷上会浮着上一份的坐标系与函数曲线，
        // 还能被选中、被拖动（"这是谁画的？"）。
        // 时间线不用手清：_history.Reset() 已经通过 Resetted 事件让 BoardHistory 自己清掉了。
        _gfxStore.Clear();
        _gfxHistory.Reset();

        // 位图与图形对象同寿命：它们是"上一份试卷的批注"的一部分，
        // 而且不随撤销栈回收（见 WebPanelImageStore 的说明），所以换文档是唯一的释放点。
        _webPanelImages.Clear();

        // 换文档必须重置视口：FitToWidth 的竖直方向是"保持当前视口中心"，
        // 若沿用上一份文档的视口（可能停在第 5 页），新试卷打开后也会落在中部。
        // 重置到世界原点 + 随后的 FitWidth 夹紧 ⇒ 新试卷必定从第一页顶部开始。
        _viewport.Reset();

        if (document is null || !document.IsOpen || document.PageCount == 0)
        {
            _layout.Rebuild(Array.Empty<Size>());
            UpdateInkSurface();
            return;
        }

        // 1) 取各页尺寸（world 单位）并重建排版
        var sizes = new List<Size>(document.PageCount);
        for (int i = 0; i < document.PageCount; i++)
        {
            sizes.Add(document.GetPageSize(i));
        }
        _layout.Rebuild(sizes);

        // 1.5) 按新的排版结果摆放墨迹层（尺寸随文档变化，原点恒为世界原点）
        UpdateInkSurface();

        // 2) 建页视觉：白色纸面矩形 + 位图 Image。
        //    两者宽高都用世界单位，缩放完全交给 WorldTransform，位图长宽比与页面一致故不会变形。
        for (int i = 0; i < _layout.PageRects.Count; i++)
        {
            var rect = _layout.PageRects[i];

            var paper = new Rectangle
            {
                Width = rect.Width,
                Height = rect.Height,
                Fill = _paperBrush,   // M13 S2：共享可变画刷，换纸张色一处生效
                Stroke = Tokens.Brush("Canvas.PageEdge"),
                StrokeThickness = 0.6,
            };
            Canvas.SetLeft(paper, rect.X);
            Canvas.SetTop(paper, rect.Y);
            PdfPageLayer.Children.Add(paper);

            var image = new Image
            {
                Width = rect.Width,
                Height = rect.Height,
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(image, rect.X);
            Canvas.SetTop(image, rect.Y);
            PdfPageLayer.Children.Add(image);

            _pageImages.Add(image);
            _displayedLevels.Add(-1);
        }

        // 3) 适配 + 渲染。ActualWidth 为 0 说明还没完成首次布局，
        //    这时交给 SizeChanged 去补，避免算出 NaN 缩放。
        FitWidth();
    }

    /// <summary>按世界包围盒的宽度铺满视口宽度，并回到「跟随窗口」的自动模式。</summary>
    public void FitWidth()
    {
        // FitWidth 刻意不触发 ViewportMutated，所以这里显式回到自动模式
        _gestures.FitWidth(SidePadding);
        _autoFitWidth = true;
    }

    /// <summary>
    /// 复原工程文件里保存的视口（M8）。返回 <c>true</c> 表示按存档复原成功，
    /// <c>false</c> 表示存档视口指向页面之外、已兜底改成「适应宽度」。
    /// </summary>
    /// <remarks>
    /// 两件事必须在这里做，放在窗口侧做都来不及：
    /// <list type="number">
    /// <item>
    /// <b>复原必须退出「自动适应宽度」。</b>存档里的位置是老师的意图（“我停在第五题那个缩放上”），
    /// 而自动模式会在下一次窗口尺寸变化时把它整个算掉 —— 表现成“打开工程永远回到第 1 页顶部”。
    /// <see cref="CanvasViewport.SetView"/> 不会触发 <c>ViewportMutated</c>，所以只能显式关掉。
    /// </item>
    /// <item>
    /// <b>存档视口可能落在页面之外的空白里。</b>排版把页面放在 (960,960) 起、四周留 960pt 留白，
    /// 于是 100%@(0,0) 这种存档看到的是<b>一片空白</b>（旧档、换过窗口的档、坏档都会这样）。
    /// 白屏在老师眼里等于“文件没打开”，一律兜底回适应宽度。
    /// </item>
    /// </list>
    /// </remarks>
    public bool RestoreView(double scale, double offsetX, double offsetY)
    {
        _viewport.SetView(scale, offsetX, offsetY);

        // 复原 = 用户意图：此后窗口尺寸怎么变都不许再“纠正”它
        _autoFitWidth = false;

        if (ProjectComposer.IsViewUsable(scale, offsetX, offsetY,
                                         new Size(ActualWidth, ActualHeight), _layout.WorldBounds))
        {
            return true;
        }

        AppLog.Info($"[视图] 存档视口 {scale:F2}@({offsetX:F0},{offsetY:F0}) 看不到任何页面，"
                    + "已退回「适应宽度」");

        // 兜底：适应宽度（它会把 _autoFitWidth 重新置回 true）
        FitWidth();
        return false;
    }

    /// <summary>
    /// 某页当前显示的位图档位；<c>-1</c> 表示这页没有位图。
    /// </summary>
    /// <remarks>
    /// 只为验收 harness 而开：<see cref="OverridePageImage"/> 的"还原"在界面上<b>看不出来</b>
    /// （还原对了与没还原，屏幕上都长一个样），只有把这个数拿出来才能断言。
    /// 与 <c>StrokeCount</c> / <c>GfxObjectCount</c> 是同一类"为可断言而暴露"的只读口。
    /// </remarks>
    public int DisplayedLevelOf(int pageIndex)
        => pageIndex >= 0 && pageIndex < _displayedLevels.Count ? _displayedLevels[pageIndex] : -1;

    /// <summary>
    /// 离屏渲染源（导出用）：屏幕上那条渲染链的根。
    /// </summary>
    /// <remarks>
    /// 导出与屏幕<b>共用这一棵视觉树</b>，不另搭离屏树（理由见 <c>PageRasterizer</c> 的注释）。
    /// <para>
    /// 刻意返回 <see cref="Visual"/> 而不是 <c>Canvas</c>：导出层只该把它当"渲染源"用。
    /// 交出去一个 Canvas，就等于交出了"改它子元素"的能力 —— 那正是两处渲染开始分叉的起点。
    /// </para>
    /// </remarks>
    public Visual RenderSurface => WorldHost;

    /// <summary>
    /// 临时把某页的底图换成指定图像（导出时换成按导出 dpi 渲的那一份）。释放返回值即还原。
    /// </summary>
    /// <remarks>
    /// ★ 为什么需要它：屏幕上每页用哪个<b>档位</b>的位图由当前缩放决定
    /// （<c>UpdateRenderDemand</c> 里的 <c>RenderLevels.ForScale(视口.Scale)</c>），
    /// 而导出固定要 200 dpi。老师缩到 25% 通览整卷时页面位图只有 0.25 档，
    /// 拿它去铺 200 dpi 等于放大 8 倍 —— 糊，而且是"缩着看时才糊"的偶发症状。
    /// <para>
    /// 借用期间宿主处于渲染冻结中，但<b>后台渲染线程可能还在跑更早排下的需求</b>：
    /// 它回调 <c>OnPageRendered</c> 时会比 <c>_displayedLevels</c> 决定换不换 ——
    /// 所以这里把该页显示档位顶到最高，让那些迟到的结果<b>顶不掉我们的高清底图</b>。
    /// 不顶这一下，症状是"导出图偶尔糊"，比稳定糊难查得多。
    /// </para>
    /// </remarks>
    /// <param name="pageIndex">页号（从 0 起）。</param>
    /// <param name="image">替换用的底图，不允许 <c>null</c>。</param>
    public IDisposable OverridePageImage(int pageIndex, ImageSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (pageIndex < 0 || pageIndex >= _pageImages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                $"页号超出范围（当前文档共 {_pageImages.Count} 页）");
        }

        // ★ 先记下原状再改：token 构造完成后才动 Source/档位，
        //   这样"记"与"改"之间不会插进任何可能抛异常的表达式（抛了就把原状记丢了）。
        var token = new PageImageOverride(this, pageIndex,
                                          _pageImages[pageIndex].Source,
                                          _displayedLevels[pageIndex]);

        _pageImages[pageIndex].Source = image;
        _displayedLevels[pageIndex] = RenderLevels.MaxIndex;

        return token;
    }

    /// <summary>底图替换令牌：释放时把原底图与原档位还回去。</summary>
    private sealed class PageImageOverride : IDisposable
    {
        private readonly CanvasViewportHost _host;
        private readonly int _pageIndex;
        private readonly ImageSource? _oldSource;
        private readonly int _oldLevel;
        private bool _disposed;

        public PageImageOverride(CanvasViewportHost host, int pageIndex,
                                 ImageSource? oldSource, int oldLevel)
        {
            _host = host;
            _pageIndex = pageIndex;
            _oldSource = oldSource;
            _oldLevel = oldLevel;
        }

        public void Dispose()
        {
            // 幂等：重复 Dispose 不该把"借用后又被别人改过"的状态覆盖回去
            if (_disposed) return;
            _disposed = true;

            _host._pageImages[_pageIndex].Source = _oldSource;
            _host._displayedLevels[_pageIndex] = _oldLevel;
        }
    }

    /// <summary>
    /// 冻结「视口 → 渲染需求」这条链路（导出期间用）。释放返回值即解冻，并补排一次需求。
    /// </summary>
    /// <remarks>
    /// ★ 为什么必须有它：导出要临时把视口摆成 1:1 供离屏渲染，而视口一变，
    /// <see cref="UpdateRenderDemand"/> 就会按新视口重算 —— 它会把<b>视口外</b>的页
    /// <c>Source</c> 清成 <c>null</c> 省内存。导出用的 1:1 视口只看得见目标页，
    /// 于是其余各页（甚至目标页自己，若它还没与那个视口相交）在导出那一刻正好是被清空的状态，
    /// 渲染出来就是一张白纸。
    /// <para>
    /// 它排在 <c>Background</c> 优先级、平时肉眼看不见（下一帧就补回来），
    /// 所以不冻结的话症状是<b>"偶尔导出成白纸"</b> —— 比稳定失败难查得多，
    /// 而且本机的时序往往碰不出来。宁可多这一道开关。
    /// </para>
    /// <para>
    /// 冻结只挡渲染需求，<b>不挡视口本身</b>：<c>WorldTransform</c> 照常更新 ——
    /// 离屏渲染要的正是那个变换。
    /// </para>
    /// </remarks>
    public IDisposable BeginRenderFreeze()
    {
        _renderFreezeDepth++;
        return new RenderFreezeToken(this);
    }

    /// <summary>冻结令牌：释放时减计数，减到 0 才真正解冻并补排一次渲染需求。</summary>
    private sealed class RenderFreezeToken : IDisposable
    {
        private readonly CanvasViewportHost _host;
        private bool _disposed;

        public RenderFreezeToken(CanvasViewportHost host) => _host = host;

        public void Dispose()
        {
            // 幂等：重复 Dispose 不该把计数多减一次（那会让别的冻结提前失效）
            if (_disposed) return;
            _disposed = true;

            if (--_host._renderFreezeDepth > 0) return;

            _host._renderFreezeDepth = 0;

            // 冻结期间视口多半被改过又还原了，解冻后按当前视口补排一次，
            // 否则画面会停在冻结前的档位上，直到老师下一次缩放才恢复。
            _host.QueueRenderUpdate();
        }
    }

    /// <summary>缩放到 100%（1 world 单位 = 1 DIP）。</summary>
    public void ActualSize() => _gestures.SetScale(1.0);

    public void ZoomIn() => _gestures.ZoomIn();

    public void ZoomOut() => _gestures.ZoomOut();

    /// <summary>释放后台渲染线程。窗口关闭时调用。</summary>
    public void Shutdown()
    {
        // 关窗口是最后一次写诊断汇总的机会。一次会话里"落笔数 vs 抬笔数"、
        // "触摸按下 vs 抬起"是否配对，只有汇总出来才看得出来 ——
        // 不配对就是"输入序列被截断"的直接证据。
        // M24 热修③：把笔线程的过滤计数（断笔/坏点/第二触点）一并并进汇总。
        var filterCounters = InkLayer.Sampler.GetFilterCounters();
        _diag.NoteSamplerFilterCounters(
            filterCounters.JumpBreaks, filterCounters.BadPointDrops,
            filterCounters.ForeignContacts, filterCounters.ForeignPackets);
        _diag.FlushSummary();

        ClearPreview();

        _scheduler?.Dispose();
        _scheduler = null;

        // 演示面板（M7.5）：WebView2 常驻内存几百 MB，关程序时必须真释放，
        // 不能只是"窗口没了"。
        if (_geoGebra is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"释放演示面板失败：{ex.GetType().Name} {ex.Message}");
            }
        }

        _geoGebra = null;
    }

    // ---------------------------------------------------------------- 墨迹层

    /// <summary>
    /// 按排版结果摆放墨迹层：位置恒为 <c>(0,0)</c>、尺寸 = <see cref="WorldLayout.InkExtent"/>。
    /// </summary>
    /// <remarks>
    /// 位置写死 0 是<b>有意</b>的：`InkExtent` 的原点由 `WorldLayout` 保证为 0
    /// （页面从 `(PageMargin,PageMargin)` 起排，正好把它抵消掉）。
    /// 这里不用 `extent.X/Y` 当 Left/Top —— 那样等于把"补偿"偷偷塞回来，
    /// 一旦哪天留白逻辑变了，本地坐标就不等于世界坐标了，而且没人会发现。
    /// 所以改为：原点不是 0 就<b>报警</b>，把隐患变成看得见的信号。
    /// </remarks>
    private void UpdateInkSurface()
    {
        var extent = _layout.InkExtent;

        if (extent.IsEmpty || extent.Width <= 0 || extent.Height <= 0)
        {
            InkLayer.Width = 0;
            InkLayer.Height = 0;
            Canvas.SetLeft(InkLayer, 0);
            Canvas.SetTop(InkLayer, 0);
            return;
        }

        if (Math.Abs(extent.X) > 1e-6 || Math.Abs(extent.Y) > 1e-6)
        {
            AppLog.Warn($"墨迹层原点偏了：InkExtent=({extent.X:F3},{extent.Y:F3}) 应为 (0,0)。"
                        + "墨迹层本地坐标将不再等于世界坐标，笔迹会整体偏移。");
        }

        Canvas.SetLeft(InkLayer, 0);
        Canvas.SetTop(InkLayer, 0);
        InkLayer.Width = extent.Width;
        InkLayer.Height = extent.Height;
    }

    // ---------------------------------------------------------------- 笔输入分工

    private void OnInkPreviewStylusDown(object sender, StylusDownEventArgs e)
    {
        // 只有真会落到纸上的输入才开事务。手工具下不会产生任何墨迹变更，
        // 给它们开事务只会白白提交一个空单元。
        if (ObserveStylusInput("down", e) != InkInputAction.Suppress) _history.BeginTransaction();
    }

    private void OnInkPreviewStylusMove(object sender, StylusEventArgs e)
        => ObserveStylusInput("move", e);

    private void OnInkPreviewStylusUp(object sender, StylusEventArgs e)
    {
        ObserveStylusInput("up", e);

        // 无论本次是否落墨都要提交：Down 没开事务时提交是空操作；
        // 但若漏掉这一步，事务会一直悬着，把**下一次**操作并进同一个撤销单元 ——
        // 表现为"按一下 Ctrl+Z 撤销掉了两笔"，且极难定位。
        _history.CommitTransaction();

        // ★ M11 S3 的尺子：量一次「抬笔 → UI 线程把这一笔处理完」用了多久。
        //   只测量、不改变任何行为。本机没有笔，书写延迟测不出来；但"UI 线程每一笔要忙多久"
        //   在一体机上跑一遍就有答案，而这个数正是"抬笔顿一下"的直接证据。
        //   排在 CommitTransaction 之后，量到的就是"收尾工作全做完"的时刻。
        MeasureUiSettle();
    }

    /// <summary>
    /// 量一次 UI 线程收尾耗时（M11 S3，只测不改）。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="DispatcherPriority.Background"/> 排队：它排在输入与渲染之后，
    /// 回调真正被执行的时刻就近似等于"UI 线程终于闲下来"的时刻。
    /// <para>
    /// ★ 必须是 <c>BeginInvoke</c>（异步）：这里就在 UI 线程上，同步调用会立即执行、
    /// 永远量到 0 —— 那不是测量，是自欺。
    /// </para>
    /// </remarks>
    private void MeasureUiSettle()
    {
        var watch = Stopwatch.StartNew();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            watch.Stop();
            _diag.NoteUiSettleMs((int)watch.ElapsedMilliseconds);
        }));
    }

    /// <summary>
    /// 观测一次笔输入：记诊断、算出它该被当作什么，然后<b>原样放行</b>。
    /// </summary>
    /// <remarks>
    /// <b>M4.2 的核心改动：这里不再设 <c>e.Handled = true</c>。</b>
    /// <para>
    /// M3/M4 曾经用 <c>Handled</c> 把"手指 / 手掌"的输入掐掉，理由是"从源头阻止落墨"。
    /// 一体机实测证明这个做法是坏的，而且坏得很隐蔽：
    /// </para>
    /// <list type="number">
    /// <item><c>Handled</c> 只挡得住 UI 线程上的正式采集，<b>挡不住跑在笔输入线程上的
    /// <c>DynamicRenderer</c></b>。于是实时墨迹照画（看得见），抬笔时因为笔画从未提交，
    /// 那层临时墨迹被清掉 —— 表现就是"写上去的字，笔一离开屏幕就消失"。</item>
    /// <item>采集被打断会把输入捕获留在半途状态。红外屏上<b>所有触点共用同一个设备</b>，
    /// 捕获一旦没释放，后续每一次触摸都直送墨迹层 —— 所有按钮都点不动，
    /// 只有退出进程才能解。</item>
    /// </list>
    /// <para>
    /// 所以"不要落墨"改用<b>确定有效且无副作用</b>的手段：<c>IsHitTestVisible=false</c>
    /// （见 <see cref="ApplyTool"/>），输入根本不路由到墨迹层，连实时层都不会启动。
    /// </para>
    /// </remarks>
    private InkInputAction ObserveStylusInput(string phase, StylusEventArgs e)
    {
        var device = e.StylusDevice;

        // 取不到设备信息时保守处理：不给它开撤销事务（它也不可能产生真正的变更）
        if (device is null)
        {
            _diag.Count($"stylus.{phase}.nodevice");
            return InkInputAction.Suppress;
        }

        // 全工程唯一一处把 WPF 设备对象翻成策略输入的地方（M4.1 的笔尾反转兜底替换点）
        var info = StylusInputInfo.From(device);
        var action = ClassifyStylusInput(info);

        _diag.NoteStylus(phase, info.DeviceType, info.Inverted, info.Name, DescribeAction(action));

        // Draw 与 Erase 在这里的处置**完全一样**：都放行。
        // 擦除不是我们执行的动作 —— 笔尾反转走 InkCanvas 的 EditingModeInverted，
        // 橡皮工具走 EditingMode=EraseByPoint。返回值只用于两件事：
        // 调用方据此决定要不要开撤销事务，以及日志里写明"这次输入不落墨"。
        return action;
    }

    /// <summary>
    /// 分类一次笔输入。
    /// </summary>
    /// <remarks>
    /// 做成 <c>public</c> 是为了让验收 harness 能把<b>一体机上才会发生的组合</b>
    /// （笔尾反转、手工具下写字）喂进来，验证"宿主 → 策略"这条接线本身，
    /// 而不只是验证策略函数算得对。
    /// </remarks>
    public InkInputAction ClassifyStylusInput(StylusInputInfo info)
        => InkInputPolicy.Decide(_tools.ActiveTool?.InputKind ?? ToolInputKind.None, info.Inverted);

    private static string DescribeAction(InkInputAction action) => action switch
    {
        InkInputAction.Draw => "落墨",
        InkInputAction.Erase => "擦除",
        _ => "不落墨",
    };

    /// <summary>
    /// 抬笔后校正压感：设备若不报压力，把这根笔画按"无压感"重设属性。
    /// </summary>
    /// <remarks>
    /// 为什么只能放在抬笔时：压力到底存不存在，要拿到整串采样点才能判断，落笔那一刻看不出来。
    /// <para>
    /// 这里<b>只改 <c>DrawingAttributes</c></b>（一次属性赋值），绝不碰 <c>StylusPoints</c>，
    /// 所以"笔迹几何永不重算"这条不变量依然成立。
    /// </para>
    /// <para>
    /// 视觉代价：无压感设备上会看到笔迹在抬笔瞬间由细变粗。M4 默认关闭压感，
    /// 就是为了让这件事只在用户<b>主动打开压感之后</b>才有可能发生。
    /// </para>
    /// <para>
    /// ★ <b>M11 笔锋的接线</b>：笔锋开启时，我们伪造的 <c>PressureFactor</c> 已由插件
    /// <b>写回了采样点</b>，所以 <c>e.Stroke.StylusPoints</c> 里看到的就是<b>伪造后的那一批</b> ——
    /// 这里一行都不用改，它天然就是在判断"我们真正要画的那一笔"。语义也正好对：
    /// 伪造出来的因子<b>恒定</b>时（例如一个孤零零的墨点）笔锋本就无从体现，
    /// 退化成恒定笔宽是合理的。
    /// </para>
    /// </remarks>
    private void OnStrokeCollected(object? sender, InkCanvasStrokeCollectedEventArgs e)
    {
        // M18：断点检测判定本笔混入手掌瞬移 / Down 丢失 ⇒ 整笔丢弃。
        // ★ M24 热修⑤（2026-09-28）修正旧注释的错误假设：旧注释以为"移除会落进加入那一步的
        //   同一个事务 ⇒ add+remove 净空、不会留鬼影"。实测（探针）不是这样：那一步照样进了
        //   撤销栈，老师一按 Ctrl+Z 就把这条长线整笔复活成一条谁也没见过的鬼影。
        //   所以这里必须做两件事：① 这次移除本身静默（不留记录）；② 把"加入本笔"那一步
        //   从历史里抹掉（DiscardAdded 同时兼顾"还在事务里"与"已经入栈"两种时机）。
        if (InkLayer.Sampler.ConsumeJumpDetected())
        {
            _history.Silently(() => Strokes.Remove(e.Stroke));
            _history.DiscardAdded(e.Stroke);

            _diag.Count("stroke.jumpDiscarded");
            AppLog.Warn("已丢弃一笔：检测到手掌并入/Down 丢失（防止出现横贯直线）。");
            return;
        }

        // 这条计数为 0 就说明"看得见的墨，一笔都没真正提交" ——
        // 一体机那台"写上去、笔一离开就消失"的现象，在日志里就是长这样的。
        _diag.NoteStrokeCollected();

        if (_pen.IgnorePressure) return;
        if (!PressurePolicy.IsPressureConstant(e.Stroke.StylusPoints)) return;

        var attributes = e.Stroke.DrawingAttributes.Clone();
        attributes.IgnorePressure = true;
        e.Stroke.DrawingAttributes = attributes;
    }

    // ---------------------------------------------------------------- 视口 → 渲染

    private void OnViewportChanged(object? sender, EventArgs e)
    {
        WorldTransform.Matrix = _viewport.WorldToViewport;
        QueueRenderUpdate();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 把本帧内的多次视口变化合并成一次渲染需求重算。
    /// </summary>
    /// <remarks>
    /// 一次捏合会连续产生多个 <c>Changed</c>，而重算需求要遍历所有页并查缓存，
    /// 每个都算一遍纯属浪费。合并到 Background 优先级，等于"本帧只算一次"。
    /// </remarks>
    private void QueueRenderUpdate()
    {
        // 冻结期间不排重算：导出正把视口摆成 1:1，按那个视口算出来的需求会把页位图清掉
        // （整条道理见 BeginRenderFreeze 的注释）
        if (_renderFreezeDepth > 0) return;
        if (_renderUpdateQueued) return;
        _renderUpdateQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _renderUpdateQueued = false;
            UpdateRenderDemand();
        }));
    }

    /// <summary>
    /// 重算「哪些页需要什么档位」，并立即用缓存把画面顶住。
    /// </summary>
    private void UpdateRenderDemand()
    {
        // 已排队的重算可能在冻结生效之后才落地 —— 这里再挡一道，
        // 否则导出照样会被"视口外的页清位图"清成白纸
        if (_renderFreezeDepth > 0) return;

        if (_document is null || !_document.IsOpen) return;
        if (_pageImages.Count == 0) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var scheduler = EnsureScheduler();

        int targetLevel = RenderLevels.ForScale(_viewport.Scale);
        var visible = GetVisibleWorldRect();
        var center = _viewport.ToWorld(new Point(ActualWidth / 2, ActualHeight / 2));

        var ordered = new List<(int Page, double Distance)>(_pageImages.Count);

        for (int i = 0; i < _pageImages.Count; i++)
        {
            var rect = _layout.GetPageRect(i);

            if (!rect.IntersectsWith(visible))
            {
                // 视口外的页清掉位图省内存。白纸底矩形仍在，所以滚回来看到的是纸而不是空洞。
                _pageImages[i].Source = null;
                _displayedLevels[i] = -1;
                continue;
            }

            // 关键：先用缓存里最接近的档位把画面顶住，绝不置 null —— 置 null 会在缩放过程中反复闪白。
            if (scheduler.Cache.TryGetClosest(i, targetLevel, out int cachedLevel, out var bitmap)
                && bitmap is not null)
            {
                if (cachedLevel > _displayedLevels[i])
                {
                    _pageImages[i].Source = bitmap;
                    _displayedLevels[i] = cachedLevel;
                }

                // 已经是目标档位，无需后台再渲
                if (cachedLevel == targetLevel) continue;
            }

            ordered.Add((i, DistanceTo(rect, center)));
        }

        // 按"离视口中心近"排序后整体替换队列：最可能被看到的页先渲
        ordered.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        var demand = new List<PageRenderScheduler.Request>(ordered.Count);
        foreach (var (page, _) in ordered)
        {
            demand.Add(new PageRenderScheduler.Request(page, targetLevel));
        }

        scheduler.UpdateDemand(demand);

        if (RenderTrace)
        {
            AppLog.Info($"[渲染] 需求 档位={targetLevel} 视口={_viewport.Scale:F2}"
                        + $"@({_viewport.OffsetX:F0},{_viewport.OffsetY:F0})"
                        + $" 可见={visible.Width:F0}x{visible.Height:F0}"
                        + $" 待渲={demand.Count} 页[{string.Join(",", demand.Select(d => d.PageIndex + 1))}]");
        }
    }

    /// <summary>后台渲染完成（回调已切到 UI 线程）。</summary>
    private void OnPageRendered(object? sender, PageRenderedEventArgs e)
    {
        if (e.PageIndex < 0 || e.PageIndex >= _pageImages.Count) return;

        // 不比当前显示的更清晰就别换 —— 换了反而是降质
        if (e.LevelIndex <= _displayedLevels[e.PageIndex]) return;

        // 页已经移出视口就别装了（下次进来会重新从缓存取）
        if (!_layout.GetPageRect(e.PageIndex).IntersectsWith(GetVisibleWorldRect()))
        {
            if (RenderTrace) AppLog.Info($"[渲染] 丢弃 第{e.PageIndex + 1}页 档位{e.LevelIndex}（已移出视口）");
            return;
        }

        if (RenderTrace)
        {
            AppLog.Info($"[渲染] 完成 第{e.PageIndex + 1}页 档位{e.LevelIndex}"
                        + $"（当前显示档 {_displayedLevels[e.PageIndex]}）");
        }

        _pageImages[e.PageIndex].Source = e.Bitmap;
        _displayedLevels[e.PageIndex] = e.LevelIndex;
    }

    /// <summary>当前视口对应的世界矩形（含预渲染余量）。</summary>
    private Rect GetVisibleWorldRect()
    {
        var topLeft = _viewport.ToWorld(new Point(0, 0));
        var bottomRight = _viewport.ToWorld(new Point(ActualWidth, ActualHeight));

        var rect = new Rect(topLeft, bottomRight);
        rect.Inflate(PrerenderMargin, PrerenderMargin);
        return rect;
    }

    private static double DistanceTo(Rect rect, Point from)
    {
        double dx = rect.X + rect.Width / 2.0 - from.X;
        double dy = rect.Y + rect.Height / 2.0 - from.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// 渲染追踪开关（环境变量 <c>TB_RENDER_TRACE=1</c>）。
    /// </summary>
    /// <remarks>
    /// 排查"打开之后画面是空的"这类问题时，只在日志里说"渲染了没有、为什么被丢弃"
    /// 是最省事的办法 —— 这类问题在界面上看不出任何差别，都是白画布。
    /// </remarks>
    private static readonly bool RenderTrace =
        Environment.GetEnvironmentVariable("TB_RENDER_TRACE") == "1";

    private PageRenderScheduler EnsureScheduler()
    {
        if (_scheduler is null)
        {
            _scheduler = new PageRenderScheduler(Dispatcher);
            _scheduler.PageRendered += OnPageRendered;
        }

        return _scheduler;
    }

    // ---------------------------------------------------------------- 扫描档 DPI

    // ---------------------------------------------------------------- 尺寸

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 只有"自动适配"模式才让窗口尺寸去改视口
        if (_autoFitWidth) _gestures.FitWidth(SidePadding);

        // 但无论哪种模式，可见世界范围都变了 ⇒ 渲染需求必须重算
        QueueRenderUpdate();
    }

    // ---------------------------------------------------------------- 鼠标输入

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!CanInteract() || _activeTouches.Count > 0) return;

        _gestures.WheelZoom(e.GetPosition(this), e.Delta);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        // 一体机上值得留意的信号：鼠标事件数远多于实际鼠标操作数 ⇒ 触摸被提升成了鼠标事件。
        _diag.NoteMouse("down", e.ChangedButton.ToString());

        // 输入分工（M3 起，M7.1 起由工具声明推出）：
        //   中键     —— 恒为平移，与工具无关
        //   左键     —— "空闲"工具（手）下平移；笔/橡皮下交给墨迹层写字；
        //               需要指针的插件工具（直尺）下交给工具自己用（所以不在这里平移）
        //   单指拖动 —— 平移（走 Manipulation）
        //   双指捏合 —— 缩放（走 Manipulation）
        // 既然墨迹层可能把左键事件消费掉，这里就<b>不去看"事件是否已被处理"</b> ——
        // 那是别人的实现细节，靠它迟早踩空。
        bool panButton = e.ChangedButton == MouseButton.Middle
                      || (e.ChangedButton == MouseButton.Left && IsViewportIdleTool);

        if (panButton && CanInteract() && _activeTouches.Count == 0)
        {
            _panning = true;
            _panLast = e.GetPosition(this);
            CaptureMouse();
            Cursor = Cursors.Hand;
            e.Handled = true;
        }

        // 需要指针的工具（直尺）也要能从鼠标链路拿到事件。鼠标输入本来会被 WPF 提升成 Stylus
        // （那条链路已在 OnPreviewStylus* 里转发），但这条提升链在不同驱动/系统版本下未必一致，
        // 所以补一条，并用同一闸门去重 —— 重复到达的那一份会被忽略。
        if (e.ChangedButton == MouseButton.Left && !_panning) ForwardMouse(ToolPointerPhase.Down, e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panning)
        {
            var current = e.GetPosition(this);
            _gestures.PanBy(current.X - _panLast.X, current.Y - _panLast.Y);
            _panLast = current;
            e.Handled = true;
            return;
        }

        ForwardMouse(ToolPointerPhase.Move, e);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        _diag.NoteMouse("up", e.ChangedButton.ToString());

        if (_panning && e.ChangedButton is MouseButton.Middle or MouseButton.Left)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
        }

        if (e.ChangedButton == MouseButton.Left) ForwardMouse(ToolPointerPhase.Up, e);
    }

    /// <summary>
    /// 鼠标捕获意外丢失时的兜底（M4.2 新增）。
    /// </summary>
    /// <remarks>
    /// 一体机上"触摸会被提升成鼠标事件"，而那条提升链路的抬升事件未必总能到达这里。
    /// 一旦 <c>_panning</c> 卡在 true 且捕获没释放，后续每一次鼠标事件都会被本控件独占 ——
    /// 窗口还在、按钮一个都点不动，只能退进程重来。捕获被系统收走这件事本身就会触发本回调，
    /// 正好是复位的时机。
    /// </remarks>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            Cursor = Cursors.Arrow;
        }

        // 捕获被系统收走 ⇒ 这次拖动不可能再继续。必须把指针状态一并复位，
        // 否则需要指针的工具会永远停在"正在拖动"，下一次按下被当成第二次开始而忽略。
        _toolPointerActive = false;

        base.OnLostMouseCapture(e);
    }

    // ---------------------------------------------------------------- 触摸输入

    protected override void OnTouchDown(TouchEventArgs e)
    {
        // 只记触点，不改 Handled —— 处理它会干扰 WPF 的 Manipulation 子系统。
        // 屏蔽"触摸被提升成鼠标事件"改用触点计数在鼠标分支里做闸门。
        _activeTouches.Add(e.TouchDevice.Id);

        // 保险：触点表异常膨胀，说明之前有 TouchUp 没收到（一体机上指纹不清的触摸屏常见），
        // 清掉重来。残留的后果是"触摸进行中"的闸门永久关闭 ⇒ 平移与手势全部失灵。
        if (_activeTouches.Count > 8)
        {
            _diag.Count("touch.table-reset");
            _activeTouches.Clear();
            _activeTouches.Add(e.TouchDevice.Id);
        }

        // 接触面积是软件层做手掌拒绝的唯一可能依据，先记下来看这台机器给不给。
        // 注意面积挂在 TouchPoint 上而不是 TouchDevice 上（WPF 里只有前者带尺寸）。
        _diag.NoteTouch("down", e.TouchDevice.Id, e.GetTouchPoint(this).Size);

        base.OnTouchDown(e);
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        _activeTouches.Remove(e.TouchDevice.Id);
        _diag.NoteTouch("up", e.TouchDevice.Id, default);

        base.OnTouchUp(e);
    }

    /// <summary>
    /// 触点捕获意外丢失时的兜底（M4.2 新增）。
    /// </summary>
    /// <remarks>
    /// 与鼠标捕获同理：触点表一旦残留，鼠标分支里"触摸进行中就不响应鼠标"的闸门会永久关闭。
    /// 系统收回捕获时正好走这里，顺手清掉。
    /// </remarks>
    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        _activeTouches.Remove(e.TouchDevice.Id);
        base.OnLostTouchCapture(e);
    }

    /// <summary>手势收尾：把触摸与平移状态一并复位。学习课时最怕的状态就是"程序还在、操作全失灵"。</summary>
    private void ResetTouchState()
    {
        _activeTouches.Clear();

        if (_panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }
    }

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        // 把操作容器限定为本控件，否则 ManipulationOrigin 会落在别的元素的坐标系里
        e.ManipulationContainer = this;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        e.Handled = true;

        _diag.NoteManipulation("starting");

        base.OnManipulationStarting(e);
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        _diag.NoteManipulation("delta");

        if (!CanInteract())
        {
            base.OnManipulationDelta(e);
            return;
        }

        ManipulationDelta delta = e.DeltaManipulation;

        // 显式取分量而非直接内联，让编译器在这里校验字段类型。
        // 注意 ManipulationDelta.Scale 是 Vector 而不是 double（WPF 允许各向异性缩放），
        // 而我们的视口是等比缩放，捏合时 X 与 Y 相同，取 X 分量即可。
        Vector translation = delta.Translation;
        double scaleDelta = delta.Scale.X;

        // ManipulationOrigin 是手势原点（捏合中心 / 单指位置），正好是锚点缩放要的语义。
        // 单指拖动时 Scale 恒为 1，自动退化成纯平移。
        _gestures.Manipulate(e.ManipulationOrigin, translation, scaleDelta);

        e.Handled = true;
        base.OnManipulationDelta(e);
    }

    /// <summary>
    /// 手势结束：清理触点表。这是"手势卡住 ⇒ 所有按钮点不动"的第一道保险。
    /// </summary>
    protected override void OnManipulationCompleted(ManipulationCompletedEventArgs e)
    {
        _diag.NoteManipulation("completed");
        ResetTouchState();

        base.OnManipulationCompleted(e);
    }

    private bool CanInteract() => _document is not null && _document.IsOpen;
}
