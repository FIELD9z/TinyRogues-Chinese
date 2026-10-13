"""Check native related-layout captures without equating them to gameplay coverage."""
import argparse
import itertools
import json
import re
from pathlib import Path


def inspect(path, expected):
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines()]
    failures = []
    candidates = []
    if len(rows) != expected:
        failures.append(f"expected {expected} scenarios, got {len(rows)}")
    if len({row["scenario"] for row in rows}) != len(rows):
        failures.append("duplicate scenario identities")
    labels = 0
    for row in rows:
        name = row["scenario"]
        if "error" in row:
            failures.append(name + ": " + row["error"])
            continue
        if not row.get("labels"):
            failures.append(name + ": no labels captured")
        for label in row.get("labels", []):
            labels += 1
            prefix = name + " / " + label["path"]
            if "missingCjk" not in label or "glyphGap" not in label:
                failures.append(prefix + ": incomplete geometry schema")
                continue
            if label["missingCjk"]:
                failures.append(prefix + ": missing CJK glyph")
            if label["measuredHeight"] > label["height"] + 0.1:
                failures.append(prefix + ": generated line height exceeds container")
            if re.search(r"[\u4e00-\u9fff]", label["text"]) and label["glyphGap"] < -0.05:
                failures.append(prefix + ": adjacent line glyph quads overlap vertically")
        # Conservative candidate only: whole-label AABBs are not pixel masks.
        for left, right in itertools.combinations(row.get("labels", []), 2):
            a, b = left["glyphBounds"], right["glyphBounds"]
            dx = min(a[2], b[2]) - max(a[0], b[0])
            dy = min(a[3], b[3]) - max(a[1], b[1])
            if dx > 0.02 and dy > 0.02:
                candidates.append({"scenario": name, "paths": [left["path"], right["path"]], "overlap": [dx, dy]})
    return {"file": str(path), "scenarios": len(rows), "labels": labels,
            "failures": failures, "interControlCandidates": candidates}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", nargs="+", type=Path)
    parser.add_argument("--expected", type=int, required=True)
    args = parser.parse_args()
    results = [inspect(path, args.expected) for path in args.captures]
    print(json.dumps(results, ensure_ascii=False, indent=2))
    raise SystemExit(1 if any(result["failures"] for result in results) else 0)
