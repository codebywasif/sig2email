import { useState } from 'react';
import {
  Badge, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option,
  SpinButton, Switch, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text,
} from '@fluentui/react-components';
import { AddRegular, CopyRegular, DeleteRegular, EditRegular } from '@fluentui/react-icons';
import { api, type Survey, type SurveyStyle } from '../api';
import { ConfirmDialog, Empty, LoadState, Page, TagInput, useAction, useLoad, useNotify } from '../common';

type Draft = Omit<Survey, 'id' | 'maxScore'> & { id?: string };

const STYLES: { value: SurveyStyle; label: string; sample: string }[] = [
  { value: 'Emoji', label: 'Emoticons (5)', sample: '😡 🙁 😐 🙂 😍' },
  { value: 'Stars', label: 'Stars (5)', sample: '★ ★ ★ ★ ★' },
  { value: 'Thumbs', label: 'Thumbs (2)', sample: '👎 👍' },
  { value: 'Custom', label: 'Custom icons', sample: 'Your own images' },
];
const maxOf = (d: Draft) => (d.style === 'Thumbs' ? 2 : d.style === 'Custom' ? Math.max(2, d.customIcons.length) : 5);

const blank = (): Draft => ({
  name: '', question: 'How did I do today?', style: 'Emoji', customIcons: [], thankYouMessage: 'Thank you for your feedback!', askComment: true,
  negativeThreshold: 2, alertEmails: [], webhookUrl: null, enabled: true,
});

export function Surveys() {
  const surveys = useLoad(() => api.get<Survey[]>('/api/surveys'));
  const [draft, setDraft] = useState<Draft | null>(null);
  const [deleting, setDeleting] = useState<Survey | null>(null);
  const { run, busy } = useAction();
  const notify = useNotify();
  const set = (patch: Partial<Draft>) => setDraft((d) => (d ? { ...d, ...patch } : d));

  const save = () => run(async () => {
    const body = { ...draft!, negativeThreshold: Math.min(draft!.negativeThreshold, maxOf(draft!)) };
    if (draft!.id) await api.put(`/api/surveys/${draft!.id}`, body);
    else await api.post('/api/surveys', body);
    setDraft(null);
    await surveys.reload();
  }, 'Survey saved');

  return (
    <Page title="Surveys" description="One-click satisfaction ratings that recipients answer straight from your signature — no login."
      actions={<Button appearance="primary" icon={<AddRegular />} onClick={() => setDraft(blank())}>New survey</Button>}>
      <LoadState loading={surveys.loading && !surveys.data} error={surveys.error} onRetry={surveys.reload} />
      {surveys.data?.length === 0 && <section className="panel"><Empty title="No surveys yet">Create a survey, then add it to a template with the Survey block.</Empty></section>}
      {surveys.data && surveys.data.length > 0 && (
        <section className="panel flush">
          <div className="table-wrap">
            <Table aria-label="Surveys">
              <TableHeader><TableRow>
                <TableHeaderCell>Survey</TableHeaderCell><TableHeaderCell>Rating style</TableHeaderCell><TableHeaderCell>Negative alerts</TableHeaderCell>
                <TableHeaderCell>Status</TableHeaderCell><TableHeaderCell style={{ width: 150 }}>Actions</TableHeaderCell>
              </TableRow></TableHeader>
              <TableBody>
                {surveys.data.map((s) => (
                  <TableRow key={s.id}>
                    <TableCell><Text weight="semibold">{s.name}</Text><br /><Text size={200} className="muted">{s.question}</Text></TableCell>
                    <TableCell>{STYLES.find((x) => x.value === s.style)!.sample}</TableCell>
                    <TableCell>
                      {s.alertEmails.length === 0 && !s.webhookUrl ? <span className="muted">Off</span>
                        : <>Rating ≤ {s.negativeThreshold} → {[s.alertEmails.length > 0 && `${s.alertEmails.length} email(s)`, s.webhookUrl && 'webhook'].filter(Boolean).join(' + ')}</>}
                    </TableCell>
                    <TableCell><Badge appearance="tint" color={s.enabled ? 'success' : 'subtle'}>{s.enabled ? 'Active' : 'Paused'}</Badge></TableCell>
                    <TableCell>
                      <Button appearance="subtle" icon={<EditRegular />} aria-label={`Edit ${s.name}`} onClick={() => setDraft({ ...s })} />
                      <Button appearance="subtle" icon={<CopyRegular />} aria-label={`Copy template token for ${s.name}`}
                        onClick={() => void navigator.clipboard.writeText(`{Csat:${s.id}}`).then(() => notify('Token copied — paste it into a template’s HTML'))} />
                      <Button appearance="subtle" icon={<DeleteRegular />} aria-label={`Delete ${s.name}`} onClick={() => setDeleting(s)} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </section>
      )}

      <Dialog open={draft != null} onOpenChange={(_, d) => { if (!d.open) setDraft(null); }}>
        <DialogSurface>
          {draft && (
            <DialogBody>
              <DialogTitle>{draft.id ? 'Edit survey' : 'New survey'}</DialogTitle>
              <DialogContent className="stack">
                <Field label="Name" required hint="Internal name, shown in analytics"><Input value={draft.name} onChange={(_, d) => set({ name: d.value })} /></Field>
                <Field label="Question shown in the signature"><Input value={draft.question} onChange={(_, d) => set({ question: d.value })} /></Field>
                <Field label="Rating style">
                  <Dropdown value={STYLES.find((s) => s.value === draft.style)!.label} selectedOptions={[draft.style]} onOptionSelect={(_, d) => set({ style: d.optionValue as SurveyStyle })}>
                    {STYLES.map((s) => <Option key={s.value} value={s.value} text={s.label}>{s.label} — {s.sample}</Option>)}
                  </Dropdown>
                </Field>
                {draft.style === 'Custom' && (
                  <Field label="Icons, lowest rating first" hint="2–10 icons. Use image URLs from the template designer’s image library.">
                    <div className="stack tight">
                      {draft.customIcons.map((icon, i) => (
                        <div className="row" key={i}>
                          <Input aria-label={`Icon ${i + 1} image URL`} className="grow" placeholder="https://…/icon.png" value={icon.imageUrl}
                            onChange={(_, d) => set({ customIcons: draft.customIcons.map((x, j) => (j === i ? { ...x, imageUrl: d.value } : x)) })} />
                          <Input aria-label={`Icon ${i + 1} label`} placeholder="Label" value={icon.label} style={{ width: 130 }}
                            onChange={(_, d) => set({ customIcons: draft.customIcons.map((x, j) => (j === i ? { ...x, label: d.value } : x)) })} />
                          <Button appearance="subtle" icon={<DeleteRegular />} aria-label={`Remove icon ${i + 1}`} onClick={() => set({ customIcons: draft.customIcons.filter((_, j) => j !== i) })} />
                        </div>
                      ))}
                      <div><Button size="small" icon={<AddRegular />} disabled={draft.customIcons.length >= 10} onClick={() => set({ customIcons: [...draft.customIcons, { imageUrl: '', label: '' }] })}>Add icon</Button></div>
                    </div>
                  </Field>
                )}
                <Field label="Landing page heading"><Input value={draft.thankYouMessage} onChange={(_, d) => set({ thankYouMessage: d.value })} /></Field>
                <Checkbox checked={draft.askComment} onChange={(_, d) => set({ askComment: !!d.checked })} label="Ask for an optional comment" />
                <Field label={`Alert when the rating is at or below (of ${maxOf(draft)})`}>
                  <SpinButton min={0} max={maxOf(draft)} value={Math.min(draft.negativeThreshold, maxOf(draft))} onChange={(_, d) => set({ negativeThreshold: d.value ?? Number(d.displayValue) ?? 0 })} />
                </Field>
                <Field label="Send alert emails to"><TagInput ariaLabel="Alert email addresses" values={draft.alertEmails} onChange={(v) => set({ alertEmails: v })} placeholder="support-lead@contoso.com" /></Field>
                <Field label="Send alerts to a webhook" hint="An https:// URL that receives a JSON POST (Teams, Slack, ticketing…)">
                  <Input value={draft.webhookUrl ?? ''} onChange={(_, d) => set({ webhookUrl: d.value || null })} placeholder="https://…" />
                </Field>
                <Switch checked={draft.enabled} onChange={(_, d) => set({ enabled: d.checked })} label="Active — shown in signatures" />
              </DialogContent>
              <DialogActions>
                <Button onClick={() => setDraft(null)}>Cancel</Button>
                <Button appearance="primary" disabled={busy || !draft.name.trim()} onClick={() => void save()}>Save survey</Button>
              </DialogActions>
            </DialogBody>
          )}
        </DialogSurface>
      </Dialog>
      <ConfirmDialog open={deleting != null} title="Delete survey?" confirmLabel="Delete survey" onClose={() => setDeleting(null)}
        body={<>“{deleting?.name}” will be deleted. Ratings already collected stay in analytics; links in emails already sent stop working.</>}
        onConfirm={() => void run(async () => { await api.del(`/api/surveys/${deleting!.id}`); await surveys.reload(); }, 'Survey deleted')} />
    </Page>
  );
}
