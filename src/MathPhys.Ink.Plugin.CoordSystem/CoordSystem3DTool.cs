using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CoordSystem;

/// <summary>
/// 空间直角坐标系（xOyZ 静态轴测）：按下放原点 → 拖出单位长度 → 抬笔落成对象（M23）。
/// </summary>
/// <remarks>
/// 交互与平面版完全同构（复用 <see cref="CoordSystemTool.ResolveOrigin"/> 的吸页逻辑），
/// 只是落对象换成 <see cref="CoordSystem3DRenderer.KindName"/>、参数换成
/// 「对称范围 ±extent」。没有网格/标签开关可调 —— 轴测图上那点虚线与字母就是全部修饰。
/// </remarks>
public sealed class CoordSystem3DTool : ITool, IGfxTool
{
    private IToolContext? _context;

    private bool _drawing;

    /// <summary>已吸附的原点（世界坐标）—— 按下那一刻定下，之后再不挪动。</summary>
    private Point _origin;

    private double _unitWorld = CoordSystem3DGeometry.DefaultUnitWorld;

    private int _pageIndex;

    private Path? _axesPreview;

    public string Id => CoordSystemToolIds.Space3D;

    public string DisplayName => "空间坐标系";

    public string ToolTip
        => "空间坐标系（xOyZ）：按下放原点（自动吸到当前页的页中心/页边），拖出单位长度，抬笔落下。"
        + "静态轴测画法 —— x 轴指向左下 45°、y 轴水平向右、z 轴竖直向上，适合讲空间向量与投影。"
        + "落下后可用「选择」拖动、旋转、缩放。";

    /// <summary>
    /// 快捷键刻意<b>不设</b>：C 已被族代表（平面坐标系）占用，
    /// 空间坐标系从「坐标系」族的二级菜单进 —— 重键的表现是"按下去只选中其中一个"，查不出来。
    /// </summary>
    public Key? Shortcut => null;

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
                (_origin, _pageIndex) = CoordSystemTool.ResolveOrigin(context, pointer.World);
                _drawing = true;
                _unitWorld = CoordSystem3DGeometry.DefaultUnitWorld;

                context.ClearPreview();
                BuildPreview(context);
                UpdatePreview();
                break;

            case ToolPointerPhase.Move:
                if (!_drawing) return;
                {
                    double drag = (pointer.World - _origin).Length;
                    _unitWorld = CoordSystem3DGeometry.ClampUnit(drag);
                    UpdatePreview();
                    context.SetStatus(Describe());
                }
                break;

            case ToolPointerPhase.Up:
                if (!_drawing) return;
                _drawing = false;
                Commit(context, pointer.World);
                break;
        }
    }

    // ---------------------------------------------------------------- 抬笔

    private void Commit(IToolContext context, Point rawWorld)
    {
        var origin = _origin;
        double drag = (rawWorld - origin).Length;

        context.ClearPreview();
        Reset();

        if (drag < CoordSystemGeometry.MinDragWorld)
        {
            context.SetStatus("空间坐标系：拖动距离太短，未落下");
            return;
        }

        var gfx = context.Gfx;
        if (gfx is null)
        {
            context.SetStatus("空间坐标系：当前程序不支持图形对象，坐标系放不下");
            return;
        }

        double unit = CoordSystem3DGeometry.ClampUnit(drag);

        gfx.Add(new GfxDraft
        {
            Kind = CoordSystem3DRenderer.KindName,
            Center = origin,
            RotationDegrees = 0.0,
            Scale = 1.0,
            Color = context.PenColor,
            LineWorldWidth = context.PenWorldWidth,

            Numbers = new Dictionary<string, double>
            {
                [CoordSystem3DGeometry.UnitWorldKey] = unit,
                [CoordSystem3DGeometry.ExtentKey] = CoordSystem3DGeometry.DefaultExtent,
                [CoordSystem3DGeometry.StepKey] = 1.0,
            },
        });

        string clamped = unit > drag + 1e-9 ? "（已按最小尺寸放大）" : string.Empty;
        context.SetStatus(Describe() + clamped);
    }

    // ---------------------------------------------------------------- 预览

    private void BuildPreview(IToolContext context)
    {
        var stroke = new SolidColorBrush(context.PenColor) { Opacity = 0x66 / 255.0 };

        _axesPreview = new Path
        {
            Stroke = stroke,
            StrokeThickness = context.PenWorldWidth,
            Stretch = Stretch.None,
            IsHitTestVisible = false,
        };

        context.AddPreview(_axesPreview);
    }

    /// <summary>
    /// 刷新预览：只画三根正半轴（负半轴/刻度/字母太碎，预览里不画，免得遮住卷子）。
    /// </summary>
    /// <remarks>
    /// 与平面版同一条不变量：<c>Stretch=None</c> 的 <c>Path</c> 不平移负坐标几何，
    /// 所以摆位 <c>Canvas.Left/Top</c> 直接给<b>原点本身</b>。
    /// </remarks>
    private void UpdatePreview()
    {
        var preview = _axesPreview;
        if (preview is null) return;

        double e = CoordSystem3DGeometry.DefaultExtent * _unitWorld;

        var axes = new StreamGeometry();
        using (var ctx = axes.Open())
        {
            foreach (var (_, dir) in CoordSystem3DGeometry.Axes)
            {
                ctx.BeginFigure(new Point(0, 0), isFilled: false, isClosed: false);
                ctx.LineTo(new Point(dir.X * e, dir.Y * e), isStroked: true, isSmoothJoin: false);
            }
        }
        axes.Freeze();

        preview.Data = axes;
        Canvas.SetLeft(preview, _origin.X);
        Canvas.SetTop(preview, _origin.Y);
    }

    // ---------------------------------------------------------------- 复位

    private void Reset()
    {
        _drawing = false;
        _axesPreview = null;
        _origin = default;
        _unitWorld = CoordSystem3DGeometry.DefaultUnitWorld;
        _pageIndex = -1;
    }

    private string Describe()
    {
        string pageText = _pageIndex > 0 ? $"（吸到第 {_pageIndex} 页）" : string.Empty;
        return CoordSystem3DGeometry.Describe(_unitWorld, CoordSystem3DGeometry.DefaultExtent, 1.0)
            + pageText;
    }
}
