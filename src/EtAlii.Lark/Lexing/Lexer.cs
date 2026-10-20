namespace EtAlii.Lark;

using System.Text;
using System.Text.RegularExpressions;
using EtAlii.Lark.Parsers;
using EtAlii.Lark.Regexes;
using EtAlii.Lark.Utilities;

/// <summary>A utility class for keeping track of line &amp; column information.</summary>
public sealed class LineCounter : IEquatable<LineCounter>
{
    public LineCounter(char newlineChar = '\n')
    {
        NewlineChar = newlineChar;
    }

    public char NewlineChar { get; }

    public int CharPos { get; set; }

    public int Line { get; set; } = 1;

    public int Column { get; set; } = 1;

    public int LineStartPos { get; set; }

    /// <summary>Consume a token and calculate the new line &amp; column.</summary>
    public void Feed(string token, bool testNewline = true)
    {
        if (testNewline)
        {
            var newlines = 0;
            var last = -1;
            for (var i = 0; i < token.Length; i++)
            {
                if (token[i] == NewlineChar)
                {
                    newlines++;
                    last = i;
                }
            }
            if (newlines > 0)
            {
                Line += newlines;
                LineStartPos = CharPos + last + 1;
            }
        }
        CharPos += token.Length;
        Column = CharPos - LineStartPos + 1;
    }

    /// <summary>Consume a slice and calculate the new line &amp; column.</summary>
    public void Feed(TextSlice slice, bool testNewline = true)
    {
        if (testNewline)
        {
            var newlines = slice.Count(NewlineChar);
            if (newlines > 0)
            {
                Line += newlines;
                LineStartPos = CharPos + (slice.RIndex(NewlineChar) - slice.Start) + 1;
            }
        }
        CharPos += slice.Length;
        Column = CharPos - LineStartPos + 1;
    }

    public LineCounter Copy() => (LineCounter)MemberwiseClone();

    public bool Equals(LineCounter? other) => other is not null && CharPos == other.CharPos && NewlineChar == other.NewlineChar;

    public override bool Equals(object? obj) => obj is LineCounter l && Equals(l);

    public override int GetHashCode() => HashCode.Combine(CharPos, NewlineChar);
}

/// <summary>
/// Represents the current state of the lexer as it scans the text.
/// (Lexer objects are only instantiated per grammar, not per text.)
/// </summary>
public sealed class LexerState : IEquatable<LexerState>
{
    private string? _matchText;

    public LexerState(TextSlice text, LineCounter? lineCounter = null, Token? lastToken = null)
    {
        if (lineCounter is null)
        {
            lineCounter = new LineCounter();
            if (text.Start > 0)
            {
                lineCounter.Feed(new TextSlice(text.Text, 0, text.Start));
            }
        }
        if (!(text.Start <= lineCounter.CharPos && lineCounter.CharPos <= text.End))
        {
            throw new ArgumentException("LineCounter.CharPos is out of bounds");
        }
        Text = text;
        LineCounter = lineCounter;
        LastToken = lastToken;
    }

    /// <summary>Creates a lexer state for a custom (non-text) input, for use with custom lexers.</summary>
    public LexerState(object customInput)
    {
        CustomInput = customInput;
        Text = TextSlice.From(customInput as string ?? "");
        LineCounter = new LineCounter();
    }

    public TextSlice Text { get; internal set; }

    /// <summary>The input when parsing something other than text with a custom lexer.</summary>
    public object? CustomInput { get; }

    public LineCounter LineCounter { get; }

    public Token? LastToken { get; set; }

    /// <summary>The string that regular expressions are matched against (the text up to the end of the slice).</summary>
    internal string MatchText => _matchText ??= Text.End == Text.Text.Length ? Text.Text : Text.Text[..Text.End];

    public LexerState Copy() => new(Text, LineCounter.Copy(), LastToken) { _matchText = _matchText };

    public bool Equals(LexerState? other) =>
        other is not null && Text.Equals(other.Text) && LineCounter.Equals(other.LineCounter) && Equals(LastToken, other.LastToken);

    public override bool Equals(object? obj) => obj is LexerState s && Equals(s);

    public override int GetHashCode() => HashCode.Combine(Text.Start, Text.End, LineCounter);
}

/// <summary>A thread that ties a lexer instance and a lexer state, to be used by the parser.</summary>
public class LexerThread : IEquatable<LexerThread>
{
    public LexerThread(Lexer lexer, LexerState? state)
    {
        Lexer = lexer;
        State = state;
    }

    public Lexer Lexer { get; }

    public LexerState? State { get; }

    public static LexerThread FromText(Lexer lexer, TextSlice text) => new(lexer, new LexerState(text));

    public static LexerThread FromCustomInput(Lexer lexer, object input) => new(lexer, new LexerState(input));

    public virtual IEnumerable<Token> Lex(object? parserState)
    {
        if (State is null) throw new InvalidOperationException("Cannot lex: No text assigned to lexer state");
        return Lexer.Lex(State, parserState);
    }

    public virtual LexerThread Copy() => new(Lexer, State?.Copy());

    public bool Equals(LexerThread? other) => other is not null && ReferenceEquals(Lexer, other.Lexer) && Equals(State, other.State);

    public override bool Equals(object? obj) => obj is LexerThread t && Equals(t);

    public override int GetHashCode() => State?.GetHashCode() ?? 0;
}

/// <summary>Lexer interface. Implement <see cref="Lex"/> to write a custom lexer.</summary>
public abstract class Lexer
{
    /// <summary>Lex the text of the given state. <paramref name="parserState"/> is the state of the parser (if any).</summary>
    public abstract IEnumerable<Token> Lex(LexerState lexerState, object? parserState);
}

/// <summary>Post-processes the token stream of the lexer (e.g. to inject indentation tokens).</summary>
public abstract class PostLex
{
    public abstract IEnumerable<Token> Process(IEnumerable<Token> stream);

    /// <summary>Terminals that the contextual lexer must always accept (because the post-lexer consumes them).</summary>
    public virtual IReadOnlyCollection<string> AlwaysAccept => Array.Empty<string>();
}

internal sealed class PostLexConnector : Lexer
{
    private readonly Lexer _lexer;
    private readonly PostLex _postLexer;

    public PostLexConnector(Lexer lexer, PostLex postLexer)
    {
        _lexer = lexer;
        _postLexer = postLexer;
    }

    public override IEnumerable<Token> Lex(LexerState lexerState, object? parserState) =>
        _postLexer.Process(_lexer.Lex(lexerState, parserState));
}

/// <summary>Matches text against a combined regular expression of multiple terminals.</summary>
internal sealed class Scanner
{
    private readonly Regex _regex;
    private readonly List<(int Group, string Name)> _groups = new();
    private readonly Regex _fullRegex;

    public Scanner(IReadOnlyList<TerminalDef> terminals, RegexOptions options)
    {
        Terminals = terminals;
        AllowedTypes = terminals.Select(t => t.Name).ToHashSet();

        var sb = new StringBuilder();
        for (var i = 0; i < terminals.Count; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append("(?<__lark_t").Append(i).Append('>').Append(PyRegex.ToDotNet(terminals[i].Pattern.ToRegexp())).Append(')');
        }
        var pattern = sb.ToString();
        _regex = new Regex("\\G(?:" + pattern + ")", options | RegexOptions.CultureInvariant);
        _fullRegex = new Regex("\\A(?:" + pattern + ")\\z", options | RegexOptions.CultureInvariant);
        for (var i = 0; i < terminals.Count; i++)
        {
            _groups.Add((_regex.GroupNumberFromName("__lark_t" + i), terminals[i].Name));
        }
    }

    public IReadOnlyList<TerminalDef> Terminals { get; }

    public HashSet<string> AllowedTypes { get; }

    public (string Value, string Type)? Match(string text, int pos)
    {
        var m = _regex.Match(text, pos);
        if (!m.Success) return null;
        foreach (var (group, name) in _groups)
        {
            if (m.Groups[group].Success) return (m.Value, name);
        }
        return null;
    }

    public string? FullMatch(string text)
    {
        var m = _fullRegex.Match(text);
        if (!m.Success) return null;
        foreach (var (group, name) in _groups)
        {
            // group numbers are identical between both regexes as the patterns are identical
            if (m.Groups[group].Success) return name;
        }
        return null;
    }
}

/// <summary>Base class of lexers that produce one token at a time.</summary>
public abstract class AbstractBasicLexer : Lexer
{
    public abstract IReadOnlyDictionary<string, TerminalDef> TerminalsByName { get; }

    /// <summary>Returns the next token, or null at the end of the input.</summary>
    public abstract Token? NextToken(LexerState lexState, object? parserState = null);

    public override IEnumerable<Token> Lex(LexerState state, object? parserState)
    {
        while (true)
        {
            var token = NextToken(state, parserState);
            if (token is null) yield break;
            yield return token;
        }
    }
}

/// <summary>A lexer that matches all terminals of the grammar, independently of the parser state.</summary>
public class BasicLexer : AbstractBasicLexer
{
    private Scanner? _scanner;
    private Dictionary<string, Func<Token, Token>> _callback = new();
    private readonly IReadOnlyDictionary<string, Func<Token, Token>> _userCallbacks;
    private readonly RegexOptions _regexOptions;
    private readonly IReadOnlyDictionary<string, TerminalDef> _terminalsByName;

    public BasicLexer(LexerConf conf)
    {
        var terminals = conf.Terminals.ToList();

        if (!conf.SkipValidation)
        {
            foreach (var t in terminals)
            {
                var regexp = t.Pattern.ToRegexp();
                try
                {
                    _ = new Regex(PyRegex.ToDotNet(regexp), conf.RegexOptions);
                }
                catch (ArgumentException)
                {
                    throw new LexException($"Cannot compile token {t.Name}: {t.Pattern}");
                }

                if (t.Pattern.MinWidth == 0)
                {
                    throw new LexException($"Lexer does not allow zero-width terminals. ({t.Name}: {t.Pattern})");
                }
            }

            var names = terminals.Select(t => t.Name).ToHashSet();
            var missing = conf.Ignore.Where(i => !names.Contains(i)).ToList();
            if (missing.Count > 0)
            {
                throw new LexException("Ignore terminals are not defined: {" + string.Join(", ", missing.Select(Helpers.Repr)) + "}");
            }

            if (conf.Strict)
            {
                RegexCollisions.Check(terminals, conf.RegexOptions, strict: true);
            }
        }

        NewlineTypes = terminals.Where(t => RegexpHasNewline(t.Pattern.ToRegexp())).Select(t => t.Name).ToHashSet();
        IgnoreTypes = conf.Ignore.ToHashSet();

        terminals.Sort((a, b) =>
        {
            var c = b.Priority.CompareTo(a.Priority);
            if (c != 0) return c;
            c = b.Pattern.MaxWidth.CompareTo(a.Pattern.MaxWidth);
            if (c != 0) return c;
            c = b.Pattern.Value.Length.CompareTo(a.Pattern.Value.Length);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Name, b.Name);
        });
        Terminals = terminals;
        _userCallbacks = conf.Callbacks;
        _regexOptions = conf.RegexOptions;
        _terminalsByName = conf.TerminalsByName;
    }

    public IReadOnlyList<TerminalDef> Terminals { get; }

    public HashSet<string> IgnoreTypes { get; }

    public HashSet<string> NewlineTypes { get; }

    public override IReadOnlyDictionary<string, TerminalDef> TerminalsByName => _terminalsByName;

    internal static bool RegexpHasNewline(string r) =>
        r.Contains('\n') || r.Contains("\\n") || r.Contains("\\s") || r.Contains("[^") || (r.Contains("(?s") && r.Contains('.'));

    private Scanner BuildScanner()
    {
        var (terminals, callback) = CreateUnless(Terminals, _regexOptions);
        _callback = callback;

        foreach (var (type, f) in _userCallbacks)
        {
            if (_callback.TryGetValue(type, out var existing))
            {
                // Already a callback there, probably UnlessCallback
                var t = type;
                _callback[type] = token =>
                {
                    var t2 = existing(token);
                    return t2.Type == t ? f(token) : t2;
                };
            }
            else
            {
                _callback[type] = f;
            }
        }

        return new Scanner(terminals, _regexOptions);
    }

    internal Scanner Scanner => _scanner ??= BuildScanner();

    private static (List<TerminalDef>, Dictionary<string, Func<Token, Token>>) CreateUnless(IReadOnlyList<TerminalDef> terminals, RegexOptions options)
    {
        var strs = terminals.Where(t => t.Pattern is PatternStr).ToList();
        var embeddedStrs = new HashSet<TerminalDef>();
        var callback = new Dictionary<string, Func<Token, Token>>();
        foreach (var retok in terminals.Where(t => t.Pattern is PatternRE))
        {
            var unless = new List<TerminalDef>();
            foreach (var strtok in strs)
            {
                if (strtok.Priority != retok.Priority) continue;
                var s = strtok.Pattern.Value;
                var m = PyRegex.MatchStart(retok.Pattern.ToRegexp(), s, options);
                if (m is not null && m.Value == s)
                {
                    unless.Add(strtok);
                    if (strtok.Pattern.Flags.All(f => retok.Pattern.Flags.Contains(f)))
                    {
                        embeddedStrs.Add(strtok);
                    }
                }
            }
            if (unless.Count > 0)
            {
                var scanner = new Scanner(unless, options);
                callback[retok.Name] = t =>
                {
                    var res = scanner.FullMatch(t.Value);
                    if (res is not null) t.Type = res;
                    return t;
                };
            }
        }

        var newTerminals = terminals.Where(t => !embeddedStrs.Contains(t)).ToList();
        return (newTerminals, callback);
    }

    public (string Value, string Type)? Match(LexerState state, int pos) => Scanner.Match(state.MatchText, pos);

    public override Token? NextToken(LexerState lexState, object? parserState = null)
    {
        var lineCtr = lexState.LineCounter;
        var scanner = Scanner;
        while (lineCtr.CharPos < lexState.Text.End)
        {
            var res = scanner.Match(lexState.MatchText, lineCtr.CharPos);
            if (res is null)
            {
                var allowed = scanner.AllowedTypes.Where(t => !IgnoreTypes.Contains(t)).ToHashSet();
                if (allowed.Count == 0) allowed.Add("<END-OF-FILE>");
                throw new UnexpectedCharactersException(lexState.Text.Text, lineCtr.CharPos, lineCtr.Line, lineCtr.Column,
                    allowed: allowed, tokenHistory: lexState.LastToken is null ? null : new[] { lexState.LastToken },
                    state: parserState, terminalsByName: TerminalsByName);
            }

            var (value, type) = res.Value;
            var ignored = IgnoreTypes.Contains(type);
            Token? t = null;
            if (!ignored || _callback.ContainsKey(type))
            {
                t = new Token(type, value, lineCtr.CharPos, lineCtr.Line, lineCtr.Column);
            }
            lineCtr.Feed(value, NewlineTypes.Contains(type));
            if (t is not null)
            {
                t.EndLine = lineCtr.Line;
                t.EndColumn = lineCtr.Column;
                t.EndPos = lineCtr.CharPos;
                if (_callback.TryGetValue(t.Type, out var cb))
                {
                    t = cb(t);
                }
                if (!ignored)
                {
                    if (t is null) throw new LexException("Callbacks must return a token (returned None)");
                    lexState.LastToken = t;
                    return t;
                }
            }
        }
        return null;
    }
}

/// <summary>
/// A lexer that only matches the terminals accepted by the parser in its current state (LALR only).
/// </summary>
public class ContextualLexer : Lexer
{
    private readonly Dictionary<int, AbstractBasicLexer> _lexers = new();
    private readonly AbstractBasicLexer _rootLexer;

    public ContextualLexer(LexerConf conf, IReadOnlyDictionary<int, IReadOnlyCollection<string>> states, IReadOnlyCollection<string>? alwaysAccept = null,
        Func<LexerConf, AbstractBasicLexer>? basicLexerFactory = null)
    {
        basicLexerFactory ??= c => new BasicLexer(c);
        var terminals = conf.Terminals;
        var terminalsByName = conf.TerminalsByName;
        alwaysAccept ??= Array.Empty<string>();

        var lexerByTokens = new Dictionary<string, AbstractBasicLexer>();
        foreach (var (state, accepts) in states)
        {
            var key = string.Join("\u0001", accepts.OrderBy(a => a, StringComparer.Ordinal));
            if (!lexerByTokens.TryGetValue(key, out var lexer))
            {
                var all = new OrderedSet<string>(accepts);
                all.AddRange(conf.Ignore);
                all.AddRange(alwaysAccept);
                var lexerConf = conf.WithTerminals(all.Where(terminalsByName.ContainsKey).Select(n => terminalsByName[n]).ToList());
                lexer = basicLexerFactory(lexerConf);
                lexerByTokens[key] = lexer;
            }
            _lexers[state] = lexer;
        }

        var rootConf = conf.WithTerminals(terminals);
        rootConf.SkipValidation = true;
        _rootLexer = basicLexerFactory(rootConf);
    }

    public override IEnumerable<Token> Lex(LexerState lexerState, object? parserState)
    {
        var state = (ParserState)parserState!;
        while (true)
        {
            var lexer = _lexers[state.Position];
            Token? token;
            try
            {
                token = lexer.NextToken(lexerState, parserState);
            }
            catch (UnexpectedCharactersException e)
            {
                // In the contextual lexer, UnexpectedCharacters can mean that the terminal is defined, but not in the current context.
                // This tests the input against the global context, to provide a nicer error.
                var lastToken = lexerState.LastToken;
                Token? rootToken;
                try
                {
                    rootToken = _rootLexer.NextToken(lexerState, parserState);
                }
                catch (UnexpectedCharactersException)
                {
                    throw e;
                }
                if (rootToken is null) throw;
                throw new UnexpectedTokenException(rootToken, e.Allowed ?? Array.Empty<string>(), state: parserState,
                    tokenHistory: new[] { lastToken }, terminalsByName: _rootLexer.TerminalsByName);
            }
            if (token is null) yield break;
            yield return token;
        }
    }
}
