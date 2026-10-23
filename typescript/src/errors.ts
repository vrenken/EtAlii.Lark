// Lark's exceptions.

import type { TerminalDef } from './lexer.js';
import { Token } from './token.js';
import { expandTabs, sortedUnique } from './utils.js';

/** A value representing the parser state at an error, used by {@link UnexpectedInput.matchExamples}. Only its key matters. */
export type ErrorState = { kind: 'lalr'; depth: number; position: number } | { kind: 'expected'; items: string[] };

function stateKey(s: ErrorState | undefined): string | undefined {
  if (s === undefined) return undefined;
  return s.kind === 'lalr' ? `lalr:${s.depth}:${s.position}` : 'expected:' + sortedUnique(s.items).join('\u0000');
}

export type TerminalsByName = ReadonlyMap<string, TerminalDef>;

function formatExpected(expected: Iterable<string>, terminals: TerminalsByName | undefined): string {
  const names = [...expected].map((name) => terminals?.get(name)?.userRepr() ?? name);
  return `Expected one of: \n\t* ${names.join('\n\t* ')}\n`;
}

/** Returns a string pinpointing a position in the text, with `span` characters of context around it. */
export function getContext(text: string, pos: number, span = 40): string {
  pos = Math.min(Math.max(pos, 0), text.length);
  const start = Math.max(pos - span, 0);
  const end = Math.min(pos + span, text.length);
  const before = text.slice(start, pos).split('\n').pop() ?? '';
  const after = text.slice(pos, end).split('\n')[0] ?? '';
  return `${before}${after}\n${' '.repeat([...expandTabs(before)].length)}^\n`;
}

/** The base of all of the package's errors (Lark's `LarkError`). */
export class LarkError extends Error {
  /** The name of the Lark exception this error corresponds to. */
  get larkName(): string {
    return 'LarkError';
  }

  constructor(message: string) {
    super(message);
    this.name = new.target.name;
  }
}

/** Invalid options (Lark's `ConfigurationError`). */
export class ConfigurationError extends LarkError {
  override get larkName(): string {
    return 'ConfigurationError';
  }
}

/** An invalid grammar (Lark's `GrammarError`). */
export class GrammarError extends LarkError {
  override get larkName(): string {
    return 'GrammarError';
  }
}

/** A parse error, such as one raised by the CYK parser (Lark's `ParseError`). */
export class ParseError extends LarkError {
  override get larkName(): string {
    return 'ParseError';
  }
}

/** A lexer error, such as a terminal that can't be compiled (Lark's `LexError`). */
export class LexError extends LarkError {
  override get larkName(): string {
    return 'LexError';
  }
}

/** A tree template variable has no value (Lark's `MissingVariableError`). */
export class MissingVariableError extends LarkError {
  override get larkName(): string {
    return 'MissingVariableError';
  }
}

/** The indenter found a dedent to a column that wasn't indented to (Lark's `DedentError`). */
export class DedentError extends LarkError {
  override get larkName(): string {
    return 'DedentError';
  }
}

/** A visitor or transformer callback failed (Lark's `VisitError`). */
export class VisitError extends LarkError {
  /** The rule or terminal whose callback failed. */
  readonly rule: string;
  /** The tree or token the callback was given. */
  readonly obj: unknown;
  /** The error of the callback. */
  readonly origExc: unknown;

  constructor(rule: string, obj: unknown, origExc: unknown) {
    super(`Error trying to process rule "${rule}":\n\n${origExc instanceof Error ? origExc.message : String(origExc)}`);
    this.rule = rule;
    this.obj = obj;
    this.origExc = origExc;
    this.cause = origExc;
  }

  override get larkName(): string {
    return 'VisitError';
  }
}

/**
 * One of the unexpected-input errors (Lark's `UnexpectedInput`): {@link UnexpectedCharacters},
 * {@link UnexpectedToken} and {@link UnexpectedEOF}.
 */
export abstract class UnexpectedInput extends LarkError {
  /** The line of the error, or -1 at the end of the input. */
  line = -1;
  /** The column of the error, or -1 at the end of the input. */
  column = -1;
  /** The offset of the error in the text. */
  posInStream?: number;
  /** The parser state at the error. */
  state?: ErrorState;
  /** The tokens before the error. */
  tokenHistory?: Token[];
  /** @internal */
  terminalsByName?: TerminalsByName;

  /** A string pinpointing the error in the text, with `span` characters of context (Lark's `get_context`). */
  getContext(text: string, span = 40): string {
    return getContext(text, this.posInStream ?? 0, span);
  }

  /** @internal The token for comparing errors in {@link matchExamples}. */
  protected exampleToken(): Token | undefined {
    return undefined;
  }

  /**
   * Finds which of the examples fails the same way as this error, and returns its label (Lark's `match_examples`).
   * `parse` is usually a function that calls `Lark.parse`.
   */
  matchExamples<L>(
    parse: (text: string) => unknown,
    examples: Iterable<[L, Iterable<string>]> | Record<string, Iterable<string>>,
    tokenTypeMatchFallback = false,
    useAccepts = true,
  ): L | undefined {
    const state = stateKey(this.state);
    if (state === undefined) return undefined;
    const entries: Iterable<[L, Iterable<string>]> =
      Symbol.iterator in examples ? (examples as Iterable<[L, Iterable<string>]>) : (Object.entries(examples) as Array<[L, Iterable<string>]>);
    let candidate: [L | undefined, boolean] = [undefined, false];
    for (const [label, example] of entries) {
      for (const malformed of example) {
        let ut: unknown;
        try {
          parse(malformed);
          continue;
        } catch (e) {
          ut = e;
        }
        if (!(ut instanceof UnexpectedInput)) continue;
        if (stateKey(ut.state) !== state) continue;
        if (useAccepts && this instanceof UnexpectedToken && ut instanceof UnexpectedToken) {
          if (sortedUnique(this.accepts ?? []).join('\u0000') !== sortedUnique(ut.accepts ?? []).join('\u0000')) continue;
          if ((this.accepts === undefined) !== (ut.accepts === undefined)) continue;
        }
        const mine = this.exampleToken();
        const theirs = ut.exampleToken();
        if (mine !== undefined && theirs !== undefined) {
          if (mine.equals(theirs)) return label;
          if (tokenTypeMatchFallback && theirs.type === mine.type && !candidate[1]) candidate = [label, true];
        }
        if (candidate[0] === undefined) candidate = [label, false];
      }
    }
    return candidate[0];
  }
}

/** The lexer found no terminal that matches the text at its position (Lark's `UnexpectedCharacters`). */
export class UnexpectedCharacters extends UnexpectedInput {
  /** The character that didn't match. */
  readonly char: string;
  /** The terminals that were allowed at this point. */
  allowed?: string[];
  /** The terminals the Earley parser considered. */
  consideredTokens?: string[];
  /** The rules the Earley parser considered. */
  consideredRules?: string[];
  private readonly context: string;

  /** @internal */
  constructor(
    text: string,
    pos: number,
    line: number,
    column: number,
    allowed?: string[],
    state?: ErrorState,
    tokenHistory?: Token[],
    terminalsByName?: TerminalsByName,
  ) {
    const cp = text.codePointAt(Math.min(pos, text.length));
    const char = cp === undefined ? '' : String.fromCodePoint(cp);
    super(`No terminal matches '${char}' in the current parser context, at line ${line} col ${column}`);
    this.char = char;
    this.line = line;
    this.column = column;
    this.posInStream = pos;
    this.allowed = allowed;
    this.state = state;
    this.tokenHistory = tokenHistory;
    this.terminalsByName = terminalsByName;
    this.context = getContext(text, pos, 40);
    this.message = this.describe();
  }

  /** @internal */
  describe(): string {
    let message = `No terminal matches '${this.char}' in the current parser context, at line ${this.line} col ${this.column}`;
    message += '\n\n' + this.context;
    if (this.allowed !== undefined && this.allowed.length > 0) message += formatExpected(this.allowed, this.terminalsByName);
    if (this.tokenHistory !== undefined && this.tokenHistory.length > 0) {
      message += `\nPrevious tokens: ${this.tokenHistory.map((t) => t.repr()).join(', ')}\n`;
    }
    return message;
  }

  override get larkName(): string {
    return 'UnexpectedCharacters';
  }
}

/** The parser received a token that doesn't fit the grammar at that point (Lark's `UnexpectedToken`). */
export class UnexpectedToken extends UnexpectedInput {
  /** The mismatched token. */
  readonly token: Token;
  /** The terminals that were expected. */
  expected: string[];
  /** The terminals the LALR parser would have accepted (Lark's `accepts`). */
  accepts?: string[];
  /** The rules the Earley parser considered. */
  consideredRules?: string[];

  /** @internal */
  constructor(token: Token, expected: Iterable<string>, state?: ErrorState, terminalsByName?: TerminalsByName) {
    super('');
    this.token = token;
    this.expected = sortedUnique(expected);
    this.line = token.line ?? -1;
    this.column = token.column ?? -1;
    this.posInStream = token.startPos;
    this.state = state;
    this.terminalsByName = terminalsByName;
    this.refreshMessage();
  }

  /** @internal */
  refreshMessage(): void {
    const expected = this.accepts !== undefined && this.accepts.length > 0 ? this.accepts : this.expected;
    let message = `Unexpected token ${this.token.repr()} at line ${this.line}, column ${this.column}.\n${formatExpected(expected, this.terminalsByName)}`;
    if (this.tokenHistory !== undefined && this.tokenHistory.length > 0) {
      message += `Previous tokens: [${this.tokenHistory.map((t) => t.repr()).join(', ')}]\n`;
    }
    this.message = message;
  }

  protected override exampleToken(): Token {
    return this.token;
  }

  override get larkName(): string {
    return 'UnexpectedToken';
  }
}

/** The input ended while the parser still expected a token (Lark's `UnexpectedEOF`). */
export class UnexpectedEOF extends UnexpectedInput {
  /** The terminals that were expected. */
  expected: string[];

  /** @internal */
  constructor(expected: Iterable<string>, state?: ErrorState, terminalsByName?: TerminalsByName) {
    super('');
    this.expected = sortedUnique(expected);
    this.state = state;
    this.terminalsByName = terminalsByName;
    this.message = `Unexpected end-of-input. ${formatExpected(this.expected, terminalsByName)}`;
  }

  protected override exampleToken(): Token {
    return new Token('<EOF>', '');
  }

  override get larkName(): string {
    return 'UnexpectedEOF';
  }
}

/** Whether Lark would raise the error as a `ParseError` (including `UnexpectedToken` and `UnexpectedEOF`). */
export function isParseError(e: unknown): boolean {
  return e instanceof ParseError || e instanceof UnexpectedToken || e instanceof UnexpectedEOF;
}

/** Whether Lark would raise the error as a `LexError` (including `UnexpectedCharacters`). */
export function isLexError(e: unknown): boolean {
  return e instanceof LexError || e instanceof UnexpectedCharacters;
}
