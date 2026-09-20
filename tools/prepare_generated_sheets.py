"""Turn ImageGen pose sheets into aligned, transparent 60 fps sprite clips.

Source-sheet dimensions are configured per clip.  Background removal is a
border-connected matte operation: light neutral pixels connected to a cell's
edge are treated as generated white/checkerboard background, while the white
head stays intact because the mascot outline encloses it.
"""

from __future__ import annotations

import argparse
from collections import deque
from dataclasses import dataclass, replace
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

import numpy as np
from PIL import Image, ImageDraw


@dataclass(frozen=True)
class ClipSpec:
    name: str
    sheet_name: str
    duration_seconds: float
    columns: int = 5
    rows: int = 2
    detached_component_min_area: int = 0
    allow_detached_edge_contact: bool = False
    detached_edge_contact_after_frame: int | None = None
    detached_components_right_only: bool = False
    detached_motion_monotonic_right: bool = False
    smoke_pose_index: int | None = None
    authored_order: tuple[int, ...] | None = None
    source_pose_count: int | None = None
    supplemental_sheet_name: str | None = None
    supplemental_columns: int = 0
    supplemental_rows: int = 0
    supplemental_pose_count: int = 0
    supplemental_insert_after_index: int | None = None
    derive_release_mid_after_index: int | None = None
    authored_frame_indices: tuple[int, ...] | None = None
    lock_authored_frames: bool = False
    segmentwise_interpolation: bool = False
    layered_reference_sheet_name: str | None = None
    layered_midpoint_sheet_name: str | None = None
    detached_right_only_after_frame: int | None = None
    detached_motion_after_frame: int | None = None
    authored_root_calibration: bool = False

    @property
    def uses_layered_bomb_pipeline(self) -> bool:
        return self.layered_reference_sheet_name is not None

    @property
    def source_count(self) -> int:
        return self.source_pose_count or (self.columns * self.rows)

    @property
    def pose_count(self) -> int:
        return self.source_count + self.supplemental_pose_count + (
            1 if self.derive_release_mid_after_index is not None else 0
        )


@dataclass(frozen=True)
class RootAnchor:
    """A body-root observation that ignores expressive wings and props."""

    torso_x: float
    foot_contact_y: float
    torso_sample_count: int
    foot_sample_count: int


@dataclass(frozen=True)
class AlphaComponent:
    flat_indices: np.ndarray
    area: int
    center_x: float
    center_y: float
    minimum_x: int
    minimum_y: int
    maximum_x: int
    maximum_y: int


@dataclass
class LayeredBombBundle:
    body_key_dir: Path
    body_keys: list[Image.Image]
    reference_keys: list[Image.Image]
    midpoint_references: list[Image.Image]
    prop_sprite: Image.Image
    prop_gray_anchor: tuple[float, float]
    prop_control_centers: dict[int, tuple[float, float]]
    smoke_layer: Image.Image
    foreground_key_masks: dict[int, Image.Image]
    composite_keys: list[Image.Image]
    report: dict[str, object]

CLIPS = (
    ClipSpec("Yawn", "yawn-sheet-v2.png", 2.0, 5, 3),
    ClipSpec("Shy", "shy-sheet-v2.png", 2.0, 5, 3),
    ClipSpec("SideEye", "sideeye-sheet-v2.png", 2.0, 5, 3),
    # The detached bomb and smoke puffs are intentional; keep sizeable
    # disconnected alpha components after RIFE reconstruction. Detached props
    # must remain on the right and a thrown bomb may only travel right/disappear.
    ClipSpec(
        name="Bomb",
        sheet_name="bomb-body-sheet-v1.png",
        duration_seconds=2.0,
        columns=5,
        rows=3,
        detached_component_min_area=24,
        allow_detached_edge_contact=True,
        # key-07 lands exactly on frame 60.  The bomb is still held through
        # that pose, so every visible pixel needs normal character clearance.
        # Only the released prop/smoke may contact the right edge afterwards.
        detached_edge_contact_after_frame=64,
        detached_components_right_only=True,
        detached_motion_monotonic_right=True,
        detached_right_only_after_frame=60,
        detached_motion_after_frame=60,
        smoke_pose_index=12,
        # ImageGen laid the correct local prop offsets across a row wrap in a
        # non-monotonic sheet order.  Reorder whole authored poses (body and
        # every assigned component) before stabilization/interpolation.
        authored_order=(0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 9, 12, 13, 14),
        source_pose_count=15,
        authored_frame_indices=(
            0, 8, 16, 24, 32, 40, 48, 60, 68, 72, 76, 80, 88, 104, 120,
        ),
        lock_authored_frames=True,
        segmentwise_interpolation=True,
        layered_reference_sheet_name="bomb-sheet-v3.png",
        layered_midpoint_sheet_name="bomb-swing-midpoints.png",
    ),
)

CANVAS_SIZE = (384, 346)
DISPLAY_PREVIEW_SIZE = (160, 174)
TARGET_NEUTRAL_HEIGHT = 252
TARGET_BASELINE_Y = 337
ROOT_ALPHA_THRESHOLD = 32
ROOT_X_TOLERANCE_PIXELS = 4.0
ROOT_Y_TOLERANCE_PIXELS = 1.5
MINIMUM_CLEAR_BORDER_FRACTION = 0.95
MINIMUM_VISIBLE_MARGIN_PIXELS = 8
RIFE_MATTE_RGB = np.asarray((248.0, 248.0, 248.0), dtype=np.float32)
HALO_ALPHA_THRESHOLD = 10
HALO_NEUTRAL_MINIMUM = 185
HALO_NEUTRAL_CHROMA_MAXIMUM = 45
HALO_MAX_VISIBLE_PIXELS_PER_FRAME = 16
HALO_MAX_VISIBLE_EDGE_FRACTION = 0.025
HALO_MAX_NEUTRAL_BRIGHT_PIXELS_PER_FRAME = 4
HALO_MAX_UNSUPPORTED_LOW_ALPHA_PIXELS_PER_FRAME = 2
HALO_MAX_LOW_ALPHA_DISTANCE_TO_OPAQUE = 2
HALO_EDGE_COLOR_RESIDUAL_MAXIMUM = 48.0
HALO_EDGE_COLOR_SEARCH_RADIUS = 4
SOURCE_TRAPPED_MATTE_MAX_RELATIVE_AREA = 0.02
SOURCE_TRAPPED_MATTE_MAX_BACKGROUND_DISTANCE = 10
FINAL_MATTE_ALPHA_MINIMUM = 200
FINAL_MATTE_NEUTRAL_MINIMUM = 200
FINAL_MATTE_CHROMA_MAXIMUM = 45
FINAL_MATTE_MAX_RELATIVE_AREA = 0.02
FINAL_MATTE_TRANSPARENT_SHELL_DISTANCE = 2
FINAL_MATTE_INPAINT_RADIUS = 6
PALE_FRINGE_ALPHA_MINIMUM = 96
PALE_FRINGE_MINIMUM_RED = 210
PALE_FRINGE_MINIMUM_GREEN = 160
PALE_FRINGE_MINIMUM_BLUE = 120
PALE_FRINGE_MINIMUM_RED_BLUE_DELTA = 40
PALE_FRINGE_MINIMUM_RED_GREEN_DELTA = 15
PALE_FRINGE_MINIMUM_COMPONENT_AREA = 8
PALE_FRINGE_BODY_MINIMUM_Y_FRACTION = 0.58
PALE_FRINGE_INPAINT_RADIUS = 10
HALO_DARK_BACKGROUNDS = {
    "black": (0, 0, 0),
    "dark_12151c": (18, 21, 28),
}
BOMB_RIGHT_SIDE_MINIMUM_OFFSET = 4.0
BOMB_MONOTONIC_BACKTRACK_TOLERANCE = 2.0
BOMB_REAPPEARANCE_GAP_FRAMES = 0
BOMB_DARK_COMPONENT_MEDIAN_LUMA_MAXIMUM = 110.0
BOMB_PROP_ZONE_START_OFFSET = 90.0
BOMB_PROP_GRAY_SEED_OFFSET = 65.0
BOMB_PROP_GRAY_SUPPORT_RADIUS = 12
BOMB_THROW_TRACK_START_OFFSET = 90.0
BOMB_THROW_EDGE_EXIT_X = CANVAS_SIZE[0] - 2
BOMB_THROW_GRAY_CHROMA_MAXIMUM = 35
BOMB_THROW_GRAY_LUMA_MAXIMUM = 150.0
BOMB_THROW_COMPONENT_MINIMUM_AREA = 100
BOMB_THROW_WARM_SUPPORT_MINIMUM = 20
BOMB_THROW_MINIMUM_RIGHTWARD_PROGRESS = 8
BOMB_DETACHED_EDGE_ALLOWED_AFTER_KEY_INDEX = 8
BOMB_FIRST_RELEASE_FULL_KEY_INDEX = 8
BOMB_FIRST_RELEASE_FULL_KEY_RIGHT_MARGIN = 7
BOMB_DETACHED_THROW_KEY_INDICES = (8, 9, 10)
BOMB_DISAPPEARED_KEY_INDEX = 11
BOMB_RELEASE_MID_GAP_PIXELS = 8
BOMB_LAYER_CONTROL_FRAMES = (0, 8, 16, 24, 32, 40, 48, 52, 56, 60, 64, 68, 72, 76, 80, 88, 104, 120)
BOMB_PROP_RELEASE_FRAME = 60
BOMB_PROP_GONE_FRAME = 80
BOMB_SMOKE_PEAK_FRAME = 88
BOMB_SMOKE_END_FRAME = 98
BOMB_PROP_CONTROL_SHIFTS = {
    64: (-24, 0),
    68: (0, 0),
    72: (24, 4),
    76: (48, 8),
    80: (72, 12),
}


def _adjacent_to(mask: np.ndarray, *, diagonals: bool = True) -> np.ndarray:
    adjacent = np.zeros_like(mask, dtype=bool)
    adjacent[1:, :] |= mask[:-1, :]
    adjacent[:-1, :] |= mask[1:, :]
    adjacent[:, 1:] |= mask[:, :-1]
    adjacent[:, :-1] |= mask[:, 1:]
    if diagonals:
        adjacent[1:, 1:] |= mask[:-1, :-1]
        adjacent[1:, :-1] |= mask[:-1, 1:]
        adjacent[:-1, 1:] |= mask[1:, :-1]
        adjacent[:-1, :-1] |= mask[1:, 1:]
    return adjacent


def border_connected_background(rgb: np.ndarray) -> np.ndarray:
    """Return a mask for the baked white/checker matte around the mascot."""

    minimum = rgb.min(axis=2)
    maximum = rgb.max(axis=2)
    chroma = maximum.astype(np.int16) - minimum.astype(np.int16)
    # Includes the generated checkerboard and neutral cast-shadow haze.  Brown
    # and orange outline pixels remain foreground because their chroma is high.
    # The generated matte ranges from white to pale checker/shadow grays.  A
    # lower luminance threshold catches those edge-connected cells, while the
    # chroma limit protects the orange/brown outline and yellow feet.
    candidate = (minimum >= 185) & (chroma <= 52)
    height, width = candidate.shape
    connected = np.zeros((height, width), dtype=bool)
    queue: deque[tuple[int, int]] = deque()

    def seed(y: int, x: int) -> None:
        if candidate[y, x] and not connected[y, x]:
            connected[y, x] = True
            queue.append((y, x))

    for x in range(width):
        seed(0, x)
        seed(height - 1, x)
    for y in range(height):
        seed(y, 0)
        seed(y, width - 1)

    while queue:
        y, x = queue.popleft()
        if y > 0:
            seed(y - 1, x)
        if y + 1 < height:
            seed(y + 1, x)
        if x > 0:
            seed(y, x - 1)
        if x + 1 < width:
            seed(y, x + 1)

    return connected


def enclosed_source_matte_pockets(
    rgb: np.ndarray,
    border_background: np.ndarray,
) -> np.ndarray:
    """Find checker/matte pockets trapped just inside generated outlines.

    ImageGen can draw a brown stroke around a tiny patch of the baked checker.
    That patch is no longer border-connected, so the primary flood fill alone
    mistakes it for opaque art. Real white head regions are orders of magnitude
    larger and are retained; only tiny enclosed neutral components lying within
    ten source pixels of known exterior matte are removed.
    """

    minimum = rgb.min(axis=2)
    chroma = rgb.max(axis=2).astype(np.int16) - minimum.astype(np.int16)
    neutral_candidate = (minimum >= 185) & (chroma <= 52)
    enclosed = neutral_candidate & ~border_background
    components = label_alpha_components(
        np.where(enclosed, 255, 0).astype(np.uint8)
    )
    trapped = np.zeros_like(enclosed)
    if not components:
        return trapped

    largest_area = max(component.area for component in components)
    maximum_area = max(
        1,
        int(largest_area * SOURCE_TRAPPED_MATTE_MAX_RELATIVE_AREA),
    )
    near_background = border_background.copy()
    for _ in range(SOURCE_TRAPPED_MATTE_MAX_BACKGROUND_DISTANCE):
        near_background |= _adjacent_to(near_background)

    width = enclosed.shape[1]
    trapped_flat = trapped.ravel()
    near_flat = near_background.ravel()
    for component in components:
        if component.area > maximum_area:
            continue
        if np.any(near_flat[component.flat_indices]):
            trapped_flat[component.flat_indices] = True
    return trapped


def inspect_source_matte_cleanup(sheet_path: Path) -> dict[str, object]:
    """Describe every source-sheet component removed as trapped matte."""

    with Image.open(sheet_path) as opened:
        rgb = np.asarray(opened.convert("RGB"), dtype=np.uint8)
    border_background = border_connected_background(rgb)
    trapped = enclosed_source_matte_pockets(rgb, border_background)
    height, width = trapped.shape
    components: list[dict[str, object]] = []
    for component in label_alpha_components(
        np.where(trapped, 255, 0).astype(np.uint8)
    ):
        ys = component.flat_indices // width
        xs = component.flat_indices % width
        components.append(
            {
                "area": component.area,
                "bounds": [
                    component.minimum_x,
                    component.minimum_y,
                    component.maximum_x,
                    component.maximum_y,
                ],
                "mean_rgb": [
                    round(float(value), 3)
                    for value in np.mean(rgb[ys, xs], axis=0)
                ],
            }
        )
    components.sort(key=lambda item: int(item["area"]), reverse=True)
    return {
        "source": str(sheet_path),
        "removed_pixel_count": int(np.sum(trapped)),
        "removed_component_count": len(components),
        "maximum_relative_area": SOURCE_TRAPPED_MATTE_MAX_RELATIVE_AREA,
        "maximum_background_distance": SOURCE_TRAPPED_MATTE_MAX_BACKGROUND_DISTANCE,
        "components": components,
    }


def extract_cells(
    sheet_path: Path,
    columns: int,
    rows: int,
    cell_count: int | None = None,
    detached_component_min_area: int = 0,
) -> list[Image.Image]:
    if columns <= 0 or rows <= 0:
        raise ValueError("sheet grid dimensions must be positive")
    total_cells = columns * rows
    count = total_cells if cell_count is None else cell_count
    if not 0 < count <= total_cells:
        raise ValueError(
            f"cell count must be in [1, {total_cells}], got {count}"
        )
    with Image.open(sheet_path) as opened:
        sheet = opened.convert("RGB")
    width, height = sheet.size
    rgb = np.asarray(sheet, dtype=np.uint8)
    background = border_connected_background(rgb)
    background |= enclosed_source_matte_pockets(rgb, background)
    alpha = np.where(background, 0, 255).astype(np.uint8)
    components = sorted(
        label_alpha_components(alpha),
        key=lambda component: component.area,
        reverse=True,
    )
    if len(components) < count:
        raise RuntimeError(
            f"{sheet_path.name}: found only {len(components)} foreground "
            f"components for {count} authored poses"
        )

    # ImageGen does not keep characters inside mathematically equal grid cells:
    # poses overlap the nominal row cuts, and a flying prop can cross a column
    # cut. Select the largest component for each body, order bodies row-major,
    # and assign every detached prop to the nearest body on its left. This keeps
    # each source component in exactly one pose and prevents cell-wrap ghosts.
    body_components = components[:count]
    bodies_by_y = sorted(body_components, key=lambda component: component.center_y)
    row_groups: list[list[AlphaComponent]] = []
    consumed = 0
    for _ in range(rows):
        row_count = min(columns, count - consumed)
        if row_count <= 0:
            break
        row = sorted(
            bodies_by_y[consumed : consumed + row_count],
            key=lambda component: component.center_x,
        )
        row_groups.append(row)
        consumed += row_count
    if consumed != count:
        raise RuntimeError(
            f"{sheet_path.name}: grid {columns}x{rows} cannot contain {count} poses"
        )
    ordered_bodies = [component for row in row_groups for component in row]

    assigned: dict[int, list[AlphaComponent]] = {
        id(component): [component] for component in ordered_bodies
    }
    if detached_component_min_area > 0:
        body_ids = {id(component) for component in body_components}
        row_centers = [float(np.median([body.center_y for body in row])) for row in row_groups]
        for component in components:
            if (
                id(component) in body_ids
                or component.area < detached_component_min_area
            ):
                continue
            row_index = min(
                range(len(row_groups)),
                key=lambda index: abs(component.center_y - row_centers[index]),
            )
            row = row_groups[row_index]
            bodies_on_left = [
                body for body in row if body.center_x <= component.center_x
            ]
            owner = (
                max(bodies_on_left, key=lambda body: body.center_x)
                if bodies_on_left
                else min(row, key=lambda body: abs(body.center_x - component.center_x))
            )
            assigned[id(owner)].append(component)

    padding = 12
    maximum_left = 0.0
    maximum_right = 0.0
    maximum_above = 0.0
    maximum_below = 0.0
    for body in ordered_bodies:
        for component in assigned[id(body)]:
            maximum_left = max(maximum_left, body.center_x - component.minimum_x)
            maximum_right = max(maximum_right, component.maximum_x - body.center_x)
            maximum_above = max(maximum_above, body.maximum_y - component.minimum_y)
            maximum_below = max(maximum_below, component.maximum_y - body.maximum_y)
    anchor_x = int(np.ceil(maximum_left)) + padding
    baseline_y = int(np.ceil(maximum_above)) + padding
    cell_width = anchor_x + int(np.ceil(maximum_right)) + padding + 1
    cell_height = baseline_y + int(np.ceil(maximum_below)) + padding + 1
    sheet_rgba = np.dstack((rgb, alpha))
    cells: list[Image.Image] = []
    for body in ordered_bodies:
        cell = np.zeros((cell_height, cell_width, 4), dtype=np.uint8)
        offset_x = int(round(anchor_x - body.center_x))
        offset_y = baseline_y - body.maximum_y
        for component in assigned[id(body)]:
            source_y = component.flat_indices // width
            source_x = component.flat_indices % width
            target_x = source_x + offset_x
            target_y = source_y + offset_y
            if (
                np.any(target_x < 0)
                or np.any(target_x >= cell_width)
                or np.any(target_y < 0)
                or np.any(target_y >= cell_height)
            ):
                raise RuntimeError(
                    f"{sheet_path.name}: adaptive pose crop clipped a component"
                )
            cell[target_y, target_x] = sheet_rgba[source_y, source_x]
        cells.append(Image.fromarray(cell))
    return cells


def alpha_bbox(image: Image.Image) -> tuple[int, int, int, int]:
    alpha = image.getchannel("A")
    bbox = alpha.getbbox()
    if bbox is None:
        raise RuntimeError("A generated cell contains no foreground pixels")
    return bbox


def normalize_cells(
    cells: list[Image.Image],
    *,
    target_height: int = TARGET_NEUTRAL_HEIGHT,
) -> list[Image.Image]:
    if not cells:
        raise ValueError("cannot normalize an empty pose list")
    if target_height <= 0:
        raise ValueError("target height must be positive")
    neutral_bbox = alpha_bbox(cells[0])
    neutral_height = neutral_bbox[3] - neutral_bbox[1]
    scale = target_height / neutral_height
    neutral_center_x = (neutral_bbox[0] + neutral_bbox[2]) / 2.0
    neutral_bottom = neutral_bbox[3]
    offset_x = CANVAS_SIZE[0] / 2.0 - neutral_center_x * scale
    offset_y = TARGET_BASELINE_Y - neutral_bottom * scale

    normalized: list[Image.Image] = []
    for cell in cells:
        # Transparent sheet pixels still carry the original white/checker RGB.
        # Bleed foreground color outward before Lanczos so resize filtering
        # cannot pull that matte back into the antialiased silhouette.
        prepared = add_rgb_edge_bleed(cell, iterations=12)
        resized = prepared.resize(
            (round(cell.width * scale), round(cell.height * scale)),
            Image.Resampling.LANCZOS,
        )
        canvas = Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
        canvas.alpha_composite(resized, (round(offset_x), round(offset_y)))
        normalized.append(selective_matte_defringe(canvas))
    return normalized


def apply_authored_order(
    cells: list[Image.Image],
    spec: ClipSpec,
) -> list[Image.Image]:
    if spec.authored_order is None:
        return cells
    order = spec.authored_order
    expected = list(range(len(cells)))
    if len(order) != len(cells) or sorted(order) != expected:
        raise RuntimeError(
            f"{spec.name}: authored_order must be a permutation of {expected}"
        )
    return [cells[index] for index in order]


def insert_supplemental_cells(
    cells: list[Image.Image],
    supplemental: list[Image.Image],
    spec: ClipSpec,
) -> tuple[list[Image.Image], dict[str, object]]:
    """Insert separately authored poses without blending or synthesizing them."""

    after = spec.supplemental_insert_after_index
    if spec.supplemental_pose_count == 0:
        if supplemental:
            raise RuntimeError(
                f"{spec.name}: supplemental poses supplied without a configured count"
            )
        return cells, {"enabled": False}
    if spec.supplemental_sheet_name is None or after is None:
        raise RuntimeError(
            f"{spec.name}: supplemental pose configuration is incomplete"
        )
    if len(supplemental) != spec.supplemental_pose_count:
        raise RuntimeError(
            f"{spec.name}: extracted {len(supplemental)} supplemental poses; "
            f"expected {spec.supplemental_pose_count}"
        )
    if not 0 <= after < len(cells) - 1:
        raise RuntimeError(
            f"{spec.name}: supplemental insertion index {after} has no following pose"
        )
    insertion_index = after + 1
    result = [
        *cells[:insertion_index],
        *(image.copy() for image in supplemental),
        *cells[insertion_index:],
    ]
    return result, {
        "enabled": True,
        "source": spec.supplemental_sheet_name,
        "insert_after_source_key": after,
        "inserted_key_indices": list(
            range(insertion_index, insertion_index + len(supplemental))
        ),
        "method": "separately authored RGBA poses without crossfade",
    }


def derive_authored_cells(
    cells: list[Image.Image],
    spec: ClipSpec,
) -> tuple[list[Image.Image], dict[str, object]]:
    """Insert a real RGBA release midpoint; never alpha-crossfade the prop."""

    after = spec.derive_release_mid_after_index
    if after is None:
        return cells, {"enabled": False}
    base_index = after + 1
    if not 0 <= after < len(cells) - 1:
        raise RuntimeError(
            f"{spec.name}: release midpoint index {after} has no following pose"
        )

    base = np.asarray(cells[base_index].convert("RGBA"), dtype=np.uint8).copy()
    components = sorted(
        label_alpha_components(base[:, :, 3]),
        key=lambda component: component.area,
        reverse=True,
    )
    if len(components) < 2:
        raise RuntimeError(
            f"{spec.name}: key {base_index} has no detached bomb for release midpoint"
        )
    body = components[0]
    prop_candidates = [
        component
        for component in components[1:]
        if component.area >= spec.detached_component_min_area
        and component.center_x > body.center_x
        and _component_median_luma(base, component)
        <= BOMB_DARK_COMPONENT_MEDIAN_LUMA_MAXIMUM
    ]
    if not prop_candidates:
        raise RuntimeError(
            f"{spec.name}: key {base_index} has no right-side dark bomb component"
        )
    prop = max(prop_candidates, key=lambda component: component.area)
    original_gap = prop.minimum_x - body.maximum_x - 1
    shift_x = BOMB_RELEASE_MID_GAP_PIXELS - original_gap
    if shift_x >= 0:
        raise RuntimeError(
            f"{spec.name}: key {base_index} bomb gap {original_gap}px is not wide "
            "enough to derive a left-shifted release midpoint"
        )

    height, width = base.shape[:2]
    source_y = prop.flat_indices // width
    source_x = prop.flat_indices % width
    target_x = source_x + shift_x
    if np.any(target_x < 0) or np.any(target_x >= width):
        raise RuntimeError(f"{spec.name}: derived bomb translation leaves the canvas")
    prop_pixels = base[source_y, source_x].copy()
    base[source_y, source_x] = 0
    base[source_y, target_x] = prop_pixels
    derived = Image.fromarray(base)
    derived_components = sorted(
        label_alpha_components(base[:, :, 3]),
        key=lambda component: component.area,
        reverse=True,
    )
    derived_body = derived_components[0]
    derived_prop = max(
        (
            component
            for component in derived_components[1:]
            if component.area >= spec.detached_component_min_area
            and component.center_x > derived_body.center_x
        ),
        key=lambda component: component.area,
    )
    derived_gap = derived_prop.minimum_x - derived_body.maximum_x - 1
    if derived_gap != BOMB_RELEASE_MID_GAP_PIXELS:
        raise RuntimeError(
            f"{spec.name}: derived release gap {derived_gap}px != "
            f"{BOMB_RELEASE_MID_GAP_PIXELS}px"
        )

    result = [*cells[:base_index], derived, *cells[base_index:]]
    return result, {
        "enabled": True,
        "insert_after_key": after,
        "source_key": base_index,
        "inserted_key": base_index,
        "translation_x": shift_x,
        "original_gap_pixels": original_gap,
        "derived_gap_pixels": derived_gap,
        "prop_area": prop.area,
        "original_prop_center_x": prop.center_x,
        "derived_prop_center_x": derived_prop.center_x,
        "method": "integer RGBA component translation without crossfade",
    }


def add_rgb_edge_bleed(image: Image.Image, iterations: int = 8) -> Image.Image:
    rgba = np.asarray(image, dtype=np.uint8).copy()
    rgb = rgba[:, :, :3].astype(np.float32)
    alpha = rgba[:, :, 3]
    known = alpha > 0

    for _ in range(iterations):
        total = np.zeros_like(rgb)
        count = np.zeros(known.shape, dtype=np.float32)
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            shifted_known = np.roll(known, (dy, dx), axis=(0, 1))
            shifted_rgb = np.roll(rgb, (dy, dx), axis=(0, 1))
            if dy == -1:
                shifted_known[-1, :] = False
            elif dy == 1:
                shifted_known[0, :] = False
            if dx == -1:
                shifted_known[:, -1] = False
            elif dx == 1:
                shifted_known[:, 0] = False
            total += shifted_rgb * shifted_known[:, :, None]
            count += shifted_known
        fill = (~known) & (count > 0)
        rgb[fill] = total[fill] / count[fill, None]
        known[fill] = True

    rgba[:, :, :3] = np.clip(rgb, 0, 255).astype(np.uint8)
    return Image.fromarray(rgba)


def find_final_matte_islands(
    rgba: np.ndarray,
) -> tuple[np.ndarray, list[dict[str, object]]]:
    """Locate small near-white islands in the main silhouette's outer shell."""

    rgb = rgba[:, :, :3].astype(np.int16)
    alpha = rgba[:, :, 3]
    alpha_components = label_alpha_components(alpha)
    island_mask = np.zeros(alpha.shape, dtype=bool)
    if not alpha_components:
        return island_mask, []

    main_component = max(alpha_components, key=lambda component: component.area)
    main_mask = np.zeros(alpha.size, dtype=bool)
    main_mask[main_component.flat_indices] = True
    main_mask = main_mask.reshape(alpha.shape)
    minimum = rgb.min(axis=2)
    chroma = rgb.max(axis=2) - minimum
    near_white = (
        main_mask
        & (alpha >= FINAL_MATTE_ALPHA_MINIMUM)
        & (minimum >= FINAL_MATTE_NEUTRAL_MINIMUM)
        & (chroma <= FINAL_MATTE_CHROMA_MAXIMUM)
    )
    white_components = sorted(
        label_alpha_components(np.where(near_white, 255, 0).astype(np.uint8)),
        key=lambda component: component.area,
        reverse=True,
    )
    if len(white_components) <= 1:
        return island_mask, []

    # The largest near-white component is the enclosed face/head. Small eyes or
    # highlights are away from transparency; only tiny secondary components in
    # the two-pixel outer shell are baked-matte candidates.
    maximum_area = max(
        1,
        int(white_components[0].area * FINAL_MATTE_MAX_RELATIVE_AREA),
    )
    transparent_shell = alpha < HALO_ALPHA_THRESHOLD
    for _ in range(FINAL_MATTE_TRANSPARENT_SHELL_DISTANCE):
        transparent_shell |= _adjacent_to(transparent_shell)

    details: list[dict[str, object]] = []
    flat_islands = island_mask.ravel()
    flat_shell = transparent_shell.ravel()
    for component in white_components[1:]:
        if component.area > maximum_area:
            continue
        if not np.any(flat_shell[component.flat_indices]):
            continue
        flat_islands[component.flat_indices] = True
        details.append(
            {
                "area": component.area,
                "bounds": [
                    component.minimum_x,
                    component.minimum_y,
                    component.maximum_x,
                    component.maximum_y,
                ],
            }
        )
    return island_mask, details


def inpaint_final_matte_islands(rgba: np.ndarray) -> np.ndarray:
    """Replace trapped matte RGB from nearby outline color without eroding alpha."""

    result = rgba.copy()
    islands, _ = find_final_matte_islands(result)
    if not np.any(islands):
        return result

    rgb = result[:, :, :3].astype(np.int16)
    alpha = result[:, :, 3]
    minimum = rgb.min(axis=2)
    chroma = rgb.max(axis=2) - minimum
    near_white = (
        (alpha >= FINAL_MATTE_ALPHA_MINIMUM)
        & (minimum >= FINAL_MATTE_NEUTRAL_MINIMUM)
        & (chroma <= FINAL_MATTE_CHROMA_MAXIMUM)
    )
    red, green, blue = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    warm_outline = (
        (alpha >= FINAL_MATTE_ALPHA_MINIMUM)
        & ~near_white
        & (red >= 90)
        & (red <= 235)
        & ((red - green) >= 25)
        & ((green - blue) >= 10)
    )
    general_foreground = (alpha >= FINAL_MATTE_ALPHA_MINIMUM) & ~near_white

    for raw_y, raw_x in np.argwhere(islands):
        y, x = int(raw_y), int(raw_x)
        y0 = max(0, y - FINAL_MATTE_INPAINT_RADIUS)
        y1 = min(alpha.shape[0], y + FINAL_MATTE_INPAINT_RADIUS + 1)
        x0 = max(0, x - FINAL_MATTE_INPAINT_RADIUS)
        x1 = min(alpha.shape[1], x + FINAL_MATTE_INPAINT_RADIUS + 1)
        candidates = np.argwhere(warm_outline[y0:y1, x0:x1])
        if not candidates.size:
            candidates = np.argwhere(general_foreground[y0:y1, x0:x1])
        if not candidates.size:
            continue
        candidates[:, 0] += y0
        candidates[:, 1] += x0
        distance_squared = (
            (candidates[:, 0] - y) ** 2
            + (candidates[:, 1] - x) ** 2
        )
        nearest = candidates[distance_squared == np.min(distance_squared)]
        result[y, x, :3] = np.mean(
            result[nearest[:, 0], nearest[:, 1], :3],
            axis=0,
        ).astype(np.uint8)
    # Deliberately leave alpha untouched: this is RGB decontamination, not
    # silhouette erosion.
    return result


def find_pale_lower_fringe_clusters(
    rgba: np.ndarray,
) -> tuple[np.ndarray, list[dict[str, object]]]:
    """Find continuous peach/cream matte streaks on the lower outer shell."""

    rgb = rgba[:, :, :3].astype(np.int16)
    alpha = rgba[:, :, 3]
    alpha_components = label_alpha_components(alpha)
    result = np.zeros(alpha.shape, dtype=bool)
    if not alpha_components:
        return result, []
    main = max(alpha_components, key=lambda component: component.area)
    main_mask = np.zeros(alpha.size, dtype=bool)
    main_mask[main.flat_indices] = True
    main_mask = main_mask.reshape(alpha.shape)

    transparent_shell = alpha < HALO_ALPHA_THRESHOLD
    for _ in range(FINAL_MATTE_TRANSPARENT_SHELL_DISTANCE):
        transparent_shell |= _adjacent_to(transparent_shell)
    y_grid = np.indices(alpha.shape)[0]
    red, green, blue = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    candidate = (
        main_mask
        & transparent_shell
        & (alpha >= PALE_FRINGE_ALPHA_MINIMUM)
        & (y_grid >= round(alpha.shape[0] * PALE_FRINGE_BODY_MINIMUM_Y_FRACTION))
        & (red >= PALE_FRINGE_MINIMUM_RED)
        & (green >= PALE_FRINGE_MINIMUM_GREEN)
        & (blue >= PALE_FRINGE_MINIMUM_BLUE)
        & ((red - blue) >= PALE_FRINGE_MINIMUM_RED_BLUE_DELTA)
        & ((red - green) >= PALE_FRINGE_MINIMUM_RED_GREEN_DELTA)
    )

    # The defect often forms a one-pixel-wide diagonal/vertical streak, so use
    # 8-connectivity here rather than the 4-connectivity used for alpha bodies.
    height, width = candidate.shape
    visited = np.zeros_like(candidate)
    details: list[dict[str, object]] = []
    for raw_y, raw_x in np.argwhere(candidate):
        seed_y, seed_x = int(raw_y), int(raw_x)
        if visited[seed_y, seed_x]:
            continue
        visited[seed_y, seed_x] = True
        queue: deque[tuple[int, int]] = deque([(seed_y, seed_x)])
        indices: list[int] = []
        while queue:
            y, x = queue.popleft()
            indices.append((y * width) + x)
            for next_y in range(y - 1, y + 2):
                for next_x in range(x - 1, x + 2):
                    if next_y == y and next_x == x:
                        continue
                    if (
                        0 <= next_y < height
                        and 0 <= next_x < width
                        and candidate[next_y, next_x]
                        and not visited[next_y, next_x]
                    ):
                        visited[next_y, next_x] = True
                        queue.append((next_y, next_x))
        if len(indices) < PALE_FRINGE_MINIMUM_COMPONENT_AREA:
            continue
        flat_indices = np.asarray(indices, dtype=np.int64)
        ys = flat_indices // width
        xs = flat_indices % width
        result.ravel()[flat_indices] = True
        details.append(
            {
                "area": len(indices),
                "bounds": [
                    int(np.min(xs)),
                    int(np.min(ys)),
                    int(np.max(xs)),
                    int(np.max(ys)),
                ],
            }
        )
    return result, details


def inpaint_pale_lower_fringe(rgba: np.ndarray) -> np.ndarray:
    """Pull pale fringe RGB to a nearby saturated warm outline; keep alpha."""

    result = rgba.copy()
    fringe, _ = find_pale_lower_fringe_clusters(result)
    if not np.any(fringe):
        return result
    rgb = result[:, :, :3].astype(np.int16)
    alpha = result[:, :, 3]
    alpha_components = label_alpha_components(alpha)
    main_mask = np.zeros(alpha.size, dtype=bool)
    if alpha_components:
        main = max(alpha_components, key=lambda component: component.area)
        main_mask[main.flat_indices] = True
    main_mask = main_mask.reshape(alpha.shape)
    red, green, blue = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    saturated_warm_outline = (
        main_mask
        & (alpha >= FINAL_MATTE_ALPHA_MINIMUM)
        & (blue <= 110)
        & ((red - blue) >= 70)
        & ((red - green) >= 20)
        & ((green - blue) >= 8)
    )
    for raw_y, raw_x in np.argwhere(fringe):
        y, x = int(raw_y), int(raw_x)
        y0 = max(0, y - PALE_FRINGE_INPAINT_RADIUS)
        y1 = min(alpha.shape[0], y + PALE_FRINGE_INPAINT_RADIUS + 1)
        x0 = max(0, x - PALE_FRINGE_INPAINT_RADIUS)
        x1 = min(alpha.shape[1], x + PALE_FRINGE_INPAINT_RADIUS + 1)
        candidates = np.argwhere(saturated_warm_outline[y0:y1, x0:x1])
        if candidates.size:
            candidates[:, 0] += y0
            candidates[:, 1] += x0
        else:
            # A long generated streak can be more than ten pixels from the
            # surviving outline. Fall back to the nearest saturated warm pixel
            # anywhere on the same main component rather than silently leaving
            # a QA-visible cluster behind.
            candidates = np.argwhere(saturated_warm_outline)
        if not candidates.size:
            raise RuntimeError(
                "pale fringe has no saturated warm support on the main character"
            )
        distance_squared = (
            (candidates[:, 0] - y) ** 2
            + (candidates[:, 1] - x) ** 2
        )
        nearest = candidates[distance_squared == np.min(distance_squared)]
        result[y, x, :3] = np.mean(
            result[nearest[:, 0], nearest[:, 1], :3],
            axis=0,
        ).astype(np.uint8)
    return result


def selective_matte_defringe(image: Image.Image) -> Image.Image:
    """Replace neutral matte spill, contracting one pixel only as a fallback."""

    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    rgba = inpaint_final_matte_islands(rgba)
    rgba = inpaint_pale_lower_fringe(rgba)
    alpha = rgba[:, :, 3]
    opaque_support = alpha >= 245
    supported_boundary = opaque_support.copy()
    for _ in range(HALO_MAX_LOW_ALPHA_DISTANCE_TO_OPAQUE):
        supported_boundary |= _adjacent_to(supported_boundary)
    unsupported_fringe = (
        (alpha >= HALO_ALPHA_THRESHOLD)
        & (alpha < 245)
        & ~supported_boundary
    )
    alpha[unsupported_fringe] = 0

    # RIFE estimates RGB and alpha independently, so algebraically dividing a
    # matted RGB by the interpolated alpha can amplify their tiny alignment
    # differences into colored streaks. Instead, pull the nearest fully covered
    # foreground color through only the four-pixel alpha boundary band.
    propagated_rgb = rgba[:, :, :3].astype(np.float32)
    known = alpha >= 240
    for _ in range(8):
        total = np.zeros_like(propagated_rgb)
        neighbor_count = np.zeros(alpha.shape, dtype=np.float32)
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            shifted_known = np.roll(known, (dy, dx), axis=(0, 1))
            shifted_rgb = np.roll(propagated_rgb, (dy, dx), axis=(0, 1))
            if dy == -1:
                shifted_known[-1, :] = False
            elif dy == 1:
                shifted_known[0, :] = False
            if dx == -1:
                shifted_known[:, -1] = False
            elif dx == 1:
                shifted_known[:, 0] = False
            total += shifted_rgb * shifted_known[:, :, None]
            neighbor_count += shifted_known
        fill = (~known) & (neighbor_count > 0)
        propagated_rgb[fill] = total[fill] / neighbor_count[fill, None]
        known[fill] = True

    near_transparent = alpha < HALO_ALPHA_THRESHOLD
    for _ in range(4):
        near_transparent |= _adjacent_to(near_transparent)
    color_pull = (
        (alpha >= HALO_ALPHA_THRESHOLD)
        & (alpha < 245)
        & near_transparent
        & known
    )
    rgba[color_pull, :3] = np.clip(
        propagated_rgb[color_pull],
        0,
        255,
    ).astype(np.uint8)

    rgb = rgba[:, :, :3].astype(np.int16)
    minimum = rgb.min(axis=2)
    chroma = rgb.max(axis=2) - minimum
    outer_edge = (alpha >= HALO_ALPHA_THRESHOLD) & _adjacent_to(
        alpha < HALO_ALPHA_THRESHOLD
    )
    matte_like = (
        (minimum >= HALO_NEUTRAL_MINIMUM)
        & (chroma <= HALO_NEUTRAL_CHROMA_MAXIMUM)
    )
    contaminated = outer_edge & matte_like
    removed = np.zeros_like(contaminated)
    valid_outline = (alpha >= ROOT_ALPHA_THRESHOLD) & ~matte_like
    for raw_y, raw_x in np.argwhere(contaminated):
        y, x = int(raw_y), int(raw_x)
        y0, y1 = max(0, y - 4), min(alpha.shape[0], y + 5)
        x0, x1 = max(0, x - 4), min(alpha.shape[1], x + 5)
        candidates = np.argwhere(valid_outline[y0:y1, x0:x1])
        if candidates.size:
            candidates[:, 0] += y0
            candidates[:, 1] += x0
            distance_squared = (
                (candidates[:, 0] - y) ** 2
                + (candidates[:, 1] - x) ** 2
            )
            nearest = candidates[distance_squared == np.min(distance_squared)]
            rgba[y, x, :3] = np.mean(
                rgba[nearest[:, 0], nearest[:, 1], :3],
                axis=0,
            ).astype(np.uint8)
        else:
            removed[y, x] = True

    if np.any(removed):
        alpha[removed] = 0
        # Preserve the next colored outline pixel but give the newly exposed
        # edge subpixel coverage. RGB is never neutralized, so orange/brown
        # outline color remains intact on dark desktops.
        newly_exposed = (
            (alpha >= HALO_ALPHA_THRESHOLD)
            & _adjacent_to(removed)
        )
        alpha[newly_exposed] = np.minimum(alpha[newly_exposed], 232)
    alpha[alpha < HALO_ALPHA_THRESHOLD] = 0
    rgba[:, :, 3] = alpha
    # Alpha tightening and boundary color propagation above can expose a new
    # structural fringe, so run both RGB-only repairs once more on final alpha.
    rgba = inpaint_final_matte_islands(rgba)
    rgba = inpaint_pale_lower_fringe(rgba)
    return Image.fromarray(rgba)


def recover_foreground_from_matte(
    matted_rgb: np.ndarray,
    interpolated_mask: np.ndarray,
) -> np.ndarray:
    """Undo RIFE's stationary near-white matte in straight-alpha RGB space."""

    coverage = np.clip(interpolated_mask.astype(np.float32) / 255.0, 0.0, 1.0)
    denominator = np.maximum(coverage[:, :, None], 1.0 / 255.0)
    recovered = (
        matted_rgb.astype(np.float32)
        - RIFE_MATTE_RGB[None, None, :] * (1.0 - coverage[:, :, None])
    ) / denominator
    return np.clip(recovered, 0, 255).astype(np.uint8)


def measure_dark_background_halo(image: Image.Image) -> dict[str, object]:
    """Measure any unsupported or color-polluted low-alpha outer fringe.

    A valid antialiased edge may contain one or two translucent pixels, such as
    ``0 -> 129 -> 246 -> 255``.  Those pixels are supported by the opaque
    silhouette and must not be eroded.  Generated checker/matte remnants differ
    in one of three measurable ways: they float farther away from an opaque
    core, remain neutral-bright on the outermost edge, or have an RGB color that
    sharply disagrees with the nearest opaque outline.  The final visibility
    check is color-agnostic, so saturated orange/cyan RIFE streaks cannot evade
    QA just because they are not white.
    """

    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8)
    rgb = rgba[:, :, :3].astype(np.float32)
    alpha = rgba[:, :, 3]
    visible = alpha >= HALO_ALPHA_THRESHOLD
    low_alpha = visible & (alpha < 245)
    outer_edge = visible & _adjacent_to(~visible)
    opaque_support = alpha >= 245
    supported_boundary = opaque_support.copy()
    propagated_rgb = rgb.copy()
    known = opaque_support.copy()
    for _ in range(HALO_MAX_LOW_ALPHA_DISTANCE_TO_OPAQUE):
        supported_boundary |= _adjacent_to(supported_boundary)
        total = np.zeros_like(propagated_rgb)
        neighbor_count = np.zeros(alpha.shape, dtype=np.float32)
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            shifted_known = np.roll(known, (dy, dx), axis=(0, 1))
            shifted_rgb = np.roll(propagated_rgb, (dy, dx), axis=(0, 1))
            if dy == -1:
                shifted_known[-1, :] = False
            elif dy == 1:
                shifted_known[0, :] = False
            if dx == -1:
                shifted_known[:, -1] = False
            elif dx == 1:
                shifted_known[:, 0] = False
            total += shifted_rgb * shifted_known[:, :, None]
            neighbor_count += shifted_known
        fill = (~known) & (neighbor_count > 0)
        propagated_rgb[fill] = total[fill] / neighbor_count[fill, None]
        known[fill] = True

    unsupported_fringe = low_alpha & ~supported_boundary
    # Brown/orange outline antialiasing can be next to an opaque white face at
    # corners. Comparing only against the geometrically nearest opaque pixel
    # therefore produces false positives. Accept the edge color when it matches
    # *any* opaque outline color in a tight local neighborhood; generated color
    # streaks and checker fragments have no such supporting color nearby.
    edge_color_residual = np.full(alpha.shape, np.inf, dtype=np.float32)
    for raw_y, raw_x in np.argwhere(low_alpha & outer_edge):
        y, x = int(raw_y), int(raw_x)
        y0 = max(0, y - HALO_EDGE_COLOR_SEARCH_RADIUS)
        y1 = min(alpha.shape[0], y + HALO_EDGE_COLOR_SEARCH_RADIUS + 1)
        x0 = max(0, x - HALO_EDGE_COLOR_SEARCH_RADIUS)
        x1 = min(alpha.shape[1], x + HALO_EDGE_COLOR_SEARCH_RADIUS + 1)
        local_opaque = opaque_support[y0:y1, x0:x1]
        if np.any(local_opaque):
            candidates = rgb[y0:y1, x0:x1][local_opaque]
            edge_color_residual[y, x] = float(
                np.min(np.max(np.abs(candidates - rgb[y, x]), axis=1))
            )
    color_polluted_fringe = (
        low_alpha
        & outer_edge
        & known
        & (edge_color_residual > HALO_EDGE_COLOR_RESIDUAL_MAXIMUM)
    )
    chroma = rgb.max(axis=2) - rgb.min(axis=2)
    neutral_opaque_support = (
        opaque_support
        & (rgb.min(axis=2) >= HALO_NEUTRAL_MINIMUM)
        & (chroma <= HALO_NEUTRAL_CHROMA_MAXIMUM)
    )
    neutral_supported_boundary = neutral_opaque_support.copy()
    for _ in range(HALO_MAX_LOW_ALPHA_DISTANCE_TO_OPAQUE):
        neutral_supported_boundary |= _adjacent_to(neutral_supported_boundary)
    neutral_bright_fringe = (
        outer_edge
        & low_alpha
        & (rgb.min(axis=2) >= HALO_NEUTRAL_MINIMUM)
        & (chroma <= HALO_NEUTRAL_CHROMA_MAXIMUM)
        & ~neutral_supported_boundary
    )
    opaque_matte_islands, opaque_matte_details = find_final_matte_islands(rgba)
    pale_lower_fringe, pale_lower_fringe_details = find_pale_lower_fringe_clusters(
        rgba
    )
    suspect = (
        unsupported_fringe
        | color_polluted_fringe
        | neutral_bright_fringe
        | opaque_matte_islands
        | pale_lower_fringe
    )
    coverage = alpha.astype(np.float32)[:, :, None] / 255.0
    visible_by_background: dict[str, int] = {}
    for name, color in HALO_DARK_BACKGROUNDS.items():
        background = np.asarray(color, dtype=np.float32)
        composite = rgb * coverage + background[None, None, :] * (1.0 - coverage)
        composite_luma = (
            composite[:, :, 0] * 0.2126
            + composite[:, :, 1] * 0.7152
            + composite[:, :, 2] * 0.0722
        )
        background_luma = float(
            background[0] * 0.2126
            + background[1] * 0.7152
            + background[2] * 0.0722
        )
        visible_by_background[name] = int(
            np.sum(suspect & ((composite_luma - background_luma) >= 18.0))
        )
    edge_count = int(np.sum(outer_edge))
    worst_visible = max(visible_by_background.values(), default=0)
    return {
        "outer_edge_pixels": edge_count,
        "low_alpha_edge_pixels": int(np.sum(low_alpha & outer_edge)),
        "unsupported_low_alpha_pixels": int(np.sum(unsupported_fringe)),
        "neutral_bright_edge_pixels": int(np.sum(neutral_bright_fringe)),
        "color_polluted_edge_pixels": int(np.sum(color_polluted_fringe)),
        "opaque_matte_island_pixels": int(np.sum(opaque_matte_islands)),
        "opaque_matte_islands": opaque_matte_details,
        "pale_lower_fringe_pixels": int(np.sum(pale_lower_fringe)),
        "pale_lower_fringe_clusters": pale_lower_fringe_details,
        "maximum_edge_color_residual": float(
            np.max(edge_color_residual[low_alpha & outer_edge])
            if np.any(low_alpha & outer_edge)
            else 0.0
        ),
        "edge_color_residual_limit": HALO_EDGE_COLOR_RESIDUAL_MAXIMUM,
        "edge_color_search_radius": HALO_EDGE_COLOR_SEARCH_RADIUS,
        "visible_pixels_by_background": visible_by_background,
        "maximum_visible_pixels": worst_visible,
        "maximum_visible_edge_fraction": (
            float(worst_visible / edge_count) if edge_count else 0.0
        ),
    }


def _merged_runs(xs: np.ndarray, maximum_gap: int = 3) -> list[tuple[int, int]]:
    if xs.size == 0:
        return []
    start = int(xs[0])
    previous = start
    runs: list[tuple[int, int]] = []
    for raw_x in xs[1:]:
        x = int(raw_x)
        if x - previous > maximum_gap + 1:
            runs.append((start, previous))
            start = x
        previous = x
    runs.append((start, previous))
    return runs


def measure_root_anchor(image: Image.Image) -> RootAnchor:
    """Measure body root without using whole-silhouette centroid.

    The horizontal anchor comes from long brown runs in the central-lower torso;
    raised wings and detached props are outside that measurement. The vertical
    anchor is the lowest well-supported yellow/orange foot row. The beak is
    excluded by the lower-image restriction.
    """

    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8)
    rgb = rgba[:, :, :3].astype(np.int16)
    alpha = rgba[:, :, 3]
    height, width = alpha.shape
    red, green, blue = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]

    brown = (
        (alpha >= ROOT_ALPHA_THRESHOLD)
        & (red >= 90)
        & (red <= 230)
        & (green >= 35)
        & (green <= 180)
        & (blue <= 145)
        & ((red - green) >= 15)
        & ((green - blue) >= 5)
    )
    torso_top = round(height * 0.60)
    torso_bottom = round(height * 0.81)
    minimum_run = max(16, round(width * 0.12))
    row_midpoints: list[float] = []
    torso_sample_count = 0
    for y in range(torso_top, torso_bottom):
        choices: list[tuple[float, int, int]] = []
        for start, end in _merged_runs(np.flatnonzero(brown[y])):
            length = end - start + 1
            midpoint = (start + end) / 2.0
            if (
                length >= minimum_run
                and width * 0.10 <= midpoint <= width * 0.90
            ):
                # Prefer the broad torso run. The tiny center preference only
                # resolves ties and cannot pull a genuinely translated body.
                score = length - (0.03 * abs(midpoint - (width / 2.0)))
                choices.append((score, start, end))
        if choices:
            _, start, end = max(choices)
            row_midpoints.append((start + end) / 2.0)
            torso_sample_count += end - start + 1

    if len(row_midpoints) < 5:
        y_coordinates, x_coordinates = np.nonzero(
            brown
            & (np.indices(alpha.shape)[0] >= round(height * 0.58))
            & (np.indices(alpha.shape)[0] < round(height * 0.83))
        )
        if x_coordinates.size == 0:
            raise RuntimeError("could not locate the central brown torso")
        torso_x = float(np.median(x_coordinates))
        torso_sample_count = int(x_coordinates.size)
    else:
        torso_x = float(np.median(np.asarray(row_midpoints)))

    y_grid, x_grid = np.indices(alpha.shape)
    # Use the actual lower silhouette, not the last yellow fill pixel. RIFE can
    # deform the yellow fill inside a stationary outlined foot by 1–2 pixels;
    # treating that color change as vertical motion caused false corrections.
    # The central restriction excludes detached props and low expressive wings.
    lower_central_alpha = (
        (alpha >= ROOT_ALPHA_THRESHOLD)
        & (y_grid >= round(height * 0.68))
        & (np.abs(x_grid - torso_x) <= width * 0.32)
    )
    contact_rows = np.flatnonzero(lower_central_alpha.sum(axis=1) >= 3)
    if contact_rows.size == 0:
        raise RuntimeError("could not locate a supporting foot contact row")
    foot_contact_y = float(contact_rows[-1])
    foot_sample_count = int(lower_central_alpha.sum())

    return RootAnchor(
        torso_x=torso_x,
        foot_contact_y=foot_contact_y,
        torso_sample_count=torso_sample_count,
        foot_sample_count=foot_sample_count,
    )


def semantic_character_alpha(image: Image.Image) -> np.ndarray:
    """Return body/head/feet alpha while excluding a right-side bomb or smoke.

    The thrown bomb can remain alpha-connected to an outstretched wing, making
    "largest alpha component" unsuitable for body-boundary QA. Character colors
    remain semantic body pixels. In the far-right prop zone, warm bomb-band
    pixels are excluded only when locally supported by a substantial gray/black
    bomb or smoke region; an unaccompanied brown wing still counts as character.
    """

    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8)
    rgb = rgba[:, :, :3].astype(np.int16)
    alpha = rgba[:, :, 3]
    root_x = measure_root_anchor(image).torso_x
    y_grid, x_grid = np.indices(alpha.shape)
    del y_grid
    red, green, blue = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    minimum = rgb.min(axis=2)
    maximum = rgb.max(axis=2)
    chroma = maximum - minimum
    luma = (red * 0.2126) + (green * 0.7152) + (blue * 0.0722)

    central_white = (
        (minimum >= 205)
        & (chroma <= 45)
        & (np.abs(x_grid - root_x) <= BOMB_PROP_ZONE_START_OFFSET)
    )
    brown = (
        (red >= 90)
        & (red <= 235)
        & (green >= 35)
        & (green <= 185)
        & (blue <= 145)
        & ((red - green) >= 15)
        & ((green - blue) >= 5)
    )
    yellow = (
        (red >= 200)
        & (green >= 100)
        & (blue <= 100)
        & ((green - blue) >= 55)
    )
    orange_outline = (
        (red >= 140)
        & (green >= 45)
        & (blue <= 120)
        & ((red - green) >= 25)
        & ((green - blue) >= 10)
    )
    semantic = (
        (alpha >= HALO_ALPHA_THRESHOLD)
        & (central_white | brown | yellow | orange_outline)
    )

    prop_gray = (
        (alpha >= HALO_ALPHA_THRESHOLD)
        & (x_grid > root_x + BOMB_PROP_GRAY_SEED_OFFSET)
        & (chroma <= 28)
        & (luma <= 235)
    )
    prop_support = prop_gray.copy()
    for _ in range(BOMB_PROP_GRAY_SUPPORT_RADIUS):
        prop_support |= _adjacent_to(prop_support)
    prop_zone = (
        (x_grid > root_x + BOMB_PROP_ZONE_START_OFFSET)
        & prop_support
    )
    semantic &= ~prop_zone
    return np.where(semantic, alpha, 0).astype(np.uint8)


def _median_track(track: np.ndarray, radius: int = 1) -> np.ndarray:
    """Reject one-frame root-detector spikes without adding temporal lag."""

    if track.ndim != 2 or track.shape[1] != 2:
        raise ValueError("root track must have shape (frame_count, 2)")
    if len(track) < 3 or radius <= 0:
        return track.copy()
    result = np.empty_like(track, dtype=np.float64)
    for index in range(len(track)):
        start = max(0, index - radius)
        end = min(len(track), index + radius + 1)
        result[index] = np.median(track[start:end], axis=0)
    result[0] = track[0]
    result[-1] = track[-1]
    return result


def translate_rgba(image: Image.Image, offset_x: float, offset_y: float) -> Image.Image:
    prepared = add_rgb_edge_bleed(image.convert("RGBA"))
    return selective_matte_defringe(
        prepared.transform(
            CANVAS_SIZE,
            Image.Transform.AFFINE,
            (1.0, 0.0, -offset_x, 0.0, 1.0, -offset_y),
            resample=Image.Resampling.BICUBIC,
            fillcolor=(0, 0, 0, 0),
        )
    )


def translate_rgba_integer(
    image: Image.Image,
    offset_x: int,
    offset_y: int,
) -> Image.Image:
    """Translate exact pixels without another resampling pass."""

    source = np.asarray(image.convert("RGBA"), dtype=np.uint8)
    height, width = source.shape[:2]
    destination = np.zeros_like(source)
    source_x0 = max(0, -offset_x)
    source_x1 = min(width, width - offset_x)
    source_y0 = max(0, -offset_y)
    source_y1 = min(height, height - offset_y)
    if source_x0 >= source_x1 or source_y0 >= source_y1:
        raise RuntimeError("integer translation moved the entire frame off canvas")
    destination_x0 = source_x0 + offset_x
    destination_x1 = source_x1 + offset_x
    destination_y0 = source_y0 + offset_y
    destination_y1 = source_y1 + offset_y
    destination[
        destination_y0:destination_y1,
        destination_x0:destination_x1,
    ] = source[source_y0:source_y1, source_x0:source_x1]
    return Image.fromarray(destination)


def correct_final_root_residuals(
    frames: list[Image.Image],
    canonical_neutral: Image.Image,
    locked_indices: set[int],
) -> tuple[list[Image.Image], dict[str, object]]:
    """Remove discrete 1–2px root residuals while preserving authored keys."""

    target = measure_root_anchor(canonical_neutral)
    result: list[Image.Image] = []
    corrections: list[dict[str, object]] = []
    for index, frame in enumerate(frames):
        if index in locked_indices:
            result.append(frame.copy())
            continue
        anchor = measure_root_anchor(frame)
        offset_x = int(round(target.torso_x - anchor.torso_x))
        offset_y = int(round(target.foot_contact_y - anchor.foot_contact_y))
        corrected = (
            translate_rgba_integer(frame, offset_x, offset_y)
            if offset_x or offset_y
            else frame.copy()
        )
        result.append(corrected)
        if offset_x or offset_y:
            corrected_anchor = measure_root_anchor(corrected)
            corrections.append(
                {
                    "frame": index,
                    "offset_x": offset_x,
                    "offset_y": offset_y,
                    "before": {
                        "torso_x": anchor.torso_x,
                        "foot_contact_y": anchor.foot_contact_y,
                    },
                    "after": {
                        "torso_x": corrected_anchor.torso_x,
                        "foot_contact_y": corrected_anchor.foot_contact_y,
                    },
                }
            )

    final_anchors = [measure_root_anchor(frame) for frame in result]
    return result, {
        "locked_authored_frames": sorted(locked_indices),
        "corrected_frame_count": len(corrections),
        "corrections": corrections,
        "maximum_torso_x_error": max(
            abs(anchor.torso_x - target.torso_x) for anchor in final_anchors
        ),
        "maximum_foot_contact_y_error": max(
            abs(anchor.foot_contact_y - target.foot_contact_y)
            for anchor in final_anchors
        ),
        "method": "integer whole-frame residual translation without resampling",
    }


def clean_bomb_interpolated_matte(
    frames: list[Image.Image],
    spec: ClipSpec,
    locked_indices: set[int],
) -> tuple[list[Image.Image], dict[str, object]]:
    """RGB-inpaint non-head white RIFE tears on the connected character.

    The mascot's white head is the largest neutral component and is preserved.
    Small eye highlights and face regions inside a conservative upper-center
    box are also preserved.  Neutral components elsewhere on the main alpha
    body are interpolation matte, such as the white wedges seen between a
    moving wing and bomb. Detached bomb/smoke components are deliberately not
    touched here and remain subject to their own motion/edge diagnostics.
    """

    if not spec.detached_motion_monotonic_right:
        return [frame.copy() for frame in frames], {"enabled": False}

    result: list[Image.Image] = []
    observations: list[dict[str, object]] = []
    for frame_index, frame in enumerate(frames):
        if frame_index in locked_indices:
            result.append(frame.copy())
            continue
        rgba = np.asarray(frame.convert("RGBA"), dtype=np.uint8).copy()
        rgb = rgba[:, :, :3].astype(np.int16)
        alpha = rgba[:, :, 3]
        alpha_components = sorted(
            label_alpha_components(alpha),
            key=lambda component: component.area,
            reverse=True,
        )
        if not alpha_components:
            result.append(frame.copy())
            continue
        main = alpha_components[0]
        main_mask = np.zeros(alpha.size, dtype=bool)
        main_mask[main.flat_indices] = True
        main_mask = main_mask.reshape(alpha.shape)
        minimum = rgb.min(axis=2)
        chroma = rgb.max(axis=2) - minimum
        neutral = (
            main_mask
            & (alpha >= HALO_ALPHA_THRESHOLD)
            & (minimum >= FINAL_MATTE_NEUTRAL_MINIMUM)
            & (chroma <= FINAL_MATTE_CHROMA_MAXIMUM)
        )
        neutral_components = sorted(
            label_alpha_components(
                np.where(neutral, 255, 0).astype(np.uint8)
            ),
            key=lambda component: component.area,
            reverse=True,
        )
        if not neutral_components:
            result.append(frame.copy())
            continue

        root_x = measure_root_anchor(frame).torso_x
        safe_left = root_x - (rgba.shape[1] * 0.20)
        safe_right = root_x + (rgba.shape[1] * 0.20)
        safe_bottom = rgba.shape[0] * 0.70
        candidate = np.zeros(alpha.shape, dtype=bool)
        flat_candidate = candidate.ravel()
        removed_components: list[dict[str, object]] = []
        for component in neutral_components[1:]:
            inside_head_box = (
                component.minimum_x >= safe_left
                and component.maximum_x <= safe_right
                and component.maximum_y < safe_bottom
            )
            if inside_head_box or component.area <= 1:
                continue
            flat_candidate[component.flat_indices] = True
            removed_components.append(
                {
                    "area": component.area,
                    "bounds": [
                        component.minimum_x,
                        component.minimum_y,
                        component.maximum_x,
                        component.maximum_y,
                    ],
                }
            )

        if not np.any(candidate):
            result.append(frame.copy())
            continue
        support = main_mask & (alpha >= ROOT_ALPHA_THRESHOLD) & ~neutral
        for raw_y, raw_x in np.argwhere(candidate):
            y, x = int(raw_y), int(raw_x)
            found = False
            for radius in (8, 16, 24, 32):
                y0, y1 = max(0, y - radius), min(alpha.shape[0], y + radius + 1)
                x0, x1 = max(0, x - radius), min(alpha.shape[1], x + radius + 1)
                choices = np.argwhere(support[y0:y1, x0:x1])
                if not choices.size:
                    continue
                choices[:, 0] += y0
                choices[:, 1] += x0
                distance_squared = (
                    (choices[:, 0] - y) ** 2
                    + (choices[:, 1] - x) ** 2
                )
                nearest = choices[distance_squared == np.min(distance_squared)]
                rgba[y, x, :3] = np.mean(
                    rgba[nearest[:, 0], nearest[:, 1], :3],
                    axis=0,
                ).astype(np.uint8)
                found = True
                break
            if not found:
                raise RuntimeError(
                    f"{spec.name} frame-{frame_index:04d}: no foreground RGB "
                    "support for external neutral matte"
                )
        observations.append(
            {
                "frame": frame_index,
                "pixel_count": int(np.sum(candidate)),
                "components": removed_components,
            }
        )
        result.append(Image.fromarray(rgba))

    return result, {
        "enabled": True,
        "preserved_scope": "largest white head plus upper-center face details",
        "detached_prop_or_smoke_modified": False,
        "cleaned_frame_count": len(observations),
        "cleaned_pixel_count": sum(
            int(item["pixel_count"]) for item in observations
        ),
        "observations": observations,
    }


def repair_bomb_missing_warm_outline(
    frames: list[Image.Image],
    spec: ClipSpec,
    locked_indices: set[int],
) -> tuple[list[Image.Image], dict[str, object]]:
    """Restore a thin warm border where RIFE exposes white head pixels.

    A fast pose change can leave the mascot's white head directly adjacent to
    transparency for one frame even though both authored endpoints have an
    orange/brown outline.  This is not fixed by deleting alpha: doing so merely
    reveals the next white pixel.  For over-limit connected neutral edge runs,
    recolor a two-pixel white band from the nearest saturated warm outline while
    preserving alpha and all authored frames exactly.
    """

    if not spec.detached_motion_monotonic_right:
        return [frame.copy() for frame in frames], {"enabled": False}

    result: list[Image.Image] = []
    observations: list[dict[str, object]] = []
    for frame_index, frame in enumerate(frames):
        if frame_index in locked_indices:
            result.append(frame.copy())
            continue
        rgba = np.asarray(frame.convert("RGBA"), dtype=np.uint8).copy()
        rgb = rgba[:, :, :3].astype(np.int16)
        alpha = rgba[:, :, 3]
        alpha_components = label_alpha_components(alpha)
        if not alpha_components:
            result.append(frame.copy())
            continue
        main = max(alpha_components, key=lambda component: component.area)
        main_mask = np.zeros(alpha.size, dtype=bool)
        main_mask[main.flat_indices] = True
        main_mask = main_mask.reshape(alpha.shape)
        minimum = rgb.min(axis=2)
        chroma = rgb.max(axis=2) - minimum
        visible = alpha >= HALO_ALPHA_THRESHOLD
        neutral_like = (
            (minimum >= HALO_NEUTRAL_MINIMUM)
            & (chroma <= HALO_NEUTRAL_CHROMA_MAXIMUM)
        )
        seed = (
            main_mask
            & visible
            & (alpha < 245)
            & _adjacent_to(~visible)
            & neutral_like
        )
        seed_components = label_alpha_components(
            np.where(seed, 255, 0).astype(np.uint8)
        )
        selected_seed = np.zeros_like(seed)
        selected_seed_flat = selected_seed.ravel()
        selected_details: list[dict[str, object]] = []
        for component in seed_components:
            if component.area <= HALO_MAX_NEUTRAL_BRIGHT_PIXELS_PER_FRAME:
                continue
            selected_seed_flat[component.flat_indices] = True
            selected_details.append(
                {
                    "area": component.area,
                    "bounds": [
                        component.minimum_x,
                        component.minimum_y,
                        component.maximum_x,
                        component.maximum_y,
                    ],
                }
            )
        if not np.any(selected_seed):
            result.append(frame.copy())
            continue

        band = selected_seed.copy()
        for _ in range(2):
            band |= (
                _adjacent_to(band)
                & main_mask
                & visible
                & neutral_like
            )
        red, green, blue = (
            rgb[:, :, 0],
            rgb[:, :, 1],
            rgb[:, :, 2],
        )
        saturated_warm_outline = (
            main_mask
            & (alpha >= FINAL_MATTE_ALPHA_MINIMUM)
            & (red >= 160)
            & (blue <= 120)
            & ((red - blue) >= 70)
            & ((red - green) >= 20)
            & ((green - blue) >= 8)
        )
        support = np.argwhere(saturated_warm_outline)
        if not support.size:
            raise RuntimeError(
                f"{spec.name} frame-{frame_index:04d}: missing warm outline "
                "support for neutral-edge repair"
            )
        maximum_distance = 0.0
        for raw_y, raw_x in np.argwhere(band):
            y, x = int(raw_y), int(raw_x)
            distance_squared = (
                (support[:, 0] - y) ** 2
                + (support[:, 1] - x) ** 2
            )
            nearest_distance = int(np.min(distance_squared))
            nearest = support[distance_squared == nearest_distance]
            rgba[y, x, :3] = np.mean(
                rgba[nearest[:, 0], nearest[:, 1], :3],
                axis=0,
            ).astype(np.uint8)
            maximum_distance = max(maximum_distance, float(np.sqrt(nearest_distance)))
        if maximum_distance > 16.0:
            raise RuntimeError(
                f"{spec.name} frame-{frame_index:04d}: neutral-edge repair "
                f"reached {maximum_distance:.2f}px for warm outline support"
            )
        result.append(Image.fromarray(rgba))
        observations.append(
            {
                "frame": frame_index,
                "seed_pixels": int(np.sum(selected_seed)),
                "recolored_band_pixels": int(np.sum(band)),
                "maximum_support_distance": maximum_distance,
                "seed_components": selected_details,
            }
        )

    return result, {
        "enabled": True,
        "authored_frames_modified": False,
        "alpha_modified": False,
        "repaired_frame_count": len(observations),
        "recolored_pixel_count": sum(
            int(item["recolored_band_pixels"]) for item in observations
        ),
        "observations": observations,
    }


def stabilize_frames(
    frames: list[Image.Image],
    canonical_neutral: Image.Image,
    *,
    exact_endpoints: tuple[Image.Image, Image.Image] | None = None,
    authored_frame_indices: tuple[int, ...] | None = None,
) -> tuple[list[Image.Image], dict[str, object]]:
    if authored_frame_indices is not None:
        return stabilize_authored_root_frames(
            frames, canonical_neutral, authored_frame_indices,
            exact_endpoints=exact_endpoints,
        )
    if not frames:
        raise ValueError("cannot stabilize an empty frame sequence")
    target = measure_root_anchor(canonical_neutral)
    measured = [measure_root_anchor(frame) for frame in frames]
    raw_track = np.asarray(
        [(anchor.torso_x, anchor.foot_contact_y) for anchor in measured],
        dtype=np.float64,
    )
    filtered_track = _median_track(raw_track)
    target_point = np.asarray((target.torso_x, target.foot_contact_y), dtype=np.float64)
    translations = target_point[None, :] - filtered_track
    if float(np.max(np.abs(translations))) > max(CANVAS_SIZE) * 0.35:
        raise RuntimeError("required root correction is implausibly large")

    def render(offsets: np.ndarray) -> list[Image.Image]:
        rendered = [
            translate_rgba(frame, float(offset[0]), float(offset[1]))
            for frame, offset in zip(frames, offsets, strict=True)
        ]
        if exact_endpoints is not None:
            rendered[0] = exact_endpoints[0].copy()
            rendered[-1] = exact_endpoints[1].copy()
        return rendered

    # Bicubic translation plus threshold-based root measurement can leave a
    # small subpixel residual. Re-measure against the original render and fold
    # one direct correction into the cumulative transform (there is no second
    # resampling/blur in the final pixels). The final root track, rather than
    # the authored wing/prop motion, is therefore stationary.
    stabilized = render(translations)
    correction_history: list[np.ndarray] = []
    for _ in range(6):
        pass_anchors = [measure_root_anchor(frame) for frame in stabilized]
        correction = target_point[None, :] - np.asarray(
            [(anchor.torso_x, anchor.foot_contact_y) for anchor in pass_anchors],
            dtype=np.float64,
        )
        if exact_endpoints is not None:
            correction[0] = 0.0
            correction[-1] = 0.0
        correction_history.append(correction.copy())
        if float(np.max(np.abs(correction))) <= 0.5:
            break
        translations += correction
        # Always render from the original RIFE output with the cumulative
        # transform. Iteration therefore improves the discrete root detector
        # without repeatedly resampling/softening already translated pixels.
        stabilized = render(translations)

    stabilized_anchors = [measure_root_anchor(frame) for frame in stabilized]
    stabilized_track = np.asarray(
        [(anchor.torso_x, anchor.foot_contact_y) for anchor in stabilized_anchors],
        dtype=np.float64,
    )
    translation_steps = np.diff(translations, axis=0)
    report: dict[str, object] = {
        "target": {
            "torso_x": target.torso_x,
            "foot_contact_y": target.foot_contact_y,
        },
        "raw": {
            "torso_x_range": float(np.ptp(raw_track[:, 0])),
            "foot_contact_y_range": float(np.ptp(raw_track[:, 1])),
        },
        "translation": {
            "maximum_absolute_x": float(np.max(np.abs(translations[:, 0]))),
            "maximum_absolute_y": float(np.max(np.abs(translations[:, 1]))),
            "maximum_adjacent_step_x": (
                float(np.max(np.abs(translation_steps[:, 0])))
                if len(translation_steps)
                else 0.0
            ),
            "maximum_adjacent_step_y": (
                float(np.max(np.abs(translation_steps[:, 1])))
                if len(translation_steps)
                else 0.0
            ),
            "secondary_correction_maximum_x": float(
                max(
                    np.max(np.abs(item[:, 0]))
                    for item in correction_history
                )
            ),
            "secondary_correction_maximum_y": float(
                max(
                    np.max(np.abs(item[:, 1]))
                    for item in correction_history
                )
            ),
            "secondary_correction_passes": len(correction_history),
            "per_frame_offsets": [
                [float(offset[0]), float(offset[1])]
                for offset in translations
            ],
        },
        "stabilized": {
            "torso_x_range": float(np.ptp(stabilized_track[:, 0])),
            "foot_contact_y_range": float(np.ptp(stabilized_track[:, 1])),
            "maximum_torso_x_error": float(
                np.max(np.abs(stabilized_track[:, 0] - target.torso_x))
            ),
            "maximum_foot_contact_y_error": float(
                np.max(np.abs(stabilized_track[:, 1] - target.foot_contact_y))
            ),
        },
    }
    return stabilized, report


def stabilize_authored_root_frames(
    frames: list[Image.Image],
    canonical_neutral: Image.Image,
    authored_frame_indices: tuple[int, ...],
    *,
    exact_endpoints: tuple[Image.Image, Image.Image] | None = None,
) -> tuple[list[Image.Image], dict[str, object]]:
    """Remove segment-local root drift without moving an authored endpoint.

    Tightening reconstructed alpha can change a threshold-measured foot/root by
    0.5--1px even when RGB geometry has not moved. Pulling every reconstructed
    frame to the canonical measurement, then replacing locked keys with their
    original pixels, creates a one-frame whole-character jump. Calibrate that
    measurement bias at *every* authored endpoint and interpolate the baseline
    across each segment. Only deviation from that baseline is corrected.

    This is not permission to ship a displaced character: the normal final
    root, border and halo gates still check the actual output unchanged.
    """
    indices = authored_frame_indices
    if (not frames or len(indices) < 2
            or any(type(index) is not int for index in indices)
            or indices[0] != 0 or indices[-1] != len(frames) - 1
            or any(first >= last for first, last in zip(indices, indices[1:]))):
        raise ValueError("authored root calibration requires increasing endpoint indexes spanning all frames")
    target = measure_root_anchor(canonical_neutral)
    anchors = [measure_root_anchor(frame) for frame in frames]
    raw_track = np.asarray([(anchor.torso_x, anchor.foot_contact_y) for anchor in anchors], dtype=np.float64)
    filtered_track = _median_track(raw_track)
    target_point = np.asarray((target.torso_x, target.foot_contact_y), dtype=np.float64)
    uncalibrated = target_point[None, :] - filtered_track
    endpoint_offsets = uncalibrated[list(indices)]
    baseline = np.column_stack([
        np.interp(np.arange(len(frames)), indices, endpoint_offsets[:, dimension])
        for dimension in range(2)
    ])
    translations = uncalibrated - baseline
    # Be explicit rather than relying on floating-point interpolation identity.
    translations[list(indices)] = 0.0
    if float(np.max(np.abs(translations))) > max(CANVAS_SIZE) * 0.35:
        raise RuntimeError("required calibrated root correction is implausibly large")
    rendered = [
        frame.copy() if not np.any(offset) else translate_rgba(frame, float(offset[0]), float(offset[1]))
        for frame, offset in zip(frames, translations, strict=True)
    ]
    if exact_endpoints is not None:
        rendered[0], rendered[-1] = exact_endpoints[0].copy(), exact_endpoints[1].copy()
    result_anchors = [measure_root_anchor(frame) for frame in rendered]
    result_track = np.asarray([(anchor.torso_x, anchor.foot_contact_y) for anchor in result_anchors], dtype=np.float64)
    steps = np.diff(translations, axis=0)
    return rendered, {
        "target": {"torso_x": target.torso_x, "foot_contact_y": target.foot_contact_y},
        "raw": {"torso_x_range": float(np.ptp(raw_track[:, 0])), "foot_contact_y_range": float(np.ptp(raw_track[:, 1]))},
        "translation": {
            "maximum_absolute_x": float(np.max(np.abs(translations[:, 0]))),
            "maximum_absolute_y": float(np.max(np.abs(translations[:, 1]))),
            "maximum_adjacent_step_x": float(np.max(np.abs(steps[:, 0]))),
            "maximum_adjacent_step_y": float(np.max(np.abs(steps[:, 1]))),
            "secondary_correction_maximum_x": 0.0, "secondary_correction_maximum_y": 0.0,
            "secondary_correction_passes": 0, "per_frame_offsets": translations.tolist(),
        },
        "authored_root_calibration": {
            "enabled": True, "frame_indices": list(indices),
            "reconstructed_endpoint_root_offsets": endpoint_offsets.tolist(),
            "endpoint_baseline_offsets": baseline.tolist(),
            "maximum_authored_translation": float(np.max(np.abs(translations[list(indices)]))),
            "method": "segmentwise linear endpoint measurement-bias baseline; only intra-segment drift is corrected",
            "qa_thresholds_relaxed": False,
        },
        "stabilized": {
            "torso_x_range": float(np.ptp(result_track[:, 0])),
            "foot_contact_y_range": float(np.ptp(result_track[:, 1])),
            "maximum_torso_x_error": float(np.max(np.abs(result_track[:, 0] - target.torso_x))),
            "maximum_foot_contact_y_error": float(np.max(np.abs(result_track[:, 1] - target.foot_contact_y))),
        },
    }


def preserve_calibrated_root_residuals(
    frames: list[Image.Image], canonical_neutral: Image.Image, locked_indices: set[int],
) -> tuple[list[Image.Image], dict[str, object]]:
    """Report, but never undo, endpoint calibration with canonical re-centering."""
    target = measure_root_anchor(canonical_neutral)
    anchors = [measure_root_anchor(frame) for frame in frames]
    return frames, {
        "locked_authored_frames": sorted(locked_indices), "corrected_frame_count": 0, "corrections": [],
        "maximum_torso_x_error": max(abs(anchor.torso_x - target.torso_x) for anchor in anchors),
        "maximum_foot_contact_y_error": max(abs(anchor.foot_contact_y - target.foot_contact_y) for anchor in anchors),
        "authored_root_calibration": True,
        "method": "preserve calibrated coordinates; unchanged final QA gates decide root bounds",
        "qa_thresholds_relaxed": False,
    }


def repair_calibrated_outer_edge_rgb(
    frames: list[Image.Image], locked_indices: set[int],
) -> tuple[list[Image.Image], dict[str, object]]:
    """Repair only unsupported edge *color*, never coverage or geometry.

    The legacy color propagation treats alpha >=240 as a trusted seed while
    the strict halo gate requires >=245 opaque support. A resampled 242-alpha
    matte seed can consequently propagate its own polluted RGB forever. For
    calibrated clips, replace only outer-edge colors that disagree with every
    nearby truly opaque color. Copy an existing local color rather than mixing
    a new one. Locked keys and all opaque/interior pixels stay byte-exact.
    """
    result: list[Image.Image] = []
    observations: list[dict[str, int]] = []
    radius = HALO_EDGE_COLOR_SEARCH_RADIUS
    for index, frame in enumerate(frames):
        if index in locked_indices:
            result.append(frame.copy())
            continue
        rgba = np.array(frame.convert("RGBA"), dtype=np.uint8)
        original_rgb = rgba[:, :, :3].astype(np.int16)
        alpha = rgba[:, :, 3]
        visible = alpha >= HALO_ALPHA_THRESHOLD
        opaque = alpha >= 245
        supported = opaque.copy()
        for _ in range(HALO_MAX_LOW_ALPHA_DISTANCE_TO_OPAQUE):
            supported |= _adjacent_to(supported)
        candidates = visible & ~opaque & supported & _adjacent_to(~visible)
        repaired = 0
        for raw_y, raw_x in np.argwhere(candidates):
            y, x = int(raw_y), int(raw_x)
            y0, y1 = max(0, y - radius), min(alpha.shape[0], y + radius + 1)
            x0, x1 = max(0, x - radius), min(alpha.shape[1], x + radius + 1)
            support_positions = np.argwhere(opaque[y0:y1, x0:x1])
            if not len(support_positions):
                continue  # Unsupported coverage remains a strict QA failure.
            colors = original_rgb[y0:y1, x0:x1][opaque[y0:y1, x0:x1]]
            residuals = np.max(np.abs(colors - original_rgb[y, x]), axis=1)
            minimum = int(np.min(residuals))
            if minimum <= HALO_EDGE_COLOR_RESIDUAL_MAXIMUM:
                continue
            # Minimize color change, then break ties by spatial proximity.
            choices = np.flatnonzero(residuals == minimum)
            distances = ((support_positions[choices, 0] + y0 - y) ** 2
                         + (support_positions[choices, 1] + x0 - x) ** 2)
            selected = int(choices[int(np.argmin(distances))])
            rgba[y, x, :3] = colors[selected]
            repaired += 1
        result.append(Image.fromarray(rgba))
        if repaired:
            observations.append({"frame": index, "recolored_outer_edge_pixels": repaired})
    return result, {
        "enabled": True, "alpha_modified": False, "opaque_pixels_modified": False,
        "authored_frames_modified": False, "qa_thresholds_relaxed": False,
        "recolored_pixel_count": sum(item["recolored_outer_edge_pixels"] for item in observations),
        "observations": observations,
        "method": "RGB-only outer-alpha-edge repair from existing local >=245-alpha color support",
    }


def _premultiplied_mae(first: Image.Image, second: Image.Image) -> float:
    first_rgba = np.asarray(first.convert("RGBA"), dtype=np.float32) / 255.0
    second_rgba = np.asarray(second.convert("RGBA"), dtype=np.float32) / 255.0
    first_alpha = first_rgba[:, :, 3:4]
    second_alpha = second_rgba[:, :, 3:4]
    first_premultiplied = np.concatenate(
        (first_rgba[:, :, :3] * first_alpha, first_alpha),
        axis=2,
    )
    second_premultiplied = np.concatenate(
        (second_rgba[:, :, :3] * second_alpha, second_alpha),
        axis=2,
    )
    return float(np.mean(np.abs(first_premultiplied - second_premultiplied)))


def _clear_border_fraction(image: Image.Image) -> float:
    alpha = np.asarray(image.getchannel("A"), dtype=np.uint8)
    border = np.concatenate(
        (alpha[0], alpha[-1], alpha[1:-1, 0], alpha[1:-1, -1])
    )
    return float(np.mean(border < 10))


def qa_frame_sequence(
    frames: list[Image.Image],
    expected_count: int,
    canonical_neutral: Image.Image,
    expected_start: Image.Image,
    expected_end: Image.Image,
    *,
    prior_errors: list[str] | None = None,
    allow_detached_edge_contact: bool = False,
    component_spec: ClipSpec | None = None,
    root_reference_frames: list[Image.Image] | None = None,
) -> dict[str, object]:
    errors = list(prior_errors or ())
    if len(frames) != expected_count:
        errors.append(f"frame count {len(frames)} != expected {expected_count}")
    if not frames:
        errors.append("frame sequence is empty")
        return {"passed": False, "errors": errors}
    if root_reference_frames is not None and len(root_reference_frames) != len(frames):
        errors.append(
            "root reference frame count "
            f"{len(root_reference_frames)} != composite frame count {len(frames)}"
        )
        root_reference_frames = None

    anchors: list[RootAnchor] = []
    clear_border_fractions: list[float] = []
    body_clear_border_fractions: list[float] = []
    halo_measurements: list[dict[str, object]] = []
    full_frame_halo_measurements: list[dict[str, object]] = []
    edge_contact_classification: list[dict[str, object]] = []
    disallowed_detached_edge_contacts: list[dict[str, object]] = []
    content_margin_violations: list[dict[str, object]] = []
    character_margin_violations: list[dict[str, object]] = []
    minimum_margins = [CANVAS_SIZE[0], CANVAS_SIZE[1], CANVAS_SIZE[0], CANVAS_SIZE[1]]
    body_minimum_margins = [
        CANVAS_SIZE[0],
        CANVAS_SIZE[1],
        CANVAS_SIZE[0],
        CANVAS_SIZE[1],
    ]
    for index, frame in enumerate(frames):
        if frame.mode != "RGBA":
            errors.append(f"frame-{index:04d}: mode {frame.mode}, expected RGBA")
        if frame.size != CANVAS_SIZE:
            errors.append(
                f"frame-{index:04d}: size {frame.size}, expected {CANVAS_SIZE}"
            )
            continue
        try:
            root_frame = (
                root_reference_frames[index]
                if root_reference_frames is not None
                else frame
            )
            anchors.append(measure_root_anchor(root_frame))
        except RuntimeError as error:
            errors.append(f"frame-{index:04d}: {error}")
        clear_border_fractions.append(_clear_border_fraction(frame))
        frame_rgba = np.asarray(frame.convert("RGBA"), dtype=np.uint8)
        alpha = frame_rgba[:, :, 3]
        full_halo = measure_dark_background_halo(frame)
        full_frame_halo_measurements.append(full_halo)
        if allow_detached_edge_contact:
            # Detached gray projectile/smoke is intentionally soft and may
            # have no alpha>=245 core.  Keep its whole-frame measurement as a
            # diagnostic, but apply the strict fringe gate to the connected
            # character/held-prop component.  Once a prop disconnects, its
            # position and lifetime are governed by the right-only throw QA.
            strict_frame = frame.copy()
            strict_frame.putalpha(
                Image.fromarray(keep_character_component(alpha, 0))
            )
            halo_measurements.append(measure_dark_background_halo(strict_frame))
        else:
            halo_measurements.append(full_halo)
        ys, xs = np.nonzero(alpha >= 10)
        if xs.size:
            margins = [
                int(xs.min()),
                int(ys.min()),
                int(CANVAS_SIZE[0] - 1 - xs.max()),
                int(CANVAS_SIZE[1] - 1 - ys.max()),
            ]
            minimum_margins = [
                min(old, new) for old, new in zip(minimum_margins, margins, strict=True)
            ]
            detached_exit_is_allowed = (
                allow_detached_edge_contact
                and component_spec is not None
                and component_spec.detached_edge_contact_after_frame is not None
                and index > component_spec.detached_edge_contact_after_frame
            )
            if (
                not detached_exit_is_allowed
                and min(margins) < MINIMUM_VISIBLE_MARGIN_PIXELS
            ):
                content_margin_violations.append(
                    {
                        "frame": index,
                        "margins_left_top_right_bottom": margins,
                    }
                )
        # Bomb/smoke may intentionally exit the canvas while still connected to
        # an outstretched wing. In that clip, verify semantic character colors
        # instead of assuming the largest alpha component is only the body.
        body_alpha = (
            semantic_character_alpha(frame)
            if allow_detached_edge_contact
            else keep_character_component(alpha, 0)
        )
        if allow_detached_edge_contact:
            edge_views = {
                "left": (frame_rgba[:, 0], alpha[:, 0], body_alpha[:, 0]),
                "top": (frame_rgba[0, :], alpha[0, :], body_alpha[0, :]),
                "right": (frame_rgba[:, -1], alpha[:, -1], body_alpha[:, -1]),
                "bottom": (frame_rgba[-1, :], alpha[-1, :], body_alpha[-1, :]),
            }
            for side, (edge_rgba, edge_alpha, semantic_alpha) in edge_views.items():
                visible_edge = edge_alpha >= HALO_ALPHA_THRESHOLD
                if not np.any(visible_edge):
                    continue
                semantic_edge = semantic_alpha >= HALO_ALPHA_THRESHOLD
                prop_edge = visible_edge & ~semantic_edge
                prop_pixels = edge_rgba[prop_edge]
                observation = {
                    "frame": index,
                    "side": side,
                    "visible_pixels": int(np.sum(visible_edge)),
                    "semantic_character_pixels": int(np.sum(semantic_edge)),
                    "prop_or_smoke_pixels": int(np.sum(prop_edge)),
                    "prop_or_smoke_median_rgba": (
                        [
                            round(float(value), 3)
                            for value in np.median(prop_pixels, axis=0)
                        ]
                        if len(prop_pixels)
                        else None
                    ),
                    "ownership": (
                        "detached_prop_or_smoke"
                        if not np.any(semantic_edge)
                        else "mixed_with_semantic_character"
                    ),
                }
                edge_contact_classification.append(observation)
                if (
                    component_spec is not None
                    and component_spec.detached_components_right_only
                    and (
                        component_spec.detached_right_only_after_frame is None
                        or index > component_spec.detached_right_only_after_frame
                    )
                    and side != "right"
                    and np.any(prop_edge)
                ):
                    disallowed_detached_edge_contacts.append(observation)
        body_border = np.concatenate(
            (
                body_alpha[0],
                body_alpha[-1],
                body_alpha[1:-1, 0],
                body_alpha[1:-1, -1],
            )
        )
        body_clear_border_fractions.append(float(np.mean(body_border < 10)))
        body_ys, body_xs = np.nonzero(body_alpha >= 10)
        if body_xs.size:
            body_margins = [
                int(body_xs.min()),
                int(body_ys.min()),
                int(CANVAS_SIZE[0] - 1 - body_xs.max()),
                int(CANVAS_SIZE[1] - 1 - body_ys.max()),
            ]
            body_minimum_margins = [
                min(old, new)
                for old, new in zip(body_minimum_margins, body_margins, strict=True)
            ]
            if min(body_margins) < MINIMUM_VISIBLE_MARGIN_PIXELS:
                character_margin_violations.append(
                    {
                        "frame": index,
                        "margins_left_top_right_bottom": body_margins,
                    }
                )
        else:
            character_margin_violations.append(
                {
                    "frame": index,
                    "margins_left_top_right_bottom": None,
                    "reason": "semantic character mask is empty",
                }
            )

    target = measure_root_anchor(canonical_neutral)
    root_track = np.asarray(
        [(anchor.torso_x, anchor.foot_contact_y) for anchor in anchors],
        dtype=np.float64,
    )
    if len(root_track):
        root_x_error = np.abs(root_track[:, 0] - target.torso_x)
        root_y_error = np.abs(root_track[:, 1] - target.foot_contact_y)
        maximum_root_x_error = float(np.max(root_x_error))
        maximum_root_y_error = float(np.max(root_y_error))
        adjacent = np.diff(root_track, axis=0)
        maximum_adjacent_x = (
            float(np.max(np.abs(adjacent[:, 0]))) if len(adjacent) else 0.0
        )
        maximum_adjacent_y = (
            float(np.max(np.abs(adjacent[:, 1]))) if len(adjacent) else 0.0
        )
        if maximum_root_x_error > ROOT_X_TOLERANCE_PIXELS:
            errors.append(
                f"torso root x error {maximum_root_x_error:.2f}px exceeds "
                f"{ROOT_X_TOLERANCE_PIXELS:.2f}px"
            )
        if maximum_root_y_error > ROOT_Y_TOLERANCE_PIXELS:
            errors.append(
                f"support-foot y error {maximum_root_y_error:.2f}px exceeds "
                f"{ROOT_Y_TOLERANCE_PIXELS:.2f}px"
            )
    else:
        maximum_root_x_error = maximum_root_y_error = float("inf")
        maximum_adjacent_x = maximum_adjacent_y = float("inf")

    minimum_clear_border = min(clear_border_fractions, default=0.0)
    body_minimum_clear_border = min(body_clear_border_fractions, default=0.0)
    if (
        not allow_detached_edge_contact
        and minimum_clear_border < MINIMUM_CLEAR_BORDER_FRACTION
    ):
        errors.append(
            f"minimum clear-border fraction {minimum_clear_border:.3f} is below "
            f"{MINIMUM_CLEAR_BORDER_FRACTION:.3f}"
        )
    if content_margin_violations:
        errors.append(
            f"visible content violates the {MINIMUM_VISIBLE_MARGIN_PIXELS}px "
            f"minimum margin in {len(content_margin_violations)} frame(s): "
            f"{content_margin_violations}"
        )
    if body_minimum_clear_border < MINIMUM_CLEAR_BORDER_FRACTION:
        errors.append(
            f"main-character clear-border fraction {body_minimum_clear_border:.3f} "
            f"is below {MINIMUM_CLEAR_BORDER_FRACTION:.3f}"
        )
    if character_margin_violations:
        errors.append(
            f"main character violates the {MINIMUM_VISIBLE_MARGIN_PIXELS}px "
            f"minimum margin in {len(character_margin_violations)} frame(s): "
            f"{character_margin_violations}"
        )
    if disallowed_detached_edge_contacts:
        errors.append(
            "detached prop/smoke touches a non-right canvas edge: "
            f"{disallowed_detached_edge_contacts}"
        )

    if halo_measurements:
        def halo_failure(measurement: dict[str, object]) -> bool:
            return (
                int(measurement["unsupported_low_alpha_pixels"])
                > HALO_MAX_UNSUPPORTED_LOW_ALPHA_PIXELS_PER_FRAME
                or int(measurement["neutral_bright_edge_pixels"])
                > HALO_MAX_NEUTRAL_BRIGHT_PIXELS_PER_FRAME
                or int(measurement["opaque_matte_island_pixels"]) > 0
                or int(measurement["pale_lower_fringe_pixels"]) > 0
                or (
                    int(measurement["maximum_visible_pixels"])
                    > HALO_MAX_VISIBLE_PIXELS_PER_FRAME
                    and float(measurement["maximum_visible_edge_fraction"])
                    > HALO_MAX_VISIBLE_EDGE_FRACTION
                )
            )

        failed_halo_frames = [
            (index, measurement)
            for index, measurement in enumerate(halo_measurements)
            if halo_failure(measurement)
        ]
        candidates = failed_halo_frames or list(enumerate(halo_measurements))
        worst_halo_index, worst_halo = max(
            candidates,
            key=lambda item: (
                int(item[1]["opaque_matte_island_pixels"])
                + int(item[1]["pale_lower_fringe_pixels"]),
                int(item[1]["neutral_bright_edge_pixels"]),
                int(item[1]["unsupported_low_alpha_pixels"]),
                int(item[1]["maximum_visible_pixels"]),
                float(item[1]["maximum_visible_edge_fraction"]),
            ),
        )
        total_visible_by_background = {
            name: sum(
                int(measurement["visible_pixels_by_background"][name])
                for measurement in halo_measurements
            )
            for name in HALO_DARK_BACKGROUNDS
        }
        halo_over_limit = bool(failed_halo_frames)
        if halo_over_limit:
            errors.append(
                f"frame-{worst_halo_index:04d}: dark-background outer alpha fringe "
                f"has {worst_halo['maximum_visible_pixels']} visible pixels "
                f"({float(worst_halo['maximum_visible_edge_fraction']):.3%} "
                "of the outer alpha edge); "
                f"unsupported={worst_halo['unsupported_low_alpha_pixels']}, "
                f"neutral-bright={worst_halo['neutral_bright_edge_pixels']}, "
                f"opaque-matte={worst_halo['opaque_matte_island_pixels']}, "
                f"pale={worst_halo['pale_lower_fringe_pixels']}"
            )
    else:
        failed_halo_frames = []
        worst_halo_index = -1
        worst_halo = {
            "outer_edge_pixels": 0,
            "low_alpha_edge_pixels": 0,
            "unsupported_low_alpha_pixels": 0,
            "neutral_bright_edge_pixels": 0,
            "color_polluted_edge_pixels": 0,
            "opaque_matte_island_pixels": 0,
            "opaque_matte_islands": [],
            "pale_lower_fringe_pixels": 0,
            "pale_lower_fringe_clusters": [],
            "maximum_edge_color_residual": 0.0,
            "edge_color_residual_limit": HALO_EDGE_COLOR_RESIDUAL_MAXIMUM,
            "visible_pixels_by_background": {
                name: 0 for name in HALO_DARK_BACKGROUNDS
            },
            "maximum_visible_pixels": 0,
            "maximum_visible_edge_fraction": 0.0,
        }
        total_visible_by_background = {
            name: 0 for name in HALO_DARK_BACKGROUNDS
        }

    if full_frame_halo_measurements:
        full_worst_halo_index, full_worst_halo = max(
            enumerate(full_frame_halo_measurements),
            key=lambda item: (
                int(item[1]["unsupported_low_alpha_pixels"])
                + int(item[1]["opaque_matte_island_pixels"])
                + int(item[1]["pale_lower_fringe_pixels"]),
                int(item[1]["maximum_visible_pixels"]),
                float(item[1]["maximum_visible_edge_fraction"]),
            ),
        )
        full_total_visible_by_background = {
            name: sum(
                int(measurement["visible_pixels_by_background"][name])
                for measurement in full_frame_halo_measurements
            )
            for name in HALO_DARK_BACKGROUNDS
        }
    else:
        full_worst_halo_index = -1
        full_worst_halo = worst_halo
        full_total_visible_by_background = {
            name: 0 for name in HALO_DARK_BACKGROUNDS
        }

    component_policy = (
        inspect_detached_component_policy(frames, component_spec)
        if component_spec is not None
        else {"enabled": False, "passed": True}
    )
    if not component_policy["passed"]:
        errors.append(
            "detached-component policy failed: "
            f"left={len(component_policy['left_side_components'])}, "
            f"backtracks={len(component_policy['moving_prop_regressions'])}, "
            f"reappearances={len(component_policy['moving_prop_reappearances'])}"
        )

    first_exact = np.array_equal(
        np.asarray(frames[0].convert("RGBA")),
        np.asarray(expected_start.convert("RGBA")),
    )
    last_exact = np.array_equal(
        np.asarray(frames[-1].convert("RGBA")),
        np.asarray(expected_end.convert("RGBA")),
    )
    first_mae = _premultiplied_mae(frames[0], expected_start)
    last_mae = _premultiplied_mae(frames[-1], expected_end)
    if not first_exact:
        errors.append(f"first frame is not exact; premultiplied MAE={first_mae:.8f}")
    if not last_exact:
        errors.append(f"last frame is not exact; premultiplied MAE={last_mae:.8f}")

    return {
        "passed": not errors,
        "errors": errors,
        "frame_count": len(frames),
        "expected_frame_count": expected_count,
        "canvas": list(CANVAS_SIZE),
        "target_root": {
            "torso_x": target.torso_x,
            "foot_contact_y": target.foot_contact_y,
        },
        "root": {
            "measurement_source": (
                "body-only layer"
                if root_reference_frames is not None
                else "composited frame"
            ),
            "maximum_torso_x_error": maximum_root_x_error,
            "maximum_foot_contact_y_error": maximum_root_y_error,
            "maximum_adjacent_torso_x_step": maximum_adjacent_x,
            "maximum_adjacent_foot_y_step": maximum_adjacent_y,
            "torso_x_tolerance": ROOT_X_TOLERANCE_PIXELS,
            "foot_y_tolerance": ROOT_Y_TOLERANCE_PIXELS,
        },
        "boundary": {
            "minimum_clear_border_fraction": minimum_clear_border,
            "minimum_margins_left_top_right_bottom": minimum_margins,
            "detached_edge_contact_allowed": allow_detached_edge_contact,
            "detached_edge_contact_after_frame": (
                component_spec.detached_edge_contact_after_frame
                if component_spec is not None
                else None
            ),
            "minimum_required_margin_pixels": MINIMUM_VISIBLE_MARGIN_PIXELS,
            "content_margin_violations": content_margin_violations,
            "main_character_minimum_clear_border_fraction": body_minimum_clear_border,
            "main_character_minimum_margins_left_top_right_bottom": body_minimum_margins,
            "main_character_margin_violations": character_margin_violations,
            "edge_contact_classification": edge_contact_classification,
            "disallowed_detached_edge_contacts": disallowed_detached_edge_contacts,
        },
        "dark_background_halo": {
            "passed": not halo_over_limit,
            "backgrounds": {
                name: list(color) for name, color in HALO_DARK_BACKGROUNDS.items()
            },
            "maximum_visible_pixels_per_frame": HALO_MAX_VISIBLE_PIXELS_PER_FRAME,
            "maximum_visible_edge_fraction": HALO_MAX_VISIBLE_EDGE_FRACTION,
            "maximum_neutral_bright_pixels_per_frame": (
                HALO_MAX_NEUTRAL_BRIGHT_PIXELS_PER_FRAME
            ),
            "maximum_unsupported_low_alpha_pixels_per_frame": (
                HALO_MAX_UNSUPPORTED_LOW_ALPHA_PIXELS_PER_FRAME
            ),
            "failed_frames": [index for index, _ in failed_halo_frames],
            "worst_frame": worst_halo_index,
            "worst_frame_measurement": worst_halo,
            "total_visible_pixels_by_background": total_visible_by_background,
            "strict_scope": (
                "largest_connected_character_component"
                if allow_detached_edge_contact
                else "whole_frame"
            ),
            "whole_frame_diagnostic": {
                "worst_frame": full_worst_halo_index,
                "worst_frame_measurement": full_worst_halo,
                "total_visible_pixels_by_background": (
                    full_total_visible_by_background
                ),
                "detached_soft_prop_or_smoke_is_not_a_halo_failure": (
                    allow_detached_edge_contact
                ),
            },
        },
        "detached_component_policy": component_policy,
        "endpoints": {
            "first_exact": first_exact,
            "last_exact": last_exact,
            "first_premultiplied_mae": first_mae,
            "last_premultiplied_mae": last_mae,
        },
    }


def load_frame_sequence(
    directory: Path,
) -> tuple[list[Image.Image], list[Path], list[str]]:
    paths = sorted(
        directory.glob("frame-*.png"),
        key=lambda path: int(path.stem.removeprefix("frame-")),
    )
    errors: list[str] = []
    expected_names = [f"frame-{index:04d}.png" for index in range(len(paths))]
    if [path.name for path in paths] != expected_names:
        errors.append("frame filenames are not contiguous from frame-0000.png")
    frames: list[Image.Image] = []
    for path in paths:
        with Image.open(path) as opened:
            if opened.format != "PNG":
                errors.append(f"{path.name}: not a PNG")
            if opened.mode != "RGBA":
                errors.append(f"{path.name}: mode {opened.mode}, expected RGBA")
            frames.append(opened.convert("RGBA"))
    return frames, paths, errors


def write_qa_report(path: Path, report: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


def label_alpha_components(alpha: np.ndarray) -> list[AlphaComponent]:
    foreground = alpha >= 10
    points = np.argwhere(foreground)
    if points.size == 0:
        return []
    height, width = foreground.shape
    visited = np.zeros_like(foreground)
    components: list[AlphaComponent] = []

    for raw_y, raw_x in points:
        seed_y, seed_x = int(raw_y), int(raw_x)
        if visited[seed_y, seed_x]:
            continue
        visited[seed_y, seed_x] = True
        queue: deque[tuple[int, int]] = deque([(seed_y, seed_x)])
        indices: list[int] = []
        while queue:
            y, x = queue.popleft()
            indices.append((y * width) + x)
            for next_y, next_x in (
                (y - 1, x),
                (y + 1, x),
                (y, x - 1),
                (y, x + 1),
            ):
                if (
                    0 <= next_y < height
                    and 0 <= next_x < width
                    and foreground[next_y, next_x]
                    and not visited[next_y, next_x]
                ):
                    visited[next_y, next_x] = True
                    queue.append((next_y, next_x))
        flat_indices = np.asarray(indices, dtype=np.int64)
        ys = flat_indices // width
        xs = flat_indices % width
        components.append(
            AlphaComponent(
                flat_indices=flat_indices,
                area=len(indices),
                center_x=float(np.mean(xs)),
                center_y=float(np.mean(ys)),
                minimum_x=int(np.min(xs)),
                minimum_y=int(np.min(ys)),
                maximum_x=int(np.max(xs)),
                maximum_y=int(np.max(ys)),
            )
        )
    return components


def keep_character_component(
    alpha: np.ndarray,
    detached_component_min_area: int = 0,
) -> np.ndarray:
    """Discard interpolation debris while optionally retaining real props."""

    components = label_alpha_components(alpha)
    if not components:
        return alpha
    main_component = max(components, key=lambda component: component.area)
    kept = np.zeros_like(alpha, dtype=bool)
    kept_flat = kept.ravel()
    for component in components:
        if component is main_component or (
            detached_component_min_area > 0
            and component.area >= detached_component_min_area
        ):
            kept_flat[component.flat_indices] = True
    return np.where(kept, alpha, 0).astype(np.uint8)


def _component_median_luma(
    rgba: np.ndarray,
    component: AlphaComponent,
) -> float:
    pixels = rgba[:, :, :3].reshape(-1, 3)[component.flat_indices].astype(np.float32)
    luma = pixels[:, 0] * 0.2126 + pixels[:, 1] * 0.7152 + pixels[:, 2] * 0.0722
    return float(np.median(luma))


def inspect_bomb_throw_track(
    frames: list[Image.Image],
    spec: ClipSpec,
) -> dict[str, object]:
    """Track the released bomb without confusing earlier held-bomb poses.

    The source animation contains two visually separate gray-bomb runs: an
    earlier hold and the actual rightward throw.  Select the consecutive run
    that reaches furthest right of the stabilized torso, then require that
    run's center to advance monotonically until it disappears.  A later run is
    a real reappearance error; an earlier run is merely the authored hold.
    """

    if not spec.detached_motion_monotonic_right:
        return {
            "enabled": False,
            "passed": True,
            "started": False,
            "right_edge_exit_observed": False,
            "observations": [],
            "regressions": [],
            "reappearances": [],
        }

    all_observations: list[dict[str, object]] = []

    for frame_index, frame in enumerate(frames):
        rgba = np.asarray(frame.convert("RGBA"), dtype=np.uint8)
        rgb = rgba[:, :, :3].astype(np.int16)
        alpha = rgba[:, :, 3]
        root_x = measure_root_anchor(frame).torso_x
        _, x_grid = np.indices(alpha.shape)
        minimum = rgb.min(axis=2)
        chroma = rgb.max(axis=2) - minimum
        luma = (
            rgb[:, :, 0] * 0.2126
            + rgb[:, :, 1] * 0.7152
            + rgb[:, :, 2] * 0.0722
        )
        gray = (
            (alpha >= ROOT_ALPHA_THRESHOLD)
            & (chroma <= BOMB_THROW_GRAY_CHROMA_MAXIMUM)
            & (luma <= BOMB_THROW_GRAY_LUMA_MAXIMUM)
            & (x_grid > root_x + 20.0)
        )
        red, green, blue = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
        warm = (
            (alpha >= ROOT_ALPHA_THRESHOLD)
            & (red >= 140)
            & (green >= 45)
            & (blue <= 120)
            & ((red - green) >= 25)
            & ((green - blue) >= 10)
        )
        candidates: list[AlphaComponent] = []
        for component in label_alpha_components(
            np.where(gray, 255, 0).astype(np.uint8)
        ):
            if component.area < BOMB_THROW_COMPONENT_MINIMUM_AREA:
                continue
            if component.maximum_x <= root_x + BOMB_THROW_TRACK_START_OFFSET:
                continue
            y0 = max(0, component.minimum_y - BOMB_PROP_GRAY_SUPPORT_RADIUS)
            y1 = min(alpha.shape[0], component.maximum_y + BOMB_PROP_GRAY_SUPPORT_RADIUS + 1)
            x0 = max(0, component.minimum_x - BOMB_PROP_GRAY_SUPPORT_RADIUS)
            x1 = min(alpha.shape[1], component.maximum_x + BOMB_PROP_GRAY_SUPPORT_RADIUS + 1)
            if (
                int(np.sum(warm[y0:y1, x0:x1]))
                < BOMB_THROW_WARM_SUPPORT_MINIMUM
            ):
                # Right-side gray with no warm band/fuse is smoke, not a bomb.
                continue
            candidates.append(component)

        if not candidates:
            continue

        maximum_x = max(component.maximum_x for component in candidates)
        minimum_x = min(component.minimum_x for component in candidates)
        total_area = sum(component.area for component in candidates)
        weighted_center_x = float(
            sum(component.center_x * component.area for component in candidates)
            / total_area
        )
        observation = {
            "frame": frame_index,
            "root_x": root_x,
            "minimum_x": minimum_x,
            "maximum_x": maximum_x,
            "weighted_center_x": weighted_center_x,
            "rightward_offset": weighted_center_x - root_x,
            "rightmost_offset": maximum_x - root_x,
            "gray_area": total_area,
            "component_count": len(candidates),
        }
        all_observations.append(observation)

    segments: list[list[dict[str, object]]] = []
    for observation in all_observations:
        if (
            not segments
            or int(observation["frame"])
            > int(segments[-1][-1]["frame"]) + 1
        ):
            segments.append([observation])
        else:
            segments[-1].append(observation)

    segment_summaries: list[dict[str, object]] = []
    for index, segment in enumerate(segments):
        centers = [float(item["weighted_center_x"]) for item in segment]
        offsets = [float(item["rightward_offset"]) for item in segment]
        rightmost_offsets = [float(item["rightmost_offset"]) for item in segment]
        maximum_x_values = [int(item["maximum_x"]) for item in segment]
        segment_summaries.append(
            {
                "index": index,
                "first_frame": int(segment[0]["frame"]),
                "last_frame": int(segment[-1]["frame"]),
                "observation_count": len(segment),
                "peak_maximum_x": max(maximum_x_values),
                "peak_rightward_offset": max(offsets),
                "peak_rightmost_offset": max(rightmost_offsets),
                "center_x_progress": max(centers) - centers[0],
                "rightmost_x_progress": max(maximum_x_values) - maximum_x_values[0],
            }
        )

    selected_index: int | None = None
    selected: list[dict[str, object]] = []
    if segments:
        selected_index = max(
            range(len(segments)),
            key=lambda index: (
                float(segment_summaries[index]["peak_rightmost_offset"]),
                int(segment_summaries[index]["last_frame"]),
            ),
        )
        selected = segments[selected_index]

    regressions: list[dict[str, object]] = []
    running_maximum_x: int | None = None
    peak_position = (
        max(
            range(len(selected)),
            key=lambda index: int(selected[index]["maximum_x"]),
        )
        if selected
        else -1
    )
    # Once the leading edge has reached its furthest-right point, a shrinking
    # gray remnant is the bomb fading/disappearing rather than reverse motion.
    for observation in selected[: peak_position + 1]:
        maximum_x = int(observation["maximum_x"])
        if (
            running_maximum_x is not None
            and maximum_x
            < running_maximum_x - BOMB_MONOTONIC_BACKTRACK_TOLERANCE
        ):
            regressions.append(
                {**observation, "previous_maximum_x": running_maximum_x}
            )
        running_maximum_x = max(
            maximum_x,
            running_maximum_x if running_maximum_x is not None else maximum_x,
        )

    reappearances: list[dict[str, object]] = []
    if selected_index is not None:
        selected_end = int(selected[-1]["frame"])
        for later_segment in segments[selected_index + 1 :]:
            missing_frames = int(later_segment[0]["frame"]) - selected_end - 1
            reappearances.extend(
                {**observation, "missing_frames": missing_frames}
                for observation in later_segment
            )

    if selected:
        selected_right_edges = [int(item["maximum_x"]) for item in selected]
        rightward_progress = max(selected_right_edges) - selected_right_edges[0]
        exited = any(
            int(item["maximum_x"]) >= BOMB_THROW_EDGE_EXIT_X
            for item in selected
        )
    else:
        rightward_progress = 0.0
        exited = False
    insufficient_progress = (
        rightward_progress < BOMB_THROW_MINIMUM_RIGHTWARD_PROGRESS
    )
    started = bool(selected)
    passed = (
        started
        and not insufficient_progress
        and not regressions
        and not reappearances
    )
    return {
        "enabled": True,
        "passed": passed,
        "started": started,
        "right_edge_exit_observed": exited,
        "right_edge_exit_required": False,
        "selected_segment_index": selected_index,
        "selected_peak_frame": (
            int(selected[peak_position]["frame"])
            if peak_position >= 0
            else None
        ),
        "post_peak_fade_observation_count": (
            len(selected) - peak_position - 1 if peak_position >= 0 else 0
        ),
        "minimum_rightward_progress": BOMB_THROW_MINIMUM_RIGHTWARD_PROGRESS,
        "rightward_progress": rightward_progress,
        "insufficient_rightward_progress": insufficient_progress,
        "track_start_offset": BOMB_THROW_TRACK_START_OFFSET,
        "edge_exit_x": BOMB_THROW_EDGE_EXIT_X,
        "observations": selected,
        "all_observations": all_observations,
        "segments": segment_summaries,
        "regressions": regressions,
        "reappearances": reappearances,
    }


def inspect_detached_component_policy(
    frames: list[Image.Image],
    spec: ClipSpec,
) -> dict[str, object]:
    if spec.detached_component_min_area <= 0:
        return {
            "enabled": False,
            "passed": True,
            "left_side_components": [],
            "moving_prop_regressions": [],
            "moving_prop_reappearances": [],
            "moving_prop_track": [],
        }

    left_side: list[dict[str, object]] = []
    regressions: list[dict[str, object]] = []
    reappearances: list[dict[str, object]] = []
    moving_track: list[dict[str, object]] = []
    previous_moving_x: float | None = None
    missing_frames = 0
    moving_seen = False

    for frame_index, frame in enumerate(frames):
        rgba = np.asarray(frame.convert("RGBA"), dtype=np.uint8)
        components = label_alpha_components(rgba[:, :, 3])
        if not components:
            continue
        main_component = max(components, key=lambda component: component.area)
        root_x = measure_root_anchor(frame).torso_x
        detached = [
            component
            for component in components
            if component is not main_component
            and component.area >= spec.detached_component_min_area
        ]
        allowed: list[tuple[AlphaComponent, float]] = []
        for component in detached:
            observation = {
                "frame": frame_index,
                "center_x": component.center_x,
                "area": component.area,
                "bounds": [
                    component.minimum_x,
                    component.minimum_y,
                    component.maximum_x,
                    component.maximum_y,
                ],
            }
            if (
                spec.detached_components_right_only
                and (
                    spec.detached_right_only_after_frame is None
                    or frame_index > spec.detached_right_only_after_frame
                )
                and component.center_x
                <= root_x + BOMB_RIGHT_SIDE_MINIMUM_OFFSET
            ):
                left_side.append(observation)
                continue
            allowed.append((component, _component_median_luma(rgba, component)))

        motion_is_active = (
            spec.detached_motion_after_frame is None
            or frame_index >= spec.detached_motion_after_frame
        )
        moving_candidates = [
            (component, median_luma)
            for component, median_luma in allowed
            if motion_is_active
            and median_luma <= BOMB_DARK_COMPONENT_MEDIAN_LUMA_MAXIMUM
        ]
        if not moving_candidates:
            if moving_seen:
                missing_frames += 1
            continue

        component, median_luma = max(
            moving_candidates,
            key=lambda item: item[0].area,
        )
        observation = {
            "frame": frame_index,
            "center_x": component.center_x,
            "area": component.area,
            "median_luma": median_luma,
        }
        moving_track.append(observation)
        if spec.detached_motion_monotonic_right and previous_moving_x is not None:
            if component.center_x < (
                previous_moving_x - BOMB_MONOTONIC_BACKTRACK_TOLERANCE
            ):
                regressions.append(
                    {
                        **observation,
                        "previous_center_x": previous_moving_x,
                    }
                )
            if missing_frames > BOMB_REAPPEARANCE_GAP_FRAMES:
                reappearances.append(
                    {
                        **observation,
                        "missing_frames": missing_frames,
                    }
                )
        moving_seen = True
        missing_frames = 0
        previous_moving_x = component.center_x

    semantic_throw = inspect_bomb_throw_track(frames, spec)
    # Detached dark components also include the later smoke puff, so their raw
    # center track is diagnostic only.  The warm-band semantic tracker is what
    # distinguishes the thrown bomb from smoke and enforces monotonic motion.
    semantic_regressions = list(semantic_throw["regressions"])
    semantic_reappearances = list(semantic_throw["reappearances"])
    detached_rightward_progress = (
        max(float(item["center_x"]) for item in moving_track)
        - float(moving_track[0]["center_x"])
        if moving_track
        else 0.0
    )
    detached_track_passed = (
        bool(moving_track)
        and detached_rightward_progress >= BOMB_THROW_MINIMUM_RIGHTWARD_PROGRESS
        and not regressions
        and not reappearances
    )
    semantic_presence_passed = bool(semantic_throw.get("passed", True))
    return {
        "enabled": True,
        "passed": (
            not left_side
            and semantic_presence_passed
        ),
        "right_side_minimum_offset": BOMB_RIGHT_SIDE_MINIMUM_OFFSET,
        "backtrack_tolerance": BOMB_MONOTONIC_BACKTRACK_TOLERANCE,
        "left_side_components": left_side,
        "moving_prop_regressions": semantic_regressions,
        "moving_prop_reappearances": semantic_reappearances,
        "moving_prop_track": (
            semantic_throw["observations"]
            if semantic_throw["enabled"]
            else moving_track
        ),
        "detached_only_moving_prop_track": moving_track,
        "detached_only_motion_diagnostic": {
            "regressions": regressions,
            "reappearances": reappearances,
            "rightward_progress": detached_rightward_progress,
            "minimum_rightward_progress": BOMB_THROW_MINIMUM_RIGHTWARD_PROGRESS,
            "passed": detached_track_passed,
            "smoke_is_excluded_by_median_luma": True,
        },
        "semantic_gray_shape_regressions_diagnostic": semantic_regressions,
        "semantic_presence_passed": semantic_presence_passed,
        "semantic_throw_track": semantic_throw,
    }


def enforce_detached_component_policy(
    frames: list[Image.Image],
    spec: ClipSpec,
) -> tuple[list[Image.Image], dict[str, object]]:
    if spec.detached_component_min_area <= 0:
        return [frame.copy() for frame in frames], {
            "enabled": False,
            "removed_left_side_components": 0,
            "removed_non_monotonic_components": 0,
            "removed_reappearing_components": 0,
            "removed_pixels": 0,
        }

    # Deterministically clear impossible left-side detached pieces (the known
    # cross-cell failure).  Do not "repair" motion regressions by deleting a
    # real bomb/smoke frame: that would hide the QA failure and introduce an
    # abrupt pop.  The semantic throw tracker below reports such failures.
    result: list[Image.Image] = []
    removed_left = 0
    removed_pixels = 0

    for frame_index, frame in enumerate(frames):
        rgba = np.asarray(frame.convert("RGBA"), dtype=np.uint8).copy()
        components = label_alpha_components(rgba[:, :, 3])
        if not components:
            result.append(frame.copy())
            continue
        main_component = max(components, key=lambda component: component.area)
        root_x = measure_root_anchor(frame).torso_x
        detached = [
            component
            for component in components
            if component is not main_component
            and component.area >= spec.detached_component_min_area
        ]
        rejected: list[AlphaComponent] = []
        for component in detached:
            if (
                spec.detached_components_right_only
                and (
                    spec.detached_right_only_after_frame is None
                    or frame_index > spec.detached_right_only_after_frame
                )
                and component.center_x
                <= root_x + BOMB_RIGHT_SIDE_MINIMUM_OFFSET
            ):
                rejected.append(component)
                removed_left += 1

        alpha_channel = rgba[:, :, 3].copy()
        alpha_flat = alpha_channel.ravel()
        for component in rejected:
            alpha_flat[component.flat_indices] = 0
            removed_pixels += component.area
        rgba[:, :, 3] = alpha_channel
        result.append(Image.fromarray(rgba))

    post_policy = inspect_detached_component_policy(result, spec)
    return result, {
        "enabled": True,
        "removed_left_side_components": removed_left,
        "removed_non_monotonic_components": 0,
        "removed_reappearing_components": 0,
        "removed_pixels": removed_pixels,
        "post_policy": post_policy,
    }


def clear_generated_pngs(directory: Path, prefix: str) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    for path in directory.glob(f"{prefix}*.png"):
        path.unlink()


def inspect_authored_key_preflight(
    frames: list[Image.Image],
    spec: ClipSpec,
    *,
    required_detached_bomb_keys: tuple[int, ...] | None = None,
) -> dict[str, object]:
    """Fail before RIFE when an authored pose is cropped or misclassified."""

    errors: list[str] = []
    margin_observations: list[dict[str, object]] = []
    character_margin_observations: list[dict[str, object]] = []
    for index, frame in enumerate(frames):
        alpha = np.asarray(frame.convert("RGBA").getchannel("A"), dtype=np.uint8)
        ys, xs = np.nonzero(alpha >= HALO_ALPHA_THRESHOLD)
        if not xs.size:
            errors.append(f"key-{index:02d} has no visible pixels")
            continue
        margins = [
            int(xs.min()),
            int(ys.min()),
            int(frame.width - 1 - xs.max()),
            int(frame.height - 1 - ys.max()),
        ]
        required_right_margin = MINIMUM_VISIBLE_MARGIN_PIXELS
        if spec.allow_detached_edge_contact:
            if index == BOMB_FIRST_RELEASE_FULL_KEY_INDEX:
                required_right_margin = BOMB_FIRST_RELEASE_FULL_KEY_RIGHT_MARGIN
            elif index > BOMB_DETACHED_EDGE_ALLOWED_AFTER_KEY_INDEX:
                required_right_margin = 0
        required_margins = [
            MINIMUM_VISIBLE_MARGIN_PIXELS,
            MINIMUM_VISIBLE_MARGIN_PIXELS,
            required_right_margin,
            MINIMUM_VISIBLE_MARGIN_PIXELS,
        ]
        observation = {
            "key": index,
            "margins_left_top_right_bottom": margins,
            "required_margins_left_top_right_bottom": required_margins,
            "minimum_margin": min(margins),
        }
        margin_observations.append(observation)
        if any(
            actual < required
            for actual, required in zip(margins, required_margins, strict=True)
        ):
            errors.append(
                f"key-{index:02d} margin {margins} is below required "
                f"{required_margins}"
            )

        character_alpha = (
            semantic_character_alpha(frame)
            if spec.allow_detached_edge_contact
            else keep_character_component(alpha, 0)
        )
        character_ys, character_xs = np.nonzero(
            character_alpha >= HALO_ALPHA_THRESHOLD
        )
        if not character_xs.size:
            errors.append(f"key-{index:02d} semantic character mask is empty")
            continue
        character_margins = [
            int(character_xs.min()),
            int(character_ys.min()),
            int(frame.width - 1 - character_xs.max()),
            int(frame.height - 1 - character_ys.max()),
        ]
        character_margin_observations.append(
            {
                "key": index,
                "margins_left_top_right_bottom": character_margins,
                "minimum_margin": min(character_margins),
            }
        )
        if min(character_margins) < MINIMUM_VISIBLE_MARGIN_PIXELS:
            errors.append(
                f"key-{index:02d} character margin {character_margins} is below "
                f"{MINIMUM_VISIBLE_MARGIN_PIXELS}px"
            )

    component_policy = inspect_detached_component_policy(frames, spec)
    if not component_policy["passed"]:
        errors.append("authored-key detached-component/throw policy failed")

    detached_centroid_check: dict[str, object] | None = None
    if spec.detached_motion_monotonic_right:
        required_throw_keys = (
            required_detached_bomb_keys
            if required_detached_bomb_keys is not None
            else BOMB_DETACHED_THROW_KEY_INDICES
        )
        raw_track = component_policy.get("detached_only_moving_prop_track", [])
        by_key = {int(observation["frame"]): observation for observation in raw_track}
        missing_keys = [
            index for index in required_throw_keys if index not in by_key
        ]
        selected_track = [
            by_key[index]
            for index in required_throw_keys
            if index in by_key
        ]
        centroid_regressions: list[dict[str, object]] = []
        for previous, current in zip(selected_track, selected_track[1:]):
            if float(current["center_x"]) < (
                float(previous["center_x"])
                - BOMB_MONOTONIC_BACKTRACK_TOLERANCE
            ):
                centroid_regressions.append(
                    {
                        "key": int(current["frame"]),
                        "center_x": float(current["center_x"]),
                        "previous_center_x": float(previous["center_x"]),
                    }
                )
        disappeared_key_has_prop = BOMB_DISAPPEARED_KEY_INDEX in by_key
        detached_centroid_check = {
            "required_keys": list(required_throw_keys),
            "observations": selected_track,
            "missing_keys": missing_keys,
            "regressions": centroid_regressions,
            "disappeared_key": BOMB_DISAPPEARED_KEY_INDEX,
            "disappeared_key_has_prop": disappeared_key_has_prop,
            "passed": (
                not missing_keys
                and not centroid_regressions
                and not disappeared_key_has_prop
            ),
        }
        if missing_keys:
            errors.append(
                f"detached bomb missing from authored key(s) {missing_keys}"
            )
        if centroid_regressions:
            errors.append(
                f"detached bomb centroid regressed: {centroid_regressions}"
            )
        if disappeared_key_has_prop:
            errors.append(
                f"key-{BOMB_DISAPPEARED_KEY_INDEX:02d} still contains a dark "
                "detached bomb component"
            )

    smoke_classification: dict[str, object] | None = None
    if spec.smoke_pose_index is not None:
        smoke_index = spec.smoke_pose_index
        if smoke_index >= len(frames):
            errors.append(
                f"smoke pose index {smoke_index} is outside {len(frames)} keys"
            )
        else:
            throw_observations = component_policy.get(
                "semantic_throw_track", {}
            ).get("all_observations", [])
            classified_as_bomb = any(
                int(observation["frame"]) == smoke_index
                for observation in throw_observations
            )
            rgba = np.asarray(frames[smoke_index].convert("RGBA"), dtype=np.uint8)
            components = sorted(
                label_alpha_components(rgba[:, :, 3]),
                key=lambda component: component.area,
                reverse=True,
            )
            detached = [
                component
                for component in components[1:]
                if component.area >= spec.detached_component_min_area
            ]
            smoke_classification = {
                "key": smoke_index,
                "classified_as_bomb": classified_as_bomb,
                "detached_components": [
                    {
                        "area": component.area,
                        "center_x": component.center_x,
                        "bounds": [
                            component.minimum_x,
                            component.minimum_y,
                            component.maximum_x,
                            component.maximum_y,
                        ],
                        "median_luma": _component_median_luma(rgba, component),
                    }
                    for component in detached
                ],
            }
            if classified_as_bomb:
                errors.append(
                    f"key-{smoke_index:02d} smoke was classified as a bomb"
                )

    return {
        "passed": not errors,
        "errors": errors,
        "minimum_required_margin_pixels": MINIMUM_VISIBLE_MARGIN_PIXELS,
        "minimum_margin": min(
            (int(item["minimum_margin"]) for item in margin_observations),
            default=-1,
        ),
        "margins": margin_observations,
        "character_margins": character_margin_observations,
        "minimum_character_margin": min(
            (
                int(item["minimum_margin"])
                for item in character_margin_observations
            ),
            default=-1,
        ),
        "bomb_release_margin_policy": (
            {
                "all_content_minimum_margin_through_key": (
                    BOMB_DETACHED_EDGE_ALLOWED_AFTER_KEY_INDEX - 1
                ),
                "all_content_minimum_margin": MINIMUM_VISIBLE_MARGIN_PIXELS,
                "first_fully_released_key": BOMB_FIRST_RELEASE_FULL_KEY_INDEX,
                "first_fully_released_key_right_margin": (
                    BOMB_FIRST_RELEASE_FULL_KEY_RIGHT_MARGIN
                ),
                "detached_right_prop_may_exit_after_key": (
                    BOMB_DETACHED_EDGE_ALLOWED_AFTER_KEY_INDEX
                ),
                "character_all_keys_all_sides": MINIMUM_VISIBLE_MARGIN_PIXELS,
            }
            if spec.allow_detached_edge_contact
            else None
        ),
        "bomb_action_keys": margin_observations[
            max(0, BOMB_DETACHED_THROW_KEY_INDICES[0] - 4)
            : BOMB_DISAPPEARED_KEY_INDEX + 2
        ],
        "component_policy": component_policy,
        "detached_bomb_centroid_check": detached_centroid_check,
        "smoke_classification": smoke_classification,
    }


def _bomb_gray_mask(image: Image.Image) -> np.ndarray:
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8)
    rgb = rgba[:, :, :3].astype(np.int16)
    alpha = rgba[:, :, 3]
    chroma = rgb.max(axis=2) - rgb.min(axis=2)
    luma = (
        rgb[:, :, 0] * 0.2126
        + rgb[:, :, 1] * 0.7152
        + rgb[:, :, 2] * 0.0722
    )
    return (
        (alpha >= HALO_ALPHA_THRESHOLD)
        & (chroma <= 65)
        & (luma <= 180.0)
    )


def _bomb_gray_center(image: Image.Image) -> tuple[float, float, int]:
    gray = _bomb_gray_mask(image)
    components = label_alpha_components(
        np.where(gray, 255, 0).astype(np.uint8)
    )
    candidates = [
        component
        for component in components
        if component.area >= BOMB_THROW_COMPONENT_MINIMUM_AREA
    ]
    if not candidates:
        raise RuntimeError("could not locate the bomb's gray core")
    component = max(candidates, key=lambda item: item.area)
    return component.center_x, component.center_y, component.area


def _extract_clean_bomb_sprite(
    reference: Image.Image,
) -> tuple[Image.Image, tuple[float, float], dict[str, object]]:
    rgba = np.asarray(reference.convert("RGBA"), dtype=np.uint8)
    components = sorted(
        label_alpha_components(rgba[:, :, 3]),
        key=lambda component: component.area,
        reverse=True,
    )
    if len(components) < 2:
        raise RuntimeError("clean release reference has no detached bomb")
    body = components[0]
    candidates = [
        component
        for component in components[1:]
        if component.area >= BOMB_THROW_COMPONENT_MINIMUM_AREA
        and component.center_x > body.center_x
        and _component_median_luma(rgba, component)
        <= BOMB_THROW_GRAY_LUMA_MAXIMUM
    ]
    if not candidates:
        raise RuntimeError("clean release reference has no right-side bomb component")
    prop = max(candidates, key=lambda component: component.area)
    component_mask = np.zeros(rgba.shape[:2], dtype=bool)
    component_mask.ravel()[prop.flat_indices] = True
    isolated = rgba.copy()
    isolated[:, :, 3] = np.where(component_mask, rgba[:, :, 3], 0)
    left, top, right, bottom = (
        prop.minimum_x,
        prop.minimum_y,
        prop.maximum_x + 1,
        prop.maximum_y + 1,
    )
    sprite = add_rgb_edge_bleed(
        Image.fromarray(isolated).crop((left, top, right, bottom))
    )
    gray_x, gray_y, gray_area = _bomb_gray_center(sprite)
    return sprite, (gray_x, gray_y), {
        "source_component_area": prop.area,
        "source_bounds": [left, top, right - 1, bottom - 1],
        "sprite_size": list(sprite.size),
        "local_gray_anchor": [gray_x, gray_y],
        "local_gray_area": gray_area,
        "source_median_luma": _component_median_luma(rgba, prop),
    }


def _place_sprite_at_gray_center(
    sprite: Image.Image,
    gray_anchor: tuple[float, float],
    center: tuple[float, float] | None,
) -> tuple[Image.Image, dict[str, object]]:
    canvas = Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
    if center is None:
        return canvas, {
            "center": None,
            "top_left": None,
            "visible_alpha_pixels": 0,
        }
    left = round(center[0] - gray_anchor[0])
    top = round(center[1] - gray_anchor[1])
    source_left = max(0, -left)
    source_top = max(0, -top)
    source_right = min(sprite.width, CANVAS_SIZE[0] - left)
    source_bottom = min(sprite.height, CANVAS_SIZE[1] - top)
    if source_left < source_right and source_top < source_bottom:
        visible = sprite.crop(
            (source_left, source_top, source_right, source_bottom)
        )
        canvas.alpha_composite(
            visible,
            (left + source_left, top + source_top),
        )
    alpha = np.asarray(canvas.getchannel("A"), dtype=np.uint8)
    return canvas, {
        "center": [float(center[0]), float(center[1])],
        "top_left": [left, top],
        "visible_alpha_pixels": int(np.sum(alpha >= HALO_ALPHA_THRESHOLD)),
        "visible_alpha_sum": int(np.sum(alpha)),
    }


def _warm_character_mask(image: Image.Image) -> np.ndarray:
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8)
    rgb = rgba[:, :, :3].astype(np.int16)
    alpha = rgba[:, :, 3]
    red, green, blue = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    return (
        (alpha >= HALO_ALPHA_THRESHOLD)
        & (red >= 90)
        & (red <= 245)
        & (green >= 35)
        & (green <= 195)
        & (blue <= 150)
        & ((red - green) >= 15)
        & ((green - blue) >= 5)
    )


def _derive_foreground_mask(
    body: Image.Image,
    truth: Image.Image,
    prop_layer: Image.Image,
    *,
    fully_hide_prop: bool = False,
) -> Image.Image:
    body_rgba = np.asarray(body.convert("RGBA"), dtype=np.uint8)
    body_alpha = body_rgba[:, :, 3]
    prop_rgba = np.asarray(prop_layer.convert("RGBA"), dtype=np.uint8)
    prop_alpha = prop_rgba[:, :, 3]
    if fully_hide_prop:
        mask = (prop_alpha >= HALO_ALPHA_THRESHOLD) & (
            body_alpha >= HALO_ALPHA_THRESHOLD
        )
        for _ in range(2):
            mask |= _adjacent_to(mask)
        mask &= body_alpha >= HALO_ALPHA_THRESHOLD
    else:
        prop_rgb = prop_rgba[:, :, :3].astype(np.int16)
        prop_chroma = prop_rgb.max(axis=2) - prop_rgb.min(axis=2)
        prop_luma = (
            prop_rgb[:, :, 0] * 0.2126
            + prop_rgb[:, :, 1] * 0.7152
            + prop_rgb[:, :, 2] * 0.0722
        )
        prop_dark = (
            (prop_alpha >= HALO_ALPHA_THRESHOLD)
            & (prop_chroma <= 65)
            & (prop_luma <= 175.0)
        )
        seed = _warm_character_mask(truth) & prop_dark
        expanded = seed | _adjacent_to(seed)
        core = expanded & _warm_character_mask(body)
        mask = (core | _adjacent_to(core)) & (
            body_alpha >= HALO_ALPHA_THRESHOLD
        )
    foreground_alpha = np.where(mask, body_alpha, 0).astype(np.uint8)
    return Image.fromarray(foreground_alpha, mode="L")


def _foreground_layer_from_body(
    body: Image.Image,
    mask: Image.Image,
) -> Image.Image:
    rgba = np.asarray(body.convert("RGBA"), dtype=np.uint8).copy()
    body_alpha = rgba[:, :, 3]
    mask_alpha = np.asarray(mask.convert("L"), dtype=np.uint8)
    rgba[:, :, 3] = np.minimum(body_alpha, mask_alpha)
    return Image.fromarray(rgba)


def _compose_bomb_layers(
    body: Image.Image,
    prop_layer: Image.Image,
    foreground_mask: Image.Image,
    smoke_layer: Image.Image | None = None,
) -> Image.Image:
    composite = body.convert("RGBA").copy()
    composite.alpha_composite(prop_layer.convert("RGBA"))
    composite.alpha_composite(_foreground_layer_from_body(body, foreground_mask))
    if smoke_layer is not None:
        composite.alpha_composite(smoke_layer.convert("RGBA"))
    return composite


def _extract_smoke_layer(reference: Image.Image) -> tuple[Image.Image, dict[str, object]]:
    rgba = np.asarray(reference.convert("RGBA"), dtype=np.uint8)
    components = sorted(
        label_alpha_components(rgba[:, :, 3]),
        key=lambda component: component.area,
        reverse=True,
    )
    if len(components) < 2:
        raise RuntimeError("smoke reference has no detached smoke component")
    main = components[0]
    root_x = measure_root_anchor(reference).torso_x
    smoke_components = [
        component
        for component in components[1:]
        if component.area >= 12
        and component.center_x > root_x + BOMB_RIGHT_SIDE_MINIMUM_OFFSET
    ]
    if not smoke_components:
        raise RuntimeError("smoke reference has no right-side smoke components")
    keep = np.zeros(rgba.shape[:2], dtype=bool)
    for component in smoke_components:
        keep.ravel()[component.flat_indices] = True
    smoke = rgba.copy()
    smoke[:, :, 3] = np.where(keep, rgba[:, :, 3], 0)
    return add_rgb_edge_bleed(Image.fromarray(smoke)), {
        "component_count": len(smoke_components),
        "components": [
            {
                "area": component.area,
                "center": [component.center_x, component.center_y],
                "bounds": [
                    component.minimum_x,
                    component.minimum_y,
                    component.maximum_x,
                    component.maximum_y,
                ],
                "median_luma": _component_median_luma(rgba, component),
            }
            for component in smoke_components
        ],
        "excluded_main_area": main.area,
    }


def _find_hidden_prop_center(
    body: Image.Image,
    sprite: Image.Image,
    gray_anchor: tuple[float, float],
) -> tuple[tuple[float, float], dict[str, object]]:
    body_alpha = np.asarray(body.getchannel("A"), dtype=np.uint8)
    root = measure_root_anchor(body)
    best: tuple[int, float, float, int] | None = None
    for y in range(round(body.height * 0.70), round(body.height * 0.88) + 1, 2):
        for x in range(round(root.torso_x - 55), round(root.torso_x + 16) + 1, 2):
            layer, _ = _place_sprite_at_gray_center(sprite, gray_anchor, (x, y))
            prop_alpha = np.asarray(layer.getchannel("A"), dtype=np.uint8)
            visible = prop_alpha >= HALO_ALPHA_THRESHOLD
            outside = visible & (body_alpha < 220)
            score = int(np.sum(prop_alpha[outside]))
            outside_pixels = int(np.sum(outside))
            candidate = (score, float(x), float(y), outside_pixels)
            if best is None or candidate < best:
                best = candidate
    if best is None:
        raise RuntimeError("could not place the initially hidden bomb")
    score, x, y, outside_pixels = best
    return (x, y), {
        "center": [x, y],
        "alpha_outside_opaque_body_sum": score,
        "pixels_outside_opaque_body": outside_pixels,
        "method": "grid search inside lower central body; foreground mask occludes it",
    }


def _interpolate_control_center(
    controls: dict[int, tuple[float, float]],
    frame_index: int,
) -> tuple[float, float] | None:
    if frame_index < min(controls) or frame_index >= BOMB_PROP_GONE_FRAME:
        return None
    if frame_index in controls:
        return controls[frame_index]
    before = max(index for index in controls if index < frame_index)
    after = min(index for index in controls if index > frame_index)
    fraction = (frame_index - before) / (after - before)
    first = controls[before]
    second = controls[after]
    return (
        first[0] + ((second[0] - first[0]) * fraction),
        first[1] + ((second[1] - first[1]) * fraction),
    )


def prepare_layered_bomb_bundle(
    assets: Path,
    spec: ClipSpec,
    body_cells: list[Image.Image],
    canonical_neutral: Image.Image,
) -> tuple[Path, dict[str, object], LayeredBombBundle]:
    if not spec.uses_layered_bomb_pipeline:
        raise ValueError("layered bomb preparation requires a reference sheet")
    body_spec = replace(
        spec,
        name="BombBody",
        detached_component_min_area=0,
        allow_detached_edge_contact=False,
        detached_components_right_only=False,
        detached_motion_monotonic_right=False,
        smoke_pose_index=None,
        layered_reference_sheet_name=None,
        layered_midpoint_sheet_name=None,
        detached_right_only_after_frame=None,
        detached_motion_after_frame=None,
    )
    body_key_dir, body_stabilization = write_keys(
        assets,
        body_spec,
        body_cells,
        canonical_neutral,
    )
    body_keys = [
        Image.open(body_key_dir / f"key-{index:02d}.png").convert("RGBA")
        for index in range(spec.pose_count)
    ]

    reference_path = (
        assets / "AnimationSources" / str(spec.layered_reference_sheet_name)
    )
    reference_cells = apply_authored_order(
        normalize_cells(
            extract_cells(
                reference_path,
                spec.columns,
                spec.rows,
                spec.source_count,
                spec.detached_component_min_area,
            )
        ),
        spec,
    )
    reference_cells[0] = canonical_neutral.copy()
    reference_cells[-1] = canonical_neutral.copy()
    reference_keys, reference_stabilization = stabilize_frames(
        reference_cells,
        canonical_neutral,
        exact_endpoints=(canonical_neutral, canonical_neutral),
    )

    midpoint_name = spec.layered_midpoint_sheet_name
    if midpoint_name is None:
        raise RuntimeError("layered Bomb is missing its swing midpoint sheet")
    midpoint_path = assets / "AnimationSources" / midpoint_name
    neighbor_heights = [
        alpha_bbox(reference_keys[index])[3] - alpha_bbox(reference_keys[index])[1]
        for index in (6, 7)
    ]
    midpoint_references = normalize_cells(
        extract_cells(midpoint_path, 2, 1, 2, spec.detached_component_min_area),
        target_height=round(float(np.mean(neighbor_heights))),
    )
    midpoint_references, midpoint_stabilization = stabilize_frames(
        midpoint_references,
        canonical_neutral,
    )

    prop_sprite, prop_gray_anchor, sprite_report = _extract_clean_bomb_sprite(
        reference_keys[8]
    )
    controls: dict[int, tuple[float, float]] = {}
    hidden_center, hidden_report = _find_hidden_prop_center(
        body_keys[1], prop_sprite, prop_gray_anchor
    )
    controls[8] = hidden_center
    reference_controls = {
        16: 2,
        24: 3,
        32: 4,
        40: 5,
        48: 6,
        60: 7,
        68: 8,
    }
    center_observations: list[dict[str, object]] = []
    for frame_index, key_index in reference_controls.items():
        x, y, area = _bomb_gray_center(reference_keys[key_index])
        controls[frame_index] = (x, y)
        center_observations.append(
            {
                "frame": frame_index,
                "source": f"reference-key-{key_index:02d}",
                "center": [x, y],
                "gray_area": area,
            }
        )
    for frame_index, midpoint in zip((52, 56), midpoint_references, strict=True):
        x, y, area = _bomb_gray_center(midpoint)
        controls[frame_index] = (x, y)
        center_observations.append(
            {
                "frame": frame_index,
                "source": f"swing-midpoint-{(frame_index - 52) // 4}",
                "center": [x, y],
                "gray_area": area,
            }
        )
    released_center = controls[68]
    for frame_index, (shift_x, shift_y) in BOMB_PROP_CONTROL_SHIFTS.items():
        controls[frame_index] = (
            released_center[0] + shift_x,
            released_center[1] + shift_y,
        )
        center_observations.append(
            {
                "frame": frame_index,
                "source": "deterministic-release-translation",
                "center": list(controls[frame_index]),
                "shift_from_clean_release": [shift_x, shift_y],
            }
        )
    center_observations.sort(key=lambda item: int(item["frame"]))

    smoke_layer, smoke_report = _extract_smoke_layer(reference_keys[12])
    foreground_key_masks: dict[int, Image.Image] = {}
    authored_indices = resolve_authored_frame_indices(spec, 121)
    for key_index, frame_index in enumerate(authored_indices):
        center = _interpolate_control_center(controls, frame_index)
        prop_layer, _ = _place_sprite_at_gray_center(
            prop_sprite,
            prop_gray_anchor,
            center,
        )
        if key_index == 1:
            mask = _derive_foreground_mask(
                body_keys[key_index],
                body_keys[key_index],
                prop_layer,
                fully_hide_prop=True,
            )
        elif 2 <= key_index <= 7:
            mask = _derive_foreground_mask(
                body_keys[key_index],
                reference_keys[key_index],
                prop_layer,
            )
        else:
            mask = Image.new("L", CANVAS_SIZE, 0)
        foreground_key_masks[frame_index] = mask

    empty_layer = Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
    composite_keys: list[Image.Image] = []
    authored_prop_placements: list[dict[str, object]] = []
    composite_key_dir = assets / "AnimationKeys" / spec.name
    foreground_key_dir = assets / "AnimationKeys" / "BombForeground"
    clear_generated_pngs(composite_key_dir, "key-")
    clear_generated_pngs(foreground_key_dir, "key-")
    for key_index, (frame_index, body) in enumerate(
        zip(authored_indices, body_keys, strict=True)
    ):
        center = _interpolate_control_center(controls, frame_index)
        prop_layer, placement = _place_sprite_at_gray_center(
            prop_sprite,
            prop_gray_anchor,
            center,
        )
        authored_prop_placements.append(
            {"key": key_index, "frame": frame_index, **placement}
        )
        smoke = smoke_layer if frame_index == BOMB_SMOKE_PEAK_FRAME else empty_layer
        composite = _compose_bomb_layers(
            body,
            prop_layer,
            foreground_key_masks[frame_index],
            smoke,
        )
        if key_index in (0, 1, len(body_keys) - 1):
            # Key 1 contains the bomb fully hidden behind the body. Preserve
            # the exact body pixels at the authored instant.
            composite = body.copy()
        else:
            composite = add_rgb_edge_bleed(composite)
        composite.save(
            composite_key_dir / f"key-{key_index:02d}.png",
            optimize=True,
        )
        foreground_preview = _foreground_layer_from_body(
            body,
            foreground_key_masks[frame_index],
        )
        foreground_preview.save(
            foreground_key_dir / f"key-{key_index:02d}.png",
            optimize=True,
        )
        composite_keys.append(composite)

    key_policy_spec = replace(
        spec,
        detached_right_only_after_frame=7,
        detached_motion_after_frame=7,
    )
    preflight = inspect_authored_key_preflight(
        composite_keys,
        key_policy_spec,
        # key-10/frame-76 is deliberately mostly beyond the right edge, so a
        # median-luma connected-component test is no longer meaningful there.
        # Its nonzero clipped strip and intended center are asserted below.
        required_detached_bomb_keys=(8, 9),
    )
    key10_placement = authored_prop_placements[10]
    key10_center = key10_placement.get("center")
    key10_is_expected_exit = (
        int(key10_placement["visible_alpha_pixels"]) > 0
        and key10_center is not None
        and float(key10_center[0]) > CANVAS_SIZE[0]
    )
    if not key10_is_expected_exit:
        preflight["errors"].append(
            "key-10/frame-76 must retain a clipped right-edge prop strip with "
            "its intended center beyond the canvas"
        )
        preflight["passed"] = False
    preflight["layered_exit_key"] = {
        **key10_placement,
        "expected_center_beyond_right_edge": True,
        "passed": key10_is_expected_exit,
    }
    if not preflight["passed"]:
        details = "; ".join(str(error) for error in preflight["errors"])
        raise RuntimeError(f"Bomb layered authored-key preflight failed: {details}")

    report: dict[str, object] = {
        "enabled": True,
        "method": (
            "body-only RIFE; one fixed RGBA bomb sprite translated at integer "
            "positions; body-sampled foreground hand/wing mask; separate smoke"
        ),
        "body_stabilization": body_stabilization,
        "reference_stabilization": reference_stabilization,
        "midpoint_stabilization": midpoint_stabilization,
        "midpoint_neighbor_heights": neighbor_heights,
        "prop_sprite": sprite_report,
        "hidden_prop": hidden_report,
        "prop_control_centers": center_observations,
        "authored_prop_placements": authored_prop_placements,
        "smoke": smoke_report,
        "foreground_authored_pixel_counts": {
            str(frame): int(np.sum(np.asarray(mask) >= HALO_ALPHA_THRESHOLD))
            for frame, mask in foreground_key_masks.items()
        },
        "authored_key_preflight": preflight,
    }
    bundle = LayeredBombBundle(
        body_key_dir=body_key_dir,
        body_keys=body_keys,
        reference_keys=reference_keys,
        midpoint_references=midpoint_references,
        prop_sprite=prop_sprite,
        prop_gray_anchor=prop_gray_anchor,
        prop_control_centers=controls,
        smoke_layer=smoke_layer,
        foreground_key_masks=foreground_key_masks,
        composite_keys=composite_keys,
        report=report,
    )
    return composite_key_dir, report, bundle


def write_keys(
    root: Path,
    spec: ClipSpec,
    cells: list[Image.Image],
    canonical_neutral: Image.Image,
) -> tuple[Path, dict[str, object]]:
    key_dir = root / "AnimationKeys" / spec.name
    clear_generated_pngs(key_dir, "key-")
    prepared = [cell.copy() for cell in cells]
    prepared[0] = canonical_neutral.copy()
    prepared[-1] = canonical_neutral.copy()
    prepared, stabilization = stabilize_frames(
        prepared,
        canonical_neutral,
        exact_endpoints=(canonical_neutral, canonical_neutral),
    )
    prepared, component_policy = enforce_detached_component_policy(prepared, spec)
    stabilization["component_policy"] = component_policy
    outputs: list[Image.Image] = []
    for index, cell in enumerate(prepared):
        # Preserve the canonical endpoint pixels exactly.  Re-running edge
        # bleed would change otherwise invisible RGB beyond the alpha edge.
        output = cell if index in (0, len(prepared) - 1) else add_rgb_edge_bleed(cell)
        output.save(key_dir / f"key-{index:02d}.png", optimize=True)
        outputs.append(output)
    preflight = inspect_authored_key_preflight(outputs, spec)
    stabilization["authored_key_preflight"] = preflight
    if not preflight["passed"]:
        details = "; ".join(str(error) for error in preflight["errors"])
        raise RuntimeError(f"{spec.name} authored-key preflight failed: {details}")
    return key_dir, stabilization


def resolve_authored_frame_indices(
    spec: ClipSpec,
    output_count: int,
) -> list[int]:
    if spec.authored_frame_indices is not None:
        indices = list(spec.authored_frame_indices)
    else:
        indices = [
            round(index * (output_count - 1) / (spec.pose_count - 1))
            for index in range(spec.pose_count)
        ]
    if len(indices) != spec.pose_count:
        raise RuntimeError(
            f"{spec.name}: authored timeline has {len(indices)} indices for "
            f"{spec.pose_count} keys"
        )
    if indices[0] != 0 or indices[-1] != output_count - 1:
        raise RuntimeError(
            f"{spec.name}: authored timeline must start at 0 and end at "
            f"{output_count - 1}"
        )
    if any(second <= first for first, second in zip(indices, indices[1:])):
        raise RuntimeError(f"{spec.name}: authored timeline is not strictly increasing")
    return indices


def _invoke_rife(
    rife: Path,
    model: Path,
    input_dir: Path,
    output_dir: Path,
    requested_count: int,
) -> None:
    command = [
        str(rife),
        "-i",
        str(input_dir),
        "-o",
        str(output_dir),
        "-n",
        str(requested_count),
        "-m",
        str(model),
        "-f",
        "%08d.png",
        "-j",
        "2:2:2",
    ]
    subprocess.run(command, check=True)


def _interpolate_rife_timeline(
    rife: Path,
    model: Path,
    input_dir: Path,
    result_dir: Path,
    authored_frame_indices: list[int],
    workspace: Path,
    label: str,
) -> None:
    workspace.mkdir()
    output_index = 0
    for segment_index, (first_frame, last_frame) in enumerate(
        zip(authored_frame_indices, authored_frame_indices[1:])
    ):
        segment_count = last_frame - first_frame + 1
        segment_input = workspace / f"input-{segment_index:02d}"
        segment_output = workspace / f"output-{segment_index:02d}"
        segment_input.mkdir()
        segment_output.mkdir()
        shutil.copyfile(
            input_dir / f"{segment_index:08d}.png",
            segment_input / "00000000.png",
        )
        shutil.copyfile(
            input_dir / f"{segment_index + 1:08d}.png",
            segment_input / "00000001.png",
        )
        print(
            f"Bomb layered {label}: segment {segment_index + 1}/"
            f"{len(authored_frame_indices) - 1} -> {segment_count} frames",
            flush=True,
        )
        # RIFE directory mode samples fx=i*input_count/output_count, not
        # i*(input_count-1)/(output_count-1). With two input keys, asking for
        # segment_count frames reaches the final key halfway through and
        # repeats it for the rest. Oversample the directory timeline and keep
        # exactly t=0..1 so each authored interval receives continuous motion.
        directory_count = 2 * (segment_count - 1)
        _invoke_rife(rife, model, segment_input, segment_output, directory_count)
        segment_frames = sorted(segment_output.glob("*.png"))
        if len(segment_frames) != directory_count:
            raise RuntimeError(
                f"RIFE returned {len(segment_frames)} frames for {label} "
                f"segment {segment_index}; expected {directory_count}"
            )
        segment_frames = segment_frames[:segment_count]
        if segment_index:
            segment_frames = segment_frames[1:]
        for segment_frame in segment_frames:
            shutil.copyfile(
                segment_frame,
                result_dir / f"{output_index:08d}.png",
            )
            output_index += 1
    expected = authored_frame_indices[-1] + 1
    if output_index != expected:
        raise RuntimeError(
            f"segmented RIFE assembled {output_index} {label} frames; "
            f"expected {expected}"
        )


def _interpolate_layered_body(
    rife: Path,
    model: Path,
    keys: list[Image.Image],
    authored_frame_indices: list[int],
) -> list[Image.Image]:
    with tempfile.TemporaryDirectory(prefix="duck-pet-bomb-body-") as temporary:
        temporary_path = Path(temporary)
        matte_keys = temporary_path / "keys"
        mask_keys = temporary_path / "masks"
        interpolated = temporary_path / "interpolated"
        interpolated_masks = temporary_path / "interpolated-masks"
        for path in (matte_keys, mask_keys, interpolated, interpolated_masks):
            path.mkdir()
        for index, key in enumerate(keys):
            matte = Image.new(
                "RGB",
                key.size,
                tuple(int(value) for value in RIFE_MATTE_RGB),
            )
            matte.paste(key, (0, 0), key)
            matte.save(matte_keys / f"{index:08d}.png", optimize=True)
            alpha = key.getchannel("A")
            Image.merge("RGB", (alpha, alpha, alpha)).save(
                mask_keys / f"{index:08d}.png",
                optimize=True,
            )
        _interpolate_rife_timeline(
            rife,
            model,
            matte_keys,
            interpolated,
            authored_frame_indices,
            temporary_path / "rgb-segments",
            "body RGB",
        )
        _interpolate_rife_timeline(
            rife,
            model,
            mask_keys,
            interpolated_masks,
            authored_frame_indices,
            temporary_path / "mask-segments",
            "body alpha",
        )
        generated = sorted(interpolated.glob("*.png"))
        generated_masks = sorted(interpolated_masks.glob("*.png"))
        expected = authored_frame_indices[-1] + 1
        if len(generated) != expected or len(generated_masks) != expected:
            raise RuntimeError(
                f"RIFE returned {len(generated)} body RGB / "
                f"{len(generated_masks)} body masks; expected {expected}"
            )
        result: list[Image.Image] = []
        for generated_frame, generated_mask in zip(
            generated,
            generated_masks,
            strict=True,
        ):
            with Image.open(generated_frame) as opened:
                rgb = np.asarray(opened.convert("RGB"), dtype=np.uint8)
            with Image.open(generated_mask) as opened:
                mask = np.asarray(opened.convert("L"), dtype=np.float32)
            alpha = np.clip((mask - 42.0) * (255.0 / 185.0), 0, 255).astype(
                np.uint8
            )
            alpha[alpha < HALO_ALPHA_THRESHOLD] = 0
            frame = selective_matte_defringe(
                Image.fromarray(np.dstack((rgb, alpha)))
            )
            frame.putalpha(
                Image.fromarray(keep_character_component(alpha, 0))
            )
            result.append(frame)
    return result


def _interpolate_foreground_masks(
    rife: Path,
    model: Path,
    masks: dict[int, Image.Image],
    output_count: int,
) -> list[Image.Image]:
    indices = sorted(masks)
    if indices[0] != 0 or indices[-1] != output_count - 1:
        raise RuntimeError("foreground mask timeline must cover both endpoints")
    with tempfile.TemporaryDirectory(prefix="duck-pet-bomb-foreground-") as temporary:
        temporary_path = Path(temporary)
        inputs = temporary_path / "masks"
        interpolated = temporary_path / "interpolated"
        inputs.mkdir()
        interpolated.mkdir()
        for key_index, frame_index in enumerate(indices):
            mask = masks[frame_index].convert("L")
            Image.merge("RGB", (mask, mask, mask)).save(
                inputs / f"{key_index:08d}.png",
                optimize=True,
            )
        _interpolate_rife_timeline(
            rife,
            model,
            inputs,
            interpolated,
            indices,
            temporary_path / "segments",
            "foreground mask",
        )
        generated = sorted(interpolated.glob("*.png"))
        if len(generated) != output_count:
            raise RuntimeError(
                f"RIFE returned {len(generated)} foreground masks; "
                f"expected {output_count}"
            )
        result: list[Image.Image] = []
        for path in generated:
            with Image.open(path) as opened:
                raw = np.asarray(opened.convert("L"), dtype=np.float32)
            alpha = np.clip((raw - 28.0) * (255.0 / 199.0), 0, 255).astype(
                np.uint8
            )
            alpha[alpha < HALO_ALPHA_THRESHOLD] = 0
            result.append(Image.fromarray(alpha, mode="L"))
    for frame_index, mask in masks.items():
        result[frame_index] = mask.copy()
    return result


def _smoke_layer_for_frame(
    smoke: Image.Image,
    frame_index: int,
) -> Image.Image:
    if frame_index <= BOMB_PROP_GONE_FRAME or frame_index >= BOMB_SMOKE_END_FRAME:
        return Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
    if frame_index <= BOMB_SMOKE_PEAK_FRAME:
        progress = (
            (frame_index - BOMB_PROP_GONE_FRAME)
            / (BOMB_SMOKE_PEAK_FRAME - BOMB_PROP_GONE_FRAME)
        )
    else:
        progress = (
            (BOMB_SMOKE_END_FRAME - frame_index)
            / (BOMB_SMOKE_END_FRAME - BOMB_SMOKE_PEAK_FRAME)
        )
    eased = max(0.0, min(1.0, progress))
    eased = eased * eased * (3.0 - (2.0 * eased))
    rgba = np.asarray(smoke.convert("RGBA"), dtype=np.uint8).copy()
    rgba[:, :, 3] = np.round(rgba[:, :, 3].astype(np.float32) * eased).astype(
        np.uint8
    )
    layer = Image.fromarray(rgba)
    drift_x = max(0, round((frame_index - BOMB_SMOKE_PEAK_FRAME) * 0.5))
    return translate_rgba_integer(layer, drift_x, 0) if drift_x else layer


def _repair_layered_body_neutral_fringe(
    frames: list[Image.Image],
    locked_indices: set[int],
) -> tuple[list[Image.Image], dict[str, object]]:
    """Recolor only white RIFE edge pixels supported by a nearer warm outline."""

    result: list[Image.Image] = []
    observations: list[dict[str, object]] = []
    for frame_index, frame in enumerate(frames):
        if frame_index in locked_indices:
            result.append(frame.copy())
            continue
        rgba = np.asarray(frame.convert("RGBA"), dtype=np.uint8).copy()
        rgb = rgba[:, :, :3].astype(np.int16)
        alpha = rgba[:, :, 3]
        visible = alpha >= HALO_ALPHA_THRESHOLD
        outer = visible & _adjacent_to(~visible)
        chroma = rgb.max(axis=2) - rgb.min(axis=2)
        candidate = (
            outer
            & (alpha < 245)
            & (rgb.min(axis=2) >= HALO_NEUTRAL_MINIMUM)
            & (chroma <= HALO_NEUTRAL_CHROMA_MAXIMUM)
        )
        warm_support = _warm_character_mask(frame) & (alpha >= 245)
        neutral_support = (
            (alpha >= 245)
            & (rgb.min(axis=2) >= HALO_NEUTRAL_MINIMUM)
            & (chroma <= HALO_NEUTRAL_CHROMA_MAXIMUM)
        )
        repaired: list[list[int]] = []
        for raw_y, raw_x in np.argwhere(candidate):
            y, x = int(raw_y), int(raw_x)
            best_warm: np.ndarray | None = None
            warm_distance = float("inf")
            neutral_distance = float("inf")
            for radius in range(1, 9):
                y0, y1 = max(0, y - radius), min(alpha.shape[0], y + radius + 1)
                x0, x1 = max(0, x - radius), min(alpha.shape[1], x + radius + 1)
                if best_warm is None:
                    choices = np.argwhere(warm_support[y0:y1, x0:x1])
                    if choices.size:
                        choices[:, 0] += y0
                        choices[:, 1] += x0
                        distance_squared = (
                            (choices[:, 0] - y) ** 2
                            + (choices[:, 1] - x) ** 2
                        )
                        minimum = int(np.min(distance_squared))
                        best_warm = choices[distance_squared == minimum]
                        warm_distance = float(np.sqrt(minimum))
                if neutral_distance == float("inf"):
                    choices = np.argwhere(neutral_support[y0:y1, x0:x1])
                    if choices.size:
                        choices[:, 0] += y0
                        choices[:, 1] += x0
                        distance_squared = (
                            (choices[:, 0] - y) ** 2
                            + (choices[:, 1] - x) ** 2
                        )
                        neutral_distance = float(np.sqrt(np.min(distance_squared)))
                if best_warm is not None and neutral_distance < float("inf"):
                    break
            # A legitimate white-head AA pixel has an opaque white core closer
            # than the orange/brown outline. A white matte tear on a wing/body
            # edge has the opposite topology and is safe to recolor.
            if best_warm is not None and warm_distance + 0.75 < neutral_distance:
                rgba[y, x, :3] = np.mean(
                    rgba[best_warm[:, 0], best_warm[:, 1], :3],
                    axis=0,
                ).astype(np.uint8)
                repaired.append([x, y])
        repaired_frame = Image.fromarray(rgba)
        result.append(repaired_frame)
        remaining = int(
            measure_dark_background_halo(repaired_frame)[
                "neutral_bright_edge_pixels"
            ]
        )
        if repaired or (int(np.sum(candidate)) > 0 and remaining > 0):
            observations.append(
                {
                    "frame": frame_index,
                    "candidate_pixels": int(np.sum(candidate)),
                    "repaired_pixels": repaired,
                    "remaining_neutral_bright_pixels": remaining,
                }
            )
    return result, {
        "enabled": True,
        "alpha_modified": False,
        "method": (
            "nearest opaque warm-outline RGB only when it is closer than any "
            "opaque white-head support"
        ),
        "observations": observations,
        "repaired_pixel_count": sum(
            len(item["repaired_pixels"]) for item in observations
        ),
    }


def _inspect_layered_body_visibility(
    body_frames: list[Image.Image],
    composite_frames: list[Image.Image],
    canonical_neutral: Image.Image,
) -> dict[str, object]:
    target = measure_root_anchor(canonical_neutral)
    y_grid, x_grid = np.indices((CANVAS_SIZE[1], CANVAS_SIZE[0]))
    central = (
        (y_grid >= round(CANVAS_SIZE[1] * 0.60))
        & (y_grid < round(CANVAS_SIZE[1] * 0.84))
        & (np.abs(x_grid - target.torso_x) <= CANVAS_SIZE[0] * 0.24)
    )
    observations: list[dict[str, object]] = []
    errors: list[str] = []
    for frame_index, (body, composite) in enumerate(
        zip(body_frames, composite_frames, strict=True)
    ):
        body_warm = _warm_character_mask(body) & central
        composite_warm = _warm_character_mask(composite) & central
        body_count = int(np.sum(body_warm))
        retained_count = int(np.sum(body_warm & composite_warm))
        retained_fraction = retained_count / body_count if body_count else 0.0
        body_anchor = measure_root_anchor(body)
        composite_alpha = np.asarray(composite.getchannel("A"), dtype=np.uint8)
        foot_region = (
            (np.abs(x_grid - body_anchor.torso_x) <= CANVAS_SIZE[0] * 0.32)
            & (y_grid >= round(CANVAS_SIZE[1] * 0.68))
            & (composite_alpha >= ROOT_ALPHA_THRESHOLD)
        )
        foot_rows = np.flatnonzero(foot_region.sum(axis=1) >= 3)
        composite_foot_y = float(foot_rows[-1]) if foot_rows.size else -1.0
        foot_error = abs(composite_foot_y - body_anchor.foot_contact_y)
        if retained_fraction < 0.20:
            errors.append(
                f"frame-{frame_index:04d} retains only "
                f"{retained_fraction:.1%} of the central body color mask"
            )
        if foot_error > ROOT_Y_TOLERANCE_PIXELS:
            errors.append(
                f"frame-{frame_index:04d} composite foot differs from body "
                f"by {foot_error:.2f}px"
            )
        observations.append(
            {
                "frame": frame_index,
                "central_body_warm_pixels": body_count,
                "retained_central_body_warm_pixels": retained_count,
                "retained_fraction": retained_fraction,
                "body_foot_y": body_anchor.foot_contact_y,
                "composite_foot_y": composite_foot_y,
                "foot_error": foot_error,
            }
        )
    return {
        "passed": not errors,
        "errors": errors,
        "minimum_retained_central_body_fraction": min(
            (float(item["retained_fraction"]) for item in observations),
            default=0.0,
        ),
        "maximum_composite_foot_error": max(
            (float(item["foot_error"]) for item in observations),
            default=float("inf"),
        ),
        "observations": observations,
    }


def _write_layered_debug_frames(
    output_dir: Path,
    body_frames: list[Image.Image],
    composite_frames: list[Image.Image],
    foreground_masks: list[Image.Image],
) -> Path:
    debug_root = output_dir.parent.parent / "AnimationPreviews" / "BombLayerDebug"
    frame_root = debug_root / "Frames"
    body_root = debug_root / "BodyFrames"
    clear_generated_pngs(frame_root, "frame-")
    clear_generated_pngs(body_root, "frame-")
    for frame_index, (body, frame) in enumerate(
        zip(body_frames, composite_frames, strict=True)
    ):
        frame.save(frame_root / f"frame-{frame_index:04d}.png", optimize=True)
        body.save(body_root / f"frame-{frame_index:04d}.png", optimize=True)
    for frame_index in (48, 49, 50, 51, 52, 53, 56, 60, 64, 68, 72, 76, 80, 88):
        body_frames[frame_index].save(
            debug_root / f"body-{frame_index:04d}.png", optimize=True
        )
        foreground_masks[frame_index].save(
            debug_root / f"foreground-mask-{frame_index:04d}.png",
            optimize=True,
        )
        dark = fit_frame_to_display(
            composite_frames[frame_index],
            background=HALO_DARK_BACKGROUNDS["dark_12151c"],
        )
        dark.resize((960, 1044), Image.Resampling.NEAREST).save(
            debug_root / f"dark-6x-{frame_index:04d}.png",
            optimize=True,
        )
    return debug_root


def run_layered_bomb(
    rife: Path,
    model: Path,
    output_dir: Path,
    spec: ClipSpec,
    canonical_neutral: Image.Image,
    bundle: LayeredBombBundle,
) -> tuple[int, dict[str, object]]:
    output_count = round(spec.duration_seconds * 60) + 1
    authored_frame_indices = resolve_authored_frame_indices(spec, output_count)
    locked_indices = set(authored_frame_indices)
    raw_body = _interpolate_layered_body(
        rife,
        model,
        bundle.body_keys,
        authored_frame_indices,
    )
    body_frames, body_stabilization = stabilize_frames(
        raw_body,
        canonical_neutral,
        exact_endpoints=(canonical_neutral, canonical_neutral),
    )
    for frame_index, key in zip(
        authored_frame_indices,
        bundle.body_keys,
        strict=True,
    ):
        body_frames[frame_index] = key.copy()
    body_frames, body_root_correction = correct_final_root_residuals(
        body_frames,
        canonical_neutral,
        locked_indices,
    )
    body_frames, body_matte_cleanup = clean_bomb_interpolated_matte(
        body_frames,
        spec,
        locked_indices,
    )
    body_frames, body_neutral_fringe_cleanup = (
        _repair_layered_body_neutral_fringe(body_frames, locked_indices)
    )

    foreground_controls = {
        frame: mask.copy()
        for frame, mask in bundle.foreground_key_masks.items()
    }
    for frame_index, truth in zip(
        (52, 56),
        bundle.midpoint_references,
        strict=True,
    ):
        prop_center = _interpolate_control_center(
            bundle.prop_control_centers,
            frame_index,
        )
        prop_layer, _ = _place_sprite_at_gray_center(
            bundle.prop_sprite,
            bundle.prop_gray_anchor,
            prop_center,
        )
        foreground_controls[frame_index] = _derive_foreground_mask(
            body_frames[frame_index],
            truth,
            prop_layer,
        )
    foreground_masks = _interpolate_foreground_masks(
        rife,
        model,
        foreground_controls,
        output_count,
    )

    composed: list[Image.Image] = []
    prop_track: list[dict[str, object]] = []
    first_zero_after_release: int | None = None
    reappearing_frames: list[int] = []
    previous_release_center_x: float | None = None
    center_regressions: list[dict[str, object]] = []
    for frame_index, body in enumerate(body_frames):
        center = _interpolate_control_center(
            bundle.prop_control_centers,
            frame_index,
        )
        prop_layer, placement = _place_sprite_at_gray_center(
            bundle.prop_sprite,
            bundle.prop_gray_anchor,
            center,
        )
        foreground_mask_array = np.asarray(
            foreground_masks[frame_index].convert("L"),
            dtype=np.uint8,
        )
        body_alpha = np.asarray(body.getchannel("A"), dtype=np.uint8)
        foreground_masks[frame_index] = Image.fromarray(
            np.minimum(foreground_mask_array, body_alpha),
            mode="L",
        )
        smoke = _smoke_layer_for_frame(bundle.smoke_layer, frame_index)
        frame = _compose_bomb_layers(
            body,
            prop_layer,
            foreground_masks[frame_index],
            smoke,
        )
        if frame_index in locked_indices:
            key_index = authored_frame_indices.index(frame_index)
            frame = bundle.composite_keys[key_index].copy()
        elif frame_index == BOMB_SMOKE_PEAK_FRAME:
            # The authored smoke peak is already covered by locked_indices,
            # but keep this explicit if the timing table changes later.
            frame = _compose_bomb_layers(
                body,
                prop_layer,
                foreground_masks[frame_index],
                bundle.smoke_layer,
            )
        frame = frame if frame_index in locked_indices else add_rgb_edge_bleed(frame)
        composed.append(frame)

        visible = int(placement["visible_alpha_pixels"])
        if frame_index >= BOMB_PROP_RELEASE_FRAME:
            if center is not None:
                center_x = float(center[0])
                if (
                    previous_release_center_x is not None
                    and center_x < previous_release_center_x
                ):
                    center_regressions.append(
                        {
                            "frame": frame_index,
                            "center_x": center_x,
                            "previous_center_x": previous_release_center_x,
                        }
                    )
                previous_release_center_x = center_x
            if visible == 0 and first_zero_after_release is None:
                first_zero_after_release = frame_index
            elif visible > 0 and first_zero_after_release is not None:
                reappearing_frames.append(frame_index)
        prop_track.append({"frame": frame_index, **placement})

    debug_root = _write_layered_debug_frames(
        output_dir,
        body_frames,
        composed,
        foreground_masks,
    )
    body_visibility = _inspect_layered_body_visibility(
        body_frames,
        composed,
        canonical_neutral,
    )
    body_spec = replace(
        spec,
        name="BombBody",
        detached_component_min_area=0,
        allow_detached_edge_contact=False,
        detached_components_right_only=False,
        detached_motion_monotonic_right=False,
        smoke_pose_index=None,
        layered_reference_sheet_name=None,
        layered_midpoint_sheet_name=None,
    )
    body_qa = qa_frame_sequence(
        body_frames,
        output_count,
        canonical_neutral,
        canonical_neutral,
        canonical_neutral,
        component_spec=body_spec,
    )
    qa = qa_frame_sequence(
        composed,
        output_count,
        canonical_neutral,
        canonical_neutral,
        canonical_neutral,
        allow_detached_edge_contact=True,
        component_spec=spec,
        root_reference_frames=body_frames,
    )
    deterministic_errors: list[str] = []
    if center_regressions:
        deterministic_errors.append(
            f"deterministic prop center regressed: {center_regressions}"
        )
    if reappearing_frames:
        deterministic_errors.append(
            f"deterministic prop reappeared after exit: {reappearing_frames}"
        )
    if first_zero_after_release is None:
        deterministic_errors.append("deterministic prop never left the canvas")
    elif any(
        int(item["visible_alpha_pixels"]) > 0
        for item in prop_track[first_zero_after_release + 1 :]
    ):
        deterministic_errors.append(
            "deterministic prop alpha became nonzero after first complete exit"
        )
    if not body_qa["passed"]:
        deterministic_errors.append(
            "body-only layer QA failed: " + "; ".join(body_qa["errors"])
        )
    if not body_visibility["passed"]:
        deterministic_errors.append(
            "layered central-body visibility QA failed: "
            + "; ".join(body_visibility["errors"])
        )
    qa["errors"].extend(deterministic_errors)
    qa["passed"] = not qa["errors"]
    qa["layered_bomb"] = {
        **bundle.report,
        "body_interpolation_stabilization": body_stabilization,
        "body_root_residual_correction": body_root_correction,
        "body_matte_cleanup": body_matte_cleanup,
        "body_neutral_fringe_cleanup": body_neutral_fringe_cleanup,
        "body_qa": body_qa,
        "central_body_visibility": body_visibility,
        "debug_artifacts": str(debug_root),
        "foreground_control_frames": sorted(foreground_controls),
        "foreground_control_pixel_counts": {
            str(frame): int(
                np.sum(np.asarray(mask) >= HALO_ALPHA_THRESHOLD)
            )
            for frame, mask in foreground_controls.items()
        },
        "deterministic_prop": {
            "release_frame": BOMB_PROP_RELEASE_FRAME,
            "first_zero_alpha_frame": first_zero_after_release,
            "gone_frame_limit": BOMB_PROP_GONE_FRAME,
            "center_regressions": center_regressions,
            "reappearing_frames": reappearing_frames,
            "per_frame": prop_track,
            "passed": not center_regressions and not reappearing_frames,
        },
        "composite_order": [
            "body-only RIFE",
            "fixed bomb sprite with integer translation",
            "body-sampled foreground hand/wing mask",
            "separate smoke layer",
        ],
    }
    exact_keys: list[dict[str, object]] = []
    for key_index, (frame_index, key) in enumerate(
        zip(authored_frame_indices, bundle.composite_keys, strict=True)
    ):
        exact = np.array_equal(
            np.asarray(composed[frame_index].convert("RGBA")),
            np.asarray(key.convert("RGBA")),
        )
        exact_keys.append(
            {"key": key_index, "frame": frame_index, "exact": exact}
        )
        if not exact:
            qa["errors"].append(
                f"frame-{frame_index:04d} is not exact layered key-{key_index:02d}"
            )
    qa["authored_key_frames"] = {
        "locked": True,
        "frame_indices": authored_frame_indices,
        "all_exact": all(item["exact"] for item in exact_keys),
        "frames": exact_keys,
    }
    qa["passed"] = not qa["errors"]
    if not qa["passed"]:
        details = "; ".join(str(error) for error in qa["errors"])
        raise RuntimeError(f"Bomb layered asset QA failed: {details}")

    clear_generated_pngs(output_dir, "frame-")
    for frame_index, frame in enumerate(composed):
        frame.save(output_dir / f"frame-{frame_index:04d}.png", optimize=True)
    return output_count, qa


def run_rife(
    rife: Path,
    model: Path,
    key_dir: Path,
    output_dir: Path,
    spec: ClipSpec,
    canonical_neutral: Image.Image,
    *,
    exact_endpoints: tuple[Image.Image, Image.Image] | None = None,
) -> tuple[int, dict[str, object]]:
    if spec.authored_root_calibration and not (spec.lock_authored_frames and spec.segmentwise_interpolation):
        raise ValueError("authored_root_calibration requires locked authored frames and segmentwise interpolation")
    clear_generated_pngs(output_dir, "frame-")
    output_count = round(spec.duration_seconds * 60) + 1
    authored_frame_indices = resolve_authored_frame_indices(spec, output_count)
    locked_frame_indices = (
        set(authored_frame_indices)
        if spec.lock_authored_frames
        else {0, output_count - 1}
    )
    with tempfile.TemporaryDirectory(prefix=f"duck-pet-{spec.name.lower()}-") as temporary:
        temporary_path = Path(temporary)
        matte_keys = temporary_path / "keys"
        mask_keys = temporary_path / "masks"
        interpolated = temporary_path / "interpolated"
        interpolated_masks = temporary_path / "interpolated-masks"
        matte_keys.mkdir()
        mask_keys.mkdir()
        interpolated.mkdir()
        interpolated_masks.mkdir()

        # Motion estimation on alpha caused block streaks at the silhouette.
        # A motionless near-white matte gives RIFE a stable field; the matte
        # is removed from every authored output frame afterwards.
        authored_keys: list[Image.Image] = []
        for index in range(spec.pose_count):
            key = Image.open(key_dir / f"key-{index:02d}.png").convert("RGBA")
            authored_keys.append(key.copy())
            matte = Image.new("RGB", key.size, tuple(int(value) for value in RIFE_MATTE_RGB))
            matte.paste(key, (0, 0), key)
            matte.save(matte_keys / f"{index:08d}.png", optimize=True)
            alpha = key.getchannel("A")
            Image.merge("RGB", (alpha, alpha, alpha)).save(
                mask_keys / f"{index:08d}.png",
                optimize=True,
            )

        def interpolate(
            input_dir: Path,
            result_dir: Path,
            requested_count: int,
        ) -> None:
            command = [
                str(rife),
                "-i",
                str(input_dir),
                "-o",
                str(result_dir),
                "-n",
                str(requested_count),
                "-m",
                str(model),
                "-f",
                "%08d.png",
                "-j",
                "2:2:2",
            ]
            subprocess.run(command, check=True)

        def interpolate_timeline(
            input_dir: Path,
            result_dir: Path,
            workspace_name: str,
        ) -> None:
            if not spec.segmentwise_interpolation:
                interpolate(input_dir, result_dir, output_count)
                return

            workspace = temporary_path / workspace_name
            workspace.mkdir()
            output_index = 0
            for segment_index, (first_frame, last_frame) in enumerate(
                zip(
                    authored_frame_indices,
                    authored_frame_indices[1:],
                )
            ):
                segment_count = last_frame - first_frame + 1
                segment_input = workspace / f"input-{segment_index:02d}"
                segment_output = workspace / f"output-{segment_index:02d}"
                segment_input.mkdir()
                segment_output.mkdir()
                shutil.copyfile(
                    input_dir / f"{segment_index:08d}.png",
                    segment_input / "00000000.png",
                )
                shutil.copyfile(
                    input_dir / f"{segment_index + 1:08d}.png",
                    segment_input / "00000001.png",
                )
                # Match the pair's inclusive t=0..1 interval. RIFE directory
                # mode otherwise spends the latter half repeating key 1.
                directory_count = 2 * (segment_count - 1)
                interpolate(segment_input, segment_output, directory_count)
                segment_frames = sorted(segment_output.glob("*.png"))
                if len(segment_frames) != directory_count:
                    raise RuntimeError(
                        f"RIFE returned {len(segment_frames)} frames for "
                        f"{spec.name} segment {segment_index}; expected "
                        f"{directory_count}"
                    )
                segment_frames = segment_frames[:segment_count]
                # Adjacent segments share an authored endpoint.  Keep the end
                # of the prior segment once so the aggregate remains exactly
                # aligned to authored_frame_indices.
                if segment_index:
                    segment_frames = segment_frames[1:]
                for segment_frame in segment_frames:
                    shutil.copyfile(
                        segment_frame,
                        result_dir / f"{output_index:08d}.png",
                    )
                    output_index += 1
            if output_index != output_count:
                raise RuntimeError(
                    f"Segmented RIFE assembled {output_index} frames for "
                    f"{spec.name}; expected {output_count}"
                )

        interpolate_timeline(matte_keys, interpolated, "rgb-segments")
        interpolate_timeline(mask_keys, interpolated_masks, "mask-segments")

        generated = sorted(interpolated.glob("*.png"))
        generated_masks = sorted(interpolated_masks.glob("*.png"))
        if len(generated) != output_count or len(generated_masks) != output_count:
            raise RuntimeError(
                f"RIFE returned {len(generated)} RGB / {len(generated_masks)} mask "
                f"frames for {spec.name}; expected {output_count}"
            )
        reconstructed: list[Image.Image] = []
        for generated_frame, generated_mask in zip(
            generated,
            generated_masks,
            strict=True,
        ):
            matted_rgb = np.asarray(
                Image.open(generated_frame).convert("RGB"),
                dtype=np.uint8,
            )
            mask = np.asarray(Image.open(generated_mask).convert("L"), dtype=np.float32)
            # Keep RIFE's stable matte render and decontaminate only the alpha
            # boundary below. Algebraic unmatting amplifies small RGB/mask
            # motion-field differences into colored spikes.
            rgb = matted_rgb
            # Tighten RIFE's soft mask to discard color echoes around moving
            # limbs while retaining one antialiased transition pixel.
            alpha = np.clip((mask - 42.0) * (255.0 / 185.0), 0, 255).astype(np.uint8)
            alpha[alpha < 10] = 0
            frame = selective_matte_defringe(
                Image.fromarray(np.dstack((rgb, alpha)))
            )
            cleaned_alpha = keep_character_component(
                np.asarray(frame.getchannel("A"), dtype=np.uint8),
                detached_component_min_area=spec.detached_component_min_area,
            )
            frame.putalpha(Image.fromarray(cleaned_alpha))
            reconstructed.append(frame)

        endpoints = exact_endpoints or (canonical_neutral, canonical_neutral)
        stabilized, stabilization = stabilize_frames(
            reconstructed,
            canonical_neutral,
            exact_endpoints=endpoints,
            authored_frame_indices=tuple(authored_frame_indices) if spec.authored_root_calibration else None,
        )
        stabilized, component_policy = enforce_detached_component_policy(
            stabilized,
            spec,
        )
        if spec.lock_authored_frames:
            for frame_index, key in zip(
                authored_frame_indices,
                authored_keys,
                strict=True,
            ):
                stabilized[frame_index] = key.copy()
        residual_correction = preserve_calibrated_root_residuals if spec.authored_root_calibration else correct_final_root_residuals
        stabilized, final_root_correction = residual_correction(
            stabilized,
            canonical_neutral,
            locked_frame_indices,
        )
        stabilized, interpolated_matte_cleanup = clean_bomb_interpolated_matte(
            stabilized,
            spec,
            locked_frame_indices,
        )
        calibrated_edge_cleanup: dict[str, object] = {"enabled": False}
        if spec.authored_root_calibration:
            stabilized, calibrated_edge_cleanup = repair_calibrated_outer_edge_rgb(
                stabilized, locked_frame_indices,
            )
        component_policy["post_root_residual_policy"] = (
            inspect_detached_component_policy(stabilized, spec)
        )
        for index, frame in enumerate(stabilized):
            # Endpoint identity is part of asset QA and prevents a one-frame
            # pop when the clip enters/exits the canonical neutral pose.
            output = (
                frame
                if index in locked_frame_indices
                else add_rgb_edge_bleed(frame)
            )
            output.save(
                output_dir / f"frame-{index:04d}.png",
                optimize=True,
            )

    frames, _, load_errors = load_frame_sequence(output_dir)
    qa = qa_frame_sequence(
        frames,
        output_count,
        canonical_neutral,
        endpoints[0],
        endpoints[1],
        prior_errors=load_errors,
        allow_detached_edge_contact=spec.allow_detached_edge_contact,
        component_spec=spec,
    )
    qa["stabilization"] = stabilization
    qa["final_root_residual_correction"] = final_root_correction
    qa["interpolated_matte_cleanup"] = interpolated_matte_cleanup
    qa["calibrated_outer_edge_rgb_cleanup"] = calibrated_edge_cleanup
    qa["component_policy_cleanup"] = component_policy
    exact_authored_frames: list[dict[str, object]] = []
    for key_index, (frame_index, key) in enumerate(
        zip(authored_frame_indices, authored_keys, strict=True)
    ):
        exact = np.array_equal(
            np.asarray(frames[frame_index].convert("RGBA")),
            np.asarray(key.convert("RGBA")),
        )
        exact_authored_frames.append(
            {
                "key": key_index,
                "frame": frame_index,
                "exact": exact,
                "premultiplied_mae": _premultiplied_mae(
                    frames[frame_index], key
                ),
            }
        )
        if spec.lock_authored_frames and not exact:
            qa["errors"].append(
                f"frame-{frame_index:04d} is not exact authored key-{key_index:02d}"
            )
    qa["authored_key_frames"] = {
        "locked": spec.lock_authored_frames,
        "frame_indices": authored_frame_indices,
        "frame_intervals": [
            second - first
            for first, second in zip(
                authored_frame_indices,
                authored_frame_indices[1:],
            )
        ],
        "all_exact": all(item["exact"] for item in exact_authored_frames),
        "frames": exact_authored_frames,
    }
    qa["passed"] = not qa["errors"]
    if not qa["passed"]:
        details = "; ".join(str(error) for error in qa["errors"])
        raise RuntimeError(f"{spec.name} asset QA failed: {details}")
    return output_count, qa


def make_contact_sheet(
    output_dir: Path,
    frame_count: int,
    destination: Path,
    canonical_neutral: Image.Image,
) -> None:
    preview_width = 160
    preview_height = round(preview_width * CANVAS_SIZE[1] / CANVAS_SIZE[0])
    columns = 5
    rows = 2
    checker = Image.new("RGB", (columns * preview_width, rows * preview_height), "white")
    draw = ImageDraw.Draw(checker)
    tile = 12
    for y in range(0, checker.height, tile):
        for x in range(0, checker.width, tile):
            if (x // tile + y // tile) % 2:
                draw.rectangle((x, y, x + tile - 1, y + tile - 1), fill=(232, 235, 241))

    canonical_root = measure_root_anchor(canonical_neutral)
    for slot in range(columns * rows):
        index = round(slot * (frame_count - 1) / (columns * rows - 1))
        frame = Image.open(output_dir / f"frame-{index:04d}.png").convert("RGBA")
        frame.thumbnail((preview_width, preview_height), Image.Resampling.LANCZOS)
        x = (slot % columns) * preview_width + (preview_width - frame.width) // 2
        y = (slot // columns) * preview_height + (preview_height - frame.height) // 2
        checker.paste(frame, (x, y), frame)
        root_x = (slot % columns) * preview_width + round(
            canonical_root.torso_x * preview_width / CANVAS_SIZE[0]
        )
        contact_y = (slot // columns) * preview_height + round(
            canonical_root.foot_contact_y * preview_height / CANVAS_SIZE[1]
        )
        # Small cyan registration ticks make body/foot drift visible without
        # covering the character in the contact sheet.
        draw.line((root_x - 4, contact_y, root_x + 4, contact_y), fill=(0, 174, 214), width=1)
        draw.line((root_x, contact_y - 4, root_x, contact_y + 4), fill=(0, 174, 214), width=1)
    destination.parent.mkdir(parents=True, exist_ok=True)
    checker.save(destination, optimize=True)


def fit_frame_to_display(
    frame: Image.Image,
    *,
    background: tuple[int, int, int] | None,
) -> Image.Image:
    """Fit a source canvas into the WPF pet's 160x174 display rectangle."""

    scaled = frame.convert("RGBA")
    scaled.thumbnail(DISPLAY_PREVIEW_SIZE, Image.Resampling.LANCZOS)
    if background is None:
        display = Image.new("RGBA", DISPLAY_PREVIEW_SIZE, (0, 0, 0, 0))
    else:
        display = Image.new("RGB", DISPLAY_PREVIEW_SIZE, background)
    position = (
        (DISPLAY_PREVIEW_SIZE[0] - scaled.width) // 2,
        (DISPLAY_PREVIEW_SIZE[1] - scaled.height) // 2,
    )
    display.paste(scaled, position, scaled)
    return display


def make_key_contact_sheet(
    key_dir: Path,
    key_count: int,
    destination: Path,
) -> None:
    """Render every authored key on the actual-size solid dark background."""

    columns = 5
    rows = (key_count + columns - 1) // columns
    sheet = Image.new(
        "RGB",
        (columns * DISPLAY_PREVIEW_SIZE[0], rows * DISPLAY_PREVIEW_SIZE[1]),
        HALO_DARK_BACKGROUNDS["dark_12151c"],
    )
    draw = ImageDraw.Draw(sheet)
    for index in range(key_count):
        with Image.open(key_dir / f"key-{index:02d}.png") as opened:
            tile = fit_frame_to_display(
                opened.convert("RGBA"),
                background=HALO_DARK_BACKGROUNDS["dark_12151c"],
            )
        x = (index % columns) * DISPLAY_PREVIEW_SIZE[0]
        y = (index // columns) * DISPLAY_PREVIEW_SIZE[1]
        sheet.paste(tile, (x, y))
        draw.text((x + 3, y + 3), f"key {index:02d}", fill=(0, 220, 255))
    destination.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(destination, optimize=True)


def make_dark_background_contact_sheet(
    output_dir: Path,
    frame_count: int,
    destination: Path,
) -> None:
    preview_width, preview_height = DISPLAY_PREVIEW_SIZE
    columns = 5
    rows = 2
    sheet = Image.new(
        "RGB",
        (columns * preview_width, rows * preview_height),
        HALO_DARK_BACKGROUNDS["dark_12151c"],
    )
    for slot in range(columns * rows):
        index = round(slot * (frame_count - 1) / (columns * rows - 1))
        with Image.open(output_dir / f"frame-{index:04d}.png") as opened:
            composite = fit_frame_to_display(
                opened.convert("RGBA"),
                background=HALO_DARK_BACKGROUNDS["dark_12151c"],
            )
        x = (slot % columns) * preview_width
        y = (slot // columns) * preview_height
        sheet.paste(composite, (x, y))
    destination.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(destination, optimize=True)


def make_60fps_gif(
    output_dir: Path,
    frame_count: int,
    destination: Path,
) -> None:
    frames: list[Image.Image] = []
    for index in range(frame_count):
        with Image.open(output_dir / f"frame-{index:04d}.png") as opened:
            frames.append(
                fit_frame_to_display(
                    opened.convert("RGBA"),
                    background=HALO_DARK_BACKGROUNDS["dark_12151c"],
                )
            )
    if not frames:
        raise RuntimeError("cannot create a GIF from an empty sequence")
    # GIF delays are centiseconds. 20/20/10 ms repeats average exactly 60 fps
    # over each three-frame block while retaining every authored 60 Hz frame.
    durations = [20 if index % 3 in (0, 1) else 10 for index in range(frame_count)]
    destination.parent.mkdir(parents=True, exist_ok=True)
    frames[0].save(
        destination,
        save_all=True,
        append_images=frames[1:],
        duration=durations,
        loop=0,
        disposal=2,
        optimize=False,
    )


def make_rhythm_demo(
    assets: Path,
    canonical_neutral: Image.Image,
    destination: Path,
) -> None:
    """Preview two full actions separated by exact two-second idle holds."""

    frames: list[Image.Image] = []
    for clip_name in ("Yawn", "Bomb"):
        output_dir = assets / "Animations" / clip_name
        clip_paths = [output_dir / f"frame-{index:04d}.png" for index in range(121)]
        missing = [path for path in clip_paths if not path.is_file()]
        if missing:
            raise RuntimeError(
                f"cannot create rhythm demo; missing {len(missing)} {clip_name} frames"
            )
        for path in clip_paths:
            with Image.open(path) as opened:
                frames.append(
                    fit_frame_to_display(opened.convert("RGBA"), background=None)
                )
        neutral = fit_frame_to_display(canonical_neutral, background=None)
        frames.extend(neutral.copy() for _ in range(120))

    # GIF delays are integral centiseconds; this 20/20/10 ms cadence averages
    # exactly 60 displayed frames per second without dropping source frames.
    durations = [20 if index % 3 in (0, 1) else 10 for index in range(len(frames))]
    destination.parent.mkdir(parents=True, exist_ok=True)
    frames[0].save(
        destination,
        save_all=True,
        append_images=frames[1:],
        duration=durations,
        loop=0,
        disposal=2,
        optimize=False,
    )
    with Image.open(destination) as encoded:
        encoded_durations: list[int] = []
        transparent_corner_frames = 0
        for frame_index in range(encoded.n_frames):
            encoded.seek(frame_index)
            encoded_durations.append(int(encoded.info.get("duration", 0)))
            if encoded.convert("RGBA").getpixel((0, 0))[3] == 0:
                transparent_corner_frames += 1
        encoded_frame_count = encoded.n_frames
        encoded_size = list(encoded.size)
    write_qa_report(
        destination.with_name("RhythmDemo-qa.json"),
        {
            "passed": (
                encoded_size == list(DISPLAY_PREVIEW_SIZE)
                and sum(encoded_durations) == sum(durations)
                and transparent_corner_frames == encoded_frame_count
            ),
            "logical_tick_count": len(frames),
            "logical_duration_ms": sum(durations),
            "logical_delay_pattern_ms": [20, 20, 10],
            "display_size": list(DISPLAY_PREVIEW_SIZE),
            "transparent_background": True,
            "segments": [
                {"name": "Yawn", "ticks": 121},
                {"name": "neutral", "ticks": 120},
                {"name": "Bomb", "ticks": 121},
                {"name": "neutral", "ticks": 120},
            ],
            "encoded_gif": {
                "frame_count": encoded_frame_count,
                "duration_ms": sum(encoded_durations),
                "transparent_corner_frames": transparent_corner_frames,
                "identical_ticks_are_duration_coalesced": (
                    encoded_frame_count < len(frames)
                ),
            },
        },
    )


def print_qa_summary(name: str, report: dict[str, object]) -> None:
    root = report.get("root", {})
    boundary = report.get("boundary", {})
    halo = report.get("dark_background_halo", {})
    status = "PASS" if report.get("passed") else "FAIL"
    print(
        f"{name} QA {status}: frames={report.get('frame_count')}, "
        f"root-x-error={root.get('maximum_torso_x_error')}, "
        f"foot-y-error={root.get('maximum_foot_contact_y_error')}, "
        f"margins={boundary.get('minimum_margins_left_top_right_bottom')}, "
        f"halo-pixels={halo.get('worst_frame_measurement', {}).get('maximum_visible_pixels')}"
    )
    for error in report.get("errors", []):
        print(f"  - {error}")


def load_canonical_neutral(assets: Path) -> Image.Image:
    path = assets / "mascot-animated-neutral.png"
    if path.is_file():
        with Image.open(path) as opened:
            neutral = opened.convert("RGBA")
        if neutral.size != CANVAS_SIZE:
            raise RuntimeError(
                f"canonical neutral is {neutral.size}, expected {CANVAS_SIZE}"
            )
        return neutral

    yawn = next(spec for spec in CLIPS if spec.name == "Yawn")
    source = assets / "AnimationSources" / yawn.sheet_name
    cells = normalize_cells(
        extract_cells(
            source,
            yawn.columns,
            yawn.rows,
            yawn.pose_count,
            yawn.detached_component_min_area,
        )
    )
    return add_rgb_edge_bleed(cells[0])


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--rife", type=Path)
    parser.add_argument("--rife-model", type=Path)
    parser.add_argument(
        "--clip",
        action="append",
        choices=[spec.name for spec in CLIPS],
        help="Build/QA only this clip; may be repeated. Defaults to all clips.",
    )
    parser.add_argument(
        "--qa-only",
        action="store_true",
        help="Measure existing frames without RIFE or asset modification.",
    )
    parser.add_argument(
        "--keys-only",
        action="store_true",
        help="Extract, normalize, and QA authored keys without running RIFE.",
    )
    parser.add_argument(
        "--rhythm-demo",
        action="store_true",
        help="After successful QA/build, refresh RhythmDemo-60fps.gif.",
    )
    args = parser.parse_args()
    assets = args.assets.resolve()
    if not assets.is_dir():
        raise SystemExit(f"Assets directory not found: {assets}")

    selected_names = set(args.clip or (spec.name for spec in CLIPS))
    selected_specs = [spec for spec in CLIPS if spec.name in selected_names]
    canonical_neutral = load_canonical_neutral(assets)
    preview_root = assets / "AnimationPreviews"

    if args.qa_only:
        if args.keys_only:
            raise SystemExit("--qa-only and --keys-only cannot be used together")
        failed = False
        for spec in selected_specs:
            output_dir = assets / "Animations" / spec.name
            frames, _, load_errors = load_frame_sequence(output_dir)
            root_reference_frames: list[Image.Image] | None = None
            if spec.uses_layered_bomb_pipeline:
                body_dir = preview_root / "BombLayerDebug" / "BodyFrames"
                root_reference_frames, _, body_errors = load_frame_sequence(
                    body_dir
                )
                load_errors.extend(
                    f"layered root reference: {error}" for error in body_errors
                )
                if len(root_reference_frames) != len(frames):
                    load_errors.append(
                        "layered root reference frame count "
                        f"{len(root_reference_frames)} != {len(frames)}"
                    )
            report = qa_frame_sequence(
                frames,
                round(spec.duration_seconds * 60) + 1,
                canonical_neutral,
                canonical_neutral,
                canonical_neutral,
                prior_errors=load_errors,
                allow_detached_edge_contact=spec.allow_detached_edge_contact,
                component_spec=spec,
                root_reference_frames=root_reference_frames,
            )
            print_qa_summary(spec.name, report)
            failed = failed or not bool(report["passed"])
        if failed:
            raise SystemExit(1)
        if args.rhythm_demo:
            rhythm_path = preview_root / "RhythmDemo-60fps.gif"
            make_rhythm_demo(assets, canonical_neutral, rhythm_path)
            print(f"RhythmDemo refreshed -> {rhythm_path}")
        return

    if not args.keys_only and (args.rife is None or args.rife_model is None):
        raise SystemExit(
            "--rife and --rife-model are required unless --qa-only or "
            "--keys-only is used"
        )
    rife = args.rife.resolve() if args.rife is not None else None
    rife_model = args.rife_model.resolve() if args.rife_model is not None else None
    if rife is not None and not rife.is_file():
        raise SystemExit(f"RIFE executable not found: {rife}")
    if rife_model is not None and not rife_model.is_dir():
        raise SystemExit(f"RIFE model directory not found: {rife_model}")
    needed_names = set(selected_names)

    extracted: dict[str, list[Image.Image]] = {}
    derived_pose_reports: dict[str, dict[str, object]] = {}
    source_matte_cleanup: dict[str, dict[str, object]] = {}
    key_directories: dict[str, Path] = {}
    key_preview_paths: dict[str, Path] = {}
    key_stabilization: dict[str, dict[str, object]] = {}
    layered_bundles: dict[str, LayeredBombBundle] = {}
    if needed_names:
        needed_names.add("Yawn")
        for spec in CLIPS:
            if spec.name not in needed_names:
                continue
            source = assets / "AnimationSources" / spec.sheet_name
            if not source.is_file():
                raise SystemExit(f"Animation source sheet not found: {source}")
            source_matte_cleanup[spec.name] = {
                "primary": inspect_source_matte_cleanup(source),
            }
            if spec.uses_layered_bomb_pipeline:
                reference_source = (
                    assets
                    / "AnimationSources"
                    / str(spec.layered_reference_sheet_name)
                )
                midpoint_source = (
                    assets
                    / "AnimationSources"
                    / str(spec.layered_midpoint_sheet_name)
                )
                for label, path in (
                    ("layered_reference", reference_source),
                    ("layered_midpoints", midpoint_source),
                ):
                    if not path.is_file():
                        raise SystemExit(
                            f"Layered Bomb {label} source sheet not found: {path}"
                        )
                    source_matte_cleanup[spec.name][label] = (
                        inspect_source_matte_cleanup(path)
                    )
            ordered = apply_authored_order(
                normalize_cells(
                    extract_cells(
                        source,
                        spec.columns,
                        spec.rows,
                        spec.source_count,
                        spec.detached_component_min_area,
                    )
                ),
                spec,
            )
            derived, release_report = derive_authored_cells(ordered, spec)
            supplemental: list[Image.Image] = []
            supplemental_report: dict[str, object] = {"enabled": False}
            if spec.supplemental_pose_count:
                if (
                    spec.supplemental_sheet_name is None
                    or spec.supplemental_insert_after_index is None
                    or spec.supplemental_columns <= 0
                    or spec.supplemental_rows <= 0
                ):
                    raise RuntimeError(
                        f"{spec.name}: supplemental pose configuration is incomplete"
                    )
                supplemental_source = (
                    assets / "AnimationSources" / spec.supplemental_sheet_name
                )
                if not supplemental_source.is_file():
                    raise SystemExit(
                        "Supplemental animation source sheet not found: "
                        f"{supplemental_source}"
                    )
                source_matte_cleanup[spec.name]["supplemental"] = (
                    inspect_source_matte_cleanup(supplemental_source)
                )
                after = spec.supplemental_insert_after_index
                neighbor_heights = [
                    alpha_bbox(ordered[index])[3] - alpha_bbox(ordered[index])[1]
                    for index in (after, after + 1)
                ]
                target_height = round(float(np.mean(neighbor_heights)))
                supplemental = normalize_cells(
                    extract_cells(
                        supplemental_source,
                        spec.supplemental_columns,
                        spec.supplemental_rows,
                        spec.supplemental_pose_count,
                        spec.detached_component_min_area,
                    ),
                    target_height=target_height,
                )
                derived, supplemental_report = insert_supplemental_cells(
                    derived,
                    supplemental,
                    spec,
                )
                supplemental_report["adjacent_source_key_heights"] = neighbor_heights
                supplemental_report["normalized_target_height"] = target_height
                supplemental_report["normalized_pose_heights"] = [
                    alpha_bbox(image)[3] - alpha_bbox(image)[1]
                    for image in supplemental
                ]
                if bool(release_report.get("enabled")):
                    release_index = int(release_report["inserted_key"])
                    release_report["final_inserted_key"] = release_index + (
                        spec.supplemental_pose_count
                        if after < release_index
                        else 0
                    )
            extracted[spec.name] = derived
            derived_pose_reports[spec.name] = {
                "release_midpoint": release_report,
                "supplemental_poses": supplemental_report,
            }
            if len(extracted[spec.name]) != spec.pose_count:
                raise RuntimeError(
                    f"{spec.name}: prepared {len(extracted[spec.name])} authored "
                    f"poses; expected {spec.pose_count}"
                )

        canonical_neutral = add_rgb_edge_bleed(extracted["Yawn"][0])
        canonical_neutral.save(assets / "mascot-animated-neutral.png", optimize=True)

        for spec in CLIPS:
            if spec.name not in needed_names:
                continue
            if spec.uses_layered_bomb_pipeline:
                key_dir, stabilization, bundle = prepare_layered_bomb_bundle(
                    assets,
                    spec,
                    extracted[spec.name],
                    canonical_neutral,
                )
                layered_bundles[spec.name] = bundle
            else:
                key_dir, stabilization = write_keys(
                    assets,
                    spec,
                    extracted[spec.name],
                    canonical_neutral,
                )
            key_directories[spec.name] = key_dir
            stabilization["derived_pose"] = derived_pose_reports[spec.name]
            key_stabilization[spec.name] = stabilization
            key_preview = preview_root / f"{spec.name}-keys-dark.png"
            make_key_contact_sheet(key_dir, spec.pose_count, key_preview)
            key_preview_paths[spec.name] = key_preview

    if args.keys_only:
        for spec in selected_specs:
            report = key_stabilization[spec.name]["authored_key_preflight"]
            print_qa_summary(f"{spec.name} authored keys", report)
            print(
                f"{spec.name}: {spec.pose_count} authored keys -> "
                f"{key_directories[spec.name]}"
            )
            print(f"{spec.name}: dark key contact -> {key_preview_paths[spec.name]}")
        return

    for spec in selected_specs:
        if rife is None or rife_model is None:
            raise RuntimeError("RIFE paths unexpectedly unavailable during build")
        output_dir = assets / "Animations" / spec.name
        if spec.uses_layered_bomb_pipeline:
            frame_count, qa = run_layered_bomb(
                rife,
                rife_model,
                output_dir,
                spec,
                canonical_neutral,
                layered_bundles[spec.name],
            )
        else:
            frame_count, qa = run_rife(
                rife,
                rife_model,
                key_directories[spec.name],
                output_dir,
                spec,
                canonical_neutral,
            )
        qa["key_stabilization"] = key_stabilization[spec.name]
        qa["source_matte_cleanup"] = source_matte_cleanup[spec.name]
        make_contact_sheet(
            output_dir,
            frame_count,
            preview_root / f"{spec.name}.png",
            canonical_neutral,
        )
        make_dark_background_contact_sheet(
            output_dir,
            frame_count,
            preview_root / f"{spec.name}-dark.png",
        )
        make_60fps_gif(
            output_dir,
            frame_count,
            preview_root / f"{spec.name}-60fps.gif",
        )
        qa["preview_artifacts"] = {
            "contact_sheet": str(preview_root / f"{spec.name}.png"),
            "dark_background_contact_sheet": str(
                preview_root / f"{spec.name}-dark.png"
            ),
            "average_60fps_gif": str(preview_root / f"{spec.name}-60fps.gif"),
            "authored_keys_dark_contact_sheet": str(key_preview_paths[spec.name]),
            "gif_delay_pattern_ms": [20, 20, 10],
            "display_size": list(DISPLAY_PREVIEW_SIZE),
            "dark_background_rgb": list(HALO_DARK_BACKGROUNDS["dark_12151c"]),
        }
        write_qa_report(preview_root / f"{spec.name}-qa.json", qa)
        print_qa_summary(spec.name, qa)
        print(f"{spec.name}: {frame_count} frames at 60 fps -> {output_dir}")

    if args.rhythm_demo or selected_names == {spec.name for spec in CLIPS}:
        rhythm_path = preview_root / "RhythmDemo-60fps.gif"
        make_rhythm_demo(assets, canonical_neutral, rhythm_path)
        print(
            "RhythmDemo: Yawn 121 + idle 120 + Bomb 121 + idle 120 frames "
            f"-> {rhythm_path}"
        )

if __name__ == "__main__":
    main()
