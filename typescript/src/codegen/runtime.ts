// The runtime of generated code: the base class of the node wrappers, the typed visitor, and the types that compute
// the visitor's handler parameters from a grammar's shapes and a map of result types. Generated files use it as
// `codegen` from the package; it isn't meant to be used directly.

import { MemoryLoader } from '../loadGrammar.js';
import { Token } from '../token.js';
import { Node, Tree } from '../tree.js';
import { ANY, PLACEHOLDER, ShapeElement } from './shapes.js';

/** An element of a shape, without its name. */
export type Element = Pick<ShapeElement, 'kind' | 'members'>;

/** The runtime model of a grammar: the elements of its trees (`null` for no fixed shape), and their node classes. */
export interface Model {
  readonly shapes: Readonly<Record<string, readonly Element[] | null>>;
  readonly nodes: Readonly<Record<string, new (tree: Tree) => NodeBase>>;
}

/** The wrapper of a child: the node of a tree with a node class, else the child itself. */
export function wrap(model: Model, child: Node): unknown {
  if (child instanceof Tree) {
    const node = model.nodes[child.data];
    return node === undefined ? child : new node(child);
  }
  return child;
}

function fits(members: readonly string[], child: Node): boolean {
  if (members.includes(ANY)) return true;
  if (child === null) return members.includes(PLACEHOLDER);
  return members.includes(child instanceof Tree ? child.data : child.type);
}

const MISMATCH = "The tree doesn't match the grammar.";

/** Where each element's children start, and where the last one ends. The children are matched greedily. */
export function spans(elements: readonly Element[], children: readonly Node[]): number[] {
  const result: number[] = [];
  let i = 0;
  for (const e of elements) {
    result.push(i);
    if (e.kind === 'single') i++;
    else if (e.kind === 'optional') {
      if (i < children.length && fits(e.members, children[i]!)) i++;
    } else while (i < children.length && fits(e.members, children[i]!)) i++;
  }
  if (i !== children.length) throw new Error(MISMATCH);
  result.push(i);
  return result;
}

/** The base class of the node wrappers of generated code: a tree, with typed getters for its children. */
export abstract class NodeBase {
  /** The tree data, the name of the rule, alias or template. */
  abstract get data(): string;
  /** The runtime model of the grammar. */
  protected abstract get __model(): Model;
  private __spans?: number[];

  constructor(
    /** The tree. */
    readonly tree: Tree,
  ) {
    // The data getter of the subclass, which is defined by then.
    const data = (this as unknown as { data: string }).data;
    if (tree.data !== data) throw new Error(`Not a '${data}' tree, but a '${tree.data}' tree.`);
  }

  private __span(index: number): readonly Node[] {
    const elements = this.__model.shapes[this.data];
    if (!Array.isArray(elements)) throw new Error(MISMATCH);
    this.__spans ??= spans(elements, this.tree.children);
    return this.tree.children.slice(this.__spans[index], this.__spans[index + 1]);
  }

  /** The child of a single element. */
  protected __one(index: number): unknown {
    return wrap(this.__model, this.__span(index)[0]!);
  }

  /** The child of an optional element, or `null`. */
  protected __optional(index: number): unknown {
    const span = this.__span(index);
    return span.length === 0 ? null : wrap(this.__model, span[0]!);
  }

  /** The children of a repeated element. */
  protected __many(index: number): unknown[] {
    return this.__span(index).map((c) => wrap(this.__model, c));
  }

  /** The children, wrapped: for trees that have no fixed shape. */
  protected __children(): unknown[] {
    return this.tree.children.map((c) => wrap(this.__model, c));
  }

  toString(): string {
    return this.tree.toString();
  }
}

/** Joins the directory of a grammar and the path of a grammar it imports, both relative to the main grammar. */
export function joinPath(directory: string, path: string): string {
  const parts: string[] = [];
  for (const p of `${directory}/${path}`.replaceAll('\\', '/').split('/')) {
    if (p === '' || p === '.') continue;
    if (p === '..' && parts.length > 0 && parts[parts.length - 1] !== '..') parts.pop();
    else parts.push(p);
  }
  return parts.join('/');
}

/** Loads the grammars a generated grammar imports, embedded by path relative to the grammar. */
export class EmbeddedLoader extends MemoryLoader {
  override load(basePath: string | undefined, grammarPath: string): [string, string] | undefined {
    if (basePath === undefined || basePath.startsWith('lark://')) return undefined;
    return super.load(basePath, joinPath(basePath, grammarPath));
  }
}

type Handler = (...args: unknown[]) => unknown;

/**
 * Visits the tree bottom-up without recursing: each tree and token with a handler gets the value its handler returns,
 * and a tree's handler gets its children's values (or, without a handler, their node wrappers). Children that the
 * shapes can't type, and those of trees without a fixed shape, aren't visited.
 */
export function visit(model: Model, handlers: object, root: Tree): unknown {
  const table = handlers as Record<string, unknown>;
  const handled = (name: string): boolean => typeof table[name] === 'function';
  const call = (name: string, args: unknown[]): unknown => (table[name] as Handler).apply(handlers, args);
  // The elements of a tree whose children are visited first.
  const elementsOf = (tree: Tree): readonly Element[] | undefined => {
    const shape = model.shapes[tree.data];
    return handled(tree.data) && Array.isArray(shape) ? shape : undefined;
  };
  const leaf = (child: Node): unknown => {
    if (child instanceof Token) return handled(child.type) ? call(child.type, [child]) : child;
    if (child instanceof Tree && handled(child.data)) return call(child.data, [wrap(model, child)]);
    return wrap(model, child);
  };

  interface Frame {
    tree: Tree;
    elements: readonly Element[];
    spans: number[];
    /** The children that are visited; the others are passed as their wrappers. */
    visited: boolean[];
    next: number;
    values: unknown[];
  }
  const frame = (tree: Tree, elements: readonly Element[]): Frame => {
    const s = spans(elements, tree.children);
    const visited = new Array<boolean>(tree.children.length);
    elements.forEach((e, k) => visited.fill(!e.members.includes(ANY), s[k], s[k + 1]));
    return { tree, elements, spans: s, visited, next: 0, values: [] };
  };
  const build = (f: Frame): unknown => {
    const args: unknown[] = [wrap(model, f.tree)];
    f.elements.forEach((e, k) => {
      const values = f.values.slice(f.spans[k], f.spans[k + 1]);
      args.push(e.kind === 'repeated' ? values : e.kind === 'optional' && values.length === 0 ? null : values[0]);
    });
    return call(f.tree.data, args);
  };

  const rootElements = elementsOf(root);
  if (rootElements === undefined) return leaf(root);
  const frames: Frame[] = [frame(root, rootElements)];
  for (;;) {
    const f = frames[frames.length - 1]!;
    if (f.next < f.tree.children.length) {
      const i = f.next++;
      const child = f.tree.children[i]!;
      if (!f.visited[i]) {
        f.values.push(wrap(model, child));
        continue;
      }
      const elements = child instanceof Tree ? elementsOf(child) : undefined;
      if (elements !== undefined) frames.push(frame(child as Tree, elements));
      else f.values.push(leaf(child));
      continue;
    }
    frames.pop();
    const value = build(f);
    if (frames.length === 0) return value;
    frames[frames.length - 1]!.values.push(value);
  }
}

// The types of the visitor, computed from the grammar's `Shapes` type (each tree's elements, or `null`), its `Nodes`
// type (the node class of each tree name, `Token` for terminals, and what `#none` and `#any` stand for) and the map
// `R` of result types.

/** `R` with only names of the grammar as keys: any other key has the type `never`. */
export type Results<R, Name extends string> = { [K in keyof R]: K extends Name ? unknown : never };

/** The type of a child that is one of the members `M`: its result type when it has one, else its node. */
export type Value<R, N, M> = M extends keyof R ? R[M] : M extends keyof N ? N[M] : never;

/** The type of the children of an element. */
export type Param<R, N, E> = E extends { kind: 'single'; members: infer M }
  ? Value<R, N, M>
  : E extends { kind: 'optional'; members: infer M }
    ? Value<R, N, M> | null
    : E extends { members: infer M }
      ? Value<R, N, M>[]
      : never;

/** The parameters of a tree's handler after the node: one per element of its shape. */
export type Params<R, N, S extends readonly unknown[]> = { [K in keyof S]: Param<R, N, S[K]> };

/** The handlers of a typed visitor: one per key of `R`, which returns that key's result type. */
export type Handlers<R, N, S> = {
  [K in keyof R]-?: K extends keyof S
    ? S[K] extends infer E extends readonly unknown[]
      ? (node: N[K & keyof N], ...children: Params<R, N, E>) => R[K]
      : (node: N[K & keyof N]) => R[K]
    : (token: Token) => R[K];
};
