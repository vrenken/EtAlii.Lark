namespace EtAlii.Lark;

using EtAlii.Lark.Utilities;

/// <summary>The wrappers that shape the parse tree while it is being built.</summary>
internal static class TreeBuilderWrappers
{
    public static Func<List<object?>, object?> ExpandSingleChild(Func<List<object?>, object?> nodeBuilder) =>
        children => children.Count == 1 ? children[0] : nodeBuilder(children);

    private static ILarkPositioned? PpGetMeta(IEnumerable<object?> children, Func<object?, bool>? nodeFilter)
    {
        foreach (var c in children)
        {
            if (nodeFilter is not null && !nodeFilter(c)) continue;
            switch (c)
            {
                case Tree t:
                    if (!t.Meta.Empty) return t.Meta;
                    break;
                case Token tok:
                    return tok;
                case ILarkMetaProvider p:
                    return p.LarkMeta;
            }
        }
        return null;
    }

    public static Func<List<object?>, object?> PropagatePositions(Func<List<object?>, object?> nodeBuilder, Func<object?, bool>? nodeFilter)
    {
        return children =>
        {
            var res = nodeBuilder(children);

            if (res is Tree tree)
            {
                // Calculate positions while the tree is streaming, according to the rule:
                // - nodes start at the start of their first child's container,
                //   and end at the end of their last child's container.
                // Containers are nodes that take up space in text, but have been inlined in the tree.
                var resMeta = tree.Meta;

                var firstMeta = PpGetMeta(children, nodeFilter);
                if (firstMeta is not null)
                {
                    if (resMeta.Line is null)
                    {
                        // meta was already set, probably because the rule has been inlined (e.g. `?rule`)
                        resMeta.Line = firstMeta.ContainerLine ?? firstMeta.Line;
                        resMeta.Column = firstMeta.ContainerColumn ?? firstMeta.Column;
                        resMeta.StartPos = firstMeta.ContainerStartPos ?? firstMeta.StartPos;
                        resMeta.Empty = false;
                    }

                    resMeta.ContainerLine = firstMeta.ContainerLine ?? firstMeta.Line;
                    resMeta.ContainerColumn = firstMeta.ContainerColumn ?? firstMeta.Column;
                    resMeta.ContainerStartPos = firstMeta.ContainerStartPos ?? firstMeta.StartPos;
                }

                var lastMeta = PpGetMeta(Enumerable.Reverse(children), nodeFilter);
                if (lastMeta is not null)
                {
                    if (resMeta.EndLine is null)
                    {
                        resMeta.EndLine = lastMeta.ContainerEndLine ?? lastMeta.EndLine;
                        resMeta.EndColumn = lastMeta.ContainerEndColumn ?? lastMeta.EndColumn;
                        resMeta.EndPos = lastMeta.ContainerEndPos ?? lastMeta.EndPos;
                        resMeta.Empty = false;
                    }

                    resMeta.ContainerEndLine = lastMeta.ContainerEndLine ?? lastMeta.EndLine;
                    resMeta.ContainerEndColumn = lastMeta.ContainerEndColumn ?? lastMeta.EndColumn;
                    resMeta.ContainerEndPos = lastMeta.ContainerEndPos ?? lastMeta.EndPos;
                }
            }

            return res;
        };
    }

    public static Func<List<object?>, object?> ChildFilter(IReadOnlyList<(int Index, bool ToExpand, int AddNone)> toInclude, int appendNone, Func<List<object?>, object?> nodeBuilder)
    {
        return children =>
        {
            var filtered = new List<object?>();
            foreach (var (i, toExpand, addNone) in toInclude)
            {
                for (var k = 0; k < addNone; k++) filtered.Add(null);
                if (toExpand) filtered.AddRange(((Tree)children[i]!).Children);
                else filtered.Add(children[i]);
            }
            for (var k = 0; k < appendNone; k++) filtered.Add(null);
            return nodeBuilder(filtered);
        };
    }

    /// <summary>Optimized child filter for LALR (assumes no duplication in parse tree, so it's safe to change it).</summary>
    public static Func<List<object?>, object?> ChildFilterLalr(IReadOnlyList<(int Index, bool ToExpand, int AddNone)> toInclude, int appendNone, Func<List<object?>, object?> nodeBuilder)
    {
        return children =>
        {
            var filtered = new List<object?>();
            foreach (var (i, toExpand, addNone) in toInclude)
            {
                for (var k = 0; k < addNone; k++) filtered.Add(null);
                if (toExpand)
                {
                    var sub = ((Tree)children[i]!).Children;
                    if (filtered.Count > 0) filtered.AddRange(sub);
                    else filtered = sub; // Optimize for left-recursion
                }
                else
                {
                    filtered.Add(children[i]);
                }
            }
            for (var k = 0; k < appendNone; k++) filtered.Add(null);
            return nodeBuilder(filtered);
        };
    }

    /// <summary>Optimized child filter for LALR without placeholders.</summary>
    public static Func<List<object?>, object?> ChildFilterLalrNoPlaceholders(IReadOnlyList<(int Index, bool ToExpand)> toInclude, Func<List<object?>, object?> nodeBuilder)
    {
        return children =>
        {
            var filtered = new List<object?>();
            foreach (var (i, toExpand) in toInclude)
            {
                if (toExpand)
                {
                    var sub = ((Tree)children[i]!).Children;
                    if (filtered.Count > 0) filtered.AddRange(sub);
                    else filtered = sub; // Optimize for left-recursion
                }
                else
                {
                    filtered.Add(children[i]);
                }
            }
            return nodeBuilder(filtered);
        };
    }

    private static bool ShouldExpand(Symbol sym) => !sym.IsTerm && sym.Name.StartsWith('_');

    public static Func<Func<List<object?>, object?>, Func<List<object?>, object?>>? MaybeCreateChildFilter(
        IReadOnlyList<Symbol> expansion, bool keepAllTokens, bool ambiguous, IReadOnlyList<bool>? emptyIndicesIn)
    {
        // Prepare empty_indices as: How many Nones to insert at each index?
        int[] emptyIndices;
        var hasEmptyIndices = emptyIndicesIn is { Count: > 0 };
        if (hasEmptyIndices)
        {
            var counts = new List<int>();
            var ones = 0;
            foreach (var b in emptyIndicesIn!)
            {
                if (b) ones++;
                else
                {
                    counts.Add(ones);
                    ones = 0;
                }
            }
            counts.Add(ones);
            emptyIndices = counts.ToArray();
            if (emptyIndices.Length != expansion.Count + 1) throw new InvalidOperationException("Bad empty indices");
        }
        else
        {
            emptyIndices = new int[expansion.Count + 1];
        }

        var toInclude = new List<(int, bool, int)>();
        var nonesToAdd = 0;
        for (var i = 0; i < expansion.Count; i++)
        {
            var sym = expansion[i];
            nonesToAdd += emptyIndices[i];
            if (keepAllTokens || !(sym.IsTerm && ((Terminal)sym).FilterOut))
            {
                toInclude.Add((i, ShouldExpand(sym), nonesToAdd));
                nonesToAdd = 0;
            }
        }

        nonesToAdd += emptyIndices[expansion.Count];

        if (hasEmptyIndices || toInclude.Count < expansion.Count || toInclude.Any(x => x.Item2))
        {
            var finalNones = nonesToAdd;
            if (hasEmptyIndices || ambiguous)
            {
                return ambiguous
                    ? nb => ChildFilter(toInclude, finalNones, nb)
                    : nb => ChildFilterLalr(toInclude, finalNones, nb);
            }
            // LALR without placeholders
            var simple = toInclude.Select(x => (x.Item1, x.Item2)).ToList();
            return nb => ChildFilterLalrNoPlaceholders(simple, nb);
        }
        return null;
    }

    /// <summary>
    /// Deal with the case where we're expanding children ('_rule') into a parent but the children are ambiguous,
    /// i.e. (parent->_ambig->_expand_this_rule). In this case, make the parent itself ambiguous with as many copies
    /// as there are ambiguous children, and then copy the ambiguous children into the right parents in the right
    /// places, essentially shifting the ambiguity up the tree.
    /// </summary>
    public static Func<List<object?>, object?> AmbiguousExpander(IReadOnlyList<int> toExpand, Func<string, List<object?>, Tree> treeClass, Func<List<object?>, object?> nodeBuilder)
    {
        return children =>
        {
            var ambiguous = new List<int>();
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is Tree { Data: "_ambig" } child)
                {
                    if (toExpand.Contains(i)) ambiguous.Add(i);
                    child.ExpandKidsByData("_ambig");
                }
            }

            if (ambiguous.Count == 0) return nodeBuilder(children);

            var expand = children.Select((child, i) => ambiguous.Contains(i)
                ? (IReadOnlyList<object?>)((Tree)child!).Children
                : new[] { child }).ToList();
            var combos = Helpers.CombineAlternatives(expand);
            return treeClass("_ambig", combos.Select(f => nodeBuilder(f)).ToList());
        };
    }

    public static Func<Func<List<object?>, object?>, Func<List<object?>, object?>>? MaybeCreateAmbiguousExpander(
        Func<string, List<object?>, Tree> treeClass, IReadOnlyList<Symbol> expansion, bool keepAllTokens)
    {
        var toExpand = new List<int>();
        for (var i = 0; i < expansion.Count; i++)
        {
            var sym = expansion[i];
            if (keepAllTokens || (!(sym.IsTerm && ((Terminal)sym).FilterOut) && ShouldExpand(sym))) toExpand.Add(i);
        }
        if (toExpand.Count > 0) return nb => AmbiguousExpander(toExpand, treeClass, nb);
        return null;
    }

    /// <summary>Propagate ambiguous intermediate nodes and their derivations up to the current rule.</summary>
    public static Func<List<object?>, object?> AmbiguousIntermediateExpander(Func<string, List<object?>, Tree> treeClass, Func<List<object?>, object?> nodeBuilder)
    {
        List<Tree>? CollapseIambig(List<object?> children)
        {
            // Due to the structure of the SPPF, an '_iambig' node can only appear as the first child
            if (children.Count > 0 && children[0] is Tree { Data: "_iambig" } iambigNode)
            {
                var result = new List<Tree>();
                foreach (var grandchild in iambigNode.Children.Cast<Tree>())
                {
                    var collapsed = CollapseIambig(grandchild.Children);
                    if (collapsed is { Count: > 0 })
                    {
                        foreach (var child in collapsed) child.Children.AddRange(children.Skip(1));
                        result.AddRange(collapsed);
                    }
                    else
                    {
                        var newTree = treeClass("_inter", grandchild.Children.Concat(children.Skip(1)).ToList());
                        result.Add(newTree);
                    }
                }
                return result;
            }
            return null;
        }

        return children =>
        {
            var collapsed = CollapseIambig(children);
            if (collapsed is { Count: > 0 })
            {
                var processedNodes = collapsed.Select(c => nodeBuilder(c.Children)).ToList();
                return treeClass("_ambig", processedNodes);
            }
            return nodeBuilder(children);
        };
    }
}

/// <summary>Builds the callbacks that create (and shape) the parse tree for each rule.</summary>
internal sealed class ParseTreeBuilder
{
    private readonly List<(Rule Rule, List<Func<Func<List<object?>, object?>, Func<List<object?>, object?>>> Chain)> _ruleBuilders = new();

    public ParseTreeBuilder(IReadOnlyList<Rule> rules, Func<string, List<object?>, Tree> treeClass, bool propagatePositions = false,
        Func<object?, bool>? propagateFilter = null, bool ambiguous = false, bool maybePlaceholders = false)
    {
        TreeClass = treeClass;
        Ambiguous = ambiguous;
        MaybePlaceholders = maybePlaceholders;

        foreach (var rule in rules)
        {
            var options = rule.Options;
            var keepAllTokens = options.KeepAllTokens;
            var expandSingleChild = options.Expand1;

            var chain = new List<Func<Func<List<object?>, object?>, Func<List<object?>, object?>>>();
            if (expandSingleChild && rule.Alias is null) chain.Add(TreeBuilderWrappers.ExpandSingleChild);
            var cf = TreeBuilderWrappers.MaybeCreateChildFilter(rule.Expansion, keepAllTokens, ambiguous, maybePlaceholders ? options.EmptyIndices : null);
            if (cf is not null) chain.Add(cf);
            if (propagatePositions) chain.Add(nb => TreeBuilderWrappers.PropagatePositions(nb, propagateFilter));
            if (ambiguous)
            {
                var ae = TreeBuilderWrappers.MaybeCreateAmbiguousExpander(treeClass, rule.Expansion, keepAllTokens);
                if (ae is not null) chain.Add(ae);
                chain.Add(nb => TreeBuilderWrappers.AmbiguousIntermediateExpander(treeClass, nb));
            }

            _ruleBuilders.Add((rule, chain));
        }
    }

    public Func<string, List<object?>, Tree> TreeClass { get; }

    public bool Ambiguous { get; }

    public bool MaybePlaceholders { get; }

    public Dictionary<Rule, Func<List<object?>, object?>> CreateCallback(Transformer? transformer = null)
    {
        var callbacks = new Dictionary<Rule, Func<List<object?>, object?>>();

        foreach (var (rule, chain) in _ruleBuilders)
        {
            var userCallbackName = rule.Alias ?? rule.Options.TemplateSource ?? rule.Origin.Name;
            Func<List<object?>, object?> f;
            if (transformer is not null && transformer.TryGetRuleMethod(userCallbackName, out var method, out var needsMeta))
            {
                if (needsMeta)
                {
                    throw new NotSupportedException("Meta args not supported for internal transformer; use YourTransformer().Transform(parser.Parse()) instead");
                }
                var name = userCallbackName;
                if (transformer.IsInPlace)
                {
                    f = children =>
                    {
                        var tree = new Tree(name, children);
                        return method!(name, tree.Children, null);
                    };
                }
                else
                {
                    f = children => method!(name, children, null);
                }
            }
            else if (transformer is not null)
            {
                var name = userCallbackName;
                f = children => transformer.Default(name, children, null);
            }
            else
            {
                var name = userCallbackName;
                var treeClass = TreeClass;
                f = children => treeClass(name, children);
            }

            foreach (var w in chain) f = w(f);

            if (callbacks.ContainsKey(rule)) throw new GrammarException($"Rule '{rule}' already exists");
            callbacks[rule] = f;
        }

        return callbacks;
    }
}
