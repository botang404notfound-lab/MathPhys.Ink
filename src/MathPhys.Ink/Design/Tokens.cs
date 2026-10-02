using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Design;

/// <summary>
/// 设计令牌的 <b>C# 侧访问面</b>（M10 S0）。
/// </summary>
/// <remarks>
/// 为什么必须有这个类：<c>GfxParameterPanelView</c> / <c>GfxSelectionTool</c> /
/// <c>CanvasViewportHost</c> 都是<b>纯代码构建界面</b>，拿不到 XAML 的 <c>StaticResource</c>。
/// <para>
/// ★ 但它<b>不存任何色值</b> —— 唯一的数据来源永远是 <c>Design/Palette*.xaml</c>。
/// 这里只做"按名字去问资源字典要一个值"。
/// 之所以要写这么死，是因为本项目已经吃过一次亏：<c>InkPalette.cs</c> 定义了一份墨色，
/// <c>MainWindow.xaml</c> 又手抄了一份，改一处另一处不跟着变（docs/15 §1）。
/// 设计令牌绝不能重蹈覆辙。
/// </para>
/// <para>
/// ★ 查找顺序：注入的字典（换肤/测试用）→ <see cref="Application"/> 资源 → 离线自建字典。
/// 最后一档是为<b>验收 harness</b>准备的：harness 不开窗口、没有 <c>Application.Current</c>，
/// 但仍要能断言"每个被引用的令牌键都能解析到"（拼错的键 WPF 不报错、只会静默用默认值，
/// 是这套东西里最容易漏的天坑）。
/// </para>
/// </remarks>
public static class Tokens
{
    /// <summary>本程序集名（拼 pack URI 用）。</summary>
    private const string AssemblyName = "MathPhys.Ink";

    /// <summary>离线字典要合并的字典文件（顺序 = 优先级，与 App.xaml 保持一致）。</summary>
    /// <remarks>
    /// ★ M20 S1 顺手把顺序真的对齐了 App.xaml：原先 Icons 被排在最后，
    ///   与 App.xaml（Icons 在 Controls 之前）不一致 —— 因为这几本定义的键
    ///   互不重名，一直没暴露问题，但注释写着「与 App.xaml 保持一致」而实际不是，
    ///   是个会误导下一个人的隐患。既然要加 Motion，就一并摆正（键集不重叠，
    ///   顺序调整对取值结果无影响，harness 全量回归已确认）。
    /// </remarks>
    private static readonly string[] Modules =
    {
        "Palette.Canvas",
        "Palette.Dark",
        "Typography",
        "Spacing",
        "Motion",
        "Icons",
        "Controls/Buttons",
        "Controls/Inputs",
        "Controls/Panels",
    };

    private static readonly List<string> Missing = new();
    private static readonly HashSet<string> MissingSeen = new(StringComparer.Ordinal);

    private static ResourceDictionary? _injected;
    private static ResourceDictionary? _offline;
    private static bool _offlineTried;

    /// <summary>取过但没解析到的令牌键（同一个键只记一次）。harness 断言它为空。</summary>
    public static IReadOnlyList<string> MissingKeys => Missing;

    /// <summary>清空"没解析到"的记录。断言前先清一次，免得把别的用例的痕迹算进来。</summary>
    public static void ResetMissingKeys()
    {
        Missing.Clear();
        MissingSeen.Clear();
    }

    /// <summary>换肤 / 测试时注入"当前生效的字典"；传 <c>null</c> 表示恢复正常的查找顺序。</summary>
    public static void UseDictionary(ResourceDictionary? dictionary) => _injected = dictionary;

    /// <summary>
    /// 按名字取一个令牌值。
    /// </summary>
    /// <returns>取到返回 true；没有这个键返回 false（并记进 <see cref="MissingKeys"/>）。</returns>
    public static bool TryGet(string key, out object? value)
    {
        if (TryFrom(_injected, key, out value)) return true;
        if (TryFrom(Application.Current?.Resources, key, out value)) return true;
        if (TryFrom(OfflineDictionary(), key, out value)) return true;

        // 记一笔：这个键谁都解析不到。调用方拿到的是兜底值（不会崩），
        // 但 harness 会因此判 FAIL —— 这正是我们要的"早暴露"。
        if (MissingSeen.Add(key)) Missing.Add(key);
        value = null;
        return false;
    }

    /// <summary>这个键解析得到吗（键存在性断言用）。</summary>
    public static bool Exists(string key) => TryGet(key, out _);

    // ---------------------------------------------------------------- 类型化取值

    /// <summary>取一个画刷。解析不到时给透明（不崩，但会被 harness 抓到）。</summary>
    public static Brush Brush(string key)
        => TryGet(key, out var value) && value is Brush brush ? brush : Brushes.Transparent;

    /// <summary>取一个颜色（从画刷里读）。解析不到时给透明。</summary>
    public static Color Color(string key)
        => Brush(key) is SolidColorBrush solid ? solid.Color : Colors.Transparent;

    /// <summary>取一个数值（字号 / 间距 / 圆角）。解析不到时给 0。</summary>
    public static double Number(string key)
        => TryGet(key, out var value) && value is double number ? number : 0;

    /// <summary>取一个内边距 / 间隙。解析不到时给全 0。</summary>
    public static Thickness Thickness(string key)
        => TryGet(key, out var value) && value is Thickness thickness ? thickness : default;

    /// <summary>取一个圆角。解析不到时给全 0。</summary>
    public static CornerRadius CornerRadius(string key)
        => TryGet(key, out var value) && value is CornerRadius corner ? corner : default;

    /// <summary>取一个字重。解析不到时给 Normal。</summary>
    public static FontWeight FontWeight(string key)
        => TryGet(key, out var value) && value is FontWeight weight ? weight : FontWeights.Normal;

    /// <summary>取一个字体族。解析不到时给空（由 WPF 自己回退）。</summary>
    public static FontFamily FontFamily(string key)
        => TryGet(key, out var value) && value is FontFamily family ? family : new FontFamily();

    /// <summary>
    /// 取一个几何（图标路径 <c>Icon.*</c>）。解析不到时给 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// M20 S4 加的：工具按钮的图标来自<b>目录表</b>（<c>Tools/ToolCatalog.cs</c>）里的键名字符串，
    /// 不是 XAML 里的 <c>{StaticResource}</c> —— 键名在运行时才知道，静态引用用不上。
    /// <para>
    /// 走这里而不是直接 <c>Application.Current.FindResource</c> 的原因与其它取值一致：
    /// 拼错的键会记进 <see cref="MissingKeys"/>，被 harness 当场抓到；
    /// 而 <c>FindResource</c> 抛异常、<c>TryFindResource</c> 静默给 null（图标凭空消失）。
    /// </para>
    /// <para>
    /// ★ 图标几何是<b>冻结的</b>（色板里带 <c>po:Freeze</c>），同一个 Geometry 实例被多个
    /// <c>Path</c> 共享是安全的 —— 它不可变。
    /// </para>
    /// </remarks>
    public static Geometry? Geometry(string key)
        => TryGet(key, out var value) && value is Geometry geometry ? geometry : null;

    /// <summary>
    /// 把一个依赖属性<b>接到设计令牌上</b> —— 代码版的 <c>{DynamicResource}</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么需要它：<c>Tokens.Brush(key)</c> 取的是<b>一次快照</b>。XAML 里写
    /// <c>{DynamicResource}</c> 的兄弟元素换肤后跟着变，纯代码构建的面板却还留着旧色 ——
    /// 屏幕上表现为「漏了一块」。M20 步 2b 收掉的就是这批面板。
    /// </para>
    /// <para>
    /// ★ 判据是「<b>当前确实有一本 Application 资源字典能解析出这个键</b>」，而这正是
    ///   WPF 解析 <c>DynamicResource</c> 的条件。不能只看 <c>Application.Current is null</c>：
    ///   验收 harness 自己也会造一个 <c>Application</c> 并合并同一批字典
    ///   （<c>PdfSmokeTest.InitializeDesignResources</c>），看 null 会把它误判成离线。
    /// </para>
    /// <para>
    /// ★ 解析不到时<b>退回取一次静态值</b>，而不是什么都不做：资源引用解析不到会把属性
    ///   打回依赖属性的默认值（通常是 <c>null</c>），颜色会凭空消失；而没有资源环境的场景里
    ///   根本不存在换肤，快照不会残留旧色。
    /// </para>
    /// </remarks>
    public static T FollowTheme<T>(this T element, DependencyProperty property, string key)
        where T : FrameworkElement
    {
        if (Application.Current is { } app && app.Resources.Contains(key))
        {
            element.SetResourceReference(property, key);
        }
        else if (TryGet(key, out var value))
        {
            element.SetValue(property, value);
        }

        return element;
    }

    // ---------------------------------------------------------------- 内部

    private static bool TryFrom(ResourceDictionary? dictionary, string key, out object? value)
    {
        // ResourceDictionary.Contains 会连同 MergedDictionaries 一起查 ——
        // 所以"键在合并进来的某一本里"也算存在。
        if (dictionary is not null && dictionary.Contains(key))
        {
            value = dictionary[key];
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// 离线字典：给"没有 Application 的场景"（验收 harness）用。
    /// </summary>
    /// <remarks>
    /// 只加载<b>不需要窗口</b>的那几本。任何一本缺了都只记一笔并返回 null ——
    /// 这时所有取值都会走兜底，harness 的"键存在性"断言会立刻炸出来，
    /// 不会变成"看起来一切正常、只是颜色不对"。
    /// </remarks>
    private static ResourceDictionary? OfflineDictionary()
    {
        if (_offlineTried) return _offline;
        _offlineTried = true;

        var root = new ResourceDictionary();

        foreach (var module in Modules)
        {
            try
            {
                var part = new ResourceDictionary
                {
                    Source = new Uri(
                        $"pack://application:,,,/{AssemblyName};component/Design/{module}.xaml",
                        UriKind.Absolute),
                };
                root.MergedDictionaries.Add(part);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"设计令牌：离线加载 Design/{module}.xaml 失败 —— {ex.Message}");
            }
        }

        _offline = root;
        return _offline;
    }
}
