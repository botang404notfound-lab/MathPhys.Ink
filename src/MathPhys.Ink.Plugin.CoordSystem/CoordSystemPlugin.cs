using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CoordSystem;

/// <summary>
/// 坐标系插件：注册 3 个工具（M23 起为「坐标系」族 —— 带网格 / 无网格 / 空间轴测）。
/// </summary>
/// <remarks>
/// 渲染器不在这注册，由宿主 <c>RegisterRenderers</c> 扫同名 dll 自动登记
/// （与 Ruler、Protractor、Triangle 同款做法）。
/// </remarks>
public sealed class CoordSystemPlugin : IWhiteBoardPlugin
{
    public string Name => "坐标系（学科工具）";

    public void Register(IToolRegistry registry)
    {
        // 族代表（带网格，占瓦片）；两个族成员折叠进二级菜单（ToolCatalog 里 VariantOf=coordsystem）。
        registry.Add(new CoordSystemTool());
        registry.Add(new CoordSystemTool(CoordSystemToolIds.Plain, "坐标系·无网格", showGrid: false));
        registry.Add(new CoordSystem3DTool());
    }
}