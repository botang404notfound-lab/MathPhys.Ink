namespace MathPhys.Ink.Plugins;

/// <summary>
/// Web 面板的 <b>profile 标识</b>（M22）。
/// </summary>
/// <remarks>
/// 一个「通用 Web 面板门面」可以同时服务多种网页应用，每种应用叫一个 <b>profile</b>：
/// 各有自己的离线资源目录、虚拟域名与入口页。插件只说"打开哪个 profile"，
/// 不必知道背后是虚拟域名还是本地目录。
/// <para>
/// 用常量而不是裸字符串：拼错一个字母的表现是"点了没反应"（profile 查不到 ⇒ 静默降级），
/// 那是本项目最不想留的一类故障。
/// </para>
/// </remarks>
public static class WebPanelIds
{
    /// <summary>GeoGebra 演示面板（M7.5 起既有，资源目录 <c>geogebra/</c>）。</summary>
    public const string GeoGebra = "geogebra";

    /// <summary>
    /// 物理仿真面板（M22）：一个自建导航页内嵌 CircuitJS 与 PhET 动画，
    /// 资源目录 <c>websim/</c>。<b>只有完整版携带</b>（轻量版不含 WebView2 运行时）。
    /// </summary>
    public const string WebSim = "websim";
}
