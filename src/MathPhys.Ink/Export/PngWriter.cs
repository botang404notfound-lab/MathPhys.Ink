using System.IO;
using System.Windows.Media.Imaging;

namespace MathPhys.Ink.Export;

/// <summary>
/// PNG 落盘：文件名规则 + 编码。
/// </summary>
/// <remarks>
/// 刻意<b>不</b>在这里弹任何对话框 —— 选目录是界面层的事。这样本类全是可断言的纯逻辑
/// （名字怎么拼、字节写没写出去），harness 不必去点一个真对话框。
/// <para>
/// 也刻意<b>不</b>在这里清洗非法字符：基名由调用方经 <see cref="ExportNaming"/> 生成。
/// 同一套清洗写两遍，早晚会分叉成两种行为（一个允许的名字在另一处被拒），
/// 这类分叉只有用户撞上才发现。这里只对空基名兜底。
/// </para>
/// </remarks>
public static class PngWriter
{
    /// <summary>基名为空时的兜底名字。</summary>
    public const string FallbackBaseName = "导出页";

    /// <summary>
    /// 一页的文件名：<c>基名-第NN页.png</c>。
    /// </summary>
    /// <remarks>
    /// <b>页号补零、且补到"比总页数的位数还多一位"</b>（<c>Math.Max(2, 位数)</c>）：
    /// 不补零的话，文件管理器按名字排序会得到 1、10、11、2、3…… 打印店老板拿到的顺序就是乱的。
    /// 两位起步是为了 9 页、99 页这类常见卷子看起来整齐；超过 99 页时自动扩到三位，
    /// 不会出现"第100页"和"第10页"排在一起分不出先后。
    /// </remarks>
    public static string FileName(string? baseName, int pageIndex, int pageCount)
    {
        string name = string.IsNullOrWhiteSpace(baseName) ? FallbackBaseName : baseName.Trim();

        int digits = Math.Max(2, Math.Max(1, pageCount).ToString().Length);
        string page = (pageIndex + 1).ToString().PadLeft(digits, '0');

        return $"{name}-第{page}页.png";
    }

    /// <summary>
    /// 把一张位图编码成 PNG 字节（不落盘）。
    /// </summary>
    /// <remarks>
    /// 单独抽出来是为了<b>给 PDF 复用同一条图像链</b>（<see cref="PdfWriter"/> 贴的就是这些字节）：
    /// 一处编码、一处质量参数，不会出现"导出的 PNG 清晰、塞进 PDF 里那张糊"这种分叉。
    /// <para>
    /// 用 PNG 而不是 JPEG：试卷是<b>白底细黑字</b>，JPEG 的块效应正好落在笔画边缘上，
    /// 打印出来是"字发毛"。体积换清晰度，在这个场景里划算。
    /// </para>
    /// </remarks>
    public static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 把一张位图编码成 PNG 写到 <paramref name="path"/>，返回写出的字节数。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="FileMode.Create"/>：重复导出同名试卷应当<b>覆盖</b>上一次的结果，
    /// 而不是在旁边堆出"-1""-2"。老师重新导出的心智是"更新那一份"。
    /// <para>
    /// 写完显式 <c>Flush</c> 并<b>回读文件长度</b>再返回 —— 返回值会被拿去给老师报
    /// "共 N MB"，也是冒烟脚本的判据；一个没落到盘上的长度会让两边都说假话。
    /// </para>
    /// </remarks>
    public static long Write(string path, BitmapSource bitmap)
    {
        byte[] bytes = Encode(bitmap);

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        // 回读实际长度：不能拿"编码前的估算"或流的 Position 冒充（缓冲可能还没落地）
        return new FileInfo(path).Length;
    }
}
