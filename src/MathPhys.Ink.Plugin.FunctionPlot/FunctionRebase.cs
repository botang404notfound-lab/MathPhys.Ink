using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Plugin.FunctionPlot.Expr;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.FunctionPlot;

/// <summary>
/// 函数曲线的"拖动 → 参数"折算（M12 S7.2）—— <b>纯函数</b>，不碰视觉树、不存状态。
/// </summary>
/// <remarks>
/// <para>
/// <b>只做平移可解的形式</b>（docs/17 §7.2 拍板的 V1 边界）：
/// 一次函数 <c>y=ax+b</c> 与二次函数（顶点形式 <c>a(x-h)²+k</c> 或任意展开形式）。
/// 拖动 = 把世界位移折回数学位移，再按形式反解：
/// </para>
/// <list type="bullet">
/// <item>一次：<c>b' = b + dmy − a·dmx</c>（代入 <c>f(x−dmx)+dmy</c> 展开）；</item>
/// <item>二次：<c>h' = h + dmx、k' = k + dmy</c>（顶点平移）。</item>
/// </list>
/// <para>
/// <b>形式判别用数值差分</b>，不猜表达式长相：等距采 5 点，二阶差分 ≈ 0 ⇒ 一次；
/// 四阶差分 ≈ 0（而二阶不为 0）⇒ 二次；其余（三角/指数/分段…）返回 false，
/// 宿主照旧走"转嫁给坐标系"的老路 —— 拖的是整张图，行为与旧版一致。
/// </para>
/// <para>
/// <b>参数优先、表达式兜底</b>：若表达式里已有能吸收平移的参数
/// （常数项参数 ∂f/∂p≡1；二次的 h 参数 ∂f/∂p 是斜率为 −2a 的仿函数），
/// 就只改参数 —— 老师的滑块联动还在；否则把表达式改写成规范数值形式
/// （一次 <c>a*x+b</c>、二次顶点式 <c>a*(x-h)^2+k</c>）。
/// </para>
/// <para>
/// ★ <b>y 轴方向约定</b>：函数对象的本地几何 = 数学坐标再取 <c>y → −y</c>
/// （与坐标系网格的 <c>FlipY</c>、特征点标注的 <c>−p.Y</c> 同一条约定，
/// 渲染器在 <c>CreateVisual</c> 里统一翻转）。于是数学位移 = 逆位姿位移的 <b>y 取反</b>。
/// </para>
/// </remarks>
public static class FunctionRebase
{
    /// <summary>表达式所在的文本参数键（与渲染器/工具一致）。</summary>
    public const string ExprKey = "expr";

    /// <summary>数值参数键前缀（与渲染器/工具一致）。</summary>
    public const string ParamPrefix = "param_";

    // ---------------------------------------------------------------- 入口

    /// <summary>
    /// 尝试把一帧平移折进函数参数。返回 <c>false</c> 表示此函数不平移可解（宿主走老路）。
    /// </summary>
    public static bool TryRebase(
        IGfxObjectRef obj, Vector worldDelta,
        out IReadOnlyDictionary<string, double> numbers,
        out IReadOnlyDictionary<string, string> texts)
    {
        var emptyNumbers = new Dictionary<string, double>();
        var emptyTexts = new Dictionary<string, string>();
        numbers = emptyNumbers;
        texts = emptyTexts;

        if (obj is null) return false;
        if (!double.IsFinite(worldDelta.X) || !double.IsFinite(worldDelta.Y)) return false;
        if (obj.Scale <= 0 || !double.IsFinite(obj.Scale)) return false;

        // ---- 1) 世界位移 → 本地位移（宿主位姿 = 缩放→旋转→平移；逆矩阵天然处理旋转）----
        var pose = new Matrix();
        pose.Scale(obj.Scale, obj.Scale);
        pose.Rotate(obj.RotationDegrees);
        if (!pose.HasInverse) return false;
        pose.Invert();
        var localDelta = pose.Transform(worldDelta);

        // ---- 2) 本地位移 → 数学位移（本地 y = −数学 y，见类注释）----
        double mdx = localDelta.X;
        double mdy = -localDelta.Y;

        // ---- 3) 解析表达式 ----
        string expr = obj.GetText(ExprKey, "");
        if (string.IsNullOrWhiteSpace(expr)) return false;

        ExprNode ast;
        try
        {
            ast = ExpressionParser.Parse(expr);
        }
        catch (ParseException)
        {
            return false;
        }

        var parameters = new Dictionary<string, double>();
        foreach (var name in ExpressionEvaluator.CollectParameters(ast))
            parameters[name] = obj.GetNumber(ParamPrefix + name, 0.0);

        // ---- 4) 等距采 5 点做差分判别（全取在范围内侧，避开端点的定义域坑）----
        double xMin = obj.GetNumber("xMin", -10);
        double xMax = obj.GetNumber("xMax", 10);
        double span = xMax - xMin;
        if (!(span > 0) || !double.IsFinite(span)) return false;

        double start = xMin + 0.1 * span;
        double step = 0.2 * span;
        var xs = new double[5];
        var ys = new double[5];
        double magnitude = 1.0;

        for (int i = 0; i < 5; i++)
        {
            xs[i] = start + step * i;
            ys[i] = ExpressionEvaluator.Eval(ast, xs[i], parameters);
            if (!double.IsFinite(ys[i])) return false;      // 奇点/定义域外 ⇒ 不折算
            magnitude = Math.Max(magnitude, Math.Abs(ys[i]));
        }

        double d2 = ys[0] - 2 * ys[2] + ys[4];                              // 二阶差分（跨 2 步）
        double d4 = ys[0] - 4 * ys[1] + 6 * ys[2] - 4 * ys[3] + ys[4];      // 四阶差分

        // 真一次函数的差分是浮点零（~1e-15 相对）；真二次的四阶差分同样是零。
        // 阈值取得比浮点噪声宽 3 个数量级、又远小于任何"肉眼可见的弯曲"。
        double tol2 = 1e-9 * magnitude;
        double tol4 = 1e-6 * magnitude;

        if (Math.Abs(d2) <= tol2)
            return TryRebaseLinear(ast, parameters, xs, ys, mdx, mdy, out numbers, out texts);

        if (Math.Abs(d4) <= tol4)
            return TryRebaseQuadratic(ast, parameters, xs, ys, mdx, mdy, out numbers, out texts);

        return false;   // 三角/指数/分段… 拖动只平移对象本身（转嫁给坐标系，旧行为）
    }

    // ---------------------------------------------------------------- 一次

    private static bool TryRebaseLinear(
        ExprNode ast, Dictionary<string, double> parameters,
        double[] xs, double[] ys,
        double mdx, double mdy,
        out IReadOnlyDictionary<string, double> numbers,
        out IReadOnlyDictionary<string, string> texts)
    {
        numbers = new Dictionary<string, double>();
        texts = new Dictionary<string, string>();

        var numberMap = new Dictionary<string, double>();
        var textMap = new Dictionary<string, string>();

        // 精确斜率/截距（真一次函数两点即定，取首尾最稳）
        double slope = (ys[4] - ys[0]) / (xs[4] - xs[0]);
        double intercept = ys[2] - slope * xs[2];

        // 平移可解：g(x) = f(x − mdx) + mdy = a·x + (b + mdy − a·mdx)
        double newIntercept = intercept + mdy - slope * mdx;

        // 常数项参数（∂f/∂p ≡ 1）能吸收平移 ⇒ 只改参数，滑块联动还在
        string? constantParam = FindConstantParam(ast, parameters, xs);
        if (constantParam is not null)
        {
            numberMap[ParamPrefix + constantParam] =
                parameters[constantParam] + (mdy - slope * mdx);
        }
        else
        {
            textMap[ExprKey] = FormatLinear(slope, newIntercept);
        }

        numbers = numberMap;
        texts = textMap;
        return true;
    }

    // ---------------------------------------------------------------- 二次

    private static bool TryRebaseQuadratic(
        ExprNode ast, Dictionary<string, double> parameters,
        double[] xs, double[] ys,
        double mdx, double mdy,
        out IReadOnlyDictionary<string, double> numbers,
        out IReadOnlyDictionary<string, string> texts)
    {
        numbers = new Dictionary<string, double>();
        texts = new Dictionary<string, string>();

        var numberMap = new Dictionary<string, double>();
        var textMap = new Dictionary<string, string>();

        // 用等距三点 (x0, x2, x4) 精确解抛物线系数（间距 m = 2·step）
        double x0 = xs[0], x2 = xs[2], x4 = xs[4];
        double m = x2 - x0;
        if (!(Math.Abs(m) > 1e-12) || !(Math.Abs(x4 - x0) > 1e-12)) return false;

        double a2 = (ys[0] - 2 * ys[2] + ys[4]) / (2.0 * m * m);
        if (!double.IsFinite(a2) || Math.Abs(a2) < 1e-12) return false;

        double b2 = (ys[4] - ys[0] - a2 * (x4 * x4 - x0 * x0)) / (x4 - x0);
        double c2 = ys[0] - a2 * x0 * x0 - b2 * x0;

        // 平移：g(x) = f(x − mdx) + mdy ⇒ 系数变换
        double bT = b2 - 2.0 * a2 * mdx;
        double cT = c2 - b2 * mdx + a2 * mdx * mdx + mdy;

        // 顶点形式参数（h = −b/2a，k = c − b²/4a）
        double h = -bT / (2.0 * a2);
        double k = cT - bT * bT / (4.0 * a2);

        // 参数探测：h 参数（∂f/∂p 是斜率 ≈ −2a 的仿函数）与 k 参数（∂f/∂p ≡ 1）
        string? hParam = FindAffineParam(ast, parameters, xs, a2);
        string? kParam = FindConstantParam(ast, parameters, xs);

        if (hParam is not null && kParam is not null && !string.Equals(hParam, kParam, StringComparison.Ordinal))
        {
            numberMap[ParamPrefix + hParam] = parameters[hParam] + mdx;
            numberMap[ParamPrefix + kParam] = parameters[kParam] + mdy;
        }
        else
        {
            textMap[ExprKey] = FormatQuadratic(a2, h, k);
        }

        numbers = numberMap;
        texts = textMap;
        return true;
    }

    // ---------------------------------------------------------------- 参数探测

    /// <summary>找"常数项参数"：∂f/∂p 在所有采样点都 ≈ 1。</summary>
    private static string? FindConstantParam(
        ExprNode ast, Dictionary<string, double> parameters, double[] xs)
    {
        foreach (var name in ExpressionEvaluator.CollectParameters(ast))
        {
            if (PartialIsEverywhere(ast, parameters, name, xs, target: 1.0))
                return name;
        }
        return null;
    }

    /// <summary>
    /// 找"二次的 h 参数"：∂f/∂p 关于 x 是<b>仿函数</b>且斜率 ≈ −2a
    /// （因为 ∂[a(x−h)²+k]/∂h = −2a(x−h)）。
    /// </summary>
    private static string? FindAffineParam(
        ExprNode ast, Dictionary<string, double> parameters, double[] xs, double a2)
    {
        foreach (var name in ExpressionEvaluator.CollectParameters(ast))
        {
            var s = new double[5];
            bool ok = true;
            for (int i = 0; i < 5; i++)
            {
                if (!TryPartial(ast, parameters, name, xs[i], out s[i])) { ok = false; break; }
            }
            if (!ok) continue;

            // 仿函数检验：两段割线斜率相等
            double s01 = (s[1] - s[0]) / (xs[1] - xs[0]);
            double s34 = (s[4] - s[3]) / (xs[4] - xs[3]);
            if (Math.Abs(s01 - s34) > 1e-6 * (1.0 + Math.Abs(s01))) continue;

            // 斜率 ≈ −2a
            if (Math.Abs(s01 + 2.0 * a2) > 1e-4 * (1.0 + Math.Abs(a2))) continue;

            return name;
        }
        return null;
    }

    private static bool PartialIsEverywhere(
        ExprNode ast, Dictionary<string, double> parameters, string name,
        double[] xs, double target)
    {
        foreach (var x in xs)
        {
            if (!TryPartial(ast, parameters, name, x, out double s)) return false;
            if (Math.Abs(s - target) > 1e-6 * (1.0 + Math.Abs(target))) return false;
        }
        return true;
    }

    /// <summary>中心差分算 ∂f/∂p（失败返回 false：扰动后出现奇点等）。</summary>
    private static bool TryPartial(
        ExprNode ast, Dictionary<string, double> parameters, string name,
        double x, out double partial)
    {
        partial = 0.0;
        double v = parameters[name];
        double eps = Math.Max(1e-4, Math.Abs(v) * 1e-4);

        var plus = new Dictionary<string, double>(parameters);
        var minus = new Dictionary<string, double>(parameters);
        plus[name] = v + eps;
        minus[name] = v - eps;

        double yp = ExpressionEvaluator.Eval(ast, x, plus);
        double ym = ExpressionEvaluator.Eval(ast, x, minus);
        if (!double.IsFinite(yp) || !double.IsFinite(ym)) return false;

        partial = (yp - ym) / (2.0 * eps);
        return double.IsFinite(partial);
    }

    // ---------------------------------------------------------------- 表达式格式化

    /// <summary>一次函数规范形：<c>a*x+b</c>（a=1 省系数、b=0 省常数、a=0 退化为常数）。</summary>
    public static string FormatLinear(double a, double b)
    {
        string text = string.Empty;

        if (Math.Abs(a) > 1e-12)
        {
            if (Math.Abs(a - 1.0) < 1e-12) text = "x";
            else if (Math.Abs(a + 1.0) < 1e-12) text = "-x";
            else text = FormatG(a) + "*x";
        }

        if (Math.Abs(b) > 1e-12)
        {
            if (text.Length == 0) text = FormatG(b);
            else text += b > 0 ? "+" + FormatG(b) : "-" + FormatG(-b);
        }

        return text.Length == 0 ? "0" : text;
    }

    /// <summary>二次函数规范顶点形：<c>a*(x-h)^2+k</c>（各段按 0/±1 省略）。</summary>
    public static string FormatQuadratic(double a, double h, double k)
    {
        string prefix;
        if (Math.Abs(a - 1.0) < 1e-12) prefix = string.Empty;
        else if (Math.Abs(a + 1.0) < 1e-12) prefix = "-";
        else prefix = FormatG(a) + "*";

        string core = Math.Abs(h) < 1e-12
            ? "x^2"
            : "(x" + (h > 0 ? "-" : "+") + FormatG(Math.Abs(h)) + ")^2";

        string text = prefix + core;

        if (Math.Abs(k) > 1e-12)
            text += k > 0 ? "+" + FormatG(k) : "-" + FormatG(-k);

        return text;
    }

    /// <summary>紧凑不变文化数字：整数不带小数点，小数最多 3 位（0.001 数学单位 ≈ 0.03 世界，亚像素）。</summary>
    private static string FormatG(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 5e-13) v = Math.Round(v);
        string text = v.ToString("0.###", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}
