using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MathPhys.Ink.Plugin.NativeSim.Kinematics;

namespace MathPhys.Ink.Plugin.NativeSim;

/// <summary>
/// 三种模型的画法 —— <b>纯绘图</b>，不持有状态、不认识窗口。
/// </summary>
/// <remarks>
/// <para>
/// 与窗口分开的理由：绘图是"给定模型状态与画布尺寸，画一张图"的纯函数，
/// 而窗口管的是滑杆、事件、动画时钟。混在一起之后，"图对不对"就只能靠肉眼在真机上看。
/// </para>
/// <para>
/// ★ <b>每张图的左上角都带一行参数标题</b>（如「单摆  L=1.00 m  g=9.80 m/s²」）。
/// 这不是装饰：这张画面会被<b>导出到卷面</b>上、跟着 PDF 印出去，
/// 印出来之后没有标题就没人知道当时那几个参数是多少。
/// </para>
/// <para>
/// ★★ <b>屏幕上的画与导出的 PNG 走的是同一条路</b>（同一个 <see cref="Draw"/>）：
/// 窗口的 <c>OnRender</c> 与 <see cref="RenderPng"/> 只是把
/// <c>DrawingContext</c> 换成"屏幕"或"离屏位图"。分成两份实现的话，
/// 迟早出现"导出来的图少了个箭头"，而那种差异在真机上从来不容易发现。
/// </para>
/// </remarks>
public static class SimScene
{
    /// <summary>斜面上"看得见"的行程（m）—— 滑块滑过这一段就回到起点重新来。</summary>
    /// <remarks>
    /// 与绘图比例是同一个常量（见 <see cref="DrawIncline"/>），窗口也读它做自动重启 ——
    /// 两处各自写一个数，迟早出现"图上跑出画面了才重启"或者"还没到边就跳回去"。
    /// </remarks>
    public const double InclineMetersOnSlope = 6.0;

    /// <summary>没有可用尺寸时的兜底画布宽（DIP）。</summary>
    public const double DefaultSurfaceWidth = 900.0;

    /// <summary>没有可用尺寸时的兜底画布高（DIP）。</summary>
    public const double DefaultSurfaceHeight = 560.0;

    /// <summary>画布四周的内边距（px）。</summary>
    private const double Pad = 26.0;

    private const double CaptionEm = 15.0;
    private const double LabelEm = 14.0;

    // ---------------------------------------------------------------- 对外入口

    /// <summary>
    /// 把某个模型画到给定的画布上（先在整块画布上铺底色）。
    /// </summary>
    /// <param name="dc">绘制上下文（屏幕 <c>OnRender</c> 与离屏位图共用）。</param>
    /// <param name="kind">画哪一种。</param>
    /// <param name="model">对应的模型实例；类型不匹配或为 <c>null</c> 时只画底色与标题。</param>
    /// <param name="size">画布尺寸（DIP）。</param>
    /// <param name="pixelsPerDip">每个 DIP 有多少物理像素（必须传，否则文字会糊）。</param>
    /// <param name="caption">左上角的参数标题。</param>
    public static void Draw(
        DrawingContext dc, SimKind kind, object? model, Size size, double pixelsPerDip, string caption)
    {
        double width = Normalize(size.Width, DefaultSurfaceWidth);
        double height = Normalize(size.Height, DefaultSurfaceHeight);
        var area = new Size(width, height);

        dc.DrawRectangle(SimPalette.Background, null, new Rect(0, 0, width, height));

        double dip = pixelsPerDip <= 0 || double.IsNaN(pixelsPerDip) ? 1.0 : pixelsPerDip;

        switch (kind)
        {
            case SimKind.Pendulum when model is PendulumModel pendulum:
                DrawPendulum(dc, pendulum, area, dip, caption);
                return;

            case SimKind.Spring when model is SpringModel spring:
                DrawSpring(dc, spring, area, dip, caption);
                return;

            case SimKind.Incline when model is InclineModel incline:
                DrawIncline(dc, incline, area, dip, caption);
                return;

            default:
                // 类型对不上：只留底色与标题，别画一半半成品
                DrawCaption(dc, caption, dip);
                return;
        }
    }

    /// <summary>
    /// 把某个模型渲染成 PNG 字节。
    /// </summary>
    /// <param name="kind">画哪一种。</param>
    /// <param name="model">模型实例。</param>
    /// <param name="width">画布宽（DIP）；非法值退回 <see cref="DefaultSurfaceWidth"/>。</param>
    /// <param name="height">画布高（DIP）；非法值退回 <see cref="DefaultSurfaceHeight"/>。</param>
    /// <param name="scale">像素放大倍数（1.5 ≈ 打印够清晰，又不会让字节数爆掉）。</param>
    /// <param name="pixelsPerDip">屏幕的 DPI 缩放（跟着窗口走，导出看起来才与屏幕一致）。</param>
    /// <param name="caption">左上角的参数标题。</param>
    /// <remarks>
    /// <para>
    /// <b>为什么离屏渲染而不是抓窗口。</b>这条路不需要窗口存在、不需要它被显示、
    /// 也不需要消息循环 —— 于是"导出的图长什么样"可以在无窗口的验收 harness 里断言。
    /// 而且它天然避开了原生 WPF 与 WebView2 共有的那类"抓帧抓不到"的问题：
    /// 这里画的就是同一段 <see cref="Draw"/> 代码。
    /// </para>
    /// <para>
    /// 背景是<b>不透明</b>的（<see cref="Draw"/> 第一件事就是铺底色），
    /// 所以 PNG 不需要再合成白底 —— 这一条在 M9 的导出里踩过
    /// （透明底落到纸上就是一片黑）。
    /// </para>
    /// </remarks>
    public static byte[] RenderPng(
        SimKind kind, object? model, double width, double height,
        double scale, double pixelsPerDip, string caption)
    {
        double w = Normalize(width, DefaultSurfaceWidth);
        double h = Normalize(height, DefaultSurfaceHeight);
        double factor = scale <= 0 || double.IsNaN(scale) ? 1.0 : Math.Min(scale, 4.0);

        int pixelWidth = Math.Max(16, (int)Math.Round(w * factor));
        int pixelHeight = Math.Max(16, (int)Math.Round(h * factor));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 位图按 factor 倍栅格化 ⇒ 文字也按同一倍率做亚像素定位，字缘才不毛
            Draw(dc, kind, model, new Size(w, h), pixelsPerDip * factor, caption);
        }

        var bitmap = new RenderTargetBitmap(
            pixelWidth, pixelHeight, 96.0 * factor, 96.0 * factor, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ---------------------------------------------------------------- 单摆

    /// <summary>画单摆。</summary>
    private static void DrawPendulum(
        DrawingContext dc, PendulumModel m, Size size, double pixelsPerDip, string caption)
    {
        double cx = size.Width / 2.0;
        double ceiling = Pad + 16.0;
        double usable = Math.Max(80.0, size.Height - ceiling - Pad - 24.0);

        // 比例尺：摆长上限（3 m）正好占满可用高度 ⇒ 几何与真实长度严格成比例
        double pxPerMeter = usable / PendulumModel.MaxLength;
        double rod = Math.Max(20.0, m.Length * pxPerMeter);

        // 天花板
        dc.DrawLine(SimPalette.InkPen, new Point(cx - 130, ceiling), new Point(cx + 130, ceiling));
        for (double x = cx - 126; x <= cx + 126; x += 18)
        {
            dc.DrawLine(SimPalette.MutedPen, new Point(x, ceiling), new Point(x - 11, ceiling - 11));
        }

        // 竖直参考线（拉长一点，让"θ 是从哪儿量的"一目了然）
        dc.DrawLine(SimPalette.DashPen, new Point(cx, ceiling), new Point(cx, ceiling + rod * 1.08));

        // 摆幅包络（±θ₀）：一眼看出"摆到哪儿会停下来"
        double envelope = PendulumModel.ToRadians(m.StartAngleDegrees);
        dc.DrawGeometry(null, SimPalette.DashPen,
            ArcGeometry(new Point(cx, ceiling), rod, -envelope, envelope));

        // 摆角圆弧 + 角度标注
        dc.DrawGeometry(null, SimPalette.MutedPen, ArcGeometry(new Point(cx, ceiling), 54.0, 0, m.Angle));
        DrawText(dc, $"{m.Angle * 180.0 / Math.PI:F0}°",
            Polar(new Point(cx, ceiling), 74.0, m.Angle / 2.0), LabelEm, SimPalette.Muted,
            pixelsPerDip, TextAlignment.Center);

        // 摆杆 + 摆球
        var pivot = new Point(cx, ceiling);
        var bob = Polar(pivot, rod, m.Angle);

        dc.DrawLine(SimPalette.InkPen, pivot, bob);
        dc.DrawEllipse(SimPalette.Ink, SimPalette.ThinPen, pivot, 4.5, 4.5);
        dc.DrawEllipse(SimPalette.Accent, SimPalette.AccentPen, bob, 13, 13);

        // 长度标注（贴在摆杆中点偏一侧）
        var mid = new Point((pivot.X + bob.X) / 2.0, (pivot.Y + bob.Y) / 2.0);
        DrawText(dc, $"L = {m.Length:F2} m", new Point(mid.X + 14, mid.Y - 9),
                 LabelEm, SimPalette.Muted, pixelsPerDip, TextAlignment.Left);

        // 切向速度箭头（够明显才画，免得静止时也戳一根零长度的线）
        if (Math.Abs(m.TangentialSpeed) > 0.08)
        {
            // 切向单位向量 = d/dθ (sinθ, cosθ) = (cosθ, −sinθ)；ω 的符号给方向
            var tangent = new Point(Math.Cos(m.Angle), -Math.Sin(m.Angle));
            DrawArrow(dc, bob, tangent, Math.Sign(m.AngularVelocity),
                      Math.Abs(m.TangentialSpeed), SimPalette.Accent);
        }

        DrawCaption(dc, caption, pixelsPerDip);
    }

    // ---------------------------------------------------------------- 弹簧振子

    /// <summary>画弹簧振子。</summary>
    private static void DrawSpring(
        DrawingContext dc, SpringModel m, Size size, double pixelsPerDip, string caption)
    {
        double cx = size.Width / 2.0;
        double ceiling = Pad + 16.0;
        double eqY = ceiling + (size.Height - ceiling - Pad) * 0.46;

        const double pxPerMeter = 240.0;
        double limit = Math.Max(40.0, size.Height - eqY - Pad - 46.0);
        double offset = Math.Clamp(m.Displacement * pxPerMeter, -limit, limit);

        const double bodyW = 84.0;
        const double bodyH = 52.0;
        double bodyCy = eqY + offset;
        double coilBottom = bodyCy - bodyH / 2.0;

        // 天花板
        dc.DrawLine(SimPalette.InkPen, new Point(cx - 120, ceiling), new Point(cx + 120, ceiling));
        for (double x = cx - 116; x <= cx + 116; x += 18)
        {
            dc.DrawLine(SimPalette.MutedPen, new Point(x, ceiling), new Point(x - 11, ceiling - 11));
        }

        // 平衡位置（虚线）
        dc.DrawLine(SimPalette.DashPen, new Point(Pad, eqY), new Point(size.Width - Pad, eqY));
        DrawText(dc, "平衡位置", new Point(size.Width - Pad, eqY - 20),
                 LabelEm, SimPalette.Muted, pixelsPerDip, TextAlignment.Right);

        // 弹簧（折线）
        dc.DrawGeometry(null, SimPalette.InkPen, SpringGeometry(cx, ceiling, coilBottom));

        // 物块
        dc.DrawRectangle(SimPalette.Body, SimPalette.BodyPen,
            new Rect(cx - bodyW / 2.0, bodyCy - bodyH / 2.0, bodyW, bodyH));

        // 位移标注：平衡位置 → 物块中心
        dc.DrawLine(SimPalette.AccentPen,
            new Point(cx + bodyW / 2.0 + 26, eqY), new Point(cx + bodyW / 2.0 + 26, bodyCy));
        DrawText(dc, $"x = {m.Displacement:F3} m",
                 new Point(cx + bodyW / 2.0 + 36, (eqY + bodyCy) / 2.0 - 9),
                 LabelEm, SimPalette.Accent, pixelsPerDip, TextAlignment.Left);

        // 速度箭头（方向 = 运动方向；屏幕 y 向下 ⇒ 正速度是 90°）
        if (Math.Abs(m.Velocity) > 0.02)
        {
            double length = Math.Min(Math.Abs(m.Velocity) * 46.0, 130.0);
            int sign = Math.Sign(m.Velocity);
            dc.DrawLine(SimPalette.AccentPen,
                new Point(cx, bodyCy), new Point(cx, bodyCy + sign * length));
            DrawArrowHead(dc, new Point(cx, bodyCy + sign * length), sign > 0 ? 90.0 : -90.0, SimPalette.Accent);
        }

        DrawCaption(dc, caption, pixelsPerDip);
    }

    // ---------------------------------------------------------------- 斜面

    /// <summary>画斜面滑块。</summary>
    private static void DrawIncline(
        DrawingContext dc, InclineModel m, Size size, double pixelsPerDip, string caption)
    {
        double left = Pad + 18.0;
        double bottom = size.Height - Pad - 12.0;

        double theta = m.AngleRadians;
        double cos = Math.Cos(theta);
        double sin = Math.Sin(theta);

        // 斜边长度：横竖两个方向都不许超出画面
        double slope = Math.Min(
            (size.Width - left - Pad) / Math.Max(cos, 1e-6),
            (bottom - Pad - 40.0) / Math.Max(sin, 1e-6));

        var apex = new Point(left + slope * cos, bottom - slope * sin);
        var foot = new Point(apex.X, bottom);
        var origin = new Point(left, bottom);

        // 三角斜劈（坡面本体）
        var triangle = new StreamGeometry();
        using (var ctx = triangle.Open())
        {
            ctx.BeginFigure(origin, isFilled: true, isClosed: true);
            ctx.LineTo(apex, isStroked: true, isSmoothJoin: false);
            ctx.LineTo(foot, isStroked: true, isSmoothJoin: false);
        }
        triangle.Freeze();
        dc.DrawGeometry(SimPalette.Slope, SimPalette.InkPen, triangle);

        // 坡面粗糙度（沿斜边的短斜线）
        var dir = new Vector(cos, -sin);
        var normal = new Vector(sin, cos);
        for (double s = 16; s < slope - 10; s += 26)
        {
            var at = origin + dir * s;
            dc.DrawLine(SimPalette.HatchPen, at, at + normal * 11);
        }

        // 倾角圆弧 + 标注
        dc.DrawGeometry(null, SimPalette.MutedPen, ArcGeometry(origin, 58.0, -theta, 0));
        DrawText(dc, $"θ = {m.AngleDegrees:F0}°",
                 Polar(origin, 82.0, -theta / 2.0), LabelEm, SimPalette.Muted, pixelsPerDip, TextAlignment.Center);

        // 滑块：沿斜面走，位置由位移换算
        double pxPerMeter = Math.Max(24.0, (slope - 120.0) / InclineMetersOnSlope);
        double along = Math.Clamp(56.0 + m.Travel * pxPerMeter, 0.0, slope);
        var onSlope = origin + dir * along;

        const double blockW = 60.0;
        const double blockH = 42.0;
        var blockCenter = onSlope + normal * (blockH / 2.0);

        dc.PushTransform(new RotateTransform(-m.AngleDegrees, blockCenter.X, blockCenter.Y));
        dc.DrawRectangle(SimPalette.Body, SimPalette.BodyPen,
            new Rect(blockCenter.X - blockW / 2.0, blockCenter.Y - blockH / 2.0, blockW, blockH));
        dc.Pop();

        // 速度箭头
        if (Math.Abs(m.Speed) > 0.05)
        {
            double length = Math.Min(Math.Abs(m.Speed) * 26.0 + 26.0, 140.0);
            var tip = blockCenter + dir * (Math.Sign(m.Speed) * (blockW / 2.0 + length));
            dc.DrawLine(SimPalette.AccentPen, blockCenter, tip);

            double headDegrees = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
            DrawArrowHead(dc, tip, Math.Sign(m.Speed) > 0 ? headDegrees : headDegrees + 180.0, SimPalette.Accent);
        }

        // 摩擦因数与运动状态（贴在图中部偏下）
        Brush stateBrush = m.IsAtRest && !m.SlidesFromRest ? SimPalette.Warn : SimPalette.Muted;
        DrawText(dc, $"µ = {m.Friction:F2}　临界 tanθ = {m.CriticalFriction:F2}　{m.DescribeMotion()}",
                 new Point(size.Width / 2.0, size.Height - Pad - 4),
                 LabelEm, stateBrush, pixelsPerDip, TextAlignment.Center);

        DrawCaption(dc, caption, pixelsPerDip);
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>尺寸兜底：<c>NaN</c> / 无穷 / 太小一律退回给定值。</summary>
    private static double Normalize(double value, double fallback)
        => double.IsNaN(value) || double.IsInfinity(value) || value < 2.0 ? fallback : value;

    /// <summary>在左上角画参数标题（导出成图后靠它认得出当时的参数）。</summary>
    private static void DrawCaption(DrawingContext dc, string caption, double pixelsPerDip)
        => DrawText(dc, caption, new Point(Pad, Pad - 12), CaptionEm, SimPalette.Ink,
                    pixelsPerDip, TextAlignment.Left);

    /// <summary>极坐标取点（角度以"正下方"为 0、顺时针为正，与摆的 θ 同一套）。</summary>
    private static Point Polar(Point origin, double radius, double angle)
        => new(origin.X + radius * Math.Sin(angle), origin.Y + radius * Math.Cos(angle));

    /// <summary>从 <paramref name="from"/> 沿 <paramref name="unit"/> 方向画带箭头的长度线（长度按速率缩放）。</summary>
    private static void DrawArrow(
        DrawingContext dc, Point from, Point unit, int sign, double speed, Brush brush)
    {
        var direction = new Vector(unit.X, unit.Y);
        if (direction.Length < 1e-9) return;
        direction.Normalize();

        if (sign == 0) sign = 1;

        double length = Math.Min(speed * 34.0 + 18.0, 150.0);
        var tip = from + direction * (sign * (16.0 + length));

        dc.DrawLine(SimPalette.AccentPen, from + direction * (sign * 14.0), tip);

        double degrees = Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
        DrawArrowHead(dc, tip, sign > 0 ? degrees : degrees + 180.0, brush);
    }

    /// <summary>画一个实心三角箭头（朝 <paramref name="degrees"/> 方向）。</summary>
    private static void DrawArrowHead(DrawingContext dc, Point tip, double degrees, Brush brush)
    {
        const double size = 11.0;
        double radians = degrees * Math.PI / 180.0;
        var back = new Vector(-Math.Cos(radians), -Math.Sin(radians));
        var side = new Vector(-back.Y, back.X);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(tip, isFilled: true, isClosed: true);
            ctx.LineTo(tip + back * size + side * (size * 0.5), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(tip + back * size - side * (size * 0.5), isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>绕 <paramref name="center"/> 从 <paramref name="fromAngle"/> 到 <paramref name="toAngle"/> 的圆弧。</summary>
    /// <remarks>
    /// 这里的弧都很短（最多 90°），用一段三次贝塞尔逼近即可：
    /// 控制点长度取经典近似 <c>k = 4/3·tan(Δ/4)</c>，屏幕上分不出来。
    /// </remarks>
    private static Geometry ArcGeometry(Point center, double radius, double fromAngle, double toAngle)
    {
        double delta = toAngle - fromAngle;
        if (Math.Abs(delta) < 1e-4) delta = delta < 0 ? -1e-4 : 1e-4;

        var start = Polar(center, radius, fromAngle);
        var end = Polar(center, radius, toAngle);

        // 切向：d/dθ (sin θ, cos θ) = (cos θ, −sin θ)
        var startTangent = new Vector(Math.Cos(fromAngle), -Math.Sin(fromAngle));
        var endTangent = new Vector(Math.Cos(toAngle), -Math.Sin(toAngle));
        double k = radius * 4.0 / 3.0 * Math.Tan(delta / 4.0);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.BezierTo(
                start + startTangent * k,
                end - endTangent * k,
                end,
                isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>弹簧的折线（从 <paramref name="top"/> 到 <paramref name="bottom"/>，绕 <c>x = cx</c>）。</summary>
    private static Geometry SpringGeometry(double cx, double top, double bottom)
    {
        const double widen = 20.0;
        double length = Math.Max(20.0, bottom - top);
        int turns = Math.Clamp((int)(length / 22.0), 4, 16);
        double each = length / turns;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(cx, top), isFilled: false, isClosed: false);

            double y = top;
            bool right = true;
            for (int i = 0; i < turns; i++)
            {
                y += each;
                ctx.LineTo(new Point(cx + (right ? widen : -widen), y), isStroked: true, isSmoothJoin: false);
                right = !right;
            }

            ctx.LineTo(new Point(cx, bottom), isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>画一行字（中文用微软雅黑；<paramref name="pixelsPerDip"/> 必须传，否则字会糊）。</summary>
    private static void DrawText(
        DrawingContext dc, string text, Point at, double emSize, Brush brush,
        double pixelsPerDip, TextAlignment align)
    {
        if (string.IsNullOrEmpty(text)) return;

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal,
                         FontWeights.SemiBold, FontStretches.Normal),
            emSize,
            brush,
            pixelsPerDip <= 0 ? 1.0 : pixelsPerDip);

        double x = align switch
        {
            TextAlignment.Center => at.X - formatted.Width / 2.0,
            TextAlignment.Right => at.X - formatted.Width,
            _ => at.X,
        };

        dc.DrawText(formatted, new Point(x, at.Y));
    }
}
