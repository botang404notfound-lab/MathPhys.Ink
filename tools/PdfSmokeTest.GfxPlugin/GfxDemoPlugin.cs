using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.PdfSmokeTest.GfxPlugin;

/// <summary>
/// 验收用「图形型插件」：注册一个创建型工具（<see cref="IGfxTool"/>）与一个图形渲染器。
/// </summary>
/// <remarks>
/// 它存在的意义是让 M7.4 的新链路有<b>真的 dll</b> 可走，而不是在测试里手工 new：
/// <list type="number">
/// <item>插件加载器从 <c>plugins\</c> 里加载本 dll → 反射找到 <see cref="IWhiteBoardPlugin"/> → 注册工具；</item>
/// <item>同一趟扫描里找到 <see cref="IGfxObjectRenderer"/> 实现 → 自动登记进宿主渲染器注册表；</item>
/// <item>工具在拖动结束时把几何交给 <c>IGfxObjectHost.Add</c> → 宿主负责渲染 / 拖动 / 撤销 / 存档。</item>
/// </list>
/// 第 2 条是最容易静默失败的一环（渲染器没人登记 ⇒ 对象只能画成虚线占位框，
/// 而日志里什么都不像错误），所以必须由真 dll 端到端跑一遍。
/// </remarks>
public sealed class GfxDemoPlugin : IWhiteBoardPlugin
{
    public string Name => "图形（试验）";

    public void Register(IToolRegistry registry) => registry.Add(new GfxDemoTool());
}

/// <summary>
/// 一个最简的图形渲染器：一个方框。
/// </summary>
/// <remarks>
/// 尺寸<b>固定</b>（不吃参数）是刻意的：M7.4 的验收重点是"位姿数学与视觉树是否同源"，
/// 尺寸若是参数相关的，断言里就得再复算一遍渲染器的公式 —— 那等于用同一段代码验证自己。
/// </remarks>
public sealed class GfxDemoRenderer : IGfxObjectRenderer
{
    /// <summary>对象种类名。harness 用它构造同名对象。</summary>
    public const string KindName = "demo-box";

    /// <summary>固定本地尺寸（世界单位）。</summary>
    public static readonly Size BoxSize = new(120, 80);

    public string Kind => KindName;

    public Size Measure(IGfxObjectRef obj) => BoxSize;

    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        var canvas = new Canvas
        {
            Width = BoxSize.Width,
            Height = BoxSize.Height,
            IsHitTestVisible = false,
        };

        var box = new Rectangle
        {
            Width = BoxSize.Width,
            Height = BoxSize.Height,
            Stroke = new SolidColorBrush(obj.Color),
            StrokeThickness = Math.Max(obj.LineWorldWidth, 0.5),
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };

        // ★ 本地原点在几何中心：所以方框从 (-W/2, -H/2) 开始画。
        //   放到左上角会造成"图形看着在这儿、选中框在那儿"，且只在旋转后暴露。
        Canvas.SetLeft(box, -BoxSize.Width / 2.0);
        Canvas.SetTop(box, -BoxSize.Height / 2.0);
        canvas.Children.Add(box);

        return canvas;
    }
}

/// <summary>
/// 创建型工具：按下定起点 → 拖动出一根预览线 → 抬笔在抬起点落成一个图形对象。
/// </summary>
/// <remarks>
/// 它演示了 M7.4 契约的两条用法：
/// <list type="bullet">
/// <item>拖动预览走 <c>AddPreview</c>（不入墨迹层、不入撤销栈、抬笔即清）；</item>
/// <item>成品走 <c>Gfx.Add</c> ⇒ 自动获得渲染 / 选中 / 拖动 / 撤销 / 持久化。</item>
/// </list>
/// </remarks>
public sealed class GfxDemoTool : IGfxTool
{
    private IToolContext? _context;
    private Point _start;
    private bool _drawing;
    private Line? _preview;

    public string Id => "demo-glyph";

    public string DisplayName => "试验图形";

    public string ToolTip => "试验图形：按下拖一下，抬起在落点放一个方框（M7.4 对象层验收用）";

    public Key? Shortcut => Key.D8;

    public bool UsesInkLayer => false;

    public bool NeedsPointer => true;

    public ToolInputKind InputKind => ToolInputKind.None;

    public ToolInkMode InkMode => ToolInkMode.None;

    public Cursor? Cursor => Cursors.Cross;

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
                _start = pointer.World;
                _drawing = true;
                StartPreview(context);
                break;

            case ToolPointerPhase.Move:
                if (!_drawing || _preview is null) return;
                _preview.X2 = pointer.World.X;
                _preview.Y2 = pointer.World.Y;
                break;

            case ToolPointerPhase.Up:
                if (!_drawing) return;

                _drawing = false;
                context.ClearPreview();

                // ★ 唯一需要写的一行"落成"代码：几何交出去，其余全归宿主。
                //   context.Gfx 为 null 表示宿主不支持对象层（老宿主），必须自行判空
                //   而不是直接崩 —— 插件可能被一个还没有图形层的宿主加载。
                if (context.Gfx is { } gfx)
                {
                    gfx.Add(new GfxDraft
                    {
                        Kind = GfxDemoRenderer.KindName,
                        Center = pointer.World,
                        Color = context.PenColor,
                        LineWorldWidth = context.PenWorldWidth,
                        Numbers = new Dictionary<string, double> { ["span"] = (pointer.World - _start).Length },
                        Texts = new Dictionary<string, string> { ["label"] = "demo" },
                    });

                    context.SetStatus($"已放一个试验图形（中心 {pointer.World.X:F0},{pointer.World.Y:F0}）");
                }
                else
                {
                    context.SetStatus("宿主不支持图形对象层");
                }

                break;
        }
    }

    private void StartPreview(IToolContext context)
    {
        _preview = new Line
        {
            X1 = _start.X,
            Y1 = _start.Y,
            X2 = _start.X,
            Y2 = _start.Y,
            Stroke = new SolidColorBrush(context.PenColor),
            StrokeThickness = 1.0,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            IsHitTestVisible = false,
        };

        context.AddPreview(_preview);
    }

    private void Reset()
    {
        _drawing = false;
        _preview = null;
    }
}
