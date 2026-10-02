using System.IO;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Ink;

/// <summary>
/// 把工程内嵌的 PDF 释放成临时文件，并负责收尾清理。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么绕不开这一步。</b>Pdfium 只认路径、喂不了字节流，而 <c>.twb</c> 里的试卷
/// 是包内字节 —— 于是"打开内嵌工程"必然要落一次盘。M8 起就有这个动作，
/// M9 S3 把它从界面层挪到这里，理由只有一个：<b>它必须能被断言</b>。
/// 原来它是 <c>MainWindow</c> 里的一个私有方法，"从 .twb 内嵌 PDF 导出"这条路
/// 就只能靠人去点界面才能验；而这条接缝恰好是最容易静默坏掉的地方
/// （文件名变了、文件没落盘、两次释放互相覆盖……每一样都只在"老师拿到一份怪名字的成品"
/// 时才被发现）。
/// </para>
/// <para>
/// <b>★ 文件名带 8 位随机后缀，不许用固定名字。</b>同一个工程在一个会话里可能被打开两次
/// （关掉再打开）。固定名会让第二次的写入撞上第一份仍被 pdfium 打开着的文件：
/// 运气好只是"写不进去"，运气不好是渲染读到一半被替换的内容 —— 两种都不出声。
/// </para>
/// <para>
/// <b>失败不抛异常，返回 <c>null</c>。</b>临时目录不可写（磁盘满、被安全软件锁住、
/// <c>%TEMP%</c> 指到一个不存在的盘）时，调用方要的是"拿到一个能说给老师听的理由"，
/// 而不是一个冒到 <c>App</c> 层的异常弹窗 —— 那时工程已经装了一半。
/// </para>
/// </remarks>
public sealed class EmbeddedPdfReleaser
{
    /// <summary>临时文件所在子目录名（放在 <c>%TEMP%\MathPhys.Ink\</c> 之下）。</summary>
    public const string FolderName = "工程卷面";

    /// <summary>本次进程释放出来的、<b>尚未删掉</b>的临时文件（按释放顺序）。</summary>
    private readonly List<string> _released = new();

    private readonly string _directory;

    /// <summary>
    /// 建一个释放器。
    /// </summary>
    /// <param name="directory">
    /// 临时文件目录；<c>null</c> 表示用 <see cref="DefaultDirectory"/>。
    /// 允许指定是为了让 harness 能拿一个"故意写不进去"的目录来验失败分支。
    /// </param>
    public EmbeddedPdfReleaser(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory!;
    }

    /// <summary>默认临时目录：<c>%TEMP%\MathPhys.Ink\工程卷面</c>。</summary>
    /// <remarks>
    /// 落在自己的子目录里而不是 <c>%TEMP%</c> 根上：老师把 <c>%TEMP%</c> 打开排查时
    /// 能一眼看出这些是我们放的，而不是满屏分不清来源的临时文件。
    /// </remarks>
    public static string DefaultDirectory
        => Path.Combine(Path.GetTempPath(), "MathPhys.Ink", FolderName);

    /// <summary>本目录，供日志与断言引用。</summary>
    public string Directory => _directory;

    /// <summary>还挂着的临时文件数（<b>尚未删掉</b>的那些；清理干净后为 0）。</summary>
    public int Count => _released.Count;

    /// <summary>还挂着的临时文件路径（只读视图）。</summary>
    public IReadOnlyList<string> Paths => _released;

    /// <summary>
    /// 拼一个释放路径：<c>&lt;目录&gt;\&lt;工程名&gt;-&lt;后缀&gt;.pdf</c>。
    /// </summary>
    /// <remarks>
    /// 单独抽成纯函数是为了让 harness 直接断言命名规则，不必真落一次盘。
    /// 工程名留在前缀里是给<b>排查</b>用的：老师把日志发回来时，
    /// <c>工程卷面\静电场复习-3f9a1c02.pdf</c> 比一串 GUID 有用得多。
    /// </remarks>
    public static string BuildPath(string directory, string twbPath, string suffix)
    {
        string name = Path.GetFileNameWithoutExtension(twbPath);
        if (string.IsNullOrWhiteSpace(name)) name = "工程";

        return Path.Combine(directory, name + "-" + suffix + ".pdf");
    }

    /// <summary>
    /// 把 <paramref name="pdfBytes"/> 释放到临时文件；成功返回路径，失败返回 <c>null</c>。
    /// </summary>
    /// <param name="twbPath">来源工程路径（只用来取文件名前缀）。</param>
    /// <param name="pdfBytes">内嵌的 PDF 字节。</param>
    public string? Release(string twbPath, byte[] pdfBytes)
    {
        if (pdfBytes is null || pdfBytes.Length == 0)
        {
            AppLog.Warn("工程内嵌试卷内容为空，无法释放到临时文件");
            return null;
        }

        string path;

        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            path = BuildPath(_directory, twbPath, Guid.NewGuid().ToString("N")[..8]);

            // FileMode.Create：同名（几乎不可能，GUID 撞了）时覆盖而不是追加 ——
            // 追加会在原字节后面续上一段，得到一个"开头像 PDF、后面是垃圾"的文件，
            // pdfium 打开它可能还能渲出前几页，是最难解释的一种坏。
            File.WriteAllBytes(path, pdfBytes);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"工程内嵌试卷释放失败：{twbPath} —— {ex.Message}");
            return null;
        }

        _released.Add(path);
        AppLog.Info($"工程内嵌试卷已释放到临时文件：{path}（{pdfBytes.Length / 1024} KB）");

        return path;
    }

    /// <summary>
    /// 尽力删掉本次释放出来的临时文件，返回<b>这一轮</b>真正删掉的数量。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 删不掉（仍被占用、权限不足）<b>不算失败</b>：临时目录迟早会被系统清理，
    /// 而为一个删不掉的副本打断退出流程是本末倒置。失败只记日志。
    /// </para>
    /// <para>
    /// <b>★ 登记表代表「还没删掉的」，不是「曾经释放过的」。</b>
    /// 删成功（或文件已经不在了）才把它从登记里摘掉；删不掉的<b>留着</b>，
    /// 下一次调用还有机会。反过来写（删不掉也照样清空）有两个后果：
    /// 顺序万一被排错（把清理排在关文档之前，文件仍被 pdfium 占着），
    /// 这一份临时卷面就<b>再也没人管</b>，只在日志里留一行 warn；
    /// 而且"还剩几份没清掉"这个信息也丢了。
    /// <i>（S3 真机冒烟恰好抓到过这个顺序错误：M8 起临时试卷其实一次都没删掉过。）</i>
    /// </para>
    /// <para>
    /// 幂等：全都删掉之后再调返回 0，不抛异常 —— 关窗口与关工程两处都会调它。
    /// </para>
    /// </remarks>
    public int Cleanup()
    {
        int removed = 0;

        // 倒着走：一边删一边 RemoveAt，正着走会漏掉被移除元素后面的那一项
        for (int i = _released.Count - 1; i >= 0; i--)
        {
            string path = _released[i];

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }

                // 删成功、或它本来就不在了（系统清过 / 上一次删成功）：摘掉登记
                _released.RemoveAt(i);
            }
            catch (Exception ex)
            {
                // ★ 删不掉就让它**留在登记里**（下一次调用还会再试），
                //   而不是顺手清空 —— 清空等于宣布"这一份从此不管了"。
                AppLog.Warn($"临时试卷文件未能删除：{path} —— {ex.Message}");
            }
        }

        return removed;
    }
}
