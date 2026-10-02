using System;

namespace MathPhys.Ink.Plugin.NativeSim.Kinematics;

/// <summary>
/// 定步长四阶龙格-库塔，解一维二阶系统 <c>y'' = a(t, y, y')</c>。
/// </summary>
/// <remarks>
/// <para>
/// 项目里只有两个模型需要它（单摆、弹簧振子），但两处都用同一份实现：
/// 积分器写错的样子是"图还是动的、数值悄悄偏了"，那种错只有拿解析解比对才看得见，
/// 所以它必须是<b>一处</b>实现、可被单独钉死。
/// </para>
/// <para>
/// 状态用 <c>(y, v)</c> 一对 double 就地推进（<c>ref</c>），不建对象：
/// 动画一帧要走上百步，每步分配一个状态对象是纯粹的浪费。
/// </para>
/// </remarks>
internal static class Rk4
{
    /// <summary>加速度函数：给 <c>(t, y, v)</c> 返回 <c>y''</c>。</summary>
    internal delegate double Acceleration(double t, double y, double v);

    /// <summary>
    /// 推进一步（就地更新 <paramref name="y"/> 与 <paramref name="v"/>）。
    /// </summary>
    /// <param name="accel">加速度函数。</param>
    /// <param name="t">这一步的起始时刻（加速度里含 t 时要用）。</param>
    /// <param name="dt">步长（秒）。</param>
    /// <param name="y">位置（进出）。</param>
    /// <param name="v">速度（进出）。</param>
    /// <remarks>
    /// 标准四阶格式，只是把状态写成 <c>(y, v)</c> 两个 double：
    /// <code>
    /// k1 = f(y);          k2 = f(y + h/2·k1)
    /// k3 = f(y + h/2·k2); k4 = f(y + h·k3)
    /// y += h/6·(k1 + 2k2 + 2k3 + k4)
    /// </code>
    /// 其中 <c>f(y) = (v, a(t, y, v))</c> —— 展开后就是下面四组 <c>ky / kv</c>。
    /// </remarks>
    internal static void Step(Acceleration accel, double t, double dt, ref double y, ref double v)
    {
        double k1y = v;
        double k1v = accel(t, y, v);

        double half = 0.5 * dt;

        double k2y = v + half * k1v;
        double k2v = accel(t + half, y + half * k1y, v + half * k1v);

        double k3y = v + half * k2v;
        double k3v = accel(t + half, y + half * k2y, v + half * k2v);

        double k4y = v + dt * k3v;
        double k4v = accel(t + dt, y + dt * k3y, v + dt * k3v);

        double sixth = dt / 6.0;
        y += sixth * (k1y + 2 * k2y + 2 * k3y + k4y);
        v += sixth * (k1v + 2 * k2v + 2 * k3v + k4v);
    }
}
