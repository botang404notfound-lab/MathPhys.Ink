using System.Collections.Generic;

namespace MathPhys.Ink.Plugin.FunctionPlot;

/// <summary>一次"画函数"的完整输入：表达式 + 参数取值 + 显示范围 + 标注开关。</summary>
/// <remarks>
/// 由 <see cref="FunctionInputWindow"/> 产出，交给 <see cref="FunctionTool"/> 落成对象。
/// 它是一个纯数据载体，便于验收 harness 绕过弹窗直接驱动工具逻辑。
/// </remarks>
public sealed class FunctionSpec
{
    /// <summary>表达式字符串，如 <c>x^2</c> / <c>a*sin(b*x)</c> / <c>{x&lt;0: -x, else: x}</c>。</summary>
    public string Expr = "";

    /// <summary>参数字母 → 当前取值（滑块给出）。</summary>
    public Dictionary<string, double> Parameters { get; set; } = new();

    /// <summary>x / y 显示范围（同时也是定义域裁剪范围）。</summary>
    public double XMin = -10, XMax = 10, YMin = -10, YMax = 10;

    /// <summary>是否标注零点（与 x 轴的交点）。</summary>
    public bool ShowZeros;

    /// <summary>是否标注极值（极大 / 极小）。</summary>
    public bool ShowExtrema;

    /// <summary>是否标注与同坐标系上其它曲线的交点。</summary>
    public bool ShowIntersections;
}
