namespace EtAlii.Lark;

/// <summary>
/// Template configuration: decides which tree nodes are template variables, and how template code is parsed into a tree.
/// </summary>
/// <remarks>A tree template is a tree that contains nodes that are template variables.</remarks>
public class TemplateConf
{
    private const char TemplateMarker = '$';

    private readonly Func<string, Tree>? _parse;

    /// <param name="parse">Parses template code into a tree; required when templates or trees are given as strings.</param>
    public TemplateConf(Func<string, Tree>? parse = null)
    {
        _parse = parse;
    }

    /// <summary>The default configuration, which can't parse code.</summary>
    public static TemplateConf Default { get; } = new();

    /// <summary>Given a tree node, returns its name when it is a template variable; otherwise null.</summary>
    /// <remarks>Strings (and tokens) starting with '$' are variables, as are <c>var</c> trees whose first child is one.</remarks>
    public virtual string? TestVar(object? var)
    {
        if (AsString(var) is { } s) return GetTemplateName(s);

        if (var is Tree { Data: "var", Children.Count: > 0 } tree && AsString(tree.Children[0]) is { } first)
        {
            return GetTemplateName(first);
        }

        return null;
    }

    /// <summary>Creates a template bound to this configuration.</summary>
    public Template Create(Tree template) => new(template, this);

    /// <summary>Parses the code and creates a template bound to this configuration.</summary>
    public Template Create(string template) => new(template, this);

    internal Tree GetTree(Tree template) => template;

    internal Tree GetTree(string template)
    {
        if (_parse is null) throw new InvalidOperationException("This template configuration has no parser, so templates must be given as trees.");
        return _parse(template) ?? throw new InvalidOperationException("The template parser must return a Tree instance.");
    }

    /// <summary>Returns a mapping of variable names to matched trees when the template matches; otherwise null.</summary>
    internal Dictionary<string, Tree>? MatchTreeTemplate(object? template, object? tree)
    {
        var templateVar = TestVar(template);
        if (!string.IsNullOrEmpty(templateVar))
        {
            if (tree is not Tree t) throw new ArgumentException($"Template variables can only match Tree instances. Not {Utilities.Helpers.Repr(tree)}");
            return new Dictionary<string, Tree> { [templateVar] = t };
        }

        if (AsString(template) is not null)
        {
            return Tree.ValueEquals(template, tree) || Tree.ValueEquals(tree, template) ? new Dictionary<string, Tree>() : null;
        }

        if (template is not Tree templateTree || tree is not Tree other)
        {
            throw new InvalidOperationException($"template={template} tree={tree}");
        }

        if (templateTree.Data != other.Data || templateTree.Children.Count != other.Children.Count) return null;

        var res = new Dictionary<string, Tree>();
        for (var i = 0; i < templateTree.Children.Count; i++)
        {
            var matches = MatchTreeTemplate(templateTree.Children[i], other.Children[i]);
            if (matches is null) return null;
            foreach (var (k, v) in matches) res[k] = v;
        }
        return res;
    }

    private static string? AsString(object? value) => value switch
    {
        string s => s,
        Token t => t.Value,
        _ => null,
    };

    private static string? GetTemplateName(string value) =>
        value.StartsWith(TemplateMarker) ? value.TrimStart(TemplateMarker) : null;
}

/// <summary>
/// A tree template, tied to a specific configuration. Template variables match any tree.
/// </summary>
public class Template
{
    public Template(Tree tree, TemplateConf? conf = null)
    {
        Conf = conf ?? TemplateConf.Default;
        Tree = Conf.GetTree(tree);
    }

    public Template(string code, TemplateConf? conf = null)
    {
        Conf = conf ?? TemplateConf.Default;
        Tree = Conf.GetTree(code);
    }

    public TemplateConf Conf { get; }

    public Tree Tree { get; }

    /// <summary>
    /// Matches the template against a tree. A template without variables only matches a tree equal to it.
    /// </summary>
    /// <returns>A mapping of template variable names to their matching tree nodes, or null when there is no match.</returns>
    public Dictionary<string, Tree>? Match(Tree tree) => Conf.MatchTreeTemplate(Tree, tree);

    /// <inheritdoc cref="Match(Tree)"/>
    public Dictionary<string, Tree>? Match(string code) => Match(Conf.GetTree(code));

    /// <summary>Searches for all occurrences of the template inside the tree.</summary>
    public IEnumerable<(Tree Subtree, Dictionary<string, Tree> Vars)> Search(Tree tree)
    {
        foreach (var subtree in tree.IterSubtrees())
        {
            var res = Match(subtree);
            if (res is { Count: > 0 }) yield return (subtree, res);
        }
    }

    /// <inheritdoc cref="Search(Tree)"/>
    public IEnumerable<(Tree Subtree, Dictionary<string, Tree> Vars)> Search(string code) => Search(Conf.GetTree(code));

    /// <summary>Returns a copy of the template tree with its variables replaced.</summary>
    /// <exception cref="MissingVariableException">A template variable has no mapping.</exception>
    public Tree ApplyVars(IReadOnlyDictionary<string, Tree> vars) => (Tree)new ReplaceVars(Conf, vars).Transform(Tree)!;

    /// <summary>Searches the tree and translates each occurrence of <paramref name="from"/> into <paramref name="to"/>, in place.</summary>
    public static Tree Translate(Template from, Template to, Tree tree)
    {
        foreach (var (subtree, vars) in from.Search(tree).ToList())
        {
            var res = to.ApplyVars(vars);
            subtree.Set(res.Data, res.Children);
        }
        return tree;
    }

    /// <inheritdoc cref="Translate(Template, Template, Tree)"/>
    public static Tree Translate(Template from, Template to, string code) => Translate(from, to, from.Conf.GetTree(code));

    private sealed class ReplaceVars : Transformer
    {
        private readonly TemplateConf _conf;
        private readonly IReadOnlyDictionary<string, Tree> _vars;

        public ReplaceVars(TemplateConf conf, IReadOnlyDictionary<string, Tree> vars)
        {
            _conf = conf;
            _vars = vars;
        }

        protected internal override object? Default(string data, List<object?> children, Meta? meta)
        {
            var tree = base.Default(data, children, meta);
            var var = _conf.TestVar(tree);
            if (!string.IsNullOrEmpty(var))
            {
                return _vars.TryGetValue(var, out var replacement)
                    ? replacement
                    : throw new MissingVariableException($"No mapping for template variable ({var})");
            }
            return tree;
        }
    }
}

/// <summary>Translates a collection of template patterns, in order.</summary>
public class TemplateTranslator
{
    private readonly IReadOnlyList<KeyValuePair<Template, Template>> _translations;

    public TemplateTranslator(IEnumerable<KeyValuePair<Template, Template>> translations)
    {
        _translations = translations.ToList();
    }

    public TemplateTranslator(params (Template From, Template To)[] translations)
        : this(translations.Select(t => KeyValuePair.Create(t.From, t.To))) { }

    public Tree Translate(Tree tree)
    {
        foreach (var (from, to) in _translations)
        {
            tree = Template.Translate(from, to, tree);
        }
        return tree;
    }
}
