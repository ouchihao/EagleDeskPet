import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import sharp from 'sharp';
import { writePsdBuffer, readPsd, initializeCanvas } from 'ag-psd';

initializeCanvas(() => { throw new Error('This authoring tool uses raw RGBA'); },
  (width, height) => ({ width, height, data: new Uint8ClampedArray(width * height * 4) }));

const [sourcePath, hiddenPartsPath, outputDirectory] = process.argv.slice(2);
if (!sourcePath || !hiddenPartsPath || !outputDirectory)
  throw new Error('Usage: node pack-canonical-eagle.mjs <canonical-neutral.png> <hidden-parts-v2.png> <new-output-directory>');
const output = path.resolve(outputDirectory);
console.log(`Output: ${output}`);
await fs.mkdir(output, { recursive: true });
if ((await fs.readdir(output)).length) throw new Error('Output directory must be empty');
const { data: original, info } = await sharp(sourcePath).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
const { width, height } = info;
if (width !== 384 || height !== 346) throw new Error('Canonical layout requires the original 384 x 346 neutral');
const sourceHash = crypto.createHash('sha256').update(original).digest('hex');
if (sourceHash !== 'bfb4b0cba193d0a296070e9ec731f1629a10a261adb275b3a8d40d6506186e79')
  throw new Error('Canonical pixels changed; review masks before authoring a different reference');
const hiddenPartsFile = await fs.readFile(hiddenPartsPath);
const { data: hiddenPartsPixels, info: hiddenPartsInfo } = await sharp(hiddenPartsFile)
  .ensureAlpha().raw().toBuffer({ resolveWithObject: true });
if (hiddenPartsInfo.width !== 1254 || hiddenPartsInfo.height !== 1254)
  throw new Error('Hidden backing requires the reviewed 1254 x 1254 parts-v2 sheet');
const hiddenPartsHash = crypto.createHash('sha256').update(hiddenPartsPixels).digest('hex');
if (hiddenPartsHash !== '1aaa0012bef0cfe7a9281cf426d07de44ad568f7c04acb2513d149157b5643cf')
  throw new Error('Hidden backing pixels changed; review occluded geometry before using a different parts sheet');

const names = ['footwear-r', 'footwear-l', 'topwear', 'handwear-r', 'handwear-l', 'face',
  'facedetail-r', 'facedetail-l', 'eyebrow-r', 'eyebrow-l', 'irides-r', 'irides-l',
  'mouth', 'mouth lower', 'mouth close'];
const layers = names.map(name => ({ name, rgba: Buffer.alloc(original.length) }));
const labels = new Uint8Array(width * height);
const inside = (x, y, points) => {
  let result = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const [xi, yi] = points[i], [xj, yj] = points[j];
    if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) result = !result;
  }
  return result;
};
const rect = (x, y, box) => x >= box[0] && y >= box[1] && x < box[0] + box[2] && y < box[1] + box[3];
const head = [[0, 0], [384, 0], [384, 231], [266, 230], [239, 217], [229, 239], [224, 239],
  [203, 218], [191, 242], [185, 242], [169, 220], [154, 237], [148, 237], [141, 219], [123, 230], [0, 230]];
const wings = [
  [[0, 230], [123, 230], [140, 217], [144, 241], [155, 260], [160, 271], [158, 282], [149, 290], [134, 295], [0, 295]],
  [[238, 217], [235, 243], [228, 257], [224, 272], [227, 283], [235, 290], [252, 295], [384, 295], [384, 230], [266, 230]],
];
const features = [
  [6, [139, 162, 23, 12]], [7, [235, 162, 23, 12]],
  [8, [152, 125, 29, 16]], [9, [214, 125, 31, 16]],
  [10, [150, 139, 32, 25]], [11, [215, 139, 30, 25]],
];
const beak = [[188, 148], [202, 148], [215, 154], [224, 161], [232, 165], [233, 178],
  [222, 185], [210, 196], [201, 198], [186, 192], [173, 184], [165, 177], [165, 165], [177, 158]];

// Native PSD cut masks only partition source pixels; they never redraw the visible character
for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
  const i = y * width + x, offset = i * 4;
  let label = 2;
  if (inside(x + 0.5, y + 0.5, head)) label = 5;
  else if (y >= 309 && x < 193) label = 0;
  else if (y >= 309 && x >= 193) label = 1;
  else if (inside(x + 0.5, y + 0.5, wings[0])) label = 3;
  else if (inside(x + 0.5, y + 0.5, wings[1])) label = 4;
  if (label === 5) {
    if (inside(x + 0.5, y + 0.5, beak)) label = 14;
    for (const [index, box] of features) if (rect(x, y, box) && (index < 10 || label !== 14)) label = index;
  }
  labels[i] = label;
  original.copy(layers[label].rgba, offset, offset, offset + 4);
}

// Existing generated art supplies occluded backing only, below the untouched source cutouts
async function backing(crop, placement) {
  const [left, top, w, h] = placement;
  const data = await sharp(hiddenPartsFile).extract({ left: crop[0], top: crop[1], width: crop[2], height: crop[3] })
    .resize(w, h, { fit: 'fill' }).ensureAlpha().raw().toBuffer();
  return { left, top, width: w, height: h, data };
}
const bodyBacking = await backing([440, 210, 180, 110], [111, 197, 162, 119]);
const wingBackings = [
  await backing([752, 124, 184, 251], [111, 197, 49, 98]),
  await backing([1030, 124, 183, 251], [224, 197, 49, 98]),
];
function copyBacking(target, image, accepts) {
  for (let y = image.top; y < image.top + image.height; y++) for (let x = image.left; x < image.left + image.width; x++) {
    const i = y * width + x, offset = i * 4;
    if (original[offset + 3] !== 255 || !accepts(labels[i], x, y)) continue;
    const source = ((y - image.top) * image.width + x - image.left) * 4;
    image.data.copy(layers[target].rgba, offset, source, source + 4);
  }
}
// Clone an unoccluded strip of the actual face beneath each cutout, not another drawing's white
for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
  const i = y * width + x, label = labels[i], offset = i * 4;
  if (label < 6 || original[offset + 3] !== 255) continue;
  const row = label === 14 ? 199 : label >= 10 ? y : label >= 8 ? 123 : 161;
  const column = label === 10 ? 146 : label === 11 ? 248 : x;
  const source = (row * width + column) * 4;
  original.copy(layers[5].rgba, offset, source, source + 4);
}
copyBacking(2, bodyBacking, label => label === 3 || label === 4 || label === 5);
copyBacking(3, wingBackings[0], label => label >= 5);
copyBacking(4, wingBackings[1], label => label >= 5);
// Shared interior pixels extend beneath the feet so bilinear sampling cannot open the ankle cut
for (let y = 305; y < 309; y++) for (let x = 0; x < width; x++) {
  const offset = (y * width + x) * 4;
  if (original[offset + 3] !== 255) continue;
  original.copy(layers[x < 193 ? 0 : 1].rgba, offset, offset, offset + 4);
}
const inner = await backing([732, 1096, 134, 82], [187, 172, 22, 13]);
const lower = await backing([401, 824, 225, 102], [184, 177, 29, 13]);
copyBacking(12, inner, label => label === 14);
copyBacking(13, lower, label => label === 14);

const children = [];
for (const layer of layers) {
  let left = width, top = height, right = -1, bottom = -1;
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    if (!layer.rgba[(y * width + x) * 4 + 3]) continue;
    left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x); bottom = Math.max(bottom, y);
  }
  if (right < left) throw new Error(`Empty canonical layer: ${layer.name}`);
  const box = { left, top, width: right - left + 1, height: bottom - top + 1 };
  const rgba = await sharp(layer.rgba, { raw: { width, height, channels: 4 } }).extract(box).raw().toBuffer();
  children.push({ name: layer.name, left, top, imageData: { width: box.width, height: box.height, data: new Uint8ClampedArray(rgba) } });
}
const composite = Buffer.alloc(original.length);
for (const child of children) {
  const { width: w, height: h, data } = child.imageData;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const source = (y * w + x) * 4, destination = ((child.top + y) * width + child.left + x) * 4;
    const alpha = data[source + 3];
    if (!alpha) continue;
    if (alpha === 255 || !composite[destination + 3]) {
      composite.set(data.subarray(source, source + 4), destination);
      continue;
    }
    const a = alpha / 255, behind = composite[destination + 3] / 255, combined = a + behind * (1 - a);
    for (let c = 0; c < 3; c++)
      composite[destination + c] = Math.round((data[source + c] * a + composite[destination + c] * behind * (1 - a)) / combined);
    composite[destination + 3] = Math.round(combined * 255);
  }
}
let alphaDifferences = 0, visibleDifferences = 0, opaqueDifferences = 0, maximumPremultipliedDelta = 0;
for (let i = 0; i < original.length; i += 4) {
  if (original[i + 3] !== composite[i + 3]) alphaDifferences++;
  let differs = false;
  for (let c = 0; c < 3; c++) {
    const delta = Math.abs(original[i + c] * original[i + 3] - composite[i + c] * composite[i + 3]) / 255;
    maximumPremultipliedDelta = Math.max(maximumPremultipliedDelta, delta);
    if (delta > 0) differs = true;
  }
  if (differs) visibleDifferences++;
  if (differs && original[i + 3] === 255) opaqueDifferences++;
}
if (alphaDifferences || visibleDifferences)
  throw new Error(`Neutral identity mismatch: ${JSON.stringify({ alphaDifferences, opaqueDifferences, maximumPremultipliedDelta })}`);
const psd = writePsdBuffer({ width, height, children,
  imageData: { width, height, data: new Uint8ClampedArray(composite) } }, { generateThumbnail: false, noBackground: true });
const checked = readPsd(psd, { useImageData: true, skipThumbnail: true });
if (checked.children.length !== 15) throw new Error('Canonical PSD layer round-trip failed');
await fs.writeFile(path.join(output, 'eagle.psd'), psd);
await sharp(composite, { raw: { width, height, channels: 4 } }).png().toFile(path.join(output, 'eagle-neutral.png'));
const report = { source: sourcePath, sourceRgbaSha256: sourceHash, canvas: [width, height],
  hiddenPartsSource: hiddenPartsPath, hiddenPartsRgbaSha256: hiddenPartsHash,
  hiddenPartsCanvas: [hiddenPartsInfo.width, hiddenPartsInfo.height],
  alphaDifferences, visibleDifferences, opaqueDifferences, maximumPremultipliedDelta,
  sourceVisiblePixelsReused: true, generatedArt: 'Hidden backing only; no generated visible silhouette or facial features',
  layers: children.map(layer => ({ name: layer.name, left: layer.left, top: layer.top, width: layer.imageData.width, height: layer.imageData.height })) };
await fs.writeFile(path.join(output, 'identity.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify(report, null, 2));
