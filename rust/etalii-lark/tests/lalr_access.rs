//! The LALR parser's table and state, as Python Lark leaves them readable: `parse_table`, a parser state's stacks, and
//! the interactive parser's next token, for tools that print the automaton and recover from errors themselves.

use etalii_lark::{LalrAction, Lark, LexerKind, Node, ParserKind};

fn lalr(grammar: &str) -> Lark {
    Lark::builder(grammar)
        .parser(ParserKind::Lalr)
        .lexer(LexerKind::Contextual)
        .build()
        .unwrap()
}

const BLOCKS: &str = r#"
start: item*
item: NAME ";" | "{" item* "}"
NAME: /[a-z]+/
%ignore " "
"#;

#[test]
fn the_parse_table_is_the_parsers() {
    let parser = lalr(BLOCKS);
    let table = parser.parse_table().expect("a LALR parser has a table");
    let start = table.start_states["start"];
    // The start state shifts a name or an opening brace, and reduces the empty list of items on the end.
    let actions = &table.states[start];
    assert!(matches!(actions.get("NAME"), Some(LalrAction::Shift(_))));
    assert!(matches!(actions.get("LBRACE"), Some(LalrAction::Shift(_))));
    assert!(table.end_states.contains_key("start"));
    // Every shift goes to a state of the table, and every reduce names a rule of the grammar.
    let rules: Vec<String> = parser.rules().map(|r| r.to_string()).collect();
    for state in &table.states {
        for action in state.values() {
            match action {
                LalrAction::Shift(target) => assert!(*target < table.states.len()),
                LalrAction::Reduce(rule) => assert!(rules.contains(&rule.to_string()), "{rule}"),
            }
        }
    }

    let earley = Lark::builder(BLOCKS).parser(ParserKind::Earley).build().unwrap();
    assert!(earley.parse_table().is_none());
}

#[test]
fn the_interactive_parser_lexes_one_token_for_its_state() {
    let parser = lalr("start: \"if\" NAME\nNAME: /[a-z]+/\n%ignore \" \"\n");
    let mut interactive = parser.parse_interactive("if if", None).unwrap();
    // The first `if` is the keyword; in the state after it only a name is read, so the second is a name.
    let first = interactive.next_token().unwrap().unwrap();
    assert_eq!((first.kind.as_str(), first.value.as_str()), ("IF", "if"));
    interactive.feed_token(first).unwrap();
    let second = interactive.next_token().unwrap().unwrap();
    assert_eq!((second.kind.as_str(), second.start_pos), ("NAME", Some(3)));
    interactive.feed_token(second).unwrap();
    assert!(interactive.next_token().unwrap().is_none());
    assert!(interactive.feed_eof(None).unwrap().is_some());
}

#[test]
fn a_parser_state_is_read_and_popped_for_recovery() {
    let parser = lalr(BLOCKS);
    let mut interactive = parser.parse_interactive("a; { b; c ; } d;", None).unwrap();
    for _ in 0..4 {
        let token = interactive.next_token().unwrap().unwrap();
        interactive.feed_token(token).unwrap();
    }
    // `a ;` reduced to an item, `{` and `b` shifted: four states above the start, three values.
    let state = interactive.parser_state();
    assert_eq!(state.state_stack().len(), state.value_stack().len() + 1);
    assert!(matches!(state.value_stack().last(), Some(Node::Token(t)) if t.value == "b"));

    // Pop back to the state that read `{`, as a recovery that drops the half-read item would.
    let depth = interactive.parser_state().depth();
    let popped = interactive.parser_state_mut().truncate(depth - 1);
    assert_eq!(popped.len(), 1);
    assert!(matches!(&popped[0], Node::Token(t) if t.value == "b"));
    assert_eq!(interactive.parser_state().depth(), depth - 1);
    // The state that read `{` does not read the `;` that followed `b`: the lexer refuses it, and skipping it the
    // parse goes on with `c ; } d ;`.
    assert!(interactive.next_token().is_err());
    interactive.skip_chars(1);
    let rest = interactive.resume_parse();
    assert!(rest.is_ok(), "{rest:?}");

    // The start state is never popped.
    assert_eq!(interactive.parser_state_mut().truncate(0).len(), 0);
    assert_eq!(interactive.parser_state().depth(), 1);
}
