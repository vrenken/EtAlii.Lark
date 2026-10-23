// Visitors and transformers (Lark's `Visitor` and `Transformer`).

import { VisitError } from './errors.js';
import { Token } from './token.js';
import { Meta, Node, Tree } from './tree.js';

/** Returned by a transformer callback to remove the node from its parent's children (Lark's `Discard`). */
export const Discard: unique symbol = Symbol('Discard');
export type Discard = typeof Discard;

type Callback = (children: unknown[], meta: Meta) => unknown;
type TokenCallback = (token: Token) => unknown;

const RESERVED = new Set(['constructor', 'transform', 'default', 'defaultToken', 'missing', 'visitTokens', 'call', 'transformTree', 'transformToken']);

function method(target: object, name: string): ((...args: never[]) => unknown) | undefined {
  if (RESERVED.has(name)) return undefined;
  const f = (target as Record<string, unknown>)[name];
  return typeof f === 'function' ? (f as (...args: never[]) => unknown) : undefined;
}

/**
 * Turns a tree into a value, bottom-up: each tree is transformed after its children (Lark's `Transformer`).
 *
 * Define a method for each rule (or alias) you want to transform, named after it, which gets the transformed
 * children and the tree's `meta`. With `visitTokens` (the default), a method named after a terminal transforms its
 * tokens. Trees without a method are rebuilt with their transformed children ({@link Transformer.default}).
 *
 * A callback that throws stops the transformation with a {@link VisitError} that names the rule or terminal.
 * Trees can be arbitrarily deep: the transformation doesn't recurse, so it doesn't overflow the stack.
 *
 * ```ts
 * class Sum extends Transformer<number> {
 *   start(children: number[]) { return children.reduce((a, b) => a + b, 0); }
 *   NUMBER(token: Token) { return Number(token.value); }
 * }
 * ```
 */
export class Transformer<T = unknown> {
  /** Whether tokens are passed to the methods named after their terminal. */
  visitTokens = true;

  /** Transforms a tree, token or placeholder. */
  transform(node: Node): T {
    type Work = { visit: Node } | { build: Tree; count: number };
    const work: Work[] = [{ visit: node }];
    const values: unknown[] = [];
    while (work.length > 0) {
      const item = work.pop()!;
      if ('build' in item) {
        const tree = item.build;
        const children = values.splice(values.length - item.count, item.count).filter((v) => v !== Discard);
        values.push(this.call(tree.data, () => this.transformTree(tree, children), tree));
        continue;
      }
      const n = item.visit;
      if (n instanceof Tree) {
        work.push({ build: n, count: n.children.length });
        for (let i = n.children.length - 1; i >= 0; i--) work.push({ visit: n.children[i]! });
      } else if (n instanceof Token) {
        values.push(this.visitTokens ? this.call(n.type, () => this.transformToken(n), n) : n);
      } else {
        values.push(this.missing());
      }
    }
    return values.pop() as T;
  }

  private call(rule: string, f: () => unknown, obj: unknown): unknown {
    try {
      return f();
    } catch (e) {
      if (e instanceof VisitError) throw e;
      throw new VisitError(rule, obj, e);
    }
  }

  private transformTree(tree: Tree, children: unknown[]): unknown {
    const f = method(this, tree.data) as Callback | undefined;
    return f !== undefined ? f.call(this, children, tree.meta) : this.default(tree.data, children, tree.meta);
  }

  private transformToken(token: Token): unknown {
    const f = method(this, token.type) as TokenCallback | undefined;
    return f !== undefined ? f.call(this, token) : this.defaultToken(token);
  }

  /** Transforms a tree that has no method: by default, rebuilds it with the transformed children (`__default__`). */
  default(data: string, children: unknown[], meta: Meta): unknown {
    return new Tree(data, children as Node[], meta);
  }

  /** Transforms a token that has no method: by default, keeps it (`__default_token__`). */
  defaultToken(token: Token): unknown {
    return token;
  }

  /** Transforms the placeholder of an unmatched `[optional]` (see `maybePlaceholders`). By default, `null`. */
  missing(): unknown {
    return null;
  }
}

/**
 * Walks a tree and calls the method named after each subtree's rule, without changing the tree (Lark's `Visitor`).
 * Subtrees without a method go to {@link Visitor.default}.
 */
export class Visitor {
  /** Visits the subtrees bottom-up: each tree after its children, in Lark's order (`visit`). */
  visit(tree: Tree): Tree {
    for (const subtree of tree.iterSubtrees()) this.callUserFunc(subtree);
    return tree;
  }

  /** Visits the subtrees top-down: each tree before its children (`visit_topdown`). */
  visitTopdown(tree: Tree): Tree {
    for (const subtree of tree.iterSubtreesTopdown()) this.callUserFunc(subtree);
    return tree;
  }

  private callUserFunc(tree: Tree): void {
    const f = RESERVED_VISITOR.has(tree.data) ? undefined : (this as unknown as Record<string, unknown>)[tree.data];
    if (typeof f === 'function') (f as (t: Tree) => void).call(this, tree);
    else this.default(tree);
  }

  /** Called for the subtrees that have no method. Does nothing by default (`__default__`). */
  default(_tree: Tree): void {}
}

const RESERVED_VISITOR = new Set(['constructor', 'visit', 'visitTopdown', 'default', 'callUserFunc']);
