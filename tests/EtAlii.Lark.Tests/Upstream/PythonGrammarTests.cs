// Ported from lark/tests/test_python_grammar.py.
// ReSharper disable InconsistentNaming
#pragma warning disable IDE1006
namespace EtAlii.Lark.Tests.Upstream;

using static TestHelpers;

public class PythonGrammarTests
{
    private static readonly string[] ValidDecNumber = { "0", "000", "0_0_0", "4_2", "1_0000_0000", "123456789012345678901234567890" };

    private static readonly string[] ValidHexNumber = { "0x_f", "0xffff_ffff", "0xffffffffffffffff", "0Xffffffffffffffff" };

    private static readonly string[] ValidOctNumber = { "0o5_7_7", "0o_5", "0o77777777777777777", "0O77777777777777777" };

    private static readonly string[] ValidBinNumber =
    {
        "0b1001_0100",
        "0b_0",
        "0b100000000000000000000000000000000000000000000000000000000000000000000",
        "0B111111111111111111111111111111111111111111111111111111111111111111111",
    };

    private static readonly string[] ValidFloatNumber =
    {
        "1_00_00.5", "1_00_00.5e5", "1_00_00e5_1", "1e1_0", ".1_4", ".1_4e1", "1_2.5", "3.14", "314.", "0.314",
        "000.314", ".314", "3e14", "3E14", "3e-14", "3e+14", "3.e14", ".3e14", "3.1e4",
    };

    private static readonly string[] ValidImagNumber =
    {
        "0j", "123456789012345678901234567890j", "1_00_00j", "1_00_00.5j", "1_00_00e5_1j", ".1_4j", "3_3j", ".5_6j",
        "3.14j", "314.j", "0.314j", "000.314j", ".314j", "3e14j", "3E14j", "3e-14j", "3e+14j", "3.e14j", ".3e14j", "3.1e4j",
    };

    private static readonly string[] ValidNumber = ValidDecNumber.Concat(ValidHexNumber).Concat(ValidOctNumber)
        .Concat(ValidBinNumber).Concat(ValidFloatNumber).Concat(ValidImagNumber).ToArray();

    private static readonly string[] InvalidNumber =
    {
        "0_", "42_", "1.4j_", "0x_", "0b1_", "0xf_", "0o5_", "1_Else", "0_b0", "0_xf", "0_o5", "0_7", "09_99",
        "4_______2", "0.1__4", "0.1__4j", "0b1001__0100", "0xffff__ffff", "0x___", "0o5__77", "1e1__0", "1e1__0j",
        "1_.4", "1_.4j", "1._4", "1._4j", "._5", "._5j", "1.0e+_1", "1.0e+_1j", "1.4_j", "1.4e5_j", "1_e1", "1.4_e1",
        "1.4_e1j", "1e_1", "1.4e_1", "1.4e_1j", "1+1.5_j_", "1+1.5_j",

        "_0", "_42", "_1.4j", "_0x", "_0b1", "_0xf", "_0o5", "_1_Else", "_0_b0", "_0_xf", "_0_o5", "_0_7", "_09_99",
        "_4_______2", "_0.1__4", "_0.1__4j", "_0b1001__0100", "_0xffff__ffff", "_0x__", "_0o5__77", "_1e1__0",
        "_1e1__0j", "_1_.4", "_1_.4j", "_1._4", "_1._4j", "_._5", "_._5j", "_1.0e+_1", "_1.0e+_1j", "_1.4_j",
        "_1.4e5_j", "_1_e1", "_1.4_e1", "_1.4_e1j", "_1e_1", "_1.4e_1", "_1.4e_1j", "_1+1.5_j", "_1+1.5_j",
    };

    private static readonly string[] ValidMatchStatements =
    {
        // constant and capture patterns
        """

        match greeting:
            case "":
                print("Hello!")
            case name:
                print(f"Hi {name}!")

        """,

        // pattern unions
        """

        match something:
            case 0 | 1 | 2:
                print("Small number")
            case [] | [_]:
                print("A short sequence")
            case str() | bytes():
                print("Something string-like")
            case _:
                print("Something else")

        """,

        // guards
        """

        match val:
            case [x, y] if x > 0 and y > 0:
                return f"A pair of {x} and {y}"
            case [x, *other]:
                return f"A sequence starting with {x}"
            case int():
                return f"Some integer"

        """,

        // "as" patterns
        """

        match command.split():
            case ["go", ("north" | "south" | "east" | "west") as direction]:
                current_room = current_room.neighbor(direction)

        """,
    };

    private static readonly string[] InvalidMatchStatements =
    {
        // no cases
        """

        match val:
            pass

        """,

        // cases not indented relative to match
        """

        match val:
        case x:
            pass

        """,
    };

    private static readonly Lark PythonParser = Lark.OpenFromResource(typeof(Lark).Assembly, "python.lark", new[] { "Grammars" }, new LarkOptions
    {
        Parser = ParserKind.Lalr,
        PostLex = new PythonIndenter(),
        Start = new[] { "number", "file_input" },
    });

    private static void AssertParsedIsThisTerminal(string text, string terminal, string start)
    {
        var tree = PythonParser.Parse(text, start);
        var token = Assert.IsType<Token>(Assert.Single(tree.Children));
        Assert.Equal(terminal, token.Type);
        Assert.Equal(text, token.Value);
    }

    private static void AssertParsedIsFileContainingOnlyThisStatement(string text, string statement)
    {
        var tree = PythonParser.Parse(text, "file_input");
        var child = Assert.IsType<Tree>(Assert.Single(tree.Children));
        Assert.Equal(statement, child.Data);
    }

    [Fact]
    public void test_DEC_NUMBER()
    {
        foreach (var c in ValidDecNumber) AssertParsedIsThisTerminal(c, "DEC_NUMBER", "number");
    }

    [Fact]
    public void test_HEX_NUMBER()
    {
        foreach (var c in ValidHexNumber) AssertParsedIsThisTerminal(c, "HEX_NUMBER", "number");
    }

    [Fact]
    public void test_OCT_NUMBER()
    {
        foreach (var c in ValidOctNumber) AssertParsedIsThisTerminal(c, "OCT_NUMBER", "number");
    }

    [Fact]
    public void test_BIN_NUMBER()
    {
        foreach (var c in ValidBinNumber) AssertParsedIsThisTerminal(c, "BIN_NUMBER", "number");
    }

    [Fact]
    public void test_FLOAT_NUMBER()
    {
        foreach (var c in ValidFloatNumber) AssertParsedIsThisTerminal(c, "FLOAT_NUMBER", "number");
    }

    [Fact]
    public void test_IMAG_NUMBER()
    {
        foreach (var c in ValidImagNumber) AssertParsedIsThisTerminal(c, "IMAG_NUMBER", "number");
    }

    [Fact]
    public void test_valid_number()
    {
        // XXX: all valid test cases should run with the above tests for numbers
        foreach (var c in ValidNumber) PythonParser.Parse(c, "number"); // no error
    }

    [Fact]
    public void test_invalid_number()
    {
        foreach (var c in InvalidNumber)
        {
            var e = Record.Exception(() => PythonParser.Parse(c, "number"));
            Assert.True(e is UnexpectedCharactersException or UnexpectedTokenException, $"{c}: {e?.GetType().Name ?? "no exception"}");
        }
    }

    [Fact]
    public void test_valid_match_statement()
    {
        foreach (var c in ValidMatchStatements) AssertParsedIsFileContainingOnlyThisStatement(c, "match_stmt");
    }

    [Fact]
    public void test_invalid_match_statement()
    {
        foreach (var c in InvalidMatchStatements)
        {
            ThrowsIface<IParseError>(() => PythonParser.Parse(c, "file_input"));
        }
    }

    [Fact]
    public void test_assign_to_variable_named_match()
    {
        const string text = """

            match = re.match(pattern, string)

            """;

        AssertParsedIsFileContainingOnlyThisStatement(text, "assign_stmt");
    }

    [Fact]
    public void test_assign_expr_with_variable_named_match()
    {
        const string text = """

            if match := re.match(pattern, string):
                do_thing(match)

            """;

        AssertParsedIsFileContainingOnlyThisStatement(text, "if_stmt");
    }
}
