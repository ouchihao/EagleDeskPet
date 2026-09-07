"""Generate the independent ImageGen-authored, 60 Hz busy-flame layer."""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path

import numpy as np
from PIL import Image

import prepare_generated_sheets as pipeline
import work_asset_acceleration


def fire_root(image: Image.Image) -> pipeline.RootAnchor:
    a = np.asarray(image)[:, :, 3]
    ys, xs = np.where(a >= 32)
    bottom = int(ys.max())
    low_y, low_x = np.where((a >= 32) & (np.arange(image.height)[:, None] >= bottom - 12))
    return pipeline.RootAnchor(float((low_x.min() + low_x.max()) / 2.0), float(bottom), len(low_x), len(low_y))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--rife", type=Path, required=True)
    parser.add_argument("--rife-model", type=Path, required=True)
    args = parser.parse_args()
    assets = args.assets.resolve(strict=True)
    source = assets / "AnimationSources/busy-fire-sheet-v1.png"
    output = assets / "SceneProps/Work/Fire"
    key_dir = assets / "AnimationKeys/BusyFire"
    print(f"Exact output targets: {output}; {key_dir}", flush=True)
    work_asset_acceleration.enable(pipeline)
    source_image = Image.open(source).convert("RGBA")
    rgba = np.array(source_image)
    # The generator baked checker pixels into this RGB sheet, including the
    # large enclosed hollow. For a strictly red/yellow effect, the known hue
    # palette safely identifies all background, including that interior hole.
    red, green, blue = [rgba[:, :, c].astype(np.int16) for c in range(3)]
    alpha = np.where((red >= 160) & (red - blue >= 95) & (red - green >= 12), 255, 0).astype(np.uint8)
    rgba[:, :, 3] = np.minimum(rgba[:, :, 3], alpha)
    components = sorted(pipeline.label_alpha_components(rgba[:, :, 3]), key=lambda c: c.area, reverse=True)[:4]
    components.sort(key=lambda c: c.center_y)
    ordered = sorted(components[:2], key=lambda c: c.center_x) + sorted(components[2:], key=lambda c: c.center_x)
    if len(ordered) != 4:
        raise RuntimeError("Expected four generated connected flame poses")
    frames = []
    for item in ordered:
        ys, xs = np.divmod(item.flat_indices, source_image.width)
        crop = np.zeros((item.maximum_y - item.minimum_y + 1, item.maximum_x - item.minimum_x + 1, 4), dtype=np.uint8)
        crop[ys - item.minimum_y, xs - item.minimum_x] = rgba[ys, xs]
        art = pipeline.add_rgb_edge_bleed(Image.fromarray(crop), iterations=12)
        art = art.resize((258, 282), Image.Resampling.LANCZOS)
        canvas = Image.new("RGBA", pipeline.CANVAS_SIZE)
        canvas.alpha_composite(art, (63, 53))
        frames.append(pipeline.add_rgb_edge_bleed(pipeline.selective_matte_defringe(canvas)))
    frames.append(frames[0].copy())
    pipeline.measure_root_anchor = fire_root
    frames, _ = pipeline.stabilize_frames(frames, frames[0], exact_endpoints=(frames[0], frames[0]))
    key_dir.mkdir(parents=True, exist_ok=True)
    for index, frame in enumerate(frames):
        frame.save(key_dir / f"key-{index:02d}.png", optimize=True)
    spec = pipeline.ClipSpec(name="BusyFire", sheet_name=source.name, duration_seconds=2.0,
                             columns=5, rows=1, source_pose_count=5,
                             authored_frame_indices=(0, 30, 60, 90, 120), segmentwise_interpolation=True)
    count, qa = pipeline.run_rife(args.rife.resolve(strict=True), args.rife_model.resolve(strict=True), key_dir,
                                  output, spec, frames[0], exact_endpoints=(frames[0], frames[0]))
    final, _, _ = pipeline.load_frame_sequence(output)
    hashes = [hashlib.sha256(frame.tobytes()).hexdigest() for frame in final]
    qa["temporal_sampling"] = {"unique_pixel_frames": len(set(hashes)),
                               "adjacent_duplicate_indexes": [i for i in range(1, count) if hashes[i] == hashes[i-1]]}
    qa["provenance"] = {"generator": "built-in imagegen", "source": source.name,
                        "layer": "behind desk and character", "effect_growth_anchor": [192, 334],
                        "source_background_method": "known red/yellow palette, including hollow center",
                        "loop": "exact endpoint, continuous RGB and alpha optical flow"}
    pipeline.write_qa_report(assets / "AnimationPreviews/BusyFire-qa.json", qa)
    pipeline.make_dark_background_contact_sheet(output, count, assets / "AnimationPreviews/BusyFire-dark.png")
    pipeline.make_60fps_gif(output, count, assets / "AnimationPreviews/BusyFire-60fps.gif")
    print(f"BusyFire: {count} frames, {len(set(hashes))} unique, QA passed {qa['passed']}", flush=True)


if __name__ == "__main__":
    main()
