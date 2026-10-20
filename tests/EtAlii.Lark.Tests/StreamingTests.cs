namespace EtAlii.Lark.Tests;

using System.Text;

public class StreamingTests
{
    /// <summary>A reader that returns at most a few characters per read, and yields on async reads.</summary>
    private sealed class TrickleReader : TextReader
    {
        private readonly string _text;
        private readonly int _maxRead;
        private int _pos;

        public TrickleReader(string text, int maxRead = 3)
        {
            _text = text;
            _maxRead = maxRead;
        }

        public int Reads { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            Reads++;
            var n = Math.Min(Math.Min(count, _maxRead), _text.Length - _pos);
            _text.CopyTo(_pos, buffer, index, n);
            _pos += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            var n = Math.Min(Math.Min(buffer.Length, _maxRead), _text.Length - _pos);
            _text.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }
    }

    private const string JsonGrammar = """
        ?start: value
        ?value: object
              | array
              | string
              | SIGNED_NUMBER      -> number
              | "true"             -> true
              | "false"            -> false
              | "null"             -> null
        array  : "[" [value ("," value)*] "]"
        object : "{" [pair ("," pair)*] "}"
        pair   : string ":" value
        string : ESCAPED_STRING
        %import common.ESCAPED_STRING
        %import common.SIGNED_NUMBER
        %import common.WS
        %ignore WS
        """;

    private const string JsonText = """
        {
            "empty_object" : {},
            "empty_array"  : [],
            "booleans"     : { "YES" : true, "NO" : false },
            "numbers"      : [ 0, 1, -2, 3.3, 4.4e5, 6.6e-7 ],
            "strings"      : [ "This", [ "And" , "That", "And a \"b" ] ],
            "nothing"      : null
        }
        """;

    private static LarkOptions Options(ParserKind parser, LexerKind lexer = LexerKind.Auto) => new()
    {
        Parser = parser,
        Lexer = lexer,
        StreamChunkSize = 4,
        StreamLookahead = 2,
    };

    public static IEnumerable<object[]> Configurations() => new[]
    {
        new object[] { ParserKind.Lalr, LexerKind.Contextual },
        new object[] { ParserKind.Lalr, LexerKind.Basic },
        new object[] { ParserKind.Earley, LexerKind.Basic },
        new object[] { ParserKind.Earley, LexerKind.Dynamic },
        new object[] { ParserKind.Cyk, LexerKind.Basic },
    };

    [Theory]
    [MemberData(nameof(Configurations))]
    public void Parse_Reader_EqualsParseString(ParserKind parser, LexerKind lexer)
    {
        var lark = new Lark(JsonGrammar, Options(parser, lexer));
        var expected = lark.Parse(JsonText);

        var reader = new TrickleReader(JsonText);
        Assert.Equal(expected, lark.Parse(reader));
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task ParseAsync_Reader_EqualsParseString(ParserKind parser, LexerKind lexer)
    {
        var lark = new Lark(JsonGrammar, Options(parser, lexer));
        var expected = lark.Parse(JsonText);

        Assert.Equal(expected, await lark.ParseAsync(new TrickleReader(JsonText)));
    }

    [Fact]
    public async Task ParseAsync_Lalr_ReadsIncrementally()
    {
        var lark = new Lark(JsonGrammar, Options(ParserKind.Lalr));
        var reader = new TrickleReader(JsonText);

        await lark.ParseAsync(reader);

        Assert.True(reader.Reads > JsonText.Length / 3, $"Expected many small reads, got {reader.Reads}.");
    }

    [Fact]
    public async Task ParseAsync_Stream_DecodesUtf8()
    {
        const string text = """{ "naïve" : ["日本語", "😀", 1] }""";
        var lark = new Lark(JsonGrammar, Options(ParserKind.Lalr));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        var tree = await lark.ParseAsync(stream);

        Assert.Equal(lark.Parse(text), tree);
        Assert.True(stream.CanRead, "The stream should be left open.");
    }

    [Fact]
    public void Parse_Stream_DecodesUtf8()
    {
        const string text = """["€", "ü"]""";
        var lark = new Lark(JsonGrammar, Options(ParserKind.Lalr));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        Assert.Equal(lark.Parse(text), lark.Parse(stream));
    }

    [Fact]
    public async Task ParseAsync_TokenLongerThanLookahead_IsLexedWhole()
    {
        var longString = "\"" + new string('x', 10_000) + "\"";
        var text = "[" + longString + ", 12345678901234567890]";
        var lark = new Lark(JsonGrammar, Options(ParserKind.Lalr));

        var tree = await lark.ParseAsync(new TrickleReader(text, 7));

        Assert.Equal(lark.Parse(text), tree);
    }

    [Fact]
    public async Task ParseAsync_PositionsMatchParseString()
    {
        var lark = new Lark(JsonGrammar, new LarkOptions { Parser = ParserKind.Lalr, PropagatePositions = true, StreamChunkSize = 4, StreamLookahead = 2 });
        var expected = lark.Parse(JsonText).ScanValues(v => v is Token).Cast<Token>().ToList();

        var actual = (await lark.ParseAsync(new TrickleReader(JsonText))).ScanValues(v => v is Token).Cast<Token>().ToList();

        Assert.Equal(expected.Select(t => (t.StartPos, t.Line, t.Column, t.EndPos, t.EndLine, t.EndColumn)),
            actual.Select(t => (t.StartPos, t.Line, t.Column, t.EndPos, t.EndLine, t.EndColumn)));
    }

    [Theory]
    [InlineData(LexerKind.Basic)]
    [InlineData(LexerKind.Contextual)]
    public async Task ParseAsync_UnexpectedCharacters_ReportsAbsolutePosition(LexerKind lexer)
    {
        var text = JsonText.Replace("\"nothing\"", "\"nothing\" @");
        var lark = new Lark(JsonGrammar, Options(ParserKind.Lalr, lexer));
        var expected = Assert.ThrowsAny<UnexpectedCharactersException>(() => lark.Parse(text));

        var actual = await Assert.ThrowsAnyAsync<UnexpectedCharactersException>(() => lark.ParseAsync(new TrickleReader(text)));

        Assert.Equal(expected.PosInStream, actual.PosInStream);
        Assert.Equal(expected.Line, actual.Line);
        Assert.Equal(expected.Column, actual.Column);
        Assert.Equal('@', actual.Char);
    }

    [Fact]
    public async Task ParseAsync_ContextualLexer_ReportsUnexpectedToken()
    {
        const string text = "[1, 2 3]";
        var lark = new Lark(JsonGrammar, Options(ParserKind.Lalr));
        var expected = Assert.Throws<UnexpectedTokenException>(() => lark.Parse(text));

        var actual = await Assert.ThrowsAsync<UnexpectedTokenException>(() => lark.ParseAsync(new TrickleReader(text)));

        Assert.Equal(expected.Token, actual.Token);
        Assert.Equal(expected.Line, actual.Line);
        Assert.Equal(expected.Column, actual.Column);
    }

    [Fact]
    public async Task ParseAsync_UnexpectedEof_Throws()
    {
        var lark = new Lark(JsonGrammar, Options(ParserKind.Lalr));

        await Assert.ThrowsAnyAsync<UnexpectedInputException>(() => lark.ParseAsync(new TrickleReader("[1, 2")));
    }

    [Fact]
    public async Task ParseAsync_Cancelled_Throws()
    {
        var lark = new Lark(JsonGrammar, Options(ParserKind.Lalr));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lark.ParseAsync(new TrickleReader(JsonText), cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ParseAsync_WithIndenter_EqualsParseString()
    {
        var lark = Lark.OpenFromResource(typeof(Lark).Assembly, "python.lark", new[] { "Grammars" }, new LarkOptions
        {
            Parser = ParserKind.Lalr,
            PostLex = new PythonIndenter(),
            Start = new[] { "file_input" },
            StreamChunkSize = 8,
            StreamLookahead = 4,
        });
        const string code = """
            def f(x):
                if x:
                    return [1,
                        2]
                return None

            class A:
                pass

            """;
        var expected = lark.Parse(code);

        Assert.Equal(expected, await lark.ParseAsync(new TrickleReader(code)));
        Assert.Equal(expected, lark.Parse(new TrickleReader(code)));
    }

    private sealed class SumTransformer : Transformer
    {
        public object number(List<object?> children) => double.Parse(((Token)children[0]!).Value, System.Globalization.CultureInfo.InvariantCulture);

        public object array(List<object?> children) => children.Sum(c => (double)c!);
    }

    [Fact]
    public async Task ParseAsync_WithEmbeddedTransformer_ReturnsResult()
    {
        var lark = new Lark(JsonGrammar, new LarkOptions { Parser = ParserKind.Lalr, Transformer = new SumTransformer() });

        Assert.Equal(6.5, await lark.ParseAsync<double>(new StringReader("[1, 2, 3.5]")));
    }

    [Fact]
    public async Task ParseAsync_LargeInput_EqualsParseString()
    {
        var text = "[" + string.Join(", ", Enumerable.Range(0, 20_000).Select(i => i % 3 == 0 ? $"\"item {i}\"" : i.ToString())) + "]";
        var lark = new Lark(JsonGrammar, new LarkOptions { Parser = ParserKind.Lalr });

        Assert.Equal(lark.Parse(text), await lark.ParseAsync(new StringReader(text)));
    }

    [Fact]
    public async Task LexAsync_EqualsLex()
    {
        var lark = new Lark(JsonGrammar, Options(ParserKind.Lalr));
        var expected = lark.Lex(JsonText).ToList();

        var actual = new List<Token>();
        await foreach (var token in lark.LexAsync(new TrickleReader(JsonText)))
        {
            actual.Add(token);
        }

        Assert.Equal(expected, actual);
        Assert.Equal(expected, lark.Lex(new TrickleReader(JsonText)).ToList());
    }

    [Fact]
    public async Task LexAsync_DontIgnore_ReturnsIgnoredTokens()
    {
        var lark = new Lark(JsonGrammar, Options(ParserKind.Lalr));

        var types = new List<string>();
        await foreach (var token in lark.LexAsync(new TrickleReader("[1, 2]"), dontIgnore: true))
        {
            types.Add(token.Type);
        }

        Assert.Contains("WS", types);
    }

    [Fact]
    public async Task LexAsync_Stream_EqualsLex()
    {
        var lark = new Lark(JsonGrammar, new LarkOptions { Parser = ParserKind.Lalr });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonText));

        var actual = new List<Token>();
        await foreach (var token in lark.LexAsync(stream))
        {
            actual.Add(token);
        }

        Assert.Equal(lark.Lex(JsonText).ToList(), actual);
    }
}
