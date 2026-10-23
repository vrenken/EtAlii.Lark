namespace EtAlii.Lark;

/// <summary>Tools for loading and inspecting Lark grammars, without building a parser.</summary>
public static class GrammarTools
{
    /// <summary>The example errors used to produce friendly grammar error messages (Lark's <c>GRAMMAR_ERRORS</c>).</summary>
    public static IReadOnlyList<(string Message, string[] Examples)> GrammarErrors => MetaGrammar.GrammarErrors;

    /// <summary>Loads a grammar (resolving its imports), without compiling it into a parser.</summary>
    public static Grammar Load(string grammar, string sourcePath = "<string>", IEnumerable<IGrammarLoader>? importPaths = null, bool keepAllTokens = false) =>
        GrammarBuilder.Load(grammar, sourcePath, importPaths?.ToList(), keepAllTokens).Grammar;

    /// <summary>
    /// Finds the syntax errors in a grammar, recovering after each one. Returns at most one error per line, with a
    /// friendly description of the error.
    /// </summary>
    public static IReadOnlyList<(UnexpectedInputException Error, string Message)> FindGrammarErrors(string text, string start = "start") =>
        MetaGrammar.FindGrammarErrors(text, start);

    /// <summary>Returns the paths of the grammars imported by the given grammar (recursively).</summary>
    public static IReadOnlyList<string> ListGrammarImports(string grammar, IEnumerable<IGrammarLoader>? importPaths = null) =>
        GrammarBuilder.ListGrammarImports(grammar, importPaths?.ToList());
}
