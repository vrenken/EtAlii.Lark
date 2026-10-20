// Ported from lark/tests/test_pattern_matching.py, using C# positional, property and list patterns.
// ReSharper disable InconsistentNaming
#pragma warning disable IDE1006
namespace EtAlii.Lark.Tests.Upstream;

using static TestHelpers;

public class PatternMatchingTests
{
    private readonly Token _token = Tok("A", "a");

    [Fact]
    public void test_matches_with_string()
    {
        Assert.True(_token is { Value: "a" });
        Assert.True(_token.Equals("a"));
    }

    [Fact]
    public void test_matches_with_token_positional_arg()
    {
        var result = _token switch
        {
            ("a", _) => false,
            ("A", _) => true,
            _ => false,
        };
        Assert.True(result);
    }

    [Fact]
    public void test_matches_with_token_kwarg_type()
    {
        Assert.True(_token is { Type: "A" });
    }

    [Fact]
    public void test_matches_with_bad_token_type()
    {
        Assert.False(_token is { Type: "B" });
    }

    [Fact]
    public void test_match_on_tree()
    {
        var tree1 = T("a", T("b", "x"), T("c", "y"), T("d", "z"));
        var tree2 = T("a",
            T("b", Tok("T", "x")),
            T("c", Tok("T", "y")),
            T("d", T("z", Tok("T", "zz"), T("zzz", "z", "z", "z"))));

        Assert.False(tree1 is ("X", []));
        Assert.False(tree1 is ("a", []));
        Assert.False(tree1 is (_, ["b"]));
        Assert.False(tree1 is ("X", _));

        var tree = T("q", Tok("T", "x"));
        Assert.True(tree is ("q", [Token("T", "x")]));

        var tr = T("a", T("b", Tok("T", "a")));
        Assert.True(tr is ("a", [Tree("b", [Token("T", "a")])]));

        // test nested trees
        Assert.True(tree2 is ("a", [
            Tree("b", [Token("T", "x")]),
            Tree("c", [Token("T", "y")]),
            Tree("d", [
                Tree("z", [
                    Token("T", "zz"),
                    Tree("zzz", ["z", "z", "z"]),
                ]),
            ]),
        ]));
    }
}
