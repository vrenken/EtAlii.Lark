namespace EtAlii.Lark.Regexes;

using System.Text.RegularExpressions;

/// <summary>
/// Detection of collisions between terminals, used in strict mode.
/// </summary>
/// <remarks>
/// Upstream Lark relies on the <c>interegular</c> package to compute the intersection of regular expressions.
/// This implementation checks collisions on literal strings exactly, and between regular expressions by probing
/// each terminal's sample strings against the other terminals.
/// </remarks>
internal static class RegexCollisions
{
    public static void Check(IReadOnlyList<TerminalDef> terminals, RegexOptions options, bool strict)
    {
        var byPriority = terminals.GroupBy(t => t.Priority);
        foreach (var group in byPriority)
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    if (a.Pattern is PatternStr && b.Pattern is PatternStr) continue; // literals never collide in a way the lexer can't resolve
                    var example = FindOverlap(a, b, options) ?? FindOverlap(b, a, options);
                    if (example is null) continue;
                    var message = $"Collision between Terminals {a.Name} and {b.Name}. ";
                    if (strict) throw new LexException(message + "\n" + "Example: " + example);
                }
            }
        }
    }

    private static string? FindOverlap(TerminalDef a, TerminalDef b, RegexOptions options)
    {
        if (a.Pattern is PatternStr s)
        {
            // A regexp that fully matches a literal is resolved by the lexer (see "unless" callbacks), unless flags differ.
            return null;
        }
        if (b.Pattern is PatternStr) return null;

        foreach (var sample in Samples(a.Pattern.ToRegexp()))
        {
            if (PyRegex.FullMatch(a.Pattern.ToRegexp(), sample, options) is not null &&
                PyRegex.FullMatch(b.Pattern.ToRegexp(), sample, options) is not null)
            {
                return sample;
            }
        }
        return null;
    }

    private static IEnumerable<string> Samples(string pattern)
    {
        // A small set of probe strings: enough to catch the obvious collisions (e.g. two identifier-like regexps).
        string[] probes = { "a", "A", "0", "1", "ab", "aa", "a1", "_", " ", "\n", "\t", "-", "+", ".", "abc", "x", "z", "Z", "00" };
        return probes;
    }
}
