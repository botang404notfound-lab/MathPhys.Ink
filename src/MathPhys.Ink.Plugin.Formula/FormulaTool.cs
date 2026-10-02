using System.Windows;
using System.Windows.Input;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.Formula;

/// <summary>公式工具的 Id（宿主与测试引用它）。</summary>
public static class FormulaToolIds
{
    public const string Id = "formula";
}

/// <summary>
/// 公式工具：弹出 LaTeX 输入面板，点画布落公式；<b>落成后宿主自动切回选择</b>（IGfxTool 契约）。
/// </summary>
/// <remarks>
/// <para>
/// 动线：点工具栏「公式」→ 面板浮出 → 输入/编辑 LaTeX（实时预览）→ 在卷面上点一下 →
/// 公式落在该点 → 自动切回选择（挪位置 / 缩放）。面板记住上一次的内容，
/// 连续落多个相似公式时改一改再点即可。
/// </para>
/// <para>
/// 面板被老师手动关掉也不影响：再点画布会重新浮出来（不丢内容）。
/// </para>
/// </remarks>
public sealed class FormulaTool : IGfxTool
{
    private readonly FormulaRenderer _renderer = new();
    private FormulaPanelWindow? _panel;

    public string Id => FormulaToolIds.Id;
    public string DisplayName => "公式";
    public string ToolTip
        => "公式：输入 LaTeX（如 \\frac{1}{2}mv^2），点画布落一个排版好的数学公式。"
           + "支持分式、根号、矢量箭头、希腊字母、积分求和；不支持中文与 \\mathbb。";

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
            context.SetStatus("公式：当前程序不支持图形对象，公式放不下");
            return;
        }

        context.SetStatus("公式：在面板里输入 LaTeX，然后点画布落下（面板可随时改，改完再点）");
        ShowPanel();
    }

    public void Deactivate()
    {
        // 切走工具（含"落成后自动切回选择"）就收面板：落一个公式收一次，节奏干净
        _context = null;
        ClosePanel();
    }

    public void OnPointer(ToolPointer pointer)
    {
        if (pointer.Phase != ToolPointerPhase.Up) return;

        var context = _context;
        var gfx = context?.Gfx;
        if (context is null || gfx is null) return;

        // 面板被手动关了：重新浮出来，内容还在
        ShowPanel();

        string latex = _panel?.CurrentLatex?.Trim() ?? string.Empty;
        if (latex.Length == 0)
        {
            context.SetStatus("公式：先在面板里输入 LaTeX，再点画布");
            return;
        }

        // 无效公式不落对象 —— 落一个占位框到卷面上只会吓人
        if (_renderer.GetEntry(latex).LocalGeometry is null)
        {
            context.SetStatus($"公式：LaTeX 解析失败（{Shorten(latex)}），检查写法后再点画布");
            return;
        }

        gfx.Add(new GfxDraft
        {
            Kind = FormulaRenderer.KindName,
            Center = pointer.World,
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,
            Texts = new Dictionary<string, string>
            {
                [FormulaRenderer.LatexKey] = latex,
            },
        });

        // 不在这里 SetStatus：IGfxTool 落成即切回选择，状态栏交给选择工具说
    }

    // ---------------------------------------------------------------- 面板管理

    private IToolContext? _context;

    private void ShowPanel()
    {
        if (_panel is { IsLoaded: true }) return;

        _panel = new FormulaPanelWindow(_renderer, _panel?.CurrentLatex ?? string.Empty);
        _panel.Closed += (_, _) => _panel = null;
        _panel.Show();
    }

    private void ClosePanel()
    {
        var panel = _panel;
        _panel = null;
        panel?.Close();
    }

    private static string Shorten(string s) => s.Length <= 24 ? s : s[..24] + "…";
}
