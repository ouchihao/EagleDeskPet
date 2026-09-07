"""Read-only authored-frame QA plus a runtime-layer motion preview."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

import prepare_generated_sheets as pipeline
from prepare_care_animation import measure_eat_root


EXPECTED = {"WorkEnter": 211, "WorkLoop": 121, "WorkToBusy": 91,
            "BusyLoop": 121, "WorkExit": 121, "BusyExit": 121}


def same(first: Image.Image, second: Image.Image) -> bool:
    return np.array_equal(np.asarray(first), np.asarray(second))


def ease(value: float) -> float:
    t = max(0.0, min(1.0, value))
    return t * t * (3.0 - 2.0 * t)


def offsets(name: str, seconds: float) -> tuple[int, int]:
    # Mirror production WorkStageMotion.Sample (2026-09-05). The preview is
    # quantized to source pixels; production WPF retains subpixel translation.
    def unit(value: float) -> float:
        return max(0.0, min(1.0, value))
    if name == "WorkEnter":
        table = unit((seconds - 0.15) / 0.85)
        desk_x = round(-440 * (1.0 - table) ** 3)
        t = unit((seconds - 0.9) / 0.9)
        if t < 0.65:
            laptop_y = round(-440 * (1.0 - (t / 0.65) ** 2))
        elif t < 0.84:
            laptop_y = round(-14 * math.sin(math.pi * (t - 0.65) / 0.19))
        else:
            laptop_y = round(-4 * math.sin(math.pi * (t - 0.84) / 0.16))
        return desk_x, laptop_y
    if name in ("WorkExit", "BusyExit"):
        lift = unit((seconds - 0.35) / 0.8)
        table = unit((seconds - 0.9) / 1.05)
        return round(-440 * table ** 3), round(-440 * lift ** 3)
    return 0, 0


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    args = parser.parse_args()
    assets = args.assets.resolve(strict=True)
    preview = assets / "AnimationPreviews"
    print(f"Exact output targets: {preview / 'Work-scene-qa.json'}; {preview / 'Work-scene-demo.gif'}", flush=True)
    neutral = Image.open(assets / "mascot-animated-neutral.png").convert("RGBA")
    clips = {}
    report = {"clips": {}, "seams": {}, "passed": True}
    target_root = measure_eat_root(neutral)
    for name, count in EXPECTED.items():
        paths = sorted((assets / "Animations" / name).glob("frame-*.png"))
        if len(paths) != count:
            raise RuntimeError(f"{name}: expected {count} complete frames, got {len(paths)}")
        frames = [Image.open(path).convert("RGBA") for path in paths]
        hashes = [hashlib.sha256(frame.tobytes()).hexdigest() for frame in frames]
        roots = [measure_eat_root(frame) for frame in frames]
        adjacent_mae = [pipeline._premultiplied_mae(a, b) for a, b in zip(frames, frames[1:])]
        report["clips"][name] = {
            "frames": count, "unique_frames": len(set(hashes)),
            "adjacent_duplicates": [i for i in range(1, count) if hashes[i] == hashes[i - 1]],
            "maximum_foot_x_error": max(abs(item.torso_x - target_root.torso_x) for item in roots),
            "maximum_foot_y_error": max(abs(item.foot_contact_y - target_root.foot_contact_y) for item in roots),
            "maximum_adjacent_premultiplied_mae": max(adjacent_mae),
        }
        clips[name] = frames
    seams = {
        "neutral_to_enter": (neutral, clips["WorkEnter"][0]),
        "enter_to_work": (clips["WorkEnter"][-1], clips["WorkLoop"][0]),
        "work_loop": (clips["WorkLoop"][-1], clips["WorkLoop"][0]),
        "work_to_transition": (clips["WorkLoop"][-1], clips["WorkToBusy"][0]),
        "transition_to_busy": (clips["WorkToBusy"][-1], clips["BusyLoop"][0]),
        "busy_loop": (clips["BusyLoop"][-1], clips["BusyLoop"][0]),
        "work_to_exit": (clips["WorkLoop"][-1], clips["WorkExit"][0]),
        "busy_to_exit": (clips["BusyLoop"][-1], clips["BusyExit"][0]),
        "work_exit_to_neutral": (clips["WorkExit"][-1], neutral),
        "busy_exit_to_neutral": (clips["BusyExit"][-1], neutral),
    }
    report["seams"] = {name: same(*pair) for name, pair in seams.items()}
    report["passed"] = all(report["seams"].values()) and all(
        not item["adjacent_duplicates"] and item["maximum_foot_x_error"] <= 1.0
        and item["maximum_foot_y_error"] <= 0.5 for item in report["clips"].values())
    (preview / "Work-scene-qa.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    if not report["passed"]:
        print(json.dumps(report, indent=2))
        raise RuntimeError("Scene seam, root, or temporal QA failed")
    props = assets / "SceneProps/Work"
    rear, front, laptop = [Image.open(props / file).convert("RGBA")
                           for file in ("desk-back.png", "desk-front.png", "laptop.png")]
    fire_paths = sorted((props / "Fire").glob("frame-*.png"))
    if len(fire_paths) != 121:
        raise RuntimeError("The complete 121-frame busy flame loop is required")
    fire_frames = [Image.open(path).convert("RGBA") for path in fire_paths]
    if not same(fire_frames[0], fire_frames[-1]):
        raise RuntimeError("Busy flame loop endpoint is not pixel-exact")
    snapshots = []
    gif_frames = []
    fire_clock = 0
    for name in ("WorkEnter", "WorkLoop", "WorkToBusy", "BusyLoop", "BusyExit"):
        for index, eagle in enumerate(clips[name][:-1]):
            scene = Image.new("RGBA", pipeline.CANVAS_SIZE)
            desk_x, laptop_y = offsets(name, index / 60.0)
            if name in ("WorkToBusy", "BusyLoop", "BusyExit"):
                seconds = index / 60.0
                growth = ease((seconds - 0.25) / 1.05) if name == "WorkToBusy" else (
                    1.0 - ease(seconds / 0.85) if name == "BusyExit" else 1.0)
                if growth > 0.0:
                    fire = fire_frames[fire_clock % 120]
                    height = max(1, round(346 * growth))
                    fire = fire.resize((384, height), Image.Resampling.LANCZOS)
                    scene.alpha_composite(fire, (0, round(334 * (1.0 - growth))))
                fire_clock += 1
            scene.alpha_composite(rear, (desk_x, 0))
            scene.alpha_composite(eagle)
            scene.alpha_composite(front, (desk_x, 0))
            scene.alpha_composite(laptop, (0, laptop_y))
            if name == "BusyLoop" and index == 30:
                full_preview = Image.new("RGB", scene.size, (18, 21, 28))
                full_preview.paste(scene, (0, 0), scene)
                full_preview.save(preview / "Busy-scene-fire-dark.png")
            display = pipeline.fit_frame_to_display(scene, background=None)
            background = Image.new("RGB", display.size, (18, 21, 28))
            background.paste(display, (0, 0), display)
            gif_frames.append(background)
            if index in (0, len(clips[name]) // 3, 2 * len(clips[name]) // 3, len(clips[name]) - 2):
                snapshots.append((f"{name} {index}", background))
    gif_frames[0].save(preview / "Work-scene-demo.gif", save_all=True, append_images=gif_frames[1:],
                       duration=[20 if index % 3 != 2 else 10 for index in range(len(gif_frames))],
                       loop=0, optimize=False)
    contact = Image.new("RGB", (160 * 4, 194 * 5), (18, 21, 28))
    draw = ImageDraw.Draw(contact)
    for index, (label, thumbnail) in enumerate(snapshots):
        x, y = index % 4 * 160, index // 4 * 194
        contact.paste(thumbnail, (x, y + 20))
        draw.text((x + 2, y + 3), label, fill=(110, 220, 230))
    contact.save(preview / "Work-scene-demo-dark.png")
    print(json.dumps(report, indent=2), flush=True)


if __name__ == "__main__":
    main()
