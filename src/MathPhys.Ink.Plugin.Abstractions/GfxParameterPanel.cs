namespace MathPhys.Ink.Plugins;

/// <summary>
/// 参数面板里<b>一个可编辑的数值项</b>的描述（纯数据，不含任何 WPF 控件）。
/// </summary>
/// <remarks>
/// ★ <b>为什么是"描述"而不是"给我一个控件"</b>：
/// 若契约让插件返回 <c>UIElement</c>，就同时坏掉三件事 ——
/// <list type="number">
/// <item>插件被迫依赖宿主的界面风格常量（字号、配色、间距），宿主一改版插件就长得不一样；</item>
/// <item>面板逻辑散到每个插件里，同样的"数值输入框"要写 N 遍，N 份 bug；</item>
/// <item><b>最要命的一条</b>：验收 harness 里根本没有窗口，返回控件的接口在 harness 里
/// 完全测不了 —— 于是"面板能改参数"这条路径永远处于无测试状态。</item>
/// </list>
/// 返回<b>数据</b>就没这些事：宿主决定长什么样，插件只回答"我这个对象有哪些参数"，
/// 断言可以只验数据（"该出现哪几项、取值范围对不对"），根本不需要窗口。
/// </remarks>
public sealed class GfxParameterField
{
    /// <summary>参数键（读写都走它，与 <see cref="IGfxObjectRef.GetNumber"/> 同键）。</summary>
    public string Key { get; init; } = "";

    /// <summary>面板上显示的名字（「半径」「步长」…）。</summary>
    public string Label { get; init; } = "";

    /// <summary>当前值（已按 <see cref="Step"/> 规整过的最好）。</summary>
    public double Value { get; init; }

    /// <summary>
    /// 最小值 / 最大值（含）。两者相等表示<b>不限制</b> —— 用一个值表达
    /// "没有范围约束"，而不是再加一个布尔字段：两个字段能组合出"最小值 &gt; 最大值"
    /// 这种非法状态，一个字段不能。
    /// </summary>
    public double Min { get; init; }

    /// <summary>最大值（见 <see cref="Min"/>；<c>Min == Max</c> 即不限制）。</summary>
    public double Max { get; init; }

    /// <summary>点击加减按钮时的步长；<c>&lt;= 0</c> 时宿主用一个默认步长。</summary>
    public double Step { get; init; }

    /// <summary>单位后缀（「cm」「°」「N」…）；空串表示不显示。</summary>
    public string Unit { get; init; } = "";

    /// <summary>是否只读（显示但改不了；例如由别人算出来的量）。</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>是否限制取值范围（<see cref="Min"/> 与 <see cref="Max"/> 不同时为真）。</summary>
    public bool HasRange => Min < Max;

    /// <summary>把值夹到 [Min, Max]（不限制时原样返回；NaN/Infinity 也会被挡掉）。</summary>
    public double Clamp(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return Value;
        if (!HasRange) return value;
        return value < Min ? Min : value > Max ? Max : value;
    }

    /// <summary>按步长把一个值规整到最近的档位（有范围时夹住）。</summary>
    public double Snap(double value)
    {
        double v = Clamp(value);
        if (Step <= 0 || double.IsNaN(v)) return v;

        // 用 Min 当基准：步长是"相对范围起点"的，否则拖动时会出现 0.1 的累积余数
        double baseValue = HasRange ? Min : 0.0;
        double snapped = baseValue + Math.Round((v - baseValue) / Step) * Step;

        return Clamp(snapped);
    }
}

/// <summary>
/// 某个对象在参数面板里的<b>完整描述</b>。
/// </summary>
/// <remarks>
/// 渲染器（那个知道"这个对象有哪些参数"的人）生产它，宿主把它变成界面。
/// <b>没有参数就返回 <c>null</c></b>（宿主据此隐藏面板），不要返回一个空壳 ——
/// "空面板"和"没有面板"在界面上的差别是"多了个空白框"，看着像坏了。
/// </remarks>
public sealed class GfxParameterPanel
{
    /// <summary>面板标题（一般写对象的显示名，如「量角器」）。</summary>
    public string Title { get; init; } = "";

    /// <summary>面板副标题（可放"这个对象自己算出来的读数"，如「12.3 cm」）。空串表示不显示。</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>可编辑的数值项（顺序即显示顺序）。</summary>
    public IReadOnlyList<GfxParameterField> Fields { get; init; } = System.Array.Empty<GfxParameterField>();

    /// <summary>可编辑的文本项（顺序即显示顺序）；空列表表示没有文本项（默认）。</summary>
    /// <remarks>
    /// <b>契约只加不改</b>：带默认值的新属性 —— 旧插件构造 <see cref="GfxParameterPanel"/>
    /// 时不写 TextFields 就是空列表，面板与从前长得一模一样。
    /// </remarks>
    public IReadOnlyList<GfxTextField> TextFields { get; init; } = System.Array.Empty<GfxTextField>();

    /// <summary>面板里放不下的补充说明（例如"这个参数改动会重建几何"）；空串表示不显示。</summary>
    public string Note { get; init; } = "";
}
