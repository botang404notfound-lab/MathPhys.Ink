using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CircuitKit;

/// <summary>电路工具的 Id（宿主与测试引用它）。</summary>
public static class CircuitToolIds
{
    public const string Id = "circuit";
}

/// <summary>
/// 电路工具：调色板选元件，点画布落下；<b>导线则按下拖动画线</b>。
/// </summary>
/// <remarks>
/// <para>
/// 刻意<b>不实现 <see cref="IGfxTool"/></b>（与题号标记同款决策）：
/// 搭一个电路要连续放十几个元件，落一个就被弹回选择工具，节奏就断了。
/// 保持普通指针工具，老师放完自己切走（或点其他按钮）。
/// </para>
/// <para>
/// 导线是拖动手势：按下记起点、移动画橡皮筋预览、抬起落线
/// （中心 = 中点、旋转角 = atan2、半长存参数）。短于最小长度的拖动视为手抖，不落。
/// M15 起两端会<b>吸附元件接线柱</b>：吸上的那端把"{元件Id}:{引脚序号}"记进导线的
/// Texts[wireA/wireB]，之后元件挪动 / 旋转 / 缩放，导线由
/// <see cref="CircuitRenderer.TrySolve"/> 跟随 —— 这是"画一幅完整电路图"的关键。
/// </para>
/// <para>
/// 放元件也会做<b>引脚对齐</b>：落点附近已有别的引脚时，让自己的引脚正对上去。
/// </para>
/// </remarks>
public sealed class CircuitTool : ITool
{
    private IToolContext? _context;
    private CircuitPaletteWindow? _palette;

    /// <summary>当前选中的元件键（调色板改它；"wire" = 导线）。</summary>
    private string _symbol = "cell";

    /// <summary>导线拖动状态。</summary>
    private bool _draggingWire;
    private Point _wireStart;

    /// <summary>起点吸中的引脚（<c>null</c> = 自由端）；终点吸中的在抬笔时现算。</summary>
    private PinHit? _wireStartPin;

    /// <summary>橡皮筋预览（每帧重画，带着端点吸附高亮）。</summary>
    private WireRubberBand? _rubberBand;

    public string Id => CircuitToolIds.Id;
    public string DisplayName => "电路";
    public string ToolTip
        => "电路：调色板里选元件，点画布落下（可连续放）；选「导线」后按下拖动画线。"
           + "落下的元件可用选择工具挪动、旋转、缩放。";

    public Key? Shortcut => null;
    public bool UsesInkLayer => false;
    public bool NeedsPointer => true;
    public ToolInputKind InputKind => ToolInputKind.None;
    public ToolInkMode InkMode => ToolInkMode.None;
    public Cursor? Cursor => Cursors.None;

    public void Activate(IToolContext context)
    {
        _context = context;

        if (context.Gfx is null)
        {
            context.SetStatus("电路：当前程序不支持图形对象，元件放不下");
            return;
        }

        context.SetStatus($"电路：已选 {SymbolLabel(_symbol)}，点画布落下；选「导线」则按下拖动");
        ShowPalette();
    }

    public void Deactivate()
    {
        // 预览残留由宿主在切工具时统一清（契约承诺），这里只收面板与状态
        _context = null;
        _draggingWire = false;
        ClosePalette();
    }

    public void OnPointer(ToolPointer pointer)
    {
        var context = _context;
        var gfx = context?.Gfx;
        if (context is null || gfx is null) return;

        switch (pointer.Phase)
        {
            case ToolPointerPhase.Down:
                if (IsWire)
                {
                    _draggingWire = true;
                    _wireStartPin = FindSnap(context, pointer.World, out _wireStart);
                }
                break;

            case ToolPointerPhase.Move:
                if (_draggingWire)
                {
                    var endPin = FindSnap(context, pointer.World, out Point snappedEnd);
                    UpdateWirePreview(context, _wireStart, snappedEnd, endPin is not null);
                }
                break;

            case ToolPointerPhase.Up:
                if (_draggingWire)
                {
                    _draggingWire = false;
                    context.ClearPreview();
                    _rubberBand = null;

                    var endPin = FindSnap(context, pointer.World, out Point snappedEnd);
                    PlaceWire(context, _wireStart, snappedEnd, _wireStartPin, endPin);
                    _wireStartPin = null;
                }
                else
                {
                    PlaceSymbol(context, pointer.World);
                }
                break;
        }
    }

    // ---------------------------------------------------------------- 吸附

    /// <summary>
    /// 在 <paramref name="world"/> 附近找元件引脚；吸上返回命中（并把
    /// <paramref name="snapped"/> 给成引脚坐标），没吸上返回 <c>null</c>（原样落下）。
    /// </summary>
    /// <remarks>
    /// 吸附半径按屏幕尺寸算（12 DIP ÷ 视口缩放）：手指触点的"胖"是屏幕上的事，
    /// 换算成世界单位才不会在缩放后忽大忽小（与手柄命中半径同一套账）。
    /// 无图形通道 / 无查询通道时降级成不吸附 —— 少个便利不等于坏功能。
    /// </remarks>
    private PinHit? FindSnap(IToolContext context, Point world, out Point snapped)
    {
        snapped = world;
        var gfx = context.Gfx;
        if (gfx is null) return null;

        double radius = CircuitSnap.RadiusPixels / Math.Max(context.Scale, 1e-6);

        if (CircuitSnap.TryFindPin(gfx.Objects, world, radius, out var hit))
        {
            snapped = hit.World;
            return hit;
        }

        return null;
    }

    // ---------------------------------------------------------------- 落元件

    /// <summary>当前选的是不是导线。</summary>
    private bool IsWire => string.Equals(_symbol, CircuitSymbols.WireKey, StringComparison.Ordinal);

    /// <summary>状态栏显示用中文名。</summary>
    private string SymbolLabel(string key)
        => key == CircuitSymbols.WireKey ? "导线" : CircuitSymbols.Find(key)?.Name ?? key;

    private void PlaceSymbol(IToolContext context, Point world)
    {
        var gfx = context.Gfx;
        if (gfx is null) return;

        var def = CircuitSymbols.Find(_symbol);
        if (def is null) return;   // 目录里没有的键（不该发生）—— 静默不落比落一个占位框好

        // 引脚对齐：附近已有别的引脚就让新元件的引脚正对上去（落下即"接好"）
        double radius = CircuitSnap.RadiusPixels / Math.Max(context.Scale, 1e-6);
        Point center = CircuitSnap.TryAlignSymbol(def, gfx.Objects, world, radius, out Point aligned)
            ? aligned
            : world;

        gfx.Add(new GfxDraft
        {
            Kind = CircuitRenderer.KindName,
            Center = center,
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,
            Texts = new Dictionary<string, string>
            {
                [CircuitRenderer.SymbolKey] = _symbol,
            },
        });

        // 不 SetStatus 打断节奏：连续放元件时每落一个都报一句反而吵
    }

    private void PlaceWire(IToolContext context, Point start, Point end,
        PinHit? startPin, PinHit? endPin)
    {
        var gfx = context.Gfx;
        if (gfx is null) return;

        double dx = end.X - start.X, dy = end.Y - start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);

        // 手抖级短拖：不落（导线没有一个"点一下就出现"的合理语义）。
        // 两端吸在同一个引脚上时长度必为 0，同样落不下来 —— 这是正确行为：
        // 一小段看不见的线除了让选中变难没有任何用处。
        if (length < CircuitSymbols.WireMinLength)
        {
            context.SetStatus("电路：拖动距离太短，导线没画（按住拖一段再松手）");
            return;
        }

        var texts = new Dictionary<string, string>
        {
            [CircuitRenderer.SymbolKey] = CircuitSymbols.WireKey,
        };
        if (startPin is not null)
        {
            texts[CircuitSymbols.WireAKey] = CircuitSymbols.PinTag(startPin.Value.ObjectId, startPin.Value.PinIndex);
        }
        if (endPin is not null)
        {
            texts[CircuitSymbols.WireBKey] = CircuitSymbols.PinTag(endPin.Value.ObjectId, endPin.Value.PinIndex);
        }

        gfx.Add(new GfxDraft
        {
            Kind = CircuitRenderer.KindName,
            Center = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2),
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,
            // y 向下坐标系：正旋转角 = 屏幕顺时针（宿主 Matrix.Rotate 同向），atan2 直接用
            RotationDegrees = Math.Atan2(dy, dx) * 180.0 / Math.PI,
            Numbers = new Dictionary<string, double>
            {
                [CircuitSymbols.WireHalfLenKey] = length / 2,
            },
            Texts = texts,
        });

        int bound = (startPin is not null ? 1 : 0) + (endPin is not null ? 1 : 0);
        if (bound > 0)
        {
            context.SetStatus(bound == 2
                ? "电路：导线两端已接上接线柱（挪动元件导线会跟着走）"
                : "电路：一端已接上接线柱");
        }
    }

    /// <summary>导线橡皮筋预览（不进墨迹层、不进撤销栈 —— 宿主在抬笔/切工具时清）。</summary>
    private void UpdateWirePreview(IToolContext context, Point start, Point end, bool endSnapped)
    {
        context.ClearPreview();
        _rubberBand = new WireRubberBand(context.PenColor, start, end, endSnapped);
        context.AddPreview(_rubberBand);
    }

    // ---------------------------------------------------------------- 调色板

    private void ShowPalette()
    {
        if (_palette is { IsLoaded: true }) return;

        _palette = new CircuitPaletteWindow(_symbol);
        _palette.SymbolPicked += key =>
        {
            _symbol = key;
            _context?.SetStatus($"电路：已选 {SymbolLabel(key)}，"
                                + (IsWire ? "按住拖动画线" : "点画布落下（可连续放）"));
        };
        _palette.Closed += (_, _) => _palette = null;
        _palette.Show();
    }

    private void ClosePalette()
    {
        var palette = _palette;
        _palette = null;
        palette?.Close();
    }
}

/// <summary>
/// 导线橡皮筋：起点小圆 + 一条线 + 随手移动的终点。
/// </summary>
/// <remarks>
/// 预览视觉加在世界的 GfxLayer 上（坐标即世界坐标），不走对象位姿矩阵 ——
/// 与墨迹橡皮筋同一待遇，宽高给 0 也照常渲染（无裁剪）。
/// <paramref name="endSnapped"/> 为 <c>true</c> 时终点画成实心大圆 ——
/// "吸上了"必须让手看得见，否则老师不知道这一下有没有接上。
/// </remarks>
internal sealed class WireRubberBand : FrameworkElement
{
    private readonly Pen _pen;
    private readonly Brush _ink;
    private readonly Point _start;
    private Point _end;
    private readonly bool _endSnapped;

    public WireRubberBand(Color color, Point start, Point end, bool endSnapped = false)
    {
        _start = start;
        _end = end;
        _endSnapped = endSnapped;
        _ink = new SolidColorBrush(color);
        if (_ink.CanFreeze) _ink.Freeze();
        _pen = new Pen(_ink, 1.5) { DashStyle = new DashStyle(new double[] { 5, 4 }, 0) };
        if (_pen.CanFreeze) _pen.Freeze();

        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawLine(_pen, _start, _end);
        drawingContext.DrawEllipse(_ink, null, _start, 2.5, 2.5);

        // 终点：吸上 = 实心大圆（明确的"接好了"）；没吸 = 与起点同款小圆
        double radius = _endSnapped ? 5.0 : 2.5;
        drawingContext.DrawEllipse(_ink, null, _end, radius, radius);
    }
}
