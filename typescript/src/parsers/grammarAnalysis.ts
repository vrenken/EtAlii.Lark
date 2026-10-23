// FIRST/FOLLOW/NULLABLE sets and LR(0) items (Lark's `grammar_analysis`).

import { GrammarError } from '../errors.js';
import { Rule, ruleOptions, Symbol } from '../grammar.js';
import { bfs } from '../utils.js';

/** An LR(0) item: a rule (an index into the rules) with a dot in its expansion. */
export interface RulePtr {
  readonly rule: number;
  readonly index: number;
}

export const rulePtrKey = (rp: RulePtr): string => `${rp.rule},${rp.index}`;

export function rootSymbol(start: string): Symbol {
  return Symbol.rule(`$root_${start}`);
}

export const END = '$END';

export function endSymbol(): Symbol {
  return Symbol.terminal(END);
}

function byOrigin(rules: readonly Rule[]): Map<string, number[]> {
  const map = new Map<string, number[]>();
  rules.forEach((r, i) => {
    let list = map.get(r.origin.key);
    if (list === undefined) {
      list = [];
      map.set(r.origin.key, list);
    }
    list.push(i);
  });
  return map;
}

/** All the initial items reachable from the rule (Lark's `expand_rule`), in order and without duplicates. */
export function expandRule(source: Symbol, rules: readonly Rule[], origins: Map<string, number[]>): RulePtr[] {
  const ptrs: RulePtr[] = [];
  const seen = new Set<number>();
  bfs(
    [source],
    (rule) => {
      const next: Symbol[] = [];
      for (const r of origins.get(rule.key) ?? []) {
        if (!seen.has(r)) {
          seen.add(r);
          ptrs.push({ rule: r, index: 0 });
        }
        const first = rules[r]!.expansion[0];
        if (first !== undefined && !first.isTerm) next.push(first);
      }
      return next;
    },
    (s) => s.key,
  );
  return ptrs;
}

/** @internal */
export class GrammarAnalyzer {
  /** The rules of the grammar, followed by the root rules (`$root_start: start $END`). */
  readonly rules: Rule[];
  readonly rulesByOrigin: Map<string, number[]>;
  /** The rules without `$END` in the root rules, for the LR(0) automaton. */
  readonly lr0Rules: Rule[];
  readonly lr0RulesByOrigin: Map<string, number[]>;
  readonly lr0RootRules: Map<string, number>;
  readonly nullable: Set<string>;

  constructor(
    rules: readonly Rule[],
    start: readonly string[],
    readonly debug = false,
    readonly strict = false,
  ) {
    const all = [...rules];
    for (const s of start) all.push(new Rule(rootSymbol(s), [Symbol.rule(s), endSymbol()], 0, undefined, ruleOptions()));
    const seen = new Set<string>();
    const duplicates: string[] = [];
    for (const r of all) {
      if (seen.has(r.key)) duplicates.push(r.toString());
      seen.add(r.key);
    }
    if (duplicates.length > 0) throw new GrammarError(`Rules defined twice: ${duplicates.join(', ')}`);
    this.rulesByOrigin = byOrigin(all);
    for (const r of all) {
      for (const sym of r.expansion) {
        if (!sym.isTerm && !this.rulesByOrigin.has(sym.key)) throw new GrammarError(`Using an undefined rule: ${sym.repr()}`);
      }
    }
    const lr0 = [...rules];
    this.lr0RootRules = new Map();
    for (const s of start) {
      this.lr0RootRules.set(s, lr0.length);
      lr0.push(new Rule(rootSymbol(s), [Symbol.rule(s)], 0, undefined, ruleOptions()));
    }
    this.rules = all;
    this.lr0Rules = lr0;
    this.lr0RulesByOrigin = byOrigin(lr0);
    this.nullable = calculateNullable(all);
  }

  expandRuleLr0(source: Symbol): RulePtr[] {
    return expandRule(source, this.lr0Rules, this.lr0RulesByOrigin);
  }
}

/** The NULLABLE set (the keys of the rules that can match nothing). */
export function calculateNullable(rules: readonly Rule[]): Set<string> {
  const nullable = new Set<string>();
  let changed = true;
  while (changed) {
    changed = false;
    for (const rule of rules) {
      if (!nullable.has(rule.origin.key) && rule.expansion.every((s) => nullable.has(s.key))) {
        nullable.add(rule.origin.key);
        changed = true;
      }
    }
  }
  return nullable;
}
