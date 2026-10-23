import { describe, expect, test } from 'vitest';

import { Discard, GrammarError, Lark, MemoryLoader, Token, Transformer, Tree, UnexpectedInput, Visitor, VisitError } from '../src/index.js';

const calc = `
?start: sum
?sum: product | sum "+" product -> add | sum "-" product -> sub
?product: atom | product "*" atom -> mul
?atom: NUMBER -> number | "(" sum ")"
%import common.NUMBER
%import common.WS_INLINE
%ignore WS_INLINE
`;

class Calc extends Transformer<number> {
  add([a, b]: number[]) {
    return a! + b!;
  }
  sub([a, b]: number[]) {
    return a! - b!;
  }
  mul([a, b]: number[]) {
    return a! * b!;
  }
  number([n]: number[]) {
    return n!;
  }
  NUMBER(token: Token) {
    return Number(token.value);
  }
}

describe('Lark', () => {
  for (const parser of ['lalr', 'earley', 'cyk'] as const) {
    test(`parses with ${parser}`, () => {
      const lark = new Lark(calc, { parser });
      expect(new Calc().transform(lark.parse('1 + 2 * (3 - 1)'))).toBe(5);
    });
  }

  test('reports grammar errors', () => {
    expect(() => new Lark('start: undefined_rule')).toThrow(GrammarError);
  });

  test('imports from a memory loader', () => {
    const lark = new Lark('start: WORD\n%import sub.WORD', {
      importPaths: [new MemoryLoader({ 'sub.lark': 'WORD: /[a-z]+/' })],
    });
    expect(lark.parse('abc').children).toHaveLength(1);
  });

  test('recovers from errors with on_error', () => {
    const lark = new Lark('start: "a"+', { parser: 'lalr' });
    const errors: UnexpectedInput[] = [];
    const tree = lark.parse('aabaa', undefined, (e) => {
      errors.push(e);
      return true;
    });
    expect(errors).toHaveLength(1);
    expect(tree.data).toBe('start');
  });

  test('parses interactively', () => {
    const lark = new Lark('start: A B\nA: "a"\nB: "b"', { parser: 'lalr' });
    const ip = lark.parseInteractive('ab');
    ip.exhaustLexer();
    expect(ip.accepts()).toEqual(['$END']);
    expect((ip.feedEof() as Tree).children).toHaveLength(2);
  });
});

describe('visitors', () => {
  test('visit bottom-up', () => {
    const lark = new Lark('start: a b\na: "a"\nb: "b"', { parser: 'lalr' });
    const names: string[] = [];
    class Names extends Visitor {
      override default(tree: Tree) {
        names.push(tree.data);
      }
    }
    new Names().visit(lark.parse('ab'));
    expect(names).toEqual(['a', 'b', 'start']);
  });

  test('transforms deep trees without recursing', () => {
    let tree = new Tree('leaf', []);
    for (let i = 0; i < 200_000; i++) tree = new Tree('node', [tree]);
    class Depth extends Transformer<number> {
      override default(_data: string, children: unknown[]) {
        return Math.max(0, ...(children as number[])) + 1;
      }
    }
    expect(new Depth().transform(tree)).toBe(200_001);
  });

  test('discards nodes and wraps errors', () => {
    const lark = new Lark('start: A B\nA: "a"\nB: "b"', { parser: 'lalr' });
    class DropA extends Transformer {
      A() {
        return Discard;
      }
    }
    expect((new DropA().transform(lark.parse('ab')) as Tree).children).toHaveLength(1);
    class Fails extends Transformer {
      B(): never {
        throw new Error('no');
      }
    }
    expect(() => new Fails().transform(lark.parse('ab'))).toThrow(VisitError);
  });
});
