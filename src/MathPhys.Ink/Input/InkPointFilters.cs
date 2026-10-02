using System;

namespace MathPhys.Ink.Input;

/// <summary>笔迹平滑档位（M18，独立于笔锋三档 —— 用户先例：多档开关）。</summary>
public enum SmoothStrength
{
    Off = 0,
    Light = 1,
    Medium = 2,
}

/// <summary>
/// 断点判定 —— 「起笔突然一条横贯直线」的对症拦截（M18）。
/// </summary>
/// <remarks>
/// <para>
/// 现场机型（红外触摸框 + 被动笔）会把<b>手掌与笔并入同一触点点列</b>，点列从笔尖瞬移到手掌
/// ⇒ DynamicRenderer 逐包增量连线把跳变画成一条长直线；此外现场日志实证 Down 事件会丢
/// （stylus up=1150 &gt; down=1148），丢失后 Move 续画同样呈现直线。
/// </para>
/// <para>
/// <b>阈值推导（写成常量是为了真机可调）</b>：
/// 真机采样 213~281 Hz ⇒ 相邻包间隔 3.5~4.7 ms；正常快写速度 0.2~0.5 world/ms
/// （<see cref="SpeedPressureMap"/> 参考值），暴力快划按 1.0 world/ms 算，
/// 相邻包位移也只有 ~3.5~5 world。而手掌触点到笔尖的物理距离 &gt; 30 mm ≈ &gt; 85 world
/// （world = PDF point ≈ 1/72 英寸）。两组数字相距 8~20 倍，中间留足余量。
/// </para>
/// <para>
/// 刻意做成<b>纯函数、无 RTI 依赖</b>（照 <see cref="InkSampleBuffer"/> 的先例）：
/// 验收 harness 在本机没有笔、<c>RawStylusInput</c> 不可能被伪造，纯函数层才能被直接断言。
/// </para>
/// </remarks>
public static class JumpDetector
{
    /// <summary>相邻包速度超过此值（world/ms）才算跳变：比最快正常书写高 4 倍。</summary>
    public const double JumpSpeedThreshold = 2.0;

    /// <summary>相邻包位移超过此值（world ≈ 28 mm）才算跳变：排除"快但连续"的正常快笔。</summary>
    public const double JumpMinDistance = 80.0;

    /// <summary>时间间隙超过此值（ms）且随后位移大 ⇒ 按 Down 丢失处理。</summary>
    public const int GapThresholdMs = 120;

    /// <summary>
    /// 双条件判定：速度超阈 <b>且</b> 绝对距离超阈（任一单独成立都不判，防误伤快笔）。
    /// 长停顿后的大位移（Down 丢失特征）单看距离即可判。
    /// </summary>
    public static bool IsJump(double prevX, double prevY, int prevTimestamp, double x, double y, int timestamp)
    {
        double dx = x - prevX;
        double dy = y - prevY;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // 坏点（NaN / 无穷）不判也不抛 —— 静默放过，绝不影响落墨
        if (double.IsNaN(dist) || double.IsInfinity(dist)) return false;

        int dt = timestamp - prevTimestamp;

        // 长停顿后的大位移：典型"Down 丢失后 Move 续画"特征
        if (dt > GapThresholdMs) return dist > JumpMinDistance;

        // 正常包：双条件（快 且 远）
        double speed = dist / (dt > 0 ? dt : 1);
        return speed > JumpSpeedThreshold && dist > JumpMinDistance;
    }
}

/// <summary>
/// 保点数指数低通平滑 —— 「字迹更顺滑」的温和档实现（M18）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么否决 Catmull-Rom / 贝塞尔细分</b>：细分会<b>增加点数</b>，直接踩 docs/16 S5 的红线
/// —— 点数是笔锋的原料，增删点都会改变 ISF 存档口径。指数低通逐点推进、<b>零增删点</b>，
/// 压力因子原样保留（笔锋不磨平）。
/// </para>
/// <para>
/// <b>公式</b>：<c>s_i = s_{i-1} + α·(p_i − s_{i-1})</c>，X/Y 各自独立，O(1) 每点。
/// 相位延迟 ≈ (1−α)/α 个包：α=0.6 时约 0.67 包 ≈ 2~3 ms（281 Hz），远低于人手感知阈值。
/// </para>
/// <para>
/// ★ 与「笔迹几何永不重算」不变量的关系：平滑发生在<b>笔线程采集时一次成型</b> ——
/// 提交的 Stroke 几何就是平滑后的点列，ISF 存的就是它，此后（缩放/保存/加载/回放）永不重算；
/// 不变量约束的"UI 线程二次几何重算"并未发生。旧档的点集不经过本插件，按原样渲染，零影响。
/// </para>
/// </remarks>
public static class PathSmoother
{
    /// <summary>起笔甩尾抑制半径：与首点距离小于此值（world）的点拉回与首点重合。</summary>
    public const double StartJitterRadius = 2.0;

    /// <summary>档位对应 α：越大越跟手。Light=0.6（出厂，延迟 ~2 ms）；Medium=0.4（延迟 ~6 ms，更顺）。</summary>
    public static double Alpha(SmoothStrength strength) => strength switch
    {
        SmoothStrength.Medium => 0.4,
        SmoothStrength.Light => 0.6,
        _ => 1.0,   // Off：恒等映射
    };

    /// <summary>单点推进。锚无效时调用方直接取当前点（首点锚定，防起笔漂移）。</summary>
    public static double Next(double anchor, double value, double alpha)
        => anchor + alpha * (value - anchor);

    /// <summary>两点距离是否在起笔抖动半径内（甩尾抑制判据）。</summary>
    public static bool IsWithinJitter(double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        return dx * dx + dy * dy <= StartJitterRadius * StartJitterRadius;
    }
}

/// <summary>包内单点的分级结论（M24 热修①：逐点过滤）。</summary>
public enum PacketPointVerdict
{
    /// <summary>完好点：采集 + 写回。</summary>
    Good,

    /// <summary>坏点：NaN / 无穷 / 精确 (0,0) —— 直接丢弃，不进几何。</summary>
    BadPoint,

    /// <summary>跳变点：与上一个完好点之间发生了空间瞬移 —— 断笔。</summary>
    Jump,
}

/// <summary>
/// 包内逐点过滤 —— 「横贯直线」的第二刀（M24 热修①）。
/// </summary>
/// <remarks>
/// <para>
/// M18 的 <see cref="JumpDetector"/> 只查<b>包边界</b>（上一包末点 vs 本包首点），
/// 包内点无条件直通 DynamicRenderer —— UI 繁忙时 RTI 会把多个采样合进一个包，
/// 手掌并入若发生在包中间，直线照画照提交。本类把判定<b>逐点化</b>：
/// 包内点与包边界点一视同仁，每个点都对照「上一个完好点」判跳变、判坏点。
/// </para>
/// <para>
/// ★ 同包内所有点共享 <c>RawStylusInput.Timestamp</c> ⇒ 包内 dt=0，
/// <see cref="JumpDetector.IsJump"/> 自动退化为「距离 &gt; <see cref="JumpDetector.JumpMinDistance"/>」
/// 单条件 —— 真机采样 213~281 Hz，相邻点正常位移不到 5 world，80 world（≈28 mm）
/// 的点间瞬移不可能是真笔迹，阈值与 M18 同源、不引入新旋钮。
/// </para>
/// <para>
/// 坏点三签名：NaN / 无穷 / 精确 (0,0)。前两者会让几何计算失效（平滑、笔锋全被污染）；
/// 精确 (0,0) 是红外框漏检/故障的典型输出（真实落点是亚毫米精度的浮点值，命中双零概率为零）。
/// 坏点<b>丢弃而不判跳变</b>：一个 (0,0) 毛刺不该牺牲老师正在写的整笔。
/// </para>
/// <para>纯函数、无 RTI 依赖 —— harness 直接断言（照 JumpDetector 先例）。</para>
/// </remarks>
public static class PacketPointFilter
{
    /// <summary>
    /// 单点分级：先判坏点、再判跳变、其余完好。无锚（本笔首点）只判坏点。
    /// </summary>
    /// <remarks>
    /// ★ 跳变判定完全复用 <see cref="JumpDetector.IsJump"/>（阈值、停顿分支全同源）：
    /// 包边界（跨包，dt 为真实间隔）与包内（dt=0）天然由同一函数覆盖，
    /// 不存在「两套判定口径」的维护负担。
    /// </remarks>
    public static PacketPointVerdict Classify(
        bool hasAnchor, double anchorX, double anchorY, int anchorTimestamp,
        double x, double y, int timestamp)
    {
        if (IsBadPoint(x, y)) return PacketPointVerdict.BadPoint;

        if (!hasAnchor) return PacketPointVerdict.Good;

        return JumpDetector.IsJump(anchorX, anchorY, anchorTimestamp, x, y, timestamp)
            ? PacketPointVerdict.Jump
            : PacketPointVerdict.Good;
    }

    /// <summary>
    /// 坏点判定：NaN / ±无穷 / 精确 (0,0)。
    /// </summary>
    public static bool IsBadPoint(double x, double y)
        => !double.IsFinite(x) || !double.IsFinite(y) || (x == 0.0 && y == 0.0);
}
