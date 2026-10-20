using System;
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
        LocationInfo? Location);

    /// <summary>An additional file with a <c>.lark</c> extension.</summary>
    internal sealed record GrammarFile(string Path, string Text);
}
