using System.Windows.Input;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Tools;

/// <summary>
/// 工具注册表 + 当前工具 —— 全程序唯一的"有哪些工具、现在用哪个"的真相源。
/// </summary>
/// <remarks>
/// 和 <c>InkHistory</c> 一样是<b>自包含</b>的：自己管集合、自己管激活与停用，只把结果通知出去。
/// 宿主订阅 <see cref="ActiveToolChanged"/> 后去改墨迹层的四个属性，界面订阅它去刷新按钮。
/// <para>
/// 这里的所有插件调用都<b>包了异常隔离</b>。理由不是"防御性编程"——
/// 工具将来是<b>别人写的 dll</b>，它抛异常时必须只损失它自己一个功能，
/// 而不是把整个白板带崩：老师正在讲台上用，程序崩了没有第二次机会。
/// </para>
/// </remarks>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly List<ITool> _tools = new();

    private IToolContext? _context;
    private ITool? _active;

    /// <summary>当前已注册的工具，顺序即界面按钮顺序。</summary>
    public IReadOnlyList<ITool> Tools => _tools;

    /// <summary>当前工具。注册了至少一个工具后恒不为 null。</summary>
    public ITool? ActiveTool => _active;

    /// <summary>当前工具发生变化（或首次被自动选中）。宿主据此重设墨迹层。</summary>
    public event EventHandler? ActiveToolChanged;

    /// <summary>
    /// 递给工具用的宿主能力门面。注册<b>之前</b>就要设好 ——
    /// 工具在 <c>Activate</c> 里就会用它。
    /// </summary>
    public void AttachContext(IToolContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>
    /// 注册一个工具。
    /// </summary>
    /// <remarks>
    /// 重复 Id 直接拒绝并记日志，不覆盖：插件各写各的，Id 撞车时"后者替换前者"
    /// 会表现成"重启一次工具就变了"，比明确拒绝难查得多。
    /// 若当前还没有工具，则第一个注册的自动成为当前工具 ——
    /// 否则启动瞬间会处于"有按钮但没工具"的空白态。
    /// </remarks>
    public void Add(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (string.IsNullOrWhiteSpace(tool.Id))
        {
            AppLog.Warn($"工具 {tool.GetType().FullName} 的 Id 为空，已拒绝注册。");
            return;
        }

        foreach (var existing in _tools)
        {
            if (!string.Equals(existing.Id, tool.Id, StringComparison.Ordinal))
            {
                continue;
            }

            AppLog.Warn($"工具 Id 重复：{tool.Id}"
                        + $"（已有 {existing.GetType().Name}，又来 {tool.GetType().Name}），已拒绝后一个。");
            return;
        }

        // ★ 快捷键撞车：两个工具声明同一个键时，FindByShortcut 只会返回<b>先注册的那个</b>
        //   （插件按目录名排序加载 ⇒ 谁先谁赢），另一个的快捷键就成了死的，而它的 tooltip
        //   照样写着那个键 —— 老师按下去选中了别的工具，从现象上看是"工具选错了"。
        //   这里只记日志不拒绝：工具本身是好的，屏幕入口（工具栏按钮）还在，
        //   为一次键位冲突把功能整个丢掉比留着更糟。踩过一次（坐标系 vs 沿边画线都写 9）。
        if (tool.Shortcut is { } shortcut)
        {
            foreach (var existing in _tools)
            {
                if (existing.Shortcut != shortcut) continue;

                AppLog.Warn($"工具快捷键重复：{Describe(existing)} 与 {Describe(tool)} 都声明了 {shortcut}，"
                            + $"按这个键只会选中 {Describe(existing)}；请改其中一方的键位。");
                break;
            }
        }

        _tools.Add(tool);

        if (_active is null) Activate(tool);
    }

    /// <summary>日志里说清"是哪个插件/哪个工具"—— 只有类型名的话，插件作者找不到自己那处。</summary>
    private static string Describe(ITool tool)
        => $"「{tool.DisplayName}」({tool.Id})";

    /// <summary>按 Id 查找工具；找不到返回 null。</summary>
    public ITool? Find(string id)
    {
        foreach (var tool in _tools)
        {
            if (string.Equals(tool.Id, id, StringComparison.Ordinal)) return tool;
        }

        return null;
    }

    /// <summary>
    /// 按快捷键查找工具；没有工具声明这个键则返回 null。
    /// </summary>
    /// <remarks>
    /// 小键盘数字与大键盘数字视为同一个键：用户想按的是"1"，
    /// 不会关心它在哪块键盘上，更不会理解"为什么小键盘那个按了没反应"。
    /// </remarks>
    public ITool? FindByShortcut(Key key)
    {
        var normalized = NormalizeShortcut(key);

        foreach (var tool in _tools)
        {
            if (tool.Shortcut == normalized) return tool;
        }

        return null;
    }

    /// <summary>把小键盘数字键归一化成大键盘数字键；其余按键原样返回。</summary>
    private static Key NormalizeShortcut(Key key)
        => key >= Key.NumPad0 && key <= Key.NumPad9
            ? (Key)((int)Key.D0 + (int)(key - Key.NumPad0))
            : key;

    /// <summary>
    /// 按 Id 切换当前工具。
    /// </summary>
    /// <returns>是否切换成功（Id 不存在返回 false）。</returns>
    public bool Activate(string id)
    {
        var tool = Find(id);

        if (tool is null)
        {
            AppLog.Warn($"没有名为「{id}」的工具，切换被忽略。");
            return false;
        }

        Activate(tool);
        return true;
    }

    /// <summary>
    /// 切换当前工具：先停用旧的、再启用新的。
    /// </summary>
    /// <remarks>
    /// <b>旧工具一定要先 Deactivate</b>，哪怕它正在拖动中：
    /// 停在拖动中途的工具会留下预览图元与"上一次起点"这类内部状态，
    /// 切回来时表现为"线从一个莫名其妙的地方开始画"。
    /// </remarks>
    public void Activate(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (ReferenceEquals(tool, _active)) return;

        var previous = _active;
        _active = tool;

        if (previous is not null) Guard($"{previous.Id}.Deactivate", previous.Deactivate);

        Guard($"{tool.Id}.Activate", () => tool.Activate(_context!));

        ActiveToolChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 把一次工具调用包进异常隔离。
    /// </summary>
    /// <remarks>
    /// 插件是别人写的代码，它出错必须只损失它自己。这里只记日志不弹窗：
    /// 课堂上一旦弹窗，老师就得先关掉它才能继续讲。
    /// </remarks>
    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"工具调用失败（{what}）：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>转发一次指针事件给当前工具（仅在工具声明 <c>NeedsPointer</c> 时由宿主调用）。</summary>
    public void DispatchPointer(ToolPointer pointer)
    {
        var tool = _active;
        if (tool is null || !tool.NeedsPointer) return;

        Guard($"{tool.Id}.OnPointer", () => tool.OnPointer(pointer));
    }
}
