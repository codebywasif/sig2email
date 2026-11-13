import { useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Badge, Button, Checkbox, Dropdown, Field, Input, MessageBar, MessageBarBody, Option, Radio, RadioGroup, Switch, Text, Textarea,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, PlayRegular } from '@fluentui/react-icons';
import { api, emptyConditions, type AttributeFilter, type FilterOp, type MessageKind, type RecipientScope, type Rule, type TemplateSummary } from '../api';
import { useAuth } from '../auth';
import { GroupPicker, LoadState, Page, TagInput, UserPicker, UserSelect, fromLocalInput, toLocalInput, useAction, useLoad } from '../common';

type Draft = Omit<Rule, 'id' | 'priority'>;

const blank = (): Draft => ({
  name: '', enabled: true, templateId: '', placement: 'AboveQuotedText', channels: 'Both', onMatch: 'Stop', userSelectable: false,
  trackLinks: false, utmSource: null, utmMedium: null, utmCampaign: null, conditions: emptyConditions(),
});

const OPS: { value: FilterOp; label: string; needsValue: boolean }[] = [
  { value: 'Equals', label: 'equals', needsValue: true }, { value: 'NotEquals', label: 'does not equal', needsValue: true },
  { value: 'Contains', label: 'contains', needsValue: true }, { value: 'StartsWith', label: 'starts with', needsValue: true },
  { value: 'IsEmpty', label: 'is empty', needsValue: false }, { value: 'IsNotEmpty', label: 'is not empty', needsValue: false },
];
const SCOPES: { value: RecipientScope; label: string }[] = [
  { value: 'Any', label: 'Any recipients' }, { value: 'InternalOnly', label: 'Internal only — every recipient is in the organisation' },
  { value: 'ExternalAny', label: 'External — at least one recipient is outside' }, { value: 'ExternalOnly', label: 'External only — every recipient is outside' },
];
const KINDS: { value: MessageKind; label: string }[] = [{ value: 'New', label: 'New messages' }, { value: 'Reply', label: 'Replies' }, { value: 'Forward', label: 'Forwards' }];

interface SimResult { matches: { id: string; name: string; onMatch: string }[]; above: string; bottom: string; text: string; skippedUnpublished: string[] }

export function RuleEditor() {
  const { id } = useParams();
  const isNew = id === 'new';
  const navigate = useNavigate();
  const { can, config } = useAuth();
  const canEdit = can('RuleAdmin');
  const templates = useLoad(() => api.get<TemplateSummary[]>('/api/templates'));
  const existing = useLoad(() => (isNew ? Promise.resolve(null) : api.get<Rule>(`/api/rules/${id}`)), [id]);
  const [rule, setRule] = useState<Draft>(blank);
  const { run, busy } = useAction();

  useEffect(() => { if (existing.data) setRule(existing.data); }, [existing.data]);

  const set = (patch: Partial<Draft>) => setRule((r) => ({ ...r, ...patch }));
  const cond = rule.conditions;
  const setSenders = (patch: Partial<Draft['conditions']['senders']>) => set({ conditions: { ...cond, senders: { ...cond.senders, ...patch } } });
  const setRecipients = (patch: Partial<Draft['conditions']['recipients']>) => set({ conditions: { ...cond, recipients: { ...cond.recipients, ...patch } } });
  const setCond = (patch: Partial<Draft['conditions']>) => set({ conditions: { ...cond, ...patch } });
  const setFilter = (i: number, patch: Partial<AttributeFilter>) => setSenders({ filters: cond.senders.filters.map((f, j) => (j === i ? { ...f, ...patch } : f)) });
  const template = templates.data?.find((t) => t.id === rule.templateId);

  const save = () => run(async () => {
    if (isNew) await api.post('/api/rules', rule);
    else await api.put(`/api/rules/${id}`, rule);
    navigate('/rules');
  }, isNew ? 'Rule created' : 'Rule saved');

  // ----- simulator -----
  const [sender, setSender] = useState<string | null>(null);
  const [simTo, setSimTo] = useState<string[]>(['someone@example.org']);
  const [simKind, setSimKind] = useState<MessageKind>('New');
  const [simSubject, setSimSubject] = useState('Hello');
  const [simBody, setSimBody] = useState('');
  const [simChannel, setSimChannel] = useState<'Server' | 'Client'>('Server');
  const [sim, setSim] = useState<SimResult | null>(null);
  const simulate = () => run(async () => setSim(await api.post<SimResult>('/api/rules/simulate', {
    senderId: sender, recipients: simTo, messageType: simKind, subject: simSubject, body: simBody, channel: simChannel,
  })));

  return (
    <Page title={isNew ? 'New rule' : rule.name || 'Rule'} description="Who gets which signature, and when."
      actions={<>
        <Button onClick={() => navigate('/rules')}>{canEdit ? 'Cancel' : 'Back'}</Button>
        {canEdit && <Button appearance="primary" disabled={busy || !rule.name.trim() || !rule.templateId} onClick={() => void save()}>{isNew ? 'Create rule' : 'Save rule'}</Button>}
      </>}>
      <LoadState loading={existing.loading || (templates.loading && !templates.data)} error={existing.error ?? templates.error} />
      {!canEdit && <MessageBar><MessageBarBody>You can view this rule and test it in the simulator, but only rule administrators can change it.</MessageBarBody></MessageBar>}
      {!existing.loading && !existing.error && (
        <div className="grid-2" style={{ alignItems: 'start' }}>
          <fieldset disabled={!canEdit} style={{ border: 0, padding: 0, margin: 0, minWidth: 0 }} className="stack">
            <section className="panel stack">
              <h2>Basics</h2>
              <Field label="Name" required><Input value={rule.name} onChange={(_, d) => set({ name: d.value })} placeholder="e.g. Sales – external signature" /></Field>
              <Field label="Template" required validationState={template && template.publishedVersion == null ? 'warning' : 'none'}
                validationMessage={template && template.publishedVersion == null ? 'This template has not been published yet, so the rule will be skipped until it is.' : undefined}>
                <Dropdown placeholder="Choose a template" value={template?.name ?? ''} selectedOptions={[rule.templateId]} onOptionSelect={(_, d) => set({ templateId: d.optionValue ?? '' })}>
                  {(templates.data ?? []).map((t) => <Option key={t.id} value={t.id} text={t.name}>{t.name}{t.publishedVersion == null && <Text size={200} className="muted"> — draft only</Text>}</Option>)}
                </Dropdown>
              </Field>
              <Switch checked={rule.enabled} onChange={(_, d) => set({ enabled: d.checked })} label="Enabled" />
              <Field label="Where the template is inserted">
                <RadioGroup value={rule.placement} onChange={(_, d) => set({ placement: d.value as Draft['placement'] })}>
                  <Radio value="AboveQuotedText" label="Directly under the new text (above quoted replies) — for signatures" />
                  <Radio value="Bottom" label="At the very bottom of the message — for disclaimers and banners" />
                </RadioGroup>
              </Field>
              <Field label="Applies in">
                <RadioGroup value={rule.channels} onChange={(_, d) => set({ channels: d.value as Draft['channels'] })}>
                  <Radio value="Both" label="Outlook add-in and mail flow (never both on the same message)" />
                  <Radio value="Server" label="Mail flow only — added after sending, on every device" />
                  <Radio value="Client" label="Outlook add-in only — visible while composing" />
                </RadioGroup>
              </Field>
              <Field label="After this rule matches">
                <RadioGroup value={rule.onMatch} onChange={(_, d) => set({ onMatch: d.value as Draft['onMatch'] })}>
                  <Radio value="Stop" label="Stop — do not evaluate the rules below" />
                  <Radio value="Continue" label="Continue — rules below can add more (e.g. signature + disclaimer)" />
                </RadioGroup>
              </Field>
              <Checkbox checked={rule.userSelectable} onChange={(_, d) => set({ userSelectable: !!d.checked })}
                label="Let senders pick this signature manually in the Outlook task pane" />
            </section>

            <section className="panel stack">
              <h2>Senders</h2>
              <Switch checked={cond.senders.everyone} onChange={(_, d) => setSenders({ everyone: d.checked })} label="Everyone in the organisation" />
              {!cond.senders.everyone && (
                <>
                  <Text size={200} className="muted">A sender matches if they are one of these people, a member of one of these groups, or match the attribute filter.</Text>
                  <Field label="People"><UserPicker ariaLabel="Sender people" values={cond.senders.userIds} onChange={(v) => setSenders({ userIds: v })} /></Field>
                  <Field label="Groups" hint="Security groups, distribution lists and Microsoft 365 groups; nested members count.">
                    <GroupPicker ariaLabel="Sender groups" values={cond.senders.groupIds} onChange={(v) => setSenders({ groupIds: v })} />
                  </Field>
                  <Field label="Attribute filter">
                    <div className="stack tight">
                      {cond.senders.filters.map((f, i) => {
                        const op = OPS.find((o) => o.value === f.op)!;
                        return (
                          <div className="row" key={i}>
                            <Dropdown aria-label="Attribute" value={f.attribute} selectedOptions={[f.attribute]} onOptionSelect={(_, d) => setFilter(i, { attribute: d.optionValue ?? '' })} style={{ minWidth: 170 }}>
                              {config.placeholders.map((p) => <Option key={p} value={p}>{p}</Option>)}
                            </Dropdown>
                            <Dropdown aria-label="Comparison" value={op.label} selectedOptions={[f.op]} onOptionSelect={(_, d) => setFilter(i, { op: d.optionValue as FilterOp })} style={{ minWidth: 150 }}>
                              {OPS.map((o) => <Option key={o.value} value={o.value}>{o.label}</Option>)}
                            </Dropdown>
                            {op.needsValue && <Input aria-label="Value" value={f.value ?? ''} onChange={(_, d) => setFilter(i, { value: d.value })} placeholder="Value" />}
                            <Button appearance="subtle" icon={<DeleteRegular />} aria-label="Remove filter" onClick={() => setSenders({ filters: cond.senders.filters.filter((_, j) => j !== i) })} />
                          </div>
                        );
                      })}
                      <div className="row">
                        <Button size="small" icon={<AddRegular />} onClick={() => setSenders({ filters: [...cond.senders.filters, { attribute: 'Department', op: 'Equals', value: '' }] })}>Add condition</Button>
                        {cond.senders.filters.length > 1 && (
                          <RadioGroup layout="horizontal" value={cond.senders.filterMatch} onChange={(_, d) => setSenders({ filterMatch: d.value as 'All' | 'Any' })}>
                            <Radio value="All" label="Match all" /><Radio value="Any" label="Match any" />
                          </RadioGroup>
                        )}
                      </div>
                    </div>
                  </Field>
                </>
              )}
              <Field label="Except these people"><UserPicker ariaLabel="Excluded people" values={cond.senders.excludeUserIds} onChange={(v) => setSenders({ excludeUserIds: v })} /></Field>
              <Field label="Except members of"><GroupPicker ariaLabel="Excluded groups" values={cond.senders.excludeGroupIds} onChange={(v) => setSenders({ excludeGroupIds: v })} /></Field>
            </section>

            <section className="panel stack">
              <h2>Recipients &amp; message</h2>
              <Field label="Recipients">
                <Dropdown value={SCOPES.find((s) => s.value === cond.recipients.scope)!.label} selectedOptions={[cond.recipients.scope]} onOptionSelect={(_, d) => setRecipients({ scope: d.optionValue as RecipientScope })}>
                  {SCOPES.map((s) => <Option key={s.value} value={s.value}>{s.label}</Option>)}
                </Dropdown>
              </Field>
              <Field label="Only when sent to these domains" hint="Leave empty for any. Press Enter after each domain.">
                <TagInput ariaLabel="Recipient domains" values={cond.recipients.domains} onChange={(v) => setRecipients({ domains: v })} placeholder="partner.com" />
              </Field>
              <Field label="Never when sent to" hint="Domains or full addresses.">
                <TagInput ariaLabel="Excluded recipients" values={cond.recipients.exclude} onChange={(v) => setRecipients({ exclude: v })} placeholder="competitor.com, legal@partner.com" />
              </Field>
              <Field label="Message types" hint="None ticked means all types.">
                <div className="row">
                  {KINDS.map((k) => (
                    <Checkbox key={k.value} label={k.label} checked={cond.messageTypes.includes(k.value)}
                      onChange={(_, d) => setCond({ messageTypes: d.checked ? [...cond.messageTypes, k.value] : cond.messageTypes.filter((m) => m !== k.value) })} />
                  ))}
                </div>
              </Field>
              <Field label="Only if the subject contains"><TagInput ariaLabel="Subject keywords" values={cond.subjectKeywords} onChange={(v) => setCond({ subjectKeywords: v })} placeholder="invoice" /></Field>
              <Field label="Only if the body contains" hint="Evaluated in mail flow only — the add-in cannot see the finished body.">
                <TagInput ariaLabel="Body keywords" values={cond.bodyKeywords} onChange={(v) => setCond({ bodyKeywords: v })} />
              </Field>
              <Field label="Skip if subject or body contains" hint="Lets senders opt out per message, e.g. #nosig.">
                <TagInput ariaLabel="Suppress keywords" values={cond.suppressKeywords} onChange={(v) => setCond({ suppressKeywords: v })} placeholder="#nosig" />
              </Field>
              <div className="form-grid">
                <Field label="Active from" hint="Optional campaign start (your local time)">
                  <Input type="datetime-local" value={toLocalInput(cond.startUtc)} onChange={(_, d) => setCond({ startUtc: fromLocalInput(d.value) })} />
                </Field>
                <Field label="Active until" hint="Optional campaign end">
                  <Input type="datetime-local" value={toLocalInput(cond.endUtc)} onChange={(_, d) => setCond({ endUtc: fromLocalInput(d.value) })} />
                </Field>
              </div>
            </section>

            <section className="panel stack">
              <h2>Link tracking</h2>
              <Switch checked={rule.trackLinks} onChange={(_, d) => set({ trackLinks: d.checked })} label="Count clicks on links in this signature" />
              <div className="form-grid">
                <Field label="utm_source"><Input value={rule.utmSource ?? ''} onChange={(_, d) => set({ utmSource: d.value || null })} placeholder="email-signature" /></Field>
                <Field label="utm_medium"><Input value={rule.utmMedium ?? ''} onChange={(_, d) => set({ utmMedium: d.value || null })} placeholder="email" /></Field>
                <Field label="utm_campaign"><Input value={rule.utmCampaign ?? ''} onChange={(_, d) => set({ utmCampaign: d.value || null })} placeholder="spring-launch" /></Field>
              </div>
            </section>
          </fieldset>

          <section className="panel stack" style={{ position: 'sticky', top: 64 }}>
            <h2>Test the saved rule set</h2>
            <Text size={200} className="muted">Runs the live pipeline for a sample message. Unsaved changes on this page are not included.</Text>
            <Field label="From"><UserSelect ariaLabel="Simulated sender" value={sender} onChange={setSender} /></Field>
            <Field label="To"><TagInput ariaLabel="Simulated recipients" values={simTo} onChange={setSimTo} placeholder="name@domain.com" /></Field>
            <div className="form-grid">
              <Field label="Message type">
                <Dropdown value={KINDS.find((k) => k.value === simKind)!.label} selectedOptions={[simKind]} onOptionSelect={(_, d) => setSimKind(d.optionValue as MessageKind)}>
                  {KINDS.map((k) => <Option key={k.value} value={k.value}>{k.label}</Option>)}
                </Dropdown>
              </Field>
              <Field label="Evaluated by">
                <Dropdown value={simChannel === 'Server' ? 'Mail flow' : 'Outlook add-in'} selectedOptions={[simChannel]} onOptionSelect={(_, d) => setSimChannel(d.optionValue as 'Server' | 'Client')}>
                  <Option value="Server">Mail flow</Option><Option value="Client">Outlook add-in</Option>
                </Dropdown>
              </Field>
            </div>
            <Field label="Subject"><Input value={simSubject} onChange={(_, d) => setSimSubject(d.value)} /></Field>
            <Field label="Body text"><Textarea value={simBody} onChange={(_, d) => setSimBody(d.value)} rows={2} /></Field>
            <div><Button icon={<PlayRegular />} disabled={busy || !sender} onClick={() => void simulate()}>Run test</Button></div>
            {sim && (
              <div className="stack" aria-live="polite">
                {sim.matches.length === 0
                  ? <MessageBar intent="info"><MessageBarBody>No rule matches — this message would be sent without a signature.</MessageBarBody></MessageBar>
                  : <div className="row">{sim.matches.map((m, i) => <Badge key={m.id} appearance="tint" color="brand">{i + 1}. {m.name}{m.onMatch === 'Stop' ? ' (stop)' : ''}</Badge>)}</div>}
                {sim.skippedUnpublished.length > 0 && <Text size={200} className="muted">Skipped (template not published): {sim.skippedUnpublished.join(', ')}</Text>}
                {sim.matches.length > 0 && (
                  <iframe className="preview-frame" title="Simulated result" sandbox="" style={{ height: 320 }}
                    srcDoc={`<body style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;margin:12px"><p style="color:#888">[Message text]</p>${sim.above}<p style="color:#888;border-left:2px solid #ccc;padding-left:8px">[Quoted earlier messages]</p>${sim.bottom}</body>`} />
                )}
              </div>
            )}
          </section>
        </div>
      )}
    </Page>
  );
}
