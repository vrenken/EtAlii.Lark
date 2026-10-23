//! A post-lexer for Python-style indentation (Lark's `indenter`).

use std::collections::VecDeque;

use crate::error::{Error, Result};
use crate::lexer::{PostLex, TokenStream};
use crate::token::Token;

/// Injects indent and dedent tokens based on the indentation that follows each newline token. Inside
/// parentheses the indentation is ignored, and no indent or dedent tokens are generated (Lark's `Indenter`).
#[derive(Clone, Debug)]
pub struct Indenter {
    /// The name of the newline token.
    pub nl_type: String,
    /// The names of the tokens that open a parenthesis.
    pub open_paren_types: Vec<String>,
    /// The names of the tokens that close a parenthesis.
    pub close_paren_types: Vec<String>,
    /// The name of the token that starts an indentation in the grammar (see `%declare`).
    pub indent_type: String,
    /// The name of the token that ends an indentation in the grammar.
    pub dedent_type: String,
    /// How many spaces a tab equals.
    pub tab_len: usize,
}

impl Indenter {
    /// The indenter for Python's syntax (Lark's `PythonIndenter`).
    pub fn python() -> Indenter {
        Indenter {
            nl_type: "_NEWLINE".into(),
            open_paren_types: vec!["LPAR".into(), "LSQB".into(), "LBRACE".into()],
            close_paren_types: vec!["RPAR".into(), "RSQB".into(), "RBRACE".into()],
            indent_type: "_INDENT".into(),
            dedent_type: "_DEDENT".into(),
            tab_len: 8,
        }
    }
}

struct IndenterStream<'a> {
    conf: &'a Indenter,
    inner: TokenStream<'a>,
    paren_level: usize,
    indent_level: Vec<usize>,
    pending: VecDeque<Result<Token>>,
    last: Option<Token>,
    done: bool,
}

impl IndenterStream<'_> {
    fn handle_nl(&mut self, token: Token) {
        if self.paren_level > 0 {
            return;
        }
        let indent_str = token.value.rsplit('\n').next().unwrap_or("").to_string(); // Tabs and spaces
        let indent = indent_str.matches(' ').count() + indent_str.matches('\t').count() * self.conf.tab_len;
        let borrow = token.clone();
        self.pending.push_back(Ok(token));
        if indent > *self.indent_level.last().unwrap() {
            self.indent_level.push(indent);
            self.pending
                .push_back(Ok(Token::new_borrow_pos(self.conf.indent_type.clone(), indent_str, &borrow)));
        } else {
            while indent < *self.indent_level.last().unwrap() {
                self.indent_level.pop();
                self.pending.push_back(Ok(Token::new_borrow_pos(
                    self.conf.dedent_type.clone(),
                    indent_str.clone(),
                    &borrow,
                )));
            }
            let expected = *self.indent_level.last().unwrap();
            if indent != expected {
                self.pending.push_back(Err(Error::Dedent(format!(
                    "Unexpected dedent to column {}. Expected dedent to {}",
                    indent, expected
                ))));
            }
        }
    }
}

impl Iterator for IndenterStream<'_> {
    type Item = Result<Token>;

    fn next(&mut self) -> Option<Result<Token>> {
        loop {
            if let Some(t) = self.pending.pop_front() {
                if t.is_err() {
                    self.done = true;
                    self.pending.clear();
                }
                return Some(t);
            }
            if self.done {
                return None;
            }
            match self.inner.next() {
                Some(Err(e)) => {
                    self.done = true;
                    return Some(Err(e));
                }
                Some(Ok(token)) => {
                    self.last = Some(token.clone());
                    let kind = token.kind.clone();
                    if kind == self.conf.nl_type {
                        self.handle_nl(token);
                    } else {
                        self.pending.push_back(Ok(token));
                    }
                    if self.conf.open_paren_types.contains(&kind) {
                        self.paren_level += 1;
                    } else if self.conf.close_paren_types.contains(&kind) {
                        self.paren_level = self.paren_level.saturating_sub(1);
                    }
                }
                None => {
                    self.done = true;
                    while self.indent_level.len() > 1 {
                        self.indent_level.pop();
                        let t = match &self.last {
                            Some(last) => Token::new_borrow_pos(self.conf.dedent_type.clone(), "", last),
                            None => Token {
                                kind: self.conf.dedent_type.clone(),
                                start_pos: Some(0),
                                line: Some(0),
                                column: Some(0),
                                end_line: Some(0),
                                end_column: Some(0),
                                end_pos: Some(0),
                                ..Default::default()
                            },
                        };
                        self.pending.push_back(Ok(t));
                    }
                }
            }
        }
    }
}

impl PostLex for Indenter {
    fn process<'a>(&'a self, stream: TokenStream<'a>) -> TokenStream<'a> {
        Box::new(IndenterStream {
            conf: self,
            inner: stream,
            paren_level: 0,
            indent_level: vec![0],
            pending: VecDeque::new(),
            last: None,
            done: false,
        })
    }

    fn always_accept(&self) -> Vec<String> {
        vec![self.nl_type.clone()]
    }
}
