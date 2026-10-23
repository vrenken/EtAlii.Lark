// Ported from lark/tests/test_grammar.py.
// ReSharper disable InconsistentNaming
namespace EtAlii.Lark.Tests.Upstream;

using static TestHelpers;

public class GrammarTests
{
    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    // Upstream passes source_path=__file__, so that relative imports resolve next to the test file.
    private static string SourcePath => Path.Combine(FixturesDir, "test_grammar.cs");

    private static Lark Lark(string grammar, Action<LarkOptions>? configure = null)
    {
        var options = new LarkOptions();
        configure?.Invoke(options);
        return new Lark(grammar, options);
    }

    [Fact]
    public void test_errors()
    {
        foreach (var (msg, examples) in GrammarTools.GrammarErrors)
        {
            foreach (var example in examples)
            {
                var e = Assert.Throws<GrammarException>(() => Lark(example));
                Assert.Contains(msg, e.Message);
            }
        }
    }

    [Fact]
    public void test_empty_literal()
    {
        // Issues #888
        Assert.Throws<GrammarException>(() => Lark("start: \"\""));
    }

    [Fact]
    public void test_ignore_name()
    {
        var spaces = new List<Token>();
        var p = Lark("""
            start: "a" "b"
            WS: " "
            %ignore WS
            """, o =>
        {
            o.Parser = ParserKind.Lalr;
            o.LexerCallbacks["WS"] = t =>
            {
                spaces.Add(t);
                return t;
            };
        });
        Assert.Equal(p.Parse("a b"), p.Parse("a    b"));
        Assert.Equal(5, spaces.Count);
    }

    [Fact]
    public void test_override_rule()
    {
        // Overrides the 'sep' template in existing grammar to add an optional terminating delimiter
        // Thus extending it beyond its original capacity
        var p = Lark("""
            %import .test_templates_import (start, sep)

            %override sep{item, delim}: item (delim item)* delim?
            %ignore " "
            """, o => o.SourcePath = SourcePath);

        var a = p.Parse("[1, 2, 3]");
        var b = p.Parse("[1, 2, 3, ]");
        Assert.Equal(a, b);

        Assert.Throws<GrammarException>(() => Lark("""
            %import .test_templates_import (start, sep)

            %override sep{item}: item (delim item)* delim?
            """, o => o.SourcePath = SourcePath));

        Assert.Throws<GrammarException>(() => Lark("""
            %override sep{item}: item (delim item)* delim?
            """, o => o.SourcePath = SourcePath));
    }

    [Fact]
    public void test_override_terminal()
    {
        var p = Lark("""

            %import .grammars.ab (startab, A, B)

            %override A: "c"
            %override B: "d"
            """, o =>
        {
            o.Start = new[] { "startab" };
            o.SourcePath = SourcePath;
        });

        var a = p.Parse("cd");
        AssertEq(new[] { Tok("A", "c"), Tok("B", "d") }, ((Tree)a.Children[0]!).Children);
    }

    [Fact]
    public void test_extend_rule()
    {
        var p = Lark("""
            %import .grammars.ab (startab, A, B, expr)

            %extend expr: B A
            """, o =>
        {
            o.Start = new[] { "startab" };
            o.SourcePath = SourcePath;
        });
        var a = p.Parse("abab");
        AssertEq(Py.List("['a', Tree('expr', ['b', 'a']), 'b']"), ((Tree)a.Children[0]!).Children);

        Assert.Throws<GrammarException>(() => Lark("""
            %extend expr: B A
            """));
    }

    [Fact]
    public void test_extend_term()
    {
        var p = Lark("""
            %import .grammars.ab (startab, A, B, expr)

            %extend A: "c"
            """, o =>
        {
            o.Start = new[] { "startab" };
            o.SourcePath = SourcePath;
        });
        var a = p.Parse("acbb");
        AssertEq(Py.List("['a', Tree('expr', ['c', 'b']), 'b']"), ((Tree)a.Children[0]!).Children);
    }

    [Fact]
    public void test_extend_twice()
    {
        var p = Lark("""
            start: x+

            x: "a"
            %extend x: "b"
            %extend x: "c"
            """);

        Assert.Equal(p.Parse("abccbba"), p.Parse("cbabbbb"));
    }

    [Fact]
    public void test_undefined_ignore()
    {
        Assert.Throws<GrammarException>(() => Lark("""
            !start: "A"

            %ignore B
            """));

        Assert.Throws<GrammarException>(() => Lark("""
            !start: "A"

            %ignore start
            """));
    }

    [Fact]
    public void test_alias_in_terminal()
    {
        Assert.Throws<GrammarException>(() => Lark("""
            start: TERM
            TERM: "a" -> alias
            """));
    }

    [Fact]
    public void test_undefined_rule()
    {
        Assert.Throws<GrammarException>(() => Lark("start: a"));
    }

    [Fact]
    public void test_undefined_term()
    {
        Assert.Throws<GrammarException>(() => Lark("start: A"));
    }

    [Fact]
    public void test_token_multiline_only_works_with_x_flag()
    {
        Assert.Throws<GrammarException>(() => Lark("""
            start: ABC
            ABC: /  a      b c
                        d
                        e f
                    /i
            """));
    }

    [Fact]
    public void test_import_custom_sources()
    {
        var customLoader = new EmbeddedResourceGrammarLoader(typeof(GrammarTests).Assembly, new[] { "Fixtures/grammars" });

        var p = Lark("""
            start: startab

            %import ab.startab
            """, o => o.ImportPaths.Add(customLoader));
        AssertEq(Py.Tree("Tree('start', [Tree('startab', [Tree('ab__expr', [Token('ab__A', 'a'), Token('ab__B', 'b')])])])"), p.Parse("ab"));
    }

    [Fact]
    public void test_import_custom_sources2()
    {
        var customLoader = new EmbeddedResourceGrammarLoader(typeof(GrammarTests).Assembly, new[] { "Fixtures/grammars" });

        var p = Lark("""
            start: rule_to_import

            %import test_relative_import_of_nested_grammar__grammar_to_import.rule_to_import
            """, o => o.ImportPaths.Add(customLoader));
        var x = p.Parse("N");
        AssertEq(new[] { "N" }, x.FindData("rule_to_import").First().Children);
    }

    [Fact]
    public void test_import_custom_sources3()
    {
        var customLoader2 = new EmbeddedResourceGrammarLoader(typeof(GrammarTests).Assembly);
        var p = Lark("""
            %import .test_relative_import (start, WS)
            %ignore WS
            """, o =>
        {
            o.ImportPaths.Add(customLoader2);
            o.SourcePath = SourcePath; // import relative to current file
        });
        var x = p.Parse("12 capybaras");
        AssertEq(new[] { "12", "capybaras" }, x.Children);
    }

    [Fact]
    public void test_find_grammar_errors()
    {
        var text = """

            a: rule
            b rule
            c: rule
            B.: "hello" f
            D: "okay"

            """;
        Assert.Equal(new[] { 3, 5 }, GrammarTools.FindGrammarErrors(text).Select(e => e.Error.Line));

        text = """

            a: rule
            b rule
            | ok
            c: rule
            B.: "hello" f
            D: "okay"

            """;
        Assert.Equal(new[] { 3, 4, 6 }, GrammarTools.FindGrammarErrors(text).Select(e => e.Error.Line));

        text = """

            a: rule @#$#@$@&&
            b: rule
            | ok
            c: rule
            B: "hello" f @
            D: "okay"

            """;
        Assert.Equal(new[] { 2, 6 }, GrammarTools.FindGrammarErrors(text).Select(e => e.Error.Line));
    }

    [Fact]
    public void test_ranged_repeat_terms()
    {
        var l = Lark("""
            !start: AAA
            AAA: "A"~3
            """, o => o.Parser = ParserKind.Lalr);
        AssertEq(T("start", "AAA"), l.Parse("AAA"));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("AA"));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("AAAA"));

        l = Lark("""
            !start: AABB CC
            AABB: "A"~0..2 "B"~2
            CC: "C"~1..2
            """, o => o.Parser = ParserKind.Lalr);
        AssertEq(T("start", "AABB", "CC"), l.Parse("AABBCC"));
        AssertEq(T("start", "BB", "C"), l.Parse("BBC"));
        AssertEq(T("start", "ABB", "CC"), l.Parse("ABBCC"));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("AAAB"));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("AAABBB"));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("ABB"));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("AAAABB"));
    }

    [Fact]
    public void test_ranged_repeat_large()
    {
        var l = Lark("""
            !start: "A"~60
            """, o => o.Parser = ParserKind.Lalr);
        Assert.True(l.Rules.Count > 1, "Expected that more than one rule will be generated");
        AssertEq(new Tree("start", Enumerable.Repeat<object?>("A", 60).ToList()), l.Parse(new string('A', 60)));
        ThrowsIface<IParseError>(() => l.Parse(new string('A', 59)));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse(new string('A', 61)));

        l = Lark("""
            !start: "A"~15..100
            """, o => o.Parser = ParserKind.Lalr);
        for (var i = 0; i < 110; i++)
        {
            if (i is >= 15 and <= 100)
            {
                AssertEq(new Tree("start", Enumerable.Repeat<object?>("A", i).ToList()), l.Parse(new string('A', i)));
            }
            else
            {
                Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse(new string('A', i)));
            }
        }

        // 8191 is a Mersenne prime
        l = Lark("""
            start: "A"~8191
            """, o => o.Parser = ParserKind.Lalr);
        AssertEq(T("start"), l.Parse(new string('A', 8191)));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse(new string('A', 8190)));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse(new string('A', 8192)));
    }

    [Fact]
    public void test_large_terminal()
    {
        var g = "start: NUMBERS\n";
        g += "NUMBERS: " + string.Join("|", Enumerable.Range(0, 1000).Select(i => $"\"{i}\""));

        var l = Lark(g, o => o.Parser = ParserKind.Lalr);
        foreach (var i in new[] { 0, 9, 99, 999 })
        {
            AssertEq(T("start", i.ToString()), l.Parse(i.ToString()));
        }
        foreach (var i in new[] { -1, 1000 })
        {
            Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse(i.ToString()));
        }
    }

    [Fact]
    public void test_list_grammar_imports()
    {
        const string grammar = """
            %import .test_templates_import (start, sep)

            %override sep{item, delim}: item (delim item)* delim?
            %ignore " "
            """;

        var imports = GrammarTools.ListGrammarImports(grammar, new[] { new FileSystemGrammarLoader(FixturesDir) });
        Assert.True(imports.Select(Path.GetFileName).ToHashSet().SetEquals(new[] { "test_templates_import.lark", "templates.lark" }));

        imports = GrammarTools.ListGrammarImports("%import common.WS");
        Assert.Single(imports);
        Assert.StartsWith("embedded://EtAlii.Lark/", imports[0]);
    }

    [Fact]
    public void test_inline_with_expand_single()
    {
        Assert.Throws<GrammarException>(() => Lark("""
            start: _a
            !?_a: "A"
            """));
    }

    [Fact]
    public void test_line_breaks()
    {
        var p = Lark("""
            start: "a" \
                   "b"
            """);
        p.Parse("ab");
    }

    [Fact]
    public void test_symbol_eq()
    {
        Symbol? a = null;
        var b = new NonTerminal("abc");
        Assert.NotEqual(a, b);
    }
}
