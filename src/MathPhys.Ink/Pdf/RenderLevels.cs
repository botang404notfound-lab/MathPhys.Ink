namespace MathPhys.Ink.Pdf;

/// <summary>
/// 渲染档位表：把连续的视口缩放量化成固定档位。
/// </summary>
/// <remarks>
/// <b>这不是优化，是位图缓存能生效的前提。</b>
/// 若直接拿连续的 <c>Scale</c> 当缓存键，缩放手势产生的每一个中间值都是一次全新渲染，
/// 命中率恒为 0 —— 加再多缓存也等于没加。
/// <para>
/// 取档规则：<b>不小于当前 scale 的最小档位</b>。宁可超采样（多花点内存），
/// 绝不欠采样（画面会糊，而且糊在 PDF 文字上是立刻能看出来的）。
/// </para>
/// </remarks>
public static class RenderLevels
{
    /// <summary>
    /// 档位取值（zoom；zoom=1 表示「1 world 单位 → 1 DIP」）。
    /// 数组下标即缓存的 level key，所以这个数组<b>只能追加、不能重排</b>。
    /// </summary>
    /// <remarks>
    /// 上限取 6.0 的依据：4K 一体机（3840 宽）按页宽适配时 scale ≈ 6.45，取 6.0 档近似 1:1；
    /// 1080p 一体机 fit-width ≈ 3.23，会落到 4.0 档（超采样，更清晰）。
    /// 再高的倍率本版本用位图拉伸承受，见设计方案 §6。
    /// </remarks>
    private static readonly double[] ZoomValues =
    {
        0.25, 0.375, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0,
    };

    /// <summary>档位数值表（只读）。</summary>
    public static IReadOnlyList<double> Values => ZoomValues;

    /// <summary>1.0 档的下标。首次渲染、缓存预热都用它。</summary>
    public const int DefaultIndex = 4;

    /// <summary>最高档位下标。</summary>
    public static int MaxIndex => ZoomValues.Length - 1;

    /// <summary>最低档位下标（恒为 0）。</summary>
    public const int MinIndex = 0;

    /// <summary>档位下标 → 渲染 zoom（会钳制到合法范围）。</summary>
    public static double ToZoom(int levelIndex)
        => ZoomValues[Math.Clamp(levelIndex, MinIndex, MaxIndex)];

    /// <summary>渲染 zoom → 最接近的档位下标（用于给外部量估档位）。</summary>
    public static int FromZoom(double zoom) => ForScale(zoom);

    /// <summary>
    /// 当前视口 scale → 应采用的档位下标：<b>不小于 scale 的最小档</b>。
    /// </summary>
    public static int ForScale(double scale)
    {
        if (double.IsNaN(scale) || scale <= ZoomValues[0])
        {
            return MinIndex;
        }

        for (int i = 0; i < ZoomValues.Length; i++)
        {
            if (ZoomValues[i] >= scale) return i;
        }

        return MaxIndex;
    }
}
