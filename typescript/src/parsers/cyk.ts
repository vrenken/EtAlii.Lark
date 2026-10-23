// The CYK parser, which converts the grammar to Chomsky normal form (Lark's `cyk`).

import { ParseError } from '../errors.js';
import { Rule, Symbol } from '../grammar.js';
import type { Callbacks } from '../parseTreeBuilder.js';
import type { Token } from '../token.js';
import type { Node } from '../tree.js';

/** The index of the original rule, or how the rule was created. */
type Alias = number | 'split' | 'term';

interface CnfRule {
  readonly lhs: Symbol;
  readonly rhs: Symbol[];
  readonly weight: number;
  readonly alias: Alias;
  /** For the rules that replace unit rules: the rules that were skipped. */
  readonly skipped?: CnfRule[];
}

/** Rules are equal when their lhs, rhs and skipped rules are equal. */
function cnfKey(r: CnfRule): string {
  const base = [r.lhs.key, ...r.rhs.map((s) => s.key)].join('\u0000');
  return r.skipped === undefined ? base : `${base}\u0001[${r.skipped.map(cnfKey).join('\u0002')}]`;
}

type Child = RuleNode | number;

interface RuleNode {
  readonly rule: CnfRule;
  readonly children: Child[];
  readonly weight: number;
}

function distinct(rules: CnfRule[]): CnfRule[] {
  const map = new Map<string, CnfRule>();
  for (const r of rules) {
    const k = cnfKey(r);
    if (!map.has(k)) map.set(k, r);
  }
  return [...map.values()];
}

function term(g: CnfRule[]): CnfRule[] {
  const tRules = new Map<string, CnfRule>();
  for (const r of g) {
    for (const x of r.rhs) {
      if (x.isTerm && !tRules.has(x.key)) {
        tRules.set(x.key, { lhs: Symbol.rule(`__T_${x.repr()}`), rhs: [x], weight: 0, alias: 'term' });
      }
    }
  }
  const newRules: CnfRule[] = [];
  for (const rule of g) {
    if (rule.rhs.length > 1 && rule.rhs.some((x) => x.isTerm)) {
      const newRhs = rule.rhs.map((x) => (x.isTerm ? tRules.get(x.key)!.lhs : x));
      newRules.push({ lhs: rule.lhs, rhs: newRhs, weight: rule.weight, alias: rule.alias });
      for (const [k, v] of tRules) if (rule.rhs.some((x) => x.key === k)) newRules.push(v);
    } else {
      newRules.push(rule);
    }
  }
  return distinct(newRules);
}

function split(rule: CnfRule): CnfRule[] {
  const ruleStr = `${rule.lhs.repr()}__${rule.rhs.map((x) => x.repr()).join('_')}`;
  const name = (i: number) => Symbol.rule(`__SP_${ruleStr}_${i}`);
  const n = rule.rhs.length;
  const result: CnfRule[] = [{ lhs: rule.lhs, rhs: [rule.rhs[0]!, name(1)], weight: rule.weight, alias: rule.alias }];
  for (let i = 1; i < n - 2; i++) result.push({ lhs: name(i), rhs: [rule.rhs[i]!, name(i + 1)], weight: 0, alias: 'split' });
  result.push({ lhs: name(n - 2), rhs: rule.rhs.slice(n - 2), weight: 0, alias: 'split' });
  return result;
}

function bin(g: CnfRule[]): CnfRule[] {
  const newRules: CnfRule[] = [];
  for (const rule of g) {
    if (rule.rhs.length > 2) newRules.push(...split(rule));
    else newRules.push(rule);
  }
  return distinct(newRules);
}

function buildUnitSkipRule(unitRule: CnfRule, targetRule: CnfRule): CnfRule {
  const skipped = [...(unitRule.skipped ?? []), targetRule, ...(targetRule.skipped ?? [])];
  return { lhs: unitRule.lhs, rhs: targetRule.rhs, weight: unitRule.weight + targetRule.weight, alias: unitRule.alias, skipped };
}

function unit(g: CnfRule[]): CnfRule[] {
  for (;;) {
    const nt = g.find((r) => r.rhs.length === 1 && !r.rhs[0]!.isTerm);
    if (nt === undefined) return g;
    const ntKey = cnfKey(nt);
    const newRules = g.filter((x) => cnfKey(x) !== ntKey);
    for (const r of g) if (r.lhs.equals(nt.rhs[0]!)) newRules.push(buildUnitSkipRule(nt, r));
    g = distinct(newRules);
  }
}

function unrollUnitSkipRule(lhs: Symbol, origRhs: Symbol[], skipped: CnfRule[], children: Child[], weight: number, alias: Alias): RuleNode {
  if (skipped.length === 0) return { rule: { lhs, rhs: origRhs, weight, alias }, children, weight };
  const first = skipped[0]!;
  const w = weight - first.weight;
  const inner = unrollUnitSkipRule(first.lhs, origRhs, skipped.slice(1), children, first.weight, first.alias);
  return { rule: { lhs, rhs: [first.lhs], weight: w, alias }, children: [inner], weight: w };
}

/** Reverts a parse tree to the form of the original grammar. */
function revertCnf(node: Child): Child {
  if (typeof node === 'number') return node;
  // Reverts TERM rule.
  if (node.rule.lhs.name.startsWith('__T_')) return node.children[0]!;
  const children: Child[] = [];
  for (const child of node.children.map(revertCnf)) {
    // Reverts BIN rule.
    if (typeof child !== 'number' && child.rule.lhs.name.startsWith('__SP_')) children.push(...child.children);
    else children.push(child);
  }
  // Reverts UNIT rule.
  const r = node.rule;
  if (r.skipped !== undefined) return unrollUnitSkipRule(r.lhs, r.rhs, r.skipped, children, r.weight, r.alias);
  return { rule: r, children, weight: 0 };
}

/** @internal */
export class CykParser {
  private readonly terminalRules = new Map<string, CnfRule[]>();
  private readonly nonterminalRules = new Map<string, CnfRule[]>();

  constructor(
    private readonly rules: readonly Rule[],
    private readonly callbacks: Callbacks,
  ) {
    const cnf = unit(
      bin(term(distinct(rules.map((r, i) => ({ lhs: r.origin, rhs: r.expansion, weight: r.options.priority ?? 0, alias: i }))))),
    );
    for (const r of cnf) {
      if (r.rhs.length !== 1 && r.rhs.length !== 2) throw new ParseError("CYK doesn't support empty rules");
      if (r.rhs.length === 1 && r.rhs[0]!.isTerm) {
        push(this.terminalRules, r.rhs[0]!.key, r);
      } else if (r.rhs.length === 2 && r.rhs.every((x) => !x.isTerm)) {
        push(this.nonterminalRules, `${r.rhs[0]!.key}\u0000${r.rhs[1]!.key}`, r);
      } else {
        throw new ParseError(`Invalid CNF rule: ${r.lhs.repr()} -> [${r.rhs.map((x) => x.repr()).join(', ')}]`);
      }
    }
  }

  parse(tokens: Token[], start: string): Node {
    const startSym = Symbol.rule(start);
    const n = tokens.length;
    const table = new Map<string, Map<string, CnfRule>>();
    const trees = new Map<string, Map<string, RuleNode>>();
    const cell = <V>(m: Map<string, Map<string, V>>, k: string): Map<string, V> => {
      let c = m.get(k);
      if (c === undefined) {
        c = new Map();
        m.set(k, c);
      }
      return c;
    };

    // Populate base case with existing terminal production rules
    tokens.forEach((w, i) => {
      for (const rules of this.terminalRules.values()) {
        if (rules[0]!.rhs[0]!.name !== w.type) continue;
        for (const rule of rules) {
          const span = `${i},${i}`;
          const t = cell(table, span);
          const k = cnfKey(rule);
          if (!t.has(k)) t.set(k, rule);
          const tc = cell(trees, span);
          const existing = tc.get(rule.lhs.key);
          if (existing === undefined || rule.weight < existing.weight) {
            tc.set(rule.lhs.key, { rule, children: [i], weight: rule.weight });
          }
        }
      }
    });

    // Iterate over lengths of sub-sentences
    for (let l = 2; l <= n; l++) {
      // Iterate over sub-sentences with the given length
      for (let i = 0; i < n - l + 1; i++) {
        // Choose partition of the sub-sentence in [1, l)
        for (let p = i + 1; p < i + l; p++) {
          const span1 = `${i},${p - 1}`;
          const span2 = `${p},${i + l - 1}`;
          const c1 = [...(table.get(span1)?.values() ?? [])];
          const c2 = [...(table.get(span2)?.values() ?? [])];
          for (const r1 of c1) {
            for (const r2 of c2) {
              const rules = this.nonterminalRules.get(`${r1.lhs.key}\u0000${r2.lhs.key}`);
              if (rules === undefined) continue;
              for (const rule of rules) {
                const target = `${i},${i + l - 1}`;
                const t = cell(table, target);
                const k = cnfKey(rule);
                if (!t.has(k)) t.set(k, rule);
                const r1Tree = trees.get(span1)!.get(r1.lhs.key)!;
                const r2Tree = trees.get(span2)!.get(r2.lhs.key)!;
                const total = rule.weight + r1Tree.weight + r2Tree.weight;
                const tc = cell(trees, target);
                const existing = tc.get(rule.lhs.key);
                if (existing === undefined || total < existing.weight) {
                  tc.set(rule.lhs.key, { rule, children: [r1Tree, r2Tree], weight: total });
                }
              }
            }
          }
        }
      }
    }

    // Check if the parse succeeded.
    const last = `0,${n - 1}`;
    const ok = n > 0 && [...(table.get(last)?.values() ?? [])].some((r) => r.lhs.equals(startSym));
    if (!ok) throw new ParseError('Parsing failed.');
    const reverted = revertCnf(trees.get(last)!.get(startSym.key)!);
    return typeof reverted === 'number' ? tokens[reverted]! : this.toNode(reverted, tokens);
  }

  /** Builds the tree of the original grammar, with the tree builders of its rules. */
  private toNode(rn: RuleNode, tokens: Token[]): Node {
    const index = rn.rule.alias;
    if (typeof index !== 'number') throw new Error('reverted rules are original rules');
    const children = rn.children.map((c) => (typeof c === 'number' ? tokens[c]! : this.toNode(c, tokens)));
    return this.callbacks.build(this.rules[index]!, children);
  }
}

function push<V>(map: Map<string, V[]>, key: string, value: V): void {
  const list = map.get(key);
  if (list === undefined) map.set(key, [value]);
  else list.push(value);
}
