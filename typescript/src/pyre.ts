// Lark's regular expressions are written in the dialect of Python's `re` module. This module parses that dialect and
// gives the three things Lark needs from it: the width of a pattern (mirroring `sre_parse`'s `getwidth`), a
// translation to JavaScript's syntax that keeps Python's semantics, and matching.
//
// The translation never relies on JavaScript's flags (other than `u`): each construct is emitted with the meaning
// it has in Python. `.` excludes only `\n`, `$` matches before a final `\n`, `\w`, `\d` and `\s` are Unicode-aware
// as in Python 3, case-insensitive parts are expanded into character sets, and atomic groups and possessive
// repeats are emulated with lookaheads.

/** Python's `MAXWIDTH` (`1 << 64`), the width of an unbounded pattern. */
export const MAX_WIDTH = 2 ** 64;
/** Python's `MAXREPEAT`, the upper bound of `*`, `+` and `{n,}`. */
const MAX_REPEAT = 4294967295;

/** An error in a regular expression, with Python's wording where possible. */
export class RegexError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'RegexError';
  }
}

/** The flags of Python's `re` that change how a pattern is interpreted. */
export interface Flags {
  ignoreCase: boolean;
  multiline: boolean;
  dotAll: boolean;
  verbose: boolean;
}

/** Parses flags written as letters (`"imsx"`); other letters (`a`, `L`, `u`) are accepted and ignored. */
export function flagsFromLetters(letters: string): Flags {
  const flags: Flags = { ignoreCase: false, multiline: false, dotAll: false, verbose: false };
  for (const c of letters) setFlag(flags, c, true);
  return flags;
}

function setFlag(flags: Flags, c: string, on: boolean): void {
  if (c === 'i') flags.ignoreCase = on;
  else if (c === 'm') flags.multiline = on;
  else if (c === 's') flags.dotAll = on;
  else if (c === 'x') flags.verbose = on;
}

type Category = 'digit' | 'space' | 'word';

type ClassItem =
  | { kind: 'char'; cp: number }
  | { kind: 'range'; lo: number; hi: number }
  | { kind: 'category'; category: Category; negated: boolean };

type Look = 'ahead' | 'notAhead' | 'behind' | 'notBehind';

type RNode =
  | { kind: 'empty' }
  | { kind: 'literal'; cp: number; ignoreCase: boolean }
  | { kind: 'any'; dotAll: boolean }
  | { kind: 'class'; items: ClassItem[]; negated: boolean; ignoreCase: boolean }
  | { kind: 'category'; category: Category; negated: boolean }
  | { kind: 'lineStart'; multiline: boolean }
  | { kind: 'lineEnd'; multiline: boolean }
  | { kind: 'textStart' }
  | { kind: 'textEnd' }
  | { kind: 'wordBoundary'; positive: boolean }
  | { kind: 'group'; index: number | null; body: RNode }
  | { kind: 'atomic'; body: RNode }
  | { kind: 'look'; look: Look; body: RNode }
  | { kind: 'concat'; items: RNode[] }
  | { kind: 'alt'; branches: RNode[] }
  | { kind: 'repeat'; node: RNode; min: number; max: number | null; greedy: boolean; possessive: boolean }
  | { kind: 'backref'; group: number; ignoreCase: boolean }
  | { kind: 'conditional'; group: number; yes: RNode; no: RNode | null };

type Width = [number, number];

const isOctal = (c: string | undefined): c is string => c !== undefined && c >= '0' && c <= '7';
const isDigit = (c: string | undefined): c is string => c !== undefined && c >= '0' && c <= '9';
const isAsciiAlnum = (c: string): boolean => /^[A-Za-z0-9]$/.test(c);
const WHITESPACE = /^\s$/u;
const NAME_START = /^[_\p{L}]$/u;
const NAME_CONTINUE = /^[_\p{L}\p{N}]$/u;

class Parser {
  private readonly chars: string[];
  pos = 0;
  groupCount = 0;
  private readonly groupNames = new Map<string, number>();
  private readonly openGroups: number[] = [];
  readonly groupWidths: Width[] = [];

  constructor(pattern: string) {
    this.chars = [...pattern];
  }

  get length(): number {
    return this.chars.length;
  }

  error(message: string): RegexError {
    return new RegexError(`${message} at position ${this.pos}`);
  }

  peek(offset = 0): string | undefined {
    return this.chars[this.pos + offset];
  }

  private startsWith(s: string): boolean {
    let i = 0;
    for (const c of s) {
      if (this.chars[this.pos + i] !== c) return false;
      i++;
    }
    return true;
  }

  private skipVerbose(flags: Flags): void {
    if (!flags.verbose) return;
    for (;;) {
      const c = this.peek();
      if (c === undefined) return;
      if (WHITESPACE.test(c)) {
        this.pos++;
      } else if (c === '#') {
        while (this.peek() !== undefined && this.peek() !== '\n') this.pos++;
      } else {
        return;
      }
    }
  }

  parseAlternation(flags: Flags, top: boolean): RNode {
    const branches = [this.parseSequence(flags, top)];
    while (this.peek() === '|') {
      this.pos++;
      branches.push(this.parseSequence(flags, false));
    }
    return branches.length === 1 ? branches[0]! : { kind: 'alt', branches };
  }

  private parseSequence(flags: Flags, atStart: boolean): RNode {
    const items: RNode[] = [];
    for (;;) {
      this.skipVerbose(flags);
      const c = this.peek();
      if (c === undefined || c === '|' || c === ')') break;
      let item: RNode;
      if (c === '*' || c === '+' || c === '?') throw this.error('nothing to repeat');
      if (c === '{' && this.repeatAhead() !== null) throw this.error('nothing to repeat');
      if (c === '(') {
        if (this.startsWith('(?') && this.globalFlagsAhead()) {
          if (!atStart || items.length > 0) throw this.error('global flags not at the start of the expression');
          this.parseGlobalFlags(flags);
          continue;
        }
        item = this.parseGroup(flags);
      } else if (c === '[') {
        item = this.parseClass(flags);
      } else if (c === '.') {
        this.pos++;
        item = { kind: 'any', dotAll: flags.dotAll };
      } else if (c === '^') {
        this.pos++;
        item = { kind: 'lineStart', multiline: flags.multiline };
      } else if (c === '$') {
        this.pos++;
        item = { kind: 'lineEnd', multiline: flags.multiline };
      } else if (c === '\\') {
        item = this.parseEscape(flags);
      } else {
        this.pos++;
        item = { kind: 'literal', cp: c.codePointAt(0)!, ignoreCase: flags.ignoreCase };
      }
      atStart = false;
      items.push(this.parseRepeats(item, flags));
    }
    if (items.length === 0) return { kind: 'empty' };
    if (items.length === 1) return items[0]!;
    return { kind: 'concat', items };
  }

  /** The (min, max, length) of a `{m,n}` quantifier at the current position, if there is one. */
  private repeatAhead(): [number, number | null, number] | null {
    let i = this.pos + 1;
    let lo = '';
    while (isDigit(this.chars[i])) lo += this.chars[i++];
    let hi: string | null = null;
    if (this.chars[i] === ',') {
      i++;
      hi = '';
      while (isDigit(this.chars[i])) hi += this.chars[i++];
    }
    if (this.chars[i] !== '}') return null;
    if (lo === '' && hi === null) return null;
    const min = lo === '' ? 0 : Number(lo);
    const max = hi === null ? min : hi === '' ? null : Number(hi);
    return [min, max, i + 1 - this.pos];
  }

  private parseRepeats(item: RNode, flags: Flags): RNode {
    let repeated = false;
    for (;;) {
      this.skipVerbose(flags);
      let min: number;
      let max: number | null;
      const c = this.peek();
      if (c === '*') {
        this.pos++;
        [min, max] = [0, null];
      } else if (c === '+') {
        this.pos++;
        [min, max] = [1, null];
      } else if (c === '?') {
        this.pos++;
        [min, max] = [0, 1];
      } else if (c === '{') {
        const r = this.repeatAhead();
        if (r === null) return item;
        if (r[1] !== null && r[1] < r[0]) throw this.error('min repeat greater than max repeat');
        this.pos += r[2];
        [min, max] = [r[0], r[1]];
      } else {
        return item;
      }
      if (repeated) throw this.error('multiple repeat');
      if (['lineStart', 'lineEnd', 'textStart', 'textEnd', 'wordBoundary'].includes(item.kind)) {
        throw this.error('nothing to repeat');
      }
      let greedy = true;
      let possessive = false;
      if (this.peek() === '?') {
        this.pos++;
        greedy = false;
      } else if (this.peek() === '+') {
        this.pos++;
        possessive = true;
      }
      item = { kind: 'repeat', node: item, min, max, greedy, possessive };
      repeated = true;
    }
  }

  private globalFlagsAhead(): boolean {
    let i = this.pos + 2;
    let any = false;
    while (this.chars[i] !== undefined && 'aiLmsux'.includes(this.chars[i]!)) {
      any = true;
      i++;
    }
    return any && this.chars[i] === ')';
  }

  private parseGlobalFlags(flags: Flags): void {
    this.pos += 2;
    for (;;) {
      const c = this.peek();
      if (c === undefined) throw this.error('missing ), unterminated subpattern');
      this.pos++;
      if (c === ')') return;
      setFlag(flags, c, true);
    }
  }

  private readName(terminator: string): string {
    const start = this.pos;
    for (;;) {
      const c = this.peek();
      if (c === undefined) throw this.error('missing group name terminator');
      if (c === terminator) {
        const name = this.chars.slice(start, this.pos).join('');
        this.pos++;
        if (name === '') throw this.error('missing group name');
        const cs = [...name];
        if (!NAME_START.test(cs[0]!) || !cs.slice(1).every((x) => NAME_CONTINUE.test(x))) {
          throw this.error(`bad character in group name '${name}'`);
        }
        return name;
      }
      this.pos++;
    }
  }

  private groupRef(name: string): number {
    if (/^[0-9]+$/.test(name)) return Number(name);
    const index = this.groupNames.get(name);
    if (index === undefined) throw this.error(`unknown group name '${name}'`);
    return index;
  }

  private parseBody(flags: Flags): RNode {
    const inner = { ...flags };
    const body = this.parseAlternation(inner, false);
    if (this.peek() !== ')') throw this.error('missing ), unterminated subpattern');
    this.pos++;
    return body;
  }

  private parseGroup(flags: Flags): RNode {
    this.pos++;
    if (this.peek() !== '?') return this.parseCapture(null, flags);
    this.pos++;
    const c = this.peek();
    switch (c) {
      case ':':
        this.pos++;
        return this.parseBody(flags);
      case '>':
        this.pos++;
        return { kind: 'atomic', body: this.parseBody(flags) };
      case '=':
        this.pos++;
        return { kind: 'look', look: 'ahead', body: this.parseBody(flags) };
      case '!':
        this.pos++;
        return { kind: 'look', look: 'notAhead', body: this.parseBody(flags) };
      case '<': {
        this.pos++;
        const l = this.peek();
        if (l !== '=' && l !== '!') throw this.error('unknown extension ?<');
        this.pos++;
        const body = this.parseBody(flags);
        const [lo, hi] = widthOf(body, this.groupWidths);
        if (lo !== hi) throw this.error('look-behind requires fixed-width pattern');
        return { kind: 'look', look: l === '=' ? 'behind' : 'notBehind', body };
      }
      case '#':
        for (;;) {
          const x = this.peek();
          if (x === undefined) throw this.error('missing ), unterminated comment');
          this.pos++;
          if (x === ')') return { kind: 'empty' };
        }
      case 'P': {
        this.pos++;
        if (this.peek() === '<') {
          this.pos++;
          const name = this.readName('>');
          if (this.groupNames.has(name)) throw this.error(`redefinition of group name '${name}'`);
          return this.parseCapture(name, flags);
        }
        if (this.peek() === '=') {
          this.pos++;
          const group = this.groupRef(this.readName(')'));
          if (this.openGroups.includes(group)) throw this.error('cannot refer to an open group');
          return { kind: 'backref', group, ignoreCase: flags.ignoreCase };
        }
        throw this.error('unknown extension ?P');
      }
      case '(': {
        this.pos++;
        const group = this.groupRef(this.readName(')'));
        if (group === 0 || group > this.groupCount) throw this.error('invalid group reference');
        const inner = { ...flags };
        const yes = this.parseSequence(inner, false);
        let no: RNode | null = null;
        if (this.peek() === '|') {
          this.pos++;
          no = this.parseSequence(inner, false);
          if (this.peek() === '|') throw this.error('conditional backref with more than two branches');
        }
        if (this.peek() !== ')') throw this.error('missing ), unterminated subpattern');
        this.pos++;
        return { kind: 'conditional', group, yes, no };
      }
      default:
        if (c !== undefined && 'aiLmsux-'.includes(c)) {
          // Scoped flags: (?imsx-imsx:...)
          const inner = { ...flags };
          let on = true;
          for (;;) {
            const x = this.peek();
            if (x === undefined || x === ':') break;
            if (x === '-') {
              if (!on) throw this.error('bad inline flags');
              on = false;
            } else if ('aiLmsux'.includes(x)) {
              if (!on && !'imsx'.includes(x)) throw this.error("bad inline flags: cannot turn off flags 'a', 'u' and 'L'");
              setFlag(inner, x, on);
            } else if (x === ')') {
              throw this.error('global flags not at the start of the expression');
            } else {
              throw this.error('unknown flag');
            }
            this.pos++;
          }
          if (this.peek() !== ':') throw this.error('missing -, : or )');
          this.pos++;
          return { kind: 'concat', items: [this.parseBody(inner)] };
        }
        throw this.error('unknown extension');
    }
  }

  private parseCapture(name: string | null, flags: Flags): RNode {
    const index = ++this.groupCount;
    if (name !== null) this.groupNames.set(name, index);
    this.openGroups.push(index);
    this.groupWidths.push([0, 0]);
    const body = this.parseBody(flags);
    this.openGroups.pop();
    this.groupWidths[index - 1] = widthOf(body, this.groupWidths);
    return { kind: 'group', index, body };
  }

  private parseHex(digits: number): number {
    const text = this.chars.slice(this.pos, this.pos + digits).join('');
    if (text.length !== digits || !/^[0-9a-fA-F]+$/.test(text)) throw this.error('incomplete escape');
    this.pos += digits;
    const value = parseInt(text, 16);
    if (value > 0x10ffff) throw this.error('bad escape (invalid code point)');
    return value;
  }

  /** Escapes that mean the same inside and outside a class. Returns null for the others. */
  private parseCommonEscape(c: string): number | null {
    switch (c) {
      case 'n':
        return 0x0a;
      case 't':
        return 0x09;
      case 'r':
        return 0x0d;
      case 'f':
        return 0x0c;
      case 'v':
        return 0x0b;
      case 'a':
        return 0x07;
      case 'x':
        return this.parseHex(2);
      case 'u':
        return this.parseHex(4);
      case 'U':
        return this.parseHex(8);
      case 'N':
        throw this.error('named Unicode escapes (\\N{...}) are not supported');
    }
    if (isAsciiAlnum(c)) return null;
    return c.codePointAt(0)!;
  }

  private parseEscape(flags: Flags): RNode {
    this.pos++;
    const c = this.peek();
    if (c === undefined) throw this.error('bad escape (end of pattern)');
    this.pos++;
    const ic = flags.ignoreCase;
    switch (c) {
      case 'A':
        return { kind: 'textStart' };
      case 'Z':
        return { kind: 'textEnd' };
      case 'b':
        return { kind: 'wordBoundary', positive: true };
      case 'B':
        return { kind: 'wordBoundary', positive: false };
      case 'd':
        return { kind: 'category', category: 'digit', negated: false };
      case 'D':
        return { kind: 'category', category: 'digit', negated: true };
      case 's':
        return { kind: 'category', category: 'space', negated: false };
      case 'S':
        return { kind: 'category', category: 'space', negated: true };
      case 'w':
        return { kind: 'category', category: 'word', negated: false };
      case 'W':
        return { kind: 'category', category: 'word', negated: true };
      case '0': {
        let value = 0;
        for (let n = 0; n < 2 && isOctal(this.peek()); n++) value = value * 8 + Number(this.chars[this.pos++]);
        return { kind: 'literal', cp: value, ignoreCase: ic };
      }
    }
    if (c >= '1' && c <= '9') {
      // An octal escape of three digits, or a group reference of one or two digits.
      if (isOctal(c) && isOctal(this.peek()) && isOctal(this.peek(1))) {
        const value = parseInt(c + this.peek() + this.peek(1), 8);
        if (value > 0o377) throw this.error('octal escape value outside of range 0-0o377');
        this.pos += 2;
        return { kind: 'literal', cp: value, ignoreCase: ic };
      }
      let group = Number(c);
      if (isDigit(this.peek())) group = group * 10 + Number(this.chars[this.pos++]);
      if (group > this.groupCount) throw this.error('invalid group reference');
      if (this.openGroups.includes(group)) throw this.error('cannot refer to an open group');
      return { kind: 'backref', group, ignoreCase: ic };
    }
    const cp = this.parseCommonEscape(c);
    if (cp === null) throw this.error(`bad escape \\${c}`);
    return { kind: 'literal', cp, ignoreCase: ic };
  }

  private parseClassChar(): ClassItem {
    const c = this.peek();
    if (c === undefined) throw this.error('unterminated character set');
    this.pos++;
    if (c !== '\\') return { kind: 'char', cp: c.codePointAt(0)! };
    const e = this.peek();
    if (e === undefined) throw this.error('unterminated character set');
    this.pos++;
    switch (e) {
      case 'd':
        return { kind: 'category', category: 'digit', negated: false };
      case 'D':
        return { kind: 'category', category: 'digit', negated: true };
      case 's':
        return { kind: 'category', category: 'space', negated: false };
      case 'S':
        return { kind: 'category', category: 'space', negated: true };
      case 'w':
        return { kind: 'category', category: 'word', negated: false };
      case 'W':
        return { kind: 'category', category: 'word', negated: true };
      case 'b':
        return { kind: 'char', cp: 0x08 };
    }
    if (isOctal(e)) {
      let value = Number(e);
      for (let n = 1; n < 3 && isOctal(this.peek()); n++) value = value * 8 + Number(this.chars[this.pos++]);
      if (value > 0o377) throw this.error('octal escape value outside of range 0-0o377');
      return { kind: 'char', cp: value };
    }
    const cp = this.parseCommonEscape(e);
    if (cp === null) throw this.error(`bad escape \\${e}`);
    return { kind: 'char', cp };
  }

  private parseClass(flags: Flags): RNode {
    this.pos++;
    let negated = false;
    if (this.peek() === '^') {
      this.pos++;
      negated = true;
    }
    const items: ClassItem[] = [];
    let first = true;
    for (;;) {
      const c = this.peek();
      if (c === undefined) throw this.error('unterminated character set');
      if (c === ']' && !first) {
        this.pos++;
        break;
      }
      first = false;
      const item = this.parseClassChar();
      if (this.peek() === '-' && this.peek(1) !== undefined && this.peek(1) !== ']') {
        this.pos++;
        const end = this.parseClassChar();
        if (item.kind !== 'char' || end.kind !== 'char') throw this.error('bad character range');
        if (end.cp < item.cp) throw this.error('bad character range');
        items.push({ kind: 'range', lo: item.cp, hi: end.cp });
      } else {
        items.push(item);
      }
    }
    return { kind: 'class', items, negated, ignoreCase: flags.ignoreCase };
  }
}

const add = (a: number, b: number): number => Math.min(a + b, MAX_WIDTH);

/** `sre_parse`'s `getwidth`. */
function widthOf(node: RNode, groups: Width[]): Width {
  let lo: number;
  let hi: number;
  switch (node.kind) {
    case 'empty':
    case 'lineStart':
    case 'lineEnd':
    case 'textStart':
    case 'textEnd':
    case 'wordBoundary':
    case 'look':
      [lo, hi] = [0, 0];
      break;
    case 'literal':
    case 'any':
    case 'class':
    case 'category':
      [lo, hi] = [1, 1];
      break;
    case 'group':
    case 'atomic':
      [lo, hi] = widthOf(node.body, groups);
      break;
    case 'concat':
      [lo, hi] = [0, 0];
      for (const item of node.items) {
        const [l, h] = widthOf(item, groups);
        lo = add(lo, l);
        hi = add(hi, h);
      }
      break;
    case 'alt':
      [lo, hi] = [MAX_WIDTH, 0];
      for (const b of node.branches) {
        const [l, h] = widthOf(b, groups);
        lo = Math.min(lo, l);
        hi = Math.max(hi, h);
      }
      break;
    case 'repeat': {
      const [i, j] = widthOf(node.node, groups);
      lo = i * node.min;
      const max = node.max ?? MAX_REPEAT;
      hi = max === MAX_REPEAT && j > 0 ? MAX_WIDTH : j * max;
      break;
    }
    case 'backref':
      [lo, hi] = groups[node.group - 1] ?? [0, 0];
      break;
    case 'conditional': {
      const [i, j] = widthOf(node.yes, groups);
      if (node.no !== null) {
        const [l, h] = widthOf(node.no, groups);
        [lo, hi] = [Math.min(i, l), Math.max(j, h)];
      } else {
        [lo, hi] = [0, j];
      }
      break;
    }
  }
  return [Math.min(lo, MAX_WIDTH), Math.min(hi, MAX_WIDTH)];
}

// ---------------------------------------------------------------------------------------------------------------
//  Case folding
// ---------------------------------------------------------------------------------------------------------------

let caseGroups: Map<number, number[]> | undefined;
let casedCodePoints: number[] | undefined;

/** Builds the classes of characters that are equal when case is ignored (simple case mappings, as in `re`). */
function buildCaseGroups(): void {
  const parent = new Map<number, number>();
  const find = (x: number): number => {
    let r = x;
    while (parent.has(r) && parent.get(r) !== r) r = parent.get(r)!;
    let y = x;
    while (y !== r) {
      const next = parent.get(y)!;
      parent.set(y, r);
      y = next;
    }
    return r;
  };
  const union = (a: number, b: number): void => {
    if (!parent.has(a)) parent.set(a, a);
    if (!parent.has(b)) parent.set(b, b);
    const ra = find(a);
    const rb = find(b);
    if (ra !== rb) parent.set(Math.max(ra, rb), Math.min(ra, rb));
  };
  const single = (s: string): number | null => {
    const cp = s.codePointAt(0)!;
    return s.length === (cp > 0xffff ? 2 : 1) ? cp : null;
  };
  for (let cp = 0; cp < 0x20000; cp++) {
    if (cp >= 0xd800 && cp <= 0xdfff) continue;
    const c = String.fromCodePoint(cp);
    const lower = single(c.toLowerCase());
    const upper = single(c.toUpperCase());
    if (lower !== null && lower !== cp) union(cp, lower);
    if (upper !== null && upper !== cp) union(cp, upper);
  }
  // Python's `re` matches these too, though their case mappings don't connect them.
  for (const [a, b] of [
    [0x130, 0x69],
    [0x131, 0x69],
    [0x390, 0x1fd3],
    [0x3b0, 0x1fe3],
    [0xfb05, 0xfb06],
  ] as const) {
    union(a, b);
  }
  const groups = new Map<number, number[]>();
  for (const cp of parent.keys()) {
    const r = find(cp);
    let g = groups.get(r);
    if (g === undefined) {
      g = [];
      groups.set(r, g);
    }
    g.push(cp);
  }
  caseGroups = new Map();
  for (const g of groups.values()) {
    if (g.length < 2) continue;
    g.sort((a, b) => a - b);
    for (const cp of g) caseGroups.set(cp, g);
  }
  casedCodePoints = [...caseGroups.keys()].sort((a, b) => a - b);
}

/** The characters equal to `cp` when case is ignored, including `cp`. */
function caseVariants(cp: number): number[] {
  if (caseGroups === undefined) buildCaseGroups();
  return caseGroups!.get(cp) ?? [cp];
}

/** The characters with case variants in [lo, hi]. */
function casedInRange(lo: number, hi: number): number[] {
  if (casedCodePoints === undefined) buildCaseGroups();
  const list = casedCodePoints!;
  let a = 0;
  let b = list.length;
  while (a < b) {
    const m = (a + b) >> 1;
    if (list[m]! < lo) a = m + 1;
    else b = m;
  }
  const result: number[] = [];
  for (let i = a; i < list.length && list[i]! <= hi; i++) result.push(list[i]!);
  return result;
}

// ---------------------------------------------------------------------------------------------------------------
//  Translation to JavaScript
// ---------------------------------------------------------------------------------------------------------------

// Python's `\w`: `str.isalnum()` characters and `_`. `\d`: decimal digits. `\s`: `str.isspace()` characters.
const WORD_CLASS = '\\p{L}\\p{N}_';
const DIGIT_CLASS = '\\p{Nd}';
const SPACE_CLASS = '\\t-\\r\\x1c-\\x20\\x85\\xa0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000';

function categoryClass(category: Category): string {
  return category === 'digit' ? DIGIT_CLASS : category === 'space' ? SPACE_CLASS : WORD_CLASS;
}

function emitChar(cp: number): string {
  const c = String.fromCodePoint(cp);
  if (/^[A-Za-z0-9_]$/.test(c) || (cp > 0x7f && !/^[\s\p{C}]$/u.test(c))) return c;
  return '\\u{' + cp.toString(16) + '}';
}

class Emitter {
  /** The number of capturing groups emitted so far (including those that emulate atomic groups). */
  private groups = 0;
  /** The JavaScript group of each Python group. */
  private readonly groupMap = new Map<number, number>();

  emit(node: RNode): string {
    switch (node.kind) {
      case 'empty':
        return '';
      case 'literal': {
        const variants = node.ignoreCase ? caseVariants(node.cp) : [node.cp];
        return variants.length === 1 ? emitChar(node.cp) : '[' + variants.map(emitChar).join('') + ']';
      }
      case 'any':
        return node.dotAll ? '[\\s\\S]' : '[^\\n]';
      case 'class':
        return this.emitClass(node.items, node.negated, node.ignoreCase);
      case 'category':
        return (node.negated ? '[^' : '[') + categoryClass(node.category) + ']';
      case 'lineStart':
        return node.multiline ? '(?:(?<![\\s\\S])|(?<=\\n))' : '(?<![\\s\\S])';
      case 'lineEnd':
        return node.multiline ? '(?=\\n|(?![\\s\\S]))' : '(?=\\n?(?![\\s\\S]))';
      case 'textStart':
        return '(?<![\\s\\S])';
      case 'textEnd':
        return '(?![\\s\\S])';
      case 'wordBoundary': {
        const w = WORD_CLASS;
        return node.positive
          ? `(?:(?<=[${w}])(?![${w}])|(?<![${w}])(?=[${w}]))`
          : `(?:(?<=[${w}])(?=[${w}])|(?<![${w}])(?![${w}]))`;
      }
      case 'group': {
        const index = ++this.groups;
        if (node.index !== null) this.groupMap.set(node.index, index);
        return '(' + this.emit(node.body) + ')';
      }
      case 'atomic':
        return this.atomic(() => this.emit(node.body));
      case 'look': {
        const open = { ahead: '(?=', notAhead: '(?!', behind: '(?<=', notBehind: '(?<!' }[node.look];
        return open + this.emit(node.body) + ')';
      }
      case 'concat':
        // Wrap alternations so that they don't swallow their neighbours.
        return node.items.map((item) => (item.kind === 'alt' ? '(?:' + this.emit(item) + ')' : this.emit(item))).join('');
      case 'alt':
        return node.branches.map((b) => this.emit(b)).join('|');
      case 'repeat': {
        const body = () => {
          let q: string;
          if (node.min === 0 && node.max === null) q = '*';
          else if (node.min === 1 && node.max === null) q = '+';
          else if (node.min === 0 && node.max === 1) q = '?';
          else if (node.max === null) q = `{${node.min},}`;
          else if (node.min === node.max) q = `{${node.min}}`;
          else q = `{${node.min},${node.max}}`;
          return '(?:' + this.emit(node.node) + ')' + q + (node.greedy ? '' : '?');
        };
        return node.possessive ? this.atomic(body) : body();
      }
      case 'backref': {
        if (node.ignoreCase) throw new RegexError('case-insensitive back-references are not supported');
        const index = this.groupMap.get(node.group);
        if (index === undefined) throw new RegexError('invalid group reference');
        return '(?:\\' + index + ')';
      }
      case 'conditional':
        throw new RegexError('conditional groups ((?(1)...)) are not supported');
    }
  }

  /** An atomic group, emulated by a lookahead that captures the match and a back-reference that consumes it. */
  private atomic(body: () => string): string {
    const index = ++this.groups;
    return '(?=(' + body() + '))\\' + index;
  }

  private emitClass(items: ClassItem[], negated: boolean, ignoreCase: boolean): string {
    let positive = '';
    const extra = new Set<number>();
    const negatedCategories: Category[] = [];
    for (const item of items) {
      if (item.kind === 'char') {
        positive += emitChar(item.cp);
        if (ignoreCase) for (const v of caseVariants(item.cp)) extra.add(v);
      } else if (item.kind === 'range') {
        positive += emitChar(item.lo) + '-' + emitChar(item.hi);
        if (ignoreCase) for (const cp of casedInRange(item.lo, item.hi)) for (const v of caseVariants(cp)) extra.add(v);
      } else if (item.negated) {
        negatedCategories.push(item.category);
      } else {
        positive += categoryClass(item.category);
      }
    }
    for (const cp of extra) positive += emitChar(cp);
    if (negatedCategories.length === 0) {
      if (positive === '') return negated ? '[\\s\\S]' : '[^\\s\\S]';
      return (negated ? '[^' : '[') + positive + ']';
    }
    // JavaScript can't put a negated category (like \W) in a set without the `v` flag: use an alternation.
    const parts = negatedCategories.map((c) => '[^' + categoryClass(c) + ']');
    if (positive !== '') parts.unshift('[' + positive + ']');
    const union = parts.join('|');
    return negated ? '(?:(?!' + union + ')[\\s\\S])' : '(?:' + union + ')';
  }
}

/** A parsed regular expression. */
export class Ast {
  readonly node: RNode;
  private readonly groupWidths: Width[];

  private constructor(node: RNode, groupWidths: Width[]) {
    this.node = node;
    this.groupWidths = groupWidths;
  }

  /** Parses a pattern in Python's `re` dialect. */
  static parse(pattern: string, flags: Flags = flagsFromLetters('')): Ast {
    const parser = new Parser(pattern);
    const node = parser.parseAlternation({ ...flags }, true);
    if (parser.pos < parser.length) throw parser.error('unbalanced parenthesis');
    return new Ast(node, parser.groupWidths);
  }

  /** The minimum and maximum number of characters the pattern matches, as Python computes them. */
  width(): Width {
    return widthOf(this.node, this.groupWidths);
  }

  /** The pattern in JavaScript's syntax, for the `u` flag. */
  toJs(): string {
    return new Emitter().emit(this.node);
  }

  /** Strings that (likely) match the pattern, for detecting collisions between terminals. */
  samples(): string[] {
    const groups: string[][] = [];
    return [...new Set(samplesOf(this.node, groups))];
  }
}

/** Python's `re.escape`. */
export function escape(value: string): string {
  let out = '';
  for (const c of value) {
    if ('()[]{}?*+-|^$\\.&~# \t\n\r\x0b\x0c'.includes(c)) {
      out += '\\';
      out += c === '\t' ? 't' : c === '\n' ? 'n' : c === '\r' ? 'r' : c === '\x0b' ? 'v' : c === '\x0c' ? 'f' : c;
    } else {
      out += c;
    }
  }
  return out;
}

/** The width of a pattern, as Python's `sre_parse` computes it. */
export function width(pattern: string, flags?: Flags): Width {
  return Ast.parse(pattern, flags).width();
}

/** A compiled regular expression with Python's semantics. */
export class Regex {
  /** The pattern, as written in Python's dialect. */
  readonly source: string;
  private readonly sticky: RegExp;
  private readonly translated: string;
  private full?: RegExp;

  /** Compiles a pattern in Python's `re` dialect. */
  constructor(pattern: string, flags?: Flags) {
    this.source = pattern;
    this.translated = Ast.parse(pattern, flags).toJs();
    try {
      this.sticky = new RegExp(this.translated, 'uy');
    } catch (e) {
      throw new RegexError((e as Error).message);
    }
  }

  /**
   * Python's `pattern.match(text, pos)`: the end of the match that starts at `pos`, if any. Assertions see the text
   * before `pos`, as in Python.
   */
  matchAt(text: string, pos: number): number | null {
    this.sticky.lastIndex = pos;
    const m = this.sticky.exec(text);
    return m === null ? null : pos + m[0].length;
  }

  /** Python's `re.search(pattern, text, pos)`: the first match at or after `pos`, as [start, end]. */
  searchFrom(text: string, pos: number): [number, number] | null {
    for (let start = pos; ; ) {
      const end = this.matchAt(text, start);
      if (end !== null) return [start, end];
      if (start >= text.length) return null;
      start += (text.codePointAt(start) ?? 0) > 0xffff ? 2 : 1;
    }
  }

  /** Python's `fullmatch`: whether the whole text matches, trying every way the pattern can match. */
  fullMatch(text: string): boolean {
    if (this.full === undefined) this.full = new RegExp('(?:' + this.translated + ')(?![\\s\\S])', 'uy');
    this.full.lastIndex = 0;
    return this.full.test(text);
  }
}

// ---------------------------------------------------------------------------------------------------------------
//  Samples, for strict mode's collision detection
// ---------------------------------------------------------------------------------------------------------------

const SAMPLE_LIMIT = 256;
const SAMPLE_ALPHABET = [..."aAzZ09_ -+.,;:!?\"'()[]{}<>/\\|@#$%^&*=~`\t\néß"];

const CATEGORY_TESTS: Record<Category, RegExp> = {
  digit: new RegExp('^[' + DIGIT_CLASS + ']$', 'u'),
  space: new RegExp('^[' + SPACE_CLASS + ']$', 'u'),
  word: new RegExp('^[' + WORD_CLASS + ']$', 'u'),
};

function classContains(items: ClassItem[], c: string): boolean {
  const cp = c.codePointAt(0)!;
  return items.some((item) => {
    if (item.kind === 'char') return item.cp === cp;
    if (item.kind === 'range') return item.lo <= cp && cp <= item.hi;
    return CATEGORY_TESTS[item.category].test(c) !== item.negated;
  });
}

function sampleChars(node: RNode): string[] {
  switch (node.kind) {
    case 'literal': {
      const c = String.fromCodePoint(node.cp);
      const v = [c];
      if (node.ignoreCase) for (const x of [c.toUpperCase(), c.toLowerCase()]) if (x !== c && !v.includes(x)) v.push(x);
      return v;
    }
    case 'any':
      return node.dotAll ? SAMPLE_ALPHABET : SAMPLE_ALPHABET.filter((c) => c !== '\n');
    case 'class': {
      const v: string[] = [];
      if (!node.negated) {
        for (const item of node.items) {
          if (item.kind === 'char') v.push(String.fromCodePoint(item.cp));
          else if (item.kind === 'range') {
            v.push(String.fromCodePoint(item.lo), String.fromCodePoint(item.hi));
            const mid = Math.floor((item.lo + item.hi) / 2);
            if (mid < 0xd800 || mid > 0xdfff) v.push(String.fromCodePoint(mid));
          } else {
            v.push(...SAMPLE_ALPHABET.filter((c) => classContains([item], c)));
          }
        }
      } else {
        v.push(...SAMPLE_ALPHABET.filter((c) => !classContains(node.items, c)));
      }
      return v.filter((c, i) => i === 0 || v[i - 1] !== c);
    }
    case 'category':
      return SAMPLE_ALPHABET.filter((c) => CATEGORY_TESTS[node.category].test(c) !== node.negated);
    default:
      return [];
  }
}

function samplesOf(node: RNode, groups: string[][]): string[] {
  let out: string[];
  switch (node.kind) {
    case 'literal':
    case 'any':
    case 'class':
    case 'category':
      out = sampleChars(node);
      break;
    case 'group': {
      const s = samplesOf(node.body, groups);
      if (node.index !== null) {
        while (groups.length < node.index) groups.push([]);
        groups[node.index - 1] = s;
      }
      out = s;
      break;
    }
    case 'atomic':
      out = samplesOf(node.body, groups);
      break;
    case 'backref':
      out = groups[node.group - 1] ?? [''];
      break;
    case 'concat': {
      let acc = [''];
      for (const item of node.items) {
        const next = samplesOf(item, groups);
        if (next.length === 0) continue;
        const combined: string[] = [];
        for (const a of acc) {
          for (const b of next) {
            if (combined.length >= SAMPLE_LIMIT) break;
            combined.push(a + b);
          }
        }
        acc = combined;
      }
      out = acc;
      break;
    }
    case 'alt':
      out = node.branches.flatMap((b) => samplesOf(b, groups));
      break;
    case 'repeat': {
      const inner = samplesOf(node.node, groups);
      out = [];
      const hi = Math.min(node.max ?? node.min + 3, node.min + 3);
      for (let n = node.min; n <= hi; n++) {
        if (n === 0) {
          out.push('');
          continue;
        }
        for (const s of inner) out.push(s.repeat(n));
        if (inner.length > 1 && n > 1) out.push(inner[0]!.repeat(n - 1) + inner[1]!);
      }
      break;
    }
    case 'conditional':
      out = [...samplesOf(node.yes, groups), ...(node.no ? samplesOf(node.no, groups) : [])];
      break;
    default:
      out = [''];
  }
  return out.slice(0, SAMPLE_LIMIT);
}
