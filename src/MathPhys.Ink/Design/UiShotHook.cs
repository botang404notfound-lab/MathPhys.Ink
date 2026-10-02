using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MathPhys.Ink.Ink;
using MathPhys.Ink.Views;

namespace MathPhys.Ink.Design;

/// <summary>
/// 界面自测钩子：让程序**自己**把主窗口渲染成 PNG 并退出。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：验收「换肤到底换没换」这类判据只能看像素，
/// 而系统级截图（<c>ImageGrab</c>）与 <c>PrintWindow</c> 都依赖桌面可见 ——
/// 机器一锁屏，前者抓到的是锁屏界面、后者拿到的是合成器里的**旧帧**
/// （实测还会带着另一个 DPI 缩放，看起来像"控件消失了"，能把人带进沟里）。
/// </para>
/// <para>
/// 这里改用 WPF 自己的 <see cref="RenderTargetBitmap"/>：它渲染的是**当前视觉树**，
/// 与窗口是否可见、会话是否锁屏都无关，因此结果确定。
/// </para>
/// <para>
/// 用法（没有该环境变量时这段代码完全不执行，对正常使用零影响）：
/// <code>set M10_UI_SHOT=D:\shots &amp;&amp; MathPhys.Ink.exe</code>
/// 输出：<c>ui-a.png</c>（当前主题）、<c>ui-b.png</c>（切到另一主题）、
/// <c>ui-shot.txt</c>（两张图的主题名与墨色真值，供断言比对）。
/// </para>
/// <para>
/// 与 M7.5 的 <c>M75_GGB_SELFTEST</c> 同属"环境变量门控的自测钩子"这一套做法。
/// </para>
/// </remarks>
public static class UiShotHook
{
    /// <summary>启用本钩子的环境变量名；值 = 输出目录。</summary>
    public const string EnvName = "M10_UI_SHOT";

    /// <summary>窗口构造完成后调用；未设置环境变量时立刻返回。</summary>
    public static void RunIfRequested(Window window)
    {
        string? dir = Environment.GetEnvironmentVariable(EnvName);
        if (string.IsNullOrWhiteSpace(dir)) return;

        try { Directory.CreateDirectory(dir); }
        catch { return; }

        // 必须在布局与首帧渲染都完成之后再抓，否则抓到的是空内容。
        window.Dispatcher.BeginInvoke(new Action(() => Run(window, dir)),
            DispatcherPriority.ApplicationIdle);
    }

    private static void Run(Window window, string dir)
    {
        var report = new StringBuilder();
        try
        {
            Shoot(window, dir, "a", report);

            ThemeService.Toggle();
            window.UpdateLayout();
            Shoot(window, dir, "b", report);
        }
        catch (Exception ex)
        {
            report.AppendLine("EX: " + ex);
        }

        try { File.WriteAllText(Path.Combine(dir, "ui-shot.txt"), report.ToString()); }
        catch { /* 出图失败不该让程序挂在这儿 */ }

        Application.Current?.Shutdown();
    }

    private static void Shoot(Window window, string dir, string tag, StringBuilder report)
    {
        window.UpdateLayout();

        double w = Math.Max(1, window.ActualWidth);
        double h = Math.Max(1, window.ActualHeight);
        var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(Path.Combine(dir, $"ui-{tag}.png")))
        {
            encoder.Save(fs);
        }

        report.AppendLine($"[{tag}] size = {(int)w}x{(int)h}  theme = {ThemeService.CurrentDisplayName}");
        report.AppendLine($"[{tag}] {DescribeToolbar(window)}");
        report.AppendLine($"[{tag}] ui.chrome = {Describe(Tokens.Brush("Ui.ChromeBackground"))}");
        report.AppendLine($"[{tag}] ui.window = {Describe(Tokens.Brush("Ui.WindowBackground"))}");
        report.AppendLine($"[{tag}] ui.text   = {Describe(Tokens.Brush("Ui.TextPrimary"))}");
        foreach (InkColorOption option in InkPalette.Colors)
        {
            report.AppendLine($"[{tag}] ink.{option.Name} = {Describe(new SolidColorBrush(option.Color))}");
        }

        report.AppendLine();
    }

    /// <summary>
    /// 工具栏的**实测尺寸**与可点目标数（M20 S4 加的，用于「分组前后到底矮了没有」）。
    /// </summary>
    /// <remarks>
    /// 为什么要专门量它：工具栏是**浮动**的，限宽之后只能靠换行容纳全部瓦片，
    /// 而"到底占了多高"这件事在源码里看不出来 —— 只能量。
    /// 一体机屏幕高 720，工具栏一旦高过可视区就会把下半截的按钮推到屏幕外，
    /// 那种情况下老师"找不到按钮"却看不出来是布局问题（截图里它只是被截掉了）。
    /// <para>
    /// 同时数一遍<b>可点目标</b>（Button / ToggleButton / CheckBox）：
    /// 分组与折叠二级工具的目的就是"少占地方又不丢功能"，
    /// 所以个数与高度要一起看 —— 只矮了但丢了几颗按钮，那是错的。
    /// </para>
    /// </remarks>
    private static string DescribeToolbar(Window window)
    {
        if (window.FindName("TopChrome") is not FrameworkElement chrome)
        {
            return "toolbar = (未找到 TopChrome)";
        }

        int count = 0;
        CountTargets(chrome, ref count);

        string line = $"toolbar = {(int)chrome.ActualWidth}x{(int)chrome.ActualHeight}  targets = {count}";

        // 分组 + 二级菜单之后，"有没有把工具弄丢"必须能自证：
        // tiles + folded + other 应当等于注册表里的工具数（见 MainWindow.ToolbarAudit）。
        if (window is MainWindow main) line += "  " + main.ToolbarAudit;

        return line;
    }

    /// <summary>数Visual 树里的可点目标（含模板展开出来的那些）。</summary>
    private static void CountTargets(DependencyObject node, ref int count)
    {
        int n = VisualTreeHelper.GetChildrenCount(node);

        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);

            // CheckBox / ToggleButton 都是 ButtonBase 的派生类，判基类即可一次数全。
            if (child is System.Windows.Controls.Primitives.ButtonBase)
            {
                count++;
            }

            CountTargets(child, ref count);
        }
    }

    /// <summary>把画刷写成 <c>#AARRGGBB</c>，便于脚本直接比对截图里的像素。</summary>
    private static string Describe(Brush? brush)
        => brush is SolidColorBrush s ? $"#{s.Color.A:X2}{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "(非纯色)";
}
