//! Generates the typed Rust API of a grammar: a parser, name constants, node wrappers and, with result mappings, a
//! typed bottom-up visitor. The `etalii-lark-derive` crate's `#[lark_grammar]` macro is a thin wrapper around it, and
//! a build script can use it directly to write the code to a file.

use std::collections::{BTreeMap, BTreeSet};
use std::fmt;
use std::path::PathBuf;

use super::shapes::{ElementKind, Member, Shape, ShapeElement, Shapes};
use crate::lark::{Lark, LarkOptions, LexerKind, ParserKind};

/// The options of the generated parser, as far as they matter for code generation.
#[derive(Clone, Debug)]
pub struct CodegenOptions {
    pub parser: ParserKind,
    pub lexer: LexerKind,
    /// The start rules. Defaults to `start`.
    pub start: Vec<String>,
    pub maybe_placeholders: bool,
    pub keep_all_tokens: bool,
}

impl Default for CodegenOptions {
    fn default() -> Self {
        let defaults = LarkOptions::default();
        CodegenOptions {
            parser: defaults.parser,
            lexer: defaults.lexer,
            start: defaults.start,
            maybe_placeholders: defaults.maybe_placeholders,
            keep_all_tokens: defaults.keep_all_tokens,
        }
    }
}

/// Maps rules, aliases and terminals to the type their visit method returns (`#[lark_result(T, names...)]`).
#[derive(Clone, Debug)]
pub struct ResultMapping {
    /// The type, as Rust source (`f64`, `crate::model::Order`).
    pub ty: String,
    /// The rule, alias and terminal names.
    pub names: Vec<String>,
}

/// Why the code couldn't be generated.
#[derive(Clone, Debug)]
pub struct CodegenError {
    pub message: String,
    /// The index of the result mapping the error is about, if any.
    pub mapping: Option<usize>,
}

impl fmt::Display for CodegenError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for CodegenError {}

fn error(message: impl Into<String>, mapping: Option<usize>) -> CodegenError {
    CodegenError {
        message: message.into(),
        mapping,
    }
}

/// Generates the Rust code of a grammar.
///
/// ```
/// use etalii_lark::codegen::Generator;
/// use etalii_lark::ParserKind;
///
/// let code = Generator::new("start: NUMBER+\n%import common.NUMBER\n%ignore \" \"")
///     .parser(ParserKind::Lalr)
///     .result("f64", ["NUMBER"])
///     .generate()
///     .unwrap();
/// assert!(code.contains("pub trait Visitor"));
/// ```
///
/// The code is a list of items, to be placed in a module of their own; `include!` it from a build script's output,
/// or use the `#[lark_grammar]` macro of `etalii-lark-derive`, which does this for you.
#[derive(Clone, Debug)]
pub struct Generator {
    grammar: String,
    path: Option<PathBuf>,
    options: CodegenOptions,
    results: Vec<ResultMapping>,
}

impl Generator {
    /// A generator for the grammar text.
    pub fn new(grammar: impl Into<String>) -> Generator {
        Generator {
            grammar: grammar.into(),
            path: None,
            options: CodegenOptions::default(),
            results: Vec::new(),
        }
    }

    /// A generator for a grammar file. The generated code embeds the file with `include_str!`, so the path should be
    /// absolute (from `CARGO_MANIFEST_DIR`); relative `%import`s are resolved from its directory.
    pub fn open(path: impl Into<PathBuf>) -> std::io::Result<Generator> {
        let path = path.into();
        let grammar = std::fs::read_to_string(&path)?;
        Ok(Generator {
            path: Some(path),
            ..Generator::new(grammar)
        })
    }

    /// Sets all options.
    pub fn options(mut self, options: CodegenOptions) -> Self {
        self.options = options;
        self
    }

    pub fn parser(mut self, parser: ParserKind) -> Self {
        self.options.parser = parser;
        self
    }

    pub fn lexer(mut self, lexer: LexerKind) -> Self {
        self.options.lexer = lexer;
        self
    }

    /// Sets the start rules.
    pub fn start<S: Into<String>>(mut self, start: impl IntoIterator<Item = S>) -> Self {
        self.options.start = start.into_iter().map(Into::into).collect();
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

    /// Maps rules, aliases and terminals to a result type of the typed visitor.
    pub fn result<S: Into<String>>(mut self, ty: impl Into<String>, names: impl IntoIterator<Item = S>) -> Self {
        self.results.push(ResultMapping {
            ty: ty.into(),
            names: names.into_iter().map(Into::into).collect(),
        });
        self
    }

    fn lark_options(&self) -> LarkOptions {
        LarkOptions {
            parser: self.options.parser,
            lexer: self.options.lexer,
            start: self.options.start.clone(),
            maybe_placeholders: self.options.maybe_placeholders,
            keep_all_tokens: self.options.keep_all_tokens,
            source_path: self.path.as_ref().map(|p| p.display().to_string()),
            ..Default::default()
        }
    }

    /// Compiles the grammar and generates the code.
    pub fn generate(&self) -> Result<String, CodegenError> {
        if self.options.lexer == LexerKind::Custom {
            return Err(error("Code generation doesn't support custom lexers.", None));
        }
        let lark = Lark::new(&self.grammar, self.lark_options()).map_err(|e| error(format!("{}: {e}", e.lark_name()), None))?;
        let shapes = Shapes::of(&lark);
        let terminals: BTreeSet<String> = lark.terminals().iter().map(|t| t.name.to_string()).collect();
        let model = Model::new(&shapes, terminals, &self.options.start, &self.results)?;
        let mut out = Out::default();
        Emitter {
            generator: self,
            model: &model,
            out: &mut out,
        }
        .emit();
        Ok(out.text)
    }
}

/// How a child is represented: a value from the typed visitor, a node wrapper, a token or any node.
#[derive(Clone, Debug, PartialEq, Eq)]
enum Repr {
    /// The result of the visit method; the index of its type.
    Value(usize),
    /// The node wrapper of the tree.
    Node(String),
    Token,
    Any,
}

/// The type of the children of an element (without the `Option` of placeholders, and the kind).
#[derive(Clone, Debug)]
enum Inner {
    Unified(Repr),
    /// A generated enum: its name and its variants (variant name, member, representation).
    Enum(usize),
}

#[derive(Clone, Debug)]
struct EnumDef {
    name: String,
    variants: Vec<(String, Member, Repr)>,
    /// Whether it is a typed enum (with visitor values), or an accessor enum.
    typed: bool,
}

#[derive(Clone, Debug)]
struct ElementType {
    inner: Inner,
    /// The children can be placeholders.
    nullable: bool,
}

struct Model {
    /// Tree names that make trees, with their shapes.
    trees: BTreeMap<String, Shape>,
    references: BTreeMap<String, Vec<Member>>,
    terminals: BTreeSet<String>,
    /// Mapped names, with the index of their type.
    mapped: BTreeMap<String, usize>,
    /// The distinct result types.
    types: Vec<String>,
    /// The struct of each tree name.
    nodes: BTreeMap<String, String>,
    enums: Vec<EnumDef>,
    /// The element types of each fixed shape, untyped and typed.
    accessors: BTreeMap<String, Vec<ElementType>>,
    params: BTreeMap<String, Vec<ElementType>>,
    /// The type of the trees a start rule can produce, when they all have the same result type.
    start_types: BTreeMap<String, usize>,
}

fn normalize_type(ty: &str) -> String {
    ty.chars().filter(|c| !c.is_whitespace()).collect()
}

impl Model {
    fn new(shapes: &Shapes, terminals: BTreeSet<String>, start: &[String], results: &[ResultMapping]) -> Result<Model, CodegenError> {
        let trees: BTreeMap<String, Shape> = shapes
            .trees
            .iter()
            .filter(|(_, s)| **s != Shape::Inlined)
            .map(|(n, s)| (n.clone(), s.clone()))
            .collect();

        // The names a mapping may name: trees, terminals, rules, and the _rules children stand for.
        let mut known: BTreeSet<String> = shapes.trees.keys().cloned().collect();
        known.extend(terminals.iter().cloned());
        known.extend(shapes.references.keys().cloned());
        for shape in shapes.trees.values() {
            if let Shape::Fixed(elements) = shape {
                known.extend(elements.iter().filter_map(|e| e.slot.clone()));
            }
        }
        let mut mapped = BTreeMap::new();
        let mut types: Vec<String> = Vec::new();
        for (index, mapping) in results.iter().enumerate() {
            let normalized = normalize_type(&mapping.ty);
            let type_index = match types.iter().position(|t| normalize_type(t) == normalized) {
                Some(i) => i,
                None => {
                    types.push(mapping.ty.clone());
                    types.len() - 1
                }
            };
            for name in &mapping.names {
                if !known.contains(name) {
                    return Err(error(
                        format!("`{name}` is not a rule, alias or terminal of the grammar."),
                        Some(index),
                    ));
                }
                if mapped.insert(name.clone(), type_index).is_some() {
                    return Err(error(format!("`{name}` has more than one result type."), Some(index)));
                }
            }
        }
        let mapping_of = |name: &str| results.iter().position(|m| m.names.iter().any(|n| n == name));

        let mut used: BTreeSet<String> = ["Visitor".to_string()].into();
        let mut nodes = BTreeMap::new();
        for name in trees.keys() {
            nodes.insert(name.clone(), unique(format!("{}Node", pascal(name)), &mut used));
        }

        let mut model = Model {
            trees,
            references: shapes.references.clone(),
            terminals,
            mapped,
            types,
            nodes,
            enums: Vec::new(),
            accessors: BTreeMap::new(),
            params: BTreeMap::new(),
            start_types: BTreeMap::new(),
        };
        let trees: Vec<(String, Shape)> = model.trees.iter().map(|(n, s)| (n.clone(), s.clone())).collect();
        for (name, shape) in &trees {
            let Shape::Fixed(elements) = shape else { continue };
            let mut accessors = Vec::new();
            let mut params = Vec::new();
            for e in elements {
                accessors.push(model.element_type(name, e, false, &mut used));
                if model.mapped.contains_key(name) {
                    let typed = model.element_type(name, e, true, &mut used);
                    let slot_index = e.slot.as_deref().and_then(|s| model.mapped.get(s).copied());
                    if let Some(slot_type) = slot_index {
                        if let Inner::Unified(Repr::Value(t)) = typed.inner {
                            if t == slot_type {
                                params.push(typed);
                                continue;
                            }
                        }
                        let slot = e.slot.as_deref().unwrap();
                        return Err(error(
                            format!(
                                "`{slot}` is mapped to `{}`, but a `{name}` child it stands for can be {}, which doesn't have that result type.",
                                model.types[slot_type],
                                model.mismatch(e, slot_type)
                            ),
                            mapping_of(slot),
                        ));
                    }
                    params.push(typed);
                }
            }
            model.accessors.insert(name.clone(), accessors);
            if model.mapped.contains_key(name) {
                model.params.insert(name.clone(), params);
            }
        }
        for s in start {
            if model.trees.contains_key(s) {
                continue;
            }
            let Some(members) = model.references.get(s) else { continue };
            let reprs: BTreeSet<Option<usize>> = members
                .iter()
                .map(|m| match model.repr(m, true) {
                    Repr::Value(t) => Some(t),
                    _ => None,
                })
                .collect();
            if let [Some(t)] = reprs.iter().copied().collect::<Vec<_>>()[..] {
                model.start_types.insert(s.clone(), t);
            }
        }
        Ok(model)
    }

    fn mismatch(&self, e: &ShapeElement, slot_type: usize) -> String {
        for m in &e.members {
            if *m == Member::Placeholder {
                continue;
            }
            match self.repr(m, true) {
                Repr::Value(t) if t == slot_type => {}
                Repr::Value(t) => return format!("`{m}` (`{}`)", self.types[t]),
                _ => return format!("`{m}` (not mapped)"),
            }
        }
        "something else".to_string()
    }

    fn repr(&self, member: &Member, typed: bool) -> Repr {
        match member {
            Member::Tree(name) | Member::Token(name) if typed && self.mapped.contains_key(name) => Repr::Value(self.mapped[name]),
            Member::Tree(name) => self.nodes.get(name).map(|n| Repr::Node(n.clone())).unwrap_or(Repr::Any),
            Member::Token(_) => Repr::Token,
            Member::Placeholder | Member::Any => Repr::Any,
        }
    }

    fn element_type(&mut self, tree: &str, e: &ShapeElement, typed: bool, used: &mut BTreeSet<String>) -> ElementType {
        let nullable = e.members.contains(&Member::Placeholder);
        let members: Vec<&Member> = e.members.iter().filter(|m| **m != Member::Placeholder).collect();
        if members.is_empty() || members.iter().any(|m| **m == Member::Any) {
            return ElementType {
                inner: Inner::Unified(Repr::Any),
                nullable,
            };
        }
        let reprs: Vec<Repr> = members.iter().map(|m| self.repr(m, typed)).collect();
        if reprs.iter().all(|r| *r == reprs[0]) {
            return ElementType {
                inner: Inner::Unified(reprs[0].clone()),
                nullable,
            };
        }
        let typed = typed && reprs.iter().any(|r| matches!(r, Repr::Value(_)));
        let base = match &e.slot {
            Some(slot) => pascal(slot),
            None => format!("{}{}", pascal(tree), pascal(&e.name)),
        };
        let base = if typed { format!("{base}Value") } else { base };
        let mut variants: Vec<(String, Member, Repr)> = Vec::new();
        let mut names = BTreeSet::new();
        for (m, r) in members.iter().zip(&reprs) {
            if let Member::Tree(name) = m {
                names.insert(pascal(name));
                variants.push((pascal(name), (*m).clone(), r.clone()));
            }
        }
        for (m, r) in members.iter().zip(&reprs) {
            if let Member::Token(name) = m {
                let mut variant = pascal(name);
                if names.contains(&variant) {
                    variant.push_str("Token");
                }
                names.insert(variant.clone());
                variants.push((variant, (*m).clone(), r.clone()));
            }
        }
        // The same enum serves every element with the same name and members.
        if let Some(i) = self
            .enums
            .iter()
            .position(|d| d.typed == typed && (d.name == base || d.name.starts_with(&base)) && d.variants == variants)
        {
            return ElementType {
                inner: Inner::Enum(i),
                nullable,
            };
        }
        let name = unique(base, used);
        self.enums.push(EnumDef { name, variants, typed });
        ElementType {
            inner: Inner::Enum(self.enums.len() - 1),
            nullable,
        }
    }

    fn has_lifetime(&self, repr: &Repr) -> bool {
        !matches!(repr, Repr::Value(_))
    }

    fn enum_has_lifetime(&self, index: usize) -> bool {
        self.enums[index].variants.iter().any(|(_, _, r)| self.has_lifetime(r))
    }

    fn repr_type(&self, repr: &Repr) -> String {
        match repr {
            Repr::Value(t) => format!("types::T{t}"),
            Repr::Node(n) => format!("{n}<'a>"),
            Repr::Token => "&'a __lark::Token".to_string(),
            Repr::Any => "&'a __lark::Node".to_string(),
        }
    }

    fn inner_type(&self, inner: &Inner) -> String {
        match inner {
            Inner::Unified(r) => self.repr_type(r),
            Inner::Enum(i) if self.enum_has_lifetime(*i) => format!("{}<'a>", self.enums[*i].name),
            Inner::Enum(i) => self.enums[*i].name.clone(),
        }
    }

    fn item_type(&self, t: &ElementType) -> String {
        let inner = self.inner_type(&t.inner);
        if t.nullable {
            format!("Option<{inner}>")
        } else {
            inner
        }
    }
}

fn unique(name: String, used: &mut BTreeSet<String>) -> String {
    let mut candidate = name.clone();
    let mut n = 2;
    while used.contains(&candidate) {
        candidate = format!("{name}{n}");
        n += 1;
    }
    used.insert(candidate.clone());
    candidate
}

/// `add_expr` → `AddExpr`, `NUMBER` → `Number`, `__ANON_0` → `Anon0`.
fn pascal(name: &str) -> String {
    let mut result = String::new();
    for part in name.split(|c: char| c == '_' || !c.is_alphanumeric()).filter(|p| !p.is_empty()) {
        let mut chars = part.chars();
        let first = chars.next().unwrap();
        result.extend(first.to_uppercase());
        result.push_str(&chars.as_str().to_lowercase());
    }
    if result.is_empty() || result.starts_with(|c: char| c.is_ascii_digit()) {
        result.insert(0, 'X');
    }
    if result == "Self" {
        result.push('_');
    }
    result
}

const KEYWORDS: &[&str] = &[
    "as", "break", "const", "continue", "crate", "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop",
    "match", "mod", "move", "mut", "pub", "ref", "return", "self", "static", "struct", "super", "trait", "true", "type", "unsafe", "use",
    "where", "while", "async", "await", "dyn", "abstract", "become", "box", "do", "final", "macro", "override", "priv", "typeof",
    "unsized", "virtual", "yield", "try", "gen",
];

/// `TEXT` → `text`, `__ANON_0` → `anon_0`, `type` → `type_`.
fn snake(name: &str) -> String {
    let mut result: String = name
        .trim_start_matches('_')
        .chars()
        .map(|c| if c.is_alphanumeric() { c.to_ascii_lowercase() } else { '_' })
        .collect();
    if result.is_empty() || result.starts_with(|c: char| c.is_ascii_digit()) {
        result.insert(0, 'x');
    }
    escape(result)
}

/// `type` → `type_`.
fn escape(mut name: String) -> String {
    if KEYWORDS.contains(&name.as_str()) {
        name.push('_');
    }
    name
}

/// `line` → `lines`, `address` → `addresses`, `entry` → `entries`.
fn plural(name: &str) -> String {
    if KEYWORDS.contains(&name.trim_end_matches('_')) {
        return escape(plural(name.trim_end_matches('_')));
    }
    if ["s", "x", "z", "ch", "sh"].iter().any(|s| name.ends_with(s)) {
        format!("{name}es")
    } else if name.ends_with('y') && !name.ends_with("ay") && !name.ends_with("ey") && !name.ends_with("oy") && !name.ends_with("uy") {
        format!("{}ies", &name[..name.len() - 1])
    } else {
        format!("{name}s")
    }
}

/// Names for the elements of a shape: snake case, plural for repeated elements, unique, and not one of `reserved`.
fn element_names(elements: &[ShapeElement], reserved: &[&str]) -> Vec<String> {
    let mut used: BTreeSet<String> = reserved.iter().map(|s| s.to_string()).collect();
    let mut result = Vec::new();
    for e in elements {
        let base = snake(&e.name);
        let base = if e.kind == ElementKind::Repeated {
            escape(plural(&base))
        } else {
            base
        };
        let mut name = base.clone();
        let mut n = 2;
        while used.contains(&name) {
            name = format!("{base}{n}");
            n += 1;
        }
        used.insert(name.clone());
        result.push(name);
    }
    result
}

#[derive(Default)]
struct Out {
    text: String,
    indent: usize,
}

impl Out {
    fn line(&mut self, line: impl AsRef<str>) {
        let line = line.as_ref();
        if line.starts_with('}') || line.starts_with(')') || line.starts_with(']') {
            self.indent = self.indent.saturating_sub(1);
        }
        if !line.is_empty() {
            for _ in 0..self.indent {
                self.text.push_str("    ");
            }
        }
        self.text.push_str(line);
        self.text.push('\n');
        if line.ends_with('{') || line.ends_with('(') || line.ends_with('[') {
            self.indent += 1;
        }
    }
}

const MISMATCH: &str = "the tree doesn't match the grammar";

struct Emitter<'g> {
    generator: &'g Generator,
    model: &'g Model,
    out: &'g mut Out,
}

/// A pattern that matches the children that are one of the members.
fn fits(members: &[Member], child: &str) -> String {
    if members.contains(&Member::Any) {
        return "true".to_string();
    }
    let trees: Vec<String> = members
        .iter()
        .filter_map(|m| match m {
            Member::Tree(n) => Some(format!("{n:?}")),
            _ => None,
        })
        .collect();
    let tokens: Vec<String> = members
        .iter()
        .filter_map(|m| match m {
            Member::Token(n) => Some(format!("{n:?}")),
            _ => None,
        })
        .collect();
    let tree = if trees.is_empty() {
        "false".to_string()
    } else {
        format!("matches!(t.data.as_str(), {})", trees.join(" | "))
    };
    let token = if tokens.is_empty() {
        "false".to_string()
    } else {
        format!("matches!(t.kind.as_str(), {})", tokens.join(" | "))
    };
    let missing = members.contains(&Member::Placeholder);
    format!("match {child} {{ __lark::Node::Tree(t) => {tree}, __lark::Node::Token(t) => {token}, __lark::Node::Missing => {missing} }}")
}

impl Emitter<'_> {
    fn line(&mut self, line: impl AsRef<str>) {
        self.out.line(line);
    }

    fn emit(&mut self) {
        let model = self.model;
        self.line("// Generated by etalii-lark's code generator. Don't edit.");
        self.line("#[allow(dead_code, unused_imports, unused_mut, unused_variables, unused_assignments, non_camel_case_types, missing_docs, clippy::all, clippy::pedantic, clippy::nursery)]");
        self.line("mod __generated {");
        self.line("use ::etalii_lark as __lark;");
        self.line("");
        if !model.types.is_empty() {
            // The result types, resolved where the module is declared, before the generated names can shadow them.
            self.line("mod types {");
            self.line("use super::super::super::*;");
            for (i, ty) in model.types.iter().enumerate() {
                self.line(format!("pub type T{i} = {ty};"));
            }
            self.line("}");
            self.line("");
        }
        self.emit_parser();
        self.emit_constants();
        for name in model.trees.keys() {
            self.emit_node(name);
        }
        for i in 0..model.enums.len() {
            self.emit_enum(i);
        }
        if !model.mapped.is_empty() {
            self.emit_visitor();
        }
        self.line("}");
        self.line("pub use __generated::*;");
    }

    fn emit_parser(&mut self) {
        let generator = self.generator;
        let options = &generator.options;
        match &generator.path {
            Some(path) => {
                self.line("/// The grammar.");
                self.line(format!(
                    "pub static GRAMMAR: &str = include_str!({:?});",
                    path.display().to_string()
                ));
            }
            None => {
                self.line("/// The grammar.");
                self.line(format!("pub static GRAMMAR: &str = {:?};", generator.grammar));
            }
        }
        self.line("");
        self.line("/// The options the parser is built with.");
        self.line("pub fn options() -> __lark::LarkOptions {");
        self.line("__lark::LarkOptions {");
        self.line(format!("parser: __lark::ParserKind::{:?},", options.parser));
        self.line(format!("lexer: __lark::LexerKind::{:?},", options.lexer));
        let start: Vec<String> = options.start.iter().map(|s| format!("{s:?}.to_string()")).collect();
        self.line(format!("start: vec![{}],", start.join(", ")));
        self.line(format!("maybe_placeholders: {},", options.maybe_placeholders));
        self.line(format!("keep_all_tokens: {},", options.keep_all_tokens));
        if let Some(path) = &generator.path {
            self.line(format!("source_path: Some({:?}.to_string()),", path.display().to_string()));
        }
        self.line("..Default::default()");
        self.line("}");
        self.line("}");
        self.line("");
        self.line("/// The parser, built on first use.");
        self.line("pub fn parser() -> &'static __lark::Lark {");
        self.line("static PARSER: ::std::sync::OnceLock<__lark::Lark> = ::std::sync::OnceLock::new();");
        self.line(
            "PARSER.get_or_init(|| __lark::Lark::new(GRAMMAR, options()).expect(\"the grammar was checked when the code was generated\"))",
        );
        self.line("}");
        self.line("");
        let first = &options.start[0];
        self.line(format!("/// Parses the text from the start rule `{first}`."));
        self.line("pub fn parse(text: &str) -> __lark::Result<__lark::Tree> {");
        self.line(format!("parser().parse_start(text, Some({first:?}))"));
        self.line("}");
        if options.start.len() > 1 {
            for s in &options.start {
                self.line("");
                self.line(format!("/// Parses the text from the start rule `{s}`."));
                self.line(format!("pub fn parse_{}(text: &str) -> __lark::Result<__lark::Tree> {{", snake(s)));
                self.line(format!("parser().parse_start(text, Some({s:?}))"));
                self.line("}");
            }
        }
        self.line("");
        self.line("/// Splits the text into tokens, without the ignored ones.");
        self.line("pub fn lex(text: &str) -> __lark::Result<Vec<__lark::Token>> {");
        self.line("parser().lex(text, false)");
        self.line("}");
        self.line("");
    }

    fn emit_constants(&mut self) {
        let model = self.model;
        let mut rules: BTreeSet<&String> = model.trees.keys().collect();
        rules.extend(model.references.keys());
        self.line("/// The names of the rules, aliases and templates.");
        self.line("pub mod rules {");
        for name in rules {
            self.line(format!(
                "pub const {}: &str = {name:?};",
                snake(name).trim_end_matches('_').to_uppercase()
            ));
        }
        self.line("}");
        self.line("");
        self.line("/// The names of the terminals.");
        self.line("pub mod terminals {");
        for name in &model.terminals {
            let ident = name
                .chars()
                .map(|c| if c.is_alphanumeric() || c == '_' { c } else { '_' })
                .collect::<String>();
            self.line(format!("pub const {ident}: &str = {name:?};"));
        }
        self.line("}");
        self.line("");
    }

    fn emit_node(&mut self, name: &str) {
        let model = self.model;
        let node = &model.nodes[name];
        let shape = &model.trees[name];
        self.line(format!("/// A `{name}` tree."));
        self.line("#[derive(Clone, Copy, Debug)]");
        self.line(format!("pub struct {node}<'a> {{"));
        self.line("tree: &'a __lark::Tree,");
        self.line("}");
        self.line("");
        self.line(format!("impl<'a> {node}<'a> {{"));
        self.line(format!("/// The tree data, `{name:?}`."));
        self.line(format!("pub const NAME: &'static str = {name:?};"));
        self.line("");
        self.line(format!("/// The tree as a `{node}`, if it is a `{name}` tree."));
        self.line("pub fn new(tree: &'a __lark::Tree) -> Option<Self> {");
        self.line(format!("(tree.data == {name:?}).then_some({node} {{ tree }})"));
        self.line("}");
        self.line("");
        self.line("/// The tree.");
        self.line("pub fn tree(&self) -> &'a __lark::Tree {");
        self.line("self.tree");
        self.line("}");
        match shape {
            Shape::Fixed(elements) => {
                self.line("");
                self.emit_spans(elements);
                let names = element_names(elements, &["tree", "new", "children"]);
                let types = &model.accessors[name];
                for (i, (e, t)) in elements.iter().zip(types).enumerate() {
                    self.line("");
                    let item = model.item_type(t);
                    let conv = self.conversion(t, "c", false);
                    let fname = &names[i];
                    match e.kind {
                        ElementKind::Single => {
                            self.line(format!("/// The `{}` child.", e.name));
                            self.line(format!("pub fn {fname}(&self) -> {item} {{"));
                            self.line(format!("let c = &self.tree.children[self.spans()[{i}].0];"));
                            self.line(conv);
                        }
                        ElementKind::Optional => {
                            self.line(format!("/// The `{}` child, if there is one.", e.name));
                            let item = if t.nullable { item } else { format!("Option<{item}>") };
                            let map = if t.nullable { "and_then" } else { "map" };
                            self.line(format!("pub fn {fname}(&self) -> {item} {{"));
                            self.line(format!("let (start, end) = self.spans()[{i}];"));
                            self.line(format!("self.tree.children[start..end].first().{map}(|c| {conv})"));
                        }
                        ElementKind::Repeated => {
                            self.line(format!("/// The `{}` children.", e.name));
                            self.line(format!("pub fn {fname}(&self) -> impl ExactSizeIterator<Item = {item}> + 'a {{"));
                            self.line(format!("let (start, end) = self.spans()[{i}];"));
                            self.line("let children: &'a [__lark::Node] = &self.tree.children[start..end];");
                            self.line(format!("children.iter().map(|c| {conv})"));
                        }
                    }
                    self.line("}");
                }
            }
            _ => {
                self.line("");
                self.line("/// The children. The trees have no fixed shape, so they aren't typed.");
                self.line("pub fn children(&self) -> &'a [__lark::Node] {");
                self.line("&self.tree.children");
                self.line("}");
            }
        }
        self.line("}");
        self.line("");
        self.line(format!("impl<'a> ::std::ops::Deref for {node}<'a> {{"));
        self.line("type Target = __lark::Tree;");
        self.line("fn deref(&self) -> &__lark::Tree {");
        self.line("self.tree");
        self.line("}");
        self.line("}");
        self.line("");
    }

    /// Where each element's children start and end. The children are matched greedily, as the shapes guarantee.
    fn emit_spans(&mut self, elements: &[ShapeElement]) {
        let n = elements.len();
        self.line(format!("fn spans(&self) -> [(usize, usize); {n}] {{"));
        self.line("let children = &self.tree.children;");
        self.line("let mut i = 0;");
        self.line(format!("let mut spans = [(0, 0); {n}];"));
        for (k, e) in elements.iter().enumerate() {
            match e.kind {
                ElementKind::Single => {
                    self.line(format!("spans[{k}] = (i, i + 1);"));
                    self.line("i += 1;");
                }
                ElementKind::Optional => {
                    self.line("let start = i;");
                    self.line(format!("if i < children.len() && {} {{", fits(&e.members, "&children[i]")));
                    self.line("i += 1;");
                    self.line("}");
                    self.line(format!("spans[{k}] = (start, i);"));
                }
                ElementKind::Repeated => {
                    self.line("let start = i;");
                    self.line(format!("while i < children.len() && {} {{", fits(&e.members, "&children[i]")));
                    self.line("i += 1;");
                    self.line("}");
                    self.line(format!("spans[{k}] = (start, i);"));
                }
            }
        }
        self.line("spans");
        self.line("}");
    }

    /// An expression that converts the child `c` (a `&'a Node`) to the element's item type. Typed conversions take
    /// the values of visited children from `values`.
    fn conversion(&self, t: &ElementType, c: &str, typed: bool) -> String {
        let inner = self.inner_conversion(&t.inner, c, typed);
        if t.nullable {
            format!("match {c} {{ __lark::Node::Missing => None, {c} => Some({inner}) }}")
        } else {
            inner
        }
    }

    fn inner_conversion(&self, inner: &Inner, c: &str, typed: bool) -> String {
        match inner {
            Inner::Unified(r) => self.repr_conversion(r, c, typed),
            Inner::Enum(i) => {
                let def = &self.model.enums[*i];
                let mut trees = Vec::new();
                let mut tokens = Vec::new();
                for (variant, member, repr) in &def.variants {
                    match member {
                        Member::Tree(name) => trees.push(format!(
                            "{name:?} => {}::{variant}({}),",
                            def.name,
                            self.member_conversion(repr, "t", true, typed)
                        )),
                        Member::Token(name) => tokens.push(format!(
                            "{name:?} => {}::{variant}({}),",
                            def.name,
                            self.member_conversion(repr, "t", false, typed)
                        )),
                        _ => {}
                    }
                }
                format!(
                    "match {c} {{ __lark::Node::Tree(t) => match t.data.as_str() {{ {} _ => unreachable!({MISMATCH:?}) }}, __lark::Node::Token(t) => match t.kind.as_str() {{ {} _ => unreachable!({MISMATCH:?}) }}, __lark::Node::Missing => unreachable!({MISMATCH:?}) }}",
                    trees.join(" "),
                    tokens.join(" ")
                )
            }
        }
    }

    /// The conversion of a child that is known to be a tree (`is_tree`) or token, bound to `t`.
    fn member_conversion(&self, repr: &Repr, t: &str, is_tree: bool, typed: bool) -> String {
        match repr {
            Repr::Value(i) if typed => format!("__take_{i}(&mut values)"),
            Repr::Node(n) if is_tree => format!("{n} {{ tree: {t} }}"),
            Repr::Token if !is_tree => t.to_string(),
            _ => unreachable!("a member's representation matches its kind"),
        }
    }

    fn repr_conversion(&self, repr: &Repr, c: &str, typed: bool) -> String {
        match repr {
            Repr::Value(i) => format!("__take_{i}(&mut values)"),
            Repr::Node(n) => format!("{n} {{ tree: {c}.as_tree().expect({MISMATCH:?}) }}"),
            Repr::Token => format!("{c}.as_token().expect({MISMATCH:?})"),
            // Children that were visited have a value, which isn't used here.
            Repr::Any if typed => format!("{{ if __visited({c}) {{ values.next(); }} {c} }}"),
            Repr::Any => c.to_string(),
        }
    }

    fn emit_enum(&mut self, index: usize) {
        let model = self.model;
        let def = &model.enums[index];
        let lifetime = if model.enum_has_lifetime(index) { "<'a>" } else { "" };
        let members: Vec<String> = def.variants.iter().map(|(_, m, _)| format!("`{m}`")).collect();
        self.line(format!("/// One of {}.", members.join(", ")));
        if !def.typed {
            self.line("#[derive(Clone, Copy, Debug)]");
        }
        self.line(format!("pub enum {}{lifetime} {{", def.name));
        for (variant, _, repr) in &def.variants {
            self.line(format!("{variant}({}),", model.repr_type(repr)));
        }
        self.line("}");
        self.line("");
    }

    fn emit_visitor(&mut self) {
        let model = self.model;
        // The values of visited children, one variant per result type.
        self.line("enum __Value {");
        for i in 0..model.types.len() {
            self.line(format!("V{i}(types::T{i}),"));
        }
        self.line("}");
        self.line("");
        for i in 0..model.types.len() {
            self.line(format!(
                "fn __take_{i}(values: &mut ::std::vec::IntoIter<__Value>) -> types::T{i} {{"
            ));
            self.line("match values.next() {");
            self.line(format!("Some(__Value::V{i}(value)) => value,"));
            self.line(format!("_ => unreachable!({MISMATCH:?}),"));
            self.line("}");
            self.line("}");
            self.line("");
        }
        let mapped_trees: Vec<&String> = model.mapped.keys().filter(|n| model.trees.contains_key(*n)).collect();
        let mapped_tokens: Vec<&String> = model.mapped.keys().filter(|n| model.terminals.contains(*n)).collect();
        let quoted = |names: &[&String]| names.iter().map(|n| format!("{n:?}")).collect::<Vec<_>>().join(" | ");
        let tree_pattern = if mapped_trees.is_empty() {
            "false".to_string()
        } else {
            format!("matches!(t.data.as_str(), {})", quoted(&mapped_trees))
        };
        let token_pattern = if mapped_tokens.is_empty() {
            "false".to_string()
        } else {
            format!("matches!(t.kind.as_str(), {})", quoted(&mapped_tokens))
        };
        self.line("/// Whether the child gets a value: a mapped tree or token.");
        self.line("fn __visited(node: &__lark::Node) -> bool {");
        self.line(format!(
            "match node {{ __lark::Node::Tree(t) => {tree_pattern}, __lark::Node::Token(t) => {token_pattern}, __lark::Node::Missing => false }}"
        ));
        self.line("}");
        self.line("");

        self.line("/// A typed visitor: visits the trees bottom-up, and passes each tree's children to its method, already");
        self.line("/// converted to their result types. Children without a result type are passed as their node.");
        self.line("pub trait Visitor {");
        self.line("/// The error that stops the visit.");
        self.line("type Error;");
        for name in &mapped_trees {
            let ty = format!("types::T{}", model.mapped[*name]);
            let node = &model.nodes[*name];
            self.line("");
            let mut params = vec![format!("node: {node}<'a>")];
            if let Shape::Fixed(elements) = &model.trees[*name] {
                let names = element_names(elements, &["node", "self"]);
                for ((e, t), pname) in elements.iter().zip(&model.params[*name]).zip(&names) {
                    let item = model.item_type(t);
                    let pty = match e.kind {
                        ElementKind::Single => item,
                        ElementKind::Optional if t.nullable => item,
                        ElementKind::Optional => format!("Option<{item}>"),
                        ElementKind::Repeated => format!("Vec<{item}>"),
                    };
                    params.push(format!("{pname}: {pty}"));
                }
                self.line(format!("/// Visits a `{name}` tree, whose children were visited first."));
            } else {
                self.line(format!(
                    "/// Visits a `{name}` tree. It has no fixed shape, so its children aren't visited first."
                ));
            }
            self.line(format!(
                "fn visit_{}<'a>(&mut self, {}) -> Result<{ty}, Self::Error>;",
                snake(name).trim_end_matches('_'),
                params.join(", ")
            ));
        }
        for name in &mapped_tokens {
            let ty = format!("types::T{}", model.mapped[*name]);
            self.line("");
            self.line(format!("/// Visits a `{name}` token."));
            self.line(format!(
                "fn visit_{}_token(&mut self, token: &__lark::Token) -> Result<{ty}, Self::Error>;",
                snake(name).trim_end_matches('_')
            ));
        }
        for name in &mapped_trees {
            let i = model.mapped[*name];
            let ty = format!("types::T{i}");
            self.line("");
            self.line(format!(
                "/// Visits a `{name}` tree and its children. Panics when it isn't a `{name}` tree."
            ));
            self.line(format!(
                "fn visit_{}_tree(&mut self, tree: &__lark::Tree) -> Result<{ty}, Self::Error> where Self: Sized {{",
                snake(name).trim_end_matches('_')
            ));
            self.line(format!("assert_eq!(tree.data, {name:?}, \"not a `{name}` tree\");"));
            self.line(format!("__visit(self, tree).map(|value| __take_{i}(&mut vec![value].into_iter()))"));
            self.line("}");
        }
        for (start, i) in &model.start_types {
            let ty = format!("types::T{i}");
            self.line("");
            self.line(format!("/// Visits a tree parsed from the start rule `{start}`."));
            self.line(format!(
                "fn visit_{}(&mut self, tree: &__lark::Tree) -> Result<{ty}, Self::Error> where Self: Sized {{",
                snake(start).trim_end_matches('_')
            ));
            self.line(format!("__visit(self, tree).map(|value| __take_{i}(&mut vec![value].into_iter()))"));
            self.line("}");
        }
        self.line("}");
        self.line("");
        self.emit_visit(&mapped_trees, &mapped_tokens);
    }

    fn emit_visit(&mut self, mapped_trees: &[&String], mapped_tokens: &[&String]) {
        let model = self.model;
        let fixed: Vec<&&String> = mapped_trees
            .iter()
            .filter(|n| matches!(model.trees[**n], Shape::Fixed(_)))
            .collect();
        let descends = if fixed.is_empty() {
            "false".to_string()
        } else {
            format!(
                "matches!(t.data.as_str(), {})",
                fixed.iter().map(|n| format!("{n:?}")).collect::<Vec<_>>().join(" | ")
            )
        };
        self.line("fn __visit<V: Visitor + ?Sized>(visitor: &mut V, root: &__lark::Tree) -> Result<__Value, V::Error> {");
        self.line(format!(
            "__lark::codegen::visit_bottom_up(root, __visited, |t| {descends}, |item, mut values| match item {{"
        ));
        self.line("__lark::codegen::Item::Tree(t) => match t.data.as_str() {");
        for name in mapped_trees {
            let i = model.mapped[*name];
            let node = &model.nodes[*name];
            let fname = snake(name).trim_end_matches('_').to_string();
            self.line(format!("{name:?} => {{"));
            self.line(format!("let node = {node} {{ tree: t }};"));
            let mut args = vec!["node".to_string()];
            if let Shape::Fixed(elements) = &model.trees[*name] {
                self.line("let spans = node.spans();");
                for (k, (e, t)) in elements.iter().zip(&model.params[*name]).enumerate() {
                    let conv = self.conversion(t, "c", true);
                    let expr = match e.kind {
                        ElementKind::Single => format!("{{ let c = &t.children[spans[{k}].0]; {conv} }}"),
                        ElementKind::Optional => {
                            let map = if t.nullable { "and_then" } else { "map" };
                            format!("t.children[spans[{k}].0..spans[{k}].1].first().{map}(|c| {conv})")
                        }
                        ElementKind::Repeated => {
                            format!("t.children[spans[{k}].0..spans[{k}].1].iter().map(|c| {conv}).collect::<Vec<_>>()")
                        }
                    };
                    self.line(format!("let p{k} = {expr};"));
                    args.push(format!("p{k}"));
                }
            }
            self.line(format!("visitor.visit_{fname}({}).map(__Value::V{i})", args.join(", ")));
            self.line("}");
        }
        self.line(format!("_ => unreachable!({MISMATCH:?}),"));
        self.line("},");
        self.line("__lark::codegen::Item::Token(t) => match t.kind.as_str() {");
        for name in mapped_tokens {
            let i = model.mapped[*name];
            let fname = snake(name).trim_end_matches('_').to_string();
            self.line(format!("{name:?} => visitor.visit_{fname}_token(t).map(__Value::V{i}),"));
        }
        self.line(format!("_ => unreachable!({MISMATCH:?}),"));
        self.line("},");
        self.line("})");
        self.line("}");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names() {
        assert_eq!(pascal("add_expr"), "AddExpr");
        assert_eq!(pascal("NUMBER"), "Number");
        assert_eq!(pascal("__ANON_0"), "Anon0");
        assert_eq!(snake("TEXT"), "text");
        assert_eq!(snake("type"), "type_");
        assert_eq!(snake("__ANON_0"), "anon_0");
        assert_eq!(plural("line"), "lines");
        assert_eq!(plural("address"), "addresses");
        assert_eq!(plural("entry"), "entries");
        assert_eq!(plural("key"), "keys");
    }

    #[test]
    fn reports_unknown_names() {
        let e = Generator::new("start: \"a\"").result("u8", ["nope"]).generate().unwrap_err();
        assert_eq!(e.mapping, Some(0));
        assert!(e.message.contains("`nope`"), "{e}");
    }

    #[test]
    fn reports_grammar_errors() {
        let e = Generator::new("start: missing").generate().unwrap_err();
        assert!(e.message.starts_with("GrammarError"), "{e}");
    }
}
