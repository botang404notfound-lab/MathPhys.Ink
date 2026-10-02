using System;
using System.Collections.Generic;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 把"当前选中的对象"翻译成参数面板内容，并把面板上的修改写回对象。
/// </summary>
/// <remarks>
/// 这一层刻意做成<b>不碰任何 WPF 控件</b>的纯逻辑：它只回答两个问题 ——
/// 「现在该显示什么」（<see cref="CurrentPanel"/>）与「用户把这个键改成这个值之后，对象该变成什么」
/// （<see cref="Apply"/>）。界面由 <c>GfxParameterPanelView</c> 负责长出来。
/// <para>
/// ★ 这么分的最直接好处：验收 harness 里<b>没有窗口</b>，如果这一层带着控件，
/// "面板能改参数"这条最关键的路径就永远测不到。现在它可以被完整断言
/// （面板内容对不对、越界值被夹住没有、改了之后对象的几何签名变没变）。
/// </para>
/// <para>
/// 数据来源分两路：渲染器（<see cref="IGfxParameterProvider"/>）负责"有哪些参数"，
/// 渲染器的 <c>Kind</c> 决定去哪个渲染器问。插件没实现这个可选接口 ⇒ 没有面板，一切照常。
/// </para>
/// </remarks>
public sealed class GfxParameterCoordinator
{
    /// <summary>一个数值项允许的最大步长倍率兜底（没给 Step 时按范围算）。</summary>
    private const double DefaultStepFallback = 1.0;

    private readonly GfxObjectStore _store;
    private readonly GfxRendererCatalog _catalog;
    private readonly IGfxObjectHost _host;

    public GfxParameterCoordinator(GfxObjectStore store, GfxRendererCatalog catalog, IGfxObjectHost host)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// 当前选中对象的参数面板；没有选中、或该对象没有可调参数时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>每次都向插件重新问一遍</b>，不缓存：对象的值随时可能被别处改掉
    /// （拖动会改位姿、合力会重算），缓存一份就必然出现"面板上还是旧数"。
    /// 问一次的开销是构造几个小对象，与"显示错了"的代价完全不成比例。
    /// </remarks>
    public GfxParameterPanel? CurrentPanel
    {
        get
        {
            var selected = _store.Selected;
            if (selected is null) return null;

            var provider = _catalog.FindParameterProvider(selected.Kind);
            if (provider is null) return null;

            try
            {
                return provider.BuildPanel(selected);
            }
            catch (Exception ex)
            {
                // 插件的显示逻辑出错 ⇒ 没有面板，而不是"选中就崩"。
                // 属性 getter 里绝不能把异常抛出去给界面。
                AppLog.Warn($"参数面板构建失败（{selected.Kind}）：{ex.GetType().Name} {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 把某个字段设成新值。
    /// </summary>
    /// <param name="key">参数键。</param>
    /// <param name="value">用户输入的值（<b>未</b>规整；本方法内部按字段的 Step/范围收敛）。</param>
    /// <param name="status">给状态栏的一句话（成功或失败都有一句）。</param>
    /// <returns>是否真的改动了对象。</returns>
    /// <remarks>
    /// 顺序是硬要求：
    /// <list type="number">
    /// <item><b>先取面板</b>拿到字段定义（值域、步长）—— 没有字段定义就无从判断合法性；</item>
    /// <item><b>再规整</b>（Snap：夹范围 + 对齐步长）—— 让 "<c>1e9</c>" 这种输入变成上限而不是把图形甩到天边；</item>
    /// <item><b>问插件</b>要最终要落盘的键值对（插件可能要连带改别的键，宿主不猜）；</item>
    /// <item><b>最后改</b>，并且只在<b>真的会变</b>时才记历史步 —— 否则撤销栈里会多出"什么都没变"的空白步。</item>
    /// </list>
    /// </remarks>
    public bool Apply(string key, double value, out string status)
    {
        status = string.Empty;

        var selected = _store.Selected;
        if (selected is null)
        {
            status = "没有选中的对象，改不了参数。";
            return false;
        }

        var panel = CurrentPanel;
        if (panel is null)
        {
            status = "这个对象没有可调参数。";
            return false;
        }

        GfxParameterField? field = null;
        foreach (var f in panel.Fields)
        {
            if (string.Equals(f.Key, key, StringComparison.Ordinal)) { field = f; break; }
        }

        if (field is null)
        {
            status = $"「{selected.Kind}」不认识参数 {key}。";
            return false;
        }

        if (field.IsReadOnly)
        {
            status = $"「{field.Label}」是只读的（由别的量算出来）。";
            return false;
        }

        double snapped = field.Snap(value);
        if (Nearly(snapped, field.Value))
        {
            // 值没变：不记历史、不动对象。这不叫失败，所以不报红。
            status = $"「{field.Label}」已经是 {Describe(snapped, field.Unit)}。";
            return false;
        }

        var provider = _catalog.FindParameterProvider(selected.Kind);
        if (provider is null || provider is not IGfxParameterSink sink)
        {
            // 能显示、不能改 —— 这是合法组合（比如插件只想展示读数）。
            status = $"「{field.Label}」暂不支持修改。";
            return false;
        }

        IReadOnlyDictionary<string, double>? changes;
        try
        {
            changes = sink.Apply(selected, key, snapped);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"参数应用失败（{selected.Kind}.{key}）：{ex.GetType().Name} {ex.Message}");
            status = $"改「{field.Label}」时出错：{ex.Message}";
            return false;
        }

        if (changes is null || changes.Count == 0)
        {
            status = $"「{field.Label}」改成 {Describe(snapped, field.Unit)} 会被拒绝（非法值）。";
            return false;
        }

        // ★ 先把这一步记进历史，再改 —— 反过来的话撤销会退回到"已经改过"的状态。
        _host.BeginStep($"调参数 {field.Label}");
        if (!_host.UpdateNumbers(selected.Id, changes))
        {
            status = $"改「{field.Label}」没有落到对象上。";
            return false;
        }

        status = $"「{field.Label}」= {Describe(snapped, field.Unit)}";
        return true;
    }

    /// <summary>
    /// 把某个文本字段设成新内容。
    /// </summary>
    /// <param name="key">参数键（与面板上该文本项的键一致）。</param>
    /// <param name="text">用户输入的内容（原始文本；本方法内部 trim）。</param>
    /// <param name="status">给状态栏的一句话（成功或失败都有一句）。</param>
    /// <returns>是否真的改动了对象。</returns>
    /// <remarks>
    /// 顺序与 <see cref="Apply"/> 完全同构：
    /// <list type="number">
    /// <item><b>先取面板</b>确认「这个文本项存在、不是只读」—— 没有字段定义就无从判断；</item>
    /// <item><b>没变就不动</b>（trim 后相等）—— 撤销栈里不留空白步；</item>
    /// <item><b>问插件</b>（<see cref="IGfxTextParameterSink"/>）要最终要落盘的键值对；</item>
    /// <item><b>最后改</b>，而且只在真的会变时才记历史步。</item>
    /// </list>
    /// </remarks>
    public bool ApplyText(string key, string text, out string status)
    {
        status = string.Empty;

        var selected = _store.Selected;
        if (selected is null)
        {
            status = "没有选中的对象，改不了参数。";
            return false;
        }

        var panel = CurrentPanel;
        if (panel is null)
        {
            status = "这个对象没有可调参数。";
            return false;
        }

        GfxTextField? field = null;
        foreach (var f in panel.TextFields)
        {
            if (string.Equals(f.Key, key, StringComparison.Ordinal)) { field = f; break; }
        }

        if (field is null)
        {
            status = $"「{selected.Kind}」没有文本参数 {key}。";
            return false;
        }

        if (field.IsReadOnly)
        {
            status = $"「{field.Label}」是只读的。";
            return false;
        }

        string trimmed = text?.Trim() ?? "";
        if (trimmed == field.Value)
        {
            // 内容没变：不记历史、不动对象。这不叫失败，所以不报红。
            status = $"「{field.Label}」已经是该内容。";
            return false;
        }

        var provider = _catalog.FindParameterProvider(selected.Kind);
        if (provider is null || provider is not IGfxTextParameterSink textSink)
        {
            // 能显示、不能改 —— 这是合法组合（比如插件只想展示内容）。
            status = $"「{field.Label}」暂不支持修改。";
            return false;
        }

        IReadOnlyDictionary<string, string>? changes;
        try
        {
            changes = textSink.ApplyText(selected, key, trimmed);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"文本参数应用失败（{selected.Kind}.{key}）：{ex.GetType().Name} {ex.Message}");
            status = $"改「{field.Label}」时出错：{ex.Message}";
            return false;
        }

        if (changes is null || changes.Count == 0)
        {
            status = $"「{field.Label}」被拒绝（内容不合法）。";
            return false;
        }

        // ★ 先把这一步记进历史，再改 —— 同数值路径的理由。
        _host.BeginStep($"改{field.Label}");
        if (!_host.UpdateTexts(selected.Id, changes))
        {
            status = $"改「{field.Label}」没有落到对象上。";
            return false;
        }

        status = $"「{field.Label}」已更新。";
        return true;
    }

    /// <summary>把值转成"带单位的人话"（面板与状态栏共用，免得两处格式不一致）。</summary>
    /// <remarks>
    /// 用不变文化（<c>InvariantCulture</c>）：一体机若装了区域设置，
    /// 小数分隔符可能是逗号，那会让 "1,5" 这种读数看着像两个数。
    /// </remarks>
    public static string Describe(double value, string unit)
    {
        string text = Math.Abs(value - Math.Round(value)) < 1e-9
            ? Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        return string.IsNullOrEmpty(unit) ? text : text + " " + unit;
    }

    /// <summary>浮点比较：参数是"人对人手输的"，1e-9 的差不算变化。</summary>
    private static bool Nearly(double a, double b) => Math.Abs(a - b) < 1e-9;
}
