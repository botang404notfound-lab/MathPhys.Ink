using System.Collections.Generic;
using System.Windows;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 吸附目标收集器 —— 宿主递给 <see cref="ISnapTargetProvider"/> 的"产出登记口"。
/// </summary>
/// <remarks>
/// 用收集器而不是让提供者自己返回大集合：网格交点可能有几百个，
/// 收集器让宿主有机会在途中做上限与"离得远就别收"的剪枝，避免每次拖动都物化一张大表。
/// </remarks>
public interface ISnapTargetCollector
{
    /// <summary>登记一个吸附点（世界坐标）。</summary>
    void AddPoint(Point world);

    /// <summary>登记一条吸附线段（世界坐标，两点；轴线这类"吸到线上最近点"的目标用它）。</summary>
    void AddLine(Point from, Point to);
}

/// <summary>
/// 可选能力："我这种图形能给工具提供<b>吸附目标</b>"（坐标系网格点、坐标轴……）。
/// </summary>
/// <remarks>
/// <para>
/// 由<b>渲染器</b>实现（宿主遍历画布对象 → 按 Kind 找渲染器 → <c>is ISnapTargetProvider</c> 探测）。
/// 这是 M12 智能吸附的唯一扩展点：<b>只加新接口</b>，不改任何已有契约成员 ——
/// 没实现它的旧渲染器完全不受影响。
/// </para>
/// <para>
/// 约定：实现必须快（拖动的每一帧都会来问）、必须<b>纯函数</b>（不碰视觉树、不存状态），
/// 抛异常会被宿主吞掉并按"没有吸附目标"处理。
/// </para>
/// </remarks>
public interface ISnapTargetProvider
{
    /// <summary>
    /// 把某个对象提供的吸附目标登记到 <paramref name="collector"/>。
    /// 返回 <c>false</c> 表示该对象当前不提供目标（参数残缺等），宿主直接跳过。
    /// </summary>
    bool TryCollectSnapTargets(IGfxObjectRef obj, ISnapTargetCollector collector);
}
