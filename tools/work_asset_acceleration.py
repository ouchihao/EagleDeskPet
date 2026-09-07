"""Optional build-only acceleration preserving the existing component contract.

SciPy is installed only under .tool-cache/work-animation-python, never added to
the pet EXE or machine-wide Python. Without it, the established implementation
is used unchanged. A deterministic equivalence test gates activation.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np


def enable(pipeline) -> bool:
    optional = Path(__file__).resolve().parents[1] / ".tool-cache/work-animation-python"
    if not (optional / "scipy").is_dir():
        return False
    sys.path.insert(0, str(optional))
    from scipy import ndimage

    original = pipeline.label_alpha_components

    def fast(alpha):
        # ndimage's default cross structure is exactly four-neighbor
        # connectivity, matching the old BFS (not diagonal/eight-neighbor).
        labels, count = ndimage.label(alpha >= 10)
        result = []
        width = alpha.shape[1]
        for identifier, bounds in enumerate(ndimage.find_objects(labels), 1):
            if bounds is None:
                continue
            ys, xs = np.where(labels[bounds] == identifier)
            ys, xs = ys + bounds[0].start, xs + bounds[1].start
            indices = ys.astype(np.int64) * width + xs
            result.append(pipeline.AlphaComponent(
                flat_indices=indices, area=len(indices), center_x=float(xs.mean()),
                center_y=float(ys.mean()), minimum_x=int(xs.min()), minimum_y=int(ys.min()),
                maximum_x=int(xs.max()), maximum_y=int(ys.max())))
        return result

    rng = np.random.default_rng(602)
    samples = [np.zeros((9, 13), dtype=np.uint8), np.full((12, 8), 255, dtype=np.uint8)]
    samples.extend(rng.integers(0, 24, (35, 39), dtype=np.uint8) for _ in range(12))
    for sample in samples:
        expected, actual = original(sample), fast(sample)
        if len(expected) != len(actual):
            raise RuntimeError("Accelerated labeling changed component count")
        for left, right in zip(expected, actual, strict=True):
            if (not np.array_equal(np.sort(left.flat_indices), np.sort(right.flat_indices))
                or (left.area, left.center_x, left.center_y, left.minimum_x, left.minimum_y,
                    left.maximum_x, left.maximum_y) !=
                   (right.area, right.center_x, right.center_y, right.minimum_x, right.minimum_y,
                    right.maximum_x, right.maximum_y)):
                raise RuntimeError("Accelerated labeling differs from existing alpha semantics")
    pipeline.label_alpha_components = fast
    print("Optional component accelerator: 14 exact equivalence tests passed", flush=True)
    return True
