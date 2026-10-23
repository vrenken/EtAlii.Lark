import { repr } from './utils.js';

/**
 * A piece of the text, produced by the lexer (Lark's `Token`).
 *
 * Positions follow JavaScript conventions: `startPos` and `endPos` are offsets into the string (in UTF-16 code
 * units), so `text.slice(startPos, endPos)` is the matched text. `line` and `column` start at 1; columns count UTF-16
 * code units as well.
 *
 * Like Lark's tokens, two tokens are equal ({@link Token.equals}) when their type and value are equal.
 */
export class Token {
  /** The name of the terminal (Lark's `token.type`). */
  type: string;
  /** The matched text. */
  value: string;
  /** The offset of the token in the text. */
  startPos?: number;
  /** The line of the token (starting with 1). */
  line?: number;
  /** The column of the token (starting with 1). */
  column?: number;
  /** The line where the token ends. */
  endLine?: number;
  /** The column after the end of the token. */
  endColumn?: number;
  /** The offset where the token ends. */
  endPos?: number;

  constructor(
    type: string,
    value: string,
    startPos?: number,
    line?: number,
    column?: number,
    endLine?: number,
    endColumn?: number,
    endPos?: number,
  ) {
    this.type = type;
    this.value = value;
    this.startPos = startPos;
    this.line = line;
    this.column = column;
    this.endLine = endLine;
    this.endColumn = endColumn;
    this.endPos = endPos;
  }

  /** Creates a token with the position of another token (Lark's `Token.new_borrow_pos`). */
  static newBorrowPos(type: string, value: string, borrow: Token): Token {
    return new Token(type, value, borrow.startPos, borrow.line, borrow.column, borrow.endLine, borrow.endColumn, borrow.endPos);
  }

  /** Returns a copy with another type and/or value, at the same position (Lark's `Token.update`). */
  update(type?: string, value?: string): Token {
    return Token.newBorrowPos(type ?? this.type, value ?? this.value, this);
  }

  /** Whether the other token has the same type and value. */
  equals(other: Token): boolean {
    return this.type === other.type && this.value === other.value;
  }

  toString(): string {
    return this.value;
  }

  /** Lark's `repr`: `Token('NAME', 'value')`. */
  repr(): string {
    return `Token(${repr(this.type)}, ${repr(this.value)})`;
  }
}
