namespace EtAlii.Lark.Parsers;

/// <summary>A CYK parser (a legacy algorithm, kept for completeness). Converts the grammar to Chomsky normal form.</summary>
public sealed class CykParser
{
    private class CnfRule : IEquatable<CnfRule>
    {
        public CnfRule(NonTerminal lhs, IReadOnlyList<Symbol> rhs, int weight, object? alias)
        {
            Lhs = lhs;
            Rhs = rhs;
            Weight = weight;
            Alias = alias;
        }

        public NonTerminal Lhs { get; }
        public IReadOnlyList<Symbol> Rhs { get; }
        public int Weight { get; }

        /// <summary>The original lark rule, or "Split"/"Term" markers.</summary>
        public object? Alias { get; }

        public virtual bool Equals(CnfRule? other) => other is not null && other.GetType() == GetType() && Lhs.Equals(other.Lhs) && Rhs.SequenceEqual(other.Rhs);

        public override bool Equals(object? obj) => obj is CnfRule r && Equals(r);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(Lhs);
            foreach (var s in Rhs) h.Add(s);
            return h.ToHashCode();
        }

        public override string ToString() => $"{Lhs} -> {string.Join(" ", Rhs)}";
    }

    private sealed class UnitSkipRule : CnfRule
    {
        public UnitSkipRule(NonTerminal lhs, IReadOnlyList<Symbol> rhs, List<CnfRule> skippedRules, int weight, object? alias)
            : base(lhs, rhs, weight, alias)
        {
            SkippedRules = skippedRules;
        }

        public List<CnfRule> SkippedRules { get; }

        public override bool Equals(CnfRule? other) =>
            other is UnitSkipRule u && base.Equals(other) && SkippedRules.SequenceEqual(u.SkippedRules);
    }

    private sealed class RuleNode
    {
        public RuleNode(CnfRule rule, List<object> children, int weight = 0)
        {
            Rule = rule;
            Children = children;
            Weight = weight;
        }

        public CnfRule Rule { get; }
        public List<object> Children { get; }
        public int Weight { get; }
    }

    /// <summary>A terminal leaf in the CYK parse, wrapping the matched token.</summary>
    private sealed record TokenLeaf(Token Token);

    private readonly HashSet<Rule> _origRules;
    private readonly List<CnfRule> _rules;
    private readonly Dictionary<Symbol, List<CnfRule>> _terminalRules = new();
    private readonly Dictionary<(Symbol, Symbol), List<CnfRule>> _nonterminalRules = new();

    public CykParser(IReadOnlyList<Rule> rules)
    {
        _origRules = rules.ToHashSet();
        var cnfRules = rules.Select(ToRule).ToList();
        _rules = ToCnf(cnfRules);
        foreach (var r in _rules)
        {
            if (r.Rhs.Count is not (1 or 2)) throw new ParseException("CYK doesn't support empty rules");
            if (r.Rhs.Count == 1 && r.Rhs[0].IsTerm)
            {
                if (!_terminalRules.TryGetValue(r.Rhs[0], out var l)) _terminalRules[r.Rhs[0]] = l = new List<CnfRule>();
                l.Add(r);
            }
            else if (r.Rhs.Count == 2 && r.Rhs.All(x => !x.IsTerm))
            {
                var key = (r.Rhs[0], r.Rhs[1]);
                if (!_nonterminalRules.TryGetValue(key, out var l)) _nonterminalRules[key] = l = new List<CnfRule>();
                l.Add(r);
            }
            else
            {
                throw new InvalidOperationException($"Invalid CNF rule: {r}");
            }
        }
    }

    private static CnfRule ToRule(Rule larkRule) =>
        new(larkRule.Origin, larkRule.Expansion, larkRule.Options.Priority ?? 0, larkRule);

    /// <summary>Parses a list of tokens. Returns a tree whose nodes carry their original <see cref="Rule"/>.</summary>
    internal CykTree Parse(IReadOnlyList<Token> tokenized, string start)
    {
        var startSym = new NonTerminal(start);
        var (table, trees) = ParseTable(tokenized);
        var last = (0, tokenized.Count - 1);
        if (!table.TryGetValue(last, out var cell) || cell.All(r => !r.Lhs.Equals(startSym)))
        {
            throw new ParseException("Parsing failed.");
        }
        var parse = trees[last][startSym];
        return ToTree((RuleNode)RevertCnf(parse));
    }

    private CykTree ToTree(RuleNode ruleNode)
    {
        var origRule = (Rule)ruleNode.Rule.Alias!;
        var children = new List<object?>();
        foreach (var child in ruleNode.Children)
        {
            if (child is RuleNode rn) children.Add(ToTree(rn));
            else children.Add(((TokenLeaf)child).Token);
        }
        return new CykTree(origRule.Origin.Name, children, origRule);
    }

    private (Dictionary<(int, int), HashSet<CnfRule>>, Dictionary<(int, int), Dictionary<NonTerminal, RuleNode>>) ParseTable(IReadOnlyList<Token> s)
    {
        var table = new Dictionary<(int, int), HashSet<CnfRule>>();
        var trees = new Dictionary<(int, int), Dictionary<NonTerminal, RuleNode>>();

        HashSet<CnfRule> Cell((int, int) k)
        {
            if (!table.TryGetValue(k, out var c)) table[k] = c = new HashSet<CnfRule>();
            return c;
        }

        Dictionary<NonTerminal, RuleNode> TreeCell((int, int) k)
        {
            if (!trees.TryGetValue(k, out var c)) trees[k] = c = new Dictionary<NonTerminal, RuleNode>();
            return c;
        }

        // Populate base case with existing terminal production rules
        for (var i = 0; i < s.Count; i++)
        {
            var w = s[i];
            foreach (var (terminal, rules) in _terminalRules)
            {
                if (terminal.Name != w.Type) continue;
                foreach (var rule in rules)
                {
                    Cell((i, i)).Add(rule);
                    var tc = TreeCell((i, i));
                    if (!tc.TryGetValue(rule.Lhs, out var existing) || rule.Weight < existing.Weight)
                    {
                        tc[rule.Lhs] = new RuleNode(rule, new List<object> { new TokenLeaf(w) }, rule.Weight);
                    }
                }
            }
        }

        // Iterate over lengths of sub-sentences
        for (var l = 2; l <= s.Count; l++)
        {
            // Iterate over sub-sentences with the given length
            for (var i = 0; i < s.Count - l + 1; i++)
            {
                // Choose partition of the sub-sentence in [1, l)
                for (var p = i + 1; p < i + l; p++)
                {
                    var span1 = (i, p - 1);
                    var span2 = (p, i + l - 1);
                    var c1 = Cell(span1).ToList();
                    var c2 = Cell(span2).ToList();
                    foreach (var r1 in c1)
                    {
                        foreach (var r2 in c2)
                        {
                            if (!_nonterminalRules.TryGetValue((r1.Lhs, r2.Lhs), out var rules)) continue;
                            foreach (var rule in rules)
                            {
                                var target = (i, i + l - 1);
                                Cell(target).Add(rule);
                                var r1Tree = trees[span1][r1.Lhs];
                                var r2Tree = trees[span2][r2.Lhs];
                                var total = rule.Weight + r1Tree.Weight + r2Tree.Weight;
                                var tc = TreeCell(target);
                                if (!tc.TryGetValue(rule.Lhs, out var existing) || total < existing.Weight)
                                {
                                    tc[rule.Lhs] = new RuleNode(rule, new List<object> { r1Tree, r2Tree }, total);
                                }
                            }
                        }
                    }
                }
            }
        }
        return (table, trees);
    }

    // --- Conversion to Chomsky normal form ---

    private static List<CnfRule> Term(List<CnfRule> g)
    {
        var allT = g.SelectMany(r => r.Rhs).Where(x => x.IsTerm).Distinct().ToList();
        var tRules = allT.ToDictionary(t => t, t => new CnfRule(new NonTerminal("__T_" + t), new[] { t }, 0, "Term"));
        var newRules = new List<CnfRule>();
        foreach (var rule in g)
        {
            if (rule.Rhs.Count > 1 && rule.Rhs.Any(x => x.IsTerm))
            {
                var newRhs = rule.Rhs.Select(x => x.IsTerm ? tRules[x].Lhs : x).ToList();
                newRules.Add(new CnfRule(rule.Lhs, newRhs, rule.Weight, rule.Alias));
                newRules.AddRange(tRules.Where(kv => rule.Rhs.Contains(kv.Key)).Select(kv => kv.Value));
            }
            else
            {
                newRules.Add(rule);
            }
        }
        return newRules.Distinct().ToList();
    }

    private static IEnumerable<CnfRule> Split(CnfRule rule)
    {
        var ruleStr = rule.Lhs + "__" + string.Join("_", rule.Rhs.Select(x => x.ToString()));
        string RuleName(int i) => "__SP_" + ruleStr + "_" + i;
        yield return new CnfRule(rule.Lhs, new Symbol[] { rule.Rhs[0], new NonTerminal(RuleName(1)) }, rule.Weight, rule.Alias);
        for (var i = 1; i < rule.Rhs.Count - 2; i++)
        {
            yield return new CnfRule(new NonTerminal(RuleName(i)), new Symbol[] { rule.Rhs[i], new NonTerminal(RuleName(i + 1)) }, 0, "Split");
        }
        yield return new CnfRule(new NonTerminal(RuleName(rule.Rhs.Count - 2)), rule.Rhs.Skip(rule.Rhs.Count - 2).ToList(), 0, "Split");
    }

    private static List<CnfRule> Bin(List<CnfRule> g)
    {
        var newRules = new List<CnfRule>();
        foreach (var rule in g)
        {
            if (rule.Rhs.Count > 2) newRules.AddRange(Split(rule));
            else newRules.Add(rule);
        }
        return newRules.Distinct().ToList();
    }

    private static CnfRule BuildUnitSkipRule(CnfRule unitRule, CnfRule targetRule)
    {
        var skippedRules = new List<CnfRule>();
        if (unitRule is UnitSkipRule u) skippedRules.AddRange(u.SkippedRules);
        skippedRules.Add(targetRule);
        if (targetRule is UnitSkipRule t) skippedRules.AddRange(t.SkippedRules);
        return new UnitSkipRule(unitRule.Lhs, targetRule.Rhs, skippedRules, unitRule.Weight + targetRule.Weight, unitRule.Alias);
    }

    private static List<CnfRule> Unit(List<CnfRule> g)
    {
        while (true)
        {
            var nt = g.FirstOrDefault(r => r.Rhs.Count == 1 && !r.Rhs[0].IsTerm);
            if (nt is null) return g;
            var newRules = g.Where(x => !x.Equals(nt)).ToList();
            var refs = g.Where(x => x.Lhs.Equals(nt.Rhs[0])).ToList();
            newRules.AddRange(refs.Select(r => BuildUnitSkipRule(nt, r)));
            g = newRules.Distinct().ToList();
        }
    }

    private static List<CnfRule> ToCnf(List<CnfRule> g) => Unit(Bin(Term(g.Distinct().ToList())));

    private static RuleNode UnrollUnitSkipRule(NonTerminal lhs, IReadOnlyList<Symbol> origRhs, List<CnfRule> skippedRules, List<object> children, int weight, object? alias)
    {
        if (skippedRules.Count == 0)
        {
            return new RuleNode(new CnfRule(lhs, origRhs, weight, alias), children, weight);
        }
        weight -= skippedRules[0].Weight;
        return new RuleNode(
            new CnfRule(lhs, new Symbol[] { skippedRules[0].Lhs }, weight, alias),
            new List<object>
            {
                UnrollUnitSkipRule(skippedRules[0].Lhs, origRhs, skippedRules.Skip(1).ToList(), children, skippedRules[0].Weight, skippedRules[0].Alias),
            },
            weight);
    }

    /// <summary>Reverts a parse tree (RuleNode) to its original non-CNF form.</summary>
    private static object RevertCnf(object node)
    {
        if (node is TokenLeaf) return node;
        var rn = (RuleNode)node;
        // Reverts TERM rule.
        if (rn.Rule.Lhs.Name.StartsWith("__T_", StringComparison.Ordinal))
        {
            return rn.Children[0];
        }

        var children = new List<object>();
        foreach (var child in rn.Children.Select(RevertCnf))
        {
            // Reverts BIN rule.
            if (child is RuleNode c && c.Rule.Lhs.Name.StartsWith("__SP_", StringComparison.Ordinal)) children.AddRange(c.Children);
            else children.Add(child);
        }
        // Reverts UNIT rule.
        if (rn.Rule is UnitSkipRule u)
        {
            return UnrollUnitSkipRule(u.Lhs, u.Rhs, u.SkippedRules, children, u.Weight, u.Alias);
        }
        return new RuleNode(rn.Rule, children);
    }
}

/// <summary>A tree produced by the CYK parser, which remembers the rule it was created from.</summary>
internal sealed class CykTree : Tree
{
    public CykTree(string data, List<object?> children, Rule rule) : base(data, children)
    {
        Rule = rule;
    }

    public Rule Rule { get; }
}
