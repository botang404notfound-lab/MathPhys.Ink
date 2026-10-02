using System.Windows.Media.Imaging;

namespace MathPhys.Ink.Pdf;

/// <summary>
/// 渲染位图的 LRU 缓存。渲染线程写、UI 线程读，内部加锁。
/// </summary>
/// <remarks>
/// 两个刻意的设计：
/// <list type="number">
/// <item>键用 <c>(页索引, 档位索引)</c> 两个<b>整数</b>，不用 double。
///       浮点做字典键会踩相等性与哈希的坑，而档位本来就是离散的（见 <see cref="RenderLevels"/>）。</item>
/// <item>容量按<b>字节</b>而非条目数计。不同档位的位图大小差 30 倍以上
///       （A4：1.0 档约 3.6 MB，6.0 档约 128 MB），按条目数限制等于没限制。</item>
/// </list>
/// </remarks>
public sealed class PageRenderCache
{
    /// <summary>缓存键：页索引 + <see cref="RenderLevels"/> 的档位索引。</summary>
    public readonly record struct Key(int PageIndex, int LevelIndex);

    private sealed class Entry
    {
        public Key Key;
        public BitmapSource Bitmap = null!;
        public long Bytes;
    }

    private readonly object _gate = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _index = new();
    private readonly LinkedList<Entry> _lru = new();   // 头部 = 最近使用

    private long _maxBytes = 256L * 1024 * 1024;

    /// <summary>容量上限（字节）。默认 256 MB。调小会立即触发淘汰。</summary>
    public long MaxBytes
    {
        get => Volatile.Read(ref _maxBytes);
        set
        {
            Volatile.Write(ref _maxBytes, Math.Max(0, value));
            lock (_gate) Trim();
        }
    }

    /// <summary>当前占用字节数。</summary>
    public long CurrentBytes { get; private set; }

    public int Count
    {
        get { lock (_gate) return _index.Count; }
    }

    public bool Contains(Key key)
    {
        lock (_gate) return _index.ContainsKey(key);
    }

    /// <summary>取缓存；命中会把它提升为最近使用。</summary>
    public bool TryGet(Key key, out BitmapSource? bitmap)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                if (!ReferenceEquals(_lru.First, node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                }
                bitmap = node.Value.Bitmap;
                return true;
            }

            bitmap = null;
            return false;
        }
    }

    /// <summary>
    /// 按「不超过目标档位的最大已缓存档位」取图，找不到再退而求其次取更高档位。
    /// </summary>
    /// <remarks>
    /// 这是「不闪白」的关键一步：视口一变就要立刻有画面，不能等后台渲染。
    /// 优先向下找是因为把低倍位图放大只是略糊，而把高倍位图缩小虽然清晰却浪费内存。
    /// </remarks>
    public bool TryGetClosest(int pageIndex, int preferredLevelIndex, out int levelIndex, out BitmapSource? bitmap)
    {
        lock (_gate)
        {
            int preferred = Math.Clamp(preferredLevelIndex, RenderLevels.MinIndex, RenderLevels.MaxIndex);

            for (int i = preferred; i >= RenderLevels.MinIndex; i--)
            {
                if (TryGet(new Key(pageIndex, i), out bitmap))
                {
                    levelIndex = i;
                    return true;
                }
            }

            for (int i = preferred + 1; i <= RenderLevels.MaxIndex; i++)
            {
                if (TryGet(new Key(pageIndex, i), out bitmap))
                {
                    levelIndex = i;
                    return true;
                }
            }

            levelIndex = -1;
            bitmap = null;
            return false;
        }
    }

    /// <summary>放入缓存（已存在则替换）。</summary>
    public void Add(Key key, BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        long bytes = EstimateBytes(bitmap);

        lock (_gate)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _index.Remove(key);
                CurrentBytes -= existing.Value.Bytes;
            }

            var entry = new Entry { Key = key, Bitmap = bitmap, Bytes = bytes };
            var node = _lru.AddFirst(entry);
            _index[key] = node;
            CurrentBytes += bytes;

            Trim();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _index.Clear();
            _lru.Clear();
            CurrentBytes = 0;
        }
    }

    /// <summary>按位图格式估算内存占用（字节）。</summary>
    public static long EstimateBytes(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int bytesPerPixel = Math.Max(1, bitmap.Format.BitsPerPixel / 8);
        return (long)bitmap.PixelWidth * bitmap.PixelHeight * bytesPerPixel;
    }

    /// <summary>从最久未使用的尾部淘汰，直到回到预算内。</summary>
    private void Trim()
    {
        // 始终保留至少一项：否则单张位图超过预算时就永远没画面可显示
        while (CurrentBytes > MaxBytes && _lru.Count > 1)
        {
            var tail = _lru.Last;
            if (tail is null) break;

            _lru.RemoveLast();
            _index.Remove(tail.Value.Key);
            CurrentBytes -= tail.Value.Bytes;
        }
    }
}
