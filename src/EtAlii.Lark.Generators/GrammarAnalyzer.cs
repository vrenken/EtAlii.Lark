using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EtAlii.Lark.Generators
{
    /// <summary>A grammar syntax error found by the <see cref="GrammarAnalyzer"/>.</summary>
    internal sealed class GrammarSyntaxException : Exception
    {
        public GrammarSyntaxException(string message, int line, int column) : base(message)
        {
            Line = line;
            Column = column;
        }

        public int Line { get; }

        public int Column { get; }
    }

    internal enum ItemKind
    {
        Rule,
        Terminal,
        String,
        Regexp,
        Complex,
    }

    /// <summary>
    /// One item of an alternative. Complex items (groups, operators, ranges, templates) have no fixed shape as node
    /// properties; their <see cref="Expression"/> describes their structure for the typed visitor.
    /// </summary>
    internal sealed class GrammarItem
    {
        public GrammarItem(ItemKind kind, string name = "", GrammarExpression? expression = null)
        {
            Kind = kind;
            Name = name;
            Expression = expression ?? new SymbolExpression(kind, name);
        }

        public ItemKind Kind { get; }

        public string Name { get; }

        public GrammarExpression Expression { get; }
    }

    /// <summary>The structure of an item of an alternative.</summary>
    internal abstract class GrammarExpression
    {
    }

    /// <summary>A rule, terminal, string or regular expression.</summary>
    internal sealed class SymbolExpression : GrammarExpression
    {
        public SymbolExpression(ItemKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public ItemKind Kind { get; }

        public string Name { get; }
    }

    /// <summary>A group <c>( a | b )</c>, or an optional group <c>[ a | b ]</c>.</summary>
    internal sealed class GroupExpression : GrammarExpression
    {
        public GroupExpression(List<Alternative> alternatives, bool isMaybe)
        {
            Alternatives = alternatives;
            IsMaybe = isMaybe;
        }

        public List<Alternative> Alternatives { get; }

        /// <summary>True for <c>[ ... ]</c>, which produces placeholders when <c>maybe_placeholders</c> is set.</summary>
        public bool IsMaybe { get; }
    }

    /// <summary>An expression with an operator: <c>?</c>, <c>*</c>, <c>+</c> or a <c>~</c> range.</summary>
    internal sealed class OperatorExpression : GrammarExpression
    {
        public OperatorExpression(GrammarExpression operand, string op)
        {
            Operand = operand;
            Operator = op;
        }

        public GrammarExpression Operand { get; }

        /// <summary><c>?</c>, <c>*</c>, <c>+</c> or <c>~</c>.</summary>
        public string Operator { get; }
    }

    /// <summary>An expression whose trees the generator doesn't model, such as a template or a string range.</summary>
    internal sealed class UnsupportedExpression : GrammarExpression
    {
        public static readonly UnsupportedExpression Instance = new();
    }

    internal sealed class Alternative
    {
        public List<GrammarItem> Items { get; } = new();

        public string? Alias { get; set; }
    }

    internal sealed class RuleDefinition
    {
        public RuleDefinition(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public bool Expand1 { get; set; }

        public bool KeepAllTokens { get; set; }

        public bool IsTemplate { get; set; }

        /// <summary>Imported rules have an unknown shape.</summary>
        public bool IsImported { get; set; }

        public List<Alternative> Alternatives { get; } = new();
    }

    internal enum ChildKind
    {
        /// <summary>The child is always a tree of a known rule.</summary>
        Node,

        /// <summary>The child is always a token.</summary>
        Token,

        /// <summary>The child may be a tree or a token (e.g. an inlined <c>?rule</c>).</summary>
        Value,
    }

    internal sealed class ChildModel
    {
        public ChildModel(string propertyName, ChildKind kind, string? nodeName, int index)
        {
            PropertyName = propertyName;
            Kind = kind;
            NodeName = nodeName;
            Index = index;
        }

        public string PropertyName { get; }

        public ChildKind Kind { get; }

        public string? NodeName { get; }

        public int Index { get; }
    }

    /// <summary>A tree data name (a rule or an alias), with its children when they have a fixed shape.</summary>
    internal sealed class NodeModel
    {
        public NodeModel(string name, List<ChildModel>? shape, bool isSolid)
        {
            Name = name;
            Shape = shape;
            IsSolid = isSolid;
        }

        public string Name { get; }

        public List<ChildModel>? Shape { get; }

        /// <summary>True when parsing the rule always produces a tree with this name (no aliases, never inlined).</summary>
        public bool IsSolid { get; }
    }

    internal sealed class GrammarModel
    {
        public List<string> RuleNames { get; } = new();

        public List<string> TerminalNames { get; } = new();

        public List<NodeModel> Nodes { get; } = new();

        /// <summary>The children of each node as the typed visitor sees them; null for nodes without a fixed shape.</summary>
        public Dictionary<string, List<ShapeElement>?> TypedShapes { get; } = new();

        /// <summary>What each rule can be as the child of a tree.</summary>
        public Dictionary<string, SortedSet<ShapeMember>> RuleMembers { get; } = new();

        /// <summary>The <c>?rule</c> rules whose alternatives all have one child, so that they never create trees of their own.</summary>
        public HashSet<string> NeverTrees { get; } = new();

        /// <summary>The <c>?rule</c> and <c>_rule</c> rules, whose trees are inlined into their parents.</summary>
        public HashSet<string> InlinedRules { get; } = new();

        public NodeModel? FindNode(string name) => Nodes.FirstOrDefault(n => n.Name == name);
    }

    /// <summary>
    /// A lightweight parser of the Lark grammar syntax. It finds the rules, terminals and aliases of a grammar, and the
    /// shape of the trees of simple rules; the full grammar is compiled at runtime by the Lark library.
    /// </summary>
    internal static class GrammarAnalyzer
    {
        public static GrammarModel Analyze(string grammar, bool keepAllTokens, bool maybePlaceholders = true)
        {
            var tokens = new Tokenizer(grammar).Tokenize();
            var definitions = new Parser(tokens).Parse();
            return BuildModel(definitions, keepAllTokens, maybePlaceholders);
        }

        private static GrammarModel BuildModel(Definitions definitions, bool keepAllTokens, bool maybePlaceholders)
        {
            var model = new GrammarModel();
            var rules = definitions.Rules;

            foreach (var rule in rules.Values.Where(r => !r.IsTemplate))
            {
                model.RuleNames.Add(rule.Name);
            }
            model.TerminalNames.AddRange(definitions.Terminals);

            // A tree with the data of a rule is created by its alternatives without alias; an alias names the trees of its alternative.
            var sources = new Dictionary<string, List<(RuleDefinition Rule, Alternative? Alternative)>>();
            void AddSource(string name, RuleDefinition rule, Alternative? alternative)
            {
                if (!sources.TryGetValue(name, out var list)) sources[name] = list = new List<(RuleDefinition, Alternative?)>();
                list.Add((rule, alternative));
            }

            foreach (var rule in rules.Values)
            {
                if (rule.IsTemplate || rule.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (rule.IsImported)
                {
                    AddSource(rule.Name, rule, null);
                    continue;
                }
                foreach (var alternative in rule.Alternatives)
                {
                    AddSource(alternative.Alias ?? rule.Name, rule, alternative);
                }
            }

            foreach (var pair in sources)
            {
                var name = pair.Key;
                var rule = rules.TryGetValue(name, out var r) ? r : null;
                var isSolid = rule is { IsImported: false, Expand1: false, IsTemplate: false } && rule.Alternatives.All(a => a.Alias is null) &&
                              !rule.Name.StartsWith("_", StringComparison.Ordinal);

                List<ChildModel>? shape = null;
                if (pair.Value.Count == 1 && pair.Value[0].Alternative is { } alternative)
                {
                    shape = ShapeOf(pair.Value[0].Rule, alternative, rules, keepAllTokens);
                }
                model.Nodes.Add(new NodeModel(name, shape, isSolid));
            }

            foreach (var rule in rules.Values)
            {
                if (rule.Expand1 || rule.Name.StartsWith("_", StringComparison.Ordinal)) model.InlinedRules.Add(rule.Name);
            }
            var shapes = new ShapeAnalyzer(rules, keepAllTokens, maybePlaceholders);
            foreach (var rule in rules.Keys)
            {
                model.RuleMembers[rule] = shapes.MembersOf(rule);
            }
            foreach (var pair in sources)
            {
                model.TypedShapes[pair.Key] = shapes.ShapeOf(pair.Key, pair.Value, out var neverTree);
                if (neverTree) model.NeverTrees.Add(pair.Key);
            }

            return model;
        }

        private static List<ChildModel>? ShapeOf(RuleDefinition rule, Alternative alternative, Dictionary<string, RuleDefinition> rules, bool keepAllTokens)
        {
            var keepAll = keepAllTokens || rule.KeepAllTokens;
            var children = new List<(string Name, ChildKind Kind, string? NodeName)>();
            foreach (var item in alternative.Items)
            {
                switch (item.Kind)
                {
                    case ItemKind.Rule:
                        if (item.Name.StartsWith("_", StringComparison.Ordinal)) return null; // inlined
                        if (!rules.TryGetValue(item.Name, out var target) || target.IsTemplate) return null;
                        var solid = !target.IsImported && !target.Expand1 && target.Alternatives.All(a => a.Alias is null);
                        children.Add((ToPascalCase(item.Name), solid ? ChildKind.Node : ChildKind.Value, solid ? item.Name : null));
                        break;
                    case ItemKind.Terminal:
                        if (item.Name.StartsWith("_", StringComparison.Ordinal) && !keepAll) break; // filtered out
                        children.Add((ToPascalCase(item.Name), ChildKind.Token, null));
                        break;
                    case ItemKind.String:
                        if (keepAll) children.Add(("Token", ChildKind.Token, null));
                        break;
                    case ItemKind.Regexp:
                        children.Add(("Token", ChildKind.Token, null));
                        break;
                    default:
                        return null;
                }
            }

            // Number the properties whose names occur more than once.
            var counts = children.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Count());
            var seen = new Dictionary<string, int>();
            var shape = new List<ChildModel>();
            for (var i = 0; i < children.Count; i++)
            {
                var (name, kind, nodeName) = children[i];
                if (counts[name] > 1)
                {
                    seen.TryGetValue(name, out var n);
                    seen[name] = ++n;
                    name += n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                shape.Add(new ChildModel(name, kind, nodeName, i));
            }
            return shape;
        }

        /// <summary>Converts a rule or terminal name (<c>add_expr</c>, <c>SIGNED_NUMBER</c>) to PascalCase (<c>AddExpr</c>, <c>SignedNumber</c>).</summary>
        public static string ToPascalCase(string name)
        {
            var sb = new StringBuilder();
            var upper = true;
            foreach (var c in name)
            {
                if (c == '_')
                {
                    upper = true;
                    continue;
                }
                sb.Append(upper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                upper = false;
            }
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        private sealed class Definitions
        {
            // Ordered by first definition.
            public Dictionary<string, RuleDefinition> Rules { get; } = new();

            public List<string> Terminals { get; } = new();

            public void AddTerminal(string name)
            {
                if (!Terminals.Contains(name)) Terminals.Add(name);
            }
        }

        private enum T
        {
            Rule,
            Terminal,
            Modifiers,
            String,
            Regexp,
            Number,
            Op,
            Colon,
            Comma,
            Or,
            Dot,
            DotDot,
            Tilde,
            To,
            LPar,
            RPar,
            LBra,
            RBra,
            LBrace,
            RBrace,
            Directive,
            Newline,
            End,
        }

        private readonly struct Tok
        {
            public Tok(T type, string value, int line, int column)
            {
                Type = type;
                Value = value;
                Line = line;
                Column = column;
            }

            public T Type { get; }

            public string Value { get; }

            public int Line { get; }

            public int Column { get; }
        }

        private sealed class Tokenizer
        {
            private readonly string _text;
            private readonly List<Tok> _tokens = new();
            private int _pos;
            private int _line = 1;
            private int _lineStart;

            public Tokenizer(string text)
            {
                _text = text;
            }

            private int Column => _pos - _lineStart + 1;

            private char Peek(int offset = 0) => _pos + offset < _text.Length ? _text[_pos + offset] : '\0';

            private void Add(T type, int start, int line, int column) => _tokens.Add(new Tok(type, _text.Substring(start, _pos - start), line, column));

            private void Advance(int count = 1)
            {
                for (var i = 0; i < count && _pos < _text.Length; i++)
                {
                    if (_text[_pos] == '\n')
                    {
                        _line++;
                        _lineStart = _pos + 1;
                    }
                    _pos++;
                }
            }

            private GrammarSyntaxException Error(string message) => new(message, _line, Column);

            public List<Tok> Tokenize()
            {
                while (_pos < _text.Length)
                {
                    var c = Peek();
                    var start = _pos;
                    var line = _line;
                    var column = Column;

                    if (c == '\\' && IsLineContinuation())
                    {
                        while (Peek() != '\n') Advance();
                        Advance();
                    }
                    else if (c == ' ' || c == '\t' || c == '\f')
                    {
                        Advance();
                    }
                    else if ((c == '/' && Peek(1) == '/') || c == '#')
                    {
                        while (_pos < _text.Length && Peek() != '\n') Advance();
                    }
                    else if (c == '\r' || c == '\n')
                    {
                        // Newlines, whitespace and comment lines. A line that starts with '|' continues the definition.
                        while (true)
                        {
                            var p = Peek();
                            if (char.IsWhiteSpace(p)) Advance();
                            else if ((p == '/' && Peek(1) == '/') || p == '#') { while (_pos < _text.Length && Peek() != '\n') Advance(); }
                            else break;
                        }
                        if (Peek() == '|')
                        {
                            Advance();
                            _tokens.Add(new Tok(T.Or, "|", _line, Column - 1));
                        }
                        else
                        {
                            _tokens.Add(new Tok(T.Newline, "\n", line, column));
                        }
                    }
                    else if (c == '-' && Peek(1) == '>')
                    {
                        Advance(2);
                        Add(T.To, start, line, column);
                    }
                    else if (c == '%')
                    {
                        Advance();
                        while (char.IsLetter(Peek())) Advance();
                        Add(T.Directive, start, line, column);
                    }
                    else if (c == '"')
                    {
                        Advance();
                        while (true)
                        {
                            var p = Peek();
                            if (p == '\0' || p == '\n') throw Error("Unterminated string");
                            if (p == '\\') { Advance(2); continue; }
                            Advance();
                            if (p == '"') break;
                        }
                        if (Peek() == 'i') Advance();
                        Add(T.String, start, line, column);
                    }
                    else if (c == '/')
                    {
                        Advance();
                        while (true)
                        {
                            var p = Peek();
                            if (p == '\0') throw Error("Unterminated regular expression");
                            if (p == '\\') { Advance(2); continue; }
                            Advance();
                            if (p == '/') break;
                        }
                        while ("imslux".IndexOf(Peek()) >= 0 && Peek() != '\0') Advance();
                        Add(T.Regexp, start, line, column);
                    }
                    else if (c == '.' && Peek(1) == '.')
                    {
                        Advance(2);
                        Add(T.DotDot, start, line, column);
                    }
                    else if (c == '.')
                    {
                        Advance();
                        Add(T.Dot, start, line, column);
                    }
                    else if (char.IsDigit(c) || ((c == '+' || c == '-') && char.IsDigit(Peek(1))))
                    {
                        Advance();
                        while (char.IsDigit(Peek())) Advance();
                        Add(T.Number, start, line, column);
                    }
                    else if (c == '+' || c == '*' || (c == '?' && !IsRuleStart(Peek(1))))
                    {
                        Advance();
                        Add(T.Op, start, line, column);
                    }
                    else if ((c == '!' || c == '?') && IsModifierStart())
                    {
                        while (Peek() == '!' || Peek() == '?') Advance();
                        Add(T.Modifiers, start, line, column);
                    }
                    else if (c == '_' || char.IsLetter(c))
                    {
                        if (c == '_') Advance();
                        var isRule = char.IsLower(Peek());
                        if (!char.IsLetter(Peek())) throw Error($"Unexpected character '{c}'");
                        while (Peek() == '_' || char.IsLetterOrDigit(Peek())) Advance();
                        Add(isRule ? T.Rule : T.Terminal, start, line, column);
                    }
                    else
                    {
                        var type = c switch
                        {
                            ':' => T.Colon,
                            ',' => T.Comma,
                            '|' => T.Or,
                            '~' => T.Tilde,
                            '(' => T.LPar,
                            ')' => T.RPar,
                            '[' => T.LBra,
                            ']' => T.RBra,
                            '{' => T.LBrace,
                            '}' => T.RBrace,
                            _ => throw Error($"Unexpected character '{c}'"),
                        };
                        Advance();
                        Add(type, start, line, column);
                    }
                }

                _tokens.Add(new Tok(T.Newline, "\n", _line, Column));
                _tokens.Add(new Tok(T.End, "", _line, Column));
                return _tokens;
            }

            private static bool IsRuleStart(char c) => c == '_' || (c >= 'a' && c <= 'z');

            private bool IsModifierStart()
            {
                var i = 0;
                while (Peek(i) == '!' || Peek(i) == '?') i++;
                return i <= 2 && IsRuleStart(Peek(i));
            }

            private bool IsLineContinuation()
            {
                var i = 1;
                while (Peek(i) == ' ') i++;
                return Peek(i) == '\n' || (Peek(i) == '\r' && Peek(i + 1) == '\n');
            }
        }

        private sealed class Parser
        {
            private readonly List<Tok> _tokens;
            private readonly Definitions _definitions = new();
            private int _pos;

            public Parser(List<Tok> tokens)
            {
                _tokens = tokens;
            }

            private Tok Current => _tokens[_pos];

            private Tok Next() => _tokens[_pos++];

            private bool At(T type) => Current.Type == type;

            private bool Accept(T type)
            {
                if (!At(type)) return false;
                _pos++;
                return true;
            }

            private Tok Expect(T type)
            {
                if (!At(type)) throw Error($"Expected {type}, found '{Current.Value}'");
                return Next();
            }

            private GrammarSyntaxException Error(string message) => new(message, Current.Line, Current.Column);

            public Definitions Parse()
            {
                while (!At(T.End))
                {
                    if (Accept(T.Newline)) continue;

                    if (At(T.Directive))
                    {
                        ParseDirective();
                    }
                    else
                    {
                        ParseDefinition(DefinitionMode.Define);
                    }
                }
                return _definitions;
            }

            private enum DefinitionMode
            {
                Define,
                Override,
                Extend,
            }

            private void ParseDirective()
            {
                var directive = Next();
                switch (directive.Value)
                {
                    case "%ignore":
                        ParseExpansions();
                        Expect(T.Newline);
                        break;
                    case "%import":
                        ParseImport();
                        break;
                    case "%declare":
                        while (At(T.Terminal) || At(T.Rule))
                        {
                            var name = Next();
                            if (name.Type == T.Terminal) _definitions.AddTerminal(name.Value);
                        }
                        Expect(T.Newline);
                        break;
                    case "%override":
                        ParseDefinition(DefinitionMode.Override);
                        break;
                    case "%extend":
                        ParseDefinition(DefinitionMode.Extend);
                        break;
                    default:
                        throw new GrammarSyntaxException($"Unknown directive '{directive.Value}'", directive.Line, directive.Column);
                }
            }

            private void ParseImport()
            {
                Accept(T.Dot);
                var path = new List<string> { ParseName() };
                while (Accept(T.Dot)) path.Add(ParseName());

                if (Accept(T.LPar))
                {
                    do
                    {
                        AddImported(ParseName());
                    }
                    while (Accept(T.Comma));
                    Expect(T.RPar);
                }
                else
                {
                    var name = path[path.Count - 1];
                    if (Accept(T.To)) name = ParseName();
                    AddImported(name);
                }
                Expect(T.Newline);
            }

            private string ParseName()
            {
                if (At(T.Rule) || At(T.Terminal)) return Next().Value;
                throw Error($"Expected a name, found '{Current.Value}'");
            }

            private void AddImported(string name)
            {
                if (IsTerminalName(name))
                {
                    _definitions.AddTerminal(name);
                }
                else if (!_definitions.Rules.ContainsKey(name))
                {
                    _definitions.Rules[name] = new RuleDefinition(name) { IsImported = true };
                }
            }

            private static bool IsTerminalName(string name) => name.TrimStart('_').Length > 0 && char.IsUpper(name.TrimStart('_')[0]);

            private void ParseDefinition(DefinitionMode mode)
            {
                var modifiers = Accept(T.Modifiers) ? _tokens[_pos - 1].Value : "";
                var nameToken = Current;
                var name = ParseName();
                var isTemplate = false;
                if (Accept(T.LBrace))
                {
                    isTemplate = true;
                    do
                    {
                        ParseName();
                    }
                    while (Accept(T.Comma));
                    Expect(T.RBrace);
                }
                if (Accept(T.Dot)) Expect(T.Number);
                Expect(T.Colon);
                var alternatives = ParseExpansions();
                Expect(T.Newline);

                if (IsTerminalName(name))
                {
                    if (nameToken.Type != T.Terminal || modifiers.Length > 0) throw new GrammarSyntaxException($"Invalid terminal name '{name}'", nameToken.Line, nameToken.Column);
                    if (!isTemplate) _definitions.AddTerminal(name);
                    return;
                }

                if (mode == DefinitionMode.Extend && _definitions.Rules.TryGetValue(name, out var existing))
                {
                    existing.Alternatives.AddRange(alternatives);
                    return;
                }

                var rule = new RuleDefinition(name)
                {
                    Expand1 = modifiers.Contains("?"),
                    KeepAllTokens = modifiers.Contains("!"),
                    IsTemplate = isTemplate,
                    IsImported = mode == DefinitionMode.Extend,
                };
                rule.Alternatives.AddRange(alternatives);
                _definitions.Rules[name] = rule;
            }

            private List<Alternative> ParseExpansions()
            {
                var alternatives = new List<Alternative> { ParseAlternative() };
                while (Accept(T.Or)) alternatives.Add(ParseAlternative());
                return alternatives;
            }

            private Alternative ParseAlternative()
            {
                var alternative = new Alternative();
                while (true)
                {
                    var item = TryParseExpr();
                    if (item is null) break;
                    alternative.Items.Add(item);
                }
                if (Accept(T.To)) alternative.Alias = Expect(T.Rule).Value;
                return alternative;
            }

            private GrammarItem? TryParseExpr()
            {
                var atom = TryParseAtom();
                if (atom is null) return null;

                if (Accept(T.Op)) return new GrammarItem(ItemKind.Complex, expression: new OperatorExpression(atom.Expression, _tokens[_pos - 1].Value));
                if (Accept(T.Tilde))
                {
                    var min = Expect(T.Number).Value;
                    var max = Accept(T.DotDot) ? Expect(T.Number).Value : min;
                    // A range that may be empty can be absent, like '*'.
                    return new GrammarItem(ItemKind.Complex, expression: new OperatorExpression(atom.Expression, min == "0" ? "*" : max == "1" && min == "1" ? "1" : "+"));
                }
                return atom;
            }

            private GrammarItem? TryParseAtom()
            {
                switch (Current.Type)
                {
                    case T.LPar:
                    {
                        Next();
                        var alternatives = ParseExpansions();
                        Expect(T.RPar);
                        return new GrammarItem(ItemKind.Complex, expression: new GroupExpression(alternatives, isMaybe: false));
                    }
                    case T.LBra:
                    {
                        Next();
                        var alternatives = ParseExpansions();
                        Expect(T.RBra);
                        return new GrammarItem(ItemKind.Complex, expression: new GroupExpression(alternatives, isMaybe: true));
                    }
                    case T.String:
                        Next();
                        if (Accept(T.DotDot))
                        {
                            Expect(T.String);
                            // A character range is a single anonymous string-like token.
                            return new GrammarItem(ItemKind.Complex, expression: new SymbolExpression(ItemKind.String, ""));
                        }
                        return new GrammarItem(ItemKind.String);
                    case T.Regexp:
                        Next();
                        return new GrammarItem(ItemKind.Regexp);
                    case T.Rule:
                    case T.Terminal:
                        var name = Next();
                        if (Accept(T.LBrace))
                        {
                            do
                            {
                                if (TryParseAtom() is null) throw Error("Expected a template argument");
                            }
                            while (Accept(T.Comma));
                            Expect(T.RBrace);
                            return new GrammarItem(ItemKind.Complex, expression: UnsupportedExpression.Instance);
                        }
                        return new GrammarItem(name.Type == T.Rule ? ItemKind.Rule : ItemKind.Terminal, name.Value);
                    default:
                        return null;
                }
            }
        }
    }
}
