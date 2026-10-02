using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.VectorArrow;

/// <summary>
/// 矢量箭头插件：注册 4 个工具（单根箭头、合力、正交分解、矢量组）。
/// </summary>
/// <remarks>
/// 渲染器不在这注册，由宿主 <c>RegisterRenderers</c> 扫同名 dll 自动登记
/// （与 Ruler、Protractor、Triangle、CoordSystem、FunctionPlot 同款做法）。
/// </remarks>
public sealed class VectorArrowPlugin : IWhiteBoardPlugin
{
    public string Name => "矢量箭头（学科工具）";

    public void Register(IToolRegistry registry)
    {
        // 顺序即工具栏按钮顺序，也是老师讲课的自然顺序：
        // 先画分力 → 再求合力 → 把一根斜力拆成正交分量 → 最后把一堆力捆成一组整体搬动
        registry.Add(new ArrowTool());
        registry.Add(new ArrowSumTool());
        registry.Add(new ArrowDecomposeTool());
        registry.Add(new ArrowGroupTool());
    }
}
