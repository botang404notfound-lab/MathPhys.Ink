using System;
using System.Windows;
using System.Windows.Controls;
using MathPhys.Ink.Design;

namespace MathPhys.Ink.Views.Controls;

/// <summary>
/// 回放控制条（M12）：极简四件套 —— 进度、单步、播放/暂停、结束。
/// </summary>
/// <remarks>
/// 与图层面板同一挂法（纯代码、屏幕坐标、状态经回调读写）。刻意不做时间轴拖动：
/// V1 的回放语义是"按书写步骤重演"，拖动时间轴意味着持久化逐点时间戳，收益配不上成本。
/// </remarks>
public sealed class ReplayPanelView : Border
{
    private readonly Action _step;
    private readonly Action _toggleAuto;
    private readonly Action _end;

    private TextBlock _progress = null!;
    private Button _playButton = null!;

    public ReplayPanelView(Action step, Action toggleAuto, Action end)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _toggleAuto = toggleAuto ?? throw new ArgumentNullException(nameof(toggleAuto));
        _end = end ?? throw new ArgumentNullException(nameof(end));

        // ★ M20 步 2b：底板接在设计令牌上（代码版 DynamicResource）
        this.FollowTheme(BackgroundProperty, "Ui.OverlayPanel");
        this.FollowTheme(BorderBrushProperty, "Ui.OverlayBorder");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10, 6, 10, 6);
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Center;
        Margin = new Thickness(0, 0, 0, 16);

        Child = BuildContent();
    }

    private UIElement BuildContent()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        // 必须套文字层级样式：不写 Foreground 的 TextBlock 跟的是 Windows 主题
        // （教室一体机通常是浅色系统 ⇒ 深色浮条上是黑字，看不到"回放到第几步"）
        _progress = new TextBlock
        {
            Style = (Style)FindResource("Style.TextBody"),
            MinWidth = 76,
            Text = "回放 0/0",
        };
        row.Children.Add(_progress);

        row.Children.Add(MakeButton("单步", (_, _) => _step()));
        _playButton = MakeButton("自动播放", (_, _) => _toggleAuto());
        row.Children.Add(_playButton);
        row.Children.Add(MakeButton("结束", (_, _) => _end()));

        return row;
    }

    /// <remarks>
    /// ★ 从 <c>static</c> 改成实例方法：套样式要走 <c>FindResource</c>，那是
    /// <see cref="FrameworkElement"/> 的<b>实例</b>方法，静态上下文里调不到。
    /// </remarks>
    private Button MakeButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("Style.ToolbarButton"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += onClick;
        return button;
    }

    /// <summary>按宿主的回放状态重刷（幂等）。</summary>
    public void Refresh(bool active, int position, int total, bool autoPlay)
    {
        _progress.Text = $"回放 {position}/{total}";
        _playButton.Content = autoPlay ? "暂停" : "自动播放";
        _playButton.IsEnabled = position < total;
    }
}
