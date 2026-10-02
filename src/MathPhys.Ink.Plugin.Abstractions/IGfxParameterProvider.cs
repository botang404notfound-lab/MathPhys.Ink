namespace MathPhys.Ink.Plugins;

/// <summary>
/// 渲染器可以<b>额外实现</b>这个接口，来让自己的对象出现在参数面板里。
/// </summary>
/// <remarks>
/// ★ <b>可选接口，不是 <see cref="IGfxObjectRenderer"/> 上的新成员</b>：
/// 契约只加不改 —— 老插件（量角器 / 直尺 / 坐标系）不实现它照样能加载，
/// 只是在面板上"没有可调参数"。宿主一律用 <c>is</c> 判断后再调，不调也不会坏。
/// <para>
/// <b>为什么挂在渲染器上而不是工具上</b>：面板是"选中某个对象之后"出现的，
/// 而"这个对象有哪些参数"只有渲染器知道（工具负责创建，创建完就切回选择工具了，
/// 此时工具早已 <c>Deactivate</c>）。挂在工具上会出现"选中一个量角器，
/// 但当前工具是选择工具，所以面板不知道该显示什么"。
/// </para>
/// <para>
/// <b>必须实例实现，禁止 static 缓存</b>：同一个渲染器类型可能被实例化多次
/// （宿主一个、harness 一个），静态字段会互相踩 —— 与
/// <c>IGfxBoardAwareRenderer</c> 踩过的坑完全一样。本接口无状态，天然规避。
/// </para>
/// </remarks>
public interface IGfxParameterProvider
{
    /// <summary>
    /// 给出该对象在参数面板里的描述；<b>没有可调参数时返回 <c>null</c></b>。
    /// </summary>
    /// <param name="obj">被选中的对象。</param>
    /// <remarks>
    /// 纯函数：只读 <paramref name="obj"/>、不改任何东西、不弹窗、不抛异常。
    /// 拿不准的一律降级成"返回 <c>null</c>"（没有面板）——
    /// 少一个面板是少个便利，"选中就崩"是故障。
    /// </remarks>
    GfxParameterPanel? BuildPanel(IGfxObjectRef obj);
}

/// <summary>
/// 参数面板把"用户改了一个值"这件事回传给插件的方式。
/// </summary>
/// <remarks>
/// <see cref="IGfxParameterProvider"/> 负责"显示什么"，本接口负责"改了怎么办"。
/// 分成两个接口是因为显示是<b>无副作用</b>的、随时会被调用（每次选中都要问一遍），
/// 而应用修改<b>有副作用</b>（改存档、触发几何重建）。混在一个接口里，
/// 迟早有人把"写盘"写进"显示"那半边。
/// </remarks>
public interface IGfxParameterSink
{
    /// <summary>
    /// 应用一个参数修改。
    /// </summary>
    /// <param name="obj">被改的对象。</param>
    /// <param name="key">参数键（与本插件渲染器用的键一致）。</param>
    /// <param name="value">新值（已按 <see cref="GfxParameterField.Snap"/> 规整）。</param>
    /// <returns>
    /// 真正的改动（合并进对象存档的那一份）；返回 <c>null</c> 表示"这次不改"。
    /// </returns>
    /// <remarks>
    /// ★ 返回"归一化之后的数值"而不是 <c>bool</c>：一次修改常常要连带改别的键
    /// （例如改三角板"斜边"要同时改两条直角边），宿主不该猜这些 ——
    /// 插件直接把最终要落盘的键值对交出来，宿主原样合并。
    /// <para>
    /// <b>返回 <c>null</c> 的正确用途</b>是"这个值不合法/没变化"，而不是"出错了" ——
    /// 出错的正确做法是返回 <c>null</c> 并在状态栏说明，绝不要抛异常。
    /// </para>
    /// </remarks>
    System.Collections.Generic.IReadOnlyDictionary<string, double>? Apply(
        IGfxObjectRef obj, string key, double value);
}
