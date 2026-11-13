import { useState } from 'react';
import {
  Avatar, Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, Tab, TabList, Table, TableBody,
  TableCell, TableHeader, TableHeaderCell, TableRow, Text,
} from '@fluentui/react-components';
import { ArrowSyncRegular, SearchRegular } from '@fluentui/react-icons';
import { api, type DirectoryGroup, type DirectoryUser, type TenantInfo } from '../api';
import { useAuth } from '../auth';
import { Empty, LoadState, Page, fmtDate, useAction, useLoad } from '../common';

const PAGE = 50;

export function Directory() {
  const { can } = useAuth();
  const [tab, setTab] = useState<'users' | 'groups'>('users');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(0);
  const [detail, setDetail] = useState<DirectoryUser | null>(null);
  const tenant = useLoad(() => api.get<TenantInfo>('/api/tenant'));
  const users = useLoad(() => api.get<{ total: number; items: DirectoryUser[] }>(`/api/directory/users?take=${PAGE}&skip=${page * PAGE}&search=${encodeURIComponent(search)}`), [search, page]);
  const groups = useLoad(() => api.get<DirectoryGroup[]>(`/api/directory/groups?search=${encodeURIComponent(search)}`), [search]);
  const { run, busy } = useAction();
  const t = tenant.data;

  return (
    <Page title="Directory" description={t ? `Synchronised from Microsoft Entra ID · last sync ${fmtDate(t.lastSyncAt)}${t.syncRequested ? ' · sync queued' : ''}` : undefined}
      actions={can('RuleAdmin') && (
        <Button icon={<ArrowSyncRegular />} disabled={busy || t?.syncRequested}
          onClick={() => void run(async () => { await api.post('/api/tenant/sync'); await tenant.reload(); }, 'Directory sync queued')}>Sync now</Button>
      )}>
      {t?.lastSyncError && <Text className="muted">Last sync error: {t.lastSyncError}</Text>}
      <div className="row">
        <TabList selectedValue={tab} onTabSelect={(_, d) => { setTab(d.value as 'users' | 'groups'); setPage(0); }}>
          <Tab value="users">People{users.data ? ` (${users.data.total})` : ''}</Tab>
          <Tab value="groups">Groups{groups.data ? ` (${groups.data.length})` : ''}</Tab>
        </TabList>
        <Input className="right" contentBefore={<SearchRegular />} placeholder="Search" aria-label="Search the directory" value={search}
          onChange={(_, d) => { setSearch(d.value); setPage(0); }} style={{ minWidth: 260 }} />
      </div>

      {tab === 'users' && (
        <section className="panel flush">
          <LoadState loading={users.loading && !users.data} error={users.error} onRetry={users.reload} />
          {users.data && users.data.items.length === 0 && <Empty title="No people found">{search ? 'Try a different search.' : 'The directory has not been synchronised yet.'}</Empty>}
          {users.data && users.data.items.length > 0 && (
            <div className="table-wrap">
              <Table aria-label="People">
                <TableHeader><TableRow>
                  <TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Job title</TableHeaderCell><TableHeaderCell>Department</TableHeaderCell>
                  <TableHeaderCell>Email</TableHeaderCell><TableHeaderCell>Groups</TableHeaderCell>
                </TableRow></TableHeader>
                <TableBody>
                  {users.data.items.map((u) => (
                    <TableRow key={u.id}>
                      <TableCell><Button appearance="transparent" className="link-button" icon={<Avatar name={u.displayName} size={24} color="colorful" />} onClick={() => setDetail(u)}>{u.displayName}</Button></TableCell>
                      <TableCell>{u.jobTitle ?? '—'}</TableCell>
                      <TableCell>{u.department ?? '—'}</TableCell>
                      <TableCell>{u.mail ?? u.upn}</TableCell>
                      <TableCell>{u.groupIds.length}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
          {users.data && users.data.total > PAGE && (
            <div className="row" style={{ padding: 10 }}>
              <Text size={200} className="muted">Showing {page * PAGE + 1}–{Math.min((page + 1) * PAGE, users.data.total)} of {users.data.total}</Text>
              <Button size="small" className="right" disabled={page === 0} onClick={() => setPage(page - 1)}>Previous</Button>
              <Button size="small" disabled={(page + 1) * PAGE >= users.data.total} onClick={() => setPage(page + 1)}>Next</Button>
            </div>
          )}
        </section>
      )}

      {tab === 'groups' && (
        <section className="panel flush">
          <LoadState loading={groups.loading && !groups.data} error={groups.error} onRetry={groups.reload} />
          {groups.data && groups.data.length === 0 && <Empty title="No groups found" />}
          {groups.data && groups.data.length > 0 && (
            <div className="table-wrap">
              <Table aria-label="Groups">
                <TableHeader><TableRow><TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Type</TableHeaderCell><TableHeaderCell>Email</TableHeaderCell><TableHeaderCell>Direct members</TableHeaderCell></TableRow></TableHeader>
                <TableBody>
                  {groups.data.map((g) => (
                    <TableRow key={g.id}>
                      <TableCell>{g.displayName}</TableCell>
                      <TableCell><Badge appearance="outline">{g.kind === 'Microsoft365' ? 'Microsoft 365' : g.kind === 'Distribution' ? 'Distribution list' : 'Security'}</Badge></TableCell>
                      <TableCell>{g.mail ?? '—'}</TableCell>
                      <TableCell>{g.memberCount}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </section>
      )}

      <Dialog open={detail != null} onOpenChange={(_, d) => { if (!d.open) setDetail(null); }}>
        <DialogSurface>
          {detail && (
            <DialogBody>
              <DialogTitle>{detail.displayName}</DialogTitle>
              <DialogContent>
                <Table size="small" aria-label="Attributes available as placeholders">
                  <TableBody>
                    {([
                      ['FirstName', detail.firstName], ['LastName', detail.lastName], ['Email', detail.mail ?? detail.upn], ['JobTitle', detail.jobTitle],
                      ['Department', detail.department], ['Company', detail.company], ['Office', detail.office], ['Phone', detail.phone],
                      ['MobilePhone', detail.mobilePhone], ['City', detail.city], ['Country', detail.country],
                      ...Object.entries(detail.customAttributes).map(([k, v]) => [`CustomAttribute${k}`, v] as [string, string]),
                      ['Other addresses', detail.addresses.filter((a) => a !== (detail.mail ?? detail.upn).toLowerCase()).join(', ')],
                    ] as [string, string | null][]).map(([k, v]) => (
                      <TableRow key={k}><TableCell><span className="mono">{k.includes(' ') ? k : `{${k}}`}</span></TableCell><TableCell>{v || <span className="muted">empty</span>}</TableCell></TableRow>
                    ))}
                  </TableBody>
                </Table>
              </DialogContent>
              <DialogActions><Button appearance="primary" onClick={() => setDetail(null)}>Close</Button></DialogActions>
            </DialogBody>
          )}
        </DialogSurface>
      </Dialog>
    </Page>
  );
}
