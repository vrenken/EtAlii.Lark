//! Builds a purchase order model with the typed visitor: the Rust version of the .NET example in
//! `dotnet/docs/examples/PurchaseOrders`, on the same grammar.
//!
//! ```sh
//! cargo run --example purchase_orders
//! ```

use etalii_lark::Token;
use etalii_lark_derive::{lark_grammar, lark_result};

// The model. Amounts are in cents, percentages in hundredths of a percent.

#[derive(Clone, Copy, Debug, PartialEq)]
enum Currency {
    Eur,
    Usd,
    Gbp,
}

#[derive(Clone, Copy, Debug, PartialEq)]
enum CustomerTier {
    Bronze,
    Silver,
    Gold,
}

#[derive(Clone, Copy, Debug, PartialEq)]
enum OrderField {
    OrderTotal,
    LineQuantity,
    CustomerTier,
}

#[derive(Clone, Copy, Debug, PartialEq)]
enum ComparisonOperator {
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

#[derive(Debug, PartialEq)]
struct PurchaseOrder {
    id: String,
    customer: Customer,
    currency: Currency,
    lines: Vec<OrderLine>,
    shipping: Option<Shipping>,
    rules: Vec<PricingRule>,
}

#[derive(Debug, PartialEq)]
struct Customer {
    name: String,
    tier: CustomerTier,
}

#[derive(Debug, PartialEq)]
struct OrderLine {
    sku: String,
    description: String,
    quantity: u32,
    unit_price: i64,
    discount: Option<i64>,
}

impl OrderLine {
    fn total(&self) -> i64 {
        self.quantity as i64 * self.unit_price * (10_000 - self.discount.unwrap_or(0)) / 10_000
    }
}

#[derive(Debug, PartialEq)]
enum Shipping {
    Standard(Vec<String>),
    Express(Vec<String>),
    Pickup(String),
}

#[derive(Debug, PartialEq)]
struct PricingRule {
    when: Condition,
    then: RuleAction,
}

#[derive(Debug, PartialEq)]
enum Condition {
    AnyOf(Box<Condition>, Box<Condition>),
    AllOf(Box<Condition>, Box<Condition>),
    Comparison(OrderField, ComparisonOperator, Operand),
}

#[derive(Debug, PartialEq)]
enum Operand {
    Amount(i64),
    Count(u32),
    Tier(CustomerTier),
}

#[derive(Debug, PartialEq)]
enum RuleAction {
    PercentDiscount(i64),
    FreeShipping,
    RequireApproval(String),
}

// The grammar, with the result type of each rule, alias and terminal. The aliases of a ?rule share the type of the
// rule (`Shipping`, `Condition`, ...), so its children come as that type.

#[lark_grammar(
    file = "../../dotnet/docs/examples/PurchaseOrders/purchase-order.lark",
    parser = "lalr",
    start = "order"
)]
#[lark_result(PurchaseOrder, "order")]
#[lark_result(Customer, "customer")]
#[lark_result(OrderLine, "line")]
#[lark_result(Shipping, "standard_shipping", "express_shipping", "pickup")]
#[lark_result(Vec<String>, "address")]
#[lark_result(PricingRule, "rule")]
#[lark_result(Condition, "any_of", "all_of", "comparison")]
#[lark_result(Operand, "amount_operand", "count_operand", "tier_operand")]
#[lark_result(RuleAction, "percent_discount", "free_shipping", "require_approval")]
#[lark_result(String, "TEXT")]
#[lark_result(u32, "COUNT")]
#[lark_result(i64, "AMOUNT", "PERCENT")]
#[lark_result(CustomerTier, "TIER")]
#[lark_result(Currency, "CURRENCY")]
#[lark_result(OrderField, "FIELD")]
#[lark_result(ComparisonOperator, "OP")]
mod purchase_order {}

use purchase_order::*;

/// Builds a `PurchaseOrder`. Each method gets the children of its tree already built.
struct Builder;

impl Visitor for Builder {
    type Error = String;

    fn visit_order(
        &mut self,
        _: OrderNode<'_>,
        id: String,
        customer: Customer,
        currency: Currency,
        lines: Vec<OrderLine>,
        shipping: Option<Shipping>,
        rules: Vec<PricingRule>,
    ) -> Result<PurchaseOrder, String> {
        Ok(PurchaseOrder {
            id,
            customer,
            currency,
            lines,
            shipping,
            rules,
        })
    }

    fn visit_customer(&mut self, _: CustomerNode<'_>, name: String, tier: Option<CustomerTier>) -> Result<Customer, String> {
        Ok(Customer {
            name,
            tier: tier.unwrap_or(CustomerTier::Bronze),
        })
    }

    fn visit_line(
        &mut self,
        _: LineNode<'_>,
        sku: String,
        description: String,
        quantity: u32,
        unit_price: i64,
        discount: Option<i64>,
    ) -> Result<OrderLine, String> {
        if quantity == 0 {
            return Err(format!("Order line {sku} has no quantity."));
        }
        Ok(OrderLine {
            sku,
            description,
            quantity,
            unit_price,
            discount,
        })
    }

    fn visit_standard_shipping(&mut self, _: StandardShippingNode<'_>, address: Vec<String>) -> Result<Shipping, String> {
        Ok(Shipping::Standard(address))
    }

    fn visit_express_shipping(&mut self, _: ExpressShippingNode<'_>, address: Vec<String>) -> Result<Shipping, String> {
        Ok(Shipping::Express(address))
    }

    fn visit_pickup(&mut self, _: PickupNode<'_>, location: String) -> Result<Shipping, String> {
        Ok(Shipping::Pickup(location))
    }

    fn visit_address(&mut self, _: AddressNode<'_>, lines: Vec<String>) -> Result<Vec<String>, String> {
        Ok(lines)
    }

    fn visit_rule(&mut self, _: RuleNode<'_>, when: Condition, then: RuleAction) -> Result<PricingRule, String> {
        Ok(PricingRule { when, then })
    }

    fn visit_any_of(&mut self, _: AnyOfNode<'_>, left: Condition, right: Condition) -> Result<Condition, String> {
        Ok(Condition::AnyOf(Box::new(left), Box::new(right)))
    }

    fn visit_all_of(&mut self, _: AllOfNode<'_>, left: Condition, right: Condition) -> Result<Condition, String> {
        Ok(Condition::AllOf(Box::new(left), Box::new(right)))
    }

    fn visit_comparison(
        &mut self,
        _: ComparisonNode<'_>,
        field: OrderField,
        op: ComparisonOperator,
        value: Operand,
    ) -> Result<Condition, String> {
        Ok(Condition::Comparison(field, op, value))
    }

    fn visit_amount_operand(&mut self, _: AmountOperandNode<'_>, amount: i64) -> Result<Operand, String> {
        Ok(Operand::Amount(amount))
    }

    fn visit_count_operand(&mut self, _: CountOperandNode<'_>, count: u32) -> Result<Operand, String> {
        Ok(Operand::Count(count))
    }

    fn visit_tier_operand(&mut self, _: TierOperandNode<'_>, tier: CustomerTier) -> Result<Operand, String> {
        Ok(Operand::Tier(tier))
    }

    fn visit_percent_discount(&mut self, _: PercentDiscountNode<'_>, percentage: i64) -> Result<RuleAction, String> {
        Ok(RuleAction::PercentDiscount(percentage))
    }

    fn visit_free_shipping(&mut self, _: FreeShippingNode<'_>) -> Result<RuleAction, String> {
        Ok(RuleAction::FreeShipping)
    }

    fn visit_require_approval(&mut self, _: RequireApprovalNode<'_>, role: String) -> Result<RuleAction, String> {
        Ok(RuleAction::RequireApproval(role))
    }

    // Terminals

    fn visit_text_token(&mut self, token: &Token) -> Result<String, String> {
        Ok(token.value[1..token.value.len() - 1].replace("\\\"", "\""))
    }

    fn visit_count_token(&mut self, token: &Token) -> Result<u32, String> {
        token.value.parse().map_err(|e| format!("{e}"))
    }

    /// `1299.00` → 129900 cents, `12.5%` → 1250 hundredths of a percent.
    fn visit_amount_token(&mut self, token: &Token) -> Result<i64, String> {
        let text = token.value.trim_end_matches('%');
        let (whole, fraction) = text.split_once('.').unwrap_or((text, ""));
        let fraction = format!("{fraction:0<2}");
        Ok(whole.parse::<i64>().map_err(|e| e.to_string())? * 100 + fraction[..2].parse::<i64>().map_err(|e| e.to_string())?)
    }

    fn visit_percent_token(&mut self, token: &Token) -> Result<i64, String> {
        self.visit_amount_token(token)
    }

    fn visit_tier_token(&mut self, token: &Token) -> Result<CustomerTier, String> {
        Ok(match token.value.as_str() {
            "gold" => CustomerTier::Gold,
            "silver" => CustomerTier::Silver,
            _ => CustomerTier::Bronze,
        })
    }

    fn visit_currency_token(&mut self, token: &Token) -> Result<Currency, String> {
        Ok(match token.value.as_str() {
            "EUR" => Currency::Eur,
            "USD" => Currency::Usd,
            _ => Currency::Gbp,
        })
    }

    fn visit_field_token(&mut self, token: &Token) -> Result<OrderField, String> {
        Ok(match token.value.as_str() {
            "order.total" => OrderField::OrderTotal,
            "line.quantity" => OrderField::LineQuantity,
            _ => OrderField::CustomerTier,
        })
    }

    fn visit_op_token(&mut self, token: &Token) -> Result<ComparisonOperator, String> {
        Ok(match token.value.as_str() {
            "=" => ComparisonOperator::Equal,
            "!=" => ComparisonOperator::NotEqual,
            "<" => ComparisonOperator::Less,
            "<=" => ComparisonOperator::LessOrEqual,
            ">" => ComparisonOperator::Greater,
            _ => ComparisonOperator::GreaterOrEqual,
        })
    }
}

fn build(text: &str) -> Result<PurchaseOrder, String> {
    let tree = parse(text).map_err(|e| e.to_string())?;
    Builder.visit_order_tree(&tree)
}

const ORDER: &str = r#"
order "PO-2026-0142"
customer "ACME Corp" (gold)
currency EUR
line "LT-14" "Laptop 14 inch" qty 5 at 1299.00
line "DS-02" "Docking station" qty 5 at 189.50 discount 10%
ship express to "Keizersgracht 1" "1015 CS Amsterdam" "NL"
rule when order.total > 5000.00 and customer.tier = gold then discount 5%
rule when line.quantity >= 50 or (order.total >= 20000.00) then require approval by "finance"
"#;

fn main() {
    let order = build(ORDER).unwrap();
    let total: i64 = order.lines.iter().map(OrderLine::total).sum();
    println!(
        "{} for {}: {} lines, total {}.{:02} {:?}",
        order.id,
        order.customer.name,
        order.lines.len(),
        total / 100,
        total % 100,
        order.currency
    );

    assert_eq!(total, 734_775);
    assert_eq!(
        order.customer,
        Customer {
            name: "ACME Corp".into(),
            tier: CustomerTier::Gold
        }
    );
    assert_eq!(
        order.shipping,
        Some(Shipping::Express(vec![
            "Keizersgracht 1".into(),
            "1015 CS Amsterdam".into(),
            "NL".into()
        ]))
    );
    assert_eq!(order.rules[0].then, RuleAction::PercentDiscount(500));
    assert!(matches!(order.rules[0].when, Condition::AllOf(..)));
    assert_eq!(order.rules[1].then, RuleAction::RequireApproval("finance".into()));

    let error = build(r#"order "PO-1" customer "B" currency USD line "A" "B" qty 0 at 1.00"#).unwrap_err();
    assert_eq!(error, "Order line A has no quantity.");
}
