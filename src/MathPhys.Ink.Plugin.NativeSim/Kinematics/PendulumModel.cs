using System;

namespace MathPhys.Ink.Plugin.NativeSim.Kinematics;

/// <summary>
/// 单摆（理想摆：轻杆 + 质点），含粘性阻尼。
/// </summary>
/// <remarks>
/// <para>
/// 运动方程（<c>θ</c> = 偏离竖直方向的角度，rad；单位质量）：
/// <code>θ'' = −(g / L)·sin θ − b·θ'</code>
/// </para>
/// <para>
/// <b>为什么要真解这个方程，而不是画一条正弦曲线。</b>物理课真正的看点恰恰在"小角近似之外会怎样"：
/// 摆角一大，<b>周期就变长</b>（<c>T ≈ T₀·(1 + θ₀²/16 + …)</c>），而任何正弦曲线永远给出同一个周期。
/// 用四阶龙格-库塔真解，"大摆角周期更长"在屏幕上是看得见的，数值上也是可断言的。
/// </para>
/// <para>
/// <b>单位一律国际单位制</b>（m / s / rad），像素换算只在绘图时做。
/// 这样"数值对不对"可以拿<b>解析解</b>（小角）与<b>守恒量</b>（无阻尼时的机械能）直接断言，
/// 不必和界面纠缠。
/// </para>
/// </remarks>
public sealed class PendulumModel
{
    /// <summary>摆长下限（m）。</summary>
    public const double MinLength = 0.2;

    /// <summary>摆长上限（m）。</summary>
    public const double MaxLength = 3.0;

    /// <summary>重力加速度下限（m/s²）—— 让它能演示月球（1.6）与木星（24.8 被上限裁到 20）。</summary>
    public const double MinGravity = 1.0;

    /// <summary>重力加速度上限（m/s²）。</summary>
    public const double MaxGravity = 20.0;

    /// <summary>阻尼下限（1/s）：0 = 理想摆，能量守恒。</summary>
    public const double MinDamping = 0.0;

    /// <summary>阻尼上限（1/s）：够大到"一两下就停下"，再多就没教学意义了。</summary>
    public const double MaxDamping = 1.0;

    /// <summary>初始摆角下限（度）。</summary>
    public const double MinStartAngleDegrees = 1.0;

    /// <summary>初始摆角上限（度）：90° 是"摆到水平"，再大就不是单摆了。</summary>
    public const double MaxStartAngleDegrees = 89.0;

    private double _length = 1.0;
    private double _gravity = 9.8;
    private double _damping;
    private double _startAngleDegrees = 20.0;

    /// <summary>摆长（m）。</summary>
    public double Length
    {
        get => _length;
        set => _length = Clamp(value, MinLength, MaxLength);
    }

    /// <summary>重力加速度（m/s²）。</summary>
    public double Gravity
    {
        get => _gravity;
        set => _gravity = Clamp(value, MinGravity, MaxGravity);
    }

    /// <summary>粘性阻尼系数（1/s）。</summary>
    public double Damping
    {
        get => _damping;
        set => _damping = Clamp(value, MinDamping, MaxDamping);
    }

    /// <summary>初始摆角（度）。改它只影响<b>下一次重置</b>，不会打断正在进行的运动。</summary>
    public double StartAngleDegrees
    {
        get => _startAngleDegrees;
        set => _startAngleDegrees = Clamp(value, MinStartAngleDegrees, MaxStartAngleDegrees);
    }

    /// <summary>当前摆角（rad，偏离竖直方向，正 = 屏幕上的顺时针）。</summary>
    public double Angle { get; private set; }

    /// <summary>当前角速度（rad/s）。</summary>
    public double AngularVelocity { get; private set; }

    /// <summary>已经过的时间（s）。</summary>
    public double Time { get; private set; }

    /// <summary>小角近似的周期（s）：<c>T₀ = 2π·√(L/g)</c>。</summary>
    /// <remarks>
    /// 它是给学生看的"理论值"，与 <see cref="EstimatePeriod"/> 实测值并排显示 ——
    /// 两者之间的差就是"大摆角时周期变长"这件事的量化。
    /// </remarks>
    public double SmallAnglePeriod => 2.0 * Math.PI * Math.Sqrt(Length / Gravity);

    /// <summary>小角近似的角频率（rad/s）。</summary>
    public double AngularFrequency => Math.Sqrt(Gravity / Length);

    /// <summary>
    /// 单位质量的机械能（J/kg）：<c>½·L²·θ'² + g·L·(1 − cos θ)</c>。
    /// </summary>
    /// <remarks>
    /// 取"最低点"为零势能面。无阻尼时它必须守恒 —— 这是数值积分唯一诚实的自证，
    /// 比"看着像在摆"强得多。
    /// </remarks>
    public double SpecificEnergy
    {
        get
        {
            double speed = Length * AngularVelocity;
            return 0.5 * speed * speed + Gravity * Length * (1.0 - Math.Cos(Angle));
        }
    }

    /// <summary>摆球沿切向的速率（m/s）。</summary>
    public double TangentialSpeed => Length * AngularVelocity;

    /// <summary>摆球相对最低点的高度（m）。</summary>
    public double Height => Length * (1.0 - Math.Cos(Angle));

    /// <summary>摆球离最低点的水平偏移（m，向右为正）。</summary>
    public double Offset => Length * Math.Sin(Angle);

    /// <summary>
    /// 当前角加速度（rad/s²）＝ <c>−(g/L)·sin θ − b·θ'</c>。
    /// </summary>
    public double AngularAcceleration
        => -(Gravity / Length) * Math.Sin(Angle) - Damping * AngularVelocity;

    /// <summary>
    /// 回到初始状态（摆到 <see cref="StartAngleDegrees"/>、速度为零、时间归零）。
    /// </summary>
    public void Reset(double? startAngleDegrees = null)
    {
        if (startAngleDegrees is { } degrees) StartAngleDegrees = degrees;

        Angle = ToRadians(StartAngleDegrees);
        AngularVelocity = 0;
        Time = 0;
    }

    /// <summary>推进一步（单个 RK4 步）。</summary>
    public void Step(double dt)
    {
        if (dt <= 0) return;

        double y = Angle;
        double v = AngularVelocity;
        Rk4.Step(AccelerationAt, Time, dt, ref y, ref v);

        Angle = y;
        AngularVelocity = v;
        Time += dt;
    }

    /// <summary>
    /// 推进一段真实时间，内部拆成不超过 <paramref name="maxStep"/> 的小步。
    /// </summary>
    /// <remarks>
    /// 动画帧间隔是不定的（关键看于显卡与负载），而 RK4 的精度依赖步长。
    /// 直接拿帧间隔当步长，会出现"卡一下之后摆突然跳了一大截"——那不只是难看，是<b>算错了</b>。
    /// 拆小步的代价可以忽略（一帧最多几十步）。
    /// </remarks>
    public void Advance(double dt, double maxStep = 1e-3)
    {
        if (dt <= 0) return;

        double step = Clamp(maxStep, 1e-5, 0.05);

        // 上限兜底：窗口被最小化一分钟后恢复，别真的去跑几万步
        int steps = (int)Math.Min(Math.Ceiling(dt / step), 20000);
        double each = dt / steps;

        for (int i = 0; i < steps; i++) Step(each);
    }

    /// <summary>
    /// 用数值积分<b>实测</b>周期（过零法）。
    /// </summary>
    /// <param name="dt">积分步长（秒）。默认 0.2 ms —— 周期的相对误差远小于 1%，够画读数用。</param>
    /// <param name="halfCycles">数几个半周期再取平均（越多越稳）。</param>
    /// <returns>实测周期（s）。摆不动（初始角为 0）时退回小角近似值。</returns>
    /// <remarks>
    /// <b>不改自身状态</b>：在一个副本上跑。否则每帧刷新读数都会把摆"偷偷挪走"。
    /// </remarks>
    public double EstimatePeriod(double dt = 2e-4, int halfCycles = 8)
    {
        int needed = Math.Max(1, halfCycles);

        var probe = new PendulumModel
        {
            Length = Length,
            Gravity = Gravity,
            Damping = Damping,
        };
        probe.Reset(StartAngleDegrees);

        // 步数上限：按小角周期给 4 倍余量。有它，参数取到离谱的组合也不会把界面卡死。
        long limit = (long)(SmallAnglePeriod / dt * (needed + 2) * 4) + 1000;
        double previous = probe.Angle;
        double? lastCrossing = null;
        double total = 0;
        int counted = 0;

        for (long i = 0; i < limit && counted < needed; i++)
        {
            probe.Step(dt);

            bool crossed = (previous < 0 && probe.Angle >= 0) || (previous > 0 && probe.Angle <= 0);
            previous = probe.Angle;

            if (!crossed) continue;

            if (lastCrossing is { } earlier)
            {
                total += probe.Time - earlier;
                counted++;
            }

            lastCrossing = probe.Time;
        }

        return counted == 0 ? SmallAnglePeriod : 2.0 * total / counted;
    }

    /// <summary>
    /// 大摆角周期的一阶修正（<c>T ≈ T₀·(1 + θ₀²/16)</c>），给界面当"理论参考"用。
    /// </summary>
    public double CorrectedPeriod
    {
        get
        {
            double amplitude = ToRadians(StartAngleDegrees);
            return SmallAnglePeriod * (1.0 + amplitude * amplitude / 16.0);
        }
    }

    private double AccelerationAt(double t, double y, double v)
        => -(Gravity / Length) * Math.Sin(y) - Damping * v;

    internal static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    internal static double Clamp(double value, double low, double high)
        => double.IsNaN(value) ? low : Math.Clamp(value, low, high);
}
