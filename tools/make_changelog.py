#!/usr/bin/env python3
"""Generate changelog/history.md from the build files changelog/YYYYMMDD-HHMM-sha7.md.

The manager has no version numbers, only nightly builds (nightly-YYYYMMDD-sha7), so each file is the entry of one build:
the name carries the date and time of its commit (for the order) and the 7-character commit; the file starts with
`## YYYY-MM-DD (sha7)` and holds short player-facing bullets. history.md is the files, newest first, separated by one blank line.
The manager reads it from the repository to show "What's new" after an update (the sha7 in the heading is the commit of the build stamp).
With --check nothing is written; the exit code is 1 when history.md is missing or out of date.
"""
import argparse, os, re, sys

NAME = re.compile(r'^(\d{8}-\d{4})-([0-9a-f]{7})\.md$')


def build(folder):
    files = sorted((n for n in os.listdir(folder) if NAME.match(n)), reverse=True)
    parts = []
    for n in files:
        with open(os.path.join(folder, n), encoding='utf-8-sig') as fh:
            text = fh.read().replace('\r\n', '\n').strip('\n')
        sha = NAME.match(n).group(2)
        if not re.match(r'^## \d{4}-\d\d-\d\d \(%s\)$' % sha, text.split('\n')[0]):
            sys.exit('%s: the first line must be "## YYYY-MM-DD (%s)"' % (n, sha))
        parts.append(text)
    return '\n\n'.join(parts) + '\n' if parts else None


def main():
    a = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    a.add_argument('--dir', default='changelog')
    a.add_argument('--check', action='store_true')
    o = a.parse_args()
    text = build(o.dir)
    if text is None:
        print('no changelog files in %s' % o.dir)
        return 0
    target = os.path.join(o.dir, 'history.md')
    have = open(target, encoding='utf-8', newline='').read().replace('\r\n', '\n') if os.path.exists(target) else None
    if have == text:
        print('history.md is up to date')
        return 0
    if o.check:
        print('%s is out of date: run python tools/make_changelog.py' % target, file=sys.stderr)
        return 1
    with open(target, 'w', encoding='utf-8', newline='\n') as fh:
        fh.write(text)
    print('history.md written')
    return 0


if __name__ == '__main__':
    sys.exit(main())
