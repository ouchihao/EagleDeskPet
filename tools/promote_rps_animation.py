"""Validated installation helper for prepare_rps_animation.py --promote.

All selected clips are validated before the first production write. Existing
destinations are backed up under staging, and an I/O failure triggers rollback.
No neutral, Yawn, manifest, source sheet or non-RPS resource is a destination.
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil

from PIL import Image

import prepare_rps_animation as rps


@dataclass(frozen=True)
class CopyItem:
    source: Path
    destination: Path
    sha256: str


def _image(path: Path, canvas: tuple[int, int]) -> bytes:
    with Image.open(path) as image:
        image.load()
        if image.mode != "RGBA" or image.size != canvas:
            raise ValueError(f"Incorrect RGBA canvas: {path}; expected {canvas}")
        return image.tobytes()


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def _destination(assets: Path, *parts: str) -> Path:
    target = assets.joinpath(*parts).resolve()
    _require(target.is_relative_to(assets), f"Production destination escapes exact Assets root: {target}")
    _require(not target.exists() or target.is_file(), f"Destination is not a regular file: {target}")
    return target


def validate_selection(assets: Path, stage: Path, selected: list[tuple[str, str]], *, repository_root=rps.ROOT):
    assets = assets.resolve(strict=True)
    expected = (repository_root / "DuckDeskPet" / "Assets").resolve(strict=True)
    _require(assets == expected, f"Promotion only targets this repository's exact Assets directory: {expected}")
    stage = rps.validate_output_root(stage, repository_root)
    _require(bool(selected), "No clips selected")
    _require(len(selected) == len(set(selected)), "Duplicate selected clips")
    plan, stale, input_digests = [], [], {}
    for outfit, clip in selected:
        source, neutral_path = rps.input_paths(assets, outfit, clip)
        order, metadata = rps.read_source_order(source)
        spec = rps.clip_spec(clip, source.name)
        expected_count = round(spec.duration_seconds * 60) + 1
        keys, animations, qa_dir = rps.output_paths(stage, outfit, clip)
        key_names = [f"key-{i:02d}.png" for i in range(16)]
        frame_names = [f"frame-{i:04d}.png" for i in range(expected_count)]
        _require([p.name for p in sorted(keys.glob("key-*.png"))] == key_names, f"Missing/extra staged keys: {outfit}/{clip}")
        _require([p.name for p in sorted(animations.glob("frame-*.png"))] == frame_names, f"Missing/extra staged frames: {outfit}/{clip}")
        source_hash, neutral_hash = rps.sha256(source), rps.sha256(neutral_path)
        metadata_hash = rps.sha256(metadata) if metadata else None
        for path in (source, neutral_path, *([metadata] if metadata else [])):
            input_digests[path] = rps.sha256(path)
        if metadata is None:
            input_digests[source.with_suffix(".json")] = None
        reports = {}
        for suffix, count in (("keys-qa", 16), ("qa", expected_count)):
            path = qa_dir / f"{clip}-{suffix}.json"
            _require(path.is_file(), f"Missing QA report: {path}")
            report = json.loads(path.read_text(encoding="utf-8-sig"))
            _require(report.get("passed") is True and not report.get("errors"), f"Failed/pending QA report: {path}")
            _require(report.get("source_sha256") == source_hash, f"Stale source hash: {path}")
            _require(report.get("neutral_sha256") == neutral_hash, f"Stale neutral hash: {path}")
            _require(report.get("authored_frame_indices") == list(spec.authored_frame_indices), f"Incorrect key timing: {path}")
            _require(report.get("frame_count") == count, f"QA frame count mismatch: {path}")
            provenance = report.get("provenance", {})
            _require(provenance.get("source_sha256") == source_hash and provenance.get("neutral_sha256") == neutral_hash,
                     f"Inconsistent build provenance hashes: {path}")
            _require(provenance.get("source_order") == list(order), f"Stale source order: {path}")
            _require(provenance.get("source_order_metadata_sha256") == metadata_hash, f"Stale source-order metadata: {path}")
            _require(provenance.get("duration_seconds") == spec.duration_seconds and
                     provenance.get("expected_frame_count") == expected_count and provenance.get("authored_keys_locked") is True and
                     provenance.get("segmentwise_interpolation") is True and provenance.get("qa_thresholds_relaxed") is False,
                     f"Incomplete or incompatible build provenance: {path}")
            reports[suffix] = report
        _require(reports["keys-qa"].get("preflight", {}).get("passed") is True, f"Unpassed key preflight: {outfit}/{clip}")
        locked = reports["qa"].get("authored_key_frames", {})
        _require(locked.get("locked") is True and locked.get("all_exact") is True and
                 locked.get("frame_indices") == list(spec.authored_frame_indices), f"Missing exact key-lock QA: {outfit}/{clip}")
        canvas = rps.pipeline.CANVAS_SIZE
        neutral_bytes = _image(neutral_path, canvas)
        key_pixels = [_image(keys / name, canvas) for name in key_names]
        _require(key_pixels[0] == neutral_bytes and key_pixels[-1] == neutral_bytes, f"Key endpoints differ from neutral: {outfit}/{clip}")
        lock_by_index = dict(zip(spec.authored_frame_indices, key_pixels))
        for index, name in enumerate(frame_names):
            pixels = _image(animations / name, canvas)
            if index in lock_by_index:
                _require(pixels == lock_by_index[index], f"Authored key changed at frame {index}: {outfit}/{clip}")
        base = () if outfit == "default" else ("Outfits", outfit.title())
        key_destination = ("AnimationKeys", clip) if outfit == "default" else (*base, "Keys", clip)
        frame_destination = (*base, "Animations", clip)
        qa_destination = ("AnimationPreviews",) if outfit == "default" else ("AnimationPreviews", outfit.title())
        for source_dir, names, destination_parts, digest_key in (
                (keys, key_names, key_destination, "key_file_sha256"),
                (animations, frame_names, frame_destination, "frame_file_sha256")):
            recorded_hashes = reports["qa"].get(digest_key)
            if recorded_hashes is not None:
                _require(set(recorded_hashes) == set(names), f"Incomplete recorded file hashes: {outfit}/{clip}/{digest_key}")
            for name in names:
                path = source_dir / name
                digest = rps.sha256(path)
                if recorded_hashes is not None:
                    _require(recorded_hashes[name] == digest, f"Staged file changed after QA: {path}")
                plan.append(CopyItem(path, _destination(assets, *destination_parts, name), digest))
            destination_dir = assets.joinpath(*destination_parts).resolve()
            _require(destination_dir.is_relative_to(assets), f"Production clip directory escaped Assets: {destination_dir}")
            pattern = "key-*.png" if digest_key == "key_file_sha256" else "frame-*.png"
            for path in destination_dir.glob(pattern):
                if path.name not in names:
                    stale.append(_destination(assets, *destination_parts, path.name))
        for suffix in ("keys-qa.json", "qa.json", "keys-dark.png", "dark.png", "60fps.gif"):
            path = qa_dir / f"{clip}-{suffix}"
            _require(path.is_file(), f"Missing staged review artifact: {path}")
            if path.suffix in (".png", ".gif"):
                with Image.open(path) as image:
                    image.verify()
            plan.append(CopyItem(path, _destination(assets, *qa_destination, path.name), rps.sha256(path)))
        print(f"Validated promotion inputs: {outfit}/{clip}: 16 exact keys, {expected_count} RGBA frames", flush=True)
    _require(len({item.destination for item in plan}) == len(plan), "Duplicate production destinations")
    return assets, stage, plan, stale, input_digests


def promote(assets: Path, stage: Path, selected: list[tuple[str, str]], *, repository_root=rps.ROOT):
    # No mkdir/copy/write occurs until the entire selection has passed.
    assets, stage, plan, stale, input_digests = validate_selection(assets, stage, selected, repository_root=repository_root)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    transaction = stage / "PromotionBackups" / stamp
    _require(transaction.resolve().is_relative_to(stage), f"Promotion backup escapes staging root: {transaction}")
    print(f"Exact production Assets root: {assets}", flush=True)
    print(f"Exact recoverable promotion backup: {transaction}", flush=True)
    for directory in sorted({str(item.destination.parent) for item in plan}):
        print(f"Exact production destination directory: {directory}", flush=True)
    backup, prepared = transaction / "previous", transaction / "prepared"
    backup.mkdir(parents=True)
    prepared.mkdir()
    previous = {}
    for destination in [*(item.destination for item in plan), *stale]:
        _require(not destination.exists() or destination.is_file(), f"Destination changed type: {destination}")
        previous[destination] = rps.sha256(destination) if destination.exists() else None
        if destination.exists():
            path = backup / destination.relative_to(assets)
            path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(destination, path)
    for index, item in enumerate(plan):
        copy = prepared / str(index)
        shutil.copy2(item.source, copy)
        _require(rps.sha256(copy) == item.sha256, f"Staged source changed while preparing: {item.source}")
    for path, digest in input_digests.items():
        _require((not path.exists()) if digest is None else (path.is_file() and rps.sha256(path) == digest),
                 f"Read-only build input changed before promotion: {path}")
    for destination, digest in previous.items():
        _require((rps.sha256(destination) if destination.is_file() else None) == digest,
                 f"Production destination changed before promotion: {destination}")
    touched = []
    manifest = {"created_utc": rps.utc_now(), "assets": str(assets), "selected": selected,
                "files": [{"source": str(item.source), "destination": str(item.destination), "sha256": item.sha256,
                           "previous_sha256": previous[item.destination]} for item in plan],
                "removed_stale_rps_files": [str(path) for path in stale], "passed": False}
    log = transaction / "promotion.json"
    log.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    try:
        for index, item in enumerate(plan):
            item.destination.parent.mkdir(parents=True, exist_ok=True)
            os.replace(prepared / str(index), item.destination)
            touched.append(item.destination)
        for path in stale:
            # Existing contents are retained in the explicit backup above.
            path.unlink()
            touched.append(path)
        for item in plan:
            _require(rps.sha256(item.destination) == item.sha256, f"Post-copy validation failed: {item.destination}")
        manifest["passed"] = True
    except Exception as error:
        manifest["error"] = str(error)
        rollback_errors = []
        for destination in reversed(touched):
            try:
                if previous[destination] is None:
                    destination.unlink(missing_ok=True)
                else:
                    shutil.copy2(backup / destination.relative_to(assets), destination)
            except Exception as rollback_error:
                rollback_errors.append(f"{destination}: {rollback_error}")
        manifest["rollback_errors"] = rollback_errors
        raise
    finally:
        log.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"promoted_clips": len(selected), "files": len(plan), "backup": str(transaction),
                      "removed_stale_rps_files": len(stale), "passed": True}), flush=True)
    return manifest
