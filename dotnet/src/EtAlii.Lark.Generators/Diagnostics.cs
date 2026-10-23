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
    
        public static readonly DiagnosticDescriptor UnknownResultName = new(
            "LARK005",
            "Unknown name in [LarkResult]",
            "'{0}' is not a rule, alias or terminal of the grammar of '{1}'",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateResultName = new(
            "LARK006",
            "Name mapped more than once",
            "'{0}' appears in more than one [LarkResult] attribute of '{1}'",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor NoCommonResultType = new(
            "LARK007",
            "No common result type",
            "The children '{0}' of '{1}' can be {2}, which have no common base type; they are passed as object. {3}",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ResultTypeMismatch = new(
            "LARK008",
            "Result type doesn't fit the inlined rule",
            "The '{0}' rule is mapped to {1}, but it can contain '{2}', whose result type {3} doesn't convert to it",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor NoFixedShape = new(
            "LARK009",
            "Node children have no fixed shape",
            "The children of '{0}' have no fixed shape, so the typed visitor passes them as a list of objects. Aliases for its alternatives give each one typed parameters.",
            Category, DiagnosticSeverity.Info, isEnabledByDefault: true);
    }
}
