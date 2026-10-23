// Ported from lark/tests/test_tree_forest_transformer.py.
// ReSharper disable InconsistentNaming
#pragma warning disable IDE1006
namespace EtAlii.Lark.Tests.Upstream;

using EtAlii.Lark.Parsers;

public class TreeForestTransformerTests
{
    private const string Grammar = """
        start: ab bc cd
        !ab: "A" "B"?
        !bc: "B"? "C"?
        !cd: "C"? "D"
        """;

    private static readonly Lark Parser = new(Grammar, new LarkOptions { Parser = ParserKind.Earley, Ambiguity = Ambiguity.Forest });

    private static ForestNode Forest => Parser.Parse<ForestNode>("ABCD");

    [Fact]
    public void test_identity_resolve_ambiguity()
    {
        var l = new Lark(Grammar, new LarkOptions { Parser = ParserKind.Earley, Ambiguity = Ambiguity.Resolve });
        var tree1 = l.Parse("ABCD");
        var tree2 = new TreeForestTransformer(resolveAmbiguity: true).Transform(Forest);
        Assert.Equal(tree1, tree2);
    }

    [Fact]
    public void test_identity_explicit_ambiguity()
    {
        var l = new Lark(Grammar, new LarkOptions { Parser = ParserKind.Earley, Ambiguity = Ambiguity.Explicit });
        var tree1 = l.Parse("ABCD");
        var tree2 = new TreeForestTransformer(resolveAmbiguity: false).Transform(Forest);
        Assert.Equal(tree1, tree2);
    }

    private sealed class CustomTree : Tree
    {
        public CustomTree(string data, List<object?> children) : base(data, children) { }
    }

    private sealed class TreeChecker : Visitor
    {
        protected override void Default(Tree tree) => Assert.IsType<CustomTree>(tree);
    }

    [Fact]
    public void test_tree_class()
    {
        var tree = new TreeForestTransformer((d, c) => new CustomTree(d, c), resolveAmbiguity: false).Transform(Forest);
        new TreeChecker().Visit((Tree)tree!);
    }

    private sealed class TokenCallsTransformer : TreeForestTransformer
    {
        public readonly bool[] Visited = new bool[4];

        public TokenCallsTransformer() : base(resolveAmbiguity: false) { }

        public object? A(Token node) { Assert.Equal("A", node.Type); Visited[0] = true; return null; }

        public object? B(Token node) { Assert.Equal("B", node.Type); Visited[1] = true; return null; }

        public object? C(Token node) { Assert.Equal("C", node.Type); Visited[2] = true; return null; }

        public object? D(Token node) { Assert.Equal("D", node.Type); Visited[3] = true; return null; }
    }

    [Fact]
    public void test_token_calls()
    {
        var t = new TokenCallsTransformer();
        t.Transform(Forest);
        Assert.Equal(new[] { true, true, true, true }, t.Visited);
    }

    private sealed class DefaultTokenTransformer : TreeForestTransformer
    {
        public int TokenCount;

        public DefaultTokenTransformer() : base(resolveAmbiguity: true) { }

        protected override object? DefaultToken(object token)
        {
            TokenCount++;
            Assert.IsType<Token>(token);
            return null;
        }
    }

    [Fact]
    public void test_default_token()
    {
        var t = new DefaultTokenTransformer();
        t.Transform(Forest);
        Assert.Equal(4, t.TokenCount);
    }

    private sealed class RuleCallsTransformer : TreeForestTransformer
    {
        public bool VisitedStart, VisitedAb, VisitedBc, VisitedCd;

        public RuleCallsTransformer() : base(resolveAmbiguity: false) { }

        public object? start(List<object?> data) { VisitedStart = true; return null; }

        public object? ab(List<object?> data) { VisitedAb = true; return null; }

        public object? bc(List<object?> data) { VisitedBc = true; return null; }

        public object? cd(List<object?> data) { VisitedCd = true; return null; }
    }

    [Fact]
    public void test_rule_calls()
    {
        var t = new RuleCallsTransformer();
        t.Transform(Forest);
        Assert.True(t.VisitedStart);
        Assert.True(t.VisitedAb);
        Assert.True(t.VisitedBc);
        Assert.True(t.VisitedCd);
    }

    private sealed class DefaultRuleTransformer : TreeForestTransformer
    {
        public int RuleCount;

        public DefaultRuleTransformer() : base(resolveAmbiguity: true) { }

        protected override object? Default(string name, List<object?> data)
        {
            RuleCount++;
            return null;
        }
    }

    [Fact]
    public void test_default_rule()
    {
        var t = new DefaultRuleTransformer();
        t.Transform(Forest);
        Assert.Equal(4, t.RuleCount);
    }

    private sealed class DefaultAmbigTransformer : TreeForestTransformer
    {
        public int AmbigCount;

        public DefaultAmbigTransformer() : base(resolveAmbiguity: false) { }

        protected override object? DefaultAmbig(string name, List<object?> data)
        {
            if (data.Count > 1) AmbigCount++;
            return null;
        }
    }

    [Fact]
    public void test_default_ambig()
    {
        var t = new DefaultAmbigTransformer();
        t.Transform(Forest);
        Assert.Equal(1, t.AmbigCount);
    }

    private sealed class HandlesAmbiguityTransformer : TreeForestTransformer
    {
        public HandlesAmbiguityTransformer() : base(resolveAmbiguity: false) { }

        [HandlesAmbiguity]
        public object? start(List<object?> data)
        {
            Assert.Equal(4, data.Count);
            foreach (var tree in data) Assert.Equal("start", ((Tree)tree!).Data);
            return "handled";
        }

        [HandlesAmbiguity]
        public object? ab(List<object?> data)
        {
            Assert.Single(data);
            Assert.Equal("ab", ((Tree)data[0]!).Data);
            return null;
        }
    }

    [Fact]
    public void test_handles_ambiguity()
    {
        var tree = new HandlesAmbiguityTransformer().Transform(Forest);
        Assert.Equal("handled", tree);
    }

    private sealed class DiscardTransformer : TreeForestTransformer
    {
        public DiscardTransformer() : base(resolveAmbiguity: false) { }

        public object? bc(List<object?> data) => Discard.Value;

        public object? D(Token node) => Discard.Value;
    }

    private sealed class DiscardChecker : Transformer
    {
        public DiscardChecker() : base(visitTokens: true) { }

        public object? bc(List<object?> children) => throw new InvalidOperationException("bc should have been discarded");

        public object? D(Token token) => throw new InvalidOperationException("D should have been discarded");
    }

    [Fact]
    public void test_discard()
    {
        var tree = new DiscardTransformer().Transform(Forest);
        new DiscardChecker().Transform((Tree)tree!);
    }

    private sealed class AliasesTransformer : TreeForestTransformer
    {
        public bool VisitedAmbiguous, VisitedFull;

        public AliasesTransformer() : base(resolveAmbiguity: false) { }

        [HandlesAmbiguity]
        public object? start(List<object?> data)
        {
            foreach (var tree in data) Assert.Contains(((Tree)tree!).Data, new[] { "ambiguous", "full" });
            return null;
        }

        public object? ambiguous(List<object?> data)
        {
            VisitedAmbiguous = true;
            Assert.Equal(3, data.Count);
            Assert.Equal("ab", ((Tree)data[0]!).Data);
            Assert.Equal("bc", ((Tree)data[1]!).Data);
            Assert.Equal("cd", ((Tree)data[2]!).Data);
            return new Tree("ambiguous", data);
        }

        public object? full(List<object?> data)
        {
            VisitedFull = true;
            Assert.Single(data);
            Assert.Equal("abcd", ((Tree)data[0]!).Data);
            return new Tree("full", data);
        }
    }

    [Fact]
    public void test_aliases()
    {
        const string grammar = """
            start: ab bc cd -> ambiguous
                | abcd -> full
            !ab: "A" "B"?
            !bc: "B"? "C"?
            !cd: "C"? "D"
            !abcd: "ABCD"
            """;

        var l = new Lark(grammar, new LarkOptions { Parser = ParserKind.Earley, Ambiguity = Ambiguity.Forest });
        var forest = l.Parse<ForestNode>("ABCD");
        var t = new AliasesTransformer();
        t.Transform(forest);
        Assert.True(t.VisitedAmbiguous);
        Assert.True(t.VisitedFull);
    }

    private sealed class TransformationTransformer : TreeForestTransformer
    {
        public TransformationTransformer() : base(resolveAmbiguity: false) { }

        protected override object? Default(string name, List<object?> data)
        {
            var result = new List<object?>();
            foreach (var item in data)
            {
                if (item is List<object?> list) result.AddRange(list);
                else result.Add(item);
            }
            return result;
        }

        protected override object? DefaultToken(object token) => ((Token)token).Value.ToLowerInvariant();

        protected override object? DefaultAmbig(string name, List<object?> data) => data[0];
    }

    [Fact]
    public void test_transformation()
    {
        var result = new TransformationTransformer().Transform(Forest);
        Assert.Equal(new object?[] { "a", "b", "c", "d" }, (List<object?>)result!);
    }
}
