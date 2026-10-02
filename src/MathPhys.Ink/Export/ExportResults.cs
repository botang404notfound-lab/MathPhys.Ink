namespace MathPhys.Ink.Export;

/// <summary>
/// 一次导出的结果。
/// </summary>
/// <remarks>
/// 导出是"手动触发 + 产出落盘物"的操作，成败都必须能给老师一句人话：成功说清存到哪、多大，
/// 失败说清为什么。所以不用 bool 当返回值 —— bool 只能让调用处再编一句话，编出来的往往是黑话。
/// <para>
/// 「用户取消」单列一个 <see cref="Cancelled"/> 而不算失败：在选文件对话框里点取消是正常操作，
/// 弹窗报错、状态栏报黄字都是打扰。
/// </para>
/// </remarks>
/// <param name="Ok">是否成功落盘。</param>
/// <param name="Message">给老师看的一句话（状态栏与弹窗共用同一句，不各写一份）。</param>
/// <param name="SavedPath">成功时的落盘路径；失败或取消时为 <c>null</c>。</param>
/// <param name="BytesWritten">写出的字节数；失败或取消时为 0。用于"文件较大"提示与自检。</param>
/// <param name="Cancelled">是否是"用户在选文件对话框里取消"（不是失败）。</param>
public readonly record struct ExportResult(
    bool Ok,
    string Message,
    string? SavedPath,
    long BytesWritten,
    bool Cancelled = false)
{
    /// <summary>用户取消：静默处理。</summary>
    public static ExportResult UserCancelled(string message = "已取消导出。")
        => new(false, message, null, 0, Cancelled: true);

    /// <summary>失败：调用方据此弹窗 + 状态栏黄字 + 写日志。</summary>
    public static ExportResult Failure(string message) => new(false, message, null, 0);

    /// <summary>成功。</summary>
    public static ExportResult Success(string message, string savedPath, long bytesWritten)
        => new(true, message, savedPath, bytesWritten);
}

/// <summary>
/// 整卷导出时"某一页失败"。带页号，好在调用处翻成一句老师读得懂的话。
/// </summary>
/// <remarks>
/// 存在的理由：整卷导出要"某页失败就整体失败并说清是第几页"（半份产物比没有更危险），
/// 而渲染与编码发生在 <see cref="PdfWriter"/> 的逐页回调里 ——
/// 用本异常把页号<b>穿透回调边界</b>带回编排层，比在回调里返回 null 哨兵值清楚得多。
/// </remarks>
public sealed class ExportPageException : Exception
{
    public ExportPageException(string message, Exception? inner = null) : base(message, inner) { }
}
