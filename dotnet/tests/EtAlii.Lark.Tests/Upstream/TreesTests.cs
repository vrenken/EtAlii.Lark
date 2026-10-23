// Ported from lark/tests/test_trees.py.
// Python-specific tests (pickle, repr/eval, functools.partial, staticmethod descriptors, properties) are not applicable.
// ReSharper disable InconsistentNaming
#pragma warning disable IDE1006
namespace EtAlii.Lark.Tests.Upstream;

using static TestHelpers;

public class TreesTests
{
    private readonly Tree _tree1 = T("a", T("b", "x"), T("c", "y"), T("d", "z"));

    private readonly Tree _tree2 = T("a",
        T("b", Tok("T", "x")),
        T("c", Tok("T", "y")),
        T("d", T("z", Tok("T", "zz"), T("zzz", "z", "z", "z"))));

    [Fact]
    public void test_eq()
    {
        Assert.Equal(_tree1, _tree1);
        Assert.False(_tree1.Equals(0));
    }

    [Fact]
    public void test_copy()
    {
        Assert.Equal(_tree1, _tree1.Copy());
    }

    [Fact]
    public void test_deepcopy()
    {
        Assert.Equal(_tree1, _tree1.DeepCopy());
    }

    [Fact]
    public void test_iter_subtrees()
    {
        var expected = new[] { T("b", "x"), T("c", "y"), T("d", "z"), T("a", T("b", "x"), T("c", "y"), T("d", "z")) };
        Assert.Equal(expected, _tree1.IterSubtrees());
    }

    [Fact]
    public void test_iter_subtrees_topdown()
    {
        var expected = new[] { T("a", T("b", "x"), T("c", "y"), T("d", "z")), T("b", "x"), T("c", "y"), T("d", "z") };
        Assert.Equal(expected, _tree1.IterSubtreesTopDown());
    }

    [Fact]
    public void test_find_token()
    {
        var expected = new[] { Tok("T", "x"), Tok("T", "y"), Tok("T", "zz") };
        Assert.Equal(expected, _tree2.FindToken("T"));
    }

    private class Visitor1 : Visitor
    {
        public readonly List<Tree> Nodes = new();

        protected override void Default(Tree tree) => Nodes.Add(tree);
    }

    private class Visitor1Recursive : RecursiveVisitor
    {
        public readonly List<Tree> Nodes = new();

        protected override void Default(Tree tree) => Nodes.Add(tree);
    }

    [Fact]
    public void test_visitor()
    {
        var visitor1 = new Visitor1();
        var visitor1Recursive = new Visitor1Recursive();

        var expectedTopDown = new[] { T("a", T("b", "x"), T("c", "y"), T("d", "z")), T("b", "x"), T("c", "y"), T("d", "z") };
        var expectedBottomUp = new[] { T("b", "x"), T("c", "y"), T("d", "z"), T("a", T("b", "x"), T("c", "y"), T("d", "z")) };

        visitor1.Visit(_tree1);
        Assert.Equal(expectedBottomUp, visitor1.Nodes);

        visitor1Recursive.Visit(_tree1);
        Assert.Equal(expectedBottomUp, visitor1Recursive.Nodes);

        visitor1.Nodes.Clear();
        visitor1Recursive.Nodes.Clear();

        visitor1.VisitTopDown(_tree1);
        Assert.Equal(expectedTopDown, visitor1.Nodes);

        visitor1Recursive.VisitTopDown(_tree1);
        Assert.Equal(expectedTopDown, visitor1Recursive.Nodes);
    }

    private class Interp1 : Interpreter
    {
        public object a(Tree tree) => VisitChildren(tree).Append("e").ToList();

        public object b(Tree tree) => "B";

        public object c(Tree tree) => "C";
    }

    private class Interp2 : Interpreter
    {
        // A method accepting a list receives the visited children (Lark's visit_children_decor).
        public object a(List<object?> values) => values.Append("e").ToList();

        public object b(Tree tree) => "B";

        public object c(Tree tree) => "C";
    }

    private class Interp3 : Interpreter
    {
        public object b(Tree tree) => "B";

        public object c(Tree tree) => "C";
    }

    [Fact]
    public void test_interp()
    {
        var t = T("a", T("b"), T("c"), "d");

        AssertEq(new[] { "B", "C", "d", "e" }, new Interp1().Visit(t));
        AssertEq(new[] { "B", "C", "d", "e" }, new Interp2().Visit(t));
        AssertEq(new[] { "B", "C", "d" }, new Interp3().Visit(t));
    }

    private class Transformer1 : Transformer
    {
        [VArgs(Inline = true)]
        public object i(int value) => value;

        [VArgs(Inline = true)]
        public object f(double value) => value;

        public object sub(List<object?> values) => Convert.ToDouble(values[0]) - Convert.ToDouble(values[1]);

        public object add(List<object?> values) => values.Sum(Convert.ToDouble);
    }

    [VArgs(Inline = true)]
    private class Transformer2 : Transformer
    {
        public object i(int value) => value;

        public object f(double value) => value;

        public object sub(double a, double b) => a - b;

        public object add(double a, double b) => a + b;
    }

    [Fact]
    public void test_transformer()
    {
        var t = T("add", T("sub", T("i", "3"), T("f", "1.1")), T("i", "1"));

        Assert.Equal(2.9, new Transformer1().Transform(t));
        Assert.Equal(2.9, new Transformer2().Transform(t));
    }

    [VArgs]
    private class MyVArgsTransformer : Transformer
    {
        public static object integer(List<object?> args) => 1;

        public static object integer2(List<object?> args) => 2;

        public static object hello(List<object?> args) => "hello";
    }

    [Fact]
    public void test_vargs()
    {
        Assert.Equal(1, new MyVArgsTransformer().Transform(T("integer", 2)));
        Assert.Equal(2, new MyVArgsTransformer().Transform(T("integer2", 2)));
        Assert.Equal("hello", new MyVArgsTransformer().Transform(T("hello", 2)));
    }

    [VArgs(Inline = true)]
    private class InlineStatic : Transformer
    {
        public static object test(string a, string b) => a + b;
    }

    [Fact]
    public void test_inline_static()
    {
        Assert.Equal("ab", new InlineStatic().Transform(T("test", "a", "b")));
    }

    [VArgs(Inline = true)]
    private class VArgsOverride : Transformer
    {
        public object i(int value) => value;

        public object f(double value) => value;

        public object sub(double a, double b) => a - b;

        [VArgs(Inline = false)]
        public object add(List<object?> values) => values.Sum(Convert.ToDouble);
    }

    [Fact]
    public void test_vargs_override()
    {
        var t = T("add", T("sub", T("i", "3"), T("f", "1.1")), T("i", "1"));
        Assert.Equal(2.9, new VArgsOverride().Transform(t));
    }

    private class DiscardTransformer : Transformer
    {
        public object a(List<object?> args) => 1;

        public object b(List<object?> args) => Discard.Value;
    }

    [Fact]
    public void test_discard()
    {
        var t = T("root", T("b"), T("a"), T("b"), T("c"), T("b"));
        var t2 = T("root", 1, T("c"));

        AssertEq(t2, new DiscardTransformer().Transform(t));
    }

    private class VariantT : Transformer
    {
        public object add(List<object?> children) => children.Sum(Convert.ToInt32);

        public object N(Token token) => int.Parse(token.Value);

        public object ignore_tree(List<object?> children) => Discard.Value;

        public object IGNORE_TOKEN(Token token) => Discard.Value;
    }

    private class VariantInPlace : InPlaceTransformer
    {
        public object add(List<object?> children) => children.Sum(Convert.ToInt32);

        public object N(Token token) => int.Parse(token.Value);

        public object ignore_tree(List<object?> children) => Discard.Value;

        public object IGNORE_TOKEN(Token token) => Discard.Value;
    }

    private class VariantNonRecursive : NonRecursiveTransformer
    {
        public object add(List<object?> children) => children.Sum(Convert.ToInt32);

        public object N(Token token) => int.Parse(token.Value);

        public object ignore_tree(List<object?> children) => Discard.Value;

        public object IGNORE_TOKEN(Token token) => Discard.Value;
    }

    private class VariantInPlaceRecursive : InPlaceRecursiveTransformer
    {
        public object add(List<object?> children) => children.Sum(Convert.ToInt32);

        public object N(Token token) => int.Parse(token.Value);

        public object ignore_tree(List<object?> children) => Discard.Value;

        public object IGNORE_TOKEN(Token token) => Discard.Value;
    }

    [Fact]
    public void test_transformer_variants()
    {
        var tree = T("start",
            T("add", Tok("N", "1"), Tok("N", "2"), Tok("IGNORE_TOKEN", "4")),
            T("add", Tok("N", "3"), Tok("N", "4")),
            T("ignore_tree", Tok("DO", "NOT PANIC")));
        foreach (var t in new Transformer[] { new VariantT(), new VariantInPlace(), new VariantNonRecursive(), new VariantInPlaceRecursive() })
        {
            var copied = tree.DeepCopy();
            var result = t.Transform(copied);
            AssertEq(T("start", 3, 7), result);
        }
    }

    private static int Sum(List<object?> children) => children.Sum(Convert.ToInt32);

    private class MergeT1 : Transformer
    {
        public object A(Token t) => int.Parse(t.Value);

        public object B(Token t) => int.Parse(t.Value);

        public object main(List<object?> children) => Sum(children);

        public object start(List<object?> children) => children;

        public object module__main(List<object?> children) => Sum(children);
    }

    private class MergeT2 : Transformer
    {
        public object A(Token t) => int.Parse(t.Value);

        public object B(Token t) => int.Parse(t.Value);

        public object main(List<object?> children) => Sum(children);

        public object start(List<object?> children) => children;
    }

    private class MergeT3 : Transformer
    {
        public object main(List<object?> children) => Sum(children);
    }

    private class MergeT4 : Transformer
    {
        public object main(List<object?> children) => Sum(children);
    }

    [Fact]
    public void test_merge_transformers()
    {
        var tree = T("start",
            T("main", Tok("A", "1"), Tok("B", "2")),
            T("module__main", Tok("A", "2"), Tok("B", "3")));

        var t1Res = new MergeT1().Transform(tree);
        var composedRes = Transformer.Merge(new MergeT2(), ("module", new MergeT3())).Transform(tree);
        AssertEq(t1Res, composedRes);

        var composedRes2 = Transformer.Merge(new MergeT2(), ("module", new MergeT4())).Transform(tree);
        AssertEq(t1Res, composedRes2);

        Assert.Throws<InvalidOperationException>(() => Transformer.Merge(new MergeT1(), ("module", new MergeT3())));
    }

    private class IntTransformer : Transformer
    {
        public object INT(Token value) => int.Parse(value.Value);
    }

    private class DiscardIntTransformer : Transformer
    {
        public object INT(Token value) => Discard.Value;
    }

    [Fact]
    public void test_transform_token()
    {
        var t = Tok("INT", "123");
        Assert.Equal(123, new IntTransformer().Transform(t));
        Assert.Null(new DiscardIntTransformer().Transform(t));
    }
}
