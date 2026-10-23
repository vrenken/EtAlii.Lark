//! The main interface: compiles a grammar into a parser (Lark's `Lark` class and its parser front-ends).

use std::collections::HashSet;
use std::sync::Arc;

use indexmap::IndexMap;

use crate::error::{Error, Result};
use crate::grammar::Rule;
use crate::lexer::{
    BasicLexer, ContextualLexer, CustomLexer, LexerConf, LexerImpl, LexerState, LexerThread, PostLex, TerminalDef, TokenCallback,
};
use crate::load_grammar::{Grammar, GrammarBuilder, GrammarLoader};
use crate::parse_tree_builder::Callbacks;
use crate::parsers::cyk::CykParser;
use crate::parsers::earley::{EarleyInput, EarleyParser};
use crate::parsers::grammar_analysis::GrammarAnalyzer;
use crate::parsers::lalr_analysis::compute_lalr;
use crate::parsers::lalr_parser::{parse_with_recovery, InteractiveParser, LalrParser};
use crate::pyre::Flags;
use crate::token::Token;
use crate::tree::{Node, Tree};

/// The parsing algorithm.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ParserKind {
    /// Earley: parses any context-free grammar, and can return all the ambiguities. The default, as in Lark.
    #[default]
    Earley,
    /// LALR(1): fast, and uses less memory, but only for LALR(1) grammars.
    Lalr,
    /// CYK: a legacy algorithm, kept for completeness.
    Cyk,
}

/// The lexer.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum LexerKind {
    /// Chooses based on the parser: contextual for LALR, dynamic for Earley, basic for CYK.
    #[default]
    Auto,
    /// Matches every terminal, regardless of the parser state.
    Basic,
    /// LALR only: matches only the terminals the parser accepts in its state.
    Contextual,
    /// Earley only: matches terminals with regular expressions while parsing, so tokens can be ambiguous.
    Dynamic,
    /// Earley only: like `Dynamic`, but also tries every shorter match of each terminal (slow).
    DynamicComplete,
    /// A lexer written by the user; see [`LarkOptions::custom_lexer`].
    Custom,
}

/// How the Earley parser handles ambiguity.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Ambiguity {
    /// `Resolve` for Earley.
    #[default]
    Auto,
    /// Choose the best derivation, based on priorities and the order of the rules.
    Resolve,
    /// Return all derivations, under `_ambig` nodes.
    Explicit,
}

/// How priorities are evaluated.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum PriorityMode {
    /// `Normal`.
    #[default]
    Auto,
    Normal,
    /// Negates all priorities.
    Invert,
    /// Ignores all priorities.
    None,
}

/// The options of a parser. See Lark's documentation of the same options.
#[derive(Clone)]
pub struct LarkOptions {
    /// The start rules. Defaults to `start`.
    pub start: Vec<String>,
    /// Display debug information and extra warnings.
    pub debug: bool,
    /// Raises an error on any potential ambiguity: shift/reduce conflicts and regex collisions.
    pub strict: bool,
    /// Don't remove the "punctuation" tokens from the tree.
    pub keep_all_tokens: bool,
    pub parser: ParserKind,
    pub lexer: LexerKind,
    pub ambiguity: Ambiguity,
    pub priority: PriorityMode,
    /// Propagates the positions (line, column, ...) to the `meta` of each tree.
    pub propagate_positions: bool,
    /// When true, the `[]` operator returns `None` (a [`Node::Missing`]) when not matched. Defaults to true.
    pub maybe_placeholders: bool,
    /// Regular expression flags applied to all terminals, as letters (`i`, `m`, `s`, `x`), like Lark's
    /// `g_regex_flags`.
    pub regex_flags: String,
    /// A post-lexing step, such as the [`Indenter`](crate::Indenter). Requires the basic or contextual lexer.
    pub postlex: Option<Arc<dyn PostLex>>,
    /// Called for every token of a terminal after it's matched; the result replaces the token.
    pub lexer_callbacks: IndexMap<String, TokenCallback>,
    /// The lexer, for [`LexerKind::Custom`].
    pub custom_lexer: Option<Arc<dyn CustomLexer>>,
    /// Loaders that are tried, in order, for `%import` statements, before the grammars that come with Lark.
    pub import_paths: Vec<Arc<dyn GrammarLoader>>,
    /// The path of the grammar, for relative imports and error messages.
    pub source_path: Option<String>,
}

impl Default for LarkOptions {
    fn default() -> Self {
        LarkOptions {
            start: vec!["start".to_string()],
            debug: false,
            strict: false,
            keep_all_tokens: false,
            parser: ParserKind::Earley,
            lexer: LexerKind::Auto,
            ambiguity: Ambiguity::Auto,
            priority: PriorityMode::Auto,
            propagate_positions: false,
            maybe_placeholders: true,
            regex_flags: String::new(),
            postlex: None,
            lexer_callbacks: IndexMap::new(),
            custom_lexer: None,
            import_paths: Vec::new(),
            source_path: None,
        }
    }
}

enum ParserImpl {
    Lalr(LalrParser),
    Earley(EarleyParser),
    Cyk(CykParser),
}

/// A parser for a grammar, written in Lark's grammar syntax.
///
/// ```
/// use etalii_lark::{Lark, ParserKind};
///
/// let parser = Lark::builder(r#"
///     start: WORD "," WORD "!"
///     %import common.WORD
///     %ignore " "
/// "#).parser(ParserKind::Lalr).build().unwrap();
/// let tree = parser.parse("Hello, World!").unwrap();
/// assert_eq!(tree.children.len(), 2);
/// ```
pub struct Lark {
    options: LarkOptions,
    grammar: Grammar,
    terminals: Vec<TerminalDef>,
    rules: Vec<Arc<Rule>>,
    ignore_tokens: Vec<String>,
    lexer_conf: LexerConf,
    parser: ParserImpl,
    lexer: Option<LexerImpl>,
}

impl std::fmt::Debug for Lark {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "Lark(source={:?}, parser={:?}, lexer={:?})",
            self.options.source_path.as_deref().unwrap_or("<string>"),
            self.options.parser,
            self.options.lexer
        )
    }
}

impl Lark {
    /// Compiles a grammar into a parser.
    pub fn new(grammar: &str, options: LarkOptions) -> Result<Lark> {
        let source = options.source_path.clone().unwrap_or_else(|| "<string>".to_string());
        let loaded = GrammarBuilder::load(grammar, &source, options.import_paths.clone(), options.keep_all_tokens)?;
        Lark::from_grammar(loaded, options)
    }

    /// Starts configuring a parser for the grammar.
    pub fn builder(grammar: impl Into<String>) -> LarkBuilder {
        LarkBuilder {
            grammar: grammar.into(),
            options: LarkOptions::default(),
        }
    }

    /// Reads the grammar from a file and compiles it. Relative imports are resolved from the file's directory.
    pub fn open(path: impl AsRef<std::path::Path>, mut options: LarkOptions) -> Result<Lark> {
        let path = path.as_ref();
        let text = std::fs::read_to_string(path)?;
        if options.source_path.is_none() {
            options.source_path = Some(path.to_string_lossy().into_owned());
        }
        Lark::new(&text, options)
    }

    /// Compiles a loaded grammar into a parser.
    pub fn from_grammar(grammar: Grammar, mut options: LarkOptions) -> Result<Lark> {
        if options.start.is_empty() {
            return Err(Error::config("At least one start rule is required"));
        }
        if options.lexer == LexerKind::Auto {
            options.lexer = match options.parser {
                ParserKind::Lalr => LexerKind::Contextual,
                ParserKind::Earley if options.postlex.is_some() => LexerKind::Basic,
                ParserKind::Earley => LexerKind::Dynamic,
                ParserKind::Cyk => LexerKind::Basic,
            };
        }
        let dynamic = matches!(options.lexer, LexerKind::Dynamic | LexerKind::DynamicComplete);
        if options.postlex.is_some() && dynamic {
            return Err(Error::config(
                "Can't use postlex with a dynamic lexer. Use basic or contextual instead",
            ));
        }
        if options.lexer == LexerKind::Custom && options.custom_lexer.is_none() {
            return Err(Error::config("LexerKind::Custom requires a custom_lexer"));
        }
        if options.ambiguity == Ambiguity::Auto {
            if options.parser == ParserKind::Earley {
                options.ambiguity = Ambiguity::Resolve;
            }
        } else if options.parser == ParserKind::Lalr {
            return Err(Error::config(
                "'lalr' doesn't support disambiguation. Use one of these parsers instead: earley, cyk",
            ));
        }
        if options.priority == PriorityMode::Auto {
            options.priority = PriorityMode::Normal;
        }

        let terminals_to_keep: HashSet<String> = options
            .postlex
            .as_ref()
            .map(|p| p.always_accept().into_iter().collect())
            .unwrap_or_default();

        // Compile the EBNF grammar into BNF
        let compiled = grammar.compile(&options.start, Some(&terminals_to_keep))?;
        let mut terminals = compiled.terminals;
        let mut rules = compiled.rules;
        match options.priority {
            PriorityMode::Invert => {
                for rule in &mut rules {
                    rule.options.priority = rule.options.priority.map(|p| -p);
                }
                for t in &mut terminals {
                    t.priority = -t.priority;
                }
            }
            PriorityMode::None => {
                for rule in &mut rules {
                    rule.options.priority = None;
                }
                for t in &mut terminals {
                    t.priority = 0;
                }
            }
            _ => {}
        }
        let rules: Vec<Arc<Rule>> = rules.into_iter().map(Arc::new).collect();

        let mut lexer_conf = LexerConf::new(terminals.clone(), compiled.ignore.clone());
        lexer_conf.callbacks = options.lexer_callbacks.clone();
        lexer_conf.regex_flags = Flags::from_letters(&options.regex_flags);
        lexer_conf.strict = options.strict;

        let expected: &[LexerKind] = match options.parser {
            ParserKind::Lalr => &[LexerKind::Basic, LexerKind::Contextual],
            ParserKind::Earley => &[LexerKind::Basic, LexerKind::Dynamic, LexerKind::DynamicComplete],
            ParserKind::Cyk => &[LexerKind::Basic],
        };
        if options.lexer != LexerKind::Custom && !expected.contains(&options.lexer) {
            return Err(Error::config(format!(
                "Parser {:?} does not support lexer {:?}, expected one of {:?}",
                options.parser, options.lexer, expected
            )));
        }

        let ambiguous = options.parser != ParserKind::Lalr && options.ambiguity == Ambiguity::Explicit;
        let callbacks = Arc::new(Callbacks::new(
            &rules,
            options.propagate_positions,
            ambiguous,
            options.maybe_placeholders,
        ));

        // Set up the parser, then the lexer
        let mut lalr_table = None;
        let parser = match options.parser {
            ParserKind::Lalr => {
                let analyzer = GrammarAnalyzer::new(&rules, &options.start, options.debug, options.strict)?;
                let table = Arc::new(compute_lalr(&analyzer)?);
                lalr_table = Some(table.clone());
                ParserImpl::Lalr(LalrParser { table, callbacks })
            }
            ParserKind::Earley => {
                let dynamic = match options.lexer {
                    LexerKind::Dynamic => Some(false),
                    LexerKind::DynamicComplete => Some(true),
                    _ => None,
                };
                if dynamic.is_some() && !options.lexer_callbacks.is_empty() {
                    return Err(Error::grammar("Earley's dynamic lexer doesn't support lexer_callbacks."));
                }
                ParserImpl::Earley(EarleyParser::new(
                    rules.clone(),
                    &options.start,
                    callbacks,
                    &terminals,
                    lexer_conf.terminals_by_name.clone(),
                    &compiled.ignore,
                    lexer_conf.regex_flags,
                    dynamic,
                    options.ambiguity == Ambiguity::Resolve,
                )?)
            }
            ParserKind::Cyk => ParserImpl::Cyk(CykParser::new(rules.clone(), callbacks)?),
        };

        let lexer = match options.lexer {
            LexerKind::Dynamic | LexerKind::DynamicComplete => None,
            LexerKind::Basic => Some(LexerImpl::Basic(BasicLexer::new(&lexer_conf)?)),
            LexerKind::Contextual => {
                let table = lalr_table.as_ref().expect("contextual lexer requires LALR");
                let states: IndexMap<usize, Vec<String>> = table
                    .states
                    .iter()
                    .enumerate()
                    .map(|(i, s)| (i, s.keys().cloned().collect()))
                    .collect();
                let always_accept = options.postlex.as_ref().map(|p| p.always_accept()).unwrap_or_default();
                Some(LexerImpl::Contextual(ContextualLexer::new(&lexer_conf, &states, &always_accept)?))
            }
            LexerKind::Custom => Some(LexerImpl::Custom(options.custom_lexer.clone().expect("checked above"))),
            LexerKind::Auto => unreachable!("resolved above"),
        };

        Ok(Lark {
            options,
            grammar,
            terminals,
            rules,
            ignore_tokens: compiled.ignore,
            lexer_conf,
            parser,
            lexer,
        })
    }

    /// The options, with the automatic choices resolved.
    pub fn options(&self) -> &LarkOptions {
        &self.options
    }

    /// The loaded grammar.
    pub fn grammar(&self) -> &Grammar {
        &self.grammar
    }

    /// The compiled terminals.
    pub fn terminals(&self) -> &[TerminalDef] {
        &self.terminals
    }

    /// A terminal by name.
    pub fn get_terminal(&self, name: &str) -> Option<&TerminalDef> {
        self.terminals.iter().find(|t| t.name == name)
    }

    /// The compiled (BNF) rules.
    pub fn rules(&self) -> impl Iterator<Item = &Rule> {
        self.rules.iter().map(|r| r.as_ref())
    }

    /// The names of the ignored terminals.
    pub fn ignore_tokens(&self) -> &[String] {
        &self.ignore_tokens
    }

    fn verify_start<'s>(&'s self, start: Option<&'s str>) -> Result<&'s str> {
        match start {
            None => {
                if self.options.start.len() > 1 {
                    return Err(Error::config(format!(
                        "Lark initialized with more than 1 possible start rule. Must specify which start rule to parse: {:?}",
                        self.options.start
                    )));
                }
                Ok(&self.options.start[0])
            }
            Some(s) => {
                if !self.options.start.iter().any(|x| x == s) {
                    return Err(Error::config(format!(
                        "Unknown start rule {}. Must be one of {:?}",
                        s, self.options.start
                    )));
                }
                Ok(s)
            }
        }
    }

    fn thread<'a>(&'a self, text: &'a str) -> LexerThread<'a> {
        let lexer = self.lexer.as_ref().expect("a lexer");
        LexerThread::new(lexer, self.options.postlex.as_deref(), LexerState::new(text, 0, text.len()))
    }

    /// Parses the text and returns the tree. Fails when the result isn't a tree (a `?start` rule that inlines a
    /// single token); use [`Lark::parse_node`] for those.
    pub fn parse(&self, text: &str) -> Result<Tree> {
        self.parse_start(text, None)
    }

    /// Parses the text from the given start rule.
    pub fn parse_start(&self, text: &str, start: Option<&str>) -> Result<Tree> {
        match self.parse_node(text, start)? {
            Node::Tree(t) => Ok(t),
            other => Err(Error::Parse(format!("The parse result is not a tree: {}", other))),
        }
    }

    /// Parses the text and returns the result: a tree, or a token when the start rule inlines a single token.
    pub fn parse_node(&self, text: &str, start: Option<&str>) -> Result<Node> {
        let start = self.verify_start(start)?;
        match &self.parser {
            ParserImpl::Lalr(p) => {
                let thread = self.thread(text);
                let mut ps = p.new_state(start)?;
                p.parse_from_state(&mut ps, &thread, None)
            }
            ParserImpl::Earley(p) => match &self.lexer {
                None => p.parse(EarleyInput::Text(text), start),
                Some(_) => {
                    let thread = self.thread(text);
                    p.parse(EarleyInput::Tokens(thread.lex(None)), start)
                }
            },
            ParserImpl::Cyk(p) => {
                let thread = self.thread(text);
                let tokens = thread.lex(None).collect::<Result<Vec<Token>>>()?;
                p.parse(tokens, start)
            }
        }
    }

    /// Parses with error recovery (LALR only): on an unexpected-input error, `on_error` gets the error and the
    /// interactive parser at the error, and returns whether to resume parsing (Lark's `on_error`).
    pub fn parse_with_recovery(
        &self,
        text: &str,
        start: Option<&str>,
        on_error: &mut dyn FnMut(&Error, &mut InteractiveParser<'_>) -> bool,
    ) -> Result<Node> {
        let start = self.verify_start(start)?;
        match &self.parser {
            ParserImpl::Lalr(p) => {
                let thread = self.thread(text);
                parse_with_recovery(p, start, thread, &mut |e, ip| on_error(e, ip))
            }
            _ => Err(Error::config("The on_error option is only implemented for the LALR(1) parser.")),
        }
    }

    /// Starts an interactive parse of the text (LALR only).
    pub fn parse_interactive<'a>(&'a self, text: &'a str, start: Option<&'a str>) -> Result<InteractiveParser<'a>> {
        let start = self.verify_start(start)?;
        match &self.parser {
            ParserImpl::Lalr(p) => Ok(InteractiveParser::new(p, p.new_state(start)?, self.thread(text))),
            _ => Err(Error::config("parse_interactive() currently only works with the LALR parser")),
        }
    }

    /// Only lexes the text, with the basic lexer (and the post-lexer). With `dont_ignore`, the ignored tokens are
    /// returned too.
    pub fn lex(&self, text: &str, dont_ignore: bool) -> Result<Vec<Token>> {
        let mut conf = self.lexer_conf.clone();
        if dont_ignore {
            conf.ignore = Vec::new();
        }
        let lexer = match (&self.options.lexer, &self.options.custom_lexer) {
            (LexerKind::Custom, Some(custom)) => LexerImpl::Custom(custom.clone()),
            _ => LexerImpl::Basic(BasicLexer::new(&conf)?),
        };
        let thread = LexerThread::new(&lexer, self.options.postlex.as_deref(), LexerState::new(text, 0, text.len()));
        let tokens = thread.lex(None).collect::<Result<Vec<Token>>>();
        tokens
    }
}

/// Configures a [`Lark`] parser.
#[derive(Clone)]
pub struct LarkBuilder {
    grammar: String,
    options: LarkOptions,
}

impl LarkBuilder {
    /// Uses the LALR(1) parser (with the contextual lexer, unless another lexer is chosen).
    pub fn lalr(self) -> Self {
        self.parser(ParserKind::Lalr)
    }

    pub fn parser(mut self, parser: ParserKind) -> Self {
        self.options.parser = parser;
        self
    }

    pub fn lexer(mut self, lexer: LexerKind) -> Self {
        self.options.lexer = lexer;
        self
    }

    pub fn start(mut self, start: impl Into<String>) -> Self {
        self.options.start = vec![start.into()];
        self
    }

    pub fn starts<S: Into<String>>(mut self, start: impl IntoIterator<Item = S>) -> Self {
        self.options.start = start.into_iter().map(Into::into).collect();
        self
    }

    pub fn ambiguity(mut self, ambiguity: Ambiguity) -> Self {
        self.options.ambiguity = ambiguity;
        self
    }

    pub fn priority(mut self, priority: PriorityMode) -> Self {
        self.options.priority = priority;
        self
    }

    pub fn propagate_positions(mut self, value: bool) -> Self {
        self.options.propagate_positions = value;
        self
    }

    pub fn maybe_placeholders(mut self, value: bool) -> Self {
        self.options.maybe_placeholders = value;
        self
    }

    pub fn keep_all_tokens(mut self, value: bool) -> Self {
        self.options.keep_all_tokens = value;
        self
    }

    pub fn strict(mut self, value: bool) -> Self {
        self.options.strict = value;
        self
    }

    pub fn regex_flags(mut self, flags: impl Into<String>) -> Self {
        self.options.regex_flags = flags.into();
        self
    }

    pub fn postlex(mut self, postlex: impl PostLex + 'static) -> Self {
        self.options.postlex = Some(Arc::new(postlex));
        self
    }

    pub fn lexer_callback(mut self, terminal: impl Into<String>, callback: impl Fn(Token) -> Token + Send + Sync + 'static) -> Self {
        self.options.lexer_callbacks.insert(terminal.into(), Arc::new(callback));
        self
    }

    pub fn custom_lexer(mut self, lexer: impl CustomLexer + 'static) -> Self {
        self.options.lexer = LexerKind::Custom;
        self.options.custom_lexer = Some(Arc::new(lexer));
        self
    }

    pub fn import_path(mut self, loader: impl GrammarLoader + 'static) -> Self {
        self.options.import_paths.push(Arc::new(loader));
        self
    }

    pub fn source_path(mut self, path: impl Into<String>) -> Self {
        self.options.source_path = Some(path.into());
        self
    }

    /// Changes the options directly.
    pub fn options(mut self, f: impl FnOnce(&mut LarkOptions)) -> Self {
        f(&mut self.options);
        self
    }

    pub fn build(self) -> Result<Lark> {
        Lark::new(&self.grammar, self.options)
    }
}
