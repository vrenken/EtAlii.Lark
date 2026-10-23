namespace EtAlii.Lark.Parsers;

using System.Text.RegularExpressions;
using EtAlii.Lark.Regexes;
using EtAlii.Lark.Utilities;

/// <summary>An Earley item, the atom of the algorithm.</summary>
internal sealed class EarleyItem : IEquatable<EarleyItem>
{
    private readonly int _hash;

    public EarleyItem(Rule rule, int ptr, int start)
    {
        IsComplete = rule.Expansion.Count == ptr;
        Rule = rule;
        Ptr = ptr;
        Start = start;
        if (IsComplete)
        {
            S = rule.Origin;
            Expect = null;
        }
        else
        {
            S = new Lr0Item(rule, ptr);
            Expect = rule.Expansion[ptr];
        }
        Previous = ptr > 0 && rule.Expansion.Count > 0 ? rule.Expansion[ptr - 1] : null;
        _hash = HashCode.Combine(S, Start, Rule);
    }

    public object S { get; }

    public Rule Rule { get; }

    public int Ptr { get; }

    public int Start { get; }

    public bool IsComplete { get; }

    public Symbol? Expect { get; }

    public Symbol? Previous { get; }

    public SymbolNode? Node { get; set; }

    public EarleyItem Advance() => new(Rule, Ptr + 1, Start);

    public bool Equals(EarleyItem? other) =>
        other is not null && (ReferenceEquals(this, other) || (_hash == other._hash && S.Equals(other.S) && Start == other.Start && Rule.Equals(other.Rule)));

    public override bool Equals(object? obj) => obj is EarleyItem i && Equals(i);

    public override int GetHashCode() => _hash;

    public override string ToString()
    {
        var before = Rule.Expansion.Take(Ptr).Select(x => x.Name);
        var after = Rule.Expansion.Skip(Ptr).Select(x => x.Name);
        return $"{Rule.Origin.Name} ::= {string.Join(" ", before)}* {string.Join(" ", after)} ({Start})";
    }
}

/// <summary>
/// An Earley parser, based on Elizabeth Scott's implementation. Outputs an SPPF, which is then converted to a tree.
/// </summary>
public class EarleyParser
{
    private readonly Dictionary<NonTerminal, List<Rule>> _predictions = new();
    private readonly bool _hasPriorities;

    internal EarleyParser(LexerConf lexerConf, ParserConf parserConf, Func<Symbol, object, bool> termMatcher,
        bool resolveAmbiguity = true, bool debug = false, Func<string, List<object?>, Tree>? treeFactory = null, bool buildTree = true)
    {
        var analysis = new GrammarAnalyzer(parserConf);
        LexerConf = lexerConf;
        ParserConf = parserConf;
        ResolveAmbiguity = resolveAmbiguity;
        Debug = debug;
        TreeFactory = treeFactory ?? ((data, children) => new Tree(data, children));
        BuildTree = buildTree;

        Nullable = analysis.Nullable;
        Callbacks = parserConf.Callbacks;

        Terminals = parserConf.Rules.SelectMany(r => r.Expansion).Where(s => s.IsTerm).ToHashSet();
        NonTerminals = parserConf.Rules.SelectMany(r => r.Expansion).Where(s => !s.IsTerm).ToHashSet();

        foreach (var rule in parserConf.Rules)
        {
            if (!_predictions.ContainsKey(rule.Origin))
            {
                _predictions[rule.Origin] = analysis.ExpandRule(rule.Origin).Select(x => x.Rule).ToList();
            }

            if (!_hasPriorities && rule.Options.Priority is not null) _hasPriorities = true;
        }

        // Check terminals for priorities. Ignore terminal priorities if the basic lexer is used
        if (lexerConf.LexerKind != LexerKind.Basic && !_hasPriorities)
        {
            if (lexerConf.Terminals.Any(t => t.Priority != 0)) _hasPriorities = true;
        }

        TermMatcher = termMatcher;
    }

    internal LexerConf LexerConf { get; }

    internal ParserConf ParserConf { get; }

    internal bool ResolveAmbiguity { get; }

    internal bool Debug { get; }

    internal Func<string, List<object?>, Tree> TreeFactory { get; }

    /// <summary>When false, the parser returns the root of the SPPF (ambiguity='forest').</summary>
    internal bool BuildTree { get; }

    internal HashSet<Symbol> Nullable { get; }

    internal ParserCallbacks Callbacks { get; }

    internal HashSet<Symbol> Terminals { get; }

    internal HashSet<Symbol> NonTerminals { get; }

    internal Func<Symbol, object, bool> TermMatcher { get; }

    private static SymbolNode GetNode(Dictionary<(object, int, int), SymbolNode> nodeCache, object s, int start, int end)
    {
        var label = (s, start, end);
        if (!nodeCache.TryGetValue(label, out var node))
        {
            node = new SymbolNode(s, start, end);
            nodeCache[label] = node;
        }
        return node;
    }

    /// <summary>The core Earley Predictor and Completer.</summary>
    internal void PredictAndComplete(int i, OrderedSet<EarleyItem> toScan, List<OrderedSet<EarleyItem>> columns, Dictionary<(object, int, int), SymbolNode> nodeCache)
    {
        // Held Completions (H in E.Scotts paper).
        var heldCompletions = new Dictionary<Symbol, SymbolNode>();

        var column = columns[i];
        // R (items) = Ei (column.items)
        var items = new List<EarleyItem>(column);
        while (items.Count > 0)
        {
            var item = items[^1];
            items.RemoveAt(items.Count - 1);

            // The Earley completer
            if (item.IsComplete)
            {
                if (item.Node is null)
                {
                    item.Node = GetNode(nodeCache, item.S, item.Start, i);
                    item.Node.AddFamily(item.S, item.Rule, item.Start, null, null);
                }

                // Empty has 0 length. If we complete an empty symbol in a particular parse step, we need to be able
                // to use that same empty symbol to complete any predictions that result, that themselves require
                // empty. Avoids infinite recursion on empty symbols.
                var isEmptyItem = item.Start == i;
                if (isEmptyItem)
                {
                    heldCompletions[item.Rule.Origin] = item.Node;
                }

                var originators = columns[item.Start].Where(o => o.Expect is not null && o.Expect.Equals(item.S)).ToList();
                foreach (var originator in originators)
                {
                    var newItem = originator.Advance();
                    newItem.Node = GetNode(nodeCache, newItem.S, originator.Start, i);
                    newItem.Node.AddFamily(newItem.S, newItem.Rule, i, originator.Node, item.Node);
                    if (newItem.Expect is not null && Terminals.Contains(newItem.Expect))
                    {
                        // Add (B :: aC.B, h, y) to Q
                        toScan.Add(newItem);
                    }
                    else if (!column.Contains(newItem))
                    {
                        // Add (B :: aC.B, h, y) to Ei and R
                        column.Add(newItem);
                        items.Add(newItem);
                    }
                }
            }
            // The Earley predictor
            else if (item.Expect is not null && NonTerminals.Contains(item.Expect))
            {
                var newItems = new List<EarleyItem>();
                foreach (var rule in _predictions[(NonTerminal)item.Expect])
                {
                    newItems.Add(new EarleyItem(rule, 0, i));
                }

                // Process any held completions (H).
                if (heldCompletions.TryGetValue(item.Expect, out var held))
                {
                    var newItem = item.Advance();
                    newItem.Node = GetNode(nodeCache, newItem.S, item.Start, i);
                    newItem.Node.AddFamily(newItem.S, newItem.Rule, newItem.Start, item.Node, held);
                    newItems.Add(newItem);
                }

                foreach (var newItem in newItems)
                {
                    if (newItem.Expect is not null && Terminals.Contains(newItem.Expect))
                    {
                        toScan.Add(newItem);
                    }
                    else if (!column.Contains(newItem))
                    {
                        column.Add(newItem);
                        items.Add(newItem);
                    }
                }
            }
        }
    }

    /// <summary>Runs the main Earley loop over a stream of tokens (basic lexer).</summary>
    internal virtual OrderedSet<EarleyItem> ParseInternal(object input, List<OrderedSet<EarleyItem>> columns, OrderedSet<EarleyItem> toScan, NonTerminal startSymbol)
    {
        var lexer = (Func<object?, IEnumerable<object>>)input;
        var terminals = LexerConf.TerminalsByName;
        var i = 0;
        var nodeCache = new Dictionary<(object, int, int), SymbolNode>();
        var expects = toScan.Select(x => x.Expect!).ToHashSet();

        foreach (var token in lexer(expects))
        {
            PredictAndComplete(i, toScan, columns, nodeCache);

            // The core Earley Scanner.
            var nextToScan = new OrderedSet<EarleyItem>();
            var nextSet = new OrderedSet<EarleyItem>();
            columns.Add(nextSet);
            nodeCache = new Dictionary<(object, int, int), SymbolNode>();

            foreach (var item in toScan.ToList())
            {
                if (TermMatcher(item.Expect!, token))
                {
                    var newItem = item.Advance();
                    // 'terminals' may not contain token.type when using %declare
                    // Additionally, token is not always a Token. For example, it can be a Tree when using TreeMatcher
                    var term = token is Token tok && terminals.TryGetValue(tok.Type, out var td) ? td : null;
                    // Set the priority of the token node to 0 so that the terminal priorities do not affect the Tree
                    // chosen by ForestSumVisitor after the basic lexer has already "used up" the terminal priorities
                    var tokenNode = new TokenNode(token, term, priority: 0);
                    newItem.Node = GetNode(nodeCache, newItem.S, newItem.Start, i + 1);
                    newItem.Node.AddFamily(newItem.S, item.Rule, newItem.Start, item.Node, tokenNode);

                    if (newItem.Expect is not null && Terminals.Contains(newItem.Expect))
                    {
                        nextToScan.Add(newItem);
                    }
                    else
                    {
                        nextSet.Add(newItem);
                    }
                }
            }

            if (nextSet.Count == 0 && nextToScan.Count == 0)
            {
                var expect = toScan.Select(x => x.Expect!.Name).ToHashSet();
                var state = new FrozenSet<object>(toScan.Select(x => x.S));
                if (token is Token t)
                {
                    throw new UnexpectedTokenException(t, expect, considered_rules(toScan), state: state);
                }
                throw new UnexpectedTokenException(new Token("<TREE>", token.ToString() ?? ""), expect, considered_rules(toScan), state: state);
            }

            toScan = nextToScan;
            i++;

            expects.Clear();
            expects.UnionWith(toScan.Select(x => x.Expect!));
        }

        PredictAndComplete(i, toScan, columns, nodeCache);

        // Column is now the final column in the parse.
        return toScan;

        static object considered_rules(OrderedSet<EarleyItem> items) => items.ToHashSet();
    }

    /// <summary>Parses the input. For the basic lexer, <paramref name="input"/> is a lexing function; for the dynamic lexer, the text.</summary>
    public object? Parse(object input, string start)
    {
        if (string.IsNullOrEmpty(start)) throw new ArgumentException("start");
        var startSymbol = new NonTerminal(start);

        var columns = new List<OrderedSet<EarleyItem>> { new() };
        var toScan = new OrderedSet<EarleyItem>(); // The scan buffer. 'Q' in E.Scott's paper.

        // Predict for the start_symbol.
        foreach (var rule in _predictions[startSymbol])
        {
            var item = new EarleyItem(rule, 0, 0);
            if (item.Expect is not null && Terminals.Contains(item.Expect)) toScan.Add(item);
            else columns[0].Add(item);
        }

        toScan = ParseInternal(input, columns, toScan, startSymbol);

        // If the parse was successful, the start symbol should have been completed in the last step of the Earley
        // cycle, and will be in this column. Find the item for the start_symbol, which is the root of the SPPF tree.
        var solutions = Helpers.DedupList(columns[^1]
            .Where(n => n.IsComplete && n.Node is not null && n.S.Equals(startSymbol) && n.Start == 0)
            .Select(n => n.Node!)
            .Cast<object>()).Cast<SymbolNode>().ToList();

        if (solutions.Count == 0)
        {
            var expectedTerminals = toScan.Select(t => t.Expect!.Name).ToList();
            throw new UnexpectedEofException(expectedTerminals, state: new FrozenSet<object>(toScan.Select(x => x.S)));
        }
        if (solutions.Count > 1)
        {
            throw new InvalidOperationException("Earley should not generate multiple start symbol items! Please report this bug.");
        }
        var solution = solutions[0];

        if (BuildTree)
        {
            // Perform our SPPF -> AST conversion. Disable the ForestToParseTree cache when ambiguity='resolve'
            // to prevent a tree construction bug. See upstream issue #1283.
            var useCache = !ResolveAmbiguity;
            var transformer = new ForestToParseTree(TreeFactory, Callbacks.Rules, _hasPriorities ? new ForestSumVisitor() : null, ResolveAmbiguity, useCache);
            return transformer.Transform(solution);
        }

        // return the root of the SPPF
        return solution;
    }
}

/// <summary>An Earley parser with a dynamic (scanner-less) lexer, matching terminals with regular expressions on the text.</summary>
public sealed class XEarleyParser : EarleyParser
{
    private readonly List<Terminal> _ignore;
    private readonly bool _completeLex;
    private readonly Dictionary<string, Regex> _regexps = new();

    internal XEarleyParser(LexerConf lexerConf, ParserConf parserConf, bool resolveAmbiguity = true, bool completeLex = false, bool debug = false,
        Func<string, List<object?>, Tree>? treeFactory = null, bool buildTree = true)
        : base(lexerConf, parserConf, (_, _) => false, resolveAmbiguity, debug, treeFactory, buildTree)
    {
        if (lexerConf.Callbacks.Count > 0)
        {
            throw new GrammarException("Earley's dynamic lexer doesn't support lexer_callbacks.");
        }

        foreach (var t in lexerConf.Terminals)
        {
            var regexp = t.Pattern.ToRegexp();
            int width;
            try
            {
                width = PyRegex.GetWidth(regexp).Min;
            }
            catch (ArgumentException)
            {
                throw new GrammarException($"Bad regexp in token {t.Name}: {regexp}");
            }
            if (width == 0)
            {
                throw new GrammarException($"Dynamic Earley doesn't allow zero-width regexps ({t})");
            }
            _regexps[t.Name] = PyRegex.CompileAnchored(regexp, lexerConf.RegexOptions);
        }

        _ignore = lexerConf.Ignore.Select(t => new Terminal(t)).ToList();
        _completeLex = completeLex;
    }

    private Match? MatchTerm(Symbol term, string text, int index)
    {
        var m = _regexps[term.Name].Match(text, index);
        return m.Success ? m : null;
    }

    internal override OrderedSet<EarleyItem> ParseInternal(object input, List<OrderedSet<EarleyItem>> columns, OrderedSet<EarleyItem> toScan, NonTerminal startSymbol)
    {
        var stream = (string)input;
        var delayedMatches = new Dictionary<int, List<(EarleyItem Item, int Start, Token? Token)>>();
        var terminals = LexerConf.TerminalsByName;

        List<(EarleyItem, int, Token?)> Delayed(int index)
        {
            if (!delayedMatches.TryGetValue(index, out var l))
            {
                l = new List<(EarleyItem, int, Token?)>();
                delayedMatches[index] = l;
            }
            return l;
        }

        var textLine = 1;
        var textColumn = 1;

        OrderedSet<EarleyItem> Scan(int i, OrderedSet<EarleyItem> scan, out Dictionary<(object, int, int), SymbolNode> nodeCache)
        {
            nodeCache = new Dictionary<(object, int, int), SymbolNode>();

            // 1) Loop the expectations and ask the lexer to match.
            // Since regexp is forward looking on the input stream, and we only want to process tokens when we hit the
            // point in the stream at which they complete, we push all tokens into a buffer (delayed_matches), to be
            // held possibly for a later parse step when we reach the point in the input stream at which they complete.
            foreach (var item in scan.ToList())
            {
                var m = MatchTerm(item.Expect!, stream, i);
                if (m is not null)
                {
                    var t = new Token(item.Expect!.Name, m.Value, i, textLine, textColumn);
                    Delayed(m.Index + m.Length).Add((item, i, t));

                    if (_completeLex)
                    {
                        var s = m.Value;
                        for (var j = 1; j < s.Length; j++)
                        {
                            var m2 = MatchTerm(item.Expect!, s[..^j], 0);
                            if (m2 is not null)
                            {
                                var t2 = new Token(item.Expect!.Name, m2.Value, i, textLine, textColumn);
                                Delayed(i + m2.Index + m2.Length).Add((item, i, t2));
                            }
                        }
                    }
                }
            }

            // 3) Process any ignores. This is typically used for e.g. whitespace.
            // We carry over any unmatched items from the to_scan buffer to be matched again after the ignore. This
            // should allow us to use ignored symbols in non-terminals to implement e.g. mandatory spacing.
            foreach (var x in _ignore)
            {
                var m = MatchTerm(x, stream, i);
                if (m is not null)
                {
                    var end = m.Index + m.Length;
                    // Carry over any items still in the scan buffer, to past the end of the ignored items.
                    Delayed(end).AddRange(scan.Select(item => (item, i, (Token?)null)));

                    // If we're ignoring up to the end of the file, carry over the start symbol if it already completed.
                    Delayed(end).AddRange(columns[i].Where(item => item.IsComplete && item.S.Equals(startSymbol)).Select(item => (item, i, (Token?)null)));
                }
            }

            var nextToScan = new OrderedSet<EarleyItem>();
            var nextSet = new OrderedSet<EarleyItem>();
            columns.Add(nextSet);

            // 4) Process Tokens from delayed_matches.
            // This is the core of the Earley scanner. Create an SPPF node for each Token, and create the symbol node
            // in the SPPF tree. Advance the item that completed, and add the resulting new item to either the Earley
            // set (for processing by the completer/predictor) or the to_scan buffer for the next parse step.
            if (delayedMatches.TryGetValue(i + 1, out var matches))
            {
                foreach (var (item, _, token) in matches)
                {
                    EarleyItem newItem;
                    if (token is not null)
                    {
                        token.EndLine = textLine;
                        token.EndColumn = textColumn + 1;
                        token.EndPos = i + 1;

                        newItem = item.Advance();
                        var tokenNode = new TokenNode(token, terminals[token.Type]);
                        newItem.Node = GetNodeStatic(nodeCache, newItem.S, newItem.Start, i + 1);
                        newItem.Node.AddFamily(newItem.S, item.Rule, newItem.Start, item.Node, tokenNode);
                    }
                    else
                    {
                        newItem = item;
                    }

                    if (newItem.Expect is not null && Terminals.Contains(newItem.Expect)) nextToScan.Add(newItem);
                    else nextSet.Add(newItem);
                }
            }

            delayedMatches.Remove(i + 1); // No longer needed, so unburden memory

            if (nextSet.Count == 0 && delayedMatches.Count == 0 && nextToScan.Count == 0)
            {
                var consideredRules = scan.OrderBy(k => k.Rule.Origin.Name, StringComparer.Ordinal).ToList();
                throw new UnexpectedCharactersException(stream, i, textLine, textColumn,
                    scan.Select(item => item.Expect!.Name).ToHashSet(), scan.ToHashSet(),
                    state: new FrozenSet<object>(scan.Select(x => x.S)), consideredRules: consideredRules);
            }

            return nextToScan;
        }

        // The main Earley loop.
        var idx = 0;
        var cache = new Dictionary<(object, int, int), SymbolNode>();
        foreach (var c in stream)
        {
            PredictAndComplete(idx, toScan, columns, cache);

            toScan = Scan(idx, toScan, out cache);

            if (c == '\n')
            {
                textLine++;
                textColumn = 1;
            }
            else
            {
                textColumn++;
            }
            idx++;
        }

        PredictAndComplete(idx, toScan, columns, cache);

        return toScan;
    }

    private static SymbolNode GetNodeStatic(Dictionary<(object, int, int), SymbolNode> nodeCache, object s, int start, int end)
    {
        var label = (s, start, end);
        if (!nodeCache.TryGetValue(label, out var node))
        {
            node = new SymbolNode(s, start, end);
            nodeCache[label] = node;
        }
        return node;
    }
}
