"""Validate packs, compare source catalogs and report honest source coverage.

Uses Python's standard library. Translation import is explicit; no translation
API, network service, or old dictionary is used.
"""
import argparse
import collections
import json
import re
from pathlib import Path

PROTECTED = re.compile(r"<[^>]*>|\[\[|\]\]|##|\{[^{}]*\}|\((?:[+\-#x%]*x\d+[x%\d]*|rate|br|lb|\*)\)")
NUMBER = re.compile(r"\d+(?:[.,]\d+)?")
RED_MARKERS = re.compile(r"\(\([^()]*\)\)")
NATIVE = re.compile(r"\((?!multiplicative\))(?:[+\-#x0-9%.,]+|[A-Za-z_][A-Za-z_0-9]*)\)")
ACTIVE = {"translated", "reviewed"}


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def write(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def validate(root):
    errors, counts, matches, all_ids = [], collections.Counter(), {}, set()
    for path in sorted((Path(root) / "locales/zh-Hans").rglob("*.json")):
        pack = read(path)
        if pack.get("schemaVersion") != 1 or pack.get("language") != "zh-Hans": errors.append(f"{path.name}: schema/language")
        ids = set()
        for entry in pack.get("entries", []):
            key = entry.get("id", "")
            source, target = entry.get("source", ""), entry.get("translation", "")
            if not key or key in ids: errors.append(f"{key}: missing/duplicate id")
            if key in all_ids: errors.append(f"{key}: id appears in multiple built-in packs")
            ids.add(key); all_ids.add(key)
            counts[entry.get("status", "missing-status")] += 1
            if entry.get("status") not in ACTIVE: continue
            if not source or not target: errors.append(f"{key}: source/translation empty")
            if collections.Counter(PROTECTED.findall(source)) != collections.Counter(PROTECTED.findall(target)):
                errors.append(f"{key}: markup/placeholders changed")
            if len(RED_MARKERS.findall(source)) != len(RED_MARKERS.findall(target)):
                errors.append(f"{key}: red color markers changed")
            if entry.get('context', '').endswith('-template') and collections.Counter(NATIVE.findall(RED_MARKERS.sub('',source))) != collections.Counter(NATIVE.findall(RED_MARKERS.sub('',target))):
                errors.append(f"{key}: native variables changed")
            if source.count("\n") != target.count("\n"): errors.append(f"{key}: newline count changed")
            for token in entry.get("protectedTokens", []):
                if not token or token not in source or source.count(token) != target.count(token): errors.append(f"{key}: native token changed: {token}")
            before, after = PROTECTED.sub("", source), PROTECTED.sub("", target)
            if collections.Counter(NUMBER.findall(before)) != collections.Counter(NUMBER.findall(after)):
                errors.append(f"{key}: literal number changed")
            signature = (pack.get("priority", 0), entry.get("context", "*"), source)
            if signature in matches and matches[signature] != target: errors.append(f"{key}: conflicting visible source translation")
            matches[signature] = target
    return {"statusCounts": dict(counts), "activeMatchingKeys": len(matches), "errors": errors}


def diff(old, new):
    before = {row["id"]: row for row in read(old)["entries"]}
    after = {row["id"]: row for row in read(new)["entries"]}
    added = [after[key] for key in after.keys() - before.keys()]
    removed = [before[key] for key in before.keys() - after.keys()]
    changed = [{"id": key, "before": before[key], "after": after[key]} for key in after.keys() & before.keys() if before[key]["text"] != after[key]["text"]]
    return {"added": sorted(added, key=lambda row: row["id"]), "changed": sorted(changed, key=lambda row: row["id"]), "removed": sorted(removed, key=lambda row: row["id"]),
            "identityNote": "Dialogue_EN keys are stable. Metadata/content-hash and untyped asset identities may appear as removed+added after an update; never automatically reuse an old translation for a changed source."}


def coverage(root, source):
    originals = read(source)["entries"]
    active, identities = set(), set()
    for path in (Path(root) / "locales/zh-Hans").rglob("*.json"):
        for entry in read(path)["entries"]:
            if entry.get("status") in ACTIVE:
                active.add(entry["source"]); identities.add(entry["id"])
    groups = collections.defaultdict(lambda: {"sourceCandidates": set(), "matchedSources": set(), "unmatched": []})
    for row in originals:
        group = groups[row["category"]]
        group["sourceCandidates"].add(row["text"])
        if row["text"] in active: group["matchedSources"].add(row["text"])
        else: group["unmatched"].append({"id": row["id"], "source": row["text"], "confidence": row["confidence"], "origin": row["origin"]})
    summary = {key: {"uniqueCandidates": len(value["sourceCandidates"]), "matchedOriginalSources": len(value["matchedSources"]), "unmatched": value["unmatched"]} for key, value in groups.items()}
    dialogue = [row for row in originals if row["category"] == "dialogue"]
    return {"dialogueIdentities": len(dialogue), "dialogueIdentityTranslations": sum(row["id"] in identities for row in dialogue), "categories": summary,
            "scope": "This measures source-catalog matches, not playable-screen coverage. Candidates include technical names and unused strings. Runtime missing.jsonl and gameplay regression are separate requirements."}


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    check = commands.add_parser("validate"); check.add_argument("root", type=Path)
    compare = commands.add_parser("diff"); compare.add_argument("old"); compare.add_argument("new"); compare.add_argument("--out", required=True)
    cover = commands.add_parser("coverage"); cover.add_argument("root", type=Path); cover.add_argument("source"); cover.add_argument("--out", required=True)
    args = parser.parse_args()
    if args.command == "validate":
        result = validate(args.root)
        print(json.dumps(result, ensure_ascii=True, indent=2))
        return bool(result["errors"])
    if args.command == "diff": result = diff(args.old, args.new)
    else: result = coverage(args.root, args.source)
    write(args.out, result)
    print(f"Saved {args.out}")
    return 0


if __name__ == "__main__": raise SystemExit(main())
