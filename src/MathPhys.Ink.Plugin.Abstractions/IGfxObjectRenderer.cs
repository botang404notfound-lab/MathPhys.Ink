using System.Windows;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 把一种 <see cref="IGfxObjectRef.Kind"/> 画出来 —— 插件对"图形库"这一层的贡献。
/// </summary>
/// <remarks>
/// 宿主负责对象的一切（集合、位姿、选中、拖动、旋转、撤销、存档），
/// 渲染器只负责"给定参数，产出矢量视觉树"。这条分工是 M7.4 最重要的一刀：
/// 每个学科工具只写自己的几何，公共操作只有一份实现。
/// <para><b>三条必须遵守的约定</b></para>
/// <list type="number">
/// <item><b>不要设置 <c>RenderTransform</c></b>（也不要用 <c>Canvas.Left/Top</c> 去摆位）——
/// 位姿由宿主统一施加，只有一个矩阵真相源。插件自己也摆一次，就会出现
/// "看着在这、点着不在"，而且只在某个旋转/缩放组合下复现。</item>
/// <item><b>本地坐标以 <c>(0,0)</c> 为中心</b>，范围 <c>[-W/2, W/2] × [-H/2, H/2]</c>，
/// <c>W/H</c> 就是 <see cref="Measure"/> 返回的尺寸。别把原点放左上角。</item>
/// <item><b>视觉树里的固定字号/图元尺寸要自己挂反向缩放</b>
/// （<c>LayoutTransform = ScaleTransform(1/Scale)</c>）。图形层是世界坐标层，
/// 直接放文字会随缩放一起糊掉 —— 与 M7.3 直尺的长度数字是同一个坑。</item>
/// </list>
/// <para>
/// 实现类必须有一个<b>公开无参构造函数</b>（宿主不知道该怎么构造你）。
/// </para>
/// </remarks>
public interface IGfxObjectRenderer
{
    /// <summary>负责的对象种类；与 <see cref="GfxDraft.Kind"/> 一一对应。</summary>
    string Kind { get; }

    /// <summary>
    /// 本地包围盒（以 <c>(0,0)</c> 为中心，不含旋转与缩放）。
    /// </summary>
    /// <remarks>
    /// 必须是<b>纯函数</b>：只吃对象参数、不碰视觉树、不依赖布局。
    /// 命中测试、选中框、拖动吸附都靠它 —— 而这些必须在没有窗口的验收 harness 里也能算，
    /// 所以不能"先建个控件再量一下"。
    /// </remarks>
    Size Measure(IGfxObjectRef obj);

    /// <summary>
    /// 产出矢量视觉树（本地坐标，原点居中）。
    /// </summary>
    /// <remarks>
    /// 只在"新建"与"参数变化"时被调用；拖动的每一帧都<b>不会</b>重建视觉树
    /// （宿主只改矩阵）。所以这里可以放心地构造几何，不必为每帧开销担心。
    /// <para>
    /// 抛异常会被宿主捕获并降级成占位框，只损失这一个对象 —— 不会带走整个白板。
    /// </para>
    /// </remarks>
    FrameworkElement CreateVisual(IGfxObjectRef obj);
}
