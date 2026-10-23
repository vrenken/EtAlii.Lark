"""The reference shape analysis for code generation, on Python Lark's compiled grammar.

The code generators of the ports type each tree by its *shape*: what can stand at each position among its children.
This module defines the shapes. It works on the BNF rules Lark compiles a grammar into, and follows what Lark's parse
tree builder does with them: filtered tokens, inlined `_rules`, `?rules` with one child, aliases, templates,
`maybe_placeholders` placeholders, and the helper rules that `*`, `+` and `~` become.

A shape is a list of elements. An element is one child (`single`), zero or one child (`optional`), or any number of
children (`repeated`), each one of its `members`: a tree name (a rule, alias or template), a terminal name,
`#none` (a placeholder) or `#any` (unknown). The analysis gives, for each tree name:

- a list of elements: every tree of that name matches it, children matched to elements greedily, in order;
- `null`: its trees have no fixed shape (alternatives that can't be aligned, or ambiguous runs);
- `"inlined"`: the name never makes a tree (a `?rule` that always has one child).

Each element also has a `name` (for accessors and parameters) and, when its type comes from a rule rather than from
its members, a `slot`: the `?rule`, all-aliased rule or `_rule` it stands for.

`generate.py` adds the shapes to every case, and checks that each expected tree matches them, so the shapes are
validated against the trees Lark really builds. The ports compute the same shapes from their own compiled grammars.
"""

SINGLE, OPTIONAL, REPEATED = 'single', 'optional', 'repeated'
NONE, ANY = '#none', '#any'
_RANK = {SINGLE: 0, OPTIONAL: 1, REPEATED: 2}


class Element:
    __slots__ = ('kind', 'members', 'names', 'slots')

    def __init__(self, kind, members, names, slots):
        self.kind = kind
        self.members = frozenset(members)
        self.names = frozenset(names)     # Empty for a placeholder: it takes the name of what it stands for.
        self.slots = frozenset(slots)     # Idem; None is "no slot".

    def with_kind(self, kind):
        return Element(kind, self.members, self.names, self.slots)

    def union(self, other, kind):
        return Element(kind, self.members | other.members, self.names | other.names, self.slots | other.slots)


PLACEHOLDER = Element(SINGLE, [NONE], [], [])


def _optional(e):
    return e.with_kind(OPTIONAL) if e.kind == SINGLE else e


def _overlaps(a, b):
    return ANY in a or ANY in b or bool(a & b)


def _columns(a, b):
    """Two sequences of the same length and kinds, merged position by position."""
    return [x.union(y, x.kind) for x, y in zip(a, b)]


def _score(x, y):
    """How well two elements align: equal members best, then overlapping members, then a placeholder with the single
    child it stands for (`[a b*]` leaves one for `a` only). Others don't align."""
    if x.members == y.members:
        return 3
    if x.members & y.members:
        return 2
    if (x.members == {NONE} and y.kind == SINGLE) or (y.members == {NONE} and x.kind == SINGLE):
        return 1
    return 0


def _align(a, b):
    """Two sequences merged on their best alignment of overlapping members; the rest becomes optional."""
    n, m = len(a), len(b)
    best = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(n - 1, -1, -1):
        for j in range(m - 1, -1, -1):
            score = _score(a[i], b[j])
            best[i][j] = max(best[i + 1][j], best[i][j + 1], best[i + 1][j + 1] + score if score else 0)
    result = []
    i = j = 0
    while i < n or j < m:
        score = _score(a[i], b[j]) if i < n and j < m else 0
        if score and best[i][j] == best[i + 1][j + 1] + score:
            result.append(a[i].union(b[j], max(a[i].kind, b[j].kind, key=_RANK.get)))
            i += 1
            j += 1
        elif j == m or (i < n and best[i + 1][j] >= best[i][j + 1]):
            result.append(_optional(a[i]))
            i += 1
        else:
            result.append(_optional(b[j]))
            j += 1
    return result


def _normalize(seq):
    """Adjacent optional or repeated elements with the same members become one repeated element (`x~2..5`)."""
    result = []
    for e in seq:
        last = result[-1] if result else None
        if last is not None and e.kind != SINGLE and last.kind != SINGLE and last.members == e.members:
            result[-1] = last.union(e, REPEATED)
        else:
            result.append(e)
    return result


def merge(seqs):
    """The sequences of the alternatives of a rule as one sequence; None when one of them is unknown.

    The longest sequences go first. A sequence with the same length and kinds as the result so far merges position by
    position (`(a|b)`, and `[a]` with placeholders); others are aligned to it.
    """
    if any(s is None for s in seqs):
        return None
    ordered = sorted(seqs, key=len, reverse=True)
    result = ordered[0]
    for seq in ordered[1:]:
        same = len(seq) == len(result) and all(x.kind == y.kind for x, y in zip(seq, result))
        result = _columns(result, seq) if same else _align(result, seq)
    return _normalize(result)


def _is_single(seq):
    return len(seq) == 1 and seq[0].kind == SINGLE


class ShapeAnalyzer:
    """Computes the shapes of the trees of a compiled grammar.

    `rules` are Lark's compiled rules (`Lark(...).rules`), `maybe_placeholders` the option the parser uses.
    """

    def __init__(self, rules, maybe_placeholders):
        self.maybe_placeholders = maybe_placeholders
        self.by_origin = {}
        for rule in rules:
            self.by_origin.setdefault(rule.origin.name, []).append(rule)
        self.members = {name: frozenset() for name in self.by_origin if not name.startswith('_')}
        # Rules refer to each other, so this iterates until nothing changes.
        changed = True
        while changed:
            changed = False
            for name in self.members:
                members = self._members_of(name)
                if members != self.members[name]:
                    self.members[name] = members
                    changed = True

    @staticmethod
    def tree_name(rule):
        return rule.alias or rule.options.template_source or rule.origin.name

    def tree_names(self):
        names = set()
        for origin, rules in self.by_origin.items():
            if not origin.startswith('_'):
                names.update(self.tree_name(r) for r in rules)
        return sorted(names)

    def _members_of(self, origin):
        """What a reference to the rule can be as a child: its trees, or the children a `?rule` inlines."""
        members = set()
        for rule in self.by_origin[origin]:
            if rule.alias or not rule.options.expand1:
                members.add(self.tree_name(rule))
                continue
            seq = self.children(rule)
            if seq is None:
                members.add(ANY)
                continue
            if not _is_single(seq):
                members.add(self.tree_name(rule))
            # With exactly one child, the ?rule is replaced by it.
            singles = [e for e in seq if e.kind == SINGLE]
            if len(singles) == 1:
                members |= singles[0].members
            elif not singles:
                for e in seq:
                    members |= e.members
        return frozenset(members)

    def children(self, rule, stack=(), skip_first=False):
        """The children of the trees the rule builds, as Lark's ChildFilter leaves them; None when unknown."""
        indices = rule.options.empty_indices if self.maybe_placeholders else ()
        if indices:
            empty = [len(ones) for ones in ''.join('1' if b else '0' for b in indices).split('0')]
        else:
            empty = [0] * (len(rule.expansion) + 1)
        keep_all = rule.options.keep_all_tokens
        seq = []
        nones = 0
        for i, sym in enumerate(rule.expansion):
            nones += empty[i]
            if keep_all or not (sym.is_term and sym.filter_out):
                seq += [PLACEHOLDER] * nones
                nones = 0
                if skip_first and i == 0:
                    continue
                part = self._symbol(sym, stack)
                if part is None:
                    return None
                seq += part
        seq += [PLACEHOLDER] * (nones + empty[len(rule.expansion)])
        return seq

    def _symbol(self, sym, stack):
        if sym.is_term:
            return [Element(SINGLE, [sym.name], [sym.name], [None])]
        name = sym.name
        if not name.startswith('_'):
            rules = self.by_origin[name]
            template = rules[0].options.template_source
            slot = name if not template and (rules[0].options.expand1 or all(r.alias for r in rules)) else None
            return [Element(SINGLE, self.members[name], [template or name], [slot])]
        return self._splice(name, stack)

    def _splice(self, name, stack):
        """The children of an inlined `_rule`, which take its place among the children of the parent."""
        if name in stack:
            return None
        rules = self.by_origin[name]
        if any(r.options.expand1 for r in rules):
            return None
        stack = stack + (name,)
        recursive = [r for r in rules if any(not s.is_term and s.name == name for s in r.expansion)]
        if recursive:
            # The helper rules of `x+` and `x*`: `_r: x | _r x`.
            if any(r.expansion[0].name != name or r.expansion[0].is_term
                   or any(not s.is_term and s.name == name for s in r.expansion[1:]) for r in recursive):
                return None
            base_rules = [r for r in rules if r not in recursive]
            if not base_rules:
                return None
            base = merge([self.children(r, stack) for r in base_rules])
            step = merge([self.children(r, stack, skip_first=True) for r in recursive])
            if base is None or step is None:
                return None
            if not base and not step:
                return []
            if len(base) != 1 or len(step) != 1:
                return None
            return [base[0].union(step[0], REPEATED)]
        seq = merge([self.children(r, stack) for r in rules])
        if seq is not None and len(rules) > 1 and len(seq) == 1 and not name.startswith('__'):
            seq = [Element(seq[0].kind, seq[0].members, [name.lstrip('_')], [name])]
        return seq

    def shape(self, name):
        """The shape of the trees with this name: a list of elements, None (no fixed shape) or 'inlined'."""
        seqs = []
        for origin, rules in self.by_origin.items():
            if origin.startswith('_'):
                continue
            for rule in rules:
                if self.tree_name(rule) != name:
                    continue
                seq = self.children(rule)
                if seq is None:
                    return None
                # A ?rule with one child is replaced by it, and makes no tree.
                if rule.options.expand1 and not rule.alias and _is_single(seq):
                    continue
                seqs.append(seq)
        if not seqs:
            return 'inlined'
        shape = merge(seqs)
        # Optional and repeated children are matched greedily, so they can't take a child of what follows them.
        for i, e in enumerate(shape):
            if e.kind == SINGLE:
                continue
            for f in shape[i + 1:]:
                if _overlaps(e.members, f.members):
                    return None
                if f.kind == SINGLE:
                    break
        return shape

    def shapes_json(self):
        result = {}
        for name in self.tree_names():
            shape = self.shape(name)
            result[name] = shape if shape is None or shape == 'inlined' else [_element_json(e, name, len(shape)) for e in shape]
        return result


def _element_json(e, tree, count):
    names = sorted(e.names)
    name = names[0] if len(names) == 1 else (tree if count == 1 else 'child') if names else 'none'
    result = {'kind': e.kind, 'name': name, 'members': sorted(e.members)}
    slots = [s for s in e.slots]
    if len(slots) == 1 and slots[0] is not None:
        result['slot'] = slots[0]
    return result


def matches(shape, children):
    """Whether the children (JSON trees, tokens and nulls, as in the cases) match the shape."""
    def member(child):
        if child is None:
            return NONE
        return child['data'] if 'data' in child else child['type']

    i = 0
    for e in shape:
        fits = lambda c: ANY in e['members'] or member(c) in e['members']
        if e['kind'] == SINGLE:
            if i >= len(children) or not fits(children[i]):
                return False
            i += 1
        elif e['kind'] == OPTIONAL:
            if i < len(children) and fits(children[i]):
                i += 1
        else:
            while i < len(children) and fits(children[i]):
                i += 1
    return i == len(children)


def check_tree(shapes, tree, problems):
    """Checks that the JSON tree and its subtrees match their shapes; adds what doesn't to `problems`."""
    if tree is None or 'data' not in tree:
        return
    name = tree['data']
    if name != '_ambig':
        shape = shapes.get(name, 'unknown')
        if shape == 'unknown':
            problems.append('%s: not a tree name' % name)
        elif shape == 'inlined':
            problems.append('%s: a tree, but the shape says it is always inlined' % name)
        elif shape is not None and not matches(shape, tree['children']):
            problems.append('%s: %s doesn\'t match %s' % (
                name, [c if c is None else c.get('data', c.get('type')) for c in tree['children']], shape))
    for child in tree['children']:
        check_tree(shapes, child, problems)
