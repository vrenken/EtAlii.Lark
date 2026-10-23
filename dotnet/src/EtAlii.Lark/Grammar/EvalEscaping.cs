namespace EtAlii.Lark;

using System.Globalization;
using System.Text;

/// <summary>Evaluation of the escapes in grammar literals, mirroring upstream Lark (which uses Python's literal_eval).</summary>
internal static class EvalEscaping
{
    public static string Eval(string s)
    {
        var w = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var n = s[i];
            w.Append(n);
            if (n == '\\')
            {
                if (i + 1 >= s.Length)
                {
                    throw new GrammarException($"Literal ended unexpectedly (bad escaping): `{s}`");
                }
                var n2 = s[++i];
                if (n2 == '\\')
                {
                    w.Append("\\\\");
                }
                else if ("Uuxnftr".IndexOf(n2) < 0)
                {
                    w.Append('\\');
                }
                w.Append(n2);
            }
        }
        var str = w.ToString().Replace("\\\"", "\"").Replace("'", "\\'");
        try
        {
            return LiteralEval(str);
        }
        catch (FormatException e)
        {
            throw new GrammarException(s + ": " + e.Message, e);
        }
    }

    /// <summary>Evaluates the escapes of a Python (unicode) string literal body.</summary>
    private static string LiteralEval(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 >= s.Length) throw new FormatException("EOL while scanning string literal");
            var e = s[++i];
            switch (e)
            {
                case '\n': break;
                case '\\': sb.Append('\\'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'v': sb.Append('\v'); break;
                case 'x':
                    sb.Append((char)ParseHex(s, ref i, 2));
                    break;
                case 'u':
                    sb.Append((char)ParseHex(s, ref i, 4));
                    break;
                case 'U':
                {
                    var cp = ParseHex(s, ref i, 8);
                    if (cp > 0x10FFFF) throw new FormatException("illegal Unicode character");
                    sb.Append(char.ConvertFromUtf32(cp));
                    break;
                }
                case 'N':
                    throw new FormatException("\\N{...} escapes are not supported");
                default:
                    if (e is >= '0' and <= '7')
                    {
                        var value = e - '0';
                        var count = 1;
                        while (count < 3 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7')
                        {
                            value = value * 8 + (s[++i] - '0');
                            count++;
                        }
                        sb.Append((char)value);
                    }
                    else
                    {
                        // Unknown escapes are kept as-is
                        sb.Append('\\').Append(e);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static int ParseHex(string s, ref int i, int digits)
    {
        if (i + digits >= s.Length + 0 && i + digits > s.Length - 1 + 1)
        {
            throw new FormatException($"truncated \\{s[i]}{new string('X', digits)} escape");
        }
        var hex = s.Substring(i + 1, Math.Min(digits, s.Length - i - 1));
        if (hex.Length != digits || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"truncated \\{s[i]}{new string('X', digits)} escape");
        }
        i += digits;
        return value;
    }
}
