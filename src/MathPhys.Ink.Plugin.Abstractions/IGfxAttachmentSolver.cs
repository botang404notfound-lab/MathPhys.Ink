using System.Collections.Generic;
using System.Windows;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 一次位姿解算的结果：新的中心 / 旋转角，以及（可选）要一并写回的数值参数。
/// </summary>
/// <remarks>
/// <see cref="Numbers"/> 传 <c>null</c> 表示"没有额外参数要写"。
/// 为什么要带数值参数：导线这类图形的"长度"存在 <c>Numbers</c> 里而不是 <see cref="IGfxObjectRef.LocalSize"/> 里，
/// 只解位姿不写长度，导线还是老长度 —— 两端到不了元件引脚上。
/// </remarks>
public readonly record struct GfxSolvedPose(
    Point Center,
    double RotationDegrees,
    IReadOnlyDictionary<string, double>? Numbers);

/// <summary>
/// 可选能力：渲染器声明"<b>我这个图形的位姿由画布上的别人决定</b>"。
/// </summary>
/// <remarks>
/// <para>
/// 典型例子是电路导线（M15）：它两端吸在元件的接线柱上（绑定键存在自己的
/// <c>Texts</c> 里），元件一移动，导线就得跟着走 —— 而"谁连着我、他现在在哪"
/// 只有掌握全部对象的存储层答得出来，渲染器平时只看得到"自己这一个对象"。
/// 于是宿主在每次集合 / 位姿变动后（<c>RaiseChanged</c> 的重算窗口里）把
/// <b>全部对象的只读视图</b>递进来，让渲染器自己解算 —— 宿主只认接口、不懂电路。
/// </para>
/// <para>
/// 与 <c>bindTo</c>（单目标绑定）和 <c>sumOf</c>（合力只重算尺寸）的分工同一哲学：
/// 字符串键与数字键是插件与宿主之间既有的约定形式，"怎么算"是插件的事。
/// </para>
/// <para>
/// <b>实现约定</b>：必须快（每次画布变动都会来问）、必须纯函数（不碰视觉树、不存状态、
/// 不抛事件 —— 宿主在通知路径上调用它，再抛事件就是递归）。与别人无关时立刻返回
/// <c>false</c>（导线之外的普通元件第一行就该退出去）。
/// 绑定的目标<b>缺失</b>（被删了）时按"解不了"处理：宿主保持现状，图形留在原地 ——
/// 与 bindTo 的"目标缺失不静默丢"同一语义。
/// </para>
/// <para>
/// <b>不实现它就完全没有影响</b>：宿主只在渲染器实现了本接口时才调用（is 探测，
/// 实例实现）。铁律"契约只加新接口、ITool 成员零新增"的又一次落地。
/// </para>
/// </remarks>
public interface IGfxAttachmentSolver
{
    /// <summary>
    /// 试解算 <paramref name="self"/> 的位姿。返回 <c>false</c> = 解不了（与别人无关 / 目标缺失），宿主保持现状。
    /// </summary>
    bool TrySolve(IGfxObjectRef self, IReadOnlyList<IGfxObjectRef> board, out GfxSolvedPose pose);
}
