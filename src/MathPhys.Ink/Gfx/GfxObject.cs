using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 画布上一个图形对象的运行期形态。
/// </summary>
/// <remarks>
/// 它是<b>可变的</b>（拖动时每一帧都要改 <see cref="Center"/>），
/// 而对外（插件）只暴露只读视图 <see cref="IGfxObjectRef"/> ——
/// 插件拿不到这个类，就改不了别人的对象。
/// <para>
/// <b>本地坐标的约定</b>（与 <see cref="IGfxObjectRef"/> 的说明同一件事，两处都写是因为它太容易被写错）：
/// 原点在几何中心，本地范围 <c>[-LocalSize/2, +LocalSize/2]</c>。
/// "本地 → 世界" = 缩放 → 旋转 → 平移，全部由 <see cref="GfxTransform"/> 一处给出。
/// </para>
/// </remarks>
internal sealed class GfxObject : IGfxObjectRef
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string PluginName { get; init; }

    public Point Center { get; set; }

    public double RotationDegrees { get; set; }

    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// 本地包围盒。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="IGfxObjectRenderer.Measure"/> 算出来（参数变化时重算），
    /// <b>不</b>从视觉树量 —— 于是没有窗口、没有布局之前也能算命中测试。
    /// </remarks>
    public Size LocalSize { get; set; }

    public Color Color { get; init; } = GfxObjectData.DefaultColorValue;

    /// <summary>
    /// 线宽（世界单位）。
    /// </summary>
    /// <remarks>
    /// 可写是因为<b>绑定</b>：跟随别的对象的图形（如绑了坐标系的函数曲线）要跟着目标的
    /// 缩放一起变粗，否则"图像和坐标轴一样粗"只在没缩放过时成立。
    /// 对插件仍然是只读的（<see cref="IGfxObjectRef.LineWorldWidth"/>）。
    /// </remarks>
    public double LineWorldWidth { get; set; }

    public Dictionary<string, double> Numbers { get; } = new();

    public Dictionary<string, string> Texts { get; } = new();

    /// <summary>
    /// 内容版本号：参数/文本每次被<b>直接合并写入</b>时 +1（M12 函数平移折算用）。
    /// </summary>
    /// <remarks>
    /// 视觉层的重建判据是 LocalSize 变化，但函数曲线的平移折算只改
    /// <c>h/k/b</c> 这类参数 —— 窗口尺寸一个字节都不变，曲线却真的换了形状。
    /// 没有这个版本号，折算后的曲线永远不会重画（表现成"拖了没反应"）。
    /// 走 <c>UpdateNumbers/UpdateTexts</c> 的常规路径不需要它：那条路本来就重算 LocalSize。
    /// </remarks>
    public int ContentVersion { get; set; }

    /// <summary>外接矩形（世界坐标，含旋转与缩放）。</summary>
    public Rect BoundsWorld => GfxTransform.BoundsWorld(this);

    public bool HasNumber(string key) => Numbers.ContainsKey(key);

    public double GetNumber(string key, double fallback = 0)
        => Numbers.TryGetValue(key, out double value) ? value : fallback;

    public string GetText(string key, string fallback = "")
        => Texts.TryGetValue(key, out string? value) ? value : fallback;

    /// <summary>导出成可序列化形态。</summary>
    public GfxObjectData ToData()
    {
        var data = new GfxObjectData
        {
            Id = Id,
            Kind = Kind,
            Plugin = PluginName,
            X = Center.X,
            Y = Center.Y,
            Rotation = RotationDegrees,
            Scale = Scale,
            Color = GfxObjectData.ToHex(Color),
            LineWidth = LineWorldWidth,
        };

        foreach (var pair in Numbers) data.Numbers[pair.Key] = pair.Value;
        foreach (var pair in Texts) data.Texts[pair.Key] = pair.Value;

        return data;
    }

    /// <summary>
    /// 从存档数据重建。
    /// </summary>
    /// <remarks>
    /// 全程不抛异常：单个字段坏掉（颜色写成 <c>"red"</c>、Scale 是 0 或 NaN）
    /// 只让这个对象退回一个能用的默认值。一份存档里有一个坏对象，
    /// 不该导致整份板书打不开 —— 这里宁可用"残缺但看得见"，也不要用"干净利落的失败"。
    /// </remarks>
    public static GfxObject FromData(GfxObjectData data)
    {
        var scale = data.Scale;
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1.0;

        var rotation = data.Rotation;
        if (double.IsNaN(rotation) || double.IsInfinity(rotation)) rotation = 0;

        var x = double.IsNaN(data.X) || double.IsInfinity(data.X) ? 0 : data.X;
        var y = double.IsNaN(data.Y) || double.IsInfinity(data.Y) ? 0 : data.Y;

        var width = data.LineWidth;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) width = 1.5;

        var obj = new GfxObject
        {
            Id = string.IsNullOrEmpty(data.Id) ? GfxObjectStore.NewId() : data.Id,
            Kind = data.Kind ?? string.Empty,
            PluginName = data.Plugin ?? string.Empty,
            Center = new Point(x, y),
            RotationDegrees = rotation,
            Scale = scale,
            Color = GfxObjectData.ParseColor(data.Color) ?? GfxObjectData.DefaultColorValue,
            LineWorldWidth = width,
        };

        foreach (var pair in data.Numbers) obj.Numbers[pair.Key] = pair.Value;
        foreach (var pair in data.Texts) obj.Texts[pair.Key] = pair.Value;

        return obj;
    }
}
