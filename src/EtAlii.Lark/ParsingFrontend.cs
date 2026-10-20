namespace EtAlii.Lark;

using EtAlii.Lark.Parsers;

/// <summary>Ties a lexer and a parser together (Lark's <c>ParsingFrontend</c>).</summary>
internal sealed class ParsingFrontend
{
    public ParsingFrontend(LexerConf lexerConf, ParserConf parserConf, LarkOptions? options, LalrParser? parser = null)
    {
        LexerConf = lexerConf;
        ParserConf = parserConf;
        Options = options;

        // Set-up parser
        Parser = parser ?? CreateParser(lexerConf, parserConf, options);

        // Set-up lexer
        var lexerKind = lexerConf.LexerKind;
        if (lexerKind is LexerKind.Dynamic or LexerKind.DynamicComplete)
        {
            if (lexerConf.PostLex is not null) throw new ConfigurationException("Can't use postlex with a dynamic lexer. Use basic or contextual instead");
            SkipLexer = true;
            return;
        }

        Lexer = lexerKind switch
        {
            LexerKind.Basic => new BasicLexer(lexerConf),
            LexerKind.Contextual => CreateContextualLexer(lexerConf, (LalrParser)Parser, lexerConf.PostLex),
            LexerKind.Custom => (lexerConf.CustomLexerFactory ?? throw new ConfigurationException("A custom lexer requires a lexer factory"))(lexerConf),
            _ => throw new ConfigurationException($"Bad value for lexer: {lexerKind}"),
        };

        if (lexerConf.PostLex is not null)
        {
            Lexer = new PostLexConnector(Lexer, lexerConf.PostLex);
        }
    }

    public LexerConf LexerConf { get; }

    public ParserConf ParserConf { get; }

    public LarkOptions? Options { get; }

    /// <summary>A <see cref="LalrParser"/>, <see cref="EarleyParser"/> or <see cref="CykFrontend"/>.</summary>
    public object Parser { get; }

    public Lexer? Lexer { get; }

    public bool SkipLexer { get; }

    private static object CreateParser(LexerConf lexerConf, ParserConf parserConf, LarkOptions? options)
    {
        switch (parserConf.ParserKind)
        {
            case ParserKind.Lalr:
                return new LalrParser(parserConf, options?.Debug ?? false, options?.Strict ?? false);
            case ParserKind.Earley:
            {
                var ambiguity = options?.Ambiguity ?? Ambiguity.Resolve;
                var resolveAmbiguity = ambiguity == Ambiguity.Resolve;
                var debug = options?.Debug ?? false;
                var treeFactory = options?.TreeFactory;
                var buildTree = ambiguity != Ambiguity.Forest;
                return lexerConf.LexerKind switch
                {
                    LexerKind.Dynamic => new XEarleyParser(lexerConf, parserConf, resolveAmbiguity, false, debug, treeFactory, buildTree),
                    LexerKind.DynamicComplete => new XEarleyParser(lexerConf, parserConf, resolveAmbiguity, true, debug, treeFactory, buildTree),
                    _ => new EarleyParser(lexerConf, parserConf, MatchEarleyBasic, resolveAmbiguity, debug, treeFactory, buildTree),
                };
            }
            case ParserKind.Cyk:
                return new CykFrontend(parserConf);
            default:
                throw new ConfigurationException($"Unknown parser: {parserConf.ParserKind}");
        }
    }

    private static bool MatchEarleyBasic(Symbol term, object token) => token is Token t && term.Name == t.Type;

    private static ContextualLexer CreateContextualLexer(LexerConf lexerConf, LalrParser parser, PostLex? postLex)
    {
        var states = parser.ParseTable.States.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value.Keys.ToList());
        var alwaysAccept = postLex?.AlwaysAccept ?? Array.Empty<string>();
        return new ContextualLexer(lexerConf, states, alwaysAccept);
    }

    public string VerifyStart(string? start)
    {
        if (start is null)
        {
            if (ParserConf.Start.Count > 1)
            {
                throw new ConfigurationException("Lark initialized with more than 1 possible start rule. Must specify which start rule to parse: " +
                                                 string.Join(", ", ParserConf.Start));
            }
            return ParserConf.Start[0];
        }
        if (!ParserConf.Start.Contains(start))
        {
            throw new ConfigurationException($"Unknown start rule {start}. Must be one of [{string.Join(", ", ParserConf.Start.Select(s => "'" + s + "'"))}]");
        }
        return start;
    }

    private LexerThread MakeLexerThread(object? text) => text switch
    {
        null => new LexerThread(Lexer!, null),
        string s => LexerThread.FromText(Lexer!, s),
        TextSlice ts => LexerThread.FromText(Lexer!, ts),
        _ => LexerThread.FromCustomInput(Lexer!, text),
    };

    public object? Parse(object? text, string? start = null, Func<UnexpectedInputException, bool>? onError = null)
    {
        if (LexerConf.LexerKind is LexerKind.Dynamic or LexerKind.DynamicComplete && text is TextSlice slice && !slice.IsCompleteText)
        {
            throw new ArgumentException($"Lexer {LexerConf.LexerKind} does not support text slices.");
        }

        var chosenStart = VerifyStart(start);
        switch (Parser)
        {
            case LalrParser lalr:
                return lalr.Parse(MakeLexerThread(text), chosenStart, onError);
            case EarleyParser earley:
            {
                if (onError is not null) throw new NotSupportedException("The onError option is only implemented for the LALR(1) parser.");
                if (SkipLexer)
                {
                    var str = text switch
                    {
                        string s => s,
                        TextSlice ts => ts.Text,
                        _ => throw new ArgumentException("The dynamic lexer requires text input"),
                    };
                    return earley.Parse(str, chosenStart);
                }
                var thread = MakeLexerThread(text);
                return earley.Parse(new Func<object?, IEnumerable<object>>(s => thread.Lex(s)), chosenStart);
            }
            case CykFrontend cyk:
                if (onError is not null) throw new NotSupportedException("The onError option is only implemented for the LALR(1) parser.");
                return cyk.Parse(MakeLexerThread(text), chosenStart);
            default:
                throw new InvalidOperationException();
        }
    }

    public InteractiveParser ParseInteractive(object? text = null, string? start = null)
    {
        var chosenStart = VerifyStart(start);
        if (Parser is not LalrParser lalr)
        {
            throw new ConfigurationException("ParseInteractive() currently only works with the LALR parser");
        }
        return lalr.ParseInteractive(MakeLexerThread(text), chosenStart);
    }
}

/// <summary>Runs the CYK parser and applies the tree building callbacks.</summary>
internal sealed class CykFrontend
{
    private readonly CykParser _parser;
    private readonly ParserCallbacks _callbacks;

    public CykFrontend(ParserConf parserConf)
    {
        _parser = new CykParser(parserConf.Rules);
        _callbacks = parserConf.Callbacks;
    }

    public object? Parse(LexerThread lexerThread, string start)
    {
        var tokens = lexerThread.Lex(null).ToList();
        var tree = _parser.Parse(tokens, start);
        return Transform(tree);
    }

    private object? Transform(CykTree tree)
    {
        foreach (var subtree in tree.IterSubtrees().ToList())
        {
            subtree.Children = subtree.Children.Select(c => c is CykTree ct ? ApplyCallback(ct) : c).ToList();
        }
        return ApplyCallback(tree);
    }

    private object? ApplyCallback(CykTree tree) => _callbacks.Rules[tree.Rule](tree.Children);
}
