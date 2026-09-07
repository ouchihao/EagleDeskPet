"""Encode the WPF-produced diagnostic frames with portable GIF timing.

Some Windows WIC GIF encoders silently discard frame-delay metadata. This is a
format/timing conversion of existing captured frames, not artwork generation.
The raw WPF GIF is retained as input evidence; a separate preview is emitted.
"""
from pathlib import Path
import argparse
from PIL import Image, ImageSequence

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--input", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
source = args.input.resolve(strict=True)
target = args.output.resolve()
if source == target:
    raise SystemExit("Use a distinct output so the raw WPF capture is preserved.")
print(f"Exact timing-conversion target: {target}")
with Image.open(source) as image:
    frames = [frame.convert("RGB") for frame in ImageSequence.Iterator(image)]
frames[0].save(target, save_all=True, append_images=frames[1:], duration=50, loop=0, optimize=False)
with Image.open(target) as verified:
    assert verified.n_frames == len(frames)
    for index in range(verified.n_frames):
        verified.seek(index)
        assert verified.info.get("duration") == 50
print(f"Verified {len(frames)} frames at 50 ms each ({len(frames) / 20:.2f}s).")
