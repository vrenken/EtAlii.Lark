namespace EtAlii.Lark;

/// <summary>
/// A view of a string, between the start and end indices. Never creates a copy.
/// Lark accepts instances of <see cref="TextSlice"/> as input when the lexer is 'basic' or 'contextual'.
/// </summary>
public readonly record struct TextSlice
{
    /// <summary>Creates a slice. Negative indices count from the end; a null end means the end of the text.</summary>
    public TextSlice(string text, int start, int? end = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        if (start < 0)
        {
            start += text.Length;
            if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        }
        var e = end ?? text.Length;
        if (e < 0)
        {
            e += text.Length;
        }
        if (e > text.Length || e < 0) throw new ArgumentOutOfRangeException(nameof(end));
        if (start > text.Length) throw new ArgumentOutOfRangeException(nameof(start));
        Start = start;
        End = e;
    }

    public string Text { get; }

    public int Start { get; }

    public int End { get; }

    public int Length => End - Start;

    public static TextSlice From(string text) => new(text, 0, text.Length);

    public static implicit operator TextSlice(string text) => From(text);

    public bool IsCompleteText => Start == 0 && End == Text.Length;

    public int Count(char c)
    {
        var count = 0;
        for (var i = Start; i < End; i++)
        {
            if (Text[i] == c) count++;
        }
        return count;
    }

    public int RIndex(char c) => Text.LastIndexOf(c, End - 1, End - Start);

    public override string ToString() => Text.Substring(Start, End - Start);
}
