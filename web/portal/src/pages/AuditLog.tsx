import { useState } from 'react';
import { Button, Input, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text } from '@fluentui/react-components';
import { SearchRegular } from '@fluentui/react-icons';
import { api } from '../api';
import { Empty, LoadState, Page, fmtDate, useLoad } from '../common';

interface Entry { id: string; actorName: string; action: string; entityType: string; summary: string | null; ip: string | null; at: string }
const PAGE = 50;

export function AuditLog() {
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(0);
  const { data, loading, error, reload } = useLoad(
    () => api.get<{ total: number; items: Entry[] }>(`/api/audit?take=${PAGE}&skip=${page * PAGE}&search=${encodeURIComponent(search)}`), [search, page]);

  return (
    <Page title="Audit log" description="Every configuration change, who made it and when.">
      <Input contentBefore={<SearchRegular />} placeholder="Filter by action, person or detail" aria-label="Filter the audit log" value={search}
        onChange={(_, d) => { setSearch(d.value); setPage(0); }} style={{ maxWidth: 360 }} />
      <section className="panel flush">
        <LoadState loading={loading && !data} error={error} onRetry={reload} />
        {data && data.items.length === 0 && <Empty title="No matching entries" />}
        {data && data.items.length > 0 && (
          <div className="table-wrap">
            <Table size="small" aria-label="Audit log">
              <TableHeader><TableRow>
                <TableHeaderCell>When</TableHeaderCell><TableHeaderCell>Who</TableHeaderCell><TableHeaderCell>Action</TableHeaderCell>
                <TableHeaderCell>Detail</TableHeaderCell><TableHeaderCell>IP address</TableHeaderCell>
              </TableRow></TableHeader>
              <TableBody>
                {data.items.map((e) => (
                  <TableRow key={e.id}>
                    <TableCell className="nowrap">{fmtDate(e.at)}</TableCell>
                    <TableCell>{e.actorName}</TableCell>
                    <TableCell><span className="mono">{e.action}</span></TableCell>
                    <TableCell>{e.summary ?? '—'}</TableCell>
                    <TableCell>{e.ip ?? '—'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
        {data && data.total > PAGE && (
          <div className="row" style={{ padding: 10 }}>
            <Text size={200} className="muted">Showing {page * PAGE + 1}–{Math.min((page + 1) * PAGE, data.total)} of {data.total}</Text>
            <Button size="small" className="right" disabled={page === 0} onClick={() => setPage(page - 1)}>Newer</Button>
            <Button size="small" disabled={(page + 1) * PAGE >= data.total} onClick={() => setPage(page + 1)}>Older</Button>
          </div>
        )}
      </section>
    </Page>
  );
}
