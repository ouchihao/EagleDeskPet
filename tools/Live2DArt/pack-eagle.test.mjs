import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, readdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readPsd, initializeCanvas } from 'ag-psd';

initializeCanvas(() => { throw new Error('Tests use raw RGBA, not Canvas'); },
  (width, height) => ({ width, height, data: new Uint8ClampedArray(width * height * 4) }));

const directory = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(directory, '../..');
const source = path.join(repo, 'DuckDeskPet/Assets/Live2DSource/eagle-parts-v1.png');
const expected = path.join(repo, 'DuckDeskPet/Assets/Live2DSource/eagle.psd');
const compactSource = path.join(repo, 'DuckDeskPet/Assets/Live2DSource/eagle-parts-v2.png');
const compactLayoutPath = path.join(directory, 'eagle-compact-layout.json');
const compactLayout = JSON.parse(await readFile(compactLayoutPath, 'utf8'));
const evidence = await mkdtemp(path.join(tmpdir(), 'eagle-psd-test-'));
console.log(`Evidence: ${evidence}`);
const run = (image, output, layout) => spawnSync(process.execPath,
  [path.join(directory, 'pack-eagle.mjs'), image, output, ...(layout ? [layout] : [])], { encoding: 'utf8' });
const decode = async output => readPsd(await readFile(path.join(output, 'eagle.psd')),
  { useImageData: true, skipThumbnail: true });
const placement = layer => [layer.left, layer.top, layer.imageData.width, layer.imageData.height];
const writeLayout = async (name, change) => {
  const layout = structuredClone(compactLayout);
  change(layout.layout);
  const file = path.join(evidence, `${name}.json`);
  await writeFile(file, JSON.stringify(layout));
  return file;
};

test('rebuild retains the reviewed PSD byte for byte and all 15 layers', async () => {
  const output = path.join(evidence, 'valid');
  const result = run(source, output);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Verified 15 separate RGBA layers/);
  assert.deepEqual(await readFile(path.join(output, 'eagle.psd')), await readFile(expected));
});

test('existing output is refused without changing it', async () => {
  const output = path.join(evidence, 'valid');
  const before = await readFile(path.join(output, 'eagle.psd'));
  const result = run(source, output);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Output directory must be empty/);
  assert.deepEqual(await readFile(path.join(output, 'eagle.psd')), before);
});

test('a differently sized source cannot silently reuse the measured crop layout', async () => {
  const output = path.join(evidence, 'wrong-size');
  const result = run(path.join(repo, 'DuckDeskPet/Assets/mascot-animated-neutral.png'), output);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /1254 x 1254/);
  assert.deepEqual(await readdir(output), []);
});

test('custom layout preserves 15 layers and every configured placement', async () => {
  const before = await readFile(compactSource);
  const output = path.join(evidence, 'compact');
  const result = run(compactSource, output, compactLayoutPath);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Verified 15 separate RGBA layers/);
  const psd = await decode(output);
  assert.deepEqual([psd.width, psd.height], [512, 512]);
  assert.deepEqual(psd.children.map(layer => layer.name), [
    'footwear-r', 'footwear-l', 'topwear', 'handwear-r', 'handwear-l', 'face',
    'facedetail-r', 'facedetail-l', 'eyebrow-r', 'eyebrow-l', 'irides-r', 'irides-l',
    'mouth', 'mouth lower', 'mouth close',
  ]);
  const layers = Object.fromEntries(psd.children.map(layer => [layer.name, layer]));
  for (const [name, , geometry] of compactLayout.layout)
    assert.deepEqual(placement(layers[name]), geometry, name);
  assert.deepEqual(await readFile(compactSource), before);
});

test('changing one configured layer leaves every other layer unchanged', async () => {
  const modifiedLayout = await writeLayout('one-layer-change', layout => {
    const face = layout.find(layer => layer[0] === 'face')[2];
    face[0] += 1;
    face[2] -= 2;
  });
  const output = path.join(evidence, 'one-layer-change');
  const result = run(compactSource, output, modifiedLayout);
  assert.equal(result.status, 0, result.stderr);
  const before = await decode(output);
  const after = await decode(path.join(evidence, 'compact'));
  assert.notDeepEqual(before.children.find(layer => layer.name === 'face'),
    after.children.find(layer => layer.name === 'face'));
  for (let i = 0; i < before.children.length; i++) {
    if (before.children[i].name === 'face') continue;
    assert.deepEqual(after.children[i], before.children[i], before.children[i].name);
  }
});

test('compact output is refused without changing existing files', async () => {
  const output = path.join(evidence, 'compact');
  const files = await readdir(output);
  const before = await Promise.all(files.map(file => readFile(path.join(output, file))));
  const result = run(compactSource, output, compactLayoutPath);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Output directory must be empty/);
  assert.deepEqual(await readdir(output), files);
  assert.deepEqual(await Promise.all(files.map(file => readFile(path.join(output, file)))), before);
});

test('duplicate compact layer names cannot silently replace a model part', async () => {
  const layout = await writeLayout('duplicate-layer', layers => { layers[1][0] = layers[0][0]; });
  const output = path.join(evidence, 'duplicate-layer');
  const result = run(compactSource, output, layout);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Expected 15 uniquely named model layers/);
  assert.deepEqual(await readdir(output), []);
});

for (const [name, geometry] of Object.entries({
  'negative-position': [-1, 49, 267, 255],
  'outside-canvas': [300, 49, 267, 255],
  'zero-width': [124, 49, 0, 255],
  'fractional-position': [124.5, 49, 267, 255],
})) test(`compact layout rejects ${name} without publishing a partial model`, async () => {
  const layout = await writeLayout(name, layers => { layers.find(layer => layer[0] === 'face')[2] = geometry; });
  const output = path.join(evidence, name);
  const result = run(compactSource, output, layout);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Invalid layer geometry: face/);
  assert.deepEqual(await readdir(output), []);
});
