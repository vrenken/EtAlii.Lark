namespace EtAlii.Lark.Utilities;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

internal static class Helpers
{
    public static Dictionary<TKey, List<TValue>> Classify<TItem, TKey, TValue>(IEnumerable<TItem> seq, Func<TItem, TKey> key, Func<TItem, TValue> value)
        where TKey : notnull
    {
        var d = new Dictionary<TKey, List<TValue>>();
        foreach (var item in seq)
        {
            var k = key(item);
            if (!d.TryGetValue(k, out var list))
            {
                list = new List<TValue>();
                d[k] = list;
            }
            list.Add(value(item));
        }
        return d;
    }

    public static Dictionary<TKey, List<TItem>> Classify<TItem, TKey>(IEnumerable<TItem> seq, Func<TItem, TKey> key) where TKey : notnull
        => Classify(seq, key, x => x);

    public static (List<T> True, List<T> False) ClassifyBool<T>(IEnumerable<T> seq, Func<T, bool> pred)
    {
        var t = new List<T>();
        var f = new List<T>();
        foreach (var item in seq)
        {
            if (pred(item)) t.Add(item); else f.Add(item);
        }
        return (t, f);
    }

    public static List<T> DedupList<T>(IEnumerable<T> items) where T : notnull
        => new OrderedSet<T>(items).ToList();

    public static IEnumerable<T> Bfs<T>(IEnumerable<T> initial, Func<T, IEnumerable<T>> expand) where T : notnull
    {
        var openQ = new Queue<T>();
        var visited = new HashSet<T>();
        foreach (var i in initial)
        {
            if (visited.Add(i)) openQ.Enqueue(i);
        }
        while (openQ.Count > 0)
        {
            var node = openQ.Dequeue();
            yield return node;
            foreach (var next in expand(node))
            {
                if (visited.Add(next)) openQ.Enqueue(next);
            }
        }
    }

    public static IEnumerable<T> BfsAllUnique<T>(IEnumerable<T> initial, Func<T, IEnumerable<T>> expand)
    {
        var openQ = new Queue<T>(initial);
        while (openQ.Count > 0)
        {
            var node = openQ.Dequeue();
            yield return node;
            foreach (var next in expand(node)) openQ.Enqueue(next);
        }
    }

    /// <summary>
    /// Splits n up into smaller factors and summands &lt;= max_factor.
    /// </summary>
    public static List<(int A, int B)> SmallFactors(int n, int maxFactor)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (maxFactor <= 2) throw new ArgumentOutOfRangeException(nameof(maxFactor));
        if (n <= maxFactor) return new List<(int, int)> { (n, 0) };

        for (var a = maxFactor; a > 1; a--)
        {
            var r = n / a;
            var b = n % a;
            if (a + b <= maxFactor)
            {
                var result = SmallFactors(r, maxFactor);
                result.Add((a, b));
                return result;
            }
        }
        throw new InvalidOperationException($"Failed to factorize {n}");
    }

    /// <summary>Equivalent of Python's <c>str.isupper()</c>.</summary>
    public static bool IsUpper(string s)
    {
        var cased = false;
        foreach (var c in s)
        {
            if (char.IsLower(c)) return false;
            if (char.IsUpper(c)) cased = true;
        }
        return cased;
    }

    /// <summary>Equivalent of Python's <c>str.islower()</c>.</summary>
    public static bool IsLower(string s)
    {
        var cased = false;
        foreach (var c in s)
        {
            if (char.IsUpper(c)) return false;
            if (char.IsLower(c)) cased = true;
        }
        return cased;
    }

    private static readonly UnicodeCategory[] IdStart =
    {
        UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter,
        UnicodeCategory.ModifierLetter, UnicodeCategory.OtherLetter, UnicodeCategory.NonSpacingMark,
        UnicodeCategory.SpacingCombiningMark, UnicodeCategory.ConnectorPunctuation,
    };

    private static readonly UnicodeCategory[] IdContinue = IdStart
        .Concat(new[] { UnicodeCategory.DecimalDigitNumber, UnicodeCategory.LetterNumber })
        .ToArray();

    private static bool TestUnicodeCategory(string s, UnicodeCategory[] categories)
    {
        if (s.Length == 0) return true;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '_') continue;
            UnicodeCategory cat;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cat = CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, s[i + 1]));
                i++;
            }
            else
            {
                cat = CharUnicodeInfo.GetUnicodeCategory(c);
            }
            if (Array.IndexOf(categories, cat) < 0) return false;
        }
        return true;
    }

    /// <summary>Checks if all characters in <paramref name="s"/> are identifier characters (Python's ID_CONTINUE).</summary>
    public static bool IsIdContinue(string s) => TestUnicodeCategory(s, IdContinue);

    /// <summary>Checks if all characters in <paramref name="s"/> are identifier-start characters (Python's ID_START).</summary>
    public static bool IsIdStart(string s) => TestUnicodeCategory(s, IdStart);

    public static bool IsIdContinue(char c) => IsIdContinue(c.ToString());

    public static string Sha256Digest(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Enumerates all possible concatenations of the given alternatives (Python's itertools.product).</summary>
    public static List<List<T>> CombineAlternatives<T>(IReadOnlyList<IReadOnlyList<T>> lists)
    {
        var result = new List<List<T>> { new() };
        foreach (var options in lists)
        {
            var next = new List<List<T>>();
            foreach (var prefix in result)
            {
                foreach (var option in options)
                {
                    var l = new List<T>(prefix) { option };
                    next.Add(l);
                }
            }
            result = next;
        }
        return result;
    }

    /// <summary>Python's <c>str.expandtabs()</c> with the default tab size of 8.</summary>
    public static string ExpandTabs(string s, int tabSize = 8)
    {
        if (s.IndexOf('\t') < 0) return s;
        var sb = new StringBuilder();
        var column = 0;
        foreach (var c in s)
        {
            if (c == '\t')
            {
                var spaces = tabSize - (column % tabSize);
                sb.Append(' ', spaces);
                column += spaces;
            }
            else
            {
                sb.Append(c);
                column = c is '\n' or '\r' ? 0 : column + 1;
            }
        }
        return sb.ToString();
    }

    /// <summary>Python style repr of a string (single quoted, with escapes).</summary>
    public static string Repr(string? s)
    {
        if (s is null) return "None";
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder();
        sb.Append(quote);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c == quote) sb.Append('\\').Append(c);
                    else if (c < ' ' || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("x2"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append(quote);
        return sb.ToString();
    }

    /// <summary>Python style repr for an arbitrary value, used for debugging output.</summary>
    public static string Repr(object? o) => o switch
    {
        null => "None",
        string s => Repr(s),
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        System.Collections.IEnumerable e when o is not Tree => "[" + string.Join(", ", e.Cast<object?>().Select(Repr)) + "]",
        _ => o.ToString() ?? "",
    };
}
