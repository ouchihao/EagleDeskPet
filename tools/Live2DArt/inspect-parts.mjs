import sharp from 'sharp';

const path = process.argv[2];
if (!path) throw new Error('Usage: node inspect-parts.mjs <parts.png>');
const { data, info } = await sharp(path).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
const seen = new Uint8Array(info.width * info.height);
const queue = new Int32Array(seen.length);
const parts = [];
for (let index = 0; index < seen.length; index++) {
  if (seen[index] || data[index * 4 + 3] < 32) continue;
  let begin = 0, end = 1, count = 0;
  let left = info.width, top = info.height, right = 0, bottom = 0;
  queue[0] = index;
  seen[index] = 1;
  while (begin < end) {
    const pixel = queue[begin++];
    const x = pixel % info.width, y = Math.floor(pixel / info.width);
    count++;
    left = Math.min(left, x); right = Math.max(right, x);
    top = Math.min(top, y); bottom = Math.max(bottom, y);
    const neighbors = [];
    if (x) neighbors.push(pixel - 1);
    if (x + 1 < info.width) neighbors.push(pixel + 1);
    if (y) neighbors.push(pixel - info.width);
    if (y + 1 < info.height) neighbors.push(pixel + info.width);
    for (const next of neighbors) {
      if (seen[next] || data[next * 4 + 3] < 32) continue;
      seen[next] = 1;
      queue[end++] = next;
    }
  }
  if (count >= 100) parts.push({ pixels: count, left, top, width: right - left + 1, height: bottom - top + 1 });
}
console.log(JSON.stringify({ width: info.width, height: info.height, parts: parts.sort((a, b) => b.pixels - a.pixels) }, null, 2));
