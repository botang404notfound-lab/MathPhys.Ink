using System.Windows;
using System.Windows.Media;

namespace MathPhys.Ink.Viewport;

/// <summary>
/// 视口：世界坐标（PDF point）与视口坐标（DIP）之间的<b>唯一换算真值源</b>。
/// </summary>
/// <remarks>
/// 约定：<c>WorldToViewport = Translate(OffsetX,OffsetY) ∘ Scale(Scale)</c>，
/// 即先缩放再平移；矩阵构造参数顺序为 (m11, m12, m21, m22, offsetX, offsetY)。
/// <para>
/// 本类<b>只管数学，不碰任何 UI 元素也不处理任何输入事件</b>。
/// 视图侧把 <see cref="WorldToViewport"/> 接到世界层的 RenderTransform 上即可，
/// 无需在这里做任何坐标补偿。
/// </para>
/// </remarks>
public sealed class CanvasViewport
{
    /// <summary>缩放下限。低于此值时页面细到无意义，且位图会浪费内存。</summary>
    public const double MinScale = 0.05;

    /// <summary>缩放上限。再高就该走分档重渲而不是拉伸位图了。</summary>
    public const double MaxScale = 16.0;

    public double Scale { get; private set; } = 1.0;

    public double OffsetX { get; private set; }

    public double OffsetY { get; private set; }

    /// <summary>世界坐标 → 视口坐标 的矩阵。</summary>
    public Matrix WorldToViewport => new(Scale, 0, 0, Scale, OffsetX, OffsetY);

    /// <summary>视口变化通知。视图侧据此刷新 RenderTransform。</summary>
    public event EventHandler? Changed;

    /// <summary>视口坐标 → 世界坐标。</summary>
    public Point ToWorld(Point viewportPoint)
    {
        var matrix = WorldToViewport;
        if (!matrix.HasInverse) return viewportPoint;
        matrix.Invert();
        return matrix.Transform(viewportPoint);
    }

    /// <summary>世界坐标 → 视口坐标。</summary>
    public Point ToViewport(Point worldPoint) => WorldToViewport.Transform(worldPoint);

    /// <summary>直接设定视口（缩放会被钳制到合法区间）。</summary>
    public void SetView(double scale, double offsetX, double offsetY)
    {
        Scale = Math.Clamp(scale, MinScale, MaxScale);
        OffsetX = offsetX;
        OffsetY = offsetY;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 视口快照：<see cref="Restore"/> 的入参。
    /// </summary>
    /// <remarks>
    /// 值类型三元组，不带事件、不带矩阵 —— 它的唯一用途就是"原样存下来、原样摆回去"。
    /// 做成具名类型是为了调用处一眼看出这是一份<b>存档</b>，而不是三个散装 double：
    /// 导出这种"临时改视口、事后必须还原"的场景里，参数传错顺序是无声的。
    /// </remarks>
    public readonly record struct ViewSnapshot(double Scale, double OffsetX, double OffsetY);

    /// <summary>取当前视口的快照。</summary>
    public ViewSnapshot Save() => new(Scale, OffsetX, OffsetY);

    /// <summary>
    /// 按快照原样复原视口（等价于把存档里的三个数喂给 <see cref="SetView"/>）。
    /// </summary>
    /// <remarks>
    /// ★ 它<b>会</b>照常触发 <see cref="Changed"/> —— 即使三个数与当前完全相同。
    /// 这是故意的：视图侧靠这个事件把世界层的 RenderTransform 重新对齐，而"数值没变就不通知"
    /// 会让被外部改过、又还原回来的视口在屏幕上不对（屏幕与数学模型脱钩）。
    /// <para>
    /// 反过来，视图侧的「自动适应宽度」逻辑必须能识别"这是用户意图"，
    /// 否则刚复原的视口会被随后的自动适应再算掉一次（M8 S2 踩过，见 docs/06 §9.5）。
    /// </para>
    /// </remarks>
    public void Restore(ViewSnapshot snapshot)
        => SetView(snapshot.Scale, snapshot.OffsetX, snapshot.OffsetY);

    /// <summary>
    /// 回到初始视口（100%、世界原点在左上角）。
    /// </summary>
    /// <remarks>
    /// 换文档 / 新建文档时用。配合"适配宽度只保持视口中心"的语义，它保证了
    /// <b>打开任何一份试卷都从第一页顶部开始</b>，而不是沿用上一份文档的视口位置。
    /// </remarks>
    public void Reset() => SetView(1.0, 0, 0);

    /// <summary>
    /// 以视口上的某一点为锚点缩放——"光标下的内容不动"。
    /// </summary>
    /// <remarks>
    /// 锚点公式：<c>newOffset = p - (p - oldOffset) * k</c>，其中 <c>k = newScale / oldScale</c>。
    /// 若以窗口中心为锚点缩放，老师想看清某道题就得"缩放→再拖回来"，
    /// 一体机上这个体感差异极其明显，所以从第一版就按锚点缩放实现。
    /// </remarks>
    public void ZoomAt(Point viewportAnchor, double factor)
    {
        double oldScale = Scale;
        double newScale = Math.Clamp(oldScale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - oldScale) < 1e-9) return;

        double k = newScale / oldScale;
        SetView(newScale,
                viewportAnchor.X - (viewportAnchor.X - OffsetX) * k,
                viewportAnchor.Y - (viewportAnchor.Y - OffsetY) * k);
    }

    /// <summary>按视口像素平移（拖动手势用）。</summary>
    public void PanBy(double deltaX, double deltaY)
        => SetView(Scale, OffsetX + deltaX, OffsetY + deltaY);

    /// <summary>只改缩放、保持视口中心的内容不动（滚轮/快捷键缩放用）。</summary>
    public void ZoomBy(double factor, Size viewportSize)
        => ZoomAt(new Point(viewportSize.Width / 2, viewportSize.Height / 2), factor);

    /// <summary>
    /// 把一次触摸手势（缩放 + 平移）合成<b>一次</b>视口更新。
    /// </summary>
    /// <remarks>
    /// 语义 = 先以 <paramref name="anchor"/> 为锚点缩放 <paramref name="scaleDelta"/>，
    /// 再把视口平移 <paramref name="translation"/>（两者都在视口坐标系里）。
    /// <para>
    /// 合并成一次是为了只触发一次 <see cref="Changed"/> —— 捏合的每个事件都触发两次刷新，
    /// 视图侧会白白多算一轮渲染需求。
    /// </para>
    /// <para>
    /// 不变量（harness 会断言）：
    /// <c>After.ToViewport(Before.ToWorld(anchor)) == anchor + translation</c>。
    /// 物理含义很直白：<b>手势前恰好位于锚点处的那个内容点，手势后应正好移动到 anchor 平移后的位置</b>。
    /// 这条式子同时约束了"锚点缩放正确"和"先缩放后平移的顺序正确"——顺序反过来就不成立。
    /// </para>
    /// </remarks>
    public void ApplyManipulation(Point anchor, Vector translation, double scaleDelta)
    {
        double oldScale = Scale;
        double newScale = Math.Clamp(oldScale * scaleDelta, MinScale, MaxScale);

        // 缩放被钳制到上下限（或本帧不缩放）时 k=1，自动退化成纯平移，且避免除零
        double k = newScale != oldScale && oldScale > 0 ? newScale / oldScale : 1.0;

        SetView(newScale,
                anchor.X - (anchor.X - OffsetX) * k + translation.X,
                anchor.Y - (anchor.Y - OffsetY) * k + translation.Y);
    }

    /// <summary>
    /// 让世界矩形<b>按宽度铺满</b>视口（内部左右留 <paramref name="padding"/>）；
    /// 竖直方向<b>只保持视口中心的世界 Y 不动</b>，越界时夹紧到内容范围内。
    /// </summary>
    /// <remarks>
    /// 试卷是竖长条，按宽度适配才能让题干字号最大；按"整页装入"会白白浪费两侧空间。
    /// <para>
    /// 竖直方向为什么不"整份文档居中"：7 页试卷按 1000 px 视口适配后世界高约 9660 DIP，
    /// 居中会把视口送到第 4 页 —— 而"打开试卷"这个主用例期望看到<b>第一页顶部</b>。
    /// 改成"保持视口中心"后：刚打开（视口还停在世界原点附近）→ 被夹紧到文档顶，
    /// 正好落在首页顶部；老师看到第 5 页时按 Ctrl+0 → 停在第 5 页，不会被弹回第 1 页。
    /// </para>
    /// </remarks>
    public void FitToWidth(Rect worldBounds, Size viewportSize, double padding)
    {
        if (worldBounds.IsEmpty || worldBounds.Width <= 0) return;
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0) return;

        double available = Math.Max(1.0, viewportSize.Width - padding * 2);

        // ★ 必须<b>先钳到合法区间</b>再算偏移：<see cref="SetView"/> 会把缩放钳住，
        //   而偏移若仍按"没钳过的 scale"算，实际缩放与偏移就对不上 ——
        //   窗口被拖到极窄（或世界特别宽）时 page 会整个偏出视口，还是静默的。
        double scale = Math.Clamp(available / worldBounds.Width, MinScale, MaxScale);

        // 必须在 SetView 之前取旧值：竖直方向要按"旧视口中心看到的世界 Y"来算
        double offsetY = FitVerticalOffset(worldBounds, viewportSize.Height, scale, Scale, OffsetY);

        SetView(scale, padding - worldBounds.X * scale, offsetY);
    }

    /// <summary>
    /// 适配时的竖直偏移：保持"视口中心的世界 Y"不变，再夹紧到内容范围内。
    /// </summary>
    /// <param name="oldScale">改动前的缩放，用来反解当前视口中心的世界 Y。</param>
    /// <param name="oldOffsetY">改动前的竖直平移。</param>
    /// <remarks>
    /// 内容装不下 → 夹紧到 <c>[视口高 - 内容底×scale, -内容顶×scale]</c>，即"视口必须落在内容范围内"，
    /// 避免适配后屏幕上一片空白（那看起来像"文件没打开"）。
    /// 内容装得下 → 竖直居中，与旧的"整份文档居中"行为一致（小文档没有落点问题）。
    /// </remarks>
    private static double FitVerticalOffset(Rect worldBounds, double viewportHeight, double scale,
                                            double oldScale, double oldOffsetY)
    {
        double contentHeight = worldBounds.Height * scale;

        if (contentHeight <= viewportHeight)
        {
            return (viewportHeight - contentHeight) / 2.0 - worldBounds.Y * scale;
        }

        double centerWorldY = oldScale > 0
            ? (viewportHeight / 2.0 - oldOffsetY) / oldScale
            : worldBounds.Y;
        double offsetY = viewportHeight / 2.0 - centerWorldY * scale;

        double minOffsetY = viewportHeight - worldBounds.Bottom * scale;
        double maxOffsetY = -worldBounds.Y * scale;
        return Math.Clamp(offsetY, minOffsetY, maxOffsetY);
    }
}
