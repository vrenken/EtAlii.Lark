namespace EtAlii.Lark;

using System.Runtime.CompilerServices;
using System.Text;

public partial class Lark
{
    /// <summary>
    /// Parses the text of the reader. With the basic and contextual lexers, the text is lexed and parsed as it is read,
    /// so the input never needs to be in memory as a whole.
    /// </summary>
    /// <param name="reader">The text to parse.</param>
    /// <param name="start">Required if Lark was given multiple possible start symbols.</param>
    public Tree Parse(TextReader reader, string? start = null) => AsTree(ParseObject(reader, start));

    /// <summary>Parses the text of the stream; see <see cref="Parse(TextReader, string?)"/>. The stream is left open.</summary>
    /// <param name="stream">The text to parse.</param>
    /// <param name="encoding">The encoding of the stream; UTF-8 by default. A byte order mark overrides it.</param>
    /// <param name="start">Required if Lark was given multiple possible start symbols.</param>
    public Tree Parse(Stream stream, Encoding? encoding = null, string? start = null)
    {
        using var reader = CreateReader(stream, encoding);
        return Parse(reader, start);
    }

    /// <summary>Parses the text of the reader and returns the result as <typeparamref name="T"/>.</summary>
    public T Parse<T>(TextReader reader, string? start = null) => (T)ParseObject(reader, start)!;

    /// <summary>Parses the text of the reader, and returns the parse result: a <see cref="Tree"/>, or whatever the embedded transformer returns.</summary>
    public object? ParseObject(TextReader reader, string? start = null) => RequireParser().ParseStream(reader, start);

    /// <summary>
    /// Parses the text asynchronously. With LALR and the basic or contextual lexer, the text is lexed and parsed as it is
    /// read; the other parsers read all text first.
    /// </summary>
    /// <param name="reader">The text to parse.</param>
    /// <param name="start">Required if Lark was given multiple possible start symbols.</param>
    /// <param name="cancellationToken">Stops reading and parsing.</param>
    public async Task<Tree> ParseAsync(TextReader reader, string? start = null, CancellationToken cancellationToken = default) =>
        AsTree(await ParseObjectAsync(reader, start, cancellationToken).ConfigureAwait(false));

    /// <summary>Parses the text of the stream asynchronously; see <see cref="ParseAsync(TextReader, string?, CancellationToken)"/>. The stream is left open.</summary>
    /// <param name="stream">The text to parse.</param>
    /// <param name="encoding">The encoding of the stream; UTF-8 by default. A byte order mark overrides it.</param>
    /// <param name="start">Required if Lark was given multiple possible start symbols.</param>
    /// <param name="cancellationToken">Stops reading and parsing.</param>
    public async Task<Tree> ParseAsync(Stream stream, Encoding? encoding = null, string? start = null, CancellationToken cancellationToken = default)
    {
        using var reader = CreateReader(stream, encoding);
        return await ParseAsync(reader, start, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses the text asynchronously; see <see cref="ParseAsync(TextReader, string?, CancellationToken)"/>.</summary>
    public Task<Tree> ParseAsync(string text, string? start = null, CancellationToken cancellationToken = default) =>
        ParseAsync(new StringReader(text), start, cancellationToken);

    /// <summary>Parses the text of the reader asynchronously and returns the result as <typeparamref name="T"/>.</summary>
    public async Task<T> ParseAsync<T>(TextReader reader, string? start = null, CancellationToken cancellationToken = default) =>
        (T)(await ParseObjectAsync(reader, start, cancellationToken).ConfigureAwait(false))!;

    /// <summary>Parses the text of the stream asynchronously and returns the result as <typeparamref name="T"/>. The stream is left open.</summary>
    public async Task<T> ParseAsync<T>(Stream stream, Encoding? encoding = null, string? start = null, CancellationToken cancellationToken = default)
    {
        using var reader = CreateReader(stream, encoding);
        return await ParseAsync<T>(reader, start, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses the text of the reader asynchronously, and returns the parse result.</summary>
    public Task<object?> ParseObjectAsync(TextReader reader, string? start = null, CancellationToken cancellationToken = default) =>
        RequireParser().ParseStreamAsync(reader, start, cancellationToken);

    /// <summary>Lexes (and postlexes) the text of the reader as it is read, without parsing it.</summary>
    /// <param name="reader">The text to lex.</param>
    /// <param name="dontIgnore">When true, the lexer will return all tokens, even those marked for %ignore.</param>
    public IEnumerable<Token> Lex(TextReader reader, bool dontIgnore = false)
    {
        var lexer = _lexer is null || dontIgnore ? BuildLexer(dontIgnore) : _lexer;
        var streamingLexer = StreamingLexer.TryCreate(lexer, Math.Max(1, Options.StreamLookahead));
        if (streamingLexer is null) return Lex(reader.ReadToEnd(), dontIgnore);

        var stream = streamingLexer.Lex(CreateBuffer(reader), () => null);
        return Options.PostLex is not null ? Options.PostLex.Process(stream) : stream;
    }

    /// <summary>Lexes (and postlexes) the text of the reader asynchronously, as it is read, without parsing it.</summary>
    /// <param name="reader">The text to lex.</param>
    /// <param name="dontIgnore">When true, the lexer will return all tokens, even those marked for %ignore.</param>
    /// <param name="cancellationToken">Stops reading and lexing.</param>
    public async IAsyncEnumerable<Token> LexAsync(TextReader reader, bool dontIgnore = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lexer = _lexer is null || dontIgnore ? BuildLexer(dontIgnore) : _lexer;
        var streamingLexer = StreamingLexer.TryCreate(lexer, Math.Max(1, Options.StreamLookahead));
        if (streamingLexer is null)
        {
            // A custom lexer works on the whole text. Lex() applies the post-lexer.
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            foreach (var token in Lex(text, dontIgnore))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return token;
            }
            yield break;
        }

        var stream = streamingLexer.LexAsync(CreateBuffer(reader), () => null, cancellationToken);
        if (Options.PostLex is not null) stream = Options.PostLex.ProcessAsync(stream, cancellationToken);
        await foreach (var token in stream.WithCancellation(cancellationToken).ConfigureAwait(false)) yield return token;
    }

    /// <summary>Lexes the text of the stream asynchronously, as it is read. The stream is left open.</summary>
    public async IAsyncEnumerable<Token> LexAsync(Stream stream, Encoding? encoding = null, bool dontIgnore = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = CreateReader(stream, encoding);
        await foreach (var token in LexAsync(reader, dontIgnore, cancellationToken).ConfigureAwait(false)) yield return token;
    }

    private StreamingTextBuffer CreateBuffer(TextReader reader) =>
        new(reader, Math.Max(Options.StreamChunkSize, 2 * Options.StreamLookahead));

    private static StreamReader CreateReader(Stream stream, Encoding? encoding) =>
        new(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: true);
}
