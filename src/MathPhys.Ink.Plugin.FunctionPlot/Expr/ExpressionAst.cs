namespace MathPhys.Ink.Plugin.FunctionPlot.Expr;

/// <summary>
/// 表达式的抽象语法树节点。
/// </summary>
/// <remarks>
/// 一棵只读的树：常量 / 变量 x / 参数(a,b,c…) / 一元负 / 二元(+-*/^) / 函数调用 /
/// 定义域限制 / 分段函数。
/// 求值与采样都只遍历它，不碰任何 UI。
/// </remarks>
public abstract record ExprNode;

/// <summary>常数，如 3.14。</summary>
public sealed record ConstantNode(double Value) : ExprNode;

/// <summary>自变量 x。</summary>
public sealed record VariableNode : ExprNode;

/// <summary>参数（滑块字母，如 a、k），取值来自外部参数表；缺省为 0。</summary>
public sealed record ParameterNode(string Name) : ExprNode;

/// <summary>一元负号：-expr。</summary>
public sealed record NegateNode(ExprNode Inner) : ExprNode;

/// <summary>二元运算：left Op right，Op ∈ {+ - * / ^}。</summary>
public sealed record BinaryNode(ExprNode Left, char Op, ExprNode Right) : ExprNode;

/// <summary>函数调用：Name(arg)，Name ∈ {sin, cos, …}。</summary>
public sealed record FunctionNode(string Name, ExprNode Argument) : ExprNode;

/// <summary>
/// 定义域限制：<c>Inner</c> 只在 <c>Condition</c> 为真处有值，否则为未定义（不画）。
/// </summary>
/// <remarks>
/// ★ <b>为什么不新增字段而是新增一个 record</b>：已有 6 个 record 的"形状"（参数个数与类型）
/// 都是别的代码 switch 依赖的，往 <see cref="FunctionNode"/> 里塞一个 <c>Condition</c>
/// 会让所有 <c>case FunctionNode f:</c> 的 <c>f.Argument</c> 语义突然多一层。<b>加新类型、不改旧类型</b>
/// 是这里唯一安全的做法：旧代码的 <c>default:</c> 分支一律返回 NaN，语义上正好就是"未定义"。
/// <para>
/// 写法：<c>{x>0: ln(x)}</c> 或 <c>ln(x) where x>0</c>。条件支持 <c>&lt; &lt;= &gt; &gt;= = !=</c>，
/// 也支持直接写一个布尔表达式（非 0 即真）。
/// </para>
/// </remarks>
public sealed record DomainNode(ExprNode Inner, ExprNode Condition) : ExprNode;

/// <summary>
/// 分段函数的一个分支：<c>Condition</c> 为真时取 <c>Value</c>。
/// </summary>
/// <remarks><c>Condition</c> 为 <c>null</c> 表示"否则"（兜底分支）。</remarks>
public sealed record PieceBranch(ExprNode? Condition, ExprNode Value);

/// <summary>
/// 分段函数：从上往下第一个条件为真的分支胜出；全不中 ⇒ 未定义（<c>NaN</c>，不画）。
/// </summary>
/// <remarks>
/// 写法：<c>{x&lt;0: -x, x&lt;1: x^2, else: 2x-1}</c>（花括号 + 逗号分隔，分支内可含冒号）。
/// <b>顺序敏感</b>：与数学教材的写法一致，先写的先判。
/// </remarks>
public sealed record PiecewiseNode(IReadOnlyList<PieceBranch> Branches) : ExprNode;

/// <summary>比较 / 逻辑运算：left Op right，Op ∈ {&lt; &lt;= &gt; &gt;= = == != &amp; |}。</summary>
/// <remarks>
/// 单独一类而不是塞进 <see cref="BinaryNode"/>：算术运算与比较运算的<b>结合性、
/// 优先级、返回值语义</b>都不同（前者返回数值、后者返回 0/1），
/// 混在一起后 <c>CollectParameters</c> / 求值 switch 都要跟着加特判，得不偿失。
/// </remarks>
public sealed record CompareNode(ExprNode Left, string Op, ExprNode Right) : ExprNode;
