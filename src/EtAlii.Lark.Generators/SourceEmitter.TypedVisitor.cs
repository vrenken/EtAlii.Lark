using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EtAlii.Lark.Generators
{
    /// <summary>Writes the typed, bottom-up <c>Visitor</c> of a grammar class with <c>[LarkResult]</c> attributes.</summary>
    internal sealed partial class SourceEmitter
    {
        private const string TreeType = "global::EtAlii.Lark.Tree";
        private const string TokenType = "global::EtAlii.Lark.Token";

        private static readonly HashSet<string> ReservedVisitNames = new() { "Tree", "Token", "Child" };

        private enum TypeKind
        {
            Mapped,
            Node,
            Tree,
            Token,
            Object,
        }

        /// <summary>The C# type of a value the typed visitor passes around.</summary>
        private sealed class TypeRef
        {
            public static readonly TypeRef Tree = new(TreeType, TypeKind.Tree, false, null);
            public static readonly TypeRef Token = new(TokenType, TypeKind.Token, false, null);
            public static readonly TypeRef Object = new("object", TypeKind.Object, false, null);

            public TypeRef(string name, TypeKind kind, bool isValueType, ResultType? info)
            {
                Name = name;
                Kind = kind;
                IsValueType = isValueType;
                Info = info;
            }

            public string Name { get; }

            public TypeKind Kind { get; }

            public bool IsValueType { get; }

            public ResultType? Info { get; }

            public bool ConvertsTo(TypeRef other) =>
                Name == other.Name || other.Kind == TypeKind.Object ||
                (Kind == TypeKind.Mapped && Info!.ConvertsTo(other.Name)) ||
                (Kind == TypeKind.Node && other.Kind == TypeKind.Tree);
        }

        /// <summary>The conversion of one child, shared by the children with the same members and type.</summary>
        private sealed class ChildConverter
        {
            public ChildConverter(string name, SortedSet<ShapeMember> members, string type)
            {
                Name = name;
                Members = members;
                Type = type;
            }

            public string Name { get; }

            public SortedSet<ShapeMember> Members { get; }

            /// <summary>The type the converter returns, nullable when the child can be a placeholder.</summary>
            public string Type { get; }

            public bool NeedsMatcher { get; set; }
        }

        private readonly List<Diagnostic> _diagnostics = new();
        private readonly Dictionary<string, ResultMapping> _results = new();
        private readonly Dictionary<string, ChildConverter> _converters = new();

        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

        private Location? ClassLocation => _model.Location?.ToLocation();

        private void EmitTypedVisitor()
        {
            var grammar = _grammarModel!;
            var known = new HashSet<string>(grammar.RuleNames.Concat(grammar.TerminalNames).Concat(grammar.Nodes.Select(n => n.Name)));
            foreach (var mapping in _model.Results)
            {
                foreach (var name in mapping.Names)
                {
                    var location = mapping.Location?.ToLocation() ?? ClassLocation;
                    if (!known.Contains(name))
                    {
                        _diagnostics.Add(Diagnostic.Create(Generators.Diagnostics.UnknownResultName, location, name, _model.ClassName));
                    }
                    else if (_results.ContainsKey(name))
                    {
                        _diagnostics.Add(Diagnostic.Create(Generators.Diagnostics.DuplicateResultName, location, name, _model.ClassName));
                    }
                    else
                    {
                        _results[name] = mapping;
                    }
                }
            }

            // Work out the parameters of every node first; that also collects the child converters.
            var parameters = new Dictionary<string, List<(string Type, string Name, ShapeElement Element, ChildConverter Converter)>?>();
            var nodes = grammar.Nodes.Where(n => !grammar.NeverTrees.Contains(n.Name)).ToList();
            foreach (var node in nodes)
            {
                parameters[node.Name] = ParametersOf(node.Name);
            }

            var mappedTerminals = grammar.TerminalNames.Where(_results.ContainsKey).ToList();

            Line();
            Line("/// <summary>");
            Line("/// A typed visitor that works bottom-up: it visits the children of a tree first, and passes them to the visit method of");
            Line("/// the tree as parameters of their result types, set with <c>[LarkResult]</c>. Nodes without a result type are passed as");
            Line("/// their node struct, and terminals without one as their token.");
            Line("/// </summary>");
            Open("public abstract partial class Visitor");

            Line("/// <summary>Visits a tree of any rule, and returns its result.</summary>");
            Line($"public virtual object? VisitTree({TreeType} tree) => tree.Data switch");
            Line("{");
            foreach (var node in nodes)
            {
                Line($"    {Literal(node.Name)} => (object?)Visit(new {_nodeTypeNames[node.Name]}(tree)),");
            }
            Line("    _ => DefaultVisit(tree),");
            Line("};");
            Line();
            Line("/// <summary>Visits a tree whose rule the visitor doesn't know. Returns the tree unless overridden.</summary>");
            Line($"protected virtual object? DefaultVisit({TreeType} tree) => tree;");
            Line();
            Line("/// <summary>Visits a token, and returns its result: the token itself, unless its terminal has a result type.</summary>");
            if (mappedTerminals.Count == 0)
            {
                Line($"public virtual object? VisitToken({TokenType} token) => token;");
            }
            else
            {
                Line($"public virtual object? VisitToken({TokenType} token) => token.Type switch");
                Line("{");
                foreach (var terminal in mappedTerminals)
                {
                    Line($"    {Literal(terminal)} => (object?){TerminalMethod(terminal)}(token),");
                }
                Line("    _ => token,");
                Line("};");
            }
            Line();
            Line("/// <summary>Visits a child of a tree: a tree, a token or a placeholder.</summary>");
            Line($"protected object? VisitChild(object? child) => child switch");
            Line("{");
            Line($"    {TreeType} tree => VisitTree(tree),");
            Line($"    {TokenType} token => VisitToken(token),");
            Line("    _ => child,");
            Line("};");

            // Start rules that are always inlined have no node of their own; they get an entry point from their tree.
            var entries = _model.Start
                .Where(start => grammar.NeverTrees.Contains(start) && grammar.RuleMembers.ContainsKey(start))
                .Select(start => (Start: start, Element: new ShapeElement(ElementKind.Single, grammar.RuleMembers[start], start, start)))
                .ToList();
            foreach (var (start, element) in entries)
            {
                var type = TypeOfElement(start, element);
                var nullable = element.Members.Contains(new ShapeMember(MemberKind.Null));
                var converter = Converter(element.Members, nullable ? type.Name + "?" : type.Name);
                Line();
                Line($"/// <summary>Visits a tree parsed from the <c>{start}</c> rule, which is inlined into the tree it parses.</summary>");
                Line($"public {converter.Type} Visit{GrammarAnalyzer.ToPascalCase(start)}({TreeType} tree) => {converter.Name}(tree);");
            }

            foreach (var node in nodes)
            {
                EmitTypedNode(node.Name, parameters[node.Name]);
            }

            foreach (var terminal in mappedTerminals)
            {
                Line();
                Line($"/// <summary>Converts a <c>{terminal}</c> token to its result.</summary>");
                Line($"protected abstract {TypeOfTerminal(terminal).Name} {TerminalMethod(terminal)}({TokenType} token);");
            }

            foreach (var converter in _converters.Values)
            {
                EmitConverter(converter);
            }

            Line();
            Open($"private static object? NextChild(global::System.Collections.Generic.List<object?> children, ref int index, string rule)");
            Line("if (index >= children.Count) throw new global::System.InvalidOperationException($\"The '{rule}' tree has fewer children than its rule describes.\");");
            Line("return children[index++];");
            Close();
            Line();
            Open($"private static void EndOfChildren(global::System.Collections.Generic.List<object?> children, int index, string rule)");
            Line("if (index != children.Count) throw new global::System.InvalidOperationException($\"The '{rule}' tree has more children than its rule describes.\");");
            Close();
            Line();
            Line("private static global::System.InvalidOperationException UnexpectedChild(object? child, string expected) =>");
            Line("    new($\"Expected {expected}, got {(child is " + TreeType + " tree ? \"a '\" + tree.Data + \"' tree\" : child is " + TokenType + " token ? \"a '\" + token.Type + \"' token\" : child?.ToString() ?? \"null\")}.\");");
            Close();
        }

        private List<(string Type, string Name, ShapeElement Element, ChildConverter Converter)>? ParametersOf(string node)
        {
            var shape = _grammarModel!.TypedShapes.TryGetValue(node, out var s) ? s : null;
            if (shape is null)
            {
                if (_results.TryGetValue(node, out var mapping))
                {
                    _diagnostics.Add(Diagnostic.Create(Generators.Diagnostics.NoFixedShape, mapping.Location?.ToLocation() ?? ClassLocation, node));
                }
                return null;
            }

            var names = ParameterNames(shape);
            var result = new List<(string, string, ShapeElement, ChildConverter)>();
            for (var i = 0; i < shape.Count; i++)
            {
                var element = shape[i];
                var type = TypeOfElement(node, element);
                var nullable = element.Members.Contains(new ShapeMember(MemberKind.Null));
                var converter = Converter(element.Members, nullable ? type.Name + "?" : type.Name);
                if (element.Kind != ElementKind.Single) converter.NeedsMatcher = true;
                var parameterType = element.Kind switch
                {
                    ElementKind.Repeated => $"global::System.Collections.Generic.IReadOnlyList<{type.Name}>",
                    ElementKind.Optional => type.Name + "?",
                    _ => converter.Type,
                };
                result.Add((parameterType, names[i], element, converter));
            }
            return result;
        }

        private void EmitTypedNode(string node, List<(string Type, string Name, ShapeElement Element, ChildConverter Converter)>? parameters)
        {
            var nodeType = _nodeTypeNames[node];
            var resultType = TypeOfNode(node).Name;
            var method = NodeMethod(node);
            var signature = parameters is null
                ? ", global::System.Collections.Generic.IReadOnlyList<object?> children"
                : string.Concat(parameters.Select(p => $", {p.Type} {p.Name}"));

            Line();
            Line($"/// <summary>Visits a <c>{node}</c> tree: visits its children, and passes them to <see cref=\"{method}\"/>.</summary>");
            Open($"public {resultType} Visit({nodeType} node)");
            if (parameters is null)
            {
                Line("var children = new global::System.Collections.Generic.List<object?>(node.Children.Count);");
                Line("foreach (var child in node.Children) children.Add(VisitChild(child));");
                Line($"return {method}(node, children);");
            }
            else
            {
                Line("var children = node.Children;");
                Line("var index = 0;");
                for (var i = 0; i < parameters.Count; i++)
                {
                    var (_, _, element, converter) = parameters[i];
                    switch (element.Kind)
                    {
                        case ElementKind.Single:
                            Line($"var p{i} = {converter.Name}(NextChild(children, ref index, {nodeType}.RuleName));");
                            break;
                        case ElementKind.Optional:
                            Line($"var p{i} = index < children.Count && {converter.Name}Matches(children[index]) ? ({converter.Type}?){converter.Name}(children[index++]) : null;");
                            break;
                        default:
                            Line($"var p{i} = new global::System.Collections.Generic.List<{converter.Type}>();");
                            Line($"while (index < children.Count && {converter.Name}Matches(children[index])) p{i}.Add({converter.Name}(children[index++]));");
                            break;
                    }
                }
                Line($"EndOfChildren(children, index, {nodeType}.RuleName);");
                Line($"return {method}(node{string.Concat(parameters.Select((_, i) => $", p{i}"))});");
            }
            Close();

            Line();
            if (_results.ContainsKey(node))
            {
                Line($"/// <summary>Creates the result of a <c>{node}</c> tree from its visited children.</summary>");
                Line($"protected abstract {resultType} {method}({nodeType} node{signature});");
            }
            else
            {
                Line($"/// <summary>Creates the result of a <c>{node}</c> tree from its visited children. Returns the node unless overridden.</summary>");
                Line($"protected virtual {resultType} {method}({nodeType} node{signature}) => node;");
            }
        }

        private void EmitConverter(ChildConverter converter)
        {
            var expected = string.Join(" or ", converter.Members.Select(m => m.Kind switch
            {
                MemberKind.Tree => $"a '{m.Name}' tree",
                MemberKind.Token => $"a '{m.Name}' token",
                MemberKind.AnyToken => "a token",
                MemberKind.Null => "a placeholder",
                _ => "a child",
            }));
            Line();
            Line($"private {converter.Type} {converter.Name}(object? child) => child switch");
            Line("{");
            foreach (var member in converter.Members.Where(m => m.Kind == MemberKind.Tree))
            {
                var call = _nodeTypeNames.TryGetValue(member.Name, out var type) ? $"Visit(new {type}(tree))" : "VisitTree(tree)";
                Line($"    {TreeType} {{ Data: {Literal(member.Name)} }} tree => ({converter.Type}){call},");
            }
            foreach (var member in converter.Members.Where(m => m.Kind == MemberKind.Token))
            {
                var call = _results.ContainsKey(member.Name) ? $"{TerminalMethod(member.Name)}(token)" : "token";
                Line($"    {TokenType} {{ Type: {Literal(member.Name)} }} token => ({converter.Type}){call},");
            }
            if (converter.Members.Any(m => m.Kind == MemberKind.AnyToken))
            {
                Line($"    {TokenType} token => ({converter.Type})VisitToken(token)!,");
            }
            if (converter.Members.Any(m => m.Kind == MemberKind.Null))
            {
                Line("    null => default,");
            }
            Line(converter.Members.Any(m => m.Kind == MemberKind.Any)
                ? $"    _ => ({converter.Type})VisitChild(child)!,"
                : $"    _ => throw UnexpectedChild(child, {Literal(expected)}),");
            Line("};");

            if (!converter.NeedsMatcher) return;
            var tests = new List<string>();
            var trees = converter.Members.Where(m => m.Kind == MemberKind.Tree).Select(m => Literal(m.Name)).ToList();
            if (trees.Count > 0) tests.Add($"child is {TreeType} {{ Data: {string.Join(" or ", trees)} }}");
            if (converter.Members.Any(m => m.Kind == MemberKind.AnyToken))
            {
                tests.Add($"child is {TokenType}");
            }
            else
            {
                var tokens = converter.Members.Where(m => m.Kind == MemberKind.Token).Select(m => Literal(m.Name)).ToList();
                if (tokens.Count > 0) tests.Add($"child is {TokenType} {{ Type: {string.Join(" or ", tokens)} }}");
            }
            if (converter.Members.Any(m => m.Kind == MemberKind.Any)) tests.Add("true");
            Line();
            Line($"private static bool {converter.Name}Matches(object? child) => {(tests.Count == 0 ? "false" : string.Join(" || ", tests))};");
        }

        private ChildConverter Converter(SortedSet<ShapeMember> members, string type)
        {
            var key = type + "|" + string.Join(",", members.Select(m => m.Kind + ":" + m.Name));
            if (!_converters.TryGetValue(key, out var converter))
            {
                converter = new ChildConverter("Child" + (_converters.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), members, type);
                _converters[key] = converter;
            }
            return converter;
        }

        private TypeRef TypeOfNode(string node) =>
            _results.TryGetValue(node, out var mapping) ? Mapped(mapping) : _nodeTypeNames.TryGetValue(node, out var type) ? new TypeRef(type, TypeKind.Node, true, null) : TypeRef.Tree;

        private TypeRef TypeOfTerminal(string terminal) => _results.TryGetValue(terminal, out var mapping) ? Mapped(mapping) : TypeRef.Token;

        private static TypeRef Mapped(ResultMapping mapping) => new(mapping.Type.Name, TypeKind.Mapped, mapping.Type.IsValueType, mapping.Type);

        private TypeRef TypeOfMember(ShapeMember member) => member.Kind switch
        {
            MemberKind.Tree => TypeOfNode(member.Name),
            MemberKind.Token => TypeOfTerminal(member.Name),
            MemberKind.AnyToken => TypeRef.Token,
            _ => TypeRef.Object,
        };

        private readonly HashSet<string> _reportedSlots = new();

        /// <summary>The type of a child: the result type of its inlined rule when that has one, else the common type of what it can be.</summary>
        private TypeRef TypeOfElement(string node, ShapeElement element)
        {
            var members = element.Members.Where(m => m.Kind != MemberKind.Null).ToList();
            var types = members.Select(TypeOfMember).ToList();

            if (element.SlotRule is { } slot && _results.TryGetValue(slot, out var slotMapping))
            {
                var explicitType = Mapped(slotMapping);
                for (var i = 0; i < members.Count; i++)
                {
                    if (!types[i].ConvertsTo(explicitType) && _reportedSlots.Add(slot + "|" + members[i]))
                    {
                        _diagnostics.Add(Diagnostic.Create(Generators.Diagnostics.ResultTypeMismatch, slotMapping.Location?.ToLocation() ?? ClassLocation,
                            slot, explicitType.Name, members[i].Name, types[i].Name));
                    }
                }
                return explicitType;
            }

            var common = CommonType(types);
            if (common is null)
            {
                if (_reportedSlots.Add(node + "|" + element.Name))
                {
                    var described = string.Join(", ", members.Select((m, i) => $"'{m}' ({types[i].Name})"));
                    _diagnostics.Add(Diagnostic.Create(Generators.Diagnostics.NoCommonResultType, ClassLocation, element.Name, node, described,
                        element.SlotRule is null
                            ? "Map them to types with a common base type to pass them typed."
                            : $"Add [LarkResult(typeof(...), \"{element.SlotRule}\")] to set their type."));
                }
                return TypeRef.Object;
            }
            return common;
        }

        /// <summary>The most specific type all the types convert to; null when only <c>object</c> fits and a mapped type is involved.</summary>
        private static TypeRef? CommonType(List<TypeRef> types)
        {
            var distinct = types.GroupBy(t => t.Name).Select(g => g.First()).ToList();
            if (distinct.Count == 0) return TypeRef.Object;
            if (distinct.Count == 1) return distinct[0];
            if (distinct.All(t => t.Kind is TypeKind.Node or TypeKind.Tree)) return TypeRef.Tree;
            if (distinct.All(t => t.Kind == TypeKind.Object || t.Kind == TypeKind.Token) || distinct.Any(t => t.Kind == TypeKind.Object)) return TypeRef.Object;
            if (distinct.Any(t => t.Kind != TypeKind.Mapped)) return distinct.Any(t => t.Kind == TypeKind.Mapped) ? null : TypeRef.Object;

            var first = distinct[0].Info!;
            var others = distinct.Skip(1).Select(t => t.Info!).ToList();
            foreach (var candidate in first.BaseTypes)
            {
                if (others.All(o => o.ConvertsTo(candidate))) return Candidate(candidate, distinct);
            }
            var interfaces = first.Interfaces.Where(i => others.All(o => o.ConvertsTo(i))).ToList();
            // Drop interfaces that another candidate already extends.
            var minimal = interfaces.Where(i => !interfaces.Any(other => other != i && first.InterfaceLinks.Contains(other + ">" + i))).ToList();
            return minimal.Count == 1 ? new TypeRef(minimal[0], TypeKind.Mapped, false, null) : null;
        }

        private static TypeRef Candidate(string name, List<TypeRef> types)
        {
            var exact = types.FirstOrDefault(t => t.Name == name);
            return exact ?? new TypeRef(name, TypeKind.Mapped, types[0].IsValueType && types[0].Info!.BaseTypes.Contains(name), null);
        }

        private string NodeMethod(string node)
        {
            var name = _nodeBaseNames[node];
            return "Visit" + (ReservedVisitNames.Contains(name) ? name + "Node" : name);
        }

        private static string TerminalMethod(string terminal) => "Visit" + GrammarAnalyzer.ToPascalCase(terminal) + "Token";

        private static List<string> ParameterNames(List<ShapeElement> shape)
        {
            var baseNames = shape.Select(e =>
            {
                var pascal = GrammarAnalyzer.ToPascalCase(e.Name.TrimStart('_'));
                var camel = char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
                return camel is "node" or "children" or "index" ? camel + "Child" : camel;
            }).ToList();
            var counts = baseNames.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
            var seen = new Dictionary<string, int>();
            var names = new List<string>();
            foreach (var name in baseNames)
            {
                var unique = name;
                if (counts[name] > 1)
                {
                    seen.TryGetValue(name, out var n);
                    seen[name] = ++n;
                    if (n > 1) unique = name + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                names.Add(SyntaxFacts.GetKeywordKind(unique) != SyntaxKind.None ? "@" + unique : unique);
            }
            return names;
        }
    }
}
