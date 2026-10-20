namespace EtAlii.Lark.Tests;

using Xunit;

public class SmokeTests
{
    private const string Grammar = """
        start: WORD "," WORD "!"
        %import common.WORD
        %ignore " "
        """;

    [Theory]
    [InlineData(ParserKind.Lalr, LexerKind.Contextual)]
    [InlineData(ParserKind.Lalr, LexerKind.Basic)]
    [InlineData(ParserKind.Earley, LexerKind.Dynamic)]
    [InlineData(ParserKind.Earley, LexerKind.Basic)]
    [InlineData(ParserKind.Earley, LexerKind.DynamicComplete)]
    [InlineData(ParserKind.Cyk, LexerKind.Basic)]
    public void HelloWorld(ParserKind parser, LexerKind lexer)
    {
        var lark = new Lark(Grammar, new LarkOptions { Parser = parser, Lexer = lexer });
        var tree = lark.Parse("Hello, World!");
        Assert.Equal(Tree.Of("start", new Token("WORD", "Hello"), new Token("WORD", "World")), tree);
    }
}
