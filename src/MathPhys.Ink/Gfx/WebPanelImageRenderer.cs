using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MathPhys.Ink.Design;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 仿真截图（<c>webPanelImage</c>）的画法 —— <b>宿主侧</b>图形 Kind。
/// </summary>
/// <remarks>
/// <para>
/// 数据约定（与坐标系等插件 Kind 同一套表达法，存档零改动）：
/// <list type="bullet">
/// <item><c>Texts["imageId"]</c> —— 位图 Id，字节在 <see cref="WebPanelImageStore"/> 与
/// <c>.twb</c> 的 <c>images/</c> 段里；</item>
/// <item><c>Numbers["w"]</c> / <c>Numbers["h"]</c> —— 世界尺寸（PDF point，即 1/72 英寸）。</item>
/// </list>
/// </para>
/// <para>
/// ★★ <b>尺寸必须存在对象里，不能在渲染时从位图量。</b>三个理由，缺一条都足够：
/// <list type="number">
/// <item><see cref="Measure"/> 必须是<b>纯函数</b>（命中测试、选中框、拖动吸附都要它，
/// 而这些在没有窗口、没有位图解码的 harness 里也要能算）；</item>
/// <item>位图可能<b>不在</b>（存档缺图、面板刚导完就断电），那时尺寸不能变成 0 ——
/// 对象会退化成一个点，老师点不中也删不掉；</item>
/// <item>世界尺寸与像素尺寸是两回事，混在一起会写出一堆"在不同缩放下大小不同"的鬼故事。</item>
/// </list>
/// </para>
/// <para>
/// ★ 视觉树里的元素<b>不能</b>依赖 <c>ActualWidth</c>：图形层的容器是
/// <see cref="Canvas"/>，它按子元素的 <c>DesiredSize</c> 排版，而这类渲染器约定
/// <c>MeasureOverride</c> 返回 <c>(0,0)</c>（原点 = 对象中心）⇒ <c>ActualWidth</c> 恒为 0。
/// 所以本文件里所有几何都按 <see cref="SizeOf"/> 算出来的尺寸画。
/// </para>
/// </remarks>
public sealed class WebPanelImageRenderer : IGfxObjectRenderer
{
    /// <summary>对象种类。</summary>
    public const string KindName = "webPanelImage";

    /// <summary>位图 Id 的文本参数键。</summary>
    public const string ImageIdKey = "imageId";

    /// <summary>世界宽度的数值键。</summary>
    public const string WidthKey = "w";

    /// <summary>世界高度的数值键。</summary>
    public const string HeightKey = "h";

    /// <summary>缺尺寸时的兜底宽度（世界单位 ≈ 148 mm，投到 1280×720 上看得清）。</summary>
    public const double FallbackWidth = 420.0;

    /// <summary>缺尺寸时的兜底高度。</summary>
    public const double FallbackHeight = 300.0;

    /// <summary>最小边长：再小就没法用手指点中了。</summary>
    public const double MinSide = 24.0;

    /// <summary>
    /// 位图名字的文本参数键（M22 S3）。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="MathPhys.Ink.Plugins.IGfxObjectHost.AddImage"/> 写入，
    /// 只影响"状态栏怎么念这张图"。缺省是空串 —— 那时退回按 Kind 念。
    /// </remarks>
    public const string LabelKey = "label";

    /// <summary>
    /// 插件自己落位图时的<b>标准世界宽度</b>（像素长宽比照抄，绝不拉伸）。
    /// </summary>
    /// <remarks>
    /// Web 面板那条路用「页宽的 46%」—— 它知道自己在哪一页上；而
    /// <see cref="MathPhys.Ink.Plugins.IGfxObjectHost.AddImage"/> 这条路的调用方
    /// （原生仿真窗）<b>看不到页</b>：契约给它的只有"放一张图"。所以这里给一个与纸张无关的定值。
    /// <para>
    /// 380 世界点 ≈ 134 mm —— A4 页宽（595）的 64%，落在卷面上是一张"看得清细节、
    /// 又不至于把题干全糊住"的图；要更大更小用选择工具整体缩放。
    /// </para>
    /// </remarks>
    public const double NominalWorldWidth = 380.0;

    private readonly WebPanelImageStore _images;

    /// <summary>
    /// 建渲染器。
    /// </summary>
    /// <param name="images">位图仓库（同一个实例必须与保存/装载用的是同一个）。</param>
    public WebPanelImageRenderer(WebPanelImageStore images)
        => _images = images ?? throw new ArgumentNullException(nameof(images));

    public string Kind => KindName;

    /// <summary>取位图 Id（空串 = 这个对象没绑到位图）。</summary>
    public static string ImageIdOf(IGfxObjectRef obj) => obj.GetText(ImageIdKey, string.Empty);

    /// <summary>取位图名字（空串 = 这张图没起名，界面应退回按 Kind 念）。</summary>
    public static string LabelOf(IGfxObjectRef obj) => obj.GetText(LabelKey, string.Empty);

    /// <summary>
    /// 对象的世界尺寸（纯函数：只吃对象参数）。
    /// </summary>
    public static Size SizeOf(IGfxObjectRef obj)
    {
        double w = Sanitize(obj.GetNumber(WidthKey, FallbackWidth), FallbackWidth);
        double h = Sanitize(obj.GetNumber(HeightKey, FallbackHeight), FallbackHeight);
        return new Size(w, h);
    }

    /// <summary>
    /// 由像素尺寸算世界尺寸：按页面宽度的比例定宽，长宽比照抄像素（<b>绝不拉伸</b>）。
    /// </summary>
    /// <param name="pixelWidth">位图像素宽。</param>
    /// <param name="pixelHeight">位图像素高。</param>
    /// <param name="targetWidthWorld">目标世界宽度。</param>
    /// <remarks>
    /// 单独抽出来是为了让 harness 能断言"比例守恒"这条 —— 它是这个功能里唯一一处
    /// 会被肉眼一眼看出不对的地方（圆成了椭圆）。
    /// </remarks>
    public static Size WorldSizeFor(int pixelWidth, int pixelHeight, double targetWidthWorld)
    {
        double target = Sanitize(targetWidthWorld, FallbackWidth);

        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return new Size(target, target * FallbackHeight / FallbackWidth);
        }

        return new Size(target, target * pixelHeight / (double)pixelWidth);
    }

    public Size Measure(IGfxObjectRef obj) => SizeOf(obj);

    public FrameworkElement CreateVisual(IGfxObjectRef obj)
    {
        var size = SizeOf(obj);

        return new WebPanelImageVisual(size, _images.Get(ImageIdOf(obj)));
    }

    private static double Sanitize(double value, double fallback)
        => double.IsNaN(value) || double.IsInfinity(value) || value < MinSide ? fallback : value;

    // ---------------------------------------------------------------- 视觉

    /// <summary>
    /// 一块位图（缺图时画成虚线占位框）。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="Canvas"/> + 显式宽高（而不是 <c>MeasureOverride</c> 返回 0 的
    /// <c>FrameworkElement</c>）：图片的"内容尺寸"就是它的本质，
    /// 顺手把它交给布局，命中框与视觉就自动同源。
    /// </remarks>
    private sealed class WebPanelImageVisual : Canvas
    {
        /// <summary>占位框的虚线。</summary>
        private static readonly DoubleCollection DashPattern = new() { 6, 4 };

        public WebPanelImageVisual(Size size, byte[]? png)
        {
            Width = size.Width;
            Height = size.Height;
            IsHitTestVisible = false;

            var bitmap = Decode(png);

            if (bitmap is not null)
            {
                var image = new Image
                {
                    Source = bitmap,
                    Width = size.Width,
                    Height = size.Height,

                    // Fill：尺寸已经按长宽比算好了，再 Uniform 只会多出一圈空白边
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                };

                Place(image, size);
                Children.Add(image);
                return;
            }

            Children.Add(BuildMissingFrame(size));
        }

        /// <summary>位图缺失（存档缺图 / 字节坏了）时的占位框：虚线 + 一行说明。</summary>
        private static FrameworkElement BuildMissingFrame(Size size)
        {
            var frame = new Rectangle
            {
                Width = size.Width,
                Height = size.Height,
                Stroke = Tokens.Brush("Canvas.PlaceholderStroke"),
                StrokeThickness = 1.5,
                StrokeDashArray = DashPattern,
                Fill = Tokens.Brush("Canvas.PlaceholderFill"),
                IsHitTestVisible = false,
            };

            Place(frame, size);

            var label = new TextBlock
            {
                Text = "仿真截图缺失",
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Tokens.Brush("Canvas.PlaceholderStroke"),
                IsHitTestVisible = false,
                TextAlignment = TextAlignment.Center,
                Width = size.Width,
            };

            // 文本框自己会被布局居中，所以左右各退半个框宽、上下各退半个文本框高
            Canvas.SetLeft(label, -size.Width / 2.0);
            Canvas.SetTop(label, -8);
            Canvas.SetZIndex(label, 1);

            var canvas = new Canvas
            {
                Width = size.Width,
                Height = size.Height,
                IsHitTestVisible = false,
            };

            canvas.Children.Add(frame);
            canvas.Children.Add(label);
            return canvas;
        }

        /// <summary>把元素摆到"以原点为中心"的位置（本地坐标原点 = 对象中心）。</summary>
        private static void Place(FrameworkElement element, Size size)
        {
            Canvas.SetLeft(element, -size.Width / 2.0);
            Canvas.SetTop(element, -size.Height / 2.0);
        }

        /// <summary>
        /// 把 PNG 字节解成位图。
        /// </summary>
        /// <remarks>
        /// <c>OnLoad</c> 这条不能省：默认的 <c>OnDemand</c> 会让位图一直<b>引用着那个流</b>，
        /// 而流是就地 <c>MemoryStream</c> —— 于是每张截图都多留一份字节在托管堆上，
        /// 而且要到 GC 才松手。
        /// </remarks>
        private static BitmapImage? Decode(byte[]? png)
        {
            if (png is not { Length: > 0 }) return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = new MemoryStream(png, writable: false);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                // 字节坏了是预期内的事（外部工具改过包、传输截断）⇒ 降级成占位框，不往上抛
                AppLog.Warn($"仿真截图解码失败，已降级为占位框：{ex.GetType().Name} {ex.Message}");
                return null;
            }
        }
    }
}
