# etalii-lark-derive

The `#[lark_grammar]` macro of [etalii-lark](https://crates.io/crates/etalii-lark), a port of the
[Lark](https://github.com/lark-parser/lark) parsing toolkit to Rust. Put it on a module, and it fills the module at
compile time with a parser for a Lark grammar, a typed wrapper for each kind of tree, and, if you map rules to result
types, a typed visitor. It works like `pest_derive` does for pest.

```sh
cargo add etalii-lark etalii-lark-derive
```

## Example

```rust
use etalii_lark::Token;
use etalii_lark_derive::{lark_grammar, lark_result};

#[lark_grammar(grammar = r#"
    ?start: sum
    ?sum: product | sum "+" product -> add
    ?product: atom | product "*" atom -> mul
    ?atom: NUMBER -> number | "(" sum ")"
    %import common.NUMBER
    %ignore " "
"#, parser = "lalr")]
#[lark_result(f64, "add", "mul", "number")]
pub mod calc {}

struct Evaluator;

impl calc::Visitor for Evaluator {
    type Error = std::num::ParseFloatError;

    fn visit_add(&mut self, _: calc::AddNode<'_>, sum: f64, product: f64) -> Result<f64, Self::Error> {
        Ok(sum + product)
    }

    fn visit_mul(&mut self, _: calc::MulNode<'_>, product: f64, atom: f64) -> Result<f64, Self::Error> {
        Ok(product * atom)
    }

    fn visit_number(&mut self, _: calc::NumberNode<'_>, number: &Token) -> Result<f64, Self::Error> {
        number.value.parse()
    }
}

fn main() {
    use calc::Visitor;
    let tree = calc::parse("1 + 2 * (3 + 4)").unwrap();
    assert_eq!(Evaluator.visit_start(&tree).unwrap(), 15.0);
}
```

Usually the grammar is a file: `#[lark_grammar(file = "src/calc.lark", parser = "lalr")]`, with the path relative to
the crate's `Cargo.toml`. The macro compiles the grammar with etalii-lark itself, so a grammar error, or a name in
`#[lark_result]` that isn't in the grammar, is a compile error that points at the attribute. Cargo rebuilds the crate
when the file changes.

[`examples/purchase_orders.rs`](https://github.com/vrenken/EtAlii.Lark/blob/develop/rust/etalii-lark-derive/examples/purchase_orders.rs)
builds a purchase order model from the grammar of the .NET documentation's typed visitor example, so the two can be
compared side by side.

## Options

| Option | Values |
|--------|--------|
| `file` or `grammar` | The grammar file (relative to `Cargo.toml`), or the grammar text. |
| `parser` | `"earley"` (the default), `"lalr"`, `"cyk"` |
| `lexer` | `"auto"` (the default), `"basic"`, `"contextual"`, `"dynamic"`, `"dynamic_complete"` |
| `start` | The start rule, `"start"` by default, or a list: `["a", "b"]` |
| `maybe_placeholders` | `true` (the default) or `false` |
| `keep_all_tokens` | `false` (the default) or `true` |

## What the module contains

- **The parser:** `parse(text)`, plus `parse_{start}(text)` for each start rule when there are several, and `lex`.
  They use `parser()`, a `Lark` built on first use. `GRAMMAR` and `options()` are what it is built from.
- **Name constants:** `rules::ADD`, `terminals::NUMBER`, and so on.
- **Node wrappers:** a `Copy` wrapper for each rule, alias and template that makes trees, such as `AddNode<'a>`.
  `AddNode::new(&tree)` returns `Some` for an `add` tree. The wrapper dereferences to the `Tree`, and has an accessor
  for each child:
  - A child that is always a `line` tree is a `LineNode`, and a token is a `&Token`.
  - A child that can be several trees or tokens is a generated enum, such as `Shipping::ExpressShipping(node)`. The
    enum is named after the `?rule` it stands for, or after the tree and the child.
  - `[item]` and `item?` give an `Option`. `item*` and `item+` give an iterator, whose accessor has a plural name
    (`lines()`).
  - Accessors are named after the grammar (`text`, `text2`, `count`). Filtered tokens, such as anonymous strings
    and `_TERMINALS`, aren't children, and `_rules` are inlined into their parent's accessors.
  - Trees whose alternatives have different children have no fixed shape. Their wrapper has `children()` instead.
- **The typed visitor**, when there are `#[lark_result]` mappings: a `Visitor` trait.
  - Each mapped tree gets a required method, `visit_add(node, sum, product)`, which gets its children already visited
    and converted to their result types. Children that aren't mapped come as their node wrapper or token, so the
    visitor only visits what you map.
  - Each mapped terminal gets `visit_number_token(token)`.
  - The provided methods visit a tree: `visit_add_tree(&tree)` for each mapped tree, and `visit_{start}(&tree)` when
    all trees of a start rule have the same result type.
  - The visitor doesn't recurse, so deep trees are fine.
  - Errors stop the visit, through `Self::Error`.

The result type of a child that can be several trees is their common result type. When they have different result
types, it is a generated enum, such as `SumValue::Add(f64)`. Map a `?rule` itself to set the type explicitly: every
tree it can stand for must then have that type.

## Code generation from a build script

The macro is a thin wrapper around `etalii_lark::codegen::Generator`, which a build script can use to write the same
code to a file, for example to read or commit it:

```rust,ignore
// build.rs
let code = etalii_lark::codegen::Generator::open(concat!(env!("CARGO_MANIFEST_DIR"), "/src/calc.lark"))?
    .parser(etalii_lark::ParserKind::Lalr)
    .result("f64", ["add", "mul", "number"])
    .generate()?;
std::fs::write(format!("{}/calc.rs", std::env::var("OUT_DIR")?), code)?;

// src/lib.rs
pub mod calc {
    include!(concat!(env!("OUT_DIR"), "/calc.rs"));
}
```

## How the shapes are worked out

The node wrappers and visitor parameters come from the *shapes* of the trees: what can stand at each position among
a tree's children. They are derived from the grammar as Lark compiles it, so they follow inlining, aliases,
templates, imports, placeholders and repetitions exactly as the parser does. The TypeScript package computes the
same shapes, and the conformance suite in the [repository](https://github.com/vrenken/EtAlii.Lark) checks them
against the trees Python Lark builds.

## License

MIT.
