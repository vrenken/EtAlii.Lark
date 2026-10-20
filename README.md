# EtAlii.Lark

A port of the [Lark](https://github.com/lark-parser/lark) parsing toolkit (version 1.3.1) to C# and .NET. It covers all of Lark:

- Lark's EBNF grammar language, with `%import`, `%ignore`, `%declare`, `%override` and `%extend`, templates, and the
  `common`, `unicode` and `python` grammars.
- The Earley, LALR(1) and CYK parsers.
- The basic, contextual, dynamic and custom lexers, plus post-lexers such as the `Indenter`.
- Tree building: `?rule` and `_rule` inlining, `!rule` token keeping, aliases, `maybe_placeholders`, positions,
  and ambiguity handling (resolve, explicit, forest).
- Transformers, visitors and interpreters, plus SPPF forest transformers.
- Error handling: `onError` recovery, the interactive parser, and `MatchExamples`.
- Tree templates, the tree matcher and the reconstructor.
- On top of that, modern .NET APIs: a fluent builder, async and streaming parsing, and a Roslyn source generator that
  turns a grammar into a type-safe parser.

```
dotnet add package EtAlii.Lark
```

The package targets .NET 8 and .NET 10, and includes the source generator.

## Quick start

```csharp
using EtAlii.Lark;

var parser = Lark.FromGrammar("""
        start: WORD "," WORD "!"
        %import common.WORD
        %ignore " "
        """)
    .UseLalr()
    .Build();

Tree tree = parser.Parse("Hello, World!");
Console.WriteLine(tree.Pretty());
```

`new Lark(grammar, new LarkOptions { ... })` is the equivalent of Python's `Lark(grammar, **options)`. Every Lark option
has a property on `LarkOptions` and a method on the builder.

## Transformers and visitors

Transformer methods are bound to rules by name, so `add` (or `Add`) handles the rule `add`, and `NUMBER` handles the
terminal of that name. You can also bind explicitly with `[Rule("...")]` and `[Terminal("...")]`. `[VArgs]` mirrors
Lark's `v_args`.

```csharp
public class CalcTransformer : Transformer
{
    public object add(List<object?> children) => (double)children[0]! + (double)children[1]!;

    [VArgs(Inline = true)]
    public object mul(double a, double b) => a * b;

    public object NUMBER(Token token) => double.Parse(token.Value, CultureInfo.InvariantCulture);
}

double result = (double)new CalcTransformer().Transform(tree)!;
```

`Visitor`, `RecursiveVisitor`, `Interpreter`, `InPlaceTransformer`, `NonRecursiveTransformer`,
`Transformer.Merge` and chaining with `*` all work as in Lark. A transformer passed through `WithTransformer(...)`
runs inside the LALR parser, so no tree is built; `parser.Parse<T>(text)` returns its result.

## Async and streaming

```csharp
await using var file = File.OpenRead("big.json");
Tree tree = await parser.ParseAsync(file, cancellationToken: cancellationToken);

using var reader = new StreamReader("big.json");
await foreach (Token token in parser.LexAsync(reader, cancellationToken: cancellationToken))
{
    // ...
}
```

`ParseAsync`, `Parse(TextReader)`, `Lex(TextReader)` and `LexAsync` accept a `TextReader` or a `Stream`.

- **LALR with the basic or contextual lexer:** the text is lexed and parsed as it is read, so the input never needs to
  be in memory as a whole.
- **Earley and CYK:** the text is read first, then parsed.

The results, token positions and error positions are the same as for parsing a string. `WithStreaming(chunkSize,
lookahead)` tunes the read size and how far the lexer looks past a match. The default of 4096 characters is plenty for
ordinary grammars.

## The source generator

Add a grammar file to the project and put `[LarkGrammar]` on a partial class:

```xml
<ItemGroup>
  <AdditionalFiles Include="calc.lark" />
</ItemGroup>
```

```csharp
[LarkGrammar("calc.lark", Parser = ParserKind.Lalr)]
public partial class CalcParser;
```

```
?start: sum
?sum: product
    | sum "+" product   -> add
?product: NUMBER        -> number
    | product "*" NUMBER -> mul
%import common.NUMBER
%ignore " "
```

The generator adds the following to `CalcParser`:

- `Parse`, `ParseAsync`, `Lex` and `LexAsync`, plus a typed `Parse{Start}` method for each start rule.
- `CalcParser.Rules` and `CalcParser.Terminals`, with the names of the rules, aliases and terminals as constants.
- A node struct for each rule and alias, such as `AddNode` and `NumberNode`. When a rule has a fixed shape, the struct
  has typed properties for its children. Child rules have their own node types, tokens are `Token`, and children that
  can be either a tree or a token are `object?`.
- An abstract `Visitor<TResult>` with a virtual `Visit{Rule}` method for each rule.

```csharp
class Evaluator : CalcParser.Visitor<double>
{
    public override double VisitAdd(CalcParser.AddNode node) => Visit(node.Sum) + Visit(node.Product);
    public override double VisitMul(CalcParser.MulNode node) => Visit(node.Product) * double.Parse(node.Number.Value);
    public override double VisitNumber(CalcParser.NumberNode node) => double.Parse(node.Number.Value);
}

double value = new Evaluator().Visit(CalcParser.Parse("1 + 2 * 3"));
```

You can also give the grammar inline, with `[LarkGrammar(Grammar = """ ... """)]`. To set any other option, implement
`static partial void ConfigureOptions(LarkOptions options)` in the class. The generator reports these diagnostics:

| Id | Meaning |
|----|---------|
| LARK001 | The class, or one of its containing types, is not `partial`. |
| LARK002 | The grammar file is not one of the project's `AdditionalFiles`. |
| LARK003 | The grammar could not be analyzed, so the parser was generated without typed nodes. |
| LARK004 | Specify either a grammar file or `Grammar`, not both and not neither. |

## Differences from Python Lark

- Names follow .NET conventions: `parse` becomes `Parse`, `lexer_callbacks` becomes `LexerCallbacks`,
  `UnexpectedToken` becomes `UnexpectedTokenException`, and so on.
- Grammar regular expressions use Python's syntax and are translated to .NET regular expressions.
- Lark's `cache` option stores the analyzed grammar as JSON. `Lark.Save(Stream)` and `Lark.Load(Stream)` replace pickling.
- `Tree` and `Token` support C# patterns such as `tree is ("add", [var left, var right])`.
- Lark's Nearley converter and its standalone-parser tool are not ported. The source generator takes the place of the
  standalone parser.

## Tests

The test project ports Lark's test suite, which covers:

- the parser tests across all parser and lexer combinations;
- grammar loading and imports;
- the lexers, the logger and the cache;
- trees, transformers and the forest transformer;
- tree templates, the reconstructor, the Python grammar, and pattern matching.

It adds tests for streaming and for the source generator.

```
dotnet test
```

## Releasing

Pushing a version tag publishes the package to nuget.org through the `Release` workflow, which builds, tests, packs
(with symbols and SourceLink), pushes, and creates a GitHub release:

```
git tag v0.1.0
git push origin v0.1.0
```

A tag with a suffix, such as `v0.2.0-preview.1`, publishes a prerelease. The workflow authenticates with nuget.org
trusted publishing when `NUGET_USER` is set (as a repository variable or secret), or with the `NUGET_API_KEY` secret otherwise.

## License

MIT; see [LICENSE](LICENSE). EtAlii.Lark contains code, grammars and tests ported from Lark (MIT, © Erez Shinan); see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
