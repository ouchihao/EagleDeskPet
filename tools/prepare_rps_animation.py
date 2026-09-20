"""Build the 16-key RPS V2 sheets into a non-production staging directory.

Inputs are read-only ImageGen 4x4 sheets and each outfit's canonical neutral.
Build mode only writes to a named .codex-build child and never registers or
installs assets; explicit --promote validates and installs selected staged RPS
resources with backups. It reuses native-alpha registration, matte cleanup, planted-foot
measurement, segmentwise RIFE and the existing unrelaxed QA gates. A failed
attempt keeps its reports and contact sheets for inspection, not publication.
"""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path

from PIL import Image

import prepare_generated_sheets as pipeline
from prepare_care_animation import add_temporal_qa, measure_eat_root
from prepare_office_outfit import clean_registered_alpha
from prepare_work_animation import align_cells, extract_native_cells
import work_asset_acceleration


ROOT = Path(__file__).resolve().parent.parent
OUTFITS = ("default", "office", "hoodie")
CLIP_SOURCES = {"RpsRock": "rock", "RpsPaper": "paper", "RpsScissors": "scissors",
                "RpsWin": "win", "RpsLose": "lose"}
THROW_POSITIONS = (0, 8, 16, 26, 36, 46, 56, 72, 92, 108, 118, 130, 142, 152, 162, 168)
REACTION_POSITIONS = (0, 8, 16, 24, 34, 44, 54, 64, 74, 84, 94, 104, 114, 126, 136, 144)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def canonical_clip(value: str) -> str:
    for clip, short in CLIP_SOURCES.items():
        if value.lower() in (clip.lower(), short):
            return clip
    raise argparse.ArgumentTypeError(f"Unknown RPS clip: {value}")


def clip_spec(clip: str, source_name: str) -> pipeline.ClipSpec:
    if clip not in CLIP_SOURCES:
        raise ValueError(f"Unknown RPS clip: {clip}")
    positions = REACTION_POSITIONS if clip in ("RpsWin", "RpsLose") else THROW_POSITIONS
    return pipeline.ClipSpec(name=clip, sheet_name=source_name,
                             duration_seconds=2.4 if positions == REACTION_POSITIONS else 2.8,
                             columns=4, rows=4, source_pose_count=16,
                             authored_frame_indices=positions, segmentwise_interpolation=True,
                             lock_authored_frames=True)


def read_source_order(source: Path) -> tuple[tuple[int, ...], Path | None]:
    metadata = source.with_suffix(".json")
    if not metadata.exists():
        return tuple(range(16)), None
    info = json.loads(metadata.read_text(encoding="utf-8-sig"))
    order = info.get("source_order") if isinstance(info, dict) else None
    if not isinstance(order, list) or len(order) != 16 or any(type(i) is not int or not 0 <= i < 16 for i in order):
        raise ValueError(f"{metadata}: source_order must contain exactly 16 integer indexes from 0 to 15 (not bool)")
    # Reusing a non-adjacent generated pose is deliberate art direction; expose
    # every repeated/omitted cell in provenance and retain the temporal QA gate.
    return tuple(order), metadata


def validate_output_root(path: Path, repository_root: Path = ROOT) -> Path:
    target = path.resolve()
    allowed = (repository_root / ".codex-build").resolve()
    if target == allowed or not target.is_relative_to(allowed):
        raise ValueError("--output-root must be a named child of this repository's .codex-build")
    if target.exists() and not target.is_dir():
        raise ValueError(f"Output root is not a directory: {target}")
    return target


def output_paths(output_root: Path, outfit: str, clip: str) -> tuple[Path, Path, Path]:
    root = output_root.resolve()
    paths = tuple((root / outfit / part / clip).resolve() for part in ("Keys", "Animations"))
    qa = (root / outfit / "QA").resolve()
    for path in (*paths, qa):
        if not path.is_relative_to(root):
            raise ValueError(f"Output child resolves outside staging root: {path}")
    return *paths, qa


def input_paths(assets: Path, outfit: str, clip: str) -> tuple[Path, Path]:
    if outfit not in OUTFITS or clip not in CLIP_SOURCES:
        raise ValueError("Unsupported outfit or clip")
    source = assets / "AnimationSources" / "RpsV2" / f"{outfit}-{CLIP_SOURCES[clip]}-v2.png"
    neutral = assets / ("mascot-animated-neutral.png" if outfit == "default" else f"Outfits/{outfit.title()}/neutral.png")
    for path in (source, neutral):
        if not path.is_file():
            raise ValueError(f"Missing read-only input: {path}")
    return source, neutral


def provenance(source: Path, neutral_path: Path, order: tuple[int, ...], metadata: Path | None,
               spec: pipeline.ClipSpec, assets: Path) -> dict:
    counts = Counter(order)
    return {
        "generator": "built-in imagegen", "source": source.relative_to(assets).as_posix(),
        "source_sha256": sha256(source), "source_layout": {"columns": 4, "rows": 4, "cells": 16},
        "source_order": list(order),
        "reused_source_cells": [{"source_cell": index, "key_positions": [i for i, cell in enumerate(order) if cell == index]}
                                for index in sorted(counts) if counts[index] > 1],
        "omitted_source_cells": [i for i in range(16) if i not in counts],
        "source_order_metadata": metadata.name if metadata else None,
        "source_order_metadata_sha256": sha256(metadata) if metadata else None,
        "neutral": neutral_path.relative_to(assets).as_posix(), "neutral_sha256": sha256(neutral_path),
        "canonical_endpoint_key_positions": [0, 15],
        "canonical_endpoint_policy": "First and last cells are replaced by the existing outfit neutral, pixel-exact.",
        "fps": 60, "duration_seconds": spec.duration_seconds,
        "authored_frame_indices": list(spec.authored_frame_indices),
        "authored_time_seconds": [round(i / 60, 6) for i in spec.authored_frame_indices],
        "expected_frame_count": round(spec.duration_seconds * 60) + 1,
        "authored_keys_locked": True, "segmentwise_interpolation": True,
        "method": "native-alpha extraction, one sheet-wide helmet registration, established alpha cleanup, paired RIFE RGB/alpha, planted-foot stabilization",
        "qa_thresholds_relaxed": False, "build_started_utc": utc_now(),
    }


def annotate(report: dict, info: dict, outfit: str, clip: str) -> dict:
    report.update({"outfit": f"outfit.{outfit}", "clip": clip,
                   "source_sha256": info["source_sha256"], "neutral_sha256": info["neutral_sha256"],
                   "authored_frame_indices": info["authored_frame_indices"],
                   "provenance": {**info, "report_written_utc": utc_now()}})
    return report


def unchanged_inputs(info: dict, source: Path, neutral_path: Path, metadata: Path | None) -> None:
    for path, expected in ((source, info["source_sha256"]), (neutral_path, info["neutral_sha256"]),
                           (metadata, info["source_order_metadata_sha256"])):
        if path is not None and (not path.is_file() or sha256(path) != expected):
            raise RuntimeError(f"Read-only input changed during build: {path}")
    if metadata is None and source.with_suffix(".json").exists():
        raise RuntimeError(f"Source-order metadata appeared during build: {source.with_suffix('.json')}")


def persist_failed_frames(error: Exception, output: Path, qa_dir: Path, spec: pipeline.ClipSpec,
                          neutral: Image.Image, info: dict, outfit: str, clip: str) -> None:
    report = {"passed": False, "errors": [f"{type(error).__name__}: {error}"], "build_error": str(error)}
    try:
        rendered, _, load_errors = pipeline.load_frame_sequence(output)
        if rendered:
            report = pipeline.qa_frame_sequence(rendered, round(spec.duration_seconds * 60) + 1,
                                                neutral, neutral, neutral,
                                                prior_errors=load_errors, component_spec=spec)
            add_temporal_qa(report, rendered)
            report["errors"].append(f"Build failed: {error}")
            report["passed"] = False
            report["build_error"] = str(error)
            # A partial sequence can have holes. Only call the shared contact
            # renderer when indexes are contiguous; never fabricate missing PNGs.
            names = [path.name for path in sorted(output.glob("frame-*.png"))]
            if names == [f"frame-{i:04d}.png" for i in range(len(rendered))]:
                pipeline.make_dark_background_contact_sheet(output, len(rendered), qa_dir / f"{clip}-dark.png")
    except Exception as diagnostic_error:
        report["diagnostic_error"] = f"{type(diagnostic_error).__name__}: {diagnostic_error}"
    pipeline.write_qa_report(qa_dir / f"{clip}-qa.json", annotate(report, info, outfit, clip))


def build_one(assets: Path, output_root: Path, outfit: str, clip: str, *, keys_only: bool,
              rife: Path | None = None, rife_model: Path | None = None) -> dict:
    source, neutral_path = input_paths(assets, outfit, clip)
    order, metadata = read_source_order(source)
    spec = clip_spec(clip, source.name)
    info = provenance(source, neutral_path, order, metadata, spec, assets)
    keys, output, qa_dir = output_paths(output_root, outfit, clip)
    print(f"[{outfit}/{clip}] exact staging keys: {keys}; frames: {output}; QA: {qa_dir}", flush=True)
    keys.mkdir(parents=True, exist_ok=True)
    qa_dir.mkdir(parents=True, exist_ok=True)
    with Image.open(neutral_path) as image:
        neutral = image.convert("RGBA")
    pipeline.measure_root_anchor = measure_eat_root
    key_report = None
    key_report_written = False
    # A keys-only rebuild must never leave an older final report publishable.
    # Existing diagnostic frames remain for review, but are explicitly pending.
    pending = {"passed": False, "errors": ["Keys are being rebuilt; final animation has not been validated for this attempt."],
               "build_state": "pending_keys_or_interpolation"}
    pipeline.write_qa_report(qa_dir / f"{clip}-qa.json", annotate(pending, info, outfit, clip))
    try:
        extra_keys = [p.name for p in keys.glob("key-*.png") if p.name not in {f"key-{i:02d}.png" for i in range(16)}]
        if extra_keys:
            raise ValueError(f"Unexpected stale keys in staging; inspect before retry: {extra_keys}")
        cells = extract_native_cells(source, 4, 4)
        if len(cells) != 16:
            raise ValueError(f"Expected 16 extracted cells, received {len(cells)}")
        # Reorder before registration so the declared opening cell controls the
        # shared helmet scale even when extraction order needs correction.
        frames = [clean_registered_alpha(frame) for frame in align_cells([cells[i] for i in order], neutral)]
        if len(frames) != 16:
            raise ValueError("Registration changed the authored key count")
        frames[0], frames[-1] = neutral.copy(), neutral.copy()
        for index, frame in enumerate(frames):
            frame.save(keys / f"key-{index:02d}.png", optimize=True)
        pipeline.make_key_contact_sheet(keys, 16, qa_dir / f"{clip}-keys-dark.png")
        key_report = pipeline.qa_frame_sequence(frames, 16, neutral, neutral, neutral, component_spec=spec)
        key_report["preflight"] = pipeline.inspect_authored_key_preflight(frames, spec)
        if not key_report["preflight"]["passed"]:
            key_report["passed"] = False
            key_report["errors"].extend(key_report["preflight"].get("errors", []))
        unchanged_inputs(info, source, neutral_path, metadata)
        pipeline.write_qa_report(qa_dir / f"{clip}-keys-qa.json", annotate(key_report, info, outfit, clip))
        key_report_written = True
        if not key_report["passed"]:
            raise RuntimeError(f"{outfit}/{clip} authored keys failed QA: {key_report['errors']}")
        print(json.dumps({"outfit": outfit, "clip": clip, "stage": "keys", "passed": True,
                          "keys": 16, "expected_frames": info["expected_frame_count"]}), flush=True)
        if keys_only:
            return key_report
        if rife is None or rife_model is None:
            raise ValueError("Provide --rife and --rife-model; no placeholder frames are generated")
        print(f"[{outfit}/{clip}] interpolating 15 RGB/alpha segments; authored keys remain locked", flush=True)
        count, report = pipeline.run_rife(rife, rife_model, keys, output, spec, neutral,
                                           exact_endpoints=(neutral, neutral))
        rendered, _, errors = pipeline.load_frame_sequence(output)
        report["errors"].extend(errors)
        if count != info["expected_frame_count"]:
            report["errors"].append(f"Unexpected output count {count}; expected {info['expected_frame_count']}")
        report["passed"] = report["passed"] and not report["errors"]
        add_temporal_qa(report, rendered)
        report["frame_file_sha256"] = {path.name: sha256(path) for path in sorted(output.glob("frame-*.png"))}
        report["key_file_sha256"] = {path.name: sha256(path) for path in sorted(keys.glob("key-*.png"))}
        unchanged_inputs(info, source, neutral_path, metadata)
        pipeline.write_qa_report(qa_dir / f"{clip}-qa.json", annotate(report, info, outfit, clip))
        pipeline.make_dark_background_contact_sheet(output, count, qa_dir / f"{clip}-dark.png")
        pipeline.make_60fps_gif(output, count, qa_dir / f"{clip}-60fps.gif")
        print(json.dumps({"outfit": outfit, "clip": clip, "stage": "frames", "passed": report["passed"],
                          "frames": count, "seconds": spec.duration_seconds}), flush=True)
        if not report["passed"]:
            raise RuntimeError(f"{outfit}/{clip} frame QA failed; do not install staging output: {report['errors']}")
        return report
    except Exception as error:
        if not key_report_written:
            key_report = key_report or {"passed": False, "errors": []}
            key_report["passed"] = False
            key_report["errors"].append(f"{type(error).__name__}: {error}")
            key_report["build_error"] = str(error)
            pipeline.write_qa_report(qa_dir / f"{clip}-keys-qa.json", annotate(key_report, info, outfit, clip))
        persist_failed_frames(error, output, qa_dir, spec, neutral, info, outfit, clip)
        raise


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, default=ROOT / "DuckDeskPet" / "Assets")
    parser.add_argument("--output-root", type=Path, default=ROOT / ".codex-build" / "rps-v2-build")
    parser.add_argument("--outfits", nargs="+", choices=OUTFITS, default=list(OUTFITS))
    parser.add_argument("--clips", nargs="+", type=canonical_clip, default=list(CLIP_SOURCES))
    parser.add_argument("--keys-only", action="store_true")
    parser.add_argument("--promote", action="store_true", help="Validate the entire selected existing staging set, then install only its RPS assets")
    parser.add_argument("--rife", type=Path)
    parser.add_argument("--rife-model", type=Path)
    parser.add_argument("--dependency-root", type=Path, help="Optional existing SciPy cache; default uses repository .tool-cache")
    args = parser.parse_args(argv)
    assets = args.assets.resolve(strict=True)
    output_root = validate_output_root(args.output_root)
    if not assets.is_dir():
        parser.error("--assets must be an existing directory")
    if args.promote:
        if args.keys_only or args.rife or args.rife_model:
            parser.error("--promote cannot be combined with build or --keys-only options")
        from promote_rps_animation import promote
        selected = [(outfit, clip) for outfit in dict.fromkeys(args.outfits) for clip in dict.fromkeys(args.clips)]
        promote(assets, output_root, selected)
        return
    if not args.keys_only and (args.rife is None or args.rife_model is None):
        parser.error("Provide --rife and --rife-model, or --keys-only")
    rife = args.rife.resolve(strict=True) if args.rife else None
    model = args.rife_model.resolve(strict=True) if args.rife_model else None
    if rife is not None and not rife.is_file():
        parser.error("--rife must be an existing executable file")
    if model is not None and not model.is_dir():
        parser.error("--rife-model must be an existing model directory")
    selected = [(outfit, clip) for outfit in dict.fromkeys(args.outfits) for clip in dict.fromkeys(args.clips)]
    # Resolve the entire selected input set before creating any staging output.
    for outfit, clip in selected:
        source, _ = input_paths(assets, outfit, clip)
        read_source_order(source)
        output_paths(output_root, outfit, clip)
    print(f"Read-only asset root: {assets}\nExact RPS staging root: {output_root}", flush=True)
    work_asset_acceleration.enable(pipeline, args.dependency_root.resolve(strict=True) if args.dependency_root else None)
    for index, (outfit, clip) in enumerate(selected, 1):
        print(f"RPS V2 clip {index}/{len(selected)}: {outfit}/{clip}", flush=True)
        build_one(assets, output_root, outfit, clip, keys_only=args.keys_only, rife=rife, rife_model=model)


if __name__ == "__main__":
    main()
