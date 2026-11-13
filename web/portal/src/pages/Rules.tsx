import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Badge, Button, MessageBar, MessageBarBody, Switch, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text } from '@fluentui/react-components';
import { AddRegular, ArrowDownRegular, ArrowUpRegular, DeleteRegular, ReOrderDotsVerticalRegular } from '@fluentui/react-icons';
import { api, type Rule, type TemplateSummary } from '../api';
import { useAuth } from '../auth';
import { ConfirmDialog, Empty, LoadState, Page, useAction, useLoad } from '../common';

export function describe(rule: Rule): string {
  const c = rule.conditions;
  const parts: string[] = [];
  const s = c.senders;
  if (s.everyone) parts.push('Everyone');
  else {
    const who = [s.userIds.length && `${s.userIds.length} people`, s.groupIds.length && `${s.groupIds.length} group(s)`,
      s.filters.length && s.filters.map((f) => `${f.attribute} ${f.op === 'Equals' ? '=' : f.op.toLowerCase()} ${f.value ?? ''}`.trim()).join(s.filterMatch === 'All' ? ' and ' : ' or ')].filter(Boolean);
    parts.push(who.length ? who.join(', ') : 'Nobody');
  }
  if (c.recipients.scope !== 'Any') parts.push({ InternalOnly: 'internal mail only', ExternalAny: 'any external recipient', ExternalOnly: 'external recipients only' }[c.recipients.scope]);
  if (c.recipients.domains.length) parts.push(`to ${c.recipients.domains.join(', ')}`);
  if (c.messageTypes.length) parts.push(c.messageTypes.map((m) => ({ New: 'new messages', Reply: 'replies', Forward: 'forwards' }[m])).join(' & '));
  if (c.subjectKeywords.length || c.bodyKeywords.length) parts.push('keyword match');
  if (c.startUtc || c.endUtc) parts.push('scheduled');
  return parts.join(' · ');
}

export function Rules() {
  const { can } = useAuth();
  const canEdit = can('RuleAdmin');
  const navigate = useNavigate();
  const rules = useLoad(() => api.get<Rule[]>('/api/rules'));
  const templates = useLoad(() => api.get<TemplateSummary[]>('/api/templates'));
  const { run, busy } = useAction();
  const [dragId, setDragId] = useState<string | null>(null);
  const [overId, setOverId] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<Rule | null>(null);
  const list = rules.data ?? [];
  const tpl = (id: string) => templates.data?.find((t) => t.id === id);

  const reorder = (ids: string[]) => {
    rules.setData(ids.map((id, i) => ({ ...list.find((r) => r.id === id)!, priority: i + 1 })));
    void run(async () => { await api.put('/api/rules/order', ids); }).then((ok) => { if (!ok) void rules.reload(); });
  };
  const move = (id: string, to: number) => {
    const ids = list.map((r) => r.id).filter((x) => x !== id);
    ids.splice(Math.max(0, Math.min(ids.length, to)), 0, id);
    reorder(ids);
  };

  return (
    <Page title="Rules" description="Rules are evaluated top to bottom for every message. Each match adds its template; “Stop” ends the evaluation."
      actions={canEdit && <Button appearance="primary" icon={<AddRegular />} onClick={() => navigate('/rules/new')}>New rule</Button>}>
      <LoadState loading={rules.loading && !rules.data} error={rules.error} onRetry={rules.reload} />
      {rules.data && list.length === 0 && (
        <section className="panel"><Empty title="No rules yet">A rule decides who gets which signature. {canEdit ? 'Create one to start stamping mail.' : ''}</Empty></section>
      )}
      {list.some((r) => r.enabled && tpl(r.templateId)?.publishedVersion == null) && templates.data && (
        <MessageBar intent="warning"><MessageBarBody>Some enabled rules use a template that has never been published, so they are skipped. Publish the template to make them live.</MessageBarBody></MessageBar>
      )}
      {list.length > 0 && (
        <section className="panel flush">
          <div className="table-wrap">
            <Table aria-label="Rules in evaluation order">
              <TableHeader><TableRow>
                <TableHeaderCell style={{ width: canEdit ? 108 : 48 }}>Order</TableHeaderCell><TableHeaderCell>Rule</TableHeaderCell><TableHeaderCell>Template</TableHeaderCell>
                <TableHeaderCell>Applies in</TableHeaderCell><TableHeaderCell>After match</TableHeaderCell><TableHeaderCell style={{ width: 90 }}>Enabled</TableHeaderCell>
                {canEdit && <TableHeaderCell style={{ width: 56 }}>Delete</TableHeaderCell>}
              </TableRow></TableHeader>
              <TableBody>
                {list.map((r, i) => (
                  <TableRow key={r.id} className={overId === r.id && dragId !== r.id ? 'drag-over' : undefined}
                    draggable={canEdit} onDragStart={(e) => { setDragId(r.id); e.dataTransfer.effectAllowed = 'move'; }}
                    onDragOver={(e) => { if (dragId) { e.preventDefault(); setOverId(r.id); } }}
                    onDragEnd={() => { setDragId(null); setOverId(null); }}
                    onDrop={(e) => { e.preventDefault(); if (dragId && dragId !== r.id) move(dragId, list.filter((x) => x.id !== dragId).findIndex((x) => x.id === r.id)); setDragId(null); setOverId(null); }}>
                    <TableCell>
                      <span className="row" style={{ gap: 2, flexWrap: 'nowrap' }}>
                        {canEdit && <ReOrderDotsVerticalRegular className="drag-handle" aria-hidden="true" />}
                        <Text weight="semibold" style={{ minWidth: 18 }}>{i + 1}</Text>
                        {canEdit && <>
                          <Button size="small" appearance="subtle" icon={<ArrowUpRegular />} aria-label={`Move ${r.name} up`} disabled={i === 0 || busy} onClick={() => move(r.id, i - 1)} />
                          <Button size="small" appearance="subtle" icon={<ArrowDownRegular />} aria-label={`Move ${r.name} down`} disabled={i === list.length - 1 || busy} onClick={() => move(r.id, i + 1)} />
                        </>}
                      </span>
                    </TableCell>
                    <TableCell>
                      <Button appearance="transparent" className="link-button" onClick={() => navigate(`/rules/${r.id}`)}>{r.name}</Button>
                      <br /><Text size={200} className="muted">{describe(r)}</Text>
                    </TableCell>
                    <TableCell>{tpl(r.templateId)?.name ?? '—'}{tpl(r.templateId) && tpl(r.templateId)!.publishedVersion == null && <> <Badge color="warning" appearance="tint">not published</Badge></>}</TableCell>
                    <TableCell>{{ Both: 'Outlook add-in + mail flow', Server: 'Mail flow only', Client: 'Outlook add-in only' }[r.channels]}</TableCell>
                    <TableCell><Badge appearance="outline" color={r.onMatch === 'Stop' ? 'important' : 'informative'}>{r.onMatch === 'Stop' ? 'Stop' : 'Continue'}</Badge></TableCell>
                    <TableCell>
                      <Switch checked={r.enabled} disabled={!canEdit || busy} aria-label={`${r.name} enabled`}
                        onChange={(_, d) => void run(async () => { await api.put(`/api/rules/${r.id}`, { ...r, enabled: d.checked }); await rules.reload(); }, d.checked ? 'Rule enabled' : 'Rule disabled')} />
                    </TableCell>
                    {canEdit && <TableCell><Button appearance="subtle" icon={<DeleteRegular />} aria-label={`Delete ${r.name}`} onClick={() => setDeleting(r)} /></TableCell>}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </section>
      )}
      <ConfirmDialog open={deleting != null} title="Delete rule?" confirmLabel="Delete rule" onClose={() => setDeleting(null)}
        body={<>“{deleting?.name}” will stop applying to new mail immediately. The template it uses is kept.</>}
        onConfirm={() => void run(async () => { await api.del(`/api/rules/${deleting!.id}`); await rules.reload(); }, 'Rule deleted')} />
    </Page>
  );
}
