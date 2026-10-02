using System.Windows;
using System.Windows.Media;

namespace MathPhys.Ink.Plugin.CircuitKit;

/// <summary>
/// 电路元件目录：key → （中文名、几何、内含字母、本地尺寸）。
/// </summary>
/// <remarks>
/// <para>
/// ★ 元件几何<b>直接按世界点（Scale = 1 的本地单位）定义</b>，不走"数学单位 ÷ unitWorld"：
/// 电路元件不绑定坐标系，没有 unitWorld 放大问题，怎么定义就怎么画 —— 简单路走到底。
/// 引线统一留 32 点长（两端各留），拼电路时元件间距自然合适。
/// </para>
/// <para>
/// 几何在静态初始化时解析并冻结（<see cref="Freeze"/>），全插件共享一份 ——
/// 渲染器与调色板图标都从这里拿，两处形状天然一致。
/// </para>
/// </remarks>
public static class CircuitSymbols
{
    /// <summary>一个元件的定义。</summary>
    /// <param name="Key">存档键（Texts["symbol"]）。</param>
    /// <param name="Name">中文名（调色板按钮 + 状态栏）。</param>
    /// <param name="StrokeGeom">描边几何（引线、轮廓 —— 线宽画）。</param>
    /// <param name="FillGeom">填充几何（电池负极板、二极管三角 —— 实心）。</param>
    /// <param name="Letter">元件内字母（电流表 A / 电压表 V…，随对象缩放，属于元件本体）。</param>
    /// <param name="Size">本地尺寸（= 描边几何包围盒；命中与选中框用它）。</param>
    /// <param name="PinOffsets">接线柱（引脚）的本地坐标。M15 起有了"引脚"概念：
    /// 吸附、导线绑定、接线柱小圆点三处共用这一份声明。</param>
    public sealed record Def(
        string Key, string Name, Geometry StrokeGeom, Geometry? FillGeom,
        string? Letter, Size Size, Point[] PinOffsets);

    /// <summary>目录（顺序即调色板顺序）。</summary>
    public static readonly IReadOnlyList<Def> All = Build();

    /// <summary>按键取定义；取不到返回 <c>null</c>（调用方按占位处理）。</summary>
    public static Def? Find(string? key)
        => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));

    // ---------------------------------------------------------------- 定义

    private static IReadOnlyList<Def> Build()
    {
        return new List<Def>
        {
            // ---- 电源与开关 ----
            Sym("cell", "电池",
                P("M -32,0 L -12,0 M 12,0 L 32,0 M -12,-18 L -12,18"),
                P("M 12,-9 L 16,-9 L 16,9 L 12,9 Z"), null, S(64, 36)),

            Sym("batt", "电池组",
                P("M -32,0 L -21,0 M -9,0 L 3,0 M 15,0 L 32,0"
                  + " M -21,-18 L -21,18 M 3,-18 L 3,18"),
                P("M -9,-9 L -5,-9 L -5,9 L -9,9 Z M 15,-9 L 19,-9 L 19,9 L 15,9 Z"),
                null, S(64, 36)),

            Sym("swOpen", "开关（断开）",
                P("M -32,0 L -10,0 M 10,0 L 32,0 M -10,0 L 16,-16"),
                Dots(new[] { (-10.0, 0.0), (10.0, 0.0) }), null, S(64, 20)),

            Sym("swClosed", "开关（闭合）",
                P("M -32,0 L -10,0 M 10,0 L 32,0 M -10,0 L 10,0"),
                Dots(new[] { (-10.0, 0.0), (10.0, 0.0) }), null, S(64, 12)),

            // ---- 电表（圆内字母随对象缩放，属于元件本体）----
            Sym("ammeter", "电流表",
                P("M -32,0 L -16,0 M 16,0 L 32,0 M 0,-16 A 16 16 0 1 1 0,16 A 16 16 0 1 1 0,-16 Z"),
                null, "A", S(64, 32)),

            Sym("voltmeter", "电压表",
                P("M -32,0 L -16,0 M 16,0 L 32,0 M 0,-16 A 16 16 0 1 1 0,16 A 16 16 0 1 1 0,-16 Z"),
                null, "V", S(64, 32)),

            Sym("galvano", "灵敏电流计",
                P("M -32,0 L -16,0 M 16,0 L 32,0 M 0,-16 A 16 16 0 1 1 0,16 A 16 16 0 1 1 0,-16 Z"),
                null, "G", S(64, 32)),

            // ---- 常用元件 ----
            Sym("bulb", "小灯泡",
                P("M -32,0 L -12,0 M 12,0 L 32,0 M 0,-12 A 12 12 0 1 1 0,12 A 12 12 0 1 1 0,-12 Z"
                  + " M -8,-8 L 8,8 M 8,-8 L -8,8"),
                null, null, S(64, 24)),

            Sym("res", "电阻",
                P("M -32,0 L -20,0 M 20,0 L 32,0 M -20,-10 L 20,-10 L 20,10 L -20,10 Z"),
                null, null, S(64, 20)),

            Sym("rheo", "滑动变阻器",
                P("M -32,0 L -20,0 M 20,0 L 32,0 M -20,-10 L 20,-10 L 20,10 L -20,10 Z"
                  + " M -22,-20 L 14,-20 M 14,-20 L 14,-12 M 10,-18 L 18,-22"),
                null, null, S(64, 42)),

            Sym("cap", "电容",
                P("M -32,0 L -5,0 M 5,0 L 32,0 M -5,-15 L -5,15 M 5,-15 L 5,15"),
                null, null, S(64, 30)),

            Sym("ind", "电感",
                P("M -32,0 L -24,0 A 6 6 0 0 1 -12,0 A 6 6 0 0 1 0,0"
                  + " A 6 6 0 0 1 12,0 A 6 6 0 0 1 24,0 L 32,0"),
                null, null, S(64, 14)),

            Sym("diode", "二极管",
                P("M -32,0 L -8,0 M 8,0 L 32,0 M 8,-12 L 8,12"),
                P("M -8,-10 L -8,10 L 8,0 Z"), null, S(64, 24)),

            Sym("motor", "电动机",
                P("M -32,0 L -14,0 M 14,0 L 32,0 M 0,-14 A 14 14 0 1 1 0,14 A 14 14 0 1 1 0,-14 Z"),
                null, "M", S(64, 28)),

            Sym("solenoid", "通电螺线管",
                P("M -32,0 L -25,0 A 5 5 0 0 1 -15,0 A 5 5 0 0 1 -5,0 A 5 5 0 0 1 5,0"
                  + " A 5 5 0 0 1 15,0 A 5 5 0 0 1 25,0 L 32,0"),
                null, null, S(64, 12)),

            // ---- 电源符号（交流）与接地 ----
            Sym("ac", "交流电源",
                P("M -32,0 L -14,0 M 14,0 L 32,0 M 0,-14 A 14 14 0 1 1 0,14 A 14 14 0 1 1 0,-14 Z"
                  + " M -7,0 C -5,-7 -2,-7 0,0 C 2,7 5,7 7,0"),
                null, null, S(64, 28)),

            Sym("ground", "接地",
                P("M 0,-20 L 0,2 M -14,2 L 14,2 M -9,8 L 9,8 M -4,14 L 4,14"),
                null, null, S(28, 34), new[] { new Point(0, -20) }),
        };
    }

    /// <summary>导线的特殊定义（拖动产生，长度在对象参数里）—— 不进目录。</summary>
    public const string WireKey = "wire";

    /// <summary>导线半长（世界点）的对象参数键（Numbers）。</summary>
    public const string WireHalfLenKey = "halfLen";

    /// <summary>导线 A 端（按下起点）的绑定键（Texts），值 = "{元件Id}:{引脚序号}"。</summary>
    /// <remarks>空 = 该端没吸上（自由端）。绑定键随 Texts 走 Snapshot/Restore 与 .twb 存档，零额外格式。</remarks>
    public const string WireAKey = "wireA";

    /// <summary>导线 B 端（抬笔终点）的绑定键（Texts），格式同 <see cref="WireAKey"/>。</summary>
    public const string WireBKey = "wireB";

    /// <summary>元件接线柱小圆点的半径（世界点）—— 与导线接线点同尺寸，视觉上"这是一对"。</summary>
    public const double PinDotRadius = 2.5;

    /// <summary>导线两端接线点的半径（世界点）。</summary>
    public const double WireDotRadius = 2.5;

    /// <summary>导线拖动的最小长度（世界点）：防手抖落成零长线。</summary>
    public const double WireMinLength = 12.0;

    // ---------------------------------------------------------------- 小工具

    /// <summary>标准双端引脚：引线两端 (±32, 0)。全部双端元件共用这一份（只读约定，无人改它）。</summary>
    private static Point[] Pins2() => new[] { new Point(-32, 0), new Point(32, 0) };

    /// <summary>导线引脚：随半长动态算（对象自己的本地系）。</summary>
    public static Point[] WirePins(double half)
        => new[] { new Point(-half, 0), new Point(half, 0) };

    /// <summary>生成绑定键文本："{元件Id}:{引脚序号}"。</summary>
    public static string PinTag(string objectId, int pinIndex)
        => objectId.ToString() + ":" + pinIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>解析绑定键文本；格式不对返回 <c>false</c>（手改存档等，按"没绑定"处理）。</summary>
    public static bool TryParsePinTag(string tag, out string objectId, out int pinIndex)
    {
        objectId = "";
        pinIndex = -1;
        if (string.IsNullOrEmpty(tag)) return false;

        int colon = tag.LastIndexOf(':');
        if (colon <= 0 || colon == tag.Length - 1) return false;
        if (!int.TryParse(tag[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture, out pinIndex))
        {
            return false;
        }

        objectId = tag[..colon];
        return objectId.Length > 0 && pinIndex >= 0;
    }

    /// <summary>解析路径串（缩写语法）。静态一次性，失败即抛 —— 定义写错要在启动时炸出来。</summary>
    private static Geometry P(string path)
    {
        var geometry = Geometry.Parse(path);
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }

    /// <summary>接线点（实心小圆）—— 开关铰链等。</summary>
    private static Geometry Dots((double X, double Y)[] points)
    {
        var group = new GeometryGroup();
        foreach (var (x, y) in points)
        {
            group.Children.Add(new EllipseGeometry(new Point(x, y), 2.2, 2.2));
        }

        if (group.CanFreeze) group.Freeze();
        return group;
    }

    private static Def Sym(string key, string name, Geometry stroke, Geometry? fill, string? letter, Size size, Point[]? pins = null)
        => new(key, name, stroke, fill, letter, size, pins ?? Pins2());

    private static Size S(double w, double h) => new(w, h);
}
