using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CoordSystem;

/// <summary>
/// 空间直角坐标系（xOyZ）的画法 —— 静态轴测，一个对象 = 一个视觉元素（M23）。
/// </summary>
/// <remarks>
/// 与 <see cref="CoordSystemRenderer"/> 同一条家规：渲染器不碰
/// <c>RenderTransform</c> / <c>Canvas.Left/Top</c>，本地几何以数学原点为中心；
/// 绘制顺序：负半轴（虚线）→ 刻度 → 正半轴 → 箭头 → 字母。
/// ★ <see cref="ActualWidth"/> 在图形层里恒为 0（docs/06 §31）——
/// 所有尺寸都从参数算，不读视觉自身的尺寸。
/// </remarks>
public sealed class CoordSystem3DRenderer : IGfxObjectRenderer, IGfxParameterProvider, IGfxParameterSink
{
    public const string KindName = "coordsystem3d";

    public string Kind => KindName;

    /// <summary>
    /// 本地包围盒：以数学原点为中心的 2E×2E 正方形（负半轴画足了 extent 才成立，
    /// 见 <see cref="CoordSystem3DGeometry"/> 的包围盒不变量）。
    /// </summary>
    public Size Measure(IGfxObjectRef obj)
    {
        double unit = UnitOf(obj);
        double extent = ExtentOf(obj);
        double side = 2.0 * extent * unit;
        return new Size(side, side);
    }

    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        return new CoordSystem3DVisual(
            obj.Color,
            obj.LineWorldWidth,
            UnitOf(obj),
            ExtentOf(obj),
            StepOf(obj));
    }

    // ---------------------------------------------------------------- 取参

    public static double UnitOf(IGfxObjectRef obj)
        => CoordSystemGeometry.ClampUnit(obj.GetNumber(CoordSystem3DGeometry.UnitWorldKey,
            CoordSystem3DGeometry.DefaultUnitWorld));

    public static double ExtentOf(IGfxObjectRef obj)
        => CoordSystem3DGeometry.ClampExtent(obj.GetNumber(CoordSystem3DGeometry.ExtentKey,
            CoordSystem3DGeometry.DefaultExtent));

    public static double StepOf(IGfxObjectRef obj)
        => obj.GetNumber(CoordSystem3DGeometry.StepKey, 1.0);

    // ---------------------------------------------------------------- 参数面板

    /// <summary>空间坐标系的参数面板：范围 / 步长 / 单位长度（比平面版少两项：没有四个端点可调）。</summary>
    public GfxParameterPanel? BuildPanel(IGfxObjectRef obj)
    {
        if (obj is null || !string.Equals(obj.Kind, KindName, StringComparison.Ordinal)) return null;

        double unit = UnitOf(obj);
        double extent = ExtentOf(obj);
        double step = StepOf(obj);

        var fields = new List<GfxParameterField>
        {
            new()
            {
                Key = CoordSystem3DGeometry.ExtentKey, Label = "三轴范围 ±", Value = extent,
                Min = 1, Max = CoordSystem3DGeometry.MaxExtent, Step = 1,
            },
            new()
            {
                Key = CoordSystem3DGeometry.StepKey, Label = "刻度步长", Value = step,
                Min = 0.1, Max = 10, Step = 0.5,
            },
            new()
            {
                Key = CoordSystem3DGeometry.UnitWorldKey, Label = "单位长度", Value = unit,
                Min = CoordSystemGeometry.MinUnitWorld, Max = CoordSystemGeometry.MaxUnitWorld,
                Step = 1, Unit = "pt",
            },
        };

        return new GfxParameterPanel
        {
            Title = "空间坐标系",
            Subtitle = $"三轴 ±{DescribeCompact(extent)}，步长 {DescribeCompact(step)}，"
                       + $"单位 {DescribeCompact(unit)} pt",
            Fields = fields,
            Note = "静态轴测画法（x 轴左下 45°、y 轴向右、z 轴竖直向上）；改参数后三轴与刻度跟着重画。",
        };
    }

    /// <summary>应用一项参数修改。</summary>
    public IReadOnlyDictionary<string, double>? Apply(IGfxObjectRef obj, string key, double value)
    {
        if (obj is null) return null;
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;

        var result = new Dictionary<string, double>(StringComparer.Ordinal);

        switch (key)
        {
            case CoordSystem3DGeometry.ExtentKey:
                result[CoordSystem3DGeometry.ExtentKey] = CoordSystem3DGeometry.ClampExtent(value);
                // 范围至少要容得下两格，否则刻度密成一团
                if (result[CoordSystem3DGeometry.ExtentKey] <= StepOf(obj))
                {
                    result[CoordSystem3DGeometry.StepKey] =
                        Math.Max(0.1, result[CoordSystem3DGeometry.ExtentKey] / 4.0);
                }
                break;

            case CoordSystem3DGeometry.StepKey:
                if (!(value > 0)) return null;
                result[CoordSystem3DGeometry.StepKey] = Math.Min(value, ExtentOf(obj));
                break;

            case CoordSystem3DGeometry.UnitWorldKey:
                if (!(value > 0)) return null;
                result[CoordSystem3DGeometry.UnitWorldKey] =
                    Math.Clamp(value, CoordSystemGeometry.MinUnitWorld, CoordSystemGeometry.MaxUnitWorld);
                break;

            default:
                return null;
        }

        return result;
    }

    private static string DescribeCompact(double v)
        => Math.Abs(v - Math.Round(v)) < 1e-9
            ? Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 空间坐标系的矢量视觉：负半轴（虚线）→ 刻度 → 正半轴 → 箭头 → 字母。
/// </summary>
internal sealed class CoordSystem3DVisual : FrameworkElement
{
    private readonly Brush _ink;
    private readonly Pen _axisPen;
    private readonly Pen _negPen;
    private readonly Pen _tickPen;
    private readonly Geometry _axes;
    private readonly Geometry _negAxes;
    private readonly Geometry _arrows;
    private readonly Geometry _ticks;
    private readonly Geometry _labels;

    public CoordSystem3DVisual(
        Color inkColor, double lineWorldWidth,
        double unitWorld, double extent, double step)
    {
        _ink = Frozen(new SolidColorBrush(inkColor));

        // 负半轴与刻度：墨色的 45%（淡），让正半轴（满墨）在视觉上站前面
        var faint = Frozen(new SolidColorBrush(Color.FromArgb(
            0x73, inkColor.R, inkColor.G, inkColor.B)));

        double axisWidth = lineWorldWidth > 0 ? lineWorldWidth : 1.5;
        _axisPen = Frozen(new Pen(_ink, axisWidth));
        _negPen = Frozen(new Pen(faint, axisWidth * 0.7) { DashStyle = DashStyles.Dash });
        _tickPen = Frozen(new Pen(faint, axisWidth * 0.8));

        double e = extent;
        _axes = CoordSystem3DGeometry.BuildAxes(e, unitWorld).Positive;
        _negAxes = CoordSystem3DGeometry.BuildAxes(e, unitWorld).Negative;
        _arrows = CoordSystem3DGeometry.BuildAxisArrows(e, unitWorld, unitWorld * 0.30);
        _ticks = CoordSystem3DGeometry.BuildTicks(e, step, unitWorld);
        _labels = CoordSystem3DGeometry.BuildLabels(e, unitWorld);

        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    /// <remarks>绘制顺序：负半轴 → 刻度 → 正半轴 → 箭头 → 字母（字母最后，不被穿过）。</remarks>
    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawGeometry(null, _negPen, _negAxes);
        drawingContext.DrawGeometry(null, _tickPen, _ticks);
        drawingContext.DrawGeometry(null, _axisPen, _axes);
        drawingContext.DrawGeometry(_ink, _axisPen, _arrows);
        drawingContext.DrawGeometry(_ink, null, _labels);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
