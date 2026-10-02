namespace MathPhys.Ink.Ink;

/// <summary>
/// 工程文件（<c>.twb</c>）里 PDF 的绑定方式。
/// </summary>
/// <remarks>
/// 刻意用<b>字符串常量</b>而不是 <c>enum</c>：manifest.json 是给人看的（用记事本打开要能读懂），
/// 枚举序列化出来是数字，看一眼根本分不清哪个是"内嵌"；而且数字与枚举成员顺序绑死后，
/// 将来在中间插一个新成员就会让所有旧文件的含义整体错位。
/// </remarks>
public static class TwbPdfEmbedding
{
    /// <summary>PDF 字节内嵌在工程包里 —— 拷走一个 .twb 就能用（推荐，M8 默认）。</summary>
    public const string Embedded = "embedded";

    /// <summary>PDF 留在原处，工程只记路径 —— 工程包小，但 PDF 一旦被移动/改名就找不到。</summary>
    public const string Referenced = "referenced";

    /// <summary>
    /// 工程里还没有 PDF（「新建工程」后的空白状态）。
    /// </summary>
    /// <remarks>
    /// 必须有这么一个值，否则"空白工程"无家可归：写成 <see cref="Referenced"/> 却没有路径，
    /// 读侧只能判成"损坏"；写成 <see cref="Embedded"/> 又没有内容，同样自相矛盾。
    /// 而空白工程是真实存在的用法 —— 老师想先建个工程、随手写几笔思路，回头再插试卷。
    /// </remarks>
    public const string None = "none";
}

/// <summary>
/// 工程里 PDF 的来源描述。
/// </summary>
public sealed class TwbPdfRef
{
    /// <summary>绑定方式，取值见 <see cref="TwbPdfEmbedding"/>。</summary>
    public string Embedding { get; set; } = TwbPdfEmbedding.Embedded;

    /// <summary>外挂模式：相对 .twb 所在目录的路径（优先用它，工程整体搬家后仍能找到）。</summary>
    public string? RelativePath { get; set; }

    /// <summary>外挂模式：绝对路径（相对路径失效时的兜底）。</summary>
    public string? AbsolutePath { get; set; }

    /// <summary>
    /// PDF 的内容指纹（见 <see cref="DocumentFingerprint"/>），用来回答"还是不是我那份试卷"。
    /// </summary>
    /// <remarks>
    /// 与 .tbink 用的是<b>同一个函数、同一种值</b>，没有新算法 —— M8 只是把它换了个地方存。
    /// </remarks>
    public string Fingerprint { get; set; } = string.Empty;
}

/// <summary>
/// 保存时的视图状态。
/// </summary>
/// <remarks>
/// 存在的意义只有一个：让老师"上次停在哪页，下次打开还在那页"。
/// 所有几何量都是 <b>world 单位（PDF point = 1/72 inch）</b>，与 M2 起的世界坐标铁律一致 ——
/// 在这里换成像素或 DIP，换台缩放比不同的机器打开就会跑位。
/// </remarks>
public sealed class TwbViewState
{
    /// <summary>当前页（0 起）。</summary>
    public int PageIndex { get; set; }

    /// <summary>缩放倍数（1.0 = 100%）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>视口左上角在世界坐标中的 X。</summary>
    public double OffsetX { get; set; }

    /// <summary>视口左上角在世界坐标中的 Y。</summary>
    public double OffsetY { get; set; }

    /// <summary>保存时是否处于全屏（一体机授课态）。</summary>
    public bool IsFullScreen { get; set; }
}

/// <summary>
/// 工程内容的条数统计。
/// </summary>
/// <remarks>
/// <b>仅用于诊断展示</b>，加载侧绝不据此判定内容 —— 真值永远以 <c>annotation.tbink</c> 载荷为准。
/// 旧程序读新文件时这些字段缺席会被填 0，那也无所谓。
/// </remarks>
public sealed class TwbStats
{
    /// <summary>笔迹条数（诊断用）。</summary>
    public int StrokeCount { get; set; }

    /// <summary>图形对象个数（诊断用）。</summary>
    public int ObjectCount { get; set; }

    /// <summary>包内位图张数（诊断用；M22 仿真截图）。</summary>
    public int ImageCount { get; set; }
}

/// <summary>
/// 工程包内的一张位图（M22 S4b：仿真面板的画面）。
/// </summary>
/// <remarks>
/// 与 <see cref="TwbStats"/> 同一层：它是<b>包内结构</b>的一部分，不是运行期形态。
/// 图形对象的 <c>Texts["imageId"]</c> 指向 <see cref="Id"/>，
/// 包内条目名由 <see cref="TwbFile.ImageEntryName"/> 从 Id 推出来 ——
/// <b>不存条目名</b>：多存一份就多一处「Id 与条目名不一致」的坏档。
/// </remarks>
public sealed class TwbImageEntry
{
    /// <summary>位图 Id（只含 <c>[0-9A-Za-z_-]</c>，可直接进文件名）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>PNG 字节；空数组表示这张图没内容（读侧会跳过）。</summary>
    public byte[] Bytes { get; set; } = System.Array.Empty<byte>();
}

/// <summary>
/// 各段载荷的内容校验值，用来回答"这份工程在保存之后被动过没有"。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么 ZIP 自己的 CRC 不够用（实测结论）。</b>内嵌 PDF 是以 <c>stored</c>（不压缩）
/// 方式存的，而 .NET 的 <see cref="System.IO.Compression.ZipArchive"/> 在<b>读取 stored 条目时不校验 CRC</b>：
/// 把 <c>document.pdf</c> 里的 1 个字节改掉再读，它会<b>静默返回被改坏的内容</b>，一声不吭。
/// </para>
/// <para>
/// 这个行为对本项目是不能接受的：工程文件是老师唯一的成果载体，一旦"卷面悄悄花掉"
/// 或"批注少了几笔"却没有任何提示，现场根本无从追查 —— 正是 docs/06 里反复强调的
/// <b>静默损坏</b>。所以这里自己存一份 SHA-256，读取时比对：不符就明确拒绝并说清是哪一段坏了。
/// </para>
/// <para>
/// 代价是保存/打开时各多算一遍 SHA-256。一份 30 MB 的扫描卷面约 100 ms，
/// 换"绝不会静默给出坏内容"，这笔账划得来。
/// </para>
/// <para>
/// 旧文件（或手改的 manifest）没有这一段时，<b>跳过校验</b>而不是判成损坏 ——
/// 校验值是增强，不是必需品。
/// </para>
/// </remarks>
public sealed class TwbIntegrity
{
    /// <summary>内嵌 PDF 的 SHA-256（十六进制小写）；外挂与空白工程时为空串。</summary>
    public string PdfSha256 { get; set; } = string.Empty;

    /// <summary>批注载荷的 SHA-256；工程里没有批注条目时为空串。</summary>
    public string AnnotationSha256 { get; set; } = string.Empty;

    /// <summary>
    /// 各张位图的 SHA-256，键是包内条目名（<c>images/xxx.png</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="PdfSha256"/> / <see cref="AnnotationSha256"/> 是同一个手段、同一套纪律：
    /// ZIP 的 CRC 在<b>读 stored 条目不校验</b>，只有自己的校验值才能保证「坏内容绝不静默流出去」。
    /// 用<b>字典</b>而不是拼成一长串：出问题时能一句话说清是哪一张坏了，也便于将来单独重取一张。
    /// </remarks>
    public System.Collections.Generic.Dictionary<string, string> ImageSha256 { get; set; } = new();
}

/// <summary>
/// 工程文件里的元信息（<c>manifest.json</c>），也是 <c>.twb</c> 的<b>识别依据</b>。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="TbinkManifest"/> 一样，它只解决一个问题：打开之前能判断
/// "这份笔迹到底属不属于这份试卷、该去哪找这份试卷"。区别在于 .twb 还多担了一件事 ——
/// <b>它就是"这是个 .twb 工程"的凭证</b>（ZIP 容器本身没有自己的魔数，
/// 只看 PK 头的话任何一个 zip 都会被误认成工程）。
/// </para>
/// <para>
/// 字段顺序与命名<b>直接对应</b> JSON 形状，不做额外映射：少一层映射就少一处"改了 A 忘了改 B"。
/// </para>
/// </remarks>
public sealed class TwbManifest
{
    /// <summary>格式版本，取值见 <see cref="TwbFile.CurrentSchemaVersion"/>。</summary>
    public int SchemaVersion { get; set; } = TwbFile.CurrentSchemaVersion;

    /// <summary>工程标题（默认取 PDF 文件名，老师可另存改名）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>首次创建时间（UTC，ISO 8601）。重新保存时<b>沿用原值</b>，便于看出工程是什么时候建的。</summary>
    public string CreatedUtc { get; set; } = string.Empty;

    /// <summary>最后一次保存时间（UTC）。由 <see cref="TwbFile.Write(string,TwbManifest,byte[]?,byte[]?,out string)"/> 自动盖戳。</summary>
    public string UpdatedUtc { get; set; } = string.Empty;

    /// <summary>PDF 来源描述。</summary>
    public TwbPdfRef Pdf { get; set; } = new();

    /// <summary>保存时的视图状态。</summary>
    public TwbViewState View { get; set; } = new();

    /// <summary>内容条数（诊断用）。</summary>
    public TwbStats Stats { get; set; } = new();

    /// <summary>各段载荷的校验值，由 <see cref="TwbFile.Write"/> 自动计算。</summary>
    public TwbIntegrity Integrity { get; set; } = new();

    /// <summary>
    /// 造一份"刚建好、还没写盘"的 manifest。
    /// </summary>
    /// <param name="title">工程标题。</param>
    /// <param name="embedding">PDF 绑定方式，取值见 <see cref="TwbPdfEmbedding"/>。</param>
    public static TwbManifest New(string title, string embedding = TwbPdfEmbedding.Embedded)
    {
        string now = DateTime.UtcNow.ToString("O");
        return new TwbManifest
        {
            SchemaVersion = TwbFile.CurrentSchemaVersion,
            Title = title ?? string.Empty,
            CreatedUtc = now,
            UpdatedUtc = now,
            Pdf = new TwbPdfRef { Embedding = embedding },
            View = new TwbViewState(),
            Stats = new TwbStats(),
        };
    }
}
