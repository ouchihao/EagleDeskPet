import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { inflateRawSync } from 'node:zlib';
import sharp from 'sharp';
import { readPsd, writePsdBuffer, initializeCanvas } from 'ag-psd';

initializeCanvas(() => { throw new Error('Raw authoring only'); },
  (width, height) => ({ width, height, data: new Uint8ClampedArray(width * height * 4) }));
const [outputArgument] = process.argv.slice(2);
if (!outputArgument) throw new Error('Usage: node tools/Live2DArt/pack-action-eagle.mjs <new-output-dir>');
const root = path.resolve(import.meta.dirname, '../..'), output = path.resolve(outputArgument);
console.log(`Output: ${output}`);
await fs.mkdir(output, { recursive: true });
if ((await fs.readdir(output)).length) throw new Error('Output directory must be empty');
const sources = new Map();
async function read(relative) {
  const bytes = await fs.readFile(path.join(root, relative));
  sources.set(relative, crypto.createHash('sha256').update(bytes).digest('hex'));
  return bytes;
}
const sourcePsd = await read('DuckDeskPet/Assets/Live2DSource/Canonical/eagle.psd');
if (crypto.createHash('sha256').update(sourcePsd).digest('hex') !== 'bc071b8fcbd1b3ccd85a0937808b6b31f3e3274260cf3414073d5e6740ec6c2b')
  throw new Error('Canonical PSD changed; review new source before authoring');
const canonical = readPsd(sourcePsd, { useImageData: true, skipThumbnail: true });
const parts = await read('DuckDeskPet/Assets/Live2DSource/eagle-action-parts-v1.png');
if (crypto.createHash('sha256').update(parts).digest('hex') !== '439fc5d78e0dc1b52450ae85806ca9739c1762c1e27d3f936fec6ac283ca36d9')
  throw new Error('Reviewed auxiliary parts changed; update layout and visual acceptance explicitly');
const partsInfo = await sharp(parts).metadata();
if (partsInfo.width !== 1254 || partsInfo.height !== 1254) throw new Error('Auxiliary sheet layout changed');
const canvas = { width: 384, height: 346 };
const partsLayout = [
  ['beak-inner', [912, 107, 267, 225], [176, 163, 44, 34]],
  ['beak-lower', [493, 178, 277, 140], [176, 173, 46, 21]],
  ['beak-upper', [74, 128, 309, 204], [167, 151, 63, 40]],
  ['rps-rock', [82, 525, 273, 276], [122, 230, 36, 46]],
  ['rps-paper', [446, 452, 363, 350], [114, 211, 53, 65]],
  ['rps-scissors', [931, 448, 241, 356], [119, 211, 43, 65]],
];
async function partLayer(name, crop, placement) {
  const [left, top, width, height] = placement;
  const rgba = await sharp(parts).extract({ left: crop[0], top: crop[1], width: crop[2], height: crop[3] })
    .resize(width, height, { fit: 'fill' }).ensureAlpha().raw().toBuffer();
  return { name, left, top, opacity: 0, hidden: false, imageData: { width, height, data: new Uint8ClampedArray(rgba) } };
}
const actionLayers = await Promise.all(partsLayout.map(args => partLayer(...args)));
for (const layer of actionLayers.filter(x => x.name.startsWith('rps-'))) {
  const original = layer.imageData, height = 280 - layer.top;
  const data = new Uint8ClampedArray(original.width * height * 4);
  const openRow = 271 - layer.top;
  data.set(original.data.subarray(0, (openRow + 1) * original.width * 4));
  // Extend the existing wrist sides and interior as an open joint, not a closed icon attached to another palm
  for (let y = openRow + 1; y < height; y++)
    data.set(original.data.subarray(openRow * original.width * 4, (openRow + 1) * original.width * 4), y * original.width * 4);
  layer.imageData = { width: original.width, height, data };
}
const busyReference = await sharp(await read('DuckDeskPet/Assets/Animations/BusyLoop/frame-0000.png')).ensureAlpha().raw().toBuffer();
const busyEyes = [];
for (const [name, sx, targetX, right] of [['busy-eye-r', 143, 152, false], ['busy-eye-l', 205, 215, true]]) {
  const width = 32, height = 24, top = 154, pixels = Buffer.alloc(width * height * 4);
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    // Follow the original slanted lid, excluding the eyebrow and surrounding white face
    const upperEdge = right ? 10 - x * .34 : 1 + x * .34;
    if (y < upperEdge || y > 23) continue;
    const s = ((top + y) * canvas.width + sx + x) * 4, d = (y * width + x) * 4;
    const minimum = Math.min(...busyReference.subarray(s, s + 3));
    if (minimum > 232) continue;
    const alpha = Math.min(1, (255 - minimum) / 220);
    for (let c = 0; c < 3; c++) pixels[d + c] = Math.max(0, Math.round((busyReference[s + c] - 255 * (1 - alpha)) / alpha));
    pixels[d + 3] = Math.round(alpha * 255);
  }
  busyEyes.push({ name, left: targetX, top: 141, opacity: 0, hidden: false,
    imageData: { width, height, data: new Uint8ClampedArray(pixels) } });
}
const extras = {};
await fs.mkdir(path.join(output, 'props'));
function cleanPropAlpha(data, width, height) {
  const mask = new Uint8Array(width * height), seen = new Uint8Array(mask.length);
  for (let y = 1; y < height - 1; y++) for (let x = 1; x < width - 1; x++) {
    let solid = true;
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++)
      solid &&= data[((y + dy) * width + x + dx) * 4 + 3] >= 224;
    mask[y * width + x] = Number(solid);
  }
  let largest = [];
  for (let start = 0; start < mask.length; start++) {
    if (!mask[start] || seen[start]) continue;
    const component = [start]; seen[start] = 1;
    for (let i = 0; i < component.length; i++) {
      const p = component[i], x = p % width, y = Math.floor(p / width);
      for (const n of [x > 0 ? p - 1 : -1, x + 1 < width ? p + 1 : -1, y > 0 ? p - width : -1, y + 1 < height ? p + width : -1]) {
        if (n >= 0 && mask[n] && !seen[n]) { seen[n] = 1; component.push(n); }
      }
    }
    if (component.length > largest.length) largest = component;
  }
  const keep = new Uint8Array(mask.length);
  for (const p of largest) {
    const x = p % width, y = Math.floor(p / width);
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
      if (x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height) keep[(y + dy) * width + x + dx] = 1;
    }
  }
  for (let p = 0; p < mask.length; p++) if (!keep[p]) data.fill(0, p * 4, p * 4 + 4);
  return data;
}
for (const [name, crop, rect] of [
  ['bowlFull', [54, 905, 334, 257], [158, 229, 79, 60]],
  ['bowlEmpty', [463, 932, 333, 230], [158, 235, 79, 54]],
  ['tea', [897, 912, 319, 254], [165, 192, 51, 41]],
]) {
  const pixels = await sharp(parts).extract({ left: crop[0], top: crop[1], width: crop[2], height: crop[3] }).ensureAlpha().raw().toBuffer();
  await sharp(cleanPropAlpha(pixels, crop[2], crop[3]), { raw: { width: crop[2], height: crop[3], channels: 4 } }).png().toFile(path.join(output, 'props', name + '.png'));
  extras[name] = { url: `props/${name}.png`, rect };
}

// Read only two named records from our versioned anatomical archive, never extract arbitrary paths
function zipEntry(zip, name) {
  let end = zip.length - 22;
  while (end >= Math.max(0, zip.length - 65557) && zip.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0 || zip.readUInt32LE(end) !== 0x06054b50) throw new Error('Invalid binding archive');
  let offset = zip.readUInt32LE(end + 16);
  for (let i = 0; i < zip.readUInt16LE(end + 10); i++) {
    if (zip.readUInt32LE(offset) !== 0x02014b50) throw new Error('Invalid central record');
    const nameLength = zip.readUInt16LE(offset + 28), extraLength = zip.readUInt16LE(offset + 30), commentLength = zip.readUInt16LE(offset + 32);
    if (zip.toString('utf8', offset + 46, offset + 46 + nameLength) === name) {
      const method = zip.readUInt16LE(offset + 10), size = zip.readUInt32LE(offset + 20), expanded = zip.readUInt32LE(offset + 24), local = zip.readUInt32LE(offset + 42);
      if (expanded > 8 * 1024 * 1024 || zip.readUInt32LE(local) !== 0x04034b50) throw new Error('Invalid bounded binding record');
      const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28), compressed = zip.subarray(start, start + size);
      const result = method === 0 ? compressed : method === 8 ? inflateRawSync(compressed, { maxOutputLength: 8 * 1024 * 1024 }) : null;
      if (!result || result.length !== expanded) throw new Error('Invalid binding record length');
      return result;
    }
    offset += 46 + nameLength + extraLength + commentLength;
  }
  throw new Error('Missing binding record: ' + name);
}
const bindingsZip = await read('DuckDeskPet/Assets/OutfitBindings/Default-v1.zip');
const bindings = JSON.parse(zipEntry(bindingsZip, 'bindings.json'));
const anchor = bindings.frames.neutral;
const map = await sharp(zipEntry(bindingsZip, anchor.map)).ensureAlpha().raw().toBuffer();
const slotLayers = { torso: 'topwear', leftSleeve: 'handwear-r', rightSleeve: 'handwear-l', leftBoot: 'footwear-r', rightBoot: 'footwear-l' };
const slotRegions = { torso: 1, leftSleeve: 2, rightSleeve: 3, leftBoot: 4, rightBoot: 5 };
function sample(image, u, v) {
  const x = Math.max(0, Math.min(1, u)) * (image.width - 1), y = Math.max(0, Math.min(1, v)) * (image.height - 1);
  const x0 = Math.floor(x), y0 = Math.floor(y), x1 = Math.min(x0 + 1, image.width - 1), y1 = Math.min(y0 + 1, image.height - 1), fx = x - x0, fy = y - y0;
  const values = [0, 0, 0, 0];
  for (const [px, py, weight] of [[x0, y0, (1 - fx) * (1 - fy)], [x1, y0, fx * (1 - fy)], [x0, y1, (1 - fx) * fy], [x1, y1, fx * fy]]) {
    const index = (py * image.width + px) * 4, a = image.data[index + 3] * weight;
    for (let c = 0; c < 3; c++) values[c] += image.data[index + c] * a;
    values[3] += a;
  }
  if (values[3]) for (let c = 0; c < 3; c++) values[c] /= values[3];
  return values;
}
function paintClothing(layer, definition, art) {
  const q = definition.uvQuad ?? [0, 0, 1, 0, 0, 1, 1, 1], region = slotRegions[definition.slot], image = layer.imageData;
  for (let y = 0; y < image.height; y++) for (let x = 0; x < image.width; x++) {
    const p = (y * image.width + x) * 4;
    if (!image.data[p + 3]) continue;
    const worldX = layer.left + x, worldY = layer.top + y, m = (worldY * canvas.width + worldX) * 4;
    const mapped = map[m] === region && map[m + 3] > 0;
    // Extrapolate the upper, normally occluded garment rather than leaving a bare shoulder under its sleeve
    if (!mapped && worldY >= 223) continue;
    let u = mapped ? map[m + 1] / 255 : x / Math.max(1, image.width - 1);
    let v = mapped ? map[m + 2] / 255 : Math.max(0, (worldY - 221) / Math.max(1, image.height - 22));
    const tx = ((1 - u) * q[0] + u * q[2]) * (1 - v) + ((1 - u) * q[4] + u * q[6]) * v;
    const ty = ((1 - u) * q[1] + u * q[3]) * (1 - v) + ((1 - u) * q[5] + u * q[7]) * v;
    const color = sample(art, tx, ty), coverage = color[3] / 255 * (mapped ? map[m + 3] / 255 : 1);
    for (let c = 0; c < 3; c++) image.data[p + c] = Math.round(image.data[p + c] * (1 - coverage) + color[c] * coverage);
  }
}
async function accessory(definition, bytes) {
  const basis = definition.slot === 'back' ? anchor.body : definition.slot === 'glasses' ? [132, 131, 125, 0, 0, 42] : anchor.head;
  const rect = definition.rect ?? [0, 0, 1, 1];
  const left = Math.round(basis[0] + rect[0] * basis[2] + rect[1] * basis[4]);
  const top = Math.round(basis[1] + rect[0] * basis[3] + rect[1] * basis[5]);
  const width = Math.round(rect[2] * Math.hypot(basis[2], basis[3])), height = Math.round(rect[3] * Math.hypot(basis[4], basis[5]));
  const rgba = await sharp(bytes).resize(width, height).ensureAlpha().raw().toBuffer();
  return { name: `outfit-${definition.slot}`, left, top, imageData: { width, height, data: new Uint8ClampedArray(rgba) } };
}
function compositeLayers(children) {
  const result = Buffer.alloc(canvas.width * canvas.height * 4);
  for (const child of children) {
    if (child.hidden || child.opacity === 0) continue;
    const { data, width, height } = child.imageData;
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const dx = child.left + x, dy = child.top + y;
      if (dx < 0 || dy < 0 || dx >= canvas.width || dy >= canvas.height) continue;
      const p = (y * width + x) * 4, d = (dy * canvas.width + dx) * 4, a = data[p + 3] / 255;
      if (!a) continue;
      const behind = result[d + 3] / 255, alpha = a + behind * (1 - a);
      for (let c = 0; c < 3; c++) result[d + c] = Math.round((data[p + c] * a + result[d + c] * behind * (1 - a)) / alpha);
      result[d + 3] = Math.round(alpha * 255);
    }
  }
  return result;
}
function separateTorso(children) {
  const body = children.find(x => x.name === 'topwear'), b = body.imageData;
  const contours = [[222, 124], [240, 128], [260, 126], [278, 129], [290, 133], [295, 132]];
  for (const side of ['r', 'l']) {
    const wing = children.find(x => x.name === 'handwear-' + side), original = wing.imageData;
    const padding = 5, newLeft = wing.left - (side === 'l' ? padding : 0), width = original.width + padding;
    const data = new Uint8ClampedArray(width * original.height * 4);
    for (let y = 0; y < original.height; y++) {
      const start = (y * width + wing.left - newLeft) * 4;
      data.set(original.data.subarray(y * original.width * 4, (y + 1) * original.width * 4), start);
    }
    wing.left = newLeft;
    const w = wing.imageData = { width, height: original.height, data };
    // Include the entire original inner feather stroke, which the old static mask left on the torso
    for (let y = 0; y < w.height; y++) {
      const wy = wing.top + y;
      if (wy < 236 || wy > 292) continue;
      const occupied = [];
      for (let x = 0; x < w.width; x++) if (w.data[(y * w.width + x) * 4 + 3]) occupied.push(x);
      if (!occupied.length) continue;
      const edge = side === 'r' ? occupied.at(-1) : occupied[0];
      for (let step = 1; step <= padding; step++) {
        const x = edge + step * (side === 'r' ? 1 : -1), wx = wing.left + x;
        if (x < 0 || x >= w.width || wx < body.left || wx >= body.left + b.width) continue;
        const source = ((wy - body.top) * b.width + wx - body.left) * 4;
        w.data.set(b.data.subarray(source, source + 4), (y * w.width + x) * 4);
      }
    }
    for (let y = 0; y < w.height; y++) for (let x = 0; x < w.width; x++) {
      const wx = wing.left + x, wy = wing.top + y, p = (y * w.width + x) * 4;
      if (wy < 222 || wy >= 295 || !w.data[p + 3]) continue;
      const segment = contours.findIndex((point, i) => i < contours.length - 1 && wy >= point[0] && wy < contours[i + 1][0]);
      const [ay, ax] = contours[segment], [by, bx] = contours[segment + 1], t = (wy - ay) / (by - ay);
      const edge = ax + (bx - ax) * t * t * (3 - 2 * t);
      const bi = ((wy - body.top) * b.width + wx - body.left) * 4;
      if (wx < body.left || wx >= body.left + b.width || !b.data[bi + 3]) continue;
      const outside = wx < edge || wx > canvas.width - edge;
      // Move the old occluded wing silhouette into its moving layer, including its antialiased outline
      const a = w.data[p + 3] / 255, behind = b.data[bi + 3] / 255, alpha = a + behind * (1 - a);
      if (outside || Math.round(alpha * 255) === 255) {
        for (let c = 0; c < 3; c++) w.data[p + c] = Math.round((w.data[p + c] * a + b.data[bi + c] * behind * (1 - a)) / alpha);
        w.data[p + 3] = Math.round(alpha * 255);
      }
      if (outside) b.data[bi + 3] = 0;
      else if (w.data[p + 3] === 255) {
        // Extend clean torso paint beneath the moving arm, never retain its old crease or feather outline
        const cleanX = side === 'r' ? 172 : 211, clean = ((wy - body.top) * b.width + cleanX - body.left) * 4;
        const distance = Math.min(wx - edge, canvas.width - edge - wx);
        const outline = Math.max(0, 1 - distance / 2.2);
        for (let c = 0; c < 3; c++) b.data[bi + c] = Math.round(b.data[clean + c] * (1 - outline * .34));
      }
    }
  }
}
const variants = [];
for (const outfit of ['default', 'office', 'hoodie', 'ox', 'hero', 'astronaut']) {
  const children = structuredClone(canonical.children);
  if (outfit === 'office' || outfit === 'hoodie') {
    const name = outfit[0].toUpperCase() + outfit.slice(1);
    const original = await sharp(await read(`DuckDeskPet/Assets/Outfits/${name}/neutral.png`)).ensureAlpha().raw().toBuffer();
    for (const child of children.filter(layer => ['topwear', 'handwear-r', 'handwear-l'].includes(layer.name))) {
      for (let y = 0; y < child.imageData.height; y++) for (let x = 0; x < child.imageData.width; x++) {
        const p = (y * child.imageData.width + x) * 4, wy = child.top + y;
        if (wy < 222 || !child.imageData.data[p + 3]) continue;
        const s = (wy * canvas.width + child.left + x) * 4;
        if (original[s + 3] < 200) continue;
        child.imageData.data.set(original.subarray(s, s + 3), p);
      }
    }
  }
  if (['office', 'ox', 'hero', 'astronaut'].includes(outfit)) {
    const name = outfit[0].toUpperCase() + outfit.slice(1);
    const recipe = JSON.parse(await read(`DuckDeskPet/Assets/Outfits/${name}/appearance.json`));
    for (const definition of recipe.layers) {
      const bytes = await read('DuckDeskPet/' + definition.image);
      if (slotLayers[definition.slot]) {
        const { data, info } = await sharp(bytes).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
        paintClothing(children.find(child => child.name === slotLayers[definition.slot]), definition, { data, ...info });
      } else {
        const layer = await accessory(definition, bytes);
        if (definition.slot === 'back') children.unshift(layer); else children.push(layer);
      }
    }
  }
  separateTorso(children);
  // Keep the shoulder root behind the head and place the articulated forearm in front
  const forearms = [];
  for (const side of ['r', 'l']) {
    const upper = children.find(child => child.name === 'handwear-' + side);
    const { width, height, data } = upper.imageData, splitY = 242, overlapEndY = 254;
    const lowerTop = Math.max(splitY, upper.top), lowerHeight = upper.top + height - lowerTop;
    const lower = Buffer.alloc(width * lowerHeight * 4);
    for (let y = 0; y < lowerHeight; y++) for (let x = 0; x < width; x++) {
      const source = ((lowerTop - upper.top + y) * width + x) * 4, target = (y * width + x) * 4;
      const nx = (x - (width - 1) / 2) / (width / 2), cap = 12 * (1 - Math.sqrt(Math.max(0, 1 - nx * nx)));
      if (lowerTop + y >= overlapEndY || y >= cap && data[source + 3] === 255)
        lower.set(data.subarray(source, source + 4), target);
    }
    upper.imageData = { width, height: overlapEndY - upper.top, data: data.slice(0, width * (overlapEndY - upper.top) * 4) };
    forearms.push({ name: 'forearm-' + side, left: upper.left, top: lowerTop,
      imageData: { width, height: lowerHeight, data: new Uint8ClampedArray(lower) } });
  }
  children.splice(children.findIndex(x => x.name === 'mouth'), 0, ...structuredClone(busyEyes));
  children.splice(children.findIndex(x => x.name === 'mouth close') + 1, 0,
    ...structuredClone(actionLayers.filter(x => x.name.startsWith('beak-'))));
  children.push(...forearms, ...structuredClone(actionLayers.filter(x => x.name.startsWith('rps-'))));
  const neutral = compositeLayers(children);
  const directory = path.join(output, outfit);
  await fs.mkdir(directory);
  const psd = writePsdBuffer({ ...canvas, children, imageData: { ...canvas, data: new Uint8ClampedArray(neutral) } }, { generateThumbnail: false, noBackground: true });
  await fs.writeFile(path.join(directory, 'eagle.psd'), psd);
  await sharp(neutral, { raw: { ...canvas, channels: 4 } }).png().toFile(path.join(directory, 'neutral.png'));
  if (outfit === 'default') {
    const expected = await sharp(await read('DuckDeskPet/Assets/mascot-animated-neutral.png')).ensureAlpha().raw().toBuffer();
    for (let p = 0; p < neutral.length; p += 4) {
      if (neutral[p + 3] !== expected[p + 3] || expected[p + 3] && [0, 1, 2].some(c => neutral[p + c] !== expected[p + c]))
        throw new Error('Action source changed the approved visible neutral at pixel ' + p / 4);
    }
  }
  const record = { id: 'outfit.' + outfit, layers: children.map(layer => ({ name: layer.name, left: layer.left, top: layer.top, width: layer.imageData.width, height: layer.imageData.height, opacity: layer.opacity ?? 1 })), psdSha256: crypto.createHash('sha256').update(psd).digest('hex') };
  await fs.writeFile(path.join(directory, 'source.json'), JSON.stringify(record, null, 2) + '\n');
  variants.push(record);
}
await fs.writeFile(path.join(output, 'source-manifest.json'), JSON.stringify({ version: 1, canvas, variants, extras, sources: Object.fromEntries(sources), defaultNeutralVisibleDifferences: 0 }, null, 2) + '\n');
console.log(JSON.stringify({ variants: variants.map(x => ({ id: x.id, layers: x.layers.length, sha256: x.psdSha256 })), extras, defaultNeutralVisibleDifferences: 0 }, null, 2));
