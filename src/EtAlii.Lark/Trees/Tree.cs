namespace EtAlii.Lark;

using System.Text;
using EtAlii.Lark.Utilities;

/// <summary>Positional information of a tree node (when <c>propagate_positions</c> is enabled).</summary>
public sealed class Meta : ILarkPositioned
{
    /// <summary>True when no position information was set.</summary>
    public bool Empty { get; set; } = true;

    public int? Line { get; set; }
    public int? Column { get; set; }
    public int? StartPos { get; set; }
    public int? EndLine { get; set; }
    public int? EndColumn { get; set; }
    public int? EndPos { get; set; }

    /// <summary>Like <see cref="Line"/>, but considering all symbols, including those that have been filtered out of the tree.</summary>
    public int? ContainerLine { get; set; }
    public int? ContainerColumn { get; set; }
    public int? ContainerStartPos { get; set; }
    public int? ContainerEndLine { get; set; }
    public int? ContainerEndColumn { get; set; }
    public int? ContainerEndPos { get; set; }

    /// <summary>Used by the reconstructor.</summary>
    public IReadOnlyList<Symbol>? OrigExpansion { get; set; }

    /// <summary>Used by the reconstructor.</summary>
    public bool MatchTree { get; set; }

    public Meta Copy() => (Meta)MemberwiseClone();
}

/// <summary>
/// The main tree class. Holds the name of the rule (<see cref="Data"/>) and the matched sub-rules and tokens (<see cref="Children"/>).
/// Trees can be hashed and compared structurally.
/// </summary>
public class Tree : IEquatable<Tree>
{
    private Meta? _meta;

    public Tree(string data, List<object?>? children = null, Meta? meta = null)
    {
        Data = data;
        Children = children ?? new List<object?>();
        _meta = meta;
    }

    /// <summary>The name of the rule or alias.</summary>
    public string Data { get; set; }

    /// <summary>List of matched sub-rules (<see cref="Tree"/>), terminals (<see cref="Token"/>) and transformed values.</summary>
    public List<object?> Children { get; set; }

    /// <summary>Deconstructs the tree into its data and children, for positional patterns like <c>tree is ("expr", [var left, _, var right])</c>.</summary>
    public void Deconstruct(out string data, out List<object?> children)
    {
        data = Data;
        children = Children;
    }

    /// <summary>Line &amp; column numbers (if propagate_positions is enabled).</summary>
    public Meta Meta
    {
        get => _meta ??= new Meta();
        set => _meta = value;
    }

    internal Meta? RawMeta => _meta;

    /// <summary>Creates a tree from params children; handy in tests and when building trees by hand.</summary>
    public static Tree Of(string data, params object?[] children) => new(data, children.ToList());

    /// <summary>Returns an indented string representation of the tree. Great for debugging.</summary>
    public string Pretty(string indentStr = "  ")
    {
        var sb = new StringBuilder();
        PrettyInto(sb, 0, indentStr);
        return sb.ToString();
    }

    protected virtual string PrettyLabel() => Data;

    private void PrettyInto(StringBuilder sb, int level, string indentStr)
    {
        sb.Append(Repeat(indentStr, level)).Append(PrettyLabel());
        if (Children.Count == 1 && Children[0] is not Tree)
        {
            sb.Append('\t').Append(Children[0]?.ToString() ?? "None").Append('\n');
        }
        else
        {
            sb.Append('\n');
            foreach (var n in Children)
            {
                if (n is Tree t) t.PrettyInto(sb, level + 1, indentStr);
                else sb.Append(Repeat(indentStr, level + 1)).Append(n?.ToString() ?? "None").Append('\n');
            }
        }
    }

    private static string Repeat(string s, int count) => count <= 0 ? "" : string.Concat(Enumerable.Repeat(s, count));

    public override string ToString() => $"Tree({Helpers.Repr(Data)}, [{string.Join(", ", Children.Select(ChildRepr))}])";

    internal static string ChildRepr(object? c) => c switch
    {
        Token t => t.Repr(),
        _ => Helpers.Repr(c),
    };

    /// <summary>
    /// Depth-first iteration (bottom-up). Iterates over all the subtrees, never returning to the same node twice
    /// (Lark's parse-tree is actually a DAG).
    /// </summary>
    public IEnumerable<Tree> IterSubtrees()
    {
        var queue = new List<Tree> { this };
        var seen = new HashSet<Tree>(ReferenceEqualityComparer.Instance);
        var ordered = new List<Tree>();
        for (var i = 0; i < queue.Count; i++)
        {
            var subtree = queue[i];
            if (seen.Add(subtree))
            {
                ordered.Add(subtree);
            }
            else
            {
                // Python dict semantics: re-inserting an existing key keeps its original position
                continue;
            }
            for (var j = subtree.Children.Count - 1; j >= 0; j--)
            {
                if (subtree.Children[j] is Tree c && !seen.Contains(c)) queue.Add(c);
            }
        }
        ordered.Reverse();
        return ordered;
    }

    /// <summary>Breadth-first iteration. Iterates over all the subtrees, returning nodes in order like <see cref="Pretty"/> does.</summary>
    public IEnumerable<Tree> IterSubtreesTopDown()
    {
        var stack = new Stack<object?>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is not Tree t) continue;
            yield return t;
            for (var i = t.Children.Count - 1; i >= 0; i--) stack.Push(t.Children[i]);
        }
    }

    /// <summary>Returns all nodes of the tree that evaluate <paramref name="pred"/> as true.</summary>
    public IEnumerable<Tree> FindPred(Func<Tree, bool> pred) => IterSubtrees().Where(pred);

    /// <summary>Returns all nodes of the tree whose data equals the given data.</summary>
    public IEnumerable<Tree> FindData(string data) => FindPred(t => t.Data == data);

    /// <summary>Returns all tokens whose type equals the given token type.</summary>
    public IEnumerable<Token> FindToken(string tokenType) => ScanValues(v => v is Token t && t.Type == tokenType).Cast<Token>();

    /// <summary>Expand (inline) children with any of the given data values. Returns true if anything changed.</summary>
    public bool ExpandKidsByData(params string[] dataValues)
    {
        var changed = false;
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (Children[i] is Tree child && dataValues.Contains(child.Data))
            {
                Children.RemoveAt(i);
                Children.InsertRange(i, child.Children);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Return all values in the tree that evaluate <paramref name="pred"/> as true.</summary>
    public IEnumerable<object?> ScanValues(Func<object?, bool> pred)
    {
        foreach (var c in Children)
        {
            if (c is Tree t)
            {
                foreach (var v in t.ScanValues(pred)) yield return v;
            }
            else if (pred(c))
            {
                yield return c;
            }
        }
    }

    /// <summary>Shallow copy: a new tree with the same data and children list contents.</summary>
    public virtual Tree Copy() => new(Data, new List<object?>(Children));

    /// <summary>Deep copy of the tree, its subtrees and its tokens.</summary>
    public Tree DeepCopy() => (Tree)DeepCopyValue(this)!;

    internal static object? DeepCopyValue(object? value) => value switch
    {
        Tree t => t.CloneWith(t.Children.Select(DeepCopyValue).ToList()),
        Token tok => tok.Copy(),
        List<object?> l => l.Select(DeepCopyValue).ToList(),
        ICloneable c => c.Clone(),
        _ => value,
    };

    /// <summary>Creates a tree of the same type with the given children (used by deep copies).</summary>
    protected virtual Tree CloneWith(List<object?> children) => new(Data, children, _meta);

    public void Set(string data, List<object?> children)
    {
        Data = data;
        Children = children;
    }

    public bool Equals(Tree? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return Data == other.Data && ChildrenEqual(Children, other.Children);
    }

    internal static bool ChildrenEqual(IReadOnlyList<object?> a, IReadOnlyList<object?> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!ValueEquals(a[i], b[i])) return false;
        }
        return true;
    }

    /// <summary>Equality as used by Lark: tokens equal strings with the same value, lists compare element-wise.</summary>
    public static bool ValueEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a is string s && b is Token tb) return tb.Equals(s);
        if (a is System.Collections.IList la && b is System.Collections.IList lb && a is not string)
        {
            return ChildrenEqual(la.Cast<object?>().ToList(), lb.Cast<object?>().ToList());
        }
        return a.Equals(b);
    }

    public override bool Equals(object? obj) => obj is Tree t && Equals(t);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Data);
        foreach (var c in Children) hash.Add(c is null ? 0 : c is string s ? s.GetHashCode() : c.GetHashCode());
        return hash.ToHashCode();
    }

    public static bool operator ==(Tree? a, Tree? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(Tree? a, Tree? b) => !(a == b);
}
