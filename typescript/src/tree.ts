import { Token } from './token.js';
import { repr } from './utils.js';
import type { Symbol } from './grammar.js';

/**
 * The position of a tree in the text, set when `propagatePositions` is enabled (Lark's `Meta`). Offsets and columns
 * are counted as for {@link Token}.
 */
export class Meta {
  /** True until a position is set. */
  empty = true;
  line?: number;
  column?: number;
  startPos?: number;
  endLine?: number;
  endColumn?: number;
  endPos?: number;
  /** The position of the tree including the tokens that were filtered out of it. */
  containerLine?: number;
  containerColumn?: number;
  containerStartPos?: number;
  containerEndLine?: number;
  containerEndColumn?: number;
  containerEndPos?: number;
  /** The expansion that matched. */
  origExpansion?: Symbol[];
  matchTree = false;
}

/**
 * A child of a {@link Tree}: a subtree, a token, or `null`, the placeholder that `maybePlaceholders` puts in place of
 * a missing `[optional]` item (Python Lark's `None`).
 */
export type Node = Tree | Token | null;

/**
 * A parse tree: the name of a rule or alias, and its children (Lark's `Tree`).
 */
export class Tree {
  /** The name of the rule or alias. */
  data: string;
  /** The matched subrules, tokens and placeholders. */
  children: Node[];
  private _meta?: Meta;

  constructor(data: string, children: Node[], meta?: Meta) {
    this.data = data;
    this.children = children;
    this._meta = meta;
  }

  /** The position of the tree (empty unless `propagatePositions` was enabled). */
  get meta(): Meta {
    if (this._meta === undefined) this._meta = new Meta();
    return this._meta;
  }

  set meta(meta: Meta) {
    this._meta = meta;
  }

  /** Whether a position was set. */
  get hasMeta(): boolean {
    return this._meta !== undefined && !this._meta.empty;
  }

  /** An indented representation of the tree, great for debugging (Lark's `pretty`). */
  pretty(indent = '  '): string {
    const out: string[] = [];
    const stack: Array<[Node, number]> = [[this, 0]];
    while (stack.length > 0) {
      const [node, level] = stack.pop()!;
      if (!(node instanceof Tree)) {
        out.push(indent.repeat(level) + String(node === null ? 'None' : node.value) + '\n');
        continue;
      }
      out.push(indent.repeat(level) + node.data);
      if (node.children.length === 1 && !(node.children[0] instanceof Tree)) {
        const c = node.children[0]!;
        out.push('\t' + (c === null ? 'None' : c.value) + '\n');
      } else {
        out.push('\n');
        for (let i = node.children.length - 1; i >= 0; i--) stack.push([node.children[i]!, level + 1]);
      }
    }
    return out.join('');
  }

  /** All subtrees, bottom-up: every tree comes after its children (Lark's `iter_subtrees`). */
  iterSubtrees(): Tree[] {
    const queue: Tree[] = [this];
    const seen = new Set<Tree>();
    const ordered: Tree[] = [];
    for (let i = 0; i < queue.length; i++) {
      const subtree = queue[i]!;
      if (seen.has(subtree)) continue;
      seen.add(subtree);
      ordered.push(subtree);
      for (let j = subtree.children.length - 1; j >= 0; j--) {
        const c = subtree.children[j];
        if (c instanceof Tree && !seen.has(c)) queue.push(c);
      }
    }
    return ordered.reverse();
  }

  /** All subtrees, top-down, in the order of {@link Tree.pretty} (Lark's `iter_subtrees_topdown`). */
  iterSubtreesTopdown(): Tree[] {
    const stack: Tree[] = [this];
    const result: Tree[] = [];
    while (stack.length > 0) {
      const t = stack.pop()!;
      result.push(t);
      for (let j = t.children.length - 1; j >= 0; j--) {
        const c = t.children[j];
        if (c instanceof Tree) stack.push(c);
      }
    }
    return result;
  }

  /** The subtrees for which the predicate holds, bottom-up (Lark's `find_pred`). */
  findPred(pred: (tree: Tree) => boolean): Tree[] {
    return this.iterSubtrees().filter(pred);
  }

  /** The subtrees with the given data, bottom-up (Lark's `find_data`). */
  findData(data: string): Tree[] {
    return this.findPred((t) => t.data === data);
  }

  /** All non-tree children, in order, for which the predicate holds (Lark's `scan_values`). */
  scanValues(pred: (value: Token | null) => boolean): Array<Token | null> {
    const result: Array<Token | null> = [];
    const stack: Node[] = [...this.children].reverse();
    while (stack.length > 0) {
      const c = stack.pop()!;
      if (c instanceof Tree) {
        for (let j = c.children.length - 1; j >= 0; j--) stack.push(c.children[j]!);
      } else if (pred(c)) {
        result.push(c);
      }
    }
    return result;
  }

  /** The tokens of the given type, in order (Lark's `find_token`). */
  findToken(type: string): Token[] {
    return this.scanValues((v) => v instanceof Token && v.type === type) as Token[];
  }

  /** Inlines the children that are trees with any of the given data. Returns whether anything changed. */
  expandKidsByData(...data: string[]): boolean {
    let changed = false;
    for (let i = this.children.length - 1; i >= 0; i--) {
      const child = this.children[i];
      if (child instanceof Tree && data.includes(child.data)) {
        this.children.splice(i, 1, ...child.children);
        changed = true;
      }
    }
    return changed;
  }

  /** Whether the other tree has the same data and children (positions are not compared). */
  equals(other: Tree): boolean {
    return nodesEqual(this, other);
  }

  /** Lark's `repr`: `Tree('data', [children])`. */
  repr(): string {
    return reprNode(this);
  }

  toString(): string {
    return reprNode(this);
  }
}

/** Whether two nodes are equal: trees by data and children, tokens by type and value. */
export function nodesEqual(a: Node, b: Node): boolean {
  const stack: Array<[Node, Node]> = [[a, b]];
  while (stack.length > 0) {
    const [x, y] = stack.pop()!;
    if (x === null || y === null) {
      if (x !== y) return false;
    } else if (x instanceof Token) {
      if (!(y instanceof Token) || !x.equals(y)) return false;
    } else {
      if (!(y instanceof Tree) || x.data !== y.data || x.children.length !== y.children.length) return false;
      for (let i = 0; i < x.children.length; i++) stack.push([x.children[i]!, y.children[i]!]);
    }
  }
  return true;
}

/** Lark's `repr` of a node. */
export function reprNode(node: Node): string {
  if (node === null) return 'None';
  if (node instanceof Token) return node.repr();
  return `Tree(${repr(node.data)}, [${node.children.map(reprNode).join(', ')}])`;
}
