using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Ruler;

/// <summary>本插件注册的工具 Id。</summary>
public static class RulerToolIds
{
    /// <summary>吸附版直尺。</summary>
    public const string Ruler = "ruler";

    /// <summary>自由角直尺（不吸附）。</summary>
    public const string FreeRuler = "ruler-free";
}

/// <summary>
/// 直尺：按下定起点 → 拖动出预览（线段 + 两端短横 + 长度角度）→ 抬笔在卷面上留一条直线。
/// </summary>
/// <remarks>
/// 这个类<b>只做三件事</b>：管拖动状态、摆预览图元、抬笔时交一条笔画。
/// 数学全在 <see cref="RulerGeometry"/> 里，落墨全交给 <see cref="IToolContext.CommitStrokes"/> ——
/// 于是撤销、橡皮、<c>.tbink</c> 持久化、随缩放平移不漂移，<b>一行都不用自己写</b>。
/// <para>
/// 吸附与否由构造函数决定，同一个类注册两次就是两个工具（<c>ruler</c> / <c>ruler-free</c>）。
/// 之所以不做"按住某键临时关吸附"：一体机上没有键盘可按（M4.2 结论），
/// 老师需要的是一条随时可切的退路，而不是一个用不上的暗号。
/// </para>
/// </remarks>
public sealed class RulerTool : ITool
{
    /// <summary>预览用的字号（屏幕像素，不随缩放变）。</summary>
    private const double LabelFontSize = 14.0;

    /// <summary>长度文字与线段之间的屏幕间距（像素）。</summary>
    private const double LabelGapPixels = 6.0;

    private readonly bool _snapEnabled;

    private IToolContext? _context;
    private bool _drawing;
    private Point _start;

    private Line? _line;
    private Line? _tickStart;
    private Line? _tickEnd;
    private Border? _label;
    private TextBlock? _labelText;

    /// <param name="snapEnabled">true = 吸附到 15° 倍数；false = 自由角。</param>
    public RulerTool(bool snapEnabled) => _snapEnabled = snapEnabled;

    public string Id => _snapEnabled ? RulerToolIds.Ruler : RulerToolIds.FreeRuler;

    public string DisplayName => _snapEnabled ? "直尺" : "直尺·自由角";

    public string ToolTip => _snapEnabled
        ? "直尺：按下定起点、拖动、抬笔留一条直线。角度吸附到 15° 的倍数（差 5° 以内才吸）。快捷键 4"
        : "直尺·自由角：同上，但角度不吸附 —— 要连任意两点、画任意方向的辅助线时用它。快捷键 5";

    public Key? Shortcut => _snapEnabled ? Key.D4 : Key.D5;

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

    /// <remarks>
    /// 拖动中途被切走也会走到这里。<b>内部状态必须在这里复位</b> ——
    /// 宿主虽然会清预览层，但"上一次的起点"是工具自己的私有状态，
    /// 不复位就会表现为"切回来一按，线从上次那个地方开始画"。
    /// </remarks>
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
                _start = pointer.World;
                _drawing = true;

                // 上一次若因异常路径没清干净，这里再兜一次 —— 预览层不该有历史
                context.ClearPreview();
                BuildPreview(context);
                Refresh(context, _start, snapped: false);
                break;

            case ToolPointerPhase.Move:
                if (!_drawing) return;
                Refresh(context,
                        RulerGeometry.ConstrainEnd(_start, pointer.World, _snapEnabled, out var snapped),
                        snapped);
                break;

            case ToolPointerPhase.Up:
                if (!_drawing) return;
                _drawing = false;

                EndDrag(context, pointer.World);
                break;
        }
    }

    // ---------------------------------------------------------------- 抬笔

    private void EndDrag(IToolContext context, Point rawEnd)
    {
        var end = RulerGeometry.ConstrainEnd(_start, rawEnd, _snapEnabled, out var snapped);
        var length = (end - _start).Length;

        context.ClearPreview();
        Reset();

        if (length < RulerGeometry.MinDragWorld)
        {
            // 误触：点一下不该在卷面上留一个点。老师切工具时习惯在卷面上点，
            // 没有这一条，一节讲评下来卷面会被小点铺满，而且擦起来得一个个擦。
            context.SetStatus($"直尺：拖动距离太短（{RulerGeometry.ToCentimeters(length):F2} cm），未落墨");
            return;
        }

        context.CommitStrokes(new StrokeCollection { MakeLine(context, _start, end) });
        context.SetStatus(RulerGeometry.Describe(_start, end, snapped));
    }

    /// <summary>把两点连成一条笔画：颜色与笔宽都取当前调色板。</summary>
    private static Stroke MakeLine(IToolContext context, Point from, Point to)
    {
        var points = new StylusPointCollection
        {
            new StylusPoint(from.X, from.Y),
            new StylusPoint(to.X, to.Y),
        };

        var attributes = new DrawingAttributes
        {
            Color = context.PenColor,
            Width = context.PenWorldWidth,
            Height = context.PenWorldWidth,

            // 两点直线绝不能被拟合成曲线 —— 直尺画出来必须是直的
            FitToCurve = false,
            IgnorePressure = true,
        };

        return new Stroke(points, attributes);
    }

    // ---------------------------------------------------------------- 预览

    /// <summary>建立预览图元。只在按下时做一次，之后每帧只改数值。</summary>
    /// <remarks>
    /// 预览层在 <c>WorldHost</c> 内部 ⇒ 它的本地坐标<b>就是</b>世界坐标，
    /// 所以这里直接写 <see cref="IToolContext.ToWorld"/> 给出的数字，不做任何换算。
    /// <para>
    /// 全部图元都设 <c>IsHitTestVisible=false</c>：预览绝不能自己去抢输入。
    /// 它挂在墨迹层上面，一旦参与命中测试，用户就会"看着笔尖在纸上，写不出字"。
    /// </para>
    /// </remarks>
    private void BuildPreview(IToolContext context)
    {
        var brush = new SolidColorBrush(context.PenColor);
        var thickness = context.PenWorldWidth;

        _line = NewLine(brush, thickness);
        _tickStart = NewLine(brush, thickness);
        _tickEnd = NewLine(brush, thickness);

        _labelText = new TextBlock
        {
            FontSize = LabelFontSize,
            FontFamily = new FontFamily("Microsoft YaHei"),
            FontWeight = FontWeights.SemiBold,
            Foreground = brush,
            IsHitTestVisible = false,
        };

        _label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Child = _labelText,
            IsHitTestVisible = false,
        };

        context.AddPreview(_line);
        context.AddPreview(_tickStart);
        context.AddPreview(_tickEnd);
        context.AddPreview(_label);
    }

    private static Line NewLine(Brush brush, double thickness) => new()
    {
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false,
    };

    /// <summary>按已经约束好的终点刷新预览：线段、两端短横、长度角度文字。</summary>
    /// <param name="context">宿主能力。</param>
    /// <param name="end">终点（<b>已经过吸附</b>，这里不再做任何几何运算）。</param>
    /// <param name="snapped">本次是否发生了吸附（只影响文字里那个「已吸附」提示）。</param>
    private void Refresh(IToolContext context, Point end, bool snapped)
    {
        if (_line is null || _tickStart is null || _tickEnd is null || _label is null || _labelText is null) return;

        _line.X1 = _start.X;
        _line.Y1 = _start.Y;
        _line.X2 = end.X;
        _line.Y2 = end.Y;

        var axis = RulerGeometry.AxisDegrees(_start, end);

        // 两端短横与尺身同角度，于是它们永远"横"在尺子两头
        var tickA = RulerGeometry.TickAt(_start, axis, RulerGeometry.TickLengthWorld);
        _tickStart.X1 = tickA.From.X;
        _tickStart.Y1 = tickA.From.Y;
        _tickStart.X2 = tickA.To.X;
        _tickStart.Y2 = tickA.To.Y;

        var tickB = RulerGeometry.TickAt(end, axis, RulerGeometry.TickLengthWorld);
        _tickEnd.X1 = tickB.From.X;
        _tickEnd.Y1 = tickB.From.Y;
        _tickEnd.X2 = tickB.To.X;
        _tickEnd.Y2 = tickB.To.Y;

        _labelText.Text = RulerGeometry.Describe(_start, end, snapped);
        PlaceLabel(context, new Point((_start.X + end.X) / 2.0, (_start.Y + end.Y) / 2.0));
    }

    /// <summary>
    /// 把长度文字摆到线段中点上方，并让它<b>在屏幕上始终是 14 px</b>。
    /// </summary>
    /// <remarks>
    /// 预览层是世界坐标层，直接放文字会跟着缩放一起变大变小 ——
    /// 缩到 50% 时长度数字就糊成一团看不见了。给容器挂
    /// <c>LayoutTransform = ScaleTransform(1/Scale)</c> 就抵消掉了缩放：
    /// 位置仍然贴世界坐标走，字号固定。
    /// </remarks>
    private void PlaceLabel(IToolContext context, Point midpointWorld)
    {
        var label = _label;
        if (label is null) return;

        var scale = context.Scale;
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;

        label.LayoutTransform = new ScaleTransform(1.0 / scale, 1.0 / scale);

        // 先量一次才知道它多大（反缩放的尺寸已包含在 DesiredSize 里）
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = label.DesiredSize;

        var gapWorld = LabelGapPixels / scale;

        Canvas.SetLeft(label, midpointWorld.X - size.Width / 2.0);
        Canvas.SetTop(label, midpointWorld.Y - gapWorld - size.Height);
    }

    // ---------------------------------------------------------------- 复位

    private void Reset()
    {
        _drawing = false;
        _line = null;
        _tickStart = null;
        _tickEnd = null;
        _label = null;
        _labelText = null;
    }
}
