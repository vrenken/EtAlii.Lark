"""Extracts conformance cases from Lark's own test suite.

Runs the upstream tests (from the Lark 1.3.1 source distribution) with a recording `Lark` class. Every parser the
tests build from a plain grammar string and plain options, and every text they parse with it, becomes a case in
`conformance/sources/upstream.json`. The expected results are not taken from the tests: `generate.py` computes them
by running Lark on each case.

    python conformance/tools/extract_upstream.py path/to/lark-1.3.1

Parsers built with Python objects as options (transformers, callbacks, custom lexers, post-lexers, tree classes)
are skipped, since those can't be described as data.
"""
import json
import os
import sys
import unittest

import lark
from lark import Lark

# Options that can be described as data. Everything else makes the parser unsuitable as a conformance case.
DATA_OPTIONS = {
    'parser', 'lexer', 'start', 'ambiguity', 'maybe_placeholders', 'keep_all_tokens', 'priority',
    'propagate_positions', 'debug', 'strict', 'ordered_sets', 'cache', 'g_regex_flags',
}
# Options that don't change the result, and are left out of the cases.
IGNORED_OPTIONS = {'propagate_positions', 'debug', 'cache', 'ordered_sets'}

cases = {}      # (grammar, options) -> case
order = []


def case_for(grammar, options):
    key = (grammar, json.dumps(options, sort_keys=True))
    case = cases.get(key)
    if case is None:
        case = {'grammar': grammar, 'options': options, 'inputs': []}
        cases[key] = case
        order.append(key)
    return case


def data_options(kwargs):
    options = {}
    for name, value in kwargs.items():
        if name not in DATA_OPTIONS:
            return None
        if name in IGNORED_OPTIONS:
            continue
        if name == 'ambiguity' and value == 'forest':
            return None     # The result is a shared packed parse forest, not a tree.
        if not isinstance(value, (str, bool, int, list, tuple, type(None))):
            return None
        if name == 'g_regex_flags':
            # The re flags as letters, which every port understands.
            letters = ''.join(l for l, f in (('i', 2), ('m', 8), ('s', 16), ('x', 64)) if value & f)
            if value & ~(2 | 8 | 16 | 64):
                return None
            options['regex_flags'] = letters
            continue
        if isinstance(value, (list, tuple)):
            if not all(isinstance(v, str) for v in value):
                return None
            value = list(value)
        options[name] = value
    return options


original_init = Lark.__init__
original_parse = Lark.parse
original_lex = Lark.lex


def recording_init(self, grammar, **kwargs):
    if hasattr(grammar, 'read'):
        grammar = grammar.read()
    options = data_options(kwargs)
    ok = isinstance(grammar, str) and options is not None and '%import .' not in grammar
    self._conformance_case = None
    try:
        original_init(self, grammar, **kwargs)
    except Exception:
        if ok:
            case_for(grammar, options)['grammar_error'] = True
        raise
    if ok:
        self._conformance_case = case_for(grammar, options)


def recording_parse(self, text, start=None, on_error=None):
    case = getattr(self, '_conformance_case', None)
    if case is not None and on_error is None and isinstance(text, str):
        entry = {'input': text}
        if start is not None:
            entry['start'] = start
        if entry not in case['inputs']:
            case['inputs'].append(entry)
    return original_parse(self, text, start, on_error)


def recording_lex(self, text, dont_ignore=False):
    case = getattr(self, '_conformance_case', None)
    if case is not None and isinstance(text, str):
        entry = {'lex': text}
        if dont_ignore:
            entry['dont_ignore'] = True
        if entry not in case['inputs']:
            case['inputs'].append(entry)
    return original_lex(self, text, dont_ignore)


def main(source_dir):
    Lark.__init__ = recording_init
    Lark.parse = recording_parse
    Lark.lex = recording_lex
    sys.path.insert(0, source_dir)
    os.chdir(source_dir)
    import tests.__main__ as upstream  # noqa: imports every upstream test module
    suite = unittest.defaultTestLoader.loadTestsFromModule(upstream)
    unittest.TextTestRunner(stream=open(os.devnull, 'w'), verbosity=0).run(suite)

    result = []
    for key in order:
        case = cases[key]
        if not case['inputs'] and not case.get('grammar_error'):
            continue
        result.append(case)
    target = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'sources', 'upstream.json')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'w', encoding='utf8', newline='\n') as f:
        json.dump({'lark': lark.__version__, 'cases': result}, f, indent=1, ensure_ascii=False)
        f.write('\n')
    print('%d cases, %d inputs' % (len(result), sum(len(c['inputs']) for c in result)))


if __name__ == '__main__':
    main(os.path.abspath(sys.argv[1]))
