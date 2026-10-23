// Generates the typed module of every grammar in the conformance suite, typechecks them with the TypeScript compiler,
// and runs their typed visitors on the trees Python Lark builds, with a handler for every tree and terminal. The
// handlers rebuild each tree from the values of its children, which must give the tree back.

import { mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { expect, test } from 'vitest';

import { codegen, generate, GenerateOptions, Token, Tree } from '../src/index.js';
import type { Node } from '../src/index.js';

type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
type JsonObject = { [key: string]: Json };

const here = dirname(fileURLToPath(import.meta.url));
const casesDir = join(here, '../../conformance/cases');
const outDir = join(here, 'codegen/.cases');

interface Generated {
  id: string;
  file: string;
  case: JsonObject;
}

function generateAll(): Generated[] {
  rmSync(outDir, { recursive: true, force: true });
  mkdirSync(outDir, { recursive: true });
  const result: Generated[] = [];
  const seen = new Set<string>();
  for (const file of readdirSync(casesDir).filter((f) => f.endsWith('.json')).sort()) {
    const root = JSON.parse(readFileSync(join(casesDir, file), 'utf8')) as { cases: JsonObject[] };
    for (const c of root.cases) {
      const o = c.options as JsonObject;
      if (c.grammar_error !== undefined || o.postlex !== undefined) continue;
      const options: GenerateOptions = {
        parser: o.parser as GenerateOptions['parser'],
        lexer: o.lexer as GenerateOptions['lexer'],
        start: o.start as string | string[] | undefined,
        maybePlaceholders: o.maybe_placeholders as boolean | undefined,
        keepAllTokens: o.keep_all_tokens as boolean | undefined,
        packageName: '../../../src/index.js',
      };
      // Cases that differ only in the parser or lexer give the same types.
      const key = JSON.stringify([c.grammar, o.start, o.maybe_placeholders, o.keep_all_tokens]);
      if (seen.has(key)) continue;
      let code: string;
      try {
        code = generate(c.grammar as string, options);
      } catch {
        // Grammars that the chosen parser can't handle; the conformance tests check those errors.
        continue;
      }
      seen.add(key);
      const id = (c.id as string).replace(/[^A-Za-z0-9]/g, '_');
      const path = join(outDir, `${id}.lark.ts`);
      writeFileSync(path, code);
      // The handler types must resolve with every name mapped, and with only the trees mapped.
      writeFileSync(
        join(outDir, `${id}.use.ts`),
        [
          `import * as G from './${id}.lark.js';`,
          'type All = { [K in G.TreeName | G.TerminalName]: number };',
          'type Trees = { [K in G.TreeName]: string };',
          'export const all: (tree: import("../../../src/index.js").Tree) => G.Value<All> = G.visitor<All>({} as G.Handlers<All>);',
          'export const trees = G.visitor<Trees>({} as G.Handlers<Trees>);',
          '',
        ].join('\n'),
      );
      result.push({ id, file: path, case: c });
    }
  }
  return result;
}

const generated = generateAll();

test('the generated modules typecheck', () => {
  expect(generated.length).toBeGreaterThan(150);
  const files = readdirSync(outDir).map((f) => join(outDir, f));
  const program = ts.createProgram(files, {
    target: ts.ScriptTarget.ES2022,
    module: ts.ModuleKind.NodeNext,
    moduleResolution: ts.ModuleResolutionKind.NodeNext,
    strict: true,
    noImplicitOverride: true,
    noEmit: true,
    skipLibCheck: true,
    types: [],
  });
  const diagnostics = ts.getPreEmitDiagnostics(program).map((d) => {
    const where = d.file === undefined ? '' : `${d.file.fileName.slice(outDir.length + 1)}(${d.file.getLineAndCharacterOfPosition(d.start ?? 0).line + 1}): `;
    return where + ts.flattenDiagnosticMessageText(d.messageText, '\n');
  });
  expect(diagnostics.slice(0, 20).join('\n')).toBe('');
}, 120_000);

function fromJson(node: Json): Node {
  if (node === null) return null;
  const o = node as JsonObject;
  if (o.data !== undefined) return new Tree(o.data as string, (o.children as Json[]).map(fromJson));
  return new Token(o.type as string, o.value as string);
}

/** A tree as JSON, without positions. */
function toJson(node: Node): Json {
  if (node instanceof Tree) return { data: node.data, children: node.children.map(toJson) };
  if (node instanceof Token) return { type: node.type, value: node.value };
  return null;
}

/** Removes the positions of expected trees. */
function strip(node: Json): Json {
  if (node === null || typeof node !== 'object' || Array.isArray(node)) return node;
  if (node.data !== undefined) return { data: node.data, children: (node.children as Json[]).map(strip) };
  return { type: node.type ?? null, value: node.value ?? null };
}

test('the typed visitors rebuild the trees', async () => {
  const problems: string[] = [];
  let visited = 0;
  for (const g of generated) {
    const c = g.case;
    if ((c.options as JsonObject).ambiguity === 'explicit') continue;
    const module = (await import(g.file)) as {
      visitor: (handlers: object) => (tree: Tree) => unknown;
      Shapes?: unknown;
      Terminals: Record<string, string>;
    };
    const shapes = (c.shapes ?? {}) as JsonObject;
    // A wrapped child, as the handlers get unmapped ones, back to JSON.
    const unwrap = (value: unknown): Json => (value instanceof codegen.NodeBase ? toJson(value.tree) : (value as Json));
    const handlers: Record<string, (...args: unknown[]) => Json> = {};
    let exact = true;
    for (const [name, shape] of Object.entries(shapes)) {
      if (shape === 'inlined') continue;
      handlers[name] = (node, ...args) => {
        const tree = (node as codegen.NodeBase).tree;
        if (!Array.isArray(shape)) return toJson(tree);
        if (args.length !== shape.length) throw new Error(`${name}: ${args.length} children values for ${shape.length} elements`);
        const children: Json[] = [];
        shape.forEach((e, k) => {
          const element = e as JsonObject;
          const value = args[k];
          if (element.kind === 'repeated') {
            if (!Array.isArray(value)) throw new Error(`${name}: a repeated element that isn't an array`);
            children.push(...value.map(unwrap));
          } else if (element.kind === 'optional') {
            // An absent child and a placeholder are both null.
            if ((element.members as string[]).includes('#none')) exact = false;
            if (value !== null) children.push(unwrap(value));
          } else children.push(unwrap(value));
        });
        return { data: name, children };
      };
    }
    for (const name of Object.values(module.Terminals)) handlers[name] = (token) => toJson(token as Token);
    const visit = module.visitor(handlers);
    for (const t of (c.tests ?? []) as JsonObject[]) {
      const expected = (t.expect as JsonObject).tree;
      if (expected === undefined) continue;
      exact = true;
      let actual: Json;
      try {
        actual = unwrap(visit(fromJson(expected) as Tree));
      } catch (e) {
        problems.push(`${g.id}: ${e instanceof Error ? e.message : String(e)}`);
        continue;
      }
      visited++;
      if (exact && JSON.stringify(actual) !== JSON.stringify(strip(expected))) {
        problems.push(`${g.id}:\n  expected ${JSON.stringify(strip(expected))}\n  actual   ${JSON.stringify(actual)}`);
      }
    }
  }
  expect(visited).toBeGreaterThan(300);
  expect(problems.slice(0, 20).join('\n')).toBe('');
}, 120_000);
