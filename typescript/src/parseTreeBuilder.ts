// Builds the parse tree from the rules as they are matched (Lark's `parse_tree_builder`).

import type { Rule, Symbol } from './grammar.js';
import { Token } from './token.js';
import { Node, Tree } from './tree.js';

/**
 * Filters the children of a rule: drops filtered-out tokens, inlines `_rules`, and inserts the placeholders of
 * `maybePlaceholders`.
 */
class ChildFilter {
  /** (index, inline it, placeholders to insert before it) */
  constructor(
    private readonly toInclude: Array<[number, boolean, number]>,
    private readonly appendNone: number,
  ) {}

  apply(children: Node[]): Node[] {
    const filtered: Node[] = [];
    for (const [i, toExpand, addNone] of this.toInclude) {
      for (let n = 0; n < addNone; n++) filtered.push(null);
      const child = children[i] ?? null;
      if (toExpand && child instanceof Tree) {
        for (const c of child.children) filtered.push(c);
      } else {
        filtered.push(child);
      }
    }
    for (let n = 0; n < this.appendNone; n++) filtered.push(null);
    return filtered;
  }
}

function shouldExpand(sym: Symbol): boolean {
  return !sym.isTerm && sym.name.startsWith('_');
}

function createChildFilter(expansion: Symbol[], keepAllTokens: boolean, emptyIndices: boolean[] | undefined): ChildFilter | undefined {
  const hasEmpty = emptyIndices !== undefined && emptyIndices.length > 0;
  // How many placeholders to insert at each index
  const empty = hasEmpty
    ? emptyIndices
        .map((b) => (b ? '1' : '0'))
        .join('')
        .split('0')
        .map((ones) => ones.length)
    : new Array<number>(expansion.length + 1).fill(0);
  const toInclude: Array<[number, boolean, number]> = [];
  let nones = 0;
  expansion.forEach((sym, i) => {
    nones += empty[i]!;
    if (keepAllTokens || !(sym.isTerm && sym.filterOut)) {
      toInclude.push([i, shouldExpand(sym), nones]);
      nones = 0;
    }
  });
  nones += empty[expansion.length]!;
  if (hasEmpty || toInclude.length < expansion.length || toInclude.some((x) => x[1])) return new ChildFilter(toInclude, nones);
  return undefined;
}

interface Position {
  line?: number;
  column?: number;
  startPos?: number;
  endLine?: number;
  endColumn?: number;
  endPos?: number;
}

function firstPosition(children: Node[], reverse: boolean): Position | undefined {
  for (let k = 0; k < children.length; k++) {
    const c = children[reverse ? children.length - 1 - k : k]!;
    if (c instanceof Tree) {
      if (c.hasMeta) {
        const m = c.meta;
        const hasStart = m.containerStartPos !== undefined || m.containerLine !== undefined;
        const hasEnd = m.containerEndPos !== undefined || m.containerEndLine !== undefined;
        return {
          line: hasStart ? m.containerLine : m.line,
          column: hasStart ? m.containerColumn : m.column,
          startPos: hasStart ? m.containerStartPos : m.startPos,
          endLine: hasEnd ? m.containerEndLine : m.endLine,
          endColumn: hasEnd ? m.containerEndColumn : m.endColumn,
          endPos: hasEnd ? m.containerEndPos : m.endPos,
        };
      }
    } else if (c instanceof Token) {
      return c;
    }
  }
  return undefined;
}

/** @internal How to build the tree of one rule. */
export class RuleBuilder {
  constructor(
    /** The tree data: the alias, the template, or the rule name. */
    readonly name: string,
    private readonly expandSingleChild: boolean,
    private readonly filter: ChildFilter | undefined,
    private readonly propagatePositions: boolean,
    /** For ambiguous parses: the children that may be `_ambig` trees to lift into this one. */
    private readonly ambiguousExpander: number[] | undefined,
    private readonly ambiguousIntermediate: boolean,
  ) {}

  build(children: Node[]): Node {
    if (this.ambiguousIntermediate) {
      const collapsed = collapseIambig(children);
      if (collapsed !== undefined && collapsed.length > 0) {
        return new Tree(
          '_ambig',
          collapsed.map((c) => this.buildExpander(c)),
        );
      }
    }
    return this.buildExpander(children);
  }

  private buildExpander(children: Node[]): Node {
    if (this.ambiguousExpander !== undefined) {
      const ambiguous: number[] = [];
      children.forEach((child, i) => {
        if (child instanceof Tree && child.data === '_ambig') {
          if (this.ambiguousExpander!.includes(i)) ambiguous.push(i);
          child.expandKidsByData('_ambig');
        }
      });
      if (ambiguous.length > 0) {
        const options: Node[][] = children.map((c, i) => (ambiguous.includes(i) ? (c as Tree).children : [c]));
        let products: Node[][] = [[]];
        for (const opts of options) {
          const next: Node[][] = [];
          for (const p of products) for (const o of opts) next.push([...p, o]);
          products = next;
        }
        return new Tree(
          '_ambig',
          products.map((p) => this.buildPositions(p)),
        );
      }
    }
    return this.buildPositions(children);
  }

  private buildPositions(children: Node[]): Node {
    if (!this.propagatePositions) return this.buildFiltered(children);
    const first = firstPosition(children, false);
    const last = firstPosition(children, true);
    const res = this.buildFiltered(children);
    if (res instanceof Tree) {
      const meta = res.meta;
      if (first !== undefined) {
        if (meta.line === undefined) {
          meta.line = first.line;
          meta.column = first.column;
          meta.startPos = first.startPos;
          meta.empty = false;
        }
        meta.containerLine = first.line;
        meta.containerColumn = first.column;
        meta.containerStartPos = first.startPos;
      }
      if (last !== undefined) {
        if (meta.endLine === undefined) {
          meta.endLine = last.endLine;
          meta.endColumn = last.endColumn;
          meta.endPos = last.endPos;
          meta.empty = false;
        }
        meta.containerEndLine = last.endLine;
        meta.containerEndColumn = last.endColumn;
        meta.containerEndPos = last.endPos;
      }
    }
    return res;
  }

  private buildFiltered(children: Node[]): Node {
    const filtered = this.filter ? this.filter.apply(children) : children;
    if (this.expandSingleChild && filtered.length === 1) return filtered[0]!;
    return new Tree(this.name, filtered);
  }
}

/** @internal The tree builders of all rules (Lark's `ParseTreeBuilder.create_callback`). */
export class Callbacks {
  private readonly rules = new Map<string, RuleBuilder>();

  constructor(rules: readonly Rule[], propagatePositions: boolean, ambiguous: boolean, maybePlaceholders: boolean) {
    for (const rule of rules) {
      const options = rule.options;
      const keepAllTokens = options.keepAllTokens;
      const empty = maybePlaceholders ? options.emptyIndices : undefined;
      let ambiguousExpander: number[] | undefined;
      if (ambiguous) {
        const toExpand: number[] = [];
        rule.expansion.forEach((sym, i) => {
          if (keepAllTokens || (!(sym.isTerm && sym.filterOut) && shouldExpand(sym))) toExpand.push(i);
        });
        if (toExpand.length > 0) ambiguousExpander = toExpand;
      }
      this.rules.set(
        rule.key,
        new RuleBuilder(
          rule.alias ?? options.templateSource ?? rule.origin.name,
          options.expand1 && rule.alias === undefined,
          createChildFilter(rule.expansion, keepAllTokens, empty),
          propagatePositions,
          ambiguousExpander,
          ambiguous,
        ),
      );
    }
  }

  build(rule: Rule, children: Node[]): Node {
    return this.rules.get(rule.key)!.build(children);
  }
}

/** Lifts the derivations of an ambiguous intermediate node (`_iambig`) to the parent rule. */
function collapseIambig(children: Node[]): Node[][] | undefined {
  const first = children[0];
  if (!(first instanceof Tree) || first.data !== '_iambig') return undefined;
  const rest = children.slice(1);
  const result: Node[][] = [];
  for (const grandchild of first.children) {
    if (!(grandchild instanceof Tree)) continue;
    const collapsed = collapseIambig(grandchild.children);
    if (collapsed !== undefined && collapsed.length > 0) {
      for (const c of collapsed) result.push([...c, ...rest]);
    } else {
      result.push([...grandchild.children, ...rest]);
    }
  }
  return result;
}
