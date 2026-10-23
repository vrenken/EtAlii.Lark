namespace EtAlii.Lark.Regexes;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Utilities for working with regular expressions written in Lark's (Python <c>re</c>) dialect:
/// escaping, translation to the .NET dialect and computation of match widths.
/// </summary>
public static class PyRegex
{
    /// <summary>Represents an unbounded maximum width.</summary>
    public const int MaxWidth = int.MaxValue;

    private const string SpecialChars = "()[]{}?*+-|^$\\.&~# \t\n\r\v\f";

    /// <summary>Equivalent of Python's <c>re.escape</c> (3.7+).</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length * 2);
        foreach (var c in value)
        {
            if (SpecialChars.IndexOf(c) >= 0)
            {
                switch (c)
                {
                    // Emit the whitespace escapes in a form that is also unambiguous for .NET.
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\v': sb.Append("\\v"); break;
                    case '\f': sb.Append("\\f"); break;
                    case ' ': sb.Append("\\ "); break;
                    default: sb.Append('\\').Append(c); break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static readonly ConcurrentDictionary<string, string> TranslationCache = new();

    /// <summary>Translates a Python style regular expression into an equivalent .NET regular expression.</summary>
    public static string ToDotNet(string pattern) => TranslationCache.GetOrAdd(pattern, p => new Translator(p).Translate());

    /// <summary>Translates the Python <c>re</c> flags (as letters) into .NET <see cref="RegexOptions"/>.</summary>
    public static RegexOptions OptionsFromFlags(string flags)
    {
        var options = RegexOptions.None;
        foreach (var f in flags)
        {
            options |= f switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                's' => RegexOptions.Singleline,
                'x' => RegexOptions.IgnorePatternWhitespace,
                _ => RegexOptions.None,
            };
        }
        return options;
    }

    private static readonly ConcurrentDictionary<(string, RegexOptions), Regex> RegexCache = new();

    /// <summary>Compiles a Python style regular expression into a .NET <see cref="Regex"/>.</summary>
    public static Regex Compile(string pattern, RegexOptions options = RegexOptions.None)
    {
        return RegexCache.GetOrAdd((pattern, options), key => new Regex(ToDotNet(key.Item1), key.Item2 | RegexOptions.CultureInvariant));
    }

    /// <summary>Compiles the pattern so that it only matches at the position it is started at (Python's <c>re.match</c>).</summary>
    public static Regex CompileAnchored(string pattern, RegexOptions options = RegexOptions.None)
    {
        return RegexCache.GetOrAdd(("\\G(?:" + pattern + ")", options), key => new Regex("\\G(?:" + ToDotNet(pattern) + ")", key.Item2 | RegexOptions.CultureInvariant));
    }

    /// <summary>Python's <c>re.fullmatch</c>.</summary>
    public static Match? FullMatch(string pattern, string text, RegexOptions options = RegexOptions.None)
    {
        var regex = RegexCache.GetOrAdd(("\\A(?:" + pattern + ")\\z", options), key => new Regex("\\A(?:" + ToDotNet(pattern) + ")\\z", key.Item2 | RegexOptions.CultureInvariant));
        var m = regex.Match(text);
        return m.Success ? m : null;
    }

    /// <summary>Python's <c>re.match</c>: matches only at the beginning of the string.</summary>
    public static Match? MatchStart(string pattern, string text, RegexOptions options = RegexOptions.None)
    {
        var m = CompileAnchored(pattern, options).Match(text);
        return m.Success ? m : null;
    }

    /// <summary>
    /// Returns the minimum and maximum number of characters the given (Python style) regular expression can match.
    /// An unbounded maximum is reported as <see cref="MaxWidth"/>.
    /// </summary>
    public static (int Min, int Max) GetWidth(string pattern)
    {
        try
        {
            return new WidthCalculator(pattern).Calculate();
        }
        catch (FormatException)
        {
            // Fall back to the most important information: whether the empty string is matched.
            Regex regex;
            try
            {
                regex = Compile(pattern);
            }
            catch (ArgumentException)
            {
                throw new ArgumentException(pattern);
            }
            var m = regex.Match("");
            return m.Success && m.Index == 0 ? (0, MaxWidth) : (1, MaxWidth);
        }
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>Translates the Python regex dialect into the .NET dialect.</summary>
    private sealed class Translator
    {
        private readonly string _p;
        private readonly StringBuilder _sb = new();
        private int _i;
        private readonly Stack<bool> _verbose = new();

        public Translator(string pattern)
        {
            _p = pattern;
        }

        private bool Verbose => _verbose.Count > 0 && _verbose.Peek();

        public string Translate()
        {
            _verbose.Push(false);
            while (_i < _p.Length)
            {
                var c = _p[_i];
                if (Verbose && c == '#')
                {
                    while (_i < _p.Length && _p[_i] != '\n') _sb.Append(_p[_i++]);
                    continue;
                }
                switch (c)
                {
                    case '\\':
                        TranslateEscape(inClass: false);
                        break;
                    case '[':
                        TranslateClass();
                        break;
                    case '(':
                        TranslateGroupOpen();
                        break;
                    case ')':
                        if (_verbose.Count > 1) _verbose.Pop();
                        _sb.Append(c);
                        _i++;
                        break;
                    case '{':
                        TranslateBrace();
                        break;
                    default:
                        _sb.Append(c);
                        _i++;
                        break;
                }
            }
            return _sb.ToString();
        }

        private void TranslateBrace()
        {
            // Python supports {,n} meaning {0,n}. .NET treats it as a literal.
            var j = _i + 1;
            if (j < _p.Length && _p[j] == ',')
            {
                var k = j + 1;
                while (k < _p.Length && char.IsAsciiDigit(_p[k])) k++;
                if (k > j + 1 && k < _p.Length && _p[k] == '}')
                {
                    _sb.Append("{0,").Append(_p, j + 1, k - j - 1).Append('}');
                    _i = k + 1;
                    return;
                }
            }
            _sb.Append('{');
            _i++;
        }

        private void TranslateGroupOpen()
        {
            if (_i + 1 < _p.Length && _p[_i + 1] == '?')
            {
                var rest = _p.AsSpan(_i);
                if (rest.StartsWith("(?P<"))
                {
                    _sb.Append("(?<");
                    _i += 4;
                    _verbose.Push(Verbose);
                    return;
                }
                if (rest.StartsWith("(?P="))
                {
                    var end = _p.IndexOf(')', _i);
                    if (end < 0) throw new ArgumentException("Unterminated group reference in " + _p);
                    _sb.Append("\\k<").Append(_p, _i + 4, end - _i - 4).Append('>');
                    _i = end + 1;
                    return;
                }
                if (rest.StartsWith("(?#"))
                {
                    var end = _p.IndexOf(')', _i);
                    if (end < 0) throw new ArgumentException("Unterminated comment in " + _p);
                    _i = end + 1;
                    return;
                }
                // Flag groups: (?aiLmsux-imsx:...) or (?aiLmsux)
                var j = _i + 2;
                var on = new StringBuilder();
                var off = new StringBuilder();
                var target = on;
                while (j < _p.Length && (char.IsAsciiLetter(_p[j]) || _p[j] == '-'))
                {
                    if (_p[j] == '-') target = off;
                    else target.Append(_p[j]);
                    j++;
                }
                if (j > _i + 2 && j < _p.Length && (_p[j] == ':' || _p[j] == ')'))
                {
                    var onFlags = new string(on.ToString().Where(f => "imsx".Contains(f)).ToArray());
                    var offFlags = new string(off.ToString().Where(f => "imsx".Contains(f)).ToArray());
                    var verbose = Verbose;
                    if (onFlags.Contains('x')) verbose = true;
                    if (offFlags.Contains('x')) verbose = false;
                    var flagText = onFlags + (offFlags.Length > 0 ? "-" + offFlags : "");
                    if (_p[j] == ':')
                    {
                        _sb.Append(flagText.Length > 0 ? "(?" + flagText + ":" : "(?:");
                        _verbose.Push(verbose);
                    }
                    else
                    {
                        if (flagText.Length > 0) _sb.Append("(?" + flagText + ")");
                        // Global flags apply to the remainder of the enclosing group.
                        if (_verbose.Count > 0)
                        {
                            _verbose.Pop();
                            _verbose.Push(verbose);
                        }
                    }
                    _i = j + 1;
                    return;
                }
            }
            _sb.Append('(');
            _i++;
            _verbose.Push(Verbose);
        }

        private void TranslateClass()
        {
            _sb.Append('[');
            _i++;
            if (_i < _p.Length && _p[_i] == '^')
            {
                _sb.Append('^');
                _i++;
            }
            if (_i < _p.Length && _p[_i] == ']')
            {
                _sb.Append("\\]");
                _i++;
            }
            while (_i < _p.Length)
            {
                var c = _p[_i];
                if (c == ']')
                {
                    _sb.Append(']');
                    _i++;
                    return;
                }
                if (c == '\\')
                {
                    TranslateEscape(inClass: true);
                    continue;
                }
                if (c == '[')
                {
                    // Python treats '[' inside a class as a literal; .NET may interpret "-[" as class subtraction.
                    _sb.Append("\\[");
                    _i++;
                    continue;
                }
                _sb.Append(c);
                _i++;
            }
            throw new ArgumentException("Unterminated character class in " + _p);
        }

        private void TranslateEscape(bool inClass)
        {
            if (_i + 1 >= _p.Length)
            {
                throw new ArgumentException("Pattern ends with a backslash: " + _p);
            }
            var n = _p[_i + 1];
            switch (n)
            {
                case 'Z':
                    _sb.Append(inClass ? "Z" : "\\z");
                    _i += 2;
                    return;
                case 'U':
                {
                    if (_i + 10 > _p.Length) throw new ArgumentException("Bad \\U escape in " + _p);
                    var hex = _p.Substring(_i + 2, 8);
                    var cp = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var s = char.ConvertFromUtf32(cp);
                    foreach (var ch in s) _sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    _i += 10;
                    return;
                }
                case 'N':
                    throw new ArgumentException("Named unicode escapes (\\N{...}) are not supported: " + _p);
                case 'x':
                {
                    // Python requires exactly two hex digits.
                    _sb.Append("\\x");
                    _i += 2;
                    return;
                }
                default:
                    if (char.IsAsciiDigit(n) && n != '0' && !inClass)
                    {
                        // Back reference; .NET uses the same syntax but disambiguate from following digits.
                        var j = _i + 1;
                        while (j < _p.Length && j < _i + 3 && char.IsAsciiDigit(_p[j])) j++;
                        _sb.Append("\\").Append(_p, _i + 1, j - _i - 1);
                        _i = j;
                        return;
                    }
                    _sb.Append('\\').Append(n);
                    _i += 2;
                    return;
            }
        }
    }

    /// <summary>
    /// Computes the width of a Python style regular expression, mirroring <c>sre_parse.getwidth</c>.
    /// </summary>
    private sealed class WidthCalculator
    {
        private readonly string _p;
        private int _i;
        private readonly Dictionary<string, (long, long)> _namedGroups = new();
        private readonly Dictionary<int, (long, long)> _groups = new();
        private int _groupCount;
        private readonly Stack<bool> _verbose = new();

        public WidthCalculator(string pattern)
        {
            _p = pattern;
        }

        private bool Verbose => _verbose.Count > 0 && _verbose.Peek();

        public (int, int) Calculate()
        {
            _verbose.Push(false);
            var (lo, hi) = ParseAlternation();
            if (_i < _p.Length) throw new FormatException("Unbalanced parenthesis in " + _p);
            return (Cap(lo), Cap(hi));
        }

        private static int Cap(long v) => v >= MaxWidth ? MaxWidth : (int)v;

        private static long Add(long a, long b) => a >= MaxWidth || b >= MaxWidth ? MaxWidth : Math.Min(a + b, MaxWidth);

        private static long Mul(long a, long b)
        {
            if (a == 0 || b == 0) return 0;
            if (a >= MaxWidth || b >= MaxWidth) return MaxWidth;
            return Math.Min(a * b, MaxWidth);
        }

        private (long, long) ParseAlternation()
        {
            long lo = long.MaxValue, hi = 0;
            while (true)
            {
                var (l, h) = ParseSequence();
                lo = Math.Min(lo, l);
                hi = Math.Max(hi, h);
                if (_i < _p.Length && _p[_i] == '|')
                {
                    _i++;
                    continue;
                }
                break;
            }
            return (lo, hi);
        }

        private void SkipVerbose()
        {
            while (Verbose && _i < _p.Length)
            {
                var c = _p[_i];
                if (char.IsWhiteSpace(c))
                {
                    _i++;
                }
                else if (c == '#')
                {
                    while (_i < _p.Length && _p[_i] != '\n') _i++;
                }
                else
                {
                    break;
                }
            }
        }

        private (long, long) ParseSequence()
        {
            long lo = 0, hi = 0;
            while (true)
            {
                SkipVerbose();
                if (_i >= _p.Length || _p[_i] == '|' || _p[_i] == ')') break;
                var (l, h) = ParseItem();
                lo = Add(lo, l);
                hi = Add(hi, h);
            }
            return (lo, hi);
        }

        private (long, long) ParseItem()
        {
            var (lo, hi) = ParseAtom();
            while (true)
            {
                SkipVerbose();
                if (_i >= _p.Length) break;
                var c = _p[_i];
                long min, max;
                if (c == '*') { min = 0; max = MaxWidth; _i++; }
                else if (c == '+') { min = 1; max = MaxWidth; _i++; }
                else if (c == '?') { min = 0; max = 1; _i++; }
                else if (c == '{' && TryParseRepeat(out min, out max)) { }
                else break;

                // Lazy / possessive suffix
                if (_i < _p.Length && (_p[_i] == '?' || _p[_i] == '+')) _i++;

                lo = Mul(lo, min);
                if (max >= MaxWidth && hi > 0) hi = MaxWidth;
                else hi = Mul(hi, max);
            }
            return (lo, hi);
        }

        private bool TryParseRepeat(out long min, out long max)
        {
            min = max = 0;
            var j = _i + 1;
            var start = j;
            while (j < _p.Length && char.IsAsciiDigit(_p[j])) j++;
            var minText = _p.Substring(start, j - start);
            string? maxText;
            if (j < _p.Length && _p[j] == ',')
            {
                j++;
                var s2 = j;
                while (j < _p.Length && char.IsAsciiDigit(_p[j])) j++;
                maxText = _p.Substring(s2, j - s2);
            }
            else
            {
                maxText = null;
            }
            if (j >= _p.Length || _p[j] != '}') return false;
            if (minText.Length == 0 && maxText is null) return false;
            min = minText.Length == 0 ? 0 : long.Parse(minText, CultureInfo.InvariantCulture);
            max = maxText is null ? min : maxText.Length == 0 ? MaxWidth : long.Parse(maxText, CultureInfo.InvariantCulture);
            _i = j + 1;
            return true;
        }

        private (long, long) ParseAtom()
        {
            var c = _p[_i];
            switch (c)
            {
                case '(':
                    return ParseGroup();
                case '[':
                    SkipClass();
                    return (1, 1);
                case '^':
                case '$':
                    _i++;
                    return (0, 0);
                case '\\':
                    return ParseEscape();
                case '*':
                case '+':
                case '?':
                    throw new FormatException("Nothing to repeat in " + _p);
                default:
                    _i++;
                    return (1, 1);
            }
        }

        private (long, long) ParseEscape()
        {
            if (_i + 1 >= _p.Length) throw new FormatException("Bad escape at end of " + _p);
            var n = _p[_i + 1];
            switch (n)
            {
                case 'A': case 'Z': case 'b': case 'B': case 'z': case 'G':
                    _i += 2;
                    return (0, 0);
                case 'x':
                    _i += 4;
                    return (1, 1);
                case 'u':
                    _i += 6;
                    return (1, 1);
                case 'U':
                    _i += 10;
                    return (1, 1);
                case 'p': case 'P':
                    if (_i + 2 < _p.Length && _p[_i + 2] == '{')
                    {
                        var end = _p.IndexOf('}', _i);
                        if (end < 0) throw new FormatException("Bad \\p escape in " + _p);
                        _i = end + 1;
                    }
                    else
                    {
                        _i += 3;
                    }
                    return (1, 1);
                case 'N':
                {
                    var end = _p.IndexOf('}', _i);
                    if (end < 0) throw new FormatException("Bad \\N escape in " + _p);
                    _i = end + 1;
                    return (1, 1);
                }
                case '0':
                {
                    var j = _i + 2;
                    while (j < _p.Length && j < _i + 4 && _p[j] is >= '0' and <= '7') j++;
                    _i = j;
                    return (1, 1);
                }
                default:
                    if (char.IsAsciiDigit(n))
                    {
                        var j = _i + 1;
                        while (j < _p.Length && j < _i + 3 && char.IsAsciiDigit(_p[j])) j++;
                        var group = int.Parse(_p.AsSpan(_i + 1, j - _i - 1), CultureInfo.InvariantCulture);
                        _i = j;
                        if (_groups.TryGetValue(group, out var w)) return w;
                        throw new FormatException("Invalid group reference in " + _p);
                    }
                    _i += 2;
                    return (1, 1);
            }
        }

        private void SkipClass()
        {
            _i++;
            if (_i < _p.Length && _p[_i] == '^') _i++;
            if (_i < _p.Length && _p[_i] == ']') _i++;
            while (_i < _p.Length)
            {
                var c = _p[_i];
                if (c == '\\')
                {
                    _i += 2;
                    continue;
                }
                _i++;
                if (c == ']') return;
            }
            throw new FormatException("Unterminated character set in " + _p);
        }

        private (long, long) ParseGroup()
        {
            var start = _i;
            _i++;
            string? name = null;
            var capturing = true;
            var zeroWidth = false;
            var verbose = Verbose;

            if (_i < _p.Length && _p[_i] == '?')
            {
                _i++;
                if (_i >= _p.Length) throw new FormatException("Unexpected end of pattern " + _p);
                var c = _p[_i];
                if (c == ':' ) { capturing = false; _i++; }
                else if (c == '>') { capturing = false; _i++; }
                else if (c == '=' || c == '!') { capturing = false; zeroWidth = true; _i++; }
                else if (c == '<' && _i + 1 < _p.Length && (_p[_i + 1] == '=' || _p[_i + 1] == '!'))
                {
                    capturing = false;
                    zeroWidth = true;
                    _i += 2;
                }
                else if (c == '#')
                {
                    var end = _p.IndexOf(')', _i);
                    if (end < 0) throw new FormatException("Missing ) in " + _p);
                    _i = end + 1;
                    return (0, 0);
                }
                else if (c == 'P' && _i + 1 < _p.Length && _p[_i + 1] == '=')
                {
                    var end = _p.IndexOf(')', _i);
                    if (end < 0) throw new FormatException("Missing ) in " + _p);
                    var refName = _p.Substring(_i + 2, end - _i - 2);
                    _i = end + 1;
                    if (_namedGroups.TryGetValue(refName, out var w)) return w;
                    throw new FormatException("Unknown group name in " + _p);
                }
                else if ((c == 'P' && _i + 1 < _p.Length && _p[_i + 1] == '<') || c == '<')
                {
                    _i += c == 'P' ? 2 : 1;
                    var end = _p.IndexOf('>', _i);
                    if (end < 0) throw new FormatException("Missing > in " + _p);
                    name = _p.Substring(_i, end - _i);
                    _i = end + 1;
                }
                else if (c == '(')
                {
                    // Conditional group (?(id/name)yes|no)
                    var end = _p.IndexOf(')', _i);
                    if (end < 0) throw new FormatException("Missing ) in " + _p);
                    _i = end + 1;
                    _verbose.Push(verbose);
                    var (lo, hi) = ParseAlternation();
                    _verbose.Pop();
                    Expect(')');
                    // A conditional without an explicit "no" branch can match empty
                    var text = _p.Substring(start, _i - start);
                    if (!text.Contains('|')) lo = 0;
                    return (lo, hi);
                }
                else
                {
                    // Flags
                    var flagsOn = true;
                    while (_i < _p.Length && (char.IsAsciiLetter(_p[_i]) || _p[_i] == '-'))
                    {
                        if (_p[_i] == '-') flagsOn = false;
                        else if (_p[_i] == 'x') verbose = flagsOn;
                        _i++;
                    }
                    if (_i >= _p.Length) throw new FormatException("Unexpected end of pattern " + _p);
                    if (_p[_i] == ')')
                    {
                        _i++;
                        if (_verbose.Count > 0)
                        {
                            _verbose.Pop();
                            _verbose.Push(verbose);
                        }
                        return (0, 0);
                    }
                    if (_p[_i] != ':') throw new FormatException("Unknown extension in " + _p);
                    _i++;
                    capturing = false;
                }
            }

            var groupIndex = capturing ? ++_groupCount : 0;
            _verbose.Push(verbose);
            var width = ParseAlternation();
            _verbose.Pop();
            Expect(')');

            if (capturing)
            {
                _groups[groupIndex] = width;
                if (name is not null) _namedGroups[name] = width;
            }
            return zeroWidth ? (0, 0) : width;
        }

        private void Expect(char c)
        {
            if (_i >= _p.Length || _p[_i] != c) throw new FormatException($"Expected '{c}' in " + _p);
            _i++;
        }
    }
}
