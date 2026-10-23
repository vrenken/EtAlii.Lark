// The Earley parser, with the basic lexer or the dynamic (scannerless) lexer, and its shared packed parse forest
// (Lark's `earley`, `xearley`, `earley_common` and `earley_forest`).
//
// The core algorithm is Elizabeth Scott's: https://www.sciencedirect.com/science/article/pii/S1571066108001497

import { ErrorState, GrammarError, LexError, ParseError, TerminalsByName, UnexpectedCharacters, UnexpectedEOF, UnexpectedToken } from '../errors.js';
import { Rule, Symbol } from '../grammar.js';
import type { TerminalDef } from '../lexer.js';
import type { Callbacks } from '../parseTreeBuilder.js';
import { Flags, flagsFromLetters, Regex, width as regexWidth } from '../pyre.js';
import { Token } from '../token.js';
import { Node, Tree } from '../tree.js';
import { compareStrings } from '../utils.js';
import { expandRule, GrammarAnalyzer } from './grammarAnalysis.js';

// ---------------------------------------------------------------------------------------------------------------
//  Items and the forest
// ---------------------------------------------------------------------------------------------------------------

/** An Earley item: a rule, the position of the dot, and where the item started. */
interface Item {
  readonly rule: number;
  readonly ptr: number;
  readonly start: number;
}

const itemKey = (k: Item): string => `${k.rule},${k.ptr},${k.start}`;

/** A reference to a node of the forest: `S` (symbol), `P` (packed) or `T` (token), and its index. */
type VNode = string;
const sym = (i: number): VNode => 'S' + i;
const packed = (i: number): VNode => 'P' + i;
const tok = (i: number): VNode => 'T' + i;
const kindOf = (v: VNode): string => v[0]!;
const indexOf = (v: VNode): number => Number(v.slice(1));

interface SymbolNode {
  /** The symbol key: a rule, or an LR(0) item for intermediate nodes. */
  readonly s: string;
  readonly isIntermediate: boolean;
  /** The packed children, by how they are told apart. */
  readonly children: Map<string, number>;
  priority: number;
}

interface PackedNode {
  readonly parent: number;
  readonly rule: number;
  readonly left: VNode | null;
  readonly right: VNode | null;
  priority: number;
}

interface TokenNode {
  readonly token: Token;
  readonly priority: number;
}

class Forest {
  readonly symbols: SymbolNode[] = [];
  readonly packed: PackedNode[] = [];
  readonly tokens: TokenNode[] = [];

  /** How packed nodes are told apart: by their children, symbol nodes by identity and tokens by type and value. */
  private childKey(n: VNode | null): string {
    if (n === null) return '-';
    if (kindOf(n) === 'T') {
      const t = this.tokens[indexOf(n)]!.token;
      return 'T' + t.type + '\u0000' + t.value;
    }
    return n;
  }

  addFamily(node: number, rule: number, left: VNode | null, right: VNode | null): void {
    const key = this.childKey(left) + '\u0001' + this.childKey(right);
    const children = this.symbols[node]!.children;
    if (children.has(key)) return;
    const id = this.packed.length;
    this.packed.push({ parent: node, rule, left, right, priority: -Infinity });
    children.set(key, id);
  }

  isIntermediate(node: number): boolean {
    return this.symbols[node]!.isIntermediate;
  }

  priority(n: VNode): number {
    const i = indexOf(n);
    switch (kindOf(n)) {
      case 'S':
        return this.symbols[i]!.priority;
      case 'P':
        return this.packed[i]!.priority;
      default:
        return this.tokens[i]!.priority;
    }
  }

  /** The packed children of a symbol node, from the highest to the lowest priority. */
  sortedChildren(node: number, rules: readonly Rule[]): number[] {
    const children = [...this.symbols[node]!.children.values()];
    return children.sort((a, b) => {
      const pa = this.packed[a]!;
      const pb = this.packed[b]!;
      const emptyA = pa.left === null && pa.right === null ? 1 : 0;
      const emptyB = pb.left === null && pb.right === null ? 1 : 0;
      if (emptyA !== emptyB) return emptyA - emptyB;
      const x = -pa.priority;
      const y = -pb.priority;
      if (x < y) return -1;
      if (x > y) return 1;
      return rules[pa.rule]!.order - rules[pb.rule]!.order;
    });
  }
}

/** An Earley set: the items in insertion order with their forest node, indexed by the symbol they expect. */
class Column {
  readonly items = new Map<string, { item: Item; node: number | null }>();
  readonly byExpect = new Map<string, Item[]>();
}

type ScanBuffer = Map<string, { item: Item; node: number | null }>;

// ---------------------------------------------------------------------------------------------------------------
//  Forest visitors
// ---------------------------------------------------------------------------------------------------------------

interface ForestVisitor {
  readonly singleVisit: boolean;
  visitToken(forest: Forest, token: number): void;
  visitSymbolIn(forest: Forest, node: number): VNode[] | null;
  visitSymbolOut(forest: Forest, node: number): void;
  visitPackedIn(forest: Forest, node: number): VNode[] | null;
  visitPackedOut(forest: Forest, node: number): void;
  onCycle(node: VNode): void;
}

/** A depth-first walk of the forest that doesn't enter cycles (Lark's `ForestVisitor.visit`). */
function visitForest(v: ForestVisitor, forest: Forest, root: VNode): void {
  const visiting = new Set<VNode>();
  const visited = new Set<VNode>();
  type Entry = { node: VNode } | { list: VNode[]; pos: number };
  const stack: Entry[] = [{ node: root }];
  while (stack.length > 0) {
    const top = stack[stack.length - 1]!;
    if ('list' in top) {
      if (top.pos >= top.list.length) {
        stack.pop();
        continue;
      }
      const next = top.list[top.pos++]!;
      if (visiting.has(next)) {
        v.onCycle(next);
        continue;
      }
      stack.push({ node: next });
      continue;
    }
    const current = top.node;
    const kind = kindOf(current);
    const index = indexOf(current);
    if (kind === 'T') {
      v.visitToken(forest, index);
      stack.pop();
      continue;
    }
    if (visiting.has(current)) {
      if (kind === 'P') v.visitPackedOut(forest, index);
      else v.visitSymbolOut(forest, index);
      stack.pop();
      visiting.delete(current);
      visited.add(current);
    } else if (v.singleVisit && visited.has(current)) {
      stack.pop();
    } else {
      visiting.add(current);
      const next = kind === 'P' ? v.visitPackedIn(forest, index) : v.visitSymbolIn(forest, index);
      if (next !== null) stack.push({ list: next, pos: 0 });
    }
  }
}

const packedChildren = (p: PackedNode): VNode[] => [p.left, p.right].filter((x): x is VNode => x !== null);

/** Cascades the priorities of rules and terminals up the forest (Lark's `ForestSumVisitor`). */
class ForestSumVisitor implements ForestVisitor {
  readonly singleVisit = true;

  constructor(private readonly rules: readonly Rule[]) {}

  visitToken(): void {}

  visitSymbolIn(forest: Forest, node: number): VNode[] {
    return forest.sortedChildren(node, this.rules).map(packed);
  }

  visitSymbolOut(forest: Forest, node: number): void {
    let max = -Infinity;
    for (const p of forest.symbols[node]!.children.values()) max = Math.max(max, forest.packed[p]!.priority);
    forest.symbols[node]!.priority = max;
  }

  visitPackedIn(forest: Forest, node: number): VNode[] {
    return packedChildren(forest.packed[node]!);
  }

  visitPackedOut(forest: Forest, node: number): void {
    const p = forest.packed[node]!;
    const pr = this.rules[p.rule]!.options.priority;
    let priority = pr !== undefined && pr !== 0 && !forest.isIntermediate(p.parent) ? pr : 0;
    if (p.right !== null) priority += forest.priority(p.right);
    if (p.left !== null) priority += forest.priority(p.left);
    p.priority = priority;
  }

  onCycle(): void {}
}

/** The result of transforming a forest node: a tree or token, or the children of an intermediate node. */
type TVal = { node: Node } | { list: Node[] };

function intoNode(v: TVal): Node {
  return 'node' in v ? v.node : new Tree('_inter', v.list);
}

/** Turns the forest into a parse tree, resolving or keeping the ambiguities (Lark's `ForestToParseTree`). */
class ForestToParseTree implements ForestVisitor {
  readonly singleVisit = false;
  private readonly cache = new Map<number, TVal | null>();
  private onCycleRetreat = false;
  private cycleNode: VNode | null = null;
  private readonly successfulVisits = new Set<VNode>();
  private readonly data = new Map<VNode | null, TVal[]>();
  private readonly nodeStack: Array<VNode | null> = [];

  constructor(
    private readonly rules: readonly Rule[],
    private readonly callbacks: Callbacks,
    private readonly resolveAmbiguity: boolean,
    private readonly useCache: boolean,
  ) {}

  transform(forest: Forest, root: number): Node | undefined {
    this.nodeStack.push(null);
    this.data.set(null, []);
    visitForest(this, forest, sym(root));
    const r = this.data.get(null)!;
    return r.length === 0 ? undefined : intoNode(r[0]!);
  }

  private checkCycle(node: VNode): boolean {
    if (this.onCycleRetreat) {
      if (node === this.cycleNode || this.successfulVisits.has(node)) {
        this.cycleNode = null;
        this.onCycleRetreat = false;
      } else {
        return true;
      }
    }
    return false;
  }

  private outHelper(node: VNode, transformed: TVal | null): void {
    if (transformed !== null) {
      const parent = this.nodeStack[this.nodeStack.length - 1]!;
      this.data.get(parent)!.push(transformed);
    }
    this.data.delete(node);
  }

  private transformSymbol(forest: Forest, node: number, data: TVal[]): TVal | null {
    const v = sym(node);
    if (!this.successfulVisits.has(v)) return null;
    if (this.checkCycle(v)) return null;
    this.successfulVisits.delete(v);
    if (forest.isIntermediate(node)) {
      if (data.length > 1) {
        const children = data.map((c) => ('list' in c ? new Tree('_inter', c.list) : new Tree('_inter', [c.node])));
        return { node: new Tree('_iambig', children) };
      }
      return data[0] ?? null;
    }
    // collapse the ambiguities of the children
    const collapsed: Node[] = [];
    for (const child of data) {
      const n = intoNode(child);
      if (n instanceof Tree && n.data === '_ambig') collapsed.push(...n.children);
      else collapsed.push(n);
    }
    if (collapsed.length === 0) return null;
    if (collapsed.length === 1) return { node: collapsed[0]! };
    return { node: new Tree('_ambig', collapsed) };
  }

  private transformPacked(forest: Forest, node: number, data: TVal[]): TVal | null {
    if (this.checkCycle(packed(node))) return null;
    const p = forest.packed[node]!;
    if (this.resolveAmbiguity && this.successfulVisits.has(sym(p.parent))) return null;
    if (this.useCache) {
      const cached = this.cache.get(node);
      if (cached !== undefined) return cached;
    }
    let k = 0;
    const left = p.left !== null ? data[k++] : undefined;
    const right = data[k];
    const children: Node[] = [];
    if (left !== undefined) {
      const leftIsIntermediate = p.left !== null && kindOf(p.left) === 'S' && forest.isIntermediate(indexOf(p.left));
      if ('list' in left && leftIsIntermediate) children.push(...left.list);
      else children.push(intoNode(left));
    }
    if (right !== undefined) {
      if ('list' in right) children.push(...right.list);
      else children.push(right.node);
    }
    const transformed: TVal = forest.isIntermediate(p.parent)
      ? { list: children }
      : { node: this.callbacks.build(this.rules[p.rule]!, children) };
    if (this.useCache) this.cache.set(node, transformed);
    return transformed;
  }

  visitToken(forest: Forest, token: number): void {
    const parent = this.nodeStack[this.nodeStack.length - 1]!;
    this.data.get(parent)!.push({ node: forest.tokens[token]!.token });
  }

  visitSymbolIn(forest: Forest, node: number): VNode[] | null {
    this.nodeStack.push(sym(node));
    this.data.set(sym(node), []);
    if (this.onCycleRetreat) return null;
    return forest.sortedChildren(node, this.rules).map(packed);
  }

  visitSymbolOut(forest: Forest, node: number): void {
    this.nodeStack.pop();
    const data = this.data.get(sym(node)) ?? [];
    this.data.set(sym(node), []);
    this.outHelper(sym(node), this.transformSymbol(forest, node, data));
  }

  visitPackedIn(forest: Forest, node: number): VNode[] | null {
    this.onCycleRetreat = false;
    this.nodeStack.push(packed(node));
    this.data.set(packed(node), []);
    const p = forest.packed[node]!;
    if ((!this.resolveAmbiguity || !this.successfulVisits.has(sym(p.parent))) && (!this.useCache || !this.cache.has(node))) {
      return packedChildren(p);
    }
    return null;
  }

  visitPackedOut(forest: Forest, node: number): void {
    this.nodeStack.pop();
    const data = this.data.get(packed(node)) ?? [];
    this.data.set(packed(node), []);
    this.outHelper(packed(node), this.transformPacked(forest, node, data));
    if (!this.onCycleRetreat) this.successfulVisits.add(sym(forest.packed[node]!.parent));
  }

  onCycle(node: VNode): void {
    this.cycleNode = node;
    this.onCycleRetreat = true;
  }
}

// ---------------------------------------------------------------------------------------------------------------
//  The parser
// ---------------------------------------------------------------------------------------------------------------

/** The dynamic lexer: matches the terminals with regular expressions while parsing (Lark's `xearley`). */
interface DynamicLexer {
  readonly regexps: Map<string, Regex>;
  readonly ignore: readonly string[];
  readonly completeLex: boolean;
}

/** The input of the Earley parser: tokens from the basic lexer, or the text for the dynamic lexer. */
export type EarleyInput = { tokens: Iterator<Token> } | { text: string };

/** @internal */
export class EarleyParser {
  private readonly predictions = new Map<string, number[]>();
  private readonly hasPriorities: boolean;
  private readonly dynamic?: DynamicLexer;

  /** `dynamic` is undefined for the basic lexer, and `{ completeLex }` for the dynamic lexers. */
  constructor(
    private readonly rules: readonly Rule[],
    start: readonly string[],
    private readonly callbacks: Callbacks,
    terminals: readonly TerminalDef[],
    private readonly terminalsByName: TerminalsByName,
    ignore: readonly string[],
    regexFlags: Flags,
    dynamic: { completeLex: boolean } | undefined,
    private readonly resolveAmbiguity: boolean,
  ) {
    const analysis = new GrammarAnalyzer(rules, start);
    let hasPriorities = false;
    for (const rule of rules) {
      if (!this.predictions.has(rule.origin.key)) {
        const ptrs = expandRule(rule.origin, analysis.rules, analysis.rulesByOrigin);
        this.predictions.set(
          rule.origin.key,
          ptrs.map((p) => p.rule),
        );
      }
      if (rule.options.priority !== undefined) hasPriorities = true;
    }
    // Terminal priorities are ignored with the basic lexer
    if (dynamic !== undefined && !hasPriorities && terminals.some((t) => t.priority !== 0)) hasPriorities = true;
    this.hasPriorities = hasPriorities;

    if (dynamic !== undefined) {
      const regexps = new Map<string, Regex>();
      for (const t of terminals) {
        const regexp = t.pattern.toRegexp();
        let w: [number, number];
        try {
          w = regexWidth(regexp, flagsFromLetters(''));
        } catch {
          throw new GrammarError(`Bad regexp in token ${t.name}: ${regexp}`);
        }
        if (w[0] === 0) throw new GrammarError(`Dynamic Earley doesn't allow zero-width regexps (${t})`);
        try {
          regexps.set(t.name, new Regex(regexp, regexFlags));
        } catch (e) {
          throw new GrammarError(`Bad regexp in token ${t.name}: ${regexp} (${(e as Error).message})`);
        }
      }
      this.dynamic = { regexps, ignore, completeLex: dynamic.completeLex };
    }
  }

  private expect(k: Item): Symbol | undefined {
    return this.rules[k.rule]!.expansion[k.ptr];
  }

  private isComplete(k: Item): boolean {
    return k.ptr === this.rules[k.rule]!.expansion.length;
  }

  private s(k: Item): string {
    return this.isComplete(k) ? 'sym:' + this.rules[k.rule]!.origin.key : `lr0:${k.rule},${k.ptr}`;
  }

  private expectsTerminal(k: Item): boolean {
    return this.expect(k)?.isTerm ?? false;
  }

  private describe(k: Item): string {
    const rule = this.rules[k.rule]!;
    const names = rule.expansion.map((s) => s.name);
    return `${rule.origin.name} ::= ${names.slice(0, k.ptr).join(' ')}* ${names.slice(k.ptr).join(' ')}`;
  }

  private errorState(toScan: ScanBuffer): ErrorState {
    return { kind: 'expected', items: [...toScan.values()].map((e) => this.describe(e.item)) };
  }

  private expectedNames(toScan: ScanBuffer): string[] {
    const names: string[] = [];
    for (const { item } of toScan.values()) {
      const e = this.expect(item);
      if (e !== undefined) names.push(e.name);
    }
    return names;
  }

  /** Parses the input from the start rule and returns the tree. */
  parse(input: EarleyInput, start: string): Node {
    const startSymbol = Symbol.rule(start);
    const st = new ParseState(this);
    let toScan: ScanBuffer = new Map(); // The scan buffer. 'Q' in E.Scott's paper.

    // Predict for the start_symbol.
    for (const rule of this.predictions.get(startSymbol.key) ?? []) {
      const item = { rule, ptr: 0, start: 0 };
      if (this.expectsTerminal(item)) {
        if (!toScan.has(itemKey(item))) toScan.set(itemKey(item), { item, node: null });
      } else {
        st.addToColumn(0, item, null);
      }
    }

    toScan = 'tokens' in input ? st.parseTokens(input.tokens, toScan) : st.parseText(input.text, toScan, startSymbol);

    // If the parse was successful, the start symbol should have been completed in the last step of the Earley cycle,
    // and will be in this column. Find the item for the start_symbol, which is the root of the SPPF tree.
    const last = st.columns[st.columns.length - 1]!;
    const solutions: number[] = [];
    for (const { item, node } of last.items.values()) {
      if (
        node !== null &&
        this.isComplete(item) &&
        item.start === 0 &&
        this.rules[item.rule]!.origin.equals(startSymbol) &&
        !solutions.includes(node)
      ) {
        solutions.push(node);
      }
    }
    if (solutions.length === 0) {
      throw new UnexpectedEOF(this.expectedNames(toScan), this.errorState(toScan), this.terminalsByName);
    }
    if (solutions.length > 1) throw new ParseError('Earley should not generate multiple start symbol items! Please report this bug.');
    const solution = solutions[0]!;

    // Perform our SPPF -> AST conversion
    if (this.hasPriorities) visitForest(new ForestSumVisitor(this.rules), st.forest, sym(solution));
    // The cache is disabled when ambiguity='resolve', to prevent a tree construction bug (Lark issue #1283)
    const transformer = new ForestToParseTree(this.rules, this.callbacks, this.resolveAmbiguity, !this.resolveAmbiguity);
    return transformer.transform(st.forest, solution) ?? null;
  }

  /** @internal */
  get internals() {
    return {
      rules: this.rules,
      predictions: this.predictions,
      dynamic: this.dynamic,
      terminalsByName: this.terminalsByName,
      expect: (k: Item) => this.expect(k),
      isComplete: (k: Item) => this.isComplete(k),
      s: (k: Item) => this.s(k),
      expectsTerminal: (k: Item) => this.expectsTerminal(k),
      describe: (k: Item) => this.describe(k),
      errorState: (t: ScanBuffer) => this.errorState(t),
      expectedNames: (t: ScanBuffer) => this.expectedNames(t),
    };
  }
}

type NodeCache = Map<string, number>;

const advance = (k: Item): Item => ({ rule: k.rule, ptr: k.ptr + 1, start: k.start });

class ParseState {
  readonly columns: Column[] = [new Column()];
  readonly forest = new Forest();
  private readonly p: EarleyParser['internals'];

  constructor(parser: EarleyParser) {
    this.p = parser.internals;
  }

  addToColumn(i: number, item: Item, node: number | null): boolean {
    const column = this.columns[i]!;
    const key = itemKey(item);
    if (column.items.has(key)) return false;
    column.items.set(key, { item, node });
    const e = this.p.expect(item);
    if (e !== undefined) {
      let list = column.byExpect.get(e.key);
      if (list === undefined) {
        list = [];
        column.byExpect.set(e.key, list);
      }
      list.push(item);
    }
    return true;
  }

  private getNode(cache: NodeCache, s: string, start: number, end: number): number {
    const label = `${s}|${start}|${end}`;
    const cached = cache.get(label);
    if (cached !== undefined) return cached;
    const id = this.forest.symbols.length;
    this.forest.symbols.push({ s, isIntermediate: s.startsWith('lr0:'), children: new Map(), priority: -Infinity });
    cache.set(label, id);
    return id;
  }

  /** Adds a new item to the scan buffer, or to the column and the queue of items to process. */
  private addItem(i: number, item: Item, node: number | null, toScan: ScanBuffer, items: Item[]): void {
    if (this.p.expectsTerminal(item)) {
      const key = itemKey(item);
      if (!toScan.has(key)) toScan.set(key, { item, node });
    } else if (this.addToColumn(i, item, node)) {
      items.push(item);
    }
  }

  /** The core Earley Predictor and Completer. */
  private predictAndComplete(i: number, toScan: ScanBuffer, cache: NodeCache): void {
    const p = this.p;
    // Held Completions (H in E.Scotts paper).
    const heldCompletions = new Map<string, number>();
    // R (items) = Ei (column.items)
    const items: Item[] = [...this.columns[i]!.items.values()].map((e) => e.item);
    while (items.length > 0) {
      const item = items.pop()!;
      const rule = p.rules[item.rule]!;
      if (p.isComplete(item)) {
        // The Earley completer
        const column = this.columns[i]!;
        const entry = column.items.get(itemKey(item));
        let node = entry?.node ?? null;
        if (node === null) {
          node = this.getNode(cache, p.s(item), item.start, i);
          this.forest.addFamily(node, item.rule, null, null);
          column.items.set(itemKey(item), { item, node });
        }

        // Empty has 0 length. If we complete an empty symbol in a particular parse step, we need to be able to use
        // that same empty symbol to complete any predictions that result, that themselves require empty. Avoids
        // infinite recursion on empty symbols.
        if (item.start === i) heldCompletions.set(rule.origin.key, node);

        const startColumn = this.columns[item.start]!;
        const originators = (startColumn.byExpect.get(rule.origin.key) ?? []).map(
          (k) => [k, startColumn.items.get(itemKey(k))!.node] as const,
        );
        for (const [originator, oNode] of originators) {
          const newItem = advance(originator);
          const n = this.getNode(cache, p.s(newItem), originator.start, i);
          this.forest.addFamily(n, newItem.rule, oNode === null ? null : sym(oNode), sym(node));
          this.addItem(i, newItem, n, toScan, items);
        }
      } else {
        const expect = p.expect(item);
        if (expect === undefined || expect.isTerm) continue;
        // The Earley predictor
        const newItems: Array<[Item, number | null]> = [];
        for (const r of p.predictions.get(expect.key) ?? []) newItems.push([{ rule: r, ptr: 0, start: i }, null]);
        // Process any held completions (H).
        const held = heldCompletions.get(expect.key);
        if (held !== undefined) {
          const newItem = advance(item);
          const itemNode = this.columns[i]!.items.get(itemKey(item))?.node ?? null;
          const n = this.getNode(cache, p.s(newItem), item.start, i);
          this.forest.addFamily(n, newItem.rule, itemNode === null ? null : sym(itemNode), sym(held));
          newItems.push([newItem, n]);
        }
        for (const [newItem, node] of newItems) this.addItem(i, newItem, node, toScan, items);
      }
    }
  }

  /** The main Earley loop over the tokens of the basic lexer. */
  parseTokens(stream: Iterator<Token>, toScan: ScanBuffer): ScanBuffer {
    const p = this.p;
    let i = 0;
    let cache: NodeCache = new Map();
    for (;;) {
      const r = stream.next();
      if (r.done) break;
      const token = r.value;
      this.predictAndComplete(i, toScan, cache);

      // The core Earley Scanner.
      const nextToScan: ScanBuffer = new Map();
      this.columns.push(new Column());
      cache = new Map();
      // The terminal priorities were used up by the basic lexer already
      const tokenNode = this.forest.tokens.length;
      this.forest.tokens.push({ token, priority: 0 });

      let matched = false;
      for (const { item, node } of toScan.values()) {
        if (p.expect(item)?.name !== token.type) continue;
        matched = true;
        const newItem = advance(item);
        const n = this.getNode(cache, p.s(newItem), newItem.start, i + 1);
        this.forest.addFamily(n, item.rule, node === null ? null : sym(node), tok(tokenNode));
        if (p.expectsTerminal(newItem)) {
          const key = itemKey(newItem);
          if (!nextToScan.has(key)) nextToScan.set(key, { item: newItem, node: n });
        } else {
          this.addToColumn(i + 1, newItem, n);
        }
      }

      if (!matched) {
        const e = new UnexpectedToken(token, p.expectedNames(toScan), p.errorState(toScan), p.terminalsByName);
        e.consideredRules = [...toScan.values()].map((x) => p.describe(x.item));
        throw e;
      }

      toScan = nextToScan;
      i++;
    }
    this.predictAndComplete(i, toScan, cache);
    return toScan;
  }

  /** The main Earley loop of the dynamic lexer, over the characters of the text. */
  parseText(text: string, toScan: ScanBuffer, startSymbol: Symbol): ScanBuffer {
    const p = this.p;
    const dynamic = p.dynamic!;
    // The offset of each character (code point), and the character index of each offset
    const offsets: number[] = [];
    for (let o = 0; o < text.length; ) {
      offsets.push(o);
      o += (text.codePointAt(o) ?? 0) > 0xffff ? 2 : 1;
    }
    const n = offsets.length;
    offsets.push(text.length);
    const charOf = new Map<number, number>();
    offsets.forEach((o, c) => charOf.set(o, c));

    type Delayed = { item: Item; node: number | null; token: Token | null };
    const delayed = new Map<number, Delayed[]>();
    const delay = (at: number): Delayed[] => {
      let list = delayed.get(at);
      if (list === undefined) {
        list = [];
        delayed.set(at, list);
      }
      return list;
    };
    let textLine = 1;
    let textColumn = 1;
    let cache: NodeCache = new Map();

    const matchTerm = (name: string, pos: number): number | null => {
      const re = dynamic.regexps.get(name);
      if (re === undefined) throw new LexError(`No terminal named ${name}`);
      return re.matchAt(text, pos);
    };

    for (let i = 0; i < n; i++) {
      this.predictAndComplete(i, toScan, cache);

      // The core Earley Scanner.
      cache = new Map();
      const pos = offsets[i]!;

      // 1) Loop the expectations and ask the lexer to match. Since regexp is forward looking on the input stream, and
      // we only want to process tokens when we hit the point in the stream at which they complete, we push all
      // tokens into a buffer (delayed_matches), to be held possibly for a later parse step when we reach the point in
      // the input stream at which they complete.
      for (const { item, node } of toScan.values()) {
        const name = p.expect(item)?.name ?? '';
        const end = matchTerm(name, pos);
        if (end === null) continue;
        const value = text.slice(pos, end);
        delay(charOf.get(end)!).push({ item, node, token: new Token(name, value, pos, textLine, textColumn) });

        if (dynamic.completeLex) {
          const re = dynamic.regexps.get(name)!;
          const chars = [...value];
          for (let j = 1; j < chars.length; j++) {
            const sub = chars.slice(0, chars.length - j).join('');
            const e = re.matchAt(sub, 0);
            if (e !== null) {
              const matched = sub.slice(0, e);
              delay(i + [...matched].length).push({ item, node, token: new Token(name, matched, pos, textLine, textColumn) });
            }
          }
        }
      }

      // 3) Process any ignores. This is typically used for e.g. whitespace. We carry over any unmatched items from the
      // to_scan buffer to be matched again after the ignore. This should allow us to use ignored symbols in
      // non-terminals to implement e.g. mandatory spacing.
      for (const x of dynamic.ignore) {
        const end = matchTerm(x, pos);
        if (end === null) continue;
        const entry = delay(charOf.get(end)!);
        // Carry over any items still in the scan buffer, to past the end of the ignored items.
        for (const { item, node } of toScan.values()) entry.push({ item, node, token: null });
        // If we're ignoring up to the end of the file, carry over the start symbol if it already completed.
        for (const { item, node } of this.columns[i]!.items.values()) {
          if (p.isComplete(item) && p.rules[item.rule]!.origin.equals(startSymbol)) entry.push({ item, node, token: null });
        }
      }

      const nextToScan: ScanBuffer = new Map();
      this.columns.push(new Column());
      const charLength = offsets[i + 1]! - pos;

      // 4) Process Tokens from delayed_matches. This is the core of the Earley scanner. Create an SPPF node for each
      // Token, and create the symbol node in the SPPF tree. Advance the item that completed, and add the resulting new
      // item to either the Earley set (for processing by the completer/predictor) or the to_scan buffer for the next
      // parse step.
      let added = false;
      const ready = delayed.get(i + 1) ?? [];
      delayed.delete(i + 1);
      for (const { item, node, token } of ready) {
        let newItem = item;
        let newNode = node;
        if (token !== null) {
          token.endLine = textLine;
          token.endColumn = textColumn + charLength;
          token.endPos = offsets[i + 1];
          newItem = advance(item);
          const priority = p.terminalsByName.get(token.type)?.priority ?? 0;
          const tokenNode = this.forest.tokens.length;
          this.forest.tokens.push({ token, priority });
          newNode = this.getNode(cache, p.s(newItem), newItem.start, i + 1);
          this.forest.addFamily(newNode, item.rule, node === null ? null : sym(node), tok(tokenNode));
        }
        added = true;
        if (p.expectsTerminal(newItem)) {
          const key = itemKey(newItem);
          if (!nextToScan.has(key)) nextToScan.set(key, { item: newItem, node: newNode });
        } else {
          this.addToColumn(i + 1, newItem, newNode);
        }
      }

      if (!added && delayed.size === 0) {
        const allowed = p.expectedNames(toScan);
        const e = new UnexpectedCharacters(text, pos, textLine, textColumn, allowed, p.errorState(toScan), undefined, p.terminalsByName);
        e.consideredTokens = allowed;
        e.consideredRules = [...toScan.values()]
          .map((x) => x.item)
          .sort((a, b) => compareStrings(p.rules[a.rule]!.origin.name, p.rules[b.rule]!.origin.name))
          .map((k) => p.describe(k));
        throw e;
      }

      toScan = nextToScan;
      if (text[pos] === '\n') {
        textLine++;
        textColumn = 1;
      } else {
        textColumn += charLength;
      }
    }

    this.predictAndComplete(n, toScan, cache);
    return toScan;
  }
}
