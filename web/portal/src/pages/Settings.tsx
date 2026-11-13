import { useEffect, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Badge, Button, Dropdown, Field, Input, Link, MessageBar, MessageBarBody, Option, Radio, RadioGroup, Text } from '@fluentui/react-components';
import { ArrowDownloadRegular } from '@fluentui/react-icons';
import { api, type DeploymentMode, type DirectoryGroup, type TenantInfo } from '../api';
import { useAuth } from '../auth';
import { LoadState, Page, download, fmtDate, useAction, useLoad } from '../common';

export function Settings() {
  const { reload: reloadMe } = useAuth();
  const tenant = useLoad(() => api.get<TenantInfo>('/api/tenant'));
  const groups = useLoad(() => api.get<DirectoryGroup[]>('/api/directory/groups'));
  const { run, busy } = useAction();
  const [name, setName] = useState('');
  const [mode, setMode] = useState<DeploymentMode>('ServerSide');
  const [relay, setRelay] = useState('');
  const [scopeGroup, setScopeGroup] = useState('');
  const [script, setScript] = useState<string | null>(null);
  const t = tenant.data;

  useEffect(() => {
    if (t) { setName(t.name); setMode(t.mode); setRelay(t.relayHost ?? ''); }
  }, [t]);

  const mailGroups = (groups.data ?? []).filter((g) => g.mail);
  const scriptUrl = `/api/tenant/provisioning-script?groupId=${encodeURIComponent(scopeGroup)}`;

  return (
    <Page title="Organisation" description="Organisation name, how signatures are added, and the Exchange Online mail-flow setup.">
      <LoadState loading={tenant.loading && !t} error={tenant.error} onRetry={tenant.reload} />
      {t && (
        <>
          <section className="panel stack">
            <h2>Organisation &amp; deployment mode</h2>
            <div className="form-grid">
              <Field label="Organisation name"><Input value={name} onChange={(_, d) => setName(d.value)} /></Field>
              <Field label="Accepted domains" hint="From Microsoft 365; used to tell internal from external recipients"><Input value={t.domains.join(', ') || 'Not synchronised yet'} readOnly /></Field>
            </div>
            <Field label="How signatures are added">
              <RadioGroup value={mode} onChange={(_, d) => setMode(d.value as DeploymentMode)}>
                <Radio value="ServerSide" label="Server-side — stamped in transit by Exchange mail flow. Works on every device, including phones." />
                <Radio value="ClientSide" label="Client-side — inserted by the Outlook add-in while composing. Users see their signature; no mail routing." />
                <Radio value="Combo" label="Combo — add-in where Outlook supports it, mail flow as the fallback, never both on the same message." />
              </RadioGroup>
            </Field>
            {mode !== 'ClientSide' && (
              <Field label="Exchange Online endpoint for returning mail" hint="Your MX endpoint, e.g. contoso-com.mail.protection.outlook.com">
                <Input value={relay} onChange={(_, d) => setRelay(d.value)} placeholder="contoso-com.mail.protection.outlook.com" style={{ maxWidth: 460 }} />
              </Field>
            )}
            <div>
              <Button appearance="primary" disabled={busy || !name.trim()}
                onClick={() => void run(async () => { await api.put('/api/tenant', { name, mode, relayHost: relay || null }); await Promise.all([tenant.reload(), reloadMe()]); }, 'Settings saved')}>
                Save
              </Button>
            </div>
          </section>

          <section className="panel stack">
            <h2>Microsoft 365 connection</h2>
            <div className="row">
              <Badge color={t.connected ? 'success' : 'warning'} appearance="filled">{t.connected ? 'Connected' : 'Not connected'}</Badge>
              <Text>{t.connected ? `Last directory sync ${fmtDate(t.lastSyncAt)}` : 'Users and groups cannot be read until the Microsoft Entra app registration is entered and tested.'}</Text>
            </div>
            {t.lastSyncError && <MessageBar intent="warning"><MessageBarBody>Last directory sync failed: {t.lastSyncError}</MessageBarBody></MessageBar>}
            <div className="row">
              <RouterLink to="/system"><Link as="span">{t.connected ? 'Change the connection under System' : 'Set up the connection under System'}</Link></RouterLink>
              {t.connected && <Button size="small" disabled={busy || t.syncRequested} onClick={() => void run(async () => { await api.post('/api/tenant/sync'); await tenant.reload(); }, 'Directory sync queued')}>{t.syncRequested ? 'Sync queued…' : 'Sync directory now'}</Button>}
            </div>
          </section>

          {mode !== 'ClientSide' && (
            <section className="panel stack">
              <h2>Exchange Online mail flow</h2>
              {t.smtpHost
                ? (
                  <Text>
                    This PowerShell script creates the outbound and inbound connectors and the transport rule that route outgoing mail through
                    {' '}<span className="mono">{t.smtpHost}</span>. Run it once as an Exchange Administrator.
                    The rule fails open, and disabling it bypasses Sig2Email immediately.
                  </Text>
                )
                : (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      The setup script needs the public name of this service&apos;s mail receiver. Enter it under{' '}
                      <RouterLink to="/system"><Link as="span">System → Mail routing</Link></RouterLink> first.
                    </MessageBarBody>
                  </MessageBar>
                )}
              <div className="row">
                <Field label="Apply to">
                  <Dropdown value={scopeGroup ? mailGroups.find((g) => g.id === scopeGroup)?.displayName ?? '' : 'Everyone in the organisation'}
                    selectedOptions={[scopeGroup]} onOptionSelect={(_, d) => { setScopeGroup(d.optionValue ?? ''); setScript(null); }} style={{ minWidth: 280 }}>
                    <Option value="">Everyone in the organisation</Option>
                    {mailGroups.map((g) => <Option key={g.id} value={g.id}>{g.displayName}</Option>)}
                  </Dropdown>
                </Field>
              </div>
              <div className="row">
                <Button disabled={busy || !t.smtpHost} onClick={() => void run(async () => setScript(await api.text(scriptUrl)))}>Show script</Button>
                <Button icon={<ArrowDownloadRegular />} disabled={busy || !t.smtpHost}
                  onClick={() => void run(async () => download('sig2email-mailflow.ps1', await api.text(scriptUrl)))}>Download setup script</Button>
                <Button appearance="subtle" icon={<ArrowDownloadRegular />} disabled={busy}
                  onClick={() => void run(async () => download('sig2email-remove.ps1', await api.text('/api/tenant/provisioning-script?remove=true')))}>Download removal script</Button>
              </div>
              {script && <pre className="script" tabIndex={0}>{script}</pre>}
            </section>
          )}
        </>
      )}
    </Page>
  );
}
