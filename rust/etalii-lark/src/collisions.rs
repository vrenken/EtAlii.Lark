//! Detection of terminals that can match the same text, for strict mode.
//!
//! Lark uses the `interegular` package to intersect the regular expressions. This samples strings from each
//! expression instead, and checks whether a sample fully matches both terminals: a reported collision is always
//! real, but some collisions may go unnoticed.

use crate::error::{Error, Result};
use crate::lexer::{compile_terminal, TerminalDef};
use crate::pyre::{Ast, Flags};
use crate::utils::{classify, repr};

pub(crate) fn check(terminals: &[TerminalDef], flags: Flags) -> Result<()> {
    let regex_terminals: Vec<&TerminalDef> = terminals.iter().filter(|t| !t.pattern.is_str()).collect();
    for group in classify(regex_terminals, |t| t.priority, |t| t).values() {
        for i in 0..group.len() {
            for j in i + 1..group.len() {
                let (a, b) = (group[i], group[j]);
                if let Some(example) = find_overlap(a, b, flags)?.or(find_overlap(b, a, flags)?) {
                    return Err(Error::Lex(format!(
                        "Collision between Terminals {} and {}. \nExample of a string matched by both: {}",
                        a.name,
                        b.name,
                        repr(&example)
                    )));
                }
            }
        }
    }
    Ok(())
}

fn find_overlap(a: &TerminalDef, b: &TerminalDef, flags: Flags) -> Result<Option<String>> {
    let ast = match Ast::parse(&a.pattern.to_regexp(), flags) {
        Ok(ast) => ast,
        Err(_) => return Ok(None),
    };
    let ra = compile_terminal(a, flags)?;
    let rb = compile_terminal(b, flags)?;
    Ok(ast
        .samples()
        .into_iter()
        .find(|s| !s.is_empty() && ra.full_match(s) && rb.full_match(s)))
}
