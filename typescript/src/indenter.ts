// A post-lexer for Python-style indentation (Lark's `indenter`).

import { DedentError } from './errors.js';
import type { PostLex } from './lexer.js';
import { Token } from './token.js';

/**
 * Injects indent and dedent tokens based on the indentation that follows each newline token. Inside parentheses the
 * indentation is ignored, and no indent or dedent tokens are generated (Lark's `Indenter`).
 */
export class Indenter implements PostLex {
  constructor(
    /** The name of the newline token. */
    readonly nlType: string,
    /** The names of the tokens that open a parenthesis. */
    readonly openParenTypes: readonly string[],
    /** The names of the tokens that close a parenthesis. */
    readonly closeParenTypes: readonly string[],
    /** The name of the token that starts an indentation in the grammar (see `%declare`). */
    readonly indentType: string,
    /** The name of the token that ends an indentation in the grammar. */
    readonly dedentType: string,
    /** How many spaces a tab equals. */
    readonly tabLen = 8,
  ) {}

  /** The indenter for Python's syntax (Lark's `PythonIndenter`). */
  static python(): Indenter {
    return new Indenter('_NEWLINE', ['LPAR', 'LSQB', 'LBRACE'], ['RPAR', 'RSQB', 'RBRACE'], '_INDENT', '_DEDENT', 8);
  }

  get alwaysAccept(): readonly string[] {
    return [this.nlType];
  }

  *process(stream: Iterable<Token>): Generator<Token> {
    let parenLevel = 0;
    const indentLevel = [0];
    let last: Token | undefined;
    for (const token of stream) {
      last = token;
      if (token.type === this.nlType) {
        // Inside parentheses, newlines are dropped.
        if (parenLevel === 0) {
          const indentStr = token.value.slice(token.value.lastIndexOf('\n') + 1); // Tabs and spaces
          const indent = indentStr.split(' ').length - 1 + (indentStr.split('\t').length - 1) * this.tabLen;
          yield token;
          if (indent > indentLevel[indentLevel.length - 1]!) {
            indentLevel.push(indent);
            yield Token.newBorrowPos(this.indentType, indentStr, token);
          } else {
            while (indent < indentLevel[indentLevel.length - 1]!) {
              indentLevel.pop();
              yield Token.newBorrowPos(this.dedentType, indentStr, token);
            }
            const expected = indentLevel[indentLevel.length - 1]!;
            if (indent !== expected) throw new DedentError(`Unexpected dedent to column ${indent}. Expected dedent to ${expected}`);
          }
        }
      } else {
        yield token;
      }
      if (this.openParenTypes.includes(token.type)) parenLevel++;
      else if (this.closeParenTypes.includes(token.type)) parenLevel = Math.max(parenLevel - 1, 0);
    }
    while (indentLevel.length > 1) {
      indentLevel.pop();
      yield last !== undefined ? Token.newBorrowPos(this.dedentType, '', last) : new Token(this.dedentType, '', 0, 0, 0, 0, 0, 0);
    }
  }
}
