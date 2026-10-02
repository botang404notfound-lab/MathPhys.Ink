using System.Collections.Generic;
using System.Windows;

namespace MathPhys.Ink.Plugin.FunctionPlot.Expr;

/// <summary>
/// 把表达式在 x 区间内采样成<b>多段</b>点序列（数学坐标，1 单位 = 1 世界点）。
/// </summary>
/// <remarks>
/// 多段是因为"奇点 / 定义域外 / 跳变"必须断开：
/// <list type="bullet">
/// <item><c>1/x</c> 在 x=0 附近 ⇒ 生成 2 段不连续路径，<b>绝不能连成一条竖线</b>（函数绘图最经典的坑）；</item>
/// <item><c>sqrt(x)</c> 在 x&lt;0 ⇒ 整段不画（不是画成 0）；</item>
/// <item>相邻两点 y 跳变超过范围跨度的两倍 ⇒ 视为奇点附近，断开（避免 <c>tan(x)</c> 渐近线连成竖线）；</item>
/// <item>y 落到 [yMin,yMax] 之外 ⇒ 该点不画（曲线停在范围内）；</item>
/// <item>采样点数设硬上限，防止 <c>sin(1000x)</c> 这类高频函数把渲染拖垮。</item>
/// </list>
/// <para>
/// ★ <b>分段函数的断点</b>：均匀采样时断点极少正好落在格点上，会把"跳变"画成一段斜坡。
/// 所以先把表达式里的断点（<c>x&lt;0</c> 里的 0）收出来，在每个断点<b>左右各补一个采样点</b>
/// （用极小的 ε 偏移），让原有的"跳变过大 ⇒ 断开"逻辑自己生效 —— 不额外加特例分支。
/// </para>
/// </remarks>
public static class FunctionSampler
{
    /// <summary>采样点数的硬上限（防止高频函数退化成海量点）。</summary>
    public const int MaxSamplesHard = 4000;

    /// <summary>给断点补的极小偏移：小到肉眼当它是断点本身，又足以落在两侧。</summary>
    private const double BreakEpsilon = 1e-7;

    /// <summary>
    /// 采样。返回若干段（每段是连续的数学坐标点列）；没有有效段时返回空列表。
    /// </summary>
    /// <param name="expr">已解析的 AST。</param>
    /// <param name="xMin">x 范围下界（也是定义域下界）。</param>
    /// <param name="xMax">x 范围上界。</param>
    /// <param name="yMin">y 显示范围下界（超出不画）。</param>
    /// <param name="yMax">y 显示范围上界。</param>
    /// <param name="parameters">参数字母 → 取值。</param>
    /// <param name="maxSamples">采样点数上限（默认 <see cref="MaxSamplesHard"/>）。</param>
    public static IReadOnlyList<IReadOnlyList<Point>> Sample(
        ExprNode expr, double xMin, double xMax, double yMin, double yMax,
        IReadOnlyDictionary<string, double> parameters, int? maxSamples = null)
    {
        int max = Math.Clamp(maxSamples ?? MaxSamplesHard, 16, MaxSamplesHard);

        // 采样密度：每数学单位 200 点，至少 64 点，封顶 max
        double span = xMax - xMin;
        int n = span <= 0 ? 64 : (int)Math.Ceiling(span * 200.0);
        n = Math.Clamp(n, 64, max);

        // 断点：收在 [xMin,xMax] 内、且不与两端点重合的那些
        var breaks = new List<double>();
        foreach (var b in ExpressionEvaluator.CollectBreakpoints(expr, parameters))
        {
            if (b > xMin + BreakEpsilon && b < xMax - BreakEpsilon) breaks.Add(b);
        }
        breaks.Sort();

        var segments = new List<List<Point>>();
        var current = new List<Point>();

        double dx = span <= 0 ? 0 : span / n;
        double breakJump = Math.Max(2.0 * (yMax - yMin), 1.0);   // 跳变阈值：范围跨度的两倍
        const double huge = 1e7;                                  // 溢出保护

        void Flush()
        {
            if (current.Count >= 2) segments.Add(current);
            current = new List<Point>();
        }

        void Visit(double x, bool forceBreak = false)
        {
            double y = ExpressionEvaluator.Eval(expr, x, parameters);

            // 奇点 / 溢出 / 定义域外：断开
            if (!double.IsFinite(y) || Math.Abs(y) > huge)
            {
                Flush();
                return;
            }
            // y 超出显示范围：不画该点（曲线停在范围内）
            if (y < yMin || y > yMax)
            {
                Flush();
                return;
            }
            // 与上一点的跳变过大 ⇒ 奇点附近，断开后从当前点重启
            // ★ 或者 forceBreak（已知的解析断点）：分段函数的跳跃可能很小
            //   （{x<0:-1, else:1} 只跳 2，远小于"范围跨度两倍"的阈值），
            //   靠幅值判据永远断不开 —— 会画成一条斜坡（本条踩过一次）。
            //   解析断点是<b>确定的事实</b>，不需要再靠幅值猜。
            if (current.Count > 0)
            {
                double lastY = current[current.Count - 1].Y;
                if (forceBreak || Math.Abs(y - lastY) > breakJump)
                    Flush();
            }
            current.Add(new Point(x, y));
        }

        int nextBreak = 0;
        for (int i = 0; i <= n; i++)
        {
            double x = xMin + dx * i;

            // ★ 条件必须是 "<="（不能写成 "<"）：格点常常正好落在断点上
            //   （x=0 就是 [-2,2] 的第 400 个格点）。若用 "<"，则格点 x=0 会先被
            //   Visit(0) 求值 —— 那属于断点<b>右侧</b>的分支 —— 结果这个 +1 被塞进了
            //   左侧那一段里，段内出现 −1→+1 的斜坡（本条踩过一次）。
            //   用 "<=" 就保证"先断、再访问格点"，断点两侧各归各段。
            while (nextBreak < breaks.Count && breaks[nextBreak] <= x)
            {
                double b = breaks[nextBreak++];
                Visit(b - BreakEpsilon);
                Visit(b + BreakEpsilon, forceBreak: true);   // 右极限必然开新段
            }

            Visit(x);
        }
        Flush();

        return segments;
    }
}
