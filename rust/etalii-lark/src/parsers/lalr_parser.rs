//! The LALR(1) parser and its interactive mode (Lark's `lalr_parser`, `lalr_parser_state` and
//! `lalr_interactive_parser`).

use std::cell::Cell;
use std::collections::BTreeSet;
use std::rc::Rc;
use std::sync::Arc;

use indexmap::IndexMap;

use super::lalr_analysis::{Action, ParseTable};
use crate::error::{Error, ErrorState, Result, UnexpectedToken};
use crate::lexer::{LexerThread, SharedParserState};
use crate::parse_tree_builder::Callbacks;
use crate::token::Token;
use crate::tree::Node;
use crate::utils::is_upper;

/// The LALR parser of a grammar: its parse table and tree builders.
#[derive(Clone)]
pub(crate) struct LalrParser {
    pub table: Arc<ParseTable>,
    pub callbacks: Arc<Callbacks>,
}

/// The state of a LALR parse: the stack of states and the stack of values (Lark's `ParserState`).
#[derive(Clone, Debug)]
pub struct ParserState {
    pub(crate) state_stack: Vec<usize>,
    pub(crate) value_stack: Vec<Node>,
    end_state: usize,
}

impl ParserState {
    /// The current state of the parser.
    pub fn position(&self) -> usize {
        *self.state_stack.last().unwrap()
    }

    /// The depth of the state stack.
    pub fn depth(&self) -> usize {
        self.state_stack.len()
    }

    /// The stack of states, the start state first (Lark's `ParserState.state_stack`).
    pub fn state_stack(&self) -> &[usize] {
        &self.state_stack
    }

    /// The stack of values: the tokens shifted and the trees reduced, one per state above the start state (Lark's
    /// `ParserState.value_stack`).
    pub fn value_stack(&self) -> &[Node] {
        &self.value_stack
    }

    /// Pops the stacks until `depth` states are left, at least the start state, and returns the values popped, the
    /// lowest first: what error recovery does to resume at a state lower on the stack, as Lark's `on_error` handlers
    /// do through `state_stack` and `value_stack`.
    pub fn truncate(&mut self, depth: usize) -> Vec<Node> {
        let depth = depth.max(1).min(self.state_stack.len());
        self.state_stack.truncate(depth);
        let values = depth - 1;
        if values < self.value_stack.len() {
            self.value_stack.split_off(values)
        } else {
            Vec::new()
        }
    }

    pub(crate) fn error_state(&self) -> ErrorState {
        ErrorState::Lalr {
            depth: self.depth(),
            position: self.position(),
        }
    }
}

impl PartialEq for ParserState {
    /// Like Lark, compares the depth and the current state.
    fn eq(&self, other: &Self) -> bool {
        self.depth() == other.depth() && self.position() == other.position()
    }
}

impl LalrParser {
    pub fn new_state(&self, start: &str) -> Result<ParserState> {
        let start_state = *self
            .table
            .start_states
            .get(start)
            .ok_or_else(|| Error::config(format!("Unknown start rule {}", start)))?;
        Ok(ParserState {
            state_stack: vec![start_state],
            value_stack: Vec::new(),
            end_state: self.table.end_states[start],
        })
    }

    /// Feeds one token; returns the result when `is_end` and the parse is complete. Without callbacks (used to
    /// probe which tokens are accepted), no trees are built.
    pub fn feed_token(&self, ps: &mut ParserState, token: Token, is_end: bool, with_callbacks: bool) -> Result<Option<Node>> {
        loop {
            let state = ps.position();
            let action = match self.table.states[state].get(&token.kind) {
                Some(a) => a,
                None => {
                    let expected: BTreeSet<String> = self.table.states[state].keys().filter(|k| is_upper(k)).cloned().collect();
                    return Err(UnexpectedToken::new(token, expected, Some(ps.error_state()), None).into());
                }
            };
            match action {
                Action::Shift(next) => {
                    ps.state_stack.push(*next);
                    ps.value_stack.push(Node::Token(token));
                    return Ok(None);
                }
                Action::Reduce(rule) => {
                    let size = rule.expansion.len();
                    let children: Vec<Node> = if size > 0 {
                        ps.state_stack.truncate(ps.state_stack.len() - size);
                        ps.value_stack.split_off(ps.value_stack.len() - size)
                    } else {
                        Vec::new()
                    };
                    let value = if with_callbacks {
                        self.callbacks.build(rule, children)
                    } else {
                        Node::Missing
                    };
                    let goto = match self.table.states[ps.position()].get(&*rule.origin.name) {
                        Some(Action::Shift(s)) => *s,
                        _ => unreachable!("goto must be a shift"),
                    };
                    ps.state_stack.push(goto);
                    ps.value_stack.push(value);
                    if is_end && ps.position() == ps.end_state {
                        return Ok(ps.value_stack.pop());
                    }
                }
            }
        }
    }

    /// The terminals that the parser accepts in this state (Lark's `InteractiveParser.accepts`).
    pub fn accepts(&self, ps: &ParserState) -> BTreeSet<String> {
        let mut accepts = BTreeSet::new();
        for t in self.table.states[ps.position()].keys() {
            if !is_upper(t) {
                continue;
            }
            let mut copy = ParserState {
                state_stack: ps.state_stack.clone(),
                value_stack: vec![Node::Missing; ps.value_stack.len()],
                end_state: ps.end_state,
            };
            if self.feed_token(&mut copy, Token::new(t.clone(), ""), t == "$END", false).is_ok() {
                accepts.insert(t.clone());
            }
        }
        accepts
    }

    /// Runs the parser on the tokens of the lexer until the end (Lark's `parse_from_state`).
    pub fn parse_from_state(&self, ps: &mut ParserState, lexer: &LexerThread<'_>, last_token: Option<Token>) -> Result<Node> {
        let shared: SharedParserState = Rc::new(Cell::new((ps.depth(), ps.position())));
        let mut stream = lexer.lex(Some(shared.clone()));
        let mut token = last_token;
        let result = (|| {
            loop {
                shared.set((ps.depth(), ps.position()));
                let t = match stream.next() {
                    None => break,
                    Some(t) => t?,
                };
                token = Some(t.clone());
                self.feed_token(ps, t, false, true)?;
            }
            let end = match &token {
                Some(t) => Token::new_borrow_pos("$END", "", t),
                None => Token {
                    kind: "$END".into(),
                    value: String::new(),
                    start_pos: Some(0),
                    line: Some(1),
                    column: Some(1),
                    ..Default::default()
                },
            };
            Ok(self.feed_token(ps, end, true, true)?.unwrap_or(Node::Missing))
        })();
        result.map_err(|e| self.complete_error(e, ps, lexer))
    }

    /// Adds what the parser knows to an error: the accepted terminals and the terminal names.
    pub fn complete_error(&self, e: Error, ps: &ParserState, lexer: &LexerThread<'_>) -> Error {
        match e {
            Error::UnexpectedToken(mut ut) => {
                if ut.terminals_by_name.is_none() {
                    ut.terminals_by_name = lexer.lexer.terminals_by_name();
                }
                ut.accepts = Some(self.accepts(ps));
                Error::UnexpectedToken(ut)
            }
            other => other,
        }
    }

    pub fn choices(&self, ps: &ParserState) -> &IndexMap<String, Action> {
        &self.table.states[ps.position()]
    }
}

/// Gives control over a LALR parse, for error recovery and for feeding tokens by hand (Lark's
/// `InteractiveParser`).
pub struct InteractiveParser<'a> {
    pub(crate) parser: &'a LalrParser,
    pub(crate) state: ParserState,
    pub(crate) lexer: LexerThread<'a>,
    /// The result of the parse, once the end token was fed.
    pub result: Option<Node>,
}

impl<'a> InteractiveParser<'a> {
    pub(crate) fn new(parser: &'a LalrParser, state: ParserState, lexer: LexerThread<'a>) -> InteractiveParser<'a> {
        InteractiveParser {
            parser,
            state,
            lexer,
            result: None,
        }
    }

    /// Feeds a token, as if it came from the lexer. Returns the result when the token is `$END`.
    pub fn feed_token(&mut self, token: Token) -> Result<Option<Node>> {
        let is_end = token.kind == "$END";
        let result = self
            .parser
            .feed_token(&mut self.state, token, is_end, true)
            .map_err(|e| self.parser.complete_error(e, &self.state, &self.lexer))?;
        if result.is_some() {
            self.result = result.clone();
        }
        Ok(result)
    }

    /// Feeds the rest of the text, without the end token. Returns the tokens.
    pub fn exhaust_lexer(&mut self) -> Result<Vec<Token>> {
        let shared: SharedParserState = Rc::new(Cell::new((self.state.depth(), self.state.position())));
        let mut stream = self.lexer.lex(Some(shared.clone()));
        let mut tokens = Vec::new();
        loop {
            shared.set((self.state.depth(), self.state.position()));
            match stream.next() {
                None => break,
                Some(t) => {
                    let t = t?;
                    tokens.push(t.clone());
                    self.feed_token(t)?;
                }
            }
        }
        Ok(tokens)
    }

    /// Feeds the end token, with the position of `last_token` if given.
    pub fn feed_eof(&mut self, last_token: Option<&Token>) -> Result<Option<Node>> {
        let eof = match last_token {
            Some(t) => Token::new_borrow_pos("$END", "", t),
            None => Token {
                kind: "$END".into(),
                value: String::new(),
                start_pos: Some(0),
                line: Some(1),
                column: Some(1),
                ..Default::default()
            },
        };
        self.feed_token(eof)
    }

    /// A copy with its own parser and lexer state.
    pub fn copy(&self) -> InteractiveParser<'a> {
        InteractiveParser {
            parser: self.parser,
            state: self.state.clone(),
            lexer: self.lexer.copy(),
            result: self.result.clone(),
        }
    }

    /// The token types the current state has an action for, with the action.
    pub fn choices(&self) -> IndexMap<String, String> {
        self.parser
            .choices(&self.state)
            .iter()
            .map(|(k, v)| {
                let action = match v {
                    Action::Shift(s) => format!("Shift({})", s),
                    Action::Reduce(r) => format!("Reduce({})", r),
                };
                (k.clone(), action)
            })
            .collect()
    }

    /// The terminals that move the parser into a valid state.
    pub fn accepts(&self) -> BTreeSet<String> {
        self.parser.accepts(&self.state)
    }

    /// Continues parsing from the current state until the end of the text.
    pub fn resume_parse(&mut self) -> Result<Node> {
        let last = self.lexer.state.borrow().last_token.clone();
        self.parser.parse_from_state(&mut self.state, &self.lexer, last)
    }

    /// The parser state.
    pub fn parser_state(&self) -> &ParserState {
        &self.state
    }

    /// The parser state, to change for error recovery.
    pub fn parser_state_mut(&mut self) -> &mut ParserState {
        &mut self.state
    }

    /// Lexes the next token for the parser's current state, without feeding it; none at the end of the text. With
    /// the contextual lexer, only the terminals the state accepts are matched, as when the parser lexes.
    pub fn next_token(&mut self) -> Result<Option<Token>> {
        let shared: SharedParserState = Rc::new(Cell::new((self.state.depth(), self.state.position())));
        let mut stream = self.lexer.lex(Some(shared));
        stream.next().transpose()
    }

    /// The byte offset the lexer is at.
    pub fn lexer_position(&self) -> usize {
        self.lexer.state.borrow().line_ctr.pos
    }

    /// Moves the lexer past `n` characters of the text, for skipping unexpected characters.
    pub fn skip_chars(&mut self, n: usize) {
        let mut state = self.lexer.state.borrow_mut();
        let pos = state.line_ctr.pos;
        let rest = &state.text[pos..state.end];
        let skip: usize = rest.chars().take(n).map(|c| c.len_utf8()).sum();
        let skipped = &rest[..skip];
        let text = skipped.to_string();
        state.line_ctr.feed(&text, true);
    }

    /// Describes the choices, for debugging.
    pub fn pretty(&self) -> String {
        let mut lines = vec!["Parser choices:".to_string()];
        for (k, v) in self.choices() {
            lines.push(format!("\t- {} -> {}", k, v));
        }
        lines.push(format!("stack size: {}", self.state.depth()));
        lines.join("\n")
    }
}

impl PartialEq for InteractiveParser<'_> {
    fn eq(&self, other: &Self) -> bool {
        self.state == other.state && *self.lexer.state.borrow() == *other.lexer.state.borrow()
    }
}

/// Parses with error recovery: when the parse fails, `on_error` gets the error and the interactive parser at the
/// point of failure, and returns whether to continue (Lark's `on_error`).
pub(crate) fn parse_with_recovery<'a>(
    parser: &'a LalrParser,
    start: &str,
    lexer: LexerThread<'a>,
    on_error: &mut dyn FnMut(&Error, &mut InteractiveParser<'a>) -> bool,
) -> Result<Node> {
    let mut ip = InteractiveParser::new(parser, parser.new_state(start)?, lexer);
    let mut e = match ip.resume_parse() {
        Ok(r) => return Ok(r),
        Err(e) if e.as_unexpected_input().is_some() => e,
        Err(e) => return Err(e),
    };
    loop {
        let pos = ip.lexer_position();
        if !on_error(&e, &mut ip) {
            return Err(e);
        }
        if matches!(e, Error::UnexpectedCharacters(_)) && pos == ip.lexer_position() {
            // The handler didn't move the lexer: skip the character.
            ip.skip_chars(1);
        }
        let before = ip.state.clone();
        match ip.resume_parse() {
            Ok(r) => return Ok(r),
            Err(e2) => {
                if let (Error::UnexpectedToken(a), Error::UnexpectedToken(b)) = (&e, &e2) {
                    if a.token.kind == "$END" && b.token.kind == "$END" && before == ip.state {
                        // Prevent an infinite loop.
                        return Err(e2);
                    }
                }
                if e2.as_unexpected_input().is_none() {
                    return Err(e2);
                }
                e = e2;
            }
        }
    }
}
