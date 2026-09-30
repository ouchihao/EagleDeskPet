import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';

const [sourceArgument, modelArgument, outputArgument] = process.argv.slice(2);
if (!outputArgument) throw new Error('Usage: node package-action-models.mjs source-directory model-directory fresh-output-directory');
const source = await fs.realpath(sourceArgument), models = await fs.realpath(modelArgument), output = path.resolve(outputArgument);
console.log(`Authored sources: ${source}\nAuthored models: ${models}\nExact package target: ${output}`);
try { await fs.access(output); throw new Error('Refusing an existing package output'); }
catch (error) { if (error.code !== 'ENOENT') throw error; }
const manifest = JSON.parse(await fs.readFile(path.join(source, 'source-manifest.json'), 'utf8'));
const requiredOutfits = new Set(['outfit.default', 'outfit.office', 'outfit.hoodie', 'outfit.ox', 'outfit.hero', 'outfit.astronaut']);
if (manifest.variants.length !== requiredOutfits.size ||
    new Set(manifest.variants.map(variant => variant.id)).size !== requiredOutfits.size ||
    manifest.variants.some(variant => !requiredOutfits.has(variant.id)))
  throw new Error('Exactly the six first-party outfit variants are required');
const outfits = {}, hashes = {};
const allowedModel = /\.(json|moc3|cmo3|png)$/;
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
async function copyTree(from, to, allowed = () => true) {
  await fs.mkdir(to, { recursive: true });
  for (const entry of await fs.readdir(from, { withFileTypes: true })) {
    const origin = path.join(from, entry.name), target = path.join(to, entry.name);
    if (entry.isSymbolicLink()) throw new Error('Source links are not allowed');
    if (entry.isDirectory()) await copyTree(origin, target, allowed);
    else if (allowed(entry.name)) {
      await fs.copyFile(origin, target);
      hashes[path.relative(output, target).replaceAll('\\', '/')] = sha(await fs.readFile(target));
    }
  }
}
for (const variant of manifest.variants) {
  const name = variant.id.slice('outfit.'.length), directory = path.join(models, name);
  const sourceBytes = await fs.readFile(path.join(source, name, 'eagle.psd'));
  if (sha(sourceBytes) !== variant.psdSha256) throw new Error('Source hash mismatch: ' + name);
  const validation = JSON.parse(await fs.readFile(path.join(directory, 'authoring-validation.json'), 'utf8'));
  if (!validation.authoringAssertionsPassed || !validation.fullBodyRig || validation.inputSha256 !== variant.psdSha256)
    throw new Error('Model was not authored from this reviewed full-body source: ' + name);
  for (const file of validation.files) {
    if (file.path.split('/').some(x => !x || x === '..' || x === '.') || /[:\\]/.test(file.path)) throw new Error('Unsafe authoring report path');
    if (sha(await fs.readFile(path.join(directory, file.path))) !== file.sha256) throw new Error('Authored file hash mismatch: ' + file.path);
  }
  const definition = JSON.parse(await fs.readFile(path.join(directory, 'eagle.model3.json'), 'utf8'));
  const bindings = JSON.parse(await fs.readFile(path.join(directory, 'action-bindings.json'), 'utf8'));
  for (const id of ['ParamBeakOpen', 'ParamWingLLift', 'ParamWingRLift', 'ParamWingLFold', 'ParamWingRFold', 'ParamBusyEyes'])
    if (!bindings.parameters.some(p => p.id === id)) throw new Error('Missing full-action parameter ' + id);
  if (bindings.foregroundDrawableIds.length !== 2) throw new Error('Incomplete wing / desk occlusion binding');
  if (bindings.parameters.some(p => p.id.startsWith('ParamHand'))) throw new Error('Removed guessing-game parameters are still present');
  for (const group of ['handAttachments', 'eyeAttachments']) for (const side of ['l', 'r']) {
    const attachment = bindings[group]?.[side];
    if (!attachment?.drawableId || !Array.isArray(attachment.source) || attachment.source.length !== 2 ||
        !attachment.source.every(Number.isFinite)) throw new Error('Missing authored ' + group + '/' + side);
  }
  for (const resource of [definition.FileReferences.Moc, ...definition.FileReferences.Textures]) {
    if (resource.split('/').some(x => !x || x === '..' || x === '.') || /[:\\]/.test(resource)) throw new Error('Unsafe model asset');
    await fs.access(path.join(directory, resource));
  }
  const relative = name === 'default' ? '' : 'outfits/' + name;
  outfits[variant.id] = { modelUrl: '/model/' + (relative ? relative + '/' : '') + 'eagle.model3.json' };
  await copyTree(directory, path.join(output, 'Model', relative), name => allowedModel.test(name));
  await copyTree(path.join(source, name), path.join(output, 'Source', name));
}
if (Object.keys(outfits).length !== 6) throw new Error('All six outfits are required');
await copyTree(path.join(source, 'props'), path.join(output, 'Model', 'props'));
await fs.copyFile(path.join(source, 'source-manifest.json'), path.join(output, 'Source', 'source-manifest.json'));
hashes['Source/source-manifest.json'] = sha(await fs.readFile(path.join(output, 'Source', 'source-manifest.json')));
const extras = Object.fromEntries(Object.entries(manifest.extras).map(([id, extra]) => [id, { ...extra, url: '/model/' + extra.url }]));
const runtime = { schemaVersion: 1, outfits, extras, defaultEquipment: { desk: 'desk.default', computer: 'computer.default', outfit: 'outfit.default' } };
await fs.writeFile(path.join(output, 'Model', 'runtime-manifest.json'), JSON.stringify(runtime, null, 2) + '\n');
hashes['Model/runtime-manifest.json'] = sha(await fs.readFile(path.join(output, 'Model', 'runtime-manifest.json')));
await fs.writeFile(path.join(output, 'package-manifest.json'), JSON.stringify({ schemaVersion: 1, authoring: 'Original eagle layers + full-body parameter rig', neutralVisibleDifferences: manifest.defaultNeutralVisibleDifferences, files: hashes, runtimeRedistributed: false, acceptance: 'See docs/LIVE2D-FULL-ACTIONS.md; authoring success alone is not visual acceptance' }, null, 2) + '\n');
console.log('Six first-party source/model variants packaged; no official Core or third-party authoring tool copied');
