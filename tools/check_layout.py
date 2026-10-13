"""Check native layout-probe JSONL output, not translation coverage."""
import argparse
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--corpus', type=Path)
parser.add_argument('--choices', type=Path)
args = parser.parse_args()
if not (args.corpus or args.choices):
    parser.error('provide --corpus and/or --choices')
if args.corpus:
    rows = [json.loads(line) for line in args.corpus.read_text(encoding='utf-8').splitlines()]
    assert rows, 'Empty corpus'
    for row in rows:
        assert len(row['labels']) == 2, row['name']
        assert 0 <= row['paragraphGap'] < 999, row['name']
        for label in row['labels']:
            assert label['measuredHeight'] <= label['height'] + .5, row['name']
    print(f"PASS: {len(rows)} native two-paragraph layouts; minimum gap {min(r['paragraphGap'] for r in rows):.3f}")
if args.choices:
    rows = [json.loads(line) for line in args.choices.read_text(encoding='utf-8').splitlines()]
    for phase in range(9):
        choices = [r for r in rows if r['phase'] == phase and r['role'] in ('choiceA', 'choiceB')]
        assert len(choices) == 2, f'Missing phase {phase}'
        if 1 <= phase <= 4:
            assert all(r['lineCount'] == 1 and not r['wrap'] for r in choices), phase
        if phase in (0, 7):
            assert all(r['wrap'] for r in choices), f'Native wrapping not restored: {phase}'
        if phase == 8:
            assert next(r for r in choices if r['role'] == 'choiceA')['wrap'], 'Multiline choice wrapping not restored'
    print('PASS: short Chinese choices and restored English/multiline wrapping')
