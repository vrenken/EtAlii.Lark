namespace EtAlii.Lark.Parsers;

using EtAlii.Lark.Utilities;

/// <summary>The configuration of a single LALR parse (table, callbacks and start symbol).</summary>
public sealed class ParseConf
{
    public ParseConf(ParseTable parseTable, ParserCallbacks? callbacks, string start)
    {
        ParseTable = parseTable;
        StartState = parseTable.StartStates[start];
        EndState = parseTable.EndStates[start];
        States = parseTable.States;
        Callbacks = callbacks;
        Start = start;
    }

    public ParseTable ParseTable { get; }

    /// <summary>The callbacks used to build the tree. When null, reductions produce plain lists.</summary>
    public ParserCallbacks? Callbacks { get; set; }

    public string Start { get; }

    public int StartState { get; }

    public int EndState { get; }

    public Dictionary<int, Dictionary<string, LalrAction>> States { get; }

    internal ParseConf WithoutCallbacks()
    {
        var copy = (ParseConf)MemberwiseClone();
        copy.Callbacks = null;
        return copy;
    }
}

/// <summary>The state of an LALR parse: the state stack and the value stack.</summary>
public sealed class ParserState : IEquatable<ParserState>
{
    public ParserState(ParseConf parseConf, LexerThread lexer, List<int>? stateStack = null, List<object?>? valueStack = null)
    {
        ParseConf = parseConf;
        Lexer = lexer;
        StateStack = stateStack is { Count: > 0 } ? stateStack : new List<int> { parseConf.StartState };
        ValueStack = valueStack ?? new List<object?>();
    }

    public ParseConf ParseConf { get; internal set; }

    public LexerThread Lexer { get; internal set; }

    public List<int> StateStack { get; }

    public List<object?> ValueStack { get; }

    /// <summary>The current state of the parser (the top of the state stack).</summary>
    public int Position => StateStack[^1];

    // Necessary for match_examples() to work
    public bool Equals(ParserState? other) => other is not null && StateStack.Count == other.StateStack.Count && Position == other.Position;

    public override bool Equals(object? obj) => obj is ParserState s && Equals(s);

    public override int GetHashCode() => HashCode.Combine(StateStack.Count, Position);

    public ParserState Copy(bool deepCopyValues = true) => new(
        ParseConf,
        Lexer,
        new List<int>(StateStack),
        deepCopyValues ? ValueStack.Select(Tree.DeepCopyValue).ToList() : new List<object?>(ValueStack));

    /// <summary>Feeds a token to the parser. Returns the result when the end token completes the parse.</summary>
    public object? FeedToken(Token token, bool isEnd = false)
    {
        var stateStack = StateStack;
        var valueStack = ValueStack;
        var states = ParseConf.States;
        var endState = ParseConf.EndState;
        var callbacks = ParseConf.Callbacks;

        while (true)
        {
            var state = stateStack[^1];
            if (!states[state].TryGetValue(token.Type, out var action))
            {
                var expected = states[state].Keys.Where(Helpers.IsUpper).ToHashSet();
                throw new UnexpectedTokenException(token, expected, state: this, interactiveParser: null);
            }

            if (action.IsShift)
            {
                // shift once and return
                stateStack.Add(action.State);
                valueStack.Add(callbacks is not null && callbacks.Tokens.TryGetValue(token.Type, out var tc) ? tc(token) : token);
                return null;
            }

            // reduce+shift as many times as necessary
            var rule = action.Rule!;
            var size = rule.Expansion.Count;
            List<object?> s;
            if (size > 0)
            {
                s = valueStack.GetRange(valueStack.Count - size, size);
                stateStack.RemoveRange(stateStack.Count - size, size);
                valueStack.RemoveRange(valueStack.Count - size, size);
            }
            else
            {
                s = new List<object?>();
            }

            object? value = callbacks is not null ? callbacks.Rules[rule](s) : s;

            var goTo = states[stateStack[^1]][rule.Origin.Name];
            stateStack.Add(goTo.State);
            valueStack.Add(value);

            if (isEnd && stateStack[^1] == endState)
            {
                return valueStack[^1];
            }
        }
    }
}

/// <summary>The LALR(1) parser.</summary>
public sealed class LalrParser
{
    internal LalrParser(ParserConf parserConf, bool debug = false, bool strict = false)
    {
        var analysis = new LalrAnalyzer(parserConf, debug, strict);
        analysis.ComputeLalr();
        ParseTable = analysis.ParseTable;
        ParserConf = parserConf;
        Parser = new LalrCoreParser(ParseTable, parserConf.Callbacks, debug);
    }

    internal LalrParser(ParseTable parseTable, ParserCallbacks callbacks, bool debug)
    {
        ParseTable = parseTable;
        Parser = new LalrCoreParser(parseTable, callbacks, debug);
    }

    public ParseTable ParseTable { get; }

    internal ParserConf? ParserConf { get; }

    internal LalrCoreParser Parser { get; }

    public InteractiveParser ParseInteractive(LexerThread lexer, string start) => (InteractiveParser)Parser.Parse(lexer, start, startInteractive: true)!;

    public object? Parse(LexerThread lexer, string start, Func<UnexpectedInputException, bool>? onError = null)
    {
        try
        {
            return Parser.Parse(lexer, start);
        }
        catch (UnexpectedInputException e0) when (onError is not null)
        {
            var e = e0;
            while (true)
            {
                LexerState? s = null;
                var p = 0;
                if (e is UnexpectedCharactersException)
                {
                    s = e.InteractiveParser!.LexerThread.State!;
                    p = s.LineCounter.CharPos;
                }

                if (!onError(e)) throw e;

                if (e is UnexpectedCharactersException && s is not null)
                {
                    // If user didn't change the character position, then we should
                    if (p == s.LineCounter.CharPos)
                    {
                        s.LineCounter.Feed(s.Text.Text.Substring(p, Math.Min(1, s.Text.Text.Length - p)));
                    }
                }

                try
                {
                    return e.InteractiveParser!.ResumeParse();
                }
                catch (UnexpectedTokenException e2)
                {
                    if (e is UnexpectedTokenException et && et.Token.Type == "$END" && e2.Token.Type == "$END" &&
                        Equals(e.InteractiveParser, e2.InteractiveParser))
                    {
                        // Prevent infinite loop
                        throw;
                    }
                    e = e2;
                }
                catch (UnexpectedCharactersException e2)
                {
                    e = e2;
                }
            }
        }
    }
}

internal sealed class LalrCoreParser
{
    public LalrCoreParser(ParseTable parseTable, ParserCallbacks callbacks, bool debug = false)
    {
        ParseTable = parseTable;
        Callbacks = callbacks;
        Debug = debug;
    }

    public ParseTable ParseTable { get; }

    public ParserCallbacks Callbacks { get; }

    public bool Debug { get; }

    public object? Parse(LexerThread lexer, string start, List<object?>? valueStack = null, List<int>? stateStack = null, bool startInteractive = false)
    {
        var parseConf = new ParseConf(ParseTable, Callbacks, start);
        var parserState = new ParserState(parseConf, lexer, stateStack, valueStack);
        if (startInteractive)
        {
            return new InteractiveParser(this, parserState, parserState.Lexer);
        }
        return ParseFromState(parserState);
    }

    /// <summary>Run the main LALR parser loop.</summary>
    public object? ParseFromState(ParserState state, Token? lastToken = null)
    {
        try
        {
            var token = lastToken;
            foreach (var t in state.Lexer.Lex(state))
            {
                token = t;
                state.FeedToken(t);
            }

            var endToken = token is not null ? Token.NewBorrowPos("$END", "", token) : new Token("$END", "", 0, 1, 1);
            return state.FeedToken(endToken, true);
        }
        catch (UnexpectedInputException e)
        {
            e.InteractiveParser = new InteractiveParser(this, state, state.Lexer);
            throw;
        }
    }
}

/// <summary>
/// InteractiveParser gives you advanced control over parsing and error handling when parsing with LALR.
/// For a simpler interface, see the <c>onError</c> argument of <see cref="Lark.Parse(string, string?, Func{UnexpectedInputException, bool}?)"/>.
/// </summary>
public class InteractiveParser : IEquatable<InteractiveParser>
{
    internal InteractiveParser(LalrCoreParser parser, ParserState parserState, LexerThread lexerThread)
    {
        Parser = parser;
        ParserState = parserState;
        LexerThread = lexerThread;
    }

    internal LalrCoreParser Parser { get; }

    public ParserState ParserState { get; }

    public LexerThread LexerThread { get; }

    /// <summary>The result of the parse, once the end token was fed (see <see cref="IterParse"/>).</summary>
    public object? Result { get; protected set; }

    /// <summary>Feed the parser with a token, and advance it to the next state, as if it received it from the lexer.</summary>
    public virtual object? FeedToken(Token token) => ParserState.FeedToken(token, token.Type == "$END");

    /// <summary>
    /// Step through the different stages of the parse, by reading tokens from the lexer and feeding them to the
    /// parser, one per iteration. When the parse is over, the resulting tree can be found in <see cref="Result"/>.
    /// </summary>
    public IEnumerable<Token> IterParse()
    {
        foreach (var token in LexerThread.Lex(ParserState))
        {
            yield return token;
            Result = FeedToken(token);
        }
    }

    /// <summary>Try to feed the rest of the lexer state into the interactive parser. Does not feed an '$END' token.</summary>
    public virtual List<Token> ExhaustLexer() => IterParse().ToList();

    /// <summary>Feed a '$END' Token. Borrows from <paramref name="lastToken"/> if given.</summary>
    public object? FeedEof(Token? lastToken = null)
    {
        var eof = lastToken is not null ? Token.NewBorrowPos("$END", "", lastToken) : new Token("$END", "", 0, 1, 1);
        return FeedToken(eof);
    }

    /// <summary>Create a new interactive parser with a separate state.</summary>
    public virtual InteractiveParser Copy(bool deepCopyValues = true) =>
        new(Parser, ParserState.Copy(deepCopyValues), LexerThread.Copy());

    public bool Equals(InteractiveParser? other) =>
        other is not null && ParserState.Equals(other.ParserState) && LexerThread.Equals(other.LexerThread);

    public override bool Equals(object? obj) => obj is InteractiveParser p && Equals(p);

    public override int GetHashCode() => HashCode.Combine(ParserState, LexerThread);

    /// <summary>Convert to an <see cref="ImmutableInteractiveParser"/>.</summary>
    public ImmutableInteractiveParser AsImmutable()
    {
        var p = Copy();
        return new ImmutableInteractiveParser(p.Parser, p.ParserState, p.LexerThread);
    }

    /// <summary>Print the output of <see cref="Choices"/> in a way that's easier to read.</summary>
    public string Pretty()
    {
        var lines = new List<string> { "Parser choices:" };
        foreach (var (k, v) in Choices()) lines.Add($"\t- {k} -> {v}");
        lines.Add($"stack size: {ParserState.StateStack.Count}");
        return string.Join("\n", lines);
    }

    /// <summary>Returns a dictionary of token types, matched to their action in the parser. Only returns token types that are accepted by the current state.</summary>
    public IReadOnlyDictionary<string, LalrAction> Choices() => ParserState.ParseConf.ParseTable.States[ParserState.Position];

    /// <summary>Returns the set of possible tokens that will advance the parser into a new valid state.</summary>
    public IReadOnlySet<string> Accepts()
    {
        var accepts = new HashSet<string>();
        var confNoCallbacks = ParserState.ParseConf.WithoutCallbacks();
        foreach (var t in Choices().Keys)
        {
            if (!Helpers.IsUpper(t)) continue;
            var newCursor = new InteractiveParser(Parser, ParserState.Copy(deepCopyValues: false), LexerThread.Copy());
            newCursor.ParserState.ParseConf = confNoCallbacks;
            try
            {
                newCursor.ParserState.FeedToken(new Token(t, ""), t == "$END");
            }
            catch (UnexpectedTokenException)
            {
                continue;
            }
            accepts.Add(t);
        }
        return accepts;
    }

    /// <summary>Resume automated parsing from the current state.</summary>
    public object? ResumeParse() => Parser.ParseFromState(ParserState, LexerThread.State?.LastToken);
}

/// <summary>Same as <see cref="InteractiveParser"/>, but operations create a new instance instead of changing it in-place.</summary>
public sealed class ImmutableInteractiveParser : InteractiveParser
{
    internal ImmutableInteractiveParser(LalrCoreParser parser, ParserState parserState, LexerThread lexerThread)
        : base(parser, parserState, lexerThread) { }

    /// <summary>Feeds the token to a copy of this parser and returns the copy.</summary>
    public new ImmutableInteractiveParser FeedToken(Token token)
    {
        var c = (ImmutableInteractiveParser)Copy();
        c.Result = c.ParserState.FeedToken(token, token.Type == "$END");
        return c;
    }

    public override InteractiveParser Copy(bool deepCopyValues = true) =>
        new ImmutableInteractiveParser(Parser, ParserState.Copy(deepCopyValues), LexerThread.Copy());

    /// <summary>Try to feed the rest of the lexer state into the parser. Returns a new parser; does not feed an '$END' token.</summary>
    public new ImmutableInteractiveParser ExhaustLexer()
    {
        var cursor = AsMutable();
        cursor.ExhaustLexer();
        return cursor.AsImmutable();
    }

    /// <summary>Convert to an <see cref="InteractiveParser"/>.</summary>
    public InteractiveParser AsMutable()
    {
        var p = Copy();
        return new InteractiveParser(p.Parser, p.ParserState, p.LexerThread);
    }

    public override int GetHashCode() => HashCode.Combine(ParserState, LexerThread);
}
