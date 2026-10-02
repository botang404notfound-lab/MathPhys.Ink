using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Ink;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 仿真截图的位图仓库 —— 图形对象只记一个 Id，字节放在这里。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么字节不能塞进对象参数里。</b><c>GfxObjectData</c> 只有
/// <c>Numbers</c>（double）与 <c>Texts</c>（string）两个袋子，而它随
/// <c>annotation.tbink</c> 走 <b>文本</b>序列化：一帧 1280×720 的 PNG 编码成 base64 约
/// 0.5 MB，写进去就是"每一个快照、每一次撤销、每一次自动保存都拖着这 0.5 MB 走一遍
/// 字符串编码"。而它还不止一份：撤销栈存 60 份快照 ⇒ 30 MB 纯字符串。
/// </para>
/// <para>
/// 所以分成两层：<b>对象里只放一个引用</b>（<c>Texts["imageId"]</c>），
/// <b>字节进 <c>.twb</c> 的独立图像段</b>（见 <see cref="TwbFile"/>）。
/// 撤销只搬引用，一个字节省下来的开销约等于零。
/// </para>
/// <para>
/// <b>按内容去重。</b>同一帧导两次（老师手抖点了两下、或从两个页面各导一次同一张画面）
/// 只存一份字节 —— 键是 SHA-256。去重是无条件的，因为"同内容不同 Id"在语义上
/// 没有任何区别：位图是不可变的。
/// </para>
/// <para>
/// <b>不做引用计数。</b>撤销掉一张截图后字节<b>不</b>回收：撤销/重做的快照里带着
/// 同一个 Id，回收了再重做就成了一张"缺失"占位框 —— 那比多占几百 KB 严重得多。
/// 字节只在换文档（<see cref="Clear"/>）时整体释放。
/// </para>
/// </remarks>
public sealed class WebPanelImageStore
{
    /// <summary>位图 Id → PNG 字节。</summary>
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    /// <summary>内容 SHA-256 → 位图 Id（去重索引）。</summary>
    private readonly Dictionary<string, string> _byHash = new(StringComparer.Ordinal);

    /// <summary>当前存了几张位图。</summary>
    public int Count => _blobs.Count;

    /// <summary>现存的字节总量（诊断用：老师问"工程怎么变大了"时能答上来）。</summary>
    public long TotalBytes { get; private set; }

    /// <summary>
    /// 存入一张位图，返回它的 Id。
    /// </summary>
    /// <remarks>
    /// 空字节不存、返回空串 —— 调用方据此判断"这次抓帧其实没拿到东西"，
    /// 而不是拿到一个指向空气的 Id（那种对象打开后就是一个空框，最难查）。
    /// </remarks>
    public string Add(byte[]? png)
    {
        if (png is null || png.Length == 0) return string.Empty;

        string hash = HashOf(png);

        if (_byHash.TryGetValue(hash, out string? existing) && _blobs.ContainsKey(existing))
        {
            return existing;   // 同内容：复用同一份字节
        }

        string id = NewId();
        _blobs[id] = png;
        _byHash[hash] = id;
        TotalBytes += png.Length;
        return id;
    }

    /// <summary>
    /// 按 Id 取字节；没有这个 Id 时返回 <c>null</c>（<b>不抛异常</b>）。
    /// </summary>
    /// <remarks>
    /// "没有"是<b>预期的正常情况</b>：别人发来的工程缺了这张图、或位图段被外部工具删掉了。
    /// 渲染器据此画一个占位框，对象本身照旧存在、照旧能拖动、存档时照旧写回。
    /// </remarks>
    public byte[]? Get(string? id)
        => !string.IsNullOrEmpty(id) && _blobs.TryGetValue(id!, out var bytes) ? bytes : null;

    /// <summary>这个 Id 有没有对应的字节。</summary>
    public bool Contains(string? id) => Get(id) is not null;

    /// <summary>扔掉一张位图。</summary>
    /// <remarks>
    /// 删除对象时<b>不</b>调用它（见类注释里"不做引用计数"那条）。留给"清理孤儿位图"
    /// 这类明确的维护动作用。
    /// </remarks>
    public bool Remove(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_blobs.TryGetValue(id!, out var bytes)) return false;

        _blobs.Remove(id!);
        _byHash.Remove(HashOf(bytes));
        TotalBytes -= bytes.Length;
        return true;
    }

    /// <summary>清空（换文档时）。</summary>
    public void Clear()
    {
        if (_blobs.Count == 0) return;

        _blobs.Clear();
        _byHash.Clear();
        TotalBytes = 0;
    }

    /// <summary>导出成可序列化形态（写 <c>.twb</c> 图像段用）。</summary>
    public IReadOnlyList<TwbImageEntry> Snapshot()
    {
        var list = new List<TwbImageEntry>(_blobs.Count);

        foreach (var pair in _blobs)
        {
            list.Add(new TwbImageEntry { Id = pair.Key, Bytes = pair.Value });
        }

        return list;
    }

    /// <summary>
    /// 用存档里的位图整体替换当前内容。
    /// </summary>
    /// <remarks>
    /// <b>必须是"替换"而不是"合并"</b>：打开一份新工程时若与上一份合并，
    /// 上一份的位图会一直躺在内存里（Id 撞不上，永远没人用），
    /// 而且下一份工程保存时会被原样写进去 —— 工程文件越存越大。
    /// </remarks>
    public void Restore(IEnumerable<TwbImageEntry>? images)
    {
        Clear();

        if (images is null) return;

        foreach (var image in images)
        {
            if (image is null) continue;
            if (string.IsNullOrEmpty(image.Id)) continue;
            if (image.Bytes is not { Length: > 0 }) continue;
            if (_blobs.ContainsKey(image.Id)) continue;   // 包内重复 Id：留第一份

            _blobs[image.Id] = image.Bytes;
            _byHash[HashOf(image.Bytes)] = image.Id;
            TotalBytes += image.Bytes.Length;
        }
    }

    /// <summary>生成一个位图 Id（短、可念、只含文件名安全字符）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>算一段字节的 SHA-256（十六进制小写）。</summary>
    public static string HashOf(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>一次运行的摘要（日志用）。</summary>
    public string Describe() => $"位图 {Count} 张 / {TotalBytes / 1024.0:F0} KB";
}
