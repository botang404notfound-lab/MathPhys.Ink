using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Input;

namespace MathPhys.Ink.Views.Controls;

/// <summary>
/// 墨迹层控件 —— <see cref="InkCanvas"/> 的派生理由<b>只有一个</b>：够到 <c>StylusPlugIns</c>。
/// </summary>
/// <remarks>
/// <b>为什么必须派生</b>：<c>UIElement.StylusPlugIns</c> 是 <b><c>protected</c></b>，
/// 从外部<b>加不进</b>插件：
/// <code>
/// protected StylusPlugInCollection StylusPlugIns { get; }   // UIElement
/// </code>
/// 反射拿这个属性太脆（属性名/可见性一变就静默失效），所以派生是唯一稳妥的路。
/// <para>
/// <b>换掉 XAML 标签名是否安全</b>：安全。验收 harness 全部经 <c>host.InkSurface</c> 访问，
/// <b>没有任何 typeof(InkCanvas) 或类型相等的断言</b>；而 <c>InkSurface</c> 的签名保持返回
/// <see cref="InkCanvas"/> 不变 ⇒ 20 余处引用一行都不用改。
/// </para>
/// <para>
/// <b>插件顺序是笔锋能否实时的判据</b>：<c>StylusPlugIns</c> 按顺序执行，排在
/// <c>DynamicRenderer</c> <b>之前</b>的插件改过的点才会实时显示；排在后面的要等抬笔才生效
/// （表现为"墨迹在抬笔瞬间跳一下"）。所以这里用 <c>Insert(0, …)</c> 抢队首，
/// 并在 <see cref="OnLoadedVerify"/> 里复查一次。
/// </para>
/// </remarks>
public class InkSurfaceCanvas : InkCanvas
{
    private readonly InkSamplerPlugIn _sampler = new();
    private bool _verified;

    public InkSurfaceCanvas()
    {
        // ★ Insert(0) 而不是 Add：见类型说明。基类构造函数已经跑完，
        //   如果 InkCanvas 是在构造里挂 DynamicRenderer 的，这一句就把它压到第 2 位。
        StylusPlugIns.Insert(0, _sampler);

        // ⚠ 待验证点 #1：InkCanvas 内部的 DynamicRenderer 到底是在**构造函数**里加的，
        //   还是 OnApplyTemplate 里加的？若是后者，它会在 InitializeComponent 之后才进来、
        //   排在我们后面。Loaded 时复查一次，两种情况都能收敛到"采样插件在队首"。
        Loaded += OnLoadedVerify;
    }

    /// <summary>笔采样插件（宿主用它接线统计出口与档位开关）。</summary>
    public InkSamplerPlugIn Sampler => _sampler;

    /// <summary>插件链本身 —— 暴露出来是给验收 harness 断言顺序用的。</summary>
    public StylusPlugInCollection PlugIns => StylusPlugIns;

    /// <summary>
    /// 清当前湿墨（M18，UI 线程调用）。
    /// </summary>
    /// <remarks>
    /// <see cref="DynamicRenderer.Reset"/> 的官方签名是 (StylusDevice, StylusPointCollection)，
    /// 语义是"用给定点集<b>重画</b>当前正在画的这一笔" —— 传<b>空点集</b>就把已画的半截清掉，
    /// 后续增量包从新位置继续画（不再与跳变前的半截相连）。
    /// <para>
    /// ★ 没有活动指针（<see cref="Stylus.CurrentStylusDevice"/> 为 <c>null</c>）时<b>直接返回</b>：
    /// 那种状态下 <c>Reset</c> 必然抛 ArgumentException（"调用重置时触笔或鼠标必须…"）——
    /// 它只是被 try/catch 兜住，但每次工具切换都在老师的日志里刷一行 ERROR 样式的告警，
    /// 把真正的故障淹掉（M18.1 打包冒烟实测：切工具就必然出现）。
    /// 而没有按下的笔，本来也不存在"半截湿墨"要清 —— 该省的调用要省掉，不是"兜住就行"。
    /// </para>
    /// <para>
    /// 真有指针却仍然失败时才记 Warn（那才是异常情况）。
    /// </para>
    /// </remarks>
    public void ResetDynamicRenderer()
    {
        if (Stylus.CurrentStylusDevice is not { } device) return;

        var empty = new StylusPointCollection();

        foreach (StylusPlugIn plugIn in StylusPlugIns)
        {
            if (plugIn is DynamicRenderer renderer)
            {
                try
                {
                    renderer.Reset(device, empty);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"清实时湿墨失败（已兜住）：{ex.GetType().Name} {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 打断点兜底：清掉可能残留的实时湿墨（M18，UI 线程调用）。
    /// </summary>
    /// <remarks>
    /// 工具切换 / 进出面板会中途打断采集（IsHitTestVisible、EditingMode 翻转），湿墨可能挂在屏幕上；
    /// 宿主在 <c>ApplyTool</c> / <c>EnterPanelMode</c> / <c>ExitPanelMode</c> 里调它兜底。
    /// </remarks>
    public void ResetActiveRenderer()
    {
        ResetDynamicRenderer();
    }

    /// <summary>
    /// 窗口挂载后做两件事：<b>复查插件顺序</b> + <b>核对坐标口径</b>。
    /// </summary>
    /// <remarks>
    /// 两件事都必须放到这里、而不能放到笔线程的插件里：
    /// <list type="bullet">
    /// <item>顺序要在 <c>OnApplyTemplate</c> 之后才定型；</item>
    /// <item>坐标口径要读布局信息，而笔线程读依赖属性会撞线程亲和性。</item>
    /// </list>
    /// </remarks>
    private void OnLoadedVerify(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedVerify;
        if (_verified) return;
        _verified = true;

        EnsureSamplerFirst();
        LogPlugInChain();
        VerifyCoordinateBasis();
    }

    /// <summary>把采样插件抢回队首（若它被后来加入的 <c>DynamicRenderer</c> 挤到后面）。</summary>
    private void EnsureSamplerFirst()
    {
        int index = StylusPlugIns.IndexOf(_sampler);
        if (index <= 0) return;

        AppLog.Info($"墨迹层插件顺序修正：采样插件原排在第 {index} 位（DynamicRenderer 在它前面），"
                    + "已抢回队首 —— 否则笔锋要等抬笔才显示。");
        StylusPlugIns.Remove(_sampler);
        StylusPlugIns.Insert(0, _sampler);
    }

    /// <summary>
    /// 记一次插件链。
    /// </summary>
    /// <remarks>
    /// 这一行日志是「笔锋为什么不实时」的第一个排查点：链上顺序一目了然，
    /// 而这件事在界面上完全看不出来（不报错，只是墨迹在抬笔时跳一下）。
    /// </remarks>
    private void LogPlugInChain()
    {
        var chain = new List<string>();
        foreach (StylusPlugIn plugIn in StylusPlugIns) chain.Add(plugIn.GetType().Name);

        AppLog.Info($"墨迹层插件链（共 {StylusPlugIns.Count} 个，执行顺序即「实时墨迹优先级」）："
                    + string.Join(" → ", chain));
    }

    /// <summary>
    /// 核对"墨迹层本地坐标 ≡ 世界坐标"这个红利是否还在。
    /// </summary>
    /// <remarks>
    /// 整套笔迹设计都建立在这一条上：<c>Left/Top = 0</c> 时，WPF 才会把笔尖坐标<b>原样</b>
    /// 过一遍世界层的逆矩阵，于是 <c>Stroke</c> 里存的直接是世界坐标。
    /// <para>
    /// 这里用<b>布局变换</b>独立算一遍墨迹层原点在世界层里的位置 —— 它与
    /// <c>CanvasViewportHost.UpdateInkSurface</c> 里那条检查是同一个不变量的两个观察点
    /// （一个在加载时、一个在文档/尺寸变化时），任一处偏了都能报出来。
    /// </para>
    /// </remarks>
    private void VerifyCoordinateBasis()
    {
        try
        {
            if (VisualTreeHelper.GetParent(this) is not UIElement worldHost) return;

            Point origin = TransformToAncestor(worldHost).Transform(new Point(0, 0));

            if (Math.Abs(origin.X) > 0.5 || Math.Abs(origin.Y) > 0.5)
            {
                AppLog.Warn($"墨迹层原点在世界层里的位置是 ({origin.X:F3},{origin.Y:F3})，应为 (0,0)。"
                            + "这等于「墨迹层本地坐标不再等于世界坐标」，笔迹会整体偏移。");
            }
            else
            {
                AppLog.Info($"墨迹层坐标口径核对通过：原点 = ({origin.X:F3},{origin.Y:F3})，"
                            + "本地坐标 ≡ 世界坐标，笔迹层可以直接按世界坐标存取。");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"墨迹层坐标口径核对失败（不影响使用）：{ex.Message}");
        }
    }
}
