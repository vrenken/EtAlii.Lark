namespace EtAlii.Lark.Tests.Generator;

using System.Collections.Immutable;
using EtAlii.Lark.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

public class GeneratorDiagnosticsTests
{
    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly string _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = text;
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(_text);
    }

    private static (ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> CompilationDiagnostics, string[] Sources) Run(
        string source, params (string Path, string Text)[] files)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(Lark).Assembly.Location));
        var compilation = CSharpCompilation.Create("GeneratorTest",
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new LarkGrammarGenerator().AsSourceGenerator() },
            files.Select(f => (AdditionalText)new InMemoryAdditionalText(f.Path, f.Text)),
            new CSharpParseOptions(LanguageVersion.Preview));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var result = driver.GetRunResult();
        var sources = result.GeneratedTrees.Select(t => t.ToString()).ToArray();
        return (diagnostics, output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray(), sources);
    }

    [Fact]
    public void GrammarFile_GeneratesCompilingParser()
    {
        var (diagnostics, errors, sources) = Run("""
            using EtAlii.Lark;
            namespace Demo;
            [LarkGrammar("grammars/expr.lark", Parser = ParserKind.Lalr)]
            public partial class ExprParser { }
            """, ("/project/grammars/expr.lark", """
            start: expr
            expr: NUMBER "+" NUMBER -> add
                | NUMBER
            %import common.NUMBER
            """));

        Assert.Empty(diagnostics);
        Assert.Empty(errors);
        var source = Assert.Single(sources);
        Assert.Contains("public readonly partial struct AddNode", source);
        Assert.Contains("public global::EtAlii.Lark.Token Number1 =>", source);
        Assert.Contains("public global::EtAlii.Lark.Token Number2 =>", source);
        Assert.Contains("public static StartNode ParseStart(string text)", source);
    }

    [Fact]
    public void NotPartial_ReportsLark001()
    {
        var (diagnostics, _, sources) = Run("""
            using EtAlii.Lark;
            [LarkGrammar(Grammar = "start: \"a\"")]
            public class P { }
            """);

        Assert.Equal("LARK001", Assert.Single(diagnostics).Id);
        Assert.Empty(sources);
    }

    [Fact]
    public void ContainingTypeNotPartial_ReportsLark001()
    {
        var (diagnostics, _, _) = Run("""
            using EtAlii.Lark;
            public class Outer
            {
                [LarkGrammar(Grammar = "start: \"a\"")]
                public partial class P { }
            }
            """);

        Assert.Equal("LARK001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void MissingFile_ReportsLark002()
    {
        var (diagnostics, _, _) = Run("""
            using EtAlii.Lark;
            [LarkGrammar("missing.lark")]
            public partial class P { }
            """, ("/project/other.lark", "start: \"a\""));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("LARK002", diagnostic.Id);
        Assert.Contains("missing.lark", diagnostic.GetMessage());
    }

    [Fact]
    public void InvalidGrammar_ReportsLark003AndStillGeneratesParser()
    {
        var (diagnostics, errors, sources) = Run("""
            using EtAlii.Lark;
            [LarkGrammar("bad.lark")]
            public partial class P { }
            """, ("/project/bad.lark", "start: \"a\"\n\nrule: (\"b\"\n"));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("LARK003", diagnostic.Id);
        Assert.Equal("/project/bad.lark", diagnostic.Location.GetLineSpan().Path);
        Assert.Equal(2, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Empty(errors);
        Assert.Contains("public static global::EtAlii.Lark.Tree Parse(string text", Assert.Single(sources));
    }

    [Theory]
    [InlineData("[LarkGrammar]")]
    [InlineData("[LarkGrammar(\"a.lark\", Grammar = \"start: \\\"a\\\"\")]")]
    public void GrammarSource_ReportsLark004(string attribute)
    {
        var (diagnostics, _, _) = Run($$"""
            using EtAlii.Lark;
            {{attribute}}
            public partial class P { }
            """, ("/project/a.lark", "start: \"a\""));

        Assert.Equal("LARK004", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void GenericAndNestedClasses_Compile()
    {
        var (diagnostics, errors, _) = Run("""
            using EtAlii.Lark;
            public partial record Outer
            {
                public partial struct Middle
                {
                    [LarkGrammar(Grammar = "start: tree token\ntree: \"t\"\ntoken: \"k\"")]
                    internal partial class P<T> { }
                }
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Empty(errors);
    }

    private const string OrderGrammar = """
        start: item+
        item: NAME AMOUNT          -> priced
            | NAME                 -> free
            | NAME "x" COUNT       -> counted
        ?amount: AMOUNT | COUNT
        total: "total" amount
        %import common.CNAME -> NAME
        %import common.NUMBER -> AMOUNT
        %import common.INT -> COUNT
        %import common.WS
        %ignore WS
        """;

    private static (ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> CompilationDiagnostics, string[] Sources) RunTyped(string attributes, string members = "") =>
        Run($$""""
            using EtAlii.Lark;
            namespace Demo;
            public record Item;
            public record Priced : Item;
            public record Free : Item;
            public class Unrelated;
            [LarkGrammar(Grammar = """
            {{OrderGrammar}}
            """)]
            {{attributes}}
            public partial class P { {{members}} }
            """");

    [Fact]
    public void LarkResult_GeneratesTypedVisitor()
    {
        var (diagnostics, errors, sources) = RunTyped("""
            [LarkResult(typeof(Priced), "priced")]
            [LarkResult(typeof(Free), "free")]
            [LarkResult(typeof(decimal), "AMOUNT")]
            """);

        // The children of start are priced, free or counted trees, and of amount AMOUNT or COUNT tokens: no common type.
        Assert.Equal(new[] { "LARK007", "LARK007" }, diagnostics.Select(d => d.Id));
        Assert.Contains("[LarkResult(typeof(...), \"item\")]", diagnostics[0].GetMessage());
        Assert.Empty(errors);
        var source = Assert.Single(sources);
        Assert.Contains("public abstract partial class Visitor\n", source);
        Assert.Contains("protected abstract global::Demo.Priced VisitPriced(PricedNode node, global::EtAlii.Lark.Token name, decimal amount);", source);
        Assert.Contains("protected virtual CountedNode VisitCounted(CountedNode node, global::EtAlii.Lark.Token name, global::EtAlii.Lark.Token count) => node;", source);
        Assert.Contains("protected abstract decimal VisitAmountToken(global::EtAlii.Lark.Token token);", source);
        Assert.Contains("VisitStart(StartNode node, global::System.Collections.Generic.IReadOnlyList<object> item)", source);
    }

    [Fact]
    public void LarkResult_InfersCommonBaseTypes()
    {
        var (diagnostics, errors, sources) = RunTyped("""
            [LarkResult(typeof(Priced), "priced")]
            [LarkResult(typeof(Free), "free", "counted")]
            """);

        Assert.Empty(diagnostics);
        Assert.Empty(errors);
        Assert.Contains("VisitStart(StartNode node, global::System.Collections.Generic.IReadOnlyList<global::Demo.Item> item)", Assert.Single(sources));
    }

    [Fact]
    public void LarkResult_InlinedRuleSetsTheTypeOfItsChildren()
    {
        var (diagnostics, errors, sources) = RunTyped("""
            [LarkResult(typeof(decimal), "amount", "AMOUNT")]
            [LarkResult(typeof(int), "COUNT")]
            """);

        // COUNT is an int, which doesn't convert to the decimal of the amount rule.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("LARK008", diagnostic.Id);
        Assert.Contains("'COUNT'", diagnostic.GetMessage());
        Assert.Contains("VisitTotal(TotalNode node, decimal amount)", Assert.Single(sources));
    }

    [Fact]
    public void LarkResult_UnknownName_ReportsLark005()
    {
        var (diagnostics, errors, _) = RunTyped("""[LarkResult(typeof(Priced), "priced", "nonsense")]""");

        var diagnostic = Assert.Single(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("LARK005", diagnostic.Id);
        Assert.Contains("'nonsense'", diagnostic.GetMessage());
        Assert.Empty(errors);
    }

    [Fact]
    public void LarkResult_DuplicateName_ReportsLark006()
    {
        var (diagnostics, _, _) = RunTyped("""
            [LarkResult(typeof(Priced), "priced")]
            [LarkResult(typeof(Free), "priced")]
            """);

        Assert.Equal("LARK006", Assert.Single(diagnostics, d => d.Severity == DiagnosticSeverity.Error).Id);
    }

    [Fact]
    public void LarkResult_NoCommonType_ReportsLark007()
    {
        var (diagnostics, errors, sources) = RunTyped("""
            [LarkResult(typeof(Priced), "priced")]
            [LarkResult(typeof(Unrelated), "free", "counted")]
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("LARK007", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Empty(errors);
        Assert.Contains("IReadOnlyList<object> item", Assert.Single(sources));
    }

    [Fact]
    public void LarkResult_NodeWithoutFixedShape_ReportsLark009()
    {
        var (diagnostics, errors, sources) = Run(""""
            using EtAlii.Lark;
            [LarkGrammar(Grammar = """
                start: value
                value: A B | A
                A: "a"
                B: "b"
                """)]
            [LarkResult(typeof(string), "value")]
            public partial class P { }
            """");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("LARK009", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Empty(errors);
        Assert.Contains("protected abstract string VisitValue(ValueNode node, global::System.Collections.Generic.IReadOnlyList<object?> children);", Assert.Single(sources));
    }

    [Fact]
    public void WithoutLarkResult_NoTypedVisitorIsGenerated()
    {
        var (_, _, sources) = RunTyped("");

        Assert.DoesNotContain("public abstract partial class Visitor\n", Assert.Single(sources));
    }

    [Fact]
    public void LarkResult_GeneratesCompilingVisitorsForAllGrammarsOfTheRepository()
    {
        foreach (var file in GrammarFiles())
        {
            var text = File.ReadAllText(file);
            var model = GrammarAnalyzer.Analyze(text, keepAllTokens: false);
            var node = model.Nodes.FirstOrDefault(n => !model.NeverTrees.Contains(n.Name));
            if (node is null) continue;
            var (_, errors, _) = Run($$"""
                using EtAlii.Lark;
                [LarkGrammar("grammar.lark")]
                [LarkResult(typeof(object), {{LarkGrammarGenerator.Literal(node.Name)}})]
                public partial class P { }
                """, ("/project/grammar.lark", text));

            Assert.True(errors.IsEmpty, $"{file}: {string.Join("; ", errors.Take(3))}");
        }
    }

    private static IEnumerable<string> GrammarFiles()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "EtAlii.Lark.sln"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.lark", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.lark", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void GrammarAnalyzer_AnalyzesAllGrammarsOfTheRepository()
    {
        var files = GrammarFiles().ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var exception = Record.Exception(() => GrammarAnalyzer.Analyze(File.ReadAllText(file), keepAllTokens: false));
            Assert.True(exception is null, $"{file}: {exception?.Message}");
        }
    }

    [Fact]
    public void GrammarAnalyzer_FindsShapes()
    {
        var model = GrammarAnalyzer.Analyze("""
            ?start: pair
            pair: KEY ":" _value /x/ _SEP? value
            _value: value
            value: KEY | NUMBER
            alias: KEY -> renamed
            !kept: "a" KEY
            other: KEY -> renamed
            %import common.NUMBER
            %declare KEY _SEP
            """, keepAllTokens: false);

        Assert.Equal(new[] { "start", "pair", "_value", "value", "alias", "kept", "other" }, model.RuleNames);
        Assert.Equal(new[] { "NUMBER", "KEY", "_SEP" }, model.TerminalNames);
        Assert.Null(model.FindNode("pair")!.Shape); // inlined _value
        Assert.False(model.FindNode("start")!.IsSolid);
        Assert.True(model.FindNode("value")!.IsSolid);
        Assert.Null(model.FindNode("renamed")!.Shape); // two sources
        Assert.Equal(new[] { "Token", "Key" }, model.FindNode("kept")!.Shape!.Select(c => c.PropertyName));
    }
}
