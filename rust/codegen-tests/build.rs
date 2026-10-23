//! Generates the typed API of every grammar in the conformance suite twice: with all trees and terminals mapped to
//! `u64`, and with the terminals mapped to `String` instead, so that the typed visitor has values, nodes and enums.

use std::fmt::Write;
use std::path::Path;

use etalii_lark::codegen::Generator;
use etalii_lark::{LexerKind, ParserKind};
use serde_json::Value;

fn main() {
    let cases = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance/cases");
    println!("cargo:rerun-if-changed={}", cases.display());
    let mut files: Vec<_> = std::fs::read_dir(&cases).unwrap().map(|e| e.unwrap().path()).collect();
    files.sort();
    let out_dir = std::env::var("OUT_DIR").unwrap();
    let mut modules = String::new();
    let mut count = 0;
    // Cases that differ only in the parser or lexer give the same code, apart from the options.
    let mut seen = std::collections::BTreeSet::new();
    for file in files.iter().filter(|f| f.extension().is_some_and(|x| x == "json")) {
        let root: Value = serde_json::from_str(&std::fs::read_to_string(file).unwrap()).unwrap();
        for case in root["cases"].as_array().unwrap() {
            if case.get("grammar_error").is_some() || case["options"].get("postlex").is_some() {
                continue;
            }
            let id = case["id"].as_str().unwrap().replace('-', "_");
            let options = &case["options"];
            let key = (
                case["grammar"].to_string(),
                options.get("start").map(Value::to_string),
                options.get("maybe_placeholders").map(Value::to_string),
                options.get("keep_all_tokens").map(Value::to_string),
            );
            if !seen.insert(key) {
                continue;
            }
            let Some(generator) = generator(case) else { continue };
            if generator.clone().generate().is_err() {
                continue;
            }
            let typed = mapped(generator.clone(), case, "u64").unwrap_or_else(|| panic!("{id}: all names mapped to one type"));
            // Terminals of another type than the trees give typed enums, unless a mapped ?rule can be both.
            let mixed = mapped(generator, case, "String").unwrap_or_default();
            let typed_path = format!("{out_dir}/{id}_typed.rs");
            std::fs::write(&typed_path, typed).unwrap();
            let mixed_path = format!("{out_dir}/{id}_mixed.rs");
            std::fs::write(&mixed_path, mixed).unwrap();
            writeln!(
                modules,
                "pub mod {id} {{ pub mod typed {{ include!({typed_path:?}); }} pub mod mixed {{ include!({mixed_path:?}); }} }}"
            )
            .unwrap();
            count += 1;
        }
    }
    assert!(count > 150, "only {count} grammars generated code");
    std::fs::write(format!("{out_dir}/cases.rs"), modules).unwrap();
}

fn generator(case: &Value) -> Option<Generator> {
    let options = &case["options"];
    let mut generator = Generator::new(case["grammar"].as_str().unwrap());
    if let Some(parser) = options.get("parser").and_then(Value::as_str) {
        generator = generator.parser(match parser {
            "lalr" => ParserKind::Lalr,
            "cyk" => ParserKind::Cyk,
            _ => ParserKind::Earley,
        });
    }
    if let Some(lexer) = options.get("lexer").and_then(Value::as_str) {
        generator = generator.lexer(match lexer {
            "basic" => LexerKind::Basic,
            "contextual" => LexerKind::Contextual,
            "dynamic" => LexerKind::Dynamic,
            "dynamic_complete" => LexerKind::DynamicComplete,
            _ => LexerKind::Auto,
        });
    }
    if let Some(start) = options.get("start") {
        generator = match start {
            Value::Array(a) => generator.start(a.iter().map(|s| s.as_str().unwrap().to_string())),
            s => generator.start([s.as_str().unwrap().to_string()]),
        };
    }
    if let Some(v) = options.get("maybe_placeholders").and_then(Value::as_bool) {
        generator = generator.maybe_placeholders(v);
    }
    if let Some(v) = options.get("keep_all_tokens").and_then(Value::as_bool) {
        generator = generator.keep_all_tokens(v);
    }
    Some(generator)
}

/// All trees mapped to `u64`, all terminals that appear in trees to `token_type`.
fn mapped(generator: Generator, case: &Value, token_type: &str) -> Option<String> {
    let shapes = case["shapes"].as_object().unwrap();
    let trees: Vec<String> = shapes
        .iter()
        .filter(|(_, s)| s.as_str() != Some("inlined"))
        .map(|(n, _)| n.clone())
        .collect();
    let mut terminals = std::collections::BTreeSet::new();
    for shape in shapes.values() {
        for e in shape.as_array().into_iter().flatten() {
            for m in e["members"].as_array().unwrap() {
                let m = m.as_str().unwrap();
                if !m.starts_with('#') && !shapes.contains_key(m) {
                    terminals.insert(m.to_string());
                }
            }
        }
    }
    let mut generator = generator.result("u64", trees);
    if !terminals.is_empty() {
        generator = generator.result(token_type, terminals);
    }
    generator.generate().ok()
}
