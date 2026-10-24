#!/usr/bin/env python3
"""Strips comments and blank lines from the C#, shader and style sheet files under a folder, in place.

The release workflow runs it on the staged copy, so the downloads carry only the code. It stops with an error on
anything it cannot strip safely (verbatim strings, block comments in C#, // inside a shader string), so a release
never ships broken code.

Usage: python3 strip_comments.py <folder>
"""
import io
import os
import re
import sys


def cs_code_end(line):
    """Where the code of one C# line ends: the first // outside a string or char literal. Strings, interpolated
    strings (with nested strings in their holes) and chars are followed; none of them may span lines."""
    stack, depth, i, n = ['code'], [], 0, len(line)
    while i < n:
        mode, c = stack[-1], line[i]
        if mode == 'code':
            if line.startswith('//', i):
                return i
            if line.startswith('$"', i):
                stack.append('interp')
                i += 2
                continue
            if c == '"':
                stack.append('string')
            elif c == "'":
                stack.append('char')
            elif c == '{' and depth:
                depth[-1] += 1
            elif c == '}' and depth:
                depth[-1] -= 1
                if depth[-1] == 0:
                    depth.pop()
                    stack.pop()
            i += 1
        elif mode in ('string', 'char'):
            if c == '\\':
                i += 2
                continue
            if c == ('"' if mode == 'string' else "'"):
                stack.pop()
            i += 1
        else:
            if c == '\\':
                i += 2
                continue
            if line.startswith('{{', i) or line.startswith('}}', i):
                i += 2
                continue
            if c == '{':
                stack.append('code')
                depth.append(1)
            elif c == '"':
                stack.pop()
            i += 1
    if len(stack) != 1:
        raise SystemExit('unfinished literal in: ' + line.strip()[:100])
    return n


def strip_cs(text, path):
    if re.search(r'@"|\$@|@\$|/\*', text):
        raise SystemExit(path + ': verbatim strings or block comments need a fuller stripper')
    lines = [l[:cs_code_end(l)].rstrip() for l in text.split('\n')]
    return '\n'.join(l for l in lines if l.strip()) + '\n'


def strip_shader(text, path):
    if '/*' in text:
        raise SystemExit(path + ': block comments need a fuller stripper')
    out = []
    for line in text.split('\n'):
        cut = line.find('//')
        if cut >= 0:
            if line[:cut].count('"') % 2:
                raise SystemExit(path + ': // inside a string: ' + line.strip()[:100])
            line = line[:cut]
        if line.strip():
            out.append(line.rstrip())
    return '\n'.join(out) + '\n'


def strip_css(text, path):
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    return '\n'.join(l.rstrip() for l in text.split('\n') if l.strip()) + '\n'


STRIPPERS = {'.cs': strip_cs, '.shader': strip_shader, '.cginc': strip_shader, '.hlsl': strip_shader, '.uss': strip_css}


def main():
    if len(sys.argv) != 2 or not os.path.isdir(sys.argv[1]):
        raise SystemExit(__doc__)
    count = 0
    for dirpath, _, names in os.walk(sys.argv[1]):
        for name in names:
            strip = STRIPPERS.get(os.path.splitext(name)[1])
            if strip is None:
                continue
            path = os.path.join(dirpath, name)
            with io.open(path, encoding='utf-8-sig') as f:
                text = f.read().replace('\r\n', '\n')
            with io.open(path, 'w', encoding='utf-8', newline='\n') as f:
                f.write(strip(text, path))
            count += 1
    print('stripped %d files' % count)


if __name__ == '__main__':
    main()
