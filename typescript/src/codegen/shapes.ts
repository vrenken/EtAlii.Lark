// The shapes of the trees of a grammar: what can stand at each position among their children.
//
// The analysis works on the compiled (BNF) rules, and follows what the tree builder does with them: filtered tokens,
// inlined `_rules`, `?rules` with one child, aliases, templates, `maybePlaceholders` placeholders, and the helper
// rules that `*`, `+` and `~` become. It is defined by `conformance/tools/shapes.py`, which checks it against the trees
// Python Lark builds; the conformance cases hold its results, and every port computes the same shapes.

import type { Rule, Symbol } from '../grammar.js';
import type { Lark } from '../lark.js';

/** How many children an element stands for: exactly one, zero or one, or any number. */
export type ElementKind = 'single' | 'optional' | 'repeated';

/** A placeholder member: the `null` of a missing `[item]` (see `maybePlaceholders`). */
export const PLACEHOLDER = '#none';
/** An unknown member: anything. */
export const ANY = '#any';

/** A run of children of a tree: one child, an optional child or repeated children, each one of `members`. */
export interface ShapeElement {
  kind: ElementKind;
  /** The grammar name the children are named after (`line`, `TEXT`). */
  name: string;
  /** What the children can be, sorted: tree names (rules, aliases, templates), terminal names, `#none` or `#any`. */
  members: string[];
  /**
   * The rule the children stand for, when their type comes from it rather than from the members: a `?rule`, a rule
   * whose expansions all have aliases, or an inlined `_rule` with alternatives.
   */
  slot?: string;
}

/**
 * The shape of the trees with a given name: the elements their children match (greedily, in order), `null` when they
 * have no fixed shape, or `'inlined'` when the name never makes a tree (a `?rule` that always has one child).
 */
export type Shape = ShapeElement[] | null | 'inlined';

/** The shapes of the trees of the parser, by tree name (sorted). */
export function shapesOf(lark: Lark): Map<string, Shape> {
  return computeShapes(lark.rules, lark.options.maybePlaceholders);
}

/** The shapes of the trees built from these compiled rules, with or without `maybePlaceholders`. */
export function computeShapes(rules: readonly Rule[], maybePlaceholders: boolean): Map<string, Shape> {
  const analyzer = new Analyzer(rules, maybePlaceholders);
  const result = new Map<string, Shape>();
  for (const name of analyzer.treeNames()) {
    const shape = analyzer.shape(name);
    result.set(name, shape === null || shape === 'inlined' ? shape : shape.map((e) => finish(e, name, shape.length)));
  }
  return result;
}

/**
 * What a reference to each rule (other than `_rules`) can be as a child, by rule name (sorted): its trees, or for a
 * `?rule`, also the children it inlines.
 */
export function referencesOf(lark: Lark): Map<string, string[]> {
  return new Analyzer(lark.rules, lark.options.maybePlaceholders).references();
}

const RANK: Record<ElementKind, number> = { single: 0, optional: 1, repeated: 2 };

interface Element {
  kind: ElementKind;
  members: Set<string>;
  /** Empty for a placeholder: it takes the name of what it stands for. */
  names: Set<string>;
  /** Idem; `null` is "no slot". */
  slots: Set<string | null>;
}

type Seq = Element[];

const placeholder = (): Element => ({ kind: 'single', members: new Set([PLACEHOLDER]), names: new Set(), slots: new Set() });

const union = <T>(a: Set<T>, b: Set<T>): Set<T> => new Set([...a, ...b]);

const unite = (x: Element, y: Element, kind: ElementKind): Element => ({
  kind,
  members: union(x.members, y.members),
  names: union(x.names, y.names),
  slots: union(x.slots, y.slots),
});

const optional = (e: Element): Element => (e.kind === 'single' ? { ...e, kind: 'optional' } : e);

const sameMembers = (a: Set<string>, b: Set<string>): boolean => a.size === b.size && [...a].every((m) => b.has(m));

const intersects = (a: Set<string>, b: Set<string>): boolean => [...a].some((m) => b.has(m));

const overlaps = (a: Set<string>, b: Set<string>): boolean => a.has(ANY) || b.has(ANY) || intersects(a, b);

const isPlaceholder = (e: Element): boolean => e.members.size === 1 && e.members.has(PLACEHOLDER);

const sorted = (values: Iterable<string>): string[] => [...values].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));

function finish(e: Element, tree: string, count: number): ShapeElement {
  const names = sorted(e.names);
  const name = names.length === 1 ? names[0]! : names.length === 0 ? 'none' : count === 1 ? tree : 'child';
  const result: ShapeElement = { kind: e.kind, name, members: sorted(e.members) };
  const slots = [...e.slots];
  if (slots.length === 1 && slots[0] !== null) result.slot = slots[0];
  return result;
}

/**
 * How well two elements align: equal members best, then overlapping members, then a placeholder with the single
 * child it stands for (`[a b*]` leaves one for `a` only). Others don't align.
 */
function score(x: Element, y: Element): number {
  if (sameMembers(x.members, y.members)) return 3;
  if (intersects(x.members, y.members)) return 2;
  if ((isPlaceholder(x) && y.kind === 'single') || (isPlaceholder(y) && x.kind === 'single')) return 1;
  return 0;
}

/** Two sequences of the same length and kinds, merged position by position. */
const columns = (a: Seq, b: Seq): Seq => a.map((x, i) => unite(x, b[i]!, x.kind));

/** Two sequences merged on their best alignment of overlapping members; the rest becomes optional. */
function align(a: Seq, b: Seq): Seq {
  const n = a.length;
  const m = b.length;
  const best = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      const s = score(a[i]!, b[j]!);
      best[i]![j] = Math.max(best[i + 1]![j]!, best[i]![j + 1]!, s > 0 ? best[i + 1]![j + 1]! + s : 0);
    }
  }
  const result: Seq = [];
  let i = 0;
  let j = 0;
  while (i < n || j < m) {
    const s = i < n && j < m ? score(a[i]!, b[j]!) : 0;
    if (s > 0 && best[i]![j] === best[i + 1]![j + 1]! + s) {
      result.push(unite(a[i]!, b[j]!, RANK[a[i]!.kind] >= RANK[b[j]!.kind] ? a[i]!.kind : b[j]!.kind));
      i++;
      j++;
    } else if (j === m || (i < n && best[i + 1]![j]! >= best[i]![j + 1]!)) {
      result.push(optional(a[i]!));
      i++;
    } else {
      result.push(optional(b[j]!));
      j++;
    }
  }
  return result;
}

/** Adjacent optional or repeated elements with the same members become one repeated element (`x~2..5`). */
function normalize(seq: Seq): Seq {
  const result: Seq = [];
  for (const e of seq) {
    const last = result[result.length - 1];
    if (last !== undefined && e.kind !== 'single' && last.kind !== 'single' && sameMembers(last.members, e.members)) {
      result[result.length - 1] = unite(last, e, 'repeated');
    } else {
      result.push(e);
    }
  }
  return result;
}

/**
 * The sequences of the alternatives of a rule as one sequence; `null` when one of them is unknown.
 *
 * The longest sequences go first. A sequence with the same length and kinds as the result so far merges position by
 * position (`(a|b)`, and `[a]` with placeholders); others are aligned to it.
 */
function merge(seqs: (Seq | null)[]): Seq | null {
  if (seqs.some((s) => s === null) || seqs.length === 0) return null;
  // Array.prototype.sort is stable, so equally long sequences keep their order.
  const ordered = (seqs as Seq[]).slice().sort((a, b) => b.length - a.length);
  let result = ordered[0]!;
  for (const seq of ordered.slice(1)) {
    const same = seq.length === result.length && seq.every((x, i) => x.kind === result[i]!.kind);
    result = same ? columns(result, seq) : align(result, seq);
  }
  return normalize(result);
}

const isSingle = (seq: Seq): boolean => seq.length === 1 && seq[0]!.kind === 'single';

const refersTo = (sym: Symbol, name: string): boolean => !sym.isTerm && sym.name === name;

class Analyzer {
  private readonly byOrigin = new Map<string, Rule[]>();
  private readonly members = new Map<string, Set<string>>();

  constructor(
    rules: readonly Rule[],
    private readonly maybePlaceholders: boolean,
  ) {
    for (const rule of rules) {
      const list = this.byOrigin.get(rule.origin.name);
      if (list === undefined) this.byOrigin.set(rule.origin.name, [rule]);
      else list.push(rule);
    }
    for (const name of this.byOrigin.keys()) if (!name.startsWith('_')) this.members.set(name, new Set());
    // Rules refer to each other, so this iterates until nothing changes.
    let changed = true;
    while (changed) {
      changed = false;
      for (const name of this.members.keys()) {
        const members = this.membersOf(name);
        if (!sameMembers(members, this.members.get(name)!)) {
          this.members.set(name, members);
          changed = true;
        }
      }
    }
  }

  static treeName(rule: Rule): string {
    return rule.alias ?? rule.options.templateSource ?? rule.origin.name;
  }

  treeNames(): string[] {
    const names = new Set<string>();
    for (const [origin, rules] of this.byOrigin) {
      if (!origin.startsWith('_')) for (const r of rules) names.add(Analyzer.treeName(r));
    }
    return sorted(names);
  }

  references(): Map<string, string[]> {
    return new Map(sorted(this.members.keys()).map((name) => [name, sorted(this.members.get(name)!)]));
  }

  /** What a reference to the rule can be as a child: its trees, or the children a `?rule` inlines. */
  private membersOf(origin: string): Set<string> {
    const members = new Set<string>();
    for (const rule of this.byOrigin.get(origin)!) {
      if (rule.alias !== undefined || !rule.options.expand1) {
        members.add(Analyzer.treeName(rule));
        continue;
      }
      const seq = this.children(rule, [], false);
      if (seq === null) {
        members.add(ANY);
        continue;
      }
      if (!isSingle(seq)) members.add(Analyzer.treeName(rule));
      // With exactly one child, the ?rule is replaced by it.
      const singles = seq.filter((e) => e.kind === 'single');
      if (singles.length === 1) for (const m of singles[0]!.members) members.add(m);
      else if (singles.length === 0) for (const e of seq) for (const m of e.members) members.add(m);
    }
    return members;
  }

  /**
   * The children of the trees the rule builds, as the tree builder's child filter leaves them; `null` when unknown.
   * With `skipFirst`, the first symbol (the recursion of a helper rule) is left out.
   */
  private children(rule: Rule, stack: readonly string[], skipFirst: boolean): Seq | null {
    const indices = this.maybePlaceholders ? rule.options.emptyIndices : [];
    const empty =
      indices.length > 0
        ? indices
            .map((b) => (b ? '1' : '0'))
            .join('')
            .split('0')
            .map((ones) => ones.length)
        : new Array<number>(rule.expansion.length + 1).fill(0);
    const keepAll = rule.options.keepAllTokens;
    const seq: Seq = [];
    let nones = 0;
    for (let i = 0; i < rule.expansion.length; i++) {
      const sym = rule.expansion[i]!;
      nones += empty[i]!;
      if (keepAll || !(sym.isTerm && sym.filterOut)) {
        for (let k = 0; k < nones; k++) seq.push(placeholder());
        nones = 0;
        if (skipFirst && i === 0) continue;
        const part = this.symbol(sym, stack);
        if (part === null) return null;
        seq.push(...part);
      }
    }
    nones += empty[rule.expansion.length]!;
    for (let k = 0; k < nones; k++) seq.push(placeholder());
    return seq;
  }

  private symbol(sym: Symbol, stack: readonly string[]): Seq | null {
    const name = sym.name;
    if (sym.isTerm) return [{ kind: 'single', members: new Set([name]), names: new Set([name]), slots: new Set([null]) }];
    if (!name.startsWith('_')) {
      const rules = this.byOrigin.get(name)!;
      const template = rules[0]!.options.templateSource;
      const slot = template === undefined && (rules[0]!.options.expand1 || rules.every((r) => r.alias !== undefined)) ? name : null;
      return [{ kind: 'single', members: new Set(this.members.get(name)!), names: new Set([template ?? name]), slots: new Set([slot]) }];
    }
    return this.splice(name, stack);
  }

  /** The children of an inlined `_rule`, which take its place among the children of the parent. */
  private splice(name: string, outer: readonly string[]): Seq | null {
    if (outer.includes(name)) return null;
    const rules = this.byOrigin.get(name)!;
    if (rules.some((r) => r.options.expand1)) return null;
    const stack = [...outer, name];
    const recursive = rules.filter((r) => r.expansion.some((s) => refersTo(s, name)));
    if (recursive.length > 0) {
      // The helper rules of `x+` and `x*`: `_r: x | _r x`.
      if (recursive.some((r) => !refersTo(r.expansion[0]!, name) || r.expansion.slice(1).some((s) => refersTo(s, name)))) return null;
      const baseRules = rules.filter((r) => !recursive.includes(r));
      if (baseRules.length === 0) return null;
      const base = merge(baseRules.map((r) => this.children(r, stack, false)));
      const step = merge(recursive.map((r) => this.children(r, stack, true)));
      if (base === null || step === null) return null;
      if (base.length === 0 && step.length === 0) return [];
      if (base.length !== 1 || step.length !== 1) return null;
      return [unite(base[0]!, step[0]!, 'repeated')];
    }
    const seq = merge(rules.map((r) => this.children(r, stack, false)));
    if (seq !== null && rules.length > 1 && seq.length === 1 && !name.startsWith('__')) {
      seq[0] = { ...seq[0]!, names: new Set([name.replace(/^_+/, '')]), slots: new Set([name]) };
    }
    return seq;
  }

  /** The shape of the trees with this name, before naming its elements. */
  shape(name: string): Seq | null | 'inlined' {
    const seqs: Seq[] = [];
    for (const [origin, rules] of this.byOrigin) {
      if (origin.startsWith('_')) continue;
      for (const rule of rules) {
        if (Analyzer.treeName(rule) !== name) continue;
        const seq = this.children(rule, [], false);
        if (seq === null) return null;
        // A ?rule with one child is replaced by it, and makes no tree.
        if (rule.options.expand1 && rule.alias === undefined && isSingle(seq)) continue;
        seqs.push(seq);
      }
    }
    if (seqs.length === 0) return 'inlined';
    const shape = merge(seqs)!;
    // Optional and repeated children are matched greedily, so they can't take a child of what follows them.
    for (let i = 0; i < shape.length; i++) {
      if (shape[i]!.kind === 'single') continue;
      for (const f of shape.slice(i + 1)) {
        if (overlaps(shape[i]!.members, f.members)) return null;
        if (f.kind === 'single') break;
      }
    }
    return shape;
  }
}
