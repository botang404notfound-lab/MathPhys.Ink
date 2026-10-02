using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MathPhys.Ink.Design;
using MathPhys.Ink.Views.Controls;
using MathPhys.Ink.Design.Dialogs;
using MathPhys.Ink.Export;
using MathPhys.Ink.WebPanel;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Ink;
using MathPhys.Ink.Input;
using MathPhys.Ink.Plugins;
using MathPhys.Ink.Tools;
using MathPhys.Ink.Tools.Plugins;
using MathPhys.Ink.ViewModels;
using MathPhys.Ink.Workflows;

namespace MathPhys.Ink.Views;

/// <summary>
/// 主窗口。只做三件事：把 ViewModel 接到界面、把文档交给画布宿主、把视口/工具/笔设置转发给宿主。
/// </summary>
/// <remarks>
/// 视口与工具操作刻意用 code-behind 转发而不是走 ViewModel：
/// 缩放手势、当前工具、墨色笔宽都是<b>纯视图态</b>，没有需要持久化或参与业务判断的状态，
/// 硬塞进 VM 只会制造"VM 反向依赖控件"的坏味道。
/// <para>
/// 墨色与笔宽的取值统一来自 <see cref="InkPalette"/>，按钮只是它的投影 ——
/// 这样"按钮标签"和"实际笔宽"不可能对不上。
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>工程文件的扩展名（命令行入口与对话框过滤都用它，避免两处各写一遍）。</summary>
    private const string ProjectExtension = ".twb";

    /// <summary>被选中的色块/笔宽按钮的边框色（亮），未选中用暗色。</summary>
    /// <remarks>
    /// ★ M10 起这两个不再是 <c>static readonly</c> 字段，而是**每次从设计令牌取**。
    /// 静态字段会在启动那一刻把颜色焊死在当时的色板上，换肤之后它们还是旧色
    /// （表现为"切成浅色了，可色块的选中框还是白的，看不见"）。
    /// 代价是每次取用多一次资源查找 —— 这些调用一次点击才走十几遍，不值得提前优化。
    /// </remarks>
    private static Brush SelectedBorder => Tokens.Brush("Ui.TextPrimary");
    private static Brush NormalBorder => Tokens.Brush("Ui.Divider");

    /// <summary>状态栏的常态文字色。</summary>
    private static Brush AnnotationNormalBrush => Tokens.Brush("Ui.TextMuted");

    /// <summary>「● 未保存」的提示色。</summary>
    /// <remarks>
    /// ★ M10 起与"墨橙"彻底分家（它们以前共用 #E86A0B）：
    /// 一个语义是"这支笔是橙色的"，另一个是"这份工程还没存"，
    /// 共用一个值意味着改任一方都会误伤另一方。
    /// </remarks>
    private static Brush AnnotationDirtyBrush => Tokens.Brush("Ui.StatusDirty");

    /// <summary>警告色 —— 批注与文档对不上、或保存失败。</summary>
    private static Brush AnnotationWarningBrush => Tokens.Brush("Ui.StatusWarning");

    private readonly MainViewModel _viewModel = new();

    private readonly AnnotationStore _annotationStore = new();

    /// <summary>
    /// 导出服务（M9）。只能在构造函数里建 —— 它要用 <c>ViewportHost</c>，
    /// 而那个控件是 <c>InitializeComponent()</c> 之后才存在的。
    /// </summary>
    private readonly ExportService _exporter;

    private readonly Button[] _colorButtons;
    private readonly Button[] _widthButtons;

    /// <summary>笔锋力度三档按钮（M11）。文字由 <see cref="SpeedPressureMap"/> 派生。</summary>
    private readonly Button[] _craftButtons;

    /// <summary>
    /// 当前选中的墨色 / 笔宽 / 笔锋力度档位。
    /// </summary>
    /// <remarks>
    /// 必须记下来：换主题时要把"选中边框"按新色板重画一遍，而重画需要知道选的是哪一档
    /// （边框亮度是 code-behind 亲手设的，不会跟着 DynamicResource 自己变）。
    /// </remarks>
    private int _colorIndex = InkPalette.DefaultColorIndex;
    private int _widthIndex = InkPalette.DefaultWidthIndex;

    /// <summary>笔锋力度档位（M11）。出厂默认「中」—— 与 <see cref="CraftStrength.Medium"/> 同一处取值。</summary>
    private int _craftStrengthIndex = (int)CraftStrength.Medium;

    /// <summary>平滑强度两档按钮（M18：轻 / 中。关 = 勾掉「平滑」复选框）。文字 code-behind 派生。</summary>
    private readonly Button[] _smoothButtons;

    /// <summary>平滑强度档位（M18）。出厂默认「轻」—— 与 <see cref="SmoothStrength.Light"/> 同一处取值。</summary>
    private int _smoothStrengthIndex = (int)SmoothStrength.Light;

    /// <summary>
    /// 全部工具按钮的视图模型（含折进二级菜单的族成员），内容来自工具注册表（M7.1）。
    /// </summary>
    /// <remarks>
    /// ★ M20 S4 起它<b>不再是任何 ItemsControl 的数据源</b>：瓦片由代码直接挂到组面板上
    /// （原因见 MainWindow.xaml 里关于 WrapPanel 量宽度的说明）。它现在的职责只剩
    /// "当前工具变了，把选中态刷到每一颗瓦片上"—— 包括二级菜单里那些看不见的成员。
    /// </remarks>
    private readonly ObservableCollection<ToolButtonViewModel> _toolButtons = new();

    /// <summary>工具栏自检：可见瓦片数（M20 S4，见 <see cref="ToolbarAudit"/>）。</summary>
    private int _toolTileCount;

    /// <summary>工具栏自检：折进二级菜单的成员数。</summary>
    private int _toolFoldedCount;

    /// <summary>工具栏自检：目录表里没登记、落到「其他」组的工具数（正常应为 0）。</summary>
    private int _toolUncataloguedCount;

    /// <summary>
    /// 工具栏自检真值，一行（给 <c>Design/UiShotHook</c> 写进自出图的真值文件）。
    /// </summary>
    /// <remarks>
    /// 分组 + 折叠最容易犯的错是<b>把工具弄丢</b>：折叠时算错成员数、或者目录表登记了
    /// 而插件没来。所以这里把几个数摆在一起自证：<c>tiles + folded == tools</c>。
    /// 缺了这条，"工具栏上少了一颗按钮"就只能靠人在截图里一个个数 ——
    /// 而本项目已有一个"图上没看见不等于图上没有"的教训。
    /// </remarks>
    public string ToolbarAudit =>
        $"tools = {ViewportHost.Tools.Count}  tiles = {_toolTileCount}"
        + $"  folded = {_toolFoldedCount}  other = {_toolUncataloguedCount}";

    /// <summary>自动保存调度器。构造在 <see cref="MainWindow"/> 里而不是字段初始化器：
    /// 它需要用本窗口的 <see cref="DispatcherObject.Dispatcher"/>，且保存委托要闭包到实例成员。</summary>
    private readonly AutoSaveScheduler _autoSave;

    /// <summary>
    /// 演示面板控制器（M7.5）。生命周期交给 <see cref="Views.Controls.CanvasViewportHost.Shutdown"/>
    /// 释放（WebView2 常驻内存几百 MB，必须真释放）。
    /// </summary>
    private WebPanelController? _geoGebraPanel;

    /// <summary>物理仿真 Web 面板（M22 S5）：自建导航页内嵌 CircuitJS。装配失败时为 null。</summary>
    private WebPanelController? _webSimPanel;

    /// <summary>
    /// 演示面板「导出到白板」的落地服务（M22 S4b）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="_geoGebraPanel"/> 同寿命：面板装配失败时它也是 <c>null</c>，
    /// 此时那条通路本来就不存在（没有面板可导出）。
    /// </remarks>
    private WebPanelExportService? _webPanelExport;

    /// <summary>
    /// 物理仿真面板「导出到白板」的落地服务（M22 S5）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="_webSimPanel"/> 同寿命：面板装配失败时它也是 <c>null</c>。
    /// 两个面板各有一份，抓的是各自后端的画面，落的是同一个卷面。
    /// </remarks>
    private WebPanelExportService? _webSimExport;

    /// <summary>
    /// 当前笔迹<b>归属</b>的 PDF 路径。
    /// </summary>
    /// <remarks>
    /// 必须自己记，<b>不能用 <c>_viewModel.Document.FilePath</c></b>：
    /// <c>OpenDocument</c> 是"先打开新文件、再通知界面"，<c>CloseDocument</c> 是"先关闭、再通知界面"，
    /// 所以事件到达时 Document.FilePath 已经是<b>新文档</b>或 <c>null</c> 了。
    /// 拿它去保存，会把上一份试卷的批注写进新试卷的文件里 —— 而且静默无提示，极难发现。
    /// </remarks>
    private string? _annotatedPath;

    /// <summary>工程文件（.twb）的装载与保存服务（M8）。</summary>
    private readonly ProjectStore _projectStore = new();

    /// <summary>
    /// 当前工程的界面状态（路径 / 标题 / 绑定方式 / 脏标记）。
    /// </summary>
    /// <remarks>
    /// 脏标记与 <see cref="_autoSave"/> 的脏标记是<b>两件事</b>：
    /// 后者管"批注要不要写侧车"，前者管"这份工程整体存住了没有"。
    /// 工程模式下自动保存不落盘（见 <see cref="SaveAnnotationsNow"/> 的说明），
    /// 于是"有没有未保存的改动"就只由这一个标记回答。
    /// </remarks>
    private readonly ProjectViewModel _project = new();

    /// <summary>装载工程时暂存的批注字节，等试卷装好后再灌进画布。</summary>
    private byte[]? _pendingProjectAnnotationBytes;

    /// <summary>
    /// 正在装载工程。
    /// </summary>
    /// <remarks>
    /// 装载过程会先把试卷装进画布、再灌批注，而装试卷会触发 <see cref="OnDocumentChanged"/>。
    /// 那个时机如果按老规矩去读试卷旁边的 <c>.tbink</c> 侧车，
    /// 就会出现"工程里的批注被磁盘上的旧侧车顶掉"——而且悄无声息。
    /// </remarks>
    private bool _loadingProject;

    /// <summary>
    /// 工程内嵌试卷的释放器（落临时文件 + 收尾清理）。
    /// </summary>
    /// <remarks>
    /// M9 S3 从本类里搬出去成了 <see cref="EmbeddedPdfReleaser"/> —— 理由是
    /// "从 .twb 内嵌 PDF 导出"这条路必须能被断言，而私有方法断言不到。
    /// 详见该类注释。
    /// </remarks>
    private readonly EmbeddedPdfReleaser _embeddedReleaser = new();

    /// <summary>需要以警告样式显示的说明；为空表示当前没有要提醒的事。</summary>
    private string _annotationNotice = string.Empty;

    /// <summary>没有警告、也没有未保存改动时，状态栏显示的常态文案。</summary>
    private string _annotationIdleText = "无批注";

    /// <summary>本窗口内最后一次成功保存的时刻，仅用于状态栏显示。</summary>
    private DateTime? _annotatedSavedAt;

    /// <summary>当前是否处于全屏覆盖状态。</summary>
    private bool _isFullScreen;

    // ================================================================ M12：个人教学工作流

    /// <summary>
    /// M12 命令总线：悬浮球 / 自定义宏 / 工作台模式共用的"动作编程入口"。
    /// 按钮 Click 的老路径原样保留 —— 总线是<b>多一个</b>入口，不是替代。
    /// </summary>
    private readonly CommandBus _commands = new();

    /// <summary>工具栏是否处于"被老师收起"的持久态（记 UiStateStore，启动恢复）。</summary>
    private bool _toolbarHidden;

    /// <summary>
    /// 浮动工具栏的位置比例（M13；null = 还没定位过，按左上角处理）。
    /// 拖动把手 / 从悬浮球呼出时更新，记 UiStateStore。
    /// </summary>
    private double? _toolbarX;
    private double? _toolbarY;

    /// <summary>"工具栏已收起"的一次性提示是否给过（每次会话一次，防"工具栏丢了"）。</summary>
    private bool _hideTipShown;

    /// <summary>悬浮球显示模式：auto（工具栏藏了才出现，默认）/ always / off。记 UiStateStore。</summary>
    private string _ballMode = "auto";

    /// <summary>自定义宏（macros.txt，启动时读一次；坏了不影响启动）。</summary>
    private IReadOnlyList<MacroDefinition> _macros = Array.Empty<MacroDefinition>();

    /// <summary>宏热键索引：Ctrl+Shift+数字 → 宏。</summary>
    private readonly Dictionary<string, MacroDefinition> _macrosByHotkey = new(StringComparer.Ordinal);

    /// <summary>当前工作台模式（记 UiStateStore；null = 本次会话还没应用过模式）。</summary>
    private BoardMode? _currentMode;

    public MainWindow()
    {
        InitializeComponent();

        _colorButtons = new[] { ColorButton0, ColorButton1, ColorButton2, ColorButton3, ColorButton4, ColorButton5 };
        _widthButtons = new[] { WidthButton0, WidthButton1, WidthButton2, WidthButton3 };
        _craftButtons = new[] { CraftButton0, CraftButton1, CraftButton2 };
        _smoothButtons = new[] { SmoothButton0, SmoothButton1 };

        // 墨色与笔宽的"长相"全部由 InkPalette 派生（M10 修掉的真实缺陷之一：
        // 以前 XAML 里手抄了一份完全一样的墨色，改了 InkPalette 色块却不跟着变）。
        BuildInkButtons();

        // 换主题后 XAML 里的 DynamicResource 会自己刷新，
        // 但 code-behind **亲手设过**的那些颜色不会 —— 挂在这里补一次。
        ThemeService.ThemeChanged += OnThemeChanged;
        UpdateThemeButton();

        // 演示面板（M7.5）必须在插件加载**之前**装配好：插件工具在 Activate 里会向
        // context.GeoGebra 查询可用性来决定按钮是否置灰，晚一步装配就会看到 null。
        // ★ 装配只创建后端与控制器，WebView2 的初始化推迟到第一次打开面板 ——
        //   启动关键路径上不该付这个代价（大多数课根本用不到演示面板）。
        AttachGeoGebraPanel();

        // 物理仿真 Web 面板（M22 S5）：同样在插件加载之前装配好 ——
        // 仿真窗的 Activate 会向 context.WebSim 查询可用性来决定网页入口置灰，
        // 晚一步装配就会看到 null（与上面 GeoGebra 那条是同一个理由）。
        AttachWebSimPanel();

        // 插件加载（M7.2）必须发生在生成按钮之前 —— 顺序反了会表现成
        // "插件工具能按快捷键切到、却没有按钮"，看起来像插件没加载成功。
        // 加载器内部对每个插件的失败都做了隔离，所以这一行不会让程序启动失败。
        //
        // ★ 必须把图形渲染器注册表一起交进去（M7.4）：插件除了工具，还贡献"某种图形怎么画"。
        //   漏掉这个参数不会有任何报错 —— 表现是"插件的图形全都画成虚线占位框"，
        //   而日志里一片正常，是这一层最难查的失败方式。
        var pluginReport = PluginLoader.LoadAll(ViewportHost.PluginRegistry, ViewportHost.GfxRenderers);

        // 工具栏按钮来自工具注册表 —— 这里之后不再出现任何工具的名字（M7.1）
        BuildToolButtons();

        _autoSave = new AutoSaveScheduler(Dispatcher, AutoSaveScheduler.DefaultDebounce, SaveAnnotationsNow);
        _autoSave.Saved += OnAnnotationsSaved;
        _project.Changed += OnProjectChanged;

        DataContext = _viewModel;
        _viewModel.DocumentChanged += OnDocumentChanged;
        ViewportHost.ViewportChanged += OnViewportChanged;
        ViewportHost.InkChanged += OnInkChanged;
        ViewportHost.HistoryChanged += OnHistoryChanged;
        ViewportHost.ToolStatusMessage += OnToolStatusMessage;

        // 出厂工具是「笔」（注册表注册的第一个工具就是它，这里只是写明确）
        ViewportHost.SetTool(ToolIds.Pen);
        RefreshToolButtons();

        // 出厂设置也写成"从调色板取"，而不是在 XAML/宿主里各写一份常量：
        // 哪天改了默认档位，只改 InkPalette 一处，不会留下互相矛盾的两个默认值。
        SelectColor(InkPalette.DefaultColorIndex);
        SelectWidth(InkPalette.DefaultWidthIndex);

        // M11 书写手感：压感仍然默认关（M4 的结论不变），但**笔锋默认开** ——
        // 有笔锋才像真笔，这是本里程碑的验收目标本身。
        // 两个开关的共同后果（IgnorePressure）由宿主一处合成，见 SyncInkPressureSettings。
        PressureCheckBox.IsChecked = false;
        ViewportHost.SetPressureEnabled(false);

        CraftCheckBox.IsChecked = true;
        ViewportHost.SetCraftEnabled(true);
        SelectCraftStrength((int)CraftStrength.Medium);

        // M18 笔迹平滑：默认开 + Light 档 —— 顺滑收益立即可见，嫌迟滞可一键关回旧版
        SmoothCheckBox.IsChecked = true;
        ViewportHost.SetSmoothEnabled(true);
        SelectSmoothStrength((int)SmoothStrength.Light);

        UpdateInkCount();
        UpdateHistoryButtons();
        UpdateAnnotationStatus();
        UpdateProjectStatus();

        // 导出服务（M9）：需要画布宿主与 PDF 服务，所以只能等到这里建（控件已由 InitializeComponent 造好）。
        _exporter = new ExportService(ViewportHost, _viewModel.PdfService);
        UpdateExportButton();

        // 插件的情况只在"有插件"或"有插件没加载成功"时占一行状态栏。
        // 一台没装插件的机器不该在状态栏上看到插件字样 —— 那只会让人以为哪里没配好。
        if (pluginReport.HasProblems || pluginReport.LoadedCount > 0)
        {
            _viewModel.SetStatus(pluginReport.Summary);
        }

        // 界面自测钩子（M10）：设了 M10_UI_SHOT 才会执行，正常使用零影响。
        UiShotHook.RunIfRequested(this);

        // 启动模式跟随上次的选择；首次运行才用出厂默认（满屏覆盖，一体机的主用法）。
        //
        // 之前是"每次启动都强制全屏"，结果是一体机上没有键盘、界面里又没有按钮时，
        // 老师进去就出不来 —— 一个只有键盘能解的默认值，在触屏设备上是 bug 不是特性。
        // 现在：默认仍然全屏，但**用户切过一次就记住**，下次按他上次的选择来。
        if (UiStateStore.ReadFullScreen() ?? true) EnterFullScreen();
        else ExitFullScreen();

        // ============ M12：教学工作流装配 ============
        // 顺序：先有命令总线（悬浮球 / 模式 / 宏都只认命令 Id），再挂消费者，
        // 最后恢复上次的工具栏显隐与悬浮球位置（都记在 UiStateStore）。
        RegisterCommands();
        AuditCommandIds();
        InitFloatingBall();
        InitToolbarHiddenState();
        InitPaperColor();
        LoadMacros();

        // M13 S4：浮动工具栏装配 —— 把手条拖动（位置持久化）+ 尺寸变化重摆。
        // 只有把手响应拖动；面板里的按钮点击与拖动天然分离。
        // M16：拖动中走渲染变换（FloatingDrag 内部处理），松手才回调最终位置。
        FloatingDrag.Attach(
            ToolbarGrip, TopChrome, CanvasOverlayGrid,
            onCommitted: point =>
            {
                double availX = Math.Max(CanvasOverlayGrid.ActualWidth - TopChrome.ActualWidth, 1.0);
                double availY = Math.Max(CanvasOverlayGrid.ActualHeight - TopChrome.ActualHeight, 1.0);
                _toolbarX = Math.Clamp(point.X / availX, 0.0, 1.0);
                _toolbarY = Math.Clamp(point.Y / availY, 0.0, 1.0);
                UiStateStore.WriteToolbarX(_toolbarX.Value);
                UiStateStore.WriteToolbarY(_toolbarY.Value);
            });

        // 窗口尺寸变化（全屏/窗口切换、分屏拖动）：按比例重摆工具栏，clamp 防出界。
        SizeChanged += (_, _) =>
        {
            if (!_toolbarHidden) ApplyToolbarMargin();
        };

        // 首次布局后面板才有 ActualWidth —— 那时再按持久化比例精确定位一次。
        Loaded += (_, _) =>
        {
            if (!_toolbarHidden) ApplyToolbarMargin();
        };

        // 命令行参数（M8）：双击工程文件 / 把文件拖到 exe 上 / "打开方式"传参。
        //
        // ★ 一个功能要能被用起来才算真的存在：只把「打开工程」放在工具栏上，
        //   意味着老师每次都得先开程序、再去对话框里翻目录找刚才那个工程。
        //   让工程文件本身成为一个入口，才是"双击就能接着上课"。
        // 命令行参数要等窗口真正显示之后再处理：装载过程中可能弹起模态对话框
        // （试卷找不到时要请老师指出位置），而在构造函数里弹的话，主窗口还在渲染队列里、
        // 屏幕上根本没有它，对话框会孤零零地出现在角落，看起来就像「一启动就报错」。
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;   // 一次性：窗口重新显示时不该把工程再开一遍
        ApplyStartupArgument();
    }

    /// <summary>
    /// 处理命令行里的文件参数：<c>.twb</c> 走工程装载，<c>.pdf</c> 走快速模式。
    /// </summary>
    /// <remarks>
    /// 只认第一个参数，并且必须真实存在 —— 现场传进来的可能是相对路径、可能是
    /// 快捷方式附加的怪参数，照着猜只会让程序"一启动就报错"。
    /// 认不出来就安安静静地照常启动（这里不是报错的地方）。
    /// </remarks>
    private void ApplyStartupArgument()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length < 2) return;

        string path;

        try
        {
            path = System.IO.Path.GetFullPath(args[1]);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"命令行参数不是有效路径：[{args[1]}] —— {ex.Message}");
            return;
        }

        if (!File.Exists(path)) return;

        AppLog.Info($"命令行打开：{path}");

        if (path.EndsWith(ProjectExtension, StringComparison.OrdinalIgnoreCase))
        {
            OpenProject(path);
        }
        else if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.OpenDocument(path);
        }
    }

    /// <summary>
    /// 文档打开 / 关闭：先把旧文档的批注存住，再切换，最后装回新文档的批注。
    /// </summary>
    /// <remarks>
    /// 顺序是硬要求，不是风格：
    /// <list type="number">
    /// <item><b>先保存</b> —— <c>SetDocument</c> 会清空笔迹，之后再想保存旧批注就没内容了；</item>
    /// <item><b>再切换</b> —— 宿主会清空笔迹、重置撤销历史、重置视口；</item>
    /// <item><b>最后装载</b> —— 把新文档自己的批注装回去。</item>
    /// </list>
    /// 整个切换过程挂起自动保存：否则"清空 + 灌入笔迹"这两步会被当成用户改了东西，
    /// 刚读完就立刻写回去一遍。
    /// </remarks>
    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        // 1) 旧文档的最后一次保存机会
        _autoSave.Flush();
        _autoSave.Cancel();

        _autoSave.IsSuspended = true;

        try
        {
            // 无文档时传 null，让画布清空
            ViewportHost.SetDocument(_viewModel.Document.HasDocument ? _viewModel.PdfService : null);

            // 注意：必须取 Document.FilePath（视图模型里持久保存的那份），
            // 而不是 PdfService.FilePath —— 后者在关闭文档后已被置空。
            _annotatedPath = _viewModel.Document.FilePath;
            LoadAnnotations();
        }
        finally
        {
            _autoSave.MarkClean();
            _autoSave.IsSuspended = false;
        }

        UpdateZoomText();
        UpdateInkCount();
        UpdateAnnotationStatus();

        // 换文档要重算导出按钮：没有试卷时它必须置灰（"点了没反应"会被当成程序坏了）
        UpdateExportButton();
    }

    private void OnViewportChanged(object? sender, EventArgs e) => UpdateZoomText();

    /// <summary>
    /// 画布内容（<b>笔迹 + 图形对象</b>）有任何变化：标记待保存 + 刷新界面。
    /// </summary>
    /// <remarks>
    /// 这里只置脏标记并重新计时，真正落盘由 <see cref="AutoSaveScheduler"/> 在停笔 2 秒后进行 ——
    /// 写字过程中每落一笔都写盘的话，一次板书会触发上百次文件写入。
    /// <para>
    /// ★ 图形对象也走这里（宿主把两类内容合成同一个事件上报）：拖一下坐标系同样是改动，
    /// 少了它就会"关窗口不问、侧车也不重写"。工程模式下它就是
    /// <see cref="ProjectClosePolicy"/> 那条三选提示的触发源。
    /// </para>
    /// </remarks>
    private void OnInkChanged(object? sender, EventArgs e)
    {
        _autoSave.MarkDirty();

        // 工程模式下"存住了没有"看的是工程自己的脏标记 ——
        // 视图状态虽然也进工程，但它不算改动（否则翻一页就会被问"要保存吗"）。
        if (_project.HasProject) _project.MarkDirty();

        UpdateInkCount();
        UpdateAnnotationStatus();
        UpdateQuestionIndexText();
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        UpdateHistoryButtons();
        UpdateQuestionIndexText();      // 题号标记是图形对象：增删改会走统一时间线
    }

    /// <summary>
    /// 刷新撤销 / 重做按钮的可用态。
    /// </summary>
    /// <remarks>
    /// 按钮必须"不能用就置灰"，而不是"点了没反应"——
    /// 一体机上使用者只能靠外观判断状态，一个点了没反应的按钮会被当成程序坏了。
    /// </remarks>
    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = ViewportHost.CanUndo;
        RedoButton.IsEnabled = ViewportHost.CanRedo;
    }

    private void UpdateZoomText() => ZoomText.Text = $"{ViewportHost.Viewport.Scale * 100:F0}%";

    /// <summary>状态栏的笔迹条数。擦除、清空、换文档都靠它给出反馈。</summary>
    private void UpdateInkCount() => InkCountText.Text = $"笔迹 {ViewportHost.StrokeCount} 条";

    private void OnFitWidthClick(object sender, RoutedEventArgs e) => ViewportHost.FitWidth();

    private void OnActualSizeClick(object sender, RoutedEventArgs e) => ViewportHost.ActualSize();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ViewportHost.ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ViewportHost.ZoomOut();

    // ---------------------------------------------------------------- 工具（M7.1：注册表驱动）

    /// <summary>按工具注册表生成工具栏按钮。</summary>
    /// <summary>
    /// 装配 GeoGebra 演示面板（M7.5）。
    /// </summary>
    /// <remarks>
    /// 装配失败<b>绝不能影响启动</b>：缺 WebView2 运行时在一体机上是常态，
    /// 那必须是"面板按钮按住没反应/置灰"，而不是白板打不开。
    /// 这与 M7.2 插件加载器的"失败隔离"是同一条纪律。
    /// </remarks>
    private void AttachGeoGebraPanel()
    {
        try
        {
            // 停靠宿主（A1 形态）一并交给后端：切换形态时由它把 WebView2 搬进 / 搬出。
            var backend = WebView2PanelBackend.Create(this, GeoGebraDockHost, WebPanelProfile.GeoGebra);
            _geoGebraPanel = new WebPanelController(WebPanelProfile.GeoGebra, backend, ViewportHost);
            ViewportHost.GeoGebra = _geoGebraPanel;
            _geoGebraPanel.FormChanged += OnGeoGebraFormChanged;
            _geoGebraPanel.Closed += OnGeoGebraPanelClosed;

            // M22 S4b：面板顶栏「导出到白板」→ 抓帧 → 落成一个可存档的位图对象。
            // 三个依赖都是"当时的实际情况"（当前页 / 视口中心 / 对象通道），
            // 所以用委托而不是先把值取出来 —— 面板是长命的，值不是。
            _webPanelExport = new WebPanelExportService(
                _geoGebraPanel,
                ViewportHost.WebPanelImages,
                () => ViewportHost.PageUnderViewportCenter,
                () => ViewportHost.ViewportCenterWorld,
                place: draft => ViewportHost.GfxHost.Add(draft));

            _geoGebraPanel.CaptureRequested += OnWebPanelCaptureRequested;

            if (!_geoGebraPanel.IsAvailable)
            {
                AppLog.Warn($"GeoGebra 演示面板不可用：{_geoGebraPanel.UnavailableReason}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"GeoGebra 演示面板装配失败（不影响白板其它功能）："
                        + $"{ex.GetType().Name} {ex.Message}");
            _geoGebraPanel = null;
            ViewportHost.GeoGebra = null;
        }
    }

    /// <summary>
    /// 装配物理仿真 Web 面板（M22 S5）：自建导航页内嵌 CircuitJS。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="AttachGeoGebraPanel"/> 同一条纪律：装配失败<b>绝不连坐</b>，
    /// 表现必须是「物理仿真窗里的网页入口置灰 + 一句中文原因」，而不是白板打不开。
    /// 插件侧（NativeSim）只说「打开哪个页」；这条路通不通由这里的装配质量决定。
    /// </remarks>
    private void AttachWebSimPanel()
    {
        try
        {
            // 物理仿真只做全屏（M22 D3），没有停靠形态。后端构造需要一个停靠宿主，
            // 给它一个永远折叠、也不在视觉树里的容器兜底：正常路径到不了它
            // （窗内按钮已隐藏、Esc 的 D 不接），真被误切进去也只是看不见，不会炸。
            var dock = new Border { Visibility = Visibility.Collapsed };

            var backend = WebView2PanelBackend.Create(this, dock, WebPanelProfile.WebSim);
            _webSimPanel = new WebPanelController(WebPanelProfile.WebSim, backend, ViewportHost);
            ViewportHost.WebSim = _webSimPanel;

            // 「导出到白板」与 GeoGebra 面板走同一套抓帧 → 落画布通路（M22 S4b）。
            _webSimExport = new WebPanelExportService(
                _webSimPanel,
                ViewportHost.WebPanelImages,
                () => ViewportHost.PageUnderViewportCenter,
                () => ViewportHost.ViewportCenterWorld,
                place: draft => ViewportHost.GfxHost.Add(draft));

            _webSimPanel.CaptureRequested += OnWebPanelCaptureRequested;

            if (!_webSimPanel.IsAvailable)
            {
                AppLog.Warn($"物理仿真面板不可用：{_webSimPanel.UnavailableReason}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"物理仿真面板装配失败（不影响白板其它功能）："
                        + $"{ex.GetType().Name} {ex.Message}");
            _webSimPanel = null;
            _webSimExport = null;
            ViewportHost.WebSim = null;
        }
    }

    /// <summary>
    /// 面板请求「导出到白板」：抓帧 → 落到卷面 → 关掉面板（M22 S4b）。
    /// </summary>
    /// <remarks>
    /// ★ <b>无论成败都要把面板关掉。</b>面板是全屏的，它盖着状态栏 ——
    /// 不关的话老师点了按钮，屏幕上什么都不会变，那在教室里就是"按钮坏了"。
    /// 关掉之后状态栏那句话才是他唯一看得到的反馈，所以失败原因必须写清楚。
    /// <para>
    /// 成功时也关：他要的正是"把这张图拿到卷面上去用"，留着面板只会挡住那张图。
    /// 想再导一张？重新打开面板只要一次点击，而看不见图的代价要大得多。
    /// </para>
    /// </remarks>
    private async void OnWebPanelCaptureRequested(object? sender, EventArgs e)
    {
        try
        {
            // 按触发者选导出服务（M22 S5 起有两个面板）：谁请求，用谁的。
            var export = ReferenceEquals(sender, _webSimPanel) ? _webSimExport : _webPanelExport;
            if (export is null) return;

            var result = await export.ExportAsync();

            AppLog.Info($"仿真画面导出：{result.Message}");

            if (sender is WebPanelController panel) await panel.CloseAsync();
            else if (_geoGebraPanel is not null) await _geoGebraPanel.CloseAsync();

            _viewModel.SetStatus(result.Message);
            UpdateInkCount();
        }
        catch (Exception ex)
        {
            // 这条通路的终点是"把图放到卷面上"，任何一环坏了都只该是一句话，
            // 不该把白板扔在教室投影上不动。导出服务本身不抛，这里是最后一道网。
            AppLog.Warn($"仿真画面导出流程异常：{ex.GetType().Name} {ex.Message}");
            _viewModel.SetStatus("导出仿真画面失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 面板形态变化：撑开 / 收起主窗口的停靠区。
    /// </summary>
    /// <remarks>
    /// ★ 停靠宽度按屏幕的 42% 取，钳在 [420, 900]：
    /// 太窄 GeoGebra 的工具栏和代数区挤成一团，太宽试卷看不下来。
    /// </remarks>
    private void OnGeoGebraFormChanged(object? sender, PanelForm form)
    {
        if (form == PanelForm.Docked)
        {
            var width = Math.Clamp(SystemParameters.PrimaryScreenWidth * 0.42, 420, 900);
            GeoGebraDock.Width = width;
            GeoGebraDock.Visibility = Visibility.Visible;
            AppLog.Info($"GeoGebra 停靠区已撑开：{width:0} DIP。");
        }
        else
        {
            GeoGebraDock.Visibility = Visibility.Collapsed;
            AppLog.Info("GeoGebra 停靠区已收起。");
        }
    }

    /// <summary>停靠区顶栏「返回白板」。</summary>
    private void OnDockBackClick(object sender, RoutedEventArgs e) => _ = _geoGebraPanel?.CloseAsync();

    /// <summary>停靠区顶栏「切全屏」。</summary>
    private void OnDockToggleFormClick(object sender, RoutedEventArgs e) => _ = _geoGebraPanel?.ToggleFormAsync();

    /// <summary>
    /// 面板关闭：收起停靠区，把整块屏还给画布。
    /// </summary>
    /// <remarks>
    /// ★ 收起要跟着"开没开"走而不是"形态是什么"：关闭不改变形态偏好
    /// （下次打开仍是停靠），但那块地必须当场还给画布 ——
    /// 否则老师退出了面板，右边还杵着一块点不动的 GeoGebra。
    /// </remarks>
    private void OnGeoGebraPanelClosed(object? sender, EventArgs e)
    {
        GeoGebraDock.Visibility = Visibility.Collapsed;
        AppLog.Info("GeoGebra 停靠区已收起。");
    }

    /// <summary>
    /// 按工具目录表把工具瓦片铺到各组里（M20 S4）。
    /// </summary>
    /// <remarks>
    /// 三步走：① 给注册表里每个工具建视图模型 —— <b>族成员各自一个</b>，
    /// 因为二级菜单上要能分别看出"现在是族里哪一个在生效"；
    /// ② 按 <see cref="ToolCatalog.Entries"/> 的顺序决定"谁上工具栏、排在哪"；
    /// ③ 工具族的<b>变体不上工具栏</b>，挂到代表那颗瓦片的二级菜单里。
    /// <para>
    /// ★ 目录表登记了、注册表里却没有的工具（插件没装 / 被停用）会被<b>静默跳过</b>：
    /// 插件目录是可裁剪的（轻量版就少了 GeoGebra），界面必须跟着少一颗按钮，
    /// 而不是留一个点了没反应的空位。
    /// </para>
    /// <para>
    /// ★ 目录表里没登记的工具进「其他」组并记一条警告：新插件带来的工具
    /// <b>看得见、点得着</b>，同时留下一条"该补目录表了"的痕迹。
    /// </para>
    /// </remarks>
    private void BuildToolButtons()
    {
        _toolButtons.Clear();
        InkToolButtons.Children.Clear();
        ToolButtons.Children.Clear();
        OtherToolButtons.Children.Clear();
        GroupPanelOther.Visibility = Visibility.Collapsed;

        var byId = new Dictionary<string, ToolButtonViewModel>(StringComparer.Ordinal);

        foreach (var tool in ViewportHost.Tools)
        {
            if (!byId.ContainsKey(tool.Id)) byId[tool.Id] = new ToolButtonViewModel(tool);
        }

        // 族成员也要进这个列表：当前工具切到变体时，二级菜单里那一行要亮起来。
        foreach (var model in byId.Values) _toolButtons.Add(model);

        int tiles = 0;
        int folded = 0;
        int other = 0;

        foreach (var entry in ToolCatalog.Entries)
        {
            if (!byId.TryGetValue(entry.Id, out var model)) continue;   // 这个插件没来

            if (entry.VariantOf is not null)
            {
                folded++;   // 变体：进二级菜单，不单独占一格
                continue;
            }

            var members = ToolCatalog.FamilyMembers(entry.Id)
                .Where(m => byId.ContainsKey(m.Id))
                .Select(m => byId[m.Id])
                .ToList();

            // 只有一个成员（变体那个插件没来）就不做二级菜单 —— 箭头点了弹个一条的菜单很怪。
            if (members.Count > 1) model.AttachFamily(entry.FamilyTitle ?? entry.TileLabel, members);

            AddToolTile(model, entry.Group);
            tiles++;
        }

        foreach (var model in byId.Values)
        {
            if (ToolCatalog.Find(model.Id) is not null) continue;

            AddToolTile(model, ToolGroup.Other);
            tiles++;
            other++;
            AppLog.Warn($"工具「{model.DisplayName}」({model.Id}) 没登记在 ToolCatalog，"
                        + "已放进工具栏「其他」组；请补进目录表（分组与图标）。");
        }

        _toolTileCount = tiles;
        _toolFoldedCount = folded;
        _toolUncataloguedCount = other;

        AppLog.Info($"工具栏：注册表 {ViewportHost.Tools.Count} 个工具 → 可见瓦片 {tiles} 颗，"
                    + $"折进二级菜单 {folded} 个，未登记 {other} 个。");
    }

    /// <summary>把一颗工具瓦片放进它那一组的容器里。</summary>
    private void AddToolTile(ToolButtonViewModel model, ToolGroup group)
    {
        FrameworkElement element = BuildToolTile(model);

        switch (group)
        {
            case ToolGroup.Ink:
                InkToolButtons.Children.Add(element);
                break;

            case ToolGroup.Other:
                GroupPanelOther.Visibility = Visibility.Visible;
                OtherToolButtons.Children.Add(element);
                break;

            default:
                // 文档 / 系统两组里没有注册表工具（那两组是宿主自己的按钮）。
                // 万一目录表把某个工具归到了那里，也一律按「图形」处理：
                // 宁可归错组，也不能让按钮从界面上消失。
                ToolButtons.Children.Add(element);
                break;
        }
    }

    /// <summary>造一颗工具瓦片（图标在上、短名在下；有二级菜单的再带一个箭头）。</summary>
    /// <remarks>
    /// 瓦片内容是代码搭的而不是 XAML 模板：同一颗瓦片有"光板"和"带二级菜单"两种形态，
    /// 用一段代码表达比"两套模板 + 选择器"清楚，也顺手避开了嵌套 ItemsControl
    /// 在 WrapPanel 里量错宽度的坑。
    /// <para>
    /// ★ 绑定一律写死 <c>Source</c>（不靠 DataContext 继承）：瓦片自身的 DataContext
    /// 还要供样式里的 <c>{Binding VariantsOpen}</c>（箭头开合）与
    /// <c>{Binding HasVariants}</c>（箭头显隐）用 —— 少一层继承假设，就少一处
    /// "以后包一层容器就悄悄断掉"的事故。
    /// </para>
    /// </remarks>
    private FrameworkElement BuildToolTile(ToolButtonViewModel model)
    {
        var tile = new ToggleButton
        {
            Style = (Style)FindResource("Style.ToolTileToggle"),
            Content = BuildTileContent(model),
            Tag = model.Id,
            DataContext = model,
            // 带二级菜单时外层还会套一个横向 StackPanel，间距挂在它身上；
            // 光板瓦片就自己带间距。
            Margin = model.HasVariants ? new Thickness(0) : Tokens.Thickness("Gap.Tile"),
        };

        tile.SetBinding(ToggleButton.IsCheckedProperty, Bind(nameof(model.IsActive), model));
        tile.SetBinding(FrameworkElement.ToolTipProperty, Bind(nameof(model.ToolTip), model));
        tile.Click += OnToolButtonClick;

        if (!model.HasVariants) return tile;

        var chevron = new ToggleButton
        {
            Style = (Style)FindResource("Style.FamilyChevron"),
            Content = BuildChevronContent(),
            DataContext = model,
            Tag = model.Id,
        };
        chevron.SetBinding(FrameworkElement.ToolTipProperty,
            Bind(nameof(model.FamilyTitle), model, stringFormat: "「{0}」族：点这里换本族的其它工具"));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = Tokens.Thickness("Gap.Tile"),
        };
        row.Children.Add(tile);
        row.Children.Add(chevron);
        row.Children.Add(BuildFamilyPopup(model, chevron));
        return row;
    }

    /// <summary>瓦片内容：图标 + 短名。</summary>
    private StackPanel BuildTileContent(ToolButtonViewModel model)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Style = (Style)FindResource("Style.ButtonIconPath"),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        icon.SetBinding(System.Windows.Shapes.Path.DataProperty, Bind(nameof(model.Icon), model));

        var text = new TextBlock
        {
            Style = (Style)FindResource("Style.TileText"),
            Margin = Tokens.Thickness("Tile.TextGap"),
        };
        text.SetBinding(TextBlock.TextProperty, Bind(nameof(model.TileLabel), model));

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        content.Children.Add(icon);
        content.Children.Add(text);
        return content;
    }

    /// <summary>箭头内容：向下的折线 + 一个"族里正用着变体"的小圆点。</summary>
    private Grid BuildChevronContent()
    {
        var arrow = new System.Windows.Shapes.Path
        {
            Style = (Style)FindResource("Style.ButtonIconPath"),
            Data = (Geometry)FindResource("Icon.ChevronDown"),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 小圆点显隐完全由样式里的 DataTrigger 决定（绑 AnyVariantActive），这里只造出来。
        var dot = new System.Windows.Shapes.Ellipse
        {
            Style = (Style)FindResource("Style.FamilyChevronDot"),
        };

        var grid = new Grid();
        grid.Children.Add(arrow);
        grid.Children.Add(dot);
        return grid;
    }

    /// <summary>造工具族的二级菜单：族名 + 每个成员一行（当前那个自带主色高亮）。</summary>
    private Popup BuildFamilyPopup(ToolButtonViewModel model, ToggleButton chevron)
    {
        var rows = new StackPanel();

        rows.Children.Add(new TextBlock
        {
            Text = model.FamilyTitle,
            Style = (Style)FindResource("Style.TextLabel"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        foreach (var member in model.Members)
        {
            var icon = new System.Windows.Shapes.Path
            {
                Style = (Style)FindResource("Style.ButtonIconPath"),
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetBinding(System.Windows.Shapes.Path.DataProperty, Bind(nameof(member.Icon), member));

            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            text.SetBinding(TextBlock.TextProperty, Bind(nameof(member.DisplayName), member));

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(text);

            var button = new ToggleButton
            {
                Style = (Style)FindResource("Style.FamilyMemberToggle"),
                Content = content,
                Tag = member.Id,
                DataContext = member,
            };
            button.SetBinding(ToggleButton.IsCheckedProperty, Bind(nameof(member.IsActive), member));
            button.SetBinding(FrameworkElement.ToolTipProperty, Bind(nameof(member.ToolTip), member));
            button.Click += OnToolButtonClick;
            rows.Children.Add(button);
        }

        var popup = new Popup
        {
            PlacementTarget = chevron,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Style = (Style)FindResource("Style.PanelSurface"),
                Padding = Tokens.Thickness("Pad.Popup"),
                Child = rows,
            },
        };

        // 开合与箭头的 IsChecked 共用同一个字段 ⇒ 点别处关掉弹窗时箭头也自己弹回来。
        popup.SetBinding(Popup.IsOpenProperty, Bind(nameof(model.VariantsOpen), model, BindingMode.TwoWay));
        return popup;
    }

    /// <summary>造一条绑定：来源写死，不依赖 DataContext 继承。</summary>
    private static Binding Bind(string path, object source,
        BindingMode mode = BindingMode.OneWay, string? stringFormat = null)
    {
        var binding = new Binding(path) { Source = source, Mode = mode };
        if (stringFormat is not null) binding.StringFormat = stringFormat;
        return binding;
    }

    /// <summary>
    /// 点了某颗工具按钮。
    /// </summary>
    /// <remarks>
    /// 按钮的 Id 从 <c>Tag</c> 上取 —— 这样一套模板就能生成任意多个工具的按钮，
    /// 界面对"有哪些工具"一无所知。
    /// </remarks>
    private void OnToolButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;

        ViewportHost.SetTool(id);
        RefreshToolButtons();
    }

    /// <summary>
    /// 把"当前工具"的勾选态刷到所有按钮上。
    /// </summary>
    /// <remarks>
    /// 必须<b>整体回写</b>而不是靠 ToggleButton 自己翻转：点已经选中的那颗按钮时，
    /// ToggleButton 会先把它翻成未选中，于是屏幕上的高亮与真正的工具就对不上了
    /// （表现为"这个按钮点了没反应"）。整体回写每次都把状态纠正回来。
    /// </remarks>
    private void RefreshToolButtons()
    {
        string? active = ViewportHost.ActiveToolId;

        foreach (var button in _toolButtons)
        {
            button.IsActive = string.Equals(button.Id, active, StringComparison.Ordinal);
        }

        // ★ 族：代表瓦片的选中态**只认代表自己**（它确实不是当前工具时就不该亮），
        //   所以"现在用的是本族变体"这件事必须另有提示 —— 就是箭头上那个小圆点。
        //   少了它会出现"切到自由角直尺，界面上哪儿都不亮"，看着像工具没切过去。
        foreach (var button in _toolButtons)
        {
            if (!button.HasVariants) continue;

            button.AnyVariantActive = false;
            for (int i = 1; i < button.Members.Count; i++)
            {
                if (!button.Members[i].IsActive) continue;
                button.AnyVariantActive = true;
                break;
            }
        }
    }

    /// <summary>工具往状态栏写的一句话。</summary>
    private void OnToolStatusMessage(object? sender, string text) => _viewModel.SetStatus(text);

    // ---------------------------------------------------------------- 墨色 / 笔宽 / 压感

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !TryGetIndex(button, out int index)) return;
        if (index < 0 || index >= InkPalette.Colors.Count) return;

        SelectColor(index);
    }

    private void OnWidthClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !TryGetIndex(button, out int index)) return;
        if (index < 0 || index >= InkPalette.Widths.Count) return;

        SelectWidth(index);
    }

    private void SelectColor(int index)
    {
        _colorIndex = index;
        ViewportHost.SetPenColor(InkPalette.Colors[index].Color);
        Highlight(_colorButtons, index);
    }

    private void SelectWidth(int index)
    {
        _widthIndex = index;
        ViewportHost.SetPenWidth(InkPalette.Widths[index].WorldWidth);
        Highlight(_widthButtons, index);
    }

    /// <summary>
    /// 把 <see cref="InkPalette"/> 里的墨色 / 笔宽铺到按钮上。
    /// </summary>
    /// <remarks>
    /// ★ 按钮的底色、文字、提示**全部是派生出来的**，一个都不手写：
    /// 手写就意味着"改一处忘一处"，而这个界面里已经出现过一次
    /// （墨色在 <c>InkPalette.cs</c> 与 <c>MainWindow.xaml</c> 各定义了一份）。
    /// </remarks>
    private void BuildInkButtons()
    {
        for (int i = 0; i < _colorButtons.Length && i < InkPalette.Colors.Count; i++)
        {
            var option = InkPalette.Colors[i];
            _colorButtons[i].Background = new SolidColorBrush(option.Color);

            // 红是批改惯例里的默认色，值得在提示里说明白；其余就报名字
            _colorButtons[i].ToolTip = i == InkPalette.DefaultColorIndex
                ? $"{option.Name}（批改，默认）"
                : option.Name;
        }

        for (int i = 0; i < _widthButtons.Length && i < InkPalette.Widths.Count; i++)
        {
            var option = InkPalette.Widths[i];
            _widthButtons[i].Content = option.Name;

            // 毫米数从 world(pt) 现算：手写过一次（0.28 / 0.53 / 0.88 / 1.41），
            // 只要 InkPalette 的笔宽一改，手写的那个数就开始骗人。
            double mm = option.WorldWidth * 25.4 / 72.0;
            _widthButtons[i].ToolTip = $"{option.Name} {option.WorldWidth:0.0} pt ≈ {mm:0.00} mm";
        }

        // 笔锋力度三档（M11）：文字与提示同样从 SpeedPressureMap 现取，不手抄 ——
        // 三档共用同一个宽度区间（0.5~1.0 倍笔宽），差别只在曲线陡峭度，
        // 所以提示里要讲清"变化明显程度"，而不是"更粗/更细"。
        for (int i = 0; i < _craftButtons.Length; i++)
        {
            var level = SpeedPressureMap.FromIndex(i);

            _craftButtons[i].Content = SpeedPressureMap.Name(level);
            _craftButtons[i].ToolTip = level switch
            {
                CraftStrength.Weak => "笔锋·弱：行笔粗细变化平缓，接近等宽",
                CraftStrength.Strong => "笔锋·强：对速度最敏感，粗细对比最大",
                _ => "笔锋·中（默认）：快写明显变细，收得住时接近满宽",
            };
        }

        // 平滑强度两档（M18）：标签直书档名；Off 档就是勾掉「平滑」复选框，不占按钮
        string[] smoothNames = { "轻", "中" };

        for (int i = 0; i < _smoothButtons.Length; i++)
        {
            var level = (SmoothStrength)(i + 1);   // Tag 从 1 起：1=Light, 2=Medium

            _smoothButtons[i].Content = smoothNames[i];
            _smoothButtons[i].ToolTip = level switch
            {
                SmoothStrength.Medium => "平滑·中：最顺滑，略有滞后感（约 6 ms）",
                _ => "平滑·轻（默认）：消除毛刺，几乎无滞后感（约 2 ms）",
            };
        }
    }

    /// <summary>换主题后重刷"code-behind 亲手设过"的颜色。</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Highlight(_colorButtons, _colorIndex);
        Highlight(_widthButtons, _widthIndex);
        Highlight(_craftButtons, _craftStrengthIndex);
        Highlight(_smoothButtons, _smoothStrengthIndex);
        UpdateProjectStatus();   // 内部会一并刷新状态栏文字色
        UpdateThemeButton();
    }

    /// <summary>
    /// 刷新主题按钮的文案。
    /// </summary>
    /// <remarks>
    /// 按钮上写的是<b>点它会怎样</b>（"浅色"＝点了变浅色），与旁边的「窗口模式」按钮同一套约定：
    /// 一体机上没有悬停，老师只能靠按钮上的字判断它会做什么。
    /// </remarks>
    // ★ M20 S3：文案改写到**文字块**上 —— 按钮内容现在是「图标 + 文字」的竖排，
    //   直接给 Content 赋值会把图标整块顶掉。
    private void UpdateThemeButton() => ThemeButtonText.Text = ThemeService.SwitchTargetName;

    private void OnToggleThemeClick(object sender, RoutedEventArgs e) => ThemeService.Toggle();

    private void OnPressureClick(object sender, RoutedEventArgs e)
        => ViewportHost.SetPressureEnabled(PressureCheckBox.IsChecked == true);

    // ---------------------------------------------------------------- 笔锋（M11）

    private void OnCraftClick(object sender, RoutedEventArgs e)
    {
        ViewportHost.SetCraftEnabled(CraftCheckBox.IsChecked == true);
        UpdateCraftStrengthEnabled();
    }

    private void OnCraftStrengthClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !TryGetIndex(button, out int index)) return;

        // 笔锋关着的时候力度无从谈起 —— 按钮本身已置灰，这里再挡一次
        if (!ViewportHost.IsCraftEnabled) return;

        SelectCraftStrength(index);
    }

    private void SelectCraftStrength(int index)
    {
        _craftStrengthIndex = index;
        ViewportHost.SetCraftStrength(SpeedPressureMap.FromIndex(index));
        Highlight(_craftButtons, index);
        UpdateCraftStrengthEnabled();
    }

    /// <summary>
    /// 让力度档位按钮的可用态跟随笔锋开关。
    /// </summary>
    /// <remarks>
    /// 关掉笔锋却留着三个能点的按钮，等于给老师一个"点了没反应"的界面 ——
    /// 在一体机上没有悬停提示，他只会以为程序坏了。所以关就置灰。
    /// </remarks>
    private void UpdateCraftStrengthEnabled()
    {
        bool on = ViewportHost.IsCraftEnabled;

        foreach (var button in _craftButtons) button.IsEnabled = on;
        CraftStrengthLabel.Opacity = on ? 1.0 : 0.4;
    }

    // ---------------------------------------------------------------- 平滑（M18）

    private void OnSmoothClick(object sender, RoutedEventArgs e)
    {
        ViewportHost.SetSmoothEnabled(SmoothCheckBox.IsChecked == true);
        UpdateSmoothStrengthEnabled();
    }

    private void OnSmoothStrengthClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !TryGetIndex(button, out int index)) return;

        // 平滑关着的时候档位无从谈起 —— 按钮本身已置灰，这里再挡一次
        if (!ViewportHost.IsSmoothEnabled) return;

        SelectSmoothStrength(index);
    }

    private void SelectSmoothStrength(int index)
    {
        _smoothStrengthIndex = index;
        ViewportHost.SetSmoothStrength((SmoothStrength)index);
        Highlight(_smoothButtons, index);
        UpdateSmoothStrengthEnabled();
    }

    /// <summary>平滑档位按钮的可用态跟随「平滑」复选框（与笔锋力度同一套置灰逻辑）。</summary>
    private void UpdateSmoothStrengthEnabled()
    {
        bool on = ViewportHost.IsSmoothEnabled;

        foreach (var button in _smoothButtons) button.IsEnabled = on;
        SmoothStrengthLabel.Opacity = on ? 1.0 : 0.4;
    }

    private static bool TryGetIndex(Button button, out int index)
        => int.TryParse(button.Tag as string, out index);

    private static void Highlight(Button[] buttons, int selected)
    {
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].BorderBrush = i == selected ? SelectedBorder : NormalBorder;
        }
    }

    // ---------------------------------------------------------------- 撤销 / 重做 / 清除

    private void OnUndoClick(object sender, RoutedEventArgs e) => ViewportHost.Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => ViewportHost.Redo();

    /// <summary>
    /// 清除全部笔迹。
    /// </summary>
    /// <remarks>
    /// M5 起不再弹确认框：这一步本身是一个可撤销的单元，误触按 Ctrl+Z 就回来了。
    /// 对一个能一键还原的操作每次都拦一道，只会拖慢讲评节奏。
    /// </remarks>
    private void OnClearInkClick(object sender, RoutedEventArgs e) => ViewportHost.ClearStrokes();

    // ---------------------------------------------------------------- 批注存档（M6）

    /// <summary>自动保存成功后刷新状态栏。</summary>
    private void OnAnnotationsSaved(object? sender, EventArgs e)
    {
        _annotatedSavedAt = DateTime.Now;
        _annotationNotice = string.Empty;
        UpdateAnnotationStatus();
    }

    /// <summary>
    /// 把当前笔迹与图形写到侧车文件。无文档时返回 <c>null</c>。
    /// </summary>
    private AnnotationSaveResult? WriteAnnotations()
    {
        if (string.IsNullOrEmpty(_annotatedPath)) return null;

        return _annotationStore.Save(
            _annotatedPath,
            ViewportHost.SnapshotStrokes(),
            ViewportHost.Layout.PageRects,
            ViewportHost.Layout.PageMargin,
            ViewportHost.Layout.PageGap,
            ViewportHost.SnapshotObjects());
    }

    /// <summary>
    /// 自动保存的落盘动作（由 <see cref="AutoSaveScheduler"/> 在防抖到点后调用）。
    /// </summary>
    /// <returns>是否真的写了盘。</returns>
    private bool SaveAnnotationsNow()
    {
        // ★ 工程模式下自动保存**不落盘**。
        //
        // 工程包默认把整份试卷内嵌进去（扫描卷面几十 MB），而自动保存是"停笔 2 秒就写一次"——
        // 一节课下来会把上百 MB 写进磁盘，一体机上表现为周期性卡顿，U 盘上更糟。
        // 所以工程模式改成"只提示、等老师按保存"：状态栏上的橙字「● 工程未保存」一直亮着，
        // 关闭与切换工程时还有一道三选拦住。
        //
        // 返回 false 会让调度器清掉它自己的脏标记并停表（见 AutoSaveScheduler.Flush）——
        // 正是我们要的：不写盘、也不每 2 秒重试一次刷满日志。
        if (_project.HasProject) return false;

        var result = WriteAnnotations();
        if (result is null) return false;

        // 失败（只读目录、磁盘满…）只记在状态栏，不弹窗 ——
        // 自动保存每 2 秒可能来一次，弹窗会把课堂变成打地鼠。
        if (!result.Ok) _annotationNotice = "⚠ " + result.Message;

        return result.Written;
    }

    /// <summary>手动保存（Ctrl+S / 点「保存批注」）：立即落盘并给出明确反馈。</summary>
    private void SaveAnnotationsManually()
    {
        _autoSave.Cancel();

        var result = WriteAnnotations();

        if (result is null)
        {
            _annotationNotice = "没有打开的文档，无法保存批注";
        }
        else if (result.Ok)
        {
            _annotationNotice = string.Empty;
            _annotatedSavedAt = DateTime.Now;
            _autoSave.MarkClean();
        }
        else
        {
            _annotationNotice = "⚠ " + result.Message;
        }

        UpdateAnnotationStatus();
    }

    /// <summary>把待保存的批注落盘并停表。换文档前、关窗口前调用。</summary>
    private void SavePendingAnnotations()
    {
        // 工程模式不在这里写盘：写一次就是几十 MB，而且真正的"存不存"该由
        // 一个明确的提示来决定（关闭工程 / 关闭窗口时问），不该在一堆隐式时点上偷偷发生。
        if (_project.HasProject)
        {
            _autoSave.Cancel();
            return;
        }

        _autoSave.Flush();
        _autoSave.Cancel();
    }

    /// <summary>
    /// 装载当前文档的批注。
    /// </summary>
    /// <remarks>
    /// 三种结果的处置是刻意区分的：
    /// <list type="bullet">
    /// <item><b>拒绝</b>（排版对不上）—— 一条也不装。宁可没有批注，也不要让它错位到别的题上；</item>
    /// <item><b>指纹不符</b>（同名文件被覆盖过）—— 照常装载但黄字提醒，不打断讲评；</item>
    /// <item><b>没有批注文件</b> —— 新试卷的正常情况，静默。</item>
    /// </list>
    /// </remarks>
    private void LoadAnnotations()
    {
        // 正在装载工程：批注来自 .twb 里的字节，不该去读磁盘上的侧车文件
        if (_loadingProject) return;

        if (string.IsNullOrEmpty(_annotatedPath)) return;

        // 宿主还没排出页面（空文档）时没有几何可比，直接跳过
        if (ViewportHost.Layout.PageRects.Count == 0) return;

        var result = _annotationStore.Load(
            _annotatedPath,
            ViewportHost.Layout.PageRects,
            ViewportHost.Layout.PageMargin,
            ViewportHost.Layout.PageGap);

        switch (result.Status)
        {
            case AnnotationLoadStatus.Loaded:
            case AnnotationLoadStatus.LoadedWithMismatch:
                ViewportHost.ReplaceStrokes(result.Strokes!);
                ViewportHost.ReplaceObjects(result.Objects);
                _annotationNotice = result.IsWarning ? result.Message : string.Empty;
                _annotationIdleText = DescribeRestored(result.Strokes!.Count, result.Objects?.Count ?? 0, out string missing);
                if (missing.Length > 0)
                {
                    // 插件被禁用/卸载时，对应图形只能画成虚线占位。
                    // 这件事必须说出来：老师看到的是"我画的角变成了虚框"，
                    // 不解释就会以为程序把板书弄坏了。
                    _annotationNotice = missing;
                }
                break;

            case AnnotationLoadStatus.Rejected:
                _annotationNotice = "⚠ " + result.Message;
                _annotationIdleText = "无批注";
                break;

            default:
                _annotationNotice = string.Empty;
                _annotationIdleText = "无批注";
                break;
        }
    }

    /// <summary>拼一句"恢复了什么"，并给出缺渲染器的提醒（没有则为空串）。</summary>
    private string DescribeRestored(int strokeCount, int objectCount, out string missingNotice)
    {
        missingNotice = string.Empty;

        int missingCount = ViewportHost.GfxObjects.MissingRendererCount;
        if (missingCount > 0)
        {
            var names = string.Join("、", ViewportHost.GfxObjects.MissingNames);
            missingNotice = $"⚠ 有 {missingCount} 个图形缺少对应插件（{names}），暂以虚线框显示；启用插件后重开即可正常显示";
        }

        if (objectCount > 0) return $"批注已恢复 {strokeCount} 条、图形 {objectCount} 个";
        return $"批注已恢复 {strokeCount} 条";
    }

    /// <summary>
    /// 刷新状态栏的批注状态。
    /// </summary>
    /// <remarks>
    /// 优先级刻意是「警告 &gt; 未保存 &gt; 常态」：警告最需要被看见，
    /// 而"有改动还没落盘"比"上次存于几点几分"更该抢占注意力。
    /// </remarks>
    private void UpdateAnnotationStatus()
    {
        // 工程模式下状态栏说的是"整个工程存住了没有"，而不是"批注写进侧车没有" ——
        // 老师按了保存，看到的总得是同一件事的结果。
        if (_project.HasProject)
        {
            if (_annotationNotice.Length > 0)
            {
                AnnotationStatusText.Foreground = AnnotationWarningBrush;
                AnnotationStatusText.Text = _annotationNotice;
                return;
            }

            AnnotationStatusText.Foreground = _project.IsDirty ? AnnotationDirtyBrush : AnnotationNormalBrush;
            AnnotationStatusText.Text = _project.SaveStatusText;
            return;
        }

        if (!_viewModel.Document.HasDocument)
        {
            AnnotationStatusText.Text = string.Empty;
            return;
        }

        if (_annotationNotice.Length > 0)
        {
            AnnotationStatusText.Foreground = AnnotationWarningBrush;
            AnnotationStatusText.Text = _annotationNotice;
            return;
        }

        if (_autoSave.IsDirty)
        {
            AnnotationStatusText.Foreground = AnnotationDirtyBrush;
            AnnotationStatusText.Text = "● 批注未保存";
            return;
        }

        AnnotationStatusText.Foreground = AnnotationNormalBrush;
        AnnotationStatusText.Text = _annotatedSavedAt is { } time
            ? $"批注已保存 {time:HH:mm:ss}"
            : _annotationIdleText;
    }

    // ---------------------------------------------------------------- 工程（M8）

    /// <summary>
    /// 把工程状态刷到工具栏与状态栏。
    /// </summary>
    /// <remarks>
    /// 「关闭」按钮按当前模式分流（关工程 / 关试卷），所以它的可用态是<b>两者之一存在</b>即可 ——
    /// 一体机上没有鼠标悬停提示，一个点了没反应的按钮会被当成程序坏了。
    /// </remarks>
    private void UpdateProjectStatus()
    {
        ProjectText.Text = _project.DisplayText;
        ProjectText.Foreground = _project.IsDirty ? AnnotationDirtyBrush : AnnotationNormalBrush;
        CloseButton.IsEnabled = _project.HasProject || _viewModel.Document.HasDocument;
        UpdateAnnotationStatus();
    }

    private void OnProjectChanged(object? sender, EventArgs e) => UpdateProjectStatus();

    /// <summary>「保存」：有工程就存整个工程，没工程就只存批注（M7.x 的老行为）。</summary>
    private void SaveAll()
    {
        if (_project.HasProject) SaveProject(forceDialog: false);
        else SaveAnnotationsManually();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveAll();

    private void OnSaveProjectAsClick(object sender, RoutedEventArgs e) => SaveProjectAs();

    /// <summary>「另存为」：没工程时它等价于"建一个新工程"（内容照旧带过去）。</summary>
    private void SaveProjectAs() => SaveProject(forceDialog: true);

    /// <summary>
    /// 把当前试卷 + 批注 + 视图状态写成一个 <c>.twb</c> 工程文件。
    /// </summary>
    /// <param name="forceDialog">是否强制弹"另存为"对话框（另存为 / 尚未有路径时）。</param>
    /// <returns>是否成功落盘。关闭提示要靠它决定放不放行 —— 保存失败时绝不能放行。</returns>
    private bool SaveProject(bool forceDialog)
    {
        string? target = _project.FilePath;

        if (forceDialog || string.IsNullOrEmpty(target))
        {
            target = PickProjectSavePath(_project.HasProject
                ? _project.Title
                : ProjectComposer.DefaultTitle(_viewModel.Document.FilePath));

            if (string.IsNullOrEmpty(target)) return false;   // 老师取消了
        }

        _autoSave.Cancel();

        string? pdfPath = _viewModel.Document.FilePath;

        byte[]? pdfBytes = TryReadPdfBytes(pdfPath, out string readError);
        if (readError.Length > 0)
        {
            FailSave(readError);
            return false;
        }

        var manifest = BuildManifest(target!, pdfPath);
        byte[] tbinkBytes = ComposeTbinkBytes(pdfPath);

        // 写工程时顺手清掉同目录的 .tbink：批注已经进了工程，
        // 再留一个内容不同的侧车在旁边，下次"打开这份 PDF"会挑中它 ——
        // "看起来少了几笔"这种诡异现场比少一个备份文件难解释得多。
        var result = _projectStore.Save(target!, manifest, pdfBytes, tbinkBytes, pdfPath,
            ViewportHost.WebPanelImages.Snapshot());

        if (!result.Ok)
        {
            FailSave(result.Message);
            return false;
        }

        if (_project.HasProject) _project.ChangePath(target!);
        else _project.Apply(target!, manifest, pdfPath is not null);

        _project.MarkClean(DateTime.Now);
        _autoSave.MarkClean();
        _annotationNotice = string.Empty;
        _viewModel.SetStatus(result.Message);
        UpdateProjectStatus();
        return true;
    }

    // ---------------------------------------------------------------- 导出（M9）

    /// <remarks>
    /// 先把弹层收掉再去做事：导出要弹系统"另存为"对话框，
    /// 让我们的浮层压在系统对话框上会挡着它（而且看着像程序卡住了）。
    /// </remarks>
    private void OnExportPngClick(object sender, RoutedEventArgs e)
    {
        ExportPopup.IsOpen = false;
        ExportPng();
    }

    private void OnExportPdfClick(object sender, RoutedEventArgs e)
    {
        ExportPopup.IsOpen = false;
        ExportPdf();
    }

    /// <summary>
    /// 两个导出按钮的可用态：跟着"有没有打开的试卷"走。
    /// </summary>
    /// <remarks>
    /// 置灰而不是"点了再提示"—— 一体机上使用者只能靠外观判断状态，
    /// 一个点了没反应的按钮会被当成程序坏了。
    /// <para>
    /// ★ 两个按钮必须<b>一起</b>更新：只写一个的话，另加的那个会一直停在 XAML 里的
    /// <c>IsEnabled="False"</c> 上 —— 界面上它永远是灰的，而所有日志、断言都正常
    /// （因为导出逻辑本身没毛病），最难查的一类"新功能上不去"。
    /// </para>
    /// </remarks>
    private void UpdateExportButton()
    {
        bool can = _exporter.CanExport;
        ExportPngButton.IsEnabled = can;
        ExportPdfButton.IsEnabled = can;
    }

    /// <summary>
    /// 把整卷导出成一份多页 PDF（含批注与学科工具图形）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ExportPng"/> 同一套骨架，只有三处不同：
    /// <list type="bullet">
    ///   <item><description>选的是<b>文件名</b>（<see cref="PickExportPdfPath"/>）而不是目录 ——
    ///     整卷是<b>一个</b>文件，"存哪儿"与"叫啥"同样重要；</description></item>
    ///   <item><description>冒烟钩子是 <c>TB_EXPORT_PDF_PATH</c>（整条路径），
    ///     不是 S1 的 <c>TB_EXPORT_PNG_DIR</c>（目录）；</description></item>
    ///   <item><description>失败弹窗标题写「导出 PDF」，好让老师截图时能说清是哪一步。</description></item>
    /// </list>
    /// <para>
    /// ★ 导出前同样要把待保存的批注<b>冲一次盘</b>：导出的是"屏幕上这一刻"，
    /// 而自动保存有 2 秒防抖 —— 不冲的话"写完最后一笔立刻导出"会少最后一笔。
    /// </para>
    /// </remarks>
    private void ExportPdf()
    {
        if (_exporter.IsBusy) return;

        if (!_exporter.CanExport)
        {
            _viewModel.SetStatus("请先打开一份试卷，再导出。");
            return;
        }

        // 对话框的默认文件名先用一个临时的（真正的基名在冲盘之后再取一次，
        // 免得"刚新建的工程还没落盘、标题取到的是旧值"）
        string provisional = ExportNaming.BaseName(
            _project.HasProject, _project.Title, _viewModel.Document.FilePath);

        string? path = Environment.GetEnvironmentVariable("TB_EXPORT_PDF_PATH");

        if (string.IsNullOrWhiteSpace(path))
        {
            path = PickExportPdfPath(PdfWriter.FileName(provisional));
        }
        else
        {
            AppLog.Info($"[导出] 走 TB_EXPORT_PDF_PATH：{path}");
        }

        if (string.IsNullOrEmpty(path)) return;   // 老师取消了

        // 冲一次待保存内容：保证"导出所见 = 屏幕上所见"
        if (_project.HasProject) SaveProject(forceDialog: false);
        else SaveAnnotationsNow();

        string baseName = ExportNaming.BaseName(
            _project.HasProject, _project.Title, _viewModel.Document.FilePath);

        // 先把"置灰 + 提示"画出来，再开始长任务 —— 否则第一条反馈要等第一页渲完才出现
        ExportPdfButton.IsEnabled = false;
        _viewModel.SetStatus($"正在导出 {_exporter.PageCount} 页…");
        PumpOneFrame();

        try
        {
            var result = _exporter.ExportPdfTo(path!, baseName, (done, total) =>
            {
                _viewModel.SetStatus($"正在导出 {done}/{total} 页…");
                PumpOneFrame();
            });

            if (result.Cancelled) return;

            if (!result.Ok)
            {
                _viewModel.SetStatus("⚠ " + result.Message);
                AppLog.Warn($"[导出] {result.Message}");
                TDialog.Show(result.Message, "导出 PDF",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _viewModel.SetStatus(result.Message);
        }
        finally
        {
            UpdateExportButton();
        }
    }

    /// <summary>
    /// 把当前试卷的每一页各导出一张 PNG（含批注与学科工具图形）。
    /// </summary>
    /// <remarks>
    /// 导出目录的来源分两条路：
    /// <list type="bullet">
    ///   <item><description>正常：<see cref="PickExportFolder"/>（系统"选择文件夹"对话框）；</description></item>
    ///   <item><description>环境变量 <c>TB_EXPORT_PNG_DIR</c> 有值时<b>跳过对话框</b>直接用该目录 ——
    ///     这是给真机冒烟脚本留的钩子（文件对话框不是标准按钮窗口，跨进程点它没法自动化），
    ///     与 M7.5 的 <c>M75_GGB_*</c> 系列同一个套路。</description></item>
    /// </list>
    /// <para>
    /// ★ 导出前先把待保存的批注<b>冲一次盘</b>：导出的是"屏幕上这一刻"，而自动保存有 2 秒防抖 ——
    /// 不冲的话，老师"写完最后一笔立刻导出"会少最后一笔，而他只会以为是导出丢了东西（其实是还没存）。
    /// </para>
    /// <para>
    /// 导出期间界面必须活着：整卷要好几秒，中间不刷新与死机无异（老师会再点一次、或者强杀进程）。
    /// 这里靠状态栏 + 逐页"泵一帧消息"（见 <see cref="PumpOneFrame"/>）维持反馈；
    /// 按钮在进入循环前就置灰，所以泵帧期间也点不到第二次。
    /// </para>
    /// </remarks>
    private void ExportPng()
    {
        if (_exporter.IsBusy) return;

        if (!_exporter.CanExport)
        {
            _viewModel.SetStatus("请先打开一份试卷，再导出。");
            return;
        }

        string? directory = Environment.GetEnvironmentVariable("TB_EXPORT_PNG_DIR");

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = PickExportFolder();
        }
        else
        {
            AppLog.Info($"[导出] 走 TB_EXPORT_PNG_DIR：{directory}");
        }

        if (string.IsNullOrEmpty(directory)) return;   // 老师取消了

        // 冲一次待保存内容：保证"导出所见 = 屏幕上所见"
        if (_project.HasProject) SaveProject(forceDialog: false);
        else SaveAnnotationsNow();

        string baseName = ExportNaming.BaseName(
            _project.HasProject, _project.Title, _viewModel.Document.FilePath);

        // 先把"置灰 + 提示"画出来，再开始长任务 —— 否则第一条反馈要等第一页渲完才出现
        ExportPngButton.IsEnabled = false;
        _viewModel.SetStatus($"正在导出 {_exporter.PageCount} 页…");
        PumpOneFrame();

        try
        {
            var result = _exporter.ExportPngTo(directory!, baseName, (done, total) =>
            {
                _viewModel.SetStatus($"正在导出 {done}/{total} 页…");
                PumpOneFrame();
            });

            if (result.Cancelled) return;

            if (!result.Ok)
            {
                _viewModel.SetStatus("⚠ " + result.Message);
                AppLog.Warn($"[导出] {result.Message}");
                TDialog.Show(result.Message, "导出 PNG",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _viewModel.SetStatus(result.Message);
        }
        finally
        {
            UpdateExportButton();
        }
    }

    /// <summary>
    /// 让界面在<b>同步长任务</b>中间刷一帧。
    /// </summary>
    /// <remarks>
    /// 这不是"睡一下"：把一个 <c>Background</c> 优先级的空操作排进消息队列，
    /// 然后泵消息循环直到它被执行 —— 期间所有优先级更高的绘制与布局都会跑完
    /// （状态栏因此真的画到屏幕上，而不是等整个导出结束才一起出现）。
    /// <para>
    /// ★ 它会<b>允许重入</b>（老师此刻仍能点到别的东西），所以只能在"入口按钮已置灰、
    /// 且外部还有 <c>IsBusy</c> 挡着"的前提下用 —— 这里正是这种情形。
    /// </para>
    /// </remarks>
    private void PumpOneFrame()
    {
        var frame = new DispatcherFrame();

        Dispatcher.BeginInvoke(DispatcherPriority.Background,
                               new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>弹"选择导出位置"对话框；取消时返回 <c>null</c>。</summary>
    /// <remarks>
    /// 导 PNG 是"一页一个文件"，所以选<b>目录</b>而不是文件名 ——
    /// 对老师来说"存哪儿"比"叫啥"重要，文件名由程序按 <c>试卷名-第NN页.png</c> 统一生成。
    /// </remarks>
    private static string? PickExportFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择导出位置（每一页会生成一张 PNG）",
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>弹"导出整卷 PDF 存到哪"对话框；取消时返回 <c>null</c>。</summary>
    /// <remarks>
    /// 与导 PNG 相反，这里选的是<b>文件</b>：整卷就是一页一份的多页 PDF，
    /// 老师会直接把它发给打印店或群里，文件名是他要认的东西。
    /// <para>
    /// <c>OverwritePrompt = true</c>：整卷 PDF 可能是"批注完的最终版"，覆盖前必须问一次。
    /// 导 PNG 那边不问我 —— 那边是"一页一个文件、重导就更新"，问十次反而烦。
    /// </para>
    /// </remarks>
    private static string? PickExportPdfPath(string defaultFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出整卷 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            FileName = defaultFileName,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// 新建工程。
    /// </summary>
    /// <remarks>
    /// <b>以当前打开的试卷为基础</b>建工程（有 PDF 就绑定它、默认内嵌），没有 PDF 时建空白工程 ——
    /// 老师的动线本来就是"先把卷子找出来，再决定把它存成工程"。
    /// 屏幕上的内容不清空：已经写过的东西随第一次保存一起进工程，
    /// 免得"我只是想存个项目，结果板书全没了"。
    /// </remarks>
    private void NewProject()
    {
        if (!ConfirmDiscardCurrentProject()) return;

        string? pdfPath = _viewModel.Document.FilePath;
        string? target = PickProjectSavePath(ProjectComposer.DefaultTitle(pdfPath));
        if (string.IsNullOrEmpty(target)) return;

        _autoSave.Cancel();

        byte[]? pdfBytes = TryReadPdfBytes(pdfPath, out string readError);
        if (readError.Length > 0)
        {
            FailSave(readError);
            return;
        }

        var manifest = BuildManifest(target!, pdfPath);
        byte[] tbinkBytes = ComposeTbinkBytes(pdfPath);

        var result = _projectStore.Save(target!, manifest, pdfBytes, tbinkBytes, pdfPath,
            ViewportHost.WebPanelImages.Snapshot());
        if (!result.Ok)
        {
            FailSave(result.Message);
            return;
        }

        _project.Apply(target!, manifest, pdfPath is not null);
        _project.MarkClean(DateTime.Now);
        _autoSave.MarkClean();
        _annotationNotice = string.Empty;
        _viewModel.SetStatus(result.Message);
        UpdateProjectStatus();
    }

    private void OnNewProjectClick(object sender, RoutedEventArgs e) => NewProject();

    private void OnOpenProjectClick(object sender, RoutedEventArgs e) => OpenProjectFromDialog();

    private void OpenProjectFromDialog()
    {
        if (!ConfirmDiscardCurrentProject()) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开白板工程",
            Filter = "白板工程 (*.twb)|*.twb|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;
        OpenProject(dialog.FileName);
    }

    /// <summary>
    /// 打开一个 <c>.twb</c> 工程（含"外挂试卷不在原位"时的定位交互）。
    /// </summary>
    private void OpenProject(string twbPath)
    {
        var result = _projectStore.Load(twbPath);

        if (result.Status == ProjectLoadStatus.Rejected)
        {
            // 打不开就保持现状：老师手里的东西一点没动，比"开一半"好得多
            AppLog.Warn($"工程装载被拒绝：{twbPath} —— {result.Message}");
            TDialog.Show(result.Message, "数理墨", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (result.Status == ProjectLoadStatus.MissingPdf)
        {
            string? located = AskForPdfLocation(twbPath);
            if (string.IsNullOrEmpty(located)) return;

            var resolution = _projectStore.ResolveReferencedPdf(
                result.Manifest!, System.IO.Path.GetDirectoryName(twbPath), located);

            if (!resolution.Ok)
            {
                TDialog.Show(resolution.Message, "数理墨", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ★ 只补路径，别重新 Load：重新读一遍会把已经解析出来的批注字节丢掉，
            //   于是"试卷挪了个位置"就变成"批注全没了"。
            result = result with
            {
                Ok = true,
                PdfPath = resolution.Path,
                HasPdf = true,
                Message = resolution.Message,
                IsWarning = !resolution.FingerprintMatches,
                Status = resolution.FingerprintMatches
                    ? ProjectLoadStatus.Loaded
                    : ProjectLoadStatus.LoadedWithMismatch,
            };
        }

        ApplyLoadedProject(twbPath, result);
    }

    /// <summary>把装载出来的工程应用到界面上：试卷 → 批注 → 视图位置 → 状态。</summary>
    private void ApplyLoadedProject(string twbPath, ProjectLoadResult result)
    {
        _autoSave.Cancel();
        _autoSave.IsSuspended = true;
        _loadingProject = true;

        try
        {
            string? pdfPathForAnnotations = null;
            string? mismatchNotice = result.IsWarning ? result.Message : null;

            // 1) 先落试卷
            if (result.EmbeddedPdf is { Length: > 0 })
            {
                pdfPathForAnnotations = _embeddedReleaser.Release(twbPath, result.EmbeddedPdf);

                if (pdfPathForAnnotations is null)
                {
                    // ★ 释放失败（临时目录不可写 / 磁盘满 / 被安全软件锁住）时，
                    //   工程仍然算"打开了"（批注与视图都在），但**必须把话说出来**：
                    //   否则老师看到的是一片空白画布，只会以为工程文件坏了，
                    //   而真正的原因（临时目录写不进去）谁也不会想到。
                    _viewModel.CloseDocument();
                    mismatchNotice = "⚠ 无法把工程里的试卷释放到临时文件（临时目录不可写或磁盘已满），"
                                     + "批注已恢复，试卷请稍后重新打开本工程再试。";
                }
                else
                {
                    _viewModel.OpenDocument(pdfPathForAnnotations);
                }
            }
            else if (!string.IsNullOrEmpty(result.PdfPath))
            {
                pdfPathForAnnotations = result.PdfPath;
                _viewModel.OpenDocument(result.PdfPath!);
            }
            else
            {
                // 空白工程：把画布清空，但保留工程 —— 老师可以接着写或插试卷
                _viewModel.CloseDocument();
            }

            // 2) 再灌批注（来自 .twb 里的字节，不是磁盘侧车）
            //    ★ 位图必须排在批注之前：对象装回来时渲染器会去仓库找字节，
            //      晚一步的结果是"图没了、只剩一个缺失占位框"，而对象本身一切正常。
            ViewportHost.ReplaceWebPanelImages(result.Images);

            _pendingProjectAnnotationBytes = result.TbinkBytes;
            string? restoredNotice = LoadProjectAnnotations(pdfPathForAnnotations);
            if (restoredNotice is { Length: > 0 }) mismatchNotice = restoredNotice;

            // 3) 最后复原视图位置（必须排在页面装好之后，否则会被"适配宽度"覆盖）
            RestoreViewState(result.Manifest?.View);

            // 4) 状态收口
            _annotatedPath = pdfPathForAnnotations;
            _project.Apply(twbPath, result.Manifest!, result.HasPdf);
            _project.MarkClean(DateTime.Now);
            _annotationNotice = mismatchNotice ?? string.Empty;
            _autoSave.MarkClean();
            _viewModel.SetStatus("工程已打开：" + _project.FileName);

            // 日志与状态栏说同一件事：这两行是现场排查"到底开了哪个工程"的唯一线索，
            // 只写状态栏的话，老师把日志发回来时这一段是空的。
            AppLog.Info($"工程已打开：{twbPath}（{_project.EmbeddingText}；"
                        + $"批注 {result.TbinkBytes.Length} 字节；"
                        + $"内嵌试卷 {(result.EmbeddedPdf is { Length: > 0 } ? result.EmbeddedPdf.Length : 0)} 字节；"
                        + $"仿真截图 {result.Images.Count} 张）");
        }
        finally
        {
            _loadingProject = false;
            _autoSave.IsSuspended = false;
            _autoSave.MarkClean();
        }

        UpdateInkCount();
        UpdateProjectStatus();
    }

    /// <summary>把工程里的批注字节灌进画布；返回给用户看的提醒（没有则为 <c>null</c>）。</summary>
    private string? LoadProjectAnnotations(string? fingerprintPath)
    {
        byte[]? payload = _pendingProjectAnnotationBytes;
        _pendingProjectAnnotationBytes = null;

        var result = _annotationStore.LoadFromBytes(
            payload,
            ViewportHost.Layout.PageRects,
            ViewportHost.Layout.PageMargin,
            ViewportHost.Layout.PageGap,
            fingerprintPath);

        switch (result.Status)
        {
            case AnnotationLoadStatus.Loaded:
            case AnnotationLoadStatus.LoadedWithMismatch:
                ViewportHost.ReplaceStrokes(result.Strokes!);
                ViewportHost.ReplaceObjects(result.Objects);
                _annotationIdleText = DescribeRestored(result.Strokes!.Count, result.Objects?.Count ?? 0, out string missing);

                // 插件被禁用时图形只能画成虚线占位，这件事必须说出来（与侧车模式同一处理）
                if (missing.Length > 0) return missing;
                return result.IsWarning ? result.Message : null;

            case AnnotationLoadStatus.Rejected:
                _annotationIdleText = "无批注";
                return "⚠ " + result.Message;

            default:
                _annotationIdleText = "无批注";
                return null;
        }
    }

    /// <summary>
    /// 复原保存时的视口位置。
    /// </summary>
    /// <remarks>
    /// 必须在文档装好之后调用 —— 装文档会重置视口并"适配宽度"，
    /// 先设视口会被那一步覆盖，表现成"打开工程永远回到第 1 页顶部"。
    /// <para>
    /// 退出"自动适应宽度"这一步在 <see cref="CanvasViewportHost.RestoreView"/> 里做：
    /// 光调 <c>SetView</c> 不改那个开关，窗口下一次改变尺寸时照样把位置算掉。
    /// </para>
    /// </remarks>
    private void RestoreViewState(TwbViewState? view)
    {
        if (view is null) return;

        bool restored = ViewportHost.RestoreView(view.Scale, view.OffsetX, view.OffsetY);

        AppLog.Info(restored
            ? $"工程视图已复原：{view.Scale * 100:F0}%（{view.OffsetX:F0},{view.OffsetY:F0}），第 {view.PageIndex + 1} 页"
            : $"工程视图未复原（存档位置看不到试卷），已退回「适应宽度」；存档第 {view.PageIndex + 1} 页");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseCurrent();

    /// <summary>「关闭」：有工程时关工程，否则关试卷。</summary>
    private void CloseCurrent()
    {
        if (_project.HasProject)
        {
            if (!ConfirmDiscardCurrentProject()) return;

            _autoSave.Cancel();

            // 顺序要紧：先清工程标记，再关文档 ——
            // 关文档会触发 OnDocumentChanged，那时若工程还在，就会按工程模式去取批注。
            _project.Clear();
            _annotatedPath = null;
            _annotationNotice = string.Empty;
            _annotationIdleText = "无批注";
            _viewModel.CloseDocument();
            UpdateProjectStatus();
            _viewModel.SetStatus("已关闭工程。");
            AppLog.Info("已关闭工程。");
            return;
        }

        _viewModel.CloseCommand.Execute(null);
    }

    /// <summary>
    /// 当前工程有未保存改动时问一句；返回 <c>false</c> 表示"这次动作别做了"。
    /// </summary>
    /// <remarks>
    /// 关窗口（<see cref="OnClosing"/>）与"新建 / 打开 / 关闭工程"共用这一处判断 ——
    /// 各写一遍的话早晚会出现"从这个入口走会提示、从那个入口走不提示"的漏网，
    /// 而漏网的代价就是老师一整节课的板书。
    /// </remarks>
    private bool ConfirmDiscardCurrentProject()
    {
        // 「什么时候问」与「问完放不放行」都在 ProjectClosePolicy 里（harness 断言在那儿）
        if (!ProjectClosePolicy.NeedsPrompt(_project.HasProject, _project.IsDirty)) return true;

        var answer = TDialog.Show(
            ProjectClosePolicy.PromptText(_project.FileName),
            "数理墨", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        var choice = ProjectClosePolicy.FromMessageBox(answer);

        switch (choice)
        {
            case ProjectCloseChoice.Discard:
                // 明确丢弃：清掉脏标记，后面的关闭 / 退出流程就按"干净"走
                AppLog.Info($"关闭时明确丢弃工程「{_project.FileName}」的未保存改动。");
                _project.MarkClean(DateTime.Now);
                break;

            case ProjectCloseChoice.Save:
            {
                bool saved = SaveProject(forceDialog: false);

                // 这一行是现场争议（"我明明点了保存"）唯一能查的东西
                AppLog.Info(saved
                    ? $"关闭前已保存工程「{_project.FileName}」。"
                    : "关闭前保存失败，这次动作已中止（改动还在）。");

                return ProjectClosePolicy.ShouldProceed(choice, saved);
            }

            default:
                AppLog.Info($"这次动作被取消（工程「{_project.FileName}」仍有未保存改动）。");
                break;
        }

        return ProjectClosePolicy.ShouldProceed(choice, saveSucceeded: false);
    }

    /// <summary>组装当前状态下该写进工程的元信息。</summary>
    private TwbManifest BuildManifest(string projectPath, string? pdfPath)
    {
        var viewport = ViewportHost.Viewport;

        // 视口中心的世界 Y —— 用来反推"现在看的是第几页"（只进诊断字段）
        double centerWorldY = viewport
            .ToWorld(new Point(ViewportHost.ActualWidth / 2.0, ViewportHost.ActualHeight / 2.0)).Y;

        var view = new TwbViewState
        {
            PageIndex = ProjectComposer.ResolvePageIndex(ViewportHost.Layout.PageRects, centerWorldY),
            Scale = viewport.Scale,
            OffsetX = viewport.OffsetX,
            OffsetY = viewport.OffsetY,
            IsFullScreen = _isFullScreen,
        };

        string title = string.IsNullOrEmpty(_project.Title)
            ? ProjectComposer.DefaultTitle(pdfPath)
            : _project.Title;

        return ProjectComposer.BuildManifest(
            title,
            projectPath,
            pdfPath,
            WantsEmbed(),
            view,
            view.PageIndex,
            ViewportHost.StrokeCount,
            ViewportHost.GfxObjectCount,
            _project.CreatedUtc);
    }

    /// <summary>
    /// 这份工程要不要把试卷字节装进去。
    /// </summary>
    /// <remarks>
    /// 只有"打开的就是外挂工程"才继续外挂 —— 其余一律内嵌。
    /// 内嵌是默认，因为它对应老师最想要的承诺：<b>拷走一个文件，试卷和批注一起走</b>。
    /// </remarks>
    private bool WantsEmbed()
        => !string.Equals(_project.Embedding, TwbPdfEmbedding.Referenced, StringComparison.Ordinal);

    /// <summary>内嵌模式要读整份试卷；读完给出可读错误（空串表示没问题）。</summary>
    private byte[]? TryReadPdfBytes(string? pdfPath, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath)) return null;   // 空白工程
        if (!WantsEmbed()) return null;                                            // 外挂工程

        try
        {
            return File.ReadAllBytes(pdfPath!);
        }
        catch (Exception ex)
        {
            // 读不到卷面就必须当场中止：否则会写出一个"标记内嵌却没有卷面"的工程，
            // 而读侧会把它判成损坏 —— 等于把老师的成果存成了废文件。
            error = $"读取试卷失败，工程未保存：{ex.Message}";
            return null;
        }
    }

    /// <summary>把当前笔迹与图形按 .tbink v2 协议序列化成字节（装进工程包用）。</summary>
    private byte[] ComposeTbinkBytes(string? pdfPath)
        => AnnotationStore.ComposeBytes(
            pdfPath,
            ViewportHost.SnapshotStrokes(),
            ViewportHost.Layout.PageRects,
            ViewportHost.Layout.PageMargin,
            ViewportHost.Layout.PageGap,
            ViewportHost.SnapshotObjects());

    /// <summary>手动保存失败：状态栏黄字 + 一次弹窗。</summary>
    /// <remarks>
    /// 自动保存失败不该弹窗（每 2 秒一次会把课堂变成打地鼠），但<b>手动</b>保存失败必须弹 ——
    /// 老师刚按下保存、以为存住了，这是最不能含糊的一种失败。
    /// </remarks>
    private void FailSave(string message)
    {
        AppLog.Warn(message);
        _annotationNotice = "⚠ " + message;
        UpdateAnnotationStatus();
        TDialog.Show(message, "数理墨", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>弹"保存工程"对话框；取消时返回 <c>null</c>。</summary>
    private static string? PickProjectSavePath(string defaultTitle)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存白板工程",
            Filter = "白板工程 (*.twb)|*.twb|所有文件 (*.*)|*.*",
            DefaultExt = ".twb",
            FileName = SafeFileName(defaultTitle) + ".twb",
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>弹"定位试卷"对话框；取消时返回 <c>null</c>。</summary>
    private static string? AskForPdfLocation(string twbPath)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "找不到工程引用的试卷，请指出这份 PDF 在哪里",
            Filter = "PDF 文件 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = System.IO.Path.GetDirectoryName(twbPath),
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// 工程"另存为"的默认文件名（去非法字符）。
    /// </summary>
    /// <remarks>
    /// 实现交给 <see cref="ExportNaming.Safe(string?, string)"/> —— 导出成品的基名
    /// 也走同一套清洗。"两处各写一遍清洗"早晚分叉成两种行为，
    /// 而那是只有老师撞上才会发现的一类不一致。
    /// </remarks>
    private static string SafeFileName(string? name)
        => ExportNaming.Safe(name, ExportNaming.FallbackProjectName);

    // ---------------------------------------------------------------- 全屏

    /// <summary>
    /// 进入全屏覆盖。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不用</b> <c>WindowState.Maximized</c>：WPF 计算最大化尺寸时仍按"带边框"的
    /// 窗口矩形来算，右边和底下会各被裁掉几个像素 —— 讲评时页面边缘的内容正好落在那一条里。
    /// 直接铺满主屏尺寸，是唯一能保证"一个像素都不浪费"的写法。
    /// <para>
    /// 用 <c>PrimaryScreenWidth/Height</c> 而不是 <c>WorkArea</c>：后者会扣掉任务栏，
    /// 而"全屏覆盖"要的正是连任务栏一起盖住。
    /// </para>
    /// </remarks>
    private void EnterFullScreen()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Normal;

        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        _isFullScreen = true;
        UpdateWindowModeButton();
        UiStateStore.WriteFullScreen(true);

        // 一体机上离得远，"以为全屏了、其实右边缺一条"这种事只有数字能看出来。
        // 本机也靠它验证：窗口尺寸必须与主屏尺寸逐个相等。
        AppLog.Info($"全屏覆盖：窗口 ({Left:F0},{Top:F0}) {Width:F0}×{Height:F0}"
                    + $"　主屏 {SystemParameters.PrimaryScreenWidth:F0}×{SystemParameters.PrimaryScreenHeight:F0}");
    }

    /// <summary>退出全屏：拿回标题栏（于是也有"关闭"按钮）与可拖动、可缩放的边框。</summary>
    private void ExitFullScreen()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;

        WindowState = WindowState.Maximized;
        _isFullScreen = false;
        UpdateWindowModeButton();
        UiStateStore.WriteFullScreen(false);

        // 和全屏那行日志成对：现场拿到日志时，"这次启动是哪种模式"必须一眼可判 ——
        // "怎么又全屏了"这类问题，答案就在这两行里。
        AppLog.Info("窗口模式：窗口（最大化，带标题栏，任务栏可见）");
    }

    private void ToggleFullScreen()
    {
        if (_isFullScreen) ExitFullScreen();
        else EnterFullScreen();
    }

    /// <summary>界面上的窗口模式切换按钮（右上角常驻）。</summary>
    private void OnToggleWindowModeClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    /// <summary>
    /// 把当前窗口模式写回按钮文案与提示。
    /// </summary>
    /// <remarks>
    /// 文案写的是"<b>点它会变成什么</b>"，而不是"现在是什么"：
    /// 全屏时没有标题栏，老师要找的是出路，不是状态说明。
    /// 而且要一眼看懂 —— 别让人去猜"窗口模式"是当前状态还是待切目标。
    /// </remarks>
    private void UpdateWindowModeButton()
    {
        WindowModeText.Text = _isFullScreen ? "窗口模式" : "全屏";

        // ★ M20 S3：图标跟着**待切目标**走（与文案同一口径）——
        //   全屏时显示「窗口」（出路是回到窗口），窗口时显示「四角展开」（去处是满屏）。
        WindowModeIcon.Data = (Geometry)FindResource(
            _isFullScreen ? "Icon.Window" : "Icon.FullScreen");
        WindowModeButton.ToolTip = _isFullScreen
            ? "退出满屏覆盖，回到带标题栏的窗口（快捷键 F11，Esc 也可）"
            : "满屏覆盖整个屏幕，连任务栏一起盖住（快捷键 F11）";
    }

    // ================================================================ M12 S5：命令总线

    /// <summary>
    /// 把窗口里所有"能做的事"登记成命令。悬浮球菜单、自定义宏、工作台模式
    /// 三者只认命令 Id，全部经由总线执行 —— 这样新增能力只要登记一次，
    /// 三个入口自动都能用。
    /// </summary>
    /// <remarks>
    /// 按钮 Click 的老路径原样保留：总线是<b>多一个</b>入口，不替代任何既有路径。
    /// 重复登记同一个 Id 会抛异常（总线约定），所以这里必须只跑一次。
    /// </remarks>
    private void RegisterCommands()
    {
        // ---- 文件 ----
        _commands.Register("file.open_pdf", () => _viewModel.OpenCommand.Execute(null));
        _commands.Register("file.open_project", OpenProjectFromDialog);
        _commands.Register("file.new_project", NewProject);
        _commands.Register("file.close", CloseCurrent);
        _commands.Register("file.save", SaveAll);
        _commands.Register("file.save_as", SaveProjectAs);
        _commands.Register("file.export_png", ExportPng);
        _commands.Register("file.export_pdf", ExportPdf);

        // ---- 视图 ----
        _commands.Register("view.fit_width", ViewportHost.FitWidth);
        _commands.Register("view.actual_size", ViewportHost.ActualSize);
        _commands.Register("view.zoom_in", ViewportHost.ZoomIn);
        _commands.Register("view.zoom_out", ViewportHost.ZoomOut);
        _commands.Register("view.toggle_theme", () => OnToggleThemeClick(this, new RoutedEventArgs()));
        _commands.Register("view.toggle_fullscreen", ToggleFullScreen);

        // ---- 工具栏显隐（S1）----
        _commands.Register("toolbar.hide", () => SetToolbarHidden(true));
        _commands.Register("toolbar.show", () => SetToolbarHidden(false));
        _commands.Register("toolbar.toggle", ToggleToolbar);

        // ---- 计算器（M13 S5）----
        // ★ 设计里它的入口就是"总线 + 工具栏按钮 + 球菜单第 6 项"（docs/18 §S5），
        //   但早先只落了后两者：球菜单照写 calc.open、总线却没登记它 ⇒ 点计算器毫无反应。
        //   open / close 语义分明（幂等），toggle 给"按一下翻个面"的按钮与球菜单用自己的那条。
        _commands.Register("calc.open", ViewportHost.ShowCalculator);
        _commands.Register("calc.close", ViewportHost.HideCalculator);
        _commands.Register("calc.toggle", ViewportHost.ToggleCalculator);

        // ---- 工具：按注册表逐个登记（M7 的纪律：宿主不写死任何工具名）----
        foreach (var tool in ViewportHost.Tools)
        {
            string toolId = tool.Id;   // 闭包捕获：循环变量必须先落到局部
            _commands.Register("tool." + toolId, () =>
            {
                ViewportHost.SetTool(toolId);
                RefreshToolButtons();
            });
        }

        // ---- 墨色与笔宽：档位随调色板 ----
        for (int i = 0; i < _colorButtons.Length; i++)
        {
            int colorIndex = i;
            _commands.Register($"ink.color.{colorIndex}", () => SelectColor(colorIndex));
        }
        _commands.Register("ink.color.default", () => SelectColor(InkPalette.DefaultColorIndex));
        for (int i = 0; i < _widthButtons.Length; i++)
        {
            int widthIndex = i;
            _commands.Register($"ink.width.{widthIndex}", () => SelectWidth(widthIndex));
        }

        // ---- 编辑与历史 ----
        _commands.Register("edit.undo", () => ViewportHost.Undo());
        _commands.Register("edit.redo", () => ViewportHost.Redo());
        _commands.Register("edit.clear_ink", () => OnClearInkClick(this, new RoutedEventArgs()));

        // ---- 翻页 ----
        _commands.Register("page.prev", () => ViewportHost.GoToPageRelative(-1));
        _commands.Register("page.next", () => ViewportHost.GoToPageRelative(1));

        // ---- 图层（S3/M10）----
        _commands.Register("layer.panel", () => ViewportHost.ShowLayerPanel(true));
        _commands.Register("layer.active.annotation", () => SetActiveInkLayerUi(InkLayers.AnnotationId));
        _commands.Register("layer.active.draft", () => SetActiveInkLayerUi(InkLayers.DraftId));
        _commands.Register("layer.show.draft", () => ViewportHost.SetInkLayerVisible(InkLayers.DraftId, true));
        _commands.Register("layer.hide.draft", () => ViewportHost.SetInkLayerVisible(InkLayers.DraftId, false));
        _commands.Register("layer.clear.draft", () => ViewportHost.ClearDraftLayer());

        // ---- 题号标记（S4）----
        _commands.Register("mark.prev", () => GoToQuestionMarkRelative(-1));
        _commands.Register("mark.next", () => GoToQuestionMarkRelative(1));

        // ---- 笔迹回放（S6）----
        _commands.Register("replay.start", StartReplayIfAvailable);
        _commands.Register("replay.end", ViewportHost.EndReplay);

        // ---- 悬浮球显示策略（S2）----
        _commands.Register("ball.mode.auto", () => ApplyBallMode("auto"));
        _commands.Register("ball.mode.always", () => ApplyBallMode("always"));
        _commands.Register("ball.mode.off", () => ApplyBallMode("off"));

        // ---- 工作台模式（S5）----
        _commands.Register("mode.grading", () => ApplyMode(BoardModes.Grading));
        _commands.Register("mode.review", () => ApplyMode(BoardModes.Review));
        _commands.Register("mode.present", () => ApplyMode(BoardModes.Present));
    }

    /// <summary>
    /// 命令自检：球菜单与内置模式引用的 Id，必须都已在总线上登记。
    /// </summary>
    /// <remarks>
    /// ★ 这条检查是被一次真实事故逼出来的：M13 S5 的设计写明计算器入口是"总线 calc.open"，
    /// 球菜单照写了，总线却从未登记 —— 表现是"点计算器毫无反应"，日志、状态栏、
    /// 当时的源级断言（只查字符串在不在源码里）三处全是静的。
    /// 所以判据必须是"登记完成之后真去问一次总线"。
    /// <para>
    /// 处理方式与设计令牌的 <c>MissingKeys</c> 同一哲学：<b>不崩、不弹窗，但必须留痕</b> ——
    /// 认不出的命令只在老师点了之后才暴露，那时人已经在课堂上，日志是唯一的证据。
    /// </para>
    /// </remarks>
    private void AuditCommandIds()
    {
        var expected = new List<string>(BallMenuItems.Length + 16);

        foreach (var (_, commandId) in BallMenuItems) expected.Add(commandId);
        foreach (var mode in BoardModes.All) expected.AddRange(mode.Commands);

        var missing = _commands.Missing(expected);
        if (missing.Count == 0) return;

        AppLog.Error("命令自检未通过，以下动作点了不会有反应：" + string.Join("、", missing));
        _viewModel.SetStatus("程序自检发现问题：部分菜单动作没接上（详见日志）。");
    }

    // ================================================================ M12 S2：悬浮球

    /// <summary>
    /// 悬浮球装配：恢复上次的画布内位置与显示策略，装配固定菜单，挂好回调。
    /// </summary>
    private void InitFloatingBall()
    {
        FloatingBallHost.XRatio = UiStateStore.ReadBallX() ?? 0.9;
        FloatingBallHost.YRatio = UiStateStore.ReadBallY() ?? 0.5;
        FloatingBallHost.PositionCommitted += (_, pos) =>
        {
            UiStateStore.WriteBallX(pos.X);
            UiStateStore.WriteBallY(pos.Y);
        };

        _ballMode = UiStateStore.ReadBallMode() ?? "auto";
        BuildBallMenu();
        UpdateFloatingBallVisibility();
    }

    /// <summary>
    /// 悬浮球菜单的固定 7 项（M13）：(显示文字, 命令 Id)。
    /// </summary>
    /// <remarks>
    /// ★ 标签与 Id 各只写一遍，菜单就照这张表装配 —— 于是"菜单上有的"与"自检查的"
    /// 在结构上不可能对不上（早先两边各写一遍，Id 写错一处谁都不知道）。
    /// 顺序就是讲台上最常用的那几下：笔 / 橡皮 / 手 / 选择 / 直尺（自由角）/ 计算器 / 工具栏显隐。
    /// </remarks>
    private static readonly (string Label, string CommandId)[] BallMenuItems =
    {
        ("笔", "tool.pen"),
        ("橡皮", "tool.eraser"),
        ("手", "tool.hand"),
        ("选择", "tool.gfx-select"),
        ("直尺（自由角）", "tool.ruler-free"),
        ("计算器", "calc.open"),
        ("显示 / 隐藏工具栏", "toolbar.toggle"),
    };

    /// <summary>
    /// 装配悬浮球菜单（M13 固定 7 项，讲台上最常用的那几下）。
    /// 不再随工具栏状态或宏文件重建 —— 菜单稳定，肌肉记忆才立得住。
    /// </summary>
    private void BuildBallMenu()
    {
        var items = new List<(string Label, Action Run)>(BallMenuItems.Length);

        foreach (var (label, commandId) in BallMenuItems)
        {
            items.Add((label, () => RunBallCommand(label, commandId)));
        }

        FloatingBallHost.SetMenuItems(items);
    }

    /// <summary>
    /// 球菜单执行一个总线命令；切工具时顺带刷新工具栏高亮。
    /// </summary>
    /// <remarks>
    /// ★ 认不出的 Id <b>必须留痕</b>。踩过：M13 S5 的设计里计算器入口是"总线 <c>calc.open</c>"，
    /// 球菜单照写了，但总线从未登记过它 —— 表现是"点计算器毫无反应"，
    /// 而当时回归里那条源级断言只查字符串存不存在，照样 PASS。
    /// 现在失败会同时进日志与状态栏（启动自检 <see cref="AuditCommandIds"/> 还会再兜一道）。
    /// </remarks>
    private void RunBallCommand(string label, string commandId)
    {
        if (!_commands.Execute(commandId))
        {
            AppLog.Warn($"悬浮球菜单「{label}」的命令未登记，动作被跳过：{commandId}");
            _viewModel.SetStatus($"「{label}」暂时不可用（命令 {commandId} 未登记，请反馈）。");
            return;
        }

        // 工具命令额外刷新一遍按钮高亮（与工具栏按钮的行为一致）
        if (commandId.StartsWith("tool.", StringComparison.Ordinal)) RefreshToolButtons();
    }

    /// <summary>切换悬浮球显示策略并落盘（auto / always / off）。</summary>
    private void ApplyBallMode(string mode)
    {
        _ballMode = mode;
        UiStateStore.WriteBallMode(mode);
        UpdateFloatingBallVisibility();
        _viewModel.SetStatus(mode switch
        {
            "always" => "悬浮球：始终显示。",
            "off" => "悬浮球：已关闭。",
            _ => "悬浮球：跟随工具栏（工具栏藏起来时出现）。",
        });
    }

    /// <summary>
    /// 悬浮球可见性：always 恒显、off 恒隐、auto 跟随工具栏 ——
    /// 藏了工具栏的老师永远留着一颗球，不然就只剩键盘能救场（S1/S2 的配套约束）。
    /// </summary>
    private void UpdateFloatingBallVisibility()
    {
        FloatingBallHost.Visibility = _ballMode switch
        {
            "always" => Visibility.Visible,
            "off" => Visibility.Collapsed,
            _ => _toolbarHidden ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    // ================================================================ M12 S1：工具栏三态

    // ================================================================ M13 S2：纸张底色

    /// <summary>纸张预设 Id → 颜色（来源唯一：Canvas 色板令牌）。</summary>
    /// <remarks>
    /// 兜底分支给白：认不出的 Id（存档被改坏、将来删档）不许变成一片黑 ——
    /// 黑底是最不像"出错了"的一种值，老师会把渲染失败当成故意设的黑板。
    /// </remarks>
    private static Color PaperPresetColor(string presetId) => presetId switch
    {
        "cream" => Tokens.Color("Canvas.PageCream"),
        "green" => Tokens.Color("Canvas.PageGreen"),
        "blue" => Tokens.Color("Canvas.PageBlue"),
        "black" => Tokens.Color("Canvas.PageBlack"),
        _ => Tokens.Color("Canvas.PageWhite"),
    };

    /// <summary>
    /// 启动恢复：读持久化的纸张底色，<b>没记录过时出厂默认深灰（黑板）</b>（M21）。
    /// </summary>
    /// <remarks>
    /// ★ "默认"在这里只兜<b>从没选过底色</b>的老师（<c>ReadPaperColor</c> 返回 null）。
    /// 已经手动点过白的机器读到的是 "white"，升级后<b>仍是白</b> —— 尊重显式选择，
    /// 不因为换了出厂值就把人家的设置覆盖掉。
    /// <para>
    /// 变的是<b>应用层</b>的默认，不是画布组件的出厂值：<c>CanvasViewportHost._paperBrush</c>
    /// 仍初始化为白纸（harness 的"页外纯白"整纸断言依赖它）。这样焊在组件层的语义不变，
    /// 只有"新装的机器开机是什么颜色"这一条变了。
    /// </para>
    /// </remarks>
    private void InitPaperColor()
    {
        string presetId = UiStateStore.ReadPaperColor() ?? "black";

        // M23 一次性迁移：M21 之前出厂默认是白，而旧版程序自己也会把当时的默认值
        // 写进偏好文件 —— 于是很多从没选过色的老师，文件里躺着 white，升级后被当成
        // 「显式选择」一直开着白纸。迁移只做一次：white 且未迁移 ⇒ 改深灰并落盘；
        // 之后老师再手动点白，标记已置位，照常尊重。
        if (presetId == "white" && !UiStateStore.ReadPaperMigratedV23())
        {
            presetId = "black";
            UiStateStore.WritePaperColor(presetId);
            UiStateStore.WritePaperMigratedV23();
            AppLog.Info("纸张底色：检测到旧版残留的白色默认值，已一次性迁移为深灰（黑板）；"
                + "此后可在「更多」面板手动改回白色。");
        }

        ViewportHost.SetPaperColor(PaperPresetColor(presetId));
    }

    /// <summary>「更多」面板：回放 / 计算器 / 纸张底色 / 工作台模式（M20 S4 合并而来）。</summary>
    private void OnMoreButtonClick(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = true;
    }

    /// <summary>「导出」面板：PNG（每页一张）/ PDF（整卷多页）。</summary>
    private void OnExportButtonClick(object sender, RoutedEventArgs e)
    {
        ExportPopup.IsOpen = true;
    }

    private void OnCalculatorClick(object sender, RoutedEventArgs e)
    {
        ViewportHost.ToggleCalculator();
    }

    /// <summary>色圆片点选：换色 + 落盘 + 关弹层（色圆用 MouseLeftButtonUp，触屏友好）。</summary>
    private void OnPaperColorPicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string presetId })
        {
            ViewportHost.SetPaperColor(PaperPresetColor(presetId));
            UiStateStore.WritePaperColor(presetId);
            _viewModel.SetStatus(presetId switch
            {
                "cream" => "纸张：米黄。",
                "green" => "纸张：淡绿。",
                "blue" => "纸张：淡蓝。",
                "black" => "纸张：深灰（黑板）。深色底建议配「白」笔。",
                _ => "纸张：白。",
            });
        }

        MorePopup.IsOpen = false;
    }

    /// <summary>面板上的「收起」按钮：与悬浮球菜单 / F9 同一条路（SetToolbarHidden）。</summary>
    private void OnToolbarCollapseClick(object sender, RoutedEventArgs e)
    {
        SetToolbarHidden(true);
    }

    /// <summary>启动恢复：读持久化的收起态与位置比例（默认显示、左上角）。</summary>
    private void InitToolbarHiddenState()
    {
        _toolbarHidden = UiStateStore.ReadToolHidden() ?? false;
        _toolbarX = UiStateStore.ReadToolbarX();
        _toolbarY = UiStateStore.ReadToolbarY();
        ApplyToolbarState();
    }

    /// <summary>
    /// 显隐合成到界面：<b>只动可见性，不碰缩放</b> —— 老师收工具栏
    /// 不该连带着试卷挪位置（M12 S1 的护栏断言钉的就是这一条）。
    /// </summary>
    private void ApplyToolbarState()
    {
        TopChrome.Visibility = _toolbarHidden ? Visibility.Collapsed : Visibility.Visible;

        if (!_toolbarHidden)
        {
            ApplyToolbarMargin();
        }

        UpdateFloatingBallVisibility();
    }

    /// <summary>按比例把工具栏摆到画布内（没记录过位置 = 左上角；clamp 防小窗口出界）。</summary>
    private void ApplyToolbarMargin()
    {
        double availX = Math.Max(CanvasOverlayGrid.ActualWidth - TopChrome.ActualWidth, 0.0);
        double availY = Math.Max(CanvasOverlayGrid.ActualHeight - TopChrome.ActualHeight, 0.0);

        double x = Math.Clamp((_toolbarX ?? 0.01) * availX, 0.0, availX);
        double y = Math.Clamp((_toolbarY ?? 0.01) * availY, 0.0, availY);
        TopChrome.Margin = new Thickness(x, y, 0, 0);
    }

    /// <summary>持久态切换（F9 / 悬浮球菜单 / 面板收起钮 / 命令共用），写回 UiStateStore。</summary>
    private void ToggleToolbar()
    {
        SetToolbarHidden(!_toolbarHidden);
    }

    private void SetToolbarHidden(bool hidden)
    {
        if (_toolbarHidden == hidden) return;

        _toolbarHidden = hidden;
        UiStateStore.WriteToolHidden(hidden);
        ApplyToolbarState();

        if (!hidden)
        {
            // M13：从悬浮球呼出 —— 面板落在球的位置附近（clamp 在画布内），并记住。
            _toolbarX = Math.Clamp(FloatingBallHost.XRatio, 0.0, 1.0);
            _toolbarY = Math.Clamp(FloatingBallHost.YRatio, 0.0, 1.0);
            UiStateStore.WriteToolbarX(_toolbarX.Value);
            UiStateStore.WriteToolbarY(_toolbarY.Value);
            ApplyToolbarMargin();
        }

        if (hidden && !_hideTipShown)
        {
            // 每个会话只提示一次：工具栏"消失"是最容易让人以为出 bug 的表现。
            _hideTipShown = true;
            _viewModel.SetStatus("工具栏已收起：点悬浮球（或按 F9）呼回；拖面板顶部的把手可挪位置。");
        }
    }

    // ================================================================ M12 S4/S5/S6：题号 · 宏 · 模式 · 回放

    /// <summary>读宏文件（macros.txt），坏行进日志不进启动路径；热键建索引。</summary>
    private void LoadMacros()
    {
        var (macros, warnings) = MacroFile.Parse(MacroFile.DefaultPath);
        _macros = macros;

        _macrosByHotkey.Clear();
        foreach (var macro in macros)
        {
            if (macro.Hotkey is { } hotkey) _macrosByHotkey[hotkey] = macro;
        }

        foreach (string warning in warnings)
        {
            AppLog.Warn("宏文件 macros.txt：" + warning);
        }
    }

    /// <summary>执行宏：逐条走总线；认不出的列在状态栏，绝不中断整串。</summary>
    private void RunMacro(MacroDefinition macro)
    {
        var missing = new List<string>();
        foreach (string id in macro.Commands)
        {
            if (!_commands.Execute(id)) missing.Add(id);
        }

        // ★ 宏跑了什么、有没有漏，只靠状态栏是留不下证据的（老师关掉窗口就没了）。
        //   一行日志让"宏里写了 calc.open 却没反应"这类问题可以事后查（也是打包冒烟的判据）。
        AppLog.Info($"宏「{macro.Name}」执行：{macro.Commands.Count} 个动作"
                    + (missing.Count == 0 ? "（全部成功）" : $"，未登记 {string.Join("、", missing)}"));

        _viewModel.SetStatus(missing.Count == 0
            ? $"宏「{macro.Name}」：{macro.Commands.Count} 个动作已执行。"
            : $"宏「{macro.Name}」部分执行，不认识的命令：{string.Join("、", missing)}");
    }

    /// <summary>应用工作台模式：命令序列 + 持久化 + 关弹层。</summary>
    private void ApplyMode(BoardMode mode)
    {
        IReadOnlyList<string> missing = mode.Apply(_commands);
        _currentMode = mode;
        UiStateStore.WriteMode(mode.Id);
        MorePopup.IsOpen = false;
        _viewModel.SetStatus(missing.Count == 0
            ? $"工作台模式：{mode.Name}（{mode.Commands.Count} 个动作）。"
            : $"工作台模式：{mode.Name}，跳过不认识的命令 {string.Join("、", missing)}。");
    }

    /// <summary>启动回放；失败情况（步数超限 / 还没写过）宿主自己在状态栏说明。</summary>
    private void StartReplayIfAvailable()
    {
        ViewportHost.BeginReplay();
    }

    /// <summary>按题号相对跳页；没有标记时给出"怎么才有"的指引，不是干巴巴的失败。</summary>
    private void GoToQuestionMarkRelative(int delta)
    {
        if (!ViewportHost.GoToQuestionMarkRelative(delta))
        {
            _viewModel.SetStatus("还没有题号标记：先用「题号」工具在试卷上标，之后这里就能逐题跳转。");
            return;
        }
        UpdateQuestionIndexText();
    }

    /// <summary>题号指示文案：没标记时干脆留空，不放占位噪音。</summary>
    private void UpdateQuestionIndexText()
    {
        int total = ViewportHost.OrderedQuestionMarks.Count;
        QuestionIndexText.Text = total == 0
            ? string.Empty
            : $"第 {Math.Max(0, ViewportHost.CurrentQuestionMarkIndex) + 1}/{total} 题";
    }

    /// <summary>切活动墨迹层（总线命令用）：切完给一句人话反馈。</summary>
    private void SetActiveInkLayerUi(string layerId)
    {
        ViewportHost.SetActiveInkLayer(layerId);
        _viewModel.SetStatus(layerId == InkLayers.DraftId ? "已切到草稿层。" : "已切到批注层。");
    }

    // ================================================================ M12：第二行按钮的事件处理器

    private void OnLayerPanelClick(object sender, RoutedEventArgs e)
    {
        ViewportHost.ShowLayerPanel(true);
    }

    private void OnQuestionPrevClick(object sender, RoutedEventArgs e)
    {
        GoToQuestionMarkRelative(-1);
    }

    private void OnQuestionNextClick(object sender, RoutedEventArgs e)
    {
        GoToQuestionMarkRelative(1);
    }

    private void OnReplayClick(object sender, RoutedEventArgs e)
    {
        StartReplayIfAvailable();
    }

    private void OnModeGradingClick(object sender, RoutedEventArgs e)
    {
        ApplyMode(BoardModes.Grading);
    }

    private void OnModeReviewClick(object sender, RoutedEventArgs e)
    {
        ApplyMode(BoardModes.Review);
    }

    private void OnModePresentClick(object sender, RoutedEventArgs e)
    {
        ApplyMode(BoardModes.Present);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // 面板打开时按键优先归面板：
        // · Esc 先关面板（而不是先退全屏）—— 用户此刻想的"退出"是退出面板；
        // · D 在停靠 / 全屏两种形态间切换（与面板顶栏的按钮同一个动作）。
        if (_geoGebraPanel is { IsOpen: true } panel)
        {
            if (e.Key == Key.Escape)
            {
                _ = panel.CloseAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.None)
            {
                _ = panel.ToggleFormAsync();
                e.Handled = true;
                return;
            }
        }

        // 物理仿真面板（M22 S5）：只做全屏，Esc 关闭；D 不接 —— 它没有停靠形态。
        if (_webSimPanel is { IsOpen: true } simPanel && e.Key == Key.Escape)
        {
            _ = simPanel.CloseAsync();
            e.Handled = true;
            return;
        }

        // M19：Esc 先问可取消工具（圆规等）要不要自己处理：
        // · 取消当前一步 / 切回上一工具成功 ⇒ 吞掉这次 Esc；
        // · 当前工具不实现 ICancellableTool ⇒ 放行，继续走下面的"退全屏"。
        // 必须排在"退全屏"之前：否则正在拖圆规半径时按 Esc，会同时取消预览又退出全屏。
        if (e.Key == Key.Escape && ViewportHost.TryCancelActiveTool())
        {
            e.Handled = true;
            return;
        }

        // 全屏是默认状态、又没有标题栏，所以必须留一条随时能进出、能关窗口的路：
        // F11 切换；Esc 退出全屏（退出后标题栏与关闭按钮就回来了）。
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullScreen)
        {
            ExitFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.F9)
        {
            // M12 S1：工具栏显隐的另一条退路（不依赖鼠标 / 悬浮球）
            ToggleToolbar();
            e.Handled = true;
        }

        // M12 宏热键：Ctrl+Shift+数字1~9（macros.txt 里绑定的宏）。
        // ★ 必须排在下面的 Ctrl 组合键 switch 之前 —— 那里的 case Key.D1 没带 when，
        //   会把 Ctrl+Shift+1 当成"原始大小"吃掉。
        if (!e.Handled && (Keyboard.Modifiers & ModifierKeys.Control) != 0
                      && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            int? digit = e.Key switch
            {
                Key.D1 or Key.NumPad1 => 1,
                Key.D2 or Key.NumPad2 => 2,
                Key.D3 or Key.NumPad3 => 3,
                Key.D4 or Key.NumPad4 => 4,
                Key.D5 or Key.NumPad5 => 5,
                Key.D6 or Key.NumPad6 => 6,
                Key.D7 or Key.NumPad7 => 7,
                Key.D8 or Key.NumPad8 => 8,
                Key.D9 or Key.NumPad9 => 9,
                _ => null,
            };

            if (digit is not null && _macrosByHotkey.TryGetValue($"Ctrl+Shift+{digit}", out var macro))
            {
                RunMacro(macro);
                e.Handled = true;
                return;
            }
        }

        // 只拦带 Ctrl 的组合键，其余原样放过 —— 免得挡住将来 M7 工具系统的单键快捷键
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                // ★ 带修饰键的分支必须排在"裸键"之前：switch 是按顺序匹配的，
                //   把 Ctrl+Shift+O 写在 Ctrl+O 后面，它永远轮不到（编译器会直接报不可达）。
                case Key.O when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                    OpenProjectFromDialog();
                    e.Handled = true;
                    break;

                case Key.Z:
                    ViewportHost.Undo();
                    e.Handled = true;
                    break;

                case Key.Y:
                    ViewportHost.Redo();
                    e.Handled = true;
                    break;

                case Key.O:
                    _viewModel.OpenCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.S when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                    // Ctrl+Shift+S 必须排在 Ctrl+S 之前：两个 case 的 Key 相同，
                    // 带 when 的先判，否则"另存为"永远会被"保存"吃掉。
                    SaveProjectAs();
                    e.Handled = true;
                    break;

                case Key.S:
                    SaveAll();
                    e.Handled = true;
                    break;

                // Ctrl+Shift+E：导出整卷 PDF。必须排在 Ctrl+E 之前 ——
                // 两个 case 的 Key 相同，带 when 的先判，否则"导出 PDF"会被"导出 PNG"吃掉
                // （与上面 Ctrl+S / Ctrl+Shift+S 同一套路）。
                case Key.E when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                    ExportPdf();
                    e.Handled = true;
                    break;

                // Ctrl+E：导出 PNG（M9）。E 取 Export —— 与"保存"区分开，
                // 它是"产出一份能带走 / 能打印的文件"，不改动当前文档。
                case Key.E:
                    ExportPng();
                    e.Handled = true;
                    break;

                case Key.N:
                    NewProject();
                    e.Handled = true;
                    break;

                case Key.W:
                    CloseCurrent();
                    e.Handled = true;
                    break;

                case Key.OemPlus:
                case Key.Add:
                    ViewportHost.ZoomIn();
                    e.Handled = true;
                    break;

                case Key.OemMinus:
                case Key.Subtract:
                    ViewportHost.ZoomOut();
                    e.Handled = true;
                    break;

                case Key.D0:
                case Key.NumPad0:
                    ViewportHost.FitWidth();
                    e.Handled = true;
                    break;

                case Key.D1:
                case Key.NumPad1:
                    ViewportHost.ActualSize();
                    e.Handled = true;
                    break;
            }
        }

        // 工具快捷键（单个数字键，不带任何修饰键）。
        // M6 时刻意只拦 Ctrl 组合，就是为了把这条路留给工具系统 ——
        // 键位由工具自己声明，宿主并不认识"直尺"这种东西（M7.1 起是插件提供的）。
        // 查不到对应工具时**原样放过**：否则会悄悄吞掉将来可能需要的单键操作。
        if (!e.Handled && Keyboard.Modifiers == ModifierKeys.None)
        {
            var tool = ViewportHost.FindToolByShortcut(e.Key);

            if (tool is not null)
            {
                ViewportHost.SetTool(tool.Id);
                RefreshToolButtons();
                e.Handled = true;
            }
        }

        if (!e.Handled) base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// 关窗口：工程有未保存改动时先问一句。
    /// </summary>
    /// <remarks>
    /// ★ 与"新建 / 打开 / 关闭工程"共用同一处 <see cref="ConfirmDiscardCurrentProject"/>，
    /// 三选逻辑只有一份 —— 各写一遍早晚会漏掉某个入口，而漏掉的代价是老师一整节课的板书。
    /// <para>
    /// 选"保存"而保存失败时<b>必须取消关闭</b>：窗口关掉了、改动没存住，
    /// 是这套机制里最不可原谅的一种失败。
    /// </para>
    /// </remarks>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardCurrentProject())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关窗口是批注的最后一次保存机会，必须排在最前面：
        // 它要读当前笔迹与文档路径，而下面几行会把这些依赖逐个拆掉。
        SavePendingAnnotations();

        // 先停后台渲染线程，再释放文档服务 —— 顺序反了会让渲染线程去碰已释放的 PdfDocument
        if (_geoGebraPanel is { } panel)
        {
            panel.FormChanged -= OnGeoGebraFormChanged;
            panel.Closed -= OnGeoGebraPanelClosed;
        }

        // 物理仿真面板（M22 S5）：窗口关闭时把「导出到白板」的订阅摘掉。
        if (_webSimPanel is { } simPanel)
        {
            simPanel.CaptureRequested -= OnWebPanelCaptureRequested;
        }
        ViewportHost.Shutdown();

        _autoSave.Dispose();
        _project.Changed -= OnProjectChanged;
        _viewModel.DocumentChanged -= OnDocumentChanged;
        ViewportHost.ViewportChanged -= OnViewportChanged;
        ViewportHost.InkChanged -= OnInkChanged;
        ViewportHost.HistoryChanged -= OnHistoryChanged;
        ViewportHost.ToolStatusMessage -= OnToolStatusMessage;

        // ★ 关文档要在删临时试卷**之前**：内嵌工程的试卷是以临时文件的形式交给 pdfium 打开的，
        //   自己还开着它就去删，Windows 会直接拒绝（being used by another process），
        //   于是临时目录里的卷面一份一份攒下去 —— 而这件事只在日志里留一行 warn，界面上毫无痕迹。
        //   真机冒烟（smoke_m9s3.py 最后一条）就是为了盯住这个顺序。
        _viewModel.Dispose();

        _embeddedReleaser.Cleanup();

        base.OnClosed(e);
    }
}
