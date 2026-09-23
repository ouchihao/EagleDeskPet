import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, readdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const directory = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(directory, '../..');
const source = path.join(repo, 'DuckDeskPet/Assets/Live2DSource/eagle-parts-v1.png');
const expected = path.join(repo, 'DuckDeskPet/Assets/Live2DSource/eagle.psd');
const evidence = await mkdtemp(path.join(tmpdir(), 'eagle-psd-test-'));
console.log(`Evidence: ${evidence}`);
const run = (image, output) => spawnSync(process.execPath, [path.join(directory, 'pack-eagle.mjs'), image, output], { encoding: 'utf8' });

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
