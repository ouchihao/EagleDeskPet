import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, readdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';
import { readPsd, initializeCanvas } from 'ag-psd';

initializeCanvas(() => { throw new Error('Tests use raw RGBA, not Canvas'); },
  (width, height) => ({ width, height, data: new Uint8ClampedArray(width * height * 4) }));

const directory = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(directory, '../..');
const source = path.join(repo, 'DuckDeskPet/Assets/mascot-animated-neutral.png');
const hiddenParts = path.join(repo, 'DuckDeskPet/Assets/Live2DSource/eagle-parts-v2.png');
const evidence = await mkdtemp(path.join(tmpdir(), 'eagle-canonical-test-'));
console.log(`Evidence: ${evidence}`);
const output = path.join(evidence, 'valid');
const originalFile = await readFile(source);
const original = await sharp(originalFile).ensureAlpha().raw().toBuffer();
const hiddenOriginalFile = await readFile(hiddenParts);
const hiddenOriginal = await sharp(hiddenOriginalFile).ensureAlpha().raw().toBuffer();
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const sourceFileHash = digest(originalFile);
const hiddenFileHash = digest(hiddenOriginalFile);
const run = (input, destination, backing = hiddenParts) => spawnSync(process.execPath,
  [path.join(directory, 'pack-canonical-eagle.mjs'), input, backing, destination], { encoding: 'utf8' });
const decode = async () => readPsd(await readFile(path.join(output, 'eagle.psd')),
  { useImageData: true, skipThumbnail: true });
const layerPixel = (layer, x, y) => {
  if (x < layer.left || x >= layer.right || y < layer.top || y >= layer.bottom) return [0, 0, 0, 0];
  const offset = ((y - layer.top) * layer.imageData.width + x - layer.left) * 4;
  return Array.from(layer.imageData.data.subarray(offset, offset + 4));
};

// Reconstruct only decoded layer pixels; the PSD composite and authoring report are not evidence
function composeLayers(psd) {
  const result = Buffer.alloc(psd.width * psd.height * 4);
  for (const layer of psd.children) {
    assert.notEqual(layer.hidden, true, layer.name);
    assert.equal(layer.opacity ?? 1, 1, layer.name);
    assert.equal(layer.blendMode ?? 'normal', 'normal', layer.name);
    const { width, height, data } = layer.imageData;
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const sourceOffset = (y * width + x) * 4;
      const destinationOffset = ((layer.top + y) * psd.width + layer.left + x) * 4;
      const sourceAlpha = data[sourceOffset + 3];
      if (sourceAlpha === 0) continue;
      const destinationAlpha = result[destinationOffset + 3];
      if (sourceAlpha === 255 || destinationAlpha === 0) {
        for (let channel = 0; channel < 4; channel++)
          result[destinationOffset + channel] = data[sourceOffset + channel];
        continue;
      }
      const retainedAlpha = destinationAlpha * (1 - sourceAlpha / 255);
      const alpha = sourceAlpha + retainedAlpha;
      for (let channel = 0; channel < 3; channel++)
        result[destinationOffset + channel] = Math.round((data[sourceOffset + channel] * sourceAlpha
          + result[destinationOffset + channel] * retainedAlpha) / alpha);
      result[destinationOffset + 3] = Math.round(alpha);
    }
  }
  return result;
}

function identityDifferences(actual) {
  let alpha = 0, visibleRgb = 0;
  for (let i = 0; i < original.length; i += 4) {
    if (actual[i + 3] !== original[i + 3]) alpha++;
    if (original[i + 3] && (actual[i] !== original[i] || actual[i + 1] !== original[i + 1]
      || actual[i + 2] !== original[i + 2])) visibleRgb++;
  }
  return { alpha, visibleRgb };
}

test('canonical PSD retains the native canvas, reference hash and 15 independent layers', async () => {
  assert.equal(digest(original), 'bfb4b0cba193d0a296070e9ec731f1629a10a261adb275b3a8d40d6506186e79');
  const result = run(source, output);
  assert.equal(result.status, 0, result.stderr);
  const psd = await decode();
  assert.deepEqual([psd.width, psd.height], [384, 346]);
  assert.deepEqual(psd.children.map(layer => layer.name), [
    'footwear-r', 'footwear-l', 'topwear', 'handwear-r', 'handwear-l', 'face',
    'facedetail-r', 'facedetail-l', 'eyebrow-r', 'eyebrow-l', 'irides-r', 'irides-l',
    'mouth', 'mouth lower', 'mouth close',
  ]);
  for (const layer of psd.children) {
    assert.ok(layer.imageData.width > 0 && layer.imageData.height > 0, layer.name);
    assert.equal(layer.imageData.data.length, layer.imageData.width * layer.imageData.height * 4, layer.name);
    assert.ok(layer.left >= 0 && layer.top >= 0 && layer.right <= 384 && layer.bottom <= 346, layer.name);
  }
  assert.equal(digest(await readFile(source)), sourceFileHash);
  assert.equal(digest(await readFile(hiddenParts)), hiddenFileHash);
  const identity = JSON.parse(await readFile(path.join(output, 'identity.json'), 'utf8'));
  assert.equal(identity.hiddenPartsSource, hiddenParts);
  assert.deepEqual(identity.hiddenPartsCanvas, [1254, 1254]);
  assert.equal(identity.hiddenPartsRgbaSha256, digest(hiddenOriginal));
  assert.equal(identity.hiddenPartsRgbaSha256, '1aaa0012bef0cfe7a9281cf426d07de44ad568f7c04acb2513d149157b5643cf');
});

test('independent source-over reconstruction has zero alpha or visible RGB differences', async () => {
  const psd = await decode();
  psd.imageData.data.fill(0);
  const actual = composeLayers(psd);
  assert.deepEqual(identityDifferences(actual), { alpha: 0, visibleRgb: 0 });
  const preview = await sharp(path.join(output, 'eagle-neutral.png')).ensureAlpha().raw().toBuffer();
  assert.deepEqual(identityDifferences(preview), { alpha: 0, visibleRgb: 0 });
});

test('native crown and feather contours are kept at their original coordinates', async () => {
  const psd = await decode();
  const face = psd.children.find(layer => layer.name === 'face');
  assert.equal(face.top, 84);
  const pixel = (x, y) => {
    const offset = ((y - face.top) * face.imageData.width + x - face.left) * 4;
    return Array.from(face.imageData.data.subarray(offset, offset + 4));
  };
  for (let y = 0; y < 125; y++) for (let x = 0; x < psd.width; x++) {
    const offset = (y * psd.width + x) * 4;
    if (original[offset + 3]) assert.deepEqual(pixel(x, y), Array.from(original.subarray(offset, offset + 4)), `${x},${y}`);
  }
  for (const [x, y] of [[151, 234], [185, 238], [186, 238], [188, 238], [224, 235]]) {
    const offset = (y * psd.width + x) * 4;
    assert.deepEqual(pixel(x, y), Array.from(original.subarray(offset, offset + 4)), `${x},${y}`);
  }
});

test('both shoulders and the feather collar retain substantial hidden overlap', async () => {
  const psd = await decode();
  const layers = Object.fromEntries(psd.children.map(layer => [layer.name, layer]));
  for (const [cx, cy, backingName] of [
    [130, 213, 'handwear-r'], [249, 213, 'handwear-l'], [186, 236, 'topwear'],
  ]) {
    assert.equal(original[(cy * 384 + cx) * 4 + 3], 255);
    assert.equal(layerPixel(layers.face, cx, cy)[3], 255);
    assert.ok(layerPixel(layers[backingName], cx, cy)[3] >= 240, `${backingName} anchor is uncovered`);
    let thickOverlap = 0;
    for (let y = cy - 3; y <= cy + 3; y++) for (let x = cx - 3; x <= cx + 3; x++) {
      const headAlpha = layerPixel(layers.face, x, y)[3];
      const backingAlpha = layerPixel(layers[backingName], x, y)[3];
      if (headAlpha >= 240 && backingAlpha >= 240) thickOverlap++;
      if (headAlpha >= 240)
        assert.ok(Math.max(backingAlpha, layerPixel(layers.topwear, x, y)[3]) >= 240,
          `Shoulder/collar backing hole at ${x},${y}`);
    }
    assert.ok(thickOverlap >= 30, `${backingName} has only ${thickOverlap}/49 solid overlapping pixels`);
  }
});

test('both ankles retain shared opaque pixels above the foot cut', async () => {
  const psd = await decode();
  const body = psd.children.find(layer => layer.name === 'topwear');
  for (const [x, name] of [[161, 'footwear-r'], [221, 'footwear-l']]) {
    const foot = psd.children.find(layer => layer.name === name);
    for (let y = 305; y < 309; y++) {
      const offset = (y * 384 + x) * 4;
      const sourcePixel = Array.from(original.subarray(offset, offset + 4));
      assert.equal(sourcePixel[3], 255);
      assert.deepEqual(layerPixel(foot, x, y), sourcePixel, `${name} underlap at ${x},${y}`);
      assert.deepEqual(layerPixel(body, x, y), sourcePixel, `torso underlap at ${x},${y}`);
    }
  }
});

test('eye cutouts exclude original beak pixels even inside their rectangular ROI', async () => {
  const psd = await decode();
  const mouth = psd.children.find(layer => layer.name === 'mouth close');
  const eyes = psd.children.filter(layer => layer.name.startsWith('irides-'));
  const yellowSamples = [[178, 162], [180, 160], [216, 160], [218, 160], [220, 162]];
  const edgeSamples = [[175, 162], [220, 160], [222, 162]];
  for (const [x, y] of yellowSamples) {
    const offset = (y * 384 + x) * 4;
    assert.ok(original[offset] > 240 && original[offset + 1] > 140 && original[offset + 2] < 20,
      `Reference sample ${x},${y} is not in the yellow beak`);
  }
  for (const [x, y] of [...yellowSamples, ...edgeSamples]) {
    const offset = (y * 384 + x) * 4;
    assert.deepEqual(layerPixel(mouth, x, y), Array.from(original.subarray(offset, offset + 4)), `${x},${y}`);
    for (const eye of eyes) assert.equal(layerPixel(eye, x, y)[3], 0, `${eye.name} cuts into beak at ${x},${y}`);
  }
  for (const eye of eyes)
    for (let i = 3; i < eye.imageData.data.length; i += 4) eye.imageData.data[i] = 0;
  const eyesHidden = composeLayers(psd);
  for (const [x, y] of [...yellowSamples, ...edgeSamples]) {
    const offset = (y * 384 + x) * 4;
    assert.deepEqual(eyesHidden.subarray(offset, offset + 4), original.subarray(offset, offset + 4),
      `Hiding eye layers changed the beak at ${x},${y}`);
  }
});

test('layer-pixel corruption is caught even when the cached PSD composite is unchanged', async () => {
  const psd = await decode();
  const eye = psd.children.find(layer => layer.name === 'irides-r');
  const offset = ((151 - eye.top) * eye.imageData.width + 166 - eye.left) * 4;
  assert.equal(eye.imageData.data[offset + 3], 255);
  eye.imageData.data[offset] ^= 1;
  assert.deepEqual(identityDifferences(composeLayers(psd)), { alpha: 0, visibleRgb: 1 });
});

test('an existing canonical output is refused without changing any file', async () => {
  const files = await readdir(output);
  const before = await Promise.all(files.map(file => readFile(path.join(output, file))));
  const result = run(source, output);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Output directory must be empty/);
  assert.deepEqual(await readdir(output), files);
  assert.deepEqual(await Promise.all(files.map(file => readFile(path.join(output, file)))), before);
  assert.equal(digest(await readFile(source)), sourceFileHash);
});

test('a same-sized reference with one changed pixel fails the reference-hash gate', async () => {
  const changed = Buffer.from(original);
  changed[(100 * 384 + 192) * 4] ^= 1;
  const input = path.join(evidence, 'changed-reference.png');
  await writeFile(input, await sharp(changed, { raw: { width: 384, height: 346, channels: 4 } }).png().toBuffer());
  const destination = path.join(evidence, 'changed-reference');
  const result = run(input, destination);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Canonical pixels changed; review masks/);
  assert.deepEqual(await readdir(destination), []);
  assert.equal(digest(await readFile(source)), sourceFileHash);
});

test('a differently sized reference is rejected before publishing model files', async () => {
  const destination = path.join(evidence, 'wrong-size');
  const result = run(path.join(repo, 'DuckDeskPet/Assets/mascot.png'), destination);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /original 384 x 346 neutral/);
  assert.deepEqual(await readdir(destination), []);
});

test('a same-sized hidden sheet with one changed interior pixel fails the backing-hash gate', async () => {
  const changed = Buffer.from(hiddenOriginal);
  const offset = (250 * 1254 + 500) * 4;
  assert.ok(changed[offset + 3] > 0);
  changed[offset] ^= 1;
  const input = path.join(evidence, 'changed-backing.png');
  await writeFile(input, await sharp(changed, { raw: { width: 1254, height: 1254, channels: 4 } }).png().toBuffer());
  const destination = path.join(evidence, 'changed-backing');
  const result = run(source, destination, input);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Hidden backing pixels changed; review occluded geometry/);
  assert.deepEqual(await readdir(destination), []);
  assert.equal(digest(await readFile(source)), sourceFileHash);
  assert.equal(digest(await readFile(hiddenParts)), hiddenFileHash);
});

test('a wrongly sized hidden sheet is rejected before publishing model files', async () => {
  const destination = path.join(evidence, 'wrong-backing-size');
  const result = run(source, destination, source);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Hidden backing requires the reviewed 1254 x 1254/);
  assert.deepEqual(await readdir(destination), []);
});
