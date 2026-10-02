using System.IO;
using System.Windows;
using System.Windows.Ink;

// 注意：StylusPoint / StylusPointCollection 在 System.Windows.Input，不在 System.Windows.Ink
// （Ink 命名空间只有 Stroke / DrawingAttributes / StrokeCollection）。
using System.Windows.Input;
using MathPhys.Ink.Gfx;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Ink;

/// <summary>批注加载的结果分类。</summary>
public enum AnnotationLoadStatus
{
    /// <summary>没有批注文件（新试卷的正常情况）。</summary>
    Missing,

    /// <summary>干净加载：指纹与排版都对得上。</summary>
    Loaded,

    /// <summary>加载了，但 PDF 内容与批注记录的不一致 —— 位置可能不准，需要提示用户。</summary>
    LoadedWithMismatch,

    /// <summary>拒绝加载（排版对不上 / 文件损坏 / 版本过高）。</summary>
    Rejected,
}

/// <summary>一次批注加载的结果。</summary>
public sealed class AnnotationLoadResult
{
    public AnnotationLoadStatus Status { get; init; }

    /// <summary>加载到的笔迹；<see cref="AnnotationLoadStatus.Missing"/> 与 <c>Rejected</c> 时为 <c>null</c>。</summary>
    public StrokeCollection? Strokes { get; init; }

    /// <summary>加载到的图形对象（M7.4）；没有或未加载时为 <c>null</c>。</summary>
    /// <remarks>
    /// 与 <see cref="Strokes"/> 同进同出：二者同属"这份批注"，任何"
    /// 只恢复笔迹不恢复图形"的结果都会让老师看到"图形凭空消失"。
    /// </remarks>
    public IReadOnlyList<GfxObjectData>? Objects { get; init; }

    /// <summary>给用户看的一句话说明（成功且无需说明时为空串）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>是否需要以"警告"语气展示。</summary>
    public bool IsWarning => Status is AnnotationLoadStatus.LoadedWithMismatch
                                      or AnnotationLoadStatus.Rejected;
}

/// <summary>一次批注保存的结果。</summary>
/// <param name="Ok">是否成功（含"笔迹为空因而删除文件"这种成功）。</param>
/// <param name="Written">是否真的写了盘（笔迹为空时是删除，不算写入）。</param>
/// <param name="Message">给用户看的一句话说明。</param>
public sealed record AnnotationSaveResult(bool Ok, bool Written, string Message);

/// <summary>
/// 批注的落盘与读取。
/// </summary>
/// <remarks>
/// <para>
/// <b>存储位置 = 试卷旁边的侧车文件</b>：<c>26西附全真模拟1物理试卷.pdf</c> 对应
/// <c>26西附全真模拟1物理试卷.pdf.tbink</c>。选它是因为老师要把试卷拷到一体机上用 ——
/// 侧车模式下"拷走 PDF 时把同名 .tbink 一起选上"批注就跟着走了；
/// 若集中存在程序数据目录，换台机器就找不到了。
/// </para>
/// <para>
/// 本类<b>只做文件 IO 与判定</b>，不碰任何控件：这样"加载判定"这套逻辑
/// 可以在验收 harness 里脱离界面被完整断言。
/// </para>
/// </remarks>
public sealed class AnnotationStore
{
    /// <summary>侧车文件扩展名。</summary>
    public const string Extension = ".tbink";

    /// <summary>几何比较容差（world 单位）。PDF point 是浮点数，逐页尺寸比较不能要求严格相等。</summary>
    private const double GeometryTolerance = 0.5;

    /// <summary>由 PDF 路径推出侧车文件路径。</summary>
    public static string SidecarPathFor(string pdfPath) => pdfPath + Extension;

    /// <summary>
    /// 加载指定 PDF 的批注。
    /// </summary>
    /// <param name="pdfPath">试卷 PDF 路径。</param>
    /// <param name="pageRects">当前文档各页在世界坐标中的矩形（来自 <c>WorldLayout.PageRects</c>）。</param>
    /// <param name="pageMargin">当前页外留白。</param>
    /// <param name="pageGap">当前页间距。</param>
    public AnnotationLoadResult Load(
        string pdfPath,
        IReadOnlyList<Rect> pageRects,
        double pageMargin,
        double pageGap)
    {
        if (string.IsNullOrEmpty(pdfPath))
        {
            return new AnnotationLoadResult { Status = AnnotationLoadStatus.Missing };
        }

        string path = SidecarPathFor(pdfPath);
        if (!File.Exists(path))
        {
            return new AnnotationLoadResult { Status = AnnotationLoadStatus.Missing };
        }

        TbinkManifest manifest;
        StrokeCollection strokes;
        IReadOnlyList<GfxObjectData>? objects;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!TbinkFile.TryRead(stream, out manifest, out strokes, out objects, out string error))
            {
                AppLog.Warn($"批注文件无法解析：{path} —— {error}");
                return Rejected($"批注文件无法读取：{error}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取批注失败：{path} —— {ex.Message}");
            return Rejected("读取批注失败：" + ex.Message);
        }

        return Judge(manifest, strokes, objects, pageRects, pdfPath);
    }

    /// <summary>
    /// 从内存字节装载批注（M8 工程模式：批注随 <c>.twb</c> 一起走，磁盘上没有侧车文件）。
    /// </summary>
    /// <param name="payload">批注载荷字节（<c>.twb</c> 里的 <c>annotation.tbink</c> 条目）。</param>
    /// <param name="pageRects">当前文档各页在世界坐标中的矩形。</param>
    /// <param name="pageMargin">当前页外留白（仅随批注存档，不参与判定）。</param>
    /// <param name="pageGap">当前页间距（同上）。</param>
    /// <param name="fingerprintPath">
    /// 用来算内容指纹的 PDF 路径；<c>null</c> 表示这份工程还没有 PDF（空白工程），跳过内容校验。
    /// </param>
    /// <remarks>
    /// 判定逻辑与 <see cref="Load"/> <b>完全共用</b>（见 <see cref="Judge"/>）——
    /// 工程模式与侧车模式必须对"这份批注能不能用"给出一致结论，
    /// 各写一套判定早晚会在某个边角上分叉，而分叉的后果是笔迹静默错位。
    /// </remarks>
    public AnnotationLoadResult LoadFromBytes(
        byte[]? payload,
        IReadOnlyList<Rect> pageRects,
        double pageMargin,
        double pageGap,
        string? fingerprintPath)
    {
        if (payload is null || payload.Length == 0)
        {
            // 空白工程：还没写过批注。这是正常状态，不是错误。
            return new AnnotationLoadResult { Status = AnnotationLoadStatus.Missing };
        }

        TbinkManifest manifest;
        StrokeCollection strokes;
        IReadOnlyList<GfxObjectData>? objects;

        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            if (!TbinkFile.TryRead(stream, out manifest, out strokes, out objects, out string error))
            {
                return Rejected("工程里的批注无法读取：" + error);
            }
        }
        catch (Exception ex)
        {
            return Rejected("读取工程里的批注失败：" + ex.Message);
        }

        return Judge(manifest, strokes, objects, pageRects, fingerprintPath);
    }

    /// <summary>
    /// 批注的四重判定：页数 → 逐页尺寸 → 整体偏移 → 内容指纹。
    /// </summary>
    /// <remarks>
    /// 顺序不是随意的：<b>先几何、后内容</b>。
    /// 几何对不上（页数 / 尺寸 / 非平移的整体偏移）意味着笔迹<b>根本落不回原来的字上</b>，只能拒绝；
    /// 而内容指纹不符（同名文件被换过）时几何仍然可用，所以照常装载、只提醒一句。
    /// 顺序反过来会出现"换了一份 PDF 就把整份批注丢掉"，那才是真的灾难。
    /// </remarks>
    private static AnnotationLoadResult Judge(
        TbinkManifest manifest,
        StrokeCollection strokes,
        IReadOnlyList<GfxObjectData>? objects,
        IReadOnlyList<Rect> pageRects,
        string? fingerprintPath)
    {
        // ---- 判定一：页数。对不上说明根本不是同一份文档 ----
        if (manifest.Pages.Count != pageRects.Count)
        {
            return Rejected($"批注属于另一份文档（批注 {manifest.Pages.Count} 页，"
                            + $"当前 {pageRects.Count} 页），已跳过以免笔迹错位");
        }

        // ---- 判定二：逐页尺寸。尺寸不同则页面内容布局已变，笔迹位置无从对应 ----
        for (int i = 0; i < pageRects.Count; i++)
        {
            var saved = manifest.Pages[i];
            if (Math.Abs(saved.Width - pageRects[i].Width) > GeometryTolerance
                || Math.Abs(saved.Height - pageRects[i].Height) > GeometryTolerance)
            {
                return Rejected($"批注与当前文档的第 {i + 1} 页尺寸不一致，已跳过以免笔迹错位");
            }
        }

        // ---- 判定三：整体偏移（例如页外留白常量被改过）----
        // 笔迹存的是世界坐标，页面整体平移后笔迹必须跟着平移才能仍落在原来的字上。
        // 只有"所有页的偏移完全一致"时才能用一次平移补回来；若各页偏移不同
        // （页间距变了），说明排版不是简单平移，拒绝比静默错位更负责任。
        double dx = pageRects.Count > 0 ? pageRects[0].X - manifest.Pages[0].X : 0;
        double dy = pageRects.Count > 0 ? pageRects[0].Y - manifest.Pages[0].Y : 0;
        bool shifted = Math.Abs(dx) > GeometryTolerance || Math.Abs(dy) > GeometryTolerance;

        if (shifted)
        {
            for (int i = 1; i < pageRects.Count; i++)
            {
                double pageDx = pageRects[i].X - manifest.Pages[i].X;
                double pageDy = pageRects[i].Y - manifest.Pages[i].Y;

                if (Math.Abs(pageDx - dx) > GeometryTolerance
                    || Math.Abs(pageDy - dy) > GeometryTolerance)
                {
                    return Rejected("文档排版已变化（不只是整体平移），批注无法可靠对应，已跳过");
                }
            }

            strokes = Translate(strokes, dx, dy);

            // 图形对象和笔迹同处一个世界坐标系，必须一起补 —— 只补笔迹会让
            // "老师画的角"和"老师标的度数"分家，这比整体错位更难解释。
            objects = TranslateObjects(objects, dx, dy);
        }

        // ---- 判定四：内容指纹。几何对得上但文件内容不同 ⇒ 加载但警告 ----
        // ★ 空白工程（还没有 PDF）没有可比对的指纹，直接算相符：
        //   硬判"不符"会让刚建好、刚写了几个字的工程一打开就挂黄字提醒，纯属噪音。
        if (!string.IsNullOrEmpty(fingerprintPath))
        {
            string fingerprint = DocumentFingerprint.Compute(fingerprintPath);

            bool sameDocument = !string.IsNullOrEmpty(fingerprint)
                                && string.Equals(fingerprint, manifest.SourceFingerprint,
                                                 StringComparison.OrdinalIgnoreCase);

            if (!sameDocument)
            {
                return new AnnotationLoadResult
                {
                    Status = AnnotationLoadStatus.LoadedWithMismatch,
                    Strokes = strokes,
                    Objects = objects,
                    Message = "⚠ 这份 PDF 与批注记录的不是同一份文件，批注位置可能不准",
                };
            }
        }

        return new AnnotationLoadResult
        {
            Status = AnnotationLoadStatus.Loaded,
            Strokes = strokes,
            Objects = objects,
            Message = shifted ? $"批注已恢复（含 {dx:F0},{dy:F0} 的位置补偿）" : string.Empty,
        };
    }

    /// <summary>
    /// 保存批注。
    /// </summary>
    /// <remarks>
    /// <b>写入采用「临时文件 + 原子替换」</b>：先写 <c>.tmp</c> 再整体替换目标文件。
    /// 直接覆写的话，一旦写到一半断电/崩溃，批注文件就是半截垃圾 ——
    /// 那比没有更糟，因为下次打开会解析失败，连"重来一次"的机会都没有。
    /// </remarks>
    public AnnotationSaveResult Save(
        string pdfPath,
        StrokeCollection strokes,
        IReadOnlyList<Rect> pageRects,
        double pageMargin,
        double pageGap,
        IReadOnlyList<GfxObjectData>? objects = null)
    {
        if (string.IsNullOrEmpty(pdfPath))
        {
            return new AnnotationSaveResult(false, false, "没有文档路径，无法保存批注");
        }

        string path = SidecarPathFor(pdfPath);

        // 笔迹与图形<b>都</b>为空才算"这份批注没东西"。
        // 判据漏掉图形的话，会出现"老师只放了一个量角器、没写字，一退出图形就没了"
        // 这种极难复现、也极难向老师解释的问题。
        if (strokes.Count == 0 && (objects is null || objects.Count == 0))
        {
            bool deleted = Delete(pdfPath);
            return new AnnotationSaveResult(true, false, deleted ? "批注已清空" : "无批注");
        }

        // 与工程模式（.twb 内的批注条目）共用同一段构造逻辑 ——
        // 两处各写一遍的话，"侧车存的和工程里存的不一样"这种事只有把两个文件摆在一起才能发现。
        byte[] payload = ComposeBytes(pdfPath, strokes, pageRects, pageMargin, pageGap,
                                      objects, ReadCreatedUtc(path));

        string temporary = path + ".tmp";

        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload, 0, payload.Length);
            }

            if (File.Exists(path))
            {
                // File.Replace 是原子替换：任何时刻读到的要么是完整旧版、要么是完整新版
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }

            return new AnnotationSaveResult(true, true, DescribeSave(strokes.Count, objects?.Count ?? 0));
        }
        catch (Exception ex)
        {
            // 只读目录（U 盘写保护）、磁盘满、权限不足都会走到这里。
            // 刻意不弹窗：自动保存每 2 秒可能触发一次，弹窗会变成灾难。日志 + 状态栏足够。
            SafeDeleteFile(temporary);
            AppLog.Warn($"批注保存失败：{path} —— {ex.Message}");
            return new AnnotationSaveResult(false, false, "批注保存失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把当前批注按 <c>.tbink</c> v2 协议序列化成字节（<b>不落盘</b>）。
    /// </summary>
    /// <remarks>
    /// M8 的工程模式要把批注装进 <c>.twb</c> 的 ZIP 条目里，走的正是这里。
    /// 与 <see cref="Save"/> 共用同一段构造逻辑，是刻意的：
    /// 否则"侧车存下来的"和"工程里存下来的"会因为两次改动而悄悄分叉。
    /// <para>
    /// <paramref name="pdfPath"/> 允许为 <c>null</c>（空白工程还没有 PDF）：
    /// 此时来源信息（文件名 / 长度 / 指纹）全为空，加载侧见指纹为空会跳过内容校验。
    /// </para>
    /// </remarks>
    /// <param name="createdUtc">沿用已有的创建时间；为空则取当前时间。</param>
    public static byte[] ComposeBytes(
        string? pdfPath,
        StrokeCollection strokes,
        IReadOnlyList<Rect> pageRects,
        double pageMargin,
        double pageGap,
        IReadOnlyList<GfxObjectData>? objects = null,
        string? createdUtc = null)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        string now = DateTime.UtcNow.ToString("O");
        bool hasPdf = !string.IsNullOrEmpty(pdfPath);

        var manifest = new TbinkManifest
        {
            CreatedUtc = string.IsNullOrEmpty(createdUtc) ? now : createdUtc!,
            UpdatedUtc = now,
            SourceFileName = hasPdf ? Path.GetFileName(pdfPath!) : string.Empty,
            SourceLength = hasPdf ? TryGetLength(pdfPath!) : 0,
            SourceFingerprint = hasPdf ? DocumentFingerprint.Compute(pdfPath!) : string.Empty,
            PageMargin = pageMargin,
            PageGap = pageGap,
            Pages = BuildPageRects(pageRects),
            StrokeCount = strokes.Count,
            ObjectCount = objects?.Count ?? 0,
        };

        using var buffer = new MemoryStream();
        TbinkFile.Write(buffer, manifest, strokes, objects);
        return buffer.ToArray();
    }

    /// <summary>删除某份 PDF 对应的批注文件。返回是否真的删掉了。</summary>
    public static bool Delete(string pdfPath)
    {
        if (string.IsNullOrEmpty(pdfPath)) return false;

        string path = SidecarPathFor(pdfPath);

        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"删除批注文件失败：{path} —— {ex.Message}");
            return false;
        }
    }

    /// <summary>批注文件是否存在。</summary>
    public static bool Exists(string pdfPath)
        => !string.IsNullOrEmpty(pdfPath) && File.Exists(SidecarPathFor(pdfPath));

    private static AnnotationLoadResult Rejected(string message)
        => new() { Status = AnnotationLoadStatus.Rejected, Message = message };

    /// <summary>拼一句"存了什么"的说明，让状态栏在只存了图形时也能说清楚。</summary>
    private static string DescribeSave(int strokeCount, int objectCount)
    {
        if (strokeCount > 0 && objectCount > 0) return $"批注已保存（{strokeCount} 条笔迹、{objectCount} 个图形）";
        if (objectCount > 0) return $"批注已保存（{objectCount} 个图形）";
        return $"批注已保存（{strokeCount} 条）";
    }

    /// <summary>
    /// 把所有笔迹整体平移。
    /// </summary>
    /// <remarks>
    /// 只有"页外留白常量被改过"这一种情况会走到这里。重建 <see cref="Stroke"/> 是必要的：
    /// <c>StylusPoints</c> 一经创建就不可改，位置只能靠重建。
    /// 属性（颜色/笔宽/压感）原样搬过去，所以视觉上除了位置没有任何变化。
    /// </remarks>
    private static StrokeCollection Translate(StrokeCollection source, double dx, double dy)
    {
        var moved = new StrokeCollection();

        foreach (var stroke in source)
        {
            var points = new StylusPointCollection(stroke.StylusPoints.Count);
            foreach (var point in stroke.StylusPoints)
            {
                points.Add(new StylusPoint(point.X + dx, point.Y + dy, point.PressureFactor));
            }

            moved.Add(new Stroke(points, stroke.DrawingAttributes));
        }

        return moved;
    }

    /// <summary>
    /// 把所有图形对象整体平移（与 <see cref="Translate(StrokeCollection,double,double)"/> 配套）。
    /// </summary>
    /// <remarks>
    /// 图形对象只存"中心点 + 旋转 + 缩放"，平移因此只是改中心点坐标 ——
    /// 不碰 <c>Numbers</c> / <c>Texts</c>，也就不会动到"角度是多少、长度是多少"这些语义参数。
    /// </remarks>
    private static IReadOnlyList<GfxObjectData>? TranslateObjects(
        IReadOnlyList<GfxObjectData>? source,
        double dx,
        double dy)
    {
        if (source is null || source.Count == 0) return source;

        var moved = new List<GfxObjectData>(source.Count);
        foreach (var item in source)
        {
            moved.Add(new GfxObjectData
            {
                Id = item.Id,
                Kind = item.Kind,
                Plugin = item.Plugin,
                X = item.X + dx,
                Y = item.Y + dy,
                Rotation = item.Rotation,
                Scale = item.Scale,
                Color = item.Color,
                LineWidth = item.LineWidth,
                Numbers = new Dictionary<string, double>(item.Numbers),
                Texts = new Dictionary<string, string>(item.Texts),
            });
        }

        return moved;
    }

    private static List<TbinkPageRect> BuildPageRects(IReadOnlyList<Rect> rects)
    {
        var result = new List<TbinkPageRect>(rects.Count);
        foreach (var rect in rects)
        {
            result.Add(new TbinkPageRect
            {
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height,
            });
        }

        return result;
    }

    /// <summary>读出已有批注文件的创建时间，好在重新保存时沿用。读不到返回 <c>null</c>。</summary>
    private static string? ReadCreatedUtc(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (TbinkFile.TryRead(stream, out var manifest, out _, out _, out _)
                && !string.IsNullOrEmpty(manifest.CreatedUtc))
            {
                return manifest.CreatedUtc;
            }
        }
        catch
        {
            // 旧文件读不出来无所谓，用当前时间当创建时间即可
        }

        return null;
    }

    private static long TryGetLength(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch
        {
            return 0;
        }
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
