using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Design.Dialogs;

/// <summary>
/// 自建弹窗窗口（M10 S5）。形态固定：标题 + 正文 + 1~3 个按钮。
/// </summary>
/// <remarks>
/// ★ 返回值是标准的 <see cref="MessageBoxResult"/>，语义与系统 <c>MessageBox</c> 一一对应：
/// <list type="bullet">
///   <item>点「是」→ <c>Yes</c>；「否」→ <c>No</c>；「确定」→ <c>OK</c>；「取消」→ <c>Cancel</c></item>
///   <item><b>按 Esc 或点右上角关闭 → 有「取消」按钮时给 <c>Cancel</c>，否则给 <c>None</c></b>
///         —— 与系统 MessageBox 的行为一致。之所以要刻意对齐这一条：
///         「关掉对话框」在老师心里等于「别动」，绝不能被我实现成「丢弃」。</item>
///   <item>按 Enter → 走默认按钮（是 / 确定）</item>
/// </list>
/// </remarks>
public partial class TDialogWindow : Window
{
    /// <summary>老师的回答。默认 <c>None</c>（＝"没回答"，调用方按"取消"处理）。</summary>
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    /// <param name="message">正文（支持换行）。</param>
    /// <param name="caption">标题栏文字。</param>
    /// <param name="buttons">按钮组。</param>
    /// <param name="image">图标语义（只影响左侧色点与符号）。</param>
    public TDialogWindow(string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(caption) ? "数理墨" : caption;
        MessageText.Text = message;

        ApplyIcon(image);
        BuildButtons(buttons);
    }

    /// <summary>按语义给出左侧的色点与符号。用 ASCII 的 ! / i 而不是字形图标 —— 后者有字体回退风险。</summary>
    private void ApplyIcon(MessageBoxImage image)
    {
        string? glyph = image switch
        {
            MessageBoxImage.Warning => "!",
            MessageBoxImage.Error => "!",
            MessageBoxImage.Question => "?",
            MessageBoxImage.Information => "i",
            _ => null,
        };

        if (glyph is null)
        {
            IconDot.Visibility = Visibility.Collapsed;
            return;
        }

        IconDot.Background = image switch
        {
            MessageBoxImage.Error => Tokens.Brush("Ui.StatusError"),
            MessageBoxImage.Warning => Tokens.Brush("Ui.StatusWarning"),
            MessageBoxImage.Question => Tokens.Brush("Ui.Accent"),
            _ => Tokens.Brush("Ui.Accent"),
        };

        IconGlyph.Text = glyph;
        IconDot.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 按按钮组生成按钮。
    /// </summary>
    /// <remarks>
    /// ★ 三个按钮的含义要能被看懂：「否」在中文里既可读作"不保存"也可读作"不，别关"，
    /// 所以按钮上写的是**动宾短语**（「不保存」/「保存」），而不是「是」/「否」；
    /// 正文里还会再说明一遍（见 <c>ProjectClosePolicy.PromptText</c>）。
    /// </remarks>
    private void BuildButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("确定", MessageBoxResult.OK, isDefault: true);
                break;

            case MessageBoxButton.OKCancel:
                AddButton("取消", MessageBoxResult.Cancel, isDefault: false);
                AddButton("确定", MessageBoxResult.OK, isDefault: true);
                break;

            case MessageBoxButton.YesNo:
                AddButton("否", MessageBoxResult.No, isDefault: false);
                AddButton("是", MessageBoxResult.Yes, isDefault: true);
                break;

            default:   // YesNoCancel
                AddButton("取消", MessageBoxResult.Cancel, isDefault: false);
                AddButton("不保存", MessageBoxResult.No, isDefault: false);
                AddButton("保存", MessageBoxResult.Yes, isDefault: true);
                break;
        }
    }

    private void AddButton(string caption, MessageBoxResult result, bool isDefault)
    {
        var button = new Button
        {
            Content = caption,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 96,
            IsDefault = isDefault,
            Style = isDefault
                ? (Style)FindResource("Style.DialogPrimaryButton")
                : (Style)FindResource("Style.ToolbarButton"),
        };

        button.Click += (_, _) =>
        {
            Result = result;
            DialogResult = true;   // 关闭对话框（true/false 只表示"对话框关掉了"）
        };

        ButtonRow.Children.Add(button);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch (Exception ex)
        {
            // DragMove 在鼠标已经抬起时会抛，属于正常竞态，不值得打扰用户
            AppLog.Warn($"弹窗拖动失败（可忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// Esc / 右上角关闭：返回「取消」而不是「否」。
    /// </summary>
    /// <remarks>
    /// ★ 这条是整个 S5 里唯一**不能写错**的地方。系统 MessageBox 在有「取消」按钮时，
    /// 按 Esc 返回 <c>Cancel</c>；我们要表现出一模一样的行为，"关掉窗口"就不会被
    /// 误解成"放弃改动"。<see cref="DialogResult"/> 保持 null ⇒ 调用方看到 <c>None</c>，
    /// 而 <c>ProjectClosePolicy.FromMessageBox</c> 把 None 也归「取消」。
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = HasCancelButton() ? MessageBoxResult.Cancel : MessageBoxResult.None;
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private bool HasCancelButton()
    {
        foreach (var child in ButtonRow.Children)
        {
            if (child is Button { Content: string text } && text == "取消") return true;
        }

        return false;
    }
}
