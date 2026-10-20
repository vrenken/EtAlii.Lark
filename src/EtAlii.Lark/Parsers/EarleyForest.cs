namespace EtAlii.Lark.Parsers;

using EtAlii.Lark.Utilities;

/// <summary>The LR(0) item (rule + position) that identifies an intermediate node in the forest.</summary>
public readonly record struct Lr0Item(Rule Rule, int Ptr)
{
    public override string ToString()
    {
        var before = Rule.Expansion.Take(Ptr).Select(x => x.Name);
        var after = Rule.Expansion.Skip(Ptr).Select(x => x.Name);
        return $"{Rule.Origin.Name} ::= {string.Join(" ", before)}* {string.Join(" ", after)}";
    }
}

/// <summary>Base class of the nodes of the Shared Packed Parse Forest (SPPF) produced by the Earley parser.</summary>
public abstract class ForestNode
{
    /// <summary>The priority of the node, as computed by <see cref="ForestSumVisitor"/>.</summary>
    public double Priority { get; set; } = double.NegativeInfinity;
}

/// <summary>
/// A Symbol Node represents a symbol (or an intermediate LR0 item). Its children are always Packed Nodes, each
/// representing a single derivation of a production. A Symbol Node with a single child is unambiguous.
/// </summary>
public sealed class SymbolNode : ForestNode
{
    private readonly OrderedSet<PackedNode> _children = new();

    public SymbolNode(object s, int start, int end)
    {
        S = s;
        Start = start;
        End = end;
        IsIntermediate = s is Lr0Item;
    }

    /// <summary>A <see cref="NonTerminal"/>, or an <see cref="Lr0Item"/> for intermediate nodes.</summary>
    public object S { get; }

    /// <summary>The index of the start of the matched substring (inclusive).</summary>
    public int Start { get; }

    /// <summary>The index of the end of the matched substring (exclusive).</summary>
    public int End { get; }

    public bool IsIntermediate { get; }

    public void AddFamily(object lr0, Rule rule, int start, ForestNode? left, ForestNode? right)
    {
        _children.Add(new PackedNode(this, lr0, rule, start, left, right));
    }

    /// <summary>True if this node is ambiguous.</summary>
    public bool IsAmbiguous => _children.Count > 1;

    /// <summary>The children of this node, sorted from greatest to least priority.</summary>
    public List<PackedNode> Children => _children.OrderBy(c => c.IsEmpty).ThenBy(c => -c.Priority).ThenBy(c => c.Rule.Order).ToList();

    internal IEnumerable<PackedNode> UnsortedChildren => _children;

    public override string ToString()
    {
        var symbol = S is Lr0Item item ? item.ToString() : ((Symbol)S).Name;
        return $"({symbol}, {Start}, {End}, {Priority})";
    }
}

/// <summary>A Packed Node represents a single derivation in a symbol node.</summary>
public sealed class PackedNode : ForestNode, IEquatable<PackedNode>
{
    private readonly int _hash;

    public PackedNode(SymbolNode parent, object s, Rule rule, int start, ForestNode? left, ForestNode? right)
    {
        Parent = parent;
        S = s;
        Start = start;
        Rule = rule;
        Left = left;
        Right = right;
        _hash = HashCode.Combine(left, right);
    }

    public SymbolNode Parent { get; }

    public object S { get; }

    public Rule Rule { get; }

    public int Start { get; }

    public ForestNode? Left { get; }

    public ForestNode? Right { get; }

    public bool IsEmpty => Left is null && Right is null;

    /// <summary>The children of this node (left and right, when present).</summary>
    public List<ForestNode> Children
    {
        get
        {
            var l = new List<ForestNode>(2);
            if (Left is not null) l.Add(Left);
            if (Right is not null) l.Add(Right);
            return l;
        }
    }

    public bool Equals(PackedNode? other) =>
        other is not null && (ReferenceEquals(this, other) || (Equals(Left, other.Left) && Equals(Right, other.Right)));

    public override bool Equals(object? obj) => obj is PackedNode p && Equals(p);

    public override int GetHashCode() => _hash;

    public override string ToString()
    {
        var symbol = S is Lr0Item item ? item.ToString() : ((Symbol)S).Name;
        return $"({symbol}, {Start}, {Priority}, {Rule.Order})";
    }
}

/// <summary>A Token Node represents a matched terminal and is always a leaf node.</summary>
public sealed class TokenNode : ForestNode, IEquatable<TokenNode>
{
    private readonly int _hash;

    public TokenNode(object token, TerminalDef? term, double? priority = null)
    {
        Token = token;
        Term = term;
        Priority = priority ?? term?.Priority ?? 0;
        _hash = token.GetHashCode();
    }

    /// <summary>The token (usually a <see cref="EtAlii.Lark.Token"/>; a <see cref="Tree"/> when matching trees).</summary>
    public object Token { get; }

    public TerminalDef? Term { get; }

    public bool Equals(TokenNode? other) => other is not null && (ReferenceEquals(this, other) || Tree.ValueEquals(Token, other.Token));

    public override bool Equals(object? obj) => obj is TokenNode t && Equals(t);

    public override int GetHashCode() => _hash;

    public override string ToString() => Token is Token t ? t.Repr() : Token.ToString() ?? "";
}

/// <summary>
/// An abstract base class for building forest visitors. Performs a controllable depth-first walk of an SPPF.
/// The visitor will not enter cycles and will backtrack if one is encountered.
/// </summary>
public abstract class ForestVisitor
{
    protected ForestVisitor(bool singleVisit = false)
    {
        SingleVisit = singleVisit;
    }

    /// <summary>If true, non-token nodes are only visited once.</summary>
    public bool SingleVisit { get; }

    /// <summary>Called when a token is visited. Token nodes are always leaves.</summary>
    public virtual void VisitTokenNode(object token) { }

    /// <summary>Called when a symbol node is visited. Returned nodes are scheduled to be visited.</summary>
    public virtual IEnumerable<ForestNode?>? VisitSymbolNodeIn(SymbolNode node) => null;

    /// <summary>Called after all nodes returned from a corresponding <see cref="VisitSymbolNodeIn"/> call have been visited.</summary>
    public virtual void VisitSymbolNodeOut(SymbolNode node) { }

    /// <summary>Called when an intermediate node is visited. Defaults to <see cref="VisitSymbolNodeIn"/>.</summary>
    public virtual IEnumerable<ForestNode?>? VisitIntermediateNodeIn(SymbolNode node) => VisitSymbolNodeIn(node);

    /// <summary>Called after the children of an intermediate node have been visited. Defaults to <see cref="VisitSymbolNodeOut"/>.</summary>
    public virtual void VisitIntermediateNodeOut(SymbolNode node) => VisitSymbolNodeOut(node);

    /// <summary>Called when a packed node is visited. Returned nodes are scheduled to be visited.</summary>
    public virtual IEnumerable<ForestNode?>? VisitPackedNodeIn(PackedNode node) => null;

    /// <summary>Called after all nodes returned from a corresponding <see cref="VisitPackedNodeIn"/> call have been visited.</summary>
    public virtual void VisitPackedNodeOut(PackedNode node) { }

    /// <summary>Called when a cycle is encountered.</summary>
    public virtual void OnCycle(ForestNode node, IReadOnlyList<ForestNode> path) { }

    /// <summary>Returns the slice of <paramref name="path"/> that makes up the cycle.</summary>
    public IReadOnlyList<ForestNode> GetCycleInPath(ForestNode node, IReadOnlyList<ForestNode> path)
    {
        var index = path.Count - 1;
        while (!ReferenceEquals(path[index], node)) index--;
        return path.Skip(index).ToList();
    }

    public virtual void Visit(ForestNode root)
    {
        var visiting = new HashSet<ForestNode>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<ForestNode>(ReferenceEqualityComparer.Instance);
        var path = new List<ForestNode>();
        var inputStack = new List<object> { root };

        while (inputStack.Count > 0)
        {
            var current = inputStack[^1];
            if (current is IEnumerator<ForestNode?> iterator)
            {
                if (!iterator.MoveNext())
                {
                    inputStack.RemoveAt(inputStack.Count - 1);
                    continue;
                }
                var nextNode = iterator.Current;
                if (nextNode is null) continue;
                if (visiting.Contains(nextNode))
                {
                    OnCycle(nextNode, path);
                    continue;
                }
                inputStack.Add(nextNode);
                continue;
            }

            if (current is TokenNode tokenNode)
            {
                VisitTokenNode(tokenNode.Token);
                inputStack.RemoveAt(inputStack.Count - 1);
                continue;
            }

            var node = (ForestNode)current;
            if (visiting.Contains(node))
            {
                if (node is PackedNode pn) VisitPackedNodeOut(pn);
                else if (((SymbolNode)node).IsIntermediate) VisitIntermediateNodeOut((SymbolNode)node);
                else VisitSymbolNodeOut((SymbolNode)node);
                inputStack.RemoveAt(inputStack.Count - 1);
                path.RemoveAt(path.Count - 1);
                visiting.Remove(node);
                visited.Add(node);
            }
            else if (SingleVisit && visited.Contains(node))
            {
                inputStack.RemoveAt(inputStack.Count - 1);
            }
            else
            {
                visiting.Add(node);
                path.Add(node);
                IEnumerable<ForestNode?>? next;
                if (node is PackedNode pn) next = VisitPackedNodeIn(pn);
                else if (((SymbolNode)node).IsIntermediate) next = VisitIntermediateNodeIn((SymbolNode)node);
                else next = VisitSymbolNodeIn((SymbolNode)node);
                if (next is null) continue;
                inputStack.Add(next.GetEnumerator());
            }
        }
    }
}

/// <summary>
/// The base class for a bottom-up forest transformation. Most users will want to use
/// <see cref="TreeForestTransformer"/> instead as it has a friendlier interface and covers most use cases.
/// </summary>
public abstract class ForestTransformer : ForestVisitor
{
    private static readonly object ResultKey = new();
    private readonly Dictionary<object, List<object?>> _data = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<object> _nodeStack = new();

    protected ForestTransformer() : base(false) { }

    /// <summary>Perform a transformation on an SPPF.</summary>
    public virtual object? Transform(ForestNode root)
    {
        _nodeStack.Push(ResultKey);
        _data[ResultKey] = new List<object?>();
        Visit(root);
        var result = _data[ResultKey];
        _data.Remove(ResultKey);
        _nodeStack.Clear();
        return result.Count > 0 ? result[0] : null;
    }

    /// <summary>Transform a symbol node.</summary>
    public virtual object? TransformSymbolNode(SymbolNode node, List<object?> data) => node;

    /// <summary>Transform an intermediate node.</summary>
    public virtual object? TransformIntermediateNode(SymbolNode node, List<object?> data) => node;

    /// <summary>Transform a packed node.</summary>
    public virtual object? TransformPackedNode(PackedNode node, List<object?> data) => node;

    /// <summary>Transform a token.</summary>
    public virtual object? TransformTokenNode(object token) => token;

    public override IEnumerable<ForestNode?>? VisitSymbolNodeIn(SymbolNode node)
    {
        _nodeStack.Push(node);
        _data[node] = new List<object?>();
        return node.Children;
    }

    public override IEnumerable<ForestNode?>? VisitPackedNodeIn(PackedNode node)
    {
        _nodeStack.Push(node);
        _data[node] = new List<object?>();
        return node.Children;
    }

    public override void VisitTokenNode(object token)
    {
        var transformed = TransformTokenNode(token);
        if (!ReferenceEquals(transformed, Discard.Value)) _data[_nodeStack.Peek()].Add(transformed);
    }

    private void VisitNodeOutHelper<TNode>(TNode node, Func<TNode, List<object?>, object?> method) where TNode : ForestNode
    {
        _nodeStack.Pop();
        var transformed = method(node, _data[node]);
        if (!ReferenceEquals(transformed, Discard.Value)) _data[_nodeStack.Peek()].Add(transformed);
        _data.Remove(node);
    }

    public override void VisitSymbolNodeOut(SymbolNode node) => VisitNodeOutHelper(node, TransformSymbolNode);

    public override void VisitIntermediateNodeOut(SymbolNode node) => VisitNodeOutHelper(node, TransformIntermediateNode);

    public override void VisitPackedNodeOut(PackedNode node) => VisitNodeOutHelper(node, TransformPackedNode);
}

/// <summary>
/// A visitor for prioritizing ambiguous parts of the forest. Walks the forest and cascades priorities upwards from the leaves.
/// </summary>
public sealed class ForestSumVisitor : ForestVisitor
{
    public ForestSumVisitor() : base(singleVisit: true) { }

    public override IEnumerable<ForestNode?>? VisitPackedNodeIn(PackedNode node) => new[] { node.Left, node.Right };

    public override IEnumerable<ForestNode?>? VisitSymbolNodeIn(SymbolNode node) => node.Children;

    public override void VisitPackedNodeOut(PackedNode node)
    {
        double priority = !node.Parent.IsIntermediate && node.Rule.Options.Priority is { } p && p != 0 ? p : 0;
        priority += node.Right?.Priority ?? 0;
        priority += node.Left?.Priority ?? 0;
        node.Priority = priority;
    }

    public override void VisitSymbolNodeOut(SymbolNode node)
    {
        node.Priority = node.UnsortedChildren.Max(c => c.Priority);
    }
}

/// <summary>
/// Used by the Earley parser when ambiguity is 'resolve' or 'explicit'. Transforms an SPPF into an (ambiguous) parse tree.
/// </summary>
public class ForestToParseTree : ForestTransformer
{
    private readonly Func<string, List<object?>, Tree> _treeFactory;
    private readonly IReadOnlyDictionary<Rule, Func<List<object?>, object?>> _callbacks;
    private readonly ForestVisitor? _prioritizer;
    private readonly bool _useCache;
    private Dictionary<PackedNode, object?> _cache = new(ReferenceEqualityComparer.Instance);
    private bool _onCycleRetreat;
    private ForestNode? _cycleNode;
    private readonly HashSet<ForestNode> _successfulVisits = new(ReferenceEqualityComparer.Instance);

    public ForestToParseTree(Func<string, List<object?>, Tree>? treeFactory = null, IReadOnlyDictionary<Rule, Func<List<object?>, object?>>? callbacks = null,
        ForestVisitor? prioritizer = null, bool resolveAmbiguity = true, bool useCache = true)
    {
        _treeFactory = treeFactory ?? ((data, children) => new Tree(data, children));
        _callbacks = callbacks ?? new Dictionary<Rule, Func<List<object?>, object?>>();
        _prioritizer = prioritizer;
        ResolveAmbiguity = resolveAmbiguity;
        _useCache = useCache;
    }

    protected bool ResolveAmbiguity { get; }

    protected Tree MakeTree(string data, List<object?> children) => _treeFactory(data, children);

    public override void Visit(ForestNode root)
    {
        _prioritizer?.Visit(root);
        base.Visit(root);
        _cache = new Dictionary<PackedNode, object?>(ReferenceEqualityComparer.Instance);
    }

    public override void OnCycle(ForestNode node, IReadOnlyList<ForestNode> path)
    {
        LarkLog.Debug($"Cycle encountered in the SPPF at node: {node}. As infinite ambiguities cannot be represented in a tree, this family of derivations will be discarded.");
        _cycleNode = node;
        _onCycleRetreat = true;
    }

    private bool CheckCycle(ForestNode node)
    {
        if (_onCycleRetreat)
        {
            if (ReferenceEquals(node, _cycleNode) || _successfulVisits.Contains(node))
            {
                _cycleNode = null;
                _onCycleRetreat = false;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    private static List<object?> CollapseAmbig(List<object?> children)
    {
        var newChildren = new List<object?>();
        foreach (var child in children)
        {
            if (child is Tree { Data: "_ambig" } t) newChildren.AddRange(t.Children);
            else newChildren.Add(child);
        }
        return newChildren;
    }

    /// <summary>Called when transforming the children of symbol nodes.</summary>
    protected virtual object? CallRuleFunc(PackedNode node, List<object?> data) => _callbacks[node.Rule](data);

    /// <summary>Called when transforming a symbol node; data is a list of trees for each derivation.</summary>
    protected virtual object? CallAmbigFunc(SymbolNode node, List<object?> data)
    {
        if (data.Count > 1) return MakeTree("_ambig", data);
        if (data.Count == 1) return data[0];
        return Discard.Value;
    }

    public override object? TransformSymbolNode(SymbolNode node, List<object?> data)
    {
        if (!_successfulVisits.Contains(node)) return Discard.Value;
        if (CheckCycle(node)) return Discard.Value;
        _successfulVisits.Remove(node);
        data = CollapseAmbig(data);
        return CallAmbigFunc(node, data);
    }

    public override object? TransformIntermediateNode(SymbolNode node, List<object?> data)
    {
        if (!_successfulVisits.Contains(node)) return Discard.Value;
        if (CheckCycle(node)) return Discard.Value;
        _successfulVisits.Remove(node);
        if (data.Count > 1)
        {
            var children = data.Select(c => (object?)MakeTree("_inter", (List<object?>)c!)).ToList();
            return MakeTree("_iambig", children);
        }
        return data[0];
    }

    private static readonly object NoData = new();

    public override object? TransformPackedNode(PackedNode node, List<object?> data)
    {
        if (CheckCycle(node)) return Discard.Value;
        if (ResolveAmbiguity && _successfulVisits.Contains(node.Parent)) return Discard.Value;
        if (_useCache && _cache.TryGetValue(node, out var cached)) return cached;

        var children = new List<object?>();
        var left = NoData;
        var right = NoData;
        if (data.Count > 0)
        {
            if (node.Left is not null)
            {
                left = data[0];
                if (data.Count > 1) right = data[1];
            }
            else
            {
                right = data[0];
            }
        }

        if (!ReferenceEquals(left, NoData))
        {
            if (node.Left is SymbolNode { IsIntermediate: true } && left is List<object?> leftList) children.AddRange(leftList);
            else children.Add(left);
        }
        if (!ReferenceEquals(right, NoData)) children.Add(right);

        var transformed = node.Parent.IsIntermediate ? children : CallRuleFunc(node, children);
        if (_useCache) _cache[node] = transformed;
        return transformed;
    }

    public override IEnumerable<ForestNode?>? VisitSymbolNodeIn(SymbolNode node)
    {
        base.VisitSymbolNodeIn(node);
        if (_onCycleRetreat) return null;
        return node.Children;
    }

    public override IEnumerable<ForestNode?>? VisitPackedNodeIn(PackedNode node)
    {
        _onCycleRetreat = false;
        var toVisit = base.VisitPackedNodeIn(node);
        if (!ResolveAmbiguity || !_successfulVisits.Contains(node.Parent))
        {
            if (!_useCache || !_cache.ContainsKey(node)) return toVisit;
        }
        return null;
    }

    public override void VisitPackedNodeOut(PackedNode node)
    {
        base.VisitPackedNodeOut(node);
        if (!_onCycleRetreat) _successfulVisits.Add(node.Parent);
    }
}

/// <summary>Marks a <see cref="TreeForestTransformer"/> method as receiving a list of all transformed derivations of the rule.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HandlesAmbiguityAttribute : Attribute { }

/// <summary>
/// A <see cref="ForestTransformer"/> with a tree <see cref="Transformer"/>-like interface. By default, it constructs a tree.
/// </summary>
/// <remarks>
/// Methods are matched by rule/symbol name (like a <see cref="Transformer"/>). Methods for rules receive a list
/// of the results of the transformations of the rule's children; methods for tokens receive the token.
/// Methods marked with <see cref="HandlesAmbiguityAttribute"/> receive a list of all the transformations of all
/// the derivations of the rule. Tree shaping features such as inlined rules and token filtering are not built
/// into the transformation, and positions are not propagated.
/// </remarks>
public class TreeForestTransformer : ForestToParseTree
{
    public TreeForestTransformer(Func<string, List<object?>, Tree>? treeFactory = null, ForestVisitor? prioritizer = null, bool resolveAmbiguity = true, bool useCache = false)
        : base(treeFactory, null, prioritizer ?? new ForestSumVisitor(), resolveAmbiguity, useCache) { }

    /// <summary>Default operation on a tree. Returns a tree with name with data as children.</summary>
    protected virtual object? Default(string name, List<object?> data) => MakeTree(name, data);

    /// <summary>Default operation on an ambiguous rule. Wraps data in an '_ambig' node if it contains more than one element.</summary>
    protected virtual object? DefaultAmbig(string name, List<object?> data)
    {
        if (data.Count > 1) return MakeTree("_ambig", data);
        if (data.Count == 1) return data[0];
        return Discard.Value;
    }

    /// <summary>Default operation on a token. Returns the token.</summary>
    protected virtual object? DefaultToken(object token) => token;

    public override object? TransformTokenNode(object token)
    {
        if (token is Token t)
        {
            var bound = CallbackBinder.FindTerminal(GetType(), t.Type);
            if (bound is not null) return bound.InvokeToken(this, t);
        }
        return DefaultToken(token);
    }

    protected override object? CallRuleFunc(PackedNode node, List<object?> data)
    {
        var name = node.Rule.Alias ?? node.Rule.Options.TemplateSource ?? node.Rule.Origin.Name;
        var bound = CallbackBinder.FindRule(GetType(), name);
        Func<List<object?>, object?> userFunc;
        if (bound is null || bound.Method.IsDefined(typeof(HandlesAmbiguityAttribute), true))
        {
            userFunc = d => Default(name, d);
        }
        else
        {
            userFunc = d => bound.InvokeRule(this, name, d, null);
        }
        if (!ResolveAmbiguity)
        {
            userFunc = TreeBuilderWrappers.AmbiguousIntermediateExpander((d, c) => MakeTree(d, c), userFunc);
        }
        return userFunc(data);
    }

    protected override object? CallAmbigFunc(SymbolNode node, List<object?> data)
    {
        var name = ((Symbol)node.S).Name;
        var bound = CallbackBinder.FindRule(GetType(), name);
        if (bound is not null && bound.Method.IsDefined(typeof(HandlesAmbiguityAttribute), true))
        {
            return bound.InvokeRule(this, name, data, null);
        }
        return DefaultAmbig(name, data);
    }
}
