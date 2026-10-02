using System.IO;
using System.Windows;
using System.Windows.Threading;
using MathPhys.Ink.Design;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink;

/// <summary>
/// 程序入口。
/// 这里做一件在教室里很关键的事：兜住所有未处理异常并落盘成日志。
/// 一体机上崩了没法调试，只能靠日志文件排查。
/// </summary>
public partial class App : Application
{
    /// <summary>日志目录：%LOCALAPPDATA%\MathPhys.Ink\logs</summary>
    public static string LogDirectory => AppLog.LogDirectory;

    /// <summary>
    /// 启动自检（M23 热修）。
    /// ★★ 必须放在**静态**构造函数里：Application 基类构造函数会把整个启动流程
    /// （DoStartup → OnStartup → 加载 App.xaml 资源）**排队成 Dispatcher 操作**，
    /// 若在实例构造函数里弹模态框，模态泵会把排在后面的启动流程一并泵进来 ——
    /// 实测发生重入：弹窗底下主题初始化照跑、启动流程在缺库状态下继续，
    /// 进程挂在半启动态（探针日志可证：弹窗后 41ms 就出现了主题切换行）。
    /// 静态构造跑在 Application 基类构造**之前**，此刻队列是空的，模态框才是纯模态框。
    /// </summary>
    static App()
    {
        // 现场实测踩到：压缩包还没解压完（或在压缩包里直接双击）就运行，
        // 自包含布局里的 WPF 原生库尚未就位 ⇒ 天书报错 XamlParseException
        // （内层 DllNotFoundException: wpfgfx_cor3.dll）直接把老师吓住。
        // 在任何 WPF 机制启动**之前**把「文件不完整」查出来，换成一句能行动的中文。
        if (!RuntimeFilesPresent())
        {
            AppLog.Error("启动自检", new InvalidOperationException(
                "自包含布局不完整：exe 旁缺 WPF 原生运行库（wpfgfx_cor3.dll / PresentationNative_cor3.dll）。"
                + "常见原因：压缩包未解压完就双击了 exe，或直接在压缩包里双击运行。"));
            MessageBox.Show(
                "程序文件不完整，暂时无法启动。\n\n"
                + "常见原因：\n"
                + "① 压缩包还没解压完就双击了 exe —— 等解压结束后再试；\n"
                + "② 直接在压缩包里双击运行 —— 请先把压缩包完整解压到一个文件夹。\n\n"
                + "处理办法：把压缩包【完整解压】到任意文件夹，再双击里面的 MathPhys.Ink.exe。",
                "数理墨 —— 文件不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
            Environment.Exit(1);
        }
    }


    /// <summary>
    /// 自包含发布的判定：exe 旁有 hostfxr.dll（自带 .NET 运行时）才做检查；
    /// 开发机 dotnet run 是框架依赖布局，原生库在共享框架目录里、exe 旁本来就没有 ⇒ 跳过。
    /// </summary>
    private static bool RuntimeFilesPresent()
    {
        string baseDir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(baseDir, "hostfxr.dll")))
        {
            return true;   // 框架依赖布局，不做这套检查
        }
        return File.Exists(Path.Combine(baseDir, "wpfgfx_cor3.dll"))
            && File.Exists(Path.Combine(baseDir, "PresentationNative_cor3.dll"));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 界面主题（M10 S6）必须在主窗口**建出来之前**定下来：
        // 窗口一建好就会用当时的色板把静态资源解析一遍，之后再换，
        // 那一批静态引用就留在旧色上了（表现为"切了浅色，有几块还是黑的"）。
        ThemeService.Initialize();

        // UI 线程未处理异常
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // 非 UI 线程未处理异常
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("DispatcherUnhandledException", e.Exception);

        MessageBox.Show(
            $"程序遇到一个错误，已写入日志：\n{LogDirectory}\n\n{e.Exception.Message}",
            "数理墨", MessageBoxButton.OK, MessageBoxImage.Error);

        // 标记已处理，避免整个程序直接退出——上课中途崩掉是最糟的情况
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            AppLog.Error("AppDomainUnhandledException", ex);
        }
    }
}
