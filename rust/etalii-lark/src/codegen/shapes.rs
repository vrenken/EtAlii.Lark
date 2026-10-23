//! The shapes of the trees of a grammar: what can stand at each position among their children.
//!
//! The analysis works on the compiled (BNF) rules, and follows what the tree builder does with them: filtered
//! tokens, inlined `_rules`, `?rules` with one child, aliases, templates, `maybe_placeholders` placeholders, and the
//! helper rules that `*`, `+` and `~` become. It is defined by `conformance/tools/shapes.py`, which checks it against
//! the trees Python Lark builds; the conformance cases hold its results, and every port computes the same shapes.

use std::collections::{BTreeMap, BTreeSet};
use std::fmt;

use indexmap::IndexMap;

use crate::grammar::{Rule, Symbol};
use crate::lark::Lark;

/// Something that can be a child at a position of a tree.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub enum Member {
    /// A tree with this data: a rule, alias or template name.
    Tree(String),
    /// A token of this terminal.
    Token(String),
    /// The placeholder of a missing `[item]` (see `maybe_placeholders`).
    Placeholder,
    /// Anything: the analysis doesn't know.
    Any,
}

impl Member {
    /// The member as the conformance cases write it: the name, `#none` or `#any`.
    pub fn as_str(&self) -> &str {
        match self {
            Member::Tree(name) | Member::Token(name) => name,
            Member::Placeholder => "#none",
            Member::Any => "#any",
        }
    }
}

impl fmt::Display for Member {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

impl PartialOrd for Member {
    fn partial_cmp(&self, other: &Member) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for Member {
    fn cmp(&self, other: &Member) -> std::cmp::Ordering {
        self.as_str().cmp(other.as_str())
    }
}

/// How many children an element stands for.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum ElementKind {
    /// Exactly one child.
    Single,
    /// Zero or one child.
    Optional,
    /// Any number of children.
    Repeated,
}

impl ElementKind {
    /// The kind as the conformance cases write it.
    pub fn as_str(self) -> &'static str {
        match self {
            ElementKind::Single => "single",
            ElementKind::Optional => "optional",
            ElementKind::Repeated => "repeated",
        }
    }
}

/// A run of children of a tree: one child, an optional child or repeated children, each one of `members`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ShapeElement {
    pub kind: ElementKind,
    /// The grammar name the children are named after (`line`, `TEXT`).
    pub name: String,
    /// What the children can be, sorted.
    pub members: Vec<Member>,
    /// The rule the children stand for, when their type comes from it rather than from the members: a `?rule`, a
    /// rule whose expansions all have aliases, or an inlined `_rule` with alternatives.
    pub slot: Option<String>,
}

/// The shape of the trees with a given name.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Shape {
    /// Every tree has these children, matched to the elements greedily, in order.
    Fixed(Vec<ShapeElement>),
    /// The trees have no fixed shape: alternatives that can't be aligned, or ambiguous runs of children.
    Variable,
    /// The name never makes a tree: a `?rule` that always has one child.
    Inlined,
}

/// The shapes of all trees of a compiled grammar, by tree name.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Shapes {
    pub trees: BTreeMap<String, Shape>,
}

impl Shapes {
    /// The shapes of the trees of the parser.
    pub fn of(lark: &Lark) -> Shapes {
        let rules: Vec<&Rule> = lark.rules().collect();
        Shapes::from_rules(&rules, lark.options().maybe_placeholders)
    }

    /// The shapes of the trees built from these compiled rules, with or without `maybe_placeholders`.
    pub fn from_rules(rules: &[&Rule], maybe_placeholders: bool) -> Shapes {
        let analyzer = Analyzer::new(rules, maybe_placeholders);
        let mut trees = BTreeMap::new();
        for name in analyzer.tree_names() {
            let shape = match analyzer.shape(&name) {
                Analysis::Unknown => Shape::Variable,
                Analysis::Inlined => Shape::Inlined,
                Analysis::Fixed(shape) => {
                    let count = shape.len();
                    Shape::Fixed(shape.into_iter().map(|e| e.finish(&name, count)).collect())
                }
            };
            trees.insert(name, shape);
        }
        Shapes { trees }
    }
}

#[derive(Clone, Debug)]
struct Element {
    kind: ElementKind,
    members: BTreeSet<Member>,
    /// Empty for a placeholder: it takes the name of what it stands for.
    names: BTreeSet<String>,
    /// Idem; `None` is "no slot".
    slots: BTreeSet<Option<String>>,
}

impl Element {
    fn new(kind: ElementKind, members: BTreeSet<Member>, name: Option<String>, slot: Option<Option<String>>) -> Element {
        Element {
            kind,
            members,
            names: name.into_iter().collect(),
            slots: slot.into_iter().collect(),
        }
    }

    fn placeholder() -> Element {
        Element::new(ElementKind::Single, BTreeSet::from([Member::Placeholder]), None, None)
    }

    fn with_kind(&self, kind: ElementKind) -> Element {
        Element { kind, ..self.clone() }
    }

    fn union(&self, other: &Element, kind: ElementKind) -> Element {
        Element {
            kind,
            members: self.members.union(&other.members).cloned().collect(),
            names: self.names.union(&other.names).cloned().collect(),
            slots: self.slots.union(&other.slots).cloned().collect(),
        }
    }

    fn optional(&self) -> Element {
        if self.kind == ElementKind::Single {
            self.with_kind(ElementKind::Optional)
        } else {
            self.clone()
        }
    }

    fn finish(self, tree: &str, count: usize) -> ShapeElement {
        let name = match self.names.len() {
            0 => "none".to_string(),
            1 => self.names.into_iter().next().unwrap(),
            _ if count == 1 => tree.to_string(),
            _ => "child".to_string(),
        };
        let slot = if self.slots.len() == 1 {
            self.slots.into_iter().next().unwrap()
        } else {
            None
        };
        ShapeElement {
            kind: self.kind,
            name,
            members: self.members.into_iter().collect(),
            slot,
        }
    }
}

type Seq = Vec<Element>;

enum Analysis {
    Fixed(Seq),
    Unknown,
    Inlined,
}

fn overlaps(a: &BTreeSet<Member>, b: &BTreeSet<Member>) -> bool {
    a.contains(&Member::Any) || b.contains(&Member::Any) || a.intersection(b).next().is_some()
}

fn is_placeholder(e: &Element) -> bool {
    e.members.len() == 1 && e.members.contains(&Member::Placeholder)
}

/// How well two elements align: equal members best, then overlapping members, then a placeholder with the single
/// child it stands for (`[a b*]` leaves one for `a` only). Others don't align.
fn score(x: &Element, y: &Element) -> usize {
    if x.members == y.members {
        3
    } else if x.members.intersection(&y.members).next().is_some() {
        2
    } else if (is_placeholder(x) && y.kind == ElementKind::Single) || (is_placeholder(y) && x.kind == ElementKind::Single) {
        1
    } else {
        0
    }
}

/// Two sequences of the same length and kinds, merged position by position.
fn columns(a: &Seq, b: &Seq) -> Seq {
    a.iter().zip(b).map(|(x, y)| x.union(y, x.kind)).collect()
}

/// Two sequences merged on their best alignment of overlapping members; the rest becomes optional.
fn align(a: &Seq, b: &Seq) -> Seq {
    let (n, m) = (a.len(), b.len());
    let mut best = vec![vec![0usize; m + 1]; n + 1];
    for i in (0..n).rev() {
        for j in (0..m).rev() {
            let s = score(&a[i], &b[j]);
            let matched = if s > 0 { best[i + 1][j + 1] + s } else { 0 };
            best[i][j] = best[i + 1][j].max(best[i][j + 1]).max(matched);
        }
    }
    let mut result = Vec::new();
    let (mut i, mut j) = (0, 0);
    while i < n || j < m {
        let s = if i < n && j < m { score(&a[i], &b[j]) } else { 0 };
        if s > 0 && best[i][j] == best[i + 1][j + 1] + s {
            result.push(a[i].union(&b[j], a[i].kind.max(b[j].kind)));
            i += 1;
            j += 1;
        } else if j == m || (i < n && best[i + 1][j] >= best[i][j + 1]) {
            result.push(a[i].optional());
            i += 1;
        } else {
            result.push(b[j].optional());
            j += 1;
        }
    }
    result
}

/// Adjacent optional or repeated elements with the same members become one repeated element (`x~2..5`).
fn normalize(seq: Seq) -> Seq {
    let mut result: Seq = Vec::new();
    for e in seq {
        if let Some(last) = result.last_mut() {
            if e.kind != ElementKind::Single && last.kind != ElementKind::Single && last.members == e.members {
                *last = last.union(&e, ElementKind::Repeated);
                continue;
            }
        }
        result.push(e);
    }
    result
}

/// The sequences of the alternatives of a rule as one sequence; `None` when one of them is unknown.
///
/// The longest sequences go first. A sequence with the same length and kinds as the result so far merges position by
/// position (`(a|b)`, and `[a]` with placeholders); others are aligned to it.
fn merge(seqs: Vec<Option<Seq>>) -> Option<Seq> {
    let mut ordered: Vec<Seq> = seqs.into_iter().collect::<Option<_>>()?;
    ordered.sort_by_key(|s| std::cmp::Reverse(s.len()));
    let mut iter = ordered.into_iter();
    let mut result = iter.next()?;
    for seq in iter {
        let same = seq.len() == result.len() && seq.iter().zip(&result).all(|(x, y)| x.kind == y.kind);
        result = if same { columns(&result, &seq) } else { align(&result, &seq) };
    }
    Some(normalize(result))
}

fn is_single(seq: &Seq) -> bool {
    seq.len() == 1 && seq[0].kind == ElementKind::Single
}

fn refers_to(sym: &Symbol, name: &str) -> bool {
    !sym.is_term && &*sym.name == name
}

struct Analyzer<'r> {
    maybe_placeholders: bool,
    by_origin: IndexMap<&'r str, Vec<&'r Rule>>,
    members: IndexMap<&'r str, BTreeSet<Member>>,
}

impl<'r> Analyzer<'r> {
    fn new(rules: &[&'r Rule], maybe_placeholders: bool) -> Analyzer<'r> {
        let mut by_origin: IndexMap<&str, Vec<&Rule>> = IndexMap::new();
        for rule in rules {
            by_origin.entry(rule.origin.name()).or_default().push(rule);
        }
        let members = by_origin
            .keys()
            .filter(|name| !name.starts_with('_'))
            .map(|name| (*name, BTreeSet::new()))
            .collect();
        let mut analyzer = Analyzer {
            maybe_placeholders,
            by_origin,
            members,
        };
        // Rules refer to each other, so this iterates until nothing changes.
        let mut changed = true;
        while changed {
            changed = false;
            let names: Vec<&str> = analyzer.members.keys().copied().collect();
            for name in names {
                let members = analyzer.members_of(name);
                if members != analyzer.members[name] {
                    analyzer.members[name] = members;
                    changed = true;
                }
            }
        }
        analyzer
    }

    fn tree_name(rule: &Rule) -> String {
        rule.alias
            .clone()
            .or_else(|| rule.options.template_source.clone())
            .unwrap_or_else(|| rule.origin.name.to_string())
    }

    fn tree_names(&self) -> BTreeSet<String> {
        self.by_origin
            .iter()
            .filter(|(origin, _)| !origin.starts_with('_'))
            .flat_map(|(_, rules)| rules.iter().map(|r| Self::tree_name(r)))
            .collect()
    }

    /// What a reference to the rule can be as a child: its trees, or the children a `?rule` inlines.
    fn members_of(&self, origin: &str) -> BTreeSet<Member> {
        let mut members = BTreeSet::new();
        for rule in &self.by_origin[origin] {
            if rule.alias.is_some() || !rule.options.expand1 {
                members.insert(Member::Tree(Self::tree_name(rule)));
                continue;
            }
            let Some(seq) = self.children(rule, &[], false) else {
                members.insert(Member::Any);
                continue;
            };
            if !is_single(&seq) {
                members.insert(Member::Tree(Self::tree_name(rule)));
            }
            // With exactly one child, the ?rule is replaced by it.
            let singles: Vec<&Element> = seq.iter().filter(|e| e.kind == ElementKind::Single).collect();
            if singles.len() == 1 {
                members.extend(singles[0].members.iter().cloned());
            } else if singles.is_empty() {
                for e in &seq {
                    members.extend(e.members.iter().cloned());
                }
            }
        }
        members
    }

    /// The children of the trees the rule builds, as the tree builder's child filter leaves them; `None` when
    /// unknown. With `skip_first`, the first symbol (the recursion of a helper rule) is left out.
    fn children(&self, rule: &Rule, stack: &[&str], skip_first: bool) -> Option<Seq> {
        let indices: &[bool] = if self.maybe_placeholders {
            &rule.options.empty_indices
        } else {
            &[]
        };
        let empty: Vec<usize> = if indices.is_empty() {
            vec![0; rule.expansion.len() + 1]
        } else {
            let s: String = indices.iter().map(|&b| if b { '1' } else { '0' }).collect();
            s.split('0').map(|ones| ones.len()).collect()
        };
        let keep_all = rule.options.keep_all_tokens;
        let mut seq = Vec::new();
        let mut nones = 0;
        for (i, sym) in rule.expansion.iter().enumerate() {
            nones += empty[i];
            if keep_all || !(sym.is_term && sym.filter_out) {
                seq.extend(std::iter::repeat_with(Element::placeholder).take(nones));
                nones = 0;
                if skip_first && i == 0 {
                    continue;
                }
                seq.extend(self.symbol(sym, stack)?);
            }
        }
        seq.extend(std::iter::repeat_with(Element::placeholder).take(nones + empty[rule.expansion.len()]));
        Some(seq)
    }

    fn symbol(&self, sym: &Symbol, stack: &[&str]) -> Option<Seq> {
        let name = sym.name();
        if sym.is_term {
            return Some(vec![Element::new(
                ElementKind::Single,
                BTreeSet::from([Member::Token(name.to_string())]),
                Some(name.to_string()),
                Some(None),
            )]);
        }
        if !name.starts_with('_') {
            let rules = &self.by_origin[name];
            let template = rules[0].options.template_source.clone();
            let slot = if template.is_none() && (rules[0].options.expand1 || rules.iter().all(|r| r.alias.is_some())) {
                Some(name.to_string())
            } else {
                None
            };
            return Some(vec![Element::new(
                ElementKind::Single,
                self.members[name].clone(),
                Some(template.unwrap_or_else(|| name.to_string())),
                Some(slot),
            )]);
        }
        self.splice(name, stack)
    }

    /// The children of an inlined `_rule`, which take its place among the children of the parent.
    fn splice(&self, name: &str, stack: &[&str]) -> Option<Seq> {
        if stack.contains(&name) {
            return None;
        }
        let rules = &self.by_origin[name];
        if rules.iter().any(|r| r.options.expand1) {
            return None;
        }
        let mut inner: Vec<&str> = stack.to_vec();
        inner.push(name);
        let stack = &inner[..];
        let (recursive, base): (Vec<&&Rule>, Vec<&&Rule>) = rules.iter().partition(|r| r.expansion.iter().any(|s| refers_to(s, name)));
        if !recursive.is_empty() {
            // The helper rules of `x+` and `x*`: `_r: x | _r x`.
            if recursive
                .iter()
                .any(|r| !refers_to(&r.expansion[0], name) || r.expansion[1..].iter().any(|s| refers_to(s, name)))
            {
                return None;
            }
            if base.is_empty() {
                return None;
            }
            let base = merge(base.iter().map(|r| self.children(r, stack, false)).collect())?;
            let step = merge(recursive.iter().map(|r| self.children(r, stack, true)).collect())?;
            if base.is_empty() && step.is_empty() {
                return Some(Vec::new());
            }
            if base.len() != 1 || step.len() != 1 {
                return None;
            }
            return Some(vec![base[0].union(&step[0], ElementKind::Repeated)]);
        }
        let mut seq = merge(rules.iter().map(|r| self.children(r, stack, false)).collect());
        if let Some(s) = &mut seq {
            if rules.len() > 1 && s.len() == 1 && !name.starts_with("__") {
                s[0].names = BTreeSet::from([name.trim_start_matches('_').to_string()]);
                s[0].slots = BTreeSet::from([Some(name.to_string())]);
            }
        }
        seq
    }

    /// The shape of the trees with this name.
    fn shape(&self, name: &str) -> Analysis {
        let mut seqs = Vec::new();
        for (origin, rules) in &self.by_origin {
            if origin.starts_with('_') {
                continue;
            }
            for rule in rules {
                if Self::tree_name(rule) != name {
                    continue;
                }
                let Some(seq) = self.children(rule, &[], false) else {
                    return Analysis::Unknown;
                };
                // A ?rule with one child is replaced by it, and makes no tree.
                if rule.options.expand1 && rule.alias.is_none() && is_single(&seq) {
                    continue;
                }
                seqs.push(Some(seq));
            }
        }
        if seqs.is_empty() {
            return Analysis::Inlined;
        }
        let Some(shape) = merge(seqs) else {
            return Analysis::Unknown;
        };
        // Optional and repeated children are matched greedily, so they can't take a child of what follows them.
        for (i, e) in shape.iter().enumerate() {
            if e.kind == ElementKind::Single {
                continue;
            }
            for f in &shape[i + 1..] {
                if overlaps(&e.members, &f.members) {
                    return Analysis::Unknown;
                }
                if f.kind == ElementKind::Single {
                    break;
                }
            }
        }
        Analysis::Fixed(shape)
    }
}
