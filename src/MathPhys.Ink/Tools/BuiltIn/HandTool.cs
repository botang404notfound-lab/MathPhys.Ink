using System.Windows.Input;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Tools.BuiltIn;

/// <summary>
/// 手：平移 / 缩放画布。
/// </summary>
/// <remarks>
/// 它自己不处理任何输入 —— 平移与缩放手势全在宿主的视口赛道里
/// （中键拖动、单指拖动、双指捏合、滚轮）。
/// 手工具的作用是<b>让墨迹层退出命中</b>（<see cref="UsesInkLayer"/> = false），
/// 于是触摸不再落墨，而是落到画布上变成平移与捏合。
/// <para>
/// 「手」也是用户在触摸屏上的<b>防误触开关</b>：扶屏、指题、擦黑板都先切到它。
/// </para>
/// </remarks>
public sealed class HandTool : ITool
{
    public string Id => ToolIds.Hand;

    public string DisplayName => "手";

    public string ToolTip => "手：单指拖动平移，双指捏合缩放（缩放只在手工具下有效）。";

    public Key? Shortcut => Key.D3;

    public bool UsesInkLayer => false;

    public bool NeedsPointer => false;

    public ToolInputKind InputKind => ToolInputKind.None;

    public ToolInkMode InkMode => ToolInkMode.None;

    public Cursor? Cursor => Cursors.Hand;

    public void Activate(IToolContext context)
    {
    }

    public void Deactivate()
    {
    }

    public void OnPointer(ToolPointer pointer)
    {
        // 不会收到：NeedsPointer = false
    }
}
