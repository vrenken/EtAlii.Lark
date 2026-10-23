//! Builds the parse tree from the rules as they are matched (Lark's `parse_tree_builder`).

use std::collections::HashMap;
use std::sync::Arc;

use crate::grammar::{Rule, Symbol};
use crate::token::Token;
use crate::tree::{Meta, Node, Tree};

/// Filters the children of a rule: drops filtered-out tokens, inlines `_rules`, and inserts the placeholders of
/// `maybe_placeholders`.
#[derive(Clone, Debug)]
struct ChildFilter {
    /// (index, inline it, placeholders to insert before it)
    to_include: Vec<(usize, bool, usize)>,
    append_none: usize,
}

impl ChildFilter {
    fn apply(&self, children: Vec<Node>) -> Vec<Node> {
        let mut children: Vec<Option<Node>> = children.into_iter().map(Some).collect();
        let mut filtered = Vec::with_capacity(children.len());
        for &(i, to_expand, add_none) in &self.to_include {
            filtered.extend(std::iter::repeat_n(Node::Missing, add_none));
            let child = children[i].take().unwrap_or(Node::Missing);
            if to_expand {
                match child {
                    Node::Tree(t) => filtered.extend(t.children),
                    other => filtered.push(other),
                }
            } else {
                filtered.push(child);
            }
        }
        filtered.extend(std::iter::repeat_n(Node::Missing, self.append_none));
        filtered
    }
}

fn should_expand(sym: &Symbol) -> bool {
    !sym.is_term && sym.name.starts_with('_')
}

fn create_child_filter(expansion: &[Symbol], keep_all_tokens: bool, empty_indices: Option<&[bool]>) -> Option<ChildFilter> {
    let empty: Vec<usize> = match empty_indices {
        Some(ei) if !ei.is_empty() => {
            // How many placeholders to insert at each index
            let s: String = ei.iter().map(|&b| if b { '1' } else { '0' }).collect();
            s.split('0').map(|ones| ones.len()).collect()
        }
        _ => vec![0; expansion.len() + 1],
    };
    let mut to_include = Vec::new();
    let mut nones = 0;
    for (i, sym) in expansion.iter().enumerate() {
        nones += empty[i];
        if keep_all_tokens || !(sym.is_term && sym.filter_out) {
            to_include.push((i, should_expand(sym), nones));
            nones = 0;
        }
    }
    nones += empty[expansion.len()];
    let has_empty = matches!(empty_indices, Some(ei) if !ei.is_empty());
    if has_empty || to_include.len() < expansion.len() || to_include.iter().any(|x| x.1) {
        Some(ChildFilter {
            to_include,
            append_none: nones,
        })
    } else {
        None
    }
}

/// How to build the tree of one rule.
#[derive(Clone, Debug)]
pub(crate) struct RuleBuilder {
    /// The tree data: the alias, the template, or the rule name.
    pub name: String,
    expand_single_child: bool,
    filter: Option<ChildFilter>,
    propagate_positions: bool,
    /// For ambiguous parses: the children that may be `_ambig` trees to lift into this one.
    ambiguous_expander: Option<Vec<usize>>,
    ambiguous_intermediate: bool,
}

/// The tree builders of all rules (Lark's `ParseTreeBuilder.create_callback`).
#[derive(Clone, Debug, Default)]
pub(crate) struct Callbacks {
    pub rules: HashMap<Rule, RuleBuilder>,
}

impl Callbacks {
    pub fn new(rules: &[Arc<Rule>], propagate_positions: bool, ambiguous: bool, maybe_placeholders: bool) -> Callbacks {
        let mut map = HashMap::new();
        for rule in rules {
            let options = &rule.options;
            let keep_all_tokens = options.keep_all_tokens;
            let empty = if maybe_placeholders {
                Some(options.empty_indices.as_slice())
            } else {
                None
            };
            let ambiguous_expander = if ambiguous {
                let to_expand: Vec<usize> = rule
                    .expansion
                    .iter()
                    .enumerate()
                    .filter(|(_, sym)| keep_all_tokens || (!(sym.is_term && sym.filter_out) && should_expand(sym)))
                    .map(|(i, _)| i)
                    .collect();
                if to_expand.is_empty() {
                    None
                } else {
                    Some(to_expand)
                }
            } else {
                None
            };
            let builder = RuleBuilder {
                name: rule
                    .alias
                    .clone()
                    .or_else(|| options.template_source.clone())
                    .unwrap_or_else(|| rule.origin.name.to_string()),
                expand_single_child: options.expand1 && rule.alias.is_none(),
                filter: create_child_filter(&rule.expansion, keep_all_tokens, empty),
                propagate_positions,
                ambiguous_expander,
                ambiguous_intermediate: ambiguous,
            };
            map.insert((**rule).clone(), builder);
        }
        Callbacks { rules: map }
    }

    pub fn build(&self, rule: &Rule, children: Vec<Node>) -> Node {
        self.rules[rule].build(children)
    }
}

impl RuleBuilder {
    pub fn build(&self, children: Vec<Node>) -> Node {
        if self.ambiguous_intermediate {
            if let Some(collapsed) = collapse_iambig(&children).filter(|c| !c.is_empty()) {
                let processed = collapsed.into_iter().map(|c| self.build_expander(c)).collect();
                return Node::Tree(Tree::new("_ambig", processed));
            }
        }
        self.build_expander(children)
    }

    fn build_expander(&self, mut children: Vec<Node>) -> Node {
        if let Some(to_expand) = &self.ambiguous_expander {
            let mut ambiguous = Vec::new();
            for (i, child) in children.iter_mut().enumerate() {
                if let Node::Tree(t) = child {
                    if t.data == "_ambig" {
                        if to_expand.contains(&i) {
                            ambiguous.push(i);
                        }
                        t.expand_kids_by_data(&["_ambig"]);
                    }
                }
            }
            if !ambiguous.is_empty() {
                let options: Vec<Vec<Node>> = children
                    .into_iter()
                    .enumerate()
                    .map(|(i, c)| match c {
                        Node::Tree(t) if ambiguous.contains(&i) => t.children,
                        other => vec![other],
                    })
                    .collect();
                let mut products: Vec<Vec<Node>> = vec![Vec::new()];
                for opts in &options {
                    let mut next = Vec::with_capacity(products.len() * opts.len());
                    for p in &products {
                        for o in opts {
                            let mut v = p.clone();
                            v.push(o.clone());
                            next.push(v);
                        }
                    }
                    products = next;
                }
                return Node::Tree(Tree::new("_ambig", products.into_iter().map(|p| self.build_positions(p)).collect()));
            }
        }
        self.build_positions(children)
    }

    fn build_positions(&self, children: Vec<Node>) -> Node {
        if !self.propagate_positions {
            return self.build_filtered(children);
        }
        let first = first_position(children.iter());
        let last = first_position(children.iter().rev());
        let mut res = self.build_filtered(children);
        if let Node::Tree(tree) = &mut res {
            let meta = tree.meta_mut();
            if let Some(p) = first {
                if meta.line.is_none() {
                    meta.line = p.line;
                    meta.column = p.column;
                    meta.start_pos = p.start_pos;
                    meta.empty = false;
                }
                meta.container_line = p.line;
                meta.container_column = p.column;
                meta.container_start_pos = p.start_pos;
            }
            if let Some(p) = last {
                if meta.end_line.is_none() {
                    meta.end_line = p.end_line;
                    meta.end_column = p.end_column;
                    meta.end_pos = p.end_pos;
                    meta.empty = false;
                }
                meta.container_end_line = p.end_line;
                meta.container_end_column = p.end_column;
                meta.container_end_pos = p.end_pos;
            }
        }
        res
    }

    fn build_filtered(&self, children: Vec<Node>) -> Node {
        let children = match &self.filter {
            Some(f) => f.apply(children),
            None => children,
        };
        if self.expand_single_child && children.len() == 1 {
            return children.into_iter().next().unwrap();
        }
        Node::Tree(Tree::new(self.name.clone(), children))
    }
}

/// The position of a child, preferring the container position of trees.
struct Position {
    line: Option<usize>,
    column: Option<usize>,
    start_pos: Option<usize>,
    end_line: Option<usize>,
    end_column: Option<usize>,
    end_pos: Option<usize>,
}

fn meta_position(m: &Meta) -> Position {
    let has_start = m.container_start_pos.is_some() || m.container_line.is_some();
    let has_end = m.container_end_pos.is_some() || m.container_end_line.is_some();
    Position {
        line: if has_start { m.container_line } else { m.line },
        column: if has_start { m.container_column } else { m.column },
        start_pos: if has_start { m.container_start_pos } else { m.start_pos },
        end_line: if has_end { m.container_end_line } else { m.end_line },
        end_column: if has_end { m.container_end_column } else { m.end_column },
        end_pos: if has_end { m.container_end_pos } else { m.end_pos },
    }
}

fn token_position(t: &Token) -> Position {
    Position {
        line: t.line,
        column: t.column,
        start_pos: t.start_pos,
        end_line: t.end_line,
        end_column: t.end_column,
        end_pos: t.end_pos,
    }
}

fn first_position<'a>(children: impl Iterator<Item = &'a Node>) -> Option<Position> {
    for c in children {
        match c {
            Node::Tree(t) => {
                if !t.meta().empty {
                    return Some(meta_position(t.meta()));
                }
            }
            Node::Token(t) => return Some(token_position(t)),
            Node::Missing => {}
        }
    }
    None
}

/// Lifts the derivations of an ambiguous intermediate node (`_iambig`) to the parent rule.
fn collapse_iambig(children: &[Node]) -> Option<Vec<Vec<Node>>> {
    let first = match children.first() {
        Some(Node::Tree(t)) if t.data == "_iambig" => t,
        _ => return None,
    };
    let mut result = Vec::new();
    for grandchild in &first.children {
        let gc_children = match grandchild {
            Node::Tree(t) => &t.children,
            _ => continue,
        };
        match collapse_iambig(gc_children) {
            Some(collapsed) if !collapsed.is_empty() => {
                for mut c in collapsed {
                    c.extend(children[1..].iter().cloned());
                    result.push(c);
                }
            }
            _ => {
                let mut c = gc_children.clone();
                c.extend(children[1..].iter().cloned());
                result.push(c);
            }
        }
    }
    Some(result)
}
