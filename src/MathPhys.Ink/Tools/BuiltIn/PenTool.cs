using System.Windows.Input;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Tools.BuiltIn;

/// <summary>
/// 笔：落墨（笔尾反转时转为擦除）。
/// </summary>
/// <remarks>
/// 它是"无状态工具"的样板：不接指针、不画预览，只是<b>声明</b>宿主该怎么配置墨迹层。
/// 真正的采集动作全交给 <c>InkCanvas</c> 原生机制 —— 这正是铁律第一条
/// （不重写墨迹引擎）在工具系统里的体现。
/// </remarks>
public sealed class PenTool : ITool
{
    public string Id => ToolIds.Pen;

    public string DisplayName => "笔";

    public string ToolTip => "笔：触摸或左键落墨。有笔数字化仪的机器上，笔尾反转随手擦。";

    public Key? Shortcut => Key.D1;

    public bool UsesInkLayer => true;

    public bool NeedsPointer => false;

    public ToolInputKind InputKind => ToolInputKind.Ink;

    public ToolInkMode InkMode => ToolInkMode.Ink;

    public Cursor? Cursor => Cursors.None;

    public void Activate(IToolContext context)
    {
        // 无状态，无需初始化
    }

    public void Deactivate()
    {
    }

    public void OnPointer(ToolPointer pointer)
    {
        // 不会收到：NeedsPointer = false
    }
}
