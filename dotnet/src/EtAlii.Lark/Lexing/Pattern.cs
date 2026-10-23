namespace EtAlii.Lark;

using EtAlii.Lark.Regexes;
using EtAlii.Lark.Utilities;

/// <summary>
/// An abstraction over regular expressions. Patterns are kept in Lark's (Python) regular expression dialect and
/// translated to .NET regular expressions when compiled.
/// </summary>
public abstract class Pattern : IEquatable<Pattern>
{
    protected Pattern(string value, IEnumerable<char>? flags = null, string? raw = null)
    {
        Value = value;
        Flags = new string((flags ?? Array.Empty<char>()).Distinct().OrderBy(c => c).ToArray());
        Raw = raw;
    }

    /// <summary>The literal string, or the regular expression (in Lark/Python syntax).</summary>
    public string Value { get; }

    /// <summary>The regular expression flags of this pattern (any of <c>imslux</c>), sorted.</summary>
    public string Flags { get; }

    /// <summary>The pattern as written in the grammar, if known.</summary>
    public string? Raw { get; }

    /// <summary>"str" for literal strings, "re" for regular expressions.</summary>
    public abstract string Type { get; }

    /// <summary>Returns the pattern as a regular expression in Lark/Python syntax.</summary>
    public abstract string ToRegexp();

    public abstract int MinWidth { get; }

    public abstract int MaxWidth { get; }

    protected string GetFlags(string value)
    {
        foreach (var f in Flags)
        {
            value = $"(?{f}:{value})";
        }
        return value;
    }

    public bool Equals(Pattern? other)
    {
        if (ReferenceEquals(this, other)) return true;
        return other is not null && GetType() == other.GetType() && Value == other.Value && Flags == other.Flags;
    }

    public override bool Equals(object? obj) => obj is Pattern p && Equals(p);

    public override int GetHashCode() => HashCode.Combine(GetType(), Value, Flags);

    public override string ToString() => Helpers.Repr(ToRegexp());
}

/// <summary>A pattern that matches a literal string.</summary>
public sealed class PatternStr : Pattern
{
    public PatternStr(string value, IEnumerable<char>? flags = null, string? raw = null) : base(value, flags, raw) { }

    public override string Type => "str";

    public override string ToRegexp() => GetFlags(PyRegex.Escape(Value));

    public override int MinWidth => Value.Length;

    public override int MaxWidth => Value.Length;
}

/// <summary>A pattern that matches a regular expression.</summary>
public sealed class PatternRE : Pattern
{
    private (int Min, int Max)? _width;

    public PatternRE(string value, IEnumerable<char>? flags = null, string? raw = null) : base(value, flags, raw) { }

    internal PatternRE(string value, IEnumerable<char>? flags, string? raw, (int, int)? width) : base(value, flags, raw)
    {
        _width = width;
    }

    public override string Type => "re";

    public override string ToRegexp() => GetFlags(Value);

    internal (int Min, int Max) Width => _width ??= PyRegex.GetWidth(ToRegexp());

    internal bool HasWidth => _width.HasValue;

    public override int MinWidth => Width.Min;

    public override int MaxWidth => Width.Max;
}

/// <summary>A definition of a terminal.</summary>
public sealed class TerminalDef
{
    public const int DefaultPriority = 0;

    public TerminalDef(string name, Pattern pattern, int priority = DefaultPriority)
    {
        Name = name;
        Pattern = pattern;
        Priority = priority;
    }

    public string Name { get; set; }

    public Pattern Pattern { get; set; }

    public int Priority { get; set; }

    public override string ToString() => $"TerminalDef({Helpers.Repr(Name)}, {Pattern})";

    /// <summary>A user-friendly representation: the pattern for anonymous terminals, otherwise the name.</summary>
    public string UserRepr()
    {
        if (Name.StartsWith("__", StringComparison.Ordinal))
        {
            return Pattern.Raw ?? Name;
        }
        return Name;
    }
}
