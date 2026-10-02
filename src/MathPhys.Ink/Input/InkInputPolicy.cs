using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Input;

/// <summary>一次笔输入该被如何处理。</summary>
public enum InkInputAction
{
    /// <summary>落墨。</summary>
    Draw,

    /// <summary>不落墨也不擦除。注意这只影响<b>我们自己的</b>行为（是否开撤销事务），不用于"拦截输入"。</summary>
    Suppress,

    /// <summary>擦除。</summary>
    Erase,
}

/// <summary>
/// 笔输入的分工决策 —— <b>纯逻辑</b>，不引用任何 UI 元素。
/// </summary>
/// <remarks>
/// <b>M4.2 起只看工具，不看设备类型。</b>这是被一体机实测推翻后的结论，值得说清楚：
/// <para>
/// M4 的决策表里有"<c>TabletDeviceType.Touch</c> ⇒ 抑制"这一条，前提是"WPF 会把手指报成 Touch、
/// 把笔报成 Stylus"。实测下来这个前提在红外触摸框一体机上<b>不成立</b> ——
/// 设备清单里只有一个 <c>name="HID Interface"</c> 的 Touch 设备，笔在系统看来就是一根手指
/// （被动笔：无压力、无反转信号）。
/// </para>
/// <para>
/// 更糟的是"抑制"这个动作本身是坏的：<c>StylusEventArgs.Handled</c>
/// 只挡得住 UI 线程上的正式采集，挡不住跑在笔输入线程上的 <c>DynamicRenderer</c>，
/// 于是"画的墨看得见、抬笔就消失"，而且会泄漏输入捕获、把后续所有触摸都锁死。
/// </para>
/// <para>
/// 所以现在改成：<b>工具即模式</b>。要"这一段不落墨"就用墨迹层退出命中
/// （输入根本不路由到墨迹层，干净且已验证有效），绝不用 <c>Handled</c>。
/// </para>
/// <para>
/// <b>M7.1 的变化</b>：入参从旧的 <c>BoardTool</c> 枚举（它把"是什么工具"与"输入怎么处理"混在一起）
/// 换成 <c>ToolInputKind</c>。于是这个函数对<b>有哪些工具</b>彻底无感 ——
/// 直尺、量角器这类插件工具一律声明 <c>None</c>，这里一行都不用改。
/// 判定空间仍是 <b>3 类输入 × 2 种反转 = 6 种</b>，与 M6 逐条等价。
/// </para>
/// </remarks>
public static class InkInputPolicy
{
    /// <summary>依据工具的输入类别与笔尾反转标志，决定这次输入是落墨、擦除还是不落墨。</summary>
    /// <param name="kind">当前工具的输入类别。</param>
    /// <param name="inverted">是否处于笔尾反转（橡皮头）状态。红外屏恒为 false。</param>
    public static InkInputAction Decide(ToolInputKind kind, bool inverted)
    {
        // 不落墨的工具（手、以及所有自己画图元的插件工具）：
        // 真正的拦截在视图侧（墨迹层退出命中测试），这里的 Suppress 只表示
        // "别开撤销事务、也别记成一次书写"。
        if (kind == ToolInputKind.None) return InkInputAction.Suppress;

        // 擦除类工具
        if (kind == ToolInputKind.Erase) return InkInputAction.Erase;

        // 落墨类工具：笔尾反转随手擦（有笔数字化仪时才可能为 true），其余落墨。
        // 这里不再判断设备类型 —— 见类型注释：红外屏上笔与手指同为 Touch，判断只会误伤。
        return inverted ? InkInputAction.Erase : InkInputAction.Draw;
    }
}
