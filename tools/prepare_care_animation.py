"""Prepare the authored Eat sheet with the established 60 Hz sprite pipeline.

This is offline asset preparation, not runtime image transforms. ImageGen draws
the new poses; existing extraction, root stabilization, RIFE interpolation and
matte-quality checks produce independent RGBA frames. The four shipped actions
and canonical neutral are never rewritten by this command.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
import prepare_generated_sheets as pipeline


SPEC = pipeline.ClipSpec(
    name="Eat", sheet_name="eat-sheet-v1.png", duration_seconds=2.0,
    columns=5, rows=3, source_pose_count=15,
    authored_frame_indices=(0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 90, 100, 110, 120),
    segmentwise_interpolation=True,
)


def measure_eat_root(image) -> pipeline.RootAnchor:
    """Use planted yellow feet, since a pale bowl occludes the brown torso.

    The generic torso detector follows exposed brown wing strips beside the
    bowl and can shift the whole sprite ~40 px despite reporting a stable
    torso. For this full-body, non-walking action the two-foot center is the
    meaningful physical anchor. Restrict color sampling to the bottom 22 px
    so the yellow beak and cream bowl cannot influence registration.
    """
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8)
    visible = rgba[:, :, 3] >= pipeline.ROOT_ALPHA_THRESHOLD
    ys, _ = np.where(visible)
    if not len(ys):
        raise RuntimeError("Eat root measurement found no visible pixels")
    bottom = int(ys.max())
    yy = np.arange(image.height)[:, None]
    red, green, blue = [rgba[:, :, c].astype(np.int16) for c in range(3)]
    foot = visible & (yy >= bottom - 22) & (red >= 200) & (green >= 125) & (blue <= 90) & (green - blue >= 65)
    fy, fx = np.where(foot)
    if len(fx) < 80:
        raise RuntimeError("Eat root measurement cannot confidently identify planted feet")
    return pipeline.RootAnchor((int(fx.min()) + int(fx.max())) / 2.0, float(bottom), len(fx), len(fy))


def add_temporal_qa(qa: dict, frames: list) -> None:
    hashes = [hashlib.sha256(frame.convert("RGBA").tobytes()).hexdigest() for frame in frames]
    duplicate_indexes = [index for index in range(1, len(hashes)) if hashes[index] == hashes[index - 1]]
    qa["temporal_sampling"] = {
        "unique_pixel_frames": len(set(hashes)),
        "adjacent_duplicate_indexes": duplicate_indexes,
        "rife_directory_pair_sampling": "2*(segment_count-1) outputs; keep first segment_count inclusive t=0..1 samples",
    }
    if len(duplicate_indexes) > 2:
        qa["errors"].append("Eat unexpectedly contains repeated adjacent frames rather than continuous pair sampling")
        qa["passed"] = False


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--rife", type=Path)
    parser.add_argument("--rife-model", type=Path)
    parser.add_argument("--keys-only", action="store_true")
    parser.add_argument("--qa-only", action="store_true")
    parser.add_argument("--reuse-keys", action="store_true", help="Interpolate the previously inspected Eat keys")
    args = parser.parse_args()
    assets = args.assets.resolve(strict=True)
    if not assets.is_dir() or not (assets / "mascot-animated-neutral.png").is_file():
        raise SystemExit("Expected an existing desktop pet Assets directory")
    preview = assets / "AnimationPreviews"
    output = assets / "Animations" / SPEC.name
    print(f"Exact output targets: {assets / 'AnimationKeys' / SPEC.name}; {output}; {preview / 'Eat-qa.json'}", flush=True)
    neutral_path = assets / "mascot-animated-neutral.png"
    neutral_sha = hashlib.sha256(neutral_path.read_bytes()).hexdigest()
    neutral = pipeline.load_canonical_neutral(assets)
    # Override only this process's metric, never the shared pipeline source or
    # any older clip. Stabilization and QA must use the same physical anchor.
    pipeline.measure_root_anchor = measure_eat_root
    if args.qa_only:
        frames, _, errors = pipeline.load_frame_sequence(output)
        qa = pipeline.qa_frame_sequence(frames, 121, neutral, neutral, neutral, prior_errors=errors, component_spec=SPEC)
        add_temporal_qa(qa, frames)
        pipeline.print_qa_summary("Eat", qa)
        if not qa["passed"]:
            raise SystemExit(1)
        return
    if args.reuse_keys:
        keys = assets / "AnimationKeys" / SPEC.name
        expected = [keys / f"key-{index:02d}.png" for index in range(SPEC.pose_count)]
        if not all(path.is_file() for path in expected):
            raise SystemExit("--reuse-keys requires the complete previously prepared key set")
        frames = [pipeline.Image.open(path).convert("RGBA") for path in expected]
        target = measure_eat_root(neutral)
        if any(abs(measure_eat_root(frame).torso_x - target.torso_x) > 1.0 or
               abs(measure_eat_root(frame).foot_contact_y - target.foot_contact_y) > 0.5 for frame in frames):
            raise SystemExit("Existing Eat keys are not registered to the physical foot anchor")
        if not all(np.array_equal(np.asarray(frame), np.asarray(neutral)) for frame in (frames[0], frames[-1])):
            raise SystemExit("Existing Eat endpoints do not match canonical neutral exactly")
        if not pipeline.inspect_authored_key_preflight(frames, SPEC)["passed"]:
            raise SystemExit("Existing Eat keys failed preflight")
    else:
        cells = pipeline.normalize_cells(pipeline.extract_cells(assets / "AnimationSources" / SPEC.sheet_name, 5, 3, 15))
        keys, key_qa = pipeline.write_keys(assets, SPEC, cells, neutral)
        pipeline.make_key_contact_sheet(keys, SPEC.pose_count, preview / "Eat-keys-dark.png")
        pipeline.write_qa_report(preview / "Eat-keys-qa.json", key_qa)
    print("Eat authored key preflight passed", flush=True)
    if args.keys_only:
        return
    if args.rife is None or args.rife_model is None:
        raise SystemExit("--rife and --rife-model required for interpolation")
    count, qa = pipeline.run_rife(args.rife.resolve(strict=True), args.rife_model.resolve(strict=True), keys, output, SPEC, neutral)
    frames, _, _ = pipeline.load_frame_sequence(output)
    add_temporal_qa(qa, frames)
    qa["asset_provenance"] = {
        "source_sheet": SPEC.sheet_name, "generation": "built-in imagegen",
        "key_count": 15, "frame_count": count, "duration_seconds": 2.0,
        "source_reference": "References/Memes/干饭.gif",
        "neutral_sha256": neutral_sha,
        "anchor": "planted yellow-foot center and bottom contact, excluding bowl/torso occlusion",
        "method": "authored coherent key poses, segmentwise RIFE RGB/mask interpolation, root stabilization and matte QA",
    }
    pipeline.write_qa_report(preview / "Eat-qa.json", qa)
    if not qa["passed"]:
        raise RuntimeError("Eat temporal sampling QA failed; do not ship this frame set")
    pipeline.make_dark_background_contact_sheet(output, count, preview / "Eat-dark.png")
    pipeline.make_60fps_gif(output, count, preview / "Eat-60fps.gif")
    if hashlib.sha256(neutral_path.read_bytes()).hexdigest() != neutral_sha:
        raise RuntimeError("Canonical neutral changed unexpectedly")
    pipeline.print_qa_summary("Eat", qa)
    print(f"Eat: {count} full-body RGBA frames -> {output}", flush=True)


if __name__ == "__main__":
    main()
