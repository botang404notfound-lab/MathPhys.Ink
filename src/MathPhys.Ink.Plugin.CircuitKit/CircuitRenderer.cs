using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CircuitKit;

/// <summary>
/// 电路元件的画法 —— 插件对"图形库"这一层的贡献。
/// </summary>
/// <remarks>
/// <para>
/// 元件几何按世界点定义（见 <see cref="CircuitSymbols"/>），本地 = 世界（Scale = 1 时），
/// 所以 <see cref="Measure"/> 直接给元件包围盒；唯一要换算的是<b>线宽</b>：
/// 本地几何会被宿主按 Scale 放大，世界线宽换算到本地要除 Scale（箭头插件的老坑）。
/// </para>
/// <para>
/// 导线（<see cref="CircuitSymbols.WireKey"/>）是拖出来的：长度存
/// <c>Numbers[WireHalfLenKey]</c>（半长，世界点）、方向存对象旋转角
/// （<see cref="CircuitTool"/> 里用 atan2 算，y 向下坐标系下正角即屏幕顺时针，与
/// 宿主 <c>Matrix.Rotate</c> 同向，无需换号）。两端画实心接线点。
/// </para>
/// <para>
/// M15 起：元件画<b>接线柱小圆点</b>（<see cref="CircuitSymbols.PinOffsets"/> 声明的引脚位置，
/// 属于元件本体、随缩放 —— 与元件内字母同待遇）；并实现 <see cref="IGfxAttachmentSolver"/>：
/// 导线两端绑定了元件引脚（Texts[wireA/wireB]）时，由宿主在每次变动后解算导线位姿 ——
/// 元件挪走，导线跟着走。
/// </para>
/// </remarks>
public sealed class CircuitRenderer : IGfxObjectRenderer, IGfxAttachmentSolver
{
    /// <summary>对象种类；与 <see cref="GfxDraft.Kind"/> 对应。</summary>
    public const string KindName = "circuitSymbol";

    /// <summary>元件键的参数键（Texts）。</summary>
    public const string SymbolKey = "symbol";

    public string Kind => KindName;

    /// <summary>读元件键。</summary>
    public static string KeyOf(IGfxObjectRef obj) => obj.GetText(SymbolKey, "");

    /// <summary>是不是导线。</summary>
    public static bool IsWire(IGfxObjectRef obj)
        => string.Equals(KeyOf(obj), CircuitSymbols.WireKey, StringComparison.Ordinal);

    /// <summary>读导线半长（非法值兜底成最小可画长度）。</summary>
    public static double WireHalfLenOf(IGfxObjectRef obj)
    {
        double half = obj.GetNumber(CircuitSymbols.WireHalfLenKey, double.NaN);
        if (double.IsNaN(half) || double.IsInfinity(half) || half < CircuitSymbols.WireMinLength / 2)
        {
            half = CircuitSymbols.WireMinLength / 2;
        }

        return half;
    }

    /// <inheritdoc/>
    /// <remarks>纯函数：查静态目录或按导线参数算，不碰视觉树。</remarks>
    public Size Measure(IGfxObjectRef obj)
    {
        if (IsWire(obj))
        {
            double half = WireHalfLenOf(obj);
            // 高度给接线点的直径留余量（选中框不至于把导线夹成一条看不见的缝）
            return new Size(half * 2 + CircuitSymbols.WireDotRadius * 2,
                            CircuitSymbols.WireDotRadius * 4);
        }

        return CircuitSymbols.Find(KeyOf(obj))?.Size ?? new Size(24, 24);
    }

    /// <inheritdoc/>
    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        // 线宽：本地会被 Scale 放大 ⇒ 世界线宽除 Scale（箭头插件的教训）
        double safeScale = obj.Scale > 0 && !double.IsNaN(obj.Scale) && !double.IsInfinity(obj.Scale)
            ? obj.Scale : 1.0;
        double localStroke = (obj.LineWorldWidth > 0 ? obj.LineWorldWidth : 1.5) / safeScale;

        if (IsWire(obj))
        {
            return new CircuitVisual(
                obj.Color, localStroke,
                WireStrokeGeom(WireHalfLenOf(obj)),
                fill: WireDotsGeom(WireHalfLenOf(obj)),
                letter: null);
        }

        var def = CircuitSymbols.Find(KeyOf(obj));
        if (def is null)
        {
            // 未知键（手改存档 / 新版本元件被旧程序打开）：画一个虚线占位框，不炸
            return new CircuitVisual(obj.Color, localStroke, PlaceholderGeom(def is null), null, null,
                                     dashed: true);
        }

        // 接线柱小圆点画在引脚位置：属于元件本体（随缩放，与元件内字母同待遇 ——
        // 缩放两倍的元件接线柱也大一倍，投影讲课时反而更清楚）
        return new CircuitVisual(obj.Color, localStroke, def.StrokeGeom, MergeFillAndPins(def), def.Letter);
    }

    /// <summary>状态栏读数。</summary>
    public string Describe(IGfxObjectRef obj)
    {
        if (IsWire(obj))
        {
            return $"导线，长 {WireHalfLenOf(obj) * 2 / 28.35:F1} cm";
        }

        var def = CircuitSymbols.Find(KeyOf(obj));
        return def is null ? "电路元件（未知种类，占位框）" : $"电路元件：{def.Name}";
    }

    // ---------------------------------------------------------------- 导线几何（按半长参数化）

    /// <summary>导线主线：(-half,0) → (half,0)。</summary>
    private static Geometry WireStrokeGeom(double half)
    {
        string left = (-half).ToString("0.##", CultureInfo.InvariantCulture);
        string right = half.ToString("0.##", CultureInfo.InvariantCulture);
        var geometry = Geometry.Parse($"M {left},0 L {right},0");
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }

    /// <summary>导线两端接线点（实心小圆）。</summary>
    private static Geometry WireDotsGeom(double half)
    {
        var group = new GeometryGroup
        {
            Children =
            {
                new EllipseGeometry(new Point(-half, 0), CircuitSymbols.WireDotRadius, CircuitSymbols.WireDotRadius),
                new EllipseGeometry(new Point(half, 0), CircuitSymbols.WireDotRadius, CircuitSymbols.WireDotRadius),
            },
        };
        if (group.CanFreeze) group.Freeze();
        return group;
    }

    // ---------------------------------------------------------------- 引脚（M15）

    /// <summary>某对象的引脚（本地坐标）：导线按半长动态算，元件查目录，未知给空表。</summary>
    public static Point[] PinsOf(IGfxObjectRef obj)
    {
        if (IsWire(obj)) return CircuitSymbols.WirePins(WireHalfLenOf(obj));

        return CircuitSymbols.Find(KeyOf(obj))?.PinOffsets ?? Array.Empty<Point>();
    }

    /// <summary>对象位姿矩阵（本地 → 世界）。插件自带一份纯数学换算 ——
    /// 契约不给宿主内部类型，而 IGfxObjectRef 的位姿三件套（Center/Rotation/Scale）足够算出来。</summary>
    private static Matrix PoseMatrixOf(IGfxObjectRef obj)
    {
        var matrix = Matrix.Identity;
        matrix.Scale(obj.Scale, obj.Scale);
        matrix.Rotate(obj.RotationDegrees);
        return matrix;
    }

    /// <summary>第 <paramref name="pinIndex"/> 个引脚的世界坐标（序号越界返回 <c>null</c>）。</summary>
    public static Point? PinWorldOf(IGfxObjectRef obj, int pinIndex)
    {
        var pins = PinsOf(obj);
        if (pinIndex < 0 || pinIndex >= pins.Length) return null;

        var local = PoseMatrixOf(obj).Transform(pins[pinIndex]);
        return new Point(obj.Center.X + local.X, obj.Center.Y + local.Y);
    }

    /// <summary>填充几何 + 接线柱小圆点（都没有时返回 <c>null</c>，让 CircuitVisual 跳过填充）。</summary>
    private static Geometry? MergeFillAndPins(CircuitSymbols.Def def)
    {
        var group = new GeometryGroup();
        if (def.FillGeom is not null) group.Children.Add(def.FillGeom);

        foreach (var pin in def.PinOffsets)
        {
            group.Children.Add(new EllipseGeometry(pin, CircuitSymbols.PinDotRadius, CircuitSymbols.PinDotRadius));
        }

        if (group.Children.Count == 0) return null;
        if (group.CanFreeze) group.Freeze();
        return group;
    }

    // ---------------------------------------------------------------- 导线跟随（IGfxAttachmentSolver）

    /// <inheritdoc/>
    /// <remarks>
    /// 只服务导线：两端绑定键（Texts[wireA/wireB]）里记着"{元件Id}:{引脚序号}"。
    /// 绑定端 = 该引脚的世界坐标；自由端 = 导线当前位姿算出的端点（原地不动）。
    /// 两端都没绑（或目标全被删了）返回 <c>false</c> —— 保持用户摆的位姿。
    /// </remarks>
    public bool TrySolve(IGfxObjectRef self, IReadOnlyList<IGfxObjectRef> board, out GfxSolvedPose pose)
    {
        pose = default;
        if (!IsWire(self)) return false;

        string tagA = self.GetText(CircuitSymbols.WireAKey, "");
        string tagB = self.GetText(CircuitSymbols.WireBKey, "");
        if (tagA.Length == 0 && tagB.Length == 0) return false;

        // 自由端用"当前位姿"算：上一轮解算把它放哪，它就留在哪（半自由导线的自由端原地不动）
        double half = WireHalfLenOf(self);
        var matrix = PoseMatrixOf(self);
        Point LocalToWorld(Point local)
        {
            var rotated = matrix.Transform(local);
            return new Point(self.Center.X + rotated.X, self.Center.Y + rotated.Y);
        }

        Point? a = ResolveEndpoint(tagA, board) ?? LocalToWorld(new Point(-half, 0));
        Point? b = ResolveEndpoint(tagB, board) ?? LocalToWorld(new Point(half, 0));

        var pa = a.Value;
        var pb = b.Value;
        double dx = pb.X - pa.X, dy = pb.Y - pa.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);

        // 两个引脚几乎重合（用户把线两头按到了同一个点上）：长度兜底到最小可画值，
        // 方向保持原样 —— 画出来是一小段线，比"零长线消失"好理解
        double newHalf = Math.Max(length / 2, CircuitSymbols.WireMinLength / 2);
        double rotation = length > 1e-9
            ? Math.Atan2(dy, dx) * 180.0 / Math.PI
            : self.RotationDegrees;

        pose = new GfxSolvedPose(
            new Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2),
            rotation,
            new Dictionary<string, double> { [CircuitSymbols.WireHalfLenKey] = newHalf });
        return true;
    }

    /// <summary>把绑定键解成引脚世界坐标；没绑 / 格式不对 / 目标没了都返回 <c>null</c>。</summary>
    private static Point? ResolveEndpoint(string tag, IReadOnlyList<IGfxObjectRef> board)
    {
        if (tag.Length == 0) return null;
        if (!CircuitSymbols.TryParsePinTag(tag, out string objectId, out int pinIndex)) return null;

        foreach (var candidate in board)
        {
            if (!string.Equals(candidate.Id, objectId, StringComparison.Ordinal)) continue;

            return PinWorldOf(candidate, pinIndex);
        }

        return null;   // 绑定的元件被删了：这端退化为自由端
    }

    /// <summary>占位框（未知元件）。</summary>
    private static Geometry PlaceholderGeom(bool _) => Geometry.Parse("M -12,-12 L 12,-12 L 12,12 L -12,12 Z");
}

/// <summary>
/// 元件视觉：描边几何（引线/轮廓）+ 填充几何（极板/三角/接线点）+ 元件内字母。
/// </summary>
/// <remarks>
/// 不设 RenderTransform / Canvas.Left（位姿由宿主施加）；字母用 FormattedText 画在
/// 本地坐标里 —— 它<b>属于元件本体</b>，随对象缩放是正确行为（区别于"屏幕尺寸恒定"的读数）。
/// </remarks>
internal sealed class CircuitVisual : FrameworkElement
{
    private readonly Brush _ink;
    private readonly Pen _strokePen;
    private readonly Geometry _stroke;
    private readonly Geometry? _fill;
    private readonly string? _letter;
    private readonly bool _dashed;

    public CircuitVisual(Color inkColor, double localStroke,
        Geometry stroke, Geometry? fill, string? letter, bool dashed = false)
    {
        _ink = FreezeIfPossible(new SolidColorBrush(inkColor));

        var pen = new Pen(_ink, localStroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (dashed)
        {
            pen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
        }

        _strokePen = FreezeIfPossible(pen);
        _stroke = stroke;
        _fill = fill;
        _letter = letter;
        _dashed = dashed;

        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawGeometry(null, _strokePen, _stroke);
        if (_fill is not null) drawingContext.DrawGeometry(_ink, null, _fill);

        if (!_dashed && _letter is not null)
        {
            // 元件内字母：Arial 16，画在圆心（视觉基线微调 y −6 让字母在圆里居中）
            var text = new FormattedText(
                _letter,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial"),
                16,
                _ink,
                1.0);

            drawingContext.DrawText(text,
                new Point(-text.Width / 2, -text.Height / 2 - 1));
        }
    }

    private static T FreezeIfPossible<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
