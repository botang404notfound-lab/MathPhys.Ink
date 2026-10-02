using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.SubjectKit;

/// <summary>
/// 学科工具插件（M23）：学科图片资料库 —— 电场线等「点一下落到卷面」的教学插图。
/// </summary>
/// <remarks>
/// 不注册渲染器：本插件不产生图形对象 Kind，落画布走宿主的位图通道
/// （<c>IGfxObjectHost.AddImage</c>，渲染由宿主的 <c>WebPanelImageRenderer</c> 承担）。
/// </remarks>
public sealed class SubjectKitPlugin : IWhiteBoardPlugin
{
    public string Name => "学科工具（图片资料库）";

    public void Register(IToolRegistry registry)
    {
        registry.Add(new SubjectKitTool());
    }
}
