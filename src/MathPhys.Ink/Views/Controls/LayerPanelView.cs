using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MathPhys.Ink.Design;
using MathPhys.Ink.Ink;

namespace MathPhys.Ink.Views.Controls;

/// <summary>
/// 图层面板（M12）：批注层 / 草稿层的可见性、当前层选择与「清空草稿」。
/// </summary>
/// <remarks>
/// 与参数面板同一挂法：纯代码建 UI、浮在 <c>RootGrid</c> 上的<b>屏幕坐标</b>元素，
/// 状态全部经回调读写 —— 面板自己不持有任何画布状态，<see cref="Refresh"/> 幂等可反复调。
/// <para>
/// 「当前层」的视觉表达 = 该层的「在此层写」按钮<b>置灰</b>（你现在就在这层，按了也是空操作）；
/// 一体机上没有悬停提示，"按钮置灰"是唯一不产生歧义的状态语言。
/// </para>
/// </remarks>
public sealed class LayerPanelView : Border
{
    private readonly Func<string> _getActiveLayer;
    private readonly Func<string, bool> _isLayerVisible;
    private readonly Action<string> _setActiveLayer;
    private readonly Action<string, bool> _setLayerVisible;
    private readonly Action _clearDraft;
    private readonly Action _close;

    private readonly Dictionary<string, Button> _activeButtons = new();
    private readonly Dictionary<string, Button> _eyeButtons = new();

    public LayerPanelView(Func<string> getActiveLayer,
                          Func<string, bool> isLayerVisible,
                          Action<string> setActiveLayer,
                          Action<string, bool> setLayerVisible,
                          Action clearDraft,
                          Action close)
    {
        _getActiveLayer = getActiveLayer ?? throw new ArgumentNullException(nameof(getActiveLayer));
        _isLayerVisible = isLayerVisible ?? throw new ArgumentNullException(nameof(isLayerVisible));
        _setActiveLayer = setActiveLayer ?? throw new ArgumentNullException(nameof(setActiveLayer));
        _setLayerVisible = setLayerVisible ?? throw new ArgumentNullException(nameof(setLayerVisible));
        _clearDraft = clearDraft ?? throw new ArgumentNullException(nameof(clearDraft));
        _close = close ?? throw new ArgumentNullException(nameof(close));

        // ★ M20 步 2b：底板接在设计令牌上（代码版 DynamicResource）。
        //   原先 Tokens.Brush(...) 取的是**一次快照** —— 换肤后面板会留着旧主题的色，
        //   而 XAML 里写 DynamicResource 的兄弟元素都跟着变了，只有它不变。
        this.FollowTheme(BackgroundProperty, "Ui.OverlayPanel");
        this.FollowTheme(BorderBrushProperty, "Ui.OverlayBorder");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12);
        MinWidth = 236;
        Child = BuildContent();
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(4) };

        // 标题行：图层 + 关闭
        var header = new DockPanel();
        var closeButton = new Button
        {
            Content = "×",
            Style = (Style)FindResource("Style.PanelCloseButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeButton.Click += (_, _) => _close();
        header.Children.Add(closeButton);
        // ★ 必须套文字层级样式：本项目**没有**隐式的 TextBlock 样式，MainWindow 也没设 Foreground，
        //   所以不写 Foreground 的 TextBlock 走的是 SystemColors.ControlTextBrush —— 它跟随
        //   **Windows 的主题**而不是本程序的主题。教室一体机通常是浅色系统 ⇒ 深色面板上黑字压黑底。
        header.Children.Add(new TextBlock
        {
            Text = "图层",
            Style = (Style)FindResource("Style.TextTitle"),
        });
        root.Children.Add(header);

        root.Children.Add(BuildLayerRow(InkLayers.AnnotationId));
        root.Children.Add(BuildLayerRow(InkLayers.DraftId));

        var clearButton = new Button
        {
            Content = "清空草稿层（可撤销）",
            Style = (Style)FindResource("Style.ToolbarButton"),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        clearButton.Click += (_, _) =>
        {
            _clearDraft();
            Refresh();
        };
        root.Children.Add(clearButton);

        return root;
    }

    private UIElement BuildLayerRow(string layerId)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        row.Children.Add(new TextBlock
        {
            Text = layerId == InkLayers.DraftId ? "草稿层" : "批注层",
            Style = (Style)FindResource("Style.TextBody"),
            MinWidth = 56,
        });

        var activeButton = new Button
        {
            Content = "在此层写",
            Style = (Style)FindResource("Style.ToolbarButton"),
            Margin = new Thickness(8, 0, 0, 0),
            Tag = layerId,
        };
        activeButton.Click += (_, _) =>
        {
            _setActiveLayer(layerId);
            Refresh();
        };
        row.Children.Add(activeButton);
        _activeButtons[layerId] = activeButton;

        // 可见性切换：按钮文字 = 点它会怎样（与主题/窗口模式按钮同一约定）
        var eyeButton = new Button
        {
            Style = (Style)FindResource("Style.ToolbarButton"),
            Margin = new Thickness(8, 0, 0, 0),
            Tag = layerId,
        };
        eyeButton.Click += (_, _) =>
        {
            _setLayerVisible(layerId, !_isLayerVisible(layerId));
            Refresh();
        };
        row.Children.Add(eyeButton);
        _eyeButtons[layerId] = eyeButton;

        return row;
    }

    /// <summary>按宿主的当前状态重刷（幂等：多调几次只是多画几遍）。</summary>
    public void Refresh()
    {
        string active = _getActiveLayer();

        foreach (var pair in _activeButtons)
        {
            // 当前层的那颗"在此层写"置灰 —— 你已经在这层了
            pair.Value.IsEnabled = !string.Equals(pair.Key, active, StringComparison.Ordinal);
        }

        foreach (var pair in _eyeButtons)
        {
            bool visible = _isLayerVisible(pair.Key);
            pair.Value.Content = visible ? "隐藏" : "显示";
        }
    }
}
