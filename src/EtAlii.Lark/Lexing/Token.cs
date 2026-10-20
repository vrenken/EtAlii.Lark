namespace EtAlii.Lark;

using EtAlii.Lark.Utilities;

/// <summary>
/// A string with meta-information, that is produced by the lexer.
/// </summary>
/// <remarks>
/// Tokens compare equal to plain strings that have the same <see cref="Value"/>, and implicitly convert to
/// <see cref="string"/>, which mirrors upstream Lark where <c>Token</c> derives from <c>str</c>.
/// Two tokens are equal when both their type and value are equal.
/// </remarks>
public sealed class Token : IEquatable<Token>, IEquatable<string>, IComparable<Token>, ILarkPositioned
{
    public Token(string type, string value, int? startPos = null, int? line = null, int? column = null, int? endLine = null, int? endColumn = null, int? endPos = null)
    {
        Type = type;
        Value = value;
        StartPos = startPos;
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
        EndPos = endPos;
    }

    /// <summary>Name of the token (as specified in the grammar).</summary>
    public string Type { get; set; }

    /// <summary>Value of the token.</summary>
    public string Value { get; set; }

    /// <summary>The index of the token in the text.</summary>
    public int? StartPos { get; set; }

    /// <summary>The line of the token in the text (starting with 1).</summary>
    public int? Line { get; set; }

    /// <summary>The column of the token in the text (starting with 1).</summary>
    public int? Column { get; set; }

    /// <summary>The line where the token ends.</summary>
    public int? EndLine { get; set; }

    /// <summary>The next column after the end of the token.</summary>
    public int? EndColumn { get; set; }

    /// <summary>The index where the token ends (basically <c>StartPos + Length</c>).</summary>
    public int? EndPos { get; set; }

    /// <summary>The length of the value.</summary>
    public int Length => Value.Length;

    /// <summary>Returns a new token with the given type and/or value, and the position of this token.</summary>
    public Token Update(string? type = null, string? value = null) => NewBorrowPos(type ?? Type, value ?? Value, this);

    /// <summary>Creates a new token that borrows the position of <paramref name="borrow"/>.</summary>
    public static Token NewBorrowPos(string type, string value, Token borrow) =>
        new(type, value, borrow.StartPos, borrow.Line, borrow.Column, borrow.EndLine, borrow.EndColumn, borrow.EndPos);

    public Token Copy() => new(Type, Value, StartPos, Line, Column, EndLine, EndColumn, EndPos);

    public static implicit operator string(Token token) => token.Value;

    public override string ToString() => Value;

    /// <summary>A debug representation, e.g. <c>Token('NAME', 'abc')</c>.</summary>
    public string Repr() => $"Token({Helpers.Repr(Type)}, {Helpers.Repr(Value)})";

    public bool Equals(Token? other) => other is not null && Type == other.Type && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override bool Equals(object? obj) => obj switch
    {
        Token t => Equals(t),
        string s => Equals(s),
        _ => false,
    };

    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(Token? other) => string.CompareOrdinal(Value, other?.Value);

    public static bool operator ==(Token? a, Token? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(Token? a, Token? b) => !(a == b);

    public static bool operator ==(Token? a, string? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(Token? a, string? b) => !(a == b);

    int? ILarkPositioned.ContainerLine => null;
    int? ILarkPositioned.ContainerColumn => null;
    int? ILarkPositioned.ContainerStartPos => null;
    int? ILarkPositioned.ContainerEndLine => null;
    int? ILarkPositioned.ContainerEndColumn => null;
    int? ILarkPositioned.ContainerEndPos => null;
}

/// <summary>Something with a position in the source text (a <see cref="Token"/> or a <see cref="Meta"/>).</summary>
public interface ILarkPositioned
{
    int? Line { get; }
    int? Column { get; }
    int? StartPos { get; }
    int? EndLine { get; }
    int? EndColumn { get; }
    int? EndPos { get; }
    int? ContainerLine { get; }
    int? ContainerColumn { get; }
    int? ContainerStartPos { get; }
    int? ContainerEndLine { get; }
    int? ContainerEndColumn { get; }
    int? ContainerEndPos { get; }
}

/// <summary>
/// Implement this on custom objects that are returned by an embedded transformer, so that
/// <c>propagate_positions</c> can find their position.
/// </summary>
public interface ILarkMetaProvider
{
    Meta LarkMeta { get; }
}
