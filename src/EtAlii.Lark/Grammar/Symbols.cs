namespace EtAlii.Lark;

using EtAlii.Lark.Utilities;

/// <summary>Base class of grammar symbols: <see cref="Terminal"/> and <see cref="NonTerminal"/>.</summary>
public abstract class Symbol : IEquatable<Symbol>
{
    protected Symbol(string name)
    {
        Name = name;
    }

    /// <summary>The name of the symbol, as written in the grammar.</summary>
    public string Name { get; }

    /// <summary>True for terminals (tokens), false for rules.</summary>
    public abstract bool IsTerm { get; }

    public bool Equals(Symbol? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return IsTerm == other.IsTerm && Name == other.Name && GetType() == other.GetType();
    }

    public override bool Equals(object? obj) => obj is Symbol s && Equals(s);

    public override int GetHashCode() => Name.GetHashCode();

    public static bool operator ==(Symbol? a, Symbol? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(Symbol? a, Symbol? b) => !(a == b);

    public override string ToString() => $"{GetType().Name}({Helpers.Repr(Name)})";

    /// <summary>Returns a copy of this symbol with its name passed through <paramref name="f"/>.</summary>
    public abstract Symbol Renamed(Func<string, string> f);
}

/// <summary>A terminal (token) symbol.</summary>
public sealed class Terminal : Symbol
{
    public Terminal(string name, bool filterOut = false) : base(name)
    {
        FilterOut = filterOut;
    }

    public override bool IsTerm => true;

    /// <summary>Whether this terminal is filtered out of the parse tree (anonymous string literals and _TERMINALS).</summary>
    public bool FilterOut { get; set; }

    public override Symbol Renamed(Func<string, string> f) => new Terminal(f(Name), FilterOut);

    public string FullRepr => $"Terminal({Helpers.Repr(Name)}, {(FilterOut ? "True" : "False")})";
}

/// <summary>A rule (non-terminal) symbol.</summary>
public sealed class NonTerminal : Symbol
{
    public NonTerminal(string name) : base(name) { }

    public override bool IsTerm => false;

    public override Symbol Renamed(Func<string, string> f) => new NonTerminal(f(Name));
}

/// <summary>Used internally to mark the empty positions of <c>[optional]</c> expansions.</summary>
internal sealed class EmptySymbol : Symbol
{
    public static readonly EmptySymbol Instance = new();

    private EmptySymbol() : base("__empty__") { }

    public override bool IsTerm => false;

    public override Symbol Renamed(Func<string, string> f) => this;
}
