// Terminals and the lexers (Lark's `lark.lexer`).

import { checkCollisions } from './collisions.js';
import { ErrorState, LexError, TerminalsByName, UnexpectedCharacters, UnexpectedToken } from './errors.js';
import { TOKEN_DEFAULT_PRIORITY } from './grammar.js';
import { escape, Flags, flagsFromLetters, MAX_WIDTH, Regex, RegexError, width as regexWidth } from './pyre.js';
import { Token } from './token.js';
import { codePointLength, compareStrings, repr } from './utils.js';

function sortedFlags(flags: string): string {
  return [...new Set(flags)].sort().join('');
}

/**
 * What a terminal matches: a literal string or a regular expression (Lark's `PatternStr` and `PatternRE`). Regular
 * expressions are written in Python's `re` dialect, as in Lark.
 */
export class Pattern {
  readonly type: 'str' | 're';
  readonly value: string;
  /** The flags (any of `imslux`), sorted. */
  readonly flags: string;
  /** The pattern as written in the grammar, when known. */
  readonly raw?: string;
  private _width?: [number, number] | RegexError;

  private constructor(type: 'str' | 're', value: string, flags: string, raw?: string) {
    this.type = type;
    this.value = value;
    this.flags = sortedFlags(flags);
    this.raw = raw;
  }

  /** A literal string. */
  static string(value: string, flags = '', raw?: string): Pattern {
    return new Pattern('str', value, flags, raw);
  }

  /** A regular expression in Python's dialect. */
  static regex(value: string, flags = '', raw?: string): Pattern {
    return new Pattern('re', value, flags, raw);
  }

  get isStr(): boolean {
    return this.type === 'str';
  }

  /** A string that identifies the pattern: equal patterns have equal keys. */
  get key(): string {
    return this.type + '\u0000' + this.flags + '\u0000' + this.value;
  }

  private withFlags(value: string): string {
    for (const f of this.flags) value = `(?${f}:${value})`;
    return value;
  }

  /** The pattern as a regular expression, in Python's dialect. */
  toRegexp(): string {
    return this.withFlags(this.isStr ? escape(this.value) : this.value);
  }

  /** @internal */
  width(): [number, number] {
    if (this.isStr) {
      const n = codePointLength(this.value);
      return [n, n];
    }
    if (this._width === undefined) {
      try {
        this._width = regexWidth(this.toRegexp());
      } catch (e) {
        if (!(e instanceof RegexError)) throw e;
        this._width = e;
      }
    }
    if (this._width instanceof RegexError) throw this._width;
    return this._width;
  }

  /** The minimum number of characters the pattern matches. */
  get minWidth(): number {
    try {
      return this.width()[0];
    } catch {
      return 0;
    }
  }

  /** The maximum number of characters the pattern matches (`2 ** 64` when unbounded, as in Python). */
  get maxWidth(): number {
    try {
      return this.width()[1];
    } catch {
      return MAX_WIDTH;
    }
  }

  repr(): string {
    return repr(this.toRegexp());
  }

  toString(): string {
    return this.repr();
  }
}

/** A terminal: its name, pattern and priority (Lark's `TerminalDef`). */
export class TerminalDef {
  name: string;
  pattern: Pattern;
  priority: number;

  constructor(name: string, pattern: Pattern, priority = TOKEN_DEFAULT_PRIORITY) {
    this.name = name;
    this.pattern = pattern;
    this.priority = priority;
  }

  /** How the terminal is shown in error messages: the pattern for anonymous terminals, otherwise the name. */
  userRepr(): string {
    return this.name.startsWith('__') ? (this.pattern.raw ?? this.name) : this.name;
  }

  toString(): string {
    return `TerminalDef(${repr(this.name)}, ${this.pattern.repr()})`;
  }
}

/** A callback that replaces each token of a terminal (Lark's `lexer_callbacks`). */
export type TokenCallback = (token: Token) => Token;

/** @internal The configuration of a lexer (Lark's `LexerConf`). */
export class LexerConf {
  terminals: TerminalDef[];
  terminalsByName: TerminalsByName;
  ignore: string[];
  callbacks: Map<string, TokenCallback> = new Map();
  regexFlags: Flags = flagsFromLetters('');
  skipValidation = false;
  strict = false;

  constructor(terminals: TerminalDef[], ignore: string[]) {
    this.terminals = terminals;
    this.terminalsByName = new Map(terminals.map((t) => [t.name, t]));
    this.ignore = ignore;
  }

  withTerminals(terminals: TerminalDef[]): LexerConf {
    const conf = Object.assign(Object.create(LexerConf.prototype), this) as LexerConf;
    conf.terminals = terminals;
    return conf;
  }

  copy(): LexerConf {
    return this.withTerminals(this.terminals);
  }
}

/** Keeps track of the line and column while the lexer advances (Lark's `LineCounter`). */
export class LineCounter {
  pos = 0;
  line = 1;
  column = 1;
  lineStartPos = 0;

  /** Consumes a token and updates the line and column. */
  feed(token: string, testNewline = true): void {
    if (testNewline) {
      let newlines = 0;
      let last = -1;
      for (let i = token.indexOf('\n'); i !== -1; i = token.indexOf('\n', i + 1)) {
        newlines++;
        last = i;
      }
      if (newlines > 0) {
        this.line += newlines;
        this.lineStartPos = this.pos + last + 1;
      }
    }
    this.pos += token.length;
    this.column = this.pos - this.lineStartPos + 1;
  }

  copy(): LineCounter {
    return Object.assign(new LineCounter(), this);
  }
}

/** @internal The state of the lexer as it scans a text (Lark's `LexerState`). */
export class LexerState {
  readonly text: string;
  /** Where lexing stops. */
  readonly end: number;
  lineCtr: LineCounter;
  lastToken?: Token;

  constructor(text: string, start = 0, end = text.length) {
    this.text = text;
    this.end = end;
    this.lineCtr = new LineCounter();
    if (start > 0) this.lineCtr.feed(text.slice(0, start), true);
  }

  copy(): LexerState {
    const s = Object.create(LexerState.prototype) as LexerState;
    Object.assign(s, this);
    s.lineCtr = this.lineCtr.copy();
    return s;
  }

  equals(other: LexerState): boolean {
    const a = this.lastToken;
    const b = other.lastToken;
    return (
      this.text === other.text &&
      this.end === other.end &&
      this.lineCtr.pos === other.lineCtr.pos &&
      (a === b || (a !== undefined && b !== undefined && a.equals(b)))
    );
  }
}

/** @internal */
export function compileTerminal(t: TerminalDef, flags: Flags): Regex {
  try {
    return new Regex(t.pattern.toRegexp(), flags);
  } catch (e) {
    if (e instanceof RegexError) throw new LexError(`Cannot compile token ${t.name}: ${t.pattern.repr()}`);
    throw e;
  }
}

/**
 * Matches the text against the terminals, in order: the first terminal that matches wins, as in a Python
 * alternation (Lark's `Scanner`).
 */
class Scanner {
  readonly terminals: TerminalDef[];
  private readonly regexes: Regex[];
  readonly allowedTypes: string[];

  constructor(terminals: TerminalDef[], flags: Flags) {
    this.terminals = terminals;
    this.regexes = terminals.map((t) => compileTerminal(t, flags));
    this.allowedTypes = terminals.map((t) => t.name);
  }

  /** The end of the match at `pos` and the index of the terminal that matched. */
  matchAt(text: string, pos: number): [number, number] | null {
    for (let i = 0; i < this.regexes.length; i++) {
      const end = this.regexes[i]!.matchAt(text, pos);
      if (end !== null) return [end, i];
    }
    return null;
  }

  fullMatch(text: string): string | null {
    for (let i = 0; i < this.regexes.length; i++) if (this.regexes[i]!.fullMatch(text)) return this.terminals[i]!.name;
    return null;
  }
}

/** Lark's `_regexp_has_newline`. */
export function regexpHasNewline(r: string): boolean {
  return r.includes('\n') || r.includes('\\n') || r.includes('\\s') || r.includes('[^') || (r.includes('(?s') && r.includes('.'));
}

type Callback = (token: Token) => Token;

function unlessCallback(scanner: Scanner): Callback {
  return (t) => {
    const type = scanner.fullMatch(t.value);
    if (type !== null) t.type = type;
    return t;
  };
}

/** A lexer that matches every terminal, regardless of the parser state (Lark's `BasicLexer`). */
export class BasicLexer {
  /** The terminals, in the order the lexer tries them. */
  readonly terminals: TerminalDef[];
  private readonly ignoreTypes: Set<string>;
  private readonly newlineTypes: Set<string>;
  private readonly userCallbacks: Map<string, TokenCallback>;
  private readonly flags: Flags;
  /** @internal */
  readonly terminalsByName: TerminalsByName;
  private built?: [Scanner, Map<string, Callback>];

  constructor(conf: LexerConf) {
    const terminals = [...conf.terminals];
    if (!conf.skipValidation) {
      for (const t of terminals) {
        compileTerminal(t, conf.regexFlags);
        if (t.pattern.minWidth === 0) {
          throw new LexError(`Lexer does not allow zero-width terminals. (${t.name}: ${t.pattern.repr()})`);
        }
      }
      const names = new Set(terminals.map((t) => t.name));
      const missing = conf.ignore.filter((i) => !names.has(i)).map(repr);
      if (missing.length > 0) throw new LexError(`Ignore terminals are not defined: {${missing.join(', ')}}`);
      if (conf.strict) checkCollisions(terminals, conf.regexFlags);
    }
    this.newlineTypes = new Set(terminals.filter((t) => regexpHasNewline(t.pattern.toRegexp())).map((t) => t.name));
    terminals.sort(
      (a, b) =>
        b.priority - a.priority ||
        b.pattern.maxWidth - a.pattern.maxWidth ||
        codePointLength(b.pattern.value) - codePointLength(a.pattern.value) ||
        compareStrings(a.name, b.name),
    );
    this.terminals = terminals;
    this.ignoreTypes = new Set(conf.ignore);
    this.userCallbacks = conf.callbacks;
    this.flags = conf.regexFlags;
    this.terminalsByName = conf.terminalsByName;
  }

  private build(): [Scanner, Map<string, Callback>] {
    if (this.built === undefined) {
      const [terminals, callbacks] = createUnless(this.terminals, this.flags);
      for (const [type, f] of this.userCallbacks) {
        const unless = callbacks.get(type);
        callbacks.set(
          type,
          unless === undefined
            ? f
            : (t) => {
                const t2 = unless(Token.newBorrowPos(t.type, t.value, t));
                return t2.type === type ? f(t) : t2;
              },
        );
      }
      this.built = [new Scanner(terminals, this.flags), callbacks];
    }
    return this.built;
  }

  /** The next token, or undefined at the end of the text. */
  nextToken(state: LexerState, parserState?: ErrorState): Token | undefined {
    const [scanner, callbacks] = this.build();
    const text = state.end === state.text.length ? state.text : state.text.slice(0, state.end);
    const lineCtr = state.lineCtr;
    while (lineCtr.pos < state.end) {
      const m = scanner.matchAt(text, lineCtr.pos);
      if (m === null) {
        let allowed = scanner.allowedTypes.filter((t) => !this.ignoreTypes.has(t));
        if (allowed.length === 0) allowed = ['<END-OF-FILE>'];
        throw new UnexpectedCharacters(
          state.text,
          lineCtr.pos,
          lineCtr.line,
          lineCtr.column,
          allowed,
          parserState,
          state.lastToken ? [state.lastToken] : undefined,
          this.terminalsByName,
        );
      }
      const [end, index] = m;
      const type = scanner.terminals[index]!.name;
      const value = text.slice(lineCtr.pos, end);
      const ignored = this.ignoreTypes.has(type);
      const callback = callbacks.get(type);
      let token: Token | undefined;
      if (!ignored || callback !== undefined) token = new Token(type, value, lineCtr.pos, lineCtr.line, lineCtr.column);
      lineCtr.feed(value, this.newlineTypes.has(type));
      if (token !== undefined) {
        token.endLine = lineCtr.line;
        token.endColumn = lineCtr.column;
        token.endPos = lineCtr.pos;
        const cb = callbacks.get(token.type);
        if (cb !== undefined) token = cb(token);
        if (!ignored) {
          state.lastToken = token;
          return token;
        }
      }
    }
    return undefined;
  }
}

function createUnless(terminals: TerminalDef[], flags: Flags): [TerminalDef[], Map<string, Callback>] {
  const strs = terminals.filter((t) => t.pattern.isStr);
  const embedded = new Set<string>();
  const callbacks = new Map<string, Callback>();
  for (const retok of terminals) {
    if (retok.pattern.isStr) continue;
    const re = compileTerminal(retok, flags);
    const unless: TerminalDef[] = [];
    for (const strtok of strs) {
      if (strtok.priority !== retok.priority) continue;
      const s = strtok.pattern.value;
      if (re.matchAt(s, 0) === s.length) {
        unless.push(strtok);
        if ([...strtok.pattern.flags].every((f) => retok.pattern.flags.includes(f))) embedded.add(strtok.name);
      }
    }
    if (unless.length > 0) callbacks.set(retok.name, unlessCallback(new Scanner(unless, flags)));
  }
  return [terminals.filter((t) => !embedded.has(t.name)), callbacks];
}

/** A lexer that only matches the terminals the LALR parser accepts in its current state (Lark's `ContextualLexer`). */
export class ContextualLexer {
  private readonly lexers = new Map<number, BasicLexer>();
  /** @internal */
  readonly rootLexer: BasicLexer;

  constructor(conf: LexerConf, states: Map<number, string[]>, alwaysAccept: string[]) {
    const byTokens = new Map<string, BasicLexer>();
    for (const [state, accepts] of states) {
      const keySet = [...new Set(accepts)].sort();
      const key = keySet.join('\u0000');
      let lexer = byTokens.get(key);
      if (lexer === undefined) {
        const all = [...new Set([...keySet, ...conf.ignore, ...alwaysAccept])].sort();
        const terminals = all.map((n) => conf.terminalsByName.get(n)).filter((t): t is TerminalDef => t !== undefined);
        lexer = new BasicLexer(conf.withTerminals(terminals));
        byTokens.set(key, lexer);
      }
      this.lexers.set(state, lexer);
    }
    const rootConf = conf.copy();
    rootConf.skipValidation = true;
    this.rootLexer = new BasicLexer(rootConf);
  }

  nextToken(state: LexerState, parserState: ErrorState): Token | undefined {
    const position = parserState.kind === 'lalr' ? parserState.position : 0;
    const lexer = this.lexers.get(position)!;
    try {
      return lexer.nextToken(state, parserState);
    } catch (e) {
      if (!(e instanceof UnexpectedCharacters)) throw e;
      // The terminal may be defined, but not expected in this state: the root lexer gives a nicer error.
      const lastToken = state.lastToken;
      let token: Token | undefined;
      try {
        token = this.rootLexer.nextToken(state, parserState);
      } catch {
        throw e;
      }
      if (token === undefined) throw e;
      const ut = new UnexpectedToken(token, e.allowed ?? [], parserState, this.rootLexer.terminalsByName);
      ut.tokenHistory = lastToken ? [lastToken] : [];
      ut.refreshMessage();
      throw ut;
    }
  }
}

/** A lexer written by the user, for the `custom` lexer (Lark's `Lexer` interface). */
export interface CustomLexer {
  /** Splits the text into tokens. */
  lex(text: string): Iterable<Token>;
}

/** Post-processes the tokens of the lexer, such as the {@link Indenter} (Lark's `PostLex`). */
export interface PostLex {
  /** Transforms the token stream. */
  process(stream: Iterable<Token>): Iterable<Token>;
  /** Terminals the contextual lexer must always accept, because the post-lexer consumes them. */
  readonly alwaysAccept?: readonly string[];
}

/** @internal The parser state that the contextual lexer reads to decide which terminals to match. */
export interface SharedParserState {
  depth: number;
  position: number;
}

/** @internal The lexer of a parser front-end. */
export type LexerImpl =
  | { kind: 'basic'; lexer: BasicLexer }
  | { kind: 'contextual'; lexer: ContextualLexer }
  | { kind: 'custom'; lexer: CustomLexer };

/** @internal */
export function lexerTerminalsByName(lexer: LexerImpl): TerminalsByName | undefined {
  if (lexer.kind === 'basic') return lexer.lexer.terminalsByName;
  if (lexer.kind === 'contextual') return lexer.lexer.rootLexer.terminalsByName;
  return undefined;
}

function* lexWith(lexer: LexerImpl, state: LexerState, parserState?: SharedParserState): Generator<Token> {
  if (lexer.kind === 'custom') {
    yield* lexer.lexer.lex(state.text.slice(state.lineCtr.pos, state.end));
    return;
  }
  for (;;) {
    let token: Token | undefined;
    if (lexer.kind === 'basic') {
      const ps: ErrorState | undefined =
        parserState === undefined ? undefined : { kind: 'lalr', depth: parserState.depth, position: parserState.position };
      token = lexer.lexer.nextToken(state, ps);
    } else {
      token = lexer.lexer.nextToken(state, {
        kind: 'lalr',
        depth: parserState?.depth ?? 0,
        position: parserState?.position ?? 0,
      });
    }
    if (token === undefined) return;
    yield token;
  }
}

/** @internal Ties a lexer to the state of the text it lexes, for the parser (Lark's `LexerThread`). */
export class LexerThread {
  readonly lexer: LexerImpl;
  readonly postlex?: PostLex;
  state: LexerState;

  constructor(lexer: LexerImpl, postlex: PostLex | undefined, state: LexerState) {
    this.lexer = lexer;
    this.postlex = postlex;
    this.state = state;
  }

  /** The tokens from the current position on; the contextual lexer reads `parserState` as it goes. */
  lex(parserState?: SharedParserState): Iterator<Token> {
    const stream = lexWith(this.lexer, this.state, parserState);
    return this.postlex ? this.postlex.process(stream)[globalThis.Symbol.iterator]() : stream;
  }

  /** A thread with a copy of the state. */
  copy(): LexerThread {
    return new LexerThread(this.lexer, this.postlex, this.state.copy());
  }
}
