//! A typed visitor that builds a JSON value, with a `?rule` mapped as a whole.

use etalii_lark::Token;
use etalii_lark_derive::{lark_grammar, lark_result};

#[derive(Debug, PartialEq)]
enum Json {
    Null,
    Bool(bool),
    Number(f64),
    String(String),
    Array(Vec<Json>),
    Object(Vec<(String, Json)>),
}

#[lark_grammar(file = "../../conformance/grammars/json.lark", parser = "lalr")]
#[lark_result(Json, "value", "array", "object", "string", "number", "true", "false", "null")]
#[lark_result((String, Json), "pair")]
mod json {}

struct Builder;

impl json::Visitor for Builder {
    type Error = std::convert::Infallible;

    fn visit_array(&mut self, _: json::ArrayNode<'_>, value: Option<Json>, values: Vec<Json>) -> Result<Json, Self::Error> {
        Ok(Json::Array(value.into_iter().chain(values).collect()))
    }

    fn visit_object(
        &mut self,
        _: json::ObjectNode<'_>,
        pair: Option<(String, Json)>,
        pairs: Vec<(String, Json)>,
    ) -> Result<Json, Self::Error> {
        Ok(Json::Object(pair.into_iter().chain(pairs).collect()))
    }

    fn visit_pair(&mut self, _: json::PairNode<'_>, string: Json, value: Json) -> Result<(String, Json), Self::Error> {
        let Json::String(key) = string else { unreachable!() };
        Ok((key, value))
    }

    fn visit_string(&mut self, _: json::StringNode<'_>, escaped_string: &Token) -> Result<Json, Self::Error> {
        let v = &escaped_string.value;
        Ok(Json::String(v[1..v.len() - 1].to_string()))
    }

    fn visit_number(&mut self, _: json::NumberNode<'_>, signed_number: &Token) -> Result<Json, Self::Error> {
        Ok(Json::Number(signed_number.value.parse().unwrap()))
    }

    fn visit_true(&mut self, _: json::TrueNode<'_>) -> Result<Json, Self::Error> {
        Ok(Json::Bool(true))
    }

    fn visit_false(&mut self, _: json::FalseNode<'_>) -> Result<Json, Self::Error> {
        Ok(Json::Bool(false))
    }

    fn visit_null(&mut self, _: json::NullNode<'_>) -> Result<Json, Self::Error> {
        Ok(Json::Null)
    }
}

#[test]
fn builds_values() {
    use json::Visitor;
    let tree = json::parse(r#"{"a": [1, true, null], "b": {}, "c": [], "d": "x"}"#).unwrap();
    let value = Builder.visit_start(&tree).unwrap();
    assert_eq!(
        value,
        Json::Object(vec![
            ("a".into(), Json::Array(vec![Json::Number(1.0), Json::Bool(true), Json::Null])),
            ("b".into(), Json::Object(vec![])),
            ("c".into(), Json::Array(vec![])),
            ("d".into(), Json::String("x".into())),
        ])
    );
}

#[test]
fn has_typed_accessors() {
    let tree = json::parse(r#"{"a": 1, "b": 2}"#).unwrap();
    let object = json::ObjectNode::new(&tree).unwrap();
    let first = object.pair().unwrap();
    assert_eq!(first.string().escaped_string().value, "\"a\"");
    assert!(matches!(first.value(), json::Value::Number(n) if n.signed_number().value == "1"));
    assert_eq!(object.pairs().len(), 1);
}
