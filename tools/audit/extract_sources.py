"""Read-only source inventory for Tiny Rogues 0.2.8.6.

This pass deliberately keeps a lossless record for every IL2CPP string-literal
record and for every plausible Unity length-prefixed UTF-8 string.  It is an
inventory of evidence, not a claim that every candidate is player-facing text.
The manifest records files and objects that could not be decoded.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import struct
import sys
import traceback
from collections import Counter
from pathlib import Path

ASCII = re.compile(rb"[A-Za-z]")
PRINTABLE = set("\t\n\r")


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def write_jsonl(path: Path, rows: list[dict]) -> None:
    with path.open("w", encoding="utf-8", newline="\n") as f:
        for row in rows:
            f.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")))
            f.write("\n")


def decoded(raw: bytes) -> tuple[str, int]:
    text = raw.decode("utf-8", "replace")
    return text, text.count("\ufffd")


def plausible(raw: bytes) -> bool:
    """Conservative validity check, without trimming or normalising content."""
    if not raw:
        return False
    try:
        text = raw.decode("utf-8", "strict")
    except UnicodeDecodeError:
        return False
    if not ASCII.search(raw):
        return False
    return all(ch.isprintable() or ch in PRINTABLE for ch in text)


def walk_strings(value, path="root", seen=None):
    if seen is None:
        seen = set()
    if isinstance(value, str):
        yield path, value
        return
    if value is None or isinstance(value, (bytes, bytearray, int, float, bool)):
        return
    oid = id(value)
    if oid in seen:
        return
    seen.add(oid)
    if isinstance(value, dict):
        for key, item in value.items():
            yield from walk_strings(item, f"{path}.{key}", seen)
    elif isinstance(value, (list, tuple)):
        for i, item in enumerate(value):
            yield from walk_strings(item, f"{path}[{i}]", seen)
    elif hasattr(value, "__dict__"):
        for key, item in vars(value).items():
            if not key.startswith("_"):
                yield from walk_strings(item, f"{path}.{key}", seen)


def metadata_scan(path: Path, rows: list[dict], manifest: dict) -> None:
    raw = path.read_bytes()
    item = manifest["inputs"].setdefault(str(path), {})
    item.update({"kind": "il2cpp_global_metadata", "size": len(raw), "sha256": sha256(path)})
    failures = item.setdefault("failures", [])
    if len(raw) < 24 or struct.unpack_from("<I", raw, 0)[0] != 0xFAB11BAF:
        failures.append({"reason": "invalid_global_metadata_header"})
        return
    version, lit_off, lit_bytes, data_off, data_bytes = struct.unpack_from("<IIIII", raw, 4)
    item.update({"metadata_version": version, "literal_table_offset": lit_off,
                 "literal_table_bytes": lit_bytes, "literal_data_offset": data_off,
                 "literal_data_bytes": data_bytes, "record_size": 8})
    records = lit_bytes // 8
    item["literal_records_declared"] = records
    if lit_bytes % 8:
        failures.append({"reason": "literal_table_not_multiple_of_8", "bytes": lit_bytes})
    for index in range(records):
        pos = lit_off + index * 8
        rowbase = {"source_kind": "il2cpp_string_literal", "source_file": str(path),
                   "record_index": index, "record_offset": pos}
        if pos + 8 > len(raw):
            failures.append({"reason": "literal_record_out_of_file", "record_index": index,
                             "record_offset": pos})
            continue
        length, start = struct.unpack_from("<II", raw, pos)
        end = data_off + start + length
        rowbase.update({"length_bytes": length, "data_start": data_off + start,
                        "data_end": end})
        if length > 16 * 1024 * 1024:
            failures.append({"reason": "literal_length_implausible", **rowbase})
            continue
        if data_off + start < 0 or end > len(raw):
            failures.append({"reason": "literal_data_out_of_file", **rowbase})
            continue
        value = raw[data_off + start:end]
        text, replacements = decoded(value)
        has_ascii = bool(ASCII.search(value))
        # Every valid record is emitted, including non-ASCII and empty records.
        # raw_base64 is the lossless source of truth; text is never stripped.
        rows.append({**rowbase, "selected_for_ascii_inventory": has_ascii,
                     "text": text, "raw_base64": base64.b64encode(value).decode("ascii"),
                     "utf8_replacement_count": replacements,
                     "contains_ascii_letter": has_ascii,
                     "confidence": "exact_length_prefixed_record"})
    item["literal_records_emitted"] = sum(1 for r in rows if r["source_file"] == str(path))
    item["literal_records_with_ascii"] = sum(1 for r in rows if r["source_file"] == str(path)
                                               and r["contains_ascii_letter"])


def unity_scan(base: Path, rows: list[dict], manifest: dict) -> None:
    try:
        import UnityPy  # type: ignore
    except Exception as exc:
        manifest["global_failures"].append({"reason": "unitypy_import_failed", "error": repr(exc)})
        return
    names = sorted({p.name for p in base.iterdir() if p.is_file() and
                    (p.suffix == '.assets' or p.name == 'globalgamemanagers' or re.fullmatch(r'level\d+', p.name))}
                   | {'globalgamemanagers', 'resources.assets'})
    for name in names:
        path = base / name
        item = manifest["inputs"].setdefault(str(path), {})
        item.update({"kind": "unity_serialized_file", "size": path.stat().st_size if path.exists() else None,
                     "sha256": sha256(path) if path.exists() else None})
        if not path.exists():
            item.setdefault("failures", []).append({"reason": "missing_input_file"})
            continue
        try:
            env = UnityPy.load(str(path))
            objects = list(env.objects)
            item["unitypy_status"] = "parsed"
            item["object_count"] = len(objects)
            item["object_types"] = dict(Counter(o.type.name for o in objects))
        except Exception as exc:
            item.setdefault("failures", []).append({"reason": "unitypy_file_load_failed",
                                                      "error": repr(exc), "traceback": traceback.format_exc(limit=2)})
            continue
        for obj in objects:
            objbase = {"source_kind": "unity_object", "source_file": str(path),
                       "path_id": int(obj.path_id), "unity_type": obj.type.name,
                       "object_byte_start": int(getattr(obj, "byte_start", -1)),
                       "object_byte_size": int(getattr(obj, "byte_size", -1))}
            try:
                raw = obj.get_raw_data()
            except Exception as exc:
                item.setdefault("failures", []).append({"reason": "object_raw_read_failed", **objbase,
                                                         "error": repr(exc)})
                continue
            item["raw_objects_read"] = item.get("raw_objects_read", 0) + 1
            # Unity serialized strings use a signed little-endian byte length.
            # The preceding field can leave a string length at any byte offset
            # (the serialized format does not promise 4-byte alignment here).
            for offset in range(0, max(0, len(raw) - 3)):
                length = struct.unpack_from("<I", raw, offset)[0]
                end = offset + 4 + length
                if length == 0 or length > min(4 * 1024 * 1024, len(raw) - offset - 4) or end > len(raw):
                    continue
                value = raw[offset + 4:end]
                if not plausible(value):
                    continue
                text, replacements = decoded(value)
                rows.append({**objbase, "record_offset": offset, "length_bytes": length,
                             "data_start": offset + 4, "data_end": end,
                             "text": text, "raw_base64": base64.b64encode(value).decode("ascii"),
                             "utf8_replacement_count": replacements,
                             "contains_ascii_letter": True,
                             "confidence": "length_prefix_candidate"})
            # A parsed object gives a useful second source view. Type-tree/Mono
            # behaviours may fail; those failures remain in the manifest.
            try:
                parsed = obj.read()
                for field_path, value in walk_strings(parsed):
                    if not ASCII.search(value.encode("utf-8", "replace")):
                        continue
                    rows.append({**objbase, "field_path": field_path, "text": value,
                                 "raw_base64": base64.b64encode(value.encode("utf-8")).decode("ascii"),
                                 "contains_ascii_letter": True, "confidence": "parsed_unity_field"})
            except Exception as exc:
                item.setdefault("failures", []).append({"reason": "object_typed_read_failed", **objbase,
                                                         "error": repr(exc)})
        item["candidate_rows_emitted"] = sum(1 for r in rows if r.get("source_file") == str(path))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("game_data", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    game_data = args.game_data
    out = args.output
    out.mkdir(parents=True, exist_ok=True)
    rows: list[dict] = []
    manifest = {"schema": "tiny-rogues-source-extraction-manifest-v1", "game_data": str(game_data),
                "inputs": {}, "global_failures": [],
                "policy": {"no_trim": True, "no_line_deletion": True,
                           "raw_base64_lossless": True,
                           "ascii_inventory": "any ASCII A-Z/a-z, including short, mixed, technical and markup strings",
                           "unity_scan": "best-effort UnityPy object and 4-byte little-endian length-prefix scan"}}
    metadata_rows: list[dict] = []
    metadata_scan(game_data / "il2cpp_data" / "Metadata" / "global-metadata.dat", metadata_rows, manifest)
    unity_rows: list[dict] = []
    unity_scan(game_data, unity_rows, manifest)
    write_jsonl(out / "il2cpp-string-literals.jsonl", metadata_rows)
    write_jsonl(out / "unity-length-prefixed-candidates.jsonl", unity_rows)
    manifest["outputs"] = {
        "il2cpp_string_literals": {"path": "il2cpp-string-literals.jsonl", "rows": len(metadata_rows),
                                   "sha256": sha256(out / "il2cpp-string-literals.jsonl")},
        "unity_candidates": {"path": "unity-length-prefixed-candidates.jsonl", "rows": len(unity_rows),
                              "sha256": sha256(out / "unity-length-prefixed-candidates.jsonl")},
    }
    manifest["summary"] = {"metadata_rows": len(metadata_rows), "metadata_ascii_rows": sum(r["contains_ascii_letter"] for r in metadata_rows),
                            "unity_rows": len(unity_rows), "unity_by_confidence": dict(Counter(r["confidence"] for r in unity_rows)),
                            "input_count": len(manifest["inputs"]),
                            "files_with_failures": sum(bool(v.get("failures")) for v in manifest["inputs"].values()),
                            "global_failure_count": len(manifest["global_failures"])}
    (out / "extraction-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest["summary"], ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
