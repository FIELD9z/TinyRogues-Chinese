"""Read original English game files. Never reads a translation mod or modifies assets.

pip install -r tools/requirements.txt
python tools/extract_sources.py --game-dir GAME --out work/catalog.json
"""
import argparse
import hashlib
import json
import re
import struct
import sys
from collections import Counter
from pathlib import Path


def digest(value):
    return hashlib.sha256(value.encode("utf-8")).hexdigest()[:20]


def classify(script, name, text):
    context = (script + " " + name).lower()
    if "trait" in context: return "traits"
    if "skill" in context: return "skills"
    if any(word in context for word in ("weapon", "equipment", "consumable", "pickup", "charm", "potion")): return "items"
    if any(word in context for word in ("stat", "effect", "trigger", "reaction", "condition")): return "mechanics"
    if any(word in context for word in ("textmesh", "menu", "button", "option", "ui.")): return "ui"
    return "unclassified"


def read_sources(game_dir):
    import UnityPy
    base = game_dir / "Tiny Rogues_Data"
    rows, errors = [], []
    asset_paths = sorted(set(base.glob("*.assets")) | {path for path in base.glob("level*") if path.is_file() and re.fullmatch(r"level\d+", path.name)})
    environments = {path.name: UnityPy.load(str(path)) for path in asset_paths}
    maps = {name: {obj.path_id: obj for obj in env.objects} for name, env in environments.items()}
    scripts = {}
    all_dialogue_count = 0
    for asset_name, env in environments.items():
        objects = maps[asset_name]
        for obj in env.objects:
            try:
                if obj.type.name == "TextAsset":
                    data = obj.read()
                    if data.m_Name != "Dialogue_EN": continue
                    value = data.m_Script
                    if isinstance(value, bytes): value = value.decode("utf-8")
                    payload = json.loads(value.lstrip("\ufeff"))
                    for key, text in payload.items():
                        if not isinstance(text, str): continue
                        all_dialogue_count += 1
                        rows.append({"id": "dialogue." + key, "text": text, "category": "dialogue", "context": "dialogue", "confidence": "confirmed-source",
                                     "origin": {"file": asset_name, "pathId": obj.path_id, "asset": "Dialogue_EN", "field": key}})
                    continue
                if obj.type.name != "MonoBehaviour": continue
                raw = obj.get_raw_data()
                if len(raw) < 32: continue
                script_file, script_id = struct.unpack_from("<iq", raw, 16)
                target_name = asset_name
                if script_file:
                    external = obj.assets_file.externals[script_file - 1].path
                    target_name = Path(external.replace("\\", "/")).name
                script_key = (target_name, script_id)
                if script_key not in scripts:
                    target = maps.get(target_name, {}).get(script_id)
                    if target and target.type.name == "MonoScript":
                        script = target.read()
                        scripts[script_key] = ".".join(filter(None, [script.m_Namespace, script.m_ClassName]))
                    else: scripts[script_key] = "unknown"
                script_name = scripts[script_key]
                name_length = struct.unpack_from("<i", raw, 28)[0]
                object_name = raw[32:32 + name_length].decode("utf-8", "replace") if 0 <= name_length <= min(2000, len(raw)-32) else ""
                # Unity serialized strings are length-prefixed and four-byte aligned.
                # Without a type tree, offsets are evidence, not field names.
                ordinal = 0
                for offset in range(28, len(raw) - 4, 4):
                    length = struct.unpack_from("<i", raw, offset)[0]
                    if not 1 <= length <= 10000 or offset + 4 + length > len(raw): continue
                    value = raw[offset + 4:offset + 4 + length]
                    try: text = value.decode("utf-8")
                    except UnicodeDecodeError: continue
                    if not re.search("[A-Za-z]{2,}", text) or any(not c.isprintable() and c not in "\r\n\t" for c in text): continue
                    if "\x00" in text: continue
                    ordinal += 1
                    rows.append({"id": "asset." + digest(f"{asset_name}|{script_name}|{object_name}|{ordinal}|{text}"), "text": text,
                                 "category": classify(script_name, object_name, text), "context": script_name,
                                 "confidence": "serialized-string-candidate",
                                 "origin": {"file": asset_name, "pathId": obj.path_id, "asset": object_name, "script": script_name, "byteOffset": offset}})
            except Exception as error:
                if len(errors) < 100: errors.append(f"{asset_name}:{obj.path_id}:{type(error).__name__}:{error}")
    metadata = base / "il2cpp_data/Metadata/global-metadata.dat"
    raw = metadata.read_bytes()
    magic, version, table_offset, table_size, data_offset, data_size = struct.unpack_from("<6I", raw)
    if magic != 0xFAB11BAF or table_size % 8 or table_offset + table_size > len(raw) or data_offset + data_size > len(raw):
        raise ValueError("Unsupported/corrupt metadata literal table")
    for index, offset in enumerate(range(table_offset, table_offset + table_size, 8)):
        length, start = struct.unpack_from("<II", raw, offset)
        if length > 12000 or start + length > data_size: continue
        text = raw[data_offset + start:data_offset + start + length].decode("utf-8", "replace")
        if not re.search("[A-Za-z]{2,}", text): continue
        rows.append({"id": "literal." + digest(text), "text": text, "context": "metadata", "category": "unclassified",
                     "confidence": "literal-candidate", "origin": {"file": "global-metadata.dat", "index": index}})
    # A repeated literal shares the same identity; retain one provenance.
    rows = list({row["id"]: row for row in rows}.values())
    return {"schemaVersion": 1, "game": "Tiny Rogues", "metadataVersion": version,
            "dialogueSourceCount": all_dialogue_count, "sourcePolicy": "Original game English only; no mod dictionary input",
            "scope": "Dialogue JSON is confirmed. Serialized strings and metadata literals require context/runtime confirmation; this is not a count of translatable UI strings.",
            "counts": dict(Counter(row["category"] for row in rows)), "errors": errors, "entries": rows}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-dir", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--python-lib", type=Path, help="Optional locally installed Python dependency directory")
    args = parser.parse_args()
    if args.python_lib: sys.path.insert(0, str(args.python_lib.resolve()))
    result = read_sources(args.game_dir)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"rows": len(result["entries"]), "dialogue": result["dialogueSourceCount"], "counts": result["counts"], "errors": len(result["errors"])}, ensure_ascii=False))


if __name__ == "__main__": main()
