namespace EtAlii.Lark;

using System.Text.RegularExpressions;

/// <summary>The options for <see cref="Lark"/>. Use <see cref="Lark.FromGrammar(string)"/> for a fluent way to configure them.</summary>
public sealed class LarkOptions
{
    /// <summary>The start symbol(s). Defaults to <c>start</c>.</summary>
    public IReadOnlyList<string> Start { get; set; } = new[] { "start" };

    /// <summary>Display debug information and extra warnings. Use only when debugging.</summary>
    public bool Debug { get; set; }

    /// <summary>Throws an exception on any potential ambiguity, including shift/reduce conflicts, and regex collisions.</summary>
    public bool Strict { get; set; }

    /// <summary>Prevent the tree builder from automagically removing "punctuation" tokens.</summary>
    public bool KeepAllTokens { get; set; }

    /// <summary>Creates the trees. Lark will produce trees created by this factory instead of the default <see cref="Tree"/>.</summary>
    public Func<string, List<object?>, Tree>? TreeFactory { get; set; }

    /// <summary>
    /// Cache the result of the grammar analysis (LALR only). When <see cref="CachePath"/> is null, a file in the temp
    /// directory is used.
    /// </summary>
    public bool Cache { get; set; }

    /// <summary>The file to cache the grammar analysis in (implies <see cref="Cache"/>).</summary>
    public string? CachePath { get; set; }

    /// <summary>A lexer post-processing step (e.g. an <see cref="Indenter"/>). Requires the basic or contextual lexer.</summary>
    public PostLex? PostLex { get; set; }

    /// <summary>The parser algorithm. Defaults to <see cref="ParserKind.Earley"/>, like upstream Lark.</summary>
    public ParserKind Parser { get; set; } = ParserKind.Earley;

    /// <summary>The lexer. <see cref="LexerKind.Auto"/> chooses based on the parser.</summary>
    public LexerKind Lexer { get; set; } = LexerKind.Auto;

    /// <summary>Creates a custom lexer; used when <see cref="Lexer"/> is <see cref="LexerKind.Custom"/>.</summary>
    public Func<LexerConf, Lexer>? CustomLexer { get; set; }

    /// <summary>Applies the transformer to every parse tree, while parsing (equivalent to applying it after the parse, but faster). LALR and CYK only.</summary>
    public Transformer? Transformer { get; set; }

    /// <summary>How priorities should be evaluated.</summary>
    public PriorityMode Priority { get; set; } = PriorityMode.Auto;

    /// <summary>Decides how to handle ambiguity in the parse (Earley only).</summary>
    public Ambiguity Ambiguity { get; set; } = Ambiguity.Auto;

    /// <summary>Propagates the positions (line, column, start_pos, ...) to the <see cref="Tree.Meta"/> of each tree.</summary>
    public bool PropagatePositions { get; set; }

    /// <summary>When set, positions are only propagated from the nodes for which this filter returns true.</summary>
    public Func<object?, bool>? PropagatePositionsFilter { get; set; }

    /// <summary>Callbacks for the lexer, called for every token of the given type, after it's matched. Their result replaces the token.</summary>
    public Dictionary<string, Func<Token, Token>> LexerCallbacks { get; set; } = new();

    /// <summary>When true, the <c>[]</c> operator returns null when not matched. When false, <c>[]</c> behaves like <c>?</c>. Defaults to true.</summary>
    public bool MaybePlaceholders { get; set; } = true;

    /// <summary>A callback for editing the terminals before the parser is created.</summary>
    public Action<TerminalDef>? EditTerminals { get; set; }

    /// <summary>Regular expression options applied to all terminals (Lark's <c>g_regex_flags</c>).</summary>
    public RegexOptions RegexOptions { get; set; }

    /// <summary>Loaders that are tried, in order, when resolving <c>%import</c> statements (before the standard library).</summary>
    public List<IGrammarLoader> ImportPaths { get; set; } = new();

    /// <summary>The path of the grammar; used for relative imports and error messages.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Returns a shallow copy of these options.</summary>
    public LarkOptions Clone()
    {
        var copy = (LarkOptions)MemberwiseClone();
        copy.LexerCallbacks = new Dictionary<string, Func<Token, Token>>(LexerCallbacks);
        copy.ImportPaths = new List<IGrammarLoader>(ImportPaths);
        return copy;
    }

    internal void Validate()
    {
        if (Parser == ParserKind.Earley && Transformer is not null)
        {
            throw new ConfigurationException("Cannot specify an embedded transformer when using the Earley algorithm. " +
                                             "Please use your transformer on the resulting parse tree, or use a different algorithm (i.e. LALR)");
        }
        if (Start.Count == 0)
        {
            throw new ConfigurationException("At least one start rule is required");
        }
    }
}
