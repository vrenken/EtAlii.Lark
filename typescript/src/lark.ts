// The main interface: compiles a grammar into a parser (Lark's `Lark` class and its parser front-ends).

import { ConfigurationError, GrammarError, ParseError, UnexpectedInput } from './errors.js';
import type { Rule } from './grammar.js';
import {
  BasicLexer,
  ContextualLexer,
  CustomLexer,
  LexerConf,
  LexerImpl,
  LexerState,
  LexerThread,
  PostLex,
  TerminalDef,
  TokenCallback,
} from './lexer.js';
import { Grammar, GrammarBuilder, GrammarLoader } from './loadGrammar.js';
import { Callbacks } from './parseTreeBuilder.js';
import { CykParser } from './parsers/cyk.js';
import { EarleyParser } from './parsers/earley.js';
import { GrammarAnalyzer } from './parsers/grammarAnalysis.js';
import { computeLalr, ParseTable } from './parsers/lalrAnalysis.js';
import { InteractiveParser, LalrParser, parseWithRecovery } from './parsers/lalrParser.js';
import { flagsFromLetters } from './pyre.js';
import type { Token } from './token.js';
import { Node, reprNode, Tree } from './tree.js';

/** The parsing algorithm. `earley` parses any context-free grammar; `lalr` is fast, but only for LALR(1) grammars. */
export type ParserKind = 'earley' | 'lalr' | 'cyk';

/**
 * The lexer. `auto` chooses based on the parser: contextual for LALR, dynamic for Earley, basic for CYK. A
 * {@link CustomLexer} object uses a lexer written by the user.
 */
export type LexerKind = 'auto' | 'basic' | 'contextual' | 'dynamic' | 'dynamic_complete' | CustomLexer;

/** How the Earley parser handles ambiguity: choose the best derivation (`resolve`), or return all of them (`explicit`). */
export type Ambiguity = 'auto' | 'resolve' | 'explicit';

/** How priorities are evaluated: normally, negated (`invert`), or ignored (`null`). */
export type PriorityMode = 'auto' | 'normal' | 'invert' | null;

/** The options of a parser. See Lark's documentation of the same options. */
export interface LarkOptions {
  /** The start rule or rules. Defaults to `start`. */
  start: string | string[];
  /** Display debug information and extra warnings. */
  debug: boolean;
  /** Raises an error on any potential ambiguity: shift/reduce conflicts and regex collisions. */
  strict: boolean;
  /** Don't remove the "punctuation" tokens from the tree. */
  keepAllTokens: boolean;
  parser: ParserKind;
  lexer: LexerKind;
  ambiguity: Ambiguity;
  priority: PriorityMode;
  /** Propagates the positions (line, column, ...) to the `meta` of each tree. */
  propagatePositions: boolean;
  /** When true, the `[]` operator returns `null` when not matched. Defaults to true. */
  maybePlaceholders: boolean;
  /** Regular expression flags applied to all terminals, as letters (`i`, `m`, `s`, `x`), like Lark's `g_regex_flags`. */
  regexFlags: string;
  /** A post-lexing step, such as the {@link Indenter}. Requires the basic or contextual lexer. */
  postlex?: PostLex;
  /** Called for every token of a terminal after it's matched; the result replaces the token. */
  lexerCallbacks: Record<string, TokenCallback>;
  /** Loaders that are tried, in order, for `%import` statements, before the grammars that come with Lark. */
  importPaths: GrammarLoader[];
  /** The path of the grammar, for relative imports and error messages. */
  sourcePath?: string;
}

/** The options with the automatic choices resolved. */
export interface ResolvedOptions extends Omit<LarkOptions, 'start' | 'lexer' | 'ambiguity' | 'priority'> {
  start: string[];
  lexer: Exclude<LexerKind, 'auto' | CustomLexer> | 'custom';
  ambiguity: Ambiguity;
  priority: 'normal' | 'invert' | null;
}

const DEFAULTS: LarkOptions = {
  start: 'start',
  debug: false,
  strict: false,
  keepAllTokens: false,
  parser: 'earley',
  lexer: 'auto',
  ambiguity: 'auto',
  priority: 'auto',
  propagatePositions: false,
  maybePlaceholders: true,
  regexFlags: '',
  lexerCallbacks: {},
  importPaths: [],
};

type ParserImpl = { kind: 'lalr'; parser: LalrParser } | { kind: 'earley'; parser: EarleyParser } | { kind: 'cyk'; parser: CykParser };

/** Called on an unexpected-input error with the interactive parser at the error; returns whether to resume parsing. */
export type OnError = (e: UnexpectedInput, ip: InteractiveParser) => boolean;

/**
 * A parser for a grammar, written in Lark's grammar syntax.
 *
 * ```ts
 * const parser = new Lark('start: WORD "," WORD "!"\n%import common.WORD\n%ignore " "', { parser: 'lalr' });
 * const tree = parser.parse('Hello, World!');
 * ```
 */
export class Lark {
  /** The options, with the automatic choices resolved. */
  readonly options: ResolvedOptions;
  /** The loaded grammar. */
  readonly grammar: Grammar;
  /** The compiled terminals. */
  readonly terminals: TerminalDef[];
  /** The compiled (BNF) rules. */
  readonly rules: Rule[];
  /** The names of the ignored terminals. */
  readonly ignoreTokens: string[];
  private readonly lexerConf: LexerConf;
  private readonly parser: ParserImpl;
  private readonly lexer?: LexerImpl;
  private readonly customLexer?: CustomLexer;

  /** Compiles a grammar (its text, or a {@link Grammar} loaded before) into a parser. */
  constructor(grammar: string | Grammar, options: Partial<LarkOptions> = {}) {
    const opts: LarkOptions = { ...DEFAULTS, ...options };
    const start = typeof opts.start === 'string' ? [opts.start] : [...opts.start];
    if (start.length === 0) throw new ConfigurationError('At least one start rule is required');

    const loaded =
      typeof grammar === 'string' ? GrammarBuilder.load(grammar, opts.sourcePath ?? '<string>', opts.importPaths, opts.keepAllTokens) : grammar;

    let lexer: ResolvedOptions['lexer'];
    let customLexer: CustomLexer | undefined;
    if (typeof opts.lexer === 'object') {
      lexer = 'custom';
      customLexer = opts.lexer;
    } else if (opts.lexer === 'auto') {
      lexer = opts.parser === 'lalr' ? 'contextual' : opts.parser === 'earley' && opts.postlex === undefined ? 'dynamic' : 'basic';
    } else {
      lexer = opts.lexer;
    }
    const dynamic = lexer === 'dynamic' || lexer === 'dynamic_complete';
    if (opts.postlex !== undefined && dynamic) {
      throw new ConfigurationError("Can't use postlex with a dynamic lexer. Use basic or contextual instead");
    }
    let ambiguity = opts.ambiguity;
    if (ambiguity === 'auto') {
      if (opts.parser === 'earley') ambiguity = 'resolve';
    } else if (opts.parser === 'lalr') {
      throw new ConfigurationError("'lalr' doesn't support disambiguation. Use one of these parsers instead: earley, cyk");
    }
    const priority = opts.priority === 'auto' ? 'normal' : opts.priority;
    if (!['earley', 'lalr', 'cyk'].includes(opts.parser)) throw new ConfigurationError(`Invalid parser: ${String(opts.parser)}`);

    this.options = { ...opts, start, lexer, ambiguity, priority };
    this.grammar = loaded;
    this.customLexer = customLexer;

    const terminalsToKeep = new Set(opts.postlex?.alwaysAccept ?? []);

    // Compile the EBNF grammar into BNF
    const compiled = loaded.compile(start, terminalsToKeep);
    const terminals = compiled.terminals;
    const rules = compiled.rules;
    if (priority === 'invert') {
      for (const rule of rules) if (rule.options.priority !== undefined) rule.options.priority = -rule.options.priority;
      for (const t of terminals) t.priority = -t.priority;
    } else if (priority === null) {
      for (const rule of rules) rule.options.priority = undefined;
      for (const t of terminals) t.priority = 0;
    }
    this.terminals = terminals;
    this.rules = rules;
    this.ignoreTokens = compiled.ignore;

    const lexerConf = new LexerConf([...terminals], [...compiled.ignore]);
    lexerConf.callbacks = new Map(Object.entries(opts.lexerCallbacks));
    lexerConf.regexFlags = flagsFromLetters(opts.regexFlags);
    lexerConf.strict = opts.strict;
    this.lexerConf = lexerConf;

    const expected: Record<ParserKind, string[]> = {
      lalr: ['basic', 'contextual'],
      earley: ['basic', 'dynamic', 'dynamic_complete'],
      cyk: ['basic'],
    };
    if (lexer !== 'custom' && !expected[opts.parser].includes(lexer)) {
      throw new ConfigurationError(`Parser ${opts.parser} does not support lexer ${lexer}, expected one of ${expected[opts.parser].join(', ')}`);
    }

    const ambiguous = opts.parser !== 'lalr' && ambiguity === 'explicit';
    const callbacks = new Callbacks(rules, opts.propagatePositions, ambiguous, opts.maybePlaceholders);

    // Set up the parser, then the lexer
    let lalrTable: ParseTable | undefined;
    if (opts.parser === 'lalr') {
      const analyzer = new GrammarAnalyzer(rules, start, opts.debug, opts.strict);
      lalrTable = computeLalr(analyzer);
      this.parser = { kind: 'lalr', parser: new LalrParser(lalrTable, callbacks) };
    } else if (opts.parser === 'earley') {
      if (dynamic && Object.keys(opts.lexerCallbacks).length > 0) {
        throw new GrammarError("Earley's dynamic lexer doesn't support lexer_callbacks.");
      }
      this.parser = {
        kind: 'earley',
        parser: new EarleyParser(
          rules,
          start,
          callbacks,
          terminals,
          lexerConf.terminalsByName,
          compiled.ignore,
          lexerConf.regexFlags,
          dynamic ? { completeLex: lexer === 'dynamic_complete' } : undefined,
          ambiguity === 'resolve',
        ),
      };
    } else {
      this.parser = { kind: 'cyk', parser: new CykParser(rules, callbacks) };
    }

    if (lexer === 'basic') {
      this.lexer = { kind: 'basic', lexer: new BasicLexer(lexerConf) };
    } else if (lexer === 'contextual') {
      const states = new Map<number, string[]>();
      lalrTable!.states.forEach((s, i) => states.set(i, [...s.keys()]));
      this.lexer = { kind: 'contextual', lexer: new ContextualLexer(lexerConf, states, [...(opts.postlex?.alwaysAccept ?? [])]) };
    } else if (lexer === 'custom') {
      this.lexer = { kind: 'custom', lexer: customLexer! };
    }
  }

  /** Loads a grammar without compiling it, for example to compile it several times with different options. */
  static loadGrammar(text: string, options: Partial<Pick<LarkOptions, 'sourcePath' | 'importPaths' | 'keepAllTokens'>> = {}): Grammar {
    return GrammarBuilder.load(text, options.sourcePath ?? '<string>', options.importPaths ?? [], options.keepAllTokens ?? false);
  }

  /** A terminal by name. */
  getTerminal(name: string): TerminalDef | undefined {
    return this.terminals.find((t) => t.name === name);
  }

  private verifyStart(start?: string): string {
    const starts = this.options.start;
    if (start === undefined) {
      if (starts.length > 1) {
        throw new ConfigurationError(
          `Lark initialized with more than 1 possible start rule. Must specify which start rule to parse: [${starts.join(', ')}]`,
        );
      }
      return starts[0]!;
    }
    if (!starts.includes(start)) throw new ConfigurationError(`Unknown start rule ${start}. Must be one of [${starts.join(', ')}]`);
    return start;
  }

  private thread(text: string): LexerThread {
    return new LexerThread(this.lexer!, this.options.postlex, new LexerState(text));
  }

  /**
   * Parses the text and returns the tree. Fails when the result isn't a tree (a `?start` rule that inlines a single
   * token); use {@link Lark.parseNode} for those. With `onError` (LALR only), the parser recovers from errors.
   */
  parse(text: string, start?: string, onError?: OnError): Tree {
    const node = this.parseNode(text, start, onError);
    if (node instanceof Tree) return node;
    throw new ParseError(`The parse result is not a tree: ${reprNode(node)}`);
  }

  /** Parses the text and returns the result: a tree, or a token when the start rule inlines a single token. */
  parseNode(text: string, start?: string, onError?: OnError): Node {
    const s = this.verifyStart(start);
    if (onError !== undefined) {
      if (this.parser.kind !== 'lalr') throw new ConfigurationError('The on_error option is only implemented for the LALR(1) parser.');
      return parseWithRecovery(this.parser.parser, s, this.thread(text), onError);
    }
    switch (this.parser.kind) {
      case 'lalr': {
        const p = this.parser.parser;
        return p.parseFromState(p.newState(s), this.thread(text));
      }
      case 'earley':
        return this.lexer === undefined
          ? this.parser.parser.parse({ text }, s)
          : this.parser.parser.parse({ tokens: this.thread(text).lex() }, s);
      case 'cyk': {
        const tokens: Token[] = [];
        const stream = this.thread(text).lex();
        for (let r = stream.next(); !r.done; r = stream.next()) tokens.push(r.value);
        return this.parser.parser.parse(tokens, s);
      }
    }
  }

  /** Starts an interactive parse of the text (LALR only). */
  parseInteractive(text = '', start?: string): InteractiveParser {
    const s = this.verifyStart(start);
    if (this.parser.kind !== 'lalr') throw new ConfigurationError('parse_interactive() currently only works with the LALR parser');
    const p = this.parser.parser;
    return new InteractiveParser(p, p.newState(s), this.thread(text));
  }

  /**
   * The LALR(1) parse table, for a LALR parser (Lark's `parser.parser.parser.parse_table`): for each state, the action
   * on each terminal and the goto on each rule, with the start and end state of each start rule.
   */
  parseTable(): ParseTable | undefined {
    return this.parser.kind === 'lalr' ? this.parser.parser.table : undefined;
  }

  /** Only lexes the text, with the basic lexer (and the post-lexer). With `dontIgnore`, the ignored tokens are returned too. */
  *lex(text: string, dontIgnore = false): Generator<Token> {
    const conf = this.lexerConf.copy();
    if (dontIgnore) conf.ignore = [];
    const lexer: LexerImpl =
      this.customLexer !== undefined ? { kind: 'custom', lexer: this.customLexer } : { kind: 'basic', lexer: new BasicLexer(conf) };
    const stream = new LexerThread(lexer, this.options.postlex, new LexerState(text)).lex();
    for (let r = stream.next(); !r.done; r = stream.next()) yield r.value;
  }

  toString(): string {
    return `Lark(source=${JSON.stringify(this.options.sourcePath ?? '<string>')}, parser=${this.options.parser}, lexer=${this.options.lexer})`;
  }
}
