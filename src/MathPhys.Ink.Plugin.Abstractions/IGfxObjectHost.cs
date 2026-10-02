using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 工具能对画布上的<b>图形对象</b>做的事 —— 受控子集，和 <see cref="IToolContext"/> 同一个思路。
/// </summary>
/// <remarks>
/// 这里<b>没有</b>的才是重点：没有"清空全部对象"、没有"把别人的对象删掉"（除了自己拿到 Id 的那个）、
/// 没有"改视口"、没有"换文档"。工具是插件，它该有的权力就只有"放一个新图形"和
/// "调自己那个图形的参数"。
///
/// <para>
/// 拿到它的途径是 <see cref="IToolContext.Gfx"/>。
/// </para>
/// </remarks>
public interface IGfxObjectHost
{
    /// <summary>当前画布上的全部对象（按 Z 序，最后一个在最上层）。只读。</summary>
    IReadOnlyList<IGfxObjectRef> Objects { get; }

    /// <summary>当前被选中的对象；没有选中时为 <c>null</c>。</summary>
    IGfxObjectRef? Selected { get; }

    /// <summary>
    /// 选中项发生变化。
    /// </summary>
    /// <remarks>
    /// 参数面板靠它把"选中了哪个对象"同步到界面上（改了表达式/范围后要立刻重画）。
    /// 订阅者要在工具 <c>Deactivate</c> 时退订，否则面板会在工具切走后继续被回调。
    /// </remarks>
    event EventHandler? SelectionChanged;

    /// <summary>
    /// 落成一个新对象，返回它的 Id。
    /// </summary>
    /// <remarks>
    /// <b>这是工具"画图形"的唯一正确姿势。</b>宿主会把它纳入：
    /// 视觉渲染（走注册的渲染器）、选中/拖动/旋转/缩放、撤销（一步操作 = 一个撤销单元）、
    /// <c>.tbink</c> 持久化 —— 一行都不用自己写。
    /// <para>
    /// 若 <paramref name="draft"/> 的 <see cref="GfxDraft.Kind"/> 没有注册渲染器，
    /// 仍然会入库并显示成占位框（存档不会丢数据），但状态栏会提示。
    /// </para>
    /// </remarks>
    string Add(GfxDraft draft);

    /// <summary>按 Id 删除一个对象；返回是否真的删了。</summary>
    bool Remove(string id);

    /// <summary>改位姿（中心 / 旋转 / 缩放）。三个值一起给，避免出现"只转了一半"的中间态。</summary>
    bool UpdatePose(string id, Point center, double rotationDegrees, double scale);

    /// <summary>改数值参数（合并语义：只覆盖给出的键）。</summary>
    bool UpdateNumbers(string id, IReadOnlyDictionary<string, double> numbers);

    /// <summary>改文本参数（合并语义：只覆盖给出的键）。</summary>
    bool UpdateTexts(string id, IReadOnlyDictionary<string, string> texts);

    /// <summary>选中某个对象；传 <c>null</c> 表示取消选中。</summary>
    void Select(string? id);

    /// <summary>
    /// 标记"接下来要做一次可撤销的修改"。
    /// </summary>
    /// <remarks>
    /// <b>必须在改之前调用，而且一次连续操作只调一次。</b>
    /// 例如拖动：在"第一次真的动起来"时调一次，而不是每一帧都调 ——
    /// 否则按一下 Ctrl+Z 只会退回一个像素，用户会以为撤销坏了。
    /// <para>
    /// 与之配套：本方法只该在<b>确实会产生变化</b>时调用。按下还没动就调用，
    /// 会在历史里留下一个"什么都没变"的空白步。
    /// </para>
    /// </remarks>
    void BeginStep(string label);

    /// <summary>
    /// 把一串 PNG 字节落成卷面上的一张位图对象，返回它的 Id（落不成时 <c>null</c>）。
    /// </summary>
    /// <param name="pngBytes">PNG 字节；空数组视为"没拿到画面"，返回 <c>null</c>。</param>
    /// <param name="label">
    /// 这张图叫什么（如「单摆仿真」）。只影响状态栏怎么念它，可以是空串。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>为什么需要它。</b>宿主有一条现成的「位图对象」通道（<c>webPanelImage</c>：
    /// 进撤销栈、随 <c>.twb</c> 存档、导出 PDF 带上），但那条通道的字节存在<b>宿主的位图仓库</b>里，
    /// 插件够不着。于是原生仿真窗自带 <c>RenderTargetBitmap</c> 截下来的画面
    /// （它是纯 WPF 视觉，没有 WebView2 的空气空间问题）就无处可放 ——
    /// 本方法把"给一串 PNG"翻译成"画布上多了一张可存档的图"。
    /// </para>
    /// <para>
    /// <b>为什么不让插件自己存字节。</b><see cref="GfxDraft"/> 只有 <c>Numbers</c> 与
    /// <c>Texts</c> 两个袋子，且随 <c>.tbink</c> 走文本序列化：一帧 1280×720 的 PNG 编成
    /// base64 约 0.5 MB，而撤销栈存 60 份快照 ⇒ 30 MB 纯字符串。字节必须留在对象之外。
    /// </para>
    /// <para>
    /// <b>尺寸与落点由宿主定</b>：按 PNG 头的像素长宽比折成世界尺寸（<b>绝不拉伸</b>），
    /// 放在视口中心并夹进当前页内。插件既不知道页在哪，也不该知道。
    /// </para>
    /// <para>
    /// 与 <see cref="Add"/> 同一套纪律：宿主会先记一步历史再落对象，
    /// 所以<b>撤销一步 = 这张图整体消失</b>，插件不用自己调 <see cref="BeginStep"/>。
    /// </para>
    /// </remarks>
    string? AddImage(byte[] pngBytes, string label = "");
}
