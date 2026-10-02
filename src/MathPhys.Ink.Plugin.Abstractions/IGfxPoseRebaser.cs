using System.Collections.Generic;
using System.Windows;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 可选能力："拖动我这个对象的<b>位姿变化</b>可以折算进我的<b>参数</b>"（M12 函数参数联动）。
/// </summary>
/// <remarks>
/// <para>
/// 背景：函数曲线绑定在坐标系上（<c>Texts[bindTo]</c>），拖曲线的位姿变化默认会被宿主
/// <b>转嫁给坐标系</b>（整个图一起挪）。但对函数来说老师真正想要的是
/// "曲线跟着手指走、坐标轴原地不动" —— 也就是把平移量折回 <c>a/b/h/k</c> 这类参数。
/// </para>
/// <para>
/// 分工：<b>语义在插件</b>（只有渲染器知道自己的参数是什么意思），
/// <b>时机在宿主</b>（<c>GfxObjectStore.UpdatePose</c> 在搬运前先问一句渲染器）。
/// 宿主给出的 <paramref name="worldDelta"/> 是"这一帧想让对象整体平移多少（世界单位）"
/// （<b>增量</b>：宿主负责把"绝对中心"折成逐帧增量，见 <c>BeginRebaseDrag</c>），
/// 实现方用<b>自己的位姿逆矩阵</b>把它换回本地/数学位移 —— 与 y 翻转等约定无关，天然正确。
/// </para>
/// <para>
/// 返回 <c>false</c>（不是平移可解的形式、表达式解析失败…）时宿主照旧走"转嫁给绑定目标"的老路。
/// <b>只加新接口</b>：旧渲染器不实现它就完全不受影响。
/// </para>
/// </remarks>
public interface IGfxPoseRebaser
{
    /// <summary>
    /// 尝试把一帧平移折进参数。返回 <c>true</c> 时 <paramref name="numbers"/> / <paramref name="texts"/>
    /// 是要<b>合并</b>进对象的参数增量（宿主负责写入、重算尺寸并让视觉重建）；
    /// 返回 <c>false</c> 表示这次平移不折算。
    /// </summary>
    /// <remarks>
    /// 为什么有 <paramref name="texts"/>：表达式改写（如 <c>x^2</c> 拖出顶点后变成
    /// <c>(x-0.5)^2</c>）与数值参数一样都是"折算"的产出，只给 numbers 表达不了它。
    /// </remarks>
    bool TryRebase(IGfxObjectRef obj, Vector worldDelta,
                   out IReadOnlyDictionary<string, double> numbers,
                   out IReadOnlyDictionary<string, string> texts);
}
