// A port of the Lark parsing toolkit to TypeScript. Grammars are written in Lark's grammar syntax, and are
// interpreted exactly as Lark 1.3.1 does: the trees, the token types and positions, and the errors are the same.

export {
  ConfigurationError,
  DedentError,
  getContext,
  GrammarError,
  LarkError,
  LexError,
  MissingVariableError,
  ParseError,
  UnexpectedCharacters,
  UnexpectedEOF,
  UnexpectedInput,
  UnexpectedToken,
  VisitError,
} from './errors.js';
export type { ErrorState } from './errors.js';
export { Rule, Symbol } from './grammar.js';
export type { RuleOptions } from './grammar.js';
export { Indenter } from './indenter.js';
export { Lark } from './lark.js';
export type { Ambiguity, LarkOptions, LexerKind, OnError, ParserKind, PriorityMode, ResolvedOptions } from './lark.js';
export { Pattern, TerminalDef } from './lexer.js';
export type { CustomLexer, PostLex, TokenCallback } from './lexer.js';
export { FileSystemLoader, Grammar, GrammarImportError, MemoryLoader, StandardLibraryLoader } from './loadGrammar.js';
export type { GrammarLoader } from './loadGrammar.js';
export { InteractiveParser, ParserState } from './parsers/lalrParser.js';
export { Token } from './token.js';
export { Meta, Tree } from './tree.js';
export type { Node } from './tree.js';
export { Discard, Transformer, Visitor } from './visitors.js';
