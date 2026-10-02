using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugin.FunctionPlot.Expr;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.FunctionPlot;

/// <summary>
/// 函数图像渲染器：把函数对象的表达式采样成曲线，并按开关标注零点 / 极值 / 与别的曲线的交点。
/// </summary>
/// <remarks>
/// 几何用<b>数学坐标</b>（1 单位 = 1 世界点），宿主会对函数对象施加
/// <c>Scale = unitWorld</c>，于是曲线自动对齐到绑定的坐标系（坐标系几何也是世界长度）。
/// ★ <b>本地 y = −数学 y</b>（<see cref="CreateVisual"/> 里统一翻转）：屏幕 y 向下，
/// 数学 y 向上 —— 与坐标系网格的 <c>FlipY</c>、特征点标注的 <c>−p.Y</c> 同一条约定。
/// 解析失败时不抛 —— 返回红框占位，避免一个坏表达式拖垮整块白板。
/// <para>
/// ★ <b>为什么实现 <see cref="IGfxBoardAwareRenderer"/></b>：交点是"两条曲线"的事，
/// 而渲染器只拿得到自己这一个对象。宿主会在建视觉前喂一份画布快照（只读引用列表），
/// 于是本渲染器能遍历出"同一张坐标系上还有哪些函数"并两两求交。
/// <b>可选接口、只加不改</b>：老插件不实现它照样加载。
/// </para>
/// <para>
/// ★ 实现 <see cref="IGfxPoseRebaser"/>（M12 S7.2）：拖曲线 = 平移折算进参数
/// （<see cref="FunctionRebase"/>，一次/二次平移可解；其余形式宿主自动回退旧行为）。
/// </para>
/// </remarks>
public sealed class FunctionRenderer : IGfxObjectRenderer, IGfxBoardAwareRenderer, IGfxPoseRebaser,
    IGfxParameterProvider, IGfxParameterSink, IGfxTextParameterSink
{
    /// <summary>对象种类；与 <see cref="GfxDraft.Kind"/> 对应。</summary>
    public const string KindName = "function";

    /// <summary>表达式的存档键（Texts）；与 <see cref="FunctionTool"/> 落成对象时写的键一致。</summary>
    public const string ExprKey = "expr";

    // ---------------------------------------------------------------- 开关键（都存 Numbers，1/0）

    /// <summary>标注零点（1=显示）。</summary>
    public const string ShowZerosKey = "showZeros";

    /// <summary>标注极值（1=显示）。</summary>
    public const string ShowExtremaKey = "showExtrema";

    /// <summary>标注与同坐标系其它曲线的交点（1=显示）。</summary>
    public const string ShowIntersectionsKey = "showIntersections";

    /// <summary>特征点结果上限（防止 sin(1000x) 糊满画面）。</summary>
    public const int MaxFeatures = 48;

    /// <summary>特征点小圆的半径（<b>数学单位</b>，最终世界半径 = 它 × Scale）。</summary>
    public const double MarkerRadiusMath = 0.13;

    /// <summary>特征点读数文字的字号（数学单位；最终世界字号 = 它 × Scale）。</summary>
    public const double MarkerFontMath = 0.34;

    /// <summary>
    /// 画布快照（宿主喂进来的）。
    /// </summary>
    /// <remarks>
    /// ★ <b>必须实例字段，禁止 static 缓存</b>：同进程里宿主一个、harness 一个渲染器实例，
    /// 静态字段会互相踩快照 —— 与合力渲染器踩过的坑完全一样。
    /// </remarks>
    private IReadOnlyList<IGfxObjectRef>? _board;

    /// <inheritdoc/>
    public string Kind => KindName;

    /// <inheritdoc/>
    public void SetBoard(IReadOnlyList<IGfxObjectRef>? objects) => _board = objects;

    /// <inheritdoc/>
    public Size Measure(IGfxObjectRef obj)
    {
        double xMin = obj.GetNumber("xMin", -10);
        double xMax = obj.GetNumber("xMax", 10);
        double yMin = obj.GetNumber("yMin", -10);
        double yMax = obj.GetNumber("yMax", 10);
        double w = Math.Max(xMax - xMin, 1.0);
        double h = Math.Max(yMax - yMin, 1.0);
        return new Size(w, h);
    }

    /// <inheritdoc/>
    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        string expr = obj.GetText("expr", "");
        if (string.IsNullOrWhiteSpace(expr))
            return Placeholder(obj, "未设置表达式");

        ExprNode ast;
        try
        {
            ast = ExpressionParser.Parse(expr);
        }
        catch (ParseException ex)
        {
            return Placeholder(obj, $"表达式错误：{ex.Message}");
        }

        // 参数：重新解析表达式收集参数字母，逐字母取保存的数值（滑块改的也是这些键）
        var parameters = new Dictionary<string, double>();
        foreach (var name in ExpressionEvaluator.CollectParameters(ast))
            parameters[name] = obj.GetNumber("param_" + name, 0.0);

        double xMin = obj.GetNumber("xMin", -10);
        double xMax = obj.GetNumber("xMax", 10);
        double yMin = obj.GetNumber("yMin", -10);
        double yMax = obj.GetNumber("yMax", 10);

        var segments = FunctionSampler.Sample(ast, xMin, xMax, yMin, yMax, parameters);
        var geometry = PlotGeometry.Build(FlipToLocal(segments));

        // ★ 线宽必须除掉 Scale：几何是"数学坐标"（1 单位 = 1 世界点），而宿主会把整个视觉
        //   按 Scale（= 坐标系的 unitWorld，约 28）放大 ⇒ 世界粗细 = 局部线宽 × Scale。
        //   少除这一次，曲线就被画成 28 倍粗的一条带子（S4 现场实测"线太粗"的真凶）。
        //   除掉之后世界粗细 == LineWorldWidth，与坐标轴（Scale=1、几何即世界长度）同粗。
        double scale = obj.Scale > 0 ? obj.Scale : 1.0;
        double localStroke = obj.LineWorldWidth / scale;

        var grid = new Grid();
        grid.Children.Add(new Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(obj.Color),
            StrokeThickness = localStroke,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        });

        // 特征点标注（零点 / 极值 / 交点）—— 只在开关打开时才算，普通绘制零开销
        var markers = BuildMarkers(obj, ast, parameters, xMin, xMax, yMin, yMax, scale);
        if (markers.Count > 0)
        {
            var markerLayer = new Canvas { IsHitTestVisible = false };
            foreach (var (p, label) in markers)
                markerLayer.Children.Add(MakeMarkerVisual(p, label, obj.Color));
            grid.Children.Add(markerLayer);
        }

        return grid;
    }

    /// <summary>
    /// 采样结果（数学坐标）→ 本地几何（<c>y → −y</c>）。
    /// </summary>
    /// <remarks>
    /// ★ 这一次翻转曾长期缺失（曲线按数学 y 直接当本地 y 画）：
    /// 屏幕本地 y 向下，不翻转的曲线相对坐标系网格整条上下镜像
    /// （<c>x^2</c> 开口朝下、<c>sin(x)</c> 先往下走）。
    /// 网格 / 轴标签（<c>FlipY</c>）、特征点标注（<c>−p.Y</c>）、输入窗预览（显式翻 y）
    /// 三处都是"本地 y = −数学 y"，只有曲线这一处漏了 —— 现补齐。
    /// 翻转收在<b>渲染出口</b>而不是采样器：采样器还喂输入窗预览（它自己翻），
    /// 改采样器会双重翻转。
    /// </remarks>
    private static IReadOnlyList<IReadOnlyList<Point>> FlipToLocal(
        IReadOnlyList<IReadOnlyList<Point>> segments)
    {
        var flipped = new List<IReadOnlyList<Point>>(segments.Count);
        foreach (var seg in segments)
        {
            var local = new List<Point>(seg.Count);
            foreach (var p in seg) local.Add(new Point(p.X, -p.Y));
            flipped.Add(local);
        }
        return flipped;
    }

    // ---------------------------------------------------------------- M12 S7.2：拖动折算进参数

    /// <inheritdoc/>
    public bool TryRebase(IGfxObjectRef obj, Vector worldDelta,
                          out IReadOnlyDictionary<string, double> numbers,
                          out IReadOnlyDictionary<string, string> texts)
        => FunctionRebase.TryRebase(obj, worldDelta, out numbers, out texts);

    // ---------------------------------------------------------------- 特征点

    private readonly record struct Marker(FeaturePoint P, string Label);

    private List<Marker> BuildMarkers(
        IGfxObjectRef obj, ExprNode ast, IReadOnlyDictionary<string, double> parameters,
        double xMin, double xMax, double yMin, double yMax, double scale)
    {
        var list = new List<Marker>();

        bool showZeros = obj.GetNumber(ShowZerosKey, 0.0) >= 0.5;
        bool showExtrema = obj.GetNumber(ShowExtremaKey, 0.0) >= 0.5;
        bool showIntersections = obj.GetNumber(ShowIntersectionsKey, 0.0) >= 0.5;

        if (showZeros)
        {
            foreach (var p in FeatureFinder.FindZeros(ast, parameters, xMin, xMax, MaxFeatures))
                if (InView(p, xMin, xMax, yMin, yMax))
                    list.Add(new Marker(p, $"({FeatureFinder.Format(p.X)}, 0)"));
        }

        if (showExtrema)
        {
            foreach (var p in FeatureFinder.FindExtrema(ast, parameters, xMin, xMax, MaxFeatures))
            {
                if (!InView(p, xMin, xMax, yMin, yMax)) continue;
                if (p.Kind == FeatureKind.Inflection) continue;   // 驻点不标注（噪声大，教学价值低）
                list.Add(new Marker(p, $"({FeatureFinder.Format(p.X)}, {FeatureFinder.Format(p.Y)})"));
            }
        }

        if (showIntersections)
        {
            foreach (var other in SiblingCurves(obj))
            {
                var otherAst = ParseCurve(other);
                if (otherAst is null) continue;

                var otherParams = new Dictionary<string, double>();
                foreach (var name in ExpressionEvaluator.CollectParameters(otherAst))
                    otherParams[name] = other.GetNumber("param_" + name, 0.0);

                foreach (var p in FeatureFinder.FindIntersections(
                             ast, otherAst, parameters, xMin, xMax, other.Id, MaxFeatures))
                {
                    // 交点必须两条曲线都有值（否则 {x<0:-x, else:x} 在 0 处的"假交点"会被标出来）
                    double yOther = ExpressionEvaluator.Eval(otherAst, p.X, otherParams);
                    if (!double.IsFinite(yOther)) continue;
                    if (System.Math.Abs(yOther - p.Y) > 1e-6) continue;
                    if (!InView(p, xMin, xMax, yMin, yMax)) continue;

                    list.Add(new Marker(p, $"({FeatureFinder.Format(p.X)}, {FeatureFinder.Format(p.Y)})"));
                }
            }
        }

        return list;
    }

    private static bool InView(FeaturePoint p, double xMin, double xMax, double yMin, double yMax)
        => p.X >= xMin - 1e-9 && p.X <= xMax + 1e-9 && p.Y >= yMin - 1e-9 && p.Y <= yMax + 1e-9;

    /// <summary>
    /// 同一张坐标系上的其它函数曲线。
    /// </summary>
    /// <remarks>
    /// 判定用 <c>bindTo</c> 相等（没有绑定目标就退回用中心点是否重合）——
    /// 与配色数曲线用的是同一套"这张图上有谁"的口径。
    /// </remarks>
    private IEnumerable<IGfxObjectRef> SiblingCurves(IGfxObjectRef self)
    {
        var board = _board;
        if (board is null) yield break;

        string selfTarget = CurveTarget(self);
        foreach (var o in board)
        {
            if (o is null) continue;
            if (string.Equals(o.Id, self.Id, System.StringComparison.Ordinal)) continue;
            if (!string.Equals(o.Kind, KindName, System.StringComparison.Ordinal)) continue;
            if (!string.Equals(CurveTarget(o), selfTarget, System.StringComparison.Ordinal)) continue;
            yield return o;
        }
    }

    private static string CurveTarget(IGfxObjectRef o)
    {
        string id = o.GetText("bindTo", "");
        return string.IsNullOrEmpty(id) ? o.GetText("systemId", "") : id;
    }

    private static ExprNode? ParseCurve(IGfxObjectRef o)
    {
        string expr = o.GetText("expr", "");
        if (string.IsNullOrWhiteSpace(expr)) return null;
        try { return ExpressionParser.Parse(expr); }
        catch (ParseException) { return null; }
    }

    /// <summary>
    /// 一个特征标记：小圆点 + 读数文字。
    /// </summary>
    /// <remarks>
    /// ★ <b>尺寸全部用"数学单位"</b>：标记画在"数学坐标"的 Canvas 里，宿主会把整个视觉
    /// 按 <c>Scale ≈ 40</c> 放大 ⇒ 屏幕上看到的世界尺寸 = 这里的值 × Scale。
    /// 半径 0.13、字号 0.34 在 unit=40 时约等于 5.2pt 半径 / 13.6pt 字 —— 与坐标系刻度字协调。
    /// <para>
    /// ★ <b>圆点必须给 <c>Fill</c> 和足够粗的 <c>Stroke</c></b>：只描边时，描边宽度也是
    /// 数学单位，0.02 在屏幕上不足 1 像素，等于什么都没画（实测只看见字、看不见点）。
    /// </para>
    /// </remarks>
    private static FrameworkElement MakeMarkerVisual(FeaturePoint p, string label, Color color)
    {
        double r = MarkerRadiusMath;                 // 数学单位半径
        double strokeMath = r * 0.35;                // 描边 ≈ 半径的 1/3，屏幕上才看得见
        double fontSizeMath = MarkerFontMath;

        var dot = new Ellipse
        {
            Width = r * 2.0,
            Height = r * 2.0,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = strokeMath,
            Fill = Brushes.White,                    // 白底：压在曲线上也能看清
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, p.X - r);
        Canvas.SetTop(dot, -p.Y - r);                // 本地 y 向下 = 数学 −y

        // 标注统一摆在特征点右上方；零点在 x 轴附近，额外抬高一点避开轴线
        double dx = r * 1.5;
        double dy = p.Kind == FeatureKind.Zero ? -fontSizeMath * 2.2 : -fontSizeMath * 1.3;

        // 文字几何已带"缩放 + 平移"的变换（内部已 Freeze，这里不能再改 Data.Transform）
        var textGeo = MakeTextGeometry(label, fontSizeMath, p.X + dx, -p.Y + dy);
        var textBounds = textGeo.Bounds;             // 变换后的实际包围盒（数学单位）

        // ★ 底色衬板：白色实心矩形垫在读数下面。
        //   曾经用"给文字描白边"（Stroke = Brushes.White）当背景 —— 那是错的：
        //   WPF 的描边是**骑在轮廓线上、内外各一半**，0.18×字号的描边单边就有 0.06 数学单位，
        //   而该字号下数字的笔画整条才 ~0.08 宽 ⇒ 白边从两侧把字芯吃光，
        //   屏幕上只剩"看不见的字"（小圆点还在，读数整片消失的现场就是这个）。
        //   改用实心衬板：文字整体压在白底上，压在曲线上也清晰。
        double pad = fontSizeMath * 0.12;
        var plate = new Rectangle
        {
            Width = textBounds.Width + pad * 2.0,
            Height = textBounds.Height + pad * 2.0,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B)),
            StrokeThickness = fontSizeMath * 0.05,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(plate, textBounds.X - pad);
        Canvas.SetTop(plate, textBounds.Y - pad);

        var textPath = new Path
        {
            Data = textGeo,
            Fill = new SolidColorBrush(color),       // 只填实心，不再描边（见上）
            IsHitTestVisible = false,
        };

        var layer = new Canvas { IsHitTestVisible = false };
        layer.Children.Add(dot);
        layer.Children.Add(plate);
        layer.Children.Add(textPath);
        return layer;
    }

    /// <summary>把一行读数变成可描边的 <see cref="Geometry"/>（字号为数学单位，落在指定本地点）。</summary>
    /// <remarks>
    /// ★ <b>必须"先按大字号建几何、再用 Transform 缩到目标尺寸"</b>：
    /// <see cref="FormattedText"/> 在字号极小（这里要的是 0.3 个数学单位）时会算出
    /// <b>空几何</b> —— 不抛异常、不报错，只是屏幕上什么都没有（本条踩过一次：
    /// 小圆点画出来了，读数文字却整片消失）。先用 <see cref="BuildFontSize"/> 建出
    /// 正常的字形轮廓，再乘一个 <see cref="ScaleTransform"/> 缩到目标字号，
    /// 就能既拿到精细轮廓、又得到想要的"数学单位"尺寸。
    /// 注意 <b>先设 Transform 再 Freeze</b>（Freeze 后写 Transform 会抛异常）。
    /// </remarks>
    private static Geometry MakeTextGeometry(string text, double fontSizeMath, double left, double top)
    {
        var typeface = new Typeface(
            new FontFamily("Microsoft YaHei, Microsoft YaHei UI, Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            BuildFontSize,                             // 大字号建轮廓
            Brushes.Black,
            1.0);                                      // pixelsPerDip = 1：与屏幕 DPI 解耦

        var geo = ft.BuildGeometry(new Point(0, 0));
        double k = fontSizeMath / BuildFontSize;       // 缩到"数学单位"字号

        // 先缩放到目标字号，再平移到落点（顺序不能反：缩放要绕原点做）
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(k, k));
        group.Children.Add(new TranslateTransform(left, top));
        geo.Transform = group;
        geo.Freeze();                                  // 先设 Transform（上一行）再 Freeze
        return geo;
    }

    /// <summary>建字模时用的大字号（只为拿到轮廓，随后会被缩到目标尺寸）。</summary>
    private const double BuildFontSize = 100.0;

    /// <summary>表达式缺失/出错时的红框占位（绝不静默丢弃，也绝不抛异常）。</summary>
    private static FrameworkElement Placeholder(IGfxObjectRef obj, string message)
    {
        double xMin = obj.GetNumber("xMin", -10);
        double xMax = obj.GetNumber("xMax", 10);
        double yMin = obj.GetNumber("yMin", -10);
        double yMax = obj.GetNumber("yMax", 10);
        double w = Math.Max(xMax - xMin, 1.0);
        double h = Math.Max(yMax - yMin, 1.0);

        // 宿主会对视觉施加 Scale = unitWorld，文字需反缩放保持屏幕字号恒定
        double inv = obj.Scale > 0 ? 1.0 / obj.Scale : 1.0;
        var text = new TextBlock
        {
            Text = message,
            Foreground = Brushes.Red,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = new ScaleTransform(inv, inv),
        };
        var rect = new RectangleGeometry(new Rect(-w / 2.0, -h / 2.0, w, h));
        var border = new Path
        {
            Data = rect,
            Stroke = Brushes.Red,
            StrokeThickness = obj.LineWorldWidth * inv,   // 同 CreateVisual：除掉 Scale 才是世界粗细
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
        };
        return new Grid { Children = { border, text } };
    }

    // ---------------------------------------------------------------- 参数面板（文本参数）

    /// <summary>
    /// 构建「选中的函数」的参数面板：表达式文本项 + 每个参数字母一个数值项 + 三个标注开关。
    /// </summary>
    /// <remarks>
    /// ★ 可选接口、只加不改：量角器 / 直尺等旧插件不实现它照常加载，宿主用 is 判断。
    /// <para>
    /// <b>纯函数</b>：只读对象、不改任何东西、不抛异常 —— 坏表达式只会让「参数字母没有可列」
    /// （表达式文本项永远在，它本身就是修表达式的入口），面板不能因为坏表达式而整个不出来。
    /// </para>
    /// </remarks>
    public GfxParameterPanel? BuildPanel(IGfxObjectRef obj)
    {
        if (obj is null || !string.Equals(obj.Kind, KindName, StringComparison.Ordinal)) return null;

        string expr = obj.GetText(ExprKey, "");
        var fields = new List<GfxParameterField>();

        // 参数字母：与 CreateVisual 同一条路径（重新解析表达式收集），逐个字母出数值项。
        // 表达式坏掉就只跳过这一步 —— 表达式文本项在下方，永远给出。
        if (expr.Trim().Length > 0)
        {
            try
            {
                var ast = ExpressionParser.Parse(expr);
                foreach (var name in ExpressionEvaluator.CollectParameters(ast))
                {
                    string key = "param_" + name;
                    fields.Add(new GfxParameterField
                    {
                        Key = key, Label = "参数 " + name,
                        Value = obj.GetNumber(key, 0.0),
                        Min = -100, Max = 100, Step = 0.5,
                    });
                }
            }
            catch
            {
                // 坏表达式：没有参数字母可列。画布上的红框占位与表达式输入框共同引导修复，这里不抛。
            }
        }

        // 标注开关：1/0 数值项（Min=0, Max=1, Step=1，宿主渲染成开关）——
        // 与坐标系「显示网格」同一条纪律，不新造布尔字段类型。
        fields.Add(new GfxParameterField { Key = ShowZerosKey, Label = "标零点", Value = obj.GetNumber(ShowZerosKey, 0), Min = 0, Max = 1, Step = 1 });
        fields.Add(new GfxParameterField { Key = ShowExtremaKey, Label = "标极值", Value = obj.GetNumber(ShowExtremaKey, 0), Min = 0, Max = 1, Step = 1 });
        fields.Add(new GfxParameterField { Key = ShowIntersectionsKey, Label = "标交点", Value = obj.GetNumber(ShowIntersectionsKey, 0), Min = 0, Max = 1, Step = 1 });

        string subtitle = expr.Trim().Length == 0
            ? "未设置表达式"
            : "y = " + (expr.Length > 40 ? expr[..40] + "…" : expr);

        return new GfxParameterPanel
        {
            Title = "函数图像",
            Subtitle = subtitle,
            Fields = fields,
            TextFields = new[] { new GfxTextField { Key = ExprKey, Label = "表达式", Value = expr } },
            Note = "改表达式后曲线立即重建；参数取值立即生效。",
        };
    }

    /// <summary>
    /// 应用一次数值参数修改（参数字母 / 标注开关）。
    /// </summary>
    /// <remarks>与 <c>CoordSystemRenderer.Apply</c> 同一条纪律：返回「最终要落盘的键值对」，
    /// 宿主原样合并；非法值返回 null，不抛异常。</remarks>
    public IReadOnlyDictionary<string, double>? Apply(IGfxObjectRef obj, string key, double value)
    {
        if (obj is null) return null;
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        switch (key)
        {
            case ShowZerosKey:
            case ShowExtremaKey:
            case ShowIntersectionsKey:
                // 开关：宿主已把值规整到 0/1 档位，这里再兜一层
                result[key] = value >= 0.5 ? 1.0 : 0.0;
                return result;

            default:
                if (key.StartsWith("param_", StringComparison.Ordinal))
                {
                    result[key] = value;
                    return result;
                }
                return null;
        }
    }

    /// <summary>
    /// 应用一次表达式修改。空文本 = 「这次不改」；坏表达式放行（渲染器退化成红框占位，
    /// 老师改到合法为止 —— 把坏表达式直接拒掉，输入框里反而没法看到自己打错了什么）。
    /// </summary>
    public IReadOnlyDictionary<string, string>? ApplyText(IGfxObjectRef obj, string key, string text)
    {
        if (obj is null) return null;
        if (!string.Equals(key, ExprKey, StringComparison.Ordinal)) return null;

        string trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return null;

        return new Dictionary<string, string>(StringComparer.Ordinal) { [ExprKey] = trimmed };
    }
}
