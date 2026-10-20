namespace EtAlii.Lark.Examples.PurchaseOrders;

public enum Currency { EUR, USD, GBP }

public enum CustomerTier { Bronze, Silver, Gold }

public enum OrderField { OrderTotal, LineQuantity, CustomerTier }

public enum ComparisonOperator { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }

public readonly record struct Percentage(decimal Value);

public sealed record PurchaseOrder(
    string Id,
    Customer Customer,
    Currency Currency,
    IReadOnlyList<OrderLine> Lines,
    Shipping? Shipping,
    IReadOnlyList<PricingRule> Rules);

public sealed record Customer(string Name, CustomerTier Tier);

public sealed record OrderLine(string Sku, string Description, int Quantity, decimal UnitPrice, Percentage? Discount)
{
    public decimal Total => Quantity * UnitPrice * (1 - (Discount?.Value ?? 0) / 100);
}

public abstract record Shipping;

public sealed record StandardShipping(Address Address) : Shipping;

public sealed record ExpressShipping(Address Address) : Shipping;

public sealed record Pickup(string Location) : Shipping;

public sealed record Address(IReadOnlyList<string> Lines);

public sealed record PricingRule(Condition When, RuleAction Then);

public abstract record Condition;

public sealed record AnyOf(Condition Left, Condition Right) : Condition;

public sealed record AllOf(Condition Left, Condition Right) : Condition;

public sealed record Comparison(OrderField Field, ComparisonOperator Operator, Operand Value) : Condition;

public abstract record Operand;

public sealed record AmountOperand(decimal Amount) : Operand;

public sealed record CountOperand(int Count) : Operand;

public sealed record TierOperand(CustomerTier Tier) : Operand;

public abstract record RuleAction;

public sealed record PercentDiscount(Percentage Percentage) : RuleAction;

public sealed record FreeShipping : RuleAction;

public sealed record RequireApproval(string Role) : RuleAction;
