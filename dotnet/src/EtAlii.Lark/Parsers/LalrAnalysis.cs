namespace EtAlii.Lark.Parsers;

using EtAlii.Lark.Utilities;

/// <summary>An action in the LALR parse table: shift to a state, or reduce by a rule.</summary>
public readonly record struct LalrAction(bool IsShift, int State, Rule? Rule)
{
    public static LalrAction Shift(int state) => new(true, state, null);

    public static LalrAction Reduce(Rule rule) => new(false, -1, rule);

    public override string ToString() => IsShift ? $"(Shift, {State})" : $"(Reduce, {Rule})";
}

/// <summary>The LALR(1) parse table.</summary>
public sealed class ParseTable
{
    public ParseTable(Dictionary<int, Dictionary<string, LalrAction>> states, Dictionary<string, int> startStates, Dictionary<string, int> endStates)
    {
        States = states;
        StartStates = startStates;
        EndStates = endStates;
    }

    public Dictionary<int, Dictionary<string, LalrAction>> States { get; }

    public Dictionary<string, int> StartStates { get; }

    public Dictionary<string, int> EndStates { get; }
}

/// <summary>Builds a LALR(1) transition table. Shift/reduce conflicts are resolved as shifts.</summary>
internal sealed class LalrAnalyzer : GrammarAnalyzer
{
    private readonly List<LR0ItemSet> _lr0ItemSets = new();
    private readonly List<(LR0ItemSet, Symbol)> _nonterminalTransitions = new();
    private readonly Dictionary<(LR0ItemSet, Symbol), HashSet<Symbol>> _directlyReads = new();
    private readonly Dictionary<(LR0ItemSet, Symbol), OrderedSet<(LR0ItemSet, Symbol)>> _reads = new();
    private readonly Dictionary<(LR0ItemSet, Symbol), OrderedSet<(LR0ItemSet, Symbol)>> _includes = new();
    private readonly Dictionary<(LR0ItemSet, Symbol), OrderedSet<(LR0ItemSet, Rule)>> _lookback = new();

    public LalrAnalyzer(ParserConf parserConf, bool debug = false, bool strict = false) : base(parserConf, debug, strict) { }

    public ParseTable ParseTable { get; private set; } = null!;

    private static TValue GetOrAdd<TKey, TValue>(Dictionary<TKey, TValue> d, TKey key) where TKey : notnull where TValue : new()
    {
        if (!d.TryGetValue(key, out var v))
        {
            v = new TValue();
            d[key] = v;
        }
        return v;
    }

    private void ComputeLr0States()
    {
        var cache = new Dictionary<FrozenSet<RulePtr>, LR0ItemSet>();
        var seen = new HashSet<LR0ItemSet>(ReferenceEqualityComparer.Instance);

        IEnumerable<LR0ItemSet> Step(LR0ItemSet state)
        {
            var unsat = state.Closure.Where(rp => !rp.IsSatisfied);
            var d = Helpers.Classify(unsat, rp => rp.Next);
            foreach (var (sym, rps) in d)
            {
                var kernel = new FrozenSet<RulePtr>(rps.Select(rp => rp.Advance(sym)));
                if (!cache.TryGetValue(kernel, out var newState))
                {
                    var closure = new OrderedSet<RulePtr>(kernel);
                    foreach (var rp in kernel)
                    {
                        if (!rp.IsSatisfied && !rp.Next.IsTerm)
                        {
                            closure.AddRange(ExpandRule((NonTerminal)rp.Next, Lr0RulesByOrigin));
                        }
                    }
                    newState = new LR0ItemSet(kernel, closure);
                    cache[kernel] = newState;
                }

                state.Transitions[sym] = newState;
                yield return newState;
            }

            if (seen.Add(state)) _lr0ItemSets.Add(state);
        }

        foreach (var _ in BfsByReference(Lr0StartStates.Values, Step)) { }
    }

    private static IEnumerable<LR0ItemSet> BfsByReference(IEnumerable<LR0ItemSet> initial, Func<LR0ItemSet, IEnumerable<LR0ItemSet>> expand)
    {
        var openQ = new Queue<LR0ItemSet>();
        var visited = new HashSet<LR0ItemSet>(ReferenceEqualityComparer.Instance);
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

    private void ComputeReadsRelations()
    {
        // handle start state
        foreach (var root in Lr0StartStates.Values)
        {
            foreach (var rp in root.Kernel)
            {
                _directlyReads[(root, rp.Next)] = new HashSet<Symbol> { new Terminal("$END") };
            }
        }

        foreach (var state in _lr0ItemSets)
        {
            var seen = new HashSet<Symbol>();
            foreach (var rp in state.Closure)
            {
                if (rp.IsSatisfied) continue;
                var s = rp.Next;
                // if s is a not a nonterminal
                if (s.IsTerm || !Lr0RulesByOrigin.ContainsKey((NonTerminal)s)) continue;
                if (!seen.Add(s)) continue;
                var nt = (state, s);
                _nonterminalTransitions.Add(nt);
                var dr = GetOrAdd(_directlyReads, nt);
                var r = GetOrAdd(_reads, nt);
                var nextState = state.Transitions[s];
                foreach (var rp2 in nextState.Closure)
                {
                    if (rp2.IsSatisfied) continue;
                    var s2 = rp2.Next;
                    // if s2 is a terminal
                    if (s2.IsTerm || !Lr0RulesByOrigin.ContainsKey((NonTerminal)s2)) dr.Add(s2);
                    if (Nullable.Contains(s2)) r.Add((nextState, s2));
                }
            }
        }
    }

    private void ComputeIncludesLookback()
    {
        foreach (var nt in _nonterminalTransitions)
        {
            var (state, nonterminal) = nt;
            var includes = new List<(LR0ItemSet, Symbol)>();
            var lookback = GetOrAdd(_lookback, nt);
            foreach (var rp in state.Closure)
            {
                if (!rp.Rule.Origin.Equals(nonterminal)) continue;
                // traverse the states for rp(.rule)
                var state2 = state;
                for (var i = rp.Index; i < rp.Rule.Expansion.Count; i++)
                {
                    var s = rp.Rule.Expansion[i];
                    var nt2 = (state2, s);
                    state2 = state2.Transitions[s];
                    if (!_reads.ContainsKey(nt2)) continue;
                    var allNullable = true;
                    for (var j = i + 1; j < rp.Rule.Expansion.Count; j++)
                    {
                        if (!Nullable.Contains(rp.Rule.Expansion[j]))
                        {
                            allNullable = false;
                            break;
                        }
                    }
                    if (allNullable) includes.Add(nt2);
                }
                // state2 is at the final state for rp.rule
                if (rp.Index == 0)
                {
                    foreach (var rp2 in state2.Closure)
                    {
                        if (rp2.Rule.Equals(rp.Rule) && rp2.IsSatisfied) lookback.Add((state2, rp2.Rule));
                    }
                }
            }
            foreach (var nt2 in includes)
            {
                GetOrAdd(_includes, nt2).Add(nt);
            }
        }
    }

    private static Dictionary<T, HashSet<Symbol>> Digraph<T>(IReadOnlyList<T> x, Func<T, IEnumerable<T>> r, Func<T, HashSet<Symbol>> g) where T : notnull
    {
        var f = new Dictionary<T, HashSet<Symbol>>();
        var s = new Stack<T>();
        var n = x.Distinct().ToDictionary(k => k, _ => 0);
        foreach (var node in x)
        {
            if (n[node] == 0) Traverse(node, s, n, r, g, f);
        }
        return f;
    }

    private static void Traverse<T>(T x, Stack<T> s, Dictionary<T, int> n, Func<T, IEnumerable<T>> r, Func<T, HashSet<Symbol>> g, Dictionary<T, HashSet<Symbol>> f) where T : notnull
    {
        s.Push(x);
        var d = s.Count;
        n[x] = d;
        f[x] = new HashSet<Symbol>(g(x));
        foreach (var y in r(x))
        {
            if (n[y] == 0) Traverse(y, s, n, r, g, f);
            var nX = n[x];
            var nY = n[y];
            if (nY > 0 && nY < nX) n[x] = nY;
            f[x].UnionWith(f[y]);
        }
        if (n[x] == d)
        {
            var fX = f[x];
            while (true)
            {
                var z = s.Pop();
                n[z] = -1;
                f[z] = fX;
                if (EqualityComparer<T>.Default.Equals(z, x)) break;
            }
        }
    }

    private void ComputeLookaheads()
    {
        IEnumerable<(LR0ItemSet, Symbol)> Reads((LR0ItemSet, Symbol) k) => _reads.TryGetValue(k, out var v) ? v : Enumerable.Empty<(LR0ItemSet, Symbol)>();
        IEnumerable<(LR0ItemSet, Symbol)> Includes((LR0ItemSet, Symbol) k) => _includes.TryGetValue(k, out var v) ? v : Enumerable.Empty<(LR0ItemSet, Symbol)>();
        HashSet<Symbol> DirectlyReads((LR0ItemSet, Symbol) k) => _directlyReads.TryGetValue(k, out var v) ? v : new HashSet<Symbol>();

        var readSets = Digraph(_nonterminalTransitions, Reads, DirectlyReads);
        var followSets = Digraph(_nonterminalTransitions, Includes, k => readSets[k]);

        foreach (var (nt, lookbacks) in _lookback)
        {
            foreach (var (state, rule) in lookbacks)
            {
                foreach (var s in followSets[nt])
                {
                    GetOrAdd(state.Lookaheads, s).Add(rule);
                }
            }
        }
    }

    private void ComputeLalr1States()
    {
        var m = new List<(LR0ItemSet ItemSet, Dictionary<string, (bool IsShift, LR0ItemSet? Next, Rule? Rule)> Actions)>();
        var reduceReduce = new List<(LR0ItemSet, Symbol, IReadOnlyCollection<Rule>)>();
        foreach (var itemset in _lr0ItemSets)
        {
            var actions = new Dictionary<Symbol, (bool IsShift, LR0ItemSet? Next, Rule? Rule)>();
            foreach (var (la, nextState) in itemset.Transitions)
            {
                actions[la] = (true, nextState, null);
            }
            foreach (var (la, rulesSet) in itemset.Lookaheads)
            {
                IReadOnlyCollection<Rule> rules = rulesSet;
                if (rules.Count > 1)
                {
                    // Try to resolve conflict based on priority
                    var p = rules.Select(r => (Priority: r.Options.Priority ?? 0, Rule: r)).OrderByDescending(x => x.Priority).ToList();
                    var best = p[0];
                    var secondBest = p[1];
                    if (best.Priority > secondBest.Priority)
                    {
                        rules = new[] { best.Rule };
                    }
                    else
                    {
                        reduceReduce.Add((itemset, la, rules));
                        continue;
                    }
                }

                var rule = rules.Single();
                if (actions.ContainsKey(la))
                {
                    if (Strict)
                    {
                        throw new GrammarException($"Shift/Reduce conflict for terminal {la.Name}. [strict-mode]\n * {rule}\n");
                    }
                    if (Debug)
                    {
                        LarkLog.Warning($"Shift/Reduce conflict for terminal {la.Name}: (resolving as shift)");
                        LarkLog.Warning($" * {rule}");
                    }
                    else
                    {
                        LarkLog.Debug($"Shift/Reduce conflict for terminal {la.Name}: (resolving as shift)");
                        LarkLog.Debug($" * {rule}");
                    }
                }
                else
                {
                    actions[la] = (false, null, rule);
                }
            }
            m.Add((itemset, actions.ToDictionary(kv => kv.Key.Name, kv => kv.Value)));
        }

        if (reduceReduce.Count > 0)
        {
            var msgs = new List<string>();
            foreach (var (itemset, la, rules) in reduceReduce)
            {
                var msg = $"Reduce/Reduce collision in {la} between the following rules: {string.Concat(rules.Select(r => "\n\t- " + r))}";
                if (Debug)
                {
                    msg += "\n    collision occurred in state: {" + string.Concat(itemset.Closure.Select(x => "\n\t" + x)) + "\n    }";
                }
                msgs.Add(msg);
            }
            throw new GrammarException(string.Join("\n\n", msgs));
        }

        var stateToIdx = new Dictionary<LR0ItemSet, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < m.Count; i++) stateToIdx[m[i].ItemSet] = i;

        var intStates = new Dictionary<int, Dictionary<string, LalrAction>>();
        foreach (var (itemset, actions) in m)
        {
            intStates[stateToIdx[itemset]] = actions.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.IsShift ? LalrAction.Shift(stateToIdx[kv.Value.Next!]) : LalrAction.Reduce(kv.Value.Rule!));
        }

        // compute end states
        var endStates = new Dictionary<string, int>();
        foreach (var (itemset, _) in m)
        {
            foreach (var rp in itemset.Closure)
            {
                foreach (var start in Lr0StartStates.Keys)
                {
                    if (rp.Rule.Origin.Name == "$root_" + start && rp.IsSatisfied)
                    {
                        endStates[start] = stateToIdx[itemset];
                    }
                }
            }
        }

        var startStates = Lr0StartStates.ToDictionary(kv => kv.Key, kv => stateToIdx[kv.Value]);
        ParseTable = new ParseTable(intStates, startStates, endStates);
    }

    public void ComputeLalr()
    {
        ComputeLr0States();
        ComputeReadsRelations();
        ComputeIncludesLookback();
        ComputeLookaheads();
        ComputeLalr1States();
    }
}
