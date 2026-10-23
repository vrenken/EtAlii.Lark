//! Lark's exceptions, as one error type.

use std::collections::BTreeSet;
use std::fmt;
use std::sync::Arc;

use crate::lexer::TerminalDef;
use crate::token::Token;
use crate::utils::expand_tabs;

/// A value representing the parser state at an error, used by [`UnexpectedInput::match_examples`].
/// Don't rely on its value, only on its equality.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub enum ErrorState {
    /// The LALR parser: the depth of its state stack and its current state.
    Lalr { depth: usize, position: usize },
    /// The Earley parser: the terminals it expected.
    Expected(BTreeSet<String>),
}

pub(crate) type TerminalsByName = Arc<indexmap::IndexMap<String, TerminalDef>>;

fn format_expected<'a>(expected: impl IntoIterator<Item = &'a String>, terminals: &Option<TerminalsByName>) -> String {
    let names: Vec<String> = expected
        .into_iter()
        .map(|name| match terminals.as_ref().and_then(|t| t.get(name)) {
            Some(t) => t.user_repr(),
            None => name.clone(),
        })
        .collect();
    format!("Expected one of: \n\t* {}\n", names.join("\n\t* "))
}

/// Returns a string pinpointing a position in the text, with `span` bytes of context around it
/// (Lark's `UnexpectedInput.get_context`).
pub fn get_context(text: &str, pos: usize, span: usize) -> String {
    let pos = floor_char_boundary(text, pos.min(text.len()));
    let start = floor_char_boundary(text, pos.saturating_sub(span));
    let end = floor_char_boundary(text, (pos + span).min(text.len()));
    let before = text[start..pos].rsplit('\n').next().unwrap_or("");
    let after = text[pos..end].split('\n').next().unwrap_or("");
    format!("{}{}\n{}^\n", before, after, " ".repeat(expand_tabs(before).chars().count()))
}

fn floor_char_boundary(text: &str, mut pos: usize) -> usize {
    while pos > 0 && !text.is_char_boundary(pos) {
        pos -= 1;
    }
    pos
}

/// The lexer found no terminal that matches the text at its position (Lark's `UnexpectedCharacters`).
#[derive(Clone, Debug)]
pub struct UnexpectedCharacters {
    pub line: usize,
    pub column: usize,
    /// The byte offset of the error.
    pub pos_in_stream: usize,
    /// The character that didn't match.
    pub char: char,
    /// The terminals that were allowed at this point.
    pub allowed: Option<BTreeSet<String>>,
    /// The terminals the Earley parser considered.
    pub considered_tokens: Option<BTreeSet<String>>,
    /// The rules the Earley parser considered.
    pub considered_rules: Option<BTreeSet<String>>,
    pub token_history: Option<Vec<Token>>,
    pub state: Option<ErrorState>,
    pub(crate) terminals_by_name: Option<TerminalsByName>,
    context: String,
}

impl UnexpectedCharacters {
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn new(
        text: &str,
        pos: usize,
        line: usize,
        column: usize,
        allowed: Option<BTreeSet<String>>,
        state: Option<ErrorState>,
        token_history: Option<Vec<Token>>,
        terminals_by_name: Option<TerminalsByName>,
    ) -> UnexpectedCharacters {
        UnexpectedCharacters {
            line,
            column,
            pos_in_stream: pos,
            char: text[pos.min(text.len())..].chars().next().unwrap_or('\0'),
            allowed,
            considered_tokens: None,
            considered_rules: None,
            token_history,
            state,
            terminals_by_name,
            context: get_context(text, pos, 40),
        }
    }
}

impl fmt::Display for UnexpectedCharacters {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "No terminal matches '{}' in the current parser context, at line {} col {}",
            self.char, self.line, self.column
        )?;
        write!(f, "\n\n{}", self.context)?;
        if let Some(allowed) = &self.allowed {
            if !allowed.is_empty() {
                f.write_str(&format_expected(allowed, &self.terminals_by_name))?;
            }
        }
        if let Some(history) = &self.token_history {
            if !history.is_empty() {
                let tokens: Vec<String> = history.iter().map(|t| format!("{:?}", t)).collect();
                write!(f, "\nPrevious tokens: {}\n", tokens.join(", "))?;
            }
        }
        Ok(())
    }
}

/// The parser received a token that doesn't fit the grammar at that point (Lark's `UnexpectedToken`).
#[derive(Clone, Debug)]
pub struct UnexpectedToken {
    /// The line of the token, or 0 when the token has no position.
    pub line: usize,
    pub column: usize,
    pub pos_in_stream: Option<usize>,
    /// The mismatched token.
    pub token: Token,
    /// The terminals that were expected.
    pub expected: BTreeSet<String>,
    /// The terminals the LALR parser would have accepted (Lark's `accepts`).
    pub accepts: Option<BTreeSet<String>>,
    pub considered_rules: Option<BTreeSet<String>>,
    pub token_history: Option<Vec<Token>>,
    pub state: Option<ErrorState>,
    pub(crate) terminals_by_name: Option<TerminalsByName>,
}

impl UnexpectedToken {
    pub(crate) fn new(
        token: Token,
        expected: BTreeSet<String>,
        state: Option<ErrorState>,
        terminals_by_name: Option<TerminalsByName>,
    ) -> UnexpectedToken {
        UnexpectedToken {
            line: token.line.unwrap_or(0),
            column: token.column.unwrap_or(0),
            pos_in_stream: token.start_pos,
            token,
            expected,
            accepts: None,
            considered_rules: None,
            token_history: None,
            state,
            terminals_by_name,
        }
    }
}

impl fmt::Display for UnexpectedToken {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let expected = match &self.accepts {
            Some(a) if !a.is_empty() => a,
            _ => &self.expected,
        };
        write!(
            f,
            "Unexpected token {:?} at line {}, column {}.\n{}",
            self.token,
            self.line,
            self.column,
            format_expected(expected, &self.terminals_by_name)
        )?;
        if let Some(history) = &self.token_history {
            if !history.is_empty() {
                writeln!(f, "Previous tokens: {:?}", history)?;
            }
        }
        Ok(())
    }
}

/// The input ended while the parser still expected a token (Lark's `UnexpectedEOF`).
#[derive(Clone, Debug)]
pub struct UnexpectedEof {
    pub expected: BTreeSet<String>,
    pub state: Option<ErrorState>,
    pub(crate) terminals_by_name: Option<TerminalsByName>,
}

impl fmt::Display for UnexpectedEof {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "Unexpected end-of-input. {}",
            format_expected(&self.expected, &self.terminals_by_name)
        )
    }
}

/// An error raised by a visitor or transformer callback (Lark's `VisitError`).
#[derive(Debug)]
pub struct VisitError {
    /// The rule or terminal whose callback failed.
    pub rule: String,
    /// The error of the callback.
    pub source: Box<dyn std::error::Error + Send + Sync>,
}

/// Every error of the crate. The variants correspond to Lark's exceptions.
#[derive(Debug)]
#[non_exhaustive]
pub enum Error {
    /// Invalid options (Lark's `ConfigurationError`).
    Configuration(String),
    /// An invalid grammar (Lark's `GrammarError`).
    Grammar(String),
    /// A lexer error other than unexpected characters, such as a terminal that can't be compiled (Lark's `LexError`).
    Lex(String),
    /// A parse error other than the unexpected-input ones, as raised by the CYK parser (Lark's `ParseError`).
    Parse(String),
    UnexpectedCharacters(Box<UnexpectedCharacters>),
    UnexpectedToken(Box<UnexpectedToken>),
    UnexpectedEof(Box<UnexpectedEof>),
    /// A visitor or transformer callback failed.
    Visit(Box<VisitError>),
    /// A tree template variable has no value (Lark's `MissingVariableError`).
    MissingVariable(String),
    /// The indenter found a dedent to a column that wasn't indented to (Lark's `DedentError`).
    Dedent(String),
    /// Reading a grammar file or the input failed.
    Io(std::io::Error),
}

impl Error {
    /// The name of the Lark exception this error corresponds to.
    pub fn lark_name(&self) -> &'static str {
        match self {
            Error::Configuration(_) => "ConfigurationError",
            Error::Grammar(_) => "GrammarError",
            Error::Lex(_) => "LexError",
            Error::Parse(_) => "ParseError",
            Error::UnexpectedCharacters(_) => "UnexpectedCharacters",
            Error::UnexpectedToken(_) => "UnexpectedToken",
            Error::UnexpectedEof(_) => "UnexpectedEOF",
            Error::Visit(_) => "VisitError",
            Error::MissingVariable(_) => "MissingVariableError",
            Error::Dedent(_) => "DedentError",
            Error::Io(_) => "IOError",
        }
    }

    /// Whether this is one of the unexpected-input errors (Lark's `UnexpectedInput`).
    pub fn as_unexpected_input(&self) -> Option<UnexpectedInput<'_>> {
        match self {
            Error::UnexpectedCharacters(e) => Some(UnexpectedInput::Characters(e)),
            Error::UnexpectedToken(e) => Some(UnexpectedInput::Token(e)),
            Error::UnexpectedEof(e) => Some(UnexpectedInput::Eof(e)),
            _ => None,
        }
    }

    /// Whether Lark would raise this as a `ParseError` (including `UnexpectedToken` and `UnexpectedEOF`).
    pub fn is_parse_error(&self) -> bool {
        matches!(self, Error::Parse(_) | Error::UnexpectedToken(_) | Error::UnexpectedEof(_))
    }

    /// Whether Lark would raise this as a `LexError` (including `UnexpectedCharacters`).
    pub fn is_lex_error(&self) -> bool {
        matches!(self, Error::Lex(_) | Error::UnexpectedCharacters(_))
    }

    pub(crate) fn grammar(message: impl Into<String>) -> Error {
        Error::Grammar(message.into())
    }

    pub(crate) fn config(message: impl Into<String>) -> Error {
        Error::Configuration(message.into())
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Configuration(m)
            | Error::Grammar(m)
            | Error::Lex(m)
            | Error::Parse(m)
            | Error::MissingVariable(m)
            | Error::Dedent(m) => f.write_str(m),
            Error::UnexpectedCharacters(e) => e.fmt(f),
            Error::UnexpectedToken(e) => e.fmt(f),
            Error::UnexpectedEof(e) => e.fmt(f),
            Error::Visit(e) => write!(f, "Error trying to process rule \"{}\":\n\n{}", e.rule, e.source),
            Error::Io(e) => e.fmt(f),
        }
    }
}

impl std::error::Error for Error {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Error::Visit(e) => Some(&*e.source),
            Error::Io(e) => Some(e),
            _ => None,
        }
    }
}

impl From<std::io::Error> for Error {
    fn from(e: std::io::Error) -> Error {
        Error::Io(e)
    }
}

impl From<UnexpectedCharacters> for Error {
    fn from(e: UnexpectedCharacters) -> Error {
        Error::UnexpectedCharacters(Box::new(e))
    }
}

impl From<UnexpectedToken> for Error {
    fn from(e: UnexpectedToken) -> Error {
        Error::UnexpectedToken(Box::new(e))
    }
}

impl From<UnexpectedEof> for Error {
    fn from(e: UnexpectedEof) -> Error {
        Error::UnexpectedEof(Box::new(e))
    }
}

/// The result type of the crate.
pub type Result<T, E = Error> = std::result::Result<T, E>;

/// One of the unexpected-input errors (Lark's `UnexpectedInput`), with what they have in common.
#[derive(Clone, Copy, Debug)]
pub enum UnexpectedInput<'a> {
    Characters(&'a UnexpectedCharacters),
    Token(&'a UnexpectedToken),
    Eof(&'a UnexpectedEof),
}

impl<'a> UnexpectedInput<'a> {
    /// The line of the error, or `None` at the end of the input.
    pub fn line(&self) -> Option<usize> {
        match self {
            UnexpectedInput::Characters(e) => Some(e.line),
            UnexpectedInput::Token(e) => e.token.line,
            UnexpectedInput::Eof(_) => None,
        }
    }

    /// The column of the error, or `None` at the end of the input.
    pub fn column(&self) -> Option<usize> {
        match self {
            UnexpectedInput::Characters(e) => Some(e.column),
            UnexpectedInput::Token(e) => e.token.column,
            UnexpectedInput::Eof(_) => None,
        }
    }

    /// The byte offset of the error.
    pub fn pos_in_stream(&self) -> Option<usize> {
        match self {
            UnexpectedInput::Characters(e) => Some(e.pos_in_stream),
            UnexpectedInput::Token(e) => e.pos_in_stream,
            UnexpectedInput::Eof(_) => None,
        }
    }

    pub fn state(&self) -> Option<&ErrorState> {
        match self {
            UnexpectedInput::Characters(e) => e.state.as_ref(),
            UnexpectedInput::Token(e) => e.state.as_ref(),
            UnexpectedInput::Eof(e) => e.state.as_ref(),
        }
    }

    fn token(&self) -> Option<Token> {
        match self {
            UnexpectedInput::Token(e) => Some(e.token.clone()),
            UnexpectedInput::Eof(_) => Some(Token::new("<EOF>", "")),
            UnexpectedInput::Characters(_) => None,
        }
    }

    /// A string pinpointing the error in the text, with `span` bytes of context (Lark's `get_context`).
    pub fn get_context(&self, text: &str, span: usize) -> String {
        get_context(text, self.pos_in_stream().unwrap_or(0), span)
    }

    /// Finds which of the examples fails the same way as this error, and returns its label (Lark's
    /// `match_examples`). `parse` is usually a closure that calls `Lark::parse`.
    pub fn match_examples<L: Clone, S: AsRef<str>, P>(
        &self,
        parse: P,
        examples: impl IntoIterator<Item = (L, Vec<S>)>,
        token_type_match_fallback: bool,
        use_accepts: bool,
    ) -> Option<L>
    where
        P: Fn(&str) -> Result<()>,
    {
        let state = self.state()?;
        let mut candidate: (Option<L>, bool) = (None, false);
        for (label, example) in examples {
            for malformed in example {
                let error = match parse(malformed.as_ref()) {
                    Err(e) => e,
                    Ok(()) => continue,
                };
                let ut = match error.as_unexpected_input() {
                    Some(ut) => ut,
                    None => continue,
                };
                if ut.state() != Some(state) {
                    continue;
                }
                if use_accepts {
                    if let (UnexpectedInput::Token(a), UnexpectedInput::Token(b)) = (self, &ut) {
                        if a.accepts != b.accepts {
                            continue;
                        }
                    }
                }
                if let (Some(mine), Some(theirs)) = (self.token(), ut.token()) {
                    if mine == theirs {
                        return Some(label);
                    }
                    if token_type_match_fallback && theirs.kind == mine.kind && !candidate.1 {
                        candidate = (Some(label.clone()), true);
                    }
                }
                if candidate.0.is_none() {
                    candidate = (Some(label.clone()), false);
                }
            }
        }
        candidate.0
    }
}
