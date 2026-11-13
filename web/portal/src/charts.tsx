import { useEffect, useRef, useState } from 'react';
import { Button, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';

export interface Datum { label: string; value: number; note?: string }

const nice = (max: number) => {
  if (max <= 0) return 1;
  const pow = 10 ** Math.floor(Math.log10(max));
  const step = [1, 2, 5, 10].find((m) => m * pow >= max / 4)! * pow;
  return Math.ceil(max / step) * step;
};

/**
 * Single-series column chart: thin bars with rounded data-ends on a baseline, recessive grid, a hover/focus
 * tooltip per column and a table view so the numbers never depend on the picture.
 */
export function ColumnChart({ data, unit, height = 200, ariaLabel }: { data: Datum[]; unit: string; height?: number; ariaLabel: string }) {
  const host = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(600);
  const [hover, setHover] = useState<number | null>(null);
  const [table, setTable] = useState(false);

  useEffect(() => {
    if (!host.current) return;
    const ro = new ResizeObserver(([entry]) => setWidth(Math.max(240, entry.contentRect.width)));
    ro.observe(host.current);
    return () => ro.disconnect();
  }, []);

  const pad = { top: 10, right: 8, bottom: 22, left: 36 };
  const innerW = width - pad.left - pad.right;
  const innerH = height - pad.top - pad.bottom;
  const max = nice(Math.max(...data.map((d) => d.value), 0));
  const slot = innerW / Math.max(data.length, 1);
  const barW = Math.max(2, Math.min(18, slot - 2));
  // Whole-number ticks only: counts are never fractional.
  const ticks = [...new Set([0, 0.25, 0.5, 0.75, 1].map((t) => t * max).filter(Number.isInteger))];
  const labelEvery = Math.max(1, Math.ceil(data.length / Math.max(2, Math.floor(innerW / 64))));
  const y = (v: number) => pad.top + innerH - (v / max) * innerH;

  return (
    <div className="stack tight">
      <div className="row"><span className="spacer" /><Button size="small" appearance="subtle" onClick={() => setTable((t) => !t)}>{table ? 'Show chart' : 'Show as table'}</Button></div>
      {table ? (
        <div className="table-wrap" style={{ maxHeight: height + 40, overflowY: 'auto' }}>
          <Table size="small" aria-label={ariaLabel}>
            <TableHeader><TableRow><TableHeaderCell>Period</TableHeaderCell><TableHeaderCell>{unit}</TableHeaderCell></TableRow></TableHeader>
            <TableBody>{data.map((d) => <TableRow key={d.label}><TableCell>{d.label}</TableCell><TableCell>{d.value.toLocaleString()}</TableCell></TableRow>)}</TableBody>
          </Table>
        </div>
      ) : (
        <div className="viz-root" ref={host} onMouseLeave={() => setHover(null)}>
          <svg height={height} role="img" aria-label={ariaLabel}>
            {ticks.map((t) => (
              <g key={t}>
                <line className="grid" x1={pad.left} x2={width - pad.right} y1={y(t)} y2={y(t)} />
                <text className="axis-label" x={pad.left - 6} y={y(t) + 4} textAnchor="end">{t.toLocaleString()}</text>
              </g>
            ))}
            {data.map((d, i) => {
              const x = pad.left + i * slot + (slot - barW) / 2;
              const h = d.value > 0 ? Math.max(2, (d.value / max) * innerH) : 0;
              const r = Math.min(4, barW / 2, h);
              const top = pad.top + innerH - h;
              return (
                <g key={d.label}>
                  {h > 0 && (
                    <path className={`bar${hover != null && hover !== i ? ' dim' : ''}`}
                      d={`M${x},${top + h} V${top + r} Q${x},${top} ${x + r},${top} H${x + barW - r} Q${x + barW},${top} ${x + barW},${top + r} V${top + h} Z`} />
                  )}
                  {i % labelEvery === 0 && <text className="axis-label" x={x + barW / 2} y={height - 6} textAnchor="middle">{d.label}</text>}
                  <rect className="hit" x={pad.left + i * slot} y={pad.top} width={slot} height={innerH} tabIndex={0}
                    aria-label={`${d.label}: ${d.value.toLocaleString()} ${unit}`} onMouseEnter={() => setHover(i)} onFocus={() => setHover(i)} onBlur={() => setHover(null)} />
                </g>
              );
            })}
          </svg>
          {hover != null && data[hover] && (
            <div className="viz-tip" style={{ left: pad.left + hover * slot + slot / 2, top: Math.max(44, y(data[hover].value) - 6) }}>
              <strong>{data[hover].value.toLocaleString()} {unit}</strong>{data[hover].label}{data[hover].note && ` · ${data[hover].note}`}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/** Ranked horizontal bars with the value written next to each bar (direct labels, no axis needed). */
export function BarList({ data, format = (v) => v.toLocaleString() }: { data: Datum[]; format?: (v: number) => string }) {
  const max = Math.max(...data.map((d) => d.value), 1);
  return (
    <div className="viz-root hbars" role="list">
      {data.map((d) => (
        <div key={d.label} role="listitem" style={{ display: 'contents' }}>
          <span title={d.label} style={{ maxWidth: 260, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{d.label}</span>
          <div className="hbar-track"><div className="hbar-fill" style={{ width: `${(d.value / max) * 100}%` }} /></div>
          <span className="hbar-value">{format(d.value)}{d.note && <span className="muted"> · {d.note}</span>}</span>
        </div>
      ))}
    </div>
  );
}

export function StatTile({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <div className="tile">
      <div className="tile-label">{label}</div>
      <div className="tile-value">{value}</div>
      {note && <div className="tile-note">{note}</div>}
    </div>
  );
}
