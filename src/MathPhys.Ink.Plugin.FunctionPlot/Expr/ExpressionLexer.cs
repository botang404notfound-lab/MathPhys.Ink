using System.Globalization;

namespace MathPhys.Ink.Plugin.FunctionPlot.Expr;

/// <summary>词法单元种类。</summary>
public enum TokenKind
{
    Number,
    Name,
    Op,        // + - * / ^ ( ) < > = ! & | { } : ,
    LParen,
    RParen,
    LBrace,    // {  分段 / 定义域块的开始
    RBrace,    // }
    Colon,     // :  分段分支的"条件 : 取值"
    Comma,     // ,  分支之间的分隔
    End,
}

/// <summary>一个词法单元（带它在原串中的位置，供报错用）。</summary>
public sealed record Token(TokenKind Kind, string Text, double Number, int Pos)
{
    public static readonly Token End = new(TokenKind.End, string.Empty, 0, -1);
}

/// <summary>
/// 把表达式字符串拆成 token；并处理<b>隐式乘法</b>：
/// <c>2x</c>、<c>3sin(x)</c>、<c>2(x+1)</c>、<c>(x+1)(x-1)</c> 都会自动插入 <c>*</c>。
/// </summary>
/// <remarks>
/// 隐式乘法是中国人写数学的默认习惯，必须支持。做法是先正常分词，
/// 再在后处理里：若相邻两个 token 满足"左是 数字/名字/右括号、右是 数字/名字/左括号"，
/// 就在它们之间补一个 <c>*</c>。一元负号保持独立 token，不受影响。
/// <para>
/// 分段 / 定义域语法新增了 <c>{ } : ,</c> 四个字符与比较运算符
/// （<c>&lt; &lt;= &gt; &gt;= = == !=</c>，以及逻辑 <c>&amp; |</c>）。
/// ★ <b>它们一律不参与隐式乘法</b>：<c>{x&lt;0: -x}</c> 里 <c>0</c> 与 <c>:</c> 之间
/// 若被补成 <c>0*:</c>，整个解析就崩了 —— 所以补乘号的"右边界"只认
/// 数字 / 名字 / <c>(</c>，花括号与冒号天然被排除在外。
/// </para>
/// </remarks>
public sealed class ExpressionLexer
{
    private readonly string _src;

    public ExpressionLexer(string source)
        => _src = source ?? string.Empty;

    /// <summary>分词（已含隐式乘法补全）。抛 <see cref="ParseException"/> 报出位置。</summary>
    public IReadOnlyList<Token> Tokenize()
    {
        var raw = ScanRaw();
        var outp = new List<Token>(raw.Count + 4);
        Token? prev = null;
        foreach (var t in raw)
        {
            if (prev is not null && NeedsImplicitMul(prev, t))
                outp.Add(new Token(TokenKind.Op, "*", 0, t.Pos));
            outp.Add(t);
            prev = t;
        }
        outp.Add(Token.End);
        return outp;
    }

    // 左 token 结尾、右 token 开头时，应补乘号
    private static bool NeedsImplicitMul(Token left, Token right)
    {
        bool leftEnds = left.Kind is TokenKind.Number or TokenKind.Name or TokenKind.RParen;
        bool rightStarts = right.Kind is TokenKind.Number or TokenKind.Name or TokenKind.LParen;
        if (!leftEnds || !rightStarts) return false;
        // 函数名后直接跟 '(' 是函数调用（sin(x)），不是隐式乘法：Name 右邻 LParen 不补乘号。
        // 只有 数字*(2(x+1)) 或 右括号*((x+1)(x-1)) 这类才需要补。
        if (left.Kind == TokenKind.Name && right.Kind == TokenKind.LParen) return false;
        // ★ 关键字（else / where / if）绝不能补乘号：
        //   `ln(x) where x>0` 里 `)` 与 `where` 会被判成"隐式乘法"而插进一个 `*`，
        //   于是 where 变成了普通名字、定义域语法整个失效（本条踩过一次）。
        if (right.Kind == TokenKind.Name && IsKeyword(right.Text)) return false;
        if (left.Kind == TokenKind.Name && IsKeyword(left.Text)) return false;
        return true;
    }

    /// <summary>语法关键字（不参与隐式乘法）。</summary>
    public static bool IsKeyword(string name)
        => name is "else" or "where" or "if";

    private List<Token> ScanRaw()
    {
        var tokens = new List<Token>();
        int i = 0;
        int n = _src.Length;
        while (i < n)
        {
            char c = _src[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(') { tokens.Add(new Token(TokenKind.LParen, "(", 0, i)); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokenKind.RParen, ")", 0, i)); i++; continue; }
            if (c == '{') { tokens.Add(new Token(TokenKind.LBrace, "{", 0, i)); i++; continue; }
            if (c == '}') { tokens.Add(new Token(TokenKind.RBrace, "}", 0, i)); i++; continue; }
            if (c == ':') { tokens.Add(new Token(TokenKind.Colon, ":", 0, i)); i++; continue; }
            if (c == ',') { tokens.Add(new Token(TokenKind.Comma, ",", 0, i)); i++; continue; }

            // 比较 / 逻辑运算符：两字符优先（<= >= == !=）
            if (c is '<' or '>' or '=' or '!' or '&' or '|')
            {
                if (i + 1 < n)
                {
                    char c2 = _src[i + 1];
                    if (c == '<' && c2 == '=') { tokens.Add(new Token(TokenKind.Op, "<=", 0, i)); i += 2; continue; }
                    if (c == '>' && c2 == '=') { tokens.Add(new Token(TokenKind.Op, ">=", 0, i)); i += 2; continue; }
                    if (c == '=' && c2 == '=') { tokens.Add(new Token(TokenKind.Op, "==", 0, i)); i += 2; continue; }
                    if (c == '!' && c2 == '=') { tokens.Add(new Token(TokenKind.Op, "!=", 0, i)); i += 2; continue; }
                }
                // 单字符：< > = & |  （'!' 单用无意义，交由解析器报错）
                if (c == '!')
                    throw new ParseException("「!」要写成「!=」（不等于）", i);
                tokens.Add(new Token(TokenKind.Op, c.ToString(), 0, i));
                i++;
                continue;
            }

            if (c is '+' or '-' or '*' or '/' or '^')
            {
                tokens.Add(new Token(TokenKind.Op, c.ToString(), 0, i));
                i++;
                continue;
            }

            // 数字：3 / 3.14 / .5 （小数点前可省）
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(_src[i + 1])))
            {
                int start = i;
                while (i < n && (char.IsDigit(_src[i]) || _src[i] == '.')) i++;
                string lit = _src.Substring(start, i - start);
                if (!double.TryParse(lit, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                    throw new ParseException($"无法识别的数字「{lit}」", start);
                tokens.Add(new Token(TokenKind.Number, lit, val, start));
                continue;
            }

            // 名字：字母或 π（常量/变量/参数/函数名）
            if (char.IsLetter(c) || c == 'π')
            {
                int start = i;
                while (i < n && (char.IsLetter(_src[i]) || _src[i] == 'π')) i++;
                string name = _src.Substring(start, i - start);
                tokens.Add(new Token(TokenKind.Name, name, 0, start));
                continue;
            }

            throw new ParseException($"无法识别的字符「{c}」", i);
        }
        return tokens;
    }
}
