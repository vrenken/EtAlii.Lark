namespace EtAlii.Lark.Tests.Generator;

using System.Globalization;

[LarkGrammar("calc.lark", Parser = ParserKind.Lalr)]
[LarkResult(typeof(double), "add", "sub", "mul", "div", "neg", "number", "var", "NUMBER")]
public partial class TypedCalcParser;

[LarkGrammar(Grammar = """
    start: pair*
    pair: KEY "=" value _NL
    value: STRING | NUMBER | list
    list: "[" [value ("," value)*] "]"
    KEY: /[a-z_]+/
    _NL: /\r?\n/
    %import common.ESCAPED_STRING -> STRING
    %import common.SIGNED_NUMBER -> NUMBER
    %import common.WS_INLINE
    %ignore WS_INLINE
    """, Parser = ParserKind.Lalr, Start = new[] { "start", "value" })]
[LarkResult(typeof(Dictionary<string, ConfigValue>), "start")]
[LarkResult(typeof(KeyValuePair<string, ConfigValue>), "pair")]
[LarkResult(typeof(ConfigValue), "value")]
[LarkResult(typeof(ListValue), "list")]
[LarkResult(typeof(StringValue), "STRING")]
[LarkResult(typeof(NumberValue), "NUMBER")]
[LarkResult(typeof(string), "KEY")]
public static partial class TypedConfigParser;

public abstract record ConfigValue;

public sealed record StringValue(string Value) : ConfigValue;

public sealed record NumberValue(double Value) : ConfigValue;

public sealed record ListValue(IReadOnlyList<ConfigValue> Items) : ConfigValue
{
    public bool Equals(ListValue? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => Items.Count;
}

[LarkGrammar(Grammar = """
    start: greeting _names punctuation?
    greeting: GREETING
    _names: NAME ("and" NAME)*
    !punctuation: "!" | "?"
    GREETING: "hello" | "hi"
    %import common.CNAME -> NAME
    %import common.WS
    %ignore WS
    """, MaybePlaceholders = false)]
[LarkResult(typeof(Greeting), "start")]
public partial class TypedGreetingParser;

public sealed record Greeting(string Words, IReadOnlyList<string> Names, string? Punctuation);

public class TypedVisitorTests
{
    private sealed class CalcEvaluator : TypedCalcParser.Visitor
    {
        private readonly Dictionary<string, double> _variables;

        public CalcEvaluator(Dictionary<string, double>? variables = null)
        {
            _variables = variables ?? new Dictionary<string, double>();
        }

        protected override double VisitAdd(TypedCalcParser.AddNode node, double sum, double product) => sum + product;

        protected override double VisitSub(TypedCalcParser.SubNode node, double sum, double product) => sum - product;

        protected override double VisitMul(TypedCalcParser.MulNode node, double product, double atom) => product * atom;

        protected override double VisitDiv(TypedCalcParser.DivNode node, double product, double atom) => product / atom;

        protected override double VisitNeg(TypedCalcParser.NegNode node, double atom) => -atom;

        protected override double VisitNumber(TypedCalcParser.NumberNode node, double number) => number;

        protected override double VisitVar(TypedCalcParser.VarNode node, Token name) => _variables[name.Value];

        protected override double VisitNumberToken(Token token) => double.Parse(token.Value, CultureInfo.InvariantCulture);
    }

    private sealed class ConfigBuilder : TypedConfigParser.Visitor
    {
        protected override Dictionary<string, ConfigValue> VisitStart(TypedConfigParser.StartNode node, IReadOnlyList<KeyValuePair<string, ConfigValue>> pairs) =>
            new(pairs);

        protected override KeyValuePair<string, ConfigValue> VisitPair(TypedConfigParser.PairNode node, string key, ConfigValue value) => new(key, value);

        protected override ConfigValue VisitValue(TypedConfigParser.ValueNode node, ConfigValue value) => value;

        protected override ListValue VisitList(TypedConfigParser.ListNode node, ConfigValue? first, IReadOnlyList<ConfigValue> rest) =>
            new(first is null ? Array.Empty<ConfigValue>() : rest.Prepend(first).ToArray());

        protected override StringValue VisitStringToken(Token token) => new(token.Value.Trim('"'));

        protected override NumberValue VisitNumberToken(Token token) => new(double.Parse(token.Value, CultureInfo.InvariantCulture));

        protected override string VisitKeyToken(Token token) => token.Value;
    }

    private sealed class GreetingBuilder : TypedGreetingParser.Visitor
    {
        // The _names rule is spliced in: its first NAME, then the repeated ones.
        protected override Greeting VisitStart(TypedGreetingParser.StartNode node, TypedGreetingParser.GreetingNode greeting,
            Token name, IReadOnlyList<Token> others, TypedGreetingParser.PunctuationNode? punctuation) =>
            new(greeting.Greeting.Value, others.Prepend(name).Select(n => n.Value).ToList(), punctuation?.Children[0] is Token p ? p.Value : null);
    }

    [Theory]
    [InlineData("1 + 2", 3)]
    [InlineData("2 * (3 + 4)", 14)]
    [InlineData("-2 - -3", 1)]
    [InlineData("10 / 4 * x", 7.5)]
    public void InlinedStartRule_GetsATypedEntryPoint(string expression, double expected)
    {
        double result = new CalcEvaluator(new() { ["x"] = 3 }).VisitStart(TypedCalcParser.ParseStart(expression));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ChildrenOfInlinedRules_HaveTheMappedType()
    {
        var add = new TypedCalcParser.AddNode(TypedCalcParser.ParseStart("1 + 2"));

        double result = new CalcEvaluator().Visit(add);

        Assert.Equal(3, result);
    }

    [Fact]
    public void MergedAlternatives_RepetitionsAndPlaceholders_AreTyped()
    {
        var config = new ConfigBuilder().Visit(TypedConfigParser.ParseStart("""
            name = "lark"
            sizes = [1, 2, [3]]
            empty = []

            """));

        Assert.Equal(new StringValue("lark"), config["name"]);
        Assert.Equal(new ListValue(new ConfigValue[] { new NumberValue(1), new NumberValue(2), new ListValue(new ConfigValue[] { new NumberValue(3) }) }), config["sizes"]);
        Assert.Equal(new ListValue(Array.Empty<ConfigValue>()), config["empty"]);
    }

    [Fact]
    public void VisitTree_DispatchesOnTheRule()
    {
        var value = new ConfigBuilder().VisitTree(TypedConfigParser.ParseValue("[1]").Tree);

        Assert.Equal(new ListValue(new ConfigValue[] { new NumberValue(1) }), value);
    }

    [Fact]
    public void UnmappedNodes_AreNodes_AndSplicedRulesAndOptionalsAreTyped()
    {
        var builder = new GreetingBuilder();

        var full = builder.Visit(TypedGreetingParser.ParseStart("hello Ada and Grace!"));
        Assert.Equal("hello", full.Words);
        Assert.Equal(new[] { "Ada", "Grace" }, full.Names);
        Assert.Equal("!", full.Punctuation);

        var greeting = builder.Visit(TypedGreetingParser.ParseStart("hi Linus"));
        Assert.Equal("hi", greeting.Words);
        Assert.Equal(new[] { "Linus" }, greeting.Names);
        Assert.Null(greeting.Punctuation);
    }

    [Fact]
    public void UnmappedNodes_DefaultToTheirNode()
    {
        var tree = TypedGreetingParser.ParseStart("hi Linus");
        var greeting = TypedGreetingParser.GreetingNode.From(tree.Children[0]);

        var result = new GreetingBuilder().Visit(greeting);

        Assert.Equal(greeting, result);
    }
}
