namespace EtAlii.Lark;

using EtAlii.Lark.Utilities;

/// <summary>
/// A loaded (but not yet compiled) grammar: rule definitions, terminal definitions and ignored terminals.
/// </summary>
public sealed class Grammar
{
    internal Grammar(List<RuleDef> ruleDefs, List<TermDef> termDefs, List<string> ignore)
    {
        RuleDefs = ruleDefs;
        TermDefs = termDefs;
        Ignore = ignore;
    }

    internal List<RuleDef> RuleDefs { get; }

    internal List<TermDef> TermDefs { get; }

    /// <summary>The names of the ignored terminals.</summary>
    public IReadOnlyList<string> Ignore { get; }

    /// <summary>The names of the rules defined in this grammar.</summary>
    public IEnumerable<string> RuleNames => RuleDefs.Select(r => r.Name);

    /// <summary>The names of the terminals defined in this grammar.</summary>
    public IEnumerable<string> TerminalNames => TermDefs.Select(t => t.Name);

    /// <summary>Compiles the EBNF grammar into BNF rules and terminal definitions.</summary>
    /// <param name="start">The start rules.</param>
    /// <param name="terminalsToKeep">Terminals to keep even if unused; null keeps all terminals.</param>
    public (List<TerminalDef> Terminals, List<Rule> Rules, IReadOnlyList<string> Ignore) Compile(IReadOnlyList<string> start, IReadOnlyCollection<string>? terminalsToKeep)
    {
        // We change the trees in-place (to support huge grammars), so a deep copy allows calling compile more than once.
        var termDefs = TermDefs.Select(t => new TermDef(t.Name, t.Tree is null ? null : GrammarTransforms.CopyTrees(t.Tree), t.Priority)).ToList();
        var ruleDefs = RuleDefs.Select(r => new RuleDef(r.Name, r.Params, GrammarTransforms.CopyTrees(r.Tree!), r.Options)).ToList();

        // ===================
        //  Compile Terminals
        // ===================

        foreach (var td in termDefs)
        {
            if (td.Tree is null) continue; // Terminal added through %declare
            var expansions = td.Tree.FindData("expansion").ToList();
            if (expansions.Count == 1 && expansions[0].Children.Count == 0)
            {
                throw new GrammarException($"Terminals cannot be empty ({td.Name})");
            }
        }

        var terminals = termDefs
            .Where(td => td.Tree is not null)
            .Select(td => new TerminalDef(td.Name, GrammarTransforms.TerminalTreeToPattern(GrammarTransforms.PrepareLiterals(td.Tree!)), td.Priority))
            .ToList();

        // =================
        //  Compile Rules
        // =================

        // 1. Pre-process terminals
        var anonTokens = new PrepareAnonTerminals(terminals);
        // 2. Inline Templates
        var applyTemplates = new ApplyTemplates(ruleDefs);

        object? Transform(Tree t)
        {
            object? r = GrammarTransforms.PrepareLiterals(t);
            r = GrammarTransforms.ValidateSymbols((Tree)r!);
            r = GrammarTransforms.InPlace((Tree)r!, anonTokens.Call);
            r = GrammarTransforms.InPlace((Tree)r!, applyTemplates.Call);
            return r;
        }

        // 3. Convert EBNF to BNF (and apply step 1 & 2)
        var ebnfToBnf = new EbnfToBnf();
        var rules = new List<(string Name, Tree Tree, RuleOptions? Options)>();
        var i = 0;
        while (i < ruleDefs.Count) // We have to do it like this because rule_defs might grow due to templates
        {
            var def = ruleDefs[i];
            i++;
            if (def.Params.Count != 0) continue; // Dont transform templates
            var ruleOptions = def.Options is { KeepAllTokens: true } ? new RuleOptions(keepAllTokens: true) : null;
            ebnfToBnf.RuleOptions = ruleOptions;
            ebnfToBnf.Prefix = def.Name;
            anonTokens.RuleOptions = ruleOptions;
            var tree = (Tree)Transform(def.Tree!)!;
            var res = (Tree)ebnfToBnf.Transform(tree)!;
            rules.Add((def.Name, res, def.Options));
        }
        rules.AddRange(ebnfToBnf.NewRules);

        if (rules.Count != rules.Select(r => r.Name).Distinct().Count())
        {
            throw new InvalidOperationException("Whoops, name collision");
        }

        // 4. Compile tree to Rule objects
        var compiledRules = new List<Rule>();
        foreach (var (name, tree, options) in rules)
        {
            GrammarTransforms.SimplifyRule(tree);
            var expansions = GrammarTransforms.RuleTreeToText(tree);

            for (var idx = 0; idx < expansions.Count; idx++)
            {
                var (expansionIn, alias) = expansions[idx];
                if (alias is not null && name.StartsWith('_'))
                {
                    throw new GrammarException($"Rule {name} is marked for expansion (it starts with an underscore) and isn't allowed to have aliases (alias={alias})");
                }

                var expansion = expansionIn;
                var emptyIndices = expansion.Select(x => ReferenceEquals(x, EmptySymbol.Instance)).ToList();
                RuleOptions? expOptions;
                if (emptyIndices.Any(x => x))
                {
                    expOptions = options?.Clone() ?? new RuleOptions();
                    expOptions.EmptyIndices = emptyIndices;
                    expansion = expansion.Where(x => !ReferenceEquals(x, EmptySymbol.Instance)).ToList();
                }
                else
                {
                    expOptions = options;
                }

                foreach (var sym in expansion)
                {
                    if (sym is Terminal term && expOptions is { KeepAllTokens: true })
                    {
                        term.FilterOut = false;
                    }
                }
                compiledRules.Add(new Rule(new NonTerminal(name), expansion, idx, alias, expOptions));
            }
        }

        // Remove duplicates of empty rules, throw error for non-empty duplicates
        if (compiledRules.Distinct().Count() != compiledRules.Count)
        {
            var duplicates = Helpers.Classify(compiledRules, x => x);
            foreach (var dups in duplicates.Values)
            {
                if (dups.Count > 1 && dups[0].Expansion.Count > 0)
                {
                    throw new GrammarException("Rules defined twice: " + string.Concat(dups.Select(d => "\n  * " + d)) +
                                               "\n\n(Might happen due to colliding expansion of optionals: [] or ?)");
                }
            }
            // Remove duplicates
            compiledRules = new OrderedSet<Rule>(compiledRules).ToList();
        }

        // Filter out unused rules
        while (true)
        {
            var c = compiledRules.Count;
            var usedRules = new HashSet<Symbol>(compiledRules
                .SelectMany(r => r.Expansion.Where(s => s is NonTerminal && !s.Equals(r.Origin))));
            foreach (var s in start) usedRules.Add(new NonTerminal(s));
            var (used, unused) = Helpers.ClassifyBool(compiledRules, r => usedRules.Contains(r.Origin));
            compiledRules = used;
            foreach (var r in unused) LarkLog.Debug($"Unused rule: {r}");
            if (compiledRules.Count == c) break;
        }

        // Filter out unused terminals
        if (terminalsToKeep is not null)
        {
            var usedTerms = compiledRules.SelectMany(r => r.Expansion.OfType<Terminal>()).Select(t => t.Name).ToHashSet();
            var (kept, unused) = Helpers.ClassifyBool(terminals, t => usedTerms.Contains(t.Name) || Ignore.Contains(t.Name) || terminalsToKeep.Contains(t.Name));
            terminals = kept;
            if (unused.Count > 0) LarkLog.Debug("Unused terminals: [" + string.Join(", ", unused.Select(t => t.Name)) + "]");
        }

        return (terminals, compiledRules, Ignore);
    }
}

internal sealed record RuleDef(string Name, IReadOnlyList<string> Params, Tree? Tree, RuleOptions? Options);

internal sealed record TermDef(string Name, Tree? Tree, int Priority);

/// <summary>Create a unique list of anonymous terminals. Attempt to give meaningful names to them when we add them.</summary>
internal sealed class PrepareAnonTerminals
{
    private static readonly Dictionary<string, string> TerminalNames = new()
    {
        ["."] = "DOT", [","] = "COMMA", [":"] = "COLON", [";"] = "SEMICOLON", ["+"] = "PLUS", ["-"] = "MINUS",
        ["*"] = "STAR", ["/"] = "SLASH", ["\\"] = "BACKSLASH", ["|"] = "VBAR", ["?"] = "QMARK", ["!"] = "BANG",
        ["@"] = "AT", ["#"] = "HASH", ["$"] = "DOLLAR", ["%"] = "PERCENT", ["^"] = "CIRCUMFLEX", ["&"] = "AMPERSAND",
        ["_"] = "UNDERSCORE", ["<"] = "LESSTHAN", [">"] = "MORETHAN", ["="] = "EQUAL", ["\""] = "DBLQUOTE",
        ["'"] = "QUOTE", ["`"] = "BACKQUOTE", ["~"] = "TILDE", ["("] = "LPAR", [")"] = "RPAR", ["{"] = "LBRACE",
        ["}"] = "RBRACE", ["["] = "LSQB", ["]"] = "RSQB", ["\n"] = "NEWLINE", ["\r\n"] = "CRLF", ["\t"] = "TAB",
        [" "] = "SPACE",
    };

    private readonly List<TerminalDef> _terminals;
    private readonly HashSet<string> _termSet;
    private readonly Dictionary<Pattern, TerminalDef> _termReverse = new();
    private int _i;

    public PrepareAnonTerminals(List<TerminalDef> terminals)
    {
        _terminals = terminals;
        _termSet = terminals.Select(t => t.Name).ToHashSet();
        foreach (var td in terminals) _termReverse[td.Pattern] = td;
    }

    public RuleOptions? RuleOptions { get; set; }

    public object? Call(Tree t)
    {
        if (t.Data != "pattern") return t;
        var p = (Pattern)t.Children[0]!;
        var value = p.Value;
        if (_termReverse.TryGetValue(p, out var existing) && p.Flags != existing.Pattern.Flags)
        {
            throw new GrammarException($"Conflicting flags for the same terminal: {p}");
        }

        string? termName = null;

        if (p is PatternStr)
        {
            if (_termReverse.TryGetValue(p, out var td))
            {
                // If already defined, use the user-defined terminal name
                termName = td.Name;
            }
            else
            {
                // Try to assign an indicative anon-terminal name
                if (TerminalNames.TryGetValue(value, out var n))
                {
                    termName = n;
                }
                else if (value.Length > 0 && Helpers.IsIdContinue(value) && Helpers.IsIdStart(value[..1]) && !_termSet.Contains(value.ToUpperInvariant()))
                {
                    termName = value.ToUpperInvariant();
                }

                if (termName is not null && _termSet.Contains(termName)) termName = null;
            }
        }
        else if (p is PatternRE)
        {
            if (_termReverse.TryGetValue(p, out var td)) termName = td.Name;
        }

        if (termName is null)
        {
            termName = $"__ANON_{_i}";
            _i++;
        }

        if (!_termSet.Contains(termName))
        {
            _termSet.Add(termName);
            var termdef = new TerminalDef(termName, p);
            _termReverse[p] = termdef;
            _terminals.Add(termdef);
        }

        var filterOut = RuleOptions is { KeepAllTokens: true } ? false : p is PatternStr;
        return new Terminal(termName, filterOut);
    }
}

/// <summary>Apply the templates, creating new rules that represent the used templates.</summary>
internal sealed class ApplyTemplates
{
    private readonly List<RuleDef> _ruleDefs;
    private readonly HashSet<string> _createdTemplates = new();

    public ApplyTemplates(List<RuleDef> ruleDefs)
    {
        _ruleDefs = ruleDefs;
    }

    public object? Call(Tree t)
    {
        if (t.Data != "template_usage") return t;
        var c = t.Children;
        var name = ((Symbol)c[0]!).Name;
        var args = c.Skip(1).Select(a => (Symbol)a!).ToList();
        var resultName = $"{name}{{{string.Join(",", args.Select(a => a.Name))}}}";
        if (_createdTemplates.Add(resultName))
        {
            var def = _ruleDefs.Single(r => r.Name == name);
            if (def.Params.Count != args.Count) throw new InvalidOperationException("Wrong number of template arguments");
            var resultTree = GrammarTransforms.DeepCopy(def.Tree!);
            var names = def.Params.Zip(args).ToDictionary(x => x.First, x => x.Second);
            resultTree = (Tree)GrammarTransforms.InPlace(resultTree, sub => ReplaceSymbols(sub, names))!;
            _ruleDefs.Add(new RuleDef(resultName, Array.Empty<string>(), resultTree, def.Options?.Clone()));
        }
        return new NonTerminal(resultName);
    }

    private static object? ReplaceSymbols(Tree t, Dictionary<string, Symbol> names)
    {
        var c = t.Children;
        if (t.Data == "value")
        {
            if (c.Count == 1 && c[0] is Symbol s && names.TryGetValue(s.Name, out var replacement)) return replacement;
            return t;
        }
        if (t.Data == "template_usage")
        {
            var name = ((Symbol)c[0]!).Name;
            if (names.TryGetValue(name, out var replacement))
            {
                return new Tree("template_usage", new List<object?> { replacement }.Concat(c.Skip(1)).ToList());
            }
            return t;
        }
        return t;
    }
}

/// <summary>Converts EBNF constructs (<c>?</c>, <c>*</c>, <c>+</c>, <c>~</c>, <c>[]</c>) into BNF.</summary>
internal sealed class EbnfToBnf
{
    // Value 5 keeps the number of states in the lalr parser somewhat minimal. It isn't optimal, but close to it.
    private const int SmallFactorThreshold = 5;

    // The threshold whether repeats via ~ are split up into different rules.
    private const int RepeatBreakThreshold = 50;

    private readonly Dictionary<object, NonTerminal> _rulesCache = new();
    private int _i;

    public List<(string Name, Tree Tree, RuleOptions? Options)> NewRules { get; } = new();

    public string Prefix { get; set; } = "anon";

    public RuleOptions? RuleOptions { get; set; }

    private static Tree ST(string data, params object?[] children) => new(data, children.ToList());

    private static Tree ST(string data, List<object?> children) => new(data, children);

    private string NameRule(string inner)
    {
        var newName = $"__{Prefix}_{inner}_{_i}";
        _i++;
        return newName;
    }

    private NonTerminal AddRule(object key, string name, Tree expansions)
    {
        var t = new NonTerminal(name);
        NewRules.Add((name, expansions, RuleOptions));
        _rulesCache[key] = t;
        return t;
    }

    private NonTerminal AddRecurseRule(string type, object expr)
    {
        if (_rulesCache.TryGetValue(new CacheKey(expr), out var cached)) return cached;
        var newName = NameRule(type);
        var t = new NonTerminal(newName);
        var tree = ST("expansions", ST("expansion", expr), ST("expansion", t, expr));
        return AddRule(new CacheKey(expr), newName, tree);
    }

    private NonTerminal AddRepeatRule(int a, int b, object target, object atom)
    {
        var key = (a, b, new CacheKey(target), new CacheKey(atom));
        if (_rulesCache.TryGetValue(key, out var cached)) return cached;
        var newName = NameRule($"repeat_a{a}_b{b}");
        var children = Enumerable.Repeat(target, a).Concat(Enumerable.Repeat(atom, b)).ToList();
        var tree = ST("expansions", ST("expansion", children));
        return AddRule(key, newName, tree);
    }

    private NonTerminal AddRepeatOptRule(int a, int b, object target, object targetOpt, object atom)
    {
        var key = (a, b, new CacheKey(target), new CacheKey(atom), "opt");
        if (_rulesCache.TryGetValue(key, out var cached)) return cached;
        var newName = NameRule($"repeat_a{a}_b{b}_opt");
        var exps = new List<object?>();
        for (var i = 0; i < a; i++) exps.Add(ST("expansion", Enumerable.Repeat(target, i).Append(targetOpt).ToList()));
        for (var i = 0; i < b; i++) exps.Add(ST("expansion", Enumerable.Repeat(target, a).Concat(Enumerable.Repeat(atom, i)).ToList()));
        var tree = ST("expansions", exps);
        return AddRule(key, newName, tree);
    }

    /// <summary>Generates a rule tree that repeats <paramref name="rule"/> exactly between mn to mx times.</summary>
    private object GenerateRepeats(object rule, int mn, int mx)
    {
        // For a small number of repeats, we can take the naive approach
        if (mx < RepeatBreakThreshold)
        {
            var exps = new List<object?>();
            for (var n = mn; n <= mx; n++) exps.Add(ST("expansion", Enumerable.Repeat(rule, n).ToList()));
            return ST("expansions", exps);
        }

        // For large repeat values, we break the repetition into sub-rules.
        object mnTarget = rule;
        foreach (var (a, b) in Helpers.SmallFactors(mn, SmallFactorThreshold))
        {
            mnTarget = AddRepeatRule(a, b, mnTarget, rule);
        }
        if (mx == mn) return mnTarget;

        var diff = mx - mn + 1; // We add one because _add_repeat_opt_rule generates rules that match one less
        var diffFactors = Helpers.SmallFactors(diff, SmallFactorThreshold);
        object diffTarget = rule; // Match rule 1 times
        object diffOptTarget = ST("expansion"); // match rule 0 times (e.g. up to 1 -1 times)
        for (var k = 0; k < diffFactors.Count - 1; k++)
        {
            var (a, b) = diffFactors[k];
            diffOptTarget = AddRepeatOptRule(a, b, diffTarget, diffOptTarget, rule);
            diffTarget = AddRepeatRule(a, b, diffTarget, rule);
        }

        var (la, lb) = diffFactors[^1];
        diffOptTarget = AddRepeatOptRule(la, lb, diffTarget, diffOptTarget, rule);

        return ST("expansions", ST("expansion", mnTarget, diffOptTarget));
    }

    private object Expr(object rule, Token op, List<object?> args)
    {
        switch (op.Value)
        {
            case "?":
                return ST("expansions", rule, ST("expansion"));
            case "+":
                // a : b c+ d  -->  a : b _c d ; _c : _c c | c;
                return AddRecurseRule("plus", rule);
            case "*":
            {
                // a : b c* d  -->  a : b _c? d ; _c : _c c | c;
                var newName = AddRecurseRule("star", rule);
                return ST("expansions", newName, ST("expansion"));
            }
            case "~":
            {
                int mn, mx;
                if (args.Count == 1)
                {
                    mn = mx = int.Parse(args[0]!.ToString()!);
                }
                else
                {
                    mn = int.Parse(args[0]!.ToString()!);
                    mx = int.Parse(args[1]!.ToString()!);
                    if (mx < mn || mn < 0)
                    {
                        throw new GrammarException($"Bad Range for {Tree.ChildRepr(rule)} ({mn}..{mx} isn't allowed)");
                    }
                }
                return GenerateRepeats(rule, mn, mx);
            }
        }
        throw new InvalidOperationException($"Unknown operator {op}");
    }

    private object Maybe(object rule)
    {
        var keepAllTokens = RuleOptions is { KeepAllTokens: true };
        var ruleSize = FindRuleSize(rule, keepAllTokens);
        var empty = ST("expansion", Enumerable.Repeat<object?>(EmptySymbol.Instance, ruleSize).ToList());
        return ST("expansions", rule, empty);
    }

    private static int FindRuleSize(object? node, bool keepAllTokens)
    {
        switch (node)
        {
            case Tree { Data: "expansion" } t:
                return t.Children.Sum(c => FindRuleSize(c, keepAllTokens));
            case Tree { Data: "expansions" } t:
                return t.Children.Count == 0 ? 0 : t.Children.Max(c => FindRuleSize(c, keepAllTokens));
            case EmptySymbol:
                return 0;
            case NonTerminal nt:
                return nt.Name.StartsWith('_') ? 0 : 1;
            case Terminal term:
                return keepAllTokens || !term.FilterOut ? 1 : 0;
            default:
                throw new InvalidOperationException($"Unexpected node in rule: {node}");
        }
    }

    private object? Call(Tree t) => t.Data switch
    {
        "expr" => Expr(t.Children[0]!, (Token)t.Children[1]!, t.Children.Skip(2).ToList()),
        "maybe" => Maybe(t.Children[0]!),
        _ => t,
    };

    public object? Transform(Tree tree) => GrammarTransforms.InPlace(tree, Call);

    /// <summary>A dictionary key with structural equality for trees and symbols.</summary>
    private readonly record struct CacheKey(object Value)
    {
        public bool Equals(CacheKey other) => Tree.ValueEquals(Value, other.Value);

        public override int GetHashCode() => Value.GetHashCode();
    }
}

/// <summary>The tree transformations used while loading and compiling grammars.</summary>
internal static class GrammarTransforms
{
    /// <summary>Deep copy of the trees of a grammar definition (leaves are shared), Lark's <c>nr_deepcopy_tree</c>.</summary>
    public static Tree CopyTrees(Tree t) => new(t.Data, t.Children.Select(c => c is Tree ct ? CopyTrees(ct) : c).ToList());

    /// <summary>Deep copy that also copies symbols and tokens.</summary>
    public static Tree DeepCopy(Tree t) => new(t.Data, t.Children.Select(c => c switch
    {
        Tree ct => DeepCopy(ct),
        Terminal term => new Terminal(term.Name, term.FilterOut),
        NonTerminal nt => new NonTerminal(nt.Name),
        Token tok => tok.Copy(),
        _ => c,
    }).ToList());

    /// <summary>Lark's <c>Transformer_InPlace</c>: transforms the tree bottom-up, replacing subtrees by the result of <paramref name="call"/>.</summary>
    public static object? InPlace(Tree tree, Func<Tree, object?> call)
    {
        foreach (var subtree in tree.IterSubtrees().ToList())
        {
            var newChildren = new List<object?>(subtree.Children.Count);
            foreach (var c in subtree.Children)
            {
                var res = c is Tree ct ? call(ct) : c;
                if (!ReferenceEquals(res, Discard.Value)) newChildren.Add(res);
            }
            subtree.Children = newChildren;
        }
        return call(tree);
    }

    /// <summary>Converts terminal and nonterminal nodes to symbols.</summary>
    public static Tree PrepareGrammar(Tree tree) => (Tree)InPlace(tree, t => t.Data switch
    {
        "terminal" => new Terminal(t.Children[0]!.ToString()!, t.Children[0]!.ToString()!.StartsWith('_')),
        "nonterminal" => new NonTerminal(((Token)t.Children[0]!).Value),
        _ => t,
    })!;

    /// <summary>Converts literals and ranges into patterns.</summary>
    public static object? PrepareLiterals(Tree tree) => InPlace(tree, t =>
    {
        switch (t.Data)
        {
            case "literal":
                return new Tree("pattern", new List<object?> { LiteralToPattern((Token)t.Children[0]!) });
            case "range":
            {
                var start = (Token)t.Children[0]!;
                var end = (Token)t.Children[1]!;
                var s = start.Value[1..^1];
                var e = end.Value[1..^1];
                var sv = EvalEscaping.Eval(s);
                var ev = EvalEscaping.Eval(e);
                if (!IsSingleCodePoint(sv) || !IsSingleCodePoint(ev))
                {
                    throw new GrammarException($"Range bounds must be single characters ({start}..{end})");
                }
                var lo = char.ConvertToUtf32(sv, 0);
                var hi = char.ConvertToUtf32(ev, 0);
                // .NET strings are UTF-16: ranges beyond the BMP are expressed with surrogate pairs.
                var regex = hi <= 0xFFFF ? $"[{s}-{e}]" : CodePointRangeRegex(lo, hi);
                return new Tree("pattern", new List<object?> { new PatternRE(regex) });
            }
            default:
                return t;
        }
    });

    private static bool IsSingleCodePoint(string s) =>
        s.Length == 1 ? !char.IsSurrogate(s[0]) : s.Length == 2 && char.IsSurrogatePair(s[0], s[1]);

    /// <summary>A regular expression that matches the code points from <paramref name="lo"/> to <paramref name="hi"/> in UTF-16 text.</summary>
    internal static string CodePointRangeRegex(int lo, int hi)
    {
        static string U(int c) => $"\\u{c:X4}";
        var parts = new List<string>();
        if (lo > hi) throw new GrammarException("Bad range: the start is larger than the end");
        if (lo <= 0xFFFF)
        {
            parts.Add($"[{U(lo)}-{U(0xFFFF)}]");
            lo = 0x10000;
        }
        var loHigh = 0xD800 + ((lo - 0x10000) >> 10);
        var hiHigh = 0xD800 + ((hi - 0x10000) >> 10);
        var loLow = 0xDC00 + ((lo - 0x10000) & 0x3FF);
        var hiLow = 0xDC00 + ((hi - 0x10000) & 0x3FF);
        if (loHigh == hiHigh)
        {
            parts.Add($"{U(loHigh)}[{U(loLow)}-{U(hiLow)}]");
        }
        else
        {
            parts.Add($"{U(loHigh)}[{U(loLow)}-{U(0xDFFF)}]");
            if (hiHigh - loHigh > 1) parts.Add($"[{U(loHigh + 1)}-{U(hiHigh - 1)}][{U(0xDC00)}-{U(0xDFFF)}]");
            parts.Add($"{U(hiHigh)}[{U(0xDC00)}-{U(hiLow)}]");
        }
        return "(?:" + string.Join("|", parts) + ")";
    }

    public static object? ValidateSymbols(Tree tree) => InPlace(tree, t =>
    {
        if (t.Data != "value") return t;
        var v = t.Children.Single();
        if (v is not (Tree or Symbol)) throw new InvalidOperationException($"Unexpected value {v}");
        return v;
    });

    private const string ReFlags = "imslux";

    public static Pattern LiteralToPattern(Token literal)
    {
        var v = literal.Value;
        var flagStart = Math.Max(v.LastIndexOf('/'), v.LastIndexOf('"')) + 1;
        if (flagStart <= 0) throw new InvalidOperationException("Bad literal");
        var flags = v[flagStart..];
        if (!flags.All(f => ReFlags.Contains(f))) throw new InvalidOperationException("Bad flags");

        if (literal.Type == "STRING" && v.Contains('\n'))
        {
            throw new GrammarException("You cannot put newlines in string literals");
        }

        if (literal.Type == "REGEXP" && v.Contains('\n') && !flags.Contains('x'))
        {
            throw new GrammarException("You can only use newlines in regular expressions with the `x` (verbose) flag");
        }

        v = v[..flagStart];
        var x = v[1..^1];

        var s = EvalEscaping.Eval(x);

        if (s.Length == 0)
        {
            throw new GrammarException($"Empty terminals are not allowed ({literal})");
        }

        if (literal.Type == "STRING")
        {
            s = s.Replace("\\\\", "\\");
            return new PatternStr(s, flags, raw: literal.Value);
        }
        if (literal.Type == "REGEXP")
        {
            return new PatternRE(s, flags, raw: literal.Value);
        }
        throw new InvalidOperationException("Invariant failed: literal.type not in [\"STRING\", \"REGEXP\"]");
    }

    /// <summary>Converts a terminal tree into a single pattern (Lark's <c>TerminalTreeToPattern</c>, non-recursive).</summary>
    public static Pattern TerminalTreeToPattern(object? root)
    {
        // Postfix evaluation, mirroring Transformer_NonRecursive
        var revPostfix = new List<object?>();
        var q = new Stack<object?>();
        q.Push(root);
        while (q.Count > 0)
        {
            var t = q.Pop();
            revPostfix.Add(t);
            if (t is Tree tt)
            {
                foreach (var c in tt.Children) q.Push(c);
            }
        }

        var stack = new List<object?>();
        for (var i = revPostfix.Count - 1; i >= 0; i--)
        {
            var x = revPostfix[i];
            if (x is Tree xt)
            {
                var size = xt.Children.Count;
                var args = stack.GetRange(stack.Count - size, size);
                stack.RemoveRange(stack.Count - size, size);
                stack.Add(TerminalNode(xt.Data, args));
            }
            else
            {
                stack.Add(x);
            }
        }
        var result = stack.Single();
        return result as Pattern ?? throw new GrammarException($"Invalid terminal definition: {result}");
    }

    private static object? TerminalNode(string data, List<object?> args)
    {
        switch (data)
        {
            case "pattern":
                return args.Single();
            case "expansion":
            {
                if (args.Count == 0) return new PatternStr("");
                if (args.Count == 1) return args[0];
                var pattern = string.Concat(args.Cast<Pattern>().Select(p => p.ToRegexp()));
                return new PatternRE(pattern);
            }
            case "expansions":
            {
                var exps = args.Cast<Pattern>().ToList();
                if (exps.Count == 1) return exps[0];
                // Do a bit of sorting to make sure that the longest option is returned
                // (Python's re module otherwise prefers just 'l' when given (l|ll) and both could match)
                exps = exps.OrderBy(x => -(long)x.MaxWidth).ThenBy(x => -x.MinWidth).ThenBy(x => -x.Value.Length).ToList();
                var pattern = "(?:" + string.Join("|", exps.Select(p => p.ToRegexp())) + ")";
                return new PatternRE(pattern);
            }
            case "expr":
                return TerminalExpr(args);
            case "maybe":
                return TerminalExpr(args.Append("?").ToList());
            case "alias":
                throw new GrammarException("Aliasing not allowed in terminals (You used -> in the wrong place)");
            case "value":
                return args[0];
            default:
                return new Tree(data, args);
        }
    }

    private static Pattern TerminalExpr(List<object?> args)
    {
        var inner = (Pattern)args[0]!;
        var op = args[1]!.ToString()!;
        if (op == "~")
        {
            if (args.Count == 3)
            {
                op = "{" + int.Parse(args[2]!.ToString()!) + "}";
            }
            else
            {
                var mn = int.Parse(args[2]!.ToString()!);
                var mx = int.Parse(args[3]!.ToString()!);
                if (mx < mn)
                {
                    throw new GrammarException($"Bad Range for {inner} ({mn}..{mx} isn't allowed)");
                }
                op = "{" + mn + "," + mx + "}";
            }
        }
        else if (args.Count != 2)
        {
            throw new InvalidOperationException("Unexpected arguments for terminal expression");
        }
        return new PatternRE($"(?:{inner.ToRegexp()}){op}", inner.Flags);
    }

    // --- SimplifyRule_Visitor ---

    private static void Flatten(Tree tree)
    {
        while (tree.ExpandKidsByData(tree.Data)) { }
    }

    public static Tree SimplifyRule(Tree tree)
    {
        foreach (var subtree in tree.IterSubtrees().ToList())
        {
            SimplifyCall(subtree);
        }
        return tree;
    }

    private static void SimplifyCall(Tree tree)
    {
        switch (tree.Data)
        {
            case "expansion":
                SimplifyExpansion(tree);
                break;
            case "alias":
                SimplifyAlias(tree);
                break;
            case "expansions":
                Flatten(tree);
                // Ensure all children are unique
                if (tree.Children.Distinct(TreeChildComparer.Instance).Count() != tree.Children.Count)
                {
                    tree.Children = DedupChildren(tree.Children);
                }
                break;
        }
    }

    private static List<object?> DedupChildren(IEnumerable<object?> children)
    {
        var seen = new HashSet<object?>(TreeChildComparer.Instance);
        var result = new List<object?>();
        foreach (var c in children)
        {
            if (seen.Add(c)) result.Add(c);
        }
        return result;
    }

    private static void SimplifyExpansion(Tree tree)
    {
        // rules_list unpacking
        // a : b (c|d) e
        //  -->
        // a : b c e | b d e
        Flatten(tree);

        for (var i = 0; i < tree.Children.Count; i++)
        {
            if (tree.Children[i] is Tree { Data: "expansions" } child)
            {
                var original = tree.Children;
                tree.Data = "expansions";
                tree.Children = DedupChildren(child.Children)
                    .Select(option => (object?)SimplifyRule(new Tree("expansion", original.Select((other, j) => i == j ? option : other).ToList())))
                    .ToList();
                Flatten(tree);
                break;
            }
        }
    }

    private static void SimplifyAlias(Tree tree)
    {
        var rule = (Tree)tree.Children[0]!;
        var aliasName = tree.Children[1];
        if (rule.Data == "expansions")
        {
            var aliases = rule.Children.Select(child => (object?)new Tree("alias", new List<object?> { child, aliasName })).ToList();
            tree.Data = "expansions";
            tree.Children = aliases;
        }
    }

    /// <summary>Lark's <c>RuleTreeToText</c>: converts a simplified rule tree into a list of (expansion, alias).</summary>
    public static List<(List<Symbol> Expansion, string? Alias)> RuleTreeToText(Tree tree)
    {
        object Convert(object? node)
        {
            switch (node)
            {
                case Tree { Data: "expansions" } t:
                    return t.Children.Select(Convert).Select(x => ((List<Symbol>, string?))x).ToList();
                case Tree { Data: "expansion" } t:
                    return (t.Children.Select(c => (Symbol)c!).ToList(), (string?)null);
                case Tree { Data: "alias" } t:
                {
                    var (expansion, innerAlias) = ((List<Symbol>, string?))Convert(t.Children[0]);
                    if (innerAlias is not null) throw new InvalidOperationException("Double alias not allowed");
                    var alias = (Symbol)t.Children[1]!;
                    return (expansion, (string?)alias.Name);
                }
                default:
                    throw new GrammarException($"Unexpected rule structure: {node}");
            }
        }

        return (List<(List<Symbol>, string?)>)Convert(tree);
    }

    private sealed class TreeChildComparer : IEqualityComparer<object?>
    {
        public static readonly TreeChildComparer Instance = new();

        public new bool Equals(object? x, object? y) => Tree.ValueEquals(x, y);

        public int GetHashCode(object? obj) => obj?.GetHashCode() ?? 0;
    }
}
