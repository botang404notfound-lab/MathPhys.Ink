using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;

namespace MathPhys.Ink.Plugin.Formula;

/// <summary>
/// LaTeX 公式的画法 —— 插件对"图形库"这一层的贡献。
/// </summary>
/// <remarks>
/// <para>
/// 一个对象 = 一段 LaTeX（<c>Texts[LatexKey]</c>）。渲染管线：
/// WPF-Math 解析 → <c>RenderToGeometry</c> 出矢量 <see cref="GeometryGroup"/>
/// （S0 勘验的结论：几何与笔刷分离，外部 <c>DrawGeometry</c> 指定画刷即可染色）→
/// 仿射变换到"以 (0,0) 为中心、高 = <see cref="NominalWorldHeight"/> 世界点"的本地坐标系。
/// </para>
/// <para>
/// 尺寸规则：<b>高固定</b>（随对象 Scale 缩放），宽按 LaTeX 排版结果的纵横比算 ——
/// 这样"落下来"的公式不管长短都保持同一视觉高度，符合黑板板书的习惯；
/// 要更大/更小就用选择工具整体缩放。
/// </para>
/// <para>
/// ★ 本地坐标以 (0,0) 为中心（渲染器契约第二条）；几何先设 Transform 再 Freeze（工程铁律）。
/// </para>
/// </remarks>
public sealed class FormulaRenderer : IGfxObjectRenderer, IGfxParameterProvider, IGfxTextParameterSink
{
    /// <summary>对象种类；与 <see cref="GfxDraft.Kind"/> 对应。</summary>
    public const string KindName = "latexFormula";

    /// <summary>LaTeX 文本的参数键（Texts）。</summary>
    public const string LatexKey = "latex";

    /// <summary>
    /// 公式的"标准世界高"（对象 Scale = 1 时）。
    /// </summary>
    /// <remarks>
    /// A4 页宽 595 世界点，44 点 ≈ 1.55 cm —— 独立公式落在卷面上的自然高度。
    /// 选择工具的等比缩放可以随意改，这里只是"落下来那一刻"的默认值。
    /// </remarks>
    public const double NominalWorldHeight = 44.0;

    /// <summary>无效公式退化的占位框尺寸（本地单位；太小会点不中、删不掉）。</summary>
    public const double InvalidLocalWidth = 72.0;
    public const double InvalidLocalHeight = 30.0;

    public string Kind => KindName;

    // ---------------------------------------------------------------- 参数面板（文本参数）

    /// <summary>
    /// 构建「选中的公式」的参数面板：一个多行的 LaTeX 文本项。
    /// </summary>
    /// <remarks>
    /// 与函数面板同一条纪律：纯函数、不抛异常、可选接口只加不改。
    /// 公式对象只有文本袋子（没有数值参数），所以只有文本项没有数值项 ——
    /// 「只有文本项的面板」不是空面板：宿主按「文本项非空也显示」的规则处理。
    /// </remarks>
    public GfxParameterPanel? BuildPanel(IGfxObjectRef obj)
    {
        if (obj is null || !string.Equals(obj.Kind, KindName, StringComparison.Ordinal)) return null;

        string latex = obj.GetText(LatexKey, "");
        bool valid = GetEntry(latex).LocalGeometry is not null;

        return new GfxParameterPanel
        {
            Title = "公式（LaTeX）",
            Subtitle = valid ? "渲染正常" : "当前 LaTeX 无效（占位框）",
            TextFields = new[] { new GfxTextField { Key = LatexKey, Label = "LaTeX", Value = latex, IsMultiline = true } },
            Note = "不支持中文（缺字形）；中文标注请用墨迹手写。",
        };
    }

    /// <summary>
    /// 应用一次 LaTeX 修改。空文本 = 「这次不改」；坏 LaTeX 放行（渲染器退化成虚线占位框，
    /// 副标题会显示「当前 LaTeX 无效」提示老师修）。
    /// </summary>
    public IReadOnlyDictionary<string, string>? ApplyText(IGfxObjectRef obj, string key, string text)
    {
        if (obj is null) return null;
        if (!string.Equals(key, LatexKey, StringComparison.Ordinal)) return null;

        string trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return null;

        return new Dictionary<string, string>(StringComparer.Ordinal) { [LatexKey] = trimmed };
    }

    // ---------------------------------------------------------------- LaTeX → 几何（带缓存）

    /// <summary>WPF-Math 的解析器（单例，静态构造时已把内嵌数学字体装好）。</summary>
    private static readonly TexFormulaParser Parser = WpfTeXFormulaParser.Instance;

    /// <summary>渲染环境：字号 20（纯内部单位，最终尺寸由 NominalWorldHeight 归一）、Arial 作 \text 回退。</summary>
    private static readonly TexEnvironment Environment = WpfTeXEnvironment.Create(TexStyle.Display, 20.0, "Arial");

    /// <summary>解析缓存条目：几何（已变换到本地坐标并冻结）+ 本地尺寸。</summary>
    public sealed record Entry(Geometry? LocalGeometry, Size LocalSize);

    /// <summary>
    /// LaTeX → 缓存。★ 实例字段：harness 与宿主可能同时存在多个渲染器实例（见 ArrowRenderer 的教训）。
    /// </summary>
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 取 LaTeX 对应的本地几何与尺寸（无效 LaTeX 返回 <c>null</c> 几何 + 占位尺寸）。
    /// </summary>
    /// <remarks>
    /// 解析 + 矢量化不便宜（毫秒级），而 Measure/CreateVisual/选中框会反复要 —— 必须缓存。
    /// 缓存上限 128 条：公式的数量级是"一节课十几个"，真到 128 条说明在批量干活，直接清空重来最省心。
    /// </remarks>
    public Entry GetEntry(string? latex)
    {
        string key = latex ?? string.Empty;

        if (_cache.TryGetValue(key, out var hit)) return hit;

        if (_cache.Count > 128) _cache.Clear();

        var entry = Build(key);
        _cache[key] = entry;
        return entry;
    }

    /// <summary>解析 + 仿射变换；任何异常都归一成"无效"占位，不让一个坏公式带走宿主。</summary>
    private static Entry Build(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex))
        {
            return new Entry(null, new Size(InvalidLocalWidth, InvalidLocalHeight));
        }

        try
        {
            var formula = Parser.Parse(latex);
            var geometry = formula.RenderToGeometry(Environment, 20.0);
            var bounds = geometry.Bounds;

            // 空公式（如只有一个空组）：给占位，避免除零与零尺寸命中区
            if (bounds.Width <= 0 || bounds.Height <= 0 ||
                double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height))
            {
                return new Entry(null, new Size(InvalidLocalWidth, InvalidLocalHeight));
            }

            double k = NominalWorldHeight / bounds.Height;
            double width = bounds.Width * k;

            // p_local = (p_tex − bounds中心) × k ⇒ 以 (0,0) 为中心、高恰好 NominalWorldHeight
            var matrix = new Matrix(k, 0, 0, k,
                -k * (bounds.X + bounds.Width / 2.0),
                -k * (bounds.Y + bounds.Height / 2.0));

            // ★ 铁律：先设 Transform 再 Freeze
            var local = geometry.Clone();
            local.Transform = new MatrixTransform(matrix);
            if (local.CanFreeze) local.Freeze();

            return new Entry(local, new Size(width, NominalWorldHeight));
        }
        catch (Exception)
        {
            // TexParseException / 字体缺字形 / 任何 WPF-Math 内部异常 —— 一律当"无效公式"
            return new Entry(null, new Size(InvalidLocalWidth, InvalidLocalHeight));
        }
    }

    // ---------------------------------------------------------------- 渲染器契约

    /// <inheritdoc/>
    /// <remarks>纯函数：只读缓存（缓存本身由纯计算填充），不碰视觉树。</remarks>
    public Size Measure(IGfxObjectRef obj) => GetEntry(obj.GetText(LatexKey, "")).LocalSize;

    /// <inheritdoc/>
    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        var entry = GetEntry(obj.GetText(LatexKey, ""));
        return new FormulaVisual(entry.LocalGeometry, entry.LocalSize);
    }

    /// <summary>状态栏读数：给老师看用的是哪段 LaTeX（截断防刷屏）。</summary>
    public string Describe(IGfxObjectRef obj)
    {
        string latex = obj.GetText(LatexKey, "");
        bool valid = GetEntry(latex).LocalGeometry is not null;

        if (!valid) return "公式（LaTeX 无效，占位框）";

        string shown = latex.Length <= 48 ? latex : latex[..48] + "…";
        return $"公式：{shown}";
    }
}

/// <summary>
/// 公式的视觉：一个冻结的本地几何，用墨色画刷填充。
/// </summary>
/// <remarks>
/// 与 <see cref="MathPhys.Ink.Plugin.Formula"/> 其它视觉同款纪律：
/// 不设 RenderTransform / Canvas.Left（位姿由宿主施加）、MeasureOverride 返回零
/// （布局尺寸由宿主按渲染器的 Measure 摆）。
/// </remarks>
internal sealed class FormulaVisual : FrameworkElement
{
    private readonly Geometry? _geometry;
    private readonly Brush _ink;
    private readonly Pen _invalidPen;
    private readonly Size _size;

    /// <param name="geometry">本地几何（已冻结；<c>null</c> = 无效公式，画虚线占位框）。</param>
    /// <param name="size">本地尺寸（= 渲染器 Measure 的结果，占位框也按它画）。</param>
    /// <param name="inkColor">墨色。</param>
    public FormulaVisual(Geometry? geometry, Size size, Color inkColor = default)
    {
        _geometry = geometry;
        _size = size;

        var color = inkColor == default ? Color.FromRgb(0, 0, 0) : inkColor;
        _ink = FreezeIfPossible(new SolidColorBrush(color));

        var dashPen = new Pen(FreezeIfPossible(new SolidColorBrush(color)), 1.5)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
        };
        _invalidPen = FreezeIfPossible(dashPen);

        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_geometry is not null)
        {
            // 矢量填充：公式就是一组封闭轮廓，直接用墨色刷
            drawingContext.DrawGeometry(_ink, null, _geometry);
            return;
        }

        // 无效公式的占位框：虚线矩形 + 一条斜杠，一眼看出"这里有个放不出来的公式"
        double w = Math.Max(_size.Width, 8), h = Math.Max(_size.Height, 8);
        var rect = new Rect(-w / 2, -h / 2, w, h);
        drawingContext.DrawRectangle(null, _invalidPen, rect);
        drawingContext.DrawLine(_invalidPen,
            new Point(-w / 2, h / 2), new Point(w / 2, -h / 2));
    }

    private static T FreezeIfPossible<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
