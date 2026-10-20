# Typed visitors

`Visitor<TResult>` returns one type for every rule. When rules stand for different things, such as an order, an order
line and a pricing rule, map each of them to its own type with `[LarkResult]`. The generator then adds a typed,
bottom-up `Visitor` class to the grammar class. It visits the children of a tree first, converts them to their result
types, and passes them as parameters to the visit method of the tree.

```csharp
[LarkGrammar("purchase-order.lark", Parser = ParserKind.Lalr, Start = new[] { "order" })]
[LarkResult(typeof(PurchaseOrder), "order")]
[LarkResult(typeof(OrderLine), "line")]
[LarkResult(typeof(decimal), "AMOUNT")]
// ...
public partial class PurchaseOrderParser;

public sealed class PurchaseOrderBuilder : PurchaseOrderParser.Visitor
{
    protected override OrderLine VisitLine(PurchaseOrderParser.LineNode node, string sku, string description,
        int quantity, decimal unitPrice, Percentage? discount) => new(sku, description, quantity, unitPrice, discount);
    // ...
}
```

The typed `Visitor` is only generated for grammar classes with at least one `[LarkResult]`; `Visitor<TResult>` is always
generated as before.

## How children become parameters

| In the grammar | Parameter |
|----------------|-----------|
| A mapped rule or alias, `line` | Its result type, `OrderLine` |
| A rule or alias without a mapping | Its node struct, `LineNode` |
| A mapped terminal, `AMOUNT` | Its result type, `decimal`, from `VisitAmountToken(Token)` |
| A terminal without a mapping | `Token` |
| `item*`, `item+`, `item~2..5` | `IReadOnlyList<T>` |
| `[item]` (with `MaybePlaceholders`, the default) | `T?`, null when missing |
| `item?`, or `[item]` without placeholders | `T?` |
| A `?rule` or an aliased rule, which can be one of several trees | The common base type of their result types |
| A `_rule` | Its children, spliced into the parent's parameters |
| Filtered tokens: anonymous strings, `_TERMINALS` | No parameter |

For the child of a `?rule`, or of a rule whose alternatives all have aliases, the generator looks for the most derived
base class that all possible result types share, then for a single shared interface. Map the `?rule` itself to set the
type explicitly; every tree it can contain must then convert to that type.

Parameter names come from the grammar (`text`, `text2`, `count`). Overrides may rename them, as in the example.

## What the generated visitor contains

- `public T Visit(XxxNode node)` for each node: it visits the children and calls the visit method below. These are the
  entry points; a start rule that is always inlined (`?start: sum`) gets `Visit{Start}(Tree tree)` instead.
- `protected abstract T VisitXxx(XxxNode node, ...)` for each mapped node, so the compiler reports a missing one.
- `protected virtual XxxNode VisitXxx(XxxNode node, ...)` for each node without a mapping. It returns the node, the way
  Lark's transformers keep a subtree, unless you override it.
- `protected abstract T VisitXxxToken(Token token)` for each mapped terminal.
- `VisitTree(Tree)`, `VisitToken(Token)` and `VisitChild(object?)`, which dispatch at runtime and return `object?`.

A node whose alternatives produce different children has no fixed shape. Its visit method gets
`IReadOnlyList<object?> children` instead, each child visited with `VisitChild`. Giving its alternatives aliases gives each
of them typed parameters.

## Example: purchase orders

The files of this example are in [examples/PurchaseOrders](examples/PurchaseOrders). The test project compiles them and
tests them in `tests/EtAlii.Lark.Tests/Generator/PurchaseOrderTests.cs`.

- [purchase-order.lark](examples/PurchaseOrders/purchase-order.lark) is the grammar: an order with lines, optional
  shipping and pricing rules, whose conditions combine comparisons with `and` and `or`.
- [PurchaseOrder.cs](examples/PurchaseOrders/PurchaseOrder.cs) is the domain model, records and enums.
- [PurchaseOrderParser.cs](examples/PurchaseOrders/PurchaseOrderParser.cs) is the grammar class with its `[LarkResult]` mappings.
- [PurchaseOrderBuilder.cs](examples/PurchaseOrders/PurchaseOrderBuilder.cs) inherits the generated visitor and builds the model.

A purchase order in the grammar:

```text
order "PO-2026-0142"
customer "ACME Corp" (gold)
currency EUR
line "LT-14" "Laptop 14 inch" qty 5 at 1299.00
line "DS-02" "Docking station" qty 5 at 189.50 discount 10%
ship express to "Keizersgracht 1" "1015 CS Amsterdam" "NL"
rule when order.total > 5000.00 and customer.tier = gold then discount 5%
rule when line.quantity >= 50 or (order.total >= 20000.00) then require approval by "finance"
```

The typing is hierarchical. `order: "order" TEXT customer "currency" CURRENCY line+ [shipping] rule*` gives:

```csharp
protected override PurchaseOrder VisitOrder(PurchaseOrderParser.OrderNode node, string id, Customer customer,
    Currency currency, IReadOnlyList<OrderLine> lines, Shipping? shipping, IReadOnlyList<PricingRule> rules) =>
    new(id, customer, currency, lines, shipping, rules);
```

`shipping` is a `?rule` with three aliases, mapped to `StandardShipping`, `ExpressShipping` and `Pickup`, so the
parameter has their common base type `Shipping`. `[shipping]` makes it nullable. The conditions work the same way:
`any_of`, `all_of` and `comparison` all derive from `Condition`, so `rule: "rule" "when" condition "then" action` gets
`(Condition when, RuleAction then)`.

Using it:

```csharp
PurchaseOrder order = PurchaseOrderBuilder.Build(text);

decimal total = order.Lines.Sum(line => line.Total);           // 7347.75
var express = (ExpressShipping)order.Shipping!;                  // "Keizersgracht 1", ...
var discount = (PercentDiscount)order.Rules[0].Then;            // 5%
```

## Diagnostics

| Id | Severity | Meaning |
|----|----------|---------|
| LARK005 | Error | A name in `[LarkResult]` is not a rule, alias or terminal of the grammar. |
| LARK006 | Error | A name appears in more than one `[LarkResult]`. |
| LARK007 | Warning | The trees or tokens at a child position have no common result type, so they are passed as `object`. |
| LARK008 | Error | A tree or token in a mapped `?rule` has a result type that doesn't convert to the rule's type. |
| LARK009 | Info | A mapped node has no fixed shape, so its children are passed as a list of objects. |
