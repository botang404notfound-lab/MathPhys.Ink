using System;
using System.Collections.Generic;
using System.Windows;

namespace MathPhys.Ink.Plugin.FunctionPlot.Expr;

/// <summary>一个"特征点"：位置 + 类别 + 读数。</summary>
/// <remarks>
/// 纯数据，不带任何 UI。渲染器负责把它画成小圆点 + 文字；
/// harness 只验这份数据（"零点在哪、极值是最大还是最小"），不需要窗口。
/// </remarks>
public sealed class FeaturePoint
{
    /// <summary>数学坐标（1 单位 = 1 世界点，与曲线几何同一套）。</summary>
    public double X { get; init; }

    /// <summary>数学坐标 y。</summary>
    public double Y { get; init; }

    /// <summary>类别。</summary>
    public FeatureKind Kind { get; init; }

    /// <summary>交点类别时的"另一条曲线"标识（<see cref="FeatureKind.Intersection"/> 才有意义）。</summary>
    public string Other { get; init; } = "";

    public Point Point => new(X, Y);
}

/// <summary>特征点类别。</summary>
public enum FeatureKind
{
    /// <summary>零点（曲线与 x 轴的交点）。</summary>
    Zero,

    /// <summary>局部极大值。</summary>
    Maximum,

    /// <summary>局部极小值。</summary>
    Minimum,

    /// <summary>驻点（导数变号但两侧同向，如 x^3 在原点的鞍点）。</summary>
    Inflection,

    /// <summary>与另一条曲线的交点。</summary>
    Intersection,
}

/// <summary>
/// 函数特征点求解：零点 / 极值 / 两条曲线的交点。
/// </summary>
/// <remarks>
/// <b>全部是纯数学，零 UI、零契约改动。</b>做法刻意朴素（不引任何数值库 —— 项目选型明确零新依赖）：
/// <list type="number">
/// <item>在定义域上<b>均匀粗扫</b>，按"符号变化"找出所有可能含解的小区间；</item>
/// <item>对每个小区间做<b>二分法</b>细化到 &lt; 1e-10（比直接上牛顿法稳 —— 牛顿法会在
/// <c>tan(x)</c> 这类陡峭处飞出定义域，而二分只要两端异号就必定收敛）；</item>
/// <item>极值则找<b>导数的变号点</b>（导数用中心差分，步长随范围自适应）。</item>
/// </list>
/// <para>
/// ★ <b>为什么不用符号微分</b>：既要给 <c>abs(x)</c> / <c>{x&lt;0:…}</c> 这类不可导的东西定义导数，
/// 又要处理"解析出导数后表达式爆炸"的问题 —— 中心差分在 1e-6 精度上足够画一个教学用的点，
/// 而代码量差一个数量级。
/// </para>
/// <para>
/// ★ <b>一切都是"尽力而为"</b>：找不到就返回空列表，绝不抛异常、绝不返回垃圾点。
/// 老师的表达式里有 <c>floor</c> / <c>sign</c> 这类阶梯时，没有零点很正常。
/// </para>
/// </remarks>
public static class FeatureFinder
{
    /// <summary>粗扫格点数（区间内均分多少份找变号）。够密才不漏掉小范围内的多个根。</summary>
    private const int CoarseSteps = 2000;

    /// <summary>二分细化次数：每轮折半，200 轮后区间宽度远小于 double 的有效位。</summary>
    private const int BisectIterations = 200;

    /// <summary>导数用的中心差分步长比例（相对区间宽度）。</summary>
    private const double DerivativeStepRatio = 1e-4;

    // ---------------------------------------------------------------- 零点

    /// <summary>
    /// 求 <paramref name="expr"/> 在 <c>[xMin, xMax]</c> 内的零点（与 x 轴的交点）。
    /// </summary>
    /// <param name="expr">已解析的 AST。</param>
    /// <param name="parameters">参数字母 → 取值。</param>
    /// <param name="xMin">左端。</param>
    /// <param name="xMax">右端。</param>
    /// <param name="maxResults">结果上限（防止 <c>sin(1000x)</c> 返回上千个点把画面糊死）。</param>
    public static IReadOnlyList<FeaturePoint> FindZeros(
        ExprNode expr, IReadOnlyDictionary<string, double> parameters,
        double xMin, double xMax, int maxResults = 64)
        => FindRoots(expr, parameters, xMin, xMax, FeatureKind.Zero, maxResults);

    /// <summary>求两条曲线的交点（即 <c>a - b == 0</c> 的根）。</summary>
    /// <remarks>
    /// ★ 返回的 <see cref="FeaturePoint.Y"/> 是<b>曲线在交点处的真实函数值</b>（用 a 算），
    /// 不是残差（残差恒为 0，拿它当 y 会把交点全画到 x 轴上 —— 本条踩过一次）。
    /// </remarks>
    public static IReadOnlyList<FeaturePoint> FindIntersections(
        ExprNode a, ExprNode b, IReadOnlyDictionary<string, double> parameters,
        double xMin, double xMax, string otherId = "", int maxResults = 64)
    {
        var diff = new BinaryNode(a, '-', b);
        var roots = FindRoots(diff, parameters, xMin, xMax, FeatureKind.Intersection, maxResults);

        var tagged = new List<FeaturePoint>(roots.Count);
        foreach (var r in roots)
        {
            double y = ExpressionEvaluator.Eval(a, r.X, parameters);
            if (!double.IsFinite(y)) continue;
            tagged.Add(new FeaturePoint
            {
                X = r.X,
                Y = y,                                  // 真实函数值，不是残差
                Kind = FeatureKind.Intersection,
                Other = otherId,
            });
        }
        return tagged;
    }

    /// <summary>零点与交点的共同实现：符号变化粗扫 + 二分细化。</summary>
    private static IReadOnlyList<FeaturePoint> FindRoots(
        ExprNode expr, IReadOnlyDictionary<string, double> parameters,
        double xMin, double xMax, FeatureKind kind, int maxResults)
    {
        var results = new List<FeaturePoint>();
        if (!(xMax > xMin) || maxResults <= 0) return results;

        double span = xMax - xMin;
        double dx = span / CoarseSteps;

        // ★ 根判定阈值必须与"函数值的尺度"挂钩，不能写死 1e-9：
        //   采样点 x 是 xMin + dx·i 累加出来的，格子不可能精确落在根上
        //   （如 x^2-2x 的根 x=2 实际算出 2.0000000000000004，残差 ~1e-15 是幸运的，
        //    而 sin(x) 这类在根附近残差随 dx 线性增长，写死阈值会漏根）。
        //   这里用"一个格距上的典型函数值变化"当尺度，再取它的 1e-9 倍。
        double yScale = 1.0;
        {
            double probe = 0;
            for (int k = 0; k <= 8; k++)
            {
                double yv = ExpressionEvaluator.Eval(expr, xMin + span * k / 8.0, parameters);
                if (double.IsFinite(yv)) probe = Math.Max(probe, Math.Abs(yv));
            }
            if (probe > 0) yScale = probe;
        }
        double zeroTol = Math.Max(yScale * 1e-9, 1e-12);

        int SignOf(double v) => Math.Abs(v) <= zeroTol ? 0 : (v > 0 ? 1 : -1);

        double prevX = xMin;
        double prevY = ExpressionEvaluator.Eval(expr, prevX, parameters);
        bool prevValid = double.IsFinite(prevY);

        // 恰好落在端点的根
        if (prevValid && SignOf(prevY) == 0)
            AddPoint(results, kind, prevX, 0, maxResults);

        double? prevNonZeroX = prevValid && SignOf(prevY) != 0 ? prevX : null;
        int prevSign = prevValid ? SignOf(prevY) : 0;

        for (int i = 1; i <= CoarseSteps && results.Count < maxResults; i++)
        {
            double x = xMin + dx * i;
            double y = ExpressionEvaluator.Eval(expr, x, parameters);
            bool valid = double.IsFinite(y);

            if (valid)
            {
                int sign = SignOf(y);
                if (sign == 0)
                {
                    // 采样点正好踩在根上
                    AddPoint(results, kind, x, 0, maxResults);
                    prevNonZeroX = null;
                    prevSign = 0;
                }
                else
                {
                    if (prevSign != 0 && sign != prevSign)
                    {
                        // ★ 奇点也会"变号"（1/x 在 0 两侧从 −∞ 跳到 +∞），必须排除：
                        //   真零点两侧的函数值有限且贴近 0；奇点两侧的绝对值都很大。
                        if (Math.Abs(prevY) < 1e5 && Math.Abs(y) < 1e5)
                        {
                            double lo = prevNonZeroX ?? prevX;
                            double root = Bisect(expr, parameters, lo, x,
                                                 ExpressionEvaluator.Eval(expr, lo, parameters), y);
                            if (double.IsFinite(root)) AddPoint(results, kind, root, 0, maxResults);
                        }
                    }
                    prevNonZeroX = x;
                    prevSign = sign;
                }
            }

            prevX = x; prevY = y; prevValid = valid;
        }

        return results;
    }

    private static bool HasSignChange(double a, double b)
        => (a < 0 && b > 0) || (a > 0 && b < 0);

    /// <summary>
    /// 二分法细化。区间已知两端异号，折半到极窄 —— 比牛顿法稳（不会飞出定义域）。
    /// </summary>
    /// <remarks>
    /// 终止条件用<b>区间宽度</b>而不是"|f| 足够小"：函数的斜率可能极陡
    /// （如 <c>100x-200</c>），此时"值为 0"的判定阈值要么太松（停在离根很远的地方）
    /// 要么太紧（永远到不了）。折半 200 次后区间宽度已远小于 double 的有效位，
    /// 直接返回中点就是机器精度下的根。
    /// </remarks>
    private static double Bisect(
        ExprNode expr, IReadOnlyDictionary<string, double> parameters,
        double lo, double hi, double fLo, double fHi)
    {
        // 先把端点是非有限值的情况挡掉（此时无法二分）
        if (!double.IsFinite(fLo) || !double.IsFinite(fHi)) return double.NaN;

        bool loNeg = fLo < 0;
        for (int i = 0; i < BisectIterations; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (hi - lo <= 1e-15) return mid;

            double fMid = ExpressionEvaluator.Eval(expr, mid, parameters);
            if (!double.IsFinite(fMid)) return double.NaN;
            if (fMid == 0) return mid;

            if ((fMid < 0) == loNeg) { lo = mid; fLo = fMid; }
            else { hi = mid; fHi = fMid; }
        }
        return 0.5 * (lo + hi);
    }

    private static void AddPoint(List<FeaturePoint> sink, FeatureKind kind, double x, double y, int maxResults)
    {
        if (sink.Count >= maxResults) return;
        // 去重：同一个根被相邻两格各报一次
        foreach (var p in sink)
            if (Math.Abs(p.X - x) < 1e-7) return;

        sink.Add(new FeaturePoint { X = x, Y = y, Kind = kind });
    }

    // ---------------------------------------------------------------- 极值

    /// <summary>
    /// 求 <paramref name="expr"/> 在 <c>[xMin, xMax]</c> 内的局部极值点。
    /// </summary>
    /// <remarks>
    /// 找的是<b>导数的变号点</b>：导数由正变负 ⇒ 极大；由负变正 ⇒ 极小；
    /// 若导数只是"穿过"而不变号（<c>x^3</c> 在原点），标成 <see cref="FeatureKind.Inflection"/>
    /// —— 它是驻点但不是极值，标出来比漏掉更有教学价值。
    /// </remarks>
    public static IReadOnlyList<FeaturePoint> FindExtrema(
        ExprNode expr, IReadOnlyDictionary<string, double> parameters,
        double xMin, double xMax, int maxResults = 64)
    {
        var results = new List<FeaturePoint>();
        if (!(xMax > xMin) || maxResults <= 0) return results;

        double span = xMax - xMin;
        double h = span * DerivativeStepRatio;
        if (!(h > 0)) h = 1e-6;
        double dx = span / CoarseSteps;

        // ★★ 导数必须用"理查森外推"的中心差分，不能用朴素中心差分 —— 这是本批次最难发现的一处：
        //   朴素差分 D(x,h) = (f(x+h)−f(x−h))/(2h) 的系统偏差是 (h²/6)·f'''(x)，
        //   在驻点处真导数为 0，剩下的偏差就是这个 h² 项。以 h = span×1e-4 = 1e-3 为例，
        //   偏差 ≈ 1e-6（x³ 在原点实测正好 1e-6）—— 比"多少算零"的阈值大两个数量级，
        //   于是 x³ 的驻点在数值上"导数恒为正"，永远逮不到（本条踩过一次）。
        //   用两个步长做一次外推：D_R = (4·D(x,h/2) − D(x,h))/3，误差降到 O(h⁴)，
        //   h² 偏差被<b>精确抵消</b>，驻点处的导数才真的回到 0 附近。
        double Deriv(double x)
        {
            double a1 = ExpressionEvaluator.Eval(expr, x - h, parameters);
            double b1 = ExpressionEvaluator.Eval(expr, x + h, parameters);
            double a2 = ExpressionEvaluator.Eval(expr, x - h / 2.0, parameters);
            double b2 = ExpressionEvaluator.Eval(expr, x + h / 2.0, parameters);
            if (!double.IsFinite(a1) || !double.IsFinite(b1)
                || !double.IsFinite(a2) || !double.IsFinite(b2)) return double.NaN;

            double d1 = (b1 - a1) / (2.0 * h);
            double d2 = (b2 - a2) / h;          // 分母 = 2*(h/2) = h
            return (4.0 * d2 - d1) / 3.0;
        }

        double prevX = xMin;
        double prevD = Deriv(prevX);
        bool prevValid = double.IsFinite(prevD);

        // ★ 不能只比较"相邻两点严格变号"：x^2 的导数在 x=0 处恰好为 0，
        //   于是 (−10 → 0) 与 (0 → +10) 两对都不是严格变号，驻点被整个漏掉（本条踩过一次）。
        //   做法：把导数取"带容差的符号"（|d| 小于阈值即记为 0），跨过连续 0 段后再判
        //   "上一次非零符号 vs 这一次非零符号"。
        // ★ 阈值不能写死：导数 3x^2 在原点附近的值随 x 二次衰减，用绝对阈值会把整段判成 0。
        //   Richardson 外推之后偏差已是 O(h⁴)，阈值取"区间尺度 × 1e-6"足够稳
        //   （既容得下浮点噪声，又远小于 h=1e-3 时一个格点上的典型导数）。
        double derivScale = Math.Max(span, 1.0);
        double zeroTol = derivScale * 1e-6;
        int SignOf(double d) => Math.Abs(d) <= zeroTol ? 0 : (d > 0 ? 1 : -1);

        int prevSign = prevValid ? SignOf(prevD) : 0;
        double signAnchorX = prevX;       // 上一次"非零符号"所在的位置（用于二分左端）
        double? zeroRunStart = null;      // 导数开始贴近 0 的位置（"只碰零"的候选）

        for (int i = 1; i <= CoarseSteps && results.Count < maxResults; i++)
        {
            double x = xMin + dx * i;
            double d = Deriv(x);
            bool valid = double.IsFinite(d);

            if (valid)
            {
                int sign = SignOf(d);
                if (sign != 0)
                {
                    if (prevSign != 0 && sign != prevSign)
                    {
                        // 在 [signAnchorX, x] 上对导数做二分求驻点
                        double stationary = BisectDerivative(Deriv, signAnchorX, x, prevSign, sign);
                        if (double.IsFinite(stationary))
                        {
                            var kind = prevSign > 0 ? FeatureKind.Maximum : FeatureKind.Minimum;
                            AddExtremum(results, expr, parameters, stationary, kind, maxResults);
                        }
                    }
                    else if (prevSign == sign && prevSign != 0 && zeroRunStart.HasValue)
                    {
                        // ★ 导数"只碰零、不变号"：x^3 的导数 3x^2 在原点两侧都为正。
                        //   这仍是驻点（切线水平），只是不是极值 —— 标成 Inflection 而非漏掉。
                        double touch = BisectDerivative(Deriv, zeroRunStart.Value, x, 0, sign);
                        if (double.IsFinite(touch))
                            AddExtremum(results, expr, parameters, touch, FeatureKind.Inflection, maxResults);
                    }
                    prevSign = sign;
                    signAnchorX = x;
                    zeroRunStart = null;
                }
                else if (prevSign != 0 && !zeroRunStart.HasValue)
                {
                    zeroRunStart = prevX;      // 记住"导数开始贴近零"的位置
                }
            }

            prevX = x; prevD = d; prevValid = valid;
        }

        return results;
    }

    /// <summary>把一个驻点加进结果（去重 + 判类别）。</summary>
    private static void AddExtremum(
        List<FeaturePoint> sink, ExprNode expr, IReadOnlyDictionary<string, double> parameters,
        double x, FeatureKind kind, int maxResults)
    {
        if (sink.Count >= maxResults) return;
        foreach (var p in sink)
            if (Math.Abs(p.X - x) < 1e-6) return;

        double y = ExpressionEvaluator.Eval(expr, x, parameters);
        if (!double.IsFinite(y)) return;
        sink.Add(new FeaturePoint { X = x, Y = y, Kind = kind });
    }

    /// <summary>在导数变号的区间上二分求驻点（导数视为函数）。</summary>
    private static double BisectDerivative(Func<double, double> derivative, double lo, double hi, double dLoSign, double dHiSign)
    {
        for (int i = 0; i < BisectIterations; i++)
        {
            double mid = 0.5 * (lo + hi);
            double dMid = derivative(mid);
            if (!double.IsFinite(dMid)) return double.NaN;

            int sign = Math.Sign(dMid);
            if (sign == 0 || hi - lo <= 1e-13)
                return mid;

            if (sign == dLoSign) { lo = mid; }
            else { hi = mid; if (sign != dHiSign) dHiSign = sign; }
        }
        return 0.5 * (lo + hi);
    }

    // ---------------------------------------------------------------- 描述（状态栏用）

    /// <summary>把一个特征点写成老师看得懂的读数。</summary>
    public static string Describe(FeaturePoint p)
    {
        string label = p.Kind switch
        {
            FeatureKind.Zero => "零点",
            FeatureKind.Maximum => "极大值",
            FeatureKind.Minimum => "极小值",
            FeatureKind.Inflection => "驻点",
            FeatureKind.Intersection => "交点",
            _ => "特征点",
        };
        return $"{label}({Format(p.X)}, {Format(p.Y)})";
    }

    /// <summary>紧凑数字格式（与坐标系刻度同一套风格：整数不带小数点）。</summary>
    public static string Format(double v)
    {
        if (!double.IsFinite(v)) return "—";
        if (Math.Abs(v - Math.Round(v)) < 1e-9)
            return ((long)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
