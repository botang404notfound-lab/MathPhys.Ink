namespace MathPhys.Ink.Infrastructure;

/// <summary>
/// 简易算式求值器（M13 S5）：支持 + − × ÷、一元 √（可连用）与乘除优先级。
/// 纯函数、无 UI 依赖 —— 计算器面板与验收 harness 共用同一份实现。
/// </summary>
/// <remarks>
/// 递归下降文法（√ 作为一元前缀进文法，而不是按键时即时求值 ——
/// 优先级由文法统一保证，<c>√9+1</c>、<c>√√16</c> 都不需要特判）：
/// <code>
/// expr  → term  (('+' | '-') term)*
/// term  → unary (('*' | '/') unary)*
/// unary → '√' unary | '-' unary | number
/// </code>
/// 除零、残缺算式、不认识的字符一律 <c>false</c> —— 讲台上宁可"算不出"也不能崩。
/// </remarks>
public static class ArithmeticEvaluator
{
    /// <summary>求值。返回 <c>false</c> = 算式有问题（残缺 / 除零 / 非法字符）。</summary>
    public static bool TryEvaluate(string expression, out double result)
    {
        result = 0;

        if (string.IsNullOrWhiteSpace(expression)) return false;

        var tokens = Tokenize(expression);
        if (tokens.Count == 0) return false;

        int pos = 0;
        if (!ParseExpr(tokens, ref pos, out double value)) return false;

        // 算式必须恰好读完（"2+" 这类残缺在这里被拒）
        return pos == tokens.Count && double.IsFinite(value) && (result = value) == value;
    }

    // ---------------------------------------------------------------- 词法

    /// <summary>记号类型：数字 / 运算符。</summary>
    private enum TokenKind { Number, Op }

    private readonly record struct Token(TokenKind Kind, double Value, char Symbol);

    /// <summary>切词：数字（含小数）与 + - * / √。× ÷ 归一为 * /（按键面板显示用字形）。</summary>
    private static List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < expression.Length)
        {
            char c = expression[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsDigit(c) || c == '.')
            {
                int start = i;
                while (i < expression.Length && (char.IsDigit(expression[i]) || expression[i] == '.'))
                {
                    i++;
                }

                // 数字里出现两个小数点之类 → 解析失败（TryParse 出来说了算）
                if (!double.TryParse(expression[start..i], System.Globalization.CultureInfo.InvariantCulture,
                                     out double number))
                {
                    return new List<Token>();
                }

                tokens.Add(new Token(TokenKind.Number, number, default));
                continue;
            }

            char normalized = c switch
            {
                '×' or 'x' or 'X' => '*',
                '÷' => '/',
                '+' or '-' or '*' or '/' or '√' => c,
                _ => default,
            };

            if (normalized == default) return new List<Token>();   // 不认识的字符：整体拒绝

            tokens.Add(new Token(TokenKind.Op, 0, normalized));
            i++;
        }

        return tokens;
    }

    // ---------------------------------------------------------------- 文法

    private static bool ParseExpr(List<Token> tokens, ref int pos, out double value)
    {
        value = 0;
        if (!ParseTerm(tokens, ref pos, out value)) return false;

        while (pos < tokens.Count && tokens[pos].Kind == TokenKind.Op
               && tokens[pos].Symbol is '+' or '-')
        {
            char op = tokens[pos].Symbol;
            pos++;

            if (!ParseTerm(tokens, ref pos, out double right)) return false;
            value = op == '+' ? value + right : value - right;
        }

        return true;
    }

    private static bool ParseTerm(List<Token> tokens, ref int pos, out double value)
    {
        value = 0;
        if (!ParseUnary(tokens, ref pos, out value)) return false;

        while (pos < tokens.Count && tokens[pos].Kind == TokenKind.Op
               && tokens[pos].Symbol is '*' or '/')
        {
            char op = tokens[pos].Symbol;
            pos++;

            if (!ParseUnary(tokens, ref pos, out double right)) return false;

            if (op == '/')
            {
                if (right == 0) return false;    // 除零：算不出，不给 Infinity
                value /= right;
            }
            else
            {
                value *= right;
            }
        }

        return true;
    }

    private static bool ParseUnary(List<Token> tokens, ref int pos, out double value)
    {
        value = 0;
        if (pos >= tokens.Count) return false;

        if (tokens[pos].Kind == TokenKind.Op)
        {
            char symbol = tokens[pos].Symbol;
            if (symbol is '√' or '-')
            {
                pos++;
                if (!ParseUnary(tokens, ref pos, out double inner)) return false;
                value = symbol == '√' ? Math.Sqrt(inner) : -inner;
                return double.IsFinite(value);
            }

            return false;
        }

        value = tokens[pos].Value;
        pos++;
        return true;
    }
}
