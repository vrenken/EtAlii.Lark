// Builds the LALR(1) parse table, with DeRemer and Pennello's lookahead computation (Lark's `lalr_analysis`).

import { GrammarError } from '../errors.js';
import type { Rule, Symbol } from '../grammar.js';
import { endSymbol, GrammarAnalyzer, RulePtr, rulePtrKey } from './grammarAnalysis.js';

/** An action of the parse table: shift to a state, or reduce a rule. */
export type Action = { kind: 'shift'; state: number } | { kind: 'reduce'; rule: Rule };

/** The LALR(1) parse table: for each state, the action for each terminal and the goto for each rule. */
export interface ParseTable {
  states: Array<Map<string, Action>>;
  startStates: Map<string, number>;
  endStates: Map<string, number>;
}

interface ItemSet {
  kernel: RulePtr[];
  closure: RulePtr[];
  transitions: Map<string, [Symbol, number]>;
  lookaheads: Map<string, [Symbol, number[]]>;
}

/** A transition (state, symbol), keyed as `state|symbolKey`. */
type Transition = string;
const tkey = (state: number, sym: Symbol): Transition => `${state}|${sym.key}`;

function addUnique(list: RulePtr[], seen: Set<string>, items: Iterable<RulePtr>): void {
  for (const rp of items) {
    const k = rulePtrKey(rp);
    if (!seen.has(k)) {
      seen.add(k);
      list.push(rp);
    }
  }
}

/** @internal */
export function computeLalr(analyzer: GrammarAnalyzer): ParseTable {
  const rules = analyzer.lr0Rules;
  const isSatisfied = (rp: RulePtr) => rp.index === rules[rp.rule]!.expansion.length;
  const next = (rp: RulePtr) => rules[rp.rule]!.expansion[rp.index]!;

  // LR(0) states
  const states: ItemSet[] = [];
  const cache = new Map<string, number>();
  const startIds = new Map<string, number>();
  for (const [start, rule] of analyzer.lr0RootRules) {
    const kernel = [{ rule, index: 0 }];
    const closure = analyzer.expandRuleLr0(rules[rule]!.origin);
    const id = states.length;
    cache.set(kernel.map(rulePtrKey).join(';'), id);
    states.push({ kernel, closure, transitions: new Map(), lookaheads: new Map() });
    startIds.set(start, id);
  }
  const order: number[] = [];
  const visited = new Set<number>(startIds.values());
  const queue: number[] = [...startIds.values()];
  const queuedSeen = new Set<number>();
  for (let head = 0; head < queue.length; head++) {
    const state = queue[head]!;
    const bySymbol = new Map<string, [Symbol, RulePtr[]]>();
    for (const rp of states[state]!.closure) {
      if (isSatisfied(rp)) continue;
      const sym = next(rp);
      let entry = bySymbol.get(sym.key);
      if (entry === undefined) {
        entry = [sym, []];
        bySymbol.set(sym.key, entry);
      }
      entry[1].push(rp);
    }
    for (const [symKey, [sym, rps]] of bySymbol) {
      const kernelMap = new Map<string, RulePtr>();
      for (const rp of rps) {
        const k = { rule: rp.rule, index: rp.index + 1 };
        kernelMap.set(rulePtrKey(k), k);
      }
      const kernel = [...kernelMap.values()].sort((a, b) => a.rule - b.rule || a.index - b.index);
      const cacheKey = kernel.map(rulePtrKey).join(';');
      let target = cache.get(cacheKey);
      if (target === undefined) {
        const closure: RulePtr[] = [];
        const seen = new Set<string>();
        addUnique(closure, seen, kernel);
        for (const rp of kernel) {
          if (!isSatisfied(rp)) {
            const n = next(rp);
            if (!n.isTerm) addUnique(closure, seen, analyzer.expandRuleLr0(n));
          }
        }
        target = states.length;
        states.push({ kernel, closure, transitions: new Map(), lookaheads: new Map() });
        cache.set(cacheKey, target);
      }
      states[state]!.transitions.set(symKey, [sym, target]);
      if (!visited.has(target)) {
        visited.add(target);
        queue.push(target);
      }
    }
    if (!queuedSeen.has(state)) {
      queuedSeen.add(state);
      order.push(state);
    }
  }

  const isRule = (s: Symbol) => !s.isTerm && analyzer.lr0RulesByOrigin.has(s.key);

  // Reads relations
  const directlyReads = new Map<Transition, Set<string>>();
  const symbols = new Map<string, Symbol>();
  const reads = new Map<Transition, Transition[]>();
  const nonterminalTransitions: Transition[] = [];
  const end = endSymbol();
  symbols.set(end.key, end);
  for (const root of startIds.values()) {
    for (const rp of states[root]!.kernel) directlyReads.set(tkey(root, next(rp)), new Set([end.key]));
  }
  for (const state of order) {
    const seen = new Set<string>();
    for (const rp of states[state]!.closure) {
      if (isSatisfied(rp)) continue;
      const s = next(rp);
      if (!isRule(s) || seen.has(s.key)) continue;
      seen.add(s.key);
      const nt = tkey(state, s);
      nonterminalTransitions.push(nt);
      const nextState = states[state]!.transitions.get(s.key)![1];
      const dr = directlyReads.get(nt) ?? new Set<string>();
      const r = reads.get(nt) ?? [];
      const rSeen = new Set(r);
      for (const rp2 of states[nextState]!.closure) {
        if (isSatisfied(rp2)) continue;
        const s2 = next(rp2);
        if (!isRule(s2)) {
          dr.add(s2.key);
          symbols.set(s2.key, s2);
        }
        if (analyzer.nullable.has(s2.key)) {
          const t = tkey(nextState, s2);
          if (!rSeen.has(t)) {
            rSeen.add(t);
            r.push(t);
          }
        }
      }
      directlyReads.set(nt, dr);
      reads.set(nt, r);
    }
  }

  // Includes and lookback
  const includes = new Map<Transition, Transition[]>();
  const lookback = new Map<Transition, Array<[number, number]>>();
  for (const nt of nonterminalTransitions) {
    const sep = nt.indexOf('|');
    const state = Number(nt.slice(0, sep));
    const ntKey = nt.slice(sep + 1);
    const inc: Transition[] = [];
    const lb = lookback.get(nt) ?? [];
    lookback.delete(nt);
    const lbSeen = new Set(lb.map(([s, r]) => `${s},${r}`));
    for (const rp of states[state]!.closure) {
      const rule = rules[rp.rule]!;
      if (rule.origin.key !== ntKey) continue;
      let state2 = state;
      for (let i = rp.index; i < rule.expansion.length; i++) {
        const s = rule.expansion[i]!;
        const nt2 = tkey(state2, s);
        state2 = states[state2]!.transitions.get(s.key)![1];
        if (!reads.has(nt2)) continue;
        if (rule.expansion.slice(i + 1).every((x) => analyzer.nullable.has(x.key))) inc.push(nt2);
      }
      if (rp.index === 0) {
        for (const rp2 of states[state2]!.closure) {
          if (rp2.rule === rp.rule && isSatisfied(rp2) && !lbSeen.has(`${state2},${rp2.rule}`)) {
            lbSeen.add(`${state2},${rp2.rule}`);
            lb.push([state2, rp2.rule]);
          }
        }
      }
    }
    lookback.set(nt, lb);
    for (const nt2 of inc) {
      let list = includes.get(nt2);
      if (list === undefined) {
        list = [];
        includes.set(nt2, list);
      }
      if (!list.includes(nt)) list.push(nt);
    }
  }

  // Lookaheads
  const readSets = digraph(
    nonterminalTransitions,
    (x) => reads.get(x) ?? [],
    (x) => new Set(directlyReads.get(x) ?? []),
  );
  const followSets = digraph(
    nonterminalTransitions,
    (x) => includes.get(x) ?? [],
    (x) => new Set(readSets.get(x)!),
  );
  for (const [nt, lookbacks] of lookback) {
    for (const [state, rule] of lookbacks) {
      for (const s of followSets.get(nt)!) {
        const la = states[state]!.lookaheads;
        let entry = la.get(s);
        if (entry === undefined) {
          entry = [symbols.get(s)!, []];
          la.set(s, entry);
        }
        if (!entry[1].includes(rule)) entry[1].push(rule);
      }
    }
  }

  // LALR(1) states
  const indexOf = new Map(order.map((s, i) => [s, i]));
  const table: Array<Map<string, Action>> = [];
  const reduceReduce: Array<[number, Symbol, number[]]> = [];
  for (const state of order) {
    const itemset = states[state]!;
    const actions = new Map<string, Action>();
    for (const [sym, nextState] of itemset.transitions.values()) {
      actions.set(sym.name, { kind: 'shift', state: indexOf.get(nextState)! });
    }
    for (const [la, ruleSet] of itemset.lookaheads.values()) {
      let candidates = ruleSet;
      if (candidates.length > 1) {
        const byPriority = candidates
          .map((r) => [rules[r]!.options.priority ?? 0, r] as const)
          .sort((a, b) => b[0] - a[0]);
        if (byPriority[0]![0] > byPriority[1]![0]) {
          candidates = [byPriority[0]![1]];
        } else {
          reduceReduce.push([state, la, candidates]);
          continue;
        }
      }
      const rule = rules[candidates[0]!]!;
      if (actions.has(la.name)) {
        if (analyzer.strict) {
          throw new GrammarError(`Shift/Reduce conflict for terminal ${la.name}. [strict-mode]\n * ${rule}\n`);
        }
      } else {
        actions.set(la.name, { kind: 'reduce', rule });
      }
    }
    table.push(actions);
  }
  if (reduceReduce.length > 0) {
    const msgs = reduceReduce.map(([state, la, rs]) => {
      let msg = `Reduce/Reduce collision in ${la.repr()} between the following rules: ${rs.map((r) => `\n\t- ${rules[r]}`).join('')}`;
      if (analyzer.debug) {
        msg += '\n    collision occurred in state: {';
        for (const rp of states[state]!.closure) msg += `\n\t${rulePtrRepr(rules, rp)}`;
        msg += '\n    }';
      }
      return msg;
    });
    throw new GrammarError(msgs.join('\n\n'));
  }

  // The rules in the table are the LR(0) ones; the root rules never get reduced, so nothing else refers to them.
  const endStates = new Map<string, number>();
  for (const state of order) {
    for (const rp of states[state]!.closure) {
      for (const start of analyzer.lr0RootRules.keys()) {
        if (rules[rp.rule]!.origin.name === `$root_${start}` && isSatisfied(rp)) endStates.set(start, indexOf.get(state)!);
      }
    }
  }
  const startStates = new Map([...startIds].map(([k, v]) => [k, indexOf.get(v)!]));
  return { states: table, startStates, endStates };
}

function rulePtrRepr(rules: Rule[], rp: RulePtr): string {
  const r = rules[rp.rule]!;
  const before = r.expansion.slice(0, rp.index).map((s) => s.name);
  const after = r.expansion.slice(rp.index).map((s) => s.name);
  return `<${r.origin.name} : ${before.join(' ')} * ${after.join(' ')}>`;
}

function digraph(
  x: Transition[],
  r: (t: Transition) => Transition[],
  g: (t: Transition) => Set<string>,
): Map<Transition, Set<string>> {
  const f = new Map<Transition, Set<string>>();
  const s: Transition[] = [];
  const n = new Map<Transition, number>();
  for (const k of x) n.set(k, 0);
  // An iterative version of the recursive traversal, so that long chains don't overflow the stack.
  for (const start of x) {
    if (n.get(start) !== 0) continue;
    const frames: Array<{ node: Transition; d: number; edges: Transition[]; i: number }> = [];
    const enter = (node: Transition) => {
      s.push(node);
      const d = s.length;
      n.set(node, d);
      f.set(node, g(node));
      frames.push({ node, d, edges: r(node), i: 0 });
    };
    enter(start);
    while (frames.length > 0) {
      const frame = frames[frames.length - 1]!;
      if (frame.i < frame.edges.length) {
        const y = frame.edges[frame.i]!;
        if ((n.get(y) ?? 0) === 0) {
          enter(y);
          continue;
        }
        frame.i++;
        finishEdge(frame.node, y);
        continue;
      }
      // All edges done
      frames.pop();
      const x0 = frame.node;
      if (n.get(x0) === frame.d) {
        const fx = f.get(x0)!;
        for (;;) {
          const z = s.pop()!;
          n.set(z, -1);
          f.set(z, new Set(fx));
          if (z === x0) break;
        }
      }
      if (frames.length > 0) {
        const parent = frames[frames.length - 1]!;
        const y = parent.edges[parent.i]!;
        parent.i++;
        finishEdge(parent.node, y);
      }
    }
  }
  return f;

  function finishEdge(x0: Transition, y: Transition): void {
    const nx = n.get(x0)!;
    const ny = n.get(y) ?? 0;
    if (ny > 0 && ny < nx) n.set(x0, ny);
    const fy = f.get(y);
    if (fy !== undefined) {
      const fx = f.get(x0)!;
      for (const v of fy) fx.add(v);
    }
  }
}
