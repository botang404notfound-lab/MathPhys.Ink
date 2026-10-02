using System.IO;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Ink;

/// <summary>工程加载的结果分类。</summary>
public enum ProjectLoadStatus
{
    /// <summary>干净加载：PDF 就位（或本来就是空白工程）、指纹对得上。</summary>
    Loaded,

    /// <summary>加载了，但找到的 PDF 与工程记录的不是同一份 —— 位置可能不准，需要提醒用户。</summary>
    LoadedWithMismatch,

    /// <summary>外挂的 PDF 找不到 —— 需要 UI 弹「定位 PDF」对话框。</summary>
    MissingPdf,

    /// <summary>拒绝加载（容器损坏 / 内容校验失败 / 版本过高 / 文件不存在）。</summary>
    Rejected,
}

/// <summary>
/// 一次工程加载的结果。
/// </summary>
/// <param name="Ok">是否成功装载（<see cref="ProjectLoadStatus.MissingPdf"/> 与 <c>Rejected</c> 为 <c>false</c>）。</param>
/// <param name="Manifest">工程元信息；完全读不出来时为 <c>null</c>。</param>
/// <param name="EmbeddedPdf">内嵌的 PDF 字节；外挂与空白工程为 <c>null</c>。</param>
/// <param name="TbinkBytes">批注字节；<b>永远非 null</b>（空白工程为空数组）。</param>
/// <param name="PdfPath">外挂模式下解析到的 PDF 路径；内嵌与空白工程为 <c>null</c>。</param>
/// <param name="HasPdf">这份工程是否带 PDF（空白工程为 <c>false</c>）。</param>
/// <param name="Message">给用户看的一句话说明（成功且无需说明时为空串）。</param>
/// <param name="Status">结果分类。</param>
/// <param name="IsWarning">是否需要以「警告」语气展示。</param>
/// <param name="Images">
/// 工程包里的位图（M22 仿真截图）；<b>永远非 null</b>（没有该段时为空数组）。
/// 与 <paramref name="TbinkBytes"/> 同一条纪律：空集合与"没有这一段"是同一件事。
/// </param>
public sealed record ProjectLoadResult(
    bool Ok,
    TwbManifest? Manifest,
    byte[]? EmbeddedPdf,
    byte[] TbinkBytes,
    string? PdfPath,
    bool HasPdf,
    string Message,
    ProjectLoadStatus Status,
    bool IsWarning,
    IReadOnlyList<TwbImageEntry> Images);

/// <summary>
/// 一次工程保存的结果。
/// </summary>
/// <param name="Ok">是否成功（含"写盘成功"这一层的结论）。</param>
/// <param name="Written">是否真的写了盘。</param>
/// <param name="Message">给用户看的一句话说明。</param>
/// <param name="SidecarTbinkDeleted">是否顺手清掉了旧的 <c>.tbink</c> 侧车文件。</param>
public sealed record ProjectSaveResult(
    bool Ok,
    bool Written,
    string Message,
    bool SidecarTbinkDeleted);

/// <summary>
/// 外挂 PDF 的定位结果。
/// </summary>
/// <param name="Ok">是否找到了文件。</param>
/// <param name="Path">找到的路径。</param>
/// <param name="FingerprintMatches">内容指纹是否与工程记录的一致。</param>
/// <param name="Message">给用户看的一句话说明。</param>
public sealed record PdfResolutionResult(
    bool Ok,
    string? Path,
    bool FingerprintMatches,
    string Message);

/// <summary>
/// 工程文件（<c>.twb</c>）的装载与保存服务 —— 宿主与 <c>.twb</c> 协议之间的<b>唯一入口</b>。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TwbFile"/> 只管"字节怎么摆"，本类管"这份工程能不能用、PDF 在哪、该不该提醒老师"。
/// 把这两层分开，是为了让判定逻辑能在 harness 里脱离界面被完整断言 ——
/// 与 <see cref="AnnotationStore"/> 的分工保持一致。
/// </para>
/// <para>
/// <b>为什么外挂模式"相对路径优先"。</b>老师的真实动线是把工程和试卷一起拷到另一个地方
/// （U 盘、共享盘、另一台一体机）。相对路径在这种情况下仍然指向正确的位置，
/// 而绝对路径必然失效。两个都记，是让"找不到时"还能靠另一条线索捞回来。
/// </para>
/// </remarks>
public sealed class ProjectStore
{
    /// <summary>
    /// 保存一个工程。
    /// </summary>
    /// <param name="twbPath">目标 <c>.twb</c> 路径。</param>
    /// <param name="manifest">工程元信息（会被 <see cref="TwbFile.Write"/> 盖版本号与时间戳）。</param>
    /// <param name="pdfBytes">内嵌模式给出 PDF 内容；外挂与空白工程传 <c>null</c>。</param>
    /// <param name="tbinkBytes">批注字节（<see cref="TbinkFile"/> 的输出）；空白工程传 <c>null</c> 或空数组。</param>
    /// <param name="sidecarPdfPathToClear">
    /// 非空时，写盘成功后顺手删掉<b>这份 PDF 旁边</b>的 <c>.tbink</c> 侧车文件。
    /// 传 <c>null</c> 表示不动任何侧车文件（空白工程、以及"打开 PDF 但不建工程"的老路径都不该动它）。
    /// </param>
    /// <remarks>
    /// <b>为什么是"传一个 PDF 路径"而不是"传一个 bool"</b>：光有 bool 说明不了该删谁。
    /// 而多一个"要删哪个 PDF 的侧车"参数，语义自洽 —— 删不掉（只读 U 盘）也不阻断，
    /// 工程已经落盘才是主要目的。
    /// </remarks>
    /// <param name="images">
    /// 位图段内容（M22 仿真截图）；<c>null</c> 或空集合表示这份工程里没有截图。
    /// <b>刻意排在 <paramref name="sidecarPdfPathToClear"/> 之后</b>：早先的调用点按位置传
    /// 第 5 个参数（PDF 路径），把新参数插在它前面会让那些调用静默绑错 —— 编译器会拦，
    /// 但那是"我改一处、别人改五处"，不如干脆不动既有位置。
    /// </param>
    public ProjectSaveResult Save(
        string twbPath,
        TwbManifest manifest,
        byte[]? pdfBytes,
        byte[]? tbinkBytes,
        string? sidecarPdfPathToClear = null,
        IReadOnlyList<TwbImageEntry>? images = null)
    {
        if (manifest is null)
        {
            return new ProjectSaveResult(false, false, "工程元信息为空，无法保存", false);
        }

        if (!TwbFile.Write(twbPath, manifest, pdfBytes, tbinkBytes, images, out string error))
        {
            return new ProjectSaveResult(false, false, error, false);
        }

        bool cleared = false;
        if (!string.IsNullOrWhiteSpace(sidecarPdfPathToClear))
        {
            cleared = DeleteSidecarTbink(sidecarPdfPathToClear!);
        }

        return new ProjectSaveResult(true, true, DescribeSave(manifest, cleared), cleared);
    }

    /// <summary>
    /// 读一个工程，并把"PDF 在哪、对不对得上"一并判完。
    /// </summary>
    /// <remarks>
    /// 判定顺序刻意是：<b>先容器、再 PDF、最后指纹</b>。
    /// 反过来的话（先查文件再判容器），一份坏工程会先弹出"找不到 PDF"，
    /// 把老师引到完全错误的方向去折腾。
    /// </remarks>
    public ProjectLoadResult Load(string twbPath)
    {
        if (!TwbFile.TryRead(twbPath, out var manifest, out var embeddedPdf, out var tbinkBytes,
                             out var images, out string readError))
        {
            // TwbFile 的每条原因都已自带上下文（"工程文件不存在""manifest.json 缺失""版本高于…"），
            // 再套一层"无法读取："只会让状态栏变成"无法读取：工程文件不存在"这种车轱辘话。
            AppLog.Warn($"工程文件无法读取：{twbPath} —— {readError}");
            return Rejected(readError);
        }

        string embedding = manifest.Pdf.Embedding ?? TwbPdfEmbedding.None;

        if (IsEmbedding(embedding, TwbPdfEmbedding.Embedded))
        {
            return new ProjectLoadResult(
                true, manifest, embeddedPdf, tbinkBytes, null, true,
                string.Empty, ProjectLoadStatus.Loaded, false, images);
        }

        if (!IsEmbedding(embedding, TwbPdfEmbedding.Referenced))
        {
            // none：空白工程。打开它只是"把工程恢复出来"，本来就没有 PDF 要开。
            return new ProjectLoadResult(
                true, manifest, null, tbinkBytes, null, false,
                string.Empty, ProjectLoadStatus.Loaded, false, images);
        }

        var resolution = ResolveReferencedPdf(manifest, Path.GetDirectoryName(twbPath));

        if (!resolution.Ok)
        {
            // Manifest 照常带回去：UI 要靠它把「定位 PDF」对话框弹出来
            return new ProjectLoadResult(
                false, manifest, null, tbinkBytes, null, false,
                resolution.Message, ProjectLoadStatus.MissingPdf, true, images);
        }

        if (!resolution.FingerprintMatches)
        {
            return new ProjectLoadResult(
                true, manifest, null, tbinkBytes, resolution.Path, true,
                "⚠ 这份 PDF 与工程记录的不是同一份文件，批注位置可能不准",
                ProjectLoadStatus.LoadedWithMismatch, true, images);
        }

        return new ProjectLoadResult(
            true, manifest, null, tbinkBytes, resolution.Path, true,
            string.Empty, ProjectLoadStatus.Loaded, false, images);
    }

    /// <summary>
    /// 定位外挂的 PDF。
    /// </summary>
    /// <param name="manifest">工程元信息。</param>
    /// <param name="projectDirectory"><c>.twb</c> 所在目录（解析相对路径的基准）。</param>
    /// <param name="userPickedPath">用户在「定位 PDF」对话框里选的路径；给了就只用它。</param>
    public PdfResolutionResult ResolveReferencedPdf(
        TwbManifest manifest,
        string? projectDirectory,
        string? userPickedPath = null)
    {
        if (manifest is null)
        {
            return new PdfResolutionResult(false, null, false, "工程元信息为空");
        }

        var pdfRef = manifest.Pdf ?? new TwbPdfRef();

        if (!string.IsNullOrWhiteSpace(userPickedPath))
        {
            return File.Exists(userPickedPath)
                ? Describe(pdfRef, userPickedPath!)
                : new PdfResolutionResult(false, null, false, "指定的 PDF 不存在：" + userPickedPath);
        }

        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(pdfRef.RelativePath))
        {
            try
            {
                string baseDir = string.IsNullOrEmpty(projectDirectory) ? string.Empty : projectDirectory;
                candidates.Add(Path.GetFullPath(Path.Combine(baseDir, pdfRef.RelativePath!)));
            }
            catch (Exception ex)
            {
                // 别的程序写进来的怪路径（含非法字符等）不该让整个打开流程崩掉
                AppLog.Warn($"工程里的相对路径无法解析：{pdfRef.RelativePath} —— {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(pdfRef.AbsolutePath))
        {
            candidates.Add(pdfRef.AbsolutePath!);
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Describe(pdfRef, candidate);
            }
        }

        string shown = candidates.Count > 0
            ? string.Join(" 或 ", candidates)
            : "（工程里没有记录路径）";

        return new PdfResolutionResult(false, null, false, "找不到工程引用的 PDF：" + shown);
    }

    /// <summary>
    /// 删掉某份 PDF 旁边的 <c>.tbink</c> 侧车文件；不存在或删不掉时返回 <c>false</c>（不抛异常）。
    /// </summary>
    /// <remarks>
    /// 写成"删除"而不是"重命名/备份"：工程模式之下批注已经进了 <c>.twb</c>，
    /// 留一个内容不同的 <c>.tbink</c> 在旁边，下次"打开这份 PDF"时两边会挑一个 ——
    /// 那种"看起来少了几笔"的诡异现场，比少一个备份文件难解释得多。
    /// </remarks>
    public static bool DeleteSidecarTbink(string pdfPath)
        => AnnotationStore.Delete(pdfPath);

    // ==================================================================== 内部

    private static ProjectLoadResult Rejected(string message)
        => new(false, null, null, Array.Empty<byte>(), null, false, message,
               ProjectLoadStatus.Rejected, true, Array.Empty<TwbImageEntry>());

    private static bool IsEmbedding(string embedding, string expected)
        => string.Equals(embedding, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>找到 PDF 之后：算指纹、给出结论。</summary>
    private static PdfResolutionResult Describe(TwbPdfRef pdfRef, string path)
    {
        string actual = DocumentFingerprint.Compute(path);
        string saved = pdfRef.Fingerprint ?? string.Empty;

        // 与 AnnotationStore 同一判据：算不出指纹时也当"对不上"。
        // 宁可多说一句提醒，也不要让老师以为"批注位置一定准"。
        bool match = !string.IsNullOrEmpty(actual)
                     && string.Equals(actual, saved, StringComparison.OrdinalIgnoreCase);

        return new PdfResolutionResult(
            true, path, match,
            match ? string.Empty : "⚠ 这份 PDF 与工程记录的不是同一份文件，批注位置可能不准");
    }

    private static string DescribeSave(TwbManifest manifest, bool sidecarCleared)
    {
        string embedding = manifest.Pdf?.Embedding ?? TwbPdfEmbedding.None;

        string what = IsEmbedding(embedding, TwbPdfEmbedding.Embedded) ? "（内嵌 PDF 与批注）"
            : IsEmbedding(embedding, TwbPdfEmbedding.Referenced) ? "（外挂 PDF，未含卷面）"
            : "（空白工程）";

        return "工程已保存" + what + (sidecarCleared ? "，并已清理旧的批注侧车文件" : string.Empty);
    }
}
