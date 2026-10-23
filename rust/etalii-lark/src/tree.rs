use std::collections::HashSet;
use std::fmt;

use crate::token::Token;

/// The position of a tree in the text, set when `propagate_positions` is enabled (Lark's `Meta`).
///
/// Offsets are in bytes and columns in characters, as for [`Token`].
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Meta {
    /// True until a position is set.
    pub empty: bool,
    pub line: Option<usize>,
    pub column: Option<usize>,
    pub start_pos: Option<usize>,
    pub end_line: Option<usize>,
    pub end_column: Option<usize>,
    pub end_pos: Option<usize>,
    /// The position of the tree including the tokens that were filtered out of it.
    pub container_line: Option<usize>,
    pub container_column: Option<usize>,
    pub container_start_pos: Option<usize>,
    pub container_end_line: Option<usize>,
    pub container_end_column: Option<usize>,
    pub container_end_pos: Option<usize>,
    /// Used by the reconstructor: the expansion that matched, as rule and terminal names.
    pub orig_expansion: Option<Vec<crate::grammar::Symbol>>,
    /// Used by the reconstructor.
    pub match_tree: bool,
}

impl Meta {
    /// A meta without a position.
    pub fn new() -> Meta {
        Meta {
            empty: true,
            ..Default::default()
        }
    }
}

/// A child of a [`Tree`]: a subtree, a token, or the placeholder that `maybe_placeholders` puts in place of a
/// missing `[optional]` item (Lark's `None`).
#[derive(Clone, PartialEq, Eq, Hash)]
pub enum Node {
    Tree(Tree),
    Token(Token),
    /// A `[optional]` item that didn't match (Python Lark's `None`).
    Missing,
}

impl Node {
    /// The subtree, if this is a tree.
    pub fn as_tree(&self) -> Option<&Tree> {
        match self {
            Node::Tree(t) => Some(t),
            _ => None,
        }
    }

    /// The subtree, if this is a tree.
    pub fn as_tree_mut(&mut self) -> Option<&mut Tree> {
        match self {
            Node::Tree(t) => Some(t),
            _ => None,
        }
    }

    /// The token, if this is a token.
    pub fn as_token(&self) -> Option<&Token> {
        match self {
            Node::Token(t) => Some(t),
            _ => None,
        }
    }

    /// Whether this is the placeholder of a missing optional item.
    pub fn is_missing(&self) -> bool {
        matches!(self, Node::Missing)
    }

    /// Consumes the node, returning the tree if it is one.
    pub fn into_tree(self) -> Option<Tree> {
        match self {
            Node::Tree(t) => Some(t),
            _ => None,
        }
    }

    /// Consumes the node, returning the token if it is one.
    pub fn into_token(self) -> Option<Token> {
        match self {
            Node::Token(t) => Some(t),
            _ => None,
        }
    }
}

impl From<Tree> for Node {
    fn from(t: Tree) -> Node {
        Node::Tree(t)
    }
}

impl From<Token> for Node {
    fn from(t: Token) -> Node {
        Node::Token(t)
    }
}

impl From<Option<Node>> for Node {
    fn from(n: Option<Node>) -> Node {
        n.unwrap_or(Node::Missing)
    }
}

impl fmt::Debug for Node {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Node::Tree(t) => fmt::Debug::fmt(t, f),
            Node::Token(t) => fmt::Debug::fmt(t, f),
            Node::Missing => f.write_str("None"),
        }
    }
}

impl fmt::Display for Node {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Node::Tree(t) => fmt::Debug::fmt(t, f),
            Node::Token(t) => fmt::Display::fmt(t, f),
            Node::Missing => f.write_str("None"),
        }
    }
}

/// A parse tree: the name of a rule or alias, and its children (Lark's `Tree`).
///
/// Trees compare equal when their data and children are equal; positions are not compared.
#[derive(Clone)]
pub struct Tree {
    /// The name of the rule or alias.
    pub data: String,
    /// The matched subrules, tokens and placeholders.
    pub children: Vec<Node>,
    meta: Option<Box<Meta>>,
}

static EMPTY_META: std::sync::OnceLock<Meta> = std::sync::OnceLock::new();

impl Tree {
    /// Creates a tree.
    pub fn new(data: impl Into<String>, children: Vec<Node>) -> Tree {
        Tree {
            data: data.into(),
            children,
            meta: None,
        }
    }

    /// Creates a tree with a position.
    pub fn with_meta(data: impl Into<String>, children: Vec<Node>, meta: Meta) -> Tree {
        Tree {
            data: data.into(),
            children,
            meta: Some(Box::new(meta)),
        }
    }

    /// The position of the tree (empty unless `propagate_positions` was enabled).
    pub fn meta(&self) -> &Meta {
        match &self.meta {
            Some(m) => m,
            None => EMPTY_META.get_or_init(Meta::new),
        }
    }

    /// The position of the tree, for changing it.
    pub fn meta_mut(&mut self) -> &mut Meta {
        self.meta.get_or_insert_with(|| Box::new(Meta::new()))
    }

    /// Replaces the position of the tree.
    pub fn set_meta(&mut self, meta: Meta) {
        self.meta = Some(Box::new(meta));
    }

    /// An indented representation of the tree, great for debugging (Lark's `pretty`).
    pub fn pretty(&self) -> String {
        self.pretty_with("  ")
    }

    /// Like [`Tree::pretty`], with the given indentation.
    pub fn pretty_with(&self, indent: &str) -> String {
        let mut out = String::new();
        self.pretty_into(&mut out, 0, indent);
        out
    }

    fn pretty_into(&self, out: &mut String, level: usize, indent: &str) {
        out.push_str(&indent.repeat(level));
        out.push_str(&self.data);
        if self.children.len() == 1 && !matches!(self.children[0], Node::Tree(_)) {
            out.push('\t');
            out.push_str(&self.children[0].to_string());
            out.push('\n');
        } else {
            out.push('\n');
            for child in &self.children {
                match child {
                    Node::Tree(t) => t.pretty_into(out, level + 1, indent),
                    other => {
                        out.push_str(&indent.repeat(level + 1));
                        out.push_str(&other.to_string());
                        out.push('\n');
                    }
                }
            }
        }
    }

    /// All subtrees, bottom-up: every tree comes after its children (Lark's `iter_subtrees`).
    pub fn iter_subtrees(&self) -> Vec<&Tree> {
        let mut queue: Vec<&Tree> = vec![self];
        let mut seen: HashSet<*const Tree> = HashSet::new();
        let mut ordered = Vec::new();
        let mut i = 0;
        while i < queue.len() {
            let subtree = queue[i];
            i += 1;
            if !seen.insert(subtree as *const Tree) {
                continue;
            }
            ordered.push(subtree);
            for child in subtree.children.iter().rev() {
                if let Node::Tree(c) = child {
                    if !seen.contains(&(c as *const Tree)) {
                        queue.push(c);
                    }
                }
            }
        }
        ordered.reverse();
        ordered
    }

    /// All subtrees, top-down, in the order of [`Tree::pretty`] (Lark's `iter_subtrees_topdown`).
    pub fn iter_subtrees_topdown(&self) -> Vec<&Tree> {
        let mut stack: Vec<&Tree> = vec![self];
        let mut result = Vec::new();
        while let Some(t) = stack.pop() {
            result.push(t);
            for child in t.children.iter().rev() {
                if let Node::Tree(c) = child {
                    stack.push(c);
                }
            }
        }
        result
    }

    /// The subtrees for which the predicate holds, bottom-up (Lark's `find_pred`).
    pub fn find_pred(&self, pred: impl Fn(&Tree) -> bool) -> Vec<&Tree> {
        self.iter_subtrees().into_iter().filter(|t| pred(t)).collect()
    }

    /// The subtrees with the given data, bottom-up (Lark's `find_data`).
    pub fn find_data(&self, data: &str) -> Vec<&Tree> {
        self.find_pred(|t| t.data == data)
    }

    /// The tokens of the given type, in order (Lark's `find_token`).
    pub fn find_token(&self, kind: &str) -> Vec<&Token> {
        self.scan_values(|n| matches!(n, Node::Token(t) if t.kind == kind))
            .into_iter()
            .filter_map(Node::as_token)
            .collect()
    }

    /// All non-tree children, in order, for which the predicate holds (Lark's `scan_values`).
    pub fn scan_values(&self, pred: impl Fn(&Node) -> bool + Copy) -> Vec<&Node> {
        let mut result = Vec::new();
        self.scan_into(pred, &mut result);
        result
    }

    fn scan_into<'a>(&'a self, pred: impl Fn(&Node) -> bool + Copy, out: &mut Vec<&'a Node>) {
        for c in &self.children {
            match c {
                Node::Tree(t) => t.scan_into(pred, out),
                other => {
                    if pred(other) {
                        out.push(other)
                    }
                }
            }
        }
    }

    /// Inlines the children that are trees with any of the given data. Returns whether anything changed.
    pub fn expand_kids_by_data(&mut self, data: &[&str]) -> bool {
        let mut changed = false;
        let mut i = self.children.len();
        while i > 0 {
            i -= 1;
            if matches!(&self.children[i], Node::Tree(t) if data.contains(&t.data.as_str())) {
                let child = match self.children.remove(i) {
                    Node::Tree(t) => t,
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
}

impl PartialEq for Tree {
    fn eq(&self, other: &Tree) -> bool {
        self.data == other.data && self.children == other.children
    }
}

impl Eq for Tree {}

impl std::hash::Hash for Tree {
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        self.data.hash(state);
        self.children.hash(state);
    }
}

impl fmt::Debug for Tree {
    /// Lark's `repr`: `Tree('data', [children])`.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Tree({}, [", crate::utils::repr(&self.data))?;
        for (i, c) in self.children.iter().enumerate() {
            if i > 0 {
                f.write_str(", ")?;
            }
            fmt::Debug::fmt(c, f)?;
        }
        f.write_str("])")
    }
}

impl fmt::Display for Tree {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Debug::fmt(self, f)
    }
}

/// Builds a [`Tree`]: `tree!("add", child1, child2)`. Children are anything that converts into a [`Node`].
#[macro_export]
macro_rules! tree {
    ($data:expr $(, $child:expr)* $(,)?) => {
        $crate::Tree::new($data, vec![$($crate::Node::from($child)),*])
    };
}
