using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using MathPhys.Ink.Design;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;
using MathPhys.Ink.Tools;

namespace MathPhys.Ink.ViewModels;

/// <summary>
/// 工具栏上的一颗按钮 —— 它是 <see cref="ITool"/> 在界面上的投影。
/// </summary>
/// <remarks>
/// <para>
/// 有了它，工具栏不再硬编码任何工具：注册表里有什么工具，界面就自动出现什么按钮
/// （M7.1 新增，M7.2 起插件带来的工具也会自己长出来）。
/// </para>
/// <para>
/// M20 S4 起它多担一件事：<b>代表一个"工具族"</b>。一族有多个成员（直尺 / 自由角直尺、
/// 三角板 45° / 60° / 沿边画线…），界面上只留一颗瓦片 + 一个展开箭头，
/// 成员列表进二级菜单。于是可见瓦片从 22 个降到 16 个，而<b>功能一个没少</b>。
/// </para>
/// <para>
/// 分组与图标<b>不在工具自己身上</b>（<c>ITool</c> 不能加成员，见
/// <see cref="ToolCatalog"/> 的说明），而是构造时从目录表查出来的。
/// </para>
/// </remarks>
public sealed class ToolButtonViewModel : ObservableObject
{
    private bool _isActive;
    private bool _variantsOpen;
    private bool _anyVariantActive;

    /// <param name="tool">工具本身（族成员各自一个）。</param>
    public ToolButtonViewModel(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        Id = tool.Id;
        DisplayName = tool.DisplayName;
        ToolTip = ComposeToolTip(tool);

        var entry = ToolCatalog.Find(tool.Id);
        Group = entry?.Group ?? ToolGroup.Other;

        // 短名没登记就退回工具自己的显示名 —— 宁可字挤一点，也不要瓦片上有字没字之间空着。
        TileLabel = entry?.TileLabel ?? tool.DisplayName;

        // ★ 图标键也从目录表来。取不到时给 null，模板里会退成一个通用方框 ——
        //   不是"看不见"，而是"看得出它没配图标"（好过静默消失）。
        Icon = entry is null ? null : Tokens.Geometry(entry.IconKey);
    }

    /// <summary>工具 Id，切换时回传给宿主。</summary>
    public string Id { get; }

    /// <summary>工具自己的完整名字（悬停提示与二级菜单里用它，<b>不缩写</b>）。</summary>
    public string DisplayName { get; }

    /// <summary>瓦片上的短名（76 宽的格子放得下的那个）。</summary>
    public string TileLabel { get; }

    /// <summary>按钮提示。</summary>
    public string ToolTip { get; }

    /// <summary>图标几何；<c>null</c> = 目录表里没登记（或图标键拼错，harness 会抓）。</summary>
    public Geometry? Icon { get; }

    /// <summary>属于哪一组。</summary>
    public ToolGroup Group { get; }

    /// <summary>
    /// 这一族的成员（含代表自己，代表排第一）；不是族则为空集合。
    /// </summary>
    /// <remarks>
    /// ★ 成员是<b>独立的对象</b>而不是"同一个 VM 换个 Id"：成员各自的选中态要独立亮，
    /// 二级菜单上要能一眼看出"当前用的是族里哪一个"。
    /// </remarks>
    public ObservableCollection<ToolButtonViewModel> Members { get; } = new();

    /// <summary>这一族叫什么（二级菜单标题）；不是族则为空串。</summary>
    public string FamilyTitle { get; private set; } = string.Empty;

    /// <summary>是不是一族（有二级菜单要展开）。</summary>
    public bool HasVariants => Members.Count > 0;

    /// <summary>
    /// 二级菜单开着没有。
    /// </summary>
    /// <remarks>
    /// <c>Popup.IsOpen</c> 与箭头的 <c>IsChecked</c> 都双向绑到它 ——
    /// 于是"点箭头开、点别处关（<c>StaysOpen=False</c>）"两条路都不需要 code-behind。
    /// </remarks>
    public bool VariantsOpen
    {
        get => _variantsOpen;
        set => SetProperty(ref _variantsOpen, value);
    }

    /// <summary>
    /// 族里除代表之外的某个成员是不是当前工具。
    /// </summary>
    /// <remarks>
    /// 用途只有一个：给展开箭头画一个小圆点。<b>没有它就会出现"看不出来现在用的是变体"</b> ——
    /// 代表瓦片不亮（它确实不是当前工具），族里也没别的提示，老师会以为工具没切过去。
    /// </remarks>
    public bool AnyVariantActive
    {
        get => _anyVariantActive;
        set => SetProperty(ref _anyVariantActive, value);
    }

    /// <summary>
    /// 是不是当前工具（按钮的选中态）。
    /// </summary>
    /// <remarks>
    /// 这个 setter <b>每次都发通知</b>，即使值没变 —— 看起来是浪费，其实是必需的：
    /// <c>ToggleButton</c> 被点击时会先自己翻转 <c>IsChecked</c>，然后才抛 <c>Click</c>。
    /// 用户点"已经选中的那颗按钮"时它翻成未选中，我们随后把它改回选中 ——
    /// 若这里因"值没变"而不发通知，UI 就会停在未选中状态，
    /// 于是<b>屏幕上的高亮与实际工具对不上</b>（点第二次才恢复，像"按钮失灵"）。
    /// </remarks>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            OnPropertyChanged();
        }
    }

    /// <summary>把这一族的成员挂上去（由 <c>BuildToolButtons</c> 调用，只在构造后立刻调一次）。</summary>
    public void AttachFamily(string familyTitle, IEnumerable<ToolButtonViewModel> members)
    {
        FamilyTitle = familyTitle;
        Members.Clear();

        foreach (var member in members) Members.Add(member);

        OnPropertyChanged(nameof(HasVariants));
        OnPropertyChanged(nameof(FamilyTitle));
    }

    /// <summary>把快捷键转成按钮提示里的文字（<c>1</c> / <c>2</c>…），非数字键返回空串。</summary>
    private static string ShortcutText(Key? key)
    {
        if (key is not { } k) return string.Empty;

        if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return ((int)(k - Key.NumPad0)).ToString();

        return string.Empty;
    }

    /// <summary>工具提示 = 工具自己的说明 + 快捷键（没有数字快捷键就不加后缀）。</summary>
    private static string ComposeToolTip(ITool tool)
    {
        string shortcut = ShortcutText(tool.Shortcut);
        return shortcut.Length == 0 ? tool.ToolTip : $"{tool.ToolTip}（快捷键 {shortcut}）";
    }
}
