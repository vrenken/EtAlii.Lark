namespace EtAlii.Lark.Tests.Conformance;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// Runs the language-neutral conformance suite in <c>conformance/cases</c>, whose expected results were produced by
/// Python Lark. The Rust and TypeScript packages run the same cases, so all ports interpret grammars identically.
/// </summary>
public class ConformanceTests
{
    private static readonly string CasesDirectory = Path.Combine(AppContext.BaseDirectory, "conformance", "cases");

    private static readonly Lazy<Dictionary<string, JsonObject>> Cases = new(LoadCases);

    private static readonly Lazy<HashSet<string>> KnownFailures = new(() => LoadKnownFailures("dotnet"));

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in Cases.Value.Keys)
        {
            data.Add(id);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Case(string id)
    {
        var failures = ConformanceRunner.Run(Cases.Value[id]);
        if (KnownFailures.Value.Contains(id))
        {
            Assert.True(failures.Count > 0, $"Case {id} passes now; remove it from conformance/known-failures/dotnet.txt.");
            return;
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    private static Dictionary<string, JsonObject> LoadCases()
    {
        var cases = new Dictionary<string, JsonObject>();
        foreach (var file in Directory.GetFiles(CasesDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var node in root["cases"]!.AsArray())
            {
                var c = node!.AsObject();
                cases.Add((string)c["id"]!, c);
            }
        }
        return cases;
    }

    internal static HashSet<string> LoadKnownFailures(string port)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "conformance", "known-failures", port + ".txt");
        var result = new HashSet<string>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Split('#')[0].Trim();
            if (text.Length > 0) result.Add(text.Split(' ', 2)[0]);
        }
        return result;
    }
}

/// <summary>Runs one conformance case and describes how the results differ from the expected ones.</summary>
internal static class ConformanceRunner
{
    public static List<string> Run(JsonObject c)
    {
        var failures = new List<string>();
        var grammar = (string)c["grammar"]!;
        Lark parser;
        try
        {
            parser = new Lark(grammar, Options(c["options"]!.AsObject()));
        }
        catch (Exception e) when (c["grammar_error"] is not null)
        {
            var expected = (string)c["grammar_error"]!;
            if (ErrorName(e) != expected)
            {
                failures.Add($"Expected grammar error {expected}, got {e.GetType().Name}: {e.Message}");
            }
            return failures;
        }
        catch (Exception e)
        {
            failures.Add($"The grammar failed to load: {e.GetType().Name}: {e.Message}");
            return failures;
        }
        if (c["grammar_error"] is not null)
        {
            failures.Add($"Expected grammar error {(string)c["grammar_error"]!}, but the grammar loaded.");
            return failures;
        }

        foreach (var node in c["tests"]!.AsArray())
        {
            var test = node!.AsObject();
            var expect = test["expect"]!.AsObject();
            var isLex = test["lex"] is not null;
            var text = (string)(isLex ? test["lex"] : test["input"])!;
            var positions = new Positions(text);
            JsonNode? actual;
            try
            {
                if (isLex)
                {
                    var dontIgnore = test["dont_ignore"] is not null && (bool)test["dont_ignore"]!;
                    var tokens = new JsonArray();
                    foreach (var token in parser.Lex(text, dontIgnore)) tokens.Add(TokenJson(token, positions));
                    actual = new JsonObject { ["tokens"] = tokens };
                }
                else
                {
                    var tree = parser.Parse(text, (string?)test["start"]);
                    actual = new JsonObject { ["tree"] = TreeJson(tree, positions) };
                }
            }
            catch (Exception e)
            {
                actual = ErrorJson(e, positions);
            }

            if (!JsonNode.DeepEquals(Normalize(expect), Normalize(actual)))
            {
                failures.Add($"Input {Show(text)}:\n  expected {expect.ToJsonString()}\n  actual   {actual?.ToJsonString()}");
            }
        }
        return failures;
    }

    private static LarkOptions Options(JsonObject options)
    {
        // Positions are always propagated, as when the expected results were generated, and compared too.
        var result = new LarkOptions { PropagatePositions = true };
        foreach (var (name, value) in options)
        {
            switch (name)
            {
                case "parser":
                    result.Parser = (string)value! switch { "lalr" => ParserKind.Lalr, "cyk" => ParserKind.Cyk, _ => ParserKind.Earley };
                    break;
                case "lexer":
                    result.Lexer = (string)value! switch
                    {
                        "basic" => LexerKind.Basic,
                        "contextual" => LexerKind.Contextual,
                        "dynamic" => LexerKind.Dynamic,
                        "dynamic_complete" => LexerKind.DynamicComplete,
                        _ => LexerKind.Auto,
                    };
                    break;
                case "start":
                    result.Start = value is JsonArray array ? array.Select(s => (string)s!).ToArray() : new[] { (string)value! };
                    break;
                case "ambiguity":
                    result.Ambiguity = (string)value! switch { "resolve" => Ambiguity.Resolve, "explicit" => Ambiguity.Explicit, _ => Ambiguity.Auto };
                    break;
                case "priority":
                    result.Priority = (string?)value switch { "normal" => PriorityMode.Normal, "invert" => PriorityMode.Invert, null => PriorityMode.None, _ => PriorityMode.Auto };
                    break;
                case "maybe_placeholders":
                    result.MaybePlaceholders = (bool)value!;
                    break;
                case "keep_all_tokens":
                    result.KeepAllTokens = (bool)value!;
                    break;
                case "strict":
                    result.Strict = (bool)value!;
                    break;
                case "regex_flags":
                    foreach (var flag in (string)value!)
                    {
                        result.RegexOptions |= flag switch
                        {
                            'i' => RegexOptions.IgnoreCase,
                            'm' => RegexOptions.Multiline,
                            's' => RegexOptions.Singleline,
                            'x' => RegexOptions.IgnorePatternWhitespace,
                            _ => RegexOptions.None,
                        };
                    }
                    break;
                case "postlex":
                    result.PostLex = (string)value! switch
                    {
                        "python_indenter" => new PythonIndenter(),
                        var other => throw new NotSupportedException("Unknown post-lexer " + other),
                    };
                    break;
                default:
                    throw new NotSupportedException("Unknown conformance option " + name);
            }
        }
        return result;
    }

    private static string? ErrorName(Exception e) => e switch
    {
        UnexpectedCharactersException => "UnexpectedCharacters",
        UnexpectedTokenException => "UnexpectedToken",
        UnexpectedEofException => "UnexpectedEOF",
        GrammarException => "GrammarError",
        ConfigurationException => "ConfigurationError",
        ParseException => "ParseError",
        LexException => "LexError",
        _ => null,
    };

    private static JsonObject ErrorJson(Exception e, Positions positions)
    {
        var result = new JsonObject { ["error"] = ErrorName(e) ?? "Exception:" + e.GetType().Name + ": " + e.Message };
        if (e is UnexpectedInputException u)
        {
            result["line"] = u.Line;
            result["column"] = u.Line > 0 && u.PosInStream is int pos ? positions.Column(pos, u.Column) : u.Column;
        }
        switch (e)
        {
            case UnexpectedTokenException ut:
                result["token"] = new JsonObject { ["type"] = ut.Token.Type, ["value"] = ut.Token.Value };
                result["expected"] = Sorted(ut.Expected);
                break;
            case UnexpectedEofException ue:
                result["expected"] = Sorted(ue.Expected);
                break;
            case UnexpectedCharactersException uc:
                result["char"] = uc.Char.ToString();
                if (uc.Allowed is not null) result["expected"] = Sorted(uc.Allowed);
                break;
        }
        return result;
    }

    private static JsonArray Sorted(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Distinct().Order(StringComparer.Ordinal)) array.Add(value);
        return array;
    }

    private static JsonNode? TreeJson(object? node, Positions positions) => node switch
    {
        Tree tree => TreeObject(tree, positions),
        Token token => TokenJson(token, positions),
        null => null,
        _ => JsonValue.Create("<" + node.GetType().Name + ">"),
    };

    private static JsonObject TreeObject(Tree tree, Positions positions)
    {
        var result = new JsonObject
        {
            ["data"] = tree.Data,
            ["children"] = new JsonArray(tree.Children.Select(child => TreeJson(child, positions)).ToArray()),
        };
        var meta = tree.Meta;
        if (!meta.Empty)
        {
            result["meta"] = new JsonArray(
                positions.Offset(meta.StartPos),
                positions.Offset(meta.EndPos),
                meta.Line,
                meta.StartPos is int s && meta.Column is int c ? positions.Column(s, c) : meta.Column,
                meta.EndLine,
                meta.EndPos is int e && meta.EndColumn is int ec ? positions.Column(e, ec) : meta.EndColumn);
        }
        return result;
    }

    private static JsonObject TokenJson(Token token, Positions positions) => new()
    {
        ["type"] = token.Type,
        ["value"] = token.Value,
        ["pos"] = new JsonArray(
            positions.Offset(token.StartPos),
            positions.Offset(token.EndPos),
            token.Line,
            token.StartPos is int s && token.Column is int c ? positions.Column(s, c) : token.Column,
            token.EndLine,
            token.EndPos is int e && token.EndColumn is int ec ? positions.Column(e, ec) : token.EndColumn),
    };

    /// <summary>Lark counts positions in code points; .NET in UTF-16 code units. This converts .NET positions.</summary>
    private sealed class Positions
    {
        private readonly int[] _codePoints;

        public Positions(string text)
        {
            _codePoints = new int[text.Length + 1];
            var count = 0;
            for (var i = 0; i < text.Length; i++)
            {
                _codePoints[i] = count;
                if (!(char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))) count++;
            }
            _codePoints[text.Length] = count;
        }

        public int? Offset(int? offset) => offset is int o && o >= 0 && o < _codePoints.Length ? _codePoints[o] : offset;

        public int Column(int offset, int column)
        {
            var lineStart = offset - (column - 1);
            if (offset < 0 || offset >= _codePoints.Length || lineStart < 0) return column;
            return _codePoints[offset] - _codePoints[lineStart] + 1;
        }
    }

    // Python Lark doesn't always report every detail; null values are left out before comparing.
    private static JsonNode? Normalize(JsonNode? node) => node?.DeepClone();

    private static string Show(string text) => text.Length > 200 ? System.Text.Json.JsonSerializer.Serialize(text[..200]) + "..." : System.Text.Json.JsonSerializer.Serialize(text);
}
