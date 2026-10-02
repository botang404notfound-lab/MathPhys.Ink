using System;

namespace MathPhys.Ink.Plugin.NativeSim.Kinematics;

/// <summary>
/// 弹簧振子（水平/竖直都适用：只关心"相对平衡位置的位移"），含粘性阻尼。
/// </summary>
/// <remarks>
/// <para>
/// 运动方程（<c>x</c> = 相对平衡位置的位移，m，沿重力方向为正）：
/// <code>x'' = −(k / m)·x − (b / m)·x'</code>
/// </para>
/// <para>
/// <b>它有精确解，这正是它值得上屏幕的理由。</b>无阻尼时
/// <c>x(t) = x₀·cos(ωt) + (v₀/ω)·sin(ωt)</c>，于是屏幕上那条曲线<b>可以被断言</b>；
/// 而且它把"<b>简谐运动的周期与振幅无关</b>"这件事演示得干干净净 ——
/// 同一个 k、m，把振幅从 2 cm 拉到 50 cm，周期一动不动。
/// 这一点单摆做不到（单摆是"小角才近似简谐"），两个模型并排看，区别就出来了。
/// </para>
/// <para>
/// 竖直悬挂时还有一个额外的好性质：平衡位置比自然长度多伸长 <c>Δ = mg/k</c>，
/// 而<b>振动周期与 Δ 无关</b>。界面上把这条虚线画出来，顺手就是一道常见题的图解。
/// </para>
/// </remarks>
public sealed class SpringModel
{
    /// <summary>质量下限（kg）。</summary>
    public const double MinMass = 0.1;

    /// <summary>质量上限（kg）。</summary>
    public const double MaxMass = 5.0;

    /// <summary>劲度系数下限（N/m）。</summary>
    public const double MinStiffness = 1.0;

    /// <summary>劲度系数上限（N/m）。</summary>
    public const double MaxStiffness = 100.0;

    /// <summary>阻尼下限（N·s/m）：0 = 理想振子，能量守恒。</summary>
    public const double MinDamping = 0.0;

    /// <summary>阻尼上限（N·s/m）。</summary>
    public const double MaxDamping = 3.0;

    /// <summary>初始位移下限（m，绝对值）。太小就看不出在动。</summary>
    public const double MinStartDisplacement = 0.02;

    /// <summary>初始位移上限（m，绝对值）。</summary>
    public const double MaxStartDisplacement = 0.6;

    private double _mass = 1.0;
    private double _stiffness = 20.0;
    private double _damping;
    private double _startDisplacement = 0.25;

    /// <summary>质量（kg）。</summary>
    public double Mass
    {
        get => _mass;
        set => _mass = PendulumModel.Clamp(value, MinMass, MaxMass);
    }

    /// <summary>劲度系数（N/m）。</summary>
    public double Stiffness
    {
        get => _stiffness;
        set => _stiffness = PendulumModel.Clamp(value, MinStiffness, MaxStiffness);
    }

    /// <summary>粘性阻尼系数（N·s/m）。</summary>
    public double Damping
    {
        get => _damping;
        set => _damping = PendulumModel.Clamp(value, MinDamping, MaxDamping);
    }

    /// <summary>重力加速度（m/s²）—— 只用来算竖直悬挂的静伸长。</summary>
    public double Gravity { get; set; } = 9.8;

    /// <summary>初始位移（m，绝对值）。改它只影响下一次重置。</summary>
    public double StartDisplacement
    {
        get => _startDisplacement;
        set => _startDisplacement = PendulumModel.Clamp(
            Math.Abs(value), MinStartDisplacement, MaxStartDisplacement);
    }

    /// <summary>当前位移（m，相对平衡位置，向下为正）。</summary>
    public double Displacement { get; private set; }

    /// <summary>当前速度（m/s）。</summary>
    public double Velocity { get; private set; }

    /// <summary>已经过的时间（s）。</summary>
    public double Time { get; private set; }

    /// <summary>固有角频率（rad/s）：<c>ω = √(k/m)</c>。</summary>
    public double Omega => Math.Sqrt(Stiffness / Mass);

    /// <summary>
    /// 周期（s）：<c>T = 2π·√(m/k)</c>。
    /// </summary>
    /// <remarks>
    /// ★ 无阻尼时它是<b>精确值</b>（不是近似）—— 与振幅、与重力都无关。
    /// 界面上"实测周期"必须与它吻合到小数点后好几位，那是无阻尼简谐运动该有的样子。
    /// </remarks>
    public double Period => 2.0 * Math.PI / Omega;

    /// <summary>机械能（J）：<c>½mv² + ½kx²</c>。无阻尼时守恒。</summary>
    public double Energy => 0.5 * Mass * Velocity * Velocity + 0.5 * Stiffness * Displacement * Displacement;

    /// <summary>恢复力（N，指向平衡位置）：<c>−k·x</c>。</summary>
    public double RestoringForce => -Stiffness * Displacement;

    /// <summary>加速度（m/s²）：<c>−(k·x + b·v) / m</c>。</summary>
    public double Acceleration => -(Stiffness * Displacement + Damping * Velocity) / Mass;

    /// <summary>竖直悬挂时的静伸长（m）：<c>Δ = m·g / k</c>。</summary>
    public double StaticExtension => Mass * Gravity / Stiffness;

    /// <summary>当前是欠阻尼 / 临界阻尼 / 过阻尼里的哪一种（界面用中文说明）。</summary>
    public string DampingRegime
    {
        get
        {
            double critical = 2.0 * Math.Sqrt(Stiffness * Mass);   // b_c = 2√(km)
            if (Damping <= 1e-9) return "无阻尼：能量守恒，振幅不变";
            if (Math.Abs(Damping - critical) < 1e-6) return "临界阻尼：最快回到平衡、不起振";
            return Damping < critical ? "欠阻尼：振幅逐步衰减" : "过阻尼：缓慢回到平衡、不起振";
        }
    }

    /// <summary>回到初始状态。</summary>
    public void Reset(double? startDisplacement = null)
    {
        if (startDisplacement is { } value) StartDisplacement = value;

        Displacement = StartDisplacement;
        Velocity = 0;
        Time = 0;
    }

    /// <summary>推进一步（单个 RK4 步）。</summary>
    public void Step(double dt)
    {
        if (dt <= 0) return;

        double y = Displacement;
        double v = Velocity;
        Rk4.Step(AccelerationAt, Time, dt, ref y, ref v);

        Displacement = y;
        Velocity = v;
        Time += dt;
    }

    /// <summary>推进一段真实时间，内部拆成不超过 <paramref name="maxStep"/> 的小步（同单摆的理由）。</summary>
    public void Advance(double dt, double maxStep = 1e-3)
    {
        if (dt <= 0) return;

        double step = PendulumModel.Clamp(maxStep, 1e-5, 0.05);
        int steps = (int)Math.Min(Math.Ceiling(dt / step), 20000);
        double each = dt / steps;

        for (int i = 0; i < steps; i++) Step(each);
    }

    /// <summary>
    /// 无阻尼简谐运动的<b>解析解</b>（供校验与界面对照）。
    /// </summary>
    /// <param name="t">时刻（s）。</param>
    /// <returns>该时刻的位移（m）。</returns>
    /// <remarks>
    /// 只有无阻尼时成立；有阻尼时它只是"如果没阻尼会怎样"的参考曲线，
    /// 界面上不画它，harness 用它把 RK4 的精度钉死。
    /// </remarks>
    public double AnalyticDisplacement(double t)
        => StartDisplacement * Math.Cos(Omega * t);

    /// <summary>
    /// 实测周期（过零法）。
    /// </summary>
    /// <param name="dt">积分步长（秒）。</param>
    /// <param name="halfCycles">数几个半周期再平均。</param>
    /// <remarks><b>不改自身状态</b>（在副本上跑）。</remarks>
    public double EstimatePeriod(double dt = 2e-4, int halfCycles = 8)
    {
        int needed = Math.Max(1, halfCycles);

        var probe = new SpringModel
        {
            Mass = Mass,
            Stiffness = Stiffness,
            Damping = Damping,
            Gravity = Gravity,
        };
        probe.Reset(StartDisplacement);

        long limit = (long)(Period / dt * (needed + 2) * 4) + 1000;
        double previous = probe.Displacement;
        double? lastCrossing = null;
        double total = 0;
        int counted = 0;

        for (long i = 0; i < limit && counted < needed; i++)
        {
            probe.Step(dt);

            bool crossed = (previous < 0 && probe.Displacement >= 0)
                           || (previous > 0 && probe.Displacement <= 0);
            previous = probe.Displacement;

            if (!crossed) continue;

            if (lastCrossing is { } earlier)
            {
                total += probe.Time - earlier;
                counted++;
            }

            lastCrossing = probe.Time;
        }

        return counted == 0 ? Period : 2.0 * total / counted;
    }

    private double AccelerationAt(double t, double y, double v)
        => -(Stiffness * y + Damping * v) / Mass;
}
