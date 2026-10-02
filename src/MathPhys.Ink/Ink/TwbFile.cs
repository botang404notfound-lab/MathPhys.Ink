using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Ink;

/// <summary>
/// 工程文件（<c>.twb</c>）的容器格式编解码。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是 ZIP。</b>工程 = PDF + 笔迹 + 图形 + 视图状态，其中 PDF 动辄几 MB 到几十 MB。
/// 装进 ZIP 之后还是<b>一个文件</b>：老师拷到一体机上，拷一个 .twb 就齐了 ——
/// 这正是 M6 选"PDF 同目录侧车"的同一个理由，只是把"两个文件"进一步收到"一个文件"。
/// ZIP 本身是行业标准（docx / xlsx 都是），资源管理器双击能看，出问题用现成工具就能拆开查。
/// </para>
/// <para><b>包内布局（固定三个条目）：</b></para>
/// <code>
/// manifest.json      工程元信息（同时也是"这是 .twb 工程"的凭证）
/// document.pdf       仅 embedded 模式存在；原样存放（PDF 早已是压缩格式，再 Deflate 只是白烧 CPU）
/// annotation.tbink   复用 v2 协议（含笔迹与图形）；空工程时<b>不写这个条目</b>
/// </code>
/// <para>
/// 也就是说：<c>referenced</c>（外挂）与 <c>none</c>（空白工程）模式下，包内只有
/// <c>manifest.json</c> 加（可能有的）<c>annotation.tbink</c>，通常只有几十 KB ——
/// 这正是"外挂"这个选项存在的意义：试卷放在学校共享盘上不搬，工程文件轻巧可随身带。
/// </para>
/// <para>
/// <c>manifest.json</c> 里还带着各段载荷的 SHA-256（见 <see cref="TwbIntegrity"/>）：
/// ZIP 自身的 CRC 在<b>读 stored 条目时不会被校验</b>，只有自己的校验值才能保证
/// "坏内容绝不静默流出去"。
/// </para>
/// <para>
/// <b>为什么内嵌 PDF 不压缩、manifest 压缩。</b>manifest 是可读文本，Deflate 能压到 1/3，
/// 而且几乎所有阅读场景都要先读它；PDF 内部已是 JPEG/Flate 流，二次压缩省不下几个字节，
/// 却让写入与读取都多走一遍全量 CPU —— 一份 30 MB 的扫描卷面，代价很实在。
/// </para>
/// <para>
/// 读取一律走 <see cref="TryRead(string,out TwbManifest,out byte[]?,out byte[],out string)"/> 而不是抛异常：
/// 工程文件可能被外部工具改坏、可能来自更高版本的程序、也可能被当成压缩包解开改过里面 ——
/// 这些都属于<b>预期内的正常情况</b>，应当优雅拒绝并说清原因，而不是把程序崩在打开对话框后面。
/// </para>
/// </remarks>
public static class TwbFile
{
    /// <summary>工程文件扩展名。</summary>
    public const string Extension = ".twb";

    /// <summary>
    /// 当前程序写出的格式版本。
    /// </summary>
    /// <remarks>
    /// <b>v1</b>（M8）：<c>manifest.json + document.pdf? + annotation.tbink?</c>。
    /// 将来若要加"多份 PDF""板书时序"等新段，靠这个版本号分支读；
    /// 读到<b>比自己高</b>的版本一律拒绝并提示升级 —— 硬着头皮读只会静默丢数据。
    /// </remarks>
    public const int CurrentSchemaVersion = 1;

    /// <summary>元信息条目名。</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>内嵌 PDF 条目名。</summary>
    public const string PdfEntryName = "document.pdf";

    /// <summary>批注（笔迹 + 图形）条目名。</summary>
    public const string AnnotationEntryName = "annotation.tbink";

    /// <summary>位图段的目录前缀（M22 S4b）。</summary>
    public const string ImagesFolder = "images";

    /// <summary>位图段的扩展名。</summary>
    public const string ImageExtension = ".png";

    /// <summary>位图 Id 的长度上限（读侧照它拒绝怪名字）。</summary>
    public const int MaxImageIdLength = 64;

    /// <summary>由位图 Id 推出包内条目名（写侧与读侧共用这一处，避免两边算得不一样）。</summary>
    public static string ImageEntryName(string id)
        => ImagesFolder + "/" + id + ImageExtension;

    /// <summary>
    /// JSON 选项。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="TbinkFile"/> 同款：<see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
    /// 让中文标题在文件里就是中文（不是一堆 <c>\u897f</c>），<see cref="JsonNamingPolicy.CamelCase"/>
    /// 让 JSON 保持常见的小驼峰写法；反序列化放宽大小写，别人手改过也能读回来。
    /// 这里写出的文本不参与任何 HTML/JS 上下文，放宽转义没有安全代价。
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    // ==================================================================== 写

    /// <summary>
    /// 写出一个工程文件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>写入采用「临时文件 + 原子替换」</b>，与 <see cref="AnnotationStore.Save"/> 同一套路。
    /// 直觉上"ZIP 库自己会收尾"似乎够了，其实不够：<c>FileMode.Create</c> 会先把原文件<b>截断</b>，
    /// 一旦写到一半断电或崩溃，老师丢掉的不只是这次改动 —— 连上一次保存的完整工程也没了。
    /// 临时文件 + <see cref="File.Replace(string,string,string?)"/> 保证任何时刻读到的
    /// 要么是完整旧版、要么是完整新版。
    /// </para>
    /// <para>
    /// 本方法会<b>就地修改</b>传入的 <paramref name="manifest"/>（盖版本号与时间戳、补创建时间），
    /// 与 <see cref="TbinkFile.Write"/> 的行为一致 —— 好处是调用方不必记得自己同步这几处。
    /// </para>
    /// </remarks>
    /// <param name="twbPath">目标路径。</param>
    /// <param name="manifest">工程元信息；会被盖上版本号与更新时间。</param>
    /// <param name="pdfBytes">内嵌模式必须给出 PDF 内容；外挂模式与空白工程忽略（传 <c>null</c>）。</param>
    /// <param name="tbinkBytes">批注字节（<see cref="TbinkFile"/> 的输出）；空数组或 <c>null</c> 表示不写该条目。</param>
    /// <param name="error">失败原因（成功时为空串）。</param>
    public static bool Write(
        string twbPath,
        TwbManifest manifest,
        byte[]? pdfBytes,
        byte[]? tbinkBytes,
        out string error)
        => Write(twbPath, manifest, pdfBytes, tbinkBytes, null, out error);

    /// <summary>
    /// 写出一个工程文件（含位图段）。
    /// </summary>
    /// <param name="images">
    /// 位图段内容（M22 仿真截图）；<c>null</c> 或空数组表示不写这个段。
    /// 与 <c>annotation.tbink</c> 同一条纪律：<b>没有就不写条目</b>，
    /// 包内结构更好读，也不会留一堆 0 字节条目。
    /// </param>
    /// <remarks>
    /// 位图以 <c>stored</c>（不压缩）方式存：PNG 内部已是 Deflate 流，二次压缩省不下几个字节，
    /// 却让保存与打开都多走一遍全量 CPU —— 一帧截图约 100~400 KB，与内嵌 PDF 是同一个理由。
    /// </remarks>
    public static bool Write(
        string twbPath,
        TwbManifest manifest,
        byte[]? pdfBytes,
        byte[]? tbinkBytes,
        IReadOnlyList<TwbImageEntry>? images,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrEmpty(twbPath))
        {
            error = "没有工程文件路径，无法保存";
            return false;
        }

        if (manifest is null)
        {
            error = "工程元信息为空，无法保存";
            return false;
        }

        string temporary = twbPath + ".tmp";
        string failure = string.Empty;

        try
        {
            bool written;

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                written = Write(stream, manifest, pdfBytes, tbinkBytes, images, out string innerError);
                if (!written) failure = innerError;
            }

            // ★ 替换必须发生在 using 之外、也就是流<b>关闭之后</b>：
            //   自己还开着句柄就去 File.Replace/File.Move，Windows 会直接拒绝
            //   （The process cannot access the file because it is being used by another process）。
            if (written)
            {
                if (File.Exists(twbPath))
                {
                    // 原子替换：任何时刻读到的要么是完整旧版、要么是完整新版
                    File.Replace(temporary, twbPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporary, twbPath);
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            // 只读目录（写保护的 U 盘）、磁盘满、文件被别的程序独占都会走到这里。
            AppLog.Warn($"工程文件保存失败：{twbPath} —— {ex.Message}");
            failure = "工程文件保存失败：" + ex.Message;
        }

        // 失败收尾：临时文件必须**等 using 把流关掉之后**再删 —— 顺序颠倒的话，
        // Windows 上文件仍被占用，Delete 会失败，老师会在工程旁边多出一个 0 字节的
        // .tmp 残骸（第一次实现就踩了这一下，harness 里留下了 0 字节的 .tmp）。
        SafeDeleteFile(temporary);
        error = failure;
        return false;
    }

    /// <summary>
    /// 把工程写进一个流（不落盘）。
    /// </summary>
    /// <remarks>
    /// 存在的理由有两条：一是验收 harness 要能造各种"损坏/异常"的工程文件；
    /// 二是将来若要"导出到网络位置"，路径重载就不够用了。语义与路径重载完全一致。
    /// </remarks>
    public static bool Write(
        Stream stream,
        TwbManifest manifest,
        byte[]? pdfBytes,
        byte[]? tbinkBytes,
        out string error)
        => Write(stream, manifest, pdfBytes, tbinkBytes, null, out error);

    /// <summary>把工程写进一个流（含位图段）。语义与路径重载完全一致。</summary>
    public static bool Write(
        Stream stream,
        TwbManifest manifest,
        byte[]? pdfBytes,
        byte[]? tbinkBytes,
        IReadOnlyList<TwbImageEntry>? images,
        out string error)
    {
        error = string.Empty;

        if (stream is null || !stream.CanWrite)
        {
            error = "输出流不可写，无法保存工程";
            return false;
        }

        if (manifest is null)
        {
            error = "工程元信息为空，无法保存";
            return false;
        }

        string embedding = manifest.Pdf?.Embedding ?? TwbPdfEmbedding.Embedded;

        if (!IsKnownEmbedding(embedding))
        {
            error = $"无法识别的 PDF 绑定方式：{embedding}";
            return false;
        }

        // 说好内嵌却没给 PDF 内容 —— 这是调用方的编程错误，写出去会得到一个
        // "打开时才报损坏"的工程文件，宁可在这一刻就拦住。
        if (embedding == TwbPdfEmbedding.Embedded && (pdfBytes is null || pdfBytes.Length == 0))
        {
            error = "内嵌模式必须提供 PDF 内容";
            return false;
        }

        // 盖版本号与时间戳（创建时间只在空缺时补，便于保留"这个工程是什么时候建的"）
        manifest.SchemaVersion = CurrentSchemaVersion;
        if (string.IsNullOrEmpty(manifest.CreatedUtc)) manifest.CreatedUtc = DateTime.UtcNow.ToString("O");
        manifest.UpdatedUtc = DateTime.UtcNow.ToString("O");

        // 留下"保存时是什么样"的凭证：ZIP 库读 stored 条目不校验 CRC（见 TwbIntegrity 的说明），
        // 没有这一段，包内字节被改坏后我们会静默把坏内容交出去。
        manifest.Integrity = new TwbIntegrity
        {
            PdfSha256 = embedding == TwbPdfEmbedding.Embedded ? Sha256Hex(pdfBytes!) : string.Empty,
            AnnotationSha256 = tbinkBytes is { Length: > 0 } ? Sha256Hex(tbinkBytes) : string.Empty,
            ImageSha256 = BuildImageIntegrity(images),
        };

        // 条数是「诊断用」的旁证（真值永远以载荷为准），顺手盖一下 ——
        // 调用方漏填时，manifest 里至少不会写着「0 张图」却躺着 3 个 images/ 条目。
        if (manifest.Stats is not null) manifest.Stats.ImageCount = images?.Count ?? 0;

        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

            WriteEntry(zip, ManifestEntryName, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
                       CompressionLevel.Optimal);

            if (embedding == TwbPdfEmbedding.Embedded)
            {
                WriteEntry(zip, PdfEntryName, pdfBytes!, CompressionLevel.NoCompression);
            }

            // 空工程不写 annotation.tbink：读侧把"条目不存在"与"条目为空"同样当作空批注，
            // 少一个 0 字节条目，包内结构也更好读。
            if (tbinkBytes is { Length: > 0 })
            {
                WriteEntry(zip, AnnotationEntryName, tbinkBytes, CompressionLevel.Optimal);
            }

            if (images is not null)
            {
                foreach (var image in images)
                {
                    // 名字不合法或没内容的直接跳过：宁可少一张图，也不写一个读不回来的条目
                    if (image is null || !IsSafeImageId(image.Id)) continue;
                    if (image.Bytes is not { Length: > 0 }) continue;

                    WriteEntry(zip, ImageEntryName(image.Id), image.Bytes, CompressionLevel.NoCompression);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"工程文件写入失败：{ex.Message}");
            error = "工程文件写入失败：" + ex.Message;
            return false;
        }
    }

    // ==================================================================== 读

    /// <summary>
    /// 读一个工程文件。失败时返回 <c>false</c> 并给出可读原因（不抛异常）。
    /// </summary>
    /// <param name="twbPath">工程文件路径。</param>
    /// <param name="manifest">读到的元信息。</param>
    /// <param name="embeddedPdf">内嵌的 PDF 字节；外挂模式为 <c>null</c>。</param>
    /// <param name="tbinkBytes">批注字节；工程内没有该条目时为空数组（<b>永不</b>为 <c>null</c>）。</param>
    /// <param name="error">失败原因（成功时为空串）。</param>
    public static bool TryRead(
        string twbPath,
        out TwbManifest manifest,
        out byte[]? embeddedPdf,
        out byte[] tbinkBytes,
        out string error)
        => TryRead(twbPath, out manifest, out embeddedPdf, out tbinkBytes, out _, out error);

    /// <summary>读一个工程文件（含位图段）。</summary>
    /// <param name="images">
    /// 包内 <c>images/</c> 段的内容；工程里没有该段时为空数组（<b>永不</b>为 <c>null</c>）。
    /// </param>
    public static bool TryRead(
        string twbPath,
        out TwbManifest manifest,
        out byte[]? embeddedPdf,
        out byte[] tbinkBytes,
        out IReadOnlyList<TwbImageEntry> images,
        out string error)
    {
        manifest = null!;
        embeddedPdf = null;
        tbinkBytes = Array.Empty<byte>();
        images = Array.Empty<TwbImageEntry>();
        error = string.Empty;

        if (string.IsNullOrEmpty(twbPath))
        {
            error = "没有工程文件路径，无法打开";
            return false;
        }

        if (!File.Exists(twbPath))
        {
            error = "工程文件不存在";
            return false;
        }

        try
        {
            // FileShare.Read：允许别人同时读（例如资源管理器里预览），但禁止写入者插手
            using var stream = new FileStream(twbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryRead(stream, out manifest, out embeddedPdf, out tbinkBytes, out images, out error);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开工程文件失败：{twbPath} —— {ex.Message}");
            error = "打开工程文件失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 从一个流里读工程（不落盘）。语义与路径重载一致。
    /// </summary>
    public static bool TryRead(
        Stream stream,
        out TwbManifest manifest,
        out byte[]? embeddedPdf,
        out byte[] tbinkBytes,
        out string error)
        => TryRead(stream, out manifest, out embeddedPdf, out tbinkBytes, out _, out error);

    /// <summary>从一个流里读工程（含位图段）。语义与路径重载一致。</summary>
    public static bool TryRead(
        Stream stream,
        out TwbManifest manifest,
        out byte[]? embeddedPdf,
        out byte[] tbinkBytes,
        out IReadOnlyList<TwbImageEntry> images,
        out string error)
    {
        manifest = null!;
        embeddedPdf = null;
        tbinkBytes = Array.Empty<byte>();
        images = Array.Empty<TwbImageEntry>();
        error = string.Empty;

        if (stream is null || !stream.CanRead)
        {
            error = "输入流不可读，无法打开工程";
            return false;
        }

        ZipArchive zip;

        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            // 最常见的两种来源：文件根本不是 zip（比如误选了别的文件），
            // 或传输中断导致中央目录丢失（拷到一半拔了 U 盘）。
            error = "这不是有效的工程文件（压缩容器已损坏，或所选文件不是 .twb）";
            return false;
        }
        catch (Exception ex)
        {
            error = "读取工程文件失败：" + ex.Message;
            return false;
        }

        using (zip)
        {
            if (!TryReadManifestEntry(zip, out manifest, out error)) return false;

            // 说好内嵌就必须真有 PDF：manifest 与包内结构自相矛盾时，
            // 继续往下走只会得到一个"打开后白屏"的工程，不如在这里就退回去。
            string embedding = manifest.Pdf.Embedding ?? TwbPdfEmbedding.None;
            if (!IsKnownEmbedding(embedding))
            {
                error = $"无法识别的 PDF 绑定方式：{embedding}（工程文件可能来自更新的程序）";
                return false;
            }

            try
            {
                if (string.Equals(embedding, TwbPdfEmbedding.Embedded, StringComparison.OrdinalIgnoreCase))
                {
                    var pdfEntry = FindEntry(zip, PdfEntryName);
                    if (pdfEntry is null)
                    {
                        error = "工程标记为内嵌 PDF，但包内找不到 document.pdf（文件可能已损坏）";
                        return false;
                    }

                    embeddedPdf = ReadAllBytes(pdfEntry);
                    if (embeddedPdf.Length == 0)
                    {
                        error = "工程内嵌的 PDF 内容为空（文件可能已损坏）";
                        embeddedPdf = null;
                        return false;
                    }
                }
                else if (string.Equals(embedding, TwbPdfEmbedding.Referenced, StringComparison.OrdinalIgnoreCase)
                         && string.IsNullOrWhiteSpace(manifest.Pdf.RelativePath)
                         && string.IsNullOrWhiteSpace(manifest.Pdf.AbsolutePath))
                {
                    error = "工程标记为外挂 PDF，但没有记录 PDF 路径（文件可能已损坏）";
                    return false;
                }
                // None：空白工程，本来就没有 PDF，不校验也不读载荷

                var annotationEntry = FindEntry(zip, AnnotationEntryName);
                if (annotationEntry is not null)
                {
                    tbinkBytes = ReadAllBytes(annotationEntry);
                }

                // 位图段（M22 S4b）：与批注一样，条目不存在就等于"没有"，不是错误
                images = ReadImages(zip);
            }
            catch (InvalidDataException ex)
            {
                // CRC 不符：包内某一段字节被改坏过。这类损坏必须报出来 ——
                // 悄悄跳过坏段会让老师以为"批注莫名其妙少了几笔"。
                AppLog.Warn($"工程文件内容校验失败：{ex.Message}");
                error = "工程文件内容已损坏（数据校验失败）：" + ex.Message;
                embeddedPdf = null;
                tbinkBytes = Array.Empty<byte>();
                return false;
            }
            catch (Exception ex)
            {
                error = "读取工程文件内容失败：" + ex.Message;
                embeddedPdf = null;
                tbinkBytes = Array.Empty<byte>();
                return false;
            }

            // 最后一道：拿保存时留下的 SHA-256 比对。ZIP 层对 stored 条目（内嵌 PDF）不做校验，
            // 这一段是"坏内容绝不静默流出去"的唯一依靠。
            if (!VerifyIntegrity(manifest, embeddedPdf, tbinkBytes, images, out error))
            {
                embeddedPdf = null;
                tbinkBytes = Array.Empty<byte>();
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 只读元信息，不碰 PDF 载荷。
    /// </summary>
    /// <remarks>
    /// 外挂模式下光看 <c>manifest.json</c> 就知道去哪找 PDF，没必要先把几十 MB 的内嵌 PDF
    /// 读进内存再丢掉；打开工程前的"这是不是我的工程"预检也只用到元信息。
    /// </remarks>
    public static bool TryReadManifest(string twbPath, out TwbManifest manifest, out string error)
    {
        manifest = null!;
        error = string.Empty;

        if (string.IsNullOrEmpty(twbPath) || !File.Exists(twbPath))
        {
            error = "工程文件不存在";
            return false;
        }

        try
        {
            using var stream = new FileStream(twbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            return TryReadManifestEntry(zip, out manifest, out error);
        }
        catch (InvalidDataException)
        {
            error = "这不是有效的工程文件（压缩容器已损坏，或所选文件不是 .twb）";
            return false;
        }
        catch (Exception ex)
        {
            error = "读取工程元信息失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 把包内某个条目原样抽到目标流（例如把内嵌 PDF 落到临时文件）。
    /// </summary>
    /// <remarks>
    /// 走流而不是"返回 byte[]"：一份 30 MB 的扫描卷面，落盘时没必要在托管堆上再留一份同样大的副本
    /// —— 一体机的内存要留给渲染缓存。
    /// </remarks>
    public static bool ExtractEntry(string twbPath, string entryName, Stream destination, out string error)
    {
        error = string.Empty;

        if (destination is null || !destination.CanWrite)
        {
            error = "目标流不可写，无法导出工程内文件";
            return false;
        }

        try
        {
            using var stream = new FileStream(twbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

            var entry = FindEntry(zip, entryName);
            if (entry is null)
            {
                error = $"工程文件里没有 {entryName}（文件可能已损坏）";
                return false;
            }

            using var source = entry.Open();
            source.CopyTo(destination);
            return true;
        }
        catch (InvalidDataException ex)
        {
            error = "工程文件内容已损坏（数据校验失败）：" + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = "导出工程内文件失败：" + ex.Message;
            return false;
        }
    }

    // ==================================================================== 内部

    /// <summary>是不是我们认识的 PDF 绑定方式。</summary>
    /// <remarks>
    /// 写侧与读侧共用同一个判据：两处各写一遍"合法的取值有哪些"，
    /// 早晚会出现"写得出去、读不回来"（或反过来）的鬼故事。
    /// </remarks>
    private static bool IsKnownEmbedding(string embedding)
        => string.Equals(embedding, TwbPdfEmbedding.Embedded, StringComparison.OrdinalIgnoreCase)
        || string.Equals(embedding, TwbPdfEmbedding.Referenced, StringComparison.OrdinalIgnoreCase)
        || string.Equals(embedding, TwbPdfEmbedding.None, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 比对保存时留下的 SHA-256。
    /// </summary>
    /// <remarks>
    /// 校验值缺席（旧文件、手改过的 manifest）时<b>跳过</b>而不是判损坏：
    /// 它是增强手段，不是格式的必需品。判据"该验才验"，才不会把好文件拦在门外。
    /// </remarks>
    private static bool VerifyIntegrity(
        TwbManifest manifest,
        byte[]? embeddedPdf,
        byte[] tbinkBytes,
        IReadOnlyList<TwbImageEntry> images,
        out string error)
    {
        error = string.Empty;

        var integrity = manifest.Integrity;
        if (integrity is null) return true;

        if (embeddedPdf is not null && !string.IsNullOrEmpty(integrity.PdfSha256)
            && !string.Equals(Sha256Hex(embeddedPdf), integrity.PdfSha256, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn("工程文件里的内嵌 PDF 校验值不符");
            error = "工程文件内容已损坏（内嵌 PDF 与保存时的校验值不符），请从备份重新获取这份工程";
            return false;
        }

        if (tbinkBytes.Length > 0 && !string.IsNullOrEmpty(integrity.AnnotationSha256)
            && !string.Equals(Sha256Hex(tbinkBytes), integrity.AnnotationSha256, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn("工程文件里的批注校验值不符");
            error = "工程文件内容已损坏（批注与保存时的校验值不符），请从备份重新获取这份工程";
            return false;
        }

        // 位图段与另外两段同一条纪律：坏内容绝不静默流出去。校验值缺席（旧文件、手改过的
        // manifest）时跳过 —— 它是增强，不是格式的必需品；但一旦写了，就必须对得上。
        if (images.Count > 0 && integrity.ImageSha256 is { Count: > 0 })
        {
            foreach (var image in images)
            {
                string entryName = ImageEntryName(image.Id);

                if (!integrity.ImageSha256.TryGetValue(entryName, out string? expected)) continue;
                if (string.IsNullOrEmpty(expected)) continue;

                if (!string.Equals(Sha256Hex(image.Bytes), expected, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Warn($"工程文件里的位图校验值不符：{entryName}");
                    error = $"工程文件内容已损坏（位图 {image.Id} 与保存时的校验值不符），请从备份重新获取这份工程";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 位图 Id 是不是「能安全当文件名用」。
    /// </summary>
    /// <remarks>
    /// 读侧<b>必须</b>查这一条：Id 是从包内条目名反推出来的，而包可以被任何工具改过。
    /// 一个含 <c>/</c> 或 <c>..</c> 的 Id 会在别处被拼成路径 —— 那就是一个目录穿越。
    /// 我们自己发的 Id 全是 12 位十六进制，所以「白名单到 <c>[0-9A-Za-z_-]</c>」零损失。
    /// </remarks>
    private static bool IsSafeImageId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id!.Length > MaxImageIdLength) return false;

        foreach (char ch in id)
        {
            bool ok = ch is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or '-';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>算位图段的校验值表（包内条目名 → SHA-256）。</summary>
    private static Dictionary<string, string> BuildImageIntegrity(IReadOnlyList<TwbImageEntry>? images)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        if (images is null) return map;

        foreach (var image in images)
        {
            if (image is null || !IsSafeImageId(image.Id)) continue;
            if (image.Bytes is not { Length: > 0 }) continue;

            map[ImageEntryName(image.Id)] = Sha256Hex(image.Bytes);
        }

        return map;
    }

    /// <summary>
    /// 把包内 <c>images/</c> 段整个读出来。
    /// </summary>
    /// <remarks>
    /// Id 从<b>条目名</b>反推，不去读 manifest 里的记录 —— 条目名才是「字节真的在这儿」的证据。
    /// 读不出来的条目（名字怪、空内容）逐条跳过并记日志：一张图坏了不该让整份板书打不开，
    /// 与「对象参数坏掉只降级这一个对象」是同一条取舍。
    /// </remarks>
    private static List<TwbImageEntry> ReadImages(ZipArchive zip)
    {
        var list = new List<TwbImageEntry>();

        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(ImagesFolder + "/", StringComparison.OrdinalIgnoreCase)) continue;

            string file = name[(ImagesFolder.Length + 1)..];
            if (file.IndexOf('/') >= 0) continue;   // 子目录：不接受
            if (!file.EndsWith(ImageExtension, StringComparison.OrdinalIgnoreCase)) continue;

            string id = file[..^ImageExtension.Length];
            if (!IsSafeImageId(id))
            {
                AppLog.Warn($"工程里的位图名字异常，已跳过：{name}");
                continue;
            }

            byte[] bytes = ReadAllBytes(entry);
            if (bytes.Length == 0) continue;

            list.Add(new TwbImageEntry { Id = id, Bytes = bytes });
        }

        return list;
    }

    /// <summary>算一段字节的 SHA-256（十六进制小写）。</summary>
    private static string Sha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>读 <c>manifest.json</c> 并做"是不是我们的工程"的全部判定。</summary>
    private static bool TryReadManifestEntry(ZipArchive zip, out TwbManifest manifest, out string error)
    {
        manifest = null!;
        error = string.Empty;

        var entry = FindEntry(zip, ManifestEntryName);
        if (entry is null)
        {
            // 只认 PK 头是不够的：任何一个 zip 都有 PK 头。真正的凭证是"包里有 manifest.json"。
            error = "工程文件里没有 manifest.json（所选文件不是 .twb 工程，或文件已损坏）";
            return false;
        }

        TwbManifest? parsed;

        try
        {
            byte[] json = ReadAllBytes(entry);
            parsed = JsonSerializer.Deserialize<TwbManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = "工程元信息解析失败（文件可能已损坏）：" + ex.Message;
            return false;
        }
        catch (InvalidDataException ex)
        {
            error = "工程元信息已损坏（数据校验失败）：" + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = "读取工程元信息失败：" + ex.Message;
            return false;
        }

        if (parsed is null)
        {
            error = "工程元信息为空";
            return false;
        }

        if (parsed.SchemaVersion <= 0)
        {
            error = "工程元信息缺少有效的格式版本（文件可能已损坏）";
            return false;
        }

        if (parsed.SchemaVersion > CurrentSchemaVersion)
        {
            error = $"工程文件版本 {parsed.SchemaVersion} 高于本程序支持的 {CurrentSchemaVersion}，请升级程序后再打开";
            return false;
        }

        // 手改过的 JSON 可能把整段写成 null；补上默认值比抛 NullReferenceException 体面得多
        parsed.Pdf ??= new TwbPdfRef();
        parsed.View ??= new TwbViewState();
        parsed.Stats ??= new TwbStats();
        parsed.Integrity ??= new TwbIntegrity();
        parsed.Title ??= string.Empty;

        manifest = parsed;
        return true;
    }

    /// <summary>按名找条目，忽略大小写（外部工具重新打包时常会改掉大小写）。</summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name)
    {
        foreach (var entry in zip.Entries)
        {
            if (string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private static byte[] ReadAllBytes(ZipArchiveEntry entry)
    {
        using var source = entry.Open();

        // 预分配：stored 条目的 Length 是准的，省掉几次扩容拷贝
        using var buffer = entry.Length is > 0 and < int.MaxValue
            ? new MemoryStream((int)entry.Length)
            : new MemoryStream();

        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理临时文件失败不值得打断流程，下次写入会覆盖它
        }
    }
}
