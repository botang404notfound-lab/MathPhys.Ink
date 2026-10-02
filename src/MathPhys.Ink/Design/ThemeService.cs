using System.Windows;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Design;

/// <summary>界面主题。只有两套，深色是出厂默认。</summary>
public enum ThemeKind
{
    /// <summary>深色（默认）。教室投影与长时间使用下更护眼。</summary>
    Dark,

    /// <summary>浅色。白板的"纸"本来就是白的，浅色界面与它更连贯。</summary>
    Light,
}

/// <summary>
/// 换肤服务（M10 S6）。
/// </summary>
/// <remarks>
/// 实现只有一句话：<b>把 App 资源里那一本界面板换掉，别的一律不动</b>。
/// <para>
/// ★ 为什么"只换一本"是这套东西的全部价值所在：
/// 三套色板（Ui / Ink / Canvas）在结构上就是分开的，所以换肤**不可能**误伤
/// 墨迹颜色或画布上的选中高亮 —— 不需要任何人去记得"这里别改"。
/// </para>
/// <para>
/// ★ 换完必须让 C# 侧也刷新一次：XAML 里的 <c>DynamicResource</c> 会自动更新，
/// 但 code-behind 里"取出来存成字段"的那些画刷不会（M10 把那些字段都改成了按需取用，
/// 并把少数确实缓存了状态的地方挂到了 <see cref="ThemeChanged"/> 上）。
/// </para>
/// </remarks>
public static class ThemeService
{
    /// <summary>界面板在资源里的文件名（换肤时按它认出那一本）。</summary>
    private const string DarkPalette = "Palette.Dark.xaml";
    private const string LightPalette = "Palette.Light.xaml";

    private const string AssemblyName = "MathPhys.Ink";

    /// <summary>当前主题。</summary>
    public static ThemeKind Current { get; private set; } = ThemeKind.Dark;

    /// <summary>主题变了（浅 ⇄ 深）。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>当前主题的名字（按钮上显示"现在是什么"时用）。</summary>
    public static string CurrentDisplayName => Current == ThemeKind.Dark ? "深色" : "浅色";

    /// <summary>切换目标的名字（按钮上显示"点了会怎样"时用）。</summary>
    public static string SwitchTargetName => Current == ThemeKind.Dark ? "浅色" : "深色";

    /// <summary>
    /// 启动时恢复上次的主题。
    /// </summary>
    /// <remarks>
    /// ★ 必须在窗口建出来**之前**调用（<c>App.OnStartup</c>）：
    /// 窗口一建好就会用当时的色板把静态资源解析一遍，
    /// 之后再换，那一批静态引用就留在了旧色上（"开了浅色却有几块还是黑的"）。
    /// </remarks>
    public static void Initialize()
    {
        var saved = UiStateStore.ReadTheme();

        // 出厂默认深色。存档里写的是 "light" 才用浅色。
        var kind = string.Equals(saved, "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeKind.Light
            : ThemeKind.Dark;

        Apply(kind, raise: false);
    }

    /// <summary>在深色与浅色之间切换。</summary>
    public static void Toggle()
        => Apply(Current == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);

    /// <summary>换到指定主题。</summary>
    public static void Apply(ThemeKind kind) => Apply(kind, raise: true);

    private static void Apply(ThemeKind kind, bool raise)
    {
        Current = kind;

        var app = Application.Current;
        if (app is null)
        {
            // 没有 Application（验收 harness）：只记状态，不改资源 —— 不值得为此报错
            if (raise) ThemeChanged?.Invoke(null, EventArgs.Empty);
            return;
        }

        string file = kind == ThemeKind.Dark ? DarkPalette : LightPalette;
        var source = new Uri(
            $"pack://application:,,,/{AssemblyName};component/Design/{file}",
            UriKind.Absolute);

        var merged = app.Resources.MergedDictionaries;
        bool replaced = false;
        int insertAt = merged.Count;

        for (int i = 0; i < merged.Count; i++)
        {
            var text = merged[i].Source?.OriginalString;
            if (text is null) continue;

            // 两本界面板互斥，认到任意一本就说明"这里就是界面板的位置"
            if (!text.Contains("Palette.Dark.xaml", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("Palette.Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            insertAt = i;
            merged.RemoveAt(i);
            merged.Insert(i, new ResourceDictionary { Source = source });
            replaced = true;
            break;
        }

        if (!replaced)
        {
            // 没找到位置（App.xaml 被改过？）就追加 —— 追加会改变优先级，
            // 所以这里必须留痕，而不是默默加一本了事
            merged.Add(new ResourceDictionary { Source = source });
            AppLog.Warn($"换肤：资源里没有找到界面板字典，已将 {file} 追加到最后（顺序会变化）。insertAt={insertAt}");
        }

        // 记下来，下次启动直接用它 —— 一体机上没人愿意每次开机重设一次主题
        UiStateStore.WriteTheme(kind == ThemeKind.Dark ? "dark" : "light");

        AppLog.Info($"界面主题已切换为：{CurrentDisplayName}");

        // ★ 换完再通知：订阅者此时读到的已经是新色板的值
        if (raise) ThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}
