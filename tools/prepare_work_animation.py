"""Author working/busy scene clips from ImageGen key sheets and fixed props.

Only work-v1 assets are written. Existing pet animations and the canonical
neutral are inputs, never rewritten. Character frames retain the 384x346
canvas and planted-foot anchor; no camera transform or pose crossfade is used.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

import prepare_generated_sheets as pipeline
from prepare_care_animation import measure_eat_root
import work_asset_acceleration


def extract_native_cells(path: Path, columns: int, rows: int) -> list[Image.Image]:
    """Retain native generated alpha rather than treating transparent RGB as art."""
    image = Image.open(path).convert("RGBA")
    if image.getchannel("A").getextrema()[0] == 255:
        return pipeline.extract_cells(path, columns, rows, columns * rows)
    rgba = np.asarray(image)
    components = sorted(pipeline.label_alpha_components(rgba[:, :, 3]),
                        key=lambda item: item.area, reverse=True)[:columns * rows]
    components.sort(key=lambda item: item.center_y)
    ordered = []
    for row in range(rows):
        ordered.extend(sorted(components[row * columns:(row + 1) * columns],
                              key=lambda item: item.center_x))
    if len(ordered) != columns * rows:
        raise RuntimeError("Native-alpha sheet has too few separate characters")
    max_width = max(item.maximum_x - item.minimum_x + 1 for item in ordered) + 24
    max_height = max(item.maximum_y - item.minimum_y + 1 for item in ordered) + 24
    cells = []
    for item in ordered:
        cell = np.zeros((max_height, max_width, 4), dtype=np.uint8)
        ys, xs = np.divmod(item.flat_indices, image.width)
        tx = xs - round((item.minimum_x + item.maximum_x) / 2) + max_width // 2
        ty = ys - item.maximum_y + max_height - 13
        cell[ty, tx] = rgba[ys, xs]
        cells.append(Image.fromarray(cell))
    return cells


def align_cells(cells: list[Image.Image], neutral: Image.Image) -> list[Image.Image]:
    normalized = pipeline.normalize_cells(cells)
    target = measure_eat_root(neutral)
    # Independent sheets sometimes widen the helmet although total height is
    # correct. Register the neutral helmet width once per sheet, not per frame
    # (per-frame fitting would erase intentional expressive motion).
    def helmet_width(frame: Image.Image) -> int:
        _, xs = np.where(np.asarray(frame)[115:200, :, 3] >= 32)
        return int(xs.max() - xs.min() + 1)
    horizontal_scale = helmet_width(neutral) / helmet_width(normalized[0])
    print(f"Source helmet width registration: {horizontal_scale:.4f}", flush=True)
    result = []
    for frame in normalized:
        width = round(frame.width * horizontal_scale)
        resized = pipeline.add_rgb_edge_bleed(frame).resize((width, frame.height), Image.Resampling.LANCZOS)
        frame = Image.new("RGBA", pipeline.CANVAS_SIZE)
        frame.alpha_composite(resized, (round((frame.width - width) / 2), 0))
        root = measure_eat_root(frame)
        aligned = pipeline.translate_rgba_integer(frame, round(target.torso_x - root.torso_x),
                                                  round(target.foot_contact_y - root.foot_contact_y))
        result.append(pipeline.add_rgb_edge_bleed(aligned))
    return result


def place_prop(cropped: Image.Image, width: int, x: int, bottom: int) -> Image.Image:
    art = cropped.crop(pipeline.alpha_bbox(cropped))
    art = pipeline.add_rgb_edge_bleed(art, iterations=12)
    height = round(art.height * width / art.width)
    art = art.resize((width, height), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", pipeline.CANVAS_SIZE)
    canvas.alpha_composite(art, (x, bottom - height))
    return canvas


def prepare_props(assets: Path, work_anchor: Image.Image, busy_anchor: Image.Image) -> None:
    # Separate source objects remain fixed raster art at runtime. A single
    # translation controls each whole prop during entry/exit; RIFE never sees
    # either prop, preventing bending laptop edges and duplicate silhouettes.
    cells = pipeline.extract_cells(assets / "AnimationSources/work-props-sheet-v1.png", 1, 2, 2)
    desk = place_prop(cells[0], 306, 39, 337)
    # Keep the original base on the table while exposing the complete beak
    # and more of both typing wings beside the smaller screen.
    laptop = place_prop(cells[1], 102, 141, 275)
    props = assets / "SceneProps/Work"
    props.mkdir(parents=True, exist_ok=True)
    desk.save(props / "desk.png", optimize=True)
    laptop.save(props / "laptop.png", optimize=True)
    # The tabletop rear surface is behind the eagle; the lip/legs are in front.
    # This lets its wings naturally hover on the surface without the brown
    # belly painting over the front fascia. Both layers share one translation.
    rear = np.array(desk)
    front = rear.copy()
    rear[276:, :, 3] = 0
    front[:276, :, 3] = 0
    Image.fromarray(rear).save(props / "desk-back.png", optimize=True)
    Image.fromarray(front).save(props / "desk-front.png", optimize=True)
    (props / "layout.json").write_text(json.dumps({
        "canvas": [384, 346], "character_offset": [0, 0],
        "character_foot_anchor": [191.5, 336.0],
        "layers_back_to_front": ["desk-back", "character", "desk-front", "laptop"],
        "desk_bounds": list(desk.getbbox()), "laptop_bounds": list(laptop.getbbox()),
        "desk_split_y": 276,
        "entry": {"desk_slide_seconds": [0.15, 1.0], "laptop_drop_seconds": [0.9, 1.8]},
        "exit": {"laptop_lift_seconds": [0.35, 1.15], "desk_slide_seconds": [0.9, 1.95], "desk_direction": "left"},
        "fire": {"growth_seconds": [0.25, 1.3], "exit_shrink_seconds": [0.0, 0.85]},
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    preview = Image.new("RGB", (768, 346), (18, 21, 28))
    for index, eagle in enumerate((work_anchor, busy_anchor)):
        scene = Image.new("RGBA", pipeline.CANVAS_SIZE)
        for layer in (Image.fromarray(rear), eagle, Image.fromarray(front), laptop):
            scene.alpha_composite(layer)
        preview.paste(scene, (index * 384, 0), scene)
    preview.save(assets / "AnimationPreviews/Work-scene-anchors-dark.png")


def prepare_keys(assets: Path, neutral: Image.Image) -> dict:
    sources = assets / "AnimationSources"
    entry = align_cells(extract_native_cells(sources / "work-entry-sheet-v1.png", 5, 3), neutral)
    working = align_cells(extract_native_cells(sources / "work-loop-sheet-v1.png", 4, 3), neutral)
    busy = align_cells(extract_native_cells(sources / "busy-sheet-v1.png", 5, 3), neutral)
    work_anchor, busy_anchor = working[0], busy[4]
    clips = {
        "WorkEnter": (3.5, [neutral, entry[1], entry[2], entry[3], entry[4], work_anchor],
                      (0, 35, 74, 112, 157, 210)),
        "WorkLoop": (2.0, [work_anchor, *working[1:11], work_anchor],
                     (0, 10, 20, 30, 40, 50, 60, 70, 80, 92, 106, 120)),
        "WorkToBusy": (1.5, [work_anchor, busy[0], busy[1], busy[2], busy[3], busy_anchor],
                       (0, 18, 36, 54, 72, 90)),
        "BusyLoop": (2.0, [busy_anchor, *busy[5:14], busy_anchor],
                     (0, 10, 20, 30, 40, 52, 64, 76, 88, 104, 120)),
        "WorkExit": (2.0, [work_anchor, entry[3], entry[13], neutral], (0, 38, 80, 120)),
        "BusyExit": (2.0, [busy_anchor, busy[2], busy[0], entry[3], entry[13], neutral],
                     (0, 20, 42, 66, 92, 120)),
    }
    prepared = {}
    for name, (duration, frames, indices) in clips.items():
        directory = assets / "AnimationKeys" / name
        directory.mkdir(parents=True, exist_ok=True)
        for index, frame in enumerate(frames):
            frame.save(directory / f"key-{index:02d}.png", optimize=True)
        spec = pipeline.ClipSpec(name=name, sheet_name="work-v1-layered", duration_seconds=duration,
                                 columns=len(frames), rows=1, source_pose_count=len(frames),
                                 authored_frame_indices=indices, segmentwise_interpolation=True)
        pipeline.make_key_contact_sheet(directory, len(frames), assets / "AnimationPreviews" / f"{name}-keys-dark.png")
        prepared[name] = (spec, frames, directory)
    prepare_props(assets, work_anchor, busy_anchor)
    return prepared


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--rife", type=Path)
    parser.add_argument("--rife-model", type=Path)
    parser.add_argument("--keys-only", action="store_true")
    parser.add_argument("--clips", nargs="*")
    args = parser.parse_args()
    assets = args.assets.resolve(strict=True)
    print(f"Exact work-v1 output root: {assets}", flush=True)
    neutral_path = assets / "mascot-animated-neutral.png"
    neutral_hash = hashlib.sha256(neutral_path.read_bytes()).hexdigest()
    neutral = Image.open(neutral_path).convert("RGBA")
    work_asset_acceleration.enable(pipeline)
    pipeline.measure_root_anchor = measure_eat_root
    clips = prepare_keys(assets, neutral)
    if args.keys_only:
        return
    if args.rife is None or args.rife_model is None:
        raise SystemExit("Provide --rife and --rife-model for continuous frame generation")
    summary = {}
    for name, (spec, keys, key_dir) in clips.items():
        if args.clips and name not in args.clips:
            continue
        output = assets / "Animations" / name
        count, qa = pipeline.run_rife(args.rife.resolve(strict=True), args.rife_model.resolve(strict=True),
                                      key_dir, output, spec, neutral,
                                      exact_endpoints=(keys[0], keys[-1]))
        frames, _, _ = pipeline.load_frame_sequence(output)
        hashes = [hashlib.sha256(frame.tobytes()).hexdigest() for frame in frames]
        qa["temporal_sampling"] = {"unique_pixel_frames": len(set(hashes)),
                                   "adjacent_duplicate_indexes": [i for i in range(1, len(hashes)) if hashes[i] == hashes[i-1]]}
        qa["provenance"] = {"generation": "built-in imagegen", "source_version": "work-v1",
                            "method": "authored pose keys + paired RIFE RGB/alpha + planted-foot registration",
                            "props": "separate raster layers, no optical-flow distortion"}
        pipeline.write_qa_report(assets / "AnimationPreviews" / f"{name}-qa.json", qa)
        pipeline.make_dark_background_contact_sheet(output, count, assets / "AnimationPreviews" / f"{name}-dark.png")
        pipeline.make_60fps_gif(output, count, assets / "AnimationPreviews" / f"{name}-60fps.gif")
        summary[name] = {"count": count, "passed": qa["passed"], "unique_pixel_frames": len(set(hashes))}
        print(f"{name} complete: {summary[name]}", flush=True)
    if hashlib.sha256(neutral_path.read_bytes()).hexdigest() != neutral_hash:
        raise RuntimeError("Canonical neutral changed unexpectedly")
    print(json.dumps(summary), flush=True)


if __name__ == "__main__":
    main()
