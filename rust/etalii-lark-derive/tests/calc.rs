//! A typed visitor that evaluates the calculator grammar.

use std::collections::HashMap;

use etalii_lark::Token;
use etalii_lark_derive::{lark_grammar, lark_result};

#[lark_grammar(file = "../../conformance/grammars/calc.lark", parser = "lalr")]
#[lark_result(f64, "add", "sub", "mul", "div", "neg", "number", "var", "assign_var")]
mod calc {}

#[derive(Debug, PartialEq)]
struct Undefined(String);

#[derive(Default)]
struct Calculator {
    variables: HashMap<String, f64>,
}

impl calc::Visitor for Calculator {
    type Error = Undefined;

    fn visit_add(&mut self, _: calc::AddNode<'_>, sum: f64, product: f64) -> Result<f64, Undefined> {
        Ok(sum + product)
    }

    fn visit_sub(&mut self, _: calc::SubNode<'_>, sum: f64, product: f64) -> Result<f64, Undefined> {
        Ok(sum - product)
    }

    fn visit_mul(&mut self, _: calc::MulNode<'_>, product: f64, atom: f64) -> Result<f64, Undefined> {
        Ok(product * atom)
    }

    fn visit_div(&mut self, _: calc::DivNode<'_>, product: f64, atom: f64) -> Result<f64, Undefined> {
        Ok(product / atom)
    }

    fn visit_neg(&mut self, _: calc::NegNode<'_>, atom: f64) -> Result<f64, Undefined> {
        Ok(-atom)
    }

    fn visit_number(&mut self, _: calc::NumberNode<'_>, number: &Token) -> Result<f64, Undefined> {
        Ok(number.value.parse().unwrap())
    }

    fn visit_var(&mut self, _: calc::VarNode<'_>, name: &Token) -> Result<f64, Undefined> {
        self.variables
            .get(&name.value)
            .copied()
            .ok_or_else(|| Undefined(name.value.clone()))
    }

    fn visit_assign_var(&mut self, _: calc::AssignVarNode<'_>, name: &Token, sum: f64) -> Result<f64, Undefined> {
        self.variables.insert(name.value.clone(), sum);
        Ok(sum)
    }
}

fn eval(calculator: &mut Calculator, text: &str) -> Result<f64, Undefined> {
    use calc::Visitor;
    calculator.visit_start(&calc::parse(text).unwrap())
}

#[test]
fn evaluates() {
    let mut calculator = Calculator::default();
    assert_eq!(eval(&mut calculator, "1 + 2 * 3 - 4 / 8"), Ok(6.5));
    assert_eq!(eval(&mut calculator, "-(1 + 2) * 2"), Ok(-6.0));
    assert_eq!(eval(&mut calculator, "x = 2 * 21"), Ok(42.0));
    assert_eq!(eval(&mut calculator, "x / 2"), Ok(21.0));
    assert_eq!(eval(&mut calculator, "y + 1"), Err(Undefined("y".into())));
}

#[test]
fn visits_deep_trees_without_recursing() {
    let text = vec!["1"; 50_000].join(" + ");
    let tree = calc::parse(&text).unwrap();
    use calc::Visitor;
    assert_eq!(Calculator::default().visit_start(&tree), Ok(50_000.0));
    // Dropping a tree this deep recurses, which a debug build's stack can't take.
    std::mem::forget(tree);
}

#[test]
fn has_name_constants_and_node_wrappers() {
    assert_eq!(calc::rules::ADD, "add");
    assert_eq!(calc::terminals::NUMBER, "NUMBER");
    let tree = calc::parse("1 + 2").unwrap();
    let add = calc::AddNode::new(&tree).unwrap();
    assert!(calc::MulNode::new(&tree).is_none());
    match add.sum() {
        calc::Sum::Number(n) => assert_eq!(n.number().value, "1"),
        other => panic!("{other:?}"),
    }
    assert_eq!(add.data, "add");
}
