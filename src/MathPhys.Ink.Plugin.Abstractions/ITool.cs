using System.Windows.Input;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 一个画布工具。宿主只认这个接口，<b>加工具不需要改宿主</b>。
/// </summary>
/// <remarks>
/// 两个布尔属性决定了宿主的行为，规则只有一条，记住它就不会错：
/// <code>
/// InkLayer.IsHitTestVisible   = UsesInkLayer
/// IsManipulationEnabled       = !UsesInkLayer &amp;&amp; !NeedsPointer
/// </code>
/// 四个组合分别是：
/// <list type="bullet">
/// <item>笔 / 橡皮：<c>UsesInkLayer=true, NeedsPointer=false</c> ⇒ 墨迹层接输入，Manipulation 关
/// （触摸被提升成笔事件，于是触摸屏上直接能写字）；</item>
/// <item>手：<c>false, false</c> ⇒ 墨迹层退出命中，Manipulation 开（单指平移 + 双指捏合）；</item>
/// <item>插件工具（如直尺）：<c>false, true</c> ⇒ 墨迹层退出命中，Manipulation 也关，
/// 指针事件转发给工具自己处理。</item>
/// </list>
/// <para>
/// <b>为什么不能用 <c>e.Handled</c> 去"抑制输入"</b>：M4.2 的一体机实测证明，
/// <c>StylusEventArgs.Handled</c> 挡得住 UI 线程的正式采集，却挡不住跑在笔输入线程上的
/// <c>DynamicRenderer</c> —— 于是墨迹看得见、抬笔就消失；而且半途打断会泄漏输入捕获，
/// 把整台机器后续所有触摸都锁死。所以"这一段不采集"一律用
/// <see cref="UsesInkLayer"/>=false 来表达，让输入根本不路由到墨迹层。
/// </para>
/// </remarks>
public interface ITool
{
    /// <summary>稳定标识（小写英文，如 <c>pen</c> / <c>ruler</c>）。注册表按它去重与查找。</summary>
    string Id { get; }

    /// <summary>按钮上显示的名字。</summary>
    string DisplayName { get; }

    /// <summary>按钮提示（一体机上鼠标悬停很少用，但本机调试时很有用）。</summary>
    string ToolTip { get; }

    /// <summary>单键快捷键；<c>null</c> 表示不参与快捷键分发。</summary>
    Key? Shortcut { get; }

    /// <summary>是否让墨迹层接输入（笔 / 橡皮为 true）。</summary>
    bool UsesInkLayer { get; }

    /// <summary>是否需要宿主把指针事件转发给 <see cref="OnPointer"/>。</summary>
    bool NeedsPointer { get; }

    /// <summary>笔输入分类（<b>只看工具，不看设备类型</b>）。</summary>
    ToolInputKind InputKind { get; }

    /// <summary>墨迹层采集模式（仅在 <see cref="UsesInkLayer"/> 为 true 时有意义）。</summary>
    ToolInkMode InkMode { get; }

    /// <summary>光标；<c>null</c> 表示沿用宿主默认。</summary>
    Cursor? Cursor { get; }

    /// <summary>被选中。工具在这里拿宿主的能力（<see cref="IToolContext"/>），做初始化。</summary>
    void Activate(IToolContext context);

    /// <summary>
    /// 被切走。
    /// </summary>
    /// <remarks>
    /// <b>拖动中被切走也必须被调用</b>（宿主保证）。工具要在这里清掉预览、复位内部状态 ——
    /// 否则残留的预览图元会一直挂在画布上，且下一次拖动的起点还是上一次的。
    /// 这与 M4.2 的"输入捕获悬空"是同一类问题。
    /// </remarks>
    void Deactivate();

    /// <summary>指针事件回调；仅在 <see cref="NeedsPointer"/> 为 true 时会被调用。</summary>
    void OnPointer(ToolPointer pointer);
}
