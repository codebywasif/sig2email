import { useState } from 'react';
import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, MessageBar, MessageBarBody,
  Option, Switch, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Textarea,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, EditRegular } from '@fluentui/react-icons';
import { api, type AutoReplyPolicy } from '../api';
import { ConfirmDialog, Empty, GroupPicker, LoadState, Page, UserPicker, fmtDate, fromLocalInput, toLocalInput, useAction, useLoad } from '../common';

interface Row { policy: AutoReplyPolicy; applied: number; errors: { userId: string; error: string }[] }
interface Targets { admin: boolean; users: { id: string; displayName: string; department: string | null }[]; groups: { id: string; displayName: string }[] }
type Draft = Omit<AutoReplyPolicy, 'id' | 'createdByName' | 'managerDelegated'> & { id?: string };

const AUDIENCE = [{ value: 'All', label: 'Everyone outside the organisation' }, { value: 'ContactsOnly', label: 'Only the person’s contacts' }, { value: 'None', label: 'Nobody outside (internal only)' }] as const;

/** The API stores HTML; people type plain text here. */
const toHtml = (text: string) => text.split(/\n{2,}/).map((p) => `<p>${p.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/\n/g, '<br>')}</p>`).join('');
const toText = (html: string) => html.replace(/<br\s*\/?>/gi, '\n').replace(/<\/p>\s*<p>/gi, '\n\n').replace(/<[^>]+>/g, '').replace(/&lt;/g, '<').replace(/&amp;/g, '&');

function status(p: AutoReplyPolicy): { label: string; color: 'success' | 'informative' | 'subtle' } {
  const now = Date.now();
  if (!p.enabled) return { label: 'Disabled', color: 'subtle' };
  if (now < Date.parse(p.startUtc)) return { label: 'Scheduled', color: 'informative' };
  if (now < Date.parse(p.endUtc)) return { label: 'Active now', color: 'success' };
  return { label: 'Ended', color: 'subtle' };
}

export function AutoReplies() {
  const rows = useLoad(() => api.get<Row[]>('/api/auto-replies'));
  const targets = useLoad(() => api.get<Targets>('/api/auto-replies/targets'));
  const [draft, setDraft] = useState<Draft | null>(null);
  const [deleting, setDeleting] = useState<AutoReplyPolicy | null>(null);
  const { run, busy } = useAction();
  const set = (patch: Partial<Draft>) => setDraft((d) => (d ? { ...d, ...patch } : d));
  const admin = targets.data?.admin ?? false;
  const known = targets.data?.users.map((u) => ({ id: u.id, label: u.displayName, detail: u.department ?? undefined }));

  const blank = (): Draft => {
    const start = new Date(); start.setHours(17, 0, 0, 0);
    const end = new Date(start); end.setDate(end.getDate() + 7); end.setHours(8, 0, 0, 0);
    return {
      name: '', enabled: true, userIds: [], groupIds: [], startUtc: start.toISOString(), endUtc: end.toISOString(),
      internalMessage: toHtml('I am out of the office and will reply when I am back.'),
      externalMessage: toHtml('Thank you for your message. I am currently out of the office and will reply as soon as possible.'),
      externalAudience: 'All', forwardTo: null,
    };
  };

  const valid = draft && draft.name.trim() && draft.userIds.length + draft.groupIds.length > 0 && Date.parse(draft.endUtc) > Date.parse(draft.startUtc);
  const save = () => run(async () => {
    if (draft!.id) await api.put(`/api/auto-replies/${draft!.id}`, draft);
    else await api.post('/api/auto-replies', draft);
    setDraft(null);
    await rows.reload();
  }, 'Auto-reply saved. It is applied to the mailboxes within a minute.');

  return (
    <Page title="Auto-replies" description={admin
      ? 'Schedule out-of-office replies centrally for people, shared mailboxes or whole departments.'
      : 'Set out-of-office replies for the people who report to you.'}
      actions={<Button appearance="primary" icon={<AddRegular />} onClick={() => setDraft(blank())}>New auto-reply</Button>}>
      <LoadState loading={rows.loading && !rows.data} error={rows.error ?? targets.error} onRetry={rows.reload} />
      {rows.data?.length === 0 && <section className="panel"><Empty title="No auto-replies scheduled">Useful for holidays, sick leave when someone cannot set it themselves, or a shared mailbox over a public holiday.</Empty></section>}
      {rows.data && rows.data.length > 0 && (
        <section className="panel flush">
          <div className="table-wrap">
            <Table aria-label="Auto-reply policies">
              <TableHeader><TableRow>
                <TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Period</TableHeaderCell><TableHeaderCell>Applies to</TableHeaderCell>
                <TableHeaderCell>Status</TableHeaderCell><TableHeaderCell style={{ width: 100 }}>Actions</TableHeaderCell>
              </TableRow></TableHeader>
              <TableBody>
                {rows.data.map(({ policy: p, applied, errors }) => {
                  const st = status(p);
                  return (
                    <TableRow key={p.id}>
                      <TableCell><Text weight="semibold">{p.name}</Text><br /><Text size={200} className="muted">By {p.createdByName}{p.managerDelegated ? ' (manager)' : ''}{p.forwardTo ? ` · forwards to ${p.forwardTo}` : ''}</Text></TableCell>
                      <TableCell>{fmtDate(p.startUtc)} →<br />{fmtDate(p.endUtc)}</TableCell>
                      <TableCell>{[p.userIds.length > 0 && `${p.userIds.length} mailbox(es)`, p.groupIds.length > 0 && `${p.groupIds.length} group(s)`].filter(Boolean).join(', ')}</TableCell>
                      <TableCell>
                        <Badge appearance="tint" color={st.color}>{st.label}</Badge>
                        {applied > 0 && <Text size={200} className="muted"> · set on {applied}</Text>}
                        {errors.length > 0 && <><br /><Badge appearance="tint" color="danger" title={errors.map((e) => `${e.userId}: ${e.error}`).join('\n')}>{errors.length} failed</Badge></>}
                      </TableCell>
                      <TableCell>
                        <Button appearance="subtle" icon={<EditRegular />} aria-label={`Edit ${p.name}`} onClick={() => setDraft({ ...p })} />
                        <Button appearance="subtle" icon={<DeleteRegular />} aria-label={`Delete ${p.name}`} onClick={() => setDeleting(p)} />
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
        </section>
      )}

      <Dialog open={draft != null} onOpenChange={(_, d) => { if (!d.open) setDraft(null); }}>
        <DialogSurface style={{ maxWidth: 680 }}>
          {draft && (
            <DialogBody>
              <DialogTitle>{draft.id ? 'Edit auto-reply' : 'New auto-reply'}</DialogTitle>
              <DialogContent className="stack">
                {!admin && <MessageBar><MessageBarBody>As a manager you can set auto-replies for your direct reports.</MessageBarBody></MessageBar>}
                <Field label="Name" required><Input value={draft.name} onChange={(_, d) => set({ name: d.value })} placeholder="e.g. Priya – annual leave" /></Field>
                <Field label="Mailboxes" hint={admin ? 'People or shared mailboxes' : undefined} required={!admin}>
                  <UserPicker ariaLabel="Mailboxes" values={draft.userIds} onChange={(v) => set({ userIds: v })} known={admin ? undefined : known} />
                </Field>
                {admin && <Field label="Groups" hint="Every member gets the auto-reply"><GroupPicker ariaLabel="Groups" values={draft.groupIds} onChange={(v) => set({ groupIds: v })} /></Field>}
                <div className="form-grid">
                  <Field label="From (your local time)"><Input type="datetime-local" value={toLocalInput(draft.startUtc)} onChange={(_, d) => set({ startUtc: fromLocalInput(d.value) ?? draft.startUtc })} /></Field>
                  <Field label="Until" validationState={Date.parse(draft.endUtc) > Date.parse(draft.startUtc) ? 'none' : 'error'}
                    validationMessage={Date.parse(draft.endUtc) > Date.parse(draft.startUtc) ? undefined : 'Must be after the start'}>
                    <Input type="datetime-local" value={toLocalInput(draft.endUtc)} onChange={(_, d) => set({ endUtc: fromLocalInput(d.value) ?? draft.endUtc })} />
                  </Field>
                </div>
                <Field label="Reply to colleagues"><Textarea rows={3} value={toText(draft.internalMessage)} onChange={(_, d) => set({ internalMessage: toHtml(d.value) })} /></Field>
                <Field label="Send replies outside the organisation to">
                  <Dropdown value={AUDIENCE.find((a) => a.value === draft.externalAudience)!.label} selectedOptions={[draft.externalAudience]}
                    onOptionSelect={(_, d) => set({ externalAudience: d.optionValue as Draft['externalAudience'] })}>
                    {AUDIENCE.map((a) => <Option key={a.value} value={a.value}>{a.label}</Option>)}
                  </Dropdown>
                </Field>
                {draft.externalAudience !== 'None' && (
                  <Field label="Reply to external senders"><Textarea rows={3} value={toText(draft.externalMessage)} onChange={(_, d) => set({ externalMessage: toHtml(d.value) })} /></Field>
                )}
                <Field label="Forward incoming mail to (optional)" hint="Only during the period above. Removed automatically afterwards.">
                  <Input type="email" value={draft.forwardTo ?? ''} onChange={(_, d) => set({ forwardTo: d.value || null })} placeholder="colleague@contoso.com" />
                </Field>
                <Switch checked={draft.enabled} onChange={(_, d) => set({ enabled: d.checked })} label="Enabled" />
              </DialogContent>
              <DialogActions>
                <Button onClick={() => setDraft(null)}>Cancel</Button>
                <Button appearance="primary" disabled={busy || !valid} onClick={() => void save()}>Save auto-reply</Button>
              </DialogActions>
            </DialogBody>
          )}
        </DialogSurface>
      </Dialog>
      <ConfirmDialog open={deleting != null} title="Delete auto-reply?" confirmLabel="Delete auto-reply" onClose={() => setDeleting(null)}
        body={<>“{deleting?.name}” will be removed and automatic replies switched off on the affected mailboxes.</>}
        onConfirm={() => void run(async () => { await api.del(`/api/auto-replies/${deleting!.id}`); await rows.reload(); }, 'Auto-reply deleted')} />
    </Page>
  );
}
