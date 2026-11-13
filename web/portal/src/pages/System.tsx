import { useEffect, useRef, useState } from 'react';
import { Badge, Button, Checkbox, Field, Input, MessageBar, MessageBarBody, SpinButton, Text } from '@fluentui/react-components';
import { ArrowDownloadRegular, ArrowUploadRegular, DeleteRegular } from '@fluentui/react-icons';
import { api, type CertificateInfo, type SystemSettings } from '../api';
import { ConfirmDialog, LoadState, Page, TagInput, download, fmtDate, useAction, useLoad } from '../common';

type Form = Omit<SystemSettings, 'certificate' | 'connected' | 'lastSyncAt' | 'lastSyncError' | 'graphPermissions' | 'entraClientSecretSet' |
  'alertMailPasswordSet' | 'developmentOverrides' | 'addinManifestUrl' | 'addinRedirectUri'> & {
  /** Empty = keep what is stored. */
  entraClientSecret: string; alertMailPassword: string;
};

const toForm = (s: SystemSettings): Form => ({
  publicBaseUrl: s.publicBaseUrl, entraTenantId: s.entraTenantId, entraClientId: s.entraClientId, entraClientSecret: '',
  smtpHostName: s.smtpHostName, smtpRequireTls: s.smtpRequireTls, smtpRequireClientCertificate: s.smtpRequireClientCertificate,
  smtpAllowedClientNames: s.smtpAllowedClientNames, smtpMaxMessageMb: s.smtpMaxMessageMb, relayPort: s.relayPort, relayRequireTls: s.relayRequireTls,
  alertMailHost: s.alertMailHost, alertMailPort: s.alertMailPort, alertMailUser: s.alertMailUser, alertMailPassword: '', alertMailFrom: s.alertMailFrom,
  syncIntervalMinutes: s.syncIntervalMinutes, autoReplyIntervalSeconds: s.autoReplyIntervalSeconds,
});

const num = (value: number | null | undefined, display: string | undefined, fallback: number) => {
  const n = value ?? Number(display);
  return Number.isFinite(n) ? n : fallback;
};

export function System() {
  const settings = useLoad(() => api.get<SystemSettings>('/api/settings'));
  const { run, busy } = useAction();
  const [form, setForm] = useState<Form | null>(null);
  const [dirty, setDirty] = useState(false);
  const [entraTest, setEntraTest] = useState<{ ok: boolean; message: string } | null>(null);
  const [mailTest, setMailTest] = useState<{ ok: boolean; message: string } | null>(null);
  const [testTo, setTestTo] = useState('');
  const [certPassword, setCertPassword] = useState('');
  const [removeCert, setRemoveCert] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const s = settings.data;

  useEffect(() => {
    if (s) { setForm(toForm(s)); setDirty(false); }
  }, [s]);

  const set = <K extends keyof Form>(key: K, value: Form[K]) => {
    setForm((f) => (f ? { ...f, [key]: value } : f));
    setDirty(true);
  };

  const save = () => run(async () => {
    const f = form!;
    // Secrets are only sent when something was typed; null tells the service to keep the stored value.
    settings.setData(await api.put<SystemSettings>('/api/settings', { ...f, entraClientSecret: f.entraClientSecret || null, alertMailPassword: f.alertMailPassword || null }));
    setEntraTest(null);
    setMailTest(null);
  }, 'System settings saved');

  const upload = (file: File | undefined) => {
    if (!file) return;
    void run(async () => {
      const { certificate } = await api.upload<{ certificate: CertificateInfo }>('/api/settings/certificate', file, { password: certPassword });
      settings.setData({ ...s!, certificate });
      setCertPassword('');
    }, 'Certificate installed');
    if (fileInput.current) fileInput.current.value = '';
  };

  const expiresSoon = s?.certificate ? new Date(s.certificate.notAfter).getTime() - Date.now() < 30 * 86400_000 : false;
  const insecure = form && (!form.smtpRequireTls || !form.smtpRequireClientCertificate);

  return (
    <Page title="System" description="Installation-wide settings. They are stored encrypted in the database and take effect within a few seconds, without restarting anything."
      actions={<Button appearance="primary" disabled={busy || !dirty} onClick={() => void save()}>Save changes</Button>}>
      <LoadState loading={settings.loading && !s} error={settings.error} onRetry={settings.reload} />
      {s && form && (
        <>
          {s.developmentOverrides && (
            <MessageBar intent="warning">
              <MessageBarBody>Demo wiring is active: organisations are identified by sender domain and stamped mail is handed to a local test server. This only works in development builds.</MessageBarBody>
            </MessageBar>
          )}

          <section className="panel stack">
            <h2>Public address</h2>
            <Field label="Address of this service" hint="Used in survey links, tracked links, hosted images and the Outlook add-in. Must be reachable by email recipients; use https in production.">
              <Input value={form.publicBaseUrl} onChange={(_, d) => set('publicBaseUrl', d.value)} placeholder="https://signatures.example.com" style={{ maxWidth: 460 }} />
            </Field>
          </section>

          <section className="panel stack">
            <h2>Microsoft Entra app registration</h2>
            <div className="row">
              <Badge color={s.connected ? 'success' : 'warning'} appearance="filled">{s.connected ? 'Connected' : 'Not connected'}</Badge>
              <Text>{s.connected ? `Last directory sync ${fmtDate(s.lastSyncAt)}` : 'Save the three values below, then test the connection.'}</Text>
            </div>
            <Text size={200} className="muted">
              Register an application in your Entra admin center (single tenant), add the Microsoft Graph application permissions {s.graphPermissions},
              grant admin consent and create a client secret. It lets Sig2Email read users and groups, and set automatic replies.
            </Text>
            <div className="form-grid">
              <Field label="Directory (tenant) ID"><Input className="mono" value={form.entraTenantId} onChange={(_, d) => set('entraTenantId', d.value)} placeholder="00000000-0000-0000-0000-000000000000" autoComplete="off" /></Field>
              <Field label="Application (client) ID"><Input className="mono" value={form.entraClientId} onChange={(_, d) => set('entraClientId', d.value)} placeholder="00000000-0000-0000-0000-000000000000" autoComplete="off" /></Field>
              <Field label="Client secret" hint={s.entraClientSecretSet ? 'A secret is stored. Type a new one to replace it.' : 'Not stored yet. It is never shown again after saving.'}>
                <Input type="password" value={form.entraClientSecret} onChange={(_, d) => set('entraClientSecret', d.value)} placeholder={s.entraClientSecretSet ? '••••••••••••' : ''} autoComplete="new-password" />
              </Field>
            </div>
            {s.lastSyncError && <MessageBar intent="warning"><MessageBarBody>Last directory sync failed: {s.lastSyncError}</MessageBarBody></MessageBar>}
            {entraTest && <MessageBar intent={entraTest.ok ? 'success' : 'error'}><MessageBarBody>{entraTest.message}</MessageBarBody></MessageBar>}
            <div className="row">
              <Button disabled={busy || dirty} title={dirty ? 'Save your changes first' : undefined}
                onClick={() => void run(async () => { setEntraTest(await api.post('/api/settings/test-entra')); await settings.reload(); })}>Test connection</Button>
              {dirty && <Text size={200} className="muted">Save your changes before testing.</Text>}
            </div>
          </section>

          <section className="panel stack">
            <h2>Mail routing</h2>
            <Text size={200} className="muted">
              Exchange Online sends outgoing mail to this service, which adds the signature and hands the message straight back. Only needed for server-side and combo mode.
            </Text>
            <div className="form-grid">
              <Field label="Public name of the mail receiver" hint="The smart host in the Exchange connector, e.g. smtp.signatures.example.com. Must match the certificate and resolve to this server (port 25).">
                <Input value={form.smtpHostName} onChange={(_, d) => set('smtpHostName', d.value)} placeholder="smtp.signatures.example.com" />
              </Field>
              <Field label="Largest message accepted (MB)">
                <SpinButton value={form.smtpMaxMessageMb} min={1} max={150} onChange={(_, d) => set('smtpMaxMessageMb', num(d.value, d.displayValue, form.smtpMaxMessageMb))} />
              </Field>
              <Field label="Port for returning mail" hint="25 for Exchange Online">
                <SpinButton value={form.relayPort} min={1} max={65535} onChange={(_, d) => set('relayPort', num(d.value, d.displayValue, form.relayPort))} />
              </Field>
            </div>
            <Checkbox checked={form.smtpRequireTls} onChange={(_, d) => set('smtpRequireTls', d.checked === true)} label="Require encryption (TLS) from the sending server" />
            <Checkbox checked={form.smtpRequireClientCertificate} onChange={(_, d) => set('smtpRequireClientCertificate', d.checked === true)}
              label="Only accept mail from servers that present a trusted certificate for one of these names" />
            {form.smtpRequireClientCertificate && (
              <Field label="Accepted certificate names" hint="Exchange Online presents mail.protection.outlook.com. Subdomains match too.">
                <TagInput ariaLabel="Accepted certificate names" values={form.smtpAllowedClientNames} onChange={(v) => set('smtpAllowedClientNames', v)} placeholder="mail.protection.outlook.com" />
              </Field>
            )}
            <Checkbox checked={form.relayRequireTls} onChange={(_, d) => set('relayRequireTls', d.checked === true)} label="Require encryption (TLS) when returning mail to Exchange Online" />
            {insecure && (
              <MessageBar intent="warning">
                <MessageBarBody>Without both checks, any server that can reach port 25 could send mail through this service in your organisation&apos;s name. Only turn them off behind a firewall that admits Exchange Online alone.</MessageBarBody>
              </MessageBar>
            )}

            <h3>TLS certificate</h3>
            {s.certificate
              ? (
                <>
                  <dl className="kv">
                    <dt>Issued to</dt><dd>{s.certificate.names.join(', ') || s.certificate.subject}</dd>
                    <dt>Issued by</dt><dd>{s.certificate.issuer}{s.certificate.intermediates > 0 && ` (+${s.certificate.intermediates} intermediate)`}</dd>
                    <dt>Valid until</dt><dd>{fmtDate(s.certificate.notAfter)} {expiresSoon && <Badge color="warning" appearance="tint">Expires soon</Badge>}</dd>
                    <dt>Thumbprint</dt><dd className="mono">{s.certificate.thumbprint}</dd>
                  </dl>
                  {form.smtpHostName && s.certificate.names.length > 0 && !s.certificate.names.some((n) => n.toLowerCase() === form.smtpHostName.toLowerCase() ||
                    (n.startsWith('*.') && form.smtpHostName.toLowerCase().endsWith(n.slice(1).toLowerCase()))) && (
                    <MessageBar intent="warning"><MessageBarBody>This certificate is not issued for {form.smtpHostName}; Exchange Online will refuse to connect.</MessageBarBody></MessageBar>
                  )}
                </>
              )
              : <Text className="muted">No certificate installed. {form.smtpRequireTls ? 'Mail is refused until one is uploaded.' : 'Connections are not encrypted.'}</Text>}
            <div className="row top">
              <Field label="Password of the .pfx file" hint="Leave empty if the file has none">
                <Input type="password" value={certPassword} onChange={(_, d) => setCertPassword(d.value)} autoComplete="new-password" />
              </Field>
              <Field label={s.certificate ? 'Replace certificate' : 'Install certificate'} hint="PKCS#12 (.pfx / .p12) with private key, from a public certificate authority">
                <Button icon={<ArrowUploadRegular />} disabled={busy} onClick={() => fileInput.current?.click()}>Choose file…</Button>
              </Field>
              {s.certificate && <Field label=" "><Button appearance="subtle" icon={<DeleteRegular />} disabled={busy} onClick={() => setRemoveCert(true)}>Remove</Button></Field>}
              <input ref={fileInput} type="file" accept=".pfx,.p12,application/x-pkcs12" hidden aria-label="Certificate file" onChange={(e) => upload(e.target.files?.[0])} />
            </div>
          </section>

          <section className="panel stack">
            <h2>Outlook add-in</h2>
            <Text size={200} className="muted">
              For client-side and combo mode. The add-in is served by this service and signs users in with the Entra app above; nothing has to be built.
              In the app registration, expose an API with the Application ID URI <span className="mono">api://{form.entraClientId || '<client id>'}</span> and
              a scope named <span className="mono">access_as_user</span>, and add the single-page-application redirect URI below.
            </Text>
            <dl className="kv">
              <dt>Redirect URI</dt><dd className="mono">{s.addinRedirectUri || '—'}</dd>
              <dt>Manifest</dt><dd className="mono">{s.addinManifestUrl}</dd>
            </dl>
            {!s.publicBaseUrl.startsWith('https://') && <MessageBar intent="warning"><MessageBarBody>Outlook only loads add-ins over https. Set an https public address first.</MessageBarBody></MessageBar>}
            <div>
              <Button icon={<ArrowDownloadRegular />} disabled={busy || dirty}
                onClick={() => void run(async () => download('sig2email-manifest.xml', await api.text('/addin/manifest.xml'), 'application/xml'))}>Download manifest</Button>
            </div>
            <Text size={200} className="muted">Upload the manifest in Microsoft 365 admin center → Settings → Integrated apps → Upload custom apps.</Text>
          </section>

          <section className="panel stack">
            <h2>Alert emails</h2>
            <Text size={200} className="muted">Mail server used to notify people about negative survey ratings. Leave empty to use webhooks only.</Text>
            <div className="form-grid">
              <Field label="SMTP server"><Input value={form.alertMailHost} onChange={(_, d) => set('alertMailHost', d.value)} placeholder="smtp.office365.com" /></Field>
              <Field label="Port"><SpinButton value={form.alertMailPort} min={1} max={65535} onChange={(_, d) => set('alertMailPort', num(d.value, d.displayValue, form.alertMailPort))} /></Field>
              <Field label="Sender address"><Input type="email" value={form.alertMailFrom} onChange={(_, d) => set('alertMailFrom', d.value)} placeholder="alerts@example.com" /></Field>
              <Field label="User name (optional)"><Input value={form.alertMailUser} onChange={(_, d) => set('alertMailUser', d.value)} autoComplete="off" /></Field>
              <Field label="Password" hint={s.alertMailPasswordSet ? 'A password is stored. Type a new one to replace it.' : undefined}>
                <Input type="password" value={form.alertMailPassword} onChange={(_, d) => set('alertMailPassword', d.value)} placeholder={s.alertMailPasswordSet ? '••••••••••••' : ''} autoComplete="new-password" />
              </Field>
            </div>
            {mailTest && <MessageBar intent={mailTest.ok ? 'success' : 'error'}><MessageBarBody>{mailTest.message}</MessageBarBody></MessageBar>}
            <div className="row">
              <Input aria-label="Send a test message to" type="email" value={testTo} onChange={(_, d) => setTestTo(d.value)} placeholder="you@example.com" style={{ minWidth: 240 }} />
              <Button disabled={busy || dirty || !testTo.includes('@')} onClick={() => void run(async () => setMailTest(await api.post('/api/settings/test-alert-mail', { to: testTo })))}>Send test message</Button>
            </div>
          </section>

          <section className="panel stack">
            <h2>Background jobs</h2>
            <div className="form-grid">
              <Field label="Directory sync every (minutes)" hint="Changes in Microsoft 365 reach signatures within this time">
                <SpinButton value={form.syncIntervalMinutes} min={5} max={1440} onChange={(_, d) => set('syncIntervalMinutes', num(d.value, d.displayValue, form.syncIntervalMinutes))} />
              </Field>
              <Field label="Check auto-reply schedules every (seconds)">
                <SpinButton value={form.autoReplyIntervalSeconds} min={30} max={3600} step={30} onChange={(_, d) => set('autoReplyIntervalSeconds', num(d.value, d.displayValue, form.autoReplyIntervalSeconds))} />
              </Field>
            </div>
          </section>

          <div><Button appearance="primary" disabled={busy || !dirty} onClick={() => void save()}>Save changes</Button></div>
        </>
      )}
      <ConfirmDialog open={removeCert} title="Remove the certificate?" confirmLabel="Remove certificate" onClose={() => setRemoveCert(false)}
        body={<>Exchange Online will no longer be able to deliver mail to this service over TLS until a new certificate is installed.</>}
        onConfirm={() => void run(async () => { await api.del('/api/settings/certificate'); await settings.reload(); }, 'Certificate removed')} />
    </Page>
  );
}
