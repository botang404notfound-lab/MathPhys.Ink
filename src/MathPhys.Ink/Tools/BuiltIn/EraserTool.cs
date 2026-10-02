using System.Windows.Input;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Tools.BuiltIn;

/// <summary>
/// 橡皮：按点擦除。
/// </summary>
/// <remarks>
/// 擦除动作<b>完全由 InkCanvas 原生机制执行</b>（<c>EditingMode = EraseByPoint</c>），
/// 我们不写一行擦除逻辑。原因见 M4 的决策：<c>StrokeCollection</c> 没有公开的擦除 API，
/// 自己实现点擦就得拆点重建几何 —— 那会推翻"笔迹几何永不重算"这条不变量。
/// <para>
/// 光标必须自己给（<see cref="Cursor"/>）：WPF 没有内置橡皮光标，
/// 而"现在能写还是能擦"必须在一体机屏幕上一眼可分。
/// </para>
/// </remarks>
public sealed class EraserTool : ITool
{
    public string Id => ToolIds.Eraser;

    public string DisplayName => "橡皮";

    public string ToolTip => "橡皮：触摸或左键擦除。怕手掌误擦就先切到「手」工具。";

    public Key? Shortcut => Key.D2;

    public bool UsesInkLayer => true;

    public bool NeedsPointer => false;

    public ToolInputKind InputKind => ToolInputKind.Erase;

    public ToolInkMode InkMode => ToolInkMode.EraseByPoint;

    public Cursor? Cursor => Cursors.None;

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
