"""Build the shared, articulated outfit UV atlas from the EXISTING authored animation.

This is mechanical segmentation/binding, not artwork generation. RGBA artwork is supplied by
ImageGen separately. No source sprite is rewritten. Maps encode R=region, G=U, B=V, A=coverage;
regions 1..5 are torso, sleeves and boots; 6/7 are foreground hands/held items. Head/back affine
anchors follow the measured pose. Source SHA-256 locks every map to its exact authored frame.

The atlas is staged until contact sheets have been reviewed. --promote is deliberately absent.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
import sys
import zipfile

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tool-cache' / 'work-animation-python'))
try:
    from scipy import ndimage as ndi
except ImportError as exc:
    raise SystemExit('Outfit bindings require the existing .tool-cache/work-animation-python scipy runtime.') from exc

WIDTH, HEIGHT = 384, 346
YY, XX = np.indices((HEIGHT, WIDTH))
PALETTE = np.array([[0, 0, 0], [46, 147, 224], [190, 73, 204], [74, 198, 174],
                    [216, 102, 70], [250, 174, 54], [251, 145, 190], [254, 218, 141]], np.uint8)


def components(mask: np.ndarray, minimum: int = 30):
    labels, _ = ndi.label(mask)
    counts = np.bincount(labels.ravel())
    result = []
    for label in np.argsort(counts[1:])[::-1] + 1:
        if counts[label] < minimum:
            break
        ys, xs = np.nonzero(labels == label)
        result.append((int(label), int(counts[label]), float(xs.mean()), float(ys.mean()),
                       (int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1)))
    return labels, result


def nearest(mask: np.ndarray, x: float, y: float) -> tuple[int, int]:
    ys, xs = np.nonzero(mask)
    if not len(xs):
        raise ValueError('No anatomical pixels near a required joint.')
    index = np.argmin((xs - x) ** 2 + (ys - y) ** 2)
    return int(ys[index]), int(xs[index])


def affine(cx: float, cy: float, width: float, height: float, angle: float) -> list[float]:
    c, s = np.cos(angle), np.sin(angle)
    return [round(cx - width * c / 2 + height * s / 2, 4), round(cy - width * s / 2 - height * c / 2, 4),
            round(width * c, 4), round(width * s, 4), round(-height * s, 4), round(height * c, 4)]


def measure(image: Image.Image) -> dict:
    rgba = np.asarray(image.convert('RGBA')).astype(np.int16)
    if rgba.shape != (HEIGHT, WIDTH, 4):
        raise ValueError('Every source must be a native 384x346 RGBA frame.')
    r, g, b, a = rgba.transpose(2, 0, 1)
    # White cranial feathers: restrict to the head band before selecting the main component,
    # so bowls, mugs, white shirt badges and scene props cannot become head anchors.
    white = (a > 160) & (r > 215) & (g > 201) & (b > 171) & (YY > 50) & (YY < 246)
    head_labels, candidates = components(white, 400)
    if not candidates:
        raise ValueError('Cannot identify the authored white head.')
    head_label, _, cx, cy, bbox = candidates[0]
    x0, y0, x1, y1 = bbox
    # Two eyebrows are a stable roll cue even during closed-eye/yawn expressions. Extreme,
    # ambiguous expressions fall back to the cranial axis instead of jumping to a prop.
    grey = (a > 180) & (np.maximum.reduce((r, g, b)) - np.minimum.reduce((r, g, b)) < 24) & \
        (r > 80) & (r < 220) & (XX > x0 + 8) & (XX < x1 - 8) & (YY > y0 + 25) & (YY < y0 + (y1 - y0) * .64)
    angles = []
    eyes = []
    for side in (XX < cx - 9, XX > cx + 9):
        _, brow_parts = components(grey & side, 20)
        brow_parts = [part for part in brow_parts if part[4][2] - part[4][0] >= 7 and part[4][3] - part[4][1] <= 24]
        brow = min(brow_parts, key=lambda part: part[3]) if brow_parts else None
        eyes.append((brow[2], brow[3]) if brow else None)
    if all(eyes):
        angle = float(np.arctan2(eyes[1][1] - eyes[0][1], eyes[1][0] - eyes[0][0]))
        if abs(angle) < .36:
            angles.append(angle)
    # The cranial outline is independent of wink/angry eyebrow expressions. Fit its upper
    # centreline and reject rows occluded by a raised hand; otherwise glasses would twitch
    # when an eyebrow disappears for a single interpolation frame.
    rows, centres = [], []
    for y in range(int(y0 + (y1 - y0) * .20), int(y0 + (y1 - y0) * .54)):
        xs = np.flatnonzero(head_labels[y] == head_label)
        if len(xs) > (x1 - x0) * .58:
            rows.append(y); centres.append((xs[0] + xs[-1]) / 2)
    if angles:
        angle = angles[0]
    elif len(rows) >= 12:
        rows, centres = np.asarray(rows), np.asarray(centres)
        fit = np.polyfit(rows, centres, 1)
        reliable = np.abs(centres - np.polyval(fit, rows)) < 2.5
        if np.count_nonzero(reliable) >= 12:
            fit = np.polyfit(rows[reliable], centres[reliable], 1)
        angle = float(np.clip(-np.arctan(fit[0]), -.35, .35))
    else:
        angle = angles[0] if angles else 0.0
    eye_centres = []
    dark = (a > 140) & (np.maximum.reduce((r, g, b)) < 112)
    for side, brow in zip((-1, 1), eyes):
        bx, by = brow if brow is not None else (cx + side * (x1 - x0) * .225, y0 + (y1 - y0) * .31)
        eye_area = dark & (np.abs(XX - bx) < 22) & (YY > by + 6) & (YY < by + 34)
        _, eye_parts = components(eye_area, 6)
        eye_parts = [part for part in eye_parts if part[4][2] - part[4][0] < 36 and part[4][3] - part[4][1] < 30]
        eye = min(eye_parts, key=lambda part: abs(part[2] - bx) + abs(part[3] - by - 18)) if eye_parts else None
        eye_centres.append((eye[2], eye[3]) if eye else (bx, by + 18))
    (elx, ely), (erx, ery) = eye_centres
    eye_angle = float(np.arctan2(ery - ely, erx - elx))
    eye_width = float(np.clip(np.hypot(erx - elx, ery - ely) / .49, 94, 157))
    eye_spec = [(elx + erx) / 2, (ely + ery) / 2, eye_width, eye_width * 173 / 512, eye_angle]
    # The outline is outside the feather component. Root/feet use yellow pixels only in the
    # planted-foot band, never the beak (which can move during mouth expressions).
    feet = (a > 160) & (r > 220) & (g > 115) & (g < 239) & (b < 95) & (YY > 286)
    fy, fx = np.nonzero(feet)
    foot_y = float(np.quantile(fy, .96)) if len(fy) > 70 else 333.0
    body_cx = float(np.mean(fx)) if len(fx) > 70 else cx
    body_top = float(y0 + (y1 - y0) * .91)
    body_bottom = min(foot_y - 10, 318.0)
    body_width = float(np.clip((x1 - x0) * .89, 107, 151))
    return dict(head=[cx, (y0 + y1) / 2, float(x1 - x0 + 8), float(y1 - y0 + 9), angle], eyes=eye_spec,
                body=[body_cx, (body_top + body_bottom) / 2, body_width, max(56., body_bottom - body_top), angle * .48],
                body_top=body_top, body_bottom=body_bottom, foot_y=foot_y, feet=feet,
                head_mask=head_labels == head_label)


def bind(image: Image.Image, measured: dict | None = None) -> tuple[Image.Image, dict, dict]:
    rgba = np.asarray(image.convert('RGBA')).astype(np.int16)
    r, g, b, a = rgba.transpose(2, 0, 1)
    m = measured or measure(image)
    cx, cy, bw, bh, angle = m['body']
    body_top, body_bottom = cy - bh / 2, cy + bh / 2
    # Palette selects only candidate anatomical material. Spatial gates, closed ink contours,
    # seeded watershed and separate hand labels decide membership; no global colour replace.
    brown = (a > 0) & (r - g > 35) & (g - b > 13) & (r > 102) & (g < 184) & (b < 149) & \
            (YY > body_top - 89) & (YY < body_bottom + 10) & (np.abs(XX - cx) < bw * .77)
    # Head outline is not clothing. Allow raised wings only when their actual brown area has
    # substantial thickness; a narrow golden skull contour cannot seed a sleeve.
    interior = brown & (r > 174) & (g > 99) & (g < 169) & (b > 34) & (r - g > 43)
    labels, pieces = components(interior, 300)
    anchors = []
    for side in (-1, 1):
        possible = [p for p in pieces if side * (p[2] - cx) > bw * .20 and p[3] < body_bottom - 12 and 400 <= p[1] < 3600
                    and 25 <= p[4][2] - p[4][0] < 78 and 28 <= p[4][3] - p[4][1] < 104
                    and p[1] / ((p[4][2] - p[4][0]) * (p[4][3] - p[4][1])) > .38]
        if possible:
            selected = min(possible, key=lambda p: abs(p[2] - (cx + side * bw * .39)) + .18 * abs(p[3] - (body_top + bh * .34)))
            anchors.append((selected[2], selected[3]))
        else:
            anchors.append((cx + side * bw * .41, body_top + bh * .36))
    markers = np.zeros((HEIGHT, WIDTH), np.int32)
    markers[~brown] = 4
    torso_seed = nearest(brown & (YY > body_top + bh * .4) & (np.abs(XX - cx) < 22), cx, body_top + bh * .70)
    markers[torso_seed] = 1
    for label, (ax, ay) in enumerate(anchors, 2):
        seed = nearest(interior & ((XX < cx - 12) if label == 2 else (XX > cx + 12)), ax, ay)
        markers[seed] = label
    gradient = np.clip(255 - g.astype(float) * 1.4, 0, 255).astype(np.uint8)
    gradient[~brown] = 255
    partition = ndi.watershed_ift(gradient, markers)
    atlas = np.zeros((HEIGHT, WIDTH, 4), np.uint8)
    # A single softened ink pixel can join the hand and torso in an interpolated frame.
    # Constrain the semantic watershed by the anatomical hand envelope: it may refine the
    # contour, but must never flood a whole belly into a "hand" (or eat both hands).
    wing_masks = []
    for region, (ax, ay) in enumerate(anchors, 2):
        ellipse = ((XX - ax) / 29) ** 2 + ((YY - ay) / 39) ** 2
        wing_masks.append(brown & (ellipse < 1.25) & ((partition == region) | (ellipse < .72)))
    torso = brown & (YY >= body_top - 4) & (YY <= body_bottom + 4) & ~wing_masks[0] & ~wing_masks[1]
    c, s = np.cos(angle), np.sin(angle)
    torso_u = ((XX - cx) * c + (YY - cy) * s) / bw + .5
    torso_v = (-(XX - cx) * s + (YY - cy) * c) / bh + .5
    set_region(atlas, torso, 1, torso_u, torso_v)
    hand_pixels = 0
    for region, (ax, ay) in enumerate(anchors, 2):
        side = -1 if region == 2 else 1
        shoulder = np.array([cx + side * bw * .39 * c - (-bh / 2 + 7) * s,
                             cy + side * bw * .39 * s + (-bh / 2 + 7) * c])
        direction = np.array([ax, ay]) - shoulder
        norm = float(np.linalg.norm(direction))
        if norm < 12:
            direction = np.array([side * 3., 20.]); norm = float(np.linalg.norm(direction))
        axis = direction / norm
        wing = wing_masks[region - 2]
        # Exclude one-pixel skull-outline leakage, but preserve the existing hand alpha.
        thick = ndi.binary_opening(wing, structure=np.ones((3, 3)))
        wing &= ndi.binary_dilation(thick, iterations=2)
        projection = (XX - shoulder[0]) * axis[0] + (YY - shoulder[1]) * axis[1]
        cross = -(XX - shoulder[0]) * axis[1] + (YY - shoulder[1]) * axis[0]
        extent = float(np.quantile(projection[wing], .97)) if np.any(wing) else norm
        cutoff = max(13., (extent + 9) * .58 - 9)
        sleeve = wing & (projection >= -9) & (projection < cutoff)
        # Wing tips are explicitly retained as hands, so raised fingers never acquire a
        # floating rectangular sleeve or become hidden behind the glasses/helmet.
        set_region(atlas, sleeve, region, cross / 43 + .5, (projection + 9) / (cutoff + 9))
        hand = wing & ~sleeve
        set_region(atlas, hand, region + 4, np.zeros_like(XX), np.zeros_like(YY))
        hand_pixels += int(np.count_nonzero(hand))
    # Existing bowls/mugs and cream props are not clothing. A held object crossing the face
    # must also occlude headwear. Only non-head connected bright regions in the hand band qualify.
    props = (a > 0) & ~m['head_mask'] & (r > 220) & (g > 195) & (b > 123) & \
            (YY > body_top - 40) & (YY < body_bottom) & (np.abs(XX - cx) < 63)
    props = ndi.binary_opening(props, structure=np.ones((3, 3)))
    set_region(atlas, props, 7, np.zeros_like(XX), np.zeros_like(YY))
    for region, side in ((4, -1), (5, 1)):
        foot = m['feet'] & ((XX < cx) if side == -1 else (XX >= cx))
        fy, fx = np.nonzero(foot)
        if len(fx) < 10:
            continue
        x0, x1, y0, y1 = fx.min(), fx.max() + 1, fy.min(), fy.max() + 1
        # Boots share the authored foot silhouette/root; they cannot move the ground contact.
        foot = (a > 0) & (XX >= x0) & (XX < x1) & (YY >= max(y0 - 7, body_bottom - 5)) & (YY < y1)
        set_region(atlas, foot, region, (XX - x0) / max(1, x1 - x0 - 1), (YY - (y0 - 7)) / max(1, y1 - y0 + 6))
    atlas[a == 0] = 0
    transform = dict(head=affine(*m['head']), body=affine(*m['body']), eyes=affine(*m['eyes']))
    report = dict(torsoPixels=int(np.count_nonzero(atlas[:, :, 0] == 1)), handPixels=hand_pixels,
                  labelledPixels=int(np.count_nonzero(atlas[:, :, 0])), handAnchors=anchors,
                  head=transform['head'], body=transform['body'])
    return Image.fromarray(atlas, 'RGBA'), transform, report


def set_region(atlas, mask, region, u, v):
    atlas[mask, 0] = region
    atlas[mask, 1] = np.round(np.clip(u[mask], 0, 1) * 255).astype(np.uint8)
    atlas[mask, 2] = np.round(np.clip(v[mask], 0, 1) * 255).astype(np.uint8)
    atlas[mask, 3] = 255


def frame_paths(manifest):
    yield 'neutral', manifest['neutral']
    for action in manifest['actions']:
        for i in range(action['frameCount']):
            yield f"{action['clip']}/{i:04d}", f"{action['directory']}/frame-{i:04d}.png"


def smooth_anchors(values: np.ndarray) -> np.ndarray:
    """Remove detector quantisation, retain exact authored endpoints and the real pose motion."""
    if len(values) < 4:
        return values.copy()
    smooth = ndi.gaussian_filter1d(ndi.median_filter(values, size=(5, 1), mode='nearest'), 1.25, axis=0, mode='nearest')
    t = np.linspace(0, 1, len(values))[:, None]
    smooth += (values[0] - smooth[0]) * (1 - t) ** 3 + (values[-1] - smooth[-1]) * t ** 3
    smooth[0], smooth[-1] = values[0], values[-1]
    return smooth


def preview(source, atlas, transforms):
    result = source.convert('RGBA').copy()
    arr = np.asarray(atlas)
    colour = PALETTE[arr[:, :, 0]].copy()
    colour = np.dstack((colour, np.where(arr[:, :, 0] > 0, 150, 0).astype(np.uint8)))
    result = Image.alpha_composite(result, Image.fromarray(colour, 'RGBA'))
    draw = ImageDraw.Draw(result)
    for name, color in [('head', '#64d3ff'), ('body', '#ff68d7')]:
        t = transforms[name]
        points = [(t[0] + u * t[2] + v * t[4], t[1] + u * t[3] + v * t[5]) for u, v in [(0, 0), (1, 0), (1, 1), (0, 1), (0, 0)]]
        draw.line(points, fill=color, width=2)
    return result


def build(assets: Path, outfit: str, output: Path, clips: set[str] | None = None, samples_only=False):
    manifest_path = 'Assets/actions.json' if outfit == 'default' else f'Assets/Outfits/{outfit}/actions.json'
    project = assets.parent
    data = json.loads((project / manifest_path).read_text(encoding='utf-8-sig'))
    output.mkdir(parents=True, exist_ok=True)
    archive_path = output / (('Default' if outfit == 'default' else outfit) + '-v1.zip')
    manifest = dict(version=1, width=WIDTH, height=HEIGHT, baseManifest=manifest_path, frames={})
    reports, samples = {}, []
    cache = {}
    paths = list(frame_paths(data))
    if clips:
        paths = [(key, path) for key, path in paths if key == 'neutral' or key.split('/')[0] in clips]
    if samples_only:
        paths = [(key, path) for key, path in paths if key == 'neutral' or int(key.split('/')[1]) % 30 == 0]
    tracks = {}
    if not samples_only:
        groups = {}
        for key, source_path in paths:
            m = measure(Image.open(project / source_path))
            groups.setdefault(key.split('/')[0], []).append((key, m['head'] + m['body'] + m['eyes']))
        for poses in groups.values():
            smoothed = smooth_anchors(np.asarray([value for _, value in poses]))
            tracks.update((key, value) for (key, _), value in zip(poses, smoothed))
    # A bounded cache makes every identical canonical seam pixel-identical in its clothing binding.
    with zipfile.ZipFile(archive_path, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for index, (key, source_path) in enumerate(paths):
            source_bytes = (project / source_path).read_bytes()
            digest = hashlib.sha256(source_bytes).hexdigest()
            source = Image.open(io.BytesIO(source_bytes)).convert('RGBA')
            if digest in cache:
                encoded, transforms, report = cache[digest]
                atlas = Image.open(io.BytesIO(encoded)).convert('RGBA')
            else:
                measured = measure(source)
                if key in tracks:
                    measured['head'] = tracks[key][:5]
                    measured['body'] = tracks[key][5:10]
                    measured['eyes'] = tracks[key][10:]
                atlas, transforms, report = bind(source, measured)
                buf = io.BytesIO(); atlas.save(buf, format='PNG', optimize=False)
                encoded = buf.getvalue()
                if key == 'neutral' or key.endswith('/0000'):
                    cache[digest] = encoded, transforms, report
            map_path = f'maps/{key}.png'
            archive.writestr(map_path, encoded)
            manifest['frames'][key] = dict(source=source_path, sha256=digest, map=map_path, **transforms)
            reports[key] = report
            if key == 'neutral' or (key.split('/')[1] in {'0000', '0030', '0060', '0090', '0120', '0144', '0168', '0210'}):
                samples.append((key, preview(source, atlas, transforms)))
            if index % 200 == 0:
                print(f'{outfit}: {index}/{len(paths)} anatomical bindings', flush=True)
        archive.writestr('bindings.json', json.dumps(manifest, separators=(',', ':')))
    for start in range(0, len(samples), 20):
        page = Image.new('RGB', (5 * 260, 4 * 257), '#1e2330')
        draw = ImageDraw.Draw(page)
        for offset, (name, pic) in enumerate(samples[start:start + 20]):
            x, y = offset % 5 * 260, offset // 5 * 257
            reduced = pic.resize((256, 231), Image.Resampling.LANCZOS)
            page.paste(reduced, (x + 2, y + 23), reduced)
            draw.text((x + 7, y + 4), name, fill='white')
        page.save(output / f'{outfit}-binding-contact-{start // 20 + 1:02d}.png')
    (output / f'{outfit}-binding-report.json').write_text(json.dumps(dict(archive=str(archive_path), frames=len(paths),
        archiveBytes=archive_path.stat().st_size, schema='RGBA R=region/G=U/B=V/A=coverage; independent anatomical segmentation; source hashes locked',
        boundary='Automatic anatomical bindings require visual acceptance; never a claim of hand-drawn frame art.', measurements=reports), indent=2), encoding='utf-8')
    print(f'{archive_path}: {len(paths)} frames, {archive_path.stat().st_size / 1024 / 1024:.2f} MiB', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assets', type=Path, default=ROOT / 'DuckDeskPet' / 'Assets')
    parser.add_argument('--outfits', nargs='+', default=['default', 'Office'])
    parser.add_argument('--output', type=Path, default=ROOT / '.codex-build' / 'outfit-bindings')
    parser.add_argument('--clips', nargs='+', help='Diagnostic subset only; deliberately fails full-suite runtime validation.')
    parser.add_argument('--samples-only', action='store_true', help='Inspect every 30th pose before the complete binding build.')
    parser.add_argument('--retarget-eyes', type=Path, help='Existing complete atlas: update only eye transforms, preserving every PNG member byte-for-byte.')
    args = parser.parse_args()
    assets, output = args.assets.resolve(), args.output.resolve()
    if output == assets or assets in output.parents:
        raise SystemExit('Bindings must be staged outside production Assets until visually reviewed.')
    print(f'Source: {assets}\nStaging: {output}', flush=True)
    if args.retarget_eyes:
        retarget_eyes(assets, args.retarget_eyes.resolve(), output)
        return
    for outfit in args.outfits:
        if outfit not in ('default', 'Office'):
            raise SystemExit('Only existing default and Office anatomical source packs are supported.')
        build(assets, outfit, output, set(args.clips) if args.clips else None, args.samples_only)


def retarget_eyes(assets: Path, source_archive: Path, output: Path):
    output.mkdir(parents=True, exist_ok=True)
    target = output / source_archive.name
    if target == source_archive:
        raise ValueError('Eye retargeting must stage a distinct archive; source evidence is preserved.')
    with zipfile.ZipFile(source_archive) as original:
        manifest = json.loads(original.read('bindings.json'))
        groups = {}
        for index, (key, binding) in enumerate(manifest['frames'].items()):
            data = (assets.parent / binding['source']).read_bytes()
            if hashlib.sha256(data).hexdigest() != binding['sha256']:
                raise ValueError('Eye retarget source changed: ' + key)
            m = measure(Image.open(io.BytesIO(data)))
            groups.setdefault(key.split('/')[0], []).append((key, m['eyes']))
            if index % 500 == 0:
                print(f'Eye anchors: {index}/{len(manifest["frames"])}', flush=True)
        identical = {}
        for poses in groups.values():
            values = smooth_anchors(np.asarray([value for _, value in poses]))
            for (key, _), value in zip(poses, values):
                frame = manifest['frames'][key]
                frame['eyes'] = identical.setdefault(frame['sha256'], affine(*value))
        with zipfile.ZipFile(target, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as updated:
            for entry in original.infolist():
                if entry.filename != 'bindings.json':
                    updated.writestr(entry.filename, original.read(entry.filename))
            updated.writestr('bindings.json', json.dumps(manifest, separators=(',', ':')))
        with zipfile.ZipFile(target) as check:
            if any(original.read(entry.filename) != check.read(entry.filename) for entry in original.infolist() if entry.filename.endswith('.png')):
                raise ValueError('A UV map changed during eye-only retargeting.')
    print(f'{target}: updated eye anchors; all {len(manifest["frames"])} PNG maps are byte-identical.', flush=True)


if __name__ == '__main__':
    main()
