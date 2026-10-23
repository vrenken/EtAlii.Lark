namespace EtAlii.Lark.Tests;

using System.Globalization;
using System.Text;

/// <summary>
/// Evaluates a small subset of Python literals, so that expected values of the upstream tests can be copied verbatim:
/// <c>Tree('a', ['b', Token('B', 'b')])</c>, lists, tuples, strings, ints, floats, True/False/None.
/// </summary>
internal static class Py
{
    public static object? Eval(string source)
    {
        var p = new Parser(source);
        var v = p.ParseValue();
        p.SkipWs();
        if (!p.AtEnd) throw new FormatException($"Unexpected input at {p.Pos}: {source[p.Pos..]}");
        return v;
    }

    public static Tree Tree(string source) => (Tree)Eval(source)!;

    public static List<object?> List(string source) => (List<object?>)Eval(source)!;

    private sealed class Parser
    {
        private readonly string _s;

        public Parser(string s) => _s = s;

        public int Pos { get; private set; }

        public bool AtEnd => Pos >= _s.Length;

        public void SkipWs()
        {
            while (Pos < _s.Length && (char.IsWhiteSpace(_s[Pos]) || _s[Pos] == '#'))
            {
                if (_s[Pos] == '#')
                {
                    while (Pos < _s.Length && _s[Pos] != '\n') Pos++;
                }
                else
                {
                    Pos++;
                }
            }
        }

        private void Expect(char c)
        {
            SkipWs();
            if (Pos >= _s.Length || _s[Pos] != c) throw new FormatException($"Expected '{c}' at {Pos}: {_s[Pos..]}");
            Pos++;
        }

        private bool TryConsume(char c)
        {
            SkipWs();
            if (Pos < _s.Length && _s[Pos] == c)
            {
                Pos++;
                return true;
            }
            return false;
        }

        public object? ParseValue()
        {
            SkipWs();
            var c = _s[Pos];
            if (c is '\'' or '"') return ParseString();
            if (c is '[' or '(')
            {
                var close = c == '[' ? ']' : ')';
                Pos++;
                var items = new List<object?>();
                while (!TryConsume(close))
                {
                    items.Add(ParseValue());
                    if (!TryConsume(','))
                    {
                        Expect(close);
                        break;
                    }
                }
                return items;
            }
            if (c == '{')
            {
                // A set: represented as a list.
                Pos++;
                var items = new List<object?>();
                while (!TryConsume('}'))
                {
                    items.Add(ParseValue());
                    if (!TryConsume(','))
                    {
                        Expect('}');
                        break;
                    }
                }
                return items;
            }
            if (char.IsDigit(c) || c == '-')
            {
                var start = Pos;
                Pos++;
                while (Pos < _s.Length && (char.IsDigit(_s[Pos]) || _s[Pos] == '.')) Pos++;
                var num = _s[start..Pos];
                return num.Contains('.') ? double.Parse(num, CultureInfo.InvariantCulture) : int.Parse(num, CultureInfo.InvariantCulture);
            }

            var identStart = Pos;
            while (Pos < _s.Length && (char.IsLetterOrDigit(_s[Pos]) || _s[Pos] == '_' || _s[Pos] == '.')) Pos++;
            var ident = _s[identStart..Pos];
            switch (ident)
            {
                case "None": return null;
                case "True": return true;
                case "False": return false;
            }

            var args = ParseArgs();
            switch (ident)
            {
                case "Tree":
                case "ST":
                    return new Tree((string)args[0]!, args.Count > 1 ? (List<object?>)args[1]! : new List<object?>());
                case "Token":
                    return new Token((string)args[0]!, args[1]?.ToString() ?? "");
                default:
                    throw new FormatException($"Unknown callable {ident}");
            }
        }

        private List<object?> ParseArgs()
        {
            Expect('(');
            var args = new List<object?>();
            while (!TryConsume(')'))
            {
                args.Add(ParseValue());
                if (!TryConsume(','))
                {
                    Expect(')');
                    break;
                }
            }
            return args;
        }

        private string ParseString()
        {
            var q = _s[Pos++];
            var sb = new StringBuilder();
            while (_s[Pos] != q)
            {
                var c = _s[Pos++];
                if (c == '\\')
                {
                    var e = _s[Pos++];
                    sb.Append(e switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        _ => e,
                    });
                }
                else
                {
                    sb.Append(c);
                }
            }
            Pos++;
            return sb.ToString();
        }
    }
}
