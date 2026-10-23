//! Code generation: the typed shapes of a grammar's trees, and the generator of a typed Rust API from them.
//!
//! The `#[lark_grammar]` macro of the `etalii-lark-derive` crate is the usual way to use it. [`Generator`] does the
//! same from a build script.

mod rust;
mod shapes;

pub use rust::{CodegenError, CodegenOptions, Generator, ResultMapping};
pub use shapes::{ElementKind, Member, Shape, ShapeElement, Shapes};

use crate::token::Token;
use crate::tree::{Node, Tree};

/// A tree or token, as the typed visitor of generated code visits them.
#[doc(hidden)]
#[derive(Clone, Copy, Debug)]
pub enum Item<'t> {
    Tree(&'t Tree),
    Token(&'t Token),
}

/// Visits the tree bottom-up without recursing, for the typed visitors of generated code. Each tree or token that
/// gets a value is built from the values of its children: the children that are `visited`, of the trees that
/// `descends` into.
#[doc(hidden)]
pub fn visit_bottom_up<'t, V, E>(
    root: &'t Tree,
    visited: impl Fn(&'t Node) -> bool,
    descends: impl Fn(&'t Tree) -> bool,
    mut build: impl FnMut(Item<'t>, std::vec::IntoIter<V>) -> Result<V, E>,
) -> Result<V, E> {
    enum Work<'t> {
        Enter(Item<'t>),
        Exit(&'t Tree, usize),
    }
    let mut work = vec![Work::Enter(Item::Tree(root))];
    let mut values: Vec<V> = Vec::new();
    while let Some(item) = work.pop() {
        match item {
            Work::Enter(Item::Tree(tree)) if descends(tree) => {
                let start = work.len() + 1;
                let mut count = 0;
                work.push(Work::Exit(tree, 0));
                for child in tree.children.iter().filter(|c| visited(c)) {
                    count += 1;
                    work.push(Work::Enter(match child {
                        Node::Tree(t) => Item::Tree(t),
                        Node::Token(t) => Item::Token(t),
                        Node::Missing => unreachable!("placeholders aren't visited"),
                    }));
                }
                work[start..].reverse();
                work[start - 1] = Work::Exit(tree, count);
            }
            Work::Enter(item) => values.push(build(item, Vec::new().into_iter())?),
            Work::Exit(tree, count) => {
                let children = values.split_off(values.len() - count);
                values.push(build(Item::Tree(tree), children.into_iter())?);
            }
        }
    }
    Ok(values.pop().expect("the value of the root"))
}
