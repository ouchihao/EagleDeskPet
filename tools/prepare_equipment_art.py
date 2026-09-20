"""Rebuild EquipmentV2 from original RGBA art; crop/register only, never redraw.

Pillow + NumPy + SciPy are build-only dependencies. SciPy may live in the existing
.tool-cache/work-animation-python directory. Default output is ignored staging;
--apply imports only the explicitly listed production equipment PNGs.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
import uuid

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "DuckDeskPet" / "Assets"
SOURCES = ASSETS / "AnimationSources" / "EquipmentV2"
SOURCE_IMPORTS = {
    "desk-noodle.png": "exec-e29f85b0-2fc2-4103-b974-d3c40b12399f.png",
    "desk-cloud.png": "exec-32c2a2a9-d19c-4c1b-a08c-4393ec939912.png",
    "desk-boardroom.png": "exec-fe06f6da-980b-41b8-8721-8bfeeeb2429a.png",
    "computer-server.png": "exec-b5db911f-8e7c-4bd4-8925-3d7f6b233247.png",
    "computer-gold.png": "exec-d1f110ff-da4a-4404-ba29-cacb97f8b5ff.png",
    "computer-ultrabook.png": "exec-860fdae1-1f95-457b-80d5-c5539c37655d.png",
    "ox-atlas.png": "exec-bbccaace-f2d2-40ec-b59e-b56dc508859a.png",
    "hero-atlas.png": "exec-c7425943-14e2-4399-bdf3-054e94adcbe7.png",
    "astronaut-atlas.png": "exec-b4a91c92-2ce3-4b79-b784-977b276a1e7d.png",
    "glasses.png": "exec-d80b1a75-4c7b-4b26-8b1a-823cefd35b84.png",
}
COMPONENT_NAMES = ("torso", "left-sleeve", "right-sleeve", "head", "back", "left-boot", "right-boot")
CANVAS = (384, 346)
DESK_SPLIT_Y = 276


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def scipy_ndimage():
    try:
        from scipy import ndimage
    except ImportError:
        optional = ROOT / ".tool-cache" / "work-animation-python"
        if (optional / "scipy").is_dir():
            sys.path.insert(0, str(optional))
        try:
            from scipy import ndimage
        except ImportError as error:
            raise RuntimeError("Install build-only Pillow, NumPy and SciPy, or use the existing local tool cache.") from error
    return ndimage


def principal_components(image: Image.Image, expected: int):
    """Use alpha alone. The seven large islands cross atlas grid boundaries safely.

    AI output may contain hundreds of isolated, nearly transparent matte specks.
    Record exactly what is excluded; reject an ambiguous or substantial discard.
    Connected internal detail, holes and semitransparency are never color-keyed.
    """
    pixels = np.asarray(image.convert("RGBA"))
    alpha = pixels[:, :, 3]
    labels, count = scipy_ndimage().label(alpha > 0)
    areas = np.bincount(labels.ravel())
    ranked = sorted(range(1, count + 1), key=lambda index: int(areas[index]), reverse=True)
    if len(ranked) < expected:
        raise ValueError(f"Expected {expected} substantial alpha components, got {len(ranked)}")
    keep = ranked[:expected]
    if int(areas[keep[-1]]) < 500 or (len(ranked) > expected and areas[ranked[expected]] > areas[keep[-1]] * .08):
        raise ValueError("Component identities are ambiguous; inspect the original art instead of silently cutting it.")
    kept = np.isin(labels, keep)
    alpha_mass = int(alpha.astype(np.uint64).sum())
    discarded_mass = int(alpha[~kept].astype(np.uint64).sum())
    if alpha_mass == 0 or discarded_mass / alpha_mass > .02:
        raise ValueError("Discarded disconnected alpha is significant; manual source review required.")
    objects = scipy_ndimage().find_objects(labels)
    components = []
    for identifier in keep:
        ys, xs = objects[identifier - 1]
        bounds = (xs.start, ys.start, xs.stop, ys.stop)
        cropped = pixels[ys, xs].copy()
        cropped[labels[ys, xs] != identifier] = 0
        components.append({"image": Image.fromarray(cropped), "bounds": bounds,
                           "pixel_area": int(areas[identifier]),
                           "center": ((xs.start + xs.stop) / 2, (ys.start + ys.stop) / 2)})
    qa = {"source_canvas": list(image.size), "alpha_bounds": list(image.getchannel("A").getbbox() or ()),
          "alpha_min": int(alpha.min()), "alpha_max": int(alpha.max()),
          "zero_alpha_fraction": float((alpha == 0).mean()),
          "partial_alpha_fraction": float(((alpha > 0) & (alpha < 255)).mean()),
          "raw_component_count": int(count), "kept_component_count": expected,
          "discarded_pixel_count": int(((alpha > 0) & ~kept).sum()),
          "discarded_alpha_mass_fraction": discarded_mass / alpha_mass,
          "segmentation": "4-connected alpha>0; principal islands, no RGB key or grid clipping"}
    return components, qa


def atlas_parts(image: Image.Image):
    parts, qa = principal_components(image, 7)
    top = sorted((p for p in parts if p["center"][1] < image.height / 2), key=lambda p: p["center"][0])
    bottom = sorted((p for p in parts if p["center"][1] >= image.height / 2), key=lambda p: p["center"][0])
    if len(top) != 3 or len(bottom) != 4:
        raise ValueError("Atlas must have three upper pieces and head/back/two boots below.")
    ordered = top + bottom
    qa["components"] = {name: {k: v for k, v in part.items() if k != "image"} for name, part in zip(COMPONENT_NAMES, ordered, strict=True)}
    return {name: part["image"] for name, part in zip(COMPONENT_NAMES, ordered, strict=True)}, qa


def resized(image: Image.Image, size: tuple[int, int]):
    # Explicit premultiplication prevents hidden RGB from bleeding into edges.
    return image.convert("RGBa").resize(size, Image.Resampling.LANCZOS).convert("RGBA")


def small_piece(image: Image.Image):
    scale = min(1, 512 / max(image.size))
    size = tuple(max(1, round(value * scale)) for value in image.size)
    return resized(image, size) if size != image.size else image.copy()


def registered(image: Image.Image, bounds: tuple[int, int, int, int]):
    left, top, right, bottom = bounds
    result = Image.new("RGBA", CANVAS)
    result.alpha_composite(resized(image, (right - left, bottom - top)), (left, top))
    return result


def desk_layers(full: Image.Image):
    back, front = np.asarray(full).copy(), np.asarray(full).copy()
    back[DESK_SPLIT_Y:, :, :] = 0
    front[:DESK_SPLIT_Y, :, :] = 0
    return Image.fromarray(back), Image.fromarray(front)


def contact(images: dict[str, Image.Image], destination: Path):
    columns, cell_w, cell_h = 4, 330, 214
    rows = (len(images) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * cell_w, rows * cell_h * 2 + 40), "#111923")
    draw = ImageDraw.Draw(sheet)
    draw.text((10, 12), "EquipmentV2 / original-alpha mechanical crops / DARK + LIGHT", fill="#dfbd78")
    for index, (name, original) in enumerate(images.items()):
        col, row = index % columns, index // columns
        visual = original.copy()
        visual.thumbnail((cell_w - 20, cell_h - 35), Image.Resampling.LANCZOS)
        for light in (0, 1):
            x, y = col * cell_w, 40 + row * cell_h * 2 + light * cell_h
            draw.rectangle((x, y, x + cell_w, y + cell_h), fill="#eee6d7" if light else "#111923")
            sheet.paste(visual, (x + (cell_w - visual.width) // 2, y + 8), visual)
            draw.text((x + 8, y + cell_h - 20), name, fill="#514932" if light else "#acd6ca")
    sheet.save(destination)


def build(output: Path):
    reference_back = ASSETS / "SceneProps/Shop/desk-arcade-back.png"
    reference_front = ASSETS / "SceneProps/Shop/desk-arcade-front.png"
    reference_computer = ASSETS / "SceneProps/Shop/computer-arcade.png"
    with Image.open(reference_back) as b, Image.open(reference_front) as f, Image.open(reference_computer) as c:
        b, f, c = b.convert("RGBA"), f.convert("RGBA"), c.convert("RGBA")
        if any(image.size != CANVAS for image in (b, f, c)):
            raise ValueError("Reference registration canvas changed; review target anchors.")
        desk_bounds = Image.alpha_composite(b, f).getchannel("A").getbbox()
        computer_bounds = c.getchannel("A").getbbox()
    if desk_bounds != (39, 249, 345, 345) or computer_bounds != (141, 215, 243, 275):
        raise ValueError("Reference bounds changed; explicit layout review is required.")
    sources_qa, artifacts, previews = {}, {}, {}
    for filename in SOURCE_IMPORTS:
        path = SOURCES / filename
        with Image.open(path) as source:
            if source.mode != "RGBA": raise ValueError(f"Source is not native RGBA: {filename}")
            image = source.copy()
        if filename.endswith("-atlas.png"):
            pieces, qa = atlas_parts(image)
            outfit = filename.removesuffix("-atlas.png").title()
            for name, piece in pieces.items():
                result = small_piece(piece)
                artifacts[f"Outfits/{outfit}/Layers/{name}.png"] = result
                previews[f"{outfit}/{name}"] = result
        else:
            pieces, qa = principal_components(image, 1)
            piece = pieces[0]["image"]
            qa["components"] = {"principal": {k: v for k, v in pieces[0].items() if k != "image"}}
            if filename.startswith("desk-"):
                full = registered(piece, desk_bounds)
                back, front = desk_layers(full)
                if not np.array_equal(np.asarray(Image.alpha_composite(back, front)), np.asarray(full)):
                    raise ValueError("Desk split must reconstruct the full registered table exactly.")
                stem = Path(filename).stem
                artifacts[f"SceneProps/Shop/{stem}-back.png"] = back
                artifacts[f"SceneProps/Shop/{stem}-front.png"] = front
                previews[stem] = full
                qa["registration"] = {"canvas": CANVAS, "bounds": desk_bounds, "split_y": DESK_SPLIT_Y,
                                      "scaling": "independent x/y fit to existing desk union; back above split, front below"}
            elif filename.startswith("computer-"):
                result = registered(piece, computer_bounds)
                artifacts[f"SceneProps/Shop/{filename}"] = result
                previews[Path(filename).stem] = result
                qa["registration"] = {"canvas": CANVAS, "bounds": computer_bounds, "bottom_anchor": [192, 275]}
            else:
                result = small_piece(piece)
                artifacts["Outfits/Office/Layers/glasses.png"] = result
                previews["Office/glasses"] = result
        qa["source_sha256"] = digest(path)
        qa["source_file"] = "Assets/AnimationSources/EquipmentV2/" + filename
        sources_qa[filename] = qa
    expected = 3 * 7 + 1 + 3 * 2 + 3
    if len(artifacts) != expected: raise ValueError("Incomplete equipment artifact set.")
    qa_dir = output / "QA"
    qa_dir.mkdir(parents=True, exist_ok=True)
    outputs_qa = {}
    for name, image in artifacts.items():
        destination = output / "Assets" / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        image.save(destination)
        outputs_qa[name] = {"sha256": digest(destination), "canvas": list(image.size), "alpha_bounds": image.getchannel("A").getbbox()}
    contact(previews, qa_dir / "equipment-dark-light.png")
    contact({k: v for k, v in previews.items() if k.startswith(("desk-", "computer-"))}, qa_dir / "props-dark-light.png")
    for outfit in ("Ox", "Hero", "Astronaut", "Office"):
        contact({k: v for k, v in previews.items() if k.startswith(outfit + "/")}, qa_dir / f"{outfit.lower()}-dark-light.png")
    return {"passed": True, "method": "alpha-component isolation + premultiplied resampling + explicit registration; no generated/redrawn pixels",
            "sources": sources_qa, "outputs": outputs_qa,
            "references": {p.relative_to(ROOT).as_posix(): digest(p) for p in (reference_back, reference_front, reference_computer)}}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--import-from", type=Path, help="Copy the ten original generated PNGs without modifying or deleting them")
    parser.add_argument("--output-root", type=Path, default=Path(".codex-build/equipment-v2-build"))
    parser.add_argument("--apply", action="store_true", help="After validating the entire build, atomically replace only these 31 production PNGs")
    args = parser.parse_args(argv)
    output = (ROOT / args.output_root).resolve()
    if not output.is_relative_to(ROOT / ".codex-build") or output == ROOT / ".codex-build":
        parser.error("--output-root must name a dedicated directory beneath .codex-build")
    print(f"Exact staging root: {output}", flush=True)
    if args.import_from is not None:
        incoming = args.import_from.resolve(strict=True)
        validated = []
        for filename, original in SOURCE_IMPORTS.items():
            source = incoming / original
            with Image.open(source) as image:
                if image.mode != "RGBA": raise ValueError(f"Expected original RGBA source: {source}")
                image.verify()
            destination = SOURCES / filename
            if destination.exists() and digest(source) != digest(destination):
                raise ValueError(f"Refusing to replace a different existing original: {destination}")
            validated.append((source, destination))
        print(f"Exact original-source copy destination: {SOURCES.resolve()}", flush=True)
        SOURCES.mkdir(parents=True, exist_ok=True)
        for source, destination in validated:
            if not destination.exists(): shutil.copy2(source, destination)
    report = build(output)
    if args.apply:
        print(f"Exact production destination root: {ASSETS.resolve()}", flush=True)
        # Validate every file before the first production write, including source
        # provenance; keep originals and all intermediate QA outside production.
        for filename, qa in report["sources"].items():
            if digest(SOURCES / filename) != qa["source_sha256"]: raise ValueError("Original changed during build.")
        for name, qa in report["outputs"].items():
            target = (ASSETS / name).resolve()
            if not target.is_relative_to(ASSETS.resolve()): raise ValueError("Unexpected production destination.")
            if digest(output / "Assets" / name) != qa["sha256"]: raise ValueError("Staged output changed during build.")
            print(target, flush=True)
        for name in report["outputs"]:
            destination = ASSETS / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            temporary = destination.with_name(destination.name + ".equipment-tmp-" + uuid.uuid4().hex)
            try:
                shutil.copyfile(output / "Assets" / name, temporary)
                temporary.replace(destination)
            finally:
                if temporary.exists(): temporary.unlink()
        report["applied"] = True
    (output / "QA" / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"Equipment QA: {output / 'QA' / 'report.json'}", flush=True)


if __name__ == "__main__":
    main()
