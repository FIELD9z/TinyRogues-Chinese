"""Merge source-only inventories without silently excluding short/mixed/technical text.

Large game/source inventories stay local. Only source keys are imported from legacy dictionaries; their translation
values are never stored in the audit output.
"""
import argparse
import collections
import hashlib
import json
import re
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def prepare(workspace, game, output, supplements):
    repository = workspace / "outputs/TinyRogues-Chinese"
    output.mkdir(parents=True, exist_ok=True)
    rows, inputs, issues, occurrences = {}, [], [], collections.Counter()
    supplement_manifests=[]
    native_rules = collections.defaultdict(list)

    def loaded(path):
        if not path.exists():
            issues.append({"path": str(path), "reason": "missing-source-input"})
            return False
        inputs.append({"path": str(path.resolve()), "sha256": sha(path), "bytes": path.stat().st_size})
        return True

    def add(source, context, known, route, origin, kind="text", flags=()):
        if not isinstance(source, str):
            issues.append({"origin": origin, "reason": "non-string-source", "type": type(source).__name__})
            return
        # Empty and non-Latin imported strings are retained too. Never strip text.
        key = (source, context, known, route, kind)
        occurrences[route] += 1
        if key not in rows:
            record_id = hashlib.sha256(json.dumps(key, ensure_ascii=False).encode("utf-8")).hexdigest()
            plain = re.sub(r"<[^>]*>", "", source)
            extra = []
            if len(plain) < 4: extra.append("short-source-retained")
            if re.search(r"[\u3400-\u9fff]", source): extra.append("mixed-or-chinese-source-retained")
            if len(source) > 12000: extra.append("long-source-retained")
            if not re.search("[A-Za-z]", plain): extra.append("no-visible-latin-retained")
            if re.search(r'\{\d[^}]*\}|\[\[|\bx\d\b|\(\*\)', source): extra.append("native-format-or-description-tokens")
            if re.search(r"Exception|UnityEngine|System\.|Shader|^[A-Za-z0-9_.]+$", source): extra.append("possible-technical-or-identifier-retained")
            rows[key] = dict(id=record_id, source=source, context=context, contextKnown=known,
                route=route, kind=kind, origins=[], flags=list(dict.fromkeys([*flags, *extra])),
                historicalNativeEvidence=[])
        rows[key]["origins"].append(origin)

    # Historical evidence is attached as historical, never promoted to current pass.
    for run in ("independent-full-01", "independent-paths-01"):
        folder = workspace / "work/parity-audit/runs" / run
        paths = [folder / name for name in ("fixtures.json", "results.json", "manifest.json")]
        present = [loaded(p) for p in paths]
        if not all(present): continue
        fixtures, results, manifest = [read(p) for p in paths]
        if sha(paths[0]).lower()!=manifest.get('fixtureSha256','').lower():
            issues.append({'run':run,'reason':'historical-fixture-hash-mismatch'});continue
        identity_valid=True
        for identity in manifest.get('pluginFiles',[]):
            snapshot=folder/'plugins'/identity['path']
            if not snapshot.exists() or sha(snapshot).lower()!=identity['sha256'].lower():
                issues.append({'run':run,'reason':'historical-plugin-hash-mismatch','file':str(snapshot)})
                identity_valid=False
        if not identity_valid: continue
        by_id = {r["id"]: r for r in results}
        for fixture in fixtures:
            result = by_id.get(fixture["id"])
            if result is None:
                issues.append({"run": run, "id": fixture["id"], "reason": "missing-historical-result"})
                continue
            if result.get('source')!=fixture.get('source') or result.get('context')!=fixture.get('context'):
                issues.append({'run':run,'id':fixture['id'],'reason':'historical-result-input-mismatch'});continue
            evidence = dict(run=run, fixtureId=fixture["id"], completed=result.get("completed", False),
                context=fixture.get("context"), nativeKind=fixture.get('nativeKind'),objectName=fixture.get('objectName'),
                sourceSha256=hashlib.sha256(fixture.get('source','').encode('utf-8')).hexdigest(),
                fixtureSha256=manifest.get("fixtureSha256"), historical=True,
                historicalSnapshotHashesVerified=True, currentPackIdentityVerified=False, normalGameplayVerified=False, layoutVerified=False)
            for rule in fixture.get("legacyRuleIds", []): native_rules[rule].append(evidence)
            add(fixture.get("source", ""), fixture.get("context", ""), True, "historical-native-input",
                {"file": str(paths[0]), "id": fixture["id"], "nativeKind": fixture.get("nativeKind"), "origin": fixture.get("origin")},
                flags=["native-getter-may-preprocess-source" ] if fixture.get("nativeKind") else [])
            rows[(fixture.get('source',''),fixture.get('context',''),True,'historical-native-input','text')]['historicalNativeEvidence'].append(evidence)

    for name in ("original-text-catalog.json", "catalog-v2.json", "catalog-v3.json"):
        path = workspace / "work/independent-extraction" / name
        if not loaded(path): continue
        data = read(path)
        for n, entry in enumerate(data["entries"]):
            declared=entry.get("context", "")
            candidate=declared if declared in ("dialogue","description-template","skill-template","display-value","weapon-effect","styled-ui","styled-input") or declared.startswith("ui:") else ""
            add(entry.get("text"), candidate, False, "game-extraction", {"file": str(path), "index": n,
                "id": entry.get("id"), "origin": entry.get("origin", {k:entry.get(k) for k in ("source", "path_id", "field")}),
                "extractedContext": entry.get("context"), "confidence": entry.get("confidence")})
        for error in data.get("errors", []): issues.append({"file": str(path), "reason": "historical-extraction-error", "detail": error})

    for path in supplements:
        if not loaded(path): continue
        with path.open(encoding="utf-8-sig") as stream:
            for n, line in enumerate(stream, 1):
                try: entry = json.loads(line)
                except json.JSONDecodeError as e:
                    issues.append({"file":str(path), "line":n, "reason":"supplement-parse-error", "detail":str(e)})
                    continue
                if not isinstance(entry,dict):
                    issues.append({'file':str(path),'line':n,'reason':'non-object-supplement-row','value':entry});continue
                value=entry.get('source')
                if not isinstance(value,str) or (not value and isinstance(entry.get('text'),str)):
                    value=entry.get('text',value)
                add(value, "", False, "unfiltered-game-extraction",
                    {"file":str(path), "line":n, "detail":{k:v for k,v in entry.items() if k not in ("source", "text", "raw_base64")}})
    for path in sorted({p.parent/'extraction-manifest.json' for p in supplements}):
        if not loaded(path): continue
        extraction=read(path)
        supplement_manifests.append({"path":str(path.resolve()),"summary":extraction.get('summary',{}),"policy":extraction.get('policy',{})})
        for failure in extraction.get('global_failures',[]): issues.append({"manifest":str(path),"reason":"supplement-extraction-failure","detail":failure})
        for source_file,detail in extraction.get('inputs',{}).items():
            for failure in detail.get('failures',[]): issues.append({"file":source_file,"reason":"supplement-file-extraction-failure","detail":failure})

    path = workspace / "work/parity-audit/inventory-manifest.json"
    if loaded(path):
        legacy_manifest = read(path)
        line_audit = []
        for record in legacy_manifest["files"]:
            legacy_path = Path(record["path"])
            if not loaded(legacy_path): continue
            for n, raw in enumerate(legacy_path.read_text(encoding="utf-8-sig").splitlines(), 1):
                line_id = f"{legacy_path.name}:{n}"
                state = {"file":str(legacy_path), "line":n, "id":line_id,
                    "lineSha256":hashlib.sha256(raw.encode("utf-8")).hexdigest()}
                line_audit.append(state)
                if not raw.strip(): state["status"]="blank-accounted-for"; continue
                if raw.startswith("//"): state["status"]="comment-accounted-for"; continue
                # These files escape literal '='. An unmatched '<' is ordinary
                # source text and must not hide the actual dictionary separator.
                separators=[]
                tag_spans=[match.span() for match in re.finditer(r'</?[A-Za-z][^<>]*>',raw)]
                for i, char in enumerate(raw):
                    if char != "=": continue
                    if any(start <= i < end for start,end in tag_spans): continue
                    backslashes=0
                    for previous in reversed(raw[:i]):
                        if previous != "\\": break
                        backslashes+=1
                    if backslashes % 2 == 0: separators.append(i)
                if raw.startswith('r:"'):
                    closing=raw.rfind('"=')
                    separators=[closing+1] if closing>=3 else []
                if len(separators) != 1:
                    state["status"]="unparsed"
                    state["reason"]="ambiguous-or-missing-unescaped-separator"
                    issues.append(state.copy())
                    continue
                raw_source=raw[:separators[0]]
                is_regex=raw_source.startswith('r:"') and raw_source.endswith('"')
                if is_regex:
                    source=raw_source[3:-1].replace('\\=', '=').replace('\\"', '"')
                else:
                    source=raw_source.replace('\\=', '=').replace('\\:', ':').replace('\\n','\n').replace('\\r','\r').replace('\\t','\t')
                state["status"]="parsed-source-only"
                state["sourceSha256"]=hashlib.sha256(source.encode("utf-8")).hexdigest()
                kind="regex-rule" if is_regex else "text"
                context="dialogue" if legacy_path.name=="RuntimeDialogue_zh.txt" else ""
                add(source,context,False,"legacy-source",{"file":legacy_path.name,"line":n,"id":line_id},kind)
                if is_regex: rows[(source,context,False,"legacy-source",kind)]["historicalNativeEvidence"]=native_rules[line_id]
        with (output/"legacy-lines.jsonl").open("w",encoding="utf-8") as stream:
            for state in line_audit: stream.write(json.dumps(state,ensure_ascii=False)+"\n")

    path = game / "BepInEx/plugins/TinyRogues.Chinese/captures/missing.jsonl"
    if loaded(path):
        for n, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
            try: entry = json.loads(line)
            except json.JSONDecodeError as e:
                issues.append({"file":str(path), "line":n, "reason":"capture-parse-error", "detail":str(e)})
                continue
            add(entry.get("source"), entry.get("context", ""), bool(entry.get("context")), "runtime-capture",
                {"file":str(path), "line":n, "timestampUtc":entry.get("timestampUtc")},
                flags=[] if entry.get("context")=="dialogue" else ["may-be-typewriter-prefix-or-intermediate-text"])

    for path in sorted((repository / "tests/fixtures").glob("*.json")):
        loaded(path)
        context = "dialogue" if "tutorial" in path.name else "styled-ui"
        for n, source in enumerate(read(path)): add(source, context, True, "fixture-input", {"file":str(path),"index":n})
    path = workspace / "work/parity-audit/synthetic-rule-fixtures.json"
    if loaded(path):
        for n, entry in enumerate(read(path)):
            add(entry["source"], entry.get("context", ""), False, "synthetic-rule-input",
                {"file":str(path),"index":n,"id":entry.get("id"),"legacyRuleIds":entry.get("legacyRuleIds",[])}, flags=["synthetic-not-observed-gameplay"])
    for path in sorted((repository / "locales/zh-Hans").glob("*.json")):
        loaded(path)
        for n, entry in enumerate(read(path)["entries"]):
            add(entry["source"], entry.get("context", "*"), False, "catalog-specification",
                {"file":str(path),"index":n,"id":entry["id"],"status":entry.get("status"),"note":entry.get("note")},
                "template-specification" if entry.get("kind")=="template" else "text",
                flags=["disabled-translation-needs-review"] if entry.get("status") in ("draft","needs-review","obsolete") else [])

    capture_contexts=collections.defaultdict(dict)
    for row in rows.values():
        if row['route']=='runtime-capture' and row['context']!='dialogue':
            capture_contexts[row['context']][row['source']]=row
    for values in capture_contexts.values():
        ordered=sorted(values)
        for current, following in zip(ordered,ordered[1:]):
            if following.startswith(current): values[current]['flags'].append('strict-prefix-of-another-capture')
        by_plain=collections.defaultdict(list)
        for source,row in values.items(): by_plain[re.sub(r'<[^>]*>','',source)].append(row)
        ordered=sorted(by_plain)
        for current,following in zip(ordered,ordered[1:]):
            if following.startswith(current):
                for row in by_plain[current]: row['flags'].append('visible-prefix-of-another-capture')
    with (output / "inputs.jsonl").open("w", encoding="utf-8", newline="\n") as stream:
        for row in rows.values(): stream.write(json.dumps(row,ensure_ascii=False,separators=(",",":"))+"\n")
    manifest = dict(inputFiles=inputs, issues=issues, supplementManifests=supplement_manifests, occurrencesByRoute=dict(occurrences),
        inputRows=len(rows), sourceOccurrences=sum(occurrences.values()),
        uniqueSourceStrings=len({r["source"] for r in rows.values()}),
        inputSha256=sha(output/"inputs.jsonl"),
        scope="All imported records retained, including short, mixed, technical and empty strings. Source universe is bounded by listed inputs; missing runtime paths remain unresolved. No full-game completion percentage.")
    (output/"input-manifest.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({k:manifest[k] for k in ("inputRows","sourceOccurrences","uniqueSourceStrings","occurrencesByRoute")},ensure_ascii=False))


if __name__ == "__main__":
    p=argparse.ArgumentParser()
    p.add_argument("--workspace",type=Path,required=True)
    p.add_argument("--game-dir",type=Path,required=True)
    p.add_argument("--out",type=Path,required=True)
    p.add_argument("--supplement",type=Path,action="append",default=[])
    a=p.parse_args()
    prepare(a.workspace.resolve(),a.game_dir.resolve(),a.out.resolve(),a.supplement)
