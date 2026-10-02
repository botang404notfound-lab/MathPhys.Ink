using System.Windows;

namespace MathPhys.Ink.ViewModels;

/// <summary>老师对「工程有未保存改动」这一问的答复。</summary>
public enum ProjectCloseChoice
{
    /// <summary>先保存，再继续。</summary>
    Save,

    /// <summary>不保存，明确丢弃这次的改动。</summary>
    Discard,

    /// <summary>什么都不做 —— 这一问被打断（关窗口 / 换工程都不做）。</summary>
    Cancel,
}

/// <summary>
/// 「工程有未保存改动」这一问的<b>纯逻辑</b>：什么时候问、问完放不放行。
/// </summary>
/// <remarks>
/// 关窗口、新建工程、打开工程、关闭工程四个入口共用这一份判断。
/// <para>
/// 做成纯函数是为了让 harness 能断言整个矩阵 —— 这条路上「漏问一次」的代价是
/// 老师一整节课的板书，而它在界面上表现为「什么都没发生」，肉眼绝对看不出来
/// （不像崩溃那样有现场）。矩阵只有五行，值得把它钉死在断言里。
/// </para>
/// </remarks>
public static class ProjectClosePolicy
{
    /// <summary>要不要问：只有「有工程 <b>且</b> 有未保存改动」才问。</summary>
    /// <remarks>
    /// 没有工程时（只开着一份 PDF、批注走侧车）不问 —— 那种情况下批注是自动落盘的，
    /// 问一句「要保存吗」只会让老师以为丢了东西。
    /// </remarks>
    public static bool NeedsPrompt(bool hasProject, bool isDirty) => hasProject && isDirty;

    /// <summary>
    /// 老师的答复（+ 保存是否成功）⇒ 这次动作放不放行。
    /// </summary>
    /// <remarks>
    /// ★ <b>选「保存」而保存失败时必须不放行</b>：界面关掉了、改动没存住，
    /// 是这套机制里最不可原谅的一种失败 —— 老师以为存住了，而文件其实没变。
    /// </remarks>
    public static bool ShouldProceed(ProjectCloseChoice choice, bool saveSucceeded) => choice switch
    {
        ProjectCloseChoice.Save => saveSucceeded,
        ProjectCloseChoice.Discard => true,
        _ => false,
    };

    /// <summary>
    /// 把三选对话框的按钮结果映射成答复。
    /// </summary>
    /// <remarks>
    /// 视图侧唯一的映射点：写成 <c>switch</c> 而不是散在各处判断，
    /// 是为了让「按钮语义」这件事只有一个地方可以改错。
    /// 注意 <b>No 之外的一切（含 Esc 关闭对话框）都归 Cancel</b> ——
    /// 老师按 Esc 的本意是「别动」，绝不能理解成「丢弃」。
    /// </remarks>
    public static ProjectCloseChoice FromMessageBox(MessageBoxResult result) => result switch
    {
        MessageBoxResult.Yes => ProjectCloseChoice.Save,
        MessageBoxResult.No => ProjectCloseChoice.Discard,
        _ => ProjectCloseChoice.Cancel,
    };

    /// <summary>
    /// 提示正文。
    /// </summary>
    /// <remarks>
    /// 三个按钮各是什么意思<b>直接写在正文里</b>：中文里「否」既有「不保存」也有
    /// 「不，别关」两种读法，而这一问偏偏是决定一整节课板书去留的那一问 ——
    /// 不能让老师去猜按钮的语义。工程名也写进来，多功能窗口里能一眼认出是哪一份。
    /// </remarks>
    public static string PromptText(string projectName)
        => $"工程「{projectName}」有未保存的改动。\n\n"
         + "「是」= 保存后继续\n"
         + "「否」= 不保存，丢弃这次的改动\n"
         + "「取消」= 什么都不做";
}
