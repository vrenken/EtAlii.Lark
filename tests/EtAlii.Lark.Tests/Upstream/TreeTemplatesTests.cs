// Ported from lark/tests/test_tree_templates.py.
// ReSharper disable InconsistentNaming
#pragma warning disable IDE1006
namespace EtAlii.Lark.Tests.Upstream;

using static TestHelpers;

public class TreeTemplatesTests
{
    private const string SomeNonTemplatedString = "foo bar";
    private const string SomeTemplateName = "thing";
    private const string SomeTemplateString = "$" + SomeTemplateName;
    private const int SomeNonString = 12345;

    private const string SomeTemplatingGrammar = """
        start: DASHES? foo DASHES? bar
        DASHES: "--"
        foo: "foo"
            | TEMPLATE_NAME -> var
        bar: "bar"
            | TEMPLATE_NAME -> var
        TEMPLATE_NAME: "$" NAME
        NAME: /[^\W\d]\w*/
        %ignore /[\t \f]+/  // WS
        """;

    private const string SomeFooTemplate = SomeTemplateString + " bar";
    private const string SomeBarTemplate = "foo " + SomeTemplateString;

    private static Tree SomeNonTemplateTree => T("foo", "hi");

    private static readonly Lark Parser = new(SomeTemplatingGrammar);

    private static readonly TemplateConf Conf = new(s => Parser.Parse(s));

    // TestTreeTemplatesConf

    [Fact]
    public void test_conf_test_var__not_var()
    {
        var nonTemplates = new object?[]
        {
            SomeNonTemplatedString,
            new Tree("stmt", new List<object?>()),
            T("var", SomeNonTemplatedString),
            T("var", SomeNonTemplatedString, SomeTemplateString),
            T("var", SomeNonString),
            new Tree("var", new List<object?>()),
        };
        foreach (var testCase in nonTemplates)
        {
            Assert.Null(Conf.TestVar(testCase));
        }
    }

    [Fact]
    public void test_conf_test_var__is_var()
    {
        foreach (var testCase in new object?[] { SomeTemplateString, T("var", SomeTemplateString) })
        {
            Assert.Equal(SomeTemplateName, Conf.TestVar(testCase));
        }
    }

    [Fact]
    public void test_conf_call__same_tree()
    {
        var explicitlyParsed = Parser.Parse(SomeFooTemplate);

        Assert.Equal(explicitlyParsed, Conf.Create(SomeFooTemplate).Tree);
        Assert.Equal(explicitlyParsed, Conf.Create(explicitlyParsed).Tree);
    }

    [Fact]
    public void test_template_match__default_conf_match_same_tree__empty_dictionary()
    {
        var template = new Template(SomeNonTemplateTree);
        Assert.Empty(template.Match(SomeNonTemplateTree)!);
    }

    [Fact]
    public void test_template_match__only_tree()
    {
        // This test might become irrelevant in the future
        var templateTree = T("bar", T("var", "$foo"));
        var template = new Template(templateTree);
        Assert.Throws<ArgumentException>(() => template.Match(T("bar", "BAD")));
    }

    // TestTreeTemplatesTemplate

    [Fact]
    public void test_template_match__same_tree_no_template__empty_dictionary()
    {
        var template = new Template(SomeNonTemplateTree, Conf);
        Assert.Empty(template.Match(SomeNonTemplateTree)!);
    }

    [Fact]
    public void test_template_match__different_tree_no_template__none()
    {
        var template = new Template(SomeNonTemplateTree, Conf);
        Assert.Null(template.Match(T("foo", "bye")));
    }

    [Fact]
    public void test_template_match__no_template__empty_dictionary()
    {
        var tree = Parser.Parse(SomeNonTemplatedString);
        var template = new Template(tree, Conf);

        Assert.Empty(template.Match(SomeNonTemplatedString)!);
        Assert.Empty(template.Match(tree)!);
    }

    [Fact]
    public void test_template_match__with_template__empty_dictionary()
    {
        var tree = Parser.Parse(SomeFooTemplate);
        var template = new Template(tree, Conf);

        var expected = new Dictionary<string, Tree> { [SomeTemplateName] = (Tree)tree.Children[0]! };
        Assert.Equal(expected, template.Match(SomeFooTemplate));
        Assert.Equal(expected, template.Match(tree));
    }

    [Fact]
    public void test_template_match__different_tree__none()
    {
        var tree = Parser.Parse(SomeFooTemplate);
        var template = new Template(tree, Conf);

        Assert.Null(template.Match(SomeBarTemplate));
        Assert.Null(template.Match(Parser.Parse(SomeBarTemplate)));
    }

    [Fact]
    public void test_template_search__same_tree_no_template__empty_generator()
    {
        var template = new Template(SomeNonTemplateTree, Conf);
        Assert.Empty(template.Search(SomeNonTemplateTree));
    }

    [Fact]
    public void test_template_search__same_tree_as_child__empty_generator()
    {
        var template = new Template(SomeNonTemplateTree, Conf);
        Assert.Empty(template.Search(T("root", SomeNonTemplateTree)));
    }

    [Fact]
    public void test_template_search__with_template__matched_result_with_parent_tree()
    {
        var tree = Parser.Parse(SomeFooTemplate);
        var template = new Template(tree, Conf);

        var expectedVars = new Dictionary<string, Tree> { [SomeTemplateName] = (Tree)tree.Children[0]! };

        foreach (var result in new[] { template.Search(SomeFooTemplate).ToList(), template.Search(tree).ToList() })
        {
            var (subtree, vars) = Assert.Single(result);
            Assert.Equal(tree, subtree);
            Assert.Equal(expectedVars, vars);
        }
    }

    [Fact]
    public void test_template_apply_vars__empty__exception()
    {
        var template = new Template(Parser.Parse(SomeFooTemplate), Conf);
        Assert.Throws<MissingVariableException>(() => template.ApplyVars(new Dictionary<string, Tree>()));
    }

    [Fact]
    public void test_template_apply_vars__no_matching_vars__exception()
    {
        var template = new Template(Parser.Parse(SomeFooTemplate), Conf);
        Assert.Throws<MissingVariableException>(() => template.ApplyVars(new Dictionary<string, Tree> { ["not used"] = SomeNonTemplateTree }));
    }

    [Fact]
    public void test_template_apply_vars__matching_vars__template_replaced()
    {
        var tree = Parser.Parse(SomeFooTemplate);
        var template = new Template(tree, Conf);

        var expected = tree.DeepCopy();
        expected.Children[0] = SomeNonTemplateTree;
        Assert.Equal(expected, template.ApplyVars(new Dictionary<string, Tree> { [SomeTemplateName] = SomeNonTemplateTree }));
    }

    // TestTreeTemplatesTemplateTranslator

    [Fact]
    public void test_translate__empty_translations__same_tree()
    {
        // no translations to match, so doesn't replace anything & can't error
        var translator = new TemplateTranslator();
        var tree = Parser.Parse(SomeFooTemplate);

        var expected = tree.DeepCopy();
        Assert.Equal(expected, translator.Translate(tree));
    }

    [Fact]
    public void test_translate__one_translations__same_tree()
    {
        var translator = new TemplateTranslator(
            (Conf.Create($"${SomeTemplateName} bar"), Conf.Create($"--${SomeTemplateName}-- bar")));
        var tree = Parser.Parse(SomeNonTemplatedString);

        var expected = tree.DeepCopy();
        expected.Children.Insert(0, Tok("DASHES", "--"));
        expected.Children.Insert(2, Tok("DASHES", "--"));
        Assert.Equal(expected, translator.Translate(tree));
    }

    // TestTreeTemplatesTemplateDefaultConf

    [Fact]
    public void test_template_match__match_same_tree__empty_dictionary()
    {
        var tree = T("foo", "hi");
        var template = new Template(tree);
        Assert.Empty(template.Match(tree)!);
    }
}
