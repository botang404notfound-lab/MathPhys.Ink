namespace MathPhys.Ink.Plugins;

/// <summary>
/// 一次笔输入该被当作什么 —— 笔输入分工决策的<b>唯一输入</b>。
/// </summary>
/// <remarks>
/// 它取代了旧枚举 <c>BoardTool</c>。之所以要换，是因为旧枚举把
/// 「这是什么工具」与「这次输入怎么处理」混在一起，加一个工具就得改决策函数；
/// 现在工具只需要声明自己属于哪一类输入，决策函数对它一无所知。
/// <para>
/// 特别注意这里<b>没有</b>设备类型（笔 / 手指 / 鼠标）这一维：M4.2 的一体机实测证明，
/// 红外触摸框上被动笔与手指在系统看来<b>同为 Touch</b>，按设备类型区分只会误伤。
/// 所以分工只按"当前工具"来定。
/// </para>
/// </remarks>
public enum ToolInputKind
{
    /// <summary>不落墨也不擦除（手工具、以及所有自己画图元的插件工具）。</summary>
    None,

    /// <summary>落墨（笔尖按下即书写；笔尾反转时转为擦除）。</summary>
    Ink,

    /// <summary>擦除（橡皮工具）。</summary>
    Erase,
}

/// <summary>
/// 工具希望墨迹层处于哪种采集模式。
/// </summary>
/// <remarks>
/// 刻意不直接用 WPF 的 <c>InkCanvasEditingMode</c>：契约一旦引用它，
/// 将来宿主换采集实现（例如 M4.1 预留的 <c>WM_POINTER</c> 路线）就会波及所有插件。
/// 这里只表达意图，由宿主翻译成具体实现。
/// </remarks>
public enum ToolInkMode
{
    /// <summary>不采集（手工具、插件工具）。</summary>
    None,

    /// <summary>采集墨迹。</summary>
    Ink,

    /// <summary>按点擦除。</summary>
    EraseByPoint,
}

/// <summary>指针事件的阶段。</summary>
public enum ToolPointerPhase
{
    Down,
    Move,
    Up,
}
