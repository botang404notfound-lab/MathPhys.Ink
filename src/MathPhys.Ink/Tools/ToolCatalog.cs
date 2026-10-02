using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Tools;

/// <summary>
/// 工具栏上的分组（M20 S4）。
/// </summary>
/// <remarks>
/// ★ <b>枚举的声明顺序 == 界面上组的渲染顺序</b>，这是刻意的：
/// 组顺序是"讲台上的动线"（先拿笔 → 再拿学科工具 → 再管文档 → 最后是系统杂项），
/// 不是随手排的。把它写进枚举的序号里，界面按序号渲染，
/// 于是"改顺序"只有一处可改，不存在"枚举一个序、XAML 又一个序"的错位。
/// <para>
/// 兜底组 <see cref="Other"/> 排最后：新插件带来了目录表里没有的工具时，
/// 它们会落到这里，<b>看得见、点得着</b>，而不是从工具栏上悄悄消失。
/// </para>
/// </remarks>
public enum ToolGroup
{
    /// <summary>笔类：笔 / 橡皮 / 手（再加上墨色、笔宽这些笔属性）。</summary>
    Ink = 0,

    /// <summary>图形：全部学科工具（直尺、三角板、矢量、电路…）。</summary>
    Gfx = 1,

    /// <summary>文档：打开 / 新建 / 保存 / 导出。</summary>
    Doc = 2,

    /// <summary>系统：视口、历史、图层、题号、主题这类"白板本身"的操作。</summary>
    System = 3,

    /// <summary>兜底：目录表里没登记的工具。<b>必须排最后</b>。</summary>
    Other = 4,
}

/// <summary>组的显示信息（标题）。</summary>
/// <param name="Group">组。</param>
/// <param name="Title">组标题（行内窄标签上的字）。</param>
public sealed record ToolGroupInfo(ToolGroup Group, string Title);

/// <summary>
/// 一个工具在工具栏上的"长相"。
/// </summary>
/// <param name="Id">工具 Id，与 <c>ITool.Id</c> 一字不差。</param>
/// <param name="Group">归到哪一组。</param>
/// <param name="IconKey">图标资源键（<c>Icon.*</c>）。</param>
/// <param name="TileLabel">瓦片上的短名（76 宽的格子里放得下的那个）。</param>
/// <param name="VariantOf">
/// 所属工具族的<b>代表</b> Id；<c>null</c> 表示它自己就是代表（或不属于任何族）。
/// </param>
/// <param name="FamilyTitle">族名（二级菜单的标题）；只有代表才填。</param>
public sealed record ToolCatalogEntry(
    string Id,
    ToolGroup Group,
    string IconKey,
    string TileLabel,
    string? VariantOf = null,
    string? FamilyTitle = null);

/// <summary>
/// 工具的<b>界面目录表</b>：Id → 分组 / 图标 / 短名 / 顺序 / 所属工具族。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这张表：<c>ITool</c> 里<b>没有</b>"我归哪组、我用哪个图标"这两个信息，
/// 而且<b>不能加</b> —— 加了成员，所有已编译的插件在加载时都会
/// <c>TypeLoadException</c>（这是本项目契约的硬约束，见 AGENTS.md）。
/// 所以分组与图标只能由宿主在<b>外面</b>记一份，插件一行都不用改。
/// </para>
/// <para>
/// 三层各管各的，别混：
/// <list type="bullet">
/// <item>插件 <c>ITool</c>：能力（Id / 显示名 / 快捷键 / 怎么落墨）—— 插件自己的事；</item>
/// <item><b>本表</b>：怎么摆、画什么图标、和谁同族 —— 宿主的排版决策；</item>
/// <item><c>Design/Icons.xaml</c>：图标几何本身（脚本生成，勿手改）。</item>
/// </list>
/// </para>
/// <para>
/// ★ 表里的顺序<b>就是渲染顺序</b>；<see cref="Entries"/> 按声明顺序返回，
/// 组内顺序即声明顺序。工具族的成员必须<b>紧跟在自己的代表之后</b>声明 ——
/// 这样"折叠进二级菜单"只是把连续的几项收起来，不需要额外的定位逻辑。
/// </para>
/// <para>
/// ★ 覆盖性有回归护栏：harness 会断言"<c>src</c> 里声明的每个工具 Id 都在本表里"
/// （从插件源码里抽 <c>*ToolIds</c> 常量），漏登记会当场 FAIL ——
/// 表现为"新加的工具在工具栏上找不到"，那是本轮最不想留的坑。
/// </para>
/// </remarks>
public static class ToolCatalog
{
    /// <summary>组的顺序与标题（顺序 = 枚举声明顺序 = 渲染顺序）。</summary>
    public static readonly IReadOnlyList<ToolGroupInfo> Groups = new[]
    {
        new ToolGroupInfo(ToolGroup.Ink, "笔类"),
        new ToolGroupInfo(ToolGroup.Gfx, "图形"),
        new ToolGroupInfo(ToolGroup.Doc, "文档"),
        new ToolGroupInfo(ToolGroup.System, "系统"),
        new ToolGroupInfo(ToolGroup.Other, "其他"),
    };

    /// <summary>
    /// 工具目录（<b>声明顺序 = 渲染顺序</b>）。
    /// </summary>
    /// <remarks>
    /// 短名的取法：76 宽的瓦片在 13 号字下大约放得下 4 个汉字，
    /// 所以"题号标记""沿边画线""正交分解"这类四字名<b>照原样用</b>（不要再缩写），
    /// 只有确实放不下的才缩：三角板·45° → 三角45、直尺·自由角 → 自由角、
    /// GeoGebra 演示 → GeoGebra。完整名字在悬停提示里，一个都没丢。
    /// </remarks>
    public static readonly IReadOnlyList<ToolCatalogEntry> Entries = new[]
    {
        // ---------------- 笔类 ----------------
        new ToolCatalogEntry(ToolIds.Pen, ToolGroup.Ink, "Icon.Pen", "笔"),
        new ToolCatalogEntry(ToolIds.Eraser, ToolGroup.Ink, "Icon.Eraser", "橡皮"),
        new ToolCatalogEntry(ToolIds.Hand, ToolGroup.Ink, "Icon.Hand", "手"),

        // ---------------- 图形 ----------------
        new ToolCatalogEntry("qmark", ToolGroup.Gfx, "Icon.QuestionMark", "题号标记"),

        // ★ 注意：选择工具的 Id 也放在 Abstractions 的 ToolIds 里（与内置三个同源），
        //   但它语义上属于"图形"，所以这里按语义归到 Gfx 组。
        new ToolCatalogEntry(ToolIds.GfxSelect, ToolGroup.Gfx, "Icon.GfxSelect", "选择"),
        new ToolCatalogEntry("circuit", ToolGroup.Gfx, "Icon.Circuit", "电路"),
        // 坐标系族（M23）：带网格的代表 + 无网格平面 + 空间轴测，折叠进二级菜单。
        new ToolCatalogEntry("coordsystem", ToolGroup.Gfx, "Icon.CoordSystem", "坐标系", FamilyTitle: "坐标系"),
        new ToolCatalogEntry("coordsystem-plain", ToolGroup.Gfx, "Icon.CoordSystemPlain", "无网格", VariantOf: "coordsystem"),
        new ToolCatalogEntry("coordsystem-3d", ToolGroup.Gfx, "Icon.CoordSystem3D", "空间坐标", VariantOf: "coordsystem"),
        new ToolCatalogEntry("function", ToolGroup.Gfx, "Icon.Function", "函数图像"),
        new ToolCatalogEntry("formula", ToolGroup.Gfx, "Icon.Formula", "公式"),

        // 学科工具（M23）：图片资料库入口（电场线等，点图落到卷面）。
        new ToolCatalogEntry("subjectkit", ToolGroup.Gfx, "Icon.SubjectKit", "学科工具"),

        // 直尺族：吸附版是代表（最常用），自由角是变体。
        new ToolCatalogEntry("ruler", ToolGroup.Gfx, "Icon.Ruler", "直尺", FamilyTitle: "直尺"),
        new ToolCatalogEntry("ruler-free", ToolGroup.Gfx, "Icon.RulerFree", "自由角", VariantOf: "ruler"),

        // 三角板族：45° 是代表。
        new ToolCatalogEntry("triangle-45", ToolGroup.Gfx, "Icon.Triangle45", "三角45", FamilyTitle: "三角板"),
        new ToolCatalogEntry("triangle-60", ToolGroup.Gfx, "Icon.Triangle60", "三角60", VariantOf: "triangle-45"),
        new ToolCatalogEntry("triangle-trace", ToolGroup.Gfx, "Icon.TriangleTrace", "沿边画线", VariantOf: "triangle-45"),

        // 圆规族：整圆是代表。
        new ToolCatalogEntry("compass-circle", ToolGroup.Gfx, "Icon.CompassCircle", "圆规", FamilyTitle: "圆规"),
        new ToolCatalogEntry("compass-arc", ToolGroup.Gfx, "Icon.CompassArc", "圆弧", VariantOf: "compass-circle"),

        new ToolCatalogEntry("protractor", ToolGroup.Gfx, "Icon.Protractor", "量角器"),

        // 矢量族：4 个成员，是最大的一族，也是最该折叠的一族。
        new ToolCatalogEntry("vector", ToolGroup.Gfx, "Icon.Vector", "矢量箭头", FamilyTitle: "矢量"),
        new ToolCatalogEntry("vectorsum", ToolGroup.Gfx, "Icon.VectorSum", "合力", VariantOf: "vector"),
        new ToolCatalogEntry("vectordecompose", ToolGroup.Gfx, "Icon.VectorDecompose", "正交分解", VariantOf: "vector"),
        new ToolCatalogEntry("vectorgroup", ToolGroup.Gfx, "Icon.VectorGroup", "矢量组", VariantOf: "vector"),

        new ToolCatalogEntry("geogebra-demo", ToolGroup.Gfx, "Icon.GeoGebraDemo", "GeoGebra"),

        // 物理仿真（M22 S3）：一颗瓦片 = 一个入口。原生 WPF 运动学窗（单摆 / 弹簧 / 斜面）
        // 两个变体都带；窗里的「网页仿真」入口按 context.WebSim?.IsAvailable 置灰。
        // ★ 为什么不做成"工具族"：MainWindow.BuildToolButtons 里族成员恒 folded++ 且永不单独成瓦片，
        //   而代表不在注册表时整条跳过 ⇒ 轻量版裁掉 Web 插件后，族成员会连瓦片一起消失。
        new ToolCatalogEntry("phys-sim", ToolGroup.Gfx, "Icon.PhysicsSim", "物理仿真"),
    };

    private static readonly Dictionary<string, ToolCatalogEntry> ById = BuildIndex();

    /// <summary>按 Id 查目录项；没登记返回 <c>null</c>（调用方应把它放进「其他」组）。</summary>
    public static ToolCatalogEntry? Find(string? id)
        => id is not null && ById.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>按 Id 查组；没登记的工具落到「其他」组，<b>不会丢</b>。</summary>
    public static ToolGroup GroupOf(string? id) => Find(id)?.Group ?? ToolGroup.Other;

    /// <summary>按 Id 查示意图标键；没登记返回 <c>null</c>（界面用兜底图标）。</summary>
    public static string? IconKeyOf(string? id) => Find(id)?.IconKey;

    /// <summary>按 Id 查瓦片短名；没登记返回 <c>null</c>（界面退回工具自己的显示名）。</summary>
    public static string? TileLabelOf(string? id) => Find(id)?.TileLabel;

    /// <summary>是不是某个工具族的代表（有成员挂在它下面）。</summary>
    public static bool IsFamilyHead(string? id)
        => id is not null && Entries.Any(e => string.Equals(e.VariantOf, id, StringComparison.Ordinal));

    /// <summary>取某个代表的全部成员（含代表自己，代表排第一）。不是代表则返回空。</summary>
    public static IReadOnlyList<ToolCatalogEntry> FamilyMembers(string? headId)
    {
        if (headId is null || !IsFamilyHead(headId)) return Array.Empty<ToolCatalogEntry>();

        var head = Find(headId)!;
        var members = new List<ToolCatalogEntry> { head };

        foreach (var entry in Entries)
        {
            if (string.Equals(entry.VariantOf, headId, StringComparison.Ordinal)) members.Add(entry);
        }

        return members;
    }

    /// <summary>查一个 Id 所属族的代表 Id；它自己就是代表/不属于任何族时返回它自己。</summary>
    public static string? FamilyHeadOf(string? id)
    {
        var entry = Find(id);
        if (entry is null) return id;

        return entry.VariantOf ?? entry.Id;
    }

    /// <summary>索引：Id → 目录项。重复 Id 会在这里炸出来（启动即暴露，不留到界面上）。</summary>
    private static Dictionary<string, ToolCatalogEntry> BuildIndex()
    {
        var index = new Dictionary<string, ToolCatalogEntry>(StringComparer.Ordinal);

        foreach (var entry in Entries)
        {
            if (!index.TryAdd(entry.Id, entry))
            {
                throw new InvalidOperationException($"工具目录表里 Id 重复：{entry.Id}");
            }
        }

        return index;
    }
}
