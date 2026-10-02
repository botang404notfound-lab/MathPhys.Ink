using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace MathPhys.Ink.Plugin.FunctionPlot;

/// <summary>
/// 把多段数学坐标点序列组装成 <see cref="StreamGeometry"/>（每段一个 <c>PolyLineSegment</c>）。
/// </summary>
/// <remarks>
/// 几何用<b>数学坐标</b>（1 单位 = 1 世界点）：宿主会对函数对象施加
/// <c>Scale = unitWorld</c> 的变换，于是数学曲线自动变成正确的世界长度，
/// 与绑定的坐标系（其几何也是世界长度）严丝合缝 —— 这是函数图像与坐标系对齐的关键。
/// </remarks>
public static class PlotGeometry
{
    /// <summary>多段点序列 → 冻结的 <see cref="StreamGeometry"/>（数学坐标）。</summary>
    public static Geometry Build(IReadOnlyList<IReadOnlyList<Point>> segments)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            foreach (var seg in segments)
            {
                if (seg.Count < 2) continue;
                ctx.BeginFigure(seg[0], isFilled: false, isClosed: false);
                for (int i = 1; i < seg.Count; i++)
                    ctx.LineTo(seg[i], isStroked: true, isSmoothJoin: false);
            }
        }
        sg.Freeze();
        return sg;
    }
}
