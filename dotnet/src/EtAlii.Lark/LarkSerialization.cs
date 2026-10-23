namespace EtAlii.Lark;

using System.Text;
using System.Text.Json;
using EtAlii.Lark.Parsers;
using EtAlii.Lark.Utilities;

/// <summary>A file system abstraction, used for the grammar cache (replaceable for testing).</summary>
public interface ILarkFileSystem
{
    /// <summary>Opens the file for reading; throws <see cref="FileNotFoundException"/> when it doesn't exist.</summary>
    Stream OpenRead(string path);

    /// <summary>Creates (or overwrites) the file.</summary>
    Stream OpenWrite(string path);

    bool Exists(string path);
}

/// <summary>The default, physical, file system.</summary>
public sealed class PhysicalLarkFileSystem : ILarkFileSystem
{
    public static PhysicalLarkFileSystem Instance { get; } = new();

    public Stream OpenRead(string path) => File.OpenRead(path);

    public Stream OpenWrite(string path) => File.Create(path);

    public bool Exists(string path) => File.Exists(path);
}

public sealed partial class Lark
{
    private const int FormatVersion = 1;

    /// <summary>The file system used for the cache. Replaceable for testing.</summary>
    public static ILarkFileSystem FileSystem { get; set; } = PhysicalLarkFileSystem.Instance;

    private static string LibraryVersion => typeof(Lark).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>
    /// Saves the parser (the compiled grammar and the LALR parse table) to the given stream. Useful for caching.
    /// Callbacks (transformer, postlex, lexer callbacks, tree factory) are not saved; pass them to <see cref="Load"/>.
    /// </summary>
    public void Save(Stream stream)
    {
        if (Options.Parser != ParserKind.Lalr)
        {
            throw new NotSupportedException("Lark.Save() is only implemented for the LALR(1) parser.");
        }
        using var writer = new Utf8JsonWriter(stream);
        WriteData(writer, null);
    }

    /// <summary>
    /// Loads a parser that was saved with <see cref="Save"/>. Only the options that don't influence how the grammar is
    /// compiled are taken from <paramref name="options"/>: post-lexer, transformer, lexer callbacks, debug, regex
    /// options, position propagation and the tree factory.
    /// </summary>
    public static Lark Load(Stream stream, LarkOptions? options = null)
    {
        using var doc = JsonDocument.Parse(stream);
        var lark = new Lark();
        lark.LoadData(doc.RootElement, options);
        return lark;
    }

    private Lark()
    {
        Options = new LarkOptions();
        SourcePath = "<deserialized>";
    }

    private void WriteData(Utf8JsonWriter w, Dictionary<string, string>? usedFiles)
    {
        var parser = (LalrParser)_parser!.Parser;
        var ruleIndex = new Dictionary<Rule, int>();
        for (var i = 0; i < Rules.Count; i++) ruleIndex[Rules[i]] = i;

        w.WriteStartObject();
        w.WriteNumber("format", FormatVersion);

        if (usedFiles is not null)
        {
            w.WriteStartObject("usedFiles");
            foreach (var (path, hash) in usedFiles) w.WriteString(path, hash);
            w.WriteEndObject();
        }

        w.WriteStartObject("options");
        w.WriteString("parser", Options.Parser.ToString());
        w.WriteString("lexer", Options.Lexer.ToString());
        w.WriteStartArray("start");
        foreach (var s in Options.Start) w.WriteStringValue(s);
        w.WriteEndArray();
        w.WriteBoolean("debug", Options.Debug);
        w.WriteBoolean("strict", Options.Strict);
        w.WriteBoolean("keepAllTokens", Options.KeepAllTokens);
        w.WriteString("ambiguity", Options.Ambiguity.ToString());
        w.WriteString("priority", Options.Priority.ToString());
        w.WriteBoolean("maybePlaceholders", Options.MaybePlaceholders);
        w.WriteBoolean("propagatePositions", Options.PropagatePositions);
        w.WriteNumber("regexOptions", (int)Options.RegexOptions);
        w.WriteEndObject();

        w.WriteStartArray("terminals");
        foreach (var t in Terminals)
        {
            w.WriteStartObject();
            w.WriteString("name", t.Name);
            w.WriteString("type", t.Pattern.Type);
            w.WriteString("value", t.Pattern.Value);
            w.WriteString("flags", t.Pattern.Flags);
            if (t.Pattern.Raw is not null) w.WriteString("raw", t.Pattern.Raw);
            w.WriteNumber("priority", t.Priority);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("ignore");
        foreach (var i in IgnoreTokens) w.WriteStringValue(i);
        w.WriteEndArray();

        w.WriteStartArray("rules");
        foreach (var r in Rules)
        {
            w.WriteStartObject();
            w.WriteString("origin", r.Origin.Name);
            w.WriteStartArray("expansion");
            foreach (var s in r.Expansion)
            {
                w.WriteStartObject();
                w.WriteString("name", s.Name);
                w.WriteBoolean("term", s.IsTerm);
                if (s is Terminal { FilterOut: true }) w.WriteBoolean("filterOut", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("order", r.Order);
            if (r.Alias is not null) w.WriteString("alias", r.Alias);
            w.WriteBoolean("keepAllTokens", r.Options.KeepAllTokens);
            w.WriteBoolean("expand1", r.Options.Expand1);
            if (r.Options.Priority is { } p) w.WriteNumber("priority", p);
            if (r.Options.TemplateSource is not null) w.WriteString("templateSource", r.Options.TemplateSource);
            w.WriteStartArray("emptyIndices");
            foreach (var e in r.Options.EmptyIndices) w.WriteBooleanValue(e);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        var table = parser.ParseTable;
        w.WriteStartObject("parseTable");
        w.WriteStartObject("states");
        foreach (var (state, actions) in table.States)
        {
            w.WriteStartObject(state.ToString());
            foreach (var (token, action) in actions)
            {
                // Positive: shift to state; negative: reduce by rule (-index - 1).
                w.WriteNumber(token, action.IsShift ? action.State : -ruleIndex[action.Rule!] - 1);
            }
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteStartObject("startStates");
        foreach (var (k, v) in table.StartStates) w.WriteNumber(k, v);
        w.WriteEndObject();
        w.WriteStartObject("endStates");
        foreach (var (k, v) in table.EndStates) w.WriteNumber(k, v);
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteEndObject();
    }

    private void LoadData(JsonElement data, LarkOptions? userOptions)
    {
        if (data.GetProperty("format").GetInt32() != FormatVersion)
        {
            throw new InvalidDataException("Unsupported Lark data format");
        }

        var o = data.GetProperty("options");
        var options = userOptions?.Clone() ?? new LarkOptions();
        options.Parser = Enum.Parse<ParserKind>(o.GetProperty("parser").GetString()!);
        var savedLexer = Enum.Parse<LexerKind>(o.GetProperty("lexer").GetString()!);
        if (savedLexer == LexerKind.Custom && options.CustomLexer is null)
        {
            throw new ConfigurationException("The parser was saved with a custom lexer; pass the CustomLexer in the options when loading it.");
        }
        options.Lexer = savedLexer;
        options.Start = o.GetProperty("start").EnumerateArray().Select(e => e.GetString()!).ToList();
        if (userOptions is null) options.Debug = o.GetProperty("debug").GetBoolean();
        options.Strict = o.GetProperty("strict").GetBoolean();
        options.KeepAllTokens = o.GetProperty("keepAllTokens").GetBoolean();
        options.Ambiguity = Enum.Parse<Ambiguity>(o.GetProperty("ambiguity").GetString()!);
        options.Priority = Enum.Parse<PriorityMode>(o.GetProperty("priority").GetString()!);
        options.MaybePlaceholders = o.GetProperty("maybePlaceholders").GetBoolean();
        if (userOptions is null)
        {
            options.PropagatePositions = o.GetProperty("propagatePositions").GetBoolean();
            options.RegexOptions = (System.Text.RegularExpressions.RegexOptions)o.GetProperty("regexOptions").GetInt32();
        }
        options.SourcePath = "<deserialized>";
        Options = options;
        SourcePath = "<deserialized>";

        var terminals = new List<TerminalDef>();
        foreach (var t in data.GetProperty("terminals").EnumerateArray())
        {
            var value = t.GetProperty("value").GetString()!;
            var flags = t.GetProperty("flags").GetString()!;
            var raw = t.TryGetProperty("raw", out var r) ? r.GetString() : null;
            Pattern pattern = t.GetProperty("type").GetString() == "str" ? new PatternStr(value, flags, raw) : new PatternRE(value, flags, raw);
            terminals.Add(new TerminalDef(t.GetProperty("name").GetString()!, pattern, t.GetProperty("priority").GetInt32()));
        }

        var rules = new List<Rule>();
        foreach (var rule in data.GetProperty("rules").EnumerateArray())
        {
            var expansion = rule.GetProperty("expansion").EnumerateArray().Select(s =>
            {
                var name = s.GetProperty("name").GetString()!;
                return s.GetProperty("term").GetBoolean()
                    ? (Symbol)new Terminal(name, s.TryGetProperty("filterOut", out var f) && f.GetBoolean())
                    : new NonTerminal(name);
            }).ToList();
            var ruleOptions = new RuleOptions(
                rule.GetProperty("keepAllTokens").GetBoolean(),
                rule.GetProperty("expand1").GetBoolean(),
                rule.TryGetProperty("priority", out var p) ? p.GetInt32() : null,
                rule.TryGetProperty("templateSource", out var ts) ? ts.GetString() : null,
                rule.GetProperty("emptyIndices").EnumerateArray().Select(e => e.GetBoolean()).ToList());
            rules.Add(new Rule(new NonTerminal(rule.GetProperty("origin").GetString()!), expansion,
                rule.GetProperty("order").GetInt32(),
                rule.TryGetProperty("alias", out var a) ? a.GetString() : null,
                ruleOptions));
        }

        Terminals = terminals;
        Rules = rules;
        IgnoreTokens = data.GetProperty("ignore").EnumerateArray().Select(e => e.GetString()!).ToList();
        _terminalsDict = terminals.ToDictionary(t => t.Name);

        var pt = data.GetProperty("parseTable");
        var states = new Dictionary<int, Dictionary<string, LalrAction>>();
        foreach (var state in pt.GetProperty("states").EnumerateObject())
        {
            var actions = new Dictionary<string, LalrAction>();
            foreach (var action in state.Value.EnumerateObject())
            {
                var v = action.Value.GetInt32();
                actions[action.Name] = v >= 0 ? LalrAction.Shift(v) : LalrAction.Reduce(rules[-v - 1]);
            }
            states[int.Parse(state.Name)] = actions;
        }
        var startStates = pt.GetProperty("startStates").EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetInt32());
        var endStates = pt.GetProperty("endStates").EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetInt32());
        var parseTable = new ParseTable(states, startStates, endStates);

        LexerConf = CreateLexerConf(terminals, IgnoreTokens);
        LexerConf.SkipValidation = true;
        _parser = BuildParser(new LalrParser(parseTable, new ParserCallbacks(), Options.Debug));
    }

    private (string File, string Digest) GetCacheFile(string grammar)
    {
        var o = Options;
        var optionsStr = string.Join("|",
            o.Parser, o.Lexer, string.Join(",", o.Start), o.Debug, o.Strict, o.KeepAllTokens, o.Ambiguity, o.Priority,
            o.MaybePlaceholders, o.PropagatePositions, (int)o.RegexOptions, o.SourcePath,
            string.Join(",", o.ImportPaths.Select(p => p.ToString())), o.CustomLexer?.Method.ToString());
        var digest = Helpers.Sha256Digest(grammar + optionsStr + LibraryVersion + Environment.Version.Major);

        if (o.CachePath is not null) return (o.CachePath, digest);

        string username;
        try
        {
            username = Environment.UserName;
        }
        catch (Exception)
        {
            username = "unknown";
        }
        var file = Path.Combine(Path.GetTempPath(), $".lark_cache_{username}_{digest}_{Environment.Version.Major}.tmp");
        return (file, digest);
    }

    private bool TryLoadFromCache(string cacheFile, string digest)
    {
        var fs = FileSystem;
        var oldOptions = Options;
        try
        {
            using var stream = fs.OpenRead(cacheFile);
            LarkLog.Debug($"Loading grammar from cache: {cacheFile}");
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.GetProperty("digest").GetString() != digest) return false;
            var usedFiles = root.GetProperty("data").GetProperty("usedFiles").EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetString()!);
            if (!VerifyUsedFiles(usedFiles)) return false;
            LoadData(root.GetProperty("data"), oldOptions);
            return true;
        }
        catch (FileNotFoundException)
        {
            // The cache file doesn't exist; parse and compose the grammar as normal
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception e)
        {
            LarkLog.Error($"Failed to load Lark from cache: {cacheFile}. We will try to carry on. ({e.Message})");
            // The instance might have been messed up by the call to LoadData.
            Options = oldOptions;
            _parser = null;
            return false;
        }
    }

    private void SaveToCache(string cacheFile, string digest, Dictionary<string, string> usedFiles)
    {
        LarkLog.Debug($"Saving grammar to cache: {cacheFile}");
        try
        {
            using var stream = FileSystem.OpenWrite(cacheFile);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteString("digest", digest);
            writer.WritePropertyName("data");
            WriteData(writer, usedFiles);
            writer.WriteEndObject();
        }
        catch (IOException e)
        {
            LarkLog.Error($"Failed to save Lark to cache: {cacheFile}. ({e.Message})");
        }
        catch (UnauthorizedAccessException e)
        {
            LarkLog.Error($"Failed to save Lark to cache: {cacheFile}. ({e.Message})");
        }
    }

    private static bool VerifyUsedFiles(Dictionary<string, string> fileHashes)
    {
        foreach (var (path, old) in fileHashes)
        {
            string? text = null;
            if (path.StartsWith("embedded://", StringComparison.Ordinal))
            {
                // Embedded resources can't change without the assembly changing; the library version is part of the digest.
                continue;
            }
            if (File.Exists(path))
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            if (text is null) continue; // We don't know how to load the path. ignore it.

            if (Helpers.Sha256Digest(text) != old)
            {
                LarkLog.Info($"File {Helpers.Repr(path)} changed, rebuilding Parser");
                return false;
            }
        }
        return true;
    }
}
