// StylusPoint / StylusPointCollection 属于 System.Windows.Input（不是 System.Windows.Ink）
using System.Windows.Input;

namespace MathPhys.Ink.Ink;

/// <summary>
/// 压感的现实约束：<b>不是所有设备都真的报压力</b>。
/// </summary>
/// <remarks>
/// WPF 在 <c>DrawingAttributes.IgnorePressure = false</c> 时按压力因子线性缩放笔宽，
/// 这套映射是内建的、<b>不可自定义曲线</b>（要自定义就得在笔画落下后重建几何，
/// 那正是"墨迹几何永不重算"要避免的事）。
/// <para>
/// 真正的坑在于：鼠标、以及一部分不报压力的电容笔，压力因子恒为 <b>0.5</b>。
/// 于是"开了压感"在这些设备上的表现不是"轻写细重写粗"，
/// 而是<b>整块笔迹统一变成一半粗</b> —— 老师只会觉得"这软件怎么变细了"。
/// </para>
/// <para>
/// 所以这里提供一条判据：一根笔画的压力样本若<b>全等</b>，就认定该设备没有压感，
/// 把这一根笔画按"无压感"重设属性（只改 <c>DrawingAttributes</c>，不动几何）。
/// </para>
/// <para>
/// <b>M11 补充</b>：笔锋开启之后，采样点里的压力因子是<b>我们伪造的</b>（由行笔速度映射而来，
/// 见 <c>SpeedPressureMap</c>），而伪造值通常逐点变化 ⇒ 上面这条判据自然判为"有压感"，
/// 兜底不会误伤笔锋。唯一会命中兜底的情形是"伪造值恰好恒定" ——
/// 一个孤零零的墨点、或一段笔直匀速的极短笔画 —— 那时退化成恒定笔宽本来就是对的。
/// </para>
/// </remarks>
public static class PressurePolicy
{
    /// <summary>判定"恒定"时允许的压力浮点误差。</summary>
    private const float Epsilon = 1e-4f;

    /// <summary>
    /// 压力是否恒定（= 该设备没有压感，或压力信息没被报上来）。
    /// </summary>
    /// <remarks>
    /// 样本不足两个时返回 <c>true</c>：宁可当成"无压感"也不要凭空应用压感 ——
    /// 点一下留下的一个墨点本来就不该有粗细变化。
    /// </remarks>
    public static bool IsPressureConstant(StylusPointCollection? points)
    {
        if (points is null || points.Count < 2) return true;

        float first = points[0].PressureFactor;
        for (int i = 1; i < points.Count; i++)
        {
            if (Math.Abs(points[i].PressureFactor - first) > Epsilon) return false;
        }
        return true;
    }

    /// <summary>
    /// 这根笔画是否应当忽略压力（= 按恒定笔宽画）。
    /// </summary>
    /// <param name="pen">当前笔属性。</param>
    /// <param name="points">该笔画的原始采样点。</param>
    public static bool ShouldIgnorePressure(PenProfile pen, StylusPointCollection? points)
        => pen.IgnorePressure || IsPressureConstant(points);
}
