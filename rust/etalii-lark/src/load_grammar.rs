//! Loading grammars: the parser of Lark's grammar syntax, `%import` and the other statements, and the
//! compilation of the EBNF rules into BNF (Lark's `load_grammar`).

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::sync::{Arc, OnceLock};

use indexmap::{IndexMap, IndexSet};

use crate::error::{get_context, Error, Result, UnexpectedToken};
use crate::grammar::{Rule, RuleOptions, Symbol, TOKEN_DEFAULT_PRIORITY};
use crate::lexer::{BasicLexer, LexerConf, LexerImpl, LexerState, LexerThread, Pattern, TerminalDef};
use crate::parse_tree_builder::Callbacks;
use crate::parsers::grammar_analysis::GrammarAnalyzer;
use crate::parsers::lalr_analysis::compute_lalr;
use crate::parsers::lalr_parser::LalrParser;
use crate::token::Token;
use crate::tree::Node;
use crate::utils::{bfs, classify, is_id_continue, is_id_start, is_upper, repr, small_factors};

// ---------------------------------------------------------------------------------------------------------------
//  Grammar trees
// ---------------------------------------------------------------------------------------------------------------

/// A node of a grammar's tree, as the grammar is loaded and compiled.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub(crate) enum GNode {
    Tree(GTree),
    Token(Token),
    Sym(Symbol),
    Pattern(Pattern),
    /// The positions of the items of a `[maybe]` that didn't match (Lark's `_EMPTY`).
    Empty,
}

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub(crate) struct GTree {
    pub data: String,
    pub children: Vec<GNode>,
}

fn st(data: &str, children: Vec<GNode>) -> GNode {
    GNode::Tree(GTree {
        data: data.to_string(),
        children,
    })
}

impl GNode {
    fn as_tree(&self) -> Option<&GTree> {
        match self {
            GNode::Tree(t) => Some(t),
            _ => None,
        }
    }

    fn as_token(&self) -> Option<&Token> {
        match self {
            GNode::Token(t) => Some(t),
            _ => None,
        }
    }

    fn describe(&self) -> String {
        match self {
            GNode::Tree(t) => format!("Tree({}, {:?})", repr(&t.data), t.children),
            GNode::Token(t) => format!("{:?}", t),
            GNode::Sym(s) => format!("{:?}", s),
            GNode::Pattern(p) => format!("{:?}", p),
            GNode::Empty => "Symbol('__empty__')".to_string(),
        }
    }
}

impl GTree {
    /// The paths of all subtrees in the order of Lark's `iter_subtrees`: bottom-up, the root last.
    fn subtree_paths(&self) -> Vec<Vec<usize>> {
        let mut queue: Vec<(Vec<usize>, &GTree)> = vec![(Vec::new(), self)];
        let mut i = 0;
        while i < queue.len() {
            let (path, t) = (queue[i].0.clone(), queue[i].1);
            i += 1;
            for (j, c) in t.children.iter().enumerate().rev() {
                if let GNode::Tree(ct) = c {
                    let mut p = path.clone();
                    p.push(j);
                    queue.push((p, ct));
                }
            }
        }
        queue.into_iter().rev().map(|(p, _)| p).collect()
    }

    fn at(&self, path: &[usize]) -> &GTree {
        let mut t = self;
        for &i in path {
            t = t.children[i].as_tree().expect("path to a subtree");
        }
        t
    }

    fn at_mut(&mut self, path: &[usize]) -> &mut GTree {
        let mut t = self;
        for &i in path {
            t = match &mut t.children[i] {
                GNode::Tree(c) => c,
                _ => unreachable!("path to a subtree"),
            };
        }
        t
    }

    /// The paths of the subtrees with the given data, in the order of Lark's `find_data`.
    fn find_data(&self, data: &str) -> Vec<Vec<usize>> {
        self.subtree_paths().into_iter().filter(|p| self.at(p).data == data).collect()
    }

    fn scan_symbols<'a>(&'a self, out: &mut Vec<&'a Symbol>) {
        for c in &self.children {
            match c {
                GNode::Tree(t) => t.scan_symbols(out),
                GNode::Sym(s) => out.push(s),
                _ => {}
            }
        }
    }

    fn expand_kids_by_data(&mut self, data: &str) -> bool {
        let mut changed = false;
        let mut i = self.children.len();
        while i > 0 {
            i -= 1;
            if matches!(&self.children[i], GNode::Tree(t) if t.data == data) {
                let child = match self.children.remove(i) {
                    GNode::Tree(t) => t,
                    _ => unreachable!(),
                };
                let n = child.children.len();
                self.children.splice(i..i, child.children);
                let _ = n;
                changed = true;
            }
        }
        changed
    }

    fn flatten(&mut self) {
        let data = self.data.clone();
        while self.expand_kids_by_data(&data) {}
    }

    fn rename_symbols(&mut self, f: &dyn Fn(&str) -> String) {
        for c in &mut self.children {
            match c {
                GNode::Tree(t) => t.rename_symbols(f),
                GNode::Sym(s) => *s = s.renamed(f),
                _ => {}
            }
        }
    }
}

/// Lark's `Transformer_InPlace`: replaces each subtree, bottom-up, with the result of `f`.
fn in_place(mut tree: GTree, f: &mut dyn FnMut(GTree) -> Result<GNode>) -> Result<GNode> {
    for path in tree.subtree_paths() {
        let sub = tree.at_mut(&path);
        let children = std::mem::take(&mut sub.children);
        let mut new_children = Vec::with_capacity(children.len());
        for c in children {
            new_children.push(match c {
                GNode::Tree(t) => f(t)?,
                other => other,
            });
        }
        sub.children = new_children;
    }
    f(tree)
}

fn in_place_tree(tree: GTree, f: &mut dyn FnMut(GTree) -> Result<GNode>) -> Result<GTree> {
    match in_place(tree, f)? {
        GNode::Tree(t) => Ok(t),
        other => Err(Error::grammar(format!("Unexpected rule structure: {}", other.describe()))),
    }
}

fn dedup(items: Vec<GNode>) -> Vec<GNode> {
    let set: IndexSet<GNode> = items.into_iter().collect();
    set.into_iter().collect()
}

fn from_node(node: Node) -> GNode {
    match node {
        Node::Tree(t) => GNode::Tree(GTree {
            data: t.data,
            children: t.children.into_iter().map(from_node).collect(),
        }),
        Node::Token(t) => GNode::Token(t),
        Node::Missing => GNode::Empty,
    }
}

// ---------------------------------------------------------------------------------------------------------------
//  The meta grammar: the grammar of Lark grammars
// ---------------------------------------------------------------------------------------------------------------

const META_TERMINALS: &[(&str, &str)] = &[
    ("_LPAR", r"\("),
    ("_RPAR", r"\)"),
    ("_LBRA", r"\["),
    ("_RBRA", r"\]"),
    ("_LBRACE", r"\{"),
    ("_RBRACE", r"\}"),
    ("OP", "[+*]|[?](?![a-z_])"),
    ("_COLON", ":"),
    ("_COMMA", ","),
    ("_OR", r"\|"),
    ("_DOT", r"\.(?!\.)"),
    ("_DOTDOT", r"\.\."),
    ("TILDE", "~"),
    ("RULE_MODIFIERS", "(!|![?]?|[?]!?)(?=[_a-z])"),
    ("RULE", "_?[a-z][_a-z0-9]*"),
    ("TERMINAL", "_?[A-Z][_A-Z0-9]*"),
    ("STRING", r#""(\\"|\\\\|[^"\n])*?"i?"#),
    ("REGEXP", r"/(?!/)(\\/|\\\\|[^/])*?/[imslux]*"),
    ("_NL", r"(\r?\n)+\s*"),
    ("_NL_OR", r"(\r?\n)+\s*\|"),
    ("WS", r"[ \t]+"),
    ("COMMENT", r"\s*//[^\n]*|\s*#[^\n]*"),
    ("BACKSLASH", r"\\[ ]*\n"),
    ("_TO", "->"),
    ("_IGNORE", "%ignore"),
    ("_OVERRIDE", "%override"),
    ("_DECLARE", "%declare"),
    ("_EXTEND", "%extend"),
    ("_IMPORT", "%import"),
    ("NUMBER", r"[+-]?\d+"),
];

const META_RULES: &[(&str, &[&str])] = &[
    ("start", &["_list"]),
    ("_list", &["_item", "_list _item"]),
    (
        "_item",
        &["rule", "term", "ignore", "import", "declare", "override", "extend", "_NL"],
    ),
    ("rule", &["rule_modifiers RULE template_params priority _COLON expansions _NL"]),
    ("rule_modifiers", &["RULE_MODIFIERS", ""]),
    ("priority", &["_DOT NUMBER", ""]),
    ("template_params", &["_LBRACE _template_params _RBRACE", ""]),
    ("_template_params", &["RULE", "_template_params _COMMA RULE"]),
    ("expansions", &["_expansions"]),
    ("_expansions", &["alias", "_expansions _OR alias", "_expansions _NL_OR alias"]),
    ("?alias", &["expansion _TO nonterminal", "expansion"]),
    ("expansion", &["_expansion"]),
    ("_expansion", &["", "_expansion expr"]),
    (
        "?expr",
        &["atom", "atom OP", "atom TILDE NUMBER", "atom TILDE NUMBER _DOTDOT NUMBER"],
    ),
    ("?atom", &["_LPAR expansions _RPAR", "maybe", "value"]),
    ("value", &["terminal", "nonterminal", "literal", "range", "template_usage"]),
    ("terminal", &["TERMINAL"]),
    ("nonterminal", &["RULE"]),
    ("?name", &["RULE", "TERMINAL"]),
    ("?symbol", &["terminal", "nonterminal"]),
    ("maybe", &["_LBRA expansions _RBRA"]),
    ("range", &["STRING _DOTDOT STRING"]),
    ("template_usage", &["nonterminal _LBRACE _template_args _RBRACE"]),
    ("_template_args", &["value", "_template_args _COMMA value"]),
    (
        "term",
        &["TERMINAL _COLON expansions _NL", "TERMINAL _DOT NUMBER _COLON expansions _NL"],
    ),
    ("override", &["_OVERRIDE rule", "_OVERRIDE term"]),
    ("extend", &["_EXTEND rule", "_EXTEND term"]),
    ("ignore", &["_IGNORE expansions _NL"]),
    ("declare", &["_DECLARE _declare_args _NL"]),
    (
        "import",
        &[
            "_IMPORT _import_path _NL",
            "_IMPORT _import_path _LPAR name_list _RPAR _NL",
            "_IMPORT _import_path _TO name _NL",
        ],
    ),
    ("_import_path", &["import_lib", "import_rel"]),
    ("import_lib", &["_import_args"]),
    ("import_rel", &["_DOT _import_args"]),
    ("_import_args", &["name", "_import_args _DOT name"]),
    ("name_list", &["_name_list"]),
    ("_name_list", &["name", "_name_list _COMMA name"]),
    ("_declare_args", &["symbol", "_declare_args symbol"]),
    ("literal", &["REGEXP", "STRING"]),
];

/// Example grammars for each syntax error, used to describe the errors (Lark's `GRAMMAR_ERRORS`).
pub const GRAMMAR_ERRORS: &[(&str, &[&str])] = &[
    ("Incorrect type of value", &["a: 1\n"]),
    ("Unclosed parenthesis", &["a: (\n"]),
    ("Unmatched closing parenthesis", &["a: )\n", "a: [)\n", "a: (]\n"]),
    (
        "Expecting rule or terminal definition (missing colon)",
        &["a\n", "A\n", "a->\n", "A->\n", "a A\n"],
    ),
    ("Illegal name for rules or terminals", &["Aa:\n"]),
    ("Alias expects lowercase name", &["a: -> \"a\"\n"]),
    ("Unexpected colon", &["a::\n", "a: b:\n", "a: B:\n", "a: \"a\":\n"]),
    ("Misplaced operator", &["a: b??", "a: b(?)", "a:+\n", "a:?\n", "a:*\n", "a:|*\n"]),
    ("Expecting option (\"|\") or a new rule or terminal definition", &["a:a\n()\n"]),
    ("Terminal names cannot contain dots", &["A.B\n"]),
    ("Expecting rule or terminal definition", &["\"a\"\n"]),
    ("%import expects a name", &["%import \"a\"\n"]),
    ("%ignore expects a value", &["%ignore %import\n"]),
];

struct MetaParser {
    parser: LalrParser,
    lexer: LexerImpl,
}

fn symbol_from_strcase(s: &str) -> Symbol {
    if is_upper(s) {
        Symbol::terminal(s, s.starts_with('_'))
    } else {
        Symbol::rule(s)
    }
}

impl MetaParser {
    fn build() -> MetaParser {
        let terminals: Vec<TerminalDef> = META_TERMINALS
            .iter()
            .map(|(n, p)| TerminalDef::new(*n, Pattern::regex(*p, "", None)))
            .collect();
        let mut rules = Vec::new();
        for (name, expansions) in META_RULES {
            let options = RuleOptions {
                expand1: name.starts_with('?'),
                ..Default::default()
            };
            let origin = Symbol::rule(name.trim_start_matches('?'));
            for (i, e) in expansions.iter().enumerate() {
                let expansion = e.split_whitespace().map(symbol_from_strcase).collect();
                rules.push(Arc::new(Rule::new(origin.clone(), expansion, i, None, options.clone())));
            }
        }
        let analyzer = GrammarAnalyzer::new(&rules, &["start".to_string()], false, false).expect("valid meta grammar");
        let table = compute_lalr(&analyzer).expect("LALR meta grammar");
        let callbacks = Callbacks::new(&rules, false, false, false);
        let conf = LexerConf::new(terminals, vec!["WS".into(), "COMMENT".into(), "BACKSLASH".into()]);
        let lexer = LexerImpl::Basic(BasicLexer::new(&conf).expect("valid meta terminals"));
        MetaParser {
            parser: LalrParser {
                table: Arc::new(table),
                callbacks: Arc::new(callbacks),
            },
            lexer,
        }
    }

    fn get() -> &'static MetaParser {
        static PARSER: OnceLock<MetaParser> = OnceLock::new();
        PARSER.get_or_init(MetaParser::build)
    }

    fn parse(&self, text: &str) -> Result<Node> {
        let thread = LexerThread::new(&self.lexer, None, LexerState::new(text, 0, text.len()));
        let mut ps = self.parser.new_state("start")?;
        self.parser.parse_from_state(&mut ps, &thread, None)
    }
}

fn translate_parser_error(e: &UnexpectedToken) -> Option<String> {
    let meta = MetaParser::get();
    let error = Error::UnexpectedToken(Box::new(e.clone()));
    let ui = error.as_unexpected_input()?;
    let examples = GRAMMAR_ERRORS.iter().map(|(label, examples)| (*label, examples.to_vec()));
    if let Some(label) = ui.match_examples(|s| meta.parse(s).map(|_| ()), examples, false, true) {
        return Some(label.to_string());
    }
    if e.expected.contains("STRING") {
        return Some("Expecting a value".to_string());
    }
    None
}

/// Parses the text of a grammar into its tree (Lark's `_parse_grammar`).
fn parse_grammar(text: &str, name: &str) -> Result<GTree> {
    let full = format!("{}\n", text);
    let tree = match MetaParser::get().parse(&full) {
        Ok(tree) => tree,
        Err(Error::UnexpectedCharacters(e)) => {
            let context = get_context(text, e.pos_in_stream, 40);
            return Err(Error::grammar(format!(
                "Unexpected input at line {} column {} in {}: \n\n{}",
                e.line, e.column, name, context
            )));
        }
        Err(Error::UnexpectedToken(e)) => {
            let context = get_context(text, e.pos_in_stream.unwrap_or(0), 40);
            if let Some(error) = translate_parser_error(&e) {
                return Err(Error::grammar(format!(
                    "{}, at line {} column {}\n\n{}",
                    error, e.line, e.column, context
                )));
            }
            return Err(Error::UnexpectedToken(e));
        }
        Err(e) => return Err(e),
    };
    let tree = match from_node(tree) {
        GNode::Tree(t) => t,
        _ => unreachable!("the meta parser returns a tree"),
    };
    // Lark's PrepareGrammar: terminal and nonterminal nodes become symbols
    in_place_tree(tree, &mut |t| {
        Ok(match t.data.as_str() {
            "terminal" => {
                let name = t.children[0].as_token().map(|t| t.value.clone()).unwrap_or_default();
                let filter_out = name.starts_with('_');
                GNode::Sym(Symbol::terminal(name, filter_out))
            }
            "nonterminal" => GNode::Sym(Symbol::rule(t.children[0].as_token().map(|t| t.value.clone()).unwrap_or_default())),
            _ => GNode::Tree(t),
        })
    })
}

// ---------------------------------------------------------------------------------------------------------------
//  Literals
// ---------------------------------------------------------------------------------------------------------------

/// Evaluates the escapes of a grammar literal, as Lark does with Python's `literal_eval` (Lark's `eval_escaping`).
pub(crate) fn eval_escaping(s: &str) -> Result<String> {
    let mut w = String::with_capacity(s.len());
    let mut chars = s.chars();
    while let Some(n) = chars.next() {
        w.push(n);
        if n == '\\' {
            let n2 = chars
                .next()
                .ok_or_else(|| Error::grammar(format!("Literal ended unexpectedly (bad escaping): `{}`", repr(s))))?;
            if n2 == '\\' {
                w.push_str("\\\\");
            } else if !"Uuxnftr".contains(n2) {
                w.push('\\');
            }
            w.push(n2);
        }
    }
    let w = w.replace("\\\"", "\"").replace('\'', "\\'");
    literal_eval(&w).map_err(|e| Error::grammar(format!("{}: {}", s, e)))
}

/// Evaluates the escapes of the body of a Python string literal.
fn literal_eval(s: &str) -> std::result::Result<String, String> {
    let chars: Vec<char> = s.chars().collect();
    let mut out = String::with_capacity(s.len());
    let mut i = 0;
    let hex = |i: usize, digits: usize, e: char| -> std::result::Result<u32, String> {
        let error = || format!("truncated \\{}{} escape", e, "X".repeat(digits));
        if i + digits > chars.len() {
            return Err(error());
        }
        let text: String = chars[i..i + digits].iter().collect();
        if !text.chars().all(|c| c.is_ascii_hexdigit()) {
            return Err(error());
        }
        u32::from_str_radix(&text, 16).map_err(|_| error())
    };
    while i < chars.len() {
        let c = chars[i];
        i += 1;
        if c != '\\' {
            out.push(c);
            continue;
        }
        let e = *chars.get(i).ok_or("EOL while scanning string literal")?;
        i += 1;
        match e {
            '\n' => {}
            '\\' => out.push('\\'),
            '\'' => out.push('\''),
            '"' => out.push('"'),
            'a' => out.push('\x07'),
            'b' => out.push('\x08'),
            'f' => out.push('\x0c'),
            'n' => out.push('\n'),
            'r' => out.push('\r'),
            't' => out.push('\t'),
            'v' => out.push('\x0b'),
            'x' | 'u' | 'U' => {
                let digits = match e {
                    'x' => 2,
                    'u' => 4,
                    _ => 8,
                };
                let cp = hex(i, digits, e)?;
                i += digits;
                out.push(char::from_u32(cp).ok_or("illegal Unicode character")?);
            }
            'N' => return Err("\\N{...} escapes are not supported".to_string()),
            '0'..='7' => {
                let mut value = e as u32 - '0' as u32;
                let mut count = 1;
                while count < 3 && i < chars.len() && ('0'..='7').contains(&chars[i]) {
                    value = value * 8 + (chars[i] as u32 - '0' as u32);
                    i += 1;
                    count += 1;
                }
                out.push(char::from_u32(value).ok_or("illegal character")?);
            }
            other => {
                // Unknown escapes are kept as they are
                out.push('\\');
                out.push(other);
            }
        }
    }
    Ok(out)
}

const RE_FLAGS: &str = "imslux";

fn literal_to_pattern(literal: &Token) -> Result<Pattern> {
    let v = literal.value.as_str();
    let flag_start = v.rfind(['/', '"']).map(|i| i + 1).unwrap_or(0);
    if flag_start == 0 {
        return Err(Error::grammar(format!("Bad literal {}", v)));
    }
    let flags = &v[flag_start..];
    if !flags.chars().all(|f| RE_FLAGS.contains(f)) {
        return Err(Error::grammar(format!("Bad flags {}", flags)));
    }
    if literal.kind == "STRING" && v.contains('\n') {
        return Err(Error::grammar("You cannot put newlines in string literals"));
    }
    if literal.kind == "REGEXP" && v.contains('\n') && !flags.contains('x') {
        return Err(Error::grammar(
            "You can only use newlines in regular expressions with the `x` (verbose) flag",
        ));
    }
    let body = &v[..flag_start];
    let x = &body[1..body.len() - 1];
    let s = eval_escaping(x)?;
    if s.is_empty() {
        return Err(Error::grammar(format!("Empty terminals are not allowed ({})", literal.value)));
    }
    match literal.kind.as_str() {
        "STRING" => Ok(Pattern::string(s.replace("\\\\", "\\"), flags, Some(literal.value.clone()))),
        "REGEXP" => Ok(Pattern::regex(s, flags, Some(literal.value.clone()))),
        _ => Err(Error::grammar("Invariant failed: literal.type not in [\"STRING\", \"REGEXP\"]")),
    }
}

/// Lark's `PrepareLiterals`: literals and ranges become patterns.
fn prepare_literals(tree: GTree) -> Result<GTree> {
    in_place_tree(tree, &mut |t| match t.data.as_str() {
        "literal" => {
            let token = t.children[0].as_token().ok_or_else(|| Error::grammar("Bad literal"))?;
            Ok(st("pattern", vec![GNode::Pattern(literal_to_pattern(token)?)]))
        }
        "range" => {
            let (start, end) = match (t.children[0].as_token(), t.children[1].as_token()) {
                (Some(a), Some(b)) => (a, b),
                _ => return Err(Error::grammar("Bad range")),
            };
            let s = &start.value[1..start.value.len() - 1];
            let e = &end.value[1..end.value.len() - 1];
            if eval_escaping(s)?.chars().count() != 1 || eval_escaping(e)?.chars().count() != 1 {
                return Err(Error::grammar(format!(
                    "Range bounds must be single characters ({}..{})",
                    start.value, end.value
                )));
            }
            Ok(st(
                "pattern",
                vec![GNode::Pattern(Pattern::regex(format!("[{}-{}]", s, e), "", None))],
            ))
        }
        _ => Ok(GNode::Tree(t)),
    })
}

// ---------------------------------------------------------------------------------------------------------------
//  Terminals
// ---------------------------------------------------------------------------------------------------------------

fn parse_int(node: &GNode) -> Result<i64> {
    let text = match node {
        GNode::Token(t) => t.value.as_str(),
        other => return Err(Error::grammar(format!("Expected a number, got {}", other.describe()))),
    };
    text.parse::<i64>()
        .map_err(|_| Error::grammar(format!("Number out of range: {}", text)))
}

/// Lark's `TerminalTreeToPattern`: combines a terminal's tree into one pattern.
fn terminal_tree_to_pattern(node: GNode) -> Result<Pattern> {
    match terminal_node(node)? {
        GNode::Pattern(p) => Ok(p),
        other => Err(Error::grammar(format!("Invalid terminal definition: {}", other.describe()))),
    }
}

fn expect_pattern(node: GNode) -> Result<Pattern> {
    match node {
        GNode::Pattern(p) => Ok(p),
        other => Err(Error::grammar(format!("Invalid terminal definition: {}", other.describe()))),
    }
}

fn terminal_node(node: GNode) -> Result<GNode> {
    let tree = match node {
        GNode::Tree(t) => t,
        other => return Ok(other),
    };
    let mut args = Vec::with_capacity(tree.children.len());
    for c in tree.children {
        args.push(terminal_node(c)?);
    }
    Ok(match tree.data.as_str() {
        "pattern" => args.into_iter().next().unwrap_or(GNode::Empty),
        "expansion" => {
            if args.is_empty() {
                GNode::Pattern(Pattern::string("", "", None))
            } else if args.len() == 1 {
                args.into_iter().next().unwrap()
            } else {
                let mut pattern = String::new();
                for a in args {
                    pattern.push_str(&expect_pattern(a)?.to_regexp());
                }
                GNode::Pattern(Pattern::regex(pattern, "", None))
            }
        }
        "expansions" => {
            if args.len() == 1 {
                args.into_iter().next().unwrap()
            } else {
                let mut exps = Vec::with_capacity(args.len());
                for a in args {
                    exps.push(expect_pattern(a)?);
                }
                // Make sure that the longest option is returned (Python's re module otherwise prefers just 'l'
                // when given (l|ll) and both could match)
                exps.sort_by(|a, b| {
                    b.max_width()
                        .cmp(&a.max_width())
                        .then_with(|| b.min_width().cmp(&a.min_width()))
                        .then_with(|| b.value().chars().count().cmp(&a.value().chars().count()))
                });
                let parts: Vec<String> = exps.iter().map(Pattern::to_regexp).collect();
                GNode::Pattern(Pattern::regex(format!("(?:{})", parts.join("|")), "", None))
            }
        }
        "expr" => GNode::Pattern(terminal_expr(args)?),
        "maybe" => {
            let mut args = args;
            args.push(GNode::Token(Token::new("OP", "?")));
            GNode::Pattern(terminal_expr(args)?)
        }
        "alias" => return Err(Error::grammar("Aliasing not allowed in terminals (You used -> in the wrong place)")),
        "value" => args.into_iter().next().unwrap_or(GNode::Empty),
        _ => GNode::Tree(GTree {
            data: tree.data,
            children: args,
        }),
    })
}

fn terminal_expr(args: Vec<GNode>) -> Result<Pattern> {
    let mut it = args.into_iter();
    let inner = expect_pattern(it.next().unwrap_or(GNode::Empty))?;
    let op_node = it.next().ok_or_else(|| Error::grammar("Bad terminal expression"))?;
    let rest: Vec<GNode> = it.collect();
    let mut op = op_node.as_token().map(|t| t.value.clone()).unwrap_or_default();
    if op == "~" {
        if rest.len() == 1 {
            op = format!("{{{}}}", parse_int(&rest[0])?);
        } else {
            let mn = parse_int(&rest[0])?;
            let mx = parse_int(&rest[1])?;
            if mx < mn {
                return Err(Error::grammar(format!("Bad Range for {:?} ({}..{} isn't allowed)", inner, mn, mx)));
            }
            op = format!("{{{},{}}}", mn, mx);
        }
    } else if !rest.is_empty() {
        return Err(Error::grammar("Unexpected arguments for terminal expression"));
    }
    Ok(Pattern::regex(format!("(?:{}){}", inner.to_regexp(), op), inner.flags(), None))
}

// ---------------------------------------------------------------------------------------------------------------
//  Rules
// ---------------------------------------------------------------------------------------------------------------

/// Lark's `_TERMINAL_NAMES`: the names given to anonymous terminals of punctuation.
fn anon_terminal_name(value: &str) -> Option<&'static str> {
    Some(match value {
        "." => "DOT",
        "," => "COMMA",
        ":" => "COLON",
        ";" => "SEMICOLON",
        "+" => "PLUS",
        "-" => "MINUS",
        "*" => "STAR",
        "/" => "SLASH",
        "\\" => "BACKSLASH",
        "|" => "VBAR",
        "?" => "QMARK",
        "!" => "BANG",
        "@" => "AT",
        "#" => "HASH",
        "$" => "DOLLAR",
        "%" => "PERCENT",
        "^" => "CIRCUMFLEX",
        "&" => "AMPERSAND",
        "_" => "UNDERSCORE",
        "<" => "LESSTHAN",
        ">" => "MORETHAN",
        "=" => "EQUAL",
        "\"" => "DBLQUOTE",
        "'" => "QUOTE",
        "`" => "BACKQUOTE",
        "~" => "TILDE",
        "(" => "LPAR",
        ")" => "RPAR",
        "{" => "LBRACE",
        "}" => "RBRACE",
        "[" => "LSQB",
        "]" => "RSQB",
        "\n" => "NEWLINE",
        "\r\n" => "CRLF",
        "\t" => "TAB",
        " " => "SPACE",
        _ => return None,
    })
}

/// Lark's `PrepareAnonTerminals`: gives the anonymous terminals (literals in rules) a name.
struct PrepareAnonTerminals {
    terminals: Vec<TerminalDef>,
    term_set: HashSet<String>,
    term_reverse: HashMap<Pattern, (String, String)>,
    i: usize,
    keep_all_tokens: bool,
}

impl PrepareAnonTerminals {
    fn new(terminals: Vec<TerminalDef>) -> PrepareAnonTerminals {
        let term_set = terminals.iter().map(|t| t.name.clone()).collect();
        let mut term_reverse = HashMap::new();
        for t in &terminals {
            term_reverse.insert(t.pattern.clone(), (t.name.clone(), t.pattern.flags().to_string()));
        }
        PrepareAnonTerminals {
            terminals,
            term_set,
            term_reverse,
            i: 0,
            keep_all_tokens: false,
        }
    }

    fn call(&mut self, t: GTree) -> Result<GNode> {
        if t.data != "pattern" {
            return Ok(GNode::Tree(t));
        }
        let p = match t.children.into_iter().next() {
            Some(GNode::Pattern(p)) => p,
            _ => return Err(Error::grammar("Bad pattern")),
        };
        if let Some((_, flags)) = self.term_reverse.get(&p) {
            if p.flags() != flags {
                return Err(Error::grammar(format!("Conflicting flags for the same terminal: {:?}", p)));
            }
        }
        let mut term_name: Option<String> = None;
        if p.is_str() {
            if let Some((name, _)) = self.term_reverse.get(&p) {
                // If already defined, use the user-defined terminal name
                term_name = Some(name.clone());
            } else {
                // Try to assign an indicative anon-terminal name
                let value = p.value();
                if let Some(n) = anon_terminal_name(value) {
                    term_name = Some(n.to_string());
                } else if !value.is_empty() && is_id_continue(value) && is_id_start(&value[..value.chars().next().unwrap().len_utf8()]) {
                    let upper = value.to_uppercase();
                    if !self.term_set.contains(&upper) {
                        term_name = Some(upper);
                    }
                }
                if term_name.as_ref().is_some_and(|n| self.term_set.contains(n)) {
                    term_name = None;
                }
            }
        } else if let Some((name, _)) = self.term_reverse.get(&p) {
            term_name = Some(name.clone());
        }

        let term_name = match term_name {
            Some(n) => n,
            None => {
                let n = format!("__ANON_{}", self.i);
                self.i += 1;
                n
            }
        };

        if !self.term_set.contains(&term_name) {
            self.term_set.insert(term_name.clone());
            self.term_reverse.insert(p.clone(), (term_name.clone(), p.flags().to_string()));
            self.terminals.push(TerminalDef::new(term_name.clone(), p.clone()));
        }

        let filter_out = if self.keep_all_tokens { false } else { p.is_str() };
        Ok(GNode::Sym(Symbol::terminal(term_name, filter_out)))
    }
}

/// Lark's `ValidateSymbols`: `value` nodes are replaced by what they contain.
fn validate_symbols(tree: GTree) -> Result<GTree> {
    in_place_tree(tree, &mut |t| {
        if t.data != "value" {
            return Ok(GNode::Tree(t));
        }
        let v = t.children.into_iter().next().unwrap_or(GNode::Empty);
        match v {
            GNode::Tree(_) | GNode::Sym(_) => Ok(v),
            other => Err(Error::grammar(format!("Unexpected value {}", other.describe()))),
        }
    })
}

#[derive(Clone, Debug)]
pub(crate) struct RuleDef {
    pub name: String,
    pub params: Vec<String>,
    pub tree: Option<GTree>,
    pub options: RuleOptions,
}

#[derive(Clone, Debug)]
pub(crate) struct TermDef {
    pub name: String,
    pub tree: Option<GTree>,
    pub priority: i64,
}

/// Lark's `ApplyTemplates`: creates a rule for each use of a template.
#[derive(Default)]
struct ApplyTemplates {
    created: HashSet<String>,
}

impl ApplyTemplates {
    fn call(&mut self, t: GTree, rule_defs: &mut Vec<RuleDef>) -> Result<GNode> {
        if t.data != "template_usage" {
            return Ok(GNode::Tree(t));
        }
        let mut it = t.children.into_iter();
        let name = match it.next() {
            Some(GNode::Sym(s)) => s.name.to_string(),
            _ => return Err(Error::grammar("Bad template usage")),
        };
        let mut args = Vec::new();
        for a in it {
            match a {
                GNode::Sym(s) => args.push(s),
                other => return Err(Error::grammar(format!("Bad template argument {}", other.describe()))),
            }
        }
        let arg_names: Vec<&str> = args.iter().map(|a| a.name()).collect();
        let result_name = format!("{}{{{}}}", name, arg_names.join(","));
        if self.created.insert(result_name.clone()) {
            let def = rule_defs
                .iter()
                .find(|r| r.name == name)
                .cloned()
                .ok_or_else(|| Error::grammar(format!("Template '{}' used but not defined", name)))?;
            if def.params.len() != args.len() {
                return Err(Error::grammar(format!("Wrong number of template arguments used for {}", name)));
            }
            let names: HashMap<String, Symbol> = def.params.iter().cloned().zip(args.iter().cloned()).collect();
            let tree = def
                .tree
                .clone()
                .ok_or_else(|| Error::grammar(format!("Template '{}' is abstract", name)))?;
            let result_tree = in_place_tree(tree, &mut |sub| Ok(replace_symbols(sub, &names)))?;
            rule_defs.push(RuleDef {
                name: result_name.clone(),
                params: Vec::new(),
                tree: Some(result_tree),
                options: def.options.clone(),
            });
        }
        Ok(GNode::Sym(Symbol::rule(result_name)))
    }
}

fn replace_symbols(t: GTree, names: &HashMap<String, Symbol>) -> GNode {
    match t.data.as_str() {
        "value" => {
            if t.children.len() == 1 {
                if let GNode::Sym(s) = &t.children[0] {
                    if let Some(r) = names.get(s.name()) {
                        return GNode::Sym(r.clone());
                    }
                }
            }
            GNode::Tree(t)
        }
        "template_usage" => {
            let replacement = match t.children.first() {
                Some(GNode::Sym(s)) => names.get(s.name()).cloned(),
                _ => None,
            };
            match replacement {
                Some(r) => {
                    let mut children = t.children;
                    children[0] = GNode::Sym(r);
                    GNode::Tree(GTree { data: t.data, children })
                }
                None => GNode::Tree(t),
            }
        }
        _ => GNode::Tree(t),
    }
}

#[derive(Clone, PartialEq, Eq, Hash)]
enum CacheKey {
    Recurse(GNode),
    Repeat(usize, usize, GNode, GNode),
    RepeatOpt(usize, usize, GNode, GNode),
}

/// Lark's `EBNF_to_BNF`: replaces the operators `? * + ~ []` with new rules.
#[derive(Default)]
struct EbnfToBnf {
    new_rules: Vec<(String, GTree, RuleOptions)>,
    rules_cache: HashMap<CacheKey, Symbol>,
    prefix: String,
    i: usize,
    rule_options: Option<RuleOptions>,
}

// Value 5 keeps the number of states in the lalr parser somewhat minimal. It isn't optimal, but close to it.
const SMALL_FACTOR_THRESHOLD: usize = 5;
// The threshold whether repeats via ~ are split up into different rules.
const REPEAT_BREAK_THRESHOLD: i64 = 50;

impl EbnfToBnf {
    fn name_rule(&mut self, inner: &str) -> String {
        let name = format!("__{}_{}_{}", self.prefix, inner, self.i);
        self.i += 1;
        name
    }

    fn add_rule(&mut self, key: CacheKey, name: String, expansions: GNode) -> Symbol {
        let t = Symbol::rule(name.clone());
        let tree = match expansions {
            GNode::Tree(t) => t,
            _ => unreachable!(),
        };
        self.new_rules.push((name, tree, self.rule_options.clone().unwrap_or_default()));
        self.rules_cache.insert(key, t.clone());
        t
    }

    fn add_recurse_rule(&mut self, kind: &str, expr: GNode) -> Symbol {
        let key = CacheKey::Recurse(expr.clone());
        if let Some(s) = self.rules_cache.get(&key) {
            return s.clone();
        }
        let name = self.name_rule(kind);
        let t = Symbol::rule(name.clone());
        let tree = st(
            "expansions",
            vec![st("expansion", vec![expr.clone()]), st("expansion", vec![GNode::Sym(t), expr])],
        );
        self.add_rule(key, name, tree)
    }

    fn add_repeat_rule(&mut self, a: usize, b: usize, target: GNode, atom: GNode) -> Symbol {
        let key = CacheKey::Repeat(a, b, target.clone(), atom.clone());
        if let Some(s) = self.rules_cache.get(&key) {
            return s.clone();
        }
        let name = self.name_rule(&format!("repeat_a{}_b{}", a, b));
        let mut children = vec![target; a];
        children.extend(std::iter::repeat_n(atom, b));
        let tree = st("expansions", vec![st("expansion", children)]);
        self.add_rule(key, name, tree)
    }

    fn add_repeat_opt_rule(&mut self, a: usize, b: usize, target: GNode, target_opt: GNode, atom: GNode) -> Symbol {
        let key = CacheKey::RepeatOpt(a, b, target.clone(), atom.clone());
        if let Some(s) = self.rules_cache.get(&key) {
            return s.clone();
        }
        let name = self.name_rule(&format!("repeat_a{}_b{}_opt", a, b));
        let mut exps = Vec::new();
        for i in 0..a {
            let mut children = vec![target.clone(); i];
            children.push(target_opt.clone());
            exps.push(st("expansion", children));
        }
        for i in 0..b {
            let mut children = vec![target.clone(); a];
            children.extend(std::iter::repeat_n(atom.clone(), i));
            exps.push(st("expansion", children));
        }
        self.add_rule(key, name, st("expansions", exps))
    }

    /// A rule tree that repeats `rule` between `mn` and `mx` times.
    fn generate_repeats(&mut self, rule: GNode, mn: usize, mx: usize) -> GNode {
        // For a small number of repeats, we can take the naive approach
        if (mx as i64) < REPEAT_BREAK_THRESHOLD {
            return st("expansions", (mn..=mx).map(|n| st("expansion", vec![rule.clone(); n])).collect());
        }

        // For large repeat values, we break the repetition into sub-rules.
        let mut mn_target = rule.clone();
        for (a, b) in small_factors(mn, SMALL_FACTOR_THRESHOLD) {
            mn_target = GNode::Sym(self.add_repeat_rule(a, b, mn_target, rule.clone()));
        }
        if mx == mn {
            return mn_target;
        }

        let diff = mx - mn + 1; // We add one because add_repeat_opt_rule generates rules that match one less
        let diff_factors = small_factors(diff, SMALL_FACTOR_THRESHOLD);
        let mut diff_target = rule.clone(); // Match rule 1 times
        let mut diff_opt_target = st("expansion", Vec::new()); // match rule 0 times (e.g. up to 1 -1 times)
        for &(a, b) in &diff_factors[..diff_factors.len() - 1] {
            diff_opt_target = GNode::Sym(self.add_repeat_opt_rule(a, b, diff_target.clone(), diff_opt_target, rule.clone()));
            diff_target = GNode::Sym(self.add_repeat_rule(a, b, diff_target, rule.clone()));
        }
        let (a, b) = diff_factors[diff_factors.len() - 1];
        diff_opt_target = GNode::Sym(self.add_repeat_opt_rule(a, b, diff_target, diff_opt_target, rule));
        st("expansions", vec![st("expansion", vec![mn_target, diff_opt_target])])
    }

    fn expr(&mut self, children: Vec<GNode>) -> Result<GNode> {
        let mut it = children.into_iter();
        let rule = it.next().unwrap_or(GNode::Empty);
        let op = it.next().and_then(|o| o.as_token().map(|t| t.value.clone())).unwrap_or_default();
        let args: Vec<GNode> = it.collect();
        match op.as_str() {
            "?" => Ok(st("expansions", vec![rule, st("expansion", Vec::new())])),
            // a : b c+ d  -->  a : b _c d ; _c : _c c | c;
            "+" => Ok(GNode::Sym(self.add_recurse_rule("plus", rule))),
            // a : b c* d  -->  a : b _c? d ; _c : _c c | c;
            "*" => {
                let new_name = self.add_recurse_rule("star", rule);
                Ok(st("expansions", vec![GNode::Sym(new_name), st("expansion", Vec::new())]))
            }
            "~" => {
                let (mn, mx) = if args.len() == 1 {
                    let n = parse_int(&args[0])?;
                    (n, n)
                } else {
                    let mn = parse_int(&args[0])?;
                    let mx = parse_int(&args[1])?;
                    if mx < mn || mn < 0 {
                        return Err(Error::grammar(format!(
                            "Bad Range for {} ({}..{} isn't allowed)",
                            rule.describe(),
                            mn,
                            mx
                        )));
                    }
                    (mn, mx)
                };
                if mn < 0 {
                    return Err(Error::grammar(format!(
                        "Bad Range for {} ({}..{} isn't allowed)",
                        rule.describe(),
                        mn,
                        mx
                    )));
                }
                Ok(self.generate_repeats(rule, mn as usize, mx as usize))
            }
            _ => Err(Error::grammar(format!("Unknown operator {}", op))),
        }
    }

    fn maybe(&mut self, rule: GNode) -> Result<GNode> {
        let keep_all_tokens = self.rule_options.as_ref().is_some_and(|o| o.keep_all_tokens);
        let size = find_rule_size(&rule, keep_all_tokens)?;
        Ok(st("expansions", vec![rule, st("expansion", vec![GNode::Empty; size])]))
    }

    fn transform(&mut self, tree: GTree) -> Result<GTree> {
        in_place_tree(tree, &mut |t| match t.data.as_str() {
            "expr" => self.expr(t.children),
            "maybe" => self.maybe(t.children.into_iter().next().unwrap_or(GNode::Empty)),
            _ => Ok(GNode::Tree(t)),
        })
    }
}

/// Lark's `FindRuleSize`: how many children an expansion adds to the tree.
fn find_rule_size(node: &GNode, keep_all_tokens: bool) -> Result<usize> {
    match node {
        GNode::Tree(t) if t.data == "expansion" => {
            let mut sum = 0;
            for c in &t.children {
                sum += find_rule_size(c, keep_all_tokens)?;
            }
            Ok(sum)
        }
        GNode::Tree(t) if t.data == "expansions" => {
            let mut max = 0;
            for c in &t.children {
                max = max.max(find_rule_size(c, keep_all_tokens)?);
            }
            Ok(max)
        }
        GNode::Empty => Ok(0),
        GNode::Sym(s) if !s.is_term => Ok(if s.name.starts_with('_') { 0 } else { 1 }),
        GNode::Sym(s) => Ok(if keep_all_tokens || !s.filter_out { 1 } else { 0 }),
        other => Err(Error::grammar(format!("Unexpected node in rule: {}", other.describe()))),
    }
}

/// Lark's `SimplifyRule_Visitor`: flattens the rule into a list of alternatives.
fn simplify_rule(tree: &mut GTree) {
    for path in tree.subtree_paths() {
        simplify_call(tree.at_mut(&path));
    }
}

fn simplify_call(tree: &mut GTree) {
    match tree.data.as_str() {
        "expansion" => {
            // rules_list unpacking
            // a : b (c|d) e
            //  -->
            // a : b c e | b d e
            tree.flatten();
            let found = tree
                .children
                .iter()
                .position(|c| matches!(c, GNode::Tree(t) if t.data == "expansions"));
            if let Some(i) = found {
                let options = match &tree.children[i] {
                    GNode::Tree(t) => dedup(t.children.clone()),
                    _ => unreachable!(),
                };
                let original = std::mem::take(&mut tree.children);
                tree.data = "expansions".to_string();
                tree.children = options
                    .into_iter()
                    .map(|option| {
                        let children = original
                            .iter()
                            .enumerate()
                            .map(|(j, other)| if i == j { option.clone() } else { other.clone() })
                            .collect();
                        let mut e = GTree {
                            data: "expansion".to_string(),
                            children,
                        };
                        simplify_rule(&mut e);
                        GNode::Tree(e)
                    })
                    .collect();
                tree.flatten();
            }
        }
        "alias" => {
            if tree.children.len() == 2 {
                if let GNode::Tree(rule) = &tree.children[0] {
                    if rule.data == "expansions" {
                        let alias_name = tree.children[1].clone();
                        let aliases = rule
                            .children
                            .iter()
                            .map(|child| st("alias", vec![child.clone(), alias_name.clone()]))
                            .collect();
                        tree.data = "expansions".to_string();
                        tree.children = aliases;
                    }
                }
            }
        }
        "expansions" => {
            tree.flatten();
            // Ensure all children are unique
            let unique: HashSet<&GNode> = tree.children.iter().collect();
            if unique.len() != tree.children.len() {
                tree.children = dedup(std::mem::take(&mut tree.children));
            }
        }
        _ => {}
    }
}

/// Lark's `RuleTreeToText`: the alternatives of a simplified rule, with their alias.
fn rule_tree_to_text(tree: &GTree) -> Result<Vec<(Vec<GNode>, Option<String>)>> {
    fn expansion(node: &GNode) -> Result<(Vec<GNode>, Option<String>)> {
        match node {
            GNode::Tree(t) if t.data == "expansion" => Ok((t.children.clone(), None)),
            GNode::Tree(t) if t.data == "alias" => {
                let (exp, inner) = expansion(&t.children[0])?;
                if inner.is_some() {
                    return Err(Error::grammar("Double alias not allowed"));
                }
                match &t.children[1] {
                    GNode::Sym(s) => Ok((exp, Some(s.name.to_string()))),
                    other => Err(Error::grammar(format!("Unexpected alias {}", other.describe()))),
                }
            }
            other => Err(Error::grammar(format!("Unexpected rule structure: {}", other.describe()))),
        }
    }
    if tree.data != "expansions" {
        return Err(Error::grammar(format!("Unexpected rule structure: {}", tree.data)));
    }
    tree.children.iter().map(expansion).collect()
}

// ---------------------------------------------------------------------------------------------------------------
//  Grammar
// ---------------------------------------------------------------------------------------------------------------

/// A loaded grammar, with its imports resolved, that isn't compiled into BNF yet (Lark's `Grammar`).
#[derive(Clone, Debug)]
pub struct Grammar {
    pub(crate) rule_defs: Vec<RuleDef>,
    pub(crate) term_defs: Vec<TermDef>,
    /// The names of the ignored terminals.
    pub ignore: Vec<String>,
}

/// The result of compiling a grammar: its terminals, BNF rules and ignored terminals.
pub(crate) struct CompiledGrammar {
    pub terminals: Vec<TerminalDef>,
    pub rules: Vec<Rule>,
    pub ignore: Vec<String>,
}

impl Grammar {
    /// The names of the rules.
    pub fn rule_names(&self) -> impl Iterator<Item = &str> {
        self.rule_defs.iter().map(|r| r.name.as_str())
    }

    /// The names of the terminals.
    pub fn terminal_names(&self) -> impl Iterator<Item = &str> {
        self.term_defs.iter().map(|t| t.name.as_str())
    }

    /// Compiles the EBNF grammar into BNF rules and terminals. `terminals_to_keep` are kept even when unused;
    /// `None` keeps all terminals.
    pub(crate) fn compile(&self, start: &[String], terminals_to_keep: Option<&HashSet<String>>) -> Result<CompiledGrammar> {
        let term_defs = self.term_defs.clone();
        let mut rule_defs = self.rule_defs.clone();

        // ===================
        //  Compile Terminals
        // ===================
        for td in &term_defs {
            let tree = match &td.tree {
                Some(t) => t,
                None => continue, // Terminal added through %declare
            };
            let expansions = tree.find_data("expansion");
            if expansions.len() == 1 && tree.at(&expansions[0]).children.is_empty() {
                return Err(Error::grammar(format!("Terminals cannot be empty ({})", td.name)));
            }
        }

        let mut terminals = Vec::new();
        for td in term_defs {
            if let Some(tree) = td.tree {
                let pattern = terminal_tree_to_pattern(GNode::Tree(prepare_literals(tree)?))?;
                terminals.push(TerminalDef {
                    name: td.name,
                    pattern,
                    priority: td.priority,
                });
            }
        }

        // =================
        //  Compile Rules
        // =================

        // 1. Pre-process terminals
        let mut anon_tokens = PrepareAnonTerminals::new(terminals);
        // 2. Inline Templates
        let mut apply_templates = ApplyTemplates::default();

        // 3. Convert EBNF to BNF (and apply step 1 & 2)
        let mut ebnf_to_bnf = EbnfToBnf::default();
        let mut rules: Vec<(String, GTree, RuleOptions)> = Vec::new();
        let mut i = 0;
        while i < rule_defs.len() {
            // We have to do it like this because rule_defs might grow due to templates
            let def = rule_defs[i].clone();
            i += 1;
            if !def.params.is_empty() {
                continue; // Dont transform templates
            }
            let rule_options = if def.options.keep_all_tokens {
                Some(RuleOptions {
                    keep_all_tokens: true,
                    ..Default::default()
                })
            } else {
                None
            };
            ebnf_to_bnf.rule_options = rule_options.clone();
            ebnf_to_bnf.prefix = def.name.clone();
            anon_tokens.keep_all_tokens = rule_options.is_some();
            let tree = def
                .tree
                .clone()
                .ok_or_else(|| Error::grammar(format!("Rule '{}' is abstract", def.name)))?;
            let tree = prepare_literals(tree)?;
            let tree = validate_symbols(tree)?;
            let tree = in_place_tree(tree, &mut |t| anon_tokens.call(t))?;
            let tree = in_place_tree(tree, &mut |t| apply_templates.call(t, &mut rule_defs))?;
            let res = ebnf_to_bnf.transform(tree)?;
            rules.push((def.name.clone(), res, def.options.clone()));
        }
        rules.append(&mut ebnf_to_bnf.new_rules);

        let names: HashSet<&str> = rules.iter().map(|r| r.0.as_str()).collect();
        if names.len() != rules.len() {
            return Err(Error::grammar("Whoops, name collision"));
        }

        // 4. Compile tree to Rule objects
        let mut compiled_rules: Vec<Rule> = Vec::new();
        for (name, mut tree, options) in rules {
            simplify_rule(&mut tree);
            let expansions = rule_tree_to_text(&tree)?;
            for (i, (expansion, alias)) in expansions.into_iter().enumerate() {
                if let Some(alias) = alias.as_ref().filter(|_| name.starts_with('_')) {
                    return Err(Error::grammar(format!(
                        "Rule {} is marked for expansion (it starts with an underscore) and isn't allowed to have aliases (alias={})",
                        name, alias
                    )));
                }
                let empty_indices: Vec<bool> = expansion.iter().map(|x| matches!(x, GNode::Empty)).collect();
                let mut exp_options = options.clone();
                if empty_indices.iter().any(|&b| b) {
                    exp_options.empty_indices = empty_indices;
                }
                let mut symbols = Vec::with_capacity(expansion.len());
                for x in expansion {
                    match x {
                        GNode::Empty => {}
                        GNode::Sym(mut s) => {
                            if s.is_term && exp_options.keep_all_tokens {
                                s.filter_out = false;
                            }
                            symbols.push(s);
                        }
                        other => return Err(Error::grammar(format!("Unexpected rule structure: {}", other.describe()))),
                    }
                }
                compiled_rules.push(Rule::new(Symbol::rule(name.as_str()), symbols, i, alias, exp_options));
            }
        }

        // Remove duplicates of empty rules, throw error for non-empty duplicates
        let unique: HashSet<&Rule> = compiled_rules.iter().collect();
        if unique.len() != compiled_rules.len() {
            let duplicates = classify(compiled_rules.iter(), |r| (*r).clone(), |r| r);
            for dups in duplicates.values() {
                if dups.len() > 1 && !dups[0].expansion.is_empty() {
                    let list: String = dups.iter().map(|d| format!("\n  * {}", d)).collect();
                    return Err(Error::grammar(format!(
                        "Rules defined twice: {}\n\n(Might happen due to colliding expansion of optionals: [] or ?)",
                        list
                    )));
                }
            }
            let set: IndexSet<Rule> = compiled_rules.into_iter().collect();
            compiled_rules = set.into_iter().collect();
        }

        // Filter out unused rules
        loop {
            let c = compiled_rules.len();
            let mut used_rules: HashSet<Symbol> = compiled_rules
                .iter()
                .flat_map(|r| r.expansion.iter().filter(move |s| !s.is_term && **s != r.origin).cloned())
                .collect();
            used_rules.extend(start.iter().map(|s| Symbol::rule(s.as_str())));
            compiled_rules.retain(|r| used_rules.contains(&r.origin));
            if compiled_rules.len() == c {
                break;
            }
        }

        // Filter out unused terminals
        let mut terminals = anon_tokens.terminals;
        if let Some(keep) = terminals_to_keep {
            let used_terms: HashSet<&str> = compiled_rules
                .iter()
                .flat_map(|r| r.expansion.iter().filter(|s| s.is_term).map(|s| s.name()))
                .collect();
            terminals.retain(|t| used_terms.contains(t.name.as_str()) || self.ignore.contains(&t.name) || keep.contains(&t.name));
        }

        Ok(CompiledGrammar {
            terminals,
            rules: compiled_rules,
            ignore: self.ignore.clone(),
        })
    }
}

// ---------------------------------------------------------------------------------------------------------------
//  Grammar loaders
// ---------------------------------------------------------------------------------------------------------------

/// Loads the grammars of `%import` statements (Lark's import paths and loaders such as `FromPackageLoader`).
pub trait GrammarLoader: Send + Sync {
    /// Loads `grammar_path` (such as `common.lark` or `sub/grammar.lark`), and returns its full path and text.
    /// `base_path` is `None` for library imports (`%import common.X`), and the directory of the importing grammar
    /// for relative imports (`%import .x.Y`).
    fn load(&self, base_path: Option<&str>, grammar_path: &str) -> Option<(String, String)>;
}

/// Loads grammars from a directory.
#[derive(Clone, Debug)]
pub struct FileSystemLoader {
    pub directory: PathBuf,
}

impl FileSystemLoader {
    pub fn new(directory: impl Into<PathBuf>) -> FileSystemLoader {
        FileSystemLoader {
            directory: directory.into(),
        }
    }
}

impl GrammarLoader for FileSystemLoader {
    fn load(&self, _base_path: Option<&str>, grammar_path: &str) -> Option<(String, String)> {
        let path = self.directory.join(grammar_path);
        let text = std::fs::read_to_string(&path).ok()?;
        Some((path.to_string_lossy().into_owned(), text))
    }
}

/// The prefix of the paths of the grammars that come with the crate.
const STDLIB_PREFIX: &str = "lark://";

/// Loads the grammars that come with Lark: `common`, `unicode`, `python` and `lark`.
#[derive(Clone, Copy, Debug, Default)]
pub struct StandardLibraryLoader;

fn stdlib_grammar(path: &str) -> Option<&'static str> {
    Some(match path {
        "grammars/common.lark" => include_str!("../grammars/common.lark"),
        "grammars/unicode.lark" => include_str!("../grammars/unicode.lark"),
        "grammars/python.lark" => include_str!("../grammars/python.lark"),
        "grammars/lark.lark" => include_str!("../grammars/lark.lark"),
        _ => return None,
    })
}

fn normalize_path(path: &str) -> String {
    let path = path.replace('\\', "/");
    let mut parts: Vec<&str> = Vec::new();
    for p in path.split('/') {
        match p {
            "" | "." => {}
            ".." if !parts.is_empty() => {
                parts.pop();
            }
            p => parts.push(p),
        }
    }
    parts.join("/")
}

impl GrammarLoader for StandardLibraryLoader {
    fn load(&self, base_path: Option<&str>, grammar_path: &str) -> Option<(String, String)> {
        let dir = match base_path {
            None => "grammars".to_string(),
            Some(b) => b.strip_prefix(STDLIB_PREFIX)?.to_string(),
        };
        let path = normalize_path(&format!("{}/{}", dir, grammar_path));
        let text = stdlib_grammar(&path)?;
        Some((format!("{}{}", STDLIB_PREFIX, path), text.to_string()))
    }
}

impl StandardLibraryLoader {
    /// The text of a grammar that comes with Lark, such as `python` or `lark`.
    pub fn grammar(name: &str) -> Option<&'static str> {
        stdlib_grammar(&format!("grammars/{}.lark", name))
    }
}

fn directory_of(path: &str) -> String {
    if let Some(rest) = path.strip_prefix(STDLIB_PREFIX) {
        return match rest.rfind('/') {
            Some(i) => format!("{}{}", STDLIB_PREFIX, &rest[..i]),
            None => STDLIB_PREFIX.to_string(),
        };
    }
    Path::new(path)
        .parent()
        .map(|p| p.to_string_lossy().into_owned())
        .unwrap_or_default()
}

// ---------------------------------------------------------------------------------------------------------------
//  GrammarBuilder
// ---------------------------------------------------------------------------------------------------------------

#[derive(Clone, Debug)]
struct Definition {
    is_term: bool,
    tree: Option<GTree>,
    params: Vec<String>,
    /// The priority of terminals.
    priority: i64,
    /// The options of rules.
    options: RuleOptions,
}

type Mangle = Rc<dyn Fn(&str) -> String>;

/// Python has arbitrary precision integers; huge priorities are clamped, which leaves room to add them up.
const MAX_PRIORITY: i64 = 1_000_000_000;

fn parse_priority(node: &GNode) -> Result<i64> {
    let text = match node {
        GNode::Token(t) if t.kind == "NUMBER" => t.value.as_str(),
        other => return Err(Error::grammar(format!("Priority must be a number, got {}", other.describe()))),
    };
    let negative = text.starts_with('-');
    let digits = text.trim_start_matches(['+', '-']);
    let value = match digits.parse::<i64>() {
        Ok(v) => v.min(MAX_PRIORITY),
        Err(_) => MAX_PRIORITY,
    };
    Ok(if negative { -value } else { value })
}

fn format_grammar_error(is_term: bool, msg: &str, names: &[&str]) -> String {
    let (lower, title) = if is_term { ("terminal", "Terminal") } else { ("rule", "Rule") };
    let mut msg = msg.to_string();
    for (i, name) in names.iter().enumerate() {
        let postfix = if i == 0 { String::new() } else { (i + 1).to_string() };
        msg = msg
            .replace(&format!("{{name{}}}", postfix), name)
            .replace(&format!("{{type{}}}", postfix), lower)
            .replace(&format!("{{Type{}}}", postfix), title);
    }
    msg
}

fn grammar_error(is_term: bool, msg: &str, names: &[&str]) -> Error {
    Error::grammar(format_grammar_error(is_term, msg, names))
}

struct Import {
    dotted_path: Vec<String>,
    base_path: Option<String>,
    aliases: IndexMap<String, String>,
}

struct Unpacked {
    name: String,
    is_term: bool,
    tree: GTree,
    params: Vec<String>,
    priority: i64,
    options: RuleOptions,
}

/// Builds a [`Grammar`] from the text of grammars, resolving their imports (Lark's `GrammarBuilder`).
pub(crate) struct GrammarBuilder {
    global_keep_all_tokens: bool,
    import_paths: Vec<Arc<dyn GrammarLoader>>,
    definitions: IndexMap<String, Definition>,
    ignore_names: Vec<String>,
}

impl GrammarBuilder {
    pub fn new(global_keep_all_tokens: bool, import_paths: Vec<Arc<dyn GrammarLoader>>) -> GrammarBuilder {
        GrammarBuilder {
            global_keep_all_tokens,
            import_paths,
            definitions: IndexMap::new(),
            ignore_names: Vec::new(),
        }
    }

    /// Loads and builds a grammar.
    pub fn load(text: &str, source: &str, import_paths: Vec<Arc<dyn GrammarLoader>>, keep_all_tokens: bool) -> Result<Grammar> {
        let mut builder = GrammarBuilder::new(keep_all_tokens, import_paths);
        builder.load_grammar(text, source, None)?;
        builder.build()
    }

    fn define(
        &mut self,
        name: String,
        is_term: bool,
        tree: Option<GTree>,
        params: Vec<String>,
        priority: i64,
        mut options: RuleOptions,
        override_: bool,
    ) -> Result<()> {
        if self.definitions.contains_key(&name) {
            if !override_ {
                return Err(grammar_error(is_term, "{Type} '{name}' defined more than once", &[&name]));
            }
        } else if override_ {
            return Err(grammar_error(is_term, "Cannot override a nonexisting {type} {name}", &[&name]));
        }
        if name.starts_with("__") {
            return Err(grammar_error(
                is_term,
                "Names starting with double-underscore are reserved (Error at {name})",
                &[&name],
            ));
        }
        if !is_term && self.global_keep_all_tokens {
            options.keep_all_tokens = true;
        }
        self.definitions.insert(
            name,
            Definition {
                is_term,
                tree,
                params,
                priority,
                options,
            },
        );
        Ok(())
    }

    fn extend(&mut self, name: &str, is_term: bool, exp: GTree, params: &[String]) -> Result<()> {
        let d = self
            .definitions
            .get_mut(name)
            .ok_or_else(|| grammar_error(is_term, "Can't extend {type} {name} as it wasn't defined before", &[name]))?;
        if is_term != d.is_term {
            return Err(grammar_error(
                is_term,
                "Cannot extend {type} {name} - one is a terminal, while the other is not.",
                &[name],
            ));
        }
        if params != d.params.as_slice() {
            return Err(grammar_error(
                is_term,
                "Cannot extend {type} with different parameters: {name}",
                &[name],
            ));
        }
        let base = d
            .tree
            .as_mut()
            .ok_or_else(|| grammar_error(is_term, "Can't extend {type} {name} - it is abstract.", &[name]))?;
        if base.data != "expansions" {
            return Err(Error::grammar("Expected an expansions tree"));
        }
        base.children.insert(0, GNode::Tree(exp));
        Ok(())
    }

    fn ignore(&mut self, t: GTree) {
        if t.data == "expansions" && t.children.len() == 1 {
            if let GNode::Tree(t2) = &t.children[0] {
                if t2.data == "expansion" && t2.children.len() == 1 {
                    if let GNode::Tree(item) = &t2.children[0] {
                        if item.data == "value" && item.children.len() == 1 {
                            if let GNode::Sym(s) = &item.children[0] {
                                if s.is_term {
                                    // Keep terminal name, no need to create a new definition
                                    self.ignore_names.push(s.name.to_string());
                                    return;
                                }
                            }
                        }
                    }
                }
            }
        }
        let name = format!("__IGNORE_{}", self.ignore_names.len());
        self.ignore_names.push(name.clone());
        self.definitions.insert(
            name,
            Definition {
                is_term: true,
                tree: Some(t),
                params: Vec::new(),
                priority: TOKEN_DEFAULT_PRIORITY,
                options: RuleOptions::default(),
            },
        );
    }

    fn unpack_import(stmt: &GTree, grammar_name: &str) -> Result<Import> {
        let path_node = stmt.children[0].as_tree().ok_or_else(|| Error::grammar("Bad import"))?;
        let arg1 = stmt.children.get(1);
        let names = |nodes: &[GNode]| -> Vec<String> { nodes.iter().filter_map(|n| n.as_token().map(|t| t.value.clone())).collect() };
        let (dotted_path, aliases) = match arg1 {
            Some(GNode::Tree(name_list)) => {
                // Multi import. Can't have aliased multi import, so all aliases will be the same as names
                let aliases = names(&name_list.children).into_iter().map(|n| (n.clone(), n)).collect();
                (names(&path_node.children), aliases)
            }
            _ => {
                // Single import
                let all = names(&path_node.children);
                let dotted_path = all[..all.len().saturating_sub(1)].to_vec();
                if dotted_path.is_empty() {
                    return Err(Error::grammar(format!(
                        "Nothing was imported from grammar `{}`",
                        all.first().cloned().unwrap_or_default()
                    )));
                }
                let name = all.last().unwrap().clone();
                let alias = arg1
                    .and_then(GNode::as_token)
                    .map(|t| t.value.clone())
                    .unwrap_or_else(|| name.clone());
                let mut aliases = IndexMap::new();
                aliases.insert(name, alias);
                (dotted_path, aliases)
            }
        };
        let base_path = if path_node.data == "import_lib" {
            None
        } else if grammar_name == "<string>" {
            // Import relative to the current directory if the grammar is given as a string
            Some(
                std::env::current_dir()
                    .map(|d| d.to_string_lossy().into_owned())
                    .unwrap_or_else(|_| ".".to_string()),
            )
        } else {
            // Import relative to the grammar file path if external grammar file
            Some(directory_of(grammar_name))
        };
        Ok(Import {
            dotted_path,
            base_path,
            aliases,
        })
    }

    fn unpack_definition(tree: &GTree, mangle: Option<&Mangle>) -> Result<Unpacked> {
        let token = |n: &GNode| {
            n.as_token()
                .map(|t| t.value.clone())
                .ok_or_else(|| Error::grammar("Bad definition"))
        };
        let subtree = |n: &GNode| n.as_tree().cloned().ok_or_else(|| Error::grammar("Bad definition"));
        let (mut name, mut params, exp, is_term, priority, options);
        if tree.data == "rule" {
            let modifiers = subtree(&tree.children[0])?;
            name = token(&tree.children[1])?;
            let params_tree = subtree(&tree.children[2])?;
            let priority_tree = subtree(&tree.children[3])?;
            exp = subtree(&tree.children[4])?;
            let (mut keep_all_tokens, mut expand1) = (false, false);
            if let Some(m) = modifiers.children.first() {
                let m = token(m)?;
                expand1 = m.contains('?');
                if expand1 && name.starts_with('_') {
                    return Err(Error::grammar("Inlined rules (_rule) cannot use the ?rule modifier."));
                }
                keep_all_tokens = m.contains('!');
            }
            let rule_priority = match priority_tree.children.first() {
                Some(p) => Some(parse_priority(p)?),
                None => None,
            };
            params = params_tree.children.iter().map(token).collect::<Result<Vec<_>>>()?;
            options = RuleOptions {
                keep_all_tokens,
                expand1,
                priority: rule_priority,
                template_source: if params.is_empty() { None } else { Some(name.clone()) },
                empty_indices: Vec::new(),
            };
            priority = TOKEN_DEFAULT_PRIORITY;
            is_term = false;
        } else {
            name = token(&tree.children[0])?;
            params = Vec::new();
            priority = if tree.children.len() == 3 {
                parse_priority(&tree.children[1])?
            } else {
                TOKEN_DEFAULT_PRIORITY
            };
            exp = subtree(tree.children.last().unwrap())?;
            options = RuleOptions::default();
            is_term = true;
        }
        let mut exp = exp;
        if let Some(m) = mangle {
            params = params.iter().map(|p| m(p)).collect();
            name = m(&name);
            exp.rename_symbols(&|s| m(s));
        }
        Ok(Unpacked {
            name,
            is_term,
            tree: exp,
            params,
            priority,
            options,
        })
    }

    fn define_unpacked(&mut self, d: Unpacked, override_: bool) -> Result<()> {
        self.define(d.name, d.is_term, Some(d.tree), d.params, d.priority, d.options, override_)
    }

    /// Parses the grammar text and adds its definitions, and the definitions it imports.
    pub fn load_grammar(&mut self, text: &str, grammar_name: &str, mangle: Option<Mangle>) -> Result<()> {
        let tree = parse_grammar(text, grammar_name)?;

        let mut imports: IndexMap<Vec<String>, Import> = IndexMap::new();
        for stmt in &tree.children {
            let stmt = match stmt.as_tree() {
                Some(s) if s.data == "import" => s,
                _ => continue,
            };
            let import = Self::unpack_import(stmt, grammar_name)?;
            match imports.get_mut(&import.dotted_path) {
                Some(existing) => {
                    if existing.base_path != import.base_path {
                        return Err(Error::grammar(format!(
                            "Inconsistent base_path for {}.",
                            import.dotted_path.join(".")
                        )));
                    }
                    existing.aliases.extend(import.aliases);
                }
                None => {
                    imports.insert(import.dotted_path.clone(), import);
                }
            }
        }

        for import in imports.into_values() {
            self.do_import(&import.dotted_path, import.base_path.as_deref(), &import.aliases, mangle.clone())?;
        }

        for stmt in tree.children {
            let stmt = match stmt {
                GNode::Tree(t) => t,
                other => return Err(Error::grammar(format!("Unexpected statement {}", other.describe()))),
            };
            match stmt.data.as_str() {
                "term" | "rule" => {
                    let d = Self::unpack_definition(&stmt, mangle.as_ref())?;
                    self.define_unpacked(d, false)?;
                }
                "override" => {
                    let r = stmt.children[0].as_tree().ok_or_else(|| Error::grammar("Bad override"))?;
                    let d = Self::unpack_definition(r, mangle.as_ref())?;
                    self.define_unpacked(d, true)?;
                }
                "extend" => {
                    let r = stmt.children[0].as_tree().ok_or_else(|| Error::grammar("Bad extend"))?;
                    let d = Self::unpack_definition(r, mangle.as_ref())?;
                    self.extend(&d.name, d.is_term, d.tree, &d.params)?;
                }
                "ignore" => {
                    // if mangle is not None, we shouldn't apply ignore, since we aren't in a toplevel grammar
                    if mangle.is_none() {
                        if let Some(GNode::Tree(t)) = stmt.children.into_iter().next() {
                            self.ignore(t);
                        }
                    }
                }
                "declare" => {
                    for child in stmt.children {
                        let symbol = match child {
                            GNode::Sym(s) => s,
                            other => return Err(Error::grammar(format!("Unexpected {}", other.describe()))),
                        };
                        let name = match &mangle {
                            None => symbol.name.to_string(),
                            Some(m) => m(&symbol.name),
                        };
                        self.define(
                            name,
                            symbol.is_term,
                            None,
                            Vec::new(),
                            TOKEN_DEFAULT_PRIORITY,
                            RuleOptions::default(),
                            false,
                        )?;
                    }
                }
                "import" => {}
                other => return Err(Error::grammar(format!("Unexpected statement {}", other))),
            }
        }

        self.resolve_term_references()
    }

    fn resolve_term_references(&mut self) -> Result<()> {
        // The checks, in the order Lark makes them while resolving
        let mut refs: IndexMap<String, Vec<String>> = IndexMap::new();
        for (name, d) in &self.definitions {
            if !d.is_term {
                continue;
            }
            let tree = match &d.tree {
                Some(t) => t,
                None => continue, // Terminal added through %declare
            };
            let mut names = Vec::new();
            for path in tree.find_data("value") {
                let value = tree.at(&path);
                if let Some(GNode::Sym(item)) = value.children.first() {
                    if !item.is_term {
                        return Err(Error::grammar(format!(
                            "Rules aren't allowed inside terminals ({:?} in {})",
                            item, name
                        )));
                    }
                    match self.definitions.get(item.name()) {
                        Some(t) if t.is_term && t.tree.is_some() => names.push(item.name.to_string()),
                        _ => return Err(Error::grammar(format!("Terminal used but not defined: {}", item.name))),
                    }
                }
            }
            refs.insert(name.clone(), names);
        }

        for name in refs.keys() {
            // Is the terminal part of a cycle of references?
            let reaches_itself = bfs(refs[name].iter().cloned(), |n| refs.get(n).cloned().unwrap_or_default())
                .iter()
                .any(|n| n == name);
            if reaches_itself {
                return Err(Error::grammar(format!(
                    "Recursion in terminal '{}' (recursion is only allowed in rules, not terminals)",
                    name
                )));
            }
        }

        let mut resolved: HashMap<String, GTree> = HashMap::new();
        fn resolve(name: &str, definitions: &IndexMap<String, Definition>, resolved: &mut HashMap<String, GTree>) -> GTree {
            if let Some(t) = resolved.get(name) {
                return t.clone();
            }
            let mut tree = definitions[name].tree.clone().expect("defined terminal");
            for path in tree.find_data("value") {
                let referenced = match tree.at(&path).children.first() {
                    Some(GNode::Sym(item)) if item.is_term => item.name.to_string(),
                    _ => continue,
                };
                let value = resolve(&referenced, definitions, resolved);
                tree.at_mut(&path).children[0] = GNode::Tree(value);
            }
            resolved.insert(name.to_string(), tree.clone());
            tree
        }
        for name in refs.keys() {
            if !refs[name].is_empty() {
                let tree = resolve(name, &self.definitions, &mut resolved);
                self.definitions[name.as_str()].tree = Some(tree);
            }
        }
        Ok(())
    }

    fn find_used_symbols(tree: &GTree) -> HashSet<String> {
        let mut result = HashSet::new();
        for path in tree.find_data("expansion") {
            let mut symbols = Vec::new();
            tree.at(&path).scan_symbols(&mut symbols);
            result.extend(symbols.into_iter().map(|s| s.name.to_string()));
        }
        result
    }

    fn remove_unused(&mut self, used: Vec<String>) {
        let definitions = &self.definitions;
        let used: HashSet<String> = bfs(used, |symbol| match definitions.get(symbol) {
            Some(d) if !d.is_term => match &d.tree {
                Some(t) => {
                    let mut deps: Vec<String> = Self::find_used_symbols(t).into_iter().filter(|s| !d.params.contains(s)).collect();
                    deps.sort();
                    deps
                }
                None => Vec::new(),
            },
            _ => Vec::new(),
        })
        .into_iter()
        .collect();
        self.definitions.retain(|k, _| used.contains(k));
    }

    fn do_import(
        &mut self,
        dotted_path: &[String],
        base_path: Option<&str>,
        aliases: &IndexMap<String, String>,
        base_mangle: Option<Mangle>,
    ) -> Result<()> {
        let prefix = dotted_path.join("__");
        let alias_map = aliases.clone();
        let mangle: Mangle = Rc::new(move |s: &str| {
            let mut s = match alias_map.get(s) {
                Some(a) => a.clone(),
                None => match s.strip_prefix('_') {
                    Some(rest) => format!("_{}__{}", prefix, rest),
                    None => format!("{}__{}", prefix, s),
                },
            };
            if let Some(b) = &base_mangle {
                s = b(&s);
            }
            s
        });
        let grammar_path = format!("{}.lark", dotted_path.join("/"));

        let mut to_try: Vec<Arc<dyn GrammarLoader>> = self.import_paths.clone();
        if let Some(b) = base_path {
            to_try.push(Arc::new(FileSystemLoader::new(b)));
        }
        to_try.push(Arc::new(StandardLibraryLoader));

        for source in to_try {
            let (joined_path, text) = match source.load(base_path, &grammar_path) {
                Some(r) => r,
                None => continue,
            };
            let mut gb = GrammarBuilder::new(self.global_keep_all_tokens, self.import_paths.clone());
            gb.load_grammar(&text, &joined_path, Some(mangle.clone()))?;
            gb.remove_unused(aliases.keys().map(|a| mangle(a)).collect());
            for name in gb.definitions.keys() {
                if self.definitions.contains_key(name) {
                    return Err(Error::grammar(format!(
                        "Cannot import '{}' from '{}': Symbol already defined.",
                        name, grammar_path
                    )));
                }
            }
            self.definitions.extend(gb.definitions);
            return Ok(());
        }
        Err(Error::Io(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            format!("No such file or directory: '{}'", grammar_path),
        )))
    }

    fn validate(&self) -> Result<()> {
        for (name, d) in &self.definitions {
            for (i, p) in d.params.iter().enumerate() {
                if self.definitions.contains_key(p) {
                    return Err(Error::grammar(format!(
                        "Template Parameter conflicts with rule {} (in template {})",
                        p, name
                    )));
                }
                if d.params[..i].contains(p) {
                    return Err(Error::grammar(format!("Duplicate Template Parameter {} (in template {})", p, name)));
                }
            }

            let exp = match &d.tree {
                Some(t) => t,
                None => continue, // Remaining checks don't apply to abstract rules/terminals (created with %declare)
            };

            for path in exp.find_data("template_usage") {
                let temp = exp.at(&path);
                let sym = match temp.children.first() {
                    Some(GNode::Sym(s)) => s.name.to_string(),
                    _ => continue,
                };
                let args = temp.children.len() - 1;
                if !d.params.contains(&sym) {
                    let sym_def = self.definitions.get(&sym).ok_or_else(|| {
                        grammar_error(
                            d.is_term,
                            &format!("Template '{}' used but not defined (in {{type}} {{name}})", sym),
                            &[name],
                        )
                    })?;
                    if args != sym_def.params.len() {
                        return Err(grammar_error(
                            d.is_term,
                            &format!(
                                "Wrong number of template arguments used for {{name}} (expected {}, got {}) (in {{type2}} {{name2}})",
                                sym_def.params.len(),
                                args
                            ),
                            &[&sym, name],
                        ));
                    }
                }
            }

            let mut used: Vec<String> = Self::find_used_symbols(exp).into_iter().collect();
            used.sort();
            for sym in used {
                if !self.definitions.contains_key(&sym) && !d.params.contains(&sym) {
                    return Err(grammar_error(
                        d.is_term,
                        "{Type} '{name}' used but not defined (in {type2} {name2})",
                        &[&sym, name],
                    ));
                }
            }
        }

        let missing: Vec<String> = self
            .ignore_names
            .iter()
            .filter(|n| !self.definitions.contains_key(*n))
            .map(|n| repr(n))
            .collect();
        if !missing.is_empty() {
            return Err(Error::grammar(format!(
                "Terminals {{{}}} were marked to ignore but were not defined!",
                missing.join(", ")
            )));
        }
        Ok(())
    }

    pub fn build(self) -> Result<Grammar> {
        self.validate()?;
        let mut rule_defs = Vec::new();
        let mut term_defs = Vec::new();
        for (name, d) in self.definitions {
            if d.is_term {
                term_defs.push(TermDef {
                    name,
                    tree: d.tree,
                    priority: d.priority,
                });
            } else {
                rule_defs.push(RuleDef {
                    name,
                    params: d.params,
                    tree: d.tree,
                    options: d.options,
                });
            }
        }
        Ok(Grammar {
            rule_defs,
            term_defs,
            ignore: self.ignore_names,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn eval_escaping_matches_python() {
        assert_eq!(eval_escaping(r"a\nb").unwrap(), "a\nb");
        assert_eq!(eval_escaping(r"\d+").unwrap(), r"\d+");
        assert_eq!(eval_escaping(r"\\").unwrap(), r"\\");
        assert_eq!(eval_escaping(r#"\""#).unwrap(), "\"");
        assert_eq!(eval_escaping(r"\x41\u00e9").unwrap(), "Aé");
    }

    #[test]
    fn loads_and_compiles_a_grammar() {
        let grammar = GrammarBuilder::load(
            "start: WORD (\",\" WORD)* \"!\"?\n%import common.WORD\n%ignore \" \"\n",
            "<string>",
            Vec::new(),
            false,
        )
        .unwrap();
        let compiled = grammar.compile(&["start".to_string()], Some(&HashSet::new())).unwrap();
        let names: Vec<&str> = compiled.terminals.iter().map(|t| t.name.as_str()).collect();
        assert_eq!(names, vec!["WORD", "__IGNORE_0", "COMMA", "BANG"]);
        let rules: Vec<String> = compiled.rules.iter().map(|r| r.to_string()).collect();
        assert_eq!(
            rules,
            vec![
                "<start : WORD __start_star_0 BANG>",
                "<start : WORD __start_star_0>",
                "<start : WORD BANG>",
                "<start : WORD>",
                "<__start_star_0 : COMMA WORD>",
                "<__start_star_0 : __start_star_0 COMMA WORD>"
            ]
        );
    }
}
