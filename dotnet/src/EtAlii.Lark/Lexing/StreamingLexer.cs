namespace EtAlii.Lark;

using System.Runtime.CompilerServices;
using EtAlii.Lark.Parsers;

/// <summary>
/// A sliding window over the text of a <see cref="TextReader"/>. Text before the lexer position is dropped as the
/// window moves, so memory use doesn't grow with the input.
/// </summary>
internal sealed class StreamingTextBuffer
{
    // Characters kept before the lexer position, for the context of error messages.
    private const int History = 64;

    // The largest read, when one token spans many chunks.
    private const int MaxChunkSize = 1024 * 1024;

    private readonly TextReader _reader;
    private char[] _chunk;
    private int _lastFillPosition = -1;

    public StreamingTextBuffer(TextReader reader, int chunkSize)
    {
        _reader = reader;
        _chunk = new char[chunkSize];
    }

    /// <summary>The text in the window.</summary>
    public string Text { get; private set; } = "";

    /// <summary>The position of the start of the window in the input.</summary>
    public int Offset { get; private set; }

    /// <summary>True when the whole input has been read.</summary>
    public bool Eof { get; private set; }

    public void Fill(int position)
    {
        Grow(position);
        Append(position, _reader.Read(_chunk, 0, _chunk.Length));
    }

    public async ValueTask FillAsync(int position, CancellationToken cancellationToken)
    {
        Grow(position);
        Append(position, await _reader.ReadAsync(_chunk.AsMemory(), cancellationToken).ConfigureAwait(false));
    }

    // Reads larger chunks while the lexer doesn't advance, so that long tokens don't cost quadratic copying.
    private void Grow(int position)
    {
        if (position == _lastFillPosition && _chunk.Length < MaxChunkSize)
        {
            _chunk = new char[Math.Min(_chunk.Length * 2, MaxChunkSize)];
        }
        _lastFillPosition = position;
    }

    private void Append(int position, int count)
    {
        if (count == 0)
        {
            Eof = true;
            return;
        }
        var keepFrom = Math.Max(Offset, position - History);
        Text = string.Concat(Text.AsSpan(keepFrom - Offset), _chunk.AsSpan(0, count));
        Offset = keepFrom;
    }
}

/// <summary>The outcome of one step of the <see cref="StreamingLexer"/>.</summary>
internal enum StreamingLexStep
{
    Token,
    NeedMoreInput,
    End,
}

/// <summary>
/// Lexes text that arrives in chunks, using the scanners of the basic or contextual lexer.
/// </summary>
/// <remarks>
/// A token is only accepted when at least <c>lookahead</c> characters follow it (or the input has ended). The result
/// equals that of lexing the whole text, as long as no terminal needs to look more than <c>lookahead</c> characters
/// past the end of a match to find a longer one.
/// </remarks>
internal sealed class StreamingLexer
{
    private readonly Func<object?, BasicLexer> _lexerForState;
    private readonly BasicLexer? _rootLexer;
    private readonly int _lookahead;
    private readonly LineCounter _lineCounter = new();
    private Token? _lastToken;

    private StreamingLexer(Func<object?, BasicLexer> lexerForState, BasicLexer? rootLexer, int lookahead)
    {
        _lexerForState = lexerForState;
        _rootLexer = rootLexer;
        _lookahead = lookahead;
    }

    /// <summary>The position of the lexer in the input.</summary>
    public int Position => _lineCounter.CharPos;

    public Token? LastToken => _lastToken;

    /// <summary>Creates a streaming lexer for the lexer, or returns null when the lexer can't stream (custom lexers).</summary>
    public static StreamingLexer? TryCreate(Lexer? lexer, int lookahead) => lexer switch
    {
        BasicLexer basic => new StreamingLexer(_ => basic, null, lookahead),
        ContextualLexer contextual when contextual.Lexers.Values.All(l => l is BasicLexer) && contextual.RootLexer is BasicLexer root =>
            new StreamingLexer(s => (BasicLexer)contextual.Lexers[((ParserState)s!).Position], root, lookahead),
        _ => null,
    };

    public StreamingLexStep Next(StreamingTextBuffer buffer, object? parserState, out Token? token)
    {
        token = null;
        var lineCtr = _lineCounter;
        while (true)
        {
            var text = buffer.Text;
            var pos = lineCtr.CharPos - buffer.Offset;
            if (pos >= text.Length) return buffer.Eof ? StreamingLexStep.End : StreamingLexStep.NeedMoreInput;

            var lexer = _lexerForState(parserState);
            var res = lexer.Scanner.Match(text, pos);
            if (res is null)
            {
                if (!buffer.Eof) return StreamingLexStep.NeedMoreInput;
                throw UnexpectedCharacters(lexer, buffer, pos, parserState);
            }

            var (value, type) = res.Value;
            // With more text, the match might grow, or another alternative might match.
            if (!buffer.Eof && pos + value.Length + _lookahead > text.Length) return StreamingLexStep.NeedMoreInput;

            var ignored = lexer.IgnoreTypes.Contains(type);
            Token? t = null;
            if (!ignored || lexer.Callbacks.ContainsKey(type))
            {
                t = new Token(type, value, lineCtr.CharPos, lineCtr.Line, lineCtr.Column);
            }
            lineCtr.Feed(value, lexer.NewlineTypes.Contains(type));
            if (t is null) continue;

            t.EndLine = lineCtr.Line;
            t.EndColumn = lineCtr.Column;
            t.EndPos = lineCtr.CharPos;
            if (lexer.Callbacks.TryGetValue(t.Type, out var cb))
            {
                t = cb(t);
            }
            if (ignored) continue;

            if (t is null) throw new LexException("Callbacks must return a token (returned None)");
            _lastToken = t;
            token = t;
            return StreamingLexStep.Token;
        }
    }

    private UnexpectedInputException UnexpectedCharacters(BasicLexer lexer, StreamingTextBuffer buffer, int pos, object? parserState)
    {
        var lineCtr = _lineCounter;
        var history = _lastToken is null ? null : new[] { _lastToken };

        // In the contextual lexer, unexpected characters can mean that the terminal is defined, but not in the current context.
        if (_rootLexer is not null && _rootLexer.Scanner.Match(buffer.Text, pos) is var (value, type) && !_rootLexer.IgnoreTypes.Contains(type))
        {
            var rootToken = new Token(type, value, lineCtr.CharPos, lineCtr.Line, lineCtr.Column);
            var expected = lexer.Scanner.AllowedTypes.Where(t => !lexer.IgnoreTypes.Contains(t)).ToHashSet();
            return new UnexpectedTokenException(rootToken, expected, state: parserState, tokenHistory: new[] { _lastToken }, terminalsByName: _rootLexer.TerminalsByName);
        }

        var allowed = lexer.Scanner.AllowedTypes.Where(t => !lexer.IgnoreTypes.Contains(t)).ToHashSet();
        if (allowed.Count == 0) allowed.Add("<END-OF-FILE>");
        var e = new UnexpectedCharactersException(buffer.Text, pos, lineCtr.Line, lineCtr.Column,
            allowed: allowed, tokenHistory: history, state: parserState, terminalsByName: lexer.TerminalsByName);
        e.PosInStream = lineCtr.CharPos;
        return e;
    }

    /// <summary>Lexes the reader, reading synchronously whenever more input is needed.</summary>
    public IEnumerable<Token> Lex(StreamingTextBuffer buffer, Func<object?> parserState)
    {
        while (true)
        {
            switch (Next(buffer, parserState(), out var token))
            {
                case StreamingLexStep.Token:
                    yield return token!;
                    break;
                case StreamingLexStep.NeedMoreInput:
                    buffer.Fill(Position);
                    break;
                default:
                    yield break;
            }
        }
    }

    /// <summary>Lexes the reader, reading asynchronously whenever more input is needed.</summary>
    public async IAsyncEnumerable<Token> LexAsync(StreamingTextBuffer buffer, Func<object?> parserState, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (Next(buffer, parserState(), out var token))
            {
                case StreamingLexStep.Token:
                    yield return token!;
                    break;
                case StreamingLexStep.NeedMoreInput:
                    await buffer.FillAsync(Position, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    yield break;
            }
        }
    }
}

/// <summary>A lexer thread over a <see cref="TextReader"/>, for parsers that consume tokens synchronously.</summary>
internal sealed class StreamingLexerThread : LexerThread
{
    private readonly StreamingLexer _lexer;
    private readonly StreamingTextBuffer _buffer;
    private readonly PostLex? _postLex;

    public StreamingLexerThread(Lexer lexer, StreamingLexer streamingLexer, StreamingTextBuffer buffer, PostLex? postLex) : base(lexer, null)
    {
        _lexer = streamingLexer;
        _buffer = buffer;
        _postLex = postLex;
    }

    public override IEnumerable<Token> Lex(object? parserState)
    {
        var stream = _lexer.Lex(_buffer, () => parserState);
        return _postLex is not null ? _postLex.Process(stream) : stream;
    }

    public override LexerThread Copy() => throw new NotSupportedException("A lexer thread over a stream can't be copied.");
}
