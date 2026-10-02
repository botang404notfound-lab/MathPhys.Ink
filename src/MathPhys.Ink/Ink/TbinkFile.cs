using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Ink;
using MathPhys.Ink.Gfx;

namespace MathPhys.Ink.Ink;

/// <summary>批注文件里记录的某一页在世界坐标中的矩形。</summary>
public sealed class TbinkPageRect
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

/// <summary>
/// 批注文件的元信息（manifest）。
/// </summary>
/// <remarks>
/// 它的存在只有一个目的：让加载前能判断"这份笔迹到底属不属于当前打开的这份试卷"。
/// 少了它，笔迹会被静默贴到错配的 PDF 上，从界面看就是"批注自己跑偏了"。
/// </remarks>
public sealed class TbinkManifest
{
    /// <summary>首次创建时间（UTC，ISO 8601）。重新保存时沿用原值，便于看出这份批注是什么时候开始写的。</summary>
    public string CreatedUtc { get; set; } = string.Empty;

    /// <summary>最后一次保存时间（UTC）。</summary>
    public string UpdatedUtc { get; set; } = string.Empty;

    /// <summary>来源 PDF 的文件名（仅用于诊断展示，不参与匹配）。</summary>
    public string SourceFileName { get; set; } = string.Empty;

    /// <summary>来源 PDF 的字节长度。</summary>
    public long SourceLength { get; set; }

    /// <summary>来源 PDF 的内容指纹（见 <see cref="DocumentFingerprint"/>）。</summary>
    public string SourceFingerprint { get; set; } = string.Empty;

    /// <summary>保存时的页外留白（world 单位），用于判断是否需要整体平移补偿。</summary>
    public double PageMargin { get; set; }

    /// <summary>保存时的页间距（world 单位）。</summary>
    public double PageGap { get; set; }

    /// <summary>各页在世界坐标中的矩形，索引与 PDF 页索引一致。</summary>
    public List<TbinkPageRect> Pages { get; set; } = new();

    /// <summary>笔迹条数（仅用于诊断，实际以 ISF 载荷为准）。</summary>
    public int StrokeCount { get; set; }

    /// <summary>图形对象个数（M7.4，仅用于诊断，实际以 objects.json 载荷为准）。</summary>
    /// <remarks>
    /// 旧程序读新文件时看不到这个字段，反序列化会留着默认值 0 —— 这正是我们想要的：
    /// 它<b>不参与任何判定</b>，只是给人和工具看的提示。
    /// </remarks>
    public int ObjectCount { get; set; }
}

/// <summary>
/// 批注文件（<c>.tbink</c>）的容器格式编解码。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不直接往文件里写一个裸 ISF。</b>裸 ISF 里只有笔迹本身，没有"这条笔迹属于哪份 PDF、
/// 页面排在哪"这些信息。一旦它被配到另一份试卷上，笔迹会<b>静默错位到别的题上</b> ——
/// 界面上看就是"批注自己跑偏了"，几乎无法追查。包一层 manifest 才有判断依据。
/// </para>
/// <para>文件布局（整数一律小端）：</para>
/// <code>
/// v1：[0..6)   魔数 "TBINK\0"
///     [6..8)   格式版本 u16 == 1
///     [8..12)  manifest 长度 i32（UTF-8 字节数）
///     [12..)   manifest JSON
///     [...]    ISF 笔迹字节（StrokeCollection.Save 的原始输出）
///
/// v2（M7.4，含图形对象时）：同上，但在 manifest 与笔迹之间插入一段：
///     [..)     objects 长度 i32
///     [..)     objects JSON（GfxObjectData 数组）
///     [...]    ISF 笔迹字节
/// </code>
/// <para>
/// 刻意把 JSON 放在 ISF 之前：用记事本打开这个文件，开头就是一段可读的元信息，
/// 排查"这份批注是哪来的、对不对得上"时不必再写工具。
/// </para>
/// <para>
/// 读取一律走 <see cref="TryRead"/> 而不是抛异常：批注文件可能被外部工具改坏、
/// 或来自更高版本的程序，这些都属于<b>预期内的正常情况</b>，应当优雅拒绝而不是崩掉。
/// </para>
/// </remarks>
public static class TbinkFile
{
    /// <summary>当前程序写出的格式版本。</summary>
    /// <remarks>
    /// <b>v1</b>：<c>魔数 + 版本 + jsonLen + manifest.json + ISF 笔迹</c>。
    /// <b>v2</b>（M7.4）：在 manifest 与笔迹之间插入一段 <c>objLen + objects.json</c>（图形对象）。
    /// <para>
    /// 加段的代价很小、收益很实在：老师拷试卷时仍然只需要拷<b>一个</b>侧车文件，
    /// 而不是"笔迹一个、图形又一个"（那样一定会有人只拷一半）。
    /// </para>
    /// <para>
    /// 写出的版本号按内容决定：<b>没有图形对象时仍写 v1</b> —— 让老程序还能打开新程序存的纯笔迹文件，
    /// 不制造无谓的不兼容。读的时候按版本分支。
    /// </para>
    /// </remarks>
    public const int CurrentVersion = 2;

    private const int HeaderSize = 12;

    private static readonly byte[] Magic = { (byte)'T', (byte)'B', (byte)'I', (byte)'N', (byte)'K', 0 };

    /// <summary>
    /// JSON 选项。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> 而非默认编码器：
    /// 默认编码器会把中文文件名转义成 <c>\uXXXX</c>，人工查看这个文件时就完全看不懂了。
    /// 这里写出的内容不参与任何 HTML/JS 上下文，放宽转义没有安全代价。
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// 写出一个完整的批注文件。
    /// </summary>
    /// <param name="stream">目标流。</param>
    /// <param name="manifest">元信息。</param>
    /// <param name="strokes">笔迹。</param>
    /// <param name="objects">图形对象（M7.4）；为 <c>null</c> 或空时按 v1 布局写。</param>
    public static void Write(
        Stream stream,
        TbinkManifest manifest,
        StrokeCollection strokes,
        IReadOnlyList<GfxObjectData>? objects = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(strokes);

        // 条数以实际写出的为准，避免上游忘了同步这两处
        manifest.StrokeCount = strokes.Count;
        manifest.ObjectCount = objects?.Count ?? 0;

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        byte[] payload = SerializeStrokes(strokes);

        bool withObjects = objects is { Count: > 0 };
        byte[] objectJson = withObjects ? SerializeObjects(objects!) : Array.Empty<byte>();

        stream.Write(Magic, 0, Magic.Length);
        WriteUInt16(stream, withObjects ? CurrentVersion : 1);
        WriteInt32(stream, json.Length);
        stream.Write(json, 0, json.Length);

        if (withObjects)
        {
            WriteInt32(stream, objectJson.Length);
            stream.Write(objectJson, 0, objectJson.Length);
        }

        stream.Write(payload, 0, payload.Length);
    }

    /// <summary>
    /// 读出一个批注文件。失败时返回 <c>false</c> 并给出可读原因（不抛异常）。
    /// </summary>
    /// <param name="stream">源流。</param>
    /// <param name="manifest">读到的元信息。</param>
    /// <param name="strokes">读到的笔迹。</param>
    /// <param name="objects">读到的图形对象；v1 文件或无对象时为 <c>null</c>。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryRead(
        Stream stream,
        out TbinkManifest manifest,
        out StrokeCollection strokes,
        out IReadOnlyList<GfxObjectData>? objects,
        out string error)
    {
        manifest = null!;
        strokes = null!;
        objects = null;
        error = string.Empty;

        if (stream is null || !stream.CanSeek)
        {
            error = "输入流不可定位，无法读取批注文件";
            return false;
        }

        if (stream.Length - stream.Position < HeaderSize)
        {
            error = "文件过短，不是批注文件";
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);

        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            error = "魔数不符（这不是 .tbink 批注文件）";
            return false;
        }

        int version = BitConverter.ToUInt16(header[6..8]);
        if (version > CurrentVersion)
        {
            error = $"批注文件版本 {version} 高于本程序支持的 {CurrentVersion}，请升级程序后再打开";
            return false;
        }

        int jsonLength = BitConverter.ToInt32(header[8..12]);
        if (jsonLength <= 0 || jsonLength > stream.Length - stream.Position)
        {
            error = "元信息长度非法（文件可能已损坏）";
            return false;
        }

        var jsonBytes = new byte[jsonLength];
        stream.ReadExactly(jsonBytes);

        try
        {
            manifest = JsonSerializer.Deserialize<TbinkManifest>(jsonBytes, JsonOptions)!;
        }
        catch (JsonException ex)
        {
            error = "元信息解析失败：" + ex.Message;
            return false;
        }

        if (manifest is null)
        {
            error = "元信息为空";
            return false;
        }

        // v2 起多一段图形对象；v1 文件没有这一段，直接进笔迹载荷。
        // ★ 必须按版本分支读，不能"先试着读一段长度看看像不像"——
        //   那种猜法在损坏文件上会读出个巨大的长度，把好好的文件判成损坏。
        if (version >= 2)
        {
            if (stream.Length - stream.Position < 4)
            {
                error = "图形对象段长度缺失（文件可能已损坏）";
                return false;
            }

            Span<byte> lengthBytes = stackalloc byte[4];
            stream.ReadExactly(lengthBytes);
            int objectLength = BitConverter.ToInt32(lengthBytes);

            if (objectLength < 0 || objectLength > stream.Length - stream.Position)
            {
                error = "图形对象段长度非法（文件可能已损坏）";
                return false;
            }

            if (objectLength > 0)
            {
                var objectBytes = new byte[objectLength];
                stream.ReadExactly(objectBytes);

                try
                {
                    var list = JsonSerializer.Deserialize<List<GfxObjectData>>(objectBytes, JsonOptions);
                    if (list is not null) objects = list;
                }
                catch (JsonException ex)
                {
                    // 图形读不出来不该牵连笔迹：笔迹是老师这节课的主要成果，
                    // 宁可少几个图形也要把字还给他，并把原因说清楚。
                    error = "图形对象解析失败（笔迹仍按原样恢复）：" + ex.Message;
                }
            }
        }

        long payloadLength = stream.Length - stream.Position;
        if (payloadLength <= 0)
        {
            // 有元信息但没载荷：当作空批注，不算损坏
            strokes = new StrokeCollection();
            return true;
        }

        var payload = new byte[payloadLength];
        stream.ReadExactly(payload);

        try
        {
            using var payloadStream = new MemoryStream(payload, writable: false);
            strokes = new StrokeCollection(payloadStream);
        }
        catch (Exception ex)
        {
            error = "笔迹数据解析失败：" + ex.Message;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 把笔迹序列化成 ISF 字节。
    /// </summary>
    /// <remarks>
    /// 用 WPF 内置的 ISF 而非自定义格式：它由 <c>StrokeCollection.Save</c> 直接产出，
    /// 完整保留每个采样点（含压力）与 <c>DrawingAttributes</c>（颜色、笔宽、压感开关），
    /// 我们自己写一个格式只会在"哪些字段忘了存"上栽跟头。
    /// <para>
    /// <b>ISF 不是位精确格式</b>（实测数据）：把首点 <c>(100,120)</c>、笔宽 <c>4.0</c> 存进去再读回来，
    /// 得到的是 <c>(100.00629921259842,120)</c>、笔宽 <c>3.9999874017370027</c> ——
    /// 它内部按量化的物理单位存储，往返必然带来约 <b>0.006 world 单位</b>的误差
    /// （换算到 1.6 倍缩放的屏幕上约 0.01 px，肉眼与数位板都感知不到）。
    /// </para>
    /// <para>
    /// 这条事实有两个实际含义：一是<b>验收断言不能用"逐位相等"</b>（那是比格式本身更严的要求）；
    /// 二是反复存盘/读取<b>不会累积漂移</b> —— 量化是收敛的，第二次读回的值与第一次相同。
    /// </para>
    /// </remarks>
    private static byte[] SerializeStrokes(StrokeCollection strokes)
    {
        using var buffer = new MemoryStream();
        strokes.Save(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 把图形对象序列化成 JSON 字节（M7.4）。
    /// </summary>
    /// <remarks>
    /// 与笔迹用 ISF 不同，图形用的是<b>给我们自己看的 JSON</b>：图形对象数量少
    /// （一份试卷通常个位数到几十个），而结构会随学科工具增加而频繁演化，
    /// 可读、可扩展、能容忍未知字段的 JSON 比紧凑的二进制格式合适得多。
    /// <para>
    /// 注意：这里只需要序列化，<b>不需要"为了兼容旧版做降级"</b> ——
    /// 未知的 <c>kind</c> 在加载侧会被转成占位对象（而不是丢弃），
    /// 所以插件的启用/禁用不会造成"图形永久丢失"。
    /// </para>
    /// </remarks>
    private static byte[] SerializeObjects(IReadOnlyList<GfxObjectData> objects)
        => JsonSerializer.SerializeToUtf8Bytes(objects, JsonOptions);

    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BitConverter.TryWriteBytes(bytes, (ushort)value);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, value);
        stream.Write(bytes);
    }
}
