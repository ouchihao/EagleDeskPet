import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';

const [sourceArg, outputArg, javaArg, jarsArg] = process.argv.slice(2);
if (!sourceArg || !outputArg || !javaArg || !jarsArg)
  throw new Error('Usage: node export-action-models.mjs source-directory fresh-output-directory java-executable psd2live-jars-directory');
const source = fs.realpathSync(sourceArg), output = path.resolve(outputArg);
const java = fs.realpathSync(javaArg), jars = fs.realpathSync(jarsArg);
if (fs.existsSync(output)) throw new Error(`Refusing to overwrite ${output}`);
const tools = path.dirname(fileURLToPath(import.meta.url));
const base = JSON.parse(fs.readFileSync(path.join(tools, 'eagle-full-rig.json'), 'utf8'));
const variants = ['default', 'office', 'hoodie', 'ox', 'hero', 'astronaut'];
const accessories = {
  'outfit-head': {semanticTag: 'HEADWEAR', side: 'NONE', parentDeformer: 'DeformHeadContainer'},
  'outfit-glasses': {semanticTag: 'HEADWEAR', side: 'NONE', parentDeformer: 'DeformHeadContainer'},
  'outfit-back': {semanticTag: 'TOPWEAR', side: 'NONE', parentDeformer: 'DeformBodyZBreath'},
};
const inputs = variants.map(variant => {
  const dir = path.join(source, variant);
  const manifest = JSON.parse(fs.readFileSync(path.join(dir, 'source.json'), 'utf8'));
  const psd = fs.realpathSync(path.join(dir, 'eagle.psd'));
  const profile = structuredClone(base);
  profile.expectedSourceLayers = manifest.layers.length;
  for (const layer of manifest.layers.filter(layer => layer.name.startsWith('outfit-'))) {
    if (!accessories[layer.name]) throw new Error(`Unreviewed outfit layer: ${layer.name}`);
    profile.layers.push({name: layer.name, ...accessories[layer.name]});
  }
  return {variant, psd, profile};
});
console.log(`Source: ${source}\nOutput: ${output}\nJava: ${java}\nAuthoring jars: ${jars}`);
fs.mkdirSync(path.join(output, 'profiles'), {recursive: true});
for (const input of inputs) {
  const profile = path.join(output, 'profiles', `${input.variant}.json`);
  fs.writeFileSync(profile, `${JSON.stringify(input.profile, null, 2)}\n`);
  execFileSync(java, ['-Xmx4g', '-Djava.awt.headless=true', '-Dfile.encoding=UTF-8', '--class-path', path.join(jars, '*'),
    path.join(tools, 'ExportPsdModel.java'), input.psd, path.join(output, input.variant), profile], {stdio: 'inherit'});
}
console.log('All six full-body variants authored; official runtime and visible action verification remain required');
