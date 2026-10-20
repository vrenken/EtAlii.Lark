namespace EtAlii.Lark;

using System.Reflection;
using System.Text.RegularExpressions;

/// <summary>
/// Fluent configuration of a <see cref="Lark"/> parser. Start with <see cref="Lark.FromGrammar(string)"/>.
/// </summary>
public sealed class LarkBuilder
{
    private readonly string _grammar;

    internal LarkBuilder(string grammar)
    {
        _grammar = grammar;
    }

    /// <summary>The options that are being configured.</summary>
    public LarkOptions Options { get; } = new();

    /// <summary>Uses the LALR(1) parser (with the contextual lexer, unless configured otherwise).</summary>
    public LarkBuilder UseLalr(LexerKind lexer = LexerKind.Auto)
    {
        Options.Parser = ParserKind.Lalr;
        Options.Lexer = lexer;
        return this;
    }

    /// <summary>Uses the Earley parser (with the dynamic lexer, unless configured otherwise).</summary>
    public LarkBuilder UseEarley(LexerKind lexer = LexerKind.Auto, Ambiguity ambiguity = Ambiguity.Auto)
    {
        Options.Parser = ParserKind.Earley;
        Options.Lexer = lexer;
        Options.Ambiguity = ambiguity;
        return this;
    }

    /// <summary>Uses the CYK parser.</summary>
    public LarkBuilder UseCyk()
    {
        Options.Parser = ParserKind.Cyk;
        Options.Lexer = LexerKind.Basic;
        return this;
    }

    /// <summary>Only builds a lexer (see <see cref="Lark.Lex"/>).</summary>
    public LarkBuilder LexerOnly()
    {
        Options.Parser = ParserKind.None;
        Options.Lexer = LexerKind.Basic;
        return this;
    }

    /// <summary>Selects the lexer.</summary>
    public LarkBuilder WithLexer(LexerKind lexer)
    {
        Options.Lexer = lexer;
        return this;
    }

    /// <summary>Uses a custom lexer.</summary>
    public LarkBuilder WithLexer(Func<LexerConf, Lexer> lexerFactory)
    {
        Options.Lexer = LexerKind.Custom;
        Options.CustomLexer = lexerFactory;
        return this;
    }

    /// <summary>Sets the start rule(s).</summary>
    public LarkBuilder WithStart(params string[] start)
    {
        Options.Start = start;
        return this;
    }

    /// <summary>Sets how ambiguity is handled (Earley only).</summary>
    public LarkBuilder WithAmbiguity(Ambiguity ambiguity)
    {
        Options.Ambiguity = ambiguity;
        return this;
    }

    /// <summary>Sets how priorities are evaluated.</summary>
    public LarkBuilder WithPriority(PriorityMode priority)
    {
        Options.Priority = priority;
        return this;
    }

    /// <summary>Keeps all tokens in the tree, including "punctuation".</summary>
    public LarkBuilder KeepAllTokens(bool keep = true)
    {
        Options.KeepAllTokens = keep;
        return this;
    }

    /// <summary>Propagates the positions of the tokens to the <see cref="Tree.Meta"/> of the trees.</summary>
    public LarkBuilder PropagatePositions(Func<object?, bool>? filter = null)
    {
        Options.PropagatePositions = true;
        Options.PropagatePositionsFilter = filter;
        return this;
    }

    /// <summary>Sets whether <c>[]</c> produces a null placeholder when not matched (default true).</summary>
    public LarkBuilder MaybePlaceholders(bool enabled = true)
    {
        Options.MaybePlaceholders = enabled;
        return this;
    }

    /// <summary>Applies the transformer while parsing (LALR and CYK only).</summary>
    public LarkBuilder WithTransformer(Transformer transformer)
    {
        Options.Transformer = transformer;
        return this;
    }

    /// <summary>Adds a lexer post-processing step, such as an <see cref="Indenter"/>.</summary>
    public LarkBuilder WithPostLex(PostLex postLex)
    {
        Options.PostLex = postLex;
        return this;
    }

    /// <summary>Creates the trees with the given factory.</summary>
    public LarkBuilder WithTreeFactory(Func<string, List<object?>, Tree> treeFactory)
    {
        Options.TreeFactory = treeFactory;
        return this;
    }

    /// <summary>Adds a callback that is called for every token of the given terminal type.</summary>
    public LarkBuilder WithLexerCallback(string terminal, Func<Token, Token> callback)
    {
        Options.LexerCallbacks[terminal] = callback;
        return this;
    }

    /// <summary>Edits the terminals before the parser is built.</summary>
    public LarkBuilder EditTerminals(Action<TerminalDef> edit)
    {
        Options.EditTerminals = edit;
        return this;
    }

    /// <summary>Applies regular expression options to all terminals.</summary>
    public LarkBuilder WithRegexOptions(RegexOptions options)
    {
        Options.RegexOptions = options;
        return this;
    }

    /// <summary>Adds a directory that is searched for <c>%import</c> statements.</summary>
    public LarkBuilder WithImportPath(string directory)
    {
        Options.ImportPaths.Add(new FileSystemGrammarLoader(directory));
        return this;
    }

    /// <summary>Adds a loader that is used for <c>%import</c> statements.</summary>
    public LarkBuilder WithImportLoader(IGrammarLoader loader)
    {
        Options.ImportPaths.Add(loader);
        return this;
    }

    /// <summary>Adds the resources of an assembly as a location for <c>%import</c> statements.</summary>
    public LarkBuilder WithImportResources(Assembly assembly, params string[] searchPaths)
    {
        Options.ImportPaths.Add(new EmbeddedResourceGrammarLoader(assembly, searchPaths.Length == 0 ? new[] { "" } : searchPaths));
        return this;
    }

    /// <summary>Sets the source path of the grammar (used for relative imports and error messages).</summary>
    public LarkBuilder WithSourcePath(string sourcePath)
    {
        Options.SourcePath = sourcePath;
        return this;
    }

    /// <summary>Sets how many characters are read at a time, and looked ahead, when parsing a reader or stream.</summary>
    public LarkBuilder WithStreaming(int chunkSize, int lookahead)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (lookahead <= 0) throw new ArgumentOutOfRangeException(nameof(lookahead));
        Options.StreamChunkSize = chunkSize;
        Options.StreamLookahead = lookahead;
        return this;
    }

    /// <summary>Caches the grammar analysis (LALR only), optionally in the given file.</summary>
    public LarkBuilder WithCache(string? cachePath = null)
    {
        Options.Cache = true;
        Options.CachePath = cachePath;
        return this;
    }

    /// <summary>Throws on any potential ambiguity: shift/reduce conflicts and regex collisions.</summary>
    public LarkBuilder Strict(bool strict = true)
    {
        Options.Strict = strict;
        return this;
    }

    /// <summary>Enables debug information and extra warnings.</summary>
    public LarkBuilder Debug(bool debug = true)
    {
        Options.Debug = debug;
        return this;
    }

    /// <summary>Configures any of the options directly.</summary>
    public LarkBuilder Configure(Action<LarkOptions> configure)
    {
        configure(Options);
        return this;
    }

    /// <summary>Compiles the grammar and builds the parser.</summary>
    public Lark Build() => new(_grammar, Options);
}
