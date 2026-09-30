import fs from 'node:fs/promises';
import path from 'node:path';
import sharp from 'sharp';

const [evidenceArgument, outputArgument, outfit = 'outfit.default'] = process.argv.slice(2);
if (!outputArgument) throw new Error('Usage: node review-contact-sheet.mjs evidence-directory fresh-output-directory [outfit.id]');
const evidence = await fs.realpath(evidenceArgument), output = path.resolve(outputArgument);
console.log(`Own-render evidence: ${evidence}\nReview sheet target: ${output}`);
await fs.mkdir(output, { recursive: false });
const groups = [['Yawn', 'Shy', 'Eat', 'Tea'],
  ['WorkEnter', 'WorkLoop', 'WorkToBusy', 'BusyLoop', 'WorkExit', 'BusyExit'], ['HungryEnter', 'HungryLoop', 'HungryExit', 'Annoyed']];
for (let group = 0; group < groups.length; group++) {
  const width = 224, height = 224, marks = [0, .2, .4, .6, .8, 1], layers = [];
  for (let row = 0; row < groups[group].length; row++) for (let column = 0; column < marks.length; column++) {
    const name = groups[group][row], progress = marks[column].toFixed(2);
    const file = path.join(evidence, `${outfit}-${name}-${progress}.png`);
    const pixels = await sharp(file).resize(width, height - 22, { fit: 'contain', background: '#f5f0e5' }).flatten({ background: '#f5f0e5' }).png().toBuffer();
    const label = Buffer.from(`<svg width="${width}" height="22"><rect width="100%" height="100%" fill="#e4dccb"/><text x="8" y="16" font-size="12" font-family="sans-serif" fill="#3b3024">${name} · ${progress}</text></svg>`);
    layers.push({ input: label, left: column * width, top: row * height });
    layers.push({ input: pixels, left: column * width, top: row * height + 22 });
  }
  await sharp({ create: { width: width * marks.length, height: height * groups[group].length, channels: 4, background: '#f5f0e5' } })
    .composite(layers).png().toFile(path.join(output, `review-${group + 1}.png`));
}
