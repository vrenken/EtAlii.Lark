// Runs the language-neutral conformance suite in `conformance/cases`, whose expected results were produced by Python
// Lark. The .NET and Rust packages run the same cases, so all ports interpret grammars identically.

import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isDeepStrictEqual } from 'node:util';
import { expect, test } from 'vitest';

import {
  Indenter,
  Lark,
  LarkError,
  LarkOptions,
  Meta,
  Node,
  Token,
  Tree,
  UnexpectedCharacters,
  UnexpectedEOF,
  UnexpectedInput,
  UnexpectedToken,
} from '../src/index.js';

type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
type JsonObject = { [key: string]: Json };

const conformanceDir = join(dirname(fileURLToPath(import.meta.url)), '../../conformance');

function loadCases(): Map<string, JsonObject> {
  const cases = new Map<string, JsonObject>();
  const dir = join(conformanceDir, 'cases');
  for (const file of readdirSync(dir).filter((f) => f.endsWith('.json')).sort()) {
    const root = JSON.parse(readFileSync(join(dir, file), 'utf8')) as { cases: JsonObject[] };
    for (const c of root.cases) cases.set(c.id as string, c);
  }
  return new Map([...cases].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
}

function loadKnownFailures(port: string): Set<string> {
  let text: string;
  try {
    text = readFileSync(join(conformanceDir, 'known-failures', `${port}.txt`), 'utf8');
  } catch {
    return new Set();
  }
  return new Set(
    text
      .split('\n')
      .map((l) => l.split('#')[0]!.trim())
      .filter((l) => l !== '')
      .map((l) => l.split(/\s+/)[0]!),
  );
}

test('conformance', () => {
  const cases = loadCases();
  const known = loadKnownFailures('typescript');
  const filter = process.env.CONFORMANCE_FILTER;
  const problems: string[] = [];
  let failing = 0;
  for (const [id, c] of cases) {
    if (filter !== undefined && !id.includes(filter)) continue;
    let failures: string[];
    try {
      failures = runCase(c);
    } catch (e) {
      failures = [`Crashed: ${e instanceof Error ? (e.stack ?? e.message) : String(e)}`];
    }
    if (failures.length > 0) failing++;
    if (known.has(id)) {
      if (failures.length === 0) problems.push(`${id}: passes now; remove it from conformance/known-failures/typescript.txt.`);
    } else if (failures.length > 0) {
      problems.push(`${id}:\n${failures.join('\n\n')}`);
    }
  }
  console.log(`${cases.size} cases, ${failing} failing, ${problems.length} unexpected results`);
  expect(problems.join('\n\n')).toBe('');
});

function errorName(e: unknown): string {
  return e instanceof LarkError ? e.larkName : `Exception:${e instanceof Error ? `${e.name}: ${e.message}` : String(e)}`;
}

function runCase(c: JsonObject): string[] {
  const failures: string[] = [];
  const grammar = c.grammar as string;
  const grammarError = c.grammar_error as string | undefined;
  let parser: Lark;
  try {
    parser = new Lark(grammar, options(c.options as JsonObject));
  } catch (e) {
    const name = errorName(e);
    if (grammarError === undefined) failures.push(`The grammar failed to load: ${name}: ${(e as Error).message}`);
    else if (name !== grammarError) failures.push(`Expected grammar error ${grammarError}, got ${name}: ${(e as Error).message}`);
    return failures;
  }
  if (grammarError !== undefined) return [`Expected grammar error ${grammarError}, but the grammar loaded.`];

  for (const t of c.tests as JsonObject[]) {
    const isLex = t.lex !== undefined;
    const text = (isLex ? t.lex : t.input) as string;
    const positions = new Positions(text);
    let actual: Json;
    try {
      if (isLex) {
        actual = { tokens: [...parser.lex(text, (t.dont_ignore as boolean | undefined) ?? false)].map((tok) => tokenJson(tok, positions)) };
      } else {
        actual = { tree: treeJson(parser.parse(text, t.start as string | undefined), positions) };
      }
    } catch (e) {
      actual = errorJson(e, positions);
    }
    if (!isDeepStrictEqual(t.expect, actual)) {
      failures.push(`Input ${show(text)}:\n  expected ${JSON.stringify(t.expect)}\n  actual   ${JSON.stringify(actual)}`);
    }
  }
  return failures;
}

function options(o: JsonObject): Partial<LarkOptions> {
  // Positions are always propagated, as when the expected results were generated, and compared too.
  const result: Partial<LarkOptions> = { propagatePositions: true };
  for (const [name, value] of Object.entries(o)) {
    switch (name) {
      case 'parser':
        result.parser = value as LarkOptions['parser'];
        break;
      case 'lexer':
        result.lexer = value as LarkOptions['lexer'];
        break;
      case 'start':
        result.start = value as string | string[];
        break;
      case 'ambiguity':
        result.ambiguity = value as LarkOptions['ambiguity'];
        break;
      case 'priority':
        result.priority = value as LarkOptions['priority'];
        break;
      case 'maybe_placeholders':
        result.maybePlaceholders = value as boolean;
        break;
      case 'keep_all_tokens':
        result.keepAllTokens = value as boolean;
        break;
      case 'strict':
        result.strict = value as boolean;
        break;
      case 'regex_flags':
        result.regexFlags = value as string;
        break;
      case 'postlex':
        if (value !== 'python_indenter') throw new Error(`Unknown post-lexer ${String(value)}`);
        result.postlex = Indenter.python();
        break;
      default:
        throw new Error(`Unknown conformance option ${name}`);
    }
  }
  return result;
}

const sorted = (values: Iterable<string>): string[] => [...new Set(values)].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));

function errorJson(e: unknown, positions: Positions): JsonObject {
  const result: JsonObject = { error: errorName(e) };
  if (e instanceof UnexpectedInput && !(e instanceof UnexpectedToken)) {
    result.line = e.line;
    result.column = e.line > 0 && e.posInStream !== undefined ? positions.column(e.posInStream, e.column) : e.column;
  }
  if (e instanceof UnexpectedToken) {
    const hasLine = e.token.line !== undefined;
    result.line = hasLine ? e.line : null;
    result.column = hasLine ? (e.posInStream !== undefined ? positions.column(e.posInStream, e.column) : e.column) : null;
    result.token = { type: e.token.type, value: e.token.value };
    result.expected = sorted(e.expected);
  } else if (e instanceof UnexpectedEOF) {
    result.line = -1;
    result.column = -1;
    result.expected = sorted(e.expected);
  } else if (e instanceof UnexpectedCharacters) {
    result.char = e.char;
    if (e.allowed !== undefined) result.expected = sorted(e.allowed);
  }
  return result;
}

const orNull = (v: number | undefined): Json => (v === undefined ? null : v);

function metaJson(meta: Meta, positions: Positions): Json {
  return [
    positions.offset(meta.startPos),
    positions.offset(meta.endPos),
    orNull(meta.line),
    meta.startPos !== undefined && meta.column !== undefined ? positions.column(meta.startPos, meta.column) : orNull(meta.column),
    orNull(meta.endLine),
    meta.endPos !== undefined && meta.endColumn !== undefined ? positions.column(meta.endPos, meta.endColumn) : orNull(meta.endColumn),
  ];
}

function treeJson(tree: Tree, positions: Positions): JsonObject {
  const result: JsonObject = { data: tree.data, children: tree.children.map((c) => nodeJson(c, positions)) };
  if (tree.hasMeta) result.meta = metaJson(tree.meta, positions);
  return result;
}

function nodeJson(node: Node, positions: Positions): Json {
  if (node instanceof Tree) return treeJson(node, positions);
  if (node instanceof Token) return tokenJson(node, positions);
  return null;
}

function tokenJson(token: Token, positions: Positions): JsonObject {
  return {
    type: token.type,
    value: token.value,
    pos: [
      positions.offset(token.startPos),
      positions.offset(token.endPos),
      orNull(token.line),
      token.startPos !== undefined && token.column !== undefined ? positions.column(token.startPos, token.column) : orNull(token.column),
      orNull(token.endLine),
      token.endPos !== undefined && token.endColumn !== undefined ? positions.column(token.endPos, token.endColumn) : orNull(token.endColumn),
    ],
  };
}

/** Lark counts positions in code points; JavaScript in UTF-16 code units. This converts the package's positions. */
class Positions {
  private readonly codePoints: number[];

  constructor(text: string) {
    this.codePoints = new Array<number>(text.length + 1);
    let count = 0;
    for (let i = 0; i < text.length; i++) {
      this.codePoints[i] = count;
      const c = text.charCodeAt(i);
      const isPair = c >= 0xd800 && c <= 0xdbff && i + 1 < text.length && (text.charCodeAt(i + 1) & 0xfc00) === 0xdc00;
      if (!isPair) count++;
    }
    this.codePoints[text.length] = count;
  }

  offset(offset: number | undefined): Json {
    if (offset === undefined) return null;
    return offset >= 0 && offset < this.codePoints.length ? this.codePoints[offset]! : offset;
  }

  column(offset: number, column: number): number {
    const lineStart = offset - (column - 1);
    if (offset < 0 || offset >= this.codePoints.length || lineStart < 0) return column;
    return this.codePoints[offset]! - this.codePoints[lineStart]! + 1;
  }
}

function show(text: string): string {
  return text.length > 200 ? JSON.stringify(text.slice(0, 200)) + '...' : JSON.stringify(text);
}
