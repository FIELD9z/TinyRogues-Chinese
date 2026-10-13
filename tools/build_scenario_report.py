"""Build an evidence index from this investigation's native captures."""
import argparse
import hashlib
import json
import re
from pathlib import Path
from check_related_layout import inspect


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def rows(path):
    return [json.loads(s) for s in path.read_text(encoding="utf-8-sig").splitlines()]


def tokens(text):
    return (re.findall(r"\d+(?:[.,]\d+)?", re.sub(r"<[^>]*>", "", text)),
            re.findall(r"<sprite[^>]*>", text))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    work = args.work_root
    manifest = read(work / "related-final-run-manifest.json")
    assert len(manifest) == 6, "Incomplete final native runs"
    scenarios = {}
    for run in manifest:
        path = work / run["file"]
        assert sha(path).upper() == run["captureSha256"]
        family, _, font, _ = path.stem.split("-")
        check = inspect(path, 154 if family == "panel" else 191)
        assert not check["failures"], check["failures"]
        assert not check["interControlCandidates"], check["interControlCandidates"]
        previous = work / f"{family}-layout-{font.lower()}-before.jsonl"
        baseline = {r["scenario"]: r for r in rows(previous)} if previous.exists() else {}
        for row in rows(path):
            identity = row["scenario"]
            labels = row["labels"]
            assert not any(t.get("backwardAdvances", 0) for t in labels), identity
            cjk = [t for t in labels if re.search(r"[\u4e00-\u9fff]", t["text"])]
            intentional = identity.startswith("reuse:english")
            latin = [t["path"] for t in labels if re.search(r"[A-Za-z]{3,}",
                     re.sub(r"\[Shift\]|\[Select\]|Tiny Rogues", "",
                            re.sub(r"<[^>]*>", "", t["text"])))]
            assert intentional or not latin, (identity, latin)
            old = baseline.get(identity)
            invariant = "not-compared"
            if old:
                assert [t["path"] for t in labels] == [t["path"] for t in old["labels"]], identity
                assert [tokens(t["text"]) for t in labels] == [tokens(t["text"]) for t in old["labels"]], identity
                invariant = "passed-against-before-capture"
            entry = scenarios.setdefault(identity, {
                "id": identity,
                "input": "Native resource/state or injected fixture identified by id; see layout-probe/README.md",
                "controlPaths": [t["path"] for t in labels],
                "interaction": "selection-display-only; callbacks not invoked" if identity.startswith("dialogue-") else "not-exercised",
                "resolutionScope": "Recorded input, control, font, build and resolution only",
                "observations": [],
            })
            entry["observations"].append({
                "run": run["file"], "fontSetting": font,
                "fontAssets": sorted({t["font"] for t in labels}),
                "screen": [labels[0]["screenWidth"], labels[0]["screenHeight"]],
                "rowSha256": hashlib.sha256(json.dumps(row, ensure_ascii=False, sort_keys=True).encode()).hexdigest(),
                "translation": "intentional-English-reuse" if intentional else "no-unexpected-Latin-in-recorded-output",
                "valuesAndIcons": invariant,
                "layout": "passed",
                "minimumCjkGlyphQuadGap": min((t["glyphGap"] for t in cjk if t["lines"] > 1), default=None),
                "missingCjk": sum(t["missingCjk"] for t in labels),
                "backwardAdvances": 0,
            })
    corpus_path = work / "corpus-layout-related-final.jsonl"
    corpus = rows(corpus_path)
    assert len(corpus) == 2150
    assert all(0 <= r["paragraphGap"] < 999 and all(t["measuredHeight"] <= t["height"] + .5 for t in r["labels"]) for r in corpus)
    log_path = work / "related-final-native-selftest.log"
    log = log_path.read_text(encoding="utf-8-sig")
    assert "INDEPENDENT SELFTEST PASS" in log and "INDEPENDENT SELFTEST FAIL" not in log
    report = {
        "schemaVersion": 1, "date": "2026-10-13", "game": "0.2.8.6", "unity": "2022.3.62f2",
        "baseCommit": "30487bf", "pluginSha256": manifest[0]["pluginSha256"],
        "policy": "A prior record exempts only its exact still-applicable scenario. Shared-code, pack or environment changes require reassessment. Unknown branches stay pending.",
        "coreChecks": 2619, "activeEntries": 8654, "packs": 47,
        "runs": manifest,
        "scenarios": list(scenarios.values()),
        "paragraphCorpus": {"count": len(corpus), "sha256": sha(corpus_path), "minimumParagraphGap": min(r["paragraphGap"] for r in corpus), "status": "passed", "scope": "Injected original/generated text plus second paragraph; not full owner coverage"},
        "nativeSelftest": {"status": "passed", "logSha256": sha(log_path)},
        "supplementary": [
            {"family": "settings", "status": "checked-with-pending-visual-boundaries", "scope": "Native navigation of six tabs; 219 geometry records under Options Menu Canvas", "pending": "Dropdown label line boxes can exceed their Rect; active/alpha does not establish viewport clipping. No unsupported layout change made."},
            {"family": "character-panel", "status": "checked", "scope": "Four native pages in isolated initial run; 281 active geometry records; not every character/stat value combination"},
            {"family": "title-menu", "status": "pending", "reason": "Title-before-options snapshot did not contain visible title-control geometry; do not infer layout from inactive prefab labels."},
            {"family": "player-dependent-tooltips", "status": "pending", "reason": "Historical BlackMarketAlignment, Exp and Power tooltip variants need an initialized player; resource inventory exceptions were not counted as passes."},
            {"family": "all-gameplay", "status": "pending", "reason": "No claim to all runs, window sizes, scroll/mask states or interaction callbacks."},
        ],
        "evidenceLocation": "Raw native captures/logs/glyph images remain in the local work directory; source probes, repeatable fixtures and checks are committed.",
    }
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Recorded {len(scenarios)} scenarios / {sum(len(s['observations']) for s in scenarios.values())} observations")


if __name__ == "__main__":
    main()
