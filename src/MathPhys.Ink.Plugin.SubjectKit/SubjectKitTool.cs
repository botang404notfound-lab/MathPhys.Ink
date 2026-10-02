using System;
using System.Windows;
using System.Windows.Input;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.SubjectKit;

/// <summary>
/// 「学科工具」按钮（M23）：打开学科图片资料库（电场线等），点图即落到卷面上。
/// </summary>
/// <remarks>
/// <para>
/// 它是<b>瞬态工具</b>（<see cref="ITransientTool"/>）：按下去是"打开一个选图面板"，
/// 不是"进入某种模式" —— 面板开着的时候老师随时可以拖卷子、写板书。
/// <see cref="Deactivate"/> 必须<b>空实现</b>：宿主在激活瞬间就会回调它
/// （瞬态工具的定义），在这里关窗口的表现就是"面板闪一下立刻消失"（NativeSim 同款教训）。
/// </para>
/// <para>
/// 落图依赖宿主的位图通道（<see cref="IGfxObjectHost.AddImage"/>）：
/// 拿不到时窗口照开、落图给中文原因 —— 少一个便利不是故障，按下去毫无反应才是。
/// </para>
/// </remarks>
public sealed class SubjectKitTool : ITool, ITransientTool
{
    private IToolContext? _context;
    private SubjectPaletteWindow? _window;

    /// <inheritdoc />
    public string Id => SubjectKitToolIds.Id;

    /// <inheritdoc />
    public string DisplayName => "学科工具";

    /// <inheritdoc />
    public string ToolTip
        => "学科工具：图片资料库（本版收录「电场线」六张 —— 正/负点电荷、等量异种/同种、"
           + "匀强电场、静电平衡导体）。点图自动落到卷面中央，可拖动缩放到题目旁边；以后还会加新分类。";

    /// <summary>
    /// 快捷键刻意<b>不设</b>：单字母快捷键已所剩无几，这个入口从瓦片点进就够了。
    /// </summary>
    public Key? Shortcut => null;

    // ---------------------------------------------------------------- 画布行为
    //
    // 本工具不直接接触画布：落图发生在面板的点击回调里。

    /// <inheritdoc />
    public bool UsesInkLayer => false;

    /// <inheritdoc />
    public bool NeedsPointer => false;

    /// <inheritdoc />
    public ToolInputKind InputKind => ToolInputKind.None;

    /// <inheritdoc />
    public ToolInkMode InkMode => ToolInkMode.None;

    /// <inheritdoc />
    public Cursor? Cursor => null;

    /// <inheritdoc />
    public void Activate(IToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        // 已经开着就别再开一个：两个 Topmost 面板叠着，谁在上面说不清
        if (_window is { IsLoaded: true })
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            context.SetStatus("学科工具面板已经开着：点图片落到卷面上，或换左侧分类。");
            return;
        }

        var window = new SubjectPaletteWindow();
        window.ImagePicked += (png, label) => PlaceImage(png, label);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();

        context.SetStatus(context.Gfx is not null
            ? "学科工具：点图片落到卷面中央（可连续放）；左侧可切换分类。"
            : "学科工具：当前程序不支持图形对象，图片落不到卷面上（预览照常可看）。");
    }

    /// <summary>
    /// 被切走 —— <b>必须为空</b>：瞬态工具激活后宿主立刻交还控制权，
    /// <see cref="Deactivate"/> 紧跟在"刚打开面板"之后被调用。
    /// </summary>
    public void Deactivate()
    {
    }

    /// <inheritdoc />
    public void OnPointer(ToolPointer pointer)
    {
        // NeedsPointer=false，宿主不会调用；留着是为了满足接口。
    }

    // ---------------------------------------------------------------- 落图

    /// <summary>把 PNG 交给宿主落成卷面上的位图对象。</summary>
    private void PlaceImage(byte[] png, string label)
    {
        var context = _context;
        if (context is null) return;

        string? objectId = context.Gfx?.AddImage(png, label);

        context.SetStatus(string.IsNullOrEmpty(objectId)
            ? "学科工具：图片没能落到卷面上 —— 先确认已经打开了一份试卷，再点一次。"
            : $"学科工具：{label} 已落到卷面上（{png.Length / 1024} KB），可拖动、缩放到题目旁边。");
    }
}
