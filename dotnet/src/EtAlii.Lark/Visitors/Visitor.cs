namespace EtAlii.Lark;

/// <summary>Common base of visitors: dispatches a tree to the method matching its <see cref="Tree.Data"/>.</summary>
public abstract class VisitorBase
{
    internal void CallUserFunc(Tree tree)
    {
        var bound = CallbackBinder.FindRule(GetType(), tree.Data);
        if (bound is null)
        {
            Default(tree);
            return;
        }
        if (bound.Parameters.Length == 1)
        {
            bound.InvokeTree(this, tree);
        }
        else
        {
            bound.InvokeRule(this, tree.Data, tree.Children, tree.Meta, () => tree);
        }
    }

    /// <summary>Default function that is called if there is no method matching <see cref="Tree.Data"/>. Defaults to doing nothing.</summary>
    protected virtual void Default(Tree tree) { }
}

/// <summary>
/// Tree visitor, non-recursive (can handle huge trees).
/// Visiting a node calls its methods (provided by the user via inheritance) according to <see cref="Tree.Data"/>.
/// Methods receive the <see cref="Tree"/>.
/// </summary>
public abstract class Visitor : VisitorBase
{
    /// <summary>Visits the tree, starting with the leaves and finally the root (bottom-up).</summary>
    public Tree Visit(Tree tree)
    {
        foreach (var subtree in tree.IterSubtrees()) CallUserFunc(subtree);
        return tree;
    }

    /// <summary>Visit the tree, starting at the root, and ending at the leaves (top-down).</summary>
    public Tree VisitTopDown(Tree tree)
    {
        foreach (var subtree in tree.IterSubtreesTopDown()) CallUserFunc(subtree);
        return tree;
    }
}

/// <summary>Bottom-up visitor, recursive. Slightly faster than the non-recursive version.</summary>
public abstract class RecursiveVisitor : VisitorBase
{
    /// <summary>Visits the tree, starting with the leaves and finally the root (bottom-up).</summary>
    public Tree Visit(Tree tree)
    {
        foreach (var child in tree.Children)
        {
            if (child is Tree t) Visit(t);
        }
        CallUserFunc(tree);
        return tree;
    }

    /// <summary>Visit the tree, starting at the root, and ending at the leaves (top-down).</summary>
    public Tree VisitTopDown(Tree tree)
    {
        CallUserFunc(tree);
        foreach (var child in tree.Children)
        {
            if (child is Tree t) VisitTopDown(t);
        }
        return tree;
    }
}

/// <summary>
/// Interpreter walks the tree starting at the root (top-down).
/// </summary>
/// <remarks>
/// For each tree node, it calls the method matching <see cref="Tree.Data"/>. Unlike <see cref="Transformer"/> and
/// <see cref="Visitor"/>, the Interpreter doesn't automatically visit its sub-branches: the user has to explicitly
/// call <see cref="Visit"/> or <see cref="VisitChildren"/>. This allows the user to implement branching and loops.
/// A method that accepts a list (<c>List&lt;object?&gt;</c>) instead of a tree receives the values of the visited
/// children (Lark's <c>visit_children_decor</c>).
/// </remarks>
public abstract class Interpreter
{
    public object? Visit(Tree tree) => VisitTree(tree);

    private object? VisitTree(Tree tree)
    {
        var bound = CallbackBinder.FindRule(GetType(), tree.Data);
        if (bound is null) return Default(tree);
        return bound.Mode switch
        {
            ArgsMode.Tree => bound.InvokeTree(this, tree),
            ArgsMode.Children => bound.InvokeRule(this, tree.Data, VisitChildren(tree), tree.Meta),
            ArgsMode.MetaChildren => bound.InvokeRule(this, tree.Data, VisitChildren(tree), tree.Meta),
            _ => bound.InvokeRule(this, tree.Data, tree.Children, tree.Meta),
        };
    }

    /// <summary>Visits the children of the tree and returns their values (non-tree children are returned as-is).</summary>
    public List<object?> VisitChildren(Tree tree) =>
        tree.Children.Select(child => child is Tree t ? VisitTree(t) : child).ToList();

    /// <summary>Called when no method matches. Defaults to visiting the children.</summary>
    protected virtual object? Default(Tree tree) => VisitChildren(tree);
}
