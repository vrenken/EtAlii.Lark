// The code generator: writes a typed TypeScript module for a grammar, with its parser, name constants, a node class
// for each tree with typed getters for its children, and a typed visitor. The types come from the shapes of the
// trees, which are derived from the compiled grammar (see `shapes.ts`).

import { ConfigurationError } from '../errors.js';
import { Lark, LexerKind, ParserKind } from '../lark.js';
import type { GrammarLoader } from '../loadGrammar.js';
import { joinPath } from './runtime.js';
import { ANY, PLACEHOLDER, referencesOf, Shape, ShapeElement, shapesOf } from './shapes.js';

/** The options of {@link generate}: the options of the parser, and where the generated code finds the package. */
export interface GenerateOptions {
  parser?: ParserKind;
  lexer?: Exclude<LexerKind, object>;
  /** The start rule or rules. Defaults to `start`. */
  start?: string | string[];
  maybePlaceholders?: boolean;
  keepAllTokens?: boolean;
  /**
   * Reads the grammars the grammar imports with relative imports (`%import .other.rule`), by path relative to the
   * grammar. The generated code embeds them. Without it, relative imports fail.
   */
  readImport?: (path: string) => string | undefined;
  /** The name of the grammar in error messages, such as `calc.lark`. */
  sourceName?: string;
  /** The module the generated code imports the package from. Defaults to `@etalii/lark`. */
  packageName?: string;
}

/** Generates a typed TypeScript module for the grammar. Throws the grammar's errors. */
export function generate(grammar: string, options: GenerateOptions = {}): string {
  if (typeof options.lexer === 'object') throw new ConfigurationError("Code generation doesn't support custom lexers.");
  const imports: Record<string, string> = {};
  const readImport = options.readImport;
  // Relative imports are loaded by path relative to the grammar, as the generated code loads them.
  const recorder: GrammarLoader = {
    load(basePath, grammarPath) {
      if (basePath === undefined || basePath.startsWith('lark://') || readImport === undefined) return undefined;
      const path = joinPath(basePath, grammarPath);
      const text = readImport(path);
      if (text === undefined) return undefined;
      imports[path] = text;
      return [path, text];
    },
  };
  const start = options.start === undefined ? ['start'] : typeof options.start === 'string' ? [options.start] : [...options.start];
  const lark = new Lark(grammar, {
    parser: options.parser ?? 'earley',
    lexer: options.lexer ?? 'auto',
    start,
    maybePlaceholders: options.maybePlaceholders ?? true,
    keepAllTokens: options.keepAllTokens ?? false,
    sourcePath: options.sourceName ?? 'grammar.lark',
    importPaths: [recorder],
  });
  return new Emitter(grammar, options, start, lark, imports).emit();
}

/** `add_expr` → `AddExpr`, `NUMBER` → `Number`, `__ANON_0` → `Anon0`. */
export function pascal(name: string): string {
  const result = name
    .split(/[^A-Za-z0-9]+/)
    .filter((p) => p !== '')
    .map((p) => p[0]!.toUpperCase() + p.slice(1).toLowerCase())
    .join('');
  return result === '' || /^[0-9]/.test(result) ? `X${result}` : result;
}

/** `order_line` → `orderLine`, `TEXT` → `text`. */
export function camel(name: string): string {
  const p = pascal(name);
  return p[0]!.toLowerCase() + p.slice(1);
}

/** `line` → `lines`, `address` → `addresses`, `entry` → `entries`. */
export function plural(name: string): string {
  if (/(s|x|z|ch|sh)$/.test(name)) return `${name}es`;
  if (/[^aeou]y$/.test(name)) return `${name.slice(0, -1)}ies`;
  return `${name}s`;
}

/** Names for the elements of a shape: camel case, plural for repeated elements, unique, and not one of `reserved`. */
export function elementNames(elements: readonly ShapeElement[], reserved: readonly string[]): string[] {
  const used = new Set(reserved);
  return elements.map((e) => {
    const base = e.kind === 'repeated' ? plural(camel(e.name)) : camel(e.name);
    let name = base;
    for (let n = 2; used.has(name); n++) name = `${base}${n}`;
    used.add(name);
    return name;
  });
}

const KEYWORDS = new Set(
  (
    'break case catch class const continue debugger default delete do else enum export extends false finally for function if ' +
    'import in instanceof new null return super switch this throw true try typeof var void while with yield let static ' +
    'implements interface package private protected public await'
  ).split(' '),
);

const IDENTIFIER = /^[A-Za-z_$][A-Za-z0-9_$]*$/;

/** A property key: the name, or the name quoted. */
const key = (name: string): string => (IDENTIFIER.test(name) ? name : JSON.stringify(name));

const quote = (name: string): string => `'${name.replaceAll('\\', '\\\\').replaceAll("'", "\\'")}'`;

/** A tuple label: the name, unless it is a keyword. */
const label = (name: string): string => (KEYWORDS.has(name) ? `${name}_` : name);

const article = (name: string): string => (/^[aeiou]/i.test(name) ? 'An' : 'A');

/** The getters and methods of a node class that elements can't be named. */
const RESERVED = ['data', 'tree', 'children', 'toString', 'constructor'];

/** The type of any child: what `#any` stands for. */
const ANY_TYPE = 'Node | __lark.Tree | __lark.Token | null';

class Emitter {
  private readonly lines: string[] = [];
  private indent = 0;
  private readonly shapes: Map<string, Shape>;
  private readonly references: Map<string, string[]>;
  /** The trees that have a node class, and its name. */
  private readonly nodes = new Map<string, string>();
  private readonly terminals: string[];

  constructor(
    private readonly grammar: string,
    private readonly options: GenerateOptions,
    private readonly start: string[],
    private readonly lark: Lark,
    private readonly imports: Record<string, string>,
  ) {
    this.shapes = shapesOf(lark);
    this.references = referencesOf(lark);
    const used = new Set(['Node']);
    for (const [name, shape] of this.shapes) {
      if (shape === 'inlined') continue;
      let node = `${pascal(name)}Node`;
      for (let n = 2; used.has(node); n++) node = `${pascal(name)}Node${n}`;
      used.add(node);
      this.nodes.set(name, node);
    }
    const terminals = new Set(lark.terminals.map((t) => t.name));
    for (const shape of this.shapes.values()) {
      if (!Array.isArray(shape)) continue;
      for (const e of shape) for (const m of e.members) if (!m.startsWith('#') && !this.shapes.has(m)) terminals.add(m);
    }
    this.terminals = [...terminals].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
  }

  private line(line = ''): void {
    if (/^[}\])]/.test(line)) this.indent--;
    this.lines.push(line === '' ? '' : '  '.repeat(this.indent) + line);
    if (/[{[(]$/.test(line)) this.indent++;
  }

  emit(): string {
    const pkg = this.options.packageName ?? '@etalii/lark';
    this.line("// Generated by etalii-lark's code generator (`npx etalii-lark generate`). Don't edit.");
    this.line('/* eslint-disable */');
    this.line('');
    this.line(`import * as __lark from ${quote(pkg)};`);
    this.line('');
    this.emitParser();
    this.emitConstants();
    this.emitNodes();
    this.emitTypes();
    this.emitVisitor();
    return this.lines.join('\n') + '\n';
  }

  private emitParser(): void {
    const o = this.lark.options;
    this.line('/** The grammar. */');
    this.line('export const GRAMMAR: string = [');
    for (const l of this.grammar.split('\n')) this.line(`${JSON.stringify(l)},`);
    this.line("].join('\\n');");
    this.line('');
    const imports = Object.keys(this.imports).sort();
    if (imports.length > 0) {
      this.line('/** The grammars the grammar imports, by path relative to it. */');
      this.line('export const IMPORTS: Readonly<Record<string, string>> = {');
      for (const path of imports) this.line(`${JSON.stringify(path)}: ${JSON.stringify(this.imports[path])},`);
      this.line('};');
      this.line('');
    }
    this.line('/** The options the parser is built with. */');
    this.line('export const OPTIONS: Readonly<Partial<__lark.LarkOptions>> = {');
    this.line(`parser: ${quote(o.parser)},`);
    this.line(`lexer: ${quote(this.options.lexer ?? 'auto')},`);
    this.line(`start: [${this.start.map(quote).join(', ')}],`);
    this.line(`maybePlaceholders: ${o.maybePlaceholders},`);
    this.line(`keepAllTokens: ${o.keepAllTokens},`);
    this.line(`sourcePath: ${quote(this.options.sourceName ?? 'grammar.lark')},`);
    if (imports.length > 0) this.line('importPaths: [new __lark.codegen.EmbeddedLoader(IMPORTS)],');
    this.line('};');
    this.line('');
    this.line('let __parser: __lark.Lark | undefined;');
    this.line('');
    this.line('/** The parser, built on first use. */');
    this.line('export function parser(): __lark.Lark {');
    this.line('return (__parser ??= new __lark.Lark(GRAMMAR, OPTIONS));');
    this.line('}');
    this.line('');
    this.line(`/** Parses the text from the start rule \`${this.start[0]}\`. */`);
    this.line('export function parse(text: string): __lark.Tree {');
    this.line(`return parser().parse(text, ${quote(this.start[0]!)});`);
    this.line('}');
    if (this.start.length > 1) {
      for (const s of this.start) {
        this.line('');
        this.line(`/** Parses the text from the start rule \`${s}\`. */`);
        this.line(`export function parse${pascal(s)}(text: string): __lark.Tree {`);
        this.line(`return parser().parse(text, ${quote(s)});`);
        this.line('}');
      }
    }
    this.line('');
    this.line('/** Splits the text into tokens, without the ignored ones. */');
    this.line('export function lex(text: string): __lark.Token[] {');
    this.line('return [...parser().lex(text)];');
    this.line('}');
    this.line('');
  }

  private emitConstants(): void {
    const rules = new Set([...this.nodes.keys(), ...this.references.keys()]);
    this.line('/** The names of the rules, aliases and templates. */');
    this.line('export const Rules = {');
    for (const name of [...rules].sort()) this.line(`${key(name)}: ${quote(name)},`);
    this.line('} as const;');
    this.line('');
    this.line('/** The names of the terminals. */');
    this.line('export const Terminals = {');
    for (const name of this.terminals) this.line(`${key(name)}: ${quote(name)},`);
    this.line('} as const;');
    this.line('');
  }

  /** The type of a child that is one of the members, as the node classes give it. */
  private memberType(members: readonly string[]): string {
    if (members.includes(ANY)) return ANY_TYPE;
    const types: string[] = [];
    for (const m of members) if (this.nodes.has(m)) types.push(this.nodes.get(m)!);
    if (members.some((m) => !m.startsWith('#') && !this.nodes.has(m))) types.push('__lark.Token');
    if (members.includes(PLACEHOLDER)) types.push('null');
    return types.length === 0 ? 'never' : types.join(' | ');
  }

  private emitNodes(): void {
    this.line('/** The base class of the nodes of the grammar. */');
    this.line('export abstract class Node extends __lark.codegen.NodeBase {');
    this.line('protected get __model(): __lark.codegen.Model {');
    this.line('return MODEL;');
    this.line('}');
    this.line('}');
    this.line('');
    for (const [name, node] of this.nodes) {
      const shape = this.shapes.get(name)!;
      this.line(`/** ${article(name)} \`${name}\` tree. */`);
      this.line(`export class ${node} extends Node {`);
      this.line(`static readonly data = ${quote(name)};`);
      this.line('');
      this.line(`get data(): ${quote(name)} {`);
      this.line(`return ${quote(name)};`);
      this.line('}');
      this.line('');
      this.line(`/** The tree as ${article(node).toLowerCase()} \`${node}\`, if it is ${article(name).toLowerCase()} \`${name}\` tree. */`);
      this.line(`static from(tree: __lark.Tree): ${node} | null {`);
      this.line(`return tree.data === ${quote(name)} ? new ${node}(tree) : null;`);
      this.line('}');
      if (Array.isArray(shape)) {
        const names = elementNames(shape, RESERVED);
        shape.forEach((e, i) => {
          const type = this.memberType(e.members);
          this.line('');
          if (e.kind === 'single') {
            this.line(`/** The \`${e.name}\` child. */`);
            this.line(`get ${names[i]}(): ${type} {`);
            this.line(`return this.__one(${i}) as ${type};`);
          } else if (e.kind === 'optional') {
            const t = e.members.includes(PLACEHOLDER) || type === ANY_TYPE ? type : `${type} | null`;
            this.line(`/** The \`${e.name}\` child, or \`null\` when there is none. */`);
            this.line(`get ${names[i]}(): ${t} {`);
            this.line(`return this.__optional(${i}) as ${t};`);
          } else {
            this.line(`/** The \`${e.name}\` children. */`);
            const t = type.includes(' ') ? `(${type})[]` : `${type}[]`;
            this.line(`get ${names[i]}(): ${t} {`);
            this.line(`return this.__many(${i}) as ${t};`);
          }
          this.line('}');
        });
      } else {
        this.line('');
        this.line('/** The children. The trees have no fixed shape, so they aren\'t typed. */');
        this.line(`get children(): (${ANY_TYPE})[] {`);
        this.line(`return this.__children() as (${ANY_TYPE})[];`);
        this.line('}');
      }
      this.line('}');
      this.line('');
    }
  }

  private emitTypes(): void {
    this.line('/** The node class of each tree name, and the types of tokens and placeholders. */');
    this.line('export interface Nodes {');
    for (const [name, node] of this.nodes) this.line(`${key(name)}: ${node};`);
    for (const name of this.terminals) this.line(`${key(name)}: __lark.Token;`);
    this.line(`'${PLACEHOLDER}': null;`);
    this.line(`'${ANY}': ${ANY_TYPE};`);
    this.line('}');
    this.line('');
    this.line('/** The children of each tree: what can stand at each position, or `null` when the trees have no fixed shape. */');
    this.line('export interface Shapes {');
    for (const name of this.nodes.keys()) {
      const shape = this.shapes.get(name)!;
      if (!Array.isArray(shape)) {
        this.line(`${key(name)}: null;`);
        continue;
      }
      const names = elementNames(shape, RESERVED);
      const elements = shape.map((e, i) => `${label(names[i]!)}: { kind: '${e.kind}'; members: ${e.members.map(quote).join(' | ')} }`);
      this.line(`${key(name)}: [${elements.join(', ')}];`);
    }
    this.line('}');
    this.line('');
    this.line('/** The names of the trees. */');
    this.line('export type TreeName = keyof Shapes;');
    this.line('');
    this.line('/** The names of the terminals. */');
    this.line(`export type TerminalName = ${this.terminals.length === 0 ? 'never' : this.terminals.map(quote).join(' | ')};`);
    this.line('');
    const starts = new Set<string>();
    for (const s of this.start) for (const m of this.references.get(s) ?? [s]) starts.add(m);
    this.line('/** What the start rules can produce. */');
    this.line(`export type StartName = ${[...starts].sort().map(quote).join(' | ')};`);
    this.line('');
    this.line('/** The runtime model: the shapes of the trees, and their node classes. */');
    this.line('const MODEL: __lark.codegen.Model = {');
    this.line('shapes: {');
    for (const name of this.nodes.keys()) {
      const shape = this.shapes.get(name)!;
      const value = Array.isArray(shape) ? JSON.stringify(shape.map((e) => ({ kind: e.kind, members: e.members }))) : 'null';
      this.line(`${key(name)}: ${value},`);
    }
    this.line('},');
    this.line('nodes: {');
    for (const [name, node] of this.nodes) this.line(`${key(name)}: ${node},`);
    this.line('},');
    this.line('};');
    this.line('');
  }

  private emitVisitor(): void {
    this.line('/**');
    this.line(' * A map of result types: a type for each tree and terminal that the visitor turns into a value, such as');
    this.line(' * `{ add: number; NUMBER: number }`. Other keys have the type `never`.');
    this.line(' */');
    this.line('export type Results<R> = __lark.codegen.Results<R, TreeName | TerminalName>;');
    this.line('');
    this.line('/**');
    this.line(" * The handlers of a typed visitor: for each tree name in `R`, a function of the tree's node and its children,");
    this.line(' * which get their values from their own handlers, or are passed as their nodes or tokens. For each terminal in');
    this.line(' * `R`, a function of the token.');
    this.line(' */');
    this.line('export type Handlers<R extends Results<R>> = __lark.codegen.Handlers<R, Nodes, Shapes>;');
    this.line('');
    this.line('/** The type of the value of a tree, its result type or its node. */');
    this.line('export type Value<R extends Results<R>, Name extends keyof Nodes = StartName> = __lark.codegen.Value<R, Nodes, Name>;');
    this.line('');
    this.line('/**');
    this.line(' * A typed visitor: a function that visits a tree bottom-up, and returns its value. The handlers can be an object');
    this.line(' * or a class instance; the visitor doesn\'t recurse, so deep trees are fine.');
    this.line(' *');
    this.line(' * ```ts');
    this.line(' * const evaluate = visitor<{ add: number; number: number }>({ add: (node, a, b) => a + b, ... });');
    this.line(' * ```');
    this.line(' */');
    this.line('export function visitor<R extends Results<R>>(handlers: Handlers<R>): (tree: __lark.Tree) => Value<R> {');
    this.line('return (tree) => __lark.codegen.visit(MODEL, handlers, tree) as Value<R>;');
    this.line('}');
  }
}
