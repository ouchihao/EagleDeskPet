import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { readPsd, initializeCanvas } from 'ag-psd';
import sharp from 'sharp';

initializeCanvas(() => { throw new Error('Raw test only'); },
  (width, height) => ({ width, height, data: new Uint8ClampedArray(width * height * 4) }));
const root = path.resolve(import.meta.dirname, '../..');
const output = await fs.mkdtemp(path.join(os.tmpdir(), 'eagle-action-source-'));
const run = () => spawnSync(process.execPath, [path.join(import.meta.dirname, 'pack-action-eagle.mjs'), output], { cwd: root, encoding: 'utf8' });
const source = path.join(root, 'DuckDeskPet/Assets/mascot-animated-neutral.png');
const before = await fs.readFile(source);
const result = run();
assert.equal(result.status, 0, result.stderr || result.stdout);
const manifest = JSON.parse(await fs.readFile(path.join(output, 'source-manifest.json')));
const variants = new Map();
for (const variant of manifest.variants) {
  const bytes = await fs.readFile(path.join(output, variant.id.split('.')[1], 'eagle.psd'));
  variants.set(variant.id, { bytes, psd: readPsd(bytes, { useImageData: true, skipThumbnail: true }), variant });
}
console.log('Evidence: ' + output);
test('all six outfit sources share the native canvas and real articulated body / hand / beak parts', () => {
  assert.deepEqual([...variants.keys()], ['outfit.default', 'outfit.office', 'outfit.hoodie', 'outfit.ox', 'outfit.hero', 'outfit.astronaut']);
  for (const { psd, bytes, variant } of variants.values()) {
    assert.equal(psd.width, 384); assert.equal(psd.height, 346);
    assert.equal(crypto.createHash('sha256').update(bytes).digest('hex'), variant.psdSha256);
    for (const name of ['handwear-l', 'handwear-r', 'forearm-l', 'forearm-r', 'beak-upper', 'beak-lower', 'beak-inner', 'rps-rock', 'rps-paper', 'rps-scissors', 'busy-eye-l', 'busy-eye-r'])
      assert.ok(psd.children.some(layer => layer.name === name), name);
  }
});
test('new mouth and hand artwork starts at zero opacity without being hidden or empty', () => {
  for (const { psd } of variants.values()) for (const child of psd.children.filter(x => /^(beak-|rps-|busy-eye-)/.test(x.name))) {
    assert.equal(child.opacity, 0); assert.notEqual(child.hidden, true);
    assert.ok(child.imageData.data.some((n, index) => index % 4 === 3 && n > 200));
  }
});
test('upper-arm / forearm overlap is opaque-only and no lower arm sits behind the face', () => {
  for (const { psd } of variants.values()) for (const side of ['l', 'r']) {
    const upper = psd.children.find(x => x.name === 'handwear-' + side), lower = psd.children.find(x => x.name === 'forearm-' + side);
    assert.equal(upper.top + upper.imageData.height, 254); assert.equal(lower.top, 242);
    assert.ok(psd.children.indexOf(lower) > psd.children.findIndex(x => x.name === 'face'));
    assert.ok(psd.children.indexOf(lower) > psd.children.findIndex(x => x.name === 'beak-upper'));
    for (let y = 0; y < 12; y++) for (let x = 0; x < lower.imageData.width; x++) {
      const alpha = lower.imageData.data[(y * lower.imageData.width + x) * 4 + 3];
      assert.ok(alpha === 0 || alpha === 255);
    }
  }
});
test('the stationary torso does not retain the moving wing silhouette', () => {
  for (const { psd } of variants.values()) {
    const torso = psd.children.find(x => x.name === 'topwear');
    const alpha = (x, y) => torso.imageData.data[((y - torso.top) * torso.imageData.width + x - torso.left) * 4 + 3];
    assert.equal(alpha(117, 260), 0);
    assert.equal(alpha(267, 260), 0);
    assert.equal(alpha(192, 260), 255);
    assert.ok(alpha(140, 212) >= 254);
  }
});
test('gesture palms have an open wrist with hidden overlap, not a sealed floating hand icon', () => {
  const { psd } = variants.get('outfit.default');
  for (const hand of psd.children.filter(x => x.name.startsWith('rps-'))) {
    const { width, height, data } = hand.imageData;
    assert.equal(hand.top + height, 280);
    assert.deepEqual(data.subarray((271 - hand.top) * width * 4, (272 - hand.top) * width * 4), data.subarray((height - 1) * width * 4));
    assert.ok(data[((height - 1) * width + 140 - hand.left) * 4 + 3] > 200);
  }
});
test('default neutral preserves every visible original pixel after authoring', async () => {
  const expected = await sharp(before).ensureAlpha().raw().toBuffer();
  const actual = await sharp(path.join(output, 'default/neutral.png')).ensureAlpha().raw().toBuffer();
  for (let p = 0; p < expected.length; p += 4) {
    assert.equal(actual[p + 3], expected[p + 3]);
    if (expected[p + 3]) assert.deepEqual(actual.subarray(p, p + 3), expected.subarray(p, p + 3));
  }
  assert.deepEqual(await fs.readFile(source), before);
});
test('capitalist glasses and complete outfit head / back pieces are real separate layers', () => {
  assert.ok(variants.get('outfit.office').psd.children.some(x => x.name === 'outfit-glasses'));
  for (const id of ['outfit.ox', 'outfit.hero', 'outfit.astronaut']) {
    const layers = variants.get(id).psd.children;
    assert.equal(layers[0].name, 'outfit-back');
    assert.ok(layers.some(x => x.name === 'outfit-head'));
  }
});
test('three bounded scene props are present with a transparent background', async () => {
  for (const key of ['bowlFull', 'bowlEmpty', 'tea']) {
    const entry = manifest.extras[key];
    assert.equal(entry.rect.length, 4);
    const { data } = await sharp(path.join(output, entry.url)).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    assert.ok(data.some((n, index) => index % 4 === 3 && n === 0));
    assert.ok(data.some((n, index) => index % 4 === 3 && n === 255));
  }
});
test('rebuilding refuses an occupied output and preserves authored PSDs', async () => {
  const previous = await fs.readFile(path.join(output, 'default/eagle.psd'));
  const rerun = run(); assert.notEqual(rerun.status, 0);
  assert.match(rerun.stderr, /Output directory must be empty/);
  assert.deepEqual(await fs.readFile(path.join(output, 'default/eagle.psd')), previous);
});
