using System.Windows;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Design.Dialogs;

/// <summary>
/// 弹窗服务（M10 S5）：把系统 <c>MessageBox</c> 换成受主题控制的 <see cref="TDialogWindow"/>。
/// </summary>
/// <remarks>
/// ★ 参数与返回值都与 <c>MessageBox.Show</c> 保持一致，所以调用点只是换了个名字、
/// 没有任何逻辑改动 —— 这也让"要不要换回来"变成一个纯外观决定。
/// <para>
/// ★ 两条**刻意保留系统弹窗**的情况：
/// <list type="number">
///   <item>没有 <c>Application</c>（验收 harness、启动极早期）——
///         自建窗口依赖资源与调度器，那时建它只会再抛一次异常；</item>
///   <item>创建自建窗口本身失败（比如资源被改坏）—— 回退到系统弹窗，
///         提示照样能出来。这一层兜底的理由很实际：<b>这些提示里有一类是"保存失败"</b>，
///         它要是弹不出来，老师会以为存住了。</item>
/// </list>
/// </para>
/// </remarks>
public static class TDialog
{
    /// <summary>
    /// 弹一个窗，返回老师的回答。
    /// </summary>
    /// <param name="message">正文（支持换行）。</param>
    /// <param name="caption">标题栏文字。</param>
    /// <param name="buttons">按钮组。</param>
    /// <param name="image">图标语义。</param>
    public static MessageBoxResult Show(
        string message,
        string caption = "数理墨",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        var app = Application.Current;

        // 没有 Application 就没有资源系统：直接用系统弹窗（harness / 极早期启动）
        if (app is null) return MessageBox.Show(message, caption, buttons, image);

        try
        {
            var window = new TDialogWindow(message, caption, buttons, image);

            // 有主窗口就居中在它上面（一体机上"弹窗跑到屏幕角落"很容易被当成出错）
            var owner = app.MainWindow;
            if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

            window.ShowDialog();
            return window.Result;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"自建弹窗失败，已回退系统弹窗：{ex.Message}");
            return MessageBox.Show(message, caption, buttons, image);
        }
    }
}
