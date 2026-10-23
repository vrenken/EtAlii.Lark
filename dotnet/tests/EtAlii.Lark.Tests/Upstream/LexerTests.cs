// Ported from lark/tests/test_lexer.py.
// ReSharper disable InconsistentNaming
namespace EtAlii.Lark.Tests.Upstream;

public class LexerTests
{
    private static Lark Parser() => new("""
        start: "a" "b" "c" "d"
        %ignore " "
        """);

    [Fact]
    public void test_basic()
    {
        var p = Parser();

        var res = p.Lex("abc cba dd").Select(t => t.Value);
        Assert.Equal("abccbadd".Select(c => c.ToString()), res);

        res = p.Lex("abc cba dd", dontIgnore: true).Select(t => t.Value);
        Assert.Equal("abc cba dd".Select(c => c.ToString()), res);
    }

    [Fact]
    public void test_subset_lex()
    {
        var p = Parser();

        var res = p.Lex(new TextSlice("xxxabc cba ddxx", 3, -2)).Select(t => t.Value);
        Assert.Equal("abccbadd".Select(c => c.ToString()), res);

        res = p.Lex(new TextSlice("aaaabc cba dddd", 3, -2)).Select(t => t.Value);
        Assert.Equal("abccbadd".Select(c => c.ToString()), res);
    }
}
