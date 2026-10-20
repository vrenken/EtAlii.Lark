namespace EtAlii.Lark;

/// <summary>
/// Generates a type-safe parser for a Lark grammar on a <c>partial</c> class.
/// </summary>
/// <remarks>
/// The grammar is either a file, included in the project as <c>&lt;AdditionalFiles Include="calc.lark" /&gt;</c>,
/// or inline text in <see cref="Grammar"/>. The source generator adds to the class:
/// <list type="bullet">
/// <item>static <c>Parse</c>, <c>ParseAsync</c> and <c>Lex</c> methods, and a typed <c>Parse{Start}</c> method per start rule;</item>
/// <item><c>Rules</c> and <c>Terminals</c> classes with the names of the grammar symbols;</item>
/// <item>a node struct per rule (and alias), with typed properties for the children of rules with a fixed shape;</item>
/// <item>an abstract <c>Visitor&lt;TResult&gt;</c> with a visit method per rule.</item>
/// <item>with <see cref="LarkResultAttribute"/>s, a typed, bottom-up <c>Visitor</c> whose visit methods get their children as typed parameters.</item>
/// </list>
/// Implement <c>static partial void ConfigureOptions(LarkOptions options)</c> in the class to set other options.
/// </remarks>
/// <example>
/// <code>
/// [LarkGrammar("calc.lark", Parser = ParserKind.Lalr)]
/// public partial class CalcParser { }
///
/// var sum = CalcParser.ParseStart("1 + 2");
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class LarkGrammarAttribute : Attribute
{
    /// <summary>Uses the inline <see cref="Grammar"/>.</summary>
    public LarkGrammarAttribute() { }

    /// <param name="path">The path of the grammar file; it must be one of the project's <c>AdditionalFiles</c>.</param>
    public LarkGrammarAttribute(string path)
    {
        Path = path;
    }

    /// <summary>The path of the grammar file, relative to the project, or just its file name.</summary>
    public string? Path { get; }

    /// <summary>The grammar text, as an alternative to a file.</summary>
    public string? Grammar { get; set; }

    /// <inheritdoc cref="LarkOptions.Parser"/>
    public ParserKind Parser { get; set; }

    /// <inheritdoc cref="LarkOptions.Lexer"/>
    public LexerKind Lexer { get; set; }

    /// <inheritdoc cref="LarkOptions.Start"/>
    public string[]? Start { get; set; }

    /// <inheritdoc cref="LarkOptions.Ambiguity"/>
    public Ambiguity Ambiguity { get; set; }

    /// <inheritdoc cref="LarkOptions.KeepAllTokens"/>
    public bool KeepAllTokens { get; set; }

    /// <inheritdoc cref="LarkOptions.MaybePlaceholders"/>
    public bool MaybePlaceholders { get; set; } = true;

    /// <inheritdoc cref="LarkOptions.PropagatePositions"/>
    public bool PropagatePositions { get; set; }

    /// <inheritdoc cref="LarkOptions.Strict"/>
    public bool Strict { get; set; }
}
