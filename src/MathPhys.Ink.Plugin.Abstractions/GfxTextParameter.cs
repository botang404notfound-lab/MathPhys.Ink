namespace MathPhys.Ink.Plugins;

/// <summary>
/// 参数面板里<b>一个可编辑的文本项</b>的描述（纯数据，不含任何 WPF 控件）。
/// </summary>
/// <remarks>
/// ★ <b>为什么是新类，而不是给 <see cref="GfxParameterField"/> 加字符串重载</b>：
/// 数值字段的 Value/Min/Max/Step 全是 double，「不限制范围」靠 Min==Max 特判；
/// 文本没有范围与步长的概念，硬塞进数值类会让每个文本项都拖着三个死字段。
/// 独立类没有死状态。
/// <para>
/// 与 <see cref="GfxParameterField"/> 同一条纪律：返回<b>数据</b>而不是控件 ——
/// 宿主决定长什么样（单行/多行、提交时机），插件只回答「有哪些文本、现在是什么」，
/// harness 里不靠窗口就能断言「表达式项在不在、取值与对象存档一不一致」。
/// </para>
/// <para>
/// <b>契约只加不改</b>：本类与 <see cref="IGfxTextParameterSink"/> 都是纯新增 ——
/// 不实现它们的旧插件（量角器 / 直尺 / 坐标系……）照常加载，面板上只是没有可编辑文本项。
/// </para>
/// </remarks>
public sealed class GfxTextField
{
    /// <summary>参数键（读写都走它，与 <see cref="IGfxObjectRef.GetText"/> 同键）。</summary>
    public string Key { get; init; } = "";

    /// <summary>面板上显示的名字（「表达式」「LaTeX」……）。</summary>
    public string Label { get; init; } = "";

    /// <summary>当前值（对象存档里的值；空串表示尚未设置）。</summary>
    public string Value { get; init; } = "";

    /// <summary>是否只读（显示但改不了）。</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// 是否按多行显示（LaTeX 长，单行会截断）。
    /// 宿主据此决定换行与框高；多行时回车是打字换行，提交走「应用」按钮。
    /// </summary>
    public bool IsMultiline { get; init; }
}

/// <summary>
/// 插件把「用户改了一段文本」回传给宿主的方式。
/// </summary>
/// <remarks>
/// <see cref="IGfxParameterProvider"/> 管「显示什么」，<see cref="IGfxParameterSink"/> 管
/// 「改了数值怎么办」，本接口管「改了文本怎么办」—— 三路平行。
/// <para>
/// <b>为什么是新接口而不是给 <see cref="IGfxParameterSink"/> 加方法</b>：
/// 给已有接口加成员，会让所有已实现它的旧插件 dll 在加载时满足不了接口
/// （TypeLoadException）；新接口则完全没这个问题 —— 宿主用 is 判断，
/// 没实现就显示成「能看不能改」。
/// </para>
/// <para>
/// <b>必须实例实现，禁止 static 缓存</b>（与 <see cref="IGfxParameterSink"/> 同一条纪律）。
/// </para>
/// </remarks>
public interface IGfxTextParameterSink
{
    /// <summary>
    /// 应用一次文本修改。
    /// </summary>
    /// <param name="obj">被改的对象。</param>
    /// <param name="key">参数键（与面板上该文本项的 <see cref="GfxTextField.Key"/> 一致）。</param>
    /// <param name="text">新内容（已 trim；「没变 / 只读」由宿主判断，到不了这里）。</param>
    /// <returns>
    /// 真正的改动（合并进对象存档的那一份；一次修改可连带多个键）；
    /// 返回 null 表示「这次不改」（内容为空、不合法等）。
    /// </returns>
    /// <remarks>
    /// 出错时返回 null，不要抛异常。
    /// 对象存档会按新文本重新渲染：渲染器必须能接住「坏文本」并退化成占位
    /// （函数是红框、公式是虚线框），而不是让一个坏表达式带走整块白板。
    /// </remarks>
    IReadOnlyDictionary<string, string>? ApplyText(IGfxObjectRef obj, string key, string text);
}
