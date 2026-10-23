// Loading grammars: the parser of Lark's grammar syntax, `%import` and the other statements, and the compilation of
// the EBNF rules into BNF (Lark's `load_grammar`).

import { getContext, GrammarError, UnexpectedCharacters, UnexpectedToken } from './errors.js';
import { Rule, RuleOptions, ruleOptions, Symbol, TOKEN_DEFAULT_PRIORITY } from './grammar.js';
import { STANDARD_GRAMMARS } from './grammars.js';
import { BasicLexer, LexerConf, LexerState, LexerThread, Pattern, TerminalDef } from './lexer.js';
import { Callbacks } from './parseTreeBuilder.js';
import { GrammarAnalyzer } from './parsers/grammarAnalysis.js';
import { computeLalr } from './parsers/lalrAnalysis.js';
import { LalrParser } from './parsers/lalrParser.js';
import { Token } from './token.js';
import { Node, Tree } from './tree.js';
import { bfs, classify, codePointLength, isIdContinue, isIdStart, isUpper, repr, smallFactors } from './utils.js';

// ---------------------------------------------------------------------------------------------------------------
//  Grammar trees
// ---------------------------------------------------------------------------------------------------------------

/** The positions of the items of a `[maybe]` that didn't match (Lark's `_EMPTY`). */
const EMPTY = { empty: true } as const;
type Empty = typeof EMPTY;

/** A node of a grammar's tree, as the grammar is loaded and compiled. */
type GNode = GTree | Token | Symbol | Pattern | Empty;

class GTree {
  constructor(
    public data: string,
    public children: GNode[],
  ) {}
}

const st = (data: string, children: GNode[]): GTree => new GTree(data, children);

/** A deep copy: trees are mutated while they are compiled. */
function cloneG<T extends GNode>(node: T): T {
  return (node instanceof GTree ? new GTree(node.data, node.children.map(cloneG)) : node) as T;
}

/** A string that identifies the node: equal nodes have equal keys. */
function gkey(node: GNode): string {
  if (node instanceof GTree) return `(${node.data}${node.children.map((c) => ' ' + gkey(c)).join('')})`;
  if (node instanceof Token) return `K${repr(node.type)}${repr(node.value)}`;
  if (node instanceof Symbol) return `S${node.key}`;
  if (node instanceof Pattern) return `P${repr(node.key)}`;
  return 'E';
}

function describe(node: GNode): string {
  if (node instanceof GTree) return `Tree(${repr(node.data)}, [${node.children.map(describe).join(', ')}])`;
  if (node instanceof Token) return node.repr();
  if (node instanceof Symbol) return node.repr();
  if (node instanceof Pattern) return node.repr();
  return "Symbol('__empty__')";
}

const asToken = (node: GNode | undefined): Token | undefined => (node instanceof Token ? node : undefined);
const asTree = (node: GNode | undefined): GTree | undefined => (node instanceof GTree ? node : undefined);

/** All subtrees in the order of Lark's `iter_subtrees`: bottom-up, the root last. */
function subtrees(tree: GTree): GTree[] {
  const queue: GTree[] = [tree];
  for (let i = 0; i < queue.length; i++) {
    const t = queue[i]!;
    for (let j = t.children.length - 1; j >= 0; j--) {
      const c = t.children[j]!;
      if (c instanceof GTree) queue.push(c);
    }
  }
  return queue.reverse();
}

/** The subtrees with the given data, in the order of Lark's `find_data`. */
function findData(tree: GTree, data: string): GTree[] {
  return subtrees(tree).filter((t) => t.data === data);
}

function scanSymbols(tree: GTree, out: Symbol[]): void {
  for (const c of tree.children) {
    if (c instanceof GTree) scanSymbols(c, out);
    else if (c instanceof Symbol) out.push(c);
  }
}

function expandKidsByData(tree: GTree, data: string): boolean {
  let changed = false;
  for (let i = tree.children.length - 1; i >= 0; i--) {
    const c = tree.children[i]!;
    if (c instanceof GTree && c.data === data) {
      tree.children.splice(i, 1, ...c.children);
      changed = true;
    }
  }
  return changed;
}

function flatten(tree: GTree): void {
  while (expandKidsByData(tree, tree.data));
}

function renameSymbols(tree: GTree, f: (name: string) => string): void {
  tree.children.forEach((c, i) => {
    if (c instanceof GTree) renameSymbols(c, f);
    else if (c instanceof Symbol) tree.children[i] = c.renamed(f);
  });
}

/** Lark's `Transformer_InPlace`: replaces each subtree, bottom-up, with the result of `f`. */
function inPlace(tree: GTree, f: (t: GTree) => GNode): GNode {
  for (const sub of subtrees(tree)) sub.children = sub.children.map((c) => (c instanceof GTree ? f(c) : c));
  return f(tree);
}

function inPlaceTree(tree: GTree, f: (t: GTree) => GNode): GTree {
  const result = inPlace(tree, f);
  if (!(result instanceof GTree)) throw new GrammarError(`Unexpected rule structure: ${describe(result)}`);
  return result;
}

function dedup(items: GNode[]): GNode[] {
  const map = new Map<string, GNode>();
  for (const item of items) {
    const k = gkey(item);
    if (!map.has(k)) map.set(k, item);
  }
  return [...map.values()];
}

function fromNode(node: Node): GNode {
  if (node instanceof Tree) return new GTree(node.data, node.children.map(fromNode));
  if (node instanceof Token) return node;
  return EMPTY;
}

// ---------------------------------------------------------------------------------------------------------------
//  The meta grammar: the grammar of Lark grammars
// ---------------------------------------------------------------------------------------------------------------

const META_TERMINALS: Array<[string, string]> = [
  ['_LPAR', '\\('],
  ['_RPAR', '\\)'],
  ['_LBRA', '\\['],
  ['_RBRA', '\\]'],
  ['_LBRACE', '\\{'],
  ['_RBRACE', '\\}'],
  ['OP', '[+*]|[?](?![a-z_])'],
  ['_COLON', ':'],
  ['_COMMA', ','],
  ['_OR', '\\|'],
  ['_DOT', '\\.(?!\\.)'],
  ['_DOTDOT', '\\.\\.'],
  ['TILDE', '~'],
  ['RULE_MODIFIERS', '(!|![?]?|[?]!?)(?=[_a-z])'],
  ['RULE', '_?[a-z][_a-z0-9]*'],
  ['TERMINAL', '_?[A-Z][_A-Z0-9]*'],
  ['STRING', '"(\\\\"|\\\\\\\\|[^"\\n])*?"i?'],
  ['REGEXP', '/(?!/)(\\\\/|\\\\\\\\|[^/])*?/[imslux]*'],
  ['_NL', '(\\r?\\n)+\\s*'],
  ['_NL_OR', '(\\r?\\n)+\\s*\\|'],
  ['WS', '[ \\t]+'],
  ['COMMENT', '\\s*//[^\\n]*|\\s*#[^\\n]*'],
  ['BACKSLASH', '\\\\[ ]*\\n'],
  ['_TO', '->'],
  ['_IGNORE', '%ignore'],
  ['_OVERRIDE', '%override'],
  ['_DECLARE', '%declare'],
  ['_EXTEND', '%extend'],
  ['_IMPORT', '%import'],
  ['NUMBER', '[+-]?\\d+'],
];

const META_RULES: Array<[string, string[]]> = [
  ['start', ['_list']],
  ['_list', ['_item', '_list _item']],
  ['_item', ['rule', 'term', 'ignore', 'import', 'declare', 'override', 'extend', '_NL']],
  ['rule', ['rule_modifiers RULE template_params priority _COLON expansions _NL']],
  ['rule_modifiers', ['RULE_MODIFIERS', '']],
  ['priority', ['_DOT NUMBER', '']],
  ['template_params', ['_LBRACE _template_params _RBRACE', '']],
  ['_template_params', ['RULE', '_template_params _COMMA RULE']],
  ['expansions', ['_expansions']],
  ['_expansions', ['alias', '_expansions _OR alias', '_expansions _NL_OR alias']],
  ['?alias', ['expansion _TO nonterminal', 'expansion']],
  ['expansion', ['_expansion']],
  ['_expansion', ['', '_expansion expr']],
  ['?expr', ['atom', 'atom OP', 'atom TILDE NUMBER', 'atom TILDE NUMBER _DOTDOT NUMBER']],
  ['?atom', ['_LPAR expansions _RPAR', 'maybe', 'value']],
  ['value', ['terminal', 'nonterminal', 'literal', 'range', 'template_usage']],
  ['terminal', ['TERMINAL']],
  ['nonterminal', ['RULE']],
  ['?name', ['RULE', 'TERMINAL']],
  ['?symbol', ['terminal', 'nonterminal']],
  ['maybe', ['_LBRA expansions _RBRA']],
  ['range', ['STRING _DOTDOT STRING']],
  ['template_usage', ['nonterminal _LBRACE _template_args _RBRACE']],
  ['_template_args', ['value', '_template_args _COMMA value']],
  ['term', ['TERMINAL _COLON expansions _NL', 'TERMINAL _DOT NUMBER _COLON expansions _NL']],
  ['override', ['_OVERRIDE rule', '_OVERRIDE term']],
  ['extend', ['_EXTEND rule', '_EXTEND term']],
  ['ignore', ['_IGNORE expansions _NL']],
  ['declare', ['_DECLARE _declare_args _NL']],
  ['import', ['_IMPORT _import_path _NL', '_IMPORT _import_path _LPAR name_list _RPAR _NL', '_IMPORT _import_path _TO name _NL']],
  ['_import_path', ['import_lib', 'import_rel']],
  ['import_lib', ['_import_args']],
  ['import_rel', ['_DOT _import_args']],
  ['_import_args', ['name', '_import_args _DOT name']],
  ['name_list', ['_name_list']],
  ['_name_list', ['name', '_name_list _COMMA name']],
  ['_declare_args', ['symbol', '_declare_args symbol']],
  ['literal', ['REGEXP', 'STRING']],
];

/** Example grammars for each syntax error, used to describe the errors (Lark's `GRAMMAR_ERRORS`). */
export const GRAMMAR_ERRORS: Array<[string, string[]]> = [
  ['Incorrect type of value', ['a: 1\n']],
  ['Unclosed parenthesis', ['a: (\n']],
  ['Unmatched closing parenthesis', ['a: )\n', 'a: [)\n', 'a: (]\n']],
  ['Expecting rule or terminal definition (missing colon)', ['a\n', 'A\n', 'a->\n', 'A->\n', 'a A\n']],
  ['Illegal name for rules or terminals', ['Aa:\n']],
  ['Alias expects lowercase name', ['a: -> "a"\n']],
  ['Unexpected colon', ['a::\n', 'a: b:\n', 'a: B:\n', 'a: "a":\n']],
  ['Misplaced operator', ['a: b??', 'a: b(?)', 'a:+\n', 'a:?\n', 'a:*\n', 'a:|*\n']],
  ['Expecting option ("|") or a new rule or terminal definition', ['a:a\n()\n']],
  ['Terminal names cannot contain dots', ['A.B\n']],
  ['Expecting rule or terminal definition', ['"a"\n']],
  ['%import expects a name', ['%import "a"\n']],
  ['%ignore expects a value', ['%ignore %import\n']],
];

function symbolFromStrcase(s: string): Symbol {
  return isUpper(s) ? Symbol.terminal(s, s.startsWith('_')) : Symbol.rule(s);
}

class MetaParser {
  private static instance?: MetaParser;
  private readonly parser: LalrParser;
  private readonly lexer: BasicLexer;

  private constructor() {
    const terminals = META_TERMINALS.map(([n, p]) => new TerminalDef(n, Pattern.regex(p)));
    const rules: Rule[] = [];
    for (const [name, expansions] of META_RULES) {
      const options = ruleOptions({ expand1: name.startsWith('?') });
      const origin = Symbol.rule(name.replace(/^\?+/, ''));
      expansions.forEach((e, i) => {
        const expansion = e.split(/\s+/).filter((s) => s !== '').map(symbolFromStrcase);
        rules.push(new Rule(origin, expansion, i, undefined, { ...options }));
      });
    }
    const analyzer = new GrammarAnalyzer(rules, ['start']);
    const table = computeLalr(analyzer);
    const callbacks = new Callbacks(rules, false, false, false);
    this.parser = new LalrParser(table, callbacks);
    this.lexer = new BasicLexer(new LexerConf(terminals, ['WS', 'COMMENT', 'BACKSLASH']));
  }

  static get(): MetaParser {
    if (MetaParser.instance === undefined) MetaParser.instance = new MetaParser();
    return MetaParser.instance;
  }

  parse(text: string): Node {
    const thread = new LexerThread({ kind: 'basic', lexer: this.lexer }, undefined, new LexerState(text));
    return this.parser.parseFromState(this.parser.newState('start'), thread);
  }
}

function translateParserError(e: UnexpectedToken): string | undefined {
  const meta = MetaParser.get();
  const label = e.matchExamples((s) => meta.parse(s), GRAMMAR_ERRORS, false, true);
  if (label !== undefined) return label;
  if (e.expected.includes('STRING')) return 'Expecting a value';
  return undefined;
}

/** Parses the text of a grammar into its tree (Lark's `_parse_grammar`). */
function parseGrammar(text: string, name: string): GTree {
  let parsed: Node;
  try {
    parsed = MetaParser.get().parse(`${text}\n`);
  } catch (e) {
    if (e instanceof UnexpectedCharacters) {
      const context = getContext(text, e.posInStream ?? 0, 40);
      throw new GrammarError(`Unexpected input at line ${e.line} column ${e.column} in ${name}: \n\n${context}`);
    }
    if (e instanceof UnexpectedToken) {
      const context = getContext(text, e.posInStream ?? 0, 40);
      const error = translateParserError(e);
      if (error !== undefined) throw new GrammarError(`${error}, at line ${e.line} column ${e.column}\n\n${context}`);
    }
    throw e;
  }
  const tree = fromNode(parsed) as GTree;
  // Lark's PrepareGrammar: terminal and nonterminal nodes become symbols
  return inPlaceTree(tree, (t) => {
    if (t.data === 'terminal') {
      const name = asToken(t.children[0])?.value ?? '';
      return Symbol.terminal(name, name.startsWith('_'));
    }
    if (t.data === 'nonterminal') return Symbol.rule(asToken(t.children[0])?.value ?? '');
    return t;
  });
}

// ---------------------------------------------------------------------------------------------------------------
//  Literals
// ---------------------------------------------------------------------------------------------------------------

/** Evaluates the escapes of a grammar literal, as Lark does with Python's `literal_eval` (Lark's `eval_escaping`). */
export function evalEscaping(s: string): string {
  let w = '';
  const chars = [...s];
  for (let i = 0; i < chars.length; i++) {
    const n = chars[i]!;
    w += n;
    if (n === '\\') {
      i++;
      const n2 = chars[i];
      if (n2 === undefined) throw new GrammarError(`Literal ended unexpectedly (bad escaping): \`${repr(s)}\``);
      if (n2 === '\\') w += '\\\\';
      else if (!'Uuxnftr'.includes(n2)) w += '\\';
      w += n2;
    }
  }
  w = w.replaceAll('\\"', '"').replaceAll("'", "\\'");
  try {
    return literalEval(w);
  } catch (e) {
    throw new GrammarError(`${s}: ${(e as Error).message}`);
  }
}

/** Evaluates the escapes of the body of a Python string literal. */
function literalEval(s: string): string {
  const chars = [...s];
  let out = '';
  let i = 0;
  const hex = (digits: number, e: string): number => {
    const error = () => new Error(`truncated \\${e}${'X'.repeat(digits)} escape`);
    if (i + digits > chars.length) throw error();
    const text = chars.slice(i, i + digits).join('');
    if (!/^[0-9a-fA-F]*$/.test(text)) throw error();
    return parseInt(text, 16);
  };
  const push = (cp: number, message: string) => {
    if (cp > 0x10ffff) throw new Error(message);
    out += String.fromCodePoint(cp);
  };
  while (i < chars.length) {
    const c = chars[i++]!;
    if (c !== '\\') {
      out += c;
      continue;
    }
    const e = chars[i++];
    if (e === undefined) throw new Error('EOL while scanning string literal');
    switch (e) {
      case '\n':
        break;
      case '\\':
      case "'":
      case '"':
        out += e;
        break;
      case 'a':
        out += '\x07';
        break;
      case 'b':
        out += '\x08';
        break;
      case 'f':
        out += '\x0c';
        break;
      case 'n':
        out += '\n';
        break;
      case 'r':
        out += '\r';
        break;
      case 't':
        out += '\t';
        break;
      case 'v':
        out += '\x0b';
        break;
      case 'x':
      case 'u':
      case 'U': {
        const digits = e === 'x' ? 2 : e === 'u' ? 4 : 8;
        const cp = hex(digits, e);
        i += digits;
        push(cp, 'illegal Unicode character');
        break;
      }
      case 'N':
        throw new Error('\\N{...} escapes are not supported');
      default:
        if (e >= '0' && e <= '7') {
          let value = e.charCodeAt(0) - 48;
          let count = 1;
          while (count < 3 && i < chars.length && chars[i]! >= '0' && chars[i]! <= '7') {
            value = value * 8 + (chars[i]!.charCodeAt(0) - 48);
            i++;
            count++;
          }
          push(value, 'illegal character');
        } else {
          // Unknown escapes are kept as they are
          out += '\\' + e;
        }
    }
  }
  return out;
}

const RE_FLAGS = 'imslux';

function literalToPattern(literal: Token): Pattern {
  const v = literal.value;
  const flagStart = Math.max(v.lastIndexOf('/'), v.lastIndexOf('"')) + 1;
  if (flagStart === 0) throw new GrammarError(`Bad literal ${v}`);
  const flags = v.slice(flagStart);
  if (![...flags].every((f) => RE_FLAGS.includes(f))) throw new GrammarError(`Bad flags ${flags}`);
  if (literal.type === 'STRING' && v.includes('\n')) throw new GrammarError('You cannot put newlines in string literals');
  if (literal.type === 'REGEXP' && v.includes('\n') && !flags.includes('x')) {
    throw new GrammarError('You can only use newlines in regular expressions with the `x` (verbose) flag');
  }
  const body = v.slice(0, flagStart);
  const s = evalEscaping(body.slice(1, -1));
  if (s === '') throw new GrammarError(`Empty terminals are not allowed (${literal.value})`);
  if (literal.type === 'STRING') return Pattern.string(s.replaceAll('\\\\', '\\'), flags, literal.value);
  if (literal.type === 'REGEXP') return Pattern.regex(s, flags, literal.value);
  throw new GrammarError('Invariant failed: literal.type not in ["STRING", "REGEXP"]');
}

/** Lark's `PrepareLiterals`: literals and ranges become patterns. */
function prepareLiterals(tree: GTree): GTree {
  return inPlaceTree(tree, (t) => {
    if (t.data === 'literal') {
      const token = asToken(t.children[0]);
      if (token === undefined) throw new GrammarError('Bad literal');
      return st('pattern', [literalToPattern(token)]);
    }
    if (t.data === 'range') {
      const start = asToken(t.children[0]);
      const end = asToken(t.children[1]);
      if (start === undefined || end === undefined) throw new GrammarError('Bad range');
      const s = start.value.slice(1, -1);
      const e = end.value.slice(1, -1);
      if (codePointLength(evalEscaping(s)) !== 1 || codePointLength(evalEscaping(e)) !== 1) {
        throw new GrammarError(`Range bounds must be single characters (${start.value}..${end.value})`);
      }
      return st('pattern', [Pattern.regex(`[${s}-${e}]`)]);
    }
    return t;
  });
}

// ---------------------------------------------------------------------------------------------------------------
//  Terminals
// ---------------------------------------------------------------------------------------------------------------

function parseInt64(node: GNode | undefined): number {
  if (!(node instanceof Token)) throw new GrammarError(`Expected a number, got ${node === undefined ? 'nothing' : describe(node)}`);
  const text = node.value;
  const value = Number(text);
  if (!/^[+-]?\d+$/.test(text) || !Number.isSafeInteger(value)) throw new GrammarError(`Number out of range: ${text}`);
  return value;
}

function expectPattern(node: GNode): Pattern {
  if (node instanceof Pattern) return node;
  throw new GrammarError(`Invalid terminal definition: ${describe(node)}`);
}

/** Lark's `TerminalTreeToPattern`: combines a terminal's tree into one pattern. */
function terminalTreeToPattern(node: GNode): Pattern {
  return expectPattern(terminalNode(node));
}

function terminalNode(node: GNode): GNode {
  if (!(node instanceof GTree)) return node;
  const args = node.children.map(terminalNode);
  switch (node.data) {
    case 'pattern':
      return args[0] ?? EMPTY;
    case 'expansion':
      if (args.length === 0) return Pattern.string('');
      if (args.length === 1) return args[0]!;
      return Pattern.regex(args.map((a) => expectPattern(a).toRegexp()).join(''));
    case 'expansions': {
      if (args.length === 1) return args[0]!;
      const exps = args.map(expectPattern);
      // Make sure that the longest option is returned (Python's re module otherwise prefers just 'l' when given
      // (l|ll) and both could match)
      exps.sort(
        (a, b) =>
          compareNumbers(b.maxWidth, a.maxWidth) ||
          compareNumbers(b.minWidth, a.minWidth) ||
          codePointLength(b.value) - codePointLength(a.value),
      );
      return Pattern.regex(`(?:${exps.map((p) => p.toRegexp()).join('|')})`);
    }
    case 'expr':
      return terminalExpr(args);
    case 'maybe':
      return terminalExpr([...args, new Token('OP', '?')]);
    case 'alias':
      throw new GrammarError('Aliasing not allowed in terminals (You used -> in the wrong place)');
    case 'value':
      return args[0] ?? EMPTY;
    default:
      return new GTree(node.data, args);
  }
}

function compareNumbers(a: number, b: number): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function terminalExpr(args: GNode[]): Pattern {
  const inner = expectPattern(args[0] ?? EMPTY);
  const opNode = args[1];
  if (opNode === undefined) throw new GrammarError('Bad terminal expression');
  const rest = args.slice(2);
  let op = asToken(opNode)?.value ?? '';
  if (op === '~') {
    if (rest.length === 1) {
      op = `{${parseInt64(rest[0])}}`;
    } else {
      const mn = parseInt64(rest[0]);
      const mx = parseInt64(rest[1]);
      if (mx < mn) throw new GrammarError(`Bad Range for ${inner.repr()} (${mn}..${mx} isn't allowed)`);
      op = `{${mn},${mx}}`;
    }
  } else if (rest.length > 0) {
    throw new GrammarError('Unexpected arguments for terminal expression');
  }
  return Pattern.regex(`(?:${inner.toRegexp()})${op}`, inner.flags);
}

// ---------------------------------------------------------------------------------------------------------------
//  Rules
// ---------------------------------------------------------------------------------------------------------------

/** Lark's `_TERMINAL_NAMES`: the names given to anonymous terminals of punctuation. */
const TERMINAL_NAMES: Record<string, string> = {
  '.': 'DOT',
  ',': 'COMMA',
  ':': 'COLON',
  ';': 'SEMICOLON',
  '+': 'PLUS',
  '-': 'MINUS',
  '*': 'STAR',
  '/': 'SLASH',
  '\\': 'BACKSLASH',
  '|': 'VBAR',
  '?': 'QMARK',
  '!': 'BANG',
  '@': 'AT',
  '#': 'HASH',
  $: 'DOLLAR',
  '%': 'PERCENT',
  '^': 'CIRCUMFLEX',
  '&': 'AMPERSAND',
  _: 'UNDERSCORE',
  '<': 'LESSTHAN',
  '>': 'MORETHAN',
  '=': 'EQUAL',
  '"': 'DBLQUOTE',
  "'": 'QUOTE',
  '`': 'BACKQUOTE',
  '~': 'TILDE',
  '(': 'LPAR',
  ')': 'RPAR',
  '{': 'LBRACE',
  '}': 'RBRACE',
  '[': 'LSQB',
  ']': 'RSQB',
  '\n': 'NEWLINE',
  '\r\n': 'CRLF',
  '\t': 'TAB',
  ' ': 'SPACE',
};

/** Lark's `PrepareAnonTerminals`: gives the anonymous terminals (literals in rules) a name. */
class PrepareAnonTerminals {
  private readonly termSet: Set<string>;
  private readonly termReverse = new Map<string, [string, string]>();
  private i = 0;
  keepAllTokens = false;

  constructor(readonly terminals: TerminalDef[]) {
    this.termSet = new Set(terminals.map((t) => t.name));
    for (const t of terminals) this.termReverse.set(t.pattern.key, [t.name, t.pattern.flags]);
  }

  call(t: GTree): GNode {
    if (t.data !== 'pattern') return t;
    const p = t.children[0];
    if (!(p instanceof Pattern)) throw new GrammarError('Bad pattern');
    const existing = this.termReverse.get(p.key);
    if (existing !== undefined && p.flags !== existing[1]) {
      throw new GrammarError(`Conflicting flags for the same terminal: ${p.repr()}`);
    }
    let termName: string | undefined;
    if (p.isStr) {
      if (existing !== undefined) {
        // If already defined, use the user-defined terminal name
        termName = existing[0];
      } else {
        // Try to assign an indicative anon-terminal name
        const value = p.value;
        if (Object.prototype.hasOwnProperty.call(TERMINAL_NAMES, value)) {
          termName = TERMINAL_NAMES[value];
        } else if (value !== '' && isIdContinue(value) && isIdStart(String.fromCodePoint(value.codePointAt(0)!))) {
          const upper = value.toUpperCase();
          if (!this.termSet.has(upper)) termName = upper;
        }
        if (termName !== undefined && this.termSet.has(termName)) termName = undefined;
      }
    } else if (existing !== undefined) {
      termName = existing[0];
    }

    if (termName === undefined) termName = `__ANON_${this.i++}`;

    if (!this.termSet.has(termName)) {
      this.termSet.add(termName);
      this.termReverse.set(p.key, [termName, p.flags]);
      this.terminals.push(new TerminalDef(termName, p));
    }

    return Symbol.terminal(termName, this.keepAllTokens ? false : p.isStr);
  }
}

/** Lark's `ValidateSymbols`: `value` nodes are replaced by what they contain. */
function validateSymbols(tree: GTree): GTree {
  return inPlaceTree(tree, (t) => {
    if (t.data !== 'value') return t;
    const v = t.children[0] ?? EMPTY;
    if (v instanceof GTree || v instanceof Symbol) return v;
    throw new GrammarError(`Unexpected value ${describe(v)}`);
  });
}

interface RuleDef {
  name: string;
  params: string[];
  tree?: GTree;
  options: RuleOptions;
}

interface TermDef {
  name: string;
  tree?: GTree;
  priority: number;
}

/** Lark's `ApplyTemplates`: creates a rule for each use of a template. */
class ApplyTemplates {
  private readonly created = new Set<string>();

  call(t: GTree, ruleDefs: RuleDef[]): GNode {
    if (t.data !== 'template_usage') return t;
    const [first, ...rest] = t.children;
    if (!(first instanceof Symbol)) throw new GrammarError('Bad template usage');
    const name = first.name;
    const args: Symbol[] = rest.map((a) => {
      if (a instanceof Symbol) return a;
      throw new GrammarError(`Bad template argument ${describe(a)}`);
    });
    const resultName = `${name}{${args.map((a) => a.name).join(',')}}`;
    if (!this.created.has(resultName)) {
      this.created.add(resultName);
      const def = ruleDefs.find((r) => r.name === name);
      if (def === undefined) throw new GrammarError(`Template '${name}' used but not defined`);
      if (def.params.length !== args.length) throw new GrammarError(`Wrong number of template arguments used for ${name}`);
      const names = new Map(def.params.map((p, i) => [p, args[i]!] as const));
      if (def.tree === undefined) throw new GrammarError(`Template '${name}' is abstract`);
      const resultTree = inPlaceTree(cloneG(def.tree), (sub) => replaceSymbols(sub, names));
      ruleDefs.push({ name: resultName, params: [], tree: resultTree, options: { ...def.options } });
    }
    return Symbol.rule(resultName);
  }
}

function replaceSymbols(t: GTree, names: Map<string, Symbol>): GNode {
  if (t.data === 'value') {
    const c = t.children[0];
    if (t.children.length === 1 && c instanceof Symbol) {
      const r = names.get(c.name);
      if (r !== undefined) return r;
    }
    return t;
  }
  if (t.data === 'template_usage') {
    const c = t.children[0];
    const r = c instanceof Symbol ? names.get(c.name) : undefined;
    if (r !== undefined) return new GTree(t.data, [r, ...t.children.slice(1)]);
  }
  return t;
}

// Value 5 keeps the number of states in the lalr parser somewhat minimal. It isn't optimal, but close to it.
const SMALL_FACTOR_THRESHOLD = 5;
// The threshold whether repeats via ~ are split up into different rules.
const REPEAT_BREAK_THRESHOLD = 50;

/** Lark's `EBNF_to_BNF`: replaces the operators `? * + ~ []` with new rules. */
class EbnfToBnf {
  newRules: Array<[string, GTree, RuleOptions]> = [];
  private readonly rulesCache = new Map<string, Symbol>();
  prefix = '';
  private i = 0;
  ruleOptions?: RuleOptions;

  private nameRule(inner: string): string {
    return `__${this.prefix}_${inner}_${this.i++}`;
  }

  private addRule(key: string, name: string, expansions: GTree): Symbol {
    const t = Symbol.rule(name);
    this.newRules.push([name, expansions, this.ruleOptions !== undefined ? { ...this.ruleOptions } : ruleOptions()]);
    this.rulesCache.set(key, t);
    return t;
  }

  private addRecurseRule(kind: string, expr: GNode): Symbol {
    const key = `R${gkey(expr)}`;
    const cached = this.rulesCache.get(key);
    if (cached !== undefined) return cached;
    const name = this.nameRule(kind);
    const t = Symbol.rule(name);
    const tree = st('expansions', [st('expansion', [cloneG(expr)]), st('expansion', [t, expr])]);
    return this.addRule(key, name, tree);
  }

  private addRepeatRule(a: number, b: number, target: GNode, atom: GNode): Symbol {
    const key = `A${a},${b}${gkey(target)}${gkey(atom)}`;
    const cached = this.rulesCache.get(key);
    if (cached !== undefined) return cached;
    const name = this.nameRule(`repeat_a${a}_b${b}`);
    const children: GNode[] = [];
    for (let i = 0; i < a; i++) children.push(cloneG(target));
    for (let i = 0; i < b; i++) children.push(cloneG(atom));
    return this.addRule(key, name, st('expansions', [st('expansion', children)]));
  }

  private addRepeatOptRule(a: number, b: number, target: GNode, targetOpt: GNode, atom: GNode): Symbol {
    const key = `O${a},${b}${gkey(target)}${gkey(atom)}`;
    const cached = this.rulesCache.get(key);
    if (cached !== undefined) return cached;
    const name = this.nameRule(`repeat_a${a}_b${b}_opt`);
    const exps: GNode[] = [];
    for (let i = 0; i < a; i++) {
      const children: GNode[] = [];
      for (let j = 0; j < i; j++) children.push(cloneG(target));
      children.push(cloneG(targetOpt));
      exps.push(st('expansion', children));
    }
    for (let i = 0; i < b; i++) {
      const children: GNode[] = [];
      for (let j = 0; j < a; j++) children.push(cloneG(target));
      for (let j = 0; j < i; j++) children.push(cloneG(atom));
      exps.push(st('expansion', children));
    }
    return this.addRule(key, name, st('expansions', exps));
  }

  /** A rule tree that repeats `rule` between `mn` and `mx` times. */
  private generateRepeats(rule: GNode, mn: number, mx: number): GNode {
    // For a small number of repeats, we can take the naive approach
    if (mx < REPEAT_BREAK_THRESHOLD) {
      const exps: GNode[] = [];
      for (let n = mn; n <= mx; n++) {
        const children: GNode[] = [];
        for (let i = 0; i < n; i++) children.push(cloneG(rule));
        exps.push(st('expansion', children));
      }
      return st('expansions', exps);
    }

    // For large repeat values, we break the repetition into sub-rules.
    let mnTarget: GNode = rule;
    for (const [a, b] of smallFactors(mn, SMALL_FACTOR_THRESHOLD)) mnTarget = this.addRepeatRule(a, b, mnTarget, rule);
    if (mx === mn) return mnTarget;

    const diff = mx - mn + 1; // We add one because addRepeatOptRule generates rules that match one less
    const diffFactors = smallFactors(diff, SMALL_FACTOR_THRESHOLD);
    let diffTarget: GNode = rule; // Match rule 1 times
    let diffOptTarget: GNode = st('expansion', []); // match rule 0 times (e.g. up to 1 -1 times)
    for (const [a, b] of diffFactors.slice(0, -1)) {
      diffOptTarget = this.addRepeatOptRule(a, b, diffTarget, diffOptTarget, rule);
      diffTarget = this.addRepeatRule(a, b, diffTarget, rule);
    }
    const [a, b] = diffFactors[diffFactors.length - 1]!;
    diffOptTarget = this.addRepeatOptRule(a, b, diffTarget, diffOptTarget, rule);
    return st('expansions', [st('expansion', [mnTarget, diffOptTarget])]);
  }

  private expr(children: GNode[]): GNode {
    const rule = children[0] ?? EMPTY;
    const op = asToken(children[1])?.value ?? '';
    const args = children.slice(2);
    switch (op) {
      case '?':
        return st('expansions', [rule, st('expansion', [])]);
      // a : b c+ d  -->  a : b _c d ; _c : _c c | c;
      case '+':
        return this.addRecurseRule('plus', rule);
      // a : b c* d  -->  a : b _c? d ; _c : _c c | c;
      case '*':
        return st('expansions', [this.addRecurseRule('star', rule), st('expansion', [])]);
      case '~': {
        let mn: number;
        let mx: number;
        if (args.length === 1) {
          mn = mx = parseInt64(args[0]);
        } else {
          mn = parseInt64(args[0]);
          mx = parseInt64(args[1]);
          if (mx < mn || mn < 0) throw new GrammarError(`Bad Range for ${describe(rule)} (${mn}..${mx} isn't allowed)`);
        }
        if (mn < 0) throw new GrammarError(`Bad Range for ${describe(rule)} (${mn}..${mx} isn't allowed)`);
        return this.generateRepeats(rule, mn, mx);
      }
      default:
        throw new GrammarError(`Unknown operator ${op}`);
    }
  }

  private maybe(rule: GNode): GNode {
    const size = findRuleSize(rule, this.ruleOptions?.keepAllTokens ?? false);
    const empties: GNode[] = new Array<GNode>(size).fill(EMPTY);
    return st('expansions', [rule, st('expansion', empties)]);
  }

  transform(tree: GTree): GTree {
    return inPlaceTree(tree, (t) => {
      if (t.data === 'expr') return this.expr(t.children);
      if (t.data === 'maybe') return this.maybe(t.children[0] ?? EMPTY);
      return t;
    });
  }
}

/** Lark's `FindRuleSize`: how many children an expansion adds to the tree. */
function findRuleSize(node: GNode, keepAllTokens: boolean): number {
  if (node instanceof GTree && node.data === 'expansion') {
    return node.children.reduce((sum, c) => sum + findRuleSize(c, keepAllTokens), 0);
  }
  if (node instanceof GTree && node.data === 'expansions') {
    return node.children.reduce((max, c) => Math.max(max, findRuleSize(c, keepAllTokens)), 0);
  }
  if (node === EMPTY) return 0;
  if (node instanceof Symbol) {
    if (!node.isTerm) return node.name.startsWith('_') ? 0 : 1;
    return keepAllTokens || !node.filterOut ? 1 : 0;
  }
  throw new GrammarError(`Unexpected node in rule: ${describe(node)}`);
}

/** Lark's `SimplifyRule_Visitor`: flattens the rule into a list of alternatives. */
function simplifyRule(tree: GTree): void {
  for (const sub of subtrees(tree)) simplifyCall(sub);
}

function simplifyCall(tree: GTree): void {
  switch (tree.data) {
    case 'expansion': {
      // rules_list unpacking
      // a : b (c|d) e
      //  -->
      // a : b c e | b d e
      flatten(tree);
      const i = tree.children.findIndex((c) => c instanceof GTree && c.data === 'expansions');
      if (i !== -1) {
        const options = dedup((tree.children[i] as GTree).children);
        const original = tree.children;
        tree.data = 'expansions';
        tree.children = options.map((option) => {
          const e = new GTree(
            'expansion',
            original.map((other, j) => cloneG(i === j ? option : other)),
          );
          simplifyRule(e);
          return e;
        });
        flatten(tree);
      }
      break;
    }
    case 'alias': {
      const rule = tree.children[0];
      if (tree.children.length === 2 && rule instanceof GTree && rule.data === 'expansions') {
        const aliasName = tree.children[1]!;
        tree.data = 'expansions';
        tree.children = rule.children.map((child) => st('alias', [child, cloneG(aliasName)]));
      }
      break;
    }
    case 'expansions': {
      flatten(tree);
      // Ensure all children are unique
      tree.children = dedup(tree.children);
      break;
    }
  }
}

/** Lark's `RuleTreeToText`: the alternatives of a simplified rule, with their alias. */
function ruleTreeToText(tree: GTree): Array<[GNode[], string | undefined]> {
  const expansion = (node: GNode): [GNode[], string | undefined] => {
    if (node instanceof GTree && node.data === 'expansion') return [node.children, undefined];
    if (node instanceof GTree && node.data === 'alias') {
      const [exp, inner] = expansion(node.children[0]!);
      if (inner !== undefined) throw new GrammarError('Double alias not allowed');
      const alias = node.children[1];
      if (alias instanceof Symbol) return [exp, alias.name];
      throw new GrammarError(`Unexpected alias ${alias === undefined ? '' : describe(alias)}`);
    }
    throw new GrammarError(`Unexpected rule structure: ${describe(node)}`);
  };
  if (tree.data !== 'expansions') throw new GrammarError(`Unexpected rule structure: ${tree.data}`);
  return tree.children.map(expansion);
}

// ---------------------------------------------------------------------------------------------------------------
//  Grammar
// ---------------------------------------------------------------------------------------------------------------

/** @internal The result of compiling a grammar: its terminals, BNF rules and ignored terminals. */
export interface CompiledGrammar {
  terminals: TerminalDef[];
  rules: Rule[];
  ignore: string[];
}

/** A loaded grammar, with its imports resolved, that isn't compiled into BNF yet (Lark's `Grammar`). */
export class Grammar {
  /** @internal */
  constructor(
    private readonly ruleDefs: RuleDef[],
    private readonly termDefs: TermDef[],
    /** The names of the ignored terminals. */
    readonly ignore: string[],
  ) {}

  /** The names of the rules. */
  get ruleNames(): string[] {
    return this.ruleDefs.map((r) => r.name);
  }

  /** The names of the terminals. */
  get terminalNames(): string[] {
    return this.termDefs.map((t) => t.name);
  }

  /**
   * @internal Compiles the EBNF grammar into BNF rules and terminals. `terminalsToKeep` are kept even when unused;
   * undefined keeps all terminals.
   */
  compile(start: readonly string[], terminalsToKeep?: ReadonlySet<string>): CompiledGrammar {
    const termDefs = this.termDefs.map((t) => ({ ...t, tree: t.tree && cloneG(t.tree) }));
    const ruleDefs: RuleDef[] = this.ruleDefs.map((r) => ({ ...r, tree: r.tree && cloneG(r.tree), options: { ...r.options } }));

    // ===================
    //  Compile Terminals
    // ===================
    for (const td of termDefs) {
      if (td.tree === undefined) continue; // Terminal added through %declare
      const expansions = findData(td.tree, 'expansion');
      if (expansions.length === 1 && expansions[0]!.children.length === 0) {
        throw new GrammarError(`Terminals cannot be empty (${td.name})`);
      }
    }

    const terminals: TerminalDef[] = [];
    for (const td of termDefs) {
      if (td.tree !== undefined) terminals.push(new TerminalDef(td.name, terminalTreeToPattern(prepareLiterals(td.tree)), td.priority));
    }

    // =================
    //  Compile Rules
    // =================

    // 1. Pre-process terminals
    const anonTokens = new PrepareAnonTerminals(terminals);
    // 2. Inline Templates
    const applyTemplates = new ApplyTemplates();

    // 3. Convert EBNF to BNF (and apply step 1 & 2)
    const ebnfToBnf = new EbnfToBnf();
    const rules: Array<[string, GTree, RuleOptions]> = [];
    // ruleDefs might grow due to templates
    for (let i = 0; i < ruleDefs.length; i++) {
      const def = ruleDefs[i]!;
      if (def.params.length > 0) continue; // Dont transform templates
      const options = def.options.keepAllTokens ? ruleOptions({ keepAllTokens: true }) : undefined;
      ebnfToBnf.ruleOptions = options;
      ebnfToBnf.prefix = def.name;
      anonTokens.keepAllTokens = options !== undefined;
      if (def.tree === undefined) throw new GrammarError(`Rule '${def.name}' is abstract`);
      let tree = prepareLiterals(cloneG(def.tree));
      tree = validateSymbols(tree);
      tree = inPlaceTree(tree, (t) => anonTokens.call(t));
      tree = inPlaceTree(tree, (t) => applyTemplates.call(t, ruleDefs));
      const res = ebnfToBnf.transform(tree);
      rules.push([def.name, res, { ...def.options }]);
    }
    rules.push(...ebnfToBnf.newRules);

    if (new Set(rules.map((r) => r[0])).size !== rules.length) throw new GrammarError('Whoops, name collision');

    // 4. Compile tree to Rule objects
    let compiledRules: Rule[] = [];
    for (const [name, tree, options] of rules) {
      simplifyRule(tree);
      ruleTreeToText(tree).forEach(([expansion, alias], i) => {
        if (alias !== undefined && name.startsWith('_')) {
          throw new GrammarError(
            `Rule ${name} is marked for expansion (it starts with an underscore) and isn't allowed to have aliases (alias=${alias})`,
          );
        }
        const emptyIndices = expansion.map((x) => x === EMPTY);
        const expOptions: RuleOptions = { ...options };
        if (emptyIndices.some((b) => b)) expOptions.emptyIndices = emptyIndices;
        const symbols: Symbol[] = [];
        for (const x of expansion) {
          if (x === EMPTY) continue;
          if (!(x instanceof Symbol)) throw new GrammarError(`Unexpected rule structure: ${describe(x)}`);
          symbols.push(x.isTerm && expOptions.keepAllTokens ? Symbol.terminal(x.name, false) : x);
        }
        compiledRules.push(new Rule(Symbol.rule(name), symbols, i, alias, expOptions));
      });
    }

    // Remove duplicates of empty rules, throw error for non-empty duplicates
    if (new Set(compiledRules.map((r) => r.key)).size !== compiledRules.length) {
      const duplicates = classify(compiledRules, (r) => r.key);
      for (const dups of duplicates.values()) {
        if (dups.length > 1 && dups[0]!.expansion.length > 0) {
          const list = dups.map((d) => `\n  * ${d}`).join('');
          throw new GrammarError(`Rules defined twice: ${list}\n\n(Might happen due to colliding expansion of optionals: [] or ?)`);
        }
      }
      compiledRules = [...duplicates.values()].map((d) => d[0]!);
    }

    // Filter out unused rules
    for (;;) {
      const c = compiledRules.length;
      const usedRules = new Set<string>();
      for (const r of compiledRules) {
        for (const s of r.expansion) if (!s.isTerm && !s.equals(r.origin)) usedRules.add(s.key);
      }
      for (const s of start) usedRules.add(Symbol.rule(s).key);
      compiledRules = compiledRules.filter((r) => usedRules.has(r.origin.key));
      if (compiledRules.length === c) break;
    }

    // Filter out unused terminals
    let resultTerminals = anonTokens.terminals;
    if (terminalsToKeep !== undefined) {
      const usedTerms = new Set<string>();
      for (const r of compiledRules) for (const s of r.expansion) if (s.isTerm) usedTerms.add(s.name);
      resultTerminals = resultTerminals.filter((t) => usedTerms.has(t.name) || this.ignore.includes(t.name) || terminalsToKeep.has(t.name));
    }

    return { terminals: resultTerminals, rules: compiledRules, ignore: [...this.ignore] };
  }
}

// ---------------------------------------------------------------------------------------------------------------
//  Grammar loaders
// ---------------------------------------------------------------------------------------------------------------

/** Loads the grammars of `%import` statements (Lark's import paths and loaders such as `FromPackageLoader`). */
export interface GrammarLoader {
  /**
   * Loads `grammarPath` (such as `common.lark` or `sub/grammar.lark`), and returns its full path and text.
   * `basePath` is undefined for library imports (`%import common.X`), and the directory of the importing grammar for
   * relative imports (`%import .x.Y`).
   */
  load(basePath: string | undefined, grammarPath: string): [string, string] | undefined;
}

interface NodeFs {
  readFileSync(path: string, encoding: 'utf8'): string;
}

function nodeFs(): NodeFs | undefined {
  const proc = (globalThis as { process?: { getBuiltinModule?: (id: string) => unknown } }).process;
  return proc?.getBuiltinModule?.('node:fs') as NodeFs | undefined;
}

function joinPath(dir: string, path: string): string {
  if (dir === '') return path;
  return /[/\\]$/.test(dir) ? dir + path : `${dir}/${path}`;
}

/** Loads grammars from a directory (with Node's file system). */
export class FileSystemLoader implements GrammarLoader {
  constructor(readonly directory: string) {}

  load(_basePath: string | undefined, grammarPath: string): [string, string] | undefined {
    const fs = nodeFs();
    if (fs === undefined) return undefined;
    const path = joinPath(this.directory, grammarPath);
    try {
      return [path, fs.readFileSync(path, 'utf8')];
    } catch {
      return undefined;
    }
  }
}

/** Loads grammars from a map of paths to grammar texts, such as `{ 'sub/grammar.lark': '...' }`. */
export class MemoryLoader implements GrammarLoader {
  constructor(readonly grammars: Readonly<Record<string, string>>) {}

  load(_basePath: string | undefined, grammarPath: string): [string, string] | undefined {
    const text = this.grammars[grammarPath];
    return text === undefined ? undefined : [grammarPath, text];
  }
}

/** The prefix of the paths of the grammars that come with the package. */
const STDLIB_PREFIX = 'lark://';

function normalizePath(path: string): string {
  const parts: string[] = [];
  for (const p of path.replaceAll('\\', '/').split('/')) {
    if (p === '' || p === '.') continue;
    if (p === '..' && parts.length > 0) parts.pop();
    else parts.push(p);
  }
  return parts.join('/');
}

/** Loads the grammars that come with Lark: `common`, `unicode`, `python` and `lark`. */
export class StandardLibraryLoader implements GrammarLoader {
  load(basePath: string | undefined, grammarPath: string): [string, string] | undefined {
    let dir: string;
    if (basePath === undefined) dir = 'grammars';
    else if (basePath.startsWith(STDLIB_PREFIX)) dir = basePath.slice(STDLIB_PREFIX.length);
    else return undefined;
    const path = normalizePath(`${dir}/${grammarPath}`);
    const text = STANDARD_GRAMMARS[path];
    return text === undefined ? undefined : [STDLIB_PREFIX + path, text];
  }

  /** The text of a grammar that comes with Lark, such as `python` or `lark`. */
  static grammar(name: string): string | undefined {
    return STANDARD_GRAMMARS[`grammars/${name}.lark`];
  }
}

function directoryOf(path: string): string {
  if (path.startsWith(STDLIB_PREFIX)) {
    const rest = path.slice(STDLIB_PREFIX.length);
    const i = rest.lastIndexOf('/');
    return i === -1 ? STDLIB_PREFIX : STDLIB_PREFIX + rest.slice(0, i);
  }
  const i = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
  return i === -1 ? '' : path.slice(0, i);
}

function currentDirectory(): string {
  const proc = (globalThis as { process?: { cwd?: () => string } }).process;
  try {
    return proc?.cwd?.() ?? '.';
  } catch {
    return '.';
  }
}

/** A grammar import couldn't be found (Python's `IOError`/`FileNotFoundError`). */
export class GrammarImportError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'GrammarImportError';
  }
}

// ---------------------------------------------------------------------------------------------------------------
//  GrammarBuilder
// ---------------------------------------------------------------------------------------------------------------

interface Definition {
  isTerm: boolean;
  tree?: GTree;
  params: string[];
  /** The priority of terminals. */
  priority: number;
  /** The options of rules. */
  options: RuleOptions;
}

type Mangle = (name: string) => string;

/** Python has arbitrary precision integers; huge priorities are clamped, which leaves room to add them up. */
const MAX_PRIORITY = 1_000_000_000;

function parsePriority(node: GNode | undefined): number {
  if (!(node instanceof Token) || node.type !== 'NUMBER') {
    throw new GrammarError(`Priority must be a number, got ${node === undefined ? 'nothing' : describe(node)}`);
  }
  const text = node.value;
  const negative = text.startsWith('-');
  const digits = text.replace(/^[+-]+/, '');
  const parsed = Number(digits);
  const value = /^\d+$/.test(digits) && Number.isSafeInteger(parsed) ? Math.min(parsed, MAX_PRIORITY) : MAX_PRIORITY;
  return negative ? -value : value;
}

function grammarError(isTerm: boolean, msg: string, names: string[]): GrammarError {
  const [lower, title] = isTerm ? ['terminal', 'Terminal'] : ['rule', 'Rule'];
  names.forEach((name, i) => {
    const postfix = i === 0 ? '' : String(i + 1);
    msg = msg.replaceAll(`{name${postfix}}`, name).replaceAll(`{type${postfix}}`, lower).replaceAll(`{Type${postfix}}`, title);
  });
  return new GrammarError(msg);
}

interface Import {
  dottedPath: string[];
  basePath?: string;
  aliases: Map<string, string>;
}

interface Unpacked {
  name: string;
  isTerm: boolean;
  tree: GTree;
  params: string[];
  priority: number;
  options: RuleOptions;
}

/** @internal Builds a {@link Grammar} from the text of grammars, resolving their imports (Lark's `GrammarBuilder`). */
export class GrammarBuilder {
  private definitions = new Map<string, Definition>();
  private readonly ignoreNames: string[] = [];

  constructor(
    private readonly globalKeepAllTokens: boolean,
    private readonly importPaths: readonly GrammarLoader[],
  ) {}

  /** Loads and builds a grammar. */
  static load(text: string, source: string, importPaths: readonly GrammarLoader[], keepAllTokens: boolean): Grammar {
    const builder = new GrammarBuilder(keepAllTokens, importPaths);
    builder.loadGrammar(text, source);
    return builder.build();
  }

  private define(
    name: string,
    isTerm: boolean,
    tree: GTree | undefined,
    params: string[],
    priority: number,
    options: RuleOptions,
    override: boolean,
  ): void {
    if (this.definitions.has(name)) {
      if (!override) throw grammarError(isTerm, "{Type} '{name}' defined more than once", [name]);
    } else if (override) {
      throw grammarError(isTerm, 'Cannot override a nonexisting {type} {name}', [name]);
    }
    if (name.startsWith('__')) {
      throw grammarError(isTerm, 'Names starting with double-underscore are reserved (Error at {name})', [name]);
    }
    if (!isTerm && this.globalKeepAllTokens) options = { ...options, keepAllTokens: true };
    this.definitions.set(name, { isTerm, tree, params, priority, options });
  }

  private extend(name: string, isTerm: boolean, exp: GTree, params: string[]): void {
    const d = this.definitions.get(name);
    if (d === undefined) throw grammarError(isTerm, "Can't extend {type} {name} as it wasn't defined before", [name]);
    if (isTerm !== d.isTerm) {
      throw grammarError(isTerm, 'Cannot extend {type} {name} - one is a terminal, while the other is not.', [name]);
    }
    if (params.length !== d.params.length || params.some((p, i) => p !== d.params[i])) {
      throw grammarError(isTerm, 'Cannot extend {type} with different parameters: {name}', [name]);
    }
    if (d.tree === undefined) throw grammarError(isTerm, "Can't extend {type} {name} - it is abstract.", [name]);
    if (d.tree.data !== 'expansions') throw new GrammarError('Expected an expansions tree');
    d.tree.children.unshift(exp);
  }

  private ignore(t: GTree): void {
    if (t.data === 'expansions' && t.children.length === 1) {
      const t2 = t.children[0];
      if (t2 instanceof GTree && t2.data === 'expansion' && t2.children.length === 1) {
        const item = t2.children[0];
        if (item instanceof GTree && item.data === 'value' && item.children.length === 1) {
          const s = item.children[0];
          if (s instanceof Symbol && s.isTerm) {
            // Keep terminal name, no need to create a new definition
            this.ignoreNames.push(s.name);
            return;
          }
        }
      }
    }
    const name = `__IGNORE_${this.ignoreNames.length}`;
    this.ignoreNames.push(name);
    this.definitions.set(name, { isTerm: true, tree: t, params: [], priority: TOKEN_DEFAULT_PRIORITY, options: ruleOptions() });
  }

  private static unpackImport(stmt: GTree, grammarName: string): Import {
    const pathNode = asTree(stmt.children[0]);
    if (pathNode === undefined) throw new GrammarError('Bad import');
    const arg1 = stmt.children[1];
    const names = (nodes: GNode[]): string[] => nodes.flatMap((n) => (n instanceof Token ? [n.value] : []));
    let dottedPath: string[];
    const aliases = new Map<string, string>();
    if (arg1 instanceof GTree) {
      // Multi import. Can't have aliased multi import, so all aliases will be the same as names
      for (const n of names(arg1.children)) aliases.set(n, n);
      dottedPath = names(pathNode.children);
    } else {
      // Single import
      const all = names(pathNode.children);
      dottedPath = all.slice(0, -1);
      if (dottedPath.length === 0) throw new GrammarError(`Nothing was imported from grammar \`${all[0] ?? ''}\``);
      const name = all[all.length - 1]!;
      aliases.set(name, asToken(arg1)?.value ?? name);
    }
    let basePath: string | undefined;
    if (pathNode.data === 'import_lib') basePath = undefined;
    // Import relative to the current directory if the grammar is given as a string
    else if (grammarName === '<string>') basePath = currentDirectory();
    // Import relative to the grammar file path if external grammar file
    else basePath = directoryOf(grammarName);
    return { dottedPath, basePath, aliases };
  }

  private static unpackDefinition(tree: GTree, mangle?: Mangle): Unpacked {
    const token = (n: GNode | undefined): string => {
      if (n instanceof Token) return n.value;
      throw new GrammarError('Bad definition');
    };
    const subtree = (n: GNode | undefined): GTree => {
      if (n instanceof GTree) return cloneG(n);
      throw new GrammarError('Bad definition');
    };
    let name: string;
    let params: string[];
    let exp: GTree;
    let isTerm: boolean;
    let priority: number;
    let options: RuleOptions;
    if (tree.data === 'rule') {
      const modifiers = subtree(tree.children[0]);
      name = token(tree.children[1]);
      const paramsTree = subtree(tree.children[2]);
      const priorityTree = subtree(tree.children[3]);
      exp = subtree(tree.children[4]);
      let keepAllTokens = false;
      let expand1 = false;
      if (modifiers.children.length > 0) {
        const m = token(modifiers.children[0]);
        expand1 = m.includes('?');
        if (expand1 && name.startsWith('_')) throw new GrammarError('Inlined rules (_rule) cannot use the ?rule modifier.');
        keepAllTokens = m.includes('!');
      }
      const rulePriority = priorityTree.children.length > 0 ? parsePriority(priorityTree.children[0]) : undefined;
      params = paramsTree.children.map(token);
      options = ruleOptions({
        keepAllTokens,
        expand1,
        priority: rulePriority,
        templateSource: params.length > 0 ? name : undefined,
      });
      priority = TOKEN_DEFAULT_PRIORITY;
      isTerm = false;
    } else {
      name = token(tree.children[0]);
      params = [];
      priority = tree.children.length === 3 ? parsePriority(tree.children[1]) : TOKEN_DEFAULT_PRIORITY;
      exp = subtree(tree.children[tree.children.length - 1]);
      options = ruleOptions();
      isTerm = true;
    }
    if (mangle !== undefined) {
      params = params.map(mangle);
      name = mangle(name);
      renameSymbols(exp, mangle);
    }
    return { name, isTerm, tree: exp, params, priority, options };
  }

  private defineUnpacked(d: Unpacked, override: boolean): void {
    this.define(d.name, d.isTerm, d.tree, d.params, d.priority, d.options, override);
  }

  /** Parses the grammar text and adds its definitions, and the definitions it imports. */
  loadGrammar(text: string, grammarName: string, mangle?: Mangle): void {
    const tree = parseGrammar(text, grammarName);

    const imports = new Map<string, Import>();
    for (const stmt of tree.children) {
      if (!(stmt instanceof GTree) || stmt.data !== 'import') continue;
      const imp = GrammarBuilder.unpackImport(stmt, grammarName);
      const key = imp.dottedPath.join('\u0000');
      const existing = imports.get(key);
      if (existing !== undefined) {
        if (existing.basePath !== imp.basePath) throw new GrammarError(`Inconsistent base_path for ${imp.dottedPath.join('.')}.`);
        for (const [k, v] of imp.aliases) existing.aliases.set(k, v);
      } else {
        imports.set(key, imp);
      }
    }

    for (const imp of imports.values()) this.doImport(imp.dottedPath, imp.basePath, imp.aliases, mangle);

    for (const stmt of tree.children) {
      if (!(stmt instanceof GTree)) throw new GrammarError(`Unexpected statement ${describe(stmt)}`);
      switch (stmt.data) {
        case 'term':
        case 'rule':
          this.defineUnpacked(GrammarBuilder.unpackDefinition(stmt, mangle), false);
          break;
        case 'override': {
          const r = asTree(stmt.children[0]);
          if (r === undefined) throw new GrammarError('Bad override');
          this.defineUnpacked(GrammarBuilder.unpackDefinition(r, mangle), true);
          break;
        }
        case 'extend': {
          const r = asTree(stmt.children[0]);
          if (r === undefined) throw new GrammarError('Bad extend');
          const d = GrammarBuilder.unpackDefinition(r, mangle);
          this.extend(d.name, d.isTerm, d.tree, d.params);
          break;
        }
        case 'ignore': {
          // if mangle is not None, we shouldn't apply ignore, since we aren't in a toplevel grammar
          const t = stmt.children[0];
          if (mangle === undefined && t instanceof GTree) this.ignore(cloneG(t));
          break;
        }
        case 'declare':
          for (const child of stmt.children) {
            if (!(child instanceof Symbol)) throw new GrammarError(`Unexpected ${describe(child)}`);
            const name = mangle === undefined ? child.name : mangle(child.name);
            this.define(name, child.isTerm, undefined, [], TOKEN_DEFAULT_PRIORITY, ruleOptions(), false);
          }
          break;
        case 'import':
          break;
        default:
          throw new GrammarError(`Unexpected statement ${stmt.data}`);
      }
    }

    this.resolveTermReferences();
  }

  private resolveTermReferences(): void {
    // The checks, in the order Lark makes them while resolving
    const refs = new Map<string, string[]>();
    for (const [name, d] of this.definitions) {
      if (!d.isTerm || d.tree === undefined) continue; // Terminals added through %declare have no tree
      const names: string[] = [];
      for (const value of findData(d.tree, 'value')) {
        const item = value.children[0];
        if (!(item instanceof Symbol)) continue;
        if (!item.isTerm) throw new GrammarError(`Rules aren't allowed inside terminals (${item.repr()} in ${name})`);
        const t = this.definitions.get(item.name);
        if (t === undefined || !t.isTerm || t.tree === undefined) throw new GrammarError(`Terminal used but not defined: ${item.name}`);
        names.push(item.name);
      }
      refs.set(name, names);
    }

    for (const [name, names] of refs) {
      // Is the terminal part of a cycle of references?
      if (bfs(names, (n) => refs.get(n) ?? []).includes(name)) {
        throw new GrammarError(`Recursion in terminal '${name}' (recursion is only allowed in rules, not terminals)`);
      }
    }

    const resolved = new Map<string, GTree>();
    const resolve = (name: string): GTree => {
      const cached = resolved.get(name);
      if (cached !== undefined) return cloneG(cached);
      const tree = cloneG(this.definitions.get(name)!.tree!);
      for (const value of findData(tree, 'value')) {
        const item = value.children[0];
        if (!(item instanceof Symbol) || !item.isTerm) continue;
        value.children[0] = resolve(item.name);
      }
      resolved.set(name, cloneG(tree));
      return tree;
    };
    for (const [name, names] of refs) {
      if (names.length > 0) this.definitions.get(name)!.tree = resolve(name);
    }
  }

  private static findUsedSymbols(tree: GTree): Set<string> {
    const result = new Set<string>();
    for (const e of findData(tree, 'expansion')) {
      const symbols: Symbol[] = [];
      scanSymbols(e, symbols);
      for (const s of symbols) result.add(s.name);
    }
    return result;
  }

  private removeUnused(used: string[]): void {
    const keep = new Set(
      bfs(used, (symbol) => {
        const d = this.definitions.get(symbol);
        if (d === undefined || d.isTerm || d.tree === undefined) return [];
        return [...GrammarBuilder.findUsedSymbols(d.tree)].filter((s) => !d.params.includes(s)).sort();
      }),
    );
    this.definitions = new Map([...this.definitions].filter(([k]) => keep.has(k)));
  }

  private doImport(dottedPath: string[], basePath: string | undefined, aliases: Map<string, string>, baseMangle?: Mangle): void {
    const prefix = dottedPath.join('__');
    const aliasMap = new Map(aliases);
    const mangle: Mangle = (s) => {
      let r = aliasMap.get(s) ?? (s.startsWith('_') ? `_${prefix}__${s.slice(1)}` : `${prefix}__${s}`);
      if (baseMangle !== undefined) r = baseMangle(r);
      return r;
    };
    const grammarPath = `${dottedPath.join('/')}.lark`;

    const toTry: GrammarLoader[] = [...this.importPaths];
    if (basePath !== undefined) toTry.push(new FileSystemLoader(basePath));
    toTry.push(new StandardLibraryLoader());

    for (const source of toTry) {
      const loaded = source.load(basePath, grammarPath);
      if (loaded === undefined) continue;
      const [joinedPath, text] = loaded;
      const gb = new GrammarBuilder(this.globalKeepAllTokens, this.importPaths);
      gb.loadGrammar(text, joinedPath, mangle);
      gb.removeUnused([...aliases.keys()].map(mangle));
      for (const name of gb.definitions.keys()) {
        if (this.definitions.has(name)) throw new GrammarError(`Cannot import '${name}' from '${grammarPath}': Symbol already defined.`);
      }
      for (const [k, v] of gb.definitions) this.definitions.set(k, v);
      return;
    }
    throw new GrammarImportError(`No such file or directory: '${grammarPath}'`);
  }

  private validate(): void {
    for (const [name, d] of this.definitions) {
      d.params.forEach((p, i) => {
        if (this.definitions.has(p)) throw new GrammarError(`Template Parameter conflicts with rule ${p} (in template ${name})`);
        if (d.params.slice(0, i).includes(p)) throw new GrammarError(`Duplicate Template Parameter ${p} (in template ${name})`);
      });

      const exp = d.tree;
      if (exp === undefined) continue; // Remaining checks don't apply to abstract rules/terminals (created with %declare)

      for (const temp of findData(exp, 'template_usage')) {
        const first = temp.children[0];
        if (!(first instanceof Symbol)) continue;
        const sym = first.name;
        const args = temp.children.length - 1;
        if (!d.params.includes(sym)) {
          const symDef = this.definitions.get(sym);
          if (symDef === undefined) {
            throw grammarError(d.isTerm, `Template '${sym}' used but not defined (in {type} {name})`, [name]);
          }
          if (args !== symDef.params.length) {
            throw grammarError(
              d.isTerm,
              `Wrong number of template arguments used for {name} (expected ${symDef.params.length}, got ${args}) (in {type2} {name2})`,
              [sym, name],
            );
          }
        }
      }

      for (const sym of [...GrammarBuilder.findUsedSymbols(exp)].sort()) {
        if (!this.definitions.has(sym) && !d.params.includes(sym)) {
          throw grammarError(d.isTerm, "{Type} '{name}' used but not defined (in {type2} {name2})", [sym, name]);
        }
      }
    }

    const missing = this.ignoreNames.filter((n) => !this.definitions.has(n)).map(repr);
    if (missing.length > 0) throw new GrammarError(`Terminals {${missing.join(', ')}} were marked to ignore but were not defined!`);
  }

  build(): Grammar {
    this.validate();
    const ruleDefs: RuleDef[] = [];
    const termDefs: TermDef[] = [];
    for (const [name, d] of this.definitions) {
      if (d.isTerm) termDefs.push({ name, tree: d.tree, priority: d.priority });
      else ruleDefs.push({ name, params: d.params, tree: d.tree, options: d.options });
    }
    return new Grammar(ruleDefs, termDefs, [...this.ignoreNames]);
  }
}
