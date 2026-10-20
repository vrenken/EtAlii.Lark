namespace EtAlii.Lark;

/// <summary>
/// When <see cref="Value"/> is returned from a transformer callback, that node is discarded and won't appear in the parent.
/// </summary>
/// <remarks>This feature is disabled when the transformer is embedded in the parser (tree-less LALR mode).</remarks>
public sealed class Discard
{
    public static readonly Discard Value = new();

    private Discard() { }

    public override string ToString() => "lark.visitors.Discard";
}

/// <summary>Something that can transform a tree into a value.</summary>
public interface ITransformer
{
    object? Transform(Tree tree);
}

/// <summary>A rule callback as used by embedded (tree-less) transformers.</summary>
internal delegate object? RuleCallback(List<object?> children);

/// <summary>
/// Transformers work bottom-up (or depth-first), starting with visiting the leaves and working their way up until
/// ending at the root of the tree.
/// </summary>
/// <remarks>
/// <para>
/// For each node visited, the transformer calls the method whose name matches the node's <see cref="Tree.Data"/>
/// (or the PascalCase variant of it, or a method marked with <see cref="RuleAttribute"/>), and uses the returned
/// value to replace the node. Methods for tokens are matched by terminal name (or <see cref="TerminalAttribute"/>)
/// and receive the <see cref="Token"/>.
/// </para>
/// <para>
/// If no method matches, <see cref="Default"/> is called, which by default creates a copy of the node.
/// To discard a node, return <see cref="Discard.Value"/>.
/// </para>
/// </remarks>
public class Transformer : ITransformer
{
    public Transformer(bool visitTokens = true)
    {
        VisitTokens = visitTokens;
    }

    /// <summary>Should the transformer visit tokens in addition to rules.</summary>
    public bool VisitTokens { get; set; }

    /// <summary>
    /// Default function that is called if there is no method matching <paramref name="data"/>.
    /// Defaults to creating a new copy of the tree node.
    /// </summary>
    protected internal virtual object? Default(string data, List<object?> children, Meta? meta) => new Tree(data, children, meta);

    /// <summary>Default function that is called if there is no method matching the token's type. Defaults to returning the token as-is.</summary>
    protected internal virtual object? DefaultToken(Token token) => token;

    /// <summary>
    /// Override to supply callbacks dynamically. The default implementation binds methods by reflection.
    /// </summary>
    protected internal virtual bool TryGetRuleMethod(string name, out Func<string, List<object?>, Meta?, object?>? callback, out bool needsMeta)
    {
        var bound = CallbackBinder.FindRule(GetType(), name);
        if (bound is null)
        {
            callback = null;
            needsMeta = false;
            return false;
        }
        needsMeta = bound.NeedsMeta;
        callback = (data, children, meta) => bound.InvokeRule(this, data, children, meta);
        return true;
    }

    /// <summary>Override to supply token callbacks dynamically. The default implementation binds methods by reflection.</summary>
    protected internal virtual bool TryGetTokenMethod(string name, out Func<Token, object?>? callback)
    {
        var bound = CallbackBinder.FindTerminal(GetType(), name);
        if (bound is null)
        {
            callback = null;
            return false;
        }
        callback = token => bound.InvokeToken(this, token);
        return true;
    }

    /// <summary>True for in-place transformers: embedded callbacks receive a fresh tree.</summary>
    internal virtual bool IsInPlace => false;

    internal object? CallUserFunc(Tree tree, List<object?>? newChildren = null)
    {
        var children = newChildren ?? tree.Children;
        if (!TryGetRuleMethod(tree.Data, out var f, out _))
        {
            return Default(tree.Data, children, tree.Meta);
        }
        try
        {
            return f!(tree.Data, children, tree.Meta);
        }
        catch (GrammarException)
        {
            throw;
        }
        catch (VisitException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new VisitException(tree.Data, tree, e);
        }
    }

    internal object? CallUserFuncToken(Token token)
    {
        if (!TryGetTokenMethod(token.Type, out var f))
        {
            return DefaultToken(token);
        }
        try
        {
            return f!(token);
        }
        catch (GrammarException)
        {
            throw;
        }
        catch (VisitException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new VisitException(token.Type, token, e);
        }
    }

    internal IEnumerable<object?> TransformChildren(IEnumerable<object?> children)
    {
        foreach (var c in children)
        {
            object? res;
            if (c is Tree t) res = TransformTree(t);
            else if (VisitTokens && c is Token tok) res = CallUserFuncToken(tok);
            else res = c;

            if (!ReferenceEquals(res, Discard.Value)) yield return res;
        }
    }

    internal virtual object? TransformTree(Tree tree)
    {
        var children = TransformChildren(tree.Children).ToList();
        return CallUserFunc(tree, children);
    }

    /// <summary>Transform the given tree, and return the final result.</summary>
    public virtual object? Transform(Tree tree)
    {
        var res = TransformChildren(new object?[] { tree }).ToList();
        if (res.Count == 0) return null;
        return res[0];
    }

    /// <summary>Transform the given tree and cast the result.</summary>
    public T Transform<T>(Tree tree) => (T)Transform(tree)!;

    /// <summary>Chain two transformers together, returning a new transformer.</summary>
    public static TransformerChain operator *(Transformer a, ITransformer b) => new(a, b);

    /// <summary>Chain another transformer after this one.</summary>
    public TransformerChain Then(ITransformer other) => new(this, other);

    /// <summary>
    /// Merge a collection of transformers into the base transformer, each into its own 'namespace'.
    /// Callbacks of a merged transformer are reachable as <c>prefix__name</c>.
    /// </summary>
    public static Transformer Merge(Transformer? baseTransformer, params (string Prefix, Transformer Transformer)[] transformers)
    {
        var merged = new MergedTransformer(baseTransformer ?? new Transformer());
        foreach (var (prefix, transformer) in transformers)
        {
            foreach (var name in CallbackBinder.BoundNames(transformer.GetType()).Concat(transformer.ExtraNames()))
            {
                var prefixed = prefix + "__" + name;
                if (merged.HasCallback(prefixed))
                {
                    throw new InvalidOperationException($"Cannot merge: method '{prefixed}' appears more than once");
                }
            }
            merged.Add(prefix, transformer);
        }
        return merged;
    }

    internal virtual IEnumerable<string> ExtraNames() => Enumerable.Empty<string>();

    /// <summary>Creates a transformer from delegates, configured fluently.</summary>
    public static DelegateTransformer Create(bool visitTokens = true) => new(visitTokens);
}

/// <summary>A chain of transformers, applied one after the other.</summary>
public sealed class TransformerChain : ITransformer
{
    public TransformerChain(params ITransformer[] transformers)
    {
        Transformers = transformers;
    }

    public IReadOnlyList<ITransformer> Transformers { get; }

    public object? Transform(Tree tree)
    {
        object? result = tree;
        foreach (var t in Transformers)
        {
            result = t.Transform((Tree)result!);
        }
        return result;
    }

    public static TransformerChain operator *(TransformerChain a, ITransformer b) => new(a.Transformers.Append(b).ToArray());

    public TransformerChain Then(ITransformer other) => new(Transformers.Append(other).ToArray());
}

/// <summary>Same as <see cref="Transformer"/>, but non-recursive, and changes the tree in-place instead of returning new instances.</summary>
public class InPlaceTransformer : Transformer
{
    public InPlaceTransformer(bool visitTokens = true) : base(visitTokens) { }

    internal override bool IsInPlace => true;

    internal override object? TransformTree(Tree tree) => CallUserFunc(tree);

    public override object? Transform(Tree tree)
    {
        foreach (var subtree in tree.IterSubtrees())
        {
            subtree.Children = TransformChildren(subtree.Children).ToList();
        }
        return TransformTree(tree);
    }
}

/// <summary>Same as <see cref="Transformer"/>, recursive, but changes the tree in-place instead of returning new instances.</summary>
public class InPlaceRecursiveTransformer : Transformer
{
    public InPlaceRecursiveTransformer(bool visitTokens = true) : base(visitTokens) { }

    internal override bool IsInPlace => true;

    internal override object? TransformTree(Tree tree)
    {
        tree.Children = TransformChildren(tree.Children).ToList();
        return CallUserFunc(tree);
    }
}

/// <summary>Same as <see cref="Transformer"/> but non-recursive. Like Transformer, it doesn't change the original tree. Useful for huge trees.</summary>
public class NonRecursiveTransformer : Transformer
{
    public NonRecursiveTransformer(bool visitTokens = true) : base(visitTokens) { }

    public override object? Transform(Tree tree)
    {
        // Tree to postfix
        var revPostfix = new List<object?>();
        var q = new Stack<object?>();
        q.Push(tree);
        while (q.Count > 0)
        {
            var t = q.Pop();
            revPostfix.Add(t);
            if (t is Tree tt)
            {
                foreach (var c in tt.Children) q.Push(c);
            }
        }

        // Postfix to tree
        var stack = new List<object?>();
        for (var idx = revPostfix.Count - 1; idx >= 0; idx--)
        {
            var x = revPostfix[idx];
            if (x is Tree xt)
            {
                var size = xt.Children.Count;
                List<object?> args;
                if (size > 0)
                {
                    // Note: discarded children reduce the stack, mirroring upstream behaviour
                    var take = Math.Min(size, stack.Count);
                    args = stack.GetRange(stack.Count - take, take);
                    stack.RemoveRange(stack.Count - take, take);
                }
                else
                {
                    args = new List<object?>();
                }
                var res = CallUserFunc(xt, args);
                if (!ReferenceEquals(res, Discard.Value)) stack.Add(res);
            }
            else if (VisitTokens && x is Token tok)
            {
                var res = CallUserFuncToken(tok);
                if (!ReferenceEquals(res, Discard.Value)) stack.Add(res);
            }
            else
            {
                stack.Add(x);
            }
        }

        if (stack.Count != 1) throw new InvalidOperationException("Expected a single tree to remain");
        return stack[0];
    }
}

/// <summary>A transformer built from delegates.</summary>
public sealed class DelegateTransformer : Transformer
{
    private readonly Dictionary<string, Func<string, List<object?>, Meta?, object?>> _rules = new();
    private readonly HashSet<string> _metaRules = new();
    private readonly Dictionary<string, Func<Token, object?>> _tokens = new();
    private Func<string, List<object?>, Meta?, object?>? _default;
    private Func<Token, object?>? _defaultToken;

    public DelegateTransformer(bool visitTokens = true) : base(visitTokens) { }

    /// <summary>Register a callback that receives the children of the rule.</summary>
    public DelegateTransformer Rule(string name, Func<List<object?>, object?> callback)
    {
        _rules[name] = (_, children, _) => callback(children);
        return this;
    }

    /// <summary>Register a callback that receives the meta and the children of the rule.</summary>
    public DelegateTransformer Rule(string name, Func<Meta?, List<object?>, object?> callback)
    {
        _rules[name] = (_, children, meta) => callback(meta, children);
        _metaRules.Add(name);
        return this;
    }

    /// <summary>Register a callback that receives the whole tree.</summary>
    public DelegateTransformer Rule(string name, Func<Tree, object?> callback)
    {
        _rules[name] = (data, children, meta) => callback(new Tree(data, children, meta));
        return this;
    }

    /// <summary>Register a callback for a terminal.</summary>
    public DelegateTransformer Terminal(string name, Func<Token, object?> callback)
    {
        _tokens[name] = callback;
        return this;
    }

    /// <summary>Override the default behaviour for rules without a callback.</summary>
    public DelegateTransformer Default(Func<string, List<object?>, Meta?, object?> callback)
    {
        _default = callback;
        return this;
    }

    /// <summary>Override the default behaviour for terminals without a callback.</summary>
    public DelegateTransformer DefaultToken(Func<Token, object?> callback)
    {
        _defaultToken = callback;
        return this;
    }

    protected internal override object? Default(string data, List<object?> children, Meta? meta) =>
        _default is not null ? _default(data, children, meta) : base.Default(data, children, meta);

    protected internal override object? DefaultToken(Token token) =>
        _defaultToken is not null ? _defaultToken(token) : base.DefaultToken(token);

    protected internal override bool TryGetRuleMethod(string name, out Func<string, List<object?>, Meta?, object?>? callback, out bool needsMeta)
    {
        needsMeta = _metaRules.Contains(name);
        return _rules.TryGetValue(name, out callback);
    }

    protected internal override bool TryGetTokenMethod(string name, out Func<Token, object?>? callback) => _tokens.TryGetValue(name, out callback);

    internal override IEnumerable<string> ExtraNames() => _rules.Keys.Concat(_tokens.Keys);
}

internal sealed class MergedTransformer : Transformer
{
    private readonly Transformer _base;
    private readonly List<(string Prefix, Transformer Transformer)> _merged = new();

    public MergedTransformer(Transformer baseTransformer) : base(baseTransformer.VisitTokens)
    {
        _base = baseTransformer;
    }

    public void Add(string prefix, Transformer transformer) => _merged.Add((prefix, transformer));

    public bool HasCallback(string name) =>
        _base.TryGetRuleMethod(name, out _, out _) || _base.TryGetTokenMethod(name, out _) ||
        TryGetRuleMethod(name, out _, out _) || TryGetTokenMethod(name, out _);

    protected internal override object? Default(string data, List<object?> children, Meta? meta) => _base.Default(data, children, meta);

    protected internal override object? DefaultToken(Token token) => _base.DefaultToken(token);

    protected internal override bool TryGetRuleMethod(string name, out Func<string, List<object?>, Meta?, object?>? callback, out bool needsMeta)
    {
        if (_base.TryGetRuleMethod(name, out callback, out needsMeta)) return true;
        foreach (var (prefix, t) in _merged)
        {
            if (name.StartsWith(prefix + "__", StringComparison.Ordinal) && t.TryGetRuleMethod(name[(prefix.Length + 2)..], out callback, out needsMeta)) return true;
        }
        callback = null;
        needsMeta = false;
        return false;
    }

    protected internal override bool TryGetTokenMethod(string name, out Func<Token, object?>? callback)
    {
        if (_base.TryGetTokenMethod(name, out callback)) return true;
        foreach (var (prefix, t) in _merged)
        {
            if (name.StartsWith(prefix + "__", StringComparison.Ordinal) && t.TryGetTokenMethod(name[(prefix.Length + 2)..], out callback)) return true;
        }
        callback = null;
        return false;
    }
}

/// <summary>
/// Transforms a tree that contains any number of <c>_ambig</c> nodes into a list of trees, each one containing an
/// unambiguous tree. The length of the resulting list is the product of the length of all <c>_ambig</c> nodes.
/// </summary>
public sealed class CollapseAmbiguities : Transformer
{
    protected internal override bool TryGetRuleMethod(string name, out Func<string, List<object?>, Meta?, object?>? callback, out bool needsMeta)
    {
        needsMeta = false;
        if (name == "_ambig")
        {
            callback = (_, options, _) => options.SelectMany(o => (List<object?>)o!).ToList();
            return true;
        }
        callback = null;
        return false;
    }

    protected internal override object? Default(string data, List<object?> children, Meta? meta)
    {
        var lists = children.Select(c => (IReadOnlyList<object?>)(List<object?>)c!).ToList();
        return Utilities.Helpers.CombineAlternatives(lists).Select(c => (object?)new Tree(data, c, meta)).ToList();
    }

    protected internal override object? DefaultToken(Token token) => new List<object?> { token };

    /// <summary>Collapses the ambiguities and returns the list of unambiguous trees.</summary>
    public new List<Tree> Transform(Tree tree) => ((List<object?>)base.Transform(tree)!).Cast<Tree>().ToList();
}
