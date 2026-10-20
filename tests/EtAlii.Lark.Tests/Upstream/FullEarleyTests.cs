// Ported from lark/tests/test_parser.py (_make_full_earley_test).
// Tests that upstream skips for a lexer return early here.
namespace EtAlii.Lark.Tests.Upstream;

using EtAlii.Lark.Parsers;
using static TestHelpers;

public abstract class FullEarleyTests
{
    protected abstract LexerKind LexerKind { get; }

    private Lark Lark(string grammar, Ambiguity ambiguity = Ambiguity.Auto, string start = "start", bool propagatePositions = false) =>
        new(grammar, new LarkOptions
        {
            Parser = ParserKind.Earley,
            Lexer = LexerKind,
            Ambiguity = ambiguity,
            Start = new[] { start },
            PropagatePositions = propagatePositions,
        });

    private static void AssertAmbig(Tree ambigTree, string expected)
    {
        Assert.Equal("_ambig", ambigTree.Data);
        SetAssert.Equal(Py.List(expected), ambigTree.Children);
    }

    [Fact]
    public void test_anon()
    {
        // Fails an Earley implementation without special handling for empty rules,
        // or re-processing of already completed rules.
        var g = Lark("""
            start: B
            B: ("ab"|/[^b]/)+
            """);
        AssertEq("abc", g.Parse("abc").Children[0]);
    }

    [Fact]
    public void test_earley()
    {
        if (LexerKind == LexerKind.Basic) return; // Requires dynamic lexer
        var g = Lark("""
            start: A "b" c
            A: "a"+
            c: "abc"
            """);
        g.Parse("aaaababc");
    }

    [Fact]
    public void test_earley2()
    {
        const string grammar = """
            start: statement+

            statement: "r"
                     | "c" /[a-z]/+

            %ignore " "
            """;
        Lark(grammar).Parse("c b r");
    }

    [Fact]
    public void test_earley3()
    {
        if (LexerKind != LexerKind.DynamicComplete) return; // Only relevant for the dynamic_complete parser
        // Tests prioritization and disambiguation for pseudo-terminals (there should be only one result)
        // By default, `+` should imitate regexp greedy-matching
        var l = Lark("""
            start: A A
            A: "a"+
            """);
        var res = l.Parse("aaa");
        SetAssert.Equal(new object[] { "aa", "a" }, res.Children);
    }

    [Fact]
    public void test_earley4()
    {
        var l = Lark("""
            start: A A?
            A: "a"+
            """);
        var res = l.Parse("aaa");
        var children = res.Children.Select(c => c!.ToString()).ToList();
        Assert.True(children.ToHashSet().SetEquals(new[] { "aa", "a" }) || children.SequenceEqual(new[] { "aaa" }));
    }

    [Fact]
    public void test_earley_repeating_empty()
    {
        // This was a sneaky bug!
        var parser = Lark("""
            !start: "a" empty empty "b"
            empty: empty2
            empty2:
            """);
        var res = parser.Parse("ab");

        var emptyTree = T("empty", T("empty2"));
        AssertEq(new object[] { "a", emptyTree, emptyTree, "b" }, res.Children);
    }

    [Fact]
    public void test_earley_explicit_ambiguity()
    {
        if (LexerKind == LexerKind.Basic) return; // Requires dynamic lexer
        var parser = Lark("""
            start: a b | ab
            a: "a"
            b: "b"
            ab: "ab"
            """, Ambiguity.Explicit);
        var ambigTree = parser.Parse("ab");
        Assert.Equal("_ambig", ambigTree.Data);
        Assert.Equal(2, ambigTree.Children.Count);
    }

    [Fact]
    public void test_ambiguity1()
    {
        if (LexerKind == LexerKind.Basic) return; // Requires dynamic lexer
        var l = Lark("""
            start: cd+ "e"

            !cd: "c"
               | "d"
               | "cd"
            """, Ambiguity.Explicit);
        var ambigTree = l.Parse("cde");
        Assert.Equal("_ambig", ambigTree.Data);
        Assert.Equal(2, ambigTree.Children.Count);
    }

    [Fact]
    public void test_ambiguity2()
    {
        if (LexerKind == LexerKind.Basic) return; // Requires dynamic lexer
        var l = Lark("""
            ANY:  /[a-zA-Z0-9 ]+/
            a.2: "A" b+
            b.2: "B"
            c:   ANY

            start: (a|c)*
            """);
        var res = l.Parse("ABX");
        AssertEq(Py.Tree("Tree('start', [Tree('a', [Tree('b', [])]), Tree('c', ['X'])])"), res);
    }

    [Fact]
    public void test_ambiguous_inlined_rule()
    {
        var l = Lark("""
            start: _field+
            _field: f1 | f2 | f3
            f1: INT
            f2: INT "M"?
            f3: INT "M"
            %import common.INT
            """, Ambiguity.Explicit);
        AssertAmbig(l.Parse("1M2"), """
            {
                Tree('start', [Tree('f2', ['1']), Tree('f1', ['2'])]),
                Tree('start', [Tree('f2', ['1']), Tree('f2', ['2'])]),
                Tree('start', [Tree('f3', ['1']), Tree('f1', ['2'])]),
                Tree('start', [Tree('f3', ['1']), Tree('f2', ['2'])]),
            }
            """);
    }

    [Fact]
    public void test_ambiguous_intermediate_node()
    {
        var l = Lark("""
            start: ab bc d?
            !ab: "A" "B"?
            !bc: "B"? "C"
            !d: "D"
            """, Ambiguity.Explicit);
        AssertAmbig(l.Parse("ABCD"), """
            {
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B', 'C']), Tree('d', ['D'])]),
                Tree('start', [Tree('ab', ['A', 'B']), Tree('bc', ['C']), Tree('d', ['D'])])
            }
            """);
    }

    [Fact]
    public void test_ambiguous_symbol_and_intermediate_nodes()
    {
        var l = Lark("""
            start: ab bc cd
            !ab: "A" "B"?
            !bc: "B"? "C"?
            !cd: "C"? "D"
            """, Ambiguity.Explicit);
        AssertAmbig(l.Parse("ABCD"), """
            {
                Tree('start', [Tree('ab', ['A', 'B']), Tree('bc', ['C']), Tree('cd', ['D'])]),
                Tree('start', [Tree('ab', ['A', 'B']), Tree('bc', []), Tree('cd', ['C', 'D'])]),
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B', 'C']), Tree('cd', ['D'])]),
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B']), Tree('cd', ['C', 'D'])]),
            }
            """);
    }

    [Fact]
    public void test_nested_ambiguous_intermediate_nodes()
    {
        var l = Lark("""
            start: ab bc cd e?
            !ab: "A" "B"?
            !bc: "B"? "C"?
            !cd: "C"? "D"
            !e: "E"
            """, Ambiguity.Explicit);
        AssertAmbig(l.Parse("ABCDE"), """
            {
                Tree('start', [Tree('ab', ['A', 'B']), Tree('bc', ['C']), Tree('cd', ['D']), Tree('e', ['E'])]),
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B', 'C']), Tree('cd', ['D']), Tree('e', ['E'])]),
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B']), Tree('cd', ['C', 'D']), Tree('e', ['E'])]),
                Tree('start', [Tree('ab', ['A', 'B']), Tree('bc', []), Tree('cd', ['C', 'D']), Tree('e', ['E'])]),
            }
            """);
    }

    [Fact]
    public void test_nested_ambiguous_intermediate_nodes2()
    {
        var l = Lark("""
            start: ab bc cd de f
            !ab: "A" "B"?
            !bc: "B"? "C"?
            !cd: "C"? "D"?
            !de: "D"? "E"
            !f: "F"
            """, Ambiguity.Explicit);
        AssertAmbig(l.Parse("ABCDEF"), """
            {
                Tree('start', [Tree('ab', ['A', 'B']), Tree('bc', ['C']), Tree('cd', ['D']), Tree('de', ['E']), Tree('f', ['F'])]),
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B', 'C']), Tree('cd', ['D']), Tree('de', ['E']), Tree('f', ['F'])]),
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B']), Tree('cd', ['C', 'D']), Tree('de', ['E']), Tree('f', ['F'])]),
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B']), Tree('cd', ['C']), Tree('de', ['D', 'E']), Tree('f', ['F'])]),
                Tree('start', [Tree('ab', ['A', "B"]), Tree('bc', []), Tree('cd', ['C']), Tree('de', ['D', 'E']), Tree('f', ['F'])]),
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B', 'C']), Tree('cd', []), Tree('de', ['D', 'E']), Tree('f', ['F'])]),
                Tree('start', [Tree('ab', ['A', 'B']), Tree('bc', []), Tree('cd', ['C', 'D']), Tree('de', ['E']), Tree('f', ['F'])]),
                Tree('start', [Tree('ab', ['A', 'B']), Tree('bc', ['C']), Tree('cd', []), Tree('de', ['D', 'E']), Tree('f', ['F'])]),
            }
            """);
    }

    [Fact]
    public void test_ambiguous_intermediate_node_unnamed_token()
    {
        var l = Lark("""
            start: ab bc "D"
            !ab: "A" "B"?
            !bc: "B"? "C"
            """, Ambiguity.Explicit);
        AssertAmbig(l.Parse("ABCD"), """
            {
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B', 'C'])]),
                Tree('start', [Tree('ab', ['A', 'B']), Tree('bc', ['C'])])
            }
            """);
    }

    [Fact]
    public void test_ambiguous_intermediate_node_inlined_rule()
    {
        var l = Lark("""
            start: ab _bc d?
            !ab: "A" "B"?
            _bc: "B"? "C"
            !d: "D"
            """, Ambiguity.Explicit);
        AssertAmbig(l.Parse("ABCD"), """
            {
                Tree('start', [Tree('ab', ['A']), Tree('d', ['D'])]),
                Tree('start', [Tree('ab', ['A', 'B']), Tree('d', ['D'])])
            }
            """);
    }

    [Fact]
    public void test_ambiguous_intermediate_node_conditionally_inlined_rule()
    {
        var l = Lark("""
            start: ab bc d?
            !ab: "A" "B"?
            !?bc: "B"? "C"
            !d: "D"
            """, Ambiguity.Explicit);
        AssertAmbig(l.Parse("ABCD"), """
            {
                Tree('start', [Tree('ab', ['A']), Tree('bc', ['B', 'C']), Tree('d', ['D'])]),
                Tree('start', [Tree('ab', ['A', 'B']), 'C', Tree('d', ['D'])])
            }
            """);
    }

    [Fact]
    public void test_fruitflies_ambig()
    {
        if (LexerKind == LexerKind.Basic) return; // Requires dynamic lexer
        var parser = Lark("""
                start: noun verb noun        -> simple
                        | noun verb "like" noun -> comparative

                noun: adj? NOUN
                verb: VERB
                adj: ADJ

                NOUN: "flies" | "bananas" | "fruit"
                VERB: "like" | "flies"
                ADJ: "fruit"

                %import common.WS
                %ignore WS
            """, Ambiguity.Explicit);
        var tree = parser.Parse("fruit flies like bananas");

        var expected = Py.Tree("""
            Tree('_ambig', [
                    Tree('comparative', [
                        Tree('noun', ['fruit']),
                        Tree('verb', ['flies']),
                        Tree('noun', ['bananas'])
                    ]),
                    Tree('simple', [
                        Tree('noun', [Tree('adj', ['fruit']), 'flies']),
                        Tree('verb', ['like']),
                        Tree('noun', ['bananas'])
                    ])
                ])
            """);
        Assert.Equal(expected.Data, tree.Data);
        SetAssert.Equal(expected.Children, tree.Children);
    }

    [Fact]
    public void test_explicit_ambiguity2()
    {
        if (LexerKind != LexerKind.DynamicComplete) return; // Only relevant for the dynamic_complete parser
        var parser = Lark("""
            start: NAME+
            NAME: /\w+/
            %ignore " "
            """, Ambiguity.Explicit, propagatePositions: true);
        var tree = parser.Parse("cat");
        Assert.Equal("_ambig", tree.Data);

        var combinations = tree.Children.Cast<Tree>().Select(t => string.Join("|", t.Children.Select(s => s!.ToString()))).ToHashSet();
        Assert.True(combinations.SetEquals(new[] { "cat", "ca|t", "c|at", "c|a|t" }), string.Join(", ", combinations));
    }

    [Fact]
    public void test_term_ambig_resolve()
    {
        var parser = new Lark("""
            !start: NAME+
            NAME: /\w+/
            %ignore " "
            """);
        var tree = parser.Parse("foo bar");
        AssertEq(new[] { "foo", "bar" }, tree.Children);
    }

    [Fact]
    public void test_multiple_start_solutions()
    {
        const string grammar = """
            !start: a | A
            !a: A
            A: "x"
            """;

        var l = Lark(grammar, Ambiguity.Explicit);
        var tree = l.Parse("x");
        AssertEq(Py.Tree("""
            Tree('_ambig', [
                Tree('start', [Tree('a', ['x'])]),
                Tree('start', ['x']),
            ])
            """), tree);

        l = Lark(grammar, Ambiguity.Resolve);
        tree = l.Parse("x");
        AssertEq(Py.Tree("Tree('start', [Tree('a', ['x'])])"), tree);
    }

    [Fact]
    public void test_cycle()
    {
        const string grammar = """
            start: start?
            """;

        var l = Lark(grammar, Ambiguity.Resolve);
        AssertEq(T("start"), l.Parse(""));

        l = Lark(grammar, Ambiguity.Explicit);
        AssertEq(T("start"), l.Parse(""));
    }

    [Fact]
    public void test_cycle2()
    {
        var l = Lark("""
            start: _recurse
            _recurse:  v
            v: "b"
                  | "a" v
                  | _recurse
            """, Ambiguity.Explicit);
        var tree = l.Parse("ab");
        AssertEq(Py.Tree("Tree('start', [Tree('v', [Tree('v', [])])])"), tree);
    }

    [Fact]
    public void test_cycles()
    {
        const string grammar = """
            a: b
            b: c*
            c: a
            """;

        var l = Lark(grammar, Ambiguity.Resolve, start: "a");
        AssertEq(Py.Tree("Tree('a', [Tree('b', [])])"), l.Parse(""));

        l = Lark(grammar, Ambiguity.Explicit, start: "a");
        AssertEq(Py.Tree("Tree('a', [Tree('b', [])])"), l.Parse(""));
    }

    [Fact]
    public void test_many_cycles()
    {
        const string grammar = """
            start: a? | start start
            !a: "a"
            """;

        var l = Lark(grammar, Ambiguity.Resolve);
        AssertEq(Py.Tree("Tree('start', [Tree('a', ['a'])])"), l.Parse("a"));

        l = Lark(grammar, Ambiguity.Explicit);
        AssertEq(Py.Tree("Tree('start', [Tree('a', ['a'])])"), l.Parse("a"));
    }

    [Fact]
    public void test_cycles_with_child_filter()
    {
        const string grammar = """
            a: _x
            _x: _x? b
            b:
            """;

        const string grammar2 = """
            a: x
            x: x? b
            b:
            """;

        AssertEq(Py.Tree("Tree('a', [Tree('b', [])])"), Lark(grammar, Ambiguity.Resolve, "a").Parse(""));
        AssertEq(Py.Tree("Tree('a', [Tree('b', [])])"), Lark(grammar, Ambiguity.Explicit, "a").Parse(""));
        AssertEq(Py.Tree("Tree('a', [Tree('x', [Tree('b', [])])])"), Lark(grammar2, Ambiguity.Resolve, "a").Parse(""));
        AssertEq(Py.Tree("Tree('a', [Tree('x', [Tree('b', [])])])"), Lark(grammar2, Ambiguity.Explicit, "a").Parse(""));
    }

    [Fact]
    public void test_symbol_node_start_end_dynamic_lexer()
    {
        if (LexerKind == LexerKind.Basic) return; // start/end values work differently for the basic lexer

        var l = Lark("""
            start: "ABC"
            """, Ambiguity.Forest);
        var node = l.Parse<SymbolNode>("ABC");
        Assert.Equal(0, node.Start);
        Assert.Equal(3, node.End);

        l = Lark("""
            start: abc
            abc: "ABC"
            """, Ambiguity.Forest);
        node = l.Parse<SymbolNode>("ABC");
        Assert.Equal(0, node.Start);
        Assert.Equal(3, node.End);
    }

    [Fact]
    public void test_resolve_ambiguity_with_shared_node()
    {
        var l = Lark("""
            start: (a+)*
            !a.1: "A" |
            """, Ambiguity.Resolve);
        var tree = l.Parse("A");
        AssertEq(Py.Tree("Tree('start', [Tree('a', []), Tree('a', []), Tree('a', ['A'])])"), tree);
    }

    [Fact]
    public void test_resolve_ambiguity_with_shared_node2()
    {
        var l = Lark("""
            start: _s x _s
            x: "X"?
            _s: " "?
            """, Ambiguity.Resolve);
        var tree = l.Parse("");
        AssertEq(Py.Tree("Tree('start', [Tree('x', [])])"), tree);
    }

    [Fact]
    public void test_consistent_derivation_order1()
    {
        // Should return the same result for any hash-seed
        var parser = Lark("""
            start: a a
            a: "." | b
            b: "."
            """);

        var tree = parser.Parse("..");
        var n = T("a");
        AssertEq(T("start", n, n), tree);
    }
}

public class FullEarleyBasicTests : FullEarleyTests
{
    protected override LexerKind LexerKind => LexerKind.Basic;
}

public class FullEarleyDynamicTests : FullEarleyTests
{
    protected override LexerKind LexerKind => LexerKind.Dynamic;
}

public class FullEarleyDynamicCompleteTests : FullEarleyTests
{
    protected override LexerKind LexerKind => LexerKind.DynamicComplete;
}
