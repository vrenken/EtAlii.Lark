# EtAlii.Lark

Ports of the [Lark](https://github.com/lark-parser/lark) parsing toolkit (version 1.3.1) to .NET, Rust and
TypeScript. Each package covers Lark's grammar language, its Earley, LALR(1) and CYK parsers, its lexers, tree
building, and transformers and visitors, with an API that is idiomatic for its language and a code generator that
turns a grammar into a type-safe parser.

| Language | Package | Folder |
|----------|---------|--------|
| C# / .NET | [`EtAlii.Lark`](https://www.nuget.org/packages/EtAlii.Lark) on NuGet | [`dotnet/`](dotnet/README.md) |
| Rust | `etalii-lark` on crates.io (in progress) | `rust/` |
| TypeScript | `@etalii/lark` on npm (in progress) | `typescript/` |

## One interpretation of a grammar

A grammar must mean the same thing in every package: the same trees, the same token types and positions, the same
errors. The [conformance suite](conformance/README.md) makes sure of that. It holds grammars, inputs and the results
Python Lark gives for them, as language-neutral data: the cases of Lark's own test suite, extracted automatically, and
cases for Lark's example grammars and its Python and Lark grammars. Every package runs all of them in CI, and CI checks
that the expected results are exactly what Lark 1.3.1 produces.

## License

MIT; see [LICENSE](LICENSE). The packages contain code, grammars and tests ported from Lark (MIT, © Erez Shinan); see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
