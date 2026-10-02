using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 题号标记的<b>排序与定位</b>（M12 题号导航的纯逻辑部分，harness 可完整断言）。
/// </summary>
/// <remarks>
/// 排序规则就是老师翻卷子的顺序：<b>页号 → 页内从上到下</b>（同页并列的按 y，再按题号数字）。
/// 标记落点在页面之外（理论上不该发生）排在最后，绝不丢 —— 少一个标记比顺序怪一点严重得多。
/// </remarks>
public static class QuestionMarkIndex
{
    /// <summary>本 Kind 的名字（与 <see cref="QuestionMarkRenderer.Kind"/> 同值，独立常量避免渲染器依赖方向倒置）。</summary>
    public const string KindName = "questionMark";

    /// <summary>
    /// 把画布上的题号标记按"翻卷顺序"排好（只读过滤 + 排序，不改任何对象）。
    /// </summary>
    public static IReadOnlyList<IGfxObjectRef> Ordered(IReadOnlyList<IGfxObjectRef> objects,
                                                       IReadOnlyList<Rect> pageRects)
    {
        var marks = new List<IGfxObjectRef>();
        foreach (var obj in objects)
        {
            if (obj is not null && string.Equals(obj.Kind, KindName, StringComparison.Ordinal))
            {
                marks.Add(obj);
            }
        }

        marks.Sort((a, b) =>
        {
            int pageCompare = PageOf(a, pageRects).CompareTo(PageOf(b, pageRects));
            if (pageCompare != 0) return pageCompare;

            int yCompare = a.Center.Y.CompareTo(b.Center.Y);
            if (yCompare != 0) return yCompare;

            return LabelNumber(a).CompareTo(LabelNumber(b));
        });

        return marks;
    }

    /// <summary>标记落在第几页（0 起）；页外落点返回 <see cref="int.MaxValue"/>（排最后，不丢）。</summary>
    public static int PageOf(IGfxObjectRef mark, IReadOnlyList<Rect> pageRects)
    {
        for (int i = 0; i < pageRects.Count; i++)
        {
            if (pageRects[i].Contains(mark.Center)) return i;
        }

        return int.MaxValue;
    }

    /// <summary>题号文本的数字值；不是纯数字（"12a"、手改过的档）时排在该页最后。</summary>
    public static int LabelNumber(IGfxObjectRef mark)
    {
        string label = mark.GetText(QuestionMarkRenderer.LabelKey, "");
        return int.TryParse(label, out int value) ? value : int.MaxValue;
    }

    /// <summary>下一个该用的题号 = 现有最大数字题号 + 1（没有数字题号时从 1 起）。</summary>
    public static string NextLabel(IReadOnlyList<IGfxObjectRef> objects)
    {
        int max = 0;

        foreach (var obj in objects)
        {
            if (obj is null || !string.Equals(obj.Kind, KindName, StringComparison.Ordinal)) continue;

            int value = LabelNumber(obj);
            if (value != int.MaxValue) max = Math.Max(max, value);
        }

        return (max + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 视口中心此刻最接近哪个标记（返回排序列表里的下标）；一个标记都没有返回 -1。
    /// </summary>
    /// <remarks>
    /// 「上一题/下一题」按钮以它为基准 —— 老师看到的是卷面，导航必须从"现在看到的地方"出发。
    /// </remarks>
    public static int NearestIndex(IReadOnlyList<IGfxObjectRef> ordered, Point world)
    {
        int best = -1;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < ordered.Count; i++)
        {
            double d = (ordered[i].Center - world).LengthSquared;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best;
    }
}
