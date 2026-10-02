using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.PdfSmokeTest.Plugin;

/// <summary>
/// 验收用插件：注册一个"直尺形态"的工具（需要指针、不落墨层）。
/// </summary>
/// <remarks>
/// 它存在的意义是让加载器有<b>真的 dll</b> 可加载 —— 全部走"反射找插件类 → 实例化 →
/// 注册工具 → 宿主把指针喂进来 → 工具提交笔画"这条真实链路，
/// 而不是在测试里直接 new 一个工具塞进注册表（那样等于什么都没验证）。
/// </remarks>
public sealed class SmokePlugin : IWhiteBoardPlugin
{
    public string Name => "直尺（试验）";

    public void Register(IToolRegistry registry) => registry.Add(new SmokeRulerTool());
}

/// <summary>
/// 验收用插件（故意坏的）：<see cref="Register"/> 一进来就抛异常。
/// </summary>
/// <remarks>
/// <b>它必须与好插件在同一个 dll 里</b>，这样一条断言就能同时证明两件事：
/// <list type="number">
/// <item>插件代码抛异常不会把宿主带崩（<c>LoadAll</c> 照常返回）；</item>
/// <item>同一个 dll 里一个插件类炸了，不影响另一个插件类注册成功。</item>
/// </list>
/// 这两件事都是"教室场景"的硬需求：老师正在讲台上用，插件出错只该少一个功能。
/// </remarks>
public sealed class ExplodingPlugin : IWhiteBoardPlugin
{
    public string Name => "验收用插件（故意抛异常）";

    public void Register(IToolRegistry registry)
        => throw new InvalidOperationException("这是验收用的故意异常：插件注册失败不应影响宿主与其他插件。");
}

/// <summary>
/// 直尺形态的工具（M7.3 真实直尺的最小原型）：按下定起点 → 拖动预览 → 抬笔提交一条直线笔画。
/// </summary>
/// <remarks>
/// 刻意只做"两点直线"：它验证的是<b>契约</b>，不是直尺的功能（吸附、刻度留给 M7.3）。
/// 两条契约在这里被用到实处：
/// <list type="bullet">
/// <item>预览走 <c>AddPreview</c>（临时图元，抬笔即清、不进撤销栈）；</item>
/// <item>成品走 <c>CommitStrokes</c> ⇒ 自动获得撤销/可擦/可持久化。</item>
/// </list>
/// </remarks>
public sealed class SmokeRulerTool : ITool
{
    private IToolContext? _context;
    private Point _start;
    private bool _drawing;
    private Line? _preview;

    public string Id => "smoke-ruler";

    public string DisplayName => "直尺(试验)";

    public string ToolTip => "试验直尺：按下定起点、拖动、抬笔落一条直线（正式版直尺在 M7.3）";

    public Key? Shortcut => Key.D7;

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
        // 拖动中被切走也要清干净：宿主保证调用 Deactivate，但"清预览"这件事工具自己也得做 ——
        // 否则预览会挂在画布上，而用户已经换了工具，根本不会想到是上个工具留下的。
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

                // 预览图元活在"世界坐标层"，所以这里直接写世界坐标，不需要任何换算
                _preview.X2 = pointer.World.X;
                _preview.Y2 = pointer.World.Y;
                break;

            case ToolPointerPhase.Up:
                if (!_drawing) return;

                _drawing = false;
                _context?.ClearPreview();

                var length = (pointer.World - _start).Length;
                context.SetStatus($"假直尺：长度 {length:F1}（世界单位）");

                context.CommitStrokes(new System.Windows.Ink.StrokeCollection
                {
                    MakeLine(context, _start, pointer.World),
                });
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
            StrokeThickness = context.PenWorldWidth,
            IsHitTestVisible = false,   // 预览绝不能自己去抢输入
        };

        context.AddPreview(_preview);
    }

    /// <summary>把两点连成一条笔画：颜色与笔宽都取当前调色板。</summary>
    private static System.Windows.Ink.Stroke MakeLine(IToolContext context, Point from, Point to)
    {
        var points = new System.Windows.Input.StylusPointCollection
        {
            new StylusPoint(from.X, from.Y),
            new StylusPoint(to.X, to.Y),
        };

        var attributes = new System.Windows.Ink.DrawingAttributes
        {
            Color = context.PenColor,
            Width = context.PenWorldWidth,
            Height = context.PenWorldWidth,
            // 两点直线不该被拟合成曲线 —— 直尺画出来必须是直的
            FitToCurve = false,
            IgnorePressure = true,
        };

        return new System.Windows.Ink.Stroke(points, attributes);
    }

    private void Reset()
    {
        _drawing = false;
        _preview = null;
    }
}
