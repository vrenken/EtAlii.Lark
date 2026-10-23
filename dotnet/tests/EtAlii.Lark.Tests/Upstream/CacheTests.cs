// Ported from lark/tests/test_cache.py.
// ReSharper disable InconsistentNaming
#pragma warning disable IDE1006
namespace EtAlii.Lark.Tests.Upstream;

using static TestHelpers;

[Collection(GlobalStateCollection.Name)]
public sealed class CacheTests : IDisposable
{
    private sealed class MockFs : ILarkFileSystem
    {
        public readonly Dictionary<string, byte[]> Files = new();

        public Stream OpenRead(string path) =>
            Files.TryGetValue(path, out var data) ? new MemoryStream(data, writable: false) : throw new FileNotFoundException(path);

        public Stream OpenWrite(string path) => new CapturingStream(this, path);

        public bool Exists(string path) => Files.ContainsKey(path);

        private sealed class CapturingStream : MemoryStream
        {
            private readonly MockFs _fs;
            private readonly string _path;

            public CapturingStream(MockFs fs, string path)
            {
                _fs = fs;
                _path = path;
            }

            protected override void Dispose(bool disposing)
            {
                _fs.Files[_path] = ToArray();
                base.Dispose(disposing);
            }
        }
    }

    private const string G = """start: "a" """;

    private readonly ILarkFileSystem _fs;
    private readonly MockFs _mockFs = new();

    public CacheTests()
    {
        _fs = Lark.FileSystem;
        Lark.FileSystem = _mockFs;
    }

    public void Dispose()
    {
        Lark.FileSystem = _fs;
    }

    private static Lark Lalr(string grammar, Action<LarkOptions>? configure = null)
    {
        var options = new LarkOptions { Parser = ParserKind.Lalr, Cache = true };
        configure?.Invoke(options);
        return new Lark(grammar, options);
    }

    [Fact]
    public void test_simple()
    {
        const string fn = "bla";

        Lalr(G, o => o.CachePath = fn);
        Assert.True(_mockFs.Files.ContainsKey(fn));
        var parser = Lalr(G, o => o.CachePath = fn);
        Assert.Equal(T("start"), parser.Parse("a"));
    }

    [Fact]
    public void test_automatic_naming()
    {
        Assert.Empty(_mockFs.Files);
        Lalr(G);
        Assert.Single(_mockFs.Files);
        var parser = Lalr(G);
        Assert.Equal(T("start"), parser.Parse("a"));

        parser = Lalr(G + " \"b\"");
        Assert.Equal(2, _mockFs.Files.Count);
        Assert.Equal(T("start"), parser.Parse("ab"));

        parser = Lalr(G);
        Assert.Equal(T("start"), parser.Parse("a"));
    }

    private sealed class CustomLexer : Lexer
    {
        public override IEnumerable<Token> Lex(LexerState lexerState, object? parserState)
        {
            foreach (var c in lexerState.Text.ToString()) yield return new Token("A", c.ToString());
        }
    }

    [Fact]
    public void test_custom_lexer()
    {
        static void UseCustom(LarkOptions o)
        {
            o.Lexer = LexerKind.Custom;
            o.CustomLexer = _ => new CustomLexer();
        }

        Lalr(G, UseCustom);
        Assert.Single(_mockFs.Files);
        var parser = Lalr(G, UseCustom);
        Assert.Single(_mockFs.Files);
        Assert.Equal(T("start"), parser.Parse("a"));
    }

    [Fact]
    public void test_options()
    {
        // Test options persistence
        Lalr(G, o => o.Debug = true);
        var parser = Lalr(G, o => o.Debug = true);
        Assert.True(parser.Options.Debug);
    }

    private sealed class InlineTestT : Transformer
    {
        public object add(List<object?> children) => children.Sum(Convert.ToInt32);

        public object NUM(Token token) => int.Parse(token.Value);
    }

    private static Token AppendZero(Token t) => t.Update(value: t.Value + "0");

    [Fact]
    public void test_inline()
    {
        // Test inline transformer (tree-less) & lexer_callbacks
        const string g = """
            start: add+
            add: NUM "+" NUM
            NUM: /\d+/
            %ignore " "
            """;
        const string text = "1+2 3+4";
        var expected = T("start", 30, 70);

        var parser = Lalr(g, o =>
        {
            o.Transformer = new InlineTestT();
            o.LexerCallbacks["NUM"] = AppendZero;
        });
        var res0 = parser.Parse(text);
        parser = Lalr(g, o =>
        {
            o.Transformer = new InlineTestT();
            o.LexerCallbacks["NUM"] = AppendZero;
        });
        Assert.Single(_mockFs.Files);
        var res1 = parser.Parse(text);
        var res2 = new InlineTestT().Transform(Lalr(g, o => o.LexerCallbacks["NUM"] = AppendZero).Parse(text));
        AssertEq(expected, res0);
        AssertEq(expected, res1);
        AssertEq(expected, res2);
    }

    [Fact]
    public void test_imports()
    {
        const string g = """
            %import .grammars.ab (startab, expr)
            """;
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "test_cache.cs");
        Lalr(g, o =>
        {
            o.Start = new[] { "startab" };
            o.SourcePath = sourcePath;
        });
        Assert.Single(_mockFs.Files);
        var parser = Lalr(g, o =>
        {
            o.Start = new[] { "startab" };
            o.SourcePath = sourcePath;
        });
        Assert.Single(_mockFs.Files);
        AssertEq(Py.Tree("Tree('startab', [Tree('expr', ['a', 'b'])])"), parser.Parse("ab"));
    }

    [Fact]
    public void test_error_message()
    {
        // Checks that error message generation works
        const string g = """
            start: add+
            add: /\d+/ "+" /\d+/
            %ignore " "
            """;
        var texts = new[] { "1+", "+1", "", "1 1+1" };

        var parser1 = Lalr(g);
        var parser2 = Lalr(g);
        Assert.Single(_mockFs.Files);
        foreach (var text in texts)
        {
            var e1 = Assert.ThrowsAny<UnexpectedInputException>(() => parser1.Parse(text));
            var e2 = Assert.ThrowsAny<UnexpectedInputException>(() => parser2.Parse(text));
            Assert.Equal(e1.Message, e2.Message);
        }
    }

    [Fact]
    public void test_cache_requires_lalr()
    {
        Assert.Throws<ConfigurationException>(() => new Lark(G, new LarkOptions { Parser = ParserKind.Earley, Cache = true }));
    }
}
