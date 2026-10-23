namespace EtAlii.Lark;

/// <summary>
/// Binds a transformer/visitor method to a rule (or alias) name. Can be applied multiple times.
/// Without this attribute, a method is bound to the rule whose name equals the method name, or whose
/// snake_case name converts to the PascalCase method name (e.g. rule <c>add_expr</c> → method <c>AddExpr</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RuleAttribute : Attribute
{
    public RuleAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

/// <summary>
/// Binds a transformer method to a terminal (token type) name. Can be applied multiple times.
/// The method should accept a single <see cref="Token"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class TerminalAttribute : Attribute
{
    public TerminalAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

/// <summary>Excludes a method from being bound to a rule or terminal.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class NotARuleAttribute : Attribute { }

/// <summary>
/// Changes how the arguments of a transformer callback are provided (Lark's <c>v_args</c>).
/// Applies to a single method, or to all methods of a class. When omitted, the arguments are inferred from
/// the method signature:
/// <list type="bullet">
/// <item><c>(List&lt;object?&gt; children)</c>: the children as a list.</item>
/// <item><c>(Meta meta, List&lt;object?&gt; children)</c>: the meta and the children.</item>
/// <item><c>(Tree tree)</c>: the whole tree.</item>
/// <item>anything else: the children are passed inline, as separate arguments, optionally preceded by a <see cref="Meta"/>.</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true)]
public sealed class VArgsAttribute : Attribute
{
    /// <summary>Children are provided as separate arguments instead of a list.</summary>
    public bool Inline { get; set; }

    /// <summary>The <see cref="Meta"/> of the tree is provided as first argument.</summary>
    public bool Meta { get; set; }

    /// <summary>The entire tree is provided as the argument, instead of the children.</summary>
    public bool Tree { get; set; }
}
