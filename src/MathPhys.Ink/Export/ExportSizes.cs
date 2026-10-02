namespace MathPhys.Ink.Export;

/// <summary>
/// 导出产物的"体积口径"：怎么报给老师看、多大算大。
/// </summary>
/// <remarks>
/// 单独成类而<b>不</b>放在 <see cref="ExportService"/> 里当私有方法：体积口径要同时被
/// PNG 与 PDF 两条路、以及状态栏与弹窗共用；散成两三份以后一定会分叉
/// （一处按 1024、一处按 1000，老师看到的数字对不上，就会怀疑哪个是错的）。
/// <para>
/// 阈值做成 <see cref="const"/> 并配纯函数，是为了让 harness 能<b>直接断言</b>这条提示 ——
/// 否则要验"超过 50 MB 会提示什么"就得真的造一个 50 MB 的产物出来，
/// 而那种测试平时没人会跑。
/// </para>
/// </remarks>
public static class ExportSizes
{
    /// <summary>超过这个体积就额外提示一句（50 MB）。</summary>
    public const long LargeFileThreshold = 50L * 1024 * 1024;

    /// <summary>哪个体积算"大"。</summary>
    public static bool IsLarge(long bytes) => bytes >= LargeFileThreshold;

    /// <summary>
    /// 给人看的体积：大于 1 MB 给 MB 一位小数，否则给 KB。
    /// </summary>
    /// <remarks>
    /// 不分档到 GB：试卷导出到 GB 级别只可能是出了别的问题，
    /// 那种时候更应该看到"1024.0 MB"这种一眼不对的数字，而不是被换算成"1.0 GB"藏起来。
    /// </remarks>
    public static string Format(long bytes)
        => bytes >= 1048576 ? $"{bytes / 1048576.0:F1} MB" : $"{bytes / 1024.0:F0} KB";

    /// <summary>
    /// "文件较大"提示：不大时返回<b>空串</b>（调用方直接拼进消息里，不必自己判空）。
    /// </summary>
    /// <remarks>
    /// 它是提示而<b>不是</b>失败：大卷子本来就大，拦下来反而误事（老师可能就是要那份大的）。
    /// 提示的意义是"打印店那边可能慢 / 群里可能发不出去"，让人心里有数。
    /// </remarks>
    public static string LargeHint(long bytes)
        => IsLarge(bytes) ? $"（文件较大：{Format(bytes)}）" : string.Empty;
}
