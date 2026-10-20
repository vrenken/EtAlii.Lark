using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace EtAlii.Lark.Generators
{
    /// <summary>A type that contains the grammar class, e.g. <c>partial class Outer</c>.</summary>
    internal sealed record ContainingType(string Keyword, string Name, bool IsPartial);

    /// <summary>An option set on the attribute, as a C# assignment to a <c>LarkOptions</c> property.</summary>
    internal sealed record OptionValue(string Name, string Expression);

    /// <summary>A location that can be cached by the incremental generator.</summary>
    internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
    {
        public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);

        public static LocationInfo? From(Location? location) =>
            location?.SourceTree is null ? null : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }

    /// <summary>A type named in a <c>[LarkResult]</c> attribute, with what the generator needs to find common base types.</summary>
    /// <param name="Name">The fully qualified name.</param>
    /// <param name="BaseTypes">The type itself and its base classes, most derived first, without <c>object</c>.</param>
    /// <param name="Interfaces">All interfaces of the type.</param>
    /// <param name="InterfaceLinks">For each interface, its own interfaces, as <c>interface&gt;parent</c>.</param>
    internal sealed record ResultType(
        string Name,
        bool IsValueType,
        EquatableArray<string> BaseTypes,
        EquatableArray<string> Interfaces,
        EquatableArray<string> InterfaceLinks)
    {
        /// <summary>True when a value of this type converts to <paramref name="other"/> by a reference or boxing conversion.</summary>
        public bool ConvertsTo(string other) => Name == other || other == "object" || Enumerable.Contains(BaseTypes, other) || Enumerable.Contains(Interfaces, other);
    }

    /// <summary>A <c>[LarkResult(typeof(T), names...)]</c> attribute.</summary>
    internal sealed record ResultMapping(ResultType Type, EquatableArray<string> Names, LocationInfo? Location);

    /// <summary>A class with the <c>[LarkGrammar]</c> attribute.</summary>
    internal sealed record GrammarClassModel(
        string? Namespace,
        EquatableArray<ContainingType> ContainingTypes,
        string ClassName,
        string MetadataName,
        bool IsPartial,
        string? Path,
        string? InlineGrammar,
        EquatableArray<OptionValue> Options,
        EquatableArray<string> Start,
        bool KeepAllTokens,
        bool MaybePlaceholders,
        EquatableArray<ResultMapping> Results,
        LocationInfo? Location);

    /// <summary>An additional file with a <c>.lark</c> extension.</summary>
    internal sealed record GrammarFile(string Path, string Text);
}
