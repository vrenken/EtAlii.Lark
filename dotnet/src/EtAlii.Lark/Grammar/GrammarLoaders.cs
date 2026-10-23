namespace EtAlii.Lark;

using System.Reflection;

/// <summary>
/// Loads grammars for <c>%import</c> statements. Implement this to load grammars from custom locations
/// (Lark's import loader callables, e.g. <c>FromPackageLoader</c>).
/// </summary>
public interface IGrammarLoader
{
    /// <summary>
    /// Tries to load <paramref name="grammarPath"/> (e.g. <c>common.lark</c> or <c>sub/grammar.lark</c>).
    /// </summary>
    /// <param name="basePath">Null for library imports (<c>%import common.X</c>); for relative imports (<c>%import .x.Y</c>)
    /// the directory of the importing grammar.</param>
    /// <param name="grammarPath">The relative path of the grammar file to load.</param>
    /// <param name="fullPath">The full path of the loaded grammar; used for relative imports inside it.</param>
    /// <param name="text">The grammar text.</param>
    bool TryLoad(string? basePath, string grammarPath, out string fullPath, out string text);
}

/// <summary>Loads grammars from a directory on the file system.</summary>
public sealed class FileSystemGrammarLoader : IGrammarLoader
{
    public FileSystemGrammarLoader(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public bool TryLoad(string? basePath, string grammarPath, out string fullPath, out string text)
    {
        fullPath = Path.Combine(Directory, grammarPath);
        if (File.Exists(fullPath))
        {
            text = File.ReadAllText(fullPath);
            return true;
        }
        text = "";
        return false;
    }

    public override string ToString() => $"FileSystemGrammarLoader({Directory})";
}

/// <summary>
/// Loads grammars embedded as resources in an assembly (Lark's <c>FromPackageLoader</c>).
/// Relative imports are handled, as long as they resolve within the same assembly.
/// </summary>
public sealed class EmbeddedResourceGrammarLoader : IGrammarLoader
{
    private readonly Dictionary<string, string> _resourcesByPath = new(StringComparer.Ordinal);

    /// <summary>Creates a loader for the embedded resources of <paramref name="assembly"/>.</summary>
    /// <param name="assembly">The assembly that contains the grammars.</param>
    /// <param name="searchPaths">Directories (relative to the resource root, using '/') that are searched for absolute imports.</param>
    /// <param name="resourcePrefix">The prefix of the manifest resource names that corresponds to the resource root
    /// (defaults to the assembly name followed by a dot).</param>
    public EmbeddedResourceGrammarLoader(Assembly assembly, IEnumerable<string>? searchPaths = null, string? resourcePrefix = null)
    {
        Assembly = assembly;
        SearchPaths = (searchPaths ?? new[] { "" }).ToList();
        var prefix = resourcePrefix ?? assembly.GetName().Name + ".";
        Prefix = "embedded://" + assembly.GetName().Name + "/";
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = name[prefix.Length..];
            // Resource names use dots instead of directory separators; keep the extension.
            var ext = Path.GetExtension(relative);
            var withoutExt = relative[..^ext.Length];
            var path = withoutExt.Replace('.', '/') + ext;
            _resourcesByPath[path] = name;
        }
    }

    public Assembly Assembly { get; }

    public IReadOnlyList<string> SearchPaths { get; }

    /// <summary>The prefix of the full paths produced by this loader.</summary>
    public string Prefix { get; }

    public bool TryLoad(string? basePath, string grammarPath, out string fullPath, out string text)
    {
        IEnumerable<string> toTry;
        if (basePath is null)
        {
            toTry = SearchPaths;
        }
        else
        {
            // Check whether or not the importing grammar was loaded by this loader.
            if (!basePath.StartsWith(Prefix, StringComparison.Ordinal))
            {
                fullPath = "";
                text = "";
                return false;
            }
            toTry = new[] { basePath[Prefix.Length..] };
        }

        foreach (var dir in toTry)
        {
            var path = Normalize(string.IsNullOrEmpty(dir) ? grammarPath : dir.TrimEnd('/') + "/" + grammarPath);
            if (_resourcesByPath.TryGetValue(path, out var resource))
            {
                using var stream = Assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
                fullPath = Prefix + path;
                return true;
            }
        }
        fullPath = "";
        text = "";
        return false;
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Replace('\\', '/').Split('/'))
        {
            if (p is "" or ".") continue;
            if (p == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            else parts.Add(p);
        }
        return string.Join("/", parts);
    }

    /// <summary>Loads a grammar by its path within the resources.</summary>
    public (string FullPath, string Text) Load(string grammarPath)
    {
        if (!TryLoad(null, grammarPath, out var fullPath, out var text))
        {
            throw new FileNotFoundException("Cannot find grammar in given paths", grammarPath);
        }
        return (fullPath, text);
    }

    public override string ToString() => $"EmbeddedResourceGrammarLoader({Assembly.GetName().Name}, [{string.Join(", ", SearchPaths)}])";

    /// <summary>The loader for the grammars that come with Lark (common.lark, python.lark, ...).</summary>
    public static EmbeddedResourceGrammarLoader StandardLibrary { get; } =
        new(typeof(EmbeddedResourceGrammarLoader).Assembly, new[] { "Grammars" }, "EtAlii.Lark.");

    internal static string GetDirectory(string path)
    {
        var schemeEnd = path.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var slash = path.LastIndexOf('/');
            return slash > schemeEnd + 2 ? path[..slash] : path;
        }
        return Path.GetDirectoryName(path) ?? "";
    }
}
