//! Node wrappers, without a typed visitor, on the purchase order grammar of the .NET documentation.

use etalii_lark_derive::lark_grammar;

#[lark_grammar(
    file = "../../dotnet/docs/examples/PurchaseOrders/purchase-order.lark",
    parser = "lalr",
    start = "order"
)]
pub mod purchase_order {}

use purchase_order::*;

const ORDER: &str = r#"order "PO-2026-0142"
customer "ACME Corp" (gold)
currency EUR
line "LT-14" "Laptop 14 inch" qty 5 at 1299.00
line "DS-02" "Docking station" qty 5 at 189.50 discount 10%
ship express to "Keizersgracht 1" "1015 CS Amsterdam" "NL"
rule when order.total > 5000.00 and customer.tier = gold then discount 5%
rule when line.quantity >= 50 or (order.total >= 20000.00) then require approval by "finance"
"#;

#[test]
fn reads_an_order() {
    let tree = parse(ORDER).unwrap();
    let order = OrderNode::new(&tree).unwrap();
    assert_eq!(order.text().value, "\"PO-2026-0142\"");
    assert_eq!(order.customer().tier().unwrap().value, "gold");
    assert_eq!(order.currency().value, "EUR");

    let lines: Vec<LineNode> = order.lines().collect();
    assert_eq!(lines.len(), 2);
    assert_eq!(lines[0].count().value, "5");
    assert_eq!(lines[0].amount().value, "1299.00");
    assert!(lines[0].percent().is_none());
    assert_eq!(lines[1].percent().unwrap().value, "10%");

    let Some(Shipping::ExpressShipping(express)) = order.shipping() else {
        panic!()
    };
    let address: Vec<&str> = express.address().texts().map(|t| t.value.as_str()).collect();
    assert_eq!(address, ["\"Keizersgracht 1\"", "\"1015 CS Amsterdam\"", "\"NL\""]);

    let rules: Vec<RuleNode> = order.rules().collect();
    let Condition::AllOf(all) = rules[0].condition() else { panic!() };
    let Conjunction::Comparison(total) = all.conjunction() else {
        panic!()
    };
    assert_eq!(total.field().value, "order.total");
    assert!(matches!(total.operand(), Operand::AmountOperand(_)));
    assert!(matches!(rules[0].action(), Action::PercentDiscount(_)));
    assert!(matches!(rules[1].action(), Action::RequireApproval(r) if r.text().value == "\"finance\""));
}
