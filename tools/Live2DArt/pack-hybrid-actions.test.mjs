import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import sharp from 'sharp';
import { readPsd } from 'ag-psd';
import { WIDTH, HEIGHT, readZip, fitUv, correspondence, affinePoint, flatten, composeFrame, differences, materialRegion, selectedVariants } from './pack-hybrid-actions.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const zip = readZip(await fs.readFile(path.join(root, 'DuckDeskPet/Assets/OutfitBindings/Default-v1.zip')));
const bindings = JSON.parse(zip.get('bindings.json')), binding = bindings.frames.neutral;
const pixels = async file => sharp(await fs.readFile(path.join(root, file))).ensureAlpha().raw().toBuffer();
const neutral = await pixels('DuckDeskPet/' + binding.source);
const map = await sharp(zip.get(binding.map)).ensureAlpha().raw().toBuffer();
const fits = Array.from({ length: 6 }, (_, i) => i ? fitUv(map, i) : null);

test('all five material coordinate maps are exact identities at the standing pose', () => {
  for (let region = 1; region <= 5; region++) for (const [x, y] of [[111, 84], [140, 276], [244, 286], [192, 320]])
    assert.deepEqual(correspondence(fits[region], fits[region], x, y), [x, y]);
});

test('material transport is spatial, not an action-time opacity transition', () => {
  const source = [[2, 0, 5], [0, 3, -8]], target = [[2, 0, -15], [0, 3, -23]];
  assert.deepEqual(correspondence(source, target, 70, 60), [60, 55]);
});

test('accessory anchors follow affine head and body motion and preserve neutral scale', () => {
  assert.deepEqual(affinePoint([0, 0, 2, 0, 0, 3], [10, 20, 4, 0, 0, 6], 1, 1.5), [12, 23]);
  const actual = affinePoint(binding.head, binding.head, 145, 139);
  assert.ok(Math.abs(actual[0] - 145) < 1e-8 && Math.abs(actual[1] - 139) < 1e-8);
});

test('malformed or duplicate archive entries cannot silently produce bindings', () => {
  assert.throws(() => readZip(Buffer.alloc(128)), /Invalid anatomical archive/);
});

test('a complete runtime candidate cannot silently include known-failed complex outfits', () => {
  assert.deepEqual(selectedVariants(false), ['default', 'office', 'hoodie']);
  assert.throws(() => selectedVariants(false, ['astronaut']), /has not passed review/);
  assert.throws(() => selectedVariants(false, ['default', 'default']), /duplicate outfit/);
  assert.deepEqual(selectedVariants(true, ['astronaut']), ['astronaut']);
});

for (const name of ['default', 'office', 'hoodie', 'ox', 'hero', 'astronaut']) {
  test(name + ' material and accessory composition naturally reproduces approved Live2D source pixels', async () => {
    const base = 'DuckDeskPet/Assets/Live2DSource/FullActions/Source/' + name;
    const psd = readPsd(await fs.readFile(path.join(root, base, 'eagle.psd')), { useImageData: true, skipThumbnail: true });
    const body = flatten(psd.children.filter(layer => !layer.name.startsWith('outfit-')));
    const material = Array.from({ length: 6 }, () => new Float32Array(body.length));
    for (let p = 0; p < body.length; p += 4) {
      assert.equal(body[p + 3], neutral[p + 3]);
      const region = materialRegion(map, neutral, p, p / 4 % WIDTH, Math.floor(p / 4 / WIDTH), binding.body[0] + binding.body[2] / 2);
      for (let c = 0; c < 3; c++) material[region][p + c] = body[p + c] - neutral[p + c];
    }
    const actual = composeFrame(neutral, map, binding, binding, fits, {
      material, accessories: psd.children.filter(layer => layer.name.startsWith('outfit-')) });
    assert.equal(actual.length, WIDTH * HEIGHT * 4);
    assert.deepEqual(differences(actual, await pixels(base + '/neutral.png')), { alpha: 0, visible: 0, premultiplied: 0, maximum: 0 });
  });
}

test('original Yawn and Eat begin and end at the exact canonical visible pose', async () => {
  for (const clip of ['Yawn', 'Eat']) for (const frame of ['0000', '0120']) {
    const actual = await pixels(`DuckDeskPet/Assets/Animations/${clip}/frame-${frame}.png`);
    assert.deepEqual(differences(actual, neutral), { alpha: 0, visible: 0, premultiplied: 0, maximum: 0 });
  }
});

test('complete candidate has 726 real frames, original default bytes, and exact outfit seams',
  { skip: !process.env.EAGLE_HYBRID_CANDIDATE }, async () => {
    const directory = path.resolve(process.env.EAGLE_HYBRID_CANDIDATE);
    const runtimeBytes = await fs.readFile(path.join(directory, 'manifest.json'));
    assert.ok(runtimeBytes.length < 65536);
    const manifest = JSON.parse(runtimeBytes), report = JSON.parse(await fs.readFile(path.join(directory, 'authoring-validation.json')));
    assert.deepEqual(Object.keys(manifest.outfits), ['outfit.default', 'outfit.office', 'outfit.hoodie', 'outfit.ox', 'outfit.hero', 'outfit.astronaut']);
    for (const name of ['default', 'office', 'hoodie']) assert.deepEqual(manifest.outfits['outfit.' + name], { route: 'native-raster', directory: name });
    for (const name of ['ox', 'hero', 'astronaut']) assert.deepEqual(manifest.outfits['outfit.' + name], { route: 'live2d' });
    assert.equal(report.samplesOnly, false);
    let count = 0;
    for (const name of ['default', 'office', 'hoodie']) {
      const expected = await pixels(`DuckDeskPet/Assets/Live2DSource/FullActions/Source/${name}/neutral.png`);
      for (const clip of ['Yawn', 'Eat']) {
        assert.equal(report.evidence[name].curves[clip].length, 121);
        for (let frame = 0; frame <= 120; frame++) {
          const id = String(frame).padStart(4, '0'), bytes = await fs.readFile(path.join(directory, name, clip, `frame-${id}.png`));
          const { data, info } = await sharp(bytes).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
          assert.equal(info.width, WIDTH); assert.equal(info.height, HEIGHT); count++;
          if (name === 'default') {
            const original = await fs.readFile(path.join(root, `DuckDeskPet/Assets/Animations/${clip}/frame-${id}.png`));
            assert.equal(crypto.createHash('sha256').update(bytes).digest('hex'), crypto.createHash('sha256').update(original).digest('hex'));
          }
          if (frame === 0 || frame === 120) assert.deepEqual(differences(data, expected), { alpha: 0, visible: 0, premultiplied: 0, maximum: 0 });
        }
      }
    }
    assert.equal(count, 726);
    for (const excluded of ['ox', 'hero', 'astronaut'])
      await assert.rejects(fs.access(path.join(directory, excluded)), /ENOENT/);
  });
