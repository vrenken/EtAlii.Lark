namespace EtAlii.Lark;

using System.Text.RegularExpressions;
using EtAlii.Lark.Parsers;

/// <summary>
/// Matches the elements of a tree node, based on an ontology provided by a Lark grammar.
/// Supports templates and inlined rules (<c>rule{a, b,..}</c> and <c>_rule</c>).
/// </summary>
public class TreeMatcher
{
    private readonly Dictionary<string, List<Rule>> _rulesForRoot = new();
    private readonly List<Rule> _rules;
    private readonly Dictionary<Rule, IReadOnlyList<Symbol>> _origExpansions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, EarleyParser> _parserCache = new();
    private readonly Lark _parser;

    public TreeMatcher(Lark parser)
    {
        if (parser.Options.MaybePlaceholders)
        {
            throw new ConfigurationException("The tree matcher requires a parser created with MaybePlaceholders = false.");
        }

        IReadOnlyList<Rule> rules;
        if (parser.Options.PostLex is { AlwaysAccept.Count: > 0 })
        {
            // If postlexer's always_accept is used, we need to recompile the grammar with empty terminals-to-keep
            if (parser.Grammar is null)
            {
                throw new ConfigurationException(parser.Options.Cache
                    ? "Source grammar not available from cached parser, use cache_grammar=True"
                    : "Source grammar not available!");
            }
            var (terminals, compiledRules, _) = parser.Grammar.Compile(parser.Options.Start, Array.Empty<string>());
            Tokens = terminals;
            rules = compiledRules;
        }
        else
        {
            Tokens = parser.Terminals.ToList();
            rules = parser.Rules;
        }

        _rules = BuildReconsRules(rules).ToList();
        _rules.Reverse();

        // Choose the best rule from each group of {rule => [rule.alias]}, since we only really need one derivation.
        _rules = BestRulesFromGroup(_rules);

        _parser = parser;
    }

    /// <summary>The terminals of the grammar.</summary>
    protected IReadOnlyList<TerminalDef> Tokens { get; }

    internal static bool IsDiscardedTerminal(Symbol t) => t is Terminal { FilterOut: true };

    private static List<Rule> BestRulesFromGroup(IEnumerable<Rule> rules)
    {
        // Keeps the first of each group of equal rules (they have the same expansion length), then sorts by length.
        var best = new Dictionary<Rule, Rule>();
        var order = new List<Rule>();
        foreach (var rule in rules)
        {
            if (best.TryAdd(rule, rule)) order.Add(rule);
        }
        return order.OrderBy(r => r.Expansion.Count).ToList();
    }

    private static bool Match(Symbol term, object token) => token switch
    {
        Tree tree => tree.Data == ParseRuleName(term.Name).Name,
        Token tok => term == new Terminal(tok.Type),
        _ => throw new InvalidOperationException($"Unexpected token: {term}, {token}"),
    };

    private Rule MakeReconsRule(NonTerminal origin, IReadOnlyList<Symbol> expansion, IReadOnlyList<Symbol> oldExpansion)
    {
        var rule = new Rule(origin, expansion);
        _origExpansions[rule] = oldExpansion;
        return rule;
    }

    private Rule MakeReconsRuleToTerm(NonTerminal origin, Symbol term) =>
        MakeReconsRule(origin, new Symbol[] { new Terminal(term.Name) }, new[] { term });

    /// <summary>Parses rule names that may contain a template syntax (like <c>rule{a, b, ...}</c>).</summary>
    internal static (string Name, string[]? Args) ParseRuleName(string s)
    {
        var m = Regex.Match(s, @"^(\w+)(?:{(.+)})?");
        var args = m.Groups[2].Success ? m.Groups[2].Value.Split(',').Select(a => a.Trim()).ToArray() : null;
        return (m.Groups[1].Value, args);
    }

    /// <summary>Converts tree-parsing/construction rules to tree-matching rules.</summary>
    private IEnumerable<Rule> BuildReconsRules(IReadOnlyList<Rule> rules)
    {
        var expand1s = rules.Where(r => r.Options.Expand1).Select(r => r.Origin).ToHashSet();

        var aliases = new Dictionary<NonTerminal, List<string>>();
        var aliasOrder = new List<NonTerminal>();
        foreach (var r in rules)
        {
            if (r.Alias is null) continue;
            if (!aliases.TryGetValue(r.Origin, out var list))
            {
                aliases[r.Origin] = list = new List<string>();
                aliasOrder.Add(r.Origin);
            }
            list.Add(r.Alias);
        }

        var ruleNames = rules.Select(r => r.Origin).ToHashSet();
        var nonterminals = ruleNames.Where(sym => sym.Name.StartsWith('_') || expand1s.Contains(sym) || aliases.ContainsKey(sym)).ToHashSet<Symbol>();

        var seen = new HashSet<string>();
        foreach (var r in rules)
        {
            var reconsExp = r.Expansion
                .Where(sym => !IsDiscardedTerminal(sym))
                .Select(sym => nonterminals.Contains(sym) ? sym : new Terminal(sym.Name))
                .ToList();

            // Skip self-recursive constructs
            if (reconsExp.Count == 1 && reconsExp[0] == r.Origin && r.Alias is null) continue;

            var sym = r.Alias is not null ? new NonTerminal(r.Alias) : r.Origin;
            var rule = MakeReconsRule(sym, reconsExp, r.Expansion);

            if (expand1s.Contains(sym) && reconsExp.Count != 1)
            {
                RulesForRoot(sym.Name).Add(rule);

                if (seen.Add(sym.Name))
                {
                    yield return MakeReconsRuleToTerm(sym, sym);
                }
            }
            else
            {
                if (sym.Name.StartsWith('_') || expand1s.Contains(sym))
                {
                    yield return rule;
                }
                else
                {
                    RulesForRoot(sym.Name).Add(rule);
                }
            }
        }

        foreach (var origin in aliasOrder)
        {
            foreach (var alias in aliases[origin])
            {
                yield return MakeReconsRuleToTerm(origin, new NonTerminal(alias));
            }
            yield return MakeReconsRuleToTerm(origin, origin);
        }
    }

    private List<Rule> RulesForRoot(string name)
    {
        if (!_rulesForRoot.TryGetValue(name, out var list)) _rulesForRoot[name] = list = new List<Rule>();
        return list;
    }

    /// <summary>Matches the elements of <paramref name="tree"/> to the symbols of rule <paramref name="ruleName"/>.</summary>
    /// <param name="tree">The tree node to match.</param>
    /// <param name="ruleName">The expected full rule name (including template args); defaults to the tree's data.</param>
    /// <returns>An unreduced tree that matches <paramref name="ruleName"/>.</returns>
    /// <exception cref="UnexpectedTokenException">No match was found.</exception>
    /// <remarks>It's the caller's responsibility to match the tree recursively.</remarks>
    public Tree MatchTree(Tree tree, string? ruleName = null)
    {
        if (!string.IsNullOrEmpty(ruleName))
        {
            var (name, _) = ParseRuleName(ruleName);
            if (tree.Data != name) throw new ArgumentException($"Expected a tree for rule '{name}', got '{tree.Data}'.", nameof(tree));
        }
        else
        {
            ruleName = tree.Data;
        }

        if (!_parserCache.TryGetValue(ruleName, out var parser))
        {
            var rules = _rules.Concat(BestRulesFromGroup(_rulesForRoot.TryGetValue(ruleName, out var forRoot) ? forRoot : new List<Rule>())).ToList();

            var callbacks = new ParserCallbacks();
            foreach (var rule in rules)
            {
                var origin = rule.Origin.Name;
                var origExpansion = _origExpansions[rule];
                callbacks.Rules[rule] = args =>
                {
                    var t = new Tree(origin, args);
                    t.Meta.MatchTree = true;
                    t.Meta.OrigExpansion = origExpansion;
                    return t;
                };
            }
            var conf = new ParserConf(rules, callbacks, new[] { ruleName });
            parser = new EarleyParser(_parser.LexerConf, conf, Match, resolveAmbiguity: true);
            _parserCache[ruleName] = parser;
        }

        // find a full derivation
        var children = tree.Children;
        var unreducedTree = (Tree)parser.Parse(new Func<object?, IEnumerable<object>>(_ => children.Select(c => c!)), ruleName)!;
        if (unreducedTree.Data != ruleName) throw new InvalidOperationException($"Expected '{ruleName}', got '{unreducedTree.Data}'.");
        return unreducedTree;
    }
}
