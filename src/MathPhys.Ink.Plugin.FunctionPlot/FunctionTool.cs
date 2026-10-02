using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.FunctionPlot;

/// <summary>本插件注册的工具 Id。</summary>
public static class FunctionToolIds
{
    /// <summary>函数图像。快捷键 0（接在坐标系的 9 之后）。</summary>
    public const string Id = "function";
}

/// <summary>
/// 函数图像工具（创建型）：在画布上<b>单击一下</b> → 弹窗输入表达式与参数 → 落成一个函数对象，
/// 自动绑定离按下点最近的坐标系（没有则先放一个默认坐标系），之后<b>跟随</b>坐标系移动/旋转/缩放。
/// </summary>
/// <remarks>
/// <b>为什么要"单击"而不是"拖出尺寸"</b>：本工具只需要一个锚点（决定绑到哪张图上），
/// 不像直尺 / 三角板那样要靠拖动定长度。Step 4 第一版把直尺那套"必须拖动 ≥2 世界单位"的门槛
/// 抄了过来，于是单击被静默丢弃 —— 老师看到的现象就是"点了很多次才偶尔弹出一次窗口"。
/// 这里明确：<b>按下过就弹窗</b>，锚点取按下的那一点。
/// <para>
/// <b>绑定（零契约改动）</b>：遍历 <see cref="IGfxObjectHost.Objects"/> 过滤 <c>Kind=="coordsystem"</c>，
/// 优先取"把按下点包在内部"的那个坐标系（同分取数学原点最近的），都不包含时退回原点最近。
/// 落成时写入 <c>Texts["bindTo"]=坐标系Id</c> 与相对偏移 <c>Numbers[bindRelX/bindRelY/bindRelRot]</c>；
/// <b>宿主</b>每次改动后按这些值重算本对象位姿 ⇒ 坐标系一挪，曲线自己跟着走。
/// </para>
/// <para>
/// 为什么"跟随"必须由宿主算：渲染器只拿得到<b>自己这一个对象</b>（<see cref="IGfxObjectRef"/>），
/// 看不到画布上的别人。"我该跟着谁、他现在在哪"只有掌握全部对象的存储层答得出来。
/// </para>
/// </remarks>
public sealed class FunctionTool : ITool, IGfxTool
{
    // 与坐标系插件约定的参数键名（跨插件字符串约定，未在契约里集中定义）
    private const string SysKind = "coordsystem";
    private const string SysUnitKey = "unitWorld";
    private const string SysXMinKey = "xMin";
    private const string SysXMaxKey = "xMax";
    private const string SysYMinKey = "yMin";
    private const string SysYMaxKey = "yMax";
    private const double DefaultUnit = 28.35;     // 1 cm 世界长

    // ---------------------------------------------------------------- 绑定键
    // 与宿主 GfxObjectStore 约定：Texts 里放这些键，宿主就会把本对象的位姿
    // 从"目标对象"推导出来（跟随移动/旋转/缩放）。

    /// <summary>绑定目标对象 Id 的存档键。</summary>
    public const string BindToKey = "bindTo";

    /// <summary>Step 4 第一版写的绑定键名（旧存档兼容，读取时作为回退）。</summary>
    public const string LegacySystemIdKey = "systemId";

    /// <summary>相对目标原点的偏移（目标本地系、世界长度）。</summary>
    public const string BindRelXKey = "bindRelX";
    public const string BindRelYKey = "bindRelY";

    /// <summary>相对目标的额外旋转（度）。</summary>
    public const string BindRelRotKey = "bindRelRot";

    /// <summary>
    /// 多曲线配色板：彼此区分得开，也和坐标系（通常黑/深色轴）区分得开。
    /// </summary>
    /// <remarks>
    /// 老师在一张坐标系上连画 y=x^2、y=2x 两条线，若都用同一个墨色就完全分不清哪条是哪条 ——
    /// 所以颜色不能沿用"笔色"，要按"这张图上已画了几条"依次取色。
    /// </remarks>
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xE5, 0x39, 0x35),   // 红
        Color.FromRgb(0x1E, 0x88, 0xE5),   // 蓝
        Color.FromRgb(0x2E, 0x9E, 0x4F),   // 绿
        Color.FromRgb(0xF5, 0x7C, 0x00),   // 橙
        Color.FromRgb(0x8E, 0x24, 0xAA),   // 紫
        Color.FromRgb(0x00, 0x83, 0x8F),   // 青
    };

    private IToolContext? _context;
    private bool _drawing;
    private Point _anchor;

    /// <summary>
    /// 弹窗动作（默认真开窗）。
    /// </summary>
    /// <remarks>
    /// 留这个缝是为了验收：无 UI 的 harness 开不了模态窗，但"单击到底会不会弹窗"
    /// （即 S4 现场那个"点了没反应"）恰恰是最需要钉死的一条 —— 所以这里可替换。
    /// </remarks>
    public Action<Point> OpenInputDialog { get; set; }

    public FunctionTool() => OpenInputDialog = ShowInputDialog;

    public string Id => FunctionToolIds.Id;
    public string DisplayName => "函数图像";
    public string ToolTip
        => "函数图像：在画布上单击一下弹出输入窗，拼写表达式（可打字或用数学键）、设参数与范围，"
        + "确定后挂到该处的坐标系上；曲线颜色自动区分，并跟随坐标系移动/旋转。快捷键 0";

    public Key? Shortcut => Key.D0;
    public bool UsesInkLayer => false;
    public bool NeedsPointer => true;
    public ToolInputKind InputKind => ToolInputKind.None;
    public ToolInkMode InkMode => ToolInkMode.None;
    public Cursor? Cursor => Cursors.None;

    public void Activate(IToolContext context)
    {
        _context = context;
        Reset();
    }

    public void Deactivate()
    {
        Reset();
        _context?.ClearPreview();
    }

    public void OnPointer(ToolPointer pointer)
    {
        var context = _context;
        if (context is null) return;

        switch (pointer.Phase)
        {
            case ToolPointerPhase.Down:
                _anchor = pointer.World;
                _drawing = true;
                context.SetStatus("函数图像：松开即弹出输入窗");
                break;

            case ToolPointerPhase.Up:
                if (!_drawing) return;
                _drawing = false;

                // 单击即可 —— 本工具不像直尺那样"拖出尺寸"，锚点在按下那一刻就定了。
                // （曾经误加"必须拖动 ≥2 世界单位"的门槛：单击被静默丢弃，
                //   表现成"点很多次才偶尔弹出一次窗口"。绝不要再加回来。）
                context.SetStatus("函数图像：正在输入表达式…");
                OpenInputDialog(_anchor);
                break;
        }
    }

    // ---------------------------------------------------------- 弹窗

    private void ShowInputDialog(Point anchor)
    {
        // 预先按锚点处的坐标系定好默认范围：曲线默认就铺满这张图，不用老师再改一遍。
        var initial = BuildInitialSpec(anchor);
        var window = new FunctionInputWindow(initial, spec => Commit(spec, anchor));

        // 真实宿主下 Application.Current 存在；无 UI 的 harness 不走这条路径（直接调 ApplySpec）
        if (Application.Current?.MainWindow is Window owner)
            window.Owner = owner;

        window.ShowDialog();
    }

    private FunctionSpec? BuildInitialSpec(Point anchor)
    {
        var gfx = _context?.Gfx;
        if (gfx is null) return null;

        var sys = PickSystem(anchor, gfx);
        if (sys is null) return null;

        return new FunctionSpec
        {
            Expr = "x^2",
            XMin = sys.GetNumber(SysXMinKey, -10),
            XMax = sys.GetNumber(SysXMaxKey, 10),
            YMin = sys.GetNumber(SysYMinKey, -10),
            YMax = sys.GetNumber(SysYMaxKey, 10),
        };
    }

    // ---------------------------------------------------------- 落对象（绑定坐标系）

    /// <summary>
    /// 公开的可测入口：直接用一个已确定的 <see cref="FunctionSpec"/> 落成对象（绕过弹窗）。
    /// 验收 harness 用它来断言绑定 / 孤立 / 配色 / 滑块联动等行为。
    /// </summary>
    public bool ApplySpec(FunctionSpec spec, Point anchor)
    {
        if (_context is null) return false;
        if (_context.Gfx is null)
        {
            _context.SetStatus("函数图像：当前程序不支持图形对象");
            return false;
        }
        Commit(spec, anchor);
        return true;
    }

    private void Commit(FunctionSpec spec, Point anchor)
    {
        var gfx = _context!.Gfx!;

        var sys = PickSystem(anchor, gfx);

        Point sysCenter;
        double unit, sysScale, rot, axisWidth;
        Color axisColor;
        string sysId;

        if (sys is null)
        {
            // 没有坐标系：先放一个默认 [-10,10] 的坐标系，再绑定
            sysCenter = anchor;
            unit = DefaultUnit;
            sysScale = 1.0;
            rot = 0;
            axisColor = _context!.PenColor;
            axisWidth = _context.PenWorldWidth;

            sysId = gfx.Add(new GfxDraft
            {
                Kind = SysKind,
                Center = anchor,
                RotationDegrees = 0,
                Scale = 1.0,
                Color = _context.PenColor,
                LineWorldWidth = _context.PenWorldWidth,
                Numbers = new Dictionary<string, double>
                {
                    [SysUnitKey] = DefaultUnit,
                    [SysXMinKey] = -10, [SysXMaxKey] = 10,
                    [SysYMinKey] = -10, [SysYMaxKey] = 10,
                    ["step"] = 1, ["showGrid"] = 1, ["showLabels"] = 1, ["lockAspect"] = 1,
                },
            });
        }
        else
        {
            sysCenter = sys.Center;
            unit = sys.GetNumber(SysUnitKey, DefaultUnit);
            sysScale = sys.Scale > 0 && !double.IsNaN(sys.Scale) && !double.IsInfinity(sys.Scale) ? sys.Scale : 1.0;
            rot = sys.RotationDegrees;
            axisColor = sys.Color;
            axisWidth = sys.LineWorldWidth > 0 ? sys.LineWorldWidth : _context.PenWorldWidth;
            sysId = sys.Id;
        }

        if (double.IsNaN(unit) || double.IsInfinity(unit) || unit <= 0) unit = DefaultUnit;

        // 颜色：不跟笔色走，按"这张图上已画了几条曲线"取配色板下一格，并避开与轴色过近的颜色。
        int usedOnAxis = CountCurvesOn(sysId, gfx);
        var curveColor = ColorForCurve(usedOnAxis, axisColor);

        // 函数范围：老师在弹窗里可调（默认已被 ShowInputDialog 预置成坐标系的范围）
        var numbers = new Dictionary<string, double>
        {
            [SysXMinKey] = spec.XMin, [SysXMaxKey] = spec.XMax,
            [SysYMinKey] = spec.YMin, [SysYMaxKey] = spec.YMax,
            [SysUnitKey] = unit,           // 冗余保存，孤立（目标被删）时仍能自己算粗细
            [BindRelXKey] = 0, [BindRelYKey] = 0, [BindRelRotKey] = 0,
            // 标注开关（1=显示）。存 Numbers 而不是新字段类型：渲染器读 GetNumber 即可，
            // 存档 / 撤销 / 面板全都不用动（与坐标系"显示网格"是同一种表达法）。
            [FunctionRenderer.ShowZerosKey] = spec.ShowZeros ? 1.0 : 0.0,
            [FunctionRenderer.ShowExtremaKey] = spec.ShowExtrema ? 1.0 : 0.0,
            [FunctionRenderer.ShowIntersectionsKey] = spec.ShowIntersections ? 1.0 : 0.0,
        };
        foreach (var kv in spec.Parameters)
            numbers["param_" + kv.Key] = kv.Value;

        gfx.Add(new GfxDraft
        {
            Kind = FunctionRenderer.KindName,
            Center = sysCenter,                    // 数学原点落在坐标系原点上（世界坐标）
            RotationDegrees = rot,                 // 与坐标系同向
            Scale = unit * sysScale,               // 每数学单位 → 世界长度（含坐标系自身缩放）
            Color = curveColor,
            LineWorldWidth = axisWidth * sysScale, // 与坐标轴同粗（轴会随坐标系缩放一起变粗）
            Numbers = numbers,
            Texts = new Dictionary<string, string>
            {
                ["expr"] = spec.Expr,
                [BindToKey] = sysId,
                [LegacySystemIdKey] = sysId,       // 旧存档兼容键（同值，双保险）
            },
        });

        _context.SetStatus($"函数图像：y = {spec.Expr}（本图第 {usedOnAxis + 1} 条，{ColorName(curveColor)}色）");
    }

    /// <summary>给老师看的颜色名（取配色板里最接近的那个）。</summary>
    private static string ColorName(Color c)
    {
        int bestIndex = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < Palette.Length; i++)
        {
            int d = Math.Abs(Palette[i].R - c.R) + Math.Abs(Palette[i].G - c.G) + Math.Abs(Palette[i].B - c.B);
            if (d < bestDistance) { bestDistance = d; bestIndex = i; }
        }

        return bestIndex switch
        {
            0 => "红",
            1 => "蓝",
            2 => "绿",
            3 => "橙",
            4 => "紫",
            _ => "青",
        };
    }

    /// <summary>
    /// 挑要绑定的坐标系：<b>优先"把按下点包在里面"的那个</b>（点在图里 ⇒ 就是要往这张图上画），
    /// 其中取数学原点最近的；都不包含时退回"原点最近"。
    /// </summary>
    /// <remarks>
    /// 只按原点距离挑会有个恼人的表现：一张图铺得很大、老师在图的右下角单击，
    /// 结果因为另一张小图的原点恰好在附近，曲线落到了那张小图上。
    /// </remarks>
    private static IGfxObjectRef? PickSystem(Point anchor, IGfxObjectHost gfx)
    {
        IGfxObjectRef? insideBest = null;
        double insideSqr = double.MaxValue;
        IGfxObjectRef? nearBest = null;
        double nearSqr = double.MaxValue;

        foreach (var o in gfx.Objects)
        {
            if (o.Kind != SysKind) continue;

            double d2 = (o.Center - anchor).LengthSquared;
            if (d2 < nearSqr) { nearSqr = d2; nearBest = o; }

            // BoundsWorld 可能因参数异常为空矩形，Contains 对空矩形恒为 false —— 安全
            if (o.BoundsWorld.Contains(anchor) && d2 < insideSqr) { insideSqr = d2; insideBest = o; }
        }

        return insideBest ?? nearBest;
    }

    /// <summary>
    /// 验收入口：某张坐标系上已经画了几条曲线（配色就是按这个数往下取格子）。
    /// </summary>
    /// <remarks>
    /// 暴露出来是为了能单独断言"数得对不对"与"取色对不对"——
    /// 两者混在一起时，颜色不对根本分不清是数错了还是取错了。
    /// </remarks>
    public static int CountCurvesOn(string systemId, IGfxObjectHost gfx)
    {
        int used = 0;
        foreach (var o in gfx.Objects)
        {
            if (o.Kind != FunctionRenderer.KindName) continue;
            if (!string.Equals(BoundTargetOf(o), systemId, StringComparison.Ordinal)) continue;
            used++;
        }

        return used;
    }

    /// <summary>验收入口：同一张图上的第 N 条曲线（从 0 数）该用什么颜色。</summary>
    /// <remarks>
    /// <b>先在候选里筛、再取模</b>：若写成"取模后发现撞色再往后挪一格"，第 1 条跳过红得蓝、
    /// 第 2 条取模正好也是蓝，两条曲线就撞成同一个颜色了。
    /// </remarks>
    public static Color ColorForCurve(int usedOnSameAxis, Color axisColor)
    {
        var allowed = new List<Color>(Palette.Length);
        foreach (var c in Palette)
        {
            if (!IsTooCloseTo(c, axisColor)) allowed.Add(c);
        }

        if (allowed.Count == 0) return Palette[usedOnSameAxis % Palette.Length];
        return allowed[usedOnSameAxis % allowed.Count];
    }

    /// <summary>两个颜色是否"看上去差不多"（逐通道绝对差之和，阈值 160）。</summary>
    /// <remarks>
    /// 阈值定得偏大是故意的：坐标轴若是暖色（红笔画的轴），配色板里的"红"必须被判为撞色而跳过，
    /// 否则老师会看到"一条和坐标轴几乎同色的曲线"。与黑/深色轴的距离都 ≥ 274，所以正常情况不吃亏。
    /// </remarks>
    private static bool IsTooCloseTo(Color a, Color b)
        => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 160;

    /// <summary>读一个对象绑定的目标 Id（新键优先，旧键回退）。</summary>
    private static string BoundTargetOf(IGfxObjectRef o)
    {
        string id = o.GetText(BindToKey, "");
        return string.IsNullOrEmpty(id) ? o.GetText(LegacySystemIdKey, "") : id;
    }

    private void Reset()
    {
        _drawing = false;
        _anchor = default;
    }
}
