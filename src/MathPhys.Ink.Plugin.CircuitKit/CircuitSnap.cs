using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.CircuitKit;

/// <summary>一次引脚命中的结果：吸到了谁（对象 Id）、哪个引脚（序号）、吸到哪（世界坐标）。</summary>
/// <remarks>
/// "吸到的是谁"必须记下来 —— 导线跟随（<see cref="CircuitRenderer.TrySolve"/>）靠它认亲：
/// 只有坐标没有身份，元件一挪导线就不知道该跟谁走。
/// </remarks>
public readonly record struct PinHit(string ObjectId, int PinIndex, Point World);

/// <summary>
/// 电路吸附的<b>纯函数</b>模块：引脚最近点查找、放元件的引脚对齐。
/// </summary>
/// <remarks>
/// <para>
/// 为什么自己写查找而不是用宿主 <c>IBoardQuery.SnapToGfx</c>：
/// 那个接口只回答"最近的目标点在哪"，不回答"吸到的是<b>谁</b>" ——
/// 而导线跟随恰恰需要身份（对象 Id + 引脚序号）。好在
/// <see cref="IGfxObjectHost.Objects"/> 本来就给全量对象，
/// 元件引脚又是纯几何（位姿三件套 Center/Rotation/Scale 一算就出来），
/// 于是插件内自洽解决，宿主一行不用动。
/// </para>
/// <para>
/// 与量角器 / 矢量箭头的吸附同一个纪律：<b>纯函数、无状态、每帧可调</b>；
/// public 是给验收 harness 用的 —— 没有窗口也能把判据一条条钉死。
/// </para>
/// </remarks>
public static class CircuitSnap
{
    /// <summary>吸附半径（屏幕 DIP）。取手柄命中半径同款 12：
    /// 一体机上手指触点是"胖"的（M4.2 实测结论），半径太小表现为"点了吸不上"。</summary>
    public const double RadiusPixels = 12.0;

    /// <summary>
    /// 在 <paramref name="world"/> 附近找最近的元件引脚（半径内没有返回 <c>false</c>）。
    /// </summary>
    /// <remarks>
    /// 导线对象自己<b>不</b>是吸附目标（导线接导线要 T 形节点语义，MVP 不做）；
    /// 未知元件（占位框）没有引脚定义，同样跳过。
    /// </remarks>
    public static bool TryFindPin(IReadOnlyList<IGfxObjectRef> objects, Point world, double radiusWorld, out PinHit hit)
    {
        hit = default;
        if (!(radiusWorld > 0) || double.IsNaN(radiusWorld) || double.IsInfinity(radiusWorld)) return false;

        double best = radiusWorld;
        bool found = false;

        foreach (var obj in objects)
        {
            if (obj is null || CircuitRenderer.IsWire(obj)) continue;

            var pins = CircuitRenderer.PinsOf(obj);
            if (pins.Length == 0) continue;

            var matrix = PinMatrixOf(obj);
            for (int i = 0; i < pins.Length; i++)
            {
                // 引脚世界坐标（含旋转、缩放与平移；一个对象只建一次矩阵，
                // 元件数 × 引脚数通常 < 100，拖动每帧全算也绰绰有余）
                var rotated = matrix.Transform(pins[i]);
                var pinWorld = new Point(obj.Center.X + rotated.X, obj.Center.Y + rotated.Y);

                double distance = (pinWorld - world).Length;
                if (distance < best)
                {
                    best = distance;
                    hit = new PinHit(obj.Id, i, pinWorld);
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// 放元件时的引脚对齐：找"新元件某个引脚 ↔ 已有元件某个引脚"距离最近的组合，
    /// 半径内就返回让两个引脚重合的中心点。
    /// </summary>
    /// <remarks>
    /// 判据是<b>引脚到引脚</b>的距离，不是落点到目标的距离 —— 大元件（滑动变阻器 64 点宽）
    /// 如果按中心距离判，引脚明明能对上却吸不上，表现成"忽近忽远"。
    /// 新元件落下时旋转 0、缩放 1，自己的引脚就在 落点 + 本地偏移 处。
    /// </remarks>
    public static bool TryAlignSymbol(
        CircuitSymbols.Def def, IReadOnlyList<IGfxObjectRef> objects,
        Point dropWorld, double radiusWorld, out Point center)
    {
        center = dropWorld;
        if (def.PinOffsets.Length == 0) return false;

        double best = radiusWorld;
        bool found = false;

        foreach (var obj in objects)
        {
            if (obj is null || CircuitRenderer.IsWire(obj)) continue;

            var theirPins = CircuitRenderer.PinsOf(obj);
            if (theirPins.Length == 0) continue;

            var matrix = PinMatrixOf(obj);
            foreach (var theirPinLocal in theirPins)
            {
                var rotated = matrix.Transform(theirPinLocal);
                var theirPin = new Point(obj.Center.X + rotated.X, obj.Center.Y + rotated.Y);

                foreach (var myPin in def.PinOffsets)
                {
                    // 新元件引脚此刻在 落点 + myPin；要让两个引脚重合，中心应放在 theirPin - myPin
                    // （WPF 的 Point 没有 + 重载，手写分量加法）
                    var myPinWorld = new Point(dropWorld.X + myPin.X, dropWorld.Y + myPin.Y);
                    double distance = (myPinWorld - theirPin).Length;
                    if (distance < best)
                    {
                        best = distance;
                        center = new Point(theirPin.X - myPin.X, theirPin.Y - myPin.Y);
                        found = true;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>对象位姿矩阵（本地 → 世界）：与 CircuitRenderer 里同一套纯数学（缩放 → 旋转，无平移）。</summary>
    private static Matrix PinMatrixOf(IGfxObjectRef obj)
    {
        var matrix = Matrix.Identity;
        matrix.Scale(obj.Scale, obj.Scale);
        matrix.Rotate(obj.RotationDegrees);
        return matrix;
    }
}
