import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { inflateRawSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';
import { readPsd, initializeCanvas } from 'ag-psd';

initializeCanvas(() => { throw new Error('Raw pixels only'); },
  (width, height) => ({ width, height, data: new Uint8ClampedArray(width * height * 4) }));
export const WIDTH = 384, HEIGHT = 346;
const root = path.resolve(import.meta.dirname, '../..');
const sourceRoot = 'DuckDeskPet/Assets/Live2DSource/FullActions/Source';
const variants = ['default', 'office', 'hoodie', 'ox', 'hero', 'astronaut'];
const authorCandidateVariants = ['default', 'office', 'hoodie'];
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

export function readZip(bytes) {
  let end = bytes.length - 22;
  while (end >= Math.max(0, bytes.length - 65557) && bytes.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0) throw new Error('Invalid anatomical archive');
  let cursor = bytes.readUInt32LE(end + 16);
  const entries = new Map();
  for (let i = 0; i < bytes.readUInt16LE(end + 10); i++) {
    if (bytes.readUInt32LE(cursor) !== 0x02014b50) throw new Error('Invalid ZIP record');
    const length = bytes.readUInt16LE(cursor + 28), extra = bytes.readUInt16LE(cursor + 30), comment = bytes.readUInt16LE(cursor + 32);
    const name = bytes.toString('utf8', cursor + 46, cursor + 46 + length);
    const local = bytes.readUInt32LE(cursor + 42), size = bytes.readUInt32LE(cursor + 20), expanded = bytes.readUInt32LE(cursor + 24);
    if (expanded > 8 * 1024 * 1024 || entries.has(name)) throw new Error('Invalid bounded archive entry');
    const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28);
    const method = bytes.readUInt16LE(cursor + 10), compressed = bytes.subarray(start, start + size);
    const value = method === 0 ? compressed : method === 8 ? inflateRawSync(compressed) : null;
    if (!value || value.length !== expanded) throw new Error('Invalid archive compression');
    entries.set(name, value);
    cursor += 46 + length + extra + comment;
  }
  return entries;
}

export function over(pixels, p, color) {
  const a = color[3] / 255, behind = pixels[p + 3] / 255, alpha = a + behind * (1 - a);
  if (!a) return;
  for (let c = 0; c < 3; c++) pixels[p + c] = Math.round((color[c] * a + pixels[p + c] * behind * (1 - a)) / alpha);
  pixels[p + 3] = Math.round(alpha * 255);
}

export function flatten(layers) {
  const pixels = Buffer.alloc(WIDTH * HEIGHT * 4);
  for (const layer of layers) {
    if (layer.hidden || layer.opacity === 0) continue;
    const { width, height, data } = layer.imageData;
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const xx = x + layer.left, yy = y + layer.top;
      if (xx >= 0 && xx < WIDTH && yy >= 0 && yy < HEIGHT)
        over(pixels, (yy * WIDTH + xx) * 4, data.subarray((y * width + x) * 4, (y * width + x + 1) * 4));
    }
  }
  return pixels;
}

function solve(matrix, vector) {
  const a = matrix.map((row, i) => [...row, vector[i]]);
  for (let col = 0; col < 3; col++) {
    let pivot = col;
    for (let row = col + 1; row < 3; row++) if (Math.abs(a[row][col]) > Math.abs(a[pivot][col])) pivot = row;
    [a[col], a[pivot]] = [a[pivot], a[col]];
    if (Math.abs(a[col][col]) < 1e-8) throw new Error('Degenerate material UV field');
    const divisor = a[col][col];
    for (let j = col; j <= 3; j++) a[col][j] /= divisor;
    for (let row = 0; row < 3; row++) if (row !== col) {
      const weight = a[row][col];
      for (let j = col; j <= 3; j++) a[row][j] -= weight * a[col][j];
    }
  }
  return a.map(row => row[3]);
}

export function fitUv(map, region) {
  const normal = Array.from({ length: 3 }, () => [0, 0, 0]), u = [0, 0, 0], v = [0, 0, 0];
  let count = 0;
  for (let y = 0; y < HEIGHT; y++) for (let x = 0; x < WIDTH; x++) {
    const p = (y * WIDTH + x) * 4;
    if (map[p] !== region || !map[p + 3] || map[p + 1] < 2 || map[p + 1] > 253 || map[p + 2] < 2 || map[p + 2] > 253) continue;
    const row = [x, y, 1]; count++;
    for (let i = 0; i < 3; i++) {
      u[i] += row[i] * map[p + 1]; v[i] += row[i] * map[p + 2];
      for (let j = 0; j < 3; j++) normal[i][j] += row[i] * row[j];
    }
  }
  if (count < 10) throw new Error('Missing articulated UV region ' + region);
  return [solve(normal, u), solve(normal, v)];
}

export function correspondence(source, target, x, y) {
  const u = target[0][0] * x + target[0][1] * y + target[0][2] - source[0][2];
  const v = target[1][0] * x + target[1][1] * y + target[1][2] - source[1][2];
  const det = source[0][0] * source[1][1] - source[0][1] * source[1][0];
  const sx = (u * source[1][1] - v * source[0][1]) / det;
  const sy = (v * source[0][0] - u * source[1][0]) / det;
  return [Math.abs(sx - Math.round(sx)) < 1e-8 ? Math.round(sx) : sx,
    Math.abs(sy - Math.round(sy)) < 1e-8 ? Math.round(sy) : sy];
}

export function affinePoint(from, to, x, y) {
  const det = from[2] * from[5] - from[3] * from[4], dx = x - from[0], dy = y - from[1];
  const u = (dx * from[5] - dy * from[4]) / det, v = (dy * from[2] - dx * from[3]) / det;
  return [to[0] + u * to[2] + v * to[4], to[1] + u * to[3] + v * to[5]];
}

export async function hoodieSleeveCorrection(zip, bindings) {
  const anchors = [
    [45, '9965cf158c1fdd304afc3a161ae4ca2b38232f8f70a39a13c6c2b956a21ce506', '101c3e08d80c75920c35bea28eb4514227304d58f9d3937f29370177382a3f86'],
    [52, '70abd6e9a729946a6ea6944cbd5dba7c75a73871c5e91e7e9c2292efba9a8203', 'ca996e4a95c604438fa6a2fa891b9fdb77f262e8b7003ef4b9436df751b6c725'],
  ];
  const fitted = [];
  for (const [frame, expectedSource, expectedMap] of anchors) {
    const record = bindings.frames['Yawn/' + String(frame).padStart(4, '0')], bytes = zip.get(record.map);
    if (record.sha256.toLowerCase() !== expectedSource || sha(bytes) !== expectedMap)
      throw new Error('Reviewed sleeve correction anchors changed; re-author the UV track');
    fitted.push(fitUv(await sharp(bytes).ensureAlpha().raw().toBuffer(), 3));
  }
  const frames = new Map(), hashes = [];
  for (let frame = 46; frame <= 51; frame++) {
    const record = bindings.frames['Yawn/' + String(frame).padStart(4, '0')], bytes = zip.get(record.map);
    const raw = fitUv(await sharp(bytes).ensureAlpha().raw().toBuffer(), 3), t = (frame - 45) / 7;
    const authored = fitted[0].map((row, i) => row.map((value, j) => value + (fitted[1][i][j] - value) * t));
    if (raw[1][0] * authored[1][0] + raw[1][1] * authored[1][1] >= 0)
      throw new Error('Known reversed sleeve branch no longer matches; review this source instead of applying a stale correction');
    frames.set(frame, authored);
    hashes.push({ frame, sourceSha256: record.sha256, mapSha256: sha(bytes) });
  }
  return { frames, report: { outfit: 'outfit.hoodie', clip: 'Yawn', region: 3, frames: [46, 51], anchors,
    reason: 'Automatic hand detector switched to the opposite sleeve axis despite a stationary wing; raw entry reversal 171.2197 degrees and exit -169.5614 degrees',
    method: 'Spatial UV affine interpolation between reviewed source frames 45 and 52; no pixel recoloring, frame replacement, or opacity transition',
    sourceHashes: hashes } };
}

export function authoredSleeveFits(name, clip, frame, correction) {
  return name === 'hoodie' && clip === 'Yawn' && correction.frames.has(frame) ? new Map([[3, correction.frames.get(frame)]]) : undefined;
}

async function scanSleeveAxes(zip, bindings, correction) {
  const steps = [];
  for (const clip of ['Yawn', 'Eat']) {
    let previous;
    for (let frame = 0; frame <= 120; frame++) {
      const record = bindings.frames[clip + '/' + String(frame).padStart(4, '0')];
      const map = await sharp(zip.get(record.map)).ensureAlpha().raw().toBuffer();
      const fits = [2, 3].map(region => fitUv(map, region));
      const corrected = fits.map((fit, i) => authoredSleeveFits('hoodie', clip, frame, correction)?.get(i + 2) ?? fit);
      if (previous) for (let i = 0; i < 2; i++) {
        const angle = (a, b) => {
          let degrees = (Math.atan2(a[1][1], a[1][0]) - Math.atan2(b[1][1], b[1][0])) * 180 / Math.PI;
          while (degrees > 180) degrees -= 360;
          while (degrees < -180) degrees += 360;
          return degrees;
        };
        const originalDegrees = angle(fits[i], previous.raw[i]), correctedDegrees = angle(corrected[i], previous.corrected[i]);
        if (Math.abs(originalDegrees) > 30 || Math.abs(correctedDegrees) > 30)
          steps.push({ clip, frame, region: i + 2, originalDegrees, correctedDegrees });
      }
      previous = { raw: fits, corrected };
    }
  }
  return { clips: ['Yawn', 'Eat'], regions: [2, 3], framesPerClip: 121, reportingThresholdDegrees: 30,
    largeSteps: steps, boundary: 'A large detector-axis step is evidence to review against the original limb pose, not automatically a defect' };
}

function sample(data, width, height, x, y, premultiply = false) {
  if (Math.abs(x - Math.round(x)) < 1e-8) x = Math.round(x);
  if (Math.abs(y - Math.round(y)) < 1e-8) y = Math.round(y);
  const xx = Math.floor(x), yy = Math.floor(y), fx = x - xx, fy = y - yy, result = [0, 0, 0, 0];
  for (const [dx, dy, weight] of [[0, 0, (1 - fx) * (1 - fy)], [1, 0, fx * (1 - fy)], [0, 1, (1 - fx) * fy], [1, 1, fx * fy]]) {
    if (xx + dx < 0 || xx + dx >= width || yy + dy < 0 || yy + dy >= height || !weight) continue;
    const p = ((yy + dy) * width + xx + dx) * 4, a = premultiply ? data[p + 3] / 255 : 1;
    for (let c = 0; c < 3; c++) result[c] += data[p + c] * a * weight;
    result[3] += data[p + 3] * weight;
  }
  if (premultiply && result[3]) for (let c = 0; c < 3; c++) result[c] *= 255 / result[3];
  return result;
}

function paintAccessory(output, layer, neutral, binding) {
  const part = layer.name === 'outfit-back' ? 'body' : 'head';
  const from = binding[part], to = neutral[part], image = layer.imageData;
  for (let y = 0; y < HEIGHT; y++) for (let x = 0; x < WIDTH; x++) {
    const [sx, sy] = affinePoint(from, to, x, y);
    const color = sample(image.data, image.width, image.height, sx - layer.left, sy - layer.top, true);
    if (color[3]) over(output, (y * WIDTH + x) * 4, color);
  }
}

export function materialRegion(map, anatomy, p, x, y, middle) {
  const label = map[p];
  if (label >= 1 && label <= 5) return label;
  const brown = anatomy[p] - anatomy[p + 1] > 30 && anatomy[p + 1] - anatomy[p + 2] > 9;
  if (!anatomy[p + 3]) return 0;
  if (label === 6 || label === 7) return brown ? x < middle ? 2 : 3 : 0;
  if (!brown && y < 240) return 0;
  // Extend material correspondence through the thin authored ink rim, not through face/held bowls
  for (let radius = 1; radius <= 8; radius++) for (let dy = -radius; dy <= radius; dy++) for (let dx = -radius; dx <= radius; dx++) {
    if (x + dx < 0 || x + dx >= WIDTH || y + dy < 0 || y + dy >= HEIGHT) continue;
    const neighbor = map[((y + dy) * WIDTH + x + dx) * 4];
    if (neighbor >= 1 && neighbor <= 5) return neighbor;
  }
  return brown && y >= 240 && y < 300 ? x < middle - 28 ? 2 : x > middle + 28 ? 3 : 1 : 0;
}

function whiteHead(anatomy) {
  const seen = new Uint8Array(WIDTH * HEIGHT);
  let biggest = [];
  for (let start = WIDTH * 51; start < WIDTH * 246; start++) {
    const p = start * 4;
    if (seen[start] || anatomy[p + 3] <= 160 || anatomy[p] <= 215 || anatomy[p + 1] <= 201 || anatomy[p + 2] <= 171) continue;
    const component = [start]; seen[start] = 1;
    for (let i = 0; i < component.length; i++) {
      const at = component[i], x = at % WIDTH, y = Math.floor(at / WIDTH);
      for (const next of [x > 0 ? at - 1 : -1, x + 1 < WIDTH ? at + 1 : -1, y > 51 ? at - WIDTH : -1, y < 245 ? at + WIDTH : -1]) {
        const q = next * 4;
        if (next < 0 || seen[next] || anatomy[q + 3] <= 160 || anatomy[q] <= 215 || anatomy[q + 1] <= 201 || anatomy[q + 2] <= 171) continue;
        seen[next] = 1; component.push(next);
      }
    }
    if (component.length > biggest.length) biggest = component;
  }
  const result = new Uint8Array(seen.length);
  for (const p of biggest) result[p] = 1;
  return result;
}

function paintRecipe(anatomy, map, recipe) {
  const result = Buffer.from(anatomy);
  for (let p = 0; p < result.length; p += 4) {
    const layer = recipe?.get(map[p]);
    if (!layer || !map[p + 3] || !anatomy[p + 3]) continue;
    const u = map[p + 1] / 255, v = map[p + 2] / 255, q = layer.uvQuad ?? [0, 0, 1, 0, 0, 1, 1, 1];
    const tx = ((1 - u) * q[0] + u * q[2]) * (1 - v) + ((1 - u) * q[4] + u * q[6]) * v;
    const ty = ((1 - u) * q[1] + u * q[3]) * (1 - v) + ((1 - u) * q[5] + u * q[7]) * v;
    const color = sample(layer.data, layer.width, layer.height, tx * (layer.width - 1), ty * (layer.height - 1), true);
    const coverage = color[3] / 255 * map[p + 3] / 255;
    for (let c = 0; c < 3; c++) result[p + c] = Math.round(anatomy[p + c] * (1 - coverage) + color[c] * coverage);
  }
  return result;
}

function convexMask(points) {
  const result = new Uint8Array(WIDTH * HEIGHT);
  if (points.length < 3) return result;
  points.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  const cross = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const lower = [], upper = [];
  for (const p of points) { while (lower.length >= 2 && cross(lower.at(-2), lower.at(-1), p) <= 0) lower.pop(); lower.push(p); }
  for (const p of [...points].reverse()) { while (upper.length >= 2 && cross(upper.at(-2), upper.at(-1), p) <= 0) upper.pop(); upper.push(p); }
  const hull = [...lower.slice(0, -1), ...upper.slice(0, -1)];
  for (let y = Math.max(0, Math.ceil(Math.min(...hull.map(p => p[1])))); y <= Math.min(HEIGHT - 1, Math.floor(Math.max(...hull.map(p => p[1])))); y++) {
    const xs = [];
    for (let i = 0; i < hull.length; i++) {
      const a = hull[i], b = hull[(i + 1) % hull.length];
      if (y >= Math.min(a[1], b[1]) && y <= Math.max(a[1], b[1])) {
        if (a[1] === b[1]) xs.push(a[0], b[0]);
        else xs.push(a[0] + (y - a[1]) * (b[0] - a[0]) / (b[1] - a[1]));
      }
    }
    if (xs.length) for (let x = Math.max(0, Math.ceil(Math.min(...xs))); x <= Math.min(WIDTH - 1, Math.floor(Math.max(...xs))); x++) result[y * WIDTH + x] = 1;
  }
  return result;
}

function heldProps(anatomy, map, head) {
  const result = new Uint8Array(WIDTH * HEIGHT);
  const seeds = [];
  for (let y = 0; y < HEIGHT; y++) for (let x = 0; x < WIDTH; x++) {
    const p = (y * WIDTH + x) * 4;
    if (map[p] !== 7 || head[p / 4] || anatomy[p] <= 220 || anatomy[p + 1] <= 195 || anatomy[p + 2] <= 123) continue;
    seeds.push([x, y]);
    for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
      if (x + dx < 0 || x + dx >= WIDTH || y + dy < 0 || y + dy >= HEIGHT) continue;
      const q = (y + dy) * WIDTH + x + dx;
      if (!head[q]) result[q] = 1;
    }
  }
  const bowl = convexMask(seeds);
  for (let p = 0; p < result.length; p++) if (bowl[p] && !head[p]) result[p] = 1;
  // The gold concavity inside a closed cream bowl is part of that same prop, not a shirt opening
  const outside = new Uint8Array(result.length), queue = [];
  for (let x = 0; x < WIDTH; x++) queue.push(x, (HEIGHT - 1) * WIDTH + x);
  for (let y = 1; y < HEIGHT - 1; y++) queue.push(y * WIDTH, y * WIDTH + WIDTH - 1);
  for (let i = 0; i < queue.length; i++) {
    const p = queue[i];
    if (outside[p] || result[p]) continue;
    outside[p] = 1;
    const x = p % WIDTH, y = Math.floor(p / WIDTH);
    for (const q of [x > 0 ? p - 1 : -1, x + 1 < WIDTH ? p + 1 : -1, y > 0 ? p - WIDTH : -1, y + 1 < HEIGHT ? p + WIDTH : -1])
      if (q >= 0 && !outside[q] && !result[q]) queue.push(q);
  }
  for (let p = 0; p < result.length; p++) if (!outside[p] && !head[p]) result[p] = 1;
  return result;
}

function continuousGarments(anatomy, map, fits, head, props) {
  const output = Buffer.from(map), wings = [[], []];
  for (let y = 0; y < HEIGHT; y++) for (let x = 0; x < WIDTH; x++) {
    const p = (y * WIDTH + x) * 4, label = map[p];
    if (props[p / 4] || head[p / 4] || !anatomy[p + 3]) continue;
    if (label === 2 || label === 6 || label === 3 || label === 7 && anatomy[p] - anatomy[p + 1] > 30)
      wings[label === 2 || label === 6 ? 0 : 1].push([x, y]);
  }
  const wingMasks = wings.map(convexMask);
  function paint(x, y, region) {
    const p = (y * WIDTH + x) * 4;
    if (!anatomy[p + 3] || head[p / 4] || props[p / 4]) return;
    const u = fits[region][0][0] * x + fits[region][0][1] * y + fits[region][0][2];
    const v = fits[region][1][0] * x + fits[region][1][1] * y + fits[region][1][2];
    if (region !== 1 && (v < 0 || v > 255)) return;
    output.set([region, Math.round(Math.max(0, Math.min(255, u))), Math.round(Math.max(0, Math.min(255, v))), 255], p);
  }
  for (let y = 0; y < HEIGHT; y++) {
    const xs = [];
    for (let x = 0; x < WIDTH; x++) if (map[(y * WIDTH + x) * 4] === 1) xs.push(x);
    if (xs.length) for (let x = xs[0]; x <= xs.at(-1); x++) if (!wingMasks[0][y * WIDTH + x] && !wingMasks[1][y * WIDTH + x]) paint(x, y, 1);
  }
  for (let side = 0; side < 2; side++) for (let p = 0; p < wingMasks[side].length; p++) if (wingMasks[side][p]) paint(p % WIDTH, Math.floor(p / WIDTH), side + 2);
  return output;
}

export function composeFrame(anatomy, map, binding, neutralBinding, neutralFits, variant, authoredFits) {
  const fits = Array.from({ length: 6 }, (_, i) => i ? fitUv(map, i) : null);
  for (const [region, fit] of authoredFits ?? []) fits[region] = fit;
  const head = whiteHead(anatomy), props = heldProps(anatomy, map, head);
  const clothingMap = variant.recipe?.size ? continuousGarments(anatomy, map, fits, head, props) : map;
  const clothed = paintRecipe(anatomy, clothingMap, variant.recipe);
  const middle = binding.body[0] + binding.body[2] / 2;
  for (let y = 0; y < HEIGHT; y++) for (let x = 0; x < WIDTH; x++) {
    const p = (y * WIDTH + x) * 4, region = materialRegion(clothingMap, anatomy, p, x, y, middle);
    if (!region || props[p / 4]) continue;
    const [sx, sy] = correspondence(neutralFits[region], fits[region], x, y);
    const delta = sample(variant.material[region], WIDTH, HEIGHT, sx, sy);
    for (let c = 0; c < 3; c++) clothed[p + c] = Math.max(0, Math.min(255, Math.round(clothed[p + c] + delta[c])));
  }
  for (let p = 0; p < clothed.length; p += 4) if (props[p / 4]) clothed.set(anatomy.subarray(p, p + 4), p);
  const output = Buffer.alloc(anatomy.length);
  for (const layer of variant.accessories.filter(x => x.name === 'outfit-back')) paintAccessory(output, layer, neutralBinding, binding);
  for (let p = 0; p < output.length; p += 4) over(output, p, clothed.subarray(p, p + 4));
  for (const layer of variant.accessories.filter(x => x.name !== 'outfit-back')) paintAccessory(output, layer, neutralBinding, binding);
  for (let p = 0; p < output.length; p += 4) if ([2, 3, 6, 7].includes(map[p]) && map[p + 3]) {
    // Original fingers and the bowl remain in front of helmet/glasses, including their dark outline
    const x = p / 4 % WIDTH, y = Math.floor(p / 4 / WIDTH);
    const brown = anatomy[p] - anatomy[p + 1] > 30 && anatomy[p + 1] - anatomy[p + 2] > 9;
    const region = x < middle ? 2 : 3;
    const [, sourceY] = correspondence(neutralFits[region], fits[region], x, y);
    if (head[p / 4] || brown && sourceY < 242 || !brown && map[p] !== 7) continue;
    for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
      if (x + dx < 0 || x + dx >= WIDTH || y + dy < 0 || y + dy >= HEIGHT) continue;
      const q = ((y + dy) * WIDTH + x + dx) * 4;
      if (clothed[q + 3] === 255) output.set(clothed.subarray(q, q + 4), q);
    }
  }
  return output;
}

export function differences(a, b) {
  let alpha = 0, visible = 0, premultiplied = 0, maximum = 0;
  for (let p = 0; p < a.length; p += 4) {
    alpha += Number(a[p + 3] !== b[p + 3]);
    if ((a[p + 3] || b[p + 3]) && [0, 1, 2].some(c => a[p + c] !== b[p + c])) visible++;
    const difference = Math.max(...[0, 1, 2].map(c => Math.abs(a[p + c] * a[p + 3] - b[p + c] * b[p + 3]) / 255));
    if (difference > .01) premultiplied++;
    maximum = Math.max(maximum, difference);
  }
  return { alpha, visible, premultiplied, maximum };
}

export function selectedVariants(samplesOnly, requested) {
  const selected = requested ?? (samplesOnly ? variants : ['default']);
  if (!selected.length || new Set(selected).size !== selected.length || selected.some(name => !variants.includes(name)))
    throw new Error('Invalid or duplicate outfit subset');
  if (!samplesOnly && selected.some(name => !authorCandidateVariants.includes(name)))
    throw new Error('Complex outfit motion artwork has not passed review; only diagnostic samples are permitted');
  return selected;
}

export async function build(outputArgument, samplesOnly = false, requested) {
  const selected = selectedVariants(samplesOnly, requested);
  const output = path.resolve(outputArgument);
  if (!output.startsWith(path.join(root, '.codex-build') + path.sep)) throw new Error('Candidate output must be a new .codex-build directory');
  console.log('Output: ' + output);
  await fs.mkdir(output, { recursive: true });
  if ((await fs.readdir(output)).length) throw new Error('Output directory must be empty');
  const sources = {};
  async function read(name) { const bytes = await fs.readFile(path.join(root, name)); sources[name] = sha(bytes); return bytes; }
  const zip = readZip(await read('DuckDeskPet/Assets/OutfitBindings/Default-v1.zip'));
  const bindings = JSON.parse(zip.get('bindings.json')), neutralBinding = bindings.frames.neutral;
  const sleeveCorrection = selected.includes('hoodie') ? await hoodieSleeveCorrection(zip, bindings) : undefined;
  const sleeveAxisScan = selected.includes('hoodie') ? await scanSleeveAxes(zip, bindings, sleeveCorrection) : undefined;
  const neutralBytes = await read('DuckDeskPet/' + neutralBinding.source), neutral = await sharp(neutralBytes).ensureAlpha().raw().toBuffer();
  const neutralMap = await sharp(zip.get(neutralBinding.map)).ensureAlpha().raw().toBuffer();
  const neutralFits = Array.from({ length: 6 }, (_, i) => i ? fitUv(neutralMap, i) : null);
  const manifest = { version: 1, width: WIDTH, height: HEIGHT, frameCount: 121, durationSeconds: 2,
    outfits: Object.fromEntries(variants.map(name => ['outfit.' + name, { route: selected.includes(name) ? 'native-raster' : 'live2d',
      ...(selected.includes(name) ? { directory: name } : {}) }])) };
  const validation = { samplesOnly, selectedOutfits: selected, sourceHashes: sources, evidence: {},
    authoredUvCorrections: selected.includes('hoodie') ? [sleeveCorrection.report] : [],
    sleeveAxisScan,
    status: 'Candidate awaiting complete sequence review',
    productionApprovedNativeOutfits: ['default'],
    unapprovedAuthorCandidates: {
      office: 'Raised wrist can sample jacket blue from inconsistent torso/sleeve slots; requires anatomical slot re-authoring',
      hoodie: 'Local Yawn 46..51 axis correction only; still requires complete outfit animation acceptance before production use',
    },
    excludedComplexOutfits: { outfits: ['ox', 'hero', 'astronaut'],
      reason: 'Existing UV garment/hand partitions and transparent fabric cutouts fragment under raised-arm poses; neutral equality is not motion acceptance',
      followUp: 'Re-author local chest/sleeve UV and occlusion tracks for both actions; do not replace these outfits with undressed or known-bad PNGs' },
    boundary: 'Development candidate requiring full native-size review; anatomical texture transfer is not new hand-drawn animation' };
  const records = [];
  for (const name of selected) {
    const directory = path.join(output, name);
    await fs.mkdir(directory);
    const expected = await sharp(await read(`${sourceRoot}/${name}/neutral.png`)).ensureAlpha().raw().toBuffer();
    const psd = readPsd(await read(`${sourceRoot}/${name}/eagle.psd`), { useImageData: true, skipThumbnail: true });
    const body = flatten(psd.children.filter(layer => !layer.name.startsWith('outfit-')));
    const recipe = new Map();
    if (['ox', 'hero', 'astronaut'].includes(name)) {
      const title = name[0].toUpperCase() + name.slice(1);
      const definitions = JSON.parse(await read(`DuckDeskPet/Assets/Outfits/${title}/appearance.json`));
      const slots = ['torso', 'leftSleeve', 'rightSleeve', 'leftBoot', 'rightBoot'];
      for (const definition of definitions.layers.filter(x => slots.includes(x.slot))) {
        const { data, info } = await sharp(await read('DuckDeskPet/' + definition.image)).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
        recipe.set(slots.indexOf(definition.slot) + 1, { ...definition, data, ...info });
      }
    }
    const neutralHead = whiteHead(neutral), neutralProps = heldProps(neutral, neutralMap, neutralHead);
    const clothingMap = recipe.size ? continuousGarments(neutral, neutralMap, neutralFits, neutralHead, neutralProps) : neutralMap;
    const recipeNeutral = paintRecipe(neutral, clothingMap, recipe);
    const material = Array.from({ length: 6 }, () => new Float32Array(body.length));
    for (let p = 0; p < body.length; p += 4) {
      if (body[p + 3] !== neutral[p + 3]) throw new Error(name + ' changes native body alpha');
      const x = p / 4 % WIDTH, y = Math.floor(p / 4 / WIDTH), middle = neutralBinding.body[0] + neutralBinding.body[2] / 2;
      const region = materialRegion(clothingMap, neutral, p, x, y, middle);
      for (let c = 0; c < 3; c++) material[region][p + c] = body[p + c] - recipeNeutral[p + c];
    }
    const variant = { material, recipe, accessories: psd.children.filter(layer => layer.name.startsWith('outfit-')) };
    const rendered = name === 'default' ? neutral : composeFrame(neutral, neutralMap, neutralBinding, neutralBinding, neutralFits, variant);
    const seam = differences(rendered, expected);
    if (name === 'default') await fs.writeFile(path.join(directory, 'neutral.png'), neutralBytes);
    else await sharp(rendered, { raw: { width: WIDTH, height: HEIGHT, channels: 4 } }).png().toFile(path.join(directory, 'neutral.png'));
    console.log(name + ' neutral ' + JSON.stringify(seam));
    records.push({ name, variant, expected, rendered, directory, seam });
    validation.evidence[name] = { neutral: seam, curves: {} };
  }
  // Fail visibly before producing misleading action endpoints; the compositor must be identity-correct
  await fs.writeFile(path.join(output, 'neutral-report.json'), JSON.stringify(validation, null, 2) + '\n');
  if (records.some(x => x.seam.alpha || x.seam.visible)) throw new Error('Neutral material/occlusion correspondence needs review; no endpoint replacement permitted');
  for (const clip of ['Yawn', 'Eat']) {
    const previous = new Map();
    for (const record of records) { await fs.mkdir(path.join(record.directory, clip)); validation.evidence[record.name].curves[clip] = []; }
    for (let frame = 0; frame <= 120; frame++) {
      if (samplesOnly && ![0, 1, 6, 15, 30, 60, 90, 114, 119, 120].includes(frame)) continue;
      const id = String(frame).padStart(4, '0'), binding = bindings.frames[clip + '/' + id];
      const bytes = await read('DuckDeskPet/' + binding.source);
      if (sha(bytes) !== binding.sha256.toLowerCase()) throw new Error('Stale anatomy: ' + binding.source);
      const anatomy = await sharp(bytes).ensureAlpha().raw().toBuffer(), map = await sharp(zip.get(binding.map)).ensureAlpha().raw().toBuffer();
      for (const record of records) {
        const pixels = record.name === 'default' ? anatomy : composeFrame(anatomy, map, binding, neutralBinding, neutralFits, record.variant,
          authoredSleeveFits(record.name, clip, frame, sleeveCorrection));
        if ((frame === 0 || frame === 120) && (differences(pixels, record.expected).visible || differences(pixels, record.expected).alpha))
          throw new Error(record.name + ' ' + clip + ' endpoint is not the same material identity');
        const file = path.join(record.directory, clip, 'frame-' + id + '.png');
        if (record.name === 'default') await fs.writeFile(file, bytes);
        else await sharp(pixels, { raw: { width: WIDTH, height: HEIGHT, channels: 4 } }).png().toFile(file);
        const feet = [];
        for (let y = 309; y < HEIGHT; y++) for (let x = 0; x < WIDTH; x++) if (pixels[(y * WIDTH + x) * 4 + 3] > 8) feet.push([x, y]);
        validation.evidence[record.name].curves[clip].push({ frame, previous: differences(pixels, previous.get(record.name) ?? record.rendered),
          neutral: differences(pixels, record.expected), footBounds: feet.length ? [Math.min(...feet.map(p => p[0])), Math.min(...feet.map(p => p[1])), Math.max(...feet.map(p => p[0])), Math.max(...feet.map(p => p[1]))] : null });
        previous.set(record.name, pixels);
      }
      if (frame % 30 === 0) console.log(clip + ' ' + frame + '/120');
    }
  }
  await fs.writeFile(path.join(output, 'authoring-validation.json'), JSON.stringify(validation, null, 2) + '\n');
  if (!samplesOnly) await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
  console.log('Hybrid candidate complete: ' + output);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (!process.argv[2]) throw new Error('Usage: node tools/Live2DArt/pack-hybrid-actions.mjs <new-.codex-build-directory>');
  const subset = process.argv.find(value => value.startsWith('--outfits='))?.slice('--outfits='.length).split(',');
  await build(process.argv[2], process.argv.includes('--samples-only'), subset);
}
