// Ported from lark/tests/test_reconstructor.py.
// ReSharper disable InconsistentNaming
#pragma warning disable IDE1006
namespace EtAlii.Lark.Tests.Upstream;

using System.Text.Json.Nodes;

public class ReconstructorTests
{
    private const string Common = """

        %import common (WS_INLINE, NUMBER, WORD)
        %ignore WS_INLINE
        """;

    private static string RemoveWs(string s) => s.Replace(" ", "").Replace("\n", "").Replace("\r", "");

    private static Lark Lalr(string grammar, bool keepAllTokens = false) =>
        new(grammar, new LarkOptions { Parser = ParserKind.Lalr, MaybePlaceholders = false, KeepAllTokens = keepAllTokens });

    private static void AssertReconstruct(string grammar, string code, bool keepAllTokens = false)
    {
        var parser = Lalr(grammar, keepAllTokens);
        var tree = parser.Parse(code);
        var @new = new Reconstructor(parser).Reconstruct(tree);
        Assert.Equal(RemoveWs(code), RemoveWs(@new));
    }

    [Fact]
    public void test_starred_rule()
    {
        const string g = """
            start: item*
            item: NL
                | rule
            rule: WORD ":" NUMBER
            NL: /(\r?\n)+\s*/
            """ + Common;

        const string code = """

            Elephants: 12

            """;

        AssertReconstruct(g, code);
    }

    [Fact]
    public void test_starred_group()
    {
        const string g = """
            start: (rule | NL)*
            rule: WORD ":" NUMBER
            NL: /(\r?\n)+\s*/
            """ + Common;

        const string code = """

            Elephants: 12

            """;

        AssertReconstruct(g, code);
    }

    [Fact]
    public void test_alias()
    {
        const string g = """
            start: line*
            line: NL
                | rule
                | "hello" -> hi
            rule: WORD ":" NUMBER
            NL: /(\r?\n)+\s*/
            """ + Common;

        const string code = """

            Elephants: 12
            hello

            """;

        AssertReconstruct(g, code);
    }

    [Fact]
    public void test_keep_tokens()
    {
        const string g = """
            start: (NL | stmt)*
            stmt: var op var
            !op: ("+" | "-" | "*" | "/")
            var: WORD
            NL: /(\r?\n)+\s*/
            """ + Common;

        const string code = """

            a+b

            """;

        AssertReconstruct(g, code);
    }

    [Fact]
    public void test_expand_rule()
    {
        const string g = """
            ?start: (NL | mult_stmt)*
            ?mult_stmt: sum_stmt ["*" sum_stmt]
            ?sum_stmt: var ["+" var]
            var: WORD
            NL: /(\r?\n)+\s*/
            """ + Common;

        foreach (var c in new[] { "a", "a*b", "a+b", "a*b+c", "a+b*c", "a+b*c+d" })
        {
            AssertReconstruct(g, c);
        }
    }

    [Fact]
    public void test_json_example()
    {
        const string testJson = """

                {
                    "empty_object" : {},
                    "empty_array"  : [],
                    "booleans"     : { "YES" : true, "NO" : false },
                    "numbers"      : [ 0, 1, -2, 3.3, 4.4e5, 6.6e-7 ],
                    "strings"      : [ "This", [ "And" , "That", "And a \"b" ] ],
                    "nothing"      : null
                }

            """;

        const string jsonGrammar = """
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

        var jsonParser = Lalr(jsonGrammar);
        var tree = jsonParser.Parse(testJson);

        var newJson = new Reconstructor(jsonParser).Reconstruct(tree);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(testJson), JsonNode.Parse(newJson)), newJson);
    }

    [Fact]
    public void test_keep_all_tokens()
    {
        const string g = """
            start: "a"? _B? c? _d?
            _B: "b"
            c: "c"
            _d: "d"
            """;
        var examples =
            from a in new[] { "", "a" }
            from b in new[] { "", "b" }
            from c in new[] { "", "c" }
            from d in new[] { "", "d" }
            select a + b + c + d;
        foreach (var code in examples)
        {
            AssertReconstruct(g, code, keepAllTokens: true);
        }
    }

    [Fact]
    public void test_switch_grammar_unicode_terminal()
    {
        // This test checks that a parse tree built with a grammar containing only ascii characters can be reconstructed
        // with a grammar that has unicode rules (or vice versa). The original bug assigned ANON terminals to unicode
        // keywords, which offsets the ANON terminal count in the unicode grammar and causes subsequent identical ANON
        // tokens (e.g., `+=`) to mismatch between the two grammars.
        const string g1 = """
            start: (NL | stmt)*
            stmt: "keyword" var op var
            !op: ("+=" | "-=" | "*=" | "/=")
            var: WORD
            NL: /(\r?\n)+\s*/
            """ + Common;

        const string g2 = """
            start: (NL | stmt)*
            stmt: "குறிப்பு" var op var
            !op: ("+=" | "-=" | "*=" | "/=")
            var: WORD
            NL: /(\r?\n)+\s*/
            """ + Common;

        const string code = """

            keyword x += y

            """;

        var l1 = Lalr(g1);
        var l2 = Lalr(g2);
        var r = new Reconstructor(l2);

        var tree = l1.Parse(code);
        var code2 = r.Reconstruct(tree);
        Assert.Equal(tree, l2.Parse(code2));
    }
}
