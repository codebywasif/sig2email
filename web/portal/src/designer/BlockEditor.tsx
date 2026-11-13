import { useEffect, useRef, useState, type DragEvent } from 'react';
import DOMPurify from 'dompurify';
import { Button, Checkbox, Dropdown, Field, Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Option, SpinButton, Text } from '@fluentui/react-components';
import {
  AddRegular, ArrowDownRegular, ArrowUpRegular, DeleteRegular, LinkRegular, TextBoldRegular, TextItalicRegular, TextUnderlineRegular,
} from '@fluentui/react-icons';
import type { Survey } from '../api';
import {
  BLOCK_LABEL, FONTS, NETWORKS, NETWORK_LABEL, canvasHtml, createBlock, findBlock, insertBlock, moveBlock, nudgeBlock, removeBlock, updateBlock,
  type Block, type BlockType, type Design,
} from './blocks';

const DND = 'application/x-s2e-block';
const PALETTE: BlockType[] = ['text', 'image', 'photo', 'columns', 'divider', 'spacer', 'social', 'button', 'survey', 'qr'];

// Templates are authored by other administrators too, so nothing is injected into the portal DOM unsanitised.
const clean = (html: string) => DOMPurify.sanitize(html, { FORBID_TAGS: ['style', 'form', 'input', 'iframe', 'script'], FORBID_ATTR: ['id', 'name'] });
const cleanInline = (html: string) => DOMPurify.sanitize(html, { ALLOWED_TAGS: ['b', 'strong', 'i', 'em', 'u', 'a', 'br', 'span', 'font', 'div', 'p'], ALLOWED_ATTR: ['href', 'style', 'color', 'size', 'face'] });

interface Props {
  design: Design;
  onChange: (d: Design) => void;
  surveys: Survey[];
  placeholders: string[];
  publicBaseUrl: string;
  readOnly: boolean;
  /** Opens the image library; resolves with the chosen image URL. */
  pickImage: () => Promise<string | null>;
}

export function BlockEditor({ design, onChange, surveys, placeholders, publicBaseUrl, readOnly, pickImage }: Props) {
  const [selected, setSelected] = useState<string | null>(null);
  const [drop, setDrop] = useState<string | null>(null); // "list|beforeId" currently hovered
  const block = selected ? findBlock(design, selected) : undefined;
  const surveyName = (id: string) => surveys.find((s) => s.id === id)?.name;

  const add = (type: BlockType) => {
    const b = createBlock(type);
    // With a column block selected, new blocks go into its right-hand column; otherwise to the end.
    onChange(insertBlock(design, b, block?.type === 'columns' ? `${block.id}:right` : 'root'));
    setSelected(b.id);
  };

  const onDrop = (e: DragEvent, list: string, beforeId?: string) => {
    e.preventDefault();
    e.stopPropagation();
    setDrop(null);
    const raw = e.dataTransfer.getData(DND);
    if (!raw || readOnly) return;
    const data = JSON.parse(raw) as { kind: 'new'; type: BlockType } | { kind: 'move'; id: string };
    if (data.kind === 'new') {
      const b = createBlock(data.type);
      onChange(insertBlock(design, b, list, beforeId));
      setSelected(b.id);
    } else onChange(moveBlock(design, data.id, list, beforeId));
  };
  const over = (e: DragEvent, key: string) => {
    if (!e.dataTransfer.types.includes(DND) || readOnly) return;
    e.preventDefault();
    e.stopPropagation();
    setDrop(key);
  };

  const renderList = (blocks: Block[], list: string) => (
    <div className={`blocks${blocks.length === 0 ? ' empty-zone' : ''}${drop === `${list}|` ? ' drop-end' : ''}`}
      onDragOver={(e) => over(e, `${list}|`)} onDrop={(e) => onDrop(e, list)} onDragLeave={() => setDrop(null)}>
      {blocks.length === 0 && 'Drop blocks here'}
      {blocks.map((b, i) => (
        <div key={b.id} role="button" tabIndex={0} aria-label={`${BLOCK_LABEL[b.type]} block${selected === b.id ? ', selected' : ''}`}
          className={`block${selected === b.id ? ' selected' : ''}${drop === `${list}|${b.id}` ? ' drop-before' : ''}`}
          draggable={!readOnly}
          onDragStart={(e) => { e.stopPropagation(); e.dataTransfer.setData(DND, JSON.stringify({ kind: 'move', id: b.id })); e.dataTransfer.effectAllowed = 'move'; }}
          onDragOver={(e) => over(e, `${list}|${b.id}`)} onDrop={(e) => onDrop(e, list, b.id)}
          onClick={(e) => { e.stopPropagation(); setSelected(b.id); }}
          onKeyDown={(e) => {
            if (e.target !== e.currentTarget) return;
            if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setSelected(b.id); }
            if (readOnly) return;
            if (e.key === 'Delete') { onChange(removeBlock(design, b.id)); setSelected(null); }
            if (e.altKey && e.key === 'ArrowUp') { e.preventDefault(); onChange(nudgeBlock(design, b.id, -1)); }
            if (e.altKey && e.key === 'ArrowDown') { e.preventDefault(); onChange(nudgeBlock(design, b.id, 1)); }
          }}>
          {!readOnly && (
            <div className="block-tools">
              <button type="button" aria-label="Move up" disabled={i === 0} onClick={(e) => { e.stopPropagation(); onChange(nudgeBlock(design, b.id, -1)); }}><ArrowUpRegular /></button>
              <button type="button" aria-label="Move down" disabled={i === blocks.length - 1} onClick={(e) => { e.stopPropagation(); onChange(nudgeBlock(design, b.id, 1)); }}><ArrowDownRegular /></button>
              <button type="button" aria-label="Delete block" onClick={(e) => { e.stopPropagation(); onChange(removeBlock(design, b.id)); setSelected(null); }}><DeleteRegular /></button>
            </div>
          )}
          {b.type === 'columns'
            ? <div className="block-cols"><div style={{ flex: '0 0 auto', minWidth: 90 }}>{renderList(b.left, `${b.id}:left`)}</div><div>{renderList(b.right, `${b.id}:right`)}</div></div>
            : <div style={{ pointerEvents: 'none' }} dangerouslySetInnerHTML={{ __html: clean(canvasHtml(b, design, publicBaseUrl, surveyName)) }} />}
        </div>
      ))}
    </div>
  );

  const patch = (p: Partial<Block>) => block && onChange(updateBlock(design, block.id, p));

  return (
    <div className="stack">
      {!readOnly && (
        <div className="palette" role="toolbar" aria-label="Add a block (click, or drag onto the canvas)">
          {PALETTE.map((type) => (
            <button key={type} type="button" className="palette-item" draggable onClick={() => add(type)}
              onDragStart={(e) => { e.dataTransfer.setData(DND, JSON.stringify({ kind: 'new', type })); e.dataTransfer.effectAllowed = 'copy'; }}>
              <AddRegular aria-hidden="true" />{BLOCK_LABEL[type]}
            </button>
          ))}
        </div>
      )}
      <div className="canvas" style={{ fontFamily: design.fontFamily, fontSize: design.fontSize, color: design.color, lineHeight: 1.4 }} onClick={() => setSelected(null)}>
        {renderList(design.blocks, 'root')}
      </div>

      <fieldset disabled={readOnly} className="panel props" style={{ margin: 0, minWidth: 0 }}>
        {!block && (
          <>
            <h3>Signature style</h3>
            <div className="form-grid">
              <Field label="Font">
                <Dropdown value={design.fontFamily.split(',')[0]} selectedOptions={[design.fontFamily]} onOptionSelect={(_, d) => onChange({ ...design, fontFamily: d.optionValue! })}>
                  {FONTS.map((f) => <Option key={f} value={f} text={f.split(',')[0]}><span style={{ fontFamily: f }}>{f.split(',')[0]}</span></Option>)}
                </Dropdown>
              </Field>
              <Field label="Text size (px)"><SpinButton min={9} max={24} value={design.fontSize} onChange={(_, d) => onChange({ ...design, fontSize: d.value ?? (Number(d.displayValue) || design.fontSize) })} /></Field>
              <Field label="Text colour"><input type="color" aria-label="Text colour" value={design.color} onChange={(e) => onChange({ ...design, color: e.target.value })} /></Field>
            </div>
            <Text size={200} className="muted">Select a block on the canvas to edit it. Drag blocks to rearrange, or use Alt+↑/↓ with a block focused.</Text>
          </>
        )}
        {block && <h3>{BLOCK_LABEL[block.type]}</h3>}
        {block?.type === 'text' && (
          <>
            <RichText key={block.id} html={block.html} placeholders={placeholders} onChange={(html) => patch({ html })} />
            <Field label="Only show this block when" hint="Hides the whole line, label included, if the person has no value.">
              <Dropdown value={block.showIf ? `${block.showIf} is filled in` : 'Always show'} selectedOptions={[block.showIf ?? '']} onOptionSelect={(_, d) => patch({ showIf: d.optionValue || undefined })}>
                <Option value="">Always show</Option>
                {placeholders.map((p) => <Option key={p} value={p}>{`${p} is filled in`}</Option>)}
              </Dropdown>
            </Field>
          </>
        )}
        {block?.type === 'image' && (
          <>
            <div className="row">
              <Button onClick={() => void pickImage().then((url) => { if (url) patch({ src: url }); })}>{block.src ? 'Change image' : 'Choose image'}</Button>
              {block.src && <Text size={200} className="muted" style={{ wordBreak: 'break-all' }}>{block.src.split('/').pop()}</Text>}
            </div>
            <div className="form-grid">
              <Field label="Width (px)"><SpinButton min={16} max={800} value={block.width} onChange={(_, d) => patch({ width: d.value ?? (Number(d.displayValue) || block.width) })} /></Field>
              <Field label="Description (alt text)"><Input value={block.alt} onChange={(_, d) => patch({ alt: d.value })} /></Field>
              <Field label="Link (optional)"><Input value={block.href ?? ''} placeholder="https://" onChange={(_, d) => patch({ href: d.value || undefined })} /></Field>
            </div>
          </>
        )}
        {block?.type === 'photo' && (
          <>
            <Text size={200} className="muted">Shows the sender’s Microsoft 365 profile photo. People without a photo get no image and no gap.</Text>
            <div className="row">
              <Field label="Size (px)"><SpinButton min={32} max={200} value={block.size} onChange={(_, d) => patch({ size: d.value ?? (Number(d.displayValue) || block.size) })} /></Field>
              <Checkbox label="Round" checked={block.round} onChange={(_, d) => patch({ round: !!d.checked })} />
            </div>
          </>
        )}
        {block?.type === 'divider' && (
          <div className="row">
            <Field label="Colour"><input type="color" aria-label="Divider colour" value={block.color} onChange={(e) => patch({ color: e.target.value })} /></Field>
            <Field label="Thickness (px)"><SpinButton min={1} max={8} value={block.thickness} onChange={(_, d) => patch({ thickness: d.value ?? (Number(d.displayValue) || 1) })} /></Field>
          </div>
        )}
        {block?.type === 'spacer' && <Field label="Height (px)"><SpinButton min={2} max={80} value={block.height} onChange={(_, d) => patch({ height: d.value ?? (Number(d.displayValue) || block.height) })} /></Field>}
        {block?.type === 'social' && (
          <>
            {block.items.map((item, i) => (
              <div className="row" key={i}>
                <Dropdown aria-label="Network" value={NETWORK_LABEL[item.network]} selectedOptions={[item.network]} style={{ minWidth: 130 }}
                  onOptionSelect={(_, d) => patch({ items: block.items.map((x, j) => (j === i ? { ...x, network: d.optionValue! } : x)) })}>
                  {NETWORKS.map((n) => <Option key={n} value={n}>{NETWORK_LABEL[n]}</Option>)}
                </Dropdown>
                <Input className="grow" aria-label={`${NETWORK_LABEL[item.network]} link`} value={item.url} placeholder="https://"
                  onChange={(_, d) => patch({ items: block.items.map((x, j) => (j === i ? { ...x, url: d.value } : x)) })} />
                <Button appearance="subtle" icon={<DeleteRegular />} aria-label="Remove icon" onClick={() => patch({ items: block.items.filter((_, j) => j !== i) })} />
              </div>
            ))}
            <div className="row">
              <Button size="small" icon={<AddRegular />} onClick={() => patch({ items: [...block.items, { network: 'x', url: 'https://' }] })}>Add icon</Button>
              <Field label="Icon size (px)" orientation="horizontal"><SpinButton min={16} max={48} value={block.size} onChange={(_, d) => patch({ size: d.value ?? (Number(d.displayValue) || block.size) })} /></Field>
            </div>
          </>
        )}
        {block?.type === 'button' && (
          <div className="form-grid">
            <Field label="Label"><Input value={block.label} onChange={(_, d) => patch({ label: d.value })} /></Field>
            <Field label="Link"><Input value={block.href} onChange={(_, d) => patch({ href: d.value })} /></Field>
            <Field label="Background"><input type="color" aria-label="Button background" value={block.background} onChange={(e) => patch({ background: e.target.value })} /></Field>
            <Field label="Text colour"><input type="color" aria-label="Button text colour" value={block.color} onChange={(e) => patch({ color: e.target.value })} /></Field>
          </div>
        )}
        {block?.type === 'survey' && (
          <Field label="Survey" hint={surveys.length === 0 ? 'No surveys yet — a rule administrator can create one under Surveys.' : 'Recipients rate with one click, straight from the email.'}>
            <Dropdown placeholder="Choose a survey" value={surveyName(block.surveyId) ?? ''} selectedOptions={[block.surveyId]} onOptionSelect={(_, d) => patch({ surveyId: d.optionValue ?? '' })}>
              {surveys.map((s) => <Option key={s.id} value={s.id}>{s.name}</Option>)}
            </Dropdown>
          </Field>
        )}
        {block?.type === 'qr' && (
          <>
            <Text size={200} className="muted">A QR code generated per sender; scanning it saves their contact card (vCard).</Text>
            <Field label="Size (px)"><SpinButton min={48} max={200} value={block.size} onChange={(_, d) => patch({ size: d.value ?? (Number(d.displayValue) || block.size) })} /></Field>
          </>
        )}
        {block?.type === 'columns' && (
          <div className="row">
            <Field label="Gap (px)"><SpinButton min={0} max={60} value={block.gap} onChange={(_, d) => patch({ gap: d.value ?? (Number(d.displayValue) || 0) })} /></Field>
            <Checkbox label="Accent line between columns" checked={block.divider} onChange={(_, d) => patch({ divider: !!d.checked })} />
          </div>
        )}
      </fieldset>
    </div>
  );
}

/** Minimal rich-text editing for a text block: emphasis, links and placeholders. */
function RichText({ html, onChange, placeholders }: { html: string; onChange: (html: string) => void; placeholders: string[] }) {
  const ref = useRef<HTMLDivElement>(null);
  // Uncontrolled: the DOM owns the content while editing, so the caret never jumps.
  useEffect(() => { if (ref.current) ref.current.innerHTML = cleanInline(html); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, []);
  const emit = () => onChange(cleanInline(ref.current?.innerHTML ?? ''));
  const exec = (command: string, value?: string) => {
    ref.current?.focus();
    document.execCommand(command, false, value);
    emit();
  };
  const link = () => {
    const url = window.prompt('Link address (https://…, mailto:… or tel:…). Placeholders such as mailto:{Email} work.', 'https://');
    if (url && /^(https?:|mailto:|tel:)/i.test(url)) exec('createLink', url);
  };
  return (
    <div className="rich">
      <div className="rich-toolbar" role="toolbar" aria-label="Text formatting">
        <Button size="small" appearance="subtle" icon={<TextBoldRegular />} aria-label="Bold" onClick={() => exec('bold')} />
        <Button size="small" appearance="subtle" icon={<TextItalicRegular />} aria-label="Italic" onClick={() => exec('italic')} />
        <Button size="small" appearance="subtle" icon={<TextUnderlineRegular />} aria-label="Underline" onClick={() => exec('underline')} />
        <Button size="small" appearance="subtle" icon={<LinkRegular />} aria-label="Insert link" onClick={link} />
        <input type="color" aria-label="Text colour" title="Text colour" defaultValue="#0f6cbd" onChange={(e) => exec('foreColor', e.target.value)} style={{ width: 28, height: 24, padding: 0, border: 0, background: 'none' }} />
        <Menu>
          <MenuTrigger disableButtonEnhancement><Button size="small" appearance="subtle">Size</Button></MenuTrigger>
          <MenuPopover><MenuList>{[['Small', '2'], ['Normal', '3'], ['Large', '4'], ['Larger', '5']].map(([l, v]) => <MenuItem key={v} onClick={() => exec('fontSize', v)}>{l}</MenuItem>)}</MenuList></MenuPopover>
        </Menu>
        <Menu>
          <MenuTrigger disableButtonEnhancement><Button size="small" appearance="subtle" icon={<AddRegular />}>Placeholder</Button></MenuTrigger>
          <MenuPopover><MenuList style={{ maxHeight: 280, overflowY: 'auto' }}>
            {[...placeholders, 'CurrentYear', 'CurrentDate'].map((p) => <MenuItem key={p} onClick={() => exec('insertText', `{${p}}`)}>{`{${p}}`}</MenuItem>)}
          </MenuList></MenuPopover>
        </Menu>
      </div>
      <div ref={ref} className="rich-body" contentEditable suppressContentEditableWarning role="textbox" aria-multiline="true" aria-label="Block text" onInput={emit} onBlur={emit} />
    </div>
  );
}
