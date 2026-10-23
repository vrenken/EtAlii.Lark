namespace EtAlii.Lark;

/// <summary>Options that influence how a rule is built into the tree.</summary>
public sealed class RuleOptions
{
    public RuleOptions(bool keepAllTokens = false, bool expand1 = false, int? priority = null, string? templateSource = null, IReadOnlyList<bool>? emptyIndices = null)
    {
        KeepAllTokens = keepAllTokens;
        Expand1 = expand1;
        Priority = priority;
        TemplateSource = templateSource;
        EmptyIndices = emptyIndices ?? Array.Empty<bool>();
    }

    /// <summary>The <c>!</c> modifier: keep all tokens of this rule in the tree.</summary>
    public bool KeepAllTokens { get; set; }

    /// <summary>The <c>?</c> modifier: inline this rule if it has a single child.</summary>
    public bool Expand1 { get; set; }

    /// <summary>The priority of the rule (<c>rule.N: ...</c>).</summary>
    public int? Priority { get; set; }

    /// <summary>When the rule was created from a template, the name of the template.</summary>
    public string? TemplateSource { get; set; }

    /// <summary>Positions that were matched as empty by <c>[optional]</c> expansions (for maybe_placeholders).</summary>
    public IReadOnlyList<bool> EmptyIndices { get; set; }

    public RuleOptions Clone() => new(KeepAllTokens, Expand1, Priority, TemplateSource, EmptyIndices);

    public override string ToString() =>
        $"RuleOptions({Py(KeepAllTokens)}, {Py(Expand1)}, {(Priority?.ToString() ?? "None")}, {(TemplateSource ?? "None")})";

    private static string Py(bool b) => b ? "True" : "False";
}

/// <summary>A single BNF production: <c>origin : expansion</c>.</summary>
public sealed class Rule : IEquatable<Rule>
{
    private readonly int _hash;

    public Rule(NonTerminal origin, IReadOnlyList<Symbol> expansion, int order = 0, string? alias = null, RuleOptions? options = null)
    {
        Origin = origin;
        Expansion = expansion;
        Order = order;
        Alias = alias;
        Options = options ?? new RuleOptions();
        var hash = new HashCode();
        hash.Add(origin);
        foreach (var s in expansion) hash.Add(s);
        _hash = hash.ToHashCode();
    }

    /// <summary>The rule symbol on the left hand side.</summary>
    public NonTerminal Origin { get; }

    /// <summary>The symbols on the right hand side.</summary>
    public IReadOnlyList<Symbol> Expansion { get; }

    /// <summary>Index of this expansion amongst all rules of the same name.</summary>
    public int Order { get; }

    /// <summary>The alias of this expansion (<c>-&gt; alias</c>), if any.</summary>
    public string? Alias { get; }

    public RuleOptions Options { get; }

    public bool Equals(Rule? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || _hash != other._hash) return false;
        return Origin.Equals(other.Origin) && Expansion.SequenceEqual(other.Expansion);
    }

    public override bool Equals(object? obj) => obj is Rule r && Equals(r);

    public override int GetHashCode() => _hash;

    public override string ToString() => $"<{Origin.Name} : {string.Join(" ", Expansion.Select(x => x.Name))}>";
}
