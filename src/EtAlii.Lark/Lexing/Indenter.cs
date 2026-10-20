namespace EtAlii.Lark;

using System.Runtime.CompilerServices;

/// <summary>
/// A post-lexer that "injects" indent/dedent tokens based on indentation.
/// </summary>
/// <remarks>
/// It keeps track of the current indentation, as well as the current level of parentheses.
/// Inside parentheses, the indentation is ignored, and no indent/dedent tokens get generated.
/// </remarks>
public abstract class Indenter : PostLex
{
    private int _parenLevel;
    private List<int> _indentLevel = new() { 0 };

    protected Indenter()
    {
        if (TabLen <= 0) throw new ArgumentException("TabLen must be greater than zero");
    }

    /// <summary>The name of the newline token.</summary>
    public abstract string NlType { get; }

    /// <summary>The names of the tokens that open a parenthesis.</summary>
    public abstract IReadOnlyCollection<string> OpenParenTypes { get; }

    /// <summary>The names of the tokens that close a parenthesis.</summary>
    public abstract IReadOnlyCollection<string> CloseParenTypes { get; }

    /// <summary>The name of the token that starts an indentation in the grammar (see <c>%declare</c>).</summary>
    public abstract string IndentType { get; }

    /// <summary>The name of the token that ends an indentation in the grammar (see <c>%declare</c>).</summary>
    public abstract string DedentType { get; }

    /// <summary>How many spaces a tab equals.</summary>
    public abstract int TabLen { get; }

    public int ParenLevel => _parenLevel;

    public IReadOnlyList<int> IndentLevel => _indentLevel;

    protected virtual IEnumerable<Token> HandleNl(Token token)
    {
        if (_parenLevel > 0) yield break;

        yield return token;

        var value = token.Value;
        var indentStr = value[(value.LastIndexOf('\n') + 1)..];
        var indent = indentStr.Count(c => c == ' ') + indentStr.Count(c => c == '\t') * TabLen;

        if (indent > _indentLevel[^1])
        {
            _indentLevel.Add(indent);
            yield return Token.NewBorrowPos(IndentType, indentStr, token);
        }
        else
        {
            while (indent < _indentLevel[^1])
            {
                _indentLevel.RemoveAt(_indentLevel.Count - 1);
                yield return Token.NewBorrowPos(DedentType, indentStr, token);
            }

            if (indent != _indentLevel[^1])
            {
                throw new DedentException($"Unexpected dedent to column {indent}. Expected dedent to {_indentLevel[^1]}");
            }
        }
    }

    private IEnumerable<Token> ProcessToken(Token t)
    {
        if (t.Type == NlType)
        {
            foreach (var x in HandleNl(t)) yield return x;
        }
        else
        {
            yield return t;
        }

        if (OpenParenTypes.Contains(t.Type))
        {
            _parenLevel++;
        }
        else if (CloseParenTypes.Contains(t.Type))
        {
            _parenLevel--;
            if (_parenLevel < 0) throw new InvalidOperationException("Unbalanced parentheses");
        }
    }

    private IEnumerable<Token> Finish(Token? token)
    {
        while (_indentLevel.Count > 1)
        {
            _indentLevel.RemoveAt(_indentLevel.Count - 1);
            yield return token is not null ? Token.NewBorrowPos(DedentType, "", token) : new Token(DedentType, "", 0, 0, 0, 0, 0, 0);
        }
    }

    private void Reset()
    {
        _parenLevel = 0;
        _indentLevel = new List<int> { 0 };
    }

    private IEnumerable<Token> ProcessInternal(IEnumerable<Token> stream)
    {
        Token? token = null;
        foreach (var t in stream)
        {
            token = t;
            foreach (var x in ProcessToken(t)) yield return x;
        }

        foreach (var x in Finish(token)) yield return x;
    }

    public override IEnumerable<Token> Process(IEnumerable<Token> stream)
    {
        Reset();
        return ProcessInternal(stream);
    }

    /// <summary>Processes the tokens as they arrive.</summary>
    public override async IAsyncEnumerable<Token> ProcessAsync(IAsyncEnumerable<Token> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Reset();
        Token? token = null;
        await foreach (var t in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            token = t;
            foreach (var x in ProcessToken(t)) yield return x;
        }

        foreach (var x in Finish(token)) yield return x;
    }

    public override IReadOnlyCollection<string> AlwaysAccept => new[] { NlType };
}

/// <summary>
/// A post-lexer that "injects" _INDENT/_DEDENT tokens based on indentation, according to the Python syntax.
/// </summary>
public class PythonIndenter : Indenter
{
    public override string NlType => "_NEWLINE";
    public override IReadOnlyCollection<string> OpenParenTypes => new[] { "LPAR", "LSQB", "LBRACE" };
    public override IReadOnlyCollection<string> CloseParenTypes => new[] { "RPAR", "RSQB", "RBRACE" };
    public override string IndentType => "_INDENT";
    public override string DedentType => "_DEDENT";
    public override int TabLen => 8;
}
