using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MathPhys.Ink.Plugin.SubjectKit;

/// <summary>
/// 学科图片调色板：左侧分类、右侧缩略图卡，点卡片即落画布。
/// </summary>
/// <remarks>
/// 无模式窗口（画布必须保持可点）；面板保持打开支持连续落图 —— 与电路元件调色板同一套路。
/// 浅底深墨的固定配色：这个窗口是「选图用的工具面板」，不接白板主题
/// （电路调色板同款先例，M22 包冒烟也未对它做主题断言）。
/// </remarks>
internal sealed class SubjectPaletteWindow : Window
{
    /// <summary>老师点了某张图（PNG 字节 + 展示名，由工具接手交给宿主 AddImage）。</summary>
    public event Action<byte[], string>? ImagePicked;

    private readonly Brush _cardNormal = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)));
    private readonly Brush _cardHover = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0xE9, 0xFF)));

    /// <summary>缩略图缓存：同一次开窗里重复点击不重复解码。</summary>
    private readonly Dictionary<string, BitmapSource> _thumbCache = new();

    public SubjectPaletteWindow()
    {
        Title = "学科工具 · 图片资料库";
        Width = 760;
        Height = 520;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;

        // 左侧分类列表
        var categoryList = new ListBox
        {
            Width = 128,
            Margin = new Thickness(8),
            FontSize = 14.5,
            FontFamily = new FontFamily("Microsoft YaHei"),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        foreach (var (category, _) in SubjectCatalog.All)
        {
            categoryList.Items.Add(category);
        }
        categoryList.SelectedIndex = 0;

        // 右侧缩略图区（滚动 + 换行）
        var cardPanel = new WrapPanel { Margin = new Thickness(4) };
        var scroll = new ScrollViewer
        {
            Content = cardPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 8, 8, 8),
        };

        var hint = new TextBlock
        {
            Text = "点选图片 → 自动落到卷面中央（可拖动、缩放到题目旁边）；可连续放多张。",
            FontSize = 12,
            Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))),
            Margin = new Thickness(12, 0, 12, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        categoryList.SelectionChanged += (_, _) =>
        {
            if (categoryList.SelectedIndex < 0
                || categoryList.SelectedIndex >= SubjectCatalog.All.Count) return;
            var (_, images) = SubjectCatalog.All[categoryList.SelectedIndex];
            FillCards(cardPanel, images);
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                hint,       // Dock: Bottom
                categoryList,  // Dock: Left
                scroll,     // Fill
            },
        };
        DockPanel.SetDock(hint, Dock.Bottom);
        DockPanel.SetDock(categoryList, Dock.Left);

        FillCards(cardPanel, SubjectCatalog.All[0].Images);
    }

    // ---------------------------------------------------------------- 卡片

    private void FillCards(WrapPanel panel, IReadOnlyList<SubjectImage> images)
    {
        panel.Children.Clear();

        foreach (var image in images)
        {
            panel.Children.Add(MakeCard(image));
        }
    }

    private UIElement MakeCard(SubjectImage image)
    {
        var thumb = new Image
        {
            Width = 168,
            Height = 126,
            Stretch = Stretch.Uniform,
            Source = LoadThumb(image.ResourceName),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = image.Name,
            FontSize = 13,
            FontFamily = new FontFamily("Microsoft YaHei"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
            ToolTip = image.Tip,
        };

        var stack = new StackPanel { Children = { thumb, label }, Margin = new Thickness(2) };

        var card = new Border
        {
            Child = stack,
            Background = _cardNormal,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 8, 6, 6),
            Margin = new Thickness(4),
            Cursor = Cursors.Hand,
            ToolTip = image.Tip,
        };

        card.MouseEnter += (_, _) => card.Background = _cardHover;
        card.MouseLeave += (_, _) => card.Background = _cardNormal;
        card.MouseLeftButtonUp += (_, _) =>
        {
            byte[]? png = ReadResource(image.ResourceName);
            if (png is null)
            {
                MessageBox.Show(
                    "这张图片没有取出来（嵌入资源缺失：" + image.ResourceName + "）。",
                    "学科工具", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ImagePicked?.Invoke(png, "电场线·" + image.Name);
        };

        return card;
    }

    // ---------------------------------------------------------------- 资源

    /// <summary>读嵌入 PNG（本插件程序集的清单资源）。</summary>
    private static byte[]? ReadResource(string resourceName)
    {
        try
        {
            var asm = typeof(SubjectPaletteWindow).Assembly;
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>解码缩略图（OnLoad + Freeze：不占 UI 线程之外的依赖，可跨线程复用）。</summary>
    private BitmapSource? LoadThumb(string resourceName)
    {
        if (_thumbCache.TryGetValue(resourceName, out var cached)) return cached;

        BitmapSource? result = null;
        byte[]? png = ReadResource(resourceName);
        if (png is not null)
        {
            try
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(png);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                result = bmp;
            }
            catch (Exception)
            {
                result = null;
            }
        }

        _thumbCache[resourceName] = result!;
        return result;
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }
}
