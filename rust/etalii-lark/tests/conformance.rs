//! Runs the language-neutral conformance suite in `conformance/cases`, whose expected results were produced by
//! Python Lark. The .NET and TypeScript packages run the same cases, so all ports interpret grammars identically.

use std::collections::{BTreeMap, BTreeSet};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::sync::Arc;

use etalii_lark::{Ambiguity, Error, Indenter, Lark, LarkOptions, LexerKind, Node, ParserKind, PriorityMode, Token, Tree};
use serde_json::{json, Map, Value};

fn conformance_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance")
}

fn load_cases() -> BTreeMap<String, Value> {
    let mut cases = BTreeMap::new();
    let mut files: Vec<_> = std::fs::read_dir(conformance_dir().join("cases"))
        .expect("conformance/cases")
        .map(|e| e.unwrap().path())
        .filter(|p| p.extension().is_some_and(|x| x == "json"))
        .collect();
    files.sort();
    for file in files {
        let root: Value = serde_json::from_str(&std::fs::read_to_string(&file).unwrap()).unwrap();
        for c in root["cases"].as_array().unwrap() {
            cases.insert(c["id"].as_str().unwrap().to_string(), c.clone());
        }
    }
    cases
}

fn load_known_failures(port: &str) -> BTreeSet<String> {
    let path = conformance_dir().join("known-failures").join(format!("{port}.txt"));
    let Ok(text) = std::fs::read_to_string(path) else {
        return BTreeSet::new();
    };
    text.lines()
        .map(|l| l.split('#').next().unwrap().trim())
        .filter(|l| !l.is_empty())
        .map(|l| l.split_whitespace().next().unwrap().to_string())
        .collect()
}

#[test]
fn conformance() {
    let cases = load_cases();
    let known = load_known_failures("rust");
    let filter = std::env::var("CONFORMANCE_FILTER").ok();
    let mut problems = Vec::new();
    let mut failing = 0;
    for (id, c) in &cases {
        if filter.as_deref().is_some_and(|f| !id.contains(f)) {
            continue;
        }
        let failures = match catch_unwind(AssertUnwindSafe(|| run_case(c))) {
            Ok(f) => f,
            Err(e) => vec![format!(
                "Panicked: {}",
                e.downcast_ref::<String>()
                    .cloned()
                    .or_else(|| e.downcast_ref::<&str>().map(|s| s.to_string()))
                    .unwrap_or_default()
            )],
        };
        if !failures.is_empty() {
            failing += 1;
        }
        if known.contains(id) {
            if failures.is_empty() {
                problems.push(format!("{id}: passes now; remove it from conformance/known-failures/rust.txt."));
            }
        } else if !failures.is_empty() {
            problems.push(format!("{id}:\n{}", failures.join("\n\n")));
        }
    }
    eprintln!("{} cases, {} failing, {} unexpected results", cases.len(), failing, problems.len());
    assert!(problems.is_empty(), "{}", problems.join("\n\n"));
}

fn run_case(c: &Value) -> Vec<String> {
    let mut failures = Vec::new();
    let grammar = c["grammar"].as_str().unwrap();
    let grammar_error = c.get("grammar_error").and_then(Value::as_str);
    let parser = match options(c["options"].as_object().unwrap()).and_then(|o| Lark::new(grammar, o)) {
        Ok(p) => p,
        Err(e) => {
            match grammar_error {
                Some(expected) if e.lark_name() == expected => {}
                Some(expected) => failures.push(format!("Expected grammar error {expected}, got {}: {e}", e.lark_name())),
                None => failures.push(format!("The grammar failed to load: {}: {e}", e.lark_name())),
            }
            return failures;
        }
    };
    if let Some(expected) = grammar_error {
        failures.push(format!("Expected grammar error {expected}, but the grammar loaded."));
        return failures;
    }

    for test in c["tests"].as_array().unwrap() {
        let expect = &test["expect"];
        let is_lex = test.get("lex").is_some();
        let text = if is_lex {
            test["lex"].as_str().unwrap()
        } else {
            test["input"].as_str().unwrap()
        };
        let positions = Positions::new(text);
        let actual = if is_lex {
            let dont_ignore = test.get("dont_ignore").and_then(Value::as_bool).unwrap_or(false);
            match parser.lex(text, dont_ignore) {
                Ok(tokens) => json!({"tokens": tokens.iter().map(|t| token_json(t, &positions)).collect::<Vec<_>>()}),
                Err(e) => error_json(&e),
            }
        } else {
            match parser.parse_start(text, test.get("start").and_then(Value::as_str)) {
                Ok(tree) => json!({"tree": tree_json(&tree, &positions)}),
                Err(e) => error_json(&e),
            }
        };
        if *expect != actual {
            failures.push(format!("Input {}:\n  expected {}\n  actual   {}", show(text), expect, actual));
        }
    }
    failures
}

fn options(options: &Map<String, Value>) -> etalii_lark::Result<LarkOptions> {
    // Positions are always propagated, as when the expected results were generated, and compared too.
    let mut result = LarkOptions {
        propagate_positions: true,
        ..Default::default()
    };
    for (name, value) in options {
        match name.as_str() {
            "parser" => {
                result.parser = match value.as_str().unwrap() {
                    "lalr" => ParserKind::Lalr,
                    "cyk" => ParserKind::Cyk,
                    _ => ParserKind::Earley,
                }
            }
            "lexer" => {
                result.lexer = match value.as_str().unwrap() {
                    "basic" => LexerKind::Basic,
                    "contextual" => LexerKind::Contextual,
                    "dynamic" => LexerKind::Dynamic,
                    "dynamic_complete" => LexerKind::DynamicComplete,
                    _ => LexerKind::Auto,
                }
            }
            "start" => {
                result.start = match value {
                    Value::Array(a) => a.iter().map(|s| s.as_str().unwrap().to_string()).collect(),
                    v => vec![v.as_str().unwrap().to_string()],
                }
            }
            "ambiguity" => {
                result.ambiguity = match value.as_str().unwrap() {
                    "resolve" => Ambiguity::Resolve,
                    "explicit" => Ambiguity::Explicit,
                    _ => Ambiguity::Auto,
                }
            }
            "priority" => {
                result.priority = match value.as_str() {
                    Some("normal") => PriorityMode::Normal,
                    Some("invert") => PriorityMode::Invert,
                    None => PriorityMode::None,
                    _ => PriorityMode::Auto,
                }
            }
            "maybe_placeholders" => result.maybe_placeholders = value.as_bool().unwrap(),
            "keep_all_tokens" => result.keep_all_tokens = value.as_bool().unwrap(),
            "strict" => result.strict = value.as_bool().unwrap(),
            "regex_flags" => result.regex_flags = value.as_str().unwrap().to_string(),
            "postlex" => match value.as_str().unwrap() {
                "python_indenter" => result.postlex = Some(Arc::new(Indenter::python())),
                other => panic!("Unknown post-lexer {other}"),
            },
            other => panic!("Unknown conformance option {other}"),
        }
    }
    Ok(result)
}

fn sorted<'a>(values: impl IntoIterator<Item = &'a String>) -> Value {
    let set: BTreeSet<&String> = values.into_iter().collect();
    json!(set)
}

fn error_json(e: &Error) -> Value {
    let mut result = Map::new();
    result.insert("error".into(), json!(e.lark_name()));
    match e {
        Error::UnexpectedToken(u) => {
            result.insert("line".into(), line_json(u.token.line.map(|_| u.line)));
            result.insert("column".into(), line_json(u.token.line.map(|_| u.column)));
            result.insert("token".into(), json!({"type": u.token.kind, "value": u.token.value}));
            result.insert("expected".into(), sorted(&u.expected));
        }
        Error::UnexpectedEof(u) => {
            result.insert("line".into(), json!(-1));
            result.insert("column".into(), json!(-1));
            result.insert("expected".into(), sorted(&u.expected));
        }
        Error::UnexpectedCharacters(u) => {
            result.insert("line".into(), json!(u.line));
            result.insert("column".into(), json!(u.column));
            result.insert("char".into(), json!(u.char.to_string()));
            if let Some(allowed) = &u.allowed {
                result.insert("expected".into(), sorted(allowed));
            }
        }
        Error::Lex(_) | Error::Parse(_) | Error::Grammar(_) | Error::Configuration(_) => {}
        other => {
            result.insert("error".into(), json!(format!("Exception:{}: {other}", other.lark_name())));
        }
    }
    Value::Object(result)
}

fn line_json(value: Option<usize>) -> Value {
    value.map_or(Value::Null, |v| json!(v))
}

fn tree_json(tree: &Tree, positions: &Positions) -> Value {
    let mut result = Map::new();
    result.insert("data".into(), json!(tree.data));
    result.insert(
        "children".into(),
        Value::Array(tree.children.iter().map(|c| node_json(c, positions)).collect()),
    );
    let meta = tree.meta();
    if !meta.empty {
        result.insert(
            "meta".into(),
            json!([
                positions.offset(meta.start_pos),
                positions.offset(meta.end_pos),
                meta.line,
                meta.column,
                meta.end_line,
                meta.end_column
            ]),
        );
    }
    Value::Object(result)
}

fn node_json(node: &Node, positions: &Positions) -> Value {
    match node {
        Node::Tree(t) => tree_json(t, positions),
        Node::Token(t) => token_json(t, positions),
        Node::Missing => Value::Null,
    }
}

fn token_json(token: &Token, positions: &Positions) -> Value {
    json!({
        "type": token.kind,
        "value": token.value,
        "pos": [
            positions.offset(token.start_pos),
            positions.offset(token.end_pos),
            token.line,
            token.column,
            token.end_line,
            token.end_column
        ],
    })
}

/// Lark counts offsets in code points; Rust in bytes. Columns are already counted in characters.
struct Positions {
    chars: Vec<usize>,
}

impl Positions {
    fn new(text: &str) -> Positions {
        let mut chars = vec![0; text.len() + 1];
        let mut count = 0;
        for (i, ch) in text.char_indices() {
            for b in 0..ch.len_utf8() {
                chars[i + b] = count;
            }
            count += 1;
        }
        chars[text.len()] = count;
        Positions { chars }
    }

    fn offset(&self, offset: Option<usize>) -> Option<usize> {
        offset.map(|o| self.chars.get(o).copied().unwrap_or(o))
    }
}

fn show(text: &str) -> String {
    if text.chars().count() > 200 {
        format!("{}...", json!(text.chars().take(200).collect::<String>()))
    } else {
        json!(text).to_string()
    }
}
