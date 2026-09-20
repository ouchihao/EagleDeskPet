"""Prepare v1.9 ImageGen keys with the existing optical-flow and alpha QA pipeline.

Only the explicitly selected new clip directories are written. Existing frames
and the canonical neutral are inputs. No pose crossfades or repeated-frame fill.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image
import prepare_generated_sheets as pipeline
from prepare_care_animation import measure_eat_root, add_temporal_qa
from prepare_work_animation import align_cells, extract_native_cells
import work_asset_acceleration


def prepare(assets: Path, family: str, neutral: Image.Image):
    if family == "Rps":
        raise ValueError("Rps uses the 16-key v2 builder: tools/prepare_rps_animation.py; the legacy 5-key builder is disabled")
    source = assets / "AnimationSources" / f"{family.lower()}-sheet-v1.png"
    cells = align_cells(extract_native_cells(source, 5, 3), neutral)
    if family == "Hungry":
        anchor = cells[6]
        return {
            "HungryEnter": (1.5, [neutral, *cells[1:7]], (0, 12, 25, 40, 56, 73, 90)),
            "HungryLoop": (2.0, [anchor, cells[7], cells[8], cells[9], cells[10], anchor], (0, 24, 48, 72, 96, 120)),
            "HungryExit": (1.5, [anchor, cells[11], cells[12], cells[13], neutral], (0, 23, 46, 67, 90)),
        }
    if family in ("Tea", "Annoyed"):
        return {family: (2.0, [neutral, *cells[1:14], neutral], (0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 90, 100, 110, 120))}
    raise ValueError(family)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--family", choices=["Hungry", "Tea", "Rps", "Annoyed"], required=True)
    parser.add_argument("--rife", type=Path)
    parser.add_argument("--rife-model", type=Path)
    parser.add_argument("--keys-only", action="store_true")
    parser.add_argument("--clips", nargs="*")
    parser.add_argument("--dependency-root", type=Path, help="Optional existing SciPy target directory for build-only acceleration")
    args = parser.parse_args()
    if args.family == "Rps":
        parser.error("Rps uses tools/prepare_rps_animation.py; no legacy RPS assets have been written")
    assets = args.assets.resolve(strict=True)
    neutral_path = assets / "mascot-animated-neutral.png"
    baseline = hashlib.sha256(neutral_path.read_bytes()).hexdigest()
    neutral = Image.open(neutral_path).convert("RGBA")
    work_asset_acceleration.enable(pipeline, args.dependency_root.resolve(strict=True) if args.dependency_root else None)
    pipeline.measure_root_anchor = measure_eat_root
    clips = prepare(assets, args.family, neutral)
    for name, (seconds, frames, indices) in clips.items():
        if args.clips and name not in args.clips:
            continue
        key_dir = assets / "AnimationKeys" / name
        output = assets / "Animations" / name
        preview = assets / "AnimationPreviews"
        print(f"Exact new-asset targets: {key_dir}; {output}", flush=True)
        key_dir.mkdir(parents=True, exist_ok=True)
        for index, frame in enumerate(frames):
            frame.save(key_dir / f"key-{index:02d}.png", optimize=True)
        pipeline.make_key_contact_sheet(key_dir, len(frames), preview / f"{name}-keys-dark.png")
        if args.keys_only:
            continue
        if args.rife is None or args.rife_model is None:
            raise SystemExit("Provide --rife and --rife-model; no placeholder frames are generated")
        spec = pipeline.ClipSpec(name=name, sheet_name=f"{args.family.lower()}-sheet-v1.png", duration_seconds=seconds,
                                 columns=len(frames), rows=1, source_pose_count=len(frames),
                                 authored_frame_indices=indices, segmentwise_interpolation=True)
        count, qa = pipeline.run_rife(args.rife.resolve(strict=True), args.rife_model.resolve(strict=True),
                                      key_dir, output, spec, neutral, exact_endpoints=(frames[0], frames[-1]))
        rendered, _, _ = pipeline.load_frame_sequence(output)
        add_temporal_qa(qa, rendered)
        qa["provenance"] = {"generator": "built-in imagegen", "sheet": spec.sheet_name,
                            "source_sha256": hashlib.sha256((assets / "AnimationSources" / spec.sheet_name).read_bytes()).hexdigest(),
                            "method": "authored keys, paired RIFE RGB/alpha, planted-foot stabilization", "neutral_sha256": baseline}
        pipeline.write_qa_report(preview / f"{name}-qa.json", qa)
        pipeline.make_dark_background_contact_sheet(output, count, preview / f"{name}-dark.png")
        pipeline.make_60fps_gif(output, count, preview / f"{name}-60fps.gif")
        print(json.dumps({"clip": name, "frames": count, "passed": qa["passed"]}), flush=True)
        if not qa["passed"]:
            raise SystemExit(f"{name} failed QA; do not register these frames")
    assert hashlib.sha256(neutral_path.read_bytes()).hexdigest() == baseline


if __name__ == "__main__":
    main()
