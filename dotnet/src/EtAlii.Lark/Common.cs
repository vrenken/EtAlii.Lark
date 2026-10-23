namespace EtAlii.Lark;

using System.Text.RegularExpressions;

/// <summary>The parsing algorithm.</summary>
public enum ParserKind
{
    /// <summary>Earley: can parse any context-free grammar, handles ambiguity.</summary>
    Earley,

    /// <summary>LALR(1): fast, deterministic, the most efficient choice when the grammar allows it.</summary>
    Lalr,

    /// <summary>CYK: a legacy algorithm, kept for completeness.</summary>
    Cyk,

    /// <summary>No parser: lexer-only mode.</summary>
    None,
}

/// <summary>Which lexer to use.</summary>
public enum LexerKind
{
    /// <summary>Choose automatically based on the parser.</summary>
    Auto,

    /// <summary>A basic lexer that tokenizes independently of the parser.</summary>
    Basic,

    /// <summary>A stronger lexer that only considers the terminals the parser accepts at each point (LALR only).</summary>
    Contextual,

    /// <summary>Flexible and powerful scanner-less lexing (Earley only).</summary>
    Dynamic,

    /// <summary>Like <see cref="Dynamic"/>, but tries every variation of tokenizing possible (Earley only).</summary>
    DynamicComplete,

    /// <summary>A user supplied lexer (see <see cref="LarkOptions.CustomLexer"/>).</summary>
    Custom,
}

/// <summary>How ambiguity is handled by the Earley parser.</summary>
public enum Ambiguity
{
    Auto,

    /// <summary>The parser automatically chooses the simplest derivation.</summary>
    Resolve,

    /// <summary>The parser returns all derivations wrapped in <c>_ambig</c> tree nodes.</summary>
    Explicit,

    /// <summary>The parser returns the root of the shared packed parse forest.</summary>
    Forest,
}

/// <summary>How priorities are evaluated.</summary>
public enum PriorityMode
{
    Auto,
    Normal,
    Invert,

    /// <summary>Strip all priorities.</summary>
    None,
}

/// <summary>Configuration of the lexer.</summary>
public sealed class LexerConf
{
    public LexerConf(IReadOnlyList<TerminalDef> terminals, IReadOnlyCollection<string>? ignore = null, PostLex? postLex = null,
        IReadOnlyDictionary<string, Func<Token, Token>>? callbacks = null, RegexOptions regexOptions = RegexOptions.None,
        bool skipValidation = false, bool strict = false)
    {
        Terminals = terminals;
        TerminalsByName = terminals.ToDictionary(t => t.Name);
        if (TerminalsByName.Count != terminals.Count) throw new ArgumentException("Duplicate terminal names");
        Ignore = ignore ?? Array.Empty<string>();
        PostLex = postLex;
        Callbacks = callbacks ?? new Dictionary<string, Func<Token, Token>>();
        RegexOptions = regexOptions;
        SkipValidation = skipValidation;
        Strict = strict;
    }

    public IReadOnlyList<TerminalDef> Terminals { get; private set; }

    public IReadOnlyDictionary<string, TerminalDef> TerminalsByName { get; private set; }

    public IReadOnlyCollection<string> Ignore { get; set; }

    public PostLex? PostLex { get; set; }

    public IReadOnlyDictionary<string, Func<Token, Token>> Callbacks { get; set; }

    /// <summary>Global regular expression options applied to all terminals (Lark's <c>g_regex_flags</c>).</summary>
    public RegexOptions RegexOptions { get; set; }

    public bool SkipValidation { get; set; }

    public bool Strict { get; set; }

    public LexerKind LexerKind { get; set; }

    public Func<LexerConf, Lexer>? CustomLexerFactory { get; set; }

    internal LexerConf WithTerminals(IReadOnlyList<TerminalDef> terminals)
    {
        var copy = (LexerConf)MemberwiseClone();
        copy.Terminals = terminals;
        copy.TerminalsByName = terminals.ToDictionary(t => t.Name);
        return copy;
    }

    internal LexerConf Copy() => (LexerConf)MemberwiseClone();
}

/// <summary>Callbacks used by the parsers to build the tree.</summary>
public sealed class ParserCallbacks
{
    public Dictionary<Rule, Func<List<object?>, object?>> Rules { get; } = new();

    public Dictionary<string, Func<Token, object?>> Tokens { get; } = new();
}

/// <summary>Configuration of the parser.</summary>
public sealed class ParserConf
{
    public ParserConf(IReadOnlyList<Rule> rules, ParserCallbacks callbacks, IReadOnlyList<string> start)
    {
        Rules = rules;
        Callbacks = callbacks;
        Start = start;
    }

    public IReadOnlyList<Rule> Rules { get; }

    public ParserCallbacks Callbacks { get; set; }

    public IReadOnlyList<string> Start { get; }

    public ParserKind ParserKind { get; set; }
}
