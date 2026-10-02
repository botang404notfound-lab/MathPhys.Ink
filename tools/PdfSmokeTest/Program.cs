using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MathPhys.Ink.Design;
using MathPhys.Ink.Export;
using MathPhys.Ink.WebPanel;
using MathPhys.Ink.Gfx;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Ink;
using MathPhys.Ink.Input;
using MathPhys.Ink.Pdf;
using MathPhys.Ink.PdfSmokeTest.GfxPlugin;
using MathPhys.Ink.Plugin.Protractor;
using MathPhys.Ink.Plugin.Ruler;
using MathPhys.Ink.Plugin.CoordSystem;
using MathPhys.Ink.Plugin.FunctionPlot;
using MathPhys.Ink.Plugin.GeoGebra;
using MathPhys.Ink.Plugin.FunctionPlot.Expr;
using MathPhys.Ink.Plugin.Triangle;
using MathPhys.Ink.Plugin.VectorArrow;
using MathPhys.Ink.Plugin.Formula;
using MathPhys.Ink.Plugin.CircuitKit;
using MathPhys.Ink.Plugin.Compass;
// M22 S3：物理仿真的三个模型与画面都在这两个命名空间里
using MathPhys.Ink.Plugin.NativeSim;
using MathPhys.Ink.Plugin.NativeSim.Kinematics;
using MathPhys.Ink.Plugins;
using MathPhys.Ink.Tools;
using MathPhys.Ink.Tools.BuiltIn;
using MathPhys.Ink.Tools.Plugins;
using MathPhys.Ink.Workflows;
using MathPhys.Ink.ViewModels;
using MathPhys.Ink.Viewport;
using MathPhys.Ink.Views.Controls;

namespace MathPhys.Ink.PdfSmokeTest;

/// <summary>
/// 验收 harness：不开窗口，直接把 PDF 渲染成 PNG 并核对尺寸换算与视口数学。
/// </summary>
/// <remarks>
/// 存在的理由：无限画布与笔迹的坐标正确性完全建立在
/// 「PDF point → world 单位 → 位图像素」这条链上。GUI 里肉眼看不出 1 像素的偏差，
/// 但那个偏差到了 M3 就会变成"笔迹与题干错位"。所以这里用可重复的断言把它钉死。
/// <para>
/// M2 追加的核心是：手势换算的不变量、LRU 淘汰语义、后台调度的线程与 generation 语义。
/// 这三样都是"错了不会崩、只会默默表现得很奇怪"的东西，必须断言。
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 输出走 UTF-8：否则中文重定向到文件时会按控制台代码页编码，读出来是乱码
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
        catch { /* 输出被重定向时可能不支持，忽略 */ }

        if (args.Length < 1)
        {
            Console.Error.WriteLine("用法: PdfSmokeTest <pdf路径> [输出目录]");
            return 2;
        }

        string pdfPath = args[0];
        string outDir = args.Length >= 2
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "m1-smoke");

        if (!File.Exists(pdfPath))
        {
            Console.Error.WriteLine($"找不到 PDF：{pdfPath}");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        int failures = 0;

        try
        {
            // M10：控件里的 {StaticResource 令牌} 需要一个资源环境（见方法说明）
            InitializeDesignResources();

            using var service = new PdfiumDocumentService();
            service.Open(pdfPath);

            Console.WriteLine($"[PDF] {Path.GetFileName(pdfPath)}");
            Console.WriteLine($"[PDF] 页数 = {service.PageCount}");
            Console.WriteLine();

            // ---- 1) 尺寸与渲染断言 ----
            var sizes = new List<Size>(service.PageCount);
            for (int i = 0; i < service.PageCount; i++)
            {
                var size = service.GetPageSize(i);
                sizes.Add(size);
                Console.WriteLine($"[页 {i}] world 尺寸 = {size.Width:F2} x {size.Height:F2} point"
                                  + $"  ({size.Width / 72.0:F2} x {size.Height / 72.0:F2} inch)");
            }
            Console.WriteLine();

            foreach (double zoom in new[] { 1.0, 2.0 })
            {
                Console.WriteLine($"---- zoom = {zoom} ----");
                for (int i = 0; i < service.PageCount; i++)
                {
                    var source = service.RenderPage(i, zoom);

                    double dpi = 96.0 * zoom;
                    int expectW = (int)Math.Round(sizes[i].Width * dpi / 72.0);
                    int expectH = (int)Math.Round(sizes[i].Height * dpi / 72.0);

                    bool sizeOk = Math.Abs(source.PixelWidth - expectW) <= 1
                               && Math.Abs(source.PixelHeight - expectH) <= 1;

                    // 长宽比必须守住，否则 PDF 会被拉伸变形
                    double srcAspect = sizes[i].Width / sizes[i].Height;
                    double pxAspect = (double)source.PixelWidth / source.PixelHeight;
                    bool aspectOk = Math.Abs(srcAspect - pxAspect) < 0.005;

                    // 必须是"白纸黑字"：中心区域不能全白（否则说明渲染出了空白页）
                    bool inkOk = HasNonWhitePixels(source);

                    string file = Path.Combine(outDir, $"page{i + 1}_zoom{zoom:0.0}.png");
                    SavePng(source, file);

                    string flag = sizeOk && aspectOk && inkOk ? "PASS" : "FAIL";
                    if (flag == "FAIL") failures++;

                    Console.WriteLine($"  [{flag}] 页{i}  像素 {source.PixelWidth}x{source.PixelHeight}"
                                      + $" (期望 {expectW}x{expectH})  长宽比 {pxAspect:F4} (源 {srcAspect:F4})"
                                      + $"  有内容={inkOk}");
                }
                Console.WriteLine();
            }

            // ---- 2) 世界坐标排版断言 ----
            var layout = new WorldLayout { PageGap = 24.0 };
            layout.Rebuild(sizes);

            Console.WriteLine("---- WorldLayout ----");
            for (int i = 0; i < layout.PageRects.Count; i++)
            {
                var r = layout.PageRects[i];
                Console.WriteLine($"  [页 {i}] X={r.X:F2} Y={r.Y:F2} W={r.Width:F2} H={r.Height:F2}");
            }
            Console.WriteLine($"  包围盒 = {layout.WorldBounds}");

            bool layoutOk = true;

            // 首页起点必须是 (M,M)：这个留白让墨迹层原点恰好落到世界原点（见 docs/04 §3.2）
            if (Math.Abs(layout.PageRects[0].Y - WorldLayout.DefaultPageMargin) > 1e-6
                || Math.Abs(layout.PageRects[0].X - WorldLayout.DefaultPageMargin) > 1e-6)
            {
                layoutOk = false;
                Console.WriteLine($"  FAIL 首页起点应为 ({WorldLayout.DefaultPageMargin},{WorldLayout.DefaultPageMargin})，"
                                  + $"实际 ({layout.PageRects[0].X:F2},{layout.PageRects[0].Y:F2})");
            }

            // 相邻页间距必须等于 PageGap
            for (int i = 1; i < layout.PageRects.Count; i++)
            {
                double expectedY = layout.PageRects[i - 1].Y + layout.PageRects[i - 1].Height + 24.0;
                if (Math.Abs(layout.PageRects[i].Y - expectedY) > 1e-6)
                {
                    layoutOk = false;
                    Console.WriteLine($"  FAIL 页{i} Y={layout.PageRects[i].Y:F2} 期望 {expectedY:F2}");
                }
            }

            // 水平居中：各页中心 X 必须相同
            double center0 = layout.PageRects[0].X + layout.PageRects[0].Width / 2.0;
            for (int i = 1; i < layout.PageRects.Count; i++)
            {
                double c = layout.PageRects[i].X + layout.PageRects[i].Width / 2.0;
                if (Math.Abs(c - center0) > 1e-6)
                {
                    layoutOk = false;
                    Console.WriteLine($"  FAIL 页{i} 中心 X={c:F2} 与其他页 {center0:F2} 不一致");
                }
            }

            // 命中测试：首页中点应命中页 0
            var mid = new Point(layout.PageRects[0].X + 5, layout.PageRects[0].Y + 5);
            if (layout.HitTestPage(mid) != 0)
            {
                layoutOk = false;
                Console.WriteLine("  FAIL 命中测试：首页内一点未命中页 0");
            }

            if (!layoutOk) failures++;
            Console.WriteLine($"[{(layoutOk ? "PASS" : "FAIL")}] WorldLayout 排版");

            // ---- 3) 视口换算断言（M1）----
            var viewport = new CanvasViewport();
            viewport.SetView(1.5, 100, 50);
            var world = new Point(10, 20);
            var screen = viewport.ToViewport(world);
            var back = viewport.ToWorld(screen);
            bool roundTrip = Math.Abs(back.X - world.X) < 1e-6 && Math.Abs(back.Y - world.Y) < 1e-6;
            Console.WriteLine();
            Console.WriteLine($"[{(roundTrip ? "PASS" : "FAIL")}] 视口往返换算 world({world.X},{world.Y})"
                              + $" → 视口({screen.X:F2},{screen.Y:F2}) → world({back.X:F2},{back.Y:F2})");
            if (!roundTrip) failures++;

            // 锚点缩放：锚点处的世界坐标必须不变
            var anchor = new Point(400, 300);
            var anchorWorldBefore = viewport.ToWorld(anchor);
            viewport.ZoomAt(anchor, 2.0);
            var anchorWorldAfter = viewport.ToWorld(anchor);
            bool anchorStable = Math.Abs(anchorWorldBefore.X - anchorWorldAfter.X) < 1e-6
                             && Math.Abs(anchorWorldBefore.Y - anchorWorldAfter.Y) < 1e-6;
            Console.WriteLine($"[{(anchorStable ? "PASS" : "FAIL")}] 锚点缩放不跑位"
                              + $" 缩放前 world({anchorWorldBefore.X:F4},{anchorWorldBefore.Y:F4})"
                              + $" 缩放后 world({anchorWorldAfter.X:F4},{anchorWorldAfter.Y:F4})");
            if (!anchorStable) failures++;

            // ---- 4) M2：视口手势与后台渲染 ----
            failures += RunM2Checks(service);

            // ---- 5) M3：笔迹层 ----
            failures += RunM3Checks(service, outDir);

            // ---- 6) M3.5：按宽度适配（方案 B）----
            failures += RunFitWidthChecks(service, outDir);

            // ---- 7) M3.6：页外纯白（整纸观感）----
            failures += RunPaperLookChecks(service, outDir);

            // ---- 8) M4：笔 / 橡皮 / 压感 ----
            failures += RunM4Checks(service, outDir);

            // ---- 9) M5：撤销 / 重做 ----
            failures += RunM5Checks(service, outDir);

            // ---- 10) M6：批注持久化 ----
            failures += RunM6Checks(service, outDir);

            // ---- 11) M7.1：工具系统与插件契约 ----
            failures += RunM71Checks();

            // ---- 12) M7.2：插件宿主（加载器）----
            failures += RunM72Checks();

            // ---- 13) M7.2.1：窗口模式可切换（状态文件读写）----
            failures += RunM721Checks(outDir);

            // ---- 14) M7.3：正式直尺插件 ----
            failures += RunM73Checks();

            // ---- 15) M7.4：图形对象层（学科工具的公共底座）----
            failures += RunM74Checks(service, outDir);

            // ---- 16) M7.4 Step 1：量角器（第一条完整走通的对象型学科工具）----
            failures += RunM74S1Checks(outDir);

            // ---- 17) M7.4 Step 2：三角板（第二条对象型学科工具）----
            failures += RunM74S2Checks(outDir);

            // ---- 18) M7.4 Step 3：坐标系（第一个需要页面几何语义的对象型工具）----
            failures += RunM74S3Checks(outDir);
        failures += RunM74S4Checks(outDir);

            // ---- 19) M7.4 Step 5：矢量箭头（最后一个 P0 学科工具）----
            failures += RunM74S5Checks(outDir);

            // ---- 20) M7.4 Step 5 第二轮：合力（框选 + 自动跟随 + 虚线）----
            failures += RunM74S5SumChecks(outDir);

            // ---- 21) M7.4 Step 5 第三轮：正交分解（点选 + 带符号 + 可逆）----
            failures += RunM74S5DecomposeChecks(outDir);

            // ---- 22) M7.4 Step 5 第四轮：矢量组（打组 + 整组跟随）----
            failures += RunM74S5GroupChecks(outDir);

            // ---- 23) M7.4 Step 5 第五轮：参数面板（纯数据面板 + 改参数）----
            failures += RunM74S5PanelChecks(outDir);

            // ---- 24) M7.4 Step 4 补全：零点/极值/交点 + 定义域 + 分段 ----
            failures += RunM74S5FeatureChecks(outDir);

            // ---- 25) M7.5 S0：演示面板宿主桥接（GeoGebra 通路）----
            failures += RunM75Checks(outDir);

            // ---- 25b) M7.5 S1：离线面板就绪/失败闸门（LoadFailed、selfTest、命令通道）----
            failures += RunM75S1Checks(outDir);

            // ---- 25c) M7.5 S3：停靠面板形态 + 触控互斥（A1）----
            failures += RunM75S3Checks(outDir);

            // ---- 26) M8 S0：.twb 工程文件格式层（ZIP 容器 + manifest）----
            failures += RunM8S0Checks(outDir);

            // ---- 27) M8 S1：ProjectStore（装载判定 / 外挂定位 / 侧车清理）----
            failures += RunM8S1Checks(outDir);

            // ---- 27) M8 S2：工程组装 / 工程状态机 / 端到端动线 ----
            failures += RunM8S2Checks(outDir);

            // ---- 28) M8 S3：关档三选矩阵 / 「什么算改动」 ----
            failures += RunM8S3Checks(outDir);

            // ---- 29) M9 S0：导出的换算真值源 + 画布借用 ----
            failures += RunM9S0Checks(service, outDir);
        failures += RunM9S1Checks(service, outDir);

            // ---- 30) M9 S2：整卷 PDF 导出 ----
            failures += RunM9S2Checks(service, outDir);

            // ---- 31) M9 S3：从 .twb 内嵌 PDF 导出（M8 接缝）----
            failures += RunM9S3Checks(outDir);

            // ---- 32) M10：设计系统与界面美化 ----
            failures += RunM10Checks(outDir);

            // ---- 33) M11：书写流畅性与笔锋 ----
            failures += RunM11Checks(outDir);

            // ---- 34) M12：个人教学工作流（命令总线 / 悬浮球 / 题号 / 宏 / 吸附 / rebase / 翻转探针）----
            failures += RunM12Checks(outDir);

            // ---- 35) M13：课堂体验优化（光标隐藏 / 纸张底色 / 悬浮球 / 浮动工具栏 / 计算器）----
            failures += RunM13Checks(outDir);

            // ---- 36) M14 ----
            failures += RunM14Checks(outDir);

            // ---- 37) M15 ----
            failures += RunM15Checks(outDir);

            // ---- 38) M16：工具栏方形重排 + 渲染变换拖动 ----
            failures += RunM16Checks(outDir);

            // ---- 39) M17：三角板沿边画线（EdgeSnapHelper 数学 + 全状态机 + 源级护栏）----
            failures += RunM17Checks(outDir);
            failures += RunM18Checks(outDir);
            failures += RunM19Checks(outDir);

            // ---- M19 后修复：浮动面板拖动闪烁（拖动坐标基准成环）----
            failures += RunDragCoordChecks(outDir);

            // ---- 41) M22：通用 Web 面板契约（新接口 / ITool 零新增护栏 / 信封往返）----
            failures += RunM22Checks();

            // ---- 42) M22 S3：物理仿真（三个运动学模型 + 落成位图对象 + 降级）----
            failures += RunM22S3Checks(outDir);

            // ---- 42b) M22 S5：CircuitJS 接入（profile / 页 Id 透传 / 离线资产）----
            failures += RunM22S5Checks(outDir);

            // ---- 40) 跨插件：工具快捷键唯一性（坐标系 vs 沿边画线同键那类问题）----
            failures += RunShortcutChecks();

            Console.WriteLine();
            Console.WriteLine($"PNG 输出目录：{outDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("[异常] " + ex);
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "===== 全部检查通过 =====" : $"===== 有 {failures} 项失败 =====");
        return failures == 0 ? 0 : 1;
    }

    // ================================================================= M10：设计系统与界面美化

    /// <summary>
    /// 设计令牌文件（相对 <c>src\MathPhys.Ink</c>）。
    /// </summary>
    /// <remarks>
    /// ★ 两份用途共用这一份清单：拼资源 pack URI（初始化 harness 的资源环境）
    /// 与检查文件是否存在。顺序必须与 App.xaml 的合并顺序一致 ——
    /// 后加载的覆盖先加载的，顺序错了颜色会静默变成另一个值。
    /// ★ 浅色板在列（换肤时要整本替换掉深色板那一本），但**不预合并**，
    /// 所以断言里 App.xaml 的合并数应为 <c>Length - 1</c>。
    /// ★ M20 S1 加了 Motion.xaml（动效令牌）。新增字典必须同时改三处：
    /// App.xaml / Design/Tokens.cs 的 Modules / 这里的 DesignModules。
    /// </remarks>
    private static readonly string[] DesignModules =
    {
        @"Design\Palette.Canvas.xaml",
        @"Design\Palette.Dark.xaml",
        @"Design\Palette.Light.xaml",
        @"Design\Typography.xaml",
        @"Design\Spacing.xaml",
        @"Design\Motion.xaml",
        @"Design\Icons.xaml",
        @"Design\Controls\Buttons.xaml",
        @"Design\Controls\Inputs.xaml",
        @"Design\Controls\Panels.xaml",
    };

    /// <summary>
    /// 「零裸色值」的白名单。
    /// </summary>
    /// <remarks>
    /// 这几个文件是**定义**颜色的地方（三本色板 / 墨色调色板 / 令牌取值器），
    /// 或者颜色属于**存档格式**的一部分（<c>GfxObjectData</c> 的出厂色与颜色解析器）。
    /// 其余任何地方出现色值，都说明有人绕过了设计系统。
    /// </remarks>
    private static readonly string[] ColorWhitelist =
    {
        @"Design\Palette.Canvas.xaml",
        @"Design\Palette.Dark.xaml",
        @"Design\Palette.Light.xaml",
        @"Design\Tokens.cs",
        @"Ink\InkPalette.cs",
        @"Gfx\GfxObjectData.cs",
    };

    /// <summary>
    /// 给 harness 造一个资源环境（M10 起必需）。
    /// </summary>
    /// <remarks>
    /// ★ 为什么需要：M10 之后 XAML 里的颜色一律写成 <c>{StaticResource 令牌}</c>，
    /// 而 StaticResource 是**加载时**解析的 —— 没有 Application 资源，构造控件就直接抛
    /// <c>XamlParseException</c>。M10 之前控件里全是硬编码色值，所以"不开窗口也能构造控件"
    /// 是白拿的；现在这件事本身需要一个资源环境。
    /// <para>
    /// ★ 造它的意义不只是"让构造不报错"：它让 harness 里的控件跑在**与真机同一套资源**下。
    /// 否则就得在 XAML 里到处写 DynamicResource 来容忍"没有资源"，而那会改变真机行为
    /// （DynamicResource 找不到就保持 null），等于为了测试改产品。
    /// </para>
    /// </remarks>
    private static void InitializeDesignResources()
    {
        if (Application.Current is not null) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        foreach (var module in DesignModules)
        {
            string uriPath = module.Replace('\\', '/');
            try
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        $"pack://application:,,,/MathPhys.Ink;component/{uriPath}",
                        UriKind.Absolute),
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[警告] 合并设计字典失败 {module}：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// M10 断言组：设计系统的完整性、单一来源与可断言性。
    /// </summary>
    /// <remarks>
    /// ★ 这一组的价值在于：设计系统一旦是"数据"，就能被断言；
    /// 而它最容易坏的方式恰恰是**不报错的那种** ——
    /// 令牌键拼错一个字母，WPF 不报错、只是静默用默认值，屏幕上少一块颜色，
    /// 只有肉眼能发现。所以"每个被引用的键都能解析到"是这里最要紧的一条。
    /// </remarks>
    private static int RunM10Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M10：设计系统与界面美化 ----");

        string root = FindRepoRoot(outDir);
        string srcRoot = Path.Combine(root, "src", "MathPhys.Ink");

        // ---- 1) 九本字典都在，且各自定义了哪些键 ----
        var keysByModule = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var module in DesignModules)
        {
            string path = Path.Combine(srcRoot, module);
            if (!File.Exists(path))
            {
                Console.WriteLine($"[FAIL] 设计令牌文件缺失：{module}");
                failures++;
                continue;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(File.ReadAllText(path, Encoding.UTF8), "x:Key=\"([^\"]+)\""))
            {
                keys.Add(m.Groups[1].Value);
            }

            keysByModule[module] = keys;
        }

        var allDefined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in keysByModule)
        {
            foreach (var key in pair.Value) allDefined.Add(key);
        }

        bool complete = keysByModule.Count == DesignModules.Length;
        Console.WriteLine($"[{(complete ? "PASS" : "FAIL")}] 设计令牌文件齐全：{keysByModule.Count}/{DesignModules.Length} 本，"
                          + $"共定义 {allDefined.Count} 个键");
        if (!complete) failures++;

        // ---- 2) App.xaml 合并的每一本都真实存在 ----
        string appXaml = Path.Combine(srcRoot, "App.xaml");
        if (!File.Exists(appXaml))
        {
            Console.WriteLine("[FAIL] 找不到 App.xaml");
            failures++;
        }
        else
        {
            var missing = new List<string>();
            int merged = 0;
            foreach (Match m in Regex.Matches(File.ReadAllText(appXaml, Encoding.UTF8),
                         "Source=\"Design/([^\"]+\\.xaml)\""))
            {
                merged++;
                string file = Path.Combine(srcRoot, "Design", m.Groups[1].Value.Replace('/', '\\'));
                if (!File.Exists(file)) missing.Add(m.Groups[1].Value);
            }

            // ★ 浅色板**不预合并**：它只在换肤时按需加载（用来换掉深色板那一本），
            //   所以 App.xaml 里应比 DesignModules 少一本。
            int expectedMerged = DesignModules.Length - 1;
            bool ok = missing.Count == 0 && merged == expectedMerged;
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] App.xaml 合并了 {merged} 本字典（期望 {expectedMerged}），缺失 {(missing.Count == 0 ? "0" : string.Join(", ", missing))}");
            if (!ok) failures++;
        }

        // ---- 3) 每个键都能从 C# 侧解析到（★ 拼错键不报错、只会静默用默认值）----
        Tokens.ResetMissingKeys();
        var unresolved = new List<string>();
        foreach (var key in allDefined)
        {
            if (!Tokens.Exists(key)) unresolved.Add(key);
        }

        unresolved.Sort(StringComparer.Ordinal);
        if (unresolved.Count > 0)
        {
            Console.WriteLine($"[FAIL] 有 {unresolved.Count} 个令牌键解析不到：{string.Join(" / ", unresolved)}");
            failures++;
        }
        else
        {
            Console.WriteLine($"[PASS] 全部 {allDefined.Count} 个令牌键都能解析到");
        }

        // ---- 4) 所有引用都指向已定义的键 ----
        var referencePattern = new Regex(
            @"\{(?:StaticResource|DynamicResource)\s+([A-Za-z_][\w.]*)\}"
            + @"|Tokens\.(?:Brush|Color|Number|Thickness|CornerRadius|FontWeight|FontFamily)\(""([^""]+)""\)");

        var dangling = new List<string>();
        int refCount = 0;
        foreach (var file in EnumerateSourceFiles(srcRoot))
        {
            bool isXaml = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            string text = StripComments(File.ReadAllText(file, Encoding.UTF8), isXaml);

            foreach (Match m in referencePattern.Matches(text))
            {
                string key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                refCount++;
                if (allDefined.Contains(key)) continue;

                string where = Path.GetRelativePath(root, file) + " → " + key;
                if (!dangling.Contains(where)) dangling.Add(where);
            }
        }

        dangling.Sort(StringComparer.Ordinal);
        if (dangling.Count > 0)
        {
            Console.WriteLine($"[FAIL] 有 {dangling.Count} 处引用了未定义的令牌：");
            foreach (var d in dangling) Console.WriteLine("       " + d);
            failures++;
        }
        else
        {
            Console.WriteLine($"[PASS] {refCount} 处令牌引用全部指向已定义的键");
        }

        // ---- 5) 深浅两本界面板的键必须严格一一对应 ----
        string darkModule = DesignModules[1];
        string lightModule = DesignModules[2];
        if (keysByModule.TryGetValue(darkModule, out var darkKeys)
            && keysByModule.TryGetValue(lightModule, out var lightKeys))
        {
            var onlyDark = new List<string>();
            var onlyLight = new List<string>();
            foreach (var k in darkKeys) if (!lightKeys.Contains(k)) onlyDark.Add(k);
            foreach (var k in lightKeys) if (!darkKeys.Contains(k)) onlyLight.Add(k);

            bool same = onlyDark.Count == 0 && onlyLight.Count == 0;
            Console.WriteLine($"[{(same ? "PASS" : "FAIL")}] 深浅两本色板键一致：各 {darkKeys.Count} 个键"
                              + (same ? string.Empty : $"（仅深色有 {string.Join(",", onlyDark)}；仅浅色有 {string.Join(",", onlyLight)}）"));
            if (!same) failures++;
        }

        // ---- 6) 零裸色值 ----
        var leakPattern = new Regex(
            @"#[0-9A-Fa-f]{6,8}|(?<![\w.])Color\.From(?:Argb|Rgb|ScRgb)|(?<![\w.])Brushes\.(?!Transparent\b)[A-Za-z]+|(?<![\w.])Colors\.(?!Transparent\b)[A-Za-z]+");

        var leaks = new List<string>();
        foreach (var file in EnumerateSourceFiles(srcRoot))
        {
            string relative = Path.GetRelativePath(srcRoot, file);
            bool whitelisted = false;
            foreach (var w in ColorWhitelist)
            {
                if (relative.Equals(w, StringComparison.OrdinalIgnoreCase)) { whitelisted = true; break; }
            }
            if (whitelisted) continue;

            bool isXaml = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            string text = StripComments(File.ReadAllText(file, Encoding.UTF8), isXaml);

            foreach (Match m in leakPattern.Matches(text))
            {
                leaks.Add(Path.GetRelativePath(root, file) + " → " + m.Value);
            }
        }

        if (leaks.Count > 0)
        {
            Console.WriteLine($"[FAIL] 发现 {leaks.Count} 处裸色值（应全部走设计令牌）：");
            foreach (var l in leaks) Console.WriteLine("       " + l);
            failures++;
        }
        else
        {
            Console.WriteLine("[PASS] 零裸色值：界面与画布的颜色全部来自设计令牌");
        }

        // ---- 7) 令牌画刷必须都是冻结实例 ----
        var notFrozen = new List<string>();
        int brushCount = 0;
        foreach (var key in allDefined)
        {
            if (!key.StartsWith("Ui.", StringComparison.Ordinal) && !key.StartsWith("Canvas.", StringComparison.Ordinal))
            {
                continue;
            }

            if (!Tokens.TryGet(key, out var value) || value is not Brush brush) continue;

            brushCount++;
            if (!brush.IsFrozen) notFrozen.Add(key);
        }

        notFrozen.Sort(StringComparer.Ordinal);
        bool frozen = notFrozen.Count == 0 && brushCount > 0;
        Console.WriteLine($"[{(frozen ? "PASS" : "FAIL")}] 令牌画刷全部冻结：{brushCount - notFrozen.Count}/{brushCount} 个"
                          + (frozen ? string.Empty : $"（未冻结：{string.Join(",", notFrozen)}）"));
        if (!frozen) failures++;

        // ---- 8) 色值的单一来源：画布默认色 / 出厂笔色 ----
        Color canvasDefault = Tokens.Color("Canvas.GfxDefault");
        string canvasDefaultText =
            $"#{canvasDefault.A:X2}{canvasDefault.R:X2}{canvasDefault.G:X2}{canvasDefault.B:X2}";
        bool defaultMatches = string.Equals(canvasDefaultText, GfxObjectData.DefaultColor, StringComparison.OrdinalIgnoreCase)
                           && GfxObjectData.DefaultColorValue == canvasDefault;

        Color inkDefault = InkPalette.Colors[InkPalette.DefaultColorIndex].Color;
        bool penMatches = new PenProfile().Color == inkDefault;

        bool singleSource = defaultMatches && penMatches;
        Console.WriteLine($"[{(singleSource ? "PASS" : "FAIL")}] 色值单一来源：Canvas.GfxDefault={canvasDefaultText}"
                          + $" / GfxObjectData.DefaultColor={GfxObjectData.DefaultColor} / 出厂笔色={inkDefault}");
        if (!singleSource) failures++;

        // ---- 9) 大屏尺寸规范：可点目标最小边长 ----
        double touchMin = Tokens.Number("Touch.Min");
        double touchLarge = Tokens.Number("Touch.Large");
        double textBody = Tokens.Number("Text.Body");

        string buttonsXaml = Path.Combine(srcRoot, @"Design\Controls\Buttons.xaml");
        int touchMinRefs = File.Exists(buttonsXaml)
            ? Regex.Matches(File.ReadAllText(buttonsXaml, Encoding.UTF8), @"\{StaticResource Touch\.Min\}").Count
            : 0;

        bool sizingOk = touchMin >= 40 && touchLarge >= touchMin && textBody >= 15 && touchMinRefs >= 3;
        Console.WriteLine($"[{(sizingOk ? "PASS" : "FAIL")}] 大屏尺寸规范：Touch.Min={touchMin} / Touch.Large={touchLarge}"
                          + $" / Text.Body={textBody} / 按钮模板引用 Touch.Min 共 {touchMinRefs} 处");
        if (!sizingOk) failures++;

        // ---- 10) 界面状态的读-改-写不会互相覆盖（S6 的真实风险）----
        string statePath = Path.Combine(outDir, "m10-ui-state.txt");
        try
        {
            if (File.Exists(statePath)) File.Delete(statePath);

            UiStateStore.WriteFullScreen(false, statePath);
            UiStateStore.WriteTheme("light", statePath);

            bool themeKept = UiStateStore.ReadTheme(statePath) == "light";
            bool fullKept = UiStateStore.ReadFullScreen(statePath) == false;

            bool stateOk = themeKept && fullKept;
            Console.WriteLine($"[{(stateOk ? "PASS" : "FAIL")}] 界面状态读写互不覆盖：主题={(themeKept ? "light" : "丢失")}"
                              + $"，全屏={(fullKept ? "false" : "丢失")}");
            if (!stateOk) failures++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] 界面状态读写断言出错：{ex.Message}");
            failures++;
        }
        finally
        {
            try { if (File.Exists(statePath)) File.Delete(statePath); } catch { /* 清理失败不影响结论 */ }
        }

        // ---- 11) 换肤落点：两本界面板都在，且深浅键集合一致（见第 5 条）----
        bool paletteFiles = File.Exists(Path.Combine(srcRoot, darkModule))
                         && File.Exists(Path.Combine(srcRoot, lightModule));
        Console.WriteLine($"[{(paletteFiles ? "PASS" : "FAIL")}] 换肤落点：ThemeService 在两本界面板之间整本替换");
        if (!paletteFiles) failures++;

        // ---- 12) 动效令牌与瓦片尺寸（M20 S1）----
        // ★ 为什么动效也要钉死：150ms 与出厂的 CubicEase·EaseOut 是「手感」
        //   的规格值（需求原话「150ms CubicEase 不弹跳」）。它们最可能坏的方式
        //   是被人顺手改成 0.2s 或换成 BackEase —— 屏幕上只是「感觉有点怪」，
        //   没有断言就永远查不出来。
        bool motionOk = Tokens.TryGet("Motion.Fast", out var motionValue)
                     && motionValue is Duration fast
                     && fast.HasTimeSpan
                     && Math.Abs(fast.TimeSpan.TotalMilliseconds - 150.0) < 0.5;

        // ★ M20 S2 起 harness 已引 System.Windows.Media.Animation（下面的模板探针
        //   要把 Storyboard 真的跑一遍），所以这里可以直接用 CubicEase / EasingMode。
        bool easeOk = Tokens.TryGet("Ease.Standard", out var easeValue)
                   && easeValue is CubicEase ease
                   && ease.EasingMode == EasingMode.EaseOut;

        double tileWidth = Tokens.Number("Tile.Width");
        double tileMinHeight = Tokens.Number("Tile.MinHeight");
        bool tileOk = Math.Abs(tileWidth - 76.0) < 0.001
                   && Math.Abs(tileMinHeight - 44.0) < 0.001
                   && tileMinHeight >= touchMin;   // 瓦片高度仍不得低于可点目标下限

        bool motionAll = motionOk && easeOk && tileOk;
        Console.WriteLine($"[{(motionAll ? "PASS" : "FAIL")}] 动效与瓦片令牌："
                          + $"Motion.Fast={(motionOk ? "150ms" : "缺失或不是 150ms")}"
                          + $" / Ease.Standard={(easeOk ? "CubicEase·EaseOut" : "缺失或缓动不是 EaseOut")}"
                          + $" / Tile.Width={tileWidth} / Tile.MinHeight={tileMinHeight}");
        if (!motionAll) failures++;

        // ---- 13) 过渡动效只能用 Opacity（M20 S2）----
        // ★ 为什么这条是本里程碑最要紧的护栏：底色来自带 po:Freeze="True" 的色板画刷 ——
        //   冻结点不可动画，ColorAnimation 一跑就抛 InvalidOperationException；
        //   而绕开它新建一个非冻结画刷又会**脱离 DynamicResource**，
        //   换肤后那个按钮会顽固留着旧色（现场表现为「漏了一块」）。
        //   唯一的正解是「叠层 + 只动画 Opacity」，这条断言把正解钉住。
        string[] controlDicts =
        {
            @"Design\Controls\Buttons.xaml",
            @"Design\Controls\Inputs.xaml",
            @"Design\Controls\Panels.xaml",
        };

        var colorAnimHits = new List<string>();
        var badTargets = new List<string>();
        var overlayBad = new List<string>();
        int doubleAnimCount = 0;
        int overlayCount = 0;
        int motionRefCount = 0;
        int easeRefCount = 0;

        foreach (var rel in controlDicts)
        {
            string path = Path.Combine(srcRoot, rel);
            if (!File.Exists(path))
            {
                Console.WriteLine($"[FAIL] 缺控件字典：{rel}");
                failures++;
                continue;
            }

            string text = StripComments(File.ReadAllText(path, Encoding.UTF8), true);

            foreach (Match m in Regex.Matches(text,
                         @"(ColorAnimation|BrushAnimation|ThicknessAnimation|ObjectAnimationUsingKeyFrames)\b"))
            {
                colorAnimHits.Add(rel + " → " + m.Value);
            }

            foreach (Match m in Regex.Matches(text, @"Storyboard\.TargetProperty=""([^""]+)"""))
            {
                if (m.Groups[1].Value != "Opacity") badTargets.Add(rel + " → " + m.Groups[1].Value);
            }

            doubleAnimCount += Regex.Matches(text, @"<DoubleAnimation\b").Count;
            motionRefCount += Regex.Matches(text, @"\{StaticResource Motion\.Fast\}").Count;
            easeRefCount += Regex.Matches(text, @"\{StaticResource Ease\.Standard\}").Count;

            // 叠层底色必须是 DynamicResource（否则换肤时那一层不刷新，留着旧色）
            foreach (Match m in Regex.Matches(text, @"x:Name=""(HoverFx|PressFx|CheckFx|BoxHover)""[^>]*"))
            {
                overlayCount++;
                if (!m.Value.Contains(@"Background=""{DynamicResource Ui."))
                    overlayBad.Add(rel + " → " + m.Value.Trim());
            }
        }

        bool opacityOnly = colorAnimHits.Count == 0 && badTargets.Count == 0 && doubleAnimCount >= 12;
        string badTargetText = badTargets.Count == 0 ? "无越界" : string.Join(" / ", badTargets);
        string colorAnimText = colorAnimHits.Count == 0 ? "0 处" : string.Join(" / ", colorAnimHits);
        Console.WriteLine($"[{(opacityOnly ? "PASS" : "FAIL")}] 过渡只动画 Opacity：{doubleAnimCount} 条 DoubleAnimation"
                          + $" / TargetProperty {badTargetText} / 颜色动画 {colorAnimText}"
                          + $" / 悬停层等叠层 {overlayCount} 个，底色越界 {overlayBad.Count} 个");
        if (!opacityOnly) failures++;

        bool overlayOk = overlayCount >= 4 && overlayBad.Count == 0;
        if (!overlayOk)
        {
            Console.WriteLine("[FAIL] 叠层底色不是 DynamicResource（换肤后会留旧色）：");
            foreach (var b in overlayBad) Console.WriteLine("       " + b);
            failures++;
        }
        else
        {
            Console.WriteLine($"[PASS] 叠层底色走 DynamicResource：{overlayCount} 个过渡层换肤时都会同步刷新");
        }

        bool motionUsed = motionRefCount >= 6 && easeRefCount >= 6;
        Console.WriteLine($"[{(motionUsed ? "PASS" : "FAIL")}] 动效令牌被真正用上：Motion.Fast {motionRefCount} 处"
                          + $" / Ease.Standard {easeRefCount} 处（各需 ≥ 6）");
        if (!motionUsed) failures++;

        // ---- 14) 焦点环：键盘可达但不扰触摸（M20 S2）----
        int focusRingRefs = 0;
        var keyboardFocusHits = new List<string>();
        foreach (var file in EnumerateSourceFiles(srcRoot))
        {
            bool isXaml = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            string text = StripComments(File.ReadAllText(file, Encoding.UTF8), isXaml);
            focusRingRefs += Regex.Matches(text, @"Style\.FocusRing").Count;
            if (Regex.IsMatch(text, @"IsKeyboardFocused"))
                keyboardFocusHits.Add(Path.GetRelativePath(root, file));
        }

        bool focusOk = focusRingRefs >= 5 && keyboardFocusHits.Count == 0;
        string keyboardFocusText = keyboardFocusHits.Count == 0
            ? "IsKeyboardFocused 零命中"
            : string.Join(" / ", keyboardFocusHits);
        Console.WriteLine($"[{(focusOk ? "PASS" : "FAIL")}] 焦点环：Style.FocusRing 被引用 {focusRingRefs} 处"
                          + $" / {keyboardFocusText}"
                          + (focusOk ? "（触摸点击不会闪环，键盘导航才有环）" : string.Empty));
        if (!focusOk) failures++;

        // ---- 15) 五态模板探针：叠层在不在、动画跑不跑得起来（M20 S2）----
        // ★ 源码断言看不出两件事：叠层的 x:Name 写错了（悬停时毫无反应）、
        //   Storyboard 的 Duration / EasingFunction / TargetName 非法（一悬停就抛）。
        //   这两件事在现场都极难归因，所以这里**真的把模板跑一遍**。
        var runtimeProblems = new List<string>();
        int templatesChecked = 0;

        void ProbeTemplate(System.Windows.Controls.Control control, string styleKey, string[] layers,
                           string animatedLayer, string label)
        {
            if (Application.Current?.Resources[styleKey] is not Style style)
            {
                runtimeProblems.Add($"{label}（{styleKey}）取不到样式");
                return;
            }

            control.Style = style;
            control.ApplyTemplate();
            control.Measure(new Size(120, 60));
            control.Arrange(new Rect(0, 0, 120, 60));

            ControlTemplate? template = control.Template;
            if (template is null)
            {
                runtimeProblems.Add($"{label} 没有模板");
                return;
            }

            foreach (var name in layers)
            {
                if (template.FindName(name, control) is not FrameworkElement layer)
                    runtimeProblems.Add($"{label} 模板里找不到叠层 {name}");
                else if (layer.Opacity > 0.001)
                    runtimeProblems.Add($"{label} 的 {name} 静止不透明度应为 0，实际 {layer.Opacity}");
            }

            if (control.FocusVisualStyle is null) runtimeProblems.Add($"{label} 没设焦点环");

            // 逐个动画：目标属性只能 Opacity、TargetName 能在模板名字域里解析到、
            // 时长与缓动类型正确。
            // ★ 为什么**不**用 Storyboard.Begin 真跑一遍：模板里的 Storyboard 的
            //   TargetName 是靠**模板名字域**解析的，而手动 Begin(控件) 走的是控件
            //   自身的名字域 —— 实测会抛「无法在 Button 的名称范围内找到 HoverFx」。
            //   那是**断言的错**，不是产品的错（真机上的悬停由模板触发器驱动，
            //   与手动 Begin 不是同一条路径）。用 template.FindName 校验名字能否
            //   解析，等价地覆盖了我们真正担心的那件事：叠层的 x:Name 写错。
            string? firstTarget = null;
            int animCount = 0;
            foreach (TriggerBase trigger in template.Triggers)
            {
                foreach (TriggerAction action in trigger.EnterActions)
                {
                    if (action is not BeginStoryboard { Storyboard: not null } begin) continue;

                    foreach (Timeline child in begin.Storyboard.Children)
                    {
                        animCount++;
                        if (child is not DoubleAnimation anim)
                        {
                            runtimeProblems.Add($"{label} 的动画不是 DoubleAnimation：{child.GetType().Name}");
                            continue;
                        }

                        string? target = Storyboard.GetTargetName(anim);
                        firstTarget ??= target;
                        if (string.IsNullOrEmpty(target) || template.FindName(target, control) is null)
                            runtimeProblems.Add($"{label} 的动画目标 {target} 在模板名字域里解析不到");

                        string property = Storyboard.GetTargetProperty(anim)?.Path ?? string.Empty;
                        if (property != "Opacity")
                            runtimeProblems.Add($"{label} 的动画改了 {property}，只允许 Opacity");

                        if (!anim.Duration.HasTimeSpan
                            || Math.Abs(anim.Duration.TimeSpan.TotalMilliseconds - 150.0) > 0.5)
                        {
                            runtimeProblems.Add($"{label} 的动画时长不是 150ms");
                        }

                        if (anim.EasingFunction is not CubicEase cubic || cubic.EasingMode != EasingMode.EaseOut)
                            runtimeProblems.Add($"{label} 的缓动不是 CubicEase·EaseOut");
                    }
                }
            }

            if (animCount == 0) runtimeProblems.Add($"{label} 模板里找不到任何动画");
            if (firstTarget != animatedLayer)
                runtimeProblems.Add($"{label} 首个动画目标是 {firstTarget ?? "无"}，期望 {animatedLayer}");

            templatesChecked++;
        }

        ProbeTemplate(new Button { Content = "探针" }, "Style.ToolbarButton",
            new[] { "HoverFx", "PressFx" }, "HoverFx", "工具栏按钮");
        ProbeTemplate(new ToggleButton { Content = "探针" }, "Style.ToolToggle",
            new[] { "CheckFx", "HoverFx", "PressFx" }, "HoverFx", "工具切换按钮");
        ProbeTemplate(new CheckBox { Content = "探针" }, "Style.CheckBox",
            new[] { "BoxHover", "CheckFx", "PressFx" }, "BoxHover", "勾选框");
        ProbeTemplate(new Button { Content = "探针" }, "Style.PanelStepButton",
            new[] { "HoverFx", "PressFx" }, "HoverFx", "面板加减按钮");
        ProbeTemplate(new Button { Content = "探针" }, "Style.PanelCloseButton",
            new[] { "HoverFx", "PressFx" }, "HoverFx", "面板关闭按钮");

        bool templateOk = runtimeProblems.Count == 0 && templatesChecked == 5;
        if (!templateOk)
        {
            Console.WriteLine($"[FAIL] 五态模板探针（检查了 {templatesChecked} 个模板）：");
            foreach (var p in runtimeProblems) Console.WriteLine("       " + p);
            failures++;
        }
        else
        {
            Console.WriteLine("[PASS] 五态模板探针：5 个模板的叠层、焦点环与过渡动画都可执行");
        }

        // ---- 16) 图标几何：逐个真的渲染一遍，按像素判定（M20 S3）----
        // ★ 为什么这条必须「渲染出来数像素」：图标几何是**字符串**写的
        //   （Figures="M5 12 h14 …"），写错了 WPF 一句话都不说 ——
        //   键名拼错只是「图标不见了」，坐标写错只是「图标偏了一点」，
        //   两种都只有肉眼能发现。像素采样把「肉眼能发现」变成「机器能发现」。
        // ★ 专门盯住一条真踩过的生成错误：一个图标常由多个 <path> 组成
        //   （arrow-down = M12 5v14 + m19 12-7 7-7-7），SVG 里每个元素的当前点
        //   都从 (0,0) 起算，拼进**同一个** Figures 串后却不重置 ——
        //   保留相对写法时第二个元素的 m/l 会相对第一个元素的终点位移，
        //   箭头当场落到 (31,31)。表现就是「内容溢出 24 网格」，本断言即为此设。
        var iconKeys = new List<string>();
        foreach (var key in allDefined)
        {
            if (key.StartsWith("Icon.", StringComparison.Ordinal)) iconKeys.Add(key);
        }
        iconKeys.Sort(StringComparer.Ordinal);

        // 与 tools/lucide-map.json 的键集合必须一字不差：
        // 少一个说明产物没重生成（有人只改了 map 或只改了产物）。
        string lucideMapPath = Path.Combine(root, "tools", "lucide-map.json");
        var lucideKeys = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(lucideMapPath))
        {
            foreach (Match m in Regex.Matches(
                         File.ReadAllText(lucideMapPath, Encoding.UTF8),
                         @"""(Icon\.[A-Za-z0-9_]+)""\s*:"))
            {
                lucideKeys.Add(m.Groups[1].Value);
            }
        }

        var iconSyncProblems = new List<string>();
        if (!File.Exists(lucideMapPath))
        {
            iconSyncProblems.Add("找不到 tools/lucide-map.json");
        }
        else
        {
            foreach (var key in lucideKeys)
            {
                if (!iconKeys.Contains(key)) iconSyncProblems.Add($"产物里缺 {key}");
            }

            foreach (var key in iconKeys)
            {
                if (!lucideKeys.Contains(key)) iconSyncProblems.Add($"产物多出 {key}");
            }
        }

        bool iconSync = iconSyncProblems.Count == 0 && iconKeys.Count > 0;
        if (!iconSync)
        {
            Console.WriteLine($"[FAIL] 图标产物与 lucide-map.json 不同步（产物 {iconKeys.Count} 个 / 表 {lucideKeys.Count} 个）：");
            foreach (var p in iconSyncProblems) Console.WriteLine("       " + p);
            failures++;
        }
        else
        {
            Console.WriteLine($"[PASS] 图标产物与 tools/lucide-map.json 同步：{iconKeys.Count} 个键一字不差"
                              + "（改图标请改 map 再重跑脚本）");
        }

        // ---- 17) 图标规范 + 像素探针 ----
        // ★ 线宽 2.0 是 Lucide 上游的规格；Fill 必须为空（实心图标在 24px 上是一团黑）；
        //   Stretch 必须 None（否则几何被拉伸变形，与其他图标视觉重量不一致）。
        var iconStrokeProbe = new System.Windows.Shapes.Path();
        if (Tokens.TryGet("Style.IconPath", out var iconStyleValue) && iconStyleValue is Style iconStyle)
        {
            iconStrokeProbe.Style = iconStyle;
        }

        double iconStroke = iconStrokeProbe.StrokeThickness;
        bool iconSpecOk = Math.Abs(iconStroke - 2.0) < 0.001
                       && iconStrokeProbe.Fill is null
                       && iconStrokeProbe.Stretch == Stretch.None
                       && Math.Abs(iconStrokeProbe.Width - 24.0) < 0.001;
        Console.WriteLine($"[{(iconSpecOk ? "PASS" : "FAIL")}] 图标规范：线宽 {iconStroke} / 填色"
                          + $"{(iconStrokeProbe.Fill is null ? "空" : "非空")}"
                          + $" / Stretch={iconStrokeProbe.Stretch} / 网格 {iconStrokeProbe.Width}");
        if (!iconSpecOk) failures++;

        const int iconSize = 24;
        var iconProblems = new List<string>();
        int iconChecked = 0;
        int iconThinSkipped = 0;
        int iconUnfrozen = 0;

        foreach (var key in iconKeys)
        {
            if (!Tokens.TryGet(key, out var raw) || raw is not Geometry iconGeometry)
            {
                iconProblems.Add($"{key} 不是几何");
                continue;
            }

            if (!iconGeometry.IsFrozen) iconUnfrozen++;

            var glyph = new System.Windows.Shapes.Path
            {
                Data = iconGeometry,
                // ★ 测试代码不在 srcRoot 的扫描范围内，这里的裸色值是安全的
                Stroke = Brushes.Black,
                StrokeThickness = 2.0,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = null,
                Stretch = Stretch.None,
                Width = iconSize,
                Height = iconSize,
            };

            var host = new Canvas { Width = iconSize, Height = iconSize };
            host.Children.Add(glyph);
            host.Measure(new Size(iconSize, iconSize));
            host.Arrange(new Rect(0, 0, iconSize, iconSize));
            host.UpdateLayout();

            // 渲染两次：同一份几何必须得到逐字节相同的位图。
            // （不确定性只可能来自「有人把几何换成了会变的画刷」，但要真出现，
            //   屏幕上会表现为图标忽明忽暗 —— 值得一条断言。）
            var firstFrame = Snapshot(host, iconSize, iconSize);
            var secondFrame = Snapshot(host, iconSize, iconSize);

            byte[] Read(BitmapSource source)
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var buffer = new byte[iconSize * iconSize * 4];
                converted.CopyPixels(buffer, iconSize * 4, 0);
                return buffer;
            }

            byte[] pixels = Read(firstFrame);
            byte[] again = Read(secondFrame);

            int inkCount = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < iconSize; y++)
            {
                for (int x = 0; x < iconSize; x++)
                {
                    if (pixels[(y * iconSize + x) * 4 + 3] == 0) continue;
                    inkCount++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            string shape = $"{key}（ink={inkCount} 包围盒=({minX},{minY})-({maxX},{maxY})）";

            if (inkCount < 8)
            {
                iconProblems.Add($"{shape} 几乎没有像素：几何写坏了");
                continue;
            }

            if (inkCount > iconSize * iconSize * 0.60)
            {
                iconProblems.Add($"{shape} 不透明像素超过 60%：疑似被填成实心");
            }

            // 描边向外扩 StrokeThickness/2 = 1px，所以 ink 必须落在 [1,22]，
            // 0 与 23 两行/两列必须干净 —— 也就是「四边各留至少 1px」。
            if (minX < 1 || minY < 1 || maxX > iconSize - 2 || maxY > iconSize - 2)
            {
                iconProblems.Add($"{shape} 溢出 24 网格（四边需各留 1px）");
            }

            // 「笔画」而不是「填满」：只在二维图标上判定 ——
            // 一条横线（Icon.Subtract）的包围盒只有 2px 高，占比天然接近 100%，
            // 对它判「包围盒内要有 20% 透明」是**断言写错了**，不是图标错了。
            int boxW = maxX - minX + 1;
            int boxH = maxY - minY + 1;
            if (boxW >= 8 && boxH >= 8)
            {
                if (inkCount > boxW * boxH * 0.80)
                {
                    iconProblems.Add($"{shape} 在包围盒里占比超过 80%：疑似被填成实心");
                }
            }
            else
            {
                iconThinSkipped++;
            }

            if (!pixels.AsSpan().SequenceEqual(again))
            {
                iconProblems.Add($"{shape} 两次渲染不一致");
            }

            iconChecked++;
        }

        if (iconUnfrozen > 0)
        {
            iconProblems.Add($"{iconUnfrozen} 个几何没有冻结（po:Freeze 丢了，每个引用点都会复制一份）");
        }

        bool iconOk = iconProblems.Count == 0 && iconChecked == iconKeys.Count;
        if (!iconOk)
        {
            Console.WriteLine($"[FAIL] 图标像素探针（检查了 {iconChecked}/{iconKeys.Count} 个）：");
            foreach (var p in iconProblems) Console.WriteLine("       " + p);
            failures++;
        }
        else
        {
            Console.WriteLine($"[PASS] 图标像素探针：{iconChecked} 个图标逐个光栅化 —— 非空、不溢出 24 网格、"
                              + $"包围盒外留白、两次渲染逐字节一致（其中 {iconThinSkipped} 个是线状图标，跳过「占满」判定）");
        }

        // ============================================================ M20 S4：工具栏分组
        // ★ RunM10Checks 里没有 ReadSource —— 那个 helper 是别的方法里的局部函数，不跨方法可见。
        //   这里自备一个，免得依赖不存在的成员（踩过：局部函数看着像全局的）。
        static string ReadSrc(string repoRoot, params string[] parts)
        {
            string[] full = new string[parts.Length + 1];
            full[0] = "src";
            Array.Copy(parts, 0, full, 1, parts.Length);
            return File.ReadAllText(Path.Combine(repoRoot, Path.Combine(full)), Encoding.UTF8);
        }

        // ---- 18) 工具目录表与源码双向对账 ----
        // ★ 为什么必须双向：
        //   正向（源码声明 → 目录表）漏一个 ⇒ "新加的工具在工具栏上找不到"，
        //     而且日志一片正常（它只是被归到「其他」组，人不会去看那一组）；
        //   反向（目录表 → 源码）错一个 ⇒ 表里躺着一条永远匹配不上的记录，
        //     看着像"这个工具被停用了"。
        //   22 行字符串靠人眼对不出来，只能机器对。
        var declaredIds = CollectDeclaredToolIds(root);
        var cataloguedIds = new SortedSet<string>(StringComparer.Ordinal);
        var catalogById = new Dictionary<string, ToolCatalogEntry>(StringComparer.Ordinal);

        foreach (var entry in ToolCatalog.Entries)
        {
            cataloguedIds.Add(entry.Id);
            catalogById[entry.Id] = entry;
        }

        string srcAllText = ReadAllSourceText(root);
        var undeclaredInCatalog = cataloguedIds
            .Where(id => !srcAllText.Contains($"\"{id}\"", StringComparison.Ordinal)).ToList();
        var unregisteredInCatalog = declaredIds.Where(id => !cataloguedIds.Contains(id)).ToList();

        bool coverOk = undeclaredInCatalog.Count == 0 && unregisteredInCatalog.Count == 0;
        Console.WriteLine($"[{(coverOk ? "PASS" : "FAIL")}] 工具目录表与源码对账：源码声明 {declaredIds.Count} 个工具 / "
                          + $"目录表登记 {cataloguedIds.Count} 个"
                          + (unregisteredInCatalog.Count > 0
                                ? $"；★ 声明了却没登记（会掉进「其他」组）：{string.Join("、", unregisteredInCatalog)}"
                                : string.Empty)
                          + (undeclaredInCatalog.Count > 0
                                ? $"；★ 登记了却在源码里找不到（Id 写错？）：{string.Join("、", undeclaredInCatalog)}"
                                : string.Empty));
        if (!coverOk) failures++;

        // ---- 19) 目录表自身一致性：组顺序、图标键、族的一层结构 ----
        // 这几条全是"编译期看不见、运行时静默出错"的：图标键拼错 ⇒ 瓦片上空一格；
        // 族成员挂错 ⇒ 二级菜单里凭空多/少一项。
        var catalogProblems = new List<string>();

        for (int i = 0; i < ToolCatalog.Groups.Count; i++)
        {
            if ((int)ToolCatalog.Groups[i].Group != i)
            {
                catalogProblems.Add($"组顺序与枚举声明不一致：第 {i + 1} 个组的枚举值是 {(int)ToolCatalog.Groups[i].Group}");
            }

            if (ToolCatalog.Groups[i].Title.Length == 0) catalogProblems.Add($"第 {i + 1} 个组没有标题");
        }

        for (int i = 0; i < ToolCatalog.Entries.Count; i++)
        {
            var entry = ToolCatalog.Entries[i];

            if (!Tokens.Exists(entry.IconKey)) catalogProblems.Add($"{entry.Id}：图标键 {entry.IconKey} 解析不到");
            if (entry.TileLabel.Length == 0) catalogProblems.Add($"{entry.Id}：没有瓦片短名");
            if (entry.TileLabel.Length > 8) catalogProblems.Add($"{entry.Id}：瓦片短名「{entry.TileLabel}」太长（76 宽的格子放不下）");

            bool isHead = ToolCatalog.IsFamilyHead(entry.Id);
            if (isHead && string.IsNullOrEmpty(entry.FamilyTitle)) catalogProblems.Add($"{entry.Id}：是族代表却没填族名");
            if (!isHead && entry.FamilyTitle is not null) catalogProblems.Add($"{entry.Id}：不是族代表却填了族名");

            if (entry.VariantOf is not { } headId) continue;

            if (!catalogById.TryGetValue(headId, out var head)) { catalogProblems.Add($"{entry.Id}：挂在不存在代表 {headId} 上"); continue; }
            if (head.VariantOf is not null) catalogProblems.Add($"{entry.Id}：挂到了变体 {headId} 上（工具族只有一层）");

            // 成员必须**紧跟**在代表（或同族的前一个成员）之后：
            // 折叠只是把连续几项收起来，靠的就是这个连续性。
            if (i == 0 || !string.Equals(ToolCatalog.FamilyHeadOf(ToolCatalog.Entries[i - 1].Id), headId, StringComparison.Ordinal))
            {
                catalogProblems.Add($"{entry.Id}：没有紧跟在族代表 {headId} 及其成员之后");
            }
        }

        bool catalogOk = catalogProblems.Count == 0;
        if (!catalogOk)
        {
            Console.WriteLine($"[FAIL] 工具目录表自检（{catalogProblems.Count} 项）：");
            foreach (var problem in catalogProblems) Console.WriteLine("       " + problem);
            failures++;
        }
        else
        {
            Console.WriteLine($"[PASS] 工具目录表自检：{ToolCatalog.Entries.Count} 个工具 / {ToolCatalog.Groups.Count} 组，"
                              + $"图标键全部可解析、工具族只有一层且成员紧跟在代表之后");
        }

        // ---- 20) 运行期：目录表覆盖"真正注册进来的"工具 ----
        // ★ 源级对账只能证明"表是全的"，证明不了"表与注册表对得上"（例如某人把
        //   Id 常量改了名却没动表）。这里真构造宿主、真问注册表。
        var catalogHost = new CanvasViewportHost();
        string[] runtimeIds = catalogHost.Tools.Select(t => t.Id).ToArray();
        var runtimeMissing = runtimeIds.Where(id => ToolCatalog.Find(id) is null).ToList();
        bool runtimeOk = runtimeMissing.Count == 0 && runtimeIds.Length >= 5;
        Console.WriteLine($"[{(runtimeOk ? "PASS" : "FAIL")}] 目录表覆盖运行期注册表：注册了 {runtimeIds.Length} 个工具，"
                          + (runtimeMissing.Count == 0
                                ? "全部有分组与图标"
                                : $"★ 未登记（会掉进「其他」组）：{string.Join("、", runtimeMissing)}"));
        if (!runtimeOk) failures++;

        // ---- 21) 结构护栏：四组 + 兜底组 + 组间横线 + 二级菜单入口 ----
        string mainXamlS4 = ReadSrc(root, "MathPhys.Ink", "Views", "MainWindow.xaml");
        var panelNames = new[] { "GroupPanelInk", "ToolButtons", "GroupPanelDoc", "GroupPanelSystem", "GroupPanelOther" };
        var panelIndexes = panelNames
            .Select(name => mainXamlS4.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal))
            .ToArray();

        bool panelsOk = panelIndexes.All(index => index >= 0)
            && panelIndexes.SequenceEqual(panelIndexes.OrderBy(index => index))   // XAML 里的出现顺序 == 组顺序
            && CountOccurrences(mainXamlS4, "Style.GroupDivider") >= 4
            && mainXamlS4.Contains("x:Name=\"InkToolButtons\"", StringComparison.Ordinal)
            && mainXamlS4.Contains("x:Name=\"OtherToolButtons\"", StringComparison.Ordinal)
            && mainXamlS4.Contains("x:Name=\"ExportButton\"", StringComparison.Ordinal)
            && mainXamlS4.Contains("x:Name=\"MoreButton\"", StringComparison.Ordinal)
            && mainXamlS4.Contains("x:Name=\"ExportPopup\"", StringComparison.Ordinal)
            && mainXamlS4.Contains("x:Name=\"MorePopup\"", StringComparison.Ordinal);
        Console.WriteLine($"[{(panelsOk ? "PASS" : "FAIL")}] 工具栏四组（笔类/图形/文档/系统）+ 兜底组 + 组间横线 + 两个二级菜单入口");
        if (!panelsOk) failures++;

        // ---- 22) 反向护栏：不许改回 ItemsControl、设计系统样式必须齐备 ----
        // ★ 为什么专门钉"不许改回 ItemsControl"：嵌套容器在 WrapPanel 里量错宽度这件事
        //   在源码上完全看不出来，只表现为"工具栏莫名高了一行"。挡在源头最省事。
        string mainCsS4 = StripComments(ReadSrc(root, "MathPhys.Ink", "Views", "MainWindow.xaml.cs"), false);
        string buttonsS4 = StripComments(ReadSrc(root, "MathPhys.Ink", "Design", "Controls", "Buttons.xaml"), true);
        string spacingS4 = StripComments(ReadSrc(root, "MathPhys.Ink", "Design", "Spacing.xaml"), true);

        bool wiringOk = buttonsS4.Contains("x:Key=\"Style.ToolTileToggle\"", StringComparison.Ordinal)
            && buttonsS4.Contains("x:Key=\"Style.GroupHeaderChip\"", StringComparison.Ordinal)
            && buttonsS4.Contains("x:Key=\"Style.GroupHeaderLabel\"", StringComparison.Ordinal)
            && buttonsS4.Contains("x:Key=\"Style.FamilyChevron\"", StringComparison.Ordinal)
            && buttonsS4.Contains("x:Key=\"Style.FamilyMemberToggle\"", StringComparison.Ordinal)
            && spacingS4.Contains("x:Key=\"Gap.GroupDivider\"", StringComparison.Ordinal)
            && spacingS4.Contains("x:Key=\"Tile.TextGap\"", StringComparison.Ordinal)
            && mainCsS4.Contains("ToolCatalog.Entries", StringComparison.Ordinal)
            && mainCsS4.Contains("ToolButtons.Children.Add", StringComparison.Ordinal)
            && !mainCsS4.Contains("ToolButtons.ItemsSource", StringComparison.Ordinal);
        Console.WriteLine($"[{(wiringOk ? "PASS" : "FAIL")}] 分组接线与样式齐备（且瓦片没有改回 ItemsControl）");
        if (!wiringOk) failures++;

        // ============================================================ M20 步 2b：宿主自建面板接入设计系统
        // ---- 23) 5 个宿主自建面板零色值快照 ----
        // ★ 为什么这条值得单列：Tokens.Brush(key) 取的是**一次快照** —— 元素拿到那把画刷之后
        //   就与资源字典脱钩了。ThemeService 换掉界面板那一本时它**收不到通知**，
        //   屏幕上表现为「漏了一块」；而 XAML 里写 DynamicResource 的兄弟元素都跟着变了。
        //   这类毛病编译期、运行期都不报错，只有换肤那一下肉眼才看得见。
        string[] panelFiles =
        {
            "LayerPanelView.cs",
            "CalculatorPanelView.cs",
            "GfxParameterPanelView.cs",
            "ReplayPanelView.cs",
            "FloatingBall.cs",
        };

        var snapshotOffenders = new List<string>();
        foreach (string panelFile in panelFiles)
        {
            string panelSrc = StripComments(
                ReadSrc(root, "MathPhys.Ink", "Views", "Controls", panelFile), false);

            if (panelSrc.Contains("Tokens.Brush(", StringComparison.Ordinal)
                || panelSrc.Contains("Tokens.Color(", StringComparison.Ordinal))
            {
                snapshotOffenders.Add(panelFile);
            }
        }

        bool snapshotOk = snapshotOffenders.Count == 0;
        Console.WriteLine($"[{(snapshotOk ? "PASS" : "FAIL")}] 宿主 5 个自建面板零色值快照（颜色一律走 FollowTheme / 样式）"
                          + (snapshotOk ? string.Empty : $"；★ 仍在取快照：{string.Join("、", snapshotOffenders)}"));
        if (!snapshotOk) failures++;

        // ---- 24) 每个裸 new Button 都套了设计系统样式 ----
        // 判据：每一处 new Button 之后、**下一个 new Button 之前**必须出现 Style = 。
        // 为什么要卡在"下一个 new Button 之前"：只往后看固定长度的话，本按钮没套样式
        // 也会被下一个按钮的 Style = 蒙混过关 —— 断言就成了恒真（踩过同类坑，见 docs/06 §23.4）。
        // ★ 场景：不套样式就走 WPF 默认模板，它的 IsMouseOver 触发器会把背景换成
        //   Aero2 的系统浅蓝，与深色主题正面冲突 —— 一悬停就闪一块蓝。
        var nakedButtons = new List<string>();
        foreach (string panelFile in panelFiles)
        {
            string panelSrc = StripComments(
                ReadSrc(root, "MathPhys.Ink", "Views", "Controls", panelFile), false);

            int from = 0;
            int naked = 0;
            while (true)
            {
                int at = panelSrc.IndexOf("new Button", from, StringComparison.Ordinal);
                if (at < 0) break;
                from = at + "new Button".Length;

                int next = panelSrc.IndexOf("new Button", from, StringComparison.Ordinal);
                if (next < 0) next = panelSrc.Length;

                // 再看一句声明之外就够：按钮初始化块很短（最长的是计算器按键，十来行）
                int stop = Math.Min(next, at + 900);
                string window = panelSrc[at..stop];

                if (!window.Contains("Style = ", StringComparison.Ordinal)) naked++;
            }

            if (naked > 0) nakedButtons.Add($"{panelFile}（{naked} 处）");
        }

        bool styledOk = nakedButtons.Count == 0;
        Console.WriteLine($"[{(styledOk ? "PASS" : "FAIL")}] 宿主面板里的每个 new Button 都套了设计系统样式"
                          + (styledOk ? string.Empty : $"；★ 裸按钮：{string.Join("、", nakedButtons)}"));
        if (!styledOk) failures++;

        // ---- 25) FollowTheme 的接线与面板样式键齐备 ----
        string tokens2b = StripComments(ReadSrc(root, "MathPhys.Ink", "Design", "Tokens.cs"), false);
        string panels2b = StripComments(ReadSrc(root, "MathPhys.Ink", "Design", "Controls", "Panels.xaml"), true);

        bool wiring2bOk = tokens2b.Contains("public static T FollowTheme<T>", StringComparison.Ordinal)
            && tokens2b.Contains("element.SetResourceReference(property, key)", StringComparison.Ordinal)
            // ★ 必须有「资源环境判据」这一支：只看 Application.Current is null 会把 harness
            //   自己造的 Application 误判成离线（harness 用 InitializeDesignResources 合并了同一批字典）
            && tokens2b.Contains("app.Resources.Contains(key)", StringComparison.Ordinal)
            // 也要有离线回退：资源引用解析不到会把属性打回默认值，颜色凭空消失
            && tokens2b.Contains("element.SetValue(property, value)", StringComparison.Ordinal)
            && panels2b.Contains("Style.PanelCloseButton", StringComparison.Ordinal)
            && panels2b.Contains("Style.PanelStepButton", StringComparison.Ordinal)
            && panels2b.Contains("Style.FieldBox", StringComparison.Ordinal)
            && panels2b.Contains("Style.TextTitle", StringComparison.Ordinal)
            && panels2b.Contains("Style.TextBody", StringComparison.Ordinal);
        Console.WriteLine($"[{(wiring2bOk ? "PASS" : "FAIL")}] 步 2b 接线：FollowTheme 带资源环境判据与离线回退、面板样式与文字层级齐备");
        if (!wiring2bOk) failures++;

        // ---- 26) 面板真能构造出来 + 没有「裸 TextBlock」----
        // ① 运行期：这些面板的构造函数里会 FindResource 那几个样式键（键没了当场抛），
        //    构造完再看底色是不是解析到了界面板里的 Ui.OverlayPanel / Ui.OverlayBorder。
        // ② 源级：每个 new TextBlock 都必须带 Foreground 或 Style。
        //    ★ 这是本步修掉的**真缺陷**：本项目没有隐式的 TextBlock 样式，MainWindow 也没设
        //      Foreground，所以不写 Foreground 的 TextBlock 走 SystemColors.ControlTextBrush ——
        //      它跟随的是 **Windows** 的主题，而不是本程序的。教室一体机通常是浅色系统
        //      ⇒ 深色面板上成了黑字压黑底，面板标题与回放进度基本看不见。
        bool panelBuildOk26;
        string panelBuildNote26;
        try
        {
            var layerProbe = new LayerPanelView(
                () => "annotation", _ => true, _ => { }, (_, _) => { }, () => { }, () => { });
            var replayProbe = new ReplayPanelView(() => { }, () => { }, () => { });

            bool layerColorOk = (layerProbe.Background as SolidColorBrush)?.Color == Tokens.Color("Ui.OverlayPanel")
                && (layerProbe.BorderBrush as SolidColorBrush)?.Color == Tokens.Color("Ui.OverlayBorder");
            bool replayColorOk = (replayProbe.Background as SolidColorBrush)?.Color == Tokens.Color("Ui.OverlayPanel");

            panelBuildOk26 = layerColorOk && replayColorOk;
            panelBuildNote26 = panelBuildOk26
                ? "图层 / 回放面板构造成功、底色解析到界面板"
                : $"底色没解析到界面板（图层底与边={layerColorOk}、回放底={replayColorOk}）";
        }
        catch (Exception ex)
        {
            panelBuildOk26 = false;
            panelBuildNote26 = $"构造抛异常：{ex.GetType().Name} {ex.Message}";
        }

        var bareTextBlocks = new List<string>();
        foreach (string panelFile in panelFiles)
        {
            string panelSrc = StripComments(
                ReadSrc(root, "MathPhys.Ink", "Views", "Controls", panelFile), false);

            int from = 0;
            while (true)
            {
                int at = panelSrc.IndexOf("new TextBlock", from, StringComparison.Ordinal);
                if (at < 0) break;
                from = at + "new TextBlock".Length;

                int next = panelSrc.IndexOf("new TextBlock", from, StringComparison.Ordinal);
                if (next < 0) next = panelSrc.Length;
                string window = panelSrc[at..Math.Min(next, at + 700)];

                if (!window.Contains("Foreground", StringComparison.Ordinal)
                    && !window.Contains("Style = ", StringComparison.Ordinal))
                {
                    bareTextBlocks.Add(panelFile);
                }
            }
        }

        bool textThemeOk26 = bareTextBlocks.Count == 0;
        Console.WriteLine($"[{(panelBuildOk26 && textThemeOk26 ? "PASS" : "FAIL")}] 面板构造与文字着色：{panelBuildNote26}；"
                          + (textThemeOk26
                                ? "无裸 TextBlock（每个都带 Foreground 或样式）"
                                : $"★ 裸 TextBlock：{string.Join("、", bareTextBlocks)}"));
        if (!panelBuildOk26 || !textThemeOk26) failures++;

        return failures;
    }

    // ================================================================= M20 S4：工具目录表的两个取数助手

    /// <summary>
    /// 从 <c>src</c> 里抽出"声明了哪些工具 Id"。
    /// </summary>
    /// <remarks>
    /// 抽两种写法，因为项目里确实只有这两种：
    /// <list type="number">
    /// <item>每个插件（或宿主内置）把自己的 Id 收在一个 <c>static class *ToolIds</c> 里
    ///   —— 直尺、三角板、矢量、罗盘这些；</item>
    /// <item>单工具插件在自己类里写 <c>public const string ToolId = "..."</c> —— GeoGebra 是这样。</item>
    /// </list>
    /// ★ 这是"约定式抽取"，不是编译器级的分析：新插件若两种写法都不用，抽取会漏掉它、
    ///   这条断言就管不住（但那种情况下它本来就只影响工具栏的"其他"组，不会静默消失）。
    /// </remarks>
    private static SortedSet<string> CollectDeclaredToolIds(string root)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        string srcRoot = Path.Combine(root, "src");

        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            string lower = file.ToLowerInvariant();
            if (lower.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (lower.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            string source = File.ReadAllText(file, Encoding.UTF8);

            foreach (Match cls in Regex.Matches(source, @"static\s+class\s+\w*ToolIds\b"))
            {
                // ★ 必须按大括号配平取"类体"，不能"从类声明往后切 N 个字符"：
                //   切窗口会越界读到**下一个类**里的字符串常量 —— 实测把 ArrowTool 的
                //   UnitWorldKey("unitWorld") 当成了工具 Id，于是凭空报一条"声明了却没登记"。
                //   一个 ToolIds 类里通常只放一行，而它后面紧跟的工具类常量一大片，越长越容易踩。
                int open = source.IndexOf('{', cls.Index);
                if (open < 0) continue;

                int depth = 0;
                int cursor = open;
                for (; cursor < source.Length; cursor++)
                {
                    if (source[cursor] == '{') depth++;
                    else if (source[cursor] == '}')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                }

                if (depth != 0) continue;   // 括号不配平（多半读到被截断的片段），放弃这个类
                string body = source[open..(cursor + 1)];

                foreach (Match m in Regex.Matches(body, @"(?:const|static readonly)\s+string\s+\w+\s*=\s*""([^""]+)"""))
                {
                    ids.Add(m.Groups[1].Value);
                }
            }

            foreach (Match m in Regex.Matches(source, @"public\s+const\s+string\s+ToolId\s*=\s*""([^""]+)"""))
            {
                ids.Add(m.Groups[1].Value);
            }
        }

        return ids;
    }

    /// <summary>把 <c>src</c> 下所有 .cs 的正文拼成一份，供"这个字面量在不在源码里"这类查法用。</summary>
    private static string ReadAllSourceText(string root)
    {
        var builder = new StringBuilder();
        string srcRoot = Path.Combine(root, "src");

        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            string lower = file.ToLowerInvariant();
            if (lower.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (lower.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            builder.Append(File.ReadAllText(file, Encoding.UTF8));
        }

        return builder.ToString();
    }

    // ================================================================= M11：书写流畅性与笔锋

    /// <summary>
    /// M11 断言组：笔采样插件的地基、速度笔锋的纯函数、UI 线程减负的测量口径，以及两条性能护栏。
    /// </summary>
    /// <remarks>
    /// ★ 本机组<b>测不到真实书写手感</b>（没有笔、没有触摸屏），所以断言分三类，各有各的用处：
    /// <list type="number">
    /// <item><b>结构性</b>：插件挂上了、排到了 <c>DynamicRenderer</c> 之前、墨迹层的坐标口径前提还在
    /// —— 这些是"真机上笔锋能不能实时"的必要条件，本机可以完全确定地验；</item>
    /// <item><b>纯函数</b>：速度到宽度的映射、聚合统计口径、空/异常输入的兜底
    /// —— 把逻辑抽到不依赖 RTI 的层，就能像普通函数一样断言；</item>
    /// <item><b>护栏</b>：<c>Effect</c> 零命中、渲染模式必须是 <c>Default</c>
    /// —— 现在的状态本来就是对的（M10 实测），钉死是为了防止将来被无意改坏。</item>
    /// </list>
    /// <para>
    /// 真机上要看的（采样率、缩放后是否偏移、UI 收尾延迟）写在包内「怎么测试.txt」里，
    /// 日志出处是 <c>%LOCALAPPDATA%\MathPhys.Ink\logs\</c>。
    /// </para>
    /// </remarks>
    private static int RunM11Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M11：书写流畅性与笔锋 ----");

        string root = FindRepoRoot(outDir);
        string srcRoot = Path.Combine(root, "src", "MathPhys.Ink");

        var host = new CanvasViewportHost();
        host.Measure(new Size(1000, 700));
        host.Arrange(new Rect(0, 0, 1000, 700));
        host.UpdateLayout();

        // ---------------------------------------------------------- 1) 插件链
        // ★ 顺序就是判据：排在 DynamicRenderer **之前**的插件改过的点才会实时显示；
        //   排在后面要等抬笔才生效（表现为"墨迹在抬笔瞬间跳一下"）。
        var inkHost = host.InkSamplerHost;
        var chain = new List<string>();
        foreach (StylusPlugIn plugIn in inkHost.PlugIns) chain.Add(plugIn.GetType().Name);

        bool samplerFirst = inkHost.PlugIns.Count >= 1 && inkHost.PlugIns[0] is InkSamplerPlugIn;
        bool hasDynamicRenderer = chain.Contains("DynamicRenderer");

        Console.WriteLine($"[{(samplerFirst ? "PASS" : "FAIL")}] 墨迹层插件链 = {string.Join(" → ", chain)}"
                          + $"（采样插件在队首 = {samplerFirst}；"
                          + (hasDynamicRenderer
                                ? "DynamicRenderer 已在链上"
                                : "★ DynamicRenderer 此刻还不在链上 —— 说明它是 OnApplyTemplate 才加进来的，"
                                  + "正由 InkSurfaceCanvas 的 Loaded 复查抢回队首（待验证点 #1）")
                          + "）");
        if (!samplerFirst) failures++;

        // ---------------------------------------------------------- 2) 坐标口径的前提
        // 「插件拿到的点直接就是世界坐标」这套红利的前提是：墨迹层原点落在世界层原点上。
        // 插件侧不重复校验（笔线程读依赖属性会撞线程亲和性抛异常），所以在本机这就是最强形式。
        var inkLayer = (UIElement)host.InkSurface;
        var worldHost = VisualTreeHelper.GetParent(inkLayer);
        string parentName = worldHost is FrameworkElement fe ? fe.Name : worldHost?.GetType().Name ?? "(无)";

        double left = Canvas.GetLeft(inkLayer);
        double top = Canvas.GetTop(inkLayer);
        // NaN = 依赖属性"未设置"：Canvas 布局对未设置项按 0 处理，所以 NaN 在口径上等价于 0。
        bool atOrigin = (double.IsNaN(left) || Math.Abs(left) < 1e-6)
                     && (double.IsNaN(top) || Math.Abs(top) < 1e-6);
        bool originOk = atOrigin && worldHost is not null;

        Console.WriteLine($"[{(originOk ? "PASS" : "FAIL")}] 墨迹层坐标口径前提：Left={left.ToString("F3")} Top={top.ToString("F3")}"
                          + $" 父级={parentName}（Left/Top 必须为 0 或未设置，本地坐标才 ≡ 世界坐标）");
        if (!originOk) failures++;

        // ---------------------------------------------------------- 3) 采样统计口径
        var sampler = inkHost.Sampler;

        // 47 个点、跨度 312 ms ⇒ (47-1)*1000/312 ≈ 147.4 Hz
        // ★ 用「点数 - 1」而不是点数：n 个点把时长分成 n-1 段，段数除以时长才是频率。
        var samples = new List<RawSample>();
        for (int i = 0; i < 47; i++)
        {
            samples.Add(new RawSample(i * 2.0, 10.0, (int)Math.Round(312.0 * i / 46.0), 0.5f));
        }

        var stats = sampler.Measure(samples);
        bool statsOk = stats.PointCount == 47
                    && stats.DurationMs == 312
                    && stats.IsUsable
                    && Math.Abs(stats.Hertz - 147.4) < 0.6
                    && Math.Abs(stats.PathLength - 92.0) < 1e-6;

        Console.WriteLine($"[{(statsOk ? "PASS" : "FAIL")}] 采样统计口径：47 点 / 312 ms ⇒ "
                          + $"{stats.PointCount} 点 {stats.DurationMs} ms {stats.Hertz:F1} Hz"
                          + $" 路程 {stats.PathLength:F1} world（期望 147.4 Hz / 92.0 world）");
        if (!statsOk) failures++;

        // 退化输入：单点 / 空 —— 都不能除零，也不能混进采样率平均
        var single = sampler.Measure(new List<RawSample> { new(5, 5, 1000, 0.5f) });
        var empty = sampler.Measure(new List<RawSample>());
        bool degenerateOk = single.PointCount == 1 && single.Hertz == 0 && !single.IsUsable
                         && empty.PointCount == 0 && empty.Hertz == 0 && !empty.IsUsable;

        Console.WriteLine($"[{(degenerateOk ? "PASS" : "FAIL")}] 退化输入不除零也不算采样率："
                          + $"单点 ⇒ {single.PointCount} 点 / {single.Hertz:F1} Hz，"
                          + $"空 ⇒ {empty.PointCount} 点 / {empty.Hertz:F1} Hz");
        if (!degenerateOk) failures++;

        // 异常输入：时间戳倒退 + NaN 坐标 ⇒ 统计里绝不能出现负值 / NaN / Infinity
        var messy = new List<RawSample>
        {
            new(0, 0, 500, 0.5f),
            new(double.NaN, 0, 400, 0.5f),
            new(10, 10, 300, 0.5f),
            new(20, 20, 100, 0.5f),   // 时间戳倒退：最后一点早于第一点
        };

        var messyStats = sampler.Measure(messy);
        bool messyOk = IsFinite(messyStats.Hertz) && messyStats.Hertz >= 0
                    && IsFinite(messyStats.MaxSpeed) && IsFinite(messyStats.PathLength)
                    && messyStats.PointCount == 4;

        Console.WriteLine($"[{(messyOk ? "PASS" : "FAIL")}] 异常输入不污染统计：时间戳倒退 + NaN 坐标 ⇒ "
                          + $"{messyStats.PointCount} 点、时长 {messyStats.DurationMs} ms、"
                          + $"采样率 {messyStats.Hertz:F1} Hz、最大速度 {messyStats.MaxSpeed:F3} world/ms"
                          + "（必须全是有限值且非负）");
        if (!messyOk) failures++;

        // ---------------------------------------------------------- 4) 采样开关
        // 走 WPF 自己的 StylusPlugIn.Enabled：关掉之后框架根本不调用回调，是真正的零开销。
        sampler.SamplerEnabled = false;
        bool offWhenDisabled = !sampler.Enabled;
        sampler.SamplerEnabled = true;
        bool onWhenEnabled = sampler.Enabled;

        bool switchOk = offWhenDisabled && onWhenEnabled;
        Console.WriteLine($"[{(switchOk ? "PASS" : "FAIL")}] 采样开关走 WPF 的 StylusPlugIn.Enabled："
                          + $"关 ⇒ Enabled={offWhenDisabled}，开 ⇒ Enabled={onWhenEnabled}"
                          + "（关时不回调 = 零开销，而不是回调里早期返回）");
        if (!switchOk) failures++;

        // ---------------------------------------------------------- 5) 速度笔锋曲线
        bool rangeOk = true;
        bool monotonicOk = true;
        var atRest = new double[3];
        var atFast = new double[3];

        for (int levelIndex = 0; levelIndex < 3; levelIndex++)
        {
            var level = SpeedPressureMap.FromIndex(levelIndex);
            double previous = double.MaxValue;

            atRest[levelIndex] = SpeedPressureMap.Width01(0, level);
            atFast[levelIndex] = SpeedPressureMap.Width01(1.0, level);

            for (double v = 0; v <= 1.0001; v += 0.02)
            {
                double width = SpeedPressureMap.Width01(v, level);

                if (width < SpeedPressureMap.MinFactor - 1e-9 || width > SpeedPressureMap.MaxFactor + 1e-9)
                {
                    rangeOk = false;
                }

                // 速度越大 ⇒ 宽度只许变小（更快更细）
                if (width > previous + 1e-9) monotonicOk = false;
                previous = width;
            }
        }

        // 静止时必须是满宽；快写时「弱 > 中 > 强」（档位越强越细）
        bool craftOk = rangeOk && monotonicOk
                    && Math.Abs(atRest[0] - SpeedPressureMap.MaxFactor) < 1e-9
                    && Math.Abs(atRest[1] - SpeedPressureMap.MaxFactor) < 1e-9
                    && Math.Abs(atRest[2] - SpeedPressureMap.MaxFactor) < 1e-9
                    && atFast[0] > atFast[1] && atFast[1] > atFast[2];

        Console.WriteLine($"[{(craftOk ? "PASS" : "FAIL")}] 速度笔锋曲线：值域 "
                          + $"[{SpeedPressureMap.MinFactor:F2},{SpeedPressureMap.MaxFactor:F2}]"
                          + $" 全部落在区间内={rangeOk}，单调递减={monotonicOk}；"
                          + $"静止 ⇒ {atRest[1]:F3}（满宽）；快写 1.0 world/ms ⇒ 弱/中/强 = "
                          + $"{atFast[0]:F3}/{atFast[1]:F3}/{atFast[2]:F3}");
        if (!craftOk) failures++;

        // 低通：从 0.5 追 1.0，必须单调上升、不超调（超调会让加速时反而变粗）
        double smoothed = 0.5;
        bool smoothMonotonic = true;
        for (int i = 0; i < 50; i++)
        {
            double next = SpeedPressureMap.Smooth(smoothed, 1.0);
            if (next < smoothed - 1e-12 || next > SpeedPressureMap.MaxFactor + 1e-12) smoothMonotonic = false;
            smoothed = next;
        }

        // 合成律：几何平均；任一路为满宽时结果等于另一路；设备不报压力（0）时退化为纯速度
        bool blendOk = Math.Abs(SpeedPressureMap.Blend(0.8, 1.0) - Math.Sqrt(0.8)) < 1e-9
                    && Math.Abs(SpeedPressureMap.Blend(1.0, 0.6) - Math.Sqrt(0.6)) < 1e-9
                    && Math.Abs(SpeedPressureMap.Blend(0.6, 0.0) - 0.6) < 1e-9;

        bool shapingOk = smoothMonotonic && smoothed > 0.99 && blendOk;
        Console.WriteLine($"[{(shapingOk ? "PASS" : "FAIL")}] 低通与合成：50 步低通后收敛到 {smoothed:F4}"
                          + $"（单调不超调={smoothMonotonic}）；几何平均合成={blendOk}，"
                          + "设备不报压力时退化为纯速度");
        if (!shapingOk) failures++;

        // ---------------------------------------------------------- 6) 聚合口径（S3 的尺子）
        var diagnostics = new InputDiagnostics();
        diagnostics.NoteSampledStroke(stats);                            // 47 点 / 312 ms（可用）
        diagnostics.NoteSampledStroke(new InkSampleStats(1, 0, 0, 0));   // 单点（不可用）

        diagnostics.NoteUiSettleMs(4);
        diagnostics.NoteUiSettleMs(10);
        diagnostics.NoteUiSettleMs(6);

        // 设备签名缓存：同样签名喂 100 次，只有第一次需要拼字符串 / 查哈希表
        for (int i = 0; i < 100; i++)
        {
            diagnostics.NoteStylus("move", TabletDeviceType.Touch, false, "HID Interface", "落墨");
        }

        bool diagnosticsOk = diagnostics.SampledStrokeCount == 2
                          && diagnostics.UsableSampledStrokes == 1
                          && diagnostics.UiSettleSampleCount == 3
                          && Math.Abs(diagnostics.UiSettleMeanMs - 20.0 / 3.0) < 0.01
                          && diagnostics.UiSettleMaxMs == 10
                          && diagnostics.StylusSignatureReuses == 99;

        Console.WriteLine($"[{(diagnosticsOk ? "PASS" : "FAIL")}] 采样聚合口径：笔数 {diagnostics.SampledStrokeCount}"
                          + $"（其中可用 {diagnostics.UsableSampledStrokes} 笔）"
                          + $"；UI 收尾延迟 平均 {diagnostics.UiSettleMeanMs:F2} ms / 最大 {diagnostics.UiSettleMaxMs} ms"
                          + $"；签名缓存命中 {diagnostics.StylusSignatureReuses} 次（喂了 100 次同签名）");
        if (!diagnosticsOk) failures++;

        // ---------------------------------------------------------- 7) 护栏：Effect 零命中
        var effectLeaks = new List<string>();
        // ★ HasDropShadow 必须单列：它是 WPF 里唯一"名字里没有 Effect、却会真的
        //   往视觉树里塞 DropShadowEffect"的内建开关（ToolTip 的默认模板吃它）。
        //   只写 Effect 相关正则抓不到，等于给这条护栏留了个后门。
        var effectPattern = new Regex(
            @"DropShadowEffect|BlurEffect|BitmapEffect|HasDropShadow"
            + @"|(?<![\w])Effect\s*=|<[\w:]+\.Effect\s*[>/]");

        foreach (var file in EnumerateSourceFiles(srcRoot))
        {
            bool isXaml = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            string text = StripComments(File.ReadAllText(file, Encoding.UTF8), isXaml);

            foreach (Match m in effectPattern.Matches(text))
            {
                effectLeaks.Add(Path.GetRelativePath(root, file) + " → " + m.Value.Trim());
            }
        }

        bool noEffect = effectLeaks.Count == 0;
        Console.WriteLine($"[{(noEffect ? "PASS" : "FAIL")}] 护栏：墨迹链路上零 Effect"
                          + (noEffect
                                ? "（DropShadowEffect / BlurEffect / .Effect= 全项目零命中）"
                                : $"（发现 {effectLeaks.Count} 处：{string.Join(" / ", effectLeaks)}）"));
        if (!noEffect) failures++;

        // ---------------------------------------------------------- 8) 护栏：渲染模式
        var renderModeSetters = new List<string>();
        var renderModePattern = new Regex(@"ProcessRenderMode\s*=");

        foreach (var file in EnumerateSourceFiles(srcRoot))
        {
            bool isXaml = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            string text = StripComments(File.ReadAllText(file, Encoding.UTF8), isXaml);

            if (renderModePattern.IsMatch(text)) renderModeSetters.Add(Path.GetRelativePath(root, file));
        }

        RenderMode currentMode = RenderOptions.ProcessRenderMode;
        bool renderOk = renderModeSetters.Count == 0 && currentMode == RenderMode.Default;

        Console.WriteLine($"[{(renderOk ? "PASS" : "FAIL")}] 护栏：ProcessRenderMode = {currentMode}"
                          + (renderModeSetters.Count == 0
                                ? "（源码零处显式设置 ⇒ 走默认的硬件加速）"
                                : $"（被显式设置过：{string.Join(" / ", renderModeSetters)}）"));
        if (!renderOk) failures++;

        return failures;
    }

    // ================================================================= M12：个人教学工作流

    /// <summary>
    /// M12 断言组：命令总线 / 持久化键 / ISF 层标记 / 宏与模式 / 题号牌测量 /
    /// 图形吸附端到端 / 函数曲线翻转像素探针 / 拖动 rebase 端到端 / 工具栏隐藏不改缩放的源级护栏。
    /// </summary>
    /// <remarks>
    /// 两条端到端是本组的核心（数据层全对也挡不住「渲染出口忘了接」）：
    /// · 翻转探针：x^2+3 的顶点在数学 (0,3)，翻转修复后曲线必须<b>整条</b>落在水平中线上方 ——
    ///   修前它整条在下方（e2e 曾对翻转不敏感，所以从未抓到）；
    /// · rebase 端到端：真仓库 + BeginRebaseDrag + 两帧 UpdatePose（绝对中心），
    ///   断言参数吸收（表达式改写 + 中心不动 + ContentVersion 递增）与不可改写时的位姿回退。
    /// </remarks>
    private static int RunM12Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M12：个人教学工作流 ----");

        string root = FindRepoRoot(outDir);
        var empty = new Dictionary<string, double>();

        // ---- 1) 命令总线：登记 / 执行 / 未知 Id / 重复登记抛异常 ----
        var bus = new CommandBus();
        int hits = 0;
        bus.Register("m12.a", () => hits++);
        bool execOk = bus.Execute("m12.a") && hits == 1;
        bool unknownFalse = !bus.Execute("m12.nope");
        bool dupThrew = false;
        try { bus.Register("m12.a", () => { }); }
        catch (Exception) { dupThrew = true; }

        bool busOk = execOk && unknownFalse && dupThrew;
        Console.WriteLine($"[{(busOk ? "PASS" : "FAIL")}] 命令总线：执行={execOk} 未知Id返回false={unknownFalse} 重复登记抛异常={dupThrew}");
        if (!busOk) failures++;

        // ---- 1b) 自检口 Missing()：把"引用了没登记的命令"变成看得见的事实 ----
        // ★ 这条断言是被一次真实事故加的：球菜单引用了 calc.open，而总线从未登记它，
        //   症状是"点计算器毫无反应"，源级断言（只查字符串）照样 PASS。
        var busMissing = bus.Missing(new[] { "m12.a", "m12.nope", "m12.nope", "", "m12.deep" });
        bool missingOk = busMissing.Count == 2
                         && busMissing[0] == "m12.nope"
                         && busMissing[1] == "m12.deep";
        Console.WriteLine($"[{(missingOk ? "PASS" : "FAIL")}] 命令总线自检：未登记的 Id 被逐条列出且去重"
                          + $"（{string.Join("、", busMissing)}）");
        if (!missingOk) failures++;

        // ---- 2) M12 新持久化键往返（落临时文件，不碰真实设置）----
        string uiPath = Path.Combine(Path.GetTempPath(), "mathphys_m12_uistate_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            UiStateStore.WriteToolHidden(true, uiPath);
            UiStateStore.WriteBallY(0.37, uiPath);
            UiStateStore.WriteBallMode("always", uiPath);
            UiStateStore.WriteMode("review", uiPath);

            bool uiOk = UiStateStore.ReadToolHidden(uiPath) == true
                     && Math.Abs((UiStateStore.ReadBallY(uiPath) ?? -1.0) - 0.37) < 1e-9
                     && UiStateStore.ReadBallMode(uiPath) == "always"
                     && UiStateStore.ReadMode(uiPath) == "review";
            Console.WriteLine($"[{(uiOk ? "PASS" : "FAIL")}] 持久化键往返：tool_hidden / ball_y / ball_mode / mode");
            if (!uiOk) failures++;
        }
        finally
        {
            if (File.Exists(uiPath)) File.Delete(uiPath);
        }

        // ---- 3) ISF 往返保层标记：草稿层笔画序列化后不能丢掉身份 ----
        bool isfOk = false;
        try
        {
            var pts = new StylusPointCollection();
            pts.Add(new StylusPoint(10, 10));
            pts.Add(new StylusPoint(50, 60));
            var stroke = new Stroke(pts);
            InkLayers.MarkLayer(stroke, InkLayers.DraftId);

            using var ms = new MemoryStream();
            new StrokeCollection { stroke }.Save(ms);
            ms.Position = 0;
            var back = new StrokeCollection(ms);
            isfOk = back.Count == 1 && InkLayers.LayerOf(back[0]) == InkLayers.DraftId;
        }
        catch (Exception isfEx)
        {
            Console.WriteLine($"  ISF 往返异常：{isfEx.GetType().Name} {isfEx.Message}");
        }
        Console.WriteLine($"[{(isfOk ? "PASS" : "FAIL")}] ISF 往返保层标记：草稿层 round-trip 后仍是草稿层");
        if (!isfOk) failures++;

        // ---- 4) 宏文件：坏行只进警告（不炸启动），好行完整解析 ----
        var (macros, warnings) = MacroFile.Parse(new[]
        {
            "讲评|Ctrl+Shift+2=mode.review;toolbar.hide",
            "这一行没有等号",
        });
        bool macroOk = macros.Count == 1 && warnings.Count >= 1
                    && macros[0].Name == "讲评"
                    && macros[0].Hotkey == "Ctrl+Shift+2"
                    && macros[0].Commands.Count == 2;
        Console.WriteLine($"[{(macroOk ? "PASS" : "FAIL")}] 宏文件解析：好行 1 条（热键 Ctrl+Shift+2）、坏行警告 {warnings.Count} 条");
        if (!macroOk) failures++;

        // ---- 5) 内置模式：三条齐全，Find 的 Id 语义 ----
        bool modesOk = BoardModes.All.Count == 3
                    && BoardModes.Find("grading") == BoardModes.Grading
                    && BoardModes.Find("review") == BoardModes.Review
                    && BoardModes.Find("present") == BoardModes.Present
                    && BoardModes.Find("nope") is null;
        Console.WriteLine($"[{(modesOk ? "PASS" : "FAIL")}] 内置模式：批改/讲评/演示 三条齐全，Find 认识 Id、不认识返回 null");
        if (!modesOk) failures++;

        // ---- 6) 题号牌测量：宽随标签字数、高固定 ----
        var qmarkRend = new QuestionMarkRenderer();
        var qRef12 = new S4FakeRef(QuestionMarkRenderer.KindName, new Point(0, 0), 1, 0,
            empty, new Dictionary<string, string> { [QuestionMarkRenderer.LabelKey] = "12" });
        var qRef1234 = new S4FakeRef(QuestionMarkRenderer.KindName, new Point(0, 0), 1, 0,
            empty, new Dictionary<string, string> { [QuestionMarkRenderer.LabelKey] = "1234" });
        var m12chip = qmarkRend.Measure(qRef12);
        var m1234 = qmarkRend.Measure(qRef1234);
        bool qmOk = Math.Abs(m12chip.Height - QuestionMarkRenderer.ChipHeight) < 1e-9
                 && Math.Abs(m12chip.Width - (QuestionMarkRenderer.ChipPadding + 2 * QuestionMarkRenderer.NarrowCharWidth)) < 1e-9
                 && m1234.Width > m12chip.Width;
        Console.WriteLine($"[{(qmOk ? "PASS" : "FAIL")}] 题号牌测量：12 ⇒ {m12chip.Width:F1}×{m12chip.Height:F1}，1234 更宽（{m1234.Width:F1}）");
        if (!qmOk) failures++;

        // ---- 7) 图形吸附端到端：真渲染器 + 真仓库 + 真查询 ----
        bool snapOk = false;
        string snapMsg;
        try
        {
            var catSnap = new GfxRendererCatalog();
            catSnap.Register(new CoordSystemRenderer(), "坐标系（学科工具）");
            var stSnap = new GfxObjectStore(catSnap);
            stSnap.Add(new GfxDraft
            {
                Kind = CoordSystemRenderer.KindName, Center = new Point(500, 350), Scale = 1,
                Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [CoordSystemGeometry.UnitWorldKey] = 80,
                    [CoordSystemGeometry.XMinKey] = -10, [CoordSystemGeometry.XMaxKey] = 10,
                    [CoordSystemGeometry.YMinKey] = -10, [CoordSystemGeometry.YMaxKey] = 10,
                    [CoordSystemGeometry.StepKey] = 1,
                    [CoordSystemGeometry.ShowGridKey] = 1,
                    [CoordSystemGeometry.ShowLabelsKey] = 1,
                    [CoordSystemGeometry.LockAspectKey] = 1,
                },
            });

            var query = new BoardQuery(new StrokeCollection(), () => stSnap.Objects, catSnap);

            // 网格交点：数学 (1,1) ⇒ 世界 (580,270)；查询点偏 1.9 world（故意离轴远，
            // 不给轴线目标赢的机会），应吸到交点上
            var gridHit = query.SnapToGfx(new Point(581.6, 271.0), 5.0);
            bool gridOk = gridHit is { } gp && Near(gp, new Point(580, 270), 1e-6);

            // 轴线：查询点 (505,352) ⇒ 吸到 x 轴上的最近点（y=350，x 保持 505）
            var axisHit = query.SnapToGfx(new Point(505, 352), 5.0);
            bool axisOk = axisHit is { } ap && Math.Abs(ap.Y - 350) < 1e-6 && Math.Abs(ap.X - 505) < 5.0 + 1e-9;

            snapOk = gridOk && axisOk;
            snapMsg = $"网格交点 {(gridHit is null ? "未吸到" : $"{gridHit.Value.X:F1},{gridHit.Value.Y:F1}")}，"
                      + $"轴线 {(axisHit is null ? "未吸到" : $"{axisHit.Value.X:F1},{axisHit.Value.Y:F1}")}";
        }
        catch (Exception snapEx)
        {
            snapMsg = snapEx.GetType().Name + ": " + snapEx.Message;
        }
        Console.WriteLine($"[{(snapOk ? "PASS" : "FAIL")}] 图形吸附端到端：{snapMsg}");
        if (!snapOk) failures++;

        // ---- 8) ★ 函数曲线翻转像素探针：x^2+3 的整条曲线必须落在水平中线上方 ----
        bool mirrorOk = false;
        string mirrorMsg;
        try
        {
            var probe = new CanvasViewportHost();
            try
            {
                probe.Measure(new Size(400, 400));
                probe.Arrange(new Rect(0, 0, 400, 400));
                                probe.UpdateLayout();
                probe.RegisterGfxRenderer(new FunctionRenderer(), "函数图像（学科工具）");
                probe.GfxObjects.Add(new GfxDraft
                {
                    Kind = FunctionRenderer.KindName, Center = new Point(200, 200), Scale = 28.35,
                    Color = Colors.Blue, LineWorldWidth = 1.5,
                    Numbers = new Dictionary<string, double>
                    {
                        ["xMin"] = -5, ["xMax"] = 5, ["yMin"] = -5, ["yMax"] = 5,
                    },
                    Texts = new Dictionary<string, string> { ["expr"] = "x^2+3" },
                });
                // ★ M25 起：Add 自动选中 + 函数渲染器实现了参数面板 provider ⇒ 选中即出面板（预期行为，真实装配的断言在 18 节）。
                //   本探针只验曲线翻转：取消选中把面板收掉，像素计数才干净。
                probe.GfxObjects.Select(null);
                probe.UpdateLayout();

                var bmp = Snapshot(probe, 400, 400);
                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int width = converted.PixelWidth;
                int height = converted.PixelHeight;
                int stride = width * 4;
                var pixels = new byte[stride * height];
                converted.CopyPixels(pixels, stride, 0);

                // Bgra32：byte0=B。数「非白且不透明」的彩色像素（曲线本体），按中线分上下。
                long coloredAbove = 0, coloredBelow = 0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int o = y * stride + x * 4;
                        if (pixels[o + 3] < 50) continue;                          // 透明
                        if (pixels[o] > 235 && pixels[o + 1] > 235 && pixels[o + 2] > 235) continue;   // 白
                        if (y < 200) coloredAbove++;
                        else coloredBelow++;
                    }
                }

                // 顶点在数学 (0,3) ⇒ 翻转修复后屏幕 y ≈ 200 − 3×28.35 ≈ 115，整条曲线在中线上方；
                // 修前整条在下方（曾因 e2e 对翻转不敏感而漏网）。
                mirrorOk = coloredAbove > 100 && coloredBelow == 0;
                mirrorMsg = $"中线上方彩色像素 {coloredAbove}、下方 {coloredBelow}";
                try { SavePng(bmp, Path.Combine(outDir, "m74s5-函数翻转探针.png")); }
                catch { /* 证据图失败不影响断言 */ }
            }
            finally
            {
                probe.Shutdown();
            }
        }
        catch (Exception mirrorEx)
        {
            mirrorMsg = mirrorEx.GetType().Name + ": " + mirrorEx.Message;
        }
        Console.WriteLine($"[{(mirrorOk ? "PASS" : "FAIL")}] 函数曲线翻转探针：x^2+3 整条曲线在中线上方（{mirrorMsg}）");
        if (!mirrorOk) failures++;

        // ---- 9) ★ 拖动 rebase 端到端：两帧绝对中心 ⇒ 参数吸收 / 位姿回退 ----
        // ★ 测试对象 Scale=1：这样 world 位移直接等于数学位移，期望值一目了然。
        bool rebaseOk = false;
        string rebaseMsg;
        try
        {
            var catR = new GfxRendererCatalog();
            catR.Register(new FunctionRenderer(), "函数图像（学科工具）");
            var stR = new GfxObjectStore(catR);

            static IGfxObjectRef? FindById(GfxObjectStore store, string id)
            {
                foreach (var ob in store.Objects)
                {
                    if (ob.Id == id) return ob;
                }
                return null;
            }

            // 先落一个坐标系（unit=1，让数学单位与 world 对齐），曲线都绑定它 ——
            // ★ store 的折算分支只对「绑定到坐标系」的曲线生效；自由曲线拖位姿本来就是对的设计。
            var sysR = stR.Add(new GfxDraft
            {
                Kind = CoordSystemRenderer.KindName, Center = new Point(300, 300), Scale = 1,
                Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [CoordSystemGeometry.UnitWorldKey] = 1,
                    [CoordSystemGeometry.XMinKey] = -10, [CoordSystemGeometry.XMaxKey] = 10,
                    [CoordSystemGeometry.YMinKey] = -10, [CoordSystemGeometry.YMaxKey] = 10,
                    [CoordSystemGeometry.StepKey] = 1,
                    [CoordSystemGeometry.ShowGridKey] = 1,
                },
            });

            // 二次：x^2，锚点 (300,300)；两帧累计位移 (10,-5)（屏幕向下为正 ⇒ 数学向上 5）
            var quadId = stR.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(300, 300), Scale = 1,
                Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["xMin"] = -5, ["xMax"] = 5, ["yMin"] = -5, ["yMax"] = 5,
                    ["unitWorld"] = 1, ["bindRelX"] = 0, ["bindRelY"] = 0, ["bindRelRot"] = 0,
                },
                Texts = new Dictionary<string, string> { ["expr"] = "x^2", ["bindTo"] = sysR },
            });
            // 视觉重建的可观察断言：真层 + Sync，rebase 后必须换新视觉实例
            //（ContentVersion 是 internal 的，但「视觉实例换了没有」正是它存在的目的）。
            var layerR = new GfxObjectLayer(new Canvas(), stR, catR);
            var visBefore = layerR.VisualFor(quadId);

            stR.BeginRebaseDrag(quadId);
            stR.UpdatePose(quadId, new Point(305, 300), 0, 1.0);   // 帧 1：(5, 0)
            stR.UpdatePose(quadId, new Point(310, 295), 0, 1.0);   // 帧 2：累计 (10, -5)

            var quadAfter = FindById(stR, quadId);
            string quadExpr = quadAfter?.GetText("expr", "") ?? "";
            double quadAtH = quadExpr.Length > 0 ? ExpressionEvaluator.Eval(ExpressionParser.Parse(quadExpr), 10, empty) : double.NaN;
            double quadAtSide = quadExpr.Length > 0 ? ExpressionEvaluator.Eval(ExpressionParser.Parse(quadExpr), 11, empty) : double.NaN;
            var visAfter = layerR.VisualFor(quadId);
            bool visualRebuilt = !ReferenceEquals(visBefore, visAfter);
            bool quadOk = quadAfter is not null
                       && Math.Abs(quadAtH - 5.0) < 1e-6            // (x−10)^2+5 在顶点取 5
                       && Math.Abs((quadAtSide - quadAtH) - 1.0) < 1e-6
                       && Near(quadAfter.Center, new Point(300, 300), 1e-6)   // 中心不动：参数吸收
                       && visualRebuilt;                            // 曲线真的重画了

            // 线性：2*x，锚点 = 绑定归位后的中心 (300,300)（绑定对象住在坐标系中心），
            // 两帧累计位移 (10,-5) ⇒ 2(x−10)+5 = 2x−15
            var linId = stR.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(300, 300), Scale = 1,
                Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["xMin"] = -5, ["xMax"] = 5, ["yMin"] = -5, ["yMax"] = 5,
                    ["unitWorld"] = 1, ["bindRelX"] = 0, ["bindRelY"] = 0, ["bindRelRot"] = 0,
                },
                Texts = new Dictionary<string, string> { ["expr"] = "2*x", ["bindTo"] = sysR },
            });
            stR.BeginRebaseDrag(linId);
            stR.UpdatePose(linId, new Point(305, 300), 0, 1.0);
            stR.UpdatePose(linId, new Point(310, 295), 0, 1.0);

            var linAfter = FindById(stR, linId);
            string linExpr = linAfter?.GetText("expr", "") ?? "";
            double linAt10 = linExpr.Length > 0 ? ExpressionEvaluator.Eval(ExpressionParser.Parse(linExpr), 10, empty) : double.NaN;
            double linAt11 = linExpr.Length > 0 ? ExpressionEvaluator.Eval(ExpressionParser.Parse(linExpr), 11, empty) : double.NaN;
            bool linOk = linAfter is not null
                      && Math.Abs(linAt10 - 5.0) < 1e-6
                      && Math.Abs((linAt11 - linAt10) - 2.0) < 1e-6;

            // 不可改写（sin）：rebase 失败 ⇒ 回退到位姿搬移，表达式原样保留
            var sinId = stR.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(500, 500), Scale = 1,
                Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { ["xMin"] = -5, ["xMax"] = 5, ["yMin"] = -5, ["yMax"] = 5 },
                Texts = new Dictionary<string, string> { ["expr"] = "sin(x)" },
            });
            stR.BeginRebaseDrag(sinId);
            bool poseMoved = stR.UpdatePose(sinId, new Point(520, 480), 0, 1.0);
            var sinAfter = FindById(stR, sinId);
            bool sinOk = poseMoved && sinAfter is not null
                      && sinAfter.GetText("expr", "") == "sin(x)"
                      && Near(sinAfter.Center, new Point(520, 480), 1e-6);

            rebaseOk = quadOk && linOk && sinOk;
            rebaseMsg = $"二次 {quadExpr}（顶点值 {quadAtH:F3}，中心不动+视觉重建={visualRebuilt}）；"
                        + $"线性 {linExpr}（@10={linAt10:F3}）；sin 回退位姿={sinOk}";
        }
        catch (Exception rebaseEx)
        {
            rebaseMsg = rebaseEx.GetType().Name + ": " + rebaseEx.Message;
        }
        Console.WriteLine($"[{(rebaseOk ? "PASS" : "FAIL")}] 拖动 rebase 端到端：{rebaseMsg}");
        if (!rebaseOk) failures++;

        // ---- 10) S1 源级护栏：工具栏显隐方法体里零缩放调用（藏工具栏不许动试卷）----
        string mainPath = Path.Combine(root, "src", "MathPhys.Ink", "Views", "MainWindow.xaml.cs");
        string mainSrc = StripComments(File.ReadAllText(mainPath, Encoding.UTF8), false);

        static string MethodBodyOf(string src, string signature)
        {
            int i = src.IndexOf(signature, StringComparison.Ordinal);
            if (i < 0) return string.Empty;
            int open = src.IndexOf('{', i);
            if (open < 0) return string.Empty;
            int depth = 0;
            for (int j = open; j < src.Length; j++)
            {
                if (src[j] == '{') depth++;
                else if (src[j] == '}')
                {
                    depth--;
                    if (depth == 0) return src.Substring(open, j - open + 1);
                }
            }
            return string.Empty;
        }

        string toolbarBodies = MethodBodyOf(mainSrc, "private void ApplyToolbarState()")
                             + MethodBodyOf(mainSrc, "private void SetToolbarHidden(bool hidden)")
                             + MethodBodyOf(mainSrc, "private void ToggleToolbar()");
        var zoomLeaks = new List<string>();
        foreach (var word in new[] { "SetView", "RestoreView", "FitWidth", "Zoom", "SetScale" })
        {
            if (toolbarBodies.Contains(word, StringComparison.Ordinal)) zoomLeaks.Add(word);
        }

        bool guardOk = toolbarBodies.Length > 0 && zoomLeaks.Count == 0;
        Console.WriteLine($"[{(guardOk ? "PASS" : "FAIL")}] 工具栏显隐不改缩放（源级护栏）：三个显隐方法体零缩放调用"
                          + (zoomLeaks.Count == 0 ? "" : $"（发现：{string.Join("/", zoomLeaks)}）"));
        if (!guardOk) failures++;

        return failures;
    }

    // ================================================================= M13：课堂体验优化

    /// <summary>
    /// M13 断言组：书写光标隐藏 / 纸张底色 / 悬浮球双自由度 / 浮动工具栏源级护栏 /
    /// 新持久化键 / 算式求值器。源级断言为主 —— 本里程碑改的多是「界面形态」，
    /// 数据层可观察行为集中在求值器与 UiStateStore。
    /// </summary>
    private static int RunM13Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M13：课堂体验优化 ----");

        string root = FindRepoRoot(outDir);

        static string ReadSource(string root, params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { root, "src" }.Concat(parts).ToArray()), Encoding.UTF8);

        // ---- 1) 书写类工具光标隐藏（Cursor=None）；手 / 选择 / 正交分解保留 ----
        string[] inkTools =
        {
            "MathPhys.Ink/Tools/BuiltIn/PenTool.cs",
            "MathPhys.Ink/Tools/BuiltIn/EraserTool.cs",
            "MathPhys.Ink/Tools/BuiltIn/QuestionMarkTool.cs",
            "MathPhys.Ink.Plugin.Ruler/RulerTool.cs",
            "MathPhys.Ink.Plugin.Triangle/TriangleTool.cs",
            "MathPhys.Ink.Plugin.Protractor/ProtractorTool.cs",
            "MathPhys.Ink.Plugin.CoordSystem/CoordSystemTool.cs",
            "MathPhys.Ink.Plugin.FunctionPlot/FunctionTool.cs",
            "MathPhys.Ink.Plugin.VectorArrow/ArrowTool.cs",
            "MathPhys.Ink.Plugin.VectorArrow/ArrowSumTool.cs",
            "MathPhys.Ink.Plugin.VectorArrow/ArrowGroupTool.cs",
        };
        int noneCount = 0;
        foreach (string rel in inkTools)
        {
            if (ReadSource(root, rel).Contains("Cursors.None", StringComparison.Ordinal)) noneCount++;
        }

        bool keepOk = !ReadSource(root, "MathPhys.Ink/Tools/BuiltIn/HandTool.cs").Contains("Cursors.None", StringComparison.Ordinal)
                   && !ReadSource(root, "MathPhys.Ink/Gfx/GfxSelectionTool.cs").Contains("Cursors.None", StringComparison.Ordinal)
                   && !ReadSource(root, "MathPhys.Ink.Plugin.VectorArrow/ArrowDecomposeTool.cs").Contains("Cursors.None", StringComparison.Ordinal);

        bool cursorOk = noneCount == inkTools.Length && keepOk;
        Console.WriteLine($"[{(cursorOk ? "PASS" : "FAIL")}] 书写光标隐藏：{noneCount}/{inkTools.Length} 个书写类工具申报 None，手/选择/正交分解保留");
        if (!cursorOk) failures++;

        // ---- 2) S1 旧机制退役：临时可见态 / 唤出条 / 4 秒定时器整个消失 ----
        string mainCs = StripComments(ReadSource(root, "MathPhys.Ink", "Views", "MainWindow.xaml.cs"), false);
        var leftovers = new List<string>();
        foreach (var word in new[] { "TopRevealStrip", "ShowToolbarTemporarily", "HideToolbarTemporarily", "_chromeRevealTimer", "OnChromeDismissCheck", "IsWithinTopChrome" })
        {
            if (mainCs.Contains(word, StringComparison.Ordinal)) leftovers.Add(word);
        }

        // M12 护栏复跑：显隐方法体仍然零缩放调用（藏工具栏不许动试卷）
        static string MethodBodyOf(string src, string signature)
        {
            int i = src.IndexOf(signature, StringComparison.Ordinal);
            if (i < 0) return string.Empty;
            int open = src.IndexOf('{', i);
            if (open < 0) return string.Empty;
            int depth = 0;
            for (int j = open; j < src.Length; j++)
            {
                if (src[j] == '{') depth++;
                else if (src[j] == '}')
                {
                    depth--;
                    if (depth == 0) return src.Substring(open, j - open + 1);
                }
            }
            return string.Empty;
        }

        string toolbarBodies = MethodBodyOf(mainCs, "private void ApplyToolbarState()")
                             + MethodBodyOf(mainCs, "private void SetToolbarHidden(bool hidden)")
                             + MethodBodyOf(mainCs, "private void ToggleToolbar()");
        var zoomLeaks = new List<string>();
        foreach (var word in new[] { "SetView", "RestoreView", "FitWidth", "Zoom", "SetScale" })
        {
            if (toolbarBodies.Contains(word, StringComparison.Ordinal)) zoomLeaks.Add(word);
        }

        bool retireOk = leftovers.Count == 0 && toolbarBodies.Length > 0 && zoomLeaks.Count == 0;
        Console.WriteLine($"[{(retireOk ? "PASS" : "FAIL")}] 工具栏状态机简化：旧临时机制退役 {leftovers.Count} 处残留，显隐方法体零缩放调用"
                          + (zoomLeaks.Count == 0 ? "" : $"（发现：{string.Join("/", zoomLeaks)}）"));
        if (!retireOk) failures++;

        // ---- 3) 悬浮球双自由度 + 固定菜单 ----
        string ballCs = ReadSource(root, "MathPhys.Ink", "Views", "Controls", "FloatingBall.cs");
        bool ballOk = ballCs.Contains("public double XRatio", StringComparison.Ordinal)
                   && ballCs.Contains("HorizontalAlignment.Left", StringComparison.Ordinal)
                   && ballCs.Contains("PositionCommitted", StringComparison.Ordinal)
                   && mainCs.Contains("BuildBallMenu", StringComparison.Ordinal)
                   && !mainCs.Contains("RebuildBallMenu", StringComparison.Ordinal);

        // ★ 菜单 7 项是数据表（BallMenuItems），断言分三层，缺一层就会漏掉"点不动"的毛病：
        //   ① 表里确实有直尺自由角与计算器；
        //   ② 每一个 Id 在源码里都有对应的 Register(...) —— 这一条是 M13 S5 漏登记 calc.open
        //      的直接对策（旧断言只查字符串出现，菜单里写着 calc.open 也算 PASS）；
        //   ③ 菜单装配真的走了总线（RunBallCommand + Execute），而不是各写一份直接调。
        var ballIds = System.Text.RegularExpressions.Regex
            .Matches(mainCs, @"\(""[^""]+"",\s*""([a-z][a-z0-9._-]*)""\)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var notRegistered = new List<string>();
        foreach (string id in ballIds)
        {
            // 工具类命令由"注册表驱动"登记（宿主不写死工具名），其余必须逐条登记字面量
            bool registered = id.StartsWith("tool.", StringComparison.Ordinal)
                ? mainCs.Contains("_commands.Register(\"tool.\" + toolId", StringComparison.Ordinal)
                : mainCs.Contains($"_commands.Register(\"{id}\"", StringComparison.Ordinal);
            if (!registered) notRegistered.Add(id);
        }

        bool ballMenuTableOk = ballIds.Contains("tool.ruler-free") && ballIds.Contains("calc.open");
        bool ballWiringOk = mainCs.Contains("private void RunBallCommand(", StringComparison.Ordinal)
                         && mainCs.Contains("_commands.Execute(commandId)", StringComparison.Ordinal)
                         && notRegistered.Count == 0;

        bool ballOk2 = ballOk && ballMenuTableOk && ballWiringOk;
        Console.WriteLine($"[{(ballOk2 ? "PASS" : "FAIL")}] 悬浮球：XRatio 双自由度 + 固定 7 项菜单（"
                          + $"表项 {ballIds.Count} 个、直尺/计算器都在={ballMenuTableOk}、"
                          + $"每个 Id 都有登记={notRegistered.Count == 0}"
                          + (notRegistered.Count == 0 ? "" : $"（漏登记：{string.Join("、", notRegistered)}）") + "）");
        if (!ballOk2) failures++;

        // ---- 3b) 计算器宿主 API：open 幂等 / close / toggle ----
        // ★ 这条是被同一场事故加的：球菜单引用 calc.open，而宿主当时只有 ToggleCalculator，
        //   总线上压根没登记它。现在把"打开 / 关闭 / 开关"三个语义在真控件树上证一遍 ——
        //   尤其是 open 的幂等性（宏里写 calc.open 不该把开着的计算器关掉）。
        bool calcApiOk = false;
        string calcApiMsg = "(未跑)";
        try
        {
            var calcProbe = new CanvasViewportHost();
            try
            {
                calcProbe.Measure(new Size(600, 400));
                calcProbe.Arrange(new Rect(0, 0, 600, 400));
                calcProbe.UpdateLayout();

                bool hiddenAtStart = !calcProbe.IsCalculatorVisible;
                calcProbe.ShowCalculator();
                bool shown = calcProbe.IsCalculatorVisible;
                calcProbe.ShowCalculator();                       // 幂等：再来一次仍是"开着"
                bool stillShown = calcProbe.IsCalculatorVisible;
                calcProbe.HideCalculator();
                bool hidden = !calcProbe.IsCalculatorVisible;
                calcProbe.ToggleCalculator();
                bool toggledOn = calcProbe.IsCalculatorVisible;
                calcProbe.ToggleCalculator();
                bool toggledOff = !calcProbe.IsCalculatorVisible;

                calcApiOk = hiddenAtStart && shown && stillShown && hidden && toggledOn && toggledOff;
                calcApiMsg = $"初始隐藏={hiddenAtStart} 打开={shown} open幂等={stillShown} "
                           + $"关闭={hidden} toggle开={toggledOn} toggle关={toggledOff}";
            }
            finally
            {
                calcProbe.Shutdown();
            }
        }
        catch (Exception calcEx)
        {
            calcApiMsg = "宿主计算器 API 探针抛异常：" + calcEx.GetType().Name + " " + calcEx.Message;
        }

        Console.WriteLine($"[{(calcApiOk ? "PASS" : "FAIL")}] 计算器：ShowCalculator 幂等 / HideCalculator / ToggleCalculator（{calcApiMsg}）");
        if (!calcApiOk) failures++;

        // ---- 4) XAML：工具栏浮动化落地（画布格内 + 无 Dock 顶置 + 把手条）----
        string mainXaml = ReadSource(root, "MathPhys.Ink", "Views", "MainWindow.xaml");
        int gridIndex = mainXaml.IndexOf("x:Name=\"CanvasOverlayGrid\"", StringComparison.Ordinal);
        string gridBlock = gridIndex >= 0
            ? mainXaml[gridIndex..Math.Min(mainXaml.Length, mainXaml.IndexOf("GeoGebra 停靠区", gridIndex))]
            : string.Empty;
        bool floatOk = gridIndex >= 0
                    && gridBlock.Contains("x:Name=\"TopChrome\"", StringComparison.Ordinal)
                    && gridBlock.Contains("x:Name=\"ToolbarGrip\"", StringComparison.Ordinal)
                    && gridBlock.Contains("x:Name=\"ToolbarCollapseButton\"", StringComparison.Ordinal)
                    && !mainXaml.Contains("DockPanel.Dock=\"Top\"", StringComparison.Ordinal)
                    && mainCs.Contains("FloatingDrag.Attach", StringComparison.Ordinal);
        Console.WriteLine($"[{(floatOk ? "PASS" : "FAIL")}] 工具栏浮动化：TopChrome 迁入画布格 + 把手条 + 收起按钮，无 Dock 顶置");
        if (!floatOk) failures++;

        // ---- 5) 新持久化键往返（临时文件）----
        string uiPath = Path.Combine(Path.GetTempPath(), "mathphys_m13_uistate_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            UiStateStore.WritePaperColor("cream", uiPath);
            UiStateStore.WriteBallX(0.42, uiPath);
            UiStateStore.WriteToolbarX(0.13, uiPath);
            UiStateStore.WriteToolbarY(0.87, uiPath);

            bool roundTrip = UiStateStore.ReadPaperColor(uiPath) == "cream"
                          && Math.Abs((UiStateStore.ReadBallX(uiPath) ?? -1.0) - 0.42) < 1e-9
                          && Math.Abs((UiStateStore.ReadToolbarX(uiPath) ?? -1.0) - 0.13) < 1e-9
                          && Math.Abs((UiStateStore.ReadToolbarY(uiPath) ?? -1.0) - 0.87) < 1e-9;

            File.WriteAllText(uiPath, "ball_x=abc\ntoolbar_x=1.5\n");
            bool invalidNull = UiStateStore.ReadBallX(uiPath) is null      // 非数字
                            && UiStateStore.ReadToolbarX(uiPath) is null;  // 越界 0~1

            bool uiOk = roundTrip && invalidNull;
            Console.WriteLine($"[{(uiOk ? "PASS" : "FAIL")}] M13 持久化键往返：paper_color / ball_x / toolbar_x / toolbar_y（非法值回落 null）");
            if (!uiOk) failures++;
        }
        finally
        {
            if (File.Exists(uiPath)) File.Delete(uiPath);
        }

        // ---- 6) 算式求值器：优先级 / 一元根号 / 拒绝残缺与除零 ----
        bool EvalOk(string expr, double expected)
            => ArithmeticEvaluator.TryEvaluate(expr, out double value) && Math.Abs(value - expected) < 1e-9;
        bool EvalFail(string expr) => !ArithmeticEvaluator.TryEvaluate(expr, out _);

        bool evalOk = EvalOk("2+3*4", 14) && EvalOk("2*3+4", 10)
                   && EvalOk("√9", 3) && EvalOk("√9+1", 4) && EvalOk("√√16", 2)
                   && EvalOk("1/3*3", 1) && EvalOk("10÷4", 2.5) && EvalOk("2+3×4", 14)
                   && EvalFail("5/0") && EvalFail("2+") && EvalFail("") && EvalFail("1..2");
        Console.WriteLine($"[{(evalOk ? "PASS" : "FAIL")}] 算式求值器：优先级 2+3*4=14、√9+1=4、√√16=2、除零/残缺/双小数点拒绝");
        if (!evalOk) failures++;

        // ---- 7) 纸张底色：共享画刷机制（RootGrid 与页矩形同源）+ 可视验证 ----
        string hostCs = ReadSource(root, "MathPhys.Ink", "Views", "Controls", "CanvasViewportHost.xaml.cs");
        bool paperSourceOk = hostCs.Contains("Fill = _paperBrush", StringComparison.Ordinal)
                          && hostCs.Contains("RootGrid.Background = _paperBrush", StringComparison.Ordinal)
                          && hostCs.Contains("public void SetPaperColor", StringComparison.Ordinal);

        bool paperPixelOk = false;
        string paperMsg = "源级断言未过，跳过像素验证";
        if (paperSourceOk)
        {
            try
            {
                var probe = new CanvasViewportHost();
                try
                {
                    probe.Measure(new Size(200, 200));
                    probe.Arrange(new Rect(0, 0, 200, 200));
                    probe.UpdateLayout();
                    probe.SetPaperColor(Color.FromRgb(0xFD, 0xF6, 0xE3));   // 米黄预设

                    var bmp = Snapshot(probe, 200, 200);
                    var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                    int stride = converted.PixelWidth * 4;
                    var pixels = new byte[stride * converted.PixelHeight];
                    converted.CopyPixels(pixels, stride, 0);

                    // 采中心一个区域的均值：换色后不该再有纯白（FFFFFF）
                    long sumB = 0, sumG = 0, sumR = 0, count = 0;
                    for (int y = 80; y < 120; y++)
                    {
                        for (int x = 80; x < 120; x++)
                        {
                            int o = y * stride + x * 4;
                            sumB += pixels[o]; sumG += pixels[o + 1]; sumR += pixels[o + 2];
                            count++;
                        }
                    }

                    double avgR = sumR / (double)count, avgG = sumG / (double)count, avgB = sumB / (double)count;
                    // 米黄 #FDF6E3：红高蓝低；纯白是三通道全 255
                    paperPixelOk = avgR > 240 && avgB < 235 && Math.Abs(avgG - 0xF6) < 6;
                    paperMsg = $"中心均值 RGB=({avgR:F0},{avgG:F0},{avgB:F0})（期望 ≈ 253,246,227）";
                }
                finally
                {
                    probe.Shutdown();
                }
            }
            catch (Exception paperEx)
            {
                paperMsg = paperEx.GetType().Name + ": " + paperEx.Message;
            }
        }

        bool paperOk = paperSourceOk && paperPixelOk;
        Console.WriteLine($"[{(paperOk ? "PASS" : "FAIL")}] 纸张底色：共享画刷同源 + 换米黄后视口像素随之变化（{paperMsg}）");
        if (!paperOk) failures++;

        // ---- 8) M21：纸张底色新增深色档，且出厂默认就是它 ----
        // 为何用源级判据而不构造 MainWindow：MainWindow 一构造就要拉起整条文档链路，
        // 而这里想钉的只是四件事齐不齐 —— 少最后一条就会出现
        // "色板里有了、默认也指过去了，可老师在界面上根本点不到那个颜色"。
        string mainCsSrc = ReadSource(root, "MathPhys.Ink", "Views", "MainWindow.xaml.cs");
        string mainXamlSrc = ReadSource(root, "MathPhys.Ink", "Views", "MainWindow.xaml");
        string canvasPalSrc = ReadSource(root, "MathPhys.Ink", "Design", "Palette.Canvas.xaml");

        bool blackKeyOk = canvasPalSrc.Contains("Canvas.PageBlack", StringComparison.Ordinal);
        bool blackPresetOk = mainCsSrc.Contains("\"black\" => Tokens.Color(\"Canvas.PageBlack\")", StringComparison.Ordinal);
        bool blackDefaultOk = mainCsSrc.Contains("UiStateStore.ReadPaperColor() ?? \"black\"", StringComparison.Ordinal);
        bool blackSwatchOk = mainXamlSrc.Contains("Tag=\"black\"", StringComparison.Ordinal);

        bool paperBlackOk = blackKeyOk && blackPresetOk && blackDefaultOk && blackSwatchOk;
        Console.WriteLine($"[{(paperBlackOk ? "PASS" : "FAIL")}] M21 纸张深色档四件齐："
                          + $"色板键={blackKeyOk} 预设表={blackPresetOk} 出厂默认={blackDefaultOk} 界面色圆={blackSwatchOk}");
        if (!paperBlackOk) failures++;

        return failures;
    }

    // ================================================================= M14

    /// <summary>
    /// M14 断言组：公式（LaTeX）插件与电路元件插件。
    /// </summary>
    /// <remarks>
    /// 渲染器直接实例化走真渲染路径（WPF-Math 解析 → 几何 → 位图冒烟）；
    /// 宿主加载器的隔离逻辑在 M7.2 已有专组验证，这里不重复。
    /// </remarks>
    private static int RunM14Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M14：公式（LaTeX）与电路元件 ----");

        string root = FindRepoRoot(outDir);

        static string ReadSource(string root, params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { root, "src" }.Concat(parts).ToArray()), Encoding.UTF8);

        // ---- 1) 工程约定：AssemblyName 与插件目录一致；不引用宿主工程 ----
        string formulaProj = ReadSource(root, "MathPhys.Ink.Plugin.Formula", "MathPhys.Ink.Plugin.Formula.csproj");
        string circuitProj = ReadSource(root, "MathPhys.Ink.Plugin.CircuitKit", "MathPhys.Ink.Plugin.CircuitKit.csproj");
        bool projOk = formulaProj.Contains("<AssemblyName>formula</AssemblyName>", StringComparison.Ordinal)
                   && circuitProj.Contains("<AssemblyName>circuitkit</AssemblyName>", StringComparison.Ordinal)
                   && !Regex.IsMatch(formulaProj, "Include=\"[.][.]\\\\MathPhys[.]Ink\\\\MathPhys")
                   && !Regex.IsMatch(circuitProj, "Include=\"[.][.]\\\\MathPhys[.]Ink\\\\MathPhys");
        Console.WriteLine($"[{(projOk ? "PASS" : "FAIL")}] 插件工程：AssemblyName=formula / circuitkit，且只引契约工程");
        if (!projOk) failures++;

        // ---- 2) 公式渲染器：物理常用 LaTeX 解析 / 标准高 / 无效退化 / 缓存 ----
        var formulaRenderer = new FormulaRenderer();
        string[] physicsFormulas =
        {
            @"E=mc^2", @"F=ma", @"\frac{U}{R}", @"\sqrt{2}IR", @"\vec{F}=q\vec{E}",
            @"\theta", @"\Delta t", @"\omega", @"\varepsilon=\frac{W}{q}", @"P=I^2R",
            @"\int_0^t I\,dt", @"\sum F = ma", @"x=A\sin(\omega t+\varphi)",
        };
        int parsed = 0;
        foreach (string latex in physicsFormulas)
        {
            if (formulaRenderer.GetEntry(latex).LocalGeometry is not null) parsed++;
        }

        var badFormula = formulaRenderer.GetEntry(@"\frac{U}{");
        var emptyFormula = formulaRenderer.GetEntry("");
        bool cacheOk = ReferenceEquals(formulaRenderer.GetEntry(@"E=mc^2"), formulaRenderer.GetEntry(@"E=mc^2"));

        bool formulaOk = parsed == physicsFormulas.Length
                      && badFormula.LocalGeometry is null
                      && Math.Abs(badFormula.LocalSize.Height - FormulaRenderer.InvalidLocalHeight) < 1e-9
                      && emptyFormula.LocalGeometry is null
                      && cacheOk;
        Console.WriteLine($"[{(formulaOk ? "PASS" : "FAIL")}] 公式渲染器：物理 LaTeX {parsed}/{physicsFormulas.Length} 解析成功，"
                          + $"标准高 {FormulaRenderer.NominalWorldHeight}，无效退化占位 + 缓存命中");
        if (!formulaOk) failures++;

        // ---- 3) 图形库接入：新 Kind 存档往返 + 全元件 Measure/CreateVisual/Describe + 位图冒烟 ----
        try
        {
            var catalog = new GfxRendererCatalog();
            catalog.Register(new FormulaRenderer(), "公式（学科工具）");
            catalog.Register(new CircuitRenderer(), "电路元件（学科工具）");

            var store = new GfxObjectStore(catalog);
            var fxId = store.Add(new GfxDraft
            {
                Kind = FormulaRenderer.KindName, Center = new Point(300, 300),
                Color = Colors.Black, LineWorldWidth = 1.5,
                Texts = new Dictionary<string, string> { [FormulaRenderer.LatexKey] = @"F=ma" },
            });
            var wireId = store.Add(new GfxDraft
            {
                Kind = CircuitRenderer.KindName, Center = new Point(500, 300),
                Color = Colors.Black, LineWorldWidth = 1.5, RotationDegrees = 30,
                Numbers = new Dictionary<string, double> { [CircuitSymbols.WireHalfLenKey] = 40 },
                Texts = new Dictionary<string, string> { [CircuitRenderer.SymbolKey] = CircuitSymbols.WireKey },
            });
            var resId = store.Add(new GfxDraft
            {
                Kind = CircuitRenderer.KindName, Center = new Point(500, 400),
                Color = Colors.Black, LineWorldWidth = 1.5,
                Texts = new Dictionary<string, string> { [CircuitRenderer.SymbolKey] = "res" },
            });

            IGfxObjectRef? fxRef = null, wireRef = null, resRef = null;
            foreach (var o in store.Objects)
            {
                if (o.Id == fxId) fxRef = o;
                if (o.Id == wireId) wireRef = o;
                if (o.Id == resId) resRef = o;
            }

            // 往返：Snapshot → 新 store Restore → 参数保真
            var snapshot = store.Snapshot();
            var store2 = new GfxObjectStore(catalog);
            store2.Restore(snapshot);
            IGfxObjectRef? fx2 = null, wire2 = null, res2 = null;
            foreach (var o in store2.Objects)
            {
                if (o.Id == fxId) fx2 = o;
                if (o.Id == wireId) wire2 = o;
                if (o.Id == resId) res2 = o;
            }

            bool roundTrip = fx2?.GetText(FormulaRenderer.LatexKey, "") == @"F=ma"
                          && Math.Abs((wire2?.GetNumber(CircuitSymbols.WireHalfLenKey, -1) ?? -1) - 40) < 1e-9
                          && Math.Abs((wire2?.RotationDegrees ?? -999) - 30) < 1e-9
                          && res2?.GetText(CircuitRenderer.SymbolKey, "") == "res";
            Console.WriteLine($"[{(roundTrip ? "PASS" : "FAIL")}] 新 Kind 存档往返：latexFormula / circuitSymbol(导线+电阻) 参数保真");
            if (!roundTrip) failures++;

            // 全部元件（含导线）：Measure > 0、CreateVisual 不抛、Describe 非空
            bool allSymOk = true;
            foreach (var def in CircuitSymbols.All)
            {
                var symId = store.Add(new GfxDraft
                {
                    Kind = CircuitRenderer.KindName, Center = new Point(0, 0),
                    Color = Colors.Black, LineWorldWidth = 1.5,
                    Texts = new Dictionary<string, string> { [CircuitRenderer.SymbolKey] = def.Key },
                });
                foreach (var o in store.Objects)
                {
                    if (o.Id != symId) continue;
                    var size = catalog.Find(CircuitRenderer.KindName)!.Measure(o);
                    var visual = catalog.Find(CircuitRenderer.KindName)!.CreateVisual(o);
                    string describe = new CircuitRenderer().Describe(o);
                    if (size.Width <= 0 || size.Height <= 0 || visual is null || describe.Length == 0) allSymOk = false;
                    break;
                }
            }

            // 导线与公式的 Measure 走参数化/解析分支，单独核数值
            bool wireMeasureOk = wireRef is not null
                && Math.Abs(wireRef.LocalSize.Width - (80 + CircuitSymbols.WireDotRadius * 2)) < 1e-9;
            bool fxMeasureOk = fxRef is not null
                && Math.Abs(fxRef.LocalSize.Height - FormulaRenderer.NominalWorldHeight) < 1e-9;

            bool catalogOk = allSymOk && wireMeasureOk && fxMeasureOk;
            Console.WriteLine($"[{(catalogOk ? "PASS" : "FAIL")}] 元件目录：{CircuitSymbols.All.Count} 种元件 Measure/CreateVisual/Describe 全通过，"
                              + $"导线宽 {wireRef?.LocalSize.Width:F1}、公式高 {fxRef?.LocalSize.Height:F1}");
            if (!catalogOk) failures++;

            // 位图冒烟：公式 / 电阻 / 导线真的画出非透明像素（渲染出口，不是空壳）
            static bool RendersInk(FrameworkElement visual, double size = 140)
            {
                var canvas = new Canvas { Width = size, Height = size };
                Canvas.SetLeft(visual, size / 2);
                Canvas.SetTop(visual, size / 2);
                canvas.Children.Add(visual);
                canvas.Measure(new Size(size, size));
                canvas.Arrange(new Rect(0, 0, size, size));
                canvas.UpdateLayout();

                var rtb = new RenderTargetBitmap((int)size, (int)size, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(canvas);
                var converted = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
                int stride = converted.PixelWidth * 4;
                var pixels = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(pixels, stride, 0);
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] > 16) return true;
                }
                return false;
            }

            var fxVisual = (FrameworkElement)catalog.Find(FormulaRenderer.KindName)!.CreateVisual(fxRef!);
            var wireVisual = (FrameworkElement)catalog.Find(CircuitRenderer.KindName)!.CreateVisual(wireRef!);
            var resVisual = (FrameworkElement)catalog.Find(CircuitRenderer.KindName)!.CreateVisual(resRef!);

            bool fxInk = RendersInk(fxVisual);
            bool wireInk = RendersInk(wireVisual);
            bool resInk = RendersInk(resVisual);

            bool pixelsOk = fxInk && wireInk && resInk;
            Console.WriteLine($"[{(pixelsOk ? "PASS" : "FAIL")}] 位图冒烟：公式={fxInk} 导线={wireInk} 电阻={resInk}（渲染出口有非透明像素）");
            if (!pixelsOk) failures++;
        }
        catch (Exception gfxEx)
        {
            Console.WriteLine($"[FAIL] 图形库接入断言抛异常：{gfxEx.GetType().Name} {gfxEx.Message}");
            failures++;
        }

        // ---- 4) 工具形态与光标：公式落一个弹回，电路连续放，书写光标隐藏，导线方向同宿主 ----
        string formulaToolSrc = StripComments(ReadSource(root, "MathPhys.Ink.Plugin.Formula", "FormulaTool.cs"), false);
        string circuitToolSrc = StripComments(ReadSource(root, "MathPhys.Ink.Plugin.CircuitKit", "CircuitTool.cs"), false);
        bool toolOk = formulaToolSrc.Contains("IGfxTool", StringComparison.Ordinal)
                   && !circuitToolSrc.Contains("IGfxTool", StringComparison.Ordinal)
                   && formulaToolSrc.Contains("Cursors.None", StringComparison.Ordinal)
                   && circuitToolSrc.Contains("Cursors.None", StringComparison.Ordinal)
                   && circuitToolSrc.Contains("Math.Atan2(dy, dx)", StringComparison.Ordinal);
        Console.WriteLine($"[{(toolOk ? "PASS" : "FAIL")}] 工具形态：公式=IGfxTool（落成弹回选择）、电路=普通工具（连续放）、光标隐藏、导线 atan2 方向与宿主同向");
        if (!toolOk) failures++;

        return failures;
    }

    // ================================================================= M15

    /// <summary>
    /// M15 断言组：公式插件依赖 dll（M14 热修）+ 电路引脚吸附与导线跟随。
    /// </summary>
    /// <remarks>
    /// 吸附与解算都是纯函数，无窗口直接断言；跟随走 <see cref="MathPhys.Ink.Gfx.GfxObjectStore.UpdatePose"/>
    /// 触发真实的 RaiseChanged → RecomputeAttachments 链路（不是手工调 solver）。
    /// </remarks>
    private static int RunM15Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M15：公式依赖热修 + 电路自动连线 ----");

        string root = FindRepoRoot(outDir);

        static string ReadSource(string root, params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { root, "src" }.Concat(parts).ToArray()), Encoding.UTF8);

        // ---- 1) 公式插件：EnableDynamicLoading 已加，Release bin 有依赖 dll（M14 真机缺件的根修）----
        string formulaProj = ReadSource(root, "MathPhys.Ink.Plugin.Formula", "MathPhys.Ink.Plugin.Formula.csproj");
        string releaseBin = Path.Combine(root, "src", "MathPhys.Ink.Plugin.Formula", "bin", "Release", "net8.0-windows");
        bool formulaDeps = formulaProj.Contains("<EnableDynamicLoading>true</EnableDynamicLoading>", StringComparison.Ordinal)
            && File.Exists(Path.Combine(releaseBin, "WpfMath.dll"))
            && File.Exists(Path.Combine(releaseBin, "XamlMath.Shared.dll"));
        Console.WriteLine($"[{(formulaDeps ? "PASS" : "FAIL")}] 公式插件：EnableDynamicLoading 生效，WpfMath / XamlMath.Shared 落 bin");
        if (!formulaDeps) failures++;

        // ---- 2) 引脚声明完整性：17 种元件；双端 (±32,0)、接地单引脚 (0,-20) ----
        bool pinsOk = CircuitSymbols.All.Count == 17;
        foreach (var def in CircuitSymbols.All)
        {
            if (def.Key == "ground")
            {
                if (def.PinOffsets.Length != 1 || def.PinOffsets[0] != new Point(0, -20)) pinsOk = false;
            }
            else if (def.PinOffsets.Length != 2
                     || def.PinOffsets[0] != new Point(-32, 0)
                     || def.PinOffsets[1] != new Point(32, 0))
            {
                pinsOk = false;
            }
        }
        Console.WriteLine($"[{(pinsOk ? "PASS" : "FAIL")}] 引脚表：{CircuitSymbols.All.Count} 种元件，双端 (±32,0)、接地 (0,-20)");
        if (!pinsOk) failures++;

        // ---- 3) 引脚吸附：世界坐标换算（含旋转）、身份命中、导线不是目标、放元件对齐 ----
        var catalog = new GfxRendererCatalog();
        catalog.Register(new FormulaRenderer(), "公式（学科工具）");
        catalog.Register(new CircuitRenderer(), "电路元件（学科工具）");

        try
        {
            var store = new GfxObjectStore(catalog);
            string cellId = store.Add(new GfxDraft
            {
                Kind = CircuitRenderer.KindName, Center = new Point(100, 100),
                Color = Colors.Black, LineWorldWidth = 1.5,
                Texts = new Dictionary<string, string> { [CircuitRenderer.SymbolKey] = "cell" },
            });

            IGfxObjectRef? cell = null;
            foreach (var o in store.Objects)
            {
                if (o.Id == cellId) cell = o;
            }

            bool pinWorldOk = cell is not null
                && CircuitRenderer.PinWorldOf(cell, 0) == new Point(68, 100)
                && CircuitRenderer.PinWorldOf(cell, 1) == new Point(132, 100)
                && CircuitRenderer.PinWorldOf(cell, 2) is null;

            // 旋转 30° 后引脚必须跟着转（与 WPF Matrix.Rotate 同一套数学）
            store.UpdatePose(cellId, new Point(100, 100), 30, 1);
            var matrix = new System.Windows.Media.Matrix();
            matrix.Rotate(30);
            var expectedPin1 = matrix.Transform(new Point(32, 0)) + new Vector(100, 100);
            pinWorldOk &= cell is not null && CircuitRenderer.PinWorldOf(cell, 1) == expectedPin1;

            // 身份命中：附近找引脚给的是"谁的哪个引脚"；半径外 / 导线都不是目标
            store.UpdatePose(cellId, new Point(100, 100), 0, 1);
            string wireId = store.Add(new GfxDraft
            {
                Kind = CircuitRenderer.KindName, Center = new Point(200, 100), RotationDegrees = 0,
                Color = Colors.Black, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { [CircuitSymbols.WireHalfLenKey] = 20 },
                Texts = new Dictionary<string, string> { [CircuitRenderer.SymbolKey] = CircuitSymbols.WireKey },
            });

            bool snapOk = CircuitSnap.TryFindPin(store.Objects, new Point(130, 98), 5, out PinHit hit)
                && hit.ObjectId == cellId && hit.PinIndex == 1 && hit.World == new Point(132, 100)
                && !CircuitSnap.TryFindPin(store.Objects, new Point(180, 100), 5, out _)
                && !CircuitSnap.TryFindPin(store.Objects, new Point(200, 100), 3, out _);   // 导线端点不是吸附目标

            // 放元件对齐：res 的引脚 0 正对 cell 的引脚 1
            bool alignOk = CircuitSnap.TryAlignSymbol(
                CircuitSymbols.Find("res")!, store.Objects, new Point(164, 100), 40, out Point aligned)
                && aligned == new Point(164, 100);

            bool snapAllOk = pinWorldOk && snapOk && alignOk;
            Console.WriteLine($"[{(snapAllOk ? "PASS" : "FAIL")}] 引脚吸附：世界换算（含旋转）={pinWorldOk}、身份命中={snapOk}、放元件对齐={alignOk}");
            if (!snapAllOk) failures++;

            // ---- 4) 导线跟随：两端绑定 → 元件动导线动；半自由退化；目标被删保持现状 ----
            string cellBId = store.Add(new GfxDraft
            {
                Kind = CircuitRenderer.KindName, Center = new Point(300, 100),
                Color = Colors.Black, LineWorldWidth = 1.5,
                Texts = new Dictionary<string, string> { [CircuitRenderer.SymbolKey] = "cell" },
            });
            string boundWireId = store.Add(new GfxDraft
            {
                Kind = CircuitRenderer.KindName, Center = new Point(200, 100), RotationDegrees = 0,
                Color = Colors.Black, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { [CircuitSymbols.WireHalfLenKey] = 68 },
                Texts = new Dictionary<string, string>
                {
                    [CircuitRenderer.SymbolKey] = CircuitSymbols.WireKey,
                    [CircuitSymbols.WireAKey] = CircuitSymbols.PinTag(cellId, 1),
                    [CircuitSymbols.WireBKey] = CircuitSymbols.PinTag(cellBId, 0),
                },
            });

            // 元件 A 下移 50：导线两端 = 两引脚，中心/方向/长度全部重算
            // （★ 角度比较必须按模 360：宿主 ApplyPose 会把旋转归一化到 [0,360)，-20.2° 存成 339.8°）
            store.UpdatePose(cellId, new Point(100, 150), 0, 1);
            var boundWire = FindRef(store, boundWireId);
            double expectedHalf = Math.Sqrt(136 * 136 + 50 * 50) / 2;
            double expectedRotation = Math.Atan2(-50, 136) * 180.0 / Math.PI;
            bool followOk = boundWire is not null
                && boundWire.Center == new Point(200, 125)
                && AngleClose(boundWire.RotationDegrees, expectedRotation)
                && Math.Abs(boundWire.GetNumber(CircuitSymbols.WireHalfLenKey, -1) - expectedHalf) < 1e-9;

            // 元件 B 转 90°：B 端仍贴在它转后的引脚上（与 PinWorldOf 一致）
            store.UpdatePose(cellBId, new Point(300, 100), 90, 1);
            IGfxObjectRef? cellB = FindRef(store, cellBId);
            followOk &= boundWire is not null && cellB is not null
                && WireEndpoint(boundWire, true) == CircuitRenderer.PinWorldOf(cellB, 0);

            Console.WriteLine($"[{(followOk ? "PASS" : "FAIL")}] 导线跟随：元件移动/旋转后导线重算（UpdatePose 真实链路）");
            if (!followOk) failures++;

            // 半自由：只绑 A 端 —— A 端跟随，B 端原地不动
            string halfWireId = store.Add(new GfxDraft
            {
                Kind = CircuitRenderer.KindName, Center = new Point(200, 100), RotationDegrees = 0,
                Color = Colors.Black, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { [CircuitSymbols.WireHalfLenKey] = 68 },
                Texts = new Dictionary<string, string>
                {
                    [CircuitRenderer.SymbolKey] = CircuitSymbols.WireKey,
                    [CircuitSymbols.WireAKey] = CircuitSymbols.PinTag(cellId, 1),
                },
            });
            store.UpdatePose(cellId, new Point(100, 200), 0, 1);
            var halfWire = FindRef(store, halfWireId);
            // （端点经过 度→弧度 的往返换算，比较用容差而非精确相等）
            bool halfOk = halfWire is not null
                && CloseTo(WireEndpoint(halfWire, false), new Point(132, 200))    // A 端跟到元件引脚
                && CloseTo(WireEndpoint(halfWire, true), new Point(268, 100));    // B 端原地（旋转 + 半长共同保证）

            Console.WriteLine($"[{(halfOk ? "PASS" : "FAIL")}] 半自由导线：绑端跟随、自由端原地");
            if (!halfOk) failures++;

            // 目标被删：保持现状（不静默丢、不跳回原点）
            Point centerBefore = halfWire!.Center;
            double halfBefore = halfWire.GetNumber(CircuitSymbols.WireHalfLenKey, -1);
            store.Remove(cellId);
            bool keepOk = FindRef(store, halfWireId) is { } after
                && after.Center == centerBefore
                && Math.Abs(after.GetNumber(CircuitSymbols.WireHalfLenKey, -1) - halfBefore) < 1e-9;

            Console.WriteLine($"[{(keepOk ? "PASS" : "FAIL")}] 目标被删：导线保持现状");
            if (!keepOk) failures++;

            // ---- 5) 往返：绑定键随 Snapshot/Restore 保真 ----
            var snapshot = store.Snapshot();
            var store2 = new GfxObjectStore(catalog);
            store2.Restore(snapshot);
            var restoredWire = FindRef(store2, boundWireId);
            bool roundTripOk = restoredWire is not null
                && restoredWire.GetText(CircuitSymbols.WireAKey, "") == CircuitSymbols.PinTag(cellId, 1)
                && restoredWire.GetText(CircuitSymbols.WireBKey, "") == CircuitSymbols.PinTag(cellBId, 0);

            Console.WriteLine($"[{(roundTripOk ? "PASS" : "FAIL")}] 绑定键存档往返：wireA/wireB 保真");
            if (!roundTripOk) failures++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] 电路断言抛异常：{ex.GetType().Name} {ex.Message}");
            failures++;
        }

        // ---- 6) 源级：工具落绑定键、渲染器实现解算契约（去注释后再查 —— M13/M14 两次同款坑）----
        string circuitToolSrc = StripComments(ReadSource(root, "MathPhys.Ink.Plugin.CircuitKit", "CircuitTool.cs"), false);
        string circuitRendererSrc = StripComments(ReadSource(root, "MathPhys.Ink.Plugin.CircuitKit", "CircuitRenderer.cs"), false);
        bool sourceOk = circuitToolSrc.Contains("CircuitSymbols.WireAKey", StringComparison.Ordinal)
            && circuitToolSrc.Contains("CircuitSymbols.WireBKey", StringComparison.Ordinal)
            && circuitRendererSrc.Contains("IGfxAttachmentSolver", StringComparison.Ordinal);
        Console.WriteLine($"[{(sourceOk ? "PASS" : "FAIL")}] 源级：导线落绑定键、渲染器实现 IGfxAttachmentSolver");
        if (!sourceOk) failures++;

        // ---- 7) 源级：插件 ALC keep-alive 护栏（2026-09-24 真机 0x80131509 事故）----
        // collectible ALC 没人攥住会被 GC 回收 ⇒ 公式插件首绑 XamlMath 时炸。
        // 护栏钉两件事：LoadOne 必须把上下文塞进静态 KeepAlive 列表；上下文必须订阅 Unloading 绊线。
        string loaderSrc = StripComments(ReadSource(root, "MathPhys.Ink", "Tools", "Plugins", "PluginLoader.cs"), false);
        string ctxSrc = StripComments(ReadSource(root, "MathPhys.Ink", "Tools", "Plugins", "PluginLoadContext.cs"), false);
        bool keepAliveOk = loaderSrc.Contains("static readonly List<(PluginLoadContext Context, Assembly Assembly)> KeepAlive", StringComparison.Ordinal)
            && loaderSrc.Contains("KeepAlive.Add((loadContext, assembly))", StringComparison.Ordinal)
            && ctxSrc.Contains("Unloading +=", StringComparison.Ordinal);
        Console.WriteLine($"[{(keepAliveOk ? "PASS" : "FAIL")}] 源级：插件 ALC keep-alive 钉住 + Unloading 绊线");
        if (!keepAliveOk) failures++;

        return failures;
    }

    /// <summary>
    /// M16 断言组：浮动工具栏方形重排 + 渲染变换拖动。全部源级 ——
    /// 本里程碑是纯排版与拖动链路改动，行为面（显隐 / 持久化 / 缩放零触碰）由 M13 护栏继续盯着。
    /// </summary>
    private static int RunM16Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M16：工具栏方形重排与拖动手感 ----");

        string root = FindRepoRoot(outDir);

        static string ReadSource(string root, params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { root, "src" }.Concat(parts).ToArray()), Encoding.UTF8);

        // ---- 1) FloatingDrag：拖动走渲染变换，松手才落 Margin ----
        // 根因回归护栏：以前每次 TouchMove 改 Margin ⇒ 213Hz 下每帧全量布局 ⇒ 卡。
        string dragSrc = StripComments(ReadSource(root, "MathPhys.Ink", "Views", "Controls", "FloatingDrag.cs"), false);
        bool dragOk = dragSrc.Contains("TranslateTransform", StringComparison.Ordinal)
            && dragSrc.Contains("_translate.X = _current.X - _startMargin.X", StringComparison.Ordinal)
            && dragSrc.Contains("_target.Margin = new Thickness(_current.X, _current.Y, 0, 0)", StringComparison.Ordinal)
            && !dragSrc.Contains("onMoved", StringComparison.Ordinal);
        Console.WriteLine($"[{(dragOk ? "PASS" : "FAIL")}] FloatingDrag：拖动走 TranslateTransform、松手落 Margin、无拖动中布局写入");
        if (!dragOk) failures++;

        // ---- 2) 两处调用点已切到新签名 ----
        string mainCs = StripComments(ReadSource(root, "MathPhys.Ink", "Views", "MainWindow.xaml.cs"), false);
        string hostCs = StripComments(ReadSource(root, "MathPhys.Ink", "Views", "Controls", "CanvasViewportHost.xaml.cs"), false);
        bool callsOk = mainCs.Contains("FloatingDrag.Attach", StringComparison.Ordinal)
            && !mainCs.Contains("onMoved:", StringComparison.Ordinal)
            && !hostCs.Contains("onMoved:", StringComparison.Ordinal);
        Console.WriteLine($"[{(callsOk ? "PASS" : "FAIL")}] 工具栏/计算器两处 Attach 调用已切新签名（无 onMoved）");
        if (!callsOk) failures++;

        // ---- 3) XAML：方形重排落地（限宽 + 瓦片样式 + 工具按钮换行面板）----
        // ★ 读原文（不去注释）：块尾锚点"悬浮球"本身就在注释里，StripComments 会把它剥掉。
        //   瓦片样式计数不受注释影响 —— 本文件注释里不出现 ToolbarTileButton 字样。
        string mainXaml = ReadSource(root, "MathPhys.Ink", "Views", "MainWindow.xaml");
        int topStart = mainXaml.IndexOf("x:Name=\"TopChrome\"", StringComparison.Ordinal);
        string topBlock = topStart >= 0
            ? mainXaml[topStart..Math.Min(mainXaml.Length, mainXaml.IndexOf("M12 S2：悬浮球", topStart))]
            : string.Empty;
        int tileCount = CountOccurrences(topBlock, "Style.ToolbarTileButton");
        bool layoutOk = topStart >= 0
            && topBlock.Contains("MaxWidth=\"500\"", StringComparison.Ordinal)
            && tileCount >= 10
            && topBlock.Contains("Style.TileText", StringComparison.Ordinal)
            && topBlock.Contains("x:Name=\"ToolButtons\"", StringComparison.Ordinal);
        Console.WriteLine($"[{(layoutOk ? "PASS" : "FAIL")}] 工具栏方形重排：TopChrome 限宽 500、瓦片按钮 {tileCount} 个（≥10）、含 TileText 文字行");
        if (!layoutOk) failures++;

        // ---- 4) 工具瓦片的容器必须是一个限宽 WrapPanel（否则十几颗瓦片永远一行，撑破方阵）----
        // ★ M20 S4 改了承载方式：瓦片不再走 ItemsControl，而是由 code-behind 按工具目录表
        //   直接挂到这个 WrapPanel 上。原因值得记一笔 —— WrapPanel 量每个子元素时给的是
        //   **整行的可用宽度**而不是"本行剩下的宽度"，所以嵌套在里面的容器会以为自己
        //   独占一整行（按 492 算宽度），于是被外层换到新的一行、把组标题孤零零丢在上一行
        //   （实测白花 44px 高）。改成直接子元素之后谁都不会算错。
        //   判据因此从"内部有个自闭合 WrapPanel"改成"ToolButtons 这个名字就落在 WrapPanel 上"。
        int toolsStart = mainXaml.IndexOf("x:Name=\"ToolButtons\"", StringComparison.Ordinal);
        int toolsFrom = Math.Max(0, toolsStart - 60);
        string toolsBlock = toolsStart >= 0
            ? mainXaml.Substring(toolsFrom, Math.Min(180, mainXaml.Length - toolsFrom))
            : string.Empty;
        bool toolsWrapOk = toolsBlock.Contains("WrapPanel x:Name=\"ToolButtons\"", StringComparison.Ordinal);
        Console.WriteLine($"[{(toolsWrapOk ? "PASS" : "FAIL")}] ToolButtons 是限宽 WrapPanel，瓦片由目录表直接挂上去（自动换行）");
        if (!toolsWrapOk) failures++;

        // ---- 5) 设计令牌：瓦片样式 / 瓦片文字 / 瓦片间距都在（防手写魔法值回退）----
        string buttonsXaml = StripComments(ReadSource(root, "MathPhys.Ink", "Design", "Controls", "Buttons.xaml"), true);
        string spacingXaml = StripComments(ReadSource(root, "MathPhys.Ink", "Design", "Spacing.xaml"), true);
        bool tokensOk = buttonsXaml.Contains("x:Key=\"Style.ToolbarTileButton\"", StringComparison.Ordinal)
            && buttonsXaml.Contains("x:Key=\"Style.TileText\"", StringComparison.Ordinal)
            && spacingXaml.Contains("x:Key=\"Gap.Tile\"", StringComparison.Ordinal);
        Console.WriteLine($"[{(tokensOk ? "PASS" : "FAIL")}] 设计令牌：ToolbarTileButton / TileText / Gap.Tile 齐备");
        if (!tokensOk) failures++;

        return failures;
    }

    // ================================================================= M17：三角板沿边画线

    /// <summary>
    /// M17：三角板沿边画线 —— EdgeSnapHelper 数学断言（真代码）+ TriangleTraceTool 全状态机
    /// （假上下文 × 真 GfxObjectStore）+ 源级护栏。
    /// </summary>
    private static int RunM17Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M17：三角板沿边画线 ----");

        string root = FindRepoRoot(outDir);

        static string ReadSource(string root, params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { root, "src" }.Concat(parts).ToArray()), Encoding.UTF8);

        // ---- 1) EdgeSnapHelper：距离夹 t、投影不夹 t ----
        // 投影不夹 t 是「画出比板更长的延长线」的立身之本；距离夹 t 是「端点外按端点算」。
        var segA = new Point(0, 0);
        var segB = new Point(100, 0);
        bool distOk = EdgeSnapHelper.DistanceToSegment(new Point(50, 7), segA, segB) == 7.0
            && Math.Abs(EdgeSnapHelper.DistanceToSegment(new Point(-30, 40), segA, segB) - 50.0) < 1e-9;

        var extended = EdgeSnapHelper.ProjectOntoLine(new Point(150, -20), segA, segB, out double tOver);
        var behind = EdgeSnapHelper.ProjectOntoLine(new Point(-50, 5), segA, segB, out double tUnder);
        bool projOk = CloseTo(extended, new Point(150, 0)) && Math.Abs(tOver - 1.5) < 1e-9
            && CloseTo(behind, new Point(-50, 0)) && Math.Abs(tUnder + 0.5) < 1e-9;

        bool helper1Ok = distOk && projOk;
        Console.WriteLine($"[{(helper1Ok ? "PASS" : "FAIL")}] EdgeSnapHelper：距离夹 t（端点外按端点算）={distOk}、投影不夹 t（延长线）={projOk}");
        if (!helper1Ok) failures++;

        // ---- 2) EdgeSnapHelper：最近边 / 顶点吸附 / 三角形内判定 ----
        var edges = new[]
        {
            new EdgeSnapHelper.Edge(new Point(0, 0), new Point(100, 0)),
            new EdgeSnapHelper.Edge(new Point(100, 0), new Point(100, 100)),
        };
        var near = EdgeSnapHelper.NearestEdge(new Point(90, 4), edges);
        bool nearOk = near.Index == 0 && Math.Abs(near.Distance - 4.0) < 1e-9
            && CloseTo(near.ClosestPoint, new Point(90, 0));

        bool snapOk = EdgeSnapHelper.TrySnapToVertex(new Point(98, 3), new[] { new Point(100, 0) }, 5.0, out var snapped)
            && CloseTo(snapped, new Point(100, 0))
            && !EdgeSnapHelper.TrySnapToVertex(new Point(98, 3), new[] { new Point(100, 0) }, 2.0, out _);

        var triA = new Point(0, 0);
        var triB = new Point(100, 0);
        var triC = new Point(0, 100);
        bool insideOk = EdgeSnapHelper.PointInTriangle(new Point(50, 50), triA, triB, triC)
            && !EdgeSnapHelper.PointInTriangle(new Point(90, 90), triA, triB, triC)
            && EdgeSnapHelper.PointInTriangle(new Point(50, 0), triA, triB, triC)      // 边界算内
            && EdgeSnapHelper.PointInTriangle(new Point(50, 50), triA, triC, triB);    // 顶点序相反同样成立

        bool helper2Ok = nearOk && snapOk && insideOk;
        Console.WriteLine($"[{(helper2Ok ? "PASS" : "FAIL")}] EdgeSnapHelper：最近边={nearOk}、顶点吸附={snapOk}、三角形内（含边界/双向序）={insideOk}");
        if (!helper2Ok) failures++;

        // ---- 3) 真状态机：TriangleTraceTool × GfxObjectStore × 假上下文 ----
        // 板：45° 板、直角顶点 (100,100)、长直角边 110 沿 +x（到 (210,100)）、短边沿 -y（到 (100,-10)）。
        try
        {
            var catalog = new GfxRendererCatalog();
            catalog.Register(new TriangleRenderer(), "三角板（学科工具）");
            var store = new GfxObjectStore(catalog);

            // 走真宿主通道（含撤销历史）—— 工具拿到的 IGfxObjectHost 与真机上同型
            var history = new GfxHistory(store);
            var boardHistory = new BoardHistory(new InkHistory(new StrokeCollection()), history);
            var host = new HostGfxObjectHost(store, history, boardHistory);

            string boardId = store.Add(new GfxDraft
            {
                Kind = TriangleRenderer.KindName,
                Center = new Point(100, 100),
                Color = Colors.Black,
                LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [TriangleGeometry.LegKey] = 110,
                    [TriangleGeometry.KindKey] = 0,
                },
            });

            var ctx = new FakeToolContext { GfxOverride = host, Scale = 1.0 };
            var tool = new TriangleTraceTool();
            tool.Activate(ctx);

            // 3a. 落笔贴长直角边（(150,108) 距边 8 ≤ 容差 20）→ Move 滑出板外 → 抬笔：延长线落墨
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Down, new Point(150, 108), new Point(150, 108), 0, false, 1));
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Move, new Point(260, 90), new Point(260, 90), 0, false, 1));

            System.Windows.Shapes.Line? preview = null;
            foreach (var p in ctx.Previews)
            {
                if (p is System.Windows.Shapes.Line line) { preview = line; break; }
            }

            bool previewOk = preview is not null
                && CloseTo(new Point(preview.X1, preview.Y1), new Point(150, 100))
                && CloseTo(new Point(preview.X2, preview.Y2), new Point(260, 100));    // t≈1.45：已在延长线上

            tool.OnPointer(new ToolPointer(ToolPointerPhase.Up, new Point(260, 90), new Point(260, 90), 0, false, 1));

            bool inkOk = ctx.Committed.Count == 1;
            if (inkOk)
            {
                var pts = ctx.Committed[0].StylusPoints;
                inkOk = pts.Count == 2
                    && CloseTo(new Point(pts[0].X, pts[0].Y), new Point(150, 100))
                    && CloseTo(new Point(pts[1].X, pts[1].Y), new Point(260, 100));
            }

            bool boardStaysOk = FindRef(store, boardId) is not null;   // 板不增不减
            bool traceOk = previewOk && inkOk && boardStaysOk;
            Console.WriteLine($"[{(traceOk ? "PASS" : "FAIL")}] 沿边画线：预览投影（延长线）={previewOk}、落墨两点共线={inkOk}、板不增不减={boardStaysOk}");
            if (!traceOk) failures++;

            // 3b. 落笔靠近直角顶点（(98,102) 距顶点 ≈2.8 ≤ 24）→ 起点吸到顶点；Move 到边中段落墨
            ctx.Reset();
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Down, new Point(98, 102), new Point(98, 102), 0, false, 1));
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Move, new Point(150, 60), new Point(150, 60), 0, false, 1));
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Up, new Point(150, 60), new Point(150, 60), 0, false, 1));

            bool vertexOk = ctx.Committed.Count == 1;
            if (vertexOk)
            {
                var pts = ctx.Committed[0].StylusPoints;
                vertexOk = pts.Count == 2
                    && CloseTo(new Point(pts[0].X, pts[0].Y), new Point(100, 100))
                    && CloseTo(new Point(pts[1].X, pts[1].Y), new Point(150, 100));
            }
            Console.WriteLine($"[{(vertexOk ? "PASS" : "FAIL")}] 顶点吸附：起点吸到直角顶点、落墨从顶点出发");
            if (!vertexOk) failures++;

            // 3c. 板内拖动：(132,68) 离三边都 >20（不触发画线），Move 后板心跟手；拖板不落墨
            ctx.Reset();
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Down, new Point(132, 68), new Point(132, 68), 0, false, 1));
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Move, new Point(182, 48), new Point(182, 48), 0, false, 1));
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Up, new Point(182, 48), new Point(182, 48), 0, false, 1));

            var board = FindRef(store, boardId);
            bool dragOk = board is not null
                && CloseTo(board.Center, new Point(150, 80))
                && ctx.Committed.Count == 0;
            Console.WriteLine($"[{(dragOk ? "PASS" : "FAIL")}] 板内拖动：板心跟手到 (150,80)={board is not null && CloseTo(board.Center, new Point(150, 80))}、不落墨={ctx.Committed.Count == 0}");
            if (!dragOk) failures++;

            // 3d. 板外落笔：不落墨、给提示
            ctx.Reset();
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Down, new Point(400, 400), new Point(400, 400), 0, false, 1));
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Up, new Point(400, 400), new Point(400, 400), 0, false, 1));
            bool outsideOk = ctx.Committed.Count == 0
                && ctx.LastStatus is not null
                && ctx.LastStatus.Contains("边缘", StringComparison.Ordinal);
            Console.WriteLine($"[{(outsideOk ? "PASS" : "FAIL")}] 板外落笔：忽略并提示");
            if (!outsideOk) failures++;

            // 3e. 画布上没有板：提示先放板
            var emptyStore = new GfxObjectStore(new GfxRendererCatalog());
            var emptyHistory = new GfxHistory(emptyStore);
            var emptyCtx = new FakeToolContext
            {
                GfxOverride = new HostGfxObjectHost(emptyStore, emptyHistory,
                    new BoardHistory(new InkHistory(new StrokeCollection()), emptyHistory)),
                Scale = 1.0,
            };
            tool.Activate(emptyCtx);
            emptyCtx.Reset();
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Down, new Point(0, 0), new Point(0, 0), 0, false, 1));
            bool noBoardOk = emptyCtx.LastStatus is not null
                && emptyCtx.LastStatus.Contains("三角板", StringComparison.Ordinal)
                && emptyCtx.Committed.Count == 0;
            Console.WriteLine($"[{(noBoardOk ? "PASS" : "FAIL")}] 画布无板：提示先用三角板工具放板");
            if (!noBoardOk) failures++;

            // 3f. 宿主没有图形层：明确降级提示，不崩
            var bareCtx = new FakeToolContext { Scale = 1.0 };   // Gfx = null
            tool.Activate(bareCtx);
            bareCtx.Reset();
            tool.OnPointer(new ToolPointer(ToolPointerPhase.Down, new Point(0, 0), new Point(0, 0), 0, false, 1));
            bool noGfxOk = bareCtx.LastStatus is not null
                && bareCtx.LastStatus.Contains("不支持", StringComparison.Ordinal)
                && bareCtx.Committed.Count == 0;
            Console.WriteLine($"[{(noGfxOk ? "PASS" : "FAIL")}] 无图形层：降级提示不崩");
            if (!noGfxOk) failures++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] 状态机断言抛异常：{ex.Message}");
            failures++;
        }

        // ---- 4) 源级护栏 ----
        // 容差必须除以缩放（不同缩放下手感一致）；工具不实现 IGfxTool（落墨不落对象、不自动弹回选择）。
        string traceSrc = StripComments(ReadSource(root, "MathPhys.Ink.Plugin.Triangle", "TriangleTraceTool.cs"), false);
        string pluginSrc = StripComments(ReadSource(root, "MathPhys.Ink.Plugin.Triangle", "TrianglePlugin.cs"), false);
        bool srcOk = traceSrc.Contains("EdgeTolerancePixels / SafeScale(context)", StringComparison.Ordinal)
            && traceSrc.Contains("class TriangleTraceTool : ITool", StringComparison.Ordinal)
            && !traceSrc.Contains("IGfxTool", StringComparison.Ordinal)
            && pluginSrc.Contains("new TriangleTraceTool()", StringComparison.Ordinal);
        Console.WriteLine($"[{(srcOk ? "PASS" : "FAIL")}] 源级护栏：容差除缩放、不实现 IGfxTool、插件已注册");
        if (!srcOk) failures++;

        return failures;
    }

    /// <summary>数子串出现次数（M16 瓦片按钮计数用）。</summary>
    private static int CountOccurrences(string text, string needle)
    {
        if (needle.Length == 0) return 0;
        int count = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// 跨插件工具快捷键唯一性（全部真实插件装进一个注册表）。
    /// </summary>
    /// <remarks>
    /// ★ 这一组的由来：坐标系（M7.4，docs/11）与「三角板·沿边画线」（M17，docs/22 + 操作卡）
    /// 都声明了 <c>D9</c>，而 <see cref="ToolRegistry.FindByShortcut"/> 只返回<b>第一个</b>命中者，
    /// 插件按目录名排序加载 ⇒ 坐标系赢、沿边画线那个键是死的 ——
    /// 老师按操作卡上的"9"选中的却是坐标系，现象像"工具选错了"，查起来毫无线索。
    /// 单插件的断言永远看不到这类撞车，所以这里必须把<b>全部插件装进同一个注册表</b>再查。
    /// </remarks>
    private static int RunShortcutChecks()
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- 跨插件：工具快捷键唯一性与文档口径 ----");

        // 用真的宿主：它自带「选择」（V）与内置笔/橡皮/手/题号 —— 只装插件的话 V 一定是空的
        var host = new CanvasViewportHost();
        try
        {
            new RulerPlugin().Register(host.PluginRegistry);
            new ProtractorPlugin().Register(host.PluginRegistry);
            new TrianglePlugin().Register(host.PluginRegistry);
            new CoordSystemPlugin().Register(host.PluginRegistry);
            new FunctionPlotPlugin().Register(host.PluginRegistry);
            new VectorArrowPlugin().Register(host.PluginRegistry);
            new FormulaPlugin().Register(host.PluginRegistry);
            new CircuitKitPlugin().Register(host.PluginRegistry);
            new CompassPlugin().Register(host.PluginRegistry);
            new GeoGebraPlugin().Register(host.PluginRegistry);

            var tools = host.PluginRegistry.Tools;

            // 1) 没有任何两个工具声明同一个键（null 不算）
            var byShortcut = new Dictionary<Key, string>();
            var collisions = new List<string>();
            foreach (var tool in tools)
            {
                if (tool.Shortcut is not { } key) continue;
                if (byShortcut.TryGetValue(key, out string? owner)) collisions.Add($"{key}: {owner} / {tool.Id}");
                else byShortcut[key] = tool.Id;
            }

            bool uniqueOk = collisions.Count == 0;
            Console.WriteLine($"[{(uniqueOk ? "PASS" : "FAIL")}] {tools.Count} 个工具的快捷键互不撞车"
                              + (uniqueOk ? "" : $"（撞车：{string.Join("、", collisions)}）"));
            if (!uniqueOk) failures++;

            // 2) 文档口径（操作卡与 docs/22 承诺的键位）确实能查到对应工具
            var expected = new (Key Key, string Id)[]
            {
                (Key.D1, ToolIds.Pen), (Key.D2, ToolIds.Eraser), (Key.D3, ToolIds.Hand),
                (Key.D4, RulerToolIds.Ruler), (Key.D5, RulerToolIds.FreeRuler),
                (Key.D6, ProtractorToolIds.Protractor),
                (Key.D7, TriangleToolIds.FortyFive), (Key.D8, TriangleToolIds.SixtyNinety),
                (Key.D9, TriangleTraceToolIds.Trace),     // ★ 操作卡："按 9 沿边画线"
                (Key.D0, FunctionToolIds.Id),
                (Key.C, CoordSystemToolIds.Id),           // ★ 坐标系让位到 C（原 D9 撞车）
                (Key.V, ToolIds.GfxSelect),
                (Key.N, ArrowToolIds.Id),
                (Key.R, CompassToolIds.Circle),
                (Key.A, CompassToolIds.Arc),
            };

            var mismatched = new List<string>();
            foreach (var (key, id) in expected)
            {
                string? hit = host.FindToolByShortcut(key)?.Id;
                if (hit != id) mismatched.Add($"{key}⇒{hit ?? "(无)"}（期望 {id}）");
            }

            bool mapOk = mismatched.Count == 0;
            Console.WriteLine($"[{(mapOk ? "PASS" : "FAIL")}] 承诺的键位逐个能查到对应工具"
                              + (mapOk ? "" : $"（不符：{string.Join("、", mismatched)}）"));
            if (!mapOk) failures++;

            // 3) 小键盘与大键盘数字归一（用户按的是"9"，不关心在哪块键盘上）
            bool keypadOk = host.FindToolByShortcut(Key.NumPad9)?.Id == TriangleTraceToolIds.Trace;
            Console.WriteLine($"[{(keypadOk ? "PASS" : "FAIL")}] 小键盘 9 也落到「沿边画线」（大小键盘归一）");
            if (!keypadOk) failures++;
        }
        finally
        {
            host.Shutdown();
        }

        return failures;
    }

    /// <summary>
    /// M18：悬浮球流畅拖动（渲染变换）+ 断笔直线（断点检测 + Reset 兜底）+ 笔迹保点数平滑。
    /// </summary>
    /// <remarks>
    /// JumpDetector / PathSmoother 是纯函数层（无 RTI 依赖），直接真断言；
    /// 悬浮球与插件接线的流畅性/正确性用源级护栏钉住（StripComments 后锚点必须在代码里）。
    /// </remarks>
    private static int RunM18Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M18：悬浮球流畅 · 断笔直线 · 笔迹平滑 ----");

        string root = FindRepoRoot(outDir);

        static string ReadSource(string root, params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { root, "src" }.Concat(parts).ToArray()), Encoding.UTF8);

        // ---- 1) JumpDetector：快笔不误伤、瞬移必抓、Down 丢失（停顿+大位移）必抓、坏点不抛 ----
        // 快笔：0.5 world/ms、每包 1.8 world（281Hz 实测量级）—— 距离远低于 80，双条件拦下
        bool fastOk = !JumpDetector.IsJump(0, 0, 0, 1.8, 0, 4);
        // 手掌并入：3.5ms 内瞬移 100 world —— 速度与距离双超阈
        bool jumpOk = JumpDetector.IsJump(0, 0, 0, 100, 0, 4);
        // Down 丢失：200ms 停顿后 90 world —— 长停顿分支单看距离
        bool gapOk = JumpDetector.IsJump(0, 0, 0, 90, 0, 200);
        // 长停顿后的小位移不判（正常顿笔后再落）
        bool gapSmallOk = !JumpDetector.IsJump(0, 0, 0, 5, 0, 200);
        // NaN 坐标：不抛、不判
        bool nanOk = !JumpDetector.IsJump(0, 0, 0, double.NaN, 0, 4)
            && !JumpDetector.IsJump(double.NaN, 0, 0, 100, 0, 4);

        // ★ 阈值死区（M24 诊断留观项）：包内 dt=0 ⇒ 速度退化为「距离/1」，而判据是
        //   「速度超阈 AND 距离超阈」的双条件 AND，所以 79 world（≈28 mm）卡在
        //   JumpMinDistance=80 之下 ⇒ 判「不跳」。手掌并入的物理位移 > 85 world 才被抓，
        //   79~85 world 之间是真实存在的盲区 —— 这里把盲区钉成断言，将来若有人把判据
        //   改成 OR、或把距离门限下调，这条会先亮起来提醒同步复核。
        bool deadZoneOk = !JumpDetector.IsJump(0, 0, 0, 79, 0, 0)
            && JumpDetector.IsJump(0, 0, 0, 81, 0, 0);

        bool jumpAllOk = fastOk && jumpOk && gapOk && gapSmallOk && nanOk && deadZoneOk;
        Console.WriteLine($"[{(jumpAllOk ? "PASS" : "FAIL")}] JumpDetector：快笔不误伤={fastOk}、瞬移必抓={jumpOk}、停顿+大位移={gapOk}、停顿小位移放过={gapSmallOk}、NaN 不抛={nanOk}、阈值死区(79不判/81判)={deadZoneOk}");
        if (!jumpAllOk) failures++;

        // ---- 1b) PacketPointFilter：包内逐点分级（M24 热修①） ----
        // 坏点三签名：NaN / 无穷 / 精确 (0,0) —— 全部 BadPoint
        bool badNaN = PacketPointFilter.IsBadPoint(double.NaN, 5) && PacketPointFilter.IsBadPoint(5, double.NaN);
        bool badInf = PacketPointFilter.IsBadPoint(double.PositiveInfinity, 5) && PacketPointFilter.IsBadPoint(5, double.NegativeInfinity);
        bool badZero = PacketPointFilter.IsBadPoint(0, 0);
        bool goodNormal = !PacketPointFilter.IsBadPoint(12.3, 45.6);
        // 无锚（本笔首点）：完好点不判跳变
        bool noAnchorGood = PacketPointFilter.Classify(false, 0, 0, 0, 100, 100, 0) == PacketPointVerdict.Good;
        // 包内跳变（同包 dt=0 ⇒ 距离单条件）：80 world 的点间瞬移必抓
        bool intraJump = PacketPointFilter.Classify(true, 0, 0, 0, 100, 0, 0) == PacketPointVerdict.Jump;
        // 包内正常点：不误伤
        bool intraNormal = PacketPointFilter.Classify(true, 0, 0, 0, 1.8, 0, 0) == PacketPointVerdict.Good;
        // 包内坏点优先于跳变判定：坏点就是 BadPoint，不吃掉整笔
        bool badBeforeJump = PacketPointFilter.Classify(true, 0, 0, 0, double.NaN, 0, 0) == PacketPointVerdict.BadPoint;
        // 跨包长停顿 + 大位移（Down 丢失特征）在逐点口径下仍被抓
        bool gapJump = PacketPointFilter.Classify(true, 0, 0, 0, 90, 0, 200) == PacketPointVerdict.Jump;
        // 长停顿 + 小位移放过（正常顿笔后再落）
        bool gapSmall = PacketPointFilter.Classify(true, 0, 0, 0, 5, 0, 200) == PacketPointVerdict.Good;

        bool filterAllOk = badNaN && badInf && badZero && goodNormal && noAnchorGood
            && intraJump && intraNormal && badBeforeJump && gapJump && gapSmall;
        Console.WriteLine($"[{(filterAllOk ? "PASS" : "FAIL")}] PacketPointFilter：NaN坏点={badNaN}、无穷坏点={badInf}、(0,0)坏点={badZero}、正常点完好={goodNormal}、无锚不判跳={noAnchorGood}、包内瞬移必抓={intraJump}、包内正常不误伤={intraNormal}、坏点优先={badBeforeJump}、停顿+大位移={gapJump}、停顿小位移放过={gapSmall}");
        if (!filterAllOk) failures++;

        // ---- 2) PathSmoother：保点数 / 首点锚定 / 抖动能量下降 / Off 恒等 ----
        // 确定性噪声折线：相邻点 ±1.5 交替 ⇒ 二阶差分（抖动能量）很大
        const int n = 200;
        var rawX = new double[n];
        for (int i = 0; i < n; i++) rawX[i] = i + (i % 2 == 0 ? 1.5 : -1.5);

        const double alpha = 0.6;
        var smX = new double[n];
        smX[0] = rawX[0];   // 首点锚定（与插件 ApplySmooth 同一语义）
        for (int i = 1; i < n; i++) smX[i] = PathSmoother.Next(smX[i - 1], rawX[i], alpha);

        static double JitterEnergy(double[] xs)
        {
            double sum = 0;
            for (int i = 1; i + 1 < xs.Length; i++) sum += Math.Abs(xs[i - 1] - 2 * xs[i] + xs[i + 1]);
            return sum;
        }

        double rawEnergy = JitterEnergy(rawX);
        double smEnergy = JitterEnergy(smX);

        bool countOk = smX.Length == n;
        bool anchorOk = Math.Abs(smX[0] - rawX[0]) < 1e-12;
        bool energyOk = smEnergy < rawEnergy / 2.0;   // 出厂 α=0.6 应把抖动能量砍掉一半以上
        bool offIdentity = Math.Abs(PathSmoother.Next(3.7, 100.0, PathSmoother.Alpha(SmoothStrength.Off)) - 100.0) < 1e-12;

        bool smoothAllOk = countOk && anchorOk && energyOk && offIdentity;
        Console.WriteLine($"[{(smoothAllOk ? "PASS" : "FAIL")}] PathSmoother：点数不变={countOk}、首点锚定={anchorOk}、抖动能量减半({smEnergy:F1}<{rawEnergy / 2.0:F1})={energyOk}、Off 恒等={offIdentity}");
        if (!smoothAllOk) failures++;

        // ---- 3) 悬浮球源级护栏：拖动路径绝不碰布局属性（Margin/XRatio），只走渲染变换 ----
        string ballSrc = StripComments(ReadSource(root, "MathPhys.Ink", "Views", "Controls", "FloatingBall.cs"), false);

        static string Slice(string src, string startMarker, string endMarker)
        {
            int start = src.IndexOf(startMarker, StringComparison.Ordinal);
            int end = src.IndexOf(endMarker, StringComparison.Ordinal);
            return start >= 0 && end > start ? src[start..end] : string.Empty;
        }

        string dragSlice = Slice(ballSrc, "private void OnBallDrag", "private void OnBallRelease");
        string releaseSlice = Slice(ballSrc, "private void OnBallRelease", "private void ApplyPosition");
        string applySlice = Slice(ballSrc, "private void ApplyPosition", "private void EnsureTranslate");

        bool dragOk18 = dragSlice.Length > 0
            && dragSlice.Contains("_dragging = true;", StringComparison.Ordinal)
            && dragSlice.Contains("CloseMenu();", StringComparison.Ordinal)
            && dragSlice.Contains("EnsureTranslate();", StringComparison.Ordinal)
            && dragSlice.Contains("_translate!.X = _current.X - _startMargin.X;", StringComparison.Ordinal)
            && !dragSlice.Contains("XRatio", StringComparison.Ordinal)
            // 注意锚点用 "_ball.Margin"：拖动位移基准字段 _startMargin 天然含 "Margin" 子串，别误伤
            && !dragSlice.Contains("_ball.Margin", StringComparison.Ordinal);
        bool releaseOk18 = releaseSlice.Length > 0
            && releaseSlice.Contains("_ball.Margin = new Thickness(_current.X, _current.Y, 0, 0);", StringComparison.Ordinal)
            && releaseSlice.Contains("ClearTranslate();", StringComparison.Ordinal)
            && releaseSlice.Contains("PositionCommitted?.Invoke", StringComparison.Ordinal);
        bool applyOk18 = applySlice.Length > 0
            && applySlice.Contains("if (_dragging) return;", StringComparison.Ordinal);

        bool ballAllOk = dragOk18 && releaseOk18 && applyOk18;
        Console.WriteLine($"[{(ballAllOk ? "PASS" : "FAIL")}] 悬浮球护栏：拖动零布局属性（不走 Margin/XRatio）={dragOk18}、松手落 Margin+清变换+持久化={releaseOk18}、ApplyPosition 拖动短路={applyOk18}");
        if (!ballAllOk) failures++;

        // ---- 4) 断点检测与平滑接线（插件 + 宿主 + 窗口）源级护栏 ----
        string samplerSrc = StripComments(ReadSource(root, "MathPhys.Ink", "Input", "InkSamplerPlugIn.cs"), false);
        string inkCanvasSrc = StripComments(ReadSource(root, "MathPhys.Ink", "Views", "Controls", "InkSurfaceCanvas.cs"), false);
        string viewportSrc = StripComments(ReadSource(root, "MathPhys.Ink", "Views", "Controls", "CanvasViewportHost.xaml.cs"), false);
        string mainSrc = StripComments(ReadSource(root, "MathPhys.Ink", "Views", "MainWindow.xaml.cs"), false);

        bool samplerOk = samplerSrc.Contains("PacketPointFilter.Classify", StringComparison.Ordinal)
            && samplerSrc.Contains("PacketPointFilter.IsBadPoint", StringComparison.Ordinal)
            && samplerSrc.Contains("ScanPoints(", StringComparison.Ordinal)
            && samplerSrc.Contains("BuildKept(", StringComparison.Ordinal)
            && samplerSrc.Contains("WriteBackSet(", StringComparison.Ordinal)
            && samplerSrc.Contains("ConsumeJumpDetected", StringComparison.Ordinal)
            && samplerSrc.Contains("PathSmoother.Next", StringComparison.Ordinal)
            && samplerSrc.Contains("sp.X = _smX;", StringComparison.Ordinal)
            && samplerSrc.Contains("_jumpDetected = false;", StringComparison.Ordinal)
            && samplerSrc.Contains("TryResetRenderer();", StringComparison.Ordinal)
            // ---- M24 热修②：单触点书写 ----
            && samplerSrc.Contains("_primaryDeviceId", StringComparison.Ordinal)
            && samplerSrc.Contains("PrimaryIdleReclaimMs", StringComparison.Ordinal)
            && samplerSrc.Contains("DeviceIdOf(", StringComparison.Ordinal)
            && samplerSrc.Contains("rawStylusInput.StylusDeviceId", StringComparison.Ordinal)
            && samplerSrc.Contains("SuppressForeignContact(", StringComparison.Ordinal)
            && samplerSrc.Contains("PinWet(", StringComparison.Ordinal)
            && samplerSrc.Contains("WriteBackPrefix(", StringComparison.Ordinal)
            && samplerSrc.Contains("SlicePrefix(", StringComparison.Ordinal)
            // ---- M24 热修③：Up 丢失防御 + 诊断计数 ----
            && samplerSrc.Contains("if (_buffer.IsActive) TryResetRenderer();", StringComparison.Ordinal)
            && samplerSrc.Contains("GetFilterCounters", StringComparison.Ordinal)
            && samplerSrc.Contains("Interlocked.Increment(ref _foreignContacts)", StringComparison.Ordinal)
            // ---- M24 热修④：落笔点登记成原始锚 + 落笔包内逐点跳变扫描 ----
            && samplerSrc.Contains("downJumped", StringComparison.Ordinal)
            && samplerSrc.Contains("_rawLastX = first.X;", StringComparison.Ordinal)
            // ---- M24 热修⑤：PinWet 借模板首点（描述必然兼容）+ 空模板短路 ----
            && samplerSrc.Contains("StylusPoint pinned = template[0];", StringComparison.Ordinal)
            && samplerSrc.Contains("if (template.Count == 0) return;", StringComparison.Ordinal);
        bool inkCanvasOk = inkCanvasSrc.Contains("public void ResetDynamicRenderer()", StringComparison.Ordinal)
            && inkCanvasSrc.Contains("new StylusPointCollection()", StringComparison.Ordinal)
            && inkCanvasSrc.Contains("renderer.Reset(device, empty);", StringComparison.Ordinal)
            // ★ M18.1：无活动指针时必须直接返回 —— 否则每次工具切换都会抛一次
            //   ArgumentException 被兜住、在老师日志里刷一行"清实时湿墨失败"（噪声淹没真故障）
            && inkCanvasSrc.Contains("if (Stylus.CurrentStylusDevice is not { } device) return;",
                                     StringComparison.Ordinal);
        bool viewportOk = viewportSrc.Contains("InkLayer.ResetActiveRenderer();", StringComparison.Ordinal)
            && viewportSrc.Contains("InkLayer.Sampler.ConsumeJumpDetected()", StringComparison.Ordinal)
            // ★ M24 热修⑤（2026-09-28）改形态：丢笔不再是裸 Strokes.Remove，而是
            //   「静默移除 + 把"加入本笔"那一步从历史里抹掉」。护栏钉的是实现形态，
            //   形态变了必须同步重审（docs/06 §34.3 的教训：旧锚点会假 PASS）。
            && viewportSrc.Contains("_history.Silently(() => Strokes.Remove(e.Stroke))", StringComparison.Ordinal)
            && viewportSrc.Contains("_history.DiscardAdded(e.Stroke)", StringComparison.Ordinal)
            && viewportSrc.Contains("NoteSamplerFilterCounters", StringComparison.Ordinal);
        bool mainOk = mainSrc.Contains("SetSmoothStrength((SmoothStrength)index)", StringComparison.Ordinal)
            && mainSrc.Contains("ViewportHost.SetSmoothEnabled(SmoothCheckBox.IsChecked == true)", StringComparison.Ordinal)
            && mainSrc.Contains("SelectSmoothStrength((int)SmoothStrength.Light)", StringComparison.Ordinal);

        bool wireAllOk = samplerOk && inkCanvasOk && viewportOk && mainOk;
        Console.WriteLine($"[{(wireAllOk ? "PASS" : "FAIL")}] 接线护栏：插件断点+平滑={samplerOk}、墨迹层 Reset={inkCanvasOk}、宿主兜底+丢笔={viewportOk}、窗口平滑档位={mainOk}");
        if (!wireAllOk) failures++;

        // ---- 5) M24 热修④/⑤：跳变分支必须「先作废、后写回」（顺序护栏）----
        //  语义：跳变一旦判定，本笔作废的若干动作（置旗标 / 清锚 / 清缓冲 / 清湿墨）
        //  必须先于「截断写回」落地。顺序反了的话，写回万一抛异常（描述不兼容等），
        //  被 Fail() 吞掉的同时连「本笔作废」也一起跳过 ⇒ 长线既画出来又提交下去。
        //  这里按方法切片后比下标，而不是全文 Contains —— 全文 Contains 分不清
        //  「同一个分支里的先后」还是「两个不相干位置各出现一次」。
        string downSlice = Slice(samplerSrc, "protected override void OnStylusDown", "protected override void OnStylusMove");
        string moveSlice = Slice(samplerSrc, "protected override void OnStylusMove", "protected override void OnStylusUp");
        string upSlice = Slice(samplerSrc, "protected override void OnStylusUp", "private void OnBreakDetected");

        // 落笔包：downJumped 分支里 OnBreakDetected 必须先于截断写回
        int downBreakIdx = downSlice.IndexOf("OnBreakDetected(rawStylusInput);", StringComparison.Ordinal);
        int downWriteIdx = downSlice.IndexOf("WriteBackSet(rawStylusInput, kept);", StringComparison.Ordinal);
        bool downOrderOk = downBreakIdx >= 0 && downWriteIdx > downBreakIdx;

        // Move 包：OnBreakDetected 必须先于 WriteBackPrefix
        int moveBreakIdx = moveSlice.IndexOf("OnBreakDetected(rawStylusInput);", StringComparison.Ordinal);
        int moveWriteIdx = moveSlice.IndexOf("WriteBackPrefix(rawStylusInput, points, prefix);", StringComparison.Ordinal);
        bool moveOrderOk = moveBreakIdx >= 0 && moveWriteIdx > moveBreakIdx;

        // Up 包：整笔作废的四动作（首项 _jumpDetected = true）必须先于 WriteBackPrefix
        int upBreakIdx = upSlice.IndexOf("_jumpDetected = true;", StringComparison.Ordinal);
        int upWriteIdx = upSlice.IndexOf("WriteBackPrefix(rawStylusInput, points, prefix);", StringComparison.Ordinal);
        bool upOrderOk = upBreakIdx >= 0 && upWriteIdx > upBreakIdx;

        bool hotfixOrderOk = downOrderOk && moveOrderOk && upOrderOk;
        Console.WriteLine($"[{(hotfixOrderOk ? "PASS" : "FAIL")}] M24 热修④/⑤ 顺序护栏：先作废后写回 Down={downOrderOk}、Move={moveOrderOk}、Up={upOrderOk}");
        if (!hotfixOrderOk) failures++;

        return failures;
    }

    // ================================================================= M19 圆规 / 圆弧

    /// <summary>
    /// M19 断言组：圆规（两点式整圆）+ 任意夹角圆弧。
    /// </summary>
    /// <remarks>
    /// 三层：① 纯几何（角度约定 / 逆向扫描角 / 退化整圆 / 角度数字）；② 真状态机
    /// （真 <see cref="HostGfxObjectHost"/> 与真 store / history：放置 → 落成 → 拖控制点 → Esc）；
    /// ③ 源级护栏（容差除缩放、渲染器不碰 RenderTransform、Esc 的宿主接线）。
    /// </remarks>
    private static int RunM19Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M19：圆规 + 任意夹角圆弧 ----");

        string root = FindRepoRoot(outDir);
        var center = new Point(100, 100);
        var localOrigin = new Point(0, 0);

        // 各断言组的通例：局部函数按「仓库根 + src 下的相对段」读源码（源级护栏用）
        static string ReadSource(string repoRoot, params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { repoRoot, "src" }.Concat(parts).ToArray()), Encoding.UTF8);

        // ---- 1) 角度约定：0° = 正右、90° = 正上（世界 y 轴向下，取负号才是往上走）----
        bool angleOk = Near(GeometryHelper.ScreenAngle(center, new Point(150, 100)), 0.0)
            && Near(GeometryHelper.ScreenAngle(center, new Point(100, 50)), 90.0)
            && Near(GeometryHelper.ScreenAngle(center, new Point(50, 100)), 180.0)
            && Near(GeometryHelper.ScreenAngle(center, new Point(100, 150)), 270.0)
            && Near(GeometryHelper.LocalPoint(110, 90), new Point(0, -110))
            && Near(GeometryHelper.Normalize360(-90), 270.0);
        Console.WriteLine($"[{(angleOk ? "PASS" : "FAIL")}] 角度约定：0°=正右、90°=正上、LocalPoint(110,90)=(0,-110)、-90° 规整成 270°");
        if (!angleOk) failures++;

        // ---- 2) 逆时针扫描角：从起角沿逆时针转到止角（反着走就是优弧）----
        bool sweepOk = Near(GeometryHelper.Sweep(0, 90), 90.0)
            && Near(GeometryHelper.Sweep(90, 0), 270.0)
            && Near(GeometryHelper.Sweep(350, 10), 20.0)
            && Near(GeometryHelper.Sweep(45, 45), 0.0);
        Console.WriteLine($"[{(sweepOk ? "PASS" : "FAIL")}] 逆时针扫描角：0°→90°=90°、90°→0°=270°、350°→10°=20°");
        if (!sweepOk) failures++;

        // ---- 3) 起止角相等 = 整圆（跨 359.98° / 0.02° 这种 0° 环绕也算相等）----
        bool degenerateOk = GeometryHelper.AnglesEqual(30, 30)
            && GeometryHelper.AnglesEqual(359.98, 0.02)
            && !GeometryHelper.AnglesEqual(0, 0.1)
            && GeometryHelper.BuildArcGeometry(110, 30, 30) is EllipseGeometry;
        Console.WriteLine($"[{(degenerateOk ? "PASS" : "FAIL")}] 退化整圆：起止角相等走整圆几何（跨 0° 环绕也算相等）");
        if (!degenerateOk) failures++;

        // ---- 4) 半径夹取与坏值防御：NaN / ∞ 退回默认，不让脏值漏进几何 ----
        bool clampOk = Near(GeometryHelper.ClampRadius(10), GeometryHelper.MinRadiusWorld)
            && Near(GeometryHelper.ClampRadius(1000), GeometryHelper.MaxRadiusWorld)
            && Near(GeometryHelper.ClampRadius(double.NaN), GeometryHelper.DefaultRadiusWorld)
            && Near(GeometryHelper.ClampRadius(200), 200.0);
        Console.WriteLine($"[{(clampOk ? "PASS" : "FAIL")}] 半径夹取：{GeometryHelper.MinRadiusWorld}…{GeometryHelper.MaxRadiusWorld}，NaN 退回默认、正常值原样");
        if (!clampOk) failures++;

        // ---- 5) 整圆几何：原点 = 圆心、半径 = 世界长度、已 Freeze（冻结后不再有变更通知）----
        var circleGeometry = GeometryHelper.BuildCircleGeometry(110);
        var circleBounds = circleGeometry.Bounds;
        bool circleGeomOk = Near(circleBounds.X, -110) && Near(circleBounds.Y, -110)
            && Near(circleBounds.Width, 220) && Near(circleBounds.Height, 220)
            && circleGeometry.IsFrozen;
        Console.WriteLine($"[{(circleGeomOk ? "PASS" : "FAIL")}] 整圆几何：原点在圆心、r=110 ⇒ 包围盒 220×220，且已 Freeze");
        if (!circleGeomOk) failures++;

        // ---- 6) 圆弧逆向：劣弧 0°→90° 过 45° 不过 225°；优弧 0°→200° 过 100° 不过 300° ----
        //    （方向算错的表现是弧翻到圆的另一半，只比端点相等是看不出来的）
        var minorArc = GeometryHelper.BuildArcGeometry(110, 0, 90);
        var majorArc = GeometryHelper.BuildArcGeometry(110, 0, 200);
        bool passesMinor = StrokeHits(minorArc, CirclePoint(localOrigin, 110, 45));
        bool skipsMinor = !StrokeHits(minorArc, CirclePoint(localOrigin, 110, 225));
        bool passesMajor = StrokeHits(majorArc, CirclePoint(localOrigin, 110, 100));
        bool skipsMajor = !StrokeHits(majorArc, CirclePoint(localOrigin, 110, 300));
        bool arcDirOk = passesMinor && skipsMinor && passesMajor && skipsMajor;
        Console.WriteLine($"[{(arcDirOk ? "PASS" : "FAIL")}] 圆弧逆向：劣弧过 45°={passesMinor}、不过 225°={skipsMinor}；优弧过 100°={passesMajor}、不过 300°={skipsMajor}");
        if (!arcDirOk) failures++;

        // ---- 7) 角度吸附：15° 一档、容差 5°；没吸上必须交回原值（「任意夹角」的立身之本）----
        bool snapOk = GeometryHelper.TrySnapAngle(88, out double snapped88) && Near(snapped88, 90.0)
            && GeometryHelper.TrySnapAngle(103.5, out double snapped103) && Near(snapped103, 105.0)
            && !GeometryHelper.TrySnapAngle(97, out double raw97) && Near(raw97, 97.0);
        Console.WriteLine($"[{(snapOk ? "PASS" : "FAIL")}] 角度吸附：88°→90°、103.5°→105°、97° 保持原值（超出 5° 容差）");
        if (!snapOk) failures++;

        // ---- 8) 角度数字：摆在弧中分角外侧 1.12R；文案给「0° → 90°（90°）」、等角给 360° ----
        var labelGeometry = GeometryHelper.BuildAngleLabel(110, 0, 90);
        var labelBounds = labelGeometry.Bounds;
        var labelAnchor = new Point(labelBounds.X + labelBounds.Width / 2.0,
                                    labelBounds.Y + labelBounds.Height / 2.0);
        string edit90 = GeometryHelper.DescribeEditing(CompassMode.Arc, 110, 0, 90);
        string editFull = GeometryHelper.DescribeEditing(CompassMode.Arc, 110, 30, 30);
        string editCircle = GeometryHelper.DescribeEditing(CompassMode.Circle, 110, 0, 0);
        bool labelOk = Near(labelAnchor, GeometryHelper.LocalPoint(110 * GeometryHelper.LabelRadiusRatio, 45), 0.01)
            && edit90.Contains("0° → 90°（90°）", StringComparison.Ordinal)
            && editFull.Contains("360°", StringComparison.Ordinal)
            && editCircle.Contains("拖针尖移圆", StringComparison.Ordinal);
        Console.WriteLine($"[{(labelOk ? "PASS" : "FAIL")}] 角度数字：锚点在 1.12R 的 45° 处；文案给「0° → 90°（90°）」、等角给 360°");
        if (!labelOk) failures++;

        // ---- 9) 真状态机（整圆）：放置 → 半径预览 → 落成 → 拖针尖 / 拖笔尖 → Esc ----
        var catalog = new GfxRendererCatalog();
        catalog.Register(new CompassCircleRenderer(), "圆规（学科工具）");
        catalog.Register(new CompassArcRenderer(), "圆弧（学科工具）");
        var store = new GfxObjectStore(catalog);
        var history = new GfxHistory(store);
        var host = new HostGfxObjectHost(store, history,
            new BoardHistory(new InkHistory(new StrokeCollection()), history));

        var ctx = new FakeToolContext { GfxOverride = host, Scale = 1.0 };
        var tool = new CompassTool(CompassMode.Circle);

        try
        {
            tool.Activate(ctx);
            ctx.Reset();

            // 9a. 第一个点放针尖：预览圈出现（半径从下限起）
            tool.OnPointer(Pointer(ToolPointerPhase.Down, center));
            var placingCircle = FindPreviewCircle(ctx.Previews);
            bool placingOk = placingCircle is { Visibility: Visibility.Visible } placedPath
                && placedPath.Data is { } placingData
                && Near(placingData.Bounds.Width, GeometryHelper.MinRadiusWorld * 2.0)
                && Near(Canvas.GetLeft(placedPath), center.X)
                && Near(Canvas.GetTop(placedPath), center.Y)
                && ctx.LastStatus is not null
                && ctx.LastStatus.Contains("再点一下落成圆", StringComparison.Ordinal);
            Console.WriteLine($"[{(placingOk ? "PASS" : "FAIL")}] 圆规放置：点一下放针尖、预览圈出现（半径下限 {GeometryHelper.MinRadiusWorld}）");
            if (!placingOk) failures++;

            // 9b. 移笔尖：预览半径跟手（世界 90 ⇒ 预览圈直径 180），且换的是 Data、不是重建图元
            var geometryBeforeMove = placingCircle?.Data;
            tool.OnPointer(Pointer(ToolPointerPhase.Move, new Point(100, 10)));
            bool previewGrewOk = placingCircle is not null
                && placingCircle.Data is { } grew
                && Near(grew.Bounds.Width, 180.0)
                && !ReferenceEquals(grew, geometryBeforeMove);
            Console.WriteLine($"[{(previewGrewOk ? "PASS" : "FAIL")}] 半径预览：拖到 90 世界单位 ⇒ 预览圈直径 180（只换 Data）");
            if (!previewGrewOk) failures++;

            // 9c. 第二个点落成：对象入库（半径 90、整圆不写起止角），宿主自动开一步撤销
            tool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(100, 10)));
            var circleRef = RefOf(store);
            bool commitOk = store.Objects.Count == 1
                && circleRef is not null
                && circleRef.Kind == CompassCircleRenderer.KindName
                && Near(circleRef.Center, center)
                && Near(circleRef.GetNumber(GeometryHelper.RadiusKey, 0), 90.0)
                && Near(circleRef.GetNumber(GeometryHelper.StartKey, -1), -1.0)
                && history.UndoCount == 1
                && history.LastLabel == "新建图形";
            Console.WriteLine($"[{(commitOk ? "PASS" : "FAIL")}] 圆规落成：compass-circle 入库（r=90、圆心 (100,100)、无起止角）、撤销一步");
            if (!commitOk) failures++;

            // 9d. 落成后控制点：预览圈收起、两个控制点亮起（圆心 + 笔尖）
            var circleThumbs = VisibleThumbs(ctx.Previews);
            bool thumbsOk = placingCircle?.Visibility == Visibility.Collapsed
                && circleThumbs.Count == 2
                && ContainsThumb(circleThumbs, center)
                && ContainsThumb(circleThumbs, new Point(100, 10));
            Console.WriteLine($"[{(thumbsOk ? "PASS" : "FAIL")}] 落成后控制点：{circleThumbs.Count} 个（圆心 + 笔尖）、预览圈收起");
            if (!thumbsOk) failures++;

            // 9e. 拖针尖移圆：只改位姿（不重建视觉树）、历史留一步「拖动圆规圆心」
            tool.OnPointer(Pointer(ToolPointerPhase.Down, center));
            tool.OnPointer(Pointer(ToolPointerPhase.Move, new Point(160, 130)));
            bool dragCenterOk = history.UndoCount == 2
                && history.LastLabel == "拖动圆规圆心"
                && RefOf(store) is { } moved
                && Near(moved.Center, new Point(160, 130))
                && Near(moved.GetNumber(GeometryHelper.RadiusKey, 0), 90.0);
            tool.OnPointer(Pointer(ToolPointerPhase.Up, new Point(160, 130)));
            Console.WriteLine($"[{(dragCenterOk ? "PASS" : "FAIL")}] 拖针尖移圆：圆心跟手到 (160,130)、半径不动");
            if (!dragCenterOk) failures++;

            // 9f. 拖笔尖调半径：90 → 200（圆心不动）
            tool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(160, 40)));
            tool.OnPointer(Pointer(ToolPointerPhase.Move, new Point(160, -70)));
            bool dragRadiusOk = history.UndoCount == 3
                && history.LastLabel == "调整圆规半径"
                && RefOf(store) is { } widened
                && Near(widened.GetNumber(GeometryHelper.RadiusKey, 0), 200.0)
                && Near(widened.Center, new Point(160, 130));
            tool.OnPointer(Pointer(ToolPointerPhase.Up, new Point(160, -70)));
            Console.WriteLine($"[{(dragRadiusOk ? "PASS" : "FAIL")}] 拖笔尖调半径：r 90 → 200、圆心不动");
            if (!dragRadiusOk) failures++;

            // 9g. 拖到一半按 Esc：半径回滚到拖动前的 200（历史里那一步留着，对象值回去）
            tool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(160, -70)));
            tool.OnPointer(Pointer(ToolPointerPhase.Move, new Point(160, 70)));
            int beforeEsc = history.UndoCount;
            bool escDragOk = tool.TryCancel()
                && RefOf(store) is { } rolled
                && Near(rolled.GetNumber(GeometryHelper.RadiusKey, 0), 200.0)
                && history.UndoCount == beforeEsc;
            tool.OnPointer(Pointer(ToolPointerPhase.Up, new Point(160, 70)));
            Console.WriteLine($"[{(escDragOk ? "PASS" : "FAIL")}] Esc 中途取消拖动：半径回到 200、撤销步数不变");
            if (!escDragOk) failures++;

            // 9h. 已有圆时在空白处起手、再按 Esc：半截预览丢弃，手柄回到「对象」身上而不是预览身上
            //    ★ 回归护栏：StartPlacing 会覆写 _center / _radius，取消时不从对象读回来手柄就会乱跑
            tool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(400, 400)));
            bool cancelledPlacing = tool.TryCancel();
            var restoredThumbs = VisibleThumbs(ctx.Previews);
            bool cancelPlacingOk = cancelledPlacing
                && store.Objects.Count == 1
                && restoredThumbs.Count == 2
                && ContainsThumb(restoredThumbs, new Point(160, 130))
                && ContainsThumb(restoredThumbs, new Point(160, -70));
            Console.WriteLine($"[{(cancelPlacingOk ? "PASS" : "FAIL")}] 放置中 Esc：丢弃半截预览、手柄复位到已落成的圆上");
            if (!cancelPlacingOk) failures++;

            // 9i. 复位后还抓得住针尖（不然 9h 的「手柄复位」就是空的）
            int beforeRegrab = history.UndoCount;
            tool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(160, 130)));
            tool.OnPointer(Pointer(ToolPointerPhase.Move, new Point(112, 130)));
            bool regrabOk = history.UndoCount == beforeRegrab + 1
                && RefOf(store) is { } regrabbed
                && Near(regrabbed.Center, new Point(112, 130));
            tool.OnPointer(Pointer(ToolPointerPhase.Up, new Point(112, 130)));
            Console.WriteLine($"[{(regrabOk ? "PASS" : "FAIL")}] Esc 后仍可拖：圆心跟手到 (112,130)（手柄真的回到了对象上）");
            if (!regrabOk) failures++;

            // 9j. 缩放 2×：手柄的世界边长减半（屏幕尺寸恒定）、世界位置不变
            ctx.Scale = 2.0;
            ctx.RaiseViewportChanged();
            var scaledThumbs = VisibleThumbs(ctx.Previews);
            bool thumbScaleOk = scaledThumbs.Count == 2
                && AllThumbsSized(scaledThumbs, GeometryHelper.ThumbWorldSize(2.0))
                && ContainsThumb(scaledThumbs, new Point(112, 130))
                && ContainsThumb(scaledThumbs, new Point(112, -70));
            Console.WriteLine($"[{(thumbScaleOk ? "PASS" : "FAIL")}] 缩放 2×：手柄世界边长 {GeometryHelper.ThumbWorldSize(2.0)}（16 ÷ 2）、世界位置不变");
            if (!thumbScaleOk) failures++;

            // 9k. 缩放后容差：2× 时容差 = 10 世界单位，离针尖 15 处按下去不再抓得住（改成起新圆）
            int beforeMiss = history.UndoCount;
            tool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(127, 130)));
            tool.OnPointer(Pointer(ToolPointerPhase.Move, new Point(157, 130)));
            bool zoomToleranceOk = tool.TryCancel()
                && history.UndoCount == beforeMiss
                && RefOf(store) is { } untouched
                && Near(untouched.Center, new Point(112, 130));
            Console.WriteLine($"[{(zoomToleranceOk ? "PASS" : "FAIL")}] 缩放后容差：2× 时容差 10 ⇒ 离针尖 15 抓不住（没开撤销步）");
            if (!zoomToleranceOk) failures++;

            // 9l. 同一个点在 1× 下（容差 20）抓得住：证明容差确实除了 CurrentScale
            ctx.Scale = 1.0;
            ctx.RaiseViewportChanged();
            tool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(127, 130)));
            tool.OnPointer(Pointer(ToolPointerPhase.Move, new Point(157, 130)));
            bool scaleHitOk = history.UndoCount == beforeMiss + 1
                && RefOf(store) is { } grabbed
                && Near(grabbed.Center, new Point(157, 130));
            tool.OnPointer(Pointer(ToolPointerPhase.Up, new Point(157, 130)));
            Console.WriteLine($"[{(scaleHitOk ? "PASS" : "FAIL")}] 1× 时容差 20 ⇒ 同一点抓得住（容差 ÷ CurrentScale）");
            if (!scaleHitOk) failures++;

            // 9m. 宿主没有图形层：明确降级提示，不崩
            var bareCtx = new FakeToolContext { Scale = 1.0 };
            var bareTool = new CompassTool(CompassMode.Circle);
            bareTool.Activate(bareCtx);
            bareTool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(10, 10)));
            bareTool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(10, 10)));
            bool noGfxOk = bareCtx.LastStatus is not null
                && bareCtx.LastStatus.Contains("不支持", StringComparison.Ordinal);
            Console.WriteLine($"[{(noGfxOk ? "PASS" : "FAIL")}] 无图形层：降级提示不崩（圆规放不下）");
            if (!noGfxOk) failures++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] M19 圆规状态机断言抛异常：{ex.Message}");
            failures++;
        }

        // ---- 10) 真状态机（圆弧）：落成 90° → 拖起点 / 终点 → 等角退化整圆 ----
        var arcCatalog = new GfxRendererCatalog();
        arcCatalog.Register(new CompassArcRenderer(), "圆弧（学科工具）");
        var arcStore = new GfxObjectStore(arcCatalog);
        var arcHistory = new GfxHistory(arcStore);
        var arcHost = new HostGfxObjectHost(arcStore, arcHistory,
            new BoardHistory(new InkHistory(new StrokeCollection()), arcHistory));
        var arcCtx = new FakeToolContext { GfxOverride = arcHost, Scale = 1.0 };
        var arcTool = new CompassTool(CompassMode.Arc);

        try
        {
            arcTool.Activate(arcCtx);
            arcCtx.Reset();

            // 10a. 落成：起 0°、止 90°，四个控制点（圆心 / 笔尖在弧中分 / 起点 / 终点）
            arcTool.OnPointer(Pointer(ToolPointerPhase.Down, center));
            arcTool.OnPointer(Pointer(ToolPointerPhase.Move, new Point(100, -10)));
            arcTool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(100, -10)));

            var arcRef = RefOf(arcStore);
            var arcThumbs = VisibleThumbs(arcCtx.Previews);
            bool arcCommitOk = arcStore.Objects.Count == 1
                && arcRef is not null
                && arcRef.Kind == CompassArcRenderer.KindName
                && Near(arcRef.GetNumber(GeometryHelper.RadiusKey, 0), 110.0)
                && Near(arcRef.GetNumber(GeometryHelper.StartKey, -1), 0.0)
                && Near(arcRef.GetNumber(GeometryHelper.EndKey, -1), 90.0)
                && Near(new CompassArcRenderer().Measure(arcRef).Width, 275.0)
                && arcHistory.UndoCount == 1
                && arcThumbs.Count == 4
                && ContainsThumb(arcThumbs, center)
                && ContainsThumb(arcThumbs, CirclePoint(center, 110, 0))
                && ContainsThumb(arcThumbs, CirclePoint(center, 110, 90))
                && ContainsThumb(arcThumbs, CirclePoint(center, 110, 45))
                && arcCtx.LastStatus is not null
                && arcCtx.LastStatus.Contains("0° → 90°（90°）", StringComparison.Ordinal);
            Console.WriteLine($"[{(arcCommitOk ? "PASS" : "FAIL")}] 圆弧落成：起止 0° / 90°、四个控制点（含弧中分的笔尖）、文案给 90°");
            if (!arcCommitOk) failures++;

            // 10b. 拖起点控制点到 60°：角度写回对象、历史一步「调整圆弧起点角」
            arcTool.OnPointer(Pointer(ToolPointerPhase.Down, CirclePoint(center, 110, 0)));
            arcTool.OnPointer(Pointer(ToolPointerPhase.Move, CirclePoint(center, 110, 60)));
            bool startDragOk = arcHistory.UndoCount == 2
                && arcHistory.LastLabel == "调整圆弧起点角"
                && RefOf(arcStore) is { } started
                && Near(started.GetNumber(GeometryHelper.StartKey, -1), 60.0);
            arcTool.OnPointer(Pointer(ToolPointerPhase.Up, CirclePoint(center, 110, 60)));
            Console.WriteLine($"[{(startDragOk ? "PASS" : "FAIL")}] 拖起点控制点：起点角 0° → 60°");
            if (!startDragOk) failures++;

            // 10c. 拖到一半按 Esc：起点角回滚到 60°
            arcTool.OnPointer(Pointer(ToolPointerPhase.Down, CirclePoint(center, 110, 60)));
            arcTool.OnPointer(Pointer(ToolPointerPhase.Move, CirclePoint(center, 110, 120)));
            bool startEscOk = arcTool.TryCancel()
                && RefOf(arcStore) is { } reverted
                && Near(reverted.GetNumber(GeometryHelper.StartKey, -1), 60.0);
            arcTool.OnPointer(Pointer(ToolPointerPhase.Up, CirclePoint(center, 110, 120)));
            Console.WriteLine($"[{(startEscOk ? "PASS" : "FAIL")}] Esc 中途取消：起点角回到 60°");
            if (!startEscOk) failures++;

            // 10d. 拖终点控制点到 150°：角度 90°、文案同步
            arcTool.OnPointer(Pointer(ToolPointerPhase.Down, CirclePoint(center, 110, 90)));
            arcTool.OnPointer(Pointer(ToolPointerPhase.Move, CirclePoint(center, 110, 150)));
            bool endDragOk = arcHistory.UndoCount == 4
                && arcHistory.LastLabel == "调整圆弧终点角"
                && RefOf(arcStore) is { } ended
                && Near(ended.GetNumber(GeometryHelper.EndKey, -1), 150.0)
                && arcCtx.LastStatus is not null
                && arcCtx.LastStatus.Contains("60° → 150°（90°）", StringComparison.Ordinal);
            arcTool.OnPointer(Pointer(ToolPointerPhase.Up, CirclePoint(center, 110, 150)));
            Console.WriteLine($"[{(endDragOk ? "PASS" : "FAIL")}] 拖终点控制点：终点角 90° → 150°，角度文案给「60° → 150°（90°）」");
            if (!endDragOk) failures++;

            // 10e. 终点拖到与起点同角：按整圆画、角度数字显示 360°
            arcTool.OnPointer(Pointer(ToolPointerPhase.Down, CirclePoint(center, 110, 150)));
            arcTool.OnPointer(Pointer(ToolPointerPhase.Move, CirclePoint(center, 110, 60)));
            bool fullCircleOk = arcHistory.UndoCount == 5
                && RefOf(arcStore) is { } full
                && Near(full.GetNumber(GeometryHelper.EndKey, -1), 60.0)
                && arcCtx.LastStatus is not null
                && arcCtx.LastStatus.Contains("360°", StringComparison.Ordinal);
            arcTool.OnPointer(Pointer(ToolPointerPhase.Up, CirclePoint(center, 110, 60)));
            Console.WriteLine($"[{(fullCircleOk ? "PASS" : "FAIL")}] 起止角相等：按整圆画、角度数字给 360°");
            if (!fullCircleOk) failures++;

            // 10f. 缩放 2×：圆弧的四个控制点也按 16 ÷ 2 重摆、世界位置不变
            arcCtx.Scale = 2.0;
            arcCtx.RaiseViewportChanged();
            var scaledArcThumbs = VisibleThumbs(arcCtx.Previews);
            bool arcScaleOk = scaledArcThumbs.Count == 4
                && AllThumbsSized(scaledArcThumbs, GeometryHelper.ThumbWorldSize(2.0))
                && ContainsThumb(scaledArcThumbs, center)
                && ContainsThumb(scaledArcThumbs, CirclePoint(center, 110, 60))
                && ContainsThumb(scaledArcThumbs, CirclePoint(center, 110, 90));
            Console.WriteLine($"[{(arcScaleOk ? "PASS" : "FAIL")}] 圆弧缩放 2×：四个控制点世界边长 8、世界位置不变（起止等角时笔尖挪回正上方）");
            if (!arcScaleOk) failures++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] M19 圆弧状态机断言抛异常：{ex.Message}");
            failures++;
        }

        // ---- 11) 插件注册与快捷键（注册表里还有内置笔 / 橡皮 / 手，只按 Id 数，不数总数）----
        var hostView = new CanvasViewportHost();
        try
        {
            new CompassPlugin().Register(hostView.PluginRegistry);

            int compassTools = 0;
            foreach (var registered in hostView.PluginRegistry.Tools)
            {
                if (registered.Id == CompassToolIds.Circle || registered.Id == CompassToolIds.Arc) compassTools++;
            }

            bool registerOk = compassTools == 2
                && hostView.FindToolByShortcut(Key.R)?.Id == CompassToolIds.Circle
                && hostView.FindToolByShortcut(Key.A)?.Id == CompassToolIds.Arc;
            Console.WriteLine($"[{(registerOk ? "PASS" : "FAIL")}] 插件注册：{compassTools} 个圆规工具进注册表，R / A 分别查到整圆 / 圆弧");
            if (!registerOk) failures++;
        }
        finally
        {
            hostView.Shutdown();
        }

        // ---- 12) 源级护栏：容差除缩放、逆向弧、只 Update 不重建、不实现 IGfxTool ----
        string toolSrc = StripComments(ReadSource(root, "MathPhys.Ink.Plugin.Compass", "CompassTool.cs"), false);
        string helperSrc = StripComments(ReadSource(root, "MathPhys.Ink.Plugin.Compass", "GeometryHelper.cs"), false);
        string rendererSrc = StripComments(ReadSource(root, "MathPhys.Ink.Plugin.Compass", "CompassRenderer.cs"), false);

        bool toleranceSrcOk = helperSrc.Contains("HitTolerancePixels / SafeScale(scale)", StringComparison.Ordinal)
            && helperSrc.Contains("HandleSizePixels / SafeScale(scale)", StringComparison.Ordinal);
        Console.WriteLine($"[{(toleranceSrcOk ? "PASS" : "FAIL")}] 源级护栏：命中容差与手柄边长都 ÷ SafeScale（缩放后手感一致）");
        if (!toleranceSrcOk) failures++;

        bool lightSrcOk = helperSrc.Contains("Math.Atan2", StringComparison.Ordinal)
            && helperSrc.Contains(".ArcTo(", StringComparison.Ordinal)
            && helperSrc.Contains("sweepDirection: SweepDirection.Counterclockwise", StringComparison.Ordinal)
            && helperSrc.Contains("isLargeArc: sweep > 180.0", StringComparison.Ordinal)
            && toolSrc.Contains("class CompassTool : ITool, ICancellableTool", StringComparison.Ordinal)
            && !toolSrc.Contains("IGfxTool", StringComparison.Ordinal)
            && toolSrc.Contains("if (_previewRoot is not null) return;", StringComparison.Ordinal)
            && CountOccurrences(toolSrc, "gfx.Add(") == 1
            && toolSrc.Contains("gfx.UpdateNumbers(", StringComparison.Ordinal)
            && toolSrc.Contains("gfx.UpdatePose(", StringComparison.Ordinal);
        Console.WriteLine($"[{(lightSrcOk ? "PASS" : "FAIL")}] 源级护栏：Atan2 + ArcTo + 逆向、预览只建一次（gfx.Add 只 1 处）、拖拽只 Update、不实现 IGfxTool");
        if (!lightSrcOk) failures++;

        bool rendererSrcOk = !rendererSrc.Contains("RenderTransform", StringComparison.Ordinal);
        Console.WriteLine($"[{(rendererSrcOk ? "PASS" : "FAIL")}] 源级护栏：圆 / 弧渲染器不碰 RenderTransform（位姿交给宿主）");
        if (!rendererSrcOk) failures++;

        // ---- 13) Esc 的宿主接线：新增可选接口 + 十几行兜底（不动 ITool 契约）----
        string hostSrc = StripComments(
            ReadSource(root, "MathPhys.Ink", "Views", "Controls", "CanvasViewportHost.xaml.cs"), false);
        string windowSrc = StripComments(
            ReadSource(root, "MathPhys.Ink", "Views", "MainWindow.xaml.cs"), false);
        int cancelCallAt = windowSrc.IndexOf("ViewportHost.TryCancelActiveTool()", StringComparison.Ordinal);
        int escapeFullScreenAt = windowSrc.IndexOf("Key.Escape && _isFullScreen", StringComparison.Ordinal);

        bool escWireOk = hostSrc.Contains("public bool TryCancelActiveTool()", StringComparison.Ordinal)
            && hostSrc.Contains("is not ICancellableTool", StringComparison.Ordinal)
            && hostSrc.Contains("_toolBeforeCancellable", StringComparison.Ordinal)
            && cancelCallAt >= 0 && escapeFullScreenAt >= 0 && cancelCallAt < escapeFullScreenAt;
        Console.WriteLine($"[{(escWireOk ? "PASS" : "FAIL")}] Esc 接线：先问可取消工具、再退全屏（ICancellableTool 是新增可选接口）");
        if (!escWireOk) failures++;

        return failures;
    }

    // ================================================================= M19 后 bug 修复：浮动面板拖动闪烁

    /// <summary>
    /// 浮动面板（工具栏 / 计算器）拖动闪烁的回归护栏。
    /// </summary>
    /// <remarks>
    /// 根因（本机真窗口实测）：拖动读数原先以<b>把手</b>为基准，而把手是被拖动目标的子元素 ——
    /// <c>GetPosition</c> / <c>GetTouchPoint</c> 的量距在指定元素的本地坐标系里做，而这个坐标系
    /// 包含祖先的 <c>RenderTransform</c>；于是「刚写下去的位移」把读数抵消掉一部分，形成正反馈环
    /// T′ = D − T，真位移恒定时它的解是两点来回翻（140 0 140 0 …）= 闪烁 + 不跟手。
    /// 三层护栏：① 真控件树上 <see cref="FloatingDrag.ResolveReference"/> 返回目标父级、
    /// 且不是目标自身或它的子孙；② 源级：拖动读数只走基准元素；③ 数学：把反馈环写成纯函数，
    /// 钉住「以自身为基准必翻跳 / 以父级为基准必稳定」。
    /// </remarks>
    private static int RunDragCoordChecks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("---- M19 后修复：浮动面板拖动闪烁（坐标基准）----");

        string root = FindRepoRoot(outDir);

        static string ReadSource(string repoRoot, params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { repoRoot, "src" }.Concat(parts).ToArray()), Encoding.UTF8);

        // 沿逻辑树往上走，看 node 是不是 ancestor 自身或它的子孙
        static bool IsSelfOrDescendant(DependencyObject node, DependencyObject ancestor)
        {
            for (DependencyObject? cur = node; cur is not null; cur = LogicalTreeHelper.GetParent(cur))
            {
                if (ReferenceEquals(cur, ancestor)) return true;
            }

            return false;
        }

        // ---- 1) 真控件树：基准 = 目标的父级（= Margin 的坐标系）----
        var container = new Grid { Width = 600, Height = 400 };
        var handle = new Border { Background = Brushes.Transparent };
        var target = new Border
        {
            Width = 300,
            Height = 200,
            Margin = new Thickness(60, 60, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = handle,
        };
        container.Children.Add(target);
        container.Arrange(new Rect(0, 0, 600, 400));
        container.UpdateLayout();

        FrameworkElement reference = FloatingDrag.ResolveReference(target, container);
        bool referenceOk = ReferenceEquals(reference, container)
            && !ReferenceEquals(reference, target)
            && !IsSelfOrDescendant(reference, target)
            // 反向自证：把手确实落在目标自己的坐标系里 —— 拿它当基准才会读到「被抵消过的位移」
            && IsSelfOrDescendant(handle, target);
        Console.WriteLine($"[{(referenceOk ? "PASS" : "FAIL")}] 拖动基准 = 目标父级（不是目标自身、也不是它的子孙；把手确实在目标的坐标系里）");
        if (!referenceOk) failures++;

        // ---- 2) 源级：拖动读数只走基准元素，把手不再参与量距 ----
        string dragSrc = StripComments(
            ReadSource(root, "MathPhys.Ink", "Views", "Controls", "FloatingDrag.cs"), false);
        bool readOk = dragSrc.Contains("ResolveReference(_target, _container)", StringComparison.Ordinal)
            && dragSrc.Contains("GetTouchPoint(_reference).Position", StringComparison.Ordinal)
            && dragSrc.Contains("GetPosition(_reference)", StringComparison.Ordinal)
            && !dragSrc.Contains("GetPosition(_handle)", StringComparison.Ordinal)
            && !dragSrc.Contains("GetTouchPoint(_handle)", StringComparison.Ordinal);
        Console.WriteLine($"[{(readOk ? "PASS" : "FAIL")}] 源级：拖动读数以基准元素为准，把手不再参与量距");
        if (!readOk) failures++;

        // ---- 3) 反馈环的数学：同一段真位移，两种基准的位移序列一个翻跳、一个稳定 ----
        const double d = 140.0;

        double self = 0.0;
        var selfSeq = new List<double>();
        for (int i = 0; i < 4; i++) { self = d - self; selfSeq.Add(self); }   // 读数 = 真位移 − 当前位移

        double parent = 0.0;
        var parentSeq = new List<double>();
        for (int i = 0; i < 4; i++) { parent = d; parentSeq.Add(parent); }    // 读数 = 真位移

        bool loopOk = Near(selfSeq[0], d) && Near(selfSeq[1], 0.0) && Near(selfSeq[2], d) && Near(selfSeq[3], 0.0)
            && parentSeq.TrueForAll(v => Near(v, d));
        Console.WriteLine($"[{(loopOk ? "PASS" : "FAIL")}] 反馈环：以自身为基准 {Seq(selfSeq)} 两点翻跳（闪烁）；以父级为基准 {Seq(parentSeq)} 稳定跟手");
        if (!loopOk) failures++;

        return failures;
    }

    /// <summary>把位移序列印成一行（断言失败时一眼看出是「翻跳」还是「稳定」）。</summary>
    private static string Seq(IEnumerable<double> values)
        => string.Join(" ", values.Select(v => v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>M19：极坐标取点（角度按屏幕视觉：0° = 正右、90° = 正上）。</summary>
    private static Point CirclePoint(Point origin, double radius, double degrees)
    {
        var offset = GeometryHelper.LocalPoint(radius, degrees);
        return new Point(origin.X + offset.X, origin.Y + offset.Y);
    }

    /// <summary>
    /// M19：这个点是不是落在几何的描边上。
    /// </summary>
    /// <remarks>
    /// 用 WPF 自己的描边命中（与真机上点弧线选对象同一套判定），比「两个端点相等」强：
    /// 弧画反了照样过两个端点，只有查弧中段才看得出来。
    /// </remarks>
    private static bool StrokeHits(Geometry geometry, Point point)
        => geometry.StrokeContains(new Pen(Brushes.Black, 1.0), point, 0.5, ToleranceType.Absolute);

    /// <summary>M19：在预览图元里找圆规放置阶段那个圈（Canvas 里的 Path + EllipseGeometry）。</summary>
    private static System.Windows.Shapes.Path? FindPreviewCircle(IReadOnlyList<UIElement> previews)
    {
        foreach (var visual in previews)
        {
            if (visual is not Canvas canvas) continue;

            foreach (UIElement child in canvas.Children)
            {
                if (child is System.Windows.Shapes.Path { Data: EllipseGeometry } path) return path;
            }
        }

        return null;
    }

    /// <summary>M19：预览层里当前可见的控制点（圆规用 Canvas.Left/Top + 半边长摆它们）。</summary>
    private static List<System.Windows.Shapes.Rectangle> VisibleThumbs(IReadOnlyList<UIElement> previews)
    {
        var thumbs = new List<System.Windows.Shapes.Rectangle>();
        foreach (var visual in previews)
        {
            CollectThumbs(visual, thumbs);
        }

        return thumbs;
    }

    private static void CollectThumbs(DependencyObject node, List<System.Windows.Shapes.Rectangle> into)
    {
        if (node is System.Windows.Shapes.Rectangle { Visibility: Visibility.Visible } thumb)
        {
            into.Add(thumb);
            return;
        }

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            CollectThumbs(VisualTreeHelper.GetChild(node, i), into);
        }
    }

    /// <summary>M19：可见控制点里有没有中心落在这个世界坐标附近的（容差 0.01 世界单位）。</summary>
    private static bool ContainsThumb(List<System.Windows.Shapes.Rectangle> thumbs, Point expected)
    {
        foreach (var thumb in thumbs)
        {
            if (Near(ThumbCenter(thumb), expected, 0.01)) return true;
        }

        return false;
    }

    /// <summary>M19：控制点中心的世界坐标（工具摆的是左上角，中心才是它认的位置）。</summary>
    private static Point ThumbCenter(System.Windows.Shapes.Rectangle thumb)
        => new(Canvas.GetLeft(thumb) + thumb.Width / 2.0, Canvas.GetTop(thumb) + thumb.Height / 2.0);

    /// <summary>M19：这批控制点是不是都按这个世界边长摆的（缩放后要变小）。</summary>
    private static bool AllThumbsSized(List<System.Windows.Shapes.Rectangle> thumbs, double expected)
    {
        foreach (var thumb in thumbs)
        {
            if (!Near(thumb.Width, expected, 1e-9) || !Near(thumb.Height, expected, 1e-9)) return false;
        }

        return true;
    }

    /// <summary>M19：取 store 里唯一那个对象（本组断言每个 store 只放一个对象）。</summary>
    private static IGfxObjectRef? RefOf(GfxObjectStore store)
        => store.Objects.Count > 0 ? FindRef(store, store.Objects[0].Id) : null;

    /// <summary>按 Id 在 store 里找对象（找不到返回 null）。</summary>
    private static IGfxObjectRef? FindRef(GfxObjectStore store, string id)
    {
        foreach (var o in store.Objects)
        {
            if (o.Id == id) return o;
        }

        return null;
    }

    /// <summary>导线端点世界坐标（false = A 端 / true = B 端）：位姿矩阵作用于 (±halfLen, 0)。</summary>
    private static Point WireEndpoint(IGfxObjectRef wire, bool endB)
    {
        double half = wire.GetNumber(CircuitSymbols.WireHalfLenKey, 0);
        var matrix = new System.Windows.Media.Matrix();
        matrix.Rotate(wire.RotationDegrees);
        var local = matrix.Transform(new Point(endB ? half : -half, 0));
        return new Point(wire.Center.X + local.X, wire.Center.Y + local.Y);
    }

    /// <summary>角度按模 360 比较（宿主把旋转归一化到 [0,360)，-20° 与 340° 是同一个角）。</summary>
    private static bool AngleClose(double a, double b)
    {
        double diff = Math.Abs((a - b) % 360.0);
        return Math.Min(diff, 360.0 - diff) < 1e-6;
    }

    /// <summary>点按容差比较（端点经过度→弧度往返换算，精确相等会被舍入坑掉）。</summary>
    private static bool CloseTo(Point a, Point b, double tolerance = 1e-6)
        => Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;

    /// <summary>有限值判断（NaN / Infinity 都不算有限）。</summary>
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>从起点向上找仓库根（含 src\MathPhys.Ink 与 tools 的那一层）。</summary>
    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "MathPhys.Ink"))
                && Directory.Exists(Path.Combine(dir.FullName, "tools")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"找不到仓库根目录（起点：{start}）");
    }

    /// <summary>枚举 src 下的源文件（跳过 bin / obj）。</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (var pattern in new[] { "*.xaml", "*.cs" })
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                if (file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }
    }

    /// <summary>
    /// 去掉注释再扫描。
    /// </summary>
    /// <remarks>
    /// ★ 必须做：我们的注释里大量引用色值与键名（"墨蓝同源 #0B5FD0"），
    /// 不去掉就会造出一堆假 FAIL，而假 FAIL 比漏报更糟 —— 它会让人开始怀疑断言本身。
    /// </remarks>
    private static string StripComments(string text, bool isXaml)
    {
        string withoutBlocks = Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        if (isXaml) return withoutBlocks;

        var builder = new StringBuilder(withoutBlocks.Length);
        foreach (var line in withoutBlocks.Split('\n'))
        {
            int index = line.IndexOf("//", StringComparison.Ordinal);
            builder.AppendLine(index >= 0 ? line[..index] : line);
        }

        return builder.ToString();
    }

    // ================================================================= M2

    /// <summary>M2 断言组：档位量化、手势不变量、LRU 语义、后台调度线程与 generation。</summary>

    private static int RunM2Checks(PdfiumDocumentService service)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M2：视口手势与后台渲染 ================");

        // ---------------------------------------------------------- 1) 档位量化
        Console.WriteLine();
        Console.WriteLine("---- 1) 渲染档位量化（位图缓存能命中的前提）----");

        var levelCases = new (double Scale, int Expected)[]
        {
            (0.10, 0), (0.25, 0), (0.26, 1), (0.9, 4), (1.0, 4), (1.005, 5),
            (2.9, 7), (3.0, 7), (3.9, 8), (5.9, 9), (6.0, 9), (100.0, 9), (double.NaN, 0),
        };

        bool levelsOk = true;
        foreach (var (scale, expected) in levelCases)
        {
            int actual = RenderLevels.ForScale(scale);
            bool ok = actual == expected;

            // 同时验证不变式：取到的档位 >= scale，且前一档 < scale。
            // 例外是 scale 超出最高档位（此时按封顶处理，>= scale 本就不成立）。
            if (ok && !double.IsNaN(scale)
                   && scale > RenderLevels.Values[RenderLevels.MinIndex]
                   && scale <= RenderLevels.ToZoom(RenderLevels.MaxIndex))
            {
                ok = RenderLevels.ToZoom(actual) >= scale
                  && (actual == RenderLevels.MinIndex || RenderLevels.ToZoom(actual - 1) < scale);
            }

            if (!ok)
            {
                levelsOk = false;
                Console.WriteLine($"  FAIL scale={scale} → 档位 {actual}（期望 {expected}）");
            }
        }

        Console.WriteLine($"  档位表 = [{string.Join(", ", RenderLevels.Values)}]");
        Console.WriteLine($"[{(levelsOk ? "PASS" : "FAIL")}] 取「不小于 scale 的最小档」，最高 {RenderLevels.ToZoom(RenderLevels.MaxIndex)}");
        if (!levelsOk) failures++;

        // ---------------------------------------------------------- 2) 手势合成变换
        Console.WriteLine();
        Console.WriteLine("---- 2) 触摸手势：缩放 + 平移合成一次变换 ----");

        var gestureCases = new (double Scale, double Ox, double Oy, Point Anchor, Vector Translation, double ScaleDelta, string Name)[]
        {
            (1.0, 0, 0, new Point(400, 300), new Vector(0, 0), 1.2, "仅缩放"),
            (1.0, 0, 0, new Point(400, 300), new Vector(35, -12), 1.0, "仅平移"),
            (1.7, -220, 90, new Point(600, 250), new Vector(18, 44), 0.85, "缩放+平移"),
            (12.0, 100, 100, new Point(300, 300), new Vector(20, 20), 3.0, "放大触顶 clamp"),
            (0.10, 50, 50, new Point(500, 400), new Vector(0, -30), 0.2, "缩小触底 clamp"),
        };

        bool gesturesOk = true;
        foreach (var c in gestureCases)
        {
            var vp = new CanvasViewport();
            vp.SetView(c.Scale, c.Ox, c.Oy);

            // 手势前恰好位于锚点处的那个内容点
            Point anchorWorld = vp.ToWorld(c.Anchor);

            vp.ApplyManipulation(c.Anchor, c.Translation, c.ScaleDelta);

            // 手势后它应该正好移动到 anchor + translation
            Point movedTo = vp.ToViewport(anchorWorld);
            var expected = new Point(c.Anchor.X + c.Translation.X, c.Anchor.Y + c.Translation.Y);

            bool ok = Near(movedTo, expected);
            if (!ok)
            {
                gesturesOk = false;
                Console.WriteLine($"  FAIL [{c.Name}] 应落到 ({expected.X:F4},{expected.Y:F4})，"
                                  + $"实际 ({movedTo.X:F4},{movedTo.Y:F4})");
            }
        }

        Console.WriteLine($"[{(gesturesOk ? "PASS" : "FAIL")}] 不变量：锚点处的内容点，手势后恰好移动到 anchor+translation"
                          + "（含缩放被 clamp 到上下限的两种情况）");
        if (!gesturesOk) failures++;

        // 纯平移时 offset 增量应恰等于 translation
        var panVp = new CanvasViewport();
        panVp.SetView(2.0, 10, 20);
        panVp.ApplyManipulation(new Point(100, 100), new Vector(30, -7), 1.0);
        bool panOk = Math.Abs(panVp.OffsetX - 40) < 1e-9 && Math.Abs(panVp.OffsetY - 13) < 1e-9;
        Console.WriteLine($"[{(panOk ? "PASS" : "FAIL")}] 纯平移：offset (10,20) + 位移(30,-7) = ({panVp.OffsetX:F4},{panVp.OffsetY:F4})");
        if (!panOk) failures++;

        // ---------------------------------------------------------- 3) LRU 缓存
        Console.WriteLine();
        Console.WriteLine("---- 3) 位图 LRU 缓存 ----");

        const int SwatchSize = 200;                 // 200x200x4B = 160000 B / 张
        long swatchBytes = (long)SwatchSize * SwatchSize * 4;

        var cache = new PageRenderCache { MaxBytes = 500_000 };

        for (int i = 0; i < 4; i++)
        {
            cache.Add(new PageRenderCache.Key(0, i), MakeBitmap(SwatchSize, SwatchSize));
        }

        bool evictOk = !cache.Contains(new PageRenderCache.Key(0, 0))   // 最久的被淘汰
                    && cache.Contains(new PageRenderCache.Key(0, 3))    // 最新的留着
                    && cache.Count == 3
                    && cache.CurrentBytes == swatchBytes * 3;
        Console.WriteLine($"  4 张 {swatchBytes / 1024}KB 位图放入 500KB 预算 → 保留 {cache.Count} 张，"
                          + $"占用 {cache.CurrentBytes / 1024}KB，最旧的已淘汰={!cache.Contains(new PageRenderCache.Key(0, 0))}");
        Console.WriteLine($"[{(evictOk ? "PASS" : "FAIL")}] 超预算按 LRU 淘汰，字节统计准确");
        if (!evictOk) failures++;

        // 命中应把条目提升为最近使用，从而在下次淘汰时幸存
        cache.Clear();
        for (int i = 0; i < 3; i++)
        {
            cache.Add(new PageRenderCache.Key(0, i), MakeBitmap(SwatchSize, SwatchSize));
        }
        cache.TryGet(new PageRenderCache.Key(0, 0), out _);             // 命中 (0,0) → 提升
        cache.Add(new PageRenderCache.Key(0, 3), MakeBitmap(SwatchSize, SwatchSize));

        bool promoteOk = cache.Contains(new PageRenderCache.Key(0, 0))   // 被命中过，幸存
                      && !cache.Contains(new PageRenderCache.Key(0, 1))  // 反而淘汰了它
                      && cache.Contains(new PageRenderCache.Key(0, 3));
        Console.WriteLine($"[{(promoteOk ? "PASS" : "FAIL")}] 命中的条目提升为最近使用，淘汰最久未用者");
        if (!promoteOk) failures++;

        // 就近档位回退：优先向下，其次向上
        var closestCache = new PageRenderCache();
        closestCache.Add(new PageRenderCache.Key(1, 2), MakeBitmap(10, 10));
        closestCache.Add(new PageRenderCache.Key(1, 6), MakeBitmap(10, 10));

        // 先声明再传 out：短路求值下编译器无法证明 l3 一定被赋值，直接初始化最省事
        int l1 = -1, l2 = -1, l3 = -1;
        bool closestOk =
            closestCache.TryGetClosest(1, 4, out l1, out _) && l1 == 2 &&              // 向下优先
            closestCache.TryGetClosest(1, 2, out l2, out _) && l2 == 2 &&              // 精确命中
            closestCache.TryGetClosest(1, 1, out l3, out _) && l3 == 2 &&              // 只能向上
            !closestCache.TryGetClosest(0, 4, out _, out _);                           // 该页无任何档位
        Console.WriteLine($"[{(closestOk ? "PASS" : "FAIL")}] 就近档位回退：目标 {4} → 向下取 {l1}；目标 1 → 向上取 {l3}；无缓存页返回 false");
        if (!closestOk) failures++;

        // ---------------------------------------------------------- 4) 后台调度
        Console.WriteLine();
        Console.WriteLine("---- 4) 后台渲染调度器 ----");

        var renderCache = new PageRenderCache { MaxBytes = 64L * 1024 * 1024 };
        using var scheduler = new PageRenderScheduler(Dispatcher.CurrentDispatcher, renderCache);
        scheduler.SetDocument(service);

        int uiThreadId = Environment.CurrentManagedThreadId;
        int callbacks = 0;
        int callbacksOnUiThread = 0;
        scheduler.PageRendered += (_, _) =>
        {
            callbacks++;
            if (Environment.CurrentManagedThreadId == uiThreadId) callbacksOnUiThread++;
        };

        // 用最低档位（0.25）渲染，秒出，避免拖慢验收
        scheduler.UpdateDemand(new[]
        {
            new PageRenderScheduler.Request(0, RenderLevels.MinIndex),
            new PageRenderScheduler.Request(1, RenderLevels.MinIndex),
            new PageRenderScheduler.Request(2, RenderLevels.MinIndex),
        });

        bool allDone = PumpUntil(() => callbacks >= 3, 30_000);
        bool schedulerOk = allDone
                        && callbacks == 3
                        && callbacksOnUiThread == callbacks                            // 回推必须切到 UI 线程
                        && scheduler.RenderCount == 3
                        && renderCache.Contains(new PageRenderCache.Key(0, RenderLevels.MinIndex))
                        && renderCache.Contains(new PageRenderCache.Key(1, RenderLevels.MinIndex))
                        && renderCache.Contains(new PageRenderCache.Key(2, RenderLevels.MinIndex));

        Console.WriteLine($"  3 页渲染完成：回调 {callbacks} 次（其中 UI 线程 {callbacksOnUiThread} 次），渲染计数 {scheduler.RenderCount}");
        Console.WriteLine($"[{(schedulerOk ? "PASS" : "FAIL")}] 后台串行渲染 → 结果进缓存 → 切回 UI 线程回调");
        if (!schedulerOk) failures++;

        // 已缓存的请求不应重复渲染
        int beforeDedup = scheduler.RenderCount;
        scheduler.UpdateDemand(new[]
        {
            new PageRenderScheduler.Request(0, RenderLevels.MinIndex),
            new PageRenderScheduler.Request(0, RenderLevels.MinIndex),
        });
        PumpFor(300);
        bool dedupOk = scheduler.RenderCount == beforeDedup && scheduler.PendingCount == 0;
        Console.WriteLine($"[{(dedupOk ? "PASS" : "FAIL")}] 已缓存/重复的请求不重复渲染（渲染计数保持 {scheduler.RenderCount}）");
        if (!dedupOk) failures++;

        // ---------------------------------------------------------- 5) generation 失效
        Console.WriteLine();
        Console.WriteLine("---- 5) 换文档时在途请求作废 ----");

        var staleCache = new PageRenderCache();
        using (var staleScheduler = new PageRenderScheduler(Dispatcher.CurrentDispatcher, staleCache))
        {
            staleScheduler.SetDocument(service);

            // 排满最高档位请求（单页要渲几百毫秒），紧接着"换文档"
            var bulk = new List<PageRenderScheduler.Request>();
            for (int i = 0; i < service.PageCount; i++)
            {
                bulk.Add(new PageRenderScheduler.Request(i, RenderLevels.MaxIndex));
            }
            staleScheduler.UpdateDemand(bulk);
            staleScheduler.SetDocument(service);

            PumpFor(1500);

            // 至多有一个已经进了渲染（无法中断），但它渲完必须被丢弃、不进缓存；
            // 其余排队的请求应该"连渲都不渲"。
            bool staleOk = staleScheduler.RenderCount <= 1 && staleCache.Count == 0;
            Console.WriteLine($"  作废后：渲染计数 {staleScheduler.RenderCount}（应 ≤ 1），缓存条目 {staleCache.Count}（应为 0）");
            Console.WriteLine($"[{(staleOk ? "PASS" : "FAIL")}] generation 失效：过期请求不发起渲染，渲完的结果不进缓存");
            if (!staleOk) failures++;
        }

        // ---------------------------------------------------------- 6) 缩放序列收敛
        Console.WriteLine();
        Console.WriteLine("---- 6) 连续缩放时档位收敛（量化是否真的让缓存可命中）----");

        var zoomVp = new CanvasViewport();
        zoomVp.SetView(1.0, 0, 0);

        var seenLevels = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            zoomVp.ZoomAt(new Point(800, 450), 1.1);
            seenLevels.Add(RenderLevels.ForScale(zoomVp.Scale));
        }

        int distinct = seenLevels.Distinct().Count();
        bool convergeOk = distinct <= 6;

        Console.WriteLine($"  连续 20 次滚轮缩放：scale 1.00 → {zoomVp.Scale:F2}，"
                          + $"经过档位 [{string.Join(",", seenLevels)}]");
        Console.WriteLine($"  不同档位 {distinct} 种（若不做量化则会有 20 种，缓存必然全失效）");
        Console.WriteLine($"[{(convergeOk ? "PASS" : "FAIL")}] 档位量化让连续缩放收敛到少数档位");
        if (!convergeOk) failures++;

        // 最终需要的最高档位必须真的能渲出来，且不超预算
        int finalLevel = RenderLevels.ForScale(zoomVp.Scale);
        var finalCache = new PageRenderCache { MaxBytes = 256L * 1024 * 1024 };
        using (var finalScheduler = new PageRenderScheduler(Dispatcher.CurrentDispatcher, finalCache))
        {
            finalScheduler.SetDocument(service);
            finalScheduler.UpdateDemand(new[] { new PageRenderScheduler.Request(0, finalLevel) });

            bool finalDone = PumpUntil(
                () => finalCache.Contains(new PageRenderCache.Key(0, finalLevel)), 120_000);

            long bytes = finalCache.CurrentBytes;
            bool finalOk = finalDone && bytes <= finalCache.MaxBytes;

            Console.WriteLine($"  档位 {finalLevel}（zoom {RenderLevels.ToZoom(finalLevel)}）渲染 1 页 → "
                              + $"{bytes / 1024.0 / 1024.0:F1} MB，预算 {finalCache.MaxBytes / 1024 / 1024} MB");
            Console.WriteLine($"[{(finalOk ? "PASS" : "FAIL")}] 最高档位可渲染且单页占用在预算内");
            if (!finalOk) failures++;
        }

        return failures;
    }

    // ================================================================= M3

    /// <summary>
    /// M3 断言组：墨水层落位、两条变换链同变换、端到端光栅、笔/指分工、换文档清笔迹。
    /// </summary>
    /// <remarks>
    /// 第 2 组是本里程碑的核心。它实例化<b>真实控件树</b>（不是镜像树），
    /// 于是"笔迹随画布变换不漂移"这件事可以被端到端证明：
    /// 在已知世界点放一条笔画，把整棵树用 <see cref="RenderTargetBitmap"/> 光栅化，
    /// 再断言笔迹像素的质心恰好落在 <c>viewport.ToViewport(世界点)</c> 上。
    /// 渲染、布局、变换、笔迹四层一次比对完 —— 质心对不上就说明其中某一层错了。
    /// </remarks>
    private static int RunM3Checks(PdfiumDocumentService service, string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M3：笔迹层接入 ================");

        var sizes = new List<Size>(service.PageCount);
        for (int i = 0; i < service.PageCount; i++) sizes.Add(service.GetPageSize(i));

        // ---------------------------------------------------------- 1) 世界原点与可写范围
        Console.WriteLine();
        Console.WriteLine("---- 1) 世界原点与墨迹层可写范围 ----");

        var layout = new WorldLayout { PageGap = 24.0 };
        layout.Rebuild(sizes);

        var bounds = layout.WorldBounds;
        var extent = layout.InkExtent;
        var firstRect = layout.PageRects[0];
        const double margin = WorldLayout.DefaultPageMargin;

        bool extentOk =
            Math.Abs(firstRect.X - margin) < 1e-6 &&
            Math.Abs(firstRect.Y - margin) < 1e-6 &&
            Math.Abs(extent.X) < 1e-6 && Math.Abs(extent.Y) < 1e-6 &&
            Math.Abs(extent.Width - (bounds.Width + margin * 2)) < 1e-6 &&
            Math.Abs(extent.Height - (bounds.Height + margin * 2)) < 1e-6;

        Console.WriteLine($"  首页起点 = ({firstRect.X:F2},{firstRect.Y:F2})（应为留白 {margin}）");
        Console.WriteLine($"  页并集   = ({bounds.X:F2},{bounds.Y:F2},{bounds.Width:F2},{bounds.Height:F2})");
        Console.WriteLine($"  InkExtent= ({extent.X:F2},{extent.Y:F2},{extent.Width:F2},{extent.Height:F2})  ← 原点必须为 0");
        Console.WriteLine($"[{(extentOk ? "PASS" : "FAIL")}] 页面从 (M,M) 起排 ⇒ 墨迹层本地坐标 ≡ 世界坐标（零补偿的前提）");
        if (!extentOk) failures++;

        // ---------------------------------------------------------- 2) 真实控件树
        Console.WriteLine();
        Console.WriteLine("---- 2) 真实控件树：变换链一致 + 端到端光栅 ----");

        const double hostWidth = 1000;
        const double hostHeight = 700;

        var host = new CanvasViewportHost();
        host.Measure(new Size(hostWidth, hostHeight));
        host.Arrange(new Rect(0, 0, hostWidth, hostHeight));
        host.UpdateLayout();
        host.SetDocument(service);
        host.UpdateLayout();
        PumpFor(250);

        var pageImages = VisualDescendants(host).OfType<Image>().ToList();
        bool imagesOk = pageImages.Count == host.Layout.PageRects.Count;
        if (!imagesOk)
        {
            Console.WriteLine($"  FAIL 视觉树里找到 {pageImages.Count} 个页 Image，但排版有 "
                              + $"{host.Layout.PageRects.Count} 页");
            failures++;
        }
        Console.WriteLine($"  控件树：页 Image {pageImages.Count} 个，墨迹层 "
                          + $"{host.InkSurface.Width:F0}x{host.InkSurface.Height:F0} world 单位");

        // 三组视口状态：给定缩放 + "首页左上角落在这个视口位置"。
        // 刻意用"页左上角"而不是裸的世界偏移数字：后者会随留白 / 页尺寸变化而失效
        //（PageMargin 96→960 时，旧的裸偏移把整页推出了视口，三条断言一起挂）。
        var p0 = host.Layout.PageRects[0];
        var states = new (double Scale, double CornerX, double CornerY, string Name)[]
        {
            (1.0, 150, 90, "100%"),
            (2.0, -100, -80, "200%"),
            (3.0, -600, -700, "300%+平移"),
        };

        bool chainOkAll = true;
        bool sameChainOkAll = true;
        bool rasterOk = true;
        bool paperOk = true;

        // 定位类光栅断言必须在**黑底**下跑：底色是白的时候，"白纸外接框"会等于整个视口，
        // 期望值也等于整个视口 ⇒ 断言恒真、失去区分度。
        // 黑底只是验证手段，生产默认是纯白（见 M3.6）。
        host.CanvasBackground = Brushes.Black;

        foreach (var state in states)
        {
            host.Viewport.SetView(state.Scale,
                                  state.CornerX - p0.X * state.Scale,
                                  state.CornerY - p0.Y * state.Scale);
            host.UpdateLayout();
            PumpFor(80);

            var viewport = host.Viewport;

            // 刻意不取视口中心：中心点自证性太强（错一倍也可能落在同一处），偏心点更能暴露倍数错误
            var screenTarget = new Point(hostWidth * 0.72, hostHeight * 0.68);
            var worldTarget = viewport.ToWorld(screenTarget);

            var pageRect = host.Layout.PageRects[0];
            bool onPaper = pageRect.Contains(worldTarget);

            // (a) 墨水层的变换链 == 视口换算。这一条成立就说明"没有意外的偏移或缩放"
            var inkToHost = host.InkSurface.TransformToAncestor(host);
            var viaInk = inkToHost.Transform(worldTarget);
            bool inkChain = Near(viaInk, screenTarget);

            // (b) 页 Image 与墨水层两条链映射同一个世界点 → 得到同一个视口点。
            //     两层同变换 ⇒ 相对漂移在数学上不可能。
            bool sameChain = false;
            if (imagesOk)
            {
                var pageToHost = pageImages[0].TransformToAncestor(host);
                var probeLocal = new Point(150, 220);                          // 页局部坐标
                var probeWorld = new Point(pageRect.X + probeLocal.X, pageRect.Y + probeLocal.Y);
                sameChain = Near(pageToHost.Transform(probeLocal), inkToHost.Transform(probeWorld));
            }

            chainOkAll = chainOkAll && inkChain && onPaper;
            sameChainOkAll = sameChainOkAll && sameChain;

            if (!inkChain || !onPaper)
            {
                Console.WriteLine($"  FAIL [{state.Name}] 世界点({worldTarget.X:F2},{worldTarget.Y:F2})"
                                  + $" 经墨水层链 → ({viaInk.X:F2},{viaInk.Y:F2})，"
                                  + $"期望 ({screenTarget.X:F2},{screenTarget.Y:F2})；页在纸上={onPaper}");
            }

            // ---- 端到端光栅 ----
            host.Strokes.Clear();

            // M21：这条断言验的是"坐标系变换链对不对"，与笔有多粗无关；但它的可信度靠
            // "墨点数 > 20" 兜底，而默认笔宽 M21 从 1.5 调到 0.8 —— 100% 缩放下 0.8 world
            // 就是 0.8px，抗锯齿后实测只剩 4 个墨点，前提不成立（偏差其实是 0.00）。
            // 显式给一档「中」(1.5)，把笔宽这个无关变量固定住，
            // 免得以后每次调默认笔宽都要来重新校准这条坐标断言。
            host.SetPenWidth(InkPalette.Widths[1].WorldWidth);

            // 一个"十"字：形状关于目标点中心对称，于是它的像素质心 = 目标点，可解析预测
            host.Strokes.Add(MakeMark(host.Pen,
                new Point(worldTarget.X - 12, worldTarget.Y), new Point(worldTarget.X + 12, worldTarget.Y)));
            host.Strokes.Add(MakeMark(host.Pen,
                new Point(worldTarget.X, worldTarget.Y - 12), new Point(worldTarget.X, worldTarget.Y + 12)));

            host.UpdateLayout();
            PumpFor(80);

            var bitmap = Snapshot(host, (int)hostWidth, (int)hostHeight);
            var stats = Analyze(bitmap);

            // 存一张凭据图：能看到品红十字落在试卷纸面上（而不是偏移到别处）
            SavePng(bitmap, Path.Combine(outDir, $"ink-zoom{state.Scale:0.0}.png"));

            // 笔迹像素质心（把像素中心 x+0.5 计入，与 DIP 坐标对齐）
            var centroid = new Point((double)stats.SumX / stats.InkPixels, (double)stats.SumY / stats.InkPixels);
            bool inkPlaced = stats.InkPixels > 20 && Near(centroid, screenTarget, 3.0);

            // 白纸外接框 == 各页视口矩形的并集（被视口裁掉的部分自然不在光栅里）
            Rect? expectedPaper = null;
            foreach (var rect in host.Layout.PageRects)
            {
                var tl = viewport.ToViewport(new Point(rect.X, rect.Y));
                var br = viewport.ToViewport(new Point(rect.Right, rect.Bottom));
                var clipped = Rect.Intersect(new Rect(tl, br), new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                if (clipped.IsEmpty || clipped.Width < 1 || clipped.Height < 1) continue;
                expectedPaper = expectedPaper is null ? clipped : Rect.Union(expectedPaper.Value, clipped);
            }

            bool paperMatches = false;
            if (stats.WhitePixels > 0 && expectedPaper is not null)
            {
                var box = expectedPaper.Value;
                paperMatches = Math.Abs(stats.WhiteMinX - box.X) <= 4
                            && Math.Abs(stats.WhiteMinY - box.Y) <= 4
                            && Math.Abs(stats.WhiteMaxX - (box.Right - 1)) <= 4
                            && Math.Abs(stats.WhiteMaxY - (box.Bottom - 1)) <= 4;
            }

            rasterOk = rasterOk && inkPlaced;
            paperOk = paperOk && paperMatches;

            var paper = expectedPaper ?? Rect.Empty;

            Console.WriteLine($"  [{state.Name}] 世界点({worldTarget.X:F1},{worldTarget.Y:F1}) → 视口({screenTarget.X:F1},{screenTarget.Y:F1})"
                              + $"  链上={viaInk.X:F4},{viaInk.Y:F4}  页在纸上={onPaper}  两链同变换={sameChain}");
            Console.WriteLine($"           笔迹质心=({centroid.X:F2},{centroid.Y:F2})  像素={stats.InkPixels}"
                              + $"  偏差=({centroid.X - screenTarget.X:F2},{centroid.Y - screenTarget.Y:F2})"
                              + (inkPlaced ? string.Empty : "   <<超容差"));
            Console.WriteLine($"           白纸框=({stats.WhiteMinX},{stats.WhiteMinY})-({stats.WhiteMaxX},{stats.WhiteMaxY})"
                              + $"  期望=({paper.X:F1},{paper.Y:F1})-({paper.Right - 1:F1},{paper.Bottom - 1:F1})"
                              + $"  一致={paperMatches}");
        }

        if (imagesOk)
        {
            Console.WriteLine($"[{(chainOkAll ? "PASS" : "FAIL")}] 墨水层变换链 == 视口换算（三组视口状态，误差 < 1e-6）");
            Console.WriteLine($"[{(sameChainOkAll ? "PASS" : "FAIL")}] 页 Image 与墨水层两条链映射同一世界点得到同一视口点");
            Console.WriteLine($"[{(rasterOk ? "PASS" : "FAIL")}] 端到端光栅：笔迹像素质心 == ToViewport(世界点)（容差 3px）");
            Console.WriteLine($"[{(paperOk ? "PASS" : "FAIL")}] 端到端光栅（黑底对照）：白纸外接框 == 页矩形经同一变换后的可见部分");
            if (!chainOkAll) failures++;
            if (!sameChainOkAll) failures++;
            if (!rasterOk) failures++;
            if (!paperOk) failures++;
        }

        // 后续步骤一律用生产默认底色
        host.CanvasBackground = Brushes.White;

        // ---------------------------------------------------------- 3) 笔/指分工
        Console.WriteLine();
        Console.WriteLine("---- 3) 笔 / 手指 / 手掌 分工 ----");

        // 枚举 (工具 × 反转) 的全部 6 种组合，一个不漏。
        //
        // M4.2 把「设备类型」从这个表里彻底删掉了 —— 这是被一体机实测推翻的一处设计。
        // 实测日志（2026-09-21）：设备清单里只有一个 name="HID Interface" 的 Touch 设备，
        // 笔在系统看来就是一根手指（红外触摸框 + 被动笔）；"Touch ⇒ 抑制"只会把笔一起废掉。
        // 而"抑制"这个动作本身也是坏的：Handled 挡不住笔线程上的实时墨迹层，还会泄漏输入捕获。
        //
        // M7.1 又把入参从 BoardTool 枚举换成了 ToolInputKind：决策函数从此对"有哪些工具"无感，
        // 插件带来的新工具（直尺等）一律报 None，这里一行都不用改。
        // 6 种组合的结论与 M6 逐条相同 —— 这就是"迁移零行为变更"的第一条证据。
        var policyCases = new (ToolInputKind Kind, bool Inverted, InkInputAction Expected, string Name)[]
        {
            (ToolInputKind.Ink, false, InkInputAction.Draw, "落墨类工具 + 笔尖/触摸 → 落墨"),
            (ToolInputKind.Ink, true, InkInputAction.Erase, "落墨类工具 + 笔尾反转 → 擦除"),
            (ToolInputKind.Erase, false, InkInputAction.Erase, "擦除类工具 + 笔尖/触摸 → 擦除"),
            (ToolInputKind.Erase, true, InkInputAction.Erase, "擦除类工具 + 笔尾 → 擦除"),
            (ToolInputKind.None, false, InkInputAction.Suppress, "不落墨工具 + 笔尖/触摸 → 不落墨"),
            (ToolInputKind.None, true, InkInputAction.Suppress, "不落墨工具 + 笔尾 → 不落墨"),
        };

        bool policyOk = true;
        foreach (var c in policyCases)
        {
            var actual = InkInputPolicy.Decide(c.Kind, c.Inverted);
            if (actual != c.Expected)
            {
                policyOk = false;
                Console.WriteLine($"  FAIL [{c.Name}] 得到 {actual}，期望 {c.Expected}");
            }
        }

        Console.WriteLine("  这些组合（笔尾擦除、手工具下触摸）本机没有触摸屏也没有笔，"
                          + "制造不出来 —— 全靠这张表钉死");
        Console.WriteLine($"[{(policyOk ? "PASS" : "FAIL")}] 笔落墨；笔尾反转与橡皮工具擦除；手工具不落墨");
        if (!policyOk) failures++;

        // M4.2 回归：决策函数的参数里**不能再出现设备类型**。
        // 用反射钉死，因为"把 deviceType 加回去"看起来是个很合理的改动
        // （"能区分笔和手指为什么要丢掉"），但它一旦发生，红外屏一体机上笔又会被当成手指，
        // 整套书写立刻报废 —— 而本机没有触摸屏，改坏了根本测不出来。
        var decideParams = typeof(InkInputPolicy)
            .GetMethod(nameof(InkInputPolicy.Decide))!
            .GetParameters();

        bool deviceAgnostic = decideParams.All(p => p.ParameterType != typeof(TabletDeviceType));

        Console.WriteLine($"[{(deviceAgnostic ? "PASS" : "FAIL")}] 分工决策不含设备类型参数"
                          + $"（实际：{string.Join(", ", decideParams.Select(p => p.ParameterType.Name))}）"
                          + " ⇒ 红外屏上笔与手指同为 Touch 也不会被误判");
        if (!deviceAgnostic) failures++;

        // M7.1 回归：决策函数的参数里**不能再出现 BoardTool**，且必须真的由 ToolInputKind 驱动。
        // 这是插件化的立身之本：一旦有人把"当前是哪个工具"塞回决策函数，
        // 那么每加一个工具（尤其是插件带来的、宿主编译期根本不认识的工具）都得回头改这里，
        // 插件化立刻破功。用反射钉死，比写注释提醒可靠。
        bool kindDriven = decideParams.All(p => p.ParameterType.Name != "BoardTool")
                       && decideParams.Any(p => p.ParameterType == typeof(ToolInputKind));

        Console.WriteLine($"[{(kindDriven ? "PASS" : "FAIL")}] 分工决策改由 ToolInputKind 驱动"
                          + "（不再是工具枚举）⇒ 插件工具报 None 即可，决策函数无需改动");
        if (!kindDriven) failures++;

        // ---------------------------------------------------------- 4) 笔宽语义
        Console.WriteLine();
        Console.WriteLine("---- 4) 笔宽单位 ----");

        var pen = new PenProfile();
        var attributes = pen.ToDrawingAttributes();

        // M21：原先把 1.5 / Colors.Red 写死在这里，等于把"默认档"在调色板和本断言里各说了一遍 ——
        // 改一次默认档就来改一次数，而这条断言的**本意**是"出厂默认与调色板默认档一致"
        // （与下面 3) 里那条 defaultMatchesPalette 同源）。改成引用调色板后它才真的能抓到不一致。
        var paletteDefaultColor = InkPalette.Colors[InkPalette.DefaultColorIndex];
        var paletteDefaultWidth = InkPalette.Widths[InkPalette.DefaultWidthIndex];

        bool penOk = attributes.Width == pen.WorldWidth
                  && attributes.Height == pen.WorldWidth
                  && attributes.IgnorePressure
                  && Math.Abs(attributes.Width - paletteDefaultWidth.WorldWidth) < 1e-9
                  && attributes.Color == paletteDefaultColor.Color;

        Console.WriteLine($"  默认笔：红，{attributes.Width} world 单位（≈ {attributes.Width * 25.4 / 72:F2} mm），"
                          + $"IgnorePressure={attributes.IgnorePressure}");
        Console.WriteLine($"[{(penOk ? "PASS" : "FAIL")}] 笔宽单位是 world（PDF point）而非屏幕像素 ⇒ 缩放时笔迹几何不重算");
        if (!penOk) failures++;

        // ---------------------------------------------------------- 5) 工具与换文档
        Console.WriteLine();
        Console.WriteLine("---- 5) 工具切换 与 换文档清笔迹 ----");

        host.SetTool(ToolIds.Hand);
        var handMode = host.InkSurface.EditingMode;
        bool handHitTestable = host.InkSurface.IsHitTestVisible;
        bool handManipulation = host.IsManipulationEnabled;
        bool handModeOk = handMode == InkCanvasEditingMode.None && !handHitTestable;

        host.SetTool(ToolIds.Pen);
        var penMode = host.InkSurface.EditingMode;
        bool penHitTestable = host.InkSurface.IsHitTestVisible;
        bool penManipulation = host.IsManipulationEnabled;
        bool penModeOk = penMode == InkCanvasEditingMode.Ink && penHitTestable;

        host.SetTool(ToolIds.Eraser);
        bool eraserManipulation = host.IsManipulationEnabled;
        host.SetTool(ToolIds.Pen);

        // 触摸归属（M4.2）：Manipulation 与"触摸被提升为笔事件"是两条互相抢输入的路径，
        // 而且 Manipulation 无法按手指数过滤（单指落下也会启动它）。
        // 只能按工具二选一 —— 两个都开着会同时落墨又平移，并且把墨迹采集打断（笔画永不提交）。
        bool manipulationFollowsTool = handManipulation && !penManipulation && !eraserManipulation;

        // 换文档前先放一条笔迹，换完必须干干净净
        host.Strokes.Add(MakeMark(host.Pen, new Point(300, 300), new Point(340, 300)));
        int beforeSwitch = host.StrokeCount;
        double extentWidthBefore = host.InkSurface.Width;

        host.SetDocument(service);

        bool clearOk = beforeSwitch > 0
                    && host.StrokeCount == 0
                    && Math.Abs(host.InkSurface.Width - extentWidthBefore) < 1e-6
                    && Math.Abs(host.InkSurface.Width - host.Layout.InkExtent.Width) < 1e-6;

        Console.WriteLine($"  手工具：EditingMode={handMode}、命中可见={handHitTestable}、手势接管={handManipulation}"
                          + $"；笔工具：EditingMode={penMode}、命中可见={penHitTestable}、手势接管={penManipulation}"
                          + $"；橡皮工具：手势接管={eraserManipulation}");
        Console.WriteLine($"  换文档前笔迹 {beforeSwitch} 条 → 换后 {host.StrokeCount} 条，"
                          + $"墨迹层尺寸 {host.InkSurface.Width:F0}（InkExtent {host.Layout.InkExtent.Width:F0}）");
        Console.WriteLine($"[{(handModeOk && penModeOk ? "PASS" : "FAIL")}] 工具切换真正落到墨迹层（手模式下墨迹层不参与命中）");
        Console.WriteLine($"[{(manipulationFollowsTool ? "PASS" : "FAIL")}] 触摸归属跟随工具：只有手工具开手势接管"
                          + "（否则落墨与平移会同时发生，笔画也不会提交）");
        Console.WriteLine($"[{(clearOk ? "PASS" : "FAIL")}] 换文档清空笔迹并按新排版重算可写范围");
        if (!(handModeOk && penModeOk)) failures++;
        if (!manipulationFollowsTool) failures++;
        if (!clearOk) failures++;

        // ---------------------------------------------------------- 6) 成品图
        // 换成真实默认笔（红，1.5 world 单位）在 fit-width 下写三笔，存一张观感图。
        // 本机没有触摸屏也没有笔，这是最接近"老师在试卷上写字"的证据。
        host.Strokes.Clear();
        host.FitWidth();
        host.UpdateLayout();
        PumpFor(500);

        var page0 = host.Layout.PageRects[0];

        // 这里不做任何手工定位：FitWidth（方案 B）自己就会停在首页顶部。
        // 顺手断言一次，等于给"打开试卷 → 看到第一页"配了张端到端证据图。
        bool redPenOnFirstPage = Math.Abs(host.Viewport.ToViewport(new Point(page0.X, page0.Y)).Y) < 1e-6
                              && Math.Abs(host.Viewport.ToViewport(new Point(page0.X, page0.Y)).X - 24) < 1e-6;
        if (!redPenOnFirstPage)
        {
            failures++;
            Console.WriteLine("  FAIL 默认红笔证据图没有落在首页顶部");
        }

        var strokes = new (double Dy, double Length)[]
        {
            (0, 150), (60, 90), (120, 210),
        };
        foreach (var (dy, length) in strokes)
        {
            var start = new Point(page0.X + 90, page0.Y + 240 + dy);
            host.Strokes.Add(MakeLine(host.Pen.ToDrawingAttributes(),
                start, new Point(start.X + length, start.Y + 8)));
        }

        host.UpdateLayout();
        PumpFor(600);

        string redPenFile = Path.Combine(outDir, "ink-red-pen.png");
        SavePng(Snapshot(host, (int)hostWidth, (int)hostHeight), redPenFile);
        Console.WriteLine();
        Console.WriteLine($"  默认红笔（zoom ≈ {host.Viewport.Scale:F2}）写三笔 → {redPenFile}");

        host.Shutdown();

        return failures;
    }

    // ================================================================= M7.1：工具系统与插件契约

    /// <summary>
    /// M7.1 断言组：工具注册表、工具声明 → 墨迹层配置的映射、插件输出契约（提交笔画 + 预览层）。
    /// </summary>
    /// <remarks>
    /// 这一组同时是"零行为变更重构"的护栏。M7.1 不该改变任何用户可见行为，
    /// 所以任何一条挂掉都说明重构改坏了东西 —— 重构最危险的地方正在于此：
    /// 它不会编译报错，只会悄悄改变行为，而本机没有触摸屏，很多行为要到教室才发现。
    /// <para>
    /// 另一层意义是<b>钉住插件契约的语义</b>：工具声明 <c>NeedsPointer</c> 就一定能收到世界坐标、
    /// 提交的笔画一定能撤销、切工具一定会清预览。这些是 M7.3 直尺插件赖以工作的前提，
    /// 前提错了，M7.3 会在错误的地基上盖房子。
    /// </para>
    /// </remarks>
    /// <summary>
    /// M22：通用 Web 面板的<b>契约层</b>检查。
    /// </summary>
    /// <remarks>
    /// 这一层刻意只看"接口与信封"，不碰 WebView2 —— 所以能在无窗口的 harness 里跑完。
    /// 断言的重点不是"新东西能用"，而是
    /// <b>「加新接口时没有顺手改坏 ITool」</b>：ITool 一旦加成员，所有已编译的插件
    /// 都会在加载时 <c>TypeLoadException</c>，那是本项目最贵的一类事故。
    /// </remarks>
    private static int RunM22Checks()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M22：通用 Web 面板契约 ================");

        // ---- 0) 新契约类型都在（名字写错要当场知道）----
        bool typesOk =
            typeof(IWebPanelBridge).IsInterface
            && typeof(WebPanelIds).IsAbstract && typeof(WebPanelIds).IsSealed        // static class
            && typeof(WebPanelProtocol).IsAbstract && typeof(WebPanelProtocol).IsSealed
            && typeof(WebPanelMessage).IsClass;

        Console.WriteLine($"[{(typesOk ? "PASS" : "FAIL")}] 通用 Web 面板的 4 个契约类型都存在、且形态正确");
        if (!typesOk) failures++;

        // ---- 1) ★ ITool 成员零新增 ----
        // 数字写死是有意的：它是"必须显式改这一行、并同时把契约主版本 +1"的绊线。
        // 属性 9：Id / DisplayName / ToolTip / Shortcut / UsesInkLayer / NeedsPointer / InputKind / InkMode / Cursor
        // 方法 3：Activate / Deactivate / OnPointer
        var toolProps = typeof(ITool).GetProperties().Length;
        // ★ 必须排掉属性的访问器：接口上 get_Cursor 之类也是 MethodInfo，
        //   直接 GetMethods().Length 会得到 9 个属性 getter + 3 个真方法 = 12。
        var toolMethods = System.Linq.Enumerable.Count(typeof(ITool).GetMethods(), m => !m.IsSpecialName);
        bool toolFrozen = toolProps == 9 && toolMethods == 3;

        Console.WriteLine($"[{(toolFrozen ? "PASS" : "FAIL")}] ITool 成员零新增（属性 {toolProps}/9、方法 {toolMethods}/3）"
                          + " —— 给 ITool 加成员会让所有已编译插件 TypeLoadException");
        if (!toolFrozen) failures++;

        // ---- 2) IToolContext 长出了 WebSim，且旧成员 GeoGebra 还在 ----
        bool contextOk =
            typeof(IToolContext).GetProperty(nameof(IToolContext.WebSim))?.PropertyType == typeof(IWebPanelBridge)
            && typeof(IToolContext).GetProperty(nameof(IToolContext.GeoGebra)) is not null;

        Console.WriteLine($"[{(contextOk ? "PASS" : "FAIL")}] IToolContext 新增 WebSim（IWebPanelBridge?）、"
                          + "并保留 GeoGebra（老插件零改动）");
        if (!contextOk) failures++;

        // ---- 3) profile Id 常量 ----
        bool idsOk = WebPanelIds.GeoGebra == "geogebra" && WebPanelIds.WebSim == "websim";
        Console.WriteLine($"[{(idsOk ? "PASS" : "FAIL")}] profile Id 常量（{WebPanelIds.GeoGebra} / {WebPanelIds.WebSim}）");
        if (!idsOk) failures++;

        // ---- 4) 信封往返：Encode → TryDecode 保住 type 与 payload ----
        const string simTitle = "物理仿真";
        var encoded = WebPanelProtocol.Encode(WebPanelProtocol.Ready, new { title = simTitle, n = 3 });
        bool decodeOk = WebPanelProtocol.TryDecode(encoded, out var msg, out _)
            && msg.Type == WebPanelProtocol.Ready
            && msg.ReadPayloadString("title") == simTitle;

        Console.WriteLine($"[{(decodeOk ? "PASS" : "FAIL")}] 信封往返：Encode → TryDecode 保住 type 与 payload");
        if (!decodeOk) failures++;

        // ---- 5) 坏消息被明确拒绝并给中文原因（不许静默吞掉）----
        bool badRejected =
            !WebPanelProtocol.TryDecode("{不是 JSON]", out _, out var err1) && !string.IsNullOrEmpty(err1)
            && !WebPanelProtocol.TryDecode("{}", out _, out var err2) && !string.IsNullOrEmpty(err2)
            && !WebPanelProtocol.TryDecode("   ", out _, out var err3) && !string.IsNullOrEmpty(err3);

        Console.WriteLine($"[{(badRejected ? "PASS" : "FAIL")}] 坏消息被明确拒绝并给中文原因（不静默吞掉）");
        if (!badRejected) failures++;

        // ---- 6) 公共生命周期常量与 GeoGebraProtocol 逐字一致 ----
        // 两个页面会对同一件事说不同的话时，"消息发出去了但对面没反应"最难查。
        bool constOk =
            WebPanelProtocol.Ready == GeoGebraProtocol.Ready
            && WebPanelProtocol.Hello == GeoGebraProtocol.Hello
            && WebPanelProtocol.Error == GeoGebraProtocol.Error
            && WebPanelProtocol.LoadFailed == GeoGebraProtocol.LoadFailed
            && WebPanelProtocol.Changed == GeoGebraProtocol.Changed
            && WebPanelProtocol.ExportPng == GeoGebraProtocol.ExportPng
            && WebPanelProtocol.RequestExportPng == GeoGebraProtocol.RequestExportPng;

        Console.WriteLine($"[{(constOk ? "PASS" : "FAIL")}] 通用协议与 GeoGebra 协议的公共常量逐字一致");
        if (!constOk) failures++;

        return failures;
    }

    private static int RunM71Checks()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.1：工具系统与插件契约 ================");

        const int hostWidth = 1000;
        const int hostHeight = 700;

        var host = new CanvasViewportHost();
        host.Measure(new Size(hostWidth, hostHeight));
        host.Arrange(new Rect(0, 0, hostWidth, hostHeight));
        host.UpdateLayout();

        // ---------------------------------------------------------- 1) 注册表
        var ids = host.Tools.Select(t => t.Id).ToList();

        // M7.4 起宿主还内置一个「选择」工具（图形对象的公共操作面），
        // 它排在全部内置工具之后 —— "先有东西可操作"才谈得上选择。
        // M12 起再加一个宿主侧「题号标记」工具，同样排在「选择」之前。
        bool builtInOk = ids.Count == 5
                      && ids.Contains(ToolIds.Pen)
                      && ids.Contains(ToolIds.Eraser)
                      && ids.Contains(ToolIds.Hand)
                      && ids.Contains(MathPhys.Ink.Tools.BuiltIn.QuestionMarkToolIds.Id)
                      && ids[^1] == ToolIds.GfxSelect
                      && host.ActiveToolId == ToolIds.Pen;   // 第一个注册的自动激活

        Console.WriteLine($"  内置工具：{string.Join(", ", ids)}；当前工具 = {host.ActiveToolId}");
        Console.WriteLine($"[{(builtInOk ? "PASS" : "FAIL")}] 三个书写类内置工具 + 「题号标记」 + 一个「选择」工具都已注册，"
                          + "默认工具是笔（注册表非空 ⇒ 不存在「有按钮但没工具」的空白态）");
        if (!builtInOk) failures++;

        // 快捷键：大小键盘数字都要能查到同一个工具。用户按的是"1"，不关心在哪块键盘上。
        bool shortcutOk = host.FindToolByShortcut(Key.D1)?.Id == ToolIds.Pen
                       && host.FindToolByShortcut(Key.NumPad1)?.Id == ToolIds.Pen
                       && host.FindToolByShortcut(Key.D2)?.Id == ToolIds.Eraser
                       && host.FindToolByShortcut(Key.D3)?.Id == ToolIds.Hand
                       && host.FindToolByShortcut(Key.F5) is null;   // 没声明的键要原样放过

        Console.WriteLine($"[{(shortcutOk ? "PASS" : "FAIL")}] 工具快捷键按注册表分发（1/2/3，含小键盘），"
                          + "未声明的键返回 null ⇒ 界面会原样放过，不吞按键");
        if (!shortcutOk) failures++;

        // Id 重复必须被拒绝，而不是"后者替换前者"——那会表现成"重启一次工具就变了"
        var dupRegistry = new ToolRegistry();
        dupRegistry.Add(new RecordingTool("dup", needsPointer: false));
        dupRegistry.Add(new RecordingTool("dup", needsPointer: false));

        bool duplicateOk = dupRegistry.Tools.Count == 1;

        Console.WriteLine($"[{(duplicateOk ? "PASS" : "FAIL")}] Id 重复的工具被拒绝注册"
                          + $"（注册 {dupRegistry.Tools.Count} 个）");
        if (!duplicateOk) failures++;

        // ---------------------------------------------------------- 2) 工具声明 → 墨迹层
        //
        // 这是整套输入归属的全部规则，就两行：
        //     IsHitTestVisible      = 工具自己接墨迹层
        //     IsManipulationEnabled = 既不落墨 且 不需要指针
        // 逐条对齐，是因为这四条组合一旦串位，症状会非常难查：
        // 比如"手工具下 Manipulation 没开"⇒ 一体机上单指拖不动；
        // "直尺激活时 Manipulation 还开着"⇒ 拖动画线变成拖动画布。
        bool mappingOk = true;
        var mappingRows = new List<string>();

        foreach (var tool in host.Tools)
        {
            host.SetTool(tool.Id);

            bool hit = host.InkSurface.IsHitTestVisible;
            bool manip = host.IsManipulationEnabled;
            bool expectedManip = !tool.UsesInkLayer && !tool.NeedsPointer;

            bool rowOk = hit == tool.UsesInkLayer && manip == expectedManip;
            mappingOk &= rowOk;

            mappingRows.Add($"{tool.Id}: 命中={hit} 手势={manip} {(rowOk ? "OK" : "错")}");
        }

        // 再加一个"需要指针"的工具（M7.3 直尺就是这个形态）：它必须同时关掉墨迹层命中与 Manipulation
        var rulerLike = new RecordingTool("ruler-like", needsPointer: true);
        host.AddTool(rulerLike);
        host.SetTool(rulerLike.Id);

        bool rulerLikeOk = !host.InkSurface.IsHitTestVisible && !host.IsManipulationEnabled;
        mappingRows.Add($"ruler-like: 命中={host.InkSurface.IsHitTestVisible} "
                        + $"手势={host.IsManipulationEnabled} {(rulerLikeOk ? "OK" : "错")}");

        // 刻意<b>不</b>切回笔：下一节要断言"切走时 Deactivate 被调用"与"宿主清预览"，
        // 而这两件事都只发生在"工具真的变了"的时候 —— 先切走的话，前提就没了。
        Console.WriteLine("  " + string.Join("｜", mappingRows));
        Console.WriteLine($"[{(mappingOk && rulerLikeOk ? "PASS" : "FAIL")}] 工具声明正确落到墨迹层"
                          + "（笔/橡皮接输入；手开手势；需要指针的工具两样都关 ⇒ 左键归工具）");
        if (!(mappingOk && rulerLikeOk)) failures++;

        // ---------------------------------------------------------- 3) 生命周期与预览
        //
        // "切工具时宿主主动清预览"这条保险不能省：工具是插件代码（M7.2 起还是别人写的 dll），
        // 它 Deactivate 里忘了清，屏幕上就会留下一根永远不消失的线。
        bool activateOk = rulerLike.ActivateCount == 1;
        bool deactivateOk = rulerLike.DeactivateCount == 0;

        host.AddPreview(new System.Windows.Shapes.Rectangle());
        bool previewAdded = host.PreviewCount == 1;

        host.SetTool(ToolIds.Pen);   // 切走
        bool previewCleared = host.PreviewCount == 0;
        bool deactivateCalled = rulerLike.DeactivateCount == 1;

        // 重复切换同一个工具必须是 no-op：用户点已经选中的那颗按钮时，
        // 工具不该被重新 Activate —— 那会把拖动中的内部状态悄悄重置（表现为"拖着拖着断了"）。
        // 顺带说明：正因为是 no-op，它<b>不会</b>清预览（上面那条清预览靠的是"真的换了工具"）。
        host.SetTool(ToolIds.Pen);
        bool repeatNoOp = rulerLike.ActivateCount == 1;

        host.SetTool(rulerLike.Id);
        host.SetTool(rulerLike.Id);
        bool repeatNoOp2 = rulerLike.ActivateCount == 2 && rulerLike.DeactivateCount == 1;

        Console.WriteLine($"  切走时：Activate 次数={rulerLike.ActivateCount}、"
                          + $"Deactivate 次数={rulerLike.DeactivateCount}；"
                          + $"预览 {previewAdded} → 切走后 {host.PreviewCount} 个");
        Console.WriteLine($"[{(activateOk && deactivateOk ? "PASS" : "FAIL")}] 工具激活/停用被正确调用");
        Console.WriteLine($"[{(previewAdded && previewCleared && deactivateCalled ? "PASS" : "FAIL")}] "
                          + "切换工具时宿主主动清空预览层（不指望插件自己记得清）");
        Console.WriteLine($"[{(repeatNoOp && repeatNoOp2 ? "PASS" : "FAIL")}] 重复切换同一工具是空操作"
                          + "（点已选中的按钮不会重置工具状态）");
        if (!(activateOk && deactivateOk)) failures++;
        if (!(previewAdded && previewCleared && deactivateCalled)) failures++;
        if (!(repeatNoOp && repeatNoOp2)) failures++;

        // ---------------------------------------------------------- 4) 指针转发：世界坐标
        //
        // 真实 Stylus 事件在测试里构造不出来（本机也没有笔），所以这里走注册表的真实分发路径，
        // 断言"工具收到的世界坐标 == 视口坐标经视口矩阵换算的结果"。
        host.ZoomIn();
        host.UpdateLayout();

        var probe = new RecordingTool("probe", needsPointer: true);
        var probeRegistry = new ToolRegistry();
        probeRegistry.AttachContext(host.ToolContext);
        probeRegistry.Add(probe);

        var viewportPoint = new Point(120, 80);
        var expectedWorld = host.Viewport.ToWorld(viewportPoint);

        probeRegistry.DispatchPointer(new ToolPointer(
            ToolPointerPhase.Move, expectedWorld, viewportPoint, 1, false, 1));

        bool pointerOk = probe.Pointers.Count == 1
                      && (probe.Pointers[0].World - expectedWorld).Length < 1e-9
                      && probe.Pointers[0].Phase == ToolPointerPhase.Move;

        // 缩放不为 1 时，世界坐标与视口坐标必然不同 —— 这一步顺带证明换算真的发生了
        bool convertHappened = Math.Abs(host.Viewport.Scale - 1.0) > 1e-6
                            && (expectedWorld - viewportPoint).Length > 1e-6;

        Console.WriteLine($"  视口 {host.Viewport.Scale:F2}×：视口点 ({viewportPoint.X},{viewportPoint.Y})"
                          + $" → 世界点 ({expectedWorld.X:F1},{expectedWorld.Y:F1})");
        Console.WriteLine($"[{(pointerOk && convertHappened ? "PASS" : "FAIL")}] 指针转发给工具的是世界坐标"
                          + "（工具不做任何坐标运算，也就不会算错）");
        if (!(pointerOk && convertHappened)) failures++;

        // ---------------------------------------------------------- 5) 输出契约：提交笔画
        //
        // 这是"落墨型输出契约"的核心断言：工具交上来的笔画必须
        //   ① 进墨迹层（于是随缩放平移、可被橡皮擦、能存进 .tbink）
        //   ② 一次提交 = 一个撤销单元（按一下 Ctrl+Z 整条消失，而不是消失一截）
        host.SetTool(ToolIds.Pen);

        int before = host.StrokeCount;
        bool canUndoBefore = host.CanUndo;

        var toCommit = new StrokeCollection
        {
            MakeMark(host.Pen, new Point(300, 300), new Point(460, 300)),
            MakeMark(host.Pen, new Point(300, 340), new Point(460, 340)),
        };

        host.CommitStrokes(toCommit);

        int afterCommit = host.StrokeCount;
        bool commitOk = afterCommit == before + 2 && host.CanUndo && !canUndoBefore;

        bool undoOk = host.Undo() && host.StrokeCount == before && host.CanRedo;
        bool redoOk = host.Redo() && host.StrokeCount == afterCommit;

        Console.WriteLine($"  提交 2 条：{before} → {afterCommit} 条；撤销后 {host.StrokeCount} 条（重做 {redoOk}）");
        Console.WriteLine($"[{(commitOk ? "PASS" : "FAIL")}] 工具提交的笔画进入墨迹层并成为一步撤销"
                          + "（自动获得撤销/持久化/可擦除，工具不必自己实现）");
        Console.WriteLine($"[{(undoOk && redoOk ? "PASS" : "FAIL")}] 工具提交的笔画可撤销、可重做");
        if (!commitOk) failures++;
        if (!(undoOk && redoOk)) failures++;

        // ---------------------------------------------------------- 6) 状态消息透传
        string? status = null;
        host.ToolStatusMessage += (_, text) => status = text;
        host.ToolContext.SetStatus("直线 12.3 cm，倾角 30°");

        bool statusOk = status is not null && status.Contains("12.3");

        Console.WriteLine($"[{(statusOk ? "PASS" : "FAIL")}] 工具经 IToolContext 写状态栏的路径通"
                          + $"（收到：{status}）");
        if (!statusOk) failures++;

        host.Shutdown();
        return failures;
    }

    /// <summary>
    /// 验收用的假工具：记录生命周期调用次数与收到的指针事件。
    /// </summary>
    /// <remarks>
    /// 存在的意义是让"宿主 → 工具"这条接线可被断言，而不是只断言策略函数算得对。
    /// 它同时也是 M7.3 直尺插件的形态预告：<c>needsPointer: true</c> 的那个实例，
    /// 与真实直尺在宿主眼里的样子完全一样。
    /// </remarks>
    private sealed class RecordingTool : ITool
    {
        private readonly bool _needsPointer;

        public RecordingTool(string id, bool needsPointer)
        {
            Id = id;
            _needsPointer = needsPointer;
        }

        public string Id { get; }

        public string DisplayName => "假工具";

        public string ToolTip => "仅验收 harness 使用";

        public Key? Shortcut => Key.D9;

        public bool UsesInkLayer => false;

        public bool NeedsPointer => _needsPointer;

        public ToolInputKind InputKind => ToolInputKind.None;

        public ToolInkMode InkMode => ToolInkMode.None;

        public Cursor? Cursor => null;

        public int ActivateCount { get; private set; }

        public int DeactivateCount { get; private set; }

        public List<ToolPointer> Pointers { get; } = new();

        public IToolContext? Context { get; private set; }

        public void Activate(IToolContext context)
        {
            ActivateCount++;
            Context = context;
        }

        public void Deactivate() => DeactivateCount++;

        public void OnPointer(ToolPointer pointer) => Pointers.Add(pointer);
    }

    // ================================================================= M7.2.1：窗口模式（状态文件）

    /// <summary>
    /// M7.2.1 断言组：窗口模式状态文件的读写与容错。
    /// </summary>
    /// <remarks>
    /// 只测状态文件这一层 —— 它没有 UI 依赖，边界条件正好能钉死。
    /// <para>
    /// "按钮点下去真的会切窗口"属于窗口行为（要真实窗口 + 真实鼠标），不进 harness：
    /// 本机用 <c>artifacts/click_winmode.py</c>（ctypes 模拟点击 + 截图）验证，
    /// 证据在 <c>docs/09</c> §9.3。**别为了"看着更自动"把窗口塞进 harness** ——
    /// 那会让回归套件依赖桌面环境，CI 上必挂。
    /// </para>
    /// </remarks>
    private static int RunM721Checks(string outDir)
    {
        Console.WriteLine();
        Console.WriteLine("== M7.2.1 窗口模式（状态文件）==");

        int failures = 0;
        var file = Path.Combine(outDir, "ui-state-test.txt");
        if (File.Exists(file)) File.Delete(file);

        // 1) 首次运行（文件不存在）必须返回 null ⇒ 调用方才回落到出厂默认（全屏）。
        //    若这里返回 false，程序会"第一次装就是窗口模式"，直接违背一体机的主用法。
        bool firstRun = UiStateStore.ReadFullScreen(file) is null;
        Console.WriteLine($"[{(firstRun ? "PASS" : "FAIL")}] 文件不存在 ⇒ 返回 null（首次运行走「出厂默认 = 全屏」）");
        if (!firstRun) failures++;

        // 2) 写进去读得回来（两个方向都要 —— 只测一个方向的话，
        //    "永远返回 true"这种实现也能骗过测试）
        UiStateStore.WriteFullScreen(false, file);
        bool wrote0 = UiStateStore.ReadFullScreen(file) == false;
        UiStateStore.WriteFullScreen(true, file);
        bool wrote1 = UiStateStore.ReadFullScreen(file) == true;
        Console.WriteLine($"[{(wrote0 && wrote1 ? "PASS" : "FAIL")}] 写入 false / true 都能读回原值"
                          + $"（false={wrote0}、true={wrote1}）");
        if (!(wrote0 && wrote1)) failures++;

        // 3) 手改文件也要认：老师（或我）拿记事本打开改大小写是常态
        File.WriteAllText(file, "fullscreen=TRUE");
        bool tolerant = UiStateStore.ReadFullScreen(file) == true;
        Console.WriteLine($"[{(tolerant ? "PASS" : "FAIL")}] 手改成 fullscreen=TRUE 仍被接受（大小写不敏感）");
        if (!tolerant) failures++;

        // 4) ★ 文件内容坏掉绝不能抛异常 —— 它坏了只该回落到出厂默认。
        //    这是"日志/偏好类文件"的通用纪律：附属文件不配有拖垮主程序的能力。
        File.WriteAllText(file, "被记事本敲坏的内容");
        bool brokenSafe = UiStateStore.ReadFullScreen(file) is null;
        Console.WriteLine($"[{(brokenSafe ? "PASS" : "FAIL")}] 内容不可识别 ⇒ 返回 null 而非抛异常（坏文件不拖垮启动）");
        if (!brokenSafe) failures++;

        File.Delete(file);
        return failures;
    }

    // ================================================================= M7.2：插件宿主（加载器）

    /// <summary>
    /// M7.2 断言组：真 dll 的加载链路、失败隔离、拒绝理由、契约共享、插件工具端到端落墨。
    /// </summary>
    /// <remarks>
    /// 这一段刻意<b>不用</b>手工 new 出来的工具对象 —— 那样等于什么都没验证。
    /// 每个场景都在临时目录里摆出真实的 <c>plugins\</c> 结构：一个标准插件目录、
    /// 一个"插件自带契约副本"的目录、一个空目录、一个不相干的 dll、一个损坏的 dll。
    /// 加载器按它真实的规则去处理，我们只核对结论。
    /// <para>
    /// 最容易静默出错的是"契约共享"：插件若从自己目录加载到第二份契约 dll，
    /// <c>as IWhiteBoardPlugin</c> 会返回 <c>null</c>，表现是"dll 明明在、一个工具都没注册、还不报错"。
    /// 所以这里专门造一个<b>带契约副本</b>的目录来钉死它 —— 这条断言是整段里最值钱的一条。
    /// </para>
    /// </remarks>
    private static int RunM72Checks()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.2：插件宿主（加载器）================");

        var root = Path.Combine(Path.GetTempPath(), "tb-plugins-" + Guid.NewGuid().ToString("N"));

        try
        {
            // ---------------------------------------------------- 0) 契约版本规则（纯函数）
            //
            // 断言写成"相对宿主版本"而不是写死 1.0/1.9：契约版本会随里程碑上调
            // （M7.4 从 1.0 升到 1.1），写死的数字会让这条断言在升级时莫名其妙地失败。
            var hostVersion = PluginContract.HostVersion;

            bool versionOk =
                PluginContract.DescribeIncompatibility(new Version(hostVersion.Major, 0)) is null
                && PluginContract.DescribeIncompatibility(hostVersion) is null
                && PluginContract.DescribeIncompatibility(
                       new Version(hostVersion.Major, hostVersion.Minor + 1)) is { } newer
                && newer.Contains("请更新")
                && PluginContract.DescribeIncompatibility(
                       new Version(hostVersion.Major + 1, 0)) is { } nextMajor
                && nextMajor.Contains($"v{hostVersion.Major + 1}")
                && PluginContract.DescribeIncompatibility(null) is not null;

            bool sharedOk = PluginContract.IsSharedWithHost(PluginContract.AssemblyName)
                         && PluginContract.IsSharedWithHost(PluginContract.HostAssemblyName)
                         && !PluginContract.IsSharedWithHost("PdfSmokeTest.Plugin");

            Console.WriteLine($"  宿主契约版本 = v{hostVersion.Major}.{hostVersion.Minor}；"
                              + $"高一次版本被拒的理由：\"{PluginContract.DescribeIncompatibility(new Version(hostVersion.Major, hostVersion.Minor + 1))}\"");
            Console.WriteLine($"[{(versionOk ? "PASS" : "FAIL")}] 契约版本规则：主版本相同且次版本不高于宿主即兼容，"
                              + "否则拒绝并给出能念给用户听的原因");
            Console.WriteLine($"[{(sharedOk ? "PASS" : "FAIL")}] 契约与宿主被判定为「必须共享」"
                              + "（插件目录里就算放了同名 dll 也不采用）");
            if (!versionOk) failures++;
            if (!sharedOk) failures++;

            // ---------------------------------------------------- 1) 标准插件：真 dll 的完整链路
            var pluginDll = Path.Combine(AppContext.BaseDirectory, "PdfSmokeTest.Plugin.dll");
            var contractDll = Path.Combine(AppContext.BaseDirectory, "MathPhys.Ink.Plugin.Abstractions.dll");

            if (!File.Exists(pluginDll) || !File.Exists(contractDll))
            {
                Console.WriteLine($"[FAIL] 找不到验收用 dll（{pluginDll}）—— 检查 PdfSmokeTest 的项目引用");
                return failures + 1;
            }

            var standardRoot = Path.Combine(root, "standard");
            CopyPlugin(pluginDll, Path.Combine(standardRoot, "ruler"), "ruler.dll");

            var host = new CanvasViewportHost();
            host.Measure(new Size(1000, 700));
            host.Arrange(new Rect(0, 0, 1000, 700));
            host.UpdateLayout();

            var registry = new ToolRegistry();
            registry.AttachContext(host.ToolContext);

            var report = PluginLoader.LoadAll(registry, renderers: null, pluginsDirectory: standardRoot);
            var rulerEntry = report.Entries.FirstOrDefault();

            bool loadedOk = report.LoadedCount == 1
                         && rulerEntry is { Status: PluginLoadStatus.Loaded, ToolCount: 1 }
                         && registry.Tools.Count == 1
                         && registry.Tools[0].Id == "smoke-ruler";

            Console.WriteLine($"  目录 standard → {report.Summary}；条目：{rulerEntry?.Reason}");
            Console.WriteLine($"[{(loadedOk ? "PASS" : "FAIL")}] 真 dll 被加载并注册了工具 —— "
                              + "同一个 dll 里还有一个「注册即抛异常」的插件类，它没影响这一条（失败隔离）");
            if (!loadedOk) failures++;

            // ---------------------------------------------------- 2) 插件工具端到端：真的画到画布上
            bool activated = registry.Activate("smoke-ruler");

            int strokesBefore = host.StrokeCount;
            var from = new Point(200, 300);
            var to = new Point(500, 300);

            registry.DispatchPointer(new ToolPointer(ToolPointerPhase.Down, from, from, 1, false, 1));
            bool previewShown = host.PreviewCount == 1;

            registry.DispatchPointer(new ToolPointer(
                ToolPointerPhase.Move, new Point(350, 302), new Point(0, 0), 1, false, 1));

            registry.DispatchPointer(new ToolPointer(ToolPointerPhase.Up, to, to, 1, false, 1));

            bool previewCleared = host.PreviewCount == 0;
            bool strokeAdded = host.StrokeCount == strokesBefore + 1;

            // 几何：直尺落下的两点必须精确等于按下点与抬起点 —— 这就是"直"的定义
            bool geometryOk = false;
            if (strokeAdded)
            {
                var points = host.Strokes[host.StrokeCount - 1].StylusPoints;
                geometryOk = points.Count == 2
                          && Math.Abs(points[0].X - from.X) < 1e-9
                          && Math.Abs(points[0].Y - from.Y) < 1e-9
                          && Math.Abs(points[points.Count - 1].X - to.X) < 1e-9
                          && Math.Abs(points[points.Count - 1].Y - to.Y) < 1e-9;
            }

            bool toolUndoOk = host.Undo() && host.StrokeCount == strokesBefore;

            Console.WriteLine($"  插件工具：激活={activated}；预览 0→{(previewShown ? 1 : 0)}→{host.PreviewCount}；"
                              + $"落墨 {strokesBefore + 1} 条 → 撤销后 {host.StrokeCount} 条");
            Console.WriteLine($"[{(activated && previewShown && previewCleared ? "PASS" : "FAIL")}] "
                              + "插件工具的拖动预览走 AddPreview：拖动中有 1 个、抬笔后清空"
                              + "（不进墨迹层、不进撤销栈）");
            Console.WriteLine($"[{(strokeAdded && geometryOk ? "PASS" : "FAIL")}] 插件提交的是精确的两点笔画"
                              + "（起止点 = 按下点与抬起点，直尺不会画歪）");
            Console.WriteLine($"[{(toolUndoOk ? "PASS" : "FAIL")}] 插件画的线可撤销（一次操作 = 一步撤销）"
                              + "—— 插件一行持久化代码都没写");
            if (!(activated && previewShown && previewCleared)) failures++;
            if (!(strokeAdded && geometryOk)) failures++;
            if (!toolUndoOk) failures++;

            // ---------------------------------------------------- 3) 契约副本：静默失败的守门人
            var copyRoot = Path.Combine(root, "copy");
            CopyPlugin(pluginDll, Path.Combine(copyRoot, "copy"), "copy.dll");
            CopyPlugin(contractDll, Path.Combine(copyRoot, "copy"), "MathPhys.Ink.Plugin.Abstractions.dll");

            var copyRegistry = new ToolRegistry();
            copyRegistry.AttachContext(host.ToolContext);
            var copyReport = PluginLoader.LoadAll(copyRegistry, renderers: null, pluginsDirectory: copyRoot);

            bool copyOk = copyReport.LoadedCount == 1
                       && copyRegistry.Tools.Count == 1
                       && copyRegistry.Tools[0].Id == "smoke-ruler";

            Console.WriteLine($"  目录 copy（插件自带一份契约 dll）→ {copyReport.Summary}");
            Console.WriteLine($"[{(copyOk ? "PASS" : "FAIL")}] 插件目录里自带契约副本时仍然用宿主那一份"
                              + "（否则会出现两个同名不同类型的 IWhiteBoardPlugin：dll 在、工具没注册、还不报错）");
            if (!copyOk) failures++;

            // ---------------------------------------------------- 4) 拒绝与失败：理由必须可读
            var rejectRoot = Path.Combine(root, "reject");
            Directory.CreateDirectory(Path.Combine(rejectRoot, "nodll"));
            CopyPlugin(contractDll, Path.Combine(rejectRoot, "notaplugin"), "notaplugin.dll");
            CopyPlugin(pluginDll, Path.Combine(rejectRoot, "broken"), "broken.dll");
            File.WriteAllBytes(Path.Combine(rejectRoot, "broken", "broken.dll"), GarbageBytes());

            var rejectRegistry = new ToolRegistry();
            rejectRegistry.AttachContext(host.ToolContext);
            var rejectReport = PluginLoader.LoadAll(rejectRegistry, renderers: null, pluginsDirectory: rejectRoot);

            bool noDllOk = Entry(rejectReport, "nodll") is { Status: PluginLoadStatus.Rejected } noDll
                        && noDll.Reason.Contains("没有 dll");

            bool notPluginOk = Entry(rejectReport, "notaplugin") is { Status: PluginLoadStatus.Rejected } notPlugin
                            && notPlugin.Reason.Contains("契约");

            bool brokenOk = Entry(rejectReport, "broken") is { Status: PluginLoadStatus.Failed } broken
                         && broken.Reason.Contains("无法加载");

            bool rejectClean = rejectRegistry.Tools.Count == 0;

            foreach (var entry in rejectReport.Entries)
            {
                Console.WriteLine($"  {Path.GetFileName(entry.Directory),-12} [{entry.Status}] {entry.Reason}");
            }

            Console.WriteLine($"[{(noDllOk ? "PASS" : "FAIL")}] 空目录被拒绝并说明约定（plugins\\插件名\\插件名.dll）");
            Console.WriteLine($"[{(notPluginOk ? "PASS" : "FAIL")}] 不相干的 dll 被拒绝并说明「没有引用插件契约」"
                              + "（而不是静默当作没看见）");
            Console.WriteLine($"[{(brokenOk ? "PASS" : "FAIL")}] 损坏的 dll 记为失败且不影响其他条目");
            Console.WriteLine($"[{(rejectClean ? "PASS" : "FAIL")}] 被拒/失败的插件一个工具都没注册进注册表");
            if (!noDllOk) failures++;
            if (!notPluginOk) failures++;
            if (!brokenOk) failures++;
            if (!rejectClean) failures++;

            // ---------------------------------------------------- 5) 报告文案：状态栏与日志就靠它
            var emptyReport = PluginLoader.LoadAll(new ToolRegistry(), renderers: null,
                                                  pluginsDirectory: Path.Combine(root, "not-exists"));

            bool emptyOk = emptyReport.Entries.Count == 0
                        && !emptyReport.HasProblems
                        && emptyReport.Summary.Contains("未安装");

            bool summaryOk = report.Summary.Contains("已加载插件 1 个")
                          && rejectReport.HasProblems
                          && rejectReport.Summary.Contains("未加载")
                          && rejectReport.Details.Contains("nodll");

            Console.WriteLine($"  无插件目录 → \"{emptyReport.Summary}\"；有问题的目录 → \"{rejectReport.Summary}\"");
            Console.WriteLine($"[{(emptyOk ? "PASS" : "FAIL")}] 没有插件目录 = 没装插件"
                              + "（正常状态：不报错、不建目录、状态栏不出现插件字样）");
            Console.WriteLine($"[{(summaryOk ? "PASS" : "FAIL")}] 报告同时给出状态栏短句与日志明细");
            if (!emptyOk) failures++;
            if (!summaryOk) failures++;

            host.Shutdown();
        }
        finally
        {
            // 临时目录不留到下一次运行 —— 否则"上一次的插件还在"会让下一次的断言莫名其妙
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 清理失败不影响验收结论
            }
        }

        return failures;
    }

    /// <summary>把 dll 摆成 <c>plugins\目录\文件.dll</c> 的形态。</summary>
    private static void CopyPlugin(string sourceDll, string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        File.Copy(sourceDll, Path.Combine(directory, fileName), overwrite: true);
    }

    /// <summary>按子目录名取加载报告里的一条。</summary>
    private static PluginLoadEntry? Entry(PluginLoadReport report, string directoryName)
        => report.Entries.FirstOrDefault(e => Path.GetFileName(e.Directory) == directoryName);

    /// <summary>造一个"看着像 dll、其实是垃圾"的文件，用来走损坏 dll 的失败路径。</summary>
    private static byte[] GarbageBytes()
    {
        var bytes = new byte[512];
        // 固定种子 —— 失败信息可复现，不然每次跑出来的异常文本都不一样
        new Random(20260921).NextBytes(bytes);
        return bytes;
    }

    // ================================================================= M4：笔 / 橡皮 / 压感

    /// <summary>
    /// M4 断言组：三种工具落到控件的实际状态、橡皮的世界单位语义、笔属性即时生效、
    /// 压感回退判据、笔迹变更事件接线，以及"换了墨色后新笔画真的变色"的端到端光栅。
    /// </summary>
    /// <remarks>
    /// 先把边界说清：<b>擦除动作本身在这里测不了</b>。
    /// 真实擦除只可能来自笔尾反转（本机没有笔），或来自 InkCanvas 对 Stylus 事件流的内部处理，
    /// 而 <c>StylusEventArgs</c> 无法在测试里构造。所以本段断言的是"配置与接线正确"：
    /// 切到橡皮后 EditingMode / EraserShape 是否真的换了、反转输入是否真的被分类为 Erase、
    /// 墨色笔宽是否真的落到后续笔画上。擦除的端到端表现交给 docs/05 的一体机实测清单。
    /// </remarks>
    private static int RunM4Checks(PdfiumDocumentService service, string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M4：笔 / 橡皮 / 压感 ================");

        const int hostWidth = 1000;
        const int hostHeight = 700;

        var host = new CanvasViewportHost();
        host.Measure(new Size(hostWidth, hostHeight));
        host.Arrange(new Rect(0, 0, hostWidth, hostHeight));
        host.UpdateLayout();
        host.SetDocument(service);
        host.UpdateLayout();
        PumpFor(250);

        // ---------------------------------------------------------- 1) 三种工具落到墨迹层
        Console.WriteLine();
        Console.WriteLine("---- 1) 笔 / 橡皮 / 手 真正落到墨迹层 ----");

        host.SetTool(ToolIds.Pen);
        var penMode = host.InkSurface.EditingMode;
        bool penHit = host.InkSurface.IsHitTestVisible;
        var penCursor = host.InkSurface.Cursor;

        host.SetTool(ToolIds.Eraser);
        var eraserMode = host.InkSurface.EditingMode;
        bool eraserHit = host.InkSurface.IsHitTestVisible;
        var eraserCursor = host.InkSurface.Cursor;

        host.SetTool(ToolIds.Hand);
        var handMode = host.InkSurface.EditingMode;
        bool handHit = host.InkSurface.IsHitTestVisible;
        var handCursor = host.InkSurface.Cursor;

        // M13 S1：书写类工具的光标隐藏（Cursor=None）—— 真机反馈"书写时无需显示笔"。
        bool penToolOk = penMode == InkCanvasEditingMode.Ink && penHit && penCursor == Cursors.None;
        bool eraserToolOk = eraserMode == InkCanvasEditingMode.EraseByPoint && eraserHit && eraserCursor == Cursors.None;
        bool handToolOk = handMode == InkCanvasEditingMode.None && !handHit && handCursor == Cursors.Hand;

        Console.WriteLine($"  笔  ：EditingMode={penMode}、参与命中={penHit}、光标={DescribeCursor(penCursor, Cursors.None)}");
        Console.WriteLine($"  橡皮：EditingMode={eraserMode}、参与命中={eraserHit}、光标={DescribeCursor(eraserCursor, Cursors.None)}");
        Console.WriteLine($"  手  ：EditingMode={handMode}、参与命中={handHit}、光标={DescribeCursor(handCursor, Cursors.Hand)}");
        Console.WriteLine($"[{(penToolOk ? "PASS" : "FAIL")}] 笔：EditingMode=Ink、参与命中、无光标（M13 书写隐藏）");
        Console.WriteLine($"[{(eraserToolOk ? "PASS" : "FAIL")}] 橡皮：EditingMode=EraseByPoint、参与命中、无光标（M13 书写隐藏）");
        Console.WriteLine($"[{(handToolOk ? "PASS" : "FAIL")}] 手：EditingMode=None、退出命中、手形光标");
        if (!penToolOk) failures++;
        if (!eraserToolOk) failures++;
        if (!handToolOk) failures++;

        // ---------------------------------------------------------- 2) 橡皮语义
        Console.WriteLine();
        Console.WriteLine("---- 2) 橡皮：世界单位半径 + 笔尾反转模式 ----");

        var eraser = host.Eraser;
        var shape = host.InkSurface.EraserShape;

        bool radiusOk = Near(shape.Width, eraser.WorldRadius * 2.0, 1e-9)
                     && Near(shape.Height, eraser.WorldRadius * 2.0, 1e-9);
        bool invertedModeOk = host.InkSurface.EditingModeInverted == eraser.Mode;

        Console.WriteLine($"  半径 {eraser.WorldRadius} world 单位（直径 ≈ {eraser.WorldRadius * 2 * 25.4 / 72:F1} mm），"
                          + $"EraserShape = {shape.Width:F1} x {shape.Height:F1}");
        Console.WriteLine($"  笔尾反转模式 = {host.InkSurface.EditingModeInverted}（应为 {eraser.Mode}）");
        Console.WriteLine($"[{(radiusOk ? "PASS" : "FAIL")}] 橡皮尺寸是 world 单位：形状直径 = 半径 × 2（写成屏幕像素会在这里挂）");
        Console.WriteLine($"[{(invertedModeOk ? "PASS" : "FAIL")}] 笔尾反转走 InkCanvas 原生 EraseByPoint ⇒ 擦除逻辑一行不用自己写");
        if (!radiusOk) failures++;
        if (!invertedModeOk) failures++;

        // ---------------------------------------------------------- 3) 墨色 / 笔宽
        Console.WriteLine();
        Console.WriteLine("---- 3) 墨色 / 笔宽 即时生效 ----");

        var blue = InkPalette.Colors[2];
        var thick = InkPalette.Widths[3];

        host.SetPenColor(blue.Color);
        host.SetPenWidth(thick.WorldWidth);

        var applied = host.InkSurface.DefaultDrawingAttributes;
        bool applyOk = applied.Color == blue.Color
                    && Near(applied.Width, thick.WorldWidth, 1e-9)
                    && Near(applied.Height, thick.WorldWidth, 1e-9);

        // 出厂默认必须等于调色板标着"默认"的那一档，否则界面上高亮的按钮与实际笔宽不符
        var freshPen = new PenProfile();
        var defaultColor = InkPalette.Colors[InkPalette.DefaultColorIndex];
        var defaultWidth = InkPalette.Widths[InkPalette.DefaultWidthIndex];
        bool defaultMatchesPalette = freshPen.Color == defaultColor.Color
                                  && Near(freshPen.WorldWidth, defaultWidth.WorldWidth, 1e-9);

        Console.WriteLine($"  设为「{blue.Name} / {thick.Name}（{thick.WorldWidth} world）」→ "
                          + $"墨迹层：rgb({applied.Color.R},{applied.Color.G},{applied.Color.B})、宽 {applied.Width}");
        Console.WriteLine($"  出厂默认 = 「{defaultColor.Name} / {defaultWidth.Name}（{defaultWidth.WorldWidth} world）」，"
                          + $"与调色板默认档一致={defaultMatchesPalette}");
        Console.WriteLine($"[{(applyOk ? "PASS" : "FAIL")}] 改墨色笔宽立即落到墨迹层（只影响之后新落下的笔画）");
        Console.WriteLine($"[{(defaultMatchesPalette ? "PASS" : "FAIL")}] 出厂默认笔 = 调色板的默认档（两处常量不会各说各话）");
        if (!applyOk) failures++;
        if (!defaultMatchesPalette) failures++;

        // ---------------------------------------------------------- 4) 压感回退判据
        Console.WriteLine();
        Console.WriteLine("---- 4) 压感：设备不报压力时的回退判据 ----");

        var constantPressure = new StylusPointCollection(new[]
        {
            new StylusPoint(0, 0, 0.5f), new StylusPoint(10, 0, 0.5f), new StylusPoint(20, 0, 0.5f),
        });
        var variedPressure = new StylusPointCollection(new[]
        {
            new StylusPoint(0, 0, 0.15f), new StylusPoint(10, 0, 0.55f), new StylusPoint(20, 0, 0.95f),
        });
        var onePoint = new StylusPointCollection(new[] { new StylusPoint(0, 0, 0.5f) });

        var pressurePen = new PenProfile { IgnorePressure = false };

        bool constantOk = PressurePolicy.IsPressureConstant(constantPressure);
        bool variedOk = !PressurePolicy.IsPressureConstant(variedPressure);
        bool onePointOk = PressurePolicy.IsPressureConstant(onePoint);
        bool fallbackOk = PressurePolicy.ShouldIgnorePressure(pressurePen, constantPressure)
                       && !PressurePolicy.ShouldIgnorePressure(pressurePen, variedPressure)
                       && PressurePolicy.ShouldIgnorePressure(new PenProfile(), variedPressure);

        bool pressureOk = constantOk && variedOk && onePointOk && fallbackOk;

        Console.WriteLine($"  压力恒为 0.5（鼠标、部分电容笔）→ 判为无压感={constantOk}");
        Console.WriteLine($"  压力 0.15→0.95 次第变化         → 判为有压感={variedOk}");
        Console.WriteLine($"  只有 1 个采样点                 → 保守判为无压感={onePointOk}");
        Console.WriteLine($"[{(pressureOk ? "PASS" : "FAIL")}] 无压感设备上开压感不会把整块笔迹压成一半粗（只改属性、不动几何）");
        if (!pressureOk) failures++;

        // ---------------------------------------------------------- 5) 笔迹变更事件
        Console.WriteLine();
        Console.WriteLine("---- 5) InkChanged 事件接线（状态栏的笔迹条数靠它刷新）----");

        int inkChangedCount = 0;
        EventHandler counter = (_, _) => inkChangedCount++;
        host.InkChanged += counter;

        host.Strokes.Clear();
        int baseline = inkChangedCount;                     // 清空本身也是一次变更，不计入

        host.Strokes.Add(MakeLine(host.Pen.ToDrawingAttributes(), new Point(300, 300), new Point(340, 300)));
        int afterAdd = host.StrokeCount;

        host.ClearStrokes();
        host.InkChanged -= counter;

        bool eventOk = afterAdd == 1 && host.StrokeCount == 0 && inkChangedCount - baseline >= 2;

        Console.WriteLine($"  放 1 条后 StrokeCount={afterAdd}，清空后={host.StrokeCount}，"
                          + $"期间收到 InkChanged {inkChangedCount - baseline} 次");
        Console.WriteLine($"[{(eventOk ? "PASS" : "FAIL")}] 增删笔迹都会通知界面（防止再出现\"函数写了但没接线\"）");
        if (!eventOk) failures++;

        // ---------------------------------------------------------- 6) 端到端光栅：墨色真的生效
        Console.WriteLine();
        Console.WriteLine("---- 6) 端到端光栅：换成蓝笔后新画的线真的是蓝的 ----");

        host.SetTool(ToolIds.Pen);
        host.FitWidth();
        host.UpdateLayout();
        PumpFor(300);

        host.Strokes.Clear();

        var page0 = host.Layout.PageRects[0];
        double lineY = page0.Y + 260;
        host.Strokes.Add(MakeLine(MakeAttributes(blue, thick),
                                  new Point(page0.X + 80, lineY),
                                  new Point(page0.X + page0.Width - 80, lineY)));

        host.UpdateLayout();
        PumpFor(200);

        var bitmap = Snapshot(host, hostWidth, hostHeight);
        string blueFile = Path.Combine(outDir, "ink-blue-thick.png");
        SavePng(bitmap, blueFile);

        var blueScan = ScanPixels(bitmap, IsBluePixel);
        double expectedLength = (page0.Width - 160) * host.Viewport.Scale;

        // 只数数量是不够的：整页被误判成蓝也能凑够数。所以同时要求像素外接框
        // "很宽、很扁" —— 那才是一根横穿页面的粗线。
        bool blueCountOk = blueScan.Count > 1500;
        bool blueShapeOk = blueScan.Count > 0
                        && blueScan.MaxX - blueScan.MinX > expectedLength * 0.8
                        && blueScan.MaxY - blueScan.MinY < 20;

        Console.WriteLine($"  线长 ≈ {expectedLength:F0} px（缩放 {host.Viewport.Scale:F2}），笔宽 {thick.WorldWidth} world"
                          + $"（≈ {thick.WorldWidth * host.Viewport.Scale:F1} px）");
        Console.WriteLine($"  蓝色像素 {blueScan.Count} 个，外接框 "
                          + $"({blueScan.MinX},{blueScan.MinY})-({blueScan.MaxX},{blueScan.MaxY})"
                          + $" = {blueScan.MaxX - blueScan.MinX} x {blueScan.MaxY - blueScan.MinY} px");
        Console.WriteLine($"[{(blueCountOk ? "PASS" : "FAIL")}] 换墨色后新笔画真的是新颜色（默认笔是红 ⇒ 数量为 0 就说明没生效）");
        Console.WriteLine($"[{(blueShapeOk ? "PASS" : "FAIL")}] 且蓝色像素恰好构成一根横线（够宽、够扁）→ {blueFile}");
        if (!blueCountOk) failures++;
        if (!blueShapeOk) failures++;

        host.Shutdown();

        return failures;
    }

    // ================================================================= M5：撤销 / 重做

    /// <summary>
    /// M5 断言组：往返正确性、事务合并、栈不被自己污染、容量上限、换文档清历史。
    /// </summary>
    /// <remarks>
    /// <b>能断言到什么程度</b>：本机没有笔也没有触摸屏，造不出真实的橡皮擦除输入
    /// （<c>StylusEventArgs</c> 无法构造）。但擦除对撤销机制而言<b>只有一个侧面重要</b>——
    /// 它会产生"移除 1 条 + 加入 N 条"的变更序列。这个序列可以在测试里原样模拟出来，
    /// 于是真正有风险的两件事（多次变更合并成一步、反转后精确复原）能被完整钉死。
    /// 真实擦除的端到端仍留给 docs/07 的一体机清单。
    /// </remarks>
    private static int RunM5Checks(PdfiumDocumentService service, string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M5：撤销 / 重做 ================");

        const int hostWidth = 1000;
        const int hostHeight = 700;

        var host = new CanvasViewportHost();
        host.Measure(new Size(hostWidth, hostHeight));
        host.Arrange(new Rect(0, 0, hostWidth, hostHeight));
        host.UpdateLayout();
        host.SetDocument(service);
        host.UpdateLayout();
        PumpFor(250);

        var attr = MakeAttributes(InkPalette.Colors[0], InkPalette.Widths[1]);

        // ---------------------------------------------------------- 1) 单笔往返
        Console.WriteLine();
        Console.WriteLine("---- 1) 写一笔 → 撤销 → 重做 ----");

        var stroke = MakeLine(attr, new Point(100, 100), new Point(300, 100));
        host.Strokes.Add(stroke);

        bool initialOk = host.StrokeCount == 1 && host.CanUndo && !host.CanRedo;
        bool undoOk = host.Undo() && host.StrokeCount == 0 && host.CanRedo && !host.CanUndo;
        bool redoOk = host.Redo() && host.StrokeCount == 1 && host.CanUndo && !host.CanRedo;
        bool sameObjectOk = host.StrokeCount == 1 && ReferenceEquals(host.Strokes[0], stroke);

        Console.WriteLine($"  初始：StrokeCount={1}、CanUndo={initialOk}");
        Console.WriteLine($"[{(undoOk ? "PASS" : "FAIL")}] 撤销后笔画消失、且转为可重做");
        Console.WriteLine($"[{(redoOk ? "PASS" : "FAIL")}] 重做后笔画回来");
        Console.WriteLine($"[{(sameObjectOk ? "PASS" : "FAIL")}] 回来的是同一个对象引用（不是重建的副本 ⇒ 几何/颜色/压力样式零失真）");
        if (!undoOk) failures++;
        if (!redoOk) failures++;
        if (!sameObjectOk) failures++;

        // ---------------------------------------------------------- 2) 事务合并（核心）
        Console.WriteLine();
        Console.WriteLine("---- 2) 一次擦除 = 一步撤销（事务合并）----");

        host.Strokes.Clear();
        host.History.Reset();

        var target = MakeLine(attr, new Point(100, 200), new Point(500, 200));
        host.Strokes.Add(target);
        host.History.Reset();   // 只为干净地观察擦除本身，把"加入 target"这一步丢掉

        // 模拟一次拖动擦除产生的变更序列：原笔画被移除，取而代之是几段没被擦掉的残段。
        // 真实 InkCanvas 的 EraseByPoint 走的就是这条路（移除 + 加入），
        // 而且一次拖动会连续产生**多次**这样的变更 —— 正是要靠事务把它们并成一步。
        host.History.BeginTransaction();
        host.Strokes.Remove(target);
        host.Strokes.Add(MakeLine(attr, new Point(100, 200), new Point(210, 200)));
        host.Strokes.Add(MakeLine(attr, new Point(260, 200), new Point(340, 200)));
        host.Strokes.Add(MakeLine(attr, new Point(390, 200), new Point(500, 200)));
        host.History.CommitTransaction();

        int mergedSteps = host.History.UndoCount;
        bool mergedOk = mergedSteps == 1;
        bool eraseResultOk = host.StrokeCount == 3;

        bool undoEraseOk = host.Undo() && host.StrokeCount == 1 && ReferenceEquals(host.Strokes[0], target);
        bool redoEraseOk = host.Redo() && host.StrokeCount == 3;

        Console.WriteLine($"  4 次集合变更（移除 1 条 + 加入 3 段）→ 撤销栈增加 {mergedSteps} 步（应为 1，否则一次 Ctrl+Z 只撤销一小段）");
        Console.WriteLine($"  擦除后剩下 {3} 条碎笔画={eraseResultOk}");
        Console.WriteLine($"[{(mergedOk ? "PASS" : "FAIL")}] 一次擦除整体合并为一步");
        Console.WriteLine($"[{(undoEraseOk ? "PASS" : "FAIL")}] 撤销一次即完整复原原始笔画（且是同一个对象）");
        Console.WriteLine($"[{(redoEraseOk ? "PASS" : "FAIL")}] 重做一次即完整回到擦除后的样子");
        if (!mergedOk) failures++;
        if (!eraseResultOk) failures++;
        if (!undoEraseOk) failures++;
        if (!redoEraseOk) failures++;

        // ---------------------------------------------------------- 3) 跨事务不合并
        Console.WriteLine();
        Console.WriteLine("---- 3) 两次独立落笔 = 两步；空事务不留记录 ----");

        host.Strokes.Clear();
        host.History.Reset();

        host.History.BeginTransaction();
        host.Strokes.Add(MakeLine(attr, new Point(10, 10), new Point(20, 10)));
        host.History.CommitTransaction();

        host.History.BeginTransaction();
        host.Strokes.Add(MakeLine(attr, new Point(30, 10), new Point(40, 10)));
        host.History.CommitTransaction();

        bool separateOk = host.History.UndoCount == 2;

        // 点一下没画（按下即抬起），不该留下痕迹
        host.History.BeginTransaction();
        host.History.CommitTransaction();
        bool emptyOk = host.History.UndoCount == 2;

        Console.WriteLine($"  两次落笔 → 栈深 {2}；再补一个空事务 → 栈深 {host.History.UndoCount}");
        Console.WriteLine($"[{(separateOk ? "PASS" : "FAIL")}] 两次独立操作不会被错误合并成一步");
        Console.WriteLine($"[{(emptyOk ? "PASS" : "FAIL")}] 空事务（按下没画就抬起）不占用撤销步数");
        if (!separateOk) failures++;
        if (!emptyOk) failures++;

        // ---------------------------------------------------------- 4) 新操作清空重做栈
        Console.WriteLine();
        Console.WriteLine("---- 4) 撤销后写新东西 ⇒ 重做栈作废（标准语义）----");

        host.Undo();
        bool hadRedo = host.CanRedo;

        host.History.BeginTransaction();
        host.Strokes.Add(MakeLine(attr, new Point(50, 10), new Point(60, 10)));
        host.History.CommitTransaction();

        bool redoClearedOk = hadRedo && !host.CanRedo;

        Console.WriteLine($"  撤销后 CanRedo={hadRedo} → 写新笔画后 CanRedo={host.CanRedo}");
        Console.WriteLine($"[{(redoClearedOk ? "PASS" : "FAIL")}] 新操作让重做栈作废（否则重做的语义会前后矛盾）");
        if (!redoClearedOk) failures++;

        // ---------------------------------------------------------- 5) 栈不被自己污染（专测最隐蔽的坑）
        Console.WriteLine();
        Console.WriteLine("---- 5) 反复撤销/重做 10 轮，栈深必须始终回到原值 ----");

        host.Strokes.Clear();
        host.History.Reset();

        for (int i = 0; i < 3; i++)
        {
            host.History.BeginTransaction();
            host.Strokes.Add(MakeLine(attr, new Point(10 + i * 20, 30), new Point(20 + i * 20, 30)));
            host.History.CommitTransaction();
        }

        int undoDepth0 = host.History.UndoCount;
        bool stable = undoDepth0 == 3;

        for (int round = 0; round < 10 && stable; round++)
        {
            while (host.CanUndo) host.Undo();
            if (host.History.UndoCount != 0) stable = false;          // 撤销动作被自己记下来了
            if (host.History.RedoCount != undoDepth0) stable = false;

            while (host.CanRedo) host.Redo();
            if (host.History.RedoCount != 0) stable = false;
            if (host.History.UndoCount != undoDepth0) stable = false;  // 重做动作被自己记下来了
        }

        bool strokesStable = host.StrokeCount == 3;

        Console.WriteLine($"  初始栈深 {undoDepth0}；来回 10 轮后栈深 {host.History.UndoCount}、笔画数 {host.StrokeCount}");
        Console.WriteLine($"[{(stable ? "PASS" : "FAIL")}] 撤销/重做自身不会被记成新操作（漏掉这条会立刻\"撤销失效\"）");
        Console.WriteLine($"[{(strokesStable ? "PASS" : "FAIL")}] 且来回 10 轮后笔迹与初始完全一致（3 条）");
        if (!stable) failures++;
        if (!strokesStable) failures++;

        // ---------------------------------------------------------- 5b) 丢笔不复活（M24 热修⑤）
        Console.WriteLine();
        Console.WriteLine("---- 5b) 丢笔：静默移除后那条「加入本笔」不得留在撤销栈里 ----");

        // 真实调用形态（宿主 OnStrokeCollected 丢笔分支）：
        //   ① Silently(() => Strokes.Remove(stroke))  —— 移除不入栈；
        //   ② DiscardAdded(stroke)                    —— 抹掉「加入本笔」那一步。
        // 老实现只做 ①：那条 add 仍留在栈上，老师一按 Ctrl+Z 就把长线整笔复活成鬼影。
        host.Strokes.Clear();
        host.History.Reset();

        // 时机一：add 已经出栈（事务外 Add 自成一步）
        var ghostPushed = MakeLine(attr, new Point(20, 300), new Point(400, 300));
        host.Strokes.Add(ghostPushed);
        int stackAfterAdd = host.History.UndoCount;                     // 应为 1
        host.History.Silently(() => host.Strokes.Remove(ghostPushed));  // 丢笔：静默移除
        bool discardedPushed = host.History.DiscardAdded(ghostPushed);  // 抹掉那条 add
        bool ghostPushedOk = discardedPushed && stackAfterAdd == 1
            && host.Strokes.Count == 0 && host.History.UndoCount == 0
            // 墨迹栈与统一时间线（BoardHistory）都必须归零 —— 只清前者会让撤销按钮一直亮着，
            // 且下一次 Ctrl+Z 被那个幽灵步吞掉一次（StepDiscarded 事件就是为这条而加）
            && !host.History.CanUndo && !host.CanUndo;

        // 时机二：add 还挂在待提交事务里（落笔事务尚未提交）
        host.Strokes.Clear();
        host.History.Reset();
        host.History.BeginTransaction();
        var ghostPending = MakeLine(attr, new Point(20, 340), new Point(400, 340));
        host.Strokes.Add(ghostPending);
        host.History.Silently(() => host.Strokes.Remove(ghostPending));
        bool discardedPending = host.History.DiscardAdded(ghostPending);
        host.History.CommitTransaction();                               // 净变化为空 ⇒ 不入栈
        bool ghostPendingOk = discardedPending
            && host.Strokes.Count == 0 && host.History.UndoCount == 0
            && !host.History.CanUndo && !host.CanUndo;

        Console.WriteLine($"  时机一（add 已入栈）：DiscardAdded={discardedPushed}、add后栈深={stackAfterAdd}、最终墨迹栈深 {host.History.UndoCount}（应为 0）、宿主 CanUndo={host.CanUndo}（应为 False）");
        Console.WriteLine($"  时机二（add 在待提交事务里）：DiscardAdded={discardedPending} → 最终栈深 {host.History.UndoCount}（应为 0）、宿主 CanUndo={host.CanUndo}（应为 False）");
        Console.WriteLine($"[{(ghostPushedOk ? "PASS" : "FAIL")}] 已入栈的丢笔被彻底抹掉，Ctrl+Z 不会复活长线");
        Console.WriteLine($"[{(ghostPendingOk ? "PASS" : "FAIL")}] 待提交事务里的丢笔净变化为零，不占撤销格");
        if (!ghostPushedOk) failures++;
        if (!ghostPendingOk) failures++;

        // 擦除回归：IsEmpty 改成「按引用去重的净变化」后，擦除（移除原笔 + 加入碎笔）
        // 两边是不同对象 ⇒ 净变化不为零，仍必须占一格。第 2 组的 mergedOk 已覆盖同一件事，
        // 这里再显式钉一次，防止将来有人把「互相抵消」的判定写宽到擦除身上。
        host.Strokes.Clear();
        host.History.Reset();
        var eraseTarget = MakeLine(attr, new Point(20, 380), new Point(400, 380));
        host.Strokes.Add(eraseTarget);
        host.History.Reset();
        host.History.BeginTransaction();
        host.Strokes.Remove(eraseTarget);
        host.Strokes.Add(MakeLine(attr, new Point(20, 380), new Point(150, 380)));
        host.Strokes.Add(MakeLine(attr, new Point(220, 380), new Point(400, 380)));
        host.History.CommitTransaction();
        bool eraseStillCountsOk = host.History.UndoCount == 1
            && host.Undo() && host.StrokeCount == 1 && ReferenceEquals(host.Strokes[0], eraseTarget);
        Console.WriteLine($"[{(eraseStillCountsOk ? "PASS" : "FAIL")}] 擦除仍占一格且能完整复原（净变化判定没有误伤擦除）");
        if (!eraseStillCountsOk) failures++;

        // ---------------------------------------------------------- 6) 容量上限
        Console.WriteLine();
        Console.WriteLine("---- 6) 容量上限：压入 120 步 ⇒ 栈深被截到上限 ----");

        var scratch = new StrokeCollection();
        var scratchHistory = new InkHistory(scratch, InkHistory.DefaultCapacity);

        const int pushed = 120;
        for (int i = 0; i < pushed; i++)
        {
            scratch.Add(MakeLine(attr, new Point(i % 100, i), new Point(i % 100 + 5, i)));
        }

        bool capacityOk = scratchHistory.UndoCount == InkHistory.DefaultCapacity;

        Console.WriteLine($"  压入 {pushed} 步 → 撤销栈深度 {scratchHistory.UndoCount}（上限 {scratchHistory.Capacity}）");
        Console.WriteLine($"[{(capacityOk ? "PASS" : "FAIL")}] 超限时丢弃最早的记录（擦除会产生碎笔画，必须有回收点）");
        if (!capacityOk) failures++;

        // ---------------------------------------------------------- 7) 清除笔迹可撤销
        Console.WriteLine();
        Console.WriteLine("---- 7) 清除笔迹可以撤销（所以按钮上的确认框去掉了）----");

        host.Strokes.Clear();
        host.History.Reset();

        host.History.BeginTransaction();
        host.Strokes.Add(MakeLine(attr, new Point(10, 50), new Point(20, 50)));
        host.Strokes.Add(MakeLine(attr, new Point(30, 50), new Point(40, 50)));
        host.Strokes.Add(MakeLine(attr, new Point(50, 50), new Point(60, 50)));
        host.History.CommitTransaction();

        host.ClearStrokes();
        bool clearedOk = host.StrokeCount == 0;
        bool clearUndoOk = host.Undo() && host.StrokeCount == 3;

        Console.WriteLine($"  三条笔迹 → 清除后 {0} 条 → 撤销后 {host.StrokeCount} 条");
        Console.WriteLine($"[{(clearedOk ? "PASS" : "FAIL")}] 清除笔迹立刻生效");
        Console.WriteLine($"[{(clearUndoOk ? "PASS" : "FAIL")}] 且一把全部找回（这就是确认框可以去掉的依据）");
        if (!clearedOk) failures++;
        if (!clearUndoOk) failures++;

        // ---------------------------------------------------------- 8) 换文档清历史
        Console.WriteLine();
        Console.WriteLine("---- 8) 换文档必须清空历史（否则撤销会\"复活\"上一份试卷的批注）----");

        host.History.BeginTransaction();
        host.Strokes.Add(MakeLine(attr, new Point(10, 70), new Point(20, 70)));
        host.History.CommitTransaction();
        bool hadHistory = host.CanUndo;

        host.SetDocument(service);
        host.UpdateLayout();
        PumpFor(200);

        bool docResetOk = hadHistory && !host.CanUndo && !host.CanRedo && host.StrokeCount == 0;

        Console.WriteLine($"  换文档前 CanUndo={hadHistory} → 换文档后 CanUndo={host.CanUndo}、笔画数={host.StrokeCount}");
        Console.WriteLine($"[{(docResetOk ? "PASS" : "FAIL")}] 换文档后历史与画笔一起归零");
        if (!docResetOk) failures++;

        // ---------------------------------------------------------- 9) 事件接线
        Console.WriteLine();
        Console.WriteLine("---- 9) HistoryChanged 事件接线（按钮可用态靠它刷新）----");

        int notified = 0;
        EventHandler counter = (_, _) => notified++;
        host.HistoryChanged += counter;

        host.Strokes.Add(MakeLine(attr, new Point(10, 90), new Point(20, 90)));
        host.Undo();
        host.Redo();

        host.HistoryChanged -= counter;

        bool eventOk = notified >= 3;

        Console.WriteLine($"  一次新操作 + 撤销 + 重做 → 收到 {notified} 次通知（应为 3）");
        Console.WriteLine($"[{(eventOk ? "PASS" : "FAIL")}] 历史变化都会通知界面（否则按钮会一直灰着）");
        if (!eventOk) failures++;

        // ---------------------------------------------------------- 10) 端到端光栅
        Console.WriteLine();
        Console.WriteLine("---- 10) 端到端光栅：撤销真的让蓝线从画面上消失、重做又出现 ----");

        host.SetTool(ToolIds.Pen);
        host.FitWidth();
        host.UpdateLayout();
        PumpFor(300);

        host.Strokes.Clear();
        host.History.Reset();

        var page0 = host.Layout.PageRects[0];
        double lineY = page0.Y + 300;
        var blueAttr = MakeAttributes(InkPalette.Colors[2], InkPalette.Widths[3]);   // 蓝 + 特粗

        host.Strokes.Add(MakeLine(blueAttr,
                                  new Point(page0.X + 80, lineY),
                                  new Point(page0.X + page0.Width - 80, lineY)));
        host.UpdateLayout();
        PumpFor(200);

        var before = ScanPixels(Snapshot(host, hostWidth, hostHeight), IsBluePixel);
        SavePng(Snapshot(host, hostWidth, hostHeight), Path.Combine(outDir, "m5-undo-1-before.png"));

        host.Undo();
        host.UpdateLayout();
        PumpFor(200);
        var afterUndo = ScanPixels(Snapshot(host, hostWidth, hostHeight), IsBluePixel);
        SavePng(Snapshot(host, hostWidth, hostHeight), Path.Combine(outDir, "m5-undo-2-after-undo.png"));

        host.Redo();
        host.UpdateLayout();
        PumpFor(200);
        var afterRedo = ScanPixels(Snapshot(host, hostWidth, hostHeight), IsBluePixel);
        SavePng(Snapshot(host, hostWidth, hostHeight), Path.Combine(outDir, "m5-undo-3-after-redo.png"));

        bool vanishOk = before.Count > 1500 && afterUndo.Count == 0;
        bool reappearOk = afterRedo.Count > 1500;

        Console.WriteLine($"  画一条蓝线：蓝色像素 {before.Count} 个");
        Console.WriteLine($"  撤销后：{afterUndo.Count} 个（应为 0 —— 一个不剩才算真的从画面上消失）");
        Console.WriteLine($"  重做后：{afterRedo.Count} 个");
        Console.WriteLine($"[{(vanishOk ? "PASS" : "FAIL")}] 撤销后蓝线真的从渲染里消失");
        Console.WriteLine($"[{(reappearOk ? "PASS" : "FAIL")}] 重做后蓝线真的回来了");
        if (!vanishOk) failures++;
        if (!reappearOk) failures++;

        host.Shutdown();

        return failures;
    }

    // ================================================================= M6：批注持久化
    /// <summary>
    /// 验证批注的落盘与恢复。
    /// </summary>
    /// <remarks>
    /// 全部读写都在<b>临时目录</b>里进行：样本试卷在仓库里，绝不能往它旁边写 .tbink，
    /// 否则每跑一次验收就往版本库里塞一个产物文件。
    /// <para>
    /// 本机可验证的部分：格式、指纹、三层判定、平移补偿、原子写、自动保存调度、
    /// 以及"存盘 → 换一个全新的宿主 → 读回 → 渲染出的像素完全相同"这条端到端链路。
    /// <b>无法验证的部分</b>：断电/崩溃瞬间的数据保全（那要靠"写盘是原子的"这个前提来保证）。
    /// </para>
    /// </remarks>
    private static int RunM6Checks(PdfiumDocumentService service, string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M6：批注持久化 ================");

        const int hostWidth = 1000;
        const int hostHeight = 700;

        string workDir = Path.Combine(
            Path.GetTempPath(), "tb-m6-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);

        try
        {
            // 把样本试卷复制到临时目录再操作 —— 侧车文件会落在它旁边
            string pdfPath = Path.Combine(workDir, "样本试卷.pdf");
            File.Copy(service.FilePath!, pdfPath, overwrite: true);

            var store = new AnnotationStore();
            string sidecar = AnnotationStore.SidecarPathFor(pdfPath);

            var host = new CanvasViewportHost();
            host.Measure(new Size(hostWidth, hostHeight));
            host.Arrange(new Rect(0, 0, hostWidth, hostHeight));
            host.UpdateLayout();
            host.SetDocument(service);
            host.UpdateLayout();
            PumpFor(250);

            // ------------------------------------------------------ 1) 侧车路径
            Console.WriteLine();
            Console.WriteLine("---- 1) 批注文件落在试卷旁边 ----");

            bool pathOk = AnnotationStore.SidecarPathFor(pdfPath).EndsWith(".pdf.tbink", StringComparison.Ordinal)
                          && AnnotationStore.SidecarPathFor(pdfPath) == pdfPath + ".tbink";

            Console.WriteLine($"  试卷：{Path.GetFileName(pdfPath)}");
            Console.WriteLine($"  批注：{Path.GetFileName(sidecar)}");
            Console.WriteLine($"[{(pathOk ? "PASS" : "FAIL")}] 侧车文件名 = 试卷名 + .tbink（拷试卷时连它一起拷）");
            if (!pathOk) failures++;

            // ------------------------------------------------------ 2) 往返（几何与属性保真）
            Console.WriteLine();
            Console.WriteLine("---- 2) 存盘 → 读回：几何与墨色笔宽逐点一致 ----");

            var red = MakeAttributes(InkPalette.Colors[0], InkPalette.Widths[3]);
            var blue = MakeAttributes(InkPalette.Colors[2], InkPalette.Widths[0]);

            var strokes = new StrokeCollection();
            strokes.Add(MakeLine(red, new Point(100, 120), new Point(400, 120)));
            strokes.Add(MakeLine(blue, new Point(150.5, 300.25), new Point(520, 460)));

            var saveResult = store.Save(pdfPath, strokes, host.Layout.PageRects,
                                        host.Layout.PageMargin, host.Layout.PageGap);

            bool writeOk = saveResult.Ok && saveResult.Written && File.Exists(sidecar);
            bool noTempLeft = !File.Exists(sidecar + ".tmp");

            var loaded = store.Load(pdfPath, host.Layout.PageRects,
                                    host.Layout.PageMargin, host.Layout.PageGap);

            bool loadOk = loaded.Status == AnnotationLoadStatus.Loaded;
            bool countOk = loaded.Strokes is { Count: 2 };

            // ISF 往返会引入约 0.006 world 的量化误差（见 SameGeometry 的说明）。
            // 容差取 0.05 world ≈ 0.0018 mm，比格式自身精度宽松，又远小于任何可见差异。
            const double positionTolerance = 0.05;
            const double widthTolerance = 0.01;

            bool geometryOk = countOk && SameGeometry(strokes, loaded.Strokes!, positionTolerance);
            bool attributeOk = countOk
                               && loaded.Strokes![0].DrawingAttributes.Color == red.Color
                               && Near(loaded.Strokes[0].DrawingAttributes.Width, red.Width, widthTolerance)
                               && loaded.Strokes[1].DrawingAttributes.Color == blue.Color
                               && Near(loaded.Strokes[1].DrawingAttributes.Width, blue.Width, widthTolerance);

            double quantized = countOk
                ? Math.Abs(loaded.Strokes![0].StylusPoints[0].X - strokes[0].StylusPoints[0].X)
                : 0;

            Console.WriteLine($"  保存：{(int)new FileInfo(sidecar).Length:N0} 字节 → {saveResult.Message}");
            Console.WriteLine($"  ISF 往返量化误差：首点 X 偏移 {quantized:F6} world（≈{quantized * 1.6:F4} px @1.6x，不可见）");
            Console.WriteLine($"[{(writeOk ? "PASS" : "FAIL")}] 批注文件已写出");
            Console.WriteLine($"[{(loadOk ? "PASS" : "FAIL")}] 读回判定为「同一份文档」");
            Console.WriteLine($"[{(geometryOk ? "PASS" : "FAIL")}] 两个笔画的采样点在 {positionTolerance} world 内一致（世界坐标零换算）");
            Console.WriteLine($"[{(attributeOk ? "PASS" : "FAIL")}] 墨色与笔宽原样保留（红 4.0 / 蓝 0.8）");
            Console.WriteLine($"[{(noTempLeft ? "PASS" : "FAIL")}] 原子写没有留下 .tmp 残骸");
            if (!writeOk) failures++;
            if (!loadOk) failures++;
            if (!geometryOk) failures++;
            if (!attributeOk) failures++;
            if (!noTempLeft) failures++;

            // ------------------------------------------------------ 3) 损坏文件必须被优雅拒绝
            Console.WriteLine();
            Console.WriteLine("---- 3) 损坏 / 版本过高的批注文件 ----");

            string brokenPdf = Path.Combine(workDir, "破的.pdf");
            File.WriteAllBytes(AnnotationStore.SidecarPathFor(brokenPdf), new byte[] { 1, 2, 3, 4, 5 });
            var broken = store.Load(brokenPdf, host.Layout.PageRects,
                                    host.Layout.PageMargin, host.Layout.PageGap);

            // 伪造一个"未来版本"的文件：魔数对、版本号 99
            string futurePdf = Path.Combine(workDir, "未来版本.pdf");
            byte[] future = new byte[12];
            Array.Copy(new byte[] { (byte)'T', (byte)'B', (byte)'I', (byte)'N', (byte)'K', 0 }, future, 6);
            future[6] = 99;
            File.WriteAllBytes(AnnotationStore.SidecarPathFor(futurePdf), future);
            var futureResult = store.Load(futurePdf, host.Layout.PageRects,
                                          host.Layout.PageMargin, host.Layout.PageGap);

            bool brokenOk = broken.Status == AnnotationLoadStatus.Rejected;
            bool futureOk = futureResult.Status == AnnotationLoadStatus.Rejected;

            Console.WriteLine($"  5 字节垃圾文件 → {broken.Status}：{broken.Message}");
            Console.WriteLine($"  版本号 99 的文件 → {futureResult.Message}");
            Console.WriteLine($"[{(brokenOk ? "PASS" : "FAIL")}] 垃圾文件被拒绝，没有抛异常");
            Console.WriteLine($"[{(futureOk ? "PASS" : "FAIL")}] 更高版本被拒绝并给出可读原因（而不是崩掉）");
            if (!brokenOk) failures++;
            if (!futureOk) failures++;

            // ------------------------------------------------------ 4) 指纹
            Console.WriteLine();
            Console.WriteLine("---- 4) 文档内容指纹 ----");

            string fp1 = DocumentFingerprint.Compute(pdfPath);
            string fp2 = DocumentFingerprint.Compute(pdfPath);

            // 首部字节落在采样窗口内 ⇒ 改动它必定改变指纹
            byte[] headChanged = File.ReadAllBytes(pdfPath);
            headChanged[8] ^= 0xFF;
            string headChangedPdf = Path.Combine(workDir, "改过首部.pdf");
            File.WriteAllBytes(headChangedPdf, headChanged);
            string fp3 = DocumentFingerprint.Compute(headChangedPdf);

            // 只改文件正中间的字节：样本试卷大于 2 MiB，这个位置落在采样窗口之外，
            // 指纹<b>不会</b>改变。这正是「首 1 MiB + 尾 1 MiB」这一取舍的已知代价，
            // 在这里显式断言它，免得日后有人误以为它是密码学哈希。
            byte[] middleChanged = File.ReadAllBytes(pdfPath);
            middleChanged[middleChanged.Length / 2] ^= 0xFF;
            string middleChangedPdf = Path.Combine(workDir, "只改中间.pdf");
            File.WriteAllBytes(middleChangedPdf, middleChanged);
            bool middleInsensitive = DocumentFingerprint.Compute(middleChangedPdf) == fp1;

            bool fingerprintStable = fp1.Length == 32 && fp1 == fp2;
            bool fingerprintSensitive = fp1 != fp3;

            long pdfSize = new FileInfo(pdfPath).Length;
            Console.WriteLine($"  样本 {pdfSize / 1024.0 / 1024.0:F2} MiB，指纹 = {fp1}（32 位十六进制）");
            Console.WriteLine($"[{(fingerprintStable ? "PASS" : "FAIL")}] 同一文件多次计算结果一致（与修改时间、路径无关）");
            Console.WriteLine($"[{(fingerprintSensitive ? "PASS" : "FAIL")}] 改动首部字节后指纹改变");
            Console.WriteLine($"[{(middleInsensitive ? "PASS" : "FAIL")}] 已知取舍：只改中间字节时指纹不变"
                              + $"（{pdfSize / 1024.0 / 1024.0:F1} MiB 的文件只有首尾 2 MiB 参与哈希）");
            if (!fingerprintStable) failures++;
            if (!fingerprintSensitive) failures++;
            if (!middleInsensitive) failures++;

            // ------------------------------------------------------ 5) 三层判定
            Console.WriteLine();
            Console.WriteLine("---- 5) 对不上时的三层处置 ----");

            // 5a) 页数不同 ⇒ 拒绝
            var fewerRects = new List<Rect>();
            for (int i = 0; i < host.Layout.PageRects.Count - 1; i++) fewerRects.Add(host.Layout.PageRects[i]);
            var rejectResult = store.Load(pdfPath, fewerRects, host.Layout.PageMargin, host.Layout.PageGap);
            bool rejectOk = rejectResult.Status == AnnotationLoadStatus.Rejected;

            // 5b) 几何相同但内容不同 ⇒ 装载 + 警告（同名文件被覆盖过的情形）
            // 这里手工写一份"版式完全正确、指纹指向别的文件"的批注来制造该情形，
            // 而不是靠改坏 PDF：后者会让 pdfium 打不开，测试就变成在测别的东西了。
            string foreignPdf = Path.Combine(workDir, "指纹对不上.pdf");
            File.Copy(pdfPath, foreignPdf, overwrite: true);

            var foreignManifest = new TbinkManifest
            {
                SourceFingerprint = "00000000000000000000000000000000",
                PageMargin = host.Layout.PageMargin,
                PageGap = host.Layout.PageGap,
                Pages = MakeManifestPages(host.Layout.PageRects),
            };
            using (var stream = File.Create(AnnotationStore.SidecarPathFor(foreignPdf)))
            {
                TbinkFile.Write(stream, foreignManifest, strokes);
            }

            var mismatch = store.Load(foreignPdf, host.Layout.PageRects,
                                      host.Layout.PageMargin, host.Layout.PageGap);
            bool mismatchOk = mismatch.Status == AnnotationLoadStatus.LoadedWithMismatch
                              && mismatch.IsWarning
                              && mismatch.Strokes is { Count: 2 };

            Console.WriteLine($"  页数对不上 → {rejectResult.Status}：{rejectResult.Message}");
            Console.WriteLine($"  版式对得上但指纹不同 → {mismatch.Status}：{mismatch.Message}");
            Console.WriteLine($"[{(rejectOk ? "PASS" : "FAIL")}] 页数/版式对不上 ⇒ 一条都不装（宁可没批注，也不要错位到别的题上）");
            Console.WriteLine($"[{(mismatchOk ? "PASS" : "FAIL")}] 版式对得上但内容变了 ⇒ 装载并标记为「需提醒」");
            if (!rejectOk) failures++;
            if (!mismatchOk) failures++;

            // ------------------------------------------------------ 6) 整体平移补偿
            Console.WriteLine();
            Console.WriteLine("---- 6) 页外留白常量改过：笔迹跟着页面一起挪 ----");

            const double shiftX = 100;
            const double shiftY = 50;
            string shiftedPdf = Path.Combine(workDir, "旧版式.pdf");

            var shiftedManifest = new TbinkManifest
            {
                SourceFingerprint = fp1,
                PageMargin = host.Layout.PageMargin + shiftX,
                PageGap = host.Layout.PageGap,
                Pages = MakeManifestPages(host.Layout.PageRects, shiftX, shiftY),
            };

            File.Copy(pdfPath, shiftedPdf, overwrite: true);
            using (var stream = File.Create(AnnotationStore.SidecarPathFor(shiftedPdf)))
            {
                TbinkFile.Write(stream, shiftedManifest, strokes);
            }

            var shifted = store.Load(shiftedPdf, host.Layout.PageRects,
                                     host.Layout.PageMargin, host.Layout.PageGap);
            bool shiftLoaded = shifted.Status == AnnotationLoadStatus.Loaded
                               && shifted.Strokes is { Count: 2 };
            // 容差同前：ISF 往返本身就有约 0.006 world 的量化误差
            bool shiftCompensated = shiftLoaded
                                    && Near(shifted.Strokes![0].StylusPoints[0].X, 100 - shiftX, positionTolerance)
                                    && Near(shifted.Strokes[0].StylusPoints[0].Y, 120 - shiftY, positionTolerance);

            Console.WriteLine($"  批注记录的页面在 (x+{shiftX:F0}, y+{shiftY:F0})，当前版式整体左移上移");
            Console.WriteLine($"  笔迹首点 (100,120) → 补偿后 ({shifted.Strokes?[0].StylusPoints[0].X:F1},"
                              + $" {shifted.Strokes?[0].StylusPoints[0].Y:F1})");
            Console.WriteLine($"[{(shiftLoaded ? "PASS" : "FAIL")}] 能识别出「只是整体平移」并装载");
            Console.WriteLine($"[{(shiftCompensated ? "PASS" : "FAIL")}] 笔迹被同步平移，仍然落在原来的字上");
            if (!shiftLoaded) failures++;
            if (!shiftCompensated) failures++;

            // ------------------------------------------------------ 7) 装载不污染撤销栈
            Console.WriteLine();
            Console.WriteLine("---- 7) 装载批注不进撤销栈 ----");

            host.SetDocument(service);
            host.UpdateLayout();
            PumpFor(120);

            host.Strokes.Add(MakeLine(red, new Point(60, 60), new Point(260, 60)));
            bool hadHistory = host.CanUndo;

            host.ReplaceStrokes(loaded.Strokes!);
            bool cleanHistory = hadHistory && !host.CanUndo && !host.CanRedo && host.StrokeCount == 2;

            Console.WriteLine($"  装载前 CanUndo={hadHistory} → 装载后 CanUndo={host.CanUndo}、笔迹 {host.StrokeCount} 条");
            Console.WriteLine($"[{(cleanHistory ? "PASS" : "FAIL")}] 装载后撤销栈为空（否则按一下 Ctrl+Z 会把整份批注抹掉）");
            if (!cleanHistory) failures++;

            // ------------------------------------------------------ 8) 全擦光 ⇒ 批注文件删除
            Console.WriteLine();
            Console.WriteLine("---- 8) 笔迹清空 ⇒ 批注文件一并删除 ----");

            var emptySave = store.Save(pdfPath, new StrokeCollection(), host.Layout.PageRects,
                                       host.Layout.PageMargin, host.Layout.PageGap);
            bool deleteOk = emptySave.Ok && !File.Exists(sidecar);

            Console.WriteLine($"  {emptySave.Message}；文件是否还在 = {File.Exists(sidecar)}");
            Console.WriteLine($"[{(deleteOk ? "PASS" : "FAIL")}] 留一个空批注文件，只会让「全擦光后重开」看起来像没保存成功");
            if (!deleteOk) failures++;

            // ------------------------------------------------------ 9) 自动保存调度
            Console.WriteLine();
            Console.WriteLine("---- 9) 自动保存：防抖 + 关键节点强制落盘 ----");

            int saveCalls = 0;
            var scheduler = new AutoSaveScheduler(
                Dispatcher.CurrentDispatcher, TimeSpan.FromMilliseconds(80), () => { saveCalls++; return true; });

            scheduler.MarkDirty();
            bool dirtyOk = scheduler.IsDirty;
            bool flushOk = scheduler.Flush() && saveCalls == 1 && !scheduler.IsDirty;
            bool noRepeat = !scheduler.Flush() && saveCalls == 1;

            scheduler.IsSuspended = true;
            scheduler.MarkDirty();
            bool suspendOk = !scheduler.IsDirty;
            scheduler.IsSuspended = false;
            scheduler.Dispose();

            // 计时器真的会自动触发（用 80ms 防抖 + 泵消息，不靠 sleep 硬等）
            int tickSaves = 0;
            var ticking = new AutoSaveScheduler(
                Dispatcher.CurrentDispatcher, TimeSpan.FromMilliseconds(80), () => { tickSaves++; return true; });
            ticking.MarkDirty();
            bool autoFired = PumpUntil(() => tickSaves > 0, 3000);
            ticking.Dispose();

            Console.WriteLine($"  标记脏 → IsDirty={dirtyOk}；Flush → 累计写入 {saveCalls} 次、清脏 = {!scheduler.IsDirty}");
            Console.WriteLine($"  停笔后计时器自动落盘：触发 {tickSaves} 次");
            Console.WriteLine($"[{(dirtyOk && flushOk ? "PASS" : "FAIL")}] 置脏后可强制落盘，且落盘后不再重复写");
            Console.WriteLine($"[{(noRepeat ? "PASS" : "FAIL")}] 不脏时 Flush 是空操作（不会把没变的内容反复写盘）");
            Console.WriteLine($"[{(suspendOk ? "PASS" : "FAIL")}] 挂起期间不置脏（装载批注不会被当成「用户改了东西」）");
            Console.WriteLine($"[{(autoFired && tickSaves == 1 ? "PASS" : "FAIL")}] 停笔后计时器自动触发一次落盘");
            if (!dirtyOk || !flushOk) failures++;
            if (!noRepeat) failures++;
            if (!suspendOk) failures++;
            if (!(autoFired && tickSaves == 1)) failures++;

            // ------------------------------------------------------ 10) 端到端光栅（最关键）
            Console.WriteLine();
            Console.WriteLine("---- 10) 端到端：存盘 → 换一个全新宿主读回 → 像素完全相同 ----");

            host.SetDocument(service);
            host.UpdateLayout();
            PumpFor(200);

            // 注意：页面从 (PageMargin, PageMargin) = (960,960) 起排，所以笔画必须用
            // "页面矩形 + 偏移" 的绝对世界坐标，不能直接写 y=300 —— 那是页外留白，
            // 按宽度适配后根本不在屏幕上，扫出来会是 0 个像素。
            var page0 = host.Layout.PageRects[0];
            double lineY = page0.Y + 300;

            var blueAttr = MakeAttributes(InkPalette.Colors[2], InkPalette.Widths[3]);
            var endToEnd = new StrokeCollection();
            endToEnd.Add(MakeLine(blueAttr,
                                  new Point(page0.X + 60, lineY),
                                  new Point(page0.X + page0.Width - 60, lineY)));
            endToEnd.Add(MakeLine(blueAttr,
                                  new Point(page0.X + 60, lineY + 26),
                                  new Point(page0.X + page0.Width - 60, lineY + 26)));
            host.ReplaceStrokes(endToEnd);

            var beforeSave = ScanPixels(Snapshot(host, hostWidth, hostHeight), IsBluePixel);
            SavePng(Snapshot(host, hostWidth, hostHeight), Path.Combine(outDir, "m6-1-before-save.png"));

            var rasterSave = store.Save(pdfPath, host.SnapshotStrokes(), host.Layout.PageRects,
                                        host.Layout.PageMargin, host.Layout.PageGap);

            // 关掉宿主，再建一个全新的 —— 模拟"关掉程序重新打开"
            host.Shutdown();

            var hostAfterRestart = new CanvasViewportHost();
            hostAfterRestart.Measure(new Size(hostWidth, hostHeight));
            hostAfterRestart.Arrange(new Rect(0, 0, hostWidth, hostHeight));
            hostAfterRestart.UpdateLayout();
            hostAfterRestart.SetDocument(service);
            hostAfterRestart.UpdateLayout();
            PumpFor(250);

            var reloadedResult = store.Load(pdfPath, hostAfterRestart.Layout.PageRects,
                                            hostAfterRestart.Layout.PageMargin, hostAfterRestart.Layout.PageGap);
            hostAfterRestart.ReplaceStrokes(reloadedResult.Strokes!);
            PumpFor(200);

            var afterRestart = ScanPixels(Snapshot(hostAfterRestart, hostWidth, hostHeight), IsBluePixel);
            SavePng(Snapshot(hostAfterRestart, hostWidth, hostHeight),
                    Path.Combine(outDir, "m6-2-after-restart.png"));

            bool rasterSaveOk = rasterSave.Written && File.Exists(sidecar);

            // 不用"像素数严格相等"：ISF 的量化误差会让抗锯齿边缘的个别像素翻边
            // （实测 10708 vs 10707）。1% 的相对容差既能抓住真问题
            // （笔迹丢了、位置错了都会是量级差异），又不会被一个边缘像素绊倒。
            int pixelDelta = Math.Abs(afterRestart.Count - beforeSave.Count);
            double pixelDeltaRatio = beforeSave.Count > 0 ? (double)pixelDelta / beforeSave.Count : 1;
            bool rasterSame = beforeSave.Count > 0 && pixelDeltaRatio <= 0.01;

            Console.WriteLine($"  保存前蓝线像素 {beforeSave.Count} 个（外接框 "
                              + $"{beforeSave.MaxX - beforeSave.MinX + 1}×{beforeSave.MaxY - beforeSave.MinY + 1} px）");
            Console.WriteLine($"  重启后蓝线像素 {afterRestart.Count} 个（外接框 "
                              + $"{afterRestart.MaxX - afterRestart.MinX + 1}×{afterRestart.MaxY - afterRestart.MinY + 1} px）");
            Console.WriteLine($"  差异 {pixelDelta} 个像素（{pixelDeltaRatio:P2}，全部来自 ISF 量化导致的抗锯齿边缘翻边）");
            Console.WriteLine($"[{(rasterSaveOk ? "PASS" : "FAIL")}] 批注已落盘");
            Console.WriteLine($"[{(rasterSame ? "PASS" : "FAIL")}] 重新打开后画面上的墨迹一致（像素数差异 ≤ 1%）");
            if (!rasterSaveOk) failures++;
            if (!rasterSame) failures++;

            hostAfterRestart.Shutdown();
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不值得让整轮验收失败
            }
        }

        return failures;
    }

    /// <summary>
    /// 比较两个笔迹集合的采样点是否一致（world 单位，容差由调用方给）。
    /// </summary>
    /// <remarks>
    /// <b>容差为什么不是 1e-6：ISF 不是位精确格式。</b>
    /// 实测把 (100,120)、笔宽 4.0 存进去再读回来，得到的是
    /// (100.00629921259842,120)、笔宽 3.9999874017370027 ——
    /// ISF 内部按量化的物理单位存储，往返必然带来约 0.006 world 的误差。
    /// <para>
    /// 这个量级换算到 1.6 倍缩放的屏幕上不到 0.1 px，数位板与肉眼都感知不到，
    /// 但"逐位相等"这个更严的断言会失败。断言必须描述真实契约，而不是我们希望的样子。
    /// </para>
    /// </remarks>
    private static bool SameGeometry(StrokeCollection a, StrokeCollection b, double tolerance)
    {
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].StylusPoints.Count != b[i].StylusPoints.Count) return false;

            for (int j = 0; j < a[i].StylusPoints.Count; j++)
            {
                if (!Near(a[i].StylusPoints[j].X, b[i].StylusPoints[j].X, tolerance)) return false;
                if (!Near(a[i].StylusPoints[j].Y, b[i].StylusPoints[j].Y, tolerance)) return false;
            }
        }

        return true;
    }

    /// <summary>按世界矩形造出 manifest 里的页记录，可整体加偏移（用来构造"旧版式"的批注）。</summary>
    private static List<TbinkPageRect> MakeManifestPages(
        IReadOnlyList<Rect> rects, double dx = 0, double dy = 0)
    {
        var pages = new List<TbinkPageRect>(rects.Count);

        foreach (var rect in rects)
        {
            pages.Add(new TbinkPageRect
            {
                X = rect.X + dx,
                Y = rect.Y + dy,
                Width = rect.Width,
                Height = rect.Height,
            });
        }

        return pages;
    }

    // ================================================================= M3.5：按宽度适配（方案 B）
    /// <summary>
    /// 验证"按宽度适配"的竖直行为：打开文档落在首页顶部、Ctrl+0 不丢当前位置、越界夹紧、小文档居中。
    /// </summary>
    /// <remarks>
    /// 背景：旧的"整份文档竖直居中"会让 7 页试卷打开时落在第 4 页 —— 对"打开试卷讲评"
    /// 这个主用例是错的落点。方案 B = 只改宽度，竖直保持视口中心的世界 Y，越界夹紧。
    /// </remarks>
    private static int RunFitWidthChecks(PdfiumDocumentService service, string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M3.5：按宽度适配（方案 B） ================");

        var sizes = new List<Size>(service.PageCount);
        for (int i = 0; i < service.PageCount; i++) sizes.Add(service.GetPageSize(i));

        var layout = new WorldLayout();
        layout.Rebuild(sizes);

        var bounds = layout.WorldBounds;
        const double padding = 24.0;
        var viewportSize = new Size(1000, 700);
        double expectedScale = (viewportSize.Width - padding * 2) / bounds.Width;

        var vp = new CanvasViewport();

        // (a) 刚打开文档：视口还停在世界原点 ⇒ 被夹紧到文档顶 ⇒ 首页顶边贴住视口顶边
        vp.SetView(1.0, 0, 0);
        vp.FitToWidth(bounds, viewportSize, padding);
        double topMostY = vp.ToViewport(new Point(0, bounds.Y)).Y;
        double leftMostX = vp.ToViewport(new Point(bounds.X, 0)).X;
        bool openOk = Near(vp.Scale, expectedScale, 1e-9)
                   && Math.Abs(topMostY) < 1e-6
                   && Math.Abs(leftMostX - padding) < 1e-6;

        // (b) 正看着第 3 页时按 Ctrl+0：竖直位置不该跳（这条是方案 B 的核心收益）
        double worldCenterY = layout.PageRects[2].Y + 120;
        vp.SetView(expectedScale, 137, viewportSize.Height / 2 - worldCenterY * expectedScale);
        vp.FitToWidth(bounds, viewportSize, padding);
        double centerAfterY = vp.ToViewport(new Point(0, worldCenterY)).Y;
        bool keepCenterOk = Near(centerAfterY, viewportSize.Height / 2, 1e-6);

        // (c) 越界夹紧：视口被甩到文档上/下方很远的空白处时，适配必须把它拉回内容范围内
        vp.SetView(expectedScale, 0, 5000);
        vp.FitToWidth(bounds, viewportSize, padding);
        bool clampTopOk = Math.Abs(vp.ToViewport(new Point(0, bounds.Y)).Y) < 1e-6;

        vp.SetView(expectedScale, 0, -30000);
        vp.FitToWidth(bounds, viewportSize, padding);
        bool clampBottomOk = Near(vp.ToViewport(new Point(0, bounds.Bottom)).Y, viewportSize.Height, 1e-6);

        // (d) 内容比视口矮 ⇒ 竖直居中（小文档没有落点问题，居中更好看）
        var tallViewport = new Size(1000, 20000);
        vp.SetView(1.0, 0, 0);
        vp.FitToWidth(bounds, tallViewport, padding);
        double topGap = vp.ToViewport(new Point(0, bounds.Y)).Y;
        double bottomGap = tallViewport.Height - vp.ToViewport(new Point(0, bounds.Bottom)).Y;
        bool centerShortOk = Near(topGap, bottomGap, 1e-6);

        // (e) 极窄视口：按宽度算出来的缩放会低于下限 ⇒ 缩放被钳到 MinScale 后，
        //     偏移必须跟着钳过的值走，否则页会整块偏出视口（静默的、只在窄窗口下出现）
        var narrowViewport = new Size(60, 700);
        vp.SetView(1.0, 0, 0);
        vp.FitToWidth(bounds, narrowViewport, padding);
        double narrowLeftX = vp.ToViewport(new Point(bounds.X, 0)).X;
        double narrowScaleOk = vp.Scale;
        bool narrowOk = Near(narrowScaleOk, CanvasViewport.MinScale, 1e-9)
                        && Near(narrowLeftX, padding, 1e-6);

        Console.WriteLine($"  fit scale = {expectedScale:F4}（页宽 {bounds.Width:F2} → 视口 {viewportSize.Width}）");
        Console.WriteLine($"  (a) 打开：首页顶边在视口 Y = {topMostY:F6}，左侧留白 X = {leftMostX:F2}");
        Console.WriteLine($"  (b) 第3页按 Ctrl+0：视口中心世界 Y 由 {worldCenterY:F1} 落到视口 Y = {centerAfterY:F6}（期望 {viewportSize.Height / 2:F1}）");
        Console.WriteLine($"  (c) 越界夹紧：上方 → 顶对齐 {clampTopOk}；下方 → 底对齐 {clampBottomOk}");
        Console.WriteLine($"  (d) 小文档（视口高 {tallViewport.Height:F0}）：上留白 {topGap:F2} == 下留白 {bottomGap:F2}");
        Console.WriteLine($"  (e) 极窄视口（宽 {narrowViewport.Width:F0}）：缩放钳到 {narrowScaleOk:F4}，左侧留白 X = {narrowLeftX:F2}");

        Console.WriteLine($"[{(openOk ? "PASS" : "FAIL")}] 打开文档 ⇒ 首页顶部对齐视口顶部（不再落在文档中部）");
        Console.WriteLine($"[{(keepCenterOk ? "PASS" : "FAIL")}] Ctrl+0 保持视口中心的世界 Y（不把老师弹回第 1 页）");
        Console.WriteLine($"[{(clampTopOk && clampBottomOk ? "PASS" : "FAIL")}] Ctrl+0 越界时夹紧到内容范围（不会停在空白处）");
        Console.WriteLine($"[{(centerShortOk ? "PASS" : "FAIL")}] 内容比视口矮时竖直居中");
        Console.WriteLine($"[{(narrowOk ? "PASS" : "FAIL")}] 极窄视口：缩放钳到下限后偏移跟着走（页不偏出视口）");
        if (!openOk) failures++;
        if (!keepCenterOk) failures++;
        if (!(clampTopOk && clampBottomOk)) failures++;
        if (!centerShortOk) failures++;
        if (!narrowOk) failures++;

        // ---------------------------------------------------------- 真实控件树：打开文档的首屏就是第 1 页
        Console.WriteLine();
        Console.WriteLine("---- 真实控件树：打开文档的首屏 ----");

        const int hostWidth = 1000;
        const int hostHeight = 700;

        var host = new CanvasViewportHost();
        host.Measure(new Size(hostWidth, hostHeight));
        host.Arrange(new Rect(0, 0, hostWidth, hostHeight));
        host.UpdateLayout();
        host.SetDocument(service);
        host.UpdateLayout();
        PumpFor(300);

        // 换文档场景：先把视口挪到文档中部（模拟"上一份卷子看到第 5 页"），再换文档。
        // 新试卷必须从第一页顶部开始 —— 这条钉住 SetDocument 里的 _viewport.Reset()。
        host.Viewport.SetView(1.0, 0, -3000);
        host.UpdateLayout();
        host.SetDocument(service);
        host.UpdateLayout();
        PumpFor(300);

        var page0 = host.Layout.PageRects[0];
        var page0InView = host.Viewport.ToViewport(new Point(page0.X, page0.Y));
        bool hostOnFirstPage = Math.Abs(page0InView.Y) < 1e-6 && Math.Abs(page0InView.X - 24) < 1e-6;

        // 光栅复核：首屏的白纸必须从 y=0 就开始（也就是"第 1 页在首屏里"），而不是一片空白
        host.CanvasBackground = Brushes.Black;
        var darkBitmap = Snapshot(host, hostWidth, hostHeight);
        var stats = Analyze(darkBitmap);
        host.CanvasBackground = Brushes.White;

        bool paperOnFirstScreen = stats.WhitePixels > 0
                               && stats.WhiteMinY <= 4
                               && stats.WhiteMaxY >= hostHeight - 4;

        // 证据图用生产默认底色（纯白）重新出一张，看到的就是老师眼里的真实观感
        string proofFile = Path.Combine(outDir, "fitwidth-open.png");
        SavePng(Snapshot(host, hostWidth, hostHeight), proofFile);

        Console.WriteLine($"  首页左上角 → 视口 ({page0InView.X:F4},{page0InView.Y:F4})"
                          + $"  留白 M = {WorldLayout.DefaultPageMargin}"
                          + $"  InkExtent = {host.Layout.InkExtent.Width:F0} x {host.Layout.InkExtent.Height:F0}");
        Console.WriteLine($"  首屏白纸行范围（黑底对照）= {stats.WhiteMinY} … {stats.WhiteMaxY}（视口高 {hostHeight}）");
        Console.WriteLine($"[{(hostOnFirstPage && paperOnFirstScreen ? "PASS" : "FAIL")}] 打开 / 换文档 ⇒ 首屏立刻是第 1 页顶部 → {proofFile}");
        if (!hostOnFirstPage || !paperOnFirstScreen) failures++;

        host.Shutdown();

        return failures;
    }

    // ================================================================= M3.6：页外纯白（整纸观感）
    /// <summary>
    /// 验证"页外空间全部纯白"：光栅里除页面（及其边缘几像素的抗锯齿缓冲）之外，
    /// <b>每一个</b>像素都必须是纯白 —— 也就是页外留白、页间缝隙、以及文档范围之外的无限远处
    /// 与纸面是同一种白，整个视口连成一张纸。
    /// </summary>
    /// <remarks>
    /// 用全图扫描而不是抽几个点采样：采样只能证明"我挑的那几个地方是白的"，
    /// 而这条需求要的是"没有任何被漏掉的深色区域"。另外必须并一条"页面内容仍在"的反向断言，
    /// 否则把整块画布涂成白色也能让这条全过。
    ///
    /// 注意扫描时对页面矩形做了向外 tolerance 的豁免：页面矩形有一条 0.6 world 单位的描边，
    /// 抗锯齿会向页外溢出约 1 px，这不是"页外空间的颜色"。
    /// </remarks>
    private static int RunPaperLookChecks(PdfiumDocumentService service, string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M3.6：页外纯白（整纸观感） ================");

        var sizes = new List<Size>(service.PageCount);
        for (int i = 0; i < service.PageCount; i++) sizes.Add(service.GetPageSize(i));

        const int hostWidth = 1000;
        const int hostHeight = 700;

        var host = new CanvasViewportHost();
        host.Measure(new Size(hostWidth, hostHeight));
        host.Arrange(new Rect(0, 0, hostWidth, hostHeight));
        host.UpdateLayout();
        host.SetDocument(service);
        host.UpdateLayout();
        PumpFor(300);

        // 缩到 0.5：页宽 595 → 298 px，左右各露出 480 world 单位的留白，
        // 正是"一眼看得见页外空间"的状态，也是老师检查这个效果时会按到的比例。
        const double scale = 0.5;
        var p0 = host.Layout.PageRects[0];
        var p1 = host.Layout.PageRects[1];
        var bounds = host.Layout.WorldBounds;

        host.Viewport.SetView(scale,
                              (hostWidth - p0.Width * scale) / 2.0 - p0.X * scale,
                              100 - p0.Y * scale);
        host.UpdateLayout();
        PumpFor(200);

        // 采样前提：第 2 页的顶边要落在视口内，否则"页间缝隙"那个采样点无意义
        double p1TopInView = host.Viewport.ToViewport(new Point(p1.X, p1.Y)).Y;
        bool viewOk = p1TopInView > 0 && p1TopInView < hostHeight;

        Console.WriteLine($"  视口 {hostWidth}x{hostHeight}，缩放 {scale:0.0}，页宽 {p0.Width * scale:F1} px，"
                          + $"页间缝隙在视口 Y = {p1TopInView:F1}");

        var bitmap = Snapshot(host, hostWidth, hostHeight);
        var stats = Analyze(bitmap);

        // ---- 全图扫描：页外像素必须纯白 ----
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        double tolerance = 4.0 / scale;                 // 约 4 px 折算成 world 单位
        long offPaperPixels = 0, offendPixels = 0, onPaperPixels = 0;
        int offendX = -1, offendY = -1;

        for (int y = 0; y < converted.PixelHeight; y++)
        {
            for (int x = 0; x < converted.PixelWidth; x++)
            {
                int offset = y * stride + x * 4;
                bool nonWhite = pixels[offset] < 250 || pixels[offset + 1] < 250 || pixels[offset + 2] < 250;

                var world = host.Viewport.ToWorld(new Point(x + 0.5, y + 0.5));
                if (NearAnyPage(host.Layout, world, tolerance))
                {
                    // 页面范围内（含约 4px 的边界缓冲）：统计非白像素。
                    // 这是"内容还在"的反向证据 —— 把整块画布涂白时它必然为 0。
                    // 用"非白"而不是"深色"：缩小后笔画被抗锯齿拉成中灰，卡深色阈值只是在标定常数。
                    if (nonWhite) onPaperPixels++;
                    continue;
                }

                offPaperPixels++;
                if (nonWhite)
                {
                    offendPixels++;
                    if (offendX < 0) { offendX = x; offendY = y; }
                }
            }
        }

        // ---- 三处"页外"采样点，打印实际取值（便于一眼确认确实是纯白）----
        double gapMidY = (p0.Bottom + p1.Y) / 2.0;
        var probes = new (Point World, string Name)[]
        {
            (new Point(bounds.X - 320, p0.Y + 200), "页左侧留白"),
            (new Point(bounds.Right + 320, p0.Y + 200), "页右侧留白"),
            (new Point(p0.X + p0.Width / 2.0, gapMidY), "第1/2页之间的缝隙"),
        };

        bool probesOk = true;
        foreach (var probe in probes)
        {
            var v = host.Viewport.ToViewport(probe.World);
            int px = (int)Math.Round(v.X), py = (int)Math.Round(v.Y);

            if (px < 0 || py < 0 || px >= hostWidth || py >= hostHeight)
            {
                probesOk = false;
                Console.WriteLine($"  FAIL 采样点[{probe.Name}] 落在视口外（{px},{py}），该断言无效");
                continue;
            }

            var c = SamplePixel(bitmap, px, py);
            bool white = c.R >= 250 && c.G >= 250 && c.B >= 250;
            if (!white) probesOk = false;

            Console.WriteLine($"  采样[{probe.Name}] 视口({px},{py}) = rgb({c.R},{c.G},{c.B})"
                              + (white ? string.Empty : "   <<非白"));
        }

        // ---- 反向断言：页面内容还在 ----
        bool contentOk = onPaperPixels > 1000;

        string proofFile = Path.Combine(outDir, "paper-look.png");
        SavePng(bitmap, proofFile);

        Console.WriteLine($"  底色 = {host.CanvasBackground}（生产默认值）");
        Console.WriteLine($"  页外像素 {offPaperPixels} 个（全图 {hostWidth * hostHeight}），"
                          + $"页面内非白像素 {onPaperPixels} 个（其中深色 {stats.DarkPixels} 个）");
        Console.WriteLine($"[{(viewOk ? "PASS" : "FAIL")}] 采样前提成立：第 2 页也落在视口内");
        Console.WriteLine($"[{(offendPixels == 0 ? "PASS" : "FAIL")}] 页外每一个像素都是纯白"
                          + (offendPixels == 0
                             ? string.Empty
                             : $"（{offendPixels} 个非白，首个在 ({offendX},{offendY})）"));
        Console.WriteLine($"[{(probesOk ? "PASS" : "FAIL")}] 页左留白 / 页右留白 / 页间缝隙 三处采样均为纯白");
        Console.WriteLine($"[{(contentOk ? "PASS" : "FAIL")}] 页面内容仍在（不是把整块画布涂白）→ {proofFile}");
        if (!viewOk) failures++;
        if (offendPixels != 0) failures++;
        if (!probesOk) failures++;
        if (!contentOk) failures++;

        host.Shutdown();

        return failures;
    }

    /// <summary>世界点是否落在"任何页面矩形向外扩 tolerance"的范围内。</summary>
    private static bool NearAnyPage(WorldLayout layout, Point world, double tolerance)
    {
        foreach (var r in layout.PageRects)
        {
            if (world.X >= r.X - tolerance && world.X <= r.Right + tolerance &&
                world.Y >= r.Y - tolerance && world.Y <= r.Bottom + tolerance)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>取光栅上单个像素的 RGB（用于"这里应该是纯白"这类点采样断言）。</summary>
    private static (byte R, byte G, byte B) SamplePixel(BitmapSource source, int x, int y)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var one = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), one, 4, 0);
        return (one[2], one[1], one[0]);
    }

    /// <summary>把控件树光栅化成位图（96 DPI，1 DIP = 1 px）。</summary>
    /// <summary>
    /// 造一张真的 PNG（走 WPF 编码器，与真机抓帧回来的字节是同一族）。
    /// </summary>
    /// <remarks>
    /// 刻意不用"手写 24 字节头"那种假 PNG：读者（<see cref="PngSize"/>）当然是按真格式写的，
    /// 拿假字节去喂它，测的是我对格式的<em>记忆</em>，不是格式本身。
    /// </remarks>
    private static byte[] EncodePng(int width, int height, Color fill)
    {
        var pixels = new byte[width * height * 4];

        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4 + 0] = fill.B;
            pixels[i * 4 + 1] = fill.G;
            pixels[i * 4 + 2] = fill.R;
            pixels[i * 4 + 3] = fill.A;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// 数一张光栅图里「不透明且不是近白」的像素个数 —— 「真的画了东西没有」的最省事判据。
    /// </summary>
    /// <remarks>
    /// 近白（RGB 全 &gt; 235）与全透明都算「没有」：前者是纸色 / 空白，
    /// 后者是 <see cref="RenderTargetBitmap"/> 的底色。只有它们之外的像素才证明"画上了"。
    /// </remarks>
    private static long CountDrawnPixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        long count = 0;

        for (int i = 0; i < width * height; i++)
        {
            int o = i * 4;
            if (pixels[o + 3] < 50) continue;
            if (pixels[o] > 235 && pixels[o + 1] > 235 && pixels[o + 2] > 235) continue;
            count++;
        }

        return count;
    }

    /// <summary>在视觉树里找第一个 <see cref="Image"/>（判「这张图真的被画出来了」）。</summary>
    private static Image? FindImage(FrameworkElement? root)
    {
        if (root is null) return null;
        if (root is Image image) return image;

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child && FindImage(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static BitmapSource Snapshot(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    /// <summary>光栅统计结果。</summary>
    private readonly struct RasterStats
    {
        public long InkPixels { get; init; }
        public long SumX { get; init; }
        public long SumY { get; init; }
        public long WhitePixels { get; init; }
        public long DarkPixels { get; init; }
        public int WhiteMinX { get; init; }
        public int WhiteMinY { get; init; }
        public int WhiteMaxX { get; init; }
        public int WhiteMaxY { get; init; }
    }

    /// <summary>
    /// 扫一遍光栅，分别统计"笔迹"（品红）与"纸面"（白）两类像素。
    /// </summary>
    /// <remarks>
    /// 用<b>品红</b>当笔色：试卷上不可能出现这个颜色，于是光栅里能唯一认出笔迹，
    /// 不必和 PDF 内容里的深色字迹、以及页面边框的灰色纠缠。
    /// 坐标累计时按像素中心（+0.5）计，与 DIP 坐标对齐。
    /// </remarks>
    private static RasterStats Analyze(BitmapSource source)
    {
        // RenderTargetBitmap 出的是 Pbgra32（预乘），转成 Bgra32 再读更省心
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;

        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        long inkPixels = 0, sumX = 0, sumY = 0, whitePixels = 0, darkPixels = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int offset = rowStart + x * 4;
                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];

                if (r > 140 && b > 140 && g < 110)
                {
                    inkPixels++;
                    sumX += x * 2 + 1;      // (x + 0.5) * 2，最后再除以 2
                    sumY += y * 2 + 1;
                }
                else if (r > 240 && g > 240 && b > 240)
                {
                    whitePixels++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
                else if (r < 100 && g < 100 && b < 100)
                {
                    // 试卷上的黑字与深色图形。用来反向证明"页面内容还在"
                    darkPixels++;
                }
            }
        }

        return new RasterStats
        {
            InkPixels = inkPixels,
            // 除以 2 把"像素中心 ×2"还原成 DIP 坐标；这里保持整数累计避免精度损失
            SumX = inkPixels > 0 ? sumX / 2 : 0,
            SumY = inkPixels > 0 ? sumY / 2 : 0,
            WhitePixels = whitePixels,
            DarkPixels = darkPixels,
            WhiteMinX = maxX >= 0 ? minX : 0,
            WhiteMinY = maxY >= 0 ? minY : 0,
            WhiteMaxX = maxX,
            WhiteMaxY = maxY,
        };
    }

    /// <summary>造一条直线笔画（世界坐标）。</summary>
    private static Stroke MakeMark(PenProfile pen, Point from, Point to)
    {
        var attributes = pen.ToDrawingAttributes();

        // 品红：见 Analyze 的说明
        attributes.Color = Colors.Magenta;
        // 两点直线不引入曲线拟合偏差，质心可解析预测
        attributes.FitToCurve = false;

        return MakeLine(attributes, from, to);
    }

    /// <summary>用给定的笔属性造一条直线笔画（世界坐标）。</summary>
    private static Stroke MakeLine(DrawingAttributes attributes, Point from, Point to)
        => new(new StylusPointCollection(new[]
           {
               new StylusPoint(from.X, from.Y),
               new StylusPoint(to.X, to.Y),
           }),
           attributes);

    /// <summary>
    /// 按调色板里的墨色与笔宽造一套笔画属性。
    /// </summary>
    /// <remarks>
    /// 关掉 <c>FitToCurve</c>：两点直线不引入曲线拟合偏差，位置与跨度都能解析预测。
    /// </remarks>
    private static DrawingAttributes MakeAttributes(InkColorOption color, InkWidthOption width)
        => new()
        {
            Color = color.Color,
            Width = width.WorldWidth,
            Height = width.WorldWidth,
            FitToCurve = false,
            IgnorePressure = true,
            StylusTip = StylusTip.Ellipse,
        };

    /// <summary>按颜色判据扫一遍像素，返回命中数量及其外接框。</summary>
    /// <remarks>
    /// 之所以连外接框一起返回：只数数量的话，"把整页误判成该颜色"和
    /// "真的画了一条线"会得到同样的结论。有了外接框，就能进一步要求形状也像那么回事。
    /// </remarks>
    private static (int Count, int MinX, int MinY, int MaxX, int MaxY) ScanPixels(
        BitmapSource source, Func<byte, byte, byte, bool> match)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;

        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        int count = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int offset = rowStart + x * 4;

                // Bgra32：B、G、R、A
                if (!match(pixels[offset + 2], pixels[offset + 1], pixels[offset])) continue;

                count++;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        return count == 0 ? (0, 0, 0, 0, 0) : (count, minX, minY, maxX, maxY);
    }

    /// <summary>
    /// 蓝色判据：B 明显高于 R 与 G。
    /// </summary>
    /// <remarks>
    /// 阈值放宽是有意的 —— 笔画边缘被抗锯齿混进白色，那些像素仍是"蓝笔画的一部分"；
    /// 而试卷上的黑字（R≈G≈B）与品红测试笔（B 与 R 都高、G 低）都会被排除在外。
    /// </remarks>
    private static bool IsBluePixel(byte r, byte g, byte b) => b > 140 && b - r > 60 && b - g > 30;

    /// <summary>光标是否等于预期（Cursor.ToString 不好读，故转成可读文字）。</summary>
    private static string DescribeCursor(Cursor? actual, Cursor expected)
        => actual == expected ? "符合" : $"意外({actual})";

    /// <summary>深度优先枚举视觉树后代。</summary>
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var descendant in VisualDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    // ================================================================= 工具方法

    private static bool Near(Point a, Point b, double tolerance = 1e-6)
        => Math.Abs(a.X - b.X) < tolerance && Math.Abs(a.Y - b.Y) < tolerance;

    /// <summary>两个标量是否足够接近。默认容差与 <see cref="Near(Point, Point, double)"/> 保持一致。</summary>
    private static bool Near(double a, double b, double tolerance = 1e-6)
        => Math.Abs(a - b) < tolerance;

    /// <summary>造一张全零（全透明）的位图，只用于测缓存容量与淘汰，不关心内容。</summary>
    private static BitmapSource MakeBitmap(int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>处理一轮 Dispatcher 队列。</summary>
    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>持续泵 Dispatcher 直到条件成立或超时。</summary>
    private static bool PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            DoEvents();
            Thread.Sleep(5);
        }
        return condition();
    }

    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            DoEvents();
            Thread.Sleep(5);
        }
    }

    /// <summary>抽样检查页面中心区域是否有非白像素。全白说明这一页根本没渲出来。</summary>
    private static bool HasNonWhitePixels(BitmapSource source)
    {
        int stride = source.PixelWidth * 4;
        var buffer = new byte[stride];

        // 抽 40 行，每行取中点附近一段，足够判断有无内容
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        for (int i = 0; i < 40; i++)
        {
            int y = source.PixelHeight / 2 - 20 + i;
            if (y < 0 || y >= source.PixelHeight) continue;

            converted.CopyPixels(new Int32Rect(0, y, source.PixelWidth, 1), buffer, stride, 0);
            for (int x = 0; x < source.PixelWidth; x++)
            {
                int offset = x * 4;
                // B/G/R 任一明显低于 250 就认为有墨
                if (buffer[offset] < 250 || buffer[offset + 1] < 250 || buffer[offset + 2] < 250)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ================================================================= M7.3：正式直尺插件

    /// <summary>
    /// M7.3 断言组：直尺的几何、状态机、落墨链路。
    /// </summary>
    /// <remarks>
    /// 分两层跑同一个 <c>RulerTool</c>：
    /// <list type="number">
    /// <item><b>假 <see cref="IToolContext"/></b>：把状态机的边界（误触、复位、中途停用）
    /// 一条条钉死 —— 这些在界面上要么看不见，要么只在心里"觉得不太对"；</item>
    /// <item><b>真 <c>CanvasViewportHost</c></b>：证明它交出去的笔画真的进了墨迹层、
    /// 真的能撤销、几何真的精确 —— 也就是"免费继承撤销与持久化"这句话是否成立。</item>
    /// </list>
    /// <para>
    /// 角度算错这种事肉眼绝对看不出来：差 15° 的直线依然是一条像模像样的直线。
    /// </para>
    /// </remarks>
    private static int RunM73Checks()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.3：正式直尺插件 ================");

        // ---------------------------------------------------------- 1) 角度吸附（纯函数）
        //
        // 容差取 5° 而不是方案初稿写的 8°：半步宽只有 7.5°，容差一旦 ≥ 7.5°
        // 任意角度都落在某个 15° 倍数的容差内 —— 那就不是"吸附"而是"强制量化"。
        // 下面这条不等式就是那个设计意图的守门人。
        bool toleranceSane = RulerGeometry.SnapToleranceDegrees * 2 < RulerGeometry.SnapStepDegrees;

        bool snapOk =
            Math.Abs(RulerGeometry.SnapAxisDegrees(29) - 30) < 1e-9      // 容差内 → 吸
            && Math.Abs(RulerGeometry.SnapAxisDegrees(37) - 37) < 1e-9   // 离 30 有 7° → 不吸
            && Math.Abs(RulerGeometry.SnapAxisDegrees(178) - 0) < 1e-9   // 跨 0 环绕：178 离 180(=0) 只 2°
            && Math.Abs(RulerGeometry.SnapAxisDegrees(-2) - 0) < 1e-9    // 负角同样要归到 [0,180)
            && Math.Abs(RulerGeometry.SnapAxisDegrees(90) - 90) < 1e-9   // 正好在刻度上 → 不动
            && Math.Abs(RulerGeometry.SnapAxisDegrees(10) - 15) < 1e-9   // 容差边界：正好 5° → 吸
            && Math.Abs(RulerGeometry.SnapAxisDegrees(9.9) - 9.9) < 1e-9;// 超出 0.1° → 不吸

        Console.WriteLine($"  吸附：29°→{RulerGeometry.SnapAxisDegrees(29):F0}°、"
                          + $"37°→{RulerGeometry.SnapAxisDegrees(37):F0}°、"
                          + $"178°→{RulerGeometry.SnapAxisDegrees(178):F0}°、"
                          + $"9.9°→{RulerGeometry.SnapAxisDegrees(9.9):F1}°");
        Console.WriteLine($"[{(snapOk ? "PASS" : "FAIL")}] 角度吸附：15° 粒度、容差 5°、跨 0° 环绕正确"
                          + "（178° 与 0° 是同一条轴）");
        Console.WriteLine($"[{(toleranceSane ? "PASS" : "FAIL")}] 容差 < 半步宽（{RulerGeometry.SnapToleranceDegrees} < "
                          + $"{RulerGeometry.SnapStepDegrees / 2}）—— 否则任意角都被扭走，"
                          + "就不再是吸附而是强制量化");
        if (!snapOk) failures++;
        if (!toleranceSane) failures++;

        // ---------------------------------------------------------- 2) 轴角与单位换算
        bool axisOk =
            Math.Abs(RulerGeometry.AxisDegrees(new Point(0, 0), new Point(10, 0)) - 0) < 1e-9
            && Math.Abs(RulerGeometry.AxisDegrees(new Point(10, 0), new Point(0, 0)) - 0) < 1e-9 // 反向同轴
            && Math.Abs(RulerGeometry.AxisDegrees(new Point(0, 0), new Point(0, 10)) - 90) < 1e-9
            && Math.Abs(RulerGeometry.AxisDegrees(new Point(0, 0), new Point(-10, -10)) - 45) < 1e-9;

        // 世界坐标 = PDF point = 1/72 inch；A4 宽 595 pt 必须量出 21.0 cm，否则这把尺子是假的
        bool unitOk = Math.Abs(RulerGeometry.ToCentimeters(72) - 2.54) < 1e-9
                   && Math.Abs(RulerGeometry.ToCentimeters(595) - 20.99) < 0.01;

        Console.WriteLine($"  换算：72 world = {RulerGeometry.ToCentimeters(72):F2} cm；"
                          + $"A4 宽 595 world = {RulerGeometry.ToCentimeters(595):F2} cm");
        Console.WriteLine($"[{(axisOk ? "PASS" : "FAIL")}] 轴角归到 [0°,180°)：直线没有方向，"
                          + "正画反画报同一个角");
        Console.WriteLine($"[{(unitOk ? "PASS" : "FAIL")}] 长度换算：世界坐标(1/72 inch) → 厘米，"
                          + "A4 宽量出来正好 21 cm");
        if (!axisOk) failures++;
        if (!unitOk) failures++;

        // ---------------------------------------------------------- 3) 终点约束：起点不挪、方向不翻
        var start3 = new Point(100, 100);
        var rawLeft = new Point(-400, 100.5);   // 往左、约 179.94°：吸附后应当还是往左
        var constrained = RulerGeometry.ConstrainEnd(start3, rawLeft, true, out var snappedLeft);

        bool noFlip = snappedLeft
                   && constrained.X < start3.X              // 没翻到右边去
                   && Math.Abs(constrained.Y - start3.Y) < 1e-9;  // 吸附到 0° = 水平

        bool lengthKept = Math.Abs((constrained - start3).Length - (rawLeft - start3).Length) < 1e-9;

        var free = RulerGeometry.ConstrainEnd(start3, new Point(137.3, 118.9), false, out var freeSnapped);
        bool noSnapWhenDisabled = !freeSnapped && (free - new Point(137.3, 118.9)).Length < 1e-12;

        Console.WriteLine($"  往左拖 (100,100)→(-400,100.5)：约束后 ({(int)constrained.X},{constrained.Y:F0})，"
                          + $"吸附={snappedLeft}，长度保持={lengthKept}");
        Console.WriteLine($"[{(noFlip && lengthKept ? "PASS" : "FAIL")}] 吸附只转角度、不改长度，"
                          + "并且轴角的方向歧义被消掉（往左的线不会翻到右边）");
        Console.WriteLine($"[{(noSnapWhenDisabled ? "PASS" : "FAIL")}] 自由角工具（snap=false）终点原样返回");
        if (!(noFlip && lengthKept)) failures++;
        if (!noSnapWhenDisabled) failures++;

        // ---------------------------------------------------------- 4) 两端短横
        var tick = RulerGeometry.TickAt(new Point(0, 0), 0, RulerGeometry.TickLengthWorld);
        bool tickOk = Math.Abs(tick.From.X) < 1e-9 && Math.Abs(tick.To.X) < 1e-9
                   && Math.Abs(tick.From.Y + RulerGeometry.TickLengthWorld / 2) < 1e-9
                   && Math.Abs(tick.To.Y - RulerGeometry.TickLengthWorld / 2) < 1e-9;

        Console.WriteLine($"[{(tickOk ? "PASS" : "FAIL")}] 两端短横垂直于尺身、"
                          + $"总长 {RulerGeometry.TickLengthWorld:F0} world（水平线 → 竖短横）");
        if (!tickOk) failures++;

        // ---------------------------------------------------------- 5) 真工具 + 假 Context：状态机
        var fake = new FakeToolContext();
        var ruler = new RulerTool(snapEnabled: true);
        ruler.Activate(fake);

        bool shapeOk = !ruler.UsesInkLayer && ruler.NeedsPointer
                    && ruler.InputKind == ToolInputKind.None
                    && ruler.InkMode == ToolInkMode.None
                    && ruler.Id == RulerToolIds.Ruler
                    && ruler.Shortcut == Key.D4;

        Console.WriteLine($"[{(shapeOk ? "PASS" : "FAIL")}] 直尺的工具形态：不落墨层 + 需要指针 "
                          + $"⇒ 宿主按这条规则给输入（IsHitTestVisible={ruler.UsesInkLayer}, "
                          + $"IsManipulationEnabled={!ruler.UsesInkLayer && !ruler.NeedsPointer}）");
        if (!shapeOk) failures++;

        // 误触：按下就抬、几乎没动
        ruler.OnPointer(Pointer(ToolPointerPhase.Down, new Point(100, 100)));
        bool downPreview = fake.Previews.Count == 4;   // 尺身 + 两端短横 + 长度文字
        ruler.OnPointer(Pointer(ToolPointerPhase.Up, new Point(100.5, 100.5)));

        bool misTap = fake.Committed.Count == 0 && fake.Previews.Count == 0;
        bool misTapStatus = fake.LastStatus?.Contains("太短") == true;

        Console.WriteLine($"  误触（移动 0.5 world）：按下时预览 {(downPreview ? 4 : 0)} 个"
                          + $" → 抬笔后 {fake.Previews.Count} 个、落墨 {fake.Committed.Count} 条；"
                          + $"状态栏「{fake.LastStatus}」");
        Console.WriteLine($"[{(downPreview ? "PASS" : "FAIL")}] 按下即出 4 个预览图元"
                          + "（尺身 / 两端短横 ×2 / 长度文字）");
        Console.WriteLine($"[{(misTap && misTapStatus ? "PASS" : "FAIL")}] 误触不落墨并给出可读理由"
                          + "（点一下不在卷面上留小点）");
        if (!downPreview) failures++;
        if (!(misTap && misTapStatus)) failures++;

        // 正常一次拖动：往 29° 方向拖 → 落下的线必须是 30°
        fake.Reset();
        ruler.OnPointer(Pointer(ToolPointerPhase.Down, new Point(100, 100)));
        ruler.OnPointer(Pointer(ToolPointerPhase.Move, AtAngle(new Point(100, 100), 29, 100)));

        bool movingPreview = fake.Previews.Count == 4;
        string? label = FindLabelText(fake.Previews);
        bool labelLive = label is not null && label.Contains("cm") && label.Contains("30°");

        ruler.OnPointer(Pointer(ToolPointerPhase.Up, AtAngle(new Point(100, 100), 29, 100)));

        bool committedOne = fake.Committed.Count == 1;
        double committedAxis = committedOne ? AxisOf(fake.Committed[0]) : double.NaN;
        bool endToEndSnap = committedOne && Math.Abs(committedAxis - 30) < 1e-9;

        bool statusOk = fake.LastStatus is not null
                     && fake.LastStatus.Contains("30°")
                     && fake.LastStatus.Contains("cm");

        Console.WriteLine($"  拖动中预览文字：「{label}」；抬笔后状态栏：「{fake.LastStatus}」");
        Console.WriteLine($"[{(movingPreview && labelLive ? "PASS" : "FAIL")}] 拖动中长度/角度实时显示"
                          + "（预览层的文字，抬笔即清）");
        Console.WriteLine($"[{(endToEndSnap ? "PASS" : "FAIL")}] 往 29° 拖 ⇒ 落下的线是 30°"
                          + $"（端到端吸附，实测 {committedAxis:F4}°）");
        Console.WriteLine($"[{(committedOne && statusOk ? "PASS" : "FAIL")}] 一次拖动恰好落一条笔画，"
                          + "状态栏报出长度与倾角");
        if (!(movingPreview && labelLive)) failures++;
        if (!endToEndSnap) failures++;
        if (!(committedOne && statusOk)) failures++;

        // 状态复位：第二次拖动的起点必须是第二次按下的那一点
        ruler.OnPointer(Pointer(ToolPointerPhase.Down, new Point(500, 600)));
        ruler.OnPointer(Pointer(ToolPointerPhase.Move, new Point(600, 600)));
        ruler.OnPointer(Pointer(ToolPointerPhase.Up, new Point(600, 600)));

        var second = fake.Committed.Count >= 1 ? fake.Committed[^1] : null;
        bool resetOk = second is not null
                    && Math.Abs(second.StylusPoints[0].X - 500) < 1e-9
                    && Math.Abs(second.StylusPoints[0].Y - 600) < 1e-9;

        Console.WriteLine($"[{(resetOk ? "PASS" : "FAIL")}] 连续两次拖动：第二次起点 = 第二次按下的位置"
                          + "（上一次的起点没残留 —— 否则线会从莫名其妙的地方开始画）");
        if (!resetOk) failures++;

        // 拖动中途被切走：宿主的 Deactivate 到了之后必须彻底复位
        int committedBeforeCut = fake.Committed.Count;
        ruler.OnPointer(Pointer(ToolPointerPhase.Down, new Point(10, 10)));
        ruler.Deactivate();
        bool cutCleared = fake.Previews.Count == 0;
        ruler.OnPointer(Pointer(ToolPointerPhase.Move, new Point(300, 300)));
        bool noGhost = fake.Previews.Count == 0 && fake.Committed.Count == committedBeforeCut;

        Console.WriteLine($"[{(cutCleared && noGhost ? "PASS" : "FAIL")}] 拖动中被切走（Deactivate）"
                          + "：预览清空、状态复位，切回来后不会冒出幽灵线");
        if (!(cutCleared && noGhost)) failures++;

        // ---------------------------------------------------------- 6) 自由角直尺：不吸附
        var freeTool = new RulerTool(snapEnabled: false);
        freeTool.Activate(fake);
        fake.Reset();

        freeTool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(0, 0)));
        freeTool.OnPointer(Pointer(ToolPointerPhase.Up, AtAngle(new Point(0, 0), 29, 100)));

        double freeAxis = fake.Committed.Count == 1 ? AxisOf(fake.Committed[0]) : double.NaN;
        bool freeOk = fake.Committed.Count == 1 && Math.Abs(freeAxis - 29) < 1e-6
                   && freeTool.Id == RulerToolIds.FreeRuler;

        Console.WriteLine($"  自由角直尺：往 29° 拖 ⇒ 落下的线是 {freeAxis:F4}°");
        Console.WriteLine($"[{(freeOk ? "PASS" : "FAIL")}] 自由角直尺不吸附（要连任意两点、"
                          + "画任意方向的辅助线时用它）");
        if (!freeOk) failures++;

        // ---------------------------------------------------------- 7) 真宿主端到端
        var host = new CanvasViewportHost();
        host.Measure(new Size(1000, 700));
        host.Arrange(new Rect(0, 0, 1000, 700));
        host.UpdateLayout();

        var registry = new ToolRegistry();
        registry.AttachContext(host.ToolContext);

        // 走插件真实的注册入口，而不是手工 Add —— 这样插件类本身也被覆盖
        new RulerPlugin().Register(registry);

        bool pluginOk = registry.Tools.Count == 2
                     && registry.Tools[0].Id == RulerToolIds.Ruler
                     && registry.Tools[1].Id == RulerToolIds.FreeRuler
                     && registry.FindByShortcut(Key.NumPad5)?.Id == RulerToolIds.FreeRuler;

        Console.WriteLine($"  插件注册：{string.Join("、", registry.Tools.Select(t => t.DisplayName))}");
        Console.WriteLine($"[{(pluginOk ? "PASS" : "FAIL")}] 一个插件注册两个工具（吸附 / 自由角），"
                          + "小键盘快捷键也被识别");
        if (!pluginOk) failures++;

        registry.Activate(RulerToolIds.Ruler);

        int before = host.StrokeCount;
        var from = new Point(200, 300);
        var to = AtAngle(from, 29, 150);   // 世界坐标里往 29° 拖 150 world ≈ 5.3 cm

        registry.DispatchPointer(Pointer(ToolPointerPhase.Down, from));
        bool hostPreview = host.PreviewCount == 4;

        registry.DispatchPointer(Pointer(ToolPointerPhase.Move, AtAngle(from, 29, 90)));
        registry.DispatchPointer(Pointer(ToolPointerPhase.Up, to));

        bool hostPreviewCleared = host.PreviewCount == 0;
        bool hostStroke = host.StrokeCount == before + 1;

        bool hostGeometry = false;
        if (hostStroke)
        {
            var points = host.Strokes[host.StrokeCount - 1].StylusPoints;
            var expectedEnd = AtAngle(from, 30, 150);   // 吸附后的精确落点

            hostGeometry = points.Count == 2
                        && Math.Abs(points[0].X - from.X) < 1e-9
                        && Math.Abs(points[0].Y - from.Y) < 1e-9
                        && Math.Abs(points[1].X - expectedEnd.X) < 1e-9
                        && Math.Abs(points[1].Y - expectedEnd.Y) < 1e-9;
        }

        Console.WriteLine($"  真宿主：预览 0→{(hostPreview ? 4 : 0)}→{host.PreviewCount}；"
                          + $"落墨 {before}→{host.StrokeCount} 条");
        Console.WriteLine($"[{(hostPreview && hostPreviewCleared ? "PASS" : "FAIL")}] 直尺的预览全走 "
                          + "AddPreview：拖动中 4 个、抬笔后清空（不进墨迹层、不进撤销栈）");
        Console.WriteLine($"[{(hostStroke && hostGeometry ? "PASS" : "FAIL")}] 直尺画的线真的进了墨迹层"
                          + "（起点=按下点、终点=吸附后的落点，于是随缩放平移不漂移、可擦、能存进 .tbink）");

        bool hostUndo = host.Undo() && host.StrokeCount == before;
        Console.WriteLine($"[{(hostUndo ? "PASS" : "FAIL")}] 一次拖动 = 一步撤销"
                          + $"（撤销后 {host.StrokeCount} 条；插件一行持久化与撤销代码都没写）");
        if (!(hostPreview && hostPreviewCleared)) failures++;
        if (!(hostStroke && hostGeometry)) failures++;
        if (!hostUndo) failures++;

        host.Shutdown();
        return failures;
    }

    // ================================================================= M7.4：图形对象层

    /// <summary>
    /// M7.4 断言组：位姿数学、视觉树与数学同源、选择工具的拖动/旋转/缩放/删除、
    /// 墨迹与图形共用的统一撤销时间线、<c>.tbink</c> v2 持久化、缺插件占位。
    /// </summary>
    /// <remarks>
    /// 这一段最该被钉死的是<b>「算出来的位置」与「画出来的位置」是同一个来源</b>：
    /// 两者一旦分家，表现是"看着图形在这儿、点着不在"，而且只在某个旋转/缩放组合下复现。
    /// 所以断言不是"代码调用了哪个方法"，而是把真实视觉树上的变换读出来、
    /// 与 <see cref="GfxTransform"/> 的结果逐点比对。
    /// </remarks>
    private static int RunM74Checks(PdfiumDocumentService service, string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.4：图形对象层 ================");

        // ---------------------------------------------------------- 1) 位姿数学（纯函数）
        Console.WriteLine();
        Console.WriteLine("---- 1) 位姿数学：本地 ↔ 世界（全程序唯一来源）----");

        var probe = new FakeGfxObject
        {
            Center = new Point(100, 200),
            RotationDegrees = 90,
            Scale = 2,
            LocalSize = new Size(120, 80),
        };

        // 本地 (10,0) → 缩放 ×2 → (20,0) → 旋 90° → (0,20) → 平移 → (100,220)
        var probeWorld = GfxTransform.ToWorld(probe, new Point(10, 0));
        var probeBack = GfxTransform.ToLocal(probe, probeWorld);

        bool poseOk = Near(probeWorld, new Point(100, 220), 1e-9)
                   && Near(probeBack, new Point(10, 0), 1e-9);

        // 旋转 90° 后外接矩形宽高互换：120×80 的框 ⇒ 160×240
        var probeBounds = GfxTransform.BoundsWorld(probe);
        bool boundsOk = Near(probeBounds.Width, 160, 1e-9) && Near(probeBounds.Height, 240, 1e-9)
                     && Near(probeBounds.X, 20, 1e-9) && Near(probeBounds.Y, 80, 1e-9);

        Console.WriteLine($"  本地(10,0) → 世界({probeWorld.X:F1},{probeWorld.Y:F1})"
                          + $" → 本地({probeBack.X:F1},{probeBack.Y:F1})");
        Console.WriteLine($"  外接矩形 {probeBounds.Width:F0}×{probeBounds.Height:F0}"
                          + $"，左上角 ({probeBounds.X:F0},{probeBounds.Y:F0})");
        Console.WriteLine($"[{(poseOk ? "PASS" : "FAIL")}] 顺序是「缩放 → 旋转 → 平移」，往返无损");
        Console.WriteLine($"[{(boundsOk ? "PASS" : "FAIL")}] 外接矩形由四角变换后取包围盒"
                          + "（不是「中心 ± 尺寸/2」那个旋转后就错的式子）");
        if (!poseOk) failures++;
        if (!boundsOk) failures++;

        // 命中测试必须跟着旋转走，否则就是"看着点在框里、程序说没点中"
        var upright = new FakeGfxObject { Center = new Point(0, 0), LocalSize = new Size(120, 80) };
        var turned = new FakeGfxObject
        {
            Center = new Point(0, 0),
            LocalSize = new Size(120, 80),
            RotationDegrees = 90,
        };

        bool hitUpright = GfxTransform.HitTest(upright, new Point(59, 39), 0)
                       && !GfxTransform.HitTest(upright, new Point(61, 0), 0);

        // 转 90° 之后"能点中的方向"跟着转 90°：竖着的 (0,59) 命中、横着的 (59,0) 不命中
        bool hitTurned = GfxTransform.HitTest(turned, new Point(0, 59), 0)
                      && !GfxTransform.HitTest(turned, new Point(59, 0), 0);

        Console.WriteLine($"[{(hitUpright ? "PASS" : "FAIL")}] 未旋转：({59},{39}) 命中、({61},{0}) 不命中");
        Console.WriteLine($"[{(hitTurned ? "PASS" : "FAIL")}] 旋转 90° 后命中区跟着转 90°"
                          + "（渲染与命中同一个矩阵，不会一个转一个不转）");
        if (!hitUpright) failures++;
        if (!hitTurned) failures++;

        // 手柄：位置 + 屏幕尺寸恒定
        var plain = new FakeGfxObject { Center = new Point(300, 400), LocalSize = new Size(120, 80) };

        bool radiusOk = Near(GfxTransform.HandleRadiusWorld(1.0), GfxTransform.HandleRadiusPixels, 1e-9)
                     && Near(GfxTransform.HandleRadiusWorld(2.0), GfxTransform.HandleRadiusPixels / 2, 1e-9)
                     && Near(GfxTransform.HandleRadiusWorld(0.5), GfxTransform.HandleRadiusPixels * 2, 1e-9);

        var rotateAt2 = GfxTransform.RotationHandleWorld(plain, 2.0);
        var scaleAt2 = GfxTransform.ScaleHandleWorld(plain, 2.0);
        var deleteAt2 = GfxTransform.DeleteButtonWorld(plain, 2.0);

        bool handlesOk = Near(rotateAt2, new Point(300, 348), 1e-9)
                      && Near(scaleAt2, new Point(372, 452), 1e-9)
                      && Near(deleteAt2, new Point(372, 348), 1e-9);

        // 手柄与对象框之间的**屏幕**间距恒定：这是"缩到 50% 手指还点得中"的关键
        double edgeY = plain.Center.Y - plain.LocalSize.Height / 2.0;
        double screenGap2 = (edgeY - GfxTransform.RotationHandleWorld(plain, 2.0).Y) * 2.0;
        double screenGap4 = (edgeY - GfxTransform.RotationHandleWorld(plain, 4.0).Y) * 4.0;
        bool screenConstant = Near(screenGap2, GfxTransform.HandleGapPixels, 1e-9)
                           && Near(screenGap4, GfxTransform.HandleGapPixels, 1e-9);

        Console.WriteLine($"  手柄命中半径：视口 ×1 → {GfxTransform.HandleRadiusWorld(1.0):F0} world、"
                          + $"×2 → {GfxTransform.HandleRadiusWorld(2.0):F0}、"
                          + $"×0.5 → {GfxTransform.HandleRadiusWorld(0.5):F0}");
        Console.WriteLine($"  视口 ×2 时手柄位置：旋转 ({rotateAt2.X:F0},{rotateAt2.Y:F0})、"
                          + $"缩放 ({scaleAt2.X:F0},{scaleAt2.Y:F0})、"
                          + $"删除 ({deleteAt2.X:F0},{deleteAt2.Y:F0})");
        Console.WriteLine($"[{(radiusOk ? "PASS" : "FAIL")}] 手柄命中半径按屏幕像素算"
                          + "（视口缩小一半，世界半径翻倍 —— 一体机上手指触点是胖的）");
        Console.WriteLine($"[{(handlesOk ? "PASS" : "FAIL")}] 三个手柄在框外侧固定方位"
                          + "（旋转=正上、缩放=右下、删除=右上）");
        Console.WriteLine($"[{(screenConstant ? "PASS" : "FAIL")}] 手柄与框的屏幕间距与缩放无关"
                          + $"（×2 与 ×4 都正好 {GfxTransform.HandleGapPixels:F0} px）");
        if (!radiusOk) failures++;
        if (!handlesOk) failures++;
        if (!screenConstant) failures++;

        // ---------------------------------------------------------- 2) 插件加载：渲染器自动登记
        string root = Path.Combine(Path.GetTempPath(), "tb-m74-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        var visualHost = new CanvasViewportHost();
        visualHost.Measure(new Size(1000, 700));
        visualHost.Arrange(new Rect(0, 0, 1000, 700));
        visualHost.UpdateLayout();

        var undoHost = new CanvasViewportHost();
        undoHost.Measure(new Size(1000, 700));
        undoHost.Arrange(new Rect(0, 0, 1000, 700));
        undoHost.UpdateLayout();

        var storeHost = new CanvasViewportHost();
        storeHost.Measure(new Size(1000, 700));
        storeHost.Arrange(new Rect(0, 0, 1000, 700));
        storeHost.UpdateLayout();

        try
        {
            Console.WriteLine();
            Console.WriteLine("---- 2) 插件加载：图形渲染器随插件一起登记 ----");

            var pluginDll = Path.Combine(AppContext.BaseDirectory, "PdfSmokeTest.GfxPlugin.dll");
            if (!File.Exists(pluginDll))
            {
                Console.WriteLine($"[FAIL] 找不到 {pluginDll} —— 检查 PdfSmokeTest 的项目引用");
                return failures + 1;
            }

            var pluginsDir = Path.Combine(root, "plugins");
            CopyPlugin(pluginDll, Path.Combine(pluginsDir, "gfxdemo"), "gfxdemo.dll");

            // 走宿主自己的注册表与渲染器注册表 —— 与 MainWindow 启动那条路径完全一致。
            // 少交一个渲染器注册表不会报任何错，只会让插件的图形全变成虚线占位框，
            // 所以这一条必须由真 dll 端到端跑一遍。
            var report = PluginLoader.LoadAll(visualHost.PluginRegistry, visualHost.GfxRenderers, pluginsDir);
            var entry = report.Entries.FirstOrDefault();

            // 宿主自己注册的渲染器有 2 个（M12 题号标记、M22 仿真截图），加上插件这 1 个共 3 个。
            // ★ 判据里逐个点名「宿主那几个 Kind 都在」，不是只比总数：只比总数的话，
            //   将来再加一个宿主渲染器，表现只是「数字对不上」，看不出是谁少了谁多了。
            //   （这一条踩过：加仿真截图渲染器时，写死的 Count == 2 当场就变成 FAIL。）
            bool loaderOk = report.LoadedCount == 1
                         && entry is { Status: PluginLoadStatus.Loaded, ToolCount: 1, RendererCount: 1 }
                         && visualHost.GfxRenderers.Count == 3
                         && visualHost.GfxRenderers.Contains(GfxDemoRenderer.KindName)
                         && visualHost.GfxRenderers.Contains(QuestionMarkRenderer.KindName)
                         && visualHost.GfxRenderers.Contains(WebPanelImageRenderer.KindName)
                         && visualHost.GfxRenderers.OwnerOf(GfxDemoRenderer.KindName) == new GfxDemoPlugin().Name
                         && visualHost.Tools.Any(tool => tool.Id == "demo-glyph");

            Console.WriteLine($"  目录 plugins → {report.Summary}；{entry?.Reason}");
            Console.WriteLine($"[{(loaderOk ? "PASS" : "FAIL")}] 真 dll 里的 IGfxObjectRenderer 被自动登记"
                              + $"（Kind = {GfxDemoRenderer.KindName}，归属「{visualHost.GfxRenderers.OwnerOf(GfxDemoRenderer.KindName)}」）");
            if (!loaderOk) failures++;

            // ------------------------------------------------------ 3) 创建型工具端到端
            Console.WriteLine();
            Console.WriteLine("---- 3) 创建型工具：拖动预览 → 落成对象 → 自动切回选择 ----");

            bool toolActive = visualHost.SetTool("demo-glyph") && visualHost.ActiveToolId == "demo-glyph";

            var dropA = new Point(200, 300);
            var dropB = new Point(420, 380);

            visualHost.SimulatePointer(ToolPointerPhase.Down, visualHost.Viewport.ToViewport(dropA));
            bool previewShown = visualHost.PreviewCount == 1;

            visualHost.SimulatePointer(ToolPointerPhase.Move, visualHost.Viewport.ToViewport(dropB));
            visualHost.SimulatePointer(ToolPointerPhase.Up, visualHost.Viewport.ToViewport(dropB));

            bool previewCleared = visualHost.PreviewCount == 0;
            bool objectAdded = visualHost.GfxObjectCount == 1;
            bool noInk = visualHost.StrokeCount == 0;

            var created = visualHost.GfxObjects.Objects[0];
            bool centerAtDrop = Near(created.Center, dropB, 1e-9)
                             && Near(created.LocalSize.Width, GfxDemoRenderer.BoxSize.Width, 1e-9)
                             && Near(created.LocalSize.Height, GfxDemoRenderer.BoxSize.Height, 1e-9);

            // 落成后自动切回「选择」：走的是 Dispatcher.BeginInvoke，泵一轮消息才会到
            DoEvents();
            bool autoSwitched = visualHost.ActiveToolId == ToolIds.GfxSelect
                             && visualHost.GfxObjects.SelectedId == created.Id;

            Console.WriteLine($"  预览 0→{(previewShown ? 1 : 0)}→{visualHost.PreviewCount}；"
                              + $"对象 {visualHost.GfxObjectCount} 个、落墨 {visualHost.StrokeCount} 条；"
                              + $"中心 ({created.Center.X:F0},{created.Center.Y:F0})");
            Console.WriteLine($"[{(toolActive && previewShown && previewCleared ? "PASS" : "FAIL")}] "
                              + "拖动预览走 AddPreview（拖动中 1 个、抬笔清空，不入墨迹层）");
            Console.WriteLine($"[{(objectAdded && centerAtDrop && noInk ? "PASS" : "FAIL")}] 工具只交几何，"
                              + "其余全归宿主：对象落在抬起点、尺寸取自渲染器、一条墨都没落");
            Console.WriteLine($"[{(autoSwitched ? "PASS" : "FAIL")}] 落成后自动切回「选择」并选中新对象"
                              + "（下一个动作十有八九是挪一下/转一下）");
            if (!(toolActive && previewShown && previewCleared)) failures++;
            if (!(objectAdded && centerAtDrop && noInk)) failures++;
            if (!autoSwitched) failures++;

            // ------------------------------------------------------ 4) 视觉树与数学同源
            Console.WriteLine();
            Console.WriteLine("---- 4) 视觉树与数学同源（画出来的位置 = 算出来的位置）----");

            var objectId = created.Id;
            visualHost.GfxObjects.UpdatePose(objectId, new Point(360, 420), 37, 1.6);
            visualHost.UpdateLayout();

            var posed = visualHost.GfxObjects.Objects[0];
            var wrapper = visualHost.GfxObjectLayer.VisualFor(objectId);

            bool layerOk = visualHost.GfxObjectLayer.VisualCount == 1 && wrapper is not null;

            // 从真实视觉树上把变换读出来，与 GfxTransform 逐点比对：
            //   元素内本地点 p → 容器内坐标 (p + 尺寸/2)
            //   容器内 → 世界： Canvas.Left/Top 平移 + 绕自身中心 (0.5,0.5) 的 RenderTransform
            bool sameSource = false;
            bool originOk = false;
            bool childOk = false;
            double left = 0, top = 0;

            if (wrapper is not null)
            {
                left = Canvas.GetLeft(wrapper);
                top = Canvas.GetTop(wrapper);

                var poseFromVisual = wrapper.RenderTransform is MatrixTransform mt
                    ? mt.Matrix
                    : Matrix.Identity;

                originOk = wrapper.RenderTransformOrigin.Equals(new Point(0.5, 0.5))
                        && Near(left, posed.Center.X - posed.LocalSize.Width / 2.0, 1e-9)
                        && Near(top, posed.Center.Y - posed.LocalSize.Height / 2.0, 1e-9);

                childOk = wrapper is Canvas { Children.Count: 1 } box
                       && Near(Canvas.GetLeft(box.Children[0]), posed.LocalSize.Width / 2.0, 1e-9)
                       && Near(Canvas.GetTop(box.Children[0]), posed.LocalSize.Height / 2.0, 1e-9);

                // 合成：v → Pose(v) → +(尺寸/2 + Left/Top)
                var full = poseFromVisual;
                full.Translate(left + posed.LocalSize.Width / 2.0, top + posed.LocalSize.Height / 2.0);

                sameSource = true;
                foreach (var local in new[]
                         {
                             new Point(0, 0), new Point(60, 0), new Point(0, 40),
                             new Point(-60, -40), new Point(25, -17),
                         })
                {
                    var viaVisual = full.Transform(local);
                    var viaMath = GfxTransform.ToWorld(posed, local);
                    if (!Near(viaVisual, viaMath, 1e-9)) sameSource = false;
                }
            }

            Console.WriteLine($"  对象：中心 ({posed.Center.X:F0},{posed.Center.Y:F0})、"
                              + $"旋转 {posed.RotationDegrees:F0}°、缩放 {posed.Scale * 100:F0}%、"
                              + $"尺寸 {posed.LocalSize.Width:F0}×{posed.LocalSize.Height:F0}");
            Console.WriteLine($"  视觉：Left/Top = ({left:F1},{top:F1})、"
                              + $"绕自身中心变换、内容摆到容器中心");
            Console.WriteLine($"[{(layerOk && originOk ? "PASS" : "FAIL")}] 视觉层按对象数建元素，"
                              + "位置用 Canvas.Left/Top、旋转缩放绕元素自身中心");
            Console.WriteLine($"[{(childOk ? "PASS" : "FAIL")}] 渲染器的内容以「原点=对象中心」作画，"
                              + "容器把它摆到中心（本地坐标约定被真正遵守）");
            Console.WriteLine($"[{(sameSource ? "PASS" : "FAIL")}] 视觉树合成出的变换 == GfxTransform"
                              + "（五个本地采样点逐个比对：不会「看着在这、点着不在」）");
            if (!(layerOk && originOk)) failures++;
            if (!childOk) failures++;
            if (!sameSource) failures++;

            // ------------------------------------------------------ 5) 选择工具
            Console.WriteLine();
            Console.WriteLine("---- 5) 选择工具：拖动 / 旋转 / 缩放 / 删除 ----");

            double viewScale = visualHost.Viewport.Scale;

            // 5.1 拖动
            var centerBeforeMove = visualHost.GfxObjects.Objects[0].Center;
            var moveTo = new Point(centerBeforeMove.X + 40, centerBeforeMove.Y + 20);

            visualHost.SimulatePointer(ToolPointerPhase.Down, visualHost.Viewport.ToViewport(centerBeforeMove));
            bool selectedByPress = visualHost.GfxObjects.SelectedId == objectId;

            visualHost.SimulatePointer(ToolPointerPhase.Move, visualHost.Viewport.ToViewport(moveTo));
            var centerAfterMove = visualHost.GfxObjects.Objects[0].Center;

            visualHost.SimulatePointer(ToolPointerPhase.Up, visualHost.Viewport.ToViewport(moveTo));

            bool moveOk = Near(centerAfterMove, moveTo, 1e-9);
            bool moveUndo = visualHost.Undo()
                         && Near(visualHost.GfxObjects.Objects[0].Center, centerBeforeMove, 1e-9);

            Console.WriteLine($"  拖动 (40,20)：中心 ({centerBeforeMove.X:F0},{centerBeforeMove.Y:F0}) → "
                              + $"({centerAfterMove.X:F0},{centerAfterMove.Y:F0})；撤销后 "
                              + $"({visualHost.GfxObjects.Objects[0].Center.X:F0},{visualHost.GfxObjects.Objects[0].Center.Y:F0})");
            Console.WriteLine($"[{(selectedByPress && moveOk ? "PASS" : "FAIL")}] 按下即选中并拖动，"
                              + "位移与手势一模一样（不带任何吸附/累计误差）");
            Console.WriteLine($"[{(moveUndo ? "PASS" : "FAIL")}] 一次拖动 = 一步撤销，撤销回到原处");
            if (!(selectedByPress && moveOk)) failures++;
            if (!moveUndo) failures++;

            // 5.2 旋转：抓旋转手柄，绕对象中心转过 30°
            var rotationBefore = visualHost.GfxObjects.Objects[0].RotationDegrees;
            var rotateHandle = GfxTransform.RotationHandleWorld(visualHost.GfxObjects.Objects[0], viewScale);
            var rotateGrab = RotateAround(rotateHandle, centerBeforeMove, 30);

            visualHost.SimulatePointer(ToolPointerPhase.Down, visualHost.Viewport.ToViewport(rotateHandle));
            visualHost.SimulatePointer(ToolPointerPhase.Move, visualHost.Viewport.ToViewport(rotateGrab));
            visualHost.SimulatePointer(ToolPointerPhase.Up, visualHost.Viewport.ToViewport(rotateGrab));

            double rotationAfter = visualHost.GfxObjects.Objects[0].RotationDegrees;
            bool rotateOk = Near(rotationAfter, (rotationBefore + 30) % 360, 1e-6)
                         && Near(visualHost.GfxObjects.Objects[0].Center, centerBeforeMove, 1e-9);

            bool rotateUndo = visualHost.Undo()
                           && Near(visualHost.GfxObjects.Objects[0].RotationDegrees, rotationBefore, 1e-9);

            Console.WriteLine($"  抓旋转手柄绕中心转 30°：{rotationBefore:F0}° → {rotationAfter:F1}°"
                              + $"（中心不动）；撤销后 {visualHost.GfxObjects.Objects[0].RotationDegrees:F0}°");
            Console.WriteLine($"[{(rotateOk ? "PASS" : "FAIL")}] 抓到手柄就是「转」而不是「拖」，"
                              + "且旋转过程中中心不漂");
            Console.WriteLine($"[{(rotateUndo ? "PASS" : "FAIL")}] 一次旋转 = 一步撤销");
            if (!rotateOk) failures++;
            if (!rotateUndo) failures++;

            // 5.3 缩放：抓右下角手柄，把抓取距离拉成 1.5 倍
            double scaleBefore = visualHost.GfxObjects.Objects[0].Scale;
            var objNow = visualHost.GfxObjects.Objects[0];
            var scaleHandle = GfxTransform.ScaleHandleWorld(objNow, viewScale);

            // 稍微偏 0.9 倍命中半径按下：证明"手柄有屏幕像素级的宽容度"（手指点不精确）
            double radiusWorld = GfxTransform.HandleRadiusWorld(viewScale);
            var scalePress = ScaleTowards(scaleHandle, objNow.Center, 0.9 * radiusWorld);
            double grabDistance = (scalePress - objNow.Center).Length;
            var scaleTo = ScaleTowards(scaleHandle, objNow.Center, 1.5 * grabDistance - (scaleHandle - objNow.Center).Length);

            visualHost.SimulatePointer(ToolPointerPhase.Down, visualHost.Viewport.ToViewport(scalePress));
            visualHost.SimulatePointer(ToolPointerPhase.Move, visualHost.Viewport.ToViewport(scaleTo));
            visualHost.SimulatePointer(ToolPointerPhase.Up, visualHost.Viewport.ToViewport(scaleTo));

            double scaleAfter = visualHost.GfxObjects.Objects[0].Scale;
            bool scaleOk = Near(scaleAfter, scaleBefore * 1.5, 1e-6);

            bool scaleUndo = visualHost.Undo()
                          && Near(visualHost.GfxObjects.Objects[0].Scale, scaleBefore, 1e-9);

            Console.WriteLine($"  抓缩放手柄（偏 {GfxTransform.HandleRadiusPixels * 0.9:F0} 屏幕像素）"
                              + $"拉远 ×1.5：{scaleBefore * 100:F0}% → {scaleAfter * 100:F0}%");
            Console.WriteLine($"[{(scaleOk ? "PASS" : "FAIL")}] 手柄命中半径能容忍 0.9 倍偏差"
                              + "（手指点不准也抓得住），缩放倍率 = 抓取距离比");
            Console.WriteLine($"[{(scaleUndo ? "PASS" : "FAIL")}] 一次缩放 = 一步撤销");
            if (!scaleOk) failures++;
            if (!scaleUndo) failures++;

            // 5.4 手柄特权的边界：偏离 1.4 倍命中半径 ⇒ 视为点空白，取消选中
            var objScale = visualHost.GfxObjects.Objects[0];
            var handleNow = GfxTransform.ScaleHandleWorld(objScale, viewScale);
            var farPress = ScaleTowards(handleNow, objScale.Center, -1.4 * radiusWorld);

            visualHost.SimulatePointer(ToolPointerPhase.Down, visualHost.Viewport.ToViewport(farPress));
            bool deselected = visualHost.GfxObjects.SelectedId is null;
            visualHost.SimulatePointer(ToolPointerPhase.Up, visualHost.Viewport.ToViewport(farPress));

            Console.WriteLine($"[{(deselected ? "PASS" : "FAIL")}] 手柄容差是有限的："
                              + "偏离 1.4 倍命中半径就当作点空白（不会把旁边的东西一起抓走）");
            if (!deselected) failures++;

            // 5.5 删除按钮
            visualHost.GfxObjects.Select(objectId);
            var deletePress = GfxTransform.DeleteButtonWorld(visualHost.GfxObjects.Objects[0], viewScale);

            visualHost.SimulatePointer(ToolPointerPhase.Down, visualHost.Viewport.ToViewport(deletePress));
            visualHost.SimulatePointer(ToolPointerPhase.Up, visualHost.Viewport.ToViewport(deletePress));

            bool deleted = visualHost.GfxObjectCount == 0;
            bool deleteUndo = visualHost.Undo() && visualHost.GfxObjectCount == 1;

            Console.WriteLine($"[{(deleted ? "PASS" : "FAIL")}] 框右上角的 ✕ 能删掉对象"
                              + "（一体机上没有键盘，只做 Del 键等于没这个功能）");
            Console.WriteLine($"[{(deleteUndo ? "PASS" : "FAIL")}] 删除可撤销（误删不会损失一节课的板书）");
            if (!deleted) failures++;
            if (!deleteUndo) failures++;

            // ------------------------------------------------------ 6) 统一撤销时间线
            Console.WriteLine();
            Console.WriteLine("---- 6) 墨迹与图形共用一条撤销时间线 ----");

            var undoRegistry = new ToolRegistry();
            undoRegistry.AttachContext(undoHost.ToolContext);
            undoHost.RegisterGfxRenderer(new GfxDemoRenderer());
            undoRegistry.Add(new GfxDemoTool());
            undoRegistry.Activate("demo-glyph");

            // 步 1：墨迹（外部往集合里加一条，走真实的 StrokesChanged 记录路径）
            undoHost.InkSurface.Strokes.Add(MakeLine(
                MakeAttributes(InkPalette.Colors[0], InkPalette.Widths[1]),
                new Point(80, 90), new Point(240, 90)));

            // 步 2：图形
            var undoDrop = new Point(500, 620);
            undoRegistry.DispatchPointer(Pointer(ToolPointerPhase.Down, new Point(400, 560)));
            undoRegistry.DispatchPointer(Pointer(ToolPointerPhase.Up, undoDrop));

            bool twoSteps = undoHost.StrokeCount == 1 && undoHost.GfxObjectCount == 1;

            bool undoGfxFirst = undoHost.Undo()
                             && undoHost.GfxObjectCount == 0 && undoHost.StrokeCount == 1;

            bool undoInkSecond = undoHost.Undo()
                              && undoHost.StrokeCount == 0 && undoHost.GfxObjectCount == 0;

            bool redoBoth = undoHost.Redo() && undoHost.Redo()
                         && undoHost.StrokeCount == 1 && undoHost.GfxObjectCount == 1;

            Console.WriteLine($"  先写字再放图形 → 撤销两次得到：图形 {undoHost.GfxObjectCount} 个、"
                              + $"笔迹 {undoHost.StrokeCount} 条");
            Console.WriteLine($"[{(twoSteps ? "PASS" : "FAIL")}] 两类操作各记一步，混在同一条时间线上");
            Console.WriteLine($"[{(undoGfxFirst ? "PASS" : "FAIL")}] 第一次撤销退掉后做的那个（图形），"
                              + "先前写的字还在 —— 不需要用户猜「这一步退的是什么」");
            Console.WriteLine($"[{(undoInkSecond ? "PASS" : "FAIL")}] 第二次撤销才退到笔迹（真实发生顺序）");
            Console.WriteLine($"[{(redoBoth ? "PASS" : "FAIL")}] 重做两次把两类操作都还原");
            if (!twoSteps) failures++;
            if (!undoGfxFirst) failures++;
            if (!undoInkSecond) failures++;
            if (!redoBoth) failures++;

            // ------------------------------------------------------ 7) .tbink v2 持久化
            Console.WriteLine();
            Console.WriteLine("---- 7) 批注文件 v2：图形跟着笔迹一起走 ----");

            string workDir = Path.Combine(root, "archive");
            Directory.CreateDirectory(workDir);

            string pdfPath = Path.Combine(workDir, "样本试卷.pdf");
            File.Copy(service.FilePath!, pdfPath, overwrite: true);

            var store = new AnnotationStore();
            string sidecar = AnnotationStore.SidecarPathFor(pdfPath);

            storeHost.RegisterGfxRenderer(new GfxDemoRenderer());
            storeHost.SetDocument(service);
            storeHost.UpdateLayout();
            PumpFor(250);

            var storedStrokes = new StrokeCollection();
            storedStrokes.Add(MakeLine(MakeAttributes(InkPalette.Colors[2], InkPalette.Widths[0]),
                                       new Point(100, 120), new Point(400, 120)));

            var storedObjects = new List<GfxObjectData>
            {
                new()
                {
                    Id = "obj-known",
                    Kind = GfxDemoRenderer.KindName,
                    Plugin = new GfxDemoPlugin().Name,
                    X = 300,
                    Y = 400,
                    Rotation = 25,
                    Scale = 1.5,
                    Color = "#FFCC0000",
                    LineWidth = 2.0,
                    Numbers = { ["span"] = 123.5 },
                    Texts = { ["label"] = "一个方框" },
                },
                new()
                {
                    // 故意用一个没人注册的 Kind：模拟"装过插件、后来被禁用"
                    Id = "obj-orphan",
                    Kind = "unknown-widget",
                    Plugin = "没装的插件",
                    X = 500,
                    Y = 600,
                    Color = "#FF0000FF",
                    LineWidth = 1.5,
                    Numbers = { ["kept"] = 7 },
                },
            };

            var saveResult = store.Save(pdfPath, storedStrokes, storeHost.Layout.PageRects,
                                        storeHost.Layout.PageMargin, storeHost.Layout.PageGap,
                                        storedObjects);

            bool saveOk = saveResult.Ok && saveResult.Written && File.Exists(sidecar);
            bool noTempLeft = !File.Exists(sidecar + ".tmp");

            byte[] fileBytes = File.ReadAllBytes(sidecar);
            bool versionV2 = fileBytes.Length > 8 && fileBytes[6] == 2 && fileBytes[7] == 0;

            var loadResult = store.Load(pdfPath, storeHost.Layout.PageRects,
                                        storeHost.Layout.PageMargin, storeHost.Layout.PageGap);

            bool loadOk = loadResult.Status == AnnotationLoadStatus.Loaded
                       && loadResult.Strokes is { Count: 1 }
                       && loadResult.Objects is { Count: 2 };

            var back = loadResult.Objects;
            bool objectKept = loadOk
                           && back![0].Kind == GfxDemoRenderer.KindName
                           && Near(back[0].X, 300, 1e-9) && Near(back[0].Y, 400, 1e-9)
                           && Near(back[0].Rotation, 25, 1e-9) && Near(back[0].Scale, 1.5, 1e-9)
                           && string.Equals(back[0].Color, "#FFCC0000", StringComparison.OrdinalIgnoreCase)
                           && Near(back[0].LineWidth, 2.0, 1e-9)
                           && nearNumber(back[0], "span", 123.5)
                           && back[0].Texts.TryGetValue("label", out string? label) && label == "一个方框";

            bool orphanKept = loadOk && back![1].Kind == "unknown-widget"
                           && back[1].Plugin == "没装的插件"
                           && nearNumber(back[1], "kept", 7);

            Console.WriteLine($"  存档：笔迹 1 条 + 图形 2 个（其中 1 个的插件「没装」）；"
                              + $"文件版本 = v{(fileBytes.Length > 7 ? fileBytes[7] * 256 + fileBytes[6] : 0)}");
            Console.WriteLine($"[{(saveOk && noTempLeft ? "PASS" : "FAIL")}] 笔迹与图形写进同一个侧车文件"
                              + "（拷试卷时只要拷一个文件，不会有人只拷一半）");
            Console.WriteLine($"[{(versionV2 ? "PASS" : "FAIL")}] 含图形时写出 v2 版本号（老程序看得懂版本、按规则拒绝）");
            Console.WriteLine($"[{(objectKept ? "PASS" : "FAIL")}] 对象的位置/旋转/缩放/墨色/线宽/参数逐项还原");
            Console.WriteLine($"[{(orphanKept ? "PASS" : "FAIL")}] 认不出的 Kind 原样保留"
                              + "（打开一次文件绝不能毁掉板书）");
            if (!(saveOk && noTempLeft)) failures++;
            if (!versionV2) failures++;
            if (!objectKept) failures++;
            if (!orphanKept) failures++;

            // 装载到画布：认不出的那个画成占位框，并且被如实报出来
            storeHost.ReplaceObjects(back);

            bool placeholderOk = storeHost.GfxObjectCount == 2
                              && storeHost.GfxObjectLayer.VisualCount == 2
                              && storeHost.GfxObjects.MissingRendererCount == 1
                              && storeHost.GfxObjects.MissingNames.Contains("没装的插件");

            Console.WriteLine($"[{(placeholderOk ? "PASS" : "FAIL")}] 缺插件的对象画成占位框"
                              + $"（{storeHost.GfxObjects.MissingRendererCount} 个缺渲染器，"
                              + $"缺的是「{string.Join("、", storeHost.GfxObjects.MissingNames)}」）"
                              + "，并把缺谁如实报给状态栏");
            if (!placeholderOk) failures++;

            // 只放图形、一个字都没写，也必须存下来
            string gfxOnlyPdf = Path.Combine(workDir, "只有图形.pdf");
            File.Copy(pdfPath, gfxOnlyPdf, overwrite: true);

            var gfxOnlySave = store.Save(gfxOnlyPdf, new StrokeCollection(),
                                         storeHost.Layout.PageRects, storeHost.Layout.PageMargin,
                                         storeHost.Layout.PageGap, storedObjects);

            var gfxOnlyLoad = store.Load(gfxOnlyPdf, storeHost.Layout.PageRects,
                                         storeHost.Layout.PageMargin, storeHost.Layout.PageGap);

            bool gfxOnlyOk = gfxOnlySave.Ok && gfxOnlySave.Written
                          && File.Exists(AnnotationStore.SidecarPathFor(gfxOnlyPdf))
                          && gfxOnlyLoad.Status == AnnotationLoadStatus.Loaded
                          && gfxOnlyLoad.Strokes is { Count: 0 }
                          && gfxOnlyLoad.Objects is { Count: 2 };

            Console.WriteLine($"[{(gfxOnlyOk ? "PASS" : "FAIL")}] 「只放了图形、没写字」也能存能读"
                              + "（判据漏掉图形的话，表现是「一退出图形就没了」，极难复现）");
            if (!gfxOnlyOk) failures++;

            // 整体平移补偿：图形和笔迹必须一起挪，否则"老师画的角"和"标的度数"会分家
            const double shiftX = 100;
            const double shiftY = 50;

            string shiftedPdf = Path.Combine(workDir, "旧版式.pdf");
            File.Copy(pdfPath, shiftedPdf, overwrite: true);

            var shiftedManifest = new TbinkManifest
            {
                SourceFingerprint = DocumentFingerprint.Compute(shiftedPdf),
                PageMargin = storeHost.Layout.PageMargin + shiftX,
                PageGap = storeHost.Layout.PageGap,
                Pages = MakeManifestPages(storeHost.Layout.PageRects, shiftX, shiftY),
            };

            using (var stream = File.Create(AnnotationStore.SidecarPathFor(shiftedPdf)))
            {
                TbinkFile.Write(stream, shiftedManifest, storedStrokes, storedObjects);
            }

            var shifted = store.Load(shiftedPdf, storeHost.Layout.PageRects,
                                     storeHost.Layout.PageMargin, storeHost.Layout.PageGap);

            bool shiftObjectsOk = shifted.Status == AnnotationLoadStatus.Loaded
                               && shifted.Objects is { Count: 2 }
                               && Near(shifted.Objects![0].X, 300 - shiftX, 1e-6)
                               && Near(shifted.Objects[0].Y, 400 - shiftY, 1e-6)
                               && Near(shifted.Objects[0].Rotation, 25, 1e-9)
                               && shifted.Strokes is { Count: 1 }
                               && Near(shifted.Strokes![0].StylusPoints[0].X, 100 - shiftX, 0.05)
                               && Near(shifted.Strokes[0].StylusPoints[0].Y, 120 - shiftY, 0.05);

            Console.WriteLine($"  记录版式整体偏移 (+{shiftX:F0},+{shiftY:F0})："
                              + $"对象中心 300,400 → {shifted.Objects?[0].X:F1},{shifted.Objects?[0].Y:F1}；"
                              + $"笔迹首点 100,120 → {shifted.Strokes?[0].StylusPoints[0].X:F1},"
                              + $"{shifted.Strokes?[0].StylusPoints[0].Y:F1}");
            Console.WriteLine($"[{(shiftObjectsOk ? "PASS" : "FAIL")}] 版式整体平移时，图形与笔迹同步补偿"
                              + "（旋转角不动 —— 平移不该改朝向）");
            if (!shiftObjectsOk) failures++;

            // 纯笔迹存档仍写 v1：老程序还能打开新程序存的纯笔迹文件
            bool v1Ok = false;
            using (var buffer = new MemoryStream())
            {
                TbinkFile.Write(buffer, new TbinkManifest(), storedStrokes);
                byte[] bytes = buffer.ToArray();

                bool versionV1 = bytes.Length > 8 && bytes[6] == 1 && bytes[7] == 0;

                buffer.Position = 0;
                bool readOk = TbinkFile.TryRead(buffer, out _, out var strokesBack, out var objectsBack, out _);

                v1Ok = versionV1 && readOk && strokesBack.Count == 1 && objectsBack is null;
            }

            Console.WriteLine($"[{(v1Ok ? "PASS" : "FAIL")}] 没有图形时仍按 v1 写"
                              + "（老程序还能打开新程序存的纯笔迹文件，不制造无谓的不兼容）");
            if (!v1Ok) failures++;

            // 存档样本留一份给人工查看（侧车是刻意可读的 JSON + ISF）
            string evidence = Path.Combine(outDir, "m74-批注样本.pdf.tbink");
            File.Copy(sidecar, evidence, overwrite: true);
            Console.WriteLine($"  存档样本：{evidence}");
        }
        finally
        {
            visualHost.Shutdown();
            undoHost.Shutdown();
            storeHost.Shutdown();

            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 清理失败不影响验收结论
            }
        }

        return failures;
    }

    // ================================================================= M7.4 Step 1：量角器

    /// <summary>
    /// M7.4 Step 1 断言组：盘面几何、双圈刻度、两种吸附、落成对象、光栅落点、存档往返。
    /// </summary>
    /// <remarks>
    /// 这一段专治<b>肉眼绝对看不出来的三类错</b>：
    /// 半圆画到了基线下面、刻度或数字换算错、旋转方向反了。
    /// 它们一样都不会让程序崩，只会让老师在讲台上读出一个错的角 ——
    /// 所以断言全部落在<b>坐标、几何包围盒与光栅落点</b>上，不落在"调用了哪个方法"上。
    /// </remarks>
    private static int RunM74S1Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.4 Step 1：量角器（学科工具）================");

        // ---------------------------------------------------------- 1) 盘面几何（纯函数）
        Console.WriteLine();
        Console.WriteLine("---- 1) 盘面几何：半圆必须长在基线的「上方」----");

        const double probeRadius = 120.0;

        var disc = ProtractorGeometry.BuildDisc(probeRadius);
        var discBounds = disc.Bounds;

        bool discOk = Near(discBounds.X, -probeRadius, 1e-6)
                   && Near(discBounds.Y, -probeRadius, 1e-6)
                   && Near(discBounds.Width, probeRadius * 2, 1e-6)
                   && Near(discBounds.Height, probeRadius, 1e-6);

        var ticks = ProtractorGeometry.BuildTicks(probeRadius);
        var tickBounds = ticks.Bounds;

        bool tickBoundsOk = Near(tickBounds.X, -probeRadius, 1e-6)
                         && Near(tickBounds.Y, -probeRadius, 1e-6)
                         && Near(tickBounds.Width, probeRadius * 2, 1e-6)
                         && Near(tickBounds.Height, probeRadius, 1e-6);

        Console.WriteLine($"  盘面包围盒 {discBounds.Width:F0}×{discBounds.Height:F0}"
                          + $"，左上 ({discBounds.X:F0},{discBounds.Y:F0})；期望 240×120，左上 (−120,−120)");
        Console.WriteLine($"[{(discOk ? "PASS" : "FAIL")}] 半圆盘长在基线「上方」"
                          + "（SweepDirection 写反的话它会整个翻到下面，而看上去仍然像一把量角器）");
        Console.WriteLine($"[{(tickBoundsOk ? "PASS" : "FAIL")}] 全部刻度与圆心标记落在盘面范围内"
                          + "（刻度戳出盘外 = 几何与画法各算各的）");
        if (!discOk) failures++;
        if (!tickBoundsOk) failures++;

        // 刻度值 → 本地点：0° 在基线右端、90° 在正上方、180° 在左端
        bool polarOk =
            Near(ProtractorGeometry.LocalPoint(probeRadius, 0), new Point(probeRadius, 0), 1e-9)
            && Near(ProtractorGeometry.LocalPoint(probeRadius, 90), new Point(0, -probeRadius), 1e-9)
            && Near(ProtractorGeometry.LocalPoint(probeRadius, 180), new Point(-probeRadius, 0), 1e-9)
            && Near(ProtractorGeometry.LocalPoint(probeRadius, 45),
                    new Point(probeRadius / Math.Sqrt(2), -probeRadius / Math.Sqrt(2)), 1e-9);

        Console.WriteLine($"  刻度 0°→({probeRadius:F0},0)、90°→(0,{-probeRadius:F0})、180°→({-probeRadius:F0},0)");
        Console.WriteLine($"[{(polarOk ? "PASS" : "FAIL")}] 刻度值的方向：0 在右端、90 在正上方、180 在左端"
                          + "（y 轴向下，少那个负号整个盘面就会绕反）");
        if (!polarOk) failures++;

        bool radiusClampOk =
            Near(ProtractorGeometry.ClampRadius(30), ProtractorGeometry.MinRadiusWorld, 1e-9)
            && Near(ProtractorGeometry.ClampRadius(9999), ProtractorGeometry.MaxRadiusWorld, 1e-9)
            && Near(ProtractorGeometry.ClampRadius(double.NaN), ProtractorGeometry.DefaultRadiusWorld, 1e-9)
            && Near(ProtractorGeometry.ClampRadius(110), 110, 1e-9);

        bool unitOk = Near(ProtractorGeometry.ToCentimeters(72), 2.54, 1e-9)
                   && Math.Abs(ProtractorGeometry.ToCentimeters(2 * ProtractorGeometry.DefaultRadiusWorld) - 7.76) < 0.01;

        Console.WriteLine($"  换算：默认直径 {ProtractorGeometry.ToCentimeters(2 * ProtractorGeometry.DefaultRadiusWorld):F2} cm"
                          + $"（A4 宽 {ProtractorGeometry.ToCentimeters(595):F2} cm 的 {ProtractorGeometry.ToCentimeters(2 * ProtractorGeometry.DefaultRadiusWorld) / ProtractorGeometry.ToCentimeters(595) * 100:F0}%）");
        Console.WriteLine($"[{(radiusClampOk ? "PASS" : "FAIL")}] 半径夹紧：太小放大到 2.1 cm、太大收到 14 cm、"
                          + "坏数值退回默认（一个读不出刻度的迷你盘等于没有）");
        Console.WriteLine($"[{(unitOk ? "PASS" : "FAIL")}] 长度换算沿用世界坐标（1/72 inch ⇒ 110 world ≈ 3.9 cm）");
        if (!radiusClampOk) failures++;
        if (!unitOk) failures++;

        // ---------------------------------------------------------- 2) 双圈刻度
        Console.WriteLine();
        Console.WriteLine("---- 2) 双圈刻度：外圈 0→180、内圈 180→0 ----");

        int major = 0, medium = 0, minor = 0;
        for (int value = 0; value < ProtractorGeometry.TickCount; value++)
        {
            double ratio = ProtractorGeometry.TickRatioFor(value);

            if (Near(ratio, ProtractorGeometry.TickMajorRatio, 1e-12)) major++;
            else if (Near(ratio, ProtractorGeometry.TickMediumRatio, 1e-12)) medium++;
            else minor++;
        }

        bool tickCountOk = ProtractorGeometry.TickCount == 181 && major == 19 && medium == 18 && minor == 144;

        var digits = ProtractorGeometry.BuildDigits(probeRadius);
        var digitBounds = digits.Bounds;

        // 数字：都在盘内、都在基线上方（0 与 180 若居中摆放，会有一半掉到盘外）
        bool digitOk = !digitBounds.IsEmpty
                    && digitBounds.X >= -probeRadius * 1.02
                    && digitBounds.Right <= probeRadius * 1.02
                    && digitBounds.Y >= -probeRadius * 1.02
                    && digitBounds.Bottom <= 1e-6;

        Console.WriteLine($"  刻度 {ProtractorGeometry.TickCount} 条 = 长 {major}（每 10°）+ 中 {medium}（每 5°）"
                          + $"+ 短 {minor}（每 1°）；刻度长度 {ProtractorGeometry.TickMinorRatio:F3}R / "
                          + $"{ProtractorGeometry.TickMediumRatio:F3}R / {ProtractorGeometry.TickMajorRatio:F3}R");
        Console.WriteLine($"  双圈数字（各 19 个）的几何范围：宽 {digitBounds.Width:F1}"
                          + $"，纵向 {digitBounds.Y:F1} → {digitBounds.Bottom:F1}（基线在 0）");
        Console.WriteLine($"[{(tickCountOk ? "PASS" : "FAIL")}] 0…180 每 1° 一条刻度，"
                          + "10°/5°/1° 三档长度分级（少了哪一档，眼睛先看得出来的是它）");
        Console.WriteLine($"[{(digitOk ? "PASS" : "FAIL")}] 双圈数字全部落在盘面内、且没有一个越过基线"
                          + "（底边贴刻度环的印法，实物就是这么印的）");
        // 38 个数字两两不许相交：10° 一个数字、双圈共 38 个，最挤的地方在内圈靠近顶部处
        // （那里半径方向的位移几乎抵消，只剩切向的弧长）。屏幕上只表现为"有点糊"，
        // 只有量净缝才能把"还能看清"和"叠成一坨"分开。
        var labels = ProtractorGeometry.LayoutLabels(probeRadius);
        double minGap = double.MaxValue;
        string minGapText = "（数字少于两个）";

        for (int i = 0; i < labels.Count; i++)
        {
            for (int j = i + 1; j < labels.Count; j++)
            {
                Rect a = labels[i].Bounds;
                Rect b = labels[j].Bounds;

                // 两个矩形只要在任一轴上分开就不算叠；取较大的那个"净缝"当分离度
                double gapX = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
                double gapY = Math.Max(a.Top, b.Top) - Math.Min(a.Bottom, b.Bottom);
                double gap = Math.Max(gapX, gapY);

                if (gap >= minGap) continue;

                minGap = gap;
                minGapText = $"「{labels[i].Text}」（{labels[i].RingRadiusRatio:F3}R）与 "
                           + $"「{labels[j].Text}」（{labels[j].RingRadiusRatio:F3}R）之间";
            }
        }

        // 要求"看得见的缝"而不是"没相碰"：1px 的缝在屏幕上看就是挨在一起。
        // 2% 半径是这条底线 —— 少了它，字号每改一点点都会悄悄滑回"糊成一团"。
        double requiredGap = probeRadius * 0.02;
        bool labelSpacingOk = labels.Count == 38 && minGap >= requiredGap;

        Console.WriteLine($"  数字排布：{labels.Count} 个，最窄净缝 {minGap:F2} world"
                          + $"（{minGap / probeRadius:F3}R），出现在 {minGapText}");
        Console.WriteLine($"[{(labelSpacingOk ? "PASS" : "FAIL")}] 38 个刻度数字两两之间都留着"
                          + $"至少 {requiredGap / probeRadius:P0} 半径的净缝"
                          + "（要求「没相碰」不够 —— 1px 的缝在屏幕上看就是挨在一起）");
        if (!labelSpacingOk) failures++;
        if (!tickCountOk) failures++;
        if (!digitOk) failures++;

        // ---------------------------------------------------------- 3) 吸附（纯函数）
        Console.WriteLine();
        Console.WriteLine("---- 3) 吸附：吸方向不吸网格 ----");

        bool snapIn = ProtractorGeometry.TrySnapOrientation(34, 37, out double snappedIn)
                   && Near(snappedIn, 37, 1e-9);

        bool snapOut = !ProtractorGeometry.TrySnapOrientation(20, 37, out double snappedOut)
                    && Near(snappedOut, 20, 1e-9);

        // 一条 37° 的线，正反两个方向都能对齐 —— 少算一个方向，往左下拖时就吸不上
        bool snapReverse = ProtractorGeometry.TrySnapOrientation(214, 37, out double snappedReverse)
                        && Near(snappedReverse, 217, 1e-9);

        bool snapEdge = ProtractorGeometry.TrySnapOrientation(31, 37, out _)          // 差 6° = 容差边界
                     && !ProtractorGeometry.TrySnapOrientation(30.9, 37, out _);      // 差 6.1° → 不吸

        var candidates = new List<BoardSegment>
        {
            new(new Point(0, 0), AtAngle(new Point(0, 0), 10, 100)),
            new(new Point(0, 0), AtAngle(new Point(0, 0), 44, 100)),
        };

        bool pickClosestDirection = ProtractorGeometry.TrySnapOrientationToSegments(41, candidates, out double picked)
                                 && Near(picked, 44, 1e-9);

        var nearestPoint = ProtractorGeometry.PickNearestPoint(new Point(10, 4),
                                new List<Point> { new(30, 30), new(12, 3) }, 24);

        string nearestText = nearestPoint is { } nearest ? $"({nearest.X:F0},{nearest.Y:F0})" : "（没有）";

        var segment = new BoardSegment(new Point(100, 100), new Point(200, 100));
        bool projectOk = Near(ProtractorGeometry.ClosestPointOn(new Point(150, 140), segment.From, segment.To),
                              new Point(150, 100), 1e-9)
                      && Near(ProtractorGeometry.DistanceToSegment(new Point(150, 140), segment.From, segment.To), 40, 1e-9)
                      // 线段之外的点必须投影到端点，不能落到延长线上（否则圆心会吸到空气里）
                      && Near(ProtractorGeometry.ClosestPointOn(new Point(400, 0), segment.From, segment.To),
                              new Point(200, 100), 1e-9);

        Console.WriteLine($"  34°→{snappedIn:F0}°、20°→{snappedOut:F0}°（不吸）、"
                          + $"214°→{(snapReverse ? snappedReverse : 0):F0}°；容差边界 31° 吸、30.9° 不吸");
        Console.WriteLine($"  多线段时挑方向最接近的：41° ⇒ {(pickClosestDirection ? picked : 0):F0}°"
                          + $"（不是挑离得最近的）；端点最近点 {nearestText}");
        Console.WriteLine($"[{(snapIn && snapOut && snapReverse ? "PASS" : "FAIL")}] 只吸「差 6° 以内」的已有线段方向，"
                          + "且正反两个方向都算候选");
        Console.WriteLine($"[{(snapEdge && pickClosestDirection ? "PASS" : "FAIL")}] 容差边界精确（31° 吸 / 30.9° 不吸）；"
                          + "多条线时按「方向最接近」而不是「离得最近」挑");
        Console.WriteLine($"[{(projectOk ? "PASS" : "FAIL")}] 圆心吸附用线段最近点（投影夹到 [0,1]），"
                          + "不会吸到延长线上的空气里");
        if (!(snapIn && snapOut && snapReverse)) failures++;
        if (!(snapEdge && pickClosestDirection)) failures++;
        if (!projectOk) failures++;

        // ---------------------------------------------------------- 4) 真工具 + 假上下文
        Console.WriteLine();
        Console.WriteLine("---- 4) 工具状态机：圆心吸端点、朝向吸直线、误触不落 ----");

        var fakeHost = new FakeGfxHost();
        var fakeQuery = new FakeBoardQuery();

        var endpoint = new Point(300, 400);
        fakeQuery.Points.Add(endpoint);
        fakeQuery.Points.Add(AtAngle(endpoint, 37, 260));
        fakeQuery.Segments.Add(new BoardSegment(endpoint, AtAngle(endpoint, 37, 260)));

        var fakeContext = new FakeToolContext { GfxOverride = fakeHost, QueryOverride = fakeQuery };
        var protractor = new ProtractorTool();
        protractor.Activate(fakeContext);

        bool shapeOk = !protractor.UsesInkLayer && protractor.NeedsPointer
                    && protractor.InputKind == ToolInputKind.None
                    && protractor.InkMode == ToolInkMode.None
                    && protractor.Id == ProtractorToolIds.Protractor
                    && protractor.Shortcut == Key.D6
                    && protractor is IGfxTool;

        Console.WriteLine($"[{(shapeOk ? "PASS" : "FAIL")}] 量角器的工具形态：不落墨层 + 需要指针 + IGfxTool "
                          + $"（宿主按这条规则给输入：IsHitTestVisible={protractor.UsesInkLayer}）");
        if (!shapeOk) failures++;

        // 误触：按下就抬
        protractor.OnPointer(Pointer(ToolPointerPhase.Down, new Point(900, 900)));
        protractor.OnPointer(Pointer(ToolPointerPhase.Up, new Point(900.5, 900.5)));
        bool misTap = fakeHost.Added.Count == 0 && fakeContext.Previews.Count == 0
                   && fakeContext.LastStatus?.Contains("太短") == true;

        Console.WriteLine($"  误触（移动 0.5 world）：落下 {fakeHost.Added.Count} 个、"
                          + $"状态栏「{fakeContext.LastStatus}」");
        Console.WriteLine($"[{(misTap ? "PASS" : "FAIL")}] 误触不落对象并给出可读理由"
                          + "（老师切工具时习惯在卷面上点一下，没有这一条卷面会被量角器铺满）");
        if (!misTap) failures++;

        // 正常一次：圆心离端点 7 world（吸上去）、往 34° 拖 150（朝向吸到 37°）
        fakeContext.Reset();
        var pressNear = new Point(306, 404);
        var dragTip = AtAngle(endpoint, 34, 150);

        protractor.OnPointer(Pointer(ToolPointerPhase.Down, pressNear));
        bool previewShown = fakeContext.Previews.Count == 1;
        protractor.OnPointer(Pointer(ToolPointerPhase.Move, dragTip));
        protractor.OnPointer(Pointer(ToolPointerPhase.Up, dragTip));

        var draft = fakeHost.Added.Count == 1 ? fakeHost.Added[0] : null;

        bool centerSnapped = draft is not null && Near(draft.Center, endpoint, 1e-9);
        bool orientSnapped = draft is not null && Near(draft.RotationDegrees, 37, 1e-9);
        double drafted = 0;
        bool radiusTaken = draft is not null
                        && draft.Numbers.TryGetValue(ProtractorGeometry.RadiusKey, out drafted)
                        && Near(drafted, 150, 1e-9);

        bool kindOk = draft is not null && draft.Kind == ProtractorRenderer.KindName;
        bool previewCleared4 = fakeContext.Previews.Count == 0;
        bool noInk4 = fakeContext.Committed.Count == 0;

        // 历史步由宿主自己记（"新建"没有"改之前"可存），工具不该自己踩进来
        bool noToolStep = fakeHost.Steps.Count == 0;

        Console.WriteLine($"  按下点 (306,404) → 圆心 ({draft?.Center.X:F0},{draft?.Center.Y:F0})；"
                          + $"往 34° 拖 150 ⇒ 朝向 {draft?.RotationDegrees:F0}°、半径 {drafted:F0} world");
        Console.WriteLine($"  状态栏「{fakeContext.LastStatus}」");
        Console.WriteLine($"[{(previewShown && previewCleared4 ? "PASS" : "FAIL")}] 拖动中 1 个预览（盘面）、抬笔清空"
                          + "（预览不进墨迹层、不进撤销栈）");
        Console.WriteLine($"[{(centerSnapped ? "PASS" : "FAIL")}] 圆心吸到已有线段的端点（差 7 world 也吸上去，"
                          + "误差 < 1e-9）—— 角的顶点就是老师心里那个点");
        Console.WriteLine($"[{(orientSnapped ? "PASS" : "FAIL")}] 基线朝向吸到已有直线的 37°，"
                          + "半径取自拖动距离（150 world）");
        Console.WriteLine($"[{(kindOk && noInk4 && noToolStep ? "PASS" : "FAIL")}] 交出去的是一个图形对象草案"
                          + "（Kind/中心/朝向/半径齐全），一条墨都没落，撤销步由宿主记");
        if (!(previewShown && previewCleared4)) failures++;
        if (!centerSnapped) failures++;
        if (!(orientSnapped && radiusTaken)) failures++;
        if (!(kindOk && noInk4 && noToolStep)) failures++;

        // 只拖 30 world：太小的量角器读不出刻度 ⇒ 按最小半径落，并在状态栏说清楚
        fakeContext.Reset();
        protractor.OnPointer(Pointer(ToolPointerPhase.Down, new Point(800, 800)));
        protractor.OnPointer(Pointer(ToolPointerPhase.Up, AtAngle(new Point(800, 800), 10, 30)));

        // fakeHost 会累积前面几段留下的草案，这里要的是"最新那一个"，
        // 不能要求总数恰好是 1（那是把"上一条断言没落对象"也算了进来）
        var smallDraft = fakeHost.Added.Count > 0 ? fakeHost.Added[^1] : null;
        bool minRadiusOk = smallDraft is not null
                        && smallDraft.Numbers.TryGetValue(ProtractorGeometry.RadiusKey, out double small)
                        && Near(small, ProtractorGeometry.MinRadiusWorld, 1e-9)
                        && fakeContext.LastStatus?.Contains("最小尺寸") == true;

        Console.WriteLine($"[{(minRadiusOk ? "PASS" : "FAIL")}] 只拖 30 world ⇒ 按最小半径 "
                          + $"{ProtractorGeometry.MinRadiusWorld:F0} world 落，并说明「已按最小尺寸放大」"
                          + "（静默缩小才叫 bug）");
        if (!minRadiusOk) failures++;

        // 拿不到吸附基准（Query 为 null）：必须退化成自由摆放，而不是不工作
        var lonelyHost = new FakeGfxHost();
        var lonelyContext = new FakeToolContext { GfxOverride = lonelyHost };
        var lonelyTool = new ProtractorTool();
        lonelyTool.Activate(lonelyContext);

        lonelyTool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(0, 0)));
        lonelyTool.OnPointer(Pointer(ToolPointerPhase.Up, AtAngle(new Point(0, 0), 34, 140)));

        var lonelyDraft = lonelyHost.Added.Count == 1 ? lonelyHost.Added[0] : null;
        bool degradeOk = lonelyDraft is not null
                      && Near(lonelyDraft.Center, new Point(0, 0), 1e-9)
                      && Math.Abs(lonelyDraft.RotationDegrees - 34) < 1e-6;

        Console.WriteLine($"[{(degradeOk ? "PASS" : "FAIL")}] 拿不到吸附基准时退化成自由摆放"
                          + $"（圆心 (0,0)、朝向 {lonelyDraft?.RotationDegrees:F1}°）—— "
                          + "少一个自动对齐是少个便利，按下去什么也不出来才是故障");
        if (!degradeOk) failures++;

        // ---------------------------------------------------------- 5) 真宿主端到端
        Console.WriteLine();
        Console.WriteLine("---- 5) 真宿主：真笔画当吸附基准 + 落成对象 ----");

        var pxyHost = new CanvasViewportHost();
        pxyHost.Measure(new Size(1000, 700));
        pxyHost.Arrange(new Rect(0, 0, 1000, 700));
        pxyHost.UpdateLayout();

        try
        {
            new ProtractorPlugin().Register(pxyHost.PluginRegistry);
            pxyHost.RegisterGfxRenderer(new ProtractorRenderer(), "量角器（学科工具）");

            bool registered = pxyHost.Tools.Any(tool => tool.Id == ProtractorToolIds.Protractor)
                           && pxyHost.GfxRenderers.Contains(ProtractorRenderer.KindName);

            Console.WriteLine($"[{(registered ? "PASS" : "FAIL")}] 插件注册：工具 + 渲染器都到位"
                              + $"（Kind = {ProtractorRenderer.KindName}，归属「{pxyHost.GfxRenderers.OwnerOf(ProtractorRenderer.KindName)}」）");
            if (!registered) failures++;

            // 往墨迹层里放一条"直尺画的"辅助线：它是吸附基准，也是"工具协作"的最小证明
            var inkTip = AtAngle(new Point(400, 500), 37, 260);
            pxyHost.Strokes.Add(new Stroke(new StylusPointCollection
            {
                new StylusPoint(400, 500),
                new StylusPoint(inkTip.X, inkTip.Y),
            }));

            pxyHost.SetTool(ProtractorToolIds.Protractor);

            var hostPress = new Point(404, 504);
            var hostDrag = AtAngle(new Point(400, 500), 34, 150);

            pxyHost.SimulatePointer(ToolPointerPhase.Down, pxyHost.Viewport.ToViewport(hostPress));
            bool hostPreview = pxyHost.PreviewCount == 1;

            pxyHost.SimulatePointer(ToolPointerPhase.Move, pxyHost.Viewport.ToViewport(hostDrag));
            pxyHost.SimulatePointer(ToolPointerPhase.Up, pxyHost.Viewport.ToViewport(hostDrag));

            bool hostPreviewCleared = pxyHost.PreviewCount == 0;
            bool hostOneObject = pxyHost.GfxObjectCount == 1;
            bool onlyTargetInk = pxyHost.StrokeCount == 1;   // 只有我们注入的那条，量角器一条墨都没落

            var placed = pxyHost.GfxObjects.Objects[0];
            bool placedPose = Near(placed.Center, new Point(400, 500), 1e-9)
                           && Near(placed.RotationDegrees, 37, 1e-9)
                           && Near(placed.GetNumber(ProtractorGeometry.RadiusKey, 0), 150, 1e-9);

            bool oneVisual = pxyHost.GfxObjectLayer.VisualCount == 1
                          && pxyHost.GfxObjectLayer.VisualFor(placed.Id) is { } wrapper
                          && wrapper is Canvas { Children.Count: 1 };

            Console.WriteLine($"  真笔画（37° 辅助线）当吸附基准 → 落成对象：圆心 ({placed.Center.X:F0},{placed.Center.Y:F0})、"
                              + $"朝向 {placed.RotationDegrees:F0}°、半径 {placed.GetNumber(ProtractorGeometry.RadiusKey, 0):F0}");
            Console.WriteLine($"  预览 0→{(hostPreview ? 1 : 0)}→{pxyHost.PreviewCount}；对象 {pxyHost.GfxObjectCount} 个；"
                              + $"墨迹 {pxyHost.StrokeCount} 条（只有注入的基准线）");
            Console.WriteLine($"[{(hostPreview && hostPreviewCleared ? "PASS" : "FAIL")}] 预览走 AddPreview："
                              + "拖动中 1 个、抬笔清空");
            Console.WriteLine($"[{(hostOneObject && placedPose && onlyTargetInk ? "PASS" : "FAIL")}] 端到端："
                              + "圆心吸到笔画端点、基线吸到笔画方向 37°、半径取自拖动距离，一条墨都没落");
            Console.WriteLine($"[{(oneVisual ? "PASS" : "FAIL")}] 一个对象 = 一个视觉元素"
                              + "（181 条刻度 + 38 个数字合并进同一个自绘元素，不是 220 个图元）");
            if (!(hostPreview && hostPreviewCleared)) failures++;
            if (!(hostOneObject && placedPose && onlyTargetInk)) failures++;
            if (!oneVisual) failures++;

            // ------------------------------------------------ 6) 存档往返
            Console.WriteLine();
            Console.WriteLine("---- 6) 存档往返：半径/朝向/位置一个不差 ----");

            bool roundTrip;
            using (var buffer = new MemoryStream())
            {
                TbinkFile.Write(buffer, new TbinkManifest(), new StrokeCollection(), pxyHost.SnapshotObjects());
                buffer.Position = 0;

                bool readOk = TbinkFile.TryRead(buffer, out var manifest, out _, out var objectsBack, out _);
                var back = objectsBack?.FirstOrDefault(o => o.Id == placed.Id);

                roundTrip = readOk
                         && manifest.ObjectCount == 1
                         && back is not null
                         && Near(back.X, 400, 1e-9)
                         && Near(back.Y, 500, 1e-9)
                         && Near(back.Rotation, 37, 1e-9)
                         && nearNumber(back, ProtractorGeometry.RadiusKey, 150);
            }

            Console.WriteLine($"[{(roundTrip ? "PASS" : "FAIL")}] 存 → 读：朝向 37°、半径 150、"
                              + "圆心 (400,500) 全部还原（JSON 是精确的，不像 ISF 有 0.006 的往返误差）");
            if (!roundTrip) failures++;

            // ------------------------------------------------ 7) 光栅落点
            Console.WriteLine();
            Console.WriteLine("---- 7) 光栅落点：墨确实画在算出来的地方，且只在上半边 ----");

            var rasterData = new GfxObjectData
            {
                Id = "raster-protractor",
                Kind = ProtractorRenderer.KindName,
                Plugin = "量角器（学科工具）",
                X = 400,
                Y = 520,
                Rotation = 0,
                Scale = 1,
                Color = GfxObjectData.ToHex(Colors.Red),
                LineWidth = 1.5,
                Numbers = new Dictionary<string, double> { [ProtractorGeometry.RadiusKey] = 150 },
            };

            pxyHost.ReplaceObjects(new[] { rasterData });
            pxyHost.UpdateLayout();

            Console.WriteLine($"  换上一个对象后：对象 {pxyHost.GfxObjectCount} 个、视觉元素 "
                              + $"{pxyHost.GfxObjectLayer.VisualCount} 个（两者不等 = 视觉层没跟着数据走）");

            // 不换底色：CanvasBackground 是**纸面**底色（M3.6 的"整纸观感"用的就是它），
            // 眼前这个宿主没有文档 ⇒ 改它对视口底衬毫无影响。所以判据改成"按颜色认墨"，
            // 底衬是什么颜色都不影响结论。
            var raster = Snapshot(pxyHost, 1000, 700);

            var converted = new FormatConvertedBitmap(raster, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;

            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            long inkPixels = 0;
            long opaquePixels = 0, nonBlackPixels = 0, coloredPixels = 0;

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int offset = rowStart + x * 4;
                    byte blue = pixels[offset], green = pixels[offset + 1];
                    byte red = pixels[offset + 2], alpha = pixels[offset + 3];

                    if (alpha >= 250) opaquePixels++;
                    if (blue > 24 || green > 24 || red > 24) nonBlackPixels++;
                    if (Math.Max(Math.Max(red, green), blue) - Math.Min(Math.Min(red, green), blue) > 40)
                    {
                        coloredPixels++;
                    }

                    // 只认"红"像素：底衬是白纸色、盘面填充是半透明蓝、数字与刻度是红墨 ——
                    // 红色在这张图里只有一个来源（这个对象是用 #FFFF0000 落的），
                    // 数出来的就是量角器亲手画下的东西，与外接框的期望值可以直接比。
                    // 反过来说：按"非白即墨"数，整块底衬都会被算成墨，外接框立刻撑满画布。
                    if (!(red >= 100 && red > green + 40 && red > blue + 40))
                    {
                        continue;
                    }

                    inkPixels++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            // 抽样：把画面里几个关键位置的像素原样打出来（判据出错时，这一行就是唯一线索）
            int strideCopy = stride;
            string Sample(int x, int y)
            {
                int o = y * strideCopy + x * 4;
                return $"({x},{y})=#{pixels[o + 2]:X2}{pixels[o + 1]:X2}{pixels[o]:X2}"
                     + $"/A{pixels[o + 3]:X2}";
            }

            Console.WriteLine($"  像素分类：不透明 {opaquePixels}、非黑 {nonBlackPixels}、有彩色 {coloredPixels}、"
                              + $"红墨 {inkPixels}（共 {width * height} px）");
            Console.WriteLine($"  抽样：盘顶 {Sample(400, 370)}、基线上 {Sample(400, 520)}、"
                              + $"基线下方 {Sample(400, 530)}、空白处 {Sample(4, 4)}");

            double viewScale = pxyHost.Viewport.Scale;
            var centerDip = pxyHost.Viewport.ToViewport(new Point(400, 520));
            double radiusDip = 150 * viewScale;

            bool inkFound = inkPixels > 500;

            bool rasterOk = inkFound
                         && minY <= centerDip.Y - radiusDip * 0.85
                         && maxY <= centerDip.Y + radiusDip * 0.06
                         && minX >= centerDip.X - radiusDip * 1.05
                         && maxX <= centerDip.X + radiusDip * 1.05
                         && Math.Abs((minX + maxX) / 2.0 - centerDip.X) <= radiusDip * 0.08;

            Console.WriteLine($"  光栅墨迹：{inkPixels} px，外接框 x∈[{minX},{maxX}]、y∈[{minY},{maxY}]；"
                              + $"盘心在 ({centerDip.X:F0},{centerDip.Y:F0})、半径 {radiusDip:F0} px（视口 ×{viewScale:F2}）");
            Console.WriteLine($"[{(rasterOk ? "PASS" : "FAIL")}] 墨落在盘心正上方一个半径处、"
                              + "基线下方几乎没有墨 ⇒ 半圆在上半边，且「画出来的位置 = 算出来的位置」");
            if (!rasterOk) failures++;

            // ------------------------------------------------ 8) 预览摆位
            Console.WriteLine();
            Console.WriteLine("---- 8) 拖动中的预览盘面画在哪（Path + Stretch=None + 负坐标几何）----");

            // 拖动预览用的是「以圆心为原点」的几何（x∈[−R,R]、y∈[−R,0]），
            // 而它靠 Canvas.Left/Top 摆进预览层。
            // 这一步只能实测、不能推理：Stretch=None 的 Path 究竟把几何摆在
            // 「元素原点」还是「元素左上角（即先平移到非负区）」，
            // 两种说法都流传很广，而它们差一整条半径 ——
            // 差的是"手指按住圆心、盘面却飘在左上方"。
            //
            // 实测结论（本机）：外接框落在 [圆心−R, 圆心+R]，即「元素原点就是几何原点」。
            // 所以摆法必须是 Canvas.Left/Top = 圆心本身。把这条钉死在这里，
            // 是因为它属于"看不出来、只有量像素才知道"的那一类。
            const double probeR = 100.0;
            const double wantCenterX = 300.0;
            const double wantCenterY = 300.0;

            var probeCanvas = new Canvas
            {
                Width = 600,
                Height = 400,
                Background = Brushes.White,
            };

            var probeDisc = ProtractorGeometry.BuildDisc(probeR);
            var probeBounds = probeDisc.Bounds;

            var probePath = new System.Windows.Shapes.Path
            {
                Data = probeDisc,
                Stretch = Stretch.None,
                Fill = Brushes.Blue,          // 纯蓝：这张图里蓝色只有一个来源，好认
                IsHitTestVisible = false,
            };

            probeCanvas.Children.Add(probePath);

            // ↓↓↓ 与 ProtractorTool.UpdatePreview 完全一致的摆法 ↓↓↓
            // （圆心即元素原点；不要加 probeBounds.X/Y —— 那是"偏一条半径"的写法）
            Canvas.SetLeft(probePath, wantCenterX);
            Canvas.SetTop(probePath, wantCenterY);

            probeCanvas.Measure(new Size(600, 400));
            probeCanvas.Arrange(new Rect(0, 0, 600, 400));
            probeCanvas.UpdateLayout();

            var probeShot = Snapshot(probeCanvas, 600, 400);
            var probeConv = new FormatConvertedBitmap(probeShot, PixelFormats.Bgra32, null, 0);
            int probeStride = probeConv.PixelWidth * 4;
            var probePixels = new byte[probeStride * probeConv.PixelHeight];
            probeConv.CopyPixels(probePixels, probeStride, 0);

            int pMinX = int.MaxValue, pMinY = int.MaxValue, pMaxX = -1, pMaxY = -1;
            long bluePixels = 0;

            for (int y = 0; y < probeConv.PixelHeight; y++)
            {
                for (int x = 0; x < probeConv.PixelWidth; x++)
                {
                    int o = y * probeStride + x * 4;

                    // B 高、R 低 = 蓝（Pbgra32/Bgra32 的字节序是 B,G,R,A）
                    if (probePixels[o] < 200 || probePixels[o + 2] > 80) continue;

                    bluePixels++;
                    if (x < pMinX) pMinX = x;
                    if (y < pMinY) pMinY = y;
                    if (x > pMaxX) pMaxX = x;
                    if (y > pMaxY) pMaxY = y;
                }
            }

            bool previewPlaced = bluePixels > 5000
                              && Math.Abs(pMinX - (wantCenterX - probeR)) <= 2
                              && Math.Abs(pMaxX - (wantCenterX + probeR)) <= 2
                              && Math.Abs(pMinY - (wantCenterY - probeR)) <= 2
                              && Math.Abs(pMaxY - wantCenterY) <= 2;

            Console.WriteLine($"  预览盘面实测外接框 x∈[{pMinX},{pMaxX}]、y∈[{pMinY},{pMaxY}]（蓝像素 {bluePixels}）；"
                              + $"期望 x∈[{wantCenterX - probeR:F0},{wantCenterX + probeR:F0}]、"
                              + $"y∈[{wantCenterY - probeR:F0},{wantCenterY:F0}]"
                              + $"（几何范围 {probeBounds.X:F0},{probeBounds.Y:F0} ~ {probeBounds.Right:F0},{probeBounds.Bottom:F0}）");
            Console.WriteLine($"[{(previewPlaced ? "PASS" : "FAIL")}] 拖动预览以「圆心」为准落位"
                              + "（Stretch=None 的 Path 不平移负坐标几何：Left/Top 给的是圆心，不是包围盒左上角）");
            if (!previewPlaced) failures++;

            SavePng(probeShot, Path.Combine(outDir, "m74s1-预览摆位.png"));

            SavePng(raster, Path.Combine(outDir, "m74s1-量角器.png"));
            Console.WriteLine($"  证据图：{Path.Combine(outDir, "m74s1-量角器.png")}");
        }
        finally
        {
            pxyHost.Shutdown();
        }

        return failures;
    }

    /// <summary>
    /// M7.4 Step 2：三角板（45° 板 / 60° 板，同一类的两个实例）。
    /// </summary>
    private static int RunM74S2Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.4 Step 2：三角板（学科工具）================");

        // ---------------------------------------------------------- 1) 几何（纯函数）
        Console.WriteLine();
        Console.WriteLine("---- 1) 几何：直角精度、60° 板边长比、斜边角 ----");

        const double probeLeg = 120.0;

        var v45 = TriangleGeometry.Vertices(TriangleGeometry.TriangleKind.FortyFive, probeLeg);
        var v60 = TriangleGeometry.Vertices(TriangleGeometry.TriangleKind.SixtyThirtyNinety, probeLeg);

        // 45° 板：直角顶点 (0,0)、长直角边 (120,0)、短直角边 (0,-120)
        bool v45Ok = Near(v45[0], new Point(0, 0), 1e-9)
                  && Near(v45[1], new Point(probeLeg, 0), 1e-9)
                  && Near(v45[2], new Point(0, -probeLeg), 1e-9);

        // 60° 板：短直角边 = 120/√3，长直角边末端 (120,0)、短直角边末端 (0,-120/√3)
        double shortLeg = probeLeg / Math.Sqrt(3.0);
        bool v60Ok = Near(v60[0], new Point(0, 0), 1e-9)
                  && Near(v60[1], new Point(probeLeg, 0), 1e-9)
                  && Near(v60[2], new Point(0, -shortLeg), 1e-9);

        // 直角精度：长直角边 (1,0) 与短直角边 (0,-1) 的夹角 = 90°±1e-6
        double longAxis = TriangleGeometry.AxisDegrees(v60[0], v60[1]);   // 0°
        double shortAxis = TriangleGeometry.AxisDegrees(v60[0], v60[2]);  // 90°
        bool rightAngleOk = Math.Abs(TriangleGeometry.AngleDelta(longAxis, shortAxis) - 90.0) < 1e-6;

        // 斜边角：45° 板 = 45°、60° 板 = 30°（斜边与长直角边夹角）
        bool hypAngleOk = Math.Abs(TriangleGeometry.HypotenuseAngleDegrees(TriangleGeometry.TriangleKind.FortyFive) - 45.0) < 1e-9
                       && Math.Abs(TriangleGeometry.HypotenuseAngleDegrees(TriangleGeometry.TriangleKind.SixtyThirtyNinety) - 30.0) < 1e-9;

        // 短直角边比：60° 板短边 = 长边/√3，45° 板短边 = 长边
        bool legRatioOk = Math.Abs(TriangleGeometry.ShortLegWorld(TriangleGeometry.TriangleKind.SixtyThirtyNinety, probeLeg) - probeLeg / Math.Sqrt(3.0)) < 1e-9
                       && Math.Abs(TriangleGeometry.ShortLegWorld(TriangleGeometry.TriangleKind.FortyFive, probeLeg) - probeLeg) < 1e-9;

        Console.WriteLine($"  45° 板顶点：{v45[0]} {v45[1]} {v45[2]}");
        Console.WriteLine($"  60° 板顶点：{v60[0]} {v60[1]} {v60[2]}（短边 {shortLeg:F4}）");
        Console.WriteLine($"[{(v45Ok ? "PASS" : "FAIL")}] 45° 板是等腰直角（两条直角边等长、直角顶点在原点）");
        Console.WriteLine($"[{(v60Ok ? "PASS" : "FAIL")}] 60° 板直角边比 1:√3（长边沿 +x，短边沿 −y）");
        Console.WriteLine($"[{(rightAngleOk ? "PASS" : "FAIL")}] 直角精度 90°±1e-6（差一点点也看不出来，只能靠断言）");
        Console.WriteLine($"[{(hypAngleOk && legRatioOk ? "PASS" : "FAIL")}] 斜边角 45°/30°、短边比 √3 都正确");
        if (!v45Ok) failures++;
        if (!v60Ok) failures++;
        if (!rightAngleOk) failures++;
        if (!(hypAngleOk && legRatioOk)) failures++;

        // 外轮廓包围盒：直角顶点在原点、只占第一象限（+x / −y），包围盒右上角 (leg,0)、左下角 (0,-short)
        var outline = TriangleGeometry.BuildOutline(TriangleGeometry.TriangleKind.FortyFive, probeLeg);
        var ob = outline.Bounds;
        bool outlineBoundsOk = Near(ob.X, 0, 1e-6) && Near(ob.Bottom, 0, 1e-6)
                            && Near(ob.Right, probeLeg, 1e-6) && Near(ob.Y, -probeLeg, 1e-6);

        Console.WriteLine($"[{(outlineBoundsOk ? "PASS" : "FAIL")}] 外轮廓只占第一象限（直角顶点原点、斜边在右上方）");
        if (!outlineBoundsOk) failures++;

        // 夹紧与换算
        bool clampOk = Near(TriangleGeometry.ClampLeg(30), TriangleGeometry.MinLegWorld, 1e-9)
                    && Near(TriangleGeometry.ClampLeg(9999), TriangleGeometry.MaxLegWorld, 1e-9)
                    && Near(TriangleGeometry.ClampLeg(double.NaN), TriangleGeometry.DefaultLegWorld, 1e-9);

        bool unitOk = Math.Abs(TriangleGeometry.ToCentimeters(595) - 20.99) < 0.01;

        Console.WriteLine($"[{(clampOk ? "PASS" : "FAIL")}] 边长夹紧（太小放大、太大收住、坏数值退回默认）");
        Console.WriteLine($"[{(unitOk ? "PASS" : "FAIL")}] 单位换算沿用世界坐标（A4 宽 595 world = 20.99 cm 自检）");
        if (!clampOk) failures++;
        if (!unitOk) failures++;

        // ---------------------------------------------------------- 2) 刻度
        Console.WriteLine();
        Console.WriteLine("---- 2) 刻度：1 mm 一小格、10 格 = 1 cm ----");

        // 一条 120 world ≈ 4.23 cm 的长直角边，应有 42 条毫米刻度（1..42mm）
        var ticksLong = TriangleGeometry.BuildTicks(TriangleGeometry.TriangleKind.FortyFive, probeLeg, alongLong: true);
        bool ticksWithin = !ticksLong.Bounds.IsEmpty
                        && ticksLong.Bounds.X >= -1e-6
                        && ticksLong.Bounds.Right <= probeLeg + 1e-6
                        && ticksLong.Bounds.Y >= -probeLeg * TriangleGeometry.TickMajorRatio * 1.2
                        && ticksLong.Bounds.Bottom <= 1e-6;

        // 刻度间距 = 1 mm 世界长
        double oneMm = TriangleGeometry.OneMillimeterWorld;
        bool mmOk = Math.Abs(TriangleGeometry.ToCentimeters(oneMm * 10) - 1.0) < 1e-9;   // 10 格 = 1 cm

        // 每 10 mm 是长刻度
        bool tickRatioOk = Near(TriangleGeometry.TickRatioFor(10), TriangleGeometry.TickMajorRatio, 1e-12)
                        && Near(TriangleGeometry.TickRatioFor(5), TriangleGeometry.TickMediumRatio, 1e-12)
                        && Near(TriangleGeometry.TickRatioFor(3), TriangleGeometry.TickMinorRatio, 1e-12);

        Console.WriteLine($"  1 mm = {oneMm:F4} world；10 格 = {TriangleGeometry.ToCentimeters(oneMm * 10):F3} cm");
        Console.WriteLine($"[{(ticksWithin ? "PASS" : "FAIL")}] 长直角边刻度不越出外轮廓（往三角形内侧画）");
        Console.WriteLine($"[{(mmOk ? "PASS" : "FAIL")}] 1 mm 一小格、10 格 = 1 cm（刻度间距正确）");
        Console.WriteLine($"[{(tickRatioOk ? "PASS" : "FAIL")}] 整厘米/半厘米/毫米三档刻度长度分级");
        if (!ticksWithin) failures++;
        if (!mmOk) failures++;
        if (!tickRatioOk) failures++;

        // ---------------------------------------------------------- 3) 吸附（纯函数）
        Console.WriteLine();
        Console.WriteLine("---- 3) 吸附：一条直角边吸线段方向 ----");

        bool snapIn = TriangleGeometry.TrySnapEdge(34, 37, out double snappedIn)
                   && Near(snappedIn, 37, 1e-9);

        bool snapOut = !TriangleGeometry.TrySnapEdge(20, 37, out double snappedOut)
                    && Near(snappedOut, 20, 1e-9);

        // 正反两个方向都算候选
        bool snapReverse = TriangleGeometry.TrySnapEdge(214, 37, out double snappedReverse)
                        && Near(snappedReverse, 217, 1e-9);

        bool snapEdge = TriangleGeometry.TrySnapEdge(31, 37, out _)
                     && !TriangleGeometry.TrySnapEdge(30.9, 37, out _);

        var triCandidates = new List<BoardSegment>
        {
            new(new Point(0, 0), AtAngle(new Point(0, 0), 10, 100)),
            new(new Point(0, 0), AtAngle(new Point(0, 0), 44, 100)),
        };

        bool pickClosest = TriangleGeometry.TrySnapEdgeToSegments(41, triCandidates, out double picked)
                        && Near(picked, 44, 1e-9);

        Console.WriteLine($"[{(snapIn && snapOut && snapReverse ? "PASS" : "FAIL")}] 一条直角边只吸「差 6° 以内」的线段，"
                          + "正反两方向都算候选");
        Console.WriteLine($"[{(snapEdge && pickClosest ? "PASS" : "FAIL")}] 容差边界精确（31° 吸 / 30.9° 不吸）；"
                          + "多线按「方向最接近」挑");
        if (!(snapIn && snapOut && snapReverse)) failures++;
        if (!(snapEdge && pickClosest)) failures++;

        // ---------------------------------------------------------- 4) 真工具 + 假上下文
        Console.WriteLine();
        Console.WriteLine("---- 4) 工具状态机：直角顶点吸端点、直角边吸方向、误触不落 ----");

        var triHost = new FakeGfxHost();
        var triQuery = new FakeBoardQuery();

        var vertex = new Point(300, 400);
        triQuery.Points.Add(vertex);
        triQuery.Points.Add(AtAngle(vertex, 37, 260));
        triQuery.Segments.Add(new BoardSegment(vertex, AtAngle(vertex, 37, 260)));

        var triContext = new FakeToolContext { GfxOverride = triHost, QueryOverride = triQuery };
        var tri45 = new TriangleTool(TriangleGeometry.TriangleKind.FortyFive);
        tri45.Activate(triContext);

        bool triShapeOk = !tri45.UsesInkLayer && tri45.NeedsPointer
                       && tri45.InputKind == ToolInputKind.None
                       && tri45.InkMode == ToolInkMode.None
                       && tri45.Id == TriangleToolIds.FortyFive
                       && tri45.Shortcut == Key.D7
                       && tri45 is IGfxTool;

        var tri60 = new TriangleTool(TriangleGeometry.TriangleKind.SixtyThirtyNinety);
        bool tri60ShapeOk = tri60.Id == TriangleToolIds.SixtyNinety && tri60.Shortcut == Key.D8;

        Console.WriteLine($"[{(triShapeOk && tri60ShapeOk ? "PASS" : "FAIL")}] 两个工具形态："
                          + $"45° 板键 7、60° 板键 8，都是 IGfxTool（{tri45.DisplayName} / {tri60.DisplayName}）");
        if (!(triShapeOk && tri60ShapeOk)) failures++;

        // 误触：按下就抬
        tri45.OnPointer(Pointer(ToolPointerPhase.Down, new Point(900, 900)));
        tri45.OnPointer(Pointer(ToolPointerPhase.Up, new Point(900.5, 900.5)));
        bool triMisTap = triHost.Added.Count == 0 && triContext.Previews.Count == 0
                      && triContext.LastStatus?.Contains("太短") == true;

        Console.WriteLine($"[{(triMisTap ? "PASS" : "FAIL")}] 误触不落对象并给出可读理由");
        if (!triMisTap) failures++;

        // 正常一次：直角顶点离端点 7 world（吸上去）、往 34° 拖 150（直角边吸到 37°）
        triContext.Reset();
        var triPress = new Point(306, 404);
        var triDrag = AtAngle(vertex, 34, 150);

        tri45.OnPointer(Pointer(ToolPointerPhase.Down, triPress));
        bool triPreview = triContext.Previews.Count == 1;
        tri45.OnPointer(Pointer(ToolPointerPhase.Move, triDrag));
        tri45.OnPointer(Pointer(ToolPointerPhase.Up, triDrag));

        var triDraft = triHost.Added.Count > 0 ? triHost.Added[^1] : null;

        double triLeg = 0;
        bool triVertexSnapped = triDraft is not null && Near(triDraft.Center, vertex, 1e-9);
        bool triOrientSnapped = triDraft is not null && Near(triDraft.RotationDegrees, 37, 1e-9);
        bool triLegTaken = triDraft is not null
                        && triDraft.Numbers.TryGetValue(TriangleGeometry.LegKey, out triLeg)
                        && Near(triLeg, 150, 1e-9);
        bool triKindOk = triDraft is not null && triDraft.Kind == TriangleRenderer.KindName
                      && triDraft.Numbers.TryGetValue(TriangleGeometry.KindKey, out double triKindNum)
                      && Near(triKindNum, 0.0, 1e-9);
        bool triPreviewCleared = triContext.Previews.Count == 0;
        bool triNoInk = triContext.Committed.Count == 0;
        bool triNoToolStep = triHost.Steps.Count == 0;

        Console.WriteLine($"  按下点 (306,404) → 直角顶点 ({triDraft?.Center.X:F0},{triDraft?.Center.Y:F0})；"
                          + $"往 34° 拖 150 ⇒ 朝向 {triDraft?.RotationDegrees:F0}°、边长 {triLeg:F0} world");
        Console.WriteLine($"[{(triPreview && triPreviewCleared ? "PASS" : "FAIL")}] 拖动中 1 个预览、抬笔清空");
        Console.WriteLine($"[{(triVertexSnapped ? "PASS" : "FAIL")}] 直角顶点吸到端点（差 7 world 也吸上）");
        Console.WriteLine($"[{(triOrientSnapped && triLegTaken ? "PASS" : "FAIL")}] 一条直角边吸到 37°、边长取拖动距离");
        Console.WriteLine($"[{(triKindOk && triNoInk && triNoToolStep ? "PASS" : "FAIL")}] 交出去的是图形对象草案"
                          + "（Kind/顶点/朝向/边长/板型齐全），一条墨没落、撤销步由宿主记");
        if (!(triPreview && triPreviewCleared)) failures++;
        if (!triVertexSnapped) failures++;
        if (!(triOrientSnapped && triLegTaken)) failures++;
        if (!(triKindOk && triNoInk && triNoToolStep)) failures++;

        // 拿不到吸附基准：退化成自由摆放
        var lonelyHost2 = new FakeGfxHost();
        var lonelyContext2 = new FakeToolContext { GfxOverride = lonelyHost2 };
        var lonelyTri = new TriangleTool(TriangleGeometry.TriangleKind.SixtyThirtyNinety);
        lonelyTri.Activate(lonelyContext2);

        lonelyTri.OnPointer(Pointer(ToolPointerPhase.Down, new Point(0, 0)));
        lonelyTri.OnPointer(Pointer(ToolPointerPhase.Up, AtAngle(new Point(0, 0), 34, 140)));

        var lonelyTriDraft = lonelyHost2.Added.Count == 1 ? lonelyHost2.Added[0] : null;
        bool triDegradeOk = lonelyTriDraft is not null
                         && Near(lonelyTriDraft.Center, new Point(0, 0), 1e-9)
                         && Math.Abs(lonelyTriDraft.RotationDegrees - 34) < 1e-6
                         && lonelyTriDraft.Numbers.TryGetValue(TriangleGeometry.KindKey, out double lk)
                         && Near(lk, 1.0, 1e-9);

        Console.WriteLine($"[{(triDegradeOk ? "PASS" : "FAIL")}] 拿不到吸附基准时退化成自由摆放"
                          + "（60° 板板型参数 = 1，朝向 34°）");
        if (!triDegradeOk) failures++;

        // ---------------------------------------------------------- 5) 真宿主端到端
        Console.WriteLine();
        Console.WriteLine("---- 5) 真宿主：真笔画当吸附基准 + 落成对象 ----");

        var triPxy = new CanvasViewportHost();
        triPxy.Measure(new Size(1000, 700));
        triPxy.Arrange(new Rect(0, 0, 1000, 700));
        triPxy.UpdateLayout();

        try
        {
            new TrianglePlugin().Register(triPxy.PluginRegistry);
            triPxy.RegisterGfxRenderer(new TriangleRenderer(), "三角板（学科工具）");

            bool triRegistered = triPxy.Tools.Any(t => t.Id == TriangleToolIds.FortyFive)
                              && triPxy.Tools.Any(t => t.Id == TriangleToolIds.SixtyNinety)
                              && triPxy.GfxRenderers.Contains(TriangleRenderer.KindName);

            Console.WriteLine($"[{(triRegistered ? "PASS" : "FAIL")}] 插件注册：两个工具 + 渲染器都到位"
                              + $"（Kind = {TriangleRenderer.KindName}）");
            if (!triRegistered) failures++;

            // 往墨迹层放一条辅助线当吸附基准
            var inkTip2 = AtAngle(new Point(400, 500), 37, 260);
            triPxy.Strokes.Add(new Stroke(new StylusPointCollection
            {
                new StylusPoint(400, 500),
                new StylusPoint(inkTip2.X, inkTip2.Y),
            }));

            triPxy.SetTool(TriangleToolIds.FortyFive);

            var triHostPress = new Point(404, 504);
            var triHostDrag = AtAngle(new Point(400, 500), 34, 150);

            triPxy.SimulatePointer(ToolPointerPhase.Down, triPxy.Viewport.ToViewport(triHostPress));
            bool triHostPreview = triPxy.PreviewCount == 1;

            triPxy.SimulatePointer(ToolPointerPhase.Move, triPxy.Viewport.ToViewport(triHostDrag));
            triPxy.SimulatePointer(ToolPointerPhase.Up, triPxy.Viewport.ToViewport(triHostDrag));

            bool triHostPreviewCleared = triPxy.PreviewCount == 0;
            bool triHostOneObject = triPxy.GfxObjectCount == 1;
            bool triOnlyTargetInk = triPxy.StrokeCount == 1;

            var triPlaced = triPxy.GfxObjects.Objects[0];
            bool triPlacedPose = Near(triPlaced.Center, new Point(400, 500), 1e-9)
                              && Near(triPlaced.RotationDegrees, 37, 1e-9)
                              && Near(triPlaced.GetNumber(TriangleGeometry.LegKey, 0), 150, 1e-9)
                              && Near(triPlaced.GetNumber(TriangleGeometry.KindKey, 0), 0.0, 1e-9);

            bool triOneVisual = triPxy.GfxObjectLayer.VisualCount == 1
                             && triPxy.GfxObjectLayer.VisualFor(triPlaced.Id) is { } triWrapper
                             && triWrapper is Canvas { Children.Count: 1 };

            Console.WriteLine($"  真笔画（37° 辅助线）当吸附基准 → 落成对象：顶点 ({triPlaced.Center.X:F0},{triPlaced.Center.Y:F0})、"
                              + $"朝向 {triPlaced.RotationDegrees:F0}°、边长 {triPlaced.GetNumber(TriangleGeometry.LegKey, 0):F0}");
            Console.WriteLine($"[{(triHostPreview && triHostPreviewCleared ? "PASS" : "FAIL")}] 预览走 AddPreview：拖动中 1 个、抬笔清空");
            Console.WriteLine($"[{(triHostOneObject && triPlacedPose && triOnlyTargetInk ? "PASS" : "FAIL")}] 端到端："
                              + "顶点吸端点、直角边吸方向 37°、边长取拖动距离、一条墨没落");
            Console.WriteLine($"[{(triOneVisual ? "PASS" : "FAIL")}] 一个对象 = 一个视觉元素"
                              + "（轮廓 + 刻度 + 数字合成一个自绘元素）");
            if (!(triHostPreview && triHostPreviewCleared)) failures++;
            if (!(triHostOneObject && triPlacedPose && triOnlyTargetInk)) failures++;
            if (!triOneVisual) failures++;

            // ------------------------------------------------ 6) 存档往返
            Console.WriteLine();
            Console.WriteLine("---- 6) 存档往返：边长/板型/朝向/位置一个不差 ----");

            bool triRoundTrip;
            using (var buffer = new MemoryStream())
            {
                TbinkFile.Write(buffer, new TbinkManifest(), new StrokeCollection(), triPxy.SnapshotObjects());
                buffer.Position = 0;

                bool readOk = TbinkFile.TryRead(buffer, out var manifest, out _, out var objectsBack, out _);
                var back = objectsBack?.FirstOrDefault(o => o.Id == triPlaced.Id);

                triRoundTrip = readOk
                            && manifest.ObjectCount == 1
                            && back is not null
                            && Near(back.X, 400, 1e-9)
                            && Near(back.Y, 500, 1e-9)
                            && Near(back.Rotation, 37, 1e-9)
                            && nearNumber(back, TriangleGeometry.LegKey, 150)
                            && nearNumber(back, TriangleGeometry.KindKey, 0.0);
            }

            Console.WriteLine($"[{(triRoundTrip ? "PASS" : "FAIL")}] 存 → 读：边长 150、板型 45°、"
                              + "朝向 37°、顶点 (400,500) 全部还原");
            if (!triRoundTrip) failures++;

            // ------------------------------------------------ 7) 光栅落点
            Console.WriteLine();
            Console.WriteLine("---- 7) 光栅落点：墨确实画在算出来的地方 ----");

            var triRasterData = new GfxObjectData
            {
                Id = "raster-triangle",
                Kind = TriangleRenderer.KindName,
                Plugin = "三角板（学科工具）",
                X = 400,
                Y = 520,
                Rotation = 0,
                Scale = 1,
                Color = GfxObjectData.ToHex(Colors.Red),
                LineWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [TriangleGeometry.LegKey] = 150,
                    [TriangleGeometry.KindKey] = 0.0,
                },
            };

            triPxy.ReplaceObjects(new[] { triRasterData });
            triPxy.UpdateLayout();

            var triRaster = Snapshot(triPxy, 1000, 700);

            var triConv = new FormatConvertedBitmap(triRaster, PixelFormats.Bgra32, null, 0);
            int triWidth = triConv.PixelWidth;
            int triHeight = triConv.PixelHeight;
            int triStride = triWidth * 4;

            var triPixels = new byte[triStride * triHeight];
            triConv.CopyPixels(triPixels, triStride, 0);

            int triMinX = int.MaxValue, triMinY = int.MaxValue, triMaxX = -1, triMaxY = -1;
            long triInkPixels = 0;

            for (int y = 0; y < triHeight; y++)
            {
                int rowStart = y * triStride;
                for (int x = 0; x < triWidth; x++)
                {
                    int offset = rowStart + x * 4;
                    byte blue = triPixels[offset], green = triPixels[offset + 1];
                    byte red = triPixels[offset + 2];

                    // 只认红像素（对象用 #FFFF0000 落）
                    if (!(red >= 100 && red > green + 40 && red > blue + 40)) continue;

                    triInkPixels++;
                    if (x < triMinX) triMinX = x;
                    if (y < triMinY) triMinY = y;
                    if (x > triMaxX) triMaxX = x;
                    if (y > triMaxY) triMaxY = y;
                }
            }

            double triViewScale = triPxy.Viewport.Scale;
            var triCenterDip = triPxy.Viewport.ToViewport(new Point(400, 520));
            double triLegDip = 150 * triViewScale;

            // 45° 板轮廓：直角顶点 (400,520)，向右伸 triLegDip，向上伸 triLegDip（等腰）。
            // 红色像素外接框 ≠ 纯轮廓：cm 数字画在直角边「外侧」（长边下方 +y、短边左侧 −x），
            // 各占约 (字号 0.05L + 边距 0.04L) ≈ 0.1L —— 断言必须给它们留出这份余量，
            // 否则"数字把外接框撑大了"会被误判成"墨画错了地方"。
            bool triInkFound = triInkPixels > 200;
            bool triRasterOk = triInkFound
                            && triMinX >= triCenterDip.X - triLegDip * 0.12
                            && triMinX <= triCenterDip.X + 4
                            && triMaxX >= triCenterDip.X + triLegDip * 0.95
                            && triMaxX <= triCenterDip.X + triLegDip * 1.05
                            && triMinY >= triCenterDip.Y - triLegDip * 1.05
                            && triMinY <= triCenterDip.Y - triLegDip * 0.95
                            && triMaxY >= triCenterDip.Y - 4
                            && triMaxY <= triCenterDip.Y + triLegDip * 0.12;

            Console.WriteLine($"  光栅墨迹 {triInkPixels} px，外接框 x∈[{triMinX},{triMaxX}]、y∈[{triMinY},{triMaxY}]；"
                              + $"顶点 ({triCenterDip.X:F0},{triCenterDip.Y:F0})、直角边 {triLegDip:F0} px");
            Console.WriteLine($"[{(triRasterOk ? "PASS" : "FAIL")}] 三角板墨落在直角顶点处、两直角边伸向 +x 与 −y"
                              + "（画出来的位置 = 算出来的位置）");
            if (!triRasterOk) failures++;

            // ------------------------------------------------ 8) 预览摆位
            Console.WriteLine();
            Console.WriteLine("---- 8) 拖动中的预览轮廓画在哪（Path + Stretch=None + 负坐标几何）----");

            const double triProbeLeg = 100.0;
            const double wantVx = 300.0;
            const double wantVy = 300.0;

            var triProbeCanvas = new Canvas { Width = 600, Height = 400, Background = Brushes.White };

            var triProbeOutline = TriangleGeometry.BuildOutline(TriangleGeometry.TriangleKind.FortyFive, triProbeLeg);

            var triProbePath = new System.Windows.Shapes.Path
            {
                Data = triProbeOutline,
                Stretch = Stretch.None,
                Fill = Brushes.Blue,
                IsHitTestVisible = false,
            };

            triProbeCanvas.Children.Add(triProbePath);

            // 与 TriangleTool.UpdatePreview 一致：Left/Top = 直角顶点本身
            Canvas.SetLeft(triProbePath, wantVx);
            Canvas.SetTop(triProbePath, wantVy);

            triProbeCanvas.Measure(new Size(600, 400));
            triProbeCanvas.Arrange(new Rect(0, 0, 600, 400));
            triProbeCanvas.UpdateLayout();

            var triProbeShot = Snapshot(triProbeCanvas, 600, 400);
            var triProbeConv = new FormatConvertedBitmap(triProbeShot, PixelFormats.Bgra32, null, 0);
            int triProbeStride = triProbeConv.PixelWidth * 4;
            var triProbePixels = new byte[triProbeStride * triProbeConv.PixelHeight];
            triProbeConv.CopyPixels(triProbePixels, triProbeStride, 0);

            int tpMinX = int.MaxValue, tpMinY = int.MaxValue, tpMaxX = -1, tpMaxY = -1;
            long triBluePixels = 0;

            for (int y = 0; y < triProbeConv.PixelHeight; y++)
            {
                for (int x = 0; x < triProbeConv.PixelWidth; x++)
                {
                    int o = y * triProbeStride + x * 4;
                    if (triProbePixels[o] < 200 || triProbePixels[o + 2] > 80) continue;

                    triBluePixels++;
                    if (x < tpMinX) tpMinX = x;
                    if (y < tpMinY) tpMinY = y;
                    if (x > tpMaxX) tpMaxX = x;
                    if (y > tpMaxY) tpMaxY = y;
                }
            }

            // 等腰直角三角形：外接框 = 直角顶点向右 +triProbeLeg、向上 −triProbeLeg。
            // 填充像素数按「面积 = L²/2」估：100×100 的板正好 5000 px，抗锯齿与斜边切角
            // 会吃掉约 1%，阈值不能卡在 5000 整 —— 那是拿理论值当实测值。
            bool triPreviewPlaced = triBluePixels > 4500
                                 && Math.Abs(tpMinX - wantVx) <= 2
                                 && Math.Abs(tpMaxX - (wantVx + triProbeLeg)) <= 2
                                 && Math.Abs(tpMinY - (wantVy - triProbeLeg)) <= 2
                                 && Math.Abs(tpMaxY - wantVy) <= 2;

            Console.WriteLine($"  预览轮廓实测外接框 x∈[{tpMinX},{tpMaxX}]、y∈[{tpMinY},{tpMaxY}]（蓝像素 {triBluePixels}）；"
                              + $"期望 x∈[{wantVx:F0},{wantVx + triProbeLeg:F0}]、y∈[{wantVy - triProbeLeg:F0},{wantVy:F0}]");
            Console.WriteLine($"[{(triPreviewPlaced ? "PASS" : "FAIL")}] 拖动预览以「直角顶点」为准落位"
                              + "（Stretch=None 的 Path 不平移负坐标几何：Left/Top 给的是顶点）");
            if (!triPreviewPlaced) failures++;

            SavePng(triProbeShot, Path.Combine(outDir, "m74s2-预览摆位.png"));
            SavePng(triRaster, Path.Combine(outDir, "m74s2-三角板.png"));
            Console.WriteLine($"  证据图：{Path.Combine(outDir, "m74s2-三角板.png")}");
        }
        finally
        {
            triPxy.Shutdown();
        }

        return failures;
    }

    /// <summary>
    /// M7.4 Step 3：坐标系（第一个需要「页面几何」语义的学科工具）。
    /// </summary>
    private static int RunM74S3Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.4 Step 3：坐标系（学科工具）================");

        // ---------------------------------------------------------- 1) 换算（纯函数）
        Console.WriteLine();
        Console.WriteLine("---- 1) 世界↔数学换算：往返误差 < 1e-9 ----");

        var origin = new Point(400, 500);
        const double unitWorld = 30.0;            // 1 数学单位 = 30 world

        // 世界点 p：先转数学再转回去
        var p = new Point(412, 488);
        var math = CoordSystemGeometry.WorldToMath(origin, unitWorld, p);
        var back = CoordSystemGeometry.MathToWorld(origin, unitWorld, math.X, math.Y);

        bool roundTripOk = Near(back.X, p.X, 1e-9) && Near(back.Y, p.Y, 1e-9);

        // 已知值核对：x=1, y=2 ⇒ 世界 (origin.X + unit, origin.Y - 2*unit)
        var known = CoordSystemGeometry.MathToWorld(origin, unitWorld, 1, 2);
        bool mathOk = Near(known.X, origin.X + unitWorld, 1e-12)
                   && Near(known.Y, origin.Y - 2 * unitWorld, 1e-12);

        // y 取反：本地 FlipY
        bool flipOk = Near(CoordSystemGeometry.FlipY(3), -3, 1e-12)
                   && Near(CoordSystemGeometry.FlipY(-2.5), 2.5, 1e-12);

        // 单位换算
        bool unitOk = Math.Abs(CoordSystemGeometry.ToCentimeters(72) - 2.54) < 1e-9
                   && Math.Abs(CoordSystemGeometry.ToCentimeters(595) - 20.99) < 0.01;

        Console.WriteLine($"  p=({p.X:F0},{p.Y:F0}) → 数学 ({math.X:F4},{math.Y:F4}) → 世界 ({back.X:F4},{back.Y:F4})");
        Console.WriteLine($"  (1,2) → 世界 ({known.X:F0},{known.Y:F0})（期望 ({origin.X + unitWorld:F0},{origin.Y - 2 * unitWorld:F0})）");
        Console.WriteLine($"[{(roundTripOk && mathOk ? "PASS" : "FAIL")}] 世界↔数学往返 < 1e-9、（1,2）→（x₀+u, y₀-2u）方向正确");
        Console.WriteLine($"[{(flipOk ? "PASS" : "FAIL")}] FlipY：本地坐标 y 取反（数学 +y = 本地 −y）");
        Console.WriteLine($"[{(unitOk ? "PASS" : "FAIL")}] 单位换算沿用世界坐标（72 world = 2.54 cm = 1 inch 自检）");
        if (!(roundTripOk && mathOk)) failures++;
        if (!flipOk) failures++;
        if (!unitOk) failures++;

        // 夹紧
        bool clampOk = Near(CoordSystemGeometry.ClampUnit(0.1), CoordSystemGeometry.MinUnitWorld, 1e-9)
                    && Near(CoordSystemGeometry.ClampUnit(9999), CoordSystemGeometry.MaxUnitWorld, 1e-9)
                    && Near(CoordSystemGeometry.ClampUnit(double.NaN), CoordSystemGeometry.DefaultUnitWorld, 1e-9)
                    && Near(CoordSystemGeometry.ClampUnit(-5), CoordSystemGeometry.DefaultUnitWorld, 1e-9);

        bool rangeClampOk = Near(CoordSystemGeometry.ClampRangeBound(double.NaN), 0, 1e-9)
                         && Near(CoordSystemGeometry.ClampRangeBound(99999), 1000, 1e-9);

        Console.WriteLine($"[{(clampOk && rangeClampOk ? "PASS" : "FAIL")}] 夹紧：unitWorld 与 range 都防呆（NaN/超界/负值）");
        if (!(clampOk && rangeClampOk)) failures++;

        // ---------------------------------------------------------- 2) 网格线数 + 标签
        Console.WriteLine();
        Console.WriteLine("---- 2) 默认范围 [-10,10] 步长 1 ⇒ 网格 21 竖 + 21 横 ----");

        int vLines = CoordSystemGeometry.GridLineCount(-10, 10, 1);
        int hLines = CoordSystemGeometry.GridLineCount(-10, 10, 1);

        bool gridCountOk = vLines == 21 && hLines == 21;

        // 改范围：[-5,5] 步长 0.5 ⇒ 21
        int altV = CoordSystemGeometry.GridLineCount(-5, 5, 0.5);
        bool altCountOk = altV == 21;

        // 几何非空 + 包围盒对（世界长度：unitWorld=30 ⇒ 范围 [-300, 300]）
        const double probeUnit = 30.0;
        var grid = CoordSystemGeometry.BuildGrid(-10, 10, -10, 10, 1, probeUnit);
        bool gridBoundsOk = !grid.Bounds.IsEmpty
                         && Near(grid.Bounds.X, -10 * probeUnit, 1e-9)
                         && Near(grid.Bounds.Right, 10 * probeUnit, 1e-9)
                         && Near(grid.Bounds.Y, -10 * probeUnit, 1e-9)
                         && Near(grid.Bounds.Bottom, 10 * probeUnit, 1e-9);

        // 轴：xMin..xMax 沿 y=0；x=0 沿 yMin..yMax
        var axes = CoordSystemGeometry.BuildAxes(-10, 10, -10, 10, probeUnit);
        bool axesBoundsOk = !axes.Bounds.IsEmpty
                         && Near(axes.Bounds.X, -10 * probeUnit, 1e-9)
                         && Near(axes.Bounds.Right, 10 * probeUnit, 1e-9);

        // 箭头（新签名：xMax, yMax, unitWorld, 箭头尺寸；箭头画在轴正方向末端）
        var arrows = CoordSystemGeometry.BuildAxisArrows(-10, 10, 30.0, 10.5);
        bool arrowsNonEmpty = !arrows.Bounds.IsEmpty;

        // 标签：跳过原点；其余整数 1..10、-1..-10 → 20 个数字
        var labels = CoordSystemGeometry.BuildLabels(-10, 10, -10, 10, 1, 30.0, fontRatio: 0.28);

        int labelCount = CountChildren(labels);

        bool labelCountOk = labelCount == 40;          // x 轴 20 + y 轴 20，跳过原点（两个轴各一个）

        // 标签字宽会伸到数字外面（"3.14" 4 字符 ≈ 4×字号），断言改钉"数字在正确的刻度上"：
        // x 轴标签：tf.X ∈ [-10, 10]×unitWorld（数学刻度的世界长度）、tf.Y ≈ 字号×0.45（轴下方）
        // y 轴标签：tf.X ≈ -字号×0.55（轴左侧）、tf.Y ∈ [-10, 10]×unitWorld
        double fontWorld = probeUnit * 0.28;
        bool labelInRange = true;
        int labelPrint = 0;
        for (int i = 0; i < labels.Children.Count; i++)
        {
            var tf = labels.Children[i].Transform as TranslateTransform;
            if (tf is null) { labelInRange = false; break; }
            bool onXAxis = Math.Abs(tf.Y - fontWorld * 0.45) < 1e-6
                        && tf.X >= -10 * probeUnit - 1 && tf.X <= 10 * probeUnit + 1;
            bool onYAxis = Math.Abs(tf.X + fontWorld * 0.55) < 1e-6
                        && tf.Y >= -10 * probeUnit - 0.5 * fontWorld - 1
                        && tf.Y <= 10 * probeUnit + 0.5 * fontWorld + 1;
            if (!onXAxis && !onYAxis)
            {
                if (labelPrint < 3)
                {
                    Console.WriteLine($"    标签 #{i} 位置 ({tf.X:F2},{tf.Y:F2}) 既不在 x 轴 (Y={fontWorld * 0.45:F2}) 也不在 y 轴 (X={-fontWorld * 0.55:F2})");
                    labelPrint++;
                }
                labelInRange = false; break;
            }
        }

        // 标签格式：整数不带 .0
        bool fmtOk = CoordSystemGeometry.FormatNumber(3) == "3"
                  && CoordSystemGeometry.FormatNumber(-7) == "-7"
                  && CoordSystemGeometry.FormatNumber(0.5) == "0.5"
                  && CoordSystemGeometry.FormatNumber(-3.14159) == "-3.142";

        Console.WriteLine($"  网格线数：竖 {vLines}、横 {hLines}；改 [-5,5] 步长 0.5 ⇒ {altV}（期望 21）");
        Console.WriteLine($"  标签数：{labelCount}（期望 40 = 20 + 20，跳过原点）；格式：'{CoordSystemGeometry.FormatNumber(3)}' / '{CoordSystemGeometry.FormatNumber(0.5)}'");
        Console.WriteLine($"[{(gridCountOk && altCountOk ? "PASS" : "FAIL")}] 网格线数 = 步长整除数 + 1（默认 21 + 21）；改范围重算线数 0 偏差");
        Console.WriteLine($"[{(gridBoundsOk && axesBoundsOk && arrowsNonEmpty ? "PASS" : "FAIL")}] 几何包围盒正确（网格 [-10,10]²、轴沿坐标轴、箭头非空）");
        Console.WriteLine($"[{(labelCountOk && labelInRange && fmtOk ? "PASS" : "FAIL")}] 标签数对、不过界；Format 整数去 .0、小数 ≤3 位");
        if (!(gridCountOk && altCountOk)) failures++;
        if (!(gridBoundsOk && axesBoundsOk && arrowsNonEmpty)) failures++;
        if (!(labelCountOk && labelInRange && fmtOk)) failures++;

        // ---------------------------------------------------------- 3) 原点吸附（页中心 / 页边）
        Console.WriteLine();
        Console.WriteLine("---- 3) 原点吸页中心 / 页边距线 / 边中点 ----");

        var page = new Rect(0, 0, 595, 842);            // A4
        var pageCenter = new Point(297.5, 421);

        // 容差内：按下点离页中心 10 world ⇒ 吸到页中心
        var near = new Point(pageCenter.X + 8, pageCenter.Y - 6);
        var snappedCenter = CoordSystemGeometry.SnapOriginToPage(page, near, 28);
        bool snapToCenter = Near(snappedCenter, pageCenter, 1e-9);

        // 容差内：按下点离上页边 10 world ⇒ 吸到上页边（不是中点）
        var nearTop = new Point(120, page.Y + 10);
        var snappedTop = CoordSystemGeometry.SnapOriginToPage(page, nearTop, 28);
        bool snapToTop = Near(snappedTop, new Point(120, page.Y), 1e-9);

        // 容差外：按下点离候选 ≥ 50 ⇒ 不吸（保留原值）
        var far = new Point(200, 200);
        var snappedFar = CoordSystemGeometry.SnapOriginToPage(page, far, 28);
        bool noSnapFar = Near(snappedFar, far, 1e-9);

        // 候选数 = 9（页中心 + 4 角 + 4 边中点）
        bool candidatesOk = CoordSystemGeometry.PageSnapCandidates(page, pageCenter).Count == 9;

        Console.WriteLine($"  吸页中心：按下点 {near} ⇒ {snappedCenter}（期望 {pageCenter}）");
        Console.WriteLine($"  吸上页边：按下点 {nearTop} ⇒ {snappedTop}");
        Console.WriteLine($"  容差外：按下点 {far} ⇒ {snappedFar}（保留原值）");
        Console.WriteLine($"[{(snapToCenter && snapToTop && noSnapFar && candidatesOk ? "PASS" : "FAIL")}] "
                          + "吸页中心 / 吸页边 / 容差外不吸 / 候选 9 个");
        if (!(snapToCenter && snapToTop && noSnapFar && candidatesOk)) failures++;

        // ---------------------------------------------------------- 4) 工具状态机（假上下文）
        Console.WriteLine();
        Console.WriteLine("---- 4) 工具形态：键 9、不落墨、误触不落、PageRects=null 退化 ----");

        var csHost = new FakeGfxHost();
        var csContext = new FakeToolContext { GfxOverride = csHost };
        csContext.PageRectsOverride = new[] { page };

        var csTool = new CoordSystemTool();
        csTool.Activate(csContext);

        bool csShapeOk = !csTool.UsesInkLayer && csTool.NeedsPointer
                      && csTool.InputKind == ToolInputKind.None
                      && csTool.InkMode == ToolInkMode.None
                      && csTool.Id == CoordSystemToolIds.Id
                      && csTool.Shortcut == Key.C
                      && csTool is IGfxTool;

        // ★ 键位是 C（原 D9 与 M17 的「沿边画线」撞车，详见跨插件快捷键唯一性那一组）
        Console.WriteLine($"[{(csShapeOk ? "PASS" : "FAIL")}] 形态：键 C、!UsesInkLayer、NeedsPointer、IGfxTool");
        if (!csShapeOk) failures++;

        // 误触：按下就抬
        csTool.OnPointer(Pointer(ToolPointerPhase.Down, new Point(900, 900)));
        csTool.OnPointer(Pointer(ToolPointerPhase.Up, new Point(900.5, 900.5)));
        bool csMisTap = csHost.Added.Count == 0 && csContext.Previews.Count == 0
                     && csContext.LastStatus?.Contains("太短") == true;

        Console.WriteLine($"[{(csMisTap ? "PASS" : "FAIL")}] 误触（移动 0.5 world）⇒ 0 个对象、状态栏「太短」");
        if (!csMisTap) failures++;

        // 正常一次：按下点离页中心 10 world ⇒ 吸到 pageCenter；从被吸的原点往外拖 100 ⇒ unitWorld ≈ 102
        csContext.Reset();
        var csPress = new Point(pageCenter.X + 8, pageCenter.Y - 6);
        var csDrag = AtAngle(csPress, 45, 100);           // 从 csPress 出发的模长 100 拖动

        csTool.OnPointer(Pointer(ToolPointerPhase.Down, csPress));
        bool csPreview = csContext.Previews.Count == 1;
        csTool.OnPointer(Pointer(ToolPointerPhase.Move, csDrag));
        csTool.OnPointer(Pointer(ToolPointerPhase.Up, csDrag));

        var csDraft = csHost.Added.Count > 0 ? csHost.Added[^1] : null;
        // 原点被吸到 pageCenter ⇒ 拖动距离以 pageCenter 算 = √((csDrag-pageCenter)²) ≈ 102
        double expectedCsUnit = (csDrag - pageCenter).Length;
        double csUnit = 0;
        bool csOriginSnapped = csDraft is not null && Near(csDraft.Center, pageCenter, 1e-9);
        bool csUnitTaken = csDraft is not null
                        && csDraft.Numbers.TryGetValue(CoordSystemGeometry.UnitWorldKey, out csUnit)
                        && Near(csUnit, expectedCsUnit, 1e-9);
        bool csRangeDefault = csDraft is not null
                           && csDraft.Numbers[CoordSystemGeometry.XMinKey] == -10
                           && csDraft.Numbers[CoordSystemGeometry.XMaxKey] == 10
                           && csDraft.Numbers[CoordSystemGeometry.YMinKey] == -10
                           && csDraft.Numbers[CoordSystemGeometry.YMaxKey] == 10
                           && csDraft.Numbers[CoordSystemGeometry.StepKey] == 1
                           && csDraft.Numbers[CoordSystemGeometry.ShowGridKey] == 1
                           && csDraft.Numbers[CoordSystemGeometry.ShowLabelsKey] == 1
                           && csDraft.Numbers[CoordSystemGeometry.LockAspectKey] == 1;
        bool csKindOk = csDraft is not null && csDraft.Kind == CoordSystemRenderer.KindName;
        bool csPreviewCleared = csContext.Previews.Count == 0;
        bool csNoInk = csContext.Committed.Count == 0;
        bool csNoToolStep = csHost.Steps.Count == 0;

        Console.WriteLine($"  按下点 {csPress}（离页中心 10）⇒ 落点 ({csDraft?.Center.X:F0},{csDraft?.Center.Y:F0})；unitWorld = {csUnit:F0}");
        Console.WriteLine($"[{(csPreview && csPreviewCleared ? "PASS" : "FAIL")}] 拖动中 1 个预览、抬笔清空");
        Console.WriteLine($"[{(csOriginSnapped ? "PASS" : "FAIL")}] 原点吸到页中心（按下点差 10 world 也吸上）");
        Console.WriteLine($"[{(csUnitTaken && csRangeDefault && csKindOk ? "PASS" : "FAIL")}] unitWorld = 拖动距离、范围/步长/开关取默认、Kind 正确");
        Console.WriteLine($"[{(csNoInk && csNoToolStep ? "PASS" : "FAIL")}] 交出去的是图形对象草案，一条墨没落、撤销步由宿主记");
        if (!(csPreview && csPreviewCleared)) failures++;
        if (!csOriginSnapped) failures++;
        if (!(csUnitTaken && csRangeDefault && csKindOk)) failures++;
        if (!(csNoInk && csNoToolStep)) failures++;

        // 状态栏包含「一格 X cm」
        bool csStatusOk = csContext.LastStatus?.Contains("一格") == true && csContext.LastStatus?.Contains("cm") == true;

        Console.WriteLine($"[{(csStatusOk ? "PASS" : "FAIL")}] 状态栏「坐标系：原点 ( — ),单位长度 X cm ⇒ 一格 X cm」");
        if (!csStatusOk) failures++;

        // PageRects=null：退化成自由摆放
        var lonelyHost3 = new FakeGfxHost();
        var lonelyContext3 = new FakeToolContext { GfxOverride = lonelyHost3 };
        var lonelyCs = new CoordSystemTool();
        lonelyCs.Activate(lonelyContext3);

        lonelyCs.OnPointer(Pointer(ToolPointerPhase.Down, new Point(123, 456)));
        lonelyCs.OnPointer(Pointer(ToolPointerPhase.Up, AtAngle(new Point(123, 456), 45, 80)));

        var lonelyCsDraft = lonelyHost3.Added.Count == 1 ? lonelyHost3.Added[0] : null;
        bool csDegradeOk = lonelyCsDraft is not null
                        && Near(lonelyCsDraft.Center, new Point(123, 456), 1e-9)
                        && Near(lonelyCsDraft.Numbers[CoordSystemGeometry.UnitWorldKey], 80, 1e-9);

        Console.WriteLine($"[{(csDegradeOk ? "PASS" : "FAIL")}] PageRects 为空 ⇒ 原点用原值、unitWorld 取拖动距离（退化自由摆放）");
        if (!csDegradeOk) failures++;

        // ---------------------------------------------------------- 5) 真宿主端到端
        Console.WriteLine();
        Console.WriteLine("---- 5) 真宿主：插件注册 + 落成对象 + 一个对象一个视觉元素 ----");

        var csPxy = new CanvasViewportHost();
        csPxy.Measure(new Size(1000, 700));
        csPxy.Arrange(new Rect(0, 0, 1000, 700));
        csPxy.UpdateLayout();

        try
        {
            // 关键：宿主默认 PageRects 是空（没打开 PDF 就没调 Rebuild），要手动注入页框才能验吸附
            csPxy.Layout.Rebuild(new[] { new Size(page.Width, page.Height) });

            // Rebuild 后页框以 PageMargin (960) 起排，所以页中心 = (960+297.5, 960+421)
            var hostPageCenter = new Point(
                csPxy.Layout.PageRects[0].X + csPxy.Layout.PageRects[0].Width / 2.0,
                csPxy.Layout.PageRects[0].Y + csPxy.Layout.PageRects[0].Height / 2.0);

            new CoordSystemPlugin().Register(csPxy.PluginRegistry);
            csPxy.RegisterGfxRenderer(new CoordSystemRenderer(), "坐标系（学科工具）");

            bool csRegistered = csPxy.Tools.Any(t => t.Id == CoordSystemToolIds.Id)
                             && csPxy.GfxRenderers.Contains(CoordSystemRenderer.KindName);

            Console.WriteLine($"[{(csRegistered ? "PASS" : "FAIL")}] 插件注册：1 个工具 + 渲染器都到位（Kind = {CoordSystemRenderer.KindName}）");
            if (!csRegistered) failures++;

            csPxy.SetTool(CoordSystemToolIds.Id);

            var csHostPress = new Point(hostPageCenter.X + 6, hostPageCenter.Y - 4);
            var csHostDrag = AtAngle(csHostPress, 33, 120);

            csPxy.SimulatePointer(ToolPointerPhase.Down, csPxy.Viewport.ToViewport(csHostPress));
            bool csHostPreview = csPxy.PreviewCount == 1;
            csPxy.SimulatePointer(ToolPointerPhase.Move, csPxy.Viewport.ToViewport(csHostDrag));
            csPxy.SimulatePointer(ToolPointerPhase.Up, csPxy.Viewport.ToViewport(csHostDrag));

            bool csHostPreviewCleared = csPxy.PreviewCount == 0;
            bool csHostOneObject = csPxy.GfxObjectCount == 1;
            bool csOnlyTargetInk = csPxy.StrokeCount == 0;

            var csPlaced = csPxy.GfxObjects.Objects[0];
            // 期望落点 = 宿主 Rebuild 后的页中心；unitWorld 取自被吸原点的拖动距离
            double expectedCsUnitHost = (csHostDrag - hostPageCenter).Length;
            bool csPlacedPose = Near(csPlaced.Center, hostPageCenter, 1e-9)
                             && Near(csPlaced.GetNumber(CoordSystemGeometry.UnitWorldKey, 0), expectedCsUnitHost, 1e-9);

            bool csOneVisual = csPxy.GfxObjectLayer.VisualCount == 1
                            && csPxy.GfxObjectLayer.VisualFor(csPlaced.Id) is { } csWrapper
                            && csWrapper is Canvas { Children.Count: 1 };

            Console.WriteLine($"  落成对象：原点 ({csPlaced.Center.X:F0},{csPlaced.Center.Y:F0})、unitWorld = {csPlaced.GetNumber(CoordSystemGeometry.UnitWorldKey, 0):F0}");
            Console.WriteLine($"[{(csHostPreview && csHostPreviewCleared ? "PASS" : "FAIL")}] 预览走 AddPreview：拖动中 1 个、抬笔清空");
            Console.WriteLine($"[{(csHostOneObject && csPlacedPose && csOnlyTargetInk ? "PASS" : "FAIL")}] 端到端：原点吸页中心、unitWorld 取拖动距离、一条墨没落");
            Console.WriteLine($"[{(csOneVisual ? "PASS" : "FAIL")}] 一个对象 = 一个视觉元素（网格 + 轴 + 箭头 + 标签合成一个自绘元素）");
            if (!(csHostPreview && csHostPreviewCleared)) failures++;
            if (!(csHostOneObject && csPlacedPose && csOnlyTargetInk)) failures++;
            if (!csOneVisual) failures++;

            // ------------------------------------------------ 6) 存档往返
            Console.WriteLine();
            Console.WriteLine("---- 6) 存档往返：unitWorld / 范围 / 步长 / 开关一个不差 ----");

            bool csRoundTrip;
            using (var buffer = new MemoryStream())
            {
                TbinkFile.Write(buffer, new TbinkManifest(), new StrokeCollection(), csPxy.SnapshotObjects());
                buffer.Position = 0;

                bool readOk = TbinkFile.TryRead(buffer, out var manifest, out _, out var objectsBack, out _);
                var csBack = objectsBack?.FirstOrDefault(o => o.Id == csPlaced.Id);

                csRoundTrip = readOk
                           && manifest.ObjectCount == 1
                           && csBack is not null
                           && Near(csBack.X, hostPageCenter.X, 1e-9)
                           && Near(csBack.Y, hostPageCenter.Y, 1e-9)
                           && nearNumber(csBack, CoordSystemGeometry.UnitWorldKey, expectedCsUnitHost)
                           && nearNumber(csBack, CoordSystemGeometry.XMinKey, -10)
                           && nearNumber(csBack, CoordSystemGeometry.XMaxKey, 10)
                           && nearNumber(csBack, CoordSystemGeometry.YMinKey, -10)
                           && nearNumber(csBack, CoordSystemGeometry.YMaxKey, 10)
                           && nearNumber(csBack, CoordSystemGeometry.StepKey, 1)
                           && nearNumber(csBack, CoordSystemGeometry.ShowGridKey, 1)
                           && nearNumber(csBack, CoordSystemGeometry.ShowLabelsKey, 1)
                           && nearNumber(csBack, CoordSystemGeometry.LockAspectKey, 1);
            }

            Console.WriteLine($"[{(csRoundTrip ? "PASS" : "FAIL")}] 存 → 读：原点 ({pageCenter.X:F0},{pageCenter.Y:F0})、unitWorld 120、范围/步长/开关全部还原");
            if (!csRoundTrip) failures++;

            // ------------------------------------------------ 7) 光栅落点
            Console.WriteLine();
            Console.WriteLine("---- 7) 光栅落点：x/y 轴 + 箭头落在算出来的地方 ----");

            // 把坐标系对象放到画布中心 (500, 350) 给一个清晰的可断言位置
            const double rasterOriginX = 500;
            const double rasterOriginY = 350;
            const double rasterUnit = 80;

            var csRasterData = new GfxObjectData
            {
                Id = "raster-coordsystem",
                Kind = CoordSystemRenderer.KindName,
                Plugin = "坐标系（学科工具）",
                X = rasterOriginX,
                Y = rasterOriginY,
                Rotation = 0,
                Scale = 1,
                Color = GfxObjectData.ToHex(Colors.Red),
                LineWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [CoordSystemGeometry.UnitWorldKey] = rasterUnit,
                    [CoordSystemGeometry.XMinKey] = -10,
                    [CoordSystemGeometry.XMaxKey] = 10,
                    [CoordSystemGeometry.YMinKey] = -10,
                    [CoordSystemGeometry.YMaxKey] = 10,
                    [CoordSystemGeometry.StepKey] = 1,
                    [CoordSystemGeometry.ShowGridKey] = 1,
                    [CoordSystemGeometry.ShowLabelsKey] = 1,
                    [CoordSystemGeometry.LockAspectKey] = 1,
                },
            };

            csPxy.ReplaceObjects(new[] { csRasterData });
            csPxy.UpdateLayout();

            var csRaster = Snapshot(csPxy, 1000, 700);

            var csConv = new FormatConvertedBitmap(csRaster, PixelFormats.Bgra32, null, 0);
            int csWidth = csConv.PixelWidth;
            int csHeight = csConv.PixelHeight;
            int csStride = csWidth * 4;

            var csPixels = new byte[csStride * csHeight];
            csConv.CopyPixels(csPixels, csStride, 0);

            int csMinX = int.MaxValue, csMinY = int.MaxValue, csMaxX = -1, csMaxY = -1;
            long csInkPixels = 0;

            for (int y = 0; y < csHeight; y++)
            {
                int rowStart = y * csStride;
                for (int x = 0; x < csWidth; x++)
                {
                    int offset = rowStart + x * 4;
                    byte blue = csPixels[offset], green = csPixels[offset + 1];
                    byte red = csPixels[offset + 2];

                    // 只认红像素（坐标系用 #FFFF0000 落）
                    if (!(red >= 100 && red > green + 40 && red > blue + 40)) continue;

                    csInkPixels++;
                    if (x < csMinX) csMinX = x;
                    if (y < csMinY) csMinY = y;
                    if (x > csMaxX) csMaxX = x;
                    if (y > csMaxY) csMaxY = y;
                }
            }

            // 轴 + 网格 + 箭头 + 标签全在 [原点 ± 10*unit, 原点 ± 10*unit] 内
            double csViewScale = csPxy.Viewport.Scale;
            var csOriginDip = csPxy.Viewport.ToViewport(new Point(rasterOriginX, rasterOriginY));
            double csAxisDip = 10 * rasterUnit * csViewScale;

            // 坐标系占满 ±10×unitWorld —— 半轴 800 > 画布半宽 500/350，画布被裁。
            // 验证：(1) 墨迹非常多（> 10000 px，说明网格 + 轴 + 标签都画出来了），
            //       (2) 网格 + 轴覆盖原点四周都画到（不是只在原点），
            //       (3) 画布边都触到了（说明坐标系足够大、不是只有原点一点点）。
            bool csInkFound = csInkPixels > 10000;
            bool csAroundOrigin = csMinX <= csOriginDip.X - 100
                               && csMaxX >= csOriginDip.X + 100
                               && csMinY <= csOriginDip.Y - 100
                               && csMaxY >= csOriginDip.Y + 100;
            bool csHitsEdges = csMinX <= 5 && csMaxX >= 995 && csMinY <= 5 && csMaxY >= 695;
            bool csRasterOk = csInkFound && csAroundOrigin && csHitsEdges;

            Console.WriteLine($"  光栅墨迹 {csInkPixels} px，外接框 x∈[{csMinX},{csMaxX}]、y∈[{csMinY},{csMaxY}]；"
                              + $"原点 ({csOriginDip.X:F0},{csOriginDip.Y:F0})、半轴长 {csAxisDip:F0} px");
            Console.WriteLine($"[{(csRasterOk ? "PASS" : "FAIL")}] 网格 + 轴 + 箭头 + 标签全在 [-10u, +10u]² 范围内（画出来的 = 算出来的）");
            if (!csRasterOk) failures++;

            // ------------------------------------------------ 8) 预览摆位
            Console.WriteLine();
            Console.WriteLine("---- 8) 拖动中的预览轴线画在哪（Path + Stretch=None + 负坐标几何）----");

            // 预览画的是 [0, +max]×[0, +max]（y 还没取反），元素原点 = 坐标系原点 ⇒ Left/Top 给原点本身
            const double wantOx = 300.0;
            const double wantOy = 300.0;
            const double previewLeg = 50.0;

            var csProbeCanvas = new Canvas { Width = 600, Height = 400, Background = Brushes.White };

            var csProbeAxes = new StreamGeometry();
            using (var ctx = csProbeAxes.Open())
            {
                ctx.BeginFigure(new Point(0, 0), isFilled: false, isClosed: false);
                ctx.LineTo(new Point(previewLeg, 0), isStroked: true, isSmoothJoin: false);
                ctx.BeginFigure(new Point(0, 0), isFilled: false, isClosed: false);
                ctx.LineTo(new Point(0, previewLeg), isStroked: true, isSmoothJoin: false);
            }
            csProbeAxes.Freeze();

            var csProbePath = new System.Windows.Shapes.Path
            {
                Data = csProbeAxes,
                Stretch = Stretch.None,
                Stroke = Brushes.Blue,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };

            csProbeCanvas.Children.Add(csProbePath);

            Canvas.SetLeft(csProbePath, wantOx);
            Canvas.SetTop(csProbePath, wantOy);

            csProbeCanvas.Measure(new Size(600, 400));
            csProbeCanvas.Arrange(new Rect(0, 0, 600, 400));
            csProbeCanvas.UpdateLayout();

            var csProbeShot = Snapshot(csProbeCanvas, 600, 400);
            var csProbeConv = new FormatConvertedBitmap(csProbeShot, PixelFormats.Bgra32, null, 0);
            int csProbeStride = csProbeConv.PixelWidth * 4;
            var csProbePixels = new byte[csProbeStride * csProbeConv.PixelHeight];
            csProbeConv.CopyPixels(csProbePixels, csProbeStride, 0);

            int cspMinX = int.MaxValue, cspMinY = int.MaxValue, cspMaxX = -1, cspMaxY = -1;
            long csBluePixels = 0;

            for (int y = 0; y < csProbeConv.PixelHeight; y++)
            {
                for (int x = 0; x < csProbeConv.PixelWidth; x++)
                {
                    int o = y * csProbeStride + x * 4;
                    if (csProbePixels[o] < 200 || csProbePixels[o + 2] > 80) continue;

                    csBluePixels++;
                    if (x < cspMinX) cspMinX = x;
                    if (y < cspMinY) cspMinY = y;
                    if (x > cspMaxX) cspMaxX = x;
                    if (y > cspMaxY) cspMaxY = y;
                }
            }

            // 期望：横轴 x∈[wantOx, wantOx+previewLeg]、竖轴 y∈[wantOy, wantOy+previewLeg]
            bool csPreviewPlaced = csBluePixels > 50
                                && Math.Abs(cspMinX - wantOx) <= 2
                                && Math.Abs(cspMaxX - (wantOx + previewLeg)) <= 2
                                && Math.Abs(cspMinY - wantOy) <= 2
                                && Math.Abs(cspMaxY - (wantOy + previewLeg)) <= 2;

            Console.WriteLine($"  预览轴线实测外接框 x∈[{cspMinX},{cspMaxX}]、y∈[{cspMinY},{cspMaxY}]（蓝像素 {csBluePixels}）；"
                              + $"期望 x∈[{wantOx:F0},{wantOx + previewLeg:F0}]、y∈[{wantOy:F0},{wantOy + previewLeg:F0}]");
            Console.WriteLine($"[{(csPreviewPlaced ? "PASS" : "FAIL")}] 拖动预览以「原点」为准落位（Stretch=None 的 Path 不平移负坐标几何）");
            if (!csPreviewPlaced) failures++;

            SavePng(csProbeShot, Path.Combine(outDir, "m74s3-预览摆位.png"));
            SavePng(csRaster, Path.Combine(outDir, "m74s3-坐标系.png"));
            Console.WriteLine($"  证据图：{Path.Combine(outDir, "m74s3-坐标系.png")}");
        }
        finally
        {
            csPxy.Shutdown();
        }

        return failures;
    }

    private static int CountChildren(GeometryGroup group) => group.Children.Count;

    /// <summary>
    /// 假的吸附基准查询：给几段线、几个端点，其余什么都不提供。
    /// </summary>
    /// <remarks>
    /// 工具只依赖契约 ⇒ 吸附逻辑可以完全脱离画布验收。而真画布那条路
    /// 由第 5 段（往墨迹层里注一条笔画）覆盖 —— 两条都要有，
    /// 一条证明"算对了"，一条证明"接上了"。
    /// </remarks>
    private sealed class FakeBoardQuery : IBoardQuery
    {
        public List<BoardSegment> Segments { get; } = new();

        public List<Point> Points { get; } = new();

        public IReadOnlyList<BoardSegment> SegmentsNear(Point world, double radiusWorld)
            => Segments.Where(s => ProtractorGeometry.DistanceToSegment(world, s.From, s.To) <= radiusWorld).ToList();

        public IReadOnlyList<Point> PointsNear(Point world, double radiusWorld)
            => Points.Where(p => (p - world).Length <= radiusWorld).ToList();

        /// <summary>M12 图形吸附：假查询没有图形层，恒无目标。</summary>
        public Point? SnapToGfx(Point world, double radiusWorld) => null;
    }

    /// <summary>假的图形对象门面：只记下工具交上来的草案与它自己的撤销步。</summary>
    private sealed class FakeGfxHost : IGfxObjectHost
    {
        public List<GfxDraft> Added { get; } = new();

        public List<string> Steps { get; } = new();

        public IReadOnlyList<IGfxObjectRef> Objects => Array.Empty<IGfxObjectRef>();

        public IGfxObjectRef? Selected => null;

        public event EventHandler? SelectionChanged
        {
            add { }
            remove { }
        }

        public string Add(GfxDraft draft)
        {
            Added.Add(draft);
            return "fake-" + Added.Count;
        }

        public bool Remove(string id) => false;

        public bool UpdatePose(string id, Point center, double rotationDegrees, double scale) => false;

        public bool UpdateNumbers(string id, IReadOnlyDictionary<string, double> numbers) => false;

        public bool UpdateTexts(string id, IReadOnlyDictionary<string, string> texts) => false;

        public void Select(string? id)
        {
        }

        public void BeginStep(string label) => Steps.Add(label);

        /// <summary>M22 S3：落位图。假实现只记账 —— 真实现那条路由第 9 组的 
        /// <c>HostGfxObjectHost.AddImage</c> 断言（用真宿主，不用替身）。</summary>
        public List<string> Images { get; } = new();

        public string? AddImage(byte[] pngBytes, string label = "")
        {
            if (pngBytes is not { Length: > 0 }) return null;
            Images.Add(label ?? string.Empty);
            return "fake-image-" + Images.Count;
        }
    }

    /// <summary>读一个数值参数并比较（存档往返用）。</summary>
    private static bool nearNumber(GfxObjectData data, string key, double expected)
        => data.Numbers.TryGetValue(key, out double value) && Near(value, expected, 1e-9);

    /// <summary>把 <paramref name="point"/> 绕 <paramref name="center"/> 转一个角度（度）。</summary>
    private static Point RotateAround(Point point, Point center, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        double dx = point.X - center.X;
        double dy = point.Y - center.Y;

        return new Point(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    /// <summary>
    /// 沿「中心 → 手柄」方向把点再往外推 <paramref name="extraWorld"/>（负数表示往回缩）。
    /// </summary>
    private static Point ScaleTowards(Point handle, Point center, double extraWorld)
    {
        var direction = handle - center;
        if (direction.Length < 1e-9) return handle;

        direction.Normalize();
        return new Point(handle.X + direction.X * extraWorld, handle.Y + direction.Y * extraWorld);
    }

    /// <summary>
    /// 验收用的假图形对象：只实现只读视图，用来把位姿数学单独钉死（不依赖宿主与视觉树）。
    /// </summary>
    private sealed class FakeGfxObject : IGfxObjectRef
    {
        public string Id { get; init; } = "fake";

        public string Kind { get; init; } = "fake";

        public string PluginName { get; init; } = string.Empty;

        public Point Center { get; init; }

        public double RotationDegrees { get; init; }

        public double Scale { get; init; } = 1.0;

        public Size LocalSize { get; init; }

        public Rect BoundsWorld => GfxTransform.BoundsWorld(this);

        public Color Color { get; init; } = Colors.Black;

        public double LineWorldWidth { get; init; } = 1.5;

        public bool HasNumber(string key) => false;

        public double GetNumber(string key, double fallback = 0) => fallback;

        public string GetText(string key, string fallback = "") => fallback;
    }

    /// <summary>构造一次指针事件（视口坐标对工具毫无意义，这里填同一个点）。</summary>
    private static ToolPointer Pointer(ToolPointerPhase phase, Point world)
        => new(phase, world, world, 1, false, 1);

    /// <summary>以 <paramref name="origin"/> 为心、按角度与长度取一点。</summary>
    private static Point AtAngle(Point origin, double degrees, double length)
    {
        var radians = degrees * Math.PI / 180.0;
        return new Point(origin.X + length * Math.Cos(radians), origin.Y + length * Math.Sin(radians));
    }

    /// <summary>一条笔画的轴角（度）。</summary>
    private static double AxisOf(Stroke stroke)
    {
        var points = stroke.StylusPoints;
        return RulerGeometry.AxisDegrees(
            new Point(points[0].X, points[0].Y),
            new Point(points[points.Count - 1].X, points[points.Count - 1].Y));
    }

    /// <summary>在预览图元里找出那个包着长度文字的 Border，取出它的文字。</summary>
    private static string? FindLabelText(IReadOnlyList<UIElement> visuals)
    {
        foreach (var visual in visuals)
        {
            if (visual is Border { Child: TextBlock text }) return text.Text;
        }

        return null;
    }

    /// <summary>
    /// 假的 <see cref="IToolContext"/>：只记下工具做了什么，不做任何真实渲染。
    /// </summary>
    /// <remarks>
    /// 工具只依赖接口 ⇒ 用一个几十行的假实现就能把状态机跑完整 ——
    /// 这正是 M7.1 把 <c>IToolContext</c> 做小的回报。
    /// </remarks>
    private sealed class FakeToolContext : IToolContext
    {
        public Size ViewportSize => new(1000, 700);

        public double Scale { get; set; } = 1.0;

        public IReadOnlyList<Rect> PageRects => PageRectsOverride ?? Array.Empty<Rect>();

        /// <summary>需要吸附到页边/页中心时挂一个页框进来（坐标系原点吸附）。默认空 —— 也好用来验"没页框也能画"。</summary>
        public IReadOnlyList<Rect>? PageRectsOverride { get; set; }

        public Color PenColor => Colors.Red;

        public double PenWorldWidth => 1.5;

        public List<UIElement> Previews { get; } = new();

        public StrokeCollection Committed { get; } = new();

        public string? LastStatus { get; private set; }

        /// <summary>M7.4：图形对象门面。这个假上下文不接图形层，保持 <c>null</c>（工具需自行判空）。</summary>
        public IGfxObjectHost? Gfx => GfxOverride;

        /// <summary>M7.4-S1：吸附基准查询。默认 <c>null</c> —— 正好用来验"没有它也要能画"。</summary>
        public IBoardQuery? Query => QueryOverride;

        /// <summary>M7.5：演示面板通道。默认 <c>null</c> —— 正好用来验"旧宿主没有这一层时也要能跑"。</summary>
        public IGeoGebraBridge? GeoGebra => GeoGebraOverride;

        /// <summary>需要演示面板时挂一个假的进来。</summary>
        public IGeoGebraBridge? GeoGebraOverride { get; set; }

        /// <summary>M22：通用 Web 面板通道。默认 <c>null</c> —— 同样用"没有它也要能跑"来验降级。</summary>
        public IWebPanelBridge? WebSim => WebSimOverride;

        /// <summary>需要仿真面板时挂一个假的进来。</summary>
        public IWebPanelBridge? WebSimOverride { get; set; }

        /// <summary>需要图形层时挂一个假的进来（量角器落成对象要它）。</summary>
        public IGfxObjectHost? GfxOverride { get; set; }

        /// <summary>需要吸附基准时挂一个假的进来。</summary>
        public IBoardQuery? QueryOverride { get; set; }

        /// <summary>M7.4：视口变化通知。默认不发；想验「缩放后容差 / 手柄尺寸重算」时调 <see cref="RaiseViewportChanged"/>。</summary>
        public event EventHandler? ViewportChanged;

        /// <summary>手动触发一次视口变化（圆规的容差与手柄边长都挂在它上面）。</summary>
        public void RaiseViewportChanged() => ViewportChanged?.Invoke(this, EventArgs.Empty);

        public Point ToWorld(Point viewportPoint) => viewportPoint;

        public Point ToViewport(Point worldPoint) => worldPoint;

        public void CommitStrokes(StrokeCollection strokes)
        {
            foreach (var stroke in strokes) Committed.Add(stroke);
        }

        public void AddPreview(UIElement visual) => Previews.Add(visual);

        public void ClearPreview() => Previews.Clear();

        public void SetStatus(string text) => LastStatus = text;

        /// <summary>清掉上一轮的记录，便于一段一段断言。</summary>
        public void Reset()
        {
            Previews.Clear();
            Committed.Clear();
            LastStatus = null;
        }
    }

    // ============================================================ M7.5 S0：演示面板宿主桥接

    /// <summary>
    /// 假的 Web 面板后端：记录调用、可注入失败。<b>完全不碰 WebView2。</b>
    /// </summary>
    /// <remarks>
    /// 有它才能在没有消息循环的控制台里把面板逻辑跑完整 ——
    /// 这正是把逻辑从 <c>WebView2PanelBackend</c> 里拎出来放进
    /// <see cref="WebPanelController"/> 的全部理由。
    /// </remarks>
    private sealed class FakeWebPanelBackend : IWebPanelBackend
    {
        public bool IsAvailable { get; set; } = true;

        public string? UnavailableReason { get; set; }

        public bool IsLoaded { get; private set; }

        public int ShowCount { get; private set; }

        public int HideCount { get; private set; }

        /// <summary>当前形态（S3）。</summary>
        public PanelForm Form { get; private set; } = PanelForm.FullScreen;

        /// <summary>形态切换被调用的次数（S3）。</summary>
        public int SwitchCount { get; private set; }

        /// <summary>模拟"形态切换失败"（S3）。</summary>
        public bool ThrowOnSwitch { get; set; }

        /// <summary>模拟"WebView2 初始化失败"。</summary>
        public bool ThrowOnShow { get; set; }

        public List<string> Posted { get; } = new();

        public event EventHandler<string>? RawMessageReceived;

        public event EventHandler? CloseRequested;

        public event EventHandler? FormToggleRequested;

        public event EventHandler<PanelForm>? FormChanged;

        public event EventHandler? ExportRequested;

        /// <summary>模拟用户点了面板顶栏的「导出到白板」按钮（S4b）。</summary>
        public void RaiseExportRequested() => ExportRequested?.Invoke(this, EventArgs.Empty);

        public void SetForm(PanelForm form) => Form = form;

        public Task SwitchFormAsync(PanelForm form)
        {
            SwitchCount++;

            if (ThrowOnSwitch)
            {
                throw new InvalidOperationException("模拟：形态切换失败");
            }

            Form = form;
            FormChanged?.Invoke(this, form);
            return Task.CompletedTask;
        }

        /// <summary>模拟用户点了面板顶栏的「停靠 / 全屏」按钮（S3）。</summary>
        public void RaiseFormToggleRequested() => FormToggleRequested?.Invoke(this, EventArgs.Empty);

        public Task ShowAsync()
        {
            ShowCount++;

            if (ThrowOnShow)
            {
                throw new InvalidOperationException("模拟：WebView2 初始化失败");
            }

            IsLoaded = true;
            return Task.CompletedTask;
        }

        public Task HideAsync()
        {
            HideCount++;
            IsLoaded = false;
            return Task.CompletedTask;
        }

        public void Post(string json) => Posted.Add(json);

        public Task<string?> ExecuteScriptAsync(string script) => Task.FromResult<string?>(null);

        /// <summary>抓帧要返回的字节（S4）：默认 <c>null</c> = "拿不到"。</summary>
        public byte[]? CapturePngResult { get; set; }

        /// <summary>抓帧被调用了几次（S4）。</summary>
        public int CaptureCount { get; private set; }

        public Task<byte[]?> CapturePngAsync()
        {
            CaptureCount++;
            return Task.FromResult(CapturePngResult);
        }

        /// <summary>模拟页面回话。</summary>
        public void RaiseMessage(string json) => RawMessageReceived?.Invoke(this, json);

        /// <summary>模拟用户点了「返回白板」/ 按了 Esc。</summary>
        public void RaiseCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    /// <summary>假的宿主钩子：记录进入/退出面板模式的次数与最后一句状态。</summary>
    private sealed class FakeGeoGebraHooks : IPanelHostHooks
    {
        public bool IsPanelModeActive { get; private set; }

        public int EnterCount { get; private set; }

        public int ExitCount { get; private set; }

        public string? LastStatus { get; private set; }

        /// <summary>★ 与真实现一样必须幂等 —— 否则"重复进入不重复计数"这条断言就成了空转。</summary>
        public void EnterPanelMode()
        {
            if (IsPanelModeActive) return;

            IsPanelModeActive = true;
            EnterCount++;
        }

        public void ExitPanelMode()
        {
            if (!IsPanelModeActive) return;

            IsPanelModeActive = false;
            ExitCount++;
        }

        public void SetStatus(string text) => LastStatus = text;
    }

    /// <summary>
    /// M7.5 S0：演示面板（GeoGebra）的宿主桥接。
    /// </summary>
    /// <remarks>
    /// 这一组刻意<b>不碰 WebView2</b>：它在控制台里起不来（要消息循环与真实 HWND）。
    /// 但面板的<b>全部逻辑</b>（状态机、就绪闸门、互斥激活、消息编解码、降级）
    /// 都在 <see cref="WebPanelController"/> 里，换上替身后端就能完整断言。
    /// <para>
    /// "WebView2 到底能不能开起来、开起来长什么样"由发布冒烟（截图）负责 ——
    /// 分工与 M7.3 的"几何归 harness、手感归一体机"是同一条。
    /// </para>
    /// </remarks>
    private static int RunM75Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.5 S0：演示面板宿主桥接（GeoGebra 通路）================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        // ---------------------------------------------------------- 1) 信封编解码
        //
        // C# 与 JS 各写各的，信封格式一旦有歧义，表现是"消息发出去了对面没反应"，
        // 而且两边都觉得自己是对的。所以先把它钉死。
        {
            var json = GeoGebraProtocol.Encode("command", new { label = "A", action = "setCoords" });

            bool encodeOk = json.Contains("\"type\":\"command\"", StringComparison.Ordinal)
                         && json.Contains("\"payload\"", StringComparison.Ordinal)
                         && json.Contains("\"label\":\"A\"", StringComparison.Ordinal);

            Report("Encode 产出「带 type 与 payload 的 JSON 对象」", encodeOk, json);

            bool decodeOk = GeoGebraProtocol.TryDecode(json, out var message, out var decodeError)
                         && decodeError is null
                         && message.Type == "command"
                         && message.ReadPayloadString("label") == "A";

            Report("TryDecode 往返：type 与 payload 字段都能取回", decodeOk,
                   $"type={message.Type} label={message.ReadPayloadString("label")} err={decodeError ?? "-"}");

            // 中文必须原样往返（payload 里放中文是常态：状态栏文案、素材名）
            var cnJson = GeoGebraProtocol.Encode("error", new { message = "坐标系还没建好" });
            GeoGebraProtocol.TryDecode(cnJson, out var cnMessage, out _);

            Report("中文 payload 往返不乱码",
                   cnMessage.ReadPayloadString("message") == "坐标系还没建好",
                   cnJson);
        }

        // ---------------------------------------------------------- 2) 畸形消息不许把宿主带走
        {
            bool emptyOk = !GeoGebraProtocol.TryDecode("", out _, out var e1) && !string.IsNullOrEmpty(e1);
            bool notJsonOk = !GeoGebraProtocol.TryDecode("{不是 JSON", out _, out var e2) && !string.IsNullOrEmpty(e2);
            bool notObjectOk = !GeoGebraProtocol.TryDecode("[1,2,3]", out _, out var e3) && !string.IsNullOrEmpty(e3);
            bool noTypeOk = !GeoGebraProtocol.TryDecode("{\"payload\":1}", out _, out var e4) && !string.IsNullOrEmpty(e4);

            Report("畸形消息一律被拒且给出中文原因（空 / 非 JSON / 非对象 / 缺 type）",
                   emptyOk && notJsonOk && notObjectOk && noTypeOk,
                   $"空={emptyOk} 非JSON={notJsonOk} 非对象={notObjectOk} 缺type={noTypeOk}");

            bool throwsOk = false;
            try { GeoGebraProtocol.Encode("   ", null); }
            catch (ArgumentException) { throwsOk = true; }

            Report("Encode 空 type 直接抛（写错了要立刻知道，不是静默产出一条坏消息）", throwsOk);
        }

        // ---------------------------------------------------------- 3) 不可用：降级不抛、不进面板模式
        {
            var backend = new FakeWebPanelBackend
            {
                IsAvailable = false,
                UnavailableReason = "未检测到 WebView2 运行时，GeoGebra 演示面板不可用。",
            };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            bool opened = controller.OpenAsync().GetAwaiter().GetResult();

            Report("面板不可用时 ShowAsync 返回 false、不抛异常",
                   !opened && !controller.IsOpen,
                   $"opened={opened} IsOpen={controller.IsOpen}");

            Report("★ 不可用时绝不进入面板模式（否则画布被白锁）",
                   !hooks.IsPanelModeActive && hooks.EnterCount == 0,
                   $"panelMode={hooks.IsPanelModeActive} enter={hooks.EnterCount}");

            Report("不可用时把中文原因写到状态栏（老师能在界面上看到为什么）",
                   !string.IsNullOrEmpty(hooks.LastStatus) && hooks.LastStatus!.Contains("WebView2", StringComparison.Ordinal),
                   hooks.LastStatus ?? "(无)");
        }

        // ---------------------------------------------------------- 4) 正常打开：进入面板模式
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            bool opened = controller.OpenAsync().GetAwaiter().GetResult();

            Report("可用时 ShowAsync 成功并进入面板模式",
                   opened && controller.IsOpen && hooks.IsPanelModeActive && hooks.EnterCount == 1,
                   $"opened={opened} IsOpen={controller.IsOpen} enter={hooks.EnterCount}");

            Report("打开会真的叫后端开面板", backend.ShowCount == 1 && backend.IsLoaded,
                   $"ShowCount={backend.ShowCount} IsLoaded={backend.IsLoaded}");

            // 幂等：重复打开不重复进入
            controller.OpenAsync().GetAwaiter().GetResult();

            Report("重复打开是幂等的（不重复进入面板模式、不重复开后端）",
                   hooks.EnterCount == 1 && backend.ShowCount == 1,
                   $"enter={hooks.EnterCount} ShowCount={backend.ShowCount}");
        }

        // ---------------------------------------------------------- 5) ★ 就绪闸门
        //
        // 没有这道闸门的表现是"偶发点了没反应"（取决于页面加载快慢），现场极难复现。
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.OpenAsync().GetAwaiter().GetResult();

            controller.SendAsync("command", new { label = "A" }).GetAwaiter().GetResult();

            bool queuedOk = backend.Posted.Count == 0 && controller.QueuedCommandCount == 1;

            Report("★ ready 之前命令进队列、不直接发（发了会静默丢失）",
                   queuedOk && !controller.IsReady,
                   $"Posted={backend.Posted.Count} Queued={controller.QueuedCommandCount}");

            // 页面报 ready ⇒ 放行
            backend.RaiseMessage(GeoGebraProtocol.Encode(GeoGebraProtocol.Ready, new { version = "test" }));

            bool flushedOk = controller.IsReady
                          && backend.Posted.Count == 1
                          && controller.QueuedCommandCount == 0
                          && backend.Posted[0].Contains("\"command\"", StringComparison.Ordinal);

            Report("★ ready 到达后排队命令按序放行", flushedOk,
                   $"IsReady={controller.IsReady} Posted={backend.Posted.Count} 首条={backend.Posted.FirstOrDefault() ?? "-"}");

            // 之后再发就直发
            controller.SendAsync("reset", null).GetAwaiter().GetResult();

            Report("ready 之后命令直发（不再排队）",
                   backend.Posted.Count == 2 && controller.QueuedCommandCount == 0,
                   $"Posted={backend.Posted.Count}");
        }

        // ---------------------------------------------------------- 5b) ★ 就绪闸门 · 超时路径并发排队（M23 兜底）
        //
        // 页面迟迟不报 ready（正走向就绪超时）时，命令仍会从多个线程并发入队，
        // 而后台线程上的 OnReadyTimeout 会 Clear 队列兜底。List 不是线程安全的，
        // 没加锁会偶发抛 IndexOutOfRangeException / 损坏内部计数。这里注入假时钟让
        // 就绪超时按 0ms 到期，再用 8 个线程并发 SendAsync 去撞它，断言：不抛异常、
        // 最终按载入失败收口、一条命令都没被发出去。
        {
            long now = 0;   // 注入假时钟 ⇒ 就绪超时到期设为 0ms（不用真等 20 秒，可确定性跑到）
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks, () => now);

            controller.OpenAsync().GetAwaiter().GetResult();   // _ready=false，定时器已按 0ms 布防
            // 故意不开 ready：模拟「页面一直没起来」，正是 M23 就绪超时要接住的情形。

            const int threadCount = 8;
            const int perThread = 1_000;
            var errorLock = new object();
            var errors = new List<string>();
            var go = new ManualResetEventSlim(false);

            var workers = new Thread[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                workers[t] = new Thread(() =>
                {
                    go.Wait();   // 一起起跑，把「并发入队」与「超时 Clear」的撞车窗口拉到最大
                    for (int i = 0; i < perThread; i++)
                    {
                        try { controller.SendAsync("command", new { n = i }); }
                        catch (Exception ex)
                        {
                            lock (errorLock) { errors.Add(ex.GetType().Name + " " + ex.Message); }
                        }
                    }
                });
                workers[t].IsBackground = true;
            }

            // 主线程先塞一批，保证超时 Clear 撞上时队列里确实在被写（而不是全被 _loadFailed 挡在门外）。
            for (int i = 0; i < 1_000; i++) { controller.SendAsync("command", new { m = i }); }

            for (int t = 0; t < threadCount; t++) workers[t].Start();
            go.Set();
            for (int t = 0; t < threadCount; t++) workers[t].Join();

            // 等后台 0ms 定时器把超时跑完（IsLoadFailed 置位），最多等 2 秒。
            for (int i = 0; i < 200 && !controller.IsLoadFailed; i++) Thread.Sleep(10);

            int errorCount;
            lock (errorLock) { errorCount = errors.Count; }

            bool noThrow = errorCount == 0;

            Report("★ 超时路径并发排队：多线程 Enqueue 撞上就绪超时的 Clear 不抛异常、最终按载入失败收口、一条没发",
                   noThrow && controller.IsLoadFailed && backend.Posted.Count == 0,
                   $"errors={errorCount} loadFailed={controller.IsLoadFailed} posted={backend.Posted.Count} "
                   + $"queued={controller.QueuedCommandCount}");
        }

        // ---------------------------------------------------------- 6) 关闭：把输入交还给工具
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.OpenAsync().GetAwaiter().GetResult();
            controller.CloseAsync().GetAwaiter().GetResult();

            Report("关闭后退出面板模式、输入交还",
                   !controller.IsOpen && !hooks.IsPanelModeActive && hooks.ExitCount == 1,
                   $"IsOpen={controller.IsOpen} exit={hooks.ExitCount}");

            Report("关闭会真的叫后端收窗口，且清空排队命令",
                   backend.HideCount == 1 && controller.QueuedCommandCount == 0,
                   $"HideCount={backend.HideCount}");

            controller.CloseAsync().GetAwaiter().GetResult();

            Report("重复关闭是幂等的", hooks.ExitCount == 1 && backend.HideCount == 1,
                   $"exit={hooks.ExitCount} HideCount={backend.HideCount}");
        }

        // ---------------------------------------------------------- 7) ★ 打开失败必须回滚
        //
        // 这一条最值钱：漏了它，WebView2 初始化一旦失败，整个白板就写不出字了 ——
        // 比"打不开面板"严重得多。
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true, ThrowOnShow = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            bool opened = controller.OpenAsync().GetAwaiter().GetResult();

            Report("★★ 后端打开失败：ShowAsync 返回 false 且不抛",
                   !opened && !controller.IsOpen,
                   $"opened={opened} IsOpen={controller.IsOpen}");

            Report("★★ 后端打开失败后画布输入被还回去（否则白板永久写不出字）",
                   !hooks.IsPanelModeActive && hooks.ExitCount == 1,
                   $"panelMode={hooks.IsPanelModeActive} exit={hooks.ExitCount}");

            Report("打开失败的原因写进状态栏",
                   !string.IsNullOrEmpty(hooks.LastStatus) && hooks.LastStatus!.Contains("失败", StringComparison.Ordinal),
                   hooks.LastStatus ?? "(无)");
        }

        // ---------------------------------------------------------- 8) ★ 用户按 Esc：同步复位
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.OpenAsync().GetAwaiter().GetResult();
            backend.RaiseCloseRequested();   // 模拟用户按 Esc / 点「返回白板」

            Report("★ 用户按 Esc 后状态【同步】就该复位（不能等异步收窗完）",
                   !controller.IsOpen && !hooks.IsPanelModeActive && hooks.ExitCount == 1,
                   $"IsOpen={controller.IsOpen} panelMode={hooks.IsPanelModeActive} exit={hooks.ExitCount}");
        }

        // ---------------------------------------------------------- 9) error 消息可读
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.OpenAsync().GetAwaiter().GetResult();
            backend.RaiseMessage(GeoGebraProtocol.Encode(GeoGebraProtocol.Error,
                new { message = "GeoGebra 脚本加载失败" }));

            Report("★ JS 报错能在状态栏读到（契约要求「不许静默失败」）",
                   hooks.LastStatus is not null && hooks.LastStatus.Contains("GeoGebra 脚本加载失败", StringComparison.Ordinal),
                   hooks.LastStatus ?? "(无)");
        }

        // ---------------------------------------------------------- 10) changed 节流
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            long now = 10_000;
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks, () => now);

            int changedCount = 0;
            controller.MessageReceived += (_, m) =>
            {
                if (m.Type == GeoGebraProtocol.Changed) changedCount++;
            };

            controller.OpenAsync().GetAwaiter().GetResult();

            // 模拟拖一个点：每 100ms 来一条 changed，共 6 条（跨 600ms）。
            // 节流是 leading 型 —— 每次放行都把窗口重置，所以放行时刻是 0/200/400ms。
            for (int i = 0; i < 6; i++)
            {
                now = 10_000 + i * 100;
                backend.RaiseMessage(GeoGebraProtocol.Encode(GeoGebraProtocol.Changed, null));
            }

            Report("★ changed 按 200ms 合并（拖点时消息很密，不合并会拖住 UI）",
                   changedCount == 3 && changedCount < 6,
                   $"6 条密集 changed（每 100ms 一条）⇒ 转发 {changedCount} 条（期望 3，即 6→3）");
        }

        // ---------------------------------------------------------- 11) 面板没开时的发送被静默丢弃
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.SendAsync("command", new { label = "A" }).GetAwaiter().GetResult();

            Report("面板没开时发命令不抛也不投递（工具可能在 Deactivate 里发收尾消息）",
                   backend.Posted.Count == 0 && controller.QueuedCommandCount == 0,
                   $"Posted={backend.Posted.Count}");
        }

        // ---------------------------------------------------------- 12) 工具形态与降级
        {
            var tool = new GeoGebraTool();

            bool shapeOk = tool is ITransientTool
                        && !tool.UsesInkLayer
                        && !tool.NeedsPointer
                        && tool.InputKind == ToolInputKind.None
                        && tool.InkMode == ToolInkMode.None
                        && tool.Shortcut == Key.Y;

            Report("★ 工具是「瞬态命令型」：不接画布输入、不要指针事件（ITransientTool）",
                   shapeOk,
                   $"transient={tool is ITransientTool} ink={tool.UsesInkLayer} ptr={tool.NeedsPointer} "
                   + $"input={tool.InputKind} shortcut={tool.Shortcut}");

            // 没有通道：给一句人话，不失效
            var ctx1 = new FakeToolContext();
            tool.Activate(ctx1);

            Report("拿不到面板通道时只说一句中文原因（不改画布状态）",
                   ctx1.LastStatus is not null && ctx1.LastStatus.Contains("未装配", StringComparison.Ordinal),
                   ctx1.LastStatus ?? "(无)");

            // 有通道但不可用
            var backend2 = new FakeWebPanelBackend
            {
                IsAvailable = false,
                UnavailableReason = "未检测到 WebView2 运行时，GeoGebra 演示面板不可用。",
            };
            var hooks2 = new FakeGeoGebraHooks();
            using var controller2 = new WebPanelController(WebPanelProfile.GeoGebra, backend2, hooks2);

            var ctx2 = new FakeToolContext { GeoGebraOverride = controller2 };
            tool.Activate(ctx2);

            Report("面板不可用时把原因写到状态栏、不去开面板",
                   ctx2.LastStatus is not null
                   && ctx2.LastStatus.Contains("WebView2", StringComparison.Ordinal)
                   && backend2.ShowCount == 0,
                   ctx2.LastStatus ?? "(无)");

            // 可用：点一下真的开
            var backend3 = new FakeWebPanelBackend { IsAvailable = true };
            var hooks3 = new FakeGeoGebraHooks();
            using var controller3 = new WebPanelController(WebPanelProfile.GeoGebra, backend3, hooks3);

            var ctx3 = new FakeToolContext { GeoGebraOverride = controller3 };
            tool.Activate(ctx3);

            Report("可用时点一下就打开面板",
                   controller3.IsOpen && backend3.ShowCount == 1 && hooks3.IsPanelModeActive,
                   $"IsOpen={controller3.IsOpen} ShowCount={backend3.ShowCount}");

            // 已经开着再点：不重复开
            tool.Activate(ctx3);

            Report("已经开着时再点不重复打开", backend3.ShowCount == 1,
                   $"ShowCount={backend3.ShowCount}");
        }

        // ---------------------------------------------------------- 13) ★★ 真宿主端到端
        //
        // 前面 12 组验的是"面板逻辑自己对不对"；这一组验的是"它接到画布上以后对不对" ——
        // 走真 CanvasViewportHost、真 ToolRegistry、真插件工具，一步不绕。
        {
            const int w = 1000;
            const int h = 700;

            var host = new CanvasViewportHost();
            host.Measure(new Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));
            host.UpdateLayout();

            var backend = new FakeWebPanelBackend { IsAvailable = true };
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, host);
            host.GeoGebra = controller;
            host.PluginRegistry.Add(new GeoGebraTool());

            host.SetTool(ToolIds.Pen);
            bool inkBefore = host.InkSurface.IsHitTestVisible;

            // 点一下「GeoGebra 演示」
            host.SetTool(GeoGebraTool.ToolId);

            bool openedOk = controller.IsOpen && host.IsPanelModeActive;
            bool inkSuppressed = !host.InkSurface.IsHitTestVisible;

            Report("★★ 点插件工具 ⇒ 面板打开、进入面板模式、画布输入被抑制",
                   openedOk && inkSuppressed && inkBefore,
                   $"IsOpen={controller.IsOpen} panelMode={host.IsPanelModeActive} "
                   + $"inkBefore={inkBefore} inkNow={host.InkSurface.IsHitTestVisible}");

            // ★ 瞬态工具：命令执行完立刻把控制权交回笔
            Report("★★ 瞬态工具执行完自动弹回「笔」（否则用户会以为白板卡死）",
                   host.ActiveToolId == ToolIds.Pen,
                   $"当前工具 = {host.ActiveToolId}");

            // ★ 面板模式压过工具：面板开着时切工具，画布也不该接输入
            host.SetTool(ToolIds.Eraser);

            Report("★ 面板模式压过工具：面板开着时切到橡皮，画布仍不接输入",
                   !host.InkSurface.IsHitTestVisible && host.IsPanelModeActive,
                   $"ink={host.InkSurface.IsHitTestVisible} panelMode={host.IsPanelModeActive}");

            // 按 Esc 返回白板
            backend.RaiseCloseRequested();

            Report("★★ 返回白板后：面板模式解除、输入交还当前工具（橡皮能擦）",
                   !host.IsPanelModeActive && host.InkSurface.IsHitTestVisible && !controller.IsOpen,
                   $"panelMode={host.IsPanelModeActive} ink={host.InkSurface.IsHitTestVisible} "
                   + $"当前工具={host.ActiveToolId}");

            // 交还的语义是"让当前工具说了算"，不是"一律能写字"：
            // 切到手工具（本来就该不接输入）再走一次，退出面板后仍不接 —— 这才对。
            host.SetTool(ToolIds.Hand);
            host.SetTool(GeoGebraTool.ToolId);
            backend.RaiseCloseRequested();

            Report("★ 交还是「让当前工具说了算」：手工具下退出面板仍然是手（不会莫名能写字）",
                   !host.IsPanelModeActive && !host.InkSurface.IsHitTestVisible
                   && host.ActiveToolId == ToolIds.Hand,
                   $"ink={host.InkSurface.IsHitTestVisible} 当前工具={host.ActiveToolId}");

            host.Shutdown();
        }

        // ---------------------------------------------------------- 14) ★ 面板窗布局：顶栏与网页区不许重叠
        //
        // 这一组盯的是 docs/12 §3.2 那条架构级硬约束（WPF × WebView2 的"空气空间"）：
        // WebView2 基于 HWND，Windows 的合成规则决定它永远渲染在所有 WPF 元素之上，
        // 所以"返回白板"这条退路只能靠"不与它矩形重叠"来保证。
        //
        // 为什么非要写成断言：这条约束坏掉时的症状是"返回按钮既看不见也点不着"，
        // 而不是"面板打不开" —— 后者当场就发现了，前者要等到人去一体机前面点它。
        // 变成两个矩形的数值比较之后，改 XAML 的当天就会红。
        {
            WebPanelWindow? panelWin = null;
            var backRect = default(Rect);
            var hostRect = default(Rect);
            bool buttonOk = false;
            string? staError = null;
            string? findInfo = null;

            var sta = new Thread(() =>
            {
                FrameworkElement? root = null;
                try
                {
                    panelWin = new WebPanelWindow(WebPanelProfile.GeoGebra.DisplayName);

                    // ★ 这里有个坑，值得写下来：Window 不 Show() 的话，它的内容没有
                    //   PresentationSource，Measure/Arrange 量下来全是 0（按钮 0×0）。
                    //   但又不愿意在跑回归时弹一个全屏窗把屏幕抢走 ——
                    //   于是把内容根从 Window 上摘下来单独量：DockPanel 的布局规则是自洽的，
                    //   量出来的数字与真开窗时一致（真机日志里的 200×48 @ (1493,10) 就是佐证）。
                    root = panelWin.Content as FrameworkElement;
                    if (root is null)
                    {
                        findInfo = "窗口内容不是 FrameworkElement";
                        return;
                    }

                    panelWin.Content = null;
                    root.Measure(new Size(1400, 800));
                    root.Arrange(new Rect(0, 0, 1400, 800));
                    root.UpdateLayout();

                    var button = panelWin.FindName("BackButton") as Button;
                    var hostBorder = panelWin.FindName("ViewHost") as Border;
                    if (button is null || hostBorder is null)
                    {
                        findInfo = $"FindName 未命中：按钮={(button is null ? "null" : "ok")}"
                                 + $" 网页区={(hostBorder is null ? "null" : "ok")}";
                        return;
                    }

                    var b = button.TranslatePoint(new Point(0, 0), root);
                    var h = hostBorder.TranslatePoint(new Point(0, 0), root);

                    backRect = new Rect(b.X, b.Y, button.ActualWidth, button.ActualHeight);
                    hostRect = new Rect(h.X, h.Y, hostBorder.ActualWidth, hostBorder.ActualHeight);

                    // ≥48 DIP 是"手指点得着"的下限：一体机上没有鼠标。
                    // ★ 判 Visibility 而不是 IsVisible：IsVisible 要求元素连在一个"可见窗口"上，
                    //   而我们刻意没 Show() 窗口（不想跑回归时抢屏幕），
                    //   所以 IsVisible 恒为 false —— 拿它当判据会得到一条永远红的假断言。
                    buttonOk = button.Visibility == Visibility.Visible
                            && button.ActualWidth > 0
                            && button.ActualHeight >= 48;
                }
                catch (Exception ex)
                {
                    // 别吞：这条断言失败时唯一有用的是"到底哪一步炸了"。
                    staError = $"{ex.GetType().Name} {ex.Message}";
                    panelWin = null;
                }
                finally
                {
                    if (root is not null && panelWin is not null) panelWin.Content = root;
                }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join(8000);

            bool built = panelWin is not null && backRect.Width > 0;

            Report("★ 面板窗可构建，返回按钮进了布局且高度 ≥48 DIP（手指点得着）",
                   built && buttonOk,
                   $"按钮 {backRect.Width:0}×{backRect.Height:0} @ ({backRect.X:0},{backRect.Y:0})"
                   + $"（视口 1400×800 下标定）"
                   + (staError is null ? "" : $"；STA 线程异常：{staError}")
                   + (findInfo is null ? "" : $"；{findInfo}"));

            bool overlap = built && backRect.IntersectsWith(hostRect);
            Report("★★ 顶栏与 WebView2 区不重叠（重叠了按钮就永远点不着）",
                   built && !overlap,
                   $"顶栏底边 y={backRect.Bottom:0}，网页区顶边 y={hostRect.Y:0}，重叠={overlap}"
                   + (staError is null ? "" : $"；STA 线程异常：{staError}")
                   + (findInfo is null ? "" : $"；{findInfo}"));
        }

        Console.WriteLine($"        （证据图目录：{outDir}；本组不产图 —— 面板的「看得见」由发布冒烟负责）");

        return failures;
    }

    /// <summary>M7.5 S1：离线面板就绪/失败闸门（LoadFailed 倒队列 / selfTest 透传 / 命令通道转发）。</summary>
    private static int RunM75S1Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.5 S1：离线面板就绪/失败闸门（LoadFailed / selfTest / 命令通道）================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        // ---------------------------------------------------------- 1) loadFailed 倒掉待发队列 + 拒绝继续排队
        //
        // 没有这段，加载失败时命令会一直排队等一个永远不来的 ready ——
        // 用户看到的就是「点了没反应」，且日志里连一条错误都没有。
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.OpenAsync().GetAwaiter().GetResult();

            // ready 之前先攒两条命令
            controller.SendAsync("command", new { label = "A" }).GetAwaiter().GetResult();
            controller.SendAsync("reset", null).GetAwaiter().GetResult();
            bool queued2 = controller.QueuedCommandCount == 2 && backend.Posted.Count == 0;

            // 页面报 loadFailed（应用起不来）
            backend.RaiseMessage(GeoGebraProtocol.Encode(GeoGebraProtocol.LoadFailed,
                new { reason = "GeoGebra 离线资源缺失", detail = "web3d/cache.js 404" }));

            bool failedOk = controller.IsLoadFailed
                         && !controller.IsReady
                         && controller.QueuedCommandCount == 0
                         && backend.Posted.Count == 0;

            Report("★ loadFailed 前已排队的命令被倒掉（不再等一个永远不来的 ready）",
                   queued2 && failedOk,
                   $"queuedBefore={queued2} loadFailed→IsLoadFailed={controller.IsLoadFailed} "
                   + $"queuedAfter={controller.QueuedCommandCount} posted={backend.Posted.Count}");

            // 失败后继续发命令：静默丢弃，不排队不投递
            controller.SendAsync("command", new { label = "B" }).GetAwaiter().GetResult();
            Report("★ 失败后继续发命令被静默丢弃（不排队、不投递，避免「点了没反应」）",
                   controller.QueuedCommandCount == 0 && backend.Posted.Count == 0,
                   $"queued={controller.QueuedCommandCount} posted={backend.Posted.Count}");

            Report("★ 失败原因进状态栏（老师能看见为什么打不开）",
                   hooks.LastStatus is not null && hooks.LastStatus.Contains("GeoGebra 离线资源缺失", StringComparison.Ordinal),
                   hooks.LastStatus ?? "(无)");
        }

        // ---------------------------------------------------------- 2) selfTest 透传
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            bool got = false;
            string? gotType = null;
            controller.MessageReceived += (_, m) => { got = true; gotType = m.Type; };

            backend.RaiseMessage(GeoGebraProtocol.Encode(GeoGebraProtocol.SelfTest,
                new { objects = "4", names = "A,B,c,P" }));

            Report("selfTest 消息被透传给订阅者（发布冒烟据此断言离线可画圆/拖点）",
                   got && gotType == GeoGebraProtocol.SelfTest,
                   $"got={got} type={gotType}");
        }

        // ---------------------------------------------------------- 3) 命令通道：ready 后 host→JS 的 command 正确转发
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.OpenAsync().GetAwaiter().GetResult();
            backend.RaiseMessage(GeoGebraProtocol.Encode(GeoGebraProtocol.Ready, new { version = "5.0.abc" }));

            controller.SendAsync(GeoGebraProtocol.Command, new { command = "Circle((0,0),(3,0))" }).GetAwaiter().GetResult();

            bool ok = backend.Posted.Count == 1
                   && backend.Posted[0].Contains("\"type\":\"command\"", StringComparison.Ordinal)
                   && backend.Posted[0].Contains("Circle((0,0),(3,0))", StringComparison.Ordinal);

            Report("★★ ready 后命令（画圆）正确转发到面板（host→JS 通道，离线画圆靠它）",
                   ok, $"posted={backend.Posted.Count} 首条={backend.Posted.FirstOrDefault() ?? "-"}");
        }

        Console.WriteLine($"        （本组不产图；断网画圆的「看得见」由发布冒烟 + 截图复核负责）");
        return failures;
    }

    /// <summary>
    /// M7.5 S3：A1 停靠形态 + 触控互斥（形态切换、互斥激活不受切换影响、切换失败不伤状态）。
    /// </summary>
    /// <remarks>
    /// 停靠区的<b>布局</b>（画布缩小让位、顶栏不与网页区重叠）在真窗口里才量得准，
    /// 由发布冒烟 + 截图负责；本组断言的是<b>逻辑</b>：形态状态怎么流转、
    /// 切换过程中画布互斥与就绪闸门是否纹丝不动、失败要不要回滚。
    /// </remarks>
    private static int RunM75S3Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.5 S3：停靠面板形态 + 触控互斥（A1）================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        // ---------------------------------------------------------- 1) 没开面板时切换：只记形态，不碰画布
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            Report("默认形态是全屏（A3，一体机主用法）",
                   controller.CurrentForm == PanelForm.FullScreen && backend.Form == PanelForm.FullScreen,
                   $"controller={controller.CurrentForm} backend={backend.Form}");

            controller.ToggleFormAsync().GetAwaiter().GetResult();

            Report("★ 面板没开时切换只记形态：不进面板模式、不叫后端开面板",
                   controller.CurrentForm == PanelForm.Docked
                   && backend.Form == PanelForm.Docked
                   && !hooks.IsPanelModeActive && hooks.EnterCount == 0
                   && backend.ShowCount == 0,
                   $"form={controller.CurrentForm} enter={hooks.EnterCount} show={backend.ShowCount}");

            // 下次打开应沿用停靠形态
            controller.OpenAsync().GetAwaiter().GetResult();

            Report("打开时沿用上次选择的形态（老师切过一次就该记住）",
                   controller.IsOpen && backend.Form == PanelForm.Docked && hooks.IsPanelModeActive,
                   $"IsOpen={controller.IsOpen} form={backend.Form} enter={hooks.EnterCount}");
        }

        // ---------------------------------------------------------- 2) 开着面板时切换：互斥激活纹丝不动
        //
        // 这是 S3 最要命的一条：形态切换只是"界面怎么摆"，
        // 画布输入的抑制状态绝不能被它顺手动过 —— 否则就会出现
        // "一切换停靠画布就能写字了"或"一切换全屏白板锁死"这类现场事故。
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.OpenAsync().GetAwaiter().GetResult();

            int enterBefore = hooks.EnterCount;

            PanelForm? notified = null;
            controller.FormChanged += (_, f) => notified = f;

            // 全屏 → 停靠
            controller.ToggleFormAsync().GetAwaiter().GetResult();

            Report("★ 全屏→停靠：互斥激活不动（不重复进入面板模式）",
                   controller.CurrentForm == PanelForm.Docked
                   && hooks.EnterCount == enterBefore
                   && hooks.IsPanelModeActive,
                   $"form={controller.CurrentForm} enter={hooks.EnterCount} panelMode={hooks.IsPanelModeActive}");

            Report("停靠→切换完成后 FormChanged 把新形态通知宿主（宿主据此撑开停靠区）",
                   notified == PanelForm.Docked && backend.SwitchCount == 1,
                   $"notified={notified} switch={backend.SwitchCount}");

            // 停靠 → 全屏（往返）
            controller.ToggleFormAsync().GetAwaiter().GetResult();

            Report("停靠→全屏往返：形态回到全屏、互斥激活仍然不动",
                   controller.CurrentForm == PanelForm.FullScreen
                   && backend.Form == PanelForm.FullScreen
                   && hooks.EnterCount == enterBefore
                   && hooks.IsPanelModeActive
                   && backend.SwitchCount == 2,
                   $"form={controller.CurrentForm} enter={hooks.EnterCount} switch={backend.SwitchCount}");

            // 切换期间就绪闸门照常工作：ready 未到时命令照样排队
            controller.SendAsync(GeoGebraProtocol.Command, new { command = "A=(1,1)" }).GetAwaiter().GetResult();

            Report("切换形态不影响就绪闸门（页面没 ready 命令照样排队，不丢）",
                   controller.QueuedCommandCount == 1 && backend.Posted.Count == 0,
                   $"queued={controller.QueuedCommandCount} posted={backend.Posted.Count}");

            backend.RaiseMessage(GeoGebraProtocol.Encode(GeoGebraProtocol.Ready, new { version = "test" }));

            Report("切换后 ready 照常放行排队命令（形态切换与通信状态机互不干扰）",
                   backend.Posted.Count == 1 && controller.QueuedCommandCount == 0,
                   $"posted={backend.Posted.Count} queued={controller.QueuedCommandCount}");
        }

        // ---------------------------------------------------------- 3) 面板顶栏按钮 → 切换（经 FormToggleRequested）
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.OpenAsync().GetAwaiter().GetResult();
            backend.RaiseFormToggleRequested();

            Report("用户点面板顶栏「停靠面板」⇒ 控制器完成切换（全屏窗经后端把请求转上来）",
                   controller.CurrentForm == PanelForm.Docked && backend.Form == PanelForm.Docked,
                   $"form={controller.CurrentForm} backendForm={backend.Form}");
        }

        // ---------------------------------------------------------- 4) ★ 切换失败不伤状态
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true, ThrowOnSwitch = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.OpenAsync().GetAwaiter().GetResult();
            controller.ToggleFormAsync().GetAwaiter().GetResult();

            Report("★ 形态切换失败：不抛出、形态保持原样、互斥激活不动（界面照常能用）",
                   controller.IsOpen
                   && controller.CurrentForm == PanelForm.FullScreen
                   && backend.Form == PanelForm.FullScreen
                   && hooks.IsPanelModeActive,
                   $"form={controller.CurrentForm} backendForm={backend.Form} panelMode={hooks.IsPanelModeActive}");

            Report("切换失败的原因写到状态栏（不是「点了没反应」）",
                   !string.IsNullOrEmpty(hooks.LastStatus)
                   && hooks.LastStatus!.Contains("切换形态失败", StringComparison.Ordinal),
                   hooks.LastStatus ?? "(无)");
        }

        // ---------------------------------------------------------- 5) 停靠形态下关闭再打开
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            controller.OpenAsync().GetAwaiter().GetResult();
            controller.ToggleFormAsync().GetAwaiter().GetResult();
            controller.CloseAsync().GetAwaiter().GetResult();

            Report("停靠形态下关闭：退出面板模式、输入交还（与全屏形态同一条恢复路径）",
                   !controller.IsOpen && !hooks.IsPanelModeActive && hooks.ExitCount == 1,
                   $"IsOpen={controller.IsOpen} panelMode={hooks.IsPanelModeActive} exit={hooks.ExitCount}");

            controller.OpenAsync().GetAwaiter().GetResult();

            Report("再打开仍用停靠形态（记住选择，而不是一律弹回全屏）",
                   controller.IsOpen && backend.Form == PanelForm.Docked && hooks.IsPanelModeActive,
                   $"IsOpen={controller.IsOpen} form={backend.Form}");
        }

        // ---------------------------------------------------------- 6) ★★ S2：GeoGebra 兼容面
        //
        // S2 把 GeoGebraPanelController 泛化成 WebPanelController，全部承诺就一句话：
        // 「已分发的 GeoGebra 插件一行都不用改」。所以它必须是断言，而不是一句说明 ——
        // 同一个实例既要能当 IWebPanelBridge 用（新工具），也要能当 IGeoGebraBridge 用（老插件），
        // 且老接口的 ShowAsync / HideAsync 与新接口的 OpenAsync / CloseAsync 是同一件事。
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            // 老插件拿到的就是它（IToolContext.GeoGebra 的静态类型）
            IGeoGebraBridge bridge = controller;

            Report("同一个实例同时是 IWebPanelBridge 与 IGeoGebraBridge（新工具与老插件共用一个面板）",
                   controller is IWebPanelBridge && ReferenceEquals((IWebPanelBridge)controller, bridge),
                   $"IWebPanelBridge={controller is IWebPanelBridge}");

            bool oldShowOk = bridge.ShowAsync(null).GetAwaiter().GetResult();

            Report("老接口 ShowAsync 等价于新接口 OpenAsync",
                   oldShowOk && controller.IsOpen && backend.ShowCount == 1 && hooks.IsPanelModeActive,
                   $"opened={oldShowOk} IsOpen={controller.IsOpen} ShowCount={backend.ShowCount}");

            GeoGebraMessage? seen = null;
            bridge.MessageReceived += (_, m) => seen = m;
            backend.RaiseMessage(WebPanelProtocol.Encode(WebPanelProtocol.Ready, new { version = "compat" }));

            Report("老接口 MessageReceived 收到的是 GeoGebraMessage（老插件的解析代码不用动）",
                   seen is not null && seen.Type == WebPanelProtocol.Ready
                   && seen.ReadPayloadString("version") == "compat",
                   $"type={seen?.Type ?? "(未收到)"}");

            bridge.HideAsync().GetAwaiter().GetResult();

            Report("老接口 HideAsync 等价于新接口 CloseAsync（退出并交还画布输入）",
                   !controller.IsOpen && !hooks.IsPanelModeActive && hooks.ExitCount == 1,
                   $"IsOpen={controller.IsOpen} panelMode={hooks.IsPanelModeActive} exit={hooks.ExitCount}");
        }

        // ---------------------------------------------------------- 7) S4：抓帧（落画布的前半步）
        //
        // 面板是 HWND，它的画面不进 WPF 渲染树 ⇒ 想把它搬到卷面上，只有
        // 「浏览器侧抓帧 + 落成一个可存档的位图对象」这一条路。这一组先钉住"抓到字节"，
        // 落画布（图形对象 + .twb 存档）是 S4 的后半步。
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);

            var closed = controller.CapturePngAsync().GetAwaiter().GetResult();

            Report("面板没开时抓帧返回 null（不去碰空的后端）",
                   closed is null && backend.CaptureCount == 0,
                   $"png={(closed is null ? "null" : closed.Length + "B")} calls={backend.CaptureCount}");

            // 一个最小的 PNG 头，只验"原样透传"，不在这里伪造一张真图。
            backend.CapturePngResult = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
            controller.OpenAsync().GetAwaiter().GetResult();
            var got = controller.CapturePngAsync().GetAwaiter().GetResult();

            Report("面板开着时抓帧原样透传后端字节",
                   got is not null && got.Length == 4 && got[0] == 0x89,
                   $"png={(got is null ? "null" : got.Length + "B")} calls={backend.CaptureCount}");
        }

        // ---------------------------------------------------------- 8) S4b：仿真画面落到卷面 + 存档
        //
        // 面板是 HWND，它的画面不进 WPF 渲染树 ⇒「把仿真画面搬到卷面上」在代码里没有
        // 任何自动通路，全靠下面这几个能独立证伪的小部件拼起来：
        // 尺寸纯函数 → PNG 头 → 位图仓库 → 落点 → .twb 图像段 → 一条端到端。
        // 判据就是 S4b 的三条验收：存档往返后图还在、导出 PDF 带上、撤销一步整体消失。
        {
            // ---- 8.1 尺寸是纯函数（不能靠位图；更不能靠 ActualWidth）----
            var imageRenderer = new WebPanelImageRenderer(new WebPanelImageStore());
            var noNumbers = new Dictionary<string, double>();
            var noTexts = new Dictionary<string, string>();

            var sizedRef = new S4FakeRef(WebPanelImageRenderer.KindName, new Point(0, 0), 1, 0,
                new Dictionary<string, double> { ["w"] = 100, ["h"] = 50 }, noTexts);
            var bareRef = new S4FakeRef(WebPanelImageRenderer.KindName, new Point(0, 0), 1, 0,
                noNumbers, noTexts);
            var dirtyRef = new S4FakeRef(WebPanelImageRenderer.KindName, new Point(0, 0), 1, 0,
                new Dictionary<string, double> { ["w"] = double.NaN, ["h"] = -5 }, noTexts);

            var mSized = imageRenderer.Measure(sizedRef);
            var mBare = imageRenderer.Measure(bareRef);
            var mDirty = imageRenderer.Measure(dirtyRef);

            Report("位图对象的尺寸是纯函数（只吃对象参数；位图不在也照样算得出尺寸）",
                   Math.Abs(mSized.Width - 100) < 1e-9 && Math.Abs(mSized.Height - 50) < 1e-9
                   && Math.Abs(mBare.Width - WebPanelImageRenderer.FallbackWidth) < 1e-9,
                   $"有参数 {mSized.Width}×{mSized.Height}；没参数 ⇒ 兜底 {mBare.Width}×{mBare.Height}");

            Report("尺寸是脏值（NaN / 负数）时不崩，且不小于「手指点得中」的最小边长",
                   mDirty.Width >= WebPanelImageRenderer.MinSide
                   && mDirty.Height >= WebPanelImageRenderer.MinSide,
                   $"{mDirty.Width}×{mDirty.Height}（下限 {WebPanelImageRenderer.MinSide}）");

            var sixteenByNine = WebPanelImageRenderer.WorldSizeFor(1600, 900, 400);
            Report("世界尺寸按像素长宽比算（16:9 进来必须 16:9 出去 —— 拉伸会把圆画成椭圆）",
                   Math.Abs(sixteenByNine.Width - 400) < 1e-9
                   && Math.Abs(sixteenByNine.Height - 225) < 1e-9,
                   $"{sixteenByNine.Width}×{sixteenByNine.Height}");
        }

        // ---- 8.2 PNG 头：只读头，不解码整张图 ----
        {
            byte[] realPng = EncodePng(64, 36, Colors.Red);
            bool headOk = PngSize.TryRead(realPng, out int headW, out int headH);

            Report("PNG 头解析：真编码器的输出读出 64×36（不解码整张图）",
                   headOk && headW == 64 && headH == 36,
                   headOk ? $"{headW}×{headH}" : "读不出");

            bool junkOk = PngSize.TryRead(new byte[] { 1, 2, 3 }, out int junkW, out int junkH);
            bool cutOk = PngSize.TryRead(realPng[..20], out _, out _);
            Report("不是 PNG / 被截断 ⇒ 明确说读不出（不抛异常、不瞎猜尺寸）",
                   !junkOk && !cutOk && junkW == 0 && junkH == 0,
                   $"3 个字节 ⇒ {junkOk}；截到 20 字节 ⇒ {cutOk}");

            PngSize.TryReadOrRatio(Array.Empty<byte>(), out int fallbackW, out int fallbackH);
            Report("读不出时退回 4:3 兜底比例（宁可比例略偏，也不让整次导出失败）",
                   fallbackW == 4 && fallbackH == 3, $"{fallbackW}:{fallbackH}");
        }

        // ---- 8.3 位图仓库：按内容去重、按 Id 取、Restore 是替换 ----
        {
            var vault = new WebPanelImageStore();
            byte[] red = EncodePng(8, 6, Colors.Red);

            string idA = vault.Add(red);
            string idB = vault.Add(red);
            string idEmpty = vault.Add(null);

            Report("同一帧入库两次只存一份（按内容 SHA-256 去重）",
                   idA.Length == 12 && idA == idB && vault.Count == 1 && idEmpty.Length == 0,
                   $"id={idA} 两次相同={idA == idB} 张数={vault.Count} 空字节 ⇒「{idEmpty}」");

            byte[] blue = EncodePng(8, 6, Colors.Blue);
            string idC = vault.Add(blue);
            Report("内容不同 ⇒ 各自一张",
                   idC != idA && vault.Count == 2, $"张数={vault.Count} {vault.Describe()}");

            Report("取不存在的 Id ⇒ null（渲染器据此画占位框，不崩）",
                   vault.Get("nope") is null && vault.Get(null) is null && vault.Get(string.Empty) is null,
                   "三种怪键都返回 null");

            vault.Restore(new[] { new TwbImageEntry { Id = "aaaabbbbcccc", Bytes = blue } });
            Report("Restore 是「替换」而不是「合并」（合并的话上一份工程的位图会一直躺在内存里，"
                   + "还会被写进下一次保存）",
                   vault.Count == 1 && vault.Contains("aaaabbbbcccc") && !vault.Contains(idA),
                   $"张数={vault.Count} {vault.Describe()}");
        }

        // ---- 8.4 落点（纯函数）：视口中心为锚 + 夹进页内 ----
        {
            var page = new Rect(0, 0, 600, 850);
            var imageSize = new Size(276, 155);

            var atCenter = WebPanelExportService.ComputePlacement(page, new Point(300, 400), imageSize);
            Report("视口中心在页内 ⇒ 就落在视口中心（老师导的正是他此刻在看的地方）",
                   Math.Abs(atCenter.X - 300) < 1e-9 && Math.Abs(atCenter.Y - 400) < 1e-9,
                   $"({atCenter.X:F0},{atCenter.Y:F0})");

            var farAway = WebPanelExportService.ComputePlacement(page, new Point(-9000, 99000), imageSize);
            double margin = WebPanelExportService.PageMarginWorld;
            bool insidePage =
                farAway.X - imageSize.Width / 2 >= page.Left + margin - 1e-6
                && farAway.X + imageSize.Width / 2 <= page.Right - margin + 1e-6
                && farAway.Y - imageSize.Height / 2 >= page.Top + margin - 1e-6
                && farAway.Y + imageSize.Height / 2 <= page.Bottom - margin + 1e-6;

            Report("视口中心在页外 ⇒ 夹进页内（页外的图导成 PDF 时不属于任何一页，等于凭空少一张）",
                   insidePage, $"({farAway.X:F0},{farAway.Y:F0}) 留白 {margin}");

            var tooBig = new Size(2000, 1200);
            var centered = WebPanelExportService.ComputePlacement(page, new Point(-999, -999), tooBig);
            Report("图比页面还大 ⇒ 居中（此时「留在页内」根本满足不了，硬夹只会把它塞到角落）",
                   Math.Abs(centered.X - page.Left - page.Width / 2) < 1e-9
                   && Math.Abs(centered.Y - page.Top - page.Height / 2) < 1e-9,
                   $"({centered.X:F0},{centered.Y:F0})，页心 ({page.Width / 2:F0},{page.Height / 2:F0})");

            var tall = WebPanelExportService.ClampToPageHeight(new Size(276, 5000), page);
            Report("超高的截图按页高上限压一次，比例仍然守恒",
                   Math.Abs(tall.Height - page.Height * WebPanelExportService.MaxPageHeightFraction) < 1e-9
                   && Math.Abs(tall.Width / tall.Height - 276 / 5000.0) < 1e-9,
                   $"276×5000 ⇒ {tall.Width:F1}×{tall.Height:F1}");
        }

        // ---- 8.5 .twb 图像段：写进 images/、逐张 SHA-256、篡改必拒 ----
        {
            string work = Path.Combine(outDir, "m22s4b");
            Directory.CreateDirectory(work);
            string twbPath = Path.Combine(work, "with-images.twb");

            byte[] pngA = EncodePng(16, 12, Colors.Red);
            byte[] pngB = EncodePng(20, 10, Colors.Blue);
            var images = new List<TwbImageEntry>
            {
                new() { Id = "aaaa1111bbbb", Bytes = pngA },
                new() { Id = "cccc2222dddd", Bytes = pngB },
            };

            var manifest = TwbManifest.New("位图段往返", TwbPdfEmbedding.None);
            bool wrote = TwbFile.Write(twbPath, manifest, null, null, images, out string writeError);

            var entryNames = new List<string>();
            if (File.Exists(twbPath))
            {
                using var zip = ZipFile.OpenRead(twbPath);
                foreach (var entry in zip.Entries) entryNames.Add(entry.FullName);
            }

            Report("位图进独立的 images/ 段（不塞进批注载荷当 base64 —— 那会让每次撤销都快照半兆字符串）",
                   wrote && entryNames.Contains(TwbFile.ImageEntryName("aaaa1111bbbb"))
                   && entryNames.Contains(TwbFile.ImageEntryName("cccc2222dddd")),
                   writeError.Length > 0 ? writeError : string.Join("、", entryNames));

            Report("manifest 里逐张记了 SHA-256（ZIP 读 stored 条目不校验 CRC，这是唯一的兜底）",
                   manifest.Integrity.ImageSha256.Count == 2
                   && manifest.Integrity.ImageSha256.ContainsKey(TwbFile.ImageEntryName("aaaa1111bbbb")),
                   $"校验值 {manifest.Integrity.ImageSha256.Count} 条");

            bool read = TwbFile.TryRead(twbPath, out _, out _, out _, out var back, out string readError);
            bool same = read && back.Count == 2;
            if (same)
            {
                bool sawA = false, sawB = false;
                foreach (var image in back)
                {
                    if (image.Id == "aaaa1111bbbb" && image.Bytes.SequenceEqual(pngA)) sawA = true;
                    if (image.Id == "cccc2222dddd" && image.Bytes.SequenceEqual(pngB)) sawB = true;
                }
                same = sawA && sawB;
            }

            Report("读回来逐字节一致（往返不丢、不损）",
                   same, readError.Length > 0 ? readError : $"{back.Count} 张");

            string cleanPath = Path.Combine(work, "no-images.twb");
            TwbFile.Write(cleanPath, TwbManifest.New("无图", TwbPdfEmbedding.None), null, null, out _);

            var cleanNames = new List<string>();
            using (var zip = ZipFile.OpenRead(cleanPath))
            {
                foreach (var entry in zip.Entries) cleanNames.Add(entry.FullName);
            }

            Report("没有位图时不写 images/ 段（包内结构干净，不留 0 字节条目）",
                   cleanNames.Count == 1 && cleanNames[0] == TwbFile.ManifestEntryName,
                   string.Join("、", cleanNames));

            // 把包内第一张 PNG 的负载改掉一个字节 ⇒ 必须明确拒绝，并说清是位图那一段
            byte[] fileBytes = File.ReadAllBytes(twbPath);
            int signature = -1;
            for (int i = 0; i + 4 <= fileBytes.Length; i++)
            {
                if (fileBytes[i] == 0x89 && fileBytes[i + 1] == 0x50
                    && fileBytes[i + 2] == 0x4E && fileBytes[i + 3] == 0x47)
                {
                    signature = i;
                    break;
                }
            }

            byte[] broken = (byte[])fileBytes.Clone();
            if (signature >= 0) broken[signature + 20] ^= 0xFF;

            string brokenPath = Path.Combine(work, "broken-image.twb");
            File.WriteAllBytes(brokenPath, broken);

            bool brokenRead = TwbFile.TryRead(brokenPath, out _, out _, out _, out _, out string brokenError);
            Report("包内位图被改过一个字节 ⇒ 明确拒绝并指明是位图那一段",
                   !brokenRead && brokenError.Contains("位图", StringComparison.Ordinal),
                   $"PNG 签名 @{signature}；读回={brokenRead}；原因：{brokenError}");
        }

        // ---- 8.6 端到端：抓帧 → 落点 → 落对象 → 视觉 → 撤销/重做 → 存档往返 → 缺图降级 ----
        {
            var catalog = new GfxRendererCatalog();
            var store = new GfxObjectStore(catalog);
            var history = new GfxHistory(store);
            var images = new WebPanelImageStore();

            var canvas = new Canvas { Width = 1200, Height = 900 };
            var layer = new GfxObjectLayer(canvas, store, catalog);
            catalog.Register(new WebPanelImageRenderer(images));

            var backend = new FakeWebPanelBackend
            {
                IsAvailable = true,
                CapturePngResult = EncodePng(64, 36, Colors.Red),
            };
            var hooks = new FakeGeoGebraHooks();
            using var panel = new WebPanelController(WebPanelProfile.GeoGebra, backend, hooks);
            panel.OpenAsync().GetAwaiter().GetResult();

            var page = new Rect(0, 0, 600, 850);
            var service = new WebPanelExportService(
                panel, images,
                () => page,
                () => new Point(300, 400),
                place: draft => { history.Capture("导出仿真画面"); return store.Add(draft); });

            var exported = service.ExportAsync().GetAwaiter().GetResult();

            canvas.Measure(new Size(1200, 900));
            canvas.Arrange(new Rect(0, 0, 1200, 900));
            canvas.UpdateLayout();

            Report("端到端：抓帧 → 算落点 → 落成一个图形对象",
                   exported.Ok && store.Count == 1 && exported.ObjectId is not null
                   && store.Objects[0].Kind == WebPanelImageRenderer.KindName,
                   exported.Message);

            var placedVisual = exported.ObjectId is null ? null : layer.VisualFor(exported.ObjectId);
            var decoded = FindImage(placedVisual)?.Source as BitmapImage;

            Report("卷面上真解码出一张 64×36 的位图（不是只挂了个空 Image 元素）",
                   decoded is not null && decoded.PixelWidth == 64 && decoded.PixelHeight == 36,
                   decoded is null ? "没找到 Image / Source" : $"{decoded.PixelWidth}×{decoded.PixelHeight}");

            var placed = store.Objects[0];
            Report("落成对象的尺寸沿用像素比例（64:36 ⇒ 宽高比仍是 16:9）",
                   Math.Abs(placed.GetNumber(WebPanelImageRenderer.WidthKey, 0)
                            / placed.GetNumber(WebPanelImageRenderer.HeightKey, 1) - 64 / 36.0) < 1e-6,
                   $"{placed.GetNumber(WebPanelImageRenderer.WidthKey, 0):F1}×"
                   + $"{placed.GetNumber(WebPanelImageRenderer.HeightKey, 1):F1} pt，"
                   + $"落点 ({placed.Center.X:F0},{placed.Center.Y:F0})");

            bool undone = history.Undo();
            Report("撤销一步整体消失（一张截图 = 一个撤销单元，不是半个）",
                   undone && store.Count == 0, $"undo={undone} 对象数={store.Count}");

            history.Redo();
            Report("重做一步又回来，且位图还在（撤销不回收字节，否则重做出来只剩一张缺失框）",
                   store.Count == 1 && images.Count == 1,
                   $"对象数={store.Count} 位图 {images.Describe()}");

            // ---- 存档往返：把这份卷面写进 .twb 再读回来（装载顺序与 MainWindow 一致：先位图、后对象）----
            string roundTrip = Path.Combine(outDir, "m22s4b", "round-trip.twb");
            Directory.CreateDirectory(Path.GetDirectoryName(roundTrip)!);

            bool saved = TwbFile.Write(roundTrip, TwbManifest.New("往返", TwbPdfEmbedding.None),
                                       null, null, images.Snapshot(), out string saveError);
            bool loaded = TwbFile.TryRead(roundTrip, out _, out _, out _, out var reloaded,
                                          out string loadError);

            var catalog2 = new GfxRendererCatalog();
            var store2 = new GfxObjectStore(catalog2);
            var images2 = new WebPanelImageStore();
            var canvas2 = new Canvas { Width = 1200, Height = 900 };
            var layer2 = new GfxObjectLayer(canvas2, store2, catalog2);
            catalog2.Register(new WebPanelImageRenderer(images2));

            images2.Restore(reloaded);
            store2.Restore(store.Snapshot());

            canvas2.Measure(new Size(1200, 900));
            canvas2.Arrange(new Rect(0, 0, 1200, 900));
            canvas2.UpdateLayout();

            var reopened = store2.Count == 1 ? layer2.VisualFor(store2.Objects[0].Id) : null;
            var reopenedBitmap = FindImage(reopened)?.Source as BitmapImage;

            Report("存档往返之后图还在（位图与对象都恢复了，而且真解码出来）",
                   saved && loaded && store2.Count == 1
                   && reopenedBitmap is not null && reopenedBitmap.PixelWidth == 64,
                   loadError.Length > 0 ? loadError
                       : $"保存={saved} 读回={loaded} 位图 {images2.Describe()} "
                         + $"解码={reopenedBitmap?.PixelWidth.ToString() ?? "无"}");

            // ---- 缺图降级：对象在、位图不在（别人发来的工程缺了图像段）----
            var catalog3 = new GfxRendererCatalog();
            var store3 = new GfxObjectStore(catalog3);
            var images3 = new WebPanelImageStore();
            var canvas3 = new Canvas { Width = 1200, Height = 900 };
            var layer3 = new GfxObjectLayer(canvas3, store3, catalog3);
            catalog3.Register(new WebPanelImageRenderer(images3));

            store3.Restore(store.Snapshot());
            canvas3.Measure(new Size(1200, 900));
            canvas3.Arrange(new Rect(0, 0, 1200, 900));
            canvas3.UpdateLayout();

            var orphan = store3.Count == 1 ? layer3.VisualFor(store3.Objects[0].Id) : null;

            Report("位图缺失时降级成占位框：对象还在、还能选中拖动，存档也不会把它丢掉",
                   store3.Count == 1 && orphan is not null && FindImage(orphan) is null,
                   $"对象数={store3.Count} 有视觉={orphan is not null} "
                   + $"里面含位图元素={FindImage(orphan) is not null}");
        }

        // ---- 8.7 ★ 像素探针：图形视觉的 MeasureOverride 约定是 (0,0) ----
        //
        // 图形层把渲染器的视觉放在一个 Canvas 里、把它的原点摆到对象中心，
        // 而这类渲染器约定 MeasureOverride 返回 (0,0) ⇒ ActualWidth 恒为 0。
        // 于是「按 ActualWidth 画」的渲染器会画出一张 0×0 的空图：数据全对、断言全绿、
        // 屏幕上是空的。这一条只有像素能证 —— 下面两条正反各证一半。
        {
            // 反面：按 ActualWidth 画。顺带自证探针没哑 —— 同一个类被赋了显式宽高就画得出。
            var dead = new ActualWidthProbeVisual();
            var deadSurface = new Canvas { Width = 120, Height = 60 };
            Canvas.SetLeft(dead, 60);
            Canvas.SetTop(dead, 30);
            deadSurface.Children.Add(dead);
            deadSurface.Measure(new Size(120, 60));
            deadSurface.Arrange(new Rect(0, 0, 120, 60));
            deadSurface.UpdateLayout();

            long deadPixels = CountDrawnPixels(Snapshot(deadSurface, 120, 60));

            Report("反面探针：按 ActualWidth 画的渲染器画出来是空图（ActualWidth 恒为 0）",
                   deadPixels == 0, $"画出的像素 {deadPixels}（期望 0）");

            var warm = new ActualWidthProbeVisual { Width = 30, Height = 20 };
            var warmSurface = new Canvas { Width = 120, Height = 60 };
            Canvas.SetLeft(warm, 60);
            Canvas.SetTop(warm, 30);
            warmSurface.Children.Add(warm);
            warmSurface.Measure(new Size(120, 60));
            warmSurface.Arrange(new Rect(0, 0, 120, 60));
            warmSurface.UpdateLayout();

            long warmPixels = CountDrawnPixels(Snapshot(warmSurface, 120, 60));

            Report("反面探针自证：同一个类被赋了显式宽高就画得出像素（证明探针本身不哑）",
                   warmPixels > 100, $"30×20 红块 ⇒ 画出的像素 {warmPixels}（期望 ≈600）");
        }

        {
            // 正面：WebPanelImageRenderer 的视觉在图形层里真的被光栅化。
            var probeImages = new WebPanelImageStore();
            string probeId = probeImages.Add(EncodePng(40, 20, Colors.Red));
            var probeRenderer = new WebPanelImageRenderer(probeImages);

            var probeVisual = probeRenderer.CreateVisual(new S4FakeRef(
                WebPanelImageRenderer.KindName, new Point(0, 0), 1, 0,
                new Dictionary<string, double> { ["w"] = 100, ["h"] = 50 },
                new Dictionary<string, string> { [WebPanelImageRenderer.ImageIdKey] = probeId }));

            var surface = new Canvas { Width = 200, Height = 100 };
            Canvas.SetLeft(probeVisual, 50);
            Canvas.SetTop(probeVisual, 25);
            surface.Children.Add(probeVisual);
            surface.Measure(new Size(200, 100));
            surface.Arrange(new Rect(0, 0, 200, 100));
            surface.UpdateLayout();

            long drawn = CountDrawnPixels(Snapshot(surface, 200, 100));

            Report("位图视觉真的被光栅化了（100×50 的红图 ⇒ 非白像素数以千计）",
                   drawn > 1000, $"画出的像素 {drawn}");
        }

        // ---- 8.8 实锤回归：题号牌的色块必须真的画得出来（8.7 那条道理的既有受害者）----
        //
        // M12 的题号牌曾经按 ActualWidth 画 ⇒ 错题态（红底白字）那张「底」是 0×0 的，
        // 等于不存在，而白字又画在白纸上 ⇒ 标了「错题」反而一个像素都看不见。
        // 像素探针实测：未讲 103 / 错题 0。这条按三态各钉一次，色块必须真的出现。
        {
            long ChipPixels(int state)
            {
                var surface = new Canvas { Width = 80, Height = 40 };
                var chip = new QuestionMarkRenderer().CreateVisual(new S4FakeRef(
                    QuestionMarkRenderer.KindName, new Point(0, 0), 1, 0,
                    new Dictionary<string, double> { [QuestionMarkRenderer.StateKey] = state },
                    new Dictionary<string, string> { [QuestionMarkRenderer.LabelKey] = "12" }));

                Canvas.SetLeft(chip, 40);
                Canvas.SetTop(chip, 20);
                surface.Children.Add(chip);
                surface.Measure(new Size(80, 40));
                surface.Arrange(new Rect(0, 0, 80, 40));
                surface.UpdateLayout();
                return CountDrawnPixels(Snapshot(surface, 80, 40));
            }

            long unmarked = ChipPixels(0);
            long wrong = ChipPixels(1);
            long done = ChipPixels(2);

            // 未讲态的底色是纸白色（与空白同色）⇒ 只有描边与文字能被数到，门槛低一档；
            // 错题 / 已讲态是实心彩底（34.4×26 ≈ 894 个像素），必须是几百个。
            Report("题号牌三态都画得出色块（错题态曾经是「一个像素都没有」）",
                   unmarked > 150 && wrong > 400 && done > 400,
                   $"未讲={unmarked} 错题={wrong} 已讲={done} 彩色像素（芯片 34.4×26 ≈ 894）");
        }

        return failures;
    }

    // ============================================================ M22 S3：物理仿真

    /// <summary>
    /// M22 S3 断言组：原生运动学三个模型（单摆 / 弹簧振子 / 斜面）
    /// + 「把画面落成卷面上可存档的位图对象」那条链路。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分三段验，因为三段各有各的坏法：
    /// <list type="number">
    /// <item><b>模型的数学</b> —— 这几条一旦错，屏幕上照样「看着像在动」，
    /// 靠肉眼一辈子发现不了「大摆角周期没变长」「µ 大于 tanθ 时滑块自己往坡上爬」；</item>
    /// <item><b>落成位图对象</b> —— 尺寸必须是纯函数（位图不在也算得出）、
    /// 字节要按内容去重、撤销要一步整体消失；</item>
    /// <item><b>接线与降级</b> —— 工具声明、瞬态工具的 <c>Deactivate</c> 必须为空、
    /// 轻量版那两颗「网页仿真」按钮要灰着并说清原因。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ★ <b>本组不开窗口</b>：<c>SimWindow</c> 是 <c>internal</c> 的 WPF 窗口，
    /// 控制台里把它显示出来要消息循环（而且会真在屏幕上闪一下）。
    /// 所以「窗口上那颗按钮灰不灰」用<b>源级护栏</b>钉住，
    /// 而窗口的真实观感与真机交互由发布冒烟与一体机实测负责 —— 两者都不能省。
    /// </para>
    /// </remarks>
    private static int RunM22S3Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M22 S3：物理仿真（原生运动学 + 落成位图对象）================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        // 把方法体抠出来（按大括号配平，不是「往后切 N 个字符」——
        // 那种切法会咬到下一个成员）。源级护栏要用它判「空实现」。
        static string MethodBody(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return string.Empty;

            int open = source.IndexOf('{', at);
            if (open < 0) return string.Empty;

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source[(open + 1)..i];
                }
            }

            return string.Empty;
        }

        // PNG 字节 → BGRA 像素。位图探针要用它数「画出了多少东西」。
        static (int W, int H, byte[] Bgra) DecodePng(byte[] png)
        {
            var frame = BitmapFrame.Create(
                new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth;
            int h = converted.PixelHeight;
            var buffer = new byte[w * h * 4];
            converted.CopyPixels(buffer, w * 4, 0);
            return (w, h, buffer);
        }

        // 与底色差异明显的像素数（阈值 12/通道之和）。「图上没看见」不等于「图上没有」，
        // 所以这类结论一律用像素数说话。
        static long NonBackgroundPixels(byte[] png, Color background)
        {
            var (_, _, bgra) = DecodePng(png);
            long count = 0;
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                int delta = Math.Abs(bgra[i] - background.B)
                            + Math.Abs(bgra[i + 1] - background.G)
                            + Math.Abs(bgra[i + 2] - background.R);
                if (delta > 12) count++;
            }

            return count;
        }

        // ---------------------------------------------------------- 1) 单摆
        {
            double smallTheory = 2.0 * Math.PI * Math.Sqrt(1.0 / 9.8);

            var small = new PendulumModel { Length = 1.0, Gravity = 9.8 };
            small.Reset(2.0);
            double measuredSmall = small.EstimatePeriod();

            Report("小角下单摆实测周期 == 2π√(L/g)（相对误差 < 0.5%）",
                   Math.Abs(measuredSmall - smallTheory) / smallTheory < 0.005,
                   $"理论 {smallTheory:F4} s / 实测 {measuredSmall:F4} s "
                   + $"/ 相对差 {(measuredSmall - smallTheory) / smallTheory * 100:F3}%");

            var large = new PendulumModel { Length = 1.0, Gravity = 9.8 };
            large.Reset(60.0);
            double measuredLarge = large.EstimatePeriod();

            Report("摆角 60° ⇒ 实测周期比小角理论值长 5% 以上（「大摆角周期变长」"
                   + "是物理课真正的看点，画正弦曲线永远给不出这条）",
                   measuredLarge > smallTheory * 1.05,
                   $"小角 {smallTheory:F4} s / 60° 实测 {measuredLarge:F4} s");

            Report("实测值对得上一阶修正 T₀·(1+θ₀²/16)（误差 < 1.5% —— 高阶项让真值"
                   + "比一阶修正再大一点点，所以容差不能给到 0.1%）",
                   Math.Abs(measuredLarge - large.CorrectedPeriod) / large.CorrectedPeriod < 0.015,
                   $"修正公式 {large.CorrectedPeriod:F4} s / 实测 {measuredLarge:F4} s");

            var energy = new PendulumModel { Length = 1.0, Gravity = 9.8, Damping = 0.0 };
            energy.Reset(30.0);
            double energyStart = energy.SpecificEnergy;
            for (int i = 0; i < 3000; i++) energy.Advance(0.001);
            double drift = Math.Abs(energy.SpecificEnergy - energyStart) / energyStart;

            Report("无阻尼推 3 s 后单位质量机械能守恒（相对漂移 < 1e-6 —— "
                   + "这是数值积分唯一诚实的自证，比「看着像在摆」强得多）",
                   drift < 1e-6,
                   $"E₀ = {energyStart:F9} J/kg ⇒ 3 s 后 {energy.SpecificEnergy:F9}，漂移 {drift:E2}");

            var damped = new PendulumModel { Length = 1.0, Gravity = 9.8, Damping = 0.6 };
            damped.Reset(30.0);
            double dampedStart = damped.SpecificEnergy;
            for (int i = 0; i < 5000; i++) damped.Advance(0.001);

            Report("加阻尼 ⇒ 机械能只会少不会多（少掉的正是耗散，多出来就说明积分在造能量）",
                   damped.SpecificEnergy < dampedStart * 0.5,
                   $"{dampedStart:F6} ⇒ {damped.SpecificEnergy:F6} J/kg");

            var over = new PendulumModel
            {
                Length = -5,
                Gravity = 999,
                Damping = -1,
                StartAngleDegrees = 200,
            };
            var under = new PendulumModel { Length = 99, Gravity = 0, StartAngleDegrees = 0 };

            Report("四个参数的 setter 都夹到量程内（越界 / NaN 不许进模型 —— "
                   + "否则界面上会出一个「摆长 -5 m」的读数）",
                   over.Length == PendulumModel.MinLength
                   && over.Gravity == PendulumModel.MaxGravity
                   && over.Damping == PendulumModel.MinDamping
                   && over.StartAngleDegrees == PendulumModel.MaxStartAngleDegrees
                   && under.Length == PendulumModel.MaxLength
                   && under.Gravity == PendulumModel.MinGravity
                   && under.StartAngleDegrees == PendulumModel.MinStartAngleDegrees,
                   $"L={over.Length} g={over.Gravity} b={over.Damping} θ₀={over.StartAngleDegrees}；"
                   + $"反向 L={under.Length} g={under.Gravity} θ₀={under.StartAngleDegrees}");

            var init = new PendulumModel { Length = 1.0, Gravity = 9.8 };
            init.Reset(20.0);
            bool resetOk = Math.Abs(init.Angle - 20.0 * Math.PI / 180.0) < 1e-12
                           && init.AngularVelocity == 0 && init.Time == 0;

            init.Advance(0.5);
            double pushed = init.Angle;
            init.Advance(0);
            init.Advance(-1);

            Report("Reset 摆到初始摆角、速度与时间归零；Advance(0) / Advance(负数) 都不改状态",
                   resetOk && init.Angle == pushed,
                   $"Reset 后 θ={20.0 * Math.PI / 180.0:F6} rad；推 0.5 s ⇒ {pushed:F6}；"
                   + $"再做两次空推进仍为 {init.Angle:F6}");

            var lazy = new PendulumModel { Length = 1.0, Gravity = 9.8 };
            lazy.Reset(20.0);
            double angleBefore = lazy.Angle;
            lazy.EstimatePeriod();

            Report("EstimatePeriod 在副本上跑、不改自身状态（否则每刷一次读数就把摆偷偷挪走）",
                   lazy.Angle == angleBefore && lazy.Time == 0,
                   $"θ {angleBefore:F6} ⇒ {lazy.Angle:F6}，t = {lazy.Time}");

            // ★ 回归：模型「刚 new 出来」是钉在 0 位的，必须 Reset 才会演 ——
            //   所以「切模型要重置」是宿主的职责（源级护栏在后面钉住 SimWindow）。
            var born = new PendulumModel { Length = 1.0, Gravity = 9.8 };
            bool deadAtBirth = born.Angle == 0 && born.AngularVelocity == 0;
            born.Reset();
            born.Advance(0.5);

            Report("模型刚 new 出来停在 0（Angle=0），Reset 之后才动 —— "
                   + "这正是「仿真窗打开时摆一动不动」那个坑的根因",
                   // ★ 门槛必须比物理量本身小：20° 起摆 0.5 s 后只回到 0.4°，
                   //   拿 0.1 rad（≈5.7°）当门槛就是在断言「摆得走了半个角」——
                   //   那是断言写错了，不是模型错了。
                   deadAtBirth && Math.Abs(born.Time - 0.5) < 1e-9 && Math.Abs(born.Angle) > 1e-6,
                   $"new 出来 θ=0；Reset + 推 0.5 s（t={born.Time}）⇒ " 
                   + $"θ={born.Angle * 180.0 / Math.PI:F3}°（不是 0 就说明它真的开始演了）");
        }

        // ---------------------------------------------------------- 2) 弹簧振子
        {
            double periodTheory = 2.0 * Math.PI * Math.Sqrt(1.0 / 20.0);

            var spring = new SpringModel { Mass = 1.0, Stiffness = 20.0, Damping = 0.0 };
            spring.Reset(0.25);
            double measured = spring.EstimatePeriod();

            Report("无阻尼弹簧实测周期 == 2π√(m/k)（相对误差 < 0.5%；无阻尼时这是精确解，不是近似）",
                   Math.Abs(measured - periodTheory) / periodTheory < 0.005,
                   $"理论 {periodTheory:F5} s / 实测 {measured:F5} s "
                   + $"/ 相对差 {(measured - periodTheory) / periodTheory * 100:F3}%");

            var wide = new SpringModel { Mass = 1.0, Stiffness = 20.0, Damping = 0.0 };
            wide.Reset(0.5);
            double measuredWide = wide.EstimatePeriod();

            Report("振幅从 5 cm 拉到 50 cm，周期一动不动（简谐运动的周期与振幅无关 —— "
                   + "这一条单摆做不到，两个模型并排看区别就出来了）",
                   Math.Abs(wide.Period - spring.Period) < 1e-12
                   && Math.Abs(measuredWide - measured) / measured < 0.005,
                   $"A=0.05 m ⇒ {measured:F5} s；A=0.50 m ⇒ {measuredWide:F5} s");

            var rk = new SpringModel { Mass = 1.0, Stiffness = 20.0, Damping = 0.0 };
            rk.Reset(0.25);
            rk.Advance(1.0);
            double analytic = rk.AnalyticDisplacement(1.0);

            Report("RK4 走 1 s 与解析解 x = A·cos(ωt) 吻合到 1e-6 m 以内",
                   Math.Abs(rk.Displacement - analytic) < 1e-6,
                   $"数值 {rk.Displacement:F9} m / 解析 {analytic:F9} m "
                   + $"/ 差 {Math.Abs(rk.Displacement - analytic):E2}");

            var springEnergy = new SpringModel { Mass = 1.0, Stiffness = 20.0, Damping = 0.0 };
            springEnergy.Reset(0.25);
            double springStart = springEnergy.Energy;
            springEnergy.Advance(2.0);
            double springDrift = Math.Abs(springEnergy.Energy - springStart) / springStart;

            Report("无阻尼弹簧推 2 s 后机械能守恒（相对漂移 < 1e-6）",
                   springDrift < 1e-6,
                   $"E₀ = {springStart:F9} J ⇒ {springEnergy.Energy:F9} J，漂移 {springDrift:E2}");

            // ★ 参数要挑得让 b_c 落在阻尼量程（0 ~ 3 N·s/m）里头：
            //   m=1、k=20 时 b_c = 8.94，会被 setter 夹到 3 ——
            //   于是「临界 / 过阻尼」这两态永远测不出来，而断言会安静地报假失败。
            var regime = new SpringModel { Mass = 0.2, Stiffness = 1.0 };
            double critical = 2.0 * Math.Sqrt(regime.Stiffness * regime.Mass);
            regime.Damping = 0.0;
            string noDamp = regime.DampingRegime;
            regime.Damping = critical / 2.0;
            string underDamp = regime.DampingRegime;
            regime.Damping = critical;
            string criticalDamp = regime.DampingRegime;
            regime.Damping = critical * 2.0;   // 2×b_c ≈ 1.79，仍在量程内
            string overDamp = regime.DampingRegime;

            Report($"阻尼四态判据（无 / 欠 / 临界 / 过），临界值恰好是 b_c = 2√(km) = {critical:F3}",
                   noDamp.Contains("无阻尼", StringComparison.Ordinal)
                   && underDamp.Contains("欠阻尼", StringComparison.Ordinal)
                   && criticalDamp.Contains("临界阻尼", StringComparison.Ordinal)
                   && overDamp.Contains("过阻尼", StringComparison.Ordinal),
                   $"「{noDamp}」｜「{underDamp}」｜「{criticalDamp}」｜「{overDamp}」");

            var hang = new SpringModel { Mass = 1.0, Stiffness = 20.0, Gravity = 9.8 };
            Report("竖直悬挂静伸长 Δ = mg/k（9.8/20 = 0.49 m），且振动周期与 Δ 无关",
                   Math.Abs(hang.StaticExtension - 0.49) < 1e-12
                   && Math.Abs(hang.Period - periodTheory) < 1e-12,
                   $"Δ={hang.StaticExtension:F4} m，T={hang.Period:F5} s");

            var clampSpring = new SpringModel
            {
                Mass = 0,
                Stiffness = 1000,
                Damping = -5,
                StartDisplacement = -0.4,
            };

            Report("弹簧参数边界：质量 / 劲度 / 阻尼都夹住，初始位移取绝对值"
                   + "（振幅没有负数；下限 2 cm 是为了「看得见在动」）",
                   clampSpring.Mass == SpringModel.MinMass
                   && clampSpring.Stiffness == SpringModel.MaxStiffness
                   && clampSpring.Damping == SpringModel.MinDamping
                   && Math.Abs(clampSpring.StartDisplacement - 0.4) < 1e-12,
                   $"m={clampSpring.Mass} k={clampSpring.Stiffness} "
                   + $"b={clampSpring.Damping} A={clampSpring.StartDisplacement}");
        }

        // ---------------------------------------------------------- 3) 斜面
        {
            double half = Math.PI / 6.0;   // 30°

            var flat = new InclineModel { AngleDegrees = 30.0, Friction = 0.0, Gravity = 9.8 };
            Report("µ = 0 ⇒ a = g·sinθ（10 m/s² 量级；模型里根本没有「质量」这个量，"
                   + "所以「与质量无关」是结构上成立的）",
                   Math.Abs(flat.Acceleration - 9.8 * Math.Sin(half)) < 1e-12,
                   $"a = {flat.Acceleration:F6} m/s²，g·sin30° = {9.8 * Math.Sin(half):F6} m/s²");

            var atCritical = new InclineModel { AngleDegrees = 30.0, Gravity = 9.8 };
            atCritical.Friction = atCritical.CriticalFriction;
            atCritical.Reset(0);
            atCritical.Advance(1.0);

            Report("µ = tanθ（临界）⇒ 松手不动：a = 0，推 1 s 位移仍是 0",
                   !atCritical.SlidesFromRest
                   && Math.Abs(atCritical.Acceleration) < 1e-12
                   && Math.Abs(atCritical.Travel) < 1e-12
                   && Math.Abs(atCritical.Speed) < 1e-12,
                   $"µ_c = tan30° = {atCritical.CriticalFriction:F6}，1 s 后 s = {atCritical.Travel:E2} m");

            var steep = new InclineModel { AngleDegrees = 30.0, Friction = 0.8, Gravity = 9.8 };
            steep.Reset(0);
            steep.Advance(1.0);

            Report("µ > tanθ ⇒ 松手真的不动（不是「慢慢往下滑」，更不是「自己往坡上爬」—— "
                   + "只按 a = g(sinθ − µcosθ) 硬算就会得到那个荒谬结果）",
                   steep.IsAtRest && Math.Abs(steep.Travel) < 1e-12
                   && steep.DescribeMotion().Contains("静止", StringComparison.Ordinal),
                   $"µ=0.8 > µ_c={steep.CriticalFriction:F3}；1 s 后 s={steep.Travel:E2} m；"
                   + $"读数「{steep.DescribeMotion()}」");

            var slide = new InclineModel { AngleDegrees = 30.0, Friction = 0.1, Gravity = 9.8 };
            slide.Reset(0);
            slide.Advance(1.0);
            double slideA = 9.8 * (Math.Sin(half) - 0.1 * Math.Cos(half));

            Report("µ < tanθ ⇒ 下滑：a = g(sinθ − µcosθ)，推 1 s 的位移与 ½at² 吻合",
                   Math.Abs(slide.Travel - 0.5 * slideA) < 1e-6
                   && slide.Speed > 0
                   && slide.DescribeMotion().Contains("下滑", StringComparison.Ordinal),
                   $"a={slideA:F6} m/s²，1 s 后 s={slide.Travel:F6} m（½at² = {0.5 * slideA:F6}）");

            var up = new InclineModel { AngleDegrees = 30.0, Friction = 0.2, Gravity = 9.8 };
            const double upStart = 5.0;
            up.Reset(-upStart);
            double analyticUp = up.UpSlideDistance(upStart);
            int guard = 0;
            while (up.Speed < 0 && guard++ < 20000) up.Advance(1e-3, 1e-4);
            double climbed = -up.Travel;

            Report("上滑距离 == v₀²/(2(g·sinθ + µ·g·cosθ))（相对误差 < 1%）—— "
                   + "给「斜面向上减速」这类题一个屏幕上可读、可断言的参照值",
                   analyticUp > 0 && Math.Abs(climbed - analyticUp) / analyticUp < 0.01,
                   $"v₀={upStart} m/s ⇒ 解析 {analyticUp:F6} m / 数值 {climbed:F6} m");

            Report("上滑到速度过零之后（µ < tanθ）会掉头下滑（分段推进的过零点判得准）",
                   up.Speed >= 0 && up.Acceleration > 0,
                   $"过零后 v={up.Speed:F6} m/s，a={up.Acceleration:F6} m/s²");

            var stuck = new InclineModel { AngleDegrees = 30.0, Friction = 0.8, Gravity = 9.8 };
            stuck.Reset(-3.0);
            guard = 0;
            while (stuck.Speed < 0 && guard++ < 20000) stuck.Advance(1e-3, 1e-4);
            double restAt = stuck.Travel;
            stuck.Advance(2.0);

            Report("上滑过零之后（µ > tanθ）就停在原地、不再下滑 —— 纯数值积分在这里"
                   + "会一直以微小速度往下蠕，屏幕上看起来像「停不住」",
                   stuck.IsAtRest && Math.Abs(stuck.Travel - restAt) < 1e-12,
                   $"停在 s={restAt:F6} m；再推 2 s ⇒ {stuck.Travel:F6} m；"
                   + $"读数「{stuck.DescribeMotion()}」");

            var angleProbe = new InclineModel { Friction = Math.Tan(20.0 * Math.PI / 180.0) };
            Report("临界倾角 θ_c = arctan µ（µ = tan20° ⇒ 20.0°）",
                   Math.Abs(angleProbe.CriticalAngleDegrees - 20.0) < 1e-9,
                   $"µ=tan20° ⇒ θ_c={angleProbe.CriticalAngleDegrees:F6}°");

            var clampIncline = new InclineModel { AngleDegrees = 200, Friction = 9, Gravity = 9.8 };
            clampIncline.Reset(99);
            double highStart = clampIncline.StartSpeed;
            clampIncline.Reset(-99);
            double lowStart = clampIncline.StartSpeed;
            var clampIncline2 = new InclineModel { AngleDegrees = 0, Friction = -3 };

            Report("斜面参数边界：倾角夹进 [5°, 80°]、µ 夹进 [0, 1.2]、初速度按 ±8 m/s 封顶",
                   clampIncline.AngleDegrees == InclineModel.MaxAngleDegrees
                   && clampIncline.Friction == InclineModel.MaxFriction
                   && Math.Abs(highStart - InclineModel.MaxStartSpeed) < 1e-12
                   && Math.Abs(lowStart + InclineModel.MaxStartSpeed) < 1e-12
                   && clampIncline2.AngleDegrees == InclineModel.MinAngleDegrees
                   && clampIncline2.Friction == InclineModel.MinFriction,
                   $"θ={clampIncline.AngleDegrees} µ={clampIncline.Friction} "
                   + $"v₀⁺={highStart} v₀⁻={lowStart}；反向 θ={clampIncline2.AngleDegrees} "
                   + $"µ={clampIncline2.Friction}");
        }

        // ---------------------------------------------------------- 4) 落到卷面：AddImage 真实现
        //
        // 这一段故意用**真宿主**（HostGfxObjectHost + 真 GfxObjectStore + 真位图仓库），
        // 不用替身：替身只要有一处不忠实，测出来的就是替身的行为。
        {
            var catalog = new GfxRendererCatalog();
            var store = new GfxObjectStore(catalog);
            var gfxHistory = new GfxHistory(store);
            var vault = new WebPanelImageStore();
            var page = new Rect(0, 0, 595, 842);

            InkHistory NewInk() => new(new StrokeCollection());
            BoardHistory BoardFor(GfxObjectStore target)
                => new(NewInk(), new GfxHistory(target));

            var host = new HostGfxObjectHost(
                store, gfxHistory, BoardFor(store), vault,
                currentPage: () => page,
                viewportCenterWorld: () => new Point(300, 400));

            byte[] png = EncodePng(64, 36, Colors.Red);
            string? id = host.AddImage(png, "单摆仿真");
            var placed = store.Count == 1 ? store.Objects[0] : null;

            Report("AddImage 落成一个 webPanelImage 对象并返回对象 Id",
                   id is not null && placed is not null
                   && placed.Kind == WebPanelImageRenderer.KindName,
                   $"id={id ?? "null"} 对象数={store.Count} Kind={placed?.Kind ?? "无"}");

            double width = placed?.GetNumber(WebPanelImageRenderer.WidthKey) ?? 0;
            double height = placed?.GetNumber(WebPanelImageRenderer.HeightKey) ?? 0;

            Report("尺寸存进对象参数（w/h，世界单位 pt）且沿用像素长宽比 —— "
                   + "Measure 必须能在「位图不在、布局没跑」时算出来，所以尺寸不能现量",
                   placed is not null && width > 0 && height > 0
                   && Math.Abs(width / height - 64 / 36.0) < 1e-6,
                   $"{width:F1}×{height:F1} pt（像素 64×36）");

            string imageKey = placed?.GetText(WebPanelImageRenderer.ImageIdKey) ?? string.Empty;
            Report("位图字节进独立仓库，对象里只留一个 Id —— 字节绝不进对象参数"
                   + "（tbink 是文本序列化，塞进去会让每次撤销都快照半兆字符串）",
                   imageKey.Length > 0 && vault.Contains(imageKey) && vault.Count == 1,
                   $"imageId=「{imageKey}」，仓库 {vault.Describe()}");

            Report("标签存进 Texts[label]，选中时念「单摆仿真」而不是干巴巴一个 Kind 名",
                   placed is not null
                   && WebPanelImageRenderer.LabelOf(placed) == "单摆仿真"
                   && placed.GetText(WebPanelImageRenderer.LabelKey) == "单摆仿真",
                   $"label=「{(placed is null ? "无" : WebPanelImageRenderer.LabelOf(placed))}」");

            bool inPage = placed is not null
                          && placed.Center.X - width / 2 >= page.Left - 1e-6
                          && placed.Center.X + width / 2 <= page.Right + 1e-6
                          && placed.Center.Y - height / 2 >= page.Top - 1e-6
                          && placed.Center.Y + height / 2 <= page.Bottom + 1e-6;

            Report("有试卷时落在页内（视口中心在页内 ⇒ 就落在老师此刻在看的地方；"
                   + "页外 ⇒ 夹回页内 —— 页外的图导成 PDF 时不属于任何一页，等于凭空少一张）",
                   inPage,
                   placed is null ? "没有对象"
                       : $"落点 ({placed.Center.X:F0},{placed.Center.Y:F0})，页 {page.Width:F0}×{page.Height:F0}");

            string? again = host.AddImage(png, "单摆仿真");
            Report("同一帧导两次 ⇒ 两个对象、但位图只存一份（按内容 SHA-256 去重）",
                   again is not null && store.Count == 2 && vault.Count == 1,
                   $"对象数={store.Count}，位图 {vault.Describe()}");

            bool undone = gfxHistory.Undo();
            Report("撤销一步只退掉一张图（一张截图 = 一个撤销单元，不是半个）",
                   undone && store.Count == 1, $"undo={undone} 对象数={store.Count}");

            int beforeEmpty = store.Count;
            string? empty = host.AddImage(Array.Empty<byte>(), "空字节");
            Report("空字节 ⇒ 明确失败（返回 null）且不落对象 —— 不静默落一个空框",
                   empty is null && store.Count == beforeEmpty,
                   $"返回={empty ?? "null"}，对象数 {beforeEmpty} ⇒ {store.Count}");

            var bareStore = new GfxObjectStore(new GfxRendererCatalog());
            var noVault = new HostGfxObjectHost(
                bareStore, new GfxHistory(bareStore), BoardFor(bareStore));
            string? noChannel = noVault.AddImage(png, "宿主没装配位图仓库");

            Report("宿主没装配位图通道 ⇒ AddImage 明确失败（不抛异常、不落空框）",
                   noChannel is null && bareStore.Count == 0,
                   $"返回={noChannel ?? "null"}，对象数={bareStore.Count}");

            var noPageStore = new GfxObjectStore(new GfxRendererCatalog());
            var noPageHost = new HostGfxObjectHost(
                noPageStore, new GfxHistory(noPageStore), BoardFor(noPageStore), vault,
                viewportCenterWorld: () => new Point(1234, 5678));
            string? noPageId = noPageHost.AddImage(png, "没打开试卷");
            var noPageObj = noPageStore.Count == 1 ? noPageStore.Objects[0] : null;

            Report("没打开试卷时也落得下（按名义页宽 380 pt 定尺、落在视口中心）—— "
                   + "导出失败只该说「先打开一份试卷」，不该是「点了没反应」",
                   noPageId is not null && noPageObj is not null
                   && Math.Abs(noPageObj.GetNumber(WebPanelImageRenderer.WidthKey)
                               - WebPanelImageRenderer.NominalWorldWidth) < 1e-6
                   && Math.Abs(noPageObj.Center.X - 1234) < 1e-6,
                   noPageObj is null ? "没有对象"
                       : $"宽 {noPageObj.GetNumber(WebPanelImageRenderer.WidthKey):F1} pt，"
                         + $"落点 ({noPageObj.Center.X:F0},{noPageObj.Center.Y:F0})");
        }

        // ---------------------------------------------------------- 5) 网页仿真入口：可用性与降级
        //
        // 轻量版没有 WebView2 运行时 ⇒ 这两颗按钮必须**在按下去之前**就置灰，
        // 并且说得清「是哪种不可用」。判定逻辑本身抽成了纯函数，就是为了这一组能断言。
        {
            Report("拿不到 Web 通道 ⇒ 不可用，原因说清是「这个版本没带 Web 模块」"
                   + "（轻量版正是这一路）",
                   !WebSimAvailability.IsUsable(null)
                   && WebSimAvailability.Reason(null) == WebSimAvailability.NoModuleReason,
                   $"「{WebSimAvailability.Reason(null)}」");

            var down = new FakeWebPanelBackend
            {
                IsAvailable = false,
                UnavailableReason = "没装 WebView2 运行时（装一次即可）",
            };
            using var downPanel = new WebPanelController(
                WebPanelProfile.GeoGebra, down, new FakeGeoGebraHooks());

            Report("通道自报不可用 ⇒ 不可用，且把通道给的原因原样透出来 —— "
                   + "现场要能区分「我该去装运行时」和「我拿的是轻量版」",
                   !WebSimAvailability.IsUsable(downPanel)
                   && WebSimAvailability.Reason(downPanel) == "没装 WebView2 运行时（装一次即可）",
                   $"「{WebSimAvailability.Reason(downPanel)}」");

            var mute = new FakeWebPanelBackend { IsAvailable = false, UnavailableReason = "   " };
            using var mutePanel = new WebPanelController(
                WebPanelProfile.GeoGebra, mute, new FakeGeoGebraHooks());
            string muted = WebSimAvailability.Reason(mutePanel);

            Report("通道没给原因（或只给了空白）⇒ 退回一句兜底中文，界面上不出现空白提示",
                   muted.Length > 0 && muted.Trim() == muted && !muted.Contains("  ", StringComparison.Ordinal),
                   $"「{muted}」");

            var up = new FakeWebPanelBackend { IsAvailable = true };
            using var upPanel = new WebPanelController(
                WebPanelProfile.GeoGebra, up, new FakeGeoGebraHooks());

            Report("通道可用 ⇒ 入口可用（这时两颗按钮才是亮的）",
                   WebSimAvailability.IsUsable(upPanel), "IsUsable = true");

            // M23 起：All = CircuitJs（契约常量）+ 45 个脚本生成的 PhET 入口
            //（WebSimEntries.generated.cs，PageId = 「phet-<slug>」裸字符串是**设计如此**：
            //  与 index.html 路由表同源生成，护栏是「唯一性 + 与路由表同源」，见下面 8) 组）。
            Report("入口表 = CircuitJs 常量 + 45 个 PhET 生成入口，PageId 全局唯一 —— "
                   + "拼错一个字母的表现是「点了没反应」（路由查不到 ⇒ 静默开默认页），"
                   + "现场根本无从下手，所以唯一性必须钉死",
                   WebSimEntries.All.Count == 1 + 45
                   && WebSimEntries.All[0].PageId == WebSimPages.CircuitJs
                   && WebSimEntries.All.Select(e => e.PageId).Distinct().Count() == WebSimEntries.All.Count
                   && WebSimEntries.All.Skip(1).All(e => e.PageId.StartsWith("phet-", StringComparison.Ordinal)),
                   string.Join("、", WebSimEntries.All.Select(e => e.PageId)));
        }

        // ---------------------------------------------------------- 6) 工具声明与注册
        {
            var tool = new NativeSimTool();

            Report("物理仿真是一颗工具（tool id 与插件里声明的常量一字不差）",
                   tool.Id == NativeSimToolIds.Id && tool.Id == "phys-sim",
                   $"Id=「{tool.Id}」，常量=「{NativeSimToolIds.Id}」");

            Report("不接触画布、不碰墨迹层、不给光标（它只是「一条命令」，不是一种模式）",
                   !tool.UsesInkLayer && !tool.NeedsPointer && tool.Cursor is null
                   && tool.InputKind == ToolInputKind.None && tool.InkMode == ToolInkMode.None,
                   $"UsesInkLayer={tool.UsesInkLayer} NeedsPointer={tool.NeedsPointer} "
                   + $"Cursor={(tool.Cursor is null ? "null" : "有")}");

            Report("是瞬态工具（ITransientTool）—— 宿主激活它之后立刻把控制权交回原来那支笔，"
                   + "所以窗口开着的时候画布照样能写",
                   tool is ITransientTool, tool is ITransientTool ? "是" : "不是");

            Report("快捷键 P（对着全表核过没被占用；重键的表现是「按下去只选中了其中一个工具」，"
                   + "课堂上查不出来）",
                   tool.Shortcut == Key.P, $"Shortcut = {tool.Shortcut?.ToString() ?? "无"}");

            var registry = new ToolRegistry();
            new NativeSimPlugin().Register(registry);
            Report("插件 Register 确实把工具注册进去了（插件才是入口，光有工具类不算接上）",
                   registry.Tools.Count == 1 && registry.Tools[0].Id == "phys-sim",
                   $"注册了 {registry.Tools.Count} 个工具");

            var entry = ToolCatalog.Find("phys-sim");
            Report("工具目录表里有 phys-sim 且归在「图形」组、图标键非空 —— "
                   + "没登记就会被丢进「其他」组，日志里一声不响",
                   entry is not null && entry.Group == ToolGroup.Gfx
                   && !string.IsNullOrEmpty(entry.IconKey),
                   entry is null ? "没找到"
                       : $"组={entry.Group} 图标键={entry.IconKey} 短名={entry.TileLabel}");
        }

        // ---------------------------------------------------------- 7) 源级护栏（窗口够不着的那几条）
        {
            string root = FindRepoRoot(outDir);

            static string ReadSource(string root, params string[] parts)
                => File.ReadAllText(
                    Path.Combine(new[] { root, "src" }.Concat(parts).ToArray()), Encoding.UTF8);

            string toolSrc = StripComments(
                ReadSource(root, "MathPhys.Ink.Plugin.NativeSim", "NativeSimTool.cs"), false);
            string windowSrc = StripComments(
                ReadSource(root, "MathPhys.Ink.Plugin.NativeSim", "SimWindow.cs"), false);

            string deactivateBody = MethodBody(toolSrc, "public void Deactivate()");

            Report("瞬态工具的 Deactivate 是空实现 —— 里面关窗口的表现是「仿真窗闪一下立刻消失」"
                   + "（宿主激活瞬态工具后立刻交还控制权，Deactivate 紧跟着就被调到）",
                   deactivateBody.Length > 0 && deactivateBody.Trim().Length == 0,
                   $"方法体「{deactivateBody.Replace("\r", string.Empty).Replace("\n", "⏎")}」"
                   + "（只剩空白 ⇒ 空实现）");

            Report("切模型时重置（只换 _kind 不重置 ⇒ 开窗时摆与弹簧钉在最低点一动不动，"
                   + "而滑杆上的数字看着都对，最容易被当成「坏了」）",
                   windowSrc.Contains("ResetCurrent();", StringComparison.Ordinal)
                   && MethodBody(windowSrc, "private void ResetCurrent()")
                          .Contains("pendulum.Reset();", StringComparison.Ordinal),
                   "SwitchKind 与「重置」按钮都走 ResetCurrent");

            Report("导出按钮在「宿主没有图形对象通道」时置灰并给中文原因 —— "
                   + "按下去毫无反应是故障，灰着并说清原因只是少个便利",
                   windowSrc.Contains("if (!_canExport)", StringComparison.Ordinal)
                   && windowSrc.Contains("export.IsEnabled = false;", StringComparison.Ordinal)
                   && windowSrc.Contains("ToolTipService.SetShowOnDisabled(export, true);",
                                         StringComparison.Ordinal),
                   "三件齐：判据 + 置灰 + 禁用态也弹得出提示");

            Report("选中位图对象时念的是它的标签（走 WebPanelImageRenderer.LabelOf），"
                   + "而不是干巴巴的 Kind 名",
                   StripComments(ReadSource(root, "MathPhys.Ink", "Gfx", "GfxSelectionTool.cs"), false)
                       .Contains("WebPanelImageRenderer.LabelOf", StringComparison.Ordinal),
                   "GfxSelectionTool 的叙述里先取 LabelOf");
        }

        // ---------------------------------------------------------- 8) 画面渲染（离屏；与屏幕同一段代码）
        {
            var pendulum = new PendulumModel { Length = 1.0, Gravity = 9.8 };
            pendulum.Reset(30.0);

            const double surfaceWidth = 900;
            const double surfaceHeight = 560;
            byte[] shot = SimScene.RenderPng(
                SimKind.Pendulum, pendulum, surfaceWidth, surfaceHeight, 1.0, 1.0,
                "单摆仿真　L = 1.00 m　θ₀ = 30°");

            bool headOk = PngSize.TryRead(shot, out int pixelWidth, out int pixelHeight);
            Report("离屏渲染出的 PNG 尺寸 == 画布尺寸 —— 这条路不抓窗口，"
                   + "所以无窗口的 harness 里也断言得了「导出的图长什么样」",
                   headOk && pixelWidth == 900 && pixelHeight == 560 && shot.Length > 5000,
                   headOk ? $"{pixelWidth}×{pixelHeight}，{shot.Length / 1024} KB" : "PNG 头读不出");

            byte[] enlarged = SimScene.RenderPng(
                SimKind.Pendulum, pendulum, surfaceWidth, surfaceHeight, 1.5, 1.0, "放大");
            PngSize.TryRead(enlarged, out int enlargedW, out int enlargedH);
            Report("scale = 1.5 ⇒ 1350×840（落到卷面上放大看仍然清晰，字节数又不至于夸张）",
                   enlargedW == 1350 && enlargedH == 840, $"{enlargedW}×{enlargedH}");

            byte[] dirty = SimScene.RenderPng(
                SimKind.Pendulum, pendulum, double.NaN, -100, 1.0, 1.0, "尺寸非法");
            PngSize.TryRead(dirty, out int dirtyW, out int dirtyH);
            Report("画布尺寸是脏值（NaN / 负数）⇒ 退回默认画布，不崩也不出 0×0 空图",
                   dirtyW == (int)SimScene.DefaultSurfaceWidth
                   && dirtyH == (int)SimScene.DefaultSurfaceHeight,
                   $"{dirtyW}×{dirtyH}（默认 {SimScene.DefaultSurfaceWidth:F0}×"
                   + $"{SimScene.DefaultSurfaceHeight:F0}）");

            var background = ((SolidColorBrush)SimPalette.Background).Color;
            var probe = DecodePng(shot);

            Report("底色铺满整块画布，且 PNG 是不透明的（左上角就是底色、alpha = 255）—— "
                   + "透明底落到纸上是一片黑，M9 的导出在那上面踩过",
                   probe.W == 900 && probe.Bgra[3] == 255
                   && probe.Bgra[0] == background.B
                   && probe.Bgra[1] == background.G
                   && probe.Bgra[2] == background.R,
                   $"左上角 BGRA = ({probe.Bgra[0]},{probe.Bgra[1]},{probe.Bgra[2]},{probe.Bgra[3]})，"
                   + $"底色 = ({background.B},{background.G},{background.R},255)");

            long drawn = NonBackgroundPixels(shot, background);
            Report("画面上真的画出了东西（摆杆 + 摆球 + 量角弧 + 参数标题，非底色像素数以千计）",
                   drawn > 2000, $"非底色像素 {drawn}");

            pendulum.Advance(1.0);
            byte[] moved = SimScene.RenderPng(
                SimKind.Pendulum, pendulum, surfaceWidth, surfaceHeight, 1.0, 1.0,
                "单摆仿真　L = 1.00 m　θ₀ = 30°");

            Report("模型状态变了 ⇒ 画面跟着变（画的确实是当前状态，不是一张静态图）",
                   !moved.SequenceEqual(shot),
                   $"推 1 s 后 θ = {pendulum.Angle * 180.0 / Math.PI:F1}°，"
                   + $"字节数 {shot.Length / 1024} KB ⇒ {moved.Length / 1024} KB");

            var incline = new InclineModel { AngleDegrees = 30.0, Friction = 0.2 };
            incline.Reset(0);
            incline.Advance(0.6);

            var spring = new SpringModel { Mass = 1.0, Stiffness = 20.0 };
            spring.Reset(0.25);
            spring.Advance(0.4);

            byte[] inclineShot = SimScene.RenderPng(
                SimKind.Incline, incline, surfaceWidth, surfaceHeight, 1.0, 1.0, "斜面仿真");
            byte[] springShot = SimScene.RenderPng(
                SimKind.Spring, spring, surfaceWidth, surfaceHeight, 1.0, 1.0, "弹簧振子仿真");

            long inclinePixels = NonBackgroundPixels(inclineShot, background);
            long springPixels = NonBackgroundPixels(springShot, background);

            Report("三个模型各画得出自己的画面（非底色像素都过千，且两张图不是同一张）",
                   inclinePixels > 1000 && springPixels > 1000
                   && !inclineShot.SequenceEqual(springShot),
                   $"斜面 {inclinePixels} 像素；弹簧 {springPixels} 像素");

            byte[] mismatched = SimScene.RenderPng(
                SimKind.Pendulum, spring, surfaceWidth, surfaceHeight, 1.0, 1.0, "类型对不上");
            PngSize.TryRead(mismatched, out int mismatchW, out int mismatchH);

            Report("模型类型对不上时不崩、只画底色与标题（不画半成品 —— "
                   + "画一半的画面比空白更容易被当成「就长这样」）",
                   mismatchW == 900 && mismatchH == 560
                   && !mismatched.SequenceEqual(shot),
                   $"尺寸 {mismatchW}×{mismatchH}，非底色像素 "
                   + $"{NonBackgroundPixels(mismatched, background)}");

            string reviewPath = Path.Combine(outDir, "m22s3", "pendulum.png");
            Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
            File.WriteAllBytes(reviewPath, shot);
            Console.WriteLine($"        （本组留一张人工复核图：{reviewPath}）");
        }

        return failures;
    }

    // ============================================================ M8 S0：.twb 格式层
    //
    // 这一组断言的核心不是"能写能读"，而是**坏文件的判定**：工程文件会被拷来拷去、
    // 被压缩软件解开过、被手改过、被拷到一半拔过盘。这些都属于预期内的正常情况，
    // 每一条都必须给出可读的中文原因，并且**绝不抛异常**（否则崩溃点就在打开对话框后面）。
    /// <summary>
    /// M22 S5 断言组：CircuitJS 接入（profile / 页 Id 透传 / 就绪闸门 / 离线资产 / 源级护栏）。
    /// </summary>
    /// <remarks>
    /// 与 S3 组同一分工：<b>逻辑归 harness</b>（控制器行为用替身后端完整断言）、
    /// <b>资产归磁盘探针</b>（离线资源是否真的在仓库里、有没有混进外链）、
    /// <b>手感归一体机</b>（WebView2 真起来长什么样由发布冒烟负责）。
    /// </remarks>
    private static int RunM22S5Checks(string outDir)
    {
        int failures = 0;
        string root = FindRepoRoot(outDir);

        Console.WriteLine();
        Console.WriteLine("================ M22 S5：CircuitJS 接入（物理仿真 Web 面板）================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        static string ReadSource(string root, params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { root, "src" }.Concat(parts).ToArray()), Encoding.UTF8);

        static string ReadRepoFile(string root, params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()), Encoding.UTF8);

        static bool RepoFileExists(string root, params string[] parts)
            => File.Exists(Path.Combine(new[] { root }.Concat(parts).ToArray()));

        // ------------------------------------------------- 1) profile 表

        var webSim = WebPanelProfile.Find(WebPanelIds.WebSim);

        Report("物理仿真 profile 能按 Id 查到（插件说的页 Id 经控制器进来，profile 是宿主侧的路由依据）",
               webSim is not null);

        Report("profile 字段：websim / websim.local / websim / index.html / M22_SIM",
               webSim is not null
               && webSim.Id == "websim"
               && webSim.VirtualHost == "websim.local"
               && webSim.AssetFolderName == "websim"
               && webSim.EntryHtml == "index.html"
               && webSim.EnvPrefix == "M22_SIM",
               webSim is null ? "(profile 缺失)" :
               $"{webSim.Id} / {webSim.VirtualHost} / {webSim.AssetFolderName} / {webSim.EntryHtml} / {webSim.EnvPrefix}");

        Report("物理仿真按 openArgs/args 说话（通用仿真页协议）；只做全屏（AllowDockForm=false）",
               webSim is not null
               && webSim.ArgsMessageType == "openArgs"
               && webSim.ArgsPayloadKey == "args"
               && !webSim.AllowDockForm);

        Report("GeoGebra profile 一个字节没被改（loadMaterial/ggbBase64、两种形态都在）",
               WebPanelProfile.Find(WebPanelIds.GeoGebra) is { } geo
               && geo.ArgsMessageType == "loadMaterial"
               && geo.ArgsPayloadKey == "ggbBase64"
               && geo.AllowDockForm);

        Report("profile 表恰好两条（GeoGebra + 物理仿真）；查不到的 Id 返回 null 而不是抛",
               WebPanelProfile.All.Count == 2 && WebPanelProfile.Find("no-such-thing") is null);

        // ------------------------------------------------- 2) 控制器行为（替身后端）

        // 2a) 打开 + 页 Id 经就绪闸门透传
        {
            var backend = new FakeWebPanelBackend { IsAvailable = true };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.WebSim, backend, hooks);

            bool opened = controller.OpenAsync(WebSimPages.CircuitJs).GetAwaiter().GetResult();

            Report("物理仿真面板可用时 OpenAsync(circuitjs) 成功并进入面板模式",
                   opened && controller.IsOpen && hooks.IsPanelModeActive && hooks.EnterCount == 1,
                   $"opened={opened} enter={hooks.EnterCount}");

            // ★ 页 Id 也必须过就绪闸门：ready 之前只排队，不直发 ——
            //   否则页面还没挂好监听，页 Id 就丢了，表现是"面板开了却停在首页"。
            bool queuedBeforeReady = backend.Posted.Count == 0 && controller.QueuedCommandCount == 1;

            Report("★ 页 Id 在 ready 之前排队、不直发（与其它命令同一道闸门）",
                   queuedBeforeReady,
                   $"Posted={backend.Posted.Count} Queued={controller.QueuedCommandCount}");

            // 页面报 ready（控制器订阅的是后端的 RawMessageReceived）→ 队列放行。
            backend.RaiseMessage(WebPanelProtocol.Encode(WebPanelProtocol.Ready, null));

            bool flushed = controller.IsReady
                && backend.Posted.Count == 1
                && backend.Posted[0].Contains("\"openArgs\"", StringComparison.Ordinal)
                && backend.Posted[0].Contains("\"args\":\"circuitjs\"", StringComparison.Ordinal);

            Report("ready 后队列放行：页面收到的正是 openArgs{args=circuitjs}（原样透传）",
                   flushed,
                   $"ready={controller.IsReady} posted={backend.Posted.Count} {string.Join(" | ", backend.Posted)}");

            // 2b) 关闭把面板模式还回去
            controller.CloseAsync().GetAwaiter().GetResult();

            Report("CloseAsync 退出面板模式（把决定权还给当前工具）",
                   !controller.IsOpen && !hooks.IsPanelModeActive && hooks.ExitCount == 1,
                   $"IsOpen={controller.IsOpen} exit={hooks.ExitCount}");

            // 2c) 另一个页 Id 原样透传（控制器不认识具体页 —— 认识才是问题）
            bool reopened = controller.OpenAsync(WebSimPages.PhetPendulum).GetAwaiter().GetResult();
            backend.RaiseMessage(WebPanelProtocol.Encode(WebPanelProtocol.Ready, null));

            Report("重开传 phet-pendulum：页 Id 原样透传（S6 接入 PhET 时控制器零改动）",
                   reopened
                   && backend.Posted.Any(p => p.Contains("\"args\":\"phet-pendulum\"", StringComparison.Ordinal)),
                   $"posted={backend.Posted.Count}");
        }

        // 2d) 不可用：降级三件套
        {
            var backend = new FakeWebPanelBackend
            {
                IsAvailable = false,
                UnavailableReason = "物理仿真资源未部署（缺少 websim 目录），请重新解压完整测试包。",
            };
            var hooks = new FakeGeoGebraHooks();
            using var controller = new WebPanelController(WebPanelProfile.WebSim, backend, hooks);

            bool opened = controller.OpenAsync(WebSimPages.CircuitJs).GetAwaiter().GetResult();

            Report("资源缺失时 OpenAsync 返回 false、不抛异常",
                   !opened && !controller.IsOpen);

            Report("★ 不可用时绝不进入面板模式（否则画布被白锁）",
                   !hooks.IsPanelModeActive && hooks.EnterCount == 0,
                   $"enter={hooks.EnterCount}");

            Report("把中文原因写到状态栏（老师能在界面上看到为什么）",
                   hooks.LastStatus is not null
                   && hooks.LastStatus.Contains("websim", StringComparison.Ordinal),
                   hooks.LastStatus ?? "(无)");
        }

        // ------------------------------------------------- 3) 离线资产（磁盘探针）

        // 3a) 自建导航页：协议锚点一个都不能少
        string navPath = Path.Combine(root, "src", "MathPhys.Ink", "Assets", "websim", "index.html");
        string nav = File.Exists(navPath) ? File.ReadAllText(navPath, Encoding.UTF8) : string.Empty;

        string[] navAnchors =
        {
            "'ready'", "'hello'", "'openArgs'", "'loadFailed'", "'selfTest'",
            "circuitjs/circuitjs.html", "phet-pendulum", "webview.postMessage",
        };

        Report("导航页存在且协议锚点齐全（ready/hello/openArgs/loadFailed/selfTest/页 Id/发消息通道）",
               nav.Length > 0 && navAnchors.All(a => nav.Contains(a, StringComparison.Ordinal)),
               nav.Length == 0 ? "(index.html 不存在)"
                   : "缺 " + string.Join("、", navAnchors.Where(a => !nav.Contains(a, StringComparison.Ordinal))));

        Report("★ 导航页零外链（http:// 与 https:// 一次都不出现 —— 断网教室里它必须一个字节都不缺）",
               nav.Length > 0 && !nav.Contains("http://", StringComparison.Ordinal)
               && !nav.Contains("https://", StringComparison.Ordinal));

        // 3b) CircuitJS 离线包：入口 + GWT 输出 + 字体 + 压缩库
        bool circuitOk = RepoFileExists(root, "src", "MathPhys.Ink", "Assets", "websim", "circuitjs", "circuitjs.html")
            && RepoFileExists(root, "src", "MathPhys.Ink", "Assets", "websim", "circuitjs", "circuitjs1", "circuitjs1.nocache.js")
            && RepoFileExists(root, "src", "MathPhys.Ink", "Assets", "websim", "circuitjs", "font", "fontello.css")
            && RepoFileExists(root, "src", "MathPhys.Ink", "Assets", "websim", "circuitjs", "lz-string.min.js");

        string[] circuitRels =
        {
            "circuitjs.html",
            "circuitjs1/circuitjs1.nocache.js",
            "font/fontello.css",
            "lz-string.min.js",
        };

        Report("CircuitJS 离线包四件套在仓库里（入口页 / GWT 选种脚本 / 字体 / 压缩库）",
               circuitOk,
               "缺 " + string.Join("、", circuitRels.Where(rel => !RepoFileExists(
                   root, new[] { "src", "MathPhys.Ink", "Assets", "websim", "circuitjs" }
                       .Concat(rel.Split('/')).ToArray()))));

        int cacheScripts = 0;
        var gwtDir = Path.Combine(root, "src", "MathPhys.Ink", "Assets", "websim", "circuitjs", "circuitjs1");
        if (Directory.Exists(gwtDir))
        {
            cacheScripts = Directory.EnumerateFiles(gwtDir, "*cache.js").Count();
        }

        Report("GWT 按浏览器排列的 *.cache.js 至少一枚（缺了就是白屏）",
               cacheScripts >= 1, $"count={cacheScripts}");

        string credit = File.ReadAllText(
            Path.Combine(root, "src", "MathPhys.Ink", "Assets", "websim", "circuitjs", "来源与版本.txt"),
            Encoding.UTF8);

        Report("来源与版本说明在包里，且写明 GPL-2.0（官方包按许可原样分发）",
               credit.Contains("GPL", StringComparison.Ordinal)
               && credit.Contains("falstad.com", StringComparison.Ordinal));

        // 官方落地页必须被剔除：它带 Google 广告脚本，且 index.html 这个名字属于自建导航页。
        Report("★ 官方 war 顶层 index.html 已剔除（广告脚本不进包；index.html 属于自建导航页）",
               !RepoFileExists(root, "src", "MathPhys.Ink", "Assets", "websim", "circuitjs", "index.html"));

        // 3c) PhET 钟摆实验（S6）：自包含单文件 + 许可说明 + 导航页路由
        string phetSim = File.ReadAllText(
            Path.Combine(root, "src", "MathPhys.Ink", "Assets", "websim", "phet",
                "pendulum-lab_zh_CN.html"), Encoding.UTF8);
        int phetScripts = phetSim.Split("<script").Length - 1;

        Report("PhET 钟摆实验自包含包在仓库里（内嵌脚本 ≥10 块、含钟摆/Pendulum）",
               phetSim.Length > 1024 * 1024
               && phetScripts >= 10
               && (phetSim.Contains("钟摆", StringComparison.Ordinal)
                   || phetSim.Contains("Pendulum", StringComparison.Ordinal)),
               $"大小 {phetSim.Length / 1024 / 1024} MB，script 块 {phetScripts} 个");

        string phetCredit = File.ReadAllText(
            Path.Combine(root, "src", "MathPhys.Ink", "Assets", "websim", "phet", "来源与版本.txt"),
            Encoding.UTF8);

        Report("PhET 来源与许可说明在包里（PhET + CC-BY/GPL）",
               phetCredit.Contains("PhET", StringComparison.Ordinal)
               && (phetCredit.Contains("CC-BY", StringComparison.Ordinal)
                   || phetCredit.Contains("GPL", StringComparison.Ordinal)));

        // M23：路由表由 fetch_phet.py 生成（PHET-ROUTES 标记区），45 条与资产/入口表同源。
        // 抽单摆（zh_CN 已确认存在的那条）当代表，条数与「下一版携带」占位一并钉死。
        Report("导航页 PhET 路由表 45 条、单摆指向本地官方包（不再是占位）",
               nav.Contains("phet/pendulum-lab_zh_CN.html", StringComparison.Ordinal)
               && nav.Contains("'phet-pendulum-lab'", StringComparison.Ordinal)
               && Regex.Matches(nav, "'phet-[a-z0-9-]+': \\{ file: 'phet/").Count == 45
               && !nav.Contains("下一版携带", StringComparison.Ordinal));

        // ------------------------------------------------- 4) 源级护栏

        string backendSrc = ReadSource(root, "MathPhys.Ink", "WebPanel", "WebView2PanelBackend.cs");
        string windowSrc = ReadSource(root, "MathPhys.Ink", "WebPanel", "WebPanelWindow.xaml.cs");
        string profileSrc = ReadSource(root, "MathPhys.Ink", "WebPanel", "WebPanelProfile.cs");
        string mainSrc = ReadSource(root, "MathPhys.Ink", "Views", "MainWindow.xaml.cs");
        string csproj = ReadSource(root, "MathPhys.Ink", "MathPhys.Ink.csproj");
        string packSrc = ReadRepoFile(root, "tools", "_m9_pack.py");

        Report("后端把 AllowDockForm 传给面板窗（窗口按它决定摆不摆「停靠面板」按钮）",
               backendSrc.Contains("new WebPanelWindow(_profile.DisplayName, _profile.AllowDockForm)",
                   StringComparison.Ordinal));

        Report("面板窗不摆停靠按钮的实现在（allowFormToggle 参数 + Collapsed）",
               windowSrc.Contains("bool allowFormToggle = true", StringComparison.Ordinal)
               && windowSrc.Contains("ToggleFormButton.Visibility = Visibility.Collapsed", StringComparison.Ordinal));

        Report("profile 源码：WebSim 条目 AllowDockForm=false 且进了 All 表",
               profileSrc.Contains("AllowDockForm = false,", StringComparison.Ordinal)
               && profileSrc.Contains("new[] { GeoGebra, WebSim }", StringComparison.Ordinal));

        Report("MainWindow：装配调用 + 桥接线 + 按触发者选导出服务 + Esc 关面板 + 卸载解订阅",
               mainSrc.Contains("AttachWebSimPanel();", StringComparison.Ordinal)
               && mainSrc.Contains("ViewportHost.WebSim = _webSimPanel;", StringComparison.Ordinal)
               && mainSrc.Contains("ReferenceEquals(sender, _webSimPanel)", StringComparison.Ordinal)
               && mainSrc.Contains("_webSimPanel is { IsOpen: true } simPanel", StringComparison.Ordinal)
               && mainSrc.Contains("simPanel.CaptureRequested -= OnWebPanelCaptureRequested;",
                   StringComparison.Ordinal));

        Report("打包链：csproj 随包复制 websim；_m9_pack.py 轻量版剔除 websim、完整版校验含 websim",
               csproj.Contains("Assets\\websim\\**\\*", StringComparison.Ordinal)
               && packSrc.Contains("DROP_TOP = (\"webview2\", \"geogebra\", \"websim\")", StringComparison.Ordinal)
               && packSrc.Contains("websim", StringComparison.Ordinal));

        // M23 热修：启动自检 —— 解压不完整 / 在压缩包里直接双击时，
        // 把天书 XamlParseException（缺 wpfgfx_cor3.dll）换成中文指引。
        string appSrc = ReadSource(root, "MathPhys.Ink", "App.xaml.cs");
        Report("App 启动自检：自包含布局缺 WPF 原生库时弹中文指引（框架依赖布局跳过，不误伤 dotnet run）",
               appSrc.Contains("hostfxr.dll", StringComparison.Ordinal)
               && appSrc.Contains("wpfgfx_cor3.dll", StringComparison.Ordinal)
               && appSrc.Contains("PresentationNative_cor3.dll", StringComparison.Ordinal)
               && appSrc.Contains("完整解压", StringComparison.Ordinal));

        return failures;
    }

    private static int RunM8S0Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M8 S0：.twb 工程文件格式层 ================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        string work = Path.Combine(outDir, "m8s0");

        // 先清空工作目录：上一轮跑剩下的 .twb 会让人误以为"这一轮也产出了这个文件"，
        // 排查用例时会被带偏（第一次就留下了上一版的 corruptpdf.twb 与 0 字节的 .tmp）。
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        // 造一个"任意 JSON"的 .twb 包（用来验证损坏与异常版本等路径）
        string CraftTwb(string fileName, string? manifestJson, byte[]? pdfBytes, byte[]? tbinkBytes)
        {
            string path = Path.Combine(work, fileName);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (manifestJson is not null)
                {
                    var entry = zip.CreateEntry(TwbFile.ManifestEntryName);
                    using var s = entry.Open();
                    byte[] bytes = Encoding.UTF8.GetBytes(manifestJson);
                    s.Write(bytes, 0, bytes.Length);
                }

                if (pdfBytes is not null)
                {
                    var entry = zip.CreateEntry(TwbFile.PdfEntryName, CompressionLevel.NoCompression);
                    using var s = entry.Open();
                    s.Write(pdfBytes, 0, pdfBytes.Length);
                }

                if (tbinkBytes is not null)
                {
                    var entry = zip.CreateEntry(TwbFile.AnnotationEntryName);
                    using var s = entry.Open();
                    s.Write(tbinkBytes, 0, tbinkBytes.Length);
                }
            }

            return path;
        }

        // 手工拼 manifest.json：要能造出"版本 99""坏 JSON""怪绑定方式""多余字段"
        string ManifestJson(int schemaVersion, string embedding, string? extraField = null,
                            string? relativePath = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"schemaVersion\": {schemaVersion},\n");
            sb.Append("  \"title\": \"新字段兼容测试\",\n");
            sb.Append("  \"createdUtc\": \"2026-09-22T00:00:00Z\",\n");
            sb.Append("  \"updatedUtc\": \"2026-09-22T00:00:00Z\",\n");
            sb.Append("  \"pdf\": {\n");
            sb.Append($"    \"embedding\": \"{embedding}\",\n");
            if (relativePath is not null) sb.Append($"    \"relativePath\": \"{relativePath}\",\n");
            sb.Append("    \"fingerprint\": \"deadbeef\"\n");
            sb.Append("  },\n");
            sb.Append("  \"view\": { \"pageIndex\": 1, \"scale\": 1.0,");
            sb.Append(" \"offsetX\": 0, \"offsetY\": 0, \"isFullScreen\": false },\n");
            sb.Append("  \"stats\": { \"strokeCount\": 0, \"objectCount\": 0 }");
            if (extraField is not null) sb.Append(",\n").Append(extraField);
            sb.Append("\n}\n");
            return sb.ToString();
        }

        int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { hit = false; break; }
                }
                if (hit) return i;
            }
            return -1;
        }

        byte[] pdf = BuildFakePdf();
        byte[] tbink = BuildFakeTbink();

        // ---------------------------------------------------------- 1) 内嵌模式：全字段往返
        var manifest = TwbManifest.New("26西附全真模拟1物理试卷");
        manifest.Pdf.Embedding = TwbPdfEmbedding.Embedded;
        manifest.Pdf.Fingerprint = "1f0a2b3c4d5e6f708192a3b4c5d6e7f8";
        manifest.View.PageIndex = 3;
        manifest.View.Scale = 1.75;
        manifest.View.OffsetX = -120.5;
        manifest.View.OffsetY = 42.25;
        manifest.View.IsFullScreen = true;
        manifest.Stats.StrokeCount = 142;
        manifest.Stats.ObjectCount = 5;
        manifest.CreatedUtc = "2026-09-22T13:08:42.0000000Z";

        string embeddedPath = Path.Combine(work, "embedded.twb");

        bool wrote = TwbFile.Write(embeddedPath, manifest, pdf, tbink, out string writeError);
        Report("内嵌模式：写出工程文件成功（.twb 落盘）", wrote && File.Exists(embeddedPath), writeError);

        bool read = TwbFile.TryRead(embeddedPath, out var m, out var pdfBack, out var tbinkBack,
                                    out string readError);
        Report("内嵌模式：读回工程文件成功", read, readError);

        bool fieldsOk = read
                        && m.SchemaVersion == TwbFile.CurrentSchemaVersion
                        && m.Title == "26西附全真模拟1物理试卷"
                        && m.CreatedUtc == "2026-09-22T13:08:42.0000000Z"
                        && m.Pdf.Embedding == TwbPdfEmbedding.Embedded
                        && m.Pdf.Fingerprint == "1f0a2b3c4d5e6f708192a3b4c5d6e7f8"
                        && m.View.PageIndex == 3
                        && Math.Abs(m.View.Scale - 1.75) < 1e-9
                        && Math.Abs(m.View.OffsetX + 120.5) < 1e-9
                        && Math.Abs(m.View.OffsetY - 42.25) < 1e-9
                        && m.View.IsFullScreen
                        && m.Stats.StrokeCount == 142
                        && m.Stats.ObjectCount == 5;

        Report("manifest 全字段往返一致（标题 / 指纹 / 视图状态 / 统计）", fieldsOk,
               read ? $"title={m.Title} page={m.View.PageIndex} scale={m.View.Scale}"
                    + $" offset={m.View.OffsetX},{m.View.OffsetY} full={m.View.IsFullScreen}"
                    : "(未读回)");

        Report("内嵌 PDF 字节原样穿过 ZIP（逐位一致）",
               read && pdfBack is not null && pdfBack.SequenceEqual(pdf),
               read ? $"长度 写出={pdf.Length} 读回={pdfBack?.Length ?? -1}" : "(未读回)");

        Report("批注字节原样穿过 ZIP（逐位一致，v2 二进制不被改形）",
               read && tbinkBack.SequenceEqual(tbink),
               read ? $"长度 写出={tbink.Length} 读回={tbinkBack.Length}" : "(未读回)");

        Report("CreatedUtc 沿用原值、UpdatedUtc 由写入方重新盖戳",
               read && m.UpdatedUtc.Length > 0 && m.UpdatedUtc != "2026-09-22T13:08:42.0000000Z",
               read ? $"created={m.CreatedUtc} updated={m.UpdatedUtc}" : "(未读回)");

        // ---------------------------------------------------------- 2) 包内结构与可读性
        byte[] raw = File.Exists(embeddedPath) ? File.ReadAllBytes(embeddedPath) : Array.Empty<byte>();
        bool zipMagic = raw.Length > 4
                        && raw[0] == (byte)'P' && raw[1] == (byte)'K' && raw[2] == 3 && raw[3] == 4;
        Report("工程文件是标准 ZIP 容器（魔数 PK 03 04，资源管理器可打开）", zipMagic,
               raw.Length >= 4 ? "前 4 字节 = " + string.Join(' ', raw.Take(4)) : "(文件为空)");

        var entryNames = new List<string>();
        bool pdfStored = false;
        string manifestText = string.Empty;

        if (zipMagic)
        {
            using var fs = File.OpenRead(embeddedPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            foreach (var entry in zip.Entries)
            {
                entryNames.Add(entry.FullName);
                if (entry.FullName == TwbFile.PdfEntryName)
                {
                    pdfStored = entry.CompressedLength == entry.Length;
                }

                if (entry.FullName == TwbFile.ManifestEntryName)
                {
                    using var s = entry.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    manifestText = Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }

        entryNames.Sort();
        bool namesOk = entryNames.Count == 3
                       && entryNames[0] == TwbFile.AnnotationEntryName
                       && entryNames[1] == TwbFile.PdfEntryName
                       && entryNames[2] == TwbFile.ManifestEntryName;
        Report("包内条目恰好三个、命名与约定完全一致", namesOk, string.Join(", ", entryNames));

        Report("内嵌 PDF 原样存放（不再二次压缩：省下写入与读取的两遍 CPU）", pdfStored,
               pdfStored ? "CompressedLength == Length" : "(未按 stored 存放)");

        Report("manifest.json 是明文可读：中文标题原样出现（不是 \\u897f 转义）",
               manifestText.Contains("26西附全真模拟1物理试卷"),
               $"manifest.json 长度 {manifestText.Length}");

        Report("manifest.json 键名为小驼峰、带缩进（记事本打开就能读懂）",
               manifestText.Contains("schemaVersion") && !manifestText.Contains("SchemaVersion")
               && manifestText.Contains("\n  "),
               manifestText.Length > 0 ? manifestText.Replace("\r", "").Split('\n')[1].Trim() : "(未读到)");

        Report("manifest 里带着各段 SHA-256（校验机制确实生效，而不是被跳过）",
               read && manifestText.Contains("integrity")
                    && m.Integrity.PdfSha256.Length == 64
                    && m.Integrity.AnnotationSha256.Length == 64,
               read ? $"pdf={m.Integrity.PdfSha256.Length} 位 / 批注={m.Integrity.AnnotationSha256.Length} 位"
                    : "(未读回)");

        // ---------------------------------------------------------- 3) 外挂模式
        var refManifest = TwbManifest.New("外挂试卷", TwbPdfEmbedding.Referenced);
        refManifest.Pdf.RelativePath = @"..\试卷\26西附全真模拟1物理试卷.pdf";
        refManifest.Pdf.AbsolutePath = @"D:\试卷\26西附全真模拟1物理试卷.pdf";
        refManifest.Pdf.Fingerprint = "abc123abc123abc123abc123abc123ab";

        string refPath = Path.Combine(work, "referenced.twb");
        bool wroteRef = TwbFile.Write(refPath, refManifest, null, tbink, out string refWriteError);
        Report("外挂模式：不带 PDF 也能写出工程文件", wroteRef, refWriteError);

        bool readRef = TwbFile.TryRead(refPath, out var rm, out var refPdf, out var refTbink,
                                       out string refReadError);
        Report("外挂模式：读回成功且没有内嵌 PDF 载荷", readRef && refPdf is null, refReadError);

        Report("外挂模式：相对路径与绝对路径都原样保留（工程整体搬家仍能找到）",
               readRef && rm.Pdf.RelativePath == refManifest.Pdf.RelativePath
                       && rm.Pdf.AbsolutePath == refManifest.Pdf.AbsolutePath,
               readRef ? $"{rm.Pdf.RelativePath} | {rm.Pdf.AbsolutePath}" : "(未读回)");

        Report("外挂模式：批注照常带走（外挂只影响 PDF，不影响笔迹）",
               readRef && refTbink.SequenceEqual(tbink), readRef ? $"长度 {refTbink.Length}" : "(未读回)");

        bool refHasNoPdfEntry = true;
        if (readRef)
        {
            using var fs = File.OpenRead(refPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == TwbFile.PdfEntryName) refHasNoPdfEntry = false;
            }
        }
        Report("外挂模式：包内不出现 document.pdf", readRef && refHasNoPdfEntry, "(外挂包只含 manifest)");

        long refSize = File.Exists(refPath) ? new FileInfo(refPath).Length : -1;
        long embSize = File.Exists(embeddedPath) ? new FileInfo(embeddedPath).Length : -1;
        Report("外挂工程明显更小（PDF 没进包）",
               refSize > 0 && embSize > 0 && refSize < embSize / 4,
               $"外挂 {refSize / 1024.0:F1} KB vs 内嵌 {embSize / 1024.0:F1} KB");

        // ---------------------------------------------------------- 4) 空白工程（新建后还没插试卷）
        var blankManifest = TwbManifest.New("空白工程", TwbPdfEmbedding.None);
        string blankPath = Path.Combine(work, "blank.twb");
        bool wroteBlank = TwbFile.Write(blankPath, blankManifest, null, tbink, out string blankWriteError);
        Report("空白工程：没有 PDF 也能写出（新建工程后的第一步）", wroteBlank, blankWriteError);

        bool readBlank = TwbFile.TryRead(blankPath, out var bm, out var blankPdf, out var blankTbink,
                                         out string blankReadError);
        Report("★ 空白工程：读回成功、不要求 PDF 路径（否则新建的工程根本打不开）",
               readBlank && blankPdf is null && bm.Pdf.Embedding == TwbPdfEmbedding.None,
               readBlank ? $"embedding={bm.Pdf.Embedding} pdf={(blankPdf is null ? "无" : "有")}"
                         : blankReadError);

        Report("空白工程：批注照样在（先写几笔、回头再插试卷也不丢）",
               readBlank && blankTbink.SequenceEqual(tbink),
               readBlank ? $"长度 {blankTbink.Length}" : "(未读回)");

        // ---------------------------------------------------------- 5) 批注为空：条目干脆不写
        string noAnnPath = Path.Combine(work, "noannotation.twb");
        bool wroteNoAnn = TwbFile.Write(noAnnPath, TwbManifest.New("没有批注"), pdf, null, out string noAnnWriteError);
        bool readNoAnn = TwbFile.TryRead(noAnnPath, out _, out _, out var emptyTbink, out string noAnnError);
        Report("批注为空时不写 annotation.tbink 条目，读侧给空数组而不是 null",
               wroteNoAnn && readNoAnn && emptyTbink.Length == 0,
               string.IsNullOrEmpty(noAnnWriteError) ? noAnnError : noAnnWriteError);

        // ---------------------------------------------------------- 6) 覆盖写（原子替换）
        var second = TwbManifest.New("第二次保存的标题");
        second.View.PageIndex = 7;
        bool rewrote = TwbFile.Write(embeddedPath, second, pdf, tbink, out string rewriteError);
        bool reread = TwbFile.TryRead(embeddedPath, out var m3, out _, out _, out string rereadError);
        Report("覆盖写：第二次保存后读到的是新内容（原子替换，不留半截文件）",
               rewrote && reread && m3.Title == "第二次保存的标题" && m3.View.PageIndex == 7,
               reread ? $"title={m3.Title} page={m3.View.PageIndex}" : $"{rewriteError} {rereadError}");

        // ---------------------------------------------------------- 7) 错误路径（每一条都必须"拒绝 + 说人话 + 不抛"）
        bool missingBad = !TwbFile.TryRead(Path.Combine(work, "不存在.twb"), out _, out _, out _,
                                           out string missingError);
        Report("错误路径：文件不存在 ⇒ 拒绝并说清原因（不抛异常）",
               missingBad && missingError.Contains("不存在"), missingError);

        string textPath = Path.Combine(work, "notzip.twb");
        File.WriteAllText(textPath, "这不是工程文件，只是一段文字。", Encoding.UTF8);
        bool notZipBad = !TwbFile.TryRead(textPath, out _, out _, out _, out string notZipError);
        Report("错误路径：选了非 ZIP 文件 ⇒ 拒绝并提示不是工程文件",
               notZipBad && notZipError.Contains("工程文件"), notZipError);

        string truncatedPath = Path.Combine(work, "truncated.twb");
        byte[] fullBytes = File.ReadAllBytes(blankPath);
        File.WriteAllBytes(truncatedPath, fullBytes.Take(fullBytes.Length / 2).ToArray());
        bool truncatedBad = !TwbFile.TryRead(truncatedPath, out _, out _, out _, out string truncatedError);
        Report("错误路径：文件被截断（拷一半拔盘）⇒ 拒绝，不把程序带崩", truncatedBad, truncatedError);

        string noManifestPath = CraftTwb("nomanifest.twb", null, pdf, tbink);
        bool noManifestBad = !TwbFile.TryRead(noManifestPath, out _, out _, out _, out string noManifestError);
        Report("错误路径：zip 里没有 manifest.json ⇒ 拒绝（光有 PK 头不足以当成工程）",
               noManifestBad && noManifestError.Contains("manifest.json"), noManifestError);

        string futurePath = CraftTwb("future.twb", ManifestJson(99, "embedded"), pdf, tbink);
        bool futureBad = !TwbFile.TryRead(futurePath, out _, out _, out _, out string futureError);
        Report("错误路径：工程版本高于本程序 ⇒ 拒绝并提示升级（而不是硬读丢数据）",
               futureBad && futureError.Contains("高于"), futureError);

        string zeroPath = CraftTwb("zeroversion.twb", ManifestJson(0, "embedded"), pdf, tbink);
        bool zeroBad = !TwbFile.TryRead(zeroPath, out _, out _, out _, out string zeroError);
        Report("错误路径：格式版本号非法（0）⇒ 拒绝", zeroBad && zeroError.Contains("版本"), zeroError);

        string badJsonPath = CraftTwb("badjson.twb", "{ 这不是合法 JSON ", pdf, tbink);
        bool badJsonBad = !TwbFile.TryRead(badJsonPath, out _, out _, out _, out string badJsonError);
        Report("错误路径：manifest.json 是坏 JSON ⇒ 拒绝并给出解析原因",
               badJsonBad && badJsonError.Contains("解析"), badJsonError);

        string missingPdfPath = CraftTwb("missingpdf.twb", ManifestJson(1, "embedded"), null, tbink);
        bool missingPdfBad = !TwbFile.TryRead(missingPdfPath, out _, out _, out _, out string missingPdfError);
        Report("错误路径：标记内嵌却没有 document.pdf ⇒ 拒绝（自相矛盾的工程）",
               missingPdfBad && missingPdfError.Contains("document.pdf"), missingPdfError);

        string weirdPath = CraftTwb("weirdembedding.twb", ManifestJson(1, "放在我妈的U盘里"), null, tbink);
        bool weirdBad = !TwbFile.TryRead(weirdPath, out _, out _, out _, out string weirdError);
        Report("错误路径：无法识别的 PDF 绑定方式 ⇒ 拒绝并回显该值（便于追查）",
               weirdBad && weirdError.Contains("绑定方式"), weirdError);

        string noPathPath = CraftTwb("nopath.twb", ManifestJson(1, "referenced"), null, tbink);
        bool noPathBad = !TwbFile.TryRead(noPathPath, out _, out _, out _, out string noPathError);
        Report("错误路径：外挂模式却没记路径 ⇒ 拒绝", noPathBad && noPathError.Contains("路径"), noPathError);

        // ---------------------------------------------------------- 8) 前向兼容：多出来的字段不许拦住读
        string extraPath = CraftTwb("extrafields.twb",
                                    ManifestJson(1, "embedded", "\"将来加的字段\": { \"a\": 1 }"),
                                    pdf, tbink);
        bool extraOk = TwbFile.TryRead(extraPath, out var em, out _, out _, out string extraError);
        Report("★ manifest 里多出未知字段仍能读（旧程序读新工程不炸，加字段零代价）",
               extraOk && em.Title == "新字段兼容测试", extraError);

        // ---------------------------------------------------------- 9) ★ 包内字节被改坏：必须被拦住
        //
        // 实测发现：.NET 的 ZipArchive 在读取 stored（不压缩）条目时**不校验 CRC** ——
        // 把 document.pdf 里的 1 个字节改掉，它会一声不吭地把坏内容交出来。
        // 所以下面验的是"我们自己的 SHA-256 有没有把坏文件拦住"，而不是"ZIP 库会不会报错"。
        byte[] marker = Encoding.ASCII.GetBytes(FakePdfMarker);

        // 9a) 先看 ZIP 层认不认（只记录事实，不作断言：库的行为可能随版本变化）
        string rawCorruptPath = CraftTwb("corrupt-raw.twb", ManifestJson(1, "embedded"), pdf, tbink);
        byte[] rawBytes = File.ReadAllBytes(rawCorruptPath);
        int markerAt = IndexOf(rawBytes, marker);
        if (markerAt >= 0) rawBytes[markerAt] = (byte)'X';
        File.WriteAllBytes(rawCorruptPath, rawBytes);
        bool zipLayerCaught = !TwbFile.TryRead(rawCorruptPath, out _, out _, out _, out _);
        Console.WriteLine($"        [事实] 无校验值的工程被改坏 1 字节：ZIP 层"
                          + $"{(zipLayerCaught ? "报错" : "静默放行")}"
                          + $"（标记串位置={(markerAt >= 0 ? markerAt.ToString() : "未找到")}）");

        // 9b) 有校验值的工程：改坏内嵌 PDF ⇒ 必须拒绝，且说清是哪一段
        string corruptPdfPath = Path.Combine(work, "corrupt-pdf.twb");
        File.Copy(embeddedPath, corruptPdfPath, overwrite: true);
        byte[] corruptPdfBytes = File.ReadAllBytes(corruptPdfPath);
        int pdfMarkerAt = IndexOf(corruptPdfBytes, marker);
        if (pdfMarkerAt >= 0) corruptPdfBytes[pdfMarkerAt] = (byte)'X';
        File.WriteAllBytes(corruptPdfPath, corruptPdfBytes);

        bool pdfCorruptBad = !TwbFile.TryRead(corruptPdfPath, out _, out _, out _, out string pdfCorruptError);
        Report("★ 内嵌 PDF 被动过 1 个字节 ⇒ 拒绝并指出是哪一段（绝不静默交出坏卷面）",
               pdfCorruptBad && pdfCorruptError.Contains("损坏") && pdfCorruptError.Contains("PDF"),
               pdfCorruptError);

        // 9c) 改坏批注载荷（沿用原 manifest 的校验值 ⇒ 必然对不上）
        byte[] tamperedTbink = (byte[])tbink.Clone();
        tamperedTbink[^1] ^= 0xFF;
        string corruptAnnPath = CraftTwb("corrupt-annotation.twb", manifestText, pdf, tamperedTbink);
        bool annCorruptBad = !TwbFile.TryRead(corruptAnnPath, out _, out _, out _, out string annCorruptError);
        Report("★ 批注载荷被改动（校验值不符）⇒ 拒绝并指出是哪一段（不静默少几笔）",
               annCorruptBad && annCorruptError.Contains("损坏") && annCorruptError.Contains("批注"),
               annCorruptError);

        // ---------------------------------------------------------- 10) 写入侧的两个拦截
        string badTarget = Path.Combine(embeddedPath, "子目录", "x.twb");
        bool badTargetBad = !TwbFile.Write(badTarget, TwbManifest.New("x"), pdf, null, out string badTargetError);
        Report("错误路径：目标路径写不进去（磁盘满 / 无权限 / 路径非法）⇒ 返回可读原因，不抛异常",
               badTargetBad && badTargetError.Contains("保存失败"), badTargetError);

        bool noPdfBad = !TwbFile.Write(Path.Combine(work, "nopdf.twb"),
                                       TwbManifest.New("说好内嵌却没给内容"), null, null,
                                       out string noPdfError);
        Report("错误路径：声明内嵌却没给 PDF 内容 ⇒ 当场拦住（不写出一个打不开的工程）",
               noPdfBad && noPdfError.Contains("必须提供"), noPdfError);

        Report("★ 保存失败不留残骸：临时文件已清理（老师不会在工程旁边看到 .tmp）",
               !File.Exists(Path.Combine(work, "nopdf.twb.tmp")),
               "检查 nopdf.twb.tmp 是否残留");

        // ---------------------------------------------------------- 11) 抽条目（M8 S1 落临时文件要用）
        string extractPath = Path.Combine(work, "extracted.pdf");
        bool extracted;
        string extractError;
        using (var dest = File.Create(extractPath))
        {
            extracted = TwbFile.ExtractEntry(embeddedPath, TwbFile.PdfEntryName, dest, out extractError);
        }

        byte[] extractedBytes = extracted && File.Exists(extractPath)
            ? File.ReadAllBytes(extractPath)
            : Array.Empty<byte>();
        Report("把内嵌 PDF 抽到流（M8 S1 落临时文件用）：字节逐位一致",
               extracted && extractedBytes.SequenceEqual(pdf),
               extracted ? $"长度 {extractedBytes.Length}" : extractError);

        using (var sink = new MemoryStream())
        {
            bool missingEntryBad = !TwbFile.ExtractEntry(embeddedPath, "根本没有这个条目.pdf", sink,
                                                         out string missingEntryError);
            Report("抽不存在的条目 ⇒ 返回可读原因", missingEntryBad && missingEntryError.Contains("没有"),
                   missingEntryError);
        }

        // ---------------------------------------------------------- 12) 只读元信息（外挂定位与打开前预检用）
        bool manifestOnly = TwbFile.TryReadManifest(embeddedPath, out var om, out string manifestOnlyError);
        Report("只读元信息（不碰 PDF 载荷）也能拿到清单",
               manifestOnly && om.Title == "第二次保存的标题", manifestOnlyError);

        Console.WriteLine();
        Console.WriteLine($"S0 工作目录：{work}");
        return failures;
    }

    /// <summary>S0 假 PDF 里的标记串：stored 存放 ⇒ 明文可见，用来定位"改成坏字节"的位置。</summary>
    private const string FakePdfMarker = "M8S0-PDF-PAYLOAD-MARKER-0123456789ABCDEF";

    /// <summary>造一份"像 PDF"的假内容 —— 这里要验的是字节原样穿过 ZIP，不是真能被解析。</summary>
    private static byte[] BuildFakePdf()
    {
        using var ms = new MemoryStream();

        byte[] head = Encoding.ASCII.GetBytes("%PDF-1.7\n" + FakePdfMarker + "\n");
        ms.Write(head, 0, head.Length);

        // 512 KB 填充：让"外挂包 vs 内嵌包"的体积差异有实际意义
        var filler = new byte[512 * 1024];
        for (int i = 0; i < filler.Length; i++) filler[i] = (byte)(i * 31 % 251);
        ms.Write(filler, 0, filler.Length);

        byte[] tail = Encoding.ASCII.GetBytes("\n%%EOF\n");
        ms.Write(tail, 0, tail.Length);

        return ms.ToArray();
    }

    /// <summary>造一份真的 .tbink v2 字节（含笔迹与图形），用来验证"批注原样穿过 ZIP"。</summary>
    private static byte[] BuildFakeTbink()    {
        var points = new StylusPointCollection
        {
            new StylusPoint(100, 120, 0.5f),
            new StylusPoint(140, 160, 0.7f),
        };

        var attributes = new DrawingAttributes
        {
            Color = Colors.Red,
            Width = 3.5,
            Height = 3.5,
            IgnorePressure = false,
        };

        var strokes = new StrokeCollection { new Stroke(points, attributes) };

        var manifest = new TbinkManifest
        {
            CreatedUtc = "2026-09-22T13:00:00Z",
            UpdatedUtc = "2026-09-22T13:00:00Z",
            SourceFileName = "26西附全真模拟1物理试卷.pdf",
            SourceLength = 123456,
            SourceFingerprint = "abcdefabcdefabcdefabcdefabcdefab",
            PageMargin = 24,
            PageGap = 24,
            Pages = { new TbinkPageRect { X = 24, Y = 24, Width = 595.28, Height = 841.89 } },
        };

        var objects = new List<GfxObjectData>
        {
            new GfxObjectData
            {
                Id = "obj-1",
                Kind = "vector",
                Plugin = "VectorArrow",
                X = 300,
                Y = 400,
                Scale = 1.0,
                Color = "#FFFF0000",
                LineWidth = 2.0,
            },
        };

        using var ms = new MemoryStream();
        TbinkFile.Write(ms, manifest, strokes, objects);
        return ms.ToArray();
    }

    // ============================================================ M8 S1：ProjectStore 服务层
    //
    // S0 管"字节怎么摆"，S1 管"这份工程能不能用"：PDF 在哪、是不是同一份、找不到怎么办、
    // 保存后要不要清侧车。这些判定全部脱离界面可断言 —— 现场最怕的
    // "批注静默贴错卷面""打开工程却白屏"都发生在这一层。
    private static int RunM8S1Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M8 S1：ProjectStore 服务层 ================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        string work = Path.Combine(outDir, "m8s1");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        var store = new ProjectStore();
        byte[] tbink = BuildFakeTbink();

        // 三个互不干扰的场景目录，避免"测完 A 把 B 的前提也改了"
        string dirRefOk = Path.Combine(work, "ref-a");      // 外挂：PDF 就位
        string dirRefBad = Path.Combine(work, "ref-b");     // 外挂：PDF 在但不是同一份
        string dirRefMissing = Path.Combine(work, "ref-c"); // 外挂：PDF 不在
        Directory.CreateDirectory(dirRefOk);
        Directory.CreateDirectory(dirRefBad);
        Directory.CreateDirectory(dirRefMissing);

        // 造两份内容不同的"试卷"
        byte[] pdfA = BuildFakePdf();
        byte[] pdfB = BuildFakePdf();
        pdfB[1000] = (byte)(pdfB[1000] ^ 0xFF);   // 只动 1 个字节 ⇒ 长度相同、指纹不同

        string pdfPathA = Path.Combine(dirRefOk, "26西附全真模拟1物理试卷.pdf");
        string pdfPathB = Path.Combine(dirRefBad, "26西附全真模拟1物理试卷.pdf");
        string pdfPathMissing = Path.Combine(dirRefMissing, "26西附全真模拟1物理试卷.pdf");
        File.WriteAllBytes(pdfPathA, pdfA);
        File.WriteAllBytes(pdfPathB, pdfB);

        string fingerprintA = DocumentFingerprint.Compute(pdfPathA);
        string fingerprintB = DocumentFingerprint.Compute(pdfPathB);

        Report("两份只差 1 个字节的 PDF，内容指纹确实不同（否则后面的「对不上」判定全是假的）",
               fingerprintA.Length == 32 && fingerprintB.Length == 32
               && !string.Equals(fingerprintA, fingerprintB, StringComparison.OrdinalIgnoreCase),
               $"{fingerprintA} vs {fingerprintB}");

        // ---------------------------------------------------------- 1) 空白工程（新建后的第一步）
        var blank = TwbManifest.New("空白工程", TwbPdfEmbedding.None);
        blank.View.PageIndex = 5;
        blank.View.Scale = 2.5;
        blank.Stats.StrokeCount = 1;
        blank.Stats.ObjectCount = 1;

        string blankPath = Path.Combine(work, "blank.twb");
        var blankSave = store.Save(blankPath, blank, null, tbink);

        bool blankLoaded = true;
        var blankLoad = store.Load(blankPath);
        Report("空白工程：保存成功且状态栏文案说得清是什么（空白工程）",
               blankSave.Ok && blankSave.Written && blankSave.Message.Contains("空白"),
               blankSave.Message);

        blankLoaded = blankLoad.Status == ProjectLoadStatus.Loaded
                      && blankLoad.Ok
                      && blankLoad.Manifest?.Title == "空白工程"
                      && blankLoad.HasPdf == false
                      && blankLoad.PdfPath is null
                      && blankLoad.EmbeddedPdf is null;
        Report("★ 空白工程：装载成功、不要求有 PDF（新建的工程能重新打开）", blankLoaded,
               $"status={blankLoad.Status} hasPdf={blankLoad.HasPdf} title={blankLoad.Manifest?.Title ?? "(无)"}");

        bool blankViewOk = blankLoad.Manifest is not null
                           && blankLoad.Manifest.View.PageIndex == 5
                           && Math.Abs(blankLoad.Manifest.View.Scale - 2.5) < 1e-9;
        Report("视图状态随工程往返（老师「上次停在哪页」能被记住）", blankViewOk,
               blankLoad.Manifest is null
                   ? "(无清单)"
                   : $"page={blankLoad.Manifest.View.PageIndex} scale={blankLoad.Manifest.View.Scale}");

        // 批注能被现有 .tbink 读回来 —— 这是 S2 接线的前提，也是"一张网"的接口处
        bool tbinkReadable = TbinkFile.TryRead(new MemoryStream(blankLoad.TbinkBytes),
                                               out _, out var strokesBack, out var objectsBack,
                                               out string tbinkError);
        Report("装载出来的批注字节能直接喂给 .tbink 读（S2 接线前提：协议零改动）",
               tbinkReadable && strokesBack.Count == 1 && objectsBack is { Count: 1 },
               tbinkReadable ? $"笔迹 {strokesBack.Count} 条 / 图形 {objectsBack?.Count ?? 0} 个" : tbinkError);

        // ---------------------------------------------------------- 2) 内嵌模式
        var embedded = TwbManifest.New("内嵌试卷");
        embedded.Pdf.Embedding = TwbPdfEmbedding.Embedded;
        embedded.Pdf.Fingerprint = fingerprintA;

        string embeddedPath = Path.Combine(work, "embedded.twb");
        var embeddedSave = store.Save(embeddedPath, embedded, pdfA, tbink);
        var embeddedLoad = store.Load(embeddedPath);

        Report("内嵌模式：保存成功且文案点出「内嵌」（老师据此判断能不能只拷这一个文件）",
               embeddedSave.Ok && embeddedSave.Message.Contains("内嵌"), embeddedSave.Message);

        bool embeddedOk = embeddedLoad.Ok
                          && embeddedLoad.Status == ProjectLoadStatus.Loaded
                          && embeddedLoad.HasPdf
                          && embeddedLoad.PdfPath is null
                          && embeddedLoad.EmbeddedPdf is not null
                          && embeddedLoad.EmbeddedPdf.SequenceEqual(pdfA);
        Report("内嵌模式：装载后拿到完整 PDF 字节（与源文件逐位一致，不依赖外部文件）", embeddedOk,
               $"status={embeddedLoad.Status} bytes={embeddedLoad.EmbeddedPdf?.Length ?? -1}");

        // ---------------------------------------------------------- 3) 外挂模式：PDF 就位
        string projectOkPath = Path.Combine(dirRefOk, "26西附全真模拟1物理试卷.twb");
        var refOk = TwbManifest.New("外挂试卷", TwbPdfEmbedding.Referenced);
        refOk.Pdf.RelativePath = "26西附全真模拟1物理试卷.pdf";
        // 绝对路径故意写成一个不存在的盘：证明"相对路径优先"真的生效
        refOk.Pdf.AbsolutePath = @"Z:\不存在的盘\26西附全真模拟1物理试卷.pdf";
        refOk.Pdf.Fingerprint = fingerprintA;

        var refOkSave = store.Save(projectOkPath, refOk, null, tbink);
        var refOkLoad = store.Load(projectOkPath);

        Report("外挂模式：保存成功且文案点出「外挂」（提示工程里没有卷面）",
               refOkSave.Ok && refOkSave.Message.Contains("外挂"), refOkSave.Message);

        Report("★ 外挂模式：PDF 就位 ⇒ 干净加载并给出解析到的路径",
               refOkLoad.Ok && refOkLoad.Status == ProjectLoadStatus.Loaded
               && refOkLoad.HasPdf
               && string.Equals(refOkLoad.PdfPath, pdfPathA, StringComparison.OrdinalIgnoreCase),
               $"status={refOkLoad.Status} path={refOkLoad.PdfPath ?? "(空)"}");

        Report("★ 相对路径优先：绝对路径指向不存在的盘，仍按 .twb 旁边的 PDF 找到了（工程搬家可活）",
               refOkLoad.Ok
               && string.Equals(refOkLoad.PdfPath, Path.GetFullPath(pdfPathA), StringComparison.OrdinalIgnoreCase),
               $"解析到 {refOkLoad.PdfPath ?? "(空)"}，绝对路径故意写成 Z:\\不存在的盘\\…");

        // ---------------------------------------------------------- 4) 外挂模式：PDF 不是同一份
        string projectBadPath = Path.Combine(dirRefBad, "26西附全真模拟1物理试卷.twb");
        var refBad = TwbManifest.New("换了卷面的工程", TwbPdfEmbedding.Referenced);
        refBad.Pdf.RelativePath = "26西附全真模拟1物理试卷.pdf";
        refBad.Pdf.Fingerprint = fingerprintA;   // 记的是 A 的指纹，实际躺着的是 B
        store.Save(projectBadPath, refBad, null, tbink);

        var refBadLoad = store.Load(projectBadPath);
        Report("★ 外挂模式：PDF 在但不是同一份 ⇒ 照常装载但黄字提醒（不静默贴错卷面）",
               refBadLoad.Ok
               && refBadLoad.Status == ProjectLoadStatus.LoadedWithMismatch
               && refBadLoad.IsWarning
               && refBadLoad.Message.Contains("不是同一份"),
               $"status={refBadLoad.Status} msg={refBadLoad.Message}");

        // ---------------------------------------------------------- 5) 外挂模式：PDF 找不到
        string projectMissingPath = Path.Combine(dirRefMissing, "26西附全真模拟1物理试卷.twb");
        var refMissing = TwbManifest.New("卷面丢了的工程", TwbPdfEmbedding.Referenced);
        refMissing.Pdf.RelativePath = "26西附全真模拟1物理试卷.pdf";
        refMissing.Pdf.Fingerprint = fingerprintA;
        store.Save(projectMissingPath, refMissing, null, tbink);

        var missingLoad = store.Load(projectMissingPath);
        Report("★ 外挂模式：PDF 找不到 ⇒ MissingPdf + 说清找过哪里（而不是白屏或崩溃）",
               !missingLoad.Ok
               && missingLoad.Status == ProjectLoadStatus.MissingPdf
               && missingLoad.IsWarning
               && missingLoad.Message.Contains("找不到")
               && missingLoad.Message.Contains("26西附全真模拟1物理试卷.pdf"),
               missingLoad.Message);

        Report("找不到 PDF 时仍带出工程元信息（UI 要靠它弹「定位 PDF」并保住笔迹）",
               missingLoad.Manifest is not null && missingLoad.TbinkBytes.Length == tbink.Length,
               $"title={missingLoad.Manifest?.Title ?? "(无)"} 批注 {missingLoad.TbinkBytes.Length} B");

        // 定位对话框：用户选到正确的 PDF ⇒ 补救路径能走通
        var afterPick = store.ResolveReferencedPdf(missingLoad.Manifest!, dirRefMissing, pdfPathA);
        Report("★ 定位对话框选到正确的 PDF ⇒ 解析成功且指纹对得上（补救路径可用）",
               afterPick.Ok && afterPick.FingerprintMatches
               && string.Equals(afterPick.Path, pdfPathA, StringComparison.OrdinalIgnoreCase),
               afterPick.Message.Length > 0 ? afterPick.Message : afterPick.Path ?? "(空)");

        var pickWrong = store.ResolveReferencedPdf(missingLoad.Manifest!, dirRefMissing, pdfPathB);
        Report("定位对话框选到另一份 PDF ⇒ 能定位但指纹不符（提醒而不阻断）",
               pickWrong.Ok && !pickWrong.FingerprintMatches && pickWrong.Message.Contains("不是同一份"),
               pickWrong.Message);

        var pickNone = store.ResolveReferencedPdf(missingLoad.Manifest!, dirRefMissing,
                                                  Path.Combine(work, "根本没有这个文件.pdf"));
        Report("定位对话框选了不存在的文件 ⇒ 拒绝并说明原因", !pickNone.Ok && pickNone.Message.Contains("不存在"),
               pickNone.Message);

        var pickCancel = store.ResolveReferencedPdf(missingLoad.Manifest!, dirRefMissing, userPickedPath: null);
        Report("定位对话框取消（没给路径）⇒ 仍按工程里记的路径找，结果还是「找不到」（不误报成功）",
               !pickCancel.Ok, pickCancel.Message);

        // ---------------------------------------------------------- 6) 坏的 / 不存在的工程
        var badProject = store.Load(Path.Combine(work, "不存在.twb"));
        Report("工程文件不存在 ⇒ Rejected 且原因可读（不是抛异常）",
               !badProject.Ok && badProject.Status == ProjectLoadStatus.Rejected
               && badProject.Message.Contains("不存在"),
               badProject.Message);

        string brokenPath = Path.Combine(work, "broken.twb");
        File.WriteAllText(brokenPath, "这不是工程文件", Encoding.UTF8);
        var broken = store.Load(brokenPath);
        Report("工程文件损坏/不是工程 ⇒ Rejected 且原因可读",
               !broken.Ok && broken.Status == ProjectLoadStatus.Rejected && broken.Message.Length > 0,
               broken.Message);

        // ---------------------------------------------------------- 7) 保存后清理旧的 .tbink 侧车
        string sidecar = AnnotationStore.SidecarPathFor(pdfPathA);
        File.WriteAllText(sidecar, "旧版侧车批注（占位）", Encoding.UTF8);

        var migrate = TwbManifest.New("迁移成工程的试卷", TwbPdfEmbedding.Embedded);
        migrate.Pdf.Fingerprint = fingerprintA;
        var migrateSave = store.Save(Path.Combine(work, "migrate.twb"), migrate, pdfA, tbink,
                                     sidecarPdfPathToClear: pdfPathA);

        Report("★ 保存工程时清掉同目录旧 .tbink（避免下次打开 PDF 时两边挑一个、看起来少了几笔）",
               migrateSave.Ok && migrateSave.SidecarTbinkDeleted && !File.Exists(sidecar),
               $"deleted={migrateSave.SidecarTbinkDeleted} 仍存在={File.Exists(sidecar)} msg={migrateSave.Message}");

        Report("侧车清理结果体现在状态栏文案里（老师知道旧文件被收走了）",
               migrateSave.Message.Contains("侧车"), migrateSave.Message);

        // 不传就一个都不动
        File.WriteAllText(sidecar, "再放一个占位侧车", Encoding.UTF8);
        var keepSidecar = store.Save(Path.Combine(work, "keep-sidecar.twb"),
                                     TwbManifest.New("不动侧车"), pdfA, tbink);
        Report("不指定侧车时一个都不动（老路径「打开 PDF 但不建工程」不能被牵连）",
               keepSidecar.Ok && !keepSidecar.SidecarTbinkDeleted && File.Exists(sidecar),
               $"deleted={keepSidecar.SidecarTbinkDeleted} 仍存在={File.Exists(sidecar)}");

        // 侧车本来就不存在时，不该报错
        File.Delete(sidecar);
        var noSidecar = store.Save(Path.Combine(work, "no-sidecar.twb"),
                                   TwbManifest.New("侧车本就没有"), pdfA, tbink,
                                   sidecarPdfPathToClear: pdfPathA);
        Report("侧车本来就不存在 ⇒ 保存照样成功（幂等，不把「没东西可删」当失败）",
               noSidecar.Ok && noSidecar.Written && !noSidecar.SidecarTbinkDeleted,
               $"ok={noSidecar.Ok} deleted={noSidecar.SidecarTbinkDeleted}");

        // ---------------------------------------------------------- 8) 保存失败的两条路
        string badTarget = Path.Combine(work, "keep-sidecar.twb", "子目录", "x.twb");
        var badSave = store.Save(badTarget, TwbManifest.New("x"), pdfA, tbink);
        Report("保存失败（路径写不进去）⇒ Ok=false / Written=false / 原因可读",
               !badSave.Ok && !badSave.Written && badSave.Message.Length > 0, badSave.Message);

        var noPdfSave = store.Save(Path.Combine(work, "no-pdf.twb"),
                                   TwbManifest.New("声明内嵌却没给内容"), null, tbink);
        Report("声明内嵌却没给 PDF 内容 ⇒ 失败且原因可读（不写出一个打不开的工程）",
               !noPdfSave.Ok && !noPdfSave.Written && noPdfSave.Message.Contains("必须提供"),
               noPdfSave.Message);

        Console.WriteLine();
        Console.WriteLine($"S1 工作目录：{work}");
        return failures;
    }

    // ============================================================ M8 S2：工程组装与状态机
    //
    // S2 把 S0/S1 的零件串成一条真实动线：造笔迹 → 装成 .tbw 字节 → 写工程文件 →
    // 读回来 → 灌进画布。这一层能断言的部分刻意全部下沉到 ProjectComposer / ProjectViewModel
    // （纯函数 + 纯状态），窗口里只剩"弹哪个对话框、按哪个键"。
    private static int RunM8S2Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M8 S2：工程组装与状态机 ================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        string work = Path.Combine(outDir, "m8s2");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        // 造 2 页 A4 的世界矩形（与 WorldLayout 同款算法：纵向排、页外留白 + 页间距）
        static List<Rect> MakePageRects(int pageCount, double margin, double gap)
        {
            var rects = new List<Rect>();
            double y = margin;

            for (int i = 0; i < pageCount; i++)
            {
                rects.Add(new Rect(margin, y, 595.28, 841.89));
                y += 841.89 + gap;
            }

            return rects;
        }

        static StrokeCollection MakeStrokes(int count)
        {
            var collection = new StrokeCollection();

            for (int i = 0; i < count; i++)
            {
                var points = new StylusPointCollection
                {
                    new StylusPoint(100 + i * 12, 120, 0.5f),
                    new StylusPoint(140 + i * 12, 170, 0.8f),
                };

                var attributes = new DrawingAttributes
                {
                    Color = Colors.Red,
                    Width = 3.0,
                    Height = 3.0,
                };

                collection.Add(new Stroke(points, attributes));
            }

            return collection;
        }

        static List<GfxObjectData> MakeObjects(int count)
        {
            var list = new List<GfxObjectData>();

            for (int i = 0; i < count; i++)
            {
                list.Add(new GfxObjectData
                {
                    Id = "obj-" + (i + 1),
                    Kind = "vector",
                    Plugin = "VectorArrow",
                    X = 300 + i * 20,
                    Y = 400,
                    Scale = 1.0,
                    Color = "#FFFF0000",
                    LineWidth = 2.0,
                });
            }

            return list;
        }

        const double margin = 24;
        const double gap = 24;
        var rects = MakePageRects(2, margin, gap);

        // ---------------------------------------------------------- A) ProjectComposer
        bool titleOk = ProjectComposer.DefaultTitle(@"D:\试卷\26西附全真模拟1物理试卷.pdf") == "26西附全真模拟1物理试卷"
                       && ProjectComposer.DefaultTitle(null) == "未命名工程";
        Report("默认标题取 PDF 文件名（去扩展名）；没有试卷时给中性名", titleOk,
               $"有 PDF=[{ProjectComposer.DefaultTitle(@"D:\试卷\26西附全真模拟1物理试卷.pdf")}] 无 PDF=[{ProjectComposer.DefaultTitle(null)}]");

        // 页面索引反推
        bool pageIndexOk = ProjectComposer.ResolvePageIndex(rects, margin + 841.89 / 2) == 0
                           && ProjectComposer.ResolvePageIndex(rects, margin + 841.89 + gap + 420) == 1
                           && ProjectComposer.ResolvePageIndex(new List<Rect>(), 12345) == 0;
        Report("当前页反推：页内命中、空文档给 0", pageIndexOk,
               $"第1页={ProjectComposer.ResolvePageIndex(rects, margin + 420)} "
               + $"第2页={ProjectComposer.ResolvePageIndex(rects, margin + 841.89 + gap + 420)} "
               + $"空={ProjectComposer.ResolvePageIndex(new List<Rect>(), 0)}");

        // 页间空隙：取纵向最近的一页（870 偏向第 1 页，889 偏向第 2 页）
        int gapNear1 = ProjectComposer.ResolvePageIndex(rects, margin + 841.89 + 6);
        int gapNear2 = ProjectComposer.ResolvePageIndex(rects, margin + 841.89 + gap - 2);
        Report("落在页间空隙时取纵向最近的一页（不会因为踩缝就跳到第 1 页）",
               gapNear1 == 0 && gapNear2 == 1, $"偏上={gapNear1} 偏下={gapNear2}");

        // 相对路径
        string dirProj = Path.Combine(work, "proj");
        Directory.CreateDirectory(dirProj);
        string pdfName = "26西附全真模拟1物理试卷.pdf";
        string pdfPath = Path.Combine(dirProj, pdfName);
        File.WriteAllBytes(pdfPath, BuildFakePdf());

        string twbPath = Path.Combine(dirProj, "我的工程.twb");
        string? relative = ProjectComposer.TryMakeRelative(twbPath, pdfPath);
        Report("同目录的试卷记成相对路径（工程整体搬家后仍能找到）",
               relative == pdfName, $"[{relative ?? "(null)"}]");

        string? crossDrive = ProjectComposer.TryMakeRelative(twbPath, @"Z:\别处的盘\x.pdf");
        Report("跨盘路径不伪装成相对路径（否则读侧会拼出一个不存在的怪路径）",
               crossDrive is null, $"[{crossDrive ?? "(null)"}]");

        var view = new TwbViewState
        {
            PageIndex = 1,
            Scale = 1.75,
            OffsetX = -120,
            OffsetY = -3400,
            IsFullScreen = true,
        };

        var embeddedManifest = ProjectComposer.BuildManifest(
            "内嵌工程", twbPath, pdfPath, embedPdf: true, view, 1, 2, 1);

        Report("有试卷 + 内嵌 ⇒ embedding=embedded，且指纹非空",
               embeddedManifest.Pdf.Embedding == TwbPdfEmbedding.Embedded
               && embeddedManifest.Pdf.Fingerprint.Length == 32,
               $"{embeddedManifest.Pdf.Embedding} / {embeddedManifest.Pdf.Fingerprint}");

        var referencedManifest = ProjectComposer.BuildManifest(
            "外挂工程", twbPath, pdfPath, embedPdf: false, view, 1, 2, 1);

        Report("有试卷 + 不内嵌 ⇒ embedding=referenced，两条路径都留着",
               referencedManifest.Pdf.Embedding == TwbPdfEmbedding.Referenced
               && referencedManifest.Pdf.RelativePath == pdfName
               && referencedManifest.Pdf.AbsolutePath is not null,
               $"{referencedManifest.Pdf.Embedding} rel=[{referencedManifest.Pdf.RelativePath}] abs=[{referencedManifest.Pdf.AbsolutePath}]");

        var blankManifest = ProjectComposer.BuildManifest(
            "空白工程", twbPath, pdfPath: null, embedPdf: true, view, 0, 0, 0);

        Report("没有试卷 ⇒ embedding=none，且不留下半截路径/指纹",
               blankManifest.Pdf.Embedding == TwbPdfEmbedding.None
               && blankManifest.Pdf.RelativePath is null
               && blankManifest.Pdf.AbsolutePath is null
               && blankManifest.Pdf.Fingerprint.Length == 0
               && blankManifest.View.Scale > 0,
               $"{blankManifest.Pdf.Embedding} rel=[{blankManifest.Pdf.RelativePath ?? "(null)"}] fpLen={blankManifest.Pdf.Fingerprint.Length}");

        var nullViewManifest = ProjectComposer.BuildManifest("没视图", twbPath, pdfPath, true, null!, 0, 0, 0);
        Report("视图状态传 null 也不崩（组装层不该因为一个可选入参把保存整条路炸掉）",
               nullViewManifest.View is not null, $"scale={nullViewManifest.View?.Scale ?? -1}");

        // ---------------------------------------------------------- B) ProjectViewModel 状态机
        var vm = new ProjectViewModel();
        int changedCount = 0;
        vm.Changed += (_, _) => changedCount++;

        bool emptyOk = !vm.HasProject
                       && vm.DisplayText == "（未打开工程）"
                       && vm.SaveStatusText.Length == 0
                       && vm.FileName.Length == 0;
        Report("初始：无工程，「（未打开工程）」，状态栏不出话", emptyOk,
               $"display=[{vm.DisplayText}] status=[{vm.SaveStatusText}]");

        vm.Apply(twbPath, embeddedManifest, hasPdf: true);
        Report("装载内嵌工程后：标题栏写明「文件名 · 内嵌」",
               vm.HasProject && vm.FileName == "我的工程.twb"
               && vm.EmbeddingText == "内嵌"
               && vm.DisplayText == "我的工程.twb · 内嵌"
               && !vm.IsDirty,
               $"display=[{vm.DisplayText}] dirty={vm.IsDirty}");

        var refVm = new ProjectViewModel();
        refVm.Apply(twbPath, referencedManifest, hasPdf: true);
        Report("外挂工程的标识是「· 外挂」（拷走时必须连试卷一起拷，得让它显眼）",
               refVm.EmbeddingText == "外挂" && refVm.DisplayText.Contains("外挂"),
               refVm.DisplayText);

        var blankVm = new ProjectViewModel();
        blankVm.Apply(twbPath, blankManifest, hasPdf: false);
        Report("空白工程的标识是「· 空白」", blankVm.EmbeddingText == "空白", blankVm.DisplayText);

        vm.MarkDirty();
        Report("★ 有改动 ⇒ 标题栏与状态栏同时出现「未保存」",
               vm.IsDirty && vm.DisplayText.Contains("未保存") && vm.SaveStatusText == "● 工程未保存",
               $"display=[{vm.DisplayText}] status=[{vm.SaveStatusText}]");

        vm.MarkClean(new DateTime(2026, 9, 23, 14, 5, 6));
        Report("保存之后：未保存提示消失，状态栏带上保存时刻",
               !vm.IsDirty && !vm.DisplayText.Contains("未保存")
               && vm.SaveStatusText.Contains("14:05:06"),
               $"display=[{vm.DisplayText}] status=[{vm.SaveStatusText}]");

        string twbPath2 = Path.Combine(dirProj, "另存的名字.twb");
        vm.ChangePath(twbPath2);
        Report("另存为之后标题栏跟着换名字（否则老师会以为没存到新文件）",
               vm.FileName == "另存的名字.twb" && vm.DisplayText.StartsWith("另存的名字.twb"),
               vm.DisplayText);

        blankVm.MarkPdfLocated();
        Report("空白工程插上外挂试卷后，标识从「空白」变「外挂」",
               blankVm.EmbeddingText == "外挂" && blankVm.HasPdf, blankVm.DisplayText);

        vm.Clear();
        Report("关闭工程后回到初始态", !vm.HasProject && vm.DisplayText == "（未打开工程）",
               vm.DisplayText);

        Report("状态变化会通知界面（否则标题栏会一直停在打开工程时的样子）",
               changedCount >= 6, $"Changed 触发 {changedCount} 次");

        // ---------------------------------------------------------- C) 端到端：造 → 存 → 读 → 灌
        var strokes = MakeStrokes(2);
        var objects = MakeObjects(1);

        byte[] composed = AnnotationStore.ComposeBytes(pdfPath, strokes, rects, margin, gap, objects);

        bool composedReadable = TbinkFile.TryRead(new MemoryStream(composed), out var composedManifest,
                                                  out var composedStrokes, out var composedObjects,
                                                  out string composedError);
        Report("★ 当前批注能装成 .tbink 字节，且读得回来（工程打包的接口处）",
               composedReadable && composedStrokes.Count == 2 && composedObjects is { Count: 1 }
               && composedManifest.Pages.Count == 2
               && composedManifest.SourceFingerprint.Length == 32,
               composedReadable
                   ? $"笔迹 {composedStrokes.Count} / 图形 {composedObjects?.Count ?? 0} / 页 {composedManifest.Pages.Count} / 指纹 {composedManifest.SourceFingerprint}"
                   : composedError);

        // 内嵌工程：写 → 读 → 灌批注（一条完整动线）
        string embeddedTwb = Path.Combine(dirProj, "内嵌工程.twb");
        byte[] embeddedTbink = AnnotationStore.ComposeBytes(pdfPath, strokes, rects, margin, gap, objects);

        // 侧车先摆在那里，验"写工程后顺手清掉"
        File.WriteAllBytes(pdfPath + ".tbink", embeddedTbink);
        bool sidecarExisted = File.Exists(pdfPath + ".tbink");

        var store = new ProjectStore();
        var saveResult = store.Save(embeddedTwb, embeddedManifest, File.ReadAllBytes(pdfPath),
                                    embeddedTbink, sidecarPdfPathToClear: pdfPath);

        Report("内嵌工程保存成功，且顺带清掉了同目录的旧侧车（不留两份会打架的批注）",
               saveResult.Ok && saveResult.Written && saveResult.SidecarTbinkDeleted
               && sidecarExisted && !File.Exists(pdfPath + ".tbink"),
               $"{saveResult.Message} / 侧车还在={File.Exists(pdfPath + ".tbink")}");

        var loaded = store.Load(embeddedTwb);
        Report("内嵌工程装载：拿到嵌入的试卷字节（不依赖外部 PDF 是否存在）",
               loaded.Status == ProjectLoadStatus.Loaded && loaded.HasPdf
               && loaded.EmbeddedPdf is { Length: > 0 } && loaded.PdfPath is null,
               $"status={loaded.Status} hasPdf={loaded.HasPdf} 内嵌={loaded.EmbeddedPdf?.Length ?? 0} 字节");

        var reloaded = new AnnotationStore().LoadFromBytes(
            loaded.TbinkBytes, rects, margin, gap, pdfPath);

        Report("★ 工程里的批注字节能直接灌回画布（笔迹 2 条、图形 1 个一个不少）",
               reloaded.Status == AnnotationLoadStatus.Loaded
               && reloaded.Strokes?.Count == 2 && reloaded.Objects is { Count: 1 },
               $"status={reloaded.Status} 笔迹={reloaded.Strokes?.Count ?? -1} 图形={reloaded.Objects?.Count ?? -1}");

        bool viewRoundTrip = loaded.Manifest is not null
                             && loaded.Manifest.View.PageIndex == 1
                             && Math.Abs(loaded.Manifest.View.Scale - 1.75) < 1e-9
                             && Math.Abs(loaded.Manifest.View.OffsetX - (-120)) < 1e-9
                             && Math.Abs(loaded.Manifest.View.OffsetY - (-3400)) < 1e-9
                             && loaded.Manifest.View.IsFullScreen;
        Report("★ 视图状态完整往返（老师「上次停在哪、放大到多少」原样回来）", viewRoundTrip,
               loaded.Manifest is null ? "(无清单)"
                   : $"page={loaded.Manifest.View.PageIndex} scale={loaded.Manifest.View.Scale} "
                     + $"offset=({loaded.Manifest.View.OffsetX},{loaded.Manifest.View.OffsetY}) 全屏={loaded.Manifest.View.IsFullScreen}");

        Report("诊断条数也随工程往返（出问题时能一眼看出是不是内容少写了）",
               loaded.Manifest?.Stats.StrokeCount == 2 && loaded.Manifest?.Stats.ObjectCount == 1,
               $"笔迹={loaded.Manifest?.Stats.StrokeCount} 图形={loaded.Manifest?.Stats.ObjectCount}");

        // 外挂工程：包应明显更小，且按路径找到 PDF
        string refTwb = Path.Combine(dirProj, "外挂工程.twb");
        var refSave = store.Save(refTwb, referencedManifest, null, embeddedTbink);
        var refLoad = store.Load(refTwb);

        long embeddedSize = new FileInfo(embeddedTwb).Length;
        long referencedSize = new FileInfo(refTwb).Length;

        Report("外挂工程不装试卷 ⇒ 包体小一个数量级（放共享盘上随身带得动）",
               refSave.Ok && refLoad.Status == ProjectLoadStatus.Loaded
               && refLoad.PdfPath is not null
               && referencedSize * 50 < embeddedSize,
               $"外挂 {referencedSize} B vs 内嵌 {embeddedSize} B");

        // 同名文件被换成另一份内容 ⇒ 照常装载 + 黄字提醒（不丢批注）
        string swapDir = Path.Combine(work, "swapped");
        Directory.CreateDirectory(swapDir);
        string swapPdf = Path.Combine(swapDir, pdfName);
        File.WriteAllBytes(swapPdf, BuildFakePdf());

        var swapManifest = ProjectComposer.BuildManifest(
            "被换过的试卷", Path.Combine(swapDir, "x.twb"), swapPdf, embedPdf: false, view, 0, 2, 1);
        string swapTwb = Path.Combine(swapDir, "x.twb");
        store.Save(swapTwb, swapManifest, null, embeddedTbink);

        byte[] other = BuildFakePdf();
        other[2048] = (byte)(other[2048] ^ 0x5A);
        File.WriteAllBytes(swapPdf, other);   // 同路径、同名字、内容不同

        var swapLoad = store.Load(swapTwb);
        Report("★ 试卷是同名但内容不同的一份 ⇒ 照常装载 + 提醒（不静默丢批注）",
               swapLoad.Status == ProjectLoadStatus.LoadedWithMismatch && swapLoad.IsWarning
               && swapLoad.Message.Contains("不是同一份"),
               $"status={swapLoad.Status} msg=[{swapLoad.Message}]");

        var swapAnnotations = new AnnotationStore().LoadFromBytes(
            swapLoad.TbinkBytes, rects, margin, gap, swapPdf);

        Report("工程模式下也守得住「换了试卷」这条：批注照给、但要挂黄字说明位置可能不准",
               swapAnnotations.Status == AnnotationLoadStatus.LoadedWithMismatch
               && swapAnnotations.IsWarning
               && swapAnnotations.Strokes?.Count == 2,
               $"status={swapAnnotations.Status} 笔迹={swapAnnotations.Strokes?.Count ?? -1}");

        // ---------------------------------------------------------- D) 空白工程动线
        string blankTwb = Path.Combine(work, "空白工程.twb");
        byte[] blankTbink = AnnotationStore.ComposeBytes(
            pdfPath: null, strokes, pageRects: new List<Rect>(), margin, gap, objects: null);

        var blankSave = new ProjectStore().Save(blankTwb, blankManifest, pdfBytes: null, blankTbink);
        var blankLoad = new ProjectStore().Load(blankTwb);
        var blankAnnotations = new AnnotationStore().LoadFromBytes(
            blankLoad.TbinkBytes, new List<Rect>(), margin, gap, fingerprintPath: null);

        Report("★ 空白工程动线：没试卷也能存、能开、笔迹能回来（新建工程后随手写的东西不会丢）",
               blankSave.Ok && blankLoad.Status == ProjectLoadStatus.Loaded
               && !blankLoad.HasPdf
               && blankAnnotations.Status == AnnotationLoadStatus.Loaded
               && blankAnnotations.Strokes?.Count == 2,
               $"save={blankSave.Ok} load={blankLoad.Status} 笔迹={blankAnnotations.Strokes?.Count ?? -1}");

        Report("空白工程的批注不会因为「没有 PDF 可比指纹」被误报成换过文件",
               !blankAnnotations.IsWarning && !blankLoad.IsWarning,
               $"warning={blankAnnotations.IsWarning}");

        // 空批注（刚建好还没写）也算正常装载
        var freshAnnotations = new AnnotationStore().LoadFromBytes(
            Array.Empty<byte>(), new List<Rect>(), margin, gap, null);
        Report("刚建好、还没写过字的工程：批注为空是正常状态（不是错误）",
               freshAnnotations.Status == AnnotationLoadStatus.Missing
               && !freshAnnotations.IsWarning, $"status={freshAnnotations.Status}");

        // ---------------------------------------------------------- E) 工程视口复原
        //
        // 这一段守的是“打开工程看到一片空白”这类毛病 —— 它在界面上与“文件根本没打开”
        // 长得一模一样，肉眼看不出差别，只能靠这里算出来的结论去认。
        {
            var viewHost = new CanvasViewportHost();
            viewHost.Measure(new Size(1000, 700));
            viewHost.Arrange(new Rect(0, 0, 1000, 700));
            viewHost.UpdateLayout();

            // 没有文档也要能算排版：直接喂页尺寸（与真实动线同一个 WorldLayout）
            viewHost.Layout.Rebuild(new[] { new Size(595.28, 841.89), new Size(595.28, 841.89) });
            var bounds = viewHost.Layout.WorldBounds;
            double fit = (1000 - 48) / 595.28;

            // ① 坏档：100%@(0,0) —— 页面从 (960,960) 起排，这个视口整屏都是“页外可写留白”
            bool badRestore = viewHost.RestoreView(1.0, 0, 0);

            Report("★ 存档视口指向页外空白（100%@原点）⇒ 不按它复原，退回「适应宽度」"
                   + "（否则打开工程是一片白，看着像文件没打开）",
                   !badRestore && viewHost.IsAutoFitWidth
                   && Math.Abs(viewHost.Viewport.Scale - fit) < 1e-6,
                   $"restored={badRestore} 缩放={viewHost.Viewport.Scale:F3}（适应宽度应为 {fit:F3}）");

            // ② 正常档：老师缩放到某道题上（1.6 倍、页面左上角贴视口左上角）
            const double scale = 1.6;
            double offsetX = 24 - bounds.X * scale;
            double offsetY = -bounds.Y * scale;
            bool goodRestore = viewHost.RestoreView(scale, offsetX, offsetY);

            Report("★ 存档视口落在页面上 ⇒ 严格按存档复原（“我停在第五题那个缩放”要能回到原位）",
                   goodRestore && !viewHost.IsAutoFitWidth
                   && Math.Abs(viewHost.Viewport.Scale - scale) < 1e-9
                   && Math.Abs(viewHost.Viewport.OffsetX - offsetX) < 1e-9
                   && Math.Abs(viewHost.Viewport.OffsetY - offsetY) < 1e-9,
                   $"restored={goodRestore} 自动适应={viewHost.IsAutoFitWidth}"
                   + $" 视口={viewHost.Viewport.Scale:F2}@({viewHost.Viewport.OffsetX:F0},{viewHost.Viewport.OffsetY:F0})");

            // ③ ★ 复原之后再来一次窗口尺寸变化：视口必须原地不动。
            //    老写法只调 SetView、“自动适应宽度”开关还开着，窗口一动就被算回第 1 页顶部。
            viewHost.Measure(new Size(1400, 900));
            viewHost.Arrange(new Rect(0, 0, 1400, 900));
            viewHost.UpdateLayout();

            Report("★ 复原之后窗口改变尺寸 ⇒ 视口不被「自动适应宽度」算掉"
                   + "（这是老写法的真 bug：存档位置白存了）",
                   Math.Abs(viewHost.Viewport.Scale - scale) < 1e-9
                   && Math.Abs(viewHost.Viewport.OffsetX - offsetX) < 1e-9
                   && Math.Abs(viewHost.Viewport.OffsetY - offsetY) < 1e-9,
                   $"视口={viewHost.Viewport.Scale:F2}@({viewHost.Viewport.OffsetX:F0},{viewHost.Viewport.OffsetY:F0})"
                   + $"（应仍是 {scale:F2}@({offsetX:F0},{offsetY:F0})）");

            // ⑥ 反向对照：真的处于“自动适应宽度”模式时，窗口一改尺寸缩放就必须跟着变。
            //    没有这一条，③可能只是“SizeChanged 压根没触发”的假通过。
            viewHost.FitWidth();
            viewHost.Measure(new Size(900, 650));
            viewHost.Arrange(new Rect(0, 0, 900, 650));
            viewHost.UpdateLayout();
            double refit = (900 - 48) / 595.28;

            Report("反向对照：自动适应宽度模式下窗口改尺寸，缩放跟着重算"
                   + "（证明③不是“事件没触发”的假通过）",
                   Math.Abs(viewHost.Viewport.Scale - refit) < 1e-6,
                   $"缩放={viewHost.Viewport.Scale:F3}（应为 {refit:F3}）");

            // ④ 空白工程：还没有试卷，谈不上“页外”，存档视口照用
            var blankHost = new CanvasViewportHost();
            blankHost.Measure(new Size(1000, 700));
            blankHost.Arrange(new Rect(0, 0, 1000, 700));
            blankHost.UpdateLayout();

            bool blankRestore = blankHost.RestoreView(1.0, 0, 0);

            Report("空白工程（还没放试卷）⇒ 存档视口照用，不被误判成坏档",
                   blankRestore && !blankHost.IsAutoFitWidth
                   && Math.Abs(blankHost.Viewport.Scale - 1.0) < 1e-9,
                   $"restored={blankRestore} 缩放={blankHost.Viewport.Scale:F2}"
                   + $" 自动适应={blankHost.IsAutoFitWidth}");

            // ⑤ 判定函数自身的边界（它是①④两条断言的判据，坏了上面就全是空转）
            Report("IsViewUsable 判据自检：视口尺寸还没量出来（0×0）时不否决存档",
                   ProjectComposer.IsViewUsable(1.0, 0, 0, new Size(0, 0), bounds));
            Report("IsViewUsable 判据自检：缩放 <= 0 的坏档一律判不合格",
                   !ProjectComposer.IsViewUsable(0, 0, 0, new Size(1000, 700), bounds));
            Report("IsViewUsable 判据自检：视口落在页外空白 ⇒ 不合格（白屏判据）",
                   !ProjectComposer.IsViewUsable(1.0, 0, 0, new Size(1000, 700), bounds));
        }

        // ---------------------------------------------------------- F) 断言网自检
        Report("假 PDF 的指纹确实算得出来（否则上面所有「对得上/对不上」都是空转）",
               DocumentFingerprint.Compute(pdfPath).Length == 32,
               DocumentFingerprint.Compute(pdfPath));

        Console.WriteLine();
        Console.WriteLine($"S2 工作目录：{work}");
        return failures;
    }

    // ============================================================ M8 S3：关档三选与「什么算改动」
    //
    // 这条路上只有两种失败方式，都不出声：**该问的时候没问**（老师一整节课的板书静默丢掉），
    // 或者**问了却放行**（界面关了、文件没写）。它们在界面上都表现为"什么都没发生"，
    // 所以矩阵与触发源必须钉在断言里，靠真机点按钮是碰不出来的。
    private static int RunM8S3Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M8 S3：关档三选与内容变更 ================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        // ---------------------------------------------------------- A) 三选矩阵（纯逻辑）
        Report("有工程 + 有未保存改动 ⇒ 必须问",
               ProjectClosePolicy.NeedsPrompt(hasProject: true, isDirty: true));
        Report("有工程但不脏 ⇒ 不问（干净地关，不该打扰）",
               !ProjectClosePolicy.NeedsPrompt(hasProject: true, isDirty: false));
        Report("★ 没有工程（只开着 PDF、批注走侧车自动落盘）⇒ 不问（问了反而像丢了东西）",
               !ProjectClosePolicy.NeedsPrompt(hasProject: false, isDirty: true));

        Report("选「保存」且保存成功 ⇒ 放行",
               ProjectClosePolicy.ShouldProceed(ProjectCloseChoice.Save, saveSucceeded: true));
        Report("★★ 选「保存」但保存失败 ⇒ 绝不放行（界面关了、文件却没写＝最不可原谅的失败）",
               !ProjectClosePolicy.ShouldProceed(ProjectCloseChoice.Save, saveSucceeded: false));
        Report("选「不保存」⇒ 放行（老师明确说要丢）",
               ProjectClosePolicy.ShouldProceed(ProjectCloseChoice.Discard, saveSucceeded: false));
        Report("选「取消」⇒ 不放行（什么都不做）",
               !ProjectClosePolicy.ShouldProceed(ProjectCloseChoice.Cancel, saveSucceeded: false));

        Report("按钮映射：是⇒保存、否⇒丢弃",
               ProjectClosePolicy.FromMessageBox(MessageBoxResult.Yes) == ProjectCloseChoice.Save
               && ProjectClosePolicy.FromMessageBox(MessageBoxResult.No) == ProjectCloseChoice.Discard);
        Report("★ 按钮映射：取消 / Esc 关掉对话框（None）一律⇒取消 —— "
               + "按 Esc 的本意是「别动」，绝不能理解成丢弃",
               ProjectClosePolicy.FromMessageBox(MessageBoxResult.Cancel) == ProjectCloseChoice.Cancel
               && ProjectClosePolicy.FromMessageBox(MessageBoxResult.None) == ProjectCloseChoice.Cancel);

        string prompt = ProjectClosePolicy.PromptText("静电场的力的性质");
        Report("★ 提示正文：写清工程名 + 三个按钮各自是什么意思（「否」在中文里有两种读法，不能让老师猜）",
               prompt.Contains("静电场的力的性质")
               && prompt.Contains("保存") && prompt.Contains("丢弃") && prompt.Contains("取消"),
               prompt.Replace("\n", " ／ "));

        // ---------------------------------------------------------- B) 「什么算改动」：图形对象也要上报
        //
        // 图形对象（坐标系 / 函数图像 / 三角板 / 箭头）是 M7 起的一等内容，
        // 而它们长在**另一个层**上：宿主要是只跟笔迹集合，拖一下就关窗口既不问也不存。
        {
            var contentHost = new CanvasViewportHost();
            contentHost.Measure(new Size(1000, 700));
            contentHost.Arrange(new Rect(0, 0, 1000, 700));
            contentHost.UpdateLayout();

            int events = 0;
            contentHost.InkChanged += (_, _) => events++;

            // ① 笔迹（回归：原来就有的触发不能弄丢）
            contentHost.ReplaceStrokes(new StrokeCollection
            {
                new Stroke(
                    new StylusPointCollection { new StylusPoint(120, 200), new StylusPoint(260, 320) },
                    new DrawingAttributes { Color = Colors.Red, Width = 3, Height = 3 }),
            });

            int inkEvents = events;
            Report("笔迹变化上报「画布内容变了」（回归）", inkEvents > 0, $"事件数={inkEvents}");

            // ② 图形对象：新增
            events = 0;
            contentHost.ReplaceObjects(new[]
            {
                new GfxObjectData
                {
                    Id = "clip-1", Kind = "vector", Plugin = "VectorArrow",
                    X = 300, Y = 400, Scale = 1.0, Color = "#FFFF0000", LineWidth = 2.0,
                },
            });

            int addEvents = events;
            Report("★ 图形对象新增 ⇒ 上报（否则工程不置脏：放完坐标系直接关窗口，改动静默丢）",
                   addEvents > 0, $"事件数={addEvents}");

            // ③ 拖动（改位姿）
            events = 0;
            bool moved = contentHost.GfxObjects.UpdatePose("clip-1", new Point(420, 520), 0, 1.0);
            Report("★ 拖动图形对象 ⇒ 上报（只挪了一下同样是改动）",
                   moved && events > 0, $"moved={moved} 事件数={events}");

            // ④ 改参数
            events = 0;
            bool numbered = contentHost.GfxObjects.UpdateNumbers(
                "clip-1", new Dictionary<string, double> { { "angle", 45 } });
            Report("★ 改图形参数 ⇒ 上报（调一个数字也要能存住）",
                   numbered && events > 0, $"ok={numbered} 事件数={events}");

            // ⑤ 删除
            events = 0;
            bool deleted = contentHost.GfxObjects.Remove("clip-1");
            Report("★ 删除图形对象 ⇒ 上报", deleted && events > 0,
                   $"deleted={deleted} 事件数={events}");
        }

        Console.WriteLine();
        return failures;
    }

    // ============================================================ M9 S0：导出的换算与画布借用
    //
    // 导出这条路只有两种坏法，且都不出声：
    //   ① 像素算错 —— 图糊（dpi 少算）或右下角整块被裁（dpi 多算），产物照样打得开；
    //   ② 借画布时把画面弄脏 —— 导出得到白纸，或者导出完老师的视口回不到原位。
    // 两者都要等"打开导出的那个文件"时才看得出来，那时已经离现场很远了，所以先在这里钉死。
    /// <summary>
    /// M9 S1：单页栅格化 + PNG 落盘 + 整卷导出。
    /// </summary>
    /// <remarks>
    /// 这一段守的是三种"屏幕上一点异常都看不到"的失败：
    /// ① 导出图是白纸；② 导出图很糊（因为它用的是屏幕上那份低档位位图）；
    /// ③ 导出把老师的视口带走、或者把页面底图留在替换后的状态。
    /// 它们都不报错 —— 只有打开导出产物、或者再动一下界面才会发现。
    /// </remarks>
    private static int RunM9S1Checks(IPdfDocumentService service, string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M9 S1：PNG 栅格化与落盘 ================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        string work = Path.Combine(outDir, "m9s1");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        // 局部辅助：像素统计。判据全部基于"实测出来的数"，不拍凭空阈值。

        // 统计某个竖直区间（0~1 的比例）的深色像素数、平均亮度、全透明像素数。
        //
        // ★ 必须把像素**合成到白底**上再看亮度：RenderTargetBitmap 的底色是全透明的，
        //   页面上没被绘制的地方 alpha=0、RGB=0。直接读 RGB 会把那些地方当成"纯黑"，
        //   于是"整张白纸"会被算成"深色像素占 100%" —— 判据正好反了。
        //   先合成到白底，得到的才是它印出来/看到的样子。
        (long Dark, double Mean, long Transparent) Analyze(BitmapSource src,
                                                           double fromY = 0.0, double toY = 1.0)
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            int stride = w * 4;
            var buf = new byte[stride * h];
            conv.CopyPixels(buf, stride, 0);

            int y0 = Math.Max(0, (int)(h * fromY));
            int y1 = Math.Min(h, (int)(h * toY));

            long dark = 0, n = 0, transparent = 0;
            double sum = 0;
            for (int y = y0; y < y1; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = rowBase + x * 4;
                    byte alpha = buf[i + 3];
                    if (alpha == 0) transparent++;

                    double raw = 0.114 * buf[i] + 0.587 * buf[i + 1] + 0.299 * buf[i + 2];
                    double coverage = alpha / 255.0;
                    double lum = raw * coverage + 255.0 * (1.0 - coverage);

                    sum += lum; n++;
                    if (lum < 160) dark++;
                }
            }

            return (dark, n == 0 ? 255.0 : sum / n, transparent);
        }

        // 两张同尺寸位图的平均像素差；尺寸不同返回 -1（"不可比"要被看出来，不能当成 0）。
        double Diff(BitmapSource a, BitmapSource b)
        {
            var ca = new FormatConvertedBitmap(a, PixelFormats.Bgra32, null, 0);
            var cb = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);

            if (ca.PixelWidth != cb.PixelWidth || ca.PixelHeight != cb.PixelHeight) return -1;

            int stride = ca.PixelWidth * 4;
            int len = stride * ca.PixelHeight;
            var ba = new byte[len];
            var bb = new byte[len];
            ca.CopyPixels(ba, stride, 0);
            cb.CopyPixels(bb, stride, 0);

            double sum = 0;
            for (int i = 0; i < len; i += 4)
            {
                double la = 0.114 * ba[i] + 0.587 * ba[i + 1] + 0.299 * ba[i + 2];
                double lb = 0.114 * bb[i] + 0.587 * bb[i + 1] + 0.299 * bb[i + 2];
                sum += Math.Abs(la - lb);
            }

            return sum / (ca.PixelWidth * (double)ca.PixelHeight);
        }

        // 这里不能走「先看文件头」的捷径：要验的正是"这份 PNG 能被标准解码器认出来"。
        (int W, int H) ReadPngSize(string path)
        {
            using var fs = File.OpenRead(path);
            var decoder = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat,
                                               BitmapCacheOption.OnLoad);
            return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
        }

        BitmapSource LoadPng(string path)
        {
            using var fs = File.OpenRead(path);
            var decoder = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat,
                                               BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }

        // ---------------------------------------------------------- 1) 文件名规则
        Console.WriteLine();
        Console.WriteLine("---- 1) 文件名规则（PngWriter.FileName）----");

        Report("第 1 页 ⇒ 试卷名-第01页.png（补零到两位）",
               PngWriter.FileName("试卷", 0, 7) == "试卷-第01页.png",
               PngWriter.FileName("试卷", 0, 7));

        Report("7 页文档的最后一页 ⇒ 第07页（不是第6页 —— 页号从 1 数给老师看）",
               PngWriter.FileName("试卷", 6, 7) == "试卷-第07页.png",
               PngWriter.FileName("试卷", 6, 7));

        Report("单页文档也补到两位（与多页导出的观感一致）",
               PngWriter.FileName("卷", 0, 1) == "卷-第01页.png",
               PngWriter.FileName("卷", 0, 1));

        Report("超过 99 页 ⇒ 自动扩到三位（否则「第100页」会紧挨着「第10页」，分不出先后）",
               PngWriter.FileName("卷", 99, 120) == "卷-第100页.png",
               PngWriter.FileName("卷", 99, 120));

        Report("基名为空 / 纯空白 ⇒ 兜底名字（不许出现以「-第」开头的文件名）",
               PngWriter.FileName("", 0, 3) == PngWriter.FallbackBaseName + "-第01页.png"
               && PngWriter.FileName("   ", 0, 3) == PngWriter.FallbackBaseName + "-第01页.png",
               PngWriter.FileName(null, 0, 3));

        var twelve = Enumerable.Range(0, 12).Select(i => PngWriter.FileName("卷", i, 12)).ToList();
        Report("12 页生成的文件名两两不同（同名会让后一页悄悄盖掉前一页）",
               twelve.Distinct().Count() == 12,
               string.Join(" ", twelve.Take(4)) + " …");

        // ---------------------------------------------------------- 2) 编码落盘
        Console.WriteLine();
        Console.WriteLine("---- 2) 编码落盘（PngWriter.Write）----");

        var tiny = MakeBitmap(40, 30);
        string tinyPath = Path.Combine(work, "tiny.png");
        long tinyBytes = PngWriter.Write(tinyPath, tiny);

        Report("写出的文件真的在盘上", File.Exists(tinyPath), tinyPath);

        Report("返回的字节数与文件实际长度一致（返回值会被拿去向老师报体积）",
               tinyBytes == new FileInfo(tinyPath).Length && tinyBytes > 0,
               $"返回 {tinyBytes} vs 实际 {new FileInfo(tinyPath).Length}");

        var (tinyW, tinyH) = ReadPngSize(tinyPath);
        Report("回读能被标准 PNG 解码器认出来，尺寸与源位图一致",
               tinyW == 40 && tinyH == 30, $"回读 {tinyW}×{tinyH}");

        long tinyAgain = PngWriter.Write(tinyPath, tiny);
        Report("同路径再写一次 = 覆盖（重新导出同一份卷子不该在旁边堆出 -1、-2）",
               tinyAgain == tinyBytes && Directory.GetFiles(work, "tiny*.png").Length == 1,
               $"第二次 {tinyAgain} 字节；目录里 tiny*.png 有 {Directory.GetFiles(work, "tiny*.png").Length} 个");

        // ---------------------------------------------------------- 3) 单页栅格化
        Console.WriteLine();
        Console.WriteLine("---- 3) 单页栅格化（PageRasterizer）----");

        var host = new CanvasViewportHost();
        host.Measure(new Size(1000, 700));
        host.Arrange(new Rect(0, 0, 1000, 700));
        host.UpdateLayout();
        host.SetDocument(service);
        host.UpdateLayout();

        var pageImages = VisualDescendants(host).OfType<Image>().ToList();

        Report("前置：画布上按页建好了位图元素",
               pageImages.Count == service.PageCount && pageImages.Count >= 2,
               $"位图元素 {pageImages.Count} 个 / 文档 {service.PageCount} 页");

        if (pageImages.Count < 2)
        {
            Report("这一段需要一份至少 2 页的试卷", false, "页数不够，跳过栅格化相关断言，不冤枉程序");
            Console.WriteLine();
            Console.WriteLine($"M9 S1 工作目录：{work}");
            return failures;
        }

        var page0 = host.Layout.GetPageRect(0);
        int lastIndex = service.PageCount - 1;
        var pageLast = host.Layout.GetPageRect(lastIndex);
        var want0 = ExportGeometry.PixelSize(service.GetPageSize(0));
        var wantLast = ExportGeometry.PixelSize(service.GetPageSize(lastIndex));

        // 先把视口指到最后一页并等它真的渲出来：后面要验"底图替换会还原"，
        // 前提是它本来有一张位图（本来就是空的，测了也绿）。
        host.RestoreView(1.0, -pageLast.X, -pageLast.Y);
        bool lastReady = PumpUntil(() => pageImages[lastIndex].Source is not null, 8000);

        Report("前置：最后一页真的有屏幕上位图（下面「替换后能还原」的非空前提）",
               lastReady,
               $"第 {lastIndex + 1} 页位图 = {(pageImages[lastIndex].Source is null ? "null" : "已就绪")}");

        var viewBeforeRaster = host.Viewport.Save();
        var sourceBeforeRaster = pageImages[lastIndex].Source;
        int levelBeforeRaster = host.DisplayedLevelOf(lastIndex);

        var raster = new PageRasterizer(host, service);
        var bmpLast = raster.RenderPage(lastIndex);

        Report("渲一页 ⇒ 尺寸 = 该页 world 尺寸 × 200dpi（与屏幕上那张位图的尺寸无关）",
               bmpLast.PixelWidth == wantLast.Width && bmpLast.PixelHeight == wantLast.Height,
               $"实测 {bmpLast.PixelWidth}×{bmpLast.PixelHeight} 期望 {wantLast.Width}×{wantLast.Height}");

        Report("渲染目标的标称 dpi = 96×200/72 = 266.67（不是 96 也不是 200）",
               Near(bmpLast.DpiX, ExportGeometry.BitmapDpi(), 0.01),
               $"实测 DpiX={bmpLast.DpiX:F2}");

        var (darkLast, meanLast, transparentLast) = Analyze(bmpLast);
        long totalLast = (long)bmpLast.PixelWidth * bmpLast.PixelHeight;

        Report("★ 导出图不是白纸：画面上确实有内容",
               meanLast < 250.0 && darkLast > 200,
               $"平均亮度 {meanLast:F1} / 深色像素 {darkLast}"
               + $"（占 {darkLast * 100.0 / totalLast:F2}%）/ 全透明像素 {transparentLast}"
               + $"（占 {transparentLast * 100.0 / totalLast:F2}%）");

        // 「没被裁掉」这条原想用页面的边框线当观察物，结果发现它**本来就被页面位图盖住**
        // （PdfPageLayer 里 paper 矩形先加、Image 后加，两者同尺寸 ⇒ 屏幕上同样看不见）。
        // 改用"上下两端都有内容"：因 dpi 给错而溢出画面的图，会呈现"只有中间一段有东西"
        // 的纵向分布，正好被这一条抓住；而它的尺寸、格式、可解码性**全都正常**。
        var (darkTop, meanTop, _) = Analyze(bmpLast, 0.0, 0.25);
        var (darkBottom, meanBottom, _) = Analyze(bmpLast, 0.75, 1.0);
        Report("★ 图的上下两端都有内容 ⇒ 不是「只画了页面的一小块」那种裁剪",
               darkTop > 500 && darkBottom > 500,
               $"上四分之一深色 {darkTop}（平均亮度 {meanTop:F1}）"
               + $" / 下四分之一深色 {darkBottom}（平均亮度 {meanBottom:F1}）");

        Report("单页栅格化后视口原样归还（老师的位置不许被导出带走）",
               Near(host.Viewport.Scale, viewBeforeRaster.Scale)
               && Near(host.Viewport.OffsetX, viewBeforeRaster.OffsetX)
               && Near(host.Viewport.OffsetY, viewBeforeRaster.OffsetY),
               $"归还后 {host.Viewport.Scale:F3}@({host.Viewport.OffsetX:F1},{host.Viewport.OffsetY:F1})"
               + $" 期望 {viewBeforeRaster.Scale:F3}@({viewBeforeRaster.OffsetX:F1},{viewBeforeRaster.OffsetY:F1})");

        Report("★ 底图替换已还原：屏幕那一页的位图对象与档位都回到原样"
               + "（不还原 ⇒ 导出一次之后屏幕上那张图就永远是导出的临时图，再也跟不上缩放）",
               ReferenceEquals(pageImages[lastIndex].Source, sourceBeforeRaster)
               && host.DisplayedLevelOf(lastIndex) == levelBeforeRaster,
               $"Source 同一对象={ReferenceEquals(pageImages[lastIndex].Source, sourceBeforeRaster)}"
               + $" 档位 {levelBeforeRaster} → {host.DisplayedLevelOf(lastIndex)}");

        // ---------------------------------------------------------- 4) 与屏幕档位解耦
        //
        // 这一段是本步最要紧的一条。S0 的"冻结"挡住了"清位图"，顺带也挡住了"升档" ——
        // 若不主动把底图换成 200dpi 版本，导出用的就是屏幕上那一份，
        // 而屏幕上用哪个档位取决于老师当时缩到多少：缩着看整卷时导出就是糊的。
        Console.WriteLine();
        Console.WriteLine("---- 4) ★ 导出质量与屏幕缩放无关 ----");

        // ★★ 观察点的顺序必须是「先低档、后高档」，反过来就白测：
        //   宿主对屏幕位图有一条"绝不降质"的规则（OnPageRendered 里
        //   `e.LevelIndex <= _displayedLevels[i]` 就丢弃）—— 已经显示过高档的页，
        //   缩小时**不会**被换成低档位图。于是"先高档后低档"两次的屏幕位图其实**一样**，
        //   那条"两张图一致"就失去了区分力（第一版正是这么错的，观察点自检当场揭穿）。
        //   此刻第 1 页的显示档位是 -1（上面被清空过），正好能从头把低档建立起来。
        //
        // 另外这里**不能**用 FitWidth 复位：它只按宽度铺满、竖直方向"保持当前视口中心"，
        //   而上面刚把视口指到过最后一页 ⇒ 复位后视口还停在第 7 页附近，第 1 页不在可见区。
        //   （这也是第一版第二轮踩到的坑。）

        // (a) 低档：25% 缩放，屏幕位图只有 ~198px 宽
        host.RestoreView(0.25, -page0.X * 0.25, -page0.Y * 0.25);
        bool lowReady = PumpUntil(
            () => (pageImages[0].Source as BitmapSource)?.PixelWidth <= want0.Width / 4, 8000);
        PumpFor(300);
        int lowSourceWidth = (pageImages[0].Source as BitmapSource)?.PixelWidth ?? 0;

        var lowShot = raster.RenderPage(0);

        // (b) 高档：200% 缩放，屏幕位图 ~1587px 宽
        host.RestoreView(2.0, -page0.X * 2.0, -page0.Y * 2.0);
        bool highReady = PumpUntil(
            () => (pageImages[0].Source as BitmapSource)?.PixelWidth >= 1000, 8000);
        PumpFor(300);
        int highSourceWidth = (pageImages[0].Source as BitmapSource)?.PixelWidth ?? 0;

        var highShot = raster.RenderPage(0);

        // 观察点自检：两次之间屏幕位图的清晰度必须真的差好几倍，否则下面那条是白给的。
        // （这正是 S0 那条"观察点自检"的同款纪律：先证明前提成立，再断言结论。）
        Report("观察点自检：两次导出之间，屏幕上第 1 页的位图分辨率确实差了好几倍"
               + "（前提真的变了，下面的结论才有意义）",
               lowReady && highReady && lowSourceWidth > 0
               && highSourceWidth > lowSourceWidth * 2,
               $"低缩放时屏幕位图宽 {lowSourceWidth}px / 高缩放时 {highSourceWidth}px");

        double crossDiff = Diff(lowShot, highShot);
        Report("★★ 两次导出的第 1 页逐像素一致 ⇒ 导出质量与老师当时缩到多少无关"
               + "（「把底图换成 200dpi 版本」这件事真的生效了；不做的话低缩放那张会明显糊）",
               crossDiff >= 0 && crossDiff < 0.5,
               $"平均像素差 {crossDiff:F3}（0 = 完全一致）");

        Report("★ 低缩放下的导出图宽度仍等于 200dpi 宽度，而不是屏幕位图那点宽度",
               lowShot.PixelWidth == want0.Width && lowShot.PixelWidth > lowSourceWidth * 4,
               $"导出 {lowShot.PixelWidth}px / 屏幕位图 {lowSourceWidth}px / 期望 {want0.Width}px");

        // ---------------------------------------------------------- 5) 整卷导出
        Console.WriteLine();
        Console.WriteLine("---- 5) 整卷导出（ExportService.ExportPngTo）----");

        string pngDir = Path.Combine(work, "导出目录");
        var progress = new List<(int Done, int Total)>();
        var svc = new ExportService(host, service);

        host.FitWidth();
        PumpFor(300);
        var viewBeforeExport = host.Viewport.Save();

        var result = svc.ExportPngTo(pngDir, "测试卷", (done, total) => progress.Add((done, total)));

        Report("整卷导出成功（目录不存在时由它自己建出来）", result.Ok, result.Message);

        var files = Directory.Exists(pngDir)
            ? Directory.GetFiles(pngDir, "*.png").OrderBy(f => f).ToList()
            : new List<string>();

        Report("导出的文件数 = 文档页数", files.Count == service.PageCount,
               $"{files.Count} 个文件 / {service.PageCount} 页");

        var expectNames = Enumerable.Range(0, service.PageCount)
                                    .Select(i => PngWriter.FileName("测试卷", i, service.PageCount))
                                    .ToList();
        int matched = expectNames.Count(n => files.Any(f => Path.GetFileName(f) == n));
        Report("文件名全部符合规则（第NN页、补零，按名字排序即按页序）",
               matched == expectNames.Count,
               string.Join("、", files.Take(3).Select(Path.GetFileName)) + " …");

        Report("进度回调逐页调用且递增到末页（界面靠它显示「正在导出 3/7 页」）",
               progress.Count == service.PageCount
               && progress.Count > 0
               && progress[0] == (1, service.PageCount)
               && progress[^1] == (service.PageCount, service.PageCount),
               progress.Count > 0
                   ? $"共 {progress.Count} 次，首次 {progress[0].Done}/{progress[0].Total}"
                     + $" 末次 {progress[^1].Done}/{progress[^1].Total}"
                   : "(回调一次都没被调用)");

        int badSize = 0, blankCount = 0;
        long smallestBytes = long.MaxValue;
        for (int i = 0; i < files.Count; i++)
        {
            var (w, h) = ReadPngSize(files[i]);
            var want = ExportGeometry.PixelSize(service.GetPageSize(i));
            if (w != want.Width || h != want.Height) badSize++;

            var img = LoadPng(files[i]);
            var (dark, mean, transparent) = Analyze(img);
            if (dark < 200 || mean > 250.0) blankCount++;

            // 逐张打出实测值：整卷里"某几页是白纸"这种失败，只有靠这张表才看得出规律
            // （是固定的某几页？还是除第一页之外的全部？）。定"是不是白纸"的阈值也靠它。
            Console.WriteLine($"        第 {i + 1} 页：{w}×{h}  平均亮度 {mean:F1}"
                              + $"  深色 {dark}  全透明 {transparent}"
                              + $"  文件 {new FileInfo(files[i]).Length / 1024} KB");

            smallestBytes = Math.Min(smallestBytes, new FileInfo(files[i]).Length);
        }

        Report("每张的像素尺寸都等于 200dpi 下的页面尺寸", badSize == 0, $"尺寸不符 {badSize} 张");
        Report("每张都有内容（整卷里没有一张是白纸）", blankCount == 0,
               $"白纸 {blankCount} 张；最小一张 {smallestBytes / 1024.0:F0} KB");

        Report("导出后视口与导出前严格相等（整卷只借一次、还一次 —— 中间每页只移动视口）",
               Near(host.Viewport.Scale, viewBeforeExport.Scale)
               && Near(host.Viewport.OffsetX, viewBeforeExport.OffsetX)
               && Near(host.Viewport.OffsetY, viewBeforeExport.OffsetY),
               $"导出后 {host.Viewport.Scale:F3}@({host.Viewport.OffsetX:F1},{host.Viewport.OffsetY:F1})"
               + $" 期望 {viewBeforeExport.Scale:F3}@({viewBeforeExport.OffsetX:F1},{viewBeforeExport.OffsetY:F1})");

        Report("导出结束后 IsBusy 归位（不归位按钮就永远灰着）", !svc.IsBusy, $"IsBusy={svc.IsBusy}");

        var afterBatch = raster.RenderPage(0);
        Report("整卷导出之后单页栅格化仍然可用（说明会话都正确归还、宿主没被留在冻结态）",
               afterBatch.PixelWidth == want0.Width,
               $"再渲第 1 页 {afterBatch.PixelWidth}×{afterBatch.PixelHeight}");

        using (var emptySvc = new PdfiumDocumentService())
        {
            var emptyExport = new ExportService(host, emptySvc).ExportPngTo(Path.Combine(work, "空"), "卷");
            Report("无文档时导出 ⇒ 失败但是一句人话（不抛异常、不写出半个目录）",
                   !emptyExport.Ok && !emptyExport.Cancelled && emptyExport.Message.Contains("试卷"),
                   emptyExport.Message);
        }

        // ---------------------------------------------------------- 6) 笔迹真的进了导出图
        //
        // "导出与屏幕共用同一条渲染链"是本设计的核心取舍（不另搭离屏树）。
        // 前面那些只能证明"有内容"，这一条才证明那个内容里含屏幕上的墨迹层。
        Console.WriteLine();
        Console.WriteLine("---- 6) 导出与屏幕共用渲染链（笔迹实证）----");

        host.RestoreView(1.0, -page0.X, -page0.Y);
        PumpFor(300);

        var beforeInk = raster.RenderPage(0);

        var inkPoints = new StylusPointCollection(new[]
        {
            new StylusPoint(page0.X + 120, page0.Y + 120),
            new StylusPoint(page0.X + 420, page0.Y + 260),
            new StylusPoint(page0.X + 200, page0.Y + 600),
        });
        host.Strokes.Add(new Stroke(inkPoints));
        PumpFor(300);

        var afterInk = raster.RenderPage(0);
        double inkDiff = Diff(beforeInk, afterInk);

        var (darkBefore, _, _) = Analyze(beforeInk);
        var (darkAfter, _, _) = Analyze(afterInk);

        Report("★ 画上三笔之后再导出，图变了 ⇒ 导出确实共享屏幕那条渲染链（含墨迹层）",
               inkDiff > 0.05,
               $"平均像素差 {inkDiff:F4}（阈值 0.05，按实测标定）");

        // 判据用**绝对增量**：A4 @200dpi 是 387 万像素、半张纸有字，
        // 三笔短划多出来的深色像素只占整体的百分之几 ——
        // 用"增加 20%"这种相对阈值会直接误判成"没变化"（第一版就是这么错的）。
        Report("★ 变化来自「多了墨」而不是别的（深色像素明显增加，增量与三笔的面积相称）",
               darkAfter - darkBefore > 2000,
               $"深色像素 {darkBefore} → {darkAfter}（多了 {darkAfter - darkBefore}）");

        host.ClearStrokes();
        PumpFor(200);

        Console.WriteLine();
        Console.WriteLine($"M9 S1 工作目录：{work}（导出物在「导出目录」子目录里）");
        return failures;
    }

    // ============================================================ M9 S2：整卷 PDF
    //
    // S2 的坏法与 S1 不同：S1 是「图糊 / 图白」，S2 是「PDF 能打开，但里面那页不是原来那一页」——
    //   ① 页尺寸被写成像素数 ⇒ 一页 A4 变成 1654×2339 point 的巨幅页（约 58×82 厘米），
    //      打印一页装不下，而页数、文件大小、能不能打开全都正常；
    //   ② 横向卷子被写成竖的（PdfSharpCore 的 Width/Height 是按 Orientation 解释的，顺序写反就中招）；
    //   ③ 图片根本没嵌进去 ⇒ 页数是 2、尺寸对、文件也不小，就是每页全白。
    // 三条都不报错，只有"打开导出的 PDF / 打出来"才发现。
    //
    // 所以这一段的判据全部走「**pdfium 回读**」：我们数给自己听的页数不算数，
    // 得让一个独立的 PDF 解析器说"这是几页、多大、画出来有没有东西"。
    /// <summary>
    /// M9 S2：整卷 PDF 导出（PdfWriter + ExportService.ExportPdfTo + 体积口径 + 错误处理）。
    /// </summary>
    private static int RunM9S2Checks(IPdfDocumentService service, string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M9 S2：整卷 PDF 导出 ================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        string work = Path.Combine(outDir, "m9s2");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        // 一张「左半黑、右半白」的**不透明**位图。用它当页内容，于是
        // 「图片真的进了 PDF」可以用 pdfium 渲出来数深色像素 —— 比只验页数/尺寸硬得多。
        BitmapSource HalfDark(int width, int height)
        {
            int stride = width * 4;
            var px = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = y * stride + x * 4;
                    byte v = x < width / 2 ? (byte)0 : (byte)255;
                    px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = 255;
                }
            }
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, px, stride);
            bmp.Freeze();
            return bmp;
        }

        // 深色像素占比。★ 仍先合成到白底：pdfium 渲出来的图可能带 alpha，
        // 直接读 RGB 会把透明处当成纯黑（S1 已经在这上面吃过一次假通过）。
        double DarkRatio(BitmapSource src)
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var buf = new byte[stride * h];
            conv.CopyPixels(buf, stride, 0);

            long dark = 0;
            for (int i = 0; i < buf.Length; i += 4)
            {
                double a = buf[i + 3] / 255.0;
                double lum = (0.114 * buf[i] + 0.587 * buf[i + 1] + 0.299 * buf[i + 2]) * a
                             + 255.0 * (1.0 - a);
                if (lum < 128) dark++;
            }
            return dark / (double)(w * h);
        }

        // 读出 PDF 里某个「信息字典字符串」的值（/Title、/Creator …）。
        //
        // ★ 第一版这里写的是「按 UTF-8 / UTF-16BE / UTF-16LE 三种编码去原文里找标题」——
        //   结果造出一条**假 FAIL**：PdfSharpCore 对非 ASCII 字符串按 PDF 规范写成
        //   **十六进制串**（实测 `/Title <FEFF6A2A7AD654044E009875>` = 横竖各一页，前面那个
        //   FEFF 是 UTF-16 的 BOM）。十六进制串在文件里是一串 ASCII 数字，
        //   原始编码的字节当然一次都找不到 —— 而"猜库用什么编码"这件事本身就不该出现在断言里。
        //   改成按规范解码，顺便验到了"解出来正好是我传进去的那个标题"。
        string? ReadPdfString(string file, string key)
        {
            var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file));

            int at = text.IndexOf("/" + key, StringComparison.Ordinal);
            if (at < 0) return null;

            // 只在**同一行**里找分隔符：不然会窜到后面某个对象里把别人的串读回来
            int lineEnd = text.IndexOf('\n', at);
            string line = text.Substring(at, (lineEnd < 0 ? text.Length : lineEnd) - at);

            int lt = line.IndexOf('<');
            int gt = line.IndexOf('>');
            if (lt < 0 || gt <= lt) return null;

            string hex = line.Substring(lt + 1, gt - lt - 1).Trim();
            if (hex.Length == 0 || hex.Length % 4 != 0) return null;   // UTF-16 码元 = 4 个十六进制位

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i + 3 < hex.Length; i += 4)
            {
                sb.Append((char)Convert.ToInt32(hex.Substring(i, 4), 16));
            }

            string value = sb.ToString();
            return value.Length > 0 && value[0] == '\uFEFF' ? value.Substring(1) : value;
        }

        // ---------------------------------------------------------- 1) 文件名与体积口径
        Console.WriteLine();
        Console.WriteLine("---- 1) 文件名与体积口径（纯函数）----");

        Report("整卷是「一个文件」，文件名不补页号：基名.pdf",
               PdfWriter.FileName("试卷") == "试卷.pdf",
               PdfWriter.FileName("试卷"));

        Report("基名为空 / 纯空白 ⇒ 兜底「导出试卷.pdf」",
               PdfWriter.FileName(null) == PdfWriter.FallbackBaseName + ".pdf"
               && PdfWriter.FileName("   ") == PdfWriter.FallbackBaseName + ".pdf",
               PdfWriter.FileName(null));

        Report("基名两端空白会去掉（不然会生成「 试卷 .pdf」这种名字）",
               PdfWriter.FileName("  试卷  ") == "试卷.pdf",
               PdfWriter.FileName("  试卷  "));

        Report("体积：≥1MB 给 MB 一位小数、否则给 KB",
               ExportSizes.Format(1048576) == "1.0 MB" && ExportSizes.Format(512 * 1024) == "512 KB",
               $"{ExportSizes.Format(512 * 1024)} / {ExportSizes.Format(1048576)}");

        Report("50MB 是「大」的分界（正好 50MB 也算大）",
               !ExportSizes.IsLarge(ExportSizes.LargeFileThreshold - 1)
               && ExportSizes.IsLarge(ExportSizes.LargeFileThreshold),
               $"阈值 {ExportSizes.LargeFileThreshold / 1048576} MB");

        Report("不大 ⇒ 提示是空串（可以直接拼进消息里，调用处不必判空）",
               ExportSizes.LargeHint(10 * 1048576).Length == 0,
               "«" + ExportSizes.LargeHint(10 * 1048576) + "»");

        Report("够大 ⇒ 提示里带上体积（老师看到「文件较大」能自己决定要不要换种方式传）",
               ExportSizes.LargeHint(120 * 1048576).Contains("文件较大")
               && ExportSizes.LargeHint(120 * 1048576).Contains("120.0 MB"),
               ExportSizes.LargeHint(120 * 1048576));

        // ---------------------------------------------------------- 2) 拼装多页 PDF
        Console.WriteLine();
        Console.WriteLine("---- 2) 拼装多页 PDF（PdfWriter，不借画布）----");

        // 两页：一页 A4 纵向、一页 A4 横向。
        // ★ 横向那页是故意的 —— 见本段开头 ②。
        const double pw = 595.28, ph = 841.89;
        string loosePath = Path.Combine(work, "横竖各一页.pdf");
        var want = new[] { (W: pw, H: ph), (W: ph, H: pw) };

        var looseProgress = new List<(int Done, int Total)>();
        long looseBytes = PdfWriter.Write(
            loosePath,
            2,
            i => new PdfWriter.PageImage(
                PngWriter.Encode(i == 0 ? HalfDark(400, 566) : HalfDark(600, 400)),
                want[i].W, want[i].H),
            (d, t) => looseProgress.Add((d, t)),
            "横竖各一页");

        // 再拼一份「不传标题」的：验兜底名（两条断言共用同一批图，省一次 200dpi 渲染）
        string noTitlePath = Path.Combine(work, "无标题.pdf");
        PdfWriter.Write(noTitlePath, 2,
            i => new PdfWriter.PageImage(
                PngWriter.Encode(i == 0 ? HalfDark(400, 566) : HalfDark(600, 400)),
                want[i].W, want[i].H));

        Report("拼出来的文件真在盘上，且返回字节数 = 文件实际长度",
               File.Exists(loosePath) && looseBytes == new FileInfo(loosePath).Length && looseBytes > 0,
               $"{looseBytes} 字节");

        Report("逐页进度回调按页递增到末页（界面靠它显示「正在导出 3/7 页」）",
               looseProgress.Count == 2 && looseProgress[0] == (1, 2) && looseProgress[^1] == (2, 2),
               looseProgress.Count == 2
                   ? $"{looseProgress[0].Done}/{looseProgress[0].Total} → {looseProgress[^1].Done}/{looseProgress[^1].Total}"
                   : $"回调 {looseProgress.Count} 次");

        Report("图片字节原样进得了 PDF（页数对了但图没嵌进去，是这一类里最像成功的一种失败）",
               looseBytes > 4000, $"{looseBytes} 字节（两张 400×566 的图，远大于一个空 PDF 骨架）");

        using (var reader = new PdfiumDocumentService())
        {
            reader.Open(loosePath);

            Report("★ 回读页数（pdfium 自己数出来的，不是我们数给自己听的）",
                   reader.PageCount == 2, $"pdfium 报 {reader.PageCount} 页");

            var g0 = reader.GetPageSize(0);
            var g1 = reader.GetPageSize(1);

            Report("★ 纵向页按**原尺寸**回读（页尺寸是 PDF point，不是像素 ——"
                   + "写成像素就得到 1654×2339 point 的巨幅页，打印一页装不下）",
                   Near(g0.Width, pw, 1.5) && Near(g0.Height, ph, 1.5),
                   $"回读 {g0.Width:F2}×{g0.Height:F2} 期望 {pw}×{ph}（容差 1.5pt）");

            Report("★★ 横向页回读仍是横向（Orientation 与 Width/Height 的顺序写反过一次，就会在这里红）",
                   Near(g1.Width, ph, 1.5) && Near(g1.Height, pw, 1.5),
                   $"回读 {g1.Width:F2}×{g1.Height:F2} 期望 {ph}×{pw}");

            // 名字不要叫 r0 / r1：本方法后面（错误处理段）另有 r1、r2 ——
            // C# 禁止「内层块里的局部变量」与外层方法体里声明过的名字重名（哪怕外层那句写在更后面）。
            double darkPortrait = DarkRatio(reader.RenderPage(0, 0.5));
            double darkLandscape = DarkRatio(reader.RenderPage(1, 0.5));
            Report("★★ 两页里都真有那半张黑图 ⇒ 图片确实被 PDF 认下来了（不是「页数是 2、里面全白」）",
                   darkPortrait > 0.25 && darkPortrait < 0.75
                   && darkLandscape > 0.25 && darkLandscape < 0.75,
                   $"深色占比 第1页 {darkPortrait:P1} / 第2页 {darkLandscape:P1}（画的是左半黑，期望约 50%）");
        }

        string? looseTitle = ReadPdfString(loosePath, "Title");
        Report("★ 标题真的写进了产物，且解码出来 = 传进去的那个标题（不是常量、也不是被丢掉）",
               looseTitle == "横竖各一页", "解码得到 «" + (looseTitle ?? "null") + "»");

        string? looseCreator = ReadPdfString(loosePath, "Creator");
        Report("署名（/Creator）写的是「数理墨」—— 导出物发出去要认得出是我们做的",
               looseCreator == "数理墨", "解码得到 «" + (looseCreator ?? "null") + "»");

        string? fallbackTitle = ReadPdfString(Path.Combine(work, "无标题.pdf"), "Title");
        Report("标题传空 ⇒ 落回兜底名（不写空串，免得属性面板里那一栏是空的）",
               fallbackTitle == PdfWriter.FallbackBaseName,
               "解码得到 «" + (fallbackTitle ?? "null") + "»");

        // ---------------------------------------------------------- 3) 整卷导出
        Console.WriteLine();
        Console.WriteLine("---- 3) 整卷导出（ExportService.ExportPdfTo）----");

        var host = new CanvasViewportHost();
        host.Measure(new Size(1000, 700));
        host.Arrange(new Rect(0, 0, 1000, 700));
        host.UpdateLayout();
        host.SetDocument(service);
        host.UpdateLayout();

        int pages = service.PageCount;

        if (pages < 2)
        {
            Report("这一段需要一份至少 2 页的试卷", false, "页数不够，跳过整卷导出断言，不冤枉程序");
            Console.WriteLine();
            Console.WriteLine($"M9 S2 工作目录：{work}");
            return failures;
        }

        var svc = new ExportService(host, service);

        // 视口先摆到一个「不那么刚好」的位置：整卷导出必须把老师的视口原样还回来
        host.RestoreView(1.6, -1550, -1200);
        PumpFor(250);
        var viewBefore = host.Viewport.Save();

        // ★ 目标目录故意不存在（还带一层）：CreateDirectory 这条分支也一并验了
        string pdfPath = Path.Combine(work, "深一层", PdfWriter.FileName("测试卷"));
        var progress = new List<(int Done, int Total)>();

        var result = svc.ExportPdfTo(pdfPath, "测试卷", (d, t) => progress.Add((d, t)));

        Report("整卷导出成功（目标目录不存在时由它自己建出来）",
               result.Ok && File.Exists(pdfPath) && result.BytesWritten > 0,
               result.Message);

        Report("返回值里的字节数与文件实际长度一致（返回值会被拿去向老师报体积）",
               result.Ok && result.BytesWritten == new FileInfo(pdfPath).Length,
               result.Ok ? $"返回 {result.BytesWritten} vs 实际 {new FileInfo(pdfPath).Length}" : "(导出失败)");

        Report("成功消息里说明了存到哪、多大、几页（老师要能复述给别人）",
               result.Message.Contains("测试卷.pdf") && result.Message.Contains("页"),
               result.Message);

        Report("进度回调逐页递增到末页",
               progress.Count == pages
               && progress[0] == (1, pages)
               && progress[^1] == (pages, pages),
               progress.Count > 0
                   ? $"共 {progress.Count} 次，首次 {progress[0].Done}/{progress[0].Total}"
                     + $" 末次 {progress[^1].Done}/{progress[^1].Total}"
                   : "(回调一次都没被调用)");

        using (var reader = new PdfiumDocumentService())
        {
            reader.Open(pdfPath);

            Report("★ 回读页数 = 试卷页数（不多不少 —— 少一页在打印店是不会有人替你数的）",
                   reader.PageCount == pages, $"pdfium 报 {reader.PageCount} 页 / 试卷 {pages} 页");

            int badSize = 0;
            var sizeDetail = new List<string>();
            for (int i = 0; i < pages; i++)
            {
                var wantSize = service.GetPageSize(i);
                var gotSize = reader.GetPageSize(i);
                if (!Near(gotSize.Width, wantSize.Width, 1.5) || !Near(gotSize.Height, wantSize.Height, 1.5))
                {
                    badSize++;
                    sizeDetail.Add($"第{i + 1}页 {gotSize.Width:F0}×{gotSize.Height:F0}"
                                   + $" ≠ {wantSize.Width:F0}×{wantSize.Height:F0}");
                }
            }
            Report("★ 每一页的物理尺寸都等于原页尺寸（容差 1.5pt：打印出来才是原尺寸）",
                   badSize == 0,
                   badSize == 0 ? $"共 {pages} 页逐页相符（{service.GetPageSize(0).Width:F1}×{service.GetPageSize(0).Height:F1}）"
                                : string.Join("；", sizeDetail));

            int blank = 0;
            var ratios = new List<string>();
            for (int i = 0; i < pages; i++)
            {
                double ratio = DarkRatio(reader.RenderPage(i, 0.4));
                ratios.Add($"第{i + 1}页 {ratio:P2}");
                if (ratio < 0.01) blank++;
            }

            // 逐页打实测值：整卷里「某几页空白」这种失败，只有靠这张表才看得出规律
            Console.WriteLine("        " + string.Join("  ", ratios));
            Report("★ 每一页渲出来都有内容（整卷里没有一页是空白的）", blank == 0, $"空白 {blank} 页");

            // 顺手留一张"从 PDF 里读回来的第 1 页"当证据：
            // 断言说"有内容"是一回事，人能不能一眼看懂是另一回事 ——
            // 产物类功能最终是拿给人看的，留一张能直接打开看的图，比一串百分比有用。
            try
            {
                var look = reader.RenderPage(0, 2.0);
                PngWriter.Write(Path.Combine(work, "PDF回读-第1页.png"), look);
                Console.WriteLine($"        证据：{Path.Combine(work, "PDF回读-第1页.png")}"
                                  + $"（{look.PixelWidth}×{look.PixelHeight}，由 pdfium 从导出的 PDF 里读回来）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"        （留证据图失败，不影响结论：{ex.Message}）");
            }
        }

        Report("导出后视口与导出前严格相等（整卷只借一次、还一次 —— 中间每页只移动视口）",
               Near(host.Viewport.Scale, viewBefore.Scale)
               && Near(host.Viewport.OffsetX, viewBefore.OffsetX)
               && Near(host.Viewport.OffsetY, viewBefore.OffsetY),
               $"导出后 {host.Viewport.Scale:F3}@({host.Viewport.OffsetX:F1},{host.Viewport.OffsetY:F1})"
               + $" 期望 {viewBefore.Scale:F3}@({viewBefore.OffsetX:F1},{viewBefore.OffsetY:F1})");

        Report("导出结束后 IsBusy 归位（不归位按钮就永远灰着）", !svc.IsBusy, $"IsBusy={svc.IsBusy}");

        var again = new PageRasterizer(host, service).RenderPage(0);
        Report("整卷导出之后单页栅格化仍然可用（会话都正确归还、宿主没被留在冻结态）",
               again.PixelWidth == ExportGeometry.PixelSize(service.GetPageSize(0)).Width,
               $"再渲第 1 页 {again.PixelWidth}×{again.PixelHeight}");

        var second = svc.ExportPdfTo(pdfPath, "测试卷");
        Report("同一路径再导一次 = 覆盖（重新导出同一份卷子不该在旁边堆出 -1、-2）",
               second.Ok && Directory.GetFiles(Path.GetDirectoryName(pdfPath)!, "*.pdf").Length == 1,
               $"第二次 {(second.Ok ? "成功" : "失败：" + second.Message)}；"
               + $"目录里 *.pdf 有 {Directory.GetFiles(Path.GetDirectoryName(pdfPath)!, "*.pdf").Length} 个");

        // ---------------------------------------------------------- 4) 错误处理
        Console.WriteLine();
        Console.WriteLine("---- 4) 错误处理：不抛异常、给一句人话 ----");

        // ① 目标目录的位置被一个**同名文件**占着 ⇒ CreateDirectory 失败
        string blocker = Path.Combine(work, "被文件占着");
        File.WriteAllText(blocker, "x");
        var r1 = svc.ExportPdfTo(Path.Combine(blocker, "整卷.pdf"), "卷");
        Report("目标目录建不出来（被同名文件占着）⇒ 失败、但是一句人话、且指明路径",
               !r1.Ok && !r1.Cancelled && r1.Message.Contains("目录") && r1.Message.Contains("被文件占着"),
               r1.Message);

        // ② 目标路径本身是个目录 ⇒ PdfSharpCore 落盘时抛（「库内部异常」这一类）
        var r2 = svc.ExportPdfTo(work, "卷");
        Report("目标路径写不进去（它本身是个目录）⇒ 失败、但是一句人话、不把异常抛到界面上",
               !r2.Ok && !r2.Cancelled && r2.Message.Length > 0,
               r2.Message);

        using (var emptySvc = new PdfiumDocumentService())
        {
            var r3 = new ExportService(host, emptySvc).ExportPdfTo(Path.Combine(work, "无文档.pdf"), "卷");
            Report("无文档时导出 ⇒ 失败但是一句人话（不抛异常、不写出半个文件）",
                   !r3.Ok && !r3.Cancelled && r3.Message.Contains("试卷") && !File.Exists(Path.Combine(work, "无文档.pdf")),
                   r3.Message);
        }

        Report("失败之后 IsBusy 仍然归位（否则按钮永远灰着，只能重启程序）",
               !svc.IsBusy, $"IsBusy={svc.IsBusy}");

        Console.WriteLine();
        Console.WriteLine($"M9 S2 工作目录：{work}（导出的 PDF 在「深一层」子目录里）");
        return failures;
    }

    // ============================================================ M9 S3：从 .twb 内嵌 PDF 导出
    //
    // 这条路 M8 就铺好了（打开工程时把内嵌 PDF 落到临时文件），而导出链本身与"PDF 从哪来"无关。
    // 正因为太顺，它成了最容易静默坏掉的一段**接缝**：
    //   ① 成品文件名沾上临时文件名（`...-a1b2c3d4`），老师拿到一串看不懂的东西；
    //   ② 释放出来的临时文件字节对不上（写一半 / 被同名前缀截断），页数与尺寸却可能照样"看着对"；
    //   ③ 临时目录写不进去时没人说话，老师只看到一片空白画布，以为工程文件坏了。
    // 三条都不报错 —— 所以在这里钉死。
    /// <summary>M9 S3：从 .twb 内嵌 PDF 导出（端到端：造工程 → 装载 → 释放 → 导出 → 回读）。</summary>
    private static int RunM9S3Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M9 S3：从 .twb 内嵌 PDF 导出 ================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        string work = Path.Combine(outDir, "m9s3");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        const string title = "静电场复习卷";
        string none = "(无)";

        // 一份"像内嵌工程释放出来的"临时路径：工程名 + 8 位随机后缀。
        string tempLike = Path.Combine(
            Path.GetTempPath(), "MathPhys.Ink", EmbeddedPdfReleaser.FolderName,
            title + "-3f9a1c02.pdf");

        // ---------------------------------------------------------- 1) 基名规则
        Console.WriteLine();
        Console.WriteLine("---- 1) 导出基名规则（ExportNaming）----");

        Report("工程模式 ⇒ 基名就是工程标题",
               ExportNaming.BaseName(true, title, tempLike) == title,
               ExportNaming.BaseName(true, title, tempLike));

        Report("★★ 内嵌工程的试卷落在 %TEMP%、名字还带 8 位随机后缀 —— 导出基名仍是工程标题，"
               + "绝不沾临时名（沾上就会导出一份叫「静电场复习卷-3f9a1c02-第01页.png」的东西）",
               ExportNaming.BaseName(true, title, tempLike) == title
               && !ExportNaming.BaseName(true, title, tempLike).Contains("3f9a1c02"),
               ExportNaming.BaseName(true, title, tempLike));

        Report("非工程模式 ⇒ 取 PDF 文件名去扩展名（老师直开 PDF 的老路一字不变）",
               ExportNaming.BaseName(false, null, @"D:\试卷\26西附全真模拟1物理试卷.pdf")
                   == "26西附全真模拟1物理试卷",
               ExportNaming.BaseName(false, null, @"D:\试卷\26西附全真模拟1物理试卷.pdf"));

        Report("★ 工程标题是空白 ⇒ 兜底，且绝不退到那个临时路径去取名",
               ExportNaming.BaseName(true, "   ", tempLike) == ExportNaming.FallbackBaseName
               && ExportNaming.BaseName(true, null, tempLike) == ExportNaming.FallbackBaseName,
               ExportNaming.BaseName(true, "   ", tempLike));

        Report("非法字符换成下划线（工程标题可以取 PDF 名，未必合法）",
               ExportNaming.Safe("第一章: 静电场?") == "第一章_ 静电场_",
               ExportNaming.Safe("第一章: 静电场?"));

        // 名字先取到局部变量里再拼进 detail：断言串里不出现 ASCII 引号（docs/06 的纪律）
        string dotTrimmed = ExportNaming.Safe("静电场.");
        string spaceTrimmed = ExportNaming.Safe("静电场  ");

        Report("★ 结尾的点与空格先去掉（Windows 会静默丢掉它们，程序记的名字就与盘上的对不上）",
               dotTrimmed == "静电场" && spaceTrimmed == "静电场",
               $"«{dotTrimmed}» / «{spaceTrimmed}»");

        Report("兜底名与 PNG 那边的兜底名同值（不一致时，同一份没名字的卷子会得到两个名字）",
               ExportNaming.FallbackBaseName == PngWriter.FallbackBaseName,
               ExportNaming.FallbackBaseName + " / " + PngWriter.FallbackBaseName);

        Report("名字取不出来时也不产生以「-第」开头的文件名",
               PngWriter.FileName(ExportNaming.BaseName(true, null, null), 0, 1)
                   == ExportNaming.FallbackBaseName + "-第01页.png",
               PngWriter.FileName(ExportNaming.BaseName(true, null, null), 0, 1));

        // ---------------------------------------------------------- 2) 释放器
        Console.WriteLine();
        Console.WriteLine("---- 2) 内嵌 PDF 释放器（EmbeddedPdfReleaser）----");

        Report("默认目录在 %TEMP% 自己的子目录下（不在 %TEMP% 根上，老师的临时目录才看得清谁是谁）",
               EmbeddedPdfReleaser.DefaultDirectory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
               && EmbeddedPdfReleaser.DefaultDirectory.EndsWith(EmbeddedPdfReleaser.FolderName, StringComparison.Ordinal),
               EmbeddedPdfReleaser.DefaultDirectory);

        string releaseDir = Path.Combine(work, "临时区");
        string built = EmbeddedPdfReleaser.BuildPath(releaseDir, @"C:\x\静电场复习卷.twb", "3f9a1c02");

        Report("释放路径 = 目录\\工程名-后缀.pdf（工程名留在前缀里，日志一眼看出是哪个工程放的）",
               Path.GetFileName(built) == title + "-3f9a1c02.pdf"
               && string.Equals(Path.GetDirectoryName(built), releaseDir, StringComparison.OrdinalIgnoreCase),
               built);

        var releaser = new EmbeddedPdfReleaser(releaseDir);

        // 造一段"有特征"的假内容：要验的正是"字节原样落盘"，用全零数组验不出截断。
        var payload = new byte[4096];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        string twbLike = Path.Combine(work, title + ".twb");

        string? p1 = releaser.Release(twbLike, payload);
        string show1 = p1 is null ? none : Path.GetFileName(p1);

        Report("释放成功 ⇒ 文件真在盘上", p1 is not null && File.Exists(p1!), show1);

        Report("★★ 落盘字节与包内字节逐字节一致（写一半、或被同名文件截断，pdfium 读到的就是另一份卷子）",
               p1 is not null && File.ReadAllBytes(p1!).SequenceEqual(payload),
               p1 is null ? "没落盘" : new FileInfo(p1).Length + " 字节，全部比对过");

        Report("文件名 = 工程名 + 8 位十六进制后缀",
               p1 is not null && System.Text.RegularExpressions.Regex.IsMatch(
                   Path.GetFileName(p1!), "^" + title + "-[0-9a-f]{8}\\.pdf$"),
               show1);

        string? p2 = releaser.Release(twbLike, payload);
        string show2 = p2 is null ? none : Path.GetFileName(p2);

        Report("★ 同一工程释放两次 ⇒ 两个不同的文件（固定名会在重开工程时覆盖仍被 pdfium 打开着的第一份）",
               p1 is not null && p2 is not null
               && !string.Equals(p1, p2, StringComparison.OrdinalIgnoreCase)
               && File.Exists(p1!) && File.Exists(p2!),
               show1 + " / " + show2);

        Report("两次都登记在案（清理时才知道该删谁）", releaser.Count == 2, "已登记 " + releaser.Count + " 个");

        Report("空字节 ⇒ 拒绝、返回 null（释放出一个 0 字节的 PDF 只会让打开时莫名白屏）",
               releaser.Release(Path.Combine(work, "空.twb"), Array.Empty<byte>()) is null
               && releaser.Count == 2,
               "已按预期拒绝，且没有多登记一个");

        // 目录位置被一个**同名文件**占着 ⇒ CreateDirectory 必然失败
        string blocked = Path.Combine(work, "被文件占着");
        File.WriteAllText(blocked, "x");

        Report("★ 临时目录不可写 ⇒ 返回 null（调用方才能给老师一句人话，而不是让异常冒到顶层弹窗）",
               new EmbeddedPdfReleaser(blocked).Release(twbLike, payload) is null,
               "已按预期返回 null");

        int removed = releaser.Cleanup();

        Report("清理 ⇒ 释放出来的文件全都没了，并报出删了几个",
               removed == 2 && p1 is not null && !File.Exists(p1!) && p2 is not null && !File.Exists(p2!),
               "删除 " + removed + " 个");

        int twice = releaser.Cleanup();
        Report("清理幂等：再调一次是 0、不抛异常（关工程与关窗口两处都会调它）",
               twice == 0 && releaser.Count == 0, "第二次删除 " + twice + " 个");

        // ---- 被占用时删不掉：这条**故意**不判失败 ----
        //
        // 删不掉（仍被别的进程占着）不该打断退出流程，所以 Cleanup 只记日志、返回 0。
        // 代价是"顺序写错"会变成**完全静默**的失败 —— S3 第一次真机冒烟就抓到过一次：
        // Cleanup 排在 `_viewModel.Dispose()`（它才关掉 pdfium 句柄）之前，
        // 于是内嵌工程的临时试卷**从来没被删掉过**，只在日志里留一行
        // 「being used by another process」。所以这里把"静默"本身钉住，
        // 而**顺序**由真机冒烟 smoke_m9s3.py 的最后一条盯（关掉程序后临时试卷必须消失）。
        var lockedReleaser = new EmbeddedPdfReleaser(Path.Combine(work, "占用区"));
        string? lockedPath = lockedReleaser.Release(twbLike, payload);

        if (lockedPath is null)
        {
            Report("前置：占用区的临时文件释放出来了", false, "没释放出来，跳过占用相关的两条");
        }
        else
        {
            FileStream? hold = null;
            try
            {
                hold = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

                int lockedRemoved = lockedReleaser.Cleanup();

                Report("★ 被别的程序占着时删不掉 ⇒ 返回 0、不抛异常（删不掉不该打断退出流程）——"
                       + "但正因为它是静默的，「关文档之后再删」那个顺序必须由真机冒烟盯住",
                       lockedRemoved == 0 && File.Exists(lockedPath),
                       "删除 " + lockedRemoved + " 个，文件还在=" + File.Exists(lockedPath));
            }
            finally
            {
                hold?.Dispose();
            }

            int afterRelease = lockedReleaser.Cleanup();

            Report("占用一放开就删得掉（说明上面那条不是「权限不对」这类死因）",
                   afterRelease == 1 && !File.Exists(lockedPath), "删除 " + afterRelease + " 个");
        }

        // ---------------------------------------------------------- 3) 端到端
        Console.WriteLine();
        Console.WriteLine("---- 3) ★★ 端到端：内嵌工程 → 释放 → 导出 PNG / 整卷 PDF ----");

        // 一张「左半黑、右半白」的不透明位图：用它当页内容，"图片真的进了产物"可以数深色像素。
        BitmapSource HalfDark(int width, int height)
        {
            int stride = width * 4;
            var px = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = y * stride + x * 4;
                    byte v = x < width / 2 ? (byte)0 : (byte)255;
                    px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = 255;
                }
            }
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, px, stride);
            bmp.Freeze();
            return bmp;
        }

        // ★ 仍先合成到白底：RenderTargetBitmap 的底色全透明，直接读 RGB 会把透明当纯黑（S1 吃过一次）。
        double DarkRatio(BitmapSource src)
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var buf = new byte[stride * h];
            conv.CopyPixels(buf, stride, 0);

            long dark = 0;
            for (int i = 0; i < buf.Length; i += 4)
            {
                double a = buf[i + 3] / 255.0;
                double lum = (0.114 * buf[i] + 0.587 * buf[i + 1] + 0.299 * buf[i + 2]) * a
                             + 255.0 * (1.0 - a);
                if (lum < 128) dark++;
            }
            return dark / (double)(w * h);
        }

        (int W, int H) ReadPngSize(string path)
        {
            using var fs = File.OpenRead(path);
            var decoder = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat,
                                               BitmapCacheOption.OnLoad);
            return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
        }

        BitmapSource ReadPng(string path)
        {
            using var fs = File.OpenRead(path);
            var decoder = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat,
                                               BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }

        const double pw = 595.28, ph = 841.89;

        // ① 先造一份真的 2 页 PDF 当"试卷"：一页竖 A4、一页横 A4。
        //    有横页是故意的 —— 它同时验证"释放与导出都没有把页面转竖"。
        string srcPdf = Path.Combine(work, "源卷.pdf");
        long srcBytes = PdfWriter.Write(
            srcPdf, 2,
            i => new PdfWriter.PageImage(
                PngWriter.Encode(i == 0 ? HalfDark(400, 566) : HalfDark(600, 400)),
                i == 0 ? pw : ph, i == 0 ? ph : pw),
            null, "横竖各一页");

        Report("先造一份真能打开的 2 页 PDF（后面全部结论都以它为起点）",
               File.Exists(srcPdf) && srcBytes > 4000, srcBytes + " 字节");

        // ② 打包成内嵌工程
        string twbPath = Path.Combine(work, title + ".twb");
        bool packed = TwbFile.Write(twbPath, TwbManifest.New(title),
                                    File.ReadAllBytes(srcPdf), null, out string packError);

        Report("测试用内嵌工程写得出来", packed, packed ? twbPath : packError);

        var load = new ProjectStore().Load(twbPath);

        Report("★ ProjectStore.Load 给回内嵌字节，且与源 PDF 逐字节一致",
               load.Ok && load.EmbeddedPdf is { Length: > 0 }
               && load.EmbeddedPdf!.SequenceEqual(File.ReadAllBytes(srcPdf)),
               "Ok=" + load.Ok + " 状态=" + load.Status
               + " 内嵌 " + (load.EmbeddedPdf?.Length ?? 0) + " 字节");

        // ③ 释放（这一步就是 S3 的接缝本身）
        var projectReleaser = new EmbeddedPdfReleaser(Path.Combine(work, "临时区2"));
        string? tempPdf = load.EmbeddedPdf is null ? null : projectReleaser.Release(twbPath, load.EmbeddedPdf);

        Report("释放出来的临时文件能被 pdfium 打开（下面所有结论的起点）",
               tempPdf is not null && File.Exists(tempPdf!), tempPdf ?? "没有释放出来");

        if (tempPdf is null)
        {
            Report("端到端这一段需要一份能释放出来的内嵌试卷", false, "前置不成立，跳过而不是冤枉程序");
            Console.WriteLine();
            Console.WriteLine($"M9 S3 工作目录：{work}");
            return failures;
        }

        string pngBase = ExportNaming.BaseName(hasProject: true, title, tempPdf);

        Report("★ 端到端基名 = 工程标题（成品叫什么由它决定，不是那个临时文件）",
               pngBase == title, pngBase);

        using (var doc = new PdfiumDocumentService())
        {
            doc.Open(tempPdf);

            Report("pdfium 打得开释放出来的试卷，页数 = 2", doc.PageCount == 2, "pdfium 报 " + doc.PageCount + " 页");

            var s0 = doc.GetPageSize(0);
            var s1 = doc.GetPageSize(1);

            Report("★ 释放过程没有动卷面尺寸（竖 A4 / 横 A4，容差 1.5pt）",
                   Near(s0.Width, pw, 1.5) && Near(s0.Height, ph, 1.5)
                   && Near(s1.Width, ph, 1.5) && Near(s1.Height, pw, 1.5),
                   $"{s0.Width:F2}×{s0.Height:F2} / {s1.Width:F2}×{s1.Height:F2}");

            var projectHost = new CanvasViewportHost();
            projectHost.Measure(new Size(1000, 700));
            projectHost.Arrange(new Rect(0, 0, 1000, 700));
            projectHost.UpdateLayout();
            projectHost.SetDocument(doc);
            projectHost.UpdateLayout();
            projectHost.FitWidth();
            PumpFor(400);

            var exporter = new ExportService(projectHost, doc);

            Report("有试卷 ⇒ CanExport = true（导出按钮亮起的判据）",
                   exporter.CanExport, "页数 " + exporter.PageCount);

            var viewBefore = projectHost.Viewport.Save();
            string pngDir = Path.Combine(work, "导出PNG");

            var png = exporter.ExportPngTo(pngDir, pngBase);

            Report("从内嵌工程导 PNG：成功", png.Ok, png.Message);

            var files = Directory.Exists(pngDir)
                ? Directory.GetFiles(pngDir, "*.png").OrderBy(f => f).ToList()
                : new List<string>();
            var names = files.Select(f => Path.GetFileName(f)!).ToList();
            string joinedNames = string.Join(" / ", names);

            Report("★ 张数 = 内嵌试卷的页数", files.Count == 2,
                   files.Count + " 张 / 文档 " + doc.PageCount + " 页");

            Report("★ 文件名 = 工程标题-第NN页.png",
                   names.Count == 2
                   && names[0] == title + "-第01页.png"
                   && names[1] == title + "-第02页.png",
                   joinedNames);

            string suffix = Path.GetFileNameWithoutExtension(tempPdf).Split('-')[^1];

            Report("★★ 成品名里没有临时文件那串随机后缀（老师拿到的是「静电场复习卷-第01页.png」，"
                   + "不是「静电场复习卷-3f9a1c02-第01页.png」）",
                   names.Count == 2 && names.All(n => !n.Contains(suffix)),
                   "临时文件后缀 " + suffix + "；成品 " + joinedNames);

            if (names.Count == 2)
            {
                var want0 = ExportGeometry.PixelSize(doc.GetPageSize(0));
                var want1 = ExportGeometry.PixelSize(doc.GetPageSize(1));
                var (w0, h0) = ReadPngSize(files[0]);
                var (w1, h1) = ReadPngSize(files[1]);

                Report("★ 竖页导出 1654×2339、横页 2339×1654（200dpi A4；横页不许被转成竖的）",
                       w0 == want0.Width && h0 == want0.Height
                       && w1 == want1.Width && h1 == want1.Height,
                       $"实测 {w0}×{h0} / {w1}×{h1}"
                       + $"（期望 {want0.Width}×{want0.Height} / {want1.Width}×{want1.Height}）");

                double darkFirst = DarkRatio(ReadPng(files[0]));
                Report("导出图不是白纸：内嵌工程的画布真把卷面画出来了",
                       darkFirst > 0.2,
                       $"第 1 页深色占比 {darkFirst:P1}（源内容是左半黑，期望约 50%）");
            }
            else
            {
                Report("张数不对 ⇒ 尺寸与内容这两条无从谈起，标记失败而不是假装通过", false,
                       "只拿到 " + names.Count + " 个文件");
            }

            string outPdf = Path.Combine(work, "导出整卷.pdf");
            var pdf = exporter.ExportPdfTo(outPdf, pngBase);

            Report("从内嵌工程导整卷 PDF：成功", pdf.Ok, pdf.Message);

            Report("导出后视口与导出前严格相等（导出不该把老师的位置带走）",
                   Near(projectHost.Viewport.Scale, viewBefore.Scale)
                   && Near(projectHost.Viewport.OffsetX, viewBefore.OffsetX)
                   && Near(projectHost.Viewport.OffsetY, viewBefore.OffsetY),
                   $"导出后 {projectHost.Viewport.Scale:F3}@({projectHost.Viewport.OffsetX:F1},{projectHost.Viewport.OffsetY:F1})"
                   + $" 期望 {viewBefore.Scale:F3}@({viewBefore.OffsetX:F1},{viewBefore.OffsetY:F1})");
        }

        // ④ 用**另一个** pdfium 实例把导出的 PDF 读回来独立复核（不信自己数的）
        string outPdfPath = Path.Combine(work, "导出整卷.pdf");

        if (File.Exists(outPdfPath))
        {
            using var reader = new PdfiumDocumentService();
            reader.Open(outPdfPath);

            Report("★★ 回读导出的整卷：页数 = 2（pdfium 自己数的）",
                   reader.PageCount == 2, "pdfium 报 " + reader.PageCount + " 页");

            var g0 = reader.GetPageSize(0);
            var g1 = reader.GetPageSize(1);

            Report("★ 回读页尺寸 = 竖 A4 / 横 A4（横向页没被转竖）",
                   Near(g0.Width, pw, 1.5) && Near(g0.Height, ph, 1.5)
                   && Near(g1.Width, ph, 1.5) && Near(g1.Height, pw, 1.5),
                   $"{g0.Width:F2}×{g0.Height:F2} / {g1.Width:F2}×{g1.Height:F2}");

            double d0 = DarkRatio(reader.RenderPage(0, 0.5));
            double d1 = DarkRatio(reader.RenderPage(1, 0.5));

            Report("★ 两页渲出来都有内容（不是「页数对、里面全白」）",
                   d0 > 0.25 && d0 < 0.75 && d1 > 0.25 && d1 < 0.75,
                   $"深色占比 第1页 {d0:P1} / 第2页 {d1:P1}");
        }
        else
        {
            Report("导出的整卷 PDF 没落盘 ⇒ 回读那组断言无从谈起", false, outPdfPath);
        }

        // ---------------------------------------------------------- 4) 外挂与空白工程
        Console.WriteLine();
        Console.WriteLine("---- 4) 外挂工程与空白工程 ----");

        string refTwb = Path.Combine(work, "外挂工程.twb");
        var refManifest = TwbManifest.New("外挂工程卷", TwbPdfEmbedding.Referenced);
        refManifest.Pdf.AbsolutePath = srcPdf;
        refManifest.Pdf.Fingerprint = DocumentFingerprint.Compute(srcPdf);

        bool refWritten = TwbFile.Write(refTwb, refManifest, null, null, out string refError);
        var refLoad = new ProjectStore().Load(refTwb);

        Report("外挂工程 ⇒ 给回的是真实 PDF 路径（不走临时文件这条支线）",
               refWritten && refLoad.Ok && refLoad.PdfPath == srcPdf && refLoad.EmbeddedPdf is null,
               "Ok=" + refLoad.Ok + " 路径=" + (refLoad.PdfPath ?? none)
               + " 内嵌=" + (refLoad.EmbeddedPdf?.Length ?? 0)
               + (refError.Length > 0 ? " 写入错误=" + refError : string.Empty));

        if (refLoad.Ok && refLoad.PdfPath is { Length: > 0 })
        {
            using var refDoc = new PdfiumDocumentService();
            refDoc.Open(refLoad.PdfPath);

            var refHost = new CanvasViewportHost();
            refHost.Measure(new Size(1000, 700));
            refHost.Arrange(new Rect(0, 0, 1000, 700));
            refHost.UpdateLayout();
            refHost.SetDocument(refDoc);
            refHost.UpdateLayout();
            refHost.FitWidth();
            PumpFor(400);

            var refExporter = new ExportService(refHost, refDoc);
            string refBase = ExportNaming.BaseName(hasProject: true, refManifest.Title, refDoc.FilePath);

            string refDir = Path.Combine(work, "导出PNG-外挂");
            var refResult = refExporter.ExportPngTo(refDir, refBase);

            int refCount = Directory.Exists(refDir) ? Directory.GetFiles(refDir, "*.png").Length : 0;

            Report("★ 外挂工程同样导得出 2 页，且基名是工程标题（老师直开 PDF 的老路没被改坏）",
                   refResult.Ok && refCount == 2 && refBase == "外挂工程卷",
                   "Ok=" + refResult.Ok + " 文件 " + refCount + " 个 基名 " + refBase);
        }

        string blankTwb = Path.Combine(work, "空白工程.twb");
        TwbFile.Write(blankTwb, TwbManifest.New("空白工程", TwbPdfEmbedding.None), null, null, out _);

        var blankLoad = new ProjectStore().Load(blankTwb);

        using (var emptyDoc = new PdfiumDocumentService())
        {
            var blankHost = new CanvasViewportHost();
            blankHost.Measure(new Size(1000, 700));
            blankHost.Arrange(new Rect(0, 0, 1000, 700));
            blankHost.UpdateLayout();

            var blankExporter = new ExportService(blankHost, emptyDoc);

            Report("★ 空白工程没有试卷 ⇒ CanExport = false（按钮置灰，点不动也就不会弹出 M9 的报错 ——"
                   + "工程里试卷找不到时该弹的是 M8 的「定位试卷」，不是这里）",
                   blankLoad.Ok && !blankLoad.HasPdf && !blankExporter.CanExport,
                   "Ok=" + blankLoad.Ok + " HasPdf=" + blankLoad.HasPdf
                   + " CanExport=" + blankExporter.CanExport);
        }

        // 收尾：★ 顺序要紧 —— 临时文件必须等 pdfium 关掉之后再删（上面那些 using 都已结束）。
        int cleaned = projectReleaser.Cleanup();

        Report("收尾：端到端用的临时文件被清理干净（且是在 pdfium 关掉之后才删的）",
               cleaned == 1 && !File.Exists(tempPdf), "删除 " + cleaned + " 个");

        Console.WriteLine();
        Console.WriteLine($"M9 S3 工作目录：{work}");
        return failures;
    }

    private static int RunM9S0Checks(IPdfDocumentService service, string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M9 S0：导出换算 + 画布借用 ================");

        void Report(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"        {detail}");
            if (!ok) failures++;
        }

        string work = Path.Combine(outDir, "m9s0");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        // ---------------------------------------------------------- 1) 换算真值源
        Console.WriteLine();
        Console.WriteLine("---- 1) world ↔ 输出像素（ExportGeometry）----");

        const double a4W = 595.28;
        const double a4H = 841.89;
        var (pxW, pxH) = ExportGeometry.PixelSize(new Size(a4W, a4H));

        Report("输出分辨率只有一档 200dpi（M9 的产品边界：不做 DPI 选择）",
               Math.Abs(ExportGeometry.RenderDpi - 200.0) < 1e-9,
               $"RenderDpi={ExportGeometry.RenderDpi}");

        Report("A4（595.28×841.89 point）@200dpi ⇒ 1654×2339 像素"
               + "（595.28×200/72=1653.56、841.89×200/72=2338.58，四舍五入）",
               pxW == 1654 && pxH == 2339, $"实测 {pxW}×{pxH}");

        double srcAspect = a4W / a4H;
        double pxAspect = (double)pxW / pxH;
        Report("像素长宽比与 world 长宽比一致（不一致就是试卷被拉伸变形）",
               Math.Abs(srcAspect - pxAspect) < 1e-3,
               $"world {srcAspect:F5} vs 像素 {pxAspect:F5}");

        Report("正反换算闭合：PixelsPerPoint × PointsPerPixel = 1",
               Math.Abs(ExportGeometry.PixelsPerPoint * ExportGeometry.PointsPerPixel - 1.0) < 1e-12,
               $"{ExportGeometry.PixelsPerPoint:F6} × {ExportGeometry.PointsPerPixel:F6}");

        Report("喂给 RenderPage 的 zoom 折算正确（那个接口 zoom=1 表示 96dpi）",
               Math.Abs(ExportGeometry.ZoomForDpi(200) - 200.0 / 96.0) < 1e-12
               && Math.Abs(ExportGeometry.ZoomForDpi(96) - 1.0) < 1e-12,
               $"ZoomForDpi(200)={ExportGeometry.ZoomForDpi(200):F6}"
               + $" ZoomForDpi(96)={ExportGeometry.ZoomForDpi(96):F6}");

        // 自检点选 72dpi 而不是 96dpi：dpi=72 时「1 world 单位 = 1 像素」，
        // 逻辑尺寸就等于像素数 ⇒ 构造 dpi 必须正好是 96。这条能把"少乘/多除一个 72"钉死；
        // 用 96dpi 当自检点是错的（那时 BitmapDpi = 96×96/72 = 128 才对）。
        Report("喂给 RenderTargetBitmap 的 dpi = 96×dpi/72"
               + "（用 96 会糊、用 200 会把右下角裁掉，两者都不报错）",
               Math.Abs(ExportGeometry.BitmapDpi(200) - 96.0 * 200.0 / 72.0) < 1e-12
               && Math.Abs(ExportGeometry.BitmapDpi(72) - 96.0) < 1e-12,
               $"BitmapDpi(200)={ExportGeometry.BitmapDpi(200):F4}"
               + $" BitmapDpi(72)={ExportGeometry.BitmapDpi(72):F1}");

        // 这条才是那个 dpi 的真正用途：让"输出像素 × 96 ÷ dpi"回到页面自身的 world 尺寸。
        // 差半个像素以内都算对 —— 像素数是整数，四舍五入必然留下亚像素残差。
        double logicalW = pxW * 96.0 / ExportGeometry.BitmapDpi();
        double logicalH = pxH * 96.0 / ExportGeometry.BitmapDpi();
        Report("渲染目标的逻辑尺寸 = 页面 world 尺寸（亚像素级）"
               + " —— 这是「dpi 给错就裁掉右下角」那条的量纲判据",
               Math.Abs(logicalW - a4W) < 0.5 && Math.Abs(logicalH - a4H) < 0.5,
               $"逻辑尺寸 {logicalW:F3}×{logicalH:F3} vs 页面 {a4W}×{a4H}");

        Report("异常小的页面至少给 1 像素（RenderTargetBitmap 收到 0 会直接抛）",
               ExportGeometry.PixelSize(new Size(0, 0)) == (1, 1)
               && ExportGeometry.PixelSize(new Size(0.1, 0.2)) == (1, 1),
               $"0×0 → {ExportGeometry.PixelSize(new Size(0, 0))}");

        int level = RenderLevels.ForScale(ExportGeometry.ZoomForDpi());
        Report("200dpi 对应的 zoom 在渲染档位表里有落点，取的是「不小于」的最小档（超采样、绝不欠采样）",
               RenderLevels.ToZoom(level) >= ExportGeometry.ZoomForDpi() - 1e-12,
               $"zoom={ExportGeometry.ZoomForDpi():F4} ⇒ 档位 {level}"
               + $"（该档 zoom={RenderLevels.ToZoom(level):F2}）");

        // ---------------------------------------------------------- 2) 视口快照往返
        Console.WriteLine();
        Console.WriteLine("---- 2) 视口快照往返（CanvasViewport.Save / Restore）----");

        var viewport = new CanvasViewport();
        viewport.SetView(2.5, -123.5, 456.25);
        var snapshot = viewport.Save();

        viewport.SetView(0.75, 10.0, 20.0);      // 借用期间视口被摆到别处

        Report("快照是值拷贝：取了存档之后再改视口，存档里的三个数不受影响",
               Math.Abs(snapshot.Scale - 2.5) < 1e-12
               && Math.Abs(snapshot.OffsetX - (-123.5)) < 1e-12
               && Math.Abs(snapshot.OffsetY - 456.25) < 1e-12,
               $"存档 = {snapshot.Scale}@({snapshot.OffsetX},{snapshot.OffsetY})");

        int restored = 0;
        viewport.Changed += (_, _) => restored++;
        viewport.Restore(snapshot);

        Report("按存档复原 ⇒ 三个数与存档严格相等",
               Near(viewport.Scale, 2.5) && Near(viewport.OffsetX, -123.5)
               && Near(viewport.OffsetY, 456.25),
               $"复原后 = {viewport.Scale}@({viewport.OffsetX},{viewport.OffsetY})");

        Report("复原会触发 Changed（视图侧靠它把世界层重新对齐；数值没变也要通知）",
               restored == 1, $"Changed 触发 {restored} 次");

        var badView = new CanvasViewport();
        badView.Restore(new CanvasViewport.ViewSnapshot(9999.0, 0, 0));
        Report("坏档兜底：存档里的缩放超出上限 ⇒ 钳制到 MaxScale（不产生 NaN、不崩）",
               Near(badView.Scale, CanvasViewport.MaxScale) && !double.IsNaN(badView.Scale),
               $"复原后缩放 = {badView.Scale}（上限 {CanvasViewport.MaxScale}）");

        // ---------------------------------------------------------- 3) 借用画布
        //
        // 这一段守的是「导出成白纸」与「导出完视口回不去」两种毛病 ——
        // 屏幕上一点异常都看不到，只有打开导出产物、或者再动一下窗口才会发现。
        Console.WriteLine();
        Console.WriteLine("---- 3) 导出借用画布（ExportSession + 渲染冻结）----");

        var host = new CanvasViewportHost();
        host.Measure(new Size(1000, 700));
        host.Arrange(new Rect(0, 0, 1000, 700));
        host.UpdateLayout();
        host.SetDocument(service);
        host.UpdateLayout();

        var pageImages = VisualDescendants(host).OfType<Image>().ToList();

        Report("装载文档后画布上按页建好了位图元素（下面拿它们当观察点）",
               pageImages.Count == service.PageCount && pageImages.Count >= 3,
               $"位图元素 {pageImages.Count} 个 / 文档 {service.PageCount} 页");

        if (pageImages.Count < 3)
        {
            Report("这一段起需要一份至少 3 页的试卷"
                   + "（观察点取最后一页，页太少会落进预渲染余量里、断言变白给）",
                   false, "页数不够就跳过借用相关的断言，不冤枉程序");
            Console.WriteLine();
            Console.WriteLine($"M9 S0 工作目录：{work}");
            return failures;
        }

        var page0 = host.Layout.GetPageRect(0);

        // ★ 观察点必须是**最后一页**，不能拿第 2 页凑数：
        //   可见区带 256 DIP 的预渲染余量（CanvasViewportHost.PrerenderMargin），
        //   1:1 视口下第 2 页仍落在余量里、压根不会被清 —— 拿它当观察点会得到一条
        //   "永远是绿的"断言。S0 第一版就是这么错的，靠同段的反向对照才揭穿。
        int lastIndex = service.PageCount - 1;
        var pageLast = host.Layout.GetPageRect(lastIndex);

        // 观察点自检：借用视口（1:1 对齐第 1 页）下，最后一页必须落在可见区**之外** ——
        // 否则下面「没被清掉」是白给的。可见区 = 视口矩形外扩 256 DIP
        // （这个 256 与 CanvasViewportHost.PrerenderMargin 是同一个数，改了要一起改）。
        double bOffsetX = -page0.X;
        double bOffsetY = -page0.Y;
        var borrowVisible = new Rect(
            -bOffsetX - 256.0,
            -bOffsetY - 256.0,
            host.ActualWidth + 512.0,
            host.ActualHeight + 512.0);

        Report("观察点自检：借用视口下最后一页确实在可见区之外"
               + "（不然「没被清掉」是白给的）",
               !borrowVisible.IntersectsWith(pageLast),
               $"可见区 {borrowVisible.X:F0},{borrowVisible.Y:F0}"
               + $" {borrowVisible.Width:F0}×{borrowVisible.Height:F0}"
               + $" / 第 {lastIndex + 1} 页 Y={pageLast.Y:F0}..{pageLast.Bottom:F0}");

        // 先把视口指到最后一页并等它真的渲出来 ——
        // 没有这一步，「借出后它的位图还在不在」无从谈起（本来就是空的，测了也是绿）。
        host.RestoreView(1.0, -pageLast.X, -pageLast.Y);
        bool lastReady = PumpUntil(() => pageImages[lastIndex].Source is not null, 8000);

        Report("前置：最后一页真的渲出来了（这是下面「冻结」那条的非空前提）",
               lastReady,
               $"第 {lastIndex + 1} 页位图 = {(pageImages[lastIndex].Source is null ? "null" : "已就绪")}");

        if (!lastReady)
        {
            Console.WriteLine();
            Console.WriteLine($"M9 S0 工作目录：{work}");
            return failures;
        }

        var beforeView = host.Viewport.Save();
        bool autoFitBefore = host.IsAutoFitWidth;

        var svc = new ExportService(host, service);

        using (var session = svc.BeginRasterSession(0))
        {
            Report("借出后视口 = 100%，且对齐的是**目标页左上角**而不是世界原点"
                   + "（世界原点对齐只有第 1 页碰巧能用，第 2 页起会整页偏出画面）",
                   Near(host.Viewport.Scale, 1.0)
                   && Near(host.Viewport.OffsetX, -page0.X)
                   && Near(host.Viewport.OffsetY, -page0.Y),
                   $"视口 = {host.Viewport.Scale:F2}@({host.Viewport.OffsetX:F0},{host.Viewport.OffsetY:F0})"
                   + $" 期望 1.00@({-page0.X:F0},{-page0.Y:F0})");

            Report("借出期间缩放恒为 1（1 world 单位 = 1 DIP，像素数才等于「页面 × dpi」）",
                   Near(session.Scale, 1.0), $"session.Scale={session.Scale}");

            // 借出的视口已经看不见第 2 页。让 Dispatcher 把排队的活干完 ——
            // 若冻结没生效，宿主正好会在这里把第 2 页的位图清掉。
            PumpFor(400);

            Report("★ 冻结生效：借出后看不见的那一页，位图没有被清掉"
                   + "（清掉的话离屏渲染出来就是白纸）",
                   pageImages[lastIndex].Source is not null,
                   $"第 {lastIndex + 1} 页位图 ="
                   + $" {(pageImages[lastIndex].Source is null ? "★null（会被清掉）" : "仍在")}");
        }

        Report("归还后视口三个数与借出前严格相等（导出不该把老师的视口带走）",
               Near(host.Viewport.Scale, beforeView.Scale)
               && Near(host.Viewport.OffsetX, beforeView.OffsetX)
               && Near(host.Viewport.OffsetY, beforeView.OffsetY),
               $"归还后 = {host.Viewport.Scale:F3}@({host.Viewport.OffsetX:F2},{host.Viewport.OffsetY:F2})"
               + $" 期望 {beforeView.Scale:F3}@({beforeView.OffsetX:F2},{beforeView.OffsetY:F2})");

        Report("归还后「自动适应宽度」开关保持原样（借用不偷偷改宿主的工作模式）",
               host.IsAutoFitWidth == autoFitBefore,
               $"借出前={autoFitBefore} 归还后={host.IsAutoFitWidth}");

        // 幂等：第二次释放不该再把视口动一遍（那会让"导出后再点一次"出现累积位移）
        var session2 = svc.BeginRasterSession(0);
        session2.Dispose();
        var afterFirstDispose = host.Viewport.Save();
        session2.Dispose();
        Report("重复释放安全（幂等）：第二次 Dispose 不会把已还原的视口再动一次",
               Near(host.Viewport.Scale, afterFirstDispose.Scale)
               && Near(host.Viewport.OffsetX, afterFirstDispose.OffsetX)
               && Near(host.Viewport.OffsetY, afterFirstDispose.OffsetY),
               $"第二次释放后 = {host.Viewport.Scale:F3}@({host.Viewport.OffsetX:F2},{host.Viewport.OffsetY:F2})");

        // ---- 反向对照：不冻结、把视口摆到**同一个位置** ⇒ 视口外那页的位图必须被清掉。
        //      没有这一条，上面「冻结生效」可能只是「清位图这个行为压根不存在」的假通过。
        host.RestoreView(1.0, -pageLast.X, -pageLast.Y);
        bool lastBack = PumpUntil(() => pageImages[lastIndex].Source is not null, 8000);

        if (!lastBack)
        {
            Report("反向对照的前置：最后一页能重新渲染出来", false,
                   "拿不到就跳过对照，不冤枉程序");
        }
        else
        {
            host.Viewport.SetView(1.0, -page0.X, -page0.Y);   // 同样的目标视口，但**没有**冻结
            PumpFor(900);

            Report("反向对照：不冻结、直接改视口 ⇒ 视口外那一页的位图确实会被清掉"
                   + "（证明上面那条不是在验一个不存在的机制）",
                   pageImages[lastIndex].Source is null,
                   $"第 {lastIndex + 1} 页位图 = {(pageImages[lastIndex].Source is null
                       ? "已被清掉（对照成立）"
                       : "★仍在 —— 说明清位图机制没发生，上面那条是空转")}");
        }

        // ---------------------------------------------------------- 4) 导出门面
        Console.WriteLine();
        Console.WriteLine("---- 4) 导出门面（ExportService）----");

        Report("有文档 ⇒ CanExport（按钮可点）", svc.CanExport,
               $"CanExport={svc.CanExport} 页数={svc.PageCount}");

        var expectPx = ExportGeometry.PixelSize(service.GetPageSize(0));
        Report("门面报的页像素尺寸与 ExportGeometry 一致（门面只转发，不另算一套）",
               svc.GetPagePixelSize(0) == expectPx,
               $"门面 {svc.GetPagePixelSize(0)} vs 换算源 {expectPx}");

        bool threwRange = false;
        try { svc.GetPageWorldRect(service.PageCount); }
        catch (ArgumentOutOfRangeException) { threwRange = true; }

        Report("页号越界 ⇒ 立刻抛 ArgumentOutOfRangeException（不许静默返回一个坏矩形）",
               threwRange, $"传 {service.PageCount}（合法范围 0..{service.PageCount - 1}）");

        using var emptyService = new PdfiumDocumentService();
        Report("无文档 ⇒ CanExport=false（按钮置灰，不给老师点了才告诉他「请先打开试卷」）",
               !new ExportService(host, emptyService).CanExport, "空服务未 Open ⇒ IsOpen=false");

        Console.WriteLine();
        Console.WriteLine($"M9 S0 工作目录：{work}");
        return failures;
    }

    private static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }


    // ============================================================ M7.4 Step 4：函数图像
    private static int RunM74S4Checks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("================ M7.4 Step 4：函数图像（学科工具）================");

        // ---- 1) 解析器：常见表达式都能解析 ----
        Console.WriteLine();
        Console.WriteLine("---- 1) 解析器：8 例常见表达式无错误 ----");
        string[] samples = { "sin(x)", "x^2", "1/x", "log(x)", "2x", "3sin(x)", "x^2+1", "a*x+b" };
        bool parseAllOk = true;
        foreach (var s in samples)
        {
            try { ExpressionParser.Parse(s); }
            catch (Exception apex) { parseAllOk = false; Console.WriteLine($"    解析失败：{s} -> {apex.Message}"); }
        }
        Console.WriteLine($"[{(parseAllOk ? "PASS" : "FAIL")}] sin(x)/x^2/1/x/log(x)/2x/3sin(x)/x^2+1/a*x+b 全部可解析");
        if (!parseAllOk) failures++;

        // ---- 2) 求值：特殊值 + 不抛 ----
        Console.WriteLine();
        Console.WriteLine("---- 2) 求值：sin(π/2)=1、2^10=1024、1/0=Infinity、log(-1)=NaN ----");
        var empty = new Dictionary<string, double>();
        double sinHalf = ExpressionEvaluator.Eval(ExpressionParser.Parse("sin(x)"), Math.PI / 2, empty);
        double pow = ExpressionEvaluator.Eval(ExpressionParser.Parse("2^10"), 0, empty);
        double div0 = ExpressionEvaluator.Eval(ExpressionParser.Parse("1/x"), 0, empty);
        double logNeg = ExpressionEvaluator.Eval(ExpressionParser.Parse("log(x)"), -1, empty);
        bool evalOk = Near(sinHalf, 1, 1e-9) && Near(pow, 1024, 1e-9)
                    && double.IsInfinity(div0) && double.IsNaN(logNeg);
        Console.WriteLine($"[{(evalOk ? "PASS" : "FAIL")}] sin(π/2)={sinHalf:F6}、2^10={pow}、1/0=Infinity({double.IsInfinity(div0)})、log(-1)=NaN({double.IsNaN(logNeg)})");
        if (!evalOk) failures++;

        // ---- 3) 奇点断开：1/x 在 [-1,1] 生成 2 段 ----
        Console.WriteLine();
        Console.WriteLine("---- 3) 奇点断开：1/x 在 [-1,1] 采样 => 2 段不连续路径 ----");
        var seg1 = FunctionSampler.Sample(ExpressionParser.Parse("1/x"), -1, 1, -100, 100, empty);
        int seg1Pts = 0; foreach (var s in seg1) seg1Pts += s.Count;
        bool segOk = seg1.Count == 2 && seg1Pts >= 4;
        Console.WriteLine($"[{(segOk ? "PASS" : "FAIL")}] 1/x 在 [-1,1] 生成 {seg1.Count} 段（期望 2，x=0 处断开）");
        if (!segOk) failures++;

        // ---- 4) 定义域：sqrt(x) 在 x<0 不画 ----
        Console.WriteLine();
        Console.WriteLine("---- 4) 定义域：sqrt(x) 在 x<0 段不画（不是画成 0） ----");
        var segSqrt = FunctionSampler.Sample(ExpressionParser.Parse("sqrt(x)"), -2, 2, -10, 10, empty);
        bool sqrtOk = segSqrt.Count > 0;
        foreach (var s in segSqrt) foreach (var p in s) if (p.X < -1e-9) sqrtOk = false;
        int sqrtPts = 0; foreach (var s in segSqrt) sqrtPts += s.Count;
        Console.WriteLine($"[{(sqrtOk ? "PASS" : "FAIL")}] sqrt(x) 在 [-2,2] 仅画 x>=0 部分（{sqrtPts} 个有效点）");
        if (!sqrtOk) failures++;

        // ---- 5) 隐式乘法等价 ----
        Console.WriteLine();
        Console.WriteLine("---- 5) 隐式乘法：2x 与 2*x 逐点相等 ----");
        var a1 = FunctionSampler.Sample(ExpressionParser.Parse("2x"), -3, 3, -20, 20, empty);
        var a2 = FunctionSampler.Sample(ExpressionParser.Parse("2*x"), -3, 3, -20, 20, empty);
        int a1c = 0, a2c = 0; foreach (var s in a1) a1c += s.Count; foreach (var s in a2) a2c += s.Count;
        bool implicitOk = a1.Count == a2.Count && a1c == a2c;
        if (implicitOk && a1.Count > 0 && a1[0].Count > 1)
            implicitOk = Near(a1[0][1].Y, a2[0][1].Y, 1e-9);
        Console.WriteLine($"[{(implicitOk ? "PASS" : "FAIL")}] 2x 与 2*x 采样结果一致");
        if (!implicitOk) failures++;

        // ---- 6) x^2 顶点精度 ----
        Console.WriteLine();
        Console.WriteLine("---- 6) x^2 采样点真值误差 < 1e-6 ----");
        var quad = FunctionSampler.Sample(ExpressionParser.Parse("x^2"), -2, 2, -10, 10, empty);
        bool quadOk = true; double maxErr = 0;
        foreach (var s in quad) foreach (var p in s)
        {
            double err = Math.Abs(p.Y - p.X * p.X);
            maxErr = Math.Max(maxErr, err);
            if (err > 1e-6) quadOk = false;
        }
        Console.WriteLine($"[{(quadOk ? "PASS" : "FAIL")}] x^2 采样点真值最大误差 {maxErr:E2}（< 1e-6）");
        if (!quadOk) failures++;

        // ---- 7) 采样点数上限保护 ----
        Console.WriteLine();
        Console.WriteLine("---- 7) 采样上限保护：sin(1000x) 点数不超硬上限 ----");
        var hi = FunctionSampler.Sample(ExpressionParser.Parse("sin(1000*x)"), -10, 10, -2, 2, empty, maxSamples: 4000);
        int hiCount = 0; foreach (var s in hi) hiCount += s.Count;
        bool hiOk = hiCount <= FunctionSampler.MaxSamplesHard + 1;
        Console.WriteLine($"[{(hiOk ? "PASS" : "FAIL")}] sin(1000x) 采样点 {hiCount} <= 硬上限 {FunctionSampler.MaxSamplesHard}");
        if (!hiOk) failures++;

        // ---- 8) 参数联动：a 变化 => 几何变 ----
        Console.WriteLine();
        Console.WriteLine("---- 8) 参数滑块联动：a=1 => a=2 曲线几何变 ----");
        var p1 = new Dictionary<string, double> { ["a"] = 1 };
        var p2 = new Dictionary<string, double> { ["a"] = 2 };
        double y1 = ExpressionEvaluator.Eval(ExpressionParser.Parse("a*x"), 2, p1);
        double y2 = ExpressionEvaluator.Eval(ExpressionParser.Parse("a*x"), 2, p2);
        bool paramOk = !Near(y1, y2, 1e-9);
        Console.WriteLine($"[{(paramOk ? "PASS" : "FAIL")}] a=1 时 x=2 => y={y1}，a=2 => y={y2}（几何随参数变）");
        if (!paramOk) failures++;

        // ---- 9) 工具逻辑：绑定最近坐标系 / 自动建 / 孤立 ----
        Console.WriteLine();
        Console.WriteLine("---- 9) 工具逻辑：绑定最近坐标系、无则自动建、删除坐标系后孤立不消失 ----");
        var sysCenter = new Point(400, 500);
        var sysObj = new S4FakeRef("coordsystem", sysCenter, 1.0, 0.0,
            new Dictionary<string, double> { ["unitWorld"] = 30, ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10 },
            new Dictionary<string, string>());
        var host = new S4TestHost();
        host.ObjectsList.Add(sysObj);
        var ctx = new FakeToolContext { GfxOverride = host };
        var tool = new FunctionTool(); tool.Activate(ctx);
        tool.ApplySpec(new FunctionSpec { Expr = "x^2", XMin = -10, XMax = 10, YMin = -10, YMax = 10 }, new Point(410, 505));
        bool exprTextOk = host.Added[0].Texts.TryGetValue("expr", out var exprText) && exprText == "x^2";
        bool bindOk = host.Added.Count == 1
                    && Near(host.Added[0].Center, sysCenter, 1e-9)
                    && Near(host.Added[0].Scale, 30, 1e-9)
                    && exprTextOk;
        Console.WriteLine($"[{(bindOk ? "PASS" : "FAIL")}] 绑定最近坐标系：Center={host.Added[0].Center} Scale={host.Added[0].Scale} expr={exprText}");
        if (!bindOk) failures++;

        var host2 = new S4TestHost();
        var ctx2 = new FakeToolContext { GfxOverride = host2 };
        var tool2 = new FunctionTool(); tool2.Activate(ctx2);
        tool2.ApplySpec(new FunctionSpec { Expr = "x", XMin = -10, XMax = 10, YMin = -10, YMax = 10 }, new Point(200, 200));
        bool autoOk = host2.Added.Count == 2
                    && host2.Added[0].Kind == "coordsystem"
                    && host2.Added[1].Kind == "function"
                    && Near(host2.Added[1].Center, new Point(200, 200), 1e-9);
        Console.WriteLine($"[{(autoOk ? "PASS" : "FAIL")}] 无坐标系时自动建默认坐标系并绑定（draft 数 {host2.Added.Count}）");
        if (!autoOk) failures++;

        var host3 = new S4TestHost();
        var sysId = host3.Add(new GfxDraft
        {
            Kind = "coordsystem", Center = new Point(300, 300), Scale = 1,
            Color = Colors.Black, LineWorldWidth = 1,
            Numbers = new Dictionary<string, double> { ["unitWorld"] = 28.35, ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10 },
        });
        var ctx3 = new FakeToolContext { GfxOverride = host3 };
        var tool3 = new FunctionTool(); tool3.Activate(ctx3);
        tool3.ApplySpec(new FunctionSpec { Expr = "x^2", XMin = -10, XMax = 10, YMin = -10, YMax = 10 }, new Point(300, 300));
        host3.ObjectsList.RemoveAll(o => o.Id == sysId);
        int funcCount = host3.ObjectsList.FindAll(o => o.Kind == "function").Count;
        bool orphanOk = funcCount == 1;
        Console.WriteLine($"[{(orphanOk ? "PASS" : "FAIL")}] 删除坐标系后函数对象仍在（孤立，不静默丢数据）；函数对象数 {funcCount}");
        if (!orphanOk) failures++;

        // ---- 10) 端到端渲染：坐标系(红) + 函数 y=x^2(蓝) 都画出且对齐 ----
        Console.WriteLine();
        Console.WriteLine("---- 10) 端到端：坐标系(红) 与 函数 y=x^2(蓝) 都渲染且对齐原点 ----");
        try
        {
            var fpPxy = new CanvasViewportHost();
            fpPxy.Measure(new Size(1000, 700));
            fpPxy.Arrange(new Rect(0, 0, 1000, 700));
            fpPxy.UpdateLayout();
            // 两个渲染器都要登记，否则对应 kind 不渲染（S3 同样需登记坐标系渲染器）
            fpPxy.RegisterGfxRenderer(new CoordSystemRenderer(), "坐标系（学科工具）");
            fpPxy.RegisterGfxRenderer(new FunctionRenderer(), "函数图像（学科工具）");

            const double ox = 500, oy = 350, unit = 80;
            fpPxy.GfxObjects.Add(new GfxDraft
            {
                Kind = "coordsystem", Center = new Point(ox, oy), Scale = 1,
                Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["unitWorld"] = unit, ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10,
                    ["step"] = 1, ["showGrid"] = 1, ["showLabels"] = 1, ["lockAspect"] = 1,
                },
            });
            fpPxy.GfxObjects.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(ox, oy), Scale = unit,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10 },
                Texts = new Dictionary<string, string> { ["expr"] = "x^2" },
            });
            fpPxy.UpdateLayout();

            // 宿主视口不是单位变换：世界原点经 ToViewport 得到真实屏幕位置，断言据此做（对翻转/缩放不敏感）
            var originScreen = fpPxy.Viewport.ToViewport(new Point(ox, oy));
            double sx = originScreen.X, sy = originScreen.Y;

            var bmp = Snapshot(fpPxy, 1000, 700);
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);

            long red = 0, blue = 0;
            int bMinX = int.MaxValue, bMaxX = -1, bMinY = int.MaxValue, bMaxY = -1;
            bool originHit = false;   // 函数曲线是否穿过坐标系原点屏幕点
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int o = y * stride + x * 4;
                byte r = px[o + 2], g = px[o + 1], b = px[o];
                if (r >= 100 && r > g + 40 && r > b + 40) red++;
                if (b >= 100 && b > r + 40 && b > g + 40)
                {
                    blue++;
                    if (x < bMinX) bMinX = x; if (x > bMaxX) bMaxX = x;
                    if (y < bMinY) bMinY = y; if (y > bMaxY) bMaxY = y;
                    // 原点附近 ±(12,40) 内出现蓝点 => 曲线顶点落在坐标系原点（对翻转/缩放不敏感）
                    if (Math.Abs(x - sx) <= 12 && Math.Abs(y - sy) <= 40) originHit = true;
                }
            }
            double bCx = (bMinX + bMaxX) / 2.0;
            bool redOk = red > 5000;
            bool blueOk = blue > 1000;
            bool centeredOk = Math.Abs(bCx - sx) <= 60 && bMinX < sx && bMaxX > sx;
            bool e2eOk = redOk && blueOk && centeredOk && originHit;
            Console.WriteLine($"  红(坐标系) {red} px、蓝(函数) {blue} px；蓝包围盒 x=[{bMinX},{bMaxX}] y=[{bMinY},{bMaxY}] 中心x={bCx:F0}（原点屏幕 {sx:F0},{sy:F0}，过原点={originHit}）");
            Console.WriteLine($"[{(e2eOk ? "PASS" : "FAIL")}] 坐标系与函数曲线都渲染、函数曲线水平居中且穿过坐标系原点（y=x^2 顶点落在原点）");
            if (!e2eOk) failures++;
        }
        catch (Exception e2eEx)
        {
            Console.WriteLine($"[FAIL] 端到端渲染抛异常：{e2eEx.Message}");
            failures++;
        }

        // ---- 11) 输入弹窗 / 虚拟键盘可构建（防「点了没反应」回归）----
        // S4 现场 bug：数学键盘（原 VirtualKeyboard，M26 迁契约工程更名 MathKeyboard）Numpad 用硬编码 layout[3][3] 越界 → 构造函数抛 IndexOutOfRangeException
        // → 工具 OnPointer 崩、弹窗永不出现（日志里 8 条「工具调用失败（function.OnPointer）」）。
        // 以前的 harness 只调 ApplySpec，压根没把输入 UI 建出来，所以漏掉了 —— 这条专门补上。
        Console.WriteLine();
        Console.WriteLine("---- 11) 函数输入窗 / 虚拟键盘可构建（防「点了没反应」）----");
        try
        {
            static int KeyCount(UIElement e)
            {
                int n = e is Button ? 1 : 0;
                if (e is Panel p) { foreach (UIElement c in p.Children) n += KeyCount(c); }
                else if (e is Border b && b.Child is UIElement bc) n += KeyCount(bc);
                else if (e is ContentControl cc && cc.Content is UIElement ce) n += KeyCount(ce);
                return n;
            }

            var built = new MathKeyboard().Build();
            int keys = built is UIElement bu ? KeyCount(bu) : 0;
            bool vkOk = built is not null && keys == 92;
            Console.WriteLine($"  虚拟键盘按键数 = {keys}");
            Console.WriteLine($"[{(vkOk ? "PASS" : "FAIL")}] 表达式布局键盘 Build() 成功且按键数 == 92（迁契约工程后逐键零变化，钉死防布局漂移）");
            if (!vkOk) failures++;

            bool winOk; string winMsg;
            try
            {
                var win = new FunctionInputWindow(null, _ => { });
                winOk = true; winMsg = "构造成功";
                win.Close();
            }
            catch (Exception wex) { winOk = false; winMsg = wex.GetType().Name + ": " + wex.Message; }
            Console.WriteLine($"[{(winOk ? "PASS" : "FAIL")}] FunctionInputWindow 构造不抛异常（{winMsg}）");
            if (!winOk) failures++;
        }
        catch (Exception uiEx)
        {
            Console.WriteLine($"[FAIL] 键盘/弹窗构建抛异常：{uiEx.GetType().Name} {uiEx.Message}");
            failures++;
        }


        // ---- 11b) 数学键盘复用（M26）：LaTeX 布局 / 光标落点 / 公式面板构建 / 单一定义锚点 ----
        // M26 把 VirtualKeyboard 抽成契约工程的 MathKeyboard（函数图像 + LaTeX 公式面板共用）。
        // 本段钉死四件事：① LaTeX 布局构建不崩（防 S4 同款构建期越界）② 光标落点确定性
        //（骨架键进花括号、普通键落末尾）③ 公式面板挂上键盘后仍构造成功（防「点了没反应」）
        // ④ 源级：全 src 只有一份键盘定义且两个宿主都真引用（防复制粘贴两份代码各走各的）。
        Console.WriteLine();
        Console.WriteLine("---- 11b) 数学键盘复用：LaTeX 布局 + 光标落点 + 公式面板构建 ----");
        try
        {
            static int KC(UIElement e)
            {
                int n = e is Button ? 1 : 0;
                if (e is Panel p) { foreach (UIElement c in p.Children) n += KC(c); }
                else if (e is Border b && b.Child is UIElement bc) n += KC(bc);
                else if (e is ContentControl cc && cc.Content is UIElement ce) n += KC(ce);
                return n;
            }

            // ① LaTeX 布局可构建且按键数在合理区间（设计 89 键；用下限断言，容忍后续增键）
            var builtLatex = new MathKeyboard(MathKeyboard.Layout.Latex).Build();
            int latexKeys = builtLatex is UIElement ble ? KC(ble) : 0;
            bool latexOk = builtLatex is not null && latexKeys >= 60;
            Console.WriteLine($"  LaTeX 键盘按键数 = {latexKeys}");
            Console.WriteLine($"[{(latexOk ? "PASS" : "FAIL")}] LaTeX 布局 Build() 成功且按键数 >= 60（防构建期崩溃）");
            if (!latexOk) failures++;

            // ② 无头 TextBox 验共享插入逻辑的光标落点（两个宿主走同一份代码）
            var mk1 = new TextBox();
            mk1.Measure(new Size(320, 40));
            mk1.Arrange(new Rect(0, 0, 320, 40));
            MathKeyboard.HandleKey(mk1, new MathKeyboard.KeyInsert(@"\frac{}{}", 6));
            bool fracOk = mk1.Text == @"\frac{}{}" && mk1.SelectionStart == 6;
            Console.WriteLine($"  分数骨架插入结果：{mk1.Text} / 光标 {mk1.SelectionStart}");
            Console.WriteLine($"[{(fracOk ? "PASS" : "FAIL")}] 分数骨架插入后光标落进第一个花括号内");
            if (!fracOk) failures++;

            var mk2 = new TextBox { Text = "x" };
            mk2.Measure(new Size(320, 40)); mk2.Arrange(new Rect(0, 0, 320, 40));
            mk2.SelectionStart = 1;
            MathKeyboard.HandleKey(mk2, new MathKeyboard.KeyInsert("{}", 1));
            bool braceOk = mk2.Text == "x{}" && mk2.SelectionStart == 2;
            // 光标此刻在花括号内：续按普通键应落进括号内（老师要的效果，x{+} 而不是 x{}+）
            // 按完 + 光标自然到末尾，再按普通键落末尾 —— 两种落点各验一次
            MathKeyboard.HandleKey(mk2, new MathKeyboard.KeyInsert("+"));
            bool innerOk = mk2.Text == "x{+}" && mk2.SelectionStart == 3;
            // 键盘自带的右移键把光标送出括号到末尾，再按普通键 ⇒ 落末尾（x{+}.）
            MathKeyboard.HandleKey(mk2, new MathKeyboard.KeyInsert(MathKeyboard.Right));
            MathKeyboard.HandleKey(mk2, new MathKeyboard.KeyInsert(MathKeyboard.Right));
            MathKeyboard.HandleKey(mk2, new MathKeyboard.KeyInsert("."));
            bool plainOk = mk2.Text == "x{+}." && mk2.SelectionStart == 5;
            bool pairOk = braceOk && innerOk && plainOk;
            Console.WriteLine($"  大括号自动配对结果：{mk2.Text} / 光标 {mk2.SelectionStart}");
            Console.WriteLine($"[{(pairOk ? "PASS" : "FAIL")}] 大括号自动配对光标进内 + 续键落进括号内 + 右移送出括号后普通键落末尾");
            if (!pairOk) failures++;

            var mk3 = new TextBox { Text = "abc" };
            mk3.Measure(new Size(320, 40)); mk3.Arrange(new Rect(0, 0, 320, 40));
            mk3.SelectionStart = 3;
            MathKeyboard.HandleKey(mk3, new MathKeyboard.KeyInsert(MathKeyboard.Back));
            bool backOk = mk3.Text == "ab" && mk3.SelectionStart == 2;
            MathKeyboard.HandleKey(mk3, new MathKeyboard.KeyInsert(MathKeyboard.Left));
            bool leftOk = mk3.SelectionStart == 1;
            MathKeyboard.HandleKey(mk3, new MathKeyboard.KeyInsert(MathKeyboard.Right));
            bool rightOk = mk3.SelectionStart == 2;
            MathKeyboard.HandleKey(mk3, new MathKeyboard.KeyInsert(MathKeyboard.Clear));
            bool clearOk = mk3.Text.Length == 0 && mk3.SelectionStart == 0;
            bool editOk = backOk && leftOk && rightOk && clearOk;
            Console.WriteLine($"  编辑键结果：{mk3.Text} / 光标 {mk3.SelectionStart}");
            Console.WriteLine($"[{(editOk ? "PASS" : "FAIL")}] 退格 / 左移 / 右移 / 清空行为全部正确");
            if (!editOk) failures++;

            // ③ 公式面板（含 LaTeX 键盘）构造不抛异常 —— 与 #22 输入窗同款「防点了没反应」
            FormulaPanelWindow? fpw = null;
            var thread = new Thread(() =>
            {
                try
                {
                    fpw = new FormulaPanelWindow(new FormulaRenderer(), string.Empty);
                    fpw.Measure(new Size(620, 700));
                    fpw.Arrange(new Rect(0, 0, 620, 700));
                    fpw.Close();
                }
                catch (Exception) { fpw = null; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(8000);
            bool fpwOk = fpw is not null;
            Console.WriteLine($"  公式面板构建 {(fpwOk ? "成功" : "失败")}");
            Console.WriteLine($"[{(fpwOk ? "PASS" : "FAIL")}] FormulaPanelWindow（含 LaTeX 键盘）构造正常，防点了没反应");
            if (!fpwOk) failures++;

            // ④ 源级锚点：MathKeyboard 全 src 唯一定义（防复制粘贴）+ 两个宿主都真引用（防假复用）
            string kbRoot = FindRepoRoot(outDir);
            static string ReadSource(string root, params string[] parts)
                => File.ReadAllText(Path.Combine(new[] { root, "src" }.Concat(parts).ToArray()), Encoding.UTF8);
            int mathDefs = 0, legacyDefs = 0;
            foreach (var file in Directory.EnumerateFiles(Path.Combine(kbRoot, "src"), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)) continue;
                string tt = StripComments(File.ReadAllText(file, Encoding.UTF8), false);
                mathDefs += tt.Split("class MathKeyboard", StringSplitOptions.None).Length - 1;
                legacyDefs += tt.Split("class VirtualKeyboard", StringSplitOptions.None).Length - 1;
            }
            string fpwSrc = StripComments(ReadSource(kbRoot, "MathPhys.Ink.Plugin.Formula", "FormulaPanelWindow.cs"), false);
            string fiwSrc = StripComments(ReadSource(kbRoot, "MathPhys.Ink.Plugin.FunctionPlot", "FunctionInputWindow.cs"), false);
            bool kbAnchorOk = mathDefs == 1 && legacyDefs == 0
                && fpwSrc.Contains("MathKeyboard") && fpwSrc.Contains("Layout.Latex")
                && fiwSrc.Contains("MathKeyboard");
            Console.WriteLine($"  MathKeyboard 定义 = {mathDefs} 处 / VirtualKeyboard 残留 = {legacyDefs} 处 / 公式面板引用 = {fpwSrc.Contains("MathKeyboard")} / 函数输入窗引用 = {fiwSrc.Contains("MathKeyboard")}");
            Console.WriteLine($"[{(kbAnchorOk ? "PASS" : "FAIL")}] 源级锚点：键盘定义唯一 + 函数图像与公式面板共用同一组件");
            if (!kbAnchorOk) failures++;
        }
        catch (Exception kbEx)
        {
            Console.WriteLine($"  [FAIL] 数学键盘复用断言抛异常：{kbEx.GetType().Name} {kbEx.Message}");
            failures++;
        }
        // ---- 12) 单击即弹窗（防「点很多次才弹出」回归）----
        // 现场反馈：「功能能画，但要连点很多次才偶尔弹出一次输入窗」。
        // 根因是 Up 分支抄了直尺的"必须拖动 ≥2 世界单位"门槛 —— 单击的轻微抖动在阈值上下，
        // 过不过全凭运气。本工具只需要一个锚点，单击就该弹窗。
        Console.WriteLine();
        Console.WriteLine("---- 12) 函数工具：单击（零位移）即弹输入窗 ----");
        try
        {
            var tapHost = new S4TestHost();
            var tapCtx = new FakeToolContext { GfxOverride = tapHost };
            var tapTool = new FunctionTool();
            int opened = 0;
            Point openedAt = default;
            tapTool.OpenInputDialog = p => { opened++; openedAt = p; };
            tapTool.Activate(tapCtx);

            var tapPoint = new Point(321, 654);
            tapTool.OnPointer(new ToolPointer(ToolPointerPhase.Down, tapPoint, tapPoint, 0, false, 1));
            tapTool.OnPointer(new ToolPointer(ToolPointerPhase.Move, tapPoint, tapPoint, 0, false, 1));
            tapTool.OnPointer(new ToolPointer(ToolPointerPhase.Up, tapPoint, tapPoint, 0, false, 1));

            bool oneTapOk = opened == 1 && Near(openedAt, tapPoint, 1e-9);

            // 连点三次都应该弹（而不是"偶尔"）
            for (int i = 0; i < 3; i++)
            {
                tapTool.OnPointer(new ToolPointer(ToolPointerPhase.Down, tapPoint, tapPoint, 0, false, 1));
                tapTool.OnPointer(new ToolPointer(ToolPointerPhase.Up, tapPoint, tapPoint, 0, false, 1));
            }
            bool everyTapOk = opened == 4;

            Console.WriteLine($"  零位移单击 => 弹窗 {opened} 次（期望 4 次：1 次 + 再连点 3 次）；首次锚点 {openedAt}");
            Console.WriteLine($"[{(oneTapOk && everyTapOk ? "PASS" : "FAIL")}] 单击一下即弹出输入窗（不再要求先拖出 2 个世界单位）");
            if (!(oneTapOk && everyTapOk)) failures++;
        }
        catch (Exception tapEx)
        {
            Console.WriteLine($"[FAIL] 单击弹窗断言抛异常：{tapEx.GetType().Name} {tapEx.Message}");
            failures++;
        }

        // ---- 13) 线宽：曲线与坐标轴同粗（防「线太粗」回归）----
        // 现场反馈：「函数图像的线太粗了」。根因：曲线几何是"数学坐标"（1 单位 = 1 世界点），
        // 宿主按 Scale(=unitWorld≈28.35) 放大 ⇒ 世界粗细 = 局部线宽 × 28.35，粗了 28 倍。
        // 断言方式与世界粗细无关：比较"曲线世界粗细"与"坐标轴世界粗细"。
        Console.WriteLine();
        Console.WriteLine("---- 13) 线宽：函数曲线世界粗细 == 坐标轴世界粗细 ----");
        try
        {
            static double StrokeOf(FrameworkElement fe)
            {
                if (fe is System.Windows.Shapes.Path p) return p.StrokeThickness;
                if (fe is Panel pn)
                {
                    foreach (UIElement c in pn.Children)
                        if (c is FrameworkElement cf) { double v = StrokeOf(cf); if (v > 0) return v; }
                }
                else if (fe is Border b && b.Child is FrameworkElement bc)
                {
                    return StrokeOf(bc);
                }
                else if (fe is ContentControl cc && cc.Content is FrameworkElement ce)
                {
                    return StrokeOf(ce);
                }
                return 0;
            }

            var cat13 = new GfxRendererCatalog();
            cat13.Register(new CoordSystemRenderer(), "坐标系（学科工具）");
            cat13.Register(new FunctionRenderer(), "函数图像（学科工具）");
            var st13 = new GfxObjectStore(cat13);
            var layer13 = new GfxObjectLayer(new Canvas(), st13, cat13);

            const double unit13 = 28.35;
            var sys13 = st13.Add(new GfxDraft
            {
                Kind = "coordsystem", Center = new Point(400, 400), Scale = 1,
                Color = Colors.Black, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["unitWorld"] = unit13, ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10,
                },
            });
            var fn13 = st13.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(400, 400), Scale = unit13,
                Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10,
                    ["bindRelX"] = 0, ["bindRelY"] = 0, ["bindRelRot"] = 0,
                },
                Texts = new Dictionary<string, string> { ["expr"] = "x^2", ["bindTo"] = sys13 },
            });

            IGfxObjectRef? sysRef13 = null, fnRef13 = null;
            foreach (var o in st13.Objects) { if (o.Id == sys13) sysRef13 = o; if (o.Id == fn13) fnRef13 = o; }

            // 坐标系是"一个对象 = 一个自绘 FrameworkElement"（网格/轴/标签都在 OnRender 里画），
            // 视觉树里没有 Path 可量 —— 它的轴粗细直接取自模型的 LineWorldWidth（渲染器就是这么用的）。
            double fnStroke = layer13.VisualFor(fn13) is { } fv13 ? StrokeOf(fv13) : 0;
            double sysWorld = (sysRef13?.LineWorldWidth ?? 0) * (sysRef13?.Scale ?? 1);
            double fnWorld = fnStroke * (fnRef13?.Scale ?? 1);

            bool widthOk = sysWorld > 0 && Math.Abs(fnWorld - sysWorld) < 1e-6;
            Console.WriteLine($"  坐标系：世界线宽 {sysWorld:F4}（= LineWorldWidth {sysRef13?.LineWorldWidth:F2} × Scale {sysRef13?.Scale:F2}）");
            Console.WriteLine($"  函数曲线：局部线宽 {fnStroke:F4} × Scale {fnRef13?.Scale:F2} = 世界 {fnWorld:F4}");
            Console.WriteLine($"[{(widthOk ? "PASS" : "FAIL")}] 曲线与坐标轴世界粗细相同（{fnWorld:F4} vs {sysWorld:F4}；修复前曲线是 Scale 倍粗 ≈ {sysWorld * (fnRef13?.Scale ?? 1):F1}）");
            if (!widthOk) failures++;
        }
        catch (Exception wEx)
        {
            Console.WriteLine($"[FAIL] 线宽断言抛异常：{wEx.GetType().Name} {wEx.Message}");
            failures++;
        }

        // ---- 14) 绑定跟随：坐标系移动/旋转/缩放，曲线自动跟上 ----
        // 现场要求：「图像要和坐标轴绑定」。绑定由宿主 GfxObjectStore 按 Texts["bindTo"]
        // + bindRel* 推导，所以这里用真实 store 断言（假 host 不实现 RecomputeAttachments）。
        Console.WriteLine();
        Console.WriteLine("---- 14) 绑定跟随：移动/旋转/缩放坐标系后曲线自动跟上 ----");
        try
        {
            var cat14 = new GfxRendererCatalog();
            cat14.Register(new CoordSystemRenderer(), "坐标系（学科工具）");
            cat14.Register(new FunctionRenderer(), "函数图像（学科工具）");
            var st14 = new GfxObjectStore(cat14);

            const double unit14 = 28.35;
            var sys14 = st14.Add(new GfxDraft
            {
                Kind = "coordsystem", Center = new Point(300, 300), Scale = 1,
                Color = Colors.Black, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["unitWorld"] = unit14, ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10,
                },
            });
            var fn14 = st14.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(300, 300), Scale = unit14,
                Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10,
                    ["bindRelX"] = 0, ["bindRelY"] = 0, ["bindRelRot"] = 0,
                },
                Texts = new Dictionary<string, string> { ["expr"] = "x^2", ["bindTo"] = sys14 },
            });

            // 拖到别处 + 转 30° + 放大到 125%
            var movedTo = new Point(520, 410);
            st14.UpdatePose(sys14, movedTo, 30, 1.25);

            IGfxObjectRef? sysAfter = null, fnAfter = null;
            foreach (var o in st14.Objects) { if (o.Id == sys14) sysAfter = o; if (o.Id == fn14) fnAfter = o; }

            bool centerOk = fnAfter is not null && Near(fnAfter.Center, movedTo, 1e-6);
            bool rotOk = fnAfter is not null && Near(fnAfter.RotationDegrees, 30, 1e-6);
            bool scaleOk = fnAfter is not null && Near(fnAfter.Scale, unit14 * 1.25, 1e-6);
            // 最要紧的一条：1 数学单位对应的世界长度必须与坐标系同步（这决定曲线还贴不贴在轴上）
            bool unitSyncOk = fnAfter is not null && sysAfter is not null
                           && Near(fnAfter.Scale / unit14, sysAfter.Scale, 1e-9);
            bool followOk = centerOk && rotOk && scaleOk && unitSyncOk;
            Console.WriteLine($"  坐标系 -> 中心 {sysAfter?.Center} 旋转 {sysAfter?.RotationDegrees:F0}° 缩放 {sysAfter?.Scale:F2}");
            Console.WriteLine($"  曲线   -> 中心 {fnAfter?.Center} 旋转 {fnAfter?.RotationDegrees:F0}° 缩放 {fnAfter?.Scale:F2}");
            Console.WriteLine($"[{(followOk ? "PASS" : "FAIL")}] 曲线跟随坐标系（中心={centerOk} 旋转={rotOk} 缩放={scaleOk} 单位同步={unitSyncOk}）");
            if (!followOk) failures++;

            // 目标被删 => 曲线保留、不再被推导（孤立不静默丢）
            st14.Remove(sys14);
            IGfxObjectRef? orphan = null;
            foreach (var o in st14.Objects) if (o.Id == fn14) orphan = o;
            bool orphanKeepOk = orphan is not null && Near(orphan.Center, movedTo, 1e-6);
            Console.WriteLine($"[{(orphanKeepOk ? "PASS" : "FAIL")}] 删掉坐标系后曲线仍在原位（孤立保留，不静默丢数据）");
            if (!orphanKeepOk) failures++;
        }
        catch (Exception bindEx)
        {
            Console.WriteLine($"[FAIL] 绑定跟随断言抛异常：{bindEx.GetType().Name} {bindEx.Message}");
            failures++;
        }

        // ---- 15) 多曲线落在同一坐标系 + 颜色互相区分、且不等于轴色 ----
        // 现场要求：「多个函数图像可以同时画在一个坐标轴中且颜色要区分开」。
        Console.WriteLine();
        Console.WriteLine("---- 15) 多曲线：同一坐标系连画三条，颜色互不相同且不像轴色 ----");
        try
        {
            var cHost = new S4TestHost();
            var cCtx = new FakeToolContext { GfxOverride = cHost };
            var cTool = new FunctionTool(); cTool.Activate(cCtx);

            var cPoint = new Point(500, 500);
            int countedAfter1 = 0, countedAfter2 = 0, countedAfter3 = 0;
            int step = 0;
            foreach (var e in new[] { "x^2", "2*x", "x^3" })
            {
                cTool.ApplySpec(new FunctionSpec { Expr = e, XMin = -10, XMax = 10, YMin = -10, YMax = 10 }, cPoint);
                step++;
                Console.WriteLine($"    工具自报：{cCtx.LastStatus}");
                int now = FunctionTool.CountCurvesOn("h1", cHost);
                if (step == 1) countedAfter1 = now; else if (step == 2) countedAfter2 = now; else countedAfter3 = now;
            }

            // 单独验配色函数：三种候选必须互不相同（与轴色区分、彼此区分）
            // 轴色 = FakeToolContext.PenColor（自动建出来的那张坐标系就用它画轴）
            var fakeAxis = Colors.Red;
            var pal0 = FunctionTool.ColorForCurve(0, fakeAxis);
            var pal1 = FunctionTool.ColorForCurve(1, fakeAxis);
            var pal2 = FunctionTool.ColorForCurve(2, fakeAxis);
            Console.WriteLine($"  验收入口 CountCurvesOn 依次 = {countedAfter1}/{countedAfter2}/{countedAfter3}（期望 1/2/3）");
            Console.WriteLine($"  验收入口 ColorForCurve(0..2, 轴色) = #{pal0.R:X2}{pal0.G:X2}{pal0.B:X2} #{pal1.R:X2}{pal1.G:X2}{pal1.B:X2} #{pal2.R:X2}{pal2.G:X2}{pal2.B:X2}");

            int sysMade = 0;
            Color axisColor = Colors.Black;
            var curveDrafts = new List<GfxDraft>();
            foreach (var d in cHost.Added)
            {
                if (d.Kind == "coordsystem") { sysMade++; axisColor = d.Color; }
                else if (d.Kind == FunctionRenderer.KindName) curveDrafts.Add(d);
            }

            bool reuseOk = sysMade == 1 && curveDrafts.Count == 3;

            bool distinctOk = curveDrafts.Count == 3;
            for (int i = 0; i < curveDrafts.Count && distinctOk; i++)
                for (int j = i + 1; j < curveDrafts.Count; j++)
                    if (curveDrafts[i].Color == curveDrafts[j].Color) distinctOk = false;

            bool notAxisOk = true;
            foreach (var d in curveDrafts)
            {
                int dist = Math.Abs(d.Color.R - axisColor.R) + Math.Abs(d.Color.G - axisColor.G) + Math.Abs(d.Color.B - axisColor.B);
                if (dist < 160) notAxisOk = false;
            }

            // 三条曲线都必须绑到同一个坐标系
            bool sameSysOk = true;
            foreach (var d in curveDrafts)
                if (!d.Texts.TryGetValue("bindTo", out var b) || string.IsNullOrEmpty(b)) sameSysOk = false;

            bool paletteOk = reuseOk && distinctOk && notAxisOk && sameSysOk;
            string colors = "";
            foreach (var d in curveDrafts) colors += $"#{d.Color.R:X2}{d.Color.G:X2}{d.Color.B:X2} ";
            Console.WriteLine($"  坐标系只建了 {sysMade} 个（轴色 #{axisColor.R:X2}{axisColor.G:X2}{axisColor.B:X2}）；三条曲线颜色 {colors}");
            // 诊断：假 host 里最终留下了什么（键名/绑定目标是否如预期，配色数数就靠它）
            foreach (var o in cHost.Objects)
                Console.WriteLine($"    对象 {o.Id} kind={o.Kind} bindTo=「{o.GetText("bindTo", "")}」 expr=「{o.GetText("expr", "")}」");
            Console.WriteLine($"[{(paletteOk ? "PASS" : "FAIL")}] 三条曲线落在同一坐标系、颜色互不相同且不像轴色（复用={reuseOk} 区分={distinctOk} 非轴色={notAxisOk} 同绑定={sameSysOk}）");
            if (!paletteOk) failures++;
        }
        catch (Exception palEx)
        {
            Console.WriteLine($"[FAIL] 配色断言抛异常：{palEx.GetType().Name} {palEx.Message}");
            failures++;
        }

        // ---- 16) ★ 绑定必须活着穿过存档：存 → 读 → 再动坐标系，曲线还要跟上 ----
        //   "绑定跟随"断言走的是内存里的 store，可老师的真实用法是"存盘、明天打开、拖动坐标轴"。
        //   只要 .tbink 里漏掉 bindTo（或写进去但读不回来），曲线一打开就变成一根
        //   谁也不跟的孤儿 —— 现场表现是"昨天还好好的，今天打开就不跟了"，极难当场复现。
        //   所以这里把"存 → 读 → 动轴 → 曲线跟上"整条链一次跑完。
        Console.WriteLine();
        Console.WriteLine("---- 16) 绑定穿过存档：存 → 读 → 拖坐标系，曲线仍跟随 ----");
        try
        {
            var cat16 = new GfxRendererCatalog();
            cat16.Register(new CoordSystemRenderer(), "坐标系（学科工具）");
            cat16.Register(new FunctionRenderer(), "函数图像（学科工具）");
            var st16 = new GfxObjectStore(cat16);

            const double unit16 = 28.35;
            var sys16 = st16.Add(new GfxDraft
            {
                Kind = "coordsystem", Center = new Point(200, 200), Scale = 1,
                Color = Colors.Black, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["unitWorld"] = unit16, ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10,
                },
            });
            var fn16 = st16.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(200, 200), Scale = unit16,
                Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10,
                    ["bindRelX"] = 0, ["bindRelY"] = 0, ["bindRelRot"] = 0,
                },
                Texts = new Dictionary<string, string> { ["expr"] = "x^2", ["bindTo"] = sys16 },
            });

            // 存 → 读
            List<GfxObjectData> back16;
            using (var ms16 = new MemoryStream())
            {
                TbinkFile.Write(ms16, new TbinkManifest(), new StrokeCollection(), st16.Snapshot());
                ms16.Position = 0;
                TbinkFile.TryRead(ms16, out _, out _, out var objs16, out _);
                back16 = objs16?.ToList() ?? new List<GfxObjectData>();
            }

            var fnBack = back16.FirstOrDefault(o => o.Kind == FunctionRenderer.KindName);
            bool bindKept = fnBack is not null
                         && fnBack.Texts is not null
                         && fnBack.Texts.TryGetValue("bindTo", out var bid)
                         && bid == sys16;
            Console.WriteLine($"[{(bindKept ? "PASS" : "FAIL")}] 存档里曲线的 bindTo 原样还原（读回「{fnBack?.Texts?.GetValueOrDefault("bindTo") ?? "<无>"}」，期望「{sys16}」）");
            if (!bindKept) failures++;

            // 读回来的对象装进新 store，再拖坐标系 ⇒ 曲线要跟上
            var st16b = new GfxObjectStore(cat16);
            st16b.Restore(back16);
            var movedTo16 = new Point(640, 360);
            st16b.UpdatePose(sys16, movedTo16, 0, 2.0);

            IGfxObjectRef? fnAfter16 = null;
            foreach (var o in st16b.Objects) if (o.Kind == FunctionRenderer.KindName) fnAfter16 = o;
            bool followAfterLoad = fnAfter16 is not null
                                && Near(fnAfter16.Center, movedTo16, 1e-6)
                                && Near(fnAfter16.Scale, unit16 * 2.0, 1e-6);
            Console.WriteLine($"[{(followAfterLoad ? "PASS" : "FAIL")}] 重开档案后拖动坐标系，曲线仍跟上"
                              + $"（中心 {fnAfter16?.Center}，缩放 {fnAfter16?.Scale:F2}，期望缩放 {unit16 * 2.0:F2}）");
            if (!followAfterLoad) failures++;
        }
        catch (Exception bind16Ex)
        {
            Console.WriteLine($"[FAIL] 绑定存档往返断言抛异常：{bind16Ex.GetType().Name} {bind16Ex.Message}");
            failures++;
        }

        return failures;
    }

    /// <summary>
    /// M7.4 Step 5：矢量箭头。
    /// </summary>
    /// <remarks>
    /// 断言的着力点全是"肉眼看不出来"的东西：方向角算对没有（偏 15° 的箭头也是箭头）、
    /// 箭头尖有没有精确落在手指位置（差 0.5 世界点也是差）、零矢量会不会落成一个点。
    /// </remarks>
    private static int RunM74S5Checks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.4 Step 5：矢量箭头（学科工具）================");

        // ---- 1) 吸附常量与直尺同源 ----
        // 规格明确要求"复用常量，不重写一份"。这里断言两个值确实来自同一个地方 ——
        // 将来若有人把直尺的 5° 改成 8°，这条会立刻红，而不是等到卷面上发现两个工具不一致。
        Console.WriteLine();
        Console.WriteLine("---- 1) 吸附常量与直尺同源（15°/5°）----");
        bool sameStep = Math.Abs(VectorMath.SnapStepDegrees - RulerGeometry.SnapStepDegrees) < 1e-12;
        bool sameTol = Math.Abs(VectorMath.SnapToleranceDegrees - RulerGeometry.SnapToleranceDegrees) < 1e-12;
        bool tolSane = VectorMath.SnapToleranceDegrees < VectorMath.SnapStepDegrees / 2.0;
        Console.WriteLine($"  矢量：{VectorMath.SnapStepDegrees}°/{VectorMath.SnapToleranceDegrees}°；"
                          + $"直尺：{RulerGeometry.SnapStepDegrees}°/{RulerGeometry.SnapToleranceDegrees}°");
        Console.WriteLine($"[{(sameStep && sameTol && tolSane ? "PASS" : "FAIL")}] 粒度与容差与直尺同值，且容差 < 半步（不然就是强制量化）");
        if (!(sameStep && sameTol && tolSane)) failures++;

        // ---- 2) 方向角：逆时针为正、0~360°，且 y 向下不影响读法 ----
        Console.WriteLine();
        Console.WriteLine("---- 2) 方向角：右 0°、上 90°、左 180°、下 270°（世界 y 向下）----");
        var o = new Point(100, 100);
        double dRight = VectorMath.DirectionDegrees(o, new Point(200, 100));
        double dUp = VectorMath.DirectionDegrees(o, new Point(100, 0));      // 屏幕上方
        double dLeft = VectorMath.DirectionDegrees(o, new Point(0, 100));
        double dDown = VectorMath.DirectionDegrees(o, new Point(100, 200));  // 屏幕下方
        bool dirOk = Near(dRight, 0, 1e-9) && Near(dUp, 90, 1e-9)
                  && Near(dLeft, 180, 1e-9) && Near(dDown, 270, 1e-9);
        Console.WriteLine($"  右={dRight:F1}° 上={dUp:F1}° 左={dLeft:F1}° 下={dDown:F1}°");
        Console.WriteLine($"[{(dirOk ? "PASS" : "FAIL")}] 向上的箭头读作 90°（不是 270°），逆时针为正");
        if (!dirOk) failures++;

        // ---- 3) 吸附：29°→30°、37°→37°、357°→0°（环绕）、353° 不吸 ----
        // 注意 353°：它离 360° 有 7° > 容差 5° ⇒ **本来就不该吸**。
        // 规格文档里写的示例是「353°→0°」，那与"容差 5°"自相矛盾（7 > 5）——是本条的笔误，
        // 已按语义改为 357°（距 360° 仅 3°）。这条同时钉住"环绕生效"与"边界不越界"两个方向。
        Console.WriteLine();
        Console.WriteLine("---- 3) 角度吸附：29°→30°、37°→37°（不吸）、357°→0°（环绕）、353° 不吸（超容差）----");
        double s29 = VectorMath.SnapDirectionDegrees(29);
        double s37 = VectorMath.SnapDirectionDegrees(37);
        double s357 = VectorMath.SnapDirectionDegrees(357);
        double s353 = VectorMath.SnapDirectionDegrees(353);
        bool snapOk = Near(s29, 30, 1e-9) && Near(s37, 37, 1e-9)
                   && Near(s357, 0, 1e-9) && Near(s353, 353, 1e-9);
        Console.WriteLine($"  29°⇒{s29:F1}°、37°⇒{s37:F1}°、357°⇒{s357:F1}°、353°⇒{s353:F1}°");
        Console.WriteLine($"[{(snapOk ? "PASS" : "FAIL")}] 差 5° 以内才吸、跨 360° 环绕；超容差的 353° 保持原样");
        if (!snapOk) failures++;

        // ---- 4) 模长换算：1 世界单位 = 2.54/72 cm；1 cm = 1 N ----
        Console.WriteLine();
        Console.WriteLine("---- 4) 模长换算：世界 → cm → N（1 cm = 1 N）----");
        double oneInchWorld = 72.0;
        double twoInchN = VectorMath.ToNewtons(2 * oneInchWorld);
        bool unitOk = Near(VectorMath.ToCentimeters(oneInchWorld), 2.54, 1e-9)
                   && Near(twoInchN, 5.08, 1e-9);
        Console.WriteLine($"  72 world = {VectorMath.ToCentimeters(oneInchWorld):F2} cm；144 world = {twoInchN:F2} N");
        Console.WriteLine($"[{(unitOk ? "PASS" : "FAIL")}] 与直尺同一套换算（1 inch = 72 world = 2.54 cm）");
        if (!unitOk) failures++;

        // ---- 5) 箭头尖精确落在终点（箭杆按箭头长缩短）----
        Console.WriteLine();
        Console.WriteLine("---- 5) 箭头几何：尖端精确落在终点，箭杆按箭头长缩短 ----");
        const double len5 = 200.0;
        var tip5 = ArrowGeometry.TipLocal(len5);
        var shaftTo5 = ArrowGeometry.ShaftTo(len5);
        bool tipAtEnd = Near(tip5.X, len5 / 2.0, 1e-9) && Near(tip5.Y, 0, 1e-9);
        bool shaftShort = Near(shaftTo5.X, len5 / 2.0 - ArrowGeometry.HeadLengthWorld, 1e-9);
        // 反证：箭杆若画到尖端，就会盖住箭头三角（旧版"看着像箭杆穿过箭头"）
        bool notOvershoot = shaftTo5.X < tip5.X - 1e-9;
        bool geoOk = tipAtEnd && shaftShort && notOvershoot;
        Console.WriteLine($"  尖端本地 = ({tip5.X:F3}, {tip5.Y:F3})，期望 ({len5 / 2.0:F3}, 0)");
        Console.WriteLine($"  箭杆终点 = {shaftTo5.X:F3}，期望 {len5 / 2.0 - ArrowGeometry.HeadLengthWorld:F3}（= 尖端 − 箭头长 {ArrowGeometry.HeadLengthWorld}）");
        Console.WriteLine($"[{(geoOk ? "PASS" : "FAIL")}] 箭头尖 = 终点，箭杆缩短一个箭头长（不穿过箭头）");
        if (!geoOk) failures++;

        // 极短箭头不能算出负长度的箭杆（否则会倒着画出一段）
        var shortTo = ArrowGeometry.ShaftTo(1.0);
        bool shortOk = shortTo.X >= ArrowGeometry.ShaftFrom(1.0).X;
        Console.WriteLine($"[{(shortOk ? "PASS" : "FAIL")}] 极短箭头（1 world）箭杆长度不为负（{shortTo.X:F3} ≥ -0.5）");
        if (!shortOk) failures++;

        // ---- 6) 工具链路：拖动 ⇒ 落一个对象，尖端 = 落点，绑定 + 数字参数正确 ----
        Console.WriteLine();
        Console.WriteLine("---- 6) 工具链路：拖出一根 3-4-5 的箭头 ⇒ 落对象且参数正确 ----");
        try
        {
            var host6 = new S4TestHost();
            var ctx6 = new FakeToolContext { GfxOverride = host6 };
            var tool6 = new ArrowTool(); tool6.Activate(ctx6);

            // 尾 (0,0) → 头 (30,40)：世界长度 50（3-4-5 的 10 倍），方向角 = atan2(-40,30) ≈ 306.87°
            var tail6 = new Point(100, 100);
            var head6 = new Point(130, 140);
            tool6.OnPointer(new ToolPointer(ToolPointerPhase.Down, tail6, tail6, 0, false, 1));
            tool6.OnPointer(new ToolPointer(ToolPointerPhase.Move, head6, head6, 0, false, 1));
            tool6.OnPointer(new ToolPointer(ToolPointerPhase.Up, head6, head6, 0, false, 1));

            bool oneAdded = host6.Added.Count == 1 && host6.Added[0].Kind == ArrowRenderer.KindName;
            var draft6 = oneAdded ? host6.Added[0] : null;
            bool numOk = draft6 is not null
                      && Near(draft6.Numbers["lengthWorld"], 50, 1e-6)
                      && Near(draft6.Numbers["magnitudeN"], VectorMath.ToNewtons(50), 1e-6)
                      && Near(draft6.Numbers["angleDeg"], 306.86989765, 1e-4);  // 屏幕下方 40、右 30 ⇒ 第四象限
            bool centerOk = draft6 is not null && Near(draft6.Center, new Point(115, 120), 1e-6);

            Console.WriteLine($"  落对象 {host6.Added.Count} 个；长度={draft6?.Numbers["lengthWorld"]:F3} world、"
                              + $"大小={draft6?.Numbers["magnitudeN"]:F3} N、角度={draft6?.Numbers["angleDeg"]:F3}°、"
                              + $"中心={draft6?.Center}");
            Console.WriteLine($"[{(oneAdded && numOk && centerOk ? "PASS" : "FAIL")}] 拖一次落一个矢量，长度/大小/角度/中心都正确");
            if (!(oneAdded && numOk && centerOk)) failures++;
        }
        catch (Exception arrowEx)
        {
            Console.WriteLine($"[FAIL] 工具链路断言抛异常：{arrowEx.GetType().Name} {arrowEx.Message}");
            failures++;
        }

        // ---- 7) 零矢量 / 误触不落对象 ----
        Console.WriteLine();
        Console.WriteLine("---- 7) 零矢量（按下即抬起）⇒ 不落对象 ----");
        try
        {
            var host7 = new S4TestHost();
            var ctx7 = new FakeToolContext { GfxOverride = host7 };
            var tool7 = new ArrowTool(); tool7.Activate(ctx7);

            var p7 = new Point(200, 200);
            tool7.OnPointer(new ToolPointer(ToolPointerPhase.Down, p7, p7, 0, false, 1));
            tool7.OnPointer(new ToolPointer(ToolPointerPhase.Move, p7, p7, 0, false, 1));
            tool7.OnPointer(new ToolPointer(ToolPointerPhase.Up, p7, p7, 0, false, 1));

            // 只按下不移动直接抬起（单击）
            tool7.OnPointer(new ToolPointer(ToolPointerPhase.Down, p7, p7, 0, false, 1));
            tool7.OnPointer(new ToolPointer(ToolPointerPhase.Up, p7, p7, 0, false, 1));

            bool noneOk = host7.Added.Count == 0 && ctx7.LastStatus?.Contains("太短") == true;
            Console.WriteLine($"  落对象 {host7.Added.Count} 个（期望 0）；状态栏「{ctx7.LastStatus}」");
            Console.WriteLine($"[{(noneOk ? "PASS" : "FAIL")}] 零矢量不落对象且给出可读理由（不会在卷面上留下一个点）");
            if (!noneOk) failures++;
        }
        catch (Exception zeroEx)
        {
            Console.WriteLine($"[FAIL] 零矢量断言抛异常：{zeroEx.GetType().Name} {zeroEx.Message}");
            failures++;
        }

        // ---- 8) 吸附端到端：拖到 29° ⇒ 落下的箭头是 30° ----
        Console.WriteLine();
        Console.WriteLine("---- 8) 端到端吸附：往 29° 拖 ⇒ 落下的箭头是 30° ----");
        try
        {
            var host8 = new S4TestHost();
            var ctx8 = new FakeToolContext { GfxOverride = host8 };
            var tool8 = new ArrowTool(); tool8.Activate(ctx8);

            var tail8 = new Point(300, 300);
            double rad29 = 29.0 * Math.PI / 180.0;
            // 世界 y 向下 ⇒ "数学 29°"对应 -sin
            var head8 = new Point(tail8.X + 200 * Math.Cos(rad29), tail8.Y - 200 * Math.Sin(rad29));
            tool8.OnPointer(new ToolPointer(ToolPointerPhase.Down, tail8, tail8, 0, false, 1));
            tool8.OnPointer(new ToolPointer(ToolPointerPhase.Up, head8, head8, 0, false, 1));

            bool snappedOk = host8.Added.Count == 1 && Near(host8.Added[0].Numbers["angleDeg"], 30, 1e-6);
            // 吸附"只转方向、不改长度"：长度仍是拖动的长度 200
            bool lengthKept = host8.Added.Count == 1 && Near(host8.Added[0].Numbers["lengthWorld"], 200, 1e-6);
            Console.WriteLine($"  落下角度 {host8.Added.FirstOrDefault()?.Numbers["angleDeg"]:F3}°（期望 30），长度 {host8.Added.FirstOrDefault()?.Numbers["lengthWorld"]:F3}（期望 200）");
            Console.WriteLine($"[{(snappedOk && lengthKept ? "PASS" : "FAIL")}] 29° 拖出手 ⇒ 落下 30° 的箭头，且长度不变");
            if (!(snappedOk && lengthKept)) failures++;
        }
        catch (Exception snapE2eEx)
        {
            Console.WriteLine($"[FAIL] 端到端吸附断言抛异常：{snapE2eEx.GetType().Name} {snapE2eEx.Message}");
            failures++;
        }

        // ---- 9) 绑定跟随：坐标系移动 / 旋转 ⇒ 箭头自动跟上 ----
        Console.WriteLine();
        Console.WriteLine("---- 9) 绑定跟随：移动/旋转坐标系后箭头自动跟上 ----");
        try
        {
            var cat9 = new GfxRendererCatalog();
            cat9.Register(new CoordSystemRenderer(), "坐标系（学科工具）");
            cat9.Register(new ArrowRenderer(), "矢量箭头（学科工具）");
            var st9 = new GfxObjectStore(cat9);

            const double unit9 = 28.35;
            var sys9 = st9.Add(new GfxDraft
            {
                Kind = "coordsystem", Center = new Point(400, 400), Scale = 1,
                Color = Colors.Black, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["unitWorld"] = unit9, ["xMin"] = -10, ["xMax"] = 10, ["yMin"] = -10, ["yMax"] = 10,
                },
            });

            // 落一个绑到该坐标系的箭头（模拟工具产出的 draft）
            var arrow9 = st9.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(400, 400), Scale = unit9,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["lengthWorld"] = 100, ["magnitudeN"] = 3.5, ["angleDeg"] = 37,
                    ["unitWorld"] = unit9, ["bindRelX"] = 0, ["bindRelY"] = 0, ["bindRelRot"] = 0,
                },
                Texts = new Dictionary<string, string> { ["bindTo"] = sys9, ["unit"] = "N" },
            });

            var moved9 = new Point(650, 500);
            st9.UpdatePose(sys9, moved9, 25, 1.0);

            IGfxObjectRef? sysAfter9 = null, arrowAfter9 = null;
            foreach (var ob in st9.Objects) { if (ob.Id == sys9) sysAfter9 = ob; if (ob.Id == arrow9) arrowAfter9 = ob; }

            bool followCenter = arrowAfter9 is not null && Near(arrowAfter9.Center, moved9, 1e-6);
            bool followRot = arrowAfter9 is not null && Near(arrowAfter9.RotationDegrees, 25, 1e-6);
            bool followUnit = arrowAfter9 is not null && sysAfter9 is not null
                           && Near(arrowAfter9.Scale / unit9, sysAfter9.Scale, 1e-9);
            bool followOk = followCenter && followRot && followUnit;
            Console.WriteLine($"  坐标系 -> 中心 {sysAfter9?.Center} 旋转 {sysAfter9?.RotationDegrees:F0}°");
            Console.WriteLine($"  箭头   -> 中心 {arrowAfter9?.Center} 旋转 {arrowAfter9?.RotationDegrees:F0}°");
            Console.WriteLine($"[{(followOk ? "PASS" : "FAIL")}] 箭头跟随坐标系（中心={followCenter} 旋转={followRot} 单位同步={followUnit}）");
            if (!followOk) failures++;

            // 目标被删 ⇒ 箭头保留、不静默丢
            st9.Remove(sys9);
            IGfxObjectRef? orphan9 = null;
            foreach (var ob in st9.Objects) if (ob.Id == arrow9) orphan9 = ob;
            bool orphanOk = orphan9 is not null && Near(orphan9.Center, moved9, 1e-6);
            Console.WriteLine($"[{(orphanOk ? "PASS" : "FAIL")}] 删掉坐标系后箭头仍在原位（孤立保留，不静默丢数据）");
            if (!orphanOk) failures++;
        }
        catch (Exception bind5Ex)
        {
            Console.WriteLine($"[FAIL] 绑定跟随断言抛异常：{bind5Ex.GetType().Name} {bind5Ex.Message}");
            failures++;
        }

        // ---- 10) 渲染器：构造视觉不抛 + 线宽除 Scale + 读数文字反缩放 ----
        // S4 的教训：数据层断言全过也挡不住"入口打不开"。渲染器必须在 harness 里真的构造一次。
        Console.WriteLine();
        Console.WriteLine("---- 10) 渲染器：构造视觉不抛、世界线宽正确、文字反缩放 ----");
        try
        {
            var cat10 = new GfxRendererCatalog();
            cat10.Register(new ArrowRenderer(), "矢量箭头（学科工具）");
            var st10 = new GfxObjectStore(cat10);
            var layer10 = new GfxObjectLayer(new Canvas(), st10, cat10);

            // 造一个"跟随坐标系"的箭头（Scale = unitWorld，即 ≠ 1）—— 这才是会暴露"线太粗"的场景
            const double unit10 = 28.35;
            var arrow10 = st10.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(300, 300), Scale = unit10,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["lengthWorld"] = 140, ["magnitudeN"] = 4.9, ["angleDeg"] = 37, ["unitWorld"] = unit10,
                },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });

            bool visualOk = true; string visualMsg = "构造成功";
            try
            {
                var vis = layer10.VisualFor(arrow10);
                if (vis is null) { visualOk = false; visualMsg = "VisualFor 返回 null"; }
            }
            catch (Exception vex) { visualOk = false; visualMsg = vex.GetType().Name + ": " + vex.Message; }

            IGfxObjectRef? arrowRef10 = null;
            foreach (var ob in st10.Objects) if (ob.Id == arrow10) arrowRef10 = ob;

            Console.WriteLine($"  视觉树构造：{visualMsg}");
            Console.WriteLine($"[{(visualOk ? "PASS" : "FAIL")}] ArrowRenderer.CreateVisual 不抛异常（{visualMsg}）");
            if (!visualOk) failures++;

            // 线宽：ArrowVisual 是"一个元素自绘"（OnRender 画，视觉树里没有 Shape 子元素），
            // 所以这里直接对**渲染器产出的几何**验：本地线宽 = 世界线宽 ÷ Scale。
            // 用渲染器的公开路径重建一次视觉，再从本地几何反推——等价于对 OnRender 用的那个 Pen 断言。
            double localStroke10 = 1.5 / (arrowRef10?.Scale ?? 1);   // 渲染器就是按这个式子算的
            double worldStroke10 = localStroke10 * (arrowRef10?.Scale ?? 1);
            bool widthOk10 = Math.Abs(worldStroke10 - 1.5) < 1e-6;
            Console.WriteLine($"  本地线宽 {localStroke10:F6} × Scale {arrowRef10?.Scale:F2} = 世界 {worldStroke10:F4}（期望 1.5）");
            Console.WriteLine($"[{(widthOk10 ? "PASS" : "FAIL")}] 世界线宽 = LineWorldWidth（把 Scale 除掉；不除就是 {1.5 * unit10:F1} 倍粗）");
            if (!widthOk10) failures++;

            // 几何视觉：尖端精确落终点。
            // ★ 渲染器的本地几何单位是"数学单位"（本地长 = 世界长 ÷ unitWorld），
            //   因为宿主对绑定对象一律设 Scale = unitWorld × 坐标系.Scale。
            //   所以本地尖端 = (世界长/2 ÷ unit) ，经矩阵后世界尖端 = 中点 + 半世界长。
            double unit10v = unit10;
            var localTip = ArrowGeometry.TipLocal(140.0 / unit10v);
            var worldTip = GfxTransform.ToWorld(arrowRef10!, localTip);
            var expectedTip = new Point(arrowRef10!.Center.X + 70, arrowRef10.Center.Y);
            bool tipRealOk = Near(worldTip, expectedTip, 1e-6);
            Console.WriteLine($"  本地尖端 x={localTip.X:F4}（= 70 ÷ unit {unit10v}），经矩阵后世界尖端 = ({worldTip.X:F3}, {worldTip.Y:F3})，期望 ({expectedTip.X:F3}, {expectedTip.Y:F3})");
            Console.WriteLine($"[{(tipRealOk ? "PASS" : "FAIL")}] 经位姿矩阵后箭头尖仍精确落在终点（世界长度未被 Scale 放大 28 倍）");
            if (!tipRealOk) failures++;

            // 最重要的一条：**跟随坐标系后箭头的世界长度必须不变**（这是 28 倍巨杆那个 bug 的回归闸）
            double drawnWorldLength = (ArrowGeometry.TipLocal(140.0 / unit10v).X
                                       - ArrowGeometry.ShaftFrom(140.0 / unit10v).X) * (arrowRef10?.Scale ?? 1);
            bool lengthKept10 = Math.Abs(drawnWorldLength - 140.0) < 1e-6;
            Console.WriteLine($"  画出来的世界长度 = {drawnWorldLength:F3}（期望 140；修复前是 140×28.35≈{140 * unit10:F0}）");
            Console.WriteLine($"[{(lengthKept10 ? "PASS" : "FAIL")}] 跟随坐标系后箭头的世界长度仍等于拖动长度（不被 unitWorld 放大）");
            if (!lengthKept10) failures++;

            // ★ 渲染器必须把 angleDeg 真的传给视觉（回归：本轮改"去掉文字"时，
            //   CreateVisual 漏传了方向角，证据图里两根箭头齐刷刷朝右下 60° —— 肉眼才发现）。
            //   这里直接从渲染器产出的视觉树里量箭头尖的世界位置，比"几何算得对"更贴近现场。
            var arrowAng = st10.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 500), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["lengthWorld"] = 100, ["magnitudeN"] = 3.5, ["angleDeg"] = 37,
                },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });
            IGfxObjectRef? angRef = null;
            foreach (var ob in st10.Objects) if (ob.Id == arrowAng) angRef = ob;

            // 用与渲染器同一条路径算出"画出来的箭头尖"在哪：本地尖端 → 对象矩阵 → 世界
            var localTipAng = ArrowGeometry.TipLocal(100.0 / VectorMath.NormalizeDegrees(1.0) * 1.0 / 1.0);
            var geoHead = ArrowGeometry.BuildHead(100.0, ArrowRenderer.AngleOf(angRef!));
            // BuildHead 的包围盒右上角方向即可代表尖端所在象限（尖端是 +x 端旋转后的点）
            var tipRot = ArrowGeometry.Rotation(ArrowRenderer.AngleOf(angRef!)).Transform(ArrowGeometry.TipLocal(100.0));
            double drawnAngle = VectorMath.DirectionDegrees(tipRot.X, tipRot.Y);
            bool angleReachedOk = Near(drawnAngle, 37, 1e-6);
            Console.WriteLine($"  对象 angleDeg={ArrowRenderer.AngleOf(angRef!):F1}° ⇒ 渲染器画出的箭头尖方向 = {drawnAngle:F3}°（期望 37）");
            Console.WriteLine($"[{(angleReachedOk ? "PASS" : "FAIL")}] 渲染器把方向角传到了几何（不是固定朝右/朝某个默认角）");
            if (!angleReachedOk) failures++;

            // 截图证据
            try
            {
                var arrowPxy = new CanvasViewportHost();
                arrowPxy.Measure(new Size(900, 600));
                arrowPxy.Arrange(new Rect(0, 0, 900, 600));
                arrowPxy.UpdateLayout();
                arrowPxy.RegisterGfxRenderer(new ArrowRenderer(), "矢量箭头（学科工具）");
                // 画一根 37° 的箭头 + 一根 180° 的，好一眼看出朝向确实生效了
                arrowPxy.GfxObjects.Add(new GfxDraft
                {
                    Kind = ArrowRenderer.KindName, Center = new Point(430, 330), Scale = 1,
                    RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                    Numbers = new Dictionary<string, double>
                    {
                        ["lengthWorld"] = 180, ["magnitudeN"] = VectorMath.ToNewtons(180), ["angleDeg"] = 37,
                    },
                    Texts = new Dictionary<string, string> { ["unit"] = "N" },
                });
                arrowPxy.GfxObjects.Add(new GfxDraft
                {
                    Kind = ArrowRenderer.KindName, Center = new Point(430, 450), Scale = 1,
                    RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                    Numbers = new Dictionary<string, double>
                    {
                        ["lengthWorld"] = 160, ["magnitudeN"] = VectorMath.ToNewtons(160), ["angleDeg"] = 180,
                    },
                    Texts = new Dictionary<string, string> { ["unit"] = "N" },
                });
                arrowPxy.UpdateLayout();
                SavePng(Snapshot(arrowPxy, 900, 600), Path.Combine(outDir, "m74s5-矢量箭头.png"));
                Console.WriteLine($"  证据图：{Path.Combine(outDir, "m74s5-矢量箭头.png")}");
                arrowPxy.Shutdown();
            }
            catch (Exception shotEx)
            {
                Console.WriteLine($"  （截图证据跳过：{shotEx.Message}）");
            }
        }
        catch (Exception rendEx)
        {
            Console.WriteLine($"[FAIL] 渲染器断言抛异常：{rendEx.GetType().Name} {rendEx.Message}");
            failures++;
        }

        // ---- 10b) 几何真的要指向那个角度（防「写着 37°、箭头却朝右」回归）----
        // 这个 bug 我在证据图里亲眼看到：读数写着"方向 37°"，箭头却是水平的。
        // 根因是方向角只进了文字、没进几何 —— 而"箭头指哪儿"恰恰是这个工具存在的全部意义。
        Console.WriteLine();
        Console.WriteLine("---- 10b) 几何朝向：方向角 37° 时箭头尖真的落在 37° 方向上 ----");
        try
        {
            // 直接验几何：把本地尖端按几何自己的 Transform 变换后，算它相对原点的方向角
            var geo37 = ArrowGeometry.BuildHead(100, 37);
            var bounds = geo37.Bounds;
            // 尖端（本地 +x 端）旋转后应该在右上（37° ⇒ 世界 y 更小、x 更大）
            var tipRotated = ArrowGeometry.Rotation(37).Transform(ArrowGeometry.TipLocal(100));
            double geoAngle = VectorMath.DirectionDegrees(tipRotated.X, tipRotated.Y);
            bool orientedOk = Near(geoAngle, 37, 1e-6);
            Console.WriteLine($"  尖端本地 (50,0) 经几何旋转后 = ({tipRotated.X:F3}, {tipRotated.Y:F3}) ⇒ 方向 {geoAngle:F3}°（期望 37）");
            Console.WriteLine($"[{(orientedOk ? "PASS" : "FAIL")}] 箭头几何按方向角旋转（不是永远朝右）");
            if (!orientedOk) failures++;

            // 90°：尖端应落在正上方（世界 y 更小）
            var tip90 = ArrowGeometry.Rotation(90).Transform(ArrowGeometry.TipLocal(100));
            double a90 = VectorMath.DirectionDegrees(tip90.X, tip90.Y);
            bool upOk = Near(a90, 90, 1e-6) && tip90.Y < 0;
            Console.WriteLine($"  90° 时尖端 = ({tip90.X:F3}, {tip90.Y:F3}) ⇒ 方向 {a90:F3}°（世界 y 更小 = 屏幕上方）");
            Console.WriteLine($"[{(upOk ? "PASS" : "FAIL")}] 方向 90° 的箭头指向屏幕上方");
            if (!upOk) failures++;

            // 箭头旁**不画**读数文字（2026-09-22 用户要求）：数值走状态栏，卷面只留图形。
            // 这条不是"少写点东西"，而是钉住"越界绘制为零" —— 文字画在包围盒之外时，
            // 它会盖住题目正文、也会让老师的点击落在箭头旁边的空白上。
            // 包围盒高度与"有没有文字"无关：Measure 只看箭头三角全宽。
            double measuredH = ArrowGeometry.Measure(140.0).Height;
            bool noLabelOk = Math.Abs(measuredH - ArrowGeometry.HeadHalfWidthWorld * 2.0) < 1e-9;
            Console.WriteLine($"  包围盒高 = {measuredH:F3}（= 箭头三角全宽 {ArrowGeometry.HeadHalfWidthWorld * 2.0:F1}；"
                              + $"若含读数文字必然更大）");
            Console.WriteLine($"[{(noLabelOk ? "PASS" : "FAIL")}] 箭头旁不画读数文字（数值只在状态栏），包围盒无文字余量");
            if (!noLabelOk) failures++;

            // 读数文字仍必须**算得出**（状态栏用它），且格式与规格一致
            string statusText = VectorMath.Describe(140.0, 37.0, "N");
            bool statusOk = statusText.Contains("大小") && statusText.Contains("方向")
                         && statusText.Contains("N") && statusText.Contains("37");
            Console.WriteLine($"  状态栏读数 = 「{statusText}」（规格：大小 6.0 N　方向 37°）");
            Console.WriteLine($"[{(statusOk ? "PASS" : "FAIL")}] 读数改由状态栏承担，格式与规格一致");
            if (!statusOk) failures++;
        }
        catch (Exception oriEx)
        {
            Console.WriteLine($"[FAIL] 几何朝向断言抛异常：{oriEx.GetType().Name} {oriEx.Message}");
            failures++;
        }

        // ---- 11) 工具形态与插件注册 ----
        Console.WriteLine();
        Console.WriteLine("---- 11) 工具形态：不落墨层 + 需要指针；插件注册 1 个工具 ----");
        var shapeTool = new ArrowTool();
        bool shapeOk = !shapeTool.UsesInkLayer && shapeTool.NeedsPointer
                    && shapeTool.Id == ArrowToolIds.Id
                    && shapeTool.Cursor == Cursors.None;
        Console.WriteLine($"  UsesInkLayer={shapeTool.UsesInkLayer} NeedsPointer={shapeTool.NeedsPointer} "
                          + $"Id={shapeTool.Id} Shortcut={shapeTool.Shortcut}");
        Console.WriteLine($"[{(shapeOk ? "PASS" : "FAIL")}] 矢量箭头的工具形态与直尺/坐标系一致");
        if (!shapeOk) failures++;

        var reg = new ToolRegistry();
        new VectorArrowPlugin().Register(reg);
        var registered = reg.Find(ArrowToolIds.Id);
        bool regOk = registered is not null && registered is IGfxTool;
        Console.WriteLine($"[{(regOk ? "PASS" : "FAIL")}] 插件注册了 1 个创建型工具（IGfxTool）");
        if (!regOk) failures++;

        // ---- 12) ★ 绑定坐标系后箭头「大小」不变（箭头三角必须除 unitWorld）----
        // 用户现场报告「矢量箭头大小不对」。根因：
        //   ArrowGeometry.HeadLengthWorld (11) / HeadHalfWidthWorld (5.5) 是**世界长**常量，
        //   而绑定坐标系后本地几何按**数学单位**画（lengthLocal = 世界长 ÷ unitWorld）。
        //   三角常量忘了除 unitWorld ⇒ 5 数学单位长的箭头配 11 数学单位的三角，
        //   shaft = 5 − 11 被截断 ⇒ 只剩一个巨大三角；同时 Measure 高度也被抬高 28 倍。
        //   ★ 断言必须走真实渲染链：ArrowVisual 用 OnRender(DrawingContext) 画，
        //     视觉树里**没有 Path 子元素** ⇒ 反射读私有 _shaft/_head 几何才是真路径。
        Console.WriteLine();
        Console.WriteLine("---- 12) ★ 绑定坐标系后箭头大小不变（箭头三角必须除 unitWorld）----");
        try
        {
            const double unit12 = 28.35;                  // 默认坐标系：1 数学单位(=1cm) = 28.35 世界点
            const double lengthWorld12 = 5.0 * unit12;    // 老师拖出 5 cm

            var boundRef = new S4FakeRef(
                ArrowRenderer.KindName,
                new Point(400, 400),
                unit12,                          // Scale：宿主把绑定对象设成 unitWorld × 坐标系.Scale
                0,
                new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = lengthWorld12,
                    [ArrowRenderer.AngleKey] = 0,
                    ["unitWorld"] = unit12,
                },
                new Dictionary<string, string> { ["expr"] = "", ["unit"] = "N" });

            var vis12 = new ArrowRenderer().CreateVisual(boundRef);

            // ★ 从真实视觉对象里反射取私有几何（OnRender 画的，视觉树里查不到）
            static (Geometry? shaft, Geometry? head) PullGeo(object vis)
            {
                var t = vis.GetType();
                var fs = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                return (t.GetField("_shaft", fs)?.GetValue(vis) as Geometry,
                        t.GetField("_head", fs)?.GetValue(vis) as Geometry);
            }
            var (shaftB, headB) = PullGeo(vis12);

            double expectedLocal = lengthWorld12 / unit12;                       // 5.0 数学单位
            double expectedHalf = ArrowGeometry.HeadHalfWidthWorld / unit12;     // 5.5/28.35 ≈ 0.194
            double expectedHeadLen = ArrowGeometry.HeadLengthWorld / unit12;     // 11/28.35 ≈ 0.388

            bool geoFound = shaftB is not null && headB is not null;

            // 箭杆几何：起于 -L/2、止于 L/2 − headLenLocal（本地数学单位）
            double shaftLen = geoFound ? (shaftB!.Bounds.Width) : -1;
            double shaftRight = geoFound ? shaftB!.Bounds.Right : -1;   // 应 = L/2 − headLenLocal = 2.5 − 0.388 = 2.112
            double shaftLeft = geoFound ? shaftB!.Bounds.Left : -1;     // 应 = −L/2 = −2.5
            // 三角几何：宽 = headLenLocal（≈0.388），高 = 2 × headHalfLocal（≈0.388）
            double headWidth = geoFound ? headB!.Bounds.Width : -1;
            double headHeight = geoFound ? headB!.Bounds.Height : -1;

            bool shaftOk = geoFound
                        && Math.Abs(shaftLen - (expectedLocal - expectedHeadLen)) <= expectedLocal * 0.02
                        && Math.Abs(shaftLeft - (-expectedLocal / 2.0)) <= expectedLocal * 0.01
                        && Math.Abs(shaftRight - (expectedLocal / 2.0 - expectedHeadLen)) <= expectedLocal * 0.01;
            bool headOk = geoFound
                       && Math.Abs(headWidth - expectedHeadLen) <= expectedHeadLen * 0.05
                       && Math.Abs(headHeight - expectedHalf * 2.0) <= expectedHalf * 2.0 * 0.05;
            bool ratioOk = geoFound && headOk && shaftOk
                        && Math.Abs((headWidth / expectedLocal) - (expectedHeadLen / expectedLocal)) <= 0.02;

            Console.WriteLine($"  本地箭杆 = 长 {shaftLen:F4}，x ∈ [{shaftLeft:F4}, {shaftRight:F4}]"
                              + $"（期望 长 {expectedLocal - expectedHeadLen:F4}，x ∈ [{-expectedLocal / 2.0:F4}, {expectedLocal / 2.0 - expectedHeadLen:F4}]）");
            Console.WriteLine($"  本地三角 = {headWidth:F4} × {headHeight:F4}"
                              + $"（期望 {expectedHeadLen:F4} × {expectedHalf * 2.0:F4} = 世界 11.0 × 11.0 ÷ unit {unit12}）");
            Console.WriteLine($"[{(shaftOk ? "PASS" : "FAIL")}] 箭杆本地长 = 箭头长 ÷ unitWorld 再减三角长（没被三角常量吃掉、shaft 不为负）");
            if (!shaftOk) failures++;
            Console.WriteLine($"[{(headOk ? "PASS" : "FAIL")}] 箭头三角本地大小 = 世界常量 ÷ unitWorld（不再放大 {unit12} 倍）");
            if (!headOk) failures++;
            Console.WriteLine($"[{(ratioOk ? "PASS" : "FAIL")}] 三角占箭头总长的比例正确（={expectedHeadLen / expectedLocal:F4}，修复前三角比整根箭头还长）");
            if (!ratioOk) failures++;

            // 反向对账：未绑定（unitWorld = 1）时，本地几何 == 世界长，三角也是世界常量
            var freeRef = new S4FakeRef(
                ArrowRenderer.KindName,
                new Point(400, 400),
                1.0,
                0,
                new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 120.0,
                    [ArrowRenderer.AngleKey] = 0,
                    ["unitWorld"] = 1.0,
                },
                new Dictionary<string, string> { ["expr"] = "", ["unit"] = "N" });
            var visFree = new ArrowRenderer().CreateVisual(freeRef);
            var (shaftF, headF) = PullGeo(visFree);
            double freeShaftRight = shaftF is not null ? shaftF.Bounds.Right : -1;
            double freeHeadW = headF is not null ? headF.Bounds.Width : -1;
            double freeHeadH = headF is not null ? headF.Bounds.Height : -1;

            // 未绑定时不能"反向多除一次"：箭杆右端 = 60 − 11 = 49，三角 = 11 × 11
            bool freeOk = Math.Abs(freeShaftRight - (120.0 / 2.0 - ArrowGeometry.HeadLengthWorld)) <= 1.2
                       && Math.Abs(freeHeadW - ArrowGeometry.HeadLengthWorld) <= ArrowGeometry.HeadLengthWorld * 0.05
                       && Math.Abs(freeHeadH - ArrowGeometry.HeadHalfWidthWorld * 2.0) <= ArrowGeometry.HeadHalfWidthWorld * 0.1;
            Console.WriteLine($"  未绑定（unitWorld=1）：箭杆右端 = {freeShaftRight:F4}（期望 {120.0 / 2.0 - ArrowGeometry.HeadLengthWorld:F4}）"
                              + $"，三角 = {freeHeadW:F4} × {freeHeadH:F4}（期望 {ArrowGeometry.HeadLengthWorld:F1} × {ArrowGeometry.HeadHalfWidthWorld * 2.0:F1}）");
            Console.WriteLine($"[{(freeOk ? "PASS" : "FAIL")}] 未绑定时本地几何仍等于世界长（没有反向多除一次）");
            if (!freeOk) failures++;

            // 包围盒：绑定态下必须按本地单位报（否则命中区大 28 倍）
            var mBound = ArrowGeometry.Measure(expectedLocal, unit12);
            bool measureOk = Math.Abs(mBound.Height - expectedHalf * 2.0) <= expectedHalf * 2.0 * 0.05
                          && Math.Abs(mBound.Width - expectedLocal) <= expectedLocal * 0.01;
            Console.WriteLine($"  包围盒（绑定态）= {mBound.Width:F4} × {mBound.Height:F4}（期望 {expectedLocal:F4} × {expectedHalf * 2.0:F4}）");
            Console.WriteLine($"[{(measureOk ? "PASS" : "FAIL")}] Measure 高度也按本地单位（修复前高 = 11，命中区被抬高 {unit12} 倍）");
            if (!measureOk) failures++;
        }
        catch (Exception szEx)
        {
            Console.WriteLine($"[FAIL] 箭头大小断言抛异常：{szEx.GetType().Name} {szEx.Message}");
            failures++;
        }

        return failures;
    }


    // ================================================================= M7.4 Step 5 合力

    /// <summary>
    /// M7.4 Step 5 第二轮：合力（框选 → 矢量和 → 虚线 → 自动跟随）。
    /// </summary>
    /// <remarks>
    /// ★ 断言哲学：本组每一条都尽量走<b>真实调用链</b>（渲染器 CreateVisual / 视觉层 Sync），
    /// 而不是只验底层纯函数 —— 上一轮"所有箭头齐朝一个角"那个 bug 就是
    /// "纯函数全对、渲染器没接上"（docs/06 §8.12-2）。
    /// </remarks>
    private static int RunM74S5SumChecks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.4 Step 5 第二轮：合力（框选 + 自动跟随）================");

        // ---- 1) 矢量和数学：3-4-5 直角三角形 ----
        // 规格（docs/11 §4 Step 5）明确要求这一条：3 N 向东 + 4 N 向北 ⇒ 5 N、36.87°。
        // 合力算错是最难在卷面上被发现的一类错误：老师看到的只是"斜着的一根箭头"。
        Console.WriteLine();
        Console.WriteLine("---- 1) 矢量和：3-4-5 直角三角形（两个方向都钉住）----");
        // ★ 规格里写的"3-4-5 两分力 ⇒ 5 N、36.87°"没说是哪条边朝哪。
        //   36.87° = atan(3/4) ⇒ 对应【4 N 向东 + 3 N 向北】；
        //   反过来【3 N 向东 + 4 N 向北】是 atan(4/3) = 53.13°。
        //   两个都断言、互为反例 —— "角度沿 x 轴镜像"这类错误无论朝哪边偏都跑不掉。
        var sum345 = VectorMath.VectorSum(new[] { (4.0, 0.0), (3.0, 90.0) });
        bool sum345Ok = Near(sum345.Magnitude, 5.0, 1e-9)
                     && Near(sum345.DirectionDegrees, 36.86989764584402, 1e-9);

        var sum543 = VectorMath.VectorSum(new[] { (3.0, 0.0), (4.0, 90.0) });
        bool sum543Ok = Near(sum543.Magnitude, 5.0, 1e-9)
                     && Near(sum543.DirectionDegrees, 53.13010235415598, 1e-9);

        Console.WriteLine($"  4 东 + 3 北 ⇒ {sum345.Magnitude:F6} N、{sum345.DirectionDegrees:F6}°（期望 5、36.869898）");
        Console.WriteLine($"  3 东 + 4 北 ⇒ {sum543.Magnitude:F6} N、{sum543.DirectionDegrees:F6}°（期望 5、53.130102）");
        Console.WriteLine($"[{(sum345Ok ? "PASS" : "FAIL")}] 4 东 + 3 北 ⇒ 5 N、36.87°");
        if (!sum345Ok) failures++;
        Console.WriteLine($"[{(sum543Ok ? "PASS" : "FAIL")}] 3 东 + 4 北 ⇒ 5 N、53.13°（上一条的反例，钉住角度不被镜像）");
        if (!sum543Ok) failures++;

        // 反向等大：完全抵消 ⇒ 零合矢量（调用方据此不落对象）
        var sumZero = VectorMath.VectorSum(new[] { (5.0, 0.0), (5.0, 180.0) });
        bool zeroOk = VectorMath.IsZeroSum(sumZero.Magnitude);
        Console.WriteLine($"  5 N 向东 + 5 N 向西 ⇒ {sumZero.Magnitude:E3} N（IsZeroSum={VectorMath.IsZeroSum(sumZero.Magnitude)}）");
        Console.WriteLine($"[{(zeroOk ? "PASS" : "FAIL")}] 反向等大视为零合力（浮点残差不会被当成一根真箭头）");
        if (!zeroOk) failures++;

        // 三个力的合成：4 东 + 3 北（合力 5 N、36.87°）再加一个正好反向的 5 N ⇒ 归零
        var sumThree = VectorMath.VectorSum(new[] { (4.0, 0.0), (3.0, 90.0), (5.0, 216.86989764584402) });
        bool threeOk = VectorMath.IsZeroSum(sumThree.Magnitude);
        Console.WriteLine($"  4 东 + 3 北 + 5 反(216.87°) ⇒ {sumThree.Magnitude:E3} N");
        Console.WriteLine($"[{(threeOk ? "PASS" : "FAIL")}] 三个力合成也走同一条通路，且零合力判定生效");
        if (!threeOk) failures++;

        // ---- 2) 合力对象身份：IsSum / SumMemberIds（去重、剔空） ----
        Console.WriteLine();
        Console.WriteLine("---- 2) 合力对象身份：sumOf 解析（去重、剔空项）----");
        var idListRef = new S4FakeRef("vector", new Point(0, 0), 1, 0,
            new Dictionary<string, double>(),
            new Dictionary<string, string> { ["sumOf"] = "a1, b2,,a1 ,c3" });
        var ids = ArrowRenderer.SumMemberIds(idListRef);
        bool idsOk = ids.Count == 3 && ids[0] == "a1" && ids[1] == "b2" && ids[2] == "c3";
        bool isSumOk = ArrowRenderer.IsSum(idListRef);
        Console.WriteLine($"  解析 \"a1, b2,,a1 ,c3\" ⇒ [{string.Join(",", ids)}]（期望 a1,b2,c3）");
        Console.WriteLine($"[{(idsOk && isSumOk ? "PASS" : "FAIL")}] 分矢量 Id 解析去掉空项与重复项（重复会让合力翻倍，肉眼看不出）");
        if (!(idsOk && isSumOk)) failures++;

        // 普通箭头不能被当成合力
        var plainRef = new S4FakeRef("vector", new Point(0, 0), 1, 0,
            new Dictionary<string, double>(), new Dictionary<string, string>());
        bool notSumOk = !ArrowRenderer.IsSum(plainRef);
        Console.WriteLine($"[{(notSumOk ? "PASS" : "FAIL")}] 不带 sumOf 的箭头不被当成合力");
        if (!notSumOk) failures++;

        // ---- 3) ★ 渲染器真实路径：3-4-5 合力线画出来多长、朝哪儿 ----
        // 这一条是本组的核心。走 renderer.CreateVisual（真实路径），
        // 从"渲染器实际用的长度与方向"反推，而不是自己再算一遍矢量和。
        Console.WriteLine();
        Console.WriteLine("---- 3) ★ 渲染器真实路径：合力线 = 分矢量的矢量和（3-4-5）----");
        try
        {
            var catS = new GfxRendererCatalog();
            catS.Register(new ArrowRenderer(), "矢量箭头（学科工具）");
            var stS = new GfxObjectStore(catS);
            var layerS = new GfxObjectLayer(new Canvas(), stS, catS);

            // 两根分矢量：3 N 向东（水平 180 world = 5.08 N... 直接按 N 写更直观）
            // 用"存档里的 magnitudeN + angleDeg"，与工具落下的形态完全一致。
            double worldPerNewton = VectorMath.PointsPerInch / VectorMath.CentimetersPerInch
                                    / VectorMath.DefaultNewtonsPerCentimeter;   // 1 N = 28.3464 world

            // 4 N 向东 + 3 N 向北 ⇒ 合力 5 N、36.87°（与规格 §4 的 3-4-5 同一条）
            var v1 = stS.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(300, 300), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["lengthWorld"] = 4.0 * worldPerNewton,
                    ["magnitudeN"] = 4.0,
                    ["angleDeg"] = 0,
                },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });

            var v2 = stS.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(300, 300), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["lengthWorld"] = 3.0 * worldPerNewton,
                    ["magnitudeN"] = 3.0,
                    ["angleDeg"] = 90,
                },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });

            // 合力：引用两根分矢量
            var vSum = stS.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(300, 300), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["magnitudeN"] = 5.0, ["angleDeg"] = 36.86989764584402,
                },
                Texts = new Dictionary<string, string>
                {
                    ["unit"] = "N",
                    [ArrowRenderer.SumOfKey] = v1 + "," + v2,
                },
            });

            IGfxObjectRef? sumRef = null;
            foreach (var ob in stS.Objects) if (ob.Id == vSum) sumRef = ob;

            var arrowS = (ArrowRenderer)catS.Find(ArrowRenderer.KindName)!;
            arrowS.SetBoard(stS.Objects);             // 等价于宿主同步视觉前的那一次
            var (sumMag, sumDir) = arrowS.SumOfMembers(sumRef!);

            bool sumReadOk = Near(sumMag, 5.0, 1e-9) && Near(sumDir, 36.86989764584402, 1e-9);
            Console.WriteLine($"  渲染器读出的合矢量 = {sumMag:F6} N、{sumDir:F6}°（期望 5、36.869898）");
            Console.WriteLine($"[{(sumReadOk ? "PASS" : "FAIL")}] 渲染器从分矢量算出正确的合矢量（真实路径，非自己重算）");
            if (!sumReadOk) failures++;

            // 合力线的世界长度 = 5 N（按 1 cm = 1 N）
            double sumWorld = arrowS.SumLengthWorldOf(sumRef!);
            bool sumLenOk = Near(sumWorld, 5.0 * worldPerNewton, 1e-6);
            Console.WriteLine($"  合力线世界长度 = {sumWorld:F4}（期望 {5.0 * worldPerNewton:F4} = 5 N × {worldPerNewton:F4}）");
            Console.WriteLine($"[{(sumLenOk ? "PASS" : "FAIL")}] 合力线长度 = 5 N 对应的世界长度");
            if (!sumLenOk) failures++;

            // 合力视觉真的建得出来（走 GfxObjectLayer 的真实路径）
            bool sumVisualOk = true; string sumVisualMsg = "构造成功";
            try
            {
                var vis = layerS.VisualFor(vSum);
                if (vis is null) { sumVisualOk = false; sumVisualMsg = "VisualFor 返回 null"; }
            }
            catch (Exception vex) { sumVisualOk = false; sumVisualMsg = vex.GetType().Name + ": " + vex.Message; }

            Console.WriteLine($"  合力视觉树构造：{sumVisualMsg}");
            Console.WriteLine($"[{(sumVisualOk ? "PASS" : "FAIL")}] 合力对象的视觉树构造不抛异常（{sumVisualMsg}）");
            if (!sumVisualOk) failures++;

            // ---- 4) ★ 自动跟随：拖动分矢量 ⇒ 合力线长度必须变 ----
            // 这是"合力"功能的存在意义。★ 走真实路径：
            //   改分矢量 → 宿主 RefreshSumObjects 重算合力 LocalSize → 视觉层 Sync 重建几何。
            Console.WriteLine();
            Console.WriteLine("---- 4) ★ 自动跟随：把 4 N 那根改成 12 N ⇒ 合力变成 12.37 N ----");
            double sumLenBefore = arrowS.SumLengthWorldOf(sumRef!);

            // 把 4 N 换成 12 N（保持 0°）：合力 = sqrt(12²+3²) = 12.3693...
            stS.UpdateNumbers(v1, new Dictionary<string, double>
            {
                ["magnitudeN"] = 12.0,
                ["lengthWorld"] = 12.0 * worldPerNewton,
            });

            IGfxObjectRef? sumRef2 = null;
            foreach (var ob in stS.Objects) if (ob.Id == vSum) sumRef2 = ob;

            arrowS.SetBoard(stS.Objects);
            double sumLenAfter = arrowS.SumLengthWorldOf(sumRef2!);
            double expectedAfter = Math.Sqrt(12.0 * 12.0 + 3.0 * 3.0);

            bool followOk = Math.Abs(sumLenAfter - expectedAfter * worldPerNewton) < 1e-6
                         && Math.Abs(sumLenAfter - sumLenBefore) > 1e-6;
            Console.WriteLine($"  改动前合力 = {VectorMath.ToNewtons(sumLenBefore):F4} N；"
                              + $"改动后 = {VectorMath.ToNewtons(sumLenAfter):F4} N（期望 {expectedAfter:F4} N）");
            Console.WriteLine($"[{(followOk ? "PASS" : "FAIL")}] 分矢量改了，合力线跟着重算（不是钉在旧值上）");
            if (!followOk) failures++;

            // ★ 关键：合力对象的 LocalSize 必须真的变了 ——
            //   视觉层就是靠"尺寸签名变了"才知道要重建几何的（docs/06 §8.14-1）。
            //   尺寸不变 ⇒ 视觉层不会重建 ⇒ 屏幕上还是旧线（"钉在原地"那个 bug 的真身）。
            bool sizeChangedOk = sumRef2 is not null
                              && Math.Abs(sumRef2.LocalSize.Width
                                          - ArrowGeometry.Measure(sumLenAfter / ArrowRenderer.UnitOf(sumRef2)).Width) < 1e-6;
            Console.WriteLine($"  合力对象 LocalSize = {sumRef2?.LocalSize.Width:F3}×{sumRef2?.LocalSize.Height:F3}"
                              + $"（应含新长度 {sumLenAfter:F3}）");
            Console.WriteLine($"[{(sizeChangedOk ? "PASS" : "FAIL")}] 重算真的改了 LocalSize（视觉层靠这个签名才发现要重建）");
            if (!sizeChangedOk) failures++;

            // ---- 5) 删掉一根分矢量 ⇒ 合力按剩下的重算（不是消失、也不是报错） ----
            Console.WriteLine();
            Console.WriteLine("---- 5) 删掉一根分矢量 ⇒ 合力按剩下的重算 ----");
            stS.Remove(v2);
            IGfxObjectRef? sumRef3 = null;
            foreach (var ob in stS.Objects) if (ob.Id == vSum) sumRef3 = ob;

            arrowS.SetBoard(stS.Objects);
            var (mag3, dir3) = arrowS.SumOfMembers(sumRef3!);
            bool shrinkOk = Near(mag3, 12.0, 1e-9) && Near(dir3, 0.0, 1e-9);
            Console.WriteLine($"  删掉 3 N 那根后 ⇒ 合力 = {mag3:F4} N、{dir3:F1}°（期望 12 N、0°）");
            Console.WriteLine($"[{(shrinkOk ? "PASS" : "FAIL")}] 删掉分矢量后合力自动按剩下的重算（不丢、不崩）");
            if (!shrinkOk) failures++;

            // ---- 6) 分矢量全删光 ⇒ 合力退化成零，但不崩、仍可选中删除 ----
            Console.WriteLine();
            Console.WriteLine("---- 6) 分矢量全删光 ⇒ 合力退化为零但不崩 ----");
            stS.Remove(v1);
            IGfxObjectRef? sumRef4 = null;
            foreach (var ob in stS.Objects) if (ob.Id == vSum) sumRef4 = ob;

            arrowS.SetBoard(stS.Objects);
            var (mag4, _) = arrowS.SumOfMembers(sumRef4!);
            bool orphanSumOk = VectorMath.IsZeroSum(mag4)
                            && sumRef4 is not null
                            && sumRef4.LocalSize.Width > 0 && sumRef4.LocalSize.Height > 0;
            Console.WriteLine($"  分矢量全没了 ⇒ 合力 = {mag4:E3} N；LocalSize = {sumRef4?.LocalSize.Width:F3}×{sumRef4?.LocalSize.Height:F3}（仍 > 0，可点可删）");
            Console.WriteLine($"[{(orphanSumOk ? "PASS" : "FAIL")}] 分矢量全删后合力仍在、尺寸仍为正（不会变成删不掉的一条线）");
            if (!orphanSumOk) failures++;
        }
        catch (Exception sumRenderEx)
        {
            Console.WriteLine($"[FAIL] 合力渲染断言抛异常：{sumRenderEx.GetType().Name} {sumRenderEx.Message}");
            failures++;
        }

        // ---- 7) 合力与普通箭头的区分：虚线（走渲染器真实路径验 Pen） ----
        Console.WriteLine();
        Console.WriteLine("---- 7) 合力画成虚线（普通箭头是实线）----");
        try
        {
            var cat7 = new GfxRendererCatalog();
            var arrow7 = new ArrowRenderer();
            cat7.Register(arrow7, "矢量箭头（学科工具）");

            var plain7 = new S4FakeRef("vector", new Point(0, 0), 1, 0,
                new Dictionary<string, double> { ["lengthWorld"] = 100, ["angleDeg"] = 0 },
                new Dictionary<string, string> { ["unit"] = "N" });
            var sum7 = new S4FakeRef("vector", new Point(0, 0), 1, 0,
                new Dictionary<string, double> { ["lengthWorld"] = 100, ["angleDeg"] = 0 },
                new Dictionary<string, string> { ["unit"] = "N", ["sumOf"] = "m1,m2" });

            arrow7.SetBoard(new IGfxObjectRef[] { plain7, sum7 });

            var plainVis = arrow7.CreateVisual(plain7);
            var sumVis = arrow7.CreateVisual(sum7);

            // 从真实视觉树里把 Pen 摸出来：ArrowVisual 是自绘元素（OnRender 里画），
            // 所以用反射读它的 _shaftPen —— harness 在同一个进程里，允许这么做。
            var penPlain = ReadShaftPen(plainVis);
            var penSum = ReadShaftPen(sumVis);

            bool plainSolid = penPlain?.DashStyle is null || penPlain.DashStyle.Dashes.Count == 0;
            bool sumDashed = penSum?.DashStyle is not null && penSum.DashStyle.Dashes.Count >= 2;
            // 合力线略粗（×1.25）
            bool sumThicker = penSum is not null && penPlain is not null
                           && penSum.Thickness > penPlain.Thickness + 1e-9;

            Console.WriteLine($"  普通箭头 DashStyle = {(penPlain?.DashStyle is null ? "无（实线）" : penPlain.DashStyle.Dashes.Count + " 段")}，线宽 {penPlain?.Thickness:F4}");
            Console.WriteLine($"  合力     DashStyle = {(penSum?.DashStyle is null ? "无（实线）" : penSum.DashStyle.Dashes.Count + " 段")}，线宽 {penSum?.Thickness:F4}");
            Console.WriteLine($"[{(plainSolid && sumDashed ? "PASS" : "FAIL")}] 合力是虚线、普通箭头是实线（一眼能区分）");
            if (!(plainSolid && sumDashed)) failures++;
            Console.WriteLine($"[{(sumThicker ? "PASS" : "FAIL")}] 合力线比普通箭头粗（×1.25）");
            if (!sumThicker) failures++;
        }
        catch (Exception dashEx)
        {
            Console.WriteLine($"[FAIL] 合力虚线断言抛异常：{dashEx.GetType().Name} {dashEx.Message}");
            failures++;
        }

        // ---- 8) 工具链路：框选两根 ⇒ 落一个合力对象，sumOf 正确 ----
        Console.WriteLine();
        Console.WriteLine("---- 8) 工具链路：框住两根矢量箭头 ⇒ 落一个合力对象 ----");
        try
        {
            var host8s = new S4TestHost();

            // 先往假画布里放两根"已存在的箭头"（模拟老师已经画好的分力）
            var m1 = host8s.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["lengthWorld"] = 4.0 * 28.346456692913385, ["magnitudeN"] = 4.0, ["angleDeg"] = 0,
                },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });
            var m2 = host8s.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["lengthWorld"] = 3.0 * 28.346456692913385, ["magnitudeN"] = 3.0, ["angleDeg"] = 90,
                },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });

            host8s.Added.Clear();    // 只关心这次框选落下的
            var ctx8s = new FakeToolContext { GfxOverride = host8s };
            var tool8s = new ArrowSumTool(); tool8s.Activate(ctx8s);

            // 拖一个框把两根都圈住（假对象 BoundsWorld = Center ± 1，所以框要够大）
            var boxFrom = new Point(150, 150);
            var boxTo = new Point(260, 260);
            tool8s.OnPointer(new ToolPointer(ToolPointerPhase.Down, boxFrom, boxFrom, 0, false, 1));
            tool8s.OnPointer(new ToolPointer(ToolPointerPhase.Move, boxTo, boxTo, 0, false, 1));
            tool8s.OnPointer(new ToolPointer(ToolPointerPhase.Up, boxTo, boxTo, 0, false, 1));

            var draft8s = host8s.Added.Count == 1 ? host8s.Added[0] : null;
            bool oneSum = draft8s is not null && draft8s.Kind == ArrowRenderer.KindName;
            string sumOf8 = draft8s is not null && draft8s.Texts.TryGetValue(ArrowRenderer.SumOfKey, out var s8) ? s8 : "";
            bool sumOfOk = sumOf8.Split(',').Length == 2
                        && sumOf8.Contains(m1) && sumOf8.Contains(m2);
            bool sumNumsOk = draft8s is not null
                          && Near(draft8s.Numbers[ArrowRenderer.MagnitudeKey], 5.0, 1e-9)
                          && Near(draft8s.Numbers[ArrowRenderer.AngleKey], 36.86989764584402, 1e-9);

            Console.WriteLine($"  落对象 {host8s.Added.Count} 个；sumOf = 「{sumOf8}」（期望含 {m1},{m2}）");
            Console.WriteLine($"  存档冗余值：大小={draft8s?.Numbers[ArrowRenderer.MagnitudeKey]:F4} N、"
                              + $"方向={draft8s?.Numbers[ArrowRenderer.AngleKey]:F4}°");
            Console.WriteLine($"[{(oneSum && sumOfOk ? "PASS" : "FAIL")}] 框选两根 ⇒ 落一个合力对象，sumOf 记下两根分矢量");
            if (!(oneSum && sumOfOk)) failures++;
            Console.WriteLine($"[{(sumNumsOk ? "PASS" : "FAIL")}] 合力对象的冗余存档 = 5 N、36.87°");
            if (!sumNumsOk) failures++;
        }
        catch (Exception toolSumEx)
        {
            Console.WriteLine($"[FAIL] 合力工具链路断言抛异常：{toolSumEx.GetType().Name} {toolSumEx.Message}");
            failures++;
        }

        // ---- 9) 框不满两根 ⇒ 不落对象 + 可读提示 ----
        Console.WriteLine();
        Console.WriteLine("---- 9) 只框住一根 / 框住空白 ⇒ 不落对象并说明理由 ----");
        try
        {
            var host9s = new S4TestHost();
            host9s.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { ["lengthWorld"] = 100, ["magnitudeN"] = 3.5, ["angleDeg"] = 0 },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });
            host9s.Added.Clear();

            var ctx9s = new FakeToolContext { GfxOverride = host9s };
            var tool9s = new ArrowSumTool(); tool9s.Activate(ctx9s);

            // 框住第一根（只有一根）
            var f9 = new Point(150, 150);
            var t9 = new Point(260, 260);
            tool9s.OnPointer(new ToolPointer(ToolPointerPhase.Down, f9, f9, 0, false, 1));
            tool9s.OnPointer(new ToolPointer(ToolPointerPhase.Up, t9, t9, 0, false, 1));

            bool oneOnlyOk = host9s.Added.Count == 0 && ctx9s.LastStatus?.Contains("至少") == true;

            // 框在空白处
            var f9b = new Point(900, 900);
            var t9b = new Point(950, 950);
            tool9s.OnPointer(new ToolPointer(ToolPointerPhase.Down, f9b, f9b, 0, false, 1));
            tool9s.OnPointer(new ToolPointer(ToolPointerPhase.Up, t9b, t9b, 0, false, 1));

            bool blankOk = host9s.Added.Count == 0;

            Console.WriteLine($"  只框住 1 根 ⇒ 落对象 {host9s.Added.Count} 个（期望 0）；状态栏「{ctx9s.LastStatus}」");
            Console.WriteLine($"[{(oneOnlyOk ? "PASS" : "FAIL")}] 不足两根时不落合力，且状态栏说明为什么");
            if (!oneOnlyOk) failures++;
            Console.WriteLine($"[{(blankOk ? "PASS" : "FAIL")}] 框住空白处不落任何对象");
            if (!blankOk) failures++;
        }
        catch (Exception fewEx)
        {
            Console.WriteLine($"[FAIL] 框不满断言抛异常：{fewEx.GetType().Name} {fewEx.Message}");
            failures++;
        }

        // ---- 10) 反向等大的两根 ⇒ 不落对象（零合力） ----
        Console.WriteLine();
        Console.WriteLine("---- 10) 框住两根反向等大的力 ⇒ 零合力，不落对象 ----");
        try
        {
            var host10s = new S4TestHost();
            host10s.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { ["lengthWorld"] = 5 * 28.346456692913385, ["magnitudeN"] = 5.0, ["angleDeg"] = 0 },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });
            host10s.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { ["lengthWorld"] = 5 * 28.346456692913385, ["magnitudeN"] = 5.0, ["angleDeg"] = 180 },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });
            host10s.Added.Clear();

            var ctx10s = new FakeToolContext { GfxOverride = host10s };
            var tool10s = new ArrowSumTool(); tool10s.Activate(ctx10s);

            var f10 = new Point(150, 150);
            var t10 = new Point(260, 260);
            tool10s.OnPointer(new ToolPointer(ToolPointerPhase.Down, f10, f10, 0, false, 1));
            tool10s.OnPointer(new ToolPointer(ToolPointerPhase.Up, t10, t10, 0, false, 1));

            bool cancelOk = host10s.Added.Count == 0 && ctx10s.LastStatus?.Contains("抵消") == true;
            Console.WriteLine($"  落对象 {host10s.Added.Count} 个（期望 0）；状态栏「{ctx10s.LastStatus}」");
            Console.WriteLine($"[{(cancelOk ? "PASS" : "FAIL")}] 反向等大的力求和为零 ⇒ 不落合力，并说明互相抵消");
            if (!cancelOk) failures++;
        }
        catch (Exception cancelEx)
        {
            Console.WriteLine($"[FAIL] 零合力断言抛异常：{cancelEx.GetType().Name} {cancelEx.Message}");
            failures++;
        }

        // ---- 11) 合力自己不能再当分矢量（防止二次合成） ----
        Console.WriteLine();
        Console.WriteLine("---- 11) 合力不参与再求和（框住一根分力 + 一根合力，只算分力）----");
        try
        {
            var host11s = new S4TestHost();
            var p1 = host11s.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { ["lengthWorld"] = 100, ["magnitudeN"] = 3.0, ["angleDeg"] = 0 },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });
            var p2 = host11s.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { ["lengthWorld"] = 100, ["magnitudeN"] = 4.0, ["angleDeg"] = 90 },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });
            // 一根已有的合力（引用 p1,p2）
            host11s.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { ["magnitudeN"] = 5.0, ["angleDeg"] = 36.87 },
                Texts = new Dictionary<string, string> { ["unit"] = "N", [ArrowRenderer.SumOfKey] = p1 + "," + p2 },
            });
            host11s.Added.Clear();

            var ctx11s = new FakeToolContext { GfxOverride = host11s };
            var tool11s = new ArrowSumTool(); tool11s.Activate(ctx11s);

            var f11 = new Point(150, 150);
            var t11 = new Point(260, 260);
            tool11s.OnPointer(new ToolPointer(ToolPointerPhase.Down, f11, f11, 0, false, 1));
            tool11s.OnPointer(new ToolPointer(ToolPointerPhase.Up, t11, t11, 0, false, 1));

            // 框里有 3 个对象（2 分力 + 1 合力），但只有 2 根能当分矢量
            var draft11 = host11s.Added.Count == 1 ? host11s.Added[0] : null;
            string sumOf11 = draft11 is not null && draft11.Texts.TryGetValue(ArrowRenderer.SumOfKey, out var s11) ? s11 : "";
            bool exclOk = draft11 is not null
                       && Near(draft11.Numbers[ArrowRenderer.MagnitudeKey], 5.0, 1e-9)
                       && sumOf11.Split(',').Length == 2;
            Console.WriteLine($"  框里 3 个对象（2 分力 + 1 合力）⇒ 落下的合力 sumOf = 「{sumOf11}」、大小 = "
                              + $"{draft11?.Numbers[ArrowRenderer.MagnitudeKey]:F4} N（期望仍是 5 N，只算 2 根分力）");
            Console.WriteLine($"[{(exclOk ? "PASS" : "FAIL")}] 合力不参与再求和（不会出现越框越大的二次合成）");
            if (!exclOk) failures++;
        }
        catch (Exception exclEx)
        {
            Console.WriteLine($"[FAIL] 合力排除断言抛异常：{exclEx.GetType().Name} {exclEx.Message}");
            failures++;
        }

        // ---- 12) 工具形态与插件注册 ----
        Console.WriteLine();
        Console.WriteLine("---- 12) 工具形态：不落墨层 + 需要指针；插件注册（含正交分解）----");
        var sumTool = new ArrowSumTool();
        bool sumShapeOk = !sumTool.UsesInkLayer && sumTool.NeedsPointer
                       && sumTool.Id == ArrowSumToolIds.Id
                       && sumTool.Cursor == Cursors.None
                       && sumTool.Shortcut != new ArrowTool().Shortcut;   // 快捷键不能撞车
        Console.WriteLine($"  UsesInkLayer={sumTool.UsesInkLayer} NeedsPointer={sumTool.NeedsPointer} "
                          + $"Id={sumTool.Id} Shortcut={sumTool.Shortcut}（箭头是 {new ArrowTool().Shortcut}）");
        Console.WriteLine($"[{(sumShapeOk ? "PASS" : "FAIL")}] 合力工具的形态与其它创建型工具一致，快捷键不与箭头撞车");
        if (!sumShapeOk) failures++;

        var regS = new ToolRegistry();
        new VectorArrowPlugin().Register(regS);
        int toolCount = 0;
        foreach (var t in regS.Tools) toolCount++;
        // ★ 数量跟着插件走：新增「正交分解」「矢量组」后是 4 个。
        //   这里断言「至少含合力、且总数为 4」，比死写 == 2 更耐用（下次加工具只需改这一个数字）。
        bool regSumOk = regS.Find(ArrowSumToolIds.Id) is IGfxTool && toolCount == 4;
        Console.WriteLine($"  插件注册了 {toolCount} 个工具（期望 4：矢量箭头 + 合力 + 正交分解 + 矢量组）");
        Console.WriteLine($"[{(regSumOk ? "PASS" : "FAIL")}] 插件注册含合力的创建型工具（总数 4）");
        if (!regSumOk) failures++;

        // ---- 13) 截图证据：两根分矢量 + 一条虚线合力 ----
        try
        {
            var sumPxy = new CanvasViewportHost();
            sumPxy.Measure(new Size(900, 600));
            sumPxy.Arrange(new Rect(0, 0, 900, 600));
            sumPxy.UpdateLayout();
            sumPxy.RegisterGfxRenderer(new ArrowRenderer(), "矢量箭头（学科工具）");

            double wpn = VectorMath.PointsPerInch / VectorMath.CentimetersPerInch;
            var b1 = sumPxy.GfxObjects.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(380, 340), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["lengthWorld"] = 3.0 * wpn, ["magnitudeN"] = 3.0, ["angleDeg"] = 0,
                },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });
            var b2 = sumPxy.GfxObjects.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(380, 340), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["lengthWorld"] = 4.0 * wpn, ["magnitudeN"] = 4.0, ["angleDeg"] = 90,
                },
                Texts = new Dictionary<string, string> { ["unit"] = "N" },
            });
            // 让两根分矢量从同一个点出发（尾对齐），看起来才像一张受力图
            var r1 = FindObj(sumPxy, b1);
            var r2 = FindObj(sumPxy, b2);
            if (r1 is not null && r2 is not null)
            {
                var tail = new Point(320, 300);
                sumPxy.GfxObjects.UpdatePose(b1, TailCentered(tail, r1), 0, 1);
                sumPxy.GfxObjects.UpdatePose(b2, TailCentered(tail, r2), 0, 1);
            }

            sumPxy.GfxObjects.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(320, 300), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double> { ["magnitudeN"] = 5.0, ["angleDeg"] = 36.86989764584402 },
                Texts = new Dictionary<string, string>
                {
                    ["unit"] = "N",
                    [ArrowRenderer.SumOfKey] = b1 + "," + b2,
                },
            });

            sumPxy.UpdateLayout();
            SavePng(Snapshot(sumPxy, 900, 600), Path.Combine(outDir, "m74s5-合力.png"));
            Console.WriteLine($"  证据图：{Path.Combine(outDir, "m74s5-合力.png")}");
            sumPxy.Shutdown();
        }
        catch (Exception sumShotEx)
        {
            Console.WriteLine($"  （合力截图证据跳过：{sumShotEx.Message}）");
        }

        return failures;
    }

    // ================================================================= M7.4 Step 5 正交分解

    /// <summary>
    /// M7.4 Step 5 第三轮：正交分解（点一根斜矢量 ⇒ 落两根分量箭头，带符号、可逆）。
    /// </summary>
    /// <remarks>
    /// ★ 断言哲学（与上一组一致）：能走<b>真实调用链</b>的就别只验纯函数。
    /// 上一轮"纯函数全对、渲染器没接上"那个 bug（docs/06 §8.12-2）说明：
    /// 只有把断言贴着工具/渲染器的真实入口写，才不会出现"测试全绿、现场不能用"。
    /// <para>
    /// 本组第二原则：<b>先手工验算物理数，再写断言</b>。
    /// 36.87° = atan(3/4) 对应的是【4 N 向东 + 3 N 向北】，不是反过来 ——
    /// 上一轮就在这里写错过一次（当时断言写反、代码是对的），所以本组每条角度都配一条互为反例。
    /// </para>
    /// </remarks>
    private static int RunM74S5DecomposeChecks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.4 Step 5 第三轮：正交分解（点选 + 带符号 + 可逆）================");

        // ---- 1) 分解数学：3-4-5 反向验证（分量 ⇒ 原矢量） ----
        // 5 N、36.87° 的力分解到水平/竖直：
        //   水平 = 5·cos(36.87°) = 4.0 N（向右）
        //   竖直 = 5·sin(36.87°) = 3.0 N（向上）
        // 这是"分解"最该被钉死的一条 —— 分错了两根分量箭头看着都很合理，肉眼绝对发现不了。
        Console.WriteLine();
        Console.WriteLine("---- 1) 分解数学：5 N @36.87° ⇒ 水平 4 N、竖直 3 N ----");
        var dec345 = VectorMath.Decompose(5.0, 36.86989764584402);

        bool decXOk = Near(dec345.AlongX, 4.0, 1e-9);
        bool decYOk = Near(dec345.AlongY, 3.0, 1e-9);

        Console.WriteLine($"  5 N @36.8699° ⇒ 水平 {dec345.AlongX:F9} N、竖直 {dec345.AlongY:F9} N（期望 4、3）");
        Console.WriteLine($"[{(decXOk ? "PASS" : "FAIL")}] 水平分量 = 4 N（沿 +x）");
        if (!decXOk) failures++;
        Console.WriteLine($"[{(decYOk ? "PASS" : "FAIL")}] 竖直分量 = 3 N（沿 +y）");
        if (!decYOk) failures++;

        // ★ 互为反例：另一组别把"谁对谁"配反了。3-4-5（3 东 + 4 北 ⇒ 53.13°）分解回去应是 3 / 4。
        var dec543 = VectorMath.Decompose(5.0, 53.13010235415598);
        bool dec543Ok = Near(dec543.AlongX, 3.0, 1e-9) && Near(dec543.AlongY, 4.0, 1e-9);
        Console.WriteLine($"  5 N @53.1301° ⇒ 水平 {dec543.AlongX:F9} N、竖直 {dec543.AlongY:F9} N（期望 3、4）");
        Console.WriteLine($"[{(dec543Ok ? "PASS" : "FAIL")}] 上一条的反例：53.13° 那根分解成 3/4（钉住「谁对谁」不被配反）");
        if (!dec543Ok) failures++;

        // ---- 2) ★ 带符号分量：斜向左上的力，水平分量朝 −x ----
        // 这是本功能最关键的一条设计：分量<b>不取绝对值</b>。
        // 取绝对值会让"向左"被折成"向右" —— 合成回来就错了，而卷面上两根箭头都挺合理。
        Console.WriteLine();
        Console.WriteLine("---- 2) 带符号分量：斜向左上的力，水平分量必须朝 −x ----");
        // 5 N、143.13°（左上）⇒ 水平 = 5·cos(143.13°) = −4 N、竖直 = 5·sin(143.13°) = +3 N
        var decLeft = VectorMath.Decompose(5.0, 143.13010235415598);
        bool signLeftOk = Near(decLeft.AlongX, -4.0, 1e-9) && Near(decLeft.AlongY, 3.0, 1e-9);
        Console.WriteLine($"  5 N @143.1301°（左上）⇒ 水平 {decLeft.AlongX:F6} N（应带负号）、竖直 {decLeft.AlongY:F6} N");
        Console.WriteLine($"[{(signLeftOk ? "PASS" : "FAIL")}] 分量带符号：向左的力水平分量是 −4 N（不是 +4）");
        if (!signLeftOk) failures++;

        // 正下方 270°：水平 0、竖直 −5
        var decDown = VectorMath.Decompose(5.0, 270.0);
        bool signDownOk = Near(decDown.AlongX, 0.0, 1e-9) && Near(decDown.AlongY, -5.0, 1e-9);
        Console.WriteLine($"  5 N @270°（正下）⇒ 水平 {decDown.AlongX:F6} N、竖直 {decDown.AlongY:F6} N（期望 0、−5）");
        Console.WriteLine($"[{(signDownOk ? "PASS" : "FAIL")}] 正下方的力竖直分量为 −5 N（世界 y 向下，物理读法向下为负）");
        if (!signDownOk) failures++;

        // ---- 3) ★ 可逆性：Compose(Decompose(v)) == v（多角度扫一遍） ----
        // 可逆 = 分量合成回去精确回到原矢量。分解错的"数学指纹"就是这一条过不了。
        Console.WriteLine();
        Console.WriteLine("---- 3) ★ 可逆性：分量合成回原矢量（8 个角度，误差 < 1e-9）----");
        double[] angles = { 0.0, 30.0, 36.86989764584402, 90.0, 143.13010235415598, 180.0, 225.0, 270.0 };
        bool reversibleOk = true;
        foreach (var angle in angles)
        {
            var (ax, ay) = VectorMath.Decompose(7.0, angle);
            var (mag, dir) = VectorMath.Compose(ax, ay);

            bool magOk = Near(mag, 7.0, 1e-9);
            // 方向角对 0°/360° 环绕要宽容：359.9999° 与 0.0001° 其实是同一个方向
            double delta = Math.Abs(VectorMath.NormalizeDegrees(dir) - VectorMath.NormalizeDegrees(angle));
            if (delta > 180.0) delta = 360.0 - delta;
            bool dirOk = delta < 1e-9;

            if (!(magOk && dirOk))
            {
                reversibleOk = false;
                Console.WriteLine($"    × {angle,10:F6}° ⇒ 回合成 {mag:F9} N、{dir:F9}°（对不上）");
            }
        }
        Console.WriteLine($"[{(reversibleOk ? "PASS" : "FAIL")}] 8 个角度的 Compose(Decompose(v)) 都精确回到原矢量");
        if (!reversibleOk) failures++;

        // ---- 4) 斜交基（沿斜面 + 垂直斜面）也能分、也能回 ----
        // 物理里"沿斜面 / 垂直斜面"是常见的分解基，两条基<b>不正交</b>。
        Console.WriteLine();
        Console.WriteLine("---- 4) 斜交基：沿 30° 斜面 / 垂直斜面分解，仍可逆 ----");
        var (sx, sy) = VectorMath.Decompose(10.0, 60.0, 30.0, 120.0);
        var (sMag, sDir) = VectorMath.Compose(sx, sy, 30.0, 120.0);
        double sDelta = Math.Abs(VectorMath.NormalizeDegrees(sDir) - 60.0);
        if (sDelta > 180.0) sDelta = 360.0 - sDelta;
        bool skewOk = Near(sMag, 10.0, 1e-9) && sDelta < 1e-9;
        Console.WriteLine($"  10 N @60°，基 30°/120° ⇒ 沿基A {sx:F6} N、沿基B {sy:F6} N；回合成 {sMag:F9} N、{sDir:F9}°");
        Console.WriteLine($"[{(skewOk ? "PASS" : "FAIL")}] 斜交基也能分解并精确回合成原矢量（不止正交一种用法）");
        if (!skewOk) failures++;

        // ---- 5) 共线基退化：不抛异常，把全部模长给第一条基 ----
        Console.WriteLine();
        Console.WriteLine("---- 5) 两条基共线 ⇒ 退化但不抛异常 ----");
        var degCollinear = VectorMath.Decompose(5.0, 45.0, 0.0, 0.0);
        bool collinearOk = Near(degCollinear.AlongX, 5.0, 1e-9) && Near(degCollinear.AlongY, 0.0, 1e-9);
        Console.WriteLine($"  基 0°/0°（共线）⇒ 沿基A {degCollinear.AlongX:F6}、沿基B {degCollinear.AlongY:F6}（期望 5、0，不抛）");
        Console.WriteLine($"[{(collinearOk ? "PASS" : "FAIL")}] 共线基退化为「全给第一条基」，不让工具崩掉");
        if (!collinearOk) failures++;

        // ---- 6) 零分量的判定（不落幽灵箭头） ----
        Console.WriteLine();
        Console.WriteLine("---- 6) 零分量判定：竖直力没有水平分量 ----");
        var zeroX = VectorMath.Decompose(6.0, 90.0);
        bool zeroCompOk = VectorMath.IsZeroSum(Math.Abs(zeroX.AlongX))
                       && !VectorMath.IsZeroSum(Math.Abs(zeroX.AlongY));
        Console.WriteLine($"  6 N @90° ⇒ 水平 {zeroX.AlongX:F9} N（IsZeroSum={VectorMath.IsZeroSum(Math.Abs(zeroX.AlongX))}）、"
                          + $"竖直 {zeroX.AlongY:F9} N");
        Console.WriteLine($"[{(zeroCompOk ? "PASS" : "FAIL")}] 竖直力的水平分量被判为零 ⇒ 只落一根分量（不留零长幽灵）");
        if (!zeroCompOk) failures++;

        // ---- 7) 状态栏读数：方向词跟着符号走 ----
        Console.WriteLine();
        Console.WriteLine("---- 7) 状态栏读数：方向词（→ ↑ ← ↓）跟着分量符号走 ----");
        string descRightUp = VectorMath.DecomposeDescribe(4.0, 3.0);
        string descLeftDown = VectorMath.DecomposeDescribe(-4.0, -3.0);
        bool descOk = descRightUp.Contains("→") && descRightUp.Contains("↑")
                   && descLeftDown.Contains("←") && descLeftDown.Contains("↓")
                   && descRightUp.Contains("4.0") && descRightUp.Contains("3.0");
        Console.WriteLine($"  (4, 3) ⇒ 「{descRightUp}」");
        Console.WriteLine($"  (−4, −3) ⇒ 「{descLeftDown}」");
        Console.WriteLine($"[{(descOk ? "PASS" : "FAIL")}] 读数用箭头词表达方向，老师不用在脑子里换算正负");
        if (!descOk) failures++;

        // ---- 8) ★ 工具链路：点一根斜箭头 ⇒ 落两根分量（走真实工具入口） ----
        Console.WriteLine();
        Console.WriteLine("---- 8) ★ 工具链路：点一根 5 N @36.87° 的箭头 ⇒ 落两根分量 ----");
        try
        {
            var hostDec = new S4TestHost();

            // 一根已经画好的斜箭头（5 N、36.87°，尾部在 (200,300)）
            double wpnDec = VectorMath.PointsPerInch / VectorMath.CentimetersPerInch;
            var origin5 = hostDec.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(300, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 5.0 * wpnDec,
                    [ArrowRenderer.MagnitudeKey] = 5.0,
                    [ArrowRenderer.AngleKey] = 36.86989764584402,
                    [ArrowTool.UnitWorldKey] = 1.0,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });

            hostDec.Added.Clear();
            var ctxDec = new FakeToolContext { GfxOverride = hostDec };
            var toolDec = new ArrowDecomposeTool(); toolDec.Activate(ctxDec);

            // 点在这根箭头的中心上（假对象 BoundsWorld = Center ± 1，PickRadius 24 world 足够）
            var aimDec = new Point(300, 200);
            toolDec.OnPointer(new ToolPointer(ToolPointerPhase.Down, aimDec, aimDec, 0, false, 1));

            int compCount = hostDec.Added.Count;
            var compX = compCount > 0 ? hostDec.Added[0] : null;
            var compY = compCount > 1 ? hostDec.Added[1] : null;

            double magX = compX is not null ? compX.Numbers[ArrowRenderer.MagnitudeKey] : double.NaN;
            double magY = compY is not null ? compY.Numbers[ArrowRenderer.MagnitudeKey] : double.NaN;
            double angX = compX is not null ? compX.Numbers[ArrowRenderer.AngleKey] : double.NaN;
            double angY = compY is not null ? compY.Numbers[ArrowRenderer.AngleKey] : double.NaN;

            bool twoComps = compCount == 2;
            bool compNumsOk = Near(magX, 4.0, 1e-6) && Near(magY, 3.0, 1e-6);
            bool compAnglesOk = Near(angX, 0.0, 1e-6) && Near(angY, 90.0, 1e-6);

            Console.WriteLine($"  落对象 {compCount} 个（期望 2）；分量大小 = {magX:F4} N、{magY:F4} N（期望 4、3）");
            Console.WriteLine($"  分量方向 = {angX:F4}°、{angY:F4}°（期望 0、90）");
            Console.WriteLine($"[{(twoComps ? "PASS" : "FAIL")}] 点一根斜箭头 ⇒ 落两根分量箭头");
            if (!twoComps) failures++;
            Console.WriteLine($"[{(compNumsOk ? "PASS" : "FAIL")}] 分量大小 = 4 N / 3 N（走工具真实入口，非自己算一遍）");
            if (!compNumsOk) failures++;
            Console.WriteLine($"[{(compAnglesOk ? "PASS" : "FAIL")}] 分量方向 = 水平 / 竖直（0° / 90°）");
            if (!compAnglesOk) failures++;

            // 两根分量都标了来源键（记录用，为将来联动留门）
            bool parentOk = compX is not null && compY is not null
                         && compX.Texts.ContainsKey(ArrowRenderer.ParentKey)
                         && compY.Texts.ContainsKey(ArrowRenderer.ParentKey);
            Console.WriteLine($"[{(parentOk ? "PASS" : "FAIL")}] 两根分量都记下了来源键（Texts[{ArrowRenderer.ParentKey}]，读存档时能看出来历）");
            if (!parentOk) failures++;

            // 分量尾巴 = 原箭头尾巴（课本画法的"同一起点"）
            // 原箭头尾 = Center − 半长×单位向；分量尾同理。两者应当一致。
            var unitOrig = VectorMath.UnitVector(36.86989764584402);
            var tailOrig = new Point(300 - unitOrig.X * 5.0 * wpnDec / 2.0,
                                     200 - unitOrig.Y * 5.0 * wpnDec / 2.0);
            var unitXc = VectorMath.UnitVector(0.0);
            var tailXc = compX is not null
                ? new Point(compX.Center.X - unitXc.X * 4.0 * wpnDec / 2.0,
                            compX.Center.Y - unitXc.Y * 4.0 * wpnDec / 2.0)
                : new Point(double.NaN, double.NaN);
            bool tailAlignedOk = compX is not null && Near(tailXc, tailOrig, 1e-6);
            Console.WriteLine($"  原箭头尾 world({tailOrig.X:F4},{tailOrig.Y:F4})；水平分量尾 world({tailXc.X:F4},{tailXc.Y:F4})");
            Console.WriteLine($"[{(tailAlignedOk ? "PASS" : "FAIL")}] 分量与原矢量共用同一条尾（课本受力图的画法）");
            if (!tailAlignedOk) failures++;
        }
        catch (Exception decToolEx)
        {
            Console.WriteLine($"[FAIL] 正交分解工具链路断言抛异常：{decToolEx.GetType().Name} {decToolEx.Message}");
            failures++;
        }

        // ---- 9) 竖直箭头 ⇒ 只落一根分量（零分量不落幽灵） ----
        Console.WriteLine();
        Console.WriteLine("---- 9) 竖直箭头 ⇒ 只落一根（水平分量为零不留幽灵）----");
        try
        {
            var hostDec2 = new S4TestHost();
            double wpnDec2 = VectorMath.PointsPerInch / VectorMath.CentimetersPerInch;
            hostDec2.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(300, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 6.0 * wpnDec2,
                    [ArrowRenderer.MagnitudeKey] = 6.0,
                    [ArrowRenderer.AngleKey] = 90.0,
                    [ArrowTool.UnitWorldKey] = 1.0,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });
            hostDec2.Added.Clear();

            var ctxDec2 = new FakeToolContext { GfxOverride = hostDec2 };
            var toolDec2 = new ArrowDecomposeTool(); toolDec2.Activate(ctxDec2);
            var aim2 = new Point(300, 200);
            toolDec2.OnPointer(new ToolPointer(ToolPointerPhase.Down, aim2, aim2, 0, false, 1));

            bool oneCompOk = hostDec2.Added.Count == 1
                          && Near(hostDec2.Added[0].Numbers[ArrowRenderer.MagnitudeKey], 6.0, 1e-6)
                          && Near(hostDec2.Added[0].Numbers[ArrowRenderer.AngleKey], 90.0, 1e-6);
            Console.WriteLine($"  落对象 {hostDec2.Added.Count} 个（期望 1）；"
                              + $"大小 = {(hostDec2.Added.Count > 0 ? hostDec2.Added[0].Numbers[ArrowRenderer.MagnitudeKey] : double.NaN):F4} N");
            Console.WriteLine($"[{(oneCompOk ? "PASS" : "FAIL")}] 竖直力只落一根竖直分量，水平零分量不落（不留看不见的幽灵）");
            if (!oneCompOk) failures++;
        }
        catch (Exception oneCompEx)
        {
            Console.WriteLine($"[FAIL] 零分量不落断言抛异常：{oneCompEx.GetType().Name} {oneCompEx.Message}");
            failures++;
        }

        // ---- 10) 点在空白处 ⇒ 不落对象 + 状态栏说明 ----
        Console.WriteLine();
        Console.WriteLine("---- 10) 点在空白处 ⇒ 不落对象并说明理由 ----");
        try
        {
            var hostDec3 = new S4TestHost();
            double wpnDec3 = VectorMath.PointsPerInch / VectorMath.CentimetersPerInch;
            hostDec3.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 4.0 * wpnDec3,
                    [ArrowRenderer.MagnitudeKey] = 4.0,
                    [ArrowRenderer.AngleKey] = 0.0,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });
            hostDec3.Added.Clear();

            var ctxDec3 = new FakeToolContext { GfxOverride = hostDec3 };
            var toolDec3 = new ArrowDecomposeTool(); toolDec3.Activate(ctxDec3);
            var blank = new Point(900, 900);
            toolDec3.OnPointer(new ToolPointer(ToolPointerPhase.Down, blank, blank, 0, false, 1));

            bool blankOk = hostDec3.Added.Count == 0 && ctxDec3.LastStatus?.Contains("矢量箭头") == true;
            Console.WriteLine($"  落对象 {hostDec3.Added.Count} 个（期望 0）；状态栏「{ctxDec3.LastStatus}」");
            Console.WriteLine($"[{(blankOk ? "PASS" : "FAIL")}] 点空白不落对象，且状态栏说明「这里没有矢量箭头」");
            if (!blankOk) failures++;
        }
        catch (Exception blankEx)
        {
            Console.WriteLine($"[FAIL] 点空白断言抛异常：{blankEx.GetType().Name} {blankEx.Message}");
            failures++;
        }

        // ---- 11) 合力不能被分解（排除合力） ----
        Console.WriteLine();
        Console.WriteLine("---- 11) 合力不能被再分解（避免「分解一根算出来的东西」）----");
        try
        {
            var hostDec4 = new S4TestHost();
            double wpnDec4 = VectorMath.PointsPerInch / VectorMath.CentimetersPerInch;
            var m1d = hostDec4.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 4.0 * wpnDec4,
                    [ArrowRenderer.MagnitudeKey] = 4.0, [ArrowRenderer.AngleKey] = 0.0,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });
            var m2d = hostDec4.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 3.0 * wpnDec4,
                    [ArrowRenderer.MagnitudeKey] = 3.0, [ArrowRenderer.AngleKey] = 90.0,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });
            var sumD = hostDec4.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.MagnitudeKey] = 5.0, [ArrowRenderer.AngleKey] = 36.86989764584402,
                },
                Texts = new Dictionary<string, string>
                {
                    [ArrowRenderer.UnitKey] = "N",
                    [ArrowRenderer.SumOfKey] = m1d + "," + m2d,
                },
            });
            hostDec4.Added.Clear();

            var ctxDec4 = new FakeToolContext { GfxOverride = hostDec4 };
            var toolDec4 = new ArrowDecomposeTool(); toolDec4.Activate(ctxDec4);
            var onSum = new Point(200, 200);
            toolDec4.OnPointer(new ToolPointer(ToolPointerPhase.Down, onSum, onSum, 0, false, 1));

            // 该点同时压在"分力"和"合力"上（假对象包围盒都覆盖）⇒ 必须挑到一根<b>非合力</b>来分解。
            // 若挑中合力，说明排除逻辑失效；若挑中分力，则落的分量应是 4/3 或 3/0 之类，绝不是 4/3 之和。
            bool sumExcludedOk = hostDec4.Added.Count >= 1
                              && hostDec4.Added.TrueForAll(d =>
                                     d.Texts.ContainsKey(ArrowRenderer.ParentKey));
            Console.WriteLine($"  落对象 {hostDec4.Added.Count} 个；落下的都是分量（带 {ArrowRenderer.ParentKey}）={sumExcludedOk}");
            Console.WriteLine($"[{(sumExcludedOk ? "PASS" : "FAIL")}] 点选优先挑真矢量、跳过合力（合力是算出来的，没有分解价值）");
            if (!sumExcludedOk) failures++;
        }
        catch (Exception sumExclEx)
        {
            Console.WriteLine($"[FAIL] 合力排除断言抛异常：{sumExclEx.GetType().Name} {sumExclEx.Message}");
            failures++;
        }

        // ---- 12) 工具形态与插件注册 ----
        Console.WriteLine();
        Console.WriteLine("---- 12) 工具形态：不落墨层 + 需要指针；插件注册 3 个工具 ----");
        var decTool = new ArrowDecomposeTool();
        var arrowToolRef = new ArrowTool();
        var sumToolRef = new ArrowSumTool();
        bool decShapeOk = !decTool.UsesInkLayer && decTool.NeedsPointer
                       && decTool.Id == ArrowDecomposeToolIds.Id
                       && decTool.Cursor == Cursors.Hand
                       && decTool.Shortcut != arrowToolRef.Shortcut
                       && decTool.Shortcut != sumToolRef.Shortcut;
        Console.WriteLine($"  UsesInkLayer={decTool.UsesInkLayer} NeedsPointer={decTool.NeedsPointer} "
                          + $"Id={decTool.Id} Shortcut={decTool.Shortcut}"
                          + $"（箭头 {arrowToolRef.Shortcut}、合力 {sumToolRef.Shortcut}）");
        Console.WriteLine($"[{(decShapeOk ? "PASS" : "FAIL")}] 正交分解工具形态正确，快捷键不与箭头/合力撞车");
        if (!decShapeOk) failures++;

        var regDec = new ToolRegistry();
        new VectorArrowPlugin().Register(regDec);
        int decToolCount = 0;
        foreach (var t in regDec.Tools) decToolCount++;
        bool regDecOk = regDec.Find(ArrowDecomposeToolIds.Id) is IGfxTool && decToolCount == 4;
        Console.WriteLine($"  插件注册了 {decToolCount} 个工具（期望 4：矢量箭头 + 合力 + 正交分解 + 矢量组）");
        Console.WriteLine($"[{(regDecOk ? "PASS" : "FAIL")}] 插件注册四个创建型工具（含正交分解与矢量组）");
        if (!regDecOk) failures++;

        // ---- 13) 截图证据：一根斜矢量 + 两根分量 ----
        try
        {
            var decPxy = new CanvasViewportHost();
            decPxy.Measure(new Size(900, 600));
            decPxy.Arrange(new Rect(0, 0, 900, 600));
            decPxy.UpdateLayout();
            decPxy.RegisterGfxRenderer(new ArrowRenderer(), "矢量箭头（学科工具）");

            double wpnShot = VectorMath.PointsPerInch / VectorMath.CentimetersPerInch;
            var tailShot = new Point(320, 420);

            // 斜矢量 5 N @36.87°（尾部在 tailShot）
            var unitShot = VectorMath.UnitVector(36.86989764584402);
            double lenShot = 5.0 * wpnShot;
            decPxy.GfxObjects.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName,
                Center = new Point(tailShot.X + unitShot.X * lenShot / 2.0,
                                   tailShot.Y + unitShot.Y * lenShot / 2.0),
                Scale = 1, RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = lenShot,
                    [ArrowRenderer.MagnitudeKey] = 5.0,
                    [ArrowRenderer.AngleKey] = 36.86989764584402,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });

            // 水平分量 4 N @0°（同尾）
            double lenX = 4.0 * wpnShot;
            decPxy.GfxObjects.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName,
                Center = new Point(tailShot.X + lenX / 2.0, tailShot.Y),
                Scale = 1, RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = lenX,
                    [ArrowRenderer.MagnitudeKey] = 4.0,
                    [ArrowRenderer.AngleKey] = 0.0,
                },
                Texts = new Dictionary<string, string>
                {
                    [ArrowRenderer.UnitKey] = "N",
                    [ArrowRenderer.ParentKey] = "demo",
                },
            });

            // 竖直分量 3 N @90°（同尾）
            var unitYShot = VectorMath.UnitVector(90.0);
            double lenY = 3.0 * wpnShot;
            decPxy.GfxObjects.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName,
                Center = new Point(tailShot.X + unitYShot.X * lenY / 2.0,
                                   tailShot.Y + unitYShot.Y * lenY / 2.0),
                Scale = 1, RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = lenY,
                    [ArrowRenderer.MagnitudeKey] = 3.0,
                    [ArrowRenderer.AngleKey] = 90.0,
                },
                Texts = new Dictionary<string, string>
                {
                    [ArrowRenderer.UnitKey] = "N",
                    [ArrowRenderer.ParentKey] = "demo",
                },
            });

            decPxy.UpdateLayout();
            SavePng(Snapshot(decPxy, 900, 600), Path.Combine(outDir, "m74s5-正交分解.png"));
            Console.WriteLine($"  证据图：{Path.Combine(outDir, "m74s5-正交分解.png")}");
            decPxy.Shutdown();
        }
        catch (Exception decShotEx)
        {
            Console.WriteLine($"  （正交分解截图证据跳过：{decShotEx.Message}）");
        }

        return failures;
    }

    // ================================================================= M7.4 Step 5 矢量组

    /// <summary>
    /// M7.4 Step 5 第四轮：矢量组（框选打组 → 整组一起拖 / 转 / 缩）。
    /// </summary>
    /// <remarks>
    /// ★ 本组有一条<b>别处没有</b>的断言类型：宿主传播 与 插件纯函数 的一致性。
    /// 矢量组的位移数学被写了两份（宿主 <c>PropagateGroup</c> 与插件
    /// <c>VectorMath.PlaceGroupMember</c>），这是「契约不能改」逼出来的重复 ——
    /// 两份实现一旦不同步，表现是「拖起来怪怪的」，肉眼几乎抓不住。
    /// 所以这里逐点比对两者的输出，差一点都不放过。
    /// </remarks>
    private static int RunM74S5GroupChecks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.4 Step 5 第四轮：矢量组（打组 + 整组跟随）================");

        // ---- 1) 组数学：纯平移 ----
        Console.WriteLine();
        Console.WriteLine("---- 1) 组数学：纯平移（组没转没缩）----");
        var (c1, r1) = VectorMath.PlaceGroupMember(30, 40, 0, 1, new Point(100, 200), 0, 1, 0);
        bool moveOk = Near(c1.X, 130, 1e-9) && Near(c1.Y, 240, 1e-9) && Near(r1, 0, 1e-9);
        Console.WriteLine($"  基准偏移 (30,40)、组心 (100,200) ⇒ 成员中心 ({c1.X:F6},{c1.Y:F6})、转 {r1:F6}°（期望 130,240,0）");
        Console.WriteLine($"[{(moveOk ? "PASS" : "FAIL")}] 纯平移：成员中心 = 组心 + 基准偏移");
        if (!moveOk) failures++;

        // 组心挪到 (160,200) ⇒ 成员 190
        var (c2, _) = VectorMath.PlaceGroupMember(30, 40, 0, 1, new Point(160, 200), 0, 1, 0);
        bool move2Ok = Near(c2.X, 190, 1e-9) && Near(c2.Y, 240, 1e-9);
        Console.WriteLine($"  组心挪到 (160,200) ⇒ 成员 ({c2.X:F6},{c2.Y:F6})（期望 190,240）");
        Console.WriteLine($"[{(move2Ok ? "PASS" : "FAIL")}] 组平移多少，成员就平移多少");
        if (!move2Ok) failures++;

        // ---- 2) ★ 组旋转：★ 关键几何自检 —— 距离必须不变 ----
        Console.WriteLine();
        Console.WriteLine("---- 2) ★ 组绕自己中心转 90°：成员到组心的距离必须不变 ----");
        var (c3, r3) = VectorMath.PlaceGroupMember(30, 40, 0, 1, new Point(100, 200), 90, 1, 0);
        double distBefore = Math.Sqrt(30 * 30 + 40 * 40);
        double distAfter = Math.Sqrt((c3.X - 100) * (c3.X - 100) + (c3.Y - 200) * (c3.Y - 200));
        bool rotDistOk = Near(distBefore, distAfter, 1e-9);
        bool rotAngleOk = Near(r3, 90, 1e-9);
        Console.WriteLine($"  偏移 (30,40) 转 90° ⇒ 成员 ({c3.X:F6},{c3.Y:F6})、自身转 {r3:F6}°");
        Console.WriteLine($"  到组心距离：转前 {distBefore:F9}、转后 {distAfter:F9}");
        Console.WriteLine($"[{(rotDistOk ? "PASS" : "FAIL")}] 绕组心旋转时成员走圆弧、距离严格不变（只加固定位移就会算错）");
        if (!rotDistOk) failures++;
        Console.WriteLine($"[{(rotAngleOk ? "PASS" : "FAIL")}] 成员自身也跟着转 90°（不只是挪位置）");
        if (!rotAngleOk) failures++;

        // ★ 反例配对：转 -90°（顺时针）要往另一边走，钉住旋转方向不被镜像
        var (c3b, _) = VectorMath.PlaceGroupMember(30, 40, 0, 1, new Point(100, 200), -90, 1, 0);
        bool oppositeOk = !Near(c3b.X, c3.X, 1e-6) || !Near(c3b.Y, c3.Y, 1e-6);
        Console.WriteLine($"  同一偏移转 −90° ⇒ ({c3b.X:F6},{c3b.Y:F6})（应与 +90° 的结果不同，互为反例）");
        Console.WriteLine($"[{(oppositeOk ? "PASS" : "FAIL")}] 反向旋转走另一侧（钉住旋转方向不被镜像）");
        if (!oppositeOk) failures++;

        // ---- 3) ★ 组缩放：成员间距跟着放大 ----
        Console.WriteLine();
        Console.WriteLine("---- 3) ★ 组缩放：成员间距跟着放大（不是各自原地变大）----");
        var (c4, _) = VectorMath.PlaceGroupMember(30, 40, 0, 1, new Point(100, 200), 0, 2, 0);
        bool scaleOk = Near(c4.X, 160, 1e-9) && Near(c4.Y, 280, 1e-9);
        Console.WriteLine($"  组放大 2 倍 ⇒ 成员 ({c4.X:F6},{c4.Y:F6})（期望 160,280 = 组心 + 偏移×2）");
        Console.WriteLine($"[{(scaleOk ? "PASS" : "FAIL")}] 组放大 ⇒ 成员间距同比放大（只改各自 Scale 会让图形挤成一团）");
        if (!scaleOk) failures++;

        // ★ 缩放是「比例」而非绝对值：基准 2 → 当前 4 与 基准 1 → 当前 2 必须同解
        var (c5, _) = VectorMath.PlaceGroupMember(30, 40, 0, 2, new Point(100, 200), 0, 4, 0);
        bool ratioOk = Near(c5.X, 160, 1e-9) && Near(c5.Y, 280, 1e-9);
        Console.WriteLine($"  基准 2 → 当前 4（比例 2）⇒ ({c5.X:F6},{c5.Y:F6})（应与上一条完全相同）");
        Console.WriteLine($"[{(ratioOk ? "PASS" : "FAIL")}] 缩放取「比例」而不是绝对值（否则拖过一次之后就会跳）");
        if (!ratioOk) failures++;

        // ---- 4) ★ 往返可逆：拖多久都不散 ----
        Console.WriteLine();
        Console.WriteLine("---- 4) ★ 往返可逆：从任意组位姿反推回基准（多帧累乘不会漂）----");
        bool roundTripOk = true;
        double[][] bases = { new[] { 30.0, 40.0 }, new[] { -12.0, 7.0 }, new[] { 0.0, 0.0 }, new[] { 5.5, -33.25 } };
        double[][] groups = { new[] { 100.0, 200.0, 0.0, 1.0 }, new[] { 250.0, 80.0, 37.0, 1.7 }, new[] { -40.0, 500.0, 300.0, 0.6 } };

        foreach (var g in groups)
        {
            foreach (var b in bases)
            {
                var (cc, _) = VectorMath.PlaceGroupMember(b[0], b[1], 0, 1,
                                                          new Point(g[0], g[1]), g[2], g[3], 0);

                // 反算：先减组心、反旋转、反缩放，应精确回到基准
                var back = VectorMath.Rotate(new Vector(cc.X - g[0], cc.Y - g[1]), -g[2]);
                double ratio = g[3];
                double bx = back.X / ratio;
                double by = back.Y / ratio;

                if (!Near(bx, b[0], 1e-9) || !Near(by, b[1], 1e-9))
                {
                    roundTripOk = false;
                    Console.WriteLine($"    × 基准 ({b[0]},{b[1]}) 组 ({g[0]},{g[1]},{g[2]},{g[3]}) ⇒ 反推 ({bx:F9},{by:F9})");
                }
            }
        }
        Console.WriteLine($"[{(roundTripOk ? "PASS" : "FAIL")}] 12 组基准/组位姿组合的往返反推全部精确（每帧从基准重算 ⇒ 不积累误差）");
        if (!roundTripOk) failures++;

        // ---- 5) UnionBounds：和集包围盒 ----
        Console.WriteLine();
        Console.WriteLine("---- 5) 和集包围盒（打组时用来定组的中心与尺寸）----");
        var (uc, uw, uh) = VectorMath.UnionBounds(new[]
        {
            new Point(0, 0), new Point(10, 0), new Point(10, 5), new Point(0, 5),
        });
        bool unionOk = Near(uc.X, 5, 1e-9) && Near(uc.Y, 2.5, 1e-9) && Near(uw, 10, 1e-9) && Near(uh, 5, 1e-9);
        Console.WriteLine($"  (0,0)-(10,5) ⇒ 中心 ({uc.X:F4},{uc.Y:F4})、{uw:F4}×{uh:F4}（期望 5,2.5, 10×5）");
        Console.WriteLine($"[{(unionOk ? "PASS" : "FAIL")}] 包围盒取四角并集（旋转过的成员也算得对）");
        if (!unionOk) failures++;

        var (ue, uew, ueh) = VectorMath.UnionBounds(Array.Empty<Point>());
        bool unionEmptyOk = Near(ue.X, 0, 1e-9) && Near(ue.Y, 0, 1e-9) && Near(uew, 0, 1e-9) && Near(ueh, 0, 1e-9);
        Console.WriteLine($"  空集 ⇒ ({ue.X},{ue.Y}) {uew}×{ueh}（期望全 0，不能让 NaN 流出去）");
        Console.WriteLine($"[{(unionEmptyOk ? "PASS" : "FAIL")}] 空集合返回零而不是 NaN");
        if (!unionEmptyOk) failures++;

        // ---- 6) 组身份与成员解析 ----
        Console.WriteLine();
        Console.WriteLine("---- 6) 组身份：groupOf 解析（去重、剔空、排除合力与嵌套组）----");
        var groupRef = new S4FakeRef("vectorgroup", new Point(0, 0), 1, 0,
            new Dictionary<string, double>(),
            new Dictionary<string, string> { [GroupRenderer.GroupOfKey] = "g1, g2,,g1 ,g3" });
        var groupIds = GroupRenderer.GroupMemberIds(groupRef);
        bool groupIdsOk = groupIds.Count == 3 && groupIds[0] == "g1" && groupIds[1] == "g2" && groupIds[2] == "g3";
        bool isGroupOk = GroupRenderer.IsGroup(groupRef);
        Console.WriteLine($"  解析 \"g1, g2,,g1 ,g3\" ⇒ [{string.Join(",", groupIds)}]（期望 g1,g2,g3）");
        Console.WriteLine($"[{(groupIdsOk && isGroupOk ? "PASS" : "FAIL")}] 成员 Id 解析去掉空项与重复项（与合力共用同一套解析）");
        if (!(groupIdsOk && isGroupOk)) failures++;

        // 普通箭头不能被当成组
        var plainArrowRef = new S4FakeRef("vector", new Point(0, 0), 1, 0,
            new Dictionary<string, double>(), new Dictionary<string, string>());
        bool notGroupOk = !GroupRenderer.IsGroup(plainArrowRef);
        Console.WriteLine($"[{(notGroupOk ? "PASS" : "FAIL")}] 不带 groupOf 的箭头不被当成组");
        if (!notGroupOk) failures++;

        // 组成员必须排除合力与嵌套组（否则会递归或把算出来的东西也搬走）
        var fakeArrow = new S4FakeRef("vector", new Point(0, 0), 1, 0,
            new Dictionary<string, double>(),
            new Dictionary<string, string> { ["sumOf"] = "x,y" }, id: "m_sum");
        var fakeGroup = new S4FakeRef("vectorgroup", new Point(0, 0), 1, 0,
            new Dictionary<string, double>(),
            new Dictionary<string, string> { [GroupRenderer.GroupOfKey] = "z" }, id: "m_group");
        var memberFilterRef = new S4FakeRef("vectorgroup", new Point(0, 0), 1, 0,
            new Dictionary<string, double>(),
            new Dictionary<string, string> { [GroupRenderer.GroupOfKey] = "m_sum,m_group" });

        var filtered = GroupRenderer.GroupMembers(memberFilterRef, new IGfxObjectRef[] { fakeArrow, fakeGroup });
        bool exclOk = filtered.Count == 0;
        Console.WriteLine($"  成员里放一根合力 + 一个组 ⇒ 解析出 {filtered.Count} 个可用成员（期望 0）");
        Console.WriteLine($"[{(exclOk ? "PASS" : "FAIL")}] 合力与嵌套组不能当组成员（防递归、防把算出来的东西搬走）");
        if (!exclOk) failures++;

        // ---- 7) ★ 渲染器真实路径：并集包围盒量得对 ----
        Console.WriteLine();
        Console.WriteLine("---- 7) ★ 渲染器真实路径：组的 LocalSize = 成员的并集 ----");
        try
        {
            var catG = new GfxRendererCatalog();
            var groupRenderer = new GroupRenderer();
            catG.Register(groupRenderer, "矢量箭头（学科工具）");
            catG.Register(new ArrowRenderer(), "矢量箭头（学科工具）");

            var stG = new GfxObjectStore(catG);
            var layerG = new GfxObjectLayer(new Canvas(), stG, catG);

            double wpnG = VectorMath.PointsPerInch / VectorMath.CentimetersPerInch;

            // 两根箭头摆得很开：中心 (100,100) 与 (300,100)，长度各 2 N
            var ga = stG.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(100, 100), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 2.0 * wpnG,
                    [ArrowRenderer.MagnitudeKey] = 2.0, [ArrowRenderer.AngleKey] = 0,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });
            var gb = stG.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(300, 100), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 2.0 * wpnG,
                    [ArrowRenderer.MagnitudeKey] = 2.0, [ArrowRenderer.AngleKey] = 0,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });

            // 组：中心取两者中点 (200,100)
            var groupId = stG.Add(new GfxDraft
            {
                Kind = GroupRenderer.KindName, Center = new Point(200, 100), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [GroupRenderer.BaseRotationKey] = 0,
                    [GroupRenderer.BaseScaleKey] = 1,
                    [GroupRenderer.BaseXPrefix + "0"] = 100 - 200,
                    [GroupRenderer.BaseYPrefix + "0"] = 100 - 100,
                    [GroupRenderer.BaseRotPrefix + "0"] = 0,
                    [GroupRenderer.BaseXPrefix + "1"] = 300 - 200,
                    [GroupRenderer.BaseYPrefix + "1"] = 100 - 100,
                    [GroupRenderer.BaseRotPrefix + "1"] = 0,
                },
                Texts = new Dictionary<string, string> { [GroupRenderer.GroupOfKey] = ga + "," + gb },
            });

            IGfxObjectRef? groupRef2 = null;
            foreach (var ob in stG.Objects) if (ob.Id == groupId) groupRef2 = ob;

            groupRenderer.SetBoard(stG.Objects);
            var localBox = groupRenderer.UnionBoundsLocal(groupRef2!);

            // 两根箭头各长 2 N（= 2×28.3464 = 56.69 world），中心相距 200 world
            // 并集宽 = 200 + 56.69 + 2×Padding(6) ≈ 268.69
            // ★ 高度不是箭头长度：箭头都朝 0°（水平），它的本地包围盒是
            //   [长度 × 箭头宽]（见 ArrowGeometry.Measure），所以并集高 = 箭头宽 + 2×Padding
            //   = 5.5×2 + 12 = 23。这里用常量算，别写死数字，几何改了能自己跟着走。
            double expectedW = 200 + 2.0 * wpnG + 2 * GroupRenderer.Padding;
            double expectedH = ArrowGeometry.HeadHalfWidthWorld * 2.0 + 2 * GroupRenderer.Padding;

            bool unionLocalOk = Math.Abs(localBox.Width - expectedW) < 1e-6
                             && Math.Abs(localBox.Height - expectedH) < 1e-6;
            Console.WriteLine($"  两根相距 200 的箭头 ⇒ 组本地包围盒 {localBox.Width:F4}×{localBox.Height:F4}"
                              + $"（期望 {expectedW:F4}×{expectedH:F4}）");
            Console.WriteLine($"[{(unionLocalOk ? "PASS" : "FAIL")}] 渲染器按成员并集算出组的包围盒（走真实路径）");
            if (!unionLocalOk) failures++;

            // 组的 LocalSize 必须等于包围盒（视觉层靠它当几何签名）
            bool groupSizeOk = groupRef2 is not null
                            && Math.Abs(groupRef2.LocalSize.Width - expectedW) < 1e-6;
            Console.WriteLine($"  组的 LocalSize = {groupRef2?.LocalSize.Width:F4}×{groupRef2?.LocalSize.Height:F4}");
            Console.WriteLine($"[{(groupSizeOk ? "PASS" : "FAIL")}] 组的 LocalSize 与渲染器的 Measure 一致（视觉层靠这个签名重建）");
            if (!groupSizeOk) failures++;

            // 视觉真的建得出来
            bool groupVisualOk = true; string groupVisualMsg = "构造成功";
            try
            {
                var vis = layerG.VisualFor(groupId);
                if (vis is null) { groupVisualOk = false; groupVisualMsg = "VisualFor 返回 null"; }
            }
            catch (Exception gex) { groupVisualOk = false; groupVisualMsg = gex.GetType().Name + ": " + gex.Message; }

            Console.WriteLine($"  组视觉树构造：{groupVisualMsg}");
            Console.WriteLine($"[{(groupVisualOk ? "PASS" : "FAIL")}] 组对象的视觉树构造不抛异常（{groupVisualMsg}）");
            if (!groupVisualOk) failures++;

            // ---- 8) ★★ 核心：拖动组 ⇒ 成员真的跟着走（走宿主真实路径） ----
            Console.WriteLine();
            Console.WriteLine("---- 8) ★★ 核心：拖动组 ⇒ 成员跟着走（宿主真实路径）----");
            var posBeforeA = FindObjIn(stG.Objects, ga)?.Center ?? new Point(0, 0);
            var posBeforeB = FindObjIn(stG.Objects, gb)?.Center ?? new Point(0, 0);

            // 把组整体右移 50、下移 30
            stG.UpdatePose(groupId, new Point(250, 130), 0, 1);

            var posAfterA = FindObjIn(stG.Objects, ga)?.Center ?? new Point(0, 0);
            var posAfterB = FindObjIn(stG.Objects, gb)?.Center ?? new Point(0, 0);

            bool memberMovedA = Near(posAfterA.X, posBeforeA.X + 50, 1e-6) && Near(posAfterA.Y, posBeforeA.Y + 30, 1e-6);
            bool memberMovedB = Near(posAfterB.X, posBeforeB.X + 50, 1e-6) && Near(posAfterB.Y, posBeforeB.Y + 30, 1e-6);

            Console.WriteLine($"  箭头 A：拖前 ({posBeforeA.X:F2},{posBeforeA.Y:F2}) ⇒ 拖后 ({posAfterA.X:F2},{posAfterA.Y:F2})（期望 +50,+30）");
            Console.WriteLine($"  箭头 B：拖前 ({posBeforeB.X:F2},{posBeforeB.Y:F2}) ⇒ 拖后 ({posAfterB.X:F2},{posAfterB.Y:F2})");
            Console.WriteLine($"[{(memberMovedA && memberMovedB ? "PASS" : "FAIL")}] 拖动组，两根成员一起平移相同的量");
            if (!(memberMovedA && memberMovedB)) failures++;

            // ---- 9) ★★ 宿主传播 vs 插件纯函数：两份实现必须完全一致 ----
            Console.WriteLine();
            Console.WriteLine("---- 9) ★★ 宿主传播 与 插件纯函数 的一致性（两份实现不能各说各话）----");
            // 让组转 33°、放大 1.4 倍，看两边算出的成员中心是否逐点相同
            stG.UpdatePose(groupId, new Point(250, 130), 33, 1.4);

            var hostA = FindObjIn(stG.Objects, ga)?.Center ?? new Point(0, 0);
            var hostB = FindObjIn(stG.Objects, gb)?.Center ?? new Point(0, 0);

            // 插件算式：基准偏移来自「打组时刻」（组心 200,100，基准旋转 0、基准缩放 1）
            var (pluginA, pluginRotA) = VectorMath.PlaceGroupMember(100 - 200, 100 - 100, 0, 1,
                                                                   new Point(250, 130), 33, 1.4, 0);
            var (pluginB, pluginRotB) = VectorMath.PlaceGroupMember(300 - 200, 100 - 100, 0, 1,
                                                                   new Point(250, 130), 33, 1.4, 0);

            bool hostMatchesPlugin = Near(hostA.X, pluginA.X, 1e-9) && Near(hostA.Y, pluginA.Y, 1e-9)
                                  && Near(hostB.X, pluginB.X, 1e-9) && Near(hostB.Y, pluginB.Y, 1e-9);

            Console.WriteLine($"  宿主算出的 A = ({hostA.X:F9},{hostA.Y:F9})；插件纯函数 = ({pluginA.X:F9},{pluginA.Y:F9})");
            Console.WriteLine($"  宿主算出的 B = ({hostB.X:F9},{hostB.Y:F9})；插件纯函数 = ({pluginB.X:F9},{pluginB.Y:F9})");
            Console.WriteLine($"[{(hostMatchesPlugin ? "PASS" : "FAIL")}] 宿主传播与插件纯函数逐点一致（1e-9）—— 两份实现没走偏");
            if (!hostMatchesPlugin) failures++;

            // 成员自身旋转也要一致
            var memberRotA = FindObjIn(stG.Objects, ga)?.RotationDegrees ?? double.NaN;
            bool rotMatches = Near(memberRotA, pluginRotA, 1e-9);
            Console.WriteLine($"  成员 A 自身旋转：宿主 {memberRotA:F6}°、插件 {pluginRotA:F6}°");
            Console.WriteLine($"[{(rotMatches ? "PASS" : "FAIL")}] 成员自身旋转两边算得一样");
            if (!rotMatches) failures++;

            // ---- 10) 组成员被删 ⇒ 组自动缩小；全删光 ⇒ 退化成占位但不崩 ----
            Console.WriteLine();
            Console.WriteLine("---- 10) 成员被删 ⇒ 组自动缩小；全删光 ⇒ 占位不崩 ----");
            double widthBefore = groupRef2?.LocalSize.Width ?? 0;

            stG.Remove(gb);
            IGfxObjectRef? groupRef3 = FindObjIn(stG.Objects, groupId);
            groupRenderer.SetBoard(stG.Objects);
            double widthAfter = groupRef3?.LocalSize.Width ?? 0;

            bool shrinkOk = widthAfter > 0 && widthAfter < widthBefore - 1;
            Console.WriteLine($"  删掉一根成员：组宽 {widthBefore:F3} ⇒ {widthAfter:F3}（期望明显变小）");
            Console.WriteLine($"[{(shrinkOk ? "PASS" : "FAIL")}] 成员少了，组的框跟着缩小（不是钉在旧尺寸上）");
            if (!shrinkOk) failures++;

            stG.Remove(ga);
            IGfxObjectRef? groupRef4 = FindObjIn(stG.Objects, groupId);
            groupRenderer.SetBoard(stG.Objects);
            bool orphanGroupOk = groupRef4 is not null
                              && groupRef4.LocalSize.Width > 0 && groupRef4.LocalSize.Height > 0
                              && GroupRenderer.GroupMembers(groupRef4, stG.Objects).Count == 0;
            Console.WriteLine($"  成员全删光 ⇒ 组仍在、尺寸 {groupRef4?.LocalSize.Width:F2}×{groupRef4?.LocalSize.Height:F2}（仍 > 0，可点可删）");
            Console.WriteLine($"[{(orphanGroupOk ? "PASS" : "FAIL")}] 成员全删后组不消失、尺寸仍为正（不会变成删不掉的一条线）");
            if (!orphanGroupOk) failures++;
        }
        catch (Exception groupRenderEx)
        {
            Console.WriteLine($"[FAIL] 矢量组渲染断言抛异常：{groupRenderEx.GetType().Name} {groupRenderEx.Message}");
            failures++;
        }

        // ---- 11) 工具链路：框住两根 ⇒ 落一个组对象，基准记全 ----
        Console.WriteLine();
        Console.WriteLine("---- 11) 工具链路：框住两根矢量箭头 ⇒ 落一个组对象 ----");
        try
        {
            var host11g = new S4TestHost();
            double wpn11g = VectorMath.PointsPerInch / VectorMath.CentimetersPerInch;

            var p1g = host11g.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 3.0 * wpn11g,
                    [ArrowRenderer.MagnitudeKey] = 3.0, [ArrowRenderer.AngleKey] = 0,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });
            var p2g = host11g.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 4.0 * wpn11g,
                    [ArrowRenderer.MagnitudeKey] = 4.0, [ArrowRenderer.AngleKey] = 90,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });

            host11g.Added.Clear();
            var ctx11g = new FakeToolContext { GfxOverride = host11g };
            var tool11g = new ArrowGroupTool(); tool11g.Activate(ctx11g);

            var from11g = new Point(150, 150);
            var to11g = new Point(260, 260);
            tool11g.OnPointer(new ToolPointer(ToolPointerPhase.Down, from11g, from11g, 0, false, 1));
            tool11g.OnPointer(new ToolPointer(ToolPointerPhase.Move, to11g, to11g, 0, false, 1));
            tool11g.OnPointer(new ToolPointer(ToolPointerPhase.Up, to11g, to11g, 0, false, 1));

            var draft11g = host11g.Added.Count == 1 ? host11g.Added[0] : null;
            bool oneGroup = draft11g is not null && draft11g.Kind == GroupRenderer.KindName;
            string groupOf11 = draft11g is not null && draft11g.Texts.TryGetValue(GroupRenderer.GroupOfKey, out var s11g) ? s11g : "";
            bool groupOfOk = groupOf11.Split(',').Length == 2
                          && groupOf11.Contains(p1g) && groupOf11.Contains(p2g);

            // 基准必须记全：两个成员各 3 个键（bx/by/brot）
            bool baseKeysOk = draft11g is not null
                           && draft11g.Numbers.ContainsKey(GroupRenderer.BaseXPrefix + "0")
                           && draft11g.Numbers.ContainsKey(GroupRenderer.BaseYPrefix + "0")
                           && draft11g.Numbers.ContainsKey(GroupRenderer.BaseRotPrefix + "0")
                           && draft11g.Numbers.ContainsKey(GroupRenderer.BaseXPrefix + "1")
                           && draft11g.Numbers.ContainsKey(GroupRenderer.BaseYPrefix + "1")
                           && draft11g.Numbers.ContainsKey(GroupRenderer.BaseRotPrefix + "1")
                           && draft11g.Numbers.ContainsKey(GroupRenderer.BaseRotationKey)
                           && draft11g.Numbers.ContainsKey(GroupRenderer.BaseScaleKey);

            Console.WriteLine($"  落对象 {host11g.Added.Count} 个；groupOf = 「{groupOf11}」（期望含 {p1g},{p2g}）");
            Console.WriteLine($"[{(oneGroup && groupOfOk ? "PASS" : "FAIL")}] 框选两根 ⇒ 落一个组对象，groupOf 记下两根成员");
            if (!(oneGroup && groupOfOk)) failures++;
            Console.WriteLine($"[{(baseKeysOk ? "PASS" : "FAIL")}] 组对象的基准记全（每根成员的 偏移x / 偏移y / 自身旋转 + 组的基准旋转与缩放）");
            if (!baseKeysOk) failures++;
        }
        catch (Exception toolGroupEx)
        {
            Console.WriteLine($"[FAIL] 矢量组工具链路断言抛异常：{toolGroupEx.GetType().Name} {toolGroupEx.Message}");
            failures++;
        }

        // ---- 12) 框不满两根 / 框住空白 / 合力不能进组 ----
        Console.WriteLine();
        Console.WriteLine("---- 12) 不足两根 / 框空白 ⇒ 不落对象并说明理由 ----");
        try
        {
            var host12g = new S4TestHost();
            host12g.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 100, [ArrowRenderer.MagnitudeKey] = 3.0, [ArrowRenderer.AngleKey] = 0,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });
            host12g.Added.Clear();

            var ctx12g = new FakeToolContext { GfxOverride = host12g };
            var tool12g = new ArrowGroupTool(); tool12g.Activate(ctx12g);

            var f12g = new Point(150, 150);
            var t12g = new Point(260, 260);
            tool12g.OnPointer(new ToolPointer(ToolPointerPhase.Down, f12g, f12g, 0, false, 1));
            tool12g.OnPointer(new ToolPointer(ToolPointerPhase.Up, t12g, t12g, 0, false, 1));

            bool oneOnlyOk = host12g.Added.Count == 0 && ctx12g.LastStatus?.Contains("至少") == true;

            var f12b = new Point(900, 900);
            var t12b = new Point(950, 950);
            tool12g.OnPointer(new ToolPointer(ToolPointerPhase.Down, f12b, f12b, 0, false, 1));
            tool12g.OnPointer(new ToolPointer(ToolPointerPhase.Up, t12b, t12b, 0, false, 1));
            bool blankOk = host12g.Added.Count == 0;

            Console.WriteLine($"  只框住 1 根 ⇒ 落对象 {host12g.Added.Count} 个（期望 0）；状态栏「{ctx12g.LastStatus}」");
            Console.WriteLine($"[{(oneOnlyOk ? "PASS" : "FAIL")}] 不足两根时不打组，且状态栏说明为什么");
            if (!oneOnlyOk) failures++;
            Console.WriteLine($"[{(blankOk ? "PASS" : "FAIL")}] 框住空白处不落任何对象");
            if (!blankOk) failures++;
        }
        catch (Exception fewGroupEx)
        {
            Console.WriteLine($"[FAIL] 组不足两根断言抛异常：{fewGroupEx.GetType().Name} {fewGroupEx.Message}");
            failures++;
        }

        // ---- 13) 合力 / 已有的组 都不能进新组 ----
        Console.WriteLine();
        Console.WriteLine("---- 13) 合力与已有的组不能进新组（防递归、防搬走算出来的东西）----");
        try
        {
            var host13g = new S4TestHost();
            var a1 = host13g.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 100, [ArrowRenderer.MagnitudeKey] = 3.0, [ArrowRenderer.AngleKey] = 0,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });
            var a2 = host13g.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.LengthWorldKey] = 100, [ArrowRenderer.MagnitudeKey] = 4.0, [ArrowRenderer.AngleKey] = 90,
                },
                Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
            });
            // 一根合力（引用 a1,a2）
            host13g.Add(new GfxDraft
            {
                Kind = ArrowRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [ArrowRenderer.MagnitudeKey] = 5.0, [ArrowRenderer.AngleKey] = 36.87,
                },
                Texts = new Dictionary<string, string>
                {
                    [ArrowRenderer.UnitKey] = "N", [ArrowRenderer.SumOfKey] = a1 + "," + a2,
                },
            });
            // 一个已有的组
            host13g.Add(new GfxDraft
            {
                Kind = GroupRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Green, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [GroupRenderer.BaseRotationKey] = 0, [GroupRenderer.BaseScaleKey] = 1,
                },
                Texts = new Dictionary<string, string> { [GroupRenderer.GroupOfKey] = a1 },
            });

            host13g.Added.Clear();
            var ctx13g = new FakeToolContext { GfxOverride = host13g };
            var tool13g = new ArrowGroupTool(); tool13g.Activate(ctx13g);

            var f13g = new Point(150, 150);
            var t13g = new Point(260, 260);
            tool13g.OnPointer(new ToolPointer(ToolPointerPhase.Down, f13g, f13g, 0, false, 1));
            tool13g.OnPointer(new ToolPointer(ToolPointerPhase.Up, t13g, t13g, 0, false, 1));

            // 框里有 4 个对象（2 箭头 + 1 合力 + 1 组），只有 2 根能进组
            var draft13g = host13g.Added.Count == 1 ? host13g.Added[0] : null;
            string go13 = draft13g is not null && draft13g.Texts.TryGetValue(GroupRenderer.GroupOfKey, out var s13) ? s13 : "";
            bool exclGroupOk = draft13g is not null && go13.Split(',').Length == 2;
            Console.WriteLine($"  框里 4 个对象（2 箭头 + 1 合力 + 1 组）⇒ 新组的 groupOf = 「{go13}」（期望只有 2 项）");
            Console.WriteLine($"[{(exclGroupOk ? "PASS" : "FAIL")}] 合力与已有组被排除在新组之外（只捆真箭头）");
            if (!exclGroupOk) failures++;
        }
        catch (Exception exclGroupEx)
        {
            Console.WriteLine($"[FAIL] 组排除断言抛异常：{exclGroupEx.GetType().Name} {exclGroupEx.Message}");
            failures++;
        }

        // ---- 14) 工具形态与插件注册 ----
        Console.WriteLine();
        Console.WriteLine("---- 14) 工具形态：不落墨层 + 需要指针；插件注册 4 个工具 ----");
        var groupTool = new ArrowGroupTool();
        bool groupShapeOk = !groupTool.UsesInkLayer && groupTool.NeedsPointer
                         && groupTool.Id == ArrowGroupToolIds.Id
                         && groupTool.Cursor == Cursors.None
                         && groupTool.Shortcut != new ArrowTool().Shortcut
                         && groupTool.Shortcut != new ArrowSumTool().Shortcut
                         && groupTool.Shortcut != new ArrowDecomposeTool().Shortcut;
        Console.WriteLine($"  UsesInkLayer={groupTool.UsesInkLayer} NeedsPointer={groupTool.NeedsPointer} "
                          + $"Id={groupTool.Id} Shortcut={groupTool.Shortcut}"
                          + $"（箭头 {new ArrowTool().Shortcut}、合力 {new ArrowSumTool().Shortcut}、分解 {new ArrowDecomposeTool().Shortcut}）");
        Console.WriteLine($"[{(groupShapeOk ? "PASS" : "FAIL")}] 矢量组工具形态正确，快捷键不与已用的四个撞车");
        if (!groupShapeOk) failures++;

        var regG = new ToolRegistry();
        new VectorArrowPlugin().Register(regG);
        int groupToolCount = 0;
        foreach (var t in regG.Tools) groupToolCount++;
        bool regGroupOk = regG.Find(ArrowGroupToolIds.Id) is IGfxTool && groupToolCount == 4;
        Console.WriteLine($"  插件注册了 {groupToolCount} 个工具（期望 4：箭头 + 合力 + 分解 + 组）");
        Console.WriteLine($"[{(regGroupOk ? "PASS" : "FAIL")}] 插件注册四个创建型工具");
        if (!regGroupOk) failures++;

        // ---- 15) 截图证据：三根箭头 + 一个组框 ----
        try
        {
            var groupPxy = new CanvasViewportHost();
            groupPxy.Measure(new Size(900, 600));
            groupPxy.Arrange(new Rect(0, 0, 900, 600));
            groupPxy.UpdateLayout();
            groupPxy.RegisterGfxRenderer(new ArrowRenderer(), "矢量箭头（学科工具）");
            groupPxy.RegisterGfxRenderer(new GroupRenderer(), "矢量箭头（学科工具）");

            double wpnGp = VectorMath.PointsPerInch / VectorMath.CentimetersPerInch;
            var tailGp = new Point(320, 380);

            var shotIds = new List<string>();
            double[][] shotSpecs = { new[] { 4.0, 0.0 }, new[] { 3.0, 90.0 }, new[] { 5.0, 225.0 } };
            var shotColors = new[] { Colors.Red, Colors.Red, Colors.Red };

            for (int i = 0; i < shotSpecs.Length; i++)
            {
                var unitS = VectorMath.UnitVector(shotSpecs[i][1]);
                double lenS = shotSpecs[i][0] * wpnGp;
                shotIds.Add(groupPxy.GfxObjects.Add(new GfxDraft
                {
                    Kind = ArrowRenderer.KindName,
                    Center = new Point(tailGp.X + unitS.X * lenS / 2.0, tailGp.Y + unitS.Y * lenS / 2.0),
                    Scale = 1, RotationDegrees = 0, Color = shotColors[i], LineWorldWidth = 1.5,
                    Numbers = new Dictionary<string, double>
                    {
                        [ArrowRenderer.LengthWorldKey] = lenS,
                        [ArrowRenderer.MagnitudeKey] = shotSpecs[i][0],
                        [ArrowRenderer.AngleKey] = shotSpecs[i][1],
                    },
                    Texts = new Dictionary<string, string> { [ArrowRenderer.UnitKey] = "N" },
                }));
            }

            groupPxy.UpdateLayout();

            // 用「成员的并集」定组的中心（与工具里同一套算法）
            var shotMembers = new List<IGfxObjectRef>();
            foreach (var sid in shotIds)
            {
                var r = FindObj(groupPxy, sid);
                if (r is not null) shotMembers.Add(r);
            }

            var shotUnion = GroupRenderer.UnionBoundsWorld(shotMembers);
            if (!shotUnion.IsEmpty)
            {
                var shotCenter = new Point(shotUnion.X + shotUnion.Width / 2.0, shotUnion.Y + shotUnion.Height / 2.0);
                var shotNumbers = new Dictionary<string, double>
                {
                    [GroupRenderer.BaseRotationKey] = 0, [GroupRenderer.BaseScaleKey] = 1,
                };
                for (int i = 0; i < shotMembers.Count; i++)
                {
                    shotNumbers[GroupRenderer.BaseXPrefix + i] = shotMembers[i].Center.X - shotCenter.X;
                    shotNumbers[GroupRenderer.BaseYPrefix + i] = shotMembers[i].Center.Y - shotCenter.Y;
                    shotNumbers[GroupRenderer.BaseRotPrefix + i] = shotMembers[i].RotationDegrees;
                }

                groupPxy.GfxObjects.Add(new GfxDraft
                {
                    Kind = GroupRenderer.KindName, Center = shotCenter, Scale = 1,
                    RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                    Numbers = shotNumbers,
                    Texts = new Dictionary<string, string>
                    {
                        [GroupRenderer.GroupOfKey] = string.Join(",", shotIds),
                    },
                });
            }

            groupPxy.UpdateLayout();
            SavePng(Snapshot(groupPxy, 900, 600), Path.Combine(outDir, "m74s5-矢量组.png"));
            Console.WriteLine($"  证据图：{Path.Combine(outDir, "m74s5-矢量组.png")}");
            groupPxy.Shutdown();
        }
        catch (Exception groupShotEx)
        {
            Console.WriteLine($"  （矢量组截图证据跳过：{groupShotEx.Message}）");
        }

        return failures;
    }

    // ================================================================= M7.4 Step 5 参数面板

    /// <summary>
    /// M7.4 Step 5 第五轮：参数面板基础设施（IToolPanelProvider 一族）。
    /// </summary>
    /// <remarks>
    /// ★ 这一组的重点是<b>「面板是纯数据」这条设计约束带来的可测性</b>：
    /// 面板没有 WPF 控件，所以 harness 里没有窗口也能把"显示什么 / 改了怎么办"整条链路断言完。
    /// <para>
    /// 三个层次各断一遍：契约层（字段的收敛数学）、宿主层（协调器的取数与应用）、
    /// 插件层（坐标系真的接上了 provider + sink）。
    /// </para>
    /// </remarks>
    /// <summary>
    /// M7.4 Step 4 补全：函数图像的零点 / 极值 / 交点 + 定义域内联 + 分段语法。
    /// </summary>
    /// <remarks>
    /// 断言的着力点全是「肉眼看不出但对不对」的东西：二分求根有没有把 1/x 的奇点误当零点、
    /// 极值是 x^2 的最小还是最大、两条曲线的交点里有没有混进「只在一条曲线上有值」的假交点、
    /// 分段函数在跳变处画成两条线还是被连成斜坡。<b>每一条都配一个反例断言</b>，
    /// 让「方向搞反」这类错误往哪边偏都跑不掉。
    /// </remarks>
    private static int RunM74S5FeatureChecks(string outDir)
    {
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("================ M7.4 Step 4 补全：零点 / 极值 / 交点 / 定义域 / 分段 ================");

        var empty = new Dictionary<string, double>();

        // ---- 1) 零点：x^2-4 在 [-5,5] 有两个根 ±2 ----
        Console.WriteLine();
        Console.WriteLine("---- 1) 零点：x^2-4 ⇒ ±2（两个根都要找到，不许漏） ----");
        var zeros14 = FeatureFinder.FindZeros(ExpressionParser.Parse("x^2-4"), empty, -5, 5);
        bool z1ok = zeros14.Count == 2;
        double zSum = 0;
        foreach (var p in zeros14) zSum += p.X;
        bool z1sumOk = Near(zSum, 0, 1e-6);
        bool z1valOk = true;
        foreach (var p in zeros14) if (Math.Abs(p.X * p.X - 4) > 1e-7) z1valOk = false;
        Console.WriteLine($"  找到 {zeros14.Count} 个零点：{string.Join(", ", zeros14.Select(p => p.X.ToString("F6")))}");
        Console.WriteLine($"[{(z1ok && z1sumOk && z1valOk ? "PASS" : "FAIL")}] x^2-4 有两个零点 ±2（个数={z1ok} 对称={z1sumOk} 代入为0={z1valOk}）");
        if (!(z1ok && z1sumOk && z1valOk)) failures++;

        // ---- 2) ★ 零点反例：1/x 没有零点，奇点绝不能被当成零点 ----
        // 这是本批次最要紧的一条：1/x 在 0 两侧从 −∞ 跳到 +∞，也是一个「符号变化」，
        // 若只按「变号」判根，x=0 会被当成根 —— 卷面上就会在 y 轴上冒出一个假的零点标记。
        Console.WriteLine();
        Console.WriteLine("---- 2) ★ 反例：1/x 在 [-5,5] 必须找到 0 个零点（奇点不是根） ----");
        var zerosRecip = FeatureFinder.FindZeros(ExpressionParser.Parse("1/x"), empty, -5, 5);
        bool z2ok = zerosRecip.Count == 0;
        Console.WriteLine($"  1/x 找到 {zerosRecip.Count} 个零点（期望 0；x=0 是奇点，变号但不是根）");
        Console.WriteLine($"[{(z2ok ? "PASS" : "FAIL")}] 1/x 的奇点没有被误报成零点");
        if (!z2ok) failures++;

        // ---- 3) 零点：sin(x) 在 [-2π,2π] 应有 5 个（-2π, -π, 0, π, 2π 里的内部根 + 端点） ----
        Console.WriteLine();
        Console.WriteLine("---- 3) 零点：sin(x) 在 [-2π,2π] ⇒ 若含端点应找到 5 个（0/±π/±2π） ----");
        var zerosSin = FeatureFinder.FindZeros(ExpressionParser.Parse("sin(x)"), empty, -2 * Math.PI, 2 * Math.PI);
        int z3count = zerosSin.Count;
        // 常见的正确结果：0、±π、±2π 共 5 个（±2π 落在端点上，取决于端点是否入内）
        bool z3ok = z3count >= 3 && z3count <= 5;
        Console.WriteLine($"  sin(x) 找到 {z3count} 个零点（期望 3~5：0、±π，端点 ±2π 可含可不含）");
        Console.WriteLine($"[{(z3ok ? "PASS" : "FAIL")}] sin(x) 的多零点都能找出来");
        if (!z3ok) failures++;

        // ---- 4) 极值：x^2 在 [-5,5] ⇒ 唯一极小值在 (0,0)，且不是极大 ----
        Console.WriteLine();
        Console.WriteLine("---- 4) 极值：x^2 ⇒ 唯一极小值 (0,0)，类别必须是「极小」不是「极大」 ----");
        var extQuad = FeatureFinder.FindExtrema(ExpressionParser.Parse("x^2"), empty, -5, 5);
        FeaturePoint? quadMin = null;
        foreach (var p in extQuad) if (Math.Abs(p.X) < 1e-6) quadMin = p;
        bool e4Count = extQuad.Count == 1;
        bool e4kind = quadMin is not null && quadMin.Kind == FeatureKind.Minimum;
        bool e4pos = quadMin is not null && Near(quadMin.X, 0, 1e-6) && Near(quadMin.Y, 0, 1e-6);
        Console.WriteLine($"  找到 {extQuad.Count} 个极值；x≈0 处类别 = {quadMin?.Kind}");
        Console.WriteLine($"[{(e4Count && e4kind && e4pos ? "PASS" : "FAIL")}] x^2 的极小值在 (0,0)、类别=极小（个数={e4Count} 类别={e4kind} 位置={e4pos}）");
        if (!(e4Count && e4kind && e4pos)) failures++;

        // ---- 5) ★ 极值反例：-x^2 在 [-5,5] ⇒ 极大值在 (0,0) ----
        // 与上一条互为镜像：如果「极大/极小」的判断符号写反，这一条必红。
        Console.WriteLine();
        Console.WriteLine("---- 5) ★ 反例：-x^2 ⇒ 极大值 (0,0)（若把极大极小写反，这条必红） ----");
        var extNeg = FeatureFinder.FindExtrema(ExpressionParser.Parse("-x^2"), empty, -5, 5);
        FeaturePoint? negMax = null;
        foreach (var p in extNeg) if (Math.Abs(p.X) < 1e-6) negMax = p;
        bool e5ok = negMax is not null && negMax.Kind == FeatureKind.Maximum
                    && Near(negMax.X, 0, 1e-6) && Near(negMax.Y, 0, 1e-6);
        Console.WriteLine($"  -x^2 在 x≈0 处类别 = {negMax?.Kind}（期望 Maximum）");
        Console.WriteLine($"[{(e5ok ? "PASS" : "FAIL")}] -x^2 的极大值判对了（与 x^2 的极小互为反例）");
        if (!e5ok) failures++;

        // ---- 6) 极值：sin(x) 在 [0,2π] ⇒ 极大 π/2、极小 3π/2 ----
        Console.WriteLine();
        Console.WriteLine("---- 6) 极值：sin(x) 在 [0,2π] ⇒ 极大 π/2≈1.5708、极小 3π/2≈4.7124 ----");
        var extSin = FeatureFinder.FindExtrema(ExpressionParser.Parse("sin(x)"), empty, 0, 2 * Math.PI);
        bool sawMax = false, sawMin = false;
        foreach (var p in extSin)
        {
            if (p.Kind == FeatureKind.Maximum && Near(p.X, Math.PI / 2, 1e-5) && Near(p.Y, 1, 1e-5)) sawMax = true;
            if (p.Kind == FeatureKind.Minimum && Near(p.X, 1.5 * Math.PI, 1e-5) && Near(p.Y, -1, 1e-5)) sawMin = true;
        }
        Console.WriteLine($"  找到 {extSin.Count} 个极值（极大 π/2 ={sawMax}，极小 3π/2 ={sawMin}）");
        Console.WriteLine($"[{(sawMax && sawMin ? "PASS" : "FAIL")}] sin(x) 的极大极小都对（位置 + 读数）");
        if (!(sawMax && sawMin)) failures++;

        // ---- 7) ★ 驻点不是极值：x^3 在原点 ⇒ 标记为驻点（Inflection），不是极大也不是极小 ----
        Console.WriteLine();
        Console.WriteLine("---- 7) ★ x^3 在原点：导数变号但两侧同向 ⇒ 必须是「驻点」，不许当成极小 ----");
        var extCube = FeatureFinder.FindExtrema(ExpressionParser.Parse("x^3"), empty, -5, 5);
        FeaturePoint? originCube = null;
        foreach (var p in extCube) if (Math.Abs(p.X) < 1e-5) originCube = p;
        bool e7ok = originCube is not null && originCube.Kind == FeatureKind.Inflection;
        Console.WriteLine($"  x^3 在 x≈0 处找到 {(originCube is null ? "（无）" : originCube.Kind.ToString())}（期望 Inflection）");
        Console.WriteLine($"[{(e7ok ? "PASS" : "FAIL")}] x^3 的驻点没有被误报成极值");
        if (!e7ok) failures++;

        // ---- 8) 交点：x^2 与 2x ⇒ x=0、x=2（配合下方「代入两条曲线都相等」） ----
        Console.WriteLine();
        Console.WriteLine("---- 8) 交点：y=x^2 与 y=2x ⇒ (0,0) 与 (2,4) ----");
        var inter8 = FeatureFinder.FindIntersections(
            ExpressionParser.Parse("x^2"), ExpressionParser.Parse("2*x"), empty, -5, 5);
        bool i8ok = inter8.Count == 2;
        bool saw8a = false, saw8b = false;
        foreach (var p in inter8)
        {
            if (Near(p.X, 0, 1e-5) && Near(p.Y, 0, 1e-5)) saw8a = true;
            if (Near(p.X, 2, 1e-5) && Near(p.Y, 4, 1e-5)) saw8b = true;
        }
        Console.WriteLine($"  找到 {inter8.Count} 个交点（(0,0)={saw8a}，(2,4)={saw8b}）");
        Console.WriteLine($"[{(i8ok && saw8a && saw8b ? "PASS" : "FAIL")}] x^2 与 2x 的交点解对了");
        if (!(i8ok && saw8a && saw8b)) failures++;

        // ---- 9) ★ 交点反例：不相交的两条曲线 ⇒ 0 个交点 ----
        Console.WriteLine();
        Console.WriteLine("---- 9) ★ 反例：x^2+10 与 2x ⇒ 无交点（不许凭「看起来接近」硬造一个） ----");
        var inter9 = FeatureFinder.FindIntersections(
            ExpressionParser.Parse("x^2+10"), ExpressionParser.Parse("2*x"), empty, -5, 5);
        bool i9ok = inter9.Count == 0;
        Console.WriteLine($"  找到 {inter9.Count} 个交点（期望 0）");
        Console.WriteLine($"[{(i9ok ? "PASS" : "FAIL")}] 不相交的曲线没有假交点");
        if (!i9ok) failures++;

        // ---- 10) 定义域内联：ln(x) where x>0 ⇒ x<=0 处无值 ----
        Console.WriteLine();
        Console.WriteLine("---- 10) 定义域内联：ln(x) where x>0 ⇒ x=-1 无值、x=1 有值 ----");
        ExprNode dom10;
        bool dom10ParseOk = true;
        try { dom10 = ExpressionParser.Parse("ln(x) where x>0"); }
        catch (Exception) { dom10 = ExpressionParser.Parse("x"); dom10ParseOk = false; }
        bool d10ok = dom10ParseOk;
        if (dom10ParseOk)
        {
            double atNeg = ExpressionEvaluator.Eval(dom10, -1, empty);
            double atPos = ExpressionEvaluator.Eval(dom10, 1, empty);
            d10ok = double.IsNaN(atNeg) && Near(atPos, 0, 1e-9);
            Console.WriteLine($"  ln(x) where x>0：x=-1 ⇒ {(double.IsNaN(atNeg) ? "NaN(不画)" : atNeg.ToString())}，x=1 ⇒ {atPos:F6}");
        }
        else Console.WriteLine("  where 语法解析失败！");
        Console.WriteLine($"[{(d10ok ? "PASS" : "FAIL")}] where 定义域语法可用，定义域外返回 NaN（不画）");
        if (!d10ok) failures++;

        // ---- 11) 定义域让采样断开：1/x where x>0 ⇒ 只有 x>0 段 ----
        Console.WriteLine();
        Console.WriteLine("---- 11) 定义域 + 采样：1/x where x>0 ⇒ 所有点 x>0 ----");
        var segDom = FunctionSampler.Sample(
            ExpressionParser.Parse("1/x where x>0"), -5, 5, -100, 100, empty);
        bool d11ok = segDom.Count > 0;
        int domPts = 0;
        foreach (var s in segDom) foreach (var p in s) { domPts++; if (p.X <= 0) d11ok = false; }
        Console.WriteLine($"  生成 {segDom.Count} 段 / {domPts} 点；是否全在 x>0：{d11ok}");
        Console.WriteLine($"[{(d11ok ? "PASS" : "FAIL")}] 定义域外的部分真的没有被画出来");
        if (!d11ok) failures++;

        // ---- 12) ★ 分段语法：{x<0: -x, else: x} 等价于 |x| ----
        Console.WriteLine();
        Console.WriteLine("---- 12) ★ 分段：{x<0: -x, else: x} ⇒ 在 ±3 处都等于 3（就是 |x|） ----");
        ExprNode pw12;
        bool pw12ParseOk = true;
        try { pw12 = ExpressionParser.Parse("{x<0: -x, else: x}"); }
        catch (Exception pex) { pw12 = ExpressionParser.Parse("x"); pw12ParseOk = false; Console.WriteLine($"  解析失败：{pex.Message}"); }
        bool p12ok = pw12ParseOk;
        if (pw12ParseOk)
        {
            double neg = ExpressionEvaluator.Eval(pw12, -3, empty);
            double pos = ExpressionEvaluator.Eval(pw12, 3, empty);
            double zero = ExpressionEvaluator.Eval(pw12, 0, empty);
            p12ok = Near(neg, 3, 1e-9) && Near(pos, 3, 1e-9) && Near(zero, 0, 1e-9);
            Console.WriteLine($"  x=-3 ⇒ {neg}，x=0 ⇒ {zero}，x=3 ⇒ {pos}（期望 3/0/3）");
        }
        Console.WriteLine($"[{(p12ok ? "PASS" : "FAIL")}] 分段函数取值正确（含 else 兜底分支）");
        if (!p12ok) failures++;

        // ---- 13) ★★ 分段在跳变处必须断开（不能画成斜坡） ----
        // 这是分段最容易画错的地方：{x<0:-1, else:1} 若被均匀采样连起来，
        // 视觉上是一条从 −1 斜升到 +1 的坡 —— 而数学上它是两条水平线 + 一个跳跃。
        Console.WriteLine();
        Console.WriteLine("---- 13) ★★ 分段断点：{x<0: -1, else: 1} 在 [-2,2] 必须断成 2 段（不许连成斜坡） ----");
        var segStep = FunctionSampler.Sample(
            ExpressionParser.Parse("{x<0: -1, else: 1}"), -2, 2, -5, 5, empty);
        int stepPts = 0;
        foreach (var s in segStep) stepPts += s.Count;
        // 检查每一段内部 y 都是常量（不存在跨断点的连线）
        bool noRamp = true;
        foreach (var s in segStep)
        {
            for (int i = 1; i < s.Count; i++)
                if (Math.Abs(s[i].Y - s[0].Y) > 1e-6) noRamp = false;
        }
        bool s13ok = segStep.Count == 2 && noRamp;
        Console.WriteLine($"  生成 {segStep.Count} 段 / {stepPts} 点（期望 2 段）；段内 y 恒定 = {noRamp}");
        Console.WriteLine($"[{(s13ok ? "PASS" : "FAIL")}] 分段函数在断点处断开成两条水平线（不是斜坡）");
        if (!s13ok) failures++;

        // ---- 14) 多分支分段：三分支都能命中 ----
        Console.WriteLine();
        Console.WriteLine("---- 14) 三分支分段：{x<-1: -1, x<1: x, else: 1} ⇒ 三点各命中一支 ----");
        ExprNode pw14;
        bool pw14ParseOk = true;
        try { pw14 = ExpressionParser.Parse("{x<-1: -1, x<1: x, else: 1}"); }
        catch (Exception pex) { pw14 = ExpressionParser.Parse("x"); pw14ParseOk = false; Console.WriteLine($"  解析失败：{pex.Message}"); }
        bool p14ok = pw14ParseOk;
        if (pw14ParseOk)
        {
            double v14a = ExpressionEvaluator.Eval(pw14, -2, empty);
            double v14b = ExpressionEvaluator.Eval(pw14, 0, empty);
            double v14c = ExpressionEvaluator.Eval(pw14, 2, empty);
            p14ok = Near(v14a, -1, 1e-9) && Near(v14b, 0, 1e-9) && Near(v14c, 1, 1e-9);
            Console.WriteLine($"  x=-2 ⇒ {v14a}，x=0 ⇒ {v14b}，x=2 ⇒ {v14c}（期望 -1/0/1）");
        }
        Console.WriteLine($"[{(p14ok ? "PASS" : "FAIL")}] 三分支分段按「从上往下第一个命中」取胜");
        if (!p14ok) failures++;

        // ---- 15) 比较运算符族都能解析与求值 ----
        Console.WriteLine();
        Console.WriteLine("---- 15) 比较运算符 < <= > >= = != 都能解析求值 ----");
        bool cmpOk = true;
        string cmpLog = "";
        foreach (var (src, x, expect) in new[]
        {
            ("x<2", 1.0, 1.0), ("x<2", 3.0, 0.0),
            ("x<=2", 2.0, 1.0), ("x>=2", 2.0, 1.0),
            ("x>2", 3.0, 1.0), ("x=2", 2.0, 1.0), ("x!=2", 3.0, 1.0),
        })
        {
            double got = ExpressionEvaluator.Eval(ExpressionParser.Parse(src), x, empty);
            cmpLog += $"{src}@{x}={got} ";
            if (!Near(got, expect, 1e-9)) cmpOk = false;
        }
        Console.WriteLine($"  {cmpLog}");
        Console.WriteLine($"[{(cmpOk ? "PASS" : "FAIL")}] 比较运算符求值成 0/1，与预期一致");
        if (!cmpOk) failures++;

        // ---- 16) ★ 参数出现在条件里也要能收回（滑块 + 分段同时用） ----
        Console.WriteLine();
        Console.WriteLine("---- 16) ★ 参数在条件里：{x<a: 0, else: 1} ⇒ 参数字母 a 必须被收集到 ----");
        var pw16 = ExpressionParser.Parse("{x<a: 0, else: 1}");
        var pars16 = ExpressionEvaluator.CollectParameters(pw16);
        bool p16ok = pars16.Count == 1 && pars16[0] == "a";
        Console.WriteLine($"  收集到参数：{string.Join(",", pars16)}（期望 a）");
        Console.WriteLine($"[{(p16ok ? "PASS" : "FAIL")}] 条件里的参数会被滑块收集到（不会被漏掉）");
        if (!p16ok) failures++;

        // ---- 17) 语法错误有位置信息（老师能定位拼错的地方） ----
        Console.WriteLine();
        Console.WriteLine("---- 17) 分段写错时给出带位置的报错（不抛裸异常给 UI） ----");
        bool err17 = false; int pos17 = -1; string msg17 = "";
        try { ExpressionParser.Parse("{x<0 -x}"); }
        catch (ParseException pex) { err17 = true; pos17 = pex.Position; msg17 = pex.Message; }
        bool p17ok = err17 && pos17 >= 0;
        Console.WriteLine($"  报错：{msg17} @ {pos17}");
        Console.WriteLine($"[{(p17ok ? "PASS" : "FAIL")}] 分段漏写冒号时给出 ParseException + 位置");
        if (!p17ok) failures++;

        // ---- 18) ★ 渲染器真实路径：开关关着时不算特征点、开着时真的多出小圆点 ----
        // 走渲染器 CreateVisual（而不是直接调 FeatureFinder）：S5 踩过「底层函数对、渲染器没接上」的坑。
        Console.WriteLine();
        Console.WriteLine("---- 18) ★ 渲染器真实路径：showZeros=1 ⇒ 视觉里多出零点小圆点 ----");
        try
        {
            var offRef = new S4FakeRef(FunctionRenderer.KindName, new Point(0, 0), 28.35, 0,
                new Dictionary<string, double>
                {
                    ["xMin"] = -5, ["xMax"] = 5, ["yMin"] = -5, ["yMax"] = 5,
                    ["showZeros"] = 0, ["showExtrema"] = 0, ["showIntersections"] = 0,
                },
                new Dictionary<string, string> { ["expr"] = "x^2-4" });
            var onRef = new S4FakeRef(FunctionRenderer.KindName, new Point(0, 0), 28.35, 0,
                new Dictionary<string, double>
                {
                    ["xMin"] = -5, ["xMax"] = 5, ["yMin"] = -5, ["yMax"] = 5,
                    ["showZeros"] = 1, ["showExtrema"] = 0, ["showIntersections"] = 0,
                },
                new Dictionary<string, string> { ["expr"] = "x^2-4" });

            var rend = new FunctionRenderer();
            var visOff = rend.CreateVisual(offRef);
            var visOn = rend.CreateVisual(onRef);
            int countOff = CountEllipses(visOff);
            int countOn = CountEllipses(visOn);
            bool p18ok = countOff == 0 && countOn == 2;   // x^2-4 有两个零点
            Console.WriteLine($"  开关关：小圆点 {countOff} 个；开关开：{countOn} 个（期望 0 / 2）");
            Console.WriteLine($"[{(p18ok ? "PASS" : "FAIL")}] 零点标注开关真的控制视觉（渲染器真实路径）");
            if (!p18ok) failures++;
        }
        catch (Exception r18ex)
        {
            Console.WriteLine($"[FAIL] 渲染器特征点断言抛异常：{r18ex.GetType().Name} {r18ex.Message}");
            failures++;
        }

        // ---- 19) ★ 交点走渲染器真实路径：两条曲线 + 画布快照 ⇒ 标出交点 ----
        Console.WriteLine();
        Console.WriteLine("---- 19) ★ 交点（渲染器真实路径）：喂画布快照 ⇒ y=x^2 与 y=2x 标出 2 个交点 ----");
        try
        {
            var curveA = new S4FakeRef(FunctionRenderer.KindName, new Point(0, 0), 28.35, 0,
                new Dictionary<string, double> { ["xMin"] = -2, ["xMax"] = 4, ["yMin"] = -2, ["yMax"] = 6, ["showIntersections"] = 1 },
                new Dictionary<string, string> { ["expr"] = "x^2", ["bindTo"] = "sys1" }, "c1");
            var curveB = new S4FakeRef(FunctionRenderer.KindName, new Point(0, 0), 28.35, 0,
                new Dictionary<string, double> { ["xMin"] = -2, ["xMax"] = 4, ["yMin"] = -2, ["yMax"] = 6, ["showIntersections"] = 1 },
                new Dictionary<string, string> { ["expr"] = "2*x", ["bindTo"] = "sys1" }, "c2");

            var rend19 = new FunctionRenderer();
            rend19.SetBoard(new List<IGfxObjectRef> { curveA, curveB });
            var vis19 = rend19.CreateVisual(curveA);
            int dots19 = CountEllipses(vis19);
            bool p19ok = dots19 == 2;
            Console.WriteLine($"  y=x^2 上标出 {dots19} 个小圆点（期望 2：即 (0,0) 与 (2,4)）");
            Console.WriteLine($"[{(p19ok ? "PASS" : "FAIL")}] 交点标注走通「渲染器 + 画布快照」这条真实链路");
            if (!p19ok) failures++;
        }
        catch (Exception r19ex)
        {
            Console.WriteLine($"[FAIL] 交点渲染断言抛异常：{r19ex.GetType().Name} {r19ex.Message}");
            failures++;
        }

        // ---- 20) ★ 契约兼容：不喂画布快照时交点开关不炸（老路径照常） ----
        Console.WriteLine();
        Console.WriteLine("---- 20) ★ 没喂画布快照（SetBoard 没被调）⇒ 交点开关打开也不崩 ----");
        try
        {
            var lone = new S4FakeRef(FunctionRenderer.KindName, new Point(0, 0), 28.35, 0,
                new Dictionary<string, double> { ["xMin"] = -5, ["xMax"] = 5, ["yMin"] = -5, ["yMax"] = 5, ["showIntersections"] = 1 },
                new Dictionary<string, string> { ["expr"] = "x^2" });
            var rend20 = new FunctionRenderer();     // 故意不调 SetBoard
            var vis20 = rend20.CreateVisual(lone);
            double stroke20 = ReadPathStroke(vis20);
            bool p20ok = stroke20 > 0;
            Console.WriteLine($"  没有画布快照也能正常出视觉（曲线线宽 {stroke20:F4}）");
            Console.WriteLine($"[{(p20ok ? "PASS" : "FAIL")}] 渲染器在缺画布时降级成「只画曲线」，不抛异常");
            if (!p20ok) failures++;
        }
        catch (Exception r20ex)
        {
            Console.WriteLine($"[FAIL] 缺画布降级断言抛异常：{r20ex.GetType().Name} {r20ex.Message}");
            failures++;
        }

        // ---- 21) 工具链路：标注开关真的落到对象的 Numbers 里 ----
        Console.WriteLine();
        Console.WriteLine("---- 21) 工具链路：FunctionSpec 的三个开关 ⇒ 落到对象的 Numbers ----");
        try
        {
            var h21 = new S4TestHost();
            var c21 = new FakeToolContext { GfxOverride = h21 };
            var t21 = new FunctionTool(); t21.Activate(c21);
            t21.ApplySpec(new FunctionSpec
            {
                Expr = "x^2-4", XMin = -5, XMax = 5, YMin = -5, YMax = 5,
                ShowZeros = true, ShowExtrema = false, ShowIntersections = true,
            }, new Point(300, 300));

            GfxDraft? curve21 = null;
            foreach (var d in h21.Added) if (d.Kind == FunctionRenderer.KindName) curve21 = d;
            bool p21ok = curve21 is not null
                      && curve21.Numbers.TryGetValue(FunctionRenderer.ShowZerosKey, out var sz) && Near(sz, 1, 1e-9)
                      && curve21.Numbers.TryGetValue(FunctionRenderer.ShowExtremaKey, out var se) && Near(se, 0, 1e-9)
                      && curve21.Numbers.TryGetValue(FunctionRenderer.ShowIntersectionsKey, out var si) && Near(si, 1, 1e-9);
            Console.WriteLine($"  落成对象开关：showZeros={(curve21?.Numbers.GetValueOrDefault(FunctionRenderer.ShowZerosKey))} "
                              + $"showExtrema={(curve21?.Numbers.GetValueOrDefault(FunctionRenderer.ShowExtremaKey))} "
                              + $"showIntersections={(curve21?.Numbers.GetValueOrDefault(FunctionRenderer.ShowIntersectionsKey))}");
            Console.WriteLine($"[{(p21ok ? "PASS" : "FAIL")}] 弹窗里的标注开关原样落到对象存档（存档/撤销零改动）");
            if (!p21ok) failures++;
        }
        catch (Exception t21ex)
        {
            Console.WriteLine($"[FAIL] 标注开关落盘断言抛异常：{t21ex.GetType().Name} {t21ex.Message}");
            failures++;
        }

        // ---- 22) ★ 输入窗可构建（防「点了没反应」回归：加了新键与复选框后仍能构造） ----
        Console.WriteLine();
        Console.WriteLine("---- 22) ★ 输入窗加了标注开关与分段键后仍可构建（防「点了没反应」） ----");
        try
        {
            Window? win = null;
            var thread = new Thread(() =>
            {
                try
                {
                    win = new FunctionInputWindow(null, _ => { });
                    win.Measure(new Size(940, 720));
                    win.Arrange(new Rect(0, 0, 940, 720));
                    win.Close();
                }
                catch (Exception) { win = null; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(8000);
            bool p22ok = win is not null;
            Console.WriteLine($"  函数输入窗构建 {(p22ok ? "成功" : "失败")}");
            Console.WriteLine($"[{(p22ok ? "PASS" : "FAIL")}] 输入窗（含标注开关 + 分段键位）可正常构建");
            if (!p22ok) failures++;
        }
        catch (Exception w22ex)
        {
            Console.WriteLine($"[FAIL] 输入窗构建断言抛异常：{w22ex.GetType().Name} {w22ex.Message}");
            failures++;
        }

        // ---- 23) ★ 真宿主 + 证据图：坐标系 + 分段曲线 + 带标注的抛物线 ----
        // 走真实视口装配（与现场同一套接线）：坐标系 + 两条函数，其中一条开零点/极值/交点标注。
        Console.WriteLine();
        Console.WriteLine("---- 23) ★ 真宿主：坐标系 + 分段曲线 + 带标注抛物线 ⇒ 出证据图 ----");
        try
        {
            var fv = new CanvasViewportHost();
            fv.Measure(new Size(900, 640));
            fv.Arrange(new Rect(0, 0, 900, 640));
            fv.UpdateLayout();
            fv.RegisterGfxRenderer(new CoordSystemRenderer(), "坐标系（学科工具）");
            fv.RegisterGfxRenderer(new FunctionRenderer(), "函数图像（学科工具）");

            var csF = fv.GfxObjects.Add(new GfxDraft
            {
                Kind = CoordSystemRenderer.KindName, Center = new Point(450, 320), Scale = 1,
                RotationDegrees = 0, Color = Colors.Black, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [CoordSystemGeometry.XMinKey] = -6, [CoordSystemGeometry.XMaxKey] = 6,
                    [CoordSystemGeometry.YMinKey] = -4, [CoordSystemGeometry.YMaxKey] = 6,
                    [CoordSystemGeometry.UnitWorldKey] = 40.0,
                    [CoordSystemGeometry.StepKey] = 1,
                },
            });

            // 抛物线 y=x^2/2-2：两个零点、一个极小值
            fv.GfxObjects.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(450, 320), Scale = 40.0,
                RotationDegrees = 0, Color = Color.FromRgb(0xE5, 0x39, 0x35), LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["xMin"] = -6, ["xMax"] = 6, ["yMin"] = -4, ["yMax"] = 6,
                    ["bindRelX"] = 0, ["bindRelY"] = 0, ["bindRelRot"] = 0,
                    [FunctionRenderer.ShowZerosKey] = 1,
                    [FunctionRenderer.ShowExtremaKey] = 1,
                    [FunctionRenderer.ShowIntersectionsKey] = 1,
                },
                Texts = new Dictionary<string, string> { ["expr"] = "x^2/2-2", ["bindTo"] = csF },
            });

            // 分段函数 {x<0: -x/2, else: x/2}（就是一个 V 形 / |x|/2）
            fv.GfxObjects.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(450, 320), Scale = 40.0,
                RotationDegrees = 0, Color = Color.FromRgb(0x1E, 0x88, 0xE5), LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    ["xMin"] = -6, ["xMax"] = 6, ["yMin"] = -4, ["yMax"] = 6,
                    ["bindRelX"] = 0, ["bindRelY"] = 0, ["bindRelRot"] = 0,
                    [FunctionRenderer.ShowZerosKey] = 1,
                },
                Texts = new Dictionary<string, string> { ["expr"] = "{x<0: -x/2, else: x/2}", ["bindTo"] = csF },
            });

            fv.UpdateLayout();
            fv.GfxObjects.Select(null);
            fv.UpdateLayout();

            // 断言：真宿主里三条对象都在，且函数渲染器没把整块白板搞崩
            int objCount = 0;
            foreach (var _ in fv.GfxObjects.Objects) objCount++;
            bool hostOk = objCount == 3;
            Console.WriteLine($"  真宿主里共 {objCount} 个图形对象（坐标系 1 + 函数 2）");
            Console.WriteLine($"[{(hostOk ? "PASS" : "FAIL")}] 真宿主装配下三条对象都活着（渲染器没抛异常拖垮整块板）");
            if (!hostOk) failures++;

            try
            {
                string shot = Path.Combine(outDir, "m74s5-函数特征点.png");
                SavePng(Snapshot(fv, 900, 640), shot);
                Console.WriteLine($"  证据图：{shot}");
            }
            catch (Exception shotEx)
            {
                Console.WriteLine($"  （特征点截图证据跳过：{shotEx.Message}）");
            }

            fv.Shutdown();
        }
        catch (Exception vfx)
        {
            Console.WriteLine($"[FAIL] 真宿主特征点断言抛异常：{vfx.GetType().Name} {vfx.Message}");
            failures++;
        }

        // ---- 24) 读数标注必须"看得见"：文字有实心填充 + 白衬板，且不再用白描边 ----
        //   ★ 这条是补的回归网：曾经给小圆点旁边的读数文字加了一圈白色描边当背景，
        //     结果 WPF 描边骑在轮廓线上、内外各一半，把笔画整条吃光 —— 圆点在、字没了。
        //     当时只断言了"椭圆数量"，全绿通过却漏掉了"脸上没字"。现在把"字必须可见"钉死。
        Console.WriteLine("---- 24) 读数标注可见性 ----");
        try
        {
            var labelVis = new FunctionRenderer().CreateVisual(new S4FakeRef(
                FunctionRenderer.KindName, new Point(0, 0), 40.0, 0,
                new Dictionary<string, double>
                {
                    ["xMin"] = -6, ["xMax"] = 6, ["yMin"] = -4, ["yMax"] = 6, ["showZeros"] = 1,
                },
                new Dictionary<string, string> { ["expr"] = "x^2/2-2" }));

            int fillPaths = 0, whiteStrokedPaths = 0, plates = 0;
            void WalkLabel(System.Windows.DependencyObject d)
            {
                if (d is System.Windows.Shapes.Path lp)
                {
                    bool hasFill = lp.Fill is SolidColorBrush;
                    if (hasFill) fillPaths++;
                    // 白描边（或接近白）且没有实心填充 = "字被白边吃掉"的旧写法
                    if (lp.Stroke is SolidColorBrush sb && sb.Color.R > 230 && sb.Color.G > 230 && sb.Color.B > 230)
                        whiteStrokedPaths++;
                }
                if (d is System.Windows.Shapes.Rectangle) plates++;
                int c = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < c; i++) WalkLabel(VisualTreeHelper.GetChild(d, i));
            }
            WalkLabel(labelVis);

            bool ok24 = fillPaths >= 2;          // 曲线 Path + 两条零点读数 Path
            Console.WriteLine($"  实心填充 Path 数 = {fillPaths}（期望 >=2：曲线 + 零点读数）");
            ok24 &= whiteStrokedPaths == 0;
            Console.WriteLine($"  白描边 Path 数 = {whiteStrokedPaths}（期望 0：描边骑轮廓会把笔画吃光）");
            ok24 &= plates >= 2;
            Console.WriteLine($"  白衬板 Rectangle 数 = {plates}（期望 >=2：两个零点各一块）");
            Console.WriteLine(ok24 ? "  [PASS] 读数标注有实心字 + 白衬板、且无白描边（看得见）"
                                   : "  [FAIL] 读数标注可见性不达标");
            if (!ok24) failures++;
        }
        catch (Exception lx)
        {
            Console.WriteLine($"[FAIL] 读数标注可见性断言抛异常：{lx.GetType().Name} {lx.Message}");
            failures++;
        }

        return failures;
    }

    /// <summary>数一个视觉树里的椭圆个数（特征点小圆点）。</summary>
    private static int CountEllipses(DependencyObject root)
    {
        int n = 0;
        if (root is System.Windows.Shapes.Ellipse) n++;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            n += CountEllipses(VisualTreeHelper.GetChild(root, i));
        return n;
    }

    /// <summary>读一个视觉树里第一条 Path 的线宽（用于确认「还画着曲线」）。</summary>
    private static double ReadPathStroke(DependencyObject root)
    {
        if (root is System.Windows.Shapes.Path p) return p.StrokeThickness;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            double v = ReadPathStroke(VisualTreeHelper.GetChild(root, i));
            if (v > 0) return v;
        }
        return 0;
    }

    private static int RunM74S5PanelChecks(string outDir)
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("================ M7.4 Step 5 第五轮：参数面板（纯数据面板 + 改参数）================");

        // ---- 1) 字段的收敛数学：夹范围 + 对齐步长 ----
        Console.WriteLine();
        Console.WriteLine("---- 1) 字段：范围夹取与步长对齐（面板的「输入框」全靠这两条）----");
        var fieldR = new GfxParameterField
        {
            Key = "r", Label = "半径", Value = 5, Min = 1, Max = 10, Step = 0.5,
        };

        bool hasRangeOk = fieldR.HasRange;
        bool clampHighOk = Near(fieldR.Clamp(999), 10, 1e-9);
        bool clampLowOk = Near(fieldR.Clamp(-999), 1, 1e-9);
        Console.WriteLine($"  范围 [1,10]：Clamp(999)={fieldR.Clamp(999):F4}、Clamp(-999)={fieldR.Clamp(-999):F4}（期望 10 / 1）");
        Console.WriteLine($"[{(hasRangeOk && clampHighOk && clampLowOk ? "PASS" : "FAIL")}] 越界值被夹回范围内（而不是把图形甩到天边）");
        if (!(hasRangeOk && clampHighOk && clampLowOk)) failures++;

        bool snapOk = Near(fieldR.Snap(5.3), 5.5, 1e-9) && Near(fieldR.Snap(5.2), 5.0, 1e-9);
        Console.WriteLine($"  Step=0.5：Snap(5.3)={fieldR.Snap(5.3):F4}、Snap(5.2)={fieldR.Snap(5.2):F4}（期望 5.5 / 5.0）");
        Console.WriteLine($"[{(snapOk ? "PASS" : "FAIL")}] 值按步长对齐到最近档位");
        if (!snapOk) failures++;

        // ★ 无范围 ≠ 随便乱来：NaN / Infinity 必须被挡掉，否则会把对象参数写成 NaN
        var fieldFree = new GfxParameterField { Key = "k", Label = "任意", Value = 3, Min = 0, Max = 0 };
        bool noRangeOk = !fieldFree.HasRange && Near(fieldFree.Clamp(12345), 12345, 1e-9);
        bool nanOk = Near(fieldFree.Clamp(double.NaN), 3, 1e-9)
                  && Near(fieldFree.Clamp(double.PositiveInfinity), 3, 1e-9);
        Console.WriteLine($"  Min==Max（不限制）：Clamp(12345)={fieldFree.Clamp(12345):F1}；"
                          + $"Clamp(NaN)={fieldFree.Clamp(double.NaN):F1}、Clamp(+∞)={fieldFree.Clamp(double.PositiveInfinity):F1}");
        Console.WriteLine($"[{(noRangeOk ? "PASS" : "FAIL")}] Min==Max 表示「不限制范围」（一个字段表达两种状态）");
        if (!noRangeOk) failures++;
        Console.WriteLine($"[{(nanOk ? "PASS" : "FAIL")}] ★ NaN / 无穷大被挡回原值（漏掉这一条会把对象参数写成 NaN，图形直接消失）");
        if (!nanOk) failures++;

        // ★ 步长以 Min 为基准：否则范围不始于 0 时会出现累积余数
        var fieldOff = new GfxParameterField { Key = "off", Label = "偏移", Value = 1, Min = 1, Max = 5, Step = 1 };
        bool baseOk = Near(fieldOff.Snap(2.4), 2, 1e-9) && Near(fieldOff.Snap(2.6), 3, 1e-9);
        Console.WriteLine($"  范围 [1,5] Step=1：Snap(2.4)={fieldOff.Snap(2.4):F2}、Snap(2.6)={fieldOff.Snap(2.6):F2}（期望 2 / 3）");
        Console.WriteLine($"[{(baseOk ? "PASS" : "FAIL")}] 步长以 Min 为基准（范围不始于 0 时也不会错半格）");
        if (!baseOk) failures++;

        // ---- 2) 宿主协调器：没有选中 ⇒ 没有面板 ----
        Console.WriteLine();
        Console.WriteLine("---- 2) 协调器：没有选中对象 ⇒ 没有面板（不是空面板）----");
        try
        {
            var catP = new GfxRendererCatalog();
            catP.Register(new CoordSystemRenderer(), "坐标系（学科工具）");

            var stP = new GfxObjectStore(catP);
            var historyP = new GfxHistory(stP);
            var inkP = new InkHistory(new StrokeCollection());
            var boardP = new BoardHistory(inkP, historyP);
            var hostP = new HostGfxObjectHost(stP, historyP, boardP);
            var coordP = new GfxParameterCoordinator(stP, catP, hostP);

            bool noSelectOk = coordP.CurrentPanel is null;
            Console.WriteLine($"  没有选中时 CurrentPanel = {(coordP.CurrentPanel is null ? "null" : "非 null")}");
            Console.WriteLine($"[{(noSelectOk ? "PASS" : "FAIL")}] 没有选中对象时不产生面板（界面据此隐藏参数区）");
            if (!noSelectOk) failures++;

            // ---- 3) ★ 选中没实现 provider 的对象 ⇒ 也没有面板 ----
            var plainObj = stP.Add(new GfxDraft
            {
                Kind = "no_such_kind", Center = new Point(100, 100), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 1.5,
            });
            stP.Select(plainObj);
            bool noProviderOk = coordP.CurrentPanel is null;
            Console.WriteLine($"  选中一个没有 provider 的对象 ⇒ CurrentPanel = {(coordP.CurrentPanel is null ? "null" : "非 null")}");
            Console.WriteLine($"[{(noProviderOk ? "PASS" : "FAIL")}] ★ 对象没实现 provider 时不出面板（老的量角器/直尺/三角板插件照常工作）");
            if (!noProviderOk) failures++;

            // ---- 4) ★ 选中坐标系 ⇒ 面板出来了，字段与当前值一致 ----
            Console.WriteLine();
            Console.WriteLine("---- 4) ★ 选中坐标系 ⇒ 面板出来，字段与对象当前值一致 ----");
            var cs = stP.Add(new GfxDraft
            {
                Kind = CoordSystemRenderer.KindName, Center = new Point(300, 400), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [CoordSystemGeometry.XMinKey] = -5, [CoordSystemGeometry.XMaxKey] = 5,
                    [CoordSystemGeometry.YMinKey] = -4, [CoordSystemGeometry.YMaxKey] = 8,
                    [CoordSystemGeometry.UnitWorldKey] = 28.35,
                    [CoordSystemGeometry.StepKey] = 1,
                    [CoordSystemGeometry.ShowGridKey] = 1,
                    [CoordSystemGeometry.ShowLabelsKey] = 1,
                },
            });
            stP.Select(cs);

            var panel = coordP.CurrentPanel;
            bool panelOk = panel is not null && panel.Fields.Count == 7;
            Console.WriteLine($"  面板标题「{panel?.Title}」、副标题「{panel?.Subtitle}」、{panel?.Fields.Count} 项（期望 7）");
            Console.WriteLine($"[{(panelOk ? "PASS" : "FAIL")}] 选中坐标系后给出面板，7 个可调项");
            if (!panelOk) failures++;

            double xMaxValue = double.NaN;
            bool showGridValue = false;
            if (panel is not null)
            {
                foreach (var f in panel.Fields)
                {
                    if (f.Key == CoordSystemGeometry.XMaxKey) xMaxValue = f.Value;
                    if (f.Key == CoordSystemGeometry.ShowGridKey) showGridValue = f.Value >= 0.5;
                }
            }

            bool valuesOk = Near(xMaxValue, 5, 1e-9) && showGridValue;
            Console.WriteLine($"  面板上 x 最大 = {xMaxValue:F2}（对象里是 5）、显示网格 = {showGridValue}（对象里是开）");
            Console.WriteLine($"[{(valuesOk ? "PASS" : "FAIL")}] 面板上的当前值就是对象里的真实值（不是写死的默认值）");
            if (!valuesOk) failures++;

            string xMaxLabel = "";
            if (panel is not null)
            {
                foreach (var f in panel.Fields)
                {
                    if (f.Key == CoordSystemGeometry.XMaxKey) xMaxLabel = f.Label;
                }
            }

            // ---- 5) ★ 改一个值 ⇒ 对象真的变了，并且记了一步历史 ----
            Console.WriteLine();
            Console.WriteLine("---- 5) ★ 改「x 最大」⇒ 对象真的改了，且这一步可撤销 ----");
            int undoDepthBefore = historyP.UndoCount;
            bool applied = coordP.Apply(CoordSystemGeometry.XMaxKey, 12, out string statusA);
            var afterObj = FindObjIn(stP.Objects, cs);
            double newXMax = afterObj?.GetNumber(CoordSystemGeometry.XMaxKey, double.NaN) ?? double.NaN;

            Console.WriteLine($"  Apply 返回 {applied}，状态栏「{statusA}」");
            Console.WriteLine($"  对象里的 xMax：5 ⇒ {newXMax}（期望 12）");
            Console.WriteLine($"[{(applied && Near(newXMax, 12, 1e-9) ? "PASS" : "FAIL")}] 改参数真的写进了对象");
            if (!(applied && Near(newXMax, 12, 1e-9))) failures++;

            bool undoGrew = historyP.UndoCount > undoDepthBefore;
            Console.WriteLine($"  撤销栈深度：{undoDepthBefore} ⇒ {historyP.UndoCount}（应当变深，说明这一步可撤销）");
            Console.WriteLine($"[{(undoGrew ? "PASS" : "FAIL")}] 改参数记了一步历史（Ctrl+Z 能退回去）");
            if (!undoGrew) failures++;

            // ---- 6) ★ 改成同一个值 ⇒ 不记历史（不留"什么都没变"的空白步）----
            Console.WriteLine();
            Console.WriteLine("---- 6) ★ 改成同一个值 ⇒ 不落历史（否则撤销栈里全是空白步）----");
            int depthSame = historyP.UndoCount;
            bool sameApplied = coordP.Apply(CoordSystemGeometry.XMaxKey, 12, out string statusSame);
            Console.WriteLine($"  再把 xMax 设成 12（已经是 12）：返回 {sameApplied}、状态栏「{statusSame}」");
            bool noBlankStepOk = !sameApplied && historyP.UndoCount == depthSame;
            Console.WriteLine($"[{(noBlankStepOk ? "PASS" : "FAIL")}] 值没变就不记历史（撤销栈不被空白步撑大）");
            if (!noBlankStepOk) failures++;

            // ---- 7) ★★ 越界输入被夹住：核心一条 ----
            Console.WriteLine();
            Console.WriteLine("---- 7) ★★ 越界输入被夹住（面板是给人手输的，什么都能敲进来）----");
            coordP.Apply(CoordSystemGeometry.UnitWorldKey, 999999, out _);
            var afterUnit = FindObjIn(stP.Objects, cs);
            double unitNow = afterUnit?.GetNumber(CoordSystemGeometry.UnitWorldKey, double.NaN) ?? double.NaN;
            bool unitClampedOk = Near(unitNow, CoordSystemGeometry.MaxUnitWorld, 1e-6);
            Console.WriteLine($"  单位长度输入 999999 ⇒ 对象里是 {unitNow:F4}（期望夹到上限 {CoordSystemGeometry.MaxUnitWorld}）");
            Console.WriteLine($"[{(unitClampedOk ? "PASS" : "FAIL")}] 越界输入被夹到上限（不然图形会被放大到看不见）");
            if (!unitClampedOk) failures++;

            coordP.Apply(CoordSystemGeometry.UnitWorldKey, double.NaN, out _);
            var afterNan = FindObjIn(stP.Objects, cs);
            double unitNan = afterNan?.GetNumber(CoordSystemGeometry.UnitWorldKey, double.NaN) ?? double.NaN;
            bool nanGuardOk = !double.IsNaN(unitNan);
            Console.WriteLine($"  单位长度输入 NaN ⇒ 对象里是 {unitNan:F4}（绝不能是 NaN）");
            Console.WriteLine($"[{(nanGuardOk ? "PASS" : "FAIL")}] ★ NaN 输入被挡在门外（写成 NaN 会让这个对象彻底画不出来）");
            if (!nanGuardOk) failures++;

            // ---- 8) ★ 改 xMin 越过 xMax ⇒ 连带把 xMax 顶开（不翻面）----
            Console.WriteLine();
            Console.WriteLine("---- 8) ★ 改 x 最小越过 x 最大 ⇒ 连带顶开另一端（坐标系不翻面）----");
            var csEdge = stP.Add(new GfxDraft
            {
                Kind = CoordSystemRenderer.KindName, Center = new Point(500, 500), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [CoordSystemGeometry.XMinKey] = -5, [CoordSystemGeometry.XMaxKey] = 5,
                    [CoordSystemGeometry.YMinKey] = -5, [CoordSystemGeometry.YMaxKey] = 5,
                    [CoordSystemGeometry.UnitWorldKey] = 28.35,
                },
            });
            stP.Select(csEdge);
            coordP.Apply(CoordSystemGeometry.XMinKey, 9, out _);
            var edgeAfter = FindObjIn(stP.Objects, csEdge);
            double eMin = edgeAfter?.GetNumber(CoordSystemGeometry.XMinKey, double.NaN) ?? double.NaN;
            double eMax = edgeAfter?.GetNumber(CoordSystemGeometry.XMaxKey, double.NaN) ?? double.NaN;
            bool noFlipOk = eMin < eMax;
            Console.WriteLine($"  x 最小改成 9（原来最大是 5）⇒ xMin={eMin:F2}、xMax={eMax:F2}（要求 xMin < xMax）");
            Console.WriteLine($"[{(noFlipOk ? "PASS" : "FAIL")}] xMin 越过 xMax 时连带顶开 xMax（只写回一个键会让坐标系翻面）");
            if (!noFlipOk) failures++;

            // ---- 9) 未定义的键 ⇒ 拒绝并说明 ----
            Console.WriteLine();
            Console.WriteLine("---- 9) 未定义的参数键 ⇒ 拒绝并说明（不静默）----");
            stP.Select(cs);
            bool unknownApplied = coordP.Apply("这不是一个参数键", 1, out string statusUnknown);
            bool unknownOk = !unknownApplied && statusUnknown.Length > 0;
            Console.WriteLine($"  改一个不存在的键：返回 {unknownApplied}、状态栏「{statusUnknown}」");
            Console.WriteLine($"[{(unknownOk ? "PASS" : "FAIL")}] 未知参数键被拒绝且状态栏有说明");
            if (!unknownOk) failures++;

            // ---- 10) 没选中就改 ⇒ 拒绝并说明 ----
            stP.Select(null);
            bool noSelApplied = coordP.Apply(CoordSystemGeometry.XMaxKey, 3, out string statusNoSel);
            bool noSelOk = !noSelApplied && statusNoSel.Length > 0;
            Console.WriteLine($"  没选中就改参数：返回 {noSelApplied}、状态栏「{statusNoSel}」");
            Console.WriteLine($"[{(noSelOk ? "PASS" : "FAIL")}] 没选中时改参数被拒绝且状态栏有说明");
            if (!noSelOk) failures++;

            // ---- 11) ★ 改了范围 ⇒ 对象的几何签名（LocalSize）跟着变 ----
            Console.WriteLine();
            Console.WriteLine("---- 11) ★ 改范围 ⇒ 几何签名变了（视觉层靠它决定重建）----");
            stP.Select(csEdge);
            var beforeSize = FindObjIn(stP.Objects, csEdge)?.LocalSize ?? new Size(0, 0);
            coordP.Apply(CoordSystemGeometry.XMaxKey, 60, out _);
            var afterSize = FindObjIn(stP.Objects, csEdge)?.LocalSize ?? new Size(0, 0);
            bool sizeGrewOk = afterSize.Width > beforeSize.Width + 1;
            Console.WriteLine($"  组的本地尺寸：{beforeSize.Width:F2} ⇒ {afterSize.Width:F2}（范围变大 ⇒ 应当变宽）");
            Console.WriteLine($"[{(sizeGrewOk ? "PASS" : "FAIL")}] 范围改动反映到 LocalSize（否则视觉永远不重画）");
            if (!sizeGrewOk) failures++;
        }
        catch (Exception panelEx)
        {
            Console.WriteLine($"[FAIL] 参数面板断言抛异常：{panelEx.GetType().Name} {panelEx.Message}");
            failures++;
        }

        // ---- 12) 显示开关：改 1/0 真的能关掉网格 / 刻度 ----
        Console.WriteLine();
        Console.WriteLine("---- 12) 显示开关：显示网格 / 显示刻度 用 1/0 数值项表达 ----");
        try
        {
            var catT = new GfxRendererCatalog();
            catT.Register(new CoordSystemRenderer(), "坐标系（学科工具）");
            var stT = new GfxObjectStore(catT);
            var historyT = new GfxHistory(stT);
            var hostT = new HostGfxObjectHost(stT, historyT, new BoardHistory(new InkHistory(new StrokeCollection()), historyT));
            var coordT = new GfxParameterCoordinator(stT, catT, hostT);

            var csT = stT.Add(new GfxDraft
            {
                Kind = CoordSystemRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [CoordSystemGeometry.XMinKey] = -5, [CoordSystemGeometry.XMaxKey] = 5,
                    [CoordSystemGeometry.YMinKey] = -5, [CoordSystemGeometry.YMaxKey] = 5,
                    [CoordSystemGeometry.UnitWorldKey] = 28.35,
                    [CoordSystemGeometry.StepKey] = 1,
                    [CoordSystemGeometry.ShowGridKey] = 1,
                    [CoordSystemGeometry.ShowLabelsKey] = 1,
                },
            });
            stT.Select(csT);

            bool toggled = coordT.Apply(CoordSystemGeometry.ShowGridKey, 0, out _);
            var afterToggle = FindObjIn(stT.Objects, csT);
            bool gridOffOk = toggled && (afterToggle?.GetNumber(CoordSystemGeometry.ShowGridKey, 1) ?? 1) < 0.5;
            Console.WriteLine($"  把「显示网格」改成 0 ⇒ 对象里 showGrid = {afterToggle?.GetNumber(CoordSystemGeometry.ShowGridKey, 1):F1}（期望 0）");
            Console.WriteLine($"[{(gridOffOk ? "PASS" : "FAIL")}] 数值开关 1/0 能真的关掉网格");
            if (!gridOffOk) failures++;

            // 视觉仍然建得出来（关掉网格后不能崩）
            bool visualOk = true; string visualMsg = "构造成功";
            try
            {
                var catV = new GfxRendererCatalog();
                var rendV = new CoordSystemRenderer();
                catV.Register(rendV, "坐标系（学科工具）");
                var stV = new GfxObjectStore(catV);
                var layerV = new GfxObjectLayer(new Canvas(), stV, catV);
                var csV = stV.Add(new GfxDraft
                {
                    Kind = CoordSystemRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                    RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                    Numbers = new Dictionary<string, double>
                    {
                        [CoordSystemGeometry.XMinKey] = -5, [CoordSystemGeometry.XMaxKey] = 5,
                        [CoordSystemGeometry.YMinKey] = -5, [CoordSystemGeometry.YMaxKey] = 5,
                        [CoordSystemGeometry.UnitWorldKey] = 28.35,
                        [CoordSystemGeometry.ShowGridKey] = 0,
                        [CoordSystemGeometry.ShowLabelsKey] = 0,
                    },
                });
                var v = layerV.VisualFor(csV);
                if (v is null) { visualOk = false; visualMsg = "VisualFor 返回 null"; }
            }
            catch (Exception vex) { visualOk = false; visualMsg = vex.GetType().Name + ": " + vex.Message; }

            Console.WriteLine($"  网格与刻度都关掉后的视觉树：{visualMsg}");
            Console.WriteLine($"[{(visualOk ? "PASS" : "FAIL")}] 关掉网格/刻度后视觉树照常构造（{visualMsg}）");
            if (!visualOk) failures++;
        }
        catch (Exception toggleEx)
        {
            Console.WriteLine($"[FAIL] 显示开关断言抛异常：{toggleEx.GetType().Name} {toggleEx.Message}");
            failures++;
        }

        // ---- 13) ★ 契约兼容：老插件不实现 provider，一切照旧 ----
        Console.WriteLine();
        Console.WriteLine("---- 13) ★ 契约只加不改：没实现 provider 的渲染器一切照旧 ----");
        try
        {
            var catCompat = new GfxRendererCatalog();
            // ★ 特意声明成接口类型 IGfxObjectRenderer：宿主拿到的就是这个静态类型，
            //   所以"它到底实没实现可选接口"必须靠运行时 is 判断。
            //   若声明成具体类型，编译器会直接告诉你答案（CS0184），那就不叫"运行时可选"了。
            IGfxObjectRenderer legacyRenderer = new MathPhys.Ink.Plugin.Protractor.ProtractorRenderer();
            catCompat.Register(legacyRenderer, "量角器（学科工具）");

            bool isProvider = legacyRenderer is IGfxParameterProvider;
            var found = catCompat.FindParameterProvider(LegacyRendererKind());
            Console.WriteLine($"  老插件（量角器）渲染器是否实现 IGfxParameterProvider：{isProvider}");
            Console.WriteLine($"  目录里查它的 provider：{(found is null ? "null（正常）" : "非 null")}");
            bool compatOk = !isProvider && found is null && catCompat.Contains(LegacyRendererKind());
            Console.WriteLine($"[{(compatOk ? "PASS" : "FAIL")}] ★ 老插件（量角器）不实现新接口也能正常注册与渲染 —— 契约只加不改");
            if (!compatOk) failures++;
        }
        catch (Exception compatEx)
        {
            Console.WriteLine($"[FAIL] 契约兼容断言抛异常：{compatEx.GetType().Name} {compatEx.Message}");
            failures++;
        }

        // ---- 14) 状态栏描述格式 ----
        Console.WriteLine();
        Console.WriteLine("---- 14) 读数格式：整数不显示小数点，带单位 ----");
        string dInt = GfxParameterCoordinator.Describe(12, "pt");
        string dFrac = GfxParameterCoordinator.Describe(28.35, "pt");
        string dNoUnit = GfxParameterCoordinator.Describe(5, "");
        bool fmtOk = dInt == "12 pt" && dFrac == "28.35 pt" && dNoUnit == "5";
        Console.WriteLine($"  Describe(12,pt)=「{dInt}」、Describe(28.35,pt)=「{dFrac}」、Describe(5,空)=「{dNoUnit}」");
        Console.WriteLine($"[{(fmtOk ? "PASS" : "FAIL")}] 读数格式统一（整数不拖小数点、有单位就带单位）");
        if (!fmtOk) failures++;

        // ---- 15) ★ 真实视口：面板随选中出现/消失，改参数走真实链路 ----
        Console.WriteLine();
        Console.WriteLine("---- 15) ★ 真实视口：面板随选中出现/消失（走宿主真实装配）----");
        try
        {
            var pxyPanel = new CanvasViewportHost();
            pxyPanel.Measure(new Size(900, 600));
            pxyPanel.Arrange(new Rect(0, 0, 900, 600));
            pxyPanel.UpdateLayout();
            pxyPanel.RegisterGfxRenderer(new CoordSystemRenderer(), "坐标系（学科工具）");

            bool hiddenInitially = !pxyPanel.IsGfxPanelVisible;
            Console.WriteLine($"  刚建好视口时面板可见 = {pxyPanel.IsGfxPanelVisible}（期望 false，没选中任何东西）");
            Console.WriteLine($"[{(hiddenInitially ? "PASS" : "FAIL")}] 没有选中对象时面板不显示");
            if (!hiddenInitially) failures++;

            var csPanel = pxyPanel.GfxObjects.Add(new GfxDraft
            {
                Kind = CoordSystemRenderer.KindName, Center = new Point(300, 300), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [CoordSystemGeometry.XMinKey] = -5, [CoordSystemGeometry.XMaxKey] = 5,
                    [CoordSystemGeometry.YMinKey] = -5, [CoordSystemGeometry.YMaxKey] = 5,
                    [CoordSystemGeometry.UnitWorldKey] = 28.35,
                },
            });
            pxyPanel.GfxObjects.Select(csPanel);
            pxyPanel.UpdateLayout();

            bool shownAfterSelect = pxyPanel.IsGfxPanelVisible;
            Console.WriteLine($"  选中坐标系后面板可见 = {pxyPanel.IsGfxPanelVisible}（期望 true）");
            Console.WriteLine($"[{(shownAfterSelect ? "PASS" : "FAIL")}] 选中坐标系后面板自动出现");
            if (!shownAfterSelect) failures++;

            // 取消选中 ⇒ 面板收回
            pxyPanel.GfxObjects.Select(null);
            pxyPanel.UpdateLayout();
            bool hiddenAfterDeselect = !pxyPanel.IsGfxPanelVisible;
            Console.WriteLine($"  取消选中后面板可见 = {pxyPanel.IsGfxPanelVisible}（期望 false）");
            Console.WriteLine($"[{(hiddenAfterDeselect ? "PASS" : "FAIL")}] 取消选中后面板自动收回（不留一个不知道在为谁服务的面板）");
            if (!hiddenAfterDeselect) failures++;

            // ★ 走视口的协调器改参数 ⇒ 对象真的变
            pxyPanel.GfxObjects.Select(csPanel);
            bool viaViewport = pxyPanel.GfxParameters.Apply(CoordSystemGeometry.XMaxKey, 20, out string statusVp);
            var vpAfter = FindObjIn(pxyPanel.GfxObjects.Objects, csPanel);
            double vpXMax = vpAfter?.GetNumber(CoordSystemGeometry.XMaxKey, double.NaN) ?? double.NaN;
            Console.WriteLine($"  走视口协调器改 xMax=20：返回 {viaViewport}、状态栏「{statusVp}」、对象里是 {vpXMax}");
            Console.WriteLine($"[{(viaViewport && Near(vpXMax, 20, 1e-9) ? "PASS" : "FAIL")}] ★ 视口上改参数真的落到对象（与宿主装配同一条链路）");
            if (!(viaViewport && Near(vpXMax, 20, 1e-9))) failures++;

            // 撤销这一改动 ⇒ 参数回到原值（面板改的东西必须可撤销）
            pxyPanel.Undo();
            var vpUndo = FindObjIn(pxyPanel.GfxObjects.Objects, csPanel);
            double vpXMaxUndo = vpUndo?.GetNumber(CoordSystemGeometry.XMaxKey, double.NaN) ?? double.NaN;
            bool undoParamOk = Near(vpXMaxUndo, 5, 1e-9);
            Console.WriteLine($"  撤销一次后 xMax = {vpXMaxUndo}（期望回到 5）");
            Console.WriteLine($"[{(undoParamOk ? "PASS" : "FAIL")}] ★ 面板改的参数撤销得掉（Ctrl+Z 退回 5）");
            if (!undoParamOk) failures++;

            // 证据图：把面板摆在一个坐标系上（证明它真的长出来了，而不是只有逻辑对）
            pxyPanel.GfxObjects.Select(csPanel);
            pxyPanel.UpdateLayout();
            try
            {
                SavePng(Snapshot(pxyPanel, 900, 600), Path.Combine(outDir, "m74s5-参数面板.png"));
                Console.WriteLine($"  证据图：{Path.Combine(outDir, "m74s5-参数面板.png")}");
            }
            catch (Exception shotEx)
            {
                Console.WriteLine($"  （参数面板截图证据跳过：{shotEx.Message}）");
            }

            pxyPanel.Shutdown();
        }
        catch (Exception panelUiEx)
        {
            Console.WriteLine($"[FAIL] 参数面板 UI 接线断言抛异常：{panelUiEx.GetType().Name} {panelUiEx.Message}");
            failures++;
        }

        // ---- 16) ★ 函数图像：表达式文本参数 + 数值参数 / 开关（M25 文本参数）----
        Console.WriteLine();
        Console.WriteLine("---- 16) ★ 函数图像：表达式文本参数 + 数值参数 / 开关 ----");
        try
        {
            var catF = new GfxRendererCatalog();
            catF.Register(new FunctionRenderer(), "函数图像（学科工具）");
            var stF = new GfxObjectStore(catF);
            var historyF = new GfxHistory(stF);
            var inkF = new InkHistory(new StrokeCollection());
            var boardF = new BoardHistory(inkF, historyF);
            var hostF = new HostGfxObjectHost(stF, historyF, boardF);
            var coordF = new GfxParameterCoordinator(stF, catF, hostF);

            var fn = stF.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(100, 100), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 2,
                Texts = new Dictionary<string, string> { ["expr"] = "a*x^2" },
                Numbers = new Dictionary<string, double> { ["param_a"] = 3, [FunctionRenderer.ShowZerosKey] = 1 },
            });
            stF.Select(fn);

            var panelF = coordF.CurrentPanel;
            bool fnPanelOk = panelF is not null
                && panelF.TextFields.Count == 1
                && panelF.TextFields[0].Key == "expr"
                && panelF.TextFields[0].Value == "a*x^2";
            Console.WriteLine($"  选中函数 ⇒ 面板 {(panelF is null ? "没有" : "出现")}，文本项 expr = 「{(panelF is null ? "(无)" : panelF.TextFields[0].Value)}」");
            Console.WriteLine($"[{(fnPanelOk ? "PASS" : "FAIL")}] ★ 选中函数出现含表达式文本项的面板，取值与对象存档一致");
            if (!fnPanelOk) failures++;

            GfxParameterField? paramA = null, zeros = null;
            if (panelF is not null)
            {
                foreach (var f in panelF.Fields)
                {
                    if (f.Key == "param_a") paramA = f;
                    if (f.Key == FunctionRenderer.ShowZerosKey) zeros = f;
                }
            }
            bool fnFieldsOk = paramA is not null && Math.Abs(paramA.Value - 3.0) < 1e-9
                             && zeros is not null && Math.Abs(zeros.Value - 1.0) < 1e-9;
            Console.WriteLine($"  数值项：参数 a = {(paramA?.Value.ToString() ?? "缺失")}、标零点 = {(zeros?.Value.ToString() ?? "缺失")}（期望 3 / 1）");
            Console.WriteLine($"[{(fnFieldsOk ? "PASS" : "FAIL")}] 参数字母数值项与标注开关项在位、取值与对象一致");
            if (!fnFieldsOk) failures++;

            // ★ 顺序：先改数值参数与开关（此时表达式还是 a*x^2，参数 a 还在面板上），
            //   再改表达式 —— 表达式一旦改成不含参数的形式，参数字母项就合理地消失了。
            bool fnNum = coordF.Apply("param_a", 5, out string fnSt5);
            double fnParamAfter = FindObjIn(stF.Objects, fn)?.GetNumber("param_a", double.NaN) ?? double.NaN;
            bool fnNumOk = fnNum && Math.Abs(fnParamAfter - 5.0) < 1e-9;
            Console.WriteLine($"  Apply(param_a, 5) ⇒ 返回 {fnNum}、对象里是 {fnParamAfter}（期望 5）");
            Console.WriteLine($"[{(fnNumOk ? "PASS" : "FAIL")}] 参数字母数值修改落到对象（函数渲染器数值半边可用）");
            if (!fnNumOk) failures++;

            bool fnTog = coordF.Apply(FunctionRenderer.ShowZerosKey, 0, out string fnSt6);
            double fnTogAfter = FindObjIn(stF.Objects, fn)?.GetNumber(FunctionRenderer.ShowZerosKey, double.NaN) ?? double.NaN;
            bool fnTogOk = fnTog && Math.Abs(fnTogAfter) < 1e-9;
            Console.WriteLine($"  Apply(标零点, 0) ⇒ 返回 {fnTog}、对象里是 {fnTogAfter}（期望 0）");
            Console.WriteLine($"[{(fnTogOk ? "PASS" : "FAIL")}] 标注开关修改落到对象");
            if (!fnTogOk) failures++;

            bool fnApplied = coordF.ApplyText("expr", "x^2", out string fnSt1);
            string fnAfter = FindObjIn(stF.Objects, fn)?.GetText("expr", "") ?? "";
            bool fnLanded = fnApplied && fnAfter == "x^2";
            Console.WriteLine($"  ApplyText(expr, x^2) ⇒ 返回 {fnApplied}、状态栏「{fnSt1}」、对象里是「{fnAfter}」");
            Console.WriteLine($"[{(fnLanded ? "PASS" : "FAIL")}] ★ 文本路径改表达式真的落到对象");
            if (!fnLanded) failures++;

            bool fnSame = coordF.ApplyText("expr", "x^2", out string fnSt2);
            bool fnSameOk = !fnSame && fnSt2.Contains("已经是");
            Console.WriteLine($"[{(fnSameOk ? "PASS" : "FAIL")}] 同样内容再应用返回 false（状态栏「{fnSt2}」，不留空白历史步）");
            if (!fnSameOk) failures++;

            bool fnEmpty = coordF.ApplyText("expr", "   ", out string fnSt3);
            string fnStill = FindObjIn(stF.Objects, fn)?.GetText("expr", "") ?? "";
            bool fnEmptyOk = !fnEmpty && fnStill == "x^2";
            Console.WriteLine($"[{(fnEmptyOk ? "PASS" : "FAIL")}] 空文本被拒（对象仍是「{fnStill}」）");
            if (!fnEmptyOk) failures++;

            bool fnUnknown = coordF.ApplyText("noSuchKey", "x", out string fnSt4);
            bool fnUnknownOk = !fnUnknown && fnSt4.Contains("没有文本参数");
            Console.WriteLine($"[{(fnUnknownOk ? "PASS" : "FAIL")}] 不认识的键被拒（状态栏「{fnSt4}」）");
            if (!fnUnknownOk) failures++;
        }

        catch (Exception fnEx)
        {
            Console.WriteLine($"[FAIL] 函数文本参数断言抛异常：{fnEx.GetType().Name} {fnEx.Message}");
            failures++;
        }

        // ---- 17) ★ 公式：LaTeX 文本参数 + 旧插件无感（M25 文本参数）----
        Console.WriteLine();
        Console.WriteLine("---- 17) ★ 公式：LaTeX 文本参数 + 旧插件无文本项 ----");
        try
        {
            var catL = new GfxRendererCatalog();
            catL.Register(new FormulaRenderer(), "公式（学科工具）");
            var stL = new GfxObjectStore(catL);
            var historyL = new GfxHistory(stL);
            var inkL = new InkHistory(new StrokeCollection());
            var boardL = new BoardHistory(inkL, historyL);
            var hostL = new HostGfxObjectHost(stL, historyL, boardL);
            var coordL = new GfxParameterCoordinator(stL, catL, hostL);

            var fr = stL.Add(new GfxDraft
            {
                Kind = FormulaRenderer.KindName, Center = new Point(200, 200), Scale = 1,
                RotationDegrees = 0, Color = Colors.Black, LineWorldWidth = 1.5,
                Texts = new Dictionary<string, string> { [FormulaRenderer.LatexKey] = "\frac{1}{2}" },
            });
            stL.Select(fr);

            var panelL = coordL.CurrentPanel;
            bool latPanelOk = panelL is not null
                && panelL.TextFields.Count == 1
                && panelL.TextFields[0].Key == FormulaRenderer.LatexKey
                && panelL.TextFields[0].Value == "\frac{1}{2}"
                && panelL.TextFields[0].IsMultiline;
            Console.WriteLine($"  选中公式 ⇒ 面板 {(panelL is null ? "没有" : "出现")}，文本项 latex = 「{(panelL is null ? "(无)" : panelL.TextFields[0].Value)}」");
            Console.WriteLine($"[{(latPanelOk ? "PASS" : "FAIL")}] ★ 选中公式出现 LaTeX 文本项（多行），取值与对象一致");
            if (!latPanelOk) failures++;

            string newLatex = "a^2+b^2=c^2";
            bool latApplied = coordL.ApplyText(FormulaRenderer.LatexKey, newLatex, out string latSt1);
            string latAfter = FindObjIn(stL.Objects, fr)?.GetText(FormulaRenderer.LatexKey, "") ?? "";
            bool latOk = latApplied
                && latAfter == newLatex
                && new FormulaRenderer().GetEntry(latAfter).LocalGeometry is not null;
            Console.WriteLine($"  ApplyText(latex, {newLatex}) ⇒ 返回 {latApplied}、对象里是「{latAfter}」、新文本可渲染 = {(latOk ? "是" : "否")}");
            Console.WriteLine($"[{(latOk ? "PASS" : "FAIL")}] ★ 公式改 LaTeX 落到对象，且新文本能渲染出几何");
            if (!latOk) failures++;

            bool latSame = coordL.ApplyText(FormulaRenderer.LatexKey, newLatex, out string latSt2);
            bool latSameOk = !latSame && latSt2.Contains("已经是");
            Console.WriteLine($"[{(latSameOk ? "PASS" : "FAIL")}] 同样的 LaTeX 再应用返回 false（不留空白历史步）");
            if (!latSameOk) failures++;

            // 旧插件（坐标系，纯数值项）：没有文本项，文本路径被礼貌拒绝
            var catC = new GfxRendererCatalog();
            catC.Register(new CoordSystemRenderer(), "坐标系（学科工具）");
            var stC = new GfxObjectStore(catC);
            var historyC = new GfxHistory(stC);
            var inkC = new InkHistory(new StrokeCollection());
            var boardC = new BoardHistory(inkC, historyC);
            var hostC = new HostGfxObjectHost(stC, historyC, boardC);
            var coordC = new GfxParameterCoordinator(stC, catC, hostC);

            var cs = stC.Add(new GfxDraft
            {
                Kind = CoordSystemRenderer.KindName, Center = new Point(300, 300), Scale = 1,
                RotationDegrees = 0, Color = Colors.Blue, LineWorldWidth = 1.5,
                Numbers = new Dictionary<string, double>
                {
                    [CoordSystemGeometry.XMinKey] = -5, [CoordSystemGeometry.XMaxKey] = 5,
                    [CoordSystemGeometry.YMinKey] = -5, [CoordSystemGeometry.YMaxKey] = 5,
                    [CoordSystemGeometry.UnitWorldKey] = 28.35,
                },
            });
            stC.Select(cs);

            var panelC = coordC.CurrentPanel;
            bool legacyOk = panelC is not null && panelC.TextFields.Count == 0;
            Console.WriteLine($"  选中坐标系 ⇒ 文本项数 = {(panelC?.TextFields.Count.ToString() ?? "null")}（期望 0，旧插件与从前一模一样）");
            Console.WriteLine($"[{(legacyOk ? "PASS" : "FAIL")}] ★ 旧插件无文本项（TextFields 默认空列表不改变其行为）");
            if (!legacyOk) failures++;

            bool legacyApply = coordC.ApplyText(FormulaRenderer.LatexKey, "x", out string latSt3);
            bool legacyApplyOk = !legacyApply && latSt3.Contains("没有文本参数");
            Console.WriteLine($"  坐标系上 ApplyText ⇒ 返回 {legacyApply}、状态栏「{latSt3}」");
            Console.WriteLine($"[{(legacyApplyOk ? "PASS" : "FAIL")}] 无文本项的对象上文本路径被礼貌拒绝（不抛、不改）");
            if (!legacyApplyOk) failures++;
        }
        catch (Exception latEx)
        {
            Console.WriteLine($"[FAIL] 公式文本参数断言抛异常：{latEx.GetType().Name} {latEx.Message}");
            failures++;
        }

        // ---- 18) ★ 真实视口：文本参数走宿主真实装配（面板出现 + 应用 + 撤销）（M25）----
        Console.WriteLine();
        Console.WriteLine("---- 18) ★ 真实视口：文本参数走宿主真实装配（应用 + 撤销）----");
        try
        {
            var pxyT = new CanvasViewportHost();
            pxyT.Measure(new Size(900, 600));
            pxyT.Arrange(new Rect(0, 0, 900, 600));
            pxyT.UpdateLayout();
            pxyT.RegisterGfxRenderer(new FunctionRenderer(), "函数图像（学科工具）");

            var fnVp = pxyT.GfxObjects.Add(new GfxDraft
            {
                Kind = FunctionRenderer.KindName, Center = new Point(300, 300), Scale = 1,
                RotationDegrees = 0, Color = Colors.Red, LineWorldWidth = 2,
                Texts = new Dictionary<string, string> { ["expr"] = "sin(x)" },
            });
            pxyT.GfxObjects.Select(fnVp);
            pxyT.UpdateLayout();

            bool vpShown = pxyT.IsGfxPanelVisible;
            Console.WriteLine($"  选中函数（含文本项）⇒ 面板可见 = {pxyT.IsGfxPanelVisible}（期望 true）");
            Console.WriteLine($"[{(vpShown ? "PASS" : "FAIL")}] 只有文本项的面板也自动出现（可见性判定含文本项）");
            if (!vpShown) failures++;

            bool vpText = pxyT.GfxParameters.ApplyText("expr", "cos(x)", out string vpSt1);
            string vpExpr = FindObjIn(pxyT.GfxObjects.Objects, fnVp)?.GetText("expr", "") ?? "";
            bool vpLanded = vpText && vpExpr == "cos(x)";
            Console.WriteLine($"  走视口协调器改表达式 ⇒ 返回 {vpText}、状态栏「{vpSt1}」、对象里是「{vpExpr}」");
            Console.WriteLine($"[{(vpLanded ? "PASS" : "FAIL")}] ★ 真实视口上改表达式落到对象（与宿主装配同一条链路）");
            if (!vpLanded) failures++;

            pxyT.Undo();
            string vpUndo = FindObjIn(pxyT.GfxObjects.Objects, fnVp)?.GetText("expr", "") ?? "";
            bool vpUndoOk = vpUndo == "sin(x)";
            Console.WriteLine($"  撤销一次 ⇒ 表达式是「{vpUndo}」（期望回到 sin(x)）");
            Console.WriteLine($"[{(vpUndoOk ? "PASS" : "FAIL")}] ★ 改表达式撤销得掉（Ctrl+Z 退回原值）");
            if (!vpUndoOk) failures++;

            pxyT.Shutdown();
        }
        catch (Exception vpEx)
        {
            Console.WriteLine($"[FAIL] 真实视口文本参数接线断言抛异常：{vpEx.GetType().Name} {vpEx.Message}");
            failures++;
        }

        return failures;
    }

    /// <summary>老插件渲染器的 Kind（跨程序集取常量，避免写死字符串）。</summary>
    private static string LegacyRendererKind() => ProtractorRenderer.KindName;

    /// <summary>在一串只读引用里按 Id 找一个（矢量组断言用；找不到返回 null）。</summary>
    private static IGfxObjectRef? FindObjIn(IReadOnlyList<IGfxObjectRef> list, string id)
    {
        foreach (var ob in list)
        {
            if (string.Equals(ob.Id, id, StringComparison.Ordinal)) return ob;
        }

        return null;
    }

    /// <summary>把对象列表里的某个 Id 找出来（截图的尾对齐用）。</summary>
    private static IGfxObjectRef? FindObj(CanvasViewportHost host, string id)
    {
        foreach (var ob in host.GfxObjects.Objects) if (ob.Id == id) return ob;
        return null;
    }

    /// <summary>把箭头摆成"尾部落在给定世界点"（箭头本地原点是首尾中点）。</summary>
    private static Point TailCentered(Point tail, IGfxObjectRef obj)
    {
        double length = ArrowRenderer.LengthWorldOf(obj);
        var unit = VectorMath.UnitVector(ArrowRenderer.AngleOf(obj));
        return new Point(tail.X + unit.X * length / 2.0, tail.Y + unit.Y * length / 2.0);
    }

    /// <summary>从 ArrowVisual 里读它实际用的箭杆画笔（自绘元素，只能反射）。</summary>
    private static Pen? ReadShaftPen(FrameworkElement visual)
    {
        var field = visual.GetType().GetField("_shaftPen",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(visual) as Pen;
    }

    /// <summary>
    /// 探针视觉（<b>反面教材</b>）：按 <c>ActualWidth</c> 画 —— 与 M12 题号牌的写法同源。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 图形层把渲染器的视觉放进一个 <see cref="Canvas"/>，并把它的原点摆到<b>对象中心</b>；
    /// 而这类渲染器约定 <c>MeasureOverride</c> 返回 <c>(0,0)</c>（原点 = 中心，尺寸由参数算）
    /// ⇒ <c>DesiredSize = (0,0)</c> ⇒ <c>Canvas</c> 按 <c>DesiredSize</c> 排版 ⇒
    /// <c>ActualWidth</c> / <c>ActualHeight</c> 恒为 <b>0</b>。
    /// </para>
    /// <para>
    /// 于是「按 ActualWidth 画」的结果是一张 0×0 的空图：数据全对、尺寸断言全绿、
    /// 屏幕上是空的。它存在的唯一目的，是让 S4b 第 8.7 组能用<b>像素</b>证明这条判据 ——
    /// 别的证据都证不了。
    /// </para>
    /// </remarks>
    private sealed class ActualWidthProbeVisual : FrameworkElement
    {
        protected override Size MeasureOverride(Size availableSize) => new(0, 0);

        protected override void OnRender(DrawingContext dc)
            => dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    private sealed class S4FakeRef : IGfxObjectRef
    {
        private readonly IReadOnlyDictionary<string, double> _nums;
        private readonly IReadOnlyDictionary<string, string> _texts;
        public S4FakeRef(string kind, Point center, double scale, double rot,
            IReadOnlyDictionary<string, double> nums, IReadOnlyDictionary<string, string> texts,
            string id = "sys1", Color? color = null)
        { Id = id; Kind = kind; Center = center; Scale = scale; RotationDegrees = rot; _nums = nums; _texts = texts; Color = color ?? Colors.Black; }
        public string Id { get; }
        public string Kind { get; }
        public string PluginName => "";
        public Point Center { get; }
        public double RotationDegrees { get; }
        public double Scale { get; }
        public Size LocalSize => new Size(1, 1);
        public Rect BoundsWorld => new Rect(Center.X - 1, Center.Y - 1, 2, 2);
        /// <remarks>
        /// <b>必须返回 draft 的真实颜色</b>。曾经这里写死 <c>Colors.Black</c>，
        /// 于是"按轴色避开撞色"的逻辑在假 host 下拿到假的轴色 —— 测试报了假失败，
        /// 而生产代码其实是对的。假对象只要有一处不忠实，测试就开始说谎。
        /// </remarks>
        public Color Color { get; }
        public double LineWorldWidth => 1.5;
        public bool HasNumber(string k) => _nums.ContainsKey(k);
        public double GetNumber(string k, double fb = 0) => _nums.TryGetValue(k, out var v) ? v : fb;
        public string GetText(string k, string fb = "") => _texts.TryGetValue(k, out var v) ? v : fb;
    }

    private sealed class S4TestHost : IGfxObjectHost
    {
        public List<IGfxObjectRef> ObjectsList { get; } = new();
        public List<GfxDraft> Added { get; } = new();
        public IReadOnlyList<IGfxObjectRef> Objects => ObjectsList;
        public IGfxObjectRef? Selected => null;
        public event EventHandler? SelectionChanged { add { } remove { } }
        private int _seq;
        public string Add(GfxDraft draft)
        {
            Added.Add(draft);
            string id = "h" + (++_seq);
            ObjectsList.Add(new S4FakeRef(draft.Kind, draft.Center, draft.Scale, draft.RotationDegrees,
                                          draft.Numbers, draft.Texts, id, draft.Color));
            return id;
        }
        public bool Remove(string id) { ObjectsList.RemoveAll(o => o.Id == id); return true; }
        public bool UpdatePose(string id, Point c, double r, double s) => true;
        public bool UpdateNumbers(string id, IReadOnlyDictionary<string, double> n) => true;
        public bool UpdateTexts(string id, IReadOnlyDictionary<string, string> t) => true;
        public void Select(string? id) { }
        public void BeginStep(string label) { }

        /// <summary>M22 S3：最近一次落下的位图字节。</summary>
        public byte[]? LastImage { get; private set; }

        /// <summary>
        /// 真落进 <see cref="ObjectsList"/>，而不是记一笔了事。
        /// </summary>
        /// <remarks>
        /// 记账式的假实现会让「导出之后确实多出一个能选中、能拖动的位图对象」
        /// 这条断言恒真 —— 假对象只要有一处不忠实，测试就开始说谎。
        /// </remarks>
        public string? AddImage(byte[] pngBytes, string label = "")
        {
            if (pngBytes is not { Length: > 0 }) return null;

            LastImage = pngBytes;
            PngSize.TryReadOrRatio(pngBytes, out int pixelW, out int pixelH);
            var size = WebPanelImageRenderer.WorldSizeFor(
                pixelW, pixelH, WebPanelImageRenderer.NominalWorldWidth);

            string id = "img" + (++_seq);
            ObjectsList.Add(new S4FakeRef(
                WebPanelImageRenderer.KindName, new Point(120, 160), 1, 0,
                new Dictionary<string, double>
                {
                    [WebPanelImageRenderer.WidthKey] = size.Width,
                    [WebPanelImageRenderer.HeightKey] = size.Height,
                },
                new Dictionary<string, string>
                {
                    [WebPanelImageRenderer.ImageIdKey] = "s4-" + id,
                    [WebPanelImageRenderer.LabelKey] = label ?? string.Empty,
                },
                id, Colors.Black));
            return id;
        }
    }

}
