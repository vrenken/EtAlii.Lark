namespace EtAlii.Lark.Parsers;

using EtAlii.Lark.Utilities;

/// <summary>A rule with a position (dot) in its expansion: an LR(0) item.</summary>
internal readonly record struct RulePtr(Rule Rule, int Index)
{
    public Symbol Next => Rule.Expansion[Index];

    public bool IsSatisfied => Index == Rule.Expansion.Count;

    public RulePtr Advance(Symbol sym)
    {
        return new RulePtr(Rule, Index + 1);
    }

    public override string ToString()
    {
        var before = Rule.Expansion.Take(Index).Select(x => x.Name);
        var after = Rule.Expansion.Skip(Index).Select(x => x.Name);
        return $"<{Rule.Origin.Name} : {string.Join(" ", before)} * {string.Join(" ", after)}>";
    }
}

/// <summary>An immutable set with value equality, usable as a dictionary key.</summary>
internal sealed class FrozenSet<T> : IEquatable<FrozenSet<T>>, IReadOnlyCollection<T> where T : notnull
{
    private readonly HashSet<T> _set;
    private readonly int _hash;

    public FrozenSet(IEnumerable<T> items)
    {
        _set = new HashSet<T>(items);
        var h = 0;
        foreach (var i in _set) h ^= i.GetHashCode() * 397 + 17;
        _hash = h ^ _set.Count;
    }

    public int Count => _set.Count;

    public bool Contains(T item) => _set.Contains(item);

    public bool Equals(FrozenSet<T>? other) => other is not null && _hash == other._hash && _set.SetEquals(other._set);

    public override bool Equals(object? obj) => obj is FrozenSet<T> f && Equals(f);

    public override int GetHashCode() => _hash;

    public IEnumerator<T> GetEnumerator() => _set.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>An LR(0) item set (a state of the LR(0) automaton).</summary>
internal sealed class LR0ItemSet
{
    public LR0ItemSet(IEnumerable<RulePtr> kernel, IEnumerable<RulePtr> closure)
    {
        Kernel = new FrozenSet<RulePtr>(kernel);
        Closure = new OrderedSet<RulePtr>(closure);
    }

    public FrozenSet<RulePtr> Kernel { get; }

    public OrderedSet<RulePtr> Closure { get; }

    public Dictionary<Symbol, LR0ItemSet> Transitions { get; } = new();

    public Dictionary<Symbol, OrderedSet<Rule>> Lookaheads { get; } = new();

    public override string ToString() => "{" + string.Join(", ", Kernel) + " | " + string.Join(", ", Closure) + "}";
}

internal class GrammarAnalyzer
{
    public GrammarAnalyzer(ParserConf parserConf, bool debug = false, bool strict = false)
    {
        Debug = debug;
        Strict = strict;

        var rootRules = parserConf.Start.ToDictionary(
            start => start,
            start => new Rule(new NonTerminal("$root_" + start), new Symbol[] { new NonTerminal(start), new Terminal("$END") }));

        var rules = parserConf.Rules.Concat(rootRules.Values).ToList();
        RulesByOrigin = Helpers.Classify(rules, r => r.Origin);

        if (rules.Count != rules.Distinct().Count())
        {
            var duplicates = rules.GroupBy(r => r).Where(g => g.Count() > 1).Select(g => g.Key);
            throw new GrammarException("Rules defined twice: " + string.Join(", ", duplicates));
        }

        foreach (var r in rules)
        {
            foreach (var sym in r.Expansion)
            {
                if (!(sym.IsTerm || RulesByOrigin.ContainsKey((NonTerminal)sym)))
                {
                    throw new GrammarException($"Using an undefined rule: {sym}");
                }
            }
        }

        StartStates = rootRules.ToDictionary(kv => kv.Key, kv => ExpandRule(kv.Value.Origin));

        EndStates = rootRules.ToDictionary(kv => kv.Key, kv => new RulePtr(kv.Value, kv.Value.Expansion.Count));

        var lr0RootRules = parserConf.Start.ToDictionary(
            start => start,
            start => new Rule(new NonTerminal("$root_" + start), new Symbol[] { new NonTerminal(start) }));

        var lr0Rules = parserConf.Rules.Concat(lr0RootRules.Values).ToList();
        Lr0RulesByOrigin = Helpers.Classify(lr0Rules, r => r.Origin);

        Lr0StartStates = lr0RootRules.ToDictionary(
            kv => kv.Key,
            kv => new LR0ItemSet(new[] { new RulePtr(kv.Value, 0) }, ExpandRule(kv.Value.Origin, Lr0RulesByOrigin)));

        (First, Follow, Nullable) = CalculateSets(rules);
    }

    public bool Debug { get; }

    public bool Strict { get; }

    public Dictionary<NonTerminal, List<Rule>> RulesByOrigin { get; }

    public Dictionary<NonTerminal, List<Rule>> Lr0RulesByOrigin { get; }

    public Dictionary<string, OrderedSet<RulePtr>> StartStates { get; }

    public Dictionary<string, RulePtr> EndStates { get; }

    public Dictionary<string, LR0ItemSet> Lr0StartStates { get; }

    public Dictionary<Symbol, HashSet<Symbol>> First { get; }

    public Dictionary<Symbol, HashSet<Symbol>> Follow { get; }

    public HashSet<Symbol> Nullable { get; }

    /// <summary>Returns all init_ptrs accessible by rule (recursive).</summary>
    public OrderedSet<RulePtr> ExpandRule(NonTerminal sourceRule, Dictionary<NonTerminal, List<Rule>>? rulesByOrigin = null)
    {
        rulesByOrigin ??= RulesByOrigin;
        var initPtrs = new OrderedSet<RulePtr>();

        IEnumerable<Symbol> Expand(Symbol rule)
        {
            foreach (var r in rulesByOrigin[(NonTerminal)rule])
            {
                var initPtr = new RulePtr(r, 0);
                initPtrs.Add(initPtr);

                if (r.Expansion.Count > 0)
                {
                    var newR = initPtr.Next;
                    if (!newR.IsTerm) yield return newR;
                }
            }
        }

        foreach (var _ in Helpers.Bfs(new Symbol[] { sourceRule }, Expand)) { }

        return initPtrs;
    }

    private static bool UpdateSet(HashSet<Symbol> set1, HashSet<Symbol> set2)
    {
        if (set2.Count == 0) return false;
        var before = set1.Count;
        set1.UnionWith(set2);
        return set1.Count != before;
    }

    /// <summary>Calculate FIRST, FOLLOW and NULLABLE sets.</summary>
    internal static (Dictionary<Symbol, HashSet<Symbol>>, Dictionary<Symbol, HashSet<Symbol>>, HashSet<Symbol>) CalculateSets(IReadOnlyList<Rule> rules)
    {
        var symbols = new HashSet<Symbol>(rules.SelectMany(r => r.Expansion).Concat(rules.Select(r => (Symbol)r.Origin)));

        var nullable = new HashSet<Symbol>();
        var first = new Dictionary<Symbol, HashSet<Symbol>>();
        var follow = new Dictionary<Symbol, HashSet<Symbol>>();
        foreach (var sym in symbols)
        {
            first[sym] = sym.IsTerm ? new HashSet<Symbol> { sym } : new HashSet<Symbol>();
            follow[sym] = new HashSet<Symbol>();
        }

        // Calculate NULLABLE and FIRST
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var rule in rules)
            {
                if (rule.Expansion.All(nullable.Contains))
                {
                    if (nullable.Add(rule.Origin)) changed = true;
                }

                for (var i = 0; i < rule.Expansion.Count; i++)
                {
                    var sym = rule.Expansion[i];
                    var allNullable = true;
                    for (var k = 0; k < i; k++)
                    {
                        if (!nullable.Contains(rule.Expansion[k]))
                        {
                            allNullable = false;
                            break;
                        }
                    }
                    if (allNullable)
                    {
                        if (UpdateSet(first[rule.Origin], first[sym])) changed = true;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        // Calculate FOLLOW
        changed = true;
        while (changed)
        {
            changed = false;
            foreach (var rule in rules)
            {
                var exp = rule.Expansion;
                for (var i = 0; i < exp.Count; i++)
                {
                    var sym = exp[i];
                    var restNullable = true;
                    for (var k = i + 1; k < exp.Count; k++)
                    {
                        if (!nullable.Contains(exp[k]))
                        {
                            restNullable = false;
                            break;
                        }
                    }
                    if (i == exp.Count - 1 || restNullable)
                    {
                        if (UpdateSet(follow[sym], follow[rule.Origin])) changed = true;
                    }

                    for (var j = i + 1; j < exp.Count; j++)
                    {
                        var midNullable = true;
                        for (var k = i + 1; k < j; k++)
                        {
                            if (!nullable.Contains(exp[k]))
                            {
                                midNullable = false;
                                break;
                            }
                        }
                        if (midNullable)
                        {
                            if (UpdateSet(follow[sym], first[exp[j]])) changed = true;
                        }
                    }
                }
            }
        }

        return (first, follow, nullable);
    }
}
