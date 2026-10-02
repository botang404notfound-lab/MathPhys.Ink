using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.NativeSim;

/// <summary>
/// 物理仿真插件（M22 S3）：单摆 / 弹簧振子 / 斜面的原生 WPF 仿真窗。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么原生这条线要做成插件、而且两个变体都带。</b>轻量版没有 WebView2 运行时，
/// 带不了 CircuitJS / PhET；但"单摆、弹簧振子、斜面"是三个纯运动学模型，
/// 用 WPF 的基础图形画出来只要几十 KB —— 于是轻量版<b>仍然有一颗「物理仿真」瓦片</b>，
/// 只是窗里的「网页仿真」入口置灰并说明原因。
/// </para>
/// <para>
/// 整个插件<b>零 NuGet 依赖</b>（只用 WPF 基础图形与契约），所以它也能在
/// 一体机的教室里离线跑 —— 与"构建与打包绝不联网"这条家规一致。
/// </para>
/// <para>
/// 没有渲染器要注册：仿真不是画布上的图形对象（画布没有"每帧重绘"的推送通道），
/// 唯一的产物是用户手动导出的那张位图，而那条通道是宿主的。
/// </para>
/// </remarks>
public sealed class NativeSimPlugin : IWhiteBoardPlugin
{
    /// <inheritdoc />
    public string Name => "物理仿真（原生运动学）";

    /// <inheritdoc />
    public void Register(IToolRegistry registry)
    {
        // 注册要快、不做重活：真正的可用性检查（WebView2 / 图形对象通道在不在）
        // 发生在按下瓦片时，而不是启动关键路径上。
        registry.Add(new NativeSimTool());
    }
}
