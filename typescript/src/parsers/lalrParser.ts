// The LALR(1) parser and its interactive mode (Lark's `lalr_parser`, `lalr_parser_state` and
// `lalr_interactive_parser`).

import { ConfigurationError, ErrorState, UnexpectedCharacters, UnexpectedInput, UnexpectedToken } from '../errors.js';
import { LexerThread, lexerTerminalsByName, SharedParserState } from '../lexer.js';
import type { Callbacks } from '../parseTreeBuilder.js';
import { Token } from '../token.js';
import type { Node } from '../tree.js';
import { isUpper } from '../utils.js';
import type { Action, ParseTable } from './lalrAnalysis.js';

/** The state of a LALR parse: the stack of states and the stack of values (Lark's `ParserState`). */
export class ParserState {
  /** @internal */
  constructor(
    readonly stateStack: number[],
    readonly valueStack: Node[],
    /** @internal */
    readonly endState: number,
  ) {}

  /** The current state of the parser. */
  get position(): number {
    return this.stateStack[this.stateStack.length - 1]!;
  }

  /** The depth of the state stack. */
  get depth(): number {
    return this.stateStack.length;
  }

  /** @internal */
  errorState(): ErrorState {
    return { kind: 'lalr', depth: this.depth, position: this.position };
  }

  /** Like Lark, compares the depth and the current state. */
  equals(other: ParserState): boolean {
    return this.depth === other.depth && this.position === other.position;
  }

  copy(): ParserState {
    return new ParserState([...this.stateStack], [...this.valueStack], this.endState);
  }

  /**
   * Pops the stacks until `depth` states are left, at least the start state, and returns the values popped, the lowest
   * first: what error recovery does to resume at a state lower on the stack, as Lark's `on_error` handlers do through
   * `state_stack` and `value_stack`.
   */
  truncate(depth: number): Node[] {
    const keep = Math.min(Math.max(depth, 1), this.stateStack.length);
    this.stateStack.length = keep;
    const values = keep - 1;
    return values < this.valueStack.length ? this.valueStack.splice(values) : [];
  }
}

function endToken(last: Token | undefined): Token {
  return last !== undefined ? Token.newBorrowPos('$END', '', last) : new Token('$END', '', 0, 1, 1);
}

/** @internal The LALR parser of a grammar: its parse table and tree builders. */
export class LalrParser {
  constructor(
    readonly table: ParseTable,
    readonly callbacks: Callbacks,
  ) {}

  newState(start: string): ParserState {
    const startState = this.table.startStates.get(start);
    if (startState === undefined) throw new ConfigurationError(`Unknown start rule ${start}`);
    return new ParserState([startState], [], this.table.endStates.get(start)!);
  }

  /**
   * Feeds one token; returns the result when `isEnd` and the parse is complete. Without callbacks (used to probe
   * which tokens are accepted), no trees are built.
   */
  feedToken(ps: ParserState, token: Token, isEnd: boolean, withCallbacks: boolean): { done: true; value: Node } | undefined {
    for (;;) {
      const state = ps.position;
      const action = this.table.states[state]!.get(token.type);
      if (action === undefined) {
        const expected = [...this.table.states[state]!.keys()].filter(isUpper);
        throw new UnexpectedToken(token, expected, ps.errorState());
      }
      if (action.kind === 'shift') {
        ps.stateStack.push(action.state);
        ps.valueStack.push(token);
        return undefined;
      }
      const rule = action.rule;
      const size = rule.expansion.length;
      let children: Node[] = [];
      if (size > 0) {
        ps.stateStack.length -= size;
        children = ps.valueStack.splice(ps.valueStack.length - size, size);
      }
      const value = withCallbacks ? this.callbacks.build(rule, children) : null;
      const goto = this.table.states[ps.position]!.get(rule.origin.name);
      if (goto === undefined || goto.kind !== 'shift') throw new Error('goto must be a shift');
      ps.stateStack.push(goto.state);
      ps.valueStack.push(value);
      if (isEnd && ps.position === ps.endState) return { done: true, value: ps.valueStack.pop()! };
    }
  }

  /** The terminals that the parser accepts in this state (Lark's `InteractiveParser.accepts`). */
  accepts(ps: ParserState): string[] {
    const accepts: string[] = [];
    for (const t of this.table.states[ps.position]!.keys()) {
      if (!isUpper(t)) continue;
      const copy = new ParserState([...ps.stateStack], new Array<Node>(ps.valueStack.length).fill(null), ps.endState);
      try {
        this.feedToken(copy, new Token(t, ''), t === '$END', false);
        accepts.push(t);
      } catch (e) {
        if (!(e instanceof UnexpectedToken)) throw e;
      }
    }
    return accepts.sort();
  }

  /** Runs the parser on the tokens of the lexer until the end (Lark's `parse_from_state`). */
  parseFromState(ps: ParserState, lexer: LexerThread, lastToken?: Token): Node {
    const shared: SharedParserState = { depth: ps.depth, position: ps.position };
    let token = lastToken;
    try {
      const stream = lexer.lex(shared);
      for (;;) {
        shared.depth = ps.depth;
        shared.position = ps.position;
        const r = stream.next();
        if (r.done) break;
        token = r.value;
        this.feedToken(ps, r.value, false, true);
      }
      return this.feedToken(ps, endToken(token), true, true)?.value ?? null;
    } catch (e) {
      throw this.completeError(e, ps, lexer);
    }
  }

  /** Adds what the parser knows to an error: the accepted terminals and the terminal names. */
  completeError(e: unknown, ps: ParserState, lexer: LexerThread): unknown {
    if (e instanceof UnexpectedToken) {
      if (e.terminalsByName === undefined) e.terminalsByName = lexerTerminalsByName(lexer.lexer);
      e.accepts = this.accepts(ps);
      e.refreshMessage();
    }
    return e;
  }

  choices(ps: ParserState): Map<string, Action> {
    return this.table.states[ps.position]!;
  }
}

/** Gives control over a LALR parse, for error recovery and for feeding tokens by hand (Lark's `InteractiveParser`). */
export class InteractiveParser {
  /** The result of the parse, once the end token was fed. */
  result?: Node;

  /** @internal */
  constructor(
    private readonly parser: LalrParser,
    /** The parser state. */
    readonly parserState: ParserState,
    /** @internal */
    readonly lexerThread: LexerThread,
  ) {}

  /** Feeds a token, as if it came from the lexer. Returns the result when the token is `$END`. */
  feedToken(token: Token): Node | undefined {
    let r: { done: true; value: Node } | undefined;
    try {
      r = this.parser.feedToken(this.parserState, token, token.type === '$END', true);
    } catch (e) {
      throw this.parser.completeError(e, this.parserState, this.lexerThread);
    }
    if (r !== undefined) {
      this.result = r.value;
      return r.value;
    }
    return undefined;
  }

  /** Feeds the rest of the text, without the end token. Returns the tokens. */
  exhaustLexer(): Token[] {
    const shared: SharedParserState = { depth: this.parserState.depth, position: this.parserState.position };
    const stream = this.lexerThread.lex(shared);
    const tokens: Token[] = [];
    for (;;) {
      shared.depth = this.parserState.depth;
      shared.position = this.parserState.position;
      const r = stream.next();
      if (r.done) break;
      tokens.push(r.value);
      this.feedToken(r.value);
    }
    return tokens;
  }

  /**
   * Lexes the next token for the parser's current state, without feeding it; `undefined` at the end of the text. With
   * the contextual lexer, only the terminals the state accepts are matched, as when the parser lexes.
   */
  nextToken(): Token | undefined {
    const shared: SharedParserState = { depth: this.parserState.depth, position: this.parserState.position };
    const r = this.lexerThread.lex(shared).next();
    return r.done ? undefined : r.value;
  }

  /** Feeds the end token, with the position of `lastToken` if given. */
  feedEof(lastToken?: Token): Node | undefined {
    return this.feedToken(endToken(lastToken));
  }

  /** A copy with its own parser and lexer state. */
  copy(): InteractiveParser {
    const ip = new InteractiveParser(this.parser, this.parserState.copy(), this.lexerThread.copy());
    ip.result = this.result;
    return ip;
  }

  /** The token types the current state has an action for, with the action. */
  choices(): Map<string, string> {
    const result = new Map<string, string>();
    for (const [k, v] of this.parser.choices(this.parserState)) {
      result.set(k, v.kind === 'shift' ? `Shift(${v.state})` : `Reduce(${v.rule})`);
    }
    return result;
  }

  /** The terminals that move the parser into a valid state. */
  accepts(): string[] {
    return this.parser.accepts(this.parserState);
  }

  /** Continues parsing from the current state until the end of the text. */
  resumeParse(): Node {
    return this.parser.parseFromState(this.parserState, this.lexerThread, this.lexerThread.state.lastToken);
  }

  /** The offset the lexer is at. */
  get lexerPosition(): number {
    return this.lexerThread.state.lineCtr.pos;
  }

  /** Moves the lexer past `n` characters of the text, for skipping unexpected characters. */
  skipChars(n: number): void {
    const state = this.lexerThread.state;
    const pos = state.lineCtr.pos;
    let end = pos;
    for (let i = 0; i < n && end < state.end; i++) end += (state.text.codePointAt(end) ?? 0) > 0xffff ? 2 : 1;
    state.lineCtr.feed(state.text.slice(pos, end), true);
  }

  /** Describes the choices, for debugging. */
  pretty(): string {
    const lines = ['Parser choices:'];
    for (const [k, v] of this.choices()) lines.push(`\t- ${k} -> ${v}`);
    lines.push(`stack size: ${this.parserState.depth}`);
    return lines.join('\n');
  }

  equals(other: InteractiveParser): boolean {
    return this.parserState.equals(other.parserState) && this.lexerThread.state.equals(other.lexerThread.state);
  }
}

/**
 * Parses with error recovery: when the parse fails, `onError` gets the error and the interactive parser at the point
 * of failure, and returns whether to continue (Lark's `on_error`).
 */
export function parseWithRecovery(
  parser: LalrParser,
  start: string,
  lexer: LexerThread,
  onError: (e: UnexpectedInput, ip: InteractiveParser) => boolean,
): Node {
  const ip = new InteractiveParser(parser, parser.newState(start), lexer);
  let e: UnexpectedInput;
  try {
    return ip.resumeParse();
  } catch (err) {
    if (!(err instanceof UnexpectedInput)) throw err;
    e = err;
  }
  for (;;) {
    const pos = ip.lexerPosition;
    if (!onError(e, ip)) throw e;
    // The handler didn't move the lexer: skip the character.
    if (e instanceof UnexpectedCharacters && pos === ip.lexerPosition) ip.skipChars(1);
    const before = ip.parserState.copy();
    try {
      return ip.resumeParse();
    } catch (e2) {
      if (
        e instanceof UnexpectedToken &&
        e2 instanceof UnexpectedToken &&
        e.token.type === '$END' &&
        e2.token.type === '$END' &&
        before.equals(ip.parserState)
      ) {
        // Prevent an infinite loop.
        throw e2;
      }
      if (!(e2 instanceof UnexpectedInput)) throw e2;
      e = e2;
    }
  }
}
