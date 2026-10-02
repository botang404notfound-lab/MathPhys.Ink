using System;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 「网页仿真入口能不能用」的判定（M22 S3）。
/// </summary>
/// <remarks>
/// <para>
/// 抽成纯函数只有一个目的：<b>让它可断言</b>。判定逻辑本来埋在仿真窗里，
/// 而仿真窗是 <c>internal</c> 的 WPF 窗口 —— 验收 harness 够不着它，
/// 于是「轻量版里那两颗按钮必须灰着并说清原因」这条就只能靠真机肉眼确认。
/// </para>
/// <para>
/// <b>为什么把"原因"也一起管</b>：不可用有三种来源（本版本没带 Web 模块、
/// 没装 WebView2 运行时、宿主版本较旧没提供通道），而按钮灰掉时
/// <b>必须能说出是哪一种</b>。只说「不可用」等于没说 ——
/// 现场无法区分"我该去装运行时"和"我手上这本就是轻量版"。
/// </para>
/// </remarks>
public static class WebSimAvailability
{
    /// <summary>根本拿不到 Web 模块（轻量版 / 旧宿主）时的中文原因。</summary>
    public const string NoModuleReason = "这个版本没有带网页仿真模块（轻量版只保留原生仿真）。";

    /// <summary>拿不到通道、或通道自报不可用 —— 两种情况都算不可用。</summary>
    public static bool IsUsable(IWebPanelBridge? bridge) => bridge is not null && bridge.IsAvailable;

    /// <summary>
    /// 不可用的中文原因（可直接显示）。可用时也不要紧：本方法的返回值
    /// 只在"已经确认不可用"的分支里被使用。
    /// </summary>
    /// <remarks>
    /// 通道自己给的原因优先 —— 它知道究竟是运行时缺失还是离线资源没部署；
    /// 没给或给的是空白时退回一句兜底，界面上永远不出现空白提示。
    /// </remarks>
    public static string Reason(IWebPanelBridge? bridge)
    {
        if (bridge is null) return NoModuleReason;
        return string.IsNullOrWhiteSpace(bridge.UnavailableReason)
            ? "网页仿真面板当前不可用。"
            : bridge.UnavailableReason!;
    }
}
