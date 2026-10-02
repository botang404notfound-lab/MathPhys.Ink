using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugins;
using MathPhys.Ink.Design;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// "缺少插件"的占位渲染 —— 存档里有这个对象，但没人认识它该长什么样。
/// </summary>
/// <remarks>
/// 这一段是<b>数据安全</b>而不是画法：
/// 老师装插件 A 画了一节课的坐标系，第二天 A 被禁用（或换了台机器没带插件），
/// 那份 <c>.tbink</c> 里的对象<b>绝不能</b>因此消失 —— 一旦保存就把它们抹了，
/// 那等于"打开一次文件毁一次板书"。
/// <para>
/// 所以：画成虚线占位框、标明缺谁、状态栏提示有几个这样的对象、
/// 保存时<b>原样写回</b>。装上插件后它们就自己回来了。
/// </para>
/// </remarks>
internal static class GfxPlaceholder
{
    /// <summary>占位框的本地尺寸（世界单位）。对象尺寸未知，给一个能看得见的大小。</summary>
    public static readonly Size Size = new(140, 88);

    /// <summary>造一个占位视觉：虚线边框 + 中间一行说明。</summary>
    public static FrameworkElement Create(IGfxObjectRef obj)
    {
        var canvas = new Canvas
        {
            Width = Size.Width,
            Height = Size.Height,
            IsHitTestVisible = false,
        };

        var frame = new Rectangle
        {
            Width = Size.Width,
            Height = Size.Height,
            Stroke = Tokens.Brush("Canvas.PlaceholderStroke"),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 6, 4 },
            Fill = Tokens.Brush("Canvas.PlaceholderFill"),
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(frame, -Size.Width / 2.0);
        Canvas.SetTop(frame, -Size.Height / 2.0);
        canvas.Children.Add(frame);

        string who = string.IsNullOrEmpty(obj.PluginName) ? obj.Kind : obj.PluginName;

        var label = new TextBlock
        {
            Text = $"缺少插件：{who}",
            FontFamily = new FontFamily("Microsoft YaHei"),
            // 字号按世界单位给：占位框本身不讲究排版（它只是个"东西还在这儿"的记号），
            // 真要把"缺了哪个插件"说清楚，靠的是状态栏那句提示与日志 —— 那才是权威通道。
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Tokens.Brush("Canvas.PlaceholderStroke"),
            IsHitTestVisible = false,
            TextAlignment = TextAlignment.Center,
        };

        Canvas.SetLeft(label, -Size.Width / 2.0);
        Canvas.SetTop(label, -9);

        canvas.Children.Add(label);
        return canvas;
    }
}
