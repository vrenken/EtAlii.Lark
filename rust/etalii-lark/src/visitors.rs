//! Visitors and transformers (Lark's `Visitor` and `Transformer`).

use crate::error::{Error, Result, VisitError};
use crate::token::Token;
use crate::tree::{Meta, Node, Tree};

type BoxError = Box<dyn std::error::Error + Send + Sync>;

/// Turns a tree into a value, bottom-up: each tree is transformed after its children (Lark's `Transformer`).
/// A callback that fails stops the transformation with an [`Error::Visit`] that names the rule or terminal.
///
/// Trees can be arbitrarily deep; the transformation doesn't recurse, so it doesn't overflow the stack.
///
/// ```
/// use etalii_lark::{Lark, Meta, ParserKind, Token, Transformer};
///
/// struct Sum;
/// impl Transformer for Sum {
///     type Output = i64;
///     type Error = std::num::ParseIntError;
///     fn tree(&mut self, _data: &str, children: Vec<i64>, _meta: &Meta) -> Result<i64, Self::Error> {
///         Ok(children.iter().sum())
///     }
///     fn token(&mut self, token: Token) -> Result<i64, Self::Error> {
///         token.value.parse()
///     }
/// }
///
/// let lark = Lark::builder("start: (NUMBER \",\"?)+\n%import common.NUMBER").parser(ParserKind::Lalr).build().unwrap();
/// assert_eq!(Sum.transform(lark.parse("1,2,3").unwrap()).unwrap(), 6);
/// ```
pub trait Transformer {
    /// What a tree, token or placeholder becomes.
    type Output;
    /// The error of the callbacks.
    type Error: Into<BoxError>;

    /// Transforms a tree whose children were already transformed. `data` is its rule or alias name.
    fn tree(&mut self, data: &str, children: Vec<Self::Output>, meta: &Meta) -> std::result::Result<Self::Output, Self::Error>;

    /// Transforms a token.
    fn token(&mut self, token: Token) -> std::result::Result<Self::Output, Self::Error>;

    /// Transforms the placeholder of an unmatched `[optional]` (see `maybe_placeholders`). By default there's no
    /// value for it, and transforming a tree with a placeholder fails.
    fn missing(&mut self) -> Option<Self::Output> {
        None
    }

    /// Transforms the tree.
    fn transform(&mut self, tree: Tree) -> Result<Self::Output>
    where
        Self: Sized,
    {
        self.transform_node(Node::Tree(tree))
    }

    /// Transforms a tree, token or placeholder.
    fn transform_node(&mut self, node: Node) -> Result<Self::Output>
    where
        Self: Sized,
    {
        enum Work {
            Visit(Node),
            Build(String, Meta, usize),
        }
        let visit_error = |rule: &str, e: Self::Error| {
            Error::Visit(Box::new(VisitError {
                rule: rule.to_string(),
                source: e.into(),
            }))
        };
        let mut work = vec![Work::Visit(node)];
        let mut values: Vec<Self::Output> = Vec::new();
        while let Some(item) = work.pop() {
            match item {
                Work::Visit(Node::Tree(mut tree)) => {
                    let children = std::mem::take(&mut tree.children);
                    work.push(Work::Build(std::mem::take(&mut tree.data), tree.meta().clone(), children.len()));
                    work.extend(children.into_iter().rev().map(Work::Visit));
                }
                Work::Visit(Node::Token(token)) => {
                    let kind = token.kind.clone();
                    values.push(self.token(token).map_err(|e| visit_error(&kind, e))?);
                }
                Work::Visit(Node::Missing) => match self.missing() {
                    Some(value) => values.push(value),
                    None => {
                        let source: BoxError = "The transformer has no value for a placeholder; implement `Transformer::missing`.".into();
                        return Err(Error::Visit(Box::new(VisitError {
                            rule: "None".into(),
                            source,
                        })));
                    }
                },
                Work::Build(data, meta, count) => {
                    let children = values.split_off(values.len() - count);
                    values.push(self.tree(&data, children, &meta).map_err(|e| visit_error(&data, e))?);
                }
            }
        }
        Ok(values.pop().expect("a transformed node"))
    }
}

/// Walks a tree and calls [`Visitor::visit`] for each subtree, without changing it (Lark's `Visitor`).
pub trait Visitor {
    /// Called once for each subtree.
    fn visit(&mut self, tree: &Tree);

    /// Visits the subtrees bottom-up: each tree after its children, in Lark's order (`visit`).
    fn visit_all(&mut self, tree: &Tree)
    where
        Self: Sized,
    {
        for subtree in tree.iter_subtrees() {
            self.visit(subtree);
        }
    }

    /// Visits the subtrees top-down: each tree before its children (`visit_topdown`).
    fn visit_topdown(&mut self, tree: &Tree)
    where
        Self: Sized,
    {
        for subtree in tree.iter_subtrees_topdown() {
            self.visit(subtree);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Lark, ParserKind};

    struct Depth;
    impl Transformer for Depth {
        type Output = usize;
        type Error = std::convert::Infallible;
        fn tree(&mut self, _: &str, children: Vec<usize>, _: &Meta) -> std::result::Result<usize, Self::Error> {
            Ok(children.into_iter().max().unwrap_or(0) + 1)
        }
        fn token(&mut self, _: Token) -> std::result::Result<usize, Self::Error> {
            Ok(0)
        }
    }

    #[test]
    fn transforms_deep_trees_without_recursing() {
        let mut tree = Tree::new("leaf", vec![]);
        for _ in 0..200_000 {
            tree = Tree::new("node", vec![Node::Tree(tree)]);
        }
        assert_eq!(Depth.transform(tree).unwrap(), 200_001);
    }

    #[test]
    fn fails_on_unhandled_placeholders() {
        let lark = Lark::builder("start: \"a\" [B]\nB: \"b\"")
            .parser(ParserKind::Lalr)
            .build()
            .unwrap();
        let error = Depth.transform(lark.parse("a").unwrap()).unwrap_err();
        assert_eq!(error.lark_name(), "VisitError");
    }

    #[test]
    fn visits_bottom_up() {
        struct Names(Vec<String>);
        impl Visitor for Names {
            fn visit(&mut self, tree: &Tree) {
                self.0.push(tree.data.clone());
            }
        }
        let lark = Lark::builder("start: a b\na: \"a\"\nb: \"b\"")
            .parser(ParserKind::Lalr)
            .build()
            .unwrap();
        let mut names = Names(vec![]);
        names.visit_all(&lark.parse("ab").unwrap());
        assert_eq!(names.0, ["a", "b", "start"]);
    }
}
