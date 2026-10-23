//! The Earley parser, with the basic lexer or the dynamic (scannerless) lexer, and its shared packed parse forest
//! (Lark's `earley`, `xearley`, `earley_common` and `earley_forest`).
//!
//! The core algorithm is Elizabeth Scott's: <https://www.sciencedirect.com/science/article/pii/S1571066108001497>.

use std::collections::{BTreeSet, HashMap, HashSet};
use std::sync::Arc;

use indexmap::IndexMap;

use super::grammar_analysis::{expand_rule, GrammarAnalyzer};
use crate::error::{Error, ErrorState, Result, TerminalsByName, UnexpectedCharacters, UnexpectedEof, UnexpectedToken};
use crate::grammar::{Rule, Symbol};
use crate::lexer::TokenStream;
use crate::parse_tree_builder::Callbacks;
use crate::pyre::{Flags, Regex};
use crate::token::Token;
use crate::tree::{Node, Tree};

// ---------------------------------------------------------------------------------------------------------------
//  Items and the forest
// ---------------------------------------------------------------------------------------------------------------

/// An Earley item: a rule, the position of the dot, and where the item started.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
struct ItemKey {
    rule: u32,
    ptr: u32,
    start: u32,
}

/// The symbol of a forest node: a rule name, or an LR(0) item for intermediate nodes.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
enum SKey {
    Sym(Symbol),
    Lr0(u32, u32),
}

/// A reference to a node of the forest.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
enum VNode {
    Sym(usize),
    Packed(usize),
    Tok(usize),
}

/// How packed nodes are told apart: by their children, symbol nodes by identity and tokens by type and value.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
enum ChildKey {
    Sym(usize),
    Tok(String, String),
}

struct SymbolNode {
    s: SKey,
    children: IndexMap<(Option<ChildKey>, Option<ChildKey>), usize>,
    priority: f64,
}

struct PackedNode {
    parent: usize,
    rule: usize,
    left: Option<VNode>,
    right: Option<VNode>,
    priority: f64,
}

struct TokenNode {
    token: Token,
    priority: f64,
}

#[derive(Default)]
struct Forest {
    symbols: Vec<SymbolNode>,
    packed: Vec<PackedNode>,
    tokens: Vec<TokenNode>,
}

impl Forest {
    fn child_key(&self, n: Option<VNode>) -> Option<ChildKey> {
        n.map(|n| match n {
            VNode::Sym(i) => ChildKey::Sym(i),
            VNode::Tok(i) => ChildKey::Tok(self.tokens[i].token.kind.clone(), self.tokens[i].token.value.clone()),
            VNode::Packed(_) => unreachable!("packed nodes are never children of packed nodes"),
        })
    }

    fn add_family(&mut self, node: usize, rule: usize, left: Option<VNode>, right: Option<VNode>) {
        let key = (self.child_key(left), self.child_key(right));
        if self.symbols[node].children.contains_key(&key) {
            return;
        }
        let id = self.packed.len();
        self.packed.push(PackedNode {
            parent: node,
            rule,
            left,
            right,
            priority: f64::NEG_INFINITY,
        });
        self.symbols[node].children.insert(key, id);
    }

    fn is_intermediate(&self, node: usize) -> bool {
        matches!(self.symbols[node].s, SKey::Lr0(..))
    }

    fn priority(&self, n: VNode) -> f64 {
        match n {
            VNode::Sym(i) => self.symbols[i].priority,
            VNode::Packed(i) => self.packed[i].priority,
            VNode::Tok(i) => self.tokens[i].priority,
        }
    }

    /// The packed children of a symbol node, from the highest to the lowest priority.
    fn sorted_children(&self, node: usize, rules: &[Arc<Rule>]) -> Vec<usize> {
        let mut children: Vec<usize> = self.symbols[node].children.values().copied().collect();
        children.sort_by(|&a, &b| {
            let (pa, pb) = (&self.packed[a], &self.packed[b]);
            let empty_a = pa.left.is_none() && pa.right.is_none();
            let empty_b = pb.left.is_none() && pb.right.is_none();
            empty_a
                .cmp(&empty_b)
                .then_with(|| (-pa.priority).partial_cmp(&-pb.priority).unwrap_or(std::cmp::Ordering::Equal))
                .then_with(|| rules[pa.rule].order.cmp(&rules[pb.rule].order))
        });
        children
    }
}

type NodeCache = HashMap<(SKey, usize, usize), usize>;

/// An Earley set: the items in insertion order with their forest node, indexed by the symbol they expect.
#[derive(Default)]
struct Column {
    items: IndexMap<ItemKey, Option<usize>>,
    by_expect: HashMap<Symbol, Vec<ItemKey>>,
}

// ---------------------------------------------------------------------------------------------------------------
//  Forest visitors
// ---------------------------------------------------------------------------------------------------------------

trait ForestVisitor {
    fn single_visit(&self) -> bool {
        false
    }
    fn visit_token(&mut self, _forest: &mut Forest, _token: usize) {}
    fn visit_symbol_in(&mut self, forest: &mut Forest, node: usize) -> Option<Vec<VNode>>;
    fn visit_symbol_out(&mut self, forest: &mut Forest, node: usize);
    fn visit_packed_in(&mut self, forest: &mut Forest, node: usize) -> Option<Vec<VNode>>;
    fn visit_packed_out(&mut self, forest: &mut Forest, node: usize);
    fn on_cycle(&mut self, _node: VNode) {}
}

enum Entry {
    Node(VNode),
    Iter(Vec<VNode>, usize),
}

/// A depth-first walk of the forest that doesn't enter cycles (Lark's `ForestVisitor.visit`).
fn visit_forest(v: &mut dyn ForestVisitor, forest: &mut Forest, root: VNode) {
    let mut visiting: HashSet<VNode> = HashSet::new();
    let mut visited: HashSet<VNode> = HashSet::new();
    let mut stack = vec![Entry::Node(root)];
    let single_visit = v.single_visit();
    while let Some(top) = stack.last_mut() {
        let current = match top {
            Entry::Iter(list, pos) => {
                if *pos >= list.len() {
                    stack.pop();
                    continue;
                }
                let next = list[*pos];
                *pos += 1;
                if visiting.contains(&next) {
                    v.on_cycle(next);
                    continue;
                }
                stack.push(Entry::Node(next));
                continue;
            }
            Entry::Node(n) => *n,
        };
        if let VNode::Tok(t) = current {
            v.visit_token(forest, t);
            stack.pop();
            continue;
        }
        if visiting.contains(&current) {
            match current {
                VNode::Packed(p) => v.visit_packed_out(forest, p),
                VNode::Sym(s) => v.visit_symbol_out(forest, s),
                VNode::Tok(_) => unreachable!(),
            }
            stack.pop();
            visiting.remove(&current);
            visited.insert(current);
        } else if single_visit && visited.contains(&current) {
            stack.pop();
        } else {
            visiting.insert(current);
            let next = match current {
                VNode::Packed(p) => v.visit_packed_in(forest, p),
                VNode::Sym(s) => v.visit_symbol_in(forest, s),
                VNode::Tok(_) => unreachable!(),
            };
            if let Some(list) = next {
                stack.push(Entry::Iter(list, 0));
            }
        }
    }
}

/// Cascades the priorities of rules and terminals up the forest (Lark's `ForestSumVisitor`).
struct ForestSumVisitor<'a> {
    rules: &'a [Arc<Rule>],
}

impl ForestVisitor for ForestSumVisitor<'_> {
    fn single_visit(&self) -> bool {
        true
    }

    fn visit_symbol_in(&mut self, forest: &mut Forest, node: usize) -> Option<Vec<VNode>> {
        Some(forest.sorted_children(node, self.rules).into_iter().map(VNode::Packed).collect())
    }

    fn visit_symbol_out(&mut self, forest: &mut Forest, node: usize) {
        let max = forest.symbols[node]
            .children
            .values()
            .map(|&p| forest.packed[p].priority)
            .fold(f64::NEG_INFINITY, f64::max);
        forest.symbols[node].priority = max;
    }

    fn visit_packed_in(&mut self, forest: &mut Forest, node: usize) -> Option<Vec<VNode>> {
        let p = &forest.packed[node];
        Some(p.left.into_iter().chain(p.right).collect())
    }

    fn visit_packed_out(&mut self, forest: &mut Forest, node: usize) {
        let p = &forest.packed[node];
        let mut priority = match self.rules[p.rule].options.priority {
            Some(pr) if pr != 0 && !forest.is_intermediate(p.parent) => pr as f64,
            _ => 0.0,
        };
        if let Some(r) = p.right {
            priority += forest.priority(r);
        }
        if let Some(l) = p.left {
            priority += forest.priority(l);
        }
        forest.packed[node].priority = priority;
    }
}

/// The result of transforming a forest node: a tree or token, or the children of an intermediate node.
#[derive(Clone, Debug)]
enum TVal {
    Node(Node),
    List(Vec<Node>),
}

impl TVal {
    fn into_node(self) -> Node {
        match self {
            TVal::Node(n) => n,
            TVal::List(l) => Node::Tree(Tree::new("_inter", l)),
        }
    }
}

/// Turns the forest into a parse tree, resolving or keeping the ambiguities (Lark's `ForestToParseTree`).
struct ForestToParseTree<'a> {
    rules: &'a [Arc<Rule>],
    callbacks: &'a Callbacks,
    resolve_ambiguity: bool,
    use_cache: bool,
    cache: HashMap<usize, Option<TVal>>,
    on_cycle_retreat: bool,
    cycle_node: Option<VNode>,
    successful_visits: HashSet<VNode>,
    data: HashMap<Option<VNode>, Vec<TVal>>,
    node_stack: Vec<Option<VNode>>,
}

impl<'a> ForestToParseTree<'a> {
    fn transform(mut self, forest: &mut Forest, root: usize) -> Option<Node> {
        self.node_stack.push(None);
        self.data.insert(None, Vec::new());
        visit_forest(&mut self, forest, VNode::Sym(root));
        self.data
            .remove(&None)
            .and_then(|mut r| if r.is_empty() { None } else { Some(r.remove(0).into_node()) })
    }

    fn check_cycle(&mut self, node: VNode) -> bool {
        if self.on_cycle_retreat {
            if Some(node) == self.cycle_node || self.successful_visits.contains(&node) {
                self.cycle_node = None;
                self.on_cycle_retreat = false;
            } else {
                return true;
            }
        }
        false
    }

    fn out_helper(&mut self, node: VNode, transformed: Option<TVal>) {
        if let Some(t) = transformed {
            let parent = *self.node_stack.last().expect("node stack");
            self.data.get_mut(&parent).expect("parent data").push(t);
        }
        self.data.remove(&Some(node));
    }

    fn transform_symbol(&mut self, forest: &Forest, node: usize, data: Vec<TVal>) -> Option<TVal> {
        let v = VNode::Sym(node);
        if !self.successful_visits.contains(&v) {
            return None;
        }
        if self.check_cycle(v) {
            return None;
        }
        self.successful_visits.remove(&v);
        if forest.is_intermediate(node) {
            if data.len() > 1 {
                let children = data.into_iter().map(|c| match c {
                    TVal::List(l) => Node::Tree(Tree::new("_inter", l)),
                    TVal::Node(n) => Node::Tree(Tree::new("_inter", vec![n])),
                });
                return Some(TVal::Node(Node::Tree(Tree::new("_iambig", children.collect()))));
            }
            return data.into_iter().next();
        }
        // collapse the ambiguities of the children
        let mut collapsed = Vec::new();
        for child in data {
            match child.into_node() {
                Node::Tree(t) if t.data == "_ambig" => collapsed.extend(t.children),
                other => collapsed.push(other),
            }
        }
        match collapsed.len() {
            0 => None,
            1 => Some(TVal::Node(collapsed.into_iter().next().unwrap())),
            _ => Some(TVal::Node(Node::Tree(Tree::new("_ambig", collapsed)))),
        }
    }

    fn transform_packed(&mut self, forest: &Forest, node: usize, data: Vec<TVal>) -> Option<TVal> {
        if self.check_cycle(VNode::Packed(node)) {
            return None;
        }
        let p = &forest.packed[node];
        if self.resolve_ambiguity && self.successful_visits.contains(&VNode::Sym(p.parent)) {
            return None;
        }
        if self.use_cache {
            if let Some(cached) = self.cache.get(&node) {
                return cached.clone();
            }
        }
        let mut it = data.into_iter();
        let left = if p.left.is_some() { it.next() } else { None };
        let right = it.next();
        let mut children = Vec::new();
        if let Some(l) = left {
            let left_is_intermediate = matches!(p.left, Some(VNode::Sym(s)) if forest.is_intermediate(s));
            match l {
                TVal::List(list) if left_is_intermediate => children.extend(list),
                other => children.push(other.into_node()),
            }
        }
        if let Some(r) = right {
            match r {
                TVal::List(list) => children.extend(list),
                TVal::Node(n) => children.push(n),
            }
        }
        let transformed = if forest.is_intermediate(p.parent) {
            TVal::List(children)
        } else {
            TVal::Node(self.callbacks.build(&self.rules[p.rule], children))
        };
        if self.use_cache {
            self.cache.insert(node, Some(transformed.clone()));
        }
        Some(transformed)
    }
}

impl ForestVisitor for ForestToParseTree<'_> {
    fn visit_token(&mut self, forest: &mut Forest, token: usize) {
        let parent = *self.node_stack.last().expect("node stack");
        let t = forest.tokens[token].token.clone();
        self.data.get_mut(&parent).expect("parent data").push(TVal::Node(Node::Token(t)));
    }

    fn visit_symbol_in(&mut self, forest: &mut Forest, node: usize) -> Option<Vec<VNode>> {
        self.node_stack.push(Some(VNode::Sym(node)));
        self.data.insert(Some(VNode::Sym(node)), Vec::new());
        if self.on_cycle_retreat {
            return None;
        }
        Some(forest.sorted_children(node, self.rules).into_iter().map(VNode::Packed).collect())
    }

    fn visit_symbol_out(&mut self, forest: &mut Forest, node: usize) {
        self.node_stack.pop();
        let data = self.data.get_mut(&Some(VNode::Sym(node))).map(std::mem::take).unwrap_or_default();
        let transformed = self.transform_symbol(forest, node, data);
        self.out_helper(VNode::Sym(node), transformed);
    }

    fn visit_packed_in(&mut self, forest: &mut Forest, node: usize) -> Option<Vec<VNode>> {
        self.on_cycle_retreat = false;
        self.node_stack.push(Some(VNode::Packed(node)));
        self.data.insert(Some(VNode::Packed(node)), Vec::new());
        let p = &forest.packed[node];
        let to_visit: Vec<VNode> = p.left.into_iter().chain(p.right).collect();
        if (!self.resolve_ambiguity || !self.successful_visits.contains(&VNode::Sym(p.parent)))
            && (!self.use_cache || !self.cache.contains_key(&node))
        {
            return Some(to_visit);
        }
        None
    }

    fn visit_packed_out(&mut self, forest: &mut Forest, node: usize) {
        self.node_stack.pop();
        let data = self
            .data
            .get_mut(&Some(VNode::Packed(node)))
            .map(std::mem::take)
            .unwrap_or_default();
        let transformed = self.transform_packed(forest, node, data);
        self.out_helper(VNode::Packed(node), transformed);
        if !self.on_cycle_retreat {
            self.successful_visits.insert(VNode::Sym(forest.packed[node].parent));
        }
    }

    fn on_cycle(&mut self, node: VNode) {
        self.cycle_node = Some(node);
        self.on_cycle_retreat = true;
    }
}

// ---------------------------------------------------------------------------------------------------------------
//  The parser
// ---------------------------------------------------------------------------------------------------------------

/// The dynamic lexer: matches the terminals with regular expressions while parsing (Lark's `xearley`).
struct DynamicLexer {
    regexps: HashMap<String, Regex>,
    ignore: Vec<String>,
    complete_lex: bool,
}

pub(crate) struct EarleyParser {
    rules: Vec<Arc<Rule>>,
    callbacks: Arc<Callbacks>,
    predictions: HashMap<Symbol, Vec<usize>>,
    resolve_ambiguity: bool,
    has_priorities: bool,
    terminals_by_name: TerminalsByName,
    dynamic: Option<DynamicLexer>,
}

/// The input of the Earley parser.
pub(crate) enum EarleyInput<'a> {
    /// Tokens from the basic lexer.
    Tokens(TokenStream<'a>),
    /// The text, for the dynamic lexer.
    Text(&'a str),
}

struct State<'p> {
    parser: &'p EarleyParser,
    columns: Vec<Column>,
    forest: Forest,
}

impl EarleyParser {
    /// `dynamic` is `None` for the basic lexer, and `Some(complete_lex)` for the dynamic lexers.
    pub fn new(
        rules: Vec<Arc<Rule>>,
        start: &[String],
        callbacks: Arc<Callbacks>,
        terminals: &[crate::lexer::TerminalDef],
        terminals_by_name: TerminalsByName,
        ignore: &[String],
        regex_flags: Flags,
        dynamic: Option<bool>,
        resolve_ambiguity: bool,
    ) -> Result<EarleyParser> {
        let analysis = GrammarAnalyzer::new(&rules, start, false, false)?;
        let mut predictions = HashMap::new();
        let mut has_priorities = false;
        for rule in &rules {
            if !predictions.contains_key(&rule.origin) {
                let ptrs = expand_rule(&rule.origin, &analysis.rules, &analysis.rules_by_origin);
                predictions.insert(rule.origin.clone(), ptrs.into_iter().map(|p| p.rule).collect::<Vec<_>>());
            }
            if rule.options.priority.is_some() {
                has_priorities = true;
            }
        }
        // Terminal priorities are ignored with the basic lexer
        if dynamic.is_some() && !has_priorities && terminals.iter().any(|t| t.priority != 0) {
            has_priorities = true;
        }

        let dynamic = match dynamic {
            None => None,
            Some(complete_lex) => {
                let mut regexps = HashMap::new();
                for t in terminals {
                    let regexp = t.pattern.to_regexp();
                    let width = crate::pyre::width(&regexp, Flags::default())
                        .map_err(|_| Error::grammar(format!("Bad regexp in token {}: {}", t.name, regexp)))?;
                    if width.0 == 0 {
                        return Err(Error::grammar(format!("Dynamic Earley doesn't allow zero-width regexps ({:?})", t)));
                    }
                    let re = Regex::new(&regexp, regex_flags)
                        .map_err(|e| Error::grammar(format!("Bad regexp in token {}: {} ({})", t.name, regexp, e)))?;
                    regexps.insert(t.name.clone(), re);
                }
                Some(DynamicLexer {
                    regexps,
                    ignore: ignore.to_vec(),
                    complete_lex,
                })
            }
        };

        Ok(EarleyParser {
            rules,
            callbacks,
            predictions,
            resolve_ambiguity,
            has_priorities,
            terminals_by_name,
            dynamic,
        })
    }

    fn expect(&self, k: &ItemKey) -> Option<&Symbol> {
        self.rules[k.rule as usize].expansion.get(k.ptr as usize)
    }

    fn is_complete(&self, k: &ItemKey) -> bool {
        k.ptr as usize == self.rules[k.rule as usize].expansion.len()
    }

    fn s(&self, k: &ItemKey) -> SKey {
        if self.is_complete(k) {
            SKey::Sym(self.rules[k.rule as usize].origin.clone())
        } else {
            SKey::Lr0(k.rule, k.ptr)
        }
    }

    fn expects_terminal(&self, k: &ItemKey) -> bool {
        self.expect(k).is_some_and(|s| s.is_term)
    }

    fn describe(&self, k: &ItemKey) -> String {
        let rule = &self.rules[k.rule as usize];
        let names: Vec<&str> = rule.expansion.iter().map(|s| s.name()).collect();
        format!(
            "{} ::= {}* {}",
            rule.origin.name,
            names[..k.ptr as usize].join(" "),
            names[k.ptr as usize..].join(" ")
        )
    }

    fn error_state(&self, to_scan: &IndexMap<ItemKey, Option<usize>>) -> ErrorState {
        ErrorState::Expected(to_scan.keys().map(|k| self.describe(k)).collect())
    }

    /// Parses the input from the start rule and returns the tree.
    pub fn parse(&self, input: EarleyInput<'_>, start: &str) -> Result<Node> {
        let start_symbol = Symbol::rule(start);
        let mut st = State {
            parser: self,
            columns: vec![Column::default()],
            forest: Forest::default(),
        };
        let mut to_scan: IndexMap<ItemKey, Option<usize>> = IndexMap::new(); // The scan buffer. 'Q' in E.Scott's paper.

        // Predict for the start_symbol.
        for &rule in self.predictions.get(&start_symbol).map(|v| v.as_slice()).unwrap_or(&[]) {
            let item = ItemKey {
                rule: rule as u32,
                ptr: 0,
                start: 0,
            };
            if self.expects_terminal(&item) {
                to_scan.entry(item).or_insert(None);
            } else {
                st.add_to_column(0, item, None);
            }
        }

        let to_scan = match input {
            EarleyInput::Tokens(stream) => st.parse_tokens(stream, to_scan)?,
            EarleyInput::Text(text) => st.parse_text(text, to_scan, &start_symbol)?,
        };

        // If the parse was successful, the start symbol should have been completed in the last step of the Earley
        // cycle, and will be in this column. Find the item for the start_symbol, which is the root of the SPPF tree.
        let last = st.columns.last().unwrap();
        let mut solutions: Vec<usize> = Vec::new();
        for (k, node) in &last.items {
            if let Some(n) = node {
                if self.is_complete(k) && k.start == 0 && self.rules[k.rule as usize].origin == start_symbol && !solutions.contains(n) {
                    solutions.push(*n);
                }
            }
        }
        if solutions.is_empty() {
            let expected: BTreeSet<String> = to_scan.keys().filter_map(|k| self.expect(k).map(|s| s.name.to_string())).collect();
            return Err(UnexpectedEof {
                expected,
                state: Some(self.error_state(&to_scan)),
                terminals_by_name: Some(self.terminals_by_name.clone()),
            }
            .into());
        }
        if solutions.len() > 1 {
            return Err(Error::Parse(
                "Earley should not generate multiple start symbol items! Please report this bug.".into(),
            ));
        }
        let solution = solutions[0];

        // Perform our SPPF -> AST conversion
        let mut forest = st.forest;
        if self.has_priorities {
            visit_forest(&mut ForestSumVisitor { rules: &self.rules }, &mut forest, VNode::Sym(solution));
        }
        // The cache is disabled when ambiguity='resolve', to prevent a tree construction bug (Lark issue #1283)
        let transformer = ForestToParseTree {
            rules: &self.rules,
            callbacks: &self.callbacks,
            resolve_ambiguity: self.resolve_ambiguity,
            use_cache: !self.resolve_ambiguity,
            cache: HashMap::new(),
            on_cycle_retreat: false,
            cycle_node: None,
            successful_visits: HashSet::new(),
            data: HashMap::new(),
            node_stack: Vec::new(),
        };
        Ok(transformer.transform(&mut forest, solution).unwrap_or(Node::Missing))
    }
}

impl State<'_> {
    fn add_to_column(&mut self, i: usize, item: ItemKey, node: Option<usize>) -> bool {
        let column = &mut self.columns[i];
        if column.items.contains_key(&item) {
            return false;
        }
        column.items.insert(item, node);
        if let Some(e) = self.parser.expect(&item) {
            column.by_expect.entry(e.clone()).or_default().push(item);
        }
        true
    }

    fn get_node(&mut self, cache: &mut NodeCache, s: SKey, start: usize, end: usize) -> usize {
        let label = (s, start, end);
        if let Some(&n) = cache.get(&label) {
            return n;
        }
        let id = self.forest.symbols.len();
        self.forest.symbols.push(SymbolNode {
            s: label.0.clone(),
            children: IndexMap::new(),
            priority: f64::NEG_INFINITY,
        });
        cache.insert(label, id);
        id
    }

    fn advance(k: &ItemKey) -> ItemKey {
        ItemKey {
            rule: k.rule,
            ptr: k.ptr + 1,
            start: k.start,
        }
    }

    /// Adds a new item to the scan buffer, or to the column and the queue of items to process.
    fn add_item(
        &mut self,
        i: usize,
        item: ItemKey,
        node: Option<usize>,
        to_scan: &mut IndexMap<ItemKey, Option<usize>>,
        items: &mut Vec<ItemKey>,
    ) {
        if self.parser.expects_terminal(&item) {
            to_scan.entry(item).or_insert(node);
        } else if self.add_to_column(i, item, node) {
            items.push(item);
        }
    }

    /// The core Earley Predictor and Completer.
    fn predict_and_complete(&mut self, i: usize, to_scan: &mut IndexMap<ItemKey, Option<usize>>, cache: &mut NodeCache) {
        let p = self.parser;
        // Held Completions (H in E.Scotts paper).
        let mut held_completions: HashMap<Symbol, usize> = HashMap::new();
        // R (items) = Ei (column.items)
        let mut items: Vec<ItemKey> = self.columns[i].items.keys().copied().collect();
        while let Some(item) = items.pop() {
            let rule = &p.rules[item.rule as usize];
            if p.is_complete(&item) {
                // The Earley completer
                let node = match self.columns[i].items.get(&item).copied().flatten() {
                    Some(n) => n,
                    None => {
                        let n = self.get_node(cache, p.s(&item), item.start as usize, i);
                        self.forest.add_family(n, item.rule as usize, None, None);
                        self.columns[i].items.insert(item, Some(n));
                        n
                    }
                };

                // Empty has 0 length. If we complete an empty symbol in a particular parse step, we need to be able
                // to use that same empty symbol to complete any predictions that result, that themselves require
                // empty. Avoids infinite recursion on empty symbols.
                if item.start as usize == i {
                    held_completions.insert(rule.origin.clone(), node);
                }

                let start = item.start as usize;
                let originators: Vec<(ItemKey, Option<usize>)> = self.columns[start]
                    .by_expect
                    .get(&rule.origin)
                    .map(|v| v.iter().map(|k| (*k, self.columns[start].items[k])).collect())
                    .unwrap_or_default();
                for (originator, o_node) in originators {
                    let new_item = Self::advance(&originator);
                    let n = self.get_node(cache, p.s(&new_item), originator.start as usize, i);
                    self.forest
                        .add_family(n, new_item.rule as usize, o_node.map(VNode::Sym), Some(VNode::Sym(node)));
                    self.add_item(i, new_item, Some(n), to_scan, &mut items);
                }
            } else if let Some(expect) = p.expect(&item).filter(|e| !e.is_term) {
                // The Earley predictor
                let mut new_items: Vec<(ItemKey, Option<usize>)> = Vec::new();
                for &r in p.predictions.get(expect).map(|v| v.as_slice()).unwrap_or(&[]) {
                    new_items.push((
                        ItemKey {
                            rule: r as u32,
                            ptr: 0,
                            start: i as u32,
                        },
                        None,
                    ));
                }
                // Process any held completions (H).
                if let Some(&held) = held_completions.get(expect) {
                    let new_item = Self::advance(&item);
                    let item_node = self.columns[i].items.get(&item).copied().flatten();
                    let n = self.get_node(cache, p.s(&new_item), item.start as usize, i);
                    self.forest
                        .add_family(n, new_item.rule as usize, item_node.map(VNode::Sym), Some(VNode::Sym(held)));
                    new_items.push((new_item, Some(n)));
                }
                for (new_item, node) in new_items {
                    self.add_item(i, new_item, node, to_scan, &mut items);
                }
            }
        }
    }

    /// The main Earley loop over the tokens of the basic lexer.
    fn parse_tokens(
        &mut self,
        stream: TokenStream<'_>,
        mut to_scan: IndexMap<ItemKey, Option<usize>>,
    ) -> Result<IndexMap<ItemKey, Option<usize>>> {
        let p = self.parser;
        let mut i = 0;
        let mut cache = NodeCache::new();
        for token in stream {
            let token = token?;
            self.predict_and_complete(i, &mut to_scan, &mut cache);

            // The core Earley Scanner.
            let mut next_to_scan: IndexMap<ItemKey, Option<usize>> = IndexMap::new();
            self.columns.push(Column::default());
            cache = NodeCache::new();
            // The terminal priorities were used up by the basic lexer already
            let token_node = self.forest.tokens.len();
            self.forest.tokens.push(TokenNode {
                token: token.clone(),
                priority: 0.0,
            });

            let mut matched = false;
            for (item, node) in to_scan.iter() {
                if p.expect(item).is_some_and(|e| *e.name == token.kind) {
                    matched = true;
                    let new_item = Self::advance(item);
                    let n = self.get_node(&mut cache, p.s(&new_item), new_item.start as usize, i + 1);
                    self.forest
                        .add_family(n, item.rule as usize, node.map(VNode::Sym), Some(VNode::Tok(token_node)));
                    if p.expects_terminal(&new_item) {
                        next_to_scan.entry(new_item).or_insert(Some(n));
                    } else {
                        self.add_to_column(i + 1, new_item, Some(n));
                    }
                }
            }

            if !matched {
                let expect: BTreeSet<String> = to_scan.keys().filter_map(|k| p.expect(k).map(|s| s.name.to_string())).collect();
                let mut e = UnexpectedToken::new(token, expect, Some(p.error_state(&to_scan)), Some(p.terminals_by_name.clone()));
                e.considered_rules = Some(to_scan.keys().map(|k| p.describe(k)).collect());
                return Err(e.into());
            }

            to_scan = next_to_scan;
            i += 1;
        }
        self.predict_and_complete(i, &mut to_scan, &mut cache);
        Ok(to_scan)
    }

    /// The main Earley loop of the dynamic lexer, over the characters of the text.
    fn parse_text(
        &mut self,
        text: &str,
        mut to_scan: IndexMap<ItemKey, Option<usize>>,
        start_symbol: &Symbol,
    ) -> Result<IndexMap<ItemKey, Option<usize>>> {
        let p = self.parser;
        let dynamic = p.dynamic.as_ref().expect("dynamic lexer");
        // Byte offsets of each character, and the character index of each byte offset
        let mut byte_of: Vec<usize> = text.char_indices().map(|(b, _)| b).collect();
        byte_of.push(text.len());
        let mut char_of: HashMap<usize, usize> = HashMap::with_capacity(byte_of.len());
        for (c, &b) in byte_of.iter().enumerate() {
            char_of.insert(b, c);
        }

        let mut delayed: HashMap<usize, Vec<(ItemKey, Option<usize>, Option<Token>)>> = HashMap::new();
        let (mut text_line, mut text_column) = (1usize, 1usize);
        let mut cache = NodeCache::new();
        let chars: Vec<char> = text.chars().collect();

        let match_term = |name: &str, pos: usize| -> Result<Option<usize>> {
            let re = dynamic
                .regexps
                .get(name)
                .ok_or_else(|| Error::Lex(format!("No terminal named {}", name)))?;
            Ok(re.match_at(text, pos))
        };

        for i in 0..chars.len() {
            self.predict_and_complete(i, &mut to_scan, &mut cache);

            // The core Earley Scanner.
            cache = NodeCache::new();
            let pos = byte_of[i];

            // 1) Loop the expectations and ask the lexer to match. Since regexp is forward looking on the input
            // stream, and we only want to process tokens when we hit the point in the stream at which they complete,
            // we push all tokens into a buffer (delayed_matches), to be held possibly for a later parse step when we
            // reach the point in the input stream at which they complete.
            for (item, node) in to_scan.iter() {
                let name = p.expect(item).map(|s| s.name.to_string()).unwrap_or_default();
                if let Some(end) = match_term(&name, pos)? {
                    let value = &text[pos..end];
                    let t = Token {
                        kind: name.clone(),
                        value: value.to_string(),
                        start_pos: Some(pos),
                        line: Some(text_line),
                        column: Some(text_column),
                        ..Default::default()
                    };
                    delayed.entry(char_of[&end]).or_default().push((*item, *node, Some(t)));

                    if dynamic.complete_lex {
                        let re = &dynamic.regexps[&name];
                        let n = value.chars().count();
                        for j in 1..n {
                            let cut = value.char_indices().nth(n - j).map(|(b, _)| b).unwrap_or(value.len());
                            let sub = &value[..cut];
                            if let Some(e) = re.match_at(sub, 0) {
                                let t = Token {
                                    kind: name.clone(),
                                    value: sub[..e].to_string(),
                                    start_pos: Some(pos),
                                    line: Some(text_line),
                                    column: Some(text_column),
                                    ..Default::default()
                                };
                                delayed
                                    .entry(i + sub[..e].chars().count())
                                    .or_default()
                                    .push((*item, *node, Some(t)));
                            }
                        }
                    }
                }
            }

            // 3) Process any ignores. This is typically used for e.g. whitespace. We carry over any unmatched items
            // from the to_scan buffer to be matched again after the ignore. This should allow us to use ignored
            // symbols in non-terminals to implement e.g. mandatory spacing.
            for x in &dynamic.ignore {
                if let Some(end) = match_term(x, pos)? {
                    let end = char_of[&end];
                    let entry = delayed.entry(end).or_default();
                    // Carry over any items still in the scan buffer, to past the end of the ignored items.
                    entry.extend(to_scan.iter().map(|(k, n)| (*k, *n, None)));
                    // If we're ignoring up to the end of the file, carry over the start symbol if it already completed.
                    for (k, n) in &self.columns[i].items {
                        if p.is_complete(k) && p.rules[k.rule as usize].origin == *start_symbol {
                            entry.push((*k, *n, None));
                        }
                    }
                }
            }

            let mut next_to_scan: IndexMap<ItemKey, Option<usize>> = IndexMap::new();
            self.columns.push(Column::default());

            // 4) Process Tokens from delayed_matches. This is the core of the Earley scanner. Create an SPPF node for
            // each Token, and create the symbol node in the SPPF tree. Advance the item that completed, and add the
            // resulting new item to either the Earley set (for processing by the completer/predictor) or the to_scan
            // buffer for the next parse step.
            let mut added = false;
            for (item, node, token) in delayed.remove(&(i + 1)).unwrap_or_default() {
                let (new_item, new_node) = match token {
                    Some(mut token) => {
                        token.end_line = Some(text_line);
                        token.end_column = Some(text_column + 1);
                        token.end_pos = Some(byte_of[i + 1]);
                        let new_item = Self::advance(&item);
                        let priority = p.terminals_by_name.get(&token.kind).map(|t| t.priority as f64).unwrap_or(0.0);
                        let token_node = self.forest.tokens.len();
                        self.forest.tokens.push(TokenNode { token, priority });
                        let n = self.get_node(&mut cache, p.s(&new_item), new_item.start as usize, i + 1);
                        self.forest
                            .add_family(n, item.rule as usize, node.map(VNode::Sym), Some(VNode::Tok(token_node)));
                        (new_item, Some(n))
                    }
                    None => (item, node),
                };
                added = true;
                if p.expects_terminal(&new_item) {
                    next_to_scan.entry(new_item).or_insert(new_node);
                } else {
                    self.add_to_column(i + 1, new_item, new_node);
                }
            }

            if !added && delayed.is_empty() {
                let allowed: BTreeSet<String> = to_scan.keys().filter_map(|k| p.expect(k).map(|s| s.name.to_string())).collect();
                let mut e = UnexpectedCharacters::new(
                    text,
                    pos,
                    text_line,
                    text_column,
                    Some(allowed.clone()),
                    Some(p.error_state(&to_scan)),
                    None,
                    Some(p.terminals_by_name.clone()),
                );
                e.considered_tokens = Some(allowed);
                let mut considered: Vec<&ItemKey> = to_scan.keys().collect();
                considered.sort_by(|a, b| p.rules[a.rule as usize].origin.name.cmp(&p.rules[b.rule as usize].origin.name));
                e.considered_rules = Some(considered.into_iter().map(|k| p.describe(k)).collect());
                return Err(e.into());
            }

            to_scan = next_to_scan;
            if chars[i] == '\n' {
                text_line += 1;
                text_column = 1;
            } else {
                text_column += 1;
            }
        }

        self.predict_and_complete(chars.len(), &mut to_scan, &mut cache);
        Ok(to_scan)
    }
}
