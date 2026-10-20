using Microsoft.CodeAnalysis;

namespace EtAlii.Lark.Generators
{
    internal static class Diagnostics
    {
        private const string Category = "EtAlii.Lark";

        public static readonly DiagnosticDescriptor NotPartial = new(
            "LARK001",
            "The grammar class must be partial",
            "The class '{0}' must be partial, as must its containing types, to generate a Lark parser",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor GrammarFileNotFound = new(
            "LARK002",
            "The grammar file was not found",
            "The grammar file '{0}' was not found; add it to the project as <AdditionalFiles Include=\"{0}\" />",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor GrammarNotAnalyzed = new(
            "LARK003",
            "The grammar could not be analyzed",
            "The grammar of '{0}' could not be analyzed: {1}. The parser is generated without typed nodes; parsing reports the actual error.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor GrammarSourceMissing = new(
            "LARK004",
            "Specify one grammar source",
            "The [LarkGrammar] attribute of '{0}' needs either a grammar file path or the Grammar property, but not both",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
    }
}
