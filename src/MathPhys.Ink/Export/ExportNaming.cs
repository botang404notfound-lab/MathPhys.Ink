using System.IO;
using System.Text;
using MathPhys.Ink.Ink;

namespace MathPhys.Ink.Export;

/// <summary>
/// 导出成品的<b>基名</b>规则 —— "这份卷子导出来叫什么"。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么值得单独一个类。</b>基名同时喂给 PNG 文件名（<see cref="PngWriter.FileName"/>）、
/// PDF 文件名（<see cref="PdfWriter.FileName"/>）与 PDF 元数据标题三处。
/// 规则一散，就会出现"PNG 叫这个名字、PDF 叫那个名字、打印店拿到两套"；
/// 而更糟的一种分叉见下。
/// </para>
/// <para>
/// <b>★ 工程模式下绝不能退到 PDF 路径（这是 M9 S3 真正要守的一条）。</b>
/// <c>.twb</c> 内嵌的试卷在打开时被释放到
/// <c>%TEMP%\MathPhys.Ink\工程卷面\&lt;工程名&gt;-&lt;8 位随机&gt;.pdf</c>。
/// 若基名从那个路径取，导出的成品就会叫
/// <c>26西附全真模拟1物理试卷-a1b2c3d4-第01页.png</c> ——
/// 既难看，又和<b>同一次会话</b>里再打开一次工程产生的另一个随机名对不上；
/// 老师只会觉得"导出出来的文件名乱七八糟"。
/// 工程有标题就只认标题，标题为空时兜底，<b>永不</b>去看路径。
/// </para>
/// <para>
/// 清洗只此一处：<see cref="Safe"/> 同时供工程"另存为"的默认文件名用（见
/// <c>MainWindow.PickProjectSavePath</c>）。同一套清洗写两遍，早晚分叉成两种行为。
/// </para>
/// </remarks>
public static class ExportNaming
{
    /// <summary>取不出任何名字时的兜底基名。</summary>
    /// <remarks>
    /// 与 <see cref="PngWriter.FallbackBaseName"/> 同值、且 harness 专门断言这一点 ——
    /// 两边不一致时，同一个"没名字的卷子"导 PNG 与导 PDF 会得到两个不同的名字。
    /// </remarks>
    public const string FallbackBaseName = "导出页";

    /// <summary>工程"另存为"对话框用的兜底名（那时还没有试卷名可用）。</summary>
    public const string FallbackProjectName = "未命名工程";

    /// <summary>
    /// 这份卷子导出来该叫什么（不含扩展名、不含页号）。
    /// </summary>
    /// <param name="hasProject">当前是否处于工程模式（打开了 <c>.twb</c>）。</param>
    /// <param name="projectTitle">工程标题；非工程模式忽略。</param>
    /// <param name="pdfPath">当前 PDF 的路径；<b>仅</b>非工程模式使用。</param>
    /// <remarks>
    /// 非工程模式（老师直接打开一个 PDF，M7.x 行为）保持原样：取 PDF 文件名去扩展名。
    /// 那条路上的 PDF 是老师自己选的、路径有人话含义，取名字是对的。
    /// </remarks>
    public static string BaseName(bool hasProject, string? projectTitle, string? pdfPath)
    {
        if (hasProject) return Safe(projectTitle);

        // 没有 PDF 时 DefaultTitle 自己会给"未命名工程"—— 那个词做成品文件名不合适
        // （导出的是一份卷子，不是一份工程），所以再多兜一层。
        return Safe(pdfPath is null ? null : ProjectComposer.DefaultTitle(pdfPath));
    }

    /// <summary>
    /// 把任意标题清洗成能当文件名的样子；空则用 <see cref="FallbackBaseName"/>。
    /// </summary>
    public static string Safe(string? name) => Safe(name, FallbackBaseName);

    /// <summary>
    /// 同 <see cref="Safe(string?)"/>，但可指定兜底名（工程另存为用
    /// <see cref="FallbackProjectName"/>）。
    /// </summary>
    /// <remarks>
    /// <b>为什么要去掉结尾的点与空格。</b>Windows 的文件名规则里，结尾的点与空格会被
    /// <b>静默丢掉</b>：老师把工程标题写成"第一章 静电场。"，落盘却变成
    /// <c>第一章 静电场.png</c>；而程序记着的是带点的那份 —— 下次"重新导出"就会
    /// 因为拿到的路径不存在而报一个与事实无关的错。去掉它，让程序记的就是盘上的。
    /// </remarks>
    public static string Safe(string? name, string fallback)
    {
        string alternate = string.IsNullOrWhiteSpace(fallback) ? FallbackBaseName : fallback;

        if (string.IsNullOrWhiteSpace(name)) return alternate;

        char[] invalid = Path.GetInvalidFileNameChars();
        var buffer = new StringBuilder(name!.Length);

        foreach (char c in name)
        {
            // Array.IndexOf 的规模是"非法字符 41 个"这个量级，每次都线性扫也远快于
            // 建一个 HashSet —— 何况这个函数一次导出只调一两次。
            buffer.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string result = buffer.ToString().Trim().TrimEnd('.');

        return result.Length == 0 ? alternate : result;
    }
}
