using System.Collections.Generic;

namespace MathPhys.Ink.Plugin.FunctionPlot.Expr;

/// <summary>
/// 递归下降解析器：token 流 → <see cref="ExprNode"/>。
/// </summary>
/// <remarks>
/// 语法（与函数键盘能拼出来的一一对应）：
/// <code>
/// expr    = piecewise | domain
/// domain  = cmp (('where'|'if'|'|') cmp)*          // 尾随定义域：ln(x) where x>0
/// piecewise = '{' branch (',' branch)* '}'         // {x&lt;0: -x, else: x}
/// branch  = 'else' ':' expr | cmp ':' expr
/// cmp     = add (('&lt;'|'&lt;='|'&gt;'|'&gt;='|'='|'=='|'!=') add)?
/// add     = term (('+'|'-') term)*
/// term    = factor (('*'|'/'|隐式乘) factor)*
/// factor  = ('+'|'-') factor | power
/// power   = primary ('^' factor)?          // 右结合
/// primary = number | name | '(' expr ')'
/// name    = 常量(pi/π/e) | 变量(x) | 函数名(后跟 '(') | 参数(单字母)
/// </code>
/// 报错带位置（<see cref="ParseException.Position"/>），方便老师定位输错的字符。
/// </remarks>
public sealed class ExpressionParser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    public ExpressionParser(IReadOnlyList<Token> tokens)
        => _tokens = tokens;

    /// <summary>解析入口。空串/纯空白 → 抛错。</summary>
    public static ExprNode Parse(string source)
    {
        var lexer = new ExpressionLexer(source);
        var tokens = lexer.Tokenize();
        var parser = new ExpressionParser(tokens);
        var node = parser.ParseExpr();
        if (parser.Peek().Kind != TokenKind.End)
            throw new ParseException($"多余的输入「{parser.Peek().Text}」", parser.Peek().Pos);
        return node;
    }

    private Token Peek() => _tokens[_pos];
    private Token Next() => _tokens[_pos++];
    private bool At(char op) => Peek().Kind == TokenKind.Op && Peek().Text.Length == 1 && Peek().Text[0] == op;
    private bool AtOp(string op) => Peek().Kind == TokenKind.Op && Peek().Text == op;

    /// <summary>看下一个名字是不是某个关键字（如 else / where / if）。</summary>
    private bool AtName(string name)
        => Peek().Kind == TokenKind.Name && string.Equals(Peek().Text, name, System.StringComparison.Ordinal);

    // ---------------------------------------------------------------- 顶层

    /// <remarks>
    /// 顶层多了一层"分段"判断：只有<b>整串就是一个花括号块</b>时才当分段函数。
    /// 这样 <c>{x&lt;0: -x}</c> 与 <c>ln(x) where x&gt;0</c> 走不同分支，互不干扰。
    /// </remarks>
    private ExprNode ParseExpr()
    {
        if (Peek().Kind == TokenKind.LBrace)
            return ParsePiecewise();

        var node = ParseCompare();
        return WrapDomain(node);
    }

    /// <summary>尾随定义域：<c>expr where 条件</c>（也可写 <c>if</c>）。</summary>
    private ExprNode WrapDomain(ExprNode node)
    {
        while (AtName("where") || AtName("if"))
        {
            Next();
            var cond = ParseCompare();
            node = new DomainNode(node, cond);
        }
        return node;
    }

    private ExprNode ParsePiecewise()
    {
        var open = Next(); // '{'
        var branches = new List<PieceBranch>();

        while (true)
        {
            if (Peek().Kind == TokenKind.RBrace || Peek().Kind == TokenKind.End)
                break;

            if (AtName("else"))
            {
                Next();
                if (Peek().Kind != TokenKind.Colon)
                    throw new ParseException("「else」后面要跟「:」，如 else: x", Peek().Pos);
                Next(); // ':'
                branches.Add(new PieceBranch(null, ParseCompare()));
            }
            else
            {
                var cond = ParseCompare();
                if (Peek().Kind != TokenKind.Colon)
                    throw new ParseException("分段的分支要写成「条件: 取值」，如 x<0: -x", Peek().Pos);
                Next(); // ':'
                branches.Add(new PieceBranch(cond, ParseCompare()));
            }

            if (Peek().Kind == TokenKind.Comma) { Next(); continue; }
            break;
        }

        if (Peek().Kind != TokenKind.RBrace)
            throw new ParseException("分段缺少右花括号「}」", Peek().Pos);
        Next(); // '}'

        if (branches.Count == 0)
            throw new ParseException("分段「{}」里至少要有一个分支", open.Pos);

        return new PiecewiseNode(branches);
    }

    /// <summary>比较：只允许一层（<c>a&lt;b&lt;c</c> 这种连写不支持，会报"多余的输入"）。</summary>
    private ExprNode ParseCompare()
    {
        var left = ParseAdd();
        if (Peek().Kind == TokenKind.Op)
        {
            string op = Peek().Text;
            if (op is "<" or "<=" or ">" or ">=" or "=" or "==" or "!=")
            {
                Next();
                var right = ParseAdd();
                return new CompareNode(left, op, right);
            }
        }
        return left;
    }

    private ExprNode ParseAdd()
    {
        var left = ParseTerm();
        while (At('+') || At('-'))
        {
            char op = Next().Text[0];
            var right = ParseTerm();
            left = new BinaryNode(left, op, right);
        }
        return left;
    }

    private ExprNode ParseTerm()
    {
        var left = ParseFactor();
        while (true)
        {
            if (At('*') || At('/'))
            {
                char op = Next().Text[0];
                var right = ParseFactor();
                left = new BinaryNode(left, op, right);
            }
            // 隐式乘法：'*' token（由词法补全）夹在两项之间
            else if (Peek().Kind == TokenKind.Op && Peek().Text == "*"
                     && Peek().Pos != 0 && _pos + 1 < _tokens.Count)
            {
                Next(); // 吃掉补丁 '*'
                var right = ParseFactor();
                left = new BinaryNode(left, '*', right);
            }
            else break;
        }
        return left;
    }

    private ExprNode ParseFactor()
    {
        if (At('+')) { Next(); return ParseFactor(); }
        if (At('-')) { Next(); return new NegateNode(ParseFactor()); }
        return ParsePower();
    }

    private ExprNode ParsePower()
    {
        var baseNode = ParsePrimary();
        if (At('^'))
        {
            Next();
            // 右结合：指数部分允许再带幂
            var exp = ParseFactor();
            return new BinaryNode(baseNode, '^', exp);
        }
        return baseNode;
    }

    private ExprNode ParsePrimary()
    {
        var t = Peek();
        if (t.Kind == TokenKind.Number)
        {
            Next();
            return new ConstantNode(t.Number);
        }
        if (t.Kind == TokenKind.LParen)
        {
            Next();
            var inner = ParseExpr();
            ExpectRparen();
            return inner;
        }
        // 括号里也可以直接嵌一个分段（如 2*{x<0: -1, else: 1}）
        if (t.Kind == TokenKind.LBrace)
            return ParsePiecewise();
        if (t.Kind == TokenKind.Name)
        {
            Next();
            string name = t.Text;
            // 常量
            if (name is "pi" or "π") return new ConstantNode(System.Math.PI);
            if (name == "e") return new ConstantNode(System.Math.E);
            // 变量
            if (name == "x") return new VariableNode();
            // 函数名：后必须跟 '('
            if (ExpressionEvaluator.IsFunction(name))
            {
                if (Peek().Kind != TokenKind.LParen)
                    throw new ParseException($"函数「{name}」后面要跟括号，如 {name}(x)", t.Pos);
                Next(); // '('
                var arg = ParseExpr();
                ExpectRparen();
                return new FunctionNode(name, arg);
            }
            // 参数：单字母（非 x）。多字母非函数名 → 报错
            if (name.Length == 1)
                return new ParameterNode(name);
            throw new ParseException($"无法识别的符号「{name}」（函数名后需加括号）", t.Pos);
        }
        if (t.Kind == TokenKind.End)
            throw new ParseException("表达式不完整（意外结束）", t.Pos);
        throw new ParseException($"无法识别的输入「{t.Text}」", t.Pos);
    }

    private void ExpectRparen()
    {
        if (Peek().Kind != TokenKind.RParen)
            throw new ParseException("缺少右括号「)」", Peek().Pos);
        Next();
    }
}
