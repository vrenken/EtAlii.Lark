namespace EtAlii.Lark;

/// <summary>
/// Sets the result type of named nodes (rules and aliases) and terminals of a <see cref="LarkGrammarAttribute"/> grammar.
/// </summary>
/// <remarks>
/// <para>
/// When a grammar class has one or more of these attributes, the source generator adds a typed, bottom-up
/// <c>Visitor</c> class next to <c>Visitor&lt;TResult&gt;</c>. It visits the children of a tree first, and passes them,
/// converted to their result types, as parameters to the visit method of the tree:
/// </para>
/// <list type="bullet">
/// <item>a mapped node gets an abstract <c>VisitXxx(XxxNode node, ...)</c> method that returns the mapped type;</item>
/// <item>a mapped terminal gets an abstract <c>VisitXxxToken(Token token)</c> method that returns the mapped type;</item>
/// <item>a node without a mapping is passed on as its node struct, and a terminal without one as its <see cref="Token"/>;</item>
/// <item>repeated children (<c>item*</c>, <c>item+</c>) are passed as an <see cref="IReadOnlyList{T}"/>, and optional children
/// (<c>[item]</c>, <c>item?</c>) as nullable values;</item>
/// <item>a child that can be one of several nodes, such as an inlined <c>?rule</c>, has the common base type of their result
/// types. Name the <c>?rule</c> (or <c>_rule</c>) in an attribute to set that type explicitly.</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// [LarkGrammar("purchase-order.lark", Parser = ParserKind.Lalr, Start = new[] { "order" })]
/// [LarkResult(typeof(PurchaseOrder), "order")]
/// [LarkResult(typeof(OrderLine), "line")]
/// [LarkResult(typeof(decimal), "AMOUNT")]
/// public partial class PurchaseOrderParser;
///
/// public sealed class Builder : PurchaseOrderParser.Visitor
/// {
///     protected override OrderLine VisitLine(PurchaseOrderParser.LineNode node, string sku, int quantity, decimal price) =>
///         new(sku, quantity, price);
///     // ...
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class LarkResultAttribute : Attribute
{
    /// <param name="type">The type the visitor returns for the named nodes or terminals.</param>
    /// <param name="names">The names of rules, aliases or terminals, as they appear in the grammar.</param>
    public LarkResultAttribute(Type type, params string[] names)
    {
        Type = type;
        Names = names;
    }

    /// <summary>The type the visitor returns for the named nodes or terminals.</summary>
    public Type Type { get; }

    /// <summary>The names of rules, aliases or terminals, as they appear in the grammar.</summary>
    public IReadOnlyList<string> Names { get; }
}
