namespace EtAlii.Lark.Tests.Generator;

using EtAlii.Lark.Examples.PurchaseOrders;

/// <summary>Tests the typed visitor with the purchase order example of docs/typed-visitor.md.</summary>
public class PurchaseOrderTests
{
    private const string Source = """
        order "PO-2026-0142"
        customer "ACME Corp" (gold)
        currency EUR
        line "LT-14" "Laptop 14 inch" qty 5 at 1299.00
        line "DS-02" "Docking station" qty 5 at 189.50 discount 10%
        ship express to "Keizersgracht 1" "1015 CS Amsterdam" "NL"
        rule when order.total > 5000.00 and customer.tier = gold then discount 5%
        rule when line.quantity >= 50 or (order.total >= 20000.00) then require approval by "finance"
        """;

    [Fact]
    public void Builds_the_order_header_lines_and_shipping()
    {
        var order = PurchaseOrderBuilder.Build(Source);

        Assert.Equal("PO-2026-0142", order.Id);
        Assert.Equal(new Customer("ACME Corp", CustomerTier.Gold), order.Customer);
        Assert.Equal(Currency.EUR, order.Currency);
        Assert.Collection(order.Lines,
            line => Assert.Equal(new OrderLine("LT-14", "Laptop 14 inch", 5, 1299.00m, null), line),
            line => Assert.Equal(new OrderLine("DS-02", "Docking station", 5, 189.50m, new Percentage(10m)), line));
        Assert.Equal(7347.75m, order.Lines.Sum(line => line.Total));

        var shipping = Assert.IsType<ExpressShipping>(order.Shipping);
        Assert.Equal(new[] { "Keizersgracht 1", "1015 CS Amsterdam", "NL" }, shipping.Address.Lines);
    }

    [Fact]
    public void Builds_typed_pricing_rules()
    {
        var order = PurchaseOrderBuilder.Build(Source);

        var discount = order.Rules[0];
        var all = Assert.IsType<AllOf>(discount.When);
        Assert.Equal(new Comparison(OrderField.OrderTotal, ComparisonOperator.Greater, new AmountOperand(5000.00m)), all.Left);
        Assert.Equal(new Comparison(OrderField.CustomerTier, ComparisonOperator.Equal, new TierOperand(CustomerTier.Gold)), all.Right);
        Assert.Equal(new PercentDiscount(new Percentage(5m)), discount.Then);

        var approval = order.Rules[1];
        var any = Assert.IsType<AnyOf>(approval.When);
        Assert.Equal(new Comparison(OrderField.LineQuantity, ComparisonOperator.GreaterOrEqual, new CountOperand(50)), any.Left);
        Assert.Equal(new Comparison(OrderField.OrderTotal, ComparisonOperator.GreaterOrEqual, new AmountOperand(20000.00m)), any.Right);
        Assert.Equal(new RequireApproval("finance"), approval.Then);
    }

    [Fact]
    public void Optional_parts_arrive_as_null()
    {
        var order = PurchaseOrderBuilder.Build("""
            order "PO-7" customer "Small Shop" currency USD
            line "PEN" "Ballpoint" qty 100 at 0.45
            """);

        Assert.Equal(CustomerTier.Bronze, order.Customer.Tier);
        Assert.Null(order.Shipping);
        Assert.Null(Assert.Single(order.Lines).Discount);
        Assert.Empty(order.Rules);
    }

    [Fact]
    public void Shipping_alternatives_share_their_base_type()
    {
        var pickup = PurchaseOrderBuilder.Build("""
            order "PO-8" customer "Walk-in" currency GBP
            line "MUG" "Coffee mug" qty 2 at 7.50
            pickup at "Store 12"
            rule when line.quantity < 10 then free shipping
            """);

        Assert.Equal(new Pickup("Store 12"), pickup.Shipping);
        Assert.IsType<FreeShipping>(Assert.Single(pickup.Rules).Then);
    }

    [Fact]
    public void A_single_line_can_be_visited_on_its_own()
    {
        var tree = PurchaseOrderParser.ParseOrder(Source);
        var lineTree = tree.Children.OfType<Tree>().First(t => t.Data == PurchaseOrderParser.Rules.Line);

        OrderLine line = new PurchaseOrderBuilder().Visit(new PurchaseOrderParser.LineNode(lineTree));

        Assert.Equal(6495.00m, line.Total);
    }

    [Fact]
    public void Errors_in_visit_methods_surface()
    {
        var exception = Assert.Throws<FormatException>(() => PurchaseOrderBuilder.Build("""
            order "PO-9" customer "X" currency EUR
            line "A" "Nothing" qty 0 at 1.00
            """));

        Assert.Contains("A", exception.Message);
    }
}
