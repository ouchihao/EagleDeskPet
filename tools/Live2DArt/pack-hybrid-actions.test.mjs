import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import sharp from 'sharp';
import { readPsd } from 'ag-psd';
import { WIDTH, HEIGHT, readZip, fitUv, correspondence, affinePoint, flatten, composeFrame, differences, materialRegion, selectedVariants, hoodieSleeveCorrection, authoredSleeveFits } from './pack-hybrid-actions.mjs';

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
  assert.deepEqual(selectedVariants(false), ['default']);
  assert.throws(() => selectedVariants(false, ['astronaut']), /has not passed review/);
  assert.throws(() => selectedVariants(false, ['default', 'default']), /duplicate outfit/);
  assert.deepEqual(selectedVariants(true, ['astronaut']), ['astronaut']);
});

test('the proven six-frame sleeve-axis reversal is repaired without touching other garments or tracks', async () => {
  const correction = await hoodieSleeveCorrection(zip, bindings);
  assert.deepEqual([...correction.frames.keys()], [46, 47, 48, 49, 50, 51]);
  const values = [];
  for (let frame = 45; frame <= 52; frame++) {
    const entry = bindings.frames['Yawn/' + String(frame).padStart(4, '0')];
    const original = fitUv(await sharp(zip.get(entry.map)).ensureAlpha().raw().toBuffer(), 3);
    const fit = authoredSleeveFits('hoodie', 'Yawn', frame, correction)?.get(3) ?? original;
    values.push(Math.atan2(fit[1][1], fit[1][0]));
    assert.equal(authoredSleeveFits('office', 'Yawn', frame, correction), undefined);
    assert.equal(authoredSleeveFits('hoodie', 'Eat', frame, correction), undefined);
  }
  for (let i = 1; i < values.length; i++) assert.ok(Math.abs(values[i] - values[i - 1]) < .01);
  for (const frame of [0, 1, 44, 45, 52, 53, 119, 120]) assert.equal(authoredSleeveFits('hoodie', 'Yawn', frame, correction), undefined);
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

test('complete candidate has all declared native frames, original default bytes, and exact outfit seams',
  { skip: !process.env.EAGLE_HYBRID_CANDIDATE }, async () => {
    const directory = path.resolve(process.env.EAGLE_HYBRID_CANDIDATE);
    const runtimeBytes = await fs.readFile(path.join(directory, 'manifest.json'));
    assert.ok(runtimeBytes.length < 65536);
    const manifest = JSON.parse(runtimeBytes), report = JSON.parse(await fs.readFile(path.join(directory, 'authoring-validation.json')));
    assert.deepEqual(Object.keys(manifest.outfits), ['outfit.default', 'outfit.office', 'outfit.hoodie', 'outfit.ox', 'outfit.hero', 'outfit.astronaut']);
    const selected = Object.entries(manifest.outfits).filter(([, entry]) => entry.route === 'native-raster').map(([id]) => id.slice('outfit.'.length));
    selectedVariants(false, selected);
    for (const name of selected) assert.deepEqual(manifest.outfits['outfit.' + name], { route: 'native-raster', directory: name });
    const excluded = ['default', 'office', 'hoodie', 'ox', 'hero', 'astronaut'].filter(name => !selected.includes(name));
    for (const name of excluded) assert.deepEqual(manifest.outfits['outfit.' + name], { route: 'live2d' });
    assert.equal(report.samplesOnly, false);
    let count = 0;
    for (const name of selected) {
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
    assert.equal(count, selected.length * 242);
    for (const name of excluded) await assert.rejects(fs.access(path.join(directory, name)), /ENOENT/);
    if (selected.includes('hoodie') && process.env.EAGLE_HYBRID_PREVIOUS_HOODIE) {
      const changed = [];
      for (const clip of ['Yawn', 'Eat']) for (let frame = 0; frame <= 120; frame++) {
        const file = `${clip}/frame-${String(frame).padStart(4, '0')}.png`;
        const current = await fs.readFile(path.join(directory, 'hoodie', file));
        const previous = await fs.readFile(path.join(path.resolve(process.env.EAGLE_HYBRID_PREVIOUS_HOODIE), 'hoodie', file));
        if (!current.equals(previous)) changed.push(file);
      }
      assert.deepEqual(changed, [46, 47, 48, 49, 50, 51].map(frame => `Yawn/frame-${String(frame).padStart(4, '0')}.png`));
    }
  });
