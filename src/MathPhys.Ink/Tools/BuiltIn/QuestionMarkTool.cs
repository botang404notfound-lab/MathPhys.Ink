using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using MathPhys.Ink.Gfx;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Tools.BuiltIn;

/// <summary>题号标记工具的 Id（命令总线与悬浮球引用它）。</summary>
public static class QuestionMarkToolIds
{
    public const string Id = "qmark";
}

/// <summary>
/// 题号标记（M12）：在题目旁点一下落一个编号角标；<b>点已有的角标则在三种讲评状态间轮换</b>。
/// </summary>
/// <remarks>
/// <para>
/// 刻意<b>不实现 <see cref="IGfxTool"/></b>：创建型工具落一个对象就会被宿主自动切回"选择"，
/// 而标注题号的真实动线是"连续标十几题" —— 每标一题被弹回选择工具，节奏就断了。
/// 保持普通指针工具，老师标完自己切走（或点其他按钮）。
/// </para>
/// <para>
/// 题号自动编：新角标的编号 = 现有最大数字题号 + 1（<see cref="QuestionMarkIndex.NextLabel"/>），
/// 标错可选中删除，编号不会重复。
/// </para>
/// </remarks>
public sealed class QuestionMarkTool : ITool
{
    private IToolContext? _context;
    private string? _hitMarkId;

    public string Id => QuestionMarkToolIds.Id;
    public string DisplayName => "题号标记";
    public string ToolTip
        => "题号标记：在题目旁点一下落一个编号角标；再点角标轮换状态（未讲 → 错题 → 已讲评）。"
           + "配合「上一题 / 下一题」按钮按题号跳转讲评。";

    public Key? Shortcut => null;
    public bool UsesInkLayer => false;
    public bool NeedsPointer => true;
    public ToolInputKind InputKind => ToolInputKind.None;
    public ToolInkMode InkMode => ToolInkMode.None;
    public Cursor? Cursor => Cursors.None;

    public void Activate(IToolContext context)
    {
        _context = context;
        _hitMarkId = null;
    }

    public void Deactivate()
    {
        _context = null;
        _hitMarkId = null;
    }

    public void OnPointer(ToolPointer pointer)
    {
        var context = _context;
        if (context is null) return;

        switch (pointer.Phase)
        {
            case ToolPointerPhase.Down:
                _hitMarkId = FindMarkAt(context, pointer.World);
                break;

            case ToolPointerPhase.Up:
                if (_hitMarkId is not null) CycleState(context, _hitMarkId);
                else CreateMark(context, pointer.World);
                _hitMarkId = null;
                break;
        }
    }

    /// <summary>按下点是否落在一个已有角标上（包围盒膨胀一点当容差）。</summary>
    private static string? FindMarkAt(IToolContext context, Point world)
    {
        var gfx = context.Gfx;
        if (gfx is null) return null;

        foreach (var obj in gfx.Objects)
        {
            if (obj is null || !string.Equals(obj.Kind, QuestionMarkIndex.KindName, StringComparison.Ordinal))
            {
                continue;
            }

            var bounds = obj.BoundsWorld;
            bounds.Inflate(4.0, 4.0);
            if (bounds.Contains(world)) return obj.Id;
        }

        return null;
    }

    /// <summary>已有角标：讲评状态 +1 取模轮换（一次点击 = 一个可撤销单元）。</summary>
    private static void CycleState(IToolContext context, string markId)
    {
        var gfx = context.Gfx;
        if (gfx is null) return;

        foreach (var obj in gfx.Objects)
        {
            if (obj is null || !string.Equals(obj.Id, markId, StringComparison.Ordinal)) continue;

            int next = (QuestionMarkRendererStateOf(obj) + 1) % 3;

            gfx.BeginStep("切换题号状态");
            gfx.UpdateNumbers(markId, new Dictionary<string, double>
            {
                [QuestionMarkRendererStateKey] = next,
            });

            context.SetStatus($"题号标记：第 {obj.GetText(QuestionMarkRendererLabelKey, "?")} 题 → "
                              + DescribeState(next));
            return;
        }
    }

    /// <summary>空点：落一个新角标，编号自动 +1。</summary>
    private static void CreateMark(IToolContext context, Point world)
    {
        var gfx = context.Gfx;
        if (gfx is null)
        {
            context.SetStatus("题号标记：当前程序不支持图形对象，标记放不下");
            return;
        }

        string label = QuestionMarkIndexNextLabel(gfx);

        gfx.Add(new GfxDraft
        {
            Kind = "questionMark",
            Center = world,
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,
            Numbers = new Dictionary<string, double> { ["state"] = 0 },
            Texts = new Dictionary<string, string> { ["label"] = label },
        });

        context.SetStatus($"题号标记：第 {label} 题已标注（再点角标可切换讲评状态）");
    }

    // ---- 下面三个小转发是为了让本文件不依赖 Gfx 命名空间（工具只认契约）----

    private static int QuestionMarkRendererStateOf(IGfxObjectRef obj)
    {
        double raw = obj.GetNumber("state", 0.0);
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0;
        return Math.Clamp((int)Math.Round(raw), 0, 2);
    }

    private static string QuestionMarkRendererLabelKey => "label";

    private static string QuestionMarkRendererStateKey => "state";

    private static string QuestionMarkRendererLabelOf(IGfxObjectRef obj) => obj.GetText("label", "?");

    private static string QuestionMarkIndexNextLabel(IGfxObjectHost gfx)
    {
        int max = 0;

        foreach (var obj in gfx.Objects)
        {
            if (obj is null || !string.Equals(obj.Kind, "questionMark", StringComparison.Ordinal)) continue;

            string label = QuestionMarkRendererLabelOf(obj);
            if (int.TryParse(label, out int value)) max = Math.Max(max, value);
        }

        return (max + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string DescribeState(int state) => state switch
    {
        1 => "错题",
        2 => "已讲评",
        _ => "未讲",
    };
}
