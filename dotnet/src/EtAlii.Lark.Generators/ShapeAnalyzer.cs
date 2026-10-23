using System;
using System.Collections.Generic;
using System.Linq;

namespace EtAlii.Lark.Generators
{
    internal enum MemberKind
    {
        /// <summary>A tree with the given data (a rule or an alias).</summary>
        Tree,

        /// <summary>A token of the given terminal.</summary>
        Token,

        /// <summary>A token of an anonymous terminal (a regular expression, or a kept string).</summary>
        AnyToken,

        /// <summary>The <c>None</c> placeholder of a missing <c>[item]</c>.</summary>
        Null,

        /// <summary>Anything: the generator doesn't know (imported rules, templates).</summary>
        Any,
    }

    /// <summary>Something that can be a child at a position of a tree.</summary>
    internal readonly struct ShapeMember : IEquatable<ShapeMember>, IComparable<ShapeMember>
    {
        public ShapeMember(MemberKind kind, string name = "")
        {
            Kind = kind;
            Name = name;
        }

        public MemberKind Kind { get; }

        public string Name { get; }

        public bool Overlaps(ShapeMember other)
        {
            if (Kind == MemberKind.Any || other.Kind == MemberKind.Any) return true;
            if (Kind == MemberKind.AnyToken) return other.Kind is MemberKind.Token or MemberKind.AnyToken;
            if (other.Kind == MemberKind.AnyToken) return Kind == MemberKind.Token;
            return Equals(other);
        }

        public bool Equals(ShapeMember other) => Kind == other.Kind && Name == other.Name;

        public override bool Equals(object? obj) => obj is ShapeMember other && Equals(other);

        public override int GetHashCode() => ((int)Kind * 397) ^ Name.GetHashCode();

        public int CompareTo(ShapeMember other) => Kind != other.Kind ? Kind.CompareTo(other.Kind) : string.CompareOrdinal(Name, other.Name);

        public override string ToString() => Kind is MemberKind.Tree or MemberKind.Token ? Name : Kind.ToString();
    }

    internal enum ElementKind
    {
        /// <summary>Exactly one child.</summary>
        Single,

        /// <summary>Zero or one child.</summary>
        Optional,

        /// <summary>Any number of children.</summary>
        Repeated,
    }

    /// <summary>A run of children of a tree: one child, an optional child or repeated children, each one of <see cref="Members"/>.</summary>
    internal sealed class ShapeElement
    {
        public ShapeElement(ElementKind kind, IEnumerable<ShapeMember> members, string name, string? slotRule)
        {
            Kind = kind;
            Members = new SortedSet<ShapeMember>(members);
            Name = name;
            SlotRule = slotRule;
        }

        public ElementKind Kind { get; }

        public SortedSet<ShapeMember> Members { get; }

        /// <summary>The grammar name the child is named after (<c>line</c>, <c>TEXT</c>).</summary>
        public string Name { get; set; }

        /// <summary>The inlined rule (<c>?rule</c> or <c>_rule</c>) the child comes from, whose result type is the type of the child.</summary>
        public string? SlotRule { get; set; }

        public ShapeElement With(ElementKind kind, IEnumerable<ShapeMember>? extra = null) =>
            new(kind, extra is null ? Members : Members.Concat(extra), Name, SlotRule);
    }

    /// <summary>
    /// Works out which children the trees of each node have, as Lark's parse tree builder creates them: filtered
    /// tokens, inlined <c>_rule</c> and <c>?rule</c> trees, aliases, <c>[item]</c> placeholders and repetitions.
    /// </summary>
    internal sealed class ShapeAnalyzer
    {
        private readonly Dictionary<string, RuleDefinition> _rules;
        private readonly bool _keepAllTokens;
        private readonly bool _maybePlaceholders;
        private readonly Dictionary<string, SortedSet<ShapeMember>> _members = new();

        public ShapeAnalyzer(Dictionary<string, RuleDefinition> rules, bool keepAllTokens, bool maybePlaceholders)
        {
            _rules = rules;
            _keepAllTokens = keepAllTokens;
            _maybePlaceholders = maybePlaceholders;
            ComputeMembers();
        }

        /// <summary>What a reference to the rule can be as a child: its trees, aliases or inlined children.</summary>
        public SortedSet<ShapeMember> MembersOf(string rule) =>
            _members.TryGetValue(rule, out var members) ? members : new SortedSet<ShapeMember> { new(MemberKind.Any) };

        /// <summary>The children of the trees with the given data, or null when they have no fixed shape.</summary>
        public List<ShapeElement>? ShapeOf(string name, List<(RuleDefinition Rule, Alternative? Alternative)> sources, out bool neverTree)
        {
            neverTree = false;
            var shapes = new List<List<ShapeElement>>();
            foreach (var (rule, alternative) in sources)
            {
                if (alternative is null) return null;
                var elements = ElementsOf(alternative, KeepAll(rule), new HashSet<string>());
                if (elements is null) return null;
                // The single-child alternatives of a ?rule are inlined, and don't create trees of the rule.
                if (rule.Expand1 && alternative.Alias is null && IsSingle(elements)) continue;
                shapes.Add(elements);
            }
            if (shapes.Count == 0)
            {
                neverTree = true;
                return null;
            }

            var shape = shapes[0];
            if (shapes.Count > 1)
            {
                // Alternatives that produce the same kinds of children merge into one shape.
                if (shapes.Any(s => s.Count != shape.Count || s.Where((e, i) => e.Kind != shape[i].Kind).Any())) return null;
                var merged = new List<ShapeElement>();
                for (var i = 0; i < shape.Count; i++)
                {
                    var column = shapes.Select(s => s[i]).ToList();
                    var names = column.Select(e => e.Name).Distinct().ToList();
                    var slots = column.Select(e => e.SlotRule).Distinct().ToList();
                    var elementName = names.Count == 1 ? names[0] : shape.Count == 1 ? name : "child";
                    merged.Add(new ShapeElement(shape[i].Kind, column.SelectMany(e => e.Members), elementName, slots.Count == 1 ? slots[0] : null));
                }
                shape = merged;
            }

            // Variable runs of children are matched greedily; they must not be able to take a child of what follows them.
            for (var i = 0; i < shape.Count; i++)
            {
                if (shape[i].Kind == ElementKind.Single) continue;
                for (var j = i + 1; j < shape.Count; j++)
                {
                    if (shape[i].Members.Any(m => shape[j].Members.Any(m.Overlaps))) return null;
                    if (shape[j].Kind == ElementKind.Single) break;
                }
            }
            return shape;
        }

        private bool KeepAll(RuleDefinition rule) => _keepAllTokens || rule.KeepAllTokens;

        /// <summary>Exactly one child (possibly a placeholder), which a <c>?rule</c> inlines.</summary>
        private static bool IsSingle(List<ShapeElement> elements) => elements.Count == 1 && elements[0].Kind == ElementKind.Single;

        /// <summary>
        /// The members of each rule when it is a child: its trees and aliases, or, for <c>?rule</c>, the children it
        /// inlines. Rules refer to each other, so this iterates until nothing changes.
        /// </summary>
        private void ComputeMembers()
        {
            foreach (var name in _rules.Keys) _members[name] = new SortedSet<ShapeMember>();
            bool changed;
            do
            {
                changed = false;
                foreach (var rule in _rules.Values)
                {
                    var members = MembersOf(rule);
                    if (!members.SetEquals(_members[rule.Name]))
                    {
                        _members[rule.Name] = members;
                        changed = true;
                    }
                }
            }
            while (changed);
        }

        private SortedSet<ShapeMember> MembersOf(RuleDefinition rule)
        {
            var members = new SortedSet<ShapeMember>();
            if (rule.IsImported || rule.IsTemplate)
            {
                members.Add(new ShapeMember(MemberKind.Any));
                return members;
            }
            foreach (var alternative in rule.Alternatives)
            {
                if (alternative.Alias is not null)
                {
                    members.Add(new ShapeMember(MemberKind.Tree, alternative.Alias));
                    continue;
                }
                if (!rule.Expand1)
                {
                    members.Add(new ShapeMember(MemberKind.Tree, rule.Name));
                    continue;
                }

                var elements = ElementsOf(alternative, KeepAll(rule), new HashSet<string>());
                if (elements is null)
                {
                    members.Add(new ShapeMember(MemberKind.Any));
                }
                else if (IsSingle(elements))
                {
                    members.UnionWith(elements[0].Members);
                }
                else
                {
                    members.Add(new ShapeMember(MemberKind.Tree, rule.Name));
                    // A variable number of children may come down to one, which is then inlined.
                    if (elements.Any(e => e.Kind != ElementKind.Single) && elements.Count(e => e.Kind == ElementKind.Single) <= 1)
                    {
                        foreach (var element in elements) members.UnionWith(element.Members);
                    }
                }
            }
            return members;
        }

        private List<ShapeElement>? ElementsOf(Alternative alternative, bool keepAll, HashSet<string> splicing)
        {
            var elements = new List<ShapeElement>();
            foreach (var item in alternative.Items)
            {
                var itemElements = ElementsOf(item.Expression, keepAll, splicing);
                if (itemElements is null) return null;
                elements.AddRange(itemElements);
            }
            return elements;
        }

        private List<ShapeElement>? ElementsOf(GrammarExpression expression, bool keepAll, HashSet<string> splicing)
        {
            switch (expression)
            {
                case SymbolExpression symbol:
                    return ElementsOf(symbol, keepAll, splicing);
                case GroupExpression { IsMaybe: true } maybe when _maybePlaceholders:
                    return MaybeElements(maybe, keepAll, splicing);
                case GroupExpression group:
                {
                    var merged = Merge(group.Alternatives.Select(a => ElementsOf(a, keepAll, splicing)).ToList());
                    if (merged is null || !group.IsMaybe) return merged;
                    return Optional(merged);
                }
                case OperatorExpression op:
                {
                    var operand = ElementsOf(op.Operand, keepAll, splicing);
                    if (operand is null) return null;
                    switch (op.Operator)
                    {
                        case "1":
                            return operand;
                        case "?":
                            return Optional(operand);
                        default:
                            if (operand.Count == 0) return operand;
                            if (operand.Count != 1 || operand[0].Kind != ElementKind.Single) return null;
                            return new List<ShapeElement> { operand[0].With(ElementKind.Repeated) };
                    }
                }
                default:
                    return null;
            }
        }

        private List<ShapeElement>? ElementsOf(SymbolExpression symbol, bool keepAll, HashSet<string> splicing)
        {
            var none = new List<ShapeElement>();
            switch (symbol.Kind)
            {
                case ItemKind.String:
                    return keepAll ? Single(new ShapeMember(MemberKind.AnyToken), "token", null) : none;
                case ItemKind.Regexp:
                    return Single(new ShapeMember(MemberKind.AnyToken), "token", null);
                case ItemKind.Terminal:
                    if (symbol.Name.StartsWith("_", StringComparison.Ordinal) && !keepAll) return none;
                    return Single(new ShapeMember(MemberKind.Token, symbol.Name), symbol.Name, null);
                case ItemKind.Rule:
                    if (!_rules.TryGetValue(symbol.Name, out var rule) || rule.IsTemplate || rule.IsImported)
                    {
                        return Single(new ShapeMember(MemberKind.Any), symbol.Name, null);
                    }
                    if (!rule.Name.StartsWith("_", StringComparison.Ordinal))
                    {
                        return new List<ShapeElement>
                        {
                            // The result type of a ?rule, or of a rule whose alternatives all have aliases, is the type of the child.
                            new(ElementKind.Single, _members[rule.Name], rule.Name,
                                rule.Expand1 || rule.Alternatives.All(a => a.Alias is not null) ? rule.Name : null),
                        };
                    }

                    // An _rule's children are spliced into the parent.
                    if (!splicing.Add(rule.Name)) return null;
                    try
                    {
                        var alternatives = rule.Alternatives.Select(a => ElementsOf(a, KeepAll(rule), splicing)).ToList();
                        if (alternatives.Count == 1) return alternatives[0];
                        var merged = Merge(alternatives);
                        if (merged is { Count: 1 })
                        {
                            merged[0].Name = rule.Name.TrimStart('_');
                            merged[0].SlotRule = rule.Name;
                        }
                        return merged;
                    }
                    finally
                    {
                        splicing.Remove(rule.Name);
                    }
                default:
                    return null;
            }
        }

        /// <summary><c>[ ... ]</c> with placeholders: as many children as the largest alternative, missing ones are null.</summary>
        private List<ShapeElement>? MaybeElements(GroupExpression maybe, bool keepAll, HashSet<string> splicing)
        {
            var alternatives = maybe.Alternatives.Select(a => ElementsOf(a, keepAll, splicing)).ToList();
            var merged = Merge(alternatives);
            if (merged is null) return null;
            var size = maybe.Alternatives.Max(a => PlaceholderSize(a, keepAll));
            if (size > 0 && merged.Count(e => e.Kind == ElementKind.Single) == size)
            {
                // A missing group leaves a placeholder for each single child; its repetitions are just empty.
                return merged.Select(e => e.Kind == ElementKind.Single ? e.With(ElementKind.Single, new[] { new ShapeMember(MemberKind.Null) }) : e).ToList();
            }
            // Spliced _rule children don't get placeholders, so the run is just optional.
            return size == 0 ? Optional(merged) : null;
        }

        /// <summary>The number of placeholders Lark inserts for a missing <c>[ ... ]</c> (its <c>FindRuleSize</c>).</summary>
        private int PlaceholderSize(Alternative alternative, bool keepAll) => alternative.Items.Sum(i => PlaceholderSize(i.Expression, keepAll));

        private int PlaceholderSize(GrammarExpression expression, bool keepAll) => expression switch
        {
            SymbolExpression { Kind: ItemKind.Rule } s => s.Name.StartsWith("_", StringComparison.Ordinal) ? 0 : 1,
            SymbolExpression { Kind: ItemKind.Terminal } s => keepAll || !s.Name.StartsWith("_", StringComparison.Ordinal) ? 1 : 0,
            SymbolExpression { Kind: ItemKind.String } => keepAll ? 1 : 0,
            SymbolExpression { Kind: ItemKind.Regexp } => 1,
            GroupExpression g => g.Alternatives.Count == 0 ? 0 : g.Alternatives.Max(a => PlaceholderSize(a, keepAll)),
            // Repetitions become _rules, which get no placeholders.
            OperatorExpression o => o.Operator is "?" or "1" ? PlaceholderSize(o.Operand, keepAll) : 0,
            _ => 0,
        };

        /// <summary>Alternatives merge when each has the same single children; null when they differ.</summary>
        private static List<ShapeElement>? Merge(List<List<ShapeElement>?> alternatives)
        {
            if (alternatives.Any(a => a is null)) return null;
            if (alternatives.Count == 0) return new List<ShapeElement>();
            if (alternatives.Count == 1) return alternatives[0];
            if (alternatives.Any(a => a!.Any(e => e.Kind != ElementKind.Single))) return null;
            var nonEmpty = alternatives.Where(a => a!.Count > 0).ToList();
            if (nonEmpty.Count < alternatives.Count)
            {
                // Some alternatives have no children: the others make an optional child.
                var present = Merge(nonEmpty);
                return present is null ? null : present.Count == 0 ? present : Optional(present);
            }
            var first = alternatives[0]!;
            if (alternatives.Any(a => a!.Count != first.Count)) return null;
            var merged = new List<ShapeElement>();
            for (var i = 0; i < first.Count; i++)
            {
                var column = alternatives.Select(a => a![i]).ToList();
                var slots = column.Select(e => e.SlotRule).Distinct().ToList();
                merged.Add(new ShapeElement(ElementKind.Single, column.SelectMany(e => e.Members), first[i].Name, slots.Count == 1 ? slots[0] : null));
            }
            return merged;
        }

        private static List<ShapeElement>? Optional(List<ShapeElement> elements)
        {
            if (elements.Count == 0) return elements;
            if (elements.Count != 1 || elements[0].Kind != ElementKind.Single) return null;
            return new List<ShapeElement> { elements[0].With(ElementKind.Optional) };
        }

        private static List<ShapeElement> Single(ShapeMember member, string name, string? slotRule) =>
            new() { new ShapeElement(ElementKind.Single, new[] { member }, name, slotRule) };
    }
}
