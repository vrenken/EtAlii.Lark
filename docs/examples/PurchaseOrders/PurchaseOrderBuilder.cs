using System.Globalization;
using System.Text.Json;

namespace EtAlii.Lark.Examples.PurchaseOrders;

/// <summary>Builds a <see cref="PurchaseOrder"/> from a parsed purchase order.</summary>
/// <remarks>
/// Each visit method gets the children of its tree already built, as typed parameters. The parameter names come from
/// the grammar (<c>text</c>, <c>text2</c>, <c>count</c>); overrides are free to rename them.
/// </remarks>
public sealed class PurchaseOrderBuilder : PurchaseOrderParser.Visitor
{
    /// <summary>Parses and builds a purchase order.</summary>
    public static PurchaseOrder Build(string text) => new PurchaseOrderBuilder().Visit(PurchaseOrderParser.ParseOrder(text));

    // Rules

    protected override PurchaseOrder VisitOrder(PurchaseOrderParser.OrderNode node, string id, Customer customer,
        Currency currency, IReadOnlyList<OrderLine> lines, Shipping? shipping, IReadOnlyList<PricingRule> rules) =>
        new(id, customer, currency, lines, shipping, rules);

    protected override Customer VisitCustomer(PurchaseOrderParser.CustomerNode node, string name, CustomerTier? tier) =>
        new(name, tier ?? CustomerTier.Bronze);

    protected override OrderLine VisitLine(PurchaseOrderParser.LineNode node, string sku, string description,
        int quantity, decimal unitPrice, Percentage? discount) =>
        quantity > 0
            ? new(sku, description, quantity, unitPrice, discount)
            : throw new FormatException($"Order line {sku} has no quantity.");

    protected override StandardShipping VisitStandardShipping(PurchaseOrderParser.StandardShippingNode node, Address address) => new(address);

    protected override ExpressShipping VisitExpressShipping(PurchaseOrderParser.ExpressShippingNode node, Address address) => new(address);

    protected override Pickup VisitPickup(PurchaseOrderParser.PickupNode node, string location) => new(location);

    protected override Address VisitAddress(PurchaseOrderParser.AddressNode node, IReadOnlyList<string> lines) => new(lines);

    protected override PricingRule VisitRule(PurchaseOrderParser.RuleNode node, Condition when, RuleAction then) => new(when, then);

    protected override AnyOf VisitAnyOf(PurchaseOrderParser.AnyOfNode node, Condition left, Condition right) => new(left, right);

    protected override AllOf VisitAllOf(PurchaseOrderParser.AllOfNode node, Condition left, Condition right) => new(left, right);

    protected override Comparison VisitComparison(PurchaseOrderParser.ComparisonNode node,
        OrderField field, ComparisonOperator op, Operand value) => new(field, op, value);

    protected override AmountOperand VisitAmountOperand(PurchaseOrderParser.AmountOperandNode node, decimal amount) => new(amount);

    protected override CountOperand VisitCountOperand(PurchaseOrderParser.CountOperandNode node, int count) => new(count);

    protected override TierOperand VisitTierOperand(PurchaseOrderParser.TierOperandNode node, CustomerTier tier) => new(tier);

    protected override PercentDiscount VisitPercentDiscount(PurchaseOrderParser.PercentDiscountNode node, Percentage percentage) => new(percentage);

    protected override FreeShipping VisitFreeShipping(PurchaseOrderParser.FreeShippingNode node) => new();

    protected override RequireApproval VisitRequireApproval(PurchaseOrderParser.RequireApprovalNode node, string role) => new(role);

    // Terminals

    protected override string VisitTextToken(Token token) => JsonSerializer.Deserialize<string>(token.Value)!;

    protected override int VisitCountToken(Token token) => int.Parse(token.Value, CultureInfo.InvariantCulture);

    protected override decimal VisitAmountToken(Token token) => decimal.Parse(token.Value, CultureInfo.InvariantCulture);

    protected override Percentage VisitPercentToken(Token token) =>
        new(decimal.Parse(token.Value.TrimEnd('%'), CultureInfo.InvariantCulture));

    protected override CustomerTier VisitTierToken(Token token) => Enum.Parse<CustomerTier>(token.Value, ignoreCase: true);

    protected override Currency VisitCurrencyToken(Token token) => Enum.Parse<Currency>(token.Value);

    protected override OrderField VisitFieldToken(Token token) => token.Value switch
    {
        "order.total" => OrderField.OrderTotal,
        "line.quantity" => OrderField.LineQuantity,
        _ => OrderField.CustomerTier,
    };

    protected override ComparisonOperator VisitOpToken(Token token) => token.Value switch
    {
        "=" => ComparisonOperator.Equal,
        "!=" => ComparisonOperator.NotEqual,
        "<" => ComparisonOperator.Less,
        "<=" => ComparisonOperator.LessOrEqual,
        ">" => ComparisonOperator.Greater,
        _ => ComparisonOperator.GreaterOrEqual,
    };
}
