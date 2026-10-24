// The LALR parser's table and state, as Python Lark leaves them readable: `parseTable`, a parser state's stacks, and
// the interactive parser's next token, for tools that print the automaton and recover from errors themselves.

import { describe, expect, test } from 'vitest';

import { Lark, Token, type LalrAction } from '../src/index.js';

function lalr(grammar: string): Lark {
  return new Lark(grammar, { parser: 'lalr', lexer: 'contextual' });
}

const blocks = `
start: item*
item: NAME ";" | "{" item* "}"
NAME: /[a-z]+/
%ignore " "
`;

describe('LALR access', () => {
  test('the parse table is the parser\'s', () => {
    const parser = lalr(blocks);
    const table = parser.parseTable();
    expect(table).toBeDefined();
    const start = table!.startStates.get('start')!;
    // The start state shifts a name or an opening brace, and reduces the empty list of items on the end.
    const actions = table!.states[start]!;
    expect(actions.get('NAME')?.kind).toBe('shift');
    expect(actions.get('LBRACE')?.kind).toBe('shift');
    expect(table!.endStates.has('start')).toBe(true);
    // Every shift goes to a state of the table, and every reduce names a rule of the grammar.
    const rules = parser.rules.map((r) => r.toString());
    for (const state of table!.states) {
      for (const action of state.values() as Iterable<LalrAction>) {
        if (action.kind === 'shift') expect(action.state).toBeLessThan(table!.states.length);
        else expect(rules).toContain(action.rule.toString());
      }
    }

    const earley = new Lark(blocks, { parser: 'earley' });
    expect(earley.parseTable()).toBeUndefined();
  });

  test('the interactive parser lexes one token for its state', () => {
    const parser = lalr('start: "if" NAME\nNAME: /[a-z]+/\n%ignore " "\n');
    const interactive = parser.parseInteractive('if if');
    // The first `if` is the keyword; in the state after it only a name is read, so the second is a name.
    const first = interactive.nextToken()!;
    expect([first.type, first.value]).toEqual(['IF', 'if']);
    interactive.feedToken(first);
    const second = interactive.nextToken()!;
    expect([second.type, second.startPos]).toEqual(['NAME', 3]);
    interactive.feedToken(second);
    expect(interactive.nextToken()).toBeUndefined();
    expect(interactive.feedEof()).toBeDefined();
  });

  test('a parser state is read and popped for recovery', () => {
    const parser = lalr(blocks);
    const interactive = parser.parseInteractive('a; { b; c ; } d;');
    for (let i = 0; i < 4; i++) interactive.feedToken(interactive.nextToken()!);
    // `a ;` reduced to an item, `{` and `b` shifted: four states above the start, three values.
    const state = interactive.parserState;
    expect(state.stateStack.length).toBe(state.valueStack.length + 1);
    const last = state.valueStack[state.valueStack.length - 1];
    expect(last instanceof Token && last.value).toBe('b');

    // Pop back to the state that read `{`, as a recovery that drops the half-read item would.
    const depth = interactive.parserState.depth;
    const popped = interactive.parserState.truncate(depth - 1);
    expect(popped.length).toBe(1);
    expect(popped[0] instanceof Token && popped[0].value).toBe('b');
    expect(interactive.parserState.depth).toBe(depth - 1);
    // The state that read `{` does not read the `;` that followed `b`: the lexer refuses it, and skipping it the
    // parse goes on with `c ; } d ;`.
    expect(() => interactive.nextToken()).toThrow();
    interactive.skipChars(1);
    expect(() => interactive.resumeParse()).not.toThrow();

    // The start state is never popped.
    expect(interactive.parserState.truncate(0).length).toBe(0);
    expect(interactive.parserState.depth).toBe(1);
  });
});
