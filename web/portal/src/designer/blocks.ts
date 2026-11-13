// Block model of the visual designer and its compiler to email-safe (table based, inline styled) HTML.

export type Block =
  | { id: string; type: 'text'; html: string; showIf?: string }
  | { id: string; type: 'image'; src: string; alt: string; width: number; href?: string }
  | { id: string; type: 'photo'; size: number; round: boolean }
  | { id: string; type: 'divider'; color: string; thickness: number }
  | { id: string; type: 'spacer'; height: number }
  | { id: string; type: 'social'; size: number; items: { network: string; url: string }[] }
  | { id: string; type: 'button'; label: string; href: string; background: string; color: string }
  | { id: string; type: 'survey'; surveyId: string }
  | { id: string; type: 'qr'; size: number }
  | { id: string; type: 'columns'; left: Block[]; right: Block[]; gap: number; divider: boolean };

export type BlockType = Block['type'];

export interface Design {
  version: 1;
  fontFamily: string;
  fontSize: number;
  color: string;
  blocks: Block[];
}

export const NETWORKS = ['linkedin', 'x', 'facebook', 'instagram', 'youtube', 'web'] as const;
export const NETWORK_LABEL: Record<string, string> = { linkedin: 'LinkedIn', x: 'X', facebook: 'Facebook', instagram: 'Instagram', youtube: 'YouTube', web: 'Website' };

export const FONTS = ['Segoe UI, Arial, sans-serif', 'Arial, Helvetica, sans-serif', 'Verdana, Geneva, sans-serif', 'Georgia, serif', 'Tahoma, Geneva, sans-serif', 'Calibri, Arial, sans-serif'];

let counter = 0;
export const newId = () => `b${Date.now().toString(36)}${(counter++).toString(36)}`;

export const BLOCK_LABEL: Record<BlockType, string> = {
  text: 'Text', image: 'Image', photo: 'Profile photo', divider: 'Divider', spacer: 'Spacer', social: 'Social icons', button: 'Button',
  survey: 'Survey', qr: 'vCard QR code', columns: 'Two columns',
};

export function createBlock(type: BlockType): Block {
  const id = newId();
  switch (type) {
    case 'text': return { id, type, html: '<b>{DisplayName}</b><br>{JobTitle}' };
    case 'image': return { id, type, src: '', alt: 'Logo', width: 140 };
    case 'photo': return { id, type, size: 72, round: true };
    case 'divider': return { id, type, color: '#d1d1d1', thickness: 1 };
    case 'spacer': return { id, type, height: 12 };
    case 'social': return { id, type, size: 24, items: [{ network: 'linkedin', url: 'https://www.linkedin.com/company/' }, { network: 'web', url: 'https://' }] };
    case 'button': return { id, type, label: 'Book a meeting', href: 'https://', background: '#0f6cbd', color: '#ffffff' };
    case 'survey': return { id, type, surveyId: '' };
    case 'qr': return { id, type, size: 84 };
    case 'columns': return { id, type, left: [], right: [], gap: 14, divider: true };
  }
}

const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function compileBlock(b: Block, d: Design, publicBaseUrl: string): string {
  switch (b.type) {
    case 'text': {
      const html = `<div style="padding:0 0 4px 0">${b.html}</div>`;
      return b.showIf ? `{if ${b.showIf}}${html}{/if}` : html;
    }
    case 'image': {
      if (!b.src) return '';
      const img = `<img src="${esc(b.src)}" alt="${esc(b.alt)}" width="${b.width}" style="display:block;border:0;width:${b.width}px;max-width:100%;height:auto">`;
      return `<div style="padding:0 0 6px 0">${b.href ? `<a href="${esc(b.href)}" style="text-decoration:none">${img}</a>` : img}</div>`;
    }
    case 'photo':
      return `{if Photo}<img src="{Photo}" alt="{DisplayName}" width="${b.size}" height="${b.size}" style="display:block;border:0;width:${b.size}px;height:${b.size}px;${b.round ? 'border-radius:50%;' : ''}">{/if}`;
    case 'divider':
      return `<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="margin:6px 0"><tr><td style="border-top:${b.thickness}px solid ${esc(b.color)};font-size:0;line-height:0">&nbsp;</td></tr></table>`;
    case 'spacer':
      return `<div style="height:${b.height}px;line-height:${b.height}px;font-size:0">&nbsp;</div>`;
    case 'social':
      return `<div style="padding:4px 0">${b.items.filter((i) => i.url).map((i) =>
        `<a href="${esc(i.url)}" style="text-decoration:none"><img src="${publicBaseUrl}/icons/${i.network}.png" alt="${NETWORK_LABEL[i.network] ?? i.network}" width="${b.size}" height="${b.size}" style="display:inline-block;border:0;margin-right:6px"></a>`).join('')}</div>`;
    case 'button':
      return `<div style="padding:6px 0"><a href="${esc(b.href)}" style="display:inline-block;background:${esc(b.background)};color:${esc(b.color)};padding:8px 16px;border-radius:4px;text-decoration:none;font-weight:600">${esc(b.label)}</a></div>`;
    case 'survey':
      return b.surveyId ? `{Csat:${b.surveyId}}` : '';
    case 'qr':
      return `<img src="{QrCode}" alt="Scan to save my contact details" width="${b.size}" height="${b.size}" style="display:block;border:0">`;
    case 'columns': {
      const left = b.left.map((x) => compileBlock(x, d, publicBaseUrl)).join('\n');
      const right = b.right.map((x) => compileBlock(x, d, publicBaseUrl)).join('\n');
      return `<table role="presentation" cellpadding="0" cellspacing="0" border="0"><tr>` +
        `<td style="vertical-align:top;padding:0 ${b.gap}px 0 0">${left}</td>` +
        `<td style="vertical-align:top;${b.divider ? `border-left:2px solid #0f6cbd;padding:0 0 0 ${b.gap}px` : 'padding:0'}">${right}</td>` +
        `</tr></table>`;
    }
  }
}

/** Design → the HTML template stored and rendered by the service. */
export function compile(d: Design, publicBaseUrl: string): string {
  const rows = d.blocks.map((b) => compileBlock(b, d, publicBaseUrl)).filter(Boolean).map((html) => `<tr><td style="padding:0">${html}</td></tr>`).join('\n');
  return `<table role="presentation" cellpadding="0" cellspacing="0" border="0" style="font-family:${esc(d.fontFamily)};font-size:${d.fontSize}px;color:${esc(d.color)};line-height:1.4">\n${rows}\n</table>`;
}

/** What the canvas shows for one block: the compiled HTML with template logic removed, or a stand-in. */
export function canvasHtml(b: Block, d: Design, publicBaseUrl: string, surveyName: (id: string) => string | undefined): string {
  const box = (text: string, w = 0, h = 0, round = false) =>
    `<div style="display:inline-flex;align-items:center;justify-content:center;text-align:center;${w ? `width:${w}px;height:${h}px;` : 'padding:8px 12px;'}background:#f0f0f0;border:1px dashed #a0a0a0;color:#616161;font:11px Segoe UI,sans-serif;${round ? 'border-radius:50%;' : 'border-radius:4px;'}">${text}</div>`;
  switch (b.type) {
    case 'photo': return box('Photo', b.size, b.size, b.round);
    case 'qr': return box('QR', b.size, b.size);
    case 'survey': return box(b.surveyId ? `Survey: ${esc(surveyName(b.surveyId) ?? 'unknown')}` : 'Survey — choose one in the properties');
    case 'image': if (!b.src) return box('Image — choose one in the properties'); break;
    case 'spacer': return `<div style="height:${Math.max(b.height, 6)}px;background:repeating-linear-gradient(45deg,#f3f3f3 0 6px,#fff 6px 12px)"></div>`;
    case 'columns': return '';
  }
  return compileBlock(b, d, publicBaseUrl).replace(/\{\/?if[^}]*\}|\{else\}/g, '');
}

// ---------- tree operations (all return a new design) ----------

type ListKey = 'root' | `${string}:left` | `${string}:right`;

const clone = (d: Design): Design => structuredClone(d);

function walk(blocks: Block[], visit: (list: Block[], key: ListKey) => void, key: ListKey = 'root') {
  visit(blocks, key);
  for (const b of blocks) {
    if (b.type === 'columns') {
      walk(b.left, visit, `${b.id}:left`);
      walk(b.right, visit, `${b.id}:right`);
    }
  }
}

export function findBlock(d: Design, id: string): Block | undefined {
  let found: Block | undefined;
  walk(d.blocks, (list) => { found ??= list.find((b) => b.id === id); });
  return found;
}

function listOf(d: Design, key: string): Block[] | undefined {
  let found: Block[] | undefined;
  walk(d.blocks, (list, k) => { if (k === key) found = list; });
  return found;
}

function contains(block: Block, id: string): boolean {
  if (block.id === id) return true;
  return block.type === 'columns' && [...block.left, ...block.right].some((c) => contains(c, id));
}

export function updateBlock(d: Design, id: string, patch: Partial<Block>): Design {
  const next = clone(d);
  walk(next.blocks, (list) => {
    const i = list.findIndex((b) => b.id === id);
    if (i >= 0) list[i] = { ...list[i], ...patch } as Block;
  });
  return next;
}

export function removeBlock(d: Design, id: string): Design {
  const next = clone(d);
  walk(next.blocks, (list) => {
    const i = list.findIndex((b) => b.id === id);
    if (i >= 0) list.splice(i, 1);
  });
  return next;
}

/** Inserts into the list `key` before the block `beforeId` (or at the end). */
export function insertBlock(d: Design, block: Block, key: string, beforeId?: string): Design {
  const next = clone(d);
  const list = listOf(next, key);
  if (!list) return d;
  const i = beforeId ? list.findIndex((b) => b.id === beforeId) : -1;
  list.splice(i < 0 ? list.length : i, 0, block);
  return next;
}

export function moveBlock(d: Design, id: string, key: string, beforeId?: string): Design {
  const block = findBlock(d, id);
  // A columns block cannot be dropped inside itself.
  if (!block || id === beforeId || contains(block, key.split(':')[0])) return d;
  return insertBlock(removeBlock(d, id), block, key, beforeId);
}

/** Moves a block one step up (-1) or down (+1) within its own list. */
export function nudgeBlock(d: Design, id: string, delta: -1 | 1): Design {
  const next = clone(d);
  walk(next.blocks, (list) => {
    const i = list.findIndex((b) => b.id === id);
    const j = i + delta;
    if (i >= 0 && j >= 0 && j < list.length) [list[i], list[j]] = [list[j], list[i]];
  });
  return next;
}

// ---------- starters ----------

const base = (blocks: Block[]): Design => ({ version: 1, fontFamily: FONTS[0], fontSize: 13, color: '#323130', blocks });

export const STARTERS: { key: string; name: string; description: string; design: () => Design | null }[] = [
  {
    key: 'corporate', name: 'Corporate', description: 'Photo on the left, contact details on the right',
    design: () => base([{
      id: newId(), type: 'columns', gap: 14, divider: true,
      left: [{ id: newId(), type: 'photo', size: 72, round: true }],
      right: [
        { id: newId(), type: 'text', html: '<span style="font-size:16px;color:#0f6cbd"><b>{DisplayName}</b></span><br>{JobTitle}' },
        { id: newId(), type: 'text', html: 'T: {Phone}', showIf: 'Phone' },
        { id: newId(), type: 'text', html: 'M: {MobilePhone}', showIf: 'MobilePhone' },
        { id: newId(), type: 'text', html: '<a href="mailto:{Email}">{Email}</a>' },
        { id: newId(), type: 'social', size: 22, items: [{ network: 'linkedin', url: 'https://www.linkedin.com/company/' }, { network: 'web', url: 'https://' }] },
      ],
    }]),
  },
  {
    key: 'simple', name: 'Simple', description: 'Name, title and contact line',
    design: () => base([
      { id: newId(), type: 'text', html: '<b>{DisplayName}</b> | {JobTitle}' },
      { id: newId(), type: 'text', html: '{Company} · <a href="mailto:{Email}">{Email}</a>' },
    ]),
  },
  {
    key: 'disclaimer', name: 'Legal disclaimer', description: 'Small print for the bottom of external mail',
    design: () => base([
      { id: newId(), type: 'divider', color: '#e1dfdd', thickness: 1 },
      { id: newId(), type: 'text', html: '<span style="font-size:10px;color:#8a8886">This email and any attachments are confidential and intended solely for the addressee. © {CurrentYear} {Company}</span>' },
    ]),
  },
  { key: 'blank', name: 'Blank', description: 'Start from an empty canvas', design: () => base([]) },
  { key: 'source', name: 'HTML source', description: 'Paste or write your own HTML', design: () => null },
];
