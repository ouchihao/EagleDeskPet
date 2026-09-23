import fs from 'node:fs/promises';
import path from 'node:path';
import sharp from 'sharp';
import { writePsdBuffer, readPsd, initializeCanvas } from 'ag-psd';

initializeCanvas(() => { throw new Error('This tool uses raw RGBA, not Canvas'); },
  (width, height) => ({ width, height, data: new Uint8ClampedArray(width * height * 4) }));

const [sheetPath, outputDirectory] = process.argv.slice(2);
if (!sheetPath || !outputDirectory) throw new Error('Usage: node pack-eagle.mjs <parts.png> <output-directory>');
const output = path.resolve(outputDirectory);
console.log(`Output: ${output}`);
await fs.mkdir(output, { recursive: true });
if ((await fs.readdir(output)).length) throw new Error('Output directory must be empty');
const sourceSize = await sharp(sheetPath).metadata();
if (sourceSize.width !== 1254 || sourceSize.height !== 1254)
  throw new Error('This layout is for the inspected 1254 x 1254 eagle-parts-v1.png only');
const size = 512;
// Bottom to top, with character-left on the viewer's right
const layout = [
  ['footwear-r', [681, 787, 192, 120], [167, 432, 76, 48]],
  ['footwear-l', [1007, 787, 192, 120], [269, 432, 76, 48]],
  ['topwear', [350, 107, 328, 280], [148, 262, 216, 184]],
  ['handwear-r', [710, 115, 162, 252], [124, 291, 69, 107]],
  ['handwear-l', [1054, 115, 162, 252], [319, 291, 69, 107]],
  ['face', [17, 56, 318, 333], [124, 34, 264, 276]],
  ['facedetail-r', [69, 1070, 171, 75], [169, 178, 34, 15]],
  ['facedetail-l', [391, 1070, 171, 75], [309, 178, 34, 15]],
  ['eyebrow-r', [684, 516, 198, 86], [185, 116, 47, 20]],
  ['eyebrow-l', [999, 516, 198, 86], [280, 116, 47, 20]],
  ['irides-r', [104, 486, 104, 140], [201, 145, 22, 30]],
  ['irides-l', [420, 486, 104, 140], [289, 145, 22, 30]],
  ['mouth', [728, 1077, 115, 68], [234, 201, 44, 26]],
  ['mouth lower', [381, 790, 207, 90], [224, 208, 64, 28]],
  ['mouth close', [43, 740, 262, 177], [208, 173, 96, 65]],
];
const children = [];
const compositeLayers = [];
for (const [name, crop, placement] of layout) {
  const [left, top, width, height] = placement;
  const image = sharp(sheetPath).extract({ left: crop[0], top: crop[1], width: crop[2], height: crop[3] })
    .resize(width, height, { fit: 'fill' }).ensureAlpha();
  const rgba = await image.clone().raw().toBuffer();
  children.push({ name, left, top, imageData: { width, height, data: new Uint8ClampedArray(rgba) } });
  compositeLayers.push({ input: await image.png().toBuffer(), left, top });
}
const composite = sharp({ create: { width: size, height: size, channels: 4, background: '#00000000' } }).composite(compositeLayers);
const preview = await composite.clone().png().toBuffer();
const rgba = await sharp(preview).raw().toBuffer();
const psd = writePsdBuffer({ width: size, height: size, children,
  imageData: { width: size, height: size, data: new Uint8ClampedArray(rgba) } }, { generateThumbnail: false, noBackground: true });
const verified = readPsd(psd, { useImageData: true, skipThumbnail: true });
if (verified.children?.length !== layout.length || verified.width !== size || verified.height !== size)
  throw new Error('PSD round-trip verification failed');
for (let i = 0; i < layout.length; i++) {
  if (verified.children[i].name !== layout[i][0] || !verified.children[i].imageData)
    throw new Error(`PSD layer ${i} failed round-trip verification`);
}
await fs.writeFile(path.join(output, 'eagle.psd'), psd);
await fs.writeFile(path.join(output, 'eagle-neutral.png'), preview);
await fs.writeFile(path.join(output, 'layout.json'), JSON.stringify({ size, order: 'back-to-front', layout }, null, 2) + '\n');
console.log(`Verified ${layout.length} separate RGBA layers: ${path.join(output, 'eagle.psd')}`);
