using System.Windows.Media;

namespace MathPhys.Ink.Plugin.NativeSim;

/// <summary>
/// 仿真窗的作图色与笔。
/// </summary>
/// <remarks>
/// <para>
/// <b>刻意不接宿主的主题系统。</b>仿真窗是一块"物理示意图"，它的观感该跟着
/// <b>物理图册</b>走（白底、黑线、一处彩色强调），而不是跟着白板的界面主题翻转 ——
/// 而且它会被截成位图落到<b>纸面</b>上，纸面上永远得是深色墨、浅色底。
/// 所以这里是独立的一套常量，与 <c>Ink.*</c> / <c>Ui.*</c> 都不共享。
/// </para>
/// <para>
/// 所有笔刷与笔都是 <c>Frozen</c>：它们每帧被用几十次，冻结掉能省下载入期的
/// 属性变更开销（不冻的话每次 <c>DrawXxx</c> 都要过一遍 <c>Freezable</c> 检查）。
/// </para>
/// <para>
/// ★ 公开（而不是 <c>internal</c>）是为了让验收 harness 能拿同一套颜色做
/// <b>像素探针</b>（"这张图真的画出了东西"）。见 <see cref="SimScene.RenderPng"/>。
/// </para>
/// </remarks>
public static class SimPalette
{
    /// <summary>画布底色（极浅的暖白，投影上不刺眼，印到纸上也不抢眼）。</summary>
    public static readonly Brush Background = Make(0xFA, 0xFA, 0xF7);

    /// <summary>主墨色（几何线）。</summary>
    public static readonly Brush Ink = Make(0x1E, 0x1E, 0x1E);

    /// <summary>次要墨色（辅助线、刻度、说明文字）。</summary>
    public static readonly Brush Muted = Make(0x8A, 0x8A, 0x84);

    /// <summary>强调色（摆球、速度箭头）。</summary>
    public static readonly Brush Accent = Make(0x0B, 0x63, 0xC0);

    /// <summary>实物填充（滑块、物块）。</summary>
    public static readonly Brush Body = Make(0x4A, 0x55, 0x63);

    /// <summary>斜面的坡面填充。</summary>
    public static readonly Brush Slope = Make(0xE6, 0xE6, 0xDF);

    /// <summary>警告色（"摩擦力足够，推不动"这类提示）。</summary>
    public static readonly Brush Warn = Make(0xC0, 0x39, 0x2B);

    public static readonly Pen InkPen = MakePen(Ink, 2.0);
    public static readonly Pen ThinPen = MakePen(Ink, 1.2);
    public static readonly Pen MutedPen = MakePen(Muted, 1.2);
    public static readonly Pen AccentPen = MakePen(Accent, 2.4);
    public static readonly Pen BodyPen = MakePen(Body, 1.6);
    public static readonly Pen WarnPen = MakePen(Warn, 1.6);

    /// <summary>虚线笔（参考线、平衡位置）。</summary>
    public static readonly Pen DashPen = MakeDashedPen(Muted, 1.1, 5.0, 4.0);

    /// <summary>坡面摩擦的短斜线（"粗糙表面"的惯用画法）。</summary>
    public static readonly Pen HatchPen = MakePen(Muted, 1.0);

    /// <summary>用 RGB 造一支冻结的纯色刷。</summary>
    public static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>造一支冻结的实线笔。</summary>
    public static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    /// <summary>造一支冻结的虚线笔。</summary>
    public static Pen MakeDashedPen(Brush brush, double thickness, double on, double off)
    {
        var pen = new Pen(brush, thickness)
        {
            DashStyle = new DashStyle(new[] { on, off }, 0),
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat,
        };
        pen.Freeze();
        return pen;
    }
}
