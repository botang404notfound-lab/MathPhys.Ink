using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Triangle;

/// <summary>
/// 三角板插件：注册「三角板·45°」与「三角板·60°」两个工具。
/// </summary>
/// <remarks>
/// 两个工具是<b>同一个类的两个实例</b>，只差一个板型枚举 —— 照 Ruler 的做法，成本一行注册。
/// <see cref="TriangleRenderer"/> 不在这里注册，由加载器扫同名 dll 自动登记（见量角器同款注释）。
/// </remarks>
public sealed class TrianglePlugin : IWhiteBoardPlugin
{
    public string Name => "三角板（学科工具）";

    public void Register(IToolRegistry registry)
    {
        registry.Add(new TriangleTool(TriangleGeometry.TriangleKind.FortyFive));
        registry.Add(new TriangleTool(TriangleGeometry.TriangleKind.SixtyThirtyNinety));
        registry.Add(new TriangleTraceTool());
    }
}
