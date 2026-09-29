import fs from 'node:fs/promises';
import path from 'node:path';
import sharp from 'sharp';

const args = process.argv.slice(2);
const fixedCanvas = args[0] === '--fixed-canvas';
if (fixedCanvas) args.shift();
const [outputDirectory, ...inputs] = args;
if (!outputDirectory || inputs.length < 2 || inputs.length > 4)
  throw new Error('Usage: node compare-eagle.mjs [--fixed-canvas] <new-output-directory> <original.png> <candidate.png> [more.png]');
const output = path.resolve(outputDirectory);
console.log(`Output: ${output}`);
await fs.mkdir(output, { recursive: true });
if ((await fs.readdir(output)).length) throw new Error('Comparison output must be empty');
const height = 400, cellWidth = 350, canvasHeight = 486;
const composites = [], measurements = [];
for (let i = 0; i < inputs.length; i++) {
  const { data, info } = await sharp(inputs[i]).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  if (fixedCanvas && (info.width !== 512 || info.height !== 512))
    throw new Error('Fixed-canvas comparison requires 512 x 512 authoring previews');
  let left = info.width, top = info.height, right = -1, bottom = -1;
  for (let y = 0; y < info.height; y++) for (let x = 0; x < info.width; x++) {
    if (data[(y * info.width + x) * 4 + 3] < 220) continue;
    left = Math.min(left, x); right = Math.max(right, x);
    top = Math.min(top, y); bottom = Math.max(bottom, y);
  }
  if (right < left || bottom < top) throw new Error(`No opaque reference pixels: ${inputs[i]}`);
  const crop = { left, top, width: right - left + 1, height: bottom - top + 1 };
  const width = fixedCanvas ? 340 : Math.round(crop.width * height / crop.height);
  const image = await (fixedCanvas ? sharp(inputs[i]).resize(width, width) : sharp(inputs[i]).extract(crop).resize(width, height)).png().toBuffer();
  composites.push({ input: image, left: i * cellWidth + Math.round((cellWidth - width) / 2), top: 56 });
  const title = `${i + 1}. ${i === 0 ? (fixedCanvas ? 'BEFORE' : 'ORIGINAL') : (fixedCanvas ? `AFTER ${i}` : `CANDIDATE ${i}`)}`;
  composites.push({ input: Buffer.from(`<svg width="350" height="42"><text x="175" y="28" text-anchor="middle" font-family="Arial" font-size="17" fill="#625440">${title}</text></svg>`), left: i * cellWidth, top: 0 });
  measurements.push({ image: inputs[i], framing: fixedCanvas ? 'fixed-512-canvas' : 'normalized-visible-height', alphaThreshold: 220, crop, widthAtHeight444: crop.width * 444 / crop.height });
}
await sharp({ create: { width: cellWidth * inputs.length, height: canvasHeight, channels: 4, background: '#f5f1e7' } })
  .composite(composites).png().toFile(path.join(output, 'comparison.png'));
await fs.writeFile(path.join(output, 'measurements.json'), JSON.stringify(measurements, null, 2) + '\n');
console.log(JSON.stringify(measurements, null, 2));
