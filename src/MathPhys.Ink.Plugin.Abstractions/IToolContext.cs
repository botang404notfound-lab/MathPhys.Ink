using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 工具能用的宿主能力 —— <b>受控子集</b>，故意做得很小。
/// </summary>
/// <remarks>
/// 这里<b>没有</b>的才是重点：没有"改视口矩阵"、没有"开关文档"、没有"清空撤销历史"、
/// 没有"退出程序"。因为工具是插件（甚至是别人写的 dll），它不该有这些权力 ——
/// 有了就一定会有人用，用出问题来又是宿主背锅。
/// <para>
/// 想要的能力都以"意图"形式提供：要落墨就给 <see cref="CommitStrokes"/>（宿主保证入撤销栈），
/// 要临时视觉就给 <see cref="AddPreview"/>（宿主保证抬笔/切工具时清掉）。
/// </para>
/// </remarks>
public interface IToolContext
{
    /// <summary>视口尺寸（DIP）。</summary>
    Size ViewportSize { get; }

    /// <summary>
    /// 图形对象通道（M7.4）。
    /// </summary>
    /// <remarks>
    /// 放一个新图形、调它自己的参数、标记撤销步，都走这里。
    /// <para>
    /// <b>可能为 <c>null</c></b>：宿主版本较旧、或验收 harness 里的替身没有实现这一层时会出现。
    /// 需要它的工具要么在 <c>Activate</c> 里检查一次并提示，要么在 <see cref="IGfxTool"/> 形态下假定非空。
    /// </para>
    /// </remarks>
    IGfxObjectHost? Gfx { get; }

    /// <summary>
    /// 画布查询通道（吸附基准）。
    /// </summary>
    /// <remarks>
    /// 量角器要"把基线对齐到卷面上已有的一条线"，就得先能看见那些线。
    /// <para>
    /// <b>可能为 <c>null</c></b>（与 <see cref="Gfx"/> 同理）：拿不到时的正确做法是
    /// <b>降级成不吸附</b>，而不是让工具失效 —— 少一个"自动对齐"是少个便利，
    /// 而"按下去什么也不出来"是故障。
    /// </para>
    /// </remarks>
    IBoardQuery? Query { get; }

    /// <summary>
    /// 重型演示面板通道（M7.5，GeoGebra）。
    /// </summary>
    /// <remarks>
    /// "打开 GeoGebra 演示面板"这类<b>命令型</b>工具走这里。面板由宿主创建与管理
    /// （WebView2 是 HWND 控件，必须挂在宿主窗口上），插件只说"打开 / 关闭 / 发消息"。
    /// <para>
    /// <b>可能为 <c>null</c></b>（与 <see cref="Gfx"/> 同理）：宿主版本较旧、
    /// 或验收 harness 的替身没实现这一层时会出现。正确做法是把按钮
    /// <b>置灰并给中文原因</b>，而不是让工具失效。
    /// </para>
    /// </remarks>
    IGeoGebraBridge? GeoGebra { get; }

    /// <summary>
    /// 通用 Web 面板通道（M22）：GeoGebra 与物理仿真页共用同一套门面，按 profile 区分。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="GeoGebra"/> 同一套纪律、同一套降级策略：<b>可能为 <c>null</c></b>，
    /// 拿不到、或该 profile 不可用时，正确做法是<b>把按钮置灰并给中文原因</b>，
    /// 而不是让工具失效。
    /// <para>
    /// 为什么两个通道并存：<see cref="GeoGebra"/> 是 M7.5 就已分发的单应用接口，
    /// 为已编译的老插件保持原样；本属性是面向"多种仿真共用一套面板"的通用入口。
    /// </para>
    /// </remarks>
    IWebPanelBridge? WebSim { get; }

    /// <summary>当前缩放（1 world 单位 = 多少个 DIP）。</summary>
    double Scale { get; }

    /// <summary>各页在世界坐标中的矩形，索引 = 页索引。<b>工具据此做吸页、贴边、跨页判断。</b></summary>
    IReadOnlyList<Rect> PageRects { get; }

    /// <summary>视口坐标 → 世界坐标。</summary>
    Point ToWorld(Point viewportPoint);

    /// <summary>世界坐标 → 视口坐标。</summary>
    Point ToViewport(Point worldPoint);

    /// <summary>当前墨色（工具画的东西应当跟随用户选的颜色）。</summary>
    Color PenColor { get; }

    /// <summary>当前笔宽（世界单位）。</summary>
    double PenWorldWidth { get; }

    /// <summary>
    /// 把笔画提交到画布。
    /// </summary>
    /// <remarks>
    /// <b>这是工具"落墨"的唯一正确姿势。</b>宿主会用一次撤销事务包裹它，于是
    /// 一次工具操作 = 一个撤销单元（按一下 Ctrl+Z 整体消失），并且自动获得：
    /// 世界坐标（随缩放平移不漂移）、橡皮可擦、<c>.tbink</c> 可存可读 ——
    /// 一行持久化代码都不用写。
    /// </remarks>
    void CommitStrokes(StrokeCollection strokes);

    /// <summary>
    /// 加一个临时预览视觉（世界坐标层）。
    /// </summary>
    /// <remarks>
    /// 用于拖动过程中的橡皮筋、标尺刻度之类。<b>它不进墨迹层、不进撤销栈、不落盘</b>，
    /// 由 <see cref="ClearPreview"/> 或"抬笔 / 切换工具"时由宿主清掉。
    /// </remarks>
    void AddPreview(UIElement visual);

    /// <summary>清掉全部预览视觉。</summary>
    void ClearPreview();

    /// <summary>
    /// 视口发生缩放 / 平移 / 适配。
    /// </summary>
    /// <remarks>
    /// 需要它的理由很具体：凡是"屏幕尺寸恒定"的视觉（手柄、刻度、读数文字）都得在视口变化后
    /// 重算坐标。工具在 <c>Activate</c> 里订阅、在 <c>Deactivate</c> 里退订。
    /// <para>
    /// 少了它，那些图元就只能等"下一次指针事件"才被纠正 —— 表现为缩放之后手柄悬在半空，
    /// 而用户下一个动作恰恰是去抓手柄。
    /// </para>
    /// </remarks>
    event EventHandler? ViewportChanged;

    /// <summary>工具想在状态栏说的一句话（「直线 12.3 cm，倾角 30°」）。</summary>
    void SetStatus(string text);
}
