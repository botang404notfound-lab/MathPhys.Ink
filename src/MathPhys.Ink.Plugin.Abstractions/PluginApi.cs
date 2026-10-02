namespace MathPhys.Ink.Plugins;

/// <summary>插件契约的版本信息。</summary>
/// <remarks>
/// <b>兼容判定不要读下面的常量</b>：它是 <c>const</c>，编译时被<b>内联</b>进每个插件自己的 IL 里，
/// 运行时用反射读不到"这个插件编译时用的是哪一版契约"。
/// <para>
/// 真正可靠的判据是<b>插件对契约 dll 的引用版本</b>（程序集元数据），
/// 由宿主侧的 <c>PluginContract</c> 读取并判定：主版本必须相等、次版本不得高于宿主。
/// 改契约时只改程序集版本号（见 <c>MathPhys.Ink.Plugin.Abstractions.csproj</c>）。
/// </para>
/// </remarks>
public static class PluginApi
{
    /// <summary>当前契约版本（仅供日志与文档显示；判定一律以程序集版本为准）。</summary>
    public const int Version = 1;
}

/// <summary>
/// 内置工具的 Id。
/// </summary>
/// <remarks>
/// 做成常量而不是各写各的字符串字面量：工具注册用它、界面切换用它、验收 harness 也用它，
/// 一处写错字（<c>"pen "</c>）在运行时只会表现为"工具没找到"，
/// 而这里能给出唯一的定义处。
/// </remarks>
public static class ToolIds
{
    public const string Pen = "pen";
    public const string Eraser = "eraser";
    public const string Hand = "hand";

    /// <summary>选择工具（M7.4）：选中 / 拖动 / 旋转 / 缩放 / 删除画布上的图形对象。</summary>
    public const string GfxSelect = "gfx-select";
}
