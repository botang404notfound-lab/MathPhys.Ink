using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Formula;

/// <summary>
/// 公式插件：在卷面上插入 LaTeX 排版的数学公式（分式、根号、矢量箭头、希腊字母…）。
/// </summary>
/// <remarks>
/// 渲染器不在这注册，由宿主扫同名 dll 自动登记（与 VectorArrow 同款做法）。
/// </remarks>
public sealed class FormulaPlugin : IWhiteBoardPlugin
{
    public string Name => "公式（学科工具）";

    public void Register(IToolRegistry registry)
    {
        registry.Add(new FormulaTool());
    }
}
