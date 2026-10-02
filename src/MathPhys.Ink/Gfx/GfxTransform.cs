using System;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 图形对象的<b>位姿数学</b> —— 全程序唯一的"本地 ↔ 世界"换算来源。
/// </summary>
/// <remarks>
/// 与 <c>CanvasViewport</c> 是"世界 ↔ 视口"的唯一来源完全同构，理由也一样：
/// 渲染和命中测试各算一遍矩阵，迟早会出现"看着在这、点着不在"，
/// 而且只在某个旋转 / 缩放组合下复现 —— 这种 bug 最难查，所以从结构上堵死。
/// <para>
/// 本类是<b>纯函数</b>：只吃坐标、只吐坐标，不依赖视觉树、不依赖控件。
/// 于是没有窗口的验收 harness 也能把边界条件一条条钉死
/// （"算错角度这种事肉眼看不出来"，只能靠断言）。
/// </para>
/// </remarks>
public static class GfxTransform
{
    /// <summary>
    /// 手柄的命中半径（<b>屏幕</b>像素）。
    /// </summary>
    /// <remarks>
    /// 必须按屏幕尺寸算而不是按世界单位：一体机上手指触点是"胖"的（M4.2 实测结论），
    /// 手柄在缩到 50% 时若还按世界尺寸画，屏幕上就只有几个像素，表现成"点了没反应"。
    /// </remarks>
    public const double HandleRadiusPixels = 12.0;

    /// <summary>手柄的绘制尺寸（屏幕像素）。</summary>
    public const double HandleSizePixels = 16.0;

    /// <summary>手柄位于对象框外侧的距离（屏幕像素），避免与"拖动对象"抢同一个区域。</summary>
    public const double HandleGapPixels = 24.0;

    /// <summary>本地 → 世界的仿射矩阵（缩放 → 旋转 → 平移）。</summary>
    public static Matrix LocalToWorld(IGfxObjectRef obj)
    {
        var matrix = PoseMatrix(obj);
        matrix.Translate(obj.Center.X, obj.Center.Y);
        return matrix;
    }

    /// <summary>
    /// 只有缩放与旋转、<b>不含平移</b>的矩阵 —— 视觉层的 <c>RenderTransform</c> 用它。
    /// </summary>
    /// <remarks>
    /// 为什么渲染需要"少一个平移"的版本：WPF 的 <c>Canvas.Left/Top</c> 与
    /// <c>RenderTransform</c> 是<b>两段</b>变换（布局位置 + 渲染变换），
    /// 而 <c>RenderTransformOrigin=(0.5,0.5)</c> 会让旋转/缩放绕元素自身中心发生。
    /// 于是"元素左上角摆在 <c>Center - 尺寸/2</c>"+ 这一段矩阵，合起来恰好等于
    /// <see cref="LocalToWorld"/> —— 两边共用同一个矩阵，就不会出现"看着在这、点着不在"。
    /// </remarks>
    public static Matrix PoseMatrix(IGfxObjectRef obj)
    {
        var matrix = Matrix.Identity;
        matrix.Scale(obj.Scale, obj.Scale);
        matrix.Rotate(obj.RotationDegrees);
        return matrix;
    }

    /// <summary>世界 → 本地的仿射矩阵。</summary>
    public static Matrix WorldToLocal(IGfxObjectRef obj)
    {
        var matrix = LocalToWorld(obj);
        matrix.Invert();
        return matrix;
    }

    /// <summary>本地点 → 世界点。</summary>
    public static Point ToWorld(IGfxObjectRef obj, Point local) => LocalToWorld(obj).Transform(local);

    /// <summary>世界点 → 本地点。</summary>
    public static Point ToLocal(IGfxObjectRef obj, Point world) => WorldToLocal(obj).Transform(world);

    /// <summary>
    /// 把本地包围盒的四个角转到世界，取轴对齐外接矩形。
    /// </summary>
    /// <remarks>
    /// 用四角而不是"中心 ± 尺寸/2"：旋转以后那个式子是错的（宽高会互换而不自知）。
    /// </remarks>
    public static Rect BoundsWorld(IGfxObjectRef obj)
    {
        double halfWidth = Math.Max(obj.LocalSize.Width, 0) / 2.0;
        double halfHeight = Math.Max(obj.LocalSize.Height, 0) / 2.0;

        var matrix = LocalToWorld(obj);

        var a = matrix.Transform(new Point(-halfWidth, -halfHeight));
        var b = matrix.Transform(new Point(halfWidth, -halfHeight));
        var c = matrix.Transform(new Point(halfWidth, halfHeight));
        var d = matrix.Transform(new Point(-halfWidth, halfHeight));

        double minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
        double maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
        double minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
        double maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>世界点是否落在对象的本地矩形内（<paramref name="toleranceWorld"/> 为膨胀量）。</summary>
    /// <remarks>
    /// 用本地矩形而不是精确轮廓：点击容差在讲台上是<b>优点</b>
    /// （老师的目的是"抓住这个图形拖一下"，不是"证明我点得准"）。
    /// 半透明的量角器尤其如此 —— 空的那半边也该能按得住。
    /// </remarks>
    public static bool HitTest(IGfxObjectRef obj, Point world, double toleranceWorld)
    {
        var local = ToLocal(obj, world);

        double halfWidth = Math.Max(obj.LocalSize.Width, 0) / 2.0 + Math.Max(toleranceWorld, 0);
        double halfHeight = Math.Max(obj.LocalSize.Height, 0) / 2.0 + Math.Max(toleranceWorld, 0);

        return Math.Abs(local.X) <= halfWidth && Math.Abs(local.Y) <= halfHeight;
    }

    /// <summary>世界单位的手柄命中半径（把屏幕像素换算过去）。</summary>
    public static double HandleRadiusWorld(double viewScale)
        => HandleRadiusPixels / Math.Max(viewScale, 1e-6);

    /// <summary>旋转手柄的世界坐标（对象框外侧、正上方）。</summary>
    public static Point RotationHandleWorld(IGfxObjectRef obj, double viewScale)
        => ToWorld(obj, new Point(0, -HandleLocalGap(obj, viewScale) - obj.LocalSize.Height / 2.0));

    /// <summary>缩放手柄的世界坐标（对象框外侧、右下角）。</summary>
    public static Point ScaleHandleWorld(IGfxObjectRef obj, double viewScale)
    {
        double gap = HandleLocalGap(obj, viewScale);
        return ToWorld(obj, new Point(obj.LocalSize.Width / 2.0 + gap, obj.LocalSize.Height / 2.0 + gap));
    }

    /// <summary>删除按钮的世界坐标（对象框外侧、右上角）。</summary>
    /// <remarks>
    /// 删除必须有一个屏幕上的按钮：一体机上没有键盘，只做 <c>Del</c> 键等于没有这个功能
    /// （M7.2.1 的教训：只有键盘能解的默认值，在触屏设备上是 bug）。
    /// </remarks>
    public static Point DeleteButtonWorld(IGfxObjectRef obj, double viewScale)
    {
        double gap = HandleLocalGap(obj, viewScale);
        return ToWorld(obj, new Point(obj.LocalSize.Width / 2.0 + gap, -obj.LocalSize.Height / 2.0 - gap));
    }

    /// <summary>
    /// 屏幕像素 → 本地单位。
    /// </summary>
    /// <remarks>
    /// 两道缩放都要除掉：视口缩放（世界 → 屏幕）与对象自己的缩放（本地 → 世界）。
    /// 少除任何一个，旋转过的、缩放过的小对象手柄就会跑飞。
    /// </remarks>
    public static double PixelsToLocal(IGfxObjectRef obj, double viewScale, double pixels)
    {
        double combined = Math.Max(viewScale, 1e-6) * Math.Abs(obj.Scale);
        return pixels / Math.Max(combined, 1e-9);
    }

    /// <summary>手柄/按钮与对象框之间的本地间距（换算自屏幕像素）。</summary>
    private static double HandleLocalGap(IGfxObjectRef obj, double viewScale)
        => PixelsToLocal(obj, viewScale, HandleGapPixels);
}
