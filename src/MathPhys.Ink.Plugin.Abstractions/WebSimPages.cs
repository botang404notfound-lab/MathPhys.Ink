namespace MathPhys.Ink.Plugins;

/// <summary>
/// 物理仿真面板里各个<b>仿真页</b>的标识（M22）。
/// </summary>
/// <remarks>
/// 与 <see cref="WebPanelIds"/> 的分工：那里是"打开哪一套面板"（profile），
/// 这里是"面板里打开哪一页"。两者都要传：面板
/// <see cref="IWebPanelBridge.OpenAsync"/> 的 <c>args</c> 收的就是本类的值。
/// <para>
/// <b>为什么放在契约层而不是某个插件里。</b>生产方是插件（仿真窗上那两颗「网页仿真」按钮），
/// 消费方是宿主的 <c>WebPanelProfile</c>（它按这个值决定加载哪个离线页面）。
/// 插件不能引用宿主（那是插件形态的定义本身），所以这组词汇只能住在双方都引用的契约程序集里。
/// </para>
/// <para>
/// 用常量而不是裸字符串：拼错一个字母的表现是"点了没反应"（profile 查不到 ⇒ 静默打开默认页），
/// 那是本项目最不想留的一类故障。
/// </para>
/// </remarks>
public static class WebSimPages
{
    /// <summary>电路模拟器（CircuitJS，离线本地包）。</summary>
    public const string CircuitJs = "circuitjs";

    /// <summary>PhET 单摆（离线自包含包）。</summary>
    public const string PhetPendulum = "phet-pendulum";
}
