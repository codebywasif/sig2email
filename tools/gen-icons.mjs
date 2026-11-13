// Generates the built-in social icon pack as PNG files (email clients do not render SVG).
// Simple original glyphs on coloured tiles – no third-party artwork. Usage: node tools/gen-icons.mjs
import { deflateSync } from 'node:zlib';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const SIZE = 96; // displayed at 16–48 px, so this stays crisp on high-DPI screens
const SS = 3;    // supersampling factor for anti-aliasing
const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'Sig2Email.Api', 'wwwroot', 'icons');

// 5x7 bitmap glyphs
const FONT = {
  i: ['..#..', '.....', '.##..', '..#..', '..#..', '..#..', '.###.'],
  n: ['.....', '.....', '#.##.', '##..#', '#...#', '#...#', '#...#'],
  X: ['#...#', '#...#', '.#.#.', '..#..', '.#.#.', '#...#', '#...#'],
  f: ['..##.', '.#...', '.#...', '###..', '.#...', '.#...', '.#...'],
  w: ['.....', '.....', '#...#', '#...#', '#.#.#', '#.#.#', '.#.#.'],
};

const ICONS = {
  linkedin: { color: [10, 102, 194], draw: (p) => text(p, 'in', 0.5, 0.5, 0.085) },
  x: { color: [17, 17, 17], draw: (p) => text(p, 'X', 0.5, 0.5, 0.09) },
  facebook: { color: [24, 119, 242], draw: (p) => text(p, 'f', 0.5, 0.5, 0.095) },
  web: { color: [15, 108, 189], draw: (p) => text(p, 'www', 0.5, 0.5, 0.05) },
  // camera outline: rounded square ring, lens ring, flash dot
  instagram: {
    color: [214, 41, 118],
    draw: (p) => (ring(p, 0.5, 0.5, 0.2, 0.045) || roundRectRing(p, 0.24, 0.24, 0.52, 0.52, 0.13, 0.05) || disc(p, 0.665, 0.335, 0.038)),
  },
  // play triangle
  youtube: { color: [255, 0, 0], draw: ([x, y]) => x >= 0.4 && x <= 0.68 && Math.abs(y - 0.5) <= (0.68 - x) * 0.62 },
};

function text([x, y], str, cx, cy, cell) {
  const width = str.length * 6 - 1;
  const gx = Math.floor((x - (cx - (width * cell) / 2)) / cell);
  const gy = Math.floor((y - (cy - (7 * cell) / 2)) / cell);
  if (gx < 0 || gy < 0 || gy >= 7 || gx >= width || gx % 6 === 5) return false;
  return FONT[str[Math.floor(gx / 6)]][gy][gx % 6] === '#';
}
const disc = ([x, y], cx, cy, r) => (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
const ring = (p, cx, cy, r, w) => disc(p, cx, cy, r) && !disc(p, cx, cy, r - w);
function roundRect([x, y], left, top, w, h, r) {
  if (x < left || y < top || x > left + w || y > top + h) return false;
  const dx = Math.max(left + r - x, 0, x - (left + w - r));
  const dy = Math.max(top + r - y, 0, y - (top + h - r));
  return dx * dx + dy * dy <= r * r;
}
const roundRectRing = (p, l, t, w, h, r, s) => roundRect(p, l, t, w, h, r) && !roundRect(p, l + s, t + s, w - 2 * s, h - 2 * s, Math.max(r - s, 0.01));

function render({ color, draw }) {
  const px = Buffer.alloc(SIZE * SIZE * 4);
  for (let y = 0; y < SIZE; y++) {
    for (let x = 0; x < SIZE; x++) {
      let tile = 0, glyph = 0;
      for (let sy = 0; sy < SS; sy++) {
        for (let sx = 0; sx < SS; sx++) {
          const p = [(x + (sx + 0.5) / SS) / SIZE, (y + (sy + 0.5) / SS) / SIZE];
          if (roundRect(p, 0, 0, 1, 1, 0.2)) { tile++; if (draw(p)) glyph++; }
        }
      }
      const n = SS * SS, i = (y * SIZE + x) * 4, g = tile ? glyph / tile : 0;
      for (let c = 0; c < 3; c++) px[i + c] = Math.round(color[c] * (1 - g) + 255 * g);
      px[i + 3] = Math.round((255 * tile) / n);
    }
  }
  return png(px);
}

function png(rgba) {
  const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = (buf) => { let c = 0xffffffff; for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type), data]);
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const sum = Buffer.alloc(4); sum.writeUInt32BE(crc(body));
    return Buffer.concat([len, body, sum]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(SIZE, 0); ihdr.writeUInt32BE(SIZE, 4); ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
  const raw = Buffer.alloc((SIZE * 4 + 1) * SIZE);
  for (let y = 0; y < SIZE; y++) rgba.copy(raw, y * (SIZE * 4 + 1) + 1, y * SIZE * 4, (y + 1) * SIZE * 4);
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}

mkdirSync(out, { recursive: true });
for (const [name, icon] of Object.entries(ICONS)) {
  writeFileSync(join(out, `${name}.png`), render(icon));
  console.log(`wrote ${name}.png`);
}
