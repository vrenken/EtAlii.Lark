namespace EtAlii.Lark.Regexes;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Detection of collisions between regular expression terminals of the same priority (used in strict mode, and for warnings).
/// </summary>
/// <remarks>
/// Upstream Lark relies on the <c>interegular</c> package to compute the intersection of regular expressions.
/// This implementation generates sample strings from the structure of each regular expression, and checks whether
/// a sample fully matches both terminals. A reported collision is always real (the example is verified), but some
/// collisions may go undetected.
/// </remarks>
internal static class RegexCollisions
{
    private const int MaxCollisionsToShow = 8;

    public static void Check(IReadOnlyList<TerminalDef> terminals, RegexOptions options, bool strict)
    {
        var regexTerminals = terminals.Where(t => t.Pattern is PatternRE).ToList();
        var found = 0;
        foreach (var group in regexTerminals.GroupBy(t => t.Priority))
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    var example = FindOverlap(a, b, options) ?? FindOverlap(b, a, options);
                    if (example is null) continue;

                    var message = $"Collision between Terminals {a.Name} and {b.Name}. ";
                    var exampleText = $"Example of a string matched by both: {Utilities.Helpers.Repr(example)}";
                    if (strict) throw new LexException($"{message}\n{exampleText}");
                    LarkLog.Warning($"{message} The lexer will choose between them arbitrarily.\n{exampleText}");
                    if (++found >= MaxCollisionsToShow)
                    {
                        LarkLog.Warning($"Found {MaxCollisionsToShow} regex collisions, will not check for more.");
                        return;
                    }
                }
            }
        }
    }

    private static string? FindOverlap(TerminalDef a, TerminalDef b, RegexOptions options)
    {
        var ra = a.Pattern.ToRegexp();
        var rb = b.Pattern.ToRegexp();
        foreach (var sample in RegexSampler.Samples(ra))
        {
            if (sample.Length == 0) continue;
            if (PyRegex.FullMatch(ra, sample, options) is not null && PyRegex.FullMatch(rb, sample, options) is not null)
            {
                return sample;
            }
        }
        return null;
    }
}

/// <summary>Generates a bounded set of strings that (likely) match a regular expression in Lark's (Python) dialect.</summary>
internal static class RegexSampler
{
    private const int Limit = 256;

    private static readonly string Alphabet =
        "aAbBzZ09_ -+*/.,:;!?#$%&@'\"\\()[]{}<>=|^~`\t\n\r\u00e9\u00a3\u03b1";

    private abstract class Node
    {
        public abstract IEnumerable<string> Enumerate();
    }

    private sealed class Literal : Node
    {
        private readonly string[] _options;

        public Literal(params string[] options) => _options = options;

        public override IEnumerable<string> Enumerate() => _options;
    }

    private sealed class Seq : Node
    {
        public readonly List<Node> Items = new();

        public override IEnumerable<string> Enumerate()
        {
            IEnumerable<string> acc = new[] { "" };
            foreach (var item in Items)
            {
                var options = item.Enumerate().Take(Limit).ToList();
                if (options.Count == 0) return Array.Empty<string>();
                acc = acc.SelectMany(prefix => options.Select(o => prefix + o)).Take(Limit).ToList();
            }
            return acc;
        }
    }

    private sealed class Alt : Node
    {
        public readonly List<Node> Options = new();

        public override IEnumerable<string> Enumerate()
        {
            // Interleave the alternatives so that each gets represented within the limit.
            var enumerators = Options.Select(o => o.Enumerate().Take(Limit).ToList()).ToList();
            var max = enumerators.Count == 0 ? 0 : enumerators.Max(e => e.Count);
            var result = new List<string>();
            for (var i = 0; i < max && result.Count < Limit; i++)
            {
                foreach (var e in enumerators)
                {
                    if (i < e.Count) result.Add(e[i]);
                }
            }
            return result;
        }
    }

    private sealed class Repeat : Node
    {
        private readonly Node _node;
        private readonly int _min;
        private readonly int _max;

        public Repeat(Node node, int min, int max)
        {
            _node = node;
            _min = min;
            _max = max;
        }

        public override IEnumerable<string> Enumerate()
        {
            var counts = new[] { _min, _min + 1, _min + 2, _min + 3 }.Where(c => c <= _max).Distinct();
            var options = _node.Enumerate().Take(16).ToList();
            foreach (var count in counts)
            {
                var seq = new Seq();
                for (var i = 0; i < count; i++) seq.Items.Add(new Literal(options.ToArray()));
                foreach (var s in seq.Enumerate().Take(Limit / 4)) yield return s;
            }
        }
    }

    public static IEnumerable<string> Samples(string pattern)
    {
        Node node;
        try
        {
            node = new Parser(pattern).Parse();
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            return Array.Empty<string>();
        }
        return node.Enumerate().Distinct().Take(Limit * 4);
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _pos;
        private bool _ignoreCase;
        private bool _dotAll;
        private bool _verbose;

        public Parser(string s) => _s = s;

        public Node Parse()
        {
            var node = ParseAlt();
            if (_pos < _s.Length) throw new FormatException("Unbalanced parenthesis");
            return node;
        }

        private bool AtEnd => _pos >= _s.Length;

        private Node ParseAlt()
        {
            var alt = new Alt();
            alt.Options.Add(ParseSeq());
            while (!AtEnd && _s[_pos] == '|')
            {
                _pos++;
                alt.Options.Add(ParseSeq());
            }
            return alt.Options.Count == 1 ? alt.Options[0] : alt;
        }

        private Node ParseSeq()
        {
            var seq = new Seq();
            while (!AtEnd && _s[_pos] != '|' && _s[_pos] != ')')
            {
                if (_verbose)
                {
                    var c = _s[_pos];
                    if (char.IsWhiteSpace(c))
                    {
                        _pos++;
                        continue;
                    }
                    if (c == '#')
                    {
                        while (!AtEnd && _s[_pos] != '\n') _pos++;
                        continue;
                    }
                }
                var atom = ParseAtom();
                if (atom is null) continue;
                seq.Items.Add(ParseQuantifier(atom));
            }
            return seq;
        }

        private Node ParseQuantifier(Node atom)
        {
            if (AtEnd) return atom;
            int min, max;
            switch (_s[_pos])
            {
                case '*':
                    min = 0;
                    max = int.MaxValue;
                    _pos++;
                    break;
                case '+':
                    min = 1;
                    max = int.MaxValue;
                    _pos++;
                    break;
                case '?':
                    min = 0;
                    max = 1;
                    _pos++;
                    break;
                case '{':
                {
                    var close = _s.IndexOf('}', _pos);
                    var body = close < 0 ? null : _s.Substring(_pos + 1, close - _pos - 1);
                    var m = body is null ? null : Regex.Match(body, @"^(\d*)(,(\d*))?$");
                    if (m is null || !m.Success || (m.Groups[1].Value.Length == 0 && !m.Groups[2].Success)) return atom; // a literal '{'
                    min = m.Groups[1].Value.Length > 0 ? int.Parse(m.Groups[1].Value) : 0;
                    max = !m.Groups[2].Success ? min : m.Groups[3].Value.Length > 0 ? int.Parse(m.Groups[3].Value) : int.MaxValue;
                    _pos = close + 1;
                    break;
                }
                default:
                    return atom;
            }
            if (!AtEnd && (_s[_pos] == '?' || _s[_pos] == '+')) _pos++; // lazy / possessive
            return new Repeat(atom, min, max);
        }

        private Node? ParseAtom()
        {
            var c = _s[_pos++];
            switch (c)
            {
                case '(':
                    return ParseGroup();
                case '[':
                    return ParseClass();
                case '.':
                    return CharsMatching(ch => _dotAll || ch != '\n');
                case '^':
                case '$':
                    return null;
                case '\\':
                    return ParseEscape(inClass: false) is var (node, _) ? node : null;
                default:
                    return Char(c);
            }
        }

        private Node Char(char c)
        {
            if (_ignoreCase && char.ToUpperInvariant(c) != char.ToLowerInvariant(c))
            {
                return new Literal(c.ToString(), char.IsUpper(c) ? char.ToLowerInvariant(c).ToString() : char.ToUpperInvariant(c).ToString());
            }
            return new Literal(c.ToString());
        }

        private static Node CharsMatching(Func<char, bool> pred)
        {
            var chars = Alphabet.Where(pred).Select(ch => ch.ToString()).ToArray();
            return new Literal(chars);
        }

        private Node? ParseGroup()
        {
            var savedIgnoreCase = _ignoreCase;
            var savedDotAll = _dotAll;
            var savedVerbose = _verbose;
            var lookaround = false;
            if (!AtEnd && _s[_pos] == '?')
            {
                _pos++;
                var c = _s[_pos];
                if (c == ':')
                {
                    _pos++;
                }
                else if (c == 'P' && _s[_pos + 1] == '<')
                {
                    _pos = _s.IndexOf('>', _pos) + 1;
                }
                else if (c == 'P' && _s[_pos + 1] == '=')
                {
                    throw new FormatException("Backreferences are not supported");
                }
                else if (c == '<' && (_s[_pos + 1] == '=' || _s[_pos + 1] == '!'))
                {
                    _pos += 2;
                    lookaround = true;
                }
                else if (c == '<')
                {
                    _pos = _s.IndexOf('>', _pos) + 1;
                }
                else if (c is '=' or '!')
                {
                    _pos++;
                    lookaround = true;
                }
                else if (c == '#')
                {
                    _pos = _s.IndexOf(')', _pos) + 1;
                    return null;
                }
                else if (c == '(')
                {
                    throw new FormatException("Conditionals are not supported");
                }
                else
                {
                    // Inline flags: (?imsx) or (?imsx-imsx:...)
                    var start = _pos;
                    while (!AtEnd && _s[_pos] != ':' && _s[_pos] != ')') _pos++;
                    var flags = _s[start.._pos];
                    var on = flags.Split('-')[0];
                    if (on.Contains('i')) _ignoreCase = true;
                    if (on.Contains('s')) _dotAll = true;
                    if (on.Contains('x')) _verbose = true;
                    if (_s[_pos] == ')')
                    {
                        // Global flags: apply to the rest of the pattern.
                        _pos++;
                        return null;
                    }
                    _pos++;
                }
            }

            var inner = ParseAlt();
            if (AtEnd || _s[_pos] != ')') throw new FormatException("Missing )");
            _pos++;
            _ignoreCase = savedIgnoreCase;
            _dotAll = savedDotAll;
            _verbose = savedVerbose;
            return lookaround ? null : inner;
        }

        private (Node Node, Func<char, bool> Pred)? ParseEscape(bool inClass)
        {
            var c = _s[_pos++];
            switch (c)
            {
                case 'd': return Class(char.IsDigit);
                case 'D': return Class(ch => !char.IsDigit(ch));
                case 'w': return Class(ch => char.IsLetterOrDigit(ch) || ch == '_');
                case 'W': return Class(ch => !(char.IsLetterOrDigit(ch) || ch == '_'));
                case 's': return Class(char.IsWhiteSpace);
                case 'S': return Class(ch => !char.IsWhiteSpace(ch));
                case 'b' when !inClass:
                case 'B':
                case 'A':
                case 'Z':
                case 'z':
                case 'G':
                    return null;
                case 'n': return Single('\n');
                case 't': return Single('\t');
                case 'r': return Single('\r');
                case 'f': return Single('\f');
                case 'v': return Single('\v');
                case 'a': return Single('\a');
                case 'b': return Single('\b');
                case '0': return Single('\0');
                case 'x': return Single((char)Convert.ToInt32(Take(2), 16));
                case 'u': return Single((char)Convert.ToInt32(Take(4), 16));
                case 'U':
                {
                    var s = char.ConvertFromUtf32(Convert.ToInt32(Take(8), 16));
                    return (new Literal(s), ch => s.Length == 1 && ch == s[0]);
                }
                case 'p':
                case 'P':
                {
                    var close = _s.IndexOf('}', _pos);
                    var name = _s.Substring(_pos + 1, close - _pos - 1);
                    _pos = close + 1;
                    var regex = new Regex($"\\{c}{{{name}}}");
                    return Class(ch => regex.IsMatch(ch.ToString()));
                }
                default:
                    if (char.IsDigit(c)) throw new FormatException("Backreferences are not supported");
                    return Single(c);
            }

            (Node, Func<char, bool>) Single(char ch) => (Char(ch), x => x == ch);

            (Node, Func<char, bool>) Class(Func<char, bool> pred) => (CharsMatching(pred), pred);
        }

        private string Take(int n)
        {
            var s = _s.Substring(_pos, n);
            _pos += n;
            return s;
        }

        private Node ParseClass()
        {
            var negate = false;
            if (_s[_pos] == '^')
            {
                negate = true;
                _pos++;
            }
            var preds = new List<Func<char, bool>>();
            var explicitChars = new StringBuilder();
            var first = true;
            while (_s[_pos] != ']' || first)
            {
                first = false;
                char lo;
                Func<char, bool>? pred = null;
                if (_s[_pos] == '\\')
                {
                    _pos++;
                    var esc = ParseEscape(inClass: true);
                    if (esc is null) continue;
                    pred = esc.Value.Pred;
                    var sample = esc.Value.Node.Enumerate().FirstOrDefault();
                    if (sample is { Length: 1 } && _s[_pos] == '-' && _s[_pos + 1] != ']')
                    {
                        lo = sample[0];
                    }
                    else
                    {
                        preds.Add(pred);
                        foreach (var ch in esc.Value.Node.Enumerate()) explicitChars.Append(ch);
                        continue;
                    }
                }
                else
                {
                    lo = _s[_pos++];
                }

                if (_s[_pos] == '-' && _s[_pos + 1] != ']')
                {
                    _pos++;
                    char hi;
                    if (_s[_pos] == '\\')
                    {
                        _pos++;
                        var esc = ParseEscape(inClass: true);
                        hi = esc?.Node.Enumerate().First()[0] ?? lo;
                    }
                    else
                    {
                        hi = _s[_pos++];
                    }
                    var l = lo;
                    var h = hi;
                    preds.Add(ch => ch >= l && ch <= h);
                    explicitChars.Append(l).Append(h).Append((char)((l + h) / 2));
                }
                else
                {
                    var l = lo;
                    preds.Add(ch => ch == l);
                    explicitChars.Append(l);
                }
            }
            _pos++; // ']'

            bool Matches(char ch)
            {
                var m = preds.Any(p => p(ch) || (_ignoreCase && (p(char.ToLowerInvariant(ch)) || p(char.ToUpperInvariant(ch)))));
                return negate ? !m : m;
            }

            var candidates = (negate ? Alphabet : explicitChars + Alphabet).Distinct().Where(Matches).Select(ch => ch.ToString()).ToArray();
            return new Literal(candidates);
        }
    }
}
