namespace EtAlii.Lark.Tests.Conformance;

using System.Text.Json;
using System.Text.Json.Nodes;
using EtAlii.Lark.Generators;

/// <summary>
/// Compares the tree shapes the source generator works out with the <c>shapes</c> of the conformance cases. Those are
/// computed on Lark's compiled grammar by <c>conformance/tools/shapes.py</c>, and the Rust and TypeScript code
/// generators compute the same. The .NET generator reads the grammar text instead, so it differs on some cases; they
/// are listed in <c>conformance/known-failures/dotnet-shapes.txt</c>.
/// </summary>
public class ShapeConformanceTests
{
    private static readonly string CasesDirectory = Path.Combine(AppContext.BaseDirectory, "conformance", "cases");

    private static readonly Lazy<Dictionary<string, JsonObject>> Cases = new(LoadCases);

    private static readonly Lazy<HashSet<string>> KnownFailures = new(() => ConformanceTests.LoadKnownFailures("dotnet-shapes"));

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
        var c = Cases.Value[id];
        var failure = Compare(c, out var actual);
        if (actual is not null)
        {
            // Even where the generator is coarser, its shapes must fit the trees Lark builds.
            var mismatches = new List<string>();
            if ((string?)c["options"]!["ambiguity"] != "explicit")
            {
                foreach (var test in c["tests"]!.AsArray())
                {
                    CheckTree(actual, test!["expect"]!["tree"], mismatches);
                }
            }
            Assert.True(mismatches.Count == 0, "The generated shapes don't fit Lark's trees:\n" + string.Join("\n", mismatches));
        }
        if (KnownFailures.Value.Contains(id))
        {
            Assert.True(failure is not null, $"Case {id} passes now; remove it from conformance/known-failures/dotnet-shapes.txt.");
            return;
        }
        Assert.True(failure is null, failure);
    }

    private static string? Compare(JsonObject c, out JsonObject? actual)
    {
        actual = null;
        var options = c["options"]!.AsObject();
        var keepAllTokens = options["keep_all_tokens"]?.GetValue<bool>() ?? false;
        var maybePlaceholders = options["maybe_placeholders"]?.GetValue<bool>() ?? true;
        try
        {
            actual = ShapesJson(GrammarAnalyzer.Analyze((string)c["grammar"]!, keepAllTokens, maybePlaceholders));
        }
        catch (Exception e)
        {
            return $"The generator failed to analyze the grammar: {e.Message}";
        }
        var expected = c["shapes"]!.AsObject();
        if (JsonNode.DeepEquals(expected, actual)) return null;
        var lines = new List<string> { "Shapes differ:" };
        foreach (var name in expected.Select(p => p.Key).Union(actual.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            var e = expected[name];
            var a = actual[name];
            if (!expected.ContainsKey(name) || !actual.ContainsKey(name) || !JsonNode.DeepEquals(e, a))
            {
                lines.Add($"  {name}:\n    expected {Show(expected, name)}\n    actual   {Show(actual, name)}");
            }
        }
        return string.Join("\n", lines);
    }

    private static void CheckTree(JsonObject shapes, JsonNode? node, List<string> mismatches)
    {
        if (node is not JsonObject tree || !tree.ContainsKey("data")) return;
        var name = (string)tree["data"]!;
        var children = tree["children"]!.AsArray();
        // Templates have no nodes in .NET; their trees aren't typed.
        if (shapes.TryGetPropertyValue(name, out var shape))
        {
            if (shape is JsonValue)
            {
                mismatches.Add($"{name}: a tree, but the generator says it is always inlined");
            }
            else if (shape is JsonArray elements && !Matches(elements, children))
            {
                mismatches.Add($"{name}: {children.ToJsonString()} doesn't match {elements.ToJsonString()}");
            }
        }
        foreach (var child in children)
        {
            CheckTree(shapes, child, mismatches);
        }
    }

    private static bool Matches(JsonArray shape, JsonArray children)
    {
        var i = 0;
        foreach (var element in shape)
        {
            var members = element!["members"]!.AsArray().Select(m => (string)m!).ToHashSet();
            bool Fits(JsonNode? child) => members.Contains("#any") || child switch
            {
                null => members.Contains("#none"),
                JsonObject t when t.ContainsKey("data") => members.Contains((string)t["data"]!),
                JsonObject t => members.Contains("#token") || members.Contains((string)t["type"]!),
                _ => false,
            };
            switch ((string)element["kind"]!)
            {
                case "single":
                    if (i >= children.Count || !Fits(children[i])) return false;
                    i++;
                    break;
                case "optional":
                    if (i < children.Count && Fits(children[i])) i++;
                    break;
                default:
                    while (i < children.Count && Fits(children[i])) i++;
                    break;
            }
        }
        return i == children.Count;
    }

    private static string Show(JsonObject shapes, string name) =>
        shapes.ContainsKey(name) ? shapes[name]?.ToJsonString(new JsonSerializerOptions()) ?? "null" : "(no such tree)";

    private static JsonObject ShapesJson(GrammarModel model)
    {
        var result = new JsonObject();
        foreach (var pair in model.TypedShapes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (model.NeverTrees.Contains(pair.Key))
            {
                result[pair.Key] = "inlined";
                continue;
            }
            if (pair.Value is null)
            {
                result[pair.Key] = null;
                continue;
            }
            var elements = new JsonArray();
            foreach (var e in pair.Value)
            {
                var element = new JsonObject
                {
                    ["kind"] = e.Kind.ToString().ToLowerInvariant(),
                    ["name"] = e.Name,
                    ["members"] = new JsonArray(e.Members.Select(Member).Order(StringComparer.Ordinal).Select(m => (JsonNode)m!).ToArray()),
                };
                if (e.SlotRule is not null) element["slot"] = e.SlotRule;
                elements.Add(element);
            }
            result[pair.Key] = elements;
        }
        return result;
    }

    private static string Member(ShapeMember member) => member.Kind switch
    {
        MemberKind.Tree or MemberKind.Token => member.Name,
        MemberKind.Null => "#none",
        // The generator doesn't name anonymous terminals.
        MemberKind.AnyToken => "#token",
        _ => "#any",
    };

    private static Dictionary<string, JsonObject> LoadCases()
    {
        var cases = new Dictionary<string, JsonObject>();
        foreach (var file in Directory.GetFiles(CasesDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var node in root["cases"]!.AsArray())
            {
                var c = node!.AsObject();
                if (c.ContainsKey("shapes")) cases.Add((string)c["id"]!, c);
            }
        }
        return cases;
    }
}
