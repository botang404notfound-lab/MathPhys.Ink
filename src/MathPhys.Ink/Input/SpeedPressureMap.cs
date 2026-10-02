namespace MathPhys.Ink.Input;

/// <summary>笔锋强度档位。</summary>
/// <remarks>
/// 三档不是"三套宽度区间"，而是<b>同一条速度曲线的三种陡峭度</b> ——
/// 区间固定（见 <see cref="SpeedPressureMap.MinFactor"/> 的说明），
/// 档位只改"对速度有多敏感"。这样三档都稳稳落在 WPF 压感映射的有效范围内，
/// 不存在"选了强档却因为被夹住而毫无区别"。
/// </remarks>
public enum CraftStrength
{
    /// <summary>弱：行笔变化平缓，接近等宽，只在快写时略微变细。</summary>
    Weak = 0,

    /// <summary>中（出厂默认）：快写明显变细，慢写接近满宽。</summary>
    Medium = 1,

    /// <summary>强：对速度最敏感，起收笔与转折的粗细对比最大。</summary>
    Strong = 2,
}

/// <summary>
/// 速度 → 笔锋宽度的映射 —— <b>纯函数，无状态、无 UI、无 RTI 依赖</b>。
/// </summary>
/// <remarks>
/// <b>为什么是"速度"而不是"压感"</b>：
/// <list type="bullet">
/// <item>一体机上是红外触摸框 + 被动笔，设备很可能<b>根本不报压力</b>
/// （<c>InputDiagnostics.NoteTouch</c> 里已经记着"驱动不报尺寸"这一类事实）；</item>
/// <item>而<b>速度永远算得出来</b> —— 只要有点和时间戳。</item>
/// </list>
/// <para>
/// <b>为什么返回的是"压力因子"而不是"宽度"</b>：因为 WPF 的压感管线是现成的、
/// 不可自定义曲线的（见 <c>PressurePolicy</c> 的说明）。与其去改曲线，
/// 不如<b>在数据源那一端改</b>：把速度映射成 <c>PressureFactor</c> 写回采样点，
/// 于是实时墨迹（DynamicRenderer）与最终笔画（Stroke）都用同一批因子，
/// 而 <c>PressureFactor</c> 是 <c>StylusPoint</c> 的标准属性，ISF 原生保存
/// ⇒ <c>.tbink</c> / <c>.twb</c> 一行都不用改。
/// </para>
/// <para>
/// <b>值域为什么卡在 [0.5, 1.0]</b>：WPF 按压力因子线性缩放笔宽，而它的映射下限
/// 大约在 <c>Width × 0.5</c>。把下限设得比 0.5 更低，那些值会被静默夹住 ——
/// 表现是"强档和弱档看起来一样"，这种失败方式最难查（不报错、只是没效果）。
/// 所以三档共用一个安全区间，靠曲线形状区分。
/// </para>
/// </remarks>
public static class SpeedPressureMap
{
    /// <summary>速度映射的宽度下限（见类型说明：WPF 压感映射的有效下限）。</summary>
    public const double MinFactor = 0.5;

    /// <summary>速度映射的宽度上限 = 满宽。</summary>
    public const double MaxFactor = 1.0;

    /// <summary>
    /// "中速"参考点，单位 world / 毫秒。
    /// </summary>
    /// <remarks>
    /// 取 0.08：一个约 10pt 见方的字，一秒写三个，约 0.03 world/ms；
    /// 连笔快写可达 0.2~0.4 world/ms。所以 0.08 落在"正常写字"与"快写"之间，
    /// 曲线的拐点就在这里。
    /// <para>
    /// ★ 这个常量该由<b>真机数据</b>校准 —— S1 的采样日志会给出真实的平均/最大速度
    /// （见 <c>InputDiagnostics</c> 的「M11 笔采样」段）。若实测速度整体偏大或偏小，
    /// 只需要改这一个数。
    /// </para>
    /// </remarks>
    public const double ReferenceSpeed = 0.08;

    /// <summary>
    /// 宽度平滑系数（0~1，越大越跟手、越小越平滑）。
    /// </summary>
    /// <remarks>
    /// 必须做低通：单点速度会因为采样抖动而剧烈跳动，直接用会让笔迹像锯齿。
    /// 0.35 表示"新值占三成半"—— 既跟得上真实的加/减速，又压掉了单点毛刺。
    /// </remarks>
    public const double SmoothAlpha = 0.35;

    /// <summary>档位对应的曲线指数（越小越平缓）。</summary>
    public static double Exponent(CraftStrength strength) => strength switch
    {
        CraftStrength.Weak => 0.35,
        CraftStrength.Strong => 1.15,
        _ => 0.70,
    };

    /// <summary>档位的中文名（界面提示与日志共用）。</summary>
    public static string Name(CraftStrength strength) => strength switch
    {
        CraftStrength.Weak => "弱",
        CraftStrength.Strong => "强",
        _ => "中",
    };

    /// <summary>把档位序号（界面按钮的 <c>Tag</c>）夹到合法范围。</summary>
    public static CraftStrength FromIndex(int index)
        => index switch
        {
            <= 0 => CraftStrength.Weak,
            >= 2 => CraftStrength.Strong,
            _ => CraftStrength.Medium,
        };

    /// <summary>
    /// 速度 → 压力因子（宽度比例）。
    /// </summary>
    /// <param name="speed">瞬时速度，单位 world / 毫秒。<b>必须 ≥ 0</b>（负值按 0 处理）。</param>
    /// <param name="strength">笔锋强度档位。</param>
    /// <returns>区间 <c>[0.5, 1.0]</c> 内的压力因子：<b>越慢越接近满宽，越快越细</b>。</returns>
    /// <remarks>
    /// 形状是"慢度"的幂函数：<c>slow = 1 / (1 + v / v0)</c> 把 [0, ∞) 压到 (0, 1]，
    /// 再取幂。指数的意义就在"多快掉下去"：弱档掉得慢（长时间贴着满宽），强档掉得快。
    /// </remarks>
    public static double Width01(double speed, CraftStrength strength)
    {
        if (double.IsNaN(speed) || speed < 0) speed = 0;

        double slow = 1.0 / (1.0 + speed / ReferenceSpeed);
        double shaped = Math.Pow(slow, Exponent(strength));

        // 夹一次：Math.Pow 在极端输入下可能给出极小的次正规数，让它稳稳落在区间内
        double factor = MinFactor + (MaxFactor - MinFactor) * shaped;
        return factor < MinFactor ? MinFactor : factor > MaxFactor ? MaxFactor : factor;
    }

    /// <summary>宽度低通：新值按 <see cref="SmoothAlpha"/> 权重并入上一次的结果。</summary>
    public static double Smooth(double previous, double target)
        => previous + SmoothAlpha * (target - previous);

    /// <summary>
    /// 速度因子与设备原始压力的合成律。
    /// </summary>
    /// <remarks>
    /// 用<b>几何平均</b>而不是取小/取大：
    /// <list type="bullet">
    /// <item>取小 ⇒ 两个因子各掉一点，合起来细得离谱；</item>
    /// <item>取大 ⇒ 其中一路失效就完全看不出来（等于白开）；</item>
    /// <item>几何平均 ⇒ 两路都参与、都不极端，任一路为满宽时结果等于另一路。</item>
    /// </list>
    /// 只在用户<b>同时</b>开了压感与笔锋时才用得上；只开笔锋时纯用速度因子
    /// （一体机上压感常常不存在，见类型说明）。
    /// </remarks>
    public static double Blend(double craft, double rawPressure)
    {
        if (craft <= 0 || rawPressure <= 0) return craft;
        return Math.Sqrt(craft * rawPressure);
    }
}
