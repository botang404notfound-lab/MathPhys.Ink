using System.Windows;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 一次指针事件。世界坐标由宿主换算好再交给工具。
/// </summary>
/// <param name="Phase">按下 / 移动 / 抬起。</param>
/// <param name="World">
/// 世界坐标（单位 = PDF point）。<b>工具一律用这个</b> ——
/// 它已经过缩放平移，所以同一道题在不同缩放下拿到的坐标完全相同。
/// </param>
/// <param name="Viewport">视口坐标（DIP）。只有需要"屏幕固定尺寸"的视觉才用它（例如手柄方块）。</param>
/// <param name="DeviceId">触点 Id；鼠标恒为 0。</param>
/// <param name="Inverted">是否笔尾反转（红外屏恒 false，见 <see cref="ToolInputKind"/> 注释）。</param>
/// <param name="ClickCount">连击次数，1 = 单击。</param>
/// <remarks>
/// 做成 <c>record</c> 是为了让工具能用 <c>with</c> 派生，也便于验收 harness 直接构造事件喂给工具
/// （本机没有触摸屏，指针链路只能这样断言）。
/// </remarks>
public sealed record ToolPointer(
    ToolPointerPhase Phase,
    Point World,
    Point Viewport,
    int DeviceId,
    bool Inverted,
    int ClickCount);
