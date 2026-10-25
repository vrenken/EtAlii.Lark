//! Terminals and the lexers (Lark's `lark.lexer`).

use std::cell::Cell;
use std::collections::{BTreeSet, HashMap, HashSet};
use std::fmt;
use std::rc::Rc;
use std::sync::{Arc, OnceLock};

use indexmap::IndexMap;

use crate::error::{Error, ErrorState, Result, TerminalsByName, UnexpectedCharacters, UnexpectedToken};
use crate::grammar::TOKEN_DEFAULT_PRIORITY;
use crate::pyre::{self, Alternatives, Flags, Regex};
use crate::token::Token;

/// What a terminal matches: a literal string or a regular expression (Lark's `PatternStr` and `PatternRE`).
///
/// Regular expressions are written in Python's `re` dialect, as in Lark.
#[derive(Clone)]
pub enum Pattern {
    Str {
        value: String,
        flags: String,
        raw: Option<String>,
    },
    Re {
        value: String,
        flags: String,
        raw: Option<String>,
        width: Arc<OnceLock<std::result::Result<(u128, u128), pyre::RegexError>>>,
    },
}

fn sorted_flags(flags: &str) -> String {
    let mut f: Vec<char> = flags.chars().collect();
    f.sort_unstable();
    f.dedup();
    f.into_iter().collect()
}

impl Pattern {
    /// A literal string.
    pub fn string(value: impl Into<String>, flags: &str, raw: Option<String>) -> Pattern {
        Pattern::Str {
            value: value.into(),
            flags: sorted_flags(flags),
            raw,
        }
    }

    /// A regular expression in Python's dialect.
    pub fn regex(value: impl Into<String>, flags: &str, raw: Option<String>) -> Pattern {
        Pattern::Re {
            value: value.into(),
            flags: sorted_flags(flags),
            raw,
            width: Arc::new(OnceLock::new()),
        }
    }

    /// The string, or the regular expression.
    pub fn value(&self) -> &str {
        match self {
            Pattern::Str { value, .. } | Pattern::Re { value, .. } => value,
        }
    }

    /// The flags (any of `imslux`), sorted.
    pub fn flags(&self) -> &str {
        match self {
            Pattern::Str { flags, .. } | Pattern::Re { flags, .. } => flags,
        }
    }

    /// The pattern as written in the grammar, when known.
    pub fn raw(&self) -> Option<&str> {
        match self {
            Pattern::Str { raw, .. } | Pattern::Re { raw, .. } => raw.as_deref(),
        }
    }

    /// `"str"` or `"re"`.
    pub fn type_name(&self) -> &'static str {
        match self {
            Pattern::Str { .. } => "str",
            Pattern::Re { .. } => "re",
        }
    }

    pub fn is_str(&self) -> bool {
        matches!(self, Pattern::Str { .. })
    }

    fn with_flags(&self, mut value: String) -> String {
        for f in self.flags().chars() {
            value = format!("(?{}:{})", f, value);
        }
        value
    }

    /// The pattern as a regular expression, in Python's dialect.
    pub fn to_regexp(&self) -> String {
        match self {
            Pattern::Str { value, .. } => self.with_flags(pyre::escape(value)),
            Pattern::Re { value, .. } => self.with_flags(value.clone()),
        }
    }

    pub(crate) fn width(&self) -> std::result::Result<(u128, u128), pyre::RegexError> {
        match self {
            Pattern::Str { value, .. } => {
                let n = value.chars().count() as u128;
                Ok((n, n))
            }
            Pattern::Re { width, .. } => width.get_or_init(|| pyre::width(&self.to_regexp(), Flags::default())).clone(),
        }
    }

    /// The minimum number of characters the pattern matches.
    pub fn min_width(&self) -> u128 {
        self.width().map(|w| w.0).unwrap_or(0)
    }

    /// The maximum number of characters the pattern matches (`1 << 64` when unbounded, as in Python).
    pub fn max_width(&self) -> u128 {
        self.width().map(|w| w.1).unwrap_or(pyre::MAX_WIDTH)
    }
}

impl PartialEq for Pattern {
    fn eq(&self, other: &Pattern) -> bool {
        self.type_name() == other.type_name() && self.value() == other.value() && self.flags() == other.flags()
    }
}

impl Eq for Pattern {}

impl std::hash::Hash for Pattern {
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        self.type_name().hash(state);
        self.value().hash(state);
        self.flags().hash(state);
    }
}

impl fmt::Debug for Pattern {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&crate::utils::repr(&self.to_regexp()))
    }
}

/// A terminal: its name, pattern and priority (Lark's `TerminalDef`).
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct TerminalDef {
    pub name: String,
    pub pattern: Pattern,
    pub priority: i64,
}

impl TerminalDef {
    pub fn new(name: impl Into<String>, pattern: Pattern) -> TerminalDef {
        TerminalDef {
            name: name.into(),
            pattern,
            priority: TOKEN_DEFAULT_PRIORITY,
        }
    }

    /// How the terminal is shown in error messages: the pattern for anonymous terminals, otherwise the name.
    pub fn user_repr(&self) -> String {
        if self.name.starts_with("__") {
            self.pattern.raw().map(str::to_string).unwrap_or_else(|| self.name.clone())
        } else {
            self.name.clone()
        }
    }
}

/// A callback that replaces each token of a terminal (Lark's `lexer_callbacks`).
pub type TokenCallback = Arc<dyn Fn(Token) -> Token + Send + Sync>;

/// The configuration of a lexer (Lark's `LexerConf`).
#[derive(Clone)]
pub struct LexerConf {
    pub terminals: Vec<TerminalDef>,
    pub terminals_by_name: TerminalsByName,
    pub ignore: Vec<String>,
    pub callbacks: IndexMap<String, TokenCallback>,
    pub regex_flags: Flags,
    pub skip_validation: bool,
    pub strict: bool,
}

impl LexerConf {
    pub fn new(terminals: Vec<TerminalDef>, ignore: Vec<String>) -> LexerConf {
        let terminals_by_name = Arc::new(terminals.iter().map(|t| (t.name.clone(), t.clone())).collect());
        LexerConf {
            terminals,
            terminals_by_name,
            ignore,
            callbacks: IndexMap::new(),
            regex_flags: Flags::default(),
            skip_validation: false,
            strict: false,
        }
    }

    fn with_terminals(&self, terminals: Vec<TerminalDef>) -> LexerConf {
        let mut conf = self.clone();
        conf.terminals = terminals;
        conf
    }
}

/// Keeps track of the line and column while the lexer advances (Lark's `LineCounter`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LineCounter {
    /// The byte offset.
    pub pos: usize,
    /// The offset in characters, which Lark calls `char_pos`.
    pub char_pos: usize,
    pub line: usize,
    pub column: usize,
    pub line_start_char_pos: usize,
}

impl Default for LineCounter {
    fn default() -> Self {
        LineCounter {
            pos: 0,
            char_pos: 0,
            line: 1,
            column: 1,
            line_start_char_pos: 0,
        }
    }
}

impl LineCounter {
    /// Consumes a token and updates the line and column.
    pub fn feed(&mut self, token: &str, test_newline: bool) {
        let chars = if test_newline {
            let mut count = 0;
            let mut newlines = 0;
            let mut last_newline = 0;
            for c in token.chars() {
                count += 1;
                if c == '\n' {
                    newlines += 1;
                    last_newline = count;
                }
            }
            if newlines > 0 {
                self.line += newlines;
                self.line_start_char_pos = self.char_pos + last_newline;
            }
            count
        } else {
            token.chars().count()
        };
        self.pos += token.len();
        self.char_pos += chars;
        self.column = self.char_pos - self.line_start_char_pos + 1;
    }
}

/// The state of the lexer as it scans a text (Lark's `LexerState`).
#[derive(Clone, Debug)]
pub struct LexerState<'t> {
    /// The whole text.
    pub text: &'t str,
    /// Where lexing stops (the end of a `TextSlice`).
    pub end: usize,
    pub line_ctr: LineCounter,
    pub last_token: Option<Token>,
}

impl<'t> LexerState<'t> {
    /// A state that lexes `text[start..end]`, with positions counted from the start of `text`.
    pub fn new(text: &'t str, start: usize, end: usize) -> LexerState<'t> {
        let mut line_ctr = LineCounter::default();
        if start > 0 {
            line_ctr.feed(&text[..start], true);
        }
        LexerState {
            text,
            end,
            line_ctr,
            last_token: None,
        }
    }
}

impl PartialEq for LexerState<'_> {
    fn eq(&self, other: &Self) -> bool {
        std::ptr::eq(self.text, other.text)
            && self.end == other.end
            && self.line_ctr.pos == other.line_ctr.pos
            && self.last_token == other.last_token
    }
}

/// Matches the text against the terminals, in order: the first terminal that matches wins, as in a Python
/// alternation (Lark's `Scanner`).
#[derive(Clone)]
pub(crate) struct Scanner {
    terminals: Vec<TerminalDef>,
    regexes: Vec<Regex>,
    /// The terminals' regexes tried in their order, as one search.
    alternatives: Alternatives,
    /// The same, each matching a whole text, built when a callback first needs it.
    full: OnceLock<Option<Alternatives>>,
    pub(crate) allowed_types: Vec<String>,
}

impl Scanner {
    fn new(terminals: Vec<TerminalDef>, flags: Flags) -> Result<Scanner> {
        let mut regexes = Vec::with_capacity(terminals.len());
        for t in &terminals {
            regexes.push(compile_terminal(t, flags)?);
        }
        let alternatives = Alternatives::new(&regexes, false).map_err(|e| Error::Lex(e.0))?;
        let allowed_types = terminals.iter().map(|t| t.name.clone()).collect();
        Ok(Scanner {
            terminals,
            regexes,
            alternatives,
            full: OnceLock::new(),
            allowed_types,
        })
    }

    /// The end of the match at `pos` and the index of the terminal that matched: the first terminal, in order, that
    /// matches there.
    fn match_at(&self, text: &str, pos: usize) -> Option<(usize, usize)> {
        self.alternatives.match_at(text, pos)
    }

    /// The first terminal, in order, that matches the whole of `text`.
    fn full_match(&self, text: &str) -> Option<&str> {
        let full = self.full.get_or_init(|| Alternatives::new(&self.regexes, true).ok());
        match full {
            Some(full) => full.match_at(text, 0).map(|(_, i)| self.terminals[i].name.as_str()),
            None => self
                .regexes
                .iter()
                .position(|re| re.full_match(text))
                .map(|i| self.terminals[i].name.as_str()),
        }
    }
}

pub(crate) fn compile_terminal(t: &TerminalDef, flags: Flags) -> Result<Regex> {
    Regex::new(&t.pattern.to_regexp(), flags).map_err(|_| Error::Lex(format!("Cannot compile token {}: {:?}", t.name, t.pattern)))
}

/// Lark's `_regexp_has_newline`.
pub(crate) fn regexp_has_newline(r: &str) -> bool {
    r.contains('\n') || r.contains("\\n") || r.contains("\\s") || r.contains("[^") || (r.contains("(?s") && r.contains('.'))
}

#[derive(Clone)]
enum Callback {
    Unless(Arc<Scanner>),
    User(TokenCallback),
    Chain(Arc<Scanner>, TokenCallback, String),
}

impl Callback {
    fn call(&self, mut t: Token) -> Token {
        match self {
            Callback::Unless(scanner) => {
                if let Some(kind) = scanner.full_match(&t.value) {
                    t.kind = kind.to_string();
                }
                t
            }
            Callback::User(f) => f(t),
            Callback::Chain(scanner, f, kind) => {
                let t2 = Callback::Unless(scanner.clone()).call(t.clone());
                if &t2.kind == kind {
                    f(t)
                } else {
                    t2
                }
            }
        }
    }
}

/// A lexer that matches every terminal, regardless of the parser state (Lark's `BasicLexer`).
#[derive(Clone)]
pub struct BasicLexer {
    terminals: Vec<TerminalDef>,
    ignore_types: HashSet<String>,
    newline_types: HashSet<String>,
    user_callbacks: IndexMap<String, TokenCallback>,
    flags: Flags,
    pub(crate) terminals_by_name: TerminalsByName,
    built: Arc<OnceLock<std::result::Result<(Scanner, HashMap<String, Callback>), String>>>,
}

impl BasicLexer {
    pub fn new(conf: &LexerConf) -> Result<BasicLexer> {
        let mut terminals = conf.terminals.clone();
        if !conf.skip_validation {
            for t in &terminals {
                compile_terminal(t, conf.regex_flags)?;
                if t.pattern.min_width() == 0 {
                    return Err(Error::Lex(format!(
                        "Lexer does not allow zero-width terminals. ({}: {:?})",
                        t.name, t.pattern
                    )));
                }
            }
            let names: HashSet<&str> = terminals.iter().map(|t| t.name.as_str()).collect();
            let missing: Vec<String> = conf
                .ignore
                .iter()
                .filter(|i| !names.contains(i.as_str()))
                .map(|i| crate::utils::repr(i))
                .collect();
            if !missing.is_empty() {
                return Err(Error::Lex(format!("Ignore terminals are not defined: {{{}}}", missing.join(", "))));
            }
            if conf.strict {
                crate::collisions::check(&terminals, conf.regex_flags)?;
            }
        }

        let newline_types = terminals
            .iter()
            .filter(|t| regexp_has_newline(&t.pattern.to_regexp()))
            .map(|t| t.name.clone())
            .collect();
        terminals.sort_by(|a, b| {
            b.priority
                .cmp(&a.priority)
                .then_with(|| b.pattern.max_width().cmp(&a.pattern.max_width()))
                .then_with(|| b.pattern.value().chars().count().cmp(&a.pattern.value().chars().count()))
                .then_with(|| a.name.cmp(&b.name))
        });
        Ok(BasicLexer {
            terminals,
            ignore_types: conf.ignore.iter().cloned().collect(),
            newline_types,
            user_callbacks: conf.callbacks.clone(),
            flags: conf.regex_flags,
            terminals_by_name: conf.terminals_by_name.clone(),
            built: Arc::new(OnceLock::new()),
        })
    }

    /// The terminals, in the order the lexer tries them.
    pub fn terminals(&self) -> &[TerminalDef] {
        &self.terminals
    }

    fn build(&self) -> Result<&(Scanner, HashMap<String, Callback>)> {
        let built = self.built.get_or_init(|| {
            let (terminals, mut callbacks) = create_unless(&self.terminals, self.flags).map_err(|e| e.to_string())?;
            for (kind, f) in &self.user_callbacks {
                let cb = match callbacks.remove(kind) {
                    Some(Callback::Unless(scanner)) => Callback::Chain(scanner, f.clone(), kind.clone()),
                    _ => Callback::User(f.clone()),
                };
                callbacks.insert(kind.clone(), cb);
            }
            Ok((Scanner::new(terminals, self.flags).map_err(|e| e.to_string())?, callbacks))
        });
        built.as_ref().map_err(|e| Error::Lex(e.clone()))
    }

    /// The next token, or `None` at the end of the text.
    pub fn next_token(&self, state: &mut LexerState<'_>, parser_state: Option<&ErrorState>) -> Result<Option<Token>> {
        let (scanner, callbacks) = self.build()?;
        let text = &state.text[..state.end];
        while state.line_ctr.pos < state.end {
            let line_ctr = &mut state.line_ctr;
            let (end, index) = match scanner.match_at(text, line_ctr.pos) {
                Some(m) => m,
                None => {
                    let mut allowed: BTreeSet<String> = scanner
                        .allowed_types
                        .iter()
                        .filter(|t| !self.ignore_types.contains(*t))
                        .cloned()
                        .collect();
                    if allowed.is_empty() {
                        allowed.insert("<END-OF-FILE>".to_string());
                    }
                    let mut e = UnexpectedCharacters::new(
                        state.text,
                        line_ctr.pos,
                        line_ctr.line,
                        line_ctr.column,
                        Some(allowed),
                        parser_state.cloned(),
                        state.last_token.clone().map(|t| vec![t]),
                        Some(self.terminals_by_name.clone()),
                    );
                    e.considered_tokens = None;
                    return Err(e.into());
                }
            };
            let kind = &scanner.terminals[index].name;
            let value = &text[line_ctr.pos..end];
            let ignored = self.ignore_types.contains(kind);
            let callback = callbacks.get(kind);
            let mut token = if !ignored || callback.is_some() {
                Some(Token {
                    kind: kind.clone(),
                    value: value.to_string(),
                    start_pos: Some(line_ctr.pos),
                    line: Some(line_ctr.line),
                    column: Some(line_ctr.column),
                    ..Default::default()
                })
            } else {
                None
            };
            line_ctr.feed(value, self.newline_types.contains(kind));
            if let Some(mut t) = token.take() {
                t.end_line = Some(line_ctr.line);
                t.end_column = Some(line_ctr.column);
                t.end_pos = Some(line_ctr.pos);
                if let Some(cb) = callbacks.get(&t.kind) {
                    t = cb.call(t);
                }
                if !ignored {
                    state.last_token = Some(t.clone());
                    return Ok(Some(t));
                }
            }
        }
        Ok(None)
    }
}

fn create_unless(terminals: &[TerminalDef], flags: Flags) -> Result<(Vec<TerminalDef>, HashMap<String, Callback>)> {
    let strs: Vec<&TerminalDef> = terminals.iter().filter(|t| t.pattern.is_str()).collect();
    let mut embedded: HashSet<String> = HashSet::new();
    let mut callbacks = HashMap::new();
    for retok in terminals.iter().filter(|t| !t.pattern.is_str()) {
        let re = compile_terminal(retok, flags)?;
        let mut unless = Vec::new();
        for strtok in &strs {
            if strtok.priority != retok.priority {
                continue;
            }
            let s = strtok.pattern.value();
            if re.match_at(s, 0) == Some(s.len()) {
                unless.push((*strtok).clone());
                if strtok.pattern.flags().chars().all(|f| retok.pattern.flags().contains(f)) {
                    embedded.insert(strtok.name.clone());
                }
            }
        }
        if !unless.is_empty() {
            callbacks.insert(retok.name.clone(), Callback::Unless(Arc::new(Scanner::new(unless, flags)?)));
        }
    }
    let new_terminals = terminals.iter().filter(|t| !embedded.contains(&t.name)).cloned().collect();
    Ok((new_terminals, callbacks))
}

/// A lexer that only matches the terminals the LALR parser accepts in its current state (Lark's `ContextualLexer`).
#[derive(Clone)]
pub struct ContextualLexer {
    lexers: HashMap<usize, Arc<BasicLexer>>,
    root_lexer: BasicLexer,
}

impl ContextualLexer {
    pub fn new(conf: &LexerConf, states: &IndexMap<usize, Vec<String>>, always_accept: &[String]) -> Result<ContextualLexer> {
        let mut by_tokens: HashMap<BTreeSet<String>, Arc<BasicLexer>> = HashMap::new();
        let mut lexers = HashMap::new();
        for (state, accepts) in states {
            let key: BTreeSet<String> = accepts.iter().cloned().collect();
            let lexer = match by_tokens.get(&key) {
                Some(l) => l.clone(),
                None => {
                    let mut all = key.clone();
                    all.extend(conf.ignore.iter().cloned());
                    all.extend(always_accept.iter().cloned());
                    let terminals = all.iter().filter_map(|n| conf.terminals_by_name.get(n).cloned()).collect();
                    let lexer = Arc::new(BasicLexer::new(&conf.with_terminals(terminals))?);
                    by_tokens.insert(key, lexer.clone());
                    lexer
                }
            };
            lexers.insert(*state, lexer);
        }
        let mut root_conf = conf.clone();
        root_conf.skip_validation = true;
        Ok(ContextualLexer {
            lexers,
            root_lexer: BasicLexer::new(&root_conf)?,
        })
    }

    pub fn next_token(&self, state: &mut LexerState<'_>, parser_state: &ErrorState) -> Result<Option<Token>> {
        let position = match parser_state {
            ErrorState::Lalr { position, .. } => *position,
            _ => 0,
        };
        let lexer = &self.lexers[&position];
        match lexer.next_token(state, Some(parser_state)) {
            Err(Error::UnexpectedCharacters(e)) => {
                // The terminal may be defined, but not expected in this state: the root lexer gives a nicer error.
                let last_token = state.last_token.clone();
                match self.root_lexer.next_token(state, Some(parser_state)) {
                    Ok(Some(token)) => {
                        let mut ut = UnexpectedToken::new(
                            token,
                            e.allowed.clone().unwrap_or_default(),
                            Some(parser_state.clone()),
                            Some(self.root_lexer.terminals_by_name.clone()),
                        );
                        ut.token_history = Some(last_token.into_iter().collect());
                        Err(ut.into())
                    }
                    _ => Err(Error::UnexpectedCharacters(e)),
                }
            }
            other => other,
        }
    }
}

/// A lexer written by the user, for `lexer: custom` (Lark's `Lexer` interface).
pub trait CustomLexer: Send + Sync {
    /// Splits the text into tokens.
    fn lex(&self, text: &str) -> Result<Vec<Token>>;
}

/// Post-processes the tokens of the lexer, such as the [`Indenter`](crate::Indenter) (Lark's `PostLex`).
pub trait PostLex: Send + Sync {
    /// Transforms the token stream.
    fn process<'a>(&'a self, stream: TokenStream<'a>) -> TokenStream<'a>;

    /// Terminals the contextual lexer must always accept, because the post-lexer consumes them.
    fn always_accept(&self) -> Vec<String> {
        Vec::new()
    }
}

/// A stream of tokens, as produced by a lexer.
pub type TokenStream<'a> = Box<dyn Iterator<Item = Result<Token>> + 'a>;

/// The parser state that the contextual lexer reads to decide which terminals to match.
pub(crate) type SharedParserState = Rc<Cell<(usize, usize)>>;

/// The lexer of a parser front-end.
#[derive(Clone)]
pub(crate) enum LexerImpl {
    Basic(BasicLexer),
    Contextual(ContextualLexer),
    Custom(Arc<dyn CustomLexer>),
}

impl LexerImpl {
    pub(crate) fn terminals_by_name(&self) -> Option<TerminalsByName> {
        match self {
            LexerImpl::Basic(l) => Some(l.terminals_by_name.clone()),
            LexerImpl::Contextual(l) => Some(l.root_lexer.terminals_by_name.clone()),
            LexerImpl::Custom(_) => None,
        }
    }

    /// Lexes the state's text into a stream; the contextual lexer reads the parser state as it goes.
    pub(crate) fn lex<'a>(
        &'a self,
        state: Rc<std::cell::RefCell<LexerState<'a>>>,
        parser_state: Option<SharedParserState>,
    ) -> TokenStream<'a> {
        match self {
            LexerImpl::Basic(lexer) => Box::new(std::iter::from_fn(move || {
                let ps = parser_state.as_ref().map(|p| {
                    let (depth, position) = p.get();
                    ErrorState::Lalr { depth, position }
                });
                lexer.next_token(&mut state.borrow_mut(), ps.as_ref()).transpose()
            })),
            LexerImpl::Contextual(lexer) => Box::new(std::iter::from_fn(move || {
                let (depth, position) = parser_state.as_ref().map(|p| p.get()).unwrap_or((0, 0));
                lexer
                    .next_token(&mut state.borrow_mut(), &ErrorState::Lalr { depth, position })
                    .transpose()
            })),
            LexerImpl::Custom(lexer) => {
                let text = {
                    let s = state.borrow();
                    &s.text[s.line_ctr.pos..s.end]
                };
                match lexer.lex(text) {
                    Ok(tokens) => Box::new(tokens.into_iter().map(Ok)),
                    Err(e) => Box::new(std::iter::once(Err(e))),
                }
            }
        }
    }
}

/// Ties a lexer to the state of the text it lexes, for the parser (Lark's `LexerThread`).
pub(crate) struct LexerThread<'a> {
    pub lexer: &'a LexerImpl,
    pub postlex: Option<&'a dyn PostLex>,
    pub state: Rc<std::cell::RefCell<LexerState<'a>>>,
}

impl<'a> LexerThread<'a> {
    pub fn new(lexer: &'a LexerImpl, postlex: Option<&'a dyn PostLex>, state: LexerState<'a>) -> LexerThread<'a> {
        LexerThread {
            lexer,
            postlex,
            state: Rc::new(std::cell::RefCell::new(state)),
        }
    }

    /// The tokens from the current position on; the contextual lexer reads `parser_state` as it goes.
    pub fn lex(&self, parser_state: Option<SharedParserState>) -> TokenStream<'a> {
        let stream = self.lexer.lex(self.state.clone(), parser_state);
        match self.postlex {
            Some(p) => p.process(stream),
            None => stream,
        }
    }

    /// A thread with a copy of the state.
    pub fn copy(&self) -> LexerThread<'a> {
        LexerThread {
            lexer: self.lexer,
            postlex: self.postlex,
            state: Rc::new(std::cell::RefCell::new(self.state.borrow().clone())),
        }
    }
}
