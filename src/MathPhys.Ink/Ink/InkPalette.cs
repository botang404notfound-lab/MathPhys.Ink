using System.Windows.Media;

namespace MathPhys.Ink.Ink;

/// <summary>一个可选的墨色。</summary>
public sealed record InkColorOption(string Name, Color Color);

/// <summary>一个可选的笔宽档位（单位 world = PDF point）。</summary>
public sealed record InkWidthOption(string Name, double WorldWidth);

/// <summary>
/// 墨色与笔宽的预设 —— 界面上摆哪几个按钮、哪个是默认，全由这里说了算。
/// </summary>
/// <remarks>
/// 刻意做成<b>纯数据</b>（不含任何 UI 元素）：验收 harness 可以据此断言
/// "默认笔真的等于调色板里标着默认的那一档"，
/// 从而排除"按钮写着「中」，实际笔宽却是别的值"这类只有肉眼才能发现的不一致。
/// <para>
/// ★ 本表是笔宽与墨色的<b>唯一来源</b>：工具栏上色块/笔宽按钮的长相、提示文字里的毫米数、
/// 以及 <see cref="PenProfile"/> 的出厂默认值，全部从这两个表派生。
/// 往表里加一档，界面上就多一颗按钮（按钮容器是 code-behind 按本表填的）。
/// </para>
/// </remarks>
public static class InkPalette
{
    /// <summary>预设墨色。红在首位 = 默认（批改惯例）。</summary>
    public static IReadOnlyList<InkColorOption> Colors { get; } = new[]
    {
        // 纯红刻意与 PenProfile 的出厂默认保持一致；这里不用 Colors.Red 是为了避开与
        // 本属性同名的 System.Windows.Media.Colors 产生解析歧义
        new InkColorOption("红", Color.FromRgb(0xFF, 0x00, 0x00)),
        new InkColorOption("黑", Color.FromRgb(0x1A, 0x1A, 0x1A)),
        new InkColorOption("蓝", Color.FromRgb(0x0B, 0x5F, 0xD0)),
        new InkColorOption("绿", Color.FromRgb(0x14, 0x8A, 0x3C)),
        new InkColorOption("橙", Color.FromRgb(0xE8, 0x6A, 0x0B)),

        // M21：深色纸底（黑板）的配套 —— 深底上「黑」笔等于没写，白笔才是粉笔。
        // ★ 不取纯白 #FFFFFF：纯白在黑底上过于跳，写一整页会晃眼；降一档到 #F5F5F5，
        //   与纸底 #1E1E1E 的对比度仍有 15:1 以上，投影下看得清又耐看。
        new InkColorOption("白", Color.FromRgb(0xF5, 0xF5, 0xF5)),
    };

    /// <summary>预设笔宽档位。档位数与顺序即工具栏上「笔宽」按钮的个数与顺序。</summary>
    /// <remarks>
    /// 单位是 world（PDF point）：0.8 pt ≈ 0.28 mm、1.5 pt ≈ 0.53 mm。
    /// 按钮提示里的毫米数由 <c>MainWindow.BuildInkButtons</c> 现算，不在这里手写，
    /// 免得改了宽度而提示还留着旧数。
    /// </remarks>
    public static IReadOnlyList<InkWidthOption> Widths { get; } = new[]
    {
        new InkWidthOption("细", 0.8),
        new InkWidthOption("中", 1.5),
        new InkWidthOption("粗", 2.5),
        new InkWidthOption("特粗", 4.0),
    };

    /// <summary>默认墨色索引（红）。</summary>
    public const int DefaultColorIndex = 0;

    /// <summary>
    /// 默认笔宽索引（细 = 0.8 world）。
    /// </summary>
    /// <remarks>
    /// ★ M21 从「中（1.5）」改成「细（0.8）」：真机批改时 1.5 pt 落在试卷行间会把印刷体
    /// 的小字糊住，细一档才写得进字缝里。四个档位的<b>数值一个都没动</b> ——
    /// 想让笔粗回去，点一下「中」即可，不需要换包。
    /// <para>
    /// 改这一个常量，工具栏的高亮位置、以及 <see cref="PenProfile"/> 的出厂笔宽会一起跟上
    /// （后者是从本表取的，不再自己写一份）。
    /// </para>
    /// </remarks>
    public const int DefaultWidthIndex = 0;
}
