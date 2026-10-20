namespace EtAlii.Lark.Tests.Generator;

using System.Globalization;

public class GeneratedParserTests
{
    private sealed class CalcEvaluator : CalcParser.Visitor<double>
    {
        private readonly Dictionary<string, double> _variables;

        public CalcEvaluator(Dictionary<string, double>? variables = null)
        {
            _variables = variables ?? new Dictionary<string, double>();
        }

        public override double VisitAdd(CalcParser.AddNode node) => Visit(node.Sum) + Visit(node.Product);

        public override double VisitSub(CalcParser.SubNode node) => Visit(node.Sum) - Visit(node.Product);

        public override double VisitMul(CalcParser.MulNode node) => Visit(node.Product) * Visit(node.Atom);

        public override double VisitDiv(CalcParser.DivNode node) => Visit(node.Product) / Visit(node.Atom);

        public override double VisitNeg(CalcParser.NegNode node) => -Visit(node.Atom);

        public override double VisitNumber(CalcParser.NumberNode node) => double.Parse(node.Number.Value, CultureInfo.InvariantCulture);

        public override double VisitVar(CalcParser.VarNode node) => _variables[node.Name.Value];
    }

    [Theory]
    [InlineData("1 + 2", 3)]
    [InlineData("2 * (3 + 4)", 14)]
    [InlineData("10 / 4 - -1", 3.5)]
    [InlineData("x * x + y", 13)]
    public void Visitor_EvaluatesExpressions(string expression, double expected)
    {
        var tree = CalcParser.Parse(expression);
        var result = new CalcEvaluator(new Dictionary<string, double> { ["x"] = 3, ["y"] = 4 }).Visit(tree);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ParseAsync_ParsesReader()
    {
        var tree = await CalcParser.ParseAsync(new StringReader("1 + 2 * 3"));
        Assert.Equal(7, new CalcEvaluator().Visit(tree));
    }

    [Fact]
    public void Instance_UsesAttributeOptions()
    {
        Assert.Equal(ParserKind.Lalr, CalcParser.Instance.Options.Parser);
        Assert.Same(CalcParser.Instance, CalcParser.Instance);
    }

    [Fact]
    public void Names_AreConstants()
    {
        Assert.Equal("add", CalcParser.Rules.Add);
        Assert.Equal("sum", CalcParser.Rules.Sum);
        Assert.Equal("NUMBER", CalcParser.Terminals.NUMBER);
        Assert.Equal("NAME", CalcParser.Terminals.NAME);
        Assert.Equal("add", CalcParser.AddNode.RuleName);
    }

    [Fact]
    public void Node_WrapsOnlyItsRule()
    {
        var tree = CalcParser.Parse("1 + 2");

        Assert.True(CalcParser.AddNode.Is(tree));
        Assert.False(CalcParser.MulNode.Is(tree));
        Assert.True(CalcParser.AddNode.TryFrom(tree, out var add));
        Assert.Equal(tree, (Tree)add);
        Assert.False(CalcParser.MulNode.TryFrom(tree, out _));
        Assert.Throws<ArgumentException>(() => new CalcParser.MulNode(tree));
    }

    [Fact]
    public void Visitor_ThrowsForRulesWithoutVisitMethod()
    {
        var e = Assert.Throws<NotSupportedException>(() => new CalcEvaluator().Visit(new Tree("unknown", new List<object?>())));
        Assert.Contains("'unknown'", e.Message);
    }

    [Fact]
    public void TypedStartRule_ReturnsNode()
    {
        CalcParser.StartNode? notGenerated = null; // ?start is inlined, so ParseStart returns a Tree; the node type still exists.
        Assert.Null(notGenerated);
        Tree tree = CalcParser.ParseStart("1");
        Assert.Equal("number", tree.Data);

        ConfigParser.StartNode start = ConfigParser.ParseStart("""
            name = "lark"
            sizes = [1, 2, 3]

            """);
        var pairs = start.Children.Select(ConfigParser.PairNode.From).ToList();
        Assert.Equal(new[] { "name", "sizes" }, pairs.Select(p => p.Key.Value));
        Assert.Equal("\"lark\"", ((Token)pairs[0].Value.Children[0]!).Value);
        Assert.True(ConfigParser.ListNode.Is(pairs[1].Value.Children[0]));
    }

    [Fact]
    public async Task TypedStartRule_ParsesAsync()
    {
        var value = await ConfigParser.ParseValueAsync(new StringReader("[1, [2]]"));
        Assert.Equal(ConfigParser.Rules.Value, value.Tree.Data);
    }

    [Fact]
    public void NestedClass_IsGeneratedAndConfigured()
    {
        var tree = Outer.GreetingParser.ParseStart("hello world");
        Assert.Equal("world", tree.Name.Value);
        Assert.Equal(1, tree.Name.Line);
        Assert.True(Outer.GreetingParser.Instance.Options.PropagatePositions);
        Assert.Equal(1, Outer.GreetingParser.ConfigureCalls);
    }

    [Fact]
    public void KeepAllTokensRule_HasNoFixedShape()
    {
        Assert.Equal("flag", ConfigParser.FlagNode.RuleName);
        Assert.DoesNotContain(typeof(ConfigParser.FlagNode).GetProperties(), p => p.Name.StartsWith("Token", StringComparison.Ordinal));
    }
}
