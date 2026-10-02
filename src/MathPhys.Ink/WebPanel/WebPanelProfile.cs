using System;
using System.Collections.Generic;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.WebPanel;

/// <summary>
/// 一个 Web 面板 <b>profile</b> 的静态描述（M22）。
/// </summary>
/// <remarks>
/// profile 回答"这块面板是哪个网页应用、它的离线资源放在哪儿、入口页叫什么"。
/// 控制器与后端只认它、不认具体应用 —— 于是"再接入一个仿真页面"退化成"再加一条 profile"，
/// 不必去动状态机、就绪闸门或消息通路。
/// <para>
/// 为什么这些信息要收在一处：它们原来是散落在
/// <c>WebView2PanelBackend</c> 里的常量与字面量（虚拟域名、目录名、入口页、文案、
/// 自测环境变量名），散着放时"想加第二个页面"就要到处改，且极易漏一处 ——
/// 漏掉的那处表现是"面板打开了但一片空白"，最难查。
/// </para>
/// </remarks>
/// <param name="Id">稳定标识（见 <see cref="WebPanelIds"/>）。</param>
/// <param name="DisplayName">界面上与日志里的中文名（顶栏标题、状态栏文案）。</param>
/// <param name="VirtualHost">离线资源映射到的虚拟域名（<b>必须走虚拟域名</b>，见后端说明）。</param>
/// <param name="AssetFolderName">exe 旁边的资源目录名。</param>
/// <param name="EntryHtml">入口页文件名（相对资源目录）。</param>
/// <param name="EnvPrefix">该 profile 自测钩子的环境变量前缀（如 <c>M75_GGB</c>）。</param>
public sealed record WebPanelProfile(
    string Id,
    string DisplayName,
    string VirtualHost,
    string AssetFolderName,
    string EntryHtml,
    string EnvPrefix)
{
    /// <summary>发布冒烟自测：<c>{EnvPrefix}_SELFTEST</c>。</summary>
    public string SelftestEnvVar => EnvPrefix + "_SELFTEST";

    /// <summary>
    /// "载入素材"用的消息类型。
    /// </summary>
    /// <remarks>
    /// GeoGebra 的页面按 <c>loadMaterial</c> 说话（既有 JS 一个字节不改），
    /// 通用仿真页面按 <c>openArgs</c>。放进 profile，控制器就不必认识具体应用。
    /// </remarks>
    public string ArgsMessageType { get; init; } = "openArgs";

    /// <summary>"载入素材"时 payload 里的字段名（GeoGebra 是 <c>ggbBase64</c>）。</summary>
    public string ArgsPayloadKey { get; init; } = "args";

    /// <summary>
    /// 是否提供「停靠面板」形态（A1：画布缩小让出右侧一块）。
    /// </summary>
    /// <remarks>
    /// GeoGebra 要（白板与演示并排是它的常用形态）；物理仿真不要（M22 拍板只做全屏）。
    /// 三重保险里的一道：窗口按它决定摆不摆「停靠面板」按钮；另外两道是
    /// Esc 的 D 键不接、后端的停靠宿主给一个永远折叠的兜底容器。
    /// </remarks>
    public bool AllowDockForm { get; init; } = true;

    /// <summary>强制走"无运行时"降级路径：<c>{EnvPrefix}_FORCE_NO_RUNTIME</c>。</summary>
    public string NoRuntimeEnvVar => EnvPrefix + "_FORCE_NO_RUNTIME";

    /// <summary>指定 Fixed Version 运行时目录：<c>{EnvPrefix}_FIXED_RUNTIME</c>。</summary>
    public string FixedRuntimeEnvVar => EnvPrefix + "_FIXED_RUNTIME";

    /// <summary>
    /// GeoGebra 演示面板（M7.5 起既有）。
    /// </summary>
    /// <remarks>
    /// 环境变量前缀刻意保持 <c>M75_GGB</c> 不变：发布冒烟脚本与现场排障文档都按它说话，
    /// 泛化重构不该把一条已有的排障线索改掉。
    /// </remarks>
    public static readonly WebPanelProfile GeoGebra = new(
        Id: WebPanelIds.GeoGebra,
        DisplayName: "GeoGebra 演示",
        VirtualHost: "geogebra.local",
        AssetFolderName: "geogebra",
        EntryHtml: "index.html",
        EnvPrefix: "M75_GGB")
    {
        // 与既有 Assets/geogebra/*.js 的约定一字不差：泛化重构不该去改页面那边的协议。
        ArgsMessageType = "loadMaterial",
        ArgsPayloadKey = "ggbBase64",
    };

    /// <summary>
    /// 物理仿真面板（M22 S5）：一个自建导航页内嵌 CircuitJS（PhET 在 S6 接入）。
    /// </summary>
    /// <remarks>
    /// <b>页 Id 不是 profile Id</b>：插件（NativeSim 的网页仿真入口）说的是
    /// <c>circuitjs</c> / <c>phet-pendulum</c> 这样的页 Id，控制器原样透传给导航页路由；
    /// profile 回答的只是"这个面板的资源在哪儿、入口页叫什么"。
    /// 只做全屏（D3），不提供停靠形态。自测前缀是新的（M22_SIM），
    /// 不与 GeoGebra 的 M75_GGB 抢环境变量。
    /// </remarks>
    public static readonly WebPanelProfile WebSim = new(
        Id: WebPanelIds.WebSim,
        DisplayName: "物理仿真",
        VirtualHost: "websim.local",
        AssetFolderName: "websim",
        EntryHtml: "index.html",
        EnvPrefix: "M22_SIM")
    {
        AllowDockForm = false,
    };

    /// <summary>内置 profile 表（顺序 = 装配顺序）。</summary>
    public static readonly IReadOnlyList<WebPanelProfile> All = new[] { GeoGebra, WebSim };

    /// <summary>按 Id 查 profile；查不到返回 <c>null</c>（调用方据此给出中文原因，不抛）。</summary>
    public static WebPanelProfile? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        foreach (var profile in All)
        {
            if (string.Equals(profile.Id, id, StringComparison.Ordinal)) return profile;
        }

        return null;
    }
}
