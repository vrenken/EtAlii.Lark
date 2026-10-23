namespace EtAlii.Lark;

using EtAlii.Lark.Parsers;
using EtAlii.Lark.Utilities;

/// <summary>The parser for Lark grammars (the meta-grammar), built on the LALR parser and the basic lexer.</summary>
internal static class MetaGrammar
{
    internal static readonly (string Name, string Pattern)[] Terminals =
    {
        ("_LPAR", @"\("),
        ("_RPAR", @"\)"),
        ("_LBRA", @"\["),
        ("_RBRA", @"\]"),
        ("_LBRACE", @"\{"),
        ("_RBRACE", @"\}"),
        ("OP", "[+*]|[?](?![a-z_])"),
        ("_COLON", ":"),
        ("_COMMA", ","),
        ("_OR", @"\|"),
        ("_DOT", @"\.(?!\.)"),
        ("_DOTDOT", @"\.\."),
        ("TILDE", "~"),
        ("RULE_MODIFIERS", "(!|![?]?|[?]!?)(?=[_a-z])"),
        ("RULE", "_?[a-z][_a-z0-9]*"),
        ("TERMINAL", "_?[A-Z][_A-Z0-9]*"),
        ("STRING", @"""(\\""|\\\\|[^""\n])*?""i?"),
        ("REGEXP", @"/(?!/)(\\/|\\\\|[^/])*?/[imslux]*"),
        ("_NL", @"(\r?\n)+\s*"),
        ("_NL_OR", @"(\r?\n)+\s*\|"),
        ("WS", @"[ \t]+"),
        ("COMMENT", @"\s*//[^\n]*|\s*#[^\n]*"),
        ("BACKSLASH", @"\\[ ]*\n"),
        ("_TO", "->"),
        ("_IGNORE", "%ignore"),
        ("_OVERRIDE", "%override"),
        ("_DECLARE", "%declare"),
        ("_EXTEND", "%extend"),
        ("_IMPORT", "%import"),
        ("NUMBER", @"[+-]?\d+"),
    };

    internal static readonly (string Name, string[] Expansions)[] Rules =
    {
        ("start", new[] { "_list" }),
        ("_list", new[] { "_item", "_list _item" }),
        ("_item", new[] { "rule", "term", "ignore", "import", "declare", "override", "extend", "_NL" }),

        ("rule", new[] { "rule_modifiers RULE template_params priority _COLON expansions _NL" }),
        ("rule_modifiers", new[] { "RULE_MODIFIERS", "" }),
        ("priority", new[] { "_DOT NUMBER", "" }),
        ("template_params", new[] { "_LBRACE _template_params _RBRACE", "" }),
        ("_template_params", new[] { "RULE", "_template_params _COMMA RULE" }),
        ("expansions", new[] { "_expansions" }),
        ("_expansions", new[] { "alias", "_expansions _OR alias", "_expansions _NL_OR alias" }),

        ("?alias", new[] { "expansion _TO nonterminal", "expansion" }),
        ("expansion", new[] { "_expansion" }),

        ("_expansion", new[] { "", "_expansion expr" }),

        ("?expr", new[] { "atom", "atom OP", "atom TILDE NUMBER", "atom TILDE NUMBER _DOTDOT NUMBER" }),

        ("?atom", new[] { "_LPAR expansions _RPAR", "maybe", "value" }),

        ("value", new[] { "terminal", "nonterminal", "literal", "range", "template_usage" }),

        ("terminal", new[] { "TERMINAL" }),
        ("nonterminal", new[] { "RULE" }),

        ("?name", new[] { "RULE", "TERMINAL" }),
        ("?symbol", new[] { "terminal", "nonterminal" }),

        ("maybe", new[] { "_LBRA expansions _RBRA" }),
        ("range", new[] { "STRING _DOTDOT STRING" }),

        ("template_usage", new[] { "nonterminal _LBRACE _template_args _RBRACE" }),
        ("_template_args", new[] { "value", "_template_args _COMMA value" }),

        ("term", new[] { "TERMINAL _COLON expansions _NL", "TERMINAL _DOT NUMBER _COLON expansions _NL" }),
        ("override", new[] { "_OVERRIDE rule", "_OVERRIDE term" }),
        ("extend", new[] { "_EXTEND rule", "_EXTEND term" }),
        ("ignore", new[] { "_IGNORE expansions _NL" }),
        ("declare", new[] { "_DECLARE _declare_args _NL" }),
        ("import", new[] { "_IMPORT _import_path _NL", "_IMPORT _import_path _LPAR name_list _RPAR _NL", "_IMPORT _import_path _TO name _NL" }),

        ("_import_path", new[] { "import_lib", "import_rel" }),
        ("import_lib", new[] { "_import_args" }),
        ("import_rel", new[] { "_DOT _import_args" }),
        ("_import_args", new[] { "name", "_import_args _DOT name" }),

        ("name_list", new[] { "_name_list" }),
        ("_name_list", new[] { "name", "_name_list _COMMA name" }),

        ("_declare_args", new[] { "symbol", "_declare_args symbol" }),
        ("literal", new[] { "REGEXP", "STRING" }),
    };

    internal static readonly (string Label, string[] Examples)[] GrammarErrors =
    {
        ("Incorrect type of value", new[] { "a: 1\n" }),
        ("Unclosed parenthesis", new[] { "a: (\n" }),
        ("Unmatched closing parenthesis", new[] { "a: )\n", "a: [)\n", "a: (]\n" }),
        ("Expecting rule or terminal definition (missing colon)", new[] { "a\n", "A\n", "a->\n", "A->\n", "a A\n" }),
        ("Illegal name for rules or terminals", new[] { "Aa:\n" }),
        ("Alias expects lowercase name", new[] { "a: -> \"a\"\n" }),
        ("Unexpected colon", new[] { "a::\n", "a: b:\n", "a: B:\n", "a: \"a\":\n" }),
        ("Misplaced operator", new[] { "a: b??", "a: b(?)", "a:+\n", "a:?\n", "a:*\n", "a:|*\n" }),
        ("Expecting option (\"|\") or a new rule or terminal definition", new[] { "a:a\n()\n" }),
        ("Terminal names cannot contain dots", new[] { "A.B\n" }),
        ("Expecting rule or terminal definition", new[] { "\"a\"\n" }),
        ("%import expects a name", new[] { "%import \"a\"\n" }),
        ("%ignore expects a value", new[] { "%ignore %import\n" }),
    };

    private static readonly Lazy<ParsingFrontend> LazyParser = new(CreateParser, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static ParsingFrontend Parser => LazyParser.Value;

    private static Symbol SymbolFromStrCase(string s) => Helpers.IsUpper(s) ? new Terminal(s, s.StartsWith('_')) : new NonTerminal(s);

    private static ParsingFrontend CreateParser()
    {
        var terminals = Terminals.Select(t => new TerminalDef(t.Name, new PatternRE(t.Pattern))).ToList();

        var rules = new List<Rule>();
        foreach (var (name, expansions) in Rules)
        {
            var options = new RuleOptions(expand1: name.StartsWith('?'));
            var origin = new NonTerminal(name.TrimStart('?'));
            for (var i = 0; i < expansions.Length; i++)
            {
                var expansion = expansions[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(SymbolFromStrCase).ToList();
                rules.Add(new Rule(origin, expansion, i, null, options));
            }
        }

        var callbacks = new ParserCallbacks();
        foreach (var (rule, callback) in new ParseTreeBuilder(rules, (data, children) => new Tree(data, children)).CreateCallback())
        {
            callbacks.Rules[rule] = callback;
        }

        var lexerConf = new LexerConf(terminals, new[] { "WS", "COMMENT", "BACKSLASH" }) { LexerKind = LexerKind.Basic };
        var parserConf = new ParserConf(rules, callbacks, new[] { "start" }) { ParserKind = ParserKind.Lalr };
        return new ParsingFrontend(lexerConf, parserConf, null);
    }

    internal static object? Parse(string text, string? start = null, Func<UnexpectedInputException, bool>? onError = null) =>
        Parser.Parse(text, start, onError);

    internal static string? TranslateParserException(UnexpectedInputException e)
    {
        var error = e.MatchExamples(s => Parse(s), GrammarErrors, useAccepts: true);
        if (error is not null) return error;
        if (e is UnexpectedTokenException ut && ut.Expected.Contains("STRING")) return "Expecting a value";
        return null;
    }

    internal static Tree ParseGrammar(string text, string name, string start = "start")
    {
        Tree tree;
        try
        {
            tree = (Tree)Parse(text + "\n", start)!;
        }
        catch (UnexpectedCharactersException e)
        {
            var context = e.GetContext(text);
            throw new GrammarException($"Unexpected input at line {e.Line} column {e.Column} in {name}: \n\n{context}");
        }
        catch (UnexpectedTokenException e)
        {
            var context = e.GetContext(text);
            var error = TranslateParserException(e);
            if (error is not null)
            {
                throw new GrammarException($"{error}, at line {e.Line} column {e.Column}\n\n{context}");
            }
            throw;
        }

        return GrammarTransforms.PrepareGrammar(tree);
    }

    internal static string ErrorRepr(UnexpectedInputException error)
    {
        if (error is UnexpectedTokenException ut)
        {
            var error2 = TranslateParserException(ut);
            if (error2 is not null) return error2;
            var expected = string.Join(", ", (IEnumerable<string>?)ut.Accepts ?? ut.Expected);
            return $"Unexpected token {Helpers.Repr(ut.Token.Value)}. Expected one of: {{{expected}}}";
        }
        return error.Message;
    }

    private static (List<string> Path, InteractiveParser Parser)? SearchInteractiveParser(ImmutableInteractiveParser interactiveParser, Func<InteractiveParser, bool> predicate)
    {
        IEnumerable<(List<string>, ImmutableInteractiveParser)> Expand((List<string> Path, ImmutableInteractiveParser P) node)
        {
            foreach (var choice in node.P.Choices().Keys)
            {
                var t = new Token(choice, "");
                ImmutableInteractiveParser newP;
                try
                {
                    newP = node.P.FeedToken(t);
                }
                catch (Exception e) when (e is IParseError)
                {
                    continue; // Illegal
                }
                yield return (new List<string>(node.Path) { choice }, newP);
            }
        }

        foreach (var (path, p) in Helpers.BfsAllUnique(new[] { (new List<string>(), interactiveParser) }, Expand))
        {
            if (predicate(p)) return (path, p);
        }
        return null;
    }

    internal static List<(UnexpectedInputException Error, string Message)> FindGrammarErrors(string text, string start = "start")
    {
        var errors = new List<(UnexpectedInputException Error, string Message)>();

        bool OnError(UnexpectedInputException e)
        {
            errors.Add((e, ErrorRepr(e)));

            // recover to a new line
            var found = SearchInteractiveParser(e.InteractiveParser!.AsImmutable(), p => p.Choices().ContainsKey("_NL"));
            if (found is { } f)
            {
                foreach (var tokenType in f.Path)
                {
                    e.InteractiveParser.FeedToken(new Token(tokenType, ""));
                }
            }
            e.InteractiveParser.FeedToken(new Token("_NL", "\n"));
            return true;
        }

        Parse(text + "\n", start, OnError);

        var errorsByLine = Helpers.Classify(errors, e => e.Error.Line);
        var result = errorsByLine.Values.Select(el => el[0]).ToList(); // already sorted

        foreach (var e in result)
        {
            e.Error.InteractiveParser = null;
        }
        return result;
    }
}

/// <summary>Builds a <see cref="Grammar"/> from grammar text, resolving imports (Lark's <c>GrammarBuilder</c>).</summary>
internal sealed class GrammarBuilder
{
    private const string Ext = ".lark";

    private sealed class Definition
    {
        public Definition(bool isTerm, Tree? tree, IReadOnlyList<string> parameters, int termPriority, RuleOptions? ruleOptions)
        {
            IsTerm = isTerm;
            Tree = tree;
            Params = parameters;
            TermPriority = termPriority;
            RuleOptions = ruleOptions;
        }

        public bool IsTerm { get; }

        public Tree? Tree { get; }

        public IReadOnlyList<string> Params { get; }

        public int TermPriority { get; }

        public RuleOptions? RuleOptions { get; }
    }

    private Dictionary<string, Definition> _definitions = new();
    private readonly List<string> _ignoreNames = new();

    public GrammarBuilder(bool globalKeepAllTokens = false, IReadOnlyList<IGrammarLoader>? importPaths = null, Dictionary<string, string>? usedFiles = null)
    {
        GlobalKeepAllTokens = globalKeepAllTokens;
        ImportPaths = importPaths ?? Array.Empty<IGrammarLoader>();
        UsedFiles = usedFiles ?? new Dictionary<string, string>();
    }

    public bool GlobalKeepAllTokens { get; }

    public IReadOnlyList<IGrammarLoader> ImportPaths { get; }

    /// <summary>The files (full paths) used while loading the grammar, with their SHA256 digest.</summary>
    public Dictionary<string, string> UsedFiles { get; }

    private static string FormatGrammarError(bool isTerm, string msg, params string[] names)
    {
        var lowercaseType = isTerm ? "terminal" : "rule";
        var titleType = isTerm ? "Terminal" : "Rule";
        for (var i = 1; i <= names.Length; i++)
        {
            var postfix = i == 1 ? "" : i.ToString();
            msg = msg.Replace("{name" + postfix + "}", names[i - 1])
                .Replace("{type" + postfix + "}", lowercaseType)
                .Replace("{Type" + postfix + "}", titleType);
        }
        return msg;
    }

    private static GrammarException GrammarError(bool isTerm, string msg, params string[] names) => new(FormatGrammarError(isTerm, msg, names));

    private RuleOptions CheckRuleOptions(RuleOptions? options)
    {
        options ??= new RuleOptions();
        if (GlobalKeepAllTokens) options.KeepAllTokens = true;
        return options;
    }

    private void Define(string name, bool isTerm, Tree? exp, IReadOnlyList<string>? parameters = null, int termPriority = TerminalDef.DefaultPriority,
        RuleOptions? ruleOptions = null, bool @override = false)
    {
        if (_definitions.ContainsKey(name))
        {
            if (!@override) throw GrammarError(isTerm, "{Type} '{name}' defined more than once", name);
        }
        else if (@override)
        {
            throw GrammarError(isTerm, "Cannot override a nonexisting {type} {name}", name);
        }

        if (name.StartsWith("__", StringComparison.Ordinal))
        {
            throw GrammarError(isTerm, "Names starting with double-underscore are reserved (Error at {name})", name);
        }

        _definitions[name] = new Definition(isTerm, exp, parameters ?? Array.Empty<string>(), termPriority, isTerm ? null : CheckRuleOptions(ruleOptions));
    }

    private void Extend(string name, bool isTerm, Tree exp, IReadOnlyList<string> parameters)
    {
        if (!_definitions.TryGetValue(name, out var d))
        {
            throw GrammarError(isTerm, "Can't extend {type} {name} as it wasn't defined before", name);
        }

        if (isTerm != d.IsTerm)
        {
            throw GrammarError(isTerm, "Cannot extend {type} {name} - one is a terminal, while the other is not.", name);
        }
        if (!parameters.SequenceEqual(d.Params))
        {
            throw GrammarError(isTerm, "Cannot extend {type} with different parameters: {name}", name);
        }

        if (d.Tree is null)
        {
            throw GrammarError(isTerm, "Can't extend {type} {name} - it is abstract.", name);
        }

        var @base = d.Tree;
        if (@base.Data != "expansions") throw new InvalidOperationException("Expected an expansions tree");
        @base.Children.Insert(0, exp);
    }

    private void Ignore(Tree t)
    {
        if (t.Data == "expansions" && t.Children.Count == 1 && t.Children[0] is Tree { Data: "expansion" } t2 && t2.Children.Count == 1)
        {
            if (t2.Children[0] is Tree { Data: "value" } item && item.Children.Count == 1 && item.Children[0] is Terminal terminal)
            {
                // Keep terminal name, no need to create a new definition
                _ignoreNames.Add(terminal.Name);
                return;
            }
        }

        var name = $"__IGNORE_{_ignoreNames.Count}";
        _ignoreNames.Add(name);
        _definitions[name] = new Definition(true, t, Array.Empty<string>(), TerminalDef.DefaultPriority, null);
    }

    private static (List<string> DottedPath, string? BasePath, Dictionary<string, string> Aliases) UnpackImport(Tree stmt, string grammarName)
    {
        var pathNode = (Tree)stmt.Children[0]!;
        var arg1 = stmt.Children.Count > 1 ? stmt.Children[1] : null;

        List<string> dottedPath;
        Dictionary<string, string> aliases;
        if (arg1 is Tree nameList)
        {
            // Multi import
            dottedPath = pathNode.Children.Select(c => c!.ToString()!).ToList();
            // Can't have aliased multi import, so all aliases will be the same as names
            aliases = new Dictionary<string, string>();
            foreach (var n in nameList.Children)
            {
                aliases[n!.ToString()!] = n.ToString()!;
            }
        }
        else
        {
            // Single import
            dottedPath = pathNode.Children.Take(pathNode.Children.Count - 1).Select(c => c!.ToString()!).ToList();
            if (dottedPath.Count == 0)
            {
                throw new GrammarException($"Nothing was imported from grammar `{pathNode.Children[0]}`");
            }
            var name = (Token)pathNode.Children[^1]!; // Get name from dotted path
            aliases = new Dictionary<string, string> { [name.Value] = ((Token?)arg1 ?? name).Value }; // Aliases if exist
        }

        string? basePath;
        if (pathNode.Data == "import_lib")
        {
            // Import from library
            basePath = null;
        }
        else if (grammarName == "<string>")
        {
            // Import relative to the current directory if the grammar is given as a string
            basePath = Directory.GetCurrentDirectory();
        }
        else
        {
            // Import relative to grammar file path if external grammar file
            basePath = EmbeddedResourceGrammarLoader.GetDirectory(grammarName);
        }

        return (dottedPath, basePath, aliases);
    }

    private static Tree? MangleDefinitionTree(Tree? exp, Func<string, string>? mangle)
    {
        if (mangle is null || exp is null) return exp;
        exp = GrammarTransforms.DeepCopy(exp);
        foreach (var t in exp.IterSubtrees().ToList())
        {
            for (var i = 0; i < t.Children.Count; i++)
            {
                if (t.Children[i] is Symbol s) t.Children[i] = s.Renamed(mangle);
            }
        }
        return exp;
    }

    private sealed record UnpackedDefinition(string Name, bool IsTerm, Tree Exp, IReadOnlyList<string> Params, int TermPriority, RuleOptions? RuleOptions);

    private static UnpackedDefinition UnpackDefinition(Tree tree, Func<string, string>? mangle)
    {
        string name;
        IReadOnlyList<string> parameters;
        Tree exp;
        bool isTerm;
        var termPriority = TerminalDef.DefaultPriority;
        RuleOptions? ruleOptions = null;

        if (tree.Data == "rule")
        {
            var modifiersTree = (Tree)tree.Children[0]!;
            name = ((Token)tree.Children[1]!).Value;
            var paramsTree = (Tree)tree.Children[2]!;
            var priorityTree = (Tree)tree.Children[3]!;
            exp = (Tree)tree.Children[4]!;

            var keepAllTokens = false;
            var expand1 = false;
            if (modifiersTree.Children.Count > 0)
            {
                var m = modifiersTree.Children.Single()!.ToString()!;
                expand1 = m.Contains('?');
                if (expand1 && name.StartsWith('_'))
                {
                    throw new GrammarException("Inlined rules (_rule) cannot use the ?rule modifier.");
                }
                keepAllTokens = m.Contains('!');
            }

            int? priority = priorityTree.Children.Count > 0 ? ParsePriority(priorityTree.Children.Single()!.ToString()!) : null;
            parameters = paramsTree.Children.Select(t => ((Token)t!).Value).ToList();
            ruleOptions = new RuleOptions(keepAllTokens, expand1, priority, parameters.Count > 0 ? name : null);
            isTerm = false;
        }
        else
        {
            name = ((Token)tree.Children[0]!).Value;
            parameters = Array.Empty<string>(); // TODO terminal templates
            if (tree.Children.Count == 3) termPriority = ParsePriority(tree.Children[1]!.ToString()!);
            exp = (Tree)tree.Children[^1]!;
            isTerm = true;
        }

        if (mangle is not null)
        {
            parameters = parameters.Select(mangle).ToList();
            name = mangle(name);
        }

        exp = MangleDefinitionTree(exp, mangle)!;
        return new UnpackedDefinition(name, isTerm, exp, parameters, termPriority, ruleOptions);
    }

    private const int MaxPriority = 1_000_000_000;

    /// <summary>
    /// Parses a priority. Python has arbitrary precision integers; here huge values are clamped to ±1e9, which leaves
    /// room for summing and negating priorities without overflowing.
    /// </summary>
    private static int ParsePriority(string s)
    {
        var value = System.Numerics.BigInteger.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if (value > MaxPriority) return MaxPriority;
        if (value < -MaxPriority) return -MaxPriority;
        return (int)value;
    }

    private void DefineUnpacked(UnpackedDefinition d, bool @override = false) =>
        Define(d.Name, d.IsTerm, d.Exp, d.Params, d.TermPriority, d.RuleOptions, @override);

    /// <summary>Parses the grammar text and adds its definitions (and the definitions it imports).</summary>
    public void LoadGrammar(string grammarText, string grammarName = "<?>", Func<string, string>? mangle = null)
    {
        var tree = MetaGrammar.ParseGrammar(grammarText, grammarName);

        var imports = new Dictionary<string, (List<string> DottedPath, string? BasePath, Dictionary<string, string> Aliases)>();
        foreach (var stmt in tree.Children.Cast<Tree>())
        {
            if (stmt.Data != "import") continue;
            var (dottedPath, basePath, aliases) = UnpackImport(stmt, grammarName);
            var key = string.Join(".", dottedPath);
            if (imports.TryGetValue(key, out var existing))
            {
                if (existing.BasePath != basePath) throw new InvalidOperationException($"Inconsistent base_path for {key}.");
                foreach (var (k, v) in aliases) existing.Aliases[k] = v;
            }
            else
            {
                imports[key] = (dottedPath, basePath, aliases);
            }
        }

        foreach (var (dottedPath, basePath, aliases) in imports.Values)
        {
            DoImport(dottedPath, basePath, aliases, mangle);
        }

        foreach (var stmt in tree.Children.Cast<Tree>())
        {
            switch (stmt.Data)
            {
                case "term":
                case "rule":
                    DefineUnpacked(UnpackDefinition(stmt, mangle));
                    break;
                case "override":
                    DefineUnpacked(UnpackDefinition((Tree)stmt.Children.Single()!, mangle), @override: true);
                    break;
                case "extend":
                {
                    var d = UnpackDefinition((Tree)stmt.Children.Single()!, mangle);
                    Extend(d.Name, d.IsTerm, d.Exp, d.Params);
                    break;
                }
                case "ignore":
                    // if mangle is not null, we shouldn't apply ignore, since we aren't in a toplevel grammar
                    if (mangle is null) Ignore((Tree)stmt.Children.Single()!);
                    break;
                case "declare":
                    foreach (var child in stmt.Children)
                    {
                        if (child is not Symbol symbol) throw new InvalidOperationException($"Unexpected {child}");
                        var isTerm = symbol is Terminal;
                        var name = mangle is null ? symbol.Name : mangle(symbol.Name);
                        Define(name, isTerm, null);
                    }
                    break;
                case "import":
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected statement {stmt.Data}");
            }
        }

        var termDefs = _definitions.Where(kv => kv.Value.IsTerm).ToDictionary(kv => kv.Key, kv => kv.Value.Tree);
        ResolveTermReferences(termDefs);
    }

    private static void ResolveTermReferences(Dictionary<string, Tree?> termDict)
    {
        // TODO Solve with transitive closure (maybe)
        while (true)
        {
            var changed = false;
            foreach (var (name, tokenTree) in termDict)
            {
                if (tokenTree is null) continue; // Terminal added through %declare
                foreach (var exp in tokenTree.FindData("value").ToList())
                {
                    var item = exp.Children.Single();
                    if (item is NonTerminal)
                    {
                        throw new GrammarException($"Rules aren't allowed inside terminals ({item} in {name})");
                    }
                    if (item is Terminal terminal)
                    {
                        if (!termDict.TryGetValue(terminal.Name, out var termValue))
                        {
                            throw new GrammarException($"Terminal used but not defined: {terminal.Name}");
                        }
                        exp.Children[0] = termValue ?? throw new GrammarException($"Terminal used but not defined: {terminal.Name}");
                        changed = true;
                    }
                }
            }
            if (!changed) break;
        }

        foreach (var (name, term) in termDict)
        {
            if (term is null) continue; // Just declared
            foreach (var child in term.Children)
            {
                if (child is Tree ct && ct.IterSubtrees().Any(x => ReferenceEquals(x, term)))
                {
                    throw new GrammarException($"Recursion in terminal '{name}' (recursion is only allowed in rules, not terminals)");
                }
            }
        }
    }

    private static HashSet<string> FindUsedSymbols(Tree tree)
    {
        if (tree.Data != "expansions") throw new InvalidOperationException("Expected an expansions tree");
        return tree.FindData("expansion")
            .SelectMany(x => x.ScanValues(t => t is Symbol).Cast<Symbol>())
            .Select(s => s.Name)
            .ToHashSet();
    }

    private void RemoveUnused(IEnumerable<string> used)
    {
        IEnumerable<string> RuleDependencies(string symbol)
        {
            if (!_definitions.TryGetValue(symbol, out var d)) return Array.Empty<string>();
            if (d.IsTerm) return Array.Empty<string>();
            var deps = FindUsedSymbols(d.Tree!);
            deps.ExceptWith(d.Params);
            return deps;
        }

        var usedSet = Helpers.Bfs(used, RuleDependencies).ToHashSet();
        _definitions = _definitions.Where(kv => usedSet.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static Func<string, string> GetMangle(string prefix, Dictionary<string, string> aliases, Func<string, string>? baseMangle)
    {
        return s =>
        {
            if (aliases.TryGetValue(s, out var alias))
            {
                s = alias;
            }
            else if (s[0] == '_')
            {
                s = $"_{prefix}__{s[1..]}";
            }
            else
            {
                s = $"{prefix}__{s}";
            }
            if (baseMangle is not null) s = baseMangle(s);
            return s;
        };
    }

    public void DoImport(IReadOnlyList<string> dottedPath, string? basePath, Dictionary<string, string> aliases, Func<string, string>? baseMangle = null)
    {
        if (dottedPath.Count == 0) throw new ArgumentException("Empty import path", nameof(dottedPath));
        var mangle = GetMangle(string.Join("__", dottedPath), aliases, baseMangle);
        var grammarPath = string.Join("/", dottedPath) + Ext;

        var toTry = new List<IGrammarLoader>(ImportPaths);
        if (basePath is not null) toTry.Add(new FileSystemGrammarLoader(basePath));
        toTry.Add(EmbeddedResourceGrammarLoader.StandardLibrary);

        foreach (var source in toTry)
        {
            if (!source.TryLoad(basePath, grammarPath, out var joinedPath, out var text)) continue;

            var h = Helpers.Sha256Digest(text);
            if (UsedFiles.TryGetValue(joinedPath, out var existing) && existing != h)
            {
                throw new InvalidOperationException("Grammar file was changed during importing");
            }
            UsedFiles[joinedPath] = h;

            var gb = new GrammarBuilder(GlobalKeepAllTokens, ImportPaths, UsedFiles);
            gb.LoadGrammar(text, joinedPath, mangle);
            gb.RemoveUnused(aliases.Keys.Select(mangle));
            foreach (var name in gb._definitions.Keys)
            {
                if (_definitions.ContainsKey(name))
                {
                    throw new GrammarException($"Cannot import '{name}' from '{grammarPath}': Symbol already defined.");
                }
            }

            foreach (var (k, v) in gb._definitions) _definitions[k] = v;
            return;
        }

        throw new FileNotFoundException($"Cannot find grammar '{grammarPath}' in the import paths", grammarPath);
    }

    public void Validate()
    {
        foreach (var (name, d) in _definitions)
        {
            var parameters = d.Params;
            var exp = d.Tree;

            for (var i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                if (_definitions.ContainsKey(p))
                {
                    throw new GrammarException($"Template Parameter conflicts with rule {p} (in template {name})");
                }
                if (parameters.Take(i).Contains(p))
                {
                    throw new GrammarException($"Duplicate Template Parameter {p} (in template {name})");
                }
            }

            if (exp is null) continue; // Remaining checks don't apply to abstract rules/terminals (created with %declare)

            foreach (var temp in exp.FindData("template_usage"))
            {
                var sym = ((Symbol)temp.Children[0]!).Name;
                var argCount = temp.Children.Count - 1;
                if (!parameters.Contains(sym))
                {
                    if (!_definitions.TryGetValue(sym, out var symDef))
                    {
                        throw GrammarError(d.IsTerm, $"Template '{sym}' used but not defined (in {{type}} {{name}})", name);
                    }
                    if (argCount != symDef.Params.Count)
                    {
                        throw GrammarError(d.IsTerm, $"Wrong number of template arguments used for {{name}} (expected {symDef.Params.Count}, got {argCount}) (in {{type2}} {{name2}})", sym, name);
                    }
                }
            }

            foreach (var sym in FindUsedSymbols(exp))
            {
                if (!_definitions.ContainsKey(sym) && !parameters.Contains(sym))
                {
                    throw GrammarError(d.IsTerm, "{Type} '{name}' used but not defined (in {type2} {name2})", sym, name);
                }
            }
        }

        var missing = _ignoreNames.Where(n => !_definitions.ContainsKey(n)).ToList();
        if (missing.Count > 0)
        {
            throw new GrammarException("Terminals {" + string.Join(", ", missing.Select(Helpers.Repr)) + "} were marked to ignore but were not defined!");
        }
    }

    public Grammar Build()
    {
        Validate();
        var ruleDefs = new List<RuleDef>();
        var termDefs = new List<TermDef>();
        foreach (var (name, d) in _definitions)
        {
            if (d.IsTerm)
            {
                termDefs.Add(new TermDef(name, d.Tree, d.TermPriority));
            }
            else
            {
                ruleDefs.Add(new RuleDef(name, d.Params, d.Tree, d.RuleOptions));
            }
        }
        return new Grammar(ruleDefs, termDefs, new List<string>(_ignoreNames));
    }

    /// <summary>Loads and builds a grammar. Returns the grammar and the files it used.</summary>
    public static (Grammar Grammar, Dictionary<string, string> UsedFiles) Load(string grammar, string source, IReadOnlyList<IGrammarLoader>? importPaths, bool globalKeepAllTokens)
    {
        var builder = new GrammarBuilder(globalKeepAllTokens, importPaths);
        builder.LoadGrammar(grammar, source);
        return (builder.Build(), builder.UsedFiles);
    }

    /// <summary>Returns the paths of the grammars imported by the given grammar (recursively).</summary>
    public static List<string> ListGrammarImports(string grammar, IReadOnlyList<IGrammarLoader>? importPaths = null)
    {
        var builder = new GrammarBuilder(false, importPaths);
        builder.LoadGrammar(grammar, "<string>");
        return builder.UsedFiles.Keys.ToList();
    }
}
