using System.Collections.Generic;

namespace MathPhys.Ink.Plugin.FunctionPlot.Expr;

/// <summary>解析/求值过程中的错误，带出错位置。</summary>
public sealed class ParseException : Exception
{
    /// <summary>出错字符在原串中的索引（从 0 开始）。</summary>
    public int Position { get; }

    public ParseException(string message, int position) : base(message)
        => Position = position;
}

/// <summary>
/// 对 AST 求值：给定自变量 x 与参数表，算出 y。
/// </summary>
/// <remarks>
/// <b>绝不抛异常</b>：<c>1/0 → Infinity</c>、<c>sqrt(-1) → NaN</c>、<c>log(-1) → NaN</c>，
/// 都原样返回。采样器靠这些特殊值判断"奇点/定义域外"并断开路径。
/// <para>
/// 定义域与分段求不出（条件不满足）时返回 <see cref="double.NaN"/> —— 对采样器来说
/// "这一刻没有值"与"奇点"是同一种处置：断开、不画。所以新增节点<b>不需要</b>改采样器一行。
/// </para>
/// </remarks>
public static class ExpressionEvaluator
{
    private static readonly Dictionary<string, Func<double, double>> Functions = new()
    {
        ["sin"] = Math.Sin, ["cos"] = Math.Cos, ["tan"] = Math.Tan,
        ["asin"] = Math.Asin, ["acos"] = Math.Acos, ["atan"] = Math.Atan,
        ["sinh"] = Math.Sinh, ["cosh"] = Math.Cosh, ["tanh"] = Math.Tanh,
        ["ln"] = Math.Log, ["log"] = x => Math.Log10(x), ["log2"] = x => Math.Log(x, 2),
        ["sqrt"] = Math.Sqrt, ["abs"] = Math.Abs, ["exp"] = Math.Exp,
        ["floor"] = Math.Floor, ["ceil"] = Math.Ceiling, ["round"] = Math.Round,
        ["sign"] = x => Math.Sign(x),
    };

    public static bool IsFunction(string name)
        => Functions.ContainsKey(name);

    public static double Eval(ExprNode node, double x, IReadOnlyDictionary<string, double> parameters)
    {
        switch (node)
        {
            case ConstantNode c: return c.Value;
            case VariableNode: return x;
            case ParameterNode p:
                return parameters.TryGetValue(p.Name, out double v) ? v : 0.0;
            case NegateNode n: return -Eval(n.Inner, x, parameters);
            case BinaryNode b:
                double l = Eval(b.Left, x, parameters);
                double r = Eval(b.Right, x, parameters);
                return b.Op switch
                {
                    '+' => l + r,
                    '-' => l - r,
                    '*' => l * r,
                    '/' => l / r,            // 0/0=NaN, 1/0=Infinity，均不抛
                    '^' => Math.Pow(l, r),  // 负数^分数=NaN，0^负数=Infinity
                    _ => double.NaN,
                };
            case FunctionNode f:
                if (!Functions.TryGetValue(f.Name, out var fn))
                    return double.NaN;
                return fn(Eval(f.Argument, x, parameters));

            // 比较：算成 0/1（供条件使用；直接在曲线里写 x>0 也能画成 0/1 的阶梯）
            case CompareNode cmp:
                return EvalCompare(cmp, x, parameters);

            // 定义域：条件为真才给值，否则"未定义"
            case DomainNode d:
                return EvalCondition(d.Condition, x, parameters)
                    ? Eval(d.Inner, x, parameters)
                    : double.NaN;

            // 分段：从上往下第一个命中的分支胜出；全不中 ⇒ 未定义
            case PiecewiseNode pw:
                foreach (var br in pw.Branches)
                {
                    if (br.Condition is null) return Eval(br.Value, x, parameters);
                    if (EvalCondition(br.Condition, x, parameters))
                        return Eval(br.Value, x, parameters);
                }
                return double.NaN;

            default:
                return double.NaN;
        }
    }

    /// <summary>
    /// 条件求值 → 真/假。约定：非 0 即真（与 C 一致），<c>NaN</c> 视为假
    /// （"求不出"绝不该被当成"成立了"，否则 <c>ln(x) where x&gt;0</c> 会在 x&lt;0 处
    /// 因为比较出现 NaN 而误判为真）。
    /// </summary>
    public static bool EvalCondition(ExprNode node, double x, IReadOnlyDictionary<string, double> parameters)
    {
        double v = Eval(node, x, parameters);
        return !double.IsNaN(v) && Math.Abs(v) > 1e-12;
    }

    /// <summary>
    /// 把比较节点算成 0/1；比较量里出现 NaN（定义域外）时返回 NaN 而不是 0 ——
    /// 让"未知"能继续往上传，而不是被压成"假"。
    /// </summary>
    public static double EvalCompare(CompareNode cmp, double x, IReadOnlyDictionary<string, double> parameters)
    {
        double a = Eval(cmp.Left, x, parameters);
        double b = Eval(cmp.Right, x, parameters);
        if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;

        bool result = cmp.Op switch
        {
            "<" => a < b,
            "<=" => a <= b,
            ">" => a > b,
            ">=" => a >= b,
            "=" or "==" => Math.Abs(a - b) <= 1e-9,
            "!=" => Math.Abs(a - b) > 1e-9,
            _ => false,
        };
        return result ? 1.0 : 0.0;
    }

    /// <summary>收集表达式里出现的所有参数字母（去重，按出现顺序）。</summary>
    public static IReadOnlyList<string> CollectParameters(ExprNode node)
    {
        var seen = new HashSet<string>();
        var order = new List<string>();
        void Walk(ExprNode n)
        {
            switch (n)
            {
                case ParameterNode p:
                    if (seen.Add(p.Name)) order.Add(p.Name);
                    break;
                case NegateNode ng: Walk(ng.Inner); break;
                case BinaryNode bn: Walk(bn.Left); Walk(bn.Right); break;
                case FunctionNode fn: Walk(fn.Argument); break;
                case DomainNode dn: Walk(dn.Inner); Walk(dn.Condition); break;
                case PiecewiseNode pw:
                    foreach (var br in pw.Branches)
                    {
                        if (br.Condition is not null) Walk(br.Condition);
                        Walk(br.Value);
                    }
                    break;
                case CompareNode cn: Walk(cn.Left); Walk(cn.Right); break;
            }
        }
        Walk(node);
        return order;
    }

    // ---------------------------------------------------------------- 采样支撑

    /// <summary>
    /// 收集表达式里出现的所有分段"断点"（分支条件的边界值），供采样器补采样点用。
    /// </summary>
    /// <remarks>
    /// ★ <b>为什么必须做这件事</b>：分段函数在断点处是<b>跳变</b>（如 <c>{x&lt;0:-1, else:1}</c> 在 0 处
    /// 从 −1 跳到 +1）。均匀采样正好落在断点两侧时，会把这一段画成一条斜线（视觉上成了"斜坡"），
    /// 而正确的画法是两条互不相连的水平线。<b>把断点本身采两次</b>（左右极限各一次）之后，
    /// 采样器原有的"跳变过大 ⇒ 断开"逻辑就会自动把它们拆成两段。这是"让旧逻辑自己生效"，
    /// 而不是给采样器加特例。
    /// </remarks>
    public static IReadOnlyList<double> CollectBreakpoints(ExprNode node, IReadOnlyDictionary<string, double> parameters)
    {
        var found = new List<double>();
        void Walk(ExprNode n)
        {
            switch (n)
            {
                case CompareNode cn:
                    TrySolveBoundary(cn, parameters, found);
                    Walk(cn.Left); Walk(cn.Right);
                    break;
                case DomainNode dn:
                    Walk(dn.Condition); Walk(dn.Inner);
                    break;
                case PiecewiseNode pw:
                    foreach (var br in pw.Branches)
                    {
                        if (br.Condition is not null) Walk(br.Condition);
                        Walk(br.Value);
                    }
                    break;
                case NegateNode ng: Walk(ng.Inner); break;
                case BinaryNode bn: Walk(bn.Left); Walk(bn.Right); break;
                case FunctionNode fn: Walk(fn.Argument); break;
            }
        }
        Walk(node);
        return found;
    }

    /// <summary>
    /// 尝试把 <c>左侧 关系 右侧</c> 解出边界值：只有一侧是常量、另一侧含唯一的 x 时才算得出来。
    /// 解不出来就放弃（不抛）—— 断点只影响"画得准不准"，绝不该让整张图失败。
    /// </summary>
    private static void TrySolveBoundary(CompareNode cn, IReadOnlyDictionary<string, double> parameters, List<double> sink)
    {
        if (!UsesVariable(cn.Left) && !UsesVariable(cn.Right))
        {
            // 两侧都是常量：没有边界
            return;
        }

        // 归一成  f(x) 关系 constant 的形式
        bool leftHasX = UsesVariable(cn.Left);
        bool rightHasX = UsesVariable(cn.Right);
        if (leftHasX == rightHasX) return;   // 两边都有 x（如 x^2<1）⇒ 不解，交给均匀采样

        ExprNode withX = leftHasX ? cn.Left : cn.Right;
        ExprNode constant = leftHasX ? cn.Right : cn.Left;

        // 常量侧必须能算成一个数（不能含参数之外的变量）
        double c = Eval(constant, 0, parameters);
        if (double.IsNaN(c) || double.IsInfinity(c)) return;

        // 只处理"x 的线性式"：ax + b = c ⇒ x = (c - b)/a
        if (!TryLinearize(withX, parameters, out double slope, out double intercept)) return;
        if (Math.Abs(slope) < 1e-12) return;

        sink.Add((c - intercept) / slope);
    }

    /// <summary>表达式是否含自变量 x。</summary>
    public static bool UsesVariable(ExprNode node)
    {
        switch (node)
        {
            case VariableNode: return true;
            case NegateNode n: return UsesVariable(n.Inner);
            case BinaryNode b: return UsesVariable(b.Left) || UsesVariable(b.Right);
            case FunctionNode f: return UsesVariable(f.Argument);
            case DomainNode d: return UsesVariable(d.Inner) || UsesVariable(d.Condition);
            case CompareNode c: return UsesVariable(c.Left) || UsesVariable(c.Right);
            case PiecewiseNode pw:
                foreach (var br in pw.Branches)
                {
                    if (br.Condition is not null && UsesVariable(br.Condition)) return true;
                    if (UsesVariable(br.Value)) return true;
                }
                return false;
            default: return false;
        }
    }

    /// <summary>把表达式拟合成 <c>slope·x + intercept</c>；非线性就返回 false。</summary>
    private static bool TryLinearize(ExprNode node, IReadOnlyDictionary<string, double> parameters, out double slope, out double intercept)
    {
        // 用两个点求斜率 + 截距（线性式必然一致，非线性式会自相矛盾）
        const double p0 = 0.0;
        const double p1 = 1.0;
        double y0 = Eval(node, p0, parameters);
        double y1 = Eval(node, p1, parameters);
        if (!double.IsFinite(y0) || !double.IsFinite(y1)) { slope = 0; intercept = 0; return false; }

        slope = y1 - y0;
        intercept = y0;

        // 第三个点复核（防止 x^2 这类"两点看着也像线性"的巧合）
        double y2 = Eval(node, 2.0, parameters);
        if (!double.IsFinite(y2) || Math.Abs(y2 - (slope * 2.0 + intercept)) > 1e-6)
        {
            slope = 0; intercept = 0; return false;
        }
        return true;
    }
}
