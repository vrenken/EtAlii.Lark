// Detection of terminals that can match the same text, for strict mode.
//
// Lark uses the `interegular` package to intersect the regular expressions. This samples strings from each
// expression instead, and checks whether a sample fully matches both terminals: a reported collision is always real,
// but some collisions may go unnoticed.

import { LexError } from './errors.js';
import { compileTerminal, TerminalDef } from './lexer.js';
import { Ast, Flags } from './pyre.js';
import { classify, repr } from './utils.js';

export function checkCollisions(terminals: TerminalDef[], flags: Flags): void {
  const regexTerminals = terminals.filter((t) => !t.pattern.isStr);
  for (const group of classify(regexTerminals, (t) => t.priority).values()) {
    for (let i = 0; i < group.length; i++) {
      for (let j = i + 1; j < group.length; j++) {
        const a = group[i]!;
        const b = group[j]!;
        const example = findOverlap(a, b, flags) ?? findOverlap(b, a, flags);
        if (example !== undefined) {
          throw new LexError(
            `Collision between Terminals ${a.name} and ${b.name}. \nExample of a string matched by both: ${repr(example)}`,
          );
        }
      }
    }
  }
}

function findOverlap(a: TerminalDef, b: TerminalDef, flags: Flags): string | undefined {
  let ast: Ast;
  try {
    ast = Ast.parse(a.pattern.toRegexp(), flags);
  } catch {
    return undefined;
  }
  const ra = compileTerminal(a, flags);
  const rb = compileTerminal(b, flags);
  return ast.samples().find((s) => s !== '' && ra.fullMatch(s) && rb.fullMatch(s));
}
