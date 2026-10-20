// Ported from lark/tests/test_parser.py (_make_parser_test), run for every lexer/parser combination.
// Tests that upstream skips for a combination return early here.
// ReSharper disable InconsistentNaming
namespace EtAlii.Lark.Tests.Upstream;

using System.Text.RegularExpressions;
using static TestHelpers;

public abstract class ParserMatrixTests
{
    protected abstract LexerKind Lexer { get; }

    protected abstract ParserKind Parser { get; }

    /// <summary>For the custom lexer combinations: wraps the basic lexer, to test the integration.</summary>
    protected virtual bool CustomLexer => false;

    private bool IsDynamic => Lexer is LexerKind.Dynamic or LexerKind.DynamicComplete;

    private static readonly Lazy<Lark> LarkGrammarParser = new(() => global::EtAlii.Lark.Lark.FromResource(typeof(Lark).Assembly, "lark.lark", "Grammars").UseLalr().Build());

    protected static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private LarkOptions Options(Action<LarkOptions>? configure)
    {
        var options = new LarkOptions
        {
            Parser = Parser,
            Lexer = CustomLexer ? LexerKind.Custom : Lexer,
            CustomLexer = CustomLexer ? conf => new BasicLexer(conf) : null,
            PropagatePositions = true,
        };
        configure?.Invoke(options);
        return options;
    }

    private Lark _Lark(string grammar, Action<LarkOptions>? configure = null)
    {
        var inst = new Lark(grammar, Options(configure));
        ValidateWithLarkGrammar(grammar);
        return inst;
    }

    private Lark _Lark_open(string filename, Action<LarkOptions>? configure = null)
    {
        var inst = global::EtAlii.Lark.Lark.Open(filename, Options(configure), relTo: Path.Combine(FixturesDir, "test_parser.cs"));
        ValidateWithLarkGrammar(inst.SourceGrammar!);
        return inst;
    }

    private void ValidateWithLarkGrammar(string grammar)
    {
        // Check that the `lark.lark` grammar can parse every example used in these tests.
        // basic-Earley was an arbitrary choice, to make sure it only ran once.
        if (Lexer == LexerKind.Basic && Parser == ParserKind.Earley && !CustomLexer)
        {
            LarkGrammarParser.Value.Parse(grammar);
        }
    }

    private static string Datas(Tree t) => string.Concat(t.Children.Cast<Tree>().Select(x => x.Data));

    [Fact]
    public void test_basic1()
    {
        var g = _Lark("""
            start: a+ b a* "b" a*
            b: "b"
            a: "a"
            """);

        Assert.Equal("aaabaa", Datas(g.Parse("aaabaab")));
        Assert.Equal("aaabaaa", Datas(g.Parse("aaabaaba")));

        ThrowsIface<IParseError>(() => g.Parse("aaabaa"));
    }

    [Fact]
    public void test_basic2()
    {
        // Multiple parsers and colliding tokens
        var g = _Lark("""
            start: B A
            B: "12"
            A: "1"
            """);
        var g2 = _Lark("""
            start: B A
            B: "12"
            A: "2"
            """);
        var x = g.Parse("121");
        Assert.Equal("start", x.Data);
        AssertEq(new[] { "12", "1" }, x.Children);
        x = g2.Parse("122");
        Assert.Equal("start", x.Data);
        AssertEq(new[] { "12", "2" }, x.Children);
    }

    [Fact]
    public void test_stringio_unicode()
    {
        // Verify that a Lark can be created from a reader
        using var reader = new StringReader("start: a+ b a* \"b\" a*\n b: \"b\"\n a: \"a\" ");
        _Lark(reader.ReadToEnd());
    }

    [Fact]
    public void test_unicode()
    {
        var g = _Lark("""
            start: UNIA UNIB UNIA
            UNIA: /\xa3/
            UNIB: /ā/
            """);
        g.Parse("£ā£");
    }

    [Fact]
    public void test_unicode2()
    {
        var g = _Lark("""
            start: UNIA UNIB UNIA UNIC
            UNIA: /\xa3/
            UNIB: "aāb\ "
            UNIC: /a?āc\n/
            """);
        g.Parse("£aāb\\ £āc\n");
    }

    [Fact]
    public void test_unicode3()
    {
        var g = _Lark("""
            start: UNIA UNIB UNIA UNIC
            UNIA: /\xa3/
            UNIB: "ā"
            UNIC: /ȃ/ /\n/
            """);
        g.Parse("£ā£ȃ\n");
    }

    [Fact]
    public void test_unicode4()
    {
        var g = _Lark("""
            start: UNIA UNIB UNIA UNIC
            UNIA: /\xa3/
            UNIB: "\U0010FFFF"
            UNIC: /\U00100000/ /\n/
            """);
        g.Parse("£\U0010FFFF£\U00100000\n");
    }

    [Fact]
    public void test_hex_escape()
    {
        var g = _Lark("""
            start: A B C
            A: "\x01"
            B: /\x02/
            C: "\xABCD"
            """);
        g.Parse("\x01\x02«CD");
    }

    [Fact]
    public void test_unicode_literal_range_escape()
    {
        var g = _Lark("""
            start: A+
            A: "a".."c"
            """);
        g.Parse("abc");
    }

    [Fact]
    public void test_unicode_literal_range_escape2()
    {
        var g = _Lark("""
            start: A+
            A: "\U0000FFFF".."\U00010002"
            """);
        g.Parse("￿\U00010000\U00010001\U00010002");
    }

    [Fact]
    public void test_hex_literal_range_escape()
    {
        var g = _Lark("""
            start: A+
            A: "\x01".."\x03"
            """);
        g.Parse("\x01\x02\x03");
    }

    // test_bytes_utf8: not applicable, .NET strings are always UTF-16 (there is no use_bytes mode).

    [Fact]
    public void test_stack_for_ebnf()
    {
        if (Parser == ParserKind.Cyk) return; // Takes forever
        // Verify that stack depth isn't an issue for EBNF grammars
        var g = _Lark("""
            start: a+
            a : "a"
            """);
        g.Parse(new string('a', 2000));
    }

    [Fact]
    public void test_expand1_lists_with_one_item()
    {
        var g = _Lark("""
            start: list
            ?list: item+
            item : A
            A: "a"
            """);
        var r = g.Parse("a");

        // because 'list' is an expand-if-contains-one rule and we only provided one element it should have expanded to 'item'
        Assert.Equal(new[] { "item" }, r.Children.Cast<Tree>().Select(s => s.Data));

        // regardless of the amount of items: there should be only *one* child in 'start' because 'list' isn't an expand-all rule
        Assert.Single(r.Children);
    }

    [Fact]
    public void test_expand1_lists_with_one_item_2()
    {
        var g = _Lark("""
            start: list
            ?list: item+ "!"
            item : A
            A: "a"
            """);
        var r = g.Parse("a!");
        Assert.Equal(new[] { "item" }, r.Children.Cast<Tree>().Select(s => s.Data));
        Assert.Single(r.Children);
    }

    [Fact]
    public void test_dont_expand1_lists_with_multiple_items()
    {
        var g = _Lark("""
            start: list
            ?list: item+
            item : A
            A: "a"
            """);
        var r = g.Parse("aa");
        Assert.Equal(new[] { "list" }, r.Children.Cast<Tree>().Select(s => s.Data));
        var list = (Tree)Assert.Single(r.Children)!;
        Assert.Equal(new[] { "item", "item" }, list.Children.Cast<Tree>().Select(s => s.Data));
    }

    [Fact]
    public void test_dont_expand1_lists_with_multiple_items_2()
    {
        var g = _Lark("""
            start: list
            ?list: item+ "!"
            item : A
            A: "a"
            """);
        var r = g.Parse("aa!");
        Assert.Equal(new[] { "list" }, r.Children.Cast<Tree>().Select(s => s.Data));
        var list = (Tree)Assert.Single(r.Children)!;
        Assert.Equal(new[] { "item", "item" }, list.Children.Cast<Tree>().Select(s => s.Data));
    }

    [Fact]
    public void test_empty_expand1_list()
    {
        if (Parser == ParserKind.Cyk) return; // No empty rules
        var g = _Lark("""
            start: list
            ?list: item*
            item : A
            A: "a"
            """);
        var r = g.Parse("");
        Assert.Equal(new[] { "list" }, r.Children.Cast<Tree>().Select(s => s.Data));
        var list = (Tree)Assert.Single(r.Children)!;
        Assert.Empty(list.Children);
    }

    [Fact]
    public void test_empty_expand1_list_2()
    {
        if (Parser == ParserKind.Cyk) return; // No empty rules
        var g = _Lark("""
            start: list
            ?list: item* "!"?
            item : A
            A: "a"
            """);
        var r = g.Parse("");
        Assert.Equal(new[] { "list" }, r.Children.Cast<Tree>().Select(s => s.Data));
        var list = (Tree)Assert.Single(r.Children)!;
        Assert.Empty(list.Children);
    }

    [Fact]
    public void test_empty_flatten_list()
    {
        if (Parser == ParserKind.Cyk) return; // No empty rules
        var g = _Lark("""
            start: list
            list: | item "," list
            item : A
            A: "a"
            """);
        var r = g.Parse("");
        Assert.Equal(new[] { "list" }, r.Children.Cast<Tree>().Select(s => s.Data));
        var list = (Tree)Assert.Single(r.Children)!;
        Assert.Empty(list.Children);
    }

    [Fact]
    public void test_token_collision()
    {
        var g = _Lark("""
            start: "Hello" NAME
            NAME: /\w/+
            %ignore " "
            """);
        AssertEq(new[] { "World" }, g.Parse("Hello World").Children);
        AssertEq(new[] { "HelloWorld" }, g.Parse("Hello HelloWorld").Children);
    }

    [Fact]
    public void test_token_collision_WS()
    {
        var g = _Lark("""
            start: "Hello" NAME
            NAME: /\w/+
            %import common.WS
            %ignore WS
            """);
        AssertEq(new[] { "World" }, g.Parse("Hello World").Children);
        AssertEq(new[] { "HelloWorld" }, g.Parse("Hello HelloWorld").Children);
    }

    [Fact]
    public void test_token_collision2()
    {
        var g = _Lark("""
            !start: "starts"

            %import common.LCASE_LETTER
            """);
        AssertEq(new[] { "starts" }, g.Parse("starts").Children);
    }

    [Fact]
    public void test_templates()
    {
        var g = _Lark("""
            start: "[" sep{NUMBER, ","} "]"
            sep{item, delim}: item (delim item)*
            NUMBER: /\d+/
            %ignore " "
            """);
        AssertEq(Py.List("[Tree('sep', ['1', '2', '3', '4'])]"), g.Parse("[1, 2, 3, 4]").Children);
        AssertEq(Py.List("[Tree('sep', ['1'])]"), g.Parse("[1]").Children);
    }

    [Fact]
    public void test_templates_recursion()
    {
        var g = _Lark("""
            start: "[" _sep{NUMBER, ","} "]"
            _sep{item, delim}: item | _sep{item, delim} delim item
            NUMBER: /\d+/
            %ignore " "
            """);
        AssertEq(new[] { "1", "2", "3", "4" }, g.Parse("[1, 2, 3, 4]").Children);
        AssertEq(new[] { "1" }, g.Parse("[1]").Children);
    }

    [Fact]
    public void test_templates_import()
    {
        var g = _Lark_open("test_templates_import.lark");
        AssertEq(Py.List("[Tree('sep', ['1', '2', '3', '4'])]"), g.Parse("[1, 2, 3, 4]").Children);
        AssertEq(Py.List("[Tree('sep', ['1'])]"), g.Parse("[1]").Children);
    }

    [Fact]
    public void test_templates_alias()
    {
        var g = _Lark("""
            start: expr{"C"}
            expr{t}: "A" t
                   | "B" t -> b
            """);
        AssertEq(Py.List("[Tree('expr', [])]"), g.Parse("AC").Children);
        AssertEq(Py.List("[Tree('b', [])]"), g.Parse("BC").Children);
    }

    [Fact]
    public void test_templates_modifiers()
    {
        var g = _Lark("""
            start: expr{"B"}
            !expr{t}: "A" t
            """);
        AssertEq(Py.List("[Tree('expr', ['A', 'B'])]"), g.Parse("AB").Children);
        g = _Lark("""
            start: _expr{"B"}
            !_expr{t}: "A" t
            """);
        AssertEq(new[] { "A", "B" }, g.Parse("AB").Children);
        g = _Lark("""
            start: expr{b}
            b: "B"
            ?expr{t}: "A" t
            """);
        AssertEq(Py.List("[Tree('b',[])]"), g.Parse("AB").Children);
    }

    [Fact]
    public void test_templates_templates()
    {
        var g = _Lark("""
            start: a{b}
            a{t}: t{"a"}
            b{x}: x
            """);
        AssertEq(Py.List("[Tree('a', [Tree('b',[])])]"), g.Parse("a").Children);
    }

    [Fact]
    public void test_g_regex_flags()
    {
        var g = _Lark("""
            start: "a" /b+/ C
            C: "C" | D
            D: "D" E
            E: "e"
            """, o => o.RegexOptions = RegexOptions.IgnoreCase);
        g.Parse("ABBc");
        g.Parse("abdE");
    }

    [Fact]
    public void test_rule_collision()
    {
        var g = _Lark("""
            start: "a"+ "b"
                 | "a"+
            """);
        g.Parse("aaaa");
        g.Parse("aaaab");
    }

    [Fact]
    public void test_rule_collision2()
    {
        var g = _Lark("""
            start: "a"* "b"
                 | "a"+
            """);
        g.Parse("aaaa");
        g.Parse("aaaab");
        g.Parse("b");
    }

    [Fact]
    public void test_token_not_anon()
    {
        // Tests that "a" is matched as an anonymous token, and not A.
        var g = _Lark("""
            start: "a"
            A: "a"
            """);
        Assert.Empty(g.Parse("a").Children); // "a" should be considered anonymous

        g = _Lark("""
            start: "a" A
            A: "a"
            """);
        var x = g.Parse("aa");
        Assert.Single(x.Children); // only "a" should be considered anonymous
        Assert.Equal("A", ((Token)x.Children[0]!).Type);

        g = _Lark("""
            start: /a/
            A: /a/
            """);
        x = g.Parse("a");
        Assert.Single(x.Children);
        Assert.Equal("A", ((Token)x.Children[0]!).Type); // A isn't associated with /a/
    }

    [Fact]
    public void test_maybe()
    {
        if (Parser == ParserKind.Cyk) return; // No empty rules
        var g = _Lark("""start: ["a"] """);
        g.Parse("a");
        g.Parse("");
    }

    [Fact]
    public void test_start()
    {
        var g = _Lark("""a: "a" a? """, o => o.Start = new[] { "a" });
        g.Parse("a");
        g.Parse("aa");
        g.Parse("aaa");
    }

    [Fact]
    public void test_alias()
    {
        var g = _Lark("""start: "a" -> b """);
        Assert.Equal("b", g.Parse("a").Data);
    }

    [Fact]
    public void test_token_ebnf()
    {
        var g = _Lark("""
            start: A
            A: "a"* ("b"? "c".."e")+
            """);
        g.Parse("abcde");
        g.Parse("dd");
    }

    [Fact]
    public void test_backslash()
    {
        var g = _Lark("""
            start: "\\" "a"
            """);
        g.Parse(@"\a");

        g = _Lark("""
            start: /\\/ /a/
            """);
        g.Parse(@"\a");
    }

    [Fact]
    public void test_backslash2()
    {
        var g = _Lark("""
            start: "\"" "-"
            """);
        g.Parse("\"-");

        g = _Lark("""
            start: /\// /-/
            """);
        g.Parse("/-");
    }

    [Fact]
    public void test_special_chars()
    {
        var g = _Lark("""
            start: "\n"
            """);
        g.Parse("\n");

        g = _Lark("""
            start: /\n/
            """);
        g.Parse("\n");
    }

    [Fact]
    public void test_empty()
    {
        if (Parser == ParserKind.Cyk) return; // No empty rules
        // Fails an Earley implementation without special handling for empty rules,
        // or re-processing of already completed rules.
        var g = _Lark("""
            start: _empty a "B"
            a: _empty "A"
            _empty:
            """);
        g.Parse("AB");
    }

    [Fact]
    public void test_regex_quote()
    {
        var g = _Lark("""
            start: SINGLE_QUOTED_STRING | DOUBLE_QUOTED_STRING
            SINGLE_QUOTED_STRING  : /'[^']*'/
            DOUBLE_QUOTED_STRING  : /"[^"]*"/
            """);
        AssertEq(new[] { "\"hello\"" }, g.Parse("\"hello\"").Children);
        AssertEq(new[] { "'hello'" }, g.Parse("'hello'").Children);
    }

    [Fact]
    public void test_join_regex_flags()
    {
        var g = _Lark("""
            start: A
            A: B C
            B: /./s
            C: /./
            """);
        AssertEq(new[] { "  " }, g.Parse("  ").Children);
        AssertEq(new[] { "\n " }, g.Parse("\n ").Children);
        Assert.Throws<UnexpectedCharactersException>(() => g.Parse("\n\n"));

        g = _Lark("""
            start: A
            A: B | C
            B: "b"i
            C: "c"
            """);
        AssertEq(new[] { "b" }, g.Parse("b").Children);
        AssertEq(new[] { "B" }, g.Parse("B").Children);
        AssertEq(new[] { "c" }, g.Parse("c").Children);
        Assert.Throws<UnexpectedCharactersException>(() => g.Parse("C"));
    }

    [Fact]
    public void test_float_without_lexer()
    {
        var g = _Lark("""
            start: ["+"|"-"] float
            float: digit* "." digit+ exp?
                 | digit+ exp
            exp: ("e"|"E") ["+"|"-"] digit+
            digit: "0"|"1"|"2"|"3"|"4"|"5"|"6"|"7"|"8"|"9"
            """);
        g.Parse("1.2");
        g.Parse("-.2e9");
        g.Parse("+2e-9");
        if (Parser == ParserKind.Cyk)
        {
            ThrowsIface<IParseError>(() => g.Parse("+2e-9e"));
        }
        else if (IsDynamic)
        {
            Assert.Throws<UnexpectedCharactersException>(() => g.Parse("+2e-9e"));
        }
        else
        {
            Assert.Throws<UnexpectedTokenException>(() => g.Parse("+2e-9e"));
        }
    }

    [Fact]
    public void test_keep_all_tokens()
    {
        var l = _Lark("""start: "a"+ """, o => o.KeepAllTokens = true);
        AssertEq(new[] { "a", "a", "a" }, l.Parse("aaa").Children);
    }

    [Fact]
    public void test_token_flags()
    {
        var l = _Lark("""
            !start: "a"i+
            """);
        AssertEq(new[] { "a", "A" }, l.Parse("aA").Children);

        l = _Lark("""
            !start: /a/i+
            """);
        AssertEq(new[] { "a", "A" }, l.Parse("aA").Children);

        l = _Lark("""
            start: NAME "," "a"
            NAME: /[a-z_]/i /[a-z0-9_]/i*
            """);
        AssertEq(new[] { "ab" }, l.Parse("ab,a").Children);
        AssertEq(new[] { "AB" }, l.Parse("AB,a").Children);
    }

    [Fact]
    public void test_token_flags_collision()
    {
        if (Lexer == LexerKind.Basic || CustomLexer) return; // Requires context sensitive terminal selection

        var l = _Lark("""
            !start: "a"i "a"
            """);
        AssertEq(new[] { "a", "a" }, l.Parse("aa").Children);
        AssertEq(new[] { "A", "a" }, l.Parse("Aa").Children);
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("aA"));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("AA"));

        l = _Lark("""
            !start: /a/i /a/
            """);
        AssertEq(new[] { "a", "a" }, l.Parse("aa").Children);
        AssertEq(new[] { "A", "a" }, l.Parse("Aa").Children);
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("aA"));
        Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("AA"));
    }

    [Fact]
    public void test_token_flags3()
    {
        var l = _Lark("""
            !start: ABC+
            ABC: "abc"i
            """);
        AssertEq(new[] { "aBc", "AbC" }, l.Parse("aBcAbC").Children);
    }

    [Fact]
    public void test_token_flags2()
    {
        var l = _Lark("""
            !start: ("a"i | /a/ /b/?)+
            """);
        AssertEq(new[] { "a", "A" }, l.Parse("aA").Children);
    }

    [Fact]
    public void test_token_flags_verbose()
    {
        var g = _Lark("""
            start: NL | ABC
            ABC: / [a-z] /x
            NL: /\n/
            """);
        AssertEq(new[] { "a" }, g.Parse("a").Children);
    }

    [Fact]
    public void test_token_flags_verbose_multiline()
    {
        var g = _Lark("""
            start: ABC
            ABC: /  a      b c
                 d
                  e f
             /x
            """);
        AssertEq(new[] { "abcdef" }, g.Parse("abcdef").Children);
    }

    [Fact]
    public void test_twice_empty()
    {
        if (Parser == ParserKind.Cyk) return; // No empty rules
        var l = _Lark("""
            !start: ("A"?)?
            """);
        AssertEq(new[] { "A" }, l.Parse("A").Children);
        Assert.Empty(l.Parse("").Children);
    }

    [Fact]
    public void test_line_and_column()
    {
        var l = _Lark("""
            !start: "A" bc "D"
            !bc: "B\nC"
            """);
        var children = l.Parse("AB\nCD").Children;
        Assert.Equal(3, children.Count);
        var a = (Token)children[0]!;
        var bc = (Token)((Tree)children[1]!).Children.Single()!;
        var d = (Token)children[2]!;

        Assert.Equal(1, a.Line);
        Assert.Equal(1, a.Column);

        Assert.Equal(1, bc.Line);
        Assert.Equal(2, bc.Column);

        Assert.Equal(2, d.Line);
        Assert.Equal(2, d.Column);

        Assert.Equal(1, a.EndLine);
        Assert.Equal(2, a.EndColumn);
        Assert.Equal(2, bc.EndLine);
        Assert.Equal(2, bc.EndColumn);
        Assert.Equal(2, d.EndLine);
        Assert.Equal(3, d.EndColumn);
    }

    [Fact]
    public void test_reduce_cycle()
    {
        // Tests an edge-condition in the LALR parser, in which a transition state looks exactly like the end state.
        var l = _Lark("""
            term: A
                | term term

            A: "a"
            """, o => o.Start = new[] { "term" });

        var tree = l.Parse("aa");
        Assert.Equal(2, tree.Children.Count);
    }

    [Fact]
    public void test_lexer_prioritization()
    {
        if (IsDynamic) return; // basic lexer prioritization differs from dynamic lexer prioritization

        var l = _Lark("""
            start: A B | AB
            A.2: "a"
            B: "b"
            AB: "ab"
            """);
        AssertEq(new[] { "a", "b" }, l.Parse("ab").Children);

        l = _Lark("""
            start: A B | AB
            A: "a"
            B: "b"
            AB.3: "ab"
            """);
        AssertEq(new[] { "ab" }, l.Parse("ab").Children);

        l = _Lark("""
            start: A B | AB
            A: "a"
            B.-20: "b"
            AB.-10: "ab"
            """);
        AssertEq(new[] { "a", "b" }, l.Parse("ab").Children);

        l = _Lark("""
            start: A B | AB
            A.-99999999999999999999999: "a"
            B: "b"
            AB: "ab"
            """);
        AssertEq(new[] { "ab" }, l.Parse("ab").Children);
    }

    [Fact]
    public void test_dynamic_lexer_prioritization()
    {
        if (!IsDynamic) return; // dynamic lexer prioritization differs from basic lexer prioritization

        var l = _Lark("""
            start: A B | AB
            A.2: "a"
            B: "b"
            AB: "ab"
            """);
        AssertEq(new[] { "a", "b" }, l.Parse("ab").Children);

        l = _Lark("""
            start: A B | AB
            A: "a"
            B: "b"
            AB.3: "ab"
            """);
        AssertEq(new[] { "ab" }, l.Parse("ab").Children);

        // this case differs from prioritization with a basic lexer
        l = _Lark("""
            start: A B | AB
            A: "a"
            B.-20: "b"
            AB.-10: "ab"
            """);
        AssertEq(new[] { "ab" }, l.Parse("ab").Children);

        l = _Lark("""
            start: A B | AB
            A.-99999999999999999999999: "a"
            B: "b"
            AB: "ab"
            """);
        AssertEq(new[] { "ab" }, l.Parse("ab").Children);
    }

    [Fact]
    public void test_import()
    {
        var l = _Lark("""
            start: NUMBER WORD

            %import common.NUMBER
            %import common.WORD
            %import common.WS
            %ignore WS
            """);
        AssertEq(new[] { "12", "elephants" }, l.Parse("12 elephants").Children);
    }

    [Fact]
    public void test_import_rename()
    {
        var l = _Lark("""
            start: N W

            %import common.NUMBER -> N
            %import common.WORD -> W
            %import common.WS
            %ignore WS
            """);
        AssertEq(new[] { "12", "elephants" }, l.Parse("12 elephants").Children);
    }

    [Fact]
    public void test_relative_import()
    {
        var l = _Lark_open("test_relative_import.lark");
        AssertEq(new[] { "12", "lions" }, l.Parse("12 lions").Children);
    }

    [Fact]
    public void test_relative_import_unicode()
    {
        var l = _Lark_open("test_relative_import_unicode.lark");
        AssertEq(new[] { "Ø" }, l.Parse("Ø").Children);
    }

    [Fact]
    public void test_relative_import_rename()
    {
        var l = _Lark_open("test_relative_import_rename.lark");
        AssertEq(new[] { "12", "lions" }, l.Parse("12 lions").Children);
    }

    [Fact]
    public void test_relative_rule_import()
    {
        var l = _Lark_open("test_relative_rule_import.lark");
        AssertEq(Py.List("['x', Tree('expr', ['a', Tree('expr', ['a', 'b']), 'b']), 'y']"), l.Parse("xaabby").Children);
    }

    [Fact]
    public void test_relative_rule_import_drop_ignore()
    {
        // %ignore rules are dropped on import
        var l = _Lark_open("test_relative_rule_import_drop_ignore.lark");
        Assert.ThrowsAny<LarkException>(() => l.Parse("xa abby"));
    }

    [Fact]
    public void test_relative_rule_import_subrule()
    {
        var l = _Lark_open("test_relative_rule_import_subrule.lark");
        AssertEq(Py.List("""
            ['x',
                Tree('startab', [
                    Tree('grammars__ab__expr', [
                        'a', Tree('grammars__ab__expr', ['a', 'b']), 'b',
                    ]),
                ]),
                'y']
            """), l.Parse("xaabby").Children);
    }

    [Fact]
    public void test_relative_rule_import_subrule_no_conflict()
    {
        var l = _Lark_open("test_relative_rule_import_subrule_no_conflict.lark");
        AssertEq(Py.List("""
            [Tree('expr', [
                'x',
                Tree('startab', [
                    Tree('grammars__ab__expr', ['a', 'b']),
                ]),
                'y'])]
            """), l.Parse("xaby").Children);
        Assert.ThrowsAny<LarkException>(() => l.Parse("xaxabyby"));
    }

    [Fact]
    public void test_relative_rule_import_rename()
    {
        var l = _Lark_open("test_relative_rule_import_rename.lark");
        AssertEq(Py.List("['x', Tree('ab', ['a', Tree('ab', ['a', 'b']), 'b']), 'y']"), l.Parse("xaabby").Children);
    }

    [Fact]
    public void test_multi_import()
    {
        var l = _Lark("""
            start: NUMBER WORD

            %import common (NUMBER, WORD, WS)
            %ignore WS
            """);
        AssertEq(new[] { "12", "toucans" }, l.Parse("12 toucans").Children);
    }

    [Fact]
    public void test_relative_multi_import()
    {
        var l = _Lark_open("test_relative_multi_import.lark");
        AssertEq(new[] { "12", "capybaras" }, l.Parse("12 capybaras").Children);
    }

    [Fact]
    public void test_relative_import_preserves_leading_underscore()
    {
        var l = _Lark_open("test_relative_import_preserves_leading_underscore.lark");
        var x = l.Parse("Ax");
        AssertEq(new[] { "A" }, x.FindData("c").First().Children);
    }

    [Fact]
    public void test_relative_import_of_nested_grammar()
    {
        var l = _Lark_open("grammars/test_relative_import_of_nested_grammar.lark");
        var x = l.Parse("N");
        AssertEq(new[] { "N" }, x.FindData("rule_to_import").First().Children);
    }

    [Fact]
    public void test_relative_import_rules_dependencies_imported_only_once()
    {
        var l = _Lark_open("test_relative_import_rules_dependencies_imported_only_once.lark");
        var x = l.Parse("AAA");
        AssertEq(new[] { "A" }, x.FindData("a").First().Children);
        AssertEq(new[] { "A" }, x.FindData("b").First().Children);
        AssertEq(new[] { "A" }, x.FindData("d").First().Children);
    }

    [Fact]
    public void test_import_errors()
    {
        Assert.ThrowsAny<IOException>(() => _Lark("""
            start: NUMBER WORD

            %import .grammars.bad_test.NUMBER
            """));

        Assert.ThrowsAny<IOException>(() => _Lark("""
            start: NUMBER WORD

            %import bad_test.NUMBER
            """));
    }

    private sealed class TestPostLexer : PostLex
    {
        public override IEnumerable<Token> Process(IEnumerable<Token> stream)
        {
            foreach (var t in stream)
            {
                if (t.Type == "A") t.Type = "B";
                yield return t;
            }
        }

        public override IReadOnlyCollection<string> AlwaysAccept => new[] { "A" };
    }

    [Fact]
    public void test_postlex_declare()
    {
        if (IsDynamic) return; // %declare/postlex doesn't work with dynamic
        var parser = _Lark("""
            start: B
            A: "A"
            %declare B
            """, o => o.PostLex = new TestPostLexer());

        var tree = parser.Parse("A");
        AssertEq(new[] { Tok("B", "A") }, tree.Children);
        Assert.Equal("B", ((Token)tree.Children[0]!).Type);
    }

    private sealed class CustomIndenter : Indenter
    {
        public override string NlType => "NEWLINE";
        public override IReadOnlyCollection<string> OpenParenTypes => Array.Empty<string>();
        public override IReadOnlyCollection<string> CloseParenTypes => Array.Empty<string>();
        public override string IndentType => "INDENT";
        public override string DedentType => "DEDENT";
        public override int TabLen => 8;
    }

    [Fact]
    public void test_postlex_indenter()
    {
        if (IsDynamic) return; // %declare/postlex doesn't work with dynamic
        var parser = _Lark("""
            start: "a" NEWLINE INDENT "b" NEWLINE DEDENT

            NEWLINE: ( /\r?\n */  )+

            %ignore " "+
            %declare INDENT DEDENT
            """, o => o.PostLex = new CustomIndenter());
        parser.Parse("a\n    b\n");
    }

    [Fact]
    public void test_prioritization()
    {
        if (Parser == ParserKind.Cyk) return; // Doesn't work for CYK

        var l = _Lark("""
            start: a | b
            a.1: "a"
            b.2: "a"
            """);
        Assert.Equal("b", ((Tree)l.Parse("a").Children[0]!).Data);

        l = _Lark("""
            start: a | b
            a.2: "a"
            b.1: "a"
            """);
        Assert.Equal("a", ((Tree)l.Parse("a").Children[0]!).Data);

        const string grammar = """
            start: a | b
            a.2: "A"+
            b.1: "A"+ "B"?
            """;

        l = _Lark(grammar);
        Assert.Equal("a", ((Tree)l.Parse("AAAA").Children[0]!).Data);

        l = _Lark(grammar);
        Assert.Equal("b", ((Tree)l.Parse("AAAB").Children[0]!).Data);

        l = _Lark(grammar, o => o.Priority = PriorityMode.Invert);
        Assert.Equal("b", ((Tree)l.Parse("AAAA").Children[0]!).Data);
    }

    [Fact]
    public void test_prioritization_sum()
    {
        if (Parser != ParserKind.Earley || !IsDynamic) return; // Currently only Earley supports priority sum in rules

        var l = _Lark("""
            start: ab_ b_ a_ | indirection
            indirection: a_ bb_ a_
            a_: "a"
            b_: "b"
            ab_: "ab"
            bb_.1: "bb"
            """, o => o.Priority = PriorityMode.Invert);
        Assert.Equal("ab_b_a_", Datas(l.Parse("abba")));

        l = _Lark("""
            start: ab_ b_ a_ | indirection
            indirection: a_ bb_ a_
            a_: "a"
            b_: "b"
            ab_.1: "ab"
            bb_: "bb"
            """, o => o.Priority = PriorityMode.Invert);
        Assert.Equal("indirection", Datas(l.Parse("abba")));

        l = _Lark("""
            start: ab_ b_ a_ | indirection
            indirection: a_ bb_ a_
            a_.2: "a"
            b_.1: "b"
            ab_.3: "ab"
            bb_.3: "bb"
            """, o => o.Priority = PriorityMode.Invert);
        Assert.Equal("ab_b_a_", Datas(l.Parse("abba")));

        l = _Lark("""
            start: ab_ b_ a_ | indirection
            indirection: a_ bb_ a_
            a_.1: "a"
            b_.1: "b"
            ab_.4: "ab"
            bb_.3: "bb"
            """, o => o.Priority = PriorityMode.Invert);
        Assert.Equal("indirection", Datas(l.Parse("abba")));
    }

    [Fact]
    public void test_utf8()
    {
        var l = _Lark("""
            start: a
            a: "±a"
            """);
        AssertEq(Py.Tree("Tree('start', [Tree('a', [])])"), l.Parse("±a"));

        l = _Lark("""
            start: A
            A: "±a"
            """);
        AssertEq(T("start", "±a"), l.Parse("±a"));
    }

    [Fact]
    public void test_ignore()
    {
        if (Parser == ParserKind.Cyk) return; // No empty rules
        var parser = _Lark("""
            COMMENT: /(!|(\/\/))[^\n]*/
            %ignore COMMENT
            %import common.WS -> _WS
            %import common.INT
            start: "INT"i _WS+ INT _WS*
            """);

        AssertEq(new[] { "1" }, parser.Parse("int 1 ! This is a comment\n").Children);
        AssertEq(new[] { "1" }, parser.Parse("int 1 ! This is a comment").Children); // A trailing ignore token can be tricky!

        parser = _Lark("""
            start : "a"*
            %ignore "b"
            """);
        Assert.Empty(parser.Parse("bb").Children);
    }

    [Fact]
    public void test_regex_escaping()
    {
        var g = _Lark("start: /[ab]/");
        g.Parse("a");
        g.Parse("b");

        Assert.ThrowsAny<UnexpectedInputException>(() => g.Parse("c"));

        _Lark(@"start: /\w/").Parse("a");

        g = _Lark(@"start: /\\w/");
        Assert.ThrowsAny<UnexpectedInputException>(() => g.Parse("a"));
        g.Parse(@"\w");

        _Lark(@"start: /\[/").Parse("[");

        _Lark(@"start: /\//").Parse("/");

        _Lark(@"start: /\\/").Parse("\\");

        _Lark(@"start: /\[ab]/").Parse("[ab]");

        _Lark(@"start: /\\[ab]/").Parse("\\a");

        _Lark(@"start: /\t/").Parse("\t");

        _Lark(@"start: /\\t/").Parse("\\t");

        _Lark(@"start: /\\\t/").Parse("\\\t");

        _Lark(@"start: ""\t""").Parse("\t");

        _Lark(@"start: ""\\t""").Parse("\\t");

        _Lark(@"start: ""\\\t""").Parse("\\\t");
    }

    [Fact]
    public void test_ranged_repeat_rules()
    {
        var l = _Lark("""
            !start: "A"~3
            """);
        AssertEq(T("start", "A", "A", "A"), l.Parse("AAA"));
        ThrowsIface<IParseError>(() => l.Parse("AA"));
        Assert.ThrowsAny<LarkException>(() => l.Parse("AAAA"));

        if (Parser != ParserKind.Cyk)
        {
            // CYK currently doesn't support empty grammars
            l = _Lark("""
                !start: "A"~0..2
                """);
            AssertEq(T("start"), l.Parse(""));
            AssertEq(T("start", "A"), l.Parse("A"));
            AssertEq(T("start", "A", "A"), l.Parse("AA"));
            Assert.ThrowsAny<UnexpectedInputException>(() => l.Parse("AAA"));
        }

        Assert.Throws<GrammarException>(() => _Lark("""
            !start: "A"~3..2
            """));

        l = _Lark("""
            !start: "A"~2..3 "B"~2
            """);
        AssertEq(T("start", "A", "A", "B", "B"), l.Parse("AABB"));
        AssertEq(T("start", "A", "A", "A", "B", "B"), l.Parse("AAABB"));
        ThrowsIface<IParseError>(() => l.Parse("AAAB"));
        Assert.ThrowsAny<LarkException>(() => l.Parse("AAABBB"));
        Assert.ThrowsAny<LarkException>(() => l.Parse("ABB"));
        Assert.ThrowsAny<LarkException>(() => l.Parse("AAAABB"));
    }

    [Fact]
    public void test_priority_vs_embedded()
    {
        var l = _Lark("""
            A.2: "a"
            WORD: ("a".."z")+

            start: (A | WORD)+
            """);
        var t = l.Parse("abc");
        AssertEq(new[] { "a", "bc" }, t.Children);
        Assert.Equal("A", ((Token)t.Children[0]!).Type);
    }

    [Fact]
    public void test_line_counting()
    {
        var p = _Lark("start: /[^x]+/");

        const string text = "hello\nworld";
        var t = p.Parse(text);
        var tok = (Token)t.Children[0]!;
        Assert.Equal(text, tok.Value);
        Assert.Equal(1, tok.Line);
        Assert.Equal(1, tok.Column);
        Assert.Equal(2, tok.EndLine);
        Assert.Equal(6, tok.EndColumn);
    }

    [Fact]
    public void test_empty_end()
    {
        if (Parser == ParserKind.Cyk) return; // Empty rules
        var p = _Lark("""
            start: b c d
            b: "B"
            c: | "C"
            d: | "D"
            """);
        Assert.Equal(3, p.Parse("B").Children.Count);
    }

    [Fact]
    public void test_maybe_placeholders()
    {
        if (Parser == ParserKind.Cyk) return; // Empty rules

        // Anonymous tokens shouldn't count
        var p = _Lark("""start: ["a"] ["b"] ["c"] """, o => o.MaybePlaceholders = true);
        Assert.Empty(p.Parse("").Children);

        // Unless keep_all_tokens=True
        p = _Lark("""start: ["a"] ["b"] ["c"] """, o =>
        {
            o.MaybePlaceholders = true;
            o.KeepAllTokens = true;
        });
        AssertEq(new object?[] { null, null, null }, p.Parse("").Children);

        // All invisible constructs shouldn't count
        p = _Lark("""
            start: [A] ["b"] [_c] ["e" "f" _c]
            A: "a"
            _c: "c"
            """, o => o.MaybePlaceholders = true);
        AssertEq(new object?[] { null }, p.Parse("").Children);
        AssertEq(new object?[] { null }, p.Parse("c").Children);
        AssertEq(new object?[] { "a" }, p.Parse("aefc").Children);

        // ? shouldn't apply
        p = _Lark("""!start: ["a"] "b"? ["c"] """, o => o.MaybePlaceholders = true);
        AssertEq(new object?[] { null, null }, p.Parse("").Children);
        AssertEq(new object?[] { null, "b", null }, p.Parse("b").Children);

        p = _Lark("""!start: ["a"] ["b"] ["c"] """, o => o.MaybePlaceholders = true);
        AssertEq(new object?[] { null, null, null }, p.Parse("").Children);
        AssertEq(new object?[] { "a", null, null }, p.Parse("a").Children);
        AssertEq(new object?[] { null, "b", null }, p.Parse("b").Children);
        AssertEq(new object?[] { null, null, "c" }, p.Parse("c").Children);
        AssertEq(new object?[] { "a", "b", null }, p.Parse("ab").Children);
        AssertEq(new object?[] { "a", null, "c" }, p.Parse("ac").Children);
        AssertEq(new object?[] { null, "b", "c" }, p.Parse("bc").Children);
        AssertEq(new object?[] { "a", "b", "c" }, p.Parse("abc").Children);

        p = _Lark("""!start: (["a"] "b" ["c"])+ """, o => o.MaybePlaceholders = true);
        AssertEq(new object?[] { null, "b", null }, p.Parse("b").Children);
        AssertEq(new object?[] { null, "b", null, null, "b", null }, p.Parse("bb").Children);
        AssertEq(new object?[] { "a", "b", null, null, "b", "c" }, p.Parse("abbc").Children);
        AssertEq(new object?[]
        {
            null, "b", null,
            "a", "b", null,
            null, "b", "c",
            "a", "b", "c",
            null, "b", null,
        }, p.Parse("babbcabcb").Children);

        p = _Lark("""!start: ["a"] ["c"] "b"+ ["a"] ["d"] """, o => o.MaybePlaceholders = true);
        AssertEq(new object?[] { null, null, "b", "b", null, null }, p.Parse("bb").Children);
        AssertEq(new object?[] { null, null, "b", null, "d" }, p.Parse("bd").Children);
        AssertEq(new object?[] { "a", null, "b", "b", "a", null }, p.Parse("abba").Children);
        AssertEq(new object?[] { null, "c", "b", "b", "b", "b", null, null }, p.Parse("cbbbb").Children);

        p = _Lark("""!start: ["a" "b" "c"] """, o => o.MaybePlaceholders = true);
        AssertEq(new object?[] { null, null, null }, p.Parse("").Children);
        AssertEq(new object?[] { "a", "b", "c" }, p.Parse("abc").Children);

        p = _Lark("""!start: ["a" ["b" "c"]] """, o => o.MaybePlaceholders = true);
        AssertEq(new object?[] { null, null, null }, p.Parse("").Children);
        AssertEq(new object?[] { "a", null, null }, p.Parse("a").Children);
        AssertEq(new object?[] { "a", "b", "c" }, p.Parse("abc").Children);

        p = _Lark("""!start: "a" ["b" | "c"] """, o => o.MaybePlaceholders = true);
        AssertEq(new object?[] { "a", null }, p.Parse("a").Children);
        AssertEq(new object?[] { "a", "b" }, p.Parse("ab").Children);

        p = _Lark("""!start: "a" ["b" | "c" "d"] """, o => o.MaybePlaceholders = true);
        AssertEq(new object?[] { "a", null, null }, p.Parse("a").Children);
        AssertEq(new object?[] { "a", "c", "d" }, p.Parse("acd").Children);
    }

    [Fact]
    public void test_escaped_string()
    {
        // Tests common.ESCAPED_STRING
        var parser = _Lark("""
            start: ESCAPED_STRING+

            %import common (WS_INLINE, ESCAPED_STRING)
            %ignore WS_INLINE
            """);
        parser.Parse("""
            "\\" "b" "c"
            """);
        parser.Parse("""
            "That" "And a \"b"
            """);
    }

    [Fact]
    public void test_meddling_unused()
    {
        // Unless 'unused' is removed, LALR analysis will fail on reduce-reduce collision
        _Lark("""
            start: EKS* x
            x: EKS
            unused: x*
            EKS: "x"
            """);
    }

    [Fact]
    public void test_serialize()
    {
        if (Parser != ParserKind.Lalr) return; // Serialize currently only works for LALR parsers
        var parser = _Lark("""
            start: _ANY b "C"
            _ANY: /./
            b: "B"
            """);
        using var s = new MemoryStream();
        parser.Save(s);
        s.Position = 0;
        var parser2 = global::EtAlii.Lark.Lark.Load(s, CustomLexer ? new LarkOptions { CustomLexer = conf => new BasicLexer(conf) } : null);
        AssertEq(Py.Tree("Tree('start', [Tree('b', [])])"), parser2.Parse("ABC"));
    }

    [Fact]
    public void test_multi_start()
    {
        var parser = _Lark("""
            a: "x" "a"?
            b: "x" "b"?
            """, o => o.Start = new[] { "a", "b" });

        AssertEq(T("a"), parser.Parse("xa", "a"));
        AssertEq(T("b"), parser.Parse("xb", "b"));
    }

    [Fact]
    public void test_lexer_detect_newline_tokens()
    {
        // Detect newlines in regular tokens
        var g = _Lark("""
            start: "go" tail*
            !tail : SA "@" | SB "@" | SC "@" | SD "@"
            SA : "a" /\n/
            SB : /b./s
            SC : "c" /[^a-z]/
            SD : "d" /\s/
            """);
        var tokens = g.Parse("goa\n@b\n@c\n@d\n@").Children.Select(x => (Token)((Tree)x!).Children[1]!).ToList();
        Assert.Equal(new int?[] { 2, 3, 4, 5 }, tokens.Select(t => t.Line));

        // Detect newlines in ignored tokens
        foreach (var re in new[] { "/\\n/", "/[^a-z]/", "/\\s/" })
        {
            g = _Lark($"""
                !start: "a" "a"
                %ignore {re}
                """);
            var children = g.Parse("a\na").Children;
            Assert.Equal(1, ((Token)children[0]!).Line);
            Assert.Equal(2, ((Token)children[1]!).Line);
        }
    }

    [Fact]
    public void test_match_examples()
    {
        if (Parser == ParserKind.Cyk) return; // match_examples() not supported for CYK
        var p = _Lark("""
            start: "a" "b" "c"
            """);

        int MatchError(string s)
        {
            try
            {
                p.Parse(s);
            }
            catch (UnexpectedInputException u)
            {
                return u.MatchExamples(x => p.Parse(x), new[]
                {
                    (0, new[] { "abe" }),
                    (1, new[] { "ab" }),
                    (2, new[] { "cbc", "dbc" }),
                });
            }
            throw new InvalidOperationException("Expected an error");
        }

        Assert.Equal(0, MatchError("abe"));
        Assert.Equal(1, MatchError("ab"));
        Assert.Equal(2, MatchError("bbc"));
        Assert.Equal(2, MatchError("cbc"));
        Assert.Equal(2, MatchError("dbc"));
        Assert.Equal(2, MatchError("ebc"));
    }

    [Fact]
    public void test_unicode_class()
    {
        // Character classes like \p{Lu} work out of the box in .NET (upstream requires the `regex` module).
        var g = _Lark("""
            ?start: NAME
            NAME: ID_START ID_CONTINUE*
            ID_START: /[\p{Lu}\p{Ll}\p{Lt}\p{Lm}\p{Lo}\p{Nl}_]+/
            ID_CONTINUE: ID_START | /[\p{Mn}\p{Mc}\p{Nd}\p{Pc}]+/
            """);
        AssertEq("வணக்கம்", g.ParseObject("வணக்கம்"));
    }

    [Fact]
    public void test_unicode_word()
    {
        var g = _Lark("""
            ?start: NAME
            NAME: /[\w]+/
            """);
        AssertEq("வணக்கம்", g.ParseObject("வணக்கம்"));
    }

    [Fact]
    public void test_regex_width_fallback()
    {
        // .NET supports conditionals natively (upstream requires the `regex` module).
        var p = _Lark("""
            start: NAME NAME?
            NAME: /(?(?=\d)\d+|\w+)/
            """);
        AssertEq(T("start", "123", "abc"), p.Parse("123abc"));

        Assert.ThrowsAny<LarkException>(() => _Lark("""
            start: NAME NAME?
            NAME: /(?(?=\d)\d+|\w*)/
            """));
    }

    [Fact]
    public void test_parser_interactive_parser()
    {
        if (Parser != ParserKind.Lalr) return; // interactive_parser is only implemented for LALR at the moment

        var g = _Lark("""
            start: A+ B*
            A: "a"
            B: "b"
            """);

        var ip = g.ParseInteractive();

        Assert.Throws<UnexpectedTokenException>(() => ip.FeedEof());
        Assert.ThrowsAny<Exception>(() => ip.ExhaustLexer());
        ip.FeedToken(Tok("A", "a"));
        var res = ip.FeedEof();
        AssertEq(T("start", "a"), res);

        ip = g.ParseInteractive("ab");

        ip.ExhaustLexer();

        var ipCopy = ip.Copy();
        Assert.Equal(ipCopy.ParserState, ip.ParserState);
        Assert.Equal(ipCopy.LexerThread.State, ip.LexerThread.State);
        Assert.NotSame(ipCopy.ParserState, ip.ParserState);
        Assert.NotSame(ipCopy.LexerThread.State, ip.LexerThread.State);
        Assert.NotSame(ipCopy.LexerThread.State!.LineCounter, ip.LexerThread.State!.LineCounter);

        res = ip.FeedEof(ip.LexerThread.State.LastToken);
        AssertEq(T("start", "a", "b"), res);
        Assert.Throws<UnexpectedTokenException>(() => ip.FeedEof());

        Assert.Throws<UnexpectedTokenException>(() => ipCopy.FeedToken(Tok("A", "a")));
        ipCopy.FeedToken(Tok("B", "b"));
        res = ipCopy.FeedEof();
        AssertEq(T("start", "a", "b", "b"), res);
    }

    [Fact]
    public void test_error_with_interactive_parser()
    {
        if (Parser != ParserKind.Lalr) return; // interactive_parser error handling only works with LALR for now

        static bool IgnoreErrors(UnexpectedInputException e)
        {
            if (e is UnexpectedCharactersException) return true; // Skip bad character

            // Must be UnexpectedToken
            var ut = (UnexpectedTokenException)e;
            if (ut.Token.Type == "COMMA") return true; // Skip comma
            if (ut.Token.Type == "SIGNED_NUMBER")
            {
                // Try to feed a comma and retry the number
                e.InteractiveParser!.FeedToken(Tok("COMMA", ","));
                e.InteractiveParser.FeedToken(ut.Token);
                return true;
            }

            // Unhandled error. Will stop parse and raise exception
            return false;
        }

        var g = _Lark("""
            start: "[" num ("," num)* "]"
            ?num: SIGNED_NUMBER
            %import common.SIGNED_NUMBER
            %ignore " "
            """);
        var tree = g.Parse("[0 1, 2,, 3,,, 4, 5 6 ]", onError: IgnoreErrors);
        var res = tree.Children.Select(x => int.Parse(x!.ToString()!)).ToList();
        Assert.Equal(Enumerable.Range(0, 7), res);

        g.Parse("[0 1, 2,@, 3,,, 4, 5 6 ]$", onError: IgnoreErrors);
    }

    [Fact]
    public void test_on_error_without_lalr()
    {
        if (Parser == ParserKind.Lalr) return; // test on_error only works with lalr
        var p = _Lark("""start: "A" """);
        Assert.Throws<NotSupportedException>(() => p.Parse("", onError: _ => true));
    }

    [Fact]
    public void test_iter_parse()
    {
        if (Parser != ParserKind.Lalr) return; // interactive_parser error handling only works with LALR for now
        var parser = _Lark("""!start: "a"* "b"*""");
        var ip = parser.ParseInteractive("aaabb");
        using var i = ip.IterParse().GetEnumerator();
        Assert.True(i.MoveNext());
        AssertEq("a", i.Current);
        Assert.True(i.MoveNext());
        AssertEq("a", i.Current);
        Assert.True(i.MoveNext());
        AssertEq("a", i.Current);
        Assert.True(i.MoveNext());
        AssertEq("b", i.Current);
    }

    private sealed class SymTransformer : Transformer
    {
        public object SYM(Token token) => token.Value == "a" ? 1 : 2;
    }

    [Fact]
    public void test_interactive_treeless_transformer()
    {
        if (Parser != ParserKind.Lalr) return; // interactive_parser is only implemented for LALR at the moment
        var parser = _Lark("""
            start: SYM+

            SYM: "a" | "b"
            """, o => o.Transformer = new SymTransformer());
        var res = parser.Parse("aba");
        AssertEq(new[] { 1, 2, 1 }, res.Children);
        var ip = parser.ParseInteractive("aba");
        ip.ExhaustLexer();
        // Previously `accepts` would call `SYMTransformer.SYM` with `Token('SYM', '')`, which would cause an error.
        Assert.True(ip.Accepts().SetEquals(new[] { "$END", "SYM" }));
        res = (Tree)ip.FeedEof()!;
        AssertEq(new[] { 1, 2, 1 }, res.Children);
    }

    private sealed class AbTransformer : Transformer
    {
        protected internal override object? Default(string data, List<object?> children, Meta? meta) => new List<object?> { "@", data, children };
    }

    [Fact]
    public void test_default_in_treeless_mode()
    {
        if (Parser == ParserKind.Earley) return; // Tree-less mode is not supported in earley
        const string grammar = """
            start: expr

            expr: A B
                | A expr B

            A: "a"
            B: "b"

            %import common.WS
            %ignore WS
            """;
        const string s = "a a a b b b";

        var parser = _Lark(grammar);
        var a = new AbTransformer().Transform(parser.Parse(s));
        parser = _Lark(grammar, o => o.Transformer = new AbTransformer());
        var b = parser.ParseObject(s);
        AssertEq(a, b);
    }

    [Fact]
    public void test_strict()
    {
        if (Parser != ParserKind.Lalr) return; // strict mode is only supported in lalr for now

        // Test regex collision
        ThrowsIface<ILexError>(() => _Lark("""
            start: A | B

            A: /e?rez/
            B: /erez?/
            """, o => o.Strict = true));

        // Test shift-reduce collision
        Assert.Throws<GrammarException>(() => _Lark("""
            start: a "."
            a: "."+
            """, o => o.Strict = true));
    }

    [Fact]
    public void test_parse_textslice()
    {
        if (IsDynamic) return; // start_pos and end_pos not compatible with the dynamic lexer

        var parser = _Lark("""
            start: (WORD|FRAG_END|FRAG_START)+
            WORD: /\b\w+\b/ # match full word
            FRAG_END: /\B\w+/ # end of a word, i.e. start is not at a word boundary
            FRAG_START: /\w+\B/ # start of a word, i.e. end is not at a word boundary
            %ignore /\s+/
            """);
        AssertEq(T("start", Tok("WORD", "abc"), Tok("WORD", "def")), parser.Parse(new TextSlice(" abc def ", 1, -1)));
        AssertEq(T("start", Tok("WORD", "abc"), Tok("WORD", "def")), parser.Parse(new TextSlice(" abc def ", 1 - 9, -1 + 9)));
        AssertEq(T("start", Tok("FRAG_END", "abc"), Tok("WORD", "def")), parser.Parse(new TextSlice("xabc def ", 1, -1)));

        // We match the behavior of python's re module here: It doesn't look ahead beyond `end_pos`,
        // despite looking behind before `start_pos`
        AssertEq(T("start", Tok("WORD", "abc"), Tok("WORD", "def")), parser.Parse(new TextSlice(" abc defx", 1, -1)));

        parser = _Lark("""
            start: (_NL | ANY)+
            _NL: "\n"
            ANY: /[^\n]/
            """);
        var digits = string.Join("\n", "123456789".ToCharArray());
        var tree = parser.Parse(new TextSlice(digits, 2, 3));
        AssertEq(new[] { "2" }, tree.Children);
        var t = (Token)tree.Children[0]!;
        Assert.Equal((2 - 1) * 2, t.StartPos);
        Assert.Equal(2, t.Line);

        tree = parser.Parse(new TextSlice(digits, -1));
        AssertEq(new[] { "9" }, tree.Children);
        t = (Token)tree.Children[0]!;
        Assert.Equal((9 - 1) * 2, t.StartPos);
        Assert.Equal(9, t.Line);
    }

    [Fact]
    public void test_parse_textslice_fails()
    {
        if (!IsDynamic) return; // start_pos and end_pos not compatible with the dynamic lexer
        var parser = _Lark("start: ");
        Assert.ThrowsAny<ArgumentException>(() => parser.Parse(new TextSlice("hello", 2, 3)));
    }
}

public class EarleyBasicTests : ParserMatrixTests
{
    protected override LexerKind Lexer => LexerKind.Basic;
    protected override ParserKind Parser => ParserKind.Earley;
}

public class CykBasicTests : ParserMatrixTests
{
    protected override LexerKind Lexer => LexerKind.Basic;
    protected override ParserKind Parser => ParserKind.Cyk;
}

public class LalrBasicTests : ParserMatrixTests
{
    protected override LexerKind Lexer => LexerKind.Basic;
    protected override ParserKind Parser => ParserKind.Lalr;
}

public class EarleyDynamicTests : ParserMatrixTests
{
    protected override LexerKind Lexer => LexerKind.Dynamic;
    protected override ParserKind Parser => ParserKind.Earley;
}

public class EarleyDynamicCompleteTests : ParserMatrixTests
{
    protected override LexerKind Lexer => LexerKind.DynamicComplete;
    protected override ParserKind Parser => ParserKind.Earley;
}

public class LalrContextualTests : ParserMatrixTests
{
    protected override LexerKind Lexer => LexerKind.Contextual;
    protected override ParserKind Parser => ParserKind.Lalr;
}

public class LalrCustomTests : ParserMatrixTests
{
    protected override LexerKind Lexer => LexerKind.Basic;
    protected override ParserKind Parser => ParserKind.Lalr;
    protected override bool CustomLexer => true;
}

public class CykCustomTests : ParserMatrixTests
{
    protected override LexerKind Lexer => LexerKind.Basic;
    protected override ParserKind Parser => ParserKind.Cyk;
    protected override bool CustomLexer => true;
}

public class EarleyCustomTests : ParserMatrixTests
{
    protected override LexerKind Lexer => LexerKind.Basic;
    protected override ParserKind Parser => ParserKind.Earley;
    protected override bool CustomLexer => true;
}
