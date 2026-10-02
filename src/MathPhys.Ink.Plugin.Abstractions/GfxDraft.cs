using System.Windows;
using System.Windows.Media;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 一个待落成的图形对象 —— 工具算出几何后交给宿主的东西。
/// </summary>
/// <remarks>
/// <b>为什么要有这个"草案"类型，而不是让插件自己 new 一个对象类</b>：
/// 对象集合、Z 序、命中测试、位姿、撤销、持久化全在宿主手里（一套代码），
/// 插件只提供"这是什么形状、参数是多少"。于是加一个学科工具 =
/// 加一个几何类 + 一个渲染器 + 一次注册，宿主<b>一行不用改</b>。
/// <para>
/// 里面存的坐标一律是<b>世界坐标</b>（PDF point），不存屏幕像素 ——
/// 与笔迹同源，缩放平移不会漂移。
/// </para>
/// </remarks>
public sealed record GfxDraft
{
    private static readonly IReadOnlyDictionary<string, double> EmptyNumbers =
        new System.Collections.Generic.Dictionary<string, double>();

    private static readonly IReadOnlyDictionary<string, string> EmptyTexts =
        new System.Collections.Generic.Dictionary<string, string>();

    /// <summary>对象种类；必须与某个已注册渲染器的 <see cref="IGfxObjectRenderer.Kind"/> 相同。</summary>
    public required string Kind { get; init; }

    /// <summary>世界坐标下的中心点（= 对象本地原点）。</summary>
    public required Point Center { get; init; }

    /// <summary>墨色。工具通常直接给 <see cref="IToolContext.PenColor"/>。</summary>
    public required Color Color { get; init; }

    /// <summary>线宽（世界单位）。工具通常直接给 <see cref="IToolContext.PenWorldWidth"/>。</summary>
    public required double LineWorldWidth { get; init; }

    /// <summary>绕中心旋转角（度）。</summary>
    public double RotationDegrees { get; init; }

    /// <summary>缩放系数，<c>1.0</c> = 标准尺寸。</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>数值参数（半径、步长、系数…）。</summary>
    public IReadOnlyDictionary<string, double> Numbers { get; init; } = EmptyNumbers;

    /// <summary>文本参数（表达式、标签…）。</summary>
    public IReadOnlyDictionary<string, string> Texts { get; init; } = EmptyTexts;
}
