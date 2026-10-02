using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CoordSystem;

/// <summary>
/// 坐标系的画法 —— 插件对"图形库"这一层的贡献。
/// </summary>
/// <remarks>
/// 一个对象 = <b>一个</b>视觉元素（<see cref="CoordSystemVisual"/>），网格 / 轴 / 箭头 /
/// 标签都在它自己的 <c>OnRender</c> 里画。不做"一根线一个 Path"，一体机拖动直接卡。
/// </remarks>
public sealed class CoordSystemRenderer : IGfxObjectRenderer, IGfxParameterProvider, IGfxParameterSink, ISnapTargetProvider
{
    public const string KindName = "coordsystem";

    /// <summary>网格交点收集上限（超出则只提供轴线，不提供格点 —— 防 x±1000 的巨图拖垮每帧查询）。</summary>
    public const int MaxSnapGridPoints = 4096;

    public string Kind => KindName;

    /// <summary>
    /// 本地包围盒：以数学原点为中心、绕原点对称的<b>正方形</b>。
    /// </summary>
    /// <remarks>
    /// xExtent / yExtent = max(|min|,|max|) × unitWorld。宿主绕原点旋转 =
    /// 绕数学原点旋转（教学坐标系几乎都这么用）。正方形意味着选中框在纵横比
    /// 不锁定时也会偏大（用正方形以外的形状意味着引入"非中心包围盒"，留到日后再说）。
    /// </remarks>
    public Size Measure(IGfxObjectRef obj)
    {
        double unit = UnitOf(obj);
        double xExtent = Math.Max(Math.Abs(XMinOf(obj)), Math.Abs(XMaxOf(obj))) * unit;
        double yExtent = Math.Max(Math.Abs(YMinOf(obj)), Math.Abs(YMaxOf(obj))) * unit;
        double side = 2.0 * Math.Max(xExtent, yExtent);
        return new Size(side, side);
    }

    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        var ink = obj.Color;
        return new CoordSystemVisual(
            ink,
            obj.LineWorldWidth,
            UnitOf(obj),
            XMinOf(obj), XMaxOf(obj),
            YMinOf(obj), YMaxOf(obj),
            StepOf(obj),
            ShowGridOf(obj),
            ShowLabelsOf(obj));
    }

    // ---------------------------------------------------------------- 取参（与三角形/量角器同款：判空 + 走默认值）

    public static double UnitOf(IGfxObjectRef obj)
        => CoordSystemGeometry.ClampUnit(obj.GetNumber(CoordSystemGeometry.UnitWorldKey, CoordSystemGeometry.DefaultUnitWorld));

    public static double XMinOf(IGfxObjectRef obj)
        => CoordSystemGeometry.ClampRangeBound(obj.GetNumber(CoordSystemGeometry.XMinKey, CoordSystemGeometry.DefaultRange.Min));

    public static double XMaxOf(IGfxObjectRef obj)
        => CoordSystemGeometry.ClampRangeBound(obj.GetNumber(CoordSystemGeometry.XMaxKey, CoordSystemGeometry.DefaultRange.Max));

    public static double YMinOf(IGfxObjectRef obj)
        => CoordSystemGeometry.ClampRangeBound(obj.GetNumber(CoordSystemGeometry.YMinKey, CoordSystemGeometry.DefaultRange.Min));

    public static double YMaxOf(IGfxObjectRef obj)
        => CoordSystemGeometry.ClampRangeBound(obj.GetNumber(CoordSystemGeometry.YMaxKey, CoordSystemGeometry.DefaultRange.Max));

    public static double StepOf(IGfxObjectRef obj)
        => obj.GetNumber(CoordSystemGeometry.StepKey, CoordSystemGeometry.DefaultRange.Step);

    public static bool ShowGridOf(IGfxObjectRef obj)
        => obj.GetNumber(CoordSystemGeometry.ShowGridKey, 1.0) >= 0.5;

    public static bool ShowLabelsOf(IGfxObjectRef obj)
        => obj.GetNumber(CoordSystemGeometry.ShowLabelsKey, 1.0) >= 0.5;

    // ---------------------------------------------------------------- 参数面板（M7.4 批次 3）

    /// <summary>
    /// 坐标系的参数面板：范围四处 + 单位长度 + 网格 / 读数两个开关。
    /// </summary>
    /// <remarks>
    /// ★ <b>可见性开关也用 1/0 的数值项表达</b>，不新造"布尔字段"类型：
    /// 对象的存档只有"数值参数 + 文本参数"两种，多一种字段类型就要多一套持久化、
    /// 多一套撤销、多一套校验。而面板上把 <c>Min=0, Max=1, Step=1</c> 的数值项
    /// 渲染成一个复选框，是宿主那一侧一行的判断 —— 代价全在不需要改契约的地方。
    /// </remarks>
    public GfxParameterPanel? BuildPanel(IGfxObjectRef obj)
    {
        if (obj is null || !string.Equals(obj.Kind, KindName, StringComparison.Ordinal)) return null;

        double unit = UnitOf(obj);
        double xMin = XMinOf(obj), xMax = XMaxOf(obj);
        double yMin = YMinOf(obj), yMax = YMaxOf(obj);

        var fields = new List<GfxParameterField>
        {
            new()
            {
                Key = CoordSystemGeometry.XMinKey, Label = "x 最小", Value = xMin,
                Min = -1000, Max = 1000, Step = 1,
            },
            new()
            {
                Key = CoordSystemGeometry.XMaxKey, Label = "x 最大", Value = xMax,
                Min = -1000, Max = 1000, Step = 1,
            },
            new()
            {
                Key = CoordSystemGeometry.YMinKey, Label = "y 最小", Value = yMin,
                Min = -1000, Max = 1000, Step = 1,
            },
            new()
            {
                Key = CoordSystemGeometry.YMaxKey, Label = "y 最大", Value = yMax,
                Min = -1000, Max = 1000, Step = 1,
            },
            new()
            {
                Key = CoordSystemGeometry.UnitWorldKey, Label = "单位长度", Value = unit,
                Min = CoordSystemGeometry.MinUnitWorld, Max = CoordSystemGeometry.MaxUnitWorld,
                Step = 1, Unit = "pt",
            },
            new()
            {
                Key = CoordSystemGeometry.ShowGridKey, Label = "显示网格",
                Value = ShowGridOf(obj) ? 1 : 0, Min = 0, Max = 1, Step = 1,
            },
            new()
            {
                Key = CoordSystemGeometry.ShowLabelsKey, Label = "显示刻度",
                Value = ShowLabelsOf(obj) ? 1 : 0, Min = 0, Max = 1, Step = 1,
            },
        };

        // 副标题给"当前一格代表多少"—— 这是老师改参数时最想知道的一件事
        double step = StepOf(obj);
        string subtitle = $"一格 {DescribeCompact(step)} × 一格 {DescribeCompact(step)}"
                          + $"，单位 {DescribeCompact(unit)} pt";

        return new GfxParameterPanel
        {
            Title = "坐标系",
            Subtitle = subtitle,
            Fields = fields,
            Note = "改范围后曲线会跟着重画；单位长度只改画法，不改变数学坐标。",
        };
    }

    /// <summary>
    /// 应用一项参数修改。
    /// </summary>
    /// <remarks>
    /// ★ <b>这里有一处必须连带改的键，正是不该让宿主猜的理由</b>：
    /// 改 <c>xMin</c> 时必须同时保证 <c>xMin &lt; xMax</c>。若只写回被改的那一个键，
    /// 用户把 x 最小拖到比最大还大时，坐标系会翻面（轴反向、刻度全乱），
    /// 而且界面上一眼看不出哪里错了 —— 所以这里把"另一个端点"一起交出去。
    /// </remarks>
    public IReadOnlyDictionary<string, double>? Apply(IGfxObjectRef obj, string key, double value)
    {
        if (obj is null) return null;

        if (double.IsNaN(value) || double.IsInfinity(value)) return null;

        var result = new Dictionary<string, double>(StringComparer.Ordinal);

        switch (key)
        {
            case CoordSystemGeometry.XMinKey:
                result[CoordSystemGeometry.XMinKey] = value;
                // 连带保证 xMin < xMax：若越过了，把 xMax 顶到 xMin + 1（而不是默默丢弃这次修改）
                if (value >= XMaxOf(obj)) result[CoordSystemGeometry.XMaxKey] = value + 1.0;
                break;

            case CoordSystemGeometry.XMaxKey:
                result[CoordSystemGeometry.XMaxKey] = value;
                if (value <= XMinOf(obj)) result[CoordSystemGeometry.XMinKey] = value - 1.0;
                break;

            case CoordSystemGeometry.YMinKey:
                result[CoordSystemGeometry.YMinKey] = value;
                if (value >= YMaxOf(obj)) result[CoordSystemGeometry.YMaxKey] = value + 1.0;
                break;

            case CoordSystemGeometry.YMaxKey:
                result[CoordSystemGeometry.YMaxKey] = value;
                if (value <= YMinOf(obj)) result[CoordSystemGeometry.YMinKey] = value - 1.0;
                break;

            case CoordSystemGeometry.UnitWorldKey:
                if (!(value > 0)) return null;
                result[CoordSystemGeometry.UnitWorldKey] =
                    Math.Clamp(value, CoordSystemGeometry.MinUnitWorld, CoordSystemGeometry.MaxUnitWorld);
                break;

            case CoordSystemGeometry.ShowGridKey:
            case CoordSystemGeometry.ShowLabelsKey:
                result[key] = value >= 0.5 ? 1.0 : 0.0;
                break;

            default:
                return null;
        }

        return result;
    }

    // ---------------------------------------------------------------- M12 S7.1：吸附目标（网格交点 + 坐标轴）

    /// <inheritdoc/>
    /// <remarks>
    /// ★ 世界换算<b>必须走位姿矩阵</b>（缩放→旋转→平移，与宿主 <c>GfxTransform.PoseMatrix</c> 同式），
    /// 不能只用 <c>MathToWorld(Center, unit, …)</c> —— 那会丢掉旋转与坐标系自身的缩放，
    /// 旋转过的坐标系吸出来的点全是错位的。
    /// 本地几何约定与 <see cref="CoordSystemGeometry.BuildGrid"/> 一致：
    /// 数学 (mx,my) → 本地 (mx·u, −my·u)。
    /// </remarks>
    public bool TryCollectSnapTargets(IGfxObjectRef obj, ISnapTargetCollector collector)
    {
        if (obj is null || collector is null) return false;

        double unit = UnitOf(obj);
        double xMin = XMinOf(obj), xMax = XMaxOf(obj);
        double yMin = YMinOf(obj), yMax = YMaxOf(obj);
        double step = StepOf(obj);
        if (!(step > 0)) return false;

        // 网格交点（只在网格开着且总量有界时提供）
        if (ShowGridOf(obj))
        {
            int nx = CoordSystemGeometry.GridLineCount(xMin, xMax, step);
            int ny = CoordSystemGeometry.GridLineCount(yMin, yMax, step);
            if (nx > 0 && ny > 0 && (long)nx * ny <= MaxSnapGridPoints)
            {
                for (double mx = xMin; mx <= xMax + 1e-9; mx += step)
                {
                    for (double my = yMin; my <= yMax + 1e-9; my += step)
                    {
                        collector.AddPoint(ToWorld(obj, unit, mx, my));
                    }
                }
            }
        }

        // 两条轴（吸到线上最近点 —— 指向"贴着轴"的箭头用）
        collector.AddLine(ToWorld(obj, unit, xMin, 0), ToWorld(obj, unit, xMax, 0));
        collector.AddLine(ToWorld(obj, unit, 0, yMin), ToWorld(obj, unit, 0, yMax));

        return true;
    }

    /// <summary>数学坐标 → 世界坐标（位姿矩阵：缩放 → 旋转 → 平移）。</summary>
    private static Point ToWorld(IGfxObjectRef obj, double unitWorld, double mathX, double mathY)
    {
        var local = new Point(mathX * unitWorld, -mathY * unitWorld);

        var matrix = new Matrix();
        matrix.Scale(obj.Scale, obj.Scale);
        matrix.Rotate(obj.RotationDegrees);
        matrix.Translate(obj.Center.X, obj.Center.Y);
        return matrix.Transform(local);
    }

    /// <summary>紧凑数值（面板副标题用）；整数不显示小数点。</summary>
    private static string DescribeCompact(double v)
        => Math.Abs(v - Math.Round(v)) < 1e-9
            ? Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 坐标系的矢量视觉：网格 + 轴 + 箭头 + 刻度标签，全部在本地坐标画（原点 = 数学原点）。
/// </summary>
/// <remarks>
/// 不设 <c>RenderTransform</c>、<c>Canvas.Left/Top</c>：位姿由宿主统一施加，
/// 这里只管"以数学原点为中心、长什么样"。绘制顺序：网格 → 轴 → 箭头 → 标签，
/// 标签最后画，保证不被网格/轴穿过。
/// </remarks>
internal sealed class CoordSystemVisual : FrameworkElement
{
    private readonly Brush _ink;
    private readonly Brush _gridInk;
    private readonly Pen _gridPen;
    private readonly Pen _axisPen;
    private readonly Pen _arrowPen;

    private readonly Geometry _grid;
    private readonly Geometry _axes;
    private readonly Geometry _arrows;
    private readonly Geometry _labels;

    public CoordSystemVisual(
        Color inkColor, double lineWorldWidth,
        double unitWorld,
        double xMin, double xMax, double yMin, double yMax, double step,
        bool showGrid, bool showLabels)
    {
        _ink = Frozen(new SolidColorBrush(inkColor));

        // 网格色：墨色的 40% 不透明（淡灰），让轴（满墨）能压住它
        _gridInk = Frozen(new SolidColorBrush(Color.FromArgb(0x66, inkColor.R, inkColor.G, inkColor.B)));

        double axisWidth = lineWorldWidth > 0 ? lineWorldWidth : 1.5;
        _axisPen = Frozen(new Pen(_ink, axisWidth));
        _arrowPen = Frozen(new Pen(_ink, axisWidth));
        _gridPen = Frozen(new Pen(_gridInk, axisWidth * 0.6));

        // 几何：单位长度 = unitWorld，所有"本地坐标"先按数学坐标画（y 向上），
        // 再交给宿主按"原点 + unitWorld + FlipY"的位姿矩阵去摆。
        // 网格 / 轴 / 箭头：用 1 = unitWorld 的局部坐标（按参数）
        _grid = showGrid
            ? CoordSystemGeometry.BuildGrid(xMin, xMax, yMin, yMax, step, unitWorld)
            : Geometry.Empty;
        _axes = CoordSystemGeometry.BuildAxes(xMin, xMax, yMin, yMax, unitWorld);
        _arrows = CoordSystemGeometry.BuildAxisArrows(xMax, yMax, unitWorld, unitWorld * 0.35);
        _labels = showLabels
            ? CoordSystemGeometry.BuildLabels(xMin, xMax, yMin, yMax, step, unitWorld, fontRatio: 0.28)
            : Geometry.Empty;

        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    /// <remarks>绘制顺序：网格 → 轴 → 箭头 → 标签。</remarks>
    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawGeometry(null, _gridPen, _grid);
        drawingContext.DrawGeometry(null, _axisPen, _axes);
        drawingContext.DrawGeometry(_ink, _arrowPen, _arrows);
        drawingContext.DrawGeometry(_ink, null, _labels);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}