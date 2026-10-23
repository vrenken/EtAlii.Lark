#!/usr/bin/env node
// The command line: `etalii-lark generate grammar.lark`, which writes `grammar.lark.ts`. See `etalii-lark --help`.

import { generate, GenerateOptions } from './codegen/typescript.js';
import type { LexerKind, ParserKind } from './lark.js';

// The Node APIs the command line uses. The package itself doesn't depend on Node's types.
interface Process {
  argv: string[];
  exitCode?: number;
  getBuiltinModule(id: string): unknown;
  stdout: { write(text: string): void };
  stderr: { write(text: string): void };
}

interface Fs {
  readFileSync(path: string, encoding: 'utf8'): string;
  writeFileSync(path: string, text: string): void;
  existsSync(path: string): boolean;
  watch(path: string, listener: () => void): { close(): void };
}

interface Path {
  basename(path: string): string;
  dirname(path: string): string;
  join(...paths: string[]): string;
  resolve(...paths: string[]): string;
  relative(from: string, to: string): string;
}

interface Timers {
  setTimeout(callback: () => void, ms: number): unknown;
  clearTimeout(timer: unknown): void;
}

const process = (globalThis as unknown as { process: Process }).process;
const timers = globalThis as unknown as Timers;
const fs = process.getBuiltinModule('node:fs') as Fs;
const path = process.getBuiltinModule('node:path') as Path;

const USAGE = `Usage: etalii-lark generate <grammar.lark> [options]

Writes a typed TypeScript module for the grammar: its parser, the names of its rules and terminals, a node class for
each tree with typed getters for its children, and a typed visitor. Import it with
\`import * as Grammar from './grammar.lark.js'\`.

Options:
  --out <file>               Where to write the module. Defaults to the grammar's path with .ts added.
  --parser <parser>          earley (the default), lalr or cyk.
  --lexer <lexer>            auto (the default), basic, contextual, dynamic or dynamic_complete.
  --start <rule>             The start rule, start by default. Repeat it for several start rules.
  --no-maybe-placeholders    Leave out the placeholders of missing [optional] items.
  --keep-all-tokens          Keep the tokens that are filtered out by default.
  --package <name>           The module the generated code imports the package from. Defaults to @etalii/lark.
  --watch                    Generate again whenever the grammar, or a grammar it imports, changes.
  --help                     Show this help.
`;

class UsageError extends Error {}

interface Command {
  grammar: string;
  out: string;
  options: GenerateOptions;
  watch: boolean;
}

function parseArgs(args: string[]): Command | undefined {
  if (args.length === 0 || args.includes('--help') || args.includes('-h')) return undefined;
  if (args[0] !== 'generate') throw new UsageError(`Unknown command '${args[0]}'.`);
  const options: GenerateOptions = {};
  const start: string[] = [];
  let grammar: string | undefined;
  let out: string | undefined;
  let watch = false;
  for (let i = 1; i < args.length; i++) {
    const arg = args[i]!;
    const value = (): string => {
      const v = args[++i];
      if (v === undefined) throw new UsageError(`${arg} needs a value.`);
      return v;
    };
    switch (arg) {
      case '--out':
        out = value();
        break;
      case '--parser': {
        const v = value();
        if (!['earley', 'lalr', 'cyk'].includes(v)) throw new UsageError(`Unknown parser '${v}'.`);
        options.parser = v as ParserKind;
        break;
      }
      case '--lexer': {
        const v = value();
        if (!['auto', 'basic', 'contextual', 'dynamic', 'dynamic_complete'].includes(v)) throw new UsageError(`Unknown lexer '${v}'.`);
        options.lexer = v as Exclude<LexerKind, object>;
        break;
      }
      case '--start':
        start.push(value());
        break;
      case '--no-maybe-placeholders':
        options.maybePlaceholders = false;
        break;
      case '--keep-all-tokens':
        options.keepAllTokens = true;
        break;
      case '--package':
        options.packageName = value();
        break;
      case '--watch':
        watch = true;
        break;
      default:
        if (arg.startsWith('-')) throw new UsageError(`Unknown option '${arg}'.`);
        if (grammar !== undefined) throw new UsageError(`Only one grammar can be given, not '${grammar}' and '${arg}'.`);
        grammar = arg;
    }
  }
  if (grammar === undefined) throw new UsageError('No grammar given.');
  if (start.length > 0) options.start = start;
  return { grammar, out: out ?? `${grammar}.ts`, options, watch };
}

/** Generates the module, and returns the files it was generated from. */
function run(command: Command): string[] {
  const file = path.resolve(command.grammar);
  const directory = path.dirname(file);
  const files = [file];
  const code = generate(fs.readFileSync(file, 'utf8'), {
    ...command.options,
    sourceName: path.basename(file),
    readImport: (relative) => {
      const imported = path.join(directory, relative);
      if (!fs.existsSync(imported)) return undefined;
      files.push(imported);
      return fs.readFileSync(imported, 'utf8');
    },
  });
  fs.writeFileSync(command.out, code);
  process.stdout.write(`Generated ${command.out}\n`);
  return files;
}

function watch(command: Command): void {
  let watchers: { close(): void }[] = [];
  let timer: unknown;
  const again = (): void => {
    for (const w of watchers) w.close();
    let files = [path.resolve(command.grammar)];
    try {
      files = run(command);
    } catch (e) {
      process.stderr.write(`${e instanceof Error ? e.message : String(e)}\n`);
    }
    // Editors often replace files, which ends a watch, so the files are watched anew after each change.
    watchers = files.map((f) =>
      fs.watch(f, () => {
        timers.clearTimeout(timer);
        timer = timers.setTimeout(again, 50);
      }),
    );
  };
  again();
}

function main(args: string[]): void {
  let command: Command | undefined;
  try {
    command = parseArgs(args);
  } catch (e) {
    if (!(e instanceof UsageError)) throw e;
    process.stderr.write(`${e.message}\n\n${USAGE}`);
    process.exitCode = 2;
    return;
  }
  if (command === undefined) {
    process.stdout.write(USAGE);
    return;
  }
  if (command.watch) {
    watch(command);
    return;
  }
  try {
    run(command);
  } catch (e) {
    process.stderr.write(`${e instanceof Error ? `${e.name}: ${e.message}` : String(e)}\n`);
    process.exitCode = 1;
  }
}

main(process.argv.slice(2));
