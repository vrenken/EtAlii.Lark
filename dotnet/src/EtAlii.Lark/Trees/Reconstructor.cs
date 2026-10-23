namespace EtAlii.Lark;

using System.Text;
using EtAlii.Lark.Utilities;

/// <summary>
/// Generates source code from a full parse tree, based on its Lark grammar (experimental).
/// </summary>
/// <remarks>
/// The reconstructor cannot generate values from regexps. If you need to produce discarded regexes,
/// such as newlines, use <c>termSubs</c> and provide default values for them.
/// </remarks>
public class Reconstructor : TreeMatcher
{
    private readonly WriteTokensTransformer _writeTokens;

    /// <param name="parser">A Lark instance, created with <c>MaybePlaceholders = false</c>.</param>
    /// <param name="termSubs">A mapping of terminal names to functions that return their output text.</param>
    public Reconstructor(Lark parser, IReadOnlyDictionary<string, Func<Symbol, string>>? termSubs = null) : base(parser)
    {
        _writeTokens = new WriteTokensTransformer(Tokens.ToDictionary(t => t.Name), termSubs ?? new Dictionary<string, Func<Symbol, string>>());
    }

    private IEnumerable<string> ReconstructItems(Tree tree)
    {
        var unreducedTree = MatchTree(tree, tree.Data);

        var res = (List<object?>)_writeTokens.Transform(unreducedTree)!;
        foreach (var item in res)
        {
            if (item is Tree t)
            {
                // TODO use orig_expansion.rulename to support templates
                foreach (var x in ReconstructItems(t)) yield return x;
            }
            else
            {
                yield return item is Token tok ? tok.Value : (string)item!;
            }
        }
    }

    /// <summary>Reconstructs the source text of the tree.</summary>
    /// <param name="tree">The parse tree.</param>
    /// <param name="postproc">An optional post-processor of the stream of text fragments.</param>
    /// <param name="insertSpaces">Insert a space between two fragments that would otherwise merge into one identifier.</param>
    public string Reconstruct(Tree tree, Func<IEnumerable<string>, IEnumerable<string>>? postproc = null, bool insertSpaces = true)
    {
        var x = ReconstructItems(tree);
        if (postproc is not null) x = postproc(x);
        var y = new StringBuilder();
        var prevItem = "";
        foreach (var item in x)
        {
            if (insertSpaces && prevItem.Length > 0 && item.Length > 0 && Helpers.IsIdContinue(LastCodePoint(prevItem)) && Helpers.IsIdContinue(FirstCodePoint(item)))
            {
                y.Append(' ');
            }
            y.Append(item);
            prevItem = item;
        }
        return y.ToString();
    }

    private static string FirstCodePoint(string s) => char.IsHighSurrogate(s[0]) && s.Length > 1 ? s[..2] : s[..1];

    private static string LastCodePoint(string s) => char.IsLowSurrogate(s[^1]) && s.Length > 1 ? s[^2..] : s[^1..];

    /// <summary>Inserts discarded tokens into their correct place, according to the rules of the grammar.</summary>
    private sealed class WriteTokensTransformer : InPlaceTransformer
    {
        private readonly IReadOnlyDictionary<string, TerminalDef> _tokens;
        private readonly IReadOnlyDictionary<string, Func<Symbol, string>> _termSubs;

        public WriteTokensTransformer(IReadOnlyDictionary<string, TerminalDef> tokens, IReadOnlyDictionary<string, Func<Symbol, string>> termSubs)
        {
            _tokens = tokens;
            _termSubs = termSubs;
        }

        protected internal override object? Default(string data, List<object?> children, Meta? meta)
        {
            if (meta is not { MatchTree: true, OrigExpansion: { } origExpansion }) return new Tree(data, children);

            using var iterArgs = children.GetEnumerator();
            var toWrite = new List<object?>();
            foreach (var sym in origExpansion)
            {
                if (IsDiscardedTerminal(sym))
                {
                    if (_termSubs.TryGetValue(sym.Name, out var sub))
                    {
                        toWrite.Add(sub(sym));
                    }
                    else
                    {
                        var t = _tokens[sym.Name];
                        if (t.Pattern is not PatternStr)
                        {
                            throw new NotSupportedException($"Reconstructing regexps not supported yet: {t}");
                        }
                        toWrite.Add(t.Pattern.Value);
                    }
                }
                else
                {
                    if (!iterArgs.MoveNext()) throw new InvalidOperationException($"Missing child for {sym}");
                    var x = iterArgs.Current;
                    if (x is List<object?> list)
                    {
                        toWrite.AddRange(list);
                    }
                    else
                    {
                        var ok = x is Token tok ? new Terminal(tok.Type) == sym : x is Tree tree && new NonTerminal(tree.Data) == sym;
                        if (!ok) throw new InvalidOperationException($"Unexpected child {x} for {sym}");
                        toWrite.Add(x);
                    }
                }
            }

            if (iterArgs.MoveNext()) throw new InvalidOperationException("Unexpected extra children");
            return toWrite;
        }
    }
}
