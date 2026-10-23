// Small helpers shared by the modules, mirroring `lark.utils`.

/** Groups the items by key, keeping the order in which the keys first appear (Lark's `classify`). */
export function classify<T, K, V = T>(items: Iterable<T>, key: (item: T) => K, value?: (item: T) => V): Map<K, V[]> {
  const map = new Map<K, V[]>();
  for (const item of items) {
    const k = key(item);
    let list = map.get(k);
    if (list === undefined) {
      list = [];
      map.set(k, list);
    }
    list.push(value ? value(item) : (item as unknown as V));
  }
  return map;
}

/** Breadth-first search that visits every node once. `id` gives the identity of a node. */
export function bfs<T>(initial: Iterable<T>, expand: (node: T) => Iterable<T>, id: (node: T) => unknown = (n) => n): T[] {
  const visited = new Set<unknown>();
  const queue: T[] = [];
  for (const i of initial) {
    const k = id(i);
    if (!visited.has(k)) {
      visited.add(k);
      queue.push(i);
    }
  }
  const result: T[] = [];
  for (let head = 0; head < queue.length; head++) {
    const node = queue[head]!;
    for (const next of expand(node)) {
      const k = id(next);
      if (!visited.has(k)) {
        visited.add(k);
        queue.push(next);
      }
    }
    result.push(node);
  }
  return result;
}

/** Splits `n` up into smaller factors and summands <= `maxFactor` (Lark's `small_factors`). */
export function smallFactors(n: number, maxFactor: number): Array<[number, number]> {
  if (n <= maxFactor) return [[n, 0]];
  for (let a = maxFactor; a >= 2; a--) {
    const r = Math.floor(n / a);
    const b = n % a;
    if (a + b <= maxFactor) {
      const result = smallFactors(r, maxFactor);
      result.push([a, b]);
      return result;
    }
  }
  throw new Error(`Failed to factorize ${n}`);
}

const LOWER_OR_TITLE = /[\p{Lowercase}\p{Lt}]/u;
const UPPER = /\p{Uppercase}/u;

/** Python's `str.isupper()`. */
export function isUpper(s: string): boolean {
  let cased = false;
  for (const c of s) {
    if (LOWER_OR_TITLE.test(c)) return false;
    if (UPPER.test(c)) cased = true;
  }
  return cased;
}

const ID_START = /^[\p{Lu}\p{Ll}\p{Lt}\p{Lm}\p{Lo}\p{Mn}\p{Mc}\p{Pc}_]*$/u;
const ID_CONTINUE = /^[\p{Lu}\p{Ll}\p{Lt}\p{Lm}\p{Lo}\p{Mn}\p{Mc}\p{Pc}\p{Nd}\p{Nl}_]*$/u;

/** Lark's `is_id_start`: every character is a letter, a mark, connector punctuation or `_`. */
export function isIdStart(s: string): boolean {
  return ID_START.test(s);
}

/** Lark's `is_id_continue`: like {@link isIdStart}, plus decimal digits and letter numbers. */
export function isIdContinue(s: string): boolean {
  return ID_CONTINUE.test(s);
}

/** Python's `repr()` of a string. */
export function repr(s: string): string {
  const quote = s.includes("'") && !s.includes('"') ? '"' : "'";
  let out = quote;
  for (const c of s) {
    const cp = c.codePointAt(0)!;
    if (c === '\\') out += '\\\\';
    else if (c === '\n') out += '\\n';
    else if (c === '\r') out += '\\r';
    else if (c === '\t') out += '\\t';
    else if (c === quote) out += '\\' + c;
    else if (cp < 0x20 || cp === 0x7f) out += '\\x' + cp.toString(16).padStart(2, '0');
    else out += c;
  }
  return out + quote;
}

/** Python's `str.expandtabs()`. */
export function expandTabs(s: string): string {
  if (!s.includes('\t')) return s;
  let out = '';
  let column = 0;
  for (const c of s) {
    if (c === '\t') {
      const spaces = 8 - (column % 8);
      out += ' '.repeat(spaces);
      column += spaces;
    } else {
      out += c;
      column = c === '\n' || c === '\r' ? 0 : column + 1;
    }
  }
  return out;
}

/** The number of code points in the string, which is what Python's `len()` counts. */
export function codePointLength(s: string): number {
  let n = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff && i + 1 < s.length) {
      const d = s.charCodeAt(i + 1);
      if (d >= 0xdc00 && d <= 0xdfff) i++;
    }
    n++;
  }
  return n;
}

/** Compares strings by code point, as Python does. */
export function compareStrings(a: string, b: string): number {
  if (a === b) return 0;
  const n = Math.min(a.length, b.length);
  for (let i = 0; i < n; i++) {
    const x = a.codePointAt(i)!;
    const y = b.codePointAt(i)!;
    if (x !== y) return x < y ? -1 : 1;
    if (x > 0xffff) i++;
  }
  return a.length < b.length ? -1 : a.length > b.length ? 1 : 0;
}

/** The strings, sorted by code point and without duplicates. */
export function sortedUnique(values: Iterable<string>): string[] {
  return [...new Set(values)].sort(compareStrings);
}
