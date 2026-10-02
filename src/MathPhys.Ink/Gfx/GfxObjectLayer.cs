using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 把对象集合变成画布上的矢量视觉 —— <b>只做装配，不做几何</b>。
/// </summary>
/// <remarks>
/// 三种情况全在这里收口：
/// <list type="bullet">
/// <item>有渲染器 ⇒ 用它产出的视觉树；</item>
/// <item>没渲染器（缺插件）⇒ 占位框，数据不丢；</item>
/// <item>渲染器抛异常 ⇒ 占位框 + 日志，只损失这一个对象。</item>
/// </list>
///
/// <para><b>每个对象只建一次视觉，之后拖动只改矩阵。</b>
/// 这是流畅度的命门：拖动时若每帧重建 <c>StreamGeometry</c>，
/// 坐标系那种几百个顶点的图形在一体机上必然掉帧。</para>
///
/// <para>
/// 图层整体 <c>IsHitTestVisible=false</c>（见 XAML）：命中测试由
/// <see cref="GfxObjectStore.HitTest"/> 自己算，图形绝不参与 WPF 命中测试。
/// 否则就会出现 M7.3 那种"看着笔尖在纸上，却写不出字"。
/// </para>
/// </remarks>
public sealed class GfxObjectLayer
{
    private readonly Canvas _host;
    private readonly GfxObjectStore _store;
    private readonly GfxRendererCatalog _catalog;

    /// <summary>对象 Id → 视觉元素。</summary>
    private readonly Dictionary<string, FrameworkElement> _visuals = new(StringComparer.Ordinal);

    /// <summary>
    /// 对象 Id → 上次建视觉时的<b>几何签名</b>。
    /// </summary>
    /// <remarks>
    /// ★ 视觉的重建判据。原先只看"视觉在不在"，于是<b>参数变了不会重画</b>：
    /// <see cref="Sync"/> 会走进 <c>ApplyPose</c>（只改容器宽高与矩阵），
    /// 而渲染器在 <c>CreateVisual</c> 里产出的几何（曲线顶点、箭头长度…）一直是旧的。
    /// <para>
    /// 函数图像没暴露这个洞（还没有参数面板），<b>合力一上来就会暴露</b>：
    /// 分矢量被拖走时，合力对象<b>自己</b>的参数一个字节都没变（宿主只重算了它的 LocalSize），
    /// 于是"参数变"这条路走不到 —— 表现成"分矢量拖走了，合力线还钉在原地"。
    /// </para>
    /// <para>
    /// 签名取 <see cref="IGfxObjectRef.LocalSize"/>：它由渲染器的 <c>Measure</c> 算出，
    /// 而 <c>Measure</c> 与 <c>CreateVisual</c> 读的是同一份参数 ⇒
    /// 凡是能改变"画出来长什么样"的变动，必然先改变尺寸。
    /// 反过来，纯位姿变化（拖动/旋转/缩放）<b>不会</b>改变 LocalSize ⇒ 拖动仍然只改矩阵、不重建几何，
    /// 流畅度的命门没有被破坏。
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, Size> _geometry = new(StringComparer.Ordinal);

    /// <summary>
    /// 对象 Id → 上次建视觉时的<b>内容版本号</b>（<see cref="GfxObject.ContentVersion"/>）。
    /// </summary>
    /// <remarks>
    /// M12 补的第二个重建判据：函数曲线的平移折算只改参数、不改窗口尺寸，
    /// 只看 LocalSize 永远不会重画 —— 版本号由写入方（<see cref="GfxObjectStore"/> 的折算路径）
    /// 递增，这里发现版本变了就整棵重建。
    /// </remarks>
    private readonly Dictionary<string, int> _contentVersions = new(StringComparer.Ordinal);

    public GfxObjectLayer(Canvas host, GfxObjectStore store, GfxRendererCatalog catalog)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        _store.Changed += (_, _) => Sync();
        Sync();
    }

    /// <summary>当前视觉元素个数（验收：应与对象数相等）。</summary>
    public int VisualCount => _visuals.Count;

    /// <summary>某个对象对应的视觉元素；没有时返回 <c>null</c>（harness 断言变换链用）。</summary>
    public FrameworkElement? VisualFor(string objectId)
        => _visuals.TryGetValue(objectId, out var visual) ? visual : null;

    /// <summary>
    /// 让视觉与对象集合一致：删掉多余的、补上缺的、刷新所有位姿。
    /// </summary>
    /// <remarks>
    /// 做成"每次整体对账"而不是增量维护：对象是几十个量级，对账成本可以忽略，
    /// 而增量维护一旦漏掉某条路径，就会留下"删掉的对象还画在屏幕上"这种鬼影 ——
    /// 那种 bug 只能靠肉眼发现，代价远大于这点开销。
    /// </remarks>
    public void Sync()
    {
        var alive = new HashSet<string>(StringComparer.Ordinal);

        int index = 0;
        string? selectedId = _store.SelectedId;

        foreach (var obj in _store.Objects)
        {
            alive.Add(obj.Id);

            bool sizeChanged = _geometry.TryGetValue(obj.Id, out var cachedSize)
                            && cachedSize != obj.LocalSize;
            // ContentVersion 是宿主内部形态（GfxObject）的成员，契约视图上没有
            int contentVersion = obj is GfxObject g ? g.ContentVersion : 0;
            _contentVersions.TryGetValue(obj.Id, out var cachedVersion);
            bool contentChanged = cachedVersion != contentVersion;

            if (!_visuals.TryGetValue(obj.Id, out var visual))
            {
                visual = Build(obj);
                _visuals[obj.Id] = visual;
                _host.Children.Add(visual);
                _geometry[obj.Id] = obj.LocalSize;
                _contentVersions[obj.Id] = contentVersion;
            }
            else if (sizeChanged || contentChanged)
            {
                // 几何签名变了 ⇒ 渲染器读了新参数，必须重新产一遍视觉。
                // 只改宽高是不够的：几何内容是渲染器在 OnRender/CreateVisual 里定的。
                _host.Children.Remove(visual);

                visual = Build(obj);
                _visuals[obj.Id] = visual;
                _host.Children.Add(visual);
                _geometry[obj.Id] = obj.LocalSize;
                _contentVersions[obj.Id] = contentVersion;
            }

            ApplyPose(visual, obj);

            // 列表顺序 = Z 序。用显式 ZIndex 而不是依赖子元素顺序：
            // 撤销/重做会让对象反复"消失又出现"，子元素顺序会漂；列表顺序不会。
            // 选中项给一个显示层的高 Z（只影响观感，不改数据顺序）。
            Panel.SetZIndex(visual, string.Equals(obj.Id, selectedId, StringComparison.Ordinal) ? index + 1000 : index);

            index++;
        }

        // 反向对账：集合里没有的，视觉也必须没有
        if (_visuals.Count > alive.Count)
        {
            var stale = new List<string>();
            foreach (var pair in _visuals)
            {
                if (!alive.Contains(pair.Key)) stale.Add(pair.Key);
            }

            foreach (var id in stale)
            {
                _host.Children.Remove(_visuals[id]);
                _visuals.Remove(id);
                _geometry.Remove(id);
                _contentVersions.Remove(id);
            }
        }
    }

    /// <summary>重建某个对象的视觉（参数变化、或渲染器刚被注册进来时用）。</summary>
    public void Rebuild(string objectId)
    {
        if (!_visuals.TryGetValue(objectId, out var existing)) return;

        var obj = _store.FindObject(objectId);
        if (obj is null) return;

        int z = Panel.GetZIndex(existing);
        _host.Children.Remove(existing);
        _visuals.Remove(objectId);

        var rebuilt = Build(obj);
        _visuals[obj.Id] = rebuilt;
        _geometry[obj.Id] = obj.LocalSize;
        _contentVersions[obj.Id] = obj.ContentVersion;
        _host.Children.Add(rebuilt);
        Panel.SetZIndex(rebuilt, z);

        ApplyPose(rebuilt, obj);
    }

    /// <summary>建一个对象的视觉：一个容器 + 渲染器的内容。</summary>
    private FrameworkElement Build(IGfxObjectRef obj)
    {
        var wrapper = new Canvas
        {
            Width = obj.LocalSize.Width,
            Height = obj.LocalSize.Height,
            IsHitTestVisible = false,

            // ★ 旋转/缩放绕元素自身中心发生 ⇒ 与 GfxTransform.PoseMatrix 的语义一致
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new MatrixTransform(GfxTransform.PoseMatrix(obj)),
        };

        var content = CreateContent(obj);

        // 渲染器的内容以"自己的原点 = 对象中心"作画，所以把它的原点摆到容器的中心
        Canvas.SetLeft(content, obj.LocalSize.Width / 2.0);
        Canvas.SetTop(content, obj.LocalSize.Height / 2.0);
        wrapper.Children.Add(content);

        return wrapper;
    }

    private FrameworkElement CreateContent(IGfxObjectRef obj)
    {
        var renderer = _catalog.Find(obj.Kind);

        if (renderer is null) return GfxPlaceholder.Create(obj);

        // ★ 建视觉前先把画布递过去：合力箭头的几何要从分矢量当场算出来
        //   （与 GfxObjectStore.MeasureSize 里那一次是同一个理由 —— 量尺寸要、画图也要）。
        if (renderer is IGfxBoardAwareRenderer aware)
        {
            try
            {
                aware.SetBoard(_store.Objects);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"渲染器 {renderer.GetType().Name} 接收画布快照时抛异常，已忽略："
                            + $"{ex.GetType().Name} {ex.Message}");
            }
        }

        try
        {
            return renderer.CreateVisual(obj);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"渲染器 {renderer.GetType().Name}（{obj.Kind}）画图时抛异常，已降级为占位框："
                        + $"{ex.GetType().Name} {ex.Message}");
            return GfxPlaceholder.Create(obj);
        }
    }

    /// <summary>
    /// 施加位姿。
    /// </summary>
    /// <remarks>
    /// 位置用 <c>Canvas.Left/Top</c>（世界坐标，元素左上角 = 中心 − 尺寸/2），
    /// 旋转缩放用 <c>RenderTransform</c>。两段合起来 == <see cref="GfxTransform.LocalToWorld"/>。
    /// </remarks>
    private static void ApplyPose(FrameworkElement visual, IGfxObjectRef obj)
    {
        visual.Width = obj.LocalSize.Width;
        visual.Height = obj.LocalSize.Height;
        visual.RenderTransformOrigin = new Point(0.5, 0.5);
        visual.RenderTransform = new MatrixTransform(GfxTransform.PoseMatrix(obj));

        Canvas.SetLeft(visual, obj.Center.X - obj.LocalSize.Width / 2.0);
        Canvas.SetTop(visual, obj.Center.Y - obj.LocalSize.Height / 2.0);
    }
}
