// Grammar symbols and BNF rules (Lark's `lark.grammar`).

import { repr } from './utils.js';

/** The default priority of terminals. */
export const TOKEN_DEFAULT_PRIORITY = 0;

/**
 * A grammar symbol: a terminal or a rule (Lark's `Terminal` and `NonTerminal`). Symbols are equal when their kind
 * and name are equal ({@link Symbol.key}); `filterOut` is not compared.
 */
export class Symbol {
  readonly name: string;
  readonly isTerm: boolean;
  /** For terminals: whether the token is left out of the tree (anonymous strings and `_TERMINALS`). */
  filterOut: boolean;

  constructor(name: string, isTerm: boolean, filterOut = false) {
    this.name = name;
    this.isTerm = isTerm;
    this.filterOut = filterOut;
  }

  /** A terminal. */
  static terminal(name: string, filterOut = false): Symbol {
    return new Symbol(name, true, filterOut);
  }

  /** A rule (non-terminal). */
  static rule(name: string): Symbol {
    return new Symbol(name, false, false);
  }

  /** A string that identifies the symbol: equal symbols have equal keys. */
  get key(): string {
    return (this.isTerm ? 'T:' : 'N:') + this.name;
  }

  equals(other: Symbol): boolean {
    return this.isTerm === other.isTerm && this.name === other.name;
  }

  /** The symbol with its name passed through `f`. */
  renamed(f: (name: string) => string): Symbol {
    return new Symbol(f(this.name), this.isTerm, this.filterOut);
  }

  /** Lark's `repr`: `Terminal('NAME')` or `NonTerminal('name')`. */
  repr(): string {
    return `${this.isTerm ? 'Terminal' : 'NonTerminal'}(${repr(this.name)})`;
  }

  toString(): string {
    return this.name;
  }
}

/** How a rule is built into the tree. */
export interface RuleOptions {
  /** The `!` modifier: keep all tokens. */
  keepAllTokens: boolean;
  /** The `?` modifier: inline the rule when it has a single child. */
  expand1: boolean;
  /** The priority (`rule.N:`). */
  priority?: number;
  /** The template the rule was created from. */
  templateSource?: string;
  /** For each position of the expansion before `[optional]` items were removed: whether it was removed. */
  emptyIndices: boolean[];
}

export function ruleOptions(options: Partial<RuleOptions> = {}): RuleOptions {
  return { keepAllTokens: false, expand1: false, emptyIndices: [], ...options };
}

/**
 * A BNF production: `origin: expansion` (Lark's `Rule`). Rules are equal when their origin and expansion are equal
 * ({@link Rule.key}).
 */
export class Rule {
  readonly origin: Symbol;
  readonly expansion: Symbol[];
  /** The index of the expansion among the expansions of the same rule. */
  readonly order: number;
  /** The alias (`-> name`). */
  readonly alias?: string;
  options: RuleOptions;
  private _key?: string;

  constructor(origin: Symbol, expansion: Symbol[], order = 0, alias?: string, options: RuleOptions = ruleOptions()) {
    this.origin = origin;
    this.expansion = expansion;
    this.order = order;
    this.alias = alias;
    this.options = options;
  }

  /** A string that identifies the rule: equal rules have equal keys. */
  get key(): string {
    if (this._key === undefined) this._key = [this.origin.key, ...this.expansion.map((s) => s.key)].join('\u0000');
    return this._key;
  }

  /** `<origin : a b c>`. */
  toString(): string {
    return `<${this.origin.name} : ${this.expansion.map((s) => s.name).join(' ')}>`;
  }
}
