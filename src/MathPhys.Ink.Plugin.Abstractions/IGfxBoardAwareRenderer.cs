using System.Collections.Generic;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 可选的渲染器能力：<b>我画出来的东西取决于画布上的"别人"</b>。
/// </summary>
/// <remarks>
/// 绝大多数渲染器只需自己的参数（<see cref="IGfxObjectRenderer"/> 就够）。
/// 但有少数图形天生是"别人的函数"：
/// <list type="bullet">
/// <item>合力箭头 —— 它的大小与方向是若干根分矢量的矢量和；</item>
/// <item>（将来）标注"两线夹角"的图形 —— 取决于两条线段。</item>
/// </list>
/// 这类渲染器在 <c>Measure</c>/<c>CreateVisual</c> 时拿不到画布，只能看到"自己这一个对象"，
/// 于是需要一个渠道把全部对象递进来。
///
/// <para><b>为什么不在契约里加"父对象"概念</b>：那会动到已有类型的形状，
/// 而本工程的铁律是"契约只加新接口"（改一次就可能让已分发的插件 dll TypeLoadException）。
/// 新增一个<b>可选</b>接口既不动别人，也能让宿主用一次 <c>is</c> 判断就认出这类渲染器。</para>
///
/// <para><b>不实现它就完全没有影响</b>：宿主只在渲染器实现了本接口时才调用。</para>
/// </remarks>
public interface IGfxBoardAwareRenderer
{
    /// <summary>
    /// 告诉渲染器"当前画布上有哪些对象"。
    /// </summary>
    /// <remarks>
    /// 宿主在每次同步视觉<b>之前</b>调用一次（画布变了才会调），参数是当前全部对象的只读视图。
    /// <para>
    /// 传 <c>null</c> 表示"没有画布上下文"（例如离屏渲染、或对象被单独渲染时）——
    /// 此时渲染器应当降级成"用自己存档里的冗余参数画"，而不是抛异常。
    /// </para>
    /// <para>
    /// <b>实现里不要留引用</b>：列表可能在下次调用时就是另一个实例了。
    /// 正统做法是当场算完（求和、求角），把结果存进自己的私有字段。
    /// </para>
    /// </remarks>
    void SetBoard(IReadOnlyList<IGfxObjectRef>? objects);
}
