namespace EtAlii.Lark;

using System.Reflection;
using EtAlii.Lark.Parsers;

/// <summary>
/// The main interface of the library: compiles a Lark grammar into a parser.
/// </summary>
/// <example>
/// <code>
/// var parser = Lark.FromGrammar("""
///     start: WORD "," WORD "!"
///     %import common.WORD
///     %ignore " "
///     """).UseLalr().Build();
/// var tree = parser.Parse("Hello, World!");
/// </code>
/// </example>
public sealed partial class Lark
{
    private ParsingFrontend? _parser;
    private Lexer? _lexer;
    private Dictionary<string, TerminalDef> _terminalsDict = new();
    private ParserCallbacks _callbacks = new();

    /// <summary>Creates a parser for the given grammar text.</summary>
    public Lark(string grammar, LarkOptions? options = null)
        : this(grammar, null, options)
    {
    }

    /// <summary>Creates a parser for an already loaded grammar.</summary>
    public Lark(Grammar grammar, LarkOptions? options = null)
        : this(null, grammar, options)
    {
    }

    private Lark(string? grammarText, Grammar? grammar, LarkOptions? options)
    {
        Options = options?.Clone() ?? new LarkOptions();
        Options.Validate();

        SourcePath = Options.SourcePath ?? "<string>";

        Dictionary<string, string>? usedFiles = null;
        string? cacheFile = null;
        string? cacheDigest = null;
        if (grammarText is not null)
        {
            SourceGrammar = grammarText;

            if (Options.Cache || Options.CachePath is not null)
            {
                if (Options.Parser != ParserKind.Lalr)
                {
                    throw new ConfigurationException("cache only works with parser='lalr' for now");
                }
                (cacheFile, cacheDigest) = GetCacheFile(grammarText);
                if (TryLoadFromCache(cacheFile, cacheDigest))
                {
                    return;
                }
            }

            // Parse the grammar file and compose the grammars
            (Grammar, usedFiles) = GrammarBuilder.Load(grammarText, SourcePath, Options.ImportPaths, Options.KeepAllTokens);
        }
        else
        {
            Grammar = grammar!;
        }

        ResolveAutoOptions();

        IReadOnlyCollection<string>? terminalsToKeep;
        if (Options.Parser == ParserKind.None)
        {
            terminalsToKeep = null; // For lexer-only mode, keep all terminals
        }
        else if (Options.PostLex is not null)
        {
            terminalsToKeep = Options.PostLex.AlwaysAccept.ToHashSet();
        }
        else
        {
            terminalsToKeep = Array.Empty<string>();
        }

        // Compile the EBNF grammar into BNF
        var (terminals, rules, ignoreTokens) = Grammar.Compile(Options.Start, terminalsToKeep);
        Terminals = terminals;
        Rules = rules;
        IgnoreTokens = ignoreTokens;

        if (Options.EditTerminals is not null)
        {
            foreach (var t in terminals) Options.EditTerminals(t);
        }

        _terminalsDict = terminals.ToDictionary(t => t.Name);

        if (Options.Priority == PriorityMode.Invert)
        {
            // If the user asked to invert the priorities, negate them all here.
            foreach (var rule in rules)
            {
                if (rule.Options.Priority is not null) rule.Options.Priority = -rule.Options.Priority;
            }
            foreach (var term in terminals) term.Priority = -term.Priority;
        }
        else if (Options.Priority == PriorityMode.None)
        {
            // Strip the priorities from the rules and terminals. This allows the Earley parsers to skip an extra
            // forest walk for improved performance, if you don't need them (or didn't specify any).
            foreach (var rule in rules) rule.Options.Priority = null;
            foreach (var term in terminals) term.Priority = 0;
        }

        LexerConf = CreateLexerConf(terminals, ignoreTokens);

        if (Options.Parser != ParserKind.None)
        {
            _parser = BuildParser();
        }
        else
        {
            _lexer = BuildLexer();
        }

        if (cacheFile is not null)
        {
            SaveToCache(cacheFile, cacheDigest!, usedFiles ?? new Dictionary<string, string>());
        }
    }

    /// <summary>The (resolved) options of this parser.</summary>
    public LarkOptions Options { get; private set; }

    /// <summary>The path of the grammar, or <c>&lt;string&gt;</c>.</summary>
    public string SourcePath { get; private set; }

    /// <summary>The grammar text, when the parser was created from text.</summary>
    public string? SourceGrammar { get; private set; }

    /// <summary>The loaded grammar (null when the parser was loaded from a cache).</summary>
    public Grammar? Grammar { get; private set; }

    /// <summary>The compiled terminals.</summary>
    public IReadOnlyList<TerminalDef> Terminals { get; private set; } = Array.Empty<TerminalDef>();

    /// <summary>The compiled (BNF) rules.</summary>
    public IReadOnlyList<Rule> Rules { get; private set; } = Array.Empty<Rule>();

    /// <summary>The names of the ignored terminals.</summary>
    public IReadOnlyList<string> IgnoreTokens { get; private set; } = Array.Empty<string>();

    /// <summary>The configuration of the lexer.</summary>
    public LexerConf LexerConf { get; private set; } = null!;

    internal ParsingFrontend? Frontend => _parser;

    /// <summary>The underlying parser: a <see cref="LalrParser"/>, <see cref="EarleyParser"/> or a CYK parser (null in lexer-only mode).</summary>
    public object? Parser => _parser?.Parser;

    private void ResolveAutoOptions()
    {
        if (Options.Lexer == LexerKind.Auto)
        {
            switch (Options.Parser)
            {
                case ParserKind.Lalr:
                    Options.Lexer = LexerKind.Contextual;
                    break;
                case ParserKind.Earley:
                    if (Options.PostLex is not null)
                    {
                        LarkLog.Info("postlex can't be used with the dynamic lexer, so we use 'basic' instead. " +
                                     "Consider using lalr with contextual instead of earley");
                        Options.Lexer = LexerKind.Basic;
                    }
                    else
                    {
                        Options.Lexer = LexerKind.Dynamic;
                    }
                    break;
                case ParserKind.Cyk:
                case ParserKind.None:
                    Options.Lexer = LexerKind.Basic;
                    break;
            }
        }

        if (Options.Lexer == LexerKind.Custom && Options.CustomLexer is null)
        {
            throw new ConfigurationException("LexerKind.Custom requires a CustomLexer factory");
        }
        if (Options.PostLex is not null && Options.Lexer is LexerKind.Dynamic or LexerKind.DynamicComplete)
        {
            throw new ConfigurationException("Can't use postlex with a dynamic lexer. Use basic or contextual instead");
        }

        if (Options.Ambiguity == Ambiguity.Auto)
        {
            if (Options.Parser == ParserKind.Earley) Options.Ambiguity = Ambiguity.Resolve;
        }
        else if (Options.Parser is not (ParserKind.Earley or ParserKind.Cyk))
        {
            throw new ConfigurationException($"{Options.Parser} doesn't support disambiguation. Use one of these parsers instead: Earley, Cyk");
        }

        if (Options.Priority == PriorityMode.Auto)
        {
            Options.Priority = PriorityMode.Normal;
        }

        ValidateFrontendArgs(Options.Parser, Options.Lexer);
    }

    private static void ValidateFrontendArgs(ParserKind parser, LexerKind lexer)
    {
        if (lexer == LexerKind.Custom || parser == ParserKind.None) return;
        var expected = parser switch
        {
            ParserKind.Lalr => new[] { LexerKind.Basic, LexerKind.Contextual },
            ParserKind.Earley => new[] { LexerKind.Basic, LexerKind.Dynamic, LexerKind.DynamicComplete },
            ParserKind.Cyk => new[] { LexerKind.Basic },
            _ => throw new ConfigurationException($"Unknown parser {parser}"),
        };
        if (!expected.Contains(lexer))
        {
            throw new ConfigurationException($"Parser {parser} does not support lexer {lexer}, expected one of {string.Join(", ", expected)}");
        }
    }

    private LexerConf CreateLexerConf(IReadOnlyList<TerminalDef> terminals, IReadOnlyList<string> ignoreTokens) =>
        new(terminals, ignoreTokens, Options.PostLex, Options.LexerCallbacks, Options.RegexOptions, strict: Options.Strict)
        {
            LexerKind = Options.Lexer,
            CustomLexerFactory = Options.CustomLexer,
        };

    private Lexer BuildLexer(bool dontIgnore = false)
    {
        var lexerConf = LexerConf;
        if (dontIgnore)
        {
            lexerConf = lexerConf.Copy();
            lexerConf.Ignore = Array.Empty<string>();
        }
        if (Options.Lexer == LexerKind.Custom && Options.CustomLexer is not null)
        {
            return Options.CustomLexer(lexerConf);
        }
        return new BasicLexer(lexerConf);
    }

    private void PrepareCallbacks()
    {
        _callbacks = new ParserCallbacks();
        // we don't need these callbacks if we aren't building a tree
        if (Options.Ambiguity != Ambiguity.Forest)
        {
            var parseTreeBuilder = new ParseTreeBuilder(
                Rules,
                Options.TreeFactory ?? ((data, children) => new Tree(data, children)),
                Options.PropagatePositions || Options.PropagatePositionsFilter is not null,
                Options.PropagatePositionsFilter,
                Options.Parser != ParserKind.Lalr && Options.Ambiguity == Ambiguity.Explicit,
                Options.MaybePlaceholders);
            foreach (var (rule, callback) in parseTreeBuilder.CreateCallback(Options.Transformer))
            {
                _callbacks.Rules[rule] = callback;
            }
        }

        if (Options.Transformer is { } transformer)
        {
            foreach (var terminal in Terminals)
            {
                if (transformer.TryGetTokenMethod(terminal.Name, out var callback))
                {
                    _callbacks.Tokens[terminal.Name] = callback!;
                }
            }
        }
    }

    private ParsingFrontend BuildParser(LalrParser? parser = null)
    {
        PrepareCallbacks();
        ValidateFrontendArgs(Options.Parser, Options.Lexer);
        var parserConf = new ParserConf(Rules, _callbacks, Options.Start) { ParserKind = Options.Parser };
        if (parser is not null)
        {
            parser = new LalrParser(parser.ParseTable, _callbacks, Options.Debug);
        }
        return new ParsingFrontend(LexerConf, parserConf, Options, parser);
    }

    /// <summary>Creates a parser with the grammar in the given file.</summary>
    /// <param name="grammarFilename">The path of the grammar file.</param>
    /// <param name="options">The options.</param>
    /// <param name="relTo">When given, the grammar file is located relative to the directory of this file.</param>
    public static Lark Open(string grammarFilename, LarkOptions? options = null, string? relTo = null)
    {
        if (relTo is not null)
        {
            var basePath = Path.GetDirectoryName(relTo) ?? "";
            grammarFilename = Path.Combine(basePath, grammarFilename);
        }
        var text = File.ReadAllText(grammarFilename);
        options = options?.Clone() ?? new LarkOptions();
        options.SourcePath ??= grammarFilename;
        return new Lark(text, options);
    }

    /// <summary>
    /// Creates a parser with a grammar embedded as a resource in <paramref name="assembly"/> (Lark's <c>open_from_package</c>).
    /// Imports in the grammar will also be resolved from the assembly resources.
    /// </summary>
    public static Lark OpenFromResource(Assembly assembly, string grammarPath, IEnumerable<string>? searchPaths = null, LarkOptions? options = null)
    {
        var loader = new EmbeddedResourceGrammarLoader(assembly, searchPaths ?? new[] { "" });
        var (fullPath, text) = loader.Load(grammarPath);
        options = options?.Clone() ?? new LarkOptions();
        options.SourcePath ??= fullPath;
        options.ImportPaths.Add(loader);
        return new Lark(text, options);
    }

    /// <summary>Starts a fluent configuration of a parser for the given grammar.</summary>
    public static LarkBuilder FromGrammar(string grammar) => new(grammar);

    /// <summary>Starts a fluent configuration of a parser for the grammar in the given file.</summary>
    public static LarkBuilder FromFile(string grammarFilename)
    {
        var builder = new LarkBuilder(File.ReadAllText(grammarFilename));
        builder.Options.SourcePath = grammarFilename;
        return builder;
    }

    /// <summary>Starts a fluent configuration of a parser for a grammar embedded as a resource.</summary>
    public static LarkBuilder FromResource(Assembly assembly, string grammarPath, params string[] searchPaths)
    {
        var loader = new EmbeddedResourceGrammarLoader(assembly, searchPaths.Length == 0 ? new[] { "" } : searchPaths);
        var (fullPath, text) = loader.Load(grammarPath);
        var builder = new LarkBuilder(text);
        builder.Options.SourcePath = fullPath;
        builder.Options.ImportPaths.Add(loader);
        return builder;
    }

    public override string ToString() => $"Lark(open({Utilities.Helpers.Repr(SourcePath)}), parser={Options.Parser}, lexer={Options.Lexer}, ...)";

    /// <summary>
    /// Only lex (and postlex) the text, without parsing it. Only relevant when the lexer is 'basic'.
    /// </summary>
    /// <param name="text">The text to lex.</param>
    /// <param name="dontIgnore">When true, the lexer will return all tokens, even those marked for %ignore.</param>
    /// <exception cref="UnexpectedCharactersException">The lexer cannot find a suitable match.</exception>
    public IEnumerable<Token> Lex(TextSlice text, bool dontIgnore = false)
    {
        var lexer = _lexer is null || dontIgnore ? BuildLexer(dontIgnore) : _lexer;
        var lexerThread = LexerThread.FromText(lexer, text);
        var stream = lexerThread.Lex(null);
        return Options.PostLex is not null ? Options.PostLex.Process(stream) : stream;
    }

    /// <summary>Gets information about a terminal.</summary>
    public TerminalDef GetTerminal(string name) => _terminalsDict[name];

    private ParsingFrontend RequireParser() =>
        _parser ?? throw new ConfigurationException("This instance was created in lexer-only mode (parser: None); it cannot parse.");

    /// <summary>Starts an interactive parsing session. Only works with the LALR parser.</summary>
    /// <param name="text">The text to parse. Required for <see cref="InteractiveParser.ResumeParse"/>.</param>
    /// <param name="start">The start symbol.</param>
    public InteractiveParser ParseInteractive(string? text = null, string? start = null) => RequireParser().ParseInteractive(text, start);

    /// <summary>Starts an interactive parsing session on a text slice. Only works with the LALR parser.</summary>
    public InteractiveParser ParseInteractive(TextSlice text, string? start = null) => RequireParser().ParseInteractive(text, start);

    /// <summary>
    /// Parses the text, and returns the parse result: a <see cref="Tree"/>, or whatever the embedded transformer returns.
    /// For a custom lexer, the input may be an object of any type.
    /// </summary>
    /// <param name="input">A <see cref="string"/>, a <see cref="TextSlice"/> (basic and contextual lexers only), or custom lexer input.</param>
    /// <param name="start">Required if Lark was given multiple possible start symbols.</param>
    /// <param name="onError">Called on <see cref="UnexpectedInputException"/> errors. Return true to resume parsing, or false to throw. LALR only.</param>
    public object? ParseObject(object? input, string? start = null, Func<UnexpectedInputException, bool>? onError = null)
    {
        if (onError is not null && Options.Parser != ParserKind.Lalr)
        {
            throw new NotSupportedException("The onError option is only implemented for the LALR(1) parser.");
        }
        return RequireParser().Parse(input, start, onError);
    }

    /// <summary>Parses the text according to the grammar and returns the parse tree.</summary>
    /// <exception cref="UnexpectedInputException">One of <see cref="UnexpectedCharactersException"/>,
    /// <see cref="UnexpectedTokenException"/> or <see cref="UnexpectedEofException"/>.</exception>
    public Tree Parse(string text, string? start = null, Func<UnexpectedInputException, bool>? onError = null) =>
        AsTree(ParseObject(text, start, onError));

    /// <summary>Parses a slice of text according to the grammar and returns the parse tree.</summary>
    public Tree Parse(TextSlice text, string? start = null, Func<UnexpectedInputException, bool>? onError = null) =>
        AsTree(ParseObject(text, start, onError));

    /// <summary>Parses the text and returns the result as <typeparamref name="T"/>; use this with an embedded transformer, or <see cref="Ambiguity.Forest"/>.</summary>
    public T Parse<T>(string text, string? start = null, Func<UnexpectedInputException, bool>? onError = null) =>
        (T)ParseObject(text, start, onError)!;

    private static Tree AsTree(object? result) => result as Tree ?? throw new InvalidCastException(
        $"The parse result is a {result?.GetType().Name ?? "null"}, not a Tree. Use Parse<T>() or ParseObject() to get it.");
}
