using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 一个图形对象的<b>可序列化形态</b> —— 存档、撤销快照、harness 断言都用它。
/// </summary>
/// <remarks>
/// 与运行期的 <see cref="GfxObject"/> 分开是刻意的：
/// 序列化格式是"对外承诺"（写进了 <c>.tbink</c>，老文件要能读一辈子），
/// 运行期对象则会随实现自由演化。两件事绑在同一个类上，早晚会出现
/// "为了让代码更顺而偷偷改了字段名，结果读不了半年前的批注"。
/// <para>
/// 关键不变量：<b>只存世界坐标 + 无量纲参数</b>，绝不存屏幕像素、不存渲染器算出来的尺寸
/// （尺寸由渲染器的 <c>Measure</c> 重新算）。所以同一份存档在不同 DPI / 不同版本上都落在同一处。
/// </para>
/// </remarks>
public sealed class GfxObjectData
{
    /// <summary>对象 Id（宿主生成）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>对象种类，对应渲染器注册的 Kind。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>创建它的插件显示名（诊断用：插件被禁用时告诉用户少了谁）。</summary>
    public string Plugin { get; set; } = string.Empty;

    /// <summary>中心点世界坐标 X（PDF point）。</summary>
    public double X { get; set; }

    /// <summary>中心点世界坐标 Y（PDF point）。</summary>
    public double Y { get; set; }

    /// <summary>旋转角（度）。</summary>
    public double Rotation { get; set; }

    /// <summary>缩放系数。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>创建时的墨色（<c>#AARRGGBB</c>）。</summary>
    /// <summary>
    /// 图形对象的出厂颜色。
    /// </summary>
    /// <remarks>
    /// ★ 这是这个值的**权威定义**，刻意**不是**设计令牌：它属于**存档格式**的一部分 ——
    /// 新建对象时写进工程文件的就是这一串，改它会让新旧存档对不上。
    /// <para>
    /// <c>Design/Palette.Canvas.xaml</c> 里的 <c>Canvas.GfxDefault</c> 是它的镜像
    /// （画布板需要把"图形默认色"记在册），harness 会把两者放在一起断言：
    /// 改一处忘了另一处，导出的图上默认色就会偏。
    /// </para>
    /// </remarks>
    public const string DefaultColor = "#FF000000";

    /// <summary>出厂颜色的 <see cref="System.Windows.Media.Color"/> 形式（给内存中的对象用）。</summary>
    public static readonly System.Windows.Media.Color DefaultColorValue =
        ParseColor(DefaultColor) ?? System.Windows.Media.Colors.Black;

    public string Color { get; set; } = DefaultColor;

    /// <summary>创建时的线宽（世界单位）。</summary>
    public double LineWidth { get; set; }

    /// <summary>数值参数。</summary>
    public Dictionary<string, double> Numbers { get; set; } = new();

    /// <summary>文本参数。</summary>
    public Dictionary<string, string> Texts { get; set; } = new();

    /// <summary>把颜色写成 <c>#AARRGGBB</c>（大小写不敏感、可读）。</summary>
    public static string ToHex(Color color)
        => "#" + color.A.ToString("X2", CultureInfo.InvariantCulture)
               + color.R.ToString("X2", CultureInfo.InvariantCulture)
               + color.G.ToString("X2", CultureInfo.InvariantCulture)
               + color.B.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>
    /// 解析 <c>#AARRGGBB</c> / <c>#RRGGBB</c>；解析不了返回 <c>null</c>（<b>不抛异常</b>）。
    /// </summary>
    /// <remarks>
    /// 手改过的存档、别的工具生成的文件都可能给出怪值。一个字段读不懂，
    /// 只该让这一个对象退回默认颜色，不该让整份批注打不开。
    /// </remarks>
    public static Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim();
        if (value.StartsWith('#')) value = value[1..];

        if (value.Length is not (6 or 8)) return null;

        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
        {
            return null;
        }

        // 本类有个叫 Color 的字符串属性，会把 System.Windows.Media.Color 这个类型名遮住 ——
        // 这里必须写全名，否则编译器认为我们在访问实例属性。
        return value.Length == 6
            ? System.Windows.Media.Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : System.Windows.Media.Color.FromArgb(
                (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }
}
