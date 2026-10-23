// Tests the code generator: the generated modules in `test/codegen` and `examples`, which `npm run typecheck` also
// checks, are up to date, and their parsers, node classes and typed visitors work.

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, test } from 'vitest';

import { main, parsePurchaseOrder } from '../examples/purchase-orders/purchase-orders.js';
import { generate, GenerateOptions, Token, Tree } from '../src/index.js';
import { camel, elementNames, pascal, plural } from '../src/codegen/typescript.js';
import * as Calc from './codegen/calc.lark.js';
import * as Json from './codegen/json.lark.js';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

const generated: [string, GenerateOptions][] = [
  ['test/codegen/calc.lark', { parser: 'lalr' }],
  ['test/codegen/json.lark', { parser: 'lalr' }],
  ['examples/purchase-orders/purchase-order.lark', { parser: 'lalr', start: 'order' }],
];

test.each(generated)('%s.ts is up to date', (grammar, options) => {
  const path = join(root, grammar);
  const code = generate(readFileSync(path, 'utf8'), {
    ...options,
    sourceName: grammar.split('/').pop()!,
    packageName: '../../src/index.js',
  });
  if (process.env.UPDATE_GENERATED === '1') writeFileSync(`${path}.ts`, code);
  expect(code, `Run UPDATE_GENERATED=1 npm test to update ${grammar}.ts`).toBe(readFileSync(`${path}.ts`, 'utf8'));
});

type CalcResults = { add: number; sub: number; mul: number; div: number; neg: number; number: number; NUMBER: number };

const evaluate = Calc.visitor<CalcResults>({
  add: (_, sum, product) => sum + product,
  sub: (_, sum, product) => sum - product,
  mul: (_, product, atom) => product * atom,
  div: (_, product, atom) => product / atom,
  neg: (_, atom) => -atom,
  number: (_, number) => number,
  NUMBER: (token) => Number(token.value),
});

describe('calc', () => {
  test('the typed visitor', () => {
    expect(evaluate(Calc.parse('1 + 2 * (3 - 4) / -2'))).toBe(2);
    // A ?rule with one child is replaced by it, so the root is a number tree.
    expect(evaluate(Calc.parse('7'))).toBe(7);
  });

  test('unmapped children are passed as their nodes', () => {
    const show = Calc.visitor<{ add: string; NUMBER: number }>({
      add: (_, sum, product) => `${typeof sum === 'string' ? sum : sum.data} + ${typeof product === 'string' ? `(${product})` : product.data}`,
      NUMBER: (token) => Number(token.value),
    });
    expect(show(Calc.parse('1 + 2 * 3 + (4 + 5)'))).toBe('number + mul + (number + number)');
    // An unmapped root is its node.
    const node = show(Calc.parse('2 * 3'));
    expect(node).toBeInstanceOf(Calc.MulNode);
  });

  test('handlers can be a class', () => {
    class Evaluator implements Calc.Handlers<CalcResults> {
      constructor(private readonly scale: number) {}
      add(_: Calc.AddNode, sum: number, product: number) {
        return sum + product;
      }
      sub(_: Calc.SubNode, sum: number, product: number) {
        return sum - product;
      }
      mul(_: Calc.MulNode, product: number, atom: number) {
        return product * atom;
      }
      div(_: Calc.DivNode, product: number, atom: number) {
        return product / atom;
      }
      neg(_: Calc.NegNode, atom: number) {
        return -atom;
      }
      number(_: Calc.NumberNode, number: number) {
        return number * this.scale;
      }
      NUMBER(token: Token) {
        return Number(token.value);
      }
    }
    expect(Calc.visitor<CalcResults>(new Evaluator(10))(Calc.parse('1 + 2 * 3'))).toBe(610);
  });

  test('deep trees', () => {
    let tree = new Tree('number', [new Token('NUMBER', '0')]);
    for (let i = 0; i < 100_000; i++) tree = new Tree('add', [tree, new Tree('number', [new Token('NUMBER', '1')])]);
    expect(evaluate(tree)).toBe(100_000);
  });

  test('the node classes', () => {
    const tree = Calc.parse('1 + 2 * 3');
    expect(Calc.MulNode.from(tree)).toBeNull();
    const add = Calc.AddNode.from(tree)!;
    expect(add.data).toBe('add');
    expect(add.tree).toBe(tree);
    const product = add.product;
    // The data narrows the union.
    if (product.data !== 'mul') throw new Error('expected a mul tree');
    expect(product.atom.data).toBe('number');
    const left = add.sum;
    expect(left.data === 'number' && left.number.value).toBe('1');
    expect(() => new Calc.AddNode(product.tree)).toThrow("Not a 'add' tree, but a 'mul' tree.");
  });

  test('constants and lexing', () => {
    expect(Calc.Rules.add).toBe('add');
    expect(Calc.Terminals.NUMBER).toBe('NUMBER');
    expect(Calc.lex('1 + 2').map((t) => t.type)).toEqual(['NUMBER', 'PLUS', 'NUMBER']);
  });

  test('handler errors stop the visit', () => {
    const failing = Calc.visitor<{ number: number }>({
      number: () => {
        throw new Error('no numbers');
      },
    });
    // The add handler isn't there, so add trees aren't visited; their children are.
    expect(() => failing(Calc.parse('1'))).toThrow('no numbers');
  });
});

type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };

describe('json', () => {
  type Results = {
    array: JsonValue;
    object: JsonValue;
    pair: [string, JsonValue];
    string: string;
    number: number;
    true: boolean;
    false: boolean;
    null: null;
  };
  const build = Json.visitor<Results>({
    // `[value ("," value)*]`: an optional first value, and the others.
    array: (_, value, values) => (value === null ? [] : [value, ...values]),
    object: (_, pair, pairs) => Object.fromEntries(pair === null ? [] : [pair, ...pairs]),
    pair: (_, key, value) => [key, value],
    string: (_, token) => JSON.parse(token.value) as string,
    number: (_, token) => Number(token.value),
    true: () => true,
    false: () => false,
    null: () => null,
  });

  test('the typed visitor', () => {
    const text = '{"a": [1, -2.5e3, "x"], "b": {"c": null, "d": [true, false]}, "e": []}';
    expect(build(Json.parse(text))).toEqual(JSON.parse(text));
    // The start rule can produce any value, so the result is the union of their types.
    const value: JsonValue = build(Json.parse('"text"'));
    expect(value).toBe('text');
  });
});

describe('purchase orders', () => {
  test('the example', () => {
    expect(main()).toBe('PO-2026-0142 for ACME Corp: 2 lines, total 7347.75 Eur');
    expect(() => parsePurchaseOrder('order "PO-1" customer "B" currency USD line "A" "B" qty 0 at 1.00')).toThrow(
      'Order line A has no quantity.',
    );
  });
});

describe('the generator', () => {
  test('names', () => {
    expect(pascal('order_line')).toBe('OrderLine');
    expect(pascal('__ANON_0')).toBe('Anon0');
    expect(camel('TEXT')).toBe('text');
    expect(camel('express_shipping')).toBe('expressShipping');
    expect(['line', 'address', 'entry', 'day'].map(plural)).toEqual(['lines', 'addresses', 'entries', 'days']);
    const e = (name: string, kind: 'single' | 'repeated' = 'single') => ({ kind, name, members: [name] });
    expect(elementNames([e('TEXT'), e('TEXT'), e('data'), e('line', 'repeated')], ['data'])).toEqual(['text', 'text2', 'data2', 'lines']);
  });

  test('relative imports are embedded', () => {
    const files: Record<string, string> = {
      'lib/numbers.lark': 'number: DIGITS\n%import .digits.DIGITS',
      'lib/digits.lark': 'DIGITS: /[0-9]+/',
    };
    const code = generate('start: number+\n%import .lib.numbers.number\n%ignore " "', {
      readImport: (path) => files[path],
    });
    expect(code).toContain('"lib/numbers.lark": ');
    expect(code).toContain('"lib/digits.lark": ');
    expect(code).toContain('importPaths: [new __lark.codegen.EmbeddedLoader(IMPORTS)]');
    expect(() => generate('start: number\n%import .lib.numbers.number')).toThrow("No such file or directory: 'lib/numbers.lark'");
  });

  test('grammar errors', () => {
    expect(() => generate('start: missing')).toThrow('missing');
  });
});

// Type errors that the generated types must report. The functions aren't run; `npm run typecheck` checks them.
export function typeErrors(): void {
  // @ts-expect-error: not a name of the grammar.
  Calc.visitor<{ foo: number }>({ foo: () => 1 });
  // @ts-expect-error: the handler returns a string.
  Calc.visitor<{ add: number }>({ add: () => 'x' });
  // @ts-expect-error: the mul handler is missing.
  Calc.visitor<{ add: number; mul: number }>({ add: () => 1 });
  Calc.visitor<{ add: number }>({
    // @ts-expect-error: sum can be an add tree, a number, so it isn't a node.
    add: (_, sum) => sum.data.length,
  });
  Calc.visitor<{ number: string }>({
    // @ts-expect-error: the child is a token, not a string.
    number: (_, n: string) => n,
  });
}
