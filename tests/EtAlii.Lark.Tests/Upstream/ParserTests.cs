// Ported from lark/tests/test_parser.py (class TestParsers).
// ReSharper disable InconsistentNaming
#pragma warning disable IDE1006 // Method names follow the rule names of the grammars, like upstream.
namespace EtAlii.Lark.Tests.Upstream;

using static TestHelpers;

public class ParserTests
{
    private static Lark Lark(string grammar, ParserKind parser = ParserKind.Earley, LexerKind lexer = LexerKind.Auto,
        Transformer? transformer = null, bool propagatePositions = false, Func<LexerConf, Lexer>? customLexer = null) =>
        new(grammar, new LarkOptions
        {
            Parser = parser,
            Lexer = customLexer is not null ? LexerKind.Custom : lexer,
            CustomLexer = customLexer,
            Transformer = transformer,
            PropagatePositions = propagatePositions,
        });

    [Fact]
    public void test_big_list()
    {
        Lark($"start: {string.Join("|", Enumerable.Range(0, 250).Select(i => $"\"{i}\""))}");
    }

    [Fact]
    public void test_same_ast()
    {
        // Tests that Earley and LALR parsers produce equal trees
        var g = Lark("""
            start: "(" name_list ("," "*" NAME)? ")"
            name_list: NAME | name_list "," NAME
            NAME: /\w+/
            """, ParserKind.Lalr);
        var l = g.Parse("(a,b,c,*x)");

        g = Lark("""
            start: "(" name_list ("," "*" NAME)? ")"
            name_list: NAME | name_list "," NAME
            NAME: /\w/+
            """);
        var l2 = g.Parse("(a,b,c,*x)");
        Assert.Equal(l, l2);
    }

    [Fact]
    public void test_infinite_recurse()
    {
        const string g = """
            start: a
            a: a | "a"
            """;
        Assert.Throws<GrammarException>(() => Lark(g, ParserKind.Lalr));
    }

    [Fact]
    public void test_propagate_positions()
    {
        var g = Lark("""
            start: a
            a: "a"
            """, propagatePositions: true);
        var r = g.Parse("a");
        Assert.Equal(1, ((Tree)r.Children[0]!).Meta.Line);

        g = Lark("""
            start: x
            x: a
            a: "a"
            """, propagatePositions: true);
        r = g.Parse("a");
        Assert.Equal(1, ((Tree)r.Children[0]!).Meta.Line);
    }

    [Fact]
    public void test_propagate_positions2()
    {
        var g = Lark("""
            start: a
            a: b
            ?b: "(" t ")"
            !t: "t"
            """, propagatePositions: true);

        var start = g.Parse("(t)");
        var a = (Tree)Assert.Single(start.Children)!;
        var t = (Tree)Assert.Single(a.Children)!;
        AssertEq("t", t.Children[0]);

        Assert.Equal(2, t.Meta.Column);
        Assert.Equal(3, t.Meta.EndColumn);

        Assert.Equal(1, start.Meta.Column);
        Assert.Equal(1, a.Meta.Column);
        Assert.Equal(4, start.Meta.EndColumn);
        Assert.Equal(4, a.Meta.EndColumn);
    }

    [Fact]
    public void test_expand1()
    {
        var g = Lark("""
            start: a
            ?a: b
            b: "x"
            """);
        var r = g.Parse("x");
        Assert.Equal("b", ((Tree)r.Children[0]!).Data);

        g = Lark("""
            start: a
            ?a: b -> c
            b: "x"
            """);
        r = g.Parse("x");
        Assert.Equal("c", ((Tree)r.Children[0]!).Data);

        g = Lark("""
            start: a
            ?a: B -> c
            B: "x"
            """);
        Assert.Equal("c", ((Tree)r.Children[0]!).Data);

        g = Lark("""
            start: a
            ?a: b b -> c
            b: "x"
            """);
        r = g.Parse("xx");
        Assert.Equal("c", ((Tree)r.Children[0]!).Data);
    }

    [Fact]
    public void test_comment_in_rule_definition()
    {
        var g = Lark("""
            start: a
                   a: "a"
                    // A comment
                    // Another comment
                    | "b"
                    // Still more

                   c: "unrelated"
            """);
        var r = g.Parse("b");
        Assert.Equal("a", ((Tree)r.Children[0]!).Data);
    }

    private class VisitTokensT : Transformer
    {
        public VisitTokensT(bool visitTokens = true) : base(visitTokens) { }

        public object a(List<object?> children) => (string)(Token)children[0]! + "!";

        public Token A(Token tok) => tok.Update(value: tok.Value.ToUpperInvariant());
    }

    [Fact]
    public void test_visit_tokens()
    {
        const string g = """
            start: a
            a : A
            A: "x"
            """;
        var p = Lark(g, ParserKind.Lalr);
        var r = new VisitTokensT(false).Transform<Tree>(p.Parse("x"));
        AssertEq(new[] { "x!" }, r.Children);
        r = new VisitTokensT().Transform<Tree>(p.Parse("x"));
        AssertEq(new[] { "X!" }, r.Children);

        // Test internal transformer
        p = Lark(g, ParserKind.Lalr, transformer: new VisitTokensT());
        r = p.Parse("x");
        AssertEq(new[] { "X!" }, r.Children);
    }

    private static object SumChildren(object children) =>
        (children is List<object?> list ? list : ((Tree)children).Children).Sum(c => Convert.ToInt32(c));

    private class VisitTokens2Transformer : Transformer
    {
        public object add(List<object?> children) => SumChildren(children);

        public object NUM(Token token) => int.Parse(token.Value);
    }

    private class VisitTokens2InPlace : InPlaceTransformer
    {
        public object add(List<object?> children) => SumChildren(children);

        public object NUM(Token token) => int.Parse(token.Value);
    }

    private class VisitTokens2NonRecursive : NonRecursiveTransformer
    {
        public object add(List<object?> children) => SumChildren(children);

        public object NUM(Token token) => int.Parse(token.Value);
    }

    private class VisitTokens2InPlaceRecursive : InPlaceRecursiveTransformer
    {
        public object add(List<object?> children) => SumChildren(children);

        public object NUM(Token token) => int.Parse(token.Value);
    }

    [Fact]
    public void test_visit_tokens2()
    {
        const string g = """
            start: add+
            add: NUM "+" NUM
            NUM: /\d+/
            %ignore " "
            """;
        const string text = "1+2 3+4";
        var expected = T("start", 3, 7);
        foreach (var t in new Transformer[] { new VisitTokens2Transformer(), new VisitTokens2InPlace(), new VisitTokens2NonRecursive(), new VisitTokens2InPlaceRecursive() })
        {
            var parser = Lark(g, ParserKind.Lalr, transformer: t);
            var result = parser.Parse(text);
            AssertEq(expected, result);
        }
    }

    [VArgs(Meta = true)]
    private class VArgsMetaT1 : Transformer
    {
        public object? a(Meta meta, List<object?> children)
        {
            Assert.Empty(children);
            return meta.Line;
        }

        public object start(Meta meta, List<object?> children) => children;
    }

    [VArgs(Meta = true, Inline = true)]
    private class VArgsMetaT2 : Transformer
    {
        public object? a(Meta meta) => meta.Line;

        public object start(Meta meta, params object?[] res) => res.ToList();
    }

    [Fact]
    public void test_vargs_meta()
    {
        foreach (var factory in new Func<Transformer>[] { () => new VArgsMetaT1(), () => new VArgsMetaT2() })
        {
            foreach (var @internal in new[] { false, true })
            {
                Lark g;
                try
                {
                    g = Lark("""
                        start: a+
                        a : "x" _NL?
                        _NL: /\n/+
                        """, ParserKind.Lalr, transformer: @internal ? factory() : null, propagatePositions: true);
                }
                catch (NotSupportedException)
                {
                    Assert.True(@internal);
                    continue;
                }

                var res = g.Parse("xx\nx\nxxx\n\n\nxx");
                Assert.False(@internal);
                var result = factory().Transform(res);
                AssertEq(new[] { 1, 1, 2, 3, 3, 3, 6, 6 }, result);
            }
        }
    }

    [VArgs(Tree = true)]
    private class VArgsTreeT : Transformer
    {
        public object a(Tree tree) => 1;

        public object start(Tree tree) => tree.Children;
    }

    [Fact]
    public void test_vargs_tree()
    {
        var tree = Lark("""
            start: a a a
            !a: "A"
            """).Parse("AAA");
        var treeCopy = tree.DeepCopy();

        var res = new VArgsTreeT().Transform(tree);
        AssertEq(new[] { 1, 1, 1 }, res);
        Assert.Equal(treeCopy, tree);
    }

    private class EmbeddedT : Transformer
    {
        public object a(List<object?> children) => "<a>";

        public object b(List<object?> children) => "<b>";

        public object c(List<object?> children) => "<c>";
    }

    [Fact]
    public void test_embedded_transformer()
    {
        // Test regular
        var g = Lark("""
            start: a
            a : "x"
            """, ParserKind.Lalr);
        var r = new EmbeddedT().Transform<Tree>(g.Parse("x"));
        AssertEq(new[] { "<a>" }, r.Children);

        g = Lark("""
            start: a
            a : "x"
            """, ParserKind.Lalr, transformer: new EmbeddedT());
        r = g.Parse("x");
        AssertEq(new[] { "<a>" }, r.Children);

        // Test Expand1
        g = Lark("""
            start: a
            ?a : b
            b : "x"
            """, ParserKind.Lalr);
        r = new EmbeddedT().Transform<Tree>(g.Parse("x"));
        AssertEq(new[] { "<b>" }, r.Children);

        g = Lark("""
            start: a
            ?a : b
            b : "x"
            """, ParserKind.Lalr, transformer: new EmbeddedT());
        r = g.Parse("x");
        AssertEq(new[] { "<b>" }, r.Children);

        // Test Expand1 -> Alias
        g = Lark("""
            start: a
            ?a : b b -> c
            b : "x"
            """, ParserKind.Lalr);
        r = new EmbeddedT().Transform<Tree>(g.Parse("xx"));
        AssertEq(new[] { "<c>" }, r.Children);

        g = Lark("""
            start: a
            ?a : b b -> c
            b : "x"
            """, ParserKind.Lalr, transformer: new EmbeddedT());
        r = g.Parse("xx");
        AssertEq(new[] { "<c>" }, r.Children);
    }

    [VArgs(Tree = true)]
    private class InPlaceT1 : InPlaceTransformer
    {
        public object a(Tree tree)
        {
            tree.Children.Add("tested");
            return tree;
        }

        public object b(Tree tree) => new Tree(tree.Data, tree.Children.Append("tested2").ToList());
    }

    [VArgs(Tree = true)]
    private class InPlaceT2 : Transformer
    {
        public object a(Tree tree)
        {
            tree.Children.Add("tested");
            return tree;
        }

        public object b(Tree tree) => new Tree(tree.Data, tree.Children.Append("tested2").ToList());
    }

    private class InPlaceT3 : Transformer
    {
        [VArgs(Tree = true)]
        public object a(Tree tree)
        {
            tree.Children.Add("tested");
            return tree;
        }

        [VArgs(Tree = true)]
        public object b(Tree tree) => new Tree(tree.Data, tree.Children.Append("tested2").ToList());
    }

    [Fact]
    public void test_embedded_transformer_inplace()
    {
        foreach (var t in new Transformer[] { new InPlaceT1(), new InPlaceT2(), new InPlaceT3() })
        {
            foreach (var @internal in new[] { false, true })
            {
                var g = Lark("""
                    start: a b
                    a : "x"
                    b : "y"
                    """, ParserKind.Lalr, transformer: @internal ? t : null);
                object r = g.ParseObject("xy")!;
                if (!@internal) r = t.Transform((Tree)r)!;

                var children = ((Tree)r).Children;
                Assert.Equal(2, children.Count);
                AssertEq(new[] { "tested" }, ((Tree)children[0]!).Children);
                AssertEq(new[] { "tested2" }, ((Tree)children[1]!).Children);
            }
        }
    }

    [Fact]
    public void test_alias()
    {
        Lark("""start: ["a"] "b" ["c"] "e" ["f"] ["g"] ["h"] "x" -> d """);
    }

    private class OldCustomLexer : Lexer
    {
        public override IEnumerable<Token> Lex(LexerState lexerState, object? parserState)
        {
            yield return new Token("A", "A");
        }
    }

    [Fact]
    public void test_backwards_custom_lexer()
    {
        var p = Lark("""
            start: A
            %declare A
            """, ParserKind.Lalr, customLexer: _ => new OldCustomLexer());

        var r = p.Parse("");
        Assert.Equal(T("start", Tok("A", "A")), r);
    }

    [Fact]
    public void test_lexer_token_limit()
    {
        // Python has a limit of 100 groups in a regular expression. Test that we handle many terminals.
        var tokens = Enumerable.Range(0, 300).ToDictionary(i => $"A{i}", i => $"\"{i}\"");
        var g = $"start: {string.Join(" ", tokens.Keys)}\n{string.Join("\n", tokens.Select(x => $"{x.Key}: {x.Value}"))}";
        Lark(g, ParserKind.Lalr);
    }

    private class TypeLexer : Lexer
    {
        public override IEnumerable<Token> Lex(LexerState lexerState, object? parserState)
        {
            foreach (var obj in (IEnumerable<object>)lexerState.CustomInput!)
            {
                var t = obj switch
                {
                    string => "STR",
                    int => "INT",
                    _ => obj.GetType().Name.ToUpperInvariant(),
                };
                yield return new Token(t, obj.ToString()!);
            }
        }
    }

    private class ParseToDict : Transformer
    {
        [VArgs(Inline = true)]
        public object data_item(Token name, params Token[] numbers) =>
            new KeyValuePair<string, List<int>>(name.Value, numbers.Select(n => int.Parse(n.Value)).ToList());

        public object start(List<object?> items) =>
            items.Cast<KeyValuePair<string, List<int>>>().ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    [Fact]
    public void test_custom_input()
    {
        var parser = Lark("""
            start: data_item+
            data_item: STR INT*

            %declare STR INT
            """, ParserKind.Lalr, customLexer: _ => new TypeLexer());

        var data = new object[] { "alice", 1, 27, 3, "bob", 4, "carrie", "dan", 8, 6 };
        var tree = (Tree)parser.ParseObject(data)!;
        var res = (Dictionary<string, List<int>>)new ParseToDict().Transform(tree)!;
        Assert.Equal(new[] { 1, 27, 3 }, res["alice"]);
        Assert.Equal(new[] { 4 }, res["bob"]);
        Assert.Empty(res["carrie"]);
        Assert.Equal(new[] { 8, 6 }, res["dan"]);
        Assert.Equal(4, res.Count);
    }
}
