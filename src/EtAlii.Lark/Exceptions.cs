namespace EtAlii.Lark;

using System.Text;
using EtAlii.Lark.Parsers;
using EtAlii.Lark.Utilities;

/// <summary>Marker interface for all parse errors (mirrors Lark's <c>ParseError</c> base class).</summary>
public interface IParseError { }

/// <summary>Marker interface for all lexer errors (mirrors Lark's <c>LexError</c> base class).</summary>
public interface ILexError { }

/// <summary>Base class of all Lark exceptions.</summary>
public class LarkException : Exception
{
    public LarkException() { }

    public LarkException(string message) : base(message) { }

    public LarkException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>Raised for invalid configuration.</summary>
public class ConfigurationException : LarkException
{
    public ConfigurationException(string message) : base(message) { }

    internal static void AssertConfig<T>(T value, IEnumerable<T> options, string? message = null)
    {
        var list = options.ToList();
        if (!list.Contains(value))
        {
            throw new ConfigurationException(string.Format(message ?? "Got {0}, expected one of {1}", value, "[" + string.Join(", ", list) + "]"));
        }
    }
}

/// <summary>Raised when the grammar is invalid.</summary>
public class GrammarException : LarkException
{
    public GrammarException(string message) : base(message) { }

    public GrammarException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>Raised when parsing fails.</summary>
public class ParseException : LarkException, IParseError
{
    public ParseException(string message) : base(message) { }

    public ParseException() { }
}

/// <summary>Raised when lexing fails.</summary>
public class LexException : LarkException, ILexError
{
    public LexException(string message) : base(message) { }

    public LexException() { }
}

/// <summary>
/// Base class for <see cref="UnexpectedCharactersException"/>, <see cref="UnexpectedTokenException"/> and
/// <see cref="UnexpectedEofException"/>. After catching one, use <see cref="GetContext"/> or
/// <see cref="MatchExamples{T}"/> to create a nicer error message.
/// </summary>
public abstract class UnexpectedInputException : LarkException
{
    protected UnexpectedInputException() { }

    /// <summary>The line of the error (1 based), or -1 for end of input.</summary>
    public int Line { get; protected set; }

    /// <summary>The column of the error (1 based), or -1 for end of input.</summary>
    public int Column { get; protected set; }

    /// <summary>The index of the error in the input.</summary>
    public int? PosInStream { get; protected set; }

    /// <summary>A value representing the parser state. Do not rely on its value or type, only on its equality.</summary>
    public object? State { get; protected set; }

    internal IReadOnlyDictionary<string, TerminalDef>? TerminalsByName { get; set; }

    /// <summary>
    /// For LALR, an interactive parser initialized to the point of failure. Can be used for debugging and error handling.
    /// </summary>
    public InteractiveParser? InteractiveParser { get; set; }

    /// <summary>
    /// Returns a pretty string pinpointing the error in the text, with <paramref name="span"/> amount of context
    /// characters around it.
    /// </summary>
    public string GetContext(string text, int span = 40)
    {
        var pos = PosInStream ?? 0;
        if (pos < 0) pos = 0;
        pos = Math.Min(pos, text.Length);
        var start = Math.Max(pos - span, 0);
        var end = Math.Min(pos + span, text.Length);
        var beforeText = text.Substring(start, pos - start);
        var nl = beforeText.LastIndexOf('\n');
        var before = nl >= 0 ? beforeText[(nl + 1)..] : beforeText;
        var afterText = text.Substring(pos, end - pos);
        var nl2 = afterText.IndexOf('\n');
        var after = nl2 >= 0 ? afterText[..nl2] : afterText;
        return before + after + "\n" + new string(' ', Helpers.ExpandTabs(before).Length) + "^\n";
    }

    /// <summary>
    /// Allows you to detect what's wrong in the input text by matching against example errors.
    /// Given a parse function and a sequence of (label, malformed examples), returns the label of the example that
    /// best matches the current error.
    /// </summary>
    public T? MatchExamples<T>(Func<string, object?> parseFn, IEnumerable<KeyValuePair<T, IEnumerable<string>>> examples, bool tokenTypeMatchFallback = false, bool useAccepts = true)
    {
        if (State is null) throw new InvalidOperationException("Not supported for this exception");

        (T? Label, bool IsFallback) candidate = (default, false);
        var hasCandidate = false;
        foreach (var (label, example) in examples)
        {
            foreach (var malformed in example)
            {
                try
                {
                    parseFn(malformed);
                }
                catch (UnexpectedInputException ut)
                {
                    if (!Equals(ut.State, State)) continue;

                    if (useAccepts && this is UnexpectedTokenException self && ut is UnexpectedTokenException other)
                    {
                        if (!SetEquals(other.Accepts, self.Accepts)) continue;
                    }

                    if (this is UnexpectedTokenException or UnexpectedEofException && ut is UnexpectedTokenException or UnexpectedEofException)
                    {
                        var myToken = TokenOf(this);
                        var theirToken = TokenOf(ut);
                        if (theirToken == myToken) return label;

                        if (tokenTypeMatchFallback)
                        {
                            if (theirToken.Type == myToken.Type && !candidate.IsFallback)
                            {
                                candidate = (label, true);
                                hasCandidate = true;
                            }
                        }
                    }

                    if (!hasCandidate)
                    {
                        candidate = (label, false);
                        hasCandidate = true;
                    }
                }
            }
        }
        return candidate.Label;
    }

    /// <summary>Overload accepting a sequence of (label, examples) tuples.</summary>
    public T? MatchExamples<T>(Func<string, object?> parseFn, IEnumerable<(T Label, string[] Examples)> examples, bool tokenTypeMatchFallback = false, bool useAccepts = true) =>
        MatchExamples(parseFn, examples.Select(e => new KeyValuePair<T, IEnumerable<string>>(e.Label, e.Examples)), tokenTypeMatchFallback, useAccepts);

    private static Token TokenOf(UnexpectedInputException e) => e switch
    {
        UnexpectedTokenException t => t.Token,
        UnexpectedEofException eof => eof.Token,
        _ => throw new InvalidOperationException(),
    };

    private static bool SetEquals(IReadOnlySet<string>? a, IReadOnlySet<string>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Count == b.Count && a.All(b.Contains);
    }

    protected string FormatExpected(IEnumerable<string> expected)
    {
        var names = expected.Select(n => TerminalsByName is not null && TerminalsByName.TryGetValue(n, out var t) ? t.UserRepr() : n);
        return "Expected one of: \n\t* " + string.Join("\n\t* ", names) + "\n";
    }
}

/// <summary>Raised by the parser when the input ends while it still expects a token.</summary>
public sealed class UnexpectedEofException : UnexpectedInputException, IParseError
{
    public UnexpectedEofException(IReadOnlyCollection<string> expected, object? state = null, IReadOnlyDictionary<string, TerminalDef>? terminalsByName = null)
    {
        Expected = expected;
        State = state;
        Token = new Token("<EOF>", "");
        PosInStream = -1;
        Line = -1;
        Column = -1;
        TerminalsByName = terminalsByName;
    }

    public IReadOnlyCollection<string> Expected { get; }

    public Token Token { get; }

    public override string Message => "Unexpected end-of-input. " + FormatExpected(Expected);
}

/// <summary>Raised by the lexer when it cannot match the next string of characters to any of its terminals.</summary>
public sealed class UnexpectedCharactersException : UnexpectedInputException, ILexError
{
    private readonly string _context;

    public UnexpectedCharactersException(string seq, int lexPos, int line, int column, IReadOnlyCollection<string>? allowed = null,
        object? consideredTokens = null, object? state = null, IReadOnlyList<Token>? tokenHistory = null,
        IReadOnlyDictionary<string, TerminalDef>? terminalsByName = null, object? consideredRules = null)
    {
        Line = line;
        Column = column;
        PosInStream = lexPos;
        State = state;
        TerminalsByName = terminalsByName;
        Allowed = allowed;
        ConsideredTokens = consideredTokens;
        ConsideredRules = consideredRules;
        TokenHistory = tokenHistory;
        Char = lexPos < seq.Length ? seq[lexPos] : '\0';
        _context = GetContext(seq);
    }

    /// <summary>The character that could not be matched.</summary>
    public char Char { get; }

    /// <summary>The set of terminals that were allowed at this point.</summary>
    public IReadOnlyCollection<string>? Allowed { get; }

    public object? ConsideredTokens { get; }

    public object? ConsideredRules { get; }

    public IReadOnlyList<Token>? TokenHistory { get; }

    public override string Message
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append($"No terminal matches '{Char}' in the current parser context, at line {Line} col {Column}");
            sb.Append("\n\n").Append(_context);
            if (Allowed is { Count: > 0 }) sb.Append(FormatExpected(Allowed));
            if (TokenHistory is { Count: > 0 })
            {
                sb.Append("\nPrevious tokens: ").Append(string.Join(", ", TokenHistory.Select(t => t?.Repr() ?? "None"))).Append('\n');
            }
            return sb.ToString();
        }
    }
}

/// <summary>Raised by the parser when the token it received doesn't match any valid step forward.</summary>
public sealed class UnexpectedTokenException : UnexpectedInputException, IParseError
{
    private IReadOnlySet<string>? _accepts;
    private bool _acceptsComputed;

    public UnexpectedTokenException(Token token, IReadOnlyCollection<string> expected, object? consideredRules = null, object? state = null,
        InteractiveParser? interactiveParser = null, IReadOnlyDictionary<string, TerminalDef>? terminalsByName = null, IReadOnlyList<Token?>? tokenHistory = null)
    {
        Line = token.Line ?? -1;
        Column = token.Column ?? -1;
        PosInStream = token.StartPos;
        State = state;
        Token = token;
        Expected = expected;
        ConsideredRules = consideredRules;
        InteractiveParser = interactiveParser;
        TerminalsByName = terminalsByName;
        TokenHistory = tokenHistory;
    }

    /// <summary>The mismatched token.</summary>
    public Token Token { get; }

    /// <summary>The set of expected token types.</summary>
    public IReadOnlyCollection<string> Expected { get; }

    public object? ConsideredRules { get; }

    public IReadOnlyList<Token?>? TokenHistory { get; }

    /// <summary>The set of token types that would have been accepted at this point (LALR only).</summary>
    public IReadOnlySet<string>? Accepts
    {
        get
        {
            if (!_acceptsComputed)
            {
                _accepts = InteractiveParser?.Accepts();
                _acceptsComputed = InteractiveParser is not null;
            }
            return _accepts;
        }
    }

    public override string Message
    {
        get
        {
            IEnumerable<string> expected = Accepts is { Count: > 0 } a ? a : Expected;
            var msg = $"Unexpected token {Token.Repr()} at line {Line}, column {Column}.\n{FormatExpected(expected)}";
            if (TokenHistory is { Count: > 0 })
            {
                msg += "Previous tokens: [" + string.Join(", ", TokenHistory.Select(t => t?.Repr() ?? "None")) + "]\n";
            }
            return msg;
        }
    }
}

/// <summary>Raised when visitors are interrupted by an exception.</summary>
public sealed class VisitException : LarkException
{
    public VisitException(string rule, object? obj, Exception origExc)
        : base($"Error trying to process rule \"{rule}\":\n\n{origExc.Message}", origExc)
    {
        Rule = rule;
        Obj = obj;
    }

    /// <summary>The name of the visit rule that failed.</summary>
    public string Rule { get; }

    /// <summary>The tree node or token that was being processed.</summary>
    public object? Obj { get; }

    /// <summary>The exception that caused it to fail.</summary>
    public Exception OrigExc => InnerException!;
}

/// <summary>Raised when a tree template variable has no mapping.</summary>
public sealed class MissingVariableException : LarkException
{
    public MissingVariableException(string message) : base(message) { }
}

/// <summary>Raised by the <see cref="Indenter"/> on an unexpected dedent.</summary>
public sealed class DedentException : LarkException
{
    public DedentException(string message) : base(message) { }
}
