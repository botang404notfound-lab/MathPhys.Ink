using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.VectorArrow;

/// <summary>正交分解工具的 Id。</summary>
public static class ArrowDecomposeToolIds
{
    /// <summary>正交分解（点选型）。快捷键 U（N=箭头、M=合力，取 "decompose" 的发音字母 U）。</summary>
    public const string Id = "vectordecompose";
}

/// <summary>
/// 正交分解（<b>点选型</b>）：点一根已有的矢量箭头，自动生成它的两个分量箭头（沿 x / y）。
/// </summary>
/// <remarks>
/// ★ <b>为什么是"点一根"而不是"拖框"</b>：合力天生是"多根的事"（框选更省事），
/// 而分解天生是"<b>一根</b>的事"（一根斜向力拆成两截）。点一下比拖一个框更省动作，
/// 而且不会误框到旁边的箭头 —— 一次分解多根是没意义的结果。
/// <para>
/// <b>为什么分量是"能拖能删的普通箭头"而不是"钉死在原矢量上的附属物"</b>：
/// 老师的真实用法是"分解完，把水平分量单独拿去讲"，或者"把竖直分量拖开一点免得压住原图"。
/// 落成独立对象就自动获得选中/拖动/删除/撤销/存档，<b>宿主与契约零改动</b>。
/// </para>
/// <para>
/// 分量<b>带符号</b>：斜向左上的力，水平分量朝左（箭头方向就是 −x）。见
/// <see cref="VectorMath.Decompose"/> 的说明 —— 这是本功能最容易被做错的地方。
/// </para>
/// </remarks>
public sealed class ArrowDecomposeTool : ITool, IGfxTool
{
    /// <summary>点选的命中半径（世界单位）—— 与矢量箭头工具的端点吸附半径同一量级。</summary>
    private const double PickRadiusWorld = 24.0;

    private IToolContext? _context;

    public string Id => ArrowDecomposeToolIds.Id;
    public string DisplayName => "正交分解";
    public string ToolTip
        => "正交分解：点一根矢量箭头，自动画出它的<b>水平分量与竖直分量</b>（两根独立箭头，"
        + "可单独拖动/删除；分量为负时箭头朝反方向走）。读数在<b>状态栏</b>。快捷键 U";

    public Key? Shortcut => Key.U;

    public bool UsesInkLayer => false;

    /// <summary>需要指针：点选要拿按下事件。</summary>
    public bool NeedsPointer => true;

    public ToolInputKind InputKind => ToolInputKind.None;
    public ToolInkMode InkMode => ToolInkMode.None;
    public Cursor? Cursor => Cursors.Hand;

    public void Activate(IToolContext context)
    {
        _context = context;
    }

    public void Deactivate()
    {
        _context?.ClearPreview();
        _context = null;
    }

    public void OnPointer(ToolPointer pointer)
    {
        var context = _context;
        if (context is null) return;

        // ★ 按下过就必须响应：点选型工具的"目标"就是按下点本身，没有"拖动阈值"的必要
        //   （阈值只留给"拖出尺寸"型工具，见 docs/06 §8.4）。
        if (pointer.Phase != ToolPointerPhase.Down) return;

        Split(context, pointer.World);
    }

    // ---------------------------------------------------------------- 分解

    private void Split(IToolContext context, Point world)
    {
        var gfx = context.Gfx;
        if (gfx is null)
        {
            context.SetStatus("正交分解：当前程序不支持图形对象，分量放不下");
            return;
        }

        var target = PickArrow(gfx, world);
        if (target is null)
        {
            context.SetStatus("正交分解：这里没有矢量箭头（点箭头身上，或用「矢量箭头」先画一根）");
            return;
        }

        double magnitude = MagnitudeOf(target);
        double angle = ArrowRenderer.AngleOf(target);

        if (magnitude <= 0 || double.IsNaN(magnitude) || double.IsInfinity(magnitude))
        {
            context.SetStatus("正交分解：这根箭头的长度太短，分解不出来（先把它拖长一点）");
            return;
        }

        // ★ 分量是带符号的（见 VectorMath.Decompose）
        var (alongX, alongY) = VectorMath.Decompose(magnitude, angle);

        if (VectorMath.IsZeroSum(Math.Abs(alongX)) && VectorMath.IsZeroSum(Math.Abs(alongY)))
        {
            context.SetStatus("正交分解：两个分量都是零，不落分量（这根箭头本来就是零矢量）");
            return;
        }

        // 分量挂在原箭头所在的坐标系上（有的话）：坐标系一挪，原矢量和它的分量一起动，
        // 不会出现"原箭头跟着坐标系走了、分量留在原地"的错位。
        string bindTo = target.GetText(ArrowTool.BindToKey, "");
        double unitWorld = ArrowRenderer.UnitOf(target);
        double targetScale = target.Scale;
        double rotation = target.RotationDegrees;

        // 一次操作 = 一个撤销单元：在真的开始加对象之前标一次
        gfx.BeginStep("正交分解");

        int placed = 0;
        string? firstId = null;

        // 两个分量各自成一个普通箭头对象：它们的"尾"都放在原箭头的尾部（= 分解的公共起点），
        // 这样卷面上看起来就是"一根斜箭头 + 它伸出来的两条腿"，和课本画法一致。
        Point tail = TailOf(target);

        placed += PlaceComponent(context, gfx, tail, alongX, 0.0, bindTo, unitWorld,
                                 targetScale, rotation, ref firstId);
        placed += PlaceComponent(context, gfx, tail, alongY, 90.0, bindTo, unitWorld,
                                 targetScale, rotation, ref firstId);

        if (placed == 0)
        {
            context.SetStatus("正交分解：这次没落下任何分量（试着把箭头拖长一点再来）");
            return;
        }

        if (firstId is not null) gfx.Select(firstId);

        context.SetStatus("正交分解：" + VectorMath.DecomposeDescribe(alongX, alongY)
                          + $"（{placed} 根分量，可单独拖动或删除）");
    }

    /// <summary>
    /// 落一个分量箭头。返回 1（落了）或 0（该分量为零、不落）。
    /// </summary>
    /// <remarks>
    /// 分量为零 <b>不落一根零长箭头</b>：那会在卷面上留一个"看着是个点、还删不掉（因为看不见）"
    /// 的幽灵。但这不等于"分量不存在"—— 老师从状态栏读数能看到它是 0。
    /// </remarks>
    private static int PlaceComponent(
        IToolContext context, IGfxObjectHost gfx, Point tail,
        double magnitudeN, double directionDegrees,
        string bindTo, double unitWorld, double targetScale, double rotation,
        ref string? firstId)
    {
        if (VectorMath.IsZeroSum(Math.Abs(magnitudeN))) return 0;

        double lengthWorld = magnitudeN * VectorMath.PointsPerInch
                             / VectorMath.CentimetersPerInch
                             / VectorMath.DefaultNewtonsPerCentimeter;

        if (lengthWorld <= 0 || double.IsNaN(lengthWorld) || double.IsInfinity(lengthWorld)) return 0;

        var unit = VectorMath.UnitVector(directionDegrees);
        var head = new Point(tail.X + unit.X * lengthWorld, tail.Y + unit.Y * lengthWorld);
        var center = new Point((tail.X + head.X) / 2.0, (tail.Y + head.Y) / 2.0);

        var numbers = new Dictionary<string, double>
        {
            [ArrowRenderer.LengthWorldKey] = lengthWorld,
            [ArrowRenderer.MagnitudeKey] = Math.Abs(magnitudeN),
            [ArrowRenderer.AngleKey] = directionDegrees >= 0
                ? directionDegrees
                : directionDegrees + 360.0,
            [ArrowTool.UnitWorldKey] = unitWorld,
            [ArrowTool.BindRelXKey] = 0,
            [ArrowTool.BindRelYKey] = 0,
            [ArrowTool.BindRelRotKey] = 0,
        };

        var texts = new Dictionary<string, string>
        {
            [ArrowRenderer.UnitKey] = "N",
            // 记下"我是谁的分量"：便于以后做"删除原矢量则分量一起走"这类联动，
            // 也让人读存档时一眼看出这两根箭头的来历。
            [ArrowRenderer.ParentKey] = "",
        };

        if (!string.IsNullOrEmpty(bindTo))
        {
            texts[ArrowTool.BindToKey] = bindTo;
        }

        var draft = new GfxDraft
        {
            Kind = ArrowRenderer.KindName,
            Center = center,
            RotationDegrees = string.IsNullOrEmpty(bindTo) ? rotation : 0.0,
            // 与 ArrowTool 完全同一套 Scale 约定（见那里的详细说明：本地几何按数学单位画）
            Scale = string.IsNullOrEmpty(bindTo) ? 1.0 : unitWorld * Math.Max(targetScale, 1e-6),
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,
            Numbers = numbers,
            Texts = texts,
        };

        string id = gfx.Add(draft);
        firstId ??= id;
        return 1;
    }

    // ---------------------------------------------------------------- 取参

    /// <summary>把"方向角 + 世界长"还原成箭头尾部（= 中心 − 半长×单位向）。</summary>
    private static Point TailOf(IGfxObjectRef obj)
    {
        double length = ArrowRenderer.LengthWorldOf(obj);
        var unit = VectorMath.UnitVector(ArrowRenderer.AngleOf(obj));
        return new Point(obj.Center.X - unit.X * length / 2.0,
                         obj.Center.Y - unit.Y * length / 2.0);
    }

    /// <summary>取矢量大小（N）：优先用存档里的冗余值，其次按长度换算。</summary>
    private static double MagnitudeOf(IGfxObjectRef obj)
    {
        double magnitude = obj.GetNumber(ArrowRenderer.MagnitudeKey, double.NaN);
        if (!double.IsNaN(magnitude) && !double.IsInfinity(magnitude) && magnitude > 0) return magnitude;

        return VectorMath.ToNewtons(ArrowRenderer.LengthWorldOf(obj));
    }

    /// <summary>
    /// 找出按下点要分解的那根箭头：取"包围盒（外扩命中半径）包含该点"里<b>最近</b>的一根。
    /// </summary>
    /// <remarks>
    /// 用"外扩后的包围盒 + 中心距最近"而不是"几何精确命中"：一体机上手指点得没那么准，
    /// 一根本来只有 1.5 磅粗的箭头，手指几乎不可能精确压在线上。
    /// 而且这里<b>排除合力</b>（合力是算出来的，再分解它没有教学意义）。
    /// </remarks>
    private static IGfxObjectRef? PickArrow(IGfxObjectHost gfx, Point world)
    {
        IGfxObjectRef? best = null;
        double bestDistance = double.MaxValue;

        foreach (var obj in gfx.Objects)
        {
            if (obj.Kind != ArrowRenderer.KindName) continue;
            if (ArrowRenderer.IsSum(obj)) continue;

            var box = obj.BoundsWorld;
            box.Inflate(PickRadiusWorld * 0.5, PickRadiusWorld * 0.5);
            if (!box.Contains(world)) continue;

            double d2 = (obj.Center - world).LengthSquared;
            if (d2 < bestDistance) { bestDistance = d2; best = obj; }
        }

        return best;
    }
}
