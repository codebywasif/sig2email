import { useState } from 'react';
import {
  Badge, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Table,
  TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, EditRegular } from '@fluentui/react-icons';
import { api, type Account, type Role } from '../api';
import { MIN_PASSWORD_LENGTH, useAuth } from '../auth';
import { ConfirmDialog, LoadState, Page, UserPicker, fmtDate, useAction, useLoad } from '../common';

type RoleChoice = Role | 'Manager';

export const ROLES: { value: RoleChoice; label: string; hint: string }[] = [
  { value: 'Owner', label: 'Owner', hint: 'Everything, including accounts, system settings and mail-flow setup' },
  { value: 'RuleAdmin', label: 'Rule & policy administrator', hint: 'Rules, publishing, surveys and auto-replies' },
  { value: 'Designer', label: 'Template designer', hint: 'Templates and images; cannot change live mail flow' },
  { value: 'AnalyticsViewer', label: 'Analytics viewer', hint: 'Read-only dashboards and audit log' },
  { value: 'Manager', label: 'Manager (no admin role)', hint: 'Only auto-replies for the linked person’s direct reports' },
];
const roleLabel = (role: Role | null) => ROLES.find((r) => r.value === (role ?? 'Manager'))!.label;

interface Draft { id: string | null; username: string; displayName: string; email: string; role: RoleChoice; directoryUserId: string | null; disabled: boolean; password: string }
const blank = (): Draft => ({ id: null, username: '', displayName: '', email: '', role: 'Designer', directoryUserId: null, disabled: false, password: '' });

export function Accounts() {
  const { me, reload: reloadMe } = useAuth();
  const accounts = useLoad(() => api.get<Account[]>('/api/accounts'));
  const { run, busy } = useAction();
  const [draft, setDraft] = useState<Draft | null>(null);
  const [removing, setRemoving] = useState<Account | null>(null);
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft((d) => (d ? { ...d, [key]: value } : d));

  const isNew = draft?.id == null;
  const passwordProblem = draft && draft.password.length > 0 && draft.password.length < MIN_PASSWORD_LENGTH;
  const ready = draft != null && !passwordProblem && (isNew ? draft.username.trim().length >= 3 && draft.password.length >= MIN_PASSWORD_LENGTH : true) &&
    (draft.role !== 'Manager' || draft.directoryUserId != null);

  const save = async () => {
    const d = draft!;
    const body = {
      username: d.username, displayName: d.displayName, email: d.email || null, role: d.role === 'Manager' ? null : d.role,
      directoryUserId: d.directoryUserId, disabled: d.disabled, password: d.password || null,
    };
    const ok = await run(async () => {
      if (d.id) await api.put(`/api/accounts/${d.id}`, body);
      else await api.post('/api/accounts', body);
      await Promise.all([accounts.reload(), reloadMe()]);
    }, d.id ? 'Account updated' : 'Account created');
    if (ok) setDraft(null);
  };

  return (
    <Page title="Accounts" description="People who can sign in to this portal, and what each of them may do."
      actions={<Button appearance="primary" icon={<AddRegular />} onClick={() => setDraft(blank())}>New account</Button>}>
      <LoadState loading={accounts.loading && !accounts.data} error={accounts.error} onRetry={accounts.reload} />
      {accounts.data && (
        <section className="panel flush">
          <div className="table-wrap">
            <Table aria-label="Accounts">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>User name</TableHeaderCell><TableHeaderCell>Role</TableHeaderCell>
                  <TableHeaderCell>Status</TableHeaderCell><TableHeaderCell>Last sign-in</TableHeaderCell><TableHeaderCell style={{ width: 96 }}>Actions</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {accounts.data.map((a) => (
                  <TableRow key={a.id}>
                    <TableCell>{a.displayName}{a.email && <><br /><Text size={200} className="muted">{a.email}</Text></>}</TableCell>
                    <TableCell><span className="mono">{a.username}</span>{a.username === me.user.username && <Text size={200} className="muted"> (you)</Text>}</TableCell>
                    <TableCell>{roleLabel(a.role)}</TableCell>
                    <TableCell>
                      {a.disabled ? <Badge color="danger" appearance="tint">Disabled</Badge>
                        : a.locked ? <Badge color="warning" appearance="tint">Locked</Badge>
                          : a.mustChangePassword ? <Badge color="informative" appearance="tint">Temporary password</Badge>
                            : <Badge color="success" appearance="tint">Active</Badge>}
                    </TableCell>
                    <TableCell>{a.lastLoginAt ? fmtDate(a.lastLoginAt) : 'Never'}</TableCell>
                    <TableCell>
                      <Button appearance="subtle" icon={<EditRegular />} aria-label={`Edit ${a.displayName}`}
                        onClick={() => setDraft({ id: a.id, username: a.username, displayName: a.displayName, email: a.email ?? '', role: a.role ?? 'Manager', directoryUserId: a.directoryUserId, disabled: a.disabled, password: '' })} />
                      <Button appearance="subtle" icon={<DeleteRegular />} aria-label={`Delete ${a.displayName}`} disabled={a.username === me.user.username} onClick={() => setRemoving(a)} />
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
            <form onSubmit={(e) => { e.preventDefault(); if (ready && !busy) void save(); }}>
              <DialogBody>
                <DialogTitle>{isNew ? 'New account' : `Edit ${draft.username}`}</DialogTitle>
                <DialogContent className="stack">
                  {isNew && (
                    <Field label="User name" required hint="3–64 characters: letters, digits, dot, dash, underscore or @. Cannot be changed later.">
                      <Input value={draft.username} onChange={(_, d) => set('username', d.value)} autoComplete="off" />
                    </Field>
                  )}
                  <Field label="Name"><Input value={draft.displayName} onChange={(_, d) => set('displayName', d.value)} autoComplete="off" /></Field>
                  <Field label="Email (optional)"><Input type="email" value={draft.email} onChange={(_, d) => set('email', d.value)} autoComplete="off" /></Field>
                  <Field label="Role" hint={ROLES.find((r) => r.value === draft.role)!.hint}>
                    <Dropdown value={ROLES.find((r) => r.value === draft.role)!.label} selectedOptions={[draft.role]} onOptionSelect={(_, d) => set('role', d.optionValue as RoleChoice)}>
                      {ROLES.map((r) => <Option key={r.value} value={r.value} text={r.label}>{r.label}</Option>)}
                    </Dropdown>
                  </Field>
                  <Field label={draft.role === 'Manager' ? 'Person in the directory' : 'Person in the directory (optional)'} required={draft.role === 'Manager'}
                    hint="Links the account to a mailbox user, so they can also set auto-replies for their direct reports.">
                    <UserPicker ariaLabel="Linked person" values={draft.directoryUserId ? [draft.directoryUserId] : []} onChange={(v) => set('directoryUserId', v.at(-1) ?? null)} />
                  </Field>
                  <Field label={isNew ? 'Temporary password' : 'Reset password'} required={isNew}
                    hint={isNew ? `At least ${MIN_PASSWORD_LENGTH} characters. The person is asked to replace it when they first sign in.` : 'Leave empty to keep the current password. A reset also unlocks the account and signs it out everywhere.'}
                    validationState={passwordProblem ? 'error' : 'none'} validationMessage={passwordProblem ? `At least ${MIN_PASSWORD_LENGTH} characters.` : undefined}>
                    <Input type="password" value={draft.password} onChange={(_, d) => set('password', d.value)} autoComplete="new-password" />
                  </Field>
                  {!isNew && <Checkbox checked={draft.disabled} onChange={(_, d) => set('disabled', d.checked === true)} label="Disabled — cannot sign in; current sessions end immediately" />}
                </DialogContent>
                <DialogActions>
                  <Button appearance="secondary" type="button" onClick={() => setDraft(null)}>Cancel</Button>
                  <Button appearance="primary" type="submit" disabled={busy || !ready}>{isNew ? 'Create account' : 'Save'}</Button>
                </DialogActions>
              </DialogBody>
            </form>
          )}
        </DialogSurface>
      </Dialog>

      <ConfirmDialog open={removing != null} title="Delete account?" confirmLabel="Delete account" onClose={() => setRemoving(null)}
        body={<>{removing?.displayName} ({removing?.username}) will no longer be able to sign in. Their own email signature is not affected.</>}
        onConfirm={() => void run(async () => { await api.del(`/api/accounts/${removing!.id}`); await accounts.reload(); }, 'Account deleted')} />
    </Page>
  );
}
