namespace MathPhys.Ink.WebPanel;

/// <summary>
/// 面板需要宿主提供的几件事（M7.5 起，M22 由 <c>IGeoGebraHostHooks</c> 泛化而来）。
/// </summary>
/// <remarks>
/// 做成接口而不是直接抓 <c>CanvasViewportHost</c>，是为了让面板控制器能在
/// harness 里用替身跑："打开面板要抑制画布输入、退出要还回去"这条规则本身是可断言的，
/// 不该因为拿不到真实控件就没法验。
/// <para>
/// ★ <b>为什么是 Enter/Exit 而不是 SetInkHitTestVisible(bool)</b>：
/// 后者会写成一个隐蔽的死锁 —— 退出时若按"进入前是 false"（用户当时正用手工具）
/// 去调 <c>Set(false)</c>，宿主只会"再次进入抑制"，永远出不来，白板再也写不出字。
/// 用 Enter/Exit 两个动词就没有这个歧义：<b>退出 = 把决定权还给当前工具</b>，
/// 而不是"设成某个值"。
/// </para>
/// </remarks>
public interface IPanelHostHooks
{
    /// <summary>当前是否处于"面板模式"（画布输入被抑制）。</summary>
    bool IsPanelModeActive { get; }

    /// <summary>
    /// 进入面板模式：抑制画布输入（墨迹层不接输入、视口手势关闭）。
    /// </summary>
    /// <remarks>
    /// 必须<b>幂等</b>：重复进入不能把"原本的状态"覆盖掉。
    /// </remarks>
    void EnterPanelMode();

    /// <summary>
    /// 退出面板模式：把画布输入交还给<b>当前工具</b>（笔能写、手能拖）。
    /// </summary>
    /// <remarks>
    /// 必须是"让工具重新说了算"，而不是"设成能写字"：
    /// 用户进面板之前可能正用手工具，那时退出后也不该能写字。
    /// 同样必须幂等。
    /// </remarks>
    void ExitPanelMode();

    /// <summary>在状态栏说一句话。</summary>
    void SetStatus(string text);
}
