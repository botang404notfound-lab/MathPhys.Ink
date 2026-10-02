using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.NativeSim;

/// <summary>
/// 原生运动的三种模型。
/// </summary>
/// <remarks>
/// 枚举的声明顺序 == 界面上的标签顺序：单摆 → 弹簧振子 → 斜面。
/// 这是从"物理概念的推进顺序"排的（周期运动 → 简谐运动 → 受恒力加摩擦的直线运动），
/// 不是随手排的。
/// </remarks>
public enum SimKind
{
    /// <summary>单摆：大摆角周期变长，可以演示"小角近似"。</summary>
    Pendulum = 0,

    /// <summary>弹簧振子：真正的简谐运动，周期与振幅无关。</summary>
    Spring = 1,

    /// <summary>斜面滑块：含摩擦，可以演示临界角。</summary>
    Incline = 2,
}

/// <summary>模型的中文名与它演示什么。</summary>
public static class SimKindInfo
{
    /// <summary>三种模型（顺序即界面顺序）。</summary>
    public static readonly SimKind[] All = { SimKind.Pendulum, SimKind.Spring, SimKind.Incline };

    /// <summary>标签上的名字。</summary>
    public static string DisplayName(SimKind kind) => kind switch
    {
        SimKind.Pendulum => "单摆",
        SimKind.Spring => "弹簧振子",
        SimKind.Incline => "斜面",
        _ => "仿真",
    };

    /// <summary>一句话说明这个模型演示什么（按钮提示用）。</summary>
    public static string Summary(SimKind kind) => kind switch
    {
        SimKind.Pendulum => "单摆：摆动周期与摆长、重力加速度的关系；摆角一大，周期就变长（小角近似之外）",
        SimKind.Spring => "弹簧振子：简谐运动，周期只由质量与劲度系数决定，与振幅无关",
        SimKind.Incline => "斜面滑块：重力分量与摩擦力的较量；µ ≥ tan θ 时松手不动，这就是临界角",
        _ => string.Empty,
    };
}

/// <summary>
/// 仿真窗上的一颗「网页仿真」入口。
/// </summary>
/// <param name="PageId">喂给 <see cref="IWebPanelBridge.OpenAsync"/> 的仿真页 Id。</param>
/// <param name="Label">按钮上的字。</param>
/// <param name="Tip">按钮提示。</param>
/// <param name="Category">板块（力学 / 电磁学…，M23）；分组标题用它，空串 = 不分组。</param>
public sealed record WebSimEntry(string PageId, string Label, string Tip, string Category = "");

/// <summary>
/// 「网页仿真」入口表。
/// </summary>
/// <remarks>
/// <para>
/// 入口<b>与当前原生模型无关</b>：网页仿真是另一套东西（离线 HTML 页），
/// 老师点它就是要看那个页面，不该被"现在停在斜面标签上"影响。
/// </para>
/// <para>
/// ★ 这里的 <c>PageId</c> 用的是契约里的 <see cref="WebSimPages"/> 常量而不是裸字符串：
/// 拼错一个字母的表现是"点了没反应"（profile 查不到 ⇒ 静默打开默认页），
/// 而那种故障现场根本无从下手。
/// </para>
/// </remarks>
public static partial class WebSimEntries
{
    /// <summary>
    /// 全部入口（顺序即按钮顺序）：电路模拟器在前，PhET 全物理板块跟在后面
    /// （板块分组由 <see cref="WebSimEntriesGenerated.All"/> 的顺序决定 —— 生成物，勿手改）。
    /// </summary>
    public static readonly IReadOnlyList<WebSimEntry> All =
        new[]
        {
            new WebSimEntry(
                WebSimPages.CircuitJs,
                "电路模拟器（CircuitJS）",
                "打开 CircuitJS 电路模拟器（离线本地页，全屏；按 Esc 或右上角「返回白板」退出）",
                "电路"),
        }.Concat(WebSimEntriesGenerated.All).ToArray();
}
