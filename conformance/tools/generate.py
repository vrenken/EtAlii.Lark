"""Computes the expected results of the conformance cases with Python Lark.

Reads every `conformance/sources/*.json` file, runs each case with Lark, and writes the case together with its
expected results to `conformance/cases/<same name>.json`. The expected results are what Lark itself produces, so a
port that passes the cases interprets the grammars the same way Lark does.

    pip install lark==1.3.1
    python conformance/tools/generate.py
"""
import glob
import json
import os
import sys

import lark
from lark import Lark, Tree, Token
from lark import exceptions

from shapes import ShapeAnalyzer, check_tree

LARK_VERSION = '1.3.1'
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')


def lark_error_name(e):
    for cls in type(e).__mro__:
        if cls.__module__ == exceptions.__name__:
            return cls.__name__
    return None


def token_json(token):
    return {
        'type': token.type,
        'value': token.value,
        'pos': [token.start_pos, token.end_pos, token.line, token.column, token.end_line, token.end_column],
    }


def tree_json(node):
    if isinstance(node, Tree):
        result = {'data': str(node.data), 'children': [tree_json(c) for c in node.children]}
        meta = node.meta
        if not meta.empty:
            result['meta'] = [meta.start_pos, meta.end_pos, meta.line, meta.column, meta.end_line, meta.end_column]
        return result
    if isinstance(node, Token):
        return token_json(node)
    if node is None:
        return None
    raise TypeError('Unexpected node in tree: %r' % (node,))


def error_json(e):
    name = lark_error_name(e)
    if name is None:
        return None
    result = {'error': name}
    if isinstance(e, exceptions.UnexpectedInput):
        result['line'] = e.line
        result['column'] = e.column
    if isinstance(e, exceptions.UnexpectedToken):
        if isinstance(e.token, Token):
            result['token'] = {'type': e.token.type, 'value': e.token.value}
        result['expected'] = sorted(set(e.expected))
    if isinstance(e, exceptions.UnexpectedEOF):
        result['expected'] = sorted(set(e.expected))
    if isinstance(e, exceptions.UnexpectedCharacters):
        result['char'] = e.char
        if e.allowed is not None:
            result['expected'] = sorted(set(e.allowed))
    return result


def lark_options(options):
    # Positions are always propagated: it doesn't change the trees, and the positions are compared too.
    kwargs = dict(options, propagate_positions=True)
    postlex = kwargs.pop('postlex', None)
    if postlex == 'python_indenter':
        from lark.indenter import PythonIndenter
        kwargs['postlex'] = PythonIndenter()
    elif postlex is not None:
        raise ValueError('Unknown post-lexer %r' % postlex)
    flags = kwargs.pop('regex_flags', None)
    if flags:
        import re
        value = 0
        for letter in flags:
            value |= {'i': re.I, 'm': re.M, 's': re.S, 'x': re.X}[letter]
        kwargs['g_regex_flags'] = value
    return kwargs


def grammar_text(case):
    if 'grammar' in case:
        return case['grammar']
    name = case['grammar_file']
    if name.startswith('lark:'):
        path = os.path.join(os.path.dirname(lark.__file__), 'grammars', name[len('lark:'):])
    else:
        path = os.path.join(ROOT, name)
    with open(path, encoding='utf8') as f:
        return f.read()


def run_case(case):
    # The grammar is always inlined, so that the runners don't need to resolve files.
    result = {'grammar': grammar_text(case)}
    result.update((k, v) for k, v in case.items() if k not in ('inputs', 'grammar'))
    try:
        parser = Lark(result['grammar'], **lark_options(case['options']))
    except Exception as e:
        name = lark_error_name(e)
        if name is None:
            return None
        result.pop('grammar_error', None)
        result['grammar_error'] = name
        return result
    result.pop('grammar_error', None)
    shapes = ShapeAnalyzer(parser.rules, parser.options.maybe_placeholders).shapes_json()

    tests = []
    for entry in case['inputs']:
        test = dict(entry)
        try:
            if 'lex' in entry:
                test['expect'] = {'tokens': [token_json(t) for t in parser.lex(entry['lex'], entry.get('dont_ignore', False))]}
            else:
                test['expect'] = {'tree': tree_json(parser.parse(entry['input'], entry.get('start')))}
        except Exception as e:
            error = error_json(e)
            if error is None:
                continue        # Not a Lark error (a Python specific failure); not part of the suite.
            test['expect'] = error
        tests.append(test)
    if not tests:
        return None
    result['tests'] = tests
    result['shapes'] = shapes
    # The shapes must describe the trees Lark really builds (explicit ambiguity adds _ambig trees, which they don't).
    if case['options'].get('ambiguity') != 'explicit':
        problems = []
        for test in tests:
            check_tree(shapes, test['expect'].get('tree'), problems)
        if problems:
            raise AssertionError('The shapes of %r don\'t match its trees:\n%s' % (result['grammar'], '\n'.join(problems)))
    return result


def main():
    if lark.__version__ != LARK_VERSION:
        sys.exit('The conformance suite is generated with Lark %s, found %s.' % (LARK_VERSION, lark.__version__))
    for source in sorted(glob.glob(os.path.join(ROOT, 'sources', '*.json'))):
        with open(source, encoding='utf8') as f:
            data = json.load(f)
        results = []
        for index, case in enumerate(data['cases']):
            result = run_case(case)
            if result is not None:
                result = {'id': '%s-%04d' % (os.path.splitext(os.path.basename(source))[0], index), **result}
                results.append(result)
        target = os.path.join(ROOT, 'cases', os.path.basename(source))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, 'w', encoding='utf8', newline='\n') as f:
            # One case per line keeps the file small and its diffs readable.
            f.write('{"lark": %s, "cases": [\n' % json.dumps(LARK_VERSION))
            f.write(',\n'.join(json.dumps(r, ensure_ascii=False, separators=(',', ':')) for r in results))
            f.write('\n]}\n')
        print('%s: %d cases, %d tests' % (os.path.basename(source), len(results), sum(len(c.get('tests', [])) for c in results)))


if __name__ == '__main__':
    main()
