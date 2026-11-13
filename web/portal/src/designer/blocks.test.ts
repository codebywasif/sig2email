import { describe, expect, it } from 'vitest';
import { compile, createBlock, findBlock, insertBlock, moveBlock, nudgeBlock, removeBlock, updateBlock, type Block, type Design } from './blocks';

const design = (blocks: Block[]): Design => ({ version: 1, fontFamily: 'Arial, sans-serif', fontSize: 13, color: '#111111', blocks });
const text = (id: string, html = id, showIf?: string): Block => ({ id, type: 'text', html, showIf });
const ids = (blocks: Block[]) => blocks.map((b) => b.id);

describe('compile', () => {
  it('produces table-based HTML with conditional wrappers and placeholders intact', () => {
    const html = compile(design([text('a', '<b>{DisplayName}</b>'), text('b', 'M: {MobilePhone}', 'MobilePhone')]), 'https://app.test');
    expect(html).toContain('<table role="presentation"');
    expect(html).toContain('font-family:Arial, sans-serif;font-size:13px;color:#111111');
    expect(html).toContain('<b>{DisplayName}</b>');
    expect(html).toContain('{if MobilePhone}<div style="padding:0 0 4px 0">M: {MobilePhone}</div>{/if}');
  });

  it('compiles special blocks to the tokens the service understands', () => {
    const html = compile(design([
      { id: 'p', type: 'photo', size: 64, round: true },
      { id: 'q', type: 'qr', size: 80 },
      { id: 's', type: 'survey', surveyId: '11111111-2222-3333-4444-555555555555' },
      { id: 'i', type: 'social', size: 24, items: [{ network: 'linkedin', url: 'https://l.example/a?b=1&c=2' }, { network: 'x', url: '' }] },
      { id: 'e', type: 'image', src: '', alt: '', width: 100 },
    ]), 'https://app.test');
    expect(html).toContain('{if Photo}<img src="{Photo}"');
    expect(html).toContain('src="{QrCode}"');
    expect(html).toContain('{Csat:11111111-2222-3333-4444-555555555555}');
    expect(html).toContain('https://app.test/icons/linkedin.png');
    expect(html).toContain('href="https://l.example/a?b=1&amp;c=2"');
    expect(html).not.toContain('icons/x.png'); // icons without a link are dropped
    expect(html.match(/<tr>/g)).toHaveLength(4); // the empty image block emits no row
  });

  it('escapes attribute values', () => {
    const html = compile(design([{ id: 'b', type: 'button', label: '<Go>', href: 'https://x.example/"onmouseover="x', background: '#000', color: '#fff' }]), '');
    expect(html).toContain('&lt;Go&gt;');
    expect(html).not.toContain('"onmouseover="');
  });
});

describe('tree operations', () => {
  const cols: Block = { id: 'c', type: 'columns', gap: 10, divider: false, left: [text('l1')], right: [text('r1'), text('r2')] };
  const d = design([text('a'), cols, text('z')]);

  it('finds, updates and removes nested blocks without mutating the original', () => {
    expect(findBlock(d, 'r2')?.id).toBe('r2');
    const updated = updateBlock(d, 'r2', { html: 'changed' } as Partial<Block>);
    expect((findBlock(updated, 'r2') as { html: string }).html).toBe('changed');
    expect((findBlock(d, 'r2') as { html: string }).html).toBe('r2');
    expect(findBlock(removeBlock(d, 'l1'), 'l1')).toBeUndefined();
  });

  it('inserts and moves between lists', () => {
    const inserted = insertBlock(d, text('n'), 'c:left', 'l1');
    expect(ids((findBlock(inserted, 'c') as Extract<Block, { type: 'columns' }>).left)).toEqual(['n', 'l1']);
    const moved = moveBlock(d, 'z', 'c:right', 'r2');
    expect(ids(moved.blocks)).toEqual(['a', 'c']);
    expect(ids((findBlock(moved, 'c') as Extract<Block, { type: 'columns' }>).right)).toEqual(['r1', 'z', 'r2']);
    expect(ids(moveBlock(d, 'a', 'root').blocks)).toEqual(['c', 'z', 'a']);
  });

  it('refuses to move a column block into itself', () => {
    expect(moveBlock(d, 'c', 'c:left')).toBe(d);
  });

  it('nudges within a list and stops at the edges', () => {
    expect(ids(nudgeBlock(d, 'z', -1).blocks)).toEqual(['a', 'z', 'c']);
    expect(ids(nudgeBlock(d, 'a', -1).blocks)).toEqual(['a', 'c', 'z']);
  });

  it('creates every block type with a unique id', () => {
    const types = ['text', 'image', 'photo', 'divider', 'spacer', 'social', 'button', 'survey', 'qr', 'columns'] as const;
    expect(new Set(types.map((t) => createBlock(t).id)).size).toBe(types.length);
  });
});
