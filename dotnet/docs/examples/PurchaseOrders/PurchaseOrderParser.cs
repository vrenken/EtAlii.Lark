namespace EtAlii.Lark.Examples.PurchaseOrders;

[LarkGrammar("purchase-order.lark", Parser = ParserKind.Lalr, Start = new[] { "order" })]
// Rules and aliases
[LarkResult(typeof(PurchaseOrder), "order")]
[LarkResult(typeof(Customer), "customer")]
[LarkResult(typeof(OrderLine), "line")]
[LarkResult(typeof(StandardShipping), "standard_shipping")]
[LarkResult(typeof(ExpressShipping), "express_shipping")]
[LarkResult(typeof(Pickup), "pickup")]
[LarkResult(typeof(Address), "address")]
[LarkResult(typeof(PricingRule), "rule")]
[LarkResult(typeof(AnyOf), "any_of")]
[LarkResult(typeof(AllOf), "all_of")]
[LarkResult(typeof(Comparison), "comparison")]
[LarkResult(typeof(AmountOperand), "amount_operand")]
[LarkResult(typeof(CountOperand), "count_operand")]
[LarkResult(typeof(TierOperand), "tier_operand")]
[LarkResult(typeof(PercentDiscount), "percent_discount")]
[LarkResult(typeof(FreeShipping), "free_shipping")]
[LarkResult(typeof(RequireApproval), "require_approval")]
// Terminals
[LarkResult(typeof(string), "TEXT")]
[LarkResult(typeof(int), "COUNT")]
[LarkResult(typeof(decimal), "AMOUNT")]
[LarkResult(typeof(Percentage), "PERCENT")]
[LarkResult(typeof(CustomerTier), "TIER")]
[LarkResult(typeof(Currency), "CURRENCY")]
[LarkResult(typeof(OrderField), "FIELD")]
[LarkResult(typeof(ComparisonOperator), "OP")]
// Inlined rules: optional, the common base type (here Condition) is inferred otherwise.
[LarkResult(typeof(Condition), "condition", "conjunction", "test")]
public partial class PurchaseOrderParser;
