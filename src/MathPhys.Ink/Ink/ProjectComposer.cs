using System.IO;
using System.Windows;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Ink;

/// <summary>
/// 工程文件的<b>纯逻辑</b>组装：从"当前编辑器里的事实"（PDF 路径、页矩形、视口、条数）
/// 拼出一份可写盘的 <see cref="TwbManifest"/>。
/// </summary>
/// <remarks>
/// <para>
/// 刻意做成<b>无副作用的静态函数</b>而不是塞进 <c>MainWindow</c>：
/// 保存工程这条路上能出错的地方几乎全在这里（绑定方式判错、指纹写错、相对路径算错），
/// 而这些恰好是 harness 能完整断言的部分 —— 放进窗口只会变成"只能靠真机点按钮碰运气"。
/// </para>
/// <para>
/// 本类<b>不碰磁盘上的 .twb</b>（那是 <see cref="TwbFile"/> 的事），也不判定装载
/// （那是 <see cref="ProjectStore"/> 的事）。
/// </para>
/// </remarks>
public static class ProjectComposer
{
    /// <summary>
    /// 默认标题 = PDF 文件名去掉扩展名；没有 PDF 时给一个中性名字。
    /// </summary>
    /// <remarks>
    /// 老师保存工程时最想要的名字就是试卷的名字，默认填好能省一步输入；
    /// 而"未命名工程"这种占位名会让人每次都得手动改掉。
    /// </remarks>
    public static string DefaultTitle(string? pdfPath)
        => string.IsNullOrEmpty(pdfPath) ? "未命名工程" : Path.GetFileNameWithoutExtension(pdfPath);

    /// <summary>
    /// 拼一份保存用的 manifest。
    /// </summary>
    /// <param name="title">工程标题。</param>
    /// <param name="projectPath">目标 <c>.twb</c> 路径（算相对路径的基准；为 <c>null</c> 时只存绝对路径）。</param>
    /// <param name="pdfPath">当前 PDF 路径；<c>null</c> 表示这份工程还没有试卷（空白工程）。</param>
    /// <param name="embedPdf"><c>true</c>=把 PDF 字节装进工程包；<c>false</c>=只记路径。</param>
    /// <param name="view">当前视图状态。</param>
    /// <param name="pageIndex">当前页（0 起；仅用于诊断，装载复原靠 <paramref name="view"/>）。</param>
    /// <param name="strokeCount">笔迹条数（仅诊断）。</param>
    /// <param name="objectCount">图形个数（仅诊断）。</param>
    /// <param name="createdUtc">沿用原工程的创建时间；为空则用当前时间。</param>
    public static TwbManifest BuildManifest(
        string title,
        string? projectPath,
        string? pdfPath,
        bool embedPdf,
        TwbViewState view,
        int pageIndex,
        int strokeCount,
        int objectCount,
        string? createdUtc = null)
    {
        bool hasPdf = !string.IsNullOrEmpty(pdfPath);

        string embedding = !hasPdf ? TwbPdfEmbedding.None
            : embedPdf ? TwbPdfEmbedding.Embedded
            : TwbPdfEmbedding.Referenced;

        string now = DateTime.UtcNow.ToString("O");

        var manifest = new TwbManifest
        {
            SchemaVersion = TwbFile.CurrentSchemaVersion,
            Title = title ?? string.Empty,
            CreatedUtc = string.IsNullOrEmpty(createdUtc) ? now : createdUtc!,
            UpdatedUtc = now,
            Pdf = new TwbPdfRef
            {
                Embedding = embedding,
                // 内嵌模式不需要路径，但**照记一份**：万一日后想把工程改成外挂（或反过来），
                // 有这条路信息就能顺藤摸瓜，而不是让老师重新找一遍试卷。
                RelativePath = hasPdf ? TryMakeRelative(projectPath, pdfPath) : null,
                AbsolutePath = hasPdf ? SafeFullPath(pdfPath) : null,
                Fingerprint = hasPdf ? DocumentFingerprint.Compute(pdfPath!) : string.Empty,
            },
            View = view ?? new TwbViewState(),
            Stats = new TwbStats
            {
                StrokeCount = strokeCount,
                ObjectCount = objectCount,
            },
        };

        return manifest;
    }

    /// <summary>
    /// 由视口中心的世界 Y 推出"现在看的是第几页"。
    /// </summary>
    /// <remarks>
    /// 视口本身没有"当前页"这个概念（M2 起就是一张连续画布），所以只能反推：
    /// 先看视口中心落在哪一页的纵向范围内；落在页间空隙或整份试卷之外时，取纵向最近的一页。
    /// 这个值<b>只进 manifest 的诊断字段</b> —— 复原视图靠的是缩放与平移量，
    /// 不能用页号反算（那会把老师微调过的位置抹掉）。
    /// </remarks>
    public static int ResolvePageIndex(IReadOnlyList<Rect> pageRects, double worldY)
    {
        if (pageRects.Count == 0) return 0;

        for (int i = 0; i < pageRects.Count; i++)
        {
            if (worldY >= pageRects[i].Y && worldY <= pageRects[i].Bottom) return i;
        }

        int best = 0;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < pageRects.Count; i++)
        {
            double distance = Math.Abs(worldY - (pageRects[i].Y + pageRects[i].Bottom) / 2.0);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// 存档里的视口“落点是否有效” —— 即按它摆好视口后，屏幕上真能看到纸（可见范围与页面相交）。
    /// </summary>
    /// <param name="scale">存档缩放。</param>
    /// <param name="offsetX">存档水平平移（与 <c>CanvasViewport</c> 同约定：世界 → 视口 的平移量）。</param>
    /// <param name="offsetY">存档竖直平移。</param>
    /// <param name="viewportSize">视口（画布）尺寸，DIP。</param>
    /// <param name="worldBounds">页面并集的包围盒（世界坐标）。</param>
    /// <remarks>
    /// <b>为什么要有这条判定。</b>排版是“页面从 (960,960) 起排、四周留 960pt 可写留白”，
    /// 所以世界原点附近是<b>一片空白</b>。一旦存档里是 100%@(0,0)（早期档、换过窗口尺寸的档、
    /// 或干脆是个坏档），打开工程后整屏什么都没有 —— 在老师眼里这就是“文件没打开”，
    /// 比“位置不够精确”严重得多。这种情况宁可按“适应宽度”兜底。
    /// <para>
    /// 做成纯函数是为了让 harness 能直接断言这段判定 —— 它在界面上的表现只有“白屏”一种，
    /// 靠真机肉眼是看不出来的（白屏和白纸长得一样）。
    /// </para>
    /// </remarks>
    public static bool IsViewUsable(double scale, double offsetX, double offsetY, Size viewportSize, Rect worldBounds)
    {
        // 空白工程（还没有试卷）没有“页面之外”可言；画布还没量出尺寸时也无从判断
        if (worldBounds.IsEmpty) return true;
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0) return true;
        if (scale <= 0 || double.IsNaN(scale)) return false;

        // 与 CanvasViewport.WorldToViewport 同一套换算：先缩放再平移 ⇒ 世界 =（视口 - 平移）/ 缩放
        var topLeft = new Point(-offsetX / scale, -offsetY / scale);
        var bottomRight = new Point((viewportSize.Width - offsetX) / scale,
                                    (viewportSize.Height - offsetY) / scale);

        return new Rect(topLeft, bottomRight).IntersectsWith(worldBounds);
    }

    /// <summary>
    /// 算 PDF 相对于工程文件的路径；算不出（不同盘、路径非法）时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须存相对路径。</b>老师的真实动线是把工程和试卷一起拷到另一处
    /// （U 盘、共享盘、另一台一体机）。相对路径在这种情况下仍然指向正确位置，绝对路径必然失效。
    /// <para>
    /// 不同盘时 <see cref="Path.GetRelativePath"/> 会返回一个<b>绝对</b>路径
    /// （形如 <c>D:\other\x.pdf</c>），把它当相对路径存下来是错的 —— 读侧拼上项目目录会得到
    /// 一个不存在的怪路径。所以这里显式判掉，只留绝对路径那一份记录。
    /// </para>
    /// </remarks>
    public static string? TryMakeRelative(string? baseFilePath, string? targetFilePath)
    {
        if (string.IsNullOrEmpty(baseFilePath) || string.IsNullOrEmpty(targetFilePath)) return null;

        try
        {
            string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(baseFilePath)) ?? string.Empty;
            if (baseDirectory.Length == 0) return null;

            string relative = Path.GetRelativePath(baseDirectory, Path.GetFullPath(targetFilePath));

            // 同一个位置（"."）或跨盘的绝对路径都没有"相对"的意义
            if (relative.Length == 0 || relative == "." || Path.IsPathRooted(relative)) return null;

            return relative;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"无法计算相对路径（工程 {baseFilePath} → PDF {targetFilePath}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>取绝对路径；路径非法时返回 <c>null</c>（而不是抛异常打断保存）。</summary>
    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            return Path.GetFullPath(path!);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"路径无法解析为绝对路径：{path} —— {ex.Message}");
            return null;
        }
    }
}
