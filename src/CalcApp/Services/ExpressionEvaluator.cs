using System.Globalization;

namespace CalcApp.Services;

/// <summary>
/// Safe recursive-descent calculator (no code execution).
/// Grammar:
///   expr    := term (('+'|'-') term)*
///   term    := unary ((*|/|%) unary)*
///   unary   := (+|-) unary | power        (-3^2 = -9)
///   power   := postfix (^ unary)?          (right-assoc, 2^-1 ok)
///   postfix := primary ('!')*
///   primary := number | constant | func '(' expr ')' | '(' expr ')'
/// Functions: sqrt, sin, cos, tan, log (base 10), ln, abs, exp, round, floor, ceil
/// Constants: pi, e
/// </summary>
public sealed class ExpressionEvaluator
{
    public const int MaxLength = 500;

    private readonly string _s;
    private int _pos;

    private ExpressionEvaluator(string s) => _s = s;

    public static double Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) throw new CalcException("Expression is empty.");
        if (expression.Length > MaxLength) throw new CalcException($"Expression is longer than {MaxLength} characters.");

        var normalized = expression.Replace('×', '*').Replace('÷', '/').Replace('−', '-');
        var p = new ExpressionEvaluator(normalized);
        var value = p.ParseExpr();
        p.SkipWs();
        if (p._pos < p._s.Length) throw new CalcException($"Unexpected '{p._s[p._pos]}' at position {p._pos + 1}.");
        if (double.IsNaN(value)) throw new CalcException("Result is not a number.");
        if (double.IsInfinity(value)) throw new CalcException("Result is too large (overflow).");
        return value;
    }

    private void SkipWs()
    {
        while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
    }

    private bool Match(char c)
    {
        SkipWs();
        if (_pos < _s.Length && _s[_pos] == c) { _pos++; return true; }
        return false;
    }

    private double ParseExpr()
    {
        var v = ParseTerm();
        while (true)
        {
            if (Match('+')) v += ParseTerm();
            else if (Match('-')) v -= ParseTerm();
            else return v;
        }
    }

    private double ParseTerm()
    {
        var v = ParseUnary();
        while (true)
        {
            if (Match('*')) v *= ParseUnary();
            else if (Match('/'))
            {
                var d = ParseUnary();
                if (d == 0) throw new CalcException("Division by zero.");
                v /= d;
            }
            else if (Match('%'))
            {
                var d = ParseUnary();
                if (d == 0) throw new CalcException("Modulo by zero.");
                v %= d;
            }
            else return v;
        }
    }

    // Unary minus binds looser than '^':  -3^2 = -(3^2) = -9
    private double ParseUnary()
    {
        if (Match('+')) return ParseUnary();
        if (Match('-')) return -ParseUnary();
        return ParsePower();
    }

    // Right-associative, exponent may be signed:  2^3^2 = 512,  2^-1 = 0.5
    private double ParsePower()
    {
        var b = ParsePostfix();
        if (Match('^')) return Math.Pow(b, ParseUnary());
        return b;
    }

    private double ParsePostfix()
    {
        var v = ParsePrimary();
        while (Match('!')) v = Factorial(v);
        return v;
    }

    private double ParsePrimary()
    {
        SkipWs();
        if (_pos >= _s.Length) throw new CalcException("Unexpected end of expression.");

        if (Match('('))
        {
            var v = ParseExpr();
            if (!Match(')')) throw new CalcException("Missing ')'.");
            return v;
        }

        var c = _s[_pos];
        if (char.IsDigit(c) || c == '.')
        {
            var start = _pos;
            while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == '.')) _pos++;
            // optional exponent: 1e5, 2.5E-3
            if (_pos < _s.Length && (_s[_pos] == 'e' || _s[_pos] == 'E') && _pos + 1 < _s.Length &&
                (char.IsDigit(_s[_pos + 1]) || ((_s[_pos + 1] == '+' || _s[_pos + 1] == '-') && _pos + 2 < _s.Length && char.IsDigit(_s[_pos + 2]))))
            {
                _pos += 2;
                while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
            }
            var token = _s[start.._pos];
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                throw new CalcException($"Invalid number '{token}'.");
            return num;
        }

        if (char.IsLetter(c))
        {
            var start = _pos;
            while (_pos < _s.Length && char.IsLetter(_s[_pos])) _pos++;
            var name = _s[start.._pos].ToLowerInvariant();

            switch (name)
            {
                case "pi": return Math.PI;
                case "e": return Math.E;
            }

            if (!Match('(')) throw new CalcException($"Expected '(' after '{name}'.");
            var arg = ParseExpr();
            if (!Match(')')) throw new CalcException("Missing ')'.");

            return name switch
            {
                "sqrt" => arg < 0 ? throw new CalcException("Square root of a negative number.") : Math.Sqrt(arg),
                "sin" => Math.Sin(arg),
                "cos" => Math.Cos(arg),
                "tan" => Math.Tan(arg),
                "log" => arg <= 0 ? throw new CalcException("log needs a positive number.") : Math.Log10(arg),
                "ln" => arg <= 0 ? throw new CalcException("ln needs a positive number.") : Math.Log(arg),
                "abs" => Math.Abs(arg),
                "exp" => Math.Exp(arg),
                "round" => Math.Round(arg, MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(arg),
                "ceil" => Math.Ceiling(arg),
                _ => throw new CalcException($"Unknown function '{name}'.")
            };
        }

        throw new CalcException($"Unexpected '{c}' at position {_pos + 1}.");
    }

    private static double Factorial(double n)
    {
        if (n < 0 || n != Math.Floor(n)) throw new CalcException("Factorial needs a whole number >= 0.");
        if (n > 170) throw new CalcException("Factorial is too large (max 170!).");
        double r = 1;
        for (var i = 2; i <= n; i++) r *= i;
        return r;
    }
}

public sealed class CalcException(string message) : Exception(message);
