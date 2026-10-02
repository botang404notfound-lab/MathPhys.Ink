using System;

namespace MathPhys.Ink.Plugin.NativeSim.Kinematics;

/// <summary>
/// 斜面上的滑块（含动摩擦）。
/// </summary>
/// <remarks>
/// <para>
/// 取"沿斜面向下"为正方向，则：
/// <list type="bullet">
/// <item>重力分量加速度 <c>a_g = g·sin θ</c>（永远向下）；</item>
/// <item>滑动时摩擦加速度大小 <c>a_f = µ·g·cos θ</c>，<b>方向与速度相反</b>；</item>
/// <item>于是：下滑时 <c>a = a_g − a_f</c>；上滑时 <c>a = a_g + a_f</c>。</item>
/// </list>
/// </para>
/// <para>
/// <b>三条教学上必须成立的性质</b>（也是 harness 的三条断言）：
/// <list type="number">
/// <item><c>µ = 0</c> ⇒ <c>a = g·sin θ</c>，与质量无关；</item>
/// <item><c>µ = tan θ</c>（临界角）⇒ <c>a = 0</c>：推一下匀速下滑，松手就停；</item>
/// <item><c>µ &gt; tan θ</c> ⇒ 松手<b>不动</b>（最大静摩擦足够），而不是"慢慢往下滑"。</item>
/// </list>
/// 第 3 条是最容易被写错的一条：只按 <c>a = g(sin θ − µ cos θ)</c> 硬算会得到"向上滑"这种
/// 荒谬结果，屏幕上表现为"滑块自己往坡上爬"。
/// </para>
/// <para>
/// <b>静摩擦按"最大静摩擦 ≈ 动摩擦"处理</b>（题目里的常见简化）。所以判据就是
/// <c>tan θ &gt; µ</c>：够陡就动，不够陡就停，没有"半动半不动"的中间态。
/// </para>
/// </remarks>
public sealed class InclineModel
{
    /// <summary>倾角下限（度）。</summary>
    public const double MinAngleDegrees = 5.0;

    /// <summary>倾角上限（度）。</summary>
    public const double MaxAngleDegrees = 80.0;

    /// <summary>动摩擦因数下限。</summary>
    public const double MinFriction = 0.0;

    /// <summary>动摩擦因数上限。tan 80° ≈ 5.67，取 1.2 已经能覆盖"任何角度都推不动"。</summary>
    public const double MaxFriction = 1.2;

    /// <summary>初速度上限（m/s，绝对值）。</summary>
    public const double MaxStartSpeed = 8.0;

    /// <summary>速度被视为零的阈值（m/s）。比它小就按"静止"处理。</summary>
    private const double RestEpsilon = 1e-9;

    private double _angleDegrees = 30.0;
    private double _friction = 0.2;

    /// <summary>倾角（度）。</summary>
    public double AngleDegrees
    {
        get => _angleDegrees;
        set => _angleDegrees = PendulumModel.Clamp(value, MinAngleDegrees, MaxAngleDegrees);
    }

    /// <summary>动摩擦因数 µ。</summary>
    public double Friction
    {
        get => _friction;
        set => _friction = PendulumModel.Clamp(value, MinFriction, MaxFriction);
    }

    /// <summary>重力加速度（m/s²）。</summary>
    public double Gravity { get; set; } = 9.8;

    /// <summary>初始速度（m/s，沿斜面向下为正；负 = 从底端往上推）。</summary>
    public double StartSpeed { get; set; }

    /// <summary>当前速度（m/s，沿斜面向下为正）。</summary>
    public double Speed { get; private set; }

    /// <summary>沿斜面向下的位移（m；上滑阶段为负）。</summary>
    public double Travel { get; private set; }

    /// <summary>已经过的时间（s）。</summary>
    public double Time { get; private set; }

    /// <summary>倾角（rad）。</summary>
    public double AngleRadians => PendulumModel.ToRadians(AngleDegrees);

    /// <summary>重力沿斜面的分量加速度（m/s²，恒向下）。</summary>
    public double GravityAlong => Gravity * Math.Sin(AngleRadians);

    /// <summary>滑动时摩擦产生的加速度大小（m/s²）。</summary>
    public double FrictionAlong => Gravity * Friction * Math.Cos(AngleRadians);

    /// <summary>
    /// 临界摩擦因数：<c>µ_c = tan θ</c>。µ 等于它时匀速下滑，大于它则静止。
    /// </summary>
    public double CriticalFriction => Math.Tan(AngleRadians);

    /// <summary>临界倾角（度）：<c>θ_c = arctan µ</c>。小于它则推不动（松手即停）。</summary>
    public double CriticalAngleDegrees
        => Math.Atan(Friction) * 180.0 / Math.PI;

    /// <summary>松手后会不会自己下滑（<c>tan θ &gt; µ</c>）。</summary>
    public bool SlidesFromRest => GravityAlong > FrictionAlong + 1e-12;

    /// <summary>
    /// 当前加速度（m/s²，沿斜面向下为正）。
    /// </summary>
    /// <remarks>
    /// 三个分支，缺一不可：下滑、上滑、静止。静止那一支还要再按"够不够陡"分两种 ——
    /// 这正是"只按公式硬算会得到滑块自己往上爬"的那个坑。
    /// </remarks>
    public double Acceleration
    {
        get
        {
            if (Speed > RestEpsilon) return GravityAlong - FrictionAlong;    // 下滑：摩擦向上
            if (Speed < -RestEpsilon) return GravityAlong + FrictionAlong;   // 上滑：摩擦向下
            return SlidesFromRest ? GravityAlong - FrictionAlong : 0.0;      // 静止：看静摩擦够不够
        }
    }

    /// <summary>此刻是否真的停着（速度为零且不会自己动起来）。</summary>
    public bool IsAtRest => Math.Abs(Speed) <= RestEpsilon && !SlidesFromRest;

    /// <summary>
    /// 以 <paramref name="startSpeed"/> 上滑时，能冲出去的<b>最远距离</b>（m，正数）。
    /// </summary>
    /// <remarks>
    /// 解析解：<c>s = v₀² / (2·(a_g + a_f))</c>。它给"斜面向上减速"这类题一个
    /// 屏幕上可读、可断言的参照值。
    /// </remarks>
    public double UpSlideDistance(double startSpeed)
    {
        double speed = Math.Abs(startSpeed);
        double deceleration = GravityAlong + FrictionAlong;
        return deceleration <= 0 ? 0 : speed * speed / (2.0 * deceleration);
    }

    /// <summary>回到初始状态。</summary>
    public void Reset(double? startSpeed = null)
    {
        if (startSpeed is { } value)
        {
            StartSpeed = PendulumModel.Clamp(value, -MaxStartSpeed, MaxStartSpeed);
        }

        Speed = StartSpeed;
        Travel = 0;
        Time = 0;
    }

    /// <summary>
    /// 推进一步。
    /// </summary>
    /// <remarks>
    /// 用"分段恒加速度"解析推进（不是 RK4）：这里的加速度<b>只在速度过零时突变</b>，
    /// 段内是常数，解析式给出的结果比数值积分还准；而"物体停下"这件事必须显式处理 ——
    /// 纯数值积分会让它一直以微小速度往下蠕，屏幕上看起来像"停不住"。
    /// </remarks>
    public void Step(double dt)
    {
        if (dt <= 0) return;

        if (Math.Abs(Speed) <= RestEpsilon && !SlidesFromRest)
        {
            Speed = 0;
            Time += dt;
            return;
        }

        double acceleration = Acceleration;
        double next = Speed + acceleration * dt;

        if (Math.Abs(Speed) > RestEpsilon && next * Speed < 0 && !SlidesFromRest)
        {
            // 中途停住：停在过零那一刻，之后不再动（静摩擦够大）
            double stopTime = -Speed / acceleration;
            Travel += Speed * stopTime + 0.5 * acceleration * stopTime * stopTime;
            Speed = 0;
        }
        else
        {
            Travel += Speed * dt + 0.5 * acceleration * dt * dt;
            Speed = next;
        }

        Time += dt;
    }

    /// <summary>推进一段真实时间（按 <paramref name="maxStep"/> 拆步）。</summary>
    public void Advance(double dt, double maxStep = 1e-3)
    {
        if (dt <= 0) return;

        double step = PendulumModel.Clamp(maxStep, 1e-5, 0.05);
        int steps = (int)Math.Min(Math.Ceiling(dt / step), 20000);
        double each = dt / steps;

        for (int i = 0; i < steps; i++) Step(each);
    }

    /// <summary>给状态栏/读数用的一句话：当前在干什么。</summary>
    public string DescribeMotion()
    {
        if (IsAtRest) return SlidesFromRest ? "静止（松手即下滑）" : "静止（摩擦力足够，推一下停一下）";
        if (Speed > RestEpsilon) return "沿斜面下滑（摩擦向上）";
        return "沿斜面向上滑动（摩擦向下）";
    }
}
