using System.Windows;
using System.Windows.Media;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 画布上一个图形对象的<b>只读视图</b>。
/// </summary>
/// <remarks>
/// 与 <see cref="IToolRegistry"/> 只给 <c>Add</c> 是同一条原则：插件能<b>读</b>别人的对象
/// （吸附、对齐、找坐标系），但<b>不能</b>改别人的对象。一个插件若拿到"能改别人对象"的权力，
/// 出问题时就没人说得清是谁干的。
/// <para>
/// 要改自己创建的对象，走 <see cref="IGfxObjectHost"/> 上带 Id 的方法。
/// </para>
/// </remarks>
public interface IGfxObjectRef
{
    /// <summary>稳定标识（宿主生成，存档时写入）。</summary>
    string Id { get; }

    /// <summary>对象种类（<c>protractor</c> / <c>coordsystem</c> / <c>function</c> / <c>vector</c> …）。
    /// 与渲染器注册的 <see cref="IGfxObjectRenderer.Kind"/> 对应。</summary>
    string Kind { get; }

    /// <summary>创建它的插件显示名（存档诊断用；插件被禁用时界面靠它告诉用户少了谁）。</summary>
    string PluginName { get; }

    /// <summary>
    /// 对象<b>本地原点</b>在世界坐标里的位置（单位 = PDF point = 1/72 inch）。
    /// </summary>
    /// <remarks>
    /// 本地坐标系的约定（所有渲染器都必须遵守）：
    /// <b>原点在对象几何中心</b>，即本地范围是
    /// <c>[-W/2, W/2] × [-H/2, H/2]</c>，<c>W/H</c> 由 <see cref="LocalSize"/> 给出。
    /// <para>
    /// 这样一来"本地 → 世界"就是纯旋转 + 缩放 + 平移，没有隐藏的偏移。
    /// 一旦有人偷偷把原点放在左上角，命中测试与渲染会各错一半，且只在旋转后暴露。
    /// </para>
    /// </remarks>
    Point Center { get; }

    /// <summary>绕 <see cref="Center"/> 的旋转角（度，正 = 屏幕上的顺时针，因为世界坐标 y 轴向下）。</summary>
    double RotationDegrees { get; }

    /// <summary>缩放系数；<c>1.0</c> = 渲染器定义的"标准尺寸"。</summary>
    double Scale { get; }

    /// <summary>本地包围盒（未旋转、未缩放），以 <c>(0,0)</c> 为中心。</summary>
    Size LocalSize { get; }

    /// <summary>该对象的外接矩形（世界坐标，已含旋转与缩放）。</summary>
    Rect BoundsWorld { get; }

    /// <summary>创建时的墨色（之后移动/旋转都不会改它）。</summary>
    Color Color { get; }

    /// <summary>创建时的线宽（世界单位）。</summary>
    double LineWorldWidth { get; }

    /// <summary>
    /// 该对象<b>是否存在</b>某个数值参数。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="GetNumber"/> 配合使用：只靠"取回来等于 0"无法区分
    /// "参数是 0"与"参数不存在"，而这两者在面板上要显示得不一样。
    /// </remarks>
    bool HasNumber(string key);

    /// <summary>读一个数值参数（半径、步长、a/b/c 系数…）；不存在时返回 <paramref name="fallback"/>。</summary>
    double GetNumber(string key, double fallback = 0);

    /// <summary>读一个文本参数（表达式、标签、颜色以外的字符串）；不存在时返回 <paramref name="fallback"/>。</summary>
    string GetText(string key, string fallback = "");
}
